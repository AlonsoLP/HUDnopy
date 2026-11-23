// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/Integrations/DiscordIntegrationService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Zero-dependency Discord Rich Presence (Named Pipe IPC) and
//              configurable Discord Webhook milestone/event notifications.
// -----------------------------------------------------------------------------

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HUDnopy.Models;

namespace HUDnopy.Services.Integrations;

/// <summary>
/// Manages local Discord Rich Presence via Named Pipe IPC and async Discord Webhook embeds.
/// </summary>
public sealed class DiscordIntegrationService : IDisposable
{
    // Standard Discord Rich Presence client ID for HUDnopy (user's local Discord client)
    private const string DefaultDiscordAppId = "1217492502131998750";
    private static readonly TimeSpan PresenceUpdateInterval = TimeSpan.FromSeconds(15);

    private readonly ConfigService _configService;
    private readonly EliteFileWatcherService _fileWatcher;
    private readonly GameEventHub _gameEvents;
    private readonly EliteBgsWatchlistService _bgsWatchlist;
    private readonly HttpClient _httpClient;
    private readonly PeriodicTimer _presenceTimer;
    private readonly CancellationTokenSource _presenceLoopCts = new();
    private readonly SemaphoreSlim _ipcLock = new(1, 1);

    private NamedPipeClientStream? _ipcPipe;
    private bool _ipcHandshaked;
    private string _lastPresenceFingerprint = "";
    private readonly long _sessionStartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private bool _disposed;

    public string LastStatus { get; private set; } = "";
    public bool IsIpcConnected => _ipcHandshaked && _ipcPipe is { IsConnected: true };
    public event Action? StatusChanged;

    public DiscordIntegrationService(
        ConfigService configService,
        EliteFileWatcherService fileWatcher,
        GameEventHub gameEvents,
        EliteBgsWatchlistService bgsWatchlist)
    {
        _configService = configService;
        _fileWatcher = fileWatcher;
        _gameEvents = gameEvents;
        _bgsWatchlist = bgsWatchlist;

        _httpClient = HttpClientHeaderDefaults.CreateHttpClient(TimeSpan.FromSeconds(12));

        _gameEvents.JournalEventReceived += OnJournalEventReceived;
        _bgsWatchlist.ConflictDetected += OnBgsConflictDetected;

        _presenceTimer = new PeriodicTimer(PresenceUpdateInterval);
        _ = RunPresenceLoopAsync();
    }

