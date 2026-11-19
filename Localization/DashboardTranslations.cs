// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/DashboardTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized strings for the main dashboard and session summary cards.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HUDnopy.Localization;

/// <summary>
/// Localized strings for the main dashboard and session summary cards.
/// </summary>
internal static class DashboardTranslations
{
    private static readonly string[] Keys =
    {
        "missionSummary", "headerModules", "headerSaveError", "currentSystem", "credits", "commanderAndShip", "currentLocation",
        "flightResources", "visualAlerts", "nextRoute", "recentActivity", "noAlerts",
        "noNextJump", "waitingEvents", "unknownCommander", "noSignal", "online", "inactive",
        "destination", "station", "body", "deepSpace", "hull", "fuel", "cargo",
        "capacityUnconfirmed", "lightYears", "remainingJumps", "profile",
        "sessionStatsTitle", "sessionReset", "sessionStatsSummary"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog = Load();

    public static IReadOnlyDictionary<string, string> ForLanguage(string language)
    {
        if (!Catalog.TryGetValue(language, out var text))
            throw new ArgumentException($"Idioma de panel no compatible: {language}", nameof(language));
        return text;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var catalog = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string language in LanguageCatalog.Languages)
        {
            var text = LanguageCatalog.GetSection(language, "dashboard").Deserialize<Dictionary<string, string>>();
            if (text is null || text.Count != Keys.Length || Keys.Any(key => !text.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)))
                throw new InvalidDataException($"El catálogo del panel está incompleto para {language}.");
            catalog.Add(language, new ReadOnlyDictionary<string, string>(text));
        }
        return catalog;
    }
}
