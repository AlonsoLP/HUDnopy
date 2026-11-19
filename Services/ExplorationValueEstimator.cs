// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/ExplorationValueEstimator.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Credit value estimator for Universal Cartographics FSS and DSS body scans.
// -----------------------------------------------------------------------------

using System;
using HUDnopy.Models;

namespace HUDnopy.Services;

/// <summary>
/// Credit value estimator for Universal Cartographics FSS and DSS body scans.
/// </summary>
internal static class ExplorationValueEstimator
{
    private const double PlanetMassFactor = 0.56591828;

    public static void UpdateEstimate(ExplorationBodyItem body)
    {
        body.EstimatedCredits = null;
        body.RewardEstimateBasis = "";
        ExobiologyPredictor.UpdateBodyInsights(body);

        if (!TryGetBaseValue(body, out double baseValue))
            return;

        double estimate = baseValue;
        string basis = "Valor base estimado";
        if (body.WasDiscovered == false)
        {
            estimate *= 2.6;
            basis = "Primer descubrimiento (x2,6)";
        }

        if (body.HasCompletedMapping && body.WasMapped == false && body.PlanetClass.Length > 0)
        {
            estimate = body.WasDiscovered == false
                ? baseValue * 3.699622554 * 2.6
                : baseValue * 8.0956;

            basis = body.WasDiscovered == false
                ? "Primer descubrimiento + primer mapeo"
                : "Primer mapeo";

            if (body.MappingWasEfficient == true)
            {
                estimate *= 1.25;
                basis += " eficiente";
            }
        }
        else if (body.WasDiscovered == null)
        {
            basis = "Base estimada; descubrimiento previo desconocido";
        }
        else if (body.HasCompletedMapping && body.WasMapped == null && body.PlanetClass.Length > 0)
        {
            basis += "; mapeo sin clasificar";
        }

        if (!double.IsFinite(estimate) || estimate <= 0 || estimate > long.MaxValue)
            return;

        body.EstimatedCredits = (long)estimate;
        body.RewardEstimateBasis = basis + "; sin bonificación Odyssey ni exobiología";
    }

    private static bool TryGetBaseValue(ExplorationBodyItem body, out double value)
    {
        value = 0;
        if (!string.IsNullOrWhiteSpace(body.StarType))
        {
            if (!body.StellarMass.HasValue || body.StellarMass <= 0)
                return false;

            double k = body.StarType.ToUpperInvariant() switch
            {
                "N" or "H" => 22628,
                "D" or "DA" or "DAB" or "DAO" or "DAZ" or "DAV" or "DB" or "DBZ" or
                    "DBV" or "DO" or "DOV" or "DQ" or "DC" or "DCV" or "DX" => 14057,
                _ => 1200
            };
            value = k + (body.StellarMass.Value * k / 66.25);
            return true;
        }

        if (string.IsNullOrWhiteSpace(body.PlanetClass) || !body.MassEarth.HasValue || body.MassEarth <= 0)
            return false;

        string planetClass = body.PlanetClass.Replace('_', ' ').ToLowerInvariant();
        bool terraformable = body.TerraformState.Equals("Terraformable", StringComparison.OrdinalIgnoreCase);
        double kPlanet = planetClass switch
        {
            "metal rich body" => 21790 + (terraformable ? 65631 : 0),
            "ammonia world" => 96932,
            "sudarsky class i gas giant" => 1656,
            "sudarsky class ii gas giant" or "high metal content body" =>
                9654 + (terraformable ? 100677 : 0),
            "water world" => 64831 + (terraformable ? 116295 : 0),
            "earthlike body" or "earth-like world" => 64831 + 116295,
            "icy body" or "rocky body" or "rocky ice body" or
                "sudarsky class iii gas giant" or "sudarsky class iv gas giant" or
                "sudarsky class v gas giant" or "sudarsky class vi gas giant" or
                "helium rich gas giant" or "helium gas giant" =>
                300 + (terraformable ? 93328 : 0),
            _ => 0
        };
        if (kPlanet == 0)
            return false;

        value = Math.Max(kPlanet + (kPlanet * Math.Pow(body.MassEarth.Value, 0.2) * PlanetMassFactor), 500);
        return double.IsFinite(value);
    }
}
