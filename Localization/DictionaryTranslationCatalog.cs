// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/DictionaryTranslationCatalog.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Base loader and parity validator for JSON-backed translation dictionaries.
// -----------------------------------------------------------------------------

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HUDnopy.Localization;

/// <summary>
/// Base loader and parity validator for JSON-backed translation dictionaries.
/// </summary>
internal static partial class DictionaryTranslationCatalog
{
    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderRegex();

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load(string section)
    {
        var catalog = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? source = null;
        HashSet<string>? sourceKeys = null;
        foreach (string language in LanguageCatalog.Languages)
        {
            var text = LanguageCatalog.GetSection(language, section).Deserialize<Dictionary<string, string>>();
            if (text is null || text.Count == 0 || text.Any(pair => string.IsNullOrWhiteSpace(pair.Value)))
                throw new InvalidDataException($"El catálogo {section} está incompleto para {language}.");
            if (source is null)
            {
                source = text;
                sourceKeys = source.Keys.ToHashSet(StringComparer.Ordinal);
            }
            else if (!sourceKeys!.SetEquals(text.Keys))
            {
                throw new InvalidDataException($"Las claves de {section} no coinciden para {language}.");
            }
            foreach (var (key, value) in text)
            {
                string sourceValue = source[key];
                if (!sourceValue.Contains('{') && !value.Contains('{') && !value.Contains('}'))
                    continue;

                var expected = PlaceholderRegex().Matches(sourceValue).Select(m => m.Value).Order();
                var actual = PlaceholderRegex().Matches(value).Select(m => m.Value).Order();
                if (!expected.SequenceEqual(actual))
                    throw new InvalidDataException($"Los parámetros de {section} no coinciden: {language}/{key}.");
                _ = string.Format(CultureInfo.InvariantCulture, value, 0, 1, 2, 3, 4);
            }
            catalog.Add(language, new ReadOnlyDictionary<string, string>(text));
        }
        return catalog;
    }
}
