// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/MainWindow.Localization.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Dynamic multi-language UI refresh, table header binding, and locale switching.
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
/// Dynamic multi-language UI refresh, table header binding, and locale switching.
/// </summary>
public partial class MainWindow
{
    private void ApplyLanguageSettingsLabels()
    {
        var text = LanguageSettingsTranslations.ForLanguage(_uiLanguage);
        TxtLanguageLabel.Text = text.Label;
        TxtLanguageLabel.Language = System.Windows.Markup.XmlLanguage.GetLanguage(_uiLanguage);
        TxtLanguageWarning.Language = System.Windows.Markup.XmlLanguage.GetLanguage(_uiLanguage);
        CmbUiLanguage.ToolTip = text.Tooltip;
        TxtLanguageWarning.Text = text.PartialWarning;
        TxtLanguageWarning.Visibility = string.IsNullOrEmpty(text.PartialWarning) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyDashboardLabels()
    {
        var text = DashboardTranslations.ForLanguage(_uiLanguage);
        var culture = UiCulture;
        (TextBlock Label, string Key)[] labels =
        {
            (LblDashboardCommander, "commanderAndShip"), (LblDashboardLocation, "currentLocation"),
            (LblDashboardResources, "flightResources"), (LblDashboardAlerts, "visualAlerts"),
            (LblDashboardRoute, "nextRoute"), (LblDashboardActivity, "recentActivity"),
            (LblSessionStatsTitle, "sessionStatsTitle")
        };
        BtnResetSessionStats.Content = text["sessionReset"];
        foreach (var (label, key) in labels)
        {
            label.Text = text[key].ToUpper(culture);
            label.Language = System.Windows.Markup.XmlLanguage.GetLanguage(_uiLanguage);
        }
    }

    private void InitializeTableHeaderKeys()
    {
        var source = TableHeaderTranslations.ForLanguage("es-ES");
        System.Windows.Controls.DataGrid[] tables =
        {
            GridNavRoute, GridExplorationBodies, GridCargo, GridMaterials, GridMissions, GridPassengers,
            GridBackpack, GridMarket, GridOutfitting, GridShipyard, GridExplorationEvents, GridJournal,
            GridCommanderProfile, GridColonisationResources, GridUnhandledEvents,
            GridShipModules, GridShipLocker, GridEngineers,
            GridSurfaceBookmarks, GridSurfaceEvents, GridMiningRefined,
            GridCombatFactions, GridMassacreStacks,
            GridSystemFactions, GridCarrierMaterials,
            GridSpanshServices, GridGalnetArticles,
            GridSpanshRoute, GridSpanshTrade, GridSpanshMiningRings, GridColonisationSuppliers,
            GridCarrierCargo, GridCarrierMarketOrders, GridStoredShips, GridStoredModules, GridCommunityGoals, GridEngineeringWishlist,
            GridEliteBgsWatchlist, GridSpanshBodiesSearch, GridSpanshCarriersSearch, GridNearestDssa, GridArdentMarket
        };
        foreach (var column in tables.SelectMany(table => table.Columns))
        {
            if (column.Header is not string header)
                throw new InvalidOperationException("Encabezado de tabla no válido.");
            if (header is "#" or "ID")
                continue;
            string? key = TableHeader.GetKey(column);
            if (key is null || !source.ContainsKey(key))
                throw new InvalidOperationException($"Falta la clave del encabezado de tabla: {header}");
            _tableHeaderKeys.Add(column, key);
        }
    }

    private void ApplyTableHeaders()
    {
        var text = TableHeaderTranslations.ForLanguage(_uiLanguage);
        foreach (var (column, key) in _tableHeaderKeys)
            column.Header = text[key];

        string unit = DashboardTranslations.ForLanguage(_uiLanguage)["lightYears"];
        GridNavRoute.Language = System.Windows.Markup.XmlLanguage.GetLanguage(_uiLanguage);
        ((DataGridTextColumn)GridNavRoute.Columns[4]).Binding = new WpfBinding("StepDistanceLy")
        {
            StringFormat = $"{{0:0.0}} {unit}"
        };
        ((DataGridTextColumn)GridNavRoute.Columns[5]).Binding = new WpfBinding("DistanceFromStartLy")
        {
            StringFormat = $"{{0:0.0}} {unit}"
        };
    }

    private void ApplyMaterialLabels()
    {
        var text = MaterialTranslations.ForLanguage(_uiLanguage);
        LblMaterials.Text = text["title"].ToUpper(UiCulture);
        TxtMaterialSearch.ToolTip = text["search"];
        TxtWishlistTarget.ToolTip = text["wishlistTargetTip"];
        BtnToggleWishlist.Content = text["wishlistToggle"];
        TxtShipLockerSearch.ToolTip = text["search"];
        CmbMaterialCategory.ToolTip = text["categoryFilter"];
        CmbMaterialRarity.ToolTip = text["rarityFilter"];
        System.Windows.Automation.AutomationProperties.SetName(TxtMaterialSearch, text["search"]);
        System.Windows.Automation.AutomationProperties.SetName(CmbMaterialCategory, text["categoryFilter"]);
        System.Windows.Automation.AutomationProperties.SetName(CmbMaterialRarity, text["rarityFilter"]);
        var language = System.Windows.Markup.XmlLanguage.GetLanguage(_uiLanguage);
        LblMaterials.Language = language;
        TxtMaterialSearch.Language = language;
        CmbMaterialCategory.Language = language;
        CmbMaterialRarity.Language = language;
        GridMaterials.Language = language;
        string[] categories = { "allCategories", "raw", "manufactured", "encoded" };
        string[] rarities = { "allRarities", "veryCommon", "common", "standard", "rare", "veryRare", "unclassified" };
        for (int i = 0; i < categories.Length; i++)
            ((System.Windows.Controls.ComboBoxItem)CmbMaterialCategory.Items[i]).Content = text[categories[i]];
        for (int i = 0; i < rarities.Length; i++)
            ((System.Windows.Controls.ComboBoxItem)CmbMaterialRarity.Items[i]).Content =
                i is >= 1 and <= 5 ? $"{text[rarities[i]]} ({i})" : text[rarities[i]];

        ((DataGridTextColumn)GridMaterials.Columns[1]).Binding = new WpfBinding("Category")
        {
            Converter = new MaterialValueConverter(_uiLanguage, true)
        };
        ((DataGridTextColumn)GridMaterials.Columns[2]).Binding = new WpfBinding("Grade")
        {
            Converter = new MaterialValueConverter(_uiLanguage, false),
            TargetNullValue = text["unclassified"]
        };
        UpdateMaterialsStatus(_fileWatcher.State);
        ApplyMaterialFilters();
    }

    private void ApplyFlightLabels()
    {
        var text = FlightTranslations.ForLanguage(_uiLanguage);
        ApplyTextTree(CockpitResourcesSection, text, flightLabels: true, uppercaseTitles: true);
        ApplyTextTree(CockpitSystemsSection, text, flightLabels: true, uppercaseTitles: true);
        ApplyTextTree(CockpitOnFootSection, text, flightLabels: true, uppercaseTitles: true);
        ApplyTextTree(CockpitModulesSection, text, flightLabels: true, uppercaseTitles: true);
        ApplyTextTree(ViewNavigation, text, flightLabels: true, uppercaseTitles: true);
        UpdateNavigationActionsColumns();
        UpdateGalaxyStatusUi(_galaxyStatus.Current);
    }

    private void ApplyTextTree(DependencyObject root, IReadOnlyDictionary<string, string> text, bool flightLabels = false, bool uppercaseTitles = false)
    {
        var language = System.Windows.Markup.XmlLanguage.GetLanguage(_uiLanguage);
        void Apply(DependencyObject node)
        {
            string? key = UiText.GetKey(node);
            if (key is not null)
            {
                string value = text[key];
                if (flightLabels && key is ("oxygen" or "health" or "temperature" or "gravity" or "weapon"))
                    value += ": ";
                if (uppercaseTitles && key.EndsWith("Title", StringComparison.Ordinal))
                    value = value.ToUpper(UiCulture);
                if (node is TextBlock block)
                    block.Text = value;
                else if (node is System.Windows.Controls.ContentControl control && control.Content is string)
                    control.Content = value;
                else
                    throw new InvalidOperationException($"Elemento de interfaz no válido: {key}.");
                if (node is FrameworkElement element)
                    element.Language = language;
            }
            string? tooltipKey = UiText.GetTooltipKey(node);
            if (tooltipKey is not null && node is FrameworkElement tooltipElement)
                tooltipElement.ToolTip = text[tooltipKey];
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject dependency)
                    Apply(dependency);
        }
        Apply(root);
    }

