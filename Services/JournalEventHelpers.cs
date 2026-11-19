// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/JournalEventHelpers.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Shared JSON parsing helpers for Elite Dangerous Journal events.
// -----------------------------------------------------------------------------

using System;
using System.Text.Json;

namespace HUDnopy.Services;

/// <summary>
/// Shared JSON parsing helpers for Elite Dangerous Journal events.
/// </summary>
internal static class JournalEventHelpers
{
    internal static bool IsLifeSupportRepair(JsonElement root)
    {
        foreach (string propertyName in new[] { "Item", "Module" })
        {
            if (root.TryGetProperty(propertyName, out var item) &&
                item.ValueKind == JsonValueKind.String &&
                item.GetString() is string name &&
                name.Replace("_", "", StringComparison.Ordinal)
                    .Replace(" ", "", StringComparison.Ordinal)
                    .Contains("lifesupport", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
