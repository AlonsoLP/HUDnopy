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
    private System.Windows.Threading.DispatcherTimer? _radioVolumeSaveTimer;

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
            bool hotkeyOk = RegisterOverlayHotkey();

            BtnToggleOverlay.SetResourceReference(BackgroundProperty, "HudAccentBrush");
            BtnToggleOverlay.SetResourceReference(ForegroundProperty, "HudBgBrush");
            string overlayMode = _overlayWindow.IsClickThrough ? "Click-Through Locked" : "Unlocked/Drag";
            if (hotkeyOk)
                TxtStatusBarLeft.Text = $"🪟 Overlay active ({overlayMode} · {_configService.Current.OverlayToggleHotkey})";
            else
                SetStatusBarMessage($"⚠ Overlay active ({overlayMode}), but hotkey {_configService.Current.OverlayToggleHotkey} could not be registered (already in use by another app).");
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

    private bool RegisterOverlayHotkey()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return false;
            _hotkeyHwndSource ??= HwndSource.FromHwnd(hwnd);
            if (_hotkeyHwndSource is null) return false;
            _hotkeyHwndSource.RemoveHook(OverlayHotKeyWndProc);
            _hotkeyHwndSource.AddHook(OverlayHotKeyWndProc);
            if (_overlayHotkeyRegistered)
                UnregisterHotKey(hwnd, OverlayHotKeyId);
            var (mods, vk) = ParseGlobalHotkey(_configService.Current.OverlayToggleHotkey);
            _overlayHotkeyRegistered = RegisterHotKey(hwnd, OverlayHotKeyId, mods, vk);
            if (!_overlayHotkeyRegistered)
                AppDiagnosticsLogger.Warn("Overlay", $"Global hotkey '{_configService.Current.OverlayToggleHotkey}' could not be registered (already in use).");
            return _overlayHotkeyRegistered;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("Overlay", $"Global hotkey registration failed: {ex.Message}");
            _overlayHotkeyRegistered = false;
            return false;
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

        // Persist the volume once the slider settles (debounced) instead of writing config.json on every tick.
        _radioVolumeSaveTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _radioVolumeSaveTimer.Tick -= RadioVolumeSaveTimer_Tick;
        _radioVolumeSaveTimer.Tick += RadioVolumeSaveTimer_Tick;
        _radioVolumeSaveTimer.Stop();
        _radioVolumeSaveTimer.Start();
    }

    private void RadioVolumeSaveTimer_Tick(object? sender, EventArgs e)
    {
        _radioVolumeSaveTimer?.Stop();
        _configService.Save();
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

    // =========================================================================
    // Integrated Touchscreen Table Row Detail HUD Popup
    // =========================================================================

    private string _currentRowDetailClipboardText = "";
    private System.Windows.Controls.DataGrid? _currentRowDetailGrid;
    private int _currentRowDetailIndex = -1;
    private long _lastRowDetailClosedTick;

    private void OnAnyDataGridRowTapped(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RowDetailOverlay?.Visibility == Visibility.Visible)
            return;
        if (Environment.TickCount64 - _lastRowDetailClosedTick < 250)
            return;
        if (e.OriginalSource is not DependencyObject source)
            return;

        // Ignore clicks on interactive controls embedded inside cells or headers or inside the overlay itself
        DependencyObject? cur = source;
        DataGridRow? row = null;
        while (cur is not null)
        {
            if (ReferenceEquals(cur, RowDetailOverlay))
                return;
            if (cur is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.Primitives.ScrollBar)
                return;
            if (cur is System.Windows.Controls.Primitives.DataGridColumnHeader)
                return;
            if (row is null && cur is DataGridRow dgr)
                row = dgr;
            if (cur is System.Windows.Controls.DataGrid grid && row is not null)
            {
                int idx = grid.Items.IndexOf(row.Item);
                ShowDataGridRowDetailOverlay(grid, row.Item, idx);
                return;
            }
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
    }

    private void ShowDataGridRowDetailOverlay(System.Windows.Controls.DataGrid grid, object? item, int itemIndex = -1)
    {
        if (item is null || RowDetailOverlay is null || RowDetailFieldsPanel is null)
            return;

        _currentRowDetailGrid = grid;
        int totalItems = grid.Items.Count;
        _currentRowDetailIndex = itemIndex >= 0 ? itemIndex : grid.Items.IndexOf(item);
        if (_currentRowDetailIndex < 0)
            _currentRowDetailIndex = 0;

        if (TxtRowDetailCounter is not null)
            TxtRowDetailCounter.Text = totalItems > 0 ? $"{_currentRowDetailIndex + 1} / {totalItems}" : "1 / 1";
        if (BtnPrevRowDetail is not null)
            BtnPrevRowDetail.IsEnabled = _currentRowDetailIndex > 0;
        if (BtnNextRowDetail is not null)
            BtnNextRowDetail.IsEnabled = _currentRowDetailIndex + 1 < totalItems;

        var fields = new List<(string Label, string Value)>();
        foreach (var col in grid.Columns)
        {
            string header = col.Header?.ToString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(header))
                continue;

            string val = ExtractColumnDisplayValue(col, item);
            if (string.IsNullOrWhiteSpace(val))
                val = "—";
            fields.Add((header, val));
        }

        // Append extra rich detail fields when available on specific models
        if (EvaluatePropertyPath(item, "Body") is string bodyText && !string.IsNullOrWhiteSpace(bodyText))
        {
            fields.Add(("Contenido", bodyText.Trim()));
        }
        if (item is DiagnosticIssueEntry diagIssue && !string.IsNullOrWhiteSpace(diagIssue.Details))
        {
            fields.Add(("Traza completa", diagIssue.Details));
        }

        if (fields.Count == 0)
            return;

        // Pick primary title (skip purely numeric '#' index column if there's a second column)
        string title = fields[0].Value;
        if (fields.Count > 1 && (fields[0].Label == "#" || int.TryParse(fields[0].Value, out _)))
            title = $"{fields[0].Label}{fields[0].Value} · {fields[1].Value}";

        string category = TxtActiveSectionTitle?.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(TxtActiveSectionTitle.Text)
            ? TxtActiveSectionTitle.Text.ToUpperInvariant()
            : "DETALLE DEL REGISTRO";

        TxtRowDetailCategory.Text = category;
        TxtRowDetailTitle.Text = title;
        RowDetailFieldsPanel.Children.Clear();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{category}: {title}");

        for (int i = 0; i < fields.Count; i++)
        {
            var (label, value) = fields[i];
            sb.AppendLine($"{label}: {value}");

            bool isLong = value.Length > 48 || value.Contains('\n');
            if (!isLong && i + 1 < fields.Count && fields[i + 1].Value.Length <= 48 && !fields[i + 1].Value.Contains('\n'))
            {
                var pairGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                pairGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                pairGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                pairGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var card1 = BuildRowDetailFieldCard(label, value);
                Grid.SetColumn(card1, 0);
                pairGrid.Children.Add(card1);

                var (label2, value2) = fields[i + 1];
                sb.AppendLine($"{label2}: {value2}");
                var card2 = BuildRowDetailFieldCard(label2, value2);
                Grid.SetColumn(card2, 2);
                pairGrid.Children.Add(card2);

                RowDetailFieldsPanel.Children.Add(pairGrid);
                i++;
            }
            else
            {
                var card = BuildRowDetailFieldCard(label, value);
                card.Margin = new Thickness(0, 0, 0, 8);
                RowDetailFieldsPanel.Children.Add(card);
            }
        }

        _currentRowDetailClipboardText = sb.ToString().Trim();
        RowDetailOverlay.Visibility = Visibility.Visible;
    }

    private Border BuildRowDetailFieldCard(string label, string value)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            Foreground = (System.Windows.Media.Brush)FindResource("HudMutedBrush"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 3)
        });
        stack.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = (System.Windows.Media.Brush)FindResource("HudTextBrush"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("HudPanelAltBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("HudBorderBrush"),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(12, 9, 12, 9),
            Child = stack
        };
    }

    private static string ExtractColumnDisplayValue(DataGridColumn column, object item)
    {
        if (column is DataGridBoundColumn boundCol && boundCol.Binding is System.Windows.Data.Binding binding && binding.Path is not null)
        {
            object? raw = EvaluatePropertyPath(item, binding.Path.Path);
            if (raw is null)
                return "";
            if (!string.IsNullOrWhiteSpace(binding.StringFormat))
            {
                try
                {
                    return string.Format(System.Globalization.CultureInfo.CurrentCulture, binding.StringFormat, raw);
                }
                catch
                {
                    return raw.ToString() ?? "";
                }
            }
            return raw.ToString() ?? "";
        }

        // Handle template columns (scoopable, discovered, mapped)
        string? key = TableHeader.GetKey(column);
        if (key == "scoopable")
            return EvaluatePropertyPath(item, "FuelStarIndicator")?.ToString() ?? "";
        if (key == "discovered")
            return EvaluatePropertyPath(item, "Discovery")?.ToString() ?? EvaluatePropertyPath(item, "Body.DiscoveryIndicator")?.ToString() ?? "";
        if (key == "mapped")
            return EvaluatePropertyPath(item, "Mapping")?.ToString() ?? EvaluatePropertyPath(item, "Body.MappingIndicator")?.ToString() ?? "";

        return "";
    }

    private static object? EvaluatePropertyPath(object? target, string? path)
    {
        if (target is null || string.IsNullOrWhiteSpace(path))
            return null;

        object? current = target;
        foreach (string part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is null)
                return null;
            var prop = current.GetType().GetProperty(part, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (prop is null)
                return null;
            current = prop.GetValue(current);
        }
        return current;
    }

    internal void StepRowDetail(int delta)
    {
        if (_currentRowDetailGrid is null || _currentRowDetailGrid.Items.Count == 0)
            return;

        int nextIndex = Math.Clamp(_currentRowDetailIndex + delta, 0, _currentRowDetailGrid.Items.Count - 1);
        if (nextIndex == _currentRowDetailIndex)
            return;

        object? nextItem = _currentRowDetailGrid.Items[nextIndex];
        if (nextItem is null)
            return;

        _currentRowDetailGrid.SelectedIndex = nextIndex;
        if (!ReferenceEquals(_currentRowDetailGrid, GridExplorationBodies))
            _currentRowDetailGrid.ScrollIntoView(nextItem);
        ShowDataGridRowDetailOverlay(_currentRowDetailGrid, nextItem, nextIndex);
    }

    private void BtnPrevRowDetail_Click(object sender, RoutedEventArgs e) => StepRowDetail(-1);

    private void BtnNextRowDetail_Click(object sender, RoutedEventArgs e) => StepRowDetail(1);

    internal void CloseRowDetailOverlay()
    {
        _lastRowDetailClosedTick = Environment.TickCount64;
        if (RowDetailOverlay is not null)
            RowDetailOverlay.Visibility = Visibility.Collapsed;
    }

    private void CloseRowDetailOverlay_Click(object sender, RoutedEventArgs e) => CloseRowDetailOverlay();

    private void RowDetailOverlayBackdrop_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void RowDetailOverlayBackdrop_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseRowDetailOverlay();
    }

    private void RowDetailCard_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void RowDetailCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void BtnCopyRowDetail_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentRowDetailClipboardText))
            return;
        try
        {
            System.Windows.Clipboard.SetText(_currentRowDetailClipboardText);
            SetStatusBarMessage("✓ Detalle copiado al portapapeles");
        }
        catch (Exception ex)
        {
            SetStatusBarMessage($"⚠ {ex.Message}");
        }
    }
}
