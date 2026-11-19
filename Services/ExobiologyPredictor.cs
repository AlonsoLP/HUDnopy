// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/ExobiologyPredictor.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Heuristic predictor for likely Odyssey exobiology genera from body scan properties.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HUDnopy.Models;

namespace HUDnopy.Services;

/// <summary>
/// Heuristic predictor for likely Odyssey exobiology genera from body scan properties.
/// </summary>
internal static class ExobiologyPredictor
{
    private static readonly (string Genus, int DistanceMeters, long BaseCredits)[] GenusCatalog =
    {
        ("Aleoida", 150, 3_385_200),
        ("Bacterium", 500, 1_000_000),
        ("Cactoida", 300, 3_667_600),
        ("Clypeus", 150, 8_418_000),
        ("Concha", 150, 7_774_700),
        ("Electricae", 1000, 6_284_600),
        ("Fonticulua", 500, 1_901_080),
        ("Frutexa", 150, 1_632_500),
        ("Fumerola", 100, 7_528_000),
        ("Fungoida", 300, 2_680_300),
        ("Osseus", 800, 3_156_300),
        ("Recepta", 150, 12_934_900),
        ("Stratum", 500, 19_010_800),
        ("Tubus", 800, 5_727_600),
        ("Tussock", 200, 2_832_500),
        ("Anemone", 100, 1_499_900),
        ("Brain Tree", 100, 1_593_700),
        ("Bark Mound", 100, 1_471_900),
        ("Crystalline", 100, 1_628_800),
        ("Amphora", 100, 3_626_400),
        ("Sinuous Tubers", 100, 1_514_500)
    };

    public static int GetRequiredDistanceMeters(string? genusOrSpecies)
    {
        if (string.IsNullOrWhiteSpace(genusOrSpecies))
            return 500;

        foreach (var entry in GenusCatalog)
        {
            if (genusOrSpecies.Contains(entry.Genus, StringComparison.OrdinalIgnoreCase))
                return entry.DistanceMeters;
        }

        return 500;
    }

    public static long EstimateSpeciesCredits(string? genusOrSpecies, bool firstFootfall)
    {
        long baseValue = 1_900_000;
        if (!string.IsNullOrWhiteSpace(genusOrSpecies))
        {
            if (genusOrSpecies.Contains("Tectonicas", StringComparison.OrdinalIgnoreCase))
                baseValue = 19_010_800;
            else if (genusOrSpecies.Contains("Clypeus", StringComparison.OrdinalIgnoreCase) &&
                     genusOrSpecies.Contains("Speculumi", StringComparison.OrdinalIgnoreCase))
                baseValue = 16_202_800;
            else if (genusOrSpecies.Contains("Fonticulua", StringComparison.OrdinalIgnoreCase) &&
                     genusOrSpecies.Contains("Fluctus", StringComparison.OrdinalIgnoreCase))
                baseValue = 20_000_000;
            else if (genusOrSpecies.Contains("Recepta", StringComparison.OrdinalIgnoreCase) &&
                     genusOrSpecies.Contains("Deltahedronix", StringComparison.OrdinalIgnoreCase))
                baseValue = 16_202_800;
            else if (genusOrSpecies.Contains("Bacterium", StringComparison.OrdinalIgnoreCase) &&
                     genusOrSpecies.Contains("Informem", StringComparison.OrdinalIgnoreCase))
                baseValue = 8_418_000;
            else
            {
                foreach (var entry in GenusCatalog)
                {
                    if (genusOrSpecies.Contains(entry.Genus, StringComparison.OrdinalIgnoreCase))
                    {
                        baseValue = entry.BaseCredits;
                        break;
                    }
                }
            }
        }

        return firstFootfall ? baseValue * 5 : baseValue;
    }

    public static double CalculateSurfaceDistanceMeters(
        double lat1Deg, double lon1Deg,
        double lat2Deg, double lon2Deg,
        double radiusMeters) =>
        SurfaceNavigationService.CalculateDistanceMeters(lat1Deg, lon1Deg, lat2Deg, lon2Deg, radiusMeters);

    public static double CalculateSurfaceBearingDegrees(
        double lat1Deg, double lon1Deg,
        double lat2Deg, double lon2Deg) =>
        Math.Round(SurfaceNavigationService.CalculateBearingDegrees(lat1Deg, lon1Deg, lat2Deg, lon2Deg), 0) % 360;

    public static void UpdateBodyInsights(ExplorationBodyItem body)
    {
        body.Anomalies = DetectAnomalies(body);
        UpdateExobiologyPrediction(body);
    }

