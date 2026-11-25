// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/AiCopilot/AiCopilotService.Providers.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Gemini/OpenAI/Ollama SSE streaming, audio transcription, sentence TTS speaker, and speech normalization.
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

public sealed partial class AiCopilotService
{
    private async Task<(bool Success, string Reply)> SendGeminiAudioAsync(
        AppConfig cfg,
        byte[] wavBytes,
        string? requiredWakeWord = null,
        SentenceStreamSpeaker? streamSpeaker = null,
        List<(string Role, string Content)>? history = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.AiGeminiApiKey) || string.IsNullOrWhiteSpace(cfg.AiGeminiModel))
            return (false, "Configura tu Gemini API Key y modelo en AJUSTES.");

        string model = cfg.AiGeminiModel.Trim();
        string systemPrompt = BuildSystemPrompt(cfg, _fileWatcher.State, userPrompt: null);
        // Voice audio path defaults to fast thinking budget unless user explicitly forced "yes"
        int thinkingBudget = string.Equals(cfg.AiThinkingMode, "yes", StringComparison.OrdinalIgnoreCase)
            ? (cfg.AiVerbosity == "detailed" ? 1024 : 512)
            : 0;

        string audioInstruction = string.IsNullOrWhiteSpace(requiredWakeWord)
            ? "Escucha el mensaje de voz del comandante y responde directamente. Si solo pide silencio o parar de hablar, responde únicamente [[SILENCE]]."
            : $"Escucha el audio adjunto. Si el hablante NO pronuncia la palabra de activación '{requiredWakeWord}', responde ÚNICAMENTE [[IGNORE]]. Si pide silencio o parar de hablar, responde ÚNICAMENTE [[SILENCE]]. En caso contrario, responde directamente.";

        var contentsList = new List<object>();
        if (history is { Count: > 0 })
        {
            foreach (var (role, content) in history)
            {
                contentsList.Add(new
                {
                    role = role == "assistant" ? "model" : "user",
                    parts = new object[] { new { text = content } }
                });
            }
        }
        contentsList.Add(new
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
        });

        return await StreamGeminiInternalAsync(cfg, model, systemPrompt, contentsList.ToArray(), thinkingBudget, streamSpeaker, isAudio: true, ct: ct);
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
            string isoLang = (cfg.InterfaceLanguage ?? "en-GB").Split('-')[0].Trim().ToLowerInvariant();
            string wake = string.IsNullOrWhiteSpace(cfg.AiWakeWord) ? "COVAS" : cfg.AiWakeWord.Trim();
            var vocabTerms = new List<string>
            {
                wake, "Elite Dangerous", "FSD", "Supercruise", "Hyperspace", "Spansh", "GalNet",
                "EDSM", "Inara", "Tritium", "Limpets", "SRV", "AFMU", "Thargoid", "Guardian", "DSSA",
                "Platinum", "Painite", "Monazite", "Sol", "Colonia", "Sagittarius A*", "Shinrarta Dezhra", "Jameson Memorial"
            };
            if (cfg.AiVoiceMacros is { Count: > 0 })
            {
                foreach (var macro in cfg.AiVoiceMacros.Take(8))
                {
                    string firstAlias = (macro.TriggerPhrase ?? "").Split(',')[0].Trim();
                    if (!string.IsNullOrWhiteSpace(firstAlias))
                        vocabTerms.Add(firstAlias);
                }
            }

            using var form = new MultipartFormDataContent();
            var audioContent = new ByteArrayContent(wavBytes);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audioContent, "file", "voice.wav");
            form.Add(new StringContent(whisperModel), "model");
            if (isoLang.Length == 2)
                form.Add(new StringContent(isoLang), "language");
            form.Add(new StringContent(string.Join(", ", vocabTerms.Distinct(StringComparer.OrdinalIgnoreCase))), "prompt");

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
            AppDiagnosticsLogger.Warn("AI.Whisper", $"Cloud Whisper transcription failed: {ex.Message}");
            return (false, $"Error Whisper: {ex.Message}");
        }
    }

    private static (bool Success, string Transcript) TranscribeWithWindowsSpeech(byte[] wavBytes, string language, AppConfig? cfg = null)
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

            // Load a high-priority phrase grammar with wake word, silence/repeat commands, and user voice macros
            var phrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "silencio", "stop", "silence", "repite", "repeat"
            };
            string wake = (cfg?.AiWakeWord ?? "COVAS").Trim();
            if (!string.IsNullOrWhiteSpace(wake))
                phrases.Add(wake);
            if (cfg?.AiVoiceMacros is { Count: > 0 })
            {
                foreach (var macro in cfg.AiVoiceMacros)
                {
                    foreach (string alias in (macro.TriggerPhrase ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!string.IsNullOrWhiteSpace(alias))
                        {
                            phrases.Add(alias);
                            if (!string.IsNullOrWhiteSpace(wake))
                                phrases.Add($"{wake} {alias}");
                        }
                    }
                }
            }
            if (phrases.Count > 0)
            {
                var choices = new Choices(phrases.ToArray());
                var macroGrammar = new Grammar(new GrammarBuilder(choices) { Culture = recognizerInfo.Culture })
                {
                    Weight = 1.0f
                };
                engine.LoadGrammar(macroGrammar);
            }

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
            AppDiagnosticsLogger.Warn("AI.WindowsSpeech", $"Windows Speech recognition failed: {ex.Message}");
            return (false, $"Reconocimiento local no disponible: {ex.Message}");
        }
    }

    private async Task<(bool Success, string Reply)> SendProviderRequestAsync(
        AppConfig cfg,
        string systemPrompt,
        List<(string Role, string Content)> messages,
        int thinkingBudget = 0,
        SentenceStreamSpeaker? streamSpeaker = null,
        CancellationToken ct = default)
    {
        return cfg.AiActiveProvider switch
        {
            "gemini" => await SendGeminiAsync(cfg, systemPrompt, messages, thinkingBudget, streamSpeaker, ct),
            "openai" => await SendOpenAiCompatibleAsync(cfg, cfg.AiOpenAiEndpoint, cfg.AiOpenAiApiKey, cfg.AiOpenAiModel, systemPrompt, messages, isOllama: false, streamSpeaker: streamSpeaker, ct: ct),
            _ => await SendOpenAiCompatibleAsync(cfg, cfg.AiOllamaEndpoint, "", cfg.AiOllamaModel, systemPrompt, messages, isOllama: true, streamSpeaker: streamSpeaker, ct: ct)
        };
    }

    private async Task<(bool Success, string Reply)> SendOpenAiCompatibleAsync(
        AppConfig cfg,
        string baseUrl,
        string apiKey,
        string model,
        string systemPrompt,
        List<(string Role, string Content)> messages,
        bool isOllama,
        SentenceStreamSpeaker? streamSpeaker = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
            return (false, "Configura el endpoint y el modelo del proveedor IA en AJUSTES.");

        string url = baseUrl.TrimEnd('/') + "/chat/completions";
        var msgList = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var (role, content) in messages)
            msgList.Add(new { role, content });

        bool useStream = streamSpeaker is not null;
        var payload = new Dictionary<string, object>
        {
            ["model"] = model.Trim(),
            ["messages"] = msgList,
            ["temperature"] = 0.6,
            ["max_tokens"] = GetVerbosityMaxTokens(cfg),
            ["stream"] = useStream
        };
        if (isOllama)
            payload["keep_alive"] = "15m";

        try
        {
            string payloadJson = JsonSerializer.Serialize(payload);
            HttpResponseMessage? resp = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(apiKey))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

                resp = await _httpClient.SendAsync(req, useStream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);
                if (resp.IsSuccessStatusCode || (attempt == 0 && (int)resp.StatusCode is not (429 or 503)))
                    break;
                if (attempt == 0 && (int)resp.StatusCode is 429 or 503)
                {
                    resp.Dispose();
                    resp = null;
                    await Task.Delay(600, ct);
                }
            }
            using var finalResp = resp!;
            if (!finalResp.IsSuccessStatusCode)
            {
                string errBody = await finalResp.Content.ReadAsStringAsync(ct);
                return (false, $"Error HTTP {(int)finalResp.StatusCode}: {Truncate(errBody, 180)}");
            }

            if (useStream)
            {
                var full = new StringBuilder();
                using var stream = await finalResp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string json = line["data:".Length..].Trim();
                    if (string.IsNullOrEmpty(json) || json == "[DONE]")
                        continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("choices", out var ch) &&
                            ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0 &&
                            ch[0].TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var cp) &&
                            cp.ValueKind == JsonValueKind.String)
                        {
                            string chunk = cp.GetString() ?? "";
                            if (!string.IsNullOrEmpty(chunk))
                            {
                                full.Append(chunk);
                                streamSpeaker?.Append(chunk);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore malformed SSE chunk
                    }
                }
                string streamed = full.ToString().Trim();
                return !string.IsNullOrEmpty(streamed) ? (true, streamed) : (false, "Respuesta vacía del proveedor IA.");
            }

            string body = await finalResp.Content.ReadAsStringAsync();
            using var nonStreamDoc = JsonDocument.Parse(body);
            if (nonStreamDoc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var msgObj) &&
                msgObj.TryGetProperty("content", out var contentProp))
            {
                return (true, contentProp.GetString()?.Trim() ?? "");
            }

            return (false, "Respuesta vacía del proveedor IA.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("AI.OpenAI", $"Provider request failed ({baseUrl}): {ex.Message}");
            return (false, $"Error de conexión IA: {ex.Message}");
        }
    }

    private async Task<(bool Success, string Reply)> SendGeminiAsync(
        AppConfig cfg,
        string systemPrompt,
        List<(string Role, string Content)> messages,
        int thinkingBudget = 0,
        SentenceStreamSpeaker? streamSpeaker = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.AiGeminiApiKey) || string.IsNullOrWhiteSpace(cfg.AiGeminiModel))
            return (false, "Configura tu Gemini API Key y modelo en AJUSTES.");

        string model = cfg.AiGeminiModel.Trim();
        var contents = messages.Select(m => new
        {
            role = m.Role == "assistant" ? "model" : "user",
            parts = new[] { new { text = m.Content } }
        }).ToArray();

        return await StreamGeminiInternalAsync(cfg, model, systemPrompt, contents, thinkingBudget, streamSpeaker, isAudio: false, ct: ct);
    }

    /// <summary>
    /// Streams a Gemini text or audio request via SSE (:streamGenerateContent?alt=sse) so TTS can begin
    /// speaking Sentence 1 immediately while the remaining sentences are still being generated.
    /// Automatically retries without thinkingConfig if HTTP 400 INVALID_ARGUMENT or a thinking stall occurs.
    /// </summary>
    private async Task<(bool Success, string Reply)> StreamGeminiInternalAsync(
        AppConfig cfg,
        string model,
        string systemPrompt,
        object contents,
        int thinkingBudget,
        SentenceStreamSpeaker? streamSpeaker,
        bool isAudio,
        CancellationToken ct = default)
    {
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse&key={Uri.EscapeDataString(cfg.AiGeminiApiKey.Trim())}";
        string prefix = isAudio ? "Gemini Audio" : "Gemini";

        async Task<(bool Ok, int StatusCode, string TextOrErr)> ExecuteOnceAsync(bool includeThinking, int currentBudget, TimeSpan attemptTimeout)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(attemptTimeout);
            var token = attemptCts.Token;

            var payload = new
            {
                system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                contents,
                generationConfig = BuildGeminiGenerationConfig(cfg, model, includeThinking ? currentBudget : 0, includeThinking)
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
            if (!resp.IsSuccessStatusCode)
            {
                string err = await resp.Content.ReadAsStringAsync(token);
                return (false, (int)resp.StatusCode, err);
            }

            var full = new StringBuilder();
            using var stream = await resp.Content.ReadAsStreamAsync(token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(token)) is not null)
            {
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;
                string json = line["data:".Length..].Trim();
                if (string.IsNullOrEmpty(json))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    string? delta = ExtractGeminiReplyText(doc.RootElement, preserveWhitespace: true);
                    if (!string.IsNullOrEmpty(delta))
                    {
                        full.Append(delta);
                        streamSpeaker?.Append(delta);
                    }
                }
                catch
                {
                    // Skip malformed SSE event
                }
            }

            return (true, 200, full.ToString().Trim());
        }

        try
        {
            (bool ok, int statusCode, string textOrErr) result;
            try
            {
                result = await ExecuteOnceAsync(includeThinking: true, currentBudget: thinkingBudget, attemptTimeout: TimeSpan.FromSeconds(24));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && (streamSpeaker is null || !streamSpeaker.SpokeAnySentence))
            {
                // First attempt timed out before speaking: retry once in fast zero-thinking mode
                AppDiagnosticsLogger.Warn("AI.Gemini", $"{prefix} timed out on first attempt ({model}); retrying with zero thinking budget.");
                result = await ExecuteOnceAsync(includeThinking: true, currentBudget: 0, attemptTimeout: TimeSpan.FromSeconds(24));
            }

            if (!result.ok && result.statusCode is 429 or 503)
            {
                await Task.Delay(600, ct);
                result = await ExecuteOnceAsync(includeThinking: true, currentBudget: 0, attemptTimeout: TimeSpan.FromSeconds(24));
            }
            if (!result.ok && result.statusCode == 400 &&
                result.textOrErr.Contains("INVALID_ARGUMENT", StringComparison.OrdinalIgnoreCase) &&
                SupportsGeminiThinking(model))
            {
                result = await ExecuteOnceAsync(includeThinking: false, currentBudget: 0, attemptTimeout: TimeSpan.FromSeconds(24));
            }

            if (!result.ok)
                return (false, $"{prefix} HTTP {result.statusCode}: {Truncate(result.textOrErr, 180)}");

            return !string.IsNullOrWhiteSpace(result.textOrErr)
                ? (true, result.textOrErr)
                : (false, $"Respuesta vacía de {prefix}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("AI.Gemini", $"{prefix} stream failed: {ex.Message}");
            return (false, $"Error de conexión {prefix}: {ex.Message}");
        }
    }

    /// <summary>
    /// Captures the primary display as a compact JPEG (scaled to at most <paramref name="maxWidth"/> px)
    /// for multimodal cockpit screen vision analysis.
    /// </summary>
    public static byte[]? CapturePrimaryScreenJpeg(int maxWidth = 1280, long jpegQuality = 78)
    {
        try
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return null;

            using var fullBmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(fullBmp))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, System.Drawing.CopyPixelOperation.SourceCopy);
            }

            System.Drawing.Bitmap targetBmp = fullBmp;
            System.Drawing.Bitmap? resized = null;
            if (bounds.Width > maxWidth)
            {
                int newW = maxWidth;
                int newH = Math.Max(1, (int)Math.Round(bounds.Height * ((double)maxWidth / bounds.Width)));
                resized = new System.Drawing.Bitmap(newW, newH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g2 = System.Drawing.Graphics.FromImage(resized))
                {
                    g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g2.DrawImage(fullBmp, 0, 0, newW, newH);
                }
                targetBmp = resized;
            }

            try
            {
                using var ms = new MemoryStream();
                var jpegCodec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(static c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                if (jpegCodec is not null)
                {
                    using var ep = new System.Drawing.Imaging.EncoderParameters(1);
                    ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, jpegQuality);
                    targetBmp.Save(ms, jpegCodec, ep);
                }
                else
                {
                    targetBmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                }
                return ms.ToArray();
            }
            finally
            {
                resized?.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("AI.Vision", $"Primary screen capture failed: {ex.Message}");
            return null;
        }
    }

    private async Task<(bool Success, string Reply)> SendVisionProviderRequestAsync(
        AppConfig cfg,
        string systemPrompt,
        string userPrompt,
        byte[] jpegBytes,
        SentenceStreamSpeaker? streamSpeaker)
    {
        string base64 = Convert.ToBase64String(jpegBytes);

        if (string.Equals(cfg.AiActiveProvider, "gemini", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(cfg.AiGeminiApiKey) || string.IsNullOrWhiteSpace(cfg.AiGeminiModel))
                return (false, "Configura tu Gemini API Key y modelo en AJUSTES.");

            var contents = new object[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = userPrompt },
                        new { inline_data = new { mime_type = "image/jpeg", data = base64 } }
                    }
                }
            };
            return await StreamGeminiInternalAsync(cfg, cfg.AiGeminiModel.Trim(), systemPrompt, contents, thinkingBudget: 0, streamSpeaker, isAudio: false);
        }

        // OpenAI / OpenRouter / Ollama vision-capable models (OpenAI multimodal schema)
        bool isOllama = !string.Equals(cfg.AiActiveProvider, "openai", StringComparison.OrdinalIgnoreCase);
        string baseUrl = isOllama ? cfg.AiOllamaEndpoint : cfg.AiOpenAiEndpoint;
        string apiKey = isOllama ? "" : cfg.AiOpenAiApiKey;
        string model = isOllama ? cfg.AiOllamaModel : cfg.AiOpenAiModel;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
            return (false, "Configura el endpoint y el modelo del proveedor IA en AJUSTES.");

        string url = baseUrl.TrimEnd('/') + "/chat/completions";
        var payload = new Dictionary<string, object>
        {
            ["model"] = model.Trim(),
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = userPrompt },
                        new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{base64}" } }
                    }
                }
            },
            ["temperature"] = 0.5,
            ["max_tokens"] = GetVerbosityMaxTokens(cfg),
            ["stream"] = false
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
                return (false, $"Vision HTTP {(int)resp.StatusCode}: {Truncate(body, 180)}");

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var msgObj) &&
                msgObj.TryGetProperty("content", out var cp))
            {
                string reply = cp.GetString()?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(reply))
                {
                    streamSpeaker?.Append(reply);
                    return (true, reply);
                }
            }
            return (false, "Respuesta vacía del proveedor de visión.");
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("AI.Vision", $"Vision provider request failed: {ex.Message}");
            return (false, $"Error de visión IA: {ex.Message}");
        }
    }

    /// <summary>
    /// Buffers streamed LLM tokens and dispatches each completed sentence immediately to TTS
    /// while skipping control/query/action tags ([[QUERY]], [[ACTION]], [[IGNORE]], [[SILENCE]]).
    /// </summary>
    private sealed class SentenceStreamSpeaker(
        AlertSpeechService speechService,
        string language,
        bool speakEnabled,
        Action<string>? onTextChunk = null,
        Action? onFirstToken = null)
    {
        private readonly StringBuilder _buffer = new();
        private bool _suppressed;
        private bool _sawFirstToken;
        private int _sentenceCount;

        public bool SpokeAnySentence => _sentenceCount > 0;

        public void Append(string chunk)
        {
            if (!_sawFirstToken && !string.IsNullOrEmpty(chunk))
            {
                _sawFirstToken = true;
                onFirstToken?.Invoke();
            }
            if (_suppressed || string.IsNullOrEmpty(chunk))
                return;

            _buffer.Append(chunk);
            string current = _buffer.ToString();

            // Strip any completed [[ACTION:...]] tags in-flight so prose after an action tag still streams and speaks
            if (current.Contains("ACTION", StringComparison.OrdinalIgnoreCase))
            {
                string cleaned = ActionTagRegex().Replace(current, "");
                if (!string.Equals(cleaned, current, StringComparison.Ordinal))
                {
                    _buffer.Clear();
                    _buffer.Append(cleaned);
                    current = cleaned;
                }
            }

            string trimmedStart = current.TrimStart();

            // Wait until we have enough characters to be sure it isn't a leading control tag
            if (trimmedStart.Length < 10 && trimmedStart.StartsWith("[", StringComparison.Ordinal))
                return;

            if (trimmedStart.StartsWith("[[QUERY", StringComparison.OrdinalIgnoreCase) ||
                trimmedStart.StartsWith("[[IGNORE", StringComparison.OrdinalIgnoreCase) ||
                trimmedStart.StartsWith("[[SILENCE", StringComparison.OrdinalIgnoreCase))
            {
                _suppressed = true;
                _buffer.Clear();
                return;
            }

            int queryIdx = current.IndexOf("[[QUERY", StringComparison.OrdinalIgnoreCase);
            if (queryIdx >= 0)
            {
                string beforeQuery = StripInternalTags(current[..queryIdx]);
                if (!string.IsNullOrWhiteSpace(beforeQuery))
                    EmitSentence(beforeQuery);
                _buffer.Clear();
                _suppressed = true;
                return;
            }

            // Emit completed sentences as soon as a sentence terminator arrives, holding back any unclosed '[' tag
            while (true)
            {
                string buf = _buffer.ToString();
                int openBracketIdx = buf.IndexOf('[');
                string searchable = openBracketIdx >= 0 ? buf[..openBracketIdx] : buf;
                int splitIdx = FindSentenceSplitIndex(searchable);
                if (splitIdx < 0)
                    break;

                string sentence = StripInternalTags(buf[..(splitIdx + 1)]);
                _buffer.Remove(0, splitIdx + 1);
                if (!string.IsNullOrWhiteSpace(sentence))
                    EmitSentence(sentence);
            }
        }

        public void FlushRemaining()
        {
            if (_suppressed)
                return;

            string rem = _buffer.ToString();
            _buffer.Clear();
            rem = StripInternalTags(rem);
            if (!string.IsNullOrWhiteSpace(rem) &&
                !rem.Contains("[[IGNORE]]", StringComparison.OrdinalIgnoreCase) &&
                !rem.Contains("[[SILENCE]]", StringComparison.OrdinalIgnoreCase))
            {
                EmitSentence(rem);
            }
        }

        private void EmitSentence(string sentence)
        {
            onTextChunk?.Invoke(_sentenceCount == 0 ? sentence : " " + sentence);
            if (speakEnabled)
            {
                string normalized = NormalizeTextForSpeech(sentence, language);
                if (!string.IsNullOrWhiteSpace(normalized))
                    speechService.SpeakStreamSentence(normalized, interruptPrevious: _sentenceCount == 0);
            }
            _sentenceCount++;
        }

        private static int FindSentenceSplitIndex(string text)
        {
            if (text.Length < 18)
                return -1;

            for (int i = 15; i < text.Length - 1; i++)
            {
                char c = text[i];
                if (c is '.' or '!' or '?' or '\n')
                {
                    char next = text[i + 1];
                    // Avoid splitting decimal numbers like 98.5 or abbreviations
                    if (c == '.' && char.IsDigit(next))
                        continue;
                    if (c == '\n' || char.IsWhiteSpace(next))
                        return i;
                }
            }
            return -1;
        }
    }

    [GeneratedRegex(@"[*_`#]+")]
    private static partial Regex RxSpeechMarkdown();

    [GeneratedRegex(@"[⚡🛰📝⚠🔍✨📖🧹⏹➤•·—|]")]
    private static partial Regex RxSpeechSymbols();

    [GeneratedRegex(@"(?<=\d)[.,](?=\d{3}(?:\b|[^\d]))")]
    private static partial Regex RxSpeechThousandsSep();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*/\s*(\d+(?:[.,]\d+)?)\s*t\b", RegexOptions.IgnoreCase)]
    private static partial Regex RxSpeechFractionTons();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*CR\b", RegexOptions.IgnoreCase)]
    private static partial Regex RxSpeechCredits();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*CR/h\b", RegexOptions.IgnoreCase)]
    private static partial Regex RxSpeechCreditsPerHour();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*t\b", RegexOptions.IgnoreCase)]
    private static partial Regex RxSpeechTons();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(?:AL|LY)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RxSpeechLightYears();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*LS\b", RegexOptions.IgnoreCase)]
    private static partial Regex RxSpeechLightSeconds();

    [GeneratedRegex(@"\bCMDR\b", RegexOptions.IgnoreCase)]
    private static partial Regex RxSpeechCmdr();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex RxSpeechMultiSpace();

    internal static string NormalizeTextForSpeech(string text, string language)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        bool isEs = language.StartsWith("es", StringComparison.OrdinalIgnoreCase);
        bool isFr = language.StartsWith("fr", StringComparison.OrdinalIgnoreCase);
        bool isDe = language.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        bool isRu = language.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
        bool isPt = language.StartsWith("pt", StringComparison.OrdinalIgnoreCase);

        string creditsWord = isEs || isPt ? "créditos" : isFr ? "crédits" : isDe ? "Credits" : isRu ? "кредитов" : "credits";
        string perHourWord = isEs || isPt ? "por hora" : isFr ? "par heure" : isDe ? "pro Stunde" : isRu ? "в час" : "per hour";
        string tonsWord = isEs || isPt ? "toneladas" : isFr ? "tonnes" : isDe ? "Tonnen" : isRu ? "тонн" : "tons";
        string lyWord = isEs ? "años luz" : isPt ? "anos-luz" : isFr ? "années-lumière" : isDe ? "Lichtjahre" : isRu ? "световых лет" : "light years";
        string lsWord = isEs ? "segundos luz" : isPt ? "segundos-luz" : isFr ? "secondes-lumière" : isDe ? "Lichtsekunden" : isRu ? "световых секунд" : "light seconds";
        string ofWord = isEs || isPt || isFr ? "de" : isDe ? "von" : isRu ? "из" : "of";
        string cmdrWord = isEs || isPt ? "Comandante" : isFr ? "Commandant" : isDe ? "Kommandant" : isRu ? "Командир" : "Commander";

        string s = text;
        s = RxSpeechMarkdown().Replace(s, "");
        s = RxSpeechSymbols().Replace(s, " ");
        s = RxSpeechThousandsSep().Replace(s, "");
        s = RxSpeechFractionTons().Replace(s, $"$1 {ofWord} $2 {tonsWord}");
        s = RxSpeechCreditsPerHour().Replace(s, $"$1 {creditsWord} {perHourWord}");
        s = RxSpeechCredits().Replace(s, $"$1 {creditsWord}");
        s = RxSpeechTons().Replace(s, $"$1 {tonsWord}");
        s = RxSpeechLightYears().Replace(s, $"$1 {lyWord}");
        s = RxSpeechLightSeconds().Replace(s, $"$1 {lsWord}");
        s = RxSpeechCmdr().Replace(s, cmdrWord);
        s = RxSpeechMultiSpace().Replace(s, " ").Trim();
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
                string alert = string.Format(OperationsTranslations.ForLanguage(language)["covasTimerDone"], reason);
                CopilotMessageAdded?.Invoke("COVAS ⏱", alert);
                if (_configService.Current.AiSpeakReplies)
                    _speechService.SpeakCustomText(NormalizeTextForSpeech(alert, language));
            }
            catch (OperationCanceledException)
            {
                // Application shutting down.
            }
            catch (Exception ex)
            {
                AppDiagnosticsLogger.Warn("AI.Timer", $"Scheduled COVAS voice timer error: {ex.Message}");
            }
        }, token);
    }

}