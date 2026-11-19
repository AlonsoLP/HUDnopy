// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/PiperTtsService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Portable Piper neural TTS engine manager, voice downloader, and ONNX WAV synthesizer.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace HUDnopy.Services;

/// <summary>
/// Portable Piper neural TTS engine manager, voice downloader, and ONNX WAV synthesizer.
/// </summary>
public sealed record PiperVoiceInfo(
    string Id,
    string Language,
    string DisplayName,
    string HuggingFaceRelativePath,
    bool IsPrimaryForLanguage);

public sealed class PiperTtsService : IDisposable
{
    private const string PiperReleaseZipUrl =
        "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
    private const string HuggingFaceVoicesBaseUrl =
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/";

    public static readonly IReadOnlyList<PiperVoiceInfo> Catalog = new[]
    {
        new PiperVoiceInfo("es_ES-davefx-medium", "es-ES", "Español (España) — DaveFX (Natural)", "es/es_ES/davefx/medium/es_ES-davefx-medium.onnx", true),
        new PiperVoiceInfo("es_ES-sharvard-medium", "es-ES", "Español (España) — Sharvard", "es/es_ES/sharvard/medium/es_ES-sharvard-medium.onnx", false),
        new PiperVoiceInfo("es_MX-claude-high", "es-ES", "Español (México) — Claude (Alta calidad)", "es/es_MX/claude/high/es_MX-claude-high.onnx", false),
        new PiperVoiceInfo("en_GB-alba-medium", "en-GB", "English (UK) — Alba (COVAS style)", "en/en_GB/alba/medium/en_GB-alba-medium.onnx", true),
        new PiperVoiceInfo("en_GB-northern_english_male-medium", "en-GB", "English (UK) — Northern Male", "en/en_GB/northern_english_male/medium/en_GB-northern_english_male-medium.onnx", false),
        new PiperVoiceInfo("en_US-lessac-medium", "en-GB", "English (US) — Lessac", "en/en_US/lessac/medium/en_US-lessac-medium.onnx", false),
        new PiperVoiceInfo("fr_FR-siwis-medium", "fr-FR", "Français — Siwis", "fr/fr_FR/siwis/medium/fr_FR-siwis-medium.onnx", true),
        new PiperVoiceInfo("de_DE-thorsten-medium", "de-DE", "Deutsch — Thorsten", "de/de_DE/thorsten/medium/de_DE-thorsten-medium.onnx", true),
        new PiperVoiceInfo("ru_RU-dmitri-medium", "ru-RU", "Русский — Dmitri", "ru/ru_RU/dmitri/medium/ru_RU-dmitri-medium.onnx", true),
        new PiperVoiceInfo("ru_RU-irina-medium", "ru-RU", "Русский — Irina", "ru/ru_RU/irina/medium/ru_RU-irina-medium.onnx", false),
        new PiperVoiceInfo("pt_BR-faber-medium", "pt-BR", "Português (BR) — Faber", "pt/pt_BR/faber/medium/pt_BR-faber-medium.onnx", true)
    };

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    public string BaseDirectory { get; }
    public string VoicesDirectory { get; }
    public string LastError { get; private set; } = "";

