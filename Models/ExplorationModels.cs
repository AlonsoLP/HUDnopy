// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Models/ExplorationModels.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Domain models for celestial bodies, exobiology tracking, and surface bookmarks.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace HUDnopy.Models;

/// <summary>
/// Domain models for celestial bodies, exobiology tracking, and surface bookmarks.
/// </summary>
public class ExplorationBodyItem
{
    internal ExplorationBodyItem Snapshot()
    {
        var copy = (ExplorationBodyItem)MemberwiseClone();
        copy.Genuses = new List<string>(Genuses);
        copy.AnalysedSpecies = new List<string>(AnalysedSpecies);
        copy.Materials = Materials.Select(item => item.Snapshot()).ToList();
        copy.AtmosphereComposition = AtmosphereComposition.Select(item => item.Snapshot()).ToList();
        copy.Composition = Composition.Snapshot();
        copy.Rings = Rings.Select(item => item.Snapshot()).ToList();
        copy.Anomalies = Anomalies.ToList();
        return copy;
    }
    public string Id { get; set; } = "";
    public int? BodyId { get; set; }
    public int HierarchyDepth { get; set; }
    public string SystemName { get; set; } = "";
    public string BodyName { get; set; } = "";
    public string HierarchyName => HierarchyDepth <= 0 ? BodyName : $"{new string(' ', Math.Min(HierarchyDepth, 4) * 2)}└─ {BodyName}";
    public string PlanetClass { get; set; } = "";
    public string StarType { get; set; } = "";
    public double? MassEarth { get; set; }
    public double? StellarMass { get; set; }
    public double? RadiusMeters { get; set; }
    public double? SurfaceGravity { get; set; }
    public double? SurfaceTemperatureKelvin { get; set; }
    public double? SurfacePressure { get; set; }
    public double? AgeMillionsOfYears { get; set; }
    public double? DistanceFromArrivalLs { get; set; }
    public double? RotationPeriodSeconds { get; set; }
    public double? OrbitalPeriodSeconds { get; set; }
    public double? SemiMajorAxisMeters { get; set; }
    public double? Eccentricity { get; set; }
    public double? OrbitalInclination { get; set; }
    public double? Periapsis { get; set; }
    public double? AxialTilt { get; set; }
    public bool? TidalLock { get; set; }
    public string BodyType => !string.IsNullOrWhiteSpace(PlanetClass) ? PlanetClass : StarType;
    public string TerraformState { get; set; } = "";
    public string Atmosphere { get; set; } = "";
    public string AtmosphereType { get; set; } = "";
    public string Volcanism { get; set; } = "";
    public bool? Landable { get; set; }
    public bool? WasDiscovered { get; set; }
    public bool? WasMapped { get; set; }
    public bool HasCompletedMapping { get; set; }
    public bool? MappingWasEfficient { get; set; }
    public long? EstimatedCredits { get; set; }
    public string RewardEstimateBasis { get; set; } = "";
    public int BiologicalSignalCount { get; set; }
    public int GeologicalSignalCount { get; set; }
    public List<string> Genuses { get; set; } = new();
    public List<string> AnalysedSpecies { get; set; } = new();
    public string PredictedExobiology { get; set; } = "";
    public long? EstimatedExobiologyCredits { get; set; }
    public List<string> Anomalies { get; set; } = new();
    public bool IsVirginBioLandable => Landable == true && WasDiscovered == false && BiologicalSignalCount > 0;
    public List<ExplorationMaterialItem> Materials { get; set; } = new();
    public List<ExplorationMaterialItem> AtmosphereComposition { get; set; } = new();
    public ExplorationComposition Composition { get; set; } = new();
    public List<ExplorationRingItem> Rings { get; set; } = new();
    [JsonIgnore]
    public string RadiusDisplay => RadiusMeters.HasValue
        ? $"{(RadiusMeters.Value / 1000).ToString("N0", ShipState.EsCulture)} km"
        : "---";
    [JsonIgnore]
    public string SurfaceGravityDisplay => SurfaceGravity.HasValue
        ? $"{SurfaceGravity.Value.ToString("0.##", ShipState.EsCulture)} m/s²"
        : "---";
    [JsonIgnore]
    public string SurfaceTemperatureDisplay => SurfaceTemperatureKelvin.HasValue
        ? $"{SurfaceTemperatureKelvin.Value.ToString("N0", ShipState.EsCulture)} K"
        : "---";
    public string AtmosphereDisplay => !string.IsNullOrWhiteSpace(AtmosphereType) ? AtmosphereType
        : !string.IsNullOrWhiteSpace(Atmosphere) ? Atmosphere
        : "---";
    [JsonIgnore]
    public string MaterialsDisplay => Materials.Count == 0
        ? "---"
        : string.Join(", ", Materials.Select(material =>
            $"{material.Name} {material.Percent.ToString("0.##", ShipState.EsCulture)}%"));
    public string RingsDisplay => Rings.Count == 0
        ? "---"
        : string.Join(", ", Rings.Select(ring => string.IsNullOrWhiteSpace(ring.Name) ? ring.RingClass : ring.Name));
    [JsonIgnore]
    public string CompositionDisplay => JoinDisplay(new[]
    {
        FormatComposition("Hielo", Composition.Ice),
        FormatComposition("Roca", Composition.Rock),
        FormatComposition("Metal", Composition.Metal)
    });
    [JsonIgnore]
    public string AtmosphereCompositionDisplay => AtmosphereComposition.Count == 0
        ? "---"
        : string.Join(", ", AtmosphereComposition.Select(item =>
            $"{item.Name} {item.Percent.ToString("0.##", ShipState.EsCulture)}%"));
    [JsonIgnore]
    public string OrbitalDetailsDisplay => JoinDisplay(new[]
    {
        AgeMillionsOfYears.HasValue ? $"Edad: {FormatExplorationNumber(AgeMillionsOfYears)} Ma" : "",
        RotationPeriodSeconds.HasValue ? $"Rotación: {FormatExplorationNumber(RotationPeriodSeconds)} s" : "",
        OrbitalPeriodSeconds.HasValue ? $"Órbita: {FormatExplorationNumber(OrbitalPeriodSeconds)} s" : "",
        DistanceFromArrivalLs.HasValue ? $"Distancia de llegada: {FormatExplorationNumber(DistanceFromArrivalLs)} LS" : "",
        SemiMajorAxisMeters.HasValue ? $"Semieje mayor: {FormatExplorationNumber(SemiMajorAxisMeters)} m" : "",
        Eccentricity.HasValue ? $"Excentricidad: {FormatExplorationNumber(Eccentricity)}" : "",
        OrbitalInclination.HasValue ? $"Inclinación: {FormatExplorationNumber(OrbitalInclination)}°" : "",
        AxialTilt.HasValue ? $"Inclinación axial: {FormatExplorationNumber(AxialTilt)}°" : "",
        SurfacePressure.HasValue ? $"Presión superficial: {FormatExplorationNumber(SurfacePressure)}" : "",
        !string.IsNullOrWhiteSpace(Volcanism) ? $"Volcanismo: {Volcanism}" : "",
        TidalLock.HasValue ? $"Bloqueo de marea: {(TidalLock.Value ? "Sí" : "No")}" : ""
    });
    public string GenusesDisplay => string.Join(", ", Genuses);
    public string AnalysedSpeciesDisplay => string.Join(", ", AnalysedSpecies);
    [JsonIgnore]
    public string DiscoveryStatus => WasDiscovered switch
    {
        false => "Primer descubrimiento",
        true => "Ya descubierto",
        _ => "Sin dato"
    };
    public string DiscoveryIndicator => WasDiscovered switch
    {
        false => "✗",
        true => "✓",
        _ => "—"
    };
    [JsonIgnore]
    public string MappingStatus => WasMapped switch
    {
        false => "Sin mapear",
        true => "Ya mapeado",
        _ => "Sin dato"
    };
    public string MappingIndicator => WasMapped switch
    {
        false => "✗",
        true => "✓",
        _ => "—"
    };
    [JsonIgnore]
    public string EstimatedCreditsDisplay => EstimatedCredits.HasValue
        ? $"{EstimatedCredits.Value:N0} CR"
        : "Sin estimación";
    [JsonIgnore]
    public string InterestReasons => string.Join(" · ", GetInterestReasons());

    private static string FormatComposition(string name, double? value) =>
        value.HasValue
            ? $"{name}: {(value.Value * 100).ToString("0.#", ShipState.EsCulture)}%"
            : "";

    private static string FormatExplorationNumber(double? value) =>
        value?.ToString("N0", ShipState.EsCulture) ?? "";

    private static string JoinDisplay(IEnumerable<string> values)
    {
        string display = string.Join(" · ", values.Where(value => value.Length > 0));
        return display.Length == 0 ? "—" : display;
    }

    private IEnumerable<string> GetInterestReasons()
    {
        if (StarType.Equals("N", StringComparison.OrdinalIgnoreCase))
            yield return "Estrella de neutrones";
        else if (StarType.Equals("H", StringComparison.OrdinalIgnoreCase))
            yield return "Agujero negro";
        else if (StarType.StartsWith("D", StringComparison.OrdinalIgnoreCase))
            yield return "Enana blanca";
        else if (StarType.Equals("W", StringComparison.OrdinalIgnoreCase))
            yield return "Estrella Wolf-Rayet";

        if (PlanetClass.Contains("Earth-like", StringComparison.OrdinalIgnoreCase))
            yield return "Mundo tipo Tierra";
        else if (PlanetClass.Contains("Water world", StringComparison.OrdinalIgnoreCase))
            yield return "Mundo acuático";
        else if (PlanetClass.Contains("Ammonia world", StringComparison.OrdinalIgnoreCase))
            yield return "Mundo de amoníaco";

        if (!string.IsNullOrWhiteSpace(TerraformState) &&
            !TerraformState.Equals("Not terraformable", StringComparison.OrdinalIgnoreCase))
            yield return TerraformState;
        if (HasCompletedMapping)
            yield return "Cartografía DSS completada";
        if (BiologicalSignalCount > 0)
            yield return $"{BiologicalSignalCount} señales biológicas";
        if (IsVirginBioLandable)
            yield return "Primera pisada potencial (×5)";
        if (!string.IsNullOrWhiteSpace(PredictedExobiology))
            yield return PredictedExobiology;
        if (GeologicalSignalCount > 0)
            yield return $"{GeologicalSignalCount} señales geológicas";
        foreach (string anomaly in Anomalies)
            yield return anomaly;
    }

    public bool IsInteresting => !string.IsNullOrWhiteSpace(InterestReasons);
}

public class ExplorationMaterialItem
{
    internal ExplorationMaterialItem Snapshot() => (ExplorationMaterialItem)MemberwiseClone();
    public string Name { get; set; } = "";
    public double Percent { get; set; }
}

public class ExplorationComposition
{
    internal ExplorationComposition Snapshot() => (ExplorationComposition)MemberwiseClone();
    public double? Ice { get; set; }
    public double? Rock { get; set; }
    public double? Metal { get; set; }
}

public class ExplorationRingItem
{
    internal ExplorationRingItem Snapshot() => (ExplorationRingItem)MemberwiseClone();
    public string Name { get; set; } = "";
    public string RingClass { get; set; } = "";
    public double? MassMegatons { get; set; }
    public double? InnerRadiusMeters { get; set; }
    public double? OuterRadiusMeters { get; set; }
}

public sealed class ExobiologyTrackerState
{
    public string Species { get; set; } = "";
    public string Genus { get; set; } = "";
    public string BodyName { get; set; } = "";
    public int SamplesCollected { get; set; }
    public int RequiredDistanceMeters { get; set; } = 500;
    public double CurrentDistanceMeters { get; set; }
    public double? LastSampleLatitude { get; set; }
    public double? LastSampleLongitude { get; set; }
    public bool CanSampleNext => SamplesCollected is > 0 and < 3 && CurrentDistanceMeters >= RequiredDistanceMeters;

    internal ExobiologyTrackerState Snapshot() => (ExobiologyTrackerState)MemberwiseClone();
}

public sealed class SurfaceBookmarkItem
{
    public string SystemName { get; set; } = "";
    public string BodyName { get; set; } = "";
    public string Label { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string CreatedAt { get; set; } = "";
    [JsonIgnore]
    public string CoordinatesDisplay => $"{Latitude:0.0000}°, {Longitude:0.0000}°";
}

public sealed class SurfaceEventItem
{
    public string Timestamp { get; set; } = "";
    public string EventType { get; set; } = "";
    public string BodyName { get; set; } = "";
    public string Details { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string CoordinatesDisplay => Latitude.HasValue && Longitude.HasValue
        ? $"{Latitude.Value:0.0000}°, {Longitude.Value:0.0000}°"
        : "—";

    internal SurfaceEventItem Snapshot() => (SurfaceEventItem)MemberwiseClone();
}
