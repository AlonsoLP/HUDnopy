// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/JournalTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized formatting for Journal event log entries.
// -----------------------------------------------------------------------------

using HUDnopy.Models;
using System.Globalization;
using System.Text.Json.Serialization;

namespace HUDnopy.Localization;

/// <summary>
/// Localized formatting for Journal event log entries.
/// </summary>
internal sealed record JournalEntryView([property: JsonIgnore] JournalEntryItem Entry, string Timestamp, string EventType, string Summary);

internal static class JournalTranslations
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog =
        DictionaryTranslationCatalog.Load("journal");

    public static string Summary(JournalEntryItem entry, string language)
    {
        if (string.IsNullOrEmpty(entry.SummaryKey))
            return entry.Summary;

        var text = Catalog[language];
        var args = (object?[])entry.SummaryArguments.Clone();
        if (entry.SummaryKey == "command" && args.Length > 1 && args[1] is string actionId &&
            DeckTranslations.ForLanguage(language).TryGetValue("action." + actionId, out string? action))
            args[0] = action;
        if (args.Length > 0 && args[0] is null)
        {
            string fallback = entry.SummaryKey switch
            {
                "missionAccepted" or "missionRedirected" or "missionCompleted" or "missionFailed" or "missionAbandoned" or "missionExpired" => "mission",
                "allBodies" => "currentSystem",
                "mapping" or "mappingProbes" => "planetaryBody",
                "organic" or "organicBody" => "organicSpecies",
                "codex" or "codexRegion" => "newDiscovery",
                _ => ""
            };
            if (fallback.Length > 0) args[0] = text[fallback];
        }
        var culture = CultureInfo.GetCultureInfo(language);
        if (entry.SummaryKey is "fssCount" or "soldEarnings")
            args[0] = ((long)args[0]!).ToString("N0", culture);
        else if (entry.SummaryKey == "mappingProbes")
            args[1] = ((long)args[1]!).ToString("N0", culture);
        return string.Format(culture, text[entry.SummaryKey], args);
    }

    public static JournalEntryView Present(JournalEntryItem entry, string language) =>
        new(entry, entry.Timestamp, entry.SummaryKey == "command" ? Catalog[language]["commandEvent"] : entry.EventType, Summary(entry, language));
}
