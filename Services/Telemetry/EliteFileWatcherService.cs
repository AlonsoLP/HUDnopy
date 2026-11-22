// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/Telemetry/EliteFileWatcherService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Core Elite Dangerous directory watcher, process detector, and live state publisher.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Timers;
using HUDnopy.Models;
using HUDnopy.Localization;

namespace HUDnopy.Services;

/// <summary>
/// Core Elite Dangerous directory watcher, process detector, and live state publisher.
/// </summary>
public partial class EliteFileWatcherService : IDisposable
{
    private readonly ConfigService _configService;
    private readonly GameEventHub _gameEvents;
    private readonly Lock _refreshLock = new();
    private FileSystemWatcher? _watcher;
    private readonly PeriodicTimer _pollTimer;
    private readonly CancellationTokenSource _pollCts = new();
    private string _currentJournalFile = "";
    private long _lastJournalPosition = 0;
    private readonly Dictionary<string, MaterialItem> _materialsInventory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MissionItem> _activeMissions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExplorationBodyItem> _explorationBodies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _explorationBodyOrder = new();
    private readonly HashSet<string> _explorationHistoryResolvedSystems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Length, long LastWriteUtcTicks)> _snapshotFileStamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _unhandledLock = new();
    private readonly Dictionary<string, UnhandledGameMessageItem> _unhandledGrouped = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UnhandledGameMessageItem> _unhandledHistory = new();
    private readonly HashSet<string> _recordedUnhandledKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Length, long LastWriteUtcTicks)> _unhandledFileStamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(double Lat, double Lon)> _activeExoSampleCoords = new();
    private readonly Dictionary<string, long> _unsoldCartographyByBody = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unsoldExobiologyKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ShipModuleItem> _shipModulesBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EngineerProgressItem> _engineersByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _miningRefinedByMineral = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _miningSessionStartedUtc;
    private int _asteroidsProspected;
    private readonly Dictionary<string, (int Kills, long Credits)> _combatByFaction = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _combatSessionStartedUtc;
    private DateTimeOffset? _sessionStartedUtc;
    private long? _sessionStartCredits;
    private readonly HashSet<string> _sessionVisitedSystems = new(StringComparer.OrdinalIgnoreCase);
    private double _sessionDistanceLy;
    private int _sessionBodiesScanned;
    private int _sessionOrganicsAnalysed;
    private int _sessionMissionsCompleted;
    private bool _unhandledBatchDirty;
    private bool _hasMaterialSnapshot;
    private bool _hasCompletedInitialJournalLoad;
    private bool? _journalSessionActive;
    private DateTime? _processMissingSinceUtc;
    private string? _currentSystemAddress;
    private ShipState _state = new();
    private ShipState _publishedState = new();
    private bool _explorationBodiesChanged;
    private bool _materialsChanged;
    private bool _missionsChanged;
    private int _watcherRefreshPending;
    private static int? _cachedGameProcessId;
    private static long _lastProcessScanTick;
    private static readonly System.Net.Http.HttpClient EdsmShipyardHttp = HttpClientHeaderDefaults.CreateHttpClient(TimeSpan.FromSeconds(10));
    private readonly InputSimulatorService _inputSimulator;
    private string _lastEdsmShipyardLookupKey = "";
    private string _lastModuleLocalizationLang = "";
    private bool _stateDirty = true;
    private DateTime _lastHeartbeatPublishUtc = DateTime.MinValue;

    public ShipState State => Volatile.Read(ref _publishedState);
    public string UnhandledEventsFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unhandled_events.jsonl");
    public event Action<ShipState>? StateUpdated;
    public event Action? UnhandledMessagesUpdated;
    public event Action<string, JsonElement>? SnapshotFileUpdated;

    public EliteFileWatcherService(
        ConfigService configService,
        InputSimulatorService inputSimulator,
        GameEventHub gameEvents)
    {
        _configService = configService;
        _inputSimulator = inputSimulator;
        _gameEvents = gameEvents;
        _configService.ConfigChanged += OnConfigChanged;
        _inputSimulator.CommandExecuted += OnCommandExecuted;

        LoadUnhandledEventsFromFile();
        if (_configService.Current.CaptureUnhandledEvents)
            ScanCurrentFolderForUnhandledMessages(notify: false);

        _pollTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(1200));

        InitializeWatcher();
        RefreshState(forcePublish: true);
        _ = RunPollLoopAsync();
    }

    private async System.Threading.Tasks.Task RunPollLoopAsync()
    {
        try
        {
            while (await _pollTimer.WaitForNextTickAsync(_pollCts.Token))
                RefreshState(forcePublish: false);
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown
        }
    }

    private void OnConfigChanged(AppConfig cfg)
    {
        lock (_refreshLock)
        {
            InitializeWatcher();
            if (_materialsInventory.Count > 0)
                _materialsChanged = true;
        }
        RefreshState();
    }

    private void InitializeWatcher()
    {
        try
        {
            _watcher?.Dispose();
            _watcher = null;

            string path = _configService.Current.JournalFolderPath;
            if (!Directory.Exists(path)) return;

            _watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                Filter = "*.*",
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
        }
        catch
        {
            // The polling timer will be used as fallback
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (e.Name == null) return;
        if (IsHandledSnapshotFileName(e.Name) ||
            e.Name.StartsWith("Journal.", StringComparison.OrdinalIgnoreCase) ||
            (_configService.Current.CaptureUnhandledEvents && e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            if (Interlocked.Exchange(ref _watcherRefreshPending, 1) == 0)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(40).ConfigureAwait(false);
                    Interlocked.Exchange(ref _watcherRefreshPending, 0);
                    RefreshState(forcePublish: false);
                });
            }
        }
    }

    internal void RefreshStateForTest()
    {
        _journalSessionActive = true;
        RefreshState(forcePublish: true);
    }

    public void RefreshState(bool forcePublish = true)
    {
        lock (_refreshLock)
        {
            var cfg = _configService.Current;
            if (!string.Equals(_state.ThemeAccentHex, cfg.ThemeAccentHex, StringComparison.Ordinal) ||
                !string.Equals(_state.ThemeSecondaryHex, cfg.ThemeSecondaryHex, StringComparison.Ordinal))
            {
                _state.ThemeAccentHex = cfg.ThemeAccentHex;
                _state.ThemeSecondaryHex = cfg.ThemeSecondaryHex;
                _stateDirty = true;
            }

            string folder = cfg.JournalFolderPath;
            long prevJournalPos = _lastJournalPosition;
            string prevJournalFile = _currentJournalFile;
            bool prevGameDetected = _state.IsGameDetected;
            bool isGameProcessRunning = IsEliteDangerousProcessRunning();
            if (isGameProcessRunning || _journalSessionActive == true)
                ReadLatestJournal(folder);
            if (_lastJournalPosition != prevJournalPos || !string.Equals(_currentJournalFile, prevJournalFile, StringComparison.OrdinalIgnoreCase))
                _stateDirty = true;
            if (_materialsChanged)
            {
                SyncMaterialsToState();
                _materialsChanged = false;
                _stateDirty = true;
            }

            if (_journalSessionActive == true)
            {
                if (isGameProcessRunning)
                {
                    _processMissingSinceUtc = null;
                }
                else
                {
                    _processMissingSinceUtc ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - _processMissingSinceUtc.Value >=
                        TimeSpan.FromSeconds(Math.Clamp(cfg.GameProcessExitGraceSeconds, 1, 300)))
                    {
                        _journalSessionActive = false;
                    }
                }
            }
            else
            {
                _processMissingSinceUtc = null;
            }

            bool isGameSessionActive = _journalSessionActive == true;
            if (isGameSessionActive)
            {
                _state.IsSimulation = false;
                _state.IsGameDetected = true;
                ReadStatusJson(Path.Combine(folder, "Status.json"));
                ReadNavRouteJson(Path.Combine(folder, "NavRoute.json"));
                ReadCargoJson(Path.Combine(folder, "Cargo.json"));
                ReadMarketJson(Path.Combine(folder, "Market.json"));
                SyncMarketColonisationNeeds();
                ReadOutfittingJson(Path.Combine(folder, "Outfitting.json"));
                RelocalizeModuleNames();
                ReadShipyardJson(Path.Combine(folder, "Shipyard.json"));
                TryEnsureEdsmShipyardFallback();
                ReadModulesInfoJson(Path.Combine(folder, "ModulesInfo.json"));
                ReadShipLockerJson(Path.Combine(folder, "ShipLocker.json"));
                ReadCarrierMaterialsJson(Path.Combine(folder, "FCMaterials.json"));
                SyncCurrentSystemNote();
                SyncSessionStatsToState();
                if (cfg.CaptureUnhandledEvents)
                    ReadUnhandledJsonFiles(folder);
            }
            else
            {
                _state.IsSimulation = false;
                _state.IsGameDetected = false;
            }

            FlushUnhandledBatchIfNeeded();
            if (_state.IsGameDetected != prevGameDetected)
                _stateDirty = true;

            DateTime nowUtc = DateTime.UtcNow;
            bool carrierCountdownActive = _state.Carrier?.DepartureTime is DateTimeOffset dep && dep > nowUtc;
            bool heartbeatDue = isGameSessionActive && (nowUtc - _lastHeartbeatPublishUtc).TotalSeconds >= 15;

            if (forcePublish || _stateDirty || carrierCountdownActive || heartbeatDue)
            {
                _stateDirty = false;
                _lastHeartbeatPublishUtc = nowUtc;
                _state.LastUpdated = nowUtc;
                PublishState();
            }
        }
    }

    private void PublishState()
    {
        var snapshot = _state.Snapshot();
        Volatile.Write(ref _publishedState, snapshot);
        StateUpdated?.Invoke(snapshot);
    }

    private static bool IsEliteProcessName(string processName) =>
        processName.Contains("EliteDangerous", StringComparison.OrdinalIgnoreCase) ||
        (processName.Contains("Elite", StringComparison.OrdinalIgnoreCase) &&
         processName.Contains("Dangerous", StringComparison.OrdinalIgnoreCase));

    private static bool IsEliteDangerousProcessRunning()
    {
        if (_cachedGameProcessId is int cachedPid)
        {
            try
            {
                using var cachedProcess = Process.GetProcessById(cachedPid);
                if (!cachedProcess.HasExited && IsEliteProcessName(cachedProcess.ProcessName))
                    return true;
            }
            catch (ArgumentException)
            {
                // Process has already exited.
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return true;
            }

            _cachedGameProcessId = null;
        }
        else
        {
            long now = Environment.TickCount64;
            long lastScan = Volatile.Read(ref _lastProcessScanTick);
            if (lastScan != 0 && now - lastScan < 2000)
                return false;
            Volatile.Write(ref _lastProcessScanTick, now);
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }

        bool processFound = false;
        bool scanIncomplete = false;
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (!processFound && IsEliteProcessName(process.ProcessName))
                    {
                        processFound = true;
                        _cachedGameProcessId = process.Id;
                    }
                }
                catch (InvalidOperationException)
                {
                    // El proceso pudo cerrarse mientras se enumeraba.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    scanIncomplete = true;
                }
            }
        }

        return processFound || scanIncomplete;
    }

    private void OnCommandExecuted(string actionId, string displayName)
    {
        lock (_refreshLock)
        {
            if (!_state.IsGameDetected) return;

            AddLocalizedJournalEntry("Comando", "command", null, displayName, actionId);
            _state.LastUpdated = DateTime.UtcNow;
            PublishState();
        }
    }


    private void SyncSessionStatsToState()
    {
        if (_sessionStartedUtc is null && _state.IsGameDetected)
            _sessionStartedUtc = DateTimeOffset.UtcNow;
        if (_sessionStartCredits is null && _state.Credits > 0)
            _sessionStartCredits = _state.Credits;

        long net = _sessionStartCredits.HasValue ? _state.Credits - _sessionStartCredits.Value : 0;
        long crPerHour = 0;
        if (_sessionStartedUtc.HasValue && net != 0)
        {
            double hours = Math.Max(1.0 / 60.0, (DateTimeOffset.UtcNow - _sessionStartedUtc.Value).TotalHours);
            crPerHour = (long)Math.Round(net / hours);
        }

        _state.SessionStats = new SessionStatsState
        {
            StartedAtUtc = _sessionStartedUtc,
            StartCredits = _sessionStartCredits,
            NetCredits = net,
            CreditsPerHour = crPerHour,
            SystemsVisited = _sessionVisitedSystems.Count,
            TotalDistanceLy = Math.Round(_sessionDistanceLy, 1),
            BodiesScanned = _sessionBodiesScanned,
            OrganicsAnalysed = _sessionOrganicsAnalysed,
            MissionsCompleted = _sessionMissionsCompleted
        };
    }

    public void ResetSessionStats()
    {
        lock (_refreshLock)
        {
            _sessionStartedUtc = DateTimeOffset.UtcNow;
            _sessionStartCredits = _state.Credits;
            _sessionVisitedSystems.Clear();
            if (!string.IsNullOrWhiteSpace(_state.CurrentSystem))
                _sessionVisitedSystems.Add(_state.CurrentSystem);
            _sessionDistanceLy = 0;
            _sessionBodiesScanned = 0;
            _sessionOrganicsAnalysed = 0;
            _sessionMissionsCompleted = 0;
            SyncSessionStatsToState();
            PublishState();
        }
    }

    public void SyncCurrentSystemNote()
    {
        string sys = _state.CurrentSystem ?? "";
        if (!string.IsNullOrWhiteSpace(sys) &&
            _configService.Current.SystemNotes.TryGetValue(sys, out string? note))
        {
            _state.CurrentSystemNote = note ?? "";
        }
        else
        {
            _state.CurrentSystemNote = "";
        }
    }

    public void ApplyCapiEnrichment(Action<ShipState> mutator)
    {
        lock (_refreshLock)
        {
            mutator(_state);
            SyncMarketColonisationNeeds();
            _state.LastUpdated = DateTime.UtcNow;
            PublishState();
        }
    }


    private void RecordUnhandledMessage(
        string source,
        string eventType,
        string timestamp,
        string rawJson,
        string? occurrenceKey = null)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            return;

        string normalizedJson = rawJson.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        string ts = string.IsNullOrWhiteSpace(timestamp)
            ? DateTime.Now.ToString("HH:mm:ss")
            : timestamp;

        lock (_unhandledLock)
        {
            if (!string.IsNullOrEmpty(occurrenceKey) && !_recordedUnhandledKeys.Add(occurrenceKey))
                return;

            string groupKey = $"{source}:{eventType}";
            if (_unhandledGrouped.TryGetValue(groupKey, out var existing))
            {
                existing.Count++;
                existing.Timestamp = ts;
                existing.RawJson = normalizedJson;
            }
            else
            {
                _unhandledGrouped[groupKey] = new UnhandledGameMessageItem
                {
                    Timestamp = ts,
                    Source = source,
                    EventType = eventType,
                    Count = 1,
                    RawJson = normalizedJson
                };
            }

            _unhandledHistory.Insert(0, new UnhandledGameMessageItem
            {
                Timestamp = ts,
                Source = source,
                EventType = eventType,
                Count = 1,
                RawJson = normalizedJson
            });
            if (_unhandledHistory.Count > 500)
                _unhandledHistory.RemoveAt(_unhandledHistory.Count - 1);

            _unhandledBatchDirty = true;
        }
    }

    private void FlushUnhandledBatchIfNeeded(bool notify = true)
    {
        bool shouldNotify = false;
        lock (_unhandledLock)
        {
            if (_unhandledBatchDirty)
            {
                _unhandledBatchDirty = false;
                shouldNotify = true;
                if (_configService.Current.WriteUnhandledEventsToFile)
                    SaveUnhandledEventsToFileCore();
            }
        }

        if (notify && shouldNotify)
            UnhandledMessagesUpdated?.Invoke();
    }

    private void ReadUnhandledJsonFiles(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return;

            foreach (var info in new DirectoryInfo(folder).EnumerateFiles("*.json"))
            {
                if (IsHandledSnapshotFileName(info.Name))
                    continue;

                if (_unhandledFileStamps.TryGetValue(info.FullName, out var stamp) &&
                    stamp.Length == info.Length &&
                    stamp.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
                {
                    continue;
                }

                try
                {
                    using var fs = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    string rawContent = reader.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(rawContent))
                        continue;

                    using var doc = JsonDocument.Parse(rawContent);
                    var root = doc.RootElement;
                    string evt = ReadJsonString(root, "event", Path.GetFileNameWithoutExtension(info.Name));
                    string ts = ReadJsonTimestamp(root, "timestamp")?.ToLocalTime().ToString("HH:mm:ss")
                        ?? info.LastWriteTime.ToString("HH:mm:ss");

                    using var compactStream = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(compactStream))
                    {
                        root.WriteTo(writer);
                    }
                    string compactJson = Encoding.UTF8.GetString(compactStream.ToArray());

                    _unhandledFileStamps[info.FullName] = (info.Length, info.LastWriteTimeUtc.Ticks);
                    RecordUnhandledMessage(
                        info.Name,
                        evt,
                        ts,
                        compactJson,
                        $"File:{info.Name}:{info.LastWriteTimeUtc.Ticks}:{info.Length}");
                }
                catch
                {
                    // Ignorar archivo en escritura o JSON parcial
                }
            }
        }
        catch
        {
            // Ignorar errores de acceso a carpeta
        }
    }

    public int ScanCurrentFolderForUnhandledMessages(bool notify = true)
    {
        string folder = _configService.Current.JournalFolderPath;
        try
        {
            if (Directory.Exists(folder))
            {
                var latestJournal = new DirectoryInfo(folder).EnumerateFiles("Journal.*.log")
                    .OrderByDescending(static info => info.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (latestJournal != null)
                {
                    string journalFileName = latestJournal.Name;
                    using var fs = new FileStream(latestJournal.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    foreach (var (line, endPosition) in ReadCompleteJournalLines(fs))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            if (!root.TryGetProperty("event", out var evtProp))
                                continue;
                            string evt = evtProp.GetString() ?? "";
                            if (IsJournalEventHandled(evt, root))
                                continue;
                            string ts = ReadJsonTimestamp(root, "timestamp")?.ToLocalTime().ToString("HH:mm:ss")
                                ?? latestJournal.LastWriteTime.ToString("HH:mm:ss");
                            RecordUnhandledMessage("Journal", evt, ts, line, $"Journal:{journalFileName}:{endPosition}");
                        }
                        catch
                        {
                            // Ignore malformed line
                        }
                    }
                }

                ReadUnhandledJsonFiles(folder);
            }
        }
        catch
        {
            // Ignorar errores de lectura concurrente
        }

        FlushUnhandledBatchIfNeeded(notify);
        lock (_unhandledLock)
        {
            return _unhandledGrouped.Count;
        }
    }

    public (IReadOnlyList<UnhandledGameMessageItem> Items, int DistinctCount, int TotalCount) GetUnhandledMessagesSnapshot(bool grouped)
    {
        lock (_unhandledLock)
        {
            int distinctCount = _unhandledGrouped.Count;
            int totalCount = _unhandledGrouped.Values.Sum(static item => item.Count);
            List<UnhandledGameMessageItem> items = grouped
                ? _unhandledGrouped.Values
                    .OrderByDescending(static item => item.Count)
                    .ThenBy(static item => item.Source, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(static item => item.EventType, StringComparer.CurrentCultureIgnoreCase)
                    .Select(static item => item.Snapshot())
                    .ToList()
                : _unhandledHistory
                    .Select(static item => item.Snapshot())
                    .ToList();
            return (items, distinctCount, totalCount);
        }
    }

    public void ClearUnhandledMessages()
    {
        lock (_unhandledLock)
        {
            _unhandledGrouped.Clear();
            _unhandledHistory.Clear();
            _recordedUnhandledKeys.Clear();
            _unhandledFileStamps.Clear();
            _unhandledBatchDirty = false;
            try
            {
                if (File.Exists(UnhandledEventsFilePath))
                    File.Delete(UnhandledEventsFilePath);
            }
            catch
            {
                // Ignore disk deletion error
            }
        }

        UnhandledMessagesUpdated?.Invoke();
    }

    public void SaveUnhandledEventsToFile()
    {
        lock (_unhandledLock)
        {
            SaveUnhandledEventsToFileCore();
        }
    }

    private void SaveUnhandledEventsToFileCore()
    {
        try
        {
            var lines = _unhandledGrouped.Values
                .OrderBy(static item => item.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.EventType, StringComparer.OrdinalIgnoreCase)
                .Select(static item => JsonSerializer.Serialize(item));
            File.WriteAllLines(UnhandledEventsFilePath, lines, Encoding.UTF8);
        }
        catch
        {
            // Ignorar errores de escritura en disco
        }
    }

    private void LoadUnhandledEventsFromFile()
    {
        try
        {
            if (!File.Exists(UnhandledEventsFilePath))
                return;

            lock (_unhandledLock)
            {
                foreach (string line in File.ReadLines(UnhandledEventsFilePath, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    try
                    {
                        var item = JsonSerializer.Deserialize<UnhandledGameMessageItem>(line);
                        if (item == null || string.IsNullOrWhiteSpace(item.EventType))
                            continue;
                        string source = string.IsNullOrWhiteSpace(item.Source) ? "Journal" : item.Source;
                        item.Source = source;
                        item.Count = Math.Max(1, item.Count);
                        _unhandledGrouped[$"{source}:{item.EventType}"] = item;
                        if (_unhandledHistory.Count < 500)
                            _unhandledHistory.Add(item.Snapshot());
                    }
                    catch
                    {
                        // Ignore invalid line
                    }
                }
            }
        }
        catch
        {
            // Ignorar errores de lectura inicial
        }
    }

    public void Dispose()
    {
        _configService.ConfigChanged -= OnConfigChanged;
        _inputSimulator.CommandExecuted -= OnCommandExecuted;
        try { _pollCts.Cancel(); } catch (ObjectDisposedException) { }
        _pollTimer.Dispose();
        _pollCts.Dispose();
        _watcher?.Dispose();
    }
}
