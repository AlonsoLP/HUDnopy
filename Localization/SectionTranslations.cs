// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/SectionTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized titles for collapsible UI sections across desktop and web views.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HUDnopy.Localization;

/// <summary>
/// Localized titles for collapsible UI sections across desktop and web views.
/// </summary>
internal static class SectionTranslations
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog = Load();

    public static IReadOnlyDictionary<string, string> ForLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (!Catalog.TryGetValue(language, out var sections))
            throw new ArgumentException($"Idioma de secciones no compatible: {language}", nameof(language));
        return sections;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var catalog = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? expected = null;
        foreach (string language in LanguageCatalog.Languages)
        {
            var sections = LanguageCatalog.GetSection(language, "sections").Deserialize<Dictionary<string, string>>();
            if (sections is null || sections.Count == 0 || sections.Any(item => string.IsNullOrWhiteSpace(item.Value)))
                throw new InvalidDataException($"El catálogo de secciones está incompleto para {language}.");
            expected ??= new HashSet<string>(sections.Keys, StringComparer.Ordinal);
            if (!expected.SetEquals(sections.Keys))
                throw new InvalidDataException($"Las claves de secciones no coinciden para {language}.");
            catalog.Add(language, new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(sections));
        }
        return catalog;
    }
}
