// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/MainWindow.AiCopilot.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: COVAS AI copilot UI handlers, push-to-talk audio capture, and hands-free VAD.
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
/// COVAS AI copilot UI handlers, push-to-talk audio capture, and hands-free VAD.
/// </summary>
public partial class MainWindow
{
    // =========================================================================
    // AI Copilot (COVAS) — Configuration and Chat (Phase 1)
    // =========================================================================

    private void ChkEnableAiCopilot_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _configService.Current.EnableAiCopilot = ChkEnableAiCopilot.IsChecked == true;
        _configService.Save();
        UpdateAiConfigVisibility();
    }

    private void CmbAiActiveProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _configService is null) return;
        if (CmbAiActiveProvider.SelectedValue is string provider)
        {
            _configService.Current.AiActiveProvider = provider;
            _configService.Save();
            UpdateAiConfigVisibility();
        }
    }

    private void CmbAiPersonality_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _configService is null) return;
        if (CmbAiPersonality.SelectedValue is string personality)
        {
            _configService.Current.AiPersonality = personality;
            _configService.Save();
            UpdateAiConfigVisibility();
        }
    }

    private void CmbAiVerbosity_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _configService is null) return;
        if (CmbAiVerbosity.SelectedValue is string verbosity)
        {
            _configService.Current.AiVerbosity = verbosity;
            _configService.Save();
        }
    }

    private void CmbAiThinkingMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _configService is null) return;
        if (CmbAiThinkingMode.SelectedValue is string mode)
        {
            _configService.Current.AiThinkingMode = mode;
            _configService.Save();
        }
    }

    private void ChkAiRequireWakeWord_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _configService is null) return;
        _configService.Current.AiRequireWakeWord = ChkAiRequireWakeWord.IsChecked == true;
        _configService.Current.AiWakeWord = string.IsNullOrWhiteSpace(TxtAiWakeWord?.Text) ? "COVAS" : TxtAiWakeWord.Text.Trim();
        _configService.Save();
    }

    private void BtnClearAiMemory_Click(object sender, RoutedEventArgs e)
    {
        _configService.Current.AiCommanderMemory?.Clear();
        _configService.Save();
        RefreshAiMemoryPreview();
        TxtAiConfigStatus.Text = SettingsTranslations.ForLanguage(_uiLanguage)["aiMemoryCleared"];
    }

    private void ChkAiArrivalBriefing_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _configService is null) return;
        _configService.Current.AiSystemArrivalBriefing = ChkAiArrivalBriefing.IsChecked == true;
        _configService.Save();
    }

    private void ChkAiTacticalOption_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _configService is null) return;
        var cfg = _configService.Current;
        cfg.AiTacticalHighGravity = ChkAiTacticalHighGravity?.IsChecked == true;
        cfg.AiTacticalValuableSignals = ChkAiTacticalValuableSignals?.IsChecked == true;
        cfg.AiTacticalMiningProspector = ChkAiTacticalMiningProspector?.IsChecked == true;
        cfg.AiTacticalRadioChatter = ChkAiTacticalRadioChatter?.IsChecked == true;
        cfg.AiVoiceMacrosEnabled = ChkAiVoiceMacrosEnabled?.IsChecked == true;
        _configService.Save();
    }

    private void RefreshAiVoiceMacrosUi()
    {
        if (LstAiVoiceMacros is null || _configService is null)
            return;
        var macros = _configService.Current.AiVoiceMacros ??= VoiceMacroDefinition.CreateDefaults();
        LstAiVoiceMacros.ItemsSource = macros
            .Select(static m => $"«{m.TriggerPhrase}» → {m.Steps}{(string.IsNullOrWhiteSpace(m.SpokenConfirmation) ? "" : $" ({m.SpokenConfirmation})")}")
            .ToList();
    }

    private void BtnAddVoiceMacro_Click(object sender, RoutedEventArgs e)
    {
        if (_configService is null) return;
        string trigger = (TxtVoiceMacroTrigger?.Text ?? "").Trim();
        string steps = (TxtVoiceMacroSteps?.Text ?? "").Trim();
        string confirm = (TxtVoiceMacroConfirm?.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trigger) || string.IsNullOrWhiteSpace(steps))
            return;

        _configService.Current.AiVoiceMacros ??= new List<VoiceMacroDefinition>();
        _configService.Current.AiVoiceMacros.Add(new VoiceMacroDefinition
        {
            TriggerPhrase = trigger,
            Steps = steps,
            SpokenConfirmation = confirm
        });
        _configService.Save();
        if (TxtVoiceMacroTrigger is not null) TxtVoiceMacroTrigger.Clear();
        if (TxtVoiceMacroSteps is not null) TxtVoiceMacroSteps.Clear();
        if (TxtVoiceMacroConfirm is not null) TxtVoiceMacroConfirm.Clear();
        RefreshAiVoiceMacrosUi();
    }

    private void BtnRemoveVoiceMacro_Click(object sender, RoutedEventArgs e)
    {
        if (_configService is null || LstAiVoiceMacros is null) return;
        int idx = LstAiVoiceMacros.SelectedIndex;
        var list = _configService.Current.AiVoiceMacros;
        if (list is null || idx < 0 || idx >= list.Count)
            return;

        list.RemoveAt(idx);
        _configService.Save();
        RefreshAiVoiceMacrosUi();
    }

    private void BtnResetVoiceMacros_Click(object sender, RoutedEventArgs e)
    {
        if (_configService is null) return;
        _configService.Current.AiVoiceMacros = VoiceMacroDefinition.CreateDefaults();
        _configService.Save();
        RefreshAiVoiceMacrosUi();
    }

    private void ChkAiGlobalPttHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _configService is null) return;
        _configService.Current.AiGlobalPttHotkeyEnabled = ChkAiGlobalPttHotkey.IsChecked == true;
        _configService.Save();
        RegisterGlobalPttHotkey();
    }

    private void CmbAiGlobalPttHotkey_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _configService is null) return;
        if (CmbAiGlobalPttHotkey.SelectedValue is string key)
        {
            _configService.Current.AiGlobalPttHotkey = key;
            _configService.Save();
            RegisterGlobalPttHotkey();
        }
    }

    private void RefreshAiMemoryPreview()
    {
        if (TxtAiMemoryPreview is null)
            return;
        var mem = _configService.Current.AiCommanderMemory;
        if (mem is null || mem.Count == 0)
        {
            TxtAiMemoryPreview.Text = "";
            return;
        }
        TxtAiMemoryPreview.Text = $"🧠 ({mem.Count}): " + string.Join(" · ", mem.TakeLast(6));
    }

    private void ChkAiSpeakReplies_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _configService.Current.AiSpeakReplies = ChkAiSpeakReplies.IsChecked == true;
        _configService.Save();
    }

    private void ChkAiAllowActions_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _configService.Current.AiAllowActions = ChkAiAllowActions.IsChecked == true;
        _configService.Save();
    }

    private void ChkAiDynamicEvents_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _configService.Current.AiDynamicEvents = ChkAiDynamicEvents.IsChecked == true;
        _configService.Save();
    }

    private void UpdateAiConfigVisibility()
    {
        bool enabled = ChkEnableAiCopilot?.IsChecked == true;
        string provider = CmbAiActiveProvider?.SelectedValue as string ?? _configService?.Current.AiActiveProvider ?? "ollama";

        if (AiCopilotOptionsPanel is not null)
            AiCopilotOptionsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (AiOllamaPanel is not null)
            AiOllamaPanel.Visibility = enabled && provider == "ollama" ? Visibility.Visible : Visibility.Collapsed;
        if (AiGeminiPanel is not null)
            AiGeminiPanel.Visibility = enabled && provider == "gemini" ? Visibility.Visible : Visibility.Collapsed;
        if (AiOpenAiPanel is not null)
            AiOpenAiPanel.Visibility = enabled && provider == "openai" ? Visibility.Visible : Visibility.Collapsed;
        string personality = CmbAiPersonality?.SelectedValue as string ?? _configService?.Current.AiPersonality ?? "covas";
        if (AiCustomPersonalityPanel is not null)
            AiCustomPersonalityPanel.Visibility = enabled && personality == "custom" ? Visibility.Visible : Visibility.Collapsed;
        if (TxtAiProviderBadge is not null && _aiCopilot is not null)
            TxtAiProviderBadge.Text = _aiCopilot.GetActiveProviderSummary();
    }

    private static void SetAiModelComboInitial(System.Windows.Controls.ComboBox combo, string model)
    {
        if (combo is null) return;
        string clean = (model ?? "").Trim();
        if (string.IsNullOrEmpty(clean))
            return;
        combo.ItemsSource = new[] { clean };
        combo.SelectedItem = clean;
    }

    private void SaveAiFieldsToConfig()
    {
        var cfg = _configService.Current;
        cfg.EnableAiCopilot = ChkEnableAiCopilot.IsChecked == true;
        cfg.AiActiveProvider = CmbAiActiveProvider.SelectedValue as string ?? "ollama";
        cfg.AiPersonality = CmbAiPersonality.SelectedValue as string ?? "covas";
        cfg.AiOllamaEndpoint = TxtAiOllamaEndpoint.Text.Trim();
        cfg.AiOllamaModel = (CmbAiOllamaModel.SelectedItem as string ?? cfg.AiOllamaModel).Trim();
        cfg.AiGeminiApiKey = TxtAiGeminiApiKey.Text.Trim();
        cfg.AiGeminiModel = (CmbAiGeminiModel.SelectedItem as string ?? cfg.AiGeminiModel).Trim();
        cfg.AiOpenAiEndpoint = TxtAiOpenAiEndpoint.Text.Trim();
        cfg.AiOpenAiApiKey = TxtAiOpenAiApiKey.Text.Trim();
        cfg.AiOpenAiModel = (CmbAiOpenAiModel.SelectedItem as string ?? cfg.AiOpenAiModel).Trim();
        cfg.AiSpeakReplies = ChkAiSpeakReplies.IsChecked == true;
        cfg.AiAllowActions = ChkAiAllowActions.IsChecked == true;
        cfg.AiVerbosity = CmbAiVerbosity?.SelectedValue as string ?? "normal";
        cfg.AiThinkingMode = CmbAiThinkingMode?.SelectedValue as string ?? "auto";
        cfg.AiCustomPersonality = (TxtAiCustomPersonality?.Text ?? "").Trim();
        cfg.AiRequireWakeWord = ChkAiRequireWakeWord?.IsChecked == true;
        cfg.AiWakeWord = string.IsNullOrWhiteSpace(TxtAiWakeWord?.Text) ? "COVAS" : TxtAiWakeWord.Text.Trim();
        _configService.Save();
        UpdateAiConfigVisibility();
    }

    private async void BtnFetchAiModels_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string provider)
            return;

        var text = SettingsTranslations.ForLanguage(_uiLanguage);
        TxtAiConfigStatus.Text = text["aiFetchingModels"];

        string endpoint = provider switch
        {
            "openai" => TxtAiOpenAiEndpoint.Text.Trim(),
            "ollama" => TxtAiOllamaEndpoint.Text.Trim(),
            _ => ""
        };
        string apiKey = provider switch
        {
            "gemini" => TxtAiGeminiApiKey.Text.Trim(),
            "openai" => TxtAiOpenAiApiKey.Text.Trim(),
            _ => ""
        };
        var targetCombo = provider switch
        {
            "gemini" => CmbAiGeminiModel,
            "openai" => CmbAiOpenAiModel,
            _ => CmbAiOllamaModel
        };

        var (success, models, message) = await _aiCopilot.FetchAvailableModelsAsync(provider, endpoint, apiKey);
        if (success && models.Count > 0)
        {
            string previous = (targetCombo.SelectedItem as string ?? "").Trim();
            string chosen = models.FirstOrDefault(m => string.Equals(m, previous, StringComparison.OrdinalIgnoreCase))
                ?? models[0];
            targetCombo.ItemsSource = models;
            targetCombo.SelectedItem = chosen;
            SaveAiFieldsToConfig();
            TxtAiConfigStatus.Text = string.Format(text["aiModelsLoaded"], models.Count);
        }
        else
        {
            TxtAiConfigStatus.Text = success ? text["aiModelsEmpty"] : message;
        }
    }

    private void BtnSaveAiConfig_Click(object sender, RoutedEventArgs e)
    {
        SaveAiFieldsToConfig();
        string msg = SettingsTranslations.ForLanguage(_uiLanguage)["aiSaved"];
        TxtAiConfigStatus.Text = msg;
        TxtStatusBarLeft.Text = msg;
    }

    private async void BtnTestAiConfig_Click(object sender, RoutedEventArgs e)
    {
        SaveAiFieldsToConfig();
        TxtAiConfigStatus.Text = SettingsTranslations.ForLanguage(_uiLanguage)["aiTesting"];
        var (_, reply) = await _aiCopilot.TestConnectionAsync();
        TxtAiConfigStatus.Text = reply;
        TxtStatusBarLeft.Text = reply;
    }

    private void TxtAiPrompt_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = SendAiPromptAsync(TxtAiPrompt.Text);
        }
    }

    private void BtnAiVision_Click(object sender, RoutedEventArgs e)
    {
        string prompt = TxtAiPrompt.Text.Trim();
        TxtAiPrompt.Text = "";
        _ = RunAiQuickActionAsync(
            string.IsNullOrWhiteSpace(prompt) ? "👁" : $"👁 {prompt}",
            GetCovasSpeakerLabel(),
            () => _aiCopilot.AskWithScreenVisionAsync(prompt));
    }

    private void BtnSendAiPrompt_Click(object sender, RoutedEventArgs e)
    {
        _ = SendAiPromptAsync(TxtAiPrompt.Text);
    }

    private void BtnAiQuickStatus_Click(object sender, RoutedEventArgs e)
    {
        _ = SendAiPromptAsync(OperationsTranslations.ForLanguage(_uiLanguage)["aiQuickStatusPrompt"]);
    }

    private void BtnAiSummarizeGalnet_Click(object sender, RoutedEventArgs e)
    {
        _ = RunAiQuickActionAsync(
            OperationsTranslations.ForLanguage(_uiLanguage)["aiSummarizeGalnet"],
            GetCovasSpeakerLabel(),
            () => _aiCopilot.SummarizeGalnetAsync());
    }

    private void BtnAiSummarizeSession_Click(object sender, RoutedEventArgs e)
    {
        _ = RunAiQuickActionAsync(
            OperationsTranslations.ForLanguage(_uiLanguage)["aiSummarizeSession"],
            GetCovasSpeakerLabel(),
            () => _aiCopilot.SummarizeSessionAsync());
    }

    private async Task RunAiQuickActionAsync(string? userPromptLabel, string replySpeaker, Func<Task<(bool Success, string Reply)>> action)
    {
        SetAiBusy(true);
        _aiStreamLineOpen = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(userPromptLabel))
                AppendAiChatLine("CMDR", userPromptLabel);
            var (_, reply) = await action();
            FinishAiStreamOrAppend(replySpeaker, reply);
            UpdateAiLatencyStatusBar();
        }
        finally
        {
            SetAiBusy(false);
        }
    }

    private void BtnAiClearChat_Click(object sender, RoutedEventArgs e)
    {
        _aiCopilot.ClearHistory();
        TxtAiChatHistory.Clear();
    }

    private async Task SendAiPromptAsync(string prompt)
    {
        string trimmed = (prompt ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return;

        TxtAiPrompt.Clear();
        SetAiBusy(true);
        _aiStreamLineOpen = false;
        try
        {
            AppendAiChatLine("CMDR", trimmed);
            var (_, reply) = await _aiCopilot.AskAsync(trimmed);
            FinishAiStreamOrAppend(GetCovasSpeakerLabel(), reply);
            UpdateAiLatencyStatusBar();
        }
        finally
        {
            SetAiBusy(false);
        }
    }

    private void SetAiBusy(bool busy)
    {
        BtnSendAiPrompt.IsEnabled = !busy;
        BtnAiVoiceInput.IsEnabled = !busy || _isRecordingAiVoice;
        BtnAiQuickStatus.IsEnabled = !busy;
        BtnAiSummarizeGalnet.IsEnabled = !busy;
        BtnAiSummarizeSession.IsEnabled = !busy;
    }

    private async void BtnAiVoiceInput_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRecordingAiVoice && (!BtnAiVoiceInput.IsEnabled || _aiHandsFreeActive))
            return;

        var ops = OperationsTranslations.ForLanguage(_uiLanguage);
        if (!_isRecordingAiVoice)
        {
            try
            {
                _aiVoiceStream = new MemoryStream();
                var format = new NAudio.Wave.WaveFormat(16000, 16, 1);
                _aiVoiceWriter = new NAudio.Wave.WaveFileWriter(new NonClosingStream(_aiVoiceStream), format);
                _aiVoiceRecorder = new NAudio.Wave.WaveIn { WaveFormat = format, BufferMilliseconds = 80 };
                _aiVoiceRecorder.DataAvailable += (_, args) =>
                {
                    try { _aiVoiceWriter?.Write(args.Buffer, 0, args.BytesRecorded); } catch { }
                };
                _alertSpeechService.StopSpeaking();
                _aiVoiceRecorder.StartRecording();
                _isRecordingAiVoice = true;
                BtnAiVoiceInput.Content = "⏹";
                BtnAiVoiceInput.ToolTip = ops["aiVoiceStopBtn"];
                System.Windows.Automation.AutomationProperties.SetName(BtnAiVoiceInput, ops["aiVoiceStopBtn"]);
                BtnAiVoiceInput.SetResourceReference(BackgroundProperty, "HudDangerBrush");
                BtnAiVoiceInput.SetResourceReference(ForegroundProperty, "HudBgBrush");
                TxtStatusBarLeft.Text = ops["aiVoiceListening"];
            }
            catch (Exception ex)
            {
                CleanupAiVoiceRecorder();
                TxtStatusBarLeft.Text = $"Micrófono: {ex.Message}";
            }
            return;
        }

        // Stop recording and send to AI Copilot
        byte[] wavBytes = Array.Empty<byte>();
        try
        {
            _aiVoiceRecorder?.StopRecording();
            _aiVoiceWriter?.Flush();
            _aiVoiceWriter?.Dispose();
            _aiVoiceWriter = null;
            if (_aiVoiceStream is not null)
                wavBytes = _aiVoiceStream.ToArray();
        }
        finally
        {
            CleanupAiVoiceRecorder();
            BtnAiVoiceInput.Content = "🎤";
            BtnAiVoiceInput.ToolTip = ops["aiVoiceBtn"];
            System.Windows.Automation.AutomationProperties.SetName(BtnAiVoiceInput, ops["aiVoiceBtn"]);
            BtnAiVoiceInput.SetResourceReference(BackgroundProperty, "HudPanelAltBrush");
            BtnAiVoiceInput.SetResourceReference(ForegroundProperty, "HudTextBrush");
        }

        if (wavBytes.Length <= 44)
            return;

        SetAiBusy(true);
        _aiStreamLineOpen = false;
        try
        {
            DateTime sentAt = DateTime.Now;
            bool isGeminiVoice = string.Equals(_configService.Current.AiActiveProvider, "gemini", StringComparison.OrdinalIgnoreCase);
            if (isGeminiVoice)
                AppendAiChatLine("CMDR", "🎤 [Comando de voz]", sentAt);
            TxtStatusBarLeft.Text = ops["aiVoiceProcessing"];
            var (_, transcript, reply) = await _aiCopilot.AskWithAudioWavAsync(wavBytes);
            if (!isGeminiVoice && !string.IsNullOrWhiteSpace(transcript))
                AppendAiChatLine("CMDR", transcript, sentAt);
            FinishAiStreamOrAppend(GetCovasSpeakerLabel(), reply);
            UpdateAiLatencyStatusBar();
        }
        finally
        {
            SetAiBusy(false);
        }
    }

    private void CleanupAiVoiceRecorder()
    {
        _isRecordingAiVoice = false;
        try { _aiVoiceRecorder?.Dispose(); } catch { }
        _aiVoiceRecorder = null;
        try { _aiVoiceWriter?.Dispose(); } catch { }
        _aiVoiceWriter = null;
        try { _aiVoiceStream?.Dispose(); } catch { }
        _aiVoiceStream = null;
    }

    private void BtnAiHandsFree_Click(object sender, RoutedEventArgs e)
    {
        if (_aiHandsFreeActive)
        {
            StopAiHandsFree();
            return;
        }

        if (_isRecordingAiVoice)
            CleanupAiVoiceRecorder();

        var ops = OperationsTranslations.ForLanguage(_uiLanguage);
        try
        {
            StartVadRecorderInstance();
            SubscribeAudioDeviceNotifications();
            _aiHandsFreeActive = true;
            BtnAiHandsFree.Content = "🟢";
            BtnAiHandsFree.ToolTip = ops["aiHandsFreeListening"];
            System.Windows.Automation.AutomationProperties.SetName(BtnAiHandsFree, ops["aiHandsFreeListening"]);
            BtnAiHandsFree.SetResourceReference(BackgroundProperty, "HudAccentBrush");
            BtnAiHandsFree.SetResourceReference(ForegroundProperty, "HudBgBrush");
            TxtStatusBarLeft.Text = ops["aiHandsFreeStatusOn"];
        }
        catch (Exception ex)
        {
            StopAiHandsFree();
            TxtStatusBarLeft.Text = $"Micrófono: {ex.Message}";
        }
    }

    private void StartVadRecorderInstance()
    {
        try
        {
            if (_aiVadRecorder is not null)
            {
                _aiVadRecorder.DataAvailable -= OnVadAudioDataAvailable;
                _aiVadRecorder.StopRecording();
                _aiVadRecorder.Dispose();
            }
        }
        catch { }
        _aiVadRecorder = null;

        var format = new NAudio.Wave.WaveFormat(16000, 16, 1);
        _aiVadRecorder = new NAudio.Wave.WaveIn { WaveFormat = format, BufferMilliseconds = 100 };
        _aiVadRecorder.DataAvailable += OnVadAudioDataAvailable;
        _aiVadRecorder.StartRecording();
    }

    private void SubscribeAudioDeviceNotifications()
    {
        UnsubscribeAudioDeviceNotifications();
        try
        {
            _audioDeviceEnumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            _audioDeviceNotifications = _audioDeviceEnumerator.CreateNotificationClient();
            _audioDeviceNotifications.DefaultDeviceChanged += OnDefaultAudioDeviceChanged;
        }
        catch
        {
            // Optional hot-swap notification; ignore on restricted audio subsystems
            UnsubscribeAudioDeviceNotifications();
        }
    }

    private void UnsubscribeAudioDeviceNotifications()
    {
        if (_audioDeviceNotifications is not null)
        {
            _audioDeviceNotifications.DefaultDeviceChanged -= OnDefaultAudioDeviceChanged;
            try { _audioDeviceNotifications.Dispose(); } catch { }
            _audioDeviceNotifications = null;
        }
        try { _audioDeviceEnumerator?.Dispose(); } catch { }
        _audioDeviceEnumerator = null;
    }

    private void OnDefaultAudioDeviceChanged(object? sender, NAudio.CoreAudioApi.DefaultDeviceChangedEventArgs e)
    {
        if (!_aiHandsFreeActive || e.Flow != NAudio.CoreAudioApi.DataFlow.Capture)
            return;

        // Rebind hands-free VAD capture automatically when the Windows default microphone changes
        Dispatcher.BeginInvoke(() =>
        {
            if (!_aiHandsFreeActive)
                return;
            try
            {
                StartVadRecorderInstance();
                TxtStatusBarLeft.Text = OperationsTranslations.ForLanguage(_uiLanguage)["aiHandsFreeStatusOn"];
            }
            catch
            {
                // Keep existing state if the new endpoint is still initializing
            }
        });
    }

    private void StopAiHandsFree()
    {
        _aiHandsFreeActive = false;
        UnsubscribeAudioDeviceNotifications();
        lock (_vadLock)
        {
            _vadPreBuffer.Clear();
            _vadUtterancePcm?.Dispose();
            _vadUtterancePcm = null;
            _vadInSpeech = false;
            _vadSpeechFrames = 0;
            _vadSilenceFrames = 0;
        }
        try
        {
            if (_aiVadRecorder is not null)
            {
                _aiVadRecorder.DataAvailable -= OnVadAudioDataAvailable;
                _aiVadRecorder.StopRecording();
                _aiVadRecorder.Dispose();
            }
        }
        catch
        {
        }
        _aiVadRecorder = null;

        if (BtnAiHandsFree is not null)
        {
            string tip = OperationsTranslations.ForLanguage(_uiLanguage)["aiHandsFreeBtn"];
            BtnAiHandsFree.Content = "🎙";
            BtnAiHandsFree.ToolTip = tip;
            System.Windows.Automation.AutomationProperties.SetName(BtnAiHandsFree, tip);
            BtnAiHandsFree.SetResourceReference(BackgroundProperty, "HudPanelAltBrush");
            BtnAiHandsFree.SetResourceReference(ForegroundProperty, "HudTextBrush");
        }
    }

    private void OnVadAudioDataAvailable(object? sender, NAudio.Wave.WaveInEventArgs args)
    {
        if (!_aiHandsFreeActive || args.BytesRecorded <= 0)
            return;

        var (rms, zcr) = ComputePcm16Features(args.Buffer, args.BytesRecorded);

        // While processing an LLM request, ignore audio; while TTS is speaking, allow sustained loud barge-in
        if (_aiHandsFreeProcessing)
        {
            ResetVadBufferState();
            return;
        }

        if (_alertSpeechService.IsSpeaking)
        {
            double bargeThreshold = Math.Max(0.065, _vadNoiseFloor * 4.5);
            if (rms >= bargeThreshold && zcr is >= 0.03 and <= 0.45)
            {
                _vadBargeInFrames++;
                if (_vadBargeInFrames >= 3)
                {
                    _vadBargeInFrames = 0;
                    _alertSpeechService.StopSpeaking();
                }
                else
                {
                    return;
                }
            }
            else
            {
                _vadBargeInFrames = 0;
                ResetVadBufferState();
                return;
            }
        }
        else
        {
            _vadBargeInFrames = 0;
        }

        // Adaptive noise floor (EMA updated during non-speech frames) + ZCR filter to reject engine rumble
        double dynamicThreshold = Math.Clamp(_vadNoiseFloor * 2.5, 0.015, 0.055);
        bool isVoiceFrame = rms >= dynamicThreshold && zcr is >= 0.02 and <= 0.50;
        if (!isVoiceFrame && !_vadInSpeech)
            _vadNoiseFloor = (_vadNoiseFloor * 0.94) + (Math.Min(rms, 0.04) * 0.06);

        byte[] chunk = new byte[args.BytesRecorded];
        Buffer.BlockCopy(args.Buffer, 0, chunk, 0, args.BytesRecorded);

        byte[]? readyPcm = null;
        bool startedNow = false;

        lock (_vadLock)
        {
            if (!_vadInSpeech)
            {
                _vadPreBuffer.Enqueue(chunk);
                while (_vadPreBuffer.Count > 6)
                    _vadPreBuffer.Dequeue();

                if (isVoiceFrame)
                {
                    _vadInSpeech = true;
                    _vadSpeechFrames = 1;
                    _vadSilenceFrames = 0;
                    _vadUtterancePcm = new MemoryStream();
                    while (_vadPreBuffer.Count > 0)
                    {
                        byte[] pre = _vadPreBuffer.Dequeue();
                        _vadUtterancePcm.Write(pre, 0, pre.Length);
                    }
                    startedNow = true;
                }
            }
            else
            {
                _vadUtterancePcm?.Write(chunk, 0, chunk.Length);
                if (isVoiceFrame)
                {
                    _vadSpeechFrames++;
                    _vadSilenceFrames = 0;
                }
                else
                {
                    _vadSilenceFrames++;
                }

                // Faster endpointing: auto-send after 0.6 s of silence (6 x 100 ms chunks) or 15 s limit
                bool endedBySilence = _vadSilenceFrames >= 6;
                bool maxDurationReached = (_vadUtterancePcm?.Length ?? 0) >= 16000 * 2 * 15;
                if (endedBySilence || maxDurationReached)
                {
                    if (_vadSpeechFrames >= 3 && _vadUtterancePcm is not null)
                    {
                        byte[] rawPcm = _vadUtterancePcm.ToArray();
                        // Trim excess trailing silence (keep ~200 ms tail = 6,400 bytes at 16 kHz 16-bit mono) to shrink WAV upload
                        if (endedBySilence && _vadSilenceFrames > 2)
                        {
                            int trimBytes = Math.Max(0, (_vadSilenceFrames - 2) * 3200);
                            int keepLen = Math.Max(6400, rawPcm.Length - trimBytes);
                            keepLen -= keepLen % 2; // maintain 16-bit sample alignment
                            if (keepLen < rawPcm.Length)
                                rawPcm = rawPcm[..keepLen];
                        }
                        readyPcm = rawPcm;
                    }

                    _vadUtterancePcm?.Dispose();
                    _vadUtterancePcm = null;
                    _vadInSpeech = false;
                    _vadSpeechFrames = 0;
                    _vadSilenceFrames = 0;
                }
            }
        }

        if (startedNow)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_aiHandsFreeActive)
                {
                    string tip = OperationsTranslations.ForLanguage(_uiLanguage)["aiHandsFreeSpeaking"];
                    BtnAiHandsFree.Content = "🗣";
                    BtnAiHandsFree.ToolTip = tip;
                    System.Windows.Automation.AutomationProperties.SetName(BtnAiHandsFree, tip);
                }
            });
        }

        if (readyPcm is { Length: > 0 })
        {
            _aiHandsFreeProcessing = true;
            byte[] wav = WrapPcm16AsWav(readyPcm, 16000, 1);
            _ = Dispatcher.BeginInvoke(async () => await HandleVadUtteranceAsync(wav));
        }
    }

    private async Task HandleVadUtteranceAsync(byte[] wavBytes)
    {
        var ops = OperationsTranslations.ForLanguage(_uiLanguage);
        try
        {
            SetAiBusy(true);
            _aiStreamLineOpen = false;
            DateTime sentAt = DateTime.Now;
            bool showCmdrImmediately = string.Equals(_configService.Current.AiActiveProvider, "gemini", StringComparison.OrdinalIgnoreCase)
                && !_configService.Current.AiRequireWakeWord;
            if (showCmdrImmediately)
                AppendAiChatLine("CMDR", "🎤 [Comando de voz]", sentAt);
            TxtStatusBarLeft.Text = ops["aiVoiceProcessing"];
            var (_, transcript, reply) = await _aiCopilot.AskWithAudioWavAsync(wavBytes, isHandsFree: true);
            if (!showCmdrImmediately && !string.IsNullOrWhiteSpace(transcript))
                AppendAiChatLine("CMDR", transcript, sentAt);
            if (!string.IsNullOrWhiteSpace(reply))
                FinishAiStreamOrAppend(GetCovasSpeakerLabel(), reply);
            else if (_aiStreamLineOpen)
                CloseAiStreamLine();
            UpdateAiLatencyStatusBar();
        }
        finally
        {
            SetAiBusy(false);
            _aiHandsFreeProcessing = false;
            if (_aiHandsFreeActive)
            {
                BtnAiHandsFree.Content = "🟢";
                BtnAiHandsFree.ToolTip = ops["aiHandsFreeListening"];
                System.Windows.Automation.AutomationProperties.SetName(BtnAiHandsFree, ops["aiHandsFreeListening"]);
            }
        }
    }

    private void ResetVadBufferState()
    {
        lock (_vadLock)
        {
            _vadPreBuffer.Clear();
            _vadUtterancePcm?.Dispose();
            _vadUtterancePcm = null;
            _vadInSpeech = false;
            _vadSpeechFrames = 0;
            _vadSilenceFrames = 0;
        }
    }

    private static (double Rms, double Zcr) ComputePcm16Features(byte[] buffer, int bytesRecorded)
    {
        int samples = bytesRecorded / 2;
        if (samples <= 0) return (0, 0);
        double sumSq = 0;
        int zeroCrossings = 0;
        short prev = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = BitConverter.ToInt16(buffer, i * 2);
            double norm = s / 32768.0;
            sumSq += norm * norm;
            if (i > 0 && ((s >= 0 && prev < 0) || (s < 0 && prev >= 0)))
                zeroCrossings++;
            prev = s;
        }
        return (Math.Sqrt(sumSq / samples), (double)zeroCrossings / samples);
    }

    private static byte[] WrapPcm16AsWav(byte[] pcmData, int sampleRate, int channels)
    {
        byte[] conditioned = AudioPlaybackHelper.ProcessVoicePcmForRecognition(pcmData, sampleRate, channels);
        using var ms = new MemoryStream();
        using (var writer = new NAudio.Wave.WaveFileWriter(new NonClosingStream(ms), new NAudio.Wave.WaveFormat(sampleRate, 16, channels)))
        {
            writer.Write(conditioned, 0, conditioned.Length);
        }
        return ms.ToArray();
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { /* No cerrar inner MemoryStream */ }
    }

    private bool _aiStreamLineOpen;

    private string GetCovasSpeakerLabel()
    {
        if (string.Equals(_configService.Current.AiThinkingMode, "auto", StringComparison.OrdinalIgnoreCase))
            return _aiCopilot.LastThinkingUsed ? "COVAS 🧠" : "COVAS ⚡";
        return "COVAS";
    }

    private void UpdateAiLatencyStatusBar()
    {
        if (_aiCopilot.LastResponseLatencyMs <= 0)
            return;

        string ttft = _aiCopilot.LastFirstTokenLatencyMs > 0
            ? $" (1ª palabra: {_aiCopilot.LastFirstTokenLatencyMs / 1000.0:0.0} s)"
            : "";
        string thinkTag = string.Equals(_configService.Current.AiThinkingMode, "auto", StringComparison.OrdinalIgnoreCase)
            ? (_aiCopilot.LastThinkingUsed ? " · 🧠 Thinking" : " · ⚡ Rápido")
            : "";
        TxtStatusBarLeft.Text = $"COVAS: {_aiCopilot.LastResponseLatencyMs / 1000.0:0.0} s{ttft}{thinkTag} · {_aiCopilot.GetActiveProviderSummary()}";
    }

    private void AppendAiStreamChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk) || TxtAiChatHistory is null)
            return;
        if (!_aiStreamLineOpen)
        {
            _aiStreamLineOpen = true;
            TxtAiChatHistory.AppendText($"[{DateTime.Now:HH:mm:ss}] {GetCovasSpeakerLabel()}: ");
        }
        TxtAiChatHistory.AppendText(chunk);
        TxtAiChatHistory.ScrollToEnd();
    }

    private void CloseAiStreamLine()
    {
        if (!_aiStreamLineOpen || TxtAiChatHistory is null)
            return;
        _aiStreamLineOpen = false;
        TxtAiChatHistory.AppendText("\n\n");
        TxtAiChatHistory.ScrollToEnd();
    }

    private void FinishAiStreamOrAppend(string speaker, string fullReply)
    {
        if (_aiStreamLineOpen)
        {
            // Append any trailing action log lines (e.g., ⚡ / 🛰 / 📝) that were produced after the streamed prose
            int sep = fullReply?.IndexOf("\n\n", StringComparison.Ordinal) ?? -1;
            if (sep >= 0 && fullReply is not null)
            {
                string extra = fullReply[(sep + 2)..].Trim();
                if (!string.IsNullOrWhiteSpace(extra))
                    TxtAiChatHistory.AppendText($"\n{extra}");
            }
            CloseAiStreamLine();
            return;
        }
        AppendAiChatLine(speaker, fullReply);
    }

    private void AppendAiChatLine(string speaker, string message, DateTime? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        if (_aiStreamLineOpen)
            CloseAiStreamLine();
        DateTime ts = timestamp ?? DateTime.Now;
        string block = $"[{ts:HH:mm:ss}] {speaker}: {message}\n\n";
        TxtAiChatHistory.AppendText(block);
        TxtAiChatHistory.ScrollToEnd();
    }

    /// <summary>
    /// Saves the commander's live planetary coordinates as a surface bookmark when requested by COVAS.
    /// </summary>
    private void HandleAiSurfaceBookmarkSave(string label)
    {
        if (TxtBookmarkLabel is null)
            return;
        TxtBookmarkLabel.Text = label;
        BtnSaveSurfaceBookmark_Click(this, new RoutedEventArgs());
    }

    /// <summary>
    /// Targets either the latest exobiology sample or a saved surface bookmark in the live surface compass.
    /// </summary>
    private void HandleAiSurfaceNavigate(string target)
    {
        if (target.StartsWith("COORDS:", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = target["COORDS:".Length..].Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && TxtTargetLat is not null && TxtTargetLon is not null)
            {
                TxtTargetLat.Text = parts[0];
                TxtTargetLon.Text = parts[1];
                UpdateSurfaceGuidance(_fileWatcher.State);
            }
            return;
        }

        if (target.Equals("exo", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("exo", StringComparison.OrdinalIgnoreCase))
        {
            BtnTargetLastExoSample_Click(this, new RoutedEventArgs());
            return;
        }

        var match = _configService.Current.SurfaceBookmarks?
            .FirstOrDefault(b => b.Label.Contains(target, StringComparison.OrdinalIgnoreCase));
        if (match is not null && TxtTargetLat is not null && TxtTargetLon is not null)
        {
            TxtTargetLat.Text = match.Latitude.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
            TxtTargetLon.Text = match.Longitude.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
            UpdateSurfaceGuidance(_fileWatcher.State);
        }
    }

    /// <summary>
    /// Runs a Spansh ship/module search with optional Li Yong-Rui discount filter from COVAS.
    /// </summary>
    private void HandleAiSpanshItemSearch(string query, bool lyrDiscount)
    {
        if (TxtSpanshItemQuery is null)
            return;
        TxtSpanshItemQuery.Text = query;
        if (ChkSpanshLyrDiscount is not null)
            ChkSpanshLyrDiscount.IsChecked = lyrDiscount;
        BtnSearchSpanshItem_Click(this, new RoutedEventArgs());
    }

    /// <summary>
    /// Opens the active ship loadout in EDSY or Coriolis when requested by COVAS.
    /// </summary>
    private void HandleAiExportShip(string target)
    {
        if (target.Equals("coriolis", StringComparison.OrdinalIgnoreCase))
            BtnExportCoriolis_Click(this, new RoutedEventArgs());
        else
            BtnExportEdsy_Click(this, new RoutedEventArgs());
    }

    /// <summary>
    /// Switches the active HUDnopy tab or toggles the floating Mini-HUD from a COVAS command.
    /// </summary>
    private void HandleAiUiNavigation(string target)
    {
        if (target is "minihud" or "mini-hud" or "mini")
        {
            ToggleMiniHud();
            return;
        }
        if (target is "fullscreen" or "pantalla completa")
        {
            if (!_isFullscreen)
                ToggleFullscreen();
            return;
        }
        if (target is "normal" or "windowed" or "ventana")
        {
            if (_isMiniHud)
                ToggleMiniHud();
            else if (_isFullscreen)
                ToggleFullscreen();
            return;
        }

        int? tabIndex = target switch
        {
            "dashboard" or "panel" => 0,
            "flight" or "vuelo" or "combat" => 1,
            "navigation" or "navegacion" or "carrier" or "route" => 2,
            "operations" or "operaciones" or "missions" or "mining" => 3,
            "inventory" or "inventario" or "market" or "cargo" or "shipyard" => 4,
            "activity" or "actividad" or "galnet" or "journal" => 5,
            "controls" or "botonera" or "deck" => 6,
            _ => null
        };

        if (tabIndex.HasValue)
            SwitchView(tabIndex.Value);
    }

    private void BtnAiPreFlightCheck_Click(object sender, RoutedEventArgs e)
    {
        _ = RunAiQuickActionAsync(null, "COVAS (Pre-Flight)", () => _aiCopilot.RunPreFlightCheckAsync());
    }

    private void HandleAiSpanshMining(string mineral)
    {
        if (CmbSpanshMiningMineral is null)
            return;
        CmbSpanshMiningMineral.SelectedValue = mineral;
        BtnSearchMiningRings_Click(this, new RoutedEventArgs());
    }

    private void HandleAiWishlistAdd(string blueprintId)
    {
        if (string.Equals(blueprintId, "__CLEAR__", StringComparison.OrdinalIgnoreCase))
        {
            _configService.Current.EngineeringWishlistBlueprints?.Clear();
            _configService.Save();
            _lastWishlistSignature = null;
            UpdateEngineeringWishlistUi(_fileWatcher.State);
            return;
        }

        var match = EngineeringWishlistService.Catalog.FirstOrDefault(b =>
            string.Equals(b.Id, blueprintId, StringComparison.OrdinalIgnoreCase) ||
            b.DisplayName.Contains(blueprintId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return;

        _configService.Current.EngineeringWishlistBlueprints ??= new List<string>();
        if (!_configService.Current.EngineeringWishlistBlueprints.Contains(match.Id, StringComparer.OrdinalIgnoreCase))
        {
            _configService.Current.EngineeringWishlistBlueprints.Add(match.Id);
            _configService.Save();
            _lastWishlistSignature = null;
            UpdateEngineeringWishlistUi(_fileWatcher.State);
        }
    }

    private void HandleAiTritiumCalc(double lightYears)
    {
        if (TxtCarrierRouteLy is null)
            return;
        TxtCarrierRouteLy.Text = Math.Round(lightYears).ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdateCarrierTritiumCalc(_fileWatcher.State);
    }

    private void HandleAiVoiceRateAdjust(int delta)
    {
        int next = delta == 0 ? 0 : Math.Clamp((int)SldSpeechRate.Value + delta, -5, 5);
        SldSpeechRate.Value = next;
    }

    private void BtnExportAiChat_Click(object sender, RoutedEventArgs e)
    {
        if (TxtAiChatHistory is null || string.IsNullOrWhiteSpace(TxtAiChatHistory.Text))
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
            FileName = $"HUDnopy-COVAS-{DateTime.Now:yyyyMMdd-HHmm}.md"
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, "# HUDnopy COVAS Chat Log\n\n" + TxtAiChatHistory.Text, System.Text.Encoding.UTF8);
            TxtStatusBarLeft.Text = string.Format(OperationsTranslations.ForLanguage(_uiLanguage)["exportSuccess"], Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            TxtStatusBarLeft.Text = ex.Message;
        }
    }

    // =========================================================================
    // Global Push-to-Talk Hotkey (Win32 RegisterHotKey — works in Fullscreen/VR)
    // =========================================================================

    private const int WmHotKey = 0x0312;
    private const int GlobalPttHotKeyId = 0x4855; // "HU"
    private System.Windows.Interop.HwndSource? _hotkeyHwndSource;
    private bool _globalPttRegistered;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void RegisterGlobalPttHotkey()
    {
        UnregisterGlobalPttHotkey();

        if (!_configService.Current.AiGlobalPttHotkeyEnabled)
            return;

        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        if (helper.Handle == IntPtr.Zero)
            return;

        _hotkeyHwndSource ??= System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);
        if (_hotkeyHwndSource is null)
            return;

        _hotkeyHwndSource.RemoveHook(GlobalHotKeyWndProc);
        _hotkeyHwndSource.AddHook(GlobalHotKeyWndProc);

        var (mods, vk) = ParseGlobalHotkey(_configService.Current.AiGlobalPttHotkey);
        _globalPttRegistered = RegisterHotKey(helper.Handle, GlobalPttHotKeyId, mods, vk);
    }

    private void UnregisterGlobalPttHotkey()
    {
        if (!_globalPttRegistered)
            return;
        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        if (helper.Handle != IntPtr.Zero)
            UnregisterHotKey(helper.Handle, GlobalPttHotKeyId);
        _globalPttRegistered = false;
    }

    private IntPtr GlobalHotKeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == GlobalPttHotKeyId)
        {
            handled = true;
            Dispatcher.BeginInvoke(() => BtnAiVoiceInput_Click(this, new RoutedEventArgs()));
        }
        return IntPtr.Zero;
    }

    private static (uint Modifiers, uint VirtualKey) ParseGlobalHotkey(string? spec)
    {
        const uint ModAlt = 0x0001;
        const uint ModControl = 0x0002;
        const uint ModShift = 0x0004;
        const uint ModNoRepeat = 0x4000;

        return (spec ?? "Ctrl+Alt+V").Trim() switch
        {
            "Ctrl+Alt+Space" => (ModControl | ModAlt | ModNoRepeat, 0x20),
            "Ctrl+Shift+V" => (ModControl | ModShift | ModNoRepeat, 0x56),
            "Alt+F1" => (ModAlt | ModNoRepeat, 0x70),
            "F13" => (ModNoRepeat, 0x7C),
            _ => (ModControl | ModAlt | ModNoRepeat, 0x56) // Ctrl+Alt+V
        };
    }

}
