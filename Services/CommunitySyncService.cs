// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/CommunitySyncService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Background uploader and bidirectional client for EDDN, EDSM, and Inara.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using HUDnopy.Models;
using static HUDnopy.Services.JsonElementExtensions;

namespace HUDnopy.Services;

/// <summary>
/// Background uploader and bidirectional client for EDDN, EDSM, and Inara.
/// </summary>
public sealed class CommunitySyncService : IDisposable
{
    private const string EddnEndpoint = "https://eddn.edcd.io:4430/upload/";
    private const string EdsmJournalEndpoint = "https://www.edsm.net/api-journal-v1";
    private const string EdsmDiscardEndpoint = "https://www.edsm.net/api-journal-v1/discard";
    private const string EdsmSetCommentEndpoint = "https://www.edsm.net/api-logs-v1/set-comment";
    private const string EdsmGetCommentEndpoint = "https://www.edsm.net/api-logs-v1/get-comment";
    private const string EdsmTrafficEndpoint = "https://www.edsm.net/api-system-v1/traffic";
    private const string EdsmDeathsEndpoint = "https://www.edsm.net/api-system-v1/deaths";
    private const string InaraEndpoint = "https://inara.cz/inapi/v1/";
    private const string SoftwareName = HttpClientHeaderDefaults.ProductName;
    private static readonly string SoftwareVersion = HttpClientHeaderDefaults.ProductVersion;

