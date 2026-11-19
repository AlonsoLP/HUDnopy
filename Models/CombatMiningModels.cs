// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Models/CombatMiningModels.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Domain models for prospecting, mining sessions, combat targets, and massacre stacks.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace HUDnopy.Models;

/// <summary>
/// Domain models for prospecting, mining sessions, combat targets, and massacre stacks.
/// </summary>
public sealed class ProspectedMaterialItem
{
    public string Name { get; set; } = "";
    public double Proportion { get; set; }
    public string ProportionDisplay => $"{Proportion:0.0}%";
    internal ProspectedMaterialItem Snapshot() => (ProspectedMaterialItem)MemberwiseClone();
}

public sealed class ProspectedAsteroidState
{
    public string Timestamp { get; set; } = "";
    public string Content { get; set; } = "";
    public string MotherlodeMaterial { get; set; } = "";
    public double Remaining { get; set; } = 100;
    public List<ProspectedMaterialItem> Materials { get; set; } = new();
    public string SummaryDisplay
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(MotherlodeMaterial))
                parts.Add($"★ {MotherlodeMaterial}");
            if (!string.IsNullOrWhiteSpace(Content))
                parts.Add(Content);
            parts.AddRange(Materials.Select(static m => $"{m.Name} {m.Proportion:0.0}%"));
            return string.Join(" · ", parts);
        }
    }

    internal ProspectedAsteroidState Snapshot()
    {
        var copy = (ProspectedAsteroidState)MemberwiseClone();
        copy.Materials = Materials.Select(static m => m.Snapshot()).ToList();
        return copy;
    }
}

public sealed class MiningRefinedItem
{
    public string Mineral { get; set; } = "";
    public int Tons { get; set; }
    internal MiningRefinedItem Snapshot() => (MiningRefinedItem)MemberwiseClone();
}

public sealed class MiningSessionState
{
    public DateTimeOffset? StartedAtUtc { get; set; }
    public int TotalRefinedTons { get; set; }
    public int AsteroidsProspected { get; set; }
    public double TonsPerHour { get; set; }
    public List<MiningRefinedItem> RefinedByMineral { get; set; } = new();

    internal MiningSessionState Snapshot()
    {
        var copy = (MiningSessionState)MemberwiseClone();
        copy.RefinedByMineral = RefinedByMineral.Select(static m => m.Snapshot()).ToList();
        return copy;
    }
}

public sealed class TargetShipState
{
    public string Ship { get; set; } = "";
    public string PilotName { get; set; } = "";
    public string PilotRank { get; set; } = "";
    public string Faction { get; set; } = "";
    public string LegalStatus { get; set; } = "";
    public long Bounty { get; set; }
    public double? ShieldHealth { get; set; }
    public double? HullHealth { get; set; }
    public string Subsystem { get; set; } = "";
    public double? SubsystemHealth { get; set; }
    public int ScanStage { get; set; }
    public bool IsWanted => LegalStatus.Equals("Wanted", StringComparison.OrdinalIgnoreCase);

    internal TargetShipState Snapshot() => (TargetShipState)MemberwiseClone();
}

public sealed class MassacreStackItem
{
    public string IssuingFaction { get; set; } = "";
    public string TargetFaction { get; set; } = "";
    public int MissionCount { get; set; }
    public int RequiredKills { get; set; }
    public long TotalReward { get; set; }
    public string RewardDisplay => $"{TotalReward:N0} CR";

    internal MassacreStackItem Snapshot() => (MassacreStackItem)MemberwiseClone();
}

public sealed class CombatFactionItem
{
    public string Faction { get; set; } = "";
    public int Kills { get; set; }
    public long PendingCredits { get; set; }
    public string CreditsDisplay => $"{PendingCredits:N0} CR";

    internal CombatFactionItem Snapshot() => (CombatFactionItem)MemberwiseClone();
}

public sealed class CombatSessionState
{
    public DateTimeOffset? StartedAtUtc { get; set; }
    public int TotalKills { get; set; }
    public long PendingCredits { get; set; }
    public long CreditsPerHour { get; set; }
    public List<CombatFactionItem> ByFaction { get; set; } = new();

    internal CombatSessionState Snapshot()
    {
        var copy = (CombatSessionState)MemberwiseClone();
        copy.ByFaction = ByFaction.Select(static f => f.Snapshot()).ToList();
        return copy;
    }
}
