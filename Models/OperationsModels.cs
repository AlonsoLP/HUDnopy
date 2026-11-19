// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Models/OperationsModels.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Domain models for cargo, materials, missions, markets, outfitting, shipyard, and bindings.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace HUDnopy.Models;

/// <summary>
/// Domain models for cargo, materials, missions, markets, outfitting, shipyard, and bindings.
/// </summary>
public class CargoItem
{
    internal CargoItem Snapshot() => (CargoItem)MemberwiseClone();
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Count { get; set; }
    public int Stolen { get; set; }
    public string Vessel { get; set; } = "";
    public string MissionId { get; set; } = "";
    [JsonIgnore]
    public string CountDisplay => $"{Count} T";
    [JsonIgnore]
    public string StateDisplay => Stolen > 0 ? "Robado" : "Legal";
}

public class MaterialItem
{
    internal MaterialItem Snapshot() => (MaterialItem)MemberwiseClone();
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public int? Grade { get; set; }
    [JsonIgnore]
    public string Rarity => Grade switch
    {
        1 => "Muy común",
        2 => "Común",
        3 => "Estándar",
        4 => "Raro",
        5 => "Muy raro",
        _ => "Sin clasificar"
    };
    public int Count { get; set; }
    public int MaxCapacity => MaterialMetadataCatalog.GetMaxCapacity(Grade);
    public int WishlistTarget { get; set; }
    public string CountWithCapacityDisplay => $"{Count} / {MaxCapacity}";
    public string WishlistDisplay => WishlistTarget > 0 ? $"★ {Count} / {WishlistTarget}" : "—";
}

public class MissionItem
{
    private string _name = "Misión";
    internal MissionItem Snapshot() => (MissionItem)MemberwiseClone();
    public string Id { get; set; } = "";
    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            NameIsFallback = false;
        }
    }
    public bool NameIsFallback { get; set; } = true;
    public string Faction { get; set; } = "";
    public string TargetFaction { get; set; } = "";
    public int? KillCount { get; set; }
    public string DestinationSystem { get; set; } = "";
    public string DestinationStation { get; set; } = "";
    public string Commodity { get; set; } = "";
    public int? Count { get; set; }
    public bool? PassengerMission { get; set; }
    public int? PassengerCount { get; set; }
    public long? Reward { get; set; }
    public DateTimeOffset? Expiry { get; set; }
    public string ObjectiveDisplay => !string.IsNullOrWhiteSpace(Commodity)
        ? $"{Commodity}{(Count.HasValue ? $" × {Count.Value}" : "")}"
        : "";

    [JsonIgnore]
    public string Status => Expiry.HasValue && Expiry.Value <= DateTimeOffset.Now
        ? "Vencida"
        : "Activa";

    [JsonIgnore]
    public string ExpiryDisplay => Expiry?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "Sin vencimiento";

    [JsonIgnore]
    public string TimeRemaining
    {
        get
        {
            if (!Expiry.HasValue)
                return "Sin vencimiento";

            var remaining = Expiry.Value - DateTimeOffset.Now;
            if (remaining <= TimeSpan.Zero)
                return "Vencida";
            if (remaining.TotalDays >= 1)
                return $"{(int)remaining.TotalDays} d {remaining.Hours} h";
            if (remaining.TotalHours >= 1)
                return $"{(int)remaining.TotalHours} h {remaining.Minutes} min";
            return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} min";
        }
    }
}

public class ProfileValueItem
{
    internal ProfileValueItem Snapshot() => (ProfileValueItem)MemberwiseClone();
    [JsonIgnore]
    public string Category { get; set; } = "";
    public string CategoryKey { get; set; } = "";
    [JsonIgnore]
    public string Name { get; set; } = "";
    [JsonIgnore]
    public string Value { get; set; } = "";
    public bool? BooleanValue { get; set; }
    public bool IsNumber { get; set; }
}

public class PassengerItem
{
    internal PassengerItem Snapshot() => (PassengerItem)MemberwiseClone();
    public string MissionId { get; set; } = "";
    public string Type { get; set; } = "";
    public bool TypeIsFallback { get; set; }
    public int? Count { get; set; }
    public bool Vip { get; set; }
    public bool Wanted { get; set; }
    public string DestinationSystem { get; set; } = "";
    public string DestinationStation { get; set; } = "";
    public DateTimeOffset? Expiry { get; set; }
    [JsonIgnore]
    public string CountDisplay => Count?.ToString("N0", CultureInfo.GetCultureInfo("es-ES")) ?? "—";
    [JsonIgnore]
    public string ExpiryDisplay => Expiry?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "Sin vencimiento";
}

