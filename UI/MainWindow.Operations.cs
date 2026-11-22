// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/MainWindow.Operations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: UI binding and filtering for cargo, materials, markets, mining, combat, and control deck.
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
/// UI binding and filtering for cargo, materials, markets, mining, combat, and control deck.
/// </summary>
public partial class MainWindow
{
    private void RefreshBindingsUi()
    {
        UpdateBindingsProfileInfo();
        if (NavControls.IsChecked == true)
            BuildBindingCategoryNavigation();
        else
            FilterBindingsDeck();
    }

    private void FilterBindingsDeck()
    {
        string q = TxtSearchBinds.Text?.Trim() ?? "";
        var list = _bindingsService.Bindings.Select(binding => DeckTranslations.Present(binding, _uiLanguage)).ToList();
        if (!string.IsNullOrEmpty(_selectedBindingCategory))
        {
            list = list.Where(b => string.Equals(
                b.CategoryId, _selectedBindingCategory, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (ChkHideUnassignedBindings.IsChecked == true)
        {
            list = list.Where(b => b.IsAssigned).ToList();
        }
        if (!string.IsNullOrEmpty(q))
        {
            list = list.Where(b =>
                b.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                b.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                b.FormattedBind.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                b.SourceDisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                b.SourceCategory.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                b.SourceFormattedBind.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                b.ActionId.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        ItemsControlDeck.ItemsSource = list;
        ApplyDesktopFontScale(_desktopFontScale, ItemsControlDeck);
    }

    private async void ChkHideUnassignedBindings_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Hide Unassigned Bindings", async () =>
        {
            if (NavControls.IsChecked == true)
                BuildBindingCategoryNavigation();
            else
                FilterBindingsDeck();
            _configService.Current.HideUnassignedBindings = ChkHideUnassignedBindings.IsChecked == true;
            _configService.Save();
            await _webServer.BroadcastStateAsync();
        });


    private List<BackpackItem>? _lastShipLockerSource;
    private string _lastShipLockerQuery = "";
    private string _lastShipLockerLang = "";

    private void UpdateShipLockerUi(ShipState s)
    {
        if (GridShipLocker is null || TxtShipLockerStatus is null)
            return;

        string query = TxtShipLockerSearch?.Text.Trim() ?? "";
        if (ReferenceEquals(_lastShipLockerSource, s.ShipLockerInventory) &&
            string.Equals(_lastShipLockerQuery, query, StringComparison.Ordinal) &&
            string.Equals(_lastShipLockerLang, _uiLanguage, StringComparison.Ordinal))
        {
            return;
        }

        _lastShipLockerSource = s.ShipLockerInventory;
        _lastShipLockerQuery = query;
        _lastShipLockerLang = _uiLanguage;

        var text = OperationsTranslations.ForLanguage(_uiLanguage);
        var items = string.IsNullOrEmpty(query)
            ? s.ShipLockerInventory
            : s.ShipLockerInventory.Where(item =>
                item.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.Type.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();

        GridShipLocker.ItemsSource = items;
        TxtShipLockerStatus.Text = s.ShipLockerInventory.Count == 0
            ? text["shipLockerWaiting"]
            : string.Format(UiCulture, text["shipLockerCount"], s.ShipLockerInventory.Count);
    }

    private void TxtShipLockerSearch_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateShipLockerUi(_fileWatcher.State);

    private void UpdateEngineersUi(ShipState s)
    {
        if (GridEngineers is null || TxtEngineersStatus is null)
            return;

        var text = OperationsTranslations.ForLanguage(_uiLanguage);
        if (!ReferenceEquals(GridEngineers.ItemsSource, s.Engineers))
            GridEngineers.ItemsSource = s.Engineers;
        TxtEngineersStatus.Text = s.Engineers.Count == 0
            ? text["engineersWaiting"]
            : string.Format(UiCulture, text["engineersCount"], s.Engineers.Count);
    }

    private void UpdateMiningUi(ShipState s)
    {
        if (TxtProspectorSummary is null || TxtMiningSessionStats is null || GridMiningRefined is null)
            return;

        var text = OperationsTranslations.ForLanguage(_uiLanguage);
        var culture = UiCulture;
        TxtProspectorSummary.Text = s.LastProspectedAsteroid is { } asteroid
            ? $"{asteroid.Timestamp} · {asteroid.SummaryDisplay} ({asteroid.Remaining.ToString("0.#", culture)}%)"
            : text["prospectorWaiting"];

        var session = s.MiningSession;
        int nonLimpetCargo = Math.Max(0, s.CargoUsed - s.LimpetCount);
        int cargoFillPct = s.CargoCapacity > 0
            ? (int)Math.Clamp(Math.Round(nonLimpetCargo * 100.0 / s.CargoCapacity), 0, 100)
            : 0;
        TxtMiningSessionStats.Text = string.Format(
            culture,
            text["miningSessionSummary"],
            session.TotalRefinedTons,
            session.TonsPerHour.ToString("0.0", culture),
            session.AsteroidsProspected,
            cargoFillPct);

        if (!ReferenceEquals(GridMiningRefined.ItemsSource, session.RefinedByMineral))
            GridMiningRefined.ItemsSource = session.RefinedByMineral;
    }

    private void BtnSaveMiningFilter_Click(object sender, RoutedEventArgs e)
    {
        _configService.Current.MiningTargetMinerals = TxtMiningTargets.Text.Trim();
        if (int.TryParse(TxtMiningMinPercent.Text.Trim(), out int pct))
            _configService.Current.MiningMinPercent = Math.Clamp(pct, 1, 100);
        TxtMiningMinPercent.Text = _configService.Current.MiningMinPercent.ToString();
        _configService.Save();
        TxtStatusBarLeft.Text = OperationsTranslations.ForLanguage(_uiLanguage)["miningFilterSaved"];
    }

    private void BtnResetMiningSession_Click(object sender, RoutedEventArgs e)
    {
        _fileWatcher.ResetMiningSession();
    }

    private void UpdateTargetAndCombatUi(ShipState s)
    {
        var flight = FlightTranslations.ForLanguage(_uiLanguage);
        var ops = OperationsTranslations.ForLanguage(_uiLanguage);
        var culture = UiCulture;

        if (TargetShipCard is not null && TxtTargetHeader is not null && TxtTargetStatus is not null && TxtTargetVitals is not null)
        {
            if (s.CurrentTarget is { } t && !string.IsNullOrWhiteSpace(t.Ship))
            {
                TargetShipCard.Visibility = Visibility.Visible;
                string rankPart = string.IsNullOrWhiteSpace(t.PilotRank) ? "" : $" [{t.PilotRank}]";
                string pilotPart = string.IsNullOrWhiteSpace(t.PilotName) ? "" : $" · {t.PilotName}{rankPart}";
                TxtTargetHeader.Text = $"{t.Ship}{pilotPart}";

                string legalBadge = t.IsWanted
                    ? flight["targetWanted"]
                    : string.IsNullOrWhiteSpace(t.LegalStatus) ? flight["targetClean"] : t.LegalStatus;
                string bountyPart = t.Bounty > 0
                    ? $" · {string.Format(culture, flight["targetBounty"], t.Bounty)}"
                    : "";
                string factionPart = string.IsNullOrWhiteSpace(t.Faction) ? "" : $" · {t.Faction}";
                TxtTargetStatus.Text = $"{legalBadge}{bountyPart}{factionPart}";
                TxtTargetStatus.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    t.IsWanted ? "HudDangerBrush" : "HudTextBrush");

                string shText = t.ShieldHealth.HasValue ? $"{t.ShieldHealth.Value.ToString("0.0", culture)}%" : "—";
                string huText = t.HullHealth.HasValue ? $"{t.HullHealth.Value.ToString("0.0", culture)}%" : "—";
                string vitals = string.Format(culture, flight["targetShieldHull"], shText, huText);
                if (!string.IsNullOrWhiteSpace(t.Subsystem))
                {
                    string subHealth = t.SubsystemHealth.HasValue ? $"{t.SubsystemHealth.Value.ToString("0.0", culture)}%" : "—";
                    vitals += $" · {string.Format(culture, flight["targetSubsystem"], t.Subsystem, subHealth)}";
                }
                TxtTargetVitals.Text = vitals;
            }
            else
            {
                TargetShipCard.Visibility = Visibility.Collapsed;
            }
        }

        if (TxtCombatSessionStats is not null && GridCombatFactions is not null)
        {
            var combat = s.CombatSession;
            TxtCombatSessionStats.Text = string.Format(
                culture,
                ops["combatSessionSummary"],
                combat.TotalKills,
                combat.PendingCredits,
                combat.CreditsPerHour);
            if (!ReferenceEquals(GridCombatFactions.ItemsSource, combat.ByFaction))
                GridCombatFactions.ItemsSource = combat.ByFaction;
        }

        if (TxtMassacreSummary is not null && GridMassacreStacks is not null)
        {
            if (s.MassacreStacks.Count == 0)
            {
                TxtMassacreSummary.Text = ops["massacreEmpty"];
            }
            else
            {
                int totalMissions = s.MassacreStacks.Sum(static m => m.MissionCount);
                int effectiveKills = s.MassacreStacks.GroupBy(static m => m.TargetFaction, StringComparer.OrdinalIgnoreCase)
                    .Sum(static g => g.Max(static x => x.RequiredKills));
                long totalReward = s.MassacreStacks.Sum(static m => m.TotalReward);
                TxtMassacreSummary.Text = string.Format(
                    culture,
                    ops["massacreSummary"],
                    totalMissions,
                    effectiveKills,
                    totalReward);
            }

            if (!ReferenceEquals(GridMassacreStacks.ItemsSource, s.MassacreStacks))
                GridMassacreStacks.ItemsSource = s.MassacreStacks;
        }
    }

    private void BtnResetCombatSession_Click(object sender, RoutedEventArgs e)
    {
        _fileWatcher.ResetCombatSession();
    }

    private void ChkAutoContextDeck_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        _configService.Current.AutoContextDeck = ChkAutoContextDeck.IsChecked == true;
        _configService.Save();
        _lastAutoDeckCategory = null;
        if (_configService.Current.AutoContextDeck)
            ApplyAutoContextDeck(_fileWatcher.State);
    }

    private void ApplyAutoContextDeck(ShipState s)
    {
        if (!_configService.Current.AutoContextDeck || ViewControls?.Visibility != Visibility.Visible)
            return;

        string targetCategory;
        if (s.OnFoot)
            targetCategory = "A Pie (Odyssey)";
        else if (s.InSRV)
            targetCategory = "Vehículo SRV";
        else if (s.HardpointsDeployed)
            targetCategory = "Combate y Defensas";
        else
            targetCategory = "Vuelo y Navegación";

        if (string.Equals(_lastAutoDeckCategory, targetCategory, StringComparison.OrdinalIgnoreCase))
            return;

        _lastAutoDeckCategory = targetCategory;
        if (!string.Equals(_selectedBindingCategory, targetCategory, StringComparison.OrdinalIgnoreCase))
        {
            _selectedBindingCategory = targetCategory;
            BuildBindingCategoryNavigation();
        }
    }

    private void UpdateSurfaceNavigationUi(ShipState s)
    {
        if (TxtSurfaceLiveCoords is null || TxtSurfaceGuidance is null || GridSurfaceEvents is null)
            return;

        var flight = FlightTranslations.ForLanguage(_uiLanguage);
        var culture = UiCulture;
        TxtSurfaceLiveCoords.Text = s.HasLatLong
            ? $"{(string.IsNullOrWhiteSpace(s.BodyName) ? s.CurrentSystem : s.BodyName)} · LAT: {s.Latitude.ToString("0.0000", culture)}° · LON: {s.Longitude.ToString("0.0000", culture)}° · ALT: {s.Altitude.ToString("0.0", culture)} m · {flight["heading"]}: {s.Heading.ToString("0", culture)}°"
            : flight["noSurface"];

        if (!ReferenceEquals(GridSurfaceEvents.ItemsSource, s.RecentSurfaceEvents))
            GridSurfaceEvents.ItemsSource = s.RecentSurfaceEvents;

        UpdateSurfaceGuidance(s);
    }

    private void UpdateSurfaceGuidance(ShipState s)
    {
        if (TxtSurfaceGuidance is null || TxtTargetLat is null || TxtTargetLon is null)
            return;

        var flight = FlightTranslations.ForLanguage(_uiLanguage);
        var culture = UiCulture;
        if (!double.TryParse(TxtTargetLat.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double targetLat) ||
            !double.TryParse(TxtTargetLon.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double targetLon))
        {
            if (TxtSurfaceCompassTape is not null)
                TxtSurfaceCompassTape.Visibility = Visibility.Collapsed;
            TxtSurfaceGuidance.Text = flight["surfaceGuidancePrompt"];
            return;
        }

        if (!s.HasLatLong)
        {
            if (TxtSurfaceCompassTape is not null)
                TxtSurfaceCompassTape.Visibility = Visibility.Collapsed;
            TxtSurfaceGuidance.Text = flight["noSurface"];
            return;
        }

        var solution = SurfaceNavigationService.Compute(s.Latitude, s.Longitude, s.Heading, targetLat, targetLon, s.PlanetRadius);
        if (TxtSurfaceCompassTape is not null)
        {
            TxtSurfaceCompassTape.Text = $"{solution.SteeringArrow}  {solution.CompassTape}";
            TxtSurfaceCompassTape.Visibility = Visibility.Visible;
        }

        string distDisplay = SurfaceNavigationService.FormatDistance(solution.DistanceMeters);
        TxtSurfaceGuidance.Text = $"{solution.SteeringArrow} " + string.Format(culture, flight["surfaceGuidanceResult"], solution.BearingDegrees.ToString("0", culture), distDisplay);
    }

    private void BtnTargetLastExoSample_Click(object sender, RoutedEventArgs e)
    {
        var tracker = _fileWatcher.State.ExobiologyTracker;
        if (tracker?.LastSampleLatitude is double lat && tracker.LastSampleLongitude is double lon)
        {
            TxtTargetLat.Text = lat.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
            TxtTargetLon.Text = lon.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
            UpdateSurfaceGuidance(_fileWatcher.State);
            return;
        }
        TxtStatusBarLeft.Text = FlightTranslations.ForLanguage(_uiLanguage)["noExoSampleCoords"];
    }

    /// <summary>
    /// Displays active missions grouped by destination star system and warns about missing delivery cargo.
    /// </summary>
    private void UpdateMissionsGroupedSummary(ShipState s)
    {
        if (TxtMissionsGroupedSummary is null || TxtMissionsCargoWarning is null)
            return;

        if (s.ActiveMissions.Count == 0)
        {
            TxtMissionsGroupedSummary.Visibility = Visibility.Collapsed;
            TxtMissionsCargoWarning.Visibility = Visibility.Collapsed;
            return;
        }

        var ops = OperationsTranslations.ForLanguage(_uiLanguage);
        var culture = UiCulture;

        var groups = s.ActiveMissions
            .Where(static m => !string.IsNullOrWhiteSpace(m.DestinationSystem))
            .GroupBy(static m => m.DestinationSystem, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static g => g.Count())
            .ThenByDescending(static g => g.Sum(static m => m.Reward ?? 0))
            .Take(5)
            .Select(g =>
            {
                long totalCr = g.Sum(static m => m.Reward ?? 0);
                int tons = g.Where(static m => !string.IsNullOrWhiteSpace(m.Commodity) && m.Count.HasValue).Sum(static m => m.Count!.Value);
                string crText = totalCr >= 1_000_000 ? $"{(totalCr / 1_000_000.0).ToString("0.0", culture)}M CR" : $"{totalCr.ToString("N0", culture)} CR";
                return tons > 0 ? $"{g.Key} ({g.Count()} · {crText} · {tons} t)" : $"{g.Key} ({g.Count()} · {crText})";
            })
            .ToList();

        if (groups.Count > 0)
        {
            TxtMissionsGroupedSummary.Text = string.Format(ops["missionsBySystem"], string.Join("  |  ", groups));
            TxtMissionsGroupedSummary.Visibility = Visibility.Visible;
        }
        else
        {
            TxtMissionsGroupedSummary.Visibility = Visibility.Collapsed;
        }

        // Check if active commodity haulage missions require more tons than currently carried.
        var requiredByCommodity = s.ActiveMissions
            .Where(static m => !string.IsNullOrWhiteSpace(m.Commodity) && m.Count is > 0 && m.PassengerMission != true)
            .GroupBy(static m => m.Commodity.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Required: g.Sum(static m => m.Count!.Value)))
            .ToList();

        var missing = new List<string>();
        foreach (var req in requiredByCommodity)
        {
            int carried = s.CargoInventory
                .Where(c => string.Equals(c.Name?.Trim(), req.Name, StringComparison.OrdinalIgnoreCase))
                .Sum(static c => c.Count);
            if (carried < req.Required)
                missing.Add($"{req.Name} ({carried}/{req.Required} t)");
        }

        if (missing.Count > 0)
        {
            TxtMissionsCargoWarning.Text = string.Format(ops["missionsMissingCargo"], string.Join(", ", missing.Take(4)));
            TxtMissionsCargoWarning.Visibility = Visibility.Visible;
        }
        else
        {
            TxtMissionsCargoWarning.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Cross-references carried cargo against the current station market, colonisation needs, and Fleet Carrier cargo.
    /// </summary>
    private void UpdateCargoTradeProfitUi(ShipState s)
    {
        if (TxtCargoTradeProfitSummary is null)
            return;

        if (s.CargoInventory.Count == 0)
        {
            TxtCargoTradeProfitSummary.Visibility = Visibility.Collapsed;
            return;
        }

        var ops = OperationsTranslations.ForLanguage(_uiLanguage);
        var culture = UiCulture;
        var notes = new List<string>();

        if (s.Market?.Items is { Count: > 0 })
        {
            long totalSell = 0;
            long totalEstProfit = 0;
            var bestItems = new List<(string Name, long Profit)>();

            foreach (var cargo in s.CargoInventory)
            {
                if (cargo.Count <= 0 || string.IsNullOrWhiteSpace(cargo.Name))
                    continue;
                var mkt = s.Market.Items.FirstOrDefault(m =>
                    string.Equals(m.DisplayName, cargo.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(m.Name, cargo.Name, StringComparison.OrdinalIgnoreCase));
                if (mkt is null || mkt.SellPrice <= 0)
                    continue;

                long sellVal = mkt.SellPrice * cargo.Count;
                totalSell += sellVal;
                long unitProfit = mkt.MeanPrice > 0 ? mkt.SellPrice - mkt.MeanPrice : 0;
                if (unitProfit > 0)
                {
                    long p = unitProfit * cargo.Count;
                    totalEstProfit += p;
                    bestItems.Add((cargo.Name, p));
                }
            }

            if (totalSell > 0)
            {
                string bestStr = bestItems.Count > 0
                    ? " · " + string.Join(", ", bestItems.OrderByDescending(static x => x.Profit).Take(3).Select(x => $"{x.Name} (+{x.Profit.ToString("N0", culture)} CR)"))
                    : "";
                notes.Add(string.Format(culture, ops["cargoMarketSummary"], totalSell.ToString("N0", culture), totalEstProfit.ToString("N0", culture), bestStr));
            }
        }

        if (s.Colonisation?.Resources is { Count: > 0 })
        {
            var colMatches = s.CargoInventory
                .Where(c => s.Colonisation.Resources.Any(r =>
                    r.RequiredAmount > r.ProvidedAmount &&
                    string.Equals(r.DisplayName, c.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(c => $"{c.Name} ({c.Count} t)")
                .ToList();
            if (colMatches.Count > 0)
                notes.Add(string.Format(ops["cargoColonisationMatches"], string.Join(", ", colMatches)));
        }

        if (notes.Count > 0)
        {
            TxtCargoTradeProfitSummary.Text = string.Join("  |  ", notes);
            TxtCargoTradeProfitSummary.Visibility = Visibility.Visible;
        }
        else
        {
            TxtCargoTradeProfitSummary.Visibility = Visibility.Collapsed;
        }
    }

    private string? _lastWishlistSignature;

    /// <summary>
    /// Refreshes the Engineering Blueprint Wishlist grid against live ship and Odyssey material inventories.
    /// </summary>
    private void UpdateEngineeringWishlistUi(ShipState s)
    {
        if (GridEngineeringWishlist is null || TxtEngineeringWishlistSummary is null)
            return;

        var selected = _configService.Current.EngineeringWishlistBlueprints ?? new List<string>();
        var rows = EngineeringWishlistService.Evaluate(selected, s);
        string signature = $"{_uiLanguage}|{string.Join(",", selected)}|{string.Join(";", rows.Select(static r => $"{r.MaterialName}:{r.Owned}/{r.Required}"))}";
        if (string.Equals(_lastWishlistSignature, signature, StringComparison.Ordinal))
            return;

        _lastWishlistSignature = signature;
        var ops = OperationsTranslations.ForLanguage(_uiLanguage);
        GridEngineeringWishlist.ItemsSource = rows;

        if (selected.Count == 0)
        {
            TxtEngineeringWishlistSummary.Text = ops["wishlistEmpty"];
            return;
        }

        int ready = rows.Count(static r => r.Missing == 0);
        int missing = rows.Count - ready;
        TxtEngineeringWishlistSummary.Text = string.Format(ops["wishlistProgress"], selected.Count, ready, rows.Count, missing);
    }

    private void BtnAddBlueprintToWishlist_Click(object sender, RoutedEventArgs e)
    {
        if (CmbEngineeringBlueprint?.SelectedValue is not string bpId || string.IsNullOrWhiteSpace(bpId))
            return;
        _configService.Current.EngineeringWishlistBlueprints ??= new List<string>();
        _configService.Current.EngineeringWishlistBlueprints.Add(bpId);
        _configService.Save();
        _lastWishlistSignature = null;
        UpdateEngineeringWishlistUi(_fileWatcher.State);
    }

    private void BtnClearWishlist_Click(object sender, RoutedEventArgs e)
    {
        _configService.Current.EngineeringWishlistBlueprints?.Clear();
        _configService.Save();
        _lastWishlistSignature = null;
        UpdateEngineeringWishlistUi(_fileWatcher.State);
    }

    private async void BtnWishlistFindTrader_Click(object sender, RoutedEventArgs e)
    {
        var s = _fileWatcher.State;
        if (s.StarPos is not { Length: 3 })
        {
            TxtStatusBarLeft.Text = FlightTranslations.ForLanguage(_uiLanguage)["spanshServicesEmpty"];
            return;
        }

        BtnWishlistFindTrader.IsEnabled = false;
        try
        {
            var (_, results, _) = await _spanshSystemDataService.SearchNearestServicesAsync("material_trader", s.CurrentSystem, s.StarPos);
            if (results.Count > 0)
            {
                var best = results[0];
                CopySystemToClipboard(best.SystemName);
                TxtStatusBarLeft.Text = $"Material Trader: {best.SystemName} / {best.StationName} ({best.DistanceDisplay})";
            }
            else
            {
                TxtStatusBarLeft.Text = FlightTranslations.ForLanguage(_uiLanguage)["spanshServicesEmpty"];
            }
        }
        finally
        {
            BtnWishlistFindTrader.IsEnabled = true;
        }
    }

    private void SurfaceTarget_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateSurfaceGuidance(_fileWatcher.State);

    private void RefreshSurfaceBookmarksUi()
    {
        if (GridSurfaceBookmarks is null)
            return;
        GridSurfaceBookmarks.ItemsSource = null;
        GridSurfaceBookmarks.ItemsSource = _configService.Current.SurfaceBookmarks;
    }

    private void BtnSaveSurfaceBookmark_Click(object sender, RoutedEventArgs e)
    {
        var s = _fileWatcher.State;
        if (!s.HasLatLong)
            return;

        string label = TxtBookmarkLabel.Text.Trim();
        if (string.IsNullOrWhiteSpace(label))
            label = string.IsNullOrWhiteSpace(s.BodyName) ? s.CurrentSystem : s.BodyName;

        _configService.Current.SurfaceBookmarks ??= new List<SurfaceBookmarkItem>();
        _configService.Current.SurfaceBookmarks.Insert(0, new SurfaceBookmarkItem
        {
            SystemName = s.CurrentSystem,
            BodyName = s.BodyName,
            Label = label,
            Latitude = s.Latitude,
            Longitude = s.Longitude,
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
        });
        _configService.Save();
        TxtBookmarkLabel.Text = "";
        RefreshSurfaceBookmarksUi();
    }

    private void BtnNavigateBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (GridSurfaceBookmarks.SelectedItem is not SurfaceBookmarkItem bookmark)
            return;

        TxtTargetLat.Text = bookmark.Latitude.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
        TxtTargetLon.Text = bookmark.Longitude.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
        UpdateSurfaceGuidance(_fileWatcher.State);
    }

    private void BtnDeleteSurfaceBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (GridSurfaceBookmarks.SelectedItem is not SurfaceBookmarkItem bookmark)
            return;

        _configService.Current.SurfaceBookmarks?.Remove(bookmark);
        _configService.Save();
        RefreshSurfaceBookmarksUi();
    }

    private void UpdateCopilotTacticalUi(
        ShipState s,
        IReadOnlyDictionary<string, string> flight,
        System.Globalization.CultureInfo culture)
    {
        var items = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.JumpTargetSystem))
        {
            string starNote = s.JumpTargetIsHazardous
                ? flight["copilotJumpHazard"]
                : s.JumpTargetIsScoopable
                    ? flight["copilotJumpScoopable"]
                    : flight["copilotJumpNonScoopable"];
            items.Add(string.Format(culture, flight["copilotJumpTarget"], s.JumpTargetSystem, s.JumpTargetStarClass, starNote));
        }

        if (s.ApproachBodyGravityG is >= 1.5)
            items.Add(string.Format(culture, flight["copilotHighGravity"], s.ApproachBodyGravityG.Value.ToString("0.0", culture)));

        if (s.HasLimpetController)
        {
            items.Add(s.LimpetCount == 0
                ? flight["copilotNoLimpets"]
                : string.Format(culture, flight["copilotLimpets"], s.LimpetCount));
        }

        if (s.FsdHealthPercent is double fsd)
        {
            if (fsd < 80)
                items.Add(string.Format(culture, flight["copilotFsdWear"], Math.Round(fsd)));
            else if (s.IsFsdSupercharged)
                items.Add(string.Format(culture, flight["copilotFsdSupercharged"], Math.Round(fsd)));
        }

        if (s.SupercruiseOverdriveActive)
            items.Add(flight["copilotScoActive"]);

        if (!s.Docked && !s.Landed && !s.Supercruise && s.HasStatusTelemetry)
        {
            var blockers = new List<string>();
            if (s.LandingGearDown) blockers.Add(flight["gear"]);
            if (s.CargoScoopDeployed) blockers.Add(flight["scoop"]);
            if (s.HardpointsDeployed) blockers.Add(flight["hardpoints"]);
            if (blockers.Count > 0)
                items.Add(string.Format(culture, flight["copilotBlockers"], string.Join(", ", blockers)));
        }

        TxtCopilotTacticalStatus.Text = items.Count == 0
            ? flight["copilotNominal"]
            : string.Join("  ·  ", items);
    }

    private static readonly SolidColorBrush CockpitAlertCardBackgroundBrush = CreateFrozenBrush(System.Windows.Media.Color.FromArgb(65, 255, 46, 76));
    private static readonly SolidColorBrush CockpitInactiveCardBorderBrush = CreateFrozenBrush(System.Windows.Media.Color.FromArgb(45, 255, 255, 255));

    private static SolidColorBrush CreateFrozenBrush(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void RenderCockpitSystemCards(ShipState s)
    {
        var accentBrush = (System.Windows.Media.Brush)FindResource("HudAccentBrush");
        var dimBrush = (System.Windows.Media.Brush)FindResource("HudAccentDimBrush");
        var dangerBrush = (System.Windows.Media.Brush)FindResource("HudDangerBrush");
        var mutedBrush = (System.Windows.Media.Brush)FindResource("HudMutedBrush");
        var panelAltBrush = (System.Windows.Media.Brush)FindResource("HudPanelAltBrush");
        var key = (s.RawFlags, _desktopFontScale, accentBrush, dimBrush, dangerBrush, mutedBrush, panelAltBrush);
        if (_cockpitCardsKey == key) return;
        _cockpitCardsKey = key;
        var flight = FlightTranslations.ForLanguage(_uiLanguage);

        var items = new (string Title, string Icon, bool Active, bool IsAlert)[]
        {
            (flight["gear"], "🛬", s.LandingGearDown, false),
            (flight["lights"], "💡", s.LightsOn, false),
            (flight["nightVision"], "👁", s.NightVision, false),
            (flight["scoop"], "📦", s.CargoScoopDeployed, false),
            (flight["hardpoints"], "⚔", s.HardpointsDeployed, false),
            (flight["supercruiseStatus"], "🚀", s.Supercruise, false),
            (flight["flightAssist"], "🔄", s.FlightAssistOff, true),
            (flight["analysisMode"], "🎛", s.AnalysisMode, false),
            (flight["silentRunning"], "🔇", s.SilentRunning, true),
            (flight["shieldsOnline"], "🛡", s.ShieldsUp, false),
            (flight["massLock"], "⚓", s.FsdMassLocked, true),
            (flight["overheating"], "🔥", s.OverHeating, true)
        };

        CockpitSystemsGrid.Children.Clear();

        foreach (var item in items)
        {
            var card = new Border
            {
                Margin = new Thickness(5),
                Padding = new Thickness(10, 12, 10, 12),
                BorderThickness = new Thickness(1),
                Background = item.Active
                    ? (item.IsAlert ? CockpitAlertCardBackgroundBrush : dimBrush)
                    : panelAltBrush,
                BorderBrush = item.Active
                    ? (item.IsAlert ? dangerBrush : accentBrush)
                    : CockpitInactiveCardBorderBrush
            };

            var sp = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
            sp.Children.Add(new TextBlock
            {
                Text = $"{item.Icon}  {item.Title}",
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                TextAlignment = System.Windows.TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = item.Active ? (item.IsAlert ? dangerBrush : accentBrush) : System.Windows.Media.Brushes.White
            });
            sp.Children.Add(new TextBlock
            {
                Text = item.Active ? $"● {flight["active"]}" : $"○ {flight["inactive"]}",
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 5, 0, 4),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                TextAlignment = System.Windows.TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = item.Active ? (item.IsAlert ? dangerBrush : accentBrush) : mutedBrush
            });
            card.Child = sp;
            CockpitSystemsGrid.Children.Add(card);
        }

        ApplyDesktopFontScale(_desktopFontScale, CockpitSystemsGrid);
    }

    private async void BtnAction_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Deck Action", async () =>
        {
            if (sender is FrameworkElement el && el.Tag is string actionId && !string.IsNullOrEmpty(actionId))
            {
                var (success, message) = await _inputSimulator.ExecuteActionAsync(actionId);
                TxtStatusBarLeft.Text = $"[{DateTime.Now:HH:mm:ss}] {message}";
                TxtStatusBarLeft.Foreground = success
                    ? (System.Windows.Media.Brush)FindResource("HudAccentBrush")
                    : (System.Windows.Media.Brush)FindResource("HudDangerBrush");
            }
        });

    private void NavRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewDashboard == null) return;
        if (sender is System.Windows.Controls.RadioButton rb && rb.Tag is string tagStr && int.TryParse(tagStr, out int idx))
        {
            SwitchView(idx);
        }
    }

    private void SwitchView(int index)
    {
        ViewDashboard.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewCockpit.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        ViewNavigation.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        ViewOperations.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        ViewInventory.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        ViewActivity.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
        ViewControls.Visibility = index == 6 ? Visibility.Visible : Visibility.Collapsed;
        ViewConfig.Visibility = index == 7 ? Visibility.Visible : Visibility.Collapsed;

        NavDashboard.IsChecked = index == 0;
        NavCockpit.IsChecked = index == 1;
        NavNavigation.IsChecked = index == 2;
        NavOperations.IsChecked = index == 3;
        NavInventory.IsChecked = index == 4;
        NavActivity.IsChecked = index == 5;
        NavControls.IsChecked = index == 6;
        NavConfig.IsChecked = index == 7;
        BuildSectionNavigation(index);

        FrameworkElement activeView = index switch
        {
            0 => ViewDashboard,
            1 => ViewCockpit,
            2 => ViewNavigation,
            3 => ViewOperations,
            4 => ViewInventory,
            5 => ViewActivity,
            6 => ViewControls,
            7 => ViewConfig,
            _ => ViewDashboard
        };

        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                ApplyDesktopFontScale(_desktopFontScale, SectionNavigationPanel);
                ApplyDesktopFontScale(_desktopFontScale, activeView);
            }),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static readonly Dictionary<string, string> SectionIcons = new(StringComparer.Ordinal)
    {
        ["summary"] = "📊",
        ["alerts-activity"] = "🔔",
        ["status-resources"] = "🚀",
        ["ship-systems"] = "⚡",
        ["ship-modules"] = "🔧",
        ["on-foot"] = "🧑‍🚀",
        ["combat"] = "⚔️",
        ["current-system"] = "🌌",
        ["route"] = "🧭",
        ["exploration"] = "🔭",
        ["surface-nav"] = "🪐",
        ["carrier"] = "🛸",
        ["spansh-services"] = "🛰️",
        ["missions"] = "📜",
        ["passengers"] = "👥",
        ["mining"] = "⛏️",
        ["colonisation"] = "🏗️",
        ["cargo"] = "📦",
        ["materials"] = "💎",
        ["engineers"] = "⚙️",
        ["backpack"] = "🎒",
        ["market"] = "🏪",
        ["outfitting"] = "🔩",
        ["shipyard"] = "🚢",
        ["ai-copilot"] = "🤖",
        ["galnet"] = "📰",
        ["exploration-milestones"] = "🏆",
        ["journal"] = "📖",
        ["commander-profile"] = "🪪",
        ["interface"] = "🎨",
        ["paths"] = "📂",
        ["web-server"] = "🌐",
        ["integrations"] = "🔌",
        ["ai-settings"] = "🤖",
        ["voice-audio"] = "🔊",
        ["alerts"] = "🚨",
        ["diagnostics"] = "🩺"
    };

