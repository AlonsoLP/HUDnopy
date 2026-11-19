// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Models/HeaderTelemetry.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Compact telemetry header projection shared between desktop and web interfaces.
// -----------------------------------------------------------------------------

using System.Globalization;
using HUDnopy.Localization;

namespace HUDnopy.Models;

/// <summary>
/// Compact telemetry header projection shared between desktop and web interfaces.
/// </summary>
public sealed record HeaderModule(string Id, string Label, string Value, bool Enabled);

public static class HeaderTelemetry
{
    public static readonly string[] Ids = ["system", "credits", "hull", "shield", "fuel", "location", "route"];

    public static IReadOnlyList<HeaderModule> Present(ShipState state, AppConfig config, string language)
    {
        var dashboard = DashboardTranslations.ForLanguage(language);
        var flight = FlightTranslations.ForLanguage(language);
        var culture = CultureInfo.GetCultureInfo(language);
        bool hasSession = state.CommanderName != "CMDR DESCONOCIDO";
        string number(double value) => value.ToString("0.0", culture);
        string fuel = state.HasFuelCapacity && state.FuelMax > 0
            ? $"{number(state.FuelMain)} / {number(state.FuelMax)} T ({number(Math.Clamp(state.FuelMain / state.FuelMax * 100, 0, 100))}%)"
            : state.HasStatusTelemetry ? $"{number(state.FuelMain)} T ({dashboard["capacityUnconfirmed"]})" : "---";
        string shield = !state.HasShieldTelemetry ? "---"
            : state.HasLoadoutTelemetry && !state.HasShieldGenerator ? flight["noGenerator"]
            : state.ShieldsUp ? flight["shieldUp"] : flight["shieldDown"];
        string location = !string.IsNullOrEmpty(state.StationName) ? state.StationName
            : !string.IsNullOrEmpty(state.BodyName) ? state.BodyName
            : hasSession ? dashboard["deepSpace"] : "---";
        string route = state.NavRoute.LastOrDefault(waypoint => !waypoint.IsCurrentSystem)?.StarSystem ?? "---";
        var items = new (string Id, string Label, string Value)[]
        {
            ("system", dashboard["currentSystem"], state.CurrentSystem),
            ("credits", dashboard["credits"], hasSession ? $"{state.Credits.ToString("N0", culture)} CR" : "--- CR"),
            ("hull", dashboard["hull"], state.HasHullTelemetry ? $"{number(Math.Clamp(state.HullHealth, 0, 100))}%" : "---"),
            ("shield", flight["shield"], shield),
            ("fuel", dashboard["fuel"], fuel),
            ("location", dashboard["currentLocation"], location),
            ("route", dashboard["destination"], route)
        };
        return items.Select(item => new HeaderModule(item.Id, item.Label, item.Value,
            config.HeaderModules.Contains(item.Id, StringComparer.Ordinal))).ToArray();
    }
}
