// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/TableHeaderTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized DataGrid column headers validated across all supported languages.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HUDnopy.Localization;

/// <summary>
/// Localized DataGrid column headers validated across all supported languages.
/// </summary>
internal static class TableHeaderTranslations
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog = Load();

    public static IReadOnlyDictionary<string, string> ForLanguage(string language)
    {
        if (!Catalog.TryGetValue(language, out var text))
            throw new ArgumentException($"Idioma de tablas no compatible: {language}", nameof(language));
        return text;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var catalog = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? expected = null;
        foreach (string language in LanguageCatalog.Languages)
        {
            var text = LanguageCatalog.GetSection(language, "tableHeaders").Deserialize<Dictionary<string, string>>();
            if (text is null || text.Count == 0 || text.Any(entry => string.IsNullOrWhiteSpace(entry.Value)))
                throw new InvalidDataException($"El catálogo de tablas está incompleto para {language}.");
            expected ??= new HashSet<string>(text.Keys, StringComparer.Ordinal);
            if (!expected.SetEquals(text.Keys))
                throw new InvalidDataException($"Las claves de tablas no coinciden para {language}.");
            catalog.Add(language, new ReadOnlyDictionary<string, string>(text));
        }
        return catalog;
    }
}
