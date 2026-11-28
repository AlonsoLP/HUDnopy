// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        HUDnopy.Tests/ContractAndLifecycleTests.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: WPF XAML contract tests, Web HUD localization/handler contracts,
//              opt-in isolation & Dispose idempotency, AlertService/Exobiology
//              tests, and loopback HTTP smoke tests for WebServerService.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using HUDnopy.Localization;
using HUDnopy.Models;
using HUDnopy.Services;
using HUDnopy.Services.Integrations;

namespace HUDnopy.Tests;

internal static partial class Program
{
    private static void TestWpfXamlContracts()
    {
        string repoRoot = FindRepoRoot();
        string xamlPath = Path.Combine(repoRoot, "MainWindow.xaml");
        string locCsPath = Path.Combine(repoRoot, "UI", "MainWindow.Localization.cs");
        string enJsonPath = Path.Combine(repoRoot, "Resources", "Languages", "en-GB.json");

        string xamlText = File.ReadAllText(xamlPath, Encoding.UTF8);
        string locCsText = File.ReadAllText(locCsPath, Encoding.UTF8);
        var xdoc = XDocument.Parse(xamlText);

        // 1. Never allow IsEditable="True" on custom-templated WPF ComboBox controls (AGENTS.md rule #5)
        Assert(!Regex.IsMatch(xamlText, @"IsEditable\s*=\s*""True""", RegexOptions.IgnoreCase),
            "MainWindow.xaml must never set IsEditable=\"True\" on custom-templated ComboBox controls.");

        // 2. Collect all valid localization keys from en-GB.json
        var allUiKeys = new HashSet<string>(StringComparer.Ordinal);
        using (var enDoc = JsonDocument.Parse(File.ReadAllText(enJsonPath, Encoding.UTF8)))
        {
            foreach (var section in enDoc.RootElement.EnumerateObject())
            {
                if (section.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in section.Value.EnumerateObject())
                        allUiKeys.Add(prop.Name);
                }
            }
        }

        // 3. Every local:UiText.Key="..." in MainWindow.xaml must exist in en-GB.json
        foreach (Match m in Regex.Matches(xamlText, @"local:UiText\.Key=""([^""]+)"""))
        {
            string key = m.Groups[1].Value;
            Assert(allUiKeys.Contains(key), $"MainWindow.xaml references missing UiText key: '{key}'.");
        }

        // 4. Every DataGrid registered in InitializeTableHeaderKeys() must have local:TableHeader.Key on every column
        //    (except intentional fixed "#" / "ID" columns) and every key must exist in TableHeaderTranslations.
        var tableHeaders = TableHeaderTranslations.ForLanguage("en-GB");
        Match initBlock = Regex.Match(locCsText, @"DataGrid\[\]\s*tables\s*=\s*\{(?<body>[^}]+)\}", RegexOptions.Singleline);
        Assert(initBlock.Success, "Could not locate InitializeTableHeaderKeys() tables array in MainWindow.Localization.cs.");

        var registeredGrids = Regex.Matches(initBlock.Groups["body"].Value, @"\b(Grid[A-Za-z0-9_]+)\b")
            .Select(static m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        Assert(registeredGrids.Count >= 20, $"Expected >= 20 localized DataGrids, found {registeredGrids.Count}.");

        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace localNs = "clr-namespace:HUDnopy";
        foreach (var dg in xdoc.Descendants().Where(static e => e.Name.LocalName == "DataGrid"))
        {
            string? name = dg.Attribute(xNs + "Name")?.Value;
            if (string.IsNullOrWhiteSpace(name) || !registeredGrids.Contains(name))
                continue;

            foreach (var col in dg.Descendants().Where(static e => e.Name.LocalName.EndsWith("Column", StringComparison.Ordinal)))
            {
                string? header = col.Attribute("Header")?.Value;
                if (header is "#" or "ID" || string.IsNullOrWhiteSpace(header))
                    continue;

                string? key = col.Attribute(localNs + "TableHeader.Key")?.Value;
                Assert(!string.IsNullOrWhiteSpace(key),
                    $"DataGrid '{name}' column '{header}' is missing local:TableHeader.Key (would crash InitializeTableHeaderKeys).");
                Assert(tableHeaders.ContainsKey(key!),
                    $"DataGrid '{name}' column '{header}' references unknown TableHeader key '{key}'.");
            }
        }
    }

