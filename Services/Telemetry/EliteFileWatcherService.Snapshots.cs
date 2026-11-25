// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/Telemetry/EliteFileWatcherService.Snapshots.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Readers for Status.json, Cargo.json, Market.json, NavRoute.json, and related snapshot files.
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
/// Readers for Status.json, Cargo.json, Market.json, NavRoute.json, and related snapshot files.
/// </summary>
public partial class EliteFileWatcherService
{
    private void ReadStatusJson(string path)
    {
        if (!ShouldReadSnapshotFile(path)) return;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            bool hasFlagsNumber = root.TryGetProperty("Flags", out var flagsProp) &&
                flagsProp.ValueKind == JsonValueKind.Number;

            if (hasFlagsNumber)
                _state.RawFlags = flagsProp.GetUInt32();
            else
                _state.RawFlags = 0;

            if (root.TryGetProperty("Flags2", out var flags2Prop) && flags2Prop.ValueKind == JsonValueKind.Number)
                _state.RawFlags2 = flags2Prop.GetUInt32();
            else
                _state.RawFlags2 = 0;

            // On the main menu or loading screen (spinning ship), Elite writes Flags=0 and Flags2=0 without real telemetry
            _state.HasStatusTelemetry = hasFlagsNumber && (_state.RawFlags != 0 || _state.RawFlags2 != 0);
            if (_state.HasStatusTelemetry)
                _state.HasShieldTelemetry = true;

            _state.Oxygen = TryGetStatusDouble(root, "Oxygen");
            _state.Health = TryGetStatusDouble(root, "Health");
            _state.TemperatureKelvin = TryGetStatusDouble(root, "Temperature");
            _state.Gravity = TryGetStatusDouble(root, "Gravity");
            _state.SelectedWeapon = root.TryGetProperty("SelectedWeapon", out var weaponProp) &&
                weaponProp.ValueKind == JsonValueKind.String ? weaponProp.GetString() ?? "" : "";
            _state.HasOnFootTelemetry = _state.Oxygen.HasValue || _state.Health.HasValue ||
                _state.TemperatureKelvin.HasValue || _state.Gravity.HasValue ||
                !string.IsNullOrWhiteSpace(_state.SelectedWeapon);

            if (root.TryGetProperty("Pips", out var pipsProp) &&
                pipsProp.ValueKind == JsonValueKind.Array &&
                pipsProp.GetArrayLength() >= 3)
            {
                _state.PipsSys = pipsProp[0].GetInt32() / 2.0;
                _state.PipsEng = pipsProp[1].GetInt32() / 2.0;
                _state.PipsWep = pipsProp[2].GetInt32() / 2.0;
            }

            if (root.TryGetProperty("FireGroup", out var fgProp))
                _state.FireGroup = fgProp.GetInt32();

            if (root.TryGetProperty("GuiFocus", out var gfProp))
                _state.GuiFocus = gfProp.GetInt32();

            if (root.TryGetProperty("Fuel", out var fuelProp) && fuelProp.ValueKind == JsonValueKind.Object)
            {
                bool hasMain = false;
                if (fuelProp.TryGetProperty("FuelMain", out var fm) && fm.ValueKind == JsonValueKind.Number)
                {
                    _state.FuelMain = Math.Round(fm.GetDouble(), 2);
                    hasMain = true;
                }
                if (fuelProp.TryGetProperty("FuelReservoir", out var fr) && fr.ValueKind == JsonValueKind.Number)
                    _state.FuelReservoir = Math.Round(fr.GetDouble(), 2);
                _state.HasFuelTelemetry = hasMain;
            }
            else
            {
                _state.HasFuelTelemetry = false;
            }

            if (root.TryGetProperty("Cargo", out var cargoProp))
                _state.CargoUsed = (int)Math.Round(cargoProp.GetDouble());

            if (root.TryGetProperty("LegalState", out var legalProp))
                _state.LegalState = legalProp.GetString() ?? "Clean";

            if (root.TryGetProperty("Balance", out var balProp))
                _state.Credits = balProp.GetInt64();

            if (root.TryGetProperty("BodyName", out var bodyProp))
                _state.BodyName = bodyProp.GetString() ?? "";

            if (root.TryGetProperty("Latitude", out var latProp))
                _state.Latitude = Math.Round(latProp.GetDouble(), 4);

            if (root.TryGetProperty("Longitude", out var lonProp))
                _state.Longitude = Math.Round(lonProp.GetDouble(), 4);

            if (root.TryGetProperty("Altitude", out var altProp))
                _state.Altitude = Math.Round(altProp.GetDouble(), 1);

            if (root.TryGetProperty("Heading", out var headProp))
                _state.Heading = Math.Round(headProp.GetDouble(), 0);
            if (root.TryGetProperty("PlanetRadius", out var radiusProp) &&
                radiusProp.ValueKind == JsonValueKind.Number &&
                radiusProp.TryGetDouble(out double planetRadius))
                _state.PlanetRadius = planetRadius;

            if (root.TryGetProperty("Destination", out var destProp) &&
                destProp.ValueKind == JsonValueKind.Object)
            {
                _state.DestinationName = ReadJsonString(destProp, "Name");
                _state.DestinationSystem = ReadJsonString(destProp, "System");
                _state.DestinationBody = ReadJsonString(destProp, "Body");
            }

            if (_state.Gravity.HasValue && _state.Gravity.Value > 0)
                _state.ApproachBodyGravityG = Math.Round(_state.Gravity.Value, 2);

            if (_state.ExobiologyTracker is { SamplesCollected: > 0 and < 3 } tracker &&
                _activeExoSampleCoords.Count > 0 &&
                _state.HasLatLong &&
                _state.PlanetRadius > 0)
            {
                double minDist = _activeExoSampleCoords.Min(coord =>
                    SurfaceNavigationService.CalculateDistanceMeters(
                        coord.Lat, coord.Lon, _state.Latitude, _state.Longitude, _state.PlanetRadius));
                var nextTracker = tracker.Snapshot();
                nextTracker.CurrentDistanceMeters = Math.Round(minDist, 0);
                nextTracker.PlayerHeadingDegrees = _state.Heading;
                nextTracker.SamplePoints = ExobiologyTrackerState.ProjectSamplePoints(
                    _activeExoSampleCoords, _state.Latitude, _state.Longitude, _state.Heading, _state.PlanetRadius);
                _state.ExobiologyTracker = nextTracker;
            }

            MarkSnapshotFileRead(path);
        }
        catch
        {
            // El juego puede estar escribiendo en ese instante
        }
    }


    private void ReadNavRouteJson(string path)
    {
        if (!ShouldReadSnapshotFile(path)) return;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Route", out var routeProp) || routeProp.ValueKind != JsonValueKind.Array)
                return;

            var list = new List<RouteWaypoint>();
            double startX = 0, startY = 0, startZ = 0;
            double prevX = 0, prevY = 0, prevZ = 0;
            int idx = 1;

            foreach (var item in routeProp.EnumerateArray())
            {
                string sys = item.TryGetProperty("StarSystem", out var s) ? (s.GetString() ?? "") : "";
                string starClass = item.TryGetProperty("StarClass", out var sc) ? (sc.GetString() ?? "") : "";
                double x = 0, y = 0, z = 0;
                if (item.TryGetProperty("StarPos", out var pos) &&
                    pos.ValueKind == JsonValueKind.Array &&
                    pos.GetArrayLength() >= 3)
                {
                    x = pos[0].GetDouble();
                    y = pos[1].GetDouble();
                    z = pos[2].GetDouble();
                }

                if (idx == 1)
                {
                    startX = prevX = x;
                    startY = prevY = y;
                    startZ = prevZ = z;
                }

                double dxTotal = x - startX, dyTotal = y - startY, dzTotal = z - startZ;
                double dxStep = x - prevX, dyStep = y - prevY, dzStep = z - prevZ;
                double distTotal = Math.Sqrt(dxTotal * dxTotal + dyTotal * dyTotal + dzTotal * dzTotal);
                double distStep = Math.Sqrt(dxStep * dxStep + dyStep * dyStep + dzStep * dzStep);
                prevX = x; prevY = y; prevZ = z;

                list.Add(new RouteWaypoint
                {
                    Index = idx++,
                    StarSystem = sys,
                    StarClass = starClass,
                    DistanceFromStartLy = Math.Round(distTotal, 1),
                    StepDistanceLy = Math.Round(distStep, 1),
                    IsCurrentSystem = string.Equals(sys, _state.CurrentSystem, StringComparison.OrdinalIgnoreCase)
                });
            }

            _state.NavRoute = list;
            UpdateRouteFuelAnalysis();
            bool hadStamp = _snapshotFileStamps.ContainsKey(path);
            MarkSnapshotFileRead(path);
            if (_hasCompletedInitialJournalLoad && hadStamp)
                SnapshotFileUpdated?.Invoke("NavRoute", doc.RootElement.Clone());
        }
        catch
        {
            // Ignorar lectura concurrente
        }
    }

    private void ReadCargoJson(string path)
    {
        if (!ShouldReadSnapshotFile(path)) return;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Inventory", out var invProp) || invProp.ValueKind != JsonValueKind.Array)
                return;

            var list = new List<CargoItem>();
            string vessel = ReadJsonString(root, "Vessel");
            foreach (var item in invProp.EnumerateArray())
            {
                string name = item.TryGetProperty("Name", out var n) ? (n.GetString() ?? "") : "";
                string loc = item.TryGetProperty("Name_Localised", out var nl) ? (nl.GetString() ?? name) : name;
                int count = item.TryGetProperty("Count", out var c) ? c.GetInt32() : 0;
                int stolen = item.TryGetProperty("Stolen", out var st) ? st.GetInt32() : 0;

                list.Add(new CargoItem
                {
                    Name = name,
                    DisplayName = loc,
                    Count = count,
                    Stolen = stolen,
                    Vessel = vessel,
                    MissionId = ReadJsonIdentifier(item, "MissionID")
                });
            }

            _state.CargoInventory = list;
            _state.LimpetCount = list.Where(static item => item.Name.Equals("drones", StringComparison.OrdinalIgnoreCase)).Sum(static item => item.Count);
            _state.CargoVessel = vessel;
            _state.CargoTimestamp = ReadJsonTimestamp(root, "timestamp") ?? DateTimeOffset.UtcNow;
            MarkSnapshotFileRead(path);
        }
        catch
        {
            // Ignorar lectura concurrente
        }
    }

    private void ReadMarketJson(string path)
    {
        if (!ShouldReadSnapshotFile(path)) return;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            var snapshot = new MarketSnapshot
            {
                Timestamp = ReadJsonTimestamp(root, "timestamp") ?? DateTimeOffset.UtcNow,
                MarketId = ReadJsonInt64(root, "MarketID") ?? ReadJsonInt64(root, "marketId"),
                StationName = ReadJsonString(root, "StationName", ReadJsonString(root, "stationName")),
                StarSystem = ReadJsonString(root, "StarSystem")
            };

            if (root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    snapshot.Items.Add(new MarketCommodityItem
                    {
                        Name = ReadJsonString(item, "Name"),
                        DisplayName = ReadJsonString(item, "Name_Localised", ReadJsonString(item, "Name")),
                        Category = ReadJsonString(item, "Category"),
                        DisplayCategory = ReadJsonString(item, "Category_Localised", ReadJsonString(item, "Category")),
                        BuyPrice = ReadJsonInt64(item, "BuyPrice") ?? 0,
                        SellPrice = ReadJsonInt64(item, "SellPrice") ?? 0,
                        MeanPrice = ReadJsonInt64(item, "MeanPrice") ?? 0,
                        Stock = ReadJsonInt64(item, "Stock") ?? 0,
                        Demand = ReadJsonInt64(item, "Demand") ?? 0,
                        StockBracket = (int)(ReadJsonInt64(item, "StockBracket") ?? 0),
                        DemandBracket = (int)(ReadJsonInt64(item, "DemandBracket") ?? 0),
                        Consumer = ReadJsonBool(item, "Consumer"),
                        Producer = ReadJsonBool(item, "Producer"),
                        Rare = ReadJsonBool(item, "Rare")
                    });
                }
            }

            _state.Market = snapshot;
            bool hadStamp = _snapshotFileStamps.ContainsKey(path);
            MarkSnapshotFileRead(path);
            if (_hasCompletedInitialJournalLoad && hadStamp)
                SnapshotFileUpdated?.Invoke("Market", root.Clone());
        }
        catch
        {
            // El juego puede estar escribiendo el archivo en ese instante.
        }
    }

    private void ReadOutfittingJson(string path)
    {
        if (!ShouldReadSnapshotFile(path)) return;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            var snapshot = new OutfittingSnapshot
            {
                Timestamp = ReadJsonTimestamp(root, "timestamp") ?? DateTimeOffset.UtcNow,
                MarketId = ReadJsonInt64(root, "MarketID") ?? ReadJsonInt64(root, "marketId"),
                StationName = ReadJsonString(root, "StationName", ReadJsonString(root, "stationName")),
                StarSystem = ReadJsonString(root, "StarSystem"),
                Horizons = ReadJsonNullableBool(root, "Horizons")
            };

            if (root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    string name = ReadJsonString(item, "Name");
                    string localised = ReadJsonString(item, "Name_Localised");
                    bool hasLocalised = !string.IsNullOrWhiteSpace(localised);
                    snapshot.Items.Add(new OutfittingItem
                    {
                        Id = ReadJsonIdentifier(item, "id"),
                        Name = name,
                        HasLocalisedName = hasLocalised,
                        DisplayName = hasLocalised
                            ? localised
                            : ModuleDisplayCatalog.Format(name, _configService.Current.InterfaceLanguage),
                        BuyPrice = ReadJsonInt64(item, "BuyPrice") ?? 0
                    });
                }
            }

            _state.Outfitting = snapshot;
            bool hadStamp = _snapshotFileStamps.ContainsKey(path);
            MarkSnapshotFileRead(path);
            if (_hasCompletedInitialJournalLoad && hadStamp)
                SnapshotFileUpdated?.Invoke("Outfitting", root.Clone());
        }
        catch
        {
            // El juego puede estar escribiendo el archivo en ese instante.
        }
    }

    /// <summary>
    /// Re-localizes cached outfitting and ship module display names immediately when the UI language changes.
    /// </summary>
    private void RelocalizeModuleNames()
    {
        string lang = _configService.Current.InterfaceLanguage;
        if (string.Equals(_lastModuleLocalizationLang, lang, StringComparison.OrdinalIgnoreCase))
            return;
        _lastModuleLocalizationLang = lang;

        if (_state.Outfitting.Items.Count > 0)
        {
            foreach (var item in _state.Outfitting.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.Name) && !item.HasLocalisedName)
                    item.DisplayName = ModuleDisplayCatalog.Format(item.Name, lang);
            }
            _state.Outfitting = _state.Outfitting.Snapshot();
        }

        if (_shipModulesBySlot.Count > 0)
        {
            foreach (var mod in _shipModulesBySlot.Values)
            {
                if (!string.IsNullOrWhiteSpace(mod.Item))
                    mod.DisplayName = ModuleDisplayCatalog.Format(mod.Item, lang);
            }
            SyncShipModulesToState();
        }
    }

    private void ReadShipyardJson(string path)
    {
        if (!ShouldReadSnapshotFile(path)) return;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            var snapshot = new ShipyardSnapshot
            {
                Timestamp = ReadJsonTimestamp(root, "timestamp") ?? DateTimeOffset.UtcNow,
                MarketId = ReadJsonInt64(root, "MarketID") ?? ReadJsonInt64(root, "marketId"),
                StationName = ReadJsonString(root, "StationName", ReadJsonString(root, "stationName")),
                StarSystem = ReadJsonString(root, "StarSystem"),
                Horizons = ReadJsonNullableBool(root, "Horizons"),
                AllowCobraMkIv = ReadJsonNullableBool(root, "allowCobraMkIV")
            };

            if (root.TryGetProperty("PriceList", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    string shipType = ReadJsonString(item, "ShipType");
                    snapshot.Items.Add(new ShipyardItem
                    {
                        ShipType = shipType,
                        DisplayName = ReadJsonString(item, "ShipType_Localised", shipType),
                        ShipPrice = ReadJsonInt64(item, "ShipPrice") ?? 0
                    });
                }
            }

            // Preserve an existing multi-ship catalog (e.g. from EDSM fallback) if Odyssey writes a 1-ship Shipyard.json for the same station
            bool keepExisting = snapshot.Items.Count <= 1 &&
                _state.Shipyard.Available &&
                _state.Shipyard.Items.Count > 1 &&
                !string.IsNullOrWhiteSpace(snapshot.StationName) &&
                string.Equals(_state.Shipyard.StationName, snapshot.StationName, StringComparison.OrdinalIgnoreCase);

            if (!keepExisting)
                _state.Shipyard = snapshot;
            bool hadStamp = _snapshotFileStamps.ContainsKey(path);
            MarkSnapshotFileRead(path);
            if (_hasCompletedInitialJournalLoad && hadStamp)
                SnapshotFileUpdated?.Invoke("Shipyard", root.Clone());
        }
        catch
        {
            // El juego puede estar escribiendo el archivo en ese instante.
        }
    }

    /// <summary>
    /// Workaround for the Elite Dangerous Odyssey Shipyard.json bug where PriceList contains 0 or only 1 ship.
    /// Automatically fetches the full station shipyard catalog from EDSM's public API once per docked station.
    /// </summary>
    private void TryEnsureEdsmShipyardFallback()
    {
        if (!_state.Docked ||
            string.IsNullOrWhiteSpace(_state.CurrentSystem) ||
            string.IsNullOrWhiteSpace(_state.StationName))
        {
            return;
        }

        string system = _state.CurrentSystem.Trim();
        string station = _state.StationName.Trim();

        // Local Shipyard.json already has a valid multi-ship catalog for this exact station
        if (_state.Shipyard.Available &&
            _state.Shipyard.Items.Count > 1 &&
            string.Equals(_state.Shipyard.StationName, station, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_configService.Current.EnableEdsmSync)
            return;

        string key = $"{system}|{station}";
        if (string.Equals(_lastEdsmShipyardLookupKey, key, StringComparison.OrdinalIgnoreCase))
            return;
        _lastEdsmShipyardLookupKey = key;

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var snapshot = await FetchEdsmShipyardAsync(system, station);
                if (snapshot is null)
                    return;

                lock (_refreshLock)
                {
                    if (!string.Equals(_state.CurrentSystem, system, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(_state.StationName, station, StringComparison.OrdinalIgnoreCase))
                        return;

                    if (_state.Shipyard.Available &&
                        _state.Shipyard.Items.Count > 1 &&
                        string.Equals(_state.Shipyard.StationName, station, StringComparison.OrdinalIgnoreCase))
                        return;

                    _state.Shipyard = snapshot;
                    _state.LastUpdated = DateTime.UtcNow;
                    PublishState();
                }
            }
            catch (Exception ex)
            {
                AppDiagnosticsLogger.Warn("FileWatcher.Shipyard", $"EDSM shipyard fallback failed for '{station}': {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Fetches a station's shipyard catalog from EDSM's public API (shared by Watcher fallback and COVAS AI).
    /// </summary>
    internal static async System.Threading.Tasks.Task<ShipyardSnapshot?> FetchEdsmShipyardAsync(string system, string station)
    {
        if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(station))
            return null;

        try
        {
            string url = $"https://www.edsm.net/api-system-v1/stations/shipyard?systemName={Uri.EscapeDataString(system.Trim())}&stationName={Uri.EscapeDataString(station.Trim())}";
            using var resp = await EdsmShipyardHttp.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
                return null;

            string json = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json) || json.Trim() is "{}" or "[]")
                return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ships", out var shipsProp) || shipsProp.ValueKind != JsonValueKind.Array)
                return null;

            var items = new List<ShipyardItem>();
            foreach (var ship in shipsProp.EnumerateArray())
            {
                string name = ReadJsonString(ship, "name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                long price = ReadJsonInt64(ship, "baseValue")
                    ?? ReadJsonInt64(ship, "price")
                    ?? ReadJsonInt64(ship, "ShipPrice")
                    ?? 0;
                items.Add(new ShipyardItem
                {
                    ShipType = name,
                    DisplayName = name,
                    ShipPrice = price
                });
            }

            if (items.Count == 0)
                return null;

            return new ShipyardSnapshot
            {
                Timestamp = ReadJsonTimestamp(root, "updateTime") ?? DateTimeOffset.UtcNow,
                MarketId = ReadJsonInt64(root, "marketId"),
                DataSource = "EDSM",
                StationName = station.Trim(),
                StarSystem = system.Trim(),
                Items = items.OrderBy(static i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList()
            };
        }
        catch
        {
            return null;
        }
    }

    private bool ShouldReadSnapshotFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                _snapshotFileStamps.Remove(path);
                return false;
            }

            return !_snapshotFileStamps.TryGetValue(path, out var stamp) ||
                stamp.Length != info.Length ||
                stamp.LastWriteUtcTicks != info.LastWriteTimeUtc.Ticks;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void MarkSnapshotFileRead(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists)
            _snapshotFileStamps[path] = (info.Length, info.LastWriteTimeUtc.Ticks);
        _stateDirty = true;
    }

    private static string ReadJsonString(JsonElement data, string propertyName, string fallback = "") =>
        data.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static string ReadJsonIdentifier(JsonElement data, string propertyName) =>
        data.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString()
            : "";

    private static long? ReadJsonInt64(JsonElement data, string propertyName) =>
        data.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out long number)
            ? number
            : null;

    private static bool ReadJsonBool(JsonElement data, string propertyName) =>
        ReadJsonNullableBool(data, propertyName) ?? false;

    private static bool? ReadJsonNullableBool(JsonElement data, string propertyName) =>
        data.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static DateTimeOffset? ReadJsonTimestamp(JsonElement data, string propertyName) =>
        data.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), out var timestamp)
            ? timestamp
            : null;

    private static double? TryGetStatusDouble(JsonElement data, string propertyName) =>
        data.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double number) &&
        double.IsFinite(number)
            ? number
            : null;


    private static bool IsHandledSnapshotFileName(string fileName) =>
        fileName.Equals("Status.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("NavRoute.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("Cargo.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("Market.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("Outfitting.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("Shipyard.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("ModulesInfo.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("ShipLocker.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("FCMaterials.json", StringComparison.OrdinalIgnoreCase);

    private void ReadModulesInfoJson(string path)
    {
        if (!ShouldReadSnapshotFile(path)) return;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Modules", out var modulesProp) || modulesProp.ValueKind != JsonValueKind.Array)
                return;

            foreach (var module in modulesProp.EnumerateArray())
            {
                string slot = ReadJsonString(module, "Slot");
                string item = ReadJsonString(module, "Item");
                if (string.IsNullOrWhiteSpace(slot) || string.IsNullOrWhiteSpace(item))
                    continue;

                if (!_shipModulesBySlot.TryGetValue(slot, out var existing))
                {
                    existing = new ShipModuleItem
                    {
                        Slot = slot,
                        Item = item,
                        DisplayName = FormatModuleDisplayName(item)
                    };
                    _shipModulesBySlot[slot] = existing;
                }

                if (TryGetJournalDouble(module, "Power", out double power))
                    existing.PowerDrawMw = Math.Round(power, 2);
                if (TryGetJournalInt(module, "Priority", out int prio))
                    existing.Priority = prio + 1;
            }

            SyncShipModulesToState();
            MarkSnapshotFileRead(path);
        }
        catch
        {
            // Ignorar lectura concurrente
        }
    }

    private void ReadShipLockerJson(string path)
    {
        if (!ShouldReadSnapshotFile(path)) return;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            ParseShipLockerElement(doc.RootElement);
            MarkSnapshotFileRead(path);
        }
        catch
        {
            // Ignorar lectura concurrente
        }
    }

    private void ParseShipLockerElement(JsonElement data)
    {
        var items = new List<BackpackItem>();
        foreach (string category in new[] { "Items", "Components", "Consumables", "Data" })
            AppendBackpackItems(items, data, category);
        if (items.Count > 0 ||
            data.TryGetProperty("Items", out _) ||
            data.TryGetProperty("Components", out _) ||
            data.TryGetProperty("Consumables", out _) ||
            data.TryGetProperty("Data", out _))
        {
            _state.ShipLockerInventory = items
                .Where(static i => i.Count > 0)
                .OrderBy(static i => i.Type, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }


    private void ReadCarrierMaterialsJson(string path)
    {
        if (!ShouldReadSnapshotFile(path)) return;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            string station = ReadJsonString(root, "CarrierName");
            if (string.IsNullOrWhiteSpace(station))
                station = ReadJsonString(root, "CarrierID");

            var items = new List<CarrierMaterialItem>();
            if (root.TryGetProperty("Items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in itemsProp.EnumerateArray())
                {
                    string raw = ReadJsonString(entry, "Name");
                    string display = GetJournalText(entry, "Name_Localised", "Name") ?? raw;
                    if (string.IsNullOrWhiteSpace(display))
                        continue;
                    long price = TryGetJournalLong(entry, "Price", out long p) ? p : 0;
                    int stock = TryGetJournalInt(entry, "Stock", out int st) ? st : 0;
                    int demand = TryGetJournalInt(entry, "Demand", out int dm) ? dm : 0;
                    items.Add(new CarrierMaterialItem
                    {
                        Name = raw,
                        DisplayName = display,
                        Price = price,
                        Stock = stock,
                        Demand = demand
                    });
                }
            }

            _state.CarrierMaterialsStation = station;
            _state.CarrierMaterials = items
                .OrderByDescending(static i => i.Stock > 0 || i.Demand > 0)
                .ThenBy(static i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            bool hadStamp = _snapshotFileStamps.ContainsKey(path);
            MarkSnapshotFileRead(path);
            if (_hasCompletedInitialJournalLoad && hadStamp)
                SnapshotFileUpdated?.Invoke("FCMaterials", root.Clone());
        }
        catch
        {
            // Ignorar JSON en escritura
        }
    }

    internal void SyncMarketColonisationNeeds(bool force = false)
    {
        if (_state.Market.Items.Count == 0)
            return;

        if (!force &&
            ReferenceEquals(_lastSyncedMarketRef, _state.Market) &&
            ReferenceEquals(_lastSyncedColonisationRef, _state.Colonisation))
        {
            return;
        }
        _lastSyncedMarketRef = _state.Market;
        _lastSyncedColonisationRef = _state.Colonisation;

        var neededByKey = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var res in _state.Colonisation.Resources)
        {
            long remaining = Math.Max(0, res.RequiredAmount - res.ProvidedAmount);
            if (remaining <= 0)
                continue;
            if (!string.IsNullOrWhiteSpace(res.Name))
                neededByKey[NormalizeCommodityKey(res.Name)] = remaining;
            if (!string.IsNullOrWhiteSpace(res.DisplayName))
                neededByKey[NormalizeCommodityKey(res.DisplayName)] = remaining;
        }

        foreach (var item in _state.Market.Items)
        {
            long needed = 0;
            if (neededByKey.TryGetValue(NormalizeCommodityKey(item.Name), out long n1))
                needed = n1;
            else if (neededByKey.TryGetValue(NormalizeCommodityKey(item.DisplayName), out long n2))
                needed = n2;
            item.ColonisationNeeded = needed;
        }
    }

    internal static string NormalizeCommodityKey(string raw) =>
        (raw ?? "").Trim()
            .Replace("$", "", StringComparison.Ordinal)
            .Replace("_name;", "", StringComparison.OrdinalIgnoreCase)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .ToLowerInvariant();

    internal static bool CommodityMatches(string? a1, string? a2, string? b1, string? b2 = null)
    {
        string ka1 = NormalizeCommodityKey(a1 ?? "");
        string ka2 = NormalizeCommodityKey(a2 ?? "");
        string kb1 = NormalizeCommodityKey(b1 ?? "");
        string kb2 = NormalizeCommodityKey(b2 ?? "");

        return (!string.IsNullOrEmpty(ka1) && (ka1 == kb1 || ka1 == kb2)) ||
               (!string.IsNullOrEmpty(ka2) && (ka2 == kb1 || ka2 == kb2));
    }

}
