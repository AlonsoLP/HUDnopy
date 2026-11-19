// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/InputTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized key names and modifier labels for simulated game inputs.
// -----------------------------------------------------------------------------

namespace HUDnopy.Localization;

/// <summary>
/// Localized key names and modifier labels for simulated game inputs.
/// </summary>
internal static class InputTranslations
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog =
        DictionaryTranslationCatalog.Load("input");

    public static IReadOnlyDictionary<string, string> ForLanguage(string language) => Catalog[language];
}
