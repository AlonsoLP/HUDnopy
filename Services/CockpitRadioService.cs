// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/CockpitRadioService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: In-cockpit internet radio player (NAudio MediaFoundationReader)
//              for Elite Dangerous community stations with COVAS auto-ducking.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HUDnopy.Models;
using NAudio.Wave;

namespace HUDnopy.Services;

/// <summary>
/// Live playback state snapshot shared between Desktop WPF and Web HUD.
/// </summary>
public sealed record RadioStatusSnapshot(
    bool IsPlaying,
    string StationId,
    string StationName,
    int VolumePercent,
    bool IsDucked,
    string StatusMessage);

/// <summary>
/// Streams Elite Dangerous community radio stations (Radio Sidewinder, Hutton Orbital Radio,
/// Lave Radio) and user-added custom URLs with automatic volume ducking during COVAS/alert speech.
/// </summary>
public sealed class CockpitRadioService : IDisposable
{
    public static readonly IReadOnlyList<RadioStationItem> BuiltInStations = new[]
    {
        // Elite Dangerous Community Stations
        new RadioStationItem
        {
            Id = "sidewinder",
            Name = "Radio Sidewinder",
            StreamUrl = "https://radiosidewinder.out.airtime.pro/radiosidewinder_a",
            Genre = "Elite Dangerous Sci-Fi & GalNet News"
        },
        new RadioStationItem
        {
            Id = "lave",
            Name = "Lave Radio",
            StreamUrl = "https://stream.laveradio.com/laveradio.mp3",
            Genre = "Elite Community Talk & Sci-Fi Music"
        },
        new RadioStationItem
        {
            Id = "hutton",
            Name = "Hutton Orbital Radio",
            StreamUrl = "https://stream.zeno.fm/4d27t19r9k8uv",
            Genre = "For the Mug! 24/7 Truckers"
        },

        // Space Exploration, NASA Comms & Deep Ambient
        new RadioStationItem
        {
            Id = "somafm-missioncontrol",
            Name = "SomaFM · Mission Control (NASA)",
            StreamUrl = "https://ice2.somafm.com/missioncontrol-128-mp3",
            Genre = "Live/Historical NASA Audio + Space Ambient"
        },
        new RadioStationItem
        {
            Id = "somafm-deepspaceone",
            Name = "SomaFM · Deep Space One",
            StreamUrl = "https://ice2.somafm.com/deepspaceone-128-mp3",
            Genre = "Deep Space Exploration Ambient"
        },
        new RadioStationItem
        {
            Id = "somafm-spacestation",
            Name = "SomaFM · Space Station Soma",
            StreamUrl = "https://ice2.somafm.com/spacestation-128-mp3",
            Genre = "Orbital Mid-Tempo & Spaced-Out Electronica"
        },
        new RadioStationItem
        {
            Id = "somafm-synphaera",
            Name = "SomaFM · Synphaera Space",
            StreamUrl = "https://ice2.somafm.com/synphaera-128-mp3",
            Genre = "Modern Cosmic Synths & Exosphere Ambient"
        },
        new RadioStationItem
        {
            Id = "somafm-dronezone",
            Name = "SomaFM · Drone Zone",
            StreamUrl = "https://ice2.somafm.com/dronezone-128-mp3",
            Genre = "Atmospheric Minimalist Space Drone"
        },
        new RadioStationItem
        {
            Id = "somafm-darkzone",
            Name = "SomaFM · Dark Zone (The Abyss)",
            StreamUrl = "https://ice2.somafm.com/darkzone-128-mp3",
            Genre = "Deep Black-Hole & Thargoid Sector Ambient"
        },
        new RadioStationItem
        {
            Id = "ambient-sleeping-pill",
            Name = "Ambient Sleeping Pill",
            StreamUrl = "https://radio.stereoscenic.com/asp-s",
            Genre = "Serene Long-Range Exploration Ambient"
        },
        new RadioStationItem
        {
            Id = "stereoscenic-modern",
            Name = "Stereoscenic · Modern Ambient",
            StreamUrl = "https://radio.stereoscenic.com/mod-s",
            Genre = "Cinematic Ambient & Post-Rock Landscapes"
        },

        // Epic, Orchestral & Cinematic Sci-Fi
        new RadioStationItem
        {
            Id = "somafm-n5md",
            Name = "SomaFM · n5MD Cinematic",
            StreamUrl = "https://ice2.somafm.com/n5md-128-mp3",
            Genre = "Emotional Cinematic & Epic Electronic"
        },
        new RadioStationItem
        {
            Id = "klassik-symphonic",
            Name = "Klassik Radio · Symphonic Docking",
            StreamUrl = "https://live.streams.klassikradio.de/klassikradio-deutschland/stream/mp3",
            Genre = "Blue Danube Auto-Dock & Orchestral Classics"
        },
        new RadioStationItem
        {
            Id = "somafm-sf1033",
            Name = "SomaFM · SF 10-33 (Comms + Ambient)",
            StreamUrl = "https://ice2.somafm.com/sf1033-128-mp3",
            Genre = "Live Radio Dispatch Comms + Ambient"
        },

        // Spacesynth, Synthwave & Combat
        new RadioStationItem
        {
            Id = "nightride-spacesynth",
            Name = "Nightride · Spacesynth FM",
            StreamUrl = "https://stream.nightride.fm/spacesynth.mp3",
            Genre = "Cosmic Disco & Retro Space Synth"
        },
        new RadioStationItem
        {
            Id = "nightride-chillsynth",
            Name = "Nightride · Chillsynth FM",
            StreamUrl = "https://stream.nightride.fm/chillsynth.mp3",
            Genre = "Relaxing Supercruise Space Synthwave"
        },
        new RadioStationItem
        {
            Id = "nightride-fm",
            Name = "Nightride · Nightride FM",
            StreamUrl = "https://stream.nightride.fm/nightride.mp3",
            Genre = "Outrun & Retro-Futuristic Synthwave"
        },
        new RadioStationItem
        {
            Id = "nightride-darksynth",
            Name = "Nightride · Darksynth FM (Combat)",
            StreamUrl = "https://stream.nightride.fm/darksynth.mp3",
            Genre = "Heavy Combat & Conflict Zone Darksynth"
        },
        new RadioStationItem
        {
            Id = "somafm-defcon",
            Name = "SomaFM · DEF CON Radio",
            StreamUrl = "https://ice2.somafm.com/defcon-128-mp3",
            Genre = "High-Tech Cyberpunk & Mining Beats"
        },

        // Cockpit Chillout, Downtempo & Lounge
        new RadioStationItem
        {
            Id = "somafm-groovesalad",
            Name = "SomaFM · Groove Salad",
            StreamUrl = "https://ice2.somafm.com/groovesalad-128-mp3",
            Genre = "Classic Downtempo & Cockpit Chillout"
        },
        new RadioStationItem
        {
            Id = "somafm-vaporwaves",
            Name = "SomaFM · Vaporwaves",
            StreamUrl = "https://ice2.somafm.com/vaporwaves-128-mp3",
            Genre = "Retro-Futuristic Starport Lounge"
        },
        new RadioStationItem
        {
            Id = "nightwave-plaza",
            Name = "Nightwave Plaza",
            StreamUrl = "https://radio.plaza.one/mp3",
            Genre = "Late-Night Cockpit Vaporwave & Future Funk"
        }
    };

