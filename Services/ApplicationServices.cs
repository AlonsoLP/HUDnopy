// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/ApplicationServices.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Dependency composition root wiring all HUDnopy background services.
// -----------------------------------------------------------------------------

using HUDnopy.Services.Integrations;

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
    public EliteBgsWatchlistService EliteBgsWatchlist { get; }
    public ArdentMarketService ArdentMarket { get; }
    public EdastroCanonnService EdastroCanonn { get; }
    public DiscordIntegrationService Discord { get; }
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
        EliteBgsWatchlist = new EliteBgsWatchlistService(Configuration, ShipState);
        ArdentMarket = new ArdentMarketService(Configuration, ShipState);
        EdastroCanonn = new EdastroCanonnService(Configuration, ShipState, GameEvents);
        Discord = new DiscordIntegrationService(Configuration, ShipState, GameEvents, EliteBgsWatchlist);
        AiCopilot = new AiCopilotService(Configuration, ShipState, Bindings, InputSimulator, SpanshSystemData, CommunitySync, Galnet, GalaxyStatus, AlertSpeech, GameEvents, EliteBgsWatchlist, ArdentMarket, EdastroCanonn, Discord, FrontierCapi, Alerts);
        WebServer = new WebServerService(Configuration, ShipState, Bindings, InputSimulator, Alerts, SpanshSystemData, AiCopilot, GalaxyStatus, Galnet, EliteBgsWatchlist, ArdentMarket, EdastroCanonn, CommunitySync, AlertSpeech);
        AiCopilot.WebHudStatusProvider = () => WebServer.IsRunning
            ? $"Web HUD active on port {WebServer.CurrentPort}. URLs: {string.Join(", ", WebServer.GetAccessUrls())}{(string.IsNullOrWhiteSpace(WebServer.SessionCode) ? "" : $". Session code: {WebServer.SessionCode}")}."
            : $"Web HUD is stopped ({WebServer.StatusMessage}).";
    }
}
