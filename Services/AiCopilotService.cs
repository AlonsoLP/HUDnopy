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
    private readonly AlertSpeechService _speechService;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly object _historyLock = new();
    private readonly List<(string Role, string Content)> _history = new();
    private DateTimeOffset _lastDynamicEventUtc = DateTimeOffset.MinValue;
    private int _dynamicEventBusy;

    public event Action<string, string>? CopilotMessageAdded;

    public AiCopilotService(
        ConfigService configService,
        EliteFileWatcherService fileWatcher,
        InputSimulatorService inputSimulator,
        SpanshSystemDataService spanshService,
        CommunitySyncService communitySync,
        GalnetService galnetService,
        AlertSpeechService speechService,
        GameEventHub gameEvents)
    {
        _configService = configService;
        _fileWatcher = fileWatcher;
        _inputSimulator = inputSimulator;
        _spanshService = spanshService;
        _communitySync = communitySync;
        _galnetService = galnetService;
        _speechService = speechService;
        gameEvents.JournalEventReceived += OnJournalEventReceived;
    }

    private void OnJournalEventReceived(GameJournalEvent gameEvent)
    {
        if (gameEvent.IsHistorical)
            return;

        var cfg = _configService.Current;
        if (!cfg.EnableAiCopilot || !cfg.AiDynamicEvents)
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

    public async Task<(bool Success, string Transcript, string Reply)> AskWithAudioWavAsync(byte[] wavBytes)
    {
        var cfg = _configService.Current;
        if (!cfg.EnableAiCopilot)
            return (false, "", "Activa el Copiloto IA en AJUSTES.");
        if (wavBytes is null || wavBytes.Length <= 44)
            return (false, "", "No se detectó audio del micrófono.");

        // 1. Gemini: envío multimodal directo del WAV sin necesidad de STT separado
        if (cfg.AiActiveProvider == "gemini")
        {
            var (ok, reply) = await SendGeminiAudioAsync(cfg, wavBytes);
            if (!ok)
                return (false, "🎤 [Voz]", reply);

            string finalReply = await ProcessEmbeddedActionsAsync(cfg, reply);
            if (cfg.AiSpeakReplies && !string.IsNullOrWhiteSpace(finalReply))
                _speechService.SpeakCustomText(NormalizeTextForSpeech(finalReply, cfg.InterfaceLanguage));
            return (true, "🎤 [Comando de voz]", finalReply);
        }

        // 2. OpenAI / Groq: transcripción Whisper + AskAsync
        if (cfg.AiActiveProvider == "openai")
        {
            var (tOk, transcript) = await TranscribeWithWhisperAsync(cfg, wavBytes);
            if (!tOk || string.IsNullOrWhiteSpace(transcript))
                return (false, "", string.IsNullOrWhiteSpace(transcript) ? "No se pudo transcribir el audio." : transcript);

            var (ok, reply) = await AskAsync(transcript);
            return (ok, transcript, reply);
        }

        // 3. Local (Ollama): reconocimiento nativo de Windows (System.Speech.Recognition)
        var (locOk, localText) = TranscribeWithWindowsSpeech(wavBytes, cfg.InterfaceLanguage);
        if (!locOk || string.IsNullOrWhiteSpace(localText))
            return (false, "", localText);

        var (aOk, aReply) = await AskAsync(localText);
        return (aOk, localText, aReply);
    }

    private async Task<(bool Success, string Reply)> SendGeminiAudioAsync(AppConfig cfg, byte[] wavBytes)
    {
        if (string.IsNullOrWhiteSpace(cfg.AiGeminiApiKey) || string.IsNullOrWhiteSpace(cfg.AiGeminiModel))
            return (false, "Configura tu Gemini API Key y modelo en AJUSTES.");

        string model = cfg.AiGeminiModel.Trim();
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(cfg.AiGeminiApiKey.Trim())}";
        string systemPrompt = BuildSystemPrompt(cfg, _fileWatcher.State);

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
                        new { text = "Escucha el mensaje de voz del comandante y responde directamente." },
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
            _ => "Eres COVAS, el ordenador de a bordo profesional, claro y conciso de Elite Dangerous."
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
        sb.AppendLine($"{personality} Responde siempre en {langName}. Sé conciso (ideal para escuchar mientras se pilota).");
        sb.AppendLine("Estilo de locución: escribe texto natural sin formato Markdown (ni asteriscos, ni almohadillas, ni emojis). Escribe las unidades completas (créditos, toneladas, años luz, segundos luz, por ciento) en lugar de abreviaturas como CR, t, AL o LS.");
        sb.AppendLine();
        sb.AppendLine("=== TELEMETRÍA EN VIVO DE LA NAVE ===");
        sb.AppendLine($"Comandante: {s.CommanderName} | Créditos: {s.Credits:N0} CR");
        sb.AppendLine($"Nave: {s.ShipType} ({s.ShipName} / {s.ShipIdent}) | Casco: {s.HullHealth:0.0}% | Escudos: {(s.ShieldsUp ? "Activos" : "Caídos")}");
        sb.AppendLine($"Combustible: {s.FuelMain:0.1}/{s.FuelMax:0.1} t | Bodega: {s.CargoUsed}/{s.CargoCapacity} t (Drones: {s.LimpetCount})");
        sb.AppendLine($"Sistema actual: {s.CurrentSystem} | Estación/Cuerpo: {(string.IsNullOrWhiteSpace(s.StationName) ? s.BodyName : s.StationName)} | Seguridad: {s.SecurityLevel} | Economía: {s.Economy}");

        if (s.NavRoute.Count > 0)
            sb.AppendLine($"Ruta activa: {s.NavRoute.Count} saltos | Próxima estrella repostable en: {(s.RouteHasNoFuelStar ? "NINGUNA" : (s.JumpsToNextFuelStar?.ToString() ?? "?"))} saltos");
        if (!string.IsNullOrWhiteSpace(s.PowerplaySummary))
            sb.AppendLine($"Powerplay: {s.PowerplaySummary}");
        if (s.UnsoldCartographyCredits > 0 || s.UnsoldExobiologyCredits > 0)
            sb.AppendLine($"Sin vender: Cartografía ~{s.UnsoldCartographyCredits:N0} CR | Exobiología ~{s.UnsoldExobiologyCredits:N0} CR");
        if (s.CurrentTarget is { } t && !string.IsNullOrWhiteSpace(t.Ship))
            sb.AppendLine($"Objetivo fijado: {t.Ship} ({t.PilotName}, {t.PilotRank}) | Estado: {t.LegalStatus} | Recompensa: {t.Bounty:N0} CR | Escudo: {t.ShieldHealth}% Casco: {t.HullHealth}%");
        if (s.ActiveMissions.Count > 0)
            sb.AppendLine($"Misiones activas: {s.ActiveMissions.Count} (Masacres apiladas: {s.MassacreStacks.Count})");
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
                sb.AppendLine($"Salto programado hacia: {fc.Destination} (Salida: {fc.DepartureTime?.ToLocalTime().ToString("dd/MM HH:mm") ?? "pendiente"})");
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

        if (cfg.AiAllowActions)
        {
            sb.AppendLine();
            sb.AppendLine("=== ACCIONES DISPONIBLES (OPCIONAL) ===");
            sb.AppendLine("Si el comandante pide ejecutar una acción de nave, buscar un servicio cercano o guardar una nota, incluye al final una o más etiquetas exactas:");
            sb.AppendLine("- Control/Macro: [[ACTION:BIND|<ActionId>]] (ej: Macro:CombatStance, Macro:EscapeRun, Macro:LandingPrep, Macro:SilentHeatSink, LandingGearToggle, ShipSpotLightToggle, ToggleCargoScoop, DeployHardpointToggle, NightVisionToggle, DeployHeatSink, FireChaffLauncher, UseShieldCell, Supercruise, HyperSuperCombination, GalaxyMapOpen, SystemMapOpen)");
            sb.AppendLine("- Buscar servicio en Spansh: [[ACTION:SPANSH|<material_trader|tech_broker|interstellar_factors|tritium>]]");
            sb.AppendLine("- Guardar nota del sistema actual: [[ACTION:NOTE|<texto de la nota>]]");
            sb.AppendLine("No inventes etiquetas fuera de estas tres.");
        }

        return sb.ToString();
    }

    private async Task<string> ProcessEmbeddedActionsAsync(AppConfig cfg, string rawReply)
    {
        if (!cfg.AiAllowActions || !rawReply.Contains("[[ACTION:", StringComparison.OrdinalIgnoreCase))
            return rawReply.Trim();

        var matches = Regex.Matches(rawReply, @"\[\[ACTION:([A-Z]+)\|([^\]]+)\]\]", RegexOptions.IgnoreCase);
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

    public void Dispose() => _httpClient.Dispose();
}
