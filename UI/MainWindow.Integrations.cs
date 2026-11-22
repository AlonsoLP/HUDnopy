// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/MainWindow.Integrations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: UI handlers for the Web HUD server, QR pairing, EDDN/EDSM/Inara, and Frontier cAPI.
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
/// UI handlers for the Web HUD server, QR pairing, EDDN/EDSM/Inara, and Frontier cAPI.
/// </summary>
public partial class MainWindow
{
    private void RefreshWebServerUi()
    {
        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        string[] urls = _webServer.IsRunning ? _webServer.GetAccessUrls().ToArray() : Array.Empty<string>();
        LstWebUrls.ItemsSource = urls;
        LstWebUrls.Visibility = urls.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateWebUrlListHeight(urls.Length);

        BtnOpenWebHeader.IsEnabled = _webServer.IsRunning;
        BtnOpenWebHeader.Visibility = _configService.Current.AutoStartWebServer ? Visibility.Visible : Visibility.Collapsed;
        WebServerOptionsPanel.Visibility = _configService.Current.AutoStartWebServer ? Visibility.Visible : Visibility.Collapsed;
        BtnOpenWebConfig.IsEnabled = _webServer.IsRunning;
        BtnCopyWebUrl.IsEnabled = _webServer.IsRunning;
        TxtWebServerStatus.Text = _webServer.IsRunning ? text["webRunning"]
            : _configService.Current.AutoStartWebServer && _webServer.StatusMessage.StartsWith("Error", StringComparison.Ordinal)
                ? string.Format(text["webError"], _webServer.StatusMessage)
                : text["webStopped"];
        BtnCopyWebSessionCode.Content = _webServer.SessionCode is string code ? string.Format(text["webSessionCode"], code) : "";
        BtnCopyWebSessionCode.Visibility = _webServer.SessionCode is null ? Visibility.Collapsed : Visibility.Visible;
        TxtWebCertificateFingerprint.Text = _webServer.CertificateFingerprint is string fingerprint
            ? string.Format(text["webFingerprint"], fingerprint) : "";
        TxtWebCertificateFingerprint.Visibility = _webServer.CertificateFingerprint is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateWebQrCode(urls);
    }

