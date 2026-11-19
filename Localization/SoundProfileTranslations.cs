// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/SoundProfileTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized names for built-in alert sound profiles.
// -----------------------------------------------------------------------------

namespace HUDnopy.Localization;

/// <summary>
/// Localized names for built-in alert sound profiles.
/// </summary>
internal static class SoundProfileTranslations
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog =
        DictionaryTranslationCatalog.Load("soundProfiles");
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ErrorCatalog =
        DictionaryTranslationCatalog.Load("soundErrors");

    public static string Name(string id, string original, string language) =>
        Catalog[language].TryGetValue(id, out var name) ? name : original;

    public static IReadOnlyDictionary<string, string> Errors(string language) =>
        ErrorCatalog[LanguageCatalog.TryNormalize(language, out var normalized) ? normalized : "es-ES"];
}
