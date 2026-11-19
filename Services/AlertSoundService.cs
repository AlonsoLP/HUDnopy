// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/AlertSoundService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Audio playback manager for built-in and user-supplied alert sound files.
// -----------------------------------------------------------------------------

using System.IO;
using System.Text.Json;
using HUDnopy.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using HUDnopy.Localization;

namespace HUDnopy.Services;

/// <summary>
/// Audio playback manager for built-in and user-supplied alert sound files.
/// </summary>
public sealed record AlertSoundProfileOption(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed class AlertSoundService
{
    private const int MinimumSampleRate = 8000;
    private const int MaximumSampleRate = 48000;
    private const int MaximumNotesPerProfile = 32;
    private const int MaximumNoteDurationMilliseconds = 3000;
    public const string FileProfilePrefix = "file:";
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".aif", ".aiff", ".m4a", ".aac", ".wma", ".flac" };
    private readonly string _baseDirectory = AppContext.BaseDirectory;
    private readonly ConfigService _configService;
    private readonly SoundLibrary _library;
    private readonly Dictionary<string, SoundProfile> _profiles;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _synthesizedWaveCache =
        new(StringComparer.OrdinalIgnoreCase);
    private SoundCatalog _soundCatalog = new([], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []);

    public IReadOnlyList<AlertSoundProfileOption> Profiles => _soundCatalog.Options;
    public IReadOnlyList<string> DirectoryErrors => _soundCatalog.Errors;
    public int MigratedProfileCount { get; private set; }
    public string? MigrationError { get; private set; }
    public event Action<string>? PlaybackFailed;

    public AlertSoundService(AlertService alertService, ConfigService configService)
    {
        _configService = configService;
        var errors = SoundProfileTranslations.Errors(configService.Current.InterfaceLanguage);
        string libraryPath = Path.Combine(_baseDirectory, "Audio", "alert-sound-library.json");
        if (!File.Exists(libraryPath))
            throw new FileNotFoundException(errors["libraryMissing"], libraryPath);

        try
        {
            using var stream = File.OpenRead(libraryPath);
            _library = JsonSerializer.Deserialize<SoundLibrary>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidDataException(errors["libraryEmpty"]);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(string.Format(errors["libraryMalformed"], ex.Message), ex);
        }

        ValidateLibrary(_library, libraryPath, errors);
        _profiles = _library.Profiles.ToDictionary(profile => profile.Id, StringComparer.OrdinalIgnoreCase);
        ReloadProfiles();

        string[] retiredAutomaticProfiles = { "AutoCombat", "AutoTransport", "AutoExploration" };
        foreach (var preference in configService.Current.AlertPreferences.Values)
        {
            if (retiredAutomaticProfiles.Contains(preference.SoundProfile, StringComparer.OrdinalIgnoreCase) ||
                (!_profiles.ContainsKey(preference.SoundProfile) &&
                 !preference.SoundProfile.StartsWith(FileProfilePrefix, StringComparison.OrdinalIgnoreCase)))
            {
                preference.SoundProfile = "Auto";
                MigratedProfileCount++;
            }
        }

        if (MigratedProfileCount > 0)
        {
            try
            {
                configService.SaveChecked();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                MigrationError = ex.Message;
            }
        }

        alertService.AlertTriggered += OnAlertTriggered;
    }

    public void ReloadProfiles()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (string directory in _configService.Current.AlertSoundDirectories)
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(directory))
                {
                    if (SupportedExtensions.Contains(Path.GetExtension(path)))
                    {
                        string fullPath = Path.GetFullPath(path);
                        files.TryAdd(FileProfilePrefix + fullPath, fullPath);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                errors.Add($"{directory}: {ex.Message}");
            }
        }
        var options = _library.Profiles
            .OrderBy(profile => profile.Id == "Auto" ? 0 : 1)
            .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(profile => new AlertSoundProfileOption(profile.Id, profile.Name))
            .Concat(files.OrderBy(pair => pair.Value, StringComparer.CurrentCultureIgnoreCase)
                .Select(pair => new AlertSoundProfileOption(pair.Key, $"{Path.GetFileName(pair.Value)} — {Path.GetDirectoryName(pair.Value)}")))
            .ToArray();
        _soundCatalog = new SoundCatalog(options, files, errors);
    }

    public void PlayPreview(string alertId, string profileId) =>
        Play(alertId, AlertSeverity.Information, profileId);

    private void OnAlertTriggered(AlertNotification alert, AlertChannelPreferences preferences)
    {
        if (preferences.SoundEnabled)
            Play(alert.AlertId, alert.Severity, preferences.SoundProfile);
    }

    private void Play(string alertId, AlertSeverity severity, string? selectedProfile)
    {
        double volume = Math.Clamp(_configService.Current.AlertSoundVolumePercent, 0, 100) / 100d;
        _ = Task.Run(() =>
        {
            Stream? stream = null;
            WaveStream? reader = null;
            WaveOutEvent? output = null;
            int disposed = 0;
            string profileName = selectedProfile ?? "Auto";

            void DisposePlayback()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                    return;

                output?.Dispose();
                reader?.Dispose();
                stream?.Dispose();
            }

            try
            {
                if (selectedProfile?.StartsWith(FileProfilePrefix, StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (!_soundCatalog.Files.TryGetValue(selectedProfile, out string? path))
                        throw new FileNotFoundException(
                            string.Format(SettingsTranslations.ForLanguage(_configService.Current.InterfaceLanguage)["soundMissing"],
                                selectedProfile[FileProfilePrefix.Length..]));
                    profileName = Path.GetFileName(path);
                    reader = new AudioFileReader(path);
                }
                else
                {
                    SoundProfile profile = ResolveProfile(alertId, severity, selectedProfile);
                    profileName = SoundProfileTranslations.Name(profile.Id, profile.Name, _configService.Current.InterfaceLanguage);
                    stream = CreateWaveStream(profile, 1);
                    reader = new WaveFileReader(stream);
                }
                output = new WaveOutEvent
                {
                    DesiredLatency = 80,
                    NumberOfBuffers = 2
                };
                output.PlaybackStopped += (_, eventArgs) =>
                {
                    if (eventArgs.Exception is not null)
                    {
                        string language = _configService.Current.InterfaceLanguage;
                        PlaybackFailed?.Invoke(string.Format(SettingsTranslations.ForLanguage(language)["playbackStopped"],
                            profileName, eventArgs.Exception.Message));
                    }

                    _ = Task.Run(DisposePlayback);
                };
                output.Init(new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = (float)volume });
                output.Play();
            }
            catch (Exception exception)
            {
                DisposePlayback();
                string language = _configService.Current.InterfaceLanguage;
                PlaybackFailed?.Invoke(string.Format(SettingsTranslations.ForLanguage(language)["playbackError"],
                    profileName, exception.Message));
            }
        });
    }

    private SoundProfile ResolveProfile(string alertId, AlertSeverity severity, string? selectedProfileId)
    {
        string profileId = _profiles.ContainsKey(selectedProfileId ?? "")
            ? selectedProfileId!
            : "Auto";
        var profile = _profiles[profileId];

        if (profile.AutomaticRules is null && profile.SeverityDefaults is null)
            return profile;

        string? resolvedProfileId = null;
        if (profile.AutomaticRules is not null)
            profile.AutomaticRules.TryGetValue(alertId, out resolvedProfileId);
        if (resolvedProfileId is null &&
            profile.SeverityDefaults is not null &&
            profile.SeverityDefaults.TryGetValue(severity.ToString(), out var severityDefault))
            resolvedProfileId = severityDefault;

        return resolvedProfileId is not null
            ? _profiles[resolvedProfileId]
            : throw new InvalidDataException(string.Format(SoundProfileTranslations.Errors(_configService.Current.InterfaceLanguage)["automaticUnresolved"], profile.Id, alertId, severity));
    }

    private MemoryStream CreateWaveStream(SoundProfile profile, double volume)
    {
        if (volume == 1.0)
        {
            byte[] cachedBytes = _synthesizedWaveCache.GetOrAdd(
                profile.Id,
                _ => SynthesizeWaveBytes(profile, 1.0));
            return new MemoryStream(cachedBytes, writable: false);
        }

        return new MemoryStream(SynthesizeWaveBytes(profile, volume), writable: false);
    }

    private byte[] SynthesizeWaveBytes(SoundProfile profile, double volume)
    {
        int gapSamples = _library.GapMilliseconds * _library.SampleRate / 1000;
        int sampleCount = profile.Notes.Sum(note =>
            _library.SampleRate * note.DurationMilliseconds / 1000 + gapSamples);
        int dataSize = checked(sampleCount * sizeof(short));
        var buffer = new byte[44 + dataSize];
        using var audio = new MemoryStream(buffer, writable: true);

        using (var writer = new BinaryWriter(audio, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + dataSize);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(_library.SampleRate);
            writer.Write(_library.SampleRate * sizeof(short));
            writer.Write((short)sizeof(short));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(dataSize);

            foreach (var note in profile.Notes)
            {
                int toneSamples = _library.SampleRate * note.DurationMilliseconds / 1000;
                for (int sample = 0; sample < toneSamples; sample++)
                {
                    double progress = (double)sample / toneSamples;
                    double envelope = Math.Min(1, Math.Min(progress * 12, (1 - progress) * 5));
                    double phase = 2 * Math.PI * note.Frequency * sample / _library.SampleRate;
                    double fundamental = profile.Waveform switch
                    {
                        "Saturated" => Math.Tanh(Math.Sin(phase) * 2.4),
                        "Square" => Math.Sign(Math.Sin(phase)),
                        "Triangle" => 2 / Math.PI * Math.Asin(Math.Sin(phase)),
                        "Sawtooth" => 2 * (phase / (2 * Math.PI) -
                            Math.Floor(0.5 + phase / (2 * Math.PI))),
                        _ => Math.Sin(phase)
                    };
                    double harmonic = Math.Sin(phase * 2) * profile.HarmonicRatio;
                    double modulation = profile.ModulationFrequency > 0
                        ? 1 - profile.ModulationDepth / 2 +
                          profile.ModulationDepth / 2 *
                          Math.Sin(2 * Math.PI * profile.ModulationFrequency * sample / _library.SampleRate)
                        : 1;
                    double normalized = Math.Clamp(
                        (fundamental + harmonic) * modulation * envelope * profile.Amplitude * volume,
                        -1,
                        1);
                    writer.Write((short)(normalized * short.MaxValue));
                }

                for (int sample = 0; sample < gapSamples; sample++)
                    writer.Write((short)0);
            }
        }

        return buffer;
    }

    private static void ValidateLibrary(SoundLibrary library, string sourcePath, IReadOnlyDictionary<string, string> errors)
    {
        if (library.Profiles is null ||
            library.SampleRate is < MinimumSampleRate or > MaximumSampleRate ||
            library.GapMilliseconds is < 0 or > 500 ||
            library.Profiles.Count == 0)
            throw new InvalidDataException(string.Format(errors["libraryInvalid"], sourcePath));

        var profiles = new Dictionary<string, SoundProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in library.Profiles)
        {
            if (profile is null ||
                string.IsNullOrWhiteSpace(profile.Id) ||
                string.IsNullOrWhiteSpace(profile.Name) ||
                !profiles.TryAdd(profile.Id, profile))
                throw new InvalidDataException(string.Format(errors["profileDuplicate"], sourcePath));

            bool isAutomatic = profile.AutomaticRules is not null || profile.SeverityDefaults is not null;
            if (isAutomatic)
            {
                if (profile.Notes is null ||
                    profile.Notes.Count > 0 ||
                    profile.AutomaticRules is null ||
                    profile.SeverityDefaults is null ||
                    profile.AutomaticRules.Any(rule =>
                        !AlertDefinitions.All.Any(definition => definition.Id == rule.Key) ||
                        string.IsNullOrWhiteSpace(rule.Value)) ||
                    profile.SeverityDefaults.Any(rule =>
                        !Enum.GetNames<AlertSeverity>().Contains(rule.Key) ||
                        string.IsNullOrWhiteSpace(rule.Value)))
                    throw new InvalidDataException(string.Format(errors["automaticInvalid"], profile.Id));
            }
            else if (profile.Notes is null ||
                     profile.Notes.Count is < 1 or > MaximumNotesPerProfile ||
                     profile.Notes.Any(note => note is null) ||
                     profile.Notes.Any(note =>
                         note.Frequency <= 0 ||
                         note.Frequency >= library.SampleRate / 2 ||
                         note.DurationMilliseconds is < 20 or > MaximumNoteDurationMilliseconds) ||
                     profile.Amplitude is <= 0 or > 1 ||
                     profile.HarmonicRatio is < -1 or > 1 ||
                     profile.ModulationFrequency is < 0 or > 20 ||
                     profile.ModulationDepth is < 0 or > 1 ||
                     profile.Waveform is not ("Sine" or "Saturated" or "Square" or "Triangle" or "Sawtooth"))
            {
                throw new InvalidDataException(string.Format(errors["recipeInvalid"], profile.Id));
            }
        }

        if (!profiles.ContainsKey("Auto"))
            throw new InvalidDataException(string.Format(errors["automaticMissing"], sourcePath));

        foreach (var profile in library.Profiles.Where(profile =>
                     profile.AutomaticRules is not null || profile.SeverityDefaults is not null))
        {
            IEnumerable<string> targets = profile.AutomaticRules!.Values
                .Concat(profile.SeverityDefaults!.Values);
            foreach (string targetId in targets)
            {
                if (string.IsNullOrWhiteSpace(targetId) ||
                    !profiles.TryGetValue(targetId, out var target) ||
                    target.AutomaticRules is not null ||
                    target.SeverityDefaults is not null)
                    throw new InvalidDataException(string.Format(errors["automaticTarget"], profile.Id, targetId));
            }

            if (!Enum.GetNames<AlertSeverity>().All(name => profile.SeverityDefaults.ContainsKey(name)))
                throw new InvalidDataException(string.Format(errors["automaticSeverity"], profile.Id));
        }
    }

    private sealed record SoundCatalog(
        IReadOnlyList<AlertSoundProfileOption> Options,
        IReadOnlyDictionary<string, string> Files,
        IReadOnlyList<string> Errors);

    private sealed class SoundLibrary
    {
        public int SampleRate { get; set; }
        public int GapMilliseconds { get; set; }
        public List<SoundProfile> Profiles { get; set; } = new();
    }

    private sealed class SoundProfile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public Dictionary<string, string>? AutomaticRules { get; set; }
        public Dictionary<string, string>? SeverityDefaults { get; set; }
        public string Waveform { get; set; } = "Sine";
        public double Amplitude { get; set; } = 0.38;
        public double HarmonicRatio { get; set; } = 0.18;
        public double ModulationFrequency { get; set; }
        public double ModulationDepth { get; set; }
        public List<SoundNote> Notes { get; set; } = new();
    }

    private sealed class SoundNote
    {
        public double Frequency { get; set; }
        public int DurationMilliseconds { get; set; }
    }
}
