// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        HUDnopy.Tests/ResilienceAndSecurityTests.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Mature .NET test patterns inspired by EDDiscovery, EDDI, Jellyfin,
//              and PowerToys: deterministic fuzzing, property-based invariants,
//              WebSocket/EventHub concurrency stress, EDDN sanitization & dedup,
//              and LAN authentication / rate-limiting / security-header tests.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
    private static void TestDeterministicFuzzingAndCorruptionResilience()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hudnopy_fuzz_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // 1. Corrupt/truncated/wrong-type Journal lines mixed with a valid final event
            string journalPath = Path.Combine(tempDir, "Journal.2026-11-22T020000.01.log");
            File.WriteAllLines(journalPath,
            [
                "",
                "   ",
                "{truncated json without closing brace",
                "NOT_JSON_AT_ALL_!@#$%^&*()",
                """{"event":12345,"timestamp":null}""",
                """{"timestamp":"2026-11-22T02:00:00Z","event":"LoadGame","Commander":null,"FuelLevel":"nan","FuelCapacity":-5,"Credits":"oops"}""",
                """{"timestamp":"2026-11-22T02:00:01Z","event":"Location","StarSystem":null,"StarPos":"not-an-array","Docked":"maybe"}""",
                """{"timestamp":"2026-11-22T02:00:02Z","event":"DockingGranted","LandingPad":-999,"StationName":12345}""",
                """{"timestamp":"2026-11-22T02:00:03Z","event":"Scan","BodyName":null,"DistanceFromArrivalLS":"inf","WasDiscovered":"yes"}""",
                """{"timestamp":"2026-11-22T02:00:04Z","event":"FSDJump","StarSystem":"Beagle Point","StarPos":[-1111.56,-134.22,65269.75],"JumpDist":64.2}"""
            ]);

            // Corrupt Status.json and Cargo.json must not crash watcher
            File.WriteAllText(Path.Combine(tempDir, "Status.json"), "{ \"Flags\": \"broken\", ");
            File.WriteAllText(Path.Combine(tempDir, "Cargo.json"), "[]");

            var cfgSvc = new ConfigService();
            cfgSvc.Current.JournalFolderPath = tempDir;
            cfgSvc.Current.CaptureUnhandledEvents = false;
            cfgSvc.Current.EnableEdsmSync = false;
            var bindings = new BindingsService(cfgSvc);
            var inputSim = new InputSimulatorService(bindings, cfgSvc);
            var hub = new GameEventHub();
            using var watcher = new EliteFileWatcherService(cfgSvc, inputSim, hub);
            watcher.RefreshStateForTest();

            Assert(watcher.State.CurrentSystem == "Beagle Point",
                $"Watcher failed to recover from corrupt journal lines; got '{watcher.State.CurrentSystem}'.");

            // 2. Corrupt/empty .binds XML must fall back cleanly without throwing
            File.WriteAllText(Path.Combine(tempDir, "Broken.4.0.binds"), "<Root><BrokenTag attr=\"unterminated");
            cfgSvc.Current.BindingsFolderPath = tempDir;
            cfgSvc.Current.SelectedBindsFile = "Broken.4.0.binds";
            var brokenBindings = new BindingsService(cfgSvc);
            Assert(brokenBindings.Bindings is not null, "BindingsService should remain non-null after corrupt XML.");

            // 3. Adversarial inputs to COVAS AI helpers & tag sanitizer
            string[] adversarial =
            [
                "",
                "   \t\r\n  ",
                new string('[', 500) + "ACTION:TEST" + new string(']', 500),
                "[[QUERY:SYSTEM_INTEL]] [[ACTION:PIP_PRESET:SYS]] [QUERY:INVALID] \0\uFFFD🚀",
                "a".PadRight(8192, 'x')
            ];
            foreach (string input in adversarial)
            {
                string sanitized = AiCopilotService.StripInternalTags(input);
                Assert(sanitized is not null, "StripInternalTags returned null on adversarial input.");
                _ = AiCopilotService.IsRepeatVoiceCommand(input, "COVAS");
                _ = AiCopilotService.IsSilenceVoiceCommand(input);
                _ = AiCopilotService.IsScreenVisionPrompt(input, "COVAS");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void TestPropertyBasedInvariants()
    {
        var rng = new Random(42);

        // 1. SurfaceNavigationService invariants across 200 random planetary coordinate pairs
        for (int i = 0; i < 200; i++)
        {
            double lat1 = rng.NextDouble() * 178.0 - 89.0;
            double lon1 = rng.NextDouble() * 360.0 - 180.0;
            double lat2 = rng.NextDouble() * 178.0 - 89.0;
            double lon2 = rng.NextDouble() * 360.0 - 180.0;
            double radius = 200_000.0 + rng.NextDouble() * 10_000_000.0;
            double heading = rng.NextDouble() * 360.0;

            double d12 = SurfaceNavigationService.CalculateDistanceMeters(lat1, lon1, lat2, lon2, radius);
            double d21 = SurfaceNavigationService.CalculateDistanceMeters(lat2, lon2, lat1, lon1, radius);
            double dSelf = SurfaceNavigationService.CalculateDistanceMeters(lat1, lon1, lat1, lon1, radius);

            Assert(double.IsFinite(d12) && d12 >= 0, $"Invalid distance {d12}.");
            Assert(Math.Abs(d12 - d21) <= Math.Max(1e-3, d12 * 1e-9), $"Distance asymmetry: {d12} vs {d21}.");
            Assert(dSelf < 1e-6, $"Self distance must be 0, got {dSelf}.");

            var sol = SurfaceNavigationService.Compute(lat1, lon1, heading, lat2, lon2, radius);
            Assert(sol.BearingDegrees >= 0.0 && sol.BearingDegrees < 360.0, $"Bearing out of range: {sol.BearingDegrees}.");
            Assert(sol.TurnDeltaDegrees >= -180.0 && sol.TurnDeltaDegrees <= 180.0, $"TurnDelta out of range: {sol.TurnDeltaDegrees}.");
            Assert(!string.IsNullOrEmpty(sol.CompassTape) && !string.IsNullOrEmpty(sol.SteeringArrow), "Empty steering output.");
        }

        // Antipodal pole distance invariant: North Pole to South Pole = pi * R
        double poleDist = SurfaceNavigationService.CalculateDistanceMeters(90, 0, -90, 0, 1_000_000.0);
        Assert(Math.Abs(poleDist - Math.PI * 1_000_000.0) < 1.0, $"Expected pi*R, got {poleDist}.");

        // 2. LandingPadLocatorService invariants: all 45 pads x 5 station types x 6 languages
        string[] stationTypes = ["Coriolis", "Orbis", "Ocellus", "Outpost", "FleetCarrier"];
        string[] langs = ["en-GB", "es-ES", "fr-FR", "de-DE", "ru-RU", "pt-BR"];
        for (int pad = 1; pad <= 45; pad++)
        {
            foreach (string stType in stationTypes)
            {
                var info = LandingPadLocatorService.Resolve(pad, stType);
                Assert(info.PadNumber == pad, $"Pad mismatch for {pad}/{stType}.");
                foreach (string lang in langs)
                {
                    string g = info.FormatGuidance(lang);
                    Assert(!string.IsNullOrWhiteSpace(g), $"Empty guidance for pad {pad}, {stType}, {lang}.");
                }
            }
        }

        // 3. 3D Galactic coordinate distance invariants: non-negativity, symmetry, triangle inequality
        static double Dist3D(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        for (int i = 0; i < 100; i++)
        {
            double[] a = [rng.NextDouble() * 40000 - 20000, rng.NextDouble() * 4000 - 2000, rng.NextDouble() * 65000 - 10000];
            double[] b = [rng.NextDouble() * 40000 - 20000, rng.NextDouble() * 4000 - 2000, rng.NextDouble() * 65000 - 10000];
            double[] c = [rng.NextDouble() * 40000 - 20000, rng.NextDouble() * 4000 - 2000, rng.NextDouble() * 65000 - 10000];
            double dab = Dist3D(a, b), dbc = Dist3D(b, c), dac = Dist3D(a, c);
            Assert(dab >= 0 && Math.Abs(dab - Dist3D(b, a)) < 1e-9, "3D distance symmetry violated.");
            Assert(dac <= dab + dbc + 1e-6, $"Triangle inequality violated: {dac} > {dab} + {dbc}.");
        }

        // 4. ShipLoadoutExportService GZip+Base64 URL round-trip invariant
        string sampleLoadout = """{"event":"Loadout","Ship":"Mandalay","ShipName":"Explorer","Modules":[{"Slot":"FrameShiftDrive","Item":"int_hyperdrive_overcharge_size5_class5"}]}""";
        string? edsyUrl = ShipLoadoutExportService.BuildEdsyUrl(sampleLoadout);
        Assert(!string.IsNullOrEmpty(edsyUrl) && edsyUrl.StartsWith("https://edsy.org/#/I=", StringComparison.Ordinal), "Unexpected EDSY URL prefix.");
        string b64 = Uri.UnescapeDataString(edsyUrl!["https://edsy.org/#/I=".Length..]);
        byte[] gzBytes = Convert.FromBase64String(b64);
        using var gzStream = new GZipStream(new MemoryStream(gzBytes), CompressionMode.Decompress);
        using var reader = new StreamReader(gzStream, Encoding.UTF8);
        string decoded = reader.ReadToEnd();
        using var parsed = JsonDocument.Parse(decoded);
        Assert(parsed.RootElement.GetProperty("Ship").GetString() == "Mandalay", "EDSY GZip round-trip JSON mismatch.");
    }

    private static async Task TestConcurrencyAndThreadSafetyAsync()
    {
        // 1. Concurrent GameEventHub publish + subscribe/unsubscribe stress test (8 parallel publishers)
        var hub = new GameEventHub();
        int receivedCount = 0;
        Action<GameJournalEvent> persistentHandler = _ => Interlocked.Increment(ref receivedCount);
        hub.JournalEventReceived += persistentHandler;

        using var sampleDoc = JsonDocument.Parse("""{"event":"FSDJump","StarSystem":"Sol"}""");
        var publishTasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 40; i++)
            {
                Action<GameJournalEvent> tempHandler = _ => { };
                hub.JournalEventReceived += tempHandler;
                hub.Publish(new GameJournalEvent("FSDJump", DateTimeOffset.UtcNow, sampleDoc.RootElement.Clone(), IsHistorical: false));
                hub.JournalEventReceived -= tempHandler;
            }
        })).ToArray();

        await Task.WhenAll(publishTasks);
        hub.JournalEventReceived -= persistentHandler;
        Assert(receivedCount == 320, $"Expected 320 concurrent GameEventHub deliveries, got {receivedCount}.");

        // 2. Concurrent multi-client WebSocket broadcast stress test (5 simultaneous clients)
        string tempDir = Path.Combine(Path.GetTempPath(), $"hudnopy_conc_test_{Guid.NewGuid():N}");
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
            var wsUri = new UriBuilder(baseUrl) { Scheme = "wss", Path = "/ws" }.Uri;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var sockets = new List<ClientWebSocket>();
            try
            {
                // Connect 5 clients concurrently and read initial snapshot on all 5
                var connectTasks = Enumerable.Range(0, 5).Select(async _ =>
                {
                    var ws = new ClientWebSocket();
                    ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                    ws.Options.SetRequestHeader("Origin", baseUrl);
                    await ws.ConnectAsync(wsUri, cts.Token);
                    string firstFrame = await ReceiveFullWebSocketTextAsync(ws, cts.Token);
                    Assert(firstFrame.Contains("\"state\"", StringComparison.Ordinal), "Missing initial snapshot frame.");
                    return ws;
                }).ToArray();

                sockets.AddRange(await Task.WhenAll(connectTasks));

                // Trigger 10 rapid state updates while closing 2 clients mid-stream
                var closeMidStream = Task.Run(async () =>
                {
                    await sockets[0].CloseAsync(WebSocketCloseStatus.NormalClosure, "mid-stream", cts.Token);
                    await sockets[1].CloseAsync(WebSocketCloseStatus.NormalClosure, "mid-stream", cts.Token);
                });

                string journalFile = Path.Combine(tempDir, "Journal.2026-11-22T021000.01.log");
                for (int i = 0; i < 10; i++)
                {
                    File.AppendAllText(journalFile,
                        $$"""{"timestamp":"2026-11-22T02:10:{{i:00}}Z","event":"FSDJump","StarSystem":"Stress-{{i}}","StarPos":[{{i}},0,0],"JumpDist":10.5}""" + Environment.NewLine);
                    watcher.RefreshStateForTest();
                }

                await closeMidStream;

                // Remaining 3 sockets must still be open and receive broadcast updates cleanly
                foreach (var ws in sockets.Skip(2))
                {
                    string frame = await ReceiveFullWebSocketTextAsync(ws, cts.Token);
                    Assert(frame.Contains("Stress-", StringComparison.Ordinal), "Surviving WS client missed stress broadcast.");
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
                }
            }
            finally
            {
                foreach (var ws in sockets)
                    ws.Dispose();
            }

            await webServer.StopAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task TestEddnSanitizationAndDeduplicationAsync()
    {
        var capturedPayloads = new ConcurrentQueue<string>();
        var eddnSignal = new SemaphoreSlim(0);

        HttpClientHeaderDefaults.TestHttpHandlerFactory = () => new FakeHttpMessageHandler(req =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            if (url.Contains("eddn.edcd.io", StringComparison.OrdinalIgnoreCase))
            {
                string body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                capturedPayloads.Enqueue(body);
                eddnSignal.Release();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        string tempDir = Path.Combine(Path.GetTempPath(), $"hudnopy_eddn_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var cfgSvc = new ConfigService();
            cfgSvc.Current.JournalFolderPath = tempDir;
            cfgSvc.Current.CaptureUnhandledEvents = false;
            cfgSvc.Current.EnableEddnUpload = true;
            cfgSvc.Current.EnableEdsmSync = false;
            cfgSvc.Current.EnableInaraSync = false;

            var bindings = new BindingsService(cfgSvc);
            var inputSim = new InputSimulatorService(bindings, cfgSvc);
            var hub = new GameEventHub();
            using var watcher = new EliteFileWatcherService(cfgSvc, inputSim, hub);
            using var commSync = new CommunitySyncService(cfgSvc, watcher, hub);

            // 1. ApproachSettlement with forbidden personal fields + Latitude/Longitude (must preserve Lat/Lon, strip personal keys)
            using var approachDoc = JsonDocument.Parse(
                """
                {
                  "timestamp":"2026-11-22T02:20:00Z",
                  "event":"ApproachSettlement",
                  "Name":"Dav's Hope",
                  "Name_Localised":"Dav's Hope",
                  "SystemAddress":123456789,
                  "BodyID":4,
                  "BodyName":"Hyades Sector DR-V c2-23 A 5",
                  "Latitude":44.818,
                  "Longitude":-31.389,
                  "Taxi":true,
                  "Multicrew":false,
                  "InSRV":true,
                  "OnFoot":false,
                  "Wanted":true,
                  "ActiveFine":true,
                  "CockpitBreach":true
                }
                """);
            hub.Publish(new GameJournalEvent("ApproachSettlement", DateTimeOffset.UtcNow, approachDoc.RootElement.Clone(), IsHistorical: false));
            Assert(await eddnSignal.WaitAsync(TimeSpan.FromSeconds(3)), "Expected EDDN ApproachSettlement POST.");
            Assert(capturedPayloads.TryDequeue(out string? approachPayload) && !string.IsNullOrEmpty(approachPayload), "Empty EDDN payload.");

            using (var doc = JsonDocument.Parse(approachPayload!))
            {
                var msg = doc.RootElement.GetProperty("message");
                string[] forbidden = ["Name_Localised", "Taxi", "Multicrew", "InSRV", "OnFoot", "Wanted", "ActiveFine", "CockpitBreach"];
                foreach (string key in forbidden)
                    Assert(!msg.TryGetProperty(key, out _), $"EDDN payload leaked forbidden personal key '{key}'.");

                Assert(msg.TryGetProperty("Latitude", out _) && msg.TryGetProperty("Longitude", out _),
                    "EDDN ApproachSettlement must preserve Latitude and Longitude.");
            }

            // 2. Identical Market snapshot updated twice -> SHA-256 deduplication must only send ONCE
            File.WriteAllText(Path.Combine(tempDir, "Journal.2026-11-22T022000.01.log"),
                """{"timestamp":"2026-11-22T02:20:00Z","event":"LoadGame","Commander":"EddnTester","Ship":"Mandalay","Odyssey":true,"Horizons":true}""" + Environment.NewLine);
            string marketPath = Path.Combine(tempDir, "Market.json");
            // Initial read establishes the historical baseline stamp (skipped by EDDN)
            File.WriteAllText(marketPath, """{"timestamp":"2026-11-22T02:20:00Z","event":"Market","MarketID":3228826880,"StationName":"Galileo","StarSystem":"Sol","Items":[]}""");
            watcher.RefreshStateForTest();

            string marketJson =
                """
                {
                  "timestamp":"2026-11-22T02:21:00Z",
                  "event":"Market",
                  "MarketID":3228826880,
                  "StationName":"Galileo",
                  "StarSystem":"Sol",
                  "Items":[{"Name":"$platinum_name;","Category":"$MARKET_category_metals;","MeanPrice":60000,"BuyPrice":0,"Stock":0,"StockBracket":0,"SellPrice":280000,"Demand":500,"DemandBracket":3}]
                }
                """;
            File.WriteAllText(marketPath, marketJson);
            File.SetLastWriteTimeUtc(marketPath, DateTime.UtcNow.AddSeconds(2));
            watcher.RefreshStateForTest();
            Assert(await eddnSignal.WaitAsync(TimeSpan.FromSeconds(3)), "Expected first EDDN Market snapshot POST.");
            Assert(capturedPayloads.TryDequeue(out _), "Expected dequeued Market payload.");

            // Touching file with identical content triggers watcher re-read, but SHA-256 deduplication must block a second POST
            File.SetLastWriteTimeUtc(marketPath, DateTime.UtcNow.AddSeconds(4));
            watcher.RefreshStateForTest();
            bool duplicateSent = await eddnSignal.WaitAsync(TimeSpan.FromMilliseconds(350));
            Assert(!duplicateSent, "EDDN SHA-256 deduplication failed: duplicate identical Market snapshot was sent twice.");
        }
        finally
        {
            HttpClientHeaderDefaults.TestHttpHandlerFactory = null;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task TestWebLanSecurityAndAuthAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hudnopy_lan_test_{Guid.NewGuid():N}");
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
        cfgSvc.Current.WebAccessMode = "code";
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
            string? sessionCode = webServer.SessionCode;
            Assert(!string.IsNullOrWhiteSpace(sessionCode), "Expected LAN mode to generate a non-empty SessionCode.");
            string baseUrl = $"https://127.0.0.1:{port}";

            var cookieContainer = new CookieContainer();
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                AllowAutoRedirect = false,
                CookieContainer = cookieContainer
            };
            using var client = new HttpClient(handler);

            // 1. Unauthenticated GET / redirects (302) to /login and carries hardened security headers
            var unauthRoot = await client.GetAsync($"{baseUrl}/");
            Assert(unauthRoot.StatusCode == HttpStatusCode.Redirect &&
                   unauthRoot.Headers.Location?.ToString() == "/login",
                $"Expected 302 redirect to /login, got {(int)unauthRoot.StatusCode}.");
            Assert(unauthRoot.Headers.Contains("Content-Security-Policy"), "Missing Content-Security-Policy header.");
            Assert(unauthRoot.Headers.TryGetValues("X-Content-Type-Options", out var xcto) && xcto.Contains("nosniff"),
                "Missing X-Content-Type-Options: nosniff.");
            Assert(unauthRoot.Headers.CacheControl?.NoStore == true, "Missing Cache-Control: no-store.");

            // 2. Unauthenticated API call returns 401
            var unauthApi = await client.GetAsync($"{baseUrl}/api/logbook");
            Assert(unauthApi.StatusCode == HttpStatusCode.Unauthorized,
                $"Expected 401 on unauthenticated API call, got {(int)unauthApi.StatusCode}.");

            // 3. Valid login sets HttpOnly + Secure + SameSite=Strict cookie and unlocks GET /
            var loginReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/login")
            {
                Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("code", sessionCode!)])
            };
            loginReq.Headers.Add("Origin", baseUrl);
            var loginRes = await client.SendAsync(loginReq);
            Assert(loginRes.StatusCode == HttpStatusCode.Redirect &&
                   loginRes.Headers.Location?.ToString() == "/",
                $"Expected 302 redirect to / on valid login, got {(int)loginRes.StatusCode}.");
            Assert(loginRes.Headers.TryGetValues("Set-Cookie", out var setCookies) &&
                   setCookies.Any(c => c.Contains("hudnopy-session=", StringComparison.Ordinal) &&
                                       c.Contains("httponly", StringComparison.OrdinalIgnoreCase) &&
                                       c.Contains("secure", StringComparison.OrdinalIgnoreCase) &&
                                       c.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase)),
                "Expected hudnopy-session cookie with HttpOnly, Secure, and SameSite=Strict.");

            var authRoot = await client.GetAsync($"{baseUrl}/");
            Assert(authRoot.StatusCode == HttpStatusCode.OK, $"Expected 200 on authenticated GET /, got {(int)authRoot.StatusCode}.");

            // 4. Repeated invalid login attempts trigger 429 Too Many Requests brute-force lockout
            HttpStatusCode lastBadStatus = HttpStatusCode.OK;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var badReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/login")
                {
                    Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("code", "WRONG-CODE")])
                };
                badReq.Headers.Add("Origin", baseUrl);
                var badRes = await client.SendAsync(badReq);
                lastBadStatus = badRes.StatusCode;
                if (lastBadStatus == HttpStatusCode.TooManyRequests)
                    break;
            }
            Assert(lastBadStatus == HttpStatusCode.TooManyRequests,
                $"Expected 429 Too Many Requests after repeated bad logins, got {(int)lastBadStatus}.");

            await webServer.StopAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
