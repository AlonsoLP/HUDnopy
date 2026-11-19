// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/FlightTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized labels for flight telemetry, navigation, and Fleet Carrier views.
// -----------------------------------------------------------------------------

using HUDnopy.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace HUDnopy.Localization;

/// <summary>
/// Localized labels for flight telemetry, navigation, and Fleet Carrier views.
/// </summary>
internal static class FlightTranslations
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog = Load();

    public static IReadOnlyDictionary<string, string> ForLanguage(string language) =>
        Catalog.TryGetValue(language, out var text)
            ? text
            : throw new ArgumentException($"Idioma de vuelo no compatible: {language}", nameof(language));

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var catalog = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? expected = null;
        foreach (string language in LanguageCatalog.Languages)
        {
            var text = LanguageCatalog.GetSection(language, "flight").Deserialize<Dictionary<string, string>>();
            if (text is null || text.Count == 0 || text.Any(pair => string.IsNullOrWhiteSpace(pair.Value)))
                throw new InvalidDataException($"El catálogo de vuelo está incompleto para {language}.");
            expected ??= new HashSet<string>(text.Keys, StringComparer.Ordinal);
            if (!expected.SetEquals(text.Keys) || !text["remaining"].Contains("{0}", StringComparison.Ordinal))
                throw new InvalidDataException($"Las claves de vuelo no coinciden para {language}.");
            _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, text["remaining"], 1);
            catalog.Add(language, new ReadOnlyDictionary<string, string>(text));
        }
        return catalog;
    }

    private const uint KnownFlags2Mask = 0x003FFFFFu;

    public static string OnFootStatus(ShipState state, string language)
    {
        var text = ForLanguage(language);
        if ((state.RawFlags2 & KnownFlags2Mask) == 0)
            return text["noFootStatus"];

        var statuses = new List<string>();
        void Add(bool active, string key) { if (active) statuses.Add(text[key]); }
        Add(state.OnFoot, "onFoot");
        Add(state.InTaxi, "taxi");
        Add(state.InMulticrew, "multicrew");
        Add(state.OnFootInStation, "inStation");
        Add(state.OnFootOnPlanet, "onPlanet");
        Add(state.AimDownSight, "aiming");
        Add(state.LowOxygen, "lowOxygen");
        Add(state.LowHealth, "lowHealth");
        Add(state.VeryCold, "veryCold");
        Add(state.Cold && !state.VeryCold, "cold");
        Add(state.VeryHot, "veryHot");
        Add(state.Hot && !state.VeryHot, "hot");
        Add(state.GlideMode, "gliding");
        Add(state.OnFootInHangar, "hangar");
        Add(state.OnFootSocialSpace, "social");
        Add(state.OnFootExterior, "exterior");
        Add(state.BreathableAtmosphere, "breathable");
        Add(state.TelepresenceMulticrew, "telepresence");
        Add(state.PhysicalMulticrew, "physicalCrew");
        Add(state.FsdHyperdriveCharging, "charging");
        Add(state.SupercruiseOverdriveActive, "sco");
        Add(state.SupercruiseAssistActive, "assist");
        return statuses.Count == 0 ? text["noFootStatus"] : string.Join(" · ", statuses);
    }
}