    private static readonly FrozenDictionary<string, string> EddnJournalSchemas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["FSDJump"] = "https://eddn.edcd.io/schemas/journal/1",
        ["Docked"] = "https://eddn.edcd.io/schemas/journal/1",
        ["Scan"] = "https://eddn.edcd.io/schemas/journal/1",
        ["Location"] = "https://eddn.edcd.io/schemas/journal/1",
        ["CarrierJump"] = "https://eddn.edcd.io/schemas/journal/1",
        ["SAASignalsFound"] = "https://eddn.edcd.io/schemas/journal/1",
        ["FSSDiscoveryScan"] = "https://eddn.edcd.io/schemas/fssdiscoveryscan/1",
        ["FSSAllBodiesFound"] = "https://eddn.edcd.io/schemas/fssallbodiesfound/1",
        ["FSSBodySignals"] = "https://eddn.edcd.io/schemas/fssbodysignals/1",
        ["ApproachSettlement"] = "https://eddn.edcd.io/schemas/approachsettlement/1",
        ["CodexEntry"] = "https://eddn.edcd.io/schemas/codexentry/1",
        ["ScanBaryCentre"] = "https://eddn.edcd.io/schemas/scanbarycentre/1",
        ["NavBeaconScan"] = "https://eddn.edcd.io/schemas/navbeaconscan/1",
        ["DockingGranted"] = "https://eddn.edcd.io/schemas/dockinggranted/1"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> EddnForbiddenKeys = new[]
    {
        "ActiveFine", "CockpitBreach", "BoostUsed", "FuelLevel", "FuelUsed", "JumpDist",
        "Latitude", "Longitude", "Wanted", "MyReputation", "SquadronFaction",
        "HappiestSystem", "HomeSystem", "VoucherAmount", "IsNewEntry", "NewTraitsDiscovered",
        "Taxi", "Multicrew", "InSRV", "OnFoot"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> EddnSchemasAllowingStarPos = new[]
    {
        "https://eddn.edcd.io/schemas/journal/1",
        "https://eddn.edcd.io/schemas/approachsettlement/1",
        "https://eddn.edcd.io/schemas/codexentry/1",
        "https://eddn.edcd.io/schemas/scanbarycentre/1"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> DefaultEdsmDiscardEvents = new[]
    {
        "Fileheader", "Music", "ReceiveText", "SendText", "FuelScoop", "HeatWarning",
        "HeatDamage", "ShieldState", "HullDamage", "UnderAttack", "Scanned", "ShipTargeted",
        "SupercruiseEntry", "SupercruiseExit", "ApproachBody", "LeaveBody", "Touchdown",
        "Liftoff", "Shutdown", "StartJump", "ReservoirReplenished"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly ConfigService _configService;
    private readonly EliteFileWatcherService _fileWatcher;
    private readonly GameEventHub _gameEvents;
    private readonly HttpClient _httpClient = HttpClientHeaderDefaults.CreateHttpClient(TimeSpan.FromSeconds(15));
    private readonly Lock _statusLock = new();
    private readonly Lock _queueLock = new();
    private readonly List<Dictionary<string, object?>> _edsmQueue = new();
    private readonly List<Dictionary<string, object>> _inaraQueue = new();
    private readonly PeriodicTimer _flushTimer;
    private readonly CancellationTokenSource _flushLoopCts = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    private HashSet<string> _edsmDiscardEvents = new(DefaultEdsmDiscardEvents, StringComparer.OrdinalIgnoreCase);
    private bool _edsmDiscardLoaded;
    private DateTimeOffset _lastInaraFlushUtc = DateTimeOffset.MinValue;
    private string _lastInaraMaterialsFingerprint = "";
    private string _lastInaraLoadoutFingerprint = "";
    private string _lastAutoEdsmSystem = "";
    private bool _inaraStartupLoaded;
    private readonly Dictionary<string, string> _lastEddnSnapshotFingerprints = new(StringComparer.OrdinalIgnoreCase);

    public int SyncedEventsCount { get; private set; }
    public int EddnUploadedCount { get; private set; }
    public int InaraUploadedCount { get; private set; }
    public string LastSyncStatus { get; private set; } = "";
    public string LastEddnStatus { get; private set; } = "";
    public event Action? SyncStatusChanged;
    public event Action<EdsmSystemIntel>? EdsmSystemIntelUpdated;

    public CommunitySyncService(
        ConfigService configService,
        EliteFileWatcherService fileWatcher,
        GameEventHub gameEvents)
    {
        _configService = configService;
        _fileWatcher = fileWatcher;
        _gameEvents = gameEvents;
        _gameEvents.JournalEventReceived += OnJournalEventReceived;
        _fileWatcher.SnapshotFileUpdated += OnSnapshotFileUpdated;

        // Batched queue every 35 s using PeriodicTimer (non-overlapping ticks, complies with Inara rate limit)
        _flushTimer = new PeriodicTimer(TimeSpan.FromSeconds(35));
        _ = RunFlushLoopAsync();
    }

    private async Task RunFlushLoopAsync()
    {
        try
        {
            while (await _flushTimer.WaitForNextTickAsync(_flushLoopCts.Token))
            {
                try
                {
                    await FlushQueuesAsync(forceInara: false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppDiagnosticsLogger.Warn("CommunitySync", $"Flush loop iteration failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown
        }
    }

    private void OnJournalEventReceived(GameJournalEvent gameEvent)
    {
        var cfg = _configService.Current;
        var state = _fileWatcher.State;

        // Read-only startup intelligence (EDSM system discovery/factions/intel & Inara commander profile/CGs)
        // is allowed to trigger once during the initial Journal read when enabled.
        if (HttpClientHeaderDefaults.TestHttpHandlerFactory is null || !gameEvent.IsHistorical)
        {
            if (cfg.EnableEdsmSync && gameEvent.EventType is "FSDJump" or "Location")
            {
                string arrivedSys = GetString(gameEvent.Data, "StarSystem") ?? state.CurrentSystem;
                if (!string.IsNullOrWhiteSpace(arrivedSys) && arrivedSys != "---" &&
                    !string.Equals(_lastAutoEdsmSystem, arrivedSys, StringComparison.OrdinalIgnoreCase))
                {
                    _lastAutoEdsmSystem = arrivedSys;
                    _ = FetchEdsmDiscoveryAndFactionsAsync(arrivedSys);
                    _ = FetchEdsmSystemIntelAsync(arrivedSys);
                }
            }

            if (cfg.EnableInaraSync && !_inaraStartupLoaded && gameEvent.EventType is "LoadGame" or "Location")
            {
                _inaraStartupLoaded = true;
                _ = FetchInaraCommanderProfileAsync();
                if (state.CommunityGoals.Count == 0)
                    _ = FetchInaraCommunityGoalsAsync();
            }
        }

        // Priority 1: never re-upload historical journal events loaded on startup to EDDN, EDSM, or Inara
        if (gameEvent.IsHistorical)
            return;

        if (cfg.EnableEddnUpload && EddnJournalSchemas.TryGetValue(gameEvent.EventType, out string? schemaRef))
        {
            if (state.IsLegacyGalaxy)
                UpdateStatus("EDDN omitido (galaxia Legacy)", increment: false);
            else
                _ = SendEddnJournalEventAsync(gameEvent, schemaRef, state);
        }

        if (cfg.EnableEdsmSync && !string.IsNullOrWhiteSpace(cfg.EdsmApiKey))
        {
            EnqueueEdsmEvent(gameEvent, state);
        }

        if (cfg.EnableInaraSync && !string.IsNullOrWhiteSpace(cfg.InaraApiKey))
        {
            if (state.IsLegacyGalaxy)
                UpdateStatus("Inara omitido (galaxia Legacy)", increment: false);
            else
                EnqueueInaraEvents(gameEvent, state);
        }
    }

    private void OnSnapshotFileUpdated(string snapshotName, JsonElement root)
    {
        var cfg = _configService.Current;
        var state = _fileWatcher.State;
        if (!cfg.EnableEddnUpload || state.IsLegacyGalaxy)
            return;

        _ = SendEddnSnapshotAsync(snapshotName, root, state);
    }

    // =========================================================================
    // EDDN (Prioridad 1 y Prioridad 4)
    // =========================================================================

    private static string BuildAnonymousUploaderId(string? commanderName)
    {
        string clean = HttpClientHeaderDefaults.CleanCommanderName(commanderName);
        if (string.IsNullOrWhiteSpace(clean))
            clean = SoftwareName;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(clean));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    private Dictionary<string, object> BuildEddnHeader(ShipState state)
    {
        var header = new Dictionary<string, object>
        {
            ["uploaderID"] = BuildAnonymousUploaderId(state.CommanderName),
            ["softwareName"] = SoftwareName,
            ["softwareVersion"] = SoftwareVersion
        };
        if (!string.IsNullOrWhiteSpace(state.GameVersion))
            header["gameversion"] = state.GameVersion;
        if (!string.IsNullOrWhiteSpace(state.GameBuild))
            header["gamebuild"] = state.GameBuild;
        return header;
    }

    private async Task SendEddnJournalEventAsync(GameJournalEvent gameEvent, string schemaRef, ShipState state)
    {
        try
        {
            bool preserveLatLon = string.Equals(gameEvent.EventType, "ApproachSettlement", StringComparison.OrdinalIgnoreCase);
            var message = SanitizeElementToDictionary(gameEvent.Data, preserveLatLon);
            message["horizons"] = state.IsHorizons;
            message["odyssey"] = state.IsOdyssey;

            bool allowsSystemAndPos = EddnSchemasAllowingStarPos.Contains(schemaRef);
            if (allowsSystemAndPos)
            {
                if (!message.ContainsKey("StarSystem") && !string.IsNullOrWhiteSpace(state.CurrentSystem))
                    message["StarSystem"] = state.CurrentSystem;
                if (!message.ContainsKey("StarPos") && state.StarPos is { Length: 3 })
                    message["StarPos"] = state.StarPos;
            }
            if (!message.ContainsKey("SystemAddress") && long.TryParse(state.SystemAddress, out long sysAddr))
                message["SystemAddress"] = sysAddr;

            await PostEddnAsync(schemaRef, BuildEddnHeader(state), message, gameEvent.EventType);
        }
        catch (Exception ex)
        {
            LastEddnStatus = $"Error: {ex.Message}";
            AppDiagnosticsLogger.Warn("EDDN", $"Upload failed for '{gameEvent.EventType}': {ex.Message}");
            UpdateStatus($"EDDN: {ex.Message}", increment: false);
        }
    }

    private async Task SendEddnSnapshotAsync(string snapshotName, JsonElement root, ShipState state)
    {
        try
        {
            string? schemaRef = null;
            Dictionary<string, object?>? message = null;

            switch (snapshotName)
            {
                case "Market":
                    schemaRef = "https://eddn.edcd.io/schemas/commodity/3";
                    message = BuildEddnCommodityMessage(root, state);
                    break;
                case "Outfitting":
                    schemaRef = "https://eddn.edcd.io/schemas/outfitting/2";
                    message = BuildEddnOutfittingMessage(root, state);
                    break;
                case "Shipyard":
                    schemaRef = "https://eddn.edcd.io/schemas/shipyard/2";
                    message = BuildEddnShipyardMessage(root, state);
                    break;
                case "FCMaterials":
                    schemaRef = "https://eddn.edcd.io/schemas/fcmaterials_journal/1";
                    message = SanitizeElementToDictionary(root);
                    message["horizons"] = state.IsHorizons;
                    message["odyssey"] = state.IsOdyssey;
                    break;
                case "NavRoute":
                    schemaRef = "https://eddn.edcd.io/schemas/navroute/1";
                    message = SanitizeElementToDictionary(root);
                    message["horizons"] = state.IsHorizons;
                    message["odyssey"] = state.IsOdyssey;
                    break;
            }

            if (schemaRef is not null && message is not null)
            {
                // Deduplicate identical snapshots so repeated file-watcher reads never spam EDDN
                string payloadJson = JsonSerializer.Serialize(message);
                string fp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
                lock (_queueLock)
                {
                    if (_lastEddnSnapshotFingerprints.TryGetValue(snapshotName, out string? prev) && prev == fp)
                        return;
                    _lastEddnSnapshotFingerprints[snapshotName] = fp;
                }
                await PostEddnAsync(schemaRef, BuildEddnHeader(state), message, snapshotName);
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("EDDN", $"Snapshot upload failed for '{snapshotName}': {ex.Message}");
            UpdateStatus($"EDDN ({snapshotName}): {ex.Message}", increment: false);
        }
    }

    private static Dictionary<string, object?>? BuildEddnCommodityMessage(JsonElement root, ShipState state)
    {
        if (!root.TryGetProperty("Items", out var itemsProp) || itemsProp.ValueKind != JsonValueKind.Array)
            return null;

        string systemName = GetString(root, "StarSystem") ?? state.CurrentSystem;
        string stationName = GetString(root, "StationName") ?? state.StationName;
        long? marketId = GetLong(root, "MarketID");
        if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(stationName) || !marketId.HasValue)
            return null;

        var commodities = new List<Dictionary<string, object>>();
        foreach (var item in itemsProp.EnumerateArray())
        {
            string? rawName = GetString(item, "Name");
            if (string.IsNullOrWhiteSpace(rawName))
                continue;
            string cleanName = rawName.Trim().TrimStart('$').Replace("_name;", "", StringComparison.OrdinalIgnoreCase);
            string category = GetString(item, "Category") ?? "";
            if (category.Contains("NonMarketable", StringComparison.OrdinalIgnoreCase))
                continue;

            commodities.Add(new Dictionary<string, object>
            {
                ["name"] = cleanName,
                ["meanPrice"] = GetLong(item, "MeanPrice") ?? 0,
                ["buyPrice"] = GetLong(item, "BuyPrice") ?? 0,
                ["stock"] = GetLong(item, "Stock") ?? 0,
                ["stockBracket"] = GetLong(item, "StockBracket") ?? 0,
                ["sellPrice"] = GetLong(item, "SellPrice") ?? 0,
                ["demand"] = GetLong(item, "Demand") ?? 0,
                ["demandBracket"] = GetLong(item, "DemandBracket") ?? 0
            });
        }

        if (commodities.Count == 0)
            return null;

        return new Dictionary<string, object?>
        {
            ["systemName"] = systemName,
            ["stationName"] = stationName,
            ["marketId"] = marketId.Value,
            ["horizons"] = state.IsHorizons,
            ["odyssey"] = state.IsOdyssey,
            ["timestamp"] = GetString(root, "timestamp") ?? DateTimeOffset.UtcNow.ToString("O"),
            ["commodities"] = commodities
        };
    }

    private static Dictionary<string, object?>? BuildEddnOutfittingMessage(JsonElement root, ShipState state)
    {
        if (!root.TryGetProperty("Items", out var itemsProp) || itemsProp.ValueKind != JsonValueKind.Array)
            return null;

        string systemName = GetString(root, "StarSystem") ?? state.CurrentSystem;
        string stationName = GetString(root, "StationName") ?? state.StationName;
        long? marketId = GetLong(root, "MarketID");
        if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(stationName) || !marketId.HasValue)
            return null;

        var modules = itemsProp.EnumerateArray()
            .Select(static i => GetString(i, "Name"))
            .Where(static n => !string.IsNullOrWhiteSpace(n) &&
                               (n!.StartsWith("hpt_", StringComparison.OrdinalIgnoreCase) ||
                                n.StartsWith("int_", StringComparison.OrdinalIgnoreCase) ||
                                n.Contains("_armour_", StringComparison.OrdinalIgnoreCase)))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (modules.Count == 0)
            return null;

        return new Dictionary<string, object?>
        {
            ["systemName"] = systemName,
            ["stationName"] = stationName,
            ["marketId"] = marketId.Value,
            ["horizons"] = state.IsHorizons,
            ["odyssey"] = state.IsOdyssey,
            ["timestamp"] = GetString(root, "timestamp") ?? DateTimeOffset.UtcNow.ToString("O"),
            ["modules"] = modules
        };
    }

    private static Dictionary<string, object?>? BuildEddnShipyardMessage(JsonElement root, ShipState state)
    {
        if (!root.TryGetProperty("PriceList", out var listProp) || listProp.ValueKind != JsonValueKind.Array)
            return null;

        string systemName = GetString(root, "StarSystem") ?? state.CurrentSystem;
        string stationName = GetString(root, "StationName") ?? state.StationName;
        long? marketId = GetLong(root, "MarketID");
        if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(stationName) || !marketId.HasValue)
            return null;

        var ships = listProp.EnumerateArray()
            .Select(static i => GetString(i, "ShipType"))
            .Where(static n => !string.IsNullOrWhiteSpace(n))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ships.Count == 0)
            return null;

        return new Dictionary<string, object?>
        {
            ["systemName"] = systemName,
            ["stationName"] = stationName,
            ["marketId"] = marketId.Value,
            ["horizons"] = state.IsHorizons,
            ["odyssey"] = state.IsOdyssey,
            ["timestamp"] = GetString(root, "timestamp") ?? DateTimeOffset.UtcNow.ToString("O"),
            ["ships"] = ships
        };
    }

    private async Task PostEddnAsync(string schemaRef, Dictionary<string, object> header, Dictionary<string, object?> message, string label)
    {
        bool isBeta = _fileWatcher.State.GameVersion.Contains("beta", StringComparison.OrdinalIgnoreCase);
        string targetSchema = isBeta && !schemaRef.EndsWith("/test", StringComparison.OrdinalIgnoreCase)
            ? schemaRef + "/test"
            : schemaRef;

        var envelope = new Dictionary<string, object>
        {
            ["$schemaRef"] = targetSchema,
            ["header"] = header,
            ["message"] = message
        };

        string json = JsonSerializer.Serialize(envelope);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(EddnEndpoint, content);
        if (response.IsSuccessStatusCode)
        {
            EddnUploadedCount++;
            LastEddnStatus = $"OK ({label} · {DateTime.Now:HH:mm:ss})";
            UpdateStatus($"EDDN {LastEddnStatus}", increment: true);
        }
        else
        {
            LastEddnStatus = $"HTTP {(int)response.StatusCode} ({label})";
            UpdateStatus($"EDDN {LastEddnStatus}", increment: false);
        }
    }

    // =========================================================================
    // EDSM Journal API v1 (Prioridad 2)
    // =========================================================================

    private async Task EnsureEdsmDiscardListLoadedAsync()
    {
        if (_edsmDiscardLoaded)
            return;
        _edsmDiscardLoaded = true;
        try
        {
            using var resp = await _httpClient.GetAsync(EdsmDiscardEndpoint);
            if (!resp.IsSuccessStatusCode)
                return;
            string body = await resp.Content.ReadAsStringAsync();
            var list = JsonSerializer.Deserialize<List<string>>(body);
            if (list is { Count: > 0 })
                _edsmDiscardEvents = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Mantener lista local de respaldo
        }
    }

    private void EnqueueEdsmEvent(GameJournalEvent gameEvent, ShipState state)
    {
        if (_edsmDiscardEvents.Contains(gameEvent.EventType))
            return;

        var payload = ConvertElementToDictionary(gameEvent.Data);
        if (!string.IsNullOrWhiteSpace(state.CurrentSystem))
            payload["_systemName"] = state.CurrentSystem;
        if (state.StarPos is { Length: 3 })
            payload["_systemCoordinates"] = state.StarPos;
        if (!string.IsNullOrWhiteSpace(state.StationName))
            payload["_stationName"] = state.StationName;
        if (state.ShipId.HasValue)
            payload["_shipId"] = state.ShipId.Value;

        lock (_queueLock)
        {
            _edsmQueue.Add(payload);
            if (_edsmQueue.Count > 200)
                _edsmQueue.RemoveRange(0, _edsmQueue.Count - 200);
        }
    }

    private async Task<string?> FlushEdsmQueueAsync(AppConfig cfg, ShipState state)
    {
        if (!cfg.EnableEdsmSync || string.IsNullOrWhiteSpace(cfg.EdsmApiKey))
            return null;

        List<Dictionary<string, object?>> batch;
        lock (_queueLock)
        {
            if (_edsmQueue.Count == 0)
                return null;
            batch = new List<Dictionary<string, object?>>(_edsmQueue);
            _edsmQueue.Clear();
        }

        string cmdr = CleanCommanderName(cfg.EdsmCommanderName, state.CommanderName);
        if (string.IsNullOrWhiteSpace(cmdr))
            return null;

        await EnsureEdsmDiscardListLoadedAsync();

        try
        {
            string messageJson = JsonSerializer.Serialize(batch);
            using var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("commanderName", cmdr),
                new KeyValuePair<string, string>("apiKey", cfg.EdsmApiKey),
                new KeyValuePair<string, string>("fromSoftware", SoftwareName),
                new KeyValuePair<string, string>("fromSoftwareVersion", SoftwareVersion),
                new KeyValuePair<string, string>("message", messageJson)
            });

            using var resp = await _httpClient.PostAsync(EdsmJournalEndpoint, form);
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode >= 500)
                    RequeueEdsmBatch(batch);
                return $"EDSM HTTP {(int)resp.StatusCode}";
            }

            string responseBody = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);
            int msgNum = GetInt(doc.RootElement, "msgnum") ?? 0;
            bool systemCreated = false;
            if (doc.RootElement.TryGetProperty("events", out var evArr) && evArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var ev in evArr.EnumerateArray())
                {
                    if (ev.TryGetProperty("systemCreated", out var sc) && sc.ValueKind == JsonValueKind.True)
                        systemCreated = true;
                }
            }

