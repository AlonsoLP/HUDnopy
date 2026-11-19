// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/SpanshTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized display helpers for Spansh system intelligence panels.
// -----------------------------------------------------------------------------

using HUDnopy.Models;
using System.Globalization;

namespace HUDnopy.Localization;

/// <summary>
/// Localized display helpers for Spansh system intelligence panels.
/// </summary>
internal static class SpanshTranslations
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog =
        DictionaryTranslationCatalog.Load("spansh");

    public static IReadOnlyDictionary<string, string> ForLanguage(string language) =>
        Catalog.TryGetValue(language, out var text)
            ? text
            : throw new ArgumentException($"Idioma de Spansh no compatible: {language}", nameof(language));

    public static SpanshSystemDisplay Present(SpanshSystemDataState state, string language)
    {
        var text = ForLanguage(language);
        var culture = CultureInfo.GetCultureInfo(language);
        var snapshot = state.Snapshot;
        string title = text["title"];
        string status = state.Status switch
        {
            SpanshSystemDataStatus.Disabled => text["disabled"],
            SpanshSystemDataStatus.WaitingForSystem => text["waitingSystem"],
            SpanshSystemDataStatus.Loading when snapshot is not null => text["updating"],
            SpanshSystemDataStatus.Loading => text["loading"],
            SpanshSystemDataStatus.Error when snapshot is not null => text["cached"],
            SpanshSystemDataStatus.Error => text["unavailable"],
            _ when state.IsStale => text["cached"],
            _ => text["ready"]
        };

        if (snapshot is null)
            return EmptyDisplay(text, title, status);

        string formattedDate = snapshot.FetchedAtUtc.ToLocalTime().ToString("g", culture);
        return new SpanshSystemDisplay(
            title,
            status,
            string.Format(culture, text["summary"], snapshot.Name,
                snapshot.BodyCount.ToString("N0", culture),
                snapshot.StationCount.ToString("N0", culture)),
            string.Format(culture, state.IsStale ? text["cachedAt"] : text["updatedAt"], formattedDate),
            text["sourceNote"],
            string.Format(culture, text["bodiesTitle"], snapshot.BodyCount, snapshot.Bodies.Count),
            string.Format(culture, text["stationsTitle"], snapshot.StationCount, snapshot.Stations.Count),
            text["emptyBodies"],
            text["emptyStations"],
            text["body"],
            text["type"],
            text["arrival"],
            text["landable"],
            text["station"],
            text["stationBody"],
            text["controllingFaction"],
            snapshot.Bodies.Select(body => new SpanshBodyDisplay(
                body.Name,
                string.IsNullOrWhiteSpace(body.Type) ? text["unknown"] : body.Type,
                body.DistanceToArrival?.ToString("N1", culture) ?? "—",
                body.IsLandable.HasValue ? text[body.IsLandable.Value ? "yes" : "no"] : "—")).ToArray(),
            snapshot.Stations.Select(station => new SpanshStationDisplay(
                station.Name,
                string.IsNullOrWhiteSpace(station.Type) ? text["unknown"] : station.Type,
                station.BodyName,
                station.DistanceToArrival?.ToString("N1", culture) ?? "—",
                station.ControllingFaction)).ToArray());
    }

    private static SpanshSystemDisplay EmptyDisplay(
        IReadOnlyDictionary<string, string> text,
        string title,
        string status) =>
        new(title, status, "", "", text["sourceNote"], text["bodiesTitleEmpty"], text["stationsTitleEmpty"],
            text["emptyBodies"], text["emptyStations"],
            text["body"], text["type"], text["arrival"], text["landable"],
            text["station"], text["stationBody"], text["controllingFaction"],
            Array.Empty<SpanshBodyDisplay>(), Array.Empty<SpanshStationDisplay>());
}
