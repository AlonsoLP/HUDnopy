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
using HUDnopy.Models;

namespace HUDnopy.Services;

/// <summary>
/// Conversational COVAS AI copilot supporting Ollama, Gemini, OpenAI, voice input, and dynamic commentary.
/// </summary>
public sealed class AiCopilotService : IDisposable
{
    private readonly ConfigService _configService;
    private readonly EliteFileWatcherService _fileWatcher;
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
    private int _dynamicEventBusy;
    private readonly CancellationTokenSource _timerCts = new();

    public event Action<string, string>? CopilotMessageAdded;
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

    public AiCopilotService(
        ConfigService configService,
        EliteFileWatcherService fileWatcher,
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

        // Express System Arrival Briefing on FSDJump (personal note, carrier presence, or security)
        if (cfg.AiSystemArrivalBriefing && gameEvent.EventType == "FSDJump")
        {
            string sys = gameEvent.Data.TryGetProperty("StarSystem", out var sp) && sp.ValueKind == JsonValueKind.String
                ? sp.GetString() ?? ""
                : "";
            if (!string.IsNullOrWhiteSpace(sys))
            {
                var notes = new List<string>();
                if (cfg.SystemNotes.TryGetValue(sys, out string? sysNote) && !string.IsNullOrWhiteSpace(sysNote))
                    notes.Add($"Nota guardada: {sysNote}");
                if (_fileWatcher.State.Carrier is { } fc && string.Equals(fc.System, sys, StringComparison.OrdinalIgnoreCase))
                    notes.Add($"Tu portanaves {fc.Name} se encuentra en este sistema");
                if (gameEvent.Data.TryGetProperty("SystemSecurity_Localised", out var secP) && secP.ValueKind == JsonValueKind.String)
                {
                    string sec = secP.GetString() ?? "";
                    if (sec.Contains("Anarchy", StringComparison.OrdinalIgnoreCase) || sec.Contains("Anarqu", StringComparison.OrdinalIgnoreCase))
                        notes.Add("Sistema sin ley (Anarquía)");
                }

                if (notes.Count > 0)
                {
                    string briefing = $"Llegada a {sys}. {string.Join(". ", notes)}.";
                    CopilotMessageAdded?.Invoke("COVAS (Briefing)", briefing);
                    if (cfg.AiSpeakReplies)
                        _speechService.SpeakCustomText(NormalizeTextForSpeech(briefing, cfg.InterfaceLanguage));
                }
            }
        }

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
                // Ignorar error de red en comentario dinámico
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _dynamicEventBusy, 0);
            }
        });
    }

    private static string? BuildDynamicEventCue(GameJournalEvent gameEvent)
    {
        var data = gameEvent.Data;
        static string? Str(JsonElement el, string p) =>
            el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        switch (gameEvent.EventType)
        {
            case "StartJump":
                string? starClass = Str(data, "StarClass");
                if (EliteFileWatcherService.IsHazardousStarClass(starClass))
                    return $"Saltando a {Str(data, "StarSystem") ?? "sistema"} con estrella peligrosa clase {starClass}.";
                return null;

            case "Scan":
                string? pClass = Str(data, "PlanetClass") ?? "";
                if (pClass.Contains("Earthlike", StringComparison.OrdinalIgnoreCase) ||
                    pClass.Contains("Water world", StringComparison.OrdinalIgnoreCase) ||
                    pClass.Contains("Ammonia world", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Descubrimiento valioso escaneado: {Str(data, "BodyName") ?? "planeta"} ({pClass}).";
                }
                return null;

            case "ShipTargeted":
                if (data.TryGetProperty("TargetLocked", out var tl) && tl.ValueKind == JsonValueKind.True &&
                    string.Equals(Str(data, "LegalStatus"), "Wanted", StringComparison.OrdinalIgnoreCase) &&
                    data.TryGetProperty("Bounty", out var bp) && bp.ValueKind == JsonValueKind.Number &&
                    bp.TryGetInt64(out long bounty) && bounty >= 250_000)
                {
                    return $"Objetivo buscado fijado: {Str(data, "Ship_Localised") ?? Str(data, "Ship") ?? "nave"} con recompensa de {bounty} créditos.";
                }
                return null;

            case "ScanOrganic":
                if (string.Equals(Str(data, "ScanType"), "Analyse", StringComparison.OrdinalIgnoreCase))
                    return $"Muestra exobiológica completada: {Str(data, "Species_Localised") ?? Str(data, "Species") ?? "especie orgánica"}.";
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

        // Si el usuario pregunta por GalNet y aún no se descargaron noticias, cargarlas al vuelo
        if (_galnetService.Articles.Count == 0 &&
            (userPrompt.Contains("galnet", StringComparison.OrdinalIgnoreCase) ||
             userPrompt.Contains("notic", StringComparison.OrdinalIgnoreCase) ||
             userPrompt.Contains("news", StringComparison.OrdinalIgnoreCase)))
        {
            await _galnetService.RefreshAsync(cfg.InterfaceLanguage);
        }

        string systemPrompt = BuildSystemPrompt(cfg, _fileWatcher.State);
        List<(string Role, string Content)> messages;
        lock (_historyLock)
        {
            _history.Add(("user", userPrompt.Trim()));
            if (_history.Count > 12)
                _history.RemoveRange(0, _history.Count - 12);
            messages = new List<(string, string)>(_history);
        }

        var (success, rawReply) = await SendProviderRequestAsync(cfg, systemPrompt, messages);
        if (!success)
            return (false, rawReply);

        string finalReply = await ProcessEmbeddedActionsAsync(cfg, rawReply);
        lock (_historyLock)
        {
            _history.Add(("assistant", finalReply));
            if (_history.Count > 12)
                _history.RemoveRange(0, _history.Count - 12);
        }

        if (speakOverride && cfg.AiSpeakReplies && !string.IsNullOrWhiteSpace(finalReply))
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

        // 1. Gemini: direct multimodal WAV audio
        if (cfg.AiActiveProvider == "gemini")
        {
            var (ok, reply) = await SendGeminiAudioAsync(cfg, wavBytes, requireWake ? wakeWord : null);
            if (!ok)
                return (false, "🎤 [Voz]", reply);
            if (reply.Contains("[[IGNORE]]", StringComparison.OrdinalIgnoreCase))
                return (true, "", "");
            if (reply.Contains("[[SILENCE]]", StringComparison.OrdinalIgnoreCase))
            {
                _speechService.StopSpeaking();
                return (true, "🔇", "");
            }

            string finalReply = await ProcessEmbeddedActionsAsync(cfg, reply);
            if (cfg.AiSpeakReplies && !string.IsNullOrWhiteSpace(finalReply))
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

    private async Task<(bool Success, string Reply)> SendGeminiAudioAsync(AppConfig cfg, byte[] wavBytes, string? requiredWakeWord = null)
    {
        if (string.IsNullOrWhiteSpace(cfg.AiGeminiApiKey) || string.IsNullOrWhiteSpace(cfg.AiGeminiModel))
            return (false, "Configura tu Gemini API Key y modelo en AJUSTES.");

        string model = cfg.AiGeminiModel.Trim();
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(cfg.AiGeminiApiKey.Trim())}";
        string systemPrompt = BuildSystemPrompt(cfg, _fileWatcher.State);

        string audioInstruction = string.IsNullOrWhiteSpace(requiredWakeWord)
            ? "Escucha el mensaje de voz del comandante y responde directamente. Si solo pide silencio o parar de hablar, responde únicamente [[SILENCE]]."
            : $"Escucha el audio adjunto. Si el hablante NO pronuncia la palabra de activación '{requiredWakeWord}', responde ÚNICAMENTE [[IGNORE]]. Si pide silencio o parar de hablar, responde ÚNICAMENTE [[SILENCE]]. En caso contrario, responde directamente.";

        var payload = new
        {
            system_instruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = new object[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = audioInstruction },
                        new
                        {
                            inline_data = new
                            {
                                mime_type = "audio/wav",
                                data = Convert.ToBase64String(wavBytes)
                            }
                        }
                    }
                }
            },
            generationConfig = new { temperature = 0.6, maxOutputTokens = 500 }
        };

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _httpClient.PostAsync(url, content);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return (false, $"Gemini Audio HTTP {(int)resp.StatusCode}: {Truncate(body, 180)}");

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("candidates", out var cands) &&
                cands.ValueKind == JsonValueKind.Array &&
                cands.GetArrayLength() > 0 &&
                cands[0].TryGetProperty("content", out var cObj) &&
                cObj.TryGetProperty("parts", out var parts) &&
                parts.ValueKind == JsonValueKind.Array &&
                parts.GetArrayLength() > 0 &&
                parts[0].TryGetProperty("text", out var txtProp))
            {
                return (true, txtProp.GetString()?.Trim() ?? "");
            }

            return (false, "Gemini no devolvió texto para el audio.");
        }
        catch (Exception ex)
        {
            return (false, $"Error Gemini Audio: {ex.Message}");
        }
    }

    private async Task<(bool Success, string Transcript)> TranscribeWithWhisperAsync(AppConfig cfg, byte[] wavBytes)
    {
        if (string.IsNullOrWhiteSpace(cfg.AiOpenAiEndpoint) || string.IsNullOrWhiteSpace(cfg.AiOpenAiApiKey))
            return (false, "Configura el endpoint y la API Key en AJUSTES.");

        string url = cfg.AiOpenAiEndpoint.TrimEnd('/') + "/audio/transcriptions";
        string whisperModel = cfg.AiOpenAiEndpoint.Contains("groq.com", StringComparison.OrdinalIgnoreCase)
            ? "whisper-large-v3-turbo"
            : "whisper-1";

        try
        {
            using var form = new MultipartFormDataContent();
            var audioContent = new ByteArrayContent(wavBytes);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audioContent, "file", "voice.wav");
            form.Add(new StringContent(whisperModel), "model");

            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.AiOpenAiApiKey.Trim());

            using var resp = await _httpClient.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return (false, $"Whisper HTTP {(int)resp.StatusCode}: {Truncate(body, 140)}");

            using var doc = JsonDocument.Parse(body);
            string text = doc.RootElement.TryGetProperty("text", out var tp) ? (tp.GetString() ?? "").Trim() : "";
            return (!string.IsNullOrWhiteSpace(text), text);
        }
        catch (Exception ex)
        {
            return (false, $"Error Whisper: {ex.Message}");
        }
    }

    private static (bool Success, string Transcript) TranscribeWithWindowsSpeech(byte[] wavBytes, string language)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            var recognizerInfo = SpeechRecognitionEngine.InstalledRecognizers()
                .FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName.Equals(culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
                ?? SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault();

            if (recognizerInfo is null)
                return (false, "No hay reconocedor de voz de Windows instalado para dictado local. Usa Gemini/Groq o escribe tu consulta.");

            using var engine = new SpeechRecognitionEngine(recognizerInfo);
            engine.LoadGrammar(new DictationGrammar());
            using var ms = new MemoryStream(wavBytes);
            engine.SetInputToWaveStream(ms);
            var result = engine.Recognize();
            if (result is null || string.IsNullOrWhiteSpace(result.Text))
                return (false, "No se entendió el audio. Intenta hablar más claro y cerca del micrófono.");
            return (true, result.Text.Trim());
        }
        catch (Exception ex)
        {
            return (false, $"Reconocimiento local no disponible: {ex.Message}");
        }
    }

    private string BuildSystemPrompt(AppConfig cfg, ShipState s)
    {
        string personality = cfg.AiPersonality switch
        {
            "tactical" => "Eres un oficial táctico militar conciso y directo a bordo de una nave de Elite Dangerous.",
            "scientist" => "Eres un oficial científico entusiasta especializado en astrofísica y exobiología en Elite Dangerous.",
            "sarcastic" => "Eres un copiloto veterano con humor seco y ligeramente sarcástico, pero siempre leal y útil.",
            "custom" when !string.IsNullOrWhiteSpace(cfg.AiCustomPersonality) => cfg.AiCustomPersonality.Trim(),
            _ => "Eres COVAS, el ordenador de a bordo profesional, claro y conciso de Elite Dangerous."
        };

        string verbosityRule = (cfg.AiVerbosity ?? "normal").Trim().ToLowerInvariant() switch
        {
            "brief" => "Extremadamente breve: máximo 1 o 2 frases cortas, estilo radio militar de combate.",
            "detailed" => "Detallado y explicativo, ofreciendo contexto útil de exploración o estrategia cuando aporte valor.",
            _ => "Conciso (2 a 4 frases claras, ideal para escuchar mientras se pilota)."
        };

        string langName = cfg.InterfaceLanguage switch
        {
            "en-GB" => "English",
            "fr-FR" => "Français",
            "de-DE" => "Deutsch",
            "ru-RU" => "Русский",
            "pt-BR" => "Português",
            _ => "Español"
        };

        var sb = new StringBuilder();
        sb.AppendLine($"{personality} Responde siempre en {langName}. {verbosityRule}");
        sb.AppendLine("Estilo de locución: escribe texto natural sin formato Markdown (ni asteriscos, ni almohadillas, ni emojis). Escribe las unidades completas (créditos, toneladas, años luz, segundos luz, por ciento) en lugar de abreviaturas como CR, t, AL o LS.");
        sb.AppendLine();
        sb.AppendLine("=== TELEMETRÍA EN VIVO DE LA NAVE ===");
        sb.AppendLine($"Comandante: {s.CommanderName} | Créditos: {s.Credits:N0} CR");
        sb.AppendLine($"Nave: {s.ShipType} ({s.ShipName} / {s.ShipIdent}) | Casco: {s.HullHealth:0.0}% | Escudos: {(s.ShieldsUp ? "Activos" : "Caídos")}");
        sb.AppendLine($"Combustible: {s.FuelMain:0.1}/{s.FuelMax:0.1} t | Bodega: {s.CargoUsed}/{s.CargoCapacity} t (Drones: {s.LimpetCount})");
        sb.AppendLine($"Sistema actual: {s.CurrentSystem} | Estación/Cuerpo: {(string.IsNullOrWhiteSpace(s.StationName) ? s.BodyName : s.StationName)} | Seguridad: {s.SecurityLevel} | Economía: {s.Economy}");

        if (s.NavRoute.Count > 0)
            sb.AppendLine($"Ruta activa: {s.NavRoute.Count} saltos | Próxima estrella repostable en: {(s.RouteHasNoFuelStar ? "NINGUNA" : (s.JumpsToNextFuelStar?.ToString() ?? "?"))} saltos");
        if (s.AssignedLandingPad is int padNum && padNum > 0)
        {
            var padInfo = LandingPadLocatorService.Resolve(padNum, s.AssignedLandingStationType);
            sb.AppendLine($"Plataforma de aterrizaje asignada: {padInfo.FormatGuidance(cfg.InterfaceLanguage)} en {(string.IsNullOrWhiteSpace(s.AssignedLandingStation) ? s.CurrentSystem : s.AssignedLandingStation)}");
        }
        if (s.CargoInventory.Count > 0)
        {
            var topCargo = s.CargoInventory.OrderByDescending(static c => c.Count).Take(6).Select(static c => $"{c.Name} ({c.Count} t)");
            string marketNote = "";
            if (s.Market?.Items is { Count: > 0 })
            {
                long sellTotal = 0;
                foreach (var c in s.CargoInventory)
                {
                    var m = s.Market.Items.FirstOrDefault(x =>
                        string.Equals(x.DisplayName, c.Name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase));
                    if (m is not null && m.SellPrice > 0) sellTotal += m.SellPrice * c.Count;
                }
                if (sellTotal > 0) marketNote = $" | Valor venta aquí: {sellTotal:N0} CR";
            }
            sb.AppendLine($"Carga en bodega: {string.Join(", ", topCargo)}{marketNote}");
        }
        var damagedModules = s.ShipModules.Where(static m => m.HealthPercent is > 0 and < 85).OrderBy(static m => m.HealthPercent).Take(5).ToList();
        if (damagedModules.Count > 0)
            sb.AppendLine($"Módulos dañados (<85%): {string.Join(", ", damagedModules.Select(static m => $"{m.DisplayName} ({m.HealthPercent:0}%)"))}");
        if (s.ExobiologyTracker is { } exo)
            sb.AppendLine($"Exobiología activa: {exo.Species} en {exo.BodyName} ({exo.SamplesCollected}/3 muestras, distancia {exo.CurrentDistanceMeters:0}/{exo.RequiredDistanceMeters} m)");
        var valuableBodies = s.ExplorationBodies.Where(static b => (b.EstimatedCredits ?? 0) >= 500_000 || b.BiologicalSignalCount > 0).OrderByDescending(static b => b.EstimatedCredits ?? 0).Take(5).ToList();
        if (valuableBodies.Count > 0)
            sb.AppendLine($"Cuerpos valiosos en sistema: {string.Join(", ", valuableBodies.Select(static b => $"{b.BodyName} ({b.BodyType}{(b.BiologicalSignalCount > 0 ? $", {b.BiologicalSignalCount} bio" : "")})"))}");
        var unmappedTargets = s.ExplorationBodies
            .Where(static b => b.WasMapped != true && ((b.EstimatedCredits ?? 0) >= 500_000 || b.IsVirginBioLandable))
            .OrderByDescending(static b => b.EstimatedCredits ?? 0)
            .Take(5)
            .Select(static b => $"{b.BodyName} ({b.BodyType}, {(b.DistanceFromArrivalLs.HasValue ? $"{b.DistanceFromArrivalLs.Value:0} LS" : "? LS")}{(b.BiologicalSignalCount > 0 ? $", {b.BiologicalSignalCount} bio" : "")})")
            .ToList();
        if (unmappedTargets.Count > 0)
            sb.AppendLine($"Cuerpos rentables SIN MAPEAR aún: {string.Join(" | ", unmappedTargets)}");
        if (s.Market?.Items is { Count: > 0 })
        {
            var bargains = s.Market.Items
                .Where(static m => m.Stock > 50 && m.BuyPrice > 0 && m.MeanPrice > m.BuyPrice)
                .OrderByDescending(static m => m.MeanPrice - m.BuyPrice)
                .Take(4)
                .Select(static m => $"{m.DisplayName} (compra {m.BuyPrice:N0} vs media {m.MeanPrice:N0} CR, stock {m.Stock:N0})")
                .ToList();
            if (bargains.Count > 0)
                sb.AppendLine($"Ofertas de compra en mercado local (bajo media galáctica): {string.Join(" | ", bargains)}");
        }
        if (!string.IsNullOrWhiteSpace(s.CurrentSystemNote))
            sb.AppendLine($"Nota personal en {s.CurrentSystem}: {s.CurrentSystemNote}");
        if (s.SystemFactions.Count > 0)
        {
            var topFactions = s.SystemFactions.OrderByDescending(static f => f.InfluencePercent).Take(3).Select(static f => $"{f.Name} ({f.InfluenceDisplay}, {f.FactionState})");
            sb.AppendLine($"Facciones BGS principales: {string.Join(" | ", topFactions)}");
        }
        var gSnap = _galaxyStatus.Current;
        if (!string.IsNullOrWhiteSpace(gSnap.ServerMessage) || gSnap.LastBgsTickUtc.HasValue)
        {
            string tickText = gSnap.LastBgsTickUtc.HasValue ? gSnap.LastBgsTickUtc.Value.ToString("yyyy-MM-dd HH:mm") + " UTC" : "desconocido";
            sb.AppendLine($"Servidores Elite: {(string.IsNullOrWhiteSpace(gSnap.ServerMessage) ? "?" : gSnap.ServerMessage)} | Último BGS Tick: {tickText}");
        }
        if (cfg.EngineeringWishlistBlueprints is { Count: > 0 })
        {
            var missingMats = EngineeringWishlistService.Evaluate(cfg.EngineeringWishlistBlueprints, s)
                .Where(static r => r.Missing > 0)
                .Take(6)
                .Select(static r => $"{r.MaterialName} (faltan {r.Missing})");
            string wishText = missingMats.Any() ? string.Join(", ", missingMats) : "todos los materiales listos";
            sb.AppendLine($"Wishlist ingeniería ({cfg.EngineeringWishlistBlueprints.Count} planos): {wishText}");
        }
        if (!string.IsNullOrWhiteSpace(s.PowerplaySummary))
            sb.AppendLine($"Powerplay: {s.PowerplaySummary}");
        if (s.UnsoldCartographyCredits > 0 || s.UnsoldExobiologyCredits > 0)
            sb.AppendLine($"Sin vender: Cartografía ~{s.UnsoldCartographyCredits:N0} CR | Exobiología ~{s.UnsoldExobiologyCredits:N0} CR");
        if (s.CurrentTarget is { } t && !string.IsNullOrWhiteSpace(t.Ship))
            sb.AppendLine($"Objetivo fijado: {t.Ship} ({t.PilotName}, {t.PilotRank}) | Estado: {t.LegalStatus} | Recompensa: {t.Bounty:N0} CR | Escudo: {t.ShieldHealth}% Casco: {t.HullHealth}%");
        if (s.ActiveMissions.Count > 0)
        {
            var topDest = s.ActiveMissions.Where(static m => !string.IsNullOrWhiteSpace(m.DestinationSystem))
                .GroupBy(static m => m.DestinationSystem, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(static g => g.Count())
                .Take(4)
                .Select(g => $"{g.Key} ({g.Count()})");
            sb.AppendLine($"Misiones activas: {s.ActiveMissions.Count} (Masacres apiladas: {s.MassacreStacks.Count}){(topDest.Any() ? " | Destinos: " + string.Join(", ", topDest) : "")}");
        }
        if (s.CombatSession.TotalKills > 0)
            sb.AppendLine($"Combate sesión: {s.CombatSession.TotalKills} bajas | Pendiente cobro: {s.CombatSession.PendingCredits:N0} CR");
        if (s.MiningSession.TotalRefinedTons > 0)
            sb.AppendLine($"Minería sesión: {s.MiningSession.TotalRefinedTons} t ({s.MiningSession.TonsPerHour:0.0} t/h)");
        if (s.Colonisation.Resources.Count > 0)
        {
            var pending = s.Colonisation.Resources
                .Where(static r => r.RequiredAmount > r.ProvidedAmount)
                .Take(5)
                .Select(static r => $"{r.DisplayName}: {r.RequiredAmount - r.ProvidedAmount:N0} t");
            sb.AppendLine($"Colonización pendiente: {string.Join(", ", pending)}");
        }
        sb.AppendLine($"Sesión: Balance {s.SessionStats.NetCredits:+#,0;-#,0;0} CR | Sistemas: {s.SessionStats.SystemsVisited} ({s.SessionStats.TotalDistanceLy:0.0} AL) | Escaneos: {s.SessionStats.BodiesScanned} | Exobiología: {s.SessionStats.OrganicsAnalysed}");

        sb.AppendLine();
        sb.AppendLine("=== PORTANAVES (FLEET CARRIER) ===");
        if (s.Carrier is { } fc)
        {
            string fcName = string.IsNullOrWhiteSpace(fc.Name) ? "Portanaves" : fc.Name;
            string fcCall = string.IsNullOrWhiteSpace(fc.Callsign) ? "" : $" [{fc.Callsign}]";
            sb.AppendLine($"Nombre: {fcName}{fcCall} | Ubicación: {(string.IsNullOrWhiteSpace(fc.System) ? "Desconocida" : fc.System)}{(string.IsNullOrWhiteSpace(fc.Body) ? "" : $" ({fc.Body})")}");
            if (!string.IsNullOrWhiteSpace(fc.Destination))
            {
                string countdown = fc.DepartureTime.HasValue && fc.DepartureTime.Value > DateTimeOffset.UtcNow
                    ? $" — faltan {(int)(fc.DepartureTime.Value - DateTimeOffset.UtcNow).TotalMinutes} min"
                    : "";
                sb.AppendLine($"Salto programado hacia: {fc.Destination} (Salida: {fc.DepartureTime?.ToLocalTime().ToString("dd/MM HH:mm") ?? "pendiente"}{countdown})");
            }
            if (s.CarrierCargo.Count > 0)
                sb.AppendLine($"Bodega del portanaves (cAPI): {string.Join(", ", s.CarrierCargo.OrderByDescending(static c => c.Quantity).Take(6).Select(static c => $"{c.Commodity} ({c.Quantity} t)"))}");
            sb.AppendLine($"Tritio en depósito: {(fc.Fuel.HasValue ? $"{fc.Fuel.Value} toneladas" : "desconocido")} | Salto máx.: {(fc.JumpRange.HasValue ? $"{fc.JumpRange.Value:0} AL" : "500 AL")}");
            if (fc.Capacity.HasValue || fc.FreeSpace.HasValue)
                sb.AppendLine($"Espacio: libre {fc.FreeSpace?.ToString() ?? "?"} toneladas de {fc.Capacity?.ToString() ?? "?"} toneladas (carga: {fc.Cargo?.ToString() ?? "?"} toneladas)");
            if (fc.Balance.HasValue || fc.Available.HasValue)
                sb.AppendLine($"Finanzas del portanaves: saldo total {fc.Balance?.ToString("N0") ?? "?"} CR | disponible {fc.Available?.ToString("N0") ?? "?"} CR | reserva {fc.Reserve?.ToString("N0") ?? "?"} CR");
            if (!string.IsNullOrWhiteSpace(fc.DockingAccess))
                sb.AppendLine($"Acceso de atraque: {fc.DockingAccess} | Desmantelamiento pendiente: {(fc.PendingDecommission == true ? "Sí" : "No")}");
        }
        else
        {
            sb.AppendLine("No hay datos de portanaves (Fleet Carrier) registrados todavía en el diario de esta sesión. Si el comandante pregunta por el portanaves, indica claramente que aún no se han recibido datos del portanaves y NO confundas los datos con la nave actual.");
        }

        if (s.StoredShips.Count > 0)
        {
            sb.AppendLine($"Flota almacenada (Frontier cAPI): {s.StoredShips.Count} naves → {string.Join(", ", s.StoredShips.Take(8).Select(static sh => $"{sh.ShipType} en {sh.StarSystem}"))}");
        }
        if (s.CommunityGoals.Count > 0)
        {
            sb.AppendLine($"Objetivos Comunitarios activos: {string.Join(" | ", s.CommunityGoals.Take(3).Select(static cg => $"{cg.Title} ({cg.StarSystem})"))}");
        }

        var galnet = _galnetService.Articles;
        if (galnet.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== ÚLTIMAS NOTICIAS DE GALNET (ORDENADAS DE LA MÁS RECIENTE A LA MÁS ANTIGUA) ===");
            for (int i = 0; i < Math.Min(4, galnet.Count); i++)
            {
                var a = galnet[i];
                string tag = i == 0 ? "[#1 MÁS RECIENTE]" : $"[#{i + 1}]";
                string excerpt = a.Body.Length > 280 ? a.Body[..280] + "..." : a.Body;
                sb.AppendLine($"{tag} ({a.DateDisplay}) {a.Title}: {excerpt}");
            }
            sb.AppendLine("Si el comandante pregunta por la última noticia de GalNet, responde OBLIGATORIAMENTE con la marcada como [#1 MÁS RECIENTE].");
        }

        if (cfg.AiCommanderMemory is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("=== MEMORIA PERSISTENTE DEL COMANDANTE ===");
            foreach (string mem in cfg.AiCommanderMemory.TakeLast(15))
                sb.AppendLine($"- {mem}");
        }

        if (cfg.AiAllowActions)
        {
            sb.AppendLine();
            sb.AppendLine("=== ACCIONES DISPONIBLES (OPCIONAL) ===");
            sb.AppendLine("Si el comandante pide ejecutar una acción de nave, buscar un servicio cercano o guardar una nota, incluye al final una o más etiquetas exactas:");
            sb.AppendLine("- Control/Macro: [[ACTION:BIND|<ActionId>]] (ej: Macro:CombatStance, Macro:EscapeRun, Macro:LandingPrep, Macro:SilentHeatSink, LandingGearToggle, ShipSpotLightToggle, ToggleCargoScoop, DeployHardpointToggle, NightVisionToggle, DeployHeatSink, FireChaffLauncher, UseShieldCell, Supercruise, HyperSuperCombination, GalaxyMapOpen, SystemMapOpen)");
            sb.AppendLine("- Buscar servicio en Spansh: [[ACTION:SPANSH|<material_trader|tech_broker|interstellar_factors|tritium>]]");
            sb.AppendLine("- Trazar ruta en Spansh: [[ACTION:SPANSH_ROUTE|<neutron|riches|exomastery|carrier>|<SistemaDestino>]]");
            sb.AppendLine("- Buscar nave o módulo en Spansh: [[ACTION:SPANSH_ITEM|<NombreNaveOModulo>|<true|false para descuento Li Yong-Rui>]]");
            sb.AppendLine("- Planificar ruta comercial en Spansh: [[ACTION:SPANSH_TRADE|now]]");
            sb.AppendLine("- Buscar anillos prístinos de minería en Spansh: [[ACTION:SPANSH_MINING|<Mineral ej: Platinum>]]");
            sb.AppendLine("- Buscar proveedor de colonización cercano: [[ACTION:COLONISATION_SUPPLIER|now]]");
            sb.AppendLine("- Añadir plano a la Wishlist de ingeniería: [[ACTION:WISHLIST_ADD|<fsd_range_g5|fsd_mass_manager|thrusters_dirty_g5|pp_armoured_g5|pp_overcharged_g5|pd_charge_g5|shield_thermal_g5|mc_overcharged_g5|hull_heavy_g5|odyssey_suit_g5|odyssey_weapon_g5>]]");
            sb.AppendLine("- Guardar dato en memoria persistente del comandante: [[ACTION:REMEMBER|<dato breve>]]");
            sb.AppendLine("- Olvidar dato concreto de memoria persistente: [[ACTION:FORGET|<palabra o frase a olvidar>]]");
            sb.AppendLine("- Programar temporizador/recordatorio por voz: [[ACTION:TIMER|<minutos>|<motivo>]]");
            sb.AppendLine("- Calcular tritio de portanaves para una distancia: [[ACTION:TRITIUM_CALC|<años luz numéricos>]]");
            sb.AppendLine("- Ajustar velocidad de voz (-5 a +5): [[ACTION:VOICE_RATE|<+1|-1|0>]]");
            sb.AppendLine("- Pausa breve entre acciones encadenadas: [[ACTION:DELAY|<milisegundos 100..5000>]]");
            sb.AppendLine("- Guardar nota del sistema actual: [[ACTION:NOTE|<texto de la nota>]]");
            sb.AppendLine("- Copiar nombre de sistema al portapapeles: [[ACTION:COPY|<NombreSistema>]]");
            sb.AppendLine("- Guardar marcador de superficie actual: [[ACTION:BOOKMARK|<Etiqueta>]]");
            sb.AppendLine("- Guiar brújula de superficie a marcador o última muestra exo: [[ACTION:NAV_BOOKMARK|<Etiqueta o EXO>]]");
            sb.AppendLine("- Exportar nave actual: [[ACTION:EXPORT_SHIP|<edsy|coriolis>]]");
            sb.AppendLine("- Cambiar pestaña o Mini-HUD: [[ACTION:UI|<dashboard|flight|navigation|operations|inventory|activity|controls|minihud>]]");
            sb.AppendLine("No inventes etiquetas fuera de estas.");
        }

        return sb.ToString();
    }

    private async Task<string> ProcessEmbeddedActionsAsync(AppConfig cfg, string rawReply)
    {
        if (!cfg.AiAllowActions || !rawReply.Contains("[[ACTION:", StringComparison.OrdinalIgnoreCase))
            return rawReply.Trim();

        var matches = Regex.Matches(rawReply, @"\[\[ACTION:([A-Z_]+)\|([^\]]+)\]\]", RegexOptions.IgnoreCase);
        string cleanText = Regex.Replace(rawReply, @"\[\[ACTION:[^\]]+\]\]", "", RegexOptions.IgnoreCase).Trim();
        var actionLogs = new List<string>();

        foreach (Match m in matches)
        {
            string kind = m.Groups[1].Value.Trim().ToUpperInvariant();
            string param = m.Groups[2].Value.Trim();

            switch (kind)
            {
                case "BIND":
                    var (ok, msg) = await _inputSimulator.ExecuteActionAsync(param);
                    actionLogs.Add(ok ? $"⚡ {msg}" : $"⚠ {msg}");
                    break;

                case "SPANSH":
                    SpanshServiceRequested?.Invoke(param);
                    var state = _fileWatcher.State;
                    var (sOk, results, sMsg) = await _spanshService.SearchNearestServicesAsync(param, state.CurrentSystem, state.StarPos);
                    if (sOk && results.Count > 0)
                    {
                        var top = results[0];
                        actionLogs.Add($"🛰 {top.SystemName} — {top.StationName} ({top.DistanceDisplay}, Pad {top.LandingPad})");
                    }
                    else
                    {
                        actionLogs.Add($"⚠ {sMsg}");
                    }
                    break;

                case "SPANSH_ROUTE":
                    string[] parts = param.Split('|', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        SpanshRouteRequested?.Invoke(parts[0].ToLowerInvariant(), parts[1]);
                        actionLogs.Add($"🧭 Ruta Spansh ({parts[0]}) → {parts[1]} solicitada.");
                    }
                    break;

                case "SPANSH_ITEM":
                    string[] itemParts = param.Split('|', 2, StringSplitOptions.TrimEntries);
                    if (itemParts.Length >= 1 && !string.IsNullOrWhiteSpace(itemParts[0]))
                    {
                        bool lyr = itemParts.Length == 2 && bool.TryParse(itemParts[1], out bool b) && b;
                        SpanshItemRequested?.Invoke(itemParts[0], lyr);
                        actionLogs.Add($"🛰 Buscando {itemParts[0]}{(lyr ? " (Li Yong-Rui -15%)" : "")} en Spansh...");
                    }
                    break;

                case "COPY":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        CopySystemRequested?.Invoke(param);
                        actionLogs.Add($"📋 Copiado al portapapeles: {param}");
                    }
                    break;

                case "BOOKMARK":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        SurfaceBookmarkSaveRequested?.Invoke(param);
                        actionLogs.Add($"📍 Marcador de superficie solicitado: {param}");
                    }
                    break;

                case "NAV_BOOKMARK":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        SurfaceNavigateRequested?.Invoke(param);
                        actionLogs.Add($"🧭 Brújula de superficie dirigida a: {param}");
                    }
                    break;

                case "EXPORT_SHIP":
                    if (param.Equals("edsy", StringComparison.OrdinalIgnoreCase) || param.Equals("coriolis", StringComparison.OrdinalIgnoreCase))
                    {
                        ExportShipRequested?.Invoke(param.ToLowerInvariant());
                        actionLogs.Add($"🔗 Abriendo nave en {param.ToUpperInvariant()}...");
                    }
                    break;

                case "UI":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        UiNavigationRequested?.Invoke(param.ToLowerInvariant());
                        actionLogs.Add($"🖥 Vista HUD: {param}");
                    }
                    break;

                case "SPANSH_TRADE":
                    SpanshTradeRequested?.Invoke();
                    actionLogs.Add("💰 Planificando ruta comercial en Spansh...");
                    break;

                case "SPANSH_MINING":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        SpanshMiningRequested?.Invoke(param);
                        actionLogs.Add($"⛏ Buscando anillos prístinos de {param} en Spansh...");
                    }
                    break;

                case "COLONISATION_SUPPLIER":
                    ColonisationSupplierRequested?.Invoke();
                    actionLogs.Add("🏗 Buscando proveedor cercano de colonización en Spansh...");
                    break;

                case "WISHLIST_ADD":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        WishlistAddRequested?.Invoke(param);
                        actionLogs.Add($"★ Plano añadido a Wishlist: {param}");
                    }
                    break;

                case "REMEMBER":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        cfg.AiCommanderMemory ??= new List<string>();
                        if (!cfg.AiCommanderMemory.Contains(param, StringComparer.OrdinalIgnoreCase))
                        {
                            cfg.AiCommanderMemory.Add(param);
                            if (cfg.AiCommanderMemory.Count > 20)
                                cfg.AiCommanderMemory.RemoveAt(0);
                            _configService.Save();
                            MemoryChanged?.Invoke();
                        }
                        actionLogs.Add($"🧠 Recordado: {param}");
                    }
                    break;

                case "FORGET":
                    if (!string.IsNullOrWhiteSpace(param) && cfg.AiCommanderMemory is { Count: > 0 })
                    {
                        int removed = cfg.AiCommanderMemory.RemoveAll(m => m.Contains(param, StringComparison.OrdinalIgnoreCase));
                        if (removed > 0)
                        {
                            _configService.Save();
                            MemoryChanged?.Invoke();
                            actionLogs.Add($"🧹 Olvidado ({removed}): {param}");
                        }
                    }
                    break;

                case "TIMER":
                    string[] tParts = param.Split('|', 2, StringSplitOptions.TrimEntries);
                    if (tParts.Length >= 1 && double.TryParse(tParts[0].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double mins) && mins is > 0 and <= 180)
                    {
                        string reason = tParts.Length == 2 && !string.IsNullOrWhiteSpace(tParts[1]) ? tParts[1] : "Temporizador completado";
                        ScheduleVoiceTimer(TimeSpan.FromMinutes(mins), reason, cfg.InterfaceLanguage);
                        actionLogs.Add($"⏱ Temporizador programado: {mins:0.#} min — {reason}");
                    }
                    break;

                case "TRITIUM_CALC":
                    if (double.TryParse(param.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double ly) && ly > 0)
                    {
                        TritiumCalcRequested?.Invoke(ly);
                        actionLogs.Add($"⛽ Calculadora de tritio actualizada: {ly:N0} AL");
                    }
                    break;

                case "VOICE_RATE":
                    if (int.TryParse(param, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delta))
                    {
                        VoiceRateAdjustRequested?.Invoke(delta);
                        actionLogs.Add($"🔊 Velocidad de voz ajustada ({(delta > 0 ? $"+{delta}" : delta.ToString())}).");
                    }
                    break;

                case "DELAY":
                    if (int.TryParse(param, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms))
                        await Task.Delay(Math.Clamp(ms, 100, 5000));
                    break;

                case "NOTE":
                    string sys = _fileWatcher.State.CurrentSystem;
                    if (!string.IsNullOrWhiteSpace(sys) && !string.IsNullOrWhiteSpace(param))
                    {
                        _configService.Current.SystemNotes[sys] = param;
                        _configService.Save();
                        _fileWatcher.SyncCurrentSystemNote();
                        if (_configService.Current.EnableEdsmSync)
                            _ = _communitySync.SetEdsmSystemCommentAsync(sys, param);
                        actionLogs.Add($"📝 Nota guardada en {sys}.");
                    }
                    break;
            }
        }

        if (actionLogs.Count == 0)
            return cleanText;

        return string.IsNullOrWhiteSpace(cleanText)
            ? string.Join("\n", actionLogs)
            : $"{cleanText}\n\n{string.Join("\n", actionLogs)}";
    }

    private async Task<(bool Success, string Reply)> SendProviderRequestAsync(
        AppConfig cfg,
        string systemPrompt,
        List<(string Role, string Content)> messages)
    {
        return cfg.AiActiveProvider switch
        {
            "gemini" => await SendGeminiAsync(cfg, systemPrompt, messages),
            "openai" => await SendOpenAiCompatibleAsync(cfg.AiOpenAiEndpoint, cfg.AiOpenAiApiKey, cfg.AiOpenAiModel, systemPrompt, messages),
            _ => await SendOpenAiCompatibleAsync(cfg.AiOllamaEndpoint, "", cfg.AiOllamaModel, systemPrompt, messages)
        };
    }

    private async Task<(bool Success, string Reply)> SendOpenAiCompatibleAsync(
        string baseUrl,
        string apiKey,
        string model,
        string systemPrompt,
        List<(string Role, string Content)> messages)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
            return (false, "Configura el endpoint y el modelo del proveedor IA en AJUSTES.");

        string url = baseUrl.TrimEnd('/') + "/chat/completions";
        var msgList = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var (role, content) in messages)
            msgList.Add(new { role, content });

        var payload = new
        {
            model = model.Trim(),
            messages = msgList,
            temperature = 0.6,
            max_tokens = 450
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            using var resp = await _httpClient.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return (false, $"Error HTTP {(int)resp.StatusCode}: {Truncate(body, 180)}");

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var msgObj) &&
                msgObj.TryGetProperty("content", out var contentProp))
            {
                return (true, contentProp.GetString()?.Trim() ?? "");
            }

            return (false, "Respuesta vacía del proveedor IA.");
        }
        catch (Exception ex)
        {
            return (false, $"Error de conexión IA: {ex.Message}");
        }
    }

    private async Task<(bool Success, string Reply)> SendGeminiAsync(
        AppConfig cfg,
        string systemPrompt,
        List<(string Role, string Content)> messages)
    {
        if (string.IsNullOrWhiteSpace(cfg.AiGeminiApiKey) || string.IsNullOrWhiteSpace(cfg.AiGeminiModel))
            return (false, "Configura tu Gemini API Key y modelo en AJUSTES.");

        string model = cfg.AiGeminiModel.Trim();
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(cfg.AiGeminiApiKey.Trim())}";

        var contents = messages.Select(m => new
        {
            role = m.Role == "assistant" ? "model" : "user",
            parts = new[] { new { text = m.Content } }
        }).ToArray();

        var payload = new
        {
            system_instruction = new { parts = new[] { new { text = systemPrompt } } },
            contents,
            generationConfig = new { temperature = 0.6, maxOutputTokens = 500 }
        };

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _httpClient.PostAsync(url, content);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return (false, $"Gemini HTTP {(int)resp.StatusCode}: {Truncate(body, 180)}");

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("candidates", out var cands) &&
                cands.ValueKind == JsonValueKind.Array &&
                cands.GetArrayLength() > 0 &&
                cands[0].TryGetProperty("content", out var cObj) &&
                cObj.TryGetProperty("parts", out var parts) &&
                parts.ValueKind == JsonValueKind.Array &&
                parts.GetArrayLength() > 0 &&
                parts[0].TryGetProperty("text", out var txtProp))
            {
                return (true, txtProp.GetString()?.Trim() ?? "");
            }

            return (false, "Respuesta vacía de Gemini.");
        }
        catch (Exception ex)
        {
            return (false, $"Error de conexión Gemini: {ex.Message}");
        }
    }

    private static string NormalizeTextForSpeech(string text, string language)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        bool isEs = language.StartsWith("es", StringComparison.OrdinalIgnoreCase);
        bool isFr = language.StartsWith("fr", StringComparison.OrdinalIgnoreCase);
        bool isDe = language.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        bool isRu = language.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
        bool isPt = language.StartsWith("pt", StringComparison.OrdinalIgnoreCase);

        string creditsWord = isEs || isPt ? "créditos" : isFr ? "crédits" : isDe ? "Credits" : isRu ? "кредитов" : "credits";
        string tonsWord = isEs || isPt ? "toneladas" : isFr ? "tonnes" : isDe ? "Tonnen" : isRu ? "тонн" : "tons";
        string lyWord = isEs ? "años luz" : isPt ? "anos-luz" : isFr ? "années-lumière" : isDe ? "Lichtjahre" : isRu ? "световых лет" : "light years";
        string lsWord = isEs ? "segundos luz" : isPt ? "segundos-luz" : isFr ? "secondes-lumière" : isDe ? "Lichtsekunden" : isRu ? "световых секунд" : "light seconds";
        string ofWord = isEs || isPt || isFr ? "de" : isDe ? "von" : isRu ? "из" : "of";
        string cmdrWord = isEs || isPt ? "Comandante" : isFr ? "Commandant" : isDe ? "Kommandant" : isRu ? "Командир" : "Commander";

        string s = text;
        // Quitar Markdown y símbolos decorativos
        s = Regex.Replace(s, @"[*_`#]+", "");
        s = Regex.Replace(s, @"[⚡🛰📝⚠🔍✨📖🧹⏹➤•·—|]", " ");
        // Unir separadores de miles (ej. 4.000 o 1,250,000 -> 4000 / 1250000) para que SAPI lea el número completo
        s = Regex.Replace(s, @"(?<=\d)[.,](?=\d{3}(?:\b|[^\d]))", "");
        // Fracciones tipo 12/64 t -> 12 de 64 toneladas
        s = Regex.Replace(s, @"(\d+(?:[.,]\d+)?)\s*/\s*(\d+(?:[.,]\d+)?)\s*t\b", $"$1 {ofWord} $2 {tonsWord}", RegexOptions.IgnoreCase);
        // Unidades abreviadas
        s = Regex.Replace(s, @"(\d+(?:[.,]\d+)?)\s*CR\b", $"$1 {creditsWord}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(\d+(?:[.,]\d+)?)\s*CR/h\b", $"$1 {creditsWord} / h", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(\d+(?:[.,]\d+)?)\s*t\b", $"$1 {tonsWord}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(\d+(?:[.,]\d+)?)\s*(?:AL|LY)\b", $"$1 {lyWord}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(\d+(?:[.,]\d+)?)\s*LS\b", $"$1 {lsWord}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bCMDR\b", cmdrWord, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return s;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrWhiteSpace(s) ? "" : (s.Length <= max ? s : s[..max] + "...");

    private void ScheduleVoiceTimer(TimeSpan delay, string reason, string language)
    {
        var token = _timerCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token);
                string alert = $"Temporizador completado: {reason}.";
                CopilotMessageAdded?.Invoke("COVAS ⏱", alert);
                if (_configService.Current.AiSpeakReplies)
                    _speechService.SpeakCustomText(NormalizeTextForSpeech(alert, language));
            }
            catch (OperationCanceledException)
            {
                // Application shutting down.
            }
        }, token);
    }

    public void Dispose()
    {
        try { _timerCts.Cancel(); } catch { }
        _timerCts.Dispose();
        _httpClient.Dispose();
    }
}
