// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/AlertSpeechService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Local speech queue coordinating Piper neural TTS and Windows SAPI synthesis.
// -----------------------------------------------------------------------------

using System.IO;
using System.Speech.Synthesis;
using HUDnopy.Models;
using NAudio.Wave;
using HUDnopy.Localization;

namespace HUDnopy.Services;

/// <summary>
/// Local speech queue coordinating Piper neural TTS and Windows SAPI synthesis.
/// </summary>
public sealed class AlertSpeechService
{
    private const int MaxTacticalPendingRequests = 2;
    private const int MaxSequentialPendingRequests = 20;

    private readonly AlertService _alertService;
    private readonly ConfigService _configService;
    private readonly PiperTtsService _piperTts;
    private readonly Lock _queueLock = new();
    private readonly List<SpeechRequest> _pendingRequests = new();
    private SpeechRequest? _currentRequest;
    private bool _workerRunning;
    private HashSet<string>? _installedVoicesCache;

    private sealed class SpeechRequest(
        string Text,
        string? AlertId,
        string? NotificationId,
        AlertSeverity Severity,
        DateTimeOffset EnqueuedAtUtc,
        bool IsManualPreview,
        bool AppendSequential = false) : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public string Text { get; } = Text;
        public string? AlertId { get; } = AlertId;
        public string? NotificationId { get; } = NotificationId;
        public AlertSeverity Severity { get; } = Severity;
        public DateTimeOffset EnqueuedAtUtc { get; } = EnqueuedAtUtc;
        public bool IsManualPreview { get; } = IsManualPreview;
        public bool AppendSequential { get; } = AppendSequential;
        public Task<byte[]?>? PreSynthesizedWavTask { get; set; }
        public CancellationToken CancellationToken => _cts.Token;
        public bool IsCancellationRequested => _cts.IsCancellationRequested;