public class BackpackItem
{
    internal BackpackItem Snapshot() => (BackpackItem)MemberwiseClone();
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Type { get; set; } = "";
    public int Count { get; set; }
}

public class ColonisationSnapshot
{
    internal ColonisationSnapshot Snapshot()
    {
        var copy = (ColonisationSnapshot)MemberwiseClone();
        copy.Resources = Resources.Select(item => item.Snapshot()).ToList();
        return copy;
    }

    public long? MarketId { get; set; }
    public double? ConstructionProgress { get; set; }
    public bool? ConstructionComplete { get; set; }
    public bool? ConstructionFailed { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public List<ColonisationResourceItem> Resources { get; set; } = new();
    [JsonIgnore]
    public string TimestampDisplay => Timestamp?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "---";
    [JsonIgnore]
    public string ProgressDisplay => ConstructionProgress.HasValue
        ? $"{(ConstructionProgress.Value * 100).ToString("0.#", CultureInfo.GetCultureInfo("es-ES"))}%"
        : "---";
    [JsonIgnore]
    public string StatusDisplay => !Timestamp.HasValue
        ? "No hay datos de construcción disponibles."
        : ConstructionComplete == true
            ? $"Construcción completada · Actualizado: {TimestampDisplay}"
            : ConstructionFailed == true
                ? $"Construcción fallida · Actualizado: {TimestampDisplay}"
                : $"Construcción en curso · Actualizado: {TimestampDisplay}";
}

public class ColonisationResourceItem
{
    internal ColonisationResourceItem Snapshot() => (ColonisationResourceItem)MemberwiseClone();
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long RequiredAmount { get; set; }
    public long ProvidedAmount { get; set; }
    public long Payment { get; set; }
}

public class MarketSnapshot
{
    internal MarketSnapshot Snapshot()
    {
        var copy = (MarketSnapshot)MemberwiseClone();
        copy.Items = Items.Select(item => item.Snapshot()).ToList();
        return copy;
    }

    public DateTimeOffset? Timestamp { get; set; }
    public long? MarketId { get; set; }
    public string StationName { get; set; } = "";
    public string StarSystem { get; set; } = "";
    public List<MarketCommodityItem> Items { get; set; } = new();
    public bool Available => Timestamp.HasValue;
    [JsonIgnore]
    public string TimestampDisplay => Timestamp?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "---";
    [JsonIgnore]
    public string StatusDisplay => SnapshotFormatting.GetStatus(Available, StationName, StarSystem, TimestampDisplay, "No hay datos de mercado disponibles.");
}

public class MarketCommodityItem
{
    internal MarketCommodityItem Snapshot() => (MarketCommodityItem)MemberwiseClone();
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public string DisplayCategory { get; set; } = "";
    public long BuyPrice { get; set; }
    public long SellPrice { get; set; }
    public long MeanPrice { get; set; }
    public long Stock { get; set; }
    public long Demand { get; set; }
    public int StockBracket { get; set; }
    public int DemandBracket { get; set; }
    public bool Consumer { get; set; }
    public bool Producer { get; set; }
    public bool Rare { get; set; }
    public long ColonisationNeeded { get; set; }
    [JsonIgnore]
    public string ColonisationDisplay => ColonisationNeeded > 0
        ? $"🏗 {ColonisationNeeded.ToString("N0", CultureInfo.GetCultureInfo("es-ES"))} t"
        : "—";
    [JsonIgnore]
    public string BuyPriceDisplay => $"{BuyPrice.ToString("N0", CultureInfo.GetCultureInfo("es-ES"))} CR";
    [JsonIgnore]
    public string SellPriceDisplay => $"{SellPrice.ToString("N0", CultureInfo.GetCultureInfo("es-ES"))} CR";
    [JsonIgnore]
    public string MeanPriceDisplay => $"{MeanPrice.ToString("N0", CultureInfo.GetCultureInfo("es-ES"))} CR";
    [JsonIgnore]
    public string StockDisplay => Stock.ToString("N0", CultureInfo.GetCultureInfo("es-ES"));
    [JsonIgnore]
    public string DemandDisplay => Demand.ToString("N0", CultureInfo.GetCultureInfo("es-ES"));
    [JsonIgnore]
    public string StockBracketDisplay => StockBracket > 0 ? StockBracket.ToString(CultureInfo.GetCultureInfo("es-ES")) : "—";
    [JsonIgnore]
    public string DemandBracketDisplay => DemandBracket > 0 ? DemandBracket.ToString(CultureInfo.GetCultureInfo("es-ES")) : "—";
    [JsonIgnore]
    public string FlagsDisplay
    {
        get
        {
            string display = string.Join(" · ", new[]
            {
                Consumer ? "Consumidor" : "",
                Producer ? "Productor" : "",
                Rare ? "Rara" : ""
            }.Where(value => value.Length > 0));
            return display.Length == 0 ? "—" : display;
        }
    }
}

public class OutfittingSnapshot
{
    internal OutfittingSnapshot Snapshot()
    {
        var copy = (OutfittingSnapshot)MemberwiseClone();
        copy.Items = Items.Select(item => item.Snapshot()).ToList();
        return copy;
    }

