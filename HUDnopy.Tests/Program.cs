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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using HUDnopy.Localization;
using HUDnopy.Models;
using HUDnopy.Services;

namespace HUDnopy.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly Regex FormatPlaceholderRegex = new(@"\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}", RegexOptions.Compiled);

    private static int Main()
    {
        Console.WriteLine("=== HUDnopy Automated Test Suite ===");

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

        Console.WriteLine();
        Console.WriteLine($"Results: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void RunTest(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"[FAIL] {name}: {ex.Message}");
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
        string dirty = "Hull is nominal. [[QUERY:MATERIAL:g5]] [[ACTION:BIND|LandingGearToggle]] Ready.";
        string clean = AiCopilotService.StripInternalTags(dirty);
        Assert(!clean.Contains("[[QUERY", StringComparison.OrdinalIgnoreCase), $"Residual QUERY tag leaked: '{clean}'");
        Assert(!clean.Contains("[[ACTION", StringComparison.OrdinalIgnoreCase), $"Residual ACTION tag leaked: '{clean}'");
        Assert(clean.Contains("Hull is nominal.", StringComparison.Ordinal) && clean.Contains("Ready.", StringComparison.Ordinal),
            $"Unexpected sanitized output: '{clean}'");
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
}