    private static List<string> DetectAnomalies(ExplorationBodyItem body)
    {
        var anomalies = new List<string>();
        if (body.Landable == true && body.SurfaceGravity.HasValue && body.SurfaceGravity.Value >= 19.6133)
        {
            double g = body.SurfaceGravity.Value / 9.80665;
            anomalies.Add($"Alta gravedad ({g.ToString("0.0", CultureInfo.InvariantCulture)} G)");
        }
        if (body.Eccentricity.HasValue && body.Eccentricity.Value >= 0.85)
            anomalies.Add($"Órbita excéntrica ({body.Eccentricity.Value.ToString("0.00", CultureInfo.InvariantCulture)})");
        if (body.Rings.Any(static r => r.OuterRadiusMeters.HasValue && r.OuterRadiusMeters.Value >= 500_000_000))
            anomalies.Add("Anillo gigante");
        if (body.OrbitalPeriodSeconds.HasValue && body.OrbitalPeriodSeconds.Value > 0 && body.OrbitalPeriodSeconds.Value <= 28_800)
            anomalies.Add("Órbita ultrarrápida");
        if (body.Landable == true && !string.IsNullOrWhiteSpace(body.TerraformState) &&
            !body.TerraformState.Equals("Not terraformable", StringComparison.OrdinalIgnoreCase))
            anomalies.Add("Terraformable aterrizable");
        if (!string.IsNullOrWhiteSpace(body.PlanetClass) && body.DistanceFromArrivalLs.HasValue &&
            body.DistanceFromArrivalLs.Value > 0 && body.DistanceFromArrivalLs.Value <= 15)
            anomalies.Add("Órbita muy cercana a estrella");

        return anomalies;
    }

    private static void UpdateExobiologyPrediction(ExplorationBodyItem body)
    {
        body.PredictedExobiology = "";
        body.EstimatedExobiologyCredits = null;

        if (body.BiologicalSignalCount <= 0 || body.Landable != true)
            return;

        bool firstFootfall = body.WasDiscovered == false || body.WasMapped == false;

        if (body.Genuses.Count > 0)
        {
            long total = body.Genuses.Sum(g => EstimateSpeciesCredits(g, firstFootfall));
            body.EstimatedExobiologyCredits = total;
            body.PredictedExobiology = $"{string.Join(", ", body.Genuses)} (~{FormatMillions(total)}{(firstFootfall ? " ×5" : "")})";
            return;
        }

        var predicted = new List<(string Name, long BaseValue)>();
        string atmo = (body.AtmosphereType + " " + body.Atmosphere).ToLowerInvariant();
        string planet = body.PlanetClass.ToLowerInvariant();
        double temp = body.SurfaceTemperatureKelvin ?? 0;
        double gravG = body.SurfaceGravity.HasValue ? body.SurfaceGravity.Value / 9.80665 : 0;

        bool isHmc = planet.Contains("high metal content");
        bool isRocky = planet.Contains("rocky");
        bool isIcy = planet.Contains("icy");

        if (isHmc && temp >= 165 && (atmo.Contains("carbon") || atmo.Contains("ammonia") || atmo.Contains("sulphur") || atmo.Contains("sulfur") || atmo.Contains("water") || atmo.Contains("oxygen")))
            predicted.Add(("Stratum Tectonicas", 19_010_800));
        if (atmo.Contains("oxygen") && temp >= 190 && temp <= 200 && isIcy)
            predicted.Add(("Fonticulua Fluctus", 20_000_000));
        if (atmo.Contains("water") && temp >= 190)
            predicted.Add(("Clypeus", 8_418_000));
        if ((atmo.Contains("sulphur") || atmo.Contains("sulfur") || atmo.Contains("carbon")) && temp >= 190 && gravG < 0.27)
            predicted.Add(("Recepta", 12_934_900));
        if (atmo.Contains("argon") && isIcy && temp <= 150)
            predicted.Add(("Electricae", 6_284_600));
        if (!string.IsNullOrWhiteSpace(body.Volcanism) && !body.Volcanism.Equals("None", StringComparison.OrdinalIgnoreCase))
            predicted.Add(("Fumerola", 7_528_000));
        if (isRocky && temp >= 160 && temp <= 260)
            predicted.Add(("Tussock", 2_832_500));
        if (isRocky && temp >= 165 && (atmo.Contains("ammonia") || atmo.Contains("carbon")))
            predicted.Add(("Aleoida", 3_385_200));
        if (isHmc || isRocky)
            predicted.Add(("Osseus", 3_156_300));
        if (isIcy && (atmo.Contains("argon") || atmo.Contains("methane") || atmo.Contains("nitrogen")))
            predicted.Add(("Fonticulua", 1_901_080));

        predicted.Add(("Bacterium", 1_000_000));

        var top = predicted
            .GroupBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.First())
            .Take(Math.Max(body.BiologicalSignalCount, 3))
            .ToList();

        long estimated = top
            .OrderByDescending(static p => p.BaseValue)
            .Take(body.BiologicalSignalCount)
            .Sum(p => firstFootfall ? p.BaseValue * 5 : p.BaseValue);

        body.EstimatedExobiologyCredits = estimated;
        body.PredictedExobiology = $"{string.Join(", ", top.Select(static p => p.Name))} (~{FormatMillions(estimated)}{(firstFootfall ? " ×5" : "")})";
    }

    private static string FormatMillions(long credits)
    {
        if (credits >= 1_000_000)
            return $"{(credits / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture)} M CR";
        return $"{credits:N0} CR";
    }
}
