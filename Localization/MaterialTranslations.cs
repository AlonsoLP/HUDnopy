// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/MaterialTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized category, rarity, and status labels for engineering materials.
// -----------------------------------------------------------------------------

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Data;

namespace HUDnopy.Localization;

/// <summary>
/// Localized category, rarity, and status labels for engineering materials.
/// </summary>
internal static class MaterialTranslations
{
    private static readonly string[] Keys =
    {
        "title", "search", "searchPlaceholder", "categoryFilter", "rarityFilter",
        "allCategories", "raw", "manufactured", "encoded", "allRarities",
        "veryCommon", "common", "standard", "rare", "veryRare", "unclassified",
        "waiting", "empty", "inventory", "filtered", "unavailable", "emptyInventory", "noMatches",
        "wishlistTargetTip", "wishlistToggle", "traderFullSummary", "wishlistSummary"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog = Load();

    public static IReadOnlyDictionary<string, string> ForLanguage(string language) =>
        Catalog.TryGetValue(language, out var text)
            ? text
            : throw new ArgumentException($"Idioma de materiales no compatible: {language}", nameof(language));

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var catalog = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string language in LanguageCatalog.Languages)
        {
            var text = LanguageCatalog.GetSection(language, "materials").Deserialize<Dictionary<string, string>>();
            if (text is null || text.Count != Keys.Length ||
                Keys.Any(key => !text.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value)))
                throw new InvalidDataException($"El catálogo de materiales está incompleto para {language}.");
            foreach (string key in new[] { "inventory", "filtered" })
            {
                string format = text[key];
                if (!format.Contains("{0}", StringComparison.Ordinal) ||
                    (key == "filtered" && !format.Contains("{1}", StringComparison.Ordinal)))
                    throw new InvalidDataException($"Formato de materiales incompleto: {language}/{key}.");
                _ = string.Format(CultureInfo.InvariantCulture, format, 1, 2);
            }
            catalog.Add(language, new ReadOnlyDictionary<string, string>(text));
        }
        return catalog;
    }
}

internal sealed class MaterialValueConverter(string language, bool isCategory) : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = MaterialTranslations.ForLanguage(language);
        if (isCategory)
        {
            string category = value as string ?? "";
            string? key = category switch
            {
                "En bruto" => "raw",
                "Manufacturados" => "manufactured",
                "Datos codificados" => "encoded",
                _ => null
            };
            return key is null ? category : text[key];
        }

        string rarity = value switch
        {
            1 => "veryCommon",
            2 => "common",
            3 => "standard",
            4 => "rare",
            5 => "veryRare",
            _ => "unclassified"
        };
        return text[rarity];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
