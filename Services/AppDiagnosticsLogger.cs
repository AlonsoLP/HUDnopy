// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/AppDiagnosticsLogger.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Zero-dependency, thread-safe daily rolling diagnostic logger with
//              secret redaction, in-memory breadcrumbs ring buffer, error-storm
//              deduplication, HResult/AggregateException flattening, 7-day
//              retention, and 1-click sanitized Support ZIP bundling.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using HUDnopy.Models;

namespace HUDnopy.Services;

/// <summary>
/// Thread-safe, zero-dependency daily rolling diagnostic logger with secret redaction,
/// in-memory breadcrumbs, storm deduplication, and sanitized Support ZIP export.
/// </summary>
public static partial class AppDiagnosticsLogger
{
    private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB safety cap per daily file
    private const int RetentionDays = 7;
    private const int MaxBreadcrumbs = 40;
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(10);

    private static readonly Lock WriteLock = new();
    private static readonly Queue<string> Breadcrumbs = new(MaxBreadcrumbs);
    private static DateOnly _lastPrunedDayUtc = DateOnly.MinValue;
    private static string _lastDedupKey = "";
    private static DateTimeOffset _lastDedupUtc = DateTimeOffset.MinValue;
    private static int _suppressedDuplicates;

    [GeneratedRegex(@"((?:api[_-]?key|access[_-]?token|refresh[_-]?token|commander[_-]?name|password|secret|key)\s*[=:]\s*)([^\s&;""',]+)", RegexOptions.IgnoreCase)]
    private static partial Regex QueryOrKeyValueSecretRegex();

