// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/Integrations/EdastroCanonnService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Integrates EDAstro Deep Space Support Array (DSSA) Fleet Carrier
//              lookup and opt-in Canonn R&D scientific telemetry uploads.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HUDnopy.Models;
using static HUDnopy.Services.JsonElementExtensions;

namespace HUDnopy.Services.Integrations;

/// <summary>
/// Provides EDAstro DSSA Fleet Carrier discovery (sorted by 3D distance) and opt-in Canonn R&D event uploads.
/// </summary>
public sealed class EdastroCanonnService : IDisposable
{
    private const string EdastroDssaUrl = "https://edastro.com/api/dssa";
    private const string CanonnPostEventUrl = "https://us-central1-canonn-api-236217.cloudfunctions.net/postEvent";
    private static readonly TimeSpan DssaCacheTtl = TimeSpan.FromHours(6);
    private static readonly FrozenSet<string> CanonnSupportedEvents = new[]
    {
        "CodexEntry",
        "ScanOrganic",
        "FSSSignalDiscovered",
        "SAASignalsFound"
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ConfigService _configService;
    private readonly EliteFileWatcherService _fileWatcher;
    private readonly GameEventHub _gameEvents;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _dssaLock = new(1, 1);

    private List<DssaCarrierItem> _cachedDssa = new();
    private DateTimeOffset _dssaFetchedAtUtc = DateTimeOffset.MinValue;

    public int CanonnUploadedCount { get; private set; }
    public string LastStatus { get; private set; } = "";
    public event Action? StatusChanged;

    public EdastroCanonnService(
        ConfigService configService,
        EliteFileWatcherService fileWatcher,
        GameEventHub gameEvents)
    {
        _configService = configService;
        _fileWatcher = fileWatcher;
        _gameEvents = gameEvents;

        _httpClient = HttpClientHeaderDefaults.CreateHttpClient(TimeSpan.FromSeconds(15));

        _gameEvents.JournalEventReceived += OnJournalEventReceived;
    }

    private void OnJournalEventReceived(GameJournalEvent gameEvent)
    {
        if (gameEvent.IsHistorical)
            return;

        if (!_configService.Current.EnableCanonnUpload)
            return;

        if (_fileWatcher.State.IsLegacyGalaxy)
            return;

        if (CanonnSupportedEvents.Contains(gameEvent.EventType))
            _ = UploadCanonnEventAsync(gameEvent, _fileWatcher.State);
    }

    /// <summary>
    /// Finds the nearest operational DSSA Fleet Carriers relative to the commander's 3D StarPos.
    /// </summary>
    public async Task<(bool Success, List<DssaCarrierItem> Carriers, string Message)> GetNearestDssaCarriersAsync(
        double[]? starPos = null,
        int limit = 15,
        CancellationToken ct = default)
    {
        if (!_configService.Current.EnableEdastroDssa)
        {
            LastStatus = "EDAstro DSSA integration is disabled.";
            return (false, new List<DssaCarrierItem>(), LastStatus);
        }

        await EnsureDssaDirectoryLoadedAsync(ct);

        double[] pos = starPos is { Length: 3 }
            ? starPos
            : (_fileWatcher.State.StarPos is { Length: 3 } sp ? sp : [0.0, 0.0, 0.0]);

        var sorted = SortByDistance(_cachedDssa, pos[0], pos[1], pos[2], Math.Clamp(limit, 1, 50));
        _fileWatcher.ApplyCapiEnrichment(state => state.NearestDssaCarriers = sorted);
        LastStatus = $"DSSA: {sorted.Count} nearest carriers";
        StatusChanged?.Invoke();
        return (sorted.Count > 0, sorted, LastStatus);
    }

    private async Task EnsureDssaDirectoryLoadedAsync(CancellationToken ct)
    {
        if (_cachedDssa.Count > 0 && DateTimeOffset.UtcNow - _dssaFetchedAtUtc < DssaCacheTtl)
            return;

        await _dssaLock.WaitAsync(ct);
        try
        {
            if (_cachedDssa.Count > 0 && DateTimeOffset.UtcNow - _dssaFetchedAtUtc < DssaCacheTtl)
                return;

            using var resp = await _httpClient.GetAsync(EdastroDssaUrl, ct);
            if (!resp.IsSuccessStatusCode)
                return;

            string json = await resp.Content.ReadAsStringAsync(ct);
            var parsed = ParseDssaJson(json);
            if (parsed.Count > 0)
            {
                _cachedDssa = parsed;
                _dssaFetchedAtUtc = DateTimeOffset.UtcNow;
            }
        }
        catch
        {
            // Keep existing cache if transient network error occurs.
        }
        finally
        {
            _dssaLock.Release();
        }
    }

    internal static List<DssaCarrierItem> SortByDistance(
        IEnumerable<DssaCarrierItem> carriers,
        double refX,
        double refY,
        double refZ,
        int limit = 15)
    {
        return carriers
            .Select(c =>
            {
                double dx = c.X - refX;
                double dy = c.Y - refY;
                double dz = c.Z - refZ;
                double dist = Math.Round(Math.Sqrt(dx * dx + dy * dy + dz * dz), 1);
                return c with { DistanceLy = dist };
            })
            .OrderBy(static c => c.DistanceLy)
            .Take(limit)
            .ToList();
    }

    internal static List<DssaCarrierItem> ParseDssaJson(string json)
    {
        var list = new List<DssaCarrierItem>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement arr = doc.RootElement;
            if (arr.ValueKind == JsonValueKind.Object && arr.TryGetProperty("carriers", out var inner))
                arr = inner;
            if (arr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                string callsign = GetStr(item, "callsign") ?? GetStr(item, "id") ?? "";
                string name = GetStr(item, "name") ?? callsign;
                string system = GetStr(item, "system") ?? GetStr(item, "starSystem") ?? "";
                string region = GetStr(item, "region") ?? "";
                string status = GetStr(item, "status") ?? "Operational";

                if (string.IsNullOrWhiteSpace(system))
                    continue;

                double x = 0, y = 0, z = 0;
                if (item.TryGetProperty("coords", out var coords) && coords.ValueKind == JsonValueKind.Object)
                {
                    x = GetDouble(coords, "x") ?? 0;
                    y = GetDouble(coords, "y") ?? 0;
                    z = GetDouble(coords, "z") ?? 0;
                }
                else
                {
                    x = GetDouble(item, "x") ?? 0;
                    y = GetDouble(item, "y") ?? 0;
                    z = GetDouble(item, "z") ?? 0;
                }

                list.Add(new DssaCarrierItem(callsign, name, system, region, status, 0, x, y, z));
            }
        }
        catch
        {
            // Return whatever parsed cleanly.
        }

        return list;
    }

