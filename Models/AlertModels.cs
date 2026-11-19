// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Models/AlertModels.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Domain models for alert severity, rules, notifications, and audio cues.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using HUDnopy.Localization;

namespace HUDnopy.Models;

/// <summary>
/// Domain models for alert severity, rules, notifications, and audio cues.
/// </summary>
public enum AlertSeverity
{
    Information,
    Warning,
    Critical
}

public sealed class AlertChannelPreferences
{
    public bool VisualEnabled { get; set; } = true;
    public bool SoundEnabled { get; set; }
    public string SoundProfile { get; set; } = "Auto";
    public bool VoiceEnabled { get; set; }
}

public sealed record AlertDefinition(string Id, string Name, string Description);

public sealed record AlertNotification(
    string Id,
    string AlertId,
    string Title,
    string Message,
    AlertSeverity Severity,
    DateTimeOffset Timestamp)
{
    internal AlertLocalization? Localization { get; init; }
}

public static class AlertDefinitions
{
    public static IReadOnlyList<AlertDefinition> All { get; } = AlertTranslations.ForLanguage("es-ES");

    public static IReadOnlyList<AlertDefinition> ForLanguage(string language) =>
        AlertTranslations.ForLanguage(language);

    public static Dictionary<string, AlertChannelPreferences> CreateDefaultPreferences()
    {
        var preferences = new Dictionary<string, AlertChannelPreferences>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in All)
            preferences[definition.Id] = new AlertChannelPreferences();
        return preferences;
    }
}
