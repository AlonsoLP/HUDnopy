// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/Telemetry/EliteFileWatcherService.Journal.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Streaming Journal log parser and event dispatcher for all gameplay domains.
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
/// Streaming Journal log parser and event dispatcher for all gameplay domains.
/// </summary>
public partial class EliteFileWatcherService
{
    private void ReadLatestJournal(string folder)
    {
        try
        {
            var latest = new DirectoryInfo(folder).EnumerateFiles("Journal.*.log")
                .OrderByDescending(static info => info.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;

            if (latest == null) return;

            if (!string.Equals(latest, _currentJournalFile, StringComparison.OrdinalIgnoreCase))
            {
                _currentJournalFile = latest;
                _lastJournalPosition = 0;
                _journalSessionActive = null;
                _materialsInventory.Clear();
                _hasMaterialSnapshot = false;
                _state.HasMaterialsTelemetry = false;
                _state.SystemAddress = "";
                _materialsChanged = true;
                _currentSystemAddress = null;
            }

            using var fs = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < _lastJournalPosition) _lastJournalPosition = 0;
            fs.Seek(_lastJournalPosition, SeekOrigin.Begin);

            bool isInitialReplay = _lastJournalPosition == 0 && !_hasCompletedInitialJournalLoad;
            if (isInitialReplay)
                RestoreCarrierHistoryFromRecentJournals(folder, latest);
            string journalFileName = Path.GetFileName(latest);
            foreach (var (line, endPosition) in ReadCompleteJournalLines(fs))
            {
                ParseJournalLine(line, $"Journal:{journalFileName}:{endPosition}", isInitialReplay);
                _lastJournalPosition = endPosition;
            }
            _hasCompletedInitialJournalLoad = true;

            if (_missionsChanged)
            {
                _state.ActiveMissions = GetActiveMissions();
                SyncPassengerManifest();
                _missionsChanged = false;
            }
            if (!string.IsNullOrWhiteSpace(_currentSystemAddress))
                RestoreExplorationBodyHistory(folder, _currentSystemAddress);
            if (_explorationBodiesChanged)
            {
                RebuildExplorationBodiesSnapshot();
            }
        }
        catch
        {
            // Ignorar errores de acceso concurrente
        }
    }

