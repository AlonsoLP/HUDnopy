// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/LanguageSettingsTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized UI strings for language selection and partial-translation notices.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HUDnopy.Localization;

/// <summary>
/// Localized UI strings for language selection and partial-translation notices.
/// </summary>
internal sealed record LanguageSettingsText(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("tooltip")] string Tooltip,
    [property: JsonPropertyName("partialWarning")] string PartialWarning);

internal static class LanguageSettingsTranslations
{
    private static readonly IReadOnlyDictionary<string, LanguageSettingsText> Catalog = Load();

    public static LanguageSettingsText ForLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (!Catalog.TryGetValue(language, out var text))
            throw new ArgumentException($"Idioma no compatible: {language}", nameof(language));
        return text;
    }

    private static IReadOnlyDictionary<string, LanguageSettingsText> Load()
    {
        var catalog = new Dictionary<string, LanguageSettingsText>(StringComparer.OrdinalIgnoreCase);
        foreach (string language in LanguageCatalog.Languages)
        {
            var text = LanguageCatalog.GetSection(language, "languageSettings").Deserialize<LanguageSettingsText>();
            if (text is null || string.IsNullOrWhiteSpace(text.Label) || string.IsNullOrWhiteSpace(text.Tooltip) ||
                text.PartialWarning is null)
                throw new InvalidDataException($"Faltan textos del selector de idioma para {language}.");
            catalog.Add(language, text);
        }
        return catalog;
    }
}