    private void ApplySettingsLabels()
    {
        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        ApplyTextTree(ViewConfig, text);
        TxtConfigPathsTitle.Text = text["pathsTitle"];
        TxtConfigWebTitle.Text = text["webTitle"];
        TxtBindsProfileLabel.Text = text["profileShort"];
        TxtConfigSelectedBindsFile.ToolTip = text["profilePathTip"];
        BtnBrowseJournalFolder.ToolTip = text["browseJournal"];
        BtnBrowseBindingsFolder.ToolTip = text["browseBindings"];
        BtnBrowseBindsProfile.ToolTip = text["browseProfile"];
        System.Windows.Automation.AutomationProperties.SetName(BtnBrowseJournalFolder, text["browseJournal"]);
        System.Windows.Automation.AutomationProperties.SetName(BtnBrowseBindingsFolder, text["browseBindings"]);
        System.Windows.Automation.AutomationProperties.SetName(BtnBrowseBindsProfile, text["browseProfile"]);
        System.Windows.Automation.AutomationProperties.SetName(BtnRestartWebServer, text["restart"]);
        System.Windows.Automation.AutomationProperties.SetName(BtnOpenWebConfig, text["openBrowser"]);
        System.Windows.Automation.AutomationProperties.SetName(BtnCopyWebUrl, text["copyUrl"]);
        BtnCopyWebSessionCode.ToolTip = text["copySessionCode"];
        BtnWebServerInfo.ToolTip = text[WebServerInfoPanel.Visibility == Visibility.Visible ? "hideWebInfo" : "showWebInfo"];
        System.Windows.Automation.AutomationProperties.SetName(BtnWebServerInfo, (string)BtnWebServerInfo.ToolTip);
        System.Windows.Automation.AutomationProperties.SetName(BtnTestAlertVoice, text["testVoice"]);
        System.Windows.Automation.AutomationProperties.SetName(BtnPickCustomColor, text["pickColor"]);
        System.Windows.Automation.AutomationProperties.SetName(TxtUnhandledSearch, text["unhandledSearch"]);
        RefreshUnhandledMessagesUi();
        if (ChkEnableSpanshSystemData.Content is TextBlock spanshLabel)
            spanshLabel.Text = text["enableSpanshData"];
        ChkEnableSpanshSystemData.ToolTip = text["spanshDataTooltip"];
        UpdateSpanshUi(_spanshSystemDataService.State);
        if (TxtStatusBarLeft.Text == _readyStatusText)
        {
            _readyStatusText = text["readyStatus"];
            TxtStatusBarLeft.Text = _readyStatusText;
        }
        TxtStatusBarRight.Text = text["trayStatus"];
        BtnOpenWebHeader.ToolTip = text["openWeb"];
        System.Windows.Automation.AutomationProperties.SetName(BtnOpenWebHeader, text["openWeb"]);
        BtnExitHeader.ToolTip = text["exitApp"];
        System.Windows.Automation.AutomationProperties.SetName(BtnExitHeader, text["exit"]);
        BtnToggleMiniHud.ToolTip = text[_isMiniHud ? "miniHudExit" : "miniHud"];
        BtnToggleFullscreen.ToolTip = text[_isFullscreen ? "windowed" : "fullscreen"];
        System.Windows.Automation.AutomationProperties.SetName(BtnToggleFullscreen, text["fullscreenName"]);
        UpdateThemePresetOptions();
        if (_notifyIcon?.ContextMenuStrip is { } menu)
        {
            _notifyIcon.Text = text["trayActive"];
            menu.Items[0].Text = text["trayOpen"];
            menu.Items[1].Text = text["trayWeb"];
            menu.Items[2].Text = text["trayReload"];
            menu.Items[4].Text = text["exit"];
        }
        UpdateBindingsProfileInfo();
        UpdateSpeechQueueUi();
        if (_installedVoiceNames is not null)
            ApplySpeechVoiceOptions(CmbAlertVoice.SelectedValue as string ?? "");
        else
            TxtAlertVoiceStatus.Text = text["searchingVoices"];
    }

