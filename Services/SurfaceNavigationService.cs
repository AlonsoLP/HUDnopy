// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/SurfaceNavigationService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Spherical bearing, turn-delta, visual compass tape, and surface
//              distance calculations for planetary navigation and exobiology.
// -----------------------------------------------------------------------------

using System;

namespace HUDnopy.Services;

/// <summary>
/// Computed planetary navigation guidance from the commander's live coordinates to a target point.
/// </summary>
public sealed record SurfaceNavigationSolution(
    double BearingDegrees,
    double TurnDeltaDegrees,
    double DistanceMeters,
    string SteeringArrow,
    string CompassTape);

/// <summary>
/// Calculates spherical bearing, geodesic distance, and visual compass cues on planetary surfaces.
/// </summary>
public static class SurfaceNavigationService
{
    private const double DefaultPlanetRadiusMeters = 1_500_000.0;

    /// <summary>
    /// Calculates the great-circle surface distance in meters between two planetary coordinates.
    /// </summary>
    public static double CalculateDistanceMeters(
        double lat1Deg,
        double lon1Deg,
        double lat2Deg,
        double lon2Deg,
        double planetRadiusMeters)
    {
        if (planetRadiusMeters <= 0 || !double.IsFinite(planetRadiusMeters))
            return 0;

        double lat1 = DegreesToRadians(lat1Deg);
        double lat2 = DegreesToRadians(lat2Deg);
        double dLat = DegreesToRadians(lat2Deg - lat1Deg);
        double dLon = DegreesToRadians(lon2Deg - lon1Deg);

        double sinDLat = Math.Sin(dLat / 2.0);
        double sinDLon = Math.Sin(dLon / 2.0);
        double a = sinDLat * sinDLat + Math.Cos(lat1) * Math.Cos(lat2) * sinDLon * sinDLon;
        double c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0.0, 1.0 - a)));
        return planetRadiusMeters * c;
    }

    /// <summary>
    /// Calculates the initial forward azimuth (0-359 degrees) between two planetary coordinates.
    /// </summary>
    public static double CalculateBearingDegrees(
        double lat1Deg,
        double lon1Deg,
        double lat2Deg,
        double lon2Deg)
    {
        double lat1 = DegreesToRadians(lat1Deg);
        double lat2 = DegreesToRadians(lat2Deg);
        double dLon = DegreesToRadians(lon2Deg - lon1Deg);

        double y = Math.Sin(dLon) * Math.Cos(lat2);
        double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        double bearing = (RadiansToDegrees(Math.Atan2(y, x)) + 360.0) % 360.0;
        return double.IsFinite(bearing) ? bearing : 0;
    }

    /// <summary>
    /// Computes bearing, signed turn delta, geodesic distance, and compass tape to a surface target.
    /// </summary>
    public static SurfaceNavigationSolution Compute(
        double currentLatDeg,
        double currentLonDeg,
        double currentHeadingDeg,
        double targetLatDeg,
        double targetLonDeg,
        double planetRadiusMeters)
    {
        double radius = planetRadiusMeters > 1000 ? planetRadiusMeters : DefaultPlanetRadiusMeters;
        double distanceMeters = CalculateDistanceMeters(currentLatDeg, currentLonDeg, targetLatDeg, targetLonDeg, radius);
        double bearing = CalculateBearingDegrees(currentLatDeg, currentLonDeg, targetLatDeg, targetLonDeg);

        // Signed turn angle (-180..+180)
        double delta = ((bearing - currentHeadingDeg + 540.0) % 360.0) - 180.0;

        string arrow = delta switch
        {
            < -25 => "◀◀",
            < -4 => "◀",
            <= 4 => "▲",
            <= 25 => "▶",
            _ => "▶▶"
        };

        string tape = BuildCompassTape(currentHeadingDeg, bearing);
        return new SurfaceNavigationSolution(bearing, delta, distanceMeters, arrow, tape);
    }

    /// <summary>
    /// Formats a surface distance in meters or kilometers.
    /// </summary>
    public static string FormatDistance(double meters) =>
        meters >= 1000.0 ? $"{meters / 1000.0:0.00} km" : $"{meters:0} m";

    private static string BuildCompassTape(double headingDeg, double targetBearingDeg)
    {
        const int slots = 21;
        char[] chars = new char[slots];
        for (int i = 0; i < slots; i++)
            chars[i] = '·';

        int center = slots / 2;
        chars[center] = '│';

        double delta = ((targetBearingDeg - headingDeg + 540.0) % 360.0) - 180.0;
        int offset = (int)Math.Round(Math.Clamp(delta / 9.0, -center, center));
        chars[center + offset] = '◆';

        return $"[{new string(chars)}]  HDG {headingDeg:000}°  ▸  BRG {targetBearingDeg:000}°";
    }

    private static double DegreesToRadians(double deg) => deg * (Math.PI / 180.0);
    private static double RadiansToDegrees(double rad) => rad * (180.0 / Math.PI);
}
