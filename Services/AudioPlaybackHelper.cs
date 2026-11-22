// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/AudioPlaybackHelper.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: NAudio 3.3 helpers providing low-latency WasapiPlayer output with
//              automatic stream routing (fallback to WaveOut) and EffectChain
//              voice conditioning (noise gate + compressor) for COVAS speech input.
// -----------------------------------------------------------------------------

using System;
using System.IO;
using NAudio.Effects;
using NAudio.Wave;

namespace HUDnopy.Services;

/// <summary>
/// Shared NAudio 3.3 audio helpers for low-latency WASAPI playback and COVAS voice conditioning.
/// </summary>
internal static class AudioPlaybackHelper
{
    /// <summary>
    /// Creates a shared-mode <see cref="WasapiPlayer"/> with automatic Windows default-device
    /// routing, IAudioClient3 low latency, and MMCSS priority, falling back to <see cref="WaveOut"/>
    /// if WASAPI initialization is unavailable on the host system.
    /// </summary>
    public static IWavePlayer CreateOutputPlayer(int latencyMs = 80)
    {
        try
        {
            return new WasapiPlayerBuilder()
                .WithLatency(latencyMs)
                .WithLowLatency()
                .WithMmcssThreadPriority("Audio")
                .Build();
        }
        catch
        {
            return new WaveOut
            {
                BufferMilliseconds = Math.Max(20, latencyMs / 2),
                NumberOfBuffers = 2
            };
        }
    }

    /// <summary>
    /// Conditions 16-bit PCM voice audio using NAudio 3's <see cref="EffectChain"/>
    /// (<see cref="GateEffect"/> + <see cref="CompressorEffect"/>) to suppress background
    /// fan/HOTAS noise and normalize commander speech before Whisper/Gemini transcription.
    /// </summary>
    public static byte[] ProcessVoicePcmForRecognition(byte[] rawPcm16, int sampleRate = 16000, int channels = 1)
    {
        if (rawPcm16.Length < 64)
            return rawPcm16;

        try
        {
            var waveFormat = new WaveFormat(sampleRate, 16, channels);
            using var rawStream = new RawSourceWaveStream(new MemoryStream(rawPcm16, writable: false), waveFormat);

            var chain = new EffectChain(rawStream.ToSampleProvider())
                .Add(new GateEffect { ThresholdDb = -45f })
                .Add(new CompressorEffect { ThresholdDb = -18f, Ratio = 3.5f });

            var pcm16Out = chain.ToWaveProvider16();
            using var output = new MemoryStream(rawPcm16.Length);
            Span<byte> chunk = stackalloc byte[4096];
            int read;
            while ((read = pcm16Out.Read(chunk)) > 0)
                output.Write(chunk[..read]);

            return output.Length > 0 ? output.ToArray() : rawPcm16;
        }
        catch
        {
            return rawPcm16;
        }
    }
}