    public DateTimeOffset? Timestamp { get; set; }
    public long? MarketId { get; set; }
    public string StationName { get; set; } = "";
    public string StarSystem { get; set; } = "";
    public bool? Horizons { get; set; }
    public List<OutfittingItem> Items { get; set; } = new();
    public bool Available => Timestamp.HasValue;
    [JsonIgnore]
    public string TimestampDisplay => Timestamp?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "---";
    [JsonIgnore]
    public string StatusDisplay => SnapshotFormatting.GetStatus(Available, StationName, StarSystem, TimestampDisplay, "No hay datos de equipamiento disponibles.");
}

public class OutfittingItem
{
    internal OutfittingItem Snapshot() => (OutfittingItem)MemberwiseClone();
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long BuyPrice { get; set; }
    [JsonIgnore]
    public string BuyPriceDisplay => $"{BuyPrice.ToString("N0", CultureInfo.GetCultureInfo("es-ES"))} CR";
}

public class ShipyardSnapshot
{
    internal ShipyardSnapshot Snapshot()
    {
        var copy = (ShipyardSnapshot)MemberwiseClone();
        copy.Items = Items.Select(item => item.Snapshot()).ToList();
        return copy;
    }

    public DateTimeOffset? Timestamp { get; set; }
    public long? MarketId { get; set; }
    public string StationName { get; set; } = "";
    public string StarSystem { get; set; } = "";
    public bool? Horizons { get; set; }
    public bool? AllowCobraMkIv { get; set; }
    public List<ShipyardItem> Items { get; set; } = new();
    public bool Available => Timestamp.HasValue;
    [JsonIgnore]
    public string TimestampDisplay => Timestamp?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "---";
    [JsonIgnore]
    public string StatusDisplay => SnapshotFormatting.GetStatus(Available, StationName, StarSystem, TimestampDisplay, "No hay datos de astillero disponibles.");
}

public class ShipyardItem
{
    internal ShipyardItem Snapshot() => (ShipyardItem)MemberwiseClone();
    public string ShipType { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long ShipPrice { get; set; }
    [JsonIgnore]
    public string ShipPriceDisplay => $"{ShipPrice.ToString("N0", CultureInfo.GetCultureInfo("es-ES"))} CR";
}

internal static class SnapshotFormatting
{
    internal static string GetStatus(
        bool available,
        string stationName,
        string starSystem,
        string timestamp,
        string unavailableText)
    {
        if (!available)
            return unavailableText;
        string location = string.Join(" · ", new[] { stationName, starSystem }.Where(value => !string.IsNullOrWhiteSpace(value)));
        string prefix = location.Length == 0 ? "" : $"{location} · ";
        return $"{prefix}Actualizado: {timestamp}. Datos de la última visita; pueden estar desactualizados.";
    }
}

public class JournalEntryItem
{
    internal JournalEntryItem Snapshot() => (JournalEntryItem)MemberwiseClone();
    public string Timestamp { get; set; } = "";
    public string EventType { get; set; } = "";
    [JsonIgnore]
    public string Summary { get; set; } = "";
    public string SummaryKey { get; set; } = "";
    public object?[] SummaryArguments { get; set; } = Array.Empty<object?>();
}

public class GameBinding
{
    public string ActionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public string IconGlyph { get; set; } = "";
    public string PrimaryDevice { get; set; } = "";
    public string PrimaryKey { get; set; } = "";
    public List<string> PrimaryModifiers { get; set; } = new();
    public string SecondaryDevice { get; set; } = "";
    public string SecondaryKey { get; set; } = "";
    public List<string> SecondaryModifiers { get; set; } = new();