    private void ApplyOperationsLabels()
    {
        var text = OperationsTranslations.ForLanguage(_uiLanguage);
        var materialText = MaterialTranslations.ForLanguage(_uiLanguage);
        TxtMarketSearch.ToolTip = text["marketSearch"];
        TxtProfileSearch.ToolTip = text["profileSearch"];
        CmbMarketCategory.ToolTip = materialText["categoryFilter"];
        CmbProfileCategory.ToolTip = materialText["categoryFilter"];
        System.Windows.Automation.AutomationProperties.SetName(TxtMarketSearch, text["marketSearch"]);
        System.Windows.Automation.AutomationProperties.SetName(TxtProfileSearch, text["profileSearch"]);
        System.Windows.Automation.AutomationProperties.SetName(CmbMarketCategory, materialText["categoryFilter"]);
        System.Windows.Automation.AutomationProperties.SetName(CmbProfileCategory, materialText["categoryFilter"]);
        ApplyTextTree(ViewOperations, text, uppercaseTitles: true);
        ApplyTextTree(ViewInventory, text, uppercaseTitles: true);
        ApplyTextTree(ViewActivity, text, uppercaseTitles: true);
        ApplyTextTree(OperationsCombatSection, text, uppercaseTitles: true);
        LblExplorationBodies.Text = text["explorationTitle"].ToUpper(UiCulture);
        void SetColumn(System.Windows.Controls.DataGrid grid, int index, OperationsValue kind)
        {
            ((DataGridTextColumn)grid.Columns[index]).Binding = new WpfBinding
            {
                Converter = new OperationsValueConverter(_uiLanguage, kind)
            };
        }
        void SetNumberColumn(System.Windows.Controls.DataGrid grid, int index, string property, bool credits = false)
        {
            ((DataGridTextColumn)grid.Columns[index]).Binding = new WpfBinding(property)
            {
                ConverterCulture = UiCulture,
                StringFormat = credits ? "{0:N0} CR" : "{0:N0}"
            };
        }
        SetColumn(GridCargo, 2, OperationsValue.CargoState);
        SetColumn(GridMissions, 0, OperationsValue.MissionName);
        SetColumn(GridMissions, 4, OperationsValue.MissionExpiry);
        SetColumn(GridMissions, 5, OperationsValue.MissionRemaining);
        SetColumn(GridPassengers, 0, OperationsValue.PassengerType);
        SetColumn(GridPassengers, 1, OperationsValue.PassengerCount);
        SetColumn(GridPassengers, 4, OperationsValue.PassengerExpiry);
        SetColumn(GridBackpack, 1, OperationsValue.BackpackType);
        SetColumn(GridMarket, 9, OperationsValue.MarketFlags);
        SetNumberColumn(GridMarket, 2, nameof(MarketCommodityItem.BuyPrice), true);
        SetNumberColumn(GridMarket, 3, nameof(MarketCommodityItem.SellPrice), true);
        SetNumberColumn(GridMarket, 4, nameof(MarketCommodityItem.MeanPrice), true);
        SetNumberColumn(GridMarket, 5, nameof(MarketCommodityItem.Stock));
        SetNumberColumn(GridMarket, 7, nameof(MarketCommodityItem.Demand));
        SetNumberColumn(GridOutfitting, 2, nameof(OutfittingItem.BuyPrice), true);
        SetNumberColumn(GridShipyard, 1, nameof(ShipyardItem.ShipPrice), true);
        UpdateOperationsStatus(_fileWatcher.State);
        UpdateExplorationUi(_fileWatcher.State);
        UpdateProfileUi(_fileWatcher.State);
        UpdateMarketUi(_fileWatcher.State);
    }