    private void BuildSectionNavigation(int viewIndex, bool preserveSelection = false)
    {
        var previousSection = preserveSelection
            ? SectionNavigationItems.Children.OfType<System.Windows.Controls.Button>()
                .Select(button => button.Tag as FrameworkElement)
                .FirstOrDefault(element => element?.Visibility == Visibility.Visible)
            : null;
        SectionNavigationItems.Children.Clear();
        if (viewIndex == 6)
        {
            BuildBindingCategoryNavigation();
            return;
        }

        var text = SectionTranslations.ForLanguage(_uiLanguage);
        (string Key, FrameworkElement Element)[] sections = viewIndex switch
        {
            0 => new[]
            {
                ("summary", (FrameworkElement)DashboardSummarySection),
                ("alerts-activity", (FrameworkElement)DashboardOverviewSection)
            },
            1 => new[]
            {
                ("status-resources", (FrameworkElement)CockpitResourcesSection),
                ("ship-systems", CockpitSystemsSection),
                ("ship-modules", CockpitModulesSection),
                ("on-foot", CockpitOnFootSection),
                ("combat", OperationsCombatSection)
            },
            2 => new[]
            {
                ("current-system", (FrameworkElement)NavigationSystemSection),
                ("route", NavigationRouteSection),
                ("exploration", NavigationBodiesSection),
                ("surface-nav", NavigationSurfaceSection),
                ("carrier", NavigationCarrierSection),
                ("spansh-services", NavigationSpanshServicesSection)
            },
            3 => new[]
            {
                ("missions", (FrameworkElement)OperationsMissionsSection),
                ("passengers", OperationsPassengersSection),
                ("mining", OperationsMiningSection),
                ("colonisation", ActivityColonisationSection)
            },
            4 => new[]
            {
                ("cargo", (FrameworkElement)OperationsCargoSection),
                ("materials", OperationsMaterialsSection),
                ("engineers", OperationsEngineersSection),
                ("backpack", OperationsBackpackSection),
                ("market", OperationsMarketSection),
                ("outfitting", OperationsOutfittingSection),
                ("shipyard", OperationsShipyardSection)
            },
            5 => new[]
            {
                ("ai-copilot", (FrameworkElement)ActivityAiSection),
                ("galnet", ActivityGalnetSection),
                ("exploration-milestones", ActivityMilestonesSection),
                ("journal", ActivityJournalSection),
                ("commander-profile", ActivityProfileSection)
            },
            7 => new[]
            {
                ("interface", (FrameworkElement)ConfigInterfaceSection),
                ("paths", ConfigPathsSection),
                ("web-server", ConfigWebSection),
                ("integrations", ConfigIntegrationsSection),
                ("ai-settings", ConfigAiSection),
                ("voice-audio", ConfigVoiceSection),
                ("alerts", ConfigAlertsSection),
                ("diagnostics", ConfigDiagnosticsSection),
            },
            _ => Array.Empty<(string, FrameworkElement)>()
        };

        SectionNavigationPanel.Visibility = sections.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        TxtActiveSectionTitle.Visibility = sections.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (sections.Length <= 1)
            return;

        bool hasPreviousSection = sections.Any(section => ReferenceEquals(section.Element, previousSection));
        for (int i = 0; i < sections.Length; i++)
        {
            var (key, element) = sections[i];
            string title = text[key];
            string icon = SectionIcons.TryGetValue(key, out string? mapped) ? mapped : "•";
            bool selected = hasPreviousSection ? ReferenceEquals(element, previousSection) : i == 0;
            element.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            if (selected)
                TxtActiveSectionTitle.Text = title.ToUpper(UiCulture);

            var button = new System.Windows.Controls.Button
            {
                Content = icon,
                ToolTip = title,
                Language = System.Windows.Markup.XmlLanguage.GetLanguage(_uiLanguage),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
                Tag = element,
                Style = (Style)FindResource("HudButtonStyle"),
                MinWidth = 36,
                Margin = new Thickness(2),
                Padding = new Thickness(10, 5, 10, 5)
            };
            System.Windows.Automation.AutomationProperties.SetName(button, title);
            button.SetResourceReference(BorderBrushProperty, selected ? "HudAccentBrush" : "HudBorderBrush");
            button.SetResourceReference(ForegroundProperty, selected ? "HudAccentBrush" : "HudMutedBrush");
            button.SetResourceReference(BackgroundProperty, selected ? "HudAccentDimBrush" : "HudPanelAltBrush");
            button.Click += SectionNavigationButton_Click;
            SectionNavigationItems.Children.Add(button);
        }
    }

