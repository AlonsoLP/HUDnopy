// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/NavigationTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized labels, tooltips, and accessibility names for top-level navigation.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HUDnopy.Localization;

/// <summary>
/// Localized labels, tooltips, and accessibility names for top-level navigation.
/// </summary>
internal sealed record NavigationText(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("accessibleName")] string AccessibleName,
    [property: JsonPropertyName("tooltip")] string Tooltip);

internal static class NavigationTranslations
{
    private static readonly string[] Keys = { "dashboard", "flight", "navigation", "operations", "activity", "controls", "settings" };
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, NavigationText>> Catalog = Load();

    public static IReadOnlyDictionary<string, NavigationText> ForLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (!Catalog.TryGetValue(language, out var navigation))
            throw new ArgumentException($"Idioma de navegación no compatible: {language}", nameof(language));
        return navigation;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, NavigationText>> Load()
    {
        var catalog = new Dictionary<string, IReadOnlyDictionary<string, NavigationText>>(StringComparer.OrdinalIgnoreCase);
        foreach (string language in LanguageCatalog.Languages)
        {
            var navigation = LanguageCatalog.GetSection(language, "navigation")
                .Deserialize<Dictionary<string, NavigationText>>();
            if (navigation is null || navigation.Count != Keys.Length || Keys.Any(key => !navigation.ContainsKey(key)) ||
                navigation.Values.Any(item => item is null || string.IsNullOrWhiteSpace(item.Label) ||
                    string.IsNullOrWhiteSpace(item.AccessibleName) || string.IsNullOrWhiteSpace(item.Tooltip)))
                throw new InvalidDataException($"El catálogo de navegación está incompleto para {language}.");
            catalog.Add(language, new ReadOnlyDictionary<string, NavigationText>(navigation));
        }
        return catalog;
    }
}
