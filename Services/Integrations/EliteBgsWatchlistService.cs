// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/Integrations/EliteBgsWatchlistService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Polls EliteBGS v5 for user-watched minor factions (influence,
//              active/pending states, and conflict alerts).
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HUDnopy.Models;

namespace HUDnopy.Services.Integrations;

/// <summary>
/// Polls EliteBGS v5 API for user-configured minor factions and raises conflict notifications.
/// </summary>
public sealed class EliteBgsWatchlistService : IDisposable
{
    private const string EliteBgsFactionsUrl = "https://elitebgs.app/api/ebgs/v5/factions";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    private readonly ConfigService _configService;
    private readonly EliteFileWatcherService _fileWatcher;
    private readonly HttpClient _httpClient;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _loopCts = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly HashSet<string> _notifiedConflicts = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public string LastStatus { get; private set; } = "";
    public event Action<IReadOnlyList<EliteBgsFactionWatchItem>>? WatchlistUpdated;
    public event Action<EliteBgsFactionWatchItem>? ConflictDetected;

    public EliteBgsWatchlistService(ConfigService configService, EliteFileWatcherService fileWatcher)
    {
        _configService = configService;
        _fileWatcher = fileWatcher;

        _httpClient = HttpClientHeaderDefaults.CreateHttpClient(TimeSpan.FromSeconds(15));
        _timer = new PeriodicTimer(RefreshInterval);
        _ = RunRefreshLoopAsync();
    }

