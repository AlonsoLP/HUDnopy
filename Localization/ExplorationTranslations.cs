// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/ExplorationTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized formatting helpers for FSS/DSS exploration bodies and signals.
// -----------------------------------------------------------------------------

using HUDnopy.Models;
using System.Globalization;
using System.Text.Json.Serialization;

namespace HUDnopy.Localization;

/// <summary>
/// Localized formatting helpers for FSS/DSS exploration bodies and signals.
/// </summary>
internal sealed record ExplorationBodyView(
    [property: JsonIgnore] ExplorationBodyItem Body,
    string Radius, string Gravity, string Temperature, string Materials, string Composition,
    string AtmosphereComposition, string Orbital, string Interest, string Discovery, string Mapping, string Estimate);

internal static class ExplorationTranslations
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog = DictionaryTranslationCatalog.Load("exploration");

    public static IReadOnlyDictionary<string, string> ForLanguage(string language) =>
        Catalog.TryGetValue(language, out var text)
            ? text
            : throw new ArgumentException($"Idioma de exploración no compatible: {language}", nameof(language));

    public static ExplorationBodyView Present(ExplorationBodyItem body, string language)
    {
        var text = ForLanguage(language);
        var culture = CultureInfo.GetCultureInfo(language);
        string Number(double value, string format) => value.ToString(format, culture);
        string List(IEnumerable<string> values, string empty = "—")
        {
            string value = string.Join(" · ", values.Where(item => item.Length > 0));
            return value.Length == 0 ? empty : value;
        }
        string CompositionPart(string key, double? value) =>
            value.HasValue ? $"{text[key]}: {Number(value.Value * 100, "0.#")}%" : "";
        string DetailsPart(string key, double? value, string suffix = "") =>
            value.HasValue ? $"{text[key]}: {Number(value.Value, "N0")}{suffix}" : "";
        string MaterialList(IEnumerable<ExplorationMaterialItem> values)
        {
            string result = string.Join(", ", values.Select(item => $"{item.Name} {Number(item.Percent, "0.##")}%"));
            return result.Length == 0 ? "---" : result;
        }
        var reasons = new List<string>();
        if (body.StarType.Equals("N", StringComparison.OrdinalIgnoreCase)) reasons.Add(text["neutron"]);
        else if (body.StarType.Equals("H", StringComparison.OrdinalIgnoreCase)) reasons.Add(text["blackHole"]);
        else if (body.StarType.StartsWith("D", StringComparison.OrdinalIgnoreCase)) reasons.Add(text["whiteDwarf"]);
        else if (body.StarType.Equals("W", StringComparison.OrdinalIgnoreCase)) reasons.Add(text["wolfRayet"]);
        if (body.PlanetClass.Contains("Earth-like", StringComparison.OrdinalIgnoreCase)) reasons.Add(text["earthLike"]);
        else if (body.PlanetClass.Contains("Water world", StringComparison.OrdinalIgnoreCase)) reasons.Add(text["waterWorld"]);
        else if (body.PlanetClass.Contains("Ammonia world", StringComparison.OrdinalIgnoreCase)) reasons.Add(text["ammoniaWorld"]);
        if (!string.IsNullOrWhiteSpace(body.TerraformState) && !body.TerraformState.Equals("Not terraformable", StringComparison.OrdinalIgnoreCase))
            reasons.Add(body.TerraformState);
        if (body.HasCompletedMapping) reasons.Add(text["dssComplete"]);
        if (body.BiologicalSignalCount > 0) reasons.Add(string.Format(culture, text["bioSignals"], body.BiologicalSignalCount));
        if (body.IsVirginBioLandable) reasons.Add(text["firstFootfallBonus"]);
        if (!string.IsNullOrWhiteSpace(body.PredictedExobiology)) reasons.Add(body.PredictedExobiology);
        if (body.GeologicalSignalCount > 0) reasons.Add(string.Format(culture, text["geoSignals"], body.GeologicalSignalCount));
        foreach (string anomaly in body.Anomalies)
            reasons.Add(anomaly);

        return new ExplorationBodyView(
            body,
            body.RadiusMeters.HasValue ? $"{Number(body.RadiusMeters.Value / 1000, "N0")} km" : "---",
            body.SurfaceGravity.HasValue ? $"{Number(body.SurfaceGravity.Value, "0.##")} m/s²" : "---",
            body.SurfaceTemperatureKelvin.HasValue ? $"{Number(body.SurfaceTemperatureKelvin.Value, "N0")} K" : "---",
            MaterialList(body.Materials),
            List(new[] { CompositionPart("ice", body.Composition.Ice), CompositionPart("rock", body.Composition.Rock), CompositionPart("metal", body.Composition.Metal) }),
            MaterialList(body.AtmosphereComposition),
            List(new[]
            {
                DetailsPart("age", body.AgeMillionsOfYears, " Ma"), DetailsPart("rotation", body.RotationPeriodSeconds, " s"),
                DetailsPart("orbit", body.OrbitalPeriodSeconds, " s"), DetailsPart("arrivalDistance", body.DistanceFromArrivalLs, " LS"),
                DetailsPart("semiMajorAxis", body.SemiMajorAxisMeters, " m"), DetailsPart("eccentricity", body.Eccentricity),
                DetailsPart("inclination", body.OrbitalInclination, "°"), DetailsPart("axialTilt", body.AxialTilt, "°"),
                DetailsPart("pressure", body.SurfacePressure),
                !string.IsNullOrWhiteSpace(body.Volcanism) ? $"{text["volcanism"]}: {body.Volcanism}" : "",
                body.TidalLock.HasValue ? $"{text["tidalLock"]}: {text[body.TidalLock.Value ? "yes" : "no"]}" : ""
            }),
            string.Join(" · ", reasons),
            text[body.WasDiscovered == true ? "discovered" : body.WasDiscovered == false ? "firstDiscovery" : "unknown"],
            text[body.WasMapped == true ? "mapped" : body.WasMapped == false ? "unmapped" : "unknown"],
            body.EstimatedCredits.HasValue ? $"{body.EstimatedCredits.Value.ToString("N0", culture)} CR" : text["noEstimate"]);
    }
}
