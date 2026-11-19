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

    private async void CmbWebAccessMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateVpnOptionsVisibility();
        if (!_suppressEvents && !_applyingWebSettings && CmbWebAccessMode.SelectedValue is string)
            await ApplyWebSettingsAsync(useEditedPort: false);
    }

    private async void CmbWebVpnInterface_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressEvents && !_refreshingVpnInterfaces && !_applyingWebSettings &&
            CmbWebAccessMode.SelectedValue as string == "vpn" && CmbWebVpnInterface.SelectedValue is string)
            await ApplyWebSettingsAsync(useEditedPort: false);
    }

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

    private async void BtnTestCommunitySync_Click(object sender, RoutedEventArgs e)
    {
        BtnSaveCommunitySync_Click(sender, e);
        var (_, summary) = await _communitySync.SyncCommanderAsync();
        TxtStatusBarLeft.Text = summary;
    }

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

        if (StoredShipsCard is not null && GridStoredShips is not null)
        {
            bool hasShips = s.StoredShips.Count > 0;
            StoredShipsCard.Visibility = hasShips ? Visibility.Visible : Visibility.Collapsed;
            if (hasShips && !ReferenceEquals(GridStoredShips.ItemsSource, s.StoredShips))
                GridStoredShips.ItemsSource = s.StoredShips;
        }

        if (CommunityGoalsCard is not null && GridCommunityGoals is not null)
        {
            bool hasCgs = s.CommunityGoals.Count > 0;
            CommunityGoalsCard.Visibility = hasCgs ? Visibility.Visible : Visibility.Collapsed;
            if (hasCgs && !ReferenceEquals(GridCommunityGoals.ItemsSource, s.CommunityGoals))
                GridCommunityGoals.ItemsSource = s.CommunityGoals;
        }
    }

    private void BtnExitApp_Click(object sender, RoutedEventArgs e)
    {
        ExitApplicationDefinitively();
    }

}
