// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Models/FleetAndGoalsModels.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Domain models for stored ships, Community Goals, carrier cargo, BGS, and session stats.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace HUDnopy.Models;

/// <summary>
/// Domain models for stored ships, Community Goals, carrier cargo, BGS, and session stats.
/// </summary>
public sealed class CarrierMaterialItem
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long Price { get; set; }
    public int Stock { get; set; }
    public int Demand { get; set; }
    public string PriceDisplay => $"{Price:N0} CR";

    internal CarrierMaterialItem Snapshot() => (CarrierMaterialItem)MemberwiseClone();
}

public sealed class SystemFactionItem
{
    public string Name { get; set; } = "";
    public double InfluencePercent { get; set; }
    public string InfluenceDisplay => $"{InfluencePercent:0.0}%";
    public string FactionState { get; set; } = "";
    public string Government { get; set; } = "";
    public string Allegiance { get; set; } = "";
    public double? MyReputation { get; set; }
    public string ReputationDisplay => MyReputation.HasValue ? $"{MyReputation.Value:0}%" : "—";

    internal SystemFactionItem Snapshot() => (SystemFactionItem)MemberwiseClone();
}

public sealed class SessionStatsState
{
    public DateTimeOffset? StartedAtUtc { get; set; }
    public long? StartCredits { get; set; }
    public long NetCredits { get; set; }
    public long CreditsPerHour { get; set; }
    public int SystemsVisited { get; set; }
    public double TotalDistanceLy { get; set; }
    public int BodiesScanned { get; set; }
    public int OrganicsAnalysed { get; set; }
    public int MissionsCompleted { get; set; }

    internal SessionStatsState Snapshot() => (SessionStatsState)MemberwiseClone();
}

public sealed class StoredShipItem
{
    public long Id { get; set; }
    public string ShipType { get; set; } = "";
    public string ShipName { get; set; } = "";
    public string StarSystem { get; set; } = "";
    public string StationName { get; set; } = "";
    public long Value { get; set; }
    public string ValueDisplay => $"{Value:N0} CR";

    internal StoredShipItem Snapshot() => (StoredShipItem)MemberwiseClone();
}

public sealed class CommunityGoalItem
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string StarSystem { get; set; } = "";
    public string StationName { get; set; } = "";
    public string ExpiryDisplay { get; set; } = "";
    public long CurrentTotal { get; set; }
    public long PlayerContribution { get; set; }
    public string ProgressDisplay => PlayerContribution > 0
        ? $"{CurrentTotal:N0} (tú: {PlayerContribution:N0})"
        : $"{CurrentTotal:N0}";

    internal CommunityGoalItem Snapshot() => (CommunityGoalItem)MemberwiseClone();
}

public sealed class CarrierCargoItem
{
    public string Commodity { get; set; } = "";
    public int Quantity { get; set; }
    public bool Stolen { get; set; }
    public string OriginSystem { get; set; } = "";

    internal CarrierCargoItem Snapshot() => (CarrierCargoItem)MemberwiseClone();
}
