// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Models/GameJournalEvent.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Normalized Journal event envelope dispatched through GameEventHub.
// -----------------------------------------------------------------------------

using System;
using System.Text.Json;

namespace HUDnopy.Models;

/// <summary>
/// Normalized Journal event envelope dispatched through GameEventHub.
/// </summary>
public sealed record GameJournalEvent(
    string EventType,
    DateTimeOffset Timestamp,
    JsonElement Data,
    bool IsHistorical = false);