    [GeneratedRegex(@"(Bearer\s+)([A-Za-z0-9\-._~+/]+=*)", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(https?://(?:ptb\.|canary\.)?discord(?:app)?\.com/api/webhooks/\d+/)([^\s""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex DiscordWebhookRegex();

    [GeneratedRegex(@"([A-Za-z]:[\\/]+Users[\\/]+)([^\\/\s""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsUserPathRegex();

    /// <summary>
    /// Root directory where daily diagnostic logs are written.
    /// </summary>
    public static string LogsDirectory
    {
        get => field;
        internal set
        {
            field = value;
            _lastPrunedDayUtc = DateOnly.MinValue;
            _lastDedupKey = "";
            _suppressedDuplicates = 0;
        }
    } = HttpClientHeaderDefaults.GetLocalDataPath("logs");

    /// <summary>
    /// Full path to today's UTC rolling diagnostic log file.
    /// </summary>
    public static string CurrentLogFilePath =>
        Path.Combine(LogsDirectory, $"hudnopy-{DateTimeOffset.UtcNow:yyyy-MM-dd}.log");

    /// <summary>
    /// Records a lightweight in-memory breadcrumb (zero disk I/O) dumped automatically on errors/crashes.
    /// </summary>
    public static void RecordBreadcrumb(string category, string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return;

        string entry = $"{DateTimeOffset.UtcNow:HH:mm:ss.fff}Z [{category}] {RedactSecrets(detail)}";
        lock (WriteLock)
        {
            if (Breadcrumbs.Count >= MaxBreadcrumbs)
                Breadcrumbs.Dequeue();
            Breadcrumbs.Enqueue(entry);
        }
    }

    /// <summary>
    /// Redacts API keys, Bearer tokens, passwords, Discord webhook secrets, and Windows username paths before writing logs to disk.
    /// </summary>
    internal static string RedactSecrets(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        string redacted = DiscordWebhookRegex().Replace(text, "$1[REMOVED]");
        redacted = BearerTokenRegex().Replace(redacted, "$1[REMOVED]");
        redacted = QueryOrKeyValueSecretRegex().Replace(redacted, "$1[REMOVED]");
        redacted = WindowsUserPathRegex().Replace(redacted, "$1[USER]");
        return redacted;
    }

    /// <summary>
    /// Writes a privacy-safe environment and opt-in feature summary banner at application startup.
    /// </summary>
    public static void LogStartupBanner(AppConfig cfg)
    {
        Info("Startup", BuildEnvironmentSummary(cfg));
    }

    /// <summary>
    /// Exports a sanitized Support ZIP archive containing environment metadata, recent breadcrumbs,
    /// a redacted copy of AppConfig, and the last 3 days of rolling diagnostic logs.
    /// </summary>
    public static string ExportSupportBundle(AppConfig cfg, string? outputZipPath = null)
    {
        string targetZip = string.IsNullOrWhiteSpace(outputZipPath)
            ? Path.Combine(LogsDirectory, $"HUDnopy-Support-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip")
            : outputZipPath;

        string? parentDir = Path.GetDirectoryName(Path.GetFullPath(targetZip));
        if (!string.IsNullOrEmpty(parentDir))
            Directory.CreateDirectory(parentDir);

        string[] crumbsSnapshot;
        lock (WriteLock)
            crumbsSnapshot = [.. Breadcrumbs];

        if (File.Exists(targetZip))
            File.Delete(targetZip);

        using var archive = ZipFile.Open(targetZip, ZipArchiveMode.Create);

        // 1. Environment & recent breadcrumbs
        var envSb = new StringBuilder();
        envSb.AppendLine(BuildEnvironmentSummary(cfg));
        envSb.AppendLine($"WorkingSetMB: {Environment.WorkingSet / (1024 * 1024)}");
        envSb.AppendLine($"ProcessorCount: {Environment.ProcessorCount}");
        envSb.AppendLine($"ExportedUtc: {DateTimeOffset.UtcNow:O}");
        envSb.AppendLine();
        envSb.AppendLine("=== Recent Breadcrumbs ===");
        if (crumbsSnapshot.Length == 0)
            envSb.AppendLine("(none)");
        else
            foreach (string c in crumbsSnapshot)
                envSb.AppendLine(c);
        WriteZipTextEntry(archive, "environment.txt", envSb.ToString());

        // 2. Strict allowlist technical config snapshot (zero API keys, tokens, commander names, notes, memories, bookmarks, or personal paths)
        var technicalConfig = new
        {
            cfg.InterfaceLanguage,
            cfg.WebServerPort,
            cfg.AutoStartWebServer,
            cfg.WebAccessMode,
            HasVpnInterfaceSelected = !string.IsNullOrWhiteSpace(cfg.WebVpnInterfaceId),
            cfg.DesktopFontScalePercent,
            cfg.StartMinimizedToTray,
            cfg.NavigationMenuPosition,
            cfg.FocusGameWindowOnInput,
            cfg.HideUnassignedBindings,
            cfg.HideNavigationMenuText,
            cfg.SpeechEngine,
            cfg.PiperVoiceId,
            cfg.SpeechRate,
            cfg.SpeechQueueMode,
            cfg.AlertSoundVolumePercent,
            cfg.CaptureUnhandledEvents,
            cfg.EnableSpanshSystemData,
            cfg.EnableEddnUpload,
            cfg.EnableEdsmSync,
            HasEdsmCredentials = !string.IsNullOrWhiteSpace(cfg.EdsmCommanderName) && !string.IsNullOrWhiteSpace(cfg.EdsmApiKey),
            cfg.EnableInaraSync,
            HasInaraCredentials = !string.IsNullOrWhiteSpace(cfg.InaraCommanderName) && !string.IsNullOrWhiteSpace(cfg.InaraApiKey),
            cfg.EnableFrontierCapi,
            HasFrontierClientId = !string.IsNullOrWhiteSpace(cfg.FrontierCapiClientId),
            cfg.EnableEliteBgsWatchlist,
            cfg.EnableArdentMarketApi,
            cfg.EnableEdastroDssa,
            cfg.EnableCanonnUpload,
            cfg.EnableDiscord,
            HasDiscordWebhook = !string.IsNullOrWhiteSpace(cfg.DiscordWebhookUrl),
            cfg.EnableAiCopilot,
            cfg.AiActiveProvider,
            cfg.AiOllamaModel,
            cfg.AiGeminiModel,
            cfg.AiOpenAiModel,
            cfg.AiPersonality,
            cfg.AiVerbosity,
            cfg.AiThinkingMode,
            cfg.AiSpeakReplies,
            cfg.AiAllowActions,
            cfg.AiDynamicEvents,
            cfg.AiRequireWakeWord,
            cfg.AiVoiceMacrosEnabled
        };
        string safeCfg = JsonSerializer.Serialize(technicalConfig, HttpClientHeaderDefaults.IndentedJsonOptions);
        WriteZipTextEntry(archive, "config.sanitized.json", safeCfg);

        // 3. Up to 3 most recent daily rolling log files (re-sanitized on export for defense-in-depth)
        if (Directory.Exists(LogsDirectory))
        {
            var recentLogs = new DirectoryInfo(LogsDirectory)
                .EnumerateFiles("hudnopy-*.log")
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .Take(3);
            foreach (var logFile in recentLogs)
            {
                try
                {
                    using var fs = new FileStream(logFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    WriteZipTextEntry(archive, $"logs/{logFile.Name}", RedactSecrets(sr.ReadToEnd()));
                }
                catch
                {
                    // Skip locked/unreadable log file
                }
            }
        }

        return targetZip;
    }

    private static string BuildEnvironmentSummary(AppConfig cfg) =>
        $"HUDnopy v{HttpClientHeaderDefaults.ProductVersion} | {RuntimeInformation.FrameworkDescription} | " +
        $"OS={Environment.OSVersion} ({RuntimeInformation.ProcessArchitecture}) | Lang={cfg.InterfaceLanguage} | " +
        $"Web={cfg.AutoStartWebServer}({cfg.WebAccessMode}:{cfg.WebServerPort}) | " +
        $"OptIn[Spansh={cfg.EnableSpanshSystemData},EDDN={cfg.EnableEddnUpload},EDSM={cfg.EnableEdsmSync}," +
        $"Inara={cfg.EnableInaraSync},cAPI={cfg.EnableFrontierCapi},BGS={cfg.EnableEliteBgsWatchlist}," +
        $"Ardent={cfg.EnableArdentMarketApi},DSSA={cfg.EnableEdastroDssa},Canonn={cfg.EnableCanonnUpload}," +
        $"Discord={cfg.EnableDiscord},AI={cfg.EnableAiCopilot}({cfg.AiActiveProvider})]";

    private static void WriteZipTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var sw = new StreamWriter(entry.Open(), Encoding.UTF8);
        sw.Write(content);
    }

    /// <summary>
    /// Logs an informational message to the daily diagnostic log file.
    /// </summary>
    public static void Info(string category, string message) => Write("INFO", category, message, null);

    /// <summary>
    /// Logs a warning message to the daily diagnostic log file.
    /// </summary>
    public static void Warn(string category, string message) => Write("WARN", category, message, null);

    /// <summary>
    /// Logs an error and optional exception stack trace (with HResult, flattened inner exceptions, and recent breadcrumbs).
    /// </summary>
    public static void Error(string category, string message, Exception? ex = null) => Write("ERROR", category, message, ex);

    private static void Write(string level, string category, string message, Exception? ex)
    {
        try
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            DateOnly todayUtc = DateOnly.FromDateTime(nowUtc.UtcDateTime);
            string dir = LogsDirectory;
            string filePath = Path.Combine(dir, $"hudnopy-{todayUtc:yyyy-MM-dd}.log");
            string cleanMessage = RedactSecrets(message);
            string dedupKey = $"{level}|{category}|{cleanMessage}|{ex?.GetType().Name}";

            lock (WriteLock)
            {
                // Suppress identical error/warning storms within a 10-second window
                if (ex is null && dedupKey == _lastDedupKey && (nowUtc - _lastDedupUtc) < DedupWindow)
                {
                    _suppressedDuplicates++;
                    return;
                }

                var sb = new StringBuilder(256);
                if (_suppressedDuplicates > 0)
                {
                    sb.Append(nowUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                      .Append("Z [INFO] [Diagnostics] Suppressed ")
                      .Append(_suppressedDuplicates)
                      .AppendLine(" identical repeated log message(s) in the last 10s.");
                    _suppressedDuplicates = 0;
                }

                _lastDedupKey = dedupKey;
                _lastDedupUtc = nowUtc;

                sb.Append(nowUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                  .Append("Z [").Append(level).Append("] [T").Append(Environment.CurrentManagedThreadId)
                  .Append("] [").Append(category).Append("] ")
                  .AppendLine(cleanMessage);

                if (ex is not null)
                    AppendExceptionDetails(sb, ex);

                if (level == "ERROR" && Breadcrumbs.Count > 0)
                {
                    sb.AppendLine("  Recent breadcrumbs:");
                    foreach (string crumb in Breadcrumbs)
                        sb.Append("    ").AppendLine(crumb);
                }

                Directory.CreateDirectory(dir);
                if (todayUtc != _lastPrunedDayUtc)
                {
                    PruneOldLogs(dir, todayUtc);
                    _lastPrunedDayUtc = todayUtc;
                }

                if (File.Exists(filePath) && new FileInfo(filePath).Length >= MaxFileSizeBytes)
                    return;

                File.AppendAllText(filePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception logEx)
        {
            Trace.TraceError($"AppDiagnosticsLogger failed: {logEx.Message}");
        }
    }

    private static void AppendExceptionDetails(StringBuilder sb, Exception ex)
    {
        if (ex is AggregateException agg)
        {
            var flat = agg.Flatten();
            sb.Append("  AggregateException (").Append(flat.InnerExceptions.Count).AppendLine(" inner):");
            for (int i = 0; i < flat.InnerExceptions.Count; i++)
                AppendSingleException(sb, flat.InnerExceptions[i], i + 1);
            return;
        }

        AppendSingleException(sb, ex, 0);
    }

    private static void AppendSingleException(StringBuilder sb, Exception ex, int index)
    {
        Exception? cur = ex;
        int depth = 0;
        while (cur is not null && depth < 6)
        {
            string prefix = depth == 0 && index > 0 ? $"  [#{index}] " : new string(' ', 2 + depth * 2);
            sb.Append(prefix)
              .Append(cur.GetType().FullName)
              .Append(" (HResult=0x").Append(cur.HResult.ToString("X8", CultureInfo.InvariantCulture)).Append("): ")
              .AppendLine(RedactSecrets(cur.Message));
            if (!string.IsNullOrWhiteSpace(cur.StackTrace))
                sb.AppendLine(RedactSecrets(cur.StackTrace));
            cur = cur.InnerException;
            depth++;
        }
    }

    private static void PruneOldLogs(string dir, DateOnly todayUtc)
    {
        try
        {
            DateOnly cutoff = todayUtc.AddDays(-RetentionDays);
            foreach (string path in Directory.EnumerateFiles(dir, "hudnopy-*.log"))
            {
                string fileName = Path.GetFileNameWithoutExtension(path);
                if (fileName.Length == "hudnopy-yyyy-MM-dd".Length &&
                    DateOnly.TryParseExact(fileName.AsSpan(8), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDay) &&
                    fileDay < cutoff)
                {
                    try { File.Delete(path); } catch { }
                }
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
