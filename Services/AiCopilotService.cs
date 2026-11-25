// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/AiCopilotService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Conversational COVAS AI copilot supporting Ollama, Gemini, OpenAI, voice input, and dynamic commentary.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Speech.Recognition;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HUDnopy.Localization;
using HUDnopy.Models;
using HUDnopy.Services.Integrations;
using static HUDnopy.Services.JsonElementExtensions;

namespace HUDnopy.Services;

/// <summary>
/// Conversational COVAS AI copilot supporting Ollama, Gemini, OpenAI, voice input, and dynamic commentary.
/// </summary>
public sealed partial class AiCopilotService : IDisposable
{
    private readonly ConfigService _configService;
    private readonly EliteFileWatcherService _fileWatcher;
    private readonly BindingsService _bindingsService;
    private readonly InputSimulatorService _inputSimulator;
    private readonly SpanshSystemDataService _spanshService;
    private readonly CommunitySyncService _communitySync;
    private readonly GalnetService _galnetService;
    private readonly GalaxyStatusService _galaxyStatus;
    private readonly AlertSpeechService _speechService;
    private readonly EliteBgsWatchlistService? _eliteBgsWatchlist;
    private readonly ArdentMarketService? _ardentMarket;
    private readonly EdastroCanonnService? _edastroCanonn;
    private readonly DiscordIntegrationService? _discord;
    private readonly FrontierCapiService? _frontierCapi;
    private readonly AlertService? _alertService;
    private readonly GameEventHub _gameEvents;
    private string _announcedPvpWarningSystem = "";

    public Func<string>? WebHudStatusProvider { get; set; }
    private readonly HttpClient _httpClient = HttpClientHeaderDefaults.CreateHttpClient(TimeSpan.FromSeconds(75));
    private readonly Lock _historyLock = new();
    private readonly List<(string Role, string Content)> _history = new();
    private readonly SemaphoreSlim _askLock = new(1, 1);
    private readonly Lock _activeRequestCtsLock = new();
    private CancellationTokenSource? _activeRequestCts;
    private DateTimeOffset _lastDynamicEventUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRadioChatterUtc = DateTimeOffset.MinValue;
    private readonly HashSet<string> _announcedSignalsInSystem = new(StringComparer.OrdinalIgnoreCase);
    private int _dynamicEventBusy;
    private readonly CancellationTokenSource _timerCts = new();
    private string _lastSpokenReply = "";
    private DateTimeOffset _wakeWordFollowUpUntilUtc = DateTimeOffset.MinValue;
    private readonly Dictionary<string, (DateTimeOffset ExpiresUtc, string Result)> _queryCache = new(StringComparer.OrdinalIgnoreCase);
    private string _lastExternalQuerySummary = "";
    private DateTimeOffset _lastExternalQueryUtc = DateTimeOffset.MinValue;
    private string _announcedExoDistanceKey = "";
    private bool _announcedFuelScoopFull;

    public event Action<string, string>? CopilotMessageAdded;
    public event Action<string>? CopilotStreamChunk;
    public event Action<string, string>? SpanshRouteRequested;
    public event Action<string>? SpanshServiceRequested;
    public event Action<string>? CopySystemRequested;
    public event Action<string>? SurfaceBookmarkSaveRequested;
    public event Action<string>? SurfaceNavigateRequested;
    public event Action<string, bool>? SpanshItemRequested;
    public event Action<string>? ExportShipRequested;
    public event Action<string>? UiNavigationRequested;
    public event Action? SpanshTradeRequested;
    public event Action<string>? SpanshMiningRequested;
    public event Action? ColonisationSupplierRequested;
    public event Action<string>? WishlistAddRequested;
    public event Action<double>? TritiumCalcRequested;
    public event Action<int>? VoiceRateAdjustRequested;
    public event Action? MemoryChanged;
    public long LastResponseLatencyMs { get; private set; }
    public long LastFirstTokenLatencyMs { get; private set; }
    public bool LastThinkingUsed { get; private set; }
    public WhisperSttService? OfflineWhisperStt { get; set; }

    public AiCopilotService(
        ConfigService configService,
        EliteFileWatcherService fileWatcher,
        BindingsService bindingsService,
        InputSimulatorService inputSimulator,
        SpanshSystemDataService spanshService,
        CommunitySyncService communitySync,
        GalnetService galnetService,
        GalaxyStatusService galaxyStatus,
        AlertSpeechService speechService,
        GameEventHub gameEvents,
        EliteBgsWatchlistService? eliteBgsWatchlist = null,
        ArdentMarketService? ardentMarket = null,
        EdastroCanonnService? edastroCanonn = null,
        DiscordIntegrationService? discord = null,
        FrontierCapiService? frontierCapi = null,
        AlertService? alertService = null)
    {
        _configService = configService;
        _fileWatcher = fileWatcher;
        _bindingsService = bindingsService;
        _inputSimulator = inputSimulator;
        _spanshService = spanshService;
        _communitySync = communitySync;
        _galnetService = galnetService;
        _galaxyStatus = galaxyStatus;
        _speechService = speechService;
        _eliteBgsWatchlist = eliteBgsWatchlist;
        _ardentMarket = ardentMarket;
        _edastroCanonn = edastroCanonn;
        _discord = discord;
        _frontierCapi = frontierCapi;
        _alertService = alertService;
        _gameEvents = gameEvents;
        _gameEvents.JournalEventReceived += OnJournalEventReceived;
        _fileWatcher.StateUpdated += OnShipStateUpdated;
        if (_eliteBgsWatchlist is not null)
            _eliteBgsWatchlist.ConflictDetected += OnEliteBgsConflictDetected;
        _communitySync.EdsmSystemIntelUpdated += OnEdsmSystemIntelUpdated;
    }

    private void OnShipStateUpdated(ShipState state)
    {
        var cfg = _configService.Current;
        if (!cfg.EnableAiCopilot)
            return;

        bool isEs = string.Equals(cfg.InterfaceLanguage, "es-ES", StringComparison.OrdinalIgnoreCase);

        // 1. Observatory BioInsights-style genetic sample distance reached alert (0 tokens)
        if (state.ExobiologyTracker is { SamplesCollected: > 0 and < 3 } exo &&
            exo.RequiredDistanceMeters > 0 &&
            exo.CurrentDistanceMeters >= exo.RequiredDistanceMeters)
        {
            string key = $"{exo.BodyName}|{exo.Species}|{exo.SamplesCollected}";
            if (!string.Equals(_announcedExoDistanceKey, key, StringComparison.OrdinalIgnoreCase))
            {
                _announcedExoDistanceKey = key;
                string msg = isEs
                    ? $"Distancia genética alcanzada para {exo.Species} ({exo.CurrentDistanceMeters:0} metros): ya puedes tomar la muestra {exo.SamplesCollected + 1} de 3."
                    : $"Genetic diversity distance reached for {exo.Species} ({exo.CurrentDistanceMeters:0} meters): ready for sample {exo.SamplesCollected + 1} of 3.";
                CopilotMessageAdded?.Invoke("COVAS (Exobio)", msg);
                if (cfg.AiSpeakReplies)
                    _speechService.SpeakCustomText(NormalizeTextForSpeech(msg, cfg.InterfaceLanguage));
            }
        }

        // 2. EDDI-style fuel scoop tank full announcement (0 tokens)
        if (state.FuelMax > 0)
        {
            if (state.FuelMain < state.FuelMax * 0.94)
            {
                _announcedFuelScoopFull = false;
            }
            else if (state.ScoopingFuel && state.FuelMain >= state.FuelMax * 0.995 && !_announcedFuelScoopFull)
            {
                _announcedFuelScoopFull = true;
                string msg = isEs ? "Depósito de combustible al cien por cien." : "Fuel tank at one hundred percent.";
                CopilotMessageAdded?.Invoke("COVAS (Fuel)", msg);
                if (cfg.AiSpeakReplies)
                    _speechService.SpeakCustomText(NormalizeTextForSpeech(msg, cfg.InterfaceLanguage));
            }
        }
    }

