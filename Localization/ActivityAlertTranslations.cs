// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/ActivityAlertTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized templates for Journal-driven activity and safety alerts.
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
/// Localized templates for Journal-driven activity and safety alerts.
/// </summary>
internal sealed record ActivityAlertLocalization : AlertLocalization
{
    public string TitleKey { get; }
    public object[] TitleArguments { get; }

    public ActivityAlertLocalization(string titleKey, object[] titleArguments, string messageKey, params object[] arguments)
        : base(messageKey, arguments)
    {
        TitleKey = titleKey;
        TitleArguments = titleArguments;
    }
}

internal sealed record LocalizedAlertArgument(string Key);
internal sealed record AlertRemainingTime(TimeSpan Value);

internal static class ActivityAlertTranslations
{
    private static readonly IReadOnlyDictionary<string, int> Keys = new Dictionary<string, int>
    {
        ["arrivalTitle"] = 0, ["dockedTitle"] = 0, ["undockedTitle"] = 0,
        ["missionAcceptedTitle"] = 0, ["missionCompletedTitle"] = 0, ["missionAbandonedTitle"] = 0,
        ["missionFailedTitle"] = 0, ["missionExpiredTitle"] = 0,
        ["expiryWarningTitle"] = 1, ["expiryCriticalTitle"] = 1, ["expiryExpiredTitle"] = 1,
        ["newSystem"] = 0, ["station"] = 0, ["mission"] = 0,
        ["arrival"] = 1, ["docked"] = 1, ["undocked"] = 1, ["missionSimple"] = 1,
        ["missionReward"] = 2, ["expiryRemaining"] = 1, ["expiryPassed"] = 0,
        ["durationDays"] = 2, ["durationHours"] = 2, ["durationMinutes"] = 1,
        ["voucherTitle"] = 0, ["combatRewardTitle"] = 0, ["bountyTitle"] = 0, ["crimeTitle"] = 0,
        ["cargoBoughtTitle"] = 0, ["cargoSoldTitle"] = 0, ["cargoCollectedTitle"] = 0,
        ["cargoEjectedTitle"] = 0, ["cargoTransferTitle"] = 0, ["cargoActivityTitle"] = 0,
        ["carrierJumpTitle"] = 0, ["carrierRequestTitle"] = 0, ["carrierCancelTitle"] = 0,
        ["carrierBuyTitle"] = 0, ["carrierDecommissionTitle"] = 0,
        ["rewardBoth"] = 2, ["rewardAmount"] = 1, ["rewardNone"] = 0, ["rawValue"] = 1,
        ["crimeBoth"] = 2, ["fineOnly"] = 1, ["crimeNone"] = 0,
        ["cargoBoth"] = 2, ["countOnly"] = 1, ["cargoNone"] = 0,
        ["carrierDestination"] = 1, ["carrierNone"] = 0,
        ["fssTitle"] = 0, ["allBodiesTitle"] = 0, ["mappingTitle"] = 0,
        ["organicTitle"] = 0, ["codexTitle"] = 0, ["soldExplorationTitle"] = 0,
        ["currentSystem"] = 0, ["planetaryBody"] = 0, ["organicSpecies"] = 0, ["newDiscovery"] = 0,
        ["fssBodies"] = 1, ["fssComplete"] = 0, ["fssAll"] = 1,
        ["mapping"] = 1, ["mappingProbes"] = 2, ["mappingEfficient"] = 2,
        ["joinedOne"] = 1, ["joinedTwo"] = 2, ["joinedThree"] = 3,
        ["soldSystems"] = 1, ["soldSystemsEarnings"] = 2, ["soldNone"] = 0, ["soldEarnings"] = 1,
        ["exoDistanceTitle"] = 0, ["exoDistance"] = 2,
        ["jumpHazardTitle"] = 0, ["jumpHazard"] = 2,
        ["jumpScoopableTitle"] = 0, ["jumpScoopable"] = 2,
        ["highGravityTitle"] = 0, ["highGravity"] = 1,
        ["limpetsReminderTitle"] = 0, ["limpetsReminder"] = 0,
        ["fsdWearTitle"] = 0, ["fsdWear"] = 1,
        ["scoWarningTitle"] = 0, ["scoWarning"] = 0,
        ["routeFuelTitle"] = 0, ["routeFuelWarning"] = 0,
        ["moduleDamageTitle"] = 0, ["moduleDamage"] = 2,
        ["wishlistMaterialTitle"] = 0, ["wishlistMaterial"] = 1,
        ["miningCoreTitle"] = 0, ["miningCore"] = 1,
        ["miningTargetTitle"] = 0, ["miningTarget"] = 2,
        ["wantedTargetTitle"] = 0, ["wantedTarget"] = 2,
        ["illegalScanTitle"] = 0, ["illegalScan"] = 1,
        ["shipScannedTitle"] = 0, ["shipScanned"] = 1,
        ["underAttackTitle"] = 0, ["underAttack"] = 0
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog = Load();

    public static AlertNotification Translate(AlertNotification alert, ActivityAlertLocalization localization, string language) =>
        alert with
        {
            Title = Format(language, localization.TitleKey, localization.TitleArguments),
            Message = Format(language, localization.MessageKey, localization.Arguments)
        };

    public static string Format(string language, string key, params object[] arguments)
    {
        if (!Catalog.TryGetValue(language, out var text))
            throw new ArgumentException($"Idioma de alertas de actividad no compatible: {language}", nameof(language));
        var culture = CultureInfo.GetCultureInfo(language);
        object[] resolved = arguments.Select(argument => argument switch
        {
            LocalizedAlertArgument localized => text[localized.Key],
            AlertRemainingTime remaining => FormatDuration(text, culture, remaining.Value),
            _ => argument
        }).ToArray();
        return string.Format(culture, text[key], resolved);
    }

    private static string FormatDuration(IReadOnlyDictionary<string, string> text, CultureInfo culture, TimeSpan remaining)
    {
        if (remaining.TotalDays >= 1)
            return string.Format(culture, text["durationDays"], (int)remaining.TotalDays, remaining.Hours);
        if (remaining.TotalHours >= 1)
            return string.Format(culture, text["durationHours"], (int)remaining.TotalHours, remaining.Minutes);
        return string.Format(culture, text["durationMinutes"], Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)));
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var catalog = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string language in LanguageCatalog.Languages)
        {
            var text = LanguageCatalog.GetSection(language, "activityAlerts").Deserialize<Dictionary<string, string>>();
            if (text is null || text.Count != Keys.Count || Keys.Any(item => !text.TryGetValue(item.Key, out var value) || string.IsNullOrWhiteSpace(value)))
                throw new InvalidDataException($"El catálogo de alertas de actividad está incompleto para {language}.");
            foreach (var (key, count) in Keys)
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