    private void BuildBindingCategoryNavigation()
    {
        SectionNavigationItems.Children.Clear();
        TxtActiveSectionTitle.Visibility = Visibility.Collapsed;

        var categories = _bindingsService.Bindings
            .Where(binding => ChkHideUnassignedBindings.IsChecked != true ||
                binding.FormattedBind != "Sin asignar")
            .Select(binding => binding.Category)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        SectionNavigationPanel.Visibility = categories.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        TxtActiveSectionTitle.Visibility = categories.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (categories.Count == 0)
        {
            _selectedBindingCategory = null;
            FilterBindingsDeck();
            return;
        }

        if (_selectedBindingCategory == null ||
            !categories.Contains(_selectedBindingCategory, StringComparer.OrdinalIgnoreCase))
            _selectedBindingCategory = categories[0];

        for (int i = 0; i < categories.Count; i++)
        {
            string category = categories[i];
            string categoryLabel = DeckTranslations.Category(category, _uiLanguage);
            bool isSelected = string.Equals(
                category, _selectedBindingCategory, StringComparison.OrdinalIgnoreCase);
            if (isSelected)
                TxtActiveSectionTitle.Text = categoryLabel.ToUpper(UiCulture);

            var button = new System.Windows.Controls.Button
            {
                Content = (i + 1).ToString(),
                ToolTip = categoryLabel,
                Language = System.Windows.Markup.XmlLanguage.GetLanguage(_uiLanguage),
                Tag = category,
                Style = (Style)FindResource("HudButtonStyle"),
                MinWidth = 36,
                Margin = new Thickness(2),
                Padding = new Thickness(10, 5, 10, 5)
            };
            System.Windows.Automation.AutomationProperties.SetName(button, categoryLabel);
            button.SetResourceReference(BorderBrushProperty, isSelected ? "HudAccentBrush" : "HudBorderBrush");
            button.SetResourceReference(ForegroundProperty, isSelected ? "HudAccentBrush" : "HudMutedBrush");
            button.Click += BindingCategoryButton_Click;
            SectionNavigationItems.Children.Add(button);
        }

        FilterBindingsDeck();
    }

