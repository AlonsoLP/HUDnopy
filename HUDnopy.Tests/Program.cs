// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        HUDnopy.Tests/Program.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Zero-dependency automated regression test suite for HUDnopy core logic,
//              localization parity, format placeholders, COVAS helpers, and Web HUD assets.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
    private sealed record TestCaseResult(string Name, bool Passed, bool Skipped, long ElapsedMs, string? ErrorMessage);

    private static int _passed;
    private static int _failed;
    private static int _skipped;
    private static string? _filter;
    private static string? _junitPath;
    private static readonly List<TestCaseResult> _results = new();

    [GeneratedRegex(@"\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}")]
    private static partial Regex GetFormatPlaceholderRegex();
    private static readonly Regex FormatPlaceholderRegex = GetFormatPlaceholderRegex();

    private static async Task<int> Main(string[] args)
    {
        try { System.Windows.Application.ResourceAssembly = typeof(HUDnopy.App).Assembly; } catch { }
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--filter", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                _filter = args[++i];
            else if (args[i].Equals("--junit", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                _junitPath = args[++i];
        }

        var totalSw = Stopwatch.StartNew();
        Console.WriteLine("=== HUDnopy Automated Test Suite ===");
        if (!string.IsNullOrWhiteSpace(_filter))
            Console.WriteLine($"Filter: \"{_filter}\"");

        RunTest("1. Localization: 6-language JSON 100% key parity (baseline: en-GB)", TestLocalizationParity);
        RunTest("2. Localization: string.Format placeholder parity across all 6 languages", TestLocalizationFormatPlaceholders);
        RunTest("3. Default Config: English (en-GB) is primary default language", TestDefaultLanguageIsEnglish);
        RunTest("4. ModuleDisplayCatalog: Official localized module names (6 languages)", TestModuleDisplayCatalog);
        RunTest("5. LandingPadLocatorService: All 45 pads resolve cleanly", TestLandingPadLocator);
        RunTest("6. SurfaceNavigationService: Spherical distance & bearing math", TestSurfaceNavigation);
        RunTest("7. EngineeringWishlistService: Catalog, ToString, and live evaluation", TestEngineeringWishlist);
        RunTest("8. COVAS AI Helpers: Speech normalization, Auto thinking classifier, voice macro match", TestAiCopilotHelpers);
        RunTest("9. ShipLoadoutExportService: EDSY & Coriolis GZip+Base64 URL generation", TestShipLoadoutExport);
        RunTest("10. ExpeditionAnalyticsService: Journal folder analytics & logbook query", TestExpeditionAnalytics);
        RunTest("11. Web HUD Assets: index.html, styles.css, and app.js integrity", TestWebAssetsIntegrity);
        RunTest("12. EliteFileWatcherService: Synthetic Journal, Status, Cargo & LandingPad lifecycle", TestTelemetryWatcherSynthetic);
        RunTest("13. COVAS AI Tags & Local Queries: [[QUERY]]/[[ACTION]] sanitizer and tag formats", TestCovasTagSanitizer);
        RunTest("14. BindingsService & LanguageCatalog: .binds XML parsing & ISO-639 normalization", TestBindingsAndLanguageCatalog);
        RunTest("15. Desktop <-> Web Parity: Web HUD sections match Desktop navigation keys", TestDesktopWebSectionParity);
        RunTest("16. External Integrations v0.3.0: Opt-in toggles, parsers, DSSA 3D sort & Discord IPC", TestExternalIntegrationsV3);
        RunTest("17. WPF XAML Contracts: MainWindow DataGrid headers, UiText keys & ComboBox rules", TestWpfXamlContracts);
        RunTest("18. Web HUD Contracts: All data-*-key, HTML handlers & JS syntax balance", TestWebHudContracts);
        await RunTestAsync("19. Opt-In Isolation & Dispose Idempotency: 0 network when disabled & double-Dispose safe", TestOptInIsolationAndDisposeAsync);
        RunTest("20. AlertService & ExobiologyPredictor: Fuel/hull alerts, dismissal & species insights", TestAlertsAndExobiology);
        await RunTestAsync("21. WebServerService Loopback HTTP: Asset serving & /api/theme 400 validation", TestWebServerLoopbackHttpAsync);
        await RunTestAsync("22. WebServerService WebSocket (/ws): Live snapshot contract & broadcast", TestWebSocketIntegrationAsync);
        RunTest("23. Journal Replay: Full 12-event commander session state machine", TestFullSessionJournalReplay);
        await RunTestAsync("24. Mocked External HTTP APIs: Spansh polling, EDSM intel & Ardent 404 fallback", TestMockedExternalHttpIntegrationsAsync);
        RunTest("25. ConfigService Persistence: AppConfig JSON round-trip & unknown-field resilience", TestConfigRoundTripAndResilience);
        RunTest("26. Deterministic Fuzzing: Corrupt JSON/XML & adversarial COVAS inputs", TestDeterministicFuzzingAndCorruptionResilience);
        RunTest("27. Property-Based Invariants: Surface math, 45x5x6 pads, 3D geometry & EDSY GZip", TestPropertyBasedInvariants);
        await RunTestAsync("28. Concurrency & Thread-Safety: 8-thread EventHub & 5-client WS broadcast stress", TestConcurrencyAndThreadSafetyAsync);
        await RunTestAsync("29. EDDN Privacy & Deduplication: Personal-key strip, Lat/Lon & SHA-256 dedup", TestEddnSanitizationAndDeduplicationAsync);
        await RunTestAsync("30. Web LAN Security & Auth: 302/401/429 rate-limit, Secure cookies & CSP headers", TestWebLanSecurityAndAuthAsync);
        RunTest("31. External Data Unification: Spansh/Local bodies merge, toggle-off purge & CG precedence", TestExternalDataUnificationAndToggleResilience);
        RunTest("32. Crash Sanitization, Support ZIP & WPF MainWindow Layout Smoke-Test", TestCrashSanitizationAndWpfLayoutSmoke);
        RunTest("33. v0.5.0 Visual & Immersion: HUD/EDHM import, Exo radar math, 2D System Map & Update semver", TestVisualImmersionV050);
        RunTest("34. v0.5.0 Overlay, PWA, Deck Editor, Whisper Offline STT & Cockpit Radio", TestV050OverlayPwaDeckWhisperRadio);

        totalSw.Stop();
        if (!string.IsNullOrWhiteSpace(_junitPath))
            WriteJunitXmlReport(_junitPath, totalSw.ElapsedMilliseconds);

        Console.WriteLine();
        Console.WriteLine($"Results: {_passed} passed, {_failed} failed{(_skipped > 0 ? $", {_skipped} skipped" : "")} ({totalSw.ElapsedMilliseconds} ms).");
        return _failed == 0 ? 0 : 1;
    }

    private static void WriteJunitXmlReport(string outputPath, long totalMs)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var suite = new XElement("testsuite",
            new XAttribute("name", "HUDnopy.Tests"),
            new XAttribute("tests", _results.Count),
            new XAttribute("failures", _failed),
            new XAttribute("skipped", _skipped),
            new XAttribute("time", (totalMs / 1000.0).ToString("0.000", CultureInfo.InvariantCulture)));

        foreach (var r in _results)
        {
            var tc = new XElement("testcase",
                new XAttribute("classname", "HUDnopy.Tests"),
                new XAttribute("name", r.Name),
                new XAttribute("time", (r.ElapsedMs / 1000.0).ToString("0.000", CultureInfo.InvariantCulture)));
            if (r.Skipped)
                tc.Add(new XElement("skipped"));
            else if (!r.Passed)
                tc.Add(new XElement("failure", new XAttribute("message", r.ErrorMessage ?? "Assertion failed")));
            suite.Add(tc);
        }

        new XDocument(new XElement("testsuites", suite)).Save(outputPath);
    }

    private static bool MatchesFilter(string name) =>
        string.IsNullOrWhiteSpace(_filter) || name.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    private static void RunTest(string name, Action test)
    {
        if (!MatchesFilter(name))
        {
            _skipped++;
            _results.Add(new TestCaseResult(name, false, true, 0, null));
            return;
        }
        var sw = Stopwatch.StartNew();
        try
        {
            test();
            _passed++;
            _results.Add(new TestCaseResult(name, true, false, sw.ElapsedMilliseconds, null));
            Console.WriteLine($"[PASS] {name} ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            _failed++;
            _results.Add(new TestCaseResult(name, false, false, sw.ElapsedMilliseconds, ex.Message));
            Console.WriteLine($"[FAIL] {name} ({sw.ElapsedMilliseconds} ms): {ex.Message}");
        }
    }

    private static async Task RunTestAsync(string name, Func<Task> test)
    {
        if (!MatchesFilter(name))
        {
            _skipped++;
            _results.Add(new TestCaseResult(name, false, true, 0, null));
            return;
        }
        var sw = Stopwatch.StartNew();
        try
        {
            await test();
            _passed++;
            _results.Add(new TestCaseResult(name, true, false, sw.ElapsedMilliseconds, null));
            Console.WriteLine($"[PASS] {name} ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            _failed++;
            _results.Add(new TestCaseResult(name, false, false, sw.ElapsedMilliseconds, ex.Message));
            Console.WriteLine($"[FAIL] {name} ({sw.ElapsedMilliseconds} ms): {ex.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HUDnopy.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate HUDnopy.csproj repository root.");
    }

    private static Dictionary<string, Dictionary<string, string>> LoadLanguageSections(string lang)
    {
        string path = Path.Combine(FindRepoRoot(), "Resources", "Languages", $"{lang}.json");
        Assert(File.Exists(path), $"Missing language file: {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var sec in doc.RootElement.EnumerateObject())
        {
            if (sec.Value.ValueKind != JsonValueKind.Object)
                continue;
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in sec.Value.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    entries[prop.Name] = prop.Value.GetString() ?? "";
            }
            sections[sec.Name] = entries;
        }
        return sections;
    }

    private static void TestLocalizationParity()
    {
        string[] langs = ["en-GB", "es-ES", "fr-FR", "de-DE", "ru-RU", "pt-BR"];
        var baseline = LoadLanguageSections("en-GB");
        Assert(baseline.Count >= 5, "Expected at least 5 translation sections in en-GB.json.");

        foreach (string lang in langs)
        {
            var current = LoadLanguageSections(lang);
            Assert(current.Count == baseline.Count, $"Section count mismatch in {lang}.json");
            foreach (var (secName, baseEntries) in baseline)
            {
                Assert(current.TryGetValue(secName, out var curEntries), $"Missing section '{secName}' in {lang}.json");
                var missing = baseEntries.Keys.Except(curEntries!.Keys).ToList();
                var extra = curEntries.Keys.Except(baseEntries.Keys).ToList();
                Assert(missing.Count == 0, $"{lang}.json missing keys in '{secName}': {string.Join(", ", missing)}");
                Assert(extra.Count == 0, $"{lang}.json extra keys in '{secName}': {string.Join(", ", extra)}");
                foreach (var (k, v) in curEntries)
                {
                    if (!(secName == "languageSettings" && k == "partialWarning"))
                        Assert(!string.IsNullOrWhiteSpace(v), $"Empty value in {lang}.json -> {secName}.{k}");
                }
            }
        }
    }

    private static void TestLocalizationFormatPlaceholders()
    {
        string[] langs = ["es-ES", "fr-FR", "de-DE", "ru-RU", "pt-BR"];
        var baseline = LoadLanguageSections("en-GB");

        static string PlaceholderSignature(string text)
        {
            var indices = FormatPlaceholderRegex.Matches(text)
                .Select(m => int.Parse(m.Groups[1].Value))
                .Distinct()
                .OrderBy(static i => i)
                .ToArray();
            return string.Join(",", indices);
        }

        foreach (string lang in langs)
        {
            var current = LoadLanguageSections(lang);
            foreach (var (secName, baseEntries) in baseline)
            {
                var curEntries = current[secName];
                foreach (var (k, baseVal) in baseEntries)
                {
                    string baseSig = PlaceholderSignature(baseVal);
                    string curSig = PlaceholderSignature(curEntries[k]);
                    Assert(baseSig == curSig,
                        $"Format placeholder mismatch in {lang}.json -> {secName}.{k}: en-GB has [{baseSig}], {lang} has [{curSig}]");
                }
            }
        }
    }

    private static void TestDefaultLanguageIsEnglish()
    {
        var cfg = new AppConfig();
        Assert(cfg.InterfaceLanguage == "en-GB", $"Expected default InterfaceLanguage to be en-GB, got '{cfg.InterfaceLanguage}'.");
        Assert(cfg.AiGeminiModel == "gemini-flash-lite-latest", $"Expected default Gemini model to be gemini-flash-lite-latest, got '{cfg.AiGeminiModel}'.");
    }

    private static void TestModuleDisplayCatalog()
    {
        Assert(ModuleDisplayCatalog.Format("hpt_plasmaaccelerator_fixed_huge", "en-GB") == "Class 4 Plasma Accelerator (Fixed)",
            "Unexpected en-GB Plasma Accelerator decode.");
        Assert(ModuleDisplayCatalog.Format("hpt_plasmaaccelerator_fixed_huge", "es-ES") == "Clase 4 Acelerador de plasma (Fijo)",
            "Unexpected es-ES Plasma Accelerator decode.");
        Assert(ModuleDisplayCatalog.Format("int_hyperdrive_overcharge_size5_class5", "es-ES") == "5A Motor de distorsión (SCO)",
            "Unexpected es-ES FSD SCO decode.");
        Assert(ModuleDisplayCatalog.Format("int_hyperdrive_overcharge_size5_class5", "fr-FR") == "5A Réacteur FSD (SCO)",
            "Unexpected fr-FR FSD SCO decode.");
        Assert(ModuleDisplayCatalog.Format("int_fuelscoop_size6_class5", "de-DE") == "6A Treibstoffsammler",
            "Unexpected de-DE Fuel Scoop decode.");
        Assert(ModuleDisplayCatalog.Format("int_fuelscoop_size6_class5", "ru-RU") == "6A Топливозаборник",
            "Unexpected ru-RU Fuel Scoop decode.");
        Assert(ModuleDisplayCatalog.Format("int_fuelscoop_size6_class5", "pt-BR") == "6A Coletor de Combustível",
            "Unexpected pt-BR Fuel Scoop decode.");
        Assert(ModuleDisplayCatalog.Format("anaconda_armour_grade3", "en-GB") == "Military Grade Composite (Anaconda)",
            "Unexpected en-GB Armour decode.");
    }

    private static void TestLandingPadLocator()
    {
        for (int pad = 1; pad <= 45; pad++)
        {
            var info = LandingPadLocatorService.Resolve(pad, "Coriolis");
            Assert(info.PadNumber == pad, $"Pad {pad} number mismatch.");
            Assert(info.ClockHour is >= 1 and <= 12, $"Pad {pad} invalid clock hour {info.ClockHour}.");
            Assert(!string.IsNullOrWhiteSpace(info.FormatGuidance("en-GB")), $"Pad {pad} empty en-GB guidance.");
            Assert(!string.IsNullOrWhiteSpace(info.FormatGuidance("es-ES")), $"Pad {pad} empty es-ES guidance.");
        }
    }

    private static void TestSurfaceNavigation()
    {
        double zeroDist = SurfaceNavigationService.CalculateDistanceMeters(10.0, 20.0, 10.0, 20.0, 1_500_000.0);
        Assert(zeroDist < 0.01, $"Expected 0m distance for identical coordinates, got {zeroDist}");

        var northSol = SurfaceNavigationService.Compute(0.0, 0.0, 0.0, 1.0, 0.0, 1_500_000.0);
        Assert(northSol.BearingDegrees is <= 1.0 or >= 359.0, $"Expected ~0 deg bearing due North, got {northSol.BearingDegrees}");
        Assert(northSol.DistanceMeters > 20_000, $"Expected >20 km for 1 deg latitude, got {northSol.DistanceMeters}");

        var eastSol = SurfaceNavigationService.Compute(0.0, 0.0, 0.0, 0.0, 1.0, 1_500_000.0);
        Assert(Math.Abs(eastSol.BearingDegrees - 90.0) < 1.0, $"Expected ~90 deg bearing due East, got {eastSol.BearingDegrees}");
    }

    private static void TestEngineeringWishlist()
    {
        Assert(EngineeringWishlistService.Catalog.Count >= 10, "Expected at least 10 curated engineering blueprints.");
        foreach (var bp in EngineeringWishlistService.Catalog)
        {
            Assert(bp.ToString() == bp.DisplayName, $"Blueprint {bp.Id} ToString() should equal DisplayName.");
            Assert(bp.Ingredients.Count > 0, $"Blueprint {bp.Id} has no ingredients.");
        }

        var state = new ShipState
        {
            MaterialsInventory =
            [
                new MaterialItem { Name = "dataminedwake", DisplayName = "Datamined Wake Exceptions", Count = 3, Category = "Encoded", Grade = 5 }
            ]
        };
        var rows = EngineeringWishlistService.Evaluate(["fsd_range_g5"], state);
        Assert(rows.Count > 0, "Expected evaluated material rows for fsd_range_g5.");
        var dwe = rows.FirstOrDefault(r => r.MaterialName.Contains("Datamined Wake", StringComparison.OrdinalIgnoreCase));
        Assert(dwe is not null && dwe.Owned == 3, "Expected Datamined Wake Exceptions owned count to be 3.");
    }

    private static void TestAiCopilotHelpers()
    {
        string spokenEn = AiCopilotService.NormalizeTextForSpeech("**CMDR**, 1,250,000 CR at 42.5 LY (12/64 t)", "en-GB");
        Assert(!spokenEn.Contains('*'), "Markdown asterisks should be stripped.");
        Assert(spokenEn.Contains("Commander", StringComparison.OrdinalIgnoreCase), $"Expected 'Commander' in '{spokenEn}'");
        Assert(spokenEn.Contains("1250000 credits", StringComparison.OrdinalIgnoreCase), $"Expected normalized credits in '{spokenEn}'");
        Assert(spokenEn.Contains("42.5 light years", StringComparison.OrdinalIgnoreCase), $"Expected normalized light years in '{spokenEn}'");

        string crhEs = AiCopilotService.NormalizeTextForSpeech("500,000 CR/h", "es-ES");
        Assert(crhEs.Contains("500000 créditos por hora", StringComparison.OrdinalIgnoreCase), $"Expected '500000 créditos por hora', got '{crhEs}'");
        string crhEn = AiCopilotService.NormalizeTextForSpeech("500,000 CR/h", "en-GB");
        Assert(crhEn.Contains("500000 credits per hour", StringComparison.OrdinalIgnoreCase), $"Expected '500000 credits per hour', got '{crhEn}'");

        Assert(!AiCopilotService.IsComplexPrompt("Hull status"), "Short status query should be classified as fast/simple.");
        Assert(AiCopilotService.IsComplexPrompt("Compare my Anaconda build strategy for AX combat"), "Comparison/strategy query should trigger complex thinking.");

        string normMacro = AiCopilotService.NormalizeVoiceMatchText("COVAS, ¡Evasión máxima!", "COVAS");
        Assert(normMacro == "evasion maxima", $"Expected 'evasion maxima', got '{normMacro}'");
    }

    private static void TestShipLoadoutExport()
    {
        string sampleLoadout = """{"event":"Loadout","Ship":"anaconda","ShipName":"HMS Test","Modules":[]}""";
        string? edsy = ShipLoadoutExportService.BuildEdsyUrl(sampleLoadout);
        string? coriolis = ShipLoadoutExportService.BuildCoriolisUrl(sampleLoadout);

        Assert(!string.IsNullOrWhiteSpace(edsy) && edsy.StartsWith("https://edsy.org/#/I=", StringComparison.Ordinal), "Invalid EDSY export URL.");
        Assert(!string.IsNullOrWhiteSpace(coriolis) && coriolis.StartsWith("https://coriolis.io/import?data=", StringComparison.Ordinal), "Invalid Coriolis export URL.");
        Assert(ShipLoadoutExportService.BuildEdsyUrl("{invalid") is null, "Invalid JSON should return null.");
    }

    private static void TestExpeditionAnalytics()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hudnopy_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string journalPath = Path.Combine(tempDir, "Journal.2026-11-21T120000.01.log");
            string[] lines =
            [
                """{"timestamp":"2026-11-21T12:00:00Z","event":"FSDJump","StarSystem":"Sol","JumpDist":12.5}""",
                """{"timestamp":"2026-11-21T12:05:00Z","event":"FSDJump","StarSystem":"Colonia","JumpDist":50.0}""",
                """{"timestamp":"2026-11-21T12:06:00Z","event":"Scan","BodyName":"Colonia 1","PlanetClass":"Earthlike body"}""",
                """{"timestamp":"2026-11-21T12:07:00Z","event":"ScanOrganic","ScanType":"Analyse","Species":"Stratum Tectonicas"}""",
                """{"timestamp":"2026-11-21T12:08:00Z","event":"MissionCompleted","Reward":500000}"""
            ];
            File.WriteAllLines(journalPath, lines);

            var snap = ExpeditionAnalyticsService.AnalyzeFolderAsync(tempDir, maxFiles: 10).GetAwaiter().GetResult();
            Assert(snap.JournalsScanned == 1, $"Expected 1 journal scanned, got {snap.JournalsScanned}");
            Assert(snap.TotalJumps == 2, $"Expected 2 jumps, got {snap.TotalJumps}");
            Assert(Math.Abs(snap.TotalDistanceLy - 62.5) < 0.01, $"Expected 62.5 LY, got {snap.TotalDistanceLy}");
            Assert(snap.EarthLikeWorlds == 1, $"Expected 1 ELW, got {snap.EarthLikeWorlds}");
            Assert(snap.OrganicsAnalysed == 1, $"Expected 1 organic, got {snap.OrganicsAnalysed}");
            Assert(snap.MissionsCompleted == 1 && snap.TotalBountiesAndSalesCredits == 500_000, "Unexpected mission/earnings totals.");

            string logbook = ExpeditionAnalyticsService.QueryLogbookAsync(tempDir, "system:Colonia").GetAwaiter().GetResult();
            Assert(logbook.Contains("Colonia", StringComparison.OrdinalIgnoreCase) && logbook.Contains("1 visita", StringComparison.OrdinalIgnoreCase),
                $"Unexpected logbook query output: {logbook}");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void TestWebAssetsIntegrity()
    {
        string webDir = Path.Combine(FindRepoRoot(), "Web");
        string htmlPath = Path.Combine(webDir, "index.html");
        string cssPath = Path.Combine(webDir, "styles.css");
        string jsPath = Path.Combine(webDir, "app.js");

        Assert(File.Exists(htmlPath), "Web/index.html missing.");
        Assert(File.Exists(cssPath), "Web/styles.css missing.");
        Assert(File.Exists(jsPath), "Web/app.js missing.");

        string html = File.ReadAllText(htmlPath);
        string css = File.ReadAllText(cssPath);
        string js = File.ReadAllText(jsPath);

        Assert(html.Contains("href=\"/styles.css\"", StringComparison.Ordinal), "index.html missing /styles.css link.");
        Assert(html.Contains("src=\"/app.js\"", StringComparison.Ordinal), "index.html missing /app.js script.");
        Assert(!html.Contains("<style>", StringComparison.OrdinalIgnoreCase), "index.html should not contain inline <style>.");
        Assert(css.Length > 5_000, "Web/styles.css appears unexpectedly small.");
        Assert(js.Length > 20_000, "Web/app.js appears unexpectedly small.");
        Assert(js.Contains("function escapeHtml(", StringComparison.Ordinal), "Web/app.js missing escapeHtml helper.");

        // Ensure every getElementById('...') in Web/app.js exists in Web/index.html so Web HUD never crashes before becoming visible
        var jsIds = Regex.Matches(js, @"getElementById\('([^']+)'\)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);
        foreach (string id in jsIds)
        {
            Assert(html.Contains($"id=\"{id}\"", StringComparison.Ordinal), $"Web/app.js references missing DOM id '{id}' not found in Web/index.html.");
        }
        // Verify every data-translation-key in Web/index.html exists in tableHeaders
        var thDict = TableHeaderTranslations.ForLanguage("en-GB");
        foreach (System.Text.RegularExpressions.Match tm in System.Text.RegularExpressions.Regex.Matches(html, @"data-translation-key=""([^""]+)"""))
        {
            string tk = tm.Groups[1].Value;
            Assert(thDict.ContainsKey(tk), $"Web/index.html references missing tableHeaders key: '{tk}'");
        }
    }

    private static void TestTelemetryWatcherSynthetic()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hudnopy_telemetry_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Status.json"),
                """{"timestamp":"2026-11-21T12:00:05Z","Flags":16777224,"Flags2":0,"Pips":[4,8,0],"Fuel":{"FuelMain":24.5,"FuelReservoir":0.4},"Cargo":12.0,"LegalState":"Clean"}""");
            File.WriteAllText(Path.Combine(tempDir, "Cargo.json"),
                """{"timestamp":"2026-11-21T12:00:05Z","Vessel":"Ship","Count":12,"Inventory":[{"Name":"gold","Name_Localised":"Gold","Count":12,"Stolen":0}]}""");
            File.WriteAllText(Path.Combine(tempDir, "Shipyard.json"),
                """{"timestamp":"2026-11-21T12:00:05Z","StationName":"Jameson Memorial","StarSystem":"Shinrarta Dezhra","PriceList":[{"ShipType":"anaconda","ShipType_Localised":"Anaconda","ShipPrice":146969450},{"ShipType":"asp","ShipType_Localised":"Asp Explorer","ShipPrice":6661153}]}""");

            string journalPath = Path.Combine(tempDir, "Journal.2026-11-21T120000.01.log");
            File.WriteAllLines(journalPath,
            [
                """{"timestamp":"2026-11-21T12:00:00Z","event":"LoadGame","Commander":"Test","Ship":"Anaconda","FuelLevel":24.5,"FuelCapacity":32.0,"Credits":10000000}""",
                """{"timestamp":"2026-11-21T12:00:01Z","event":"Location","StarSystem":"Shinrarta Dezhra","StarPos":[55.71875,17.59375,27.15625],"Docked":false}""",
                """{"timestamp":"2026-11-21T12:00:02Z","event":"DockingGranted","LandingPad":14,"StationName":"Jameson Memorial","StationType":"Orbis"}"""
            ]);

            var cfgSvc = new ConfigService();
            cfgSvc.Current.JournalFolderPath = tempDir;
            cfgSvc.Current.CaptureUnhandledEvents = false;
            var bindings = new BindingsService(cfgSvc);
            var inputSim = new InputSimulatorService(bindings, cfgSvc);
            var eventHub = new GameEventHub();
            using var watcher = new EliteFileWatcherService(cfgSvc, inputSim, eventHub);
            watcher.RefreshStateForTest();

            var st = watcher.State;
            Assert(st.CommanderName == "CMDR Test", $"Expected CMDR Test, got '{st.CommanderName}'.");
            Assert(st.CurrentSystem == "Shinrarta Dezhra", $"Expected Shinrarta Dezhra, got '{st.CurrentSystem}'.");
            Assert(st.AssignedLandingPad == 14, $"Expected AssignedLandingPad=14, got {st.AssignedLandingPad}.");
            Assert(st.CargoInventory.Any(c => c.Name.Equals("Gold", StringComparison.OrdinalIgnoreCase) && c.Count == 12),
                "Expected 12t Gold in CargoInventory.");
            Assert(st.Shipyard.Items.Count == 2, $"Expected 2 ships in Shipyard.Items, got {st.Shipyard.Items.Count}.");

            // Jumping to another system must clear stale AssignedLandingPad
            File.AppendAllText(journalPath,
                """{"timestamp":"2026-11-21T12:05:00Z","event":"FSDJump","StarSystem":"Sol","StarPos":[0,0,0],"JumpDist":64.2}""" + Environment.NewLine);
            watcher.RefreshStateForTest();
            Assert(watcher.State.CurrentSystem == "Sol", $"Expected Sol after FSDJump, got '{watcher.State.CurrentSystem}'.");
            Assert(watcher.State.AssignedLandingPad is null or 0, $"Expected AssignedLandingPad cleared after FSDJump, got '{watcher.State.AssignedLandingPad}'.");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void TestCovasTagSanitizer()
    {
        string dirty = "Hull is nominal. [[QUERY:MATERIAL:g5]] [ACTION:BIND|\"LandingGearToggle\"] Ready.";
        string clean = AiCopilotService.StripInternalTags(dirty);
        Assert(!clean.Contains("QUERY", StringComparison.OrdinalIgnoreCase), $"Residual QUERY tag leaked: '{clean}'");
        Assert(!clean.Contains("ACTION", StringComparison.OrdinalIgnoreCase), $"Residual ACTION tag leaked: '{clean}'");
        Assert(clean.Contains("Hull is nominal.", StringComparison.Ordinal) && clean.Contains("Ready.", StringComparison.Ordinal),
            $"Unexpected sanitized output: '{clean}'");

        // Multilingual silence & repeat voice commands
        Assert(AiCopilotService.IsSilenceVoiceCommand("COVAS silencio"), "Expected ES silence match.");
        Assert(AiCopilotService.IsSilenceVoiceCommand("Ruhe"), "Expected DE silence match.");
        Assert(AiCopilotService.IsSilenceVoiceCommand("Стоп"), "Expected RU silence match.");
        Assert(AiCopilotService.IsRepeatVoiceCommand("COVAS por favor repite eso", "COVAS"), "Expected ES repeat match with filler.");
        Assert(AiCopilotService.IsRepeatVoiceCommand("please say again", "COVAS"), "Expected EN repeat match with filler.");
        Assert(AiCopilotService.IsScreenVisionPrompt("COVAS mira mi pantalla", "COVAS"), "Expected ES vision prompt match.");
        Assert(AiCopilotService.IsScreenVisionPrompt("please look at my screen", "COVAS"), "Expected EN vision prompt match.");

        // Fuzzy macro distance & filler stripping
        Assert(AiCopilotService.NormalizeVoiceMatchText("COVAS por favor tren de aterrizaje", "COVAS") == "tren de aterrizaje",
            "Expected wake word and filler stripped.");
        Assert(AiCopilotService.ComputeLevenshteinDistance("tren de aterizaje", "tren de aterrizaje") == 1,
            "Expected Levenshtein distance 1 for minor STT slip.");

        // Synthesis readiness calculation
        var synthMats = new List<MaterialItem>
        {
            new() { Name = "iron", DisplayName = "Iron", Count = 20 },
            new() { Name = "nickel", DisplayName = "Nickel", Count = 10 },
            new() { Name = "vanadium", DisplayName = "Vanadium", Count = 4 },
            new() { Name = "germanium", DisplayName = "Germanium", Count = 2 }
        };
        string synth = AiCopilotService.BuildSynthesisReadinessReport(synthMats);
        Assert(synth.Contains("Básica ×2", StringComparison.Ordinal) && synth.Contains("O2: ×10", StringComparison.Ordinal) && synth.Contains("Drones (×4): ×1", StringComparison.Ordinal),
            $"Unexpected synthesis report: {synth}");

        // Frontier cAPI /journal summary parser
        string sampleJournal = "{\"event\":\"FSDJump\",\"StarSystem\":\"Sol\"}\n{\"event\":\"Scan\",\"BodyName\":\"Earth\"}\n{\"event\":\"MissionCompleted\",\"Reward\":250000}";
        var (jLines, jSum) = FrontierCapiService.SummarizeJournalLines(sampleJournal, new DateTime(2026, 11, 21));
        Assert(jLines == 3 && jSum.Contains("1 jumps", StringComparison.Ordinal) && jSum.Contains("1 scans", StringComparison.Ordinal) && jSum.Contains("250", StringComparison.Ordinal),
            $"Unexpected Frontier /journal summary: {jSum}");

        // EDDN schema sanitization: strip Taxi/Latitude on journal/1, preserve Latitude/Longitude on ApproachSettlement
        using var locDoc = JsonDocument.Parse("{\"event\":\"Location\",\"Taxi\":true,\"Latitude\":12.5,\"Longitude\":-45.1,\"StarSystem\":\"Sol\"}");
        var sanitizedLoc = CommunitySyncService.SanitizeElementToDictionary(locDoc.RootElement, preserveLatLon: false);
        Assert(!sanitizedLoc.ContainsKey("Taxi") && !sanitizedLoc.ContainsKey("Latitude") && sanitizedLoc.ContainsKey("StarSystem"),
            "Expected journal/1 sanitizer to strip Taxi and Latitude.");
        var sanitizedSettle = CommunitySyncService.SanitizeElementToDictionary(locDoc.RootElement, preserveLatLon: true);
        Assert(sanitizedSettle.ContainsKey("Latitude") && sanitizedSettle.ContainsKey("Longitude") && !sanitizedSettle.ContainsKey("Taxi"),
            "Expected ApproachSettlement sanitizer to preserve Latitude/Longitude while stripping Taxi.");
    }

    private static void TestBindingsAndLanguageCatalog()
    {
        // LanguageCatalog ISO-639 base & regional variant normalization
        Assert(LanguageCatalog.TryNormalize("en", out string en) && en == "en-GB", $"Expected en -> en-GB, got '{en}'");
        Assert(LanguageCatalog.TryNormalize("en-US", out string enUs) && enUs == "en-GB", $"Expected en-US -> en-GB, got '{enUs}'");
        Assert(LanguageCatalog.TryNormalize("es", out string es) && es == "es-ES", $"Expected es -> es-ES, got '{es}'");
        Assert(LanguageCatalog.TryNormalize("es-MX", out string esMx) && esMx == "es-ES", $"Expected es-MX -> es-ES, got '{esMx}'");
        Assert(LanguageCatalog.TryNormalize("pt", out string pt) && pt == "pt-BR", $"Expected pt -> pt-BR, got '{pt}'");
        Assert(LanguageCatalog.TryNormalize("pt-PT", out string ptPt) && ptPt == "pt-BR", $"Expected pt-PT -> pt-BR, got '{ptPt}'");
        Assert(!LanguageCatalog.TryNormalize("zz-ZZ", out _), "Unknown language code should return false.");

        // BindingsService XML parsing
        string tempDir = Path.Combine(Path.GetTempPath(), $"hudnopy_binds_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string bindsXml = """
                <?xml version="1.0" encoding="UTF-8" ?>
                <Root PresetName="HUDnopyTestPreset" MajorVersion="4" MinorVersion="0">
                  <LandingGearToggle>
                    <Primary Device="Keyboard" Key="Key_G">
                      <Modifier Device="Keyboard" Key="Key_LeftControl" />
                    </Primary>
                    <Secondary Device="{NoDevice}" Key="" />
                  </LandingGearToggle>
                </Root>
                """;
            File.WriteAllText(Path.Combine(tempDir, "Custom.4.0.binds"), bindsXml);
            var cfgSvc = new ConfigService();
            cfgSvc.Current.BindingsFolderPath = tempDir;
            cfgSvc.Current.SelectedBindsFile = "Custom.4.0.binds";

            var bindings = new BindingsService(cfgSvc);
            Assert(!bindings.UsingDefaultBindings, "Expected BindingsService to load synthetic .binds file.");
            Assert(bindings.ActivePresetName == "HUDnopyTestPreset", $"Expected preset HUDnopyTestPreset, got '{bindings.ActivePresetName}'.");
            var gear = bindings.Bindings.FirstOrDefault(b => b.ActionId == "LandingGearToggle");
            Assert(gear is not null && gear.PrimaryKey == "Key_G", $"Expected LandingGearToggle -> Key_G, got '{gear?.PrimaryKey}'.");
            Assert(gear!.PrimaryModifiers.Contains("Key_LeftControl"), "Expected Key_LeftControl modifier on LandingGearToggle.");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void TestDesktopWebSectionParity()
    {
        string repo = FindRepoRoot();
        string webHtml = File.ReadAllText(Path.Combine(repo, "Web", "index.html"));
        var webSections = Regex.Matches(webHtml, @"data-section-key=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert(webSections.Count >= 20, $"Expected at least 20 Web HUD sections, found {webSections.Count}.");

        var enSections = LoadLanguageSections("en-GB")["sections"];
        foreach (string secKey in webSections)
        {
            Assert(enSections.ContainsKey(secKey), $"Web HUD data-section-key='{secKey}' has no matching entry in en-GB.json -> sections.");
        }
    }

    private static void TestExternalIntegrationsV3()
    {
        // 1. All external APIs must have unified opt-in toggles defaulting to false
        var cfg = new AppConfig();
        Assert(!cfg.EnableFrontierCapi, "EnableFrontierCapi should default to false.");
        Assert(!cfg.EnableEdsmSync, "EnableEdsmSync should default to false.");
        Assert(!cfg.EnableInaraSync, "EnableInaraSync should default to false.");
        Assert(!cfg.EnableEliteBgsWatchlist, "EnableEliteBgsWatchlist should default to false.");
        Assert(!cfg.EnableArdentMarketApi, "EnableArdentMarketApi should default to false.");
        Assert(!cfg.EnableEdastroDssa, "EnableEdastroDssa should default to false.");
        Assert(!cfg.EnableCanonnUpload, "EnableCanonnUpload should default to false.");
        Assert(!cfg.EnableDiscord, "EnableDiscord should default to false.");

        // 2. EliteBGS Watchlist parser & conflict detection
        var names = EliteBgsWatchlistService.ParseWatchedFactionNames("Pilots Federation, Dark Wheel; Sirius Corp");
        Assert(names.Count == 3, $"Expected 3 parsed factions, got {names.Count}.");
        using var bgsDoc = JsonDocument.Parse("""
            {"docs":[{"name":"The Dark Wheel","faction_presence":[{"system_name":"Shinrarta Dezhra","influence":0.425,"state":"War","active_states":[{"state":"War"}],"pending_states":[]}]}]}
            """);
        var bgsItems = EliteBgsWatchlistService.ParseFactionResponse(bgsDoc.RootElement);
        Assert(bgsItems.Count == 1 && bgsItems[0].HasConflict && bgsItems[0].InfluencePercent == 42.5,
            "EliteBGS parser failed to parse faction presence and conflict flag.");

        // 3. Ardent Insight parser & sorting
        string ardentJson = """
            [
              {"systemName":"Sol","stationName":"Abraham Lincoln","distance":12.4,"sellPrice":280000,"demand":500,"maxLandingPadSize":"L"},
              {"systemName":"Lave","stationName":"Lave Station","distance":80.0,"sellPrice":310000,"demand":1200,"maxLandingPadSize":"L"}
            ]
            """;
        var offers = ArdentMarketService.ParseOffers("Platinum", "sell", ardentJson);
        Assert(offers.Count == 2 && offers[0].SystemName == "Lave" && offers[0].Price == 310000,
            "Ardent Market parser failed to sort sell offers by highest price.");

        // 4. EDAstro DSSA 3D Euclidean distance sorting
        var carriers = new[]
        {
            new DssaCarrierItem("X1A-001", "Far Carrier", "Beagle Point", "The Abyss", "Operational", 0, 1000, 0, 0),
            new DssaCarrierItem("X2B-002", "Near Carrier", "Colonia", "Inner Orion Spur", "Operational", 0, 3, 4, 0)
        };
        var sortedDssa = EdastroCanonnService.SortByDistance(carriers, 0, 0, 0, 5);
        Assert(sortedDssa.Count == 2 && sortedDssa[0].Callsign == "X2B-002" && Math.Abs(sortedDssa[0].DistanceLy - 5.0) < 0.01,
            "DSSA 3D Euclidean distance sorting failed.");

        // 5. Discord IPC binary framing & presence text
        byte[] frame = DiscordIntegrationService.BuildIpcFrame(1, "{\"ok\":true}");
        Assert(frame.Length == 8 + 11 && frame[0] == 1 && frame[4] == 11, "Discord IPC frame header encoding mismatch.");
        var (details, stateLine) = DiscordIntegrationService.BuildPresenceText(new ShipState
        {
            CurrentSystem = "Sol",
            StationName = "Galileo",
            ShipType = "Krait MkII",
            RawFlags = (uint)EliteStatusFlags.Docked
        });
        Assert(details.Contains("Galileo") && stateLine.Contains("Krait MkII"), "Discord presence text formatting mismatch.");

        // 6. Inara deep-link URL builder
        string inaraUrl = CommunitySyncService.BuildInaraDeepLink("system", "Shinrarta Dezhra");
        Assert(inaraUrl == "https://inara.cz/elite/starsystem/?search=Shinrarta%20Dezhra", $"Unexpected Inara URL: {inaraUrl}");
    }

    private static void TestExternalDataUnificationAndToggleResilience()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hudnopy_unify_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfg = new ConfigService();
            cfg.Current.JournalFolderPath = tempDir;
            var bindings = new BindingsService(cfg);
            var inputSim = new InputSimulatorService(bindings, cfg);
            var hub = new GameEventHub();

            // Write synthetic Journal with 1 local Earth-like body scan in system 123456
            string journalPath = Path.Combine(tempDir, "Journal.2026-11-22T230000.01.log");
            File.WriteAllLines(journalPath, new[]
            {
                """{"timestamp":"2026-11-22T23:00:01Z","event":"Location","StarSystem":"TestUnifySys","SystemAddress":123456}""",
                """{"timestamp":"2026-11-22T23:00:05Z","event":"Scan","StarSystem":"TestUnifySys","SystemAddress":123456,"BodyName":"TestUnifySys 1","BodyID":1,"PlanetClass":"Earth-like body","WasDiscovered":false,"WasMapped":false,"DistanceFromArrivalLS":420.0}"""
            });

            using var watcher = new EliteFileWatcherService(cfg, inputSim, hub);
            watcher.RefreshStateForTest();

            Assert(watcher.State.ExplorationBodies.Count == 1, $"Expected 1 local body, got {watcher.State.ExplorationBodies.Count}");
            Assert(watcher.State.ExplorationBodies[0].DataSource == "Local", "Local scan must start with DataSource='Local'");
            Assert(watcher.State.SystemScannedBodies == 1, "SystemScannedBodies should be 1");

            // Merge Spansh snapshot containing the same body (BodyID 1) + 1 unscanned body (BodyID 2)
            var spanshSnap = new SpanshSystemSnapshot
            {
                Id64 = 123456,
                Name = "TestUnifySys",
                BodyCount = 2,
                StationCount = 0,
                FetchedAtUtc = DateTimeOffset.UtcNow,
                Bodies = new List<SpanshBodySummary>
                {
                    new("TestUnifySys 1", "Earth-like world", 425.0, false, BodyId: 1, SurfaceGravity: 9.81, SurfaceTemperature: 288),
                    new("TestUnifySys 2", "High metal content world", 850.0, true, BodyId: 2, SurfaceGravity: 19.61, SurfaceTemperature: 310)
                }
            };
            watcher.SyncSpanshSystemBodies(spanshSnap);

            Assert(watcher.State.ExplorationBodies.Count == 2, $"Expected 2 unified bodies (no duplicates), got {watcher.State.ExplorationBodies.Count}");
            Assert(watcher.State.SystemScannedBodies == 1, $"Spansh-only bodies must NOT inflate SystemScannedBodies (got {watcher.State.SystemScannedBodies})");
            var body1 = watcher.State.ExplorationBodies.First(b => b.BodyName == "TestUnifySys 1");
            var body2 = watcher.State.ExplorationBodies.First(b => b.BodyName == "TestUnifySys 2");
            Assert(body1.DataSource == "Local+Spansh", $"Expected Local+Spansh on merged body, got {body1.DataSource}");
            Assert(body1.PlanetClass == "Earth-like body" && body1.WasDiscovered == false, "Local Journal fields must take precedence over Spansh");
            Assert(body1.SurfaceGravity is > 9.0, "Missing local fields (gravity) should be enriched from Spansh");
            Assert(body2.DataSource == "Spansh" && body2.IsSpanshOnly, "Unscanned Spansh body must be tagged Spansh");

            // Disable Spansh (SyncSpanshSystemBodies(null)): Spansh-only body purged, Local body preserved!
            watcher.SyncSpanshSystemBodies(null);
            Assert(watcher.State.ExplorationBodies.Count == 1, $"Expected 1 body after Spansh disable, got {watcher.State.ExplorationBodies.Count}");
            Assert(watcher.State.ExplorationBodies[0].DataSource == "Local", "Local body must revert to DataSource='Local' when Spansh disabled");

            // Community Goals merge: cAPI authoritative + Inara non-authoritative preserves PlayerContribution
            watcher.MergeCommunityGoals(new[]
            {
                new CommunityGoalItem { Id = 777, Title = "Protect Trade", StarSystem = "Sol", CurrentTotal = 50000, PlayerContribution = 1200 }
            }, isAuthoritative: true);
            watcher.MergeCommunityGoals(new[]
            {
                new CommunityGoalItem { Id = 777, Title = "Protect Trade", StarSystem = "Sol", StationName = "Galileo", CurrentTotal = 49000, PlayerContribution = 0 },
                new CommunityGoalItem { Id = 888, Title = "Extra Inara CG", StarSystem = "Lave", CurrentTotal = 10000, PlayerContribution = 0 }
            }, isAuthoritative: false);

            Assert(watcher.State.CommunityGoals.Count == 2, $"Expected 2 merged CGs, got {watcher.State.CommunityGoals.Count}");
            var cg777 = watcher.State.CommunityGoals.First(g => g.Id == 777);
            Assert(cg777.PlayerContribution == 1200, $"Inara must never overwrite cAPI PlayerContribution (got {cg777.PlayerContribution})");
            Assert(cg777.StationName == "Galileo", "Inara should fill missing StationName metadata");

            // EDSM bodies fallback when Spansh is off: adds missing body 3 without duplicating body 1
            watcher.SyncEdsmSystemBodies("TestUnifySys", "123456", new[]
            {
                new SpanshBodySummary("TestUnifySys 1", "Earth-like world", 420.0, false, BodyId: 1),
                new SpanshBodySummary("TestUnifySys 3", "Water world", 1200.0, false, BodyId: 3)
            });
            Assert(watcher.State.ExplorationBodies.Count == 2, $"Expected 2 bodies (Local + EDSM fallback), got {watcher.State.ExplorationBodies.Count}");
            Assert(watcher.State.ExplorationBodies.Any(b => b.BodyName == "TestUnifySys 3" && b.DataSource == "EDSM"), "EDSM fallback body must be tagged EDSM");

            // Watched BGS faction marker & Inara profile merge + centralized toggle-off purge
            watcher.ApplyCapiEnrichment(st =>
            {
                st.SystemFactions = new List<SystemFactionItem> { new() { Name = "The Dark Wheel", InfluencePercent = 25.0, DataSource = "EDSM" } };
                st.WatchedBgsFactions = new List<EliteBgsFactionWatchItem> { new() { FactionName = "The Dark Wheel", SystemName = "TestUnifySys" } };
                st.InaraProfile = new InaraCommanderProfile { CommanderName = "TestCMDR", SquadronName = "TestWing", SquadronRank = "Leader", PreferredAllegiance = "Independent" };
                st.EdsmDiscoveryIntel = new EdsmSystemDiscoveryIntel { SystemName = "TestUnifySys", IsKnownInEdsm = true };
            });
            Assert(watcher.State.SystemFactions[0].IsWatched && watcher.State.SystemFactions[0].DisplayName.StartsWith("★"), "Watched faction must show ★ marker");
            Assert(watcher.State.CommanderProfile.Any(p => p.Category == "Inara" && p.Value.Contains("TestWing")), "Inara profile must merge into CommanderProfile");

            // Turning OFF EDSM, EliteBGS, and Inara purges their exclusive data while keeping Local body intact
            cfg.Current.EnableEdsmSync = false;
            cfg.Current.EnableEliteBgsWatchlist = false;
            cfg.Current.EnableInaraSync = false;
            cfg.Save();
            Assert(watcher.State.EdsmDiscoveryIntel is null, "EDSM intel must be purged when EDSM disabled");
            Assert(watcher.State.WatchedBgsFactions.Count == 0, "Watched BGS factions must be purged when EliteBGS disabled");
            Assert(watcher.State.InaraProfile is null && !watcher.State.CommanderProfile.Any(p => p.Category == "Inara"), "Inara rows must be purged when Inara disabled");
            Assert(watcher.State.ExplorationBodies.Count == 1 && watcher.State.ExplorationBodies[0].DataSource == "Local", "Only Local body must remain after disabling EDSM & Spansh");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static void TestCrashSanitizationAndWpfLayoutSmoke()
    {
        // 1. Verify crash report sanitization and Support ZIP bundling
        var fakeEx = new InvalidOperationException(@"Failed at C:\Users\alons\secret.txt with api_key=SUPERSECRET123 and Bearer tok_xyz999");
        var cfg = new AppConfig();
        string report = AppDiagnosticsLogger.BuildSanitizedCrashReport(fakeEx, "Test.Source", cfg);
        Assert(!report.Contains("alons", StringComparison.OrdinalIgnoreCase), $"Username leaked in crash report: {report}");
        Assert(!report.Contains("SUPERSECRET123", StringComparison.Ordinal), "API key leaked in crash report.");
        Assert(!report.Contains("tok_xyz999", StringComparison.Ordinal), "Bearer token leaked in crash report.");
        Assert(report.Contains("[USER]", StringComparison.Ordinal) && report.Contains("[REMOVED]", StringComparison.Ordinal), "Redaction markers missing.");

        string issueUrl = AppDiagnosticsLogger.BuildGitHubIssueUrl(fakeEx, "Test.Source", cfg);
        Assert(issueUrl.StartsWith("https://github.com/AlonsoLP/HUDnopy/issues/new?", StringComparison.Ordinal), "Invalid GitHub Issue URL.");
        Assert(!issueUrl.Contains("SUPERSECRET123", StringComparison.Ordinal), "Secret leaked in GitHub Issue URL.");

        string tempZip = Path.Combine(Path.GetTempPath(), $"hudnopy_crash_test_{Guid.NewGuid():N}.zip");
        try
        {
            AppDiagnosticsLogger.ExportSupportBundle(cfg, tempZip, crashReportText: report);
            Assert(File.Exists(tempZip), "Support ZIP was not created.");
            using var zip = System.IO.Compression.ZipFile.OpenRead(tempZip);
            var crashEntry = zip.GetEntry("crash-report.txt");
            Assert(crashEntry is not null, "crash-report.txt missing from Support ZIP.");
            using var sr = new StreamReader(crashEntry!.Open());
            string zipCrashText = sr.ReadToEnd();
            Assert(!zipCrashText.Contains("SUPERSECRET123", StringComparison.Ordinal) && !zipCrashText.Contains("alons", StringComparison.OrdinalIgnoreCase),
                "Personal data leaked inside crash-report.txt in Support ZIP.");
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }

        // 2. Real WPF STA layout smoke-test: instantiate MainWindow and run Measure/Arrange
        Exception? wpfError = null;
        var staThread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null)
                {
                    var app = new HUDnopy.App();
                    app.InitializeComponent();
                }
                var services = new ApplicationServices();
                var win = new HUDnopy.MainWindow(services);
                win.Measure(new System.Windows.Size(1280, 720));
                win.Arrange(new System.Windows.Rect(0, 0, 1280, 720));
                win.UpdateLayout();
                win.Close();
            }
            catch (Exception ex)
            {
                wpfError = ex;
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        Assert(staThread.Join(TimeSpan.FromSeconds(15)), "WPF STA smoke-test timed out.");
        if (wpfError is not null)
            throw new InvalidOperationException($"WPF MainWindow layout smoke-test failed: {wpfError.GetType().Name}: {wpfError.Message}\n{wpfError.StackTrace}", wpfError);
    }

    private static void TestVisualImmersionV050()
    {
        // 1. Block A (#19): GraphicsConfigurationOverride.xml & EDHM JSON theme parsing
        const string identityXml = """
            <GraphicsConfig>
              <GUIColour>
                <Default>
                  <LocalisationName>Standard</LocalisationName>
                  <MatrixRed>1, 0, 0</MatrixRed>
                  <MatrixGreen>0, 1, 0</MatrixGreen>
                  <MatrixBlue>0, 0, 1</MatrixBlue>
                </Default>
              </GUIColour>
            </GraphicsConfig>
            """;
        Assert(GameHudThemeImportService.TryParseGraphicsOverrideXml(identityXml, out string p1, out string s1),
            "Failed to parse identity GraphicsConfigurationOverride.xml.");
        Assert(string.Equals(p1, "#FF7100", StringComparison.OrdinalIgnoreCase), $"Expected #FF7100, got {p1}");
        Assert(string.Equals(s1, "#FFB300", StringComparison.OrdinalIgnoreCase), $"Expected #FFB300, got {s1}");

        const string cyanXml = """
            <GraphicsConfig>
              <GUIColour>
                <Default>
                  <MatrixRed>0, 0.5, 1</MatrixRed>
                  <MatrixGreen>0, 1, 0.5</MatrixGreen>
                  <MatrixBlue>1, 0, 0</MatrixBlue>
                </Default>
              </GUIColour>
            </GraphicsConfig>
            """;
        Assert(GameHudThemeImportService.TryParseGraphicsOverrideXml(cyanXml, out string p2, out string s2),
            "Failed to parse cyan GraphicsConfigurationOverride.xml.");
        Assert(!string.Equals(p2, "#FF7100", StringComparison.OrdinalIgnoreCase) && p2.StartsWith('#') && p2.Length == 7,
            $"Unexpected shifted primary HEX: {p2}");
        Assert(s2.StartsWith('#') && s2.Length == 7, $"Unexpected shifted secondary HEX: {s2}");

        const string edhmJson = """{"theme_name":"TestEDHM","ui_accent":"#00E5FF","secondary_color":"#FF4081"}""";
        Assert(GameHudThemeImportService.TryParseEdhmThemeJson(edhmJson, out string ep, out string es),
            "Failed to parse EDHM JSON theme.");
        Assert(ep == "#00E5FF" && es == "#FF4081", $"Unexpected EDHM result: {ep}/{es}");

        // 2. Block B (#7): Heading-up Exobiology 2D projection
        var samples = new List<(double Lat, double Lon)> { (0.001, 0.0) }; // Due North of player at (0,0)
        var projectedNorth = ExobiologyTrackerState.ProjectSamplePoints(samples, 0.0, 0.0, 0.0, 1_000_000.0);
        Assert(projectedNorth.Count == 1, "Expected 1 projected sample point.");
        Assert(projectedNorth[0].RelativeY > 10 && Math.Abs(projectedNorth[0].RelativeX) < 1.0,
            $"Heading 0° North sample should be ahead: Y={projectedNorth[0].RelativeY}, X={projectedNorth[0].RelativeX}");

        var projectedEast = ExobiologyTrackerState.ProjectSamplePoints(samples, 0.0, 0.0, 90.0, 1_000_000.0);
        Assert(projectedEast[0].RelativeX < -10 && Math.Abs(projectedEast[0].RelativeY) < 1.0,
            $"Heading 90° East should place North sample to the left: Y={projectedEast[0].RelativeY}, X={projectedEast[0].RelativeX}");

        // 3. Block C (#1): 2D System Map orbital tree (Exphiay-like binary pairs, orbiting Y brown dwarf, moons & L-indented sub-moons)
        var bodies = new List<ExplorationBodyItem>
        {
            new() { BodyId = 0, BodyName = "Exphiay", SystemName = "Exphiay", StarType = "G", HierarchyDepth = 0, DataSource = "Local", Rings = new List<ExplorationRingItem> { new() { Name = "Exphiay A Belt" } } },
            new() { BodyId = 3, BodyName = "Exphiay 3", SystemName = "Exphiay", PlanetClass = "High metal content body", HierarchyDepth = 1, DirectParentType = "Star", DirectParentBodyId = 0, HostStarBodyId = 0, DataSource = "Local" },
            new() { BodyId = 4, BodyName = "Exphiay 4", SystemName = "Exphiay", PlanetClass = "High metal content body", HierarchyDepth = 2, DirectParentType = "Null", DirectParentBodyId = 100, BarycentreId = 100, HostStarBodyId = 0, DataSource = "Local" },
            new() { BodyId = 5, BodyName = "Exphiay 5", SystemName = "Exphiay", PlanetClass = "Earthlike body", HierarchyDepth = 2, DirectParentType = "Null", DirectParentBodyId = 100, BarycentreId = 100, HostStarBodyId = 0, BiologicalSignalCount = 4, DataSource = "Local" },
            new() { BodyId = 7, BodyName = "Exphiay 7", SystemName = "Exphiay", StarType = "Y", HierarchyDepth = 1, DirectParentType = "Star", DirectParentBodyId = 0, HostStarBodyId = 0, DataSource = "Local" },
            new() { BodyId = 8, BodyName = "Exphiay 7 e", SystemName = "Exphiay", PlanetClass = "Rocky body", HierarchyDepth = 2, DirectParentType = "Star", DirectParentBodyId = 7, HostStarBodyId = 7, SurfaceGravity = 19.62, Landable = true, DataSource = "Spansh" },
            new() { BodyId = 9, BodyName = "Exphiay 7 e a", SystemName = "Exphiay", PlanetClass = "Icy body", HierarchyDepth = 3, DirectParentType = "Planet", DirectParentBodyId = 8, ParentPlanetBodyId = 8, HostStarBodyId = 7, Landable = true, DataSource = "Local" },
            new() { BodyId = 10, BodyName = "Exphiay 8", SystemName = "Exphiay", PlanetClass = "High metal content body", HierarchyDepth = 1, DirectParentType = "Star", DirectParentBodyId = 0, HostStarBodyId = 0, DataSource = "Local" }
        };
        var layout = SystemMapLayoutBuilder.Build(bodies, "Exphiay");
        var nodes = layout.Nodes;
        Assert(nodes.Count == 8, $"Expected 8 map nodes, got {nodes.Count}");
        Assert(layout.Brackets.Count == 1, $"Expected 1 binary pair bracket (4-5), got {layout.Brackets.Count}");
        var starG = nodes.First(n => n.Body.BodyName == "Exphiay");
        var p3 = nodes.First(n => n.Body.BodyName == "Exphiay 3");
        var p4 = nodes.First(n => n.Body.BodyName == "Exphiay 4");
        var p5 = nodes.First(n => n.Body.BodyName == "Exphiay 5");
        var starY = nodes.First(n => n.Body.BodyName == "Exphiay 7");
        var m7e = nodes.First(n => n.Body.BodyName == "Exphiay 7 e");
        var sm7ea = nodes.First(n => n.Body.BodyName == "Exphiay 7 e a");
        var p8 = nodes.First(n => n.Body.BodyName == "Exphiay 8");
        Assert(starG.IsRootStar && !starG.HasRings, "Root Star G must be root star and never render asteroid belts as rings.");
        Assert(Math.Abs(p4.Y - starG.Y) < 0.1 && Math.Abs(p5.Y - starG.Y) < 0.1 && p4.X > p3.X && p5.X > p4.X,
            "Binary planets 4 and 5 must stay on main horizontal axis (NOT stacked vertically under 3).");
        Assert(starY.IsStar && !starY.IsRootStar && Math.Abs(starY.Y - starG.Y) < 0.1 && starY.X > p5.X,
            "Orbiting Y-class brown dwarf Exphiay 7 must stay on main horizontal axis.");
        Assert(Math.Abs(m7e.X - starY.X) < 0.1 && m7e.Y > starY.Y && m7e.IsHighGravity && m7e.IsSpanshOnly,
            "Moon 7 e must hang vertically below Exphiay 7.");
        Assert(sm7ea.X > m7e.X && sm7ea.Y > m7e.Y && sm7ea.IndentLevel == 2,
            "Sub-moon 7 e a must be indented in L-shape to the right of moon 7 e.");
        Assert(Math.Abs(p8.Y - starG.Y) < 0.1 && p8.X > sm7ea.X,
            "Independent planet 8 must sit on main horizontal axis to the right of Exphiay 7's sub-moon column.");

        // 4. Block D (#6): Update semver comparison & opt-in default
        Assert(UpdateCheckService.IsNewerVersion("0.4.0", "v0.5.0"), "Expected v0.5.0 > 0.4.0");
        Assert(!UpdateCheckService.IsNewerVersion("0.5.0", "v0.5.0"), "Expected v0.5.0 == 0.5.0");
        Assert(!UpdateCheckService.IsNewerVersion("0.5.0", "v0.4.9"), "Expected v0.4.9 < 0.5.0");
        Assert(!new AppConfig().EnableAutoUpdateCheck, "EnableAutoUpdateCheck MUST default to false (strict opt-in).");
    }

    private static void TestV050OverlayPwaDeckWhisperRadio()
    {
        // 1. PWA assets integrity (manifest.json, sw.js, icon.svg)
        string webRoot = Path.Combine(FindRepoRoot(), "Web");
        string manifestPath = Path.Combine(webRoot, "manifest.json");
        string swPath = Path.Combine(webRoot, "sw.js");
        string iconPath = Path.Combine(webRoot, "icon.svg");
        Assert(File.Exists(manifestPath) && File.Exists(swPath) && File.Exists(iconPath),
            "PWA assets (manifest.json, sw.js, icon.svg) must exist in Web/.");
        using (var doc = JsonDocument.Parse(File.ReadAllText(manifestPath)))
        {
            Assert(doc.RootElement.GetProperty("display").GetString() == "standalone", "PWA manifest must set display=standalone.");
            Assert(doc.RootElement.GetProperty("icons").GetArrayLength() >= 1, "PWA manifest must define at least one icon.");
        }
        string swText = File.ReadAllText(swPath);
        Assert(swText.Contains("/ws", StringComparison.Ordinal) && swText.Contains("/api/", StringComparison.Ordinal),
            "PWA service worker must bypass live /ws and /api/ endpoints.");

        // 2. AppConfig v0.6.0 defaults & CustomDeckButtonItem round-trip
        var cfg = new AppConfig();
        Assert(cfg.OverlayClickThrough && cfg.OverlayOpacity is > 0.25 and <= 1.0, "Invalid default overlay config.");
        Assert(cfg.AiSttEngine == "auto" && cfg.WhisperModelId == "tiny", "Whisper STT must default to opt-in 'auto' mode.");
        Assert(cfg.RadioAutoDuckEnabled && cfg.RadioVolumePercent is >= 0 and <= 100, "Invalid default radio config.");
        cfg.CustomDeckButtons.Add(new CustomDeckButtonItem { Label = "Landing Gear", Icon = "🛬", ActionId = "LandingGearToggle", Order = 1, IsVisible = true });
        cfg.CustomDeckButtons.Add(new CustomDeckButtonItem { Label = "Cargo Scoop", Icon = "📦", ActionId = "ToggleCargoScoop", Order = 0, IsVisible = true });
        string cfgJson = JsonSerializer.Serialize(cfg, AppConfigJsonContext.Default.AppConfig);
        var roundTrip = JsonSerializer.Deserialize(cfgJson, AppConfigJsonContext.Default.AppConfig);
        Assert(roundTrip is not null && roundTrip.CustomDeckButtons.Count == 2, "CustomDeckButtons failed JSON round-trip.");
        Assert(roundTrip!.CustomDeckButtons.OrderBy(b => b.Order).First().ActionId == "ToggleCargoScoop", "CustomDeckButtons ordering failed.");

        // 2b. DeckLayoutService: ★ FAVORITES tab first, 0.5x-4.0x scale clamping, live active state, auto-context & JSON export/import
        var sampleTabs = new List<DeckTabDefinition>
        {
            new()
            {
                Id = "flight", Name = "Vuelo", Icon = "🚀", AutoContext = "landing", Order = 0, IsVisible = true,
                Buttons = new List<CustomDeckButtonItem>
                {
                    new() { Id = "b1", Label = "Tren", Icon = "🛬", ActionId = "LandingGearToggle", IsFavorite = true, RequireConfirmation = false, SizeScale = 2.5, Order = 0 },
                    new() { Id = "b2", Label = "Eject", Icon = "💣", ActionId = "EjectAllCargo", IsFavorite = false, RequireConfirmation = true, SizeScale = 9.0, Order = 1 }
                }
            }
        };
        string exportedDeck = DeckLayoutService.ExportTabsJson(sampleTabs);
        Assert(DeckLayoutService.TryImportTabsJson(exportedDeck, out var importedTabs, out string importErr), $"Deck import failed: {importErr}");
        Assert(importedTabs.Count == 1 && Math.Abs(importedTabs[0].Buttons[1].SizeScale - 4.0) < 0.01, "SizeScale must clamp to max 4.0x on import.");
        var fakeShip = new ShipState { RawFlags = (uint)EliteStatusFlags.LandingGearDown };
        Assert(DeckLayoutService.EvaluateLiveActiveState("LandingGearToggle", fakeShip), "Expected LandingGearToggle active when LandingGearDown.");
        var cfgDeck = new AppConfig { UseCustomDeckLayout = true, DeckTabs = importedTabs, InterfaceLanguage = "es-ES" };
        var bindingsSvc = new BindingsService(new ConfigService());
        var resolved = DeckLayoutService.BuildResolvedTabs(cfgDeck, bindingsSvc, fakeShip, "es-ES", hideUnassigned: false);
        Assert(resolved.Count == 2 && resolved[0].IsFavoritesTab && resolved[0].Name == "FAVORITOS",
            $"Expected ★ FAVORITOS tab first, got {resolved.FirstOrDefault()?.Name}");
        Assert(DeckLayoutService.ResolveAutoContextTabId(resolved, fakeShip) == "flight", "Auto-context landing tab resolution failed.");

        // 3. Offline Whisper STT helpers (zero network calls)
        Assert(WhisperSttService.NormalizeWhisperLanguage("es-ES") == "es", "Expected 'es' for es-ES.");
        Assert(WhisperSttService.NormalizeWhisperLanguage("en-GB") == "en", "Expected 'en' for en-GB.");
        Assert(WhisperSttService.SanitizeTranscript(" [BLANK_AUDIO]  Deploy landing gear (music) \n ") == "Deploy landing gear",
            "Whisper transcript sanitizer failed to strip bracketed noise.");
        Assert(WhisperSttService.Catalog.Count >= 2, "Expected tiny and base Whisper models in catalog.");

        // 4. Cockpit Radio stations, URL validation & COVAS auto-ducking math
        Assert(CockpitRadioService.BuiltInStations.Count >= 3, "Expected >= 3 built-in Elite Dangerous radio stations.");
        Assert(CockpitRadioService.IsValidStreamUrl("https://stream.laveradio.com/laveradio.mp3"), "Valid HTTPS stream rejected.");
        Assert(!CockpitRadioService.IsValidStreamUrl("file:///C:/secret.mp3"), "Non-HTTP(S) radio stream URL must be rejected.");
        float normalGain = CockpitRadioService.ComputeEffectiveGain(80, isDucked: false);
        float duckedGain = CockpitRadioService.ComputeEffectiveGain(80, isDucked: true);
        Assert(Math.Abs(normalGain - 0.8f) < 0.001f && Math.Abs(duckedGain - 0.2f) < 0.001f,
            $"Auto-ducking gain mismatch: normal={normalGain}, ducked={duckedGain}");
    }
}
