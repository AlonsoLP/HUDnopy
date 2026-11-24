// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/WhisperSttService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Portable on-demand Whisper.cpp offline speech-to-text engine manager
//              supporting a resident in-memory whisper-server.exe daemon (low-latency
//              loopback HTTP inference) with whisper-cli.exe fallback.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HUDnopy.Services;

/// <summary>
/// Metadata for an offline Whisper GGML model downloadable on demand.
/// </summary>
public sealed record WhisperModelInfo(
    string Id,
    string DisplayName,
    string FileName,
    string DownloadUrl,
    int ApproximateSizeMb);

/// <summary>
/// Manages portable offline Whisper.cpp binaries and GGML models under %LocalAppData%\HUDnopy\whisper\.
/// Uses a resident localhost whisper-server.exe process (lazy-started on first transcription) to keep
/// the GGML model warm in RAM (~120-250 ms inference, zero per-utterance disk I/O), with automatic
/// fallback to whisper-cli.exe if whisper-server.exe is unavailable.
/// </summary>
public sealed partial class WhisperSttService : IDisposable
{
    private const string WhisperReleasesApiUrl =
        "https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=10";
    private static readonly string[] FallbackWhisperZipUrls =
    [
        "https://github.com/ggml-org/whisper.cpp/releases/download/v1.7.6/whisper-bin-x64.zip",
        "https://github.com/ggml-org/whisper.cpp/releases/download/v1.7.4/whisper-bin-x64.zip"
    ];

    public static readonly IReadOnlyList<WhisperModelInfo> Catalog = new[]
    {
        new WhisperModelInfo(
            "tiny",
            "Whisper Tiny (~75 MB · Fastest, multilingual)",
            "ggml-tiny.bin",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin",
            75),
        new WhisperModelInfo(
            "base",
            "Whisper Base (~142 MB · Balanced accuracy, multilingual)",
            "ggml-base.bin",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
            142)
    };

    private readonly string _rootDir;
    private readonly HttpClient _http;
    private readonly HttpClient _localHttp;
    private readonly SemaphoreSlim _serverLock = new(1, 1);
    private Process? _serverProcess;
    private int _serverPort;
    private string _loadedModelId = "";
    private string? _cachedExePath;
    private string? _cachedServerExePath;
    private bool _disposed;