    private void LstWebUrls_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_webServer is null)
            return;
        if (LstWebUrls.SelectedItem is string selectedUrl)
            UpdateWebQrCode(new[] { selectedUrl });
    }

    private void UpdateWebQrCode(string[] urls)
    {
        if (ImgWebQrCode is null || WebQrContainer is null)
            return;

        if (!_webServer.IsRunning || urls.Length == 0)
        {
            ImgWebQrCode.Source = null;
            WebQrContainer.Visibility = Visibility.Collapsed;
            return;
        }

        string preferred = LstWebUrls.SelectedItem as string
            ?? urls.FirstOrDefault(static u => !u.Contains("localhost", StringComparison.OrdinalIgnoreCase) && !u.Contains("127.0.0.1", StringComparison.Ordinal))
            ?? urls[0];

        string qrPayload = _webServer.SessionCode is string code && !string.IsNullOrWhiteSpace(code)
            ? $"{preferred.TrimEnd('/')}/?code={Uri.EscapeDataString(code)}"
            : preferred;

        ImgWebQrCode.Source = QrCodeGenerator.CreateQrImage(qrPayload);
        WebQrContainer.Visibility = ImgWebQrCode.Source is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CmbWebAccessMode_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await RunUiAsync("Web Access Mode", async () =>
        {
            UpdateVpnOptionsVisibility();
            if (!_suppressEvents && !_applyingWebSettings && CmbWebAccessMode.SelectedValue is string)
                await ApplyWebSettingsAsync(useEditedPort: false);
        });

    private async void CmbWebVpnInterface_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await RunUiAsync("Web VPN Interface", async () =>
        {
            if (!_suppressEvents && !_refreshingVpnInterfaces && !_applyingWebSettings &&
                CmbWebAccessMode.SelectedValue as string == "vpn" && CmbWebVpnInterface.SelectedValue is string)
                await ApplyWebSettingsAsync(useEditedPort: false);
        });

    private void UpdateVpnOptionsVisibility()
    {
        if (WebVpnPanel is not null && CmbWebAccessMode is not null)
            WebVpnPanel.Visibility = CmbWebAccessMode.SelectedValue as string == "vpn" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnRefreshVpnInterfaces_Click(object sender, RoutedEventArgs e) =>
        RefreshVpnInterfaces(CmbWebVpnInterface.SelectedValue as string);

    private void RefreshVpnInterfaces(string? selectedId)
    {
        _refreshingVpnInterfaces = true;
        try
        {
            CmbWebVpnInterface.ItemsSource = WebServerService.GetNetworkInterfaces();
            CmbWebVpnInterface.SelectedValue = selectedId;
        }
        catch (System.Net.NetworkInformation.NetworkInformationException ex)
        {
            CmbWebVpnInterface.ItemsSource = null;
            TxtStatusBarLeft.Text = string.Format(SettingsTranslations.ForLanguage(_uiLanguage)["webError"], ex.Message);
        }
        finally
        {
            _refreshingVpnInterfaces = false;
        }
    }

    private void UpdateWebUrlListHeight(int urlCount)
    {
        LstWebUrls.Height = urlCount == 0 ? 0 : double.NaN;
    }

    private async void ChkEnableWebServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _configService.SetWebServerEnabled(ChkEnableWebServer.IsChecked == true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ChkEnableWebServer.IsChecked = _configService.Current.AutoStartWebServer;
            TxtStatusBarLeft.Text = string.Format(SettingsTranslations.ForLanguage(_uiLanguage)["webSaveError"], ex.Message);
            return;
        }

        if (_configService.Current.AutoStartWebServer)
        {
            string? mode = CmbWebAccessMode.SelectedValue as string;
            string vpnId = CmbWebVpnInterface.SelectedValue as string ?? "";
            if (mode != _configService.Current.WebAccessMode ||
                (mode == "vpn" && (vpnId.Length == 0 || vpnId != _configService.Current.WebVpnInterfaceId)))
                await ApplyWebSettingsAsync(useEditedPort: false);
            else
                await _webServer.StartAsync();
        }
        else
        {
            await _webServer.StopAsync();
        }

        RefreshWebServerUi();
    }


    private void ChkEnableEddnUpload_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        _configService.Current.EnableEddnUpload = ChkEnableEddnUpload.IsChecked == true;
        _configService.Save();
        UpdateCommunitySyncVisibility();
        RefreshCommunitySyncStatus();
    }

    private void ChkEnableEdsmSync_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        _configService.Current.EnableEdsmSync = ChkEnableEdsmSync.IsChecked == true;
        _configService.Save();
        UpdateCommunitySyncVisibility();
        RefreshCommunitySyncStatus();
        if (_configService.Current.EnableEdsmSync && !string.IsNullOrWhiteSpace(_fileWatcher.State.CurrentSystem))
            _ = _communitySync.FetchEdsmDiscoveryAndFactionsAsync(_fileWatcher.State.CurrentSystem);
    }

    private void ChkEnableInaraSync_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        _configService.Current.EnableInaraSync = ChkEnableInaraSync.IsChecked == true;
        _configService.Save();
        UpdateCommunitySyncVisibility();
        RefreshCommunitySyncStatus();
    }

    private void UpdateCommunitySyncVisibility()
    {
        bool eddn = ChkEnableEddnUpload?.IsChecked == true;
        bool edsm = ChkEnableEdsmSync?.IsChecked == true;
        bool inara = ChkEnableInaraSync?.IsChecked == true;

        if (EdsmOptionsPanel is not null)
            EdsmOptionsPanel.Visibility = edsm ? Visibility.Visible : Visibility.Collapsed;
        if (InaraOptionsPanel is not null)
            InaraOptionsPanel.Visibility = inara ? Visibility.Visible : Visibility.Collapsed;
        if (CommunitySyncActionsPanel is not null)
            CommunitySyncActionsPanel.Visibility = (eddn || edsm || inara) ? Visibility.Visible : Visibility.Collapsed;
        if (BtnSaveCommunitySync is not null)
            BtnSaveCommunitySync.Visibility = (edsm || inara) ? Visibility.Visible : Visibility.Collapsed;
        if (BtnTestCommunitySync is not null)
            BtnTestCommunitySync.Visibility = (edsm || inara) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnSaveCommunitySync_Click(object sender, RoutedEventArgs e)
    {
        _configService.Current.EdsmCommanderName = TxtEdsmCommander.Text.Trim();
        _configService.Current.EdsmApiKey = TxtEdsmApiKey.Text.Trim();
        _configService.Current.InaraCommanderName = TxtInaraCommander.Text.Trim();
        _configService.Current.InaraApiKey = TxtInaraApiKey.Text.Trim();
        _configService.Save();
        RefreshCommunitySyncStatus();
        TxtStatusBarLeft.Text = SettingsTranslations.ForLanguage(_uiLanguage)["communitySyncSaved"];
    }

    private async void BtnTestCommunitySync_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Community Sync Test", async () =>
        {
            BtnSaveCommunitySync_Click(sender, e);
            var (_, summary) = await _communitySync.SyncCommanderAsync();
            TxtStatusBarLeft.Text = summary;
        });

    private void RefreshCommunitySyncStatus()
    {
        if (TxtCommunitySyncStatus is null)
            return;
        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        string status = string.IsNullOrWhiteSpace(_communitySync.LastSyncStatus)
            ? (_configService.Current.EnableEddnUpload ? "EDDN" : "—")
            : _communitySync.LastSyncStatus;
        TxtCommunitySyncStatus.Text = string.Format(text["communitySyncStatus"], _communitySync.SyncedEventsCount, status);
    }

    // =========================================================================
    // Frontier Companion API (cAPI — OAuth 2.0 + PKCE)
    // =========================================================================

    private void ChkEnableFrontierCapi_Click(object sender, RoutedEventArgs e)
    {
        SaveFrontierCapiConfig();
        RefreshFrontierCapiUi();
    }

    private void TxtFrontierCallbackPort_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_frontierCapi is null || TxtFrontierRedirectUri is null)
            return;
        TxtFrontierRedirectUri.Text = FrontierCapiService.PublicRedirectUri;
    }

    private void SaveFrontierCapiConfig()
    {
        _configService.Current.EnableFrontierCapi = ChkEnableFrontierCapi.IsChecked == true;
        _configService.Current.FrontierCapiClientId = (TxtFrontierClientId.Text ?? "").Trim();
        if (int.TryParse((TxtFrontierCallbackPort.Text ?? "").Trim(), out int port) && port is >= 1024 and <= 65535)
            _configService.Current.FrontierCapiCallbackPort = port;
        _configService.Save();
    }

    private void RefreshFrontierCapiUi()
    {
        if (FrontierCapiOptionsPanel is null || TxtFrontierCapiStatus is null)
            return;

        bool enabled = ChkEnableFrontierCapi.IsChecked == true;
        FrontierCapiOptionsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        TxtFrontierRedirectUri.Text = _frontierCapi.GetRedirectUri();

        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        bool connected = _frontierCapi.IsConnected;
        BtnFrontierSyncNow.IsEnabled = connected;
        BtnFrontierDisconnect.IsEnabled = connected;

        string baseState = connected ? text["frontierConnected"] : text["frontierDisconnected"];
        TxtFrontierCapiStatus.Text = string.IsNullOrWhiteSpace(_frontierCapi.LastStatus)
            ? baseState
            : $"{baseState} · {_frontierCapi.LastStatus}";
    }

    private async void BtnFrontierAuthorize_Click(object sender, RoutedEventArgs e)
    {
        SaveFrontierCapiConfig();
        BtnFrontierAuthorize.IsEnabled = false;
        try
        {
            var (ok, msg) = await _frontierCapi.AuthorizeAsync();
            TxtStatusBarLeft.Text = msg;
            RefreshFrontierCapiUi();
            if (ok)
            {
                var (_, syncSummary) = await _frontierCapi.SyncAllAsync(includeDockedEndpoints: _fileWatcher.State.Docked);
                TxtStatusBarLeft.Text = syncSummary;
                RefreshFrontierCapiUi();
            }
        }
        finally
        {
            BtnFrontierAuthorize.IsEnabled = true;
        }
    }

    private async void BtnFrontierSyncNow_Click(object sender, RoutedEventArgs e)
    {
        SaveFrontierCapiConfig();
        BtnFrontierSyncNow.IsEnabled = false;
        try
        {
            var (_, summary) = await _frontierCapi.SyncAllAsync(includeDockedEndpoints: _fileWatcher.State.Docked);
            TxtStatusBarLeft.Text = summary;
            RefreshFrontierCapiUi();
        }
        finally
        {
            BtnFrontierSyncNow.IsEnabled = _frontierCapi.IsConnected;
        }
    }

    private void BtnFrontierDisconnect_Click(object sender, RoutedEventArgs e)
    {
        _frontierCapi.Disconnect();
        RefreshFrontierCapiUi();
        TxtStatusBarLeft.Text = SettingsTranslations.ForLanguage(_uiLanguage)["frontierDisconnected"];
    }

    private void UpdateFrontierCapiDataUi(ShipState s)
    {
        if (CarrierCargoCard is not null && GridCarrierCargo is not null)
        {
            bool hasCargo = s.CarrierCargo.Count > 0;
            CarrierCargoCard.Visibility = hasCargo ? Visibility.Visible : Visibility.Collapsed;
            if (hasCargo && !ReferenceEquals(GridCarrierCargo.ItemsSource, s.CarrierCargo))
                GridCarrierCargo.ItemsSource = s.CarrierCargo;
        }

        if (CarrierMarketOrdersCard is not null && GridCarrierMarketOrders is not null)
        {
            bool hasOrders = s.CarrierMarketOrders.Count > 0;
            CarrierMarketOrdersCard.Visibility = hasOrders ? Visibility.Visible : Visibility.Collapsed;
            if (hasOrders && !ReferenceEquals(GridCarrierMarketOrders.ItemsSource, s.CarrierMarketOrders))
                GridCarrierMarketOrders.ItemsSource = s.CarrierMarketOrders;
        }

        if (CarrierServicesCard is not null && TxtCarrierServices is not null)
        {
            bool hasServices = s.CarrierServices.Count > 0;
            CarrierServicesCard.Visibility = hasServices ? Visibility.Visible : Visibility.Collapsed;
            if (hasServices)
                TxtCarrierServices.Text = string.Join(" · ", s.CarrierServices);
        }

        if (StoredShipsCard is not null && GridStoredShips is not null)
        {
            bool hasShips = s.StoredShips.Count > 0;
            StoredShipsCard.Visibility = hasShips ? Visibility.Visible : Visibility.Collapsed;
            if (hasShips && !ReferenceEquals(GridStoredShips.ItemsSource, s.StoredShips))
                GridStoredShips.ItemsSource = s.StoredShips;
        }

        if (StoredModulesCard is not null && GridStoredModules is not null)
        {
            bool hasModules = s.StoredModules.Count > 0;
            StoredModulesCard.Visibility = hasModules ? Visibility.Visible : Visibility.Collapsed;
            if (hasModules && !ReferenceEquals(GridStoredModules.ItemsSource, s.StoredModules))
                GridStoredModules.ItemsSource = s.StoredModules;
        }

        if (CommunityGoalsCard is not null && GridCommunityGoals is not null)
        {
            bool hasCgs = s.CommunityGoals.Count > 0;
            CommunityGoalsCard.Visibility = hasCgs ? Visibility.Visible : Visibility.Collapsed;
            if (hasCgs && !ReferenceEquals(GridCommunityGoals.ItemsSource, s.CommunityGoals))
                GridCommunityGoals.ItemsSource = s.CommunityGoals;
        }

        if (BtnOpenInaraSystem is not null)
            BtnOpenInaraSystem.Visibility = _configService.Current.EnableInaraSync ? Visibility.Visible : Visibility.Collapsed;
        if (ArdentMarketCard is not null)
            ArdentMarketCard.Visibility = _configService.Current.EnableArdentMarketApi ? Visibility.Visible : Visibility.Collapsed;
        if (BtnFetchNearestDssa is not null)
            BtnFetchNearestDssa.Visibility = _configService.Current.EnableEdastroDssa ? Visibility.Visible : Visibility.Collapsed;

        if (InaraProfileCard is not null && TxtInaraProfileSummary is not null)
        {
            if (_configService.Current.EnableInaraSync && s.InaraProfile is { } ip && !string.IsNullOrWhiteSpace(ip.CommanderName))
            {
                var parts = new List<string> { $"INARA · {ip.CommanderName}" };
                if (!string.IsNullOrWhiteSpace(ip.SquadronName))
                    parts.Add(string.IsNullOrWhiteSpace(ip.SquadronRank) ? ip.SquadronName : $"{ip.SquadronName} ({ip.SquadronRank})");
                if (!string.IsNullOrWhiteSpace(ip.PreferredAllegiance))
                    parts.Add(ip.PreferredAllegiance);
                TxtInaraProfileSummary.Text = string.Join("  ·  ", parts);
                InaraProfileCard.Visibility = Visibility.Visible;
            }
            else
            {
                InaraProfileCard.Visibility = Visibility.Collapsed;
            }
        }

        if (EliteBgsWatchlistCard is not null)
        {
            bool showBgs = _configService.Current.EnableEliteBgsWatchlist || s.WatchedBgsFactions.Count > 0;
            EliteBgsWatchlistCard.Visibility = showBgs ? Visibility.Visible : Visibility.Collapsed;
            if (GridEliteBgsWatchlist is not null && s.WatchedBgsFactions.Count > 0 &&
                !ReferenceEquals(GridEliteBgsWatchlist.ItemsSource, s.WatchedBgsFactions))
            {
                GridEliteBgsWatchlist.ItemsSource = s.WatchedBgsFactions;
            }
        }
    }

    // =========================================================================
    // External Integrations v0.3.0 (Per-API Opt-In Toggles & Handlers)
    // =========================================================================

    private void SyncIntegrationsUiFromConfig()
    {
        if (_suppressEvents)
            return;
        _suppressEvents = true;
        try
        {
            var cfg = _configService.Current;
            if (ChkEnableSpanshSystemData is not null) ChkEnableSpanshSystemData.IsChecked = cfg.EnableSpanshSystemData;
            if (ChkEnableEddnUpload is not null) ChkEnableEddnUpload.IsChecked = cfg.EnableEddnUpload;
            if (ChkEnableEdsmSync is not null) ChkEnableEdsmSync.IsChecked = cfg.EnableEdsmSync;
            if (ChkEnableInaraSync is not null) ChkEnableInaraSync.IsChecked = cfg.EnableInaraSync;
            if (ChkEnableFrontierCapi is not null) ChkEnableFrontierCapi.IsChecked = cfg.EnableFrontierCapi;
            UpdateCommunitySyncVisibility();
            RefreshFrontierCapiUi();
            LoadV3IntegrationsConfigToUi(cfg);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void LoadV3IntegrationsConfigToUi(AppConfig cfg)
    {
        if (ChkEdsmAnnounceFirstDiscovery is not null) ChkEdsmAnnounceFirstDiscovery.IsChecked = cfg.EdsmAnnounceFirstDiscovery;
        if (ChkEnableEliteBgsWatchlist is not null) ChkEnableEliteBgsWatchlist.IsChecked = cfg.EnableEliteBgsWatchlist;
        if (TxtEliteBgsFactions is not null) TxtEliteBgsFactions.Text = cfg.EliteBgsWatchedFactions ?? "";
        if (ChkEnableArdentMarketApi is not null) ChkEnableArdentMarketApi.IsChecked = cfg.EnableArdentMarketApi;
        if (ChkEnableEdastroDssa is not null) ChkEnableEdastroDssa.IsChecked = cfg.EnableEdastroDssa;
        if (ChkEnableCanonnUpload is not null) ChkEnableCanonnUpload.IsChecked = cfg.EnableCanonnUpload;
        if (ChkEnableDiscord is not null) ChkEnableDiscord.IsChecked = cfg.EnableDiscord;
        if (TxtDiscordWebhookUrl is not null) TxtDiscordWebhookUrl.Text = cfg.DiscordWebhookUrl ?? "";
        if (ChkDiscordWebhookElw is not null) ChkDiscordWebhookElw.IsChecked = cfg.DiscordWebhookOnElw;
        if (ChkDiscordWebhookCarrier is not null) ChkDiscordWebhookCarrier.IsChecked = cfg.DiscordWebhookOnCarrierJump;
        if (ChkDiscordWebhookBgs is not null) ChkDiscordWebhookBgs.IsChecked = cfg.DiscordWebhookOnBgsAlert;

        UpdateV3IntegrationsVisibility();
        RefreshV3IntegrationsStatus();
    }

    private void UpdateV3IntegrationsVisibility()
    {
        if (EliteBgsOptionsPanel is not null)
            EliteBgsOptionsPanel.Visibility = ChkEnableEliteBgsWatchlist?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (DiscordOptionsPanel is not null)
            DiscordOptionsPanel.Visibility = ChkEnableDiscord?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (EliteBgsWatchlistCard is not null)
            EliteBgsWatchlistCard.Visibility = ChkEnableEliteBgsWatchlist?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshV3IntegrationsStatus()
    {
        if (TxtDiscordStatus is not null)
            TxtDiscordStatus.Text = _discord.LastStatus;
        if (TxtEliteBgsSettingsStatus is not null)
            TxtEliteBgsSettingsStatus.Text = _eliteBgsWatchlist.LastStatus;
        if (TxtEliteBgsWatchlistStatus is not null)
            TxtEliteBgsWatchlistStatus.Text = _eliteBgsWatchlist.LastStatus;
    }

    private void ChkEdsmAnnounceFirstDiscovery_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _configService.Current.EdsmAnnounceFirstDiscovery = ChkEdsmAnnounceFirstDiscovery.IsChecked == true;
        _configService.Save();
    }

    private async Task RunUiAsync(string actionName, Func<Task> action)
    {
        AppDiagnosticsLogger.RecordBreadcrumb("UI", actionName);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Error("UI", $"Unhandled error in {actionName}.", ex);
            if (TxtStatusBarLeft is not null)
                TxtStatusBarLeft.Text = $"⚠ {actionName}: {ex.Message}";
        }
    }

    private async void BtnFetchInaraProfile_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Inara Profile", async () =>
        {
            var (ok, profile, msg) = await _communitySync.FetchInaraCommanderProfileAsync();
            if (TxtInaraReadStatus is not null)
                TxtInaraReadStatus.Text = ok && profile is not null
                    ? $"{profile.CommanderName} · {profile.SquadronName} ({profile.PreferredAllegiance})"
                    : msg;
            TxtStatusBarLeft.Text = msg;
        });

    private async void BtnFetchInaraCgs_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Inara CGs", async () =>
        {
            var (_, _, msg) = await _communitySync.FetchInaraCommunityGoalsAsync();
            if (TxtInaraReadStatus is not null)
                TxtInaraReadStatus.Text = msg;
            TxtStatusBarLeft.Text = msg;
        });

    private void BtnOpenInaraCommanderProfile_Click(object sender, RoutedEventArgs e)
    {
        string url = _fileWatcher.State.InaraProfile?.InaraUrl ?? "";
        if (string.IsNullOrWhiteSpace(url))
            url = CommunitySyncService.BuildInaraDeepLink("commander", _fileWatcher.State.CommanderName);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void BtnOpenInaraSystem_Click(object sender, RoutedEventArgs e)
    {
        string sys = _fileWatcher.State.CurrentSystem;
        if (string.IsNullOrWhiteSpace(sys) || sys == "---")
            return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = CommunitySyncService.BuildInaraDeepLink("system", sys),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            TxtStatusBarLeft.Text = ex.Message;
        }
    }

    private void ChkEnableEliteBgsWatchlist_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _configService.Current.EnableEliteBgsWatchlist = ChkEnableEliteBgsWatchlist.IsChecked == true;
        _configService.Save();
        UpdateV3IntegrationsVisibility();
        if (_configService.Current.EnableEliteBgsWatchlist)
            _ = _eliteBgsWatchlist.RefreshAsync();
    }

    private async void BtnSaveEliteBgs_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("EliteBGS Save", async () =>
        {
            _configService.Current.EliteBgsWatchedFactions = (TxtEliteBgsFactions.Text ?? "").Trim();
            _configService.Save();
            var (_, _, msg) = await _eliteBgsWatchlist.RefreshAsync();
            RefreshV3IntegrationsStatus();
            TxtStatusBarLeft.Text = msg;
        });

    private async void BtnRefreshEliteBgs_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("EliteBGS Refresh", async () =>
        {
            var (_, _, msg) = await _eliteBgsWatchlist.RefreshAsync();
            RefreshV3IntegrationsStatus();
            TxtStatusBarLeft.Text = msg;
        });

    private void UpdateEliteBgsWatchlistUi(IReadOnlyList<EliteBgsFactionWatchItem> items)
    {
        if (GridEliteBgsWatchlist is not null)
            GridEliteBgsWatchlist.ItemsSource = items;
        if (EliteBgsWatchlistCard is not null)
            EliteBgsWatchlistCard.Visibility = (_configService.Current.EnableEliteBgsWatchlist || items.Count > 0)
                ? Visibility.Visible
                : Visibility.Collapsed;
        RefreshV3IntegrationsStatus();
    }

    private void ChkEnableArdentMarketApi_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _configService.Current.EnableArdentMarketApi = ChkEnableArdentMarketApi.IsChecked == true;
        _configService.Save();
    }

    private async void BtnSearchArdentMarket_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Ardent Market", async () =>
        {
            string commodity = (TxtArdentCommodity?.Text ?? "").Trim();
            string mode = (CmbArdentMode?.SelectedItem as ComboBoxItem)?.Tag as string ?? "sell";
            if (TxtArdentMarketStatus is not null)
                TxtArdentMarketStatus.Text = "Ardent Insight...";

            var (ok, offers, msg) = await _ardentMarket.SearchCommodityAsync(commodity, mode);
            if (TxtArdentMarketStatus is not null)
                TxtArdentMarketStatus.Text = msg;
            if (GridArdentMarket is not null)
            {
                GridArdentMarket.ItemsSource = ok && offers.Count > 0 ? offers : null;
                GridArdentMarket.Visibility = ok && offers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        });

    private void ChkEnableEdastroDssa_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _configService.Current.EnableEdastroDssa = ChkEnableEdastroDssa.IsChecked == true;
        _configService.Save();
    }

    private void ChkEnableCanonnUpload_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _configService.Current.EnableCanonnUpload = ChkEnableCanonnUpload.IsChecked == true;
        _configService.Save();
    }

    private async void BtnSearchSpanshBodies_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Spansh Bodies", async () =>
        {
            string query = (TxtSpanshBodyQuery?.Text ?? "").Trim();
            var st = _fileWatcher.State;
            if (TxtSpanshDeepSearchStatus is not null)
                TxtSpanshDeepSearchStatus.Text = "Spansh Bodies...";
            var (ok, results, msg) = await _spanshSystemDataService.SearchBodiesAsync(query, st.CurrentSystem, st.StarPos);
            if (TxtSpanshDeepSearchStatus is not null)
                TxtSpanshDeepSearchStatus.Text = msg;
            if (GridSpanshBodiesSearch is not null)
            {
                GridSpanshBodiesSearch.ItemsSource = ok && results.Count > 0 ? results : null;
                GridSpanshBodiesSearch.Visibility = ok && results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        });

    private async void BtnSearchSpanshCarriers_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Spansh Carriers", async () =>
        {
            string service = (CmbSpanshCarrierService?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Vista Genomics";
            var st = _fileWatcher.State;
            if (TxtSpanshDeepSearchStatus is not null)
                TxtSpanshDeepSearchStatus.Text = "Spansh Fleet Carriers...";
            var (ok, results, msg) = await _spanshSystemDataService.SearchFleetCarriersAsync(service, st.CurrentSystem, st.StarPos);
            if (TxtSpanshDeepSearchStatus is not null)
                TxtSpanshDeepSearchStatus.Text = msg;
            if (GridSpanshCarriersSearch is not null)
            {
                GridSpanshCarriersSearch.ItemsSource = ok && results.Count > 0 ? results : null;
                GridSpanshCarriersSearch.Visibility = ok && results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        });

    private async void BtnFetchNearestDssa_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("EDAstro DSSA", async () =>
        {
            if (TxtSpanshDeepSearchStatus is not null)
                TxtSpanshDeepSearchStatus.Text = "EDAstro DSSA...";
            var (ok, carriers, msg) = await _edastroCanonn.GetNearestDssaCarriersAsync();
            if (TxtSpanshDeepSearchStatus is not null)
                TxtSpanshDeepSearchStatus.Text = msg;
            if (GridNearestDssa is not null)
            {
                GridNearestDssa.ItemsSource = ok && carriers.Count > 0 ? carriers : null;
                GridNearestDssa.Visibility = ok && carriers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        });

    private void ChkEnableDiscord_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _configService.Current.EnableDiscord = ChkEnableDiscord.IsChecked == true;
        _configService.Save();
        UpdateV3IntegrationsVisibility();
        if (_configService.Current.EnableDiscord)
            _ = _discord.RefreshPresenceAsync();
        else
            _discord.DisconnectIpc();
    }

    private void ChkDiscordWebhookEvents_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _configService.Current.DiscordWebhookOnElw = ChkDiscordWebhookElw.IsChecked == true;
        _configService.Current.DiscordWebhookOnCarrierJump = ChkDiscordWebhookCarrier.IsChecked == true;
        _configService.Current.DiscordWebhookOnBgsAlert = ChkDiscordWebhookBgs.IsChecked == true;
        _configService.Save();
    }

    private void BtnSaveDiscordWebhook_Click(object sender, RoutedEventArgs e)
    {
        _configService.Current.DiscordWebhookUrl = (TxtDiscordWebhookUrl.Text ?? "").Trim();
        _configService.Save();
        TxtStatusBarLeft.Text = "Discord Webhook URL saved.";
    }

    private async void BtnTestDiscordWebhook_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Discord Webhook Test", async () =>
        {
            BtnSaveDiscordWebhook_Click(sender, e);
            var (_, msg) = await _discord.SendWebhookAsync(
                $"🛰 {HttpClientHeaderDefaults.ProductName} v{HttpClientHeaderDefaults.ProductVersion} Test",
                $"Connected for **{_fileWatcher.State.CommanderName}** in `{_fileWatcher.State.CurrentSystem}`.");
            RefreshV3IntegrationsStatus();
            TxtStatusBarLeft.Text = msg;
        });

    private void ApplyEdsmDiscoveryIntel(EdsmSystemDiscoveryIntel intel)
    {
        if (TxtEdsmDiscoveryBadge is null)
            return;
        if (!string.Equals(intel.SystemName, _fileWatcher.State.CurrentSystem, StringComparison.OrdinalIgnoreCase))
            return;

        TxtEdsmDiscoveryBadge.Text = intel.DiscoveryBadge;
        TxtEdsmDiscoveryBadge.SetResourceReference(
            TextBlock.ForegroundProperty,
            intel.IsKnownInEdsm ? "HudSecondaryBrush" : "HudOkBrush");
        TxtEdsmDiscoveryBadge.Visibility = Visibility.Visible;

        if (!intel.IsKnownInEdsm && _configService.Current.EdsmAnnounceFirstDiscovery)
        {
            string msg = _uiLanguage switch
            {
                "es-ES" => $"Sistema virgen no registrado en EDSM: {intel.SystemName}.",
                "fr-FR" => $"Système inconnu dans EDSM : {intel.SystemName}.",
                "de-DE" => $"Unentdecktes System in EDSM: {intel.SystemName}.",
                "pt-BR" => $"Sistema desconhecido no EDSM: {intel.SystemName}.",
                "ru-RU" => $"Неоткрытая система в EDSM: {intel.SystemName}.",
                _ => $"Undiscovered system in EDSM: {intel.SystemName}."
            };
            _alertSpeechService.SpeakCustomText(msg);
        }
    }

    private void BtnExitApp_Click(object sender, RoutedEventArgs e)
    {
        ExitApplicationDefinitively();
    }

}