    private async Task UploadCanonnEventAsync(GameJournalEvent gameEvent, ShipState state)
    {
        try
        {
            string cmdr = HttpClientHeaderDefaults.CleanCommanderName(state.CommanderName);
            if (string.IsNullOrWhiteSpace(cmdr))
                return;

            var payload = new
            {
                gameState = new
                {
                    systemName = state.CurrentSystem,
                    systemAddress = state.SystemAddress,
                    systemCoordinates = state.StarPos ?? [state.StarPosX, state.StarPosY, state.StarPosZ],
                    bodyName = state.BodyName,
                    clientVersion = $"{HttpClientHeaderDefaults.ProductName} {HttpClientHeaderDefaults.ProductVersion}",
                    isBeta = false,
                    isOdyssey = state.IsOdyssey
                },
                rawEvent = gameEvent.Data,
                cmdrName = cmdr
            };

            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _httpClient.PostAsync(CanonnPostEventUrl, content);
            if (resp.IsSuccessStatusCode)
            {
                CanonnUploadedCount++;
                LastStatus = $"Canonn: uploaded {gameEvent.EventType} (#{CanonnUploadedCount})";
                StatusChanged?.Invoke();
            }
        }
        catch
        {
            // Ignore transient Canonn API failures.
        }
    }

    public void Dispose()
    {
        _gameEvents.JournalEventReceived -= OnJournalEventReceived;
        _httpClient.Dispose();
        _dssaLock.Dispose();
    }
}
