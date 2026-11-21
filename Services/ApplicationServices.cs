// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/ApplicationServices.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Dependency composition root wiring all HUDnopy background services.
// -----------------------------------------------------------------------------

namespace HUDnopy.Services;

/// <summary>
/// Dependency composition root wiring all HUDnopy background services.
/// </summary>
public sealed class ApplicationServices
{
    public ConfigService Configuration { get; }
    public BindingsService Bindings { get; }
    public InputSimulatorService InputSimulator { get; }
    public GameEventHub GameEvents { get; }
    public EliteFileWatcherService ShipState { get; }
    public AlertService Alerts { get; }
    public AlertSoundService AlertSounds { get; }
    public PiperTtsService PiperTts { get; }
    public AlertSpeechService AlertSpeech { get; }
    public SpanshSystemDataService SpanshSystemData { get; }
    public CommunitySyncService CommunitySync { get; }
    public FrontierCapiService FrontierCapi { get; }
    public GalaxyStatusService GalaxyStatus { get; }
    public GalnetService Galnet { get; }
    public AiCopilotService AiCopilot { get; }
    public WebServerService WebServer { get; }

    public ApplicationServices()
    {
        Configuration = new ConfigService();
        Bindings = new BindingsService(Configuration);
        InputSimulator = new InputSimulatorService(Bindings, Configuration);
        GameEvents = new GameEventHub();
        ShipState = new EliteFileWatcherService(Configuration, InputSimulator, GameEvents);
        Alerts = new AlertService(Configuration, ShipState, GameEvents);
        AlertSounds = new AlertSoundService(Alerts, Configuration);
        PiperTts = new PiperTtsService();
        AlertSpeech = new AlertSpeechService(Alerts, Configuration, PiperTts);
        InputSimulator.IsGameDetected = () => ShipState.State.IsGameDetected;
        SpanshSystemData = new SpanshSystemDataService(Configuration, ShipState);
        CommunitySync = new CommunitySyncService(Configuration, ShipState, GameEvents);
        FrontierCapi = new FrontierCapiService(Configuration, ShipState, GameEvents);
        GalaxyStatus = new GalaxyStatusService();
        Galnet = new GalnetService();
        AiCopilot = new AiCopilotService(Configuration, ShipState, Bindings, InputSimulator, SpanshSystemData, CommunitySync, Galnet, GalaxyStatus, AlertSpeech, GameEvents);
        WebServer = new WebServerService(Configuration, ShipState, Bindings, InputSimulator, Alerts, SpanshSystemData, AiCopilot, GalaxyStatus, Galnet);
    }
}
