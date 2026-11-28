// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/Telemetry/EliteFileWatcherService.Exploration.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Exploration body tracking, history restoration, and unsold cartography/exobiology accounting.
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
/// Exploration body tracking, history restoration, and unsold cartography/exobiology accounting.
/// </summary>
public partial class EliteFileWatcherService
{
    private void UpdateExplorationBodyState(string eventType, JsonElement data, bool isHistoryRestore = false)
    {
        if (eventType is "Location" or "FSDJump")
        {
            string? destination = GetJournalRawText(data, "StarSystem");
            string? eventSystemAddress = GetJournalIdentifier(data, "SystemAddress");
            bool systemChanged =
                !string.IsNullOrWhiteSpace(destination) &&
                !string.Equals(destination, _state.CurrentSystem, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(eventSystemAddress) &&
                !string.Equals(eventSystemAddress, _currentSystemAddress, StringComparison.Ordinal);
            if (systemChanged)
            {
                _explorationBodiesChanged = true;
                _state.SystemTotalBodies = null;
                _state.SystemScannedBodies = 0;
                _state.SystemAllBodiesFound = false;
                _state.ApproachBodyName = "";
                _state.ApproachBodyGravityG = null;
                _activeExoSampleCoords.Clear();
                _state.ExobiologyTracker = null;
            }

            if (!string.IsNullOrWhiteSpace(eventSystemAddress))
            {
                _currentSystemAddress = eventSystemAddress;
                _state.SystemAddress = eventSystemAddress;
            }
        }

        if (eventType is "Location" or "Docked" or "ApproachSettlement" or "SupercruiseExit")
        {
            TryRecordLocalBodyStationOrSettlement(eventType, data);
        }

        if (eventType is not ("Scan" or "FSSBodySignals" or "SAASignalsFound" or "SAAScanComplete" or "ScanOrganic"))
            return;

        string systemName = GetJournalRawText(data, "StarSystem") ?? _state.CurrentSystem;
        string systemAddress = GetJournalIdentifier(data, "SystemAddress") ?? systemName;
        string? bodyName = GetJournalRawText(data, "BodyName");
        string? bodyId = GetJournalIdentifier(data, eventType == "ScanOrganic" ? "Body" : "BodyID");
        if (eventType == "ScanOrganic" &&
            bodyId != null &&
            _explorationBodies.TryGetValue($"{systemAddress}:{bodyId}", out var knownBody))
            bodyName ??= knownBody.BodyName;

        bodyName ??= bodyId == null ? null : $"Cuerpo {bodyId}";
        if (string.IsNullOrWhiteSpace(bodyName))
            return;

        bodyId ??= bodyName;
        string id = $"{systemAddress}:{bodyId}";
        if (!_explorationBodies.TryGetValue(id, out var body))
        {
            // Check if a Spansh/EDSM pre-populated entry exists for the same body (by BodyName or primary star alias) and upgrade it in-place
            var spanshMatch = _explorationBodies.FirstOrDefault(kv =>
                string.Equals(kv.Value.SystemName, systemName, StringComparison.OrdinalIgnoreCase) &&
                AreEquivalentBodyNames(systemName, kv.Value.BodyName, bodyName));
            if (spanshMatch.Value is not null)
            {
                body = spanshMatch.Value;
                if (!string.Equals(spanshMatch.Key, id, StringComparison.Ordinal))
                {
                    _explorationBodies.Remove(spanshMatch.Key);
                    body.Id = id;
                    _explorationBodies[id] = body;
                    _explorationBodyOrder.Enqueue(id);
                }
                body.DataSource = "Local+Spansh";
            }
            else
            {
                body = new ExplorationBodyItem { Id = id, DataSource = "Local" };
                _explorationBodies[id] = body;
                _explorationBodyOrder.Enqueue(id);
                while (_explorationBodyOrder.Count > 2000)
                    _explorationBodies.Remove(_explorationBodyOrder.Dequeue());
            }
        }
        else if (body.IsExternalOnly)
        {
            body.DataSource = body.IsSpanshOnly ? "Local+Spansh" : "Local";
        }

        body.SystemName = systemName;
        body.BodyName = bodyName;
        if (TryGetJournalInt(data, eventType == "ScanOrganic" ? "Body" : "BodyID", out int parsedBodyId))
            body.BodyId = parsedBodyId;
        AttachPendingLocalStations(body, systemAddress);

        if (eventType == "Scan")
        {
            if (!isHistoryRestore)
                _sessionBodiesScanned++;
            if (data.TryGetProperty("Parents", out var parentsProp) && parentsProp.ValueKind == JsonValueKind.Array)
            {
                var parsedParents = new List<ExplorationParentRef>();
                foreach (var pObj in parentsProp.EnumerateArray())
                {
                    if (pObj.ValueKind != JsonValueKind.Object)
                        continue;
                    foreach (var prop in pObj.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int pId))
                        {
                            parsedParents.Add(new ExplorationParentRef(prop.Name, pId));
                            break;
                        }
                    }
                }
                ApplyParentsHierarchy(body, parsedParents);
            }
            body.PlanetClass = GetJournalRawText(data, "PlanetClass") ?? body.PlanetClass;
            body.StarType = GetJournalRawText(data, "StarType") ?? body.StarType;
            body.TerraformState = GetJournalRawText(data, "TerraformState") ?? body.TerraformState;
            body.Atmosphere = GetJournalRawText(data, "Atmosphere") ?? body.Atmosphere;
            body.AtmosphereType = GetJournalRawText(data, "AtmosphereType") ?? body.AtmosphereType;
            body.Volcanism = GetJournalRawText(data, "Volcanism") ?? body.Volcanism;
            if (TryGetJournalDouble(data, "MassEM", out double massEarth) && massEarth > 0)
                body.MassEarth = massEarth;
            if (TryGetJournalDouble(data, "StellarMass", out double stellarMass) && stellarMass > 0)
                body.StellarMass = stellarMass;
            body.RadiusMeters = ReadJournalDouble(data, "Radius") ?? body.RadiusMeters;
            body.SurfaceGravity = ReadJournalDouble(data, "SurfaceGravity") ?? body.SurfaceGravity;
            body.SurfaceTemperatureKelvin = ReadJournalDouble(data, "SurfaceTemperature") ?? body.SurfaceTemperatureKelvin;
            body.SurfacePressure = ReadJournalDouble(data, "SurfacePressure") ?? body.SurfacePressure;
            body.AgeMillionsOfYears = ReadJournalDouble(data, "Age_MY") ?? body.AgeMillionsOfYears;
            body.DistanceFromArrivalLs = ReadJournalDouble(data, "DistanceFromArrivalLS") ?? body.DistanceFromArrivalLs;
            body.RotationPeriodSeconds = ReadJournalDouble(data, "RotationPeriod") ?? body.RotationPeriodSeconds;
            body.OrbitalPeriodSeconds = ReadJournalDouble(data, "OrbitalPeriod") ?? body.OrbitalPeriodSeconds;
            body.SemiMajorAxisMeters = ReadJournalDouble(data, "SemiMajorAxis") ?? body.SemiMajorAxisMeters;
            body.Eccentricity = ReadJournalDouble(data, "Eccentricity") ?? body.Eccentricity;
            body.OrbitalInclination = ReadJournalDouble(data, "OrbitalInclination") ?? body.OrbitalInclination;
            body.Periapsis = ReadJournalDouble(data, "Periapsis") ?? body.Periapsis;
            body.AxialTilt = ReadJournalDouble(data, "AxialTilt") ?? body.AxialTilt;
            body.TidalLock = GetJournalNullableBool(data, "TidalLock") ?? body.TidalLock;
            body.Materials = ReadExplorationMaterials(data, "Materials", "Name", "Name_Localised");
            body.AtmosphereComposition = ReadExplorationMaterials(data, "AtmosphereComposition", "Name", "Name_Localised");
            if (data.TryGetProperty("Composition", out var composition) &&
                composition.ValueKind == JsonValueKind.Object)
            {
                body.Composition = new ExplorationComposition
                {
                    Ice = ReadJournalDouble(composition, "Ice"),
                    Rock = ReadJournalDouble(composition, "Rock"),
                    Metal = ReadJournalDouble(composition, "Metal")
                };
            }
            if (data.TryGetProperty("Rings", out var rings) && rings.ValueKind == JsonValueKind.Array)
            {
                body.Rings = rings.EnumerateArray().Select(ring => new ExplorationRingItem
                {
                    Name = GetJournalText(ring, "Name_Localised", "Name") ?? "",
                    RingClass = GetJournalText(ring, "RingClass") ?? "",
                    MassMegatons = ReadJournalDouble(ring, "MassMT"),
                    InnerRadiusMeters = ReadJournalDouble(ring, "InnerRad"),
                    OuterRadiusMeters = ReadJournalDouble(ring, "OuterRad")
                }).ToList();
            }
            body.Landable = GetJournalNullableBool(data, "Landable") ?? body.Landable;
            body.WasDiscovered = GetJournalNullableBool(data, "WasDiscovered") ?? body.WasDiscovered;
            body.WasMapped = GetJournalNullableBool(data, "WasMapped") ?? body.WasMapped;
        }
        else if (eventType == "SAAScanComplete")
        {
            body.HasCompletedMapping = true;
            body.MappingWasEfficient =
                TryGetJournalInt(data, "ProbesUsed", out int probesUsed) &&
                TryGetJournalInt(data, "EfficiencyTarget", out int efficiencyTarget) &&
                efficiencyTarget > 0 &&
                probesUsed <= efficiencyTarget;
        }
        else if (eventType is "FSSBodySignals" or "SAASignalsFound")
        {
            body.BiologicalSignalCount = 0;
            body.GeologicalSignalCount = 0;
            if (data.TryGetProperty("Signals", out var signals) &&
                signals.ValueKind == JsonValueKind.Array)
            {
                foreach (var signal in signals.EnumerateArray())
                {
                    if (!TryGetJournalInt(signal, "Count", out int count) || count < 0)
                        continue;

                    string? rawType = GetJournalRawText(signal, "Type");
                    string? locType = GetJournalRawText(signal, "Type_Localised");
                    string combinedType = $"{rawType} {locType}";
                    if (combinedType.Contains("Biological", StringComparison.OrdinalIgnoreCase) ||
                        combinedType.Contains("Biológ", StringComparison.OrdinalIgnoreCase) ||
                        combinedType.Contains("Biolog", StringComparison.OrdinalIgnoreCase) ||
                        combinedType.Contains("Биолог", StringComparison.OrdinalIgnoreCase))
                        body.BiologicalSignalCount += count;
                    else if (combinedType.Contains("Geological", StringComparison.OrdinalIgnoreCase) ||
                             combinedType.Contains("Geológ", StringComparison.OrdinalIgnoreCase) ||
                             combinedType.Contains("Geolog", StringComparison.OrdinalIgnoreCase) ||
                             combinedType.Contains("Геолог", StringComparison.OrdinalIgnoreCase))
                        body.GeologicalSignalCount += count;
                    else if (count > 0 &&
                             (combinedType.Contains("Station", StringComparison.OrdinalIgnoreCase) ||
                              combinedType.Contains("Installation", StringComparison.OrdinalIgnoreCase) ||
                              combinedType.Contains("Outpost", StringComparison.OrdinalIgnoreCase) ||
                              combinedType.Contains("Megaship", StringComparison.OrdinalIgnoreCase) ||
                              combinedType.Contains("Instalación", StringComparison.OrdinalIgnoreCase) ||
                              combinedType.Contains("Estación", StringComparison.OrdinalIgnoreCase)))
                    {
                        string sigLabel = !string.IsNullOrWhiteSpace(locType) ? locType : (rawType ?? "Orbital Installation");
                        if (!body.Stations.Contains(sigLabel, StringComparer.OrdinalIgnoreCase))
                            body.Stations.Add(sigLabel);
                    }
                }
            }

            if (data.TryGetProperty("Genuses", out var genuses) &&
                genuses.ValueKind == JsonValueKind.Array)
            {
                foreach (var genus in genuses.EnumerateArray())
                {
                    string? name = GetJournalText(genus, "Genus_Localised", "Genus");
                    if (!string.IsNullOrWhiteSpace(name) &&
                        !body.Genuses.Contains(name, StringComparer.OrdinalIgnoreCase))
                        body.Genuses.Add(name);
                }
            }
        }
        else if (eventType == "ScanOrganic")
        {
            string scanType = GetJournalRawText(data, "ScanType") ?? "";
            string? species = GetJournalText(data, "Species_Localised", "Species");
            string? genusName = GetJournalText(data, "Genus_Localised", "Genus");
            string? rawSpecies = GetJournalRawText(data, "Species");
            string? rawGenus = GetJournalRawText(data, "Genus");
            string lookupKey = $"{rawSpecies} {rawGenus} {species} {genusName}".Trim();
            if (scanType.Equals("Log", StringComparison.OrdinalIgnoreCase) ||
                scanType.Equals("Sample", StringComparison.OrdinalIgnoreCase))
            {
                bool isFirst = scanType.Equals("Log", StringComparison.OrdinalIgnoreCase);
                string speciesDisplay = species ?? genusName ?? "";
                if (isFirst || _state.ExobiologyTracker is null ||
                    !string.Equals(_state.ExobiologyTracker.Species, speciesDisplay, StringComparison.OrdinalIgnoreCase))
                {
                    _activeExoSampleCoords.Clear();
                }

                if (_state.HasLatLong)
                    _activeExoSampleCoords.Add((_state.Latitude, _state.Longitude));

                int sampleNumber = isFirst ? 1 : Math.Min(2, (_state.ExobiologyTracker?.SamplesCollected ?? 1) + 1);
                _state.ExobiologyTracker = new ExobiologyTrackerState
                {
                    Species = speciesDisplay,
                    Genus = genusName ?? "",
                    BodyName = body.BodyName,
                    SamplesCollected = sampleNumber,
                    RequiredDistanceMeters = ExobiologyPredictor.GetRequiredDistanceMeters(lookupKey),
                    CurrentDistanceMeters = 0,
                    LastSampleLatitude = _state.HasLatLong ? _state.Latitude : null,
                    LastSampleLongitude = _state.HasLatLong ? _state.Longitude : null,
                    PlayerHeadingDegrees = _state.Heading,
                    SamplePoints = _state.HasLatLong && _state.PlanetRadius > 0
                        ? ExobiologyTrackerState.ProjectSamplePoints(_activeExoSampleCoords, _state.Latitude, _state.Longitude, _state.Heading, _state.PlanetRadius)
                        : new List<ExoSamplePoint>()
                };
            }
            else if (scanType.Equals("Analyse", StringComparison.OrdinalIgnoreCase))
            {
                if (!isHistoryRestore)
                    _sessionOrganicsAnalysed++;
                if (!string.IsNullOrWhiteSpace(species) &&
                    !body.AnalysedSpecies.Contains(species, StringComparer.OrdinalIgnoreCase))
                    body.AnalysedSpecies.Add(species);
                if (!string.IsNullOrWhiteSpace(genusName) &&
                    !body.Genuses.Contains(genusName, StringComparer.OrdinalIgnoreCase))
                    body.Genuses.Add(genusName);
                body.BiologicalSignalCount = Math.Max(1, body.BiologicalSignalCount);

                if (!isHistoryRestore)
                {
                    _activeExoSampleCoords.Clear();
                    _state.ExobiologyTracker = null;

                    string exoKey = $"{id}:{rawSpecies ?? species ?? rawGenus ?? genusName ?? "organic"}";
                    if (_unsoldExobiologyKeys.Add(exoKey))
                    {
                        bool firstFootfall = body.WasDiscovered == false || body.WasMapped == false;
                        _state.UnsoldExobiologyCredits += ExobiologyPredictor.EstimateSpeciesCredits(lookupKey, firstFootfall);
                    }
                }
            }
        }

