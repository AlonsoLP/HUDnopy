// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/CoreAlertTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized templates for core ship-telemetry safety alerts.
// -----------------------------------------------------------------------------

using HUDnopy.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HUDnopy.Localization;

/// <summary>
/// Localized templates for core ship-telemetry safety alerts.
/// </summary>
internal record AlertLocalization(string MessageKey, params object[] Arguments);

internal static class CoreAlertTranslations
{
    private static readonly string[] Keys =
    {
        "dangerTitle", "fuelLow", "fuelCritical", "fuelUnknown", "overheating", "hullCritical",
        "shieldsDown", "interdiction", "danger", "cockpitBreach", "activeOne", "activeMany"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog = Load();

    public static IReadOnlyDictionary<string, string> ForLanguage(string language)
    {
        if (!Catalog.TryGetValue(language, out var text))
            throw new ArgumentException($"Idioma de alertas no compatible: {language}", nameof(language));
        return text;
    }

    public static AlertNotification Translate(AlertNotification alert, string language)
    {
        if (alert.Localization is not { } localization)
            return alert;
        if (localization is ActivityAlertLocalization activity)
            return ActivityAlertTranslations.Translate(alert, activity, language);
        var text = ForLanguage(language);

        string title = alert.AlertId == "danger"
            ? text["dangerTitle"]
            : AlertTranslations.GetName(alert.AlertId, language);
        return alert with
        {
            Title = title,
            Message = string.Format(CultureInfo.GetCultureInfo(language), text[localization.MessageKey], localization.Arguments)
        };
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var catalog = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string language in LanguageCatalog.Languages)
        {
            var text = LanguageCatalog.GetSection(language, "coreAlerts").Deserialize<Dictionary<string, string>>();
            if (text is null || text.Count != Keys.Length || Keys.Any(key => !text.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)))
                throw new InvalidDataException($"El catálogo de alertas base está incompleto para {language}.");
            foreach (var (key, count) in new[]
            {
                ("fuelLow", 4), ("fuelCritical", 4), ("hullCritical", 2), ("activeOne", 1),
                ("activeMany", 2), ("fuelUnknown", 0), ("overheating", 0), ("shieldsDown", 0),
                ("interdiction", 0), ("danger", 0), ("cockpitBreach", 0)
            })
            {
                string template = text[key];
                for (int i = 0; i < count; i++)
                {
                    if (!template.Contains($"{{{i}", StringComparison.Ordinal))
                        throw new InvalidDataException($"Falta el parámetro {i} de {key} en el catálogo {language}.");
                }
                try { _ = string.Format(CultureInfo.InvariantCulture, template, Enumerable.Repeat<object>(1d, count).ToArray()); }
                catch (FormatException exception) { throw new InvalidDataException($"Formato inválido de {key} en el catálogo {language}.", exception); }
            }
            catalog.Add(language, new ReadOnlyDictionary<string, string>(text));
        }
        return catalog;
    }
}
