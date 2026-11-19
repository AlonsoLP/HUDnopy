// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/IGameEventSource.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Event source abstraction for Journal and snapshot telemetry notifications.
// -----------------------------------------------------------------------------

using System;
using HUDnopy.Models;

namespace HUDnopy.Services;

/// <summary>
/// Event source abstraction for Journal and snapshot telemetry notifications.
/// </summary>
public interface IGameEventSource
{
    event Action<GameJournalEvent>? JournalEventReceived;
}
