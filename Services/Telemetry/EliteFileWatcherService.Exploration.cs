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
            body = new ExplorationBodyItem { Id = id };
            _explorationBodies[id] = body;
            _explorationBodyOrder.Enqueue(id);
            while (_explorationBodyOrder.Count > 2000)
                _explorationBodies.Remove(_explorationBodyOrder.Dequeue());
        }

        body.SystemName = systemName;
        body.BodyName = bodyName;
        if (TryGetJournalInt(data, eventType == "ScanOrganic" ? "Body" : "BodyID", out int parsedBodyId))
            body.BodyId = parsedBodyId;

        if (eventType == "Scan")
        {
            if (!isHistoryRestore)
                _sessionBodiesScanned++;
            if (data.TryGetProperty("Parents", out var parentsProp) && parentsProp.ValueKind == JsonValueKind.Array)
                body.HierarchyDepth = parentsProp.GetArrayLength();
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

                    string? type = GetJournalRawText(signal, "Type_Localised", "Type");
                    if (type?.Contains("Biological", StringComparison.OrdinalIgnoreCase) == true)
                        body.BiologicalSignalCount += count;
                    else if (type?.Contains("Geological", StringComparison.OrdinalIgnoreCase) == true)
                        body.GeologicalSignalCount += count;
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
                    RequiredDistanceMeters = ExobiologyPredictor.GetRequiredDistanceMeters(!string.IsNullOrWhiteSpace(genusName) ? genusName : speciesDisplay),
                    CurrentDistanceMeters = 0
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

                    string exoKey = $"{id}:{species ?? genusName ?? "organic"}";
                    if (_unsoldExobiologyKeys.Add(exoKey))
                    {
                        bool firstFootfall = body.WasDiscovered == false || body.WasMapped == false;
                        _state.UnsoldExobiologyCredits += ExobiologyPredictor.EstimateSpeciesCredits(
                            !string.IsNullOrWhiteSpace(species) ? species : genusName, firstFootfall);
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

    private void RestoreExplorationBodyHistory(string folder, string systemAddress)
    {
        if (!_explorationHistoryResolvedSystems.Add(systemAddress))
            return;

        foreach (var journalInfo in new DirectoryInfo(folder).EnumerateFiles("Journal.*.log")
                     .OrderBy(static info => info.Name, StringComparer.OrdinalIgnoreCase))
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
                            "SAAScanComplete" or "ScanOrganic")
                            UpdateExplorationBodyState(eventType, data, isHistoryRestore: true);
                    }
                    catch (JsonException)
                    {
                        Debug.WriteLine($"No se pudo leer una línea del historial de exploración: {journalFile}");
                    }
                }
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"No se pudo leer el Journal para recuperar exploración ({journalFile}): {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"Acceso denegado al Journal de exploración ({journalFile}): {ex.Message}");
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
