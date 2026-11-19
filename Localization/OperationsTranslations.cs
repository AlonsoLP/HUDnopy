// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/OperationsTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized labels for cargo, missions, market, shipyard, and colonisation views.
// -----------------------------------------------------------------------------

using HUDnopy.Models;
using System.Globalization;
using System.Windows.Data;

namespace HUDnopy.Localization;

/// <summary>
/// Localized labels for cargo, missions, market, shipyard, and colonisation views.
/// </summary>
internal static class OperationsTranslations
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog = DictionaryTranslationCatalog.Load("operations");

    public static IReadOnlyDictionary<string, string> ForLanguage(string language) =>
        Catalog.TryGetValue(language, out var text)
            ? text
            : throw new ArgumentException($"Idioma de operaciones no compatible: {language}", nameof(language));

    public static string Expiry(DateTimeOffset? expiry, string language) =>
        expiry?.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo(language)) ?? ForLanguage(language)["noExpiry"];

    public static string Remaining(DateTimeOffset? expiry, string language)
    {
        var text = ForLanguage(language);
        if (!expiry.HasValue) return text["noExpiry"];
        TimeSpan remaining = expiry.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return text["expired"];
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays} {text["days"]} {remaining.Hours} {text["hours"]}";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours} {text["hours"]} {remaining.Minutes} {text["minutes"]}";
        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} {text["minutes"]}";
    }

    public static string CargoState(CargoItem item, string language) =>
        ForLanguage(language)[item.Stolen > 0 ? "stolen" : "legal"];

    public static string MissionName(MissionItem item, string language) =>
        item.NameIsFallback ? ForLanguage(language)["missionFallback"] : item.Name;

    public static string PassengerType(PassengerItem item, string language) =>
        item.TypeIsFallback ? ForLanguage(language)["missionFallback"] : item.Type;

    public static string BackpackType(BackpackItem item, string language)
    {
        string? key = item.Type.ToUpperInvariant() switch
        {
            "ITEM" or "ITEMS" => "backpackItems",
            "COMPONENT" or "COMPONENTS" => "backpackComponents",
            "CONSUMABLE" or "CONSUMABLES" => "backpackConsumables",
            "DATA" => "backpackData",
            _ => null
        };
        return key is null ? item.Type : ForLanguage(language)[key];
    }

    public static string MarketFlags(MarketCommodityItem item, string language)
    {
        var text = ForLanguage(language);
        var values = new List<string>();
        if (item.Consumer) values.Add(text["consumer"]);
        if (item.Producer) values.Add(text["producer"]);
        if (item.Rare) values.Add(text["rare"]);
        return values.Count == 0 ? "—" : string.Join(" · ", values);
    }

    public static string Snapshot(DateTimeOffset? timestamp, string station, string system, string unavailableKey, string language)
    {
        var text = ForLanguage(language);
        if (!timestamp.HasValue) return text[unavailableKey];
        string location = string.Join(" · ", new[] { station, system }.Where(value => !string.IsNullOrWhiteSpace(value)));
        string formatted = string.Format(text["snapshotStatus"], Expiry(timestamp, language));
        return location.Length == 0 ? formatted : $"{location} · {formatted}";
    }

    public static string Construction(ColonisationSnapshot snapshot, string language)
    {
        var text = ForLanguage(language);
        if (!snapshot.Timestamp.HasValue) return text["constructionUnavailable"];
        string key = snapshot.ConstructionComplete == true ? "constructionComplete" :
            snapshot.ConstructionFailed == true ? "constructionFailed" : "constructionProgress";
        return $"{text[key]} · {string.Format(text["updated"], Expiry(snapshot.Timestamp, language))}";
    }
}

internal enum OperationsValue { CargoState, MissionName, MissionExpiry, MissionRemaining, PassengerType, PassengerCount, PassengerExpiry, BackpackType, MarketFlags }

internal sealed class OperationsValueConverter(string language, OperationsValue kind) : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (kind, value) switch
    {
        (OperationsValue.CargoState, CargoItem cargo) => OperationsTranslations.CargoState(cargo, language),
        (OperationsValue.MissionName, MissionItem mission) => OperationsTranslations.MissionName(mission, language),
        (OperationsValue.MissionExpiry, MissionItem mission) => OperationsTranslations.Expiry(mission.Expiry, language),
        (OperationsValue.MissionRemaining, MissionItem mission) => OperationsTranslations.Remaining(mission.Expiry, language),
        (OperationsValue.PassengerType, PassengerItem passenger) => OperationsTranslations.PassengerType(passenger, language),
        (OperationsValue.PassengerCount, PassengerItem passenger) => passenger.Count?.ToString("N0", CultureInfo.GetCultureInfo(language)) ?? "—",
        (OperationsValue.PassengerExpiry, PassengerItem passenger) => OperationsTranslations.Expiry(passenger.Expiry, language),
        (OperationsValue.BackpackType, BackpackItem item) => OperationsTranslations.BackpackType(item, language),
        (OperationsValue.MarketFlags, MarketCommodityItem item) => OperationsTranslations.MarketFlags(item, language),
        _ => throw new InvalidOperationException($"Valor de operaciones no válido: {kind}.")
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
