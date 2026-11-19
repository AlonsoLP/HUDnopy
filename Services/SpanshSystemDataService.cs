// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/SpanshSystemDataService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Spansh API client for system intel, route plotting, trade, mining rings, and station search.
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using HUDnopy.Models;

namespace HUDnopy.Services;

/// <summary>
/// Spansh API client for system intel, route plotting, trade, mining rings, and station search.
/// </summary>
public sealed class SpanshSystemDataService : IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);
    private const int MaxDisplayedRecords = 100;
    private static readonly JsonSerializerOptions ApiJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConfigService _configService;
    private readonly EliteFileWatcherService _fileWatcher;
    private readonly HttpClient _httpClient;
    private readonly string _cachePath;
    private readonly object _sync = new();
    private CancellationTokenSource? _activeRequest;
    private ulong? _activeId64;
    private DateTimeOffset _retryAfterUtc;
    private SpanshSystemDataState _state = new(
        SpanshSystemDataStatus.Disabled, null, false);
    private bool _disposed;

    public event Action<SpanshSystemDataState>? StateChanged;

    public SpanshSystemDataState State
    {
        get
        {
            lock (_sync)
                return _state;
        }
    }

    public SpanshSystemDataService(ConfigService configService, EliteFileWatcherService fileWatcher)
    {
        _configService = configService;
        _fileWatcher = fileWatcher;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HUDnopy", "0.1"));
        string cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HUDnopy",
            "Spansh");
        _cachePath = Path.Combine(cacheDirectory, "current-system.json");

        _configService.ConfigChanged += OnConfigChanged;
        _fileWatcher.StateUpdated += OnShipStateUpdated;
        Evaluate(_fileWatcher.State);
    }

    private void OnConfigChanged(AppConfig config) => Evaluate(_fileWatcher.State);

    private void OnShipStateUpdated(ShipState state) => Evaluate(state);

    private void Evaluate(ShipState shipState)
    {
        CancellationTokenSource? requestToCancel = null;
        CancellationTokenSource? requestToStart = null;
        SpanshSystemDataState? changedState = null;
        ulong id64 = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            if (_disposed)
                return;

            if (!_configService.Current.EnableSpanshSystemData)
            {
                requestToCancel = _activeRequest;
                _activeRequest = null;
                _activeId64 = null;
                _retryAfterUtc = default;
                if (_state.Status != SpanshSystemDataStatus.Disabled || _state.Snapshot is not null)
                    changedState = new(SpanshSystemDataStatus.Disabled, null, false);
            }
            else if (!ulong.TryParse(shipState.SystemAddress, NumberStyles.None, CultureInfo.InvariantCulture, out id64))
            {
                requestToCancel = _activeRequest;
                _activeRequest = null;
                _activeId64 = null;
                _retryAfterUtc = default;
                if (_state.Status != SpanshSystemDataStatus.WaitingForSystem || _state.Snapshot is not null)
                    changedState = new(SpanshSystemDataStatus.WaitingForSystem, null, false);
            }
            else
            {
                bool systemChanged = _activeId64 != id64;
                bool requestInProgress = _activeRequest is not null;
                bool cacheIsFresh = _state.Snapshot is { } current &&
                    current.Id64 == id64 &&
                    now - current.FetchedAtUtc < CacheLifetime &&
                    !_state.IsStale;

                if (!systemChanged &&
                    (requestInProgress || cacheIsFresh || now < _retryAfterUtc))
                    return;

                requestToCancel = _activeRequest;
                _activeId64 = id64;
                _retryAfterUtc = default;
                SpanshSystemSnapshot? cached = _state.Snapshot is { } existing && existing.Id64 == id64
                    ? existing
                    : null;
                bool isStale = cached is not null && now - cached.FetchedAtUtc >= CacheLifetime;
                requestToStart = new CancellationTokenSource();
                _activeRequest = requestToStart;
                changedState = new(
                    SpanshSystemDataStatus.Loading,
                    cached,
                    isStale);
            }

            if (changedState is not null)
                _state = changedState;
        }

        requestToCancel?.Cancel();
        if (changedState is not null)
            StateChanged?.Invoke(changedState);
        if (requestToStart is not null)
            _ = LoadSystemAsync(id64, requestToStart);
    }

    private async Task LoadSystemAsync(ulong id64, CancellationTokenSource request)
    {
        CancellationToken cancellationToken = request.Token;
        try
        {
            SpanshSystemSnapshot? cached = await ReadCacheAsync(id64, cancellationToken);
            if (cached is not null)
            {
                bool isStale = DateTimeOffset.UtcNow - cached.FetchedAtUtc >= CacheLifetime;
                if (!isStale)
                {
                    PublishIfCurrent(id64, request, new(
                        SpanshSystemDataStatus.Ready, cached, false));
                    return;
                }

                PublishIfCurrent(id64, request, new(
                    SpanshSystemDataStatus.Loading, cached, true));
            }

            using var response = await _httpClient.GetAsync(
                $"https://spansh.co.uk/api/dump/{id64}",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var dump = await JsonSerializer.DeserializeAsync<SpanshApiDump>(
                stream, ApiJsonOptions, cancellationToken);
            if (dump?.System is null ||
                dump.System.Id64.HasValue && dump.System.Id64.Value != id64)
                throw new InvalidDataException("Spansh returned a system dump with a mismatched ID64.");

            SpanshSystemSnapshot snapshot = CreateSnapshot(dump.System, id64);
            await WriteCacheAsync(snapshot, cancellationToken);
            PublishIfCurrent(id64, request, new(
                SpanshSystemDataStatus.Ready, snapshot, false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or
                                   JsonException or IOException or InvalidDataException)
        {
            Debug.WriteLine($"Spansh system lookup failed for {id64}: {ex.Message}");
            SpanshSystemDataState current = State;
            SpanshSystemDataState failedState = current.Snapshot is { } snapshot && snapshot.Id64 == id64
                ? new(SpanshSystemDataStatus.Ready, snapshot, true)
                : new(SpanshSystemDataStatus.Error, null, false);
            PublishIfCurrent(id64, request, failedState);
            lock (_sync)
            {
                if (ReferenceEquals(_activeRequest, request))
                    _retryAfterUtc = DateTimeOffset.UtcNow + RetryDelay;
            }
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeRequest, request))
                    _activeRequest = null;
            }
            request.Dispose();
        }
    }

    private bool PublishIfCurrent(
        ulong id64,
        CancellationTokenSource request,
        SpanshSystemDataState state)
    {
        lock (_sync)
        {
            if (_disposed ||
                !_configService.Current.EnableSpanshSystemData ||
                _activeId64 != id64 ||
                !ReferenceEquals(_activeRequest, request))
                return false;
            _state = state;
        }

        StateChanged?.Invoke(state);
        return true;
    }

    private async Task<SpanshSystemSnapshot?> ReadCacheAsync(ulong id64, CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath))
            return null;

        try
        {
            await using var stream = new FileStream(
                _cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            SpanshSystemSnapshot? snapshot = await JsonSerializer.DeserializeAsync<SpanshSystemSnapshot>(
                stream, CacheJsonOptions, cancellationToken);
            return snapshot?.Id64 == id64 ? snapshot : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Spansh cache could not be read: {ex.Message}");
            return null;
        }
    }

    private async Task WriteCacheAsync(SpanshSystemSnapshot snapshot, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_cachePath);
        if (directory is null)
            return;

        string temporaryPath = _cachePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(
                temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, CacheJsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Spansh cache could not be written: {ex.Message}");
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Temporary Spansh cache could not be removed: {ex.Message}");
                }
            }
        }
    }

    private static SpanshSystemSnapshot CreateSnapshot(SpanshApiSystem source, ulong requestedId64)
    {
        List<SpanshApiBody> sourceBodies = source.Bodies ?? new();
        int stationCount = sourceBodies.Sum(body => body.Stations?.Count ?? 0);

        return new SpanshSystemSnapshot
        {
            Id64 = source.Id64 ?? requestedId64,
            Name = source.Name ?? "",
            BodyCount = source.BodyCount ?? sourceBodies.Count,
            StationCount = stationCount,
            FetchedAtUtc = DateTimeOffset.UtcNow,
            Bodies = sourceBodies
                .OrderBy(body => body.DistanceToArrival ?? double.MaxValue)
                .ThenBy(body => body.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxDisplayedRecords)
                .Select(body => new SpanshBodySummary(
                    body.Name ?? "",
                    body.SubType ?? body.Type ?? "",
                    body.DistanceToArrival,
                    body.IsLandable))
                .ToList(),
            Stations = sourceBodies
                .SelectMany(body => (body.Stations ?? new()).Select(station => (Body: body.Name ?? "", Station: station)))
                .OrderBy(item => item.Station.DistanceToArrival ?? double.MaxValue)
                .ThenBy(item => item.Station.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxDisplayedRecords)
                .Select(item => new SpanshStationSummary(
                    item.Station.Name ?? "",
                    item.Station.Type ?? "",
                    item.Body,
                    item.Station.DistanceToArrival,
                    item.Station.ControllingFaction ?? ""))
                .ToList()
        };
    }

    public void Dispose()
    {
        CancellationTokenSource? request;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            request = _activeRequest;
            _activeRequest = null;
        }

        _configService.ConfigChanged -= OnConfigChanged;
        _fileWatcher.StateUpdated -= OnShipStateUpdated;
        request?.Cancel();
        _httpClient.Dispose();
    }

    private sealed class SpanshApiDump
    {
        [JsonPropertyName("system")]
        public SpanshApiSystem? System { get; set; }
    }

    private sealed class SpanshApiSystem
    {
        [JsonPropertyName("id64")]
        public ulong? Id64 { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("bodyCount")]
        public int? BodyCount { get; set; }
        [JsonPropertyName("bodies")]
        public List<SpanshApiBody>? Bodies { get; set; }
    }

    private sealed class SpanshApiBody
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("type")]
        public string? Type { get; set; }
        [JsonPropertyName("subType")]
        public string? SubType { get; set; }
        [JsonPropertyName("distanceToArrival")]
        public double? DistanceToArrival { get; set; }
        [JsonPropertyName("isLandable")]
        public bool? IsLandable { get; set; }
        [JsonPropertyName("stations")]
        public List<SpanshApiStation>? Stations { get; set; }
    }

    private sealed class SpanshApiStation
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("type")]
        public string? Type { get; set; }
        [JsonPropertyName("distanceToArrival")]
        public double? DistanceToArrival { get; set; }
        [JsonPropertyName("controllingFaction")]
        public string? ControllingFaction { get; set; }
    }

    public async Task<(bool Success, List<SpanshServiceStationItem> Results, string Message)> SearchNearestServicesAsync(
        string serviceKey,
        string referenceSystem,
        double[]? referenceCoords)
    {
        var filters = new Dictionary<string, object>();
        switch (serviceKey)
        {
            case "material_trader":
                filters["services"] = new { value = new[] { "Material Trader" } };
                break;
            case "tech_broker":
                filters["services"] = new { value = new[] { "Technology Broker" } };
                break;
            case "interstellar_factors":
                filters["services"] = new { value = new[] { "Interstellar Factors Contact" } };
                break;
            case "tritium":
                filters["market"] = new object[]
                {
                    new { name = "Tritium", supply = new { comparison = "<=>", value = new[] { 100, 10000000 } } }
                };
                break;
            default:
                return (false, new List<SpanshServiceStationItem>(), "Servicio desconocido");
        }

        var payload = new Dictionary<string, object>
        {
            ["filters"] = filters,
            ["sort"] = new object[] { new { distance = new { direction = "asc" } } },
            ["size"] = 8,
            ["page"] = 0
        };

        if (referenceCoords is { Length: 3 })
            payload["reference_coords"] = new { x = referenceCoords[0], y = referenceCoords[1], z = referenceCoords[2] };
        else if (!string.IsNullOrWhiteSpace(referenceSystem))
            payload["reference_system"] = referenceSystem;
        else
            payload["reference_system"] = "Sol";

        try
        {
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var resp = await _httpClient.PostAsync("https://spansh.co.uk/api/stations/search", content);
            if (!resp.IsSuccessStatusCode)
                return (false, new List<SpanshServiceStationItem>(), $"Spansh HTTP {(int)resp.StatusCode}");

            string body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var results = new List<SpanshServiceStationItem>();
            if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    string sys = item.TryGetProperty("system_name", out var sp) ? (sp.GetString() ?? "") : "";
                    string sta = item.TryGetProperty("name", out var np) ? (np.GetString() ?? "") : "";
                    double distLy = item.TryGetProperty("distance", out var dp) && dp.ValueKind == JsonValueKind.Number
                        ? Math.Round(dp.GetDouble(), 1) : 0;
                    double distLs = item.TryGetProperty("distance_to_arrival", out var lsP) && lsP.ValueKind == JsonValueKind.Number
                        ? Math.Round(lsP.GetDouble(), 0) : 0;
                    bool hasLarge = item.TryGetProperty("has_large_pad", out var lp) && lp.ValueKind == JsonValueKind.True;
                    string pad = hasLarge ? "L" : "M/S";
                    string type = item.TryGetProperty("type", out var tp) ? (tp.GetString() ?? "") : "";
                    if (!string.IsNullOrWhiteSpace(sys) && !string.IsNullOrWhiteSpace(sta))
                    {
                        results.Add(new SpanshServiceStationItem(
                            sys,
                            sta,
                            $"{distLy:0.0} AL",
                            pad,
                            string.IsNullOrWhiteSpace(type) ? $"{distLs:N0} LS" : $"{type} · {distLs:N0} LS"));
                    }
                }
            }

            return (true, results, $"Spansh: {results.Count}");
        }
        catch (Exception ex)
        {
            return (false, new List<SpanshServiceStationItem>(), $"Spansh: {ex.Message}");
        }
    }

    public async Task<(bool Success, List<SpanshServiceStationItem> Results, string Message)> SearchCommoditySupplierAsync(
        string commodityName,
        long minSupply,
        bool requireLargePad,
        string referenceSystem,
        double[]? referenceCoords)
    {
        if (string.IsNullOrWhiteSpace(commodityName))
            return (false, new List<SpanshServiceStationItem>(), "Mercancía vacía");

        var filters = new Dictionary<string, object>
        {
            ["market"] = new object[]
            {
                new { name = commodityName, supply = new { comparison = "<=>", value = new[] { Math.Max(1, minSupply), 10000000 } } }
            }
        };
        if (requireLargePad)
            filters["has_large_pad"] = new { value = true };

        return await ExecuteStationSearchAsync(filters, referenceSystem, referenceCoords);
    }

    public async Task<(bool Success, List<SpanshServiceStationItem> Results, string Message)> SearchShipOrModuleAsync(
        string query,
        bool isShip,
        bool liYongRuiDiscount,
        string referenceSystem,
        double[]? referenceCoords)
    {
        if (string.IsNullOrWhiteSpace(query))
            return (false, new List<SpanshServiceStationItem>(), "Introduce un nombre de nave o módulo");

        var filters = new Dictionary<string, object>();
        if (isShip)
            filters["ships"] = new object[] { new { name = new[] { query.Trim() } } };
        else
            filters["modules"] = new object[] { new { name = new[] { query.Trim() } } };

        if (liYongRuiDiscount)
            filters["controlling_power"] = new { value = new[] { "Li Yong-Rui" } };

        return await ExecuteStationSearchAsync(filters, referenceSystem, referenceCoords);
    }

    private async Task<(bool Success, List<SpanshServiceStationItem> Results, string Message)> ExecuteStationSearchAsync(
        Dictionary<string, object> filters,
        string referenceSystem,
        double[]? referenceCoords)
    {
        var payload = new Dictionary<string, object>
        {
            ["filters"] = filters,
            ["sort"] = new object[] { new { distance = new { direction = "asc" } } },
            ["size"] = 8,
            ["page"] = 0
        };

        if (referenceCoords is { Length: 3 })
            payload["reference_coords"] = new { x = referenceCoords[0], y = referenceCoords[1], z = referenceCoords[2] };
        else if (!string.IsNullOrWhiteSpace(referenceSystem))
            payload["reference_system"] = referenceSystem;
        else
            payload["reference_system"] = "Sol";

        try
        {
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var resp = await _httpClient.PostAsync("https://spansh.co.uk/api/stations/search", content);
            if (!resp.IsSuccessStatusCode)
                return (false, new List<SpanshServiceStationItem>(), $"Spansh HTTP {(int)resp.StatusCode}");

            string body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var results = new List<SpanshServiceStationItem>();
            if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    string sys = item.TryGetProperty("system_name", out var sp) ? (sp.GetString() ?? "") : "";
                    string sta = item.TryGetProperty("name", out var np) ? (np.GetString() ?? "") : "";
                    double distLy = item.TryGetProperty("distance", out var dp) && dp.ValueKind == JsonValueKind.Number
                        ? Math.Round(dp.GetDouble(), 1) : 0;
                    double distLs = item.TryGetProperty("distance_to_arrival", out var lsP) && lsP.ValueKind == JsonValueKind.Number
                        ? Math.Round(lsP.GetDouble(), 0) : 0;
                    bool hasLarge = item.TryGetProperty("has_large_pad", out var lp) && lp.ValueKind == JsonValueKind.True;
                    string pad = hasLarge ? "L" : "M/S";
                    string type = item.TryGetProperty("type", out var tp) ? (tp.GetString() ?? "") : "";
                    if (!string.IsNullOrWhiteSpace(sys) && !string.IsNullOrWhiteSpace(sta))
                    {
                        results.Add(new SpanshServiceStationItem(
                            sys,
                            sta,
                            $"{distLy:0.0} AL",
                            pad,
                            string.IsNullOrWhiteSpace(type) ? $"{distLs:N0} LS" : $"{type} · {distLs:N0} LS"));
                    }
                }
            }

            return (true, results, $"Spansh: {results.Count}");
        }
        catch (Exception ex)
        {
            return (false, new List<SpanshServiceStationItem>(), $"Spansh: {ex.Message}");
        }
    }

    public async Task<(bool Success, List<SpanshMiningRingItem> Results, string Message)> SearchMiningRingsAsync(
        string mineralName,
        string referenceSystem,
        double[]? referenceCoords)
    {
        var filters = new Dictionary<string, object>
        {
            ["reserve_level"] = new { value = new[] { "Pristine" } },
            ["rings"] = new object[]
            {
                new
                {
                    signals = new object[]
                    {
                        new { name = new[] { mineralName }, count = new { comparison = "<=>", value = new[] { 1, 20 } } }
                    }
                }
            }
        };

        var payload = new Dictionary<string, object>
        {
            ["filters"] = filters,
            ["sort"] = new object[] { new { distance = new { direction = "asc" } } },
            ["size"] = 8,
            ["page"] = 0
        };

        if (referenceCoords is { Length: 3 })
            payload["reference_coords"] = new { x = referenceCoords[0], y = referenceCoords[1], z = referenceCoords[2] };
        else if (!string.IsNullOrWhiteSpace(referenceSystem))
            payload["reference_system"] = referenceSystem;
        else
            payload["reference_system"] = "Sol";

        try
        {
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var resp = await _httpClient.PostAsync("https://spansh.co.uk/api/bodies/search", content);
            if (!resp.IsSuccessStatusCode)
                return (false, new List<SpanshMiningRingItem>(), $"Spansh HTTP {(int)resp.StatusCode}");

            string body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var list = new List<SpanshMiningRingItem>();
            if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    string sys = item.TryGetProperty("system_name", out var sp) ? (sp.GetString() ?? "") : "";
                    string bName = item.TryGetProperty("name", out var np) ? (np.GetString() ?? "") : "";
                    double distLy = item.TryGetProperty("distance", out var dp) && dp.ValueKind == JsonValueKind.Number
                        ? Math.Round(dp.GetDouble(), 1) : 0;

                    string ringName = bName;
                    string ringType = "";
                    string signalsText = mineralName;
                    if (item.TryGetProperty("rings", out var rings) && rings.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in rings.EnumerateArray())
                        {
                            if (r.TryGetProperty("signals", out var sigs) && sigs.ValueKind == JsonValueKind.Array)
                            {
                                var matched = new List<string>();
                                foreach (var s in sigs.EnumerateArray())
                                {
                                    string sn = s.TryGetProperty("name", out var snp) ? (snp.GetString() ?? "") : "";
                                    int cnt = s.TryGetProperty("count", out var cp) && cp.ValueKind == JsonValueKind.Number ? cp.GetInt32() : 0;
                                    if (sn.Equals(mineralName, StringComparison.OrdinalIgnoreCase) && cnt > 0)
                                        matched.Add($"{sn} ×{cnt}");
                                }
                                if (matched.Count > 0)
                                {
                                    ringName = r.TryGetProperty("name", out var rnp) ? (rnp.GetString() ?? bName) : bName;
                                    ringType = r.TryGetProperty("type", out var rtp) ? (rtp.GetString() ?? "") : "";
                                    signalsText = string.Join(", ", matched);
                                    break;
                                }
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(sys))
                        list.Add(new SpanshMiningRingItem(sys, bName, ringName, ringType, $"{distLy:0.0} AL", signalsText));
                }
            }
            return (true, list, $"Spansh: {list.Count}");
        }
        catch (Exception ex)
        {
            return (false, new List<SpanshMiningRingItem>(), $"Spansh: {ex.Message}");
        }
    }

    public async Task<(bool Success, List<SpanshRouteWaypoint> Waypoints, string Message)> PlotSpanshRouteAsync(
        string mode,
        string fromSystem,
        string toSystem,
        double jumpRange,
        long carrierUsedMass = 0)
    {
        if (string.IsNullOrWhiteSpace(fromSystem))
            return (false, new List<SpanshRouteWaypoint>(), "Sistema de origen desconocido");

        string endpoint;
        var form = new List<KeyValuePair<string, string>>
        {
            new("from", fromSystem.Trim())
        };

        switch (mode)
        {
            case "neutron":
                if (string.IsNullOrWhiteSpace(toSystem))
                    return (false, new List<SpanshRouteWaypoint>(), "Introduce sistema de destino");
                endpoint = "https://spansh.co.uk/api/route";
                form.Add(new("to", toSystem.Trim()));
                form.Add(new("range", Math.Clamp(jumpRange, 10, 120).ToString("0.0", CultureInfo.InvariantCulture)));
                form.Add(new("efficiency", "60"));
                break;

            case "riches":
                endpoint = "https://spansh.co.uk/api/riches/route";
                if (!string.IsNullOrWhiteSpace(toSystem))
                    form.Add(new("to", toSystem.Trim()));
                form.Add(new("range", Math.Clamp(jumpRange, 10, 120).ToString("0.0", CultureInfo.InvariantCulture)));
                form.Add(new("radius", "30"));
                form.Add(new("max_results", "20"));
                form.Add(new("min_value", "300000"));
                form.Add(new("max_distance", "5000"));
                form.Add(new("loop", string.IsNullOrWhiteSpace(toSystem) ? "1" : "0"));
                break;

            case "exomastery":
                endpoint = "https://spansh.co.uk/api/exomastery/route";
                if (!string.IsNullOrWhiteSpace(toSystem))
                    form.Add(new("to", toSystem.Trim()));
                form.Add(new("range", Math.Clamp(jumpRange, 10, 120).ToString("0.0", CultureInfo.InvariantCulture)));
                form.Add(new("radius", "30"));
                form.Add(new("max_results", "20"));
                form.Add(new("min_value", "1000000"));
                form.Add(new("max_distance", "5000"));
                form.Add(new("loop", string.IsNullOrWhiteSpace(toSystem) ? "1" : "0"));
                break;

            case "carrier":
                if (string.IsNullOrWhiteSpace(toSystem))
                    return (false, new List<SpanshRouteWaypoint>(), "Introduce sistema de destino");
                endpoint = "https://spansh.co.uk/api/fleetcarrier/route";
                form.Add(new("source", fromSystem.Trim()));
                form.Add(new("destinations", toSystem.Trim()));
                form.Add(new("capacity_used", Math.Max(0, carrierUsedMass).ToString(CultureInfo.InvariantCulture)));
                break;

            default:
                return (false, new List<SpanshRouteWaypoint>(), "Modo de ruta desconocido");
        }

        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var postResp = await _httpClient.PostAsync(endpoint, content);
            if (!postResp.IsSuccessStatusCode)
                return (false, new List<SpanshRouteWaypoint>(), $"Spansh HTTP {(int)postResp.StatusCode}");

            using var postDoc = JsonDocument.Parse(await postResp.Content.ReadAsStringAsync());
            string? jobId = postDoc.RootElement.TryGetProperty("job", out var jp) ? jp.GetString() : null;
            if (string.IsNullOrWhiteSpace(jobId))
                return (false, new List<SpanshRouteWaypoint>(), "Spansh no devolvió job ID");

            JsonDocument? resultDoc = await PollSpanshJobAsync(jobId);
            if (resultDoc is null)
                return (false, new List<SpanshRouteWaypoint>(), "Tiempo de espera agotado en Spansh");

            using (resultDoc)
            {
                var waypoints = ParseSpanshRouteResult(mode, resultDoc.RootElement);
                return (true, waypoints, $"Spansh: {waypoints.Count} paradas");
            }
        }
        catch (Exception ex)
        {
            return (false, new List<SpanshRouteWaypoint>(), $"Spansh: {ex.Message}");
        }
    }

    public async Task<(bool Success, List<SpanshTradeHopItem> Hops, string Message)> PlotTradeRouteAsync(
        string fromSystem,
        string fromStation,
        int cargoCapacity,
        long maxBudget,
        double jumpRange,
        bool requireLargePad)
    {
        if (string.IsNullOrWhiteSpace(fromSystem))
            return (false, new List<SpanshTradeHopItem>(), "Sistema actual desconocido");

        var form = new List<KeyValuePair<string, string>>
        {
            new("system", fromSystem.Trim()),
            new("max_hops", "4"),
            new("max_hop_distance", Math.Clamp(jumpRange, 15, 80).ToString("0.0", CultureInfo.InvariantCulture)),
            new("max_cargo", Math.Clamp(cargoCapacity > 0 ? cargoCapacity : 100, 1, 794).ToString(CultureInfo.InvariantCulture)),
            new("max_price", Math.Clamp(maxBudget > 0 ? maxBudget : 10_000_000, 10_000, 500_000_000).ToString(CultureInfo.InvariantCulture)),
            new("requires_large_pad", requireLargePad ? "1" : "0"),
            new("allow_planetary", "0"),
            new("allow_player_owned", "0"),
            new("unique", "0")
        };
        if (!string.IsNullOrWhiteSpace(fromStation))
            form.Add(new("station", fromStation.Trim()));

        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var postResp = await _httpClient.PostAsync("https://spansh.co.uk/api/trade/route", content);
            if (!postResp.IsSuccessStatusCode)
                return (false, new List<SpanshTradeHopItem>(), $"Spansh HTTP {(int)postResp.StatusCode}");

            using var postDoc = JsonDocument.Parse(await postResp.Content.ReadAsStringAsync());
            string? jobId = postDoc.RootElement.TryGetProperty("job", out var jp) ? jp.GetString() : null;
            if (string.IsNullOrWhiteSpace(jobId))
                return (false, new List<SpanshTradeHopItem>(), "Spansh no devolvió job ID");

            JsonDocument? resultDoc = await PollSpanshJobAsync(jobId);
            if (resultDoc is null)
                return (false, new List<SpanshTradeHopItem>(), "Tiempo de espera agotado en Spansh");

            using (resultDoc)
            {
                var hops = new List<SpanshTradeHopItem>();
                if (resultDoc.RootElement.TryGetProperty("result", out var resArr) && resArr.ValueKind == JsonValueKind.Array)
                {
                    int idx = 1;
                    foreach (var hop in resArr.EnumerateArray())
                    {
                        string srcSys = hop.TryGetProperty("source", out var sObj) && sObj.TryGetProperty("system", out var ss) ? (ss.GetString() ?? "") : "";
                        string srcSta = hop.TryGetProperty("source", out var sObj2) && sObj2.TryGetProperty("station", out var sst) ? (sst.GetString() ?? "") : "";
                        string dstSys = hop.TryGetProperty("destination", out var dObj) && dObj.TryGetProperty("system", out var ds) ? (ds.GetString() ?? "") : "";
                        string dstSta = hop.TryGetProperty("destination", out var dObj2) && dObj2.TryGetProperty("station", out var dst) ? (dst.GetString() ?? "") : "";
                        double dist = hop.TryGetProperty("distance", out var dp) && dp.ValueKind == JsonValueKind.Number ? Math.Round(dp.GetDouble(), 1) : 0;
                        long profit = hop.TryGetProperty("total_profit", out var pp) && pp.ValueKind == JsonValueKind.Number ? pp.GetInt64() : 0;

                        string commSummary = "";
                        if (hop.TryGetProperty("commodities", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
                        {
                            commSummary = string.Join(", ", cArr.EnumerateArray().Select(c =>
                            {
                                string cn = c.TryGetProperty("name", out var np) ? (np.GetString() ?? "") : "";
                                int amt = c.TryGetProperty("amount", out var ap) && ap.ValueKind == JsonValueKind.Number ? ap.GetInt32() : 0;
                                return amt > 0 ? $"{cn} ×{amt}" : cn;
                            }).Where(static x => !string.IsNullOrWhiteSpace(x)));
                        }

                        hops.Add(new SpanshTradeHopItem(idx++, srcSys, srcSta, dstSys, dstSta, commSummary, $"{profit:N0} CR", $"{dist:0.0} AL"));
                    }
                }
                return (true, hops, $"Spansh: {hops.Count} tramos");
            }
        }
        catch (Exception ex)
        {
            return (false, new List<SpanshTradeHopItem>(), $"Spansh: {ex.Message}");
        }
    }

    private async Task<JsonDocument?> PollSpanshJobAsync(string jobId)
    {
        string url = $"https://spansh.co.uk/api/results/{Uri.EscapeDataString(jobId)}";
        for (int attempt = 0; attempt < 25; attempt++)
        {
            await Task.Delay(1200);
            using var resp = await _httpClient.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
                continue;
            string body = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(body);
            string status = doc.RootElement.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";
            if (status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                return doc;
            doc.Dispose();
            if (status.Equals("error", StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return null;
    }

    private static List<SpanshRouteWaypoint> ParseSpanshRouteResult(string mode, JsonElement root)
    {
        var list = new List<SpanshRouteWaypoint>();
        if (!root.TryGetProperty("result", out var res))
            return list;

        JsonElement jumpsArr;
        if (res.ValueKind == JsonValueKind.Array)
            jumpsArr = res;
        else if (res.TryGetProperty("system_jumps", out var sj) && sj.ValueKind == JsonValueKind.Array)
            jumpsArr = sj;
        else if (res.TryGetProperty("jumps", out var j) && j.ValueKind == JsonValueKind.Array)
            jumpsArr = j;
        else
            return list;

        int idx = 1;
        foreach (var item in jumpsArr.EnumerateArray())
        {
            string sys = item.TryGetProperty("system", out var sp) ? (sp.GetString() ?? "")
                : item.TryGetProperty("name", out var np) ? (np.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(sys))
                continue;

            double dist = item.TryGetProperty("distance_jumped", out var dj) && dj.ValueKind == JsonValueKind.Number ? dj.GetDouble()
                : item.TryGetProperty("distance", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
            bool isNeutron = item.TryGetProperty("neutron_star", out var nsp) && nsp.ValueKind == JsonValueKind.True;

            var detailParts = new List<string>();
            if (isNeutron) detailParts.Add("⚡ Neutrones");
            if (item.TryGetProperty("fuel_used", out var fu) && fu.ValueKind == JsonValueKind.Number)
                detailParts.Add($"Tritio: {fu.GetInt32()} t");
            if (item.TryGetProperty("bodies", out var bArr) && bArr.ValueKind == JsonValueKind.Array && bArr.GetArrayLength() > 0)
            {
                var bodies = bArr.EnumerateArray()
                    .Select(b => b.TryGetProperty("name", out var bn) ? (bn.GetString() ?? "") : "")
                    .Where(static n => !string.IsNullOrWhiteSpace(n))
                    .Take(3);
                detailParts.Add(string.Join(", ", bodies));
            }

            list.Add(new SpanshRouteWaypoint(idx++, sys, dist > 0 ? $"{dist:0.0} AL" : "—", string.Join(" · ", detailParts), isNeutron, false));
        }

        return list;
    }

    public static List<SpanshRouteWaypoint> ParseSpanshCsvRoute(string filePath)
    {
        var list = new List<SpanshRouteWaypoint>();
        if (!File.Exists(filePath))
            return list;

        string[] lines = File.ReadAllLines(filePath);
        if (lines.Length < 2)
            return list;

        static string[] SplitCsv(string line)
        {
            var res = new List<string>();
            var sb = new System.Text.StringBuilder();
            bool inQuotes = false;
            foreach (char c in line)
            {
                if (c == '"') { inQuotes = !inQuotes; continue; }
                if (c == ',' && !inQuotes) { res.Add(sb.ToString().Trim()); sb.Clear(); continue; }
                sb.Append(c);
            }
            res.Add(sb.ToString().Trim());
            return res.ToArray();
        }

        string[] headers = SplitCsv(lines[0]);
        int sysIdx = Array.FindIndex(headers, static h => h.Contains("System", StringComparison.OrdinalIgnoreCase));
        if (sysIdx < 0) sysIdx = 0;
        int distIdx = Array.FindIndex(headers, static h => h.Contains("Distance", StringComparison.OrdinalIgnoreCase));
        int bodyIdx = Array.FindIndex(headers, static h => h.Contains("Body", StringComparison.OrdinalIgnoreCase));
        int fuelIdx = Array.FindIndex(headers, static h => h.Contains("Fuel", StringComparison.OrdinalIgnoreCase) || h.Contains("Tritium", StringComparison.OrdinalIgnoreCase));

        int idx = 1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            string[] cols = SplitCsv(lines[i]);
            if (sysIdx >= cols.Length || string.IsNullOrWhiteSpace(cols[sysIdx])) continue;

            string sys = cols[sysIdx];
            string dist = distIdx >= 0 && distIdx < cols.Length && !string.IsNullOrWhiteSpace(cols[distIdx]) ? $"{cols[distIdx]} AL" : "—";
            var details = new List<string>();
            if (bodyIdx >= 0 && bodyIdx < cols.Length && !string.IsNullOrWhiteSpace(cols[bodyIdx]))
                details.Add(cols[bodyIdx]);
            if (fuelIdx >= 0 && fuelIdx < cols.Length && !string.IsNullOrWhiteSpace(cols[fuelIdx]))
                details.Add($"Tritio: {cols[fuelIdx]} t");

            list.Add(new SpanshRouteWaypoint(idx++, sys, dist, string.Join(" · ", details), false, false));
        }

        return list;
    }
}

public sealed record SpanshServiceStationItem(
    string SystemName,
    string StationName,
    string DistanceDisplay,
    string LandingPad,
    string Detail);

public sealed record SpanshMiningRingItem(
    string SystemName,
    string BodyName,
    string RingName,
    string RingType,
    string DistanceDisplay,
    string Signals);

public sealed record SpanshRouteWaypoint(
    int Index,
    string SystemName,
    string DistanceDisplay,
    string Details,
    bool IsNeutron,
    bool Completed)
{
    public string StatusDisplay => Completed ? "✓" : (IsNeutron ? "⚡" : "•");
}

public sealed record SpanshTradeHopItem(
    int Hop,
    string FromSystem,
    string FromStation,
    string ToSystem,
    string ToStation,
    string Commodity,
    string ProfitDisplay,
    string DistanceDisplay)
{
    public string RouteDisplay => $"{FromSystem} ({FromStation}) → {ToSystem} ({ToStation})";
}
