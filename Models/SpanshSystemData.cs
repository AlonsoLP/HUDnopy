// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Models/SpanshSystemData.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Models for Spansh system intelligence and enrichment states.
// -----------------------------------------------------------------------------

namespace HUDnopy.Models;

/// <summary>
/// Models for Spansh system intelligence and enrichment states.
/// </summary>
public enum SpanshSystemDataStatus
{
    Disabled,
    WaitingForSystem,
    Loading,
    Ready,
    Error
}

public sealed record SpanshSystemDataState(
    SpanshSystemDataStatus Status,
    SpanshSystemSnapshot? Snapshot,
    bool IsStale);

public sealed class SpanshSystemSnapshot
{
    public ulong Id64 { get; set; }
    public string Name { get; set; } = "";
    public int BodyCount { get; set; }
    public int StationCount { get; set; }
    public DateTimeOffset FetchedAtUtc { get; set; }
    public List<SpanshBodySummary> Bodies { get; set; } = new();
    public List<SpanshStationSummary> Stations { get; set; } = new();
}

public sealed record SpanshBodySummary(
    string Name,
    string Type,
    double? DistanceToArrival,
    bool? IsLandable);

public sealed record SpanshStationSummary(
    string Name,
    string Type,
    string BodyName,
    double? DistanceToArrival,
    string ControllingFaction);

public sealed record SpanshBodyDisplay(
    string Name,
    string Type,
    string ArrivalDistance,
    string Landable);

public sealed record SpanshStationDisplay(
    string Name,
    string Type,
    string BodyName,
    string ArrivalDistance,
    string ControllingFaction);

public sealed record SpanshSystemDisplay(
    string Title,
    string Status,
    string Summary,
    string Updated,
    string SourceNote,
    string BodiesTitle,
    string StationsTitle,
    string EmptyBodies,
    string EmptyStations,
    string BodyHeader,
    string TypeHeader,
    string ArrivalHeader,
    string LandableHeader,
    string StationHeader,
    string StationBodyHeader,
    string FactionHeader,
    IReadOnlyList<SpanshBodyDisplay> Bodies,
    IReadOnlyList<SpanshStationDisplay> Stations);