    [GeneratedRegex(@"\[[^\]]*\]|\([^\)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex BracketNoiseRegex();

    public WhisperSttService(string? customRootDir = null)
    {
        _rootDir = string.IsNullOrWhiteSpace(customRootDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HUDnopy", "whisper")
            : customRootDir;
        _http = HttpClientHeaderDefaults.CreateHttpClient(TimeSpan.FromMinutes(5));
        _localHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    public string RootDirectory => _rootDir;

    public string? FindExecutablePath()
    {
        if (!string.IsNullOrEmpty(_cachedExePath) && File.Exists(_cachedExePath))
            return _cachedExePath;

        if (!Directory.Exists(_rootDir))
            return null;
        foreach (string exeName in new[] { "whisper-cli.exe", "main.exe" })
        {
            string? match = Directory.EnumerateFiles(_rootDir, exeName, SearchOption.AllDirectories).FirstOrDefault();
            if (match is not null)
                return _cachedExePath = match;
        }
        return null;
    }

    public string? FindServerExecutablePath()
    {
        if (!string.IsNullOrEmpty(_cachedServerExePath) && File.Exists(_cachedServerExePath))
            return _cachedServerExePath;

        if (!Directory.Exists(_rootDir))
            return null;
        foreach (string exeName in new[] { "whisper-server.exe", "server.exe" })
        {
            string? match = Directory.EnumerateFiles(_rootDir, exeName, SearchOption.AllDirectories).FirstOrDefault();
            if (match is not null)
                return _cachedServerExePath = match;
        }
        return null;
    }

    public string GetModelPath(string modelId)
    {
        var info = ResolveModel(modelId);
        return Path.Combine(_rootDir, "models", info.FileName);
    }

    public bool IsEngineInstalled() => FindServerExecutablePath() is not null || FindExecutablePath() is not null;

    public bool IsModelInstalled(string modelId)
    {
        string path = GetModelPath(modelId);
        return File.Exists(path) && new FileInfo(path).Length > 1_000_000;
    }

    public static WhisperModelInfo ResolveModel(string? modelId) =>
        Catalog.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase)) ?? Catalog[0];

    /// <summary>
    /// Stops any running resident whisper-server.exe process and releases its RAM.
    /// </summary>
    public void StopResidentServer()
    {
        try
        {
            if (_serverProcess is not null)
            {
                if (!_serverProcess.HasExited)
                    _serverProcess.Kill(entireProcessTree: true);
                _serverProcess.Dispose();
            }
        }
        catch { }
        finally
        {
            _serverProcess = null;
            _serverPort = 0;
            _loadedModelId = "";
        }
    }

    /// <summary>
    /// Downloads and extracts the portable whisper.cpp Windows x64 binaries and the requested GGML model on demand.
    /// </summary>
    public async Task<(bool Success, string Message)> EnsureInstalledAsync(
        string modelId,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(_rootDir);
            Directory.CreateDirectory(Path.Combine(_rootDir, "models"));

            if (!IsEngineInstalled())
            {
                progress?.Report("Resolving latest Whisper.cpp Windows x64 release...");
                string zipUrl = await ResolveWhisperZipUrlAsync(ct);
                progress?.Report("Downloading Whisper.cpp portable engine...");
                string zipPath = Path.Combine(_rootDir, "whisper-bin-x64.zip");
                await DownloadFileAsync(zipUrl, zipPath, progress, "Engine", ct);
                progress?.Report("Extracting Whisper.cpp...");
                ZipFile.ExtractToDirectory(zipPath, _rootDir, overwriteFiles: true);
                try { File.Delete(zipPath); } catch { }
            }

            var model = ResolveModel(modelId);
            string modelPath = GetModelPath(model.Id);
            if (!IsModelInstalled(model.Id))
            {
                progress?.Report($"Downloading {model.FileName} (~{model.ApproximateSizeMb} MB)...");
                string tmpPath = modelPath + ".part";
                await DownloadFileAsync(model.DownloadUrl, tmpPath, progress, model.FileName, ct);
                File.Move(tmpPath, modelPath, overwrite: true);
            }

            if (!IsEngineInstalled() || !IsModelInstalled(model.Id))
                return (false, "Whisper installation incomplete: executable or model not found.");

            return (true, $"Whisper offline ready ({model.Id}).");
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("WhisperSttService", $"Install failed: {ex.Message}");
            return (false, $"Whisper install error: {ex.Message}");
        }
    }

    /// <summary>
    /// Transcribes a 16 kHz PCM WAV buffer completely offline. Prefers the resident in-memory
    /// whisper-server.exe daemon for low latency and falls back to whisper-cli.exe if needed.
    /// </summary>
    public async Task<(bool Success, string Transcript)> TranscribeWavAsync(
        byte[] wavBytes,
        string? uiLanguage,
        string modelId,
        CancellationToken ct = default)
    {
        if (wavBytes is null || wavBytes.Length <= 44)
            return (false, "");

        string modelPath = GetModelPath(modelId);
        if (!File.Exists(modelPath))
            return (false, "Whisper offline is not installed yet. Click 'Install Whisper' in Settings -> COVAS AI.");

        string lang = NormalizeWhisperLanguage(uiLanguage);

        // 1. Fast path: resident in-memory whisper-server.exe (zero model reload, zero temp WAV files)
        if (FindServerExecutablePath() is not null)
        {
            var serverResult = await TryTranscribeViaResidentServerAsync(wavBytes, lang, modelId, modelPath, ct);
            if (serverResult.Success)
                return serverResult;
        }

        // 2. Fallback path: one-shot whisper-cli.exe
        return await TranscribeViaCliAsync(wavBytes, lang, modelPath, ct);
    }

    private async Task<(bool Success, string Transcript)> TryTranscribeViaResidentServerAsync(
        byte[] wavBytes,
        string lang,
        string modelId,
        string modelPath,
        CancellationToken ct)
    {
        try
        {
            int port = await EnsureResidentServerStartedAsync(modelId, modelPath, ct);
            if (port <= 0)
                return (false, "");

            using var content = new MultipartFormDataContent();
            var wavContent = new ByteArrayContent(wavBytes);
            wavContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(wavContent, "file", "audio.wav");
            content.Add(new StringContent("json"), "response_format");
            if (lang != "auto")
                content.Add(new StringContent(lang), "language");

            using var resp = await _localHttp.PostAsync($"http://127.0.0.1:{port}/inference", content, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, "");

            string body = await resp.Content.ReadAsStringAsync(ct);
            string rawText = body;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("text", out var tp))
                    rawText = tp.GetString() ?? "";
            }
            catch { }

            string clean = SanitizeTranscript(rawText);
            return string.IsNullOrWhiteSpace(clean) ? (false, "") : (true, clean);
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("WhisperSttService", $"Resident whisper-server failed, falling back to CLI: {ex.Message}");
            StopResidentServer();
            return (false, "");
        }
    }

    private async Task<int> EnsureResidentServerStartedAsync(string modelId, string modelPath, CancellationToken ct)
    {
        await _serverLock.WaitAsync(ct);
        try
        {
            if (_serverProcess is { HasExited: false } &&
                _serverPort > 0 &&
                string.Equals(_loadedModelId, modelId, StringComparison.OrdinalIgnoreCase))
            {
                return _serverPort;
            }

            StopResidentServer();

            string? serverExe = FindServerExecutablePath();
            if (serverExe is null)
                return 0;

            int port = AllocateLoopbackPort();
            var psi = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = $"--host 127.0.0.1 --port {port} -m \"{modelPath}\"",
                WorkingDirectory = Path.GetDirectoryName(serverExe) ?? _rootDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var proc = Process.Start(psi);
            if (proc is null)
                return 0;

            // Drain stdout/stderr asynchronously so buffers never deadlock
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Wait briefly for the loopback TCP listener to accept connections (up to 6s)
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 6000 && !proc.HasExited && !ct.IsCancellationRequested)
            {
                if (await IsPortListeningAsync(port, ct))
                {
                    _serverProcess = proc;
                    _serverPort = port;
                    _loadedModelId = modelId;
                    AppDiagnosticsLogger.Info("WhisperSttService", $"Resident whisper-server ready on 127.0.0.1:{port} ({modelId}).");
                    return port;
                }
                await Task.Delay(100, ct);
            }

            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            proc.Dispose();
            return 0;
        }
        finally
        {
            _serverLock.Release();
        }
    }

    private async Task<(bool Success, string Transcript)> TranscribeViaCliAsync(
        byte[] wavBytes,
        string lang,
        string modelPath,
        CancellationToken ct)
    {
        string? exe = FindExecutablePath();
        if (exe is null)
            return (false, "Whisper offline executable not found. Click 'Install Whisper' in Settings -> COVAS AI.");

        string tempWav = Path.Combine(Path.GetTempPath(), $"hudnopy_stt_{Guid.NewGuid():N}.wav");
        Process? procRef = null;
        try
        {
            await File.WriteAllBytesAsync(tempWav, wavBytes, ct);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"-m \"{modelPath}\" -f \"{tempWav}\" -l {lang} -nt --no-prints",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? _rootDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, "Failed to start whisper-cli.exe.");
            procRef = proc;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(25));

            string stdout = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token);

            string clean = SanitizeTranscript(stdout);
            return string.IsNullOrWhiteSpace(clean) ? (false, "") : (true, clean);
        }
        catch (OperationCanceledException)
        {
            // Never leave an orphan whisper-cli.exe consuming CPU after a timeout or cancellation.
            try { if (procRef is { HasExited: false }) procRef.Kill(entireProcessTree: true); } catch { }
            return (false, "Whisper transcription timed out.");
        }
        catch (Exception ex)
        {
            try { if (procRef is { HasExited: false }) procRef.Kill(entireProcessTree: true); } catch { }
            AppDiagnosticsLogger.Warn("WhisperSttService", $"CLI transcription failed: {ex.Message}");
            return (false, $"Whisper error: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }
        }
    }

    private static int AllocateLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> IsPortListeningAsync(int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(250);
            await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string NormalizeWhisperLanguage(string? uiLanguage)
    {
        if (string.IsNullOrWhiteSpace(uiLanguage) || uiLanguage.Length < 2)
            return "auto";
        string iso = uiLanguage[..2].ToLowerInvariant();
        return iso is "es" or "en" or "fr" or "de" or "ru" or "pt" ? iso : "auto";
    }

    internal static string SanitizeTranscript(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        string stripped = BracketNoiseRegex().Replace(raw, " ");
        string collapsed = string.Join(' ', stripped.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim();
    }

    private async Task<string> ResolveWhisperZipUrlAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(WhisperReleasesApiUrl, ct);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var release in doc.RootElement.EnumerateArray())
                    {
                        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                            continue;
                        foreach (var asset in assets.EnumerateArray())
                        {
                            string name = asset.TryGetProperty("name", out var np) ? (np.GetString() ?? "") : "";
                            string dl = asset.TryGetProperty("browser_download_url", out var dp) ? (dp.GetString() ?? "") : "";
                            if (name.Equals("whisper-bin-x64.zip", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(dl))
                                return dl;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("WhisperSttService", $"GitHub release lookup failed, using fallback URL: {ex.Message}");
        }
        return FallbackWhisperZipUrls[0];
    }

    private async Task DownloadFileAsync(string url, string destinationPath, IProgress<string>? progress, string label, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destinationPath);

        byte[] buffer = new byte[65536];
        long downloaded = 0;
        int lastPct = -1;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (total is > 0)
            {
                int pct = (int)(downloaded * 100 / total.Value);
                if (pct != lastPct && pct % 10 == 0)
                {
                    lastPct = pct;
                    progress?.Report($"Downloading {label}: {pct}%");
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopResidentServer();
        _http.Dispose();
        _localHttp.Dispose();
        _serverLock.Dispose();
    }
}