    public PiperTtsService()
    {
        BaseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HUDnopy",
            "Piper");
        VoicesDirectory = Path.Combine(BaseDirectory, "voices");
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HUDnopy", "0.1"));
    }

    private static bool IsValidPiperBinary(string exePath)
    {
        if (!File.Exists(exePath))
            return false;
        string? dir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(dir))
            return false;
        // El binario oficial de Piper Windows incluye espeak-ng-data y onnxruntime.dll junto a piper.exe
        return Directory.Exists(Path.Combine(dir, "espeak-ng-data")) &&
               File.Exists(Path.Combine(dir, "onnxruntime.dll"));
    }

    public string? FindPiperExecutable()
    {
        string[] localCandidates =
        {
            Path.Combine(BaseDirectory, "piper", "piper.exe"),
            Path.Combine(BaseDirectory, "piper.exe")
        };
        foreach (string candidate in localCandidates)
        {
            if (IsValidPiperBinary(candidate))
                return candidate;
        }

        if (Directory.Exists(BaseDirectory))
        {
            try
            {
                string? found = Directory.EnumerateFiles(BaseDirectory, "piper.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(IsValidPiperBinary);
                if (found is not null)
                    return found;
            }
            catch
            {
            }
        }

        // Comprobar si ya existe una instalación completa previa en PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir, "piper.exe");
                    if (IsValidPiperBinary(candidate))
                        return candidate;
                }
                catch
                {
                    // Ignorar rutas inválidas en PATH
                }
            }
        }

        return null;
    }

    public bool IsEngineInstalled() => FindPiperExecutable() is not null;

    public string GetVoiceModelPath(string voiceId) => Path.Combine(VoicesDirectory, $"{voiceId}.onnx");

    public bool IsVoiceInstalled(string voiceId)
    {
        string onnx = GetVoiceModelPath(voiceId);
        string json = onnx + ".json";
        try
        {
            return File.Exists(onnx) && new FileInfo(onnx).Length > 1_000_000 &&
                   File.Exists(json) && new FileInfo(json).Length > 100;
        }
        catch
        {
            return false;
        }
    }

    public PiperVoiceInfo? ResolveVoice(string configuredVoiceId, string interfaceLanguage)
    {
        if (!string.IsNullOrWhiteSpace(configuredVoiceId) &&
            !configuredVoiceId.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return Catalog.FirstOrDefault(v => string.Equals(v.Id, configuredVoiceId, StringComparison.OrdinalIgnoreCase));
        }

        return Catalog.FirstOrDefault(v =>
                   v.IsPrimaryForLanguage &&
                   string.Equals(v.Language, interfaceLanguage, StringComparison.OrdinalIgnoreCase))
               ?? Catalog[0];
    }

    public async Task<(bool Success, string Message)> EnsureEngineInstalledAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (IsEngineInstalled())
            return (true, "Piper OK");

        Directory.CreateDirectory(BaseDirectory);
        string tempZip = Path.Combine(BaseDirectory, "piper_windows_amd64.zip.part");
        string finalZip = Path.Combine(BaseDirectory, "piper_windows_amd64.zip");

        try
        {
            progress?.Report("Descargando motor Piper TTS (~20 MB)...");
            var (ok, err) = await DownloadFileAtomicallyAsync(PiperReleaseZipUrl, tempZip, finalZip, minBytes: 2_000_000, ct);
            if (!ok)
                return (false, err);

            progress?.Report("Extrayendo motor Piper TTS...");
            ZipFile.ExtractToDirectory(finalZip, BaseDirectory, overwriteFiles: true);
            try { File.Delete(finalZip); } catch { }

            if (!IsEngineInstalled())
                return (false, "No se encontró piper.exe tras extraer el paquete descargado.");

            return (true, "Motor Piper TTS instalado.");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            return (false, $"Error instalando Piper TTS: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> DownloadVoiceAsync(
        string voiceId,
        string interfaceLanguage,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var voice = ResolveVoice(voiceId, interfaceLanguage);
        if (voice is null)
            return (false, "Voz no encontrada en el catálogo.");

        await _downloadLock.WaitAsync(ct);
        try
        {
            var (engOk, engMsg) = await EnsureEngineInstalledAsync(progress, ct);
            if (!engOk)
                return (false, engMsg);

            if (IsVoiceInstalled(voice.Id))
                return (true, $"Voz ya instalada: {voice.DisplayName}");

            return await DownloadVoiceCoreAsync(voice, progress, ct);
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    public async Task<(bool Success, string Message)> DownloadAllSixLanguagesPackAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        await _downloadLock.WaitAsync(ct);
        try
        {
            var (engOk, engMsg) = await EnsureEngineInstalledAsync(progress, ct);
            if (!engOk)
                return (false, engMsg);

            var primaryVoices = Catalog.Where(static v => v.IsPrimaryForLanguage).ToList();
            int downloaded = 0;
            var errors = new List<string>();

            for (int i = 0; i < primaryVoices.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var voice = primaryVoices[i];
                if (IsVoiceInstalled(voice.Id))
                {
                    downloaded++;
                    continue;
                }

                progress?.Report($"({i + 1}/{primaryVoices.Count}) {voice.DisplayName}...");
                var (ok, msg) = await DownloadVoiceCoreAsync(voice, null, ct);
                if (ok)
                    downloaded++;
                else
                    errors.Add($"{voice.Language}: {msg}");
            }

            if (errors.Count == 0)
                return (true, $"Pack completado ({downloaded}/{primaryVoices.Count} idiomas listos).");

            return (downloaded > 0, $"Descargadas {downloaded}/{primaryVoices.Count}. Errores: {string.Join(" | ", errors)}");
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private async Task<(bool Success, string Message)> DownloadVoiceCoreAsync(
        PiperVoiceInfo voice,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(VoicesDirectory);
        string onnxPath = GetVoiceModelPath(voice.Id);
        string jsonPath = onnxPath + ".json";
        string onnxUrl = HuggingFaceVoicesBaseUrl + voice.HuggingFaceRelativePath;
        string jsonUrl = onnxUrl + ".json";

        progress?.Report($"Descargando {voice.DisplayName} (~60 MB)...");
        var (okOnnx, errOnnx) = await DownloadFileAtomicallyAsync(onnxUrl, onnxPath + ".part", onnxPath, minBytes: 1_000_000, ct);
        if (!okOnnx)
            return (false, errOnnx);

        var (okJson, errJson) = await DownloadFileAtomicallyAsync(jsonUrl, jsonPath + ".part", jsonPath, minBytes: 100, ct);
        if (!okJson)
            return (false, errJson);

        SanitizeVoiceConfigJson(jsonPath);
        return (true, $"Voz instalada: {voice.DisplayName}");
    }

    private static void SanitizeVoiceConfigJson(string jsonPath)
    {
        try
        {
            if (!File.Exists(jsonPath))
                return;

            string raw = File.ReadAllText(jsonPath, Encoding.UTF8);
            var root = JsonNode.Parse(raw) as JsonObject;
            if (root is null || root["phoneme_id_map"] is not JsonObject map)
                return;

            var invalidKeys = new List<string>();
            foreach (var kv in map)
            {
                if (kv.Key.EnumerateRunes().Count() != 1)
                    invalidKeys.Add(kv.Key);
            }

            if (invalidKeys.Count == 0)
                return;

            foreach (string key in invalidKeys)
                map.Remove(key);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(jsonPath, root.ToJsonString(options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // No bloquear si el JSON ya es válido
        }
    }

    private async Task<(bool Success, string Error)> DownloadFileAtomicallyAsync(
        string url,
        string tempPath,
        string finalPath,
        long minBytes,
        CancellationToken ct)
    {
        try
        {
            using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, $"Servidor no disponible (HTTP {(int)resp.StatusCode})");

            await using (var remote = await resp.Content.ReadAsStreamAsync(ct))
            await using (var local = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await remote.CopyToAsync(local, ct);
            }

            long size = new FileInfo(tempPath).Length;
            if (size < minBytes)
            {
                try { File.Delete(tempPath); } catch { }
                return (false, "El archivo descargado está incompleto o corrupto.");
            }

            File.Move(tempPath, finalPath, overwrite: true);
            return (true, "");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            return (false, ex.Message);
        }
    }

    public async Task<byte[]?> TrySynthesizeWavAsync(
        string text,
        string configuredVoiceId,
        string interfaceLanguage,
        int speechRate,
        CancellationToken ct)
    {
        LastError = "";
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string? exe = FindPiperExecutable();
        if (exe is null)
        {
            LastError = "Motor Piper TTS no instalado. Pulsa «Descargar voz» o «Pack 6 idiomas» en Ajustes.";
            return null;
        }

        var voice = ResolveVoice(configuredVoiceId, interfaceLanguage);
        if (voice is null || !IsVoiceInstalled(voice.Id))
        {
            LastError = $"Voz Piper no descargada ({voice?.DisplayName ?? configuredVoiceId}). Pulsa «Descargar voz» en Ajustes.";
            return null;
        }

        string modelPath = GetVoiceModelPath(voice.Id);
        SanitizeVoiceConfigJson(modelPath + ".json");
        string exeDir = Path.GetDirectoryName(exe) ?? BaseDirectory;
        string espeakDir = Path.Combine(exeDir, "espeak-ng-data");
        string espeakArg = Directory.Exists(espeakDir) ? $"--espeak_data \"{espeakDir}\" " : "";
        // SpeechRate [-5..5] -> length_scale exponencial [~2.00..~0.50] (equivalente a Windows SAPI -5..+5)
        int clampedRate = Math.Clamp(speechRate, -5, 5);
        double lengthScale = Math.Clamp(Math.Pow(0.87, clampedRate), 0.50, 2.00);
        string tempWav = Path.Combine(Path.GetTempPath(), $"hudnopy_piper_{Guid.NewGuid():N}.wav");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--model \"{modelPath}\" {espeakArg}--length_scale {lengthScale.ToString("0.00", CultureInfo.InvariantCulture)} --output_file \"{tempWav}\"",
                WorkingDirectory = exeDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                LastError = "No se pudo iniciar piper.exe.";
                return null;
            }

            Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);

            string cleanLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
            await proc.StandardInput.WriteAsync(cleanLine + "\n");
            await proc.StandardInput.FlushAsync(ct);
            proc.StandardInput.Close();

            await proc.WaitForExitAsync(ct);
            string stderr = await stderrTask;
            _ = await stdoutTask;

            if (proc.ExitCode != 0 || !File.Exists(tempWav))
            {
                LastError = $"Piper error (código {proc.ExitCode}): {(string.IsNullOrWhiteSpace(stderr) ? "sin salida" : stderr.Trim())}";
                return null;
            }

            byte[] bytes = await File.ReadAllBytesAsync(tempWav, ct);
            if (bytes.Length <= 44)
            {
                LastError = "Piper generó un archivo de audio vacío.";
                return null;
            }
            return bytes;
        }
        catch (Exception ex)
        {
            LastError = $"Piper excepción: {ex.Message}";
            return null;
        }
        finally
        {
            try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _downloadLock.Dispose();
    }
}
