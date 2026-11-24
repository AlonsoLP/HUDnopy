// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/MainWindow.Telemetry.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: UI updates for dashboard, flight instruments, Fleet Carrier, tritium calculator, BGS, and session stats.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using HUDnopy.Models;
using HUDnopy.Services;
using Forms = System.Windows.Forms;
using WpfBinding = System.Windows.Data.Binding;
using WpfControl = System.Windows.Controls.Control;
using HUDnopy.Localization;

namespace HUDnopy;

/// <summary>
/// UI updates for dashboard, flight instruments, Fleet Carrier, tritium calculator, BGS, and session stats.
/// </summary>
public partial class MainWindow
{
    private void OnSpanshDataChanged(SpanshSystemDataState state)
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        Dispatcher.BeginInvoke(() => UpdateSpanshUi(state));
    }

    private void UpdateSpanshUi(SpanshSystemDataState state)
    {
        SpanshSection.Visibility = _configService.Current.EnableSpanshSystemData
            ? Visibility.Visible : Visibility.Collapsed;
        SpanshSystemDisplay display = SpanshTranslations.Present(state, _uiLanguage);
        TxtSpanshTitle.Text = display.Title;
        TxtSpanshStatus.Text = display.Status;
        TxtSpanshSummary.Text = display.Summary;
        TxtSpanshUpdated.Text = display.Updated;
        TxtSpanshUpdated.Visibility = string.IsNullOrEmpty(display.Updated) ? Visibility.Collapsed : Visibility.Visible;
        TxtSpanshSourceNote.Text = display.SourceNote;
        TxtSpanshStationsTitle.Text = display.StationsTitle;
        TxtSpanshStationsEmpty.Text = display.EmptyStations;
        TxtSpanshStationsEmpty.Visibility = display.Stations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GridSpanshStations.Columns[0].Header = display.StationHeader;
        GridSpanshStations.Columns[1].Header = display.TypeHeader;
        GridSpanshStations.Columns[2].Header = display.StationBodyHeader;
        GridSpanshStations.Columns[3].Header = display.ArrivalHeader;
        GridSpanshStations.Columns[4].Header = display.FactionHeader;
        GridSpanshStations.ItemsSource = display.Stations;
        GridSpanshStations.Visibility = display.Stations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshServiceHealthUi();
    }

    private void UpdateCarrierUi(ShipState state)
    {
        _lastCarrierState = (state.Carrier, _uiLanguage);
        var display = CarrierTranslations.Present(state.Carrier, _uiLanguage);
        TxtCarrierTitle.Text = display.Title;
        TxtCarrierStatus.Text = display.Status;
        TxtCarrierStatus.Visibility = display.HasCarrier ? Visibility.Collapsed : Visibility.Visible;
        TxtCarrierJumpTitle.Text = display.JumpTitle;
        void SetMetric(CarrierMetric metric, TextBlock title, TextBlock value, System.Windows.Controls.ProgressBar progress)
        {
            title.Text = metric.Title;
            value.Text = $"{metric.Left.Value} / {metric.Right.Value}";
            value.ToolTip = $"{metric.Left.Label}: {metric.Left.Value}; {metric.Right.Label}: {metric.Right.Value}";
            System.Windows.Automation.AutomationProperties.SetName(value, (string)value.ToolTip);
            System.Windows.Automation.AutomationProperties.SetName(progress, metric.Title);
            progress.Visibility = metric.Percent.HasValue ? Visibility.Visible : Visibility.Collapsed;
            progress.Value = metric.Percent ?? 0;
        }
        SetMetric(display.Space, TxtCarrierSpaceTitle, TxtCarrierSpaceValue, CarrierSpaceProgress);
        SetMetric(display.Finance, TxtCarrierFinanceTitle, TxtCarrierFinanceValue, CarrierFinanceProgress);
        SetMetric(display.Fuel, TxtCarrierFuelTitle, TxtCarrierFuelValue, CarrierFuelProgress);
        CarrierJumpRows.ItemsSource = display.Jump;
        CarrierCards.Visibility = display.HasCarrier ? Visibility.Visible : Visibility.Collapsed;
        CarrierJumpCard.Visibility = display.HasJump ? Visibility.Visible : Visibility.Collapsed;
        GridCarrier.Columns[0].Header = display.FieldHeader;
        GridCarrier.Columns[1].Header = display.ValueHeader;
        if (GridCarrier.ItemsSource is not IReadOnlyList<CarrierRow> currentRows || !currentRows.SequenceEqual(display.Rows))
            GridCarrier.ItemsSource = display.Rows;
        GridCarrier.Visibility = display.Rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ChkEnableSpanshSystemData_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        _configService.Current.EnableSpanshSystemData = ChkEnableSpanshSystemData.IsChecked == true;
        _configService.Save();
    }


    private void UpdateDashboardUi(ShipState s)
    {
        var text = DashboardTranslations.ForLanguage(_uiLanguage);
        var culture = UiCulture;
        bool hasSessionData = s.CommanderName != "CMDR DESCONOCIDO";
        string shipName = !hasSessionData && s.ShipName == "SIN SEÑAL" ? text["noSignal"] : s.ShipName;
        TxtModeBadge.Text = s.IsGameDetected ? text["online"] : text["inactive"];
        TxtShipTitle.Text = $"{shipName} [{s.ShipIdent}]";
        TxtShipSubHeader.Text = s.ShipType;
        TxtDashboardCommander.Text = hasSessionData ? s.CommanderName : text["unknownCommander"];
        RefreshServiceHealthUi();
        TxtDashboardShip.Text = $"{shipName} [{s.ShipIdent}] · {s.ShipType}";
        TxtDashboardSystem.Text = s.CurrentSystem;
        TxtDashboardLocation.Text = !string.IsNullOrEmpty(s.DestinationName)
            ? $"{text["destination"]}: {s.DestinationName}"
            : !string.IsNullOrEmpty(s.StationName)
                ? $"{text["station"]}: {s.StationName}"
                : !string.IsNullOrEmpty(s.BodyName)
                    ? $"{text["body"]}: {s.BodyName}"
                    : hasSessionData ? text["deepSpace"] : "---";
        string hullSummary = s.HasHullTelemetry
            ? $"{text["hull"]} {Math.Clamp(s.HullHealth, 0, 100).ToString("0.0", culture)}%"
            : $"{text["hull"]} ---";
        string fuelSummary = s.HasFuelCapacity && s.FuelMax > 0
            ? $"{text["fuel"]} {s.FuelMain.ToString("0.0", culture)}/{s.FuelMax.ToString("0.0", culture)} T ({Math.Clamp(s.FuelMain / s.FuelMax * 100, 0, 100).ToString("0.0", culture)}%)"
            : s.HasStatusTelemetry
                ? $"{text["fuel"]} {s.FuelMain.ToString("0.0", culture)} T ({text["capacityUnconfirmed"]})"
                : $"{text["fuel"]} ---";
        TxtDashboardResources.Text = $"{hullSummary} · {fuelSummary}";
        TxtDashboardCargo.Text = s.CargoCapacity > 0
            ? $"{text["cargo"]} {s.CargoUsed} / {s.CargoCapacity} T"
            : hasSessionData ? $"{text["cargo"]} {s.CargoUsed} T" : $"{text["cargo"]} ---";
        var currentWaypointIndex = s.NavRoute.FindIndex(waypoint => waypoint.IsCurrentSystem);
        var nextWaypoint = currentWaypointIndex >= 0 && currentWaypointIndex + 1 < s.NavRoute.Count
            ? s.NavRoute[currentWaypointIndex + 1]
            : s.NavRoute.FirstOrDefault(waypoint => !waypoint.IsCurrentSystem);
        TxtDashboardRoute.Text = nextWaypoint == null
            ? text["noNextJump"]
            : $"{nextWaypoint.StarSystem} · {nextWaypoint.StepDistanceLy.ToString("0.0", culture)} {text["lightYears"]} · {text["remainingJumps"]}: {Math.Max(0, s.NavRoute.Count - 1 - Math.Max(0, currentWaypointIndex))}";
        RefreshHeaderModules(s);

        var entry = s.RecentEvents.Count > 0 ? JournalTranslations.Present(s.RecentEvents[0], _uiLanguage) : null;
        TxtDashboardActivity.Text = entry is null
            ? text["waitingEvents"]
            : $"{entry.Timestamp} · {entry.EventType}: {entry.Summary}";
    }


    private void UpdateFlightUi(ShipState s)
    {
        var flight = FlightTranslations.ForLanguage(_uiLanguage);
        var culture = UiCulture;
        // Barras de estado
        double hull = Math.Clamp(s.HullHealth, 0, 100);
        TxtHullPercent.Text = s.HasHullTelemetry ? $"{hull.ToString("0.0", culture)}%" : "---";
        BarHull.Value = hull;
        TxtShieldStatus.Text = !s.HasShieldTelemetry
            ? "---"
            : s.HasLoadoutTelemetry && !s.HasShieldGenerator
                ? flight["noGenerator"]
                : s.ShieldsUp ? flight["shieldUp"] : flight["shieldDown"];
        BarShield.Value = s.ShieldsUp ? 100 : 0;
        BarShield.Visibility = s.HasShieldTelemetry ? Visibility.Visible : Visibility.Collapsed;

        bool hasFuelCapacity = s.HasFuelCapacity && s.FuelMax > 0;
        double fuelMax = hasFuelCapacity ? s.FuelMax : 0;
        double fuelPct = hasFuelCapacity ? Math.Clamp((s.FuelMain / fuelMax) * 100.0, 0, 100) : 0;
        TxtFuelValue.Text = hasFuelCapacity
            ? $"{s.FuelMain.ToString("0.0", culture)} / {fuelMax.ToString("0.0", culture)} T ({fuelPct.ToString("0.0", culture)}%) · {flight["reserve"]} {s.FuelReservoir.ToString("0.00", culture)} T"
            : s.HasStatusTelemetry
                ? $"{s.FuelMain.ToString("0.0", culture)} T · {DashboardTranslations.ForLanguage(_uiLanguage)["capacityUnconfirmed"]}"
                : "---";
        BarFuel.Value = fuelPct;

        int cargoCap = s.CargoCapacity;
        double cargoPct = cargoCap > 0 ? Math.Clamp((s.CargoUsed / (double)cargoCap) * 100.0, 0, 100) : 0;
        TxtCargoValue.Text = cargoCap > 0
            ? $"{s.CargoUsed} / {cargoCap} T"
            : s.HasStatusTelemetry ? $"{s.CargoUsed} T · {DashboardTranslations.ForLanguage(_uiLanguage)["capacityUnconfirmed"]}" : "---";
        BarCargo.Value = cargoPct;

        // Pips
        TxtPipSys.Text = s.HasStatusTelemetry ? s.PipsSys.ToString("0.0", culture) : "---";
        TxtPipEng.Text = s.HasStatusTelemetry ? s.PipsEng.ToString("0.0", culture) : "---";
        TxtPipWep.Text = s.HasStatusTelemetry ? s.PipsWep.ToString("0.0", culture) : "---";

        // Matriz de sistemas en Cabina
        RenderCockpitSystemCards(s);

        // Navigation
        TxtNavSystem.Text = s.CurrentSystem;
        TxtNavSecurity.Text = s.SecurityLevel;
        TxtNavEconomy.Text = s.Economy;
        TxtNavAllegiance.Text = s.AllegianceIsFallback ? flight["independent"] : s.Allegiance;
        TxtNavBody.Text = !string.IsNullOrEmpty(s.StationName)
            ? $"{s.StationName} ({flight["docked"]})"
            : string.IsNullOrEmpty(s.BodyName) ? "---" : s.BodyName;
        TxtNavSurface.Text = s.HasLatLong
            ? $"LAT: {s.Latitude.ToString("0.0000", culture)}°  |  LON: {s.Longitude.ToString("0.0000", culture)}°\nALT: {s.Altitude.ToString("0.0", culture)} m  |  {flight["heading"]}: {s.Heading.ToString("0", culture)}°"
            : flight["noSurface"];
        TxtOnFootStatus.Text = FlightTranslations.OnFootStatus(s, _uiLanguage);
        TxtOnFootOxygen.Text = s.Oxygen.HasValue ? $"{(s.Oxygen.Value * 100).ToString("0.#", culture)}%" : "---";
        TxtOnFootHealth.Text = s.Health.HasValue ? $"{(s.Health.Value * 100).ToString("0.#", culture)}%" : "---";
        TxtOnFootTemperature.Text = s.TemperatureKelvin.HasValue ? $"{s.TemperatureKelvin.Value.ToString("0.##", culture)} K" : "---";
        TxtOnFootGravity.Text = s.Gravity.HasValue ? $"{s.Gravity.Value.ToString("0.##", culture)} G" : "---";
        TxtOnFootWeapon.Text = string.IsNullOrWhiteSpace(s.SelectedWeapon) ? "---" : s.SelectedWeapon;
        TxtActiveSuit.Text = $"{flight["suit"]}: {(string.IsNullOrWhiteSpace(s.ActiveSuit) ? "---" : s.ActiveSuit)} · {flight["loadout"]}: {(string.IsNullOrWhiteSpace(s.ActiveSuitLoadout) ? "---" : s.ActiveSuitLoadout)}";

        string routeSummary = s.NavRoute.Count > 0
            ? string.Format(culture, flight["remaining"], Math.Max(0, s.NavRoute.Count - 1))
            : flight["noRoute"];
        if (s.NavRoute.Count > 1)
        {
            if (s.RouteHasNoFuelStar)
                routeSummary += "  ·  ⚠ " + flight["routeNoFuelStar"];
            else if (s.JumpsToNextFuelStar is int nextFuel && nextFuel > 1)
                routeSummary += "  ·  " + string.Format(culture, flight["routeNextFuelStar"], nextFuel);
        }
        TxtRouteSummary.Text = routeSummary;

        UpdateCopilotTacticalUi(s, flight, culture);
        UpdateShipModulesAndVehiclesUi(s, flight, culture);
        UpdateExplorationProgressAndTracker(s);
    }

    private IReadOnlyList<ShipModuleItem>? _lastShipModulesSource;
    private string? _lastShipModulesLoadoutJson;
    private string? _lastShipModulesLang;
    private IReadOnlyList<MaterialItem>? _lastTraderMaterialsSource;
    private string? _lastTraderMaterialsLang;

    private void UpdateShipModulesAndVehiclesUi(
        ShipState s,
        IReadOnlyDictionary<string, string> flight,
        System.Globalization.CultureInfo culture)
    {
        if (s.InSRV || s.InFighter)
        {
            var vParts = new List<string>();
            if (s.InSRV)
            {
                vParts.Add(flight["vehicleSrv"]);
                vParts.Add($"{flight["srvHandbrake"]}: {flight[s.SrvHandbrake ? "active" : "inactive"]}");
                vParts.Add($"{flight["srvDriveAssist"]}: {flight[s.SrvDriveAssist ? "active" : "inactive"]}");
                vParts.Add($"{flight["srvTurret"]}: {flight[s.SrvUsingTurretView ? "active" : s.SrvTurretRetracted ? "srvRetracted" : "inactive"]}");
                if (s.SrvHighBeam)
                    vParts.Add(flight["srvHighBeam"]);
            }
            if (s.InFighter)
                vParts.Add(flight["vehicleFighter"]);
            TxtVehicleStatus.Text = string.Join("  ·  ", vParts);
            VehicleTelemetryCard.Visibility = Visibility.Visible;
        }
        else
        {
            VehicleTelemetryCard.Visibility = Visibility.Collapsed;
        }

        if (ReferenceEquals(_lastShipModulesSource, s.ShipModules) &&
            string.Equals(_lastShipModulesLoadoutJson, s.LastLoadoutJson, StringComparison.Ordinal) &&
            string.Equals(_lastShipModulesLang, _uiLanguage, StringComparison.Ordinal))
            return;

        _lastShipModulesSource = s.ShipModules;
        _lastShipModulesLoadoutJson = s.LastLoadoutJson;
        _lastShipModulesLang = _uiLanguage;

        if (!ReferenceEquals(GridShipModules.ItemsSource, s.ShipModules))
            GridShipModules.ItemsSource = s.ShipModules;

        bool hasLoadout = !string.IsNullOrWhiteSpace(s.LastLoadoutJson);
        if (BtnExportEdsy is not null) BtnExportEdsy.IsEnabled = hasLoadout;
        if (BtnExportCoriolis is not null) BtnExportCoriolis.IsEnabled = hasLoadout;

        if (s.ShipModules.Count == 0)
        {
            TxtModulesPowerSummary.Text = flight["modulesWaiting"];
            TxtModulesAmmoSummary.Text = "";
            return;
        }

        double totalPower = s.ShipModules.Where(static m => m.On && m.PowerDrawMw > 0).Sum(static m => m.PowerDrawMw);
        var byPriority = Enumerable.Range(1, 5)
            .Select(p => (Priority: p, Power: s.ShipModules.Where(m => m.On && m.Priority == p && m.PowerDrawMw > 0).Sum(static m => m.PowerDrawMw)))
            .Where(static x => x.Power > 0)
            .Select(x => totalPower > 0
                ? $"P{x.Priority}: {x.Power.ToString("0.00", culture)} MW ({Math.Round(x.Power / totalPower * 100)}%)"
                : $"P{x.Priority}: {x.Power.ToString("0.00", culture)} MW")
            .ToList();
        TxtModulesPowerSummary.Text = byPriority.Count > 0
            ? string.Format(culture, flight["modulesPowerSummary"], totalPower.ToString("0.00", culture), string.Join(" · ", byPriority))
            : string.Format(culture, flight["modulesCount"], s.ShipModules.Count);

        var ammoItems = s.ShipModules
            .Where(static m => m.AmmoInClip.HasValue || m.AmmoInHopper.HasValue)
            .Select(static m => $"{m.DisplayName}: {m.AmmoDisplay}")
            .Take(8)
            .ToList();
        TxtModulesAmmoSummary.Text = ammoItems.Count > 0
            ? string.Format(culture, flight["modulesAmmoSummary"], string.Join(" · ", ammoItems))
            : "";
    }

    private void UpdateMaterialTraderSummary(ShipState s)
    {
        if (TxtMaterialTraderSummary is null)
            return;

        if (ReferenceEquals(_lastTraderMaterialsSource, s.MaterialsInventory) &&
            string.Equals(_lastTraderMaterialsLang, _uiLanguage, StringComparison.Ordinal))
            return;

        _lastTraderMaterialsSource = s.MaterialsInventory;
        _lastTraderMaterialsLang = _uiLanguage;

        var text = MaterialTranslations.ForLanguage(_uiLanguage);
        var parts = new List<string>();
        var fullHighGrade = s.MaterialsInventory
            .Where(static m => m.Grade is >= 4 && m.Count >= m.MaxCapacity)
            .Select(static m => $"{m.DisplayName} (G{m.Grade})")
            .Take(5)
            .ToList();
        if (fullHighGrade.Count > 0)
            parts.Add(string.Format(text["traderFullSummary"], string.Join(", ", fullHighGrade)));

        var wishlist = s.MaterialsInventory.Where(static m => m.WishlistTarget > 0).ToList();
        if (wishlist.Count > 0)
        {
            int met = wishlist.Count(static m => m.Count >= m.WishlistTarget);
            parts.Add(string.Format(text["wishlistSummary"], wishlist.Count, met));
        }

        TxtMaterialTraderSummary.Text = string.Join("  ·  ", parts);
        TxtMaterialTraderSummary.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnToggleWishlist_Click(object sender, RoutedEventArgs e)
    {
        if (GridMaterials.SelectedItem is not MaterialItem selected)
            return;

        var wishlist = _configService.Current.MaterialWishlist ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (wishlist.ContainsKey(selected.Id))
        {
            wishlist.Remove(selected.Id);
        }
        else
        {
            int target = int.TryParse(TxtWishlistTarget.Text.Trim(), out int parsed) && parsed > 0
                ? Math.Min(parsed, selected.MaxCapacity)
                : Math.Min(50, selected.MaxCapacity);
            wishlist[selected.Id] = target;
        }

        _configService.Save();
    }


    private void UpdateCarrierAdvancedUi(ShipState s)
    {
        UpdateCarrierTritiumCalc(s);
        if (TxtCarrierMaterialsStatus is null || GridCarrierMaterials is null)
            return;

        var flight = FlightTranslations.ForLanguage(_uiLanguage);
        var culture = UiCulture;
        if (s.CarrierMaterials.Count == 0)
        {
            TxtCarrierMaterialsStatus.Text = flight["fcMaterialsWaiting"];
        }
        else
        {
            string station = string.IsNullOrWhiteSpace(s.CarrierMaterialsStation) ? "Portanaves" : s.CarrierMaterialsStation;
            TxtCarrierMaterialsStatus.Text = string.Format(culture, flight["fcMaterialsCount"], station, s.CarrierMaterials.Count);
        }

        if (!ReferenceEquals(GridCarrierMaterials.ItemsSource, s.CarrierMaterials))
            GridCarrierMaterials.ItemsSource = s.CarrierMaterials;
    }

    private void TxtCarrierRouteLy_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_fileWatcher is null)
            return;
        UpdateCarrierTritiumCalc(_fileWatcher.State);
    }

    private (long Fuel, long Used, string RouteText, string Language)? _lastTritiumCalcKey;

    private void UpdateCarrierTritiumCalc(ShipState s)
    {
        if (TxtCarrierTritiumResult is null || TxtCarrierRouteLy is null)
            return;

        long usedCapacity = s.Carrier?.Capacity is long cap && s.Carrier?.FreeSpace is long free
            ? Math.Max(0, cap - free)
            : (s.Carrier?.Cargo ?? 0);
        long fuel = Math.Max(0, s.Carrier?.Fuel ?? 0);
        string routeText = TxtCarrierRouteLy.Text.Trim();
        var key = (fuel, usedCapacity, routeText, _uiLanguage);
        if (_lastTritiumCalcKey == key)
            return;
        _lastTritiumCalcKey = key;

        var flight = FlightTranslations.ForLanguage(_uiLanguage);
        var culture = UiCulture;
        if (!double.TryParse(
                routeText.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double totalLy) || totalLy <= 0)
        {
            TxtCarrierTritiumResult.Text = flight["tritiumPrompt"];
            return;
        }

        long initialExtraMass = usedCapacity + fuel;

        int jumps = (int)Math.Ceiling(totalLy / 500.0);
        double remaining = totalLy;
        long totalTritium = 0;
        long simFuel = fuel;
        for (int i = 0; i < jumps; i++)
        {
            double leg = Math.Min(500.0, remaining);
            remaining -= leg;
            long stepCost = (long)Math.Round(5.0 + (leg / 4.0) * (1.0 + (usedCapacity + simFuel) / 25000.0));
            totalTritium += stepCost;
            simFuel = Math.Max(0, simFuel - stepCost);
        }

        int depotJumps = 0;
        long depotSimFuel = fuel;
        while (depotSimFuel > 0)
        {
            long legCost = (long)Math.Round(5.0 + (500.0 / 4.0) * (1.0 + (usedCapacity + depotSimFuel) / 25000.0));
            if (depotSimFuel < legCost)
                break;
            depotSimFuel -= legCost;
            depotJumps++;
        }

        TxtCarrierTritiumResult.Text = string.Format(
            culture,
            flight["tritiumResult"],
            jumps,
            totalTritium,
            fuel,
            depotJumps,
            initialExtraMass);
    }

    private void UpdateBgsAndSystemNoteUi(ShipState s)
    {
        var flight = FlightTranslations.ForLanguage(_uiLanguage);
        if (TxtPowerplaySummary is not null)
        {
            TxtPowerplaySummary.Text = string.IsNullOrWhiteSpace(s.PowerplaySummary)
                ? flight["bgsNoPowerplay"]
                : string.Format(flight["bgsPowerplay"], s.PowerplaySummary);
        }

        if (GridSystemFactions is not null && !ReferenceEquals(GridSystemFactions.ItemsSource, s.SystemFactions))
            GridSystemFactions.ItemsSource = s.SystemFactions;

        if (TxtSystemNote is not null &&
            !string.Equals(_lastSystemNoteLoaded, s.CurrentSystem, StringComparison.OrdinalIgnoreCase))
        {
            _lastSystemNoteLoaded = s.CurrentSystem;
            TxtSystemNote.Text = s.CurrentSystemNote ?? "";
        }

        if (!string.IsNullOrWhiteSpace(s.CurrentSystem) &&
            !string.Equals(_lastEdsmIntelSystem, s.CurrentSystem, StringComparison.OrdinalIgnoreCase) &&
            _configService.Current.EnableEdsmSync)
        {
            _lastEdsmIntelSystem = s.CurrentSystem;
            _ = _communitySync.FetchEdsmSystemIntelAsync(s.CurrentSystem);
        }

        if (!string.IsNullOrWhiteSpace(s.CurrentSystem) &&
            !string.Equals(_lastEdsmDiscoverySystem, s.CurrentSystem, StringComparison.OrdinalIgnoreCase) &&
            _configService.Current.EnableEdsmSync)
        {
            _lastEdsmDiscoverySystem = s.CurrentSystem;
            _ = _communitySync.FetchEdsmDiscoveryAndFactionsAsync(s.CurrentSystem);
        }
    }

    private void BtnSaveSystemNote_Click(object sender, RoutedEventArgs e)
    {
        string sys = _fileWatcher.State.CurrentSystem;
        if (string.IsNullOrWhiteSpace(sys))
            return;

        string note = TxtSystemNote.Text.Trim();
        if (string.IsNullOrEmpty(note))
            _configService.Current.SystemNotes.Remove(sys);
        else
            _configService.Current.SystemNotes[sys] = note;
        _configService.Save();
        _fileWatcher.SyncCurrentSystemNote();
        if (_configService.Current.EnableEdsmSync)
            _ = _communitySync.SetEdsmSystemCommentAsync(sys, note);
        TxtStatusBarLeft.Text = string.Format(FlightTranslations.ForLanguage(_uiLanguage)["noteSaved"], sys);
    }

    private async void BtnFetchEdsmIntel_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("EDSM Intel", async () =>
        {
            string sys = _fileWatcher.State.CurrentSystem;
            if (string.IsNullOrWhiteSpace(sys))
                return;
            _lastEdsmIntelSystem = sys;
            var intel = await _communitySync.FetchEdsmSystemIntelAsync(sys);
            if (intel is not null)
                ApplyEdsmSystemIntel(intel);
        });

    private void ApplyEdsmSystemIntel(EdsmSystemIntel intel)
    {
        if (TxtEdsmSystemIntel is null)
            return;
        if (!string.Equals(intel.SystemName, _fileWatcher.State.CurrentSystem, StringComparison.OrdinalIgnoreCase))
            return;

        var flight = FlightTranslations.ForLanguage(_uiLanguage);
        var culture = UiCulture;
        TxtEdsmSystemIntel.Text = string.Format(
            culture,
            flight["edsmIntelSummary"],
            intel.TrafficDay,
            intel.TrafficWeek,
            intel.DeathsDay,
            intel.DeathsWeek);
        TxtEdsmSystemIntel.SetResourceReference(
            TextBlock.ForegroundProperty,
            intel.DeathsDay > 0 ? "HudDangerBrush" : "HudSecondaryBrush");
        TxtEdsmSystemIntel.Visibility = Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(intel.RemoteComment) &&
            TxtSystemNote is not null &&
            string.IsNullOrWhiteSpace(TxtSystemNote.Text))
        {
            TxtSystemNote.Text = intel.RemoteComment;
            _configService.Current.SystemNotes[intel.SystemName] = intel.RemoteComment;
            _configService.Save();
            _fileWatcher.SyncCurrentSystemNote();
        }
    }


    private void UpdateSessionStatsUi(ShipState s)
    {
        if (TxtSessionStatsSummary is null)
            return;

        var dash = DashboardTranslations.ForLanguage(_uiLanguage);
        var culture = UiCulture;
        var st = s.SessionStats;
        string sign = st.NetCredits >= 0 ? "+" : "";
        TxtSessionStatsSummary.Text = string.Format(
            culture,
            dash["sessionStatsSummary"],
            $"{sign}{st.NetCredits.ToString("N0", culture)}",
            st.CreditsPerHour.ToString("N0", culture),
            st.SystemsVisited,
            st.TotalDistanceLy.ToString("0.0", culture),
            st.BodiesScanned,
            st.OrganicsAnalysed,
            st.MissionsCompleted);
    }

    private void BtnResetSessionStats_Click(object sender, RoutedEventArgs e)
    {
        _fileWatcher.ResetSessionStats();
    }

    private void RefreshJournalHistoryList()
    {
        if (CmbJournalHistoryFiles is null)
            return;

        var ops = OperationsTranslations.ForLanguage(_uiLanguage);
        var options = new List<FilterOption> { new("", ops["journalLiveSession"]) };
        string folder = _configService.Current.JournalFolderPath;
        if (Directory.Exists(folder))
        {
            try
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("Journal.*.log")
                    .OrderByDescending(static f => f.LastWriteTimeUtc)
                    .Take(30))
                {
                    options.Add(new FilterOption(file.FullName, $"{file.Name} ({file.LastWriteTime:dd/MM HH:mm})"));
                }
            }
            catch
            {
                // Ignorar error de acceso
            }
        }

        string current = CmbJournalHistoryFiles.SelectedValue as string ?? "";
        CmbJournalHistoryFiles.ItemsSource = options;
        CmbJournalHistoryFiles.SelectedValue = options.Any(o => o.Key == current) ? current : "";
    }

    private void BtnRefreshJournalHistory_Click(object sender, RoutedEventArgs e)
    {
        RefreshJournalHistoryList();
    }

    private void CmbJournalHistoryFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fileWatcher is null || CmbJournalHistoryFiles is null || GridJournal is null)
            return;

        string selectedPath = CmbJournalHistoryFiles.SelectedValue as string ?? "";
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            _viewingHistoricalJournal = false;
            UpdateJournalUi(_fileWatcher.State);
            return;
        }

        _viewingHistoricalJournal = true;
        var entries = EliteFileWatcherService.LoadHistoricalJournal(selectedPath);
        GridJournal.ItemsSource = entries.Select(entry => JournalTranslations.Present(entry, _uiLanguage)).ToList();
    }

    private void BtnExportJournalCsv_Click(object sender, RoutedEventArgs e)
    {
        ExportJournalCurrentView(asJson: false);
    }

    private void BtnExportJournalJson_Click(object sender, RoutedEventArgs e)
    {
        ExportJournalCurrentView(asJson: true);
    }

    private void ExportJournalCurrentView(bool asJson)
    {
        var rows = (GridJournal.ItemsSource as IEnumerable<JournalEntryView>)?.ToList() ?? new List<JournalEntryView>();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"HUDnopy_Journal_{DateTime.Now:yyyyMMdd_HHmmss}",
            DefaultExt = asJson ? ".json" : ".csv",
            Filter = asJson ? "JSON (*.json)|*.json" : "CSV (*.csv)|*.csv"
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            if (asJson)
            {
                var payload = rows.Select(static r => new { r.Timestamp, r.EventType, r.Summary }).ToList();
                string json = System.Text.Json.JsonSerializer.Serialize(payload, HttpClientHeaderDefaults.IndentedJsonOptions);
                File.WriteAllText(dialog.FileName, json, System.Text.Encoding.UTF8);
            }
            else
            {
                static string Csv(string v) => "\"" + (v ?? "").Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                var lines = new List<string> { "Timestamp,EventType,Summary" };
                lines.AddRange(rows.Select(r => $"{Csv(r.Timestamp)},{Csv(r.EventType)},{Csv(r.Summary)}"));
                File.WriteAllLines(dialog.FileName, lines, System.Text.Encoding.UTF8);
            }
            TxtStatusBarLeft.Text = string.Format(OperationsTranslations.ForLanguage(_uiLanguage)["exportSuccess"], Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("Journal.Export", $"Failed to export journal view: {ex.Message}");
            SetStatusBarMessage($"⚠ {ex.Message}");
        }
    }

    // =========================================================================
    // Galaxy Status (EDSM Server Status + EliteBGS Tick) & EDSY/Coriolis Export
    // =========================================================================

    /// <summary>
    /// Updates Dashboard and BGS panels with the latest Elite server status and galaxy BGS tick.
    /// </summary>
    private void UpdateGalaxyStatusUi(GalaxyStatusSnapshot snap)
    {
        var flight = FlightTranslations.ForLanguage(_uiLanguage);

        string serverText = string.IsNullOrWhiteSpace(snap.ServerMessage) ? "---" : snap.ServerMessage;
        string tickShort = "---";
        string tickFull = flight["bgsTickUnavailable"];

        if (snap.LastBgsTickUtc is DateTimeOffset tickUtc)
        {
            TimeSpan age = DateTimeOffset.UtcNow - tickUtc;
            string elapsed = age.TotalHours >= 1
                ? $"{(int)age.TotalHours}h {age.Minutes:D2}m"
                : $"{Math.Max(0, (int)age.TotalMinutes)}m";

            string utcStr = tickUtc.ToString("yyyy-MM-dd HH:mm");
            string localStr = tickUtc.ToLocalTime().ToString("HH:mm");
            tickShort = $"{localStr} ({elapsed})";
            tickFull = string.Format(flight["bgsTickInfo"], utcStr, localStr, elapsed);
        }

        if (TxtGalaxyStatusSummary is not null)
            TxtGalaxyStatusSummary.Text = string.Format(flight["galaxyServerStatus"], serverText, tickShort);

        if (TxtBgsTickInfo is not null)
            TxtBgsTickInfo.Text = tickFull;
    }

    private async void BtnRefreshGalaxyStatus_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Refresh Galaxy Status", async () =>
        {
            if (BtnRefreshGalaxyStatus is not null)
                BtnRefreshGalaxyStatus.IsEnabled = false;
            try
            {
                var snap = await _galaxyStatus.RefreshAsync();
                UpdateGalaxyStatusUi(snap);
            }
            finally
            {
                if (BtnRefreshGalaxyStatus is not null)
                    BtnRefreshGalaxyStatus.IsEnabled = true;
            }
        });

    private void BtnExportEdsy_Click(object sender, RoutedEventArgs e)
    {
        string? url = ShipLoadoutExportService.BuildEdsyUrl(_fileWatcher.State.LastLoadoutJson);
        OpenExternalLoadoutUrl(url);
    }

    private void BtnExportCoriolis_Click(object sender, RoutedEventArgs e)
    {
        string? url = ShipLoadoutExportService.BuildCoriolisUrl(_fileWatcher.State.LastLoadoutJson);
        OpenExternalLoadoutUrl(url);
    }

    private int? _lastAnnouncedLandingPad;
    private string _lastAnnouncedLandingStation = "";
    private int _lastCarrierCountdownStage;
    private DateTimeOffset? _lastCarrierDepartureTime;

    /// <summary>
    /// Shows starport landing pad spatial guidance on DockingGranted and announces it via TTS.
    /// </summary>
    private void UpdateLandingPadUi(ShipState s)
    {
        if (LandingPadCard is null || TxtLandingPadStation is null || TxtLandingPadGuidance is null || TxtLandingPadDiagram is null)
            return;

        if (s.AssignedLandingPad is not int pad || pad <= 0)
        {
            LandingPadCard.Visibility = Visibility.Collapsed;
            _lastAnnouncedLandingPad = null;
            _lastAnnouncedLandingStation = "";
            return;
        }

        var info = LandingPadLocatorService.Resolve(pad, s.AssignedLandingStationType);
        string station = string.IsNullOrWhiteSpace(s.AssignedLandingStation) ? s.CurrentSystem : s.AssignedLandingStation;
        string guidance = info.FormatGuidance(_uiLanguage);

        TxtLandingPadStation.Text = $"🛬 {station.ToUpperInvariant()}";
        TxtLandingPadGuidance.Text = guidance;
        TxtLandingPadDiagram.Text = info.BuildBayIndicator();
        LandingPadCard.Visibility = Visibility.Visible;

        if (_lastAnnouncedLandingPad != pad || !string.Equals(_lastAnnouncedLandingStation, station, StringComparison.OrdinalIgnoreCase))
        {
            _lastAnnouncedLandingPad = pad;
            _lastAnnouncedLandingStation = station;
            _alertSpeechService.SpeakCustomText(guidance);
        }
    }

    /// <summary>
    /// Updates live Fleet Carrier jump countdown (with voice alerts at 10m, 3m lockdown, and 1m)
    /// and estimates financial upkeep runway in weeks and months.
    /// </summary>
    private void UpdateCarrierCountdownAndRunway(ShipState s)
    {
        var flight = FlightTranslations.ForLanguage(_uiLanguage);
        var culture = UiCulture;

        if (TxtCarrierUpkeepRunway is not null)
        {
            if (s.Carrier?.Balance is long bal && bal > 0)
            {
                // Base Fleet Carrier upkeep is 5,000,000 CR/week (core hull); typical equipped carriers range 10-25M/week.
                const double baseWeeklyUpkeep = 5_000_000.0;
                const double typicalWeeklyUpkeep = 15_000_000.0;
                double weeksBase = bal / baseWeeklyUpkeep;
                double weeksTypical = bal / typicalWeeklyUpkeep;
                double monthsTypical = weeksTypical / 4.345;
                TxtCarrierUpkeepRunway.Text = string.Format(
                    culture,
                    flight["carrierUpkeepRunway"],
                    Math.Floor(weeksBase).ToString("N0", culture),
                    Math.Floor(weeksTypical).ToString("N0", culture),
                    monthsTypical.ToString("0.0", culture));
            }
            else
            {
                TxtCarrierUpkeepRunway.Text = "";
            }
        }

        if (TxtCarrierJumpCountdown is null)
            return;

        if (s.Carrier?.DepartureTime is DateTimeOffset dep)
        {
            if (_lastCarrierDepartureTime != dep)
            {
                _lastCarrierDepartureTime = dep;
                _lastCarrierCountdownStage = 0;
            }

            TimeSpan rem = dep - DateTimeOffset.UtcNow;
            if (rem > TimeSpan.Zero)
            {
                TxtCarrierJumpCountdown.Text = $"⏱ T-{(int)rem.TotalMinutes:D2}:{rem.Seconds:D2}";
                TxtCarrierJumpCountdown.Visibility = Visibility.Visible;

                int stage = rem.TotalMinutes <= 1.0 ? 3 : rem.TotalMinutes <= 3.3 ? 2 : rem.TotalMinutes <= 10.0 ? 1 : 0;
                if (stage > _lastCarrierCountdownStage)
                {
                    _lastCarrierCountdownStage = stage;
                    string key = stage == 3 ? "carrierJumpVoice1m" : stage == 2 ? "carrierJumpVoiceLockdown" : "carrierJumpVoice10m";
                    _alertSpeechService.SpeakCustomText(flight[key]);
                }
                return;
            }
        }

        TxtCarrierJumpCountdown.Visibility = Visibility.Collapsed;
    }

    private ExpeditionStatsSnapshot? _lastExpeditionStats;

    /// <summary>
    /// Updates the contextual Mini-HUD banner with the highest-priority live telemetry strip
    /// (docking pad, combat target, exobiology/surface heading, or active navigation route).
    /// </summary>
    private void UpdateMiniHudContextUi(ShipState s)
    {
        if (_overlayWindow is not null && _overlayWindow.IsVisible)
            _overlayWindow.UpdateTelemetry(s, _uiLanguage);
        RefreshDesktopDeckLiveStatesIfVisible(s);

        if (MiniHudContextBanner is null || TxtMiniHudContext is null)
            return;

        if (!_isMiniHud)
        {
            MiniHudContextBanner.Visibility = Visibility.Collapsed;
            return;
        }

        string line = "";
        if (s.AssignedLandingPad is int pad && pad > 0)
        {
            var info = LandingPadLocatorService.Resolve(pad, s.AssignedLandingStationType);
            line = $"🛬 {info.FormatGuidance(_uiLanguage)}";
        }
        else if (s.CurrentTarget is { } target && (s.IsInDanger || s.HardpointsDeployed))
        {
            string shield = target.ShieldHealth.HasValue ? $"{target.ShieldHealth.Value:0}%" : "---";
            string hull = target.HullHealth.HasValue ? $"{target.HullHealth.Value:0}%" : "---";
            line = $"🎯 {target.PilotName} ({target.Ship}) · SHD {shield} · HULL {hull}";
        }
        else if (s.ExobiologyTracker is not null)
        {
            var exo = s.ExobiologyTracker;
            line = $"🧬 {exo.Species} ({exo.SamplesCollected}/3 · {exo.CurrentDistanceMeters:0}/{exo.RequiredDistanceMeters} m) · HDG {s.Heading:000}°";
        }
        else if (s.HasLatLong)
        {
            line = $"🧭 {s.BodyName} · LAT {s.Latitude:0.000}° · LON {s.Longitude:0.000}° · ALT {s.Altitude:0} m · HDG {s.Heading:000}°";
        }
        else if (s.NavRoute.Count > 1)
        {
            var next = s.NavRoute.FirstOrDefault(static w => !w.IsCurrentSystem) ?? s.NavRoute[1];
            line = $"🧭 Next: {next.StarSystem} ({next.StarClass}) · {next.StepDistanceLy:0.0} LY · {s.NavRoute.Count - 1} jumps";
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            MiniHudContextBanner.Visibility = Visibility.Collapsed;
        }
        else
        {
            TxtMiniHudContext.Text = line;
            MiniHudContextBanner.Visibility = Visibility.Visible;
        }
    }

    private async void BtnAnalyzeExpeditionHistory_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Analyze Expedition History", async () =>
        {
            if (BtnAnalyzeExpeditionHistory is null || TxtExpeditionSummary is null)
                return;

            BtnAnalyzeExpeditionHistory.IsEnabled = false;
            try
            {
                string folder = _configService.Current.JournalFolderPath;
                var stats = await ExpeditionAnalyticsService.AnalyzeFolderAsync(folder);
                _lastExpeditionStats = stats;

                var ops = OperationsTranslations.ForLanguage(_uiLanguage);
                var culture = UiCulture;
                TxtExpeditionSummary.Text = string.Format(
                    culture,
                    ops["expeditionSummary"],
                    stats.JournalsScanned,
                    stats.TotalJumps.ToString("N0", culture),
                    stats.TotalDistanceLy.ToString("N1", culture),
                    stats.UniqueSystems.ToString("N0", culture),
                    stats.EarthLikeWorlds,
                    stats.WaterWorlds,
                    stats.AmmoniaWorlds,
                    stats.OrganicsAnalysed,
                    stats.MissionsCompleted,
                    stats.TotalBountiesAndSalesCredits.ToString("N0", culture));
                TxtExpeditionSummary.Visibility = Visibility.Visible;
                if (BtnExportExpeditionMarkdown is not null)
                    BtnExportExpeditionMarkdown.IsEnabled = stats.JournalsScanned > 0;
            }
            finally
            {
                BtnAnalyzeExpeditionHistory.IsEnabled = true;
            }
        });

    private void BtnExportExpeditionMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (_lastExpeditionStats is null)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md",
            FileName = $"HUDnopy-Expedition-{DateTime.Now:yyyyMMdd}.md"
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            string md = _lastExpeditionStats.ToMarkdownReport(_fileWatcher.State.CommanderName);
            File.WriteAllText(dialog.FileName, md, System.Text.Encoding.UTF8);
            TxtStatusBarLeft.Text = string.Format(OperationsTranslations.ForLanguage(_uiLanguage)["exportSuccess"], Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("Expedition.Export", $"Failed to export expedition Markdown report: {ex.Message}");
            SetStatusBarMessage($"⚠ {ex.Message}");
        }
    }

    private void OpenExternalLoadoutUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            TxtStatusBarLeft.Text = FlightTranslations.ForLanguage(_uiLanguage)["loadoutExportUnavailable"];
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("Loadout.Export", $"Failed to open external loadout URL: {ex.Message}");
            SetStatusBarMessage($"⚠ {ex.Message}");
        }
    }

}