    private void OnEliteBgsConflictDetected(EliteBgsFactionWatchItem item)
    {
        var cfg = _configService.Current;
        if (!cfg.EnableAiCopilot || !cfg.EnableEliteBgsWatchlist)
            return;

        string msg = $"EliteBGS alert: {item.FactionName} in {item.SystemName} ({item.InfluenceDisplay}) — {item.State}.";
        CopilotMessageAdded?.Invoke("COVAS (BGS)", msg);
        if (cfg.AiSpeakReplies)
            _speechService.SpeakCustomText(NormalizeTextForSpeech(msg, cfg.InterfaceLanguage));
    }

    private void OnEdsmSystemIntelUpdated(EdsmSystemIntel intel)
    {
        var cfg = _configService.Current;
        if (!cfg.EnableAiCopilot || !cfg.EnableEdsmSync || intel.DeathsDay < 3)
            return;
        if (!string.Equals(intel.SystemName, _fileWatcher.State.CurrentSystem, StringComparison.OrdinalIgnoreCase))
            return;
        if (string.Equals(_announcedPvpWarningSystem, intel.SystemName, StringComparison.OrdinalIgnoreCase))
            return;
        _announcedPvpWarningSystem = intel.SystemName;

        string msg = $"EDSM tactical warning in {intel.SystemName}: {intel.DeathsDay} ship destructions reported in the last 24 hours ({intel.TrafficDay} visitors). Exercise caution.";
        CopilotMessageAdded?.Invoke("COVAS (EDSM)", msg);
        if (cfg.AiSpeakReplies)
            _speechService.SpeakCustomText(NormalizeTextForSpeech(msg, cfg.InterfaceLanguage));
    }