            if (msgNum == 100)
            {
                UpdateStatus(
                    systemCreated
                        ? $"EDSM OK ({batch.Count} ev · ¡Nuevo sistema! · {DateTime.Now:HH:mm:ss})"
                        : $"EDSM OK ({batch.Count} ev · {DateTime.Now:HH:mm:ss})",
                    increment: true);
                return systemCreated ? $"EDSM OK ({batch.Count} ev · Nuevo sistema)" : $"EDSM OK ({batch.Count} ev)";
            }

            string msg = GetString(doc.RootElement, "msg") ?? $"msgnum {msgNum}";
            return $"EDSM: {msg}";
        }
        catch (Exception ex)
        {
            RequeueEdsmBatch(batch);
            return $"EDSM: {ex.Message}";
        }
    }

    private void RequeueEdsmBatch(List<Dictionary<string, object?>> batch)
    {
        lock (_queueLock)
        {
            _edsmQueue.InsertRange(0, batch);
            if (_edsmQueue.Count > 250)
                _edsmQueue.RemoveRange(250, _edsmQueue.Count - 250);
        }
    }

    public async Task<bool> SetEdsmSystemCommentAsync(string systemName, string comment)
    {
        var cfg = _configService.Current;
        if (!cfg.EnableEdsmSync || string.IsNullOrWhiteSpace(cfg.EdsmApiKey) || string.IsNullOrWhiteSpace(systemName))
            return false;

        string cmdr = CleanCommanderName(cfg.EdsmCommanderName, _fileWatcher.State.CommanderName);
        if (string.IsNullOrWhiteSpace(cmdr))
            return false;

        try
        {
            using var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("commanderName", cmdr),
                new KeyValuePair<string, string>("apiKey", cfg.EdsmApiKey),
                new KeyValuePair<string, string>("systemName", systemName),
                new KeyValuePair<string, string>("comment", comment ?? "")
            });
            using var resp = await _httpClient.PostAsync(EdsmSetCommentEndpoint, form);
            if (resp.IsSuccessStatusCode)
            {
                UpdateStatus($"EDSM nota sincronizada ({systemName})", increment: true);
                return true;
            }
        }
        catch
        {
            // Ignorar fallo transitorio de red
        }
        return false;
    }

    public async Task<EdsmSystemIntel?> FetchEdsmSystemIntelAsync(string systemName)
    {
        if (!_configService.Current.EnableEdsmSync || string.IsNullOrWhiteSpace(systemName))
            return null;

        int trafficDay = 0, trafficWeek = 0, deathsDay = 0, deathsWeek = 0;
        string remoteComment = "";

        try
        {
            string encSys = Uri.EscapeDataString(systemName);
            var trafficTask = _httpClient.GetAsync($"{EdsmTrafficEndpoint}?systemName={encSys}");
            var deathsTask = _httpClient.GetAsync($"{EdsmDeathsEndpoint}?systemName={encSys}");

            Task<HttpResponseMessage>? commentTask = null;
            var cfg = _configService.Current;
            if (cfg.EnableEdsmSync && !string.IsNullOrWhiteSpace(cfg.EdsmApiKey))
            {
                string cmdr = CleanCommanderName(cfg.EdsmCommanderName, _fileWatcher.State.CommanderName);
                if (!string.IsNullOrWhiteSpace(cmdr))
                    commentTask = _httpClient.GetAsync($"{EdsmGetCommentEndpoint}?commanderName={Uri.EscapeDataString(cmdr)}&apiKey={Uri.EscapeDataString(cfg.EdsmApiKey)}&systemName={encSys}");
            }

            using var tResp = await trafficTask;
            if (tResp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await tResp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("traffic", out var tr) && tr.ValueKind == JsonValueKind.Object)
                {
                    trafficDay = GetInt(tr, "day") ?? 0;
                    trafficWeek = GetInt(tr, "week") ?? 0;
                }
            }

            using var dResp = await deathsTask;
            if (dResp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await dResp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("deaths", out var dt) && dt.ValueKind == JsonValueKind.Object)
                {
                    deathsDay = GetInt(dt, "day") ?? 0;
                    deathsWeek = GetInt(dt, "week") ?? 0;
                }
            }

            if (commentTask is not null)
            {
                using var cResp = await commentTask;
                if (cResp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await cResp.Content.ReadAsStringAsync());
                    remoteComment = GetString(doc.RootElement, "comment") ?? "";
                }
            }

            var intel = new EdsmSystemIntel(systemName, trafficDay, trafficWeek, deathsDay, deathsWeek, remoteComment);
            EdsmSystemIntelUpdated?.Invoke(intel);
            return intel;
        }
        catch
        {
            return null;
        }
    }

    public event Action<EdsmSystemDiscoveryIntel>? EdsmDiscoveryUpdated;

    /// <summary>
    /// Queries EDSM system existence, known bodies, estimated scan value, and minor factions for a star system.
    /// Gated by AppConfig.EnableEdsmSync.
    /// </summary>
    public async Task<EdsmSystemDiscoveryIntel?> FetchEdsmDiscoveryAndFactionsAsync(string systemName, CancellationToken ct = default)
    {
        if (!_configService.Current.EnableEdsmSync || string.IsNullOrWhiteSpace(systemName) || systemName == "---")
            return null;

        try
        {
            string encSys = Uri.EscapeDataString(systemName);
            var sysTask = _httpClient.GetAsync($"https://www.edsm.net/api-v1/system?systemName={encSys}&showInformation=1", ct);
            var bodiesTask = _httpClient.GetAsync($"https://www.edsm.net/api-system-v1/bodies?systemName={encSys}", ct);
            var factionsTask = _httpClient.GetAsync($"https://www.edsm.net/api-system-v1/factions?systemName={encSys}", ct);

            await Task.WhenAll(sysTask, bodiesTask, factionsTask);

            bool isKnown = false;
            string controllingFaction = "";
            using (var sResp = await sysTask)
            {
                if (sResp.IsSuccessStatusCode)
                {
                    string body = await sResp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("name", out var np) &&
                        np.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(np.GetString()))
                    {
                        isKnown = true;
                        if (doc.RootElement.TryGetProperty("information", out var info) && info.ValueKind == JsonValueKind.Object)
                            controllingFaction = GetString(info, "faction") ?? "";
                    }
                }
            }

            int knownBodies = 0;
            long estValue = 0;
            var edsmBodies = new List<SpanshBodySummary>();
            using (var bResp = await bodiesTask)
            {
                if (bResp.IsSuccessStatusCode)
                {
                    string body = await bResp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("bodies", out var bArr) &&
                        bArr.ValueKind == JsonValueKind.Array)
                    {
                        knownBodies = bArr.GetArrayLength();
                        foreach (var b in bArr.EnumerateArray())
                        {
                            string bName = GetString(b, "name") ?? "";
                            string subType = GetString(b, "subType") ?? GetString(b, "type") ?? "";
                            string terra = GetString(b, "terraformingState") ?? "";
                            bool isTerraformable = terra.Contains("Terraformable", StringComparison.OrdinalIgnoreCase);
                            estValue += EstimateEdsmBodyValue(subType, isTerraformable);
                            if (!string.IsNullOrWhiteSpace(bName))
                            {
                                double? gravG = GetDouble(b, "gravity");
                                bool? landable = b.TryGetProperty("isLandable", out var lp) && lp.ValueKind is JsonValueKind.True or JsonValueKind.False
                                    ? lp.GetBoolean()
                                    : null;
                                bool hasRings = b.TryGetProperty("rings", out var rp) && rp.ValueKind == JsonValueKind.Array && rp.GetArrayLength() > 0;
                                edsmBodies.Add(new SpanshBodySummary(
                                    bName,
                                    subType,
                                    GetDouble(b, "distanceToArrival"),
                                    landable,
                                    GetInt(b, "bodyId"),
                                    gravG.HasValue ? gravG.Value * 9.80665 : null,
                                    GetDouble(b, "surfaceTemperature"),
                                    GetString(b, "atmosphereType"),
                                    HasRings: hasRings));
                            }
                        }
                    }
                }
            }

            var factions = new List<SystemFactionItem>();
            using (var fResp = await factionsTask)
            {
                if (fResp.IsSuccessStatusCode)
                {
                    string body = await fResp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (string.IsNullOrWhiteSpace(controllingFaction) &&
                            doc.RootElement.TryGetProperty("controllingFaction", out var cf) &&
                            cf.ValueKind == JsonValueKind.Object)
                        {
                            controllingFaction = GetString(cf, "name") ?? "";
                        }

                        if (doc.RootElement.TryGetProperty("factions", out var fArr) && fArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var f in fArr.EnumerateArray())
                            {
                                string fName = GetString(f, "name") ?? "";
                                if (string.IsNullOrWhiteSpace(fName)) continue;
                                double inf = Math.Round((GetDouble(f, "influence") ?? 0) * 100.0, 1);
                                factions.Add(new SystemFactionItem
                                {
                                    Name = fName,
                                    InfluencePercent = inf,
                                    FactionState = GetString(f, "state") ?? "None",
                                    Government = GetString(f, "government") ?? "",
                                    Allegiance = GetString(f, "allegiance") ?? "",
                                    DataSource = "EDSM"
                                });
                            }
                        }
                    }
                }
            }

            var intel = new EdsmSystemDiscoveryIntel
            {
                SystemName = systemName,
                IsKnownInEdsm = isKnown,
                KnownBodiesCount = knownBodies,
                EstimatedScanValue = estValue,
                ControllingFaction = controllingFaction,
                Factions = factions.OrderByDescending(static f => f.InfluencePercent).ToList(),
                FetchedAtUtc = DateTimeOffset.UtcNow
            };

            _fileWatcher.ApplyCapiEnrichment(state =>
            {
                if (string.Equals(state.CurrentSystem, systemName, StringComparison.OrdinalIgnoreCase))
                {
                    state.EdsmDiscoveryIntel = intel;
                    if (state.SystemFactions.Count == 0 && intel.Factions.Count > 0)
                        state.SystemFactions = intel.Factions.Select(static f => f.Snapshot()).ToList();
                }
            });
            if (edsmBodies.Count > 0)
                _fileWatcher.SyncEdsmSystemBodies(systemName, _fileWatcher.State.SystemAddress, edsmBodies);

            EdsmDiscoveryUpdated?.Invoke(intel);
            return intel;
        }
        catch
        {
            return null;
        }
    }

    private static long EstimateEdsmBodyValue(string subType, bool terraformable)
    {
        if (subType.Contains("Earth-like", StringComparison.OrdinalIgnoreCase)) return 1_126_000;
        if (subType.Contains("Ammonia world", StringComparison.OrdinalIgnoreCase)) return 597_000;
        if (subType.Contains("Water world", StringComparison.OrdinalIgnoreCase)) return terraformable ? 1_119_000 : 415_000;
        if (subType.Contains("High metal content", StringComparison.OrdinalIgnoreCase)) return terraformable ? 683_000 : 14_000;
        if (subType.Contains("Rocky body", StringComparison.OrdinalIgnoreCase) && terraformable) return 540_000;
        if (subType.Contains("Class II gas giant", StringComparison.OrdinalIgnoreCase)) return 28_000;
        return 1_500;
    }

    /// <summary>
    /// Builds a direct Inara web deep-link URL for a system, station, commodity, or commander.
    /// </summary>
    public static string BuildInaraDeepLink(string kind, string target)
    {
        string clean = Uri.EscapeDataString((target ?? "").Trim());
        return kind.ToLowerInvariant() switch
        {
            "system" => $"https://inara.cz/elite/starsystem/?search={clean}",
            "station" => $"https://inara.cz/elite/station/?search={clean}",
            "commodity" => $"https://inara.cz/elite/commodity/?search={clean}",
            "commander" => $"https://inara.cz/elite/cmdr/?search={clean}",
            _ => $"https://inara.cz/elite/search/?search={clean}"
        };
    }

    /// <summary>
    /// Queries Inara Read API (getCommanderProfile) for the active or specified commander.
    /// Gated by AppConfig.EnableInaraSync.
    /// </summary>
    public async Task<(bool Success, InaraCommanderProfile? Profile, string Message)> FetchInaraCommanderProfileAsync(
        string? commanderName = null,
        CancellationToken ct = default)
    {
        var cfg = _configService.Current;
        if (!cfg.EnableInaraSync)
            return (false, null, "Inara integration is disabled.");

        string targetCmdr = CleanCommanderName(commanderName ?? cfg.InaraCommanderName, _fileWatcher.State.CommanderName);
        if (string.IsNullOrWhiteSpace(targetCmdr))
            return (false, null, "Commander name is required.");

        string apiKey = string.IsNullOrWhiteSpace(cfg.InaraApiKey) ? "anonymous" : cfg.InaraApiKey.Trim();
        var payload = new
        {
            header = new
            {
                appName = SoftwareName,
                appVersion = SoftwareVersion,
                isBeingDeveloped = false,
                APIkey = apiKey,
                commanderName = targetCmdr
            },
            events = new[]
            {
                new
                {
                    eventName = "getCommanderProfile",
                    eventTimestamp = DateTimeOffset.UtcNow.ToString("O"),
                    eventData = new { searchName = targetCmdr }
                }
            }
        };

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _httpClient.PostAsync(InaraEndpoint, content, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, null, $"Inara HTTP {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("events", out var evArr) || evArr.ValueKind != JsonValueKind.Array || evArr.GetArrayLength() == 0)
                return (false, null, "Inara returned no profile data.");

            var ev0 = evArr[0];
            if (!ev0.TryGetProperty("eventData", out var ed) || ed.ValueKind != JsonValueKind.Object)
                return (false, null, "Commander not found on Inara.");

            string sqName = "";
            string sqRank = "";
            if (ed.TryGetProperty("commanderSquadron", out var sq) && sq.ValueKind == JsonValueKind.Object)
            {
                sqName = GetString(sq, "squadronName") ?? "";
                sqRank = GetString(sq, "squadronMemberRank") ?? "";
            }

            var profile = new InaraCommanderProfile
            {
                CommanderName = GetString(ed, "commanderName") ?? targetCmdr,
                SquadronName = sqName,
                SquadronRank = sqRank,
                PreferredAllegiance = GetString(ed, "preferredAllegianceName") ?? "",
                InaraUrl = GetString(ed, "inaraURL") ?? BuildInaraDeepLink("commander", targetCmdr),
                FetchedAtUtc = DateTimeOffset.UtcNow
            };

            _fileWatcher.ApplyCapiEnrichment(state => state.InaraProfile = profile);
            return (true, profile, $"Inara profile loaded: {profile.CommanderName}");
        }
        catch (Exception ex)
        {
            return (false, null, $"Inara error: {ex.Message}");
        }
    }

    /// <summary>
    /// Queries Inara Read API (getCommunityGoalsRecent) as an automatic fallback when Frontier cAPI is unavailable.
    /// Gated by AppConfig.EnableInaraSync.
    /// </summary>
    public async Task<(bool Success, List<CommunityGoalItem> Goals, string Message)> FetchInaraCommunityGoalsAsync(CancellationToken ct = default)
    {
        var cfg = _configService.Current;
        if (!cfg.EnableInaraSync)
            return (false, new List<CommunityGoalItem>(), "Inara integration is disabled.");

        string cmdr = CleanCommanderName(cfg.InaraCommanderName, _fileWatcher.State.CommanderName);
        string apiKey = string.IsNullOrWhiteSpace(cfg.InaraApiKey) ? "anonymous" : cfg.InaraApiKey.Trim();

        var payload = new
        {
            header = new
            {
                appName = SoftwareName,
                appVersion = SoftwareVersion,
                isBeingDeveloped = false,
                APIkey = apiKey,
                commanderName = string.IsNullOrWhiteSpace(cmdr) ? "Commander" : cmdr
            },
            events = new[]
            {
                new
                {
                    eventName = "getCommunityGoalsRecent",
                    eventTimestamp = DateTimeOffset.UtcNow.ToString("O"),
                    eventData = new { }
                }
            }
        };

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _httpClient.PostAsync(InaraEndpoint, content, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, new List<CommunityGoalItem>(), $"Inara CG HTTP {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var goals = new List<CommunityGoalItem>();
            if (doc.RootElement.TryGetProperty("events", out var evArr) && evArr.ValueKind == JsonValueKind.Array && evArr.GetArrayLength() > 0 &&
                evArr[0].TryGetProperty("eventData", out var cgArr) && cgArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in cgArr.EnumerateArray())
                {
                    if (g.ValueKind != JsonValueKind.Object) continue;
                    bool isCompleted = g.TryGetProperty("isCompleted", out var ic) && ic.ValueKind == JsonValueKind.True;
                    if (isCompleted) continue;
                    string title = GetString(g, "communitygoalName") ?? "";
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    string expRaw = GetString(g, "goalExpiry") ?? "";
                    string expDisp = DateTimeOffset.TryParse(expRaw, out var dt)
                        ? dt.ToLocalTime().ToString("dd/MM HH:mm", System.Globalization.CultureInfo.CurrentCulture)
                        : expRaw;

                    goals.Add(new CommunityGoalItem
                    {
                        Id = GetLong(g, "communitygoalGameID") ?? 0,
                        Title = title,
                        StarSystem = GetString(g, "starsystemName") ?? "",
                        StationName = GetString(g, "stationName") ?? "",
                        ExpiryDisplay = expDisp,
                        CurrentTotal = GetLong(g, "contributionsTotal") ?? 0,
                        PlayerContribution = 0
                    });
                }
            }

            if (goals.Count > 0)
            {
                _fileWatcher.MergeCommunityGoals(goals, isAuthoritative: false);
            }

            return (true, goals, $"Inara CGs: {goals.Count}");
        }
        catch (Exception ex)
        {
            return (false, new List<CommunityGoalItem>(), $"Inara CG error: {ex.Message}");
        }
    }

    // =========================================================================
    // Inara API v1 (Prioridad 3: Cola por lotes 35 s + eventos completos)
    // =========================================================================

    private void EnqueueInaraEvents(GameJournalEvent gameEvent, ShipState state)
    {
        var mapped = MapJournalToInaraEvents(gameEvent, state);
        if (mapped.Count == 0)
            return;

        // EDDI-style dirty tracking: avoid re-sending identical Materials/Loadout payloads
        mapped = mapped.Where(ev =>
        {
            string name = ev["eventName"] as string ?? "";
            if (name == "setCommanderInventoryMaterials")
            {
                string fp = JsonSerializer.Serialize(ev["eventData"]);
                if (fp == _lastInaraMaterialsFingerprint) return false;
                _lastInaraMaterialsFingerprint = fp;
            }
            else if (name == "setCommanderShipLoadout")
            {
                string fp = JsonSerializer.Serialize(ev["eventData"]);
                if (fp == _lastInaraLoadoutFingerprint) return false;
                _lastInaraLoadoutFingerprint = fp;
            }
            return true;
        }).ToList();

        if (mapped.Count == 0)
            return;

        lock (_queueLock)
        {
            _inaraQueue.AddRange(mapped);
            if (_inaraQueue.Count > 250)
                _inaraQueue.RemoveRange(0, _inaraQueue.Count - 250);
        }
    }

    private static List<Dictionary<string, object>> MapJournalToInaraEvents(GameJournalEvent gameEvent, ShipState state)
    {
        var list = new List<Dictionary<string, object>>();
        var data = gameEvent.Data;
        string ts = gameEvent.Timestamp.ToUniversalTime().ToString("O");

        void Add(string eventName, object eventData) => list.Add(new Dictionary<string, object>
        {
            ["eventName"] = eventName,
            ["eventTimestamp"] = ts,
            ["eventData"] = eventData
        });

        switch (gameEvent.EventType)
        {
            case "LoadGame":
                if (GetLong(data, "Credits") is long lgCreds)
                    Add("setCommanderCredits", new { commanderCredits = lgCreds });
                break;

            case "Location":
                {
                    string sys = GetString(data, "StarSystem") ?? state.CurrentSystem;
                    if (!string.IsNullOrWhiteSpace(sys))
                    {
                        var ev = new Dictionary<string, object> { ["starsystemName"] = sys };
                        if (state.StarPos is { Length: 3 }) ev["starsystemCoords"] = state.StarPos;
                        if (GetString(data, "StationName") is string st && !string.IsNullOrWhiteSpace(st))
                            ev["stationName"] = st;
                        if (GetLong(data, "MarketID") is long mid) ev["marketID"] = mid;
                        Add("addCommanderTravelLocation", ev);
                    }
                }
                break;

            case "FSDJump":
                {
                    string sys = GetString(data, "StarSystem") ?? state.CurrentSystem;
                    if (!string.IsNullOrWhiteSpace(sys))
                    {
                        var ev = new Dictionary<string, object> { ["starsystemName"] = sys };
                        if (state.StarPos is { Length: 3 }) ev["starsystemCoords"] = state.StarPos;
                        if (GetDouble(data, "JumpDist") is double dist) ev["jumpDistance"] = dist;
                        if (state.ShipTypeInternal.Length > 0) ev["shipType"] = state.ShipTypeInternal;
                        if (state.ShipId.HasValue) ev["shipGameID"] = state.ShipId.Value;
                        Add("addCommanderTravelFSDJump", ev);
                    }
                }
                break;

            case "Docked":
                {
                    string sys = GetString(data, "StarSystem") ?? state.CurrentSystem;
                    string st = GetString(data, "StationName") ?? state.StationName;
                    if (!string.IsNullOrWhiteSpace(sys) && !string.IsNullOrWhiteSpace(st))
                    {
                        var ev = new Dictionary<string, object>
                        {
                            ["starsystemName"] = sys,
                            ["stationName"] = st
                        };
                        if (GetLong(data, "MarketID") is long mid) ev["marketID"] = mid;
                        if (state.ShipTypeInternal.Length > 0) ev["shipType"] = state.ShipTypeInternal;
                        if (state.ShipId.HasValue) ev["shipGameID"] = state.ShipId.Value;
                        Add("addCommanderTravelDock", ev);
                    }
                }
                break;

            case "CarrierJump":
                {
                    string sys = GetString(data, "StarSystem") ?? state.CurrentSystem;
                    if (!string.IsNullOrWhiteSpace(sys))
                    {
                        var ev = new Dictionary<string, object> { ["starsystemName"] = sys };
                        if (GetString(data, "StationName") is string st) ev["stationName"] = st;
                        if (GetLong(data, "MarketID") is long mid) ev["marketID"] = mid;
                        Add("addCommanderTravelCarrierJump", ev);
                    }
                }
                break;

            case "Rank":
                foreach (var (prop, inaraName) in new[]
                {
                    ("Combat", "combat"), ("Trade", "trade"), ("Explore", "explore"),
                    ("Soldier", "soldier"), ("Exobiologist", "exobiologist"),
                    ("Empire", "empire"), ("Federation", "federation"), ("CQC", "cqc")
                })
                {
                    if (GetInt(data, prop) is int rankVal)
                        Add("setCommanderRankPilot", new { rankName = inaraName, rankValue = rankVal });
                }
                break;

            case "Reputation":
                foreach (var (prop, majorName) in new[]
                {
                    ("Empire", "empire"), ("Federation", "federation"), ("Alliance", "alliance"), ("Independent", "independent")
                })
                {
                    if (GetDouble(data, prop) is double repVal)
                        Add("setCommanderReputationMajorFaction", new { majorfactionName = majorName, majorfactionReputation = Math.Round(repVal / 100.0, 4) });
                }
                break;

            case "EngineerProgress":
                if (data.TryGetProperty("Engineers", out var engArr) && engArr.ValueKind == JsonValueKind.Array)
                {
                    var engList = new List<object>();
                    foreach (var eng in engArr.EnumerateArray())
                    {
                        string? name = GetString(eng, "Engineer");
                        string? stage = GetString(eng, "Progress");
                        if (string.IsNullOrWhiteSpace(name))
                            continue;
                        var item = new Dictionary<string, object> { ["engineerName"] = name };
                        if (!string.IsNullOrWhiteSpace(stage)) item["rankStage"] = stage;
                        if (GetInt(eng, "Rank") is int rk) item["rankValue"] = rk;
                        engList.Add(item);
                    }
                    if (engList.Count > 0)
                        Add("setCommanderRankEngineer", engList);
                }
                break;

            case "Materials":
                {
                    var mats = new List<object>();
                    foreach (string cat in new[] { "Raw", "Manufactured", "Encoded" })
                    {
                        if (data.TryGetProperty(cat, out var arr) && arr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var m in arr.EnumerateArray())
                            {
                                string? name = GetString(m, "Name");
                                int? count = GetInt(m, "Count");
                                if (!string.IsNullOrWhiteSpace(name) && count.HasValue)
                                    mats.Add(new { itemName = name, itemCount = count.Value });
                            }
                        }
                    }
                    if (mats.Count > 0)
                        Add("setCommanderInventoryMaterials", mats);
                }
                break;

            case "Cargo":
                if (data.TryGetProperty("Inventory", out var invArr) && invArr.ValueKind == JsonValueKind.Array)
                {
                    var cargo = new List<object>();
                    foreach (var c in invArr.EnumerateArray())
                    {
                        string? name = GetString(c, "Name");
                        int? count = GetInt(c, "Count");
                        if (!string.IsNullOrWhiteSpace(name) && count.HasValue)
                            cargo.Add(new { itemName = name, itemCount = count.Value, isStolen = (GetInt(c, "Stolen") ?? 0) > 0 });
                    }
                    Add("setCommanderInventoryCargo", cargo);
                }
                break;

            case "Loadout":
                {
                    string? shipType = GetString(data, "Ship");
                    long? shipId = GetLong(data, "ShipID");
                    if (!string.IsNullOrWhiteSpace(shipType) && shipId.HasValue)
                    {
                        var shipInfo = new Dictionary<string, object>
                        {
                            ["shipType"] = shipType,
                            ["shipGameID"] = shipId.Value,
                            ["isCurrentShip"] = true
                        };
                        if (GetString(data, "ShipName") is string sn && !string.IsNullOrWhiteSpace(sn))
                            shipInfo["shipName"] = sn;
                        if (GetString(data, "ShipIdent") is string si && !string.IsNullOrWhiteSpace(si))
                            shipInfo["shipIdent"] = si;
                        Add("setCommanderShip", shipInfo);

                        if (data.TryGetProperty("Modules", out var mods) && mods.ValueKind == JsonValueKind.Array)
                        {
                            var loadout = new List<object>();
                            foreach (var mod in mods.EnumerateArray())
                            {
                                string? slot = GetString(mod, "Slot");
                                string? item = GetString(mod, "Item");
                                if (string.IsNullOrWhiteSpace(slot) || string.IsNullOrWhiteSpace(item))
                                    continue;
                                var mObj = new Dictionary<string, object>
                                {
                                    ["slotName"] = slot,
                                    ["itemName"] = item,
                                    ["isOn"] = !mod.TryGetProperty("On", out var onP) || onP.ValueKind != JsonValueKind.False
                                };
                                if (GetInt(mod, "Priority") is int pr) mObj["itemPriority"] = pr;
                                if (GetDouble(mod, "Health") is double hl) mObj["itemHealth"] = hl;
                                loadout.Add(mObj);
                            }
                            if (loadout.Count > 0)
                                Add("setCommanderShipLoadout", new { shipType, shipGameID = shipId.Value, shipLoadout = loadout });
                        }
                    }
                }
                break;

            case "MissionAccepted":
                if (GetLong(data, "MissionID") is long accId && GetString(data, "Name") is string accName)
                {
                    var m = new Dictionary<string, object>
                    {
                        ["missionName"] = accName,
                        ["missionGameID"] = accId,
                        ["starsystemNameOrigin"] = state.CurrentSystem
                    };
                    if (GetString(data, "DestinationSystem") is string dst) m["starsystemNameTarget"] = dst;
                    if (GetString(data, "Faction") is string fac) m["minorfactionNameOrigin"] = fac;
                    Add("addCommanderMission", m);
                }
                break;

            case "MissionCompleted":
                if (GetLong(data, "MissionID") is long cmpId)
                {
                    var m = new Dictionary<string, object> { ["missionGameID"] = cmpId };
                    if (GetLong(data, "Reward") is long rw) m["rewardCredits"] = rw;
                    Add("setCommanderMissionCompleted", m);
                }
                break;

            case "MissionAbandoned":
                if (GetLong(data, "MissionID") is long abId)
                    Add("setCommanderMissionAbandoned", new { missionGameID = abId });
                break;

            case "MissionFailed":
                if (GetLong(data, "MissionID") is long flId)
                    Add("setCommanderMissionFailed", new { missionGameID = flId });
                break;

            case "PVPKill":
                if (GetString(data, "Victim") is string victim)
                    Add("addCommanderCombatKill", new { starsystemName = state.CurrentSystem, opponentName = victim });
                break;

            case "Died":
                Add("addCommanderCombatDeath", new { starsystemName = state.CurrentSystem });
                break;
        }

        return list;
    }

    private async Task<string?> FlushInaraQueueAsync(AppConfig cfg, ShipState state, bool force)
    {
        if (!cfg.EnableInaraSync || string.IsNullOrWhiteSpace(cfg.InaraApiKey) || state.IsLegacyGalaxy)
            return null;

        if (!force && (DateTimeOffset.UtcNow - _lastInaraFlushUtc).TotalSeconds < 32)
            return null;

        List<Dictionary<string, object>> batch;
        lock (_queueLock)
        {
            if (_inaraQueue.Count == 0)
                return null;
            batch = new List<Dictionary<string, object>>(_inaraQueue);
            _inaraQueue.Clear();
        }

        string cmdr = CleanCommanderName(cfg.InaraCommanderName, state.CommanderName);
        if (string.IsNullOrWhiteSpace(cmdr))
            return null;

        _lastInaraFlushUtc = DateTimeOffset.UtcNow;

        try
        {
            var payload = new
            {
                header = new
                {
                    appName = SoftwareName,
                    appVersion = SoftwareVersion,
                    isBeingDeveloped = false,
                    APIkey = cfg.InaraApiKey,
                    commanderName = cmdr
                },
                events = batch
            };

            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _httpClient.PostAsync(InaraEndpoint, content);
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode >= 500)
                    RequeueInaraBatch(batch);
                return $"Inara HTTP {(int)resp.StatusCode}";
            }

            string respBody = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(respBody);
            int headerStatus = doc.RootElement.TryGetProperty("header", out var hdr)
                ? (GetInt(hdr, "eventStatus") ?? 200)
                : 200;

            if (headerStatus is 200 or 202 or 204)
            {
                InaraUploadedCount += batch.Count;
                UpdateStatus($"Inara OK ({batch.Count} ev · {DateTime.Now:HH:mm:ss})", increment: true);
                return $"Inara OK ({batch.Count} ev)";
            }

            string statusText = hdr.ValueKind == JsonValueKind.Object
                ? (GetString(hdr, "eventStatusText") ?? $"status {headerStatus}")
                : $"status {headerStatus}";
            return $"Inara: {statusText}";
        }
        catch (Exception ex)
        {
            RequeueInaraBatch(batch);
            return $"Inara: {ex.Message}";
        }
    }

    private void RequeueInaraBatch(List<Dictionary<string, object>> batch)
    {
        lock (_queueLock)
        {
            _inaraQueue.InsertRange(0, batch);
            if (_inaraQueue.Count > 250)
                _inaraQueue.RemoveRange(250, _inaraQueue.Count - 250);
        }
    }

    public async Task<(bool Success, string Summary)> SyncCommanderAsync()
    {
        var cfg = _configService.Current;
        var state = _fileWatcher.State;

        // If the user clicks "Sync Now" and the Inara queue is empty, enqueue current location/credits
        if (cfg.EnableInaraSync && !string.IsNullOrWhiteSpace(cfg.InaraApiKey) && !state.IsLegacyGalaxy)
        {
            lock (_queueLock)
            {
                if (_inaraQueue.Count == 0)
                {
                    string ts = DateTimeOffset.UtcNow.ToString("O");
                    _inaraQueue.Add(new Dictionary<string, object>
                    {
                        ["eventName"] = "setCommanderCredits",
                        ["eventTimestamp"] = ts,
                        ["eventData"] = new { commanderCredits = state.Credits }
                    });
                    if (!string.IsNullOrWhiteSpace(state.CurrentSystem))
                    {
                        var loc = new Dictionary<string, object> { ["starsystemName"] = state.CurrentSystem };
                        if (state.StarPos is { Length: 3 }) loc["starsystemCoords"] = state.StarPos;
                        _inaraQueue.Add(new Dictionary<string, object>
                        {
                            ["eventName"] = "addCommanderTravelLocation",
                            ["eventTimestamp"] = ts,
                            ["eventData"] = loc
                        });
                    }
                }
            }
        }

        return await FlushQueuesAsync(forceInara: true);
    }

    private async Task<(bool Success, string Summary)> FlushQueuesAsync(bool forceInara)
    {
        if (!await _flushGate.WaitAsync(0))
            return (true, LastSyncStatus);

        try
        {
            var cfg = _configService.Current;
            var state = _fileWatcher.State;
            var results = new List<string>();

            if (await FlushEdsmQueueAsync(cfg, state) is string edsmRes)
                results.Add(edsmRes);
            if (await FlushInaraQueueAsync(cfg, state, forceInara) is string inaraRes)
                results.Add(inaraRes);

            if (results.Count == 0)
            {
                string idle = cfg.EnableEddnUpload ? "EDDN activo" : "Sin eventos pendientes";
                return (true, idle);
            }

            string joined = $"{string.Join(" · ", results)} ({DateTime.Now:HH:mm:ss})";
            bool ok = results.Any(static r => r.Contains("OK", StringComparison.Ordinal));
            UpdateStatus(joined, increment: false);
            return (ok, joined);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    // =========================================================================
    // Utilidades JSON
    // =========================================================================

    private static string CleanCommanderName(string configured, string fallback) =>
        HttpClientHeaderDefaults.CleanCommanderName(!string.IsNullOrWhiteSpace(configured) ? configured : fallback);

    internal static Dictionary<string, object?> SanitizeElementToDictionary(JsonElement obj, bool preserveLatLon = false)
    {
        var dict = new Dictionary<string, object?>();
        if (obj.ValueKind != JsonValueKind.Object)
            return dict;
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.EndsWith("_Localised", StringComparison.OrdinalIgnoreCase))
                continue;
            if (EddnForbiddenKeys.Contains(prop.Name) &&
                !(preserveLatLon && (prop.Name.Equals("Latitude", StringComparison.OrdinalIgnoreCase) ||
                                     prop.Name.Equals("Longitude", StringComparison.OrdinalIgnoreCase))))
                continue;
            dict[prop.Name] = SanitizeJsonValue(prop.Value, preserveLatLon);
        }
        return dict;
    }

    private static object? SanitizeJsonValue(JsonElement el, bool preserveLatLon = false) => el.ValueKind switch
    {
        JsonValueKind.Object => SanitizeElementToDictionary(el, preserveLatLon),
        JsonValueKind.Array => el.EnumerateArray().Select(item => SanitizeJsonValue(item, preserveLatLon)).ToList(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out long l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static Dictionary<string, object?> ConvertElementToDictionary(JsonElement obj)
    {
        var dict = new Dictionary<string, object?>();
        if (obj.ValueKind != JsonValueKind.Object)
            return dict;
        foreach (var prop in obj.EnumerateObject())
            dict[prop.Name] = ConvertJsonValue(prop.Value);
        return dict;
    }

    private static object? ConvertJsonValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => ConvertElementToDictionary(el),
        JsonValueKind.Array => el.EnumerateArray().Select(ConvertJsonValue).ToList(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out long l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static string? GetString(JsonElement el, string prop) => GetStr(el, prop);

    private void UpdateStatus(string status, bool increment)
    {
        lock (_statusLock)
        {
            if (increment)
                SyncedEventsCount++;
            LastSyncStatus = status;
        }
        SyncStatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _gameEvents.JournalEventReceived -= OnJournalEventReceived;
        _fileWatcher.SnapshotFileUpdated -= OnSnapshotFileUpdated;
        try { _flushLoopCts.Cancel(); } catch (ObjectDisposedException) { }
        _flushTimer.Dispose();
        _flushLoopCts.Dispose();
        try
        {
            Task.Run(() => FlushQueuesAsync(forceInara: true)).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore timeout on shutdown
        }
        _httpClient.Dispose();
        _flushGate.Dispose();
    }
}

public sealed record EdsmSystemIntel(
    string SystemName,
    int TrafficDay,
    int TrafficWeek,
    int DeathsDay,
    int DeathsWeek,
    string RemoteComment);