    private void UpdateProfileUi(ShipState state)
    {
        _lastProfileSource = state.CommanderProfile;
        var selected = (GridCommanderProfile.SelectedItem as ProfileValueView)?.Item;
        var rows = state.CommanderProfile.Select(item => ProfileTranslations.Present(item, _uiLanguage)).ToList();
        GridCommanderProfile.ItemsSource = rows;
        UpdateFilterCategories(CmbProfileCategory, rows.Select(row => new FilterOption(CategoryKey(row.CategoryId), row.Category)));
        ApplyTableFilters();
        if (selected is not null)
            GridCommanderProfile.SelectedItem = rows.FirstOrDefault(row => ReferenceEquals(row.Item, selected) ||
                (row.Item.CategoryKey == selected.CategoryKey && row.Item.Category == selected.Category && row.Item.Name == selected.Name));
    }

    private void UpdateOperationsStatus(ShipState state)
    {
        var text = OperationsTranslations.ForLanguage(_uiLanguage);
        TxtMissionsStatus.Text = state.ActiveMissions.Count == 0 ? text["missionsEmpty"] :
            string.Format(text["missionsCount"], state.ActiveMissions.Count);
        TxtPassengerStatus.Text = state.PassengerManifest.Count == 0 ? text["passengersEmpty"] :
            string.Format(text["passengersCount"], state.PassengerManifest.Count);
        TxtBackpackStatus.Text = state.BackpackInventory.Count == 0 ? text["backpackWaiting"] :
            string.Format(text["backpackCount"], state.BackpackInventory.Count);
        TxtProfileStatus.Text = state.CommanderProfile.Count == 0 ? text["profileWaiting"] :
            string.Format(text["profileCount"], state.CommanderProfile.Count);
        TxtMarketStatus.Text = OperationsTranslations.Snapshot(state.Market.Timestamp, state.Market.StationName, state.Market.StarSystem, "marketUnavailable", _uiLanguage);
        TxtOutfittingStatus.Text = OperationsTranslations.Snapshot(state.Outfitting.Timestamp, state.Outfitting.StationName, state.Outfitting.StarSystem, "outfittingUnavailable", _uiLanguage);
        TxtShipyardStatus.Text = OperationsTranslations.Snapshot(state.Shipyard.Timestamp, state.Shipyard.StationName, state.Shipyard.StarSystem, "shipyardUnavailable", _uiLanguage);
        TxtColonisationStatus.Text = OperationsTranslations.Construction(state.Colonisation, _uiLanguage);
        string progress = state.Colonisation.ConstructionProgress.HasValue
            ? $"{(state.Colonisation.ConstructionProgress.Value * 100).ToString("0.#", UiCulture)}%"
            : "---";
        TxtColonisationProgress.Text = string.Format(text["progress"], progress);
    }