    private void OnJournalEventReceived(GameJournalEvent gameEvent)
    {
        if (gameEvent.IsHistorical)
            return;

        var cfg = _configService.Current;
        if (!cfg.EnableAiCopilot)
            return;

        // Clear per-system signal deduplication on jump
        if (gameEvent.EventType is "FSDJump" or "Location")
        {
            lock (_announcedSignalsInSystem)
                _announcedSignalsInSystem.Clear();
        }

        // Express System Arrival Briefing on FSDJump (personal note, carrier presence, or security)
        if (cfg.AiSystemArrivalBriefing && gameEvent.EventType == "FSDJump")
        {
            string sys = gameEvent.Data.TryGetProperty("StarSystem", out var sp) && sp.ValueKind == JsonValueKind.String
                ? sp.GetString() ?? ""
                : "";
            if (!string.IsNullOrWhiteSpace(sys))
            {
                var ops = OperationsTranslations.ForLanguage(cfg.InterfaceLanguage);
                var notes = new List<string>();
                if (cfg.SystemNotes.TryGetValue(sys, out string? sysNote) && !string.IsNullOrWhiteSpace(sysNote))
                    notes.Add(string.Format(ops["covasArrivalNote"], sysNote));
                if (_fileWatcher.State.Carrier is { } fc && string.Equals(fc.System, sys, StringComparison.OrdinalIgnoreCase))
                    notes.Add(string.Format(ops["covasArrivalCarrier"], fc.Name));
                if (gameEvent.Data.TryGetProperty("SystemSecurity_Localised", out var secP) && secP.ValueKind == JsonValueKind.String)
                {
                    string sec = secP.GetString() ?? "";
                    if (sec.Contains("Anarchy", StringComparison.OrdinalIgnoreCase) || sec.Contains("Anarqu", StringComparison.OrdinalIgnoreCase))
                        notes.Add(ops["covasArrivalAnarchy"]);
                }
                if (cfg.EnableEliteBgsWatchlist && _fileWatcher.State.WatchedBgsFactions.Count > 0)
                {
                    var watchedHere = _fileWatcher.State.WatchedBgsFactions
                        .Where(f => string.Equals(f.SystemName, sys, StringComparison.OrdinalIgnoreCase))
                        .Take(2)
                        .Select(static f => $"{f.FactionName} ({f.InfluenceDisplay}, {f.State})")
                        .ToList();
                    if (watchedHere.Count > 0)
                        notes.Add($"Watched BGS factions here: {string.Join(", ", watchedHere)}");
                }
                int missionsHere = _fileWatcher.State.ActiveMissions
                    .Count(m => string.Equals(m.DestinationSystem, sys, StringComparison.OrdinalIgnoreCase));
                if (missionsHere > 0)
                {
                    bool isEs = string.Equals(cfg.InterfaceLanguage, "es-ES", StringComparison.OrdinalIgnoreCase);
                    notes.Add(isEs
                        ? $"Tienes {missionsHere} misión{(missionsHere == 1 ? "" : "es")} con destino en este sistema"
                        : $"You have {missionsHere} mission{(missionsHere == 1 ? "" : "s")} with destination in this system");
                }
                if (_fileWatcher.State.NavRoute.Count > 0 &&
                    string.Equals(_fileWatcher.State.NavRoute[^1].StarSystem, sys, StringComparison.OrdinalIgnoreCase))
                {
                    bool isEs = string.Equals(cfg.InterfaceLanguage, "es-ES", StringComparison.OrdinalIgnoreCase);
                    notes.Add(isEs ? "Destino final de ruta alcanzado" : "Final route destination reached");
                }

                if (notes.Count > 0)
                {
                    string briefing = string.Format(ops["covasArrivalBriefing"], sys, string.Join(". ", notes));
                    CopilotMessageAdded?.Invoke("COVAS (Briefing)", briefing);
                    if (cfg.AiSpeakReplies)
                        _speechService.SpeakCustomText(NormalizeTextForSpeech(briefing, cfg.InterfaceLanguage));
                }
            }
        }

        // Zero-token tactical announcements (High-G, FSS valuable signals, Mining prospector, Radio chatter)
        TryHandleZeroTokenTacticalEvent(cfg, gameEvent);

        if (!cfg.AiDynamicEvents)
            return;

        int cooldown = Math.Clamp(cfg.AiDynamicEventCooldownSeconds, 10, 600);
        if ((DateTimeOffset.UtcNow - _lastDynamicEventUtc).TotalSeconds < cooldown)
            return;

        string? eventCue = BuildDynamicEventCue(gameEvent);
        if (string.IsNullOrWhiteSpace(eventCue))
            return;

        if (_askLock.CurrentCount == 0 || System.Threading.Interlocked.Exchange(ref _dynamicEventBusy, 1) != 0)
            return;

        _lastDynamicEventUtc = DateTimeOffset.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                string prompt = $"Evento táctico recién ocurrido: {eventCue}. Da un comentario breve de copiloto (1 o 2 frases máximo, tono natural de cabina).";
                var (ok, reply) = await AskAsync(prompt, speakOverride: true, streamToUi: false, cancelPrevious: false);
                if (ok && !string.IsNullOrWhiteSpace(reply))
                    CopilotMessageAdded?.Invoke("COVAS (Evento)", reply);
            }
            catch (Exception ex)
            {
                AppDiagnosticsLogger.Warn("AI.DynamicEvent", $"Dynamic tactical commentary failed: {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _dynamicEventBusy, 0);
            }
        });
    }

    /// <summary>
    /// Handles zero-token tactical announcements directly from Journal events without calling any LLM.
    /// Covers High-G approach warnings, valuable FSS signals (HGE/Thargoid/Guardian/Megaship),
    /// high-yield mining prospecting, and incoming player/pirate radio chatter.
    /// </summary>
    private void TryHandleZeroTokenTacticalEvent(AppConfig cfg, GameJournalEvent gameEvent)
    {
        var data = gameEvent.Data;
        var ops = OperationsTranslations.ForLanguage(cfg.InterfaceLanguage);
        string? announcement = null;

        switch (gameEvent.EventType)
        {
            case "Undocked":
                {
                    var st = _fileWatcher.State;
                    if (st.HasLimpetController && st.LimpetCount == 0)
                    {
                        bool isEs = string.Equals(cfg.InterfaceLanguage, "es-ES", StringComparison.OrdinalIgnoreCase);
                        announcement = isEs
                            ? "Atención comandante: llevas controlador de drones pero cero drones en bodega."
                            : "Commander reminder: you have a limpet controller fitted, but zero limpets in cargo.";
                    }
                    break;
                }

            case "Interdicted":
                {
                    bool isPlayer = data.TryGetProperty("IsPlayer", out var ip) && ip.ValueKind == JsonValueKind.True;
                    string interdictor = GetStr(data, "Interdictor") ?? "";
                    string faction = GetStr(data, "Faction") ?? "";
                    bool isEs = string.Equals(cfg.InterfaceLanguage, "es-ES", StringComparison.OrdinalIgnoreCase);
                    string who = !string.IsNullOrWhiteSpace(interdictor)
                        ? (isPlayer ? $"CMDR {interdictor}" : interdictor)
                        : (!string.IsNullOrWhiteSpace(faction) ? faction : (isEs ? "hostil desconocido" : "unknown hostile"));
                    announcement = isEs
                        ? $"Alerta de interdicción: {(isPlayer ? "comandante humano " : "")}{who}."
                        : $"Interdiction alert: {(isPlayer ? "human commander " : "")}{who}.";
                    break;
                }

            case "Docked":
                {
                    var st = _fileWatcher.State;
                    string station = GetStr(data, "StationName") ?? st.StationName;
                    bool isEs = string.Equals(cfg.InterfaceLanguage, "es-ES", StringComparison.OrdinalIgnoreCase);
                    var reminders = new List<string>();
                    int missionsHere = st.ActiveMissions.Count(m =>
                        string.Equals(m.DestinationSystem, st.CurrentSystem, StringComparison.OrdinalIgnoreCase));
                    if (missionsHere > 0)
                        reminders.Add(isEs ? $"{missionsHere} misión{(missionsHere == 1 ? "" : "es")} en este sistema" : $"{missionsHere} mission{(missionsHere == 1 ? "" : "s")} in this system");
                    long unsold = st.UnsoldCartographyCredits + st.UnsoldExobiologyCredits;
                    if (unsold >= 2_000_000)
                        reminders.Add(isEs ? $"{unsold:N0} créditos en datos sin vender" : $"{unsold:N0} credits in unsold exploration data");
                    if (st.CombatSession.PendingCredits >= 500_000)
                        reminders.Add(isEs ? $"{st.CombatSession.PendingCredits:N0} créditos en recompensas" : $"{st.CombatSession.PendingCredits:N0} credits in bounties");
                    if (reminders.Count > 0)
                        announcement = isEs
                            ? $"Atracado en {station}. Recordatorio: {string.Join(", ", reminders)}."
                            : $"Docked at {station}. Reminder: {string.Join(", ", reminders)}.";
                    break;
                }

            case "FSSAllBodiesFound":
                {
                    var st = _fileWatcher.State;
                    int bodyCount = JsonElementExtensions.GetInt(data, "Count") ?? st.ExplorationBodies.Count;
                    var valuable = st.ExplorationBodies
                        .Where(static b => b.WasMapped != true && ((b.EstimatedCredits ?? 0) >= 500_000 || b.IsVirginBioLandable))
                        .OrderByDescending(static b => b.EstimatedCredits ?? 0)
                        .Take(3)
                        .Select(static b => b.BodyName)
                        .ToList();
                    long totalEst = st.ExplorationBodies.Sum(static b => b.EstimatedCredits ?? 0);
                    if (valuable.Count > 0 || totalEst >= 1_000_000)
                    {
                        bool isEs = string.Equals(cfg.InterfaceLanguage, "es-ES", StringComparison.OrdinalIgnoreCase);
                        string valList = valuable.Count > 0
                            ? (isEs ? $" Destacados para mapear: {string.Join(", ", valuable)}." : $" Worth mapping: {string.Join(", ", valuable)}.")
                            : "";
                        announcement = isEs
                            ? $"Escaneo completo de {bodyCount} cuerpos. Valor estimado: {totalEst:N0} créditos.{valList}"
                            : $"Full system scan complete: {bodyCount} bodies, estimated {totalEst:N0} credits.{valList}";
                    }
                    break;
                }

            case "SAASignalsFound":
                {
                    string bodyName = GetStr(data, "BodyName") ?? "";
                    if (!string.IsNullOrWhiteSpace(bodyName) &&
                        data.TryGetProperty("Signals", out var sigs) && sigs.ValueKind == JsonValueKind.Array)
                    {
                        int bioCount = 0;
                        foreach (var sig in sigs.EnumerateArray())
                        {
                            string sigType = $"{GetStr(sig, "Type")} {GetStr(sig, "Type_Localised")}";
                            if (sigType.Contains("Biological", StringComparison.OrdinalIgnoreCase) ||
                                sigType.Contains("Biológ", StringComparison.OrdinalIgnoreCase) ||
                                sigType.Contains("Biolog", StringComparison.OrdinalIgnoreCase) ||
                                sigType.Contains("Биолог", StringComparison.OrdinalIgnoreCase))
                            {
                                bioCount += JsonElementExtensions.GetInt(sig, "Count") ?? 0;
                            }
                        }
                        if (bioCount > 0)
                        {
                            string dedupeKey = $"BIO|{bodyName}";
                            bool isNew;
                            lock (_announcedSignalsInSystem)
                                isNew = _announcedSignalsInSystem.Add(dedupeKey);
                            if (isNew)
                            {
                                bool isEs = string.Equals(cfg.InterfaceLanguage, "es-ES", StringComparison.OrdinalIgnoreCase);
                                announcement = isEs
                                    ? $"Escaneo superficial en {bodyName}: {bioCount} señal{(bioCount == 1 ? "" : "es")} biológica{(bioCount == 1 ? "" : "s")} detectada{(bioCount == 1 ? "" : "s")}."
                                    : $"Surface scan on {bodyName}: {bioCount} biological signal{(bioCount == 1 ? "" : "s")} detected.";
                            }
                        }
                    }
                    break;
                }

            case "ApproachBody" when cfg.AiTacticalHighGravity:
                {
                    string bodyName = GetStr(data, "Body") ?? "";
                    double? gravityG = null;
                    if (!string.IsNullOrWhiteSpace(bodyName))
                    {
                        var body = _fileWatcher.State.ExplorationBodies
                            .FirstOrDefault(b => string.Equals(b.BodyName, bodyName, StringComparison.OrdinalIgnoreCase));
                        if (body?.SurfaceGravity is double gMs2 && gMs2 > 0)
                            gravityG = gMs2 / 9.80665;
                    }
                    gravityG ??= _fileWatcher.State.ApproachBodyGravityG ?? _fileWatcher.State.Gravity;

                    if (gravityG is >= 1.5)
                        announcement = string.Format(ops["covasHighGravity"], bodyName, gravityG.Value.ToString("0.00", CultureInfo.GetCultureInfo(cfg.InterfaceLanguage)));
                    break;
                }

            case "FSSSignalDiscovered" when cfg.AiTacticalValuableSignals:
                {
                    string signalRaw = GetStr(data, "SignalName") ?? "";
                    string signalLoc = GetStr(data, "SignalName_Localised") ?? signalRaw;
                    string ussType = GetStr(data, "USSType") ?? "";
                    string? category = null;

                    if (ussType.Contains("VeryValuableSalvage", StringComparison.OrdinalIgnoreCase) ||
                        signalLoc.Contains("High Grade", StringComparison.OrdinalIgnoreCase) ||
                        signalLoc.Contains("Alto grado", StringComparison.OrdinalIgnoreCase))
                    {
                        category = ops["covasSignalHge"];
                    }
                    else if (signalRaw.Contains("Thargoid", StringComparison.OrdinalIgnoreCase) ||
                             signalLoc.Contains("Non-Human", StringComparison.OrdinalIgnoreCase) ||
                             signalLoc.Contains("No humana", StringComparison.OrdinalIgnoreCase))
                    {
                        category = ops["covasSignalThargoid"];
                    }
                    else if (signalRaw.Contains("Guardian", StringComparison.OrdinalIgnoreCase) ||
                             signalLoc.Contains("Guardian", StringComparison.OrdinalIgnoreCase))
                    {
                        category = ops["covasSignalGuardian"];
                    }
                    else if (string.Equals(GetStr(data, "SignalType"), "Megaship", StringComparison.OrdinalIgnoreCase))
                    {
                        category = string.Format(ops["covasSignalMegaship"], signalLoc);
                    }

                    if (!string.IsNullOrWhiteSpace(category))
                    {
                        string dedupeKey = $"{_fileWatcher.State.CurrentSystem}|{category}";
                        bool isNew;
                        lock (_announcedSignalsInSystem)
                            isNew = _announcedSignalsInSystem.Add(dedupeKey);
                        if (isNew)
                            announcement = string.Format(ops["covasSignalInSystem"], category, _fileWatcher.State.CurrentSystem);
                    }
                    break;
                }

            case "ProspectedAsteroid" when cfg.AiTacticalMiningProspector:
                {
                    string motherlode = GetStr(data, "MotherlodeMaterial_Localised") ?? GetStr(data, "MotherlodeMaterial") ?? "";
                    if (!string.IsNullOrWhiteSpace(motherlode))
                    {
                        announcement = string.Format(ops["covasMiningCore"], motherlode);
                        break;
                    }

                    var targets = (cfg.MiningTargetMinerals ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    int minPct = Math.Clamp(cfg.MiningMinPercent, 5, 80);

                    if (data.TryGetProperty("Materials", out var mats) && mats.ValueKind == JsonValueKind.Array)
                    {
                        var hits = new List<string>();
                        foreach (var mat in mats.EnumerateArray())
                        {
                            string rawName = GetStr(mat, "Name") ?? "";
                            string locName = GetStr(mat, "Name_Localised") ?? rawName;
                            double prop = mat.TryGetProperty("Proportion", out var pp) && pp.TryGetDouble(out double d) ? d : 0;
                            if (prop < minPct) continue;
                            bool isTarget = targets.Length == 0 || targets.Any(t =>
                                rawName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                locName.Contains(t, StringComparison.OrdinalIgnoreCase));
                            if (isTarget)
                                hits.Add(string.Format(ops["covasMiningHit"], locName, prop.ToString("0", CultureInfo.InvariantCulture)));
                        }
                        if (hits.Count > 0)
                            announcement = string.Format(ops["covasMiningRich"], string.Join(", ", hits));
                    }
                    break;
                }

            case "ReceiveText" when cfg.AiTacticalRadioChatter:
                {
                    if ((DateTimeOffset.UtcNow - _lastRadioChatterUtc).TotalSeconds < 12)
                        break;

                    string channel = (GetStr(data, "Channel") ?? "").ToLowerInvariant();
                    string from = GetStr(data, "From_Localised") ?? GetStr(data, "From") ?? "";
                    string msgRaw = GetStr(data, "Message") ?? "";
                    string msgLoc = GetStr(data, "Message_Localised") ?? msgRaw;
                    if (string.IsNullOrWhiteSpace(msgLoc))
                        break;

                    bool isPlayerChannel = channel is "player" or "wing" or "squadron" or "friends";
                    bool isPirateThreat = msgRaw.Contains("Pirate_", StringComparison.OrdinalIgnoreCase) &&
                        (msgRaw.Contains("Scan", StringComparison.OrdinalIgnoreCase) ||
                         msgRaw.Contains("Attack", StringComparison.OrdinalIgnoreCase) ||
                         msgRaw.Contains("Threat", StringComparison.OrdinalIgnoreCase));

                    if (isPlayerChannel)
                    {
                        _lastRadioChatterUtc = DateTimeOffset.UtcNow;
                        announcement = string.Format(ops["covasRadioPlayer"], channel, from, Truncate(msgLoc, 140));
                    }
                    else if (isPirateThreat)
                    {
                        _lastRadioChatterUtc = DateTimeOffset.UtcNow;
                        announcement = string.Format(ops["covasRadioHostile"], from, Truncate(msgLoc, 140));
                    }
                    break;
                }
        }

        if (string.IsNullOrWhiteSpace(announcement))
            return;

        CopilotMessageAdded?.Invoke("COVAS (Táctico)", announcement);
        if (cfg.AiSpeakReplies)
            _speechService.SpeakCustomText(NormalizeTextForSpeech(announcement, cfg.InterfaceLanguage));
    }

    private static string? BuildDynamicEventCue(GameJournalEvent gameEvent)
    {
        var data = gameEvent.Data;

        switch (gameEvent.EventType)
        {
            case "StartJump":
                string? starClass = GetStr(data, "StarClass");
                if (EliteFileWatcherService.IsHazardousStarClass(starClass))
                    return $"Saltando a {GetStr(data, "StarSystem") ?? "sistema"} con estrella peligrosa clase {starClass}.";
                return null;

            case "Scan":
                string? pClass = GetStr(data, "PlanetClass") ?? "";
                if (pClass.Contains("Earthlike", StringComparison.OrdinalIgnoreCase) ||
                    pClass.Contains("Water world", StringComparison.OrdinalIgnoreCase) ||
                    pClass.Contains("Ammonia world", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Descubrimiento valioso escaneado: {GetStr(data, "BodyName") ?? "planeta"} ({pClass}).";
                }
                return null;

            case "ShipTargeted":
                if (data.TryGetProperty("TargetLocked", out var tl) && tl.ValueKind == JsonValueKind.True &&
                    string.Equals(GetStr(data, "LegalStatus"), "Wanted", StringComparison.OrdinalIgnoreCase) &&
                    data.TryGetProperty("Bounty", out var bp) && bp.ValueKind == JsonValueKind.Number &&
                    bp.TryGetInt64(out long bounty) && bounty >= 250_000)
                {
                    return $"Objetivo buscado fijado: {GetStr(data, "Ship_Localised") ?? GetStr(data, "Ship") ?? "nave"} con recompensa de {bounty} créditos.";
                }
                return null;

            case "ScanOrganic":
                if (string.Equals(GetStr(data, "ScanType"), "Analyse", StringComparison.OrdinalIgnoreCase))
                    return $"Muestra exobiológica completada: {GetStr(data, "Species_Localised") ?? GetStr(data, "Species") ?? "especie orgánica"}.";
                return null;

            case "CockpitBreached":
                return "¡Emergencia! Cabina comprometida, soporte vital activo.";

            case "UnderAttack":
                return "La nave está recibiendo fuego hostil.";

            default:
                return null;
        }
    }

    public void ClearHistory()
    {
        lock (_historyLock)
            _history.Clear();
    }

    public string GetActiveProviderSummary()
    {
        var cfg = _configService.Current;
        if (!cfg.EnableAiCopilot)
            return "OFF";

        return cfg.AiActiveProvider switch
        {
            "gemini" => $"Gemini ({cfg.AiGeminiModel})",
            "openai" => $"OpenAI-compatible ({cfg.AiOpenAiModel})",
            _ => $"Local / Ollama ({cfg.AiOllamaModel})"
        };
    }

    public async Task<(bool Success, string Reply)> TestConnectionAsync()
    {
        var cfg = _configService.Current;
        if (!cfg.EnableAiCopilot)
            return (false, "Copiloto IA desactivado.");

        string systemPrompt = "Responde únicamente con 'OK - COVAS en línea' en el idioma del usuario.";
        return await SendProviderRequestAsync(cfg, systemPrompt, new List<(string, string)> { ("user", "Test de conexión.") });
    }

    public async Task<(bool Success, List<string> Models, string Message)> FetchAvailableModelsAsync(
        string provider,
        string endpoint,
        string apiKey)
    {
        try
        {
            if (provider == "gemini")
            {
                if (string.IsNullOrWhiteSpace(apiKey))
                    return (false, new List<string>(), "Introduce primero tu Gemini API Key.");

                string url = $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(apiKey.Trim())}";
                using var resp = await _httpClient.GetAsync(url);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    return (false, new List<string>(), $"Gemini HTTP {(int)resp.StatusCode}: {Truncate(body, 140)}");

                using var doc = JsonDocument.Parse(body);
                var models = new List<string>();
                if (doc.RootElement.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in arr.EnumerateArray())
                    {
                        bool supportsGenerate = false;
                        if (m.TryGetProperty("supportedGenerationMethods", out var methods) && methods.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var method in methods.EnumerateArray())
                            {
                                if (string.Equals(method.GetString(), "generateContent", StringComparison.OrdinalIgnoreCase))
                                {
                                    supportsGenerate = true;
                                    break;
                                }
                            }
                        }
                        if (!supportsGenerate)
                            continue;

                        string rawName = m.TryGetProperty("name", out var np) ? (np.GetString() ?? "") : "";
                        if (rawName.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                            rawName = rawName["models/".Length..];
                        if (!string.IsNullOrWhiteSpace(rawName) &&
                            !rawName.Contains("embedding", StringComparison.OrdinalIgnoreCase) &&
                            !rawName.Contains("tts", StringComparison.OrdinalIgnoreCase) &&
                            !rawName.Contains("image", StringComparison.OrdinalIgnoreCase) &&
                            !rawName.Contains("imagen", StringComparison.OrdinalIgnoreCase) &&
                            !rawName.Contains("veo", StringComparison.OrdinalIgnoreCase) &&
                            !rawName.Contains("aqa", StringComparison.OrdinalIgnoreCase) &&
                            !rawName.Contains("robotics", StringComparison.OrdinalIgnoreCase) &&
                            !rawName.Contains("computer-use", StringComparison.OrdinalIgnoreCase))
                        {
                            models.Add(rawName);
                        }
                    }
                }

                models = models
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(static n => n.Contains("flash-lite", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(static n => n.Contains("2.5-flash", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(static n => n.Contains("flash", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(static n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return (true, models, $"Gemini: {models.Count}");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                    return (false, new List<string>(), "Introduce la URL del endpoint.");

                string url = endpoint.TrimEnd('/') + "/models";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(apiKey))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

                using var resp = await _httpClient.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    return (false, new List<string>(), $"HTTP {(int)resp.StatusCode}: {Truncate(body, 140)}");

                using var doc = JsonDocument.Parse(body);
                var models = new List<string>();
                if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataArr.EnumerateArray())
                    {
                        string id = item.TryGetProperty("id", out var idP) ? (idP.GetString() ?? "") : "";
                        if (!string.IsNullOrWhiteSpace(id))
                            models.Add(id);
                    }
                }
                else if (doc.RootElement.TryGetProperty("models", out var mArr) && mArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in mArr.EnumerateArray())
                    {
                        string id = item.TryGetProperty("name", out var nP) ? (nP.GetString() ?? "") : "";
                        if (!string.IsNullOrWhiteSpace(id))
                            models.Add(id);
                    }
                }

                models = models.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static n => n, StringComparer.OrdinalIgnoreCase).ToList();
                return (true, models, $"{models.Count}");
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("AI.Models", $"Failed to fetch available models for provider '{provider}': {ex.Message}");
            return (false, new List<string>(), $"Error al consultar modelos: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a CancellationTokenSource for a new request and optionally cancels any previous
    /// in-flight request so user queries never pile up and chain stale answers together.
    /// </summary>
    private CancellationTokenSource CreateRequestScope(bool cancelPrevious)
    {
        lock (_activeRequestCtsLock)
        {
            if (cancelPrevious)
            {
                try { _activeRequestCts?.Cancel(); } catch { }
            }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_timerCts.Token);
            if (cancelPrevious || _activeRequestCts is null)
                _activeRequestCts = cts;
            return cts;
        }
    }

    public async Task<(bool Success, string Reply)> AskAsync(
        string userPrompt,
        bool speakOverride = true,
        bool streamToUi = true,
        bool cancelPrevious = true)
    {
        var cfg = _configService.Current;
        if (!cfg.EnableAiCopilot)
            return (false, "Activa el Copiloto IA en AJUSTES -> Servidor Web -> Copiloto IA.");
        if (string.IsNullOrWhiteSpace(userPrompt))
            return (false, "");

        // 0. Instant zero-LLM voice macro check (0 ms latency, 0 API tokens)
        if (cfg.AiVoiceMacrosEnabled && cfg.AiAllowActions)
        {
            var (matched, macroReply) = await TryExecuteVoiceMacroAsync(cfg, userPrompt);
            if (matched)
                return (true, macroReply);
        }

        // 0b. Instant zero-LLM repeat-last-reply or single-stat cockpit telemetry query (0 ms, 0 tokens)
        if (IsRepeatVoiceCommand(userPrompt, cfg.AiWakeWord))
        {
            if (!string.IsNullOrWhiteSpace(_lastSpokenReply))
            {
                if (speakOverride && cfg.AiSpeakReplies)
                    _speechService.SpeakCustomText(NormalizeTextForSpeech(_lastSpokenReply, cfg.InterfaceLanguage));
                return (true, _lastSpokenReply);
            }
        }
        var (instantOk, instantReply) = TryAnswerInstantTelemetryQuery(cfg, userPrompt);
        if (instantOk)
        {
            _lastSpokenReply = instantReply;
            if (speakOverride && cfg.AiSpeakReplies)
                _speechService.SpeakCustomText(NormalizeTextForSpeech(instantReply, cfg.InterfaceLanguage));
            return (true, instantReply);
        }

        // 0c. Voice/text screen vision trigger ("mira mi pantalla" / "look at my screen")
        if (IsScreenVisionPrompt(userPrompt, cfg.AiWakeWord))
            return await AskWithScreenVisionAsync(userPrompt, speakOverride, streamToUi);

        // If the user asks about GalNet and articles have not been fetched yet, load them on the fly
        if (_galnetService.Articles.Count == 0 &&
            (userPrompt.Contains("galnet", StringComparison.OrdinalIgnoreCase) ||
             userPrompt.Contains("notic", StringComparison.OrdinalIgnoreCase) ||
             userPrompt.Contains("news", StringComparison.OrdinalIgnoreCase)))
        {
            await _galnetService.RefreshAsync(cfg.InterfaceLanguage);
        }

        using var reqCts = CreateRequestScope(cancelPrevious);
        var ct = reqCts.Token;
        try
        {
            await _askLock.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return (false, "");
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LastFirstTokenLatencyMs = 0;
            string systemPrompt = BuildSystemPrompt(cfg, _fileWatcher.State, userPrompt);
            int thinkingBudget = ResolveThinkingBudget(cfg, userPrompt);
            LastThinkingUsed = thinkingBudget > 0 &&
                string.Equals(cfg.AiActiveProvider, "gemini", StringComparison.OrdinalIgnoreCase) &&
                SupportsGeminiThinking(cfg.AiGeminiModel);
            List<(string Role, string Content)> messages;
            lock (_historyLock)
            {
                messages = new List<(string, string)>(_history) { ("user", userPrompt.Trim()) };
                if (messages.Count > 12)
                    messages.RemoveRange(0, messages.Count - 12);
            }

            var streamSpeaker = new SentenceStreamSpeaker(
                _speechService,
                cfg.InterfaceLanguage,
                speakEnabled: speakOverride && cfg.AiSpeakReplies,
                onTextChunk: streamToUi ? chunk => CopilotStreamChunk?.Invoke(chunk) : null,
                onFirstToken: () => { if (LastFirstTokenLatencyMs == 0) LastFirstTokenLatencyMs = sw.ElapsedMilliseconds; });

            var (success, rawReply) = await SendProviderRequestAsync(cfg, systemPrompt, messages, thinkingBudget, streamSpeaker, ct);
            if (ct.IsCancellationRequested)
                return (false, "");
            if (!success)
            {
                LastResponseLatencyMs = sw.ElapsedMilliseconds;
                return (false, rawReply);
            }

            // 2-step ReAct query loop: if COVAS requested live lookups via [[QUERY:...]], execute them and ask for the final reasoned answer
            if (rawReply.Contains("[[QUERY", StringComparison.OrdinalIgnoreCase))
            {
                streamSpeaker = new SentenceStreamSpeaker(
                    _speechService,
                    cfg.InterfaceLanguage,
                    speakEnabled: speakOverride && cfg.AiSpeakReplies,
                    onTextChunk: streamToUi ? chunk => CopilotStreamChunk?.Invoke(chunk) : null);
                rawReply = await ResolveQueriesAndFollowUpAsync(cfg, systemPrompt, messages, rawReply, streamSpeaker, ct);
                if (ct.IsCancellationRequested)
                    return (false, "");
            }

            streamSpeaker.FlushRemaining();
            LastResponseLatencyMs = sw.ElapsedMilliseconds;

            string finalReply = await ProcessEmbeddedActionsAsync(cfg, rawReply, isHandsFree: false);
            if (!string.IsNullOrWhiteSpace(finalReply))
                _lastSpokenReply = finalReply;
            lock (_historyLock)
            {
                _history.Add(("user", userPrompt.Trim()));
                _history.Add(("assistant", finalReply));
                if (_history.Count > 12)
                    _history.RemoveRange(0, _history.Count - 12);
            }

            if (speakOverride && cfg.AiSpeakReplies && !string.IsNullOrWhiteSpace(finalReply) && !streamSpeaker.SpokeAnySentence)
                _speechService.SpeakCustomText(NormalizeTextForSpeech(finalReply, cfg.InterfaceLanguage));

            return (true, finalReply);
        }
        catch (OperationCanceledException)
        {
            return (false, "");
        }
        finally
        {
            _askLock.Release();
        }
    }

    /// <summary>
    /// Runs a tactical pre-flight check inspecting fuel, limpets, FSD health, missing mission cargo,
    /// hull/shields, and route fuel stars, then asks COVAS for a concise spoken briefing.
    /// </summary>
    public async Task<(bool Success, string Reply)> RunPreFlightCheckAsync()
    {
        var s = _fileWatcher.State;
        var issues = new List<string>();
        var okNotes = new List<string>();

        if (s.FuelMax > 0)
        {
            double pct = (s.FuelMain / s.FuelMax) * 100.0;
            if (pct < 50) issues.Add($"Combustible bajo ({pct:0}%)");
            else okNotes.Add($"combustible al {pct:0}%");
        }
        if (s.HasLimpetController && s.LimpetCount == 0)
            issues.Add("Tienes controlador de drones pero 0 drones en bodega");
        if (s.FsdHealthPercent is < 85)
            issues.Add($"Motor FSD desgastado al {s.FsdHealthPercent:0}%");
        if (s.HullHealth is > 0 and < 90)
            issues.Add($"Casco al {s.HullHealth:0}%");
        if (s.RouteHasNoFuelStar && s.NavRoute.Count > 1)
            issues.Add("La ruta activa no contiene estrellas repostables");

        var missingCargo = s.ActiveMissions
            .Where(static m => !string.IsNullOrWhiteSpace(m.Commodity) && m.Count is > 0 && m.PassengerMission != true)
            .GroupBy(static m => m.Commodity.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => s.CargoInventory.Where(c => EliteFileWatcherService.CommodityMatches(c.Name, c.DisplayName, g.Key)).Sum(static c => c.Count) < g.Sum(static m => m.Count!.Value))
            .Select(static g => g.Key)
            .ToList();
        if (missingCargo.Count > 0)
            issues.Add($"Falta carga de misión en bodega: {string.Join(", ", missingCargo)}");

        string prompt = issues.Count == 0
            ? $"Realiza un chequeo pre-vuelo en 2 frases: todos los sistemas nominales ({string.Join(", ", okNotes)}). Da autorización de despegue."
            : $"Realiza un chequeo pre-vuelo en 2 frases alertando de estos avisos antes de despegar: {string.Join("; ", issues)}.";

        return await AskAsync(prompt);
    }

    public async Task<(bool Success, string Reply)> SummarizeGalnetAsync()
    {
        var cfg = _configService.Current;
        if (!cfg.EnableAiCopilot)
            return (false, "Activa el Copiloto IA en AJUSTES.");

        var articles = _galnetService.Articles;
        if (articles.Count == 0)
        {
            await _galnetService.RefreshAsync(cfg.InterfaceLanguage);
            articles = _galnetService.Articles;
        }
        if (articles.Count == 0)
            return (false, "No hay noticias de GalNet disponibles en este momento.");

        var sb = new StringBuilder();
        sb.AppendLine("Resume de forma breve y clara en 4 o 5 viñetas (estilo boletín de a bordo COVAS) estas últimas noticias de GalNet:");
        foreach (var a in articles.Take(6))
        {
            string excerpt = a.Body.Length > 360 ? a.Body[..360] + "..." : a.Body;
            sb.AppendLine($"- [{a.DateDisplay}] {a.Title}: {excerpt}");
        }

        return await AskAsync(sb.ToString());
    }

    public async Task<(bool Success, string Reply)> SummarizeSessionAsync()
    {
        var cfg = _configService.Current;
        if (!cfg.EnableAiCopilot)
            return (false, "Activa el Copiloto IA en AJUSTES.");

        return await AskAsync("Redacta una entrada breve de Cuaderno de Bitácora (máximo 1 párrafo, estilo diario de comandante) resumiendo mi sesión actual con los datos de telemetría disponibles.");
    }

    public async Task<(bool Success, string Transcript, string Reply)> AskWithAudioWavAsync(byte[] wavBytes, bool isHandsFree = false)
    {
        var cfg = _configService.Current;
        if (!cfg.EnableAiCopilot)
            return (false, "", "Activa el Copiloto IA en AJUSTES.");
        if (wavBytes is null || wavBytes.Length <= 44)
            return (false, "", "No se detectó audio del micrófono.");

        string wakeWord = (cfg.AiWakeWord ?? "COVAS").Trim();
        bool inFollowUpWindow = DateTimeOffset.UtcNow <= _wakeWordFollowUpUntilUtc;
        bool requireWake = isHandsFree && cfg.AiRequireWakeWord && !string.IsNullOrWhiteSpace(wakeWord) && !inFollowUpWindow;

        // 0. Offline Whisper.cpp STT when explicitly selected by the user (works with all LLM providers)
        if (string.Equals(cfg.AiSttEngine, "whisper_local", StringComparison.OrdinalIgnoreCase) &&
            OfflineWhisperStt is not null)
        {
            var (wOk, wTranscript) = await OfflineWhisperStt.TranscribeWavAsync(wavBytes, cfg.InterfaceLanguage, cfg.WhisperModelId);
            if (!wOk || string.IsNullOrWhiteSpace(wTranscript))
                return (false, "", string.IsNullOrWhiteSpace(wTranscript) ? "No se pudo transcribir el audio con Whisper." : wTranscript);
            if (IsSilenceVoiceCommand(wTranscript))
            {
                _wakeWordFollowUpUntilUtc = DateTimeOffset.MinValue;
                _speechService.StopSpeaking();
                return (true, "🔇", "");
            }
            if (requireWake && !wTranscript.Contains(wakeWord, StringComparison.OrdinalIgnoreCase))
                return (true, "", "");
            var (wChatOk, wChatReply) = await AskAsync(wTranscript);
            if (wChatOk && isHandsFree)
                _wakeWordFollowUpUntilUtc = DateTimeOffset.UtcNow.AddSeconds(18);
            return (wChatOk, wTranscript, wChatReply);
        }

        // 1. Gemini: direct multimodal WAV audio with SSE sentence-by-sentence TTS streaming + multi-turn history
        if (cfg.AiActiveProvider == "gemini")
        {
            using var reqCts = CreateRequestScope(cancelPrevious: !isHandsFree);
            var ct = reqCts.Token;
            try
            {
                await _askLock.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return (false, "", "");
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                LastFirstTokenLatencyMs = 0;
                LastThinkingUsed = string.Equals(cfg.AiThinkingMode, "yes", StringComparison.OrdinalIgnoreCase) &&
                    SupportsGeminiThinking(cfg.AiGeminiModel);

                List<(string Role, string Content)> recentHistory;
                lock (_historyLock)
                    recentHistory = new List<(string, string)>(_history.TakeLast(8));

                var streamSpeaker = new SentenceStreamSpeaker(
                    _speechService,
                    cfg.InterfaceLanguage,
                    speakEnabled: cfg.AiSpeakReplies,
                    onTextChunk: chunk => CopilotStreamChunk?.Invoke(chunk),
                    onFirstToken: () => { if (LastFirstTokenLatencyMs == 0) LastFirstTokenLatencyMs = sw.ElapsedMilliseconds; });

                var (ok, reply) = await SendGeminiAudioAsync(cfg, wavBytes, requireWake ? wakeWord : null, streamSpeaker, recentHistory, ct);
                if (ct.IsCancellationRequested)
                    return (false, "", "");
                if (!ok)
                {
                    LastResponseLatencyMs = sw.ElapsedMilliseconds;
                    return (false, "🎤 [Voz]", reply);
                }
                if (reply.Contains("[[IGNORE]]", StringComparison.OrdinalIgnoreCase))
                {
                    LastResponseLatencyMs = 0;
                    return (true, "", "");
                }
                if (reply.Contains("[[SILENCE]]", StringComparison.OrdinalIgnoreCase))
                {
                    LastResponseLatencyMs = 0;
                    _wakeWordFollowUpUntilUtc = DateTimeOffset.MinValue;
                    _speechService.StopSpeaking();
                    return (true, "🔇", "");
                }

                if (reply.Contains("[[QUERY", StringComparison.OrdinalIgnoreCase))
                {
                    string sysPrompt = BuildSystemPrompt(cfg, _fileWatcher.State);
                    streamSpeaker = new SentenceStreamSpeaker(
                        _speechService,
                        cfg.InterfaceLanguage,
                        speakEnabled: cfg.AiSpeakReplies,
                        onTextChunk: chunk => CopilotStreamChunk?.Invoke(chunk));
                    var queryBase = new List<(string, string)>(recentHistory) { ("user", "[Comando de voz del comandante]") };
                    reply = await ResolveQueriesAndFollowUpAsync(
                        cfg,
                        sysPrompt,
                        queryBase,
                        reply,
                        streamSpeaker,
                        ct);
                    if (ct.IsCancellationRequested)
                        return (false, "", "");
                }

                streamSpeaker.FlushRemaining();
                LastResponseLatencyMs = sw.ElapsedMilliseconds;

                string finalReply = await ProcessEmbeddedActionsAsync(cfg, reply, isHandsFree: isHandsFree);
                if (!string.IsNullOrWhiteSpace(finalReply))
                    _lastSpokenReply = finalReply;
                lock (_historyLock)
                {
                    _history.Add(("user", "[Comando de voz]"));
                    _history.Add(("assistant", finalReply));
                    if (_history.Count > 12)
                        _history.RemoveRange(0, _history.Count - 12);
                }

                if (isHandsFree)
                    _wakeWordFollowUpUntilUtc = DateTimeOffset.UtcNow.AddSeconds(18);
                if (cfg.AiSpeakReplies && !string.IsNullOrWhiteSpace(finalReply) && !streamSpeaker.SpokeAnySentence)
                    _speechService.SpeakCustomText(NormalizeTextForSpeech(finalReply, cfg.InterfaceLanguage));
                return (true, "🎤 [Comando de voz]", finalReply);
            }
            catch (OperationCanceledException)
            {
                return (false, "", "");
            }
            finally
            {
                _askLock.Release();
            }
        }

        // 2. OpenAI / Groq (Whisper) or Local Ollama (Windows Speech)
        var (tOk, transcript) = cfg.AiActiveProvider == "openai"
            ? await TranscribeWithWhisperAsync(cfg, wavBytes)
            : TranscribeWithWindowsSpeech(wavBytes, cfg.InterfaceLanguage, cfg);

        if (!tOk || string.IsNullOrWhiteSpace(transcript))
            return (false, "", string.IsNullOrWhiteSpace(transcript) ? "No se pudo transcribir el audio." : transcript);

        if (IsSilenceVoiceCommand(transcript))
        {
            _wakeWordFollowUpUntilUtc = DateTimeOffset.MinValue;
            _speechService.StopSpeaking();
            return (true, "🔇", "");
        }

        if (requireWake && !transcript.Contains(wakeWord, StringComparison.OrdinalIgnoreCase))
            return (true, "", "");

        var (chatOk, chatReply) = await AskAsync(transcript);
        if (chatOk && isHandsFree)
            _wakeWordFollowUpUntilUtc = DateTimeOffset.UtcNow.AddSeconds(18);
        return (chatOk, transcript, chatReply);
    }

    internal static bool IsSilenceVoiceCommand(string text)
    {
        string lower = NormalizeVoiceMatchText(text, null);
        return lower is "silencio" or "callate" or "para" or "detente"
            or "stop" or "silence" or "shut up" or "quiet"
            or "tais toi" or "arrete" or "chut"
            or "ruhe" or "stopp" or "halt" or "still"
            or "cala" or "pare"
            or "тихо" or "стоп" or "молчи"
            || lower.EndsWith(" silencio", StringComparison.Ordinal)
            || lower.EndsWith(" callate", StringComparison.Ordinal)
            || lower.EndsWith(" stop", StringComparison.Ordinal)
            || lower.EndsWith(" silence", StringComparison.Ordinal)
            || lower.EndsWith(" ruhe", StringComparison.Ordinal)
            || lower.EndsWith(" стоп", StringComparison.Ordinal);
    }

    internal static bool IsRepeatVoiceCommand(string text, string? wakeWord)
    {
        string lower = NormalizeVoiceMatchText(text, wakeWord);
        return lower is "repite" or "repitelo" or "repite eso" or "que dijiste"
            or "repeat" or "repeat that" or "say again" or "say that again"
            or "repete" or "repete ca"
            or "wiederhole" or "wiederhole das"
            or "repita" or "repita isso"
            or "повтори" or "повтори это";
    }

    internal static bool IsScreenVisionPrompt(string text, string? wakeWord)
    {
        string lower = NormalizeVoiceMatchText(text, wakeWord);
        return lower.Contains("mira mi pantalla", StringComparison.Ordinal)
            || lower.Contains("mira la pantalla", StringComparison.Ordinal)
            || lower.Contains("analiza la pantalla", StringComparison.Ordinal)
            || lower.Contains("analiza mi pantalla", StringComparison.Ordinal)
            || lower.Contains("que ves en pantalla", StringComparison.Ordinal)
            || lower.Contains("que hay en pantalla", StringComparison.Ordinal)
            || lower.Contains("look at my screen", StringComparison.Ordinal)
            || lower.Contains("analyze my screen", StringComparison.Ordinal)
            || lower.Contains("whats on my screen", StringComparison.Ordinal)
            || lower.Contains("what is on my screen", StringComparison.Ordinal);
    }

    /// <summary>
    /// Captures the active cockpit display and sends it to a multimodal LLM (Gemini / OpenAI / Vision Ollama).
    /// </summary>
    public async Task<(bool Success, string Reply)> AskWithScreenVisionAsync(
        string? userPrompt = null,
        bool speakOverride = true,
        bool streamToUi = true)
    {
        var cfg = _configService.Current;
        if (!cfg.EnableAiCopilot)
            return (false, "Activa el Copiloto IA en AJUSTES.");

        byte[]? jpeg = CapturePrimaryScreenJpeg();
        if (jpeg is null || jpeg.Length == 0)
            return (false, "No se pudo capturar la pantalla.");

        string prompt = string.IsNullOrWhiteSpace(userPrompt)
            ? (string.Equals(cfg.InterfaceLanguage, "es-ES", StringComparison.OrdinalIgnoreCase)
                ? "Analiza brevemente esta captura de cabina de Elite Dangerous y dime lo más relevante o accionable que ves."
                : "Briefly analyze this Elite Dangerous cockpit screenshot and report the most relevant or actionable details.")
            : userPrompt.Trim();

        await _askLock.WaitAsync();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LastFirstTokenLatencyMs = 0;
            string systemPrompt = BuildSystemPrompt(cfg, _fileWatcher.State, prompt);
            var streamSpeaker = new SentenceStreamSpeaker(
                _speechService,
                cfg.InterfaceLanguage,
                speakEnabled: speakOverride && cfg.AiSpeakReplies,
                onTextChunk: streamToUi ? chunk => CopilotStreamChunk?.Invoke(chunk) : null,
                onFirstToken: () => { if (LastFirstTokenLatencyMs == 0) LastFirstTokenLatencyMs = sw.ElapsedMilliseconds; });

            var (ok, rawReply) = await SendVisionProviderRequestAsync(cfg, systemPrompt, prompt, jpeg, streamSpeaker);
            streamSpeaker.FlushRemaining();
            LastResponseLatencyMs = sw.ElapsedMilliseconds;
            if (!ok)
                return (false, rawReply);

            string finalReply = await ProcessEmbeddedActionsAsync(cfg, rawReply, isHandsFree: false);
            if (!string.IsNullOrWhiteSpace(finalReply))
                _lastSpokenReply = finalReply;

            lock (_historyLock)
            {
                _history.Add(("user", $"[👁 Pantalla] {prompt}"));
                _history.Add(("assistant", finalReply));
                if (_history.Count > 12)
                    _history.RemoveRange(0, _history.Count - 12);
            }

            if (speakOverride && cfg.AiSpeakReplies && !string.IsNullOrWhiteSpace(finalReply) && !streamSpeaker.SpokeAnySentence)
                _speechService.SpeakCustomText(NormalizeTextForSpeech(finalReply, cfg.InterfaceLanguage));

            return (true, finalReply);
        }
        finally
        {
            _askLock.Release();
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gameEvents.JournalEventReceived -= OnJournalEventReceived;
        _fileWatcher.StateUpdated -= OnShipStateUpdated;
        if (_eliteBgsWatchlist is not null)
            _eliteBgsWatchlist.ConflictDetected -= OnEliteBgsConflictDetected;
        _communitySync.EdsmSystemIntelUpdated -= OnEdsmSystemIntelUpdated;
        try { _timerCts.Cancel(); } catch { }
        _timerCts.Dispose();
        _askLock.Dispose();
        _httpClient.Dispose();
    }
}
