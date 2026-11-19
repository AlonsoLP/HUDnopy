// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/GameEventHub.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: In-process pub/sub event bus decoupling Journal ingestion from consumers.
// -----------------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Linq;
using HUDnopy.Models;

namespace HUDnopy.Services;

/// <summary>
/// In-process pub/sub event bus decoupling Journal ingestion from consumers.
/// </summary>
public sealed class GameEventHub : IGameEventSource
{
    public event Action<GameJournalEvent>? JournalEventReceived;

    public void Publish(GameJournalEvent gameEvent)
    {
        var handlers = JournalEventReceived?.GetInvocationList();
        if (handlers == null) return;

        foreach (var handler in handlers.Cast<Action<GameJournalEvent>>())
        {
            try
            {
                handler(gameEvent);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"El suscriptor de eventos del juego falló: {ex}");
            }
        }
    }
}
