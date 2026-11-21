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
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly object _historyLock = new();
    private readonly List<(string Role, string Content)> _history = new();
    private DateTimeOffset _lastDynamicEventUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRadioChatterUtc = DateTimeOffset.MinValue;
    private readonly HashSet<string> _announcedSignalsInSystem = new(StringComparer.OrdinalIgnoreCase);
    private int _dynamicEventBusy;
    private readonly CancellationTokenSource _timerCts = new();

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
        GameEventHub gameEvents)
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
        gameEvents.JournalEventReceived += OnJournalEventReceived;
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

        if (System.Threading.Interlocked.Exchange(ref _dynamicEventBusy, 1) != 0)
            return;

        _lastDynamicEventUtc = DateTimeOffset.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                string prompt = $"Evento táctico recién ocurrido: {eventCue}. Da un comentario breve de copiloto (1 o 2 frases máximo, tono natural de cabina).";
                var (ok, reply) = await AskAsync(prompt, speakOverride: true);
                if (ok && !string.IsNullOrWhiteSpace(reply))
                    CopilotMessageAdded?.Invoke("COVAS (Evento)", reply);
            }
            catch
            {
                // Ignore network error during dynamic commentary
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _dynamicEventBusy, 0);
            }
        });
    }

    private static string? JsonStr(JsonElement el, string p) =>
        el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

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
            case "ApproachBody" when cfg.AiTacticalHighGravity:
                {
                    string bodyName = JsonStr(data, "Body") ?? "";
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
                    string signalRaw = JsonStr(data, "SignalName") ?? "";
                    string signalLoc = JsonStr(data, "SignalName_Localised") ?? signalRaw;
                    string ussType = JsonStr(data, "USSType") ?? "";
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
                    else if (string.Equals(JsonStr(data, "SignalType"), "Megaship", StringComparison.OrdinalIgnoreCase))
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
                    string motherlode = JsonStr(data, "MotherlodeMaterial_Localised") ?? JsonStr(data, "MotherlodeMaterial") ?? "";
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
                            string rawName = JsonStr(mat, "Name") ?? "";
                            string locName = JsonStr(mat, "Name_Localised") ?? rawName;
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

                    string channel = (JsonStr(data, "Channel") ?? "").ToLowerInvariant();
                    string from = JsonStr(data, "From_Localised") ?? JsonStr(data, "From") ?? "";
                    string msgRaw = JsonStr(data, "Message") ?? "";
                    string msgLoc = JsonStr(data, "Message_Localised") ?? msgRaw;
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
                string? starClass = JsonStr(data, "StarClass");
                if (EliteFileWatcherService.IsHazardousStarClass(starClass))
                    return $"Saltando a {JsonStr(data, "StarSystem") ?? "sistema"} con estrella peligrosa clase {starClass}.";
                return null;

            case "Scan":
                string? pClass = JsonStr(data, "PlanetClass") ?? "";
                if (pClass.Contains("Earthlike", StringComparison.OrdinalIgnoreCase) ||
                    pClass.Contains("Water world", StringComparison.OrdinalIgnoreCase) ||
                    pClass.Contains("Ammonia world", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Descubrimiento valioso escaneado: {JsonStr(data, "BodyName") ?? "planeta"} ({pClass}).";
                }
                return null;

            case "ShipTargeted":
                if (data.TryGetProperty("TargetLocked", out var tl) && tl.ValueKind == JsonValueKind.True &&
                    string.Equals(JsonStr(data, "LegalStatus"), "Wanted", StringComparison.OrdinalIgnoreCase) &&
                    data.TryGetProperty("Bounty", out var bp) && bp.ValueKind == JsonValueKind.Number &&
                    bp.TryGetInt64(out long bounty) && bounty >= 250_000)
                {
                    return $"Objetivo buscado fijado: {JsonStr(data, "Ship_Localised") ?? JsonStr(data, "Ship") ?? "nave"} con recompensa de {bounty} créditos.";
                }
                return null;

            case "ScanOrganic":
                if (string.Equals(JsonStr(data, "ScanType"), "Analyse", StringComparison.OrdinalIgnoreCase))
                    return $"Muestra exobiológica completada: {JsonStr(data, "Species_Localised") ?? JsonStr(data, "Species") ?? "especie orgánica"}.";
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
                            !rawName.Contains("embedding", StringComparison.OrdinalIgnoreCase))
                        {
                            models.Add(rawName);
                        }
                    }
                }

                models = models
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(static n => n.Contains("flash", StringComparison.OrdinalIgnoreCase))
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
            return (false, new List<string>(), $"Error al consultar modelos: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Reply)> AskAsync(string userPrompt, bool speakOverride = true)
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

        // If the user asks about GalNet and articles have not been fetched yet, load them on the fly
        if (_galnetService.Articles.Count == 0 &&
            (userPrompt.Contains("galnet", StringComparison.OrdinalIgnoreCase) ||
             userPrompt.Contains("notic", StringComparison.OrdinalIgnoreCase) ||
             userPrompt.Contains("news", StringComparison.OrdinalIgnoreCase)))
        {
            await _galnetService.RefreshAsync(cfg.InterfaceLanguage);
        }

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
            _history.Add(("user", userPrompt.Trim()));
            if (_history.Count > 12)
                _history.RemoveRange(0, _history.Count - 12);
            messages = new List<(string, string)>(_history);
        }

        var streamSpeaker = new SentenceStreamSpeaker(
            _speechService,
            cfg.InterfaceLanguage,
            speakEnabled: speakOverride && cfg.AiSpeakReplies,
            onTextChunk: chunk => CopilotStreamChunk?.Invoke(chunk),
            onFirstToken: () => { if (LastFirstTokenLatencyMs == 0) LastFirstTokenLatencyMs = sw.ElapsedMilliseconds; });

        var (success, rawReply) = await SendProviderRequestAsync(cfg, systemPrompt, messages, thinkingBudget, streamSpeaker);
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
                onTextChunk: chunk => CopilotStreamChunk?.Invoke(chunk));
            rawReply = await ResolveQueriesAndFollowUpAsync(cfg, systemPrompt, messages, rawReply, streamSpeaker);
        }

        streamSpeaker.FlushRemaining();
        LastResponseLatencyMs = sw.ElapsedMilliseconds;

        string finalReply = await ProcessEmbeddedActionsAsync(cfg, rawReply);
        lock (_historyLock)
        {
            _history.Add(("assistant", finalReply));
            if (_history.Count > 12)
                _history.RemoveRange(0, _history.Count - 12);
        }

        if (speakOverride && cfg.AiSpeakReplies && !string.IsNullOrWhiteSpace(finalReply) && !streamSpeaker.SpokeAnySentence)
            _speechService.SpeakCustomText(NormalizeTextForSpeech(finalReply, cfg.InterfaceLanguage));

        return (true, finalReply);
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
            .Where(g => s.CargoInventory.Where(c => string.Equals(c.Name?.Trim(), g.Key, StringComparison.OrdinalIgnoreCase)).Sum(static c => c.Count) < g.Sum(static m => m.Count!.Value))
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
        bool requireWake = isHandsFree && cfg.AiRequireWakeWord && !string.IsNullOrWhiteSpace(wakeWord);

        // 1. Gemini: direct multimodal WAV audio with SSE sentence-by-sentence TTS streaming
        if (cfg.AiActiveProvider == "gemini")
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LastFirstTokenLatencyMs = 0;
            LastThinkingUsed = string.Equals(cfg.AiThinkingMode, "yes", StringComparison.OrdinalIgnoreCase) &&
                SupportsGeminiThinking(cfg.AiGeminiModel);
            var streamSpeaker = new SentenceStreamSpeaker(
                _speechService,
                cfg.InterfaceLanguage,
                speakEnabled: cfg.AiSpeakReplies,
                onTextChunk: chunk => CopilotStreamChunk?.Invoke(chunk),
                onFirstToken: () => { if (LastFirstTokenLatencyMs == 0) LastFirstTokenLatencyMs = sw.ElapsedMilliseconds; });

            var (ok, reply) = await SendGeminiAudioAsync(cfg, wavBytes, requireWake ? wakeWord : null, streamSpeaker);
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
                reply = await ResolveQueriesAndFollowUpAsync(
                    cfg,
                    sysPrompt,
                    new List<(string, string)> { ("user", "[Comando de voz del comandante]") },
                    reply,
                    streamSpeaker);
            }

            streamSpeaker.FlushRemaining();
            LastResponseLatencyMs = sw.ElapsedMilliseconds;

            string finalReply = await ProcessEmbeddedActionsAsync(cfg, reply);
            if (cfg.AiSpeakReplies && !string.IsNullOrWhiteSpace(finalReply) && !streamSpeaker.SpokeAnySentence)
                _speechService.SpeakCustomText(NormalizeTextForSpeech(finalReply, cfg.InterfaceLanguage));
            return (true, "🎤 [Comando de voz]", finalReply);
        }

        // 2. OpenAI / Groq (Whisper) or Local Ollama (Windows Speech)
        var (tOk, transcript) = cfg.AiActiveProvider == "openai"
            ? await TranscribeWithWhisperAsync(cfg, wavBytes)
            : TranscribeWithWindowsSpeech(wavBytes, cfg.InterfaceLanguage);

        if (!tOk || string.IsNullOrWhiteSpace(transcript))
            return (false, "", string.IsNullOrWhiteSpace(transcript) ? "No se pudo transcribir el audio." : transcript);

        if (IsSilenceVoiceCommand(transcript))
        {
            _speechService.StopSpeaking();
            return (true, "🔇", "");
        }

        if (requireWake && !transcript.Contains(wakeWord, StringComparison.OrdinalIgnoreCase))
            return (true, "", "");

        var (chatOk, chatReply) = await AskAsync(transcript);
        return (chatOk, transcript, chatReply);
    }

    private static bool IsSilenceVoiceCommand(string text)
    {
        string lower = text.Trim().ToLowerInvariant();
        return lower is "silencio" or "cállate" or "callate" or "para" or "detente" or "stop" or "silence" or "shut up" or "quiet"
            || lower.EndsWith(" silencio", StringComparison.Ordinal)
            || lower.EndsWith(" cállate", StringComparison.Ordinal)
            || lower.EndsWith(" callate", StringComparison.Ordinal)
            || lower.EndsWith(" stop", StringComparison.Ordinal)
            || lower.EndsWith(" silence", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { _timerCts.Cancel(); } catch { }
        _timerCts.Dispose();
        _httpClient.Dispose();
    }
}
