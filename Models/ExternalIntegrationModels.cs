// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Models/ExternalIntegrationModels.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Domain models for v0.3.0 external API integrations (Frontier cAPI
//              extended data, EDSM discovery intel, Inara profiles, EliteBGS
//              watchlist, Ardent Insight markets, EDAstro DSSA, and Spansh search).
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HUDnopy.Models;

/// <summary>
/// Stored ship module retrieved from Frontier cAPI (/profile).
/// </summary>
public sealed class StoredModuleItem
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string StarSystem { get; set; } = "";
    public string StationName { get; set; } = "";
    public long BuyPrice { get; set; }
    public string Engineering { get; set; } = "";
    public string PriceDisplay => BuyPrice > 0 ? $"{BuyPrice:N0} CR" : "—";

    internal StoredModuleItem Snapshot() => (StoredModuleItem)MemberwiseClone();
}

/// <summary>
/// Active Fleet Carrier commodity market order (buy or sell) retrieved from Frontier cAPI (/fleetcarrier).
/// </summary>
public sealed class CarrierMarketOrderItem
{
    public string Commodity { get; set; } = "";
    public string OrderType { get; set; } = ""; // "Sell" | "Buy"
    public int Quantity { get; set; }
    public long Price { get; set; }
    public string PriceDisplay => $"{Price:N0} CR";

    internal CarrierMarketOrderItem Snapshot() => (CarrierMarketOrderItem)MemberwiseClone();
}

/// <summary>
/// Commander profile snapshot fetched from Inara Read API (getCommanderProfile).
/// </summary>
public sealed class InaraCommanderProfile
{
    public string CommanderName { get; set; } = "";
    public string SquadronName { get; set; } = "";
    public string SquadronRank { get; set; } = "";
    public string PreferredAllegiance { get; set; } = "";
    public string InaraUrl { get; set; } = "";
    public DateTimeOffset FetchedAtUtc { get; set; }

    internal InaraCommanderProfile Snapshot() => (InaraCommanderProfile)MemberwiseClone();
}

/// <summary>
/// EDSM discovery status, known body count, estimated scan value, and faction intel for the current system.
/// </summary>
public sealed class EdsmSystemDiscoveryIntel
{
    public string SystemName { get; set; } = "";
    public bool IsKnownInEdsm { get; set; }
    public int KnownBodiesCount { get; set; }
    public long EstimatedScanValue { get; set; }
    public string ControllingFaction { get; set; } = "";
    public List<SystemFactionItem> Factions { get; set; } = new();
    public DateTimeOffset FetchedAtUtc { get; set; }

    public string DiscoveryBadge => IsKnownInEdsm
        ? $"EDSM: {KnownBodiesCount} bodies (~{EstimatedScanValue:N0} CR)"
        : "EDSM: UNDISCOVERED / NEW SYSTEM";

    internal EdsmSystemDiscoveryIntel Snapshot() => new()
    {
        SystemName = SystemName,
        IsKnownInEdsm = IsKnownInEdsm,
        KnownBodiesCount = KnownBodiesCount,
        EstimatedScanValue = EstimatedScanValue,
        ControllingFaction = ControllingFaction,
        Factions = Factions.Select(static f => f.Snapshot()).ToList(),
        FetchedAtUtc = FetchedAtUtc
    };
}

/// <summary>
/// Watched minor faction presence entry fetched from EliteBGS v5.
/// </summary>
public sealed class EliteBgsFactionWatchItem
{
    public string FactionName { get; set; } = "";
    public string SystemName { get; set; } = "";
    public double InfluencePercent { get; set; }
    public string State { get; set; } = "";
    public string ActiveStates { get; set; } = "";
    public string PendingStates { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public string InfluenceDisplay => $"{InfluencePercent:0.0}%";
    public bool HasConflict =>
        State.Contains("War", StringComparison.OrdinalIgnoreCase) ||
        State.Contains("Election", StringComparison.OrdinalIgnoreCase) ||
        ActiveStates.Contains("War", StringComparison.OrdinalIgnoreCase) ||
        ActiveStates.Contains("Election", StringComparison.OrdinalIgnoreCase) ||
        PendingStates.Contains("War", StringComparison.OrdinalIgnoreCase) ||
        PendingStates.Contains("Election", StringComparison.OrdinalIgnoreCase);

    internal EliteBgsFactionWatchItem Snapshot() => (EliteBgsFactionWatchItem)MemberwiseClone();
}

/// <summary>
/// Live commodity buy/sell offer returned by the Ardent Insight v2 open market API.
/// </summary>
public sealed record ArdentMarketOfferItem(
    string Commodity,
    string Mode, // "buy" | "sell"
    string SystemName,
    string StationName,
    double DistanceLy,
    long Price,
    long Quantity,
    string MaxLandingPad,
    string UpdatedDisplay)
{
    public string DistanceDisplay => $"{DistanceLy.ToString("0.0", CultureInfo.InvariantCulture)} LY";
    public string PriceDisplay => $"{Price:N0} CR";
    public string QuantityDisplay => $"{Quantity:N0}";
}

/// <summary>
/// Deep Space Support Array (DSSA) Fleet Carrier entry fetched from EDAstro.
/// </summary>
public sealed record DssaCarrierItem(
    string Callsign,
    string Name,
    string SystemName,
    string Region,
    string Status,
    double DistanceLy,
    double X,
    double Y,
    double Z)
{
    public string DistanceDisplay => $"{DistanceLy.ToString("0.0", CultureInfo.InvariantCulture)} LY";
}

/// <summary>
/// Planetary body search result from Spansh (/api/bodies/search).
/// </summary>
public sealed record SpanshBodySearchItem(
    string SystemName,
    string BodyName,
    string SubType,
    double DistanceLy,
    double? GravityG,
    bool IsLandable,
    string MaterialsSummary)
{
    public string DistanceDisplay => $"{DistanceLy.ToString("0.0", CultureInfo.InvariantCulture)} LY";
    public string GravityDisplay => GravityG.HasValue ? $"{GravityG.Value.ToString("0.00", CultureInfo.InvariantCulture)} G" : "—";
    public string LandableDisplay => IsLandable ? "✓" : "—";
}

/// <summary>
/// Fleet Carrier station search result from Spansh (/api/stations/search).
/// </summary>
public sealed record SpanshCarrierSearchItem(
    string Callsign,
    string SystemName,
    double DistanceLy,
    string ServicesSummary,
    string UpdatedDisplay)
{
    public string DistanceDisplay => $"{DistanceLy.ToString("0.0", CultureInfo.InvariantCulture)} LY";
}
