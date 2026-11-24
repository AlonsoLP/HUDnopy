// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/AlertService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Real-time alert evaluation engine for ship telemetry, missions, and Journal milestones.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using HUDnopy.Models;
using HUDnopy.Localization;

namespace HUDnopy.Services;

/// <summary>
/// Real-time alert evaluation engine for ship telemetry, missions, and Journal milestones.
/// </summary>
public sealed class AlertService
{
    private const int MaximumHistory = 20;
    private readonly ConfigService _configService;
    private readonly Lock _alertsLock = new();
    private readonly List<AlertNotification> _alerts = new();
    private readonly Dictionary<string, AlertNotification> _activeAlerts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missionAlertConditions = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private StateSnapshot? _previousState;
    private bool _initializedMissionConditions;
    private bool _wasExoCanSampleNext;
    private bool _wasLimpetsReminderActive;
    private bool _wasHighGravityActive;
    private bool _wasRouteFuelWarningActive;
    private bool _wasScoWarningActive;
    private bool _wasFsdWearActive;
    private bool _hasIllicitCargoOrPassengers;
    private DateTimeOffset _lastUnderAttackAlertUtc = DateTimeOffset.MinValue;
    private readonly HashSet<string> _warnedDamagedModules = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AlertNotification> Alerts
    {
        get
        {
            lock (_alertsLock)
            {
                string language = _configService.Current.InterfaceLanguage;
                return _alerts.Select(alert => CoreAlertTranslations.Translate(alert, language)).ToArray();
            }
        }
    }

    public IReadOnlyList<AlertNotification> ActiveAlerts
    {
        get
        {
            lock (_alertsLock)
            {
                string language = _configService.Current.InterfaceLanguage;
                return _activeAlerts.Values
                    .Where(alert => IsVisualEnabled(alert.AlertId))
                    .OrderByDescending(alert => alert.Severity)
                    .ThenByDescending(alert => alert.Timestamp)
                    .Select(alert => CoreAlertTranslations.Translate(alert, language))
                    .ToArray();
            }
        }
    }

    public event Action<AlertNotification>? AlertRaised;
    public event Action<AlertNotification, AlertChannelPreferences>? AlertTriggered;
    public event Action? AlertsChanged;

    public AlertService(ConfigService configService, EliteFileWatcherService fileWatcher, GameEventHub gameEvents)
    {
        _configService = configService;
        NormalizePreferences();
        _previousState = Capture(fileWatcher.State);
        fileWatcher.StateUpdated += OnStateUpdated;
        gameEvents.JournalEventReceived += OnJournalEvent;
        _configService.ConfigChanged += _ =>
        {
            NormalizePreferences();
            AlertsChanged?.Invoke();
        };
    }

    private void NormalizePreferences()
    {
        _configService.Current.FuelLowThresholdPercent =
            Math.Clamp(_configService.Current.FuelLowThresholdPercent, 2, 100);
        _configService.Current.FuelCriticalThresholdPercent =
            Math.Clamp(_configService.Current.FuelCriticalThresholdPercent, 1, _configService.Current.FuelLowThresholdPercent - 1);
        _configService.Current.HullCriticalThresholdPercent =
            Math.Clamp(_configService.Current.HullCriticalThresholdPercent, 1, 99);
        _configService.Current.MissionExpiryWarningHours =
            Math.Clamp(_configService.Current.MissionExpiryWarningHours, 2, 720);
        _configService.Current.MissionExpiryCriticalHours =
            Math.Clamp(_configService.Current.MissionExpiryCriticalHours, 1, _configService.Current.MissionExpiryWarningHours - 1);
        var configured = _configService.Current.AlertPreferences;
        if (configured == null)
            _configService.Current.AlertPreferences = configured = AlertDefinitions.CreateDefaultPreferences();
        configured.Remove("material-low");

        foreach (var definition in AlertDefinitions.All)
        {
            if (!configured.ContainsKey(definition.Id) || configured[definition.Id] == null)
                configured[definition.Id] = new AlertChannelPreferences();
        }
    }

    private bool IsVisualEnabled(string alertId) =>
        _configService.Current.AlertPreferences.TryGetValue(alertId, out var preference) &&
        preference?.VisualEnabled == true;

    public static bool IsConditionAlert(string? alertId) =>
        alertId is "fuel-low" or "fuel-critical" or "overheating" or "hull-critical" or
            "shields-down" or "interdiction" or "danger" or "cockpit-breach" or "mission-expiry";