    private readonly ConfigService _configService;
    private readonly Lock _sync = new();
    private MediaFoundationReader? _reader;
    private WaveOut? _output;
    private CancellationTokenSource? _connectCts;
    private bool _isPlaying;
    private bool _isDucked;
    private string _currentStationId;
    private string _currentStationName = "";
    private string _statusMessage = "Standby";
    private bool _disposed;

    public event Action<RadioStatusSnapshot>? StatusChanged;

    public CockpitRadioService(ConfigService configService)
    {
        _configService = configService;
        _currentStationId = string.IsNullOrWhiteSpace(configService.Current.RadioLastStationId)
            ? BuiltInStations[0].Id
            : configService.Current.RadioLastStationId;
    }

    public IReadOnlyList<RadioStationItem> GetAllStations()
    {
        var list = new List<RadioStationItem>(BuiltInStations);
        if (_configService.Current.CustomRadioStations is { Count: > 0 } custom)
        {
            foreach (var st in custom)
            {
                if (!string.IsNullOrWhiteSpace(st.Name) && IsValidStreamUrl(st.StreamUrl))
                    list.Add(st);
            }
        }
        return list;
    }

    public RadioStatusSnapshot GetStatus()
    {
        lock (_sync)
        {
            return new RadioStatusSnapshot(
                _isPlaying,
                _currentStationId,
                _currentStationName,
                Math.Clamp(_configService.Current.RadioVolumePercent, 0, 100),
                _isDucked,
                _statusMessage);
        }
    }