    private async Task RunRefreshLoopAsync()
    {
        try
        {
            await Task.Delay(2000, _loopCts.Token);
            if (!_disposed && _configService.Current.EnableEliteBgsWatchlist)
            {
                try
                {
                    await RefreshAsync(_loopCts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppDiagnosticsLogger.Warn("EliteBGS", $"Initial BGS watchlist refresh failed: {ex.Message}");
                }
            }

            while (await _timer.WaitForNextTickAsync(_loopCts.Token))
            {
                try
                {
                    await RefreshAsync(_loopCts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppDiagnosticsLogger.Warn("EliteBGS", $"BGS watchlist loop iteration failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown
        }
    }

    /// <summary>
    /// Parses the comma/semicolon-separated watched faction names from configuration.
    /// </summary>
    public static List<string> ParseWatchedFactionNames(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new List<string>();

        return raw.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    /// <summary>
    /// Fetches live presence, influence, and state data for all watched factions from EliteBGS v5.
    /// </summary>
    public async Task<(bool Success, List<EliteBgsFactionWatchItem> Items, string Message)> RefreshAsync(CancellationToken ct = default)
    {
        if (_disposed)
            return (false, new List<EliteBgsFactionWatchItem>(), "Service disposed");

        var cfg = _configService.Current;
        if (!cfg.EnableEliteBgsWatchlist)
        {
            LastStatus = "Disabled";
            if (_fileWatcher.State.WatchedBgsFactions.Count > 0)
            {
                _fileWatcher.ApplyCapiEnrichment(static s => s.WatchedBgsFactions = new List<EliteBgsFactionWatchItem>());
                WatchlistUpdated?.Invoke(Array.Empty<EliteBgsFactionWatchItem>());
            }
            return (false, new List<EliteBgsFactionWatchItem>(), "EliteBGS Watchlist is disabled.");
        }

        var factions = ParseWatchedFactionNames(cfg.EliteBgsWatchedFactions);
        if (factions.Count == 0)
        {
            LastStatus = "No watched factions configured";
            _fileWatcher.ApplyCapiEnrichment(static s => s.WatchedBgsFactions = new List<EliteBgsFactionWatchItem>());
            WatchlistUpdated?.Invoke(Array.Empty<EliteBgsFactionWatchItem>());
            return (true, new List<EliteBgsFactionWatchItem>(), LastStatus);
        }

        if (!await _refreshLock.WaitAsync(0, ct))
            return (true, _fileWatcher.State.WatchedBgsFactions.ToList(), LastStatus);

        try
        {
            var results = new List<EliteBgsFactionWatchItem>();
            foreach (string factionName in factions)
            {
                ct.ThrowIfCancellationRequested();
                string url = $"{EliteBgsFactionsUrl}?name={Uri.EscapeDataString(factionName)}";
                try
                {
                    using var resp = await _httpClient.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode)
                        continue;

                    string json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    results.AddRange(ParseFactionResponse(doc.RootElement));
                }
                catch
                {
                    // Skip transient failure for individual faction query.
                }
            }

            var ordered = results
                .OrderBy(static i => i.FactionName, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(static i => i.InfluencePercent)
                .ToList();

            _fileWatcher.ApplyCapiEnrichment(state => state.WatchedBgsFactions = ordered);

            foreach (var item in ordered.Where(static i => i.HasConflict))
            {
                string key = $"{item.FactionName}|{item.SystemName}|{item.State}|{item.ActiveStates}|{item.PendingStates}";
                if (_notifiedConflicts.Add(key))
                    ConflictDetected?.Invoke(item);
            }

            LastStatus = $"EliteBGS: {ordered.Count} presences ({DateTime.Now:HH:mm:ss})";
            WatchlistUpdated?.Invoke(ordered);
            return (true, ordered, LastStatus);
        }
        catch (Exception ex)
        {
            LastStatus = $"EliteBGS error: {ex.Message}";
            AppDiagnosticsLogger.Warn("EliteBGS", LastStatus);
            return (false, new List<EliteBgsFactionWatchItem>(), LastStatus);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    internal static List<EliteBgsFactionWatchItem> ParseFactionResponse(JsonElement root)
    {
        var list = new List<EliteBgsFactionWatchItem>();
        if (!root.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var factionDoc in docs.EnumerateArray())
        {
            if (factionDoc.ValueKind != JsonValueKind.Object)
                continue;

            string factionName = factionDoc.TryGetProperty("name", out var np) && np.ValueKind == JsonValueKind.String
                ? np.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(factionName))
                continue;

            if (!factionDoc.TryGetProperty("faction_presence", out var presArr) || presArr.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var p in presArr.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object)
                    continue;

                string sysName = p.TryGetProperty("system_name", out var sp) && sp.ValueKind == JsonValueKind.String
                    ? sp.GetString() ?? ""
                    : "";
                if (string.IsNullOrWhiteSpace(sysName))
                    continue;

                double inf = p.TryGetProperty("influence", out var ip) && ip.ValueKind == JsonValueKind.Number
                    ? Math.Round(ip.GetDouble() * 100.0, 1)
                    : 0.0;
                string state = p.TryGetProperty("state", out var stp) && stp.ValueKind == JsonValueKind.String
                    ? stp.GetString() ?? "None"
                    : "None";

                string activeStates = ReadStateArray(p, "active_states");
                string pendingStates = ReadStateArray(p, "pending_states");

                DateTimeOffset updatedAt = DateTimeOffset.UtcNow;
                if (p.TryGetProperty("updated_at", out var up) && up.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(up.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                {
                    updatedAt = dt.ToUniversalTime();
                }

                list.Add(new EliteBgsFactionWatchItem
                {
                    FactionName = factionName,
                    SystemName = sysName,
                    InfluencePercent = inf,
                    State = state,
                    ActiveStates = activeStates,
                    PendingStates = pendingStates,
                    UpdatedAtUtc = updatedAt
                });
            }
        }

        return list;
    }

    private static string ReadStateArray(JsonElement parent, string propName)
    {
        if (!parent.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return "";

        var names = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("state", out var s) &&
                s.ValueKind == JsonValueKind.String)
            {
                string val = s.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(val))
                    names.Add(val);
            }
            else if (item.ValueKind == JsonValueKind.String)
            {
                string val = item.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(val))
                    names.Add(val);
            }
        }

        return string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _loopCts.Cancel(); } catch (ObjectDisposedException) { }
        _timer.Dispose();
        _loopCts.Dispose();
        _httpClient.Dispose();
        _refreshLock.Dispose();
    }
}
