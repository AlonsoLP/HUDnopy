// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/MainWindow.SettingsAndAlerts.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Settings persistence, alert thresholds, sound libraries, themes, and Piper/SAPI TTS controls.
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
/// Settings persistence, alert thresholds, sound libraries, themes, and Piper/SAPI TTS controls.
/// </summary>
public partial class MainWindow
{
    private void BuildAlertSettingsGrid()
    {
        var text = AlertControlTranslations.ForLanguage(_uiLanguage);
        var soundProfiles = _alertSoundService.Profiles
            .Select(profile => new AlertSoundProfileOption(profile.Id, SoundProfileTranslations.Name(profile.Id, profile.Name, _uiLanguage)))
            .ToList();
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var details in AlertSettingsGrid.Children.OfType<StackPanel>())
        {
            if (details.Children.Count == 2 && details.Children[0] is DockPanel title &&
                title.Children.Count > 0 && title.Children[0] is System.Windows.Controls.Button { Tag: string id } &&
                details.Children[1] is TextBlock { Visibility: Visibility.Visible })
                expanded.Add(id);
        }

        AlertSettingsGrid.Children.Clear();
        AlertSettingsGrid.ColumnDefinitions.Clear();
        AlertSettingsGrid.RowDefinitions.Clear();
        Grid.SetIsSharedSizeScope(AlertSettingsGrid, true);
        AlertSettingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        AlertSettingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
        AlertSettingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AlertSettingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        AddAlertGridHeader(text["alert"], 0);
        AddAlertGridHeader(text["visual"], 1);
        AddAlertGridHeader(text["sound"], 2);
        AddAlertGridHeader(text["voice"], 3);