    private void BindingCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string category })
            return;

        _selectedBindingCategory = category;
        BuildBindingCategoryNavigation();
    }

    private void SectionNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: FrameworkElement selectedSection })
            return;

        foreach (var button in SectionNavigationItems.Children.OfType<System.Windows.Controls.Button>())
        {
            bool isSelected = ReferenceEquals(button, sender);
            ((FrameworkElement)button.Tag).Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            button.SetResourceReference(BorderBrushProperty, isSelected ? "HudAccentBrush" : "HudBorderBrush");
            button.SetResourceReference(ForegroundProperty, isSelected ? "HudAccentBrush" : "HudMutedBrush");
            button.SetResourceReference(BackgroundProperty, isSelected ? "HudAccentDimBrush" : "HudPanelAltBrush");
            if (isSelected && button.ToolTip is string title)
                TxtActiveSectionTitle.Text = title.ToUpper(UiCulture);
        }
    }

    private void TxtSearchBinds_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterBindingsDeck();
    }

    private void BtnReloadFiles_Click(object sender, RoutedEventArgs e)
    {
        _bindingsService.ReloadBindings();
        _fileWatcher.RefreshState();
        TxtStatusBarLeft.Text = $"[{DateTime.Now:HH:mm:ss}] {SettingsTranslations.ForLanguage(_uiLanguage)["reloaded"]}";
    }

    private void BtnBrowseJournalFolder_Click(object sender, RoutedEventArgs e)
    {
        string? selected = BrowseForFolder(TxtConfigJournalPath.Text, "browseJournal");
        if (selected is not null && ApplySelectedPaths(selected, _configService.Current.BindingsFolderPath,
            _configService.Current.SelectedBindsFile, reloadBindings: false))
            TxtConfigJournalPath.Text = selected;
    }

    private void BtnBrowseBindingsFolder_Click(object sender, RoutedEventArgs e)
    {
        string? selected = BrowseForFolder(TxtConfigBindingsPath.Text, "browseBindings");
        if (selected is null)
            return;
        string profile = string.Equals(selected, _configService.Current.BindingsFolderPath, StringComparison.OrdinalIgnoreCase)
            ? _configService.Current.SelectedBindsFile : "";
        if (ApplySelectedPaths(_configService.Current.JournalFolderPath, selected, profile, reloadBindings: true))
            TxtConfigBindingsPath.Text = selected;
    }

    private string? BrowseForFolder(string currentPath, string descriptionKey)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = SettingsTranslations.ForLanguage(_uiLanguage)[descriptionKey],
            SelectedPath = Directory.Exists(currentPath)
                ? currentPath
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private void BtnBrowseBindsProfile_Click(object sender, RoutedEventArgs e)
    {
        string selectedFile = TxtConfigSelectedBindsFile.Text.Trim();
        string bindsFolder = TxtConfigBindingsPath.Text.Trim();
        string initialDirectory = File.Exists(selectedFile)
            ? Path.GetDirectoryName(selectedFile)!
            : Directory.Exists(bindsFolder) ? bindsFolder : "";

        using var dialog = new Forms.OpenFileDialog
        {
            Title = SettingsTranslations.ForLanguage(_uiLanguage)["browseProfile"],
            Filter = "Elite Dangerous bindings (*.binds)|*.binds|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = initialDirectory,
            FileName = File.Exists(selectedFile) ? selectedFile : ""
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
            return;

        string? selectedDirectory = Path.GetDirectoryName(dialog.FileName);
        if (string.IsNullOrEmpty(selectedDirectory))
            return;
        if (ApplySelectedPaths(_configService.Current.JournalFolderPath, selectedDirectory, dialog.FileName, reloadBindings: false))
        {
            TxtConfigSelectedBindsFile.Text = dialog.FileName;
            TxtConfigBindingsPath.Text = selectedDirectory;
            _displayedBindsFile = null;
            _bindingsService.ReloadBindings();
        }
    }

    private bool ApplySelectedPaths(string journalFolder, string bindingsFolder, string profile, bool reloadBindings)
    {
        try
        {
            _configService.SetGamePaths(journalFolder, bindingsFolder, profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            TxtStatusBarLeft.Text = string.Format(SettingsTranslations.ForLanguage(_uiLanguage)["pathsSaveError"], ex.Message);
            return false;
        }
        if (reloadBindings)
            _bindingsService.ReloadBindings();
        TxtStatusBarLeft.Text = SettingsTranslations.ForLanguage(_uiLanguage)["pathsSaved"];
        return true;
    }

    private void BtnOpenWeb_Click(object sender, RoutedEventArgs e)
    {
        if (!_webServer.IsRunning)
        {
            var text = SettingsTranslations.ForLanguage(_uiLanguage);
            System.Windows.MessageBox.Show(
                text["webDisabledMessage"],
                text["webDisabled"],
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string url = $"https://localhost:{_webServer.CurrentPort}";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            var text = SettingsTranslations.ForLanguage(_uiLanguage);
            System.Windows.MessageBox.Show(string.Format(text["browserError"], ex.Message), text["error"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnCopyWebUrl_Click(object sender, RoutedEventArgs e)
    {
        string url = LstWebUrls.SelectedItem as string ?? $"https://localhost:{_webServer.CurrentPort}";
        if (!string.IsNullOrEmpty(url))
        {
            System.Windows.Clipboard.SetText(url);
            TxtStatusBarLeft.Text = string.Format(SettingsTranslations.ForLanguage(_uiLanguage)["urlCopied"], url);
        }
    }

    private void LstWebUrls_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        BtnCopyWebUrl_Click(sender, e);
    }

    private void BtnCopyWebSessionCode_Click(object sender, RoutedEventArgs e)
    {
        if (_webServer.SessionCode is not string code)
            return;
        try
        {
            System.Windows.Clipboard.SetText(code);
            TxtStatusBarLeft.Text = SettingsTranslations.ForLanguage(_uiLanguage)["sessionCodeCopied"];
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            TxtStatusBarLeft.Text = string.Format(SettingsTranslations.ForLanguage(_uiLanguage)["clipboardError"], ex.Message);
        }
    }

    private void BtnWebServerInfo_Click(object sender, RoutedEventArgs e)
    {
        WebServerInfoPanel.Visibility = WebServerInfoPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        string label = text[WebServerInfoPanel.Visibility == Visibility.Visible ? "hideWebInfo" : "showWebInfo"];
        BtnWebServerInfo.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(BtnWebServerInfo, label);
    }

    private async void BtnRestartWebServer_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Web Server Restart", () => ApplyWebSettingsAsync(useEditedPort: true));

    private async Task ApplyWebSettingsAsync(bool useEditedPort)
    {
        if (_applyingWebSettings)
            return;
        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        int port = _configService.Current.WebServerPort;
        if (useEditedPort && (!int.TryParse(TxtConfigPort.Text.Trim(), out port) || port <= 1024 || port >= 65535))
        {
            System.Windows.MessageBox.Show(text["invalidPortMessage"], text["invalidPort"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string? mode = CmbWebAccessMode.SelectedValue as string;
        string vpnInterfaceId = CmbWebVpnInterface.SelectedValue as string ?? "";
        if (mode is not ("local" or "open" or "code" or "vpn"))
        {
            TxtStatusBarLeft.Text = string.Format(text["webSaveError"], text["webMode"]);
            return;
        }

        _applyingWebSettings = true;
        CmbWebAccessMode.IsEnabled = false;
        CmbWebVpnInterface.IsEnabled = false;
        BtnRefreshVpnInterfaces.IsEnabled = false;
        BtnRestartWebServer.IsEnabled = false;
        ChkEnableWebServer.IsEnabled = false;
        try
        {
            if (mode == "vpn" && vpnInterfaceId.Length == 0)
            {
                await _webServer.StopAsync();
                TxtStatusBarLeft.Text = text["vpnRequired"];
                return;
            }
            try
            {
                _configService.SetWebSettings(port, mode, vpnInterfaceId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                _suppressEvents = true;
                try { CmbWebAccessMode.SelectedValue = _configService.Current.WebAccessMode; }
                finally { _suppressEvents = false; }
                TxtStatusBarLeft.Text = string.Format(text["webSaveError"], ex.Message);
                return;
            }
            if (_configService.Current.AutoStartWebServer)
                await _webServer.RestartAsync();
            RefreshWebServerUi();
            TxtStatusBarLeft.Text = _configService.Current.AutoStartWebServer && !_webServer.IsRunning
                ? string.Format(text["webError"], _webServer.StatusMessage)
                : string.Format(text[_webServer.IsRunning ? "webRestarted" : "portSaved"], port);
        }
        finally
        {
            CmbWebAccessMode.IsEnabled = true;
            CmbWebVpnInterface.IsEnabled = true;
            BtnRefreshVpnInterfaces.IsEnabled = true;
            BtnRestartWebServer.IsEnabled = true;
            ChkEnableWebServer.IsEnabled = true;
            _applyingWebSettings = false;
        }
    }


    private sealed record FilterOption(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    private static string CategoryKey(string? category) => string.IsNullOrWhiteSpace(category) ? "\u0001" : category;
    private static string MarketCategoryKey(MarketCommodityItem item) =>
        CategoryKey(string.IsNullOrWhiteSpace(item.Category) ? item.DisplayCategory : item.Category);

    private void UpdateFilterCategories(System.Windows.Controls.ComboBox selector, IEnumerable<FilterOption> categories)
    {
        string selected = selector.SelectedValue as string ?? "";
        var oldOption = selector.SelectedItem as FilterOption;
        var options = categories.GroupBy(option => option.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (selected.Length > 0 && options.All(option => option.Key != selected) && oldOption is not null)
            options.Add(oldOption);
        options.Insert(0, new FilterOption("", MaterialTranslations.ForLanguage(_uiLanguage)["allCategories"]));
        if (selector.ItemsSource is IEnumerable<FilterOption> current && current.SequenceEqual(options))
            return;
        _updatingTableFilters = true;
        try
        {
            selector.ItemsSource = options;
            selector.SelectedValue = selected;
        }
        finally { _updatingTableFilters = false; }
    }

    private void UpdateMarketUi(ShipState state)
    {
        GridMarket.ItemsSource = state.Market.Items;
        UpdateFilterCategories(CmbMarketCategory, state.Market.Items.Select(item =>
            new FilterOption(MarketCategoryKey(item), string.IsNullOrWhiteSpace(item.DisplayCategory) ? "—" : item.DisplayCategory)));
        ApplyTableFilters();

        if (TxtMarketColonisationSummary is not null)
        {
            var needed = state.Market.Items
                .Where(static item => item.ColonisationNeeded > 0 && item.Stock > 0)
                .OrderByDescending(static item => item.ColonisationNeeded)
                .Take(6)
                .ToList();
            if (needed.Count > 0)
            {
                var ops = OperationsTranslations.ForLanguage(_uiLanguage);
                var culture = UiCulture;
                string itemsText = string.Join(" · ", needed.Select(i => $"{i.DisplayName}: {i.ColonisationNeeded.ToString("N0", culture)} t"));
                TxtMarketColonisationSummary.Text = string.Format(culture, ops["marketColonisationMatch"], needed.Count, itemsText);
                TxtMarketColonisationSummary.Visibility = Visibility.Visible;
            }
            else
            {
                TxtMarketColonisationSummary.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void TableFilter_Changed(object sender, EventArgs e)
    {
        if (_materialFilterControlsReady && !_updatingTableFilters)
            ApplyTableFilters();
    }

    private void ApplyTableFilters()
    {
        if (!_materialFilterControlsReady || _updatingTableFilters)
            return;
        static bool Contains(string? value, string search) =>
            (value ?? "").IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0;
        string format = OperationsTranslations.ForLanguage(_uiLanguage)["filterCount"];
        var culture = UiCulture;
        if (GridMarket.ItemsSource is IEnumerable<MarketCommodityItem> market)
        {
            string search = TxtMarketSearch.Text.Trim();
            string category = CmbMarketCategory.SelectedValue as string ?? "";
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(GridMarket.ItemsSource);
            view.Filter = item => item is MarketCommodityItem commodity &&
                (search.Length == 0 || Contains(commodity.DisplayName, search) || Contains(commodity.Name, search)) &&
                (category.Length == 0 || MarketCategoryKey(commodity) == category);
            TxtMarketFilterStatus.Text = string.Format(culture, format, GridMarket.Items.Count, market.Count());
        }
        if (GridCommanderProfile.ItemsSource is IEnumerable<ProfileValueView> profile)
        {
            string search = TxtProfileSearch.Text.Trim();
            string category = CmbProfileCategory.SelectedValue as string ?? "";
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(GridCommanderProfile.ItemsSource);
            view.Filter = item => item is ProfileValueView row &&
                (search.Length == 0 || Contains(row.Category, search) || Contains(row.Name, search) ||
                 Contains(row.Value, search) || Contains(row.Item.Name, search)) &&
                (category.Length == 0 || CategoryKey(row.CategoryId) == category);
            TxtProfileFilterStatus.Text = string.Format(culture, format, GridCommanderProfile.Items.Count, profile.Count());
        }
    }

    private void ApplyMaterialFilters()
    {
        if (!_materialFilterControlsReady)
            return;
        if (GridMaterials.ItemsSource is null)
        {
            TxtMaterialFilterStatus.Text = "";
            return;
        }

        string search = TxtMaterialSearch.Text.Trim();
        int categoryIndex = CmbMaterialCategory.SelectedIndex;
        int rarityIndex = CmbMaterialRarity.SelectedIndex;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(GridMaterials.ItemsSource);
        view.Filter = item =>
        {
            if (item is not MaterialItem material)
                return false;

            if (search.Length > 0 &&
                material.DisplayName.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0 &&
                material.Name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0)
                return false;

            if (categoryIndex > 0 &&
                !string.Equals(material.Category, ((System.Windows.Controls.ComboBoxItem)CmbMaterialCategory.Items[categoryIndex]).Tag?.ToString(), StringComparison.Ordinal))
                return false;

            if (rarityIndex is >= 1 and <= 5 && material.Grade != rarityIndex)
                return false;
            if (rarityIndex == 6 && material.Grade.HasValue)
                return false;

            return true;
        };
        view.Refresh();
        TxtMaterialFilterStatus.Text = string.Format(UiCulture,
            MaterialTranslations.ForLanguage(_uiLanguage)["filtered"], GridMaterials.Items.Count, _fileWatcher.State.MaterialsInventory.Count);
    }

    private sealed record AlertPreferenceTag(string AlertId, string Channel);
    private sealed record AlertSoundPreviewTag(
        string AlertId,
        System.Windows.Controls.ComboBox ProfileSelector);
    private sealed record SpeechVoiceOption(string VoiceName, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record ThemePresetOption(HudThemePreset Preset, string Name)
    {
        public override string ToString() => Name;
    }

    private static void SizeComboBoxToLongestItem(System.Windows.Controls.ComboBox comboBox, IEnumerable<string> labels)
    {
        double longestTextWidth = labels
            .Select(label =>
            {
                var measure = new TextBlock
                {
                    Text = label,
                    FontFamily = comboBox.FontFamily,
                    FontSize = comboBox.FontSize,
                    FontStretch = comboBox.FontStretch,
                    FontStyle = comboBox.FontStyle,
                    FontWeight = comboBox.FontWeight,
                    Language = comboBox.Language
                };
                measure.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                return measure.DesiredSize.Width;
            })
            .DefaultIfEmpty(0)
            .Max();

        comboBox.Width = Math.Max(comboBox.MinWidth, Math.Ceiling(longestTextWidth + 40));
    }

    private void UpdateThemePresetOptions()
    {
        string selectedAccent = _configService.Current.ThemeAccentHex;
        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        var options = ConfigService.Presets.Select(preset =>
            new ThemePresetOption(preset, text["theme." + preset.AccentHex])).ToList();
        bool previousSuppression = _suppressEvents;
        _suppressEvents = true;
        CmbThemePresets.ItemsSource = options;
        CmbThemePresets.SelectedItem = options.FirstOrDefault(option =>
            string.Equals(option.Preset.AccentHex, selectedAccent, StringComparison.OrdinalIgnoreCase));
        SizeComboBoxToLongestItem(CmbThemePresets, options.Select(option => option.Name));
        _suppressEvents = previousSuppression;
    }

    private void CmbThemePresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (CmbThemePresets.SelectedItem is ThemePresetOption option)
        {
            var preset = option.Preset;
            _configService.Current.ThemeName = preset.Name;
            _configService.Current.ThemeAccentHex = preset.AccentHex;
            _configService.Current.ThemeSecondaryHex = preset.SecondaryHex;
            TxtCustomAccentHex.Text = preset.AccentHex;
            _configService.Save();
            _fileWatcher.RefreshState();
            RenderCockpitSystemCards(_fileWatcher.State);
        }
    }

    private void BtnPickCustomColor_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.ColorDialog
        {
            AnyColor = true,
            FullOpen = true,
            CustomColors = ConfigService.Presets
                .Select(preset =>
                {
                    var mediaColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(preset.AccentHex);
                    return mediaColor.R | (mediaColor.G << 8) | (mediaColor.B << 16);
                })
                .ToArray()
        };

        string currentHex = TxtCustomAccentHex.Text.Trim();
        if (!currentHex.StartsWith('#'))
            currentHex = "#" + currentHex;
        try
        {
            var currentMediaColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(currentHex);
            dialog.Color = System.Drawing.Color.FromArgb(currentMediaColor.R, currentMediaColor.G, currentMediaColor.B);
        }
        catch
        {
            var fallbackMediaColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_configService.Current.ThemeAccentHex);
            dialog.Color = System.Drawing.Color.FromArgb(fallbackMediaColor.R, fallbackMediaColor.G, fallbackMediaColor.B);
        }

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
            return;

        string selectedHex = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        TxtCustomAccentHex.Text = selectedHex;
        ApplyCustomAccentHex(selectedHex);
    }

    private void BtnApplyCustomColor_Click(object sender, RoutedEventArgs e)
    {
        ApplyCustomAccentHex(TxtCustomAccentHex.Text);
    }

    private void ApplyCustomAccentHex(string rawHex)
    {
        string hex = rawHex.Trim();
        if (!hex.StartsWith("#", StringComparison.Ordinal))
            hex = "#" + hex;
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            TxtCustomAccentHex.Text = hex;
            var matchedPreset = ConfigService.Presets.FirstOrDefault(p =>
                string.Equals(p.AccentHex, hex, StringComparison.OrdinalIgnoreCase));
            _configService.Current.ThemeName = matchedPreset?.Name ?? "Personalizado";
            _configService.Current.ThemeAccentHex = hex;
            _configService.Current.ThemeSecondaryHex = matchedPreset?.SecondaryHex ?? hex;
            UpdateThemePresetOptions();
            _configService.Save();
            _fileWatcher.RefreshState();
            RenderCockpitSystemCards(_fileWatcher.State);
            TxtStatusBarLeft.Text = string.Format(SettingsTranslations.ForLanguage(_uiLanguage)["colorChanged"], hex);
        }
        catch
        {
            var text = SettingsTranslations.ForLanguage(_uiLanguage);
            System.Windows.MessageBox.Show(text["invalidColorMessage"], text["invalidColor"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnToggleFullscreen_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.F11)
            return;

        e.Handled = true;
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _windowedBounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            _windowedState = WindowState;
            _windowedStyle = WindowStyle;
            _windowedResizeMode = ResizeMode;

            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
            BtnToggleFullscreen.SetResourceReference(BackgroundProperty, "HudAccentBrush");
            BtnToggleFullscreen.SetResourceReference(ForegroundProperty, "HudBgBrush");
            BtnToggleFullscreen.ToolTip = SettingsTranslations.ForLanguage(_uiLanguage)["windowed"];
            return;
        }

        WindowState = WindowState.Normal;
        WindowStyle = _windowedStyle;
        ResizeMode = _windowedResizeMode;
        Left = _windowedBounds.Left;
        Top = _windowedBounds.Top;
        Width = _windowedBounds.Width;
        Height = _windowedBounds.Height;
        WindowState = _windowedState;
        _isFullscreen = false;
        BtnToggleFullscreen.SetResourceReference(BackgroundProperty, "HudPanelAltBrush");
        BtnToggleFullscreen.SetResourceReference(ForegroundProperty, "HudTextBrush");
        BtnToggleFullscreen.ToolTip = SettingsTranslations.ForLanguage(_uiLanguage)["fullscreen"];
    }

    private void BtnToggleMiniHud_Click(object sender, RoutedEventArgs e)
    {
        ToggleMiniHud();
    }

    private void ToggleMiniHud()
    {
        if (_isFullscreen)
            ToggleFullscreen();

        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        if (!_isMiniHud)
        {
            _miniHudBounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            _miniHudState = WindowState;
            _miniHudTopmost = Topmost;
            _miniHudOpacity = Opacity;

            WindowState = WindowState.Normal;
            Topmost = true;
            Opacity = 0.88;
            Width = 560;
            Height = 290;
            NavigationMenu.Visibility = Visibility.Collapsed;
            BottomStatusBar.Visibility = Visibility.Collapsed;
            SectionNavigationPanel.Visibility = Visibility.Collapsed;
            SwitchView(0);
            _isMiniHud = true;
            UpdateMiniHudContextUi(_fileWatcher.State);
            BtnToggleMiniHud.SetResourceReference(BackgroundProperty, "HudAccentBrush");
            BtnToggleMiniHud.SetResourceReference(ForegroundProperty, "HudBgBrush");
            BtnToggleMiniHud.ToolTip = text["miniHudExit"];
            return;
        }

        Topmost = _miniHudTopmost;
        Opacity = _miniHudOpacity;
        NavigationMenu.Visibility = Visibility.Visible;
        BottomStatusBar.Visibility = Visibility.Visible;
        Left = _miniHudBounds.Left;
        Top = _miniHudBounds.Top;
        Width = _miniHudBounds.Width;
        Height = _miniHudBounds.Height;
        WindowState = _miniHudState;
        _isMiniHud = false;
        UpdateMiniHudContextUi(_fileWatcher.State);
        BtnToggleMiniHud.SetResourceReference(BackgroundProperty, "HudPanelAltBrush");
        BtnToggleMiniHud.SetResourceReference(ForegroundProperty, "HudTextBrush");
        BtnToggleMiniHud.ToolTip = text["miniHud"];
        BuildSectionNavigation(0, preserveSelection: true);
    }

    private void RefreshUnhandledMessagesUi()
    {
        if (GridUnhandledEvents is null || TxtUnhandledStatus is null)
            return;

        bool grouped = ChkGroupUnhandledEvents?.IsChecked != false;
        var (items, distinctCount, totalCount) = _fileWatcher.GetUnhandledMessagesSnapshot(grouped);
        string query = TxtUnhandledSearch?.Text.Trim() ?? "";
        IReadOnlyList<UnhandledGameMessageItem> filtered = string.IsNullOrEmpty(query)
            ? items
            : items.Where(item =>
                item.EventType.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.Source.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.RawJson.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();

        GridUnhandledEvents.ItemsSource = filtered;
        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        var culture = UiCulture;
        TxtUnhandledStatus.Text = distinctCount == 0
            ? text["unhandledEmpty"]
            : string.Format(culture, text["unhandledStatus"], distinctCount, totalCount, filtered.Count);
    }

    private void ChkCaptureUnhandledEvents_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;

        bool enabled = ChkCaptureUnhandledEvents.IsChecked == true;
        _configService.Current.CaptureUnhandledEvents = enabled;
        _configService.Save();
        if (enabled)
            _fileWatcher.ScanCurrentFolderForUnhandledMessages();
        else
            RefreshUnhandledMessagesUi();
    }

    private void ChkWriteUnhandledEventsFile_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;

        bool enabled = ChkWriteUnhandledEventsFile.IsChecked == true;
        _configService.Current.WriteUnhandledEventsToFile = enabled;
        _configService.Save();
        if (enabled)
            _fileWatcher.SaveUnhandledEventsToFile();
    }

    private void ChkGroupUnhandledEvents_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;

        _configService.Current.GroupUnhandledEvents = ChkGroupUnhandledEvents.IsChecked == true;
        _configService.Save();
        RefreshUnhandledMessagesUi();
    }

    private void TxtUnhandledSearch_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshUnhandledMessagesUi();

    private void BtnScanUnhandledFiles_Click(object sender, RoutedEventArgs e)
    {
        int count = _fileWatcher.ScanCurrentFolderForUnhandledMessages();
        RefreshUnhandledMessagesUi();
        TxtStatusBarLeft.Text = string.Format(
            UiCulture,
            SettingsTranslations.ForLanguage(_uiLanguage)["unhandledScanned"],
            count);
    }

    private void BtnCopyUnhandledJson_Click(object sender, RoutedEventArgs e)
    {
        CopySelectedOrAllUnhandledJson();
    }

    private void GridUnhandledEvents_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridUnhandledEvents.SelectedItem is UnhandledGameMessageItem)
            CopySelectedOrAllUnhandledJson();
    }

    private void CopySelectedOrAllUnhandledJson()
    {
        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        string payload;
        if (GridUnhandledEvents.SelectedItem is UnhandledGameMessageItem selected)
        {
            payload = selected.RawJson;
        }
        else if (GridUnhandledEvents.ItemsSource is IEnumerable<UnhandledGameMessageItem> visible)
        {
            payload = string.Join(Environment.NewLine, visible.Select(static item => item.RawJson));
        }
        else
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(payload))
            return;

        try
        {
            System.Windows.Clipboard.SetText(payload);
            TxtStatusBarLeft.Text = text["unhandledCopied"];
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            TxtStatusBarLeft.Text = string.Format(text["clipboardError"], ex.Message);
        }
    }

    private void BtnOpenUnhandledFile_Click(object sender, RoutedEventArgs e)
    {
        _fileWatcher.SaveUnhandledEventsToFile();
        string path = _fileWatcher.UnhandledEventsFilePath;
        if (!File.Exists(path))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            var text = SettingsTranslations.ForLanguage(_uiLanguage);
            TxtStatusBarLeft.Text = string.Format(text["clipboardError"], ex.Message);
        }
    }

    private void BtnOpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = AppDiagnosticsLogger.LogsDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("UI", $"Failed to open logs folder: {ex.Message}");
            TxtStatusBarLeft.Text = ex.Message;
        }
    }

    private void BtnExportSupportBundle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string zipPath = AppDiagnosticsLogger.ExportSupportBundle(_configService.Current);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{zipPath}\"",
                UseShellExecute = true
            });
            TxtStatusBarLeft.Text = $"📦 {Path.GetFileName(zipPath)}";
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Error("UI", "Failed to export support bundle.", ex);
            TxtStatusBarLeft.Text = ex.Message;
        }
    }

    private void BtnClearUnhandledEvents_Click(object sender, RoutedEventArgs e)
    {
        _fileWatcher.ClearUnhandledMessages();
        RefreshUnhandledMessagesUi();
        TxtStatusBarLeft.Text = SettingsTranslations.ForLanguage(_uiLanguage)["unhandledCleared"];
    }

}