    /// <summary>
    /// Scans recent historical Journal files on initial startup so the commander's last known
    /// <c>CarrierStats</c> (name, callsign, tritium fuel, balance, reserve, capacity, docking access)
    /// is populated immediately even when the current session's Journal only contains <c>CarrierLocation</c>.
    /// </summary>
    private void RestoreCarrierHistoryFromRecentJournals(string folder, string latestJournalPath)
    {
        try
        {
            var recentJournals = new DirectoryInfo(folder)
                .EnumerateFiles("Journal.*.log")
                .Where(f => !string.Equals(f.FullName, latestJournalPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .Take(20)
                .OrderBy(static f => f.LastWriteTimeUtc)
                .ToList();

            foreach (var journalInfo in recentJournals)
            {
                try
                {
                    using var stream = new FileStream(journalInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    foreach (var (line, _) in ReadCompleteJournalLines(stream))
                    {
                        if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"Carrier", StringComparison.Ordinal))
                            continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            if (!root.TryGetProperty("event", out var evtProp) || evtProp.ValueKind != JsonValueKind.String)
                                continue;
                            string evt = evtProp.GetString() ?? "";
                            CarrierJournal.Apply(_state, evt, root);
                        }
                        catch (JsonException)
                        {
                        }
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static IEnumerable<(string Line, long EndPosition)> ReadCompleteJournalLines(FileStream stream)
    {
        using var pendingLine = new MemoryStream();
        var buffer = new byte[8192];
        long bufferStart = stream.Position;
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            int start = 0;
            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                pendingLine.Write(buffer, start, i - start);
                string line = Encoding.UTF8.GetString(pendingLine.GetBuffer(), 0, (int)pendingLine.Length)
                    .TrimEnd('\r').TrimStart('\uFEFF');
                yield return (line, bufferStart + i + 1);
                pendingLine.SetLength(0);
                start = i + 1;
            }
            pendingLine.Write(buffer, start, bytesRead - start);
            bufferStart += bytesRead;
        }
        // The trailing incomplete line without a newline remains pending until the game finishes writing it.
    }

    private void SyncPassengerManifest()
    {
            _state.PassengerManifest = _state.ActiveMissions
            .Where(mission => mission.PassengerMission == true)
            .Select(mission => new PassengerItem
            {
                MissionId = mission.Id,
                Type = mission.Name,
                TypeIsFallback = mission.NameIsFallback,
                Count = mission.PassengerCount ?? mission.Count,
                DestinationSystem = mission.DestinationSystem,
                DestinationStation = mission.DestinationStation,
                Expiry = mission.Expiry
            })
            .ToList();

        _state.MassacreStacks = _state.ActiveMissions
            .Where(static m =>
                !string.IsNullOrWhiteSpace(m.TargetFaction) ||
                m.KillCount.HasValue ||
                m.Name.Contains("Massacre", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Masacre", StringComparison.OrdinalIgnoreCase))
            .GroupBy(static m => string.IsNullOrWhiteSpace(m.Faction) ? "—" : m.Faction, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MassacreStackItem
            {
                IssuingFaction = g.Key,
                TargetFaction = g.Select(static m => m.TargetFaction).FirstOrDefault(static f => !string.IsNullOrWhiteSpace(f)) ?? "—",
                MissionCount = g.Count(),
                RequiredKills = g.Sum(static m => m.KillCount ?? m.Count ?? 0),
                TotalReward = g.Sum(static m => m.Reward ?? 0)
            })
            .OrderByDescending(static s => s.RequiredKills)
            .ThenByDescending(static s => s.TotalReward)
            .ToList();
    }

    private void UpdateMissionState(string eventType, JsonElement data)
    {
        if (eventType == "Missions")
        {
            if (!data.TryGetProperty("Active", out var active) || active.ValueKind != JsonValueKind.Array)
                return;

            var updated = new Dictionary<string, MissionItem>(StringComparer.OrdinalIgnoreCase);
            DateTimeOffset snapshotTime = data.TryGetProperty("timestamp", out var timestamp) &&
                timestamp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(timestamp.GetString(), out var parsedTimestamp)
                    ? parsedTimestamp
                    : DateTimeOffset.UtcNow;
            foreach (var missionData in active.EnumerateArray())
            {
                string? activeMissionId = GetJournalIdentifier(missionData, "MissionID", "MissionId");
                if (string.IsNullOrWhiteSpace(activeMissionId))
                    continue;

                var mission = new MissionItem { Id = activeMissionId };
                mission.Name = GetJournalText(missionData, "Name_Localised", "LocalisedName", "Name") ?? "";
                mission.Faction = GetJournalText(missionData, "Faction") ?? "";
                mission.TargetFaction = GetJournalText(missionData, "TargetFaction") ?? "";
                if (TryGetJournalInt(missionData, "KillCount", out int kc))
                    mission.KillCount = kc;
                mission.DestinationSystem = GetJournalText(missionData, "DestinationSystem") ?? "";
                mission.DestinationStation = GetJournalText(missionData, "DestinationStation") ?? "";
                mission.Commodity = GetJournalText(missionData, "Commodity_Localised", "Commodity") ?? "";
                mission.PassengerMission = GetJournalNullableBool(missionData, "PassengerMission");
                if (TryGetJournalInt(missionData, "Count", out int count))
                    mission.Count = count;
                if (TryGetJournalInt(missionData, "PassengerCount", out int passengers))
                    mission.PassengerCount = passengers;
                if (TryGetJournalLong(missionData, "Reward", out long reward))
                    mission.Reward = reward;
                if (TryGetJournalLong(missionData, "Expires", out long expires))
                    mission.Expiry = snapshotTime.AddSeconds(Math.Max(0, expires));
                updated[activeMissionId] = mission;
            }

            _activeMissions.Clear();
            foreach (var item in updated)
                _activeMissions[item.Key] = item.Value;
            _missionsChanged = true;
            return;
        }

        if (eventType is not ("MissionAccepted" or "MissionRedirected" or "MissionCompleted" or
            "MissionFailed" or "MissionAbandoned" or "MissionExpired"))
            return;

        string? missionId = GetJournalIdentifier(data, "MissionID", "MissionId");
        if (string.IsNullOrWhiteSpace(missionId))
            return;

        if (eventType is "MissionCompleted" or "MissionFailed" or "MissionAbandoned" or "MissionExpired")
        {
            _activeMissions.Remove(missionId);
        }
        else
        {
            _activeMissions.TryGetValue(missionId, out var existing);
            var mission = existing ?? new MissionItem { Id = missionId, NameIsFallback = true };
            if (GetJournalText(data, "Name_Localised", "LocalisedName", "Name") is string name)
            {
                mission.Name = name;
                mission.NameIsFallback = false;
            }
            mission.Faction = GetJournalText(data, "Faction") ?? mission.Faction;
            mission.TargetFaction = GetJournalText(data, "TargetFaction") ?? mission.TargetFaction;
            if (TryGetJournalInt(data, "KillCount", out int acceptedKc))
                mission.KillCount = acceptedKc;
            mission.DestinationSystem = GetJournalText(data, "DestinationSystem") ?? mission.DestinationSystem;
            mission.DestinationStation = GetJournalText(data, "DestinationStation") ?? mission.DestinationStation;
            mission.Commodity = GetJournalText(data, "Commodity_Localised", "Commodity") ?? mission.Commodity;
            mission.PassengerMission = GetJournalNullableBool(data, "PassengerMission") ?? mission.PassengerMission;

            if (TryGetJournalInt(data, "Count", out int count))
                mission.Count = count;
            if (TryGetJournalInt(data, "PassengerCount", out int passengerCount))
                mission.PassengerCount = passengerCount;
            if (TryGetJournalLong(data, "Reward", out long reward))
                mission.Reward = reward;
            if (data.TryGetProperty("Expiry", out var expiryProperty) &&
                expiryProperty.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(expiryProperty.GetString(), out var expiry))
                mission.Expiry = expiry;

            _activeMissions[missionId] = mission;
        }
        _missionsChanged = true;
    }

    private void UpdateMaterialState(string eventType, JsonElement data)
    {
        switch (eventType)
        {
            case "Materials":
                ReplaceMaterialInventory(data);
                break;
            case "MaterialCollected":
                ApplyMaterialDelta(data, "Category", "Name", "Name_Localised", "Count", 1);
                break;
            case "MaterialDiscarded":
                ApplyMaterialDelta(data, "Category", "Name", "Name_Localised", "Count", -1);
                break;
            case "MaterialTrade":
                if (data.TryGetProperty("Paid", out var paid))
                    ApplyMaterialDelta(paid, "Category", "Material", "Material_Localised", "Quantity", -1);
                if (data.TryGetProperty("Received", out var received))
                    ApplyMaterialDelta(received, "Category", "Material", "Material_Localised", "Quantity", 1);
                break;
            case "Synthesis":
            case "EngineerCraft":
            case "TechnologyBroker":
                ApplyMaterialCosts(data);
                break;
            case "EngineerContribution":
                ApplyEngineerContribution(data);
                break;
            case "MissionCompleted":
                ApplyMissionMaterialRewards(data);
                break;
            default:
                return;
        }

        _materialsChanged = true;
    }

    private void ReplaceMaterialInventory(JsonElement data)
    {
        var updated = new Dictionary<string, MaterialItem>(StringComparer.OrdinalIgnoreCase);
        foreach (string category in new[] { "Raw", "Manufactured", "Encoded" })
        {
            if (!data.TryGetProperty(category, out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
                return;

            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    return;

                string? name = GetJournalRawText(entry, "Name");
                if (string.IsNullOrWhiteSpace(name) ||
                    !TryGetJournalInt(entry, "Count", out int count) ||
                    count < 0)
                    return;

                string categoryName = GetMaterialCategory(category);
                string id = GetMaterialId(category, name);
                updated[id] = new MaterialItem
                {
                    Id = id,
                    Name = name,
                    DisplayName = GetJournalRawText(entry, "Name_Localised", "Name_Localized") ?? name,
                    Category = categoryName,
                    Grade = MaterialMetadataCatalog.TryGetGrade(category, name, out int grade) ? grade : null,
                    Count = Math.Max(0, count)
                };
            }
        }

        _materialsInventory.Clear();
        foreach (var material in updated)
            _materialsInventory.Add(material.Key, material.Value);
        _hasMaterialSnapshot = true;
        _state.HasMaterialsTelemetry = true;
    }

    private void ApplyMaterialDelta(
        JsonElement data,
        string categoryProperty,
        string nameProperty,
        string localizedNameProperty,
        string amountProperty,
        int direction)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !TryGetJournalInt(data, amountProperty, out int amount) ||
            amount < 0)
            return;

        string? name = GetJournalRawText(data, nameProperty);
        string? category = GetJournalRawText(data, categoryProperty);
        if (string.IsNullOrWhiteSpace(name))
            return;

        ApplyMaterialAmount(category, name, GetJournalRawText(data, localizedNameProperty), amount, direction);
    }

    private void ApplyMaterialCosts(JsonElement data)
    {
        if (!data.TryGetProperty("Materials", out var materials) &&
            !data.TryGetProperty("Ingredients", out materials))
            return;

        if (materials.ValueKind == JsonValueKind.Array)
        {
            foreach (var material in materials.EnumerateArray())
                ApplyMaterialDelta(material, "Category", "Name", "Name_Localised", "Count", -1);
        }
        else if (materials.ValueKind == JsonValueKind.Object)
        {
            foreach (var material in materials.EnumerateObject())
            {
                if (material.Value.ValueKind == JsonValueKind.Number &&
                    material.Value.TryGetInt32(out int amount) &&
                    amount >= 0)
                    ApplyMaterialAmount(null, material.Name, null, amount, -1);
            }
        }
    }

    private void ApplyEngineerContribution(JsonElement data)
    {
        string? name = GetJournalRawText(data, "Material");
        if (name == null ||
            !TryGetJournalInt(data, "Quantity", out int amount) ||
            amount < 0)
            return;

        ApplyMaterialAmount(null, name, GetJournalRawText(data, "Material_Localised"), amount, -1);
    }

    private void ApplyMissionMaterialRewards(JsonElement data)
    {
        if (!data.TryGetProperty("MaterialsReward", out var rewards) ||
            rewards.ValueKind != JsonValueKind.Array)
            return;

        foreach (var reward in rewards.EnumerateArray())
            ApplyMaterialDelta(reward, "Category", "Name", "Name_Localised", "Count", 1);
    }

    private void ApplyMaterialAmount(
        string? rawCategory,
        string name,
        string? displayName,
        int amount,
        int direction)
    {
        string? category = null;
        if (rawCategory != null && !TryGetMaterialCategory(rawCategory, out category))
            return;

        MaterialItem? material;
        if (category != null)
        {
            _materialsInventory.TryGetValue(GetMaterialId(rawCategory!, name), out material);
        }
        else
        {
            material = _materialsInventory.Values.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        if (material == null)
        {
            if (category == null)
                return;

            string id = GetMaterialId(rawCategory!, name);
            material = new MaterialItem
            {
                Id = id,
                Name = name,
                DisplayName = displayName ?? name.Replace('_', ' '),
                Category = category,
                Grade = MaterialMetadataCatalog.TryGetGrade(rawCategory!, name, out int grade) ? grade : null
            };
            _materialsInventory[id] = material;
        }

        material.Count = Math.Max(0, material.Count + direction * amount);
    }

    private void SyncMaterialsToState()
    {
        var wishlist = _configService.Current.MaterialWishlist ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _state.MaterialsInventory = _materialsInventory.Values
            .Select(material =>
            {
                int target = 0;
                if (wishlist.TryGetValue(material.Id, out int idTarget))
                    target = idTarget;
                else if (wishlist.TryGetValue(material.Name, out int nameTarget))
                    target = nameTarget;
                return new MaterialItem
                {
                    Id = material.Id,
                    Name = material.Name,
                    DisplayName = material.DisplayName,
                    Category = material.Category,
                    Grade = material.Grade,
                    Count = material.Count,
                    WishlistTarget = Math.Max(0, target)
                };
            })
            .OrderBy(material => material.Category, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(material => material.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _state.HasMaterialsTelemetry = _hasMaterialSnapshot;
    }


    private static bool? GetJournalNullableBool(JsonElement data, string propertyName) =>
        data.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static bool TryGetJournalDouble(JsonElement data, string propertyName, out double number)
    {
        number = 0;
        return data.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out number) &&
            double.IsFinite(number);
    }

    private static double? ReadJournalDouble(JsonElement data, string propertyName) =>
        TryGetJournalDouble(data, propertyName, out double value) ? value : null;

    private static List<ExplorationMaterialItem> ReadExplorationMaterials(
        JsonElement data,
        string arrayProperty,
        params string[] nameProperties)
    {
        var result = new List<ExplorationMaterialItem>();
        if (!data.TryGetProperty(arrayProperty, out var array) || array.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in array.EnumerateArray())
        {
            string? name = GetJournalText(item, nameProperties);
            if (string.IsNullOrWhiteSpace(name))
                continue;
            result.Add(new ExplorationMaterialItem
            {
                Name = name,
                Percent = ReadJournalDouble(item, "Percent") ?? 0
            });
        }

        return result;
    }

    private static string GetMaterialCategory(string category) =>
        TryGetMaterialCategory(category, out var displayCategory) ? displayCategory : category;

    private static bool TryGetMaterialCategory(string category, out string displayCategory)
    {
        string normalizedCategory = category
            .Replace("$MICRORESOURCE_CATEGORY_", "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd(';');
        switch (normalizedCategory.ToLowerInvariant())
        {
            case "raw":
                displayCategory = "En bruto";
                return true;
            case "manufactured":
                displayCategory = "Manufacturados";
                return true;
            case "encoded":
                displayCategory = "Datos codificados";
                return true;
            default:
                displayCategory = "";
                return false;
        }
    }

    private static string GetMaterialId(string category, string name) =>
        $"{category.ToLowerInvariant()}:{name.ToLowerInvariant()}";

    private List<MissionItem> GetActiveMissions() =>
        _activeMissions.Values
            .OrderBy(mission => mission.Expiry ?? DateTimeOffset.MaxValue)
            .ThenBy(mission => mission.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(static mission => mission.Snapshot())
            .ToList();

    private static string? GetJournalText(JsonElement data, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (data.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!.Replace('_', ' ');
        }

        return null;
    }

    private static string? GetJournalRawText(JsonElement data, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (data.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        }

        return null;
    }

    private static string? GetJournalIdentifier(JsonElement data, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (!data.TryGetProperty(propertyName, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long id))
                return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static bool TryGetJournalInt(JsonElement data, string propertyName, out int number)
    {
        number = 0;
        return data.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out number);
    }

    private static bool TryGetJournalLong(JsonElement data, string propertyName, out long number)
    {
        number = 0;
        return data.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out number);
    }

    private void ParseJournalLine(string line, string? occurrenceKey = null, bool isHistorical = false)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var evtProp)) return;

            string evt = evtProp.GetString() ?? "";
            UpdateMissionState(evt, root);
            UpdateMaterialState(evt, root);
            UpdateExplorationBodyState(evt, root);
            UpdateCommanderProfile(evt, root);
            UpdateBackpackState(evt, root);
            UpdateColonisationState(evt, root);
            DateTimeOffset eventTimestamp;
            string ts;
            if (root.TryGetProperty("timestamp", out var tsProp))
            {
                if (DateTimeOffset.TryParse(tsProp.GetString(), out var parsedTimestamp))
                {
                    eventTimestamp = parsedTimestamp;
                    ts = parsedTimestamp.ToLocalTime().ToString("HH:mm:ss");
                }
                else
                {
                    eventTimestamp = DateTimeOffset.Now;
                    ts = "";
                }
            }
            else
            {
                eventTimestamp = DateTimeOffset.Now;
                ts = eventTimestamp.ToString("HH:mm:ss");
            }

            switch (evt)
            {
                case "Commander":
                    if (root.TryGetProperty("Name", out var cmdrName))
                        _state.CommanderName = "CMDR " + cmdrName.GetString();
                    break;

                case "Fileheader":
                    {
                        string ver = ReadJsonString(root, "gameversion");
                        if (string.IsNullOrWhiteSpace(ver))
                            ver = ReadJsonString(root, "version");
                        if (!string.IsNullOrWhiteSpace(ver))
                        {
                            _state.GameVersion = ver;
                            _state.IsLegacyGalaxy = ver.StartsWith("3.", StringComparison.Ordinal) ||
                                ver.StartsWith("2.", StringComparison.Ordinal);
                        }
                        string build = ReadJsonString(root, "build");
                        if (!string.IsNullOrWhiteSpace(build))
                            _state.GameBuild = build;
                        if (GetJournalNullableBool(root, "Odyssey") is bool fhOdy)
                            _state.IsOdyssey = fhOdy;
                    }
                    break;

                case "LoadGame":
                    if (_journalSessionActive != true)
                    {
                        string prevVer = _state.GameVersion;
                        string prevBuild = _state.GameBuild;
                        bool prevOdy = _state.IsOdyssey;
                        bool prevLegacy = _state.IsLegacyGalaxy;
                        _currentSystemAddress = null;
                        _explorationBodies.Clear();
                        _explorationBodyOrder.Clear();
                        _explorationHistoryResolvedSystems.Clear();
                        _unsoldCartographyByBody.Clear();
                        _unsoldExobiologyKeys.Clear();
                        _activeMissions.Clear();
                        _state = new ShipState
                        {
                            ThemeAccentHex = _state.ThemeAccentHex,
                            ThemeSecondaryHex = _state.ThemeSecondaryHex,
                            GameVersion = prevVer,
                            GameBuild = prevBuild,
                            IsOdyssey = prevOdy,
                            IsLegacyGalaxy = prevLegacy,
                            ActiveMissions = _activeMissions.Values
                                .OrderBy(mission => mission.Expiry ?? DateTimeOffset.MaxValue)
                                .ToList()
                        };
                        SyncPassengerManifest();
                        _snapshotFileStamps.Clear();
                        _materialsChanged = true;
                    }
                    _journalSessionActive = true;
                    _processMissingSinceUtc = null;
                    if (GetJournalNullableBool(root, "Horizons") is bool lgHorizons)
                        _state.IsHorizons = lgHorizons;
                    if (GetJournalNullableBool(root, "Odyssey") is bool lgOdyssey)
                        _state.IsOdyssey = lgOdyssey;
                    if (TryGetJournalLong(root, "ShipID", out long lgShipId))
                        _state.ShipId = lgShipId;
                    if (root.TryGetProperty("Ship", out var lgShipRaw) && lgShipRaw.ValueKind == JsonValueKind.String)
                        _state.ShipTypeInternal = lgShipRaw.GetString() ?? _state.ShipTypeInternal;
                    if (root.TryGetProperty("Commander", out var lgCmdr))
                        _state.CommanderName = "CMDR " + lgCmdr.GetString();
                    if (root.TryGetProperty("Ship_Localised", out var sLoc))
                        _state.ShipType = sLoc.GetString() ?? "";
                    else if (root.TryGetProperty("Ship", out var sRaw))
                        _state.ShipType = sRaw.GetString() ?? "";
                    if (root.TryGetProperty("ShipName", out var sName))
                        _state.ShipName = sName.GetString() ?? "";
                    if (root.TryGetProperty("ShipIdent", out var sIdent))
                        _state.ShipIdent = sIdent.GetString() ?? "";
                    if (root.TryGetProperty("FuelCapacity", out var fCap) &&
                        fCap.ValueKind == JsonValueKind.Number &&
                        fCap.GetDouble() > 0)
                    {
                        _state.FuelMax = fCap.GetDouble();
                        _state.HasFuelCapacity = true;
                    }
                    if (root.TryGetProperty("Credits", out var creds))
                    {
                        _state.Credits = creds.GetInt64();
                        _sessionStartCredits ??= _state.Credits;
                        _sessionStartedUtc ??= eventTimestamp.ToUniversalTime();
                    }
                    AddLocalizedJournalEntry(evt, "session", ts, _state.CommanderName, _state.ShipType);
                    break;

                case "Shutdown":
                    _journalSessionActive = false;
                    _processMissingSinceUtc = null;
                    break;

                case "Music":
                    if (root.TryGetProperty("MusicTrack", out var musicTrack) &&
                        string.Equals(musicTrack.GetString(), "MainMenu", StringComparison.OrdinalIgnoreCase))
                    {
                        _journalSessionActive = false;
                        _processMissingSinceUtc = null;
                    }
                    break;

                case "Loadout":
                    _state.LastLoadoutJson = line;
                    if (root.TryGetProperty("Modules", out var modulesProp) &&
                        modulesProp.ValueKind == JsonValueKind.Array)
                    {
                        _state.HasLoadoutTelemetry = true;
                        bool hasShield = false;
                        bool hasLimpet = false;
                        _shipModulesBySlot.Clear();
                        foreach (var module in modulesProp.EnumerateArray())
                        {
                            string item = ReadJsonString(module, "Item");
                            string slot = ReadJsonString(module, "Slot");
                            if (string.IsNullOrWhiteSpace(slot) || slot.StartsWith("PaintJob", StringComparison.OrdinalIgnoreCase) ||
                                slot.StartsWith("Decal", StringComparison.OrdinalIgnoreCase) ||
                                slot.StartsWith("ShipName", StringComparison.OrdinalIgnoreCase) ||
                                slot.StartsWith("ShipID", StringComparison.OrdinalIgnoreCase) ||
                                slot.StartsWith("ShipCockpit", StringComparison.OrdinalIgnoreCase) ||
                                slot.StartsWith("WeaponColour", StringComparison.OrdinalIgnoreCase) ||
                                slot.StartsWith("EngineColour", StringComparison.OrdinalIgnoreCase) ||
                                slot.StartsWith("VesselVoice", StringComparison.OrdinalIgnoreCase) ||
                                slot.StartsWith("Bobble", StringComparison.OrdinalIgnoreCase) ||
                                slot.StartsWith("StringLights", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (item.Contains("shieldgenerator", StringComparison.OrdinalIgnoreCase))
                                hasShield = true;
                            if (item.Contains("dronecontrol", StringComparison.OrdinalIgnoreCase))
                                hasLimpet = true;

                            double healthPct = TryGetJournalDouble(module, "Health", out double h) ? Math.Round(h * 100.0, 1) : 100;
                            if (slot.Equals("FrameShiftDrive", StringComparison.OrdinalIgnoreCase) ||
                                item.Contains("hyperdrive", StringComparison.OrdinalIgnoreCase))
                            {
                                _state.FsdHealthPercent = healthPct;
                            }

                            string engineering = "";
                            if (module.TryGetProperty("Engineering", out var engProp) && engProp.ValueKind == JsonValueKind.Object)
                            {
                                string bp = GetJournalText(engProp, "BlueprintName") ?? "";
                                int? level = TryGetJournalInt(engProp, "Level", out int lvl) ? lvl : null;
                                string exp = GetJournalText(engProp, "ExperimentalEffect_Localised", "ExperimentalEffect") ?? "";
                                engineering = string.Join(" · ", new[]
                                {
                                    level.HasValue ? $"G{level.Value} {bp}".Trim() : bp,
                                    exp
                                }.Where(static s => !string.IsNullOrWhiteSpace(s)));
                            }

                            _shipModulesBySlot[slot] = new ShipModuleItem
                            {
                                Slot = slot,
                                Item = item,
                                DisplayName = FormatModuleDisplayName(item),
                                On = GetJournalNullableBool(module, "On") ?? true,
                                Priority = (TryGetJournalInt(module, "Priority", out int prio) ? prio : 0) + 1,
                                HealthPercent = healthPct,
                                AmmoInClip = TryGetJournalInt(module, "AmmoInClip", out int clip) ? clip : null,
                                AmmoInHopper = TryGetJournalInt(module, "AmmoInHopper", out int hopper) ? hopper : null,
                                Engineering = engineering
                            };
                        }
                        _state.HasShieldGenerator = hasShield;
                        _state.HasLimpetController = hasLimpet;
                        SyncShipModulesToState();
                    }

                    if (root.TryGetProperty("ShipName", out var loName))
                        _state.ShipName = loName.GetString() ?? _state.ShipName;
                    if (root.TryGetProperty("ShipIdent", out var loIdent))
                        _state.ShipIdent = loIdent.GetString() ?? _state.ShipIdent;
                    if (TryGetJournalLong(root, "ShipID", out long loShipId))
                        _state.ShipId = loShipId;
                    if (root.TryGetProperty("Ship", out var loShipRaw) && loShipRaw.ValueKind == JsonValueKind.String)
                        _state.ShipTypeInternal = loShipRaw.GetString() ?? _state.ShipTypeInternal;
                    if (root.TryGetProperty("CargoCapacity", out var cCap))
                        _state.CargoCapacity = cCap.GetInt32();
                    if (root.TryGetProperty("HullHealth", out var hh))
                    {
                        _state.HullHealth = Math.Round(hh.GetDouble() * 100.0, 1);
                        _state.HasHullTelemetry = true;
                    }
                    if (root.TryGetProperty("FuelCapacity", out var fcObj) &&
                        fcObj.TryGetProperty("Main", out var fcMain) &&
                        fcMain.ValueKind == JsonValueKind.Number &&
                        fcMain.GetDouble() > 0)
                    {
                        _state.FuelMax = fcMain.GetDouble();
                        _state.HasFuelCapacity = true;
                    }
                    break;

                case "SuitLoadout":
                    _state.ActiveSuit = GetJournalText(root, "Suit_Localised", "Suit") ?? _state.ActiveSuit;
                    _state.ActiveSuitLoadout = GetJournalText(root, "LoadoutName_Localised", "LoadoutName", "Name_Localised", "Name") ?? _state.ActiveSuitLoadout;
                    break;

                case "Location":
                case "FSDJump":
                    _state.AssignedLandingPad = null;
                    _state.AssignedLandingStation = "";
                    _state.AssignedLandingStationType = "";
                    if (root.TryGetProperty("StarSystem", out var sysProp))
                        _state.CurrentSystem = sysProp.GetString() ?? _state.CurrentSystem;
                    if (root.TryGetProperty("StarPos", out var spProp) && spProp.ValueKind == JsonValueKind.Array && spProp.GetArrayLength() >= 3)
                    {
                        _state.StarPos = new[] { spProp[0].GetDouble(), spProp[1].GetDouble(), spProp[2].GetDouble() };
                    }
                    if (root.TryGetProperty("SystemSecurity_Localised", out var secProp))
                        _state.SecurityLevel = secProp.GetString() ?? "";
                    if (root.TryGetProperty("SystemEconomy_Localised", out var ecoProp))
                        _state.Economy = ecoProp.GetString() ?? "";
                    if (root.TryGetProperty("SystemAllegiance", out var allProp))
                    {
                        string? allegiance = allProp.GetString();
                        _state.AllegianceIsFallback = allegiance is null;
                        _state.Allegiance = allegiance ?? "Independiente";
                    }
                    if (root.TryGetProperty("Body", out var bProp))
                        _state.BodyName = bProp.GetString() ?? "";
                    ParseSystemFactionsAndPowerplay(root);
                    SyncCurrentSystemNote();
                    if (!string.IsNullOrWhiteSpace(_state.CurrentSystem))
                        _sessionVisitedSystems.Add(_state.CurrentSystem);
                    if (evt == "FSDJump")
                    {
                        if (TryGetJournalDouble(root, "JumpDist", out double jumpDist) && jumpDist > 0)
                            _sessionDistanceLy += jumpDist;
                        _state.JumpTargetSystem = "";
                        _state.JumpTargetStarClass = "";
                        _state.JumpTargetIsScoopable = false;
                        _state.JumpTargetIsHazardous = false;
                        _state.IsFsdSupercharged = false;
                        UpdateRouteFuelAnalysis();
                        AddLocalizedJournalEntry(evt, "arrival", ts, _state.CurrentSystem);
                    }
                    SyncSessionStatsToState();
                    break;

                case "DockingGranted":
                    if (TryGetJournalInt(root, "LandingPad", out int padNum) && padNum > 0)
                    {
                        _state.AssignedLandingPad = padNum;
                        _state.AssignedLandingStation = ReadJsonString(root, "StationName");
                        _state.AssignedLandingStationType = ReadJsonString(root, "StationType");
                    }
                    break;

                case "DockingCancelled":
                case "DockingTimeout":
                case "DockingDenied":
                    _state.AssignedLandingPad = null;
                    _state.AssignedLandingStation = "";
                    _state.AssignedLandingStationType = "";
                    break;

                case "Docked":
                    if (root.TryGetProperty("StationName", out var stProp))
                        _state.StationName = stProp.GetString() ?? "";
                    _state.AssignedLandingPad = null;
                    _state.AssignedLandingStation = "";
                    _state.AssignedLandingStationType = "";
                    AddLocalizedJournalEntry(evt, "docked", ts, _state.StationName, _state.CurrentSystem);
                    break;

                case "MissionAccepted":
                case "MissionRedirected":
                case "MissionCompleted":
                case "MissionFailed":
                case "MissionAbandoned":
                case "MissionExpired":
                    if (evt == "MissionCompleted")
                        _sessionMissionsCompleted++;
                    string? missionName = GetJournalText(root, "Name_Localised", "LocalisedName", "Name");
                    AddLocalizedJournalEntry(evt, char.ToLowerInvariant(evt[0]) + evt.Substring(1), ts, missionName);
                    break;

                case "Undocked":
                    AddLocalizedJournalEntry(evt, "undocked", ts, _state.StationName);
                    _state.StationName = "";
                    _state.AssignedLandingPad = null;
                    _state.AssignedLandingStation = "";
                    _state.AssignedLandingStationType = "";
                    break;

                case "StartJump":
                    if (root.TryGetProperty("JumpType", out var jtProp) &&
                        string.Equals(jtProp.GetString(), "Hyperspace", StringComparison.OrdinalIgnoreCase))
                    {
                        string targetSystem = ReadJsonString(root, "StarSystem");
                        string starClass = ReadJsonString(root, "StarClass");
                        _state.JumpTargetSystem = targetSystem;
                        _state.JumpTargetStarClass = starClass;
                        _state.JumpTargetIsScoopable = IsScoopableStarClass(starClass);
                        _state.JumpTargetIsHazardous = IsHazardousStarClass(starClass);
                        if (!string.IsNullOrWhiteSpace(targetSystem))
                            AddLocalizedJournalEntry(evt, "jump", ts, targetSystem);
                    }
                    break;

                case "FSSDiscoveryScan":
                case "FSSAllBodiesFound":
                case "SAAScanComplete":
                case "ScanOrganic":
                case "CodexEntry":
                case "SellExplorationData":
                case "MultiSellExplorationData":
                    if (evt == "FSSDiscoveryScan" && TryGetJournalInt(root, "BodyCount", out int totalBodies))
                        _state.SystemTotalBodies = totalBodies;
                    else if (evt == "FSSAllBodiesFound")
                        _state.SystemAllBodiesFound = true;
                    else if (evt is "SellExplorationData" or "MultiSellExplorationData")
                    {
                        _unsoldCartographyByBody.Clear();
                        _state.UnsoldCartographyCredits = 0;
                    }

                    if (evt != "ScanOrganic" ||
                        root.TryGetProperty("ScanType", out var scanType) &&
                        scanType.ValueKind == JsonValueKind.String &&
                        string.Equals(scanType.GetString(), "Analyse", StringComparison.OrdinalIgnoreCase))
                    {
                        var (summaryKey, summaryArguments) = BuildExplorationJournalSummary(evt, root);
                        AddLocalizedJournalEntry(evt, summaryKey, ts, summaryArguments);
                    }
                    break;

                case "SellOrganicData":
                    _unsoldExobiologyKeys.Clear();
                    _state.UnsoldExobiologyCredits = 0;
                    break;

                case "Died":
                    _unsoldCartographyByBody.Clear();
                    _state.UnsoldCartographyCredits = 0;
                    _unsoldExobiologyKeys.Clear();
                    _state.UnsoldExobiologyCredits = 0;
                    _activeExoSampleCoords.Clear();
                    _state.ExobiologyTracker = null;
                    _state.CurrentTarget = null;
                    foreach (var key in _combatByFaction.Keys.ToList())
                        _combatByFaction[key] = (_combatByFaction[key].Kills, 0);
                    SyncCombatSessionToState();
                    break;

                case "ApproachBody":
                    {
                        string approachBody = ReadJsonString(root, "Body");
                        _state.ApproachBodyName = approachBody;
                        var matched = _explorationBodies.Values.FirstOrDefault(item =>
                            string.Equals(item.BodyName, approachBody, StringComparison.OrdinalIgnoreCase) &&
                            item.SurfaceGravity.HasValue);
                        if (matched?.SurfaceGravity is double gravMs2)
                            _state.ApproachBodyGravityG = Math.Round(gravMs2 / 9.80665, 2);
                    }
                    break;

                case "LeaveBody":
                    _state.ApproachBodyName = "";
                    _state.ApproachBodyGravityG = null;
                    _activeExoSampleCoords.Clear();
                    _state.ExobiologyTracker = null;
                    break;

                case "JetConeBoost":
                    _state.IsFsdSupercharged = true;
                    break;

                case "JetConeDamage":
                    if (ReadJsonString(root, "Module").Contains("hyperdrive", StringComparison.OrdinalIgnoreCase) &&
                        _state.FsdHealthPercent.HasValue)
                    {
                        _state.FsdHealthPercent = Math.Max(0, Math.Round(_state.FsdHealthPercent.Value - 1.5, 1));
                    }
                    break;

                case "AfmuRepairs":
                    if (ReadJsonString(root, "Module").Contains("hyperdrive", StringComparison.OrdinalIgnoreCase) &&
                        TryGetJournalDouble(root, "Health", out double repairedFsd))
                    {
                        _state.FsdHealthPercent = Math.Round(repairedFsd * 100.0, 1);
                    }
                    break;

                case "ShipLocker":
                    ParseShipLockerElement(root);
                    break;

                case "EngineerProgress":
                    ParseEngineerProgress(root);
                    break;

                case "ProspectedAsteroid":
                    ParseProspectedAsteroid(root, ts);
                    break;

                case "MiningRefined":
                    ParseMiningRefined(root, eventTimestamp);
                    break;

                case "ApproachSettlement":
                case "Touchdown":
                case "Liftoff":
                    RecordSurfaceEvent(evt, root, ts);
                    break;

                case "ShipTargeted":
                    ParseShipTargeted(root);
                    break;

                case "Bounty":
                case "FactionKillBond":
                case "CapShipBond":
                case "PVPKill":
                    RecordCombatKill(evt, root, eventTimestamp);
                    break;

                case "RedeemVoucher":
                    if (ReadJsonString(root, "Type") is var voucherType &&
                        (voucherType.Equals("bounty", StringComparison.OrdinalIgnoreCase) ||
                         voucherType.Equals("CombatBond", StringComparison.OrdinalIgnoreCase)))
                    {
                        foreach (var key in _combatByFaction.Keys.ToList())
                            _combatByFaction[key] = (_combatByFaction[key].Kills, 0);
                        SyncCombatSessionToState();
                    }
                    break;

                case "HullDamage":
                    // Ignore damage reported for fighters or NPC pilots; only the commander's own ship hull is tracked.
                    bool hullFromOtherCraft =
                        (root.TryGetProperty("Fighter", out var fighterProp) && fighterProp.ValueKind == JsonValueKind.True) ||
                        (root.TryGetProperty("PlayerPilot", out var pilotProp) && pilotProp.ValueKind == JsonValueKind.False);
                    if (!hullFromOtherCraft && root.TryGetProperty("Health", out var hDmg) && hDmg.ValueKind == JsonValueKind.Number)
                    {
                        _state.HullHealth = Math.Round(hDmg.GetDouble() * 100.0, 1);
                        _state.HasHullTelemetry = true;
                        AddLocalizedJournalEntry(evt, "hull", ts, _state.HullHealth);
                    }
                    break;

                case "RepairAll":
                case "Resurrect":
                    _state.HullHealth = 100;
                    _state.HasHullTelemetry = true;
                    _state.CockpitBreached = false;
                    _state.FsdHealthPercent = 100;
                    break;

                case "Repair":
                    if (JournalEventHelpers.IsLifeSupportRepair(root))
                        _state.CockpitBreached = false;
                    break;

                case "CockpitBreached":
                    _state.CockpitBreached = true;
                    break;

                case "ShieldState":
                    if (root.TryGetProperty("ShieldsUp", out var shUp))
                    {
                        bool up = shUp.GetBoolean();
                        _state.RawFlags = up
                            ? _state.RawFlags | (uint)EliteStatusFlags.ShieldsUp
                            : _state.RawFlags & ~(uint)EliteStatusFlags.ShieldsUp;
                        _state.HasShieldTelemetry = true;
                        AddLocalizedJournalEntry(evt, up ? "shieldsUp" : "shieldsDown", ts);
                    }
                    break;
            }

            CarrierJournal.Apply(_state, evt, root);
            if (_configService.Current.CaptureUnhandledEvents && !IsJournalEventHandled(evt, root))
                RecordUnhandledMessage("Journal", evt, ts, line, occurrenceKey);
            _gameEvents.Publish(new GameJournalEvent(evt, eventTimestamp, root.Clone(), isHistorical));
        }
        catch
        {
            // Ignore malformed line
        }
    }

    private void UpdateCommanderProfile(string eventType, JsonElement data)
    {
        if (eventType is not ("Rank" or "Progress" or "Reputation" or "Statistics"))
            return;

        string categoryDisplay = eventType switch
        {
            "Rank" => "Rango",
            "Progress" => "Progreso",
            "Reputation" => "Reputación",
            _ => "Estadísticas"
        };
        var values = _state.CommanderProfile
            .Where(item => !string.Equals(item.Category, categoryDisplay, StringComparison.Ordinal))
            .ToList();
        if (eventType == "Statistics")
        {
            foreach (var category in data.EnumerateObject())
            {
                if (category.Name is not ("event" or "timestamp"))
                    AddProfileValues(values, categoryDisplay, category.Name, category.Value);
            }
        }
        else
        {
            foreach (var property in data.EnumerateObject())
            {
                if (property.Name is "event" or "timestamp")
                    continue;
                values.Add(new ProfileValueItem
                {
                    Category = categoryDisplay,
                    CategoryKey = eventType,
                    Name = property.Name,
                    Value = JsonValueToDisplayString(property.Value),
                    IsNumber = property.Value.ValueKind == JsonValueKind.Number,
                    BooleanValue = property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False ? property.Value.GetBoolean() : null
                });
            }
        }

        _state.CommanderProfile = values.TakeLast(300).ToList();
    }

    private static void AddProfileValues(
        List<ProfileValueItem> values,
        string category,
        string path,
        JsonElement value)
    {
        if (values.Count >= 300)
            return;

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                AddProfileValues(values, category, $"{path} / {property.Name}", property.Value);
                if (values.Count >= 300)
                    return;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var item in value.EnumerateArray())
            {
                AddProfileValues(values, category, $"{path} [{index++}]", item);
                if (values.Count >= 300)
                    return;
            }
        }
        else
        {
            values.Add(new ProfileValueItem
            {
                Category = category,
                CategoryKey = "Statistics",
                Name = path,
                Value = JsonValueToDisplayString(value),
                IsNumber = value.ValueKind == JsonValueKind.Number,
                BooleanValue = value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null
            });
        }
    }

    private static string JsonValueToDisplayString(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "Sí",
            JsonValueKind.False => "No",
            JsonValueKind.Null => "",
            _ => ""
        };

    private void UpdateBackpackState(string eventType, JsonElement data)
    {
        if (eventType == "Backpack")
        {
            var items = new List<BackpackItem>();
            foreach (string category in new[] { "Items", "Components", "Consumables", "Data" })
                AppendBackpackItems(items, data, category);
            _state.BackpackInventory = items;
            return;
        }

        if (eventType != "BackpackChange")
            return;

        var changed = new Dictionary<string, BackpackItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _state.BackpackInventory)
            changed[$"{item.Type}:{item.Name}"] = item.Snapshot();
        ApplyBackpackChange(changed, data, "Added", 1);
        ApplyBackpackChange(changed, data, "Removed", -1);
        _state.BackpackInventory = changed.Values
            .Where(item => item.Count > 0)
            .OrderBy(item => item.Type, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void AppendBackpackItems(List<BackpackItem> items, JsonElement data, string category)
    {
        if (!data.TryGetProperty(category, out var entries) || entries.ValueKind != JsonValueKind.Array)
            return;

        foreach (var entry in entries.EnumerateArray())
        {
            string name = GetJournalRawText(entry, "Name") ?? "";
            if (string.IsNullOrWhiteSpace(name))
                continue;
            items.Add(new BackpackItem
            {
                Name = name,
                DisplayName = GetJournalText(entry, "Name_Localised", "Name") ?? name,
                Type = category,
                Count = TryGetJournalInt(entry, "Count", out int count) ? count : 0
            });
        }
    }

    private static void ApplyBackpackChange(
        Dictionary<string, BackpackItem> items,
        JsonElement data,
        string propertyName,
        int direction)
    {
        if (!data.TryGetProperty(propertyName, out var entries) || entries.ValueKind != JsonValueKind.Array)
            return;

        string type = GetJournalRawText(data, "Type") ?? "";
        foreach (var entry in entries.EnumerateArray())
        {
            string name = GetJournalRawText(entry, "Name") ?? "";
            if (string.IsNullOrWhiteSpace(name))
                continue;

            string key = $"{type}:{name}";
            int count = TryGetJournalInt(entry, "Count", out int parsedCount) ? parsedCount : 1;
            if (!items.TryGetValue(key, out var existing))
            {
                existing = new BackpackItem
                {
                    Name = name,
                    DisplayName = GetJournalText(entry, "Name_Localised", "Name") ?? name,
                    Type = type
                };
                items[key] = existing;
            }
            existing.Count = Math.Max(0, existing.Count + direction * count);
        }
    }

    private void UpdateColonisationState(string eventType, JsonElement data)
    {
        if (eventType == "ColonisationConstructionDepot")
        {
            var next = _state.Colonisation.Snapshot();
            next.MarketId = TryGetJournalLong(data, "MarketID", out long marketId)
                ? marketId : next.MarketId;
            next.ConstructionProgress = ReadJournalDouble(data, "ConstructionProgress")
                ?? next.ConstructionProgress;
            next.ConstructionComplete = GetJournalNullableBool(data, "ConstructionComplete")
                ?? next.ConstructionComplete;
            next.ConstructionFailed = GetJournalNullableBool(data, "ConstructionFailed")
                ?? next.ConstructionFailed;
            if (data.TryGetProperty("ResourcesRequired", out var resources) && resources.ValueKind == JsonValueKind.Array)
            {
                next.Resources = resources.EnumerateArray().Select(resource => new ColonisationResourceItem
                {
                    Name = GetJournalRawText(resource, "Name") ?? "",
                    DisplayName = GetJournalText(resource, "Name_Localised", "Name") ?? "",
                    RequiredAmount = TryGetJournalLong(resource, "RequiredAmount", out long required) ? required : 0,
                    ProvidedAmount = TryGetJournalLong(resource, "ProvidedAmount", out long provided) ? provided : 0,
                    Payment = TryGetJournalLong(resource, "Payment", out long payment) ? payment : 0
                }).ToList();
            }
            next.Timestamp = DateTimeOffset.UtcNow;
            _state.Colonisation = next;
        }
        else if (eventType == "ColonisationContribution")
        {
            if (!data.TryGetProperty("Contributions", out var contributions) ||
                contributions.ValueKind != JsonValueKind.Array)
                return;

            var next = _state.Colonisation.Snapshot();
            foreach (var contribution in contributions.EnumerateArray())
            {
                string? name = GetJournalRawText(contribution, "Name");
                if (name == null || !TryGetJournalLong(contribution, "Amount", out long amount))
                    continue;
                var resource = next.Resources.FirstOrDefault(item =>
                    string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                if (resource != null)
                    resource.ProvidedAmount = Math.Min(resource.RequiredAmount, resource.ProvidedAmount + amount);
            }
            next.Timestamp = DateTimeOffset.UtcNow;
            _state.Colonisation = next;
            SyncMarketColonisationNeeds();
        }
    }


    public void AddJournalEntry(string eventType, string summary, string? timestamp = null)
    {
        AddJournalEntry(new JournalEntryItem
        {
            Timestamp = timestamp ?? DateTime.Now.ToString("HH:mm:ss"),
            EventType = eventType,
            Summary = summary
        });
    }

    private void AddLocalizedJournalEntry(string eventType, string key, string? timestamp, params object?[] arguments)
    {
        var entry = new JournalEntryItem
        {
            Timestamp = timestamp ?? DateTime.Now.ToString("HH:mm:ss"),
            EventType = eventType,
            SummaryKey = key,
            SummaryArguments = arguments
        };
        entry.Summary = JournalTranslations.Summary(entry, "es-ES");
        AddJournalEntry(entry);
    }

    private void AddJournalEntry(JournalEntryItem entry)
    {
        lock (_refreshLock)
        {
            var current = _state.RecentEvents;
            int keep = Math.Min(current.Count, 49);
            var next = new List<JournalEntryItem>(keep + 1) { entry };
            for (int i = 0; i < keep; i++)
                next.Add(current[i]);
            _state.RecentEvents = next;
        }
    }

    private void UpdateRouteFuelAnalysis()
    {
        var route = _state.NavRoute;
        if (route.Count == 0)
        {
            _state.JumpsToNextFuelStar = null;
            _state.RouteHasNoFuelStar = false;
            return;
        }

        int currentIdx = route.FindIndex(w =>
            w.IsCurrentSystem || string.Equals(w.StarSystem, _state.CurrentSystem, StringComparison.OrdinalIgnoreCase));
        int startIdx = currentIdx >= 0 ? currentIdx + 1 : 0;
        if (startIdx >= route.Count)
        {
            _state.JumpsToNextFuelStar = null;
            _state.RouteHasNoFuelStar = false;
            return;
        }

        int jumps = 1;
        for (int i = startIdx; i < route.Count; i++, jumps++)
        {
            if (route[i].IsFuelStar)
            {
                _state.JumpsToNextFuelStar = jumps;
                _state.RouteHasNoFuelStar = false;
                return;
            }
        }

        _state.JumpsToNextFuelStar = null;
        _state.RouteHasNoFuelStar = true;
    }

    public static bool IsScoopableStarClass(string? starClass)
    {
        if (string.IsNullOrWhiteSpace(starClass))
            return false;
        char c = char.ToUpperInvariant(starClass.Trim()[0]);
        return c is 'O' or 'B' or 'A' or 'F' or 'G' or 'K' or 'M';
    }

    public static bool IsHazardousStarClass(string? starClass)
    {
        if (string.IsNullOrWhiteSpace(starClass))
            return false;
        string s = starClass.Trim();
        return s.Equals("N", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("H", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("D", StringComparison.OrdinalIgnoreCase);
    }


    private void ParseEngineerProgress(JsonElement data)
    {
        void UpsertEngineer(JsonElement entry)
        {
            string name = GetJournalText(entry, "Engineer") ?? "";
            if (string.IsNullOrWhiteSpace(name))
                return;

            string progress = GetJournalText(entry, "Progress") ?? "";
            int? rank = TryGetJournalInt(entry, "Rank", out int r) ? r : null;
            int? rankProgress = TryGetJournalInt(entry, "RankProgress", out int rp) ? rp : null;
            _engineersByName[name] = new EngineerProgressItem
            {
                Name = name,
                Progress = progress,
                Rank = rank,
                RankProgress = rankProgress
            };
        }

        if (data.TryGetProperty("Engineers", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
                UpsertEngineer(entry);
        }
        else
        {
            UpsertEngineer(data);
        }

        _state.Engineers = _engineersByName.Values
            .OrderByDescending(static e => e.Rank ?? -1)
            .ThenBy(static e => e.Progress, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(static e => e.Snapshot())
            .ToList();
    }

    private void SyncShipModulesToState()
    {
        _state.ShipModules = _shipModulesBySlot.Values
            .OrderBy(static m => m.Priority)
            .ThenBy(static m => m.Slot, StringComparer.OrdinalIgnoreCase)
            .Select(static m => m.Snapshot())
            .ToList();
    }

    private string FormatModuleDisplayName(string rawItem) =>
        ModuleDisplayCatalog.Format(rawItem, _configService.Current.InterfaceLanguage);

    private void ParseProspectedAsteroid(JsonElement data, string ts)
    {
        var materials = new List<ProspectedMaterialItem>();
        if (data.TryGetProperty("Materials", out var mats) && mats.ValueKind == JsonValueKind.Array)
        {
            foreach (var mat in mats.EnumerateArray())
            {
                string name = GetJournalText(mat, "Name_Localised", "Name") ?? "";
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                double prop = TryGetJournalDouble(mat, "Proportion", out double p) ? Math.Round(p, 1) : 0;
                materials.Add(new ProspectedMaterialItem { Name = name, Proportion = prop });
            }
        }

        _asteroidsProspected++;
        _miningSessionStartedUtc ??= DateTimeOffset.UtcNow;
        _state.LastProspectedAsteroid = new ProspectedAsteroidState
        {
            Timestamp = ts,
            Content = GetJournalText(data, "Content_Localised", "Content") ?? "",
            MotherlodeMaterial = GetJournalText(data, "MotherlodeMaterial_Localised", "MotherlodeMaterial") ?? "",
            Remaining = TryGetJournalDouble(data, "Remaining", out double rem) ? Math.Round(rem, 1) : 100,
            Materials = materials.OrderByDescending(static m => m.Proportion).ToList()
        };
        SyncMiningSessionToState();
    }

    private void ParseMiningRefined(JsonElement data, DateTimeOffset eventTimestamp)
    {
        string mineral = GetJournalText(data, "Type_Localised", "Type") ?? "";
        if (string.IsNullOrWhiteSpace(mineral))
            return;

        _miningSessionStartedUtc ??= eventTimestamp.ToUniversalTime();
        _miningRefinedByMineral[mineral] = _miningRefinedByMineral.TryGetValue(mineral, out int current) ? current + 1 : 1;
        SyncMiningSessionToState();
    }

    private void SyncMiningSessionToState()
    {
        int totalTons = _miningRefinedByMineral.Values.Sum();
        double tonsPerHour = 0;
        if (_miningSessionStartedUtc.HasValue && totalTons > 0)
        {
            double elapsedHours = Math.Max(1.0 / 60.0, (DateTimeOffset.UtcNow - _miningSessionStartedUtc.Value).TotalHours);
            tonsPerHour = Math.Round(totalTons / elapsedHours, 1);
        }

        _state.MiningSession = new MiningSessionState
        {
            StartedAtUtc = _miningSessionStartedUtc,
            TotalRefinedTons = totalTons,
            AsteroidsProspected = _asteroidsProspected,
            TonsPerHour = tonsPerHour,
            RefinedByMineral = _miningRefinedByMineral
                .OrderByDescending(static kv => kv.Value)
                .ThenBy(static kv => kv.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(static kv => new MiningRefinedItem { Mineral = kv.Key, Tons = kv.Value })
                .ToList()
        };
    }

    public void ResetMiningSession()
    {
        lock (_refreshLock)
        {
            _miningRefinedByMineral.Clear();
            _miningSessionStartedUtc = null;
            _asteroidsProspected = 0;
            _state.LastProspectedAsteroid = null;
            _state.MiningSession = new MiningSessionState();
            PublishState();
        }
    }

    private void RecordSurfaceEvent(string eventType, JsonElement data, string ts)
    {
        string bodyName = GetJournalText(data, "BodyName") ?? _state.BodyName;
        string details = eventType switch
        {
            "ApproachSettlement" => GetJournalText(data, "Name_Localised", "Name") ?? "",
            "Touchdown" or "Liftoff" => GetJournalText(data, "NearestDestination_Localised", "NearestDestination") ?? bodyName,
            _ => ""
        };
        double? lat = TryGetJournalDouble(data, "Latitude", out double la) ? Math.Round(la, 4) : (_state.HasLatLong ? _state.Latitude : null);
        double? lon = TryGetJournalDouble(data, "Longitude", out double lo) ? Math.Round(lo, 4) : (_state.HasLatLong ? _state.Longitude : null);

        var entry = new SurfaceEventItem
        {
            Timestamp = ts,
            EventType = eventType,
            BodyName = bodyName,
            Details = details,
            Latitude = lat,
            Longitude = lon
        };
        var current = _state.RecentSurfaceEvents;
        int keep = Math.Min(current.Count, 29);
        var next = new List<SurfaceEventItem>(keep + 1) { entry };
        for (int i = 0; i < keep; i++)
            next.Add(current[i]);
        _state.RecentSurfaceEvents = next;
    }

    private void ParseShipTargeted(JsonElement data)
    {
        bool targetLocked = GetJournalNullableBool(data, "TargetLocked") ?? false;
        if (!targetLocked)
        {
            _state.CurrentTarget = null;
            return;
        }

        int scanStage = TryGetJournalInt(data, "ScanStage", out int stage) ? stage : 0;
        var prev = _state.CurrentTarget;
        string ship = GetJournalText(data, "Ship_Localised", "Ship") ?? prev?.Ship ?? "";
        string pilotName = GetJournalText(data, "PilotName_Localised", "PilotName") ?? (scanStage >= 1 ? prev?.PilotName ?? "" : "");
        string pilotRank = GetJournalText(data, "PilotRank") ?? (scanStage >= 1 ? prev?.PilotRank ?? "" : "");
        string faction = GetJournalText(data, "Faction") ?? (scanStage >= 3 ? prev?.Faction ?? "" : "");
        string legalStatus = GetJournalText(data, "LegalStatus") ?? (scanStage >= 3 ? prev?.LegalStatus ?? "" : "");
        long bounty = TryGetJournalLong(data, "Bounty", out long b) ? b : (scanStage >= 3 ? prev?.Bounty ?? 0 : 0);
        double? shield = TryGetJournalDouble(data, "ShieldHealth", out double sh) ? Math.Round(sh, 1) : (scanStage >= 2 ? prev?.ShieldHealth : null);
        double? hull = TryGetJournalDouble(data, "HullHealth", out double hu) ? Math.Round(hu, 1) : (scanStage >= 2 ? prev?.HullHealth : null);
        string subsystem = GetJournalText(data, "Subsystem_Localised", "Subsystem") ?? "";
        double? subsystemHealth = TryGetJournalDouble(data, "SubsystemHealth", out double subH) ? Math.Round(subH, 1) : null;

        _state.CurrentTarget = new TargetShipState
        {
            Ship = ship,
            PilotName = pilotName,
            PilotRank = pilotRank,
            Faction = faction,
            LegalStatus = legalStatus,
            Bounty = bounty,
            ShieldHealth = shield,
            HullHealth = hull,
            Subsystem = subsystem,
            SubsystemHealth = subsystemHealth,
            ScanStage = scanStage
        };
    }

    private void RecordCombatKill(string eventType, JsonElement data, DateTimeOffset eventTimestamp)
    {
        _combatSessionStartedUtc ??= eventTimestamp.ToUniversalTime();

        if (eventType == "Bounty" &&
            data.TryGetProperty("Rewards", out var rewardsProp) &&
            rewardsProp.ValueKind == JsonValueKind.Array &&
            rewardsProp.GetArrayLength() > 0)
        {
            bool countedKill = false;
            foreach (var reward in rewardsProp.EnumerateArray())
            {
                string faction = GetJournalText(reward, "Faction") ?? "Desconocida";
                long amount = TryGetJournalLong(reward, "Reward", out long r) ? r : 0;
                var cur = _combatByFaction.TryGetValue(faction, out var existing) ? existing : (Kills: 0, Credits: 0L);
                _combatByFaction[faction] = (cur.Kills + (countedKill ? 0 : 1), cur.Credits + amount);
                countedKill = true;
            }
        }
        else
        {
            string faction = GetJournalText(data, "AwardingFaction", "VictimFaction", "Faction") ?? "Combate";
            long amount = 0;
            if (TryGetJournalLong(data, "TotalReward", out long tr))
                amount = tr;
            else if (TryGetJournalLong(data, "Reward", out long rw))
                amount = rw;

            var cur = _combatByFaction.TryGetValue(faction, out var existing) ? existing : (Kills: 0, Credits: 0L);
            _combatByFaction[faction] = (cur.Kills + 1, cur.Credits + amount);
        }

        SyncCombatSessionToState();
    }

    private void SyncCombatSessionToState()
    {
        int totalKills = _combatByFaction.Values.Sum(static v => v.Kills);
        long pendingCredits = _combatByFaction.Values.Sum(static v => v.Credits);
        long crPerHour = 0;
        if (_combatSessionStartedUtc.HasValue && pendingCredits > 0)
        {
            double elapsedHours = Math.Max(1.0 / 60.0, (DateTimeOffset.UtcNow - _combatSessionStartedUtc.Value).TotalHours);
            crPerHour = (long)Math.Round(pendingCredits / elapsedHours);
        }

        _state.CombatSession = new CombatSessionState
        {
            StartedAtUtc = _combatSessionStartedUtc,
            TotalKills = totalKills,
            PendingCredits = pendingCredits,
            CreditsPerHour = crPerHour,
            ByFaction = _combatByFaction
                .OrderByDescending(static kv => kv.Value.Credits)
                .ThenByDescending(static kv => kv.Value.Kills)
                .ThenBy(static kv => kv.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(static kv => new CombatFactionItem
                {
                    Faction = kv.Key,
                    Kills = kv.Value.Kills,
                    PendingCredits = kv.Value.Credits
                })
                .ToList()
        };
    }

    public void ResetCombatSession()
    {
        lock (_refreshLock)
        {
            _combatByFaction.Clear();
            _combatSessionStartedUtc = null;
            _state.CombatSession = new CombatSessionState();
            PublishState();
        }
    }


    private void ParseSystemFactionsAndPowerplay(JsonElement data)
    {
        if (data.TryGetProperty("Factions", out var factionsProp) && factionsProp.ValueKind == JsonValueKind.Array)
        {
            var list = new List<SystemFactionItem>();
            foreach (var f in factionsProp.EnumerateArray())
            {
                string name = ReadJsonString(f, "Name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                double inf = TryGetJournalDouble(f, "Influence", out double i) ? Math.Round(i * 100.0, 1) : 0;
                string state = GetJournalText(f, "FactionState") ?? "";
                string gov = GetJournalText(f, "Government_Localised", "Government") ?? "";
                string allegiance = GetJournalText(f, "Allegiance") ?? "";
                double? rep = TryGetJournalDouble(f, "MyReputation", out double r) ? Math.Round(r, 0) : null;
                list.Add(new SystemFactionItem
                {
                    Name = name,
                    InfluencePercent = inf,
                    FactionState = state,
                    Government = gov,
                    Allegiance = allegiance,
                    MyReputation = rep
                });
            }

            _state.SystemFactions = list
                .OrderByDescending(static x => x.InfluencePercent)
                .ThenBy(static x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        var ppParts = new List<string>();
        string controllingPower = ReadJsonString(data, "ControllingPower");
        string ppState = ReadJsonString(data, "PowerplayState");
        if (!string.IsNullOrWhiteSpace(controllingPower))
            ppParts.Add(controllingPower);
        if (!string.IsNullOrWhiteSpace(ppState))
            ppParts.Add(ppState);
        if (ppParts.Count == 0 && data.TryGetProperty("Powers", out var powersProp) && powersProp.ValueKind == JsonValueKind.Array)
        {
            var powers = powersProp.EnumerateArray()
                .Where(static p => p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
                .Select(static p => p.GetString()!)
                .ToList();
            if (powers.Count > 0)
                ppParts.Add(string.Join(", ", powers));
        }

        _state.PowerplaySummary = string.Join(" · ", ppParts);
    }


    public static List<JournalEntryItem> LoadHistoricalJournal(string filePath)
    {
        var entries = new List<JournalEntryItem>();
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return entries;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            foreach (var (line, _) in ReadCompleteJournalLines(fs))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string evt = ReadJsonString(root, "event");
                    if (string.IsNullOrWhiteSpace(evt))
                        continue;
                    string ts = ReadJsonTimestamp(root, "timestamp")?.ToLocalTime().ToString("HH:mm:ss") ?? "";
                    string summary = BuildGenericHistoricalSummary(evt, root);
                    entries.Add(new JournalEntryItem
                    {
                        Timestamp = ts,
                        EventType = evt,
                        Summary = summary
                    });
                }
                catch
                {
                    // Ignore malformed line
                }
            }
        }
        catch
        {
            // Ignorar error de acceso
        }

        entries.Reverse();
        return entries;
    }

    private static string BuildGenericHistoricalSummary(string evt, JsonElement root)
    {
        string[] candidateKeys =
        {
            "StarSystem", "StationName", "BodyName", "Name_Localised", "Name",
            "Species_Localised", "Type_Localised", "Ship_Localised", "Ship",
            "Target_Localised", "Faction", "Message_Localised", "Message"
        };
        var parts = new List<string>();
        foreach (string key in candidateKeys)
        {
            string val = ReadJsonString(root, key);
            if (!string.IsNullOrWhiteSpace(val) && !parts.Contains(val, StringComparer.OrdinalIgnoreCase))
            {
                parts.Add(val);
                if (parts.Count >= 3)
                    break;
            }
        }

        if (TryGetJournalLong(root, "TotalReward", out long tr) && tr > 0)
            parts.Add($"{tr:N0} CR");
        else if (TryGetJournalLong(root, "Reward", out long rw) && rw > 0)
            parts.Add($"{rw:N0} CR");
        else if (TryGetJournalLong(root, "TotalEarnings", out long te) && te > 0)
            parts.Add($"{te:N0} CR");

        return parts.Count > 0 ? string.Join(" · ", parts) : evt;
    }

    public static bool IsJournalEventHandled(string evt, JsonElement root)
    {
        switch (evt)
        {
            case "Commander":
            case "Fileheader":
            case "LoadGame":
            case "Shutdown":
            case "Loadout":
            case "SuitLoadout":
            case "Location":
            case "FSDJump":
            case "Docked":
            case "Undocked":
            case "Missions":
            case "MissionAccepted":
            case "MissionRedirected":
            case "MissionCompleted":
            case "MissionFailed":
            case "MissionAbandoned":
            case "MissionExpired":
            case "Materials":
            case "MaterialCollected":
            case "MaterialDiscarded":
            case "MaterialTrade":
            case "Synthesis":
            case "EngineerCraft":
            case "TechnologyBroker":
            case "EngineerContribution":
            case "Scan":
            case "FSSBodySignals":
            case "SAASignalsFound":
            case "SAAScanComplete":
            case "FSSDiscoveryScan":
            case "FSSAllBodiesFound":
            case "CodexEntry":
            case "SellExplorationData":
            case "MultiSellExplorationData":
            case "SellOrganicData":
            case "Died":
            case "ApproachBody":
            case "LeaveBody":
            case "JetConeBoost":
            case "JetConeDamage":
            case "AfmuRepairs":
            case "ScanOrganic":
            case "Rank":
            case "Progress":
            case "Reputation":
            case "Statistics":
            case "Backpack":
            case "BackpackChange":
            case "ShipLocker":
            case "EngineerProgress":
            case "ProspectedAsteroid":
            case "MiningRefined":
            case "ApproachSettlement":
            case "Touchdown":
            case "Liftoff":
            case "ColonisationConstructionDepot":
            case "ColonisationContribution":
            case "HullDamage":
            case "RepairAll":
            case "Resurrect":
            case "CockpitBreached":
            case "ShieldState":
            case "CarrierBuy":
            case "CarrierStats":
            case "CarrierLocation":
            case "CarrierJumpRequest":
            case "CarrierJumpCancelled":
            case "CarrierJump":
            case "CarrierFinance":
            case "CarrierBankTransfer":
            case "CarrierDepositFuel":
            case "CarrierDockingPermission":
            case "CarrierNameChanged":
            case "CarrierDecommission":
            case "CarrierCancelDecommission":
            case "RedeemVoucher":
            case "Bounty":
            case "FactionKillBond":
            case "CapShipBond":
            case "PVPKill":
            case "ShipTargeted":
            case "Scanned":
            case "UnderAttack":
            case "CommitCrime":
            case "MarketBuy":
            case "MarketSell":
            case "CollectCargo":
            case "EjectCargo":
            case "MarketTransfer":
            case "CargoDepot":
                return true;

            case "Music":
                return root.TryGetProperty("MusicTrack", out var musicTrack) &&
                    string.Equals(musicTrack.GetString(), "MainMenu", StringComparison.OrdinalIgnoreCase);

            case "StartJump":
                return root.TryGetProperty("JumpType", out var jtProp) &&
                    string.Equals(jtProp.GetString(), "Hyperspace", StringComparison.OrdinalIgnoreCase);

            case "Repair":
                return JournalEventHelpers.IsLifeSupportRepair(root);

            default:
                return false;
        }
    }

}
