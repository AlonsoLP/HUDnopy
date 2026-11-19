// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/AlertControlTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized labels for alert configuration controls and sound selectors.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HUDnopy.Localization;

/// <summary>
/// Localized labels for alert configuration controls and sound selectors.
/// </summary>
internal static class AlertControlTranslations
{
    private static readonly string[] Keys =
    {
        "alert", "visual", "sound", "voice", "showInfo", "hideInfo", "showInfoName",
        "hideInfoName", "visualTooltip", "soundTooltip", "voiceTooltip", "profileTooltip",
        "soundPreview", "voicePreview", "testSound", "testVoice", "speechPrefix", "speechTest"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog = Load();

    public static IReadOnlyDictionary<string, string> ForLanguage(string language)
    {
        if (!Catalog.TryGetValue(language, out var text))
            throw new ArgumentException($"Idioma de alertas no compatible: {language}", nameof(language));
        return text;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var catalog = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string language in LanguageCatalog.Languages)
        {
            var text = LanguageCatalog.GetSection(language, "alertControls").Deserialize<Dictionary<string, string>>();
            if (text is null || text.Count != Keys.Length || Keys.Any(key => !text.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)))
                throw new InvalidDataException($"El catálogo de controles de alertas está incompleto para {language}.");
            foreach (string key in new[] { "showInfoName", "hideInfoName", "testSound", "testVoice" })
            {
                if (!text[key].Contains("{0}", StringComparison.Ordinal))
                    throw new InvalidDataException($"Falta el parámetro de {key} en el catálogo {language}.");
                try { _ = string.Format(text[key], "test"); }
                catch (FormatException exception) { throw new InvalidDataException($"Formato inválido de {key} en el catálogo {language}.", exception); }
            }
            catalog.Add(language, new ReadOnlyDictionary<string, string>(text));
        }
        return catalog;
    }
}
