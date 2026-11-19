// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/AlertTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized names and descriptions for configurable alert definitions.
// -----------------------------------------------------------------------------

using HUDnopy.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HUDnopy.Localization;

/// <summary>
/// Localized names and descriptions for configurable alert definitions.
/// </summary>
internal static class AlertTranslations
{
    private static readonly string[] AlertIds =
    {
        "fuel-low", "fuel-critical", "overheating", "hull-critical", "shields-down",
        "interdiction", "danger", "cockpit-breach", "jump-arrival", "docking",
        "mission-activity", "mission-expiry", "bounty-reward", "crime-committed",
        "cargo-activity", "carrier-activity", "exploration-activity", "copilot-safety"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<AlertDefinition>> Catalog = Load();
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> NameByIdCatalog =
        Catalog.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, string>)pair.Value.ToDictionary(def => def.Id, def => def.Name, StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
    private sealed record AlertText(string Name, string Description);

    public static string GetName(string alertId, string language) =>
        NameByIdCatalog.TryGetValue(language, out var names) && names.TryGetValue(alertId, out string? name)
            ? name
            : ForLanguage(language).First(definition => definition.Id == alertId).Name;

    public static IReadOnlyList<AlertDefinition> ForLanguage(string language)
    {
        if (language is null)
            throw new ArgumentNullException(nameof(language));
        if (!Catalog.TryGetValue(language, out var definitions))
            throw new ArgumentException($"Idioma de alertas no compatible: {language}", nameof(language));
        return definitions;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<AlertDefinition>> Load()
    {
        var catalog = new Dictionary<string, IReadOnlyList<AlertDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (string language in LanguageCatalog.Languages)
        {
            var entries = LanguageCatalog.GetSection(language, "alerts")
                .Deserialize<Dictionary<string, AlertText>>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (entries is null || entries.Count != AlertIds.Length ||
                AlertIds.Any(id => !entries.ContainsKey(id)))
                throw new InvalidDataException($"El catálogo de alertas está incompleto para {language}.");

            var definitions = new List<AlertDefinition>(AlertIds.Length);
            foreach (string id in AlertIds)
            {
                var text = entries[id];
                if (text is null || string.IsNullOrWhiteSpace(text.Name) || string.IsNullOrWhiteSpace(text.Description))
                    throw new InvalidDataException($"La traducción de {id} está incompleta para {language}.");
                definitions.Add(new AlertDefinition(id, text.Name, text.Description));
            }
            catalog.Add(language, definitions.AsReadOnly());
        }
        return catalog;
    }
}
