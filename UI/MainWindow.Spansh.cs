// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/MainWindow.Spansh.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: UI handlers for Spansh route plotting, CSV import, trade, mining rings, services, and GalNet.
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
/// UI handlers for Spansh route plotting, CSV import, trade, mining rings, services, and GalNet.
/// </summary>
public partial class MainWindow
{
    private async void BtnSearchSpanshService_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Spansh Services", async () =>
        {
            if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string serviceKey)
                return;

            var flight = FlightTranslations.ForLanguage(_uiLanguage);
            TxtSpanshServicesStatus.Text = flight["spanshServicesSearching"];
            var state = _fileWatcher.State;
            var (success, results, message) = await _spanshSystemDataService.SearchNearestServicesAsync(
                serviceKey,
                state.CurrentSystem,
                state.StarPos);

            if (success && results.Count > 0)
            {
                GridSpanshServices.ItemsSource = results;
                GridSpanshServices.Visibility = Visibility.Visible;
                TxtSpanshServicesStatus.Text = string.Format(flight["spanshServicesFound"], results.Count);
            }
            else
            {
                GridSpanshServices.ItemsSource = null;
                GridSpanshServices.Visibility = Visibility.Collapsed;
                TxtSpanshServicesStatus.Text = success ? flight["spanshServicesEmpty"] : message;
            }
        });

    private void GridSpanshServices_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridSpanshServices.SelectedItem is not SpanshServiceStationItem item || string.IsNullOrWhiteSpace(item.SystemName))
            return;
        CopySystemToClipboard(item.SystemName);
    }

    private async void BtnRefreshGalnet_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("GalNet Refresh", async () =>
        {
            var ops = OperationsTranslations.ForLanguage(_uiLanguage);
            TxtGalnetStatus.Text = ops["galnetLoading"];
            var (success, msg) = await _galnetService.RefreshAsync(_uiLanguage);
            if (!success)
                TxtGalnetStatus.Text = msg;
        });

    private void RefreshGalnetUi()
    {
        if (GridGalnetArticles is null || TxtGalnetStatus is null)
            return;

        var articles = _galnetService.Articles;
        GridGalnetArticles.ItemsSource = articles;
        TxtGalnetStatus.Text = string.Format(OperationsTranslations.ForLanguage(_uiLanguage)["galnetCount"], articles.Count);
        if (articles.Count > 0 && GridGalnetArticles.SelectedItem is null)
            GridGalnetArticles.SelectedIndex = 0;
    }

    private void GridGalnetArticles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtGalnetBody is null)
            return;
        TxtGalnetBody.Text = GridGalnetArticles.SelectedItem is GalnetArticle article
            ? $"{article.Title}\n({article.DateDisplay})\n\n{article.Body}"
            : "";
    }

    private void BtnSpeakGalnet_Click(object sender, RoutedEventArgs e)
    {
        if (GridGalnetArticles.SelectedItem is GalnetArticle article)
            _alertSpeechService.SpeakCustomText($"{article.Title}. {article.Body}");
    }

    private void BtnStopGalnetSpeech_Click(object sender, RoutedEventArgs e)
    {
        _alertSpeechService.StopSpeaking();
    }

    private async void BtnSearchSpanshItem_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Spansh Item Search", async () =>
        {
            string query = TxtSpanshItemQuery.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
                return;

            bool isShip = (CmbSpanshItemType.SelectedItem as ComboBoxItem)?.Tag as string == "ship";
            bool lyr = ChkSpanshLyrDiscount.IsChecked == true;
            var flight = FlightTranslations.ForLanguage(_uiLanguage);
            TxtSpanshServicesStatus.Text = flight["spanshServicesSearching"];

            var state = _fileWatcher.State;
            var (success, results, message) = await _spanshSystemDataService.SearchShipOrModuleAsync(
                query, isShip, lyr, state.CurrentSystem, state.StarPos);

            if (success && results.Count > 0)
            {
                GridSpanshServices.ItemsSource = results;
                GridSpanshServices.Visibility = Visibility.Visible;
                TxtSpanshServicesStatus.Text = string.Format(flight["spanshServicesFound"], results.Count);
            }
            else
            {
                GridSpanshServices.ItemsSource = null;
                GridSpanshServices.Visibility = Visibility.Collapsed;
                TxtSpanshServicesStatus.Text = success ? flight["spanshServicesEmpty"] : message;
            }
        });

    private async void BtnPlotSpanshRoute_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Spansh Route", async () =>
        {
            string mode = (CmbSpanshRouteMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "neutron";
            string dest = TxtSpanshRouteDest.Text.Trim();
            double range = double.TryParse(
                TxtSpanshRouteRange.Text.Trim().Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double r) && r > 0 ? r : 50.0;

            var state = _fileWatcher.State;
            long carrierUsed = state.Carrier?.Capacity is long cap && state.Carrier?.FreeSpace is long free
                ? Math.Max(0, cap - free)
                : (state.Carrier?.Cargo ?? 0);

            var flight = FlightTranslations.ForLanguage(_uiLanguage);
            TxtSpanshRouteStatus.Text = flight["spanshRouteCalculating"];

            var (success, waypoints, message) = await _spanshSystemDataService.PlotSpanshRouteAsync(
                mode, state.CurrentSystem, dest, range, carrierUsed);

            if (success && waypoints.Count > 0)
                ApplySpanshRouteWaypoints(waypoints);
            else
                TxtSpanshRouteStatus.Text = message;
        });

    private void BtnImportSpanshCsv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Spansh CSV (*.csv)|*.csv|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true)
                return;

            var waypoints = SpanshSystemDataService.ParseSpanshCsvRoute(dialog.FileName);
            if (waypoints.Count > 0)
                ApplySpanshRouteWaypoints(waypoints);
            else
                TxtSpanshRouteStatus.Text = FlightTranslations.ForLanguage(_uiLanguage)["spanshServicesEmpty"];
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("Spansh", $"CSV route import failed: {ex.Message}");
            SetStatusBarMessage($"⚠️ {ex.Message}");
        }
    }

    private void ApplySpanshRouteWaypoints(List<SpanshRouteWaypoint> waypoints)
    {
        _spanshRouteWaypoints = waypoints;
        _lastSpanshRouteAutoCopied = "";
        GridSpanshRoute.ItemsSource = _spanshRouteWaypoints;
        GridSpanshRoute.Visibility = Visibility.Visible;
        TxtSpanshRouteStatus.Text = string.Format(
            FlightTranslations.ForLanguage(_uiLanguage)["spanshRouteLoaded"],
            waypoints.Count);
        AdvanceSpanshRouteOnSystemChange(_fileWatcher.State);
    }

    private void AdvanceSpanshRouteOnSystemChange(ShipState s)
    {
        if (_spanshRouteWaypoints.Count == 0 || string.IsNullOrWhiteSpace(s.CurrentSystem))
            return;

        bool changed = false;
        int matchedIndex = -1;
        for (int i = 0; i < _spanshRouteWaypoints.Count; i++)
        {
            if (string.Equals(_spanshRouteWaypoints[i].SystemName, s.CurrentSystem, StringComparison.OrdinalIgnoreCase))
            {
                matchedIndex = i;
                break;
            }
        }

        if (matchedIndex >= 0)
        {
            for (int i = 0; i <= matchedIndex; i++)
            {
                if (!_spanshRouteWaypoints[i].Completed)
                {
                    _spanshRouteWaypoints[i] = _spanshRouteWaypoints[i] with { Completed = true };
                    changed = true;
                }
            }
        }

        var next = _spanshRouteWaypoints.FirstOrDefault(static w => !w.Completed);
        if (changed)
        {
            GridSpanshRoute.ItemsSource = null;
            GridSpanshRoute.ItemsSource = _spanshRouteWaypoints;
        }

        if (next is not null &&
            ChkSpanshAutoCopy?.IsChecked == true &&
            !string.Equals(_lastSpanshRouteAutoCopied, next.SystemName, StringComparison.OrdinalIgnoreCase))
        {
            _lastSpanshRouteAutoCopied = next.SystemName;
            try
            {
                System.Windows.Clipboard.SetText(next.SystemName);
                string msg = string.Format(FlightTranslations.ForLanguage(_uiLanguage)["spanshRouteNextCopied"], next.SystemName);
                TxtSpanshRouteStatus.Text = msg;
                SetStatusBarMessage(msg);
            }
            catch
            {
                // Ignorar error de portapapeles
            }
        }
    }

    private void GridSpanshRoute_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridSpanshRoute.SelectedItem is not SpanshRouteWaypoint wp)
            return;
        CopySystemToClipboard(wp.SystemName);
    }

    private async void BtnPlotSpanshTrade_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Spansh Trade", async () =>
        {
            var state = _fileWatcher.State;
            var ops = OperationsTranslations.ForLanguage(_uiLanguage);
            TxtSpanshTradeStatus.Text = ops["spanshTradeSearching"];

            var (success, hops, message) = await _spanshSystemDataService.PlotTradeRouteAsync(
                state.CurrentSystem,
                state.StationName,
                state.CargoCapacity,
                state.Credits,
                30,
                requireLargePad: state.CargoCapacity >= 200);

            if (success && hops.Count > 0)
            {
                GridSpanshTrade.ItemsSource = hops;
                GridSpanshTrade.Visibility = Visibility.Visible;
                TxtSpanshTradeStatus.Text = string.Format(ops["spanshTradeFound"], hops.Count);
            }
            else
            {
                GridSpanshTrade.ItemsSource = null;
                GridSpanshTrade.Visibility = Visibility.Collapsed;
                TxtSpanshTradeStatus.Text = success ? ops["spanshTradeEmpty"] : message;
            }
        });

    private void GridSpanshTrade_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridSpanshTrade.SelectedItem is not SpanshTradeHopItem hop)
            return;
        CopySystemToClipboard(hop.ToSystem);
    }

    private async void BtnSearchMiningRings_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Spansh Mining", async () =>
        {
            string mineral = (CmbSpanshMiningMineral.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Platinum";
            var ops = OperationsTranslations.ForLanguage(_uiLanguage);
            TxtSpanshMiningStatus.Text = ops["spanshMiningSearching"];

            var state = _fileWatcher.State;
            var (success, results, message) = await _spanshSystemDataService.SearchMiningRingsAsync(
                mineral, state.CurrentSystem, state.StarPos);

            if (success && results.Count > 0)
            {
                GridSpanshMiningRings.ItemsSource = results;
                GridSpanshMiningRings.Visibility = Visibility.Visible;
                TxtSpanshMiningStatus.Text = string.Format(ops["spanshMiningFound"], results.Count, mineral);
            }
            else
            {
                GridSpanshMiningRings.ItemsSource = null;
                GridSpanshMiningRings.Visibility = Visibility.Collapsed;
                TxtSpanshMiningStatus.Text = success ? ops["spanshMiningEmpty"] : message;
            }
        });

    private void GridSpanshMiningRings_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridSpanshMiningRings.SelectedItem is not SpanshMiningRingItem ring)
            return;
        CopySystemToClipboard(ring.SystemName);
    }

    private async void BtnFindColonisationSupplier_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Colonisation Supplier", async () =>
        {
            var state = _fileWatcher.State;
            var selected = GridColonisationResources.SelectedItem as ColonisationResourceItem
                ?? state.Colonisation.Resources.FirstOrDefault(static r => r.RequiredAmount > r.ProvidedAmount);
            var ops = OperationsTranslations.ForLanguage(_uiLanguage);

            if (selected is null)
            {
                TxtColonisationSupplierStatus.Text = ops["colonisationNoPending"];
                TxtColonisationSupplierStatus.Visibility = Visibility.Visible;
                return;
            }

            long remaining = Math.Max(1, selected.RequiredAmount - selected.ProvidedAmount);
            TxtColonisationSupplierStatus.Text = string.Format(ops["colonisationSearching"], selected.DisplayName);
            TxtColonisationSupplierStatus.Visibility = Visibility.Visible;

            var (success, results, message) = await _spanshSystemDataService.SearchCommoditySupplierAsync(
                selected.DisplayName,
                Math.Min(remaining, 500),
                requireLargePad: true,
                state.CurrentSystem,
                state.StarPos);

            if (success && results.Count > 0)
            {
                GridColonisationSuppliers.ItemsSource = results;
                GridColonisationSuppliers.Visibility = Visibility.Visible;
                TxtColonisationSupplierStatus.Text = string.Format(ops["colonisationSuppliersFound"], selected.DisplayName, results.Count);
            }
            else
            {
                GridColonisationSuppliers.ItemsSource = null;
                GridColonisationSuppliers.Visibility = Visibility.Collapsed;
                TxtColonisationSupplierStatus.Text = success ? ops["spanshTradeEmpty"] : message;
            }
        });

    private void GridColonisationSuppliers_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridColonisationSuppliers.SelectedItem is not SpanshServiceStationItem item)
            return;
        CopySystemToClipboard(item.SystemName);
    }

    private void CopySystemToClipboard(string systemName)
    {
        if (string.IsNullOrWhiteSpace(systemName))
            return;
        try
        {
            System.Windows.Clipboard.SetText(systemName);
            SetStatusBarMessage(string.Format(FlightTranslations.ForLanguage(_uiLanguage)["systemCopied"], systemName));
        }
        catch
        {
            // Ignore clipboard access errors when another process holds the clipboard lock.
        }
    }

    /// <summary>
    /// Executes a Spansh route plot requested directly by the COVAS AI Copilot.
    /// </summary>
    private void ExecuteSpanshRouteFromAi(string mode, string destination)
    {
        if (CmbSpanshRouteMode is null || TxtSpanshRouteDest is null)
            return;
        var match = CmbSpanshRouteMode.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, mode, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            CmbSpanshRouteMode.SelectedItem = match;
        else
            CmbSpanshRouteMode.SelectedValue = mode;
        TxtSpanshRouteDest.Text = destination;
        BtnPlotSpanshRoute_Click(this, new RoutedEventArgs());
    }

    /// <summary>
    /// Executes a Spansh nearest-service search requested directly by the COVAS AI Copilot.
    /// </summary>
    private void ExecuteSpanshServiceFromAi(string serviceKey)
    {
        if (string.IsNullOrWhiteSpace(serviceKey))
            return;
        BtnSearchSpanshService_Click(new System.Windows.Controls.Button { Tag = serviceKey }, new RoutedEventArgs());
    }

}