    public bool HasKeyboardBind =>
        (PrimaryDevice == "Keyboard" && !string.IsNullOrEmpty(PrimaryKey)) ||
        (SecondaryDevice == "Keyboard" && !string.IsNullOrEmpty(SecondaryKey));

    public string FormattedBind
    {
        get
        {
            if (PrimaryDevice == "Keyboard" && !string.IsNullOrEmpty(PrimaryKey))
            {
                return FormatKeyCombo(PrimaryKey, PrimaryModifiers);
            }
            if (SecondaryDevice == "Keyboard" && !string.IsNullOrEmpty(SecondaryKey))
            {
                return FormatKeyCombo(SecondaryKey, SecondaryModifiers);
            }
            if (!string.IsNullOrEmpty(PrimaryDevice) && PrimaryDevice != "{NoDevice}")
            {
                return $"{PrimaryDevice}: {CleanKeyName(PrimaryKey)}";
            }
            if (!string.IsNullOrEmpty(SecondaryDevice) && SecondaryDevice != "{NoDevice}")
            {
                return $"{SecondaryDevice}: {CleanKeyName(SecondaryKey)}";
            }
            return "Sin asignar";
        }
    }

    private static string FormatKeyCombo(string key, List<string> modifiers)
    {
        var parts = new List<string>();
        foreach (var mod in modifiers)
        {
            parts.Add(CleanKeyName(mod));
        }
        parts.Add(CleanKeyName(key));
        return string.Join(" + ", parts);
    }

    public static string CleanKeyName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        return raw.Replace("Key_", "");
    }
}

public sealed class UnhandledGameMessageItem
{
    public string Timestamp { get; set; } = "";
    public string Source { get; set; } = "";
    public string EventType { get; set; } = "";
    public int Count { get; set; } = 1;
    public string RawJson { get; set; } = "";

    internal UnhandledGameMessageItem Snapshot() => (UnhandledGameMessageItem)MemberwiseClone();
}

public sealed class ShipModuleItem
{
    public string Slot { get; set; } = "";
    public string Item { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool On { get; set; } = true;
    public int Priority { get; set; } = 1;
    public double HealthPercent { get; set; } = 100;
    public double PowerDrawMw { get; set; }
    public int? AmmoInClip { get; set; }
    public int? AmmoInHopper { get; set; }
    public string Engineering { get; set; } = "";
    [JsonIgnore]
    public bool IsVital => Slot.Equals("PowerPlant", StringComparison.OrdinalIgnoreCase) ||
        Slot.Equals("MainEngines", StringComparison.OrdinalIgnoreCase) ||
        Slot.Equals("FrameShiftDrive", StringComparison.OrdinalIgnoreCase) ||
        Slot.Equals("LifeSupport", StringComparison.OrdinalIgnoreCase) ||
        Slot.Equals("PowerDistributor", StringComparison.OrdinalIgnoreCase) ||
        Item.Contains("shieldgenerator", StringComparison.OrdinalIgnoreCase);
    public string AmmoDisplay => (AmmoInClip, AmmoInHopper) switch
    {
        (int c, int h) => $"{c} / {h}",
        (int c, null) => c.ToString(CultureInfo.InvariantCulture),
        (null, int h) => h.ToString(CultureInfo.InvariantCulture),
        _ => "—"
    };
    public string HealthDisplay => $"{HealthPercent:0}%";
    public string PowerDisplay => PowerDrawMw > 0 ? $"{PowerDrawMw:0.00} MW" : "—";
    public string PriorityDisplay => Priority > 0 ? $"P{Priority}" : "—";

    internal ShipModuleItem Snapshot() => (ShipModuleItem)MemberwiseClone();
}

public sealed class EngineerProgressItem
{
    public string Name { get; set; } = "";
    public string Progress { get; set; } = "";
    public int? Rank { get; set; }
    public int? RankProgress { get; set; }
    public string RankDisplay => Rank.HasValue
        ? (RankProgress.HasValue ? $"{Rank.Value} ({RankProgress.Value}%)" : Rank.Value.ToString(CultureInfo.InvariantCulture))
        : "—";

    internal EngineerProgressItem Snapshot() => (EngineerProgressItem)MemberwiseClone();
}

public sealed class DeckMacroDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Steps { get; set; } = "";
}