    private void UpdateExplorationUi(ShipState state)
    {
        _lastExplorationBodiesSource = state.ExplorationBodies;
        var text = ExplorationTranslations.ForLanguage(_uiLanguage);
        var selected = (GridExplorationBodies.SelectedItem as ExplorationBodyView)?.Body;
        var rows = state.ExplorationBodies.Select(body => ExplorationTranslations.Present(body, _uiLanguage)).ToList();
        GridExplorationBodies.ItemsSource = rows;
        if (selected is not null)
            GridExplorationBodies.SelectedItem = rows.FirstOrDefault(row => ReferenceEquals(row.Body, selected) ||
                (!string.IsNullOrEmpty(selected.Id) && row.Body.Id == selected.Id));
        TxtExplorationBodiesStatus.Text = rows.Count == 0 ? text["empty"] :
            string.Format(text["bodyCount"], rows.Count, state.ExplorationBodies[0].SystemName);
        TxtExplorationEventsStatus.Text = GridExplorationEvents.Items.Count == 0 ? text["milestonesEmpty"] :
            string.Format(text["milestonesCount"], GridExplorationEvents.Items.Count);
        UpdateExplorationProgressAndTracker(state);
    }

    private void UpdateExplorationProgressAndTracker(ShipState state)
    {
        var text = ExplorationTranslations.ForLanguage(_uiLanguage);
        var culture = UiCulture;

        var summaryParts = new List<string>();
        if (state.SystemTotalBodies is > 0)
        {
            string fssPart = state.SystemAllBodiesFound
                ? string.Format(culture, text["fssProgressComplete"], state.SystemTotalBodies.Value)
                : string.Format(culture, text["fssProgress"], state.SystemScannedBodies, state.SystemTotalBodies.Value);
            summaryParts.Add(fssPart);
        }
        if (state.UnsoldCartographyCredits > 0 || state.UnsoldExobiologyCredits > 0)
        {
            summaryParts.Add(string.Format(
                culture,
                text["unsoldPortfolio"],
                state.UnsoldCartographyCredits,
                state.UnsoldExobiologyCredits));
        }

        TxtExplorationPortfolio.Text = string.Join("  ·  ", summaryParts);
        TxtExplorationPortfolio.Visibility = summaryParts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (state.ExobiologyTracker is { SamplesCollected: > 0 and < 3 } exo)
        {
            double pct = exo.RequiredDistanceMeters > 0
                ? Math.Clamp((exo.CurrentDistanceMeters / exo.RequiredDistanceMeters) * 100.0, 0, 100)
                : 0;
            TxtExobiologyTrackerTitle.Text = string.Format(
                culture,
                text["exoTrackerTitle"],
                string.IsNullOrWhiteSpace(exo.Species) ? text["organicSpeciesFallback"] : exo.Species,
                exo.SamplesCollected);
            TxtExobiologyTrackerDistance.Text = exo.CanSampleNext
                ? string.Format(culture, text["exoTrackerReady"], exo.CurrentDistanceMeters, exo.RequiredDistanceMeters)
                : string.Format(culture, text["exoTrackerDistance"], exo.CurrentDistanceMeters, exo.RequiredDistanceMeters);
            BarExobiologyDistance.Value = pct;
            ExobiologyTrackerCard.Visibility = Visibility.Visible;
        }
        else
        {
            ExobiologyTrackerCard.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateMaterialsStatus(ShipState state)
    {
        var text = MaterialTranslations.ForLanguage(_uiLanguage);
        TxtMaterialsStatus.Text = !state.HasMaterialsTelemetry
            ? text["waiting"]
            : state.MaterialsInventory.Count == 0
                ? text["empty"]
                : string.Format(UiCulture, text["inventory"], state.MaterialsInventory.Count);
    }

    private void ApplyDeckLabels()
    {
        var text = DeckTranslations.ForLanguage(_uiLanguage);
        ApplyTextTree(ViewControls, text);
        System.Windows.Automation.AutomationProperties.SetName(TxtSearchBinds, text["search"]);
        FilterBindingsDeck();
    }

    private void UpdateBindingsProfileInfo()
    {
        if (_displayedBindsFile is not null &&
            !string.Equals(TxtConfigSelectedBindsFile.Text, _displayedBindsFile, StringComparison.Ordinal))
            return;

        _displayedBindsFile = _bindingsService.UsingDefaultBindings
            ? DeckTranslations.ForLanguage(_uiLanguage)["defaultFile"]
            : _bindingsService.ActiveBindsFilePath;
        TxtConfigSelectedBindsFile.Text = _displayedBindsFile;
    }

    private void CmbUiLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _configService is null || CmbUiLanguage.SelectedValue is not string language ||
            string.Equals(language, _uiLanguage, StringComparison.Ordinal))
            return;

        try
        {
            _configService.SetInterfaceLanguage(language);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _suppressEvents = true;
            CmbUiLanguage.SelectedValue = _uiLanguage;
            _suppressEvents = false;
            TxtStatusBarLeft.Text = string.Format(SettingsTranslations.ForLanguage(_uiLanguage)["languageSaveError"], ex.Message);
            return;
        }

        _uiLanguage = language;
        ApplyLanguageSettingsLabels();
        ApplyNavigationLabels();
        ApplyDashboardLabels();
        RefreshHeaderOptions();
        ApplyTableHeaders();
        ApplyMaterialLabels();
        ApplyFlightLabels();
        ApplySettingsLabels();
        RefreshWebServerUi();
        RefreshSoundDirectories();
        ApplyOperationsLabels();
        UpdateJournalUi(_fileWatcher.State);
        ApplyDeckLabels();
        _cockpitCardsKey = null;
        UpdateFlightUi(_fileWatcher.State);
        UpdateCarrierUi(_fileWatcher.State);
        BuildSectionNavigation(7, preserveSelection: true);
        BuildAlertSettingsGrid();
        ApplyDesktopFontScale(_desktopFontScale, AlertSettingsGrid);
        UpdateDashboardUi(_fileWatcher.State);
        RefreshAlertsUi();
    }

}