        public void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => _cts.Dispose();
    }

    public event Action<string>? SpeechFailed;
    public event Action<bool>? SpeakingStateChanged;

    public bool IsSpeaking
    {
        get
        {
            lock (_queueLock)
                return _workerRunning || _currentRequest is not null;
        }
    }

    public AlertSpeechService(AlertService alertService, ConfigService configService, PiperTtsService piperTts)
    {
        _alertService = alertService;
        _configService = configService;
        _piperTts = piperTts;
        _alertService.AlertTriggered += OnAlertTriggered;
        _alertService.AlertsChanged += OnAlertsChanged;
    }

    public IReadOnlyList<string> GetInstalledVoiceNames()
    {
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            string[] names = synthesizer.GetInstalledVoices()
                .Where(voice => voice.Enabled)
                .Select(voice => voice.VoiceInfo.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            _installedVoicesCache = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or PlatformNotSupportedException or
                System.Runtime.InteropServices.COMException)
        {
            SpeechFailed?.Invoke(string.Format(SettingsTranslations.ForLanguage(_configService.Current.InterfaceLanguage)["voicesQueryError"], exception.Message));
            return Array.Empty<string>();
        }
    }

    public void SpeakTest()
    {
        EnqueueSpeech(new SpeechRequest(
            AlertControlTranslations.ForLanguage(_configService.Current.InterfaceLanguage)["speechTest"],
            AlertId: null,
            NotificationId: null,
            Severity: AlertSeverity.Critical,
            EnqueuedAtUtc: DateTimeOffset.UtcNow,
            IsManualPreview: true));
    }

    public void SpeakPreview(AlertDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        string language = _configService.Current.InterfaceLanguage;
        EnqueueSpeech(new SpeechRequest(
            AlertSpeechTranslations.FormatPreview(definition, _configService.Current, language),
            AlertId: definition.Id,
            NotificationId: null,
            Severity: AlertSeverity.Critical,
            EnqueuedAtUtc: DateTimeOffset.UtcNow,
            IsManualPreview: true));
    }

    public void SpeakCustomText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        EnqueueSpeech(new SpeechRequest(
            text.Trim(),
            AlertId: null,
            NotificationId: null,
            Severity: AlertSeverity.Information,
            EnqueuedAtUtc: DateTimeOffset.UtcNow,
            IsManualPreview: true));
    }

    /// <summary>
    /// Enqueues a streamed sentence from COVAS AI. The first sentence interrupts any prior speech;
    /// subsequent sentences append seamlessly in FIFO order without cutting off the previous sentence.
    /// </summary>
    public void SpeakStreamSentence(string text, bool interruptPrevious)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        EnqueueSpeech(new SpeechRequest(
            text.Trim(),
            AlertId: null,
            NotificationId: null,
            Severity: AlertSeverity.Information,
            EnqueuedAtUtc: DateTimeOffset.UtcNow,
            IsManualPreview: interruptPrevious,
            AppendSequential: !interruptPrevious));
    }

    public void StopSpeaking()
    {
        lock (_queueLock)
        {
            foreach (var req in _pendingRequests)
                req.Dispose();
            _pendingRequests.Clear();
            _currentRequest?.Cancel();
        }
    }

    private void OnAlertTriggered(AlertNotification alert, AlertChannelPreferences preferences)
    {
        if (preferences.VoiceEnabled)
        {
            string language = _configService.Current.InterfaceLanguage;
            EnqueueSpeech(new SpeechRequest(
                AlertSpeechTranslations.FormatAlert(alert, language),
                AlertId: alert.AlertId,
                NotificationId: alert.Id,
                Severity: alert.Severity,
                EnqueuedAtUtc: DateTimeOffset.UtcNow,
                IsManualPreview: false));
        }
    }

    private void OnAlertsChanged()
    {
        if (!string.Equals(GetQueueMode(), "tactical", StringComparison.Ordinal))
            return;

        lock (_queueLock)
        {
            for (int i = _pendingRequests.Count - 1; i >= 0; i--)
            {
                var req = _pendingRequests[i];
                if (!req.IsManualPreview && !req.AppendSequential &&
                    !_alertService.IsAlertStillRelevant(req.AlertId, req.NotificationId))
                {
                    req.Dispose();
                    _pendingRequests.RemoveAt(i);
                }
            }
        }
    }

    private string GetQueueMode() =>
        _configService.Current.SpeechQueueMode is "immediate" or "sequential"
            ? _configService.Current.SpeechQueueMode
            : "tactical";

    private TimeSpan GetTransientExpiration() =>
        TimeSpan.FromSeconds(Math.Clamp(_configService.Current.SpeechQueueExpirationSeconds, 2, 15));

    private void ClearPendingRequestsLocked()
    {
        foreach (var req in _pendingRequests)
            req.Dispose();
        _pendingRequests.Clear();
    }

    private void RemovePendingRequestsLocked(Predicate<SpeechRequest> match)
    {
        for (int i = _pendingRequests.Count - 1; i >= 0; i--)
        {
            if (match(_pendingRequests[i]))
            {
                _pendingRequests[i].Dispose();
                _pendingRequests.RemoveAt(i);
            }
        }
    }

    private void EnqueueSpeech(SpeechRequest request)
    {
        bool startWorker = false;
        SpeechRequest? requestToCancel = null;
        string mode = GetQueueMode();

        lock (_queueLock)
        {
            if (request.AppendSequential)
            {
                _pendingRequests.Add(request);
            }
            else if (request.IsManualPreview || mode == "immediate")
            {
                ClearPendingRequestsLocked();
                _pendingRequests.Add(request);
                requestToCancel = _currentRequest;
            }
            else if (mode == "sequential")
            {
                _pendingRequests.Add(request);
                while (_pendingRequests.Count > MaxSequentialPendingRequests)
                {
                    _pendingRequests[0].Dispose();
                    _pendingRequests.RemoveAt(0);
                }
            }
            else
            {
                // Tactical mode: if critical fuel arrives, discard any pending low-fuel alert
                if (string.Equals(request.AlertId, "fuel-critical", StringComparison.OrdinalIgnoreCase))
                {
                    RemovePendingRequestsLocked(r =>
                        string.Equals(r.AlertId, "fuel-low", StringComparison.OrdinalIgnoreCase));
                }

                // Deduplication by alert type (Latest-Wins): replace any pending item with the same AlertId
                if (!string.IsNullOrEmpty(request.AlertId))
                {
                    RemovePendingRequestsLocked(r =>
                        !r.IsManualPreview &&
                        string.Equals(r.AlertId, request.AlertId, StringComparison.OrdinalIgnoreCase));
                }

                _pendingRequests.Add(request);
                // Ordenar por severidad descendente (Critical > Warning > Information) y luego por llegada
                _pendingRequests.Sort(static (a, b) =>
                {
                    int sev = b.Severity.CompareTo(a.Severity);
                    return sev != 0 ? sev : a.EnqueuedAtUtc.CompareTo(b.EnqueuedAtUtc);
                });

                // Cap maximum queue size by discarding lowest-priority / oldest items
                while (_pendingRequests.Count > MaxTacticalPendingRequests)
                {
                    int lastIndex = _pendingRequests.Count - 1;
                    _pendingRequests[lastIndex].Dispose();
                    _pendingRequests.RemoveAt(lastIndex);
                }

                // Interrupt current speech if the new alert has higher severity or escalates from fuel-low to fuel-critical
                if (_currentRequest is { IsManualPreview: false } current &&
                    (request.Severity > current.Severity ||
                     (string.Equals(current.AlertId, "fuel-low", StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(request.AlertId, "fuel-critical", StringComparison.OrdinalIgnoreCase))))
                {
                    requestToCancel = current;
                }
            }

            if (!_workerRunning)
            {
                _workerRunning = true;
                startWorker = true;
            }
        }

        requestToCancel?.Cancel();

        if (startWorker)
        {
            _ = Task.Run(ProcessQueueAsync);
        }
    }

    private async Task ProcessQueueAsync()
    {
        SpeakingStateChanged?.Invoke(true);
        try
        {
            while (true)
            {
                SpeechRequest? next = null;
                lock (_queueLock)
                {
                    while (_pendingRequests.Count > 0)
                    {
                        var candidate = _pendingRequests[0];
                        _pendingRequests.RemoveAt(0);
                        if (IsRequestValidToSpeak(candidate))
                        {
                            next = candidate;
                            _currentRequest = candidate;
                            break;
                        }
                        candidate.Dispose();
                    }

                    if (next is null)
                    {
                        _currentRequest = null;
                        _workerRunning = false;
                        return;
                    }

                    // Pre-synthesize the next queued sentence in parallel while the current sentence plays (Piper TTS)
                    if (_pendingRequests.Count > 0 &&
                        string.Equals(_configService.Current.SpeechEngine, "piper", StringComparison.OrdinalIgnoreCase))
                    {
                        var upcoming = _pendingRequests[0];
                        if (upcoming.PreSynthesizedWavTask is null && !upcoming.IsCancellationRequested)
                        {
                            string voiceId = _configService.Current.PiperVoiceId;
                            string lang = _configService.Current.InterfaceLanguage;
                            int rate = Math.Clamp(_configService.Current.SpeechRate, -5, 5);
                            upcoming.PreSynthesizedWavTask = _piperTts.TrySynthesizeWavAsync(
                                upcoming.Text, voiceId, lang, rate, upcoming.CancellationToken);
                        }
                    }
                }

                try
                {
                    await SpeakSingleAsync(next).ConfigureAwait(false);
                }
                finally
                {
                    next.Dispose();
                }
            }
        }
        finally
        {
            bool restart = false;
            lock (_queueLock)
            {
                _currentRequest = null;
                if (_pendingRequests.Count > 0)
                    restart = true;
                else
                    _workerRunning = false;
            }

            if (restart)
                _ = Task.Run(ProcessQueueAsync);
            else
                SpeakingStateChanged?.Invoke(false);
        }
    }

    private bool IsRequestValidToSpeak(SpeechRequest request)
    {
        if (request.IsManualPreview || request.AppendSequential || !string.Equals(GetQueueMode(), "tactical", StringComparison.Ordinal))
            return true;

        // For ship-status alerts, verify that the condition is still active
        if (AlertService.IsConditionAlert(request.AlertId))
            return _alertService.IsAlertStillRelevant(request.AlertId, request.NotificationId);

        // Si es una alerta transitoria de actividad, verificar que no haya caducado por esperar demasiado en cola
        return DateTimeOffset.UtcNow - request.EnqueuedAtUtc <= GetTransientExpiration();
    }

    private async Task SpeakSingleAsync(SpeechRequest request)
    {
        if (request.IsCancellationRequested)
            return;

        string selectedVoice = _configService.Current.SpeechVoiceName;
        int speechRate = Math.Clamp(_configService.Current.SpeechRate, -5, 5);

        try
        {
            using var audioStream = new MemoryStream();
            bool synthesizedWithPiper = false;

            if (string.Equals(_configService.Current.SpeechEngine, "piper", StringComparison.OrdinalIgnoreCase))
            {
                byte[]? piperWav = request.PreSynthesizedWavTask is not null
                    ? await request.PreSynthesizedWavTask.ConfigureAwait(false)
                    : await _piperTts.TrySynthesizeWavAsync(
                        request.Text,
                        _configService.Current.PiperVoiceId,
                        _configService.Current.InterfaceLanguage,
                        speechRate,
                        request.CancellationToken).ConfigureAwait(false);
                if (piperWav is { Length: > 44 })
                {
                    await audioStream.WriteAsync(piperWav, request.CancellationToken).ConfigureAwait(false);
                    synthesizedWithPiper = true;
                }
                else if (!string.IsNullOrWhiteSpace(_piperTts.LastError))
                {
                    SpeechFailed?.Invoke($"{_piperTts.LastError} (usando Windows SAPI)");
                }
            }

            if (!synthesizedWithPiper)
            {
                using (var synthesizer = new SpeechSynthesizer())
                {
                    if (!string.IsNullOrWhiteSpace(selectedVoice))
                    {
                        var knownVoices = _installedVoicesCache;
                        bool voiceExists = knownVoices != null
                            ? knownVoices.Contains(selectedVoice)
                            : synthesizer.GetInstalledVoices().Any(voice =>
                                voice.Enabled && string.Equals(
                                    voice.VoiceInfo.Name,
                                    selectedVoice,
                                    StringComparison.OrdinalIgnoreCase));
                        if (!voiceExists)
                        {
                            throw new InvalidOperationException(string.Format(
                                SettingsTranslations.ForLanguage(_configService.Current.InterfaceLanguage)["voiceUnavailable"], selectedVoice));
                        }

                        synthesizer.SelectVoice(selectedVoice);
                    }

                    if (request.IsCancellationRequested)
                        return;

                    synthesizer.Rate = speechRate;
                    synthesizer.SetOutputToWaveStream(audioStream);
                    synthesizer.Speak(request.Text);
                    synthesizer.SetOutputToNull();
                }
            }

            if (request.IsCancellationRequested || audioStream.Length <= 44)
                return;

            audioStream.Position = 0;
            using var reader = new WaveFileReader(audioStream);
            using var output = AudioPlaybackHelper.CreateOutputPlayer(latencyMs: 80);

            var tcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, e) => tcs.TrySetResult(e.Exception);

            using var registration = request.CancellationToken.Register(() =>
            {
                try
                {
                    output.Stop();
                }
                catch
                {
                }
                tcs.TrySetResult(null);
            });

            if (request.IsCancellationRequested)
                return;

            output.Init(reader);
            output.Play();

            Exception? playError = await tcs.Task.ConfigureAwait(false);
            if (playError != null && !request.IsCancellationRequested)
                throw playError;
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation due to priority preemption or a new manual test.
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException or IOException or
                PlatformNotSupportedException or System.Runtime.InteropServices.COMException or
                NAudio.MmException)
        {
            if (!request.IsCancellationRequested)
            {
                SpeechFailed?.Invoke(string.Format(SettingsTranslations.ForLanguage(_configService.Current.InterfaceLanguage)["speechError"], exception.Message));
            }
        }
    }
}
