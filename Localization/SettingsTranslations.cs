// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/SettingsTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized strings for application settings, integrations, AI, and TTS options.
// -----------------------------------------------------------------------------

namespace HUDnopy.Localization;

/// <summary>
/// Localized strings for application settings, integrations, AI, and TTS options.
/// </summary>
internal static class SettingsTranslations
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog = DictionaryTranslationCatalog.Load("settings");

    public static IReadOnlyDictionary<string, string> ForLanguage(string language) =>
        Catalog.TryGetValue(language, out var text)
            ? text
            : throw new ArgumentException($"Idioma de ajustes no compatible: {language}", nameof(language));

}