        int row = 1;
        foreach (var definition in AlertDefinitions.ForLanguage(_uiLanguage))
        {
            AlertSettingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var preference = _configService.Current.AlertPreferences[definition.Id];
            var details = new StackPanel { Margin = new Thickness(0, 2, 4, 2) };
            var title = new DockPanel();
            var description = new TextBlock
            {
                Text = definition.Description,
                Language = System.Windows.Markup.XmlLanguage.GetLanguage(_uiLanguage),
                Foreground = (System.Windows.Media.Brush)FindResource("HudMutedBrush"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Visibility = expanded.Contains(definition.Id) ? Visibility.Visible : Visibility.Collapsed
            };
            var infoButton = new System.Windows.Controls.Button
            {
                Content = "ⓘ",
                Tag = definition.Id,
                Style = (Style)FindResource("HudIconButtonStyle"),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol"),
                MinWidth = 28,
                MinHeight = 28,
                Padding = new Thickness(4),
                ToolTip = text[expanded.Contains(definition.Id) ? "hideInfo" : "showInfo"]
            };
            System.Windows.Automation.AutomationProperties.SetName(infoButton,
                string.Format(text[expanded.Contains(definition.Id) ? "hideInfoName" : "showInfoName"], definition.Name));
            infoButton.Click += (_, _) =>
            {
                bool show = description.Visibility != Visibility.Visible;
                description.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                infoButton.ToolTip = text[show ? "hideInfo" : "showInfo"];
                System.Windows.Automation.AutomationProperties.SetName(infoButton,
                    string.Format(text[show ? "hideInfoName" : "showInfoName"], definition.Name));
            };
            DockPanel.SetDock(infoButton, Dock.Left);
            title.Children.Add(infoButton);
            title.Children.Add(new TextBlock
            {
                Text = definition.Name,
                Language = System.Windows.Markup.XmlLanguage.GetLanguage(_uiLanguage),
                Foreground = (System.Windows.Media.Brush)FindResource("HudTextBrush"),
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
            details.Children.Add(title);
            details.Children.Add(description);
            Grid.SetRow(details, row);
            AlertSettingsGrid.Children.Add(details);

            AddAlertChannelCheckbox(definition.Id, "VisualEnabled", preference.VisualEnabled, row, 1, text["visualTooltip"]);
            var soundCheckbox = CreateAlertChannelCheckbox(definition.Id, "SoundEnabled", preference.SoundEnabled,
                text["soundTooltip"]);
            soundCheckbox.Margin = new Thickness(4, 0, 8, 0);
            var rowProfiles = soundProfiles;
            if (!soundProfiles.Any(option => string.Equals(option.Id, preference.SoundProfile, StringComparison.OrdinalIgnoreCase)))
            {
                rowProfiles = soundProfiles.ToList();
                string missingName = preference.SoundProfile.StartsWith(AlertSoundService.FileProfilePrefix, StringComparison.OrdinalIgnoreCase)
                    ? preference.SoundProfile[AlertSoundService.FileProfilePrefix.Length..]
                    : preference.SoundProfile;
                rowProfiles.Add(new AlertSoundProfileOption(preference.SoundProfile,
                    string.Format(SettingsTranslations.ForLanguage(_uiLanguage)["soundMissing"], missingName)));
            }
            var soundProfile = new System.Windows.Controls.ComboBox
            {
                ItemsSource = rowProfiles,
                DisplayMemberPath = nameof(AlertSoundProfileOption.Name),
                SelectedValuePath = nameof(AlertSoundProfileOption.Id),
                SelectedValue = rowProfiles.First(option =>
                    string.Equals(option.Id, preference.SoundProfile, StringComparison.OrdinalIgnoreCase)).Id,
                Tag = new AlertPreferenceTag(definition.Id, "SoundProfile"),
                ToolTip = text["profileTooltip"],
                MinWidth = 0,
                MaxWidth = 300,
                Margin = new Thickness(2, 4, 2, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            soundProfile.SelectionChanged += AlertSoundProfile_SelectionChanged;

            var previewButton = CreateAlertPreviewButton(
                new AlertSoundPreviewTag(definition.Id, soundProfile),
                text["soundPreview"],
                string.Format(text["testSound"], definition.Name),
                PreviewAlertSound_Click);
            var profileControls = new Grid { HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            profileControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            profileControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "AlertSoundProfile" });
            profileControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            profileControls.Children.Add(soundCheckbox);
            Grid.SetColumn(soundProfile, 1);
            profileControls.Children.Add(soundProfile);
            Grid.SetColumn(previewButton, 2);
            profileControls.Children.Add(previewButton);

            if (row == 1)
            {
                // Measure all names with the selector's font, reserving room for its dropdown arrow.
                foreach (var profile in soundProfiles)
                {
                    var measure = new StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        Visibility = Visibility.Hidden,
                        MaxWidth = 300,
                        Margin = new Thickness(9, 0, 11, 0)
                    };
                    foreach (string sample in new[] { profile.Name, "▾" })
                    {
                        var textBlock = new TextBlock { Text = sample };
                        if (sample == "▾")
                            textBlock.Margin = new Thickness(8, 0, 0, 0);
                        textBlock.SetBinding(TextElement.FontFamilyProperty, new WpfBinding(nameof(WpfControl.FontFamily)) { Source = soundProfile });
                        textBlock.SetBinding(TextElement.FontSizeProperty, new WpfBinding(nameof(WpfControl.FontSize)) { Source = soundProfile });
                        textBlock.SetBinding(TextElement.FontStyleProperty, new WpfBinding(nameof(WpfControl.FontStyle)) { Source = soundProfile });
                        textBlock.SetBinding(TextElement.FontWeightProperty, new WpfBinding(nameof(WpfControl.FontWeight)) { Source = soundProfile });
                        textBlock.SetBinding(TextElement.FontStretchProperty, new WpfBinding(nameof(WpfControl.FontStretch)) { Source = soundProfile });
                        measure.Children.Add(textBlock);
                    }
                    Grid.SetColumn(measure, 1);
                    profileControls.Children.Add(measure);
                }
            }
            Grid.SetRow(profileControls, row);
            Grid.SetColumn(profileControls, 2);
            AlertSettingsGrid.Children.Add(profileControls);

            var voiceCheckbox = CreateAlertChannelCheckbox(definition.Id, "VoiceEnabled", preference.VoiceEnabled,
                text["voiceTooltip"]);
            var voiceButton = CreateAlertPreviewButton(
                definition,
                text["voicePreview"],
                string.Format(text["testVoice"], definition.Name),
                PreviewAlertVoice_Click);
            var voiceControls = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            voiceControls.Children.Add(voiceCheckbox);
            voiceControls.Children.Add(voiceButton);
            Grid.SetRow(voiceControls, row);
            Grid.SetColumn(voiceControls, 3);
            AlertSettingsGrid.Children.Add(voiceControls);
            row++;
        }
    }

    private System.Windows.Controls.Button CreateAlertPreviewButton(
        object tag, string tooltip, string accessibleName, RoutedEventHandler click)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = "▶",
            Style = (Style)FindResource("HudIconButtonStyle"),
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(2, 4, 0, 4),
            MinWidth = 28,
            MinHeight = 28,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = tag,
            ToolTip = tooltip
        };
        System.Windows.Automation.AutomationProperties.SetName(button, accessibleName);
        button.Click += click;
        return button;
    }

