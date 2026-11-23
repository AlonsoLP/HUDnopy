// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/MainWindow.V6Features.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Desktop UI handlers for v0.5.0 features: Click-Through Overlay,
//              Custom Control Deck Editor, Offline Whisper STT, and Cockpit Radio.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using HUDnopy.Models;
using HUDnopy.Services;
using HUDnopy.UI;

namespace HUDnopy;

public partial class MainWindow
{
    private const int OverlayHotKeyId = 0x484F;
    private bool _overlayHotkeyRegistered;

    // =========================================================================
    // Block 1 (#2): Click-Through Tactical Overlay
    // =========================================================================

    private void BtnToggleOverlay_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_overlayWindow is not null)
            {
                CloseOverlayWindow();
                return;
            }

            _overlayWindow = new HudOverlayWindow(_configService);
            _overlayWindow.Closed += (_, _) =>
            {
                UnregisterOverlayHotkey();
                _overlayWindow = null;
                if (BtnToggleOverlay is not null)
                {
                    BtnToggleOverlay.SetResourceReference(BackgroundProperty, "HudPanelAltBrush");
                    BtnToggleOverlay.SetResourceReference(ForegroundProperty, "HudTextBrush");
                }
                _configService.Save();
            };
            _overlayWindow.UpdateTelemetry(_fileWatcher.State, _uiLanguage);
            _overlayWindow.Show();
            RegisterOverlayHotkey();

            BtnToggleOverlay.SetResourceReference(BackgroundProperty, "HudAccentBrush");
            BtnToggleOverlay.SetResourceReference(ForegroundProperty, "HudBgBrush");
            TxtStatusBarLeft.Text = $"🪟 Overlay active ({(_overlayWindow.IsClickThrough ? "Click-Through Locked" : "Unlocked/Drag")} · {_configService.Current.OverlayToggleHotkey})";
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Error("Overlay", "Failed to toggle Click-Through Overlay.", ex);
            App.ShowExceptionDialog(ex, "HUDnopy.Overlay", _configService.Current, isFatal: false);
        }
    }

    private void ToggleOverlayClickThroughFromHotkey()
    {
        if (_overlayWindow is null)
            return;
        _overlayWindow.SetClickThrough(!_overlayWindow.IsClickThrough);
        _configService.Save();
        TxtStatusBarLeft.Text = _overlayWindow.IsClickThrough
            ? "🔒 Overlay locked (Click-Through enabled)"
            : "🔓 Overlay unlocked (Drag to reposition)";
    }

    private void CloseOverlayWindow()
    {
        UnregisterOverlayHotkey();
        if (_overlayWindow is not null)
        {
            try { _overlayWindow.Close(); } catch { }
            _overlayWindow = null;
        }
    }

    private void RegisterOverlayHotkey()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            _hotkeyHwndSource ??= HwndSource.FromHwnd(hwnd);
            if (_hotkeyHwndSource is null) return;
            _hotkeyHwndSource.RemoveHook(OverlayHotKeyWndProc);
            _hotkeyHwndSource.AddHook(OverlayHotKeyWndProc);
            if (_overlayHotkeyRegistered)
                UnregisterHotKey(hwnd, OverlayHotKeyId);
            var (mods, vk) = ParseGlobalHotkey(_configService.Current.OverlayToggleHotkey);
            _overlayHotkeyRegistered = RegisterHotKey(hwnd, OverlayHotKeyId, mods, vk);
        }
        catch
        {
            _overlayHotkeyRegistered = false;
        }
    }

    private void UnregisterOverlayHotkey()
    {
        try
        {
            if (_overlayHotkeyRegistered)
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    UnregisterHotKey(hwnd, OverlayHotKeyId);
                _overlayHotkeyRegistered = false;
            }
            _hotkeyHwndSource?.RemoveHook(OverlayHotKeyWndProc);
        }
        catch { }
    }

    private IntPtr OverlayHotKeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == OverlayHotKeyId)
        {
            handled = true;
            ToggleOverlayClickThroughFromHotkey();
        }
        return IntPtr.Zero;
    }

    // =========================================================================
    // Block 3: Custom Control Deck Editor now lives in Settings -> Botonera (UI/MainWindow.DeckEditor.cs)
    // =========================================================================

    // =========================================================================
    // Block 4 (#4): Offline Whisper.cpp STT Settings
    // =========================================================================

    private void CmbAiSttEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _configService is null || CmbAiSttEngine?.SelectedValue is not string engine)
            return;
        _configService.Current.AiSttEngine = engine;
        _configService.Save();
        if (!string.Equals(engine, "whisper_local", StringComparison.OrdinalIgnoreCase))
            _whisperStt.StopResidentServer();
        RefreshWhisperStatusUi();
    }

    private void CmbWhisperModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _configService is null || CmbWhisperModel?.SelectedValue is not string modelId)
            return;
        _configService.Current.WhisperModelId = modelId;
        _configService.Save();
        _whisperStt.StopResidentServer();
        RefreshWhisperStatusUi();
    }

    internal void SetStatusBarMessage(string message)
    {
        if (TxtStatusBarLeft is null || string.IsNullOrWhiteSpace(message))
            return;
        TxtStatusBarLeft.Text = message;
        TxtStatusBarLeft.ToolTip = message;
        if (message.StartsWith("⚠", StringComparison.Ordinal))
            TxtStatusBarLeft.SetResourceReference(TextBlock.ForegroundProperty, "HudDangerBrush");
        else if (message.StartsWith("✓", StringComparison.Ordinal) || message.StartsWith("⬆", StringComparison.Ordinal))
            TxtStatusBarLeft.SetResourceReference(TextBlock.ForegroundProperty, "HudOkBrush");
        else
            TxtStatusBarLeft.SetResourceReference(TextBlock.ForegroundProperty, "HudMutedBrush");
    }

    private async void BtnInstallWhisper_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Whisper Offline Install", async () =>
        {
            if (BtnInstallWhisper is null)
                return;

            BtnInstallWhisper.IsEnabled = false;
            string modelId = _configService.Current.WhisperModelId;
            var progress = new Progress<string>(msg => SetStatusBarMessage($"🎤 Whisper: {msg}"));
            try
            {
                var (ok, msg) = await _whisperStt.EnsureInstalledAsync(modelId, progress);
                SetStatusBarMessage(ok ? $"✓ {msg}" : $"⚠ {msg}");
            }
            finally
            {
                BtnInstallWhisper.IsEnabled = true;
            }
        });

    private void RefreshWhisperStatusUi()
    {
        if (BtnInstallWhisper is null || _whisperStt is null)
            return;
        string modelId = _configService.Current.WhisperModelId;
        bool ready = _whisperStt.IsEngineInstalled() && _whisperStt.IsModelInstalled(modelId);
        BtnInstallWhisper.Content = ready ? $"✓ Whisper ({modelId})" : "⬇ Install Whisper";
    }

    // =========================================================================
    // Block 5: Cockpit Radio (NAudio streaming + COVAS Auto-Ducking)
    // =========================================================================

    private void RefreshCockpitRadioStationsUi()
    {
        if (CmbRadioStation is null || _cockpitRadio is null)
            return;

        bool prevSuppress = _suppressEvents;
        _suppressEvents = true;
        CmbRadioStation.Items.Clear();
        foreach (var st in _cockpitRadio.GetAllStations())
            CmbRadioStation.Items.Add(new ComboBoxItem { Content = st.Name, Tag = st.Id });
        CmbRadioStation.SelectedValue = _configService.Current.RadioLastStationId;
        if (CmbRadioStation.SelectedIndex < 0 && CmbRadioStation.Items.Count > 0)
            CmbRadioStation.SelectedIndex = 0;
        _suppressEvents = prevSuppress;
        RefreshCockpitRadioUi(_cockpitRadio.GetStatus());
    }

    private void RefreshCockpitRadioUi(RadioStatusSnapshot status)
    {
        if (BtnRadioPlayStop is not null)
            BtnRadioPlayStop.Content = status.IsPlaying ? "■ Stop" : "▶ Play";
        if (TxtRadioStatus is not null)
            TxtRadioStatus.Text = status.IsDucked ? $"{status.StatusMessage} (Ducked)" : status.StatusMessage;
        if (SldRadioVolume is not null && Math.Abs(SldRadioVolume.Value - status.VolumePercent) >= 1)
        {
            bool prev = _suppressEvents;
            _suppressEvents = true;
            SldRadioVolume.Value = status.VolumePercent;
            _suppressEvents = prev;
        }
    }

    private async void CmbRadioStation_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await RunUiAsync("Cockpit Radio Station", async () =>
        {
            if (_suppressEvents || _cockpitRadio is null || CmbRadioStation?.SelectedValue is not string stationId)
                return;
            _configService.Current.RadioLastStationId = stationId;
            _configService.Save();
            if (_cockpitRadio.GetStatus().IsPlaying)
            {
                await _cockpitRadio.PlayAsync(stationId);
                await _webServer.BroadcastStateAsync();
            }
        });

    private async void BtnRadioPlayStop_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Cockpit Radio Play/Stop", async () =>
        {
            if (_cockpitRadio is null)
                return;
            if (_cockpitRadio.GetStatus().IsPlaying)
            {
                _cockpitRadio.Stop();
            }
            else
            {
                string? stationId = CmbRadioStation?.SelectedValue as string;
                await _cockpitRadio.PlayAsync(stationId);
            }
            await _webServer.BroadcastStateAsync();
        });

    private void SldRadioVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || _cockpitRadio is null)
            return;
        _cockpitRadio.SetVolumePercent((int)Math.Round(e.NewValue));
    }

    private void ChkRadioAutoDuck_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _configService is null || ChkRadioAutoDuck is null)
            return;
        _configService.Current.RadioAutoDuckEnabled = ChkRadioAutoDuck.IsChecked == true;
        _configService.Save();
    }

    // =========================================================================
    // Block 2 (#3): One-Click Windows CurrentUser\Root HTTPS Trust for PWA
    // =========================================================================

    private void RefreshWebCertificateTrustUi()
    {
        if (BtnTrustWebCertificate is null || _webServer is null)
            return;

        if (!_webServer.IsRunning)
        {
            BtnTrustWebCertificate.IsEnabled = false;
            BtnTrustWebCertificate.Content = "🔒 Trust HTTPS Cert (PWA)";
            return;
        }

        BtnTrustWebCertificate.IsEnabled = true;
        bool trusted = _webServer.IsCertificateTrusted;
        BtnTrustWebCertificate.Content = trusted ? "🔓 Remove Trusted HTTPS Cert" : "🔒 Trust HTTPS Cert (PWA)";
    }

    private async void BtnTrustWebCertificate_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Trust HTTPS Certificate", () =>
        {
            bool ok = _webServer.IsCertificateTrusted
                ? _webServer.TryUntrustCertificate(out string msg)
                : _webServer.TryTrustCertificate(out msg);
            SetStatusBarMessage(ok ? $"✓ {msg}" : $"⚠ {msg}");
            RefreshWebCertificateTrustUi();
            return Task.CompletedTask;
        });
}