    private static void TestWebHudContracts()
    {
        string repoRoot = FindRepoRoot();
        string html = File.ReadAllText(Path.Combine(repoRoot, "Web", "index.html"), Encoding.UTF8);
        string js = File.ReadAllText(Path.Combine(repoRoot, "Web", "app.js"), Encoding.UTF8);
        using var enDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "Resources", "Languages", "en-GB.json"), Encoding.UTF8));

        var sectionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nav"] = "navigation",
            ["dashboard"] = "dashboard",
            ["flight"] = "flight",
            ["operations"] = "operations",
            ["translation"] = "tableHeaders",
            ["carrier"] = "carrier",
            ["input"] = "input"
        };

        // 1. Every data-*-key / data-*-placeholder in Web/index.html must exist in en-GB.json
        foreach (Match m in Regex.Matches(html, @"data-(nav|dashboard|flight|operations|translation|carrier|input)-(?:key|placeholder)=""([^""]+)"""))
        {
            string attrSection = m.Groups[1].Value;
            string key = m.Groups[2].Value;
            string jsonSection = sectionMap[attrSection];
            Assert(enDoc.RootElement.TryGetProperty(jsonSection, out var secObj) && secObj.TryGetProperty(key, out _),
                $"Web/index.html references missing localization key '{jsonSection}.{key}' (attr: data-{attrSection}-*).");
        }

        // 2. Every inline HTML handler (onclick/onchange/oninput) must exist as a function in Web/app.js
        foreach (Match m in Regex.Matches(html, @"on(?:click|change|input)=""([a-zA-Z0-9_]+)\("))
        {
            string fn = m.Groups[1].Value;
            Assert(Regex.IsMatch(js, $@"\bfunction\s+{Regex.Escape(fn)}\s*\("),
                $"Web/index.html calls missing JS function '{fn}()'.");
        }

        // 3. Basic structural balance check for Web/app.js (catches accidental brace/paren truncation)
        int braceDepth = 0, parenDepth = 0;
        bool inSingle = false, inDouble = false, inTemplate = false, inLineComment = false, inBlockComment = false;
        for (int i = 0; i < js.Length; i++)
        {
            char c = js[i];
            char next = i + 1 < js.Length ? js[i + 1] : '\0';
            if (inLineComment) { if (c == '\n') inLineComment = false; continue; }
            if (inBlockComment) { if (c == '*' && next == '/') { inBlockComment = false; i++; } continue; }
            if (inSingle) { if (c == '\\') { i++; continue; } if (c == '\'') inSingle = false; continue; }
            if (inDouble) { if (c == '\\') { i++; continue; } if (c == '"') inDouble = false; continue; }
            if (inTemplate) { if (c == '\\') { i++; continue; } if (c == '`') inTemplate = false; continue; }

            if (c == '/' && next == '/') { inLineComment = true; i++; continue; }
            if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
            if (c == '\'') { inSingle = true; continue; }
            if (c == '"') { inDouble = true; continue; }
            if (c == '`') { inTemplate = true; continue; }

            if (c == '{') braceDepth++;
            else if (c == '}') braceDepth--;
            else if (c == '(') parenDepth++;
            else if (c == ')') parenDepth--;
        }
        Assert(braceDepth == 0, $"Unbalanced braces in Web/app.js (depth={braceDepth}).");
    }

    private static async Task TestOptInIsolationAndDisposeAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hudnopy_optin_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfgSvc = new ConfigService();
            cfgSvc.Current.JournalFolderPath = tempDir;
            cfgSvc.Current.CaptureUnhandledEvents = false;
            cfgSvc.Current.EnableFrontierCapi = false;
            cfgSvc.Current.EnableEddnUpload = false;
            cfgSvc.Current.EnableEdsmSync = false;
            cfgSvc.Current.EnableInaraSync = false;
            cfgSvc.Current.EnableSpanshSystemData = false;
            cfgSvc.Current.EnableEliteBgsWatchlist = false;
            cfgSvc.Current.EnableArdentMarketApi = false;
            cfgSvc.Current.EnableEdastroDssa = false;
            cfgSvc.Current.EnableCanonnUpload = false;
            cfgSvc.Current.EnableDiscord = false;

            var bindings = new BindingsService(cfgSvc);
            var inputSim = new InputSimulatorService(bindings, cfgSvc);
            var hub = new GameEventHub();
            var watcher = new EliteFileWatcherService(cfgSvc, inputSim, hub);
            var spansh = new SpanshSystemDataService(cfgSvc, watcher);
            var commSync = new CommunitySyncService(cfgSvc, watcher, hub);
            var capi = new FrontierCapiService(cfgSvc, watcher, hub);
            var bgs = new EliteBgsWatchlistService(cfgSvc, watcher);
            var ardent = new ArdentMarketService(cfgSvc, watcher);
            var edastro = new EdastroCanonnService(cfgSvc, watcher, hub);
            var discord = new DiscordIntegrationService(cfgSvc, watcher, hub, bgs);

            var sw = Stopwatch.StartNew();
            Assert(!(await spansh.SearchNearestServicesAsync("tritium", "Sol", null)).Success, "Spansh nearest must block when disabled.");
            Assert(!(await spansh.PlotSpanshRouteAsync("neutron", "Sol", "Colonia", 50)).Success, "Spansh route must block when disabled.");
            Assert(!(await spansh.PlotTradeRouteAsync("Sol", "Galileo", 100, 1_000_000, 30, true)).Success, "Spansh trade must block when disabled.");
            Assert(await commSync.FetchEdsmSystemIntelAsync("Sol") is null, "EDSM intel must return null when disabled.");
            Assert(await commSync.FetchEdsmDiscoveryAndFactionsAsync("Sol") is null, "EDSM discovery must return null when disabled.");
            Assert(!(await commSync.FetchInaraCommanderProfileAsync("Test")).Success, "Inara profile must block when disabled.");
            Assert(!(await capi.SyncAllAsync()).Success, "Frontier cAPI must block when disabled.");
            Assert(!(await bgs.RefreshAsync()).Success, "EliteBGS must block when disabled.");
            Assert(!(await ardent.SearchCommodityAsync("Platinum")).Success, "Ardent must block when disabled.");
            Assert(!(await edastro.GetNearestDssaCarriersAsync()).Success, "EDAstro DSSA must block when disabled.");
            Assert(!(await discord.SendWebhookAsync("Test", "Body")).Success, "Discord webhook must block when disabled.");
            sw.Stop();
            Assert(sw.ElapsedMilliseconds < 500, $"Disabled opt-in calls took too long ({sw.ElapsedMilliseconds} ms), suggesting unexpected network I/O.");

            // Double-Dispose idempotency check across all services
            using var piper = new PiperTtsService();
            using var whisper = new WhisperSttService();
            using var radio = new CockpitRadioService(cfgSvc);
            IDisposable[] disposables = [discord, edastro, ardent, bgs, capi, commSync, spansh, watcher, piper, whisper, radio];
            foreach (var d in disposables)
            {
                d.Dispose();
                d.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void TestAlertsAndExobiology()
    {
        // 1. ExobiologyPredictor species distance & valuation
        Assert(ExobiologyPredictor.GetRequiredDistanceMeters("Stratum Tectonicas") == 500,
            "Expected Stratum Tectonicas distance = 500 m.");
        Assert(ExobiologyPredictor.GetRequiredDistanceMeters("Bacterium Aurasus") == 500,
            "Expected Bacterium distance = 500 m.");
        long baseVal = ExobiologyPredictor.EstimateSpeciesCredits("Stratum Tectonicas", firstFootfall: false);
        long ffVal = ExobiologyPredictor.EstimateSpeciesCredits("Stratum Tectonicas", firstFootfall: true);
        Assert(baseVal > 0 && ffVal == baseVal * 5, $"Expected 5x First Footfall multiplier, got base={baseVal}, ff={ffVal}.");

        // 2. AlertService fuel low -> critical escalation & DismissAllActiveAlerts
        string tempDir = Path.Combine(Path.GetTempPath(), $"hudnopy_alert_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfgSvc = new ConfigService();
            cfgSvc.Current.JournalFolderPath = tempDir;
            cfgSvc.Current.CaptureUnhandledEvents = false;
            var bindings = new BindingsService(cfgSvc);
            var inputSim = new InputSimulatorService(bindings, cfgSvc);
            var hub = new GameEventHub();
            using var watcher = new EliteFileWatcherService(cfgSvc, inputSim, hub);
            var alerts = new AlertService(cfgSvc, watcher, hub);

            // Publish a live critical journal alert (UnderAttack + HullDamage) and verify alert creation & dismissal
            using var dmgDoc = JsonDocument.Parse("""{"timestamp":"2026-11-22T00:00:02Z","event":"HullDamage","Health":0.25,"PlayerPilot":true,"Fighter":false}""");
            hub.Publish(new GameJournalEvent("HullDamage", DateTimeOffset.UtcNow, dmgDoc.RootElement.Clone(), IsHistorical: false));
            using var atkDoc = JsonDocument.Parse("""{"timestamp":"2026-11-22T00:00:03Z","event":"UnderAttack","Target":"You"}""");
            hub.Publish(new GameJournalEvent("UnderAttack", DateTimeOffset.UtcNow, atkDoc.RootElement.Clone(), IsHistorical: false));

            Assert(alerts.Alerts.Count > 0, "Expected at least one recorded alert after live HullDamage/UnderAttack events.");
            alerts.DismissAllActiveAlerts();
            Assert(alerts.ActiveAlerts.Count == 0,
                $"Expected all active alerts dismissed, got remaining={alerts.ActiveAlerts.Count}.");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task TestWebServerLoopbackHttpAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hudnopy_web_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        // Pick a free ephemeral TCP port on loopback
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
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

            // 1. HTML + CSS + JS assets return 200 OK (and Brotli pre-compression when Accept-Encoding: br is sent)
            using var rHtml = await http.GetAsync($"{baseUrl}/");
            Assert(rHtml.StatusCode == HttpStatusCode.OK, $"Expected 200 for /, got {rHtml.StatusCode}.");
            using var brReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/app.js");
            brReq.Headers.Add("Accept-Encoding", "br, gzip");
            using var rJs = await http.SendAsync(brReq);
            Assert(rJs.StatusCode == HttpStatusCode.OK, $"Expected 200 for /app.js, got {rJs.StatusCode}.");
            Assert(rJs.Content.Headers.ContentEncoding.Contains("br"),
                $"Expected Content-Encoding: br on /app.js, got '{string.Join(",", rJs.Content.Headers.ContentEncoding)}'.");

            using var rState = await http.GetAsync($"{baseUrl}/api/state");
            Assert(rState.StatusCode == HttpStatusCode.OK, $"Expected 200 for /api/state, got {rState.StatusCode}.");
            string stateJson = await rState.Content.ReadAsStringAsync();
            using var stateDoc = JsonDocument.Parse(stateJson);
            Assert(stateDoc.RootElement.ValueKind == JsonValueKind.Object && stateDoc.RootElement.TryGetProperty("state", out _),
                "Expected /api/state to return a valid JSON object with a 'state' property.");

            // 2. POST without same-origin header is rejected with HTTP 403 (CSRF protection)
            using var csrfContent = new StringContent("{}", Encoding.UTF8, "application/json");
            using var rCsrf = await http.PostAsync($"{baseUrl}/api/theme", csrfContent);
            Assert(rCsrf.StatusCode == HttpStatusCode.Forbidden, $"Expected 403 for missing Origin header on POST /api/theme, got {rCsrf.StatusCode}.");

            // 3. Same-origin POST /api/theme rejects malformed JSON with HTTP 400
            using var badJsonReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/theme")
            {
                Content = new StringContent("{not-json", Encoding.UTF8, "application/json")
            };
            badJsonReq.Headers.Add("Origin", baseUrl);
            using var rBadJson = await http.SendAsync(badJsonReq);
            Assert(rBadJson.StatusCode == HttpStatusCode.BadRequest, $"Expected 400 for malformed JSON on /api/theme, got {rBadJson.StatusCode}.");

            // 4. Same-origin POST /api/theme rejects invalid hex color with HTTP 400
            using var badHexReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/theme")
            {
                Content = new StringContent("""{"accentHex":"not-a-color"}""", Encoding.UTF8, "application/json")
            };
            badHexReq.Headers.Add("Origin", baseUrl);
            using var rBadHex = await http.SendAsync(badHexReq);
            Assert(rBadHex.StatusCode == HttpStatusCode.BadRequest, $"Expected 400 for invalid hex on /api/theme, got {rBadHex.StatusCode}.");

            await webServer.StopAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