    private void AddAlertGridHeader(string text, int column)
    {
        if (column == 0)
            AlertSettingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = text,
            Foreground = (System.Windows.Media.Brush)FindResource("HudAccentBrush"),
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = column == 0 ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Center,
            TextAlignment = column == 0 ? TextAlignment.Left : TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 4)
        };
        Grid.SetRow(header, 0);
        Grid.SetColumn(header, column);
        AlertSettingsGrid.Children.Add(header);
    }

    private void AddAlertChannelCheckbox(string alertId, string channel, bool isChecked, int row, int column, string tooltip)
    {
        var checkBox = CreateAlertChannelCheckbox(alertId, channel, isChecked, tooltip);
        Grid.SetRow(checkBox, row);
        Grid.SetColumn(checkBox, column);
        AlertSettingsGrid.Children.Add(checkBox);
    }

    private System.Windows.Controls.CheckBox CreateAlertChannelCheckbox(string alertId, string channel, bool isChecked, string tooltip)
    {
        var checkBox = new System.Windows.Controls.CheckBox
        {
            IsChecked = isChecked,
            Tag = new AlertPreferenceTag(alertId, channel),
            ToolTip = tooltip,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(checkBox, tooltip);
        checkBox.Click += AlertPreference_Click;
        return checkBox;
    }

    private void AlertPreference_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox { Tag: AlertPreferenceTag tag } checkBox)
            return;

        var preference = _configService.Current.AlertPreferences[tag.AlertId];
        switch (tag.Channel)
        {
            case "VisualEnabled":
                preference.VisualEnabled = checkBox.IsChecked == true;
                break;
            case "SoundEnabled":
                preference.SoundEnabled = checkBox.IsChecked == true;
                break;
            case "VoiceEnabled":
                preference.VoiceEnabled = checkBox.IsChecked == true;
                break;
        }

        _configService.Save();
    }

    private void AlertSoundProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || sender is not System.Windows.Controls.ComboBox
            { Tag: AlertPreferenceTag tag, SelectedValue: string profile } selector)
            return;

        var preference = _configService.Current.AlertPreferences[tag.AlertId];
        string previous = preference.SoundProfile;
        preference.SoundProfile = profile;
        try
        {
            _configService.SaveChecked();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            preference.SoundProfile = previous;
            _suppressEvents = true;
            selector.SelectedValue = previous;
            _suppressEvents = false;
            TxtSoundDirectoryStatus.Text = string.Format(SettingsTranslations.ForLanguage(_uiLanguage)["soundDirError"], ex.Message);
        }
    }

    private void RefreshSoundDirectories()
    {
        LstSoundDirectories.ItemsSource = _configService.Current.AlertSoundDirectories.ToArray();
        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        var messages = new List<string>();
        if (_alertSoundService.MigratedProfileCount > 0)
            messages.Add(string.Format(text["soundMigration"], _alertSoundService.MigratedProfileCount));
        if (_alertSoundService.MigrationError is not null)
            messages.Add(string.Format(text["soundDirError"], _alertSoundService.MigrationError));
        messages.AddRange(_alertSoundService.DirectoryErrors.Select(error => string.Format(text["soundDirError"], error)));
        TxtSoundDirectoryStatus.Text = string.Join(Environment.NewLine, messages);
    }

    private void BtnAddSoundDirectory_Click(object sender, RoutedEventArgs e)
    {
        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        using var dialog = new Forms.FolderBrowserDialog { Description = text["soundDirBrowse"] };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
            return;
        string directory = Path.GetFullPath(dialog.SelectedPath);
        if (_configService.Current.AlertSoundDirectories.Contains(directory, StringComparer.OrdinalIgnoreCase))
        {
            TxtSoundDirectoryStatus.Text = text["soundDirDuplicate"];
            return;
        }
        SaveSoundDirectories(_configService.Current.AlertSoundDirectories.Append(directory));
    }

    private void BtnRemoveSoundDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (LstSoundDirectories.SelectedItem is not string directory)
        {
            TxtSoundDirectoryStatus.Text = SettingsTranslations.ForLanguage(_uiLanguage)["soundDirSelect"];
            return;
        }
        SaveSoundDirectories(_configService.Current.AlertSoundDirectories
            .Where(item => !string.Equals(item, directory, StringComparison.OrdinalIgnoreCase)));
    }

    private void SaveSoundDirectories(IEnumerable<string> directories)
    {
        try
        {
            _configService.SetAlertSoundDirectories(directories);
            ReloadSoundDirectories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            TxtSoundDirectoryStatus.Text = string.Format(SettingsTranslations.ForLanguage(_uiLanguage)["soundDirError"], ex.Message);
        }
    }

    private void BtnRefreshSoundDirectories_Click(object sender, RoutedEventArgs e) => ReloadSoundDirectories();

    private void ReloadSoundDirectories()
    {
        _alertSoundService.ReloadProfiles();
        RefreshSoundDirectories();
        BuildAlertSettingsGrid();
        ApplyDesktopFontScale(_desktopFontScale, AlertSettingsGrid);
    }

    private async Task LoadAlertSpeechVoicesAsync(string selectedVoice)
    {
        CmbAlertVoice.IsEnabled = false;
        TxtAlertVoiceStatus.Text = SettingsTranslations.ForLanguage(_uiLanguage)["searchingVoices"];
        _installedVoiceNames = await Task.Run(_alertSpeechService.GetInstalledVoiceNames);
        ApplySpeechVoiceOptions(selectedVoice);
    }

    private void ApplySpeechVoiceOptions(string selectedVoice)
    {
        var installedVoices = _installedVoiceNames ?? throw new InvalidOperationException("Voces no inicializadas.");
        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        bool wasSuppressed = _suppressEvents;
        _suppressEvents = true;
        var voiceOptions = new List<SpeechVoiceOption>
        {
            new("", text["defaultVoice"])
        };
        voiceOptions.AddRange(installedVoices.Select(name => new SpeechVoiceOption(name, name)));

        try
        {
            CmbAlertVoice.ItemsSource = voiceOptions;
            CmbAlertVoice.SelectedValue = installedVoices.Contains(selectedVoice, StringComparer.OrdinalIgnoreCase)
                ? selectedVoice
                : "";
            CmbAlertVoice.IsEnabled = installedVoices.Count > 0;
            SizeComboBoxToLongestItem(CmbAlertVoice, voiceOptions.Select(option => option.DisplayName));
            TxtAlertVoiceStatus.Text = installedVoices.Count == 0
                ? text["noVoices"]
                : string.Format(text["voicesAvailable"], installedVoices.Count);
        }
        finally
        {
            _suppressEvents = wasSuppressed;
        }
    }

    private void CmbAlertVoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || CmbAlertVoice.SelectedValue is not string voiceName)
            return;

        _configService.Current.SpeechVoiceName = voiceName;
        _configService.Save();
    }

    private void BtnTestAlertVoice_Click(object sender, RoutedEventArgs e)
    {
        bool isPiper = string.Equals(_configService.Current.SpeechEngine, "piper", StringComparison.OrdinalIgnoreCase);
        if (isPiper || CmbAlertVoice.IsEnabled)
        {
            _alertSpeechService.SpeakTest();
            TxtStatusBarLeft.Text = SettingsTranslations.ForLanguage(_uiLanguage)["voiceTestRequested"];
        }
        else
        {
            TxtStatusBarLeft.Text = SettingsTranslations.ForLanguage(_uiLanguage)["voicesUnavailable"];
        }
    }

    private void PreviewAlertSound_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: AlertSoundPreviewTag tag })
        {
            string profile = tag.ProfileSelector.SelectedValue as string ?? "Auto";
            _alertSoundService.PlayPreview(tag.AlertId, profile);
        }
    }

    private void PreviewAlertVoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: AlertDefinition definition })
            return;

        if (!CmbAlertVoice.IsEnabled)
        {
            TxtStatusBarLeft.Text = SettingsTranslations.ForLanguage(_uiLanguage)["voicesUnavailable"];
            return;
        }

        _alertSpeechService.SpeakPreview(definition);
        TxtStatusBarLeft.Text = SettingsTranslations.ForLanguage(_uiLanguage)["voiceTestRequested"];
    }

    private void SldAlertSoundVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtAlertSoundVolume is null)
            return;

        int volume = (int)SldAlertSoundVolume.Value;
        TxtAlertSoundVolume.Text = $"{volume} %";
        if (_suppressEvents)
            return;

        _configService.Current.AlertSoundVolumePercent = volume;
        _configService.Save();
        TxtStatusBarLeft.Text = volume == 0
            ? SettingsTranslations.ForLanguage(_uiLanguage)["muted"]
            : string.Format(SettingsTranslations.ForLanguage(_uiLanguage)["volumeChanged"], volume);
    }

    private void SldSpeechRate_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtSpeechRate is null || SldSpeechRate is null || _configService is null)
            return;

        int rate = (int)SldSpeechRate.Value;
        TxtSpeechRate.Text = rate.ToString("+0;-0;0");
        if (_suppressEvents)
            return;

        int previous = _configService.Current.SpeechRate;
        try
        {
            _configService.SetSpeechRate(rate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _suppressEvents = true;
            SldSpeechRate.Value = previous;
            TxtSpeechRate.Text = previous.ToString("+0;-0;0");
            _suppressEvents = false;
            TxtStatusBarLeft.Text = string.Format(SettingsTranslations.ForLanguage(_uiLanguage)["voiceRateSaveError"], ex.Message);
        }
    }

    private void UpdateSpeechQueueUi()
    {
        if (CmbSpeechQueueMode is null || SpeechExpirationPanel is null || TxtSpeechExpiration is null || SldSpeechExpiration is null)
            return;

        string mode = CmbSpeechQueueMode.SelectedValue as string ?? _configService.Current.SpeechQueueMode;
        SpeechExpirationPanel.Visibility = string.Equals(mode, "tactical", StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        TxtSpeechExpiration.Text = string.Format(text["voiceSecondsFormat"], (int)SldSpeechExpiration.Value);
        SizeComboBoxToLongestItem(CmbSpeechQueueMode,
            CmbSpeechQueueMode.Items.OfType<System.Windows.Controls.ComboBoxItem>().Select(item => item.Content as string ?? ""));
    }

    private void CmbSpeechQueueMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbSpeechQueueMode is null || _configService is null)
            return;

        UpdateSpeechQueueUi();
        if (_suppressEvents || CmbSpeechQueueMode.SelectedValue is not string mode)
            return;

        _configService.Current.SpeechQueueMode = mode;
        _configService.Save();
    }

    private void SldSpeechExpiration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtSpeechExpiration is null || SldSpeechExpiration is null || _configService is null)
            return;

        int seconds = (int)SldSpeechExpiration.Value;
        TxtSpeechExpiration.Text = string.Format(SettingsTranslations.ForLanguage(_uiLanguage)["voiceSecondsFormat"], seconds);
        if (_suppressEvents)
            return;

        _configService.Current.SpeechQueueExpirationSeconds = seconds;
        _configService.Save();
    }

    private void RefreshAlertsUi()
    {
        var activeAlerts = _alertService.ActiveAlerts;
        var alerts = _alertService.Alerts;
        var text = CoreAlertTranslations.ForLanguage(_uiLanguage);
        if (activeAlerts.Count == 0)
        {
            AlertStatusBanner.Visibility = Visibility.Collapsed;
        }
        else
        {
            var highestPriority = activeAlerts[0];
            var bannerBrush = (System.Windows.Media.Brush)FindResource(
                highestPriority.Severity switch
                {
                    AlertSeverity.Critical => "HudDangerBrush",
                    AlertSeverity.Warning => "HudSecondaryBrush",
                    _ => "HudAccentBrush"
                });
            AlertStatusBanner.Background = bannerBrush;
            TxtAlertStatusBannerTitle.Text = activeAlerts.Count == 1
                ? string.Format(text["activeOne"], highestPriority.Title)
                : string.Format(text["activeMany"], activeAlerts.Count, highestPriority.Title);
            TxtAlertStatusBannerMessage.Text = highestPriority.Message;
            AlertStatusBanner.Visibility = Visibility.Visible;
        }

        TxtDashboardAlerts.Text = alerts.Count == 0
            ? DashboardTranslations.ForLanguage(_uiLanguage)["noAlerts"]
            : string.Join(Environment.NewLine, alerts.Take(5)
                .Select(alert => $"{alert.Timestamp.ToLocalTime():HH:mm:ss} · {alert.Title}: {alert.Message}"));
        if (AlertToast.Visibility == Visibility.Visible && _alertToastId is not null &&
            alerts.FirstOrDefault(alert => alert.Id == _alertToastId) is { } currentToast)
        {
            TxtAlertToastTitle.Text = currentToast.Title;
            TxtAlertToastMessage.Text = currentToast.Message;
        }
    }

    private void ShowAlertToast(AlertNotification alert)
    {
        alert = _alertService.Alerts.FirstOrDefault(item => item.Id == alert.Id) ?? alert;
        _alertToastId = alert.Id;
        var accent = (System.Windows.Media.Brush)FindResource(
            alert.Severity switch
            {
                AlertSeverity.Critical => "HudDangerBrush",
                AlertSeverity.Warning => "HudSecondaryBrush",
                _ => "HudAccentBrush"
            });
        AlertToast.BorderBrush = accent;
        TxtAlertToastTitle.Foreground = accent;
        TxtAlertToastTitle.Text = alert.Title;
        TxtAlertToastMessage.Text = alert.Message;
        AlertToast.Visibility = Visibility.Visible;

        _alertToastTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(12)
        };
        _alertToastTimer.Stop();
        _alertToastTimer.Tick -= AlertToastTimer_Tick;
        _alertToastTimer.Tick += AlertToastTimer_Tick;
        _alertToastTimer.Start();

        if (WindowState == WindowState.Minimized || !IsVisible)
            _notifyIcon?.ShowBalloonTip(5000, alert.Title, alert.Message, Forms.ToolTipIcon.Warning);
    }

    private void AlertToastTimer_Tick(object? sender, EventArgs e)
    {
        _alertToastTimer?.Stop();
        AlertToast.Visibility = Visibility.Collapsed;
        _alertToastId = null;
    }

    private void DismissAlertToast_Click(object sender, RoutedEventArgs e)
    {
        _alertToastTimer?.Stop();
        AlertToast.Visibility = Visibility.Collapsed;
        _alertToastId = null;
    }

    private void ChkStartMinimizedToTray_Click(object sender, RoutedEventArgs e)
    {
        _configService.Current.StartMinimizedToTray = ChkStartMinimizedToTray.IsChecked == true;
        _configService.Save();
    }

    private void Deck_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0)
            ItemsControlDeck.Tag = Math.Max(1, (int)(e.NewSize.Width / 222));
    }

    private void ResponsiveGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is UniformGrid grid && e.NewSize.Width > 0 &&
            double.TryParse(grid.Tag?.ToString(), out double minimum) && minimum > 0)
            grid.Columns = Math.Max(1, (int)(e.NewSize.Width / minimum));
    }

    private void NavigationActionsGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateNavigationActionsColumns();

    private void UpdateNavigationActionsColumns()
    {
        if (NavigationActionsGrid is null || NavigationActionsGrid.ActualWidth <= 0)
            return;

        double requiredWidth = 0;
        foreach (var button in NavigationActionsGrid.Children.OfType<System.Windows.Controls.Button>())
        {
            var label = new TextBlock
            {
                Text = button.Content as string ?? "",
                FontFamily = button.FontFamily,
                FontSize = button.FontSize,
                FontStretch = button.FontStretch,
                FontStyle = button.FontStyle,
                FontWeight = button.FontWeight
            };
            label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            requiredWidth = Math.Max(requiredWidth, label.DesiredSize.Width + button.Padding.Left + button.Padding.Right +
                button.BorderThickness.Left + button.BorderThickness.Right + button.Margin.Left + button.Margin.Right);
        }
        double width = NavigationActionsGrid.ActualWidth;
        NavigationActionsGrid.Columns = width >= requiredWidth * 4 ? 4 : width >= requiredWidth * 2 ? 2 : 1;
    }

    private void RefreshHeaderModules(ShipState state)
    {
        var modules = HeaderTelemetry.Present(state, _configService.Current, _uiLanguage)
            .Where(module => module.Enabled).ToArray();
        if (HeaderModules.ItemsSource is not IReadOnlyList<HeaderModule> current || !current.SequenceEqual(modules))
            HeaderModules.ItemsSource = modules;
    }

    private void RefreshHeaderOptions()
    {
        LblHeaderModules.Text = DashboardTranslations.ForLanguage(_uiLanguage)["headerModules"];
        HeaderModuleOptions.Children.Clear();
        foreach (var module in HeaderTelemetry.Present(_fileWatcher.State, _configService.Current, _uiLanguage))
        {
            string label = module.Label;
            if (module.Id == "shield" && label.Length > 0)
            {
                var casing = System.Globalization.CultureInfo.GetCultureInfo(_uiLanguage).TextInfo;
                label = casing.ToUpper(label[..1]) + casing.ToLower(label[1..]);
            }
            var check = new System.Windows.Controls.CheckBox
            {
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                Tag = module.Id,
                IsChecked = module.Enabled,
                Margin = new Thickness(0, 0, 16, 8),
                MaxWidth = Math.Max(0, HeaderModuleOptions.ActualWidth - 16),
                Foreground = (System.Windows.Media.Brush)FindResource("HudTextBrush")
            };
            check.Click += HeaderModule_Click;
            HeaderModuleOptions.Children.Add(check);
        }
    }

    private void HeaderModuleOptions_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (var check in HeaderModuleOptions.Children.OfType<System.Windows.Controls.CheckBox>())
            check.MaxWidth = Math.Max(0, e.NewSize.Width - check.Margin.Left - check.Margin.Right);
    }

    private void HeaderModule_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _configService.SetHeaderModules(HeaderModuleOptions.Children.OfType<System.Windows.Controls.CheckBox>()
                .Where(check => check.IsChecked == true).Select(check => (string)check.Tag));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            RefreshHeaderOptions();
            TxtStatusBarLeft.Text = string.Format(DashboardTranslations.ForLanguage(_uiLanguage)["headerSaveError"], ex.Message);
        }
    }


    private async void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtProcessExitGraceSeconds.Text.Trim(), out int exitGraceSeconds) ||
            exitGraceSeconds < 1 || exitGraceSeconds > 300)
        {
            var text = SettingsTranslations.ForLanguage(_uiLanguage);
            System.Windows.MessageBox.Show(
                text["invalidWaitMessage"],
                text["invalidWait"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string journalFolder = TxtConfigJournalPath.Text.Trim();
        string bindingsFolder = TxtConfigBindingsPath.Text.Trim();
        string selectedBindsFile = string.Equals(TxtConfigSelectedBindsFile.Text, _displayedBindsFile, StringComparison.Ordinal)
            ? _configService.Current.SelectedBindsFile
            : TxtConfigSelectedBindsFile.Text.Trim();
        bool bindingsChanged =
            !string.Equals(_configService.Current.BindingsFolderPath, bindingsFolder, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_configService.Current.SelectedBindsFile, selectedBindsFile, StringComparison.OrdinalIgnoreCase);

        _configService.Current.JournalFolderPath = journalFolder;
        _configService.Current.BindingsFolderPath = bindingsFolder;
        _configService.Current.SelectedBindsFile = selectedBindsFile;
        _configService.Current.HideUnassignedBindings = ChkHideUnassignedBindings.IsChecked == true;
        _configService.Current.FocusGameWindowOnInput = ChkFocusGameWindow.IsChecked == true;
        _configService.Current.StartMinimizedToTray = ChkStartMinimizedToTray.IsChecked == true;
        _configService.Current.GameProcessExitGraceSeconds = exitGraceSeconds;
        _configService.Save();
        _displayedBindsFile = null;
        if (bindingsChanged)
            _bindingsService.ReloadBindings();
        else
            UpdateBindingsProfileInfo();
        await _webServer.BroadcastStateAsync();
        TxtStatusBarLeft.Text = SettingsTranslations.ForLanguage(_uiLanguage)["pathsSaved"];
    }

    private async void BtnSaveAlertThresholds_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtFuelLowThreshold.Text.Trim(), out int lowThreshold) ||
            !int.TryParse(TxtFuelCriticalThreshold.Text.Trim(), out int criticalThreshold) ||
            !int.TryParse(TxtHullCriticalThreshold.Text.Trim(), out int hullCriticalThreshold) ||
            !int.TryParse(TxtMissionExpiryWarningHours.Text.Trim(), out int missionWarningHours) ||
            !int.TryParse(TxtMissionExpiryCriticalHours.Text.Trim(), out int missionCriticalHours) ||
            lowThreshold is < 2 or > 100 ||
            criticalThreshold < 1 ||
            criticalThreshold >= lowThreshold ||
            hullCriticalThreshold is < 1 or > 99 ||
            missionWarningHours is < 2 or > 720 ||
            missionCriticalHours < 1 ||
            missionCriticalHours >= missionWarningHours)
        {
            var text = SettingsTranslations.ForLanguage(_uiLanguage);
            System.Windows.MessageBox.Show(
                text["invalidThresholdMessage"],
                text["invalidThreshold"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _configService.Current.FuelLowThresholdPercent = lowThreshold;
        _configService.Current.FuelCriticalThresholdPercent = criticalThreshold;
        _configService.Current.HullCriticalThresholdPercent = hullCriticalThreshold;
        _configService.Current.MissionExpiryWarningHours = missionWarningHours;
        _configService.Current.MissionExpiryCriticalHours = missionCriticalHours;
        _configService.Save();
        TxtStatusBarLeft.Text = string.Format(SettingsTranslations.ForLanguage(_uiLanguage)["thresholdSaved"],
            lowThreshold, criticalThreshold, hullCriticalThreshold, missionWarningHours, missionCriticalHours);
        _fileWatcher.RefreshState();
        await _webServer.BroadcastStateAsync();
    }

    private void MaterialFilter_Changed(object sender, EventArgs e) => ApplyMaterialFilters();


    // =========================================================================
    // Piper Neural TTS (Free local voices with automatic download)
    // =========================================================================

    private async void CmbSpeechEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _configService is null) return;
        if (CmbSpeechEngine.SelectedValue is string engine)
        {
            _configService.Current.SpeechEngine = engine;
            _configService.Save();
            RefreshPiperVoicesUi();

            if (string.Equals(engine, "piper", StringComparison.OrdinalIgnoreCase) && !_piperTts.IsEngineInstalled())
            {
                SetPiperButtonsEnabled(false);
                var progress = new Progress<string>(msg => TxtPiperStatus.Text = msg);
                var (_, msg) = await _piperTts.EnsureEngineInstalledAsync(progress);
                RefreshPiperVoicesUi();
                TxtPiperStatus.Text = msg;
                TxtStatusBarLeft.Text = msg;
                SetPiperButtonsEnabled(true);
            }
        }
    }

    private void CmbPiperVoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _configService is null) return;
        if (CmbPiperVoice.SelectedValue is string voiceId)
        {
            _configService.Current.PiperVoiceId = voiceId;
            _configService.Save();
            UpdatePiperStatusText();
        }
    }

    private void RefreshPiperVoicesUi()
    {
        if (PiperVoicePanel is null || CmbPiperVoice is null || _piperTts is null)
            return;

        bool isPiper = string.Equals(
            CmbSpeechEngine?.SelectedValue as string ?? _configService.Current.SpeechEngine,
            "piper",
            StringComparison.OrdinalIgnoreCase);
        PiperVoicePanel.Visibility = isPiper ? Visibility.Visible : Visibility.Collapsed;
        if (SapiVoiceRow is not null)
            SapiVoiceRow.Visibility = isPiper ? Visibility.Collapsed : Visibility.Visible;

        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        var options = new List<FilterOption> { new("auto", text["piperVoiceAuto"]) };
        foreach (var v in PiperTtsService.Catalog)
        {
            string badge = _piperTts.IsVoiceInstalled(v.Id) ? "✓ " : "⬇ ";
            options.Add(new FilterOption(v.Id, $"{badge}{v.DisplayName}"));
        }

        string selected = string.IsNullOrWhiteSpace(_configService.Current.PiperVoiceId)
            ? "auto"
            : _configService.Current.PiperVoiceId;
        _suppressEvents = true;
        CmbPiperVoice.ItemsSource = options;
        CmbPiperVoice.SelectedValue = options.Any(o => o.Key == selected) ? selected : "auto";
        _suppressEvents = false;
        UpdatePiperStatusText();
    }

    private void UpdatePiperStatusText()
    {
        if (TxtPiperStatus is null || _piperTts is null)
            return;

        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        string? exePath = _piperTts.FindPiperExecutable();
        bool engineOk = exePath is not null;
        var resolved = _piperTts.ResolveVoice(_configService.Current.PiperVoiceId, _uiLanguage);
        bool voiceOk = resolved is not null && _piperTts.IsVoiceInstalled(resolved.Id);

        if (engineOk && voiceOk && resolved is not null)
        {
            TxtPiperStatus.Text = $"{string.Format(text["piperStatusReady"], resolved.DisplayName)} ({exePath})";
        }
        else if (engineOk)
        {
            TxtPiperStatus.Text = $"✓ Motor Piper listo ({exePath}) · {text["piperStatusMissing"]}";
        }
        else
        {
            TxtPiperStatus.Text = $"⚠ Motor Piper no instalado · {text["piperStatusMissing"]}";
        }
    }

    private async void BtnDownloadPiperVoice_Click(object sender, RoutedEventArgs e)
    {
        SetPiperButtonsEnabled(false);
        var progress = new Progress<string>(msg => TxtPiperStatus.Text = msg);
        string voiceId = CmbPiperVoice.SelectedValue as string ?? "auto";
        var (_, msg) = await _piperTts.DownloadVoiceAsync(voiceId, _uiLanguage, progress);
        RefreshPiperVoicesUi();
        TxtPiperStatus.Text = msg;
        TxtStatusBarLeft.Text = msg;
        SetPiperButtonsEnabled(true);
    }

    private async void BtnDownloadPiperPack_Click(object sender, RoutedEventArgs e)
    {
        SetPiperButtonsEnabled(false);
        var progress = new Progress<string>(msg => TxtPiperStatus.Text = msg);
        var (_, msg) = await _piperTts.DownloadAllSixLanguagesPackAsync(progress);
        RefreshPiperVoicesUi();
        TxtPiperStatus.Text = msg;
        TxtStatusBarLeft.Text = msg;
        SetPiperButtonsEnabled(true);
    }

    private async void BtnTestPiperVoice_Click(object sender, RoutedEventArgs e)
    {
        string voiceId = CmbPiperVoice.SelectedValue as string ?? _configService.Current.PiperVoiceId;
        var resolved = _piperTts.ResolveVoice(voiceId, _uiLanguage);
        if (!_piperTts.IsEngineInstalled())
        {
            TxtPiperStatus.Text = "⚠ Motor Piper no instalado. Pulsa «Descargar voz» o «Pack 6 idiomas».";
            return;
        }
        if (resolved is null || !_piperTts.IsVoiceInstalled(resolved.Id))
        {
            TxtPiperStatus.Text = $"⚠ La voz seleccionada ({resolved?.DisplayName ?? voiceId}) no está descargada. Pulsa «Descargar voz».";
            return;
        }

        SetPiperButtonsEnabled(false);
        BtnTestPiperVoice.IsEnabled = false;
        TxtPiperStatus.Text = $"Probando voz Piper ({resolved.DisplayName})...";

        try
        {
            int currentRate = SldSpeechRate is not null
                ? Math.Clamp((int)SldSpeechRate.Value, -5, 5)
                : Math.Clamp(_configService.Current.SpeechRate, -5, 5);
            string sample = AlertControlTranslations.ForLanguage(_uiLanguage)["speechTest"];
            byte[]? wav = await _piperTts.TrySynthesizeWavAsync(
                sample,
                resolved.Id,
                _uiLanguage,
                currentRate,
                System.Threading.CancellationToken.None);

            if (wav is not { Length: > 44 })
            {
                string err = string.IsNullOrWhiteSpace(_piperTts.LastError)
                    ? "Piper no generó audio."
                    : _piperTts.LastError;
                TxtPiperStatus.Text = $"⚠ {err}";
                TxtStatusBarLeft.Text = err;
                return;
            }

            TxtPiperStatus.Text = $"▶ Reproduciendo Piper ({resolved.DisplayName})...";
            await Task.Run(() =>
            {
                using var ms = new MemoryStream(wav);
                using var reader = new NAudio.Wave.WaveFileReader(ms);
                using var output = new NAudio.Wave.WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 2 };
                using var done = new System.Threading.ManualResetEventSlim(false);
                output.PlaybackStopped += (_, _) => done.Set();
                output.Init(reader);
                output.Play();
                done.Wait(TimeSpan.FromSeconds(15));
            });
            UpdatePiperStatusText();
        }
        catch (Exception ex)
        {
            TxtPiperStatus.Text = $"⚠ Error al reproducir Piper: {ex.Message}";
        }
        finally
        {
            BtnTestPiperVoice.IsEnabled = true;
            SetPiperButtonsEnabled(true);
        }
    }

    private void SetPiperButtonsEnabled(bool enabled)
    {
        BtnDownloadPiperVoice.IsEnabled = enabled;
        BtnDownloadPiperPack.IsEnabled = enabled;
    }
}
