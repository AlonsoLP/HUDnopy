// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        HUDnopy.Tests/AdvancedIntegrationTests.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Real ClientWebSocket loopback integration tests, deterministic
//              full-session Journal replay, mocked HTTP integration tests
//              (Spansh, EDSM, Ardent), and ConfigService resilience tests.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HUDnopy.Models;
using HUDnopy.Services;
using HUDnopy.Services.Integrations;

namespace HUDnopy.Tests;

internal static partial class Program
{
    private sealed class InstantTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return NullTimer.Instance;
        }

        private sealed class NullTimer : ITimer
        {
            public static readonly NullTimer Instance = new();
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private static async Task<string> ReceiveFullWebSocketTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16384];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                break;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task TestWebSocketIntegrationAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hudnopy_ws_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        int port;
        using (var tcp = new TcpListener(IPAddress.Loopback, 0))
        {
            tcp.Start();
            port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        }

        var cfgSvc = new ConfigService();
        cfgSvc.Current.JournalFolderPath = tempDir;
        cfgSvc.Current.CaptureUnhandledEvents = false;
        cfgSvc.Current.WebAccessMode = "local";
        cfgSvc.Current.WebServerPort = port;
        cfgSvc.Current.EnableSpanshSystemData = false;
        cfgSvc.Current.EnableEdsmSync = false;
        cfgSvc.Current.EnableInaraSync = false;
        cfgSvc.Current.EnableEddnUpload = false;

        var bindings = new BindingsService(cfgSvc);
        var inputSim = new InputSimulatorService(bindings, cfgSvc);
        var hub = new GameEventHub();
        using var watcher = new EliteFileWatcherService(cfgSvc, inputSim, hub);
        var alerts = new AlertService(cfgSvc, watcher, hub);
        using var piper = new PiperTtsService();
        var speech = new AlertSpeechService(alerts, cfgSvc, piper);
        using var spansh = new SpanshSystemDataService(cfgSvc, watcher);
        using var commSync = new CommunitySyncService(cfgSvc, watcher, hub);
        using var galnet = new GalnetService();
        using var galaxy = new GalaxyStatusService();
        using var copilot = new AiCopilotService(cfgSvc, watcher, bindings, inputSim, spansh, commSync, galnet, galaxy, speech, hub);
        using var webServer = new WebServerService(cfgSvc, watcher, bindings, inputSim, alerts, spansh, copilot, galaxy, galnet);

        try
        {
            await webServer.StartAsync();
            string baseUrl = webServer.GetAccessUrls().FirstOrDefault()?.TrimEnd('/') ?? $"https://127.0.0.1:{port}";
            var wsUri = new UriBuilder(baseUrl)
            {
                Scheme = baseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
                Path = "/ws"
            }.Uri;

            // 0. Verify WebSocket rejects cross-origin / missing-origin CSRF attempts with 403
            using (var badWs = new ClientWebSocket())
            {
                badWs.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                badWs.Options.SetRequestHeader("Origin", "https://evil.example");
                using var badCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                bool rejected403 = false;
                try { await badWs.ConnectAsync(wsUri, badCts.Token); }
                catch (WebSocketException ex) when (ex.Message.Contains("403", StringComparison.Ordinal)) { rejected403 = true; }
                Assert(rejected403, "Expected WebSocket /ws to reject mismatched Origin with HTTP 403.");
            }

            using var ws = new ClientWebSocket();
            ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            ws.Options.SetRequestHeader("Origin", baseUrl);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(wsUri, cts.Token);
            Assert(ws.State == WebSocketState.Open, $"Expected WebSocket Open, got {ws.State}.");

            // 1. Verify initial state snapshot contract
            string initialJson = await ReceiveFullWebSocketTextAsync(ws, cts.Token);
            using var doc = JsonDocument.Parse(initialJson);
            string[] requiredKeys =
            [
                "state", "appVersion", "enabledIntegrations", "operationsView",
                "flightTranslations", "dashboardTranslations", "inputTranslations",
                "galnetArticles", "engineeringWishlist"
            ];
            foreach (string key in requiredKeys)
                Assert(doc.RootElement.TryGetProperty(key, out _), $"WebSocket initial snapshot missing required key '{key}'.");

            // 2. Trigger live state update and verify broadcast frame arrives
            File.WriteAllText(Path.Combine(tempDir, "Journal.2026-11-22T010000.01.log"),
                """{"timestamp":"2026-11-22T01:00:00Z","event":"Location","StarSystem":"Colonia","StarPos":[-9530.5,-910.28,19808.12],"Docked":false}""" + Environment.NewLine);
            watcher.RefreshStateForTest();

            string updateJson = await ReceiveFullWebSocketTextAsync(ws, cts.Token);
            Assert(updateJson.Contains("Colonia", StringComparison.Ordinal),
                "Expected WebSocket live broadcast to reflect updated StarSystem 'Colonia'.");

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", cts.Token);
            await webServer.StopAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void TestFullSessionJournalReplay()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hudnopy_replay_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string journalPath = Path.Combine(tempDir, "Journal.2026-11-22T010000.01.log");
            File.WriteAllLines(journalPath,
            [
                """{"timestamp":"2026-11-22T01:00:00Z","event":"LoadGame","Commander":"ReplayCmdr","Ship":"Mandalay","ShipName":"Starlight","ShipIdent":"STR-01","FuelLevel":32.0,"FuelCapacity":32.0,"Credits":50000000}""",
                """{"timestamp":"2026-11-22T01:00:05Z","event":"Location","StarSystem":"Sol","StarPos":[0,0,0],"Docked":true,"StationName":"Galileo"}""",
                """{"timestamp":"2026-11-22T01:01:00Z","event":"Undocked","StationName":"Galileo"}""",
                """{"timestamp":"2026-11-22T01:02:00Z","event":"FSDJump","StarSystem":"Alpha Centauri","StarPos":[3.03,-0.09,3.15],"JumpDist":4.37}""",
                """{"timestamp":"2026-11-22T01:03:00Z","event":"Scan","BodyName":"Alpha Centauri 3","PlanetClass":"Earthlike body","DistanceFromArrivalLS":520.0,"WasDiscovered":false,"WasMapped":false}""",
                """{"timestamp":"2026-11-22T01:04:00Z","event":"SAASignalsFound","BodyName":"Alpha Centauri 3","Signals":[{"Type":"$SAA_SignalType_Biological;","Count":3}]}""",
                """{"timestamp":"2026-11-22T01:05:00Z","event":"ScanOrganic","ScanType":"Log","Species":"$Codex_Ent_Stratum_07_Name;","Species_Localised":"Stratum Tectonicas","Body":3}""",
                """{"timestamp":"2026-11-22T01:06:00Z","event":"ScanOrganic","ScanType":"Sample","Species":"$Codex_Ent_Stratum_07_Name;","Species_Localised":"Stratum Tectonicas","Body":3}""",
                """{"timestamp":"2026-11-22T01:07:00Z","event":"ScanOrganic","ScanType":"Analyse","Species":"$Codex_Ent_Stratum_07_Name;","Species_Localised":"Stratum Tectonicas","Body":3}""",
                """{"timestamp":"2026-11-22T01:08:00Z","event":"MiningRefined","Type":"$platinum_name;","Type_Localised":"Platinum"}""",
                """{"timestamp":"2026-11-22T01:09:00Z","event":"Bounty","Target":"python","TotalReward":350000,"VictimFaction":"Pirate Gang"}""",
                """{"timestamp":"2026-11-22T01:10:00Z","event":"DockingGranted","LandingPad":27,"StationName":"Hutton Orbital","StationType":"Outpost"}"""
            ]);

            var cfgSvc = new ConfigService();
            cfgSvc.Current.JournalFolderPath = tempDir;
            cfgSvc.Current.CaptureUnhandledEvents = false;
            cfgSvc.Current.EnableEdsmSync = false;
            var bindings = new BindingsService(cfgSvc);
            var inputSim = new InputSimulatorService(bindings, cfgSvc);
            var hub = new GameEventHub();
            using var watcher = new EliteFileWatcherService(cfgSvc, inputSim, hub);
            watcher.RefreshStateForTest();

            var st = watcher.State;
            Assert(st.CommanderName == "CMDR ReplayCmdr", $"Expected CMDR ReplayCmdr, got '{st.CommanderName}'.");
            Assert(st.CurrentSystem == "Alpha Centauri", $"Expected Alpha Centauri, got '{st.CurrentSystem}'.");
            Assert(st.ExplorationBodies.Any(b => b.BodyName == "Alpha Centauri 3"), "Expected Earthlike body recorded in ExplorationBodies.");
            Assert(st.MiningSession.TotalRefinedTons == 1, $"Expected 1t refined Platinum, got {st.MiningSession.TotalRefinedTons}.");
            Assert(st.CombatSession.TotalKills == 1 && st.CombatSession.PendingCredits == 350000,
                $"Expected 1 kill / 350k CR bounty, got {st.CombatSession.TotalKills} / {st.CombatSession.PendingCredits}.");
            Assert(st.AssignedLandingPad == 27, $"Expected LandingPad 27, got {st.AssignedLandingPad}.");
            Assert(st.RecentEvents.Count >= 4, $"Expected >= 4 recent flight-log events, got {st.RecentEvents.Count}.");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task TestMockedExternalHttpIntegrationsAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hudnopy_mockhttp_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        int spanshPollCount = 0;
        int transientGetAttempts = 0;
        int post503Attempts = 0;

        HttpClientHeaderDefaults.RetryTimeProvider = new InstantTimeProvider();
        HttpClientHeaderDefaults.TestHttpHandlerFactory = () => new FakeHttpMessageHandler(req =>
        {
            string url = req.RequestUri?.ToString() ?? "";

            // 0. Transient 503 retry verification: GET retries automatically, POST never duplicates
            if (url.Contains("transient-get.test", StringComparison.OrdinalIgnoreCase))
            {
                transientGetAttempts++;
                return transientGetAttempts == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("recovered") };
            }
            if (url.Contains("non-idempotent-post.test", StringComparison.OrdinalIgnoreCase))
            {
                post503Attempts++;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            // 1. Spansh galaxy route POST -> job id, then poll queued -> ok with float fuel_used
            if (url.Contains("spansh.co.uk/api/generic/route", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"job":"job-test-123"}""", Encoding.UTF8, "application/json")
                };
            }
            if (url.Contains("spansh.co.uk/api/results/job-test-123", StringComparison.OrdinalIgnoreCase))
            {
                spanshPollCount++;
                string body = spanshPollCount == 1
                    ? """{"status":"queued"}"""
                    : """{"status":"ok","result":{"jumps":[{"name":"Sol","distance":0,"fuel_used":0},{"name":"Colonia","distance":22000.5,"neutron_star":true,"fuel_used":4.82}]}}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            // 2. EDSM discovery + traffic + deaths + remote comment
            if (url.Contains("edsm.net/api-v1/system", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"name":"Sol","information":{"faction":"Mother Gaia"}}""", Encoding.UTF8, "application/json") };
            if (url.Contains("edsm.net/api-system-v1/bodies", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"bodies":[{"subType":"Earth-like world","terraformingState":""}]}""", Encoding.UTF8, "application/json") };
            if (url.Contains("edsm.net/api-system-v1/factions", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"factions":[{"name":"Mother Gaia","influence":0.72,"state":"Boom"}]}""", Encoding.UTF8, "application/json") };
            if (url.Contains("edsm.net/api-system-v1/traffic", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"traffic":{"day":120,"week":840}}""", Encoding.UTF8, "application/json") };
            if (url.Contains("edsm.net/api-system-v1/deaths", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"deaths":{"day":4,"week":19}}""", Encoding.UTF8, "application/json") };
            if (url.Contains("edsm.net/api-logs-v1/get-comment", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"comment":"Home base"}""", Encoding.UTF8, "application/json") };

            // 3. Ardent Insight 404 on stripped name -> 200 on raw fallback
            if (url.Contains("ardent-insight.com", StringComparison.OrdinalIgnoreCase))
            {
                if (url.Contains("lowtemperaturediamonds", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[{"systemName":"Sol","stationName":"Galileo","distance":12.4,"sellPrice":510000,"demand":800,"maxLandingPadSize":"L"}]""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        try
        {
            var cfgSvc = new ConfigService();
            cfgSvc.Current.JournalFolderPath = tempDir;
            cfgSvc.Current.CaptureUnhandledEvents = false;
            cfgSvc.Current.EnableSpanshSystemData = true;
            cfgSvc.Current.EnableEdsmSync = true;
            cfgSvc.Current.EdsmCommanderName = "TestCmdr";
            cfgSvc.Current.EdsmApiKey = "fake-key";
            cfgSvc.Current.EnableArdentMarketApi = true;

            var bindings = new BindingsService(cfgSvc);
            var inputSim = new InputSimulatorService(bindings, cfgSvc);
            var hub = new GameEventHub();
            using var watcher = new EliteFileWatcherService(cfgSvc, inputSim, hub);
            using var spansh = new SpanshSystemDataService(cfgSvc, watcher, new InstantTimeProvider());
            using var commSync = new CommunitySyncService(cfgSvc, watcher, hub);
            using var ardent = new ArdentMarketService(cfgSvc, watcher);

            // Spansh galaxy route with float fuel_used (4.82) must succeed without FormatException
            var (sOk, waypoints, sMsg) = await spansh.PlotSpanshRouteAsync("galaxy", "Sol", "Colonia", 65);
            Assert(sOk && waypoints.Count == 2 && waypoints[1].Details.Contains("4.8", StringComparison.Ordinal),
                $"Expected Spansh float fuel_used route success, got ok={sOk}, count={waypoints.Count}, msg='{sMsg}'.");

            // EDSM discovery + intel with RemoteComment
            var disc = await commSync.FetchEdsmDiscoveryAndFactionsAsync("Sol");
            Assert(disc is { IsKnownInEdsm: true, KnownBodiesCount: 1 } && disc.Factions.Count == 1,
                "Expected mocked EDSM discovery intel with 1 body and 1 faction.");
            var intel = await commSync.FetchEdsmSystemIntelAsync("Sol");
            Assert(intel is { TrafficDay: 120, DeathsDay: 4, RemoteComment: "Home base" },
                $"Expected mocked EDSM intel with RemoteComment, got {intel}.");

            // Ardent 404 fallback -> 200 success
            var (aOk, offers, aMsg) = await ardent.SearchCommodityAsync("Low Temperature Diamonds", "sell", "Sol");
            Assert(aOk && offers.Count == 1 && offers[0].Price == 510000,
                $"Expected Ardent 404 fallback to return 1 offer at 510,000 CR, got ok={aOk}, count={offers.Count}, msg='{aMsg}'.");

            // TransientHttpRetryHandler: GET recovers from 503 automatically; POST never duplicates
            using var retryClient = HttpClientHeaderDefaults.CreateHttpClient(TimeSpan.FromSeconds(5));
            using var getResp = await retryClient.GetAsync("https://transient-get.test/ping");
            Assert(getResp.StatusCode == HttpStatusCode.OK && transientGetAttempts == 2,
                $"Expected GET to recover from transient 503 on attempt 2, got {getResp.StatusCode} ({transientGetAttempts} attempts).");
            using var postResp = await retryClient.PostAsync("https://non-idempotent-post.test/upload", new StringContent("{}"));
            Assert(postResp.StatusCode == HttpStatusCode.ServiceUnavailable && post503Attempts == 1,
                $"Expected POST not to retry on 503 (1 attempt), got {post503Attempts} attempts.");
        }
        finally
        {
            HttpClientHeaderDefaults.TestHttpHandlerFactory = null;
            HttpClientHeaderDefaults.RetryTimeProvider = TimeProvider.System;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void TestConfigRoundTripAndResilience()
    {
        var original = new AppConfig
        {
            InterfaceLanguage = "es-ES",
            WebServerPort = 8443,
            EnableFrontierCapi = true,
            EnableEdsmSync = true,
            EnableDiscord = true,
            MiningMinPercent = 35,
            MiningTargetMinerals = "Platinum, Osmium",
            EngineeringWishlistBlueprints = ["fsd_range_g5", "thrusters_dirty_g5"]
        };

        string json = JsonSerializer.Serialize(original, AppConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
        Assert(restored is not null, "AppConfig deserialized to null.");
        Assert(restored!.InterfaceLanguage == "es-ES" && restored.WebServerPort == 8443, "Core config fields lost on round-trip.");
        Assert(restored.EnableFrontierCapi && restored.EnableEdsmSync && restored.EnableDiscord, "Integration opt-in flags lost on round-trip.");
        Assert(restored.MiningMinPercent == 35 && restored.EngineeringWishlistBlueprints.Count == 2, "Operational config fields lost on round-trip.");

        // C# 14 field-backed property clamping must keep out-of-range settings safe
        var clamped = new AppConfig { WebServerPort = 80, DesktopFontScalePercent = 500, AlertSoundVolumePercent = -20, MiningMinPercent = 0 };
        Assert(clamped.WebServerPort == 1024 && clamped.DesktopFontScalePercent == 200 && clamped.AlertSoundVolumePercent == 0 && clamped.MiningMinPercent == 1,
            "AppConfig field-backed clamping failed.");

        // Unknown/legacy JSON fields must not break deserialization
        string legacyJson = """{"InterfaceLanguage":"fr-FR","LegacyRemovedField":true,"ObsoleteCounter":999}""";
        var fromLegacy = JsonSerializer.Deserialize(legacyJson, AppConfigJsonContext.Default.AppConfig);
        Assert(fromLegacy is { InterfaceLanguage: "fr-FR", EnableEddnUpload: false },
            "AppConfig must ignore unknown legacy fields and keep opt-in defaults false.");
    }
}
