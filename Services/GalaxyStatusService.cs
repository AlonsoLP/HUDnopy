// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/GalaxyStatusService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Polls EDSM Elite Dangerous server status and EliteBGS v5 galaxy
//              tick timestamps for dashboard and BGS display.
// -----------------------------------------------------------------------------

using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace HUDnopy.Services;

/// <summary>
/// Immutable snapshot of Elite Dangerous server status and the latest BGS galaxy tick.
/// </summary>
public sealed record GalaxyStatusSnapshot(
    string ServerMessage,
    string ServerType,
    string ServerLastUpdate,
    DateTimeOffset? LastBgsTickUtc,
    DateTimeOffset LastCheckedUtc)
{
    public static GalaxyStatusSnapshot Empty { get; } = new("", "", "", null, DateTimeOffset.MinValue);
}

/// <summary>
/// Polls EDSM Elite Dangerous server status and EliteBGS v5 galaxy tick timestamps.
/// </summary>
public sealed class GalaxyStatusService : IDisposable
{
    private const string EdsmServerStatusUrl = "https://www.edsm.net/api-status-v1/elite-server";
    private const string EliteBgsTicksUrl = "https://elitebgs.app/api/ebgs/v5/ticks";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;
    private readonly System.Timers.Timer _timer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private GalaxyStatusSnapshot _current = GalaxyStatusSnapshot.Empty;
    private bool _disposed;

    public GalaxyStatusSnapshot Current => _current;

    /// <summary>
    /// Raised whenever server status or BGS tick data is refreshed.
    /// </summary>
    public event Action<GalaxyStatusSnapshot>? StatusUpdated;

    public GalaxyStatusService()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HUDnopy", "0.2"));

        _timer = new System.Timers.Timer(RefreshInterval.TotalMilliseconds) { AutoReset = true };
        _timer.Elapsed += async (_, _) => await RefreshAsync();
        _timer.Start();

        // Trigger initial background fetch shortly after startup.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            if (!_disposed)
                await RefreshAsync();
        });
    }

    /// <summary>
    /// Fetches live Elite Dangerous server status and the latest BGS tick timestamp.
    /// </summary>
    public async Task<GalaxyStatusSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        if (_disposed || !await _refreshLock.WaitAsync(0, ct))
            return _current;

        try
        {
            var serverTask = FetchServerStatusAsync(ct);
            var tickTask = FetchLastBgsTickAsync(ct);
            await Task.WhenAll(serverTask, tickTask);

            var (msg, type, lastUpd) = await serverTask;
            DateTimeOffset? tickUtc = await tickTask;

            _current = new GalaxyStatusSnapshot(
                string.IsNullOrWhiteSpace(msg) ? _current.ServerMessage : msg,
                string.IsNullOrWhiteSpace(type) ? _current.ServerType : type,
                string.IsNullOrWhiteSpace(lastUpd) ? _current.ServerLastUpdate : lastUpd,
                tickUtc ?? _current.LastBgsTickUtc,
                DateTimeOffset.UtcNow);

            StatusUpdated?.Invoke(_current);
            return _current;
        }
        catch
        {
            return _current;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<(string Message, string Type, string LastUpdate)> FetchServerStatusAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _httpClient.GetAsync(EdsmServerStatusUrl, ct);
            if (!resp.IsSuccessStatusCode)
                return ("", "", "");

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ("", "", "");

            string message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? ""
                : "";
            string type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? ""
                : "";
            string lastUpdate = root.TryGetProperty("lastUpdate", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString() ?? ""
                : "";

            return (message, type, lastUpdate);
        }
        catch
        {
            return ("", "", "");
        }
    }

    private async Task<DateTimeOffset?> FetchLastBgsTickAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _httpClient.GetAsync(EliteBgsTicksUrl, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement? firstEntry = null;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                firstEntry = root[0];
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("docs", out var docs) &&
                     docs.ValueKind == JsonValueKind.Array &&
                     docs.GetArrayLength() > 0)
            {
                firstEntry = docs[0];
            }

            if (firstEntry is { ValueKind: JsonValueKind.Object } entry)
            {
                string? rawTime = null;
                if (entry.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.String)
                    rawTime = t.GetString();
                else if (entry.TryGetProperty("updated_at", out var u) && u.ValueKind == JsonValueKind.String)
                    rawTime = u.GetString();

                if (!string.IsNullOrWhiteSpace(rawTime) &&
                    DateTimeOffset.TryParse(rawTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    return parsed.ToUniversalTime();
                }
            }
        }
        catch
        {
            // Ignore transient network failures; previous cached value remains valid.
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        _httpClient.Dispose();
        _refreshLock.Dispose();
    }
}
