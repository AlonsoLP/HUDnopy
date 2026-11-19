// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/ProfileTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized labels for commander profile rank and statistic entries.
// -----------------------------------------------------------------------------

using HUDnopy.Models;
using System.Globalization;
using System.Text.Json.Serialization;

namespace HUDnopy.Localization;

/// <summary>
/// Localized labels for commander profile rank and statistic entries.
/// </summary>
internal sealed record ProfileValueView([property: JsonIgnore] ProfileValueItem Item, string Category, string Name, string Value)
{
    public string CategoryId => string.IsNullOrWhiteSpace(Item.CategoryKey) ? Item.Category : Item.CategoryKey;
    public string SearchName => Item.Name;
}

internal static class ProfileTranslations
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog =
        DictionaryTranslationCatalog.Load("profile");
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> FieldCatalog =
        DictionaryTranslationCatalog.Load("profileFields");

    public static ProfileValueView Present(ProfileValueItem item, string language)
    {
        var text = Catalog[language];
        string category = text.TryGetValue(item.CategoryKey, out var translated) ? translated : item.Category;
        string value = item.BooleanValue.HasValue ? text[item.BooleanValue.Value ? "yes" : "no"] :
            item.IsNumber ? item.Value.Replace(".", CultureInfo.GetCultureInfo(language).NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal) :
            item.Value;
        string name = item.CategoryKey is "Rank" or "Progress" or "Reputation" or "Statistics"
            ? string.Join(" / ", item.Name.Split(" / ").Select(part => TranslateField(part, FieldCatalog[language])))
            : item.Name;
        return new(item, category, name, value);
    }

    private static string TranslateField(string part, IReadOnlyDictionary<string, string> fields)
    {
        int index = part.IndexOf(" [", StringComparison.Ordinal);
        string key = index < 0 ? part : part[..index];
        string suffix = index < 0 ? "" : part[index..];
        return fields.TryGetValue(key, out string? translation) ? translation + suffix : part;
    }
}