    private async Task RunPresenceLoopAsync()
    {
        try
        {
            await Task.Delay(2000, _presenceLoopCts.Token);
            if (!_disposed && _configService.Current.EnableDiscord)
                await RefreshPresenceAsync(_presenceLoopCts.Token);

            while (await _presenceTimer.WaitForNextTickAsync(_presenceLoopCts.Token))
                await RefreshPresenceAsync(_presenceLoopCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown
        }
    }

    private void OnJournalEventReceived(GameJournalEvent gameEvent)
    {
        if (gameEvent.IsHistorical)
            return;

        var cfg = _configService.Current;
        if (!cfg.EnableDiscord || string.IsNullOrWhiteSpace(cfg.DiscordWebhookUrl))
            return;

        var state = _fileWatcher.State;
        if (cfg.DiscordWebhookOnElw && gameEvent.EventType == "Scan")
        {
            string planetClass = gameEvent.Data.TryGetProperty("PlanetClass", out var pc) && pc.ValueKind == JsonValueKind.String
                ? pc.GetString() ?? ""
                : "";
            if (planetClass.Equals("Earthlike body", StringComparison.OrdinalIgnoreCase))
            {
                string bodyName = gameEvent.Data.TryGetProperty("BodyName", out var bn) && bn.ValueKind == JsonValueKind.String
                    ? bn.GetString() ?? state.CurrentSystem
                    : state.CurrentSystem;
                _ = SendWebhookAsync(
                    "🌍 Earth-Like World Discovered!",
                    $"**{state.CommanderName}** scanned an Earth-Like World: **{bodyName}** in `{state.CurrentSystem}`.",
                    0x2ECC71);
            }
        }
        else if (cfg.DiscordWebhookOnElw && gameEvent.EventType == "ScanOrganic")
        {
            string scanType = JsonElementExtensions.GetStr(gameEvent.Data, "ScanType") ?? "";
            if (scanType.Equals("Analyse", StringComparison.OrdinalIgnoreCase))
            {
                string species = JsonElementExtensions.GetStr(gameEvent.Data, "Species_Localised")
                    ?? JsonElementExtensions.GetStr(gameEvent.Data, "Species") ?? "Organic";
                string body = !string.IsNullOrWhiteSpace(state.BodyName) ? state.BodyName : state.CurrentSystem;
                _ = SendWebhookAsync(
                    "🧬 Exobiology Species Analysed (3/3)",
                    $"**{state.CommanderName}** completed genetic sampling of **{species}** on **{body}** (`{state.CurrentSystem}`).",
                    0x1ABC9C);
            }
        }
        else if (cfg.DiscordWebhookOnCarrierJump && gameEvent.EventType is "CarrierJumpRequest" or "CarrierJump" or "CarrierJumpCancelled")
        {
            string carrierName = state.Carrier is { } fc && !string.IsNullOrWhiteSpace(fc.Name)
                ? $"{fc.Name} [{fc.Callsign}]"
                : "Fleet Carrier";

            if (gameEvent.EventType == "CarrierJumpCancelled")
            {
                _ = SendWebhookAsync(
                    "🚫 Fleet Carrier Jump Cancelled",
                    $"**{carrierName}** jump cancelled in `{state.CurrentSystem}` (CMDR {state.CommanderName}).",
                    0x95A5A6);
                return;
            }

            string dest = JsonElementExtensions.GetStr(gameEvent.Data, "SystemName")
                ?? JsonElementExtensions.GetStr(gameEvent.Data, "StarSystem") ?? "";
            if (!string.IsNullOrWhiteSpace(dest))
            {
                string countdown = "";
                if (gameEvent.EventType == "CarrierJumpRequest")
                {
                    string depRaw = JsonElementExtensions.GetStr(gameEvent.Data, "DepartureTime") ?? "";
                    if (DateTimeOffset.TryParse(depRaw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var depUtc))
                    {
                        long unix = depUtc.ToUnixTimeSeconds();
                        countdown = $"\n⏱ Departure: <t:{unix}:R> (<t:{unix}:t>)";
                    }
                }
                string title = gameEvent.EventType == "CarrierJumpRequest"
                    ? "🚢 Fleet Carrier Jump Scheduled"
                    : "🚢 Fleet Carrier Arrived";
                string origin = !string.IsNullOrWhiteSpace(state.Carrier?.System) ? state.Carrier.System : state.CurrentSystem;
                string inaraUrl = CommunitySyncService.BuildInaraDeepLink("system", dest);
                _ = SendWebhookAsync(
                    title,
                    $"**{carrierName}**\n`{origin}` → **[{dest}]({inaraUrl})**{countdown}\nCMDR {state.CommanderName}",
                    0xFF7100);
            }
        }
    }

    private void OnBgsConflictDetected(EliteBgsFactionWatchItem item)
    {
        var cfg = _configService.Current;
        if (!cfg.EnableDiscord || !cfg.DiscordWebhookOnBgsAlert || string.IsNullOrWhiteSpace(cfg.DiscordWebhookUrl))
            return;

        string states = string.Join(" / ", new[] { item.State, item.ActiveStates, item.PendingStates }
            .Where(static s => !string.IsNullOrWhiteSpace(s) && !s.Equals("None", StringComparison.OrdinalIgnoreCase)));
        _ = SendWebhookAsync(
            "⚔️ EliteBGS Conflict Alert",
            $"**{item.FactionName}** in `{item.SystemName}` ({item.InfluenceDisplay}) — **{states}**",
            0xE74C3C);
    }

    /// <summary>
    /// Sends a formatted Discord embed to the configured webhook URL.
    /// </summary>
    public async Task<(bool Success, string Message)> SendWebhookAsync(
        string title,
        string description,
        int colorHex = 0xFF7100,
        CancellationToken ct = default)
    {
        var cfg = _configService.Current;
        if (!cfg.EnableDiscord)
            return (false, "Discord integration is disabled.");

        string url = (cfg.DiscordWebhookUrl ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            (!uri.Host.Contains("discord.com", StringComparison.OrdinalIgnoreCase) &&
             !uri.Host.Contains("discordapp.com", StringComparison.OrdinalIgnoreCase)))
        {
            return (false, "Invalid Discord Webhook URL.");
        }

        try
        {
            var payload = new
            {
                username = "HUDnopy Telemetry",
                embeds = new[]
                {
                    new
                    {
                        title,
                        description,
                        color = colorHex,
                        timestamp = DateTimeOffset.UtcNow.ToString("O"),
                        footer = new { text = $"{HttpClientHeaderDefaults.ProductName} v{HttpClientHeaderDefaults.ProductVersion} · Elite Dangerous" }
                    }
                }
            };

            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _httpClient.PostAsync(uri, content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                LastStatus = $"Discord Webhook HTTP {(int)resp.StatusCode}";
                StatusChanged?.Invoke();
                return (false, LastStatus);
            }

            LastStatus = $"Discord Webhook sent ({DateTime.Now:HH:mm:ss})";
            StatusChanged?.Invoke();
            return (true, LastStatus);
        }
        catch (Exception ex)
        {
            LastStatus = $"Discord Webhook error: {ex.Message}";
            StatusChanged?.Invoke();
            return (false, LastStatus);
        }
    }

    /// <summary>
    /// Sends a formatted Discord embed with an attached JPEG screenshot via multipart/form-data.
    /// </summary>
    public async Task<(bool Success, string Message)> SendWebhookWithImageAsync(
        string title,
        string description,
        byte[] jpegBytes,
        string fileName = "hudnopy_cockpit.jpg",
        int colorHex = 0xFF7100,
        CancellationToken ct = default)
    {
        var cfg = _configService.Current;
        if (!cfg.EnableDiscord)
            return (false, "Discord integration is disabled.");
        if (jpegBytes is null || jpegBytes.Length == 0)
            return (false, "Empty screenshot buffer.");

        string url = (cfg.DiscordWebhookUrl ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            (!uri.Host.Contains("discord.com", StringComparison.OrdinalIgnoreCase) &&
             !uri.Host.Contains("discordapp.com", StringComparison.OrdinalIgnoreCase)))
        {
            return (false, "Invalid Discord Webhook URL.");
        }

        try
        {
            var payload = new
            {
                username = "HUDnopy Telemetry",
                embeds = new[]
                {
                    new
                    {
                        title,
                        description,
                        color = colorHex,
                        image = new { url = $"attachment://{fileName}" },
                        timestamp = DateTimeOffset.UtcNow.ToString("O"),
                        footer = new { text = $"{HttpClientHeaderDefaults.ProductName} v{HttpClientHeaderDefaults.ProductVersion} · Elite Dangerous" }
                    }
                }
            };

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), "payload_json");
            var imgContent = new ByteArrayContent(jpegBytes);
            imgContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            form.Add(imgContent, "files[0]", fileName);

            using var resp = await _httpClient.PostAsync(uri, form, ct);
            if (!resp.IsSuccessStatusCode)
            {
                LastStatus = $"Discord Screenshot HTTP {(int)resp.StatusCode}";
                StatusChanged?.Invoke();
                return (false, LastStatus);
            }

            LastStatus = $"Discord screenshot sent ({DateTime.Now:HH:mm:ss})";
            StatusChanged?.Invoke();
            return (true, LastStatus);
        }
        catch (Exception ex)
        {
            LastStatus = $"Discord screenshot error: {ex.Message}";
            StatusChanged?.Invoke();
            return (false, LastStatus);
        }
    }

    /// <summary>
    /// Updates Discord Rich Presence over the local Discord IPC Named Pipe when enabled.
    /// </summary>
    public async Task RefreshPresenceAsync(CancellationToken ct = default)
    {
        if (_disposed)
            return;

        if (!_configService.Current.EnableDiscord)
        {
            DisconnectIpc();
            return;
        }

        var state = _fileWatcher.State;
        var (details, statusLine) = BuildPresenceText(state);
        string fingerprint = $"{details}|{statusLine}";
        if (fingerprint == _lastPresenceFingerprint && _ipcPipe is { IsConnected: true })
            return;

        if (!await _ipcLock.WaitAsync(0, ct))
            return;

        try
        {
            if (!await EnsureIpcConnectedAsync(ct))
                return;

            var cmd = new
            {
                cmd = "SET_ACTIVITY",
                args = new
                {
                    pid = Environment.ProcessId,
                    activity = new
                    {
                        details,
                        state = statusLine,
                        timestamps = new { start = _sessionStartUnix }
                    }
                },
                nonce = Guid.NewGuid().ToString("N")
            };

            byte[] frame = BuildIpcFrame(1, JsonSerializer.Serialize(cmd));
            await _ipcPipe!.WriteAsync(frame, ct);
            await _ipcPipe.FlushAsync(ct);
            await DrainIpcResponseAsync(_ipcPipe, ct);

            _lastPresenceFingerprint = fingerprint;
            LastStatus = $"Discord RPC: {details}";
            StatusChanged?.Invoke();
        }
        catch
        {
            DisconnectIpc();
        }
        finally
        {
            _ipcLock.Release();
        }
    }

    internal static (string Details, string State) BuildPresenceText(ShipState state)
    {
        string sys = string.IsNullOrWhiteSpace(state.CurrentSystem) || state.CurrentSystem == "---"
            ? "Deep Space"
            : state.CurrentSystem;
        string ship = string.IsNullOrWhiteSpace(state.ShipType) || state.ShipType == "---"
            ? "HUDnopy"
            : state.ShipType;
        string shipLine = !string.IsNullOrWhiteSpace(state.ShipName) && state.ShipName != "---"
            ? $"Ship: {ship} · {state.ShipName}"
            : $"Ship: {ship}";

        string activity;
        if (state.Docked && !string.IsNullOrWhiteSpace(state.StationName))
        {
            activity = $"Docked at {state.StationName}";
        }
        else if (state.ExobiologyTracker is { SamplesCollected: > 0 } exo)
        {
            activity = $"Exobiology: {exo.Species} ({exo.SamplesCollected}/3)";
        }
        else if (state.OnFoot)
        {
            activity = $"On Foot in {sys}";
        }
        else if (state.InSRV)
        {
            activity = $"SRV on {(!string.IsNullOrWhiteSpace(state.BodyName) ? state.BodyName : sys)}";
        }
        else if (state.CombatSession.TotalKills > 0 && state.HardpointsDeployed)
        {
            activity = $"Combat in {sys} ({state.CombatSession.TotalKills} kills)";
        }
        else if (state.MiningSession.TotalRefinedTons > 0 && state.CargoScoopDeployed)
        {
            activity = $"Mining in {sys} ({state.MiningSession.TotalRefinedTons}t)";
        }
        else if (state.NavRoute.Count > 1)
        {
            string dest = state.NavRoute[^1].StarSystem;
            activity = $"En route to {dest} ({state.NavRoute.Count - 1} jumps)";
        }
        else if (state.Supercruise)
        {
            activity = $"Supercruise in {sys}";
        }
        else
        {
            activity = $"Flying in {sys}";
        }

        return (activity, shipLine);
    }

    /// <summary>
    /// Encodes a Discord IPC binary frame: [4-byte little-endian opcode][4-byte little-endian length][UTF-8 JSON payload].
    /// </summary>
    internal static byte[] BuildIpcFrame(int opCode, string jsonPayload)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(jsonPayload);
        byte[] frame = new byte[8 + payloadBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), opCode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payloadBytes.Length);
        Buffer.BlockCopy(payloadBytes, 0, frame, 8, payloadBytes.Length);
        return frame;
    }

    private async Task<bool> EnsureIpcConnectedAsync(CancellationToken ct)
    {
        if (_ipcPipe is { IsConnected: true } && _ipcHandshaked)
            return true;

        DisconnectIpc();

        for (int i = 0; i < 10; i++)
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(150, ct);

                string handshakeJson = JsonSerializer.Serialize(new { v = 1, client_id = DefaultDiscordAppId });
                byte[] frame = BuildIpcFrame(0, handshakeJson);
                await pipe.WriteAsync(frame, ct);
                await pipe.FlushAsync(ct);
                await DrainIpcResponseAsync(pipe, ct);

                _ipcPipe = pipe;
                _ipcHandshaked = true;
                return true;
            }
            catch
            {
                try { pipe?.Dispose(); } catch { }
                // Try next discord-ipc slot.
            }
        }

        return false;
    }

    private static async Task DrainIpcResponseAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        try
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(250);
            byte[] header = new byte[8];
            int read = await pipe.ReadAsync(header.AsMemory(0, 8), readCts.Token);
            if (read == 8)
            {
                int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
                if (len > 0 && len <= 65536)
                {
                    byte[] payload = new byte[len];
                    int offset = 0;
                    while (offset < len)
                    {
                        int n = await pipe.ReadAsync(payload.AsMemory(offset, len - offset), readCts.Token);
                        if (n <= 0) break;
                        offset += n;
                    }
                }
            }
        }
        catch
        {
            // Non-fatal: timeout or closed pipe while draining ACK frame.
        }
    }

    public void DisconnectIpc()
    {
        _ipcHandshaked = false;
        _lastPresenceFingerprint = "";
        try { _ipcPipe?.Dispose(); } catch { }
        _ipcPipe = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gameEvents.JournalEventReceived -= OnJournalEventReceived;
        _bgsWatchlist.ConflictDetected -= OnBgsConflictDetected;
        try { _presenceLoopCts.Cancel(); } catch (ObjectDisposedException) { }
        _presenceTimer.Dispose();
        _presenceLoopCts.Dispose();
        DisconnectIpc();
        _httpClient.Dispose();
        _ipcLock.Dispose();
    }
}