    public static bool IsValidStreamUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public async Task<(bool Success, string Message)> PlayAsync(string? stationId = null)
    {
        if (_disposed)
            return (false, "Radio service disposed.");

        var stations = GetAllStations();
        string targetId = string.IsNullOrWhiteSpace(stationId) ? _currentStationId : stationId.Trim();
        var station = stations.FirstOrDefault(s => string.Equals(s.Id, targetId, StringComparison.OrdinalIgnoreCase))
                      ?? stations[0];

        StopInternal(notify: false);

        CancellationTokenSource cts;
        lock (_sync)
        {
            _connectCts = new CancellationTokenSource();
            cts = _connectCts;
            _currentStationId = station.Id;
            _currentStationName = station.Name;
            _statusMessage = $"Connecting to {station.Name}...";
        }
        _configService.Current.RadioLastStationId = station.Id;
        _configService.Save();
        NotifyStatus();

        try
        {
            var (reader, output) = await Task.Run(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                var mf = new MediaFoundationReader(station.StreamUrl);
                var wo = new WaveOut();
                wo.Init(mf);
                return (mf, wo);
            }, cts.Token);

            lock (_sync)
            {
                if (_disposed || cts.IsCancellationRequested)
                {
                    output.Dispose();
                    reader.Dispose();
                    return (false, "Cancelled.");
                }
                _reader = reader;
                _output = output;
                _isPlaying = true;
                _statusMessage = string.IsNullOrWhiteSpace(station.Genre)
                    ? $"♪ {station.Name}"
                    : $"♪ {station.Name} — {station.Genre}";
                ApplyEffectiveVolumeLocked();
                _output.Play();
            }

            NotifyStatus();
            return (true, $"Playing {station.Name}");
        }
        catch (OperationCanceledException)
        {
            return (false, "Cancelled.");
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("CockpitRadioService", $"Stream error ({station.Name}): {ex.Message}");
            lock (_sync)
            {
                // Only update shared state when this attempt is still the active one (a newer Play may have superseded it).
                if (ReferenceEquals(_connectCts, cts))
                {
                    _isPlaying = false;
                    _statusMessage = $"Stream unavailable: {station.Name}";
                }
            }
            NotifyStatus();
            return (false, $"Could not connect to {station.Name}: {ex.Message}");
        }
    }

    public void Stop() => StopInternal(notify: true);

    public void SetVolumePercent(int percent)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        _configService.Current.RadioVolumePercent = clamped;
        lock (_sync)
            ApplyEffectiveVolumeLocked();
        NotifyStatus();
    }

    /// <summary>
    /// Temporarily attenuates radio volume to 25% while COVAS or alert speech is active.
    /// </summary>
    public void SetDucked(bool ducked)
    {
        if (!_configService.Current.RadioAutoDuckEnabled)
            ducked = false;

        bool changed;
        lock (_sync)
        {
            changed = _isDucked != ducked;
            _isDucked = ducked;
            if (changed)
                ApplyEffectiveVolumeLocked();
        }
        if (changed)
            NotifyStatus();
    }

    internal static float ComputeEffectiveGain(int volumePercent, bool isDucked)
    {
        float baseGain = Math.Clamp(volumePercent, 0, 100) / 100f;
        return isDucked ? baseGain * 0.25f : baseGain;
    }

    private void ApplyEffectiveVolumeLocked()
    {
        if (_output is null)
            return;
        try
        {
            _output.Volume = ComputeEffectiveGain(_configService.Current.RadioVolumePercent, _isDucked);
        }
        catch
        {
            // Ignore transient audio device errors
        }
    }

    private void StopInternal(bool notify)
    {
        lock (_sync)
        {
            try { _connectCts?.Cancel(); } catch { }
            _connectCts?.Dispose();
            _connectCts = null;

            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            _output = null;

            try { _reader?.Dispose(); } catch { }
            _reader = null;

            _isPlaying = false;
            _statusMessage = "Standby";
        }
        if (notify)
            NotifyStatus();
    }

    private void NotifyStatus() => StatusChanged?.Invoke(GetStatus());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopInternal(notify: false);
    }
}