        ExplorationValueEstimator.UpdateEstimate(body);
        bool isNavBeaconScan = eventType == "Scan" &&
            string.Equals(GetJournalRawText(data, "ScanType"), "NavBeaconDetail", StringComparison.OrdinalIgnoreCase);
        if (!isHistoryRestore &&
            eventType is "Scan" or "SAAScanComplete" &&
            !isNavBeaconScan &&
            body.EstimatedCredits is long estCredits &&
            estCredits > 0)
        {
            _unsoldCartographyByBody[id] = estCredits;
            _state.UnsoldCartographyCredits = _unsoldCartographyByBody.Values.Sum();
        }
        _explorationBodiesChanged = true;
    }

    private void RebuildExplorationBodiesSnapshot()
    {
        var currentSystemBodies = _explorationBodies.Values
            .Where(item =>
                string.Equals(item.SystemName, _state.CurrentSystem, StringComparison.OrdinalIgnoreCase) &&
                !IsNonCelestialBody(item.BodyName, item.BodyType))
            .ToList();
        _state.SystemScannedBodies = currentSystemBodies.Count(static item => !item.IsExternalOnly);
        _state.ExplorationBodies = currentSystemBodies
            .OrderBy(static item => item.BodyId ?? int.MaxValue)
            .ThenBy(static item => item.BodyName, StringComparer.CurrentCultureIgnoreCase)
            .Select(static item => item.Snapshot())
            .ToList();
        _explorationBodiesChanged = false;
    }

    /// <summary>
    /// Filters out barycentres and asteroid belt clusters that are not stars, planets, or moons.
    /// </summary>
    private static bool IsNonCelestialBody(string? bodyName, string? bodyType)
    {
        string name = bodyName ?? "";
        string type = bodyType ?? "";
        if (type.Contains("Barycentre", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Belt", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("Ring", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Contains("Belt Cluster", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Belt", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static void ApplyParentsHierarchy(ExplorationBodyItem body, List<ExplorationParentRef> parents)
    {
        body.Parents = new List<ExplorationParentRef>(parents);
        body.HierarchyDepth = parents.Count;
        body.DirectParentBodyId = null;
        body.DirectParentType = "";
        body.BarycentreId = null;
        body.HostStarBodyId = null;
        body.ParentPlanetBodyId = null;

        for (int pIdx = 0; pIdx < parents.Count; pIdx++)
        {
            var p = parents[pIdx];
            if (pIdx == 0)
            {
                body.DirectParentType = p.Type;
                body.DirectParentBodyId = p.BodyId;
                if (p.Type.Equals("Null", StringComparison.OrdinalIgnoreCase))
                    body.BarycentreId = p.BodyId;
            }
            if (body.ParentPlanetBodyId is null && p.Type.Equals("Planet", StringComparison.OrdinalIgnoreCase))
                body.ParentPlanetBodyId = p.BodyId;
            if (body.HostStarBodyId is null && p.Type.Equals("Star", StringComparison.OrdinalIgnoreCase))
                body.HostStarBodyId = p.BodyId;
        }
    }

    private readonly Dictionary<string, List<string>> _pendingStationsByBodyKey = new(StringComparer.OrdinalIgnoreCase);

    private void TryRecordLocalBodyStationOrSettlement(string eventType, JsonElement data)
    {
        if (eventType == "ApproachSettlement")
            return;

        if (eventType == "SupercruiseExit")
        {
            string? bodyType = GetJournalRawText(data, "BodyType");
            if (!string.Equals(bodyType, "Station", StringComparison.OrdinalIgnoreCase))
                return;
        }

        string? stationType = GetJournalRawText(data, "StationType");
        if (!string.IsNullOrWhiteSpace(stationType) &&
            (stationType.Contains("FleetCarrier", StringComparison.OrdinalIgnoreCase) ||
             stationType.Equals("OnFootSettlement", StringComparison.OrdinalIgnoreCase) ||
             stationType.Contains("Crater", StringComparison.OrdinalIgnoreCase) ||
             stationType.Contains("Surface", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        string? stationName = eventType == "SupercruiseExit"
            ? GetJournalText(data, "Body_Localised", "Body")
            : GetJournalText(data, "StationName_Localised", "StationName");
        if (string.IsNullOrWhiteSpace(stationName))
            return;

        string? bodyName = eventType == "SupercruiseExit"
            ? null
            : (GetJournalRawText(data, "BodyName") ?? GetJournalRawText(data, "Body"));
        int? bodyId = TryGetJournalInt(data, "BodyID", out int parsedId) ? parsedId : null;
        if (string.IsNullOrWhiteSpace(bodyName) && !bodyId.HasValue)
            return;

        string systemName = GetJournalRawText(data, "StarSystem") ?? _state.CurrentSystem;
        string systemAddress = GetJournalIdentifier(data, "SystemAddress") ?? _currentSystemAddress ?? systemName;

        // Also store in pending map so if the body's Scan event arrives AFTER Docked/Location, it receives the station!
        void QueuePending(string pKey)
        {
            if (!_pendingStationsByBodyKey.TryGetValue(pKey, out var pList))
            {
                pList = new List<string>();
                _pendingStationsByBodyKey[pKey] = pList;
            }
            if (!pList.Contains(stationName, StringComparer.OrdinalIgnoreCase))
                pList.Add(stationName);
        }
        if (bodyId.HasValue)
            QueuePending($"{systemAddress}:{bodyId.Value}");
        if (!string.IsNullOrWhiteSpace(bodyName))
            QueuePending($"{systemName}::{bodyName}");

        ExplorationBodyItem? target = null;
        if (bodyId.HasValue && _explorationBodies.TryGetValue($"{systemAddress}:{bodyId.Value}", out var byId))
            target = byId;
        if (target is null && !string.IsNullOrWhiteSpace(bodyName))
        {
            target = _explorationBodies.Values.FirstOrDefault(b =>
                string.Equals(b.SystemName, systemName, StringComparison.OrdinalIgnoreCase) &&
                AreEquivalentBodyNames(systemName, b.BodyName, bodyName));
        }

        if (target is not null && !target.Stations.Contains(stationName, StringComparer.OrdinalIgnoreCase))
        {
            target.Stations.Add(stationName);
            _explorationBodiesChanged = true;
        }
    }

    private static bool IsDisplayableStationEntry(string? stationEntry)
    {
        if (string.IsNullOrWhiteSpace(stationEntry))
            return false;
        // Extract parenthesized type if present: "Name (Type)"
        int openIdx = stationEntry.LastIndexOf('(');
        int closeIdx = stationEntry.LastIndexOf(')');
        if (openIdx < 0 || closeIdx <= openIdx)
            return false;
        string typePart = stationEntry[(openIdx + 1)..closeIdx].Trim();
        return SpanshSystemDataService.IsSystemMapDisplayableStationType(typePart);
    }

    private void AttachPendingLocalStations(ExplorationBodyItem body, string systemAddress)
    {
        void MergeFromKey(string pKey)
        {
            if (_pendingStationsByBodyKey.TryGetValue(pKey, out var list))
            {
                foreach (string st in list)
                {
                    if (!body.Stations.Contains(st, StringComparer.OrdinalIgnoreCase))
                        body.Stations.Add(st);
                }
            }
        }
        if (body.BodyId.HasValue)
            MergeFromKey($"{systemAddress}:{body.BodyId.Value}");
        if (!string.IsNullOrWhiteSpace(body.BodyName))
        {
            MergeFromKey($"{body.SystemName}::{body.BodyName}");
            if (string.Equals(body.BodyName, $"{body.SystemName} A", StringComparison.OrdinalIgnoreCase))
                MergeFromKey($"{body.SystemName}::{body.SystemName}");
        }
    }

    /// <summary>
    /// Extracts Frontier-style StarType codes (O, B, A, F, G, K, M, L, T, Y, D, N, H, W, C, TTS, AeBe)
    /// from Spansh/EDSM descriptive strings such as "G (White-Yellow) Star" or "Neutron Star".
    /// </summary>
    private static string? ExtractStarCodeFromExternalType(string? externalType)
    {
        if (string.IsNullOrWhiteSpace(externalType))
            return null;

        string t = externalType.Trim();
        if (t.Contains("Black Hole", StringComparison.OrdinalIgnoreCase)) return "H";
        if (t.Contains("Neutron", StringComparison.OrdinalIgnoreCase)) return "N";
        if (t.Contains("White Dwarf", StringComparison.OrdinalIgnoreCase)) return "D";
        if (t.Contains("Wolf-Rayet", StringComparison.OrdinalIgnoreCase)) return "W";
        if (t.Contains("T Tauri", StringComparison.OrdinalIgnoreCase)) return "TTS";
        if (t.Contains("Herbig", StringComparison.OrdinalIgnoreCase)) return "AeBe";
        if (t.Contains("Carbon", StringComparison.OrdinalIgnoreCase) || t.StartsWith("C-", StringComparison.OrdinalIgnoreCase)) return "C";

        if (t.Contains("Star", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("dwarf", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("giant", StringComparison.OrdinalIgnoreCase) && !t.Contains("Gas giant", StringComparison.OrdinalIgnoreCase))
        {
            int spaceIdx = t.IndexOf(' ');
            string firstToken = (spaceIdx > 0 ? t[..spaceIdx] : t).Trim().ToUpperInvariant();
            if (firstToken is "O" or "B" or "A" or "F" or "G" or "K" or "M" or "L" or "T" or "Y" or "S" or "MS" or "CN" or "CJ")
                return firstToken;
            if (firstToken.Length == 2 && firstToken[0] is 'D' or 'W' or 'C')
                return firstToken;
            return "G";
        }

        return null;
    }

    private static bool AreEquivalentBodyNames(string systemName, string nameA, string nameB)
    {
        if (string.Equals(nameA, nameB, StringComparison.OrdinalIgnoreCase))
            return true;
        // Handle primary star naming mismatch: "SystemName" vs "SystemName A"
        string sysWithA = $"{systemName} A";
        return (string.Equals(nameA, systemName, StringComparison.OrdinalIgnoreCase) && string.Equals(nameB, sysWithA, StringComparison.OrdinalIgnoreCase)) ||
               (string.Equals(nameB, systemName, StringComparison.OrdinalIgnoreCase) && string.Equals(nameA, sysWithA, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Merges EDSM system bodies into the canonical ExplorationBodies list as a fallback when Spansh
    /// is disabled or missing a body, without ever duplicating or overwriting Local or Spansh rows.
    /// </summary>
    public void SyncEdsmSystemBodies(string systemName, string? systemAddress, IReadOnlyList<SpanshBodySummary> bodies)
    {
        if (string.IsNullOrWhiteSpace(systemName) || bodies.Count == 0)
            return;

        lock (_refreshLock)
        {
            if (!string.Equals(_state.CurrentSystem, systemName, StringComparison.OrdinalIgnoreCase))
                return;

            string sysAddr = !string.IsNullOrWhiteSpace(systemAddress) ? systemAddress : (_state.SystemAddress ?? systemName);
            bool addedAny = false;
            foreach (var eb in bodies)
            {
                if (string.IsNullOrWhiteSpace(eb.Name) || IsNonCelestialBody(eb.Name, eb.Type))
                    continue;

                bool alreadyExists =
                    (eb.BodyId.HasValue && _explorationBodies.ContainsKey($"{sysAddr}:{eb.BodyId.Value}")) ||
                    _explorationBodies.Values.Any(b =>
                        string.Equals(b.SystemName, systemName, StringComparison.OrdinalIgnoreCase) &&
                        AreEquivalentBodyNames(systemName, b.BodyName, eb.Name));
                if (alreadyExists)
                    continue;

                string? starCode = ExtractStarCodeFromExternalType(eb.Type);
                string key = eb.BodyId.HasValue ? $"{sysAddr}:{eb.BodyId.Value}" : $"{sysAddr}:edsm:{eb.Name}";
                _explorationBodies[key] = new ExplorationBodyItem
                {
                    Id = key,
                    BodyId = eb.BodyId,
                    DataSource = "EDSM",
                    SystemName = systemName,
                    BodyName = eb.Name,
                    StarType = starCode ?? "",
                    PlanetClass = starCode is null ? eb.Type : "",
                    DistanceFromArrivalLs = eb.DistanceToArrival,
                    Landable = eb.IsLandable,
                    SurfaceGravity = eb.SurfaceGravity,
                    SurfaceTemperatureKelvin = eb.SurfaceTemperature,
                    AtmosphereType = eb.AtmosphereType ?? "",
                    Rings = eb.HasRings ? new List<ExplorationRingItem> { new() { Name = "Ring" } } : new List<ExplorationRingItem>()
                };
                _explorationBodyOrder.Enqueue(key);
                while (_explorationBodyOrder.Count > 2000)
                    _explorationBodies.Remove(_explorationBodyOrder.Dequeue());
                addedAny = true;
            }

            if (addedAny)
            {
                RebuildExplorationBodiesSnapshot();
                _state.LastUpdated = DateTime.UtcNow;
                PublishState();
            }
        }
    }

    /// <summary>
    /// Merges Spansh system dump bodies into the canonical ExplorationBodies list without duplicating
    /// locally scanned bodies. Passing null (e.g. when Spansh is disabled) purges Spansh-only rows
    /// while preserving all local Journal scans intact.
    /// </summary>
    public void SyncSpanshSystemBodies(SpanshSystemSnapshot? snapshot)
    {
        lock (_refreshLock)
        {
            // Purge all Spansh-only rows first so disabling Spansh or changing systems leaves no stale entries
            var staleKeys = _explorationBodies
                .Where(static kv => kv.Value.IsSpanshOnly)
                .Select(static kv => kv.Key)
                .ToList();
            foreach (string key in staleKeys)
                _explorationBodies.Remove(key);

            if (snapshot is null)
            {
                foreach (var body in _explorationBodies.Values)
                {
                    if (string.Equals(body.DataSource, "Local+Spansh", StringComparison.OrdinalIgnoreCase))
                        body.DataSource = "Local";
                }
                RebuildExplorationBodiesSnapshot();
                _state.LastUpdated = DateTime.UtcNow;
                PublishState();
                return;
            }

            string sysAddr = snapshot.Id64.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string sysName = !string.IsNullOrWhiteSpace(snapshot.Name) ? snapshot.Name : _state.CurrentSystem;

            // Ignore snapshots for a different system if the commander already jumped away
            if (!string.IsNullOrWhiteSpace(_state.SystemAddress) &&
                !string.Equals(_state.SystemAddress, sysAddr, StringComparison.Ordinal) &&
                !string.Equals(_state.CurrentSystem, sysName, StringComparison.OrdinalIgnoreCase))
            {
                RebuildExplorationBodiesSnapshot();
                return;
            }

            foreach (var sb in snapshot.Bodies)
            {
                if (string.IsNullOrWhiteSpace(sb.Name) || IsNonCelestialBody(sb.Name, sb.Type))
                    continue;

                var bodyStations = new List<string>();
                if (sb.StationNames is { Count: > 0 })
                {
                    foreach (string rawSt in sb.StationNames)
                    {
                        if (IsDisplayableStationEntry(rawSt))
                            bodyStations.Add(rawSt);
                    }
                }
                if (bodyStations.Count == 0 && snapshot.Stations is { Count: > 0 })
                {
                    foreach (var st in snapshot.Stations)
                    {
                        if (!string.IsNullOrWhiteSpace(st.Name) &&
                            SpanshSystemDataService.IsSystemMapDisplayableStationType(st.Type, st.Name) &&
                            AreEquivalentBodyNames(sysName, st.BodyName, sb.Name))
                        {
                            string formatted = string.IsNullOrWhiteSpace(st.Type) ? st.Name : $"{st.Name} ({st.Type})";
                            if (!bodyStations.Contains(formatted, StringComparer.OrdinalIgnoreCase))
                                bodyStations.Add(formatted);
                        }
                    }
                }

                // Match existing body by BodyId or equivalent BodyName within this system
                ExplorationBodyItem? existing = null;
                if (sb.BodyId.HasValue && _explorationBodies.TryGetValue($"{sysAddr}:{sb.BodyId.Value}", out var byId))
                {
                    existing = byId;
                }
                else
                {
                    existing = _explorationBodies.Values.FirstOrDefault(b =>
                        string.Equals(b.SystemName, sysName, StringComparison.OrdinalIgnoreCase) &&
                        AreEquivalentBodyNames(sysName, b.BodyName, sb.Name));
                }

                string? starCode = ExtractStarCodeFromExternalType(sb.Type);
                if (existing is not null)
                {
                    // Local Journal always takes precedence; upgrade EDSM fallback to Spansh without falsely tagging Local
                    if (!existing.IsExternalOnly)
                        existing.DataSource = "Local+Spansh";
                    else if (string.Equals(existing.DataSource, "EDSM", StringComparison.OrdinalIgnoreCase))
                        existing.DataSource = "Spansh";

                    existing.BodyId ??= sb.BodyId;
                    if (string.IsNullOrWhiteSpace(existing.PlanetClass) && string.IsNullOrWhiteSpace(existing.StarType))
                    {
                        if (starCode is not null) existing.StarType = starCode;
                        else existing.PlanetClass = sb.Type;
                    }
                    existing.DistanceFromArrivalLs ??= sb.DistanceToArrival;
                    existing.Landable ??= sb.IsLandable;
                    existing.SurfaceGravity ??= sb.SurfaceGravity;
                    existing.SurfaceTemperatureKelvin ??= sb.SurfaceTemperature;
                    if (string.IsNullOrWhiteSpace(existing.AtmosphereType) && !string.IsNullOrWhiteSpace(sb.AtmosphereType))
                        existing.AtmosphereType = sb.AtmosphereType;
                    if (existing.Parents.Count == 0 && sb.Parents is { Count: > 0 })
                        ApplyParentsHierarchy(existing, sb.Parents);
                    if (sb.BeltNames is { Count: > 0 })
                    {
                        foreach (string beltName in sb.BeltNames)
                        {
                            if (!existing.Rings.Any(r => string.Equals(r.Name, beltName, StringComparison.OrdinalIgnoreCase)))
                                existing.Rings.Add(new ExplorationRingItem { Name = beltName, RingClass = "Belt" });
                        }
                    }
                    if (sb.HasRings && !existing.Rings.Any(static r => !r.Name.Contains("Belt", StringComparison.OrdinalIgnoreCase)))
                        existing.Rings.Add(new ExplorationRingItem { Name = "Ring" });
                    if (bodyStations.Count > 0)
                    {
                        foreach (string stName in bodyStations)
                        {
                            if (!existing.Stations.Contains(stName, StringComparer.OrdinalIgnoreCase))
                                existing.Stations.Add(stName);
                        }
                    }
                }
                else
                {
                    string key = sb.BodyId.HasValue ? $"{sysAddr}:{sb.BodyId.Value}" : $"{sysAddr}:spansh:{sb.Name}";
                    var ringsList = new List<ExplorationRingItem>();
                    if (sb.BeltNames is { Count: > 0 })
                    {
                        foreach (string beltName in sb.BeltNames)
                            ringsList.Add(new ExplorationRingItem { Name = beltName, RingClass = "Belt" });
                    }
                    if (sb.HasRings)
                        ringsList.Add(new ExplorationRingItem { Name = "Ring" });

                    var item = new ExplorationBodyItem
                    {
                        Id = key,
                        BodyId = sb.BodyId,
                        DataSource = "Spansh",
                        SystemName = sysName,
                        BodyName = sb.Name,
                        StarType = starCode ?? "",
                        PlanetClass = starCode is null ? sb.Type : "",
                        DistanceFromArrivalLs = sb.DistanceToArrival,
                        Landable = sb.IsLandable,
                        SurfaceGravity = sb.SurfaceGravity,
                        SurfaceTemperatureKelvin = sb.SurfaceTemperature,
                        AtmosphereType = sb.AtmosphereType ?? "",
                        Rings = ringsList,
                        Stations = bodyStations
                    };
                    if (sb.Parents is { Count: > 0 })
                        ApplyParentsHierarchy(item, sb.Parents);
                    AttachPendingLocalStations(item, sysAddr);
                    _explorationBodies[key] = item;
                    _explorationBodyOrder.Enqueue(key);
                    while (_explorationBodyOrder.Count > 2000)
                        _explorationBodies.Remove(_explorationBodyOrder.Dequeue());
                }
            }

            RebuildExplorationBodiesSnapshot();
            _state.LastUpdated = DateTime.UtcNow;
            PublishState();
        }
    }

    private void RestoreExplorationBodyHistory(string folder, string systemAddress)
    {
        if (!_explorationHistoryResolvedSystems.Add(systemAddress))
            return;

        FileInfo[] journals;
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;

            journals = new DirectoryInfo(folder).EnumerateFiles("Journal.*.log")
                .OrderBy(static info => info.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("FileWatcher.Exploration", $"Could not enumerate journals in '{folder}' for exploration restore: {ex.Message}");
            return;
        }

        foreach (var journalInfo in journals)
        {
            string journalFile = journalInfo.FullName;
            try
            {
                using var stream = new FileStream(journalFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                foreach (var (line, _) in ReadCompleteJournalLines(stream))
                {
                    if (string.IsNullOrWhiteSpace(line) ||
                        !line.Contains("SystemAddress", StringComparison.Ordinal) ||
                        !line.Contains(systemAddress, StringComparison.Ordinal))
                        continue;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var data = document.RootElement;
                        if (!data.TryGetProperty("event", out var eventProperty) ||
                            eventProperty.ValueKind != JsonValueKind.String ||
                            !string.Equals(GetJournalIdentifier(data, "SystemAddress"), systemAddress, StringComparison.Ordinal))
                            continue;

                        string eventType = eventProperty.GetString() ?? "";
                        if (eventType is "Scan" or "FSSBodySignals" or "SAASignalsFound" or
                            "SAAScanComplete" or "ScanOrganic" or "Location" or "Docked" or "ApproachSettlement" or "SupercruiseExit")
                            UpdateExplorationBodyState(eventType, data, isHistoryRestore: true);
                    }
                    catch (JsonException)
                    {
                        Debug.WriteLine($"No se pudo leer una línea del historial de exploración: {journalFile}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppDiagnosticsLogger.Warn("FileWatcher.Exploration", $"Could not read historical Journal for exploration restore ({Path.GetFileName(journalFile)}): {ex.Message}");
            }
        }
    }


    private static (string Key, object?[] Arguments) BuildExplorationJournalSummary(string eventType, JsonElement root)
    {
        string? ReadText(params string[] properties)
        {
            foreach (string property in properties)
            {
                if (root.TryGetProperty(property, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString();
            }

            return null;
        }

        long? ReadNumber(params string[] properties)
        {
            foreach (string property in properties)
            {
                if (root.TryGetProperty(property, out var value) &&
                    value.ValueKind == JsonValueKind.Number &&
                    value.TryGetInt64(out long number))
                    return number;
            }

            return null;
        }

        return eventType switch
        {
            "FSSDiscoveryScan" => ReadNumber("Bodies") is long bodies
                ? ("fssCount", new object?[] { bodies }) : ("fssComplete", Array.Empty<object?>()),
            "FSSAllBodiesFound" => ("allBodies", new object?[] { ReadText("SystemName") }),
            "SAAScanComplete" => ReadNumber("ProbesUsed") is long probes
                ? ("mappingProbes", new object?[] { ReadText("BodyName"), probes })
                : ("mapping", new object?[] { ReadText("BodyName") }),
            "ScanOrganic" => ReadText("Body") is string body
                ? ("organicBody", new object?[] { ReadText("Species_Localised", "Species"), body })
                : ("organic", new object?[] { ReadText("Species_Localised", "Species") }),
            "CodexEntry" => ReadText("Region") is string region
                ? ("codexRegion", new object?[] { ReadText("Name_Localised", "Name"), region })
                : ("codex", new object?[] { ReadText("Name_Localised", "Name") }),
            "SellExplorationData" or "MultiSellExplorationData" => ReadNumber("TotalEarnings") is long earnings
                ? ("soldEarnings", new object?[] { earnings }) : ("sold", Array.Empty<object?>()),
            _ => ("explorationEvent", new object?[] { eventType })
        };
    }

}
