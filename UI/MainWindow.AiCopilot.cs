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
    // Copiloto IA (COVAS) — Configuración y Chat (Fase 1)
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
        }
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

    private void BtnSendAiPrompt_Click(object sender, RoutedEventArgs e)
    {
        _ = SendAiPromptAsync(TxtAiPrompt.Text);
    }

    private void BtnAiQuickStatus_Click(object sender, RoutedEventArgs e)
    {
        _ = SendAiPromptAsync(OperationsTranslations.ForLanguage(_uiLanguage)["aiQuickStatusPrompt"]);
    }

    private async void BtnAiSummarizeGalnet_Click(object sender, RoutedEventArgs e)
    {
        SetAiBusy(true);
        AppendAiChatLine("CMDR", OperationsTranslations.ForLanguage(_uiLanguage)["aiSummarizeGalnet"]);
        var (_, reply) = await _aiCopilot.SummarizeGalnetAsync();
        AppendAiChatLine("COVAS", reply);
        SetAiBusy(false);
    }

    private async void BtnAiSummarizeSession_Click(object sender, RoutedEventArgs e)
    {
        SetAiBusy(true);
        AppendAiChatLine("CMDR", OperationsTranslations.ForLanguage(_uiLanguage)["aiSummarizeSession"]);
        var (_, reply) = await _aiCopilot.SummarizeSessionAsync();
        AppendAiChatLine("COVAS", reply);
        SetAiBusy(false);
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
        AppendAiChatLine("CMDR", trimmed);
        var (_, reply) = await _aiCopilot.AskAsync(trimmed);
        AppendAiChatLine("COVAS", reply);
        SetAiBusy(false);
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
        var ops = OperationsTranslations.ForLanguage(_uiLanguage);
        if (!_isRecordingAiVoice)
        {
            try
            {
                _aiVoiceStream = new MemoryStream();
                var format = new NAudio.Wave.WaveFormat(16000, 16, 1);
                _aiVoiceWriter = new NAudio.Wave.WaveFileWriter(new NonClosingStream(_aiVoiceStream), format);
                _aiVoiceRecorder = new NAudio.Wave.WaveInEvent { WaveFormat = format, BufferMilliseconds = 80 };
                _aiVoiceRecorder.DataAvailable += (_, args) =>
                {
                    try { _aiVoiceWriter?.Write(args.Buffer, 0, args.BytesRecorded); } catch { }
                };
                _aiVoiceRecorder.StartRecording();
                _isRecordingAiVoice = true;
                BtnAiVoiceInput.Content = ops["aiVoiceStopBtn"];
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

        // Detener grabación y enviar al Copiloto IA
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
            BtnAiVoiceInput.Content = ops["aiVoiceBtn"];
            BtnAiVoiceInput.SetResourceReference(BackgroundProperty, "HudPanelAltBrush");
            BtnAiVoiceInput.SetResourceReference(ForegroundProperty, "HudTextBrush");
        }

        if (wavBytes.Length <= 44)
            return;

        SetAiBusy(true);
        TxtStatusBarLeft.Text = ops["aiVoiceProcessing"];
        var (_, transcript, reply) = await _aiCopilot.AskWithAudioWavAsync(wavBytes);
        if (!string.IsNullOrWhiteSpace(transcript))
            AppendAiChatLine("CMDR", transcript);
        AppendAiChatLine("COVAS", reply);
        SetAiBusy(false);
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
            var format = new NAudio.Wave.WaveFormat(16000, 16, 1);
            _aiVadRecorder = new NAudio.Wave.WaveInEvent { WaveFormat = format, BufferMilliseconds = 100 };
            _aiVadRecorder.DataAvailable += OnVadAudioDataAvailable;
            _aiVadRecorder.StartRecording();
            _aiHandsFreeActive = true;
            BtnAiHandsFree.Content = ops["aiHandsFreeListening"];
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

    private void StopAiHandsFree()
    {
        _aiHandsFreeActive = false;
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
            BtnAiHandsFree.Content = OperationsTranslations.ForLanguage(_uiLanguage)["aiHandsFreeBtn"];
            BtnAiHandsFree.SetResourceReference(BackgroundProperty, "HudPanelAltBrush");
            BtnAiHandsFree.SetResourceReference(ForegroundProperty, "HudTextBrush");
        }
    }

    private void OnVadAudioDataAvailable(object? sender, NAudio.Wave.WaveInEventArgs args)
    {
        if (!_aiHandsFreeActive || args.BytesRecorded <= 0)
            return;

        // Ignorar audio mientras COVAS habla o procesa para evitar bucles de eco
        if (_aiHandsFreeProcessing || _alertSpeechService.IsSpeaking)
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
            return;
        }

        double rms = ComputePcm16Rms(args.Buffer, args.BytesRecorded);
        bool isVoiceFrame = rms >= 0.024;
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

                // Enviar automáticamente tras 1.3 s de silencio (13 bloques de 100 ms) o límite de 15 s
                bool endedBySilence = _vadSilenceFrames >= 13;
                bool maxDurationReached = (_vadUtterancePcm?.Length ?? 0) >= 16000 * 2 * 15;
                if (endedBySilence || maxDurationReached)
                {
                    if (_vadSpeechFrames >= 4 && _vadUtterancePcm is not null)
                        readyPcm = _vadUtterancePcm.ToArray();

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
                    BtnAiHandsFree.Content = OperationsTranslations.ForLanguage(_uiLanguage)["aiHandsFreeSpeaking"];
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
            TxtStatusBarLeft.Text = ops["aiVoiceProcessing"];
            var (_, transcript, reply) = await _aiCopilot.AskWithAudioWavAsync(wavBytes);
            if (!string.IsNullOrWhiteSpace(transcript))
                AppendAiChatLine("CMDR", transcript);
            AppendAiChatLine("COVAS", reply);
        }
        finally
        {
            SetAiBusy(false);
            _aiHandsFreeProcessing = false;
            if (_aiHandsFreeActive)
                BtnAiHandsFree.Content = ops["aiHandsFreeListening"];
        }
    }

    private static double ComputePcm16Rms(byte[] buffer, int bytesRecorded)
    {
        int samples = bytesRecorded / 2;
        if (samples <= 0) return 0;
        double sumSq = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = BitConverter.ToInt16(buffer, i * 2);
            double norm = s / 32768.0;
            sumSq += norm * norm;
        }
        return Math.Sqrt(sumSq / samples);
    }

    private static byte[] WrapPcm16AsWav(byte[] pcmData, int sampleRate, int channels)
    {
        using var ms = new MemoryStream();
        using (var writer = new NAudio.Wave.WaveFileWriter(new NonClosingStream(ms), new NAudio.Wave.WaveFormat(sampleRate, 16, channels)))
        {
            writer.Write(pcmData, 0, pcmData.Length);
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

    private void AppendAiChatLine(string speaker, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        string block = $"[{DateTime.Now:HH:mm:ss}] {speaker}:\n{message}\n\n";
        TxtAiChatHistory.AppendText(block);
        TxtAiChatHistory.ScrollToEnd();
    }

}
