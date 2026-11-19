// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/AlertSpeechTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized spoken announcements for voice alerts.
// -----------------------------------------------------------------------------

using HUDnopy.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace HUDnopy.Localization;

/// <summary>
/// Localized spoken announcements for voice alerts.
/// </summary>
internal static class AlertSpeechTranslations
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog =
        DictionaryTranslationCatalog.Load("speechAlerts");

    public static IReadOnlyDictionary<string, string> ForLanguage(string language) =>
        Catalog.TryGetValue(language, out var text)
            ? text
            : throw new ArgumentException($"Idioma de voz de alertas no compatible: {language}", nameof(language));

    public static string FormatAlert(AlertNotification alert, string language)
    {
        var text = ForLanguage(language);
        var culture = CultureInfo.GetCultureInfo(language);

        if (alert.Localization is ActivityAlertLocalization activity)
            return FormatActivityAlert(activity, text, culture);

        if (alert.Localization is { } core)
            return FormatCoreAlert(core, text, culture);

        return $"{alert.Title}. {alert.Message}";
    }

    public static string FormatPreview(AlertDefinition definition, AppConfig config, string language)
    {
        var text = ForLanguage(language);
        var culture = CultureInfo.GetCultureInfo(language);

        return definition.Id switch
        {
            "fuel-low" => string.Format(culture, text["fuelLow"], Math.Clamp(config.FuelLowThresholdPercent, 2, 100)),
            "fuel-critical" => string.Format(culture, text["fuelCritical"], Math.Clamp(config.FuelCriticalThresholdPercent, 1, 99)),
            "overheating" => text["overheating"],
            "hull-critical" => string.Format(culture, text["hullCritical"], Math.Max(1, Math.Clamp(config.HullCriticalThresholdPercent, 1, 99) - 5)),
            "shields-down" => text["shieldsDown"],
            "interdiction" => text["interdiction"],
            "danger" => text["danger"],
            "cockpit-breach" => text["cockpitBreach"],
            "jump-arrival" => string.Format(culture, text["arrival"], "Sol"),
            "docking" => string.Format(culture, text["docked"], "Jameson Memorial"),
            "mission-activity" => string.Format(culture, text["missionCompletedReward"], 250000),
            "mission-expiry" => string.Format(
                culture,
                text["expiryWarning"],
                text["sampleMission"],
                FormatDuration(text, culture, TimeSpan.FromHours(Math.Clamp(config.MissionExpiryWarningHours, 2, 720)))),
            "bounty-reward" => string.Format(culture, text["bountyAmount"], 120000),
            "crime-committed" => string.Format(culture, text["crimeFine"], 400),
            "cargo-activity" => string.Format(culture, text["cargoBoughtCount"], 16, text["sampleCommodity"]),
            "carrier-activity" => string.Format(culture, text["carrierRequestDest"], "Colonia"),
            "exploration-activity" => string.Format(culture, text["fssBodies"], 24),
            "copilot-safety" => string.Format(culture, text["jumpHazard"], "Jackson's Lighthouse"),
            _ => $"{definition.Name}. {definition.Description}"
        };
    }

    private static string FormatCoreAlert(
        AlertLocalization localization,
        IReadOnlyDictionary<string, string> text,
        CultureInfo culture)
    {
        return localization.MessageKey switch
        {
            "fuelLow" when localization.Arguments.Length >= 3 =>
                string.Format(culture, text["fuelLow"], Math.Round(Convert.ToDouble(localization.Arguments[2], CultureInfo.InvariantCulture))),
            "fuelCritical" when localization.Arguments.Length >= 3 =>
                string.Format(culture, text["fuelCritical"], Math.Round(Convert.ToDouble(localization.Arguments[2], CultureInfo.InvariantCulture))),
            "hullCritical" when localization.Arguments.Length >= 1 =>
                string.Format(culture, text["hullCritical"], Math.Round(Convert.ToDouble(localization.Arguments[0], CultureInfo.InvariantCulture))),
            "fuelUnknown" => text["fuelUnknown"],
            "overheating" => text["overheating"],
            "shieldsDown" => text["shieldsDown"],
            "interdiction" => text["interdiction"],
            "danger" => text["danger"],
            "cockpitBreach" => text["cockpitBreach"],
            _ => text.TryGetValue(localization.MessageKey, out string? template)
                ? string.Format(culture, template, localization.Arguments)
                : localization.MessageKey
        };
    }

    private static string FormatActivityAlert(
        ActivityAlertLocalization localization,
        IReadOnlyDictionary<string, string> text,
        CultureInfo culture)
    {
        object ResolveArg(object arg) => arg switch
        {
            LocalizedAlertArgument localized => text.TryGetValue(localized.Key, out string? val) ? val : localized.Key,
            AlertRemainingTime remaining => FormatDuration(text, culture, remaining.Value),
            _ => arg
        };

        object FirstArgOrFallback(string fallbackKey) =>
            localization.Arguments.Length > 0 ? ResolveArg(localization.Arguments[0]) : text[fallbackKey];

        object FirstTitleArgOrFallback(string fallbackKey) =>
            localization.TitleArguments.Length > 0 ? ResolveArg(localization.TitleArguments[0]) : text[fallbackKey];

        switch (localization.TitleKey)
        {
            case "arrivalTitle":
                return string.Format(culture, text["arrival"], FirstArgOrFallback("newSystem"));

            case "dockedTitle":
                return string.Format(culture, text["docked"], FirstArgOrFallback("station"));

            case "undockedTitle":
                return string.Format(culture, text["undocked"], FirstArgOrFallback("station"));

            case "missionAcceptedTitle":
                return string.Format(culture, text["missionAccepted"], FirstArgOrFallback("mission"));

            case "missionCompletedTitle":
                if (localization.MessageKey == "missionReward" && localization.Arguments.Length >= 2)
                    return string.Format(culture, text["missionCompletedReward"], ResolveArg(localization.Arguments[1]));
                return string.Format(culture, text["missionCompleted"], FirstArgOrFallback("mission"));

            case "missionAbandonedTitle":
                return string.Format(culture, text["missionAbandoned"], FirstArgOrFallback("mission"));

            case "missionFailedTitle":
                return string.Format(culture, text["missionFailed"], FirstArgOrFallback("mission"));

            case "missionExpiredTitle":
                return string.Format(culture, text["missionExpired"], FirstArgOrFallback("mission"));

            case "expiryWarningTitle":
                return localization.MessageKey == "expiryRemaining" && localization.Arguments.Length >= 1
                    ? string.Format(culture, text["expiryWarning"], FirstTitleArgOrFallback("mission"), ResolveArg(localization.Arguments[0]))
                    : string.Format(culture, text["expiryExpired"], FirstTitleArgOrFallback("mission"));

            case "expiryCriticalTitle":
                return localization.MessageKey == "expiryRemaining" && localization.Arguments.Length >= 1
                    ? string.Format(culture, text["expiryCritical"], FirstTitleArgOrFallback("mission"), ResolveArg(localization.Arguments[0]))
                    : string.Format(culture, text["expiryExpired"], FirstTitleArgOrFallback("mission"));

            case "expiryExpiredTitle":
                return string.Format(culture, text["expiryExpired"], FirstTitleArgOrFallback("mission"));

            case "bountyTitle":
            case "combatRewardTitle":
            case "voucherTitle":
            {
                string prefix = localization.TitleKey switch
                {
                    "voucherTitle" => "voucher",
                    "combatRewardTitle" => "combatReward",
                    _ => "bounty"
                };
                if (localization.MessageKey == "rewardBoth" && localization.Arguments.Length >= 2)
                    return string.Format(culture, text[prefix + "Amount"], ResolveArg(localization.Arguments[1]));
                if (localization.MessageKey == "rewardAmount" && localization.Arguments.Length >= 1)
                    return string.Format(culture, text[prefix + "Amount"], ResolveArg(localization.Arguments[0]));
                return text[prefix + "None"];
            }

            case "crimeTitle":
                if (localization.MessageKey == "crimeBoth" && localization.Arguments.Length >= 2)
                    return string.Format(culture, text["crimeFine"], ResolveArg(localization.Arguments[1]));
                if (localization.MessageKey == "fineOnly" && localization.Arguments.Length >= 1)
                    return string.Format(culture, text["crimeFine"], ResolveArg(localization.Arguments[0]));
                if (localization.MessageKey == "rawValue" && localization.Arguments.Length >= 1)
                    return string.Format(culture, text["crimeNamed"], ResolveArg(localization.Arguments[0]));
                return text["crimeNone"];

            case "cargoBoughtTitle":
            case "cargoSoldTitle":
            case "cargoCollectedTitle":
            case "cargoEjectedTitle":
            case "cargoTransferTitle":
            case "cargoActivityTitle":
            {
                string prefix = localization.TitleKey switch
                {
                    "cargoBoughtTitle" => "cargoBought",
                    "cargoSoldTitle" => "cargoSold",
                    "cargoCollectedTitle" => "cargoCollected",
                    "cargoEjectedTitle" => "cargoEjected",
                    "cargoTransferTitle" => "cargoTransfer",
                    _ => "cargoActivity"
                };
                if (localization.MessageKey == "cargoBoth" && localization.Arguments.Length >= 2)
                    return string.Format(culture, text[prefix + "Count"], ResolveArg(localization.Arguments[1]), ResolveArg(localization.Arguments[0]));
                if (localization.MessageKey == "rawValue" && localization.Arguments.Length >= 1)
                    return string.Format(culture, text[prefix + "Item"], ResolveArg(localization.Arguments[0]));
                return text[prefix + "None"];
            }

            case "carrierRequestTitle":
                return localization.MessageKey == "carrierDestination" && localization.Arguments.Length >= 1
                    ? string.Format(culture, text["carrierRequestDest"], ResolveArg(localization.Arguments[0]))
                    : text["carrierRequestNone"];

            case "carrierJumpTitle":
                return localization.MessageKey == "carrierDestination" && localization.Arguments.Length >= 1
                    ? string.Format(culture, text["carrierJumpDest"], ResolveArg(localization.Arguments[0]))
                    : text["carrierJumpNone"];

            case "carrierCancelTitle":
                return text["carrierCancel"];

            case "carrierBuyTitle":
                return text["carrierBuy"];

            case "carrierDecommissionTitle":
                return text["carrierDecommission"];

            case "fssTitle":
                return localization.MessageKey == "fssBodies" && localization.Arguments.Length >= 1
                    ? string.Format(culture, text["fssBodies"], ResolveArg(localization.Arguments[0]))
                    : text["fssComplete"];

            case "allBodiesTitle":
                return string.Format(culture, text["fssAll"], FirstArgOrFallback("currentSystem"));

            case "mappingTitle":
                return localization.MessageKey == "mappingEfficient"
                    ? string.Format(culture, text["mappingEfficient"], FirstArgOrFallback("planetaryBody"))
                    : string.Format(culture, text["mapping"], FirstArgOrFallback("planetaryBody"));

            case "organicTitle":
                return string.Format(culture, text["organic"], FirstArgOrFallback("organicSpecies"));

            case "codexTitle":
                return string.Format(culture, text["codex"], FirstArgOrFallback("newDiscovery"));

            case "soldExplorationTitle":
                if (localization.MessageKey == "soldSystemsEarnings" && localization.Arguments.Length >= 2)
                    return string.Format(culture, text["soldEarnings"], ResolveArg(localization.Arguments[1]));
                if (localization.MessageKey == "soldEarnings" && localization.Arguments.Length >= 1)
                    return string.Format(culture, text["soldEarnings"], ResolveArg(localization.Arguments[0]));
                if (localization.MessageKey == "soldSystems" && localization.Arguments.Length >= 1)
                    return string.Format(culture, text["soldSystems"], ResolveArg(localization.Arguments[0]));
                return text["soldNone"];

            case "exoDistanceTitle":
                return string.Format(culture, text["exoDistance"], FirstArgOrFallback("organicSpecies"));

            case "jumpHazardTitle":
                return string.Format(culture, text["jumpHazard"], FirstArgOrFallback("newSystem"));

            case "jumpScoopableTitle":
                return string.Format(culture, text["jumpScoopable"], FirstArgOrFallback("newSystem"));

            case "highGravityTitle":
                return localization.Arguments.Length >= 1
                    ? string.Format(culture, text["highGravity"], ResolveArg(localization.Arguments[0]))
                    : text["highGravityGeneric"];

            case "limpetsReminderTitle":
                return text["limpetsReminder"];

            case "fsdWearTitle":
                return localization.Arguments.Length >= 1
                    ? string.Format(culture, text["fsdWear"], ResolveArg(localization.Arguments[0]))
                    : text["fsdWearGeneric"];

            case "scoWarningTitle":
                return text["scoWarning"];

            case "routeFuelTitle":
                return text["routeFuelWarning"];

            case "moduleDamageTitle":
                return localization.Arguments.Length >= 2
                    ? string.Format(culture, text["moduleDamage"], ResolveArg(localization.Arguments[0]), ResolveArg(localization.Arguments[1]))
                    : text["fsdWearGeneric"];

            case "wishlistMaterialTitle":
                return localization.Arguments.Length >= 1
                    ? string.Format(culture, text["wishlistMaterial"], ResolveArg(localization.Arguments[0]))
                    : text["cargoCollectedNone"];

            case "miningCoreTitle":
                return localization.Arguments.Length >= 1
                    ? string.Format(culture, text["miningCore"], ResolveArg(localization.Arguments[0]))
                    : text["cargoActivityNone"];

            case "miningTargetTitle":
                return localization.Arguments.Length >= 2
                    ? string.Format(culture, text["miningTarget"], ResolveArg(localization.Arguments[0]), ResolveArg(localization.Arguments[1]))
                    : text["cargoActivityNone"];

            case "wantedTargetTitle":
                return localization.Arguments.Length >= 2
                    ? string.Format(culture, text["wantedTarget"], ResolveArg(localization.Arguments[0]), ResolveArg(localization.Arguments[1]))
                    : text["bountyNone"];

            case "illegalScanTitle":
                return text["illegalScan"];

            case "shipScannedTitle":
                return text["shipScanned"];

            case "underAttackTitle":
                return text["underAttack"];

            default:
                return text["fssComplete"];
        }
    }

    private static string FormatDuration(IReadOnlyDictionary<string, string> text, CultureInfo culture, TimeSpan remaining)
    {
        if (remaining.TotalDays >= 1)
        {
            int days = (int)remaining.TotalDays;
            int hours = remaining.Hours;
            return days == 1
                ? string.Format(culture, text["durationOneDayHours"], hours)
                : string.Format(culture, text["durationDaysHours"], days, hours);
        }

        if (remaining.TotalHours >= 1)
        {
            int hours = (int)remaining.TotalHours;
            int minutes = remaining.Minutes;
            return hours == 1
                ? string.Format(culture, text["durationOneHourMinutes"], minutes)
                : string.Format(culture, text["durationHoursMinutes"], hours, minutes);
        }

        int totalMinutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return totalMinutes == 1
            ? text["durationOneMinute"]
            : string.Format(culture, text["durationMinutes"], totalMinutes);
    }
}