    public bool IsAlertStillRelevant(string? alertId, string? notificationId)
    {
        if (string.IsNullOrEmpty(alertId) || !IsConditionAlert(alertId))
            return true;

        lock (_alertsLock)
        {
            return _activeAlerts.Values.Any(active =>
                (!string.IsNullOrEmpty(notificationId) && string.Equals(active.Id, notificationId, StringComparison.Ordinal)) ||
                string.Equals(active.AlertId, alertId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Dismisses all currently active condition alerts (e.g. via COVAS voice command).
    /// </summary>
    public int DismissAllActiveAlerts()
    {
        int count;
        lock (_alertsLock)
        {
            count = _activeAlerts.Count;
            if (count == 0)
                return 0;
            _activeAlerts.Clear();
        }
        AlertsChanged?.Invoke();
        return count;
    }

    private void OnStateUpdated(ShipState state)
    {
        var current = Capture(state);
        var previous = _previousState;
        _previousState = current;

        if (!current.IsGameDetected)
        {
            ClearAllActiveAlerts();
            _missionAlertConditions.Clear();
            _initializedMissionConditions = false;
            return;
        }

        EvaluateMissionExpiryConditions(state);

        if (!current.HasStatusTelemetry)
        {
            ClearAllActiveAlerts();
            return;
        }

        bool hasPreviousGameStatus = previous is
        {
            IsGameDetected: true,
            HasStatusTelemetry: true
        };

        var config = _configService.Current;
        int lowThreshold = Math.Clamp(config.FuelLowThresholdPercent, 2, 100);
        int criticalThreshold = Math.Clamp(config.FuelCriticalThresholdPercent, 1, lowThreshold - 1);

        if (state.HasFuelTelemetry && current.HasFuelCapacity && current.FuelMax > 0)
        {
            double fuelPercent = GetFuelPercent(current);
            bool critical = fuelPercent <= criticalThreshold;
            bool low = !critical && fuelPercent <= lowThreshold;
            SetCondition(
                "fuel-low",
                low,
                "Combustible bajo",
                $"Combustible principal: {current.FuelMain:0.0}/{current.FuelMax:0.0} T ({fuelPercent:0.0}%). Aviso: {lowThreshold}%.",
                AlertSeverity.Warning,
                new AlertLocalization("fuelLow", current.FuelMain, current.FuelMax, fuelPercent, lowThreshold));
            SetCondition(
                "fuel-critical",
                critical,
                "Combustible crítico",
                $"Combustible principal: {current.FuelMain:0.0}/{current.FuelMax:0.0} T ({fuelPercent:0.0}%). Crítico: {criticalThreshold}%.",
                AlertSeverity.Critical,
                new AlertLocalization("fuelCritical", current.FuelMain, current.FuelMax, fuelPercent, criticalThreshold));
        }
        else
        {
            bool nativeLowFuel = current.LowFuel;
            SetCondition(
                "fuel-low",
                nativeLowFuel,
                "Combustible bajo",
                "El juego indica combustible bajo; aún no se ha leído la capacidad del depósito para calcular el porcentaje.",
                AlertSeverity.Warning,
                new AlertLocalization("fuelUnknown"));
            SetCondition("fuel-critical", false, "", "", AlertSeverity.Critical);
        }

        SetTransitionCondition(
            "overheating",
            hasPreviousGameStatus,
            previous?.OverHeating == true,
            current.OverHeating,
            "Sobrecalentamiento",
            "La temperatura de la nave es demasiado alta.",
            AlertSeverity.Critical,
            new AlertLocalization("overheating"));
        int hullCriticalThreshold = Math.Clamp(config.HullCriticalThresholdPercent, 1, 99);
        SetCondition(
            "hull-critical",
            current.HasHullTelemetry && current.HullHealth < hullCriticalThreshold,
            "Casco crítico",
            $"Integridad del casco: {current.HullHealth:0.0}% (crítico por debajo del {hullCriticalThreshold}%).",
            AlertSeverity.Critical,
            new AlertLocalization("hullCritical", current.HullHealth, hullCriticalThreshold));
        SetTransitionCondition(
            "shields-down",
            hasPreviousGameStatus,
            previous?.ShieldsUp == true,
            !current.ShieldsUp,
            "Escudos caídos",
            "Los escudos de la nave están desactivados.",
            AlertSeverity.Warning,
            new AlertLocalization("shieldsDown"));
        SetTransitionCondition(
            "interdiction",
            hasPreviousGameStatus,
            previous?.BeingInterdicted == true,
            current.BeingInterdicted,
            "Interdicción",
            "La nave está siendo interceptada.",
            AlertSeverity.Warning,
            new AlertLocalization("interdiction"));
        SetTransitionCondition(
            "danger",
            hasPreviousGameStatus,
            previous?.IsInDanger == true,
            current.IsInDanger,
            "Situación de peligro",
            "El estado de la nave indica una situación de peligro.",
            AlertSeverity.Critical,
            new AlertLocalization("danger"));
        SetCondition(
            "cockpit-breach",
            current.CockpitBreached,
            "Cabina comprometida",
            "La cabina ha perdido integridad. Comprueba el soporte vital.",
            AlertSeverity.Critical,
            new AlertLocalization("cockpitBreach"));

        EvaluateTacticalCopilotAlerts(state);
    }

    private void EvaluateTacticalCopilotAlerts(ShipState state)
    {
        _hasIllicitCargoOrPassengers =
            state.CargoInventory.Any(static item => item.Stolen > 0) ||
            state.PassengerManifest.Any(static p => p.Wanted);

        bool exoReady = state.ExobiologyTracker?.CanSampleNext == true;
        if (exoReady && !_wasExoCanSampleNext && state.ExobiologyTracker is { } exo)
        {
            string species = string.IsNullOrWhiteSpace(exo.Species) ? "especie orgánica" : exo.Species;
            Notify(new AlertNotification(
                Guid.NewGuid().ToString("N"),
                "exploration-activity",
                "Distancia exobiológica alcanzada",
                $"Ya puedes tomar la muestra {exo.SamplesCollected + 1} de 3 de {species}.",
                AlertSeverity.Information,
                DateTimeOffset.Now)
            {
                Localization = new ActivityAlertLocalization(
                    "exoDistanceTitle",
                    Array.Empty<object>(),
                    "exoDistance",
                    species,
                    exo.SamplesCollected + 1)
            });
        }
        _wasExoCanSampleNext = exoReady;

        bool needsLimpets = state.HasLimpetController && state.LimpetCount == 0 && (state.Docked || state.Landed);
        if (needsLimpets && !_wasLimpetsReminderActive)
        {
            Notify(new AlertNotification(
                Guid.NewGuid().ToString("N"),
                "copilot-safety",
                "Recordatorio de drones",
                "Tienes controlador de drones instalado pero 0 drones en bodega.",
                AlertSeverity.Warning,
                DateTimeOffset.Now)
            {
                Localization = new ActivityAlertLocalization("limpetsReminderTitle", Array.Empty<object>(), "limpetsReminder")
            });
        }
        _wasLimpetsReminderActive = needsLimpets;

        bool highGravity = state.ApproachBodyGravityG is >= 1.5 && (state.GlideMode || !string.IsNullOrWhiteSpace(state.ApproachBodyName));
        if (highGravity && !_wasHighGravityActive && state.ApproachBodyGravityG is double gravG)
        {
            Notify(new AlertNotification(
                Guid.NewGuid().ToString("N"),
                "copilot-safety",
                "Aproximación de alta gravedad",
                $"Cuerpo con gravedad elevada ({gravG:0.0} G). Controla la velocidad de descenso.",
                AlertSeverity.Warning,
                DateTimeOffset.Now)
            {
                Localization = new ActivityAlertLocalization("highGravityTitle", Array.Empty<object>(), "highGravity", Math.Round(gravG, 1))
            });
        }
        _wasHighGravityActive = highGravity;

        bool routeFuelWarning = state.HasFuelCapacity && state.FuelMax > 0 &&
            (state.FuelMain / state.FuelMax) <= 0.35 &&
            (state.RouteHasNoFuelStar || state.JumpsToNextFuelStar > 1);
        if (routeFuelWarning && !_wasRouteFuelWarningActive)
        {
            Notify(new AlertNotification(
                Guid.NewGuid().ToString("N"),
                "copilot-safety",
                "Autonomía de ruta",
                "Combustible bajo y el siguiente salto en ruta no es una estrella repostable.",
                AlertSeverity.Warning,
                DateTimeOffset.Now)
            {
                Localization = new ActivityAlertLocalization("routeFuelTitle", Array.Empty<object>(), "routeFuelWarning")
            });
        }
        _wasRouteFuelWarningActive = routeFuelWarning;

        bool scoWarning = state.SupercruiseOverdriveActive &&
            (state.OverHeating || (state.HasFuelCapacity && state.FuelMax > 0 && (state.FuelMain / state.FuelMax) <= 0.25));
        if (scoWarning && !_wasScoWarningActive)
        {
            Notify(new AlertNotification(
                Guid.NewGuid().ToString("N"),
                "copilot-safety",
                "Sobrecarga SCO crítica",
                "Desactiva el SCO: consumo elevado de combustible o sobrecalentamiento.",
                AlertSeverity.Warning,
                DateTimeOffset.Now)
            {
                Localization = new ActivityAlertLocalization("scoWarningTitle", Array.Empty<object>(), "scoWarning")
            });
        }
        _wasScoWarningActive = scoWarning;

        bool fsdWear = state.FsdHealthPercent is < 80;
        if (fsdWear && !_wasFsdWearActive && state.FsdHealthPercent is double fsd)
        {
            Notify(new AlertNotification(
                Guid.NewGuid().ToString("N"),
                "copilot-safety",
                "Desgaste del motor FSD",
                $"Integridad del FSD al {fsd:0}% (< 80%). Repara con UMA para evitar fallos de salto.",
                AlertSeverity.Warning,
                DateTimeOffset.Now)
            {
                Localization = new ActivityAlertLocalization("fsdWearTitle", Array.Empty<object>(), "fsdWear", Math.Round(fsd))
            });
        }
        _wasFsdWearActive = fsdWear;

        foreach (var module in state.ShipModules)
        {
            if (!module.IsVital || module.Slot.Equals("FrameShiftDrive", StringComparison.OrdinalIgnoreCase))
                continue;
            bool damaged = module.HealthPercent < 80;
            if (damaged && _warnedDamagedModules.Add(module.Slot))
            {
                bool critical = module.HealthPercent < 50;
                Notify(new AlertNotification(
                    Guid.NewGuid().ToString("N"),
                    "copilot-safety",
                    "Módulo crítico dañado",
                    $"{module.DisplayName} al {module.HealthPercent:0}%.",
                    critical ? AlertSeverity.Critical : AlertSeverity.Warning,
                    DateTimeOffset.Now)
                {
                    Localization = new ActivityAlertLocalization(
                        "moduleDamageTitle",
                        Array.Empty<object>(),
                        "moduleDamage",
                        module.DisplayName,
                        Math.Round(module.HealthPercent))
                });
            }
            else if (!damaged)
            {
                _warnedDamagedModules.Remove(module.Slot);
            }
        }
    }

    private void OnJournalEvent(GameJournalEvent gameEvent)
    {
        if (gameEvent.Timestamp < _startedAt)
            return;

        if (TryCreateExplorationAlert(gameEvent, out var explorationAlert))
        {
            Notify(explorationAlert);
        }
        else if (TryCreateJournalActivityAlert(gameEvent, out var activityAlert))
        {
            Notify(activityAlert);
        }
        else if (gameEvent.EventType == "ShieldState" &&
            gameEvent.Data.TryGetProperty("ShieldsUp", out var shieldsUp) &&
            shieldsUp.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
        {
            bool isUp = shieldsUp.GetBoolean();
            SetCondition(
                "shields-down",
                !isUp,
                "Escudos caídos",
                "Los escudos de la nave están desactivados.",
                AlertSeverity.Warning,
                new AlertLocalization("shieldsDown"));
        }
        else if (gameEvent.EventType == "CockpitBreached")
        {
            SetCondition(
                "cockpit-breach",
                true,
                "Cabina comprometida",
                "La cabina ha perdido integridad. Comprueba el soporte vital.",
                AlertSeverity.Critical,
                new AlertLocalization("cockpitBreach"));
        }
        else if (gameEvent.EventType == "ShipTargeted")
        {
            EvaluateShipTargetedAlert(gameEvent);
        }
        else if (gameEvent.EventType == "Scanned")
        {
            EvaluateScannedAlert(gameEvent);
        }
        else if (gameEvent.EventType == "UnderAttack")
        {
            if ((DateTimeOffset.UtcNow - _lastUnderAttackAlertUtc).TotalSeconds >= 15)
            {
                _lastUnderAttackAlertUtc = DateTimeOffset.UtcNow;
                Notify(new AlertNotification(
                    Guid.NewGuid().ToString("N"),
                    "copilot-safety",
                    "Bajo ataque",
                    "Tu nave está recibiendo fuego hostil.",
                    AlertSeverity.Warning,
                    gameEvent.Timestamp)
                {
                    Localization = new ActivityAlertLocalization("underAttackTitle", Array.Empty<object>(), "underAttack")
                });
            }
        }
        else if (gameEvent.EventType == "ProspectedAsteroid")
        {
            EvaluateProspectedAsteroidAlert(gameEvent);
        }
        else if (gameEvent.EventType == "MaterialCollected")
        {
            var wishlist = _configService.Current.MaterialWishlist;
            if (wishlist is { Count: > 0 })
            {
                string? rawName = GetJournalText(gameEvent.Data, "Name");
                string? displayName = GetJournalText(gameEvent.Data, "Name_Localised", "Name");
                string? category = GetJournalText(gameEvent.Data, "Category");
                string id = category is not null && rawName is not null ? $"{category.ToLowerInvariant()}:{rawName.ToLowerInvariant()}" : "";
                if ((rawName is not null && wishlist.ContainsKey(rawName)) ||
                    (id.Length > 0 && wishlist.ContainsKey(id)))
                {
                    Notify(new AlertNotification(
                        Guid.NewGuid().ToString("N"),
                        "cargo-activity",
                        "Material buscado recogido",
                        $"★ {displayName ?? rawName} añadido a tu lista de seguimiento.",
                        AlertSeverity.Information,
                        gameEvent.Timestamp)
                    {
                        Localization = new ActivityAlertLocalization(
                            "wishlistMaterialTitle",
                            Array.Empty<object>(),
                            "wishlistMaterial",
                            displayName ?? rawName ?? "")
                    });
                }
            }
        }
        else if (gameEvent.EventType is "RepairAll" or "Resurrect" ||
                 gameEvent.EventType == "Repair" && JournalEventHelpers.IsLifeSupportRepair(gameEvent.Data))
        {
            SetCondition("cockpit-breach", false, "", "", AlertSeverity.Critical);
        }
    }

    private void EvaluateShipTargetedAlert(GameJournalEvent gameEvent)
    {
        var data = gameEvent.Data;
        if (!data.TryGetProperty("TargetLocked", out var locked) || locked.ValueKind != System.Text.Json.JsonValueKind.True)
            return;
        if (!TryGetJournalNumber(data, "ScanStage", out double stage) || stage < 3)
            return;
        string? legal = GetJournalText(data, "LegalStatus");
        if (!string.Equals(legal, "Wanted", StringComparison.OrdinalIgnoreCase))
            return;
        if (!TryGetJournalNumber(data, "Bounty", out double bounty) || bounty < 200_000)
            return;

        string ship = GetJournalText(data, "Ship_Localised", "Ship") ?? "Objetivo";
        Notify(new AlertNotification(
            Guid.NewGuid().ToString("N"),
            "bounty-reward",
            "Objetivo buscado de alta recompensa",
            $"{ship} · Recompensa: {bounty:N0} CR.",
            AlertSeverity.Information,
            gameEvent.Timestamp)
        {
            Localization = new ActivityAlertLocalization("wantedTargetTitle", Array.Empty<object>(), "wantedTarget", ship, bounty)
        });
    }

    private void EvaluateScannedAlert(GameJournalEvent gameEvent)
    {
        string scanType = GetJournalText(gameEvent.Data, "ScanType") ?? "Scan";
        if (_hasIllicitCargoOrPassengers)
        {
            Notify(new AlertNotification(
                Guid.NewGuid().ToString("N"),
                "copilot-safety",
                "¡Escaneo con carga o pasajeros ilegales!",
                $"Escaneo detectado ({scanType}) con carga robada o pasajeros buscados a bordo.",
                AlertSeverity.Critical,
                gameEvent.Timestamp)
            {
                Localization = new ActivityAlertLocalization("illegalScanTitle", Array.Empty<object>(), "illegalScan", scanType)
            });
        }
        else
        {
            Notify(new AlertNotification(
                Guid.NewGuid().ToString("N"),
                "copilot-safety",
                "Nave escaneada",
                $"Tu nave ha sido escaneada ({scanType}).",
                AlertSeverity.Warning,
                gameEvent.Timestamp)
            {
                Localization = new ActivityAlertLocalization("shipScannedTitle", Array.Empty<object>(), "shipScanned", scanType)
            });
        }
    }

    private void EvaluateProspectedAsteroidAlert(GameJournalEvent gameEvent)
    {
        var data = gameEvent.Data;
        string? motherlode = GetJournalText(data, "MotherlodeMaterial_Localised", "MotherlodeMaterial");
        if (!string.IsNullOrWhiteSpace(motherlode))
        {
            Notify(new AlertNotification(
                Guid.NewGuid().ToString("N"),
                "cargo-activity",
                "Núcleo de asteroide detectado",
                $"Núcleo de {motherlode}.",
                AlertSeverity.Information,
                gameEvent.Timestamp)
            {
                Localization = new ActivityAlertLocalization("miningCoreTitle", Array.Empty<object>(), "miningCore", motherlode)
            });
            return;
        }

        if (!data.TryGetProperty("Materials", out var mats) || mats.ValueKind != System.Text.Json.JsonValueKind.Array)
            return;

        string[] targets = (_configService.Current.MiningTargetMinerals ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int minPct = Math.Clamp(_configService.Current.MiningMinPercent, 1, 100);

        string? bestName = null;
        double bestProp = 0;
        foreach (var mat in mats.EnumerateArray())
        {
            string rawName = GetJournalText(mat, "Name") ?? "";
            string dispName = GetJournalText(mat, "Name_Localised", "Name") ?? rawName;
            if (!TryGetJournalNumber(mat, "Proportion", out double prop) || prop < minPct)
                continue;
            bool match = targets.Length == 0 || targets.Any(t =>
                rawName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                dispName.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (match && prop > bestProp)
            {
                bestProp = prop;
                bestName = dispName;
            }
        }

        if (bestName is not null)
        {
            Notify(new AlertNotification(
                Guid.NewGuid().ToString("N"),
                "cargo-activity",
                "Asteroide rentable detectado",
                $"{bestName} al {bestProp:0}%.",
                AlertSeverity.Information,
                gameEvent.Timestamp)
            {
                Localization = new ActivityAlertLocalization("miningTargetTitle", Array.Empty<object>(), "miningTarget", bestName, Math.Round(bestProp))
            });
        }
    }

    private static bool TryCreateExplorationAlert(
        GameJournalEvent gameEvent,
        out AlertNotification alert)
    {
        string title;
        string message;
        ActivityAlertLocalization localization;
        var data = gameEvent.Data;

        switch (gameEvent.EventType)
        {
            case "FSSDiscoveryScan":
                title = "Barrido FSS completado";
                int bodies = TryGetJournalNumber(data, "Bodies", out var bodyCount)
                    ? (int)bodyCount
                    : 0;
                message = bodies > 0
                    ? $"El escáner de espectro completo ha identificado {bodies} cuerpos."
                    : "Se ha completado el barrido del escáner de espectro completo.";
                localization = bodies > 0
                    ? new("fssTitle", Array.Empty<object>(), "fssBodies", bodies)
                    : new("fssTitle", Array.Empty<object>(), "fssComplete");
                break;

            case "FSSAllBodiesFound":
                title = "Todos los cuerpos descubiertos";
                string? journalSystem = GetJournalText(data, "SystemName");
                string system = journalSystem ?? "el sistema actual";
                message = $"Se han descubierto todos los cuerpos de {system}.";
                localization = new("allBodiesTitle", Array.Empty<object>(), "fssAll",
                    journalSystem is null ? new LocalizedAlertArgument("currentSystem") : journalSystem);
                break;

            case "SAAScanComplete":
                title = "Cartografía planetaria completada";
                string? journalBodyName = GetJournalText(data, "BodyName");
                string bodyName = journalBodyName ?? "Cuerpo planetario";
                double? probesValue = GetJournalNumeric(data, "ProbesUsed");
                string? probes = probesValue?.ToString("N0");
                message = probes == null
                    ? $"{bodyName} ha sido cartografiado."
                    : $"{bodyName} ha sido cartografiado con {probes} sondas.";
                bool efficient = TryGetJournalNumber(data, "EfficiencyTarget", out var efficiencyTarget) &&
                    probesValue.HasValue && probesValue.Value <= efficiencyTarget;
                if (efficient)
                    message += " Objetivo de eficiencia alcanzado.";
                object bodyArgument = journalBodyName is null ? new LocalizedAlertArgument("planetaryBody") : journalBodyName;
                localization = probesValue.HasValue
                    ? new("mappingTitle", Array.Empty<object>(), efficient ? "mappingEfficient" : "mappingProbes", bodyArgument, probesValue.Value)
                    : new("mappingTitle", Array.Empty<object>(), "mapping", bodyArgument);
                break;

            case "ScanOrganic":
                if (!string.Equals(
                    GetJournalText(data, "ScanType"),
                    "Analyse",
                    StringComparison.OrdinalIgnoreCase))
                {
                    alert = null!;
                    return false;
                }

                title = "Exobiología analizada";
                string? journalSpecies = GetJournalText(data, "Species_Localised", "Species");
                string species = journalSpecies ?? "Especie orgánica";
                string genus = GetJournalText(data, "Genus") ?? "";
                string body = GetJournalText(data, "Body") ?? "";
                message = string.Join(" · ", new[] { species, genus, body }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                var organicParts = new List<object>
                {
                    journalSpecies is null ? new LocalizedAlertArgument("organicSpecies") : journalSpecies
                };
                if (!string.IsNullOrWhiteSpace(genus)) organicParts.Add(genus);
                if (!string.IsNullOrWhiteSpace(body)) organicParts.Add(body);
                localization = new("organicTitle", Array.Empty<object>(), JoinedKey(organicParts.Count), organicParts.ToArray());
                break;

            case "CodexEntry":
                title = "Descubrimiento registrado en el Codex";
                string? journalCodexName = GetJournalText(data, "Name_Localised", "Name");
                string codexName = journalCodexName ?? "Nuevo descubrimiento";
                string? category = GetJournalText(data, "Category_Localised", "Category");
                string? region = GetJournalText(data, "Region");
                message = string.Join(" · ", new[] { codexName, category, region }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                var codexParts = new List<object>
                {
                    journalCodexName is null ? new LocalizedAlertArgument("newDiscovery") : journalCodexName
                };
                if (!string.IsNullOrWhiteSpace(category)) codexParts.Add(category);
                if (!string.IsNullOrWhiteSpace(region)) codexParts.Add(region);
                localization = new("codexTitle", Array.Empty<object>(), JoinedKey(codexParts.Count), codexParts.ToArray());
                break;

            case "SellExplorationData":
            case "MultiSellExplorationData":
                title = "Datos de exploración vendidos";
                int systems = data.TryGetProperty("Systems", out var systemsElement) &&
                    systemsElement.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? systemsElement.GetArrayLength()
                    : 0;
                double? earningsValue = GetJournalNumeric(data, "TotalEarnings");
                string? earnings = earningsValue?.ToString("N0");
                message = systems > 0
                    ? $"{systems} sistemas entregados"
                    : "Se ha registrado la entrega de datos exploratorios";
                if (earnings != null)
                    message += $"; ingresos: {earnings} CR";
                message += ".";
                localization = systems > 0
                    ? earningsValue.HasValue
                        ? new("soldExplorationTitle", Array.Empty<object>(), "soldSystemsEarnings", systems, earningsValue.Value)
                        : new("soldExplorationTitle", Array.Empty<object>(), "soldSystems", systems)
                    : earningsValue.HasValue
                        ? new("soldExplorationTitle", Array.Empty<object>(), "soldEarnings", earningsValue.Value)
                        : new("soldExplorationTitle", Array.Empty<object>(), "soldNone");
                break;

            default:
                alert = null!;
                return false;
        }

        alert = new AlertNotification(
            Guid.NewGuid().ToString("N"),
            "exploration-activity",
            title,
            message,
            AlertSeverity.Information,
            gameEvent.Timestamp) { Localization = localization };
        return true;
    }

    private static string JoinedKey(int count) => count switch
    {
        1 => "joinedOne",
        2 => "joinedTwo",
        3 => "joinedThree",
        _ => throw new ArgumentOutOfRangeException(nameof(count))
    };

    private static bool TryCreateJournalActivityAlert(
        GameJournalEvent gameEvent,
        out AlertNotification alert)
    {
        string eventType = gameEvent.EventType;
        string alertId;
        string title;
        string message;
        ActivityAlertLocalization? localization = null;
        AlertSeverity severity = AlertSeverity.Information;
        var data = gameEvent.Data;

        switch (eventType)
        {
            case "StartJump":
                if (!string.Equals(GetJournalText(data, "JumpType"), "Hyperspace", StringComparison.OrdinalIgnoreCase))
                {
                    alert = null!;
                    return false;
                }
                string? jumpStarClass = GetJournalText(data, "StarClass");
                string? jumpTarget = GetJournalText(data, "StarSystem");
                if (EliteFileWatcherService.IsHazardousStarClass(jumpStarClass))
                {
                    alertId = "copilot-safety";
                    title = "Estrella peligrosa en destino";
                    severity = AlertSeverity.Critical;
                    message = $"Destino {jumpTarget ?? "siguiente sistema"} (clase {jumpStarClass}). Reduce gases a cero al salir del salto.";
                    localization = new("jumpHazardTitle", Array.Empty<object>(), "jumpHazard",
                        jumpTarget is null ? new LocalizedAlertArgument("newSystem") : jumpTarget,
                        jumpStarClass ?? "");
                    break;
                }
                if (EliteFileWatcherService.IsScoopableStarClass(jumpStarClass))
                {
                    alertId = "copilot-safety";
                    title = "Estrella repostable en destino";
                    severity = AlertSeverity.Information;
                    message = $"Saltando a {jumpTarget ?? "siguiente sistema"} (clase {jumpStarClass}, repostable).";
                    localization = new("jumpScoopableTitle", Array.Empty<object>(), "jumpScoopable",
                        jumpTarget is null ? new LocalizedAlertArgument("newSystem") : jumpTarget,
                        jumpStarClass ?? "");
                    break;
                }
                alert = null!;
                return false;

            case "FSDJump":
                alertId = "jump-arrival";
                title = "Llegada a sistema";
                string? systemName = GetJournalText(data, "StarSystem");
                message = $"Has llegado a {systemName ?? "un nuevo sistema"}.";
                localization = new("arrivalTitle", Array.Empty<object>(), "arrival",
                    systemName is null ? new LocalizedAlertArgument("newSystem") : systemName);
                break;

            case "Docked":
                alertId = "docking";
                title = "Atraque";
                string? dockedStation = GetJournalText(data, "StationName");
                message = $"Acoplado en {dockedStation ?? "la estación"}.";
                localization = new("dockedTitle", Array.Empty<object>(), "docked",
                    dockedStation is null ? new LocalizedAlertArgument("station") : dockedStation);
                break;

            case "Undocked":
                alertId = "docking";
                title = "Desatraque";
                string? undockedStation = GetJournalText(data, "StationName");
                message = $"Desacoplado de {undockedStation ?? "la estación"}.";
                localization = new("undockedTitle", Array.Empty<object>(), "undocked",
                    undockedStation is null ? new LocalizedAlertArgument("station") : undockedStation);
                break;

            case "MissionAccepted":
            case "MissionCompleted":
            case "MissionAbandoned":
            case "MissionFailed":
            case "MissionExpired":
                alertId = "mission-activity";
                title = eventType switch
                {
                    "MissionAccepted" => "Misión aceptada",
                    "MissionCompleted" => "Misión completada",
                    "MissionAbandoned" => "Misión abandonada",
                    "MissionExpired" => "Misión vencida",
                    _ => "Misión fallida"
                };
                string? journalMissionName = GetJournalText(data, "Name_Localised", "LocalisedName", "Name");
                string missionName = journalMissionName ?? "Misión";
                object missionArgument = journalMissionName is null ? new LocalizedAlertArgument("mission") : journalMissionName;
                string titleKey = eventType switch
                {
                    "MissionAccepted" => "missionAcceptedTitle",
                    "MissionCompleted" => "missionCompletedTitle",
                    "MissionAbandoned" => "missionAbandonedTitle",
                    "MissionExpired" => "missionExpiredTitle",
                    _ => "missionFailedTitle"
                };
                message = $"{missionName}.";
                localization = new(titleKey, Array.Empty<object>(), "missionSimple", missionArgument);
                if (eventType == "MissionCompleted" && TryGetJournalNumber(data, "Reward", out var reward))
                {
                    message = $"{missionName}. Recompensa: {reward:N0} CR.";
                    localization = new(titleKey, Array.Empty<object>(), "missionReward", missionArgument, reward);
                }
                if (eventType is "MissionFailed" or "MissionExpired")
                    severity = AlertSeverity.Warning;
                break;

            case "RedeemVoucher":
            case "Bounty":
            case "FactionKillBond":
                alertId = "bounty-reward";
                title = eventType switch
                {
                    "RedeemVoucher" => "Vale canjeado",
                    "FactionKillBond" => "Recompensa de combate",
                    _ => "Recompensa por objetivo"
                };
                double? rewardValue = GetJournalNumeric(data, "Amount", "TotalReward", "Reward");
                string? rewardAmount = rewardValue?.ToString("N0");
                string? rewardType = GetJournalText(data, "Type", "Faction");
                message = string.Join(" · ", new[]
                {
                    rewardType,
                    rewardAmount == null ? null : $"{rewardAmount} CR"
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
                if (message.Length == 0)
                    message = "Se ha registrado una recompensa en el Journal.";
                string rewardTitleKey = eventType switch
                {
                    "RedeemVoucher" => "voucherTitle",
                    "FactionKillBond" => "combatRewardTitle",
                    _ => "bountyTitle"
                };
                localization = rewardType is not null
                    ? rewardValue.HasValue
                        ? new(rewardTitleKey, Array.Empty<object>(), "rewardBoth", rewardType, rewardValue.Value)
                        : new(rewardTitleKey, Array.Empty<object>(), "rawValue", rewardType)
                    : rewardValue.HasValue
                        ? new(rewardTitleKey, Array.Empty<object>(), "rewardAmount", rewardValue.Value)
                        : new(rewardTitleKey, Array.Empty<object>(), "rewardNone");
                break;

            case "CommitCrime":
                alertId = "crime-committed";
                title = "Delito cometido";
                severity = AlertSeverity.Warning;
                string? crime = GetJournalText(data, "CrimeType_Localised", "CrimeType");
                double? fineValue = GetJournalNumeric(data, "Fine");
                string? fine = fineValue?.ToString("N0");
                message = string.Join(" · ", new[]
                {
                    crime,
                    fine == null ? null : $"Multa: {fine} CR"
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
                if (message.Length == 0)
                    message = "El Journal ha registrado un delito.";
                localization = crime is not null
                    ? fineValue.HasValue
                        ? new("crimeTitle", Array.Empty<object>(), "crimeBoth", crime, fineValue.Value)
                        : new("crimeTitle", Array.Empty<object>(), "rawValue", crime)
                    : fineValue.HasValue
                        ? new("crimeTitle", Array.Empty<object>(), "fineOnly", fineValue.Value)
                        : new("crimeTitle", Array.Empty<object>(), "crimeNone");
                break;

            case "MarketBuy":
            case "MarketSell":
            case "CollectCargo":
            case "EjectCargo":
            case "MarketTransfer":
            case "CargoDepot":
                alertId = "cargo-activity";
                title = eventType switch
                {
                    "MarketBuy" => "Carga comprada",
                    "MarketSell" => "Carga vendida",
                    "CollectCargo" => "Carga recogida",
                    "EjectCargo" => "Carga expulsada",
                    "MarketTransfer" => "Transferencia de carga",
                    _ => "Actividad de carga"
                };
                string? commodity = GetJournalText(data, "Type_Localised", "Commodity_Localised", "Type", "Commodity");
                double? countValue = GetJournalNumeric(data, "Count");
                string? count = countValue?.ToString("N0");
                message = string.Join(" · ", new[]
                {
                    commodity,
                    count == null ? null : $"Cantidad: {count}"
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
                if (message.Length == 0)
                    message = "Se ha registrado un cambio en la carga.";
                string cargoTitleKey = eventType switch
                {
                    "MarketBuy" => "cargoBoughtTitle",
                    "MarketSell" => "cargoSoldTitle",
                    "CollectCargo" => "cargoCollectedTitle",
                    "EjectCargo" => "cargoEjectedTitle",
                    "MarketTransfer" => "cargoTransferTitle",
                    _ => "cargoActivityTitle"
                };
                localization = commodity is not null
                    ? countValue.HasValue
                        ? new(cargoTitleKey, Array.Empty<object>(), "cargoBoth", commodity, countValue.Value)
                        : new(cargoTitleKey, Array.Empty<object>(), "rawValue", commodity)
                    : countValue.HasValue
                        ? new(cargoTitleKey, Array.Empty<object>(), "countOnly", countValue.Value)
                        : new(cargoTitleKey, Array.Empty<object>(), "cargoNone");
                break;

            case "CarrierJump":
            case "CarrierJumpRequest":
            case "CarrierJumpCancelled":
            case "CarrierBuy":
            case "CarrierDecommission":
                alertId = "carrier-activity";
                title = eventType switch
                {
                    "CarrierJump" => "Salto de portanaves",
                    "CarrierJumpRequest" => "Salto de portanaves solicitado",
                    "CarrierJumpCancelled" => "Salto de portanaves cancelado",
                    "CarrierBuy" => "Portanaves adquirido",
                    _ => "Desmantelamiento de portanaves"
                };
                string? destination = GetJournalText(data, "SystemName", "StarSystem", "CarrierName");
                message = destination == null
                    ? "Se ha registrado una operación de portanaves."
                    : $"Destino o portanaves: {destination}.";
                string carrierTitleKey = eventType switch
                {
                    "CarrierJump" => "carrierJumpTitle",
                    "CarrierJumpRequest" => "carrierRequestTitle",
                    "CarrierJumpCancelled" => "carrierCancelTitle",
                    "CarrierBuy" => "carrierBuyTitle",
                    _ => "carrierDecommissionTitle"
                };
                localization = destination is null
                    ? new(carrierTitleKey, Array.Empty<object>(), "carrierNone")
                    : new(carrierTitleKey, Array.Empty<object>(), "carrierDestination", destination);
                break;

            default:
                alert = null!;
                return false;
        }

        alert = new AlertNotification(
            Guid.NewGuid().ToString("N"),
            alertId,
            title,
            message,
            severity,
            gameEvent.Timestamp) { Localization = localization };
        return true;
    }

    private static string? GetJournalText(System.Text.Json.JsonElement data, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (data.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == System.Text.Json.JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!.Replace('_', ' ');
        }

        return null;
    }

    private static double? GetJournalNumeric(System.Text.Json.JsonElement data, params string[] propertyNames) =>
        TryGetJournalNumber(data, propertyNames, out var amount) ? amount : null;

    private static bool TryGetJournalNumber(
        System.Text.Json.JsonElement data,
        string propertyName,
        out double number) =>
        TryGetJournalNumber(data, new[] { propertyName }, out number);

    private static bool TryGetJournalNumber(
        System.Text.Json.JsonElement data,
        string[] propertyNames,
        out double number)
    {
        foreach (string propertyName in propertyNames)
        {
            if (data.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == System.Text.Json.JsonValueKind.Number &&
                value.TryGetDouble(out number))
                return true;
        }

        number = 0;
        return false;
    }

    private void SetTransitionCondition(
        string alertId,
        bool canDetectTransition,
        bool wasActive,
        bool isActive,
        string title,
        string message,
        AlertSeverity severity,
        AlertLocalization localization)
    {
        if (!isActive)
            SetCondition(alertId, false, "", "", severity);
        else if (canDetectTransition && !wasActive)
            SetCondition(alertId, true, title, message, severity, localization);
    }

    private void EvaluateMissionExpiryConditions(ShipState state)
    {
        bool notifyNewConditions = _initializedMissionConditions;
        var currentConditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int warningHours = _configService.Current.MissionExpiryWarningHours;
        int criticalHours = _configService.Current.MissionExpiryCriticalHours;

        foreach (var mission in state.ActiveMissions)
        {
            if (!mission.Expiry.HasValue)
                continue;

            var remaining = mission.Expiry.Value - DateTimeOffset.Now;
            bool critical = remaining <= TimeSpan.FromHours(criticalHours);
            bool warning = !critical && remaining <= TimeSpan.FromHours(warningHours);
            string warningKey = $"mission-expiry:{mission.Id}:warning";
            string criticalKey = $"mission-expiry:{mission.Id}:critical";
            currentConditions.Add(warningKey);
            currentConditions.Add(criticalKey);

            string remainingText = remaining <= TimeSpan.Zero
                ? "La fecha límite ha pasado."
                : $"Tiempo restante: {mission.TimeRemaining}.";
            string messageKey = remaining <= TimeSpan.Zero ? "expiryPassed" : "expiryRemaining";
            object[] messageArguments = remaining <= TimeSpan.Zero
                ? Array.Empty<object>()
                : new object[] { new AlertRemainingTime(remaining) };
            SetCondition(
                "mission-expiry",
                warning,
                $"Misión próxima a vencer: {mission.Name}",
                remainingText,
                AlertSeverity.Warning,
                warningKey,
                notifyNewConditions,
                new ActivityAlertLocalization("expiryWarningTitle", new object[] { mission.NameIsFallback ? new LocalizedAlertArgument("mission") : mission.Name }, messageKey, messageArguments));
            SetCondition(
                "mission-expiry",
                critical,
                remaining <= TimeSpan.Zero
                    ? $"Misión vencida: {mission.Name}"
                    : $"Misión en tiempo crítico: {mission.Name}",
                remainingText,
                AlertSeverity.Critical,
                criticalKey,
                notifyNewConditions,
                new ActivityAlertLocalization(remaining <= TimeSpan.Zero ? "expiryExpiredTitle" : "expiryCriticalTitle",
                    new object[] { mission.NameIsFallback ? new LocalizedAlertArgument("mission") : mission.Name }, messageKey, messageArguments));
        }

        foreach (string previousCondition in _missionAlertConditions.Except(currentConditions).ToArray())
            SetCondition("mission-expiry", false, "", "", AlertSeverity.Warning, previousCondition, false);

        _missionAlertConditions.Clear();
        _missionAlertConditions.UnionWith(currentConditions);
        _initializedMissionConditions = true;
    }

    private void SetCondition(string alertId, bool isActive, string title, string message, AlertSeverity severity, AlertLocalization? localization = null)
        => SetCondition(alertId, isActive, title, message, severity, alertId, true, localization);

    private void SetCondition(
        string alertId,
        bool isActive,
        string title,
        string message,
        AlertSeverity severity,
        string conditionKey,
        bool notify,
        AlertLocalization? localization = null)
    {
        AlertNotification? notification = null;
        bool changed = false;

        lock (_alertsLock)
        {
            bool wasActive = _activeAlerts.TryGetValue(conditionKey, out var existing);
            if (!isActive)
            {
                changed = _activeAlerts.Remove(conditionKey);
            }
            else if (!wasActive || existing!.Severity != severity)
            {
                notification = new AlertNotification(
                    Guid.NewGuid().ToString("N"),
                    alertId,
                    title,
                    message,
                    severity,
                    DateTimeOffset.Now) { Localization = localization };
                _activeAlerts[conditionKey] = notification;
                changed = true;
            }
            else if (existing.Title != title || existing.Message != message)
            {
                _activeAlerts[conditionKey] = existing with { Title = title, Message = message, Localization = localization };
                changed = true;
            }
        }

        if (notification != null && notify)
            Notify(notification);
        else if (changed)
            AlertsChanged?.Invoke();
    }

    private void Notify(AlertNotification alert)
    {
        _configService.Current.AlertPreferences.TryGetValue(alert.AlertId, out var preference);
        preference ??= new AlertChannelPreferences();
        var translated = CoreAlertTranslations.Translate(alert, _configService.Current.InterfaceLanguage);

        if (preference.VisualEnabled)
        {
            lock (_alertsLock)
            {
                _alerts.Insert(0, alert);
                if (_alerts.Count > MaximumHistory)
                    _alerts.RemoveRange(MaximumHistory, _alerts.Count - MaximumHistory);
            }

            AlertRaised?.Invoke(translated);
        }

        if (preference.SoundEnabled || preference.VoiceEnabled)
            AlertTriggered?.Invoke(translated, preference);

        AlertsChanged?.Invoke();
    }

    private void ClearAllActiveAlerts()
    {
        bool changed;
        lock (_alertsLock)
        {
            changed = _activeAlerts.Count > 0;
            _activeAlerts.Clear();
        }

        if (changed)
            AlertsChanged?.Invoke();
    }

    private static double GetFuelPercent(StateSnapshot state) =>
        Math.Clamp(state.FuelMain / state.FuelMax * 100, 0, 100);

    private static StateSnapshot Capture(ShipState state) => new(
        state.IsGameDetected,
        state.HasStatusTelemetry,
        state.HasLoadoutTelemetry,
        state.HasShieldGenerator,
        state.CockpitBreached,
        state.HasFuelCapacity,
        state.HasHullTelemetry,
        state.LowFuel,
        state.OverHeating,
        state.ShieldsUp,
        state.BeingInterdicted,
        state.IsInDanger,
        state.HullHealth,
        state.FuelMain,
        state.FuelMax);

    private sealed record StateSnapshot(
        bool IsGameDetected,
        bool HasStatusTelemetry,
        bool HasLoadoutTelemetry,
        bool HasShieldGenerator,
        bool CockpitBreached,
        bool HasFuelCapacity,
        bool HasHullTelemetry,
        bool LowFuel,
        bool OverHeating,
        bool ShieldsUp,
        bool BeingInterdicted,
        bool IsInDanger,
        double HullHealth,
        double FuelMain,
        double FuelMax);
}
