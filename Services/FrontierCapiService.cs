// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/FrontierCapiService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Frontier Companion API (cAPI) client with OAuth 2.0 + PKCE and Windows DPAPI token storage.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HUDnopy.Models;
using static HUDnopy.Services.JsonElementExtensions;

namespace HUDnopy.Services;

/// <summary>
/// Frontier Companion API (cAPI) client with OAuth 2.0 + PKCE and Windows DPAPI token storage.
/// </summary>
public sealed class FrontierCapiService : IDisposable
{
    public const string DefaultClientId = "77f5fc55-aefa-491d-94c0-b0b4aea27552";
    // Frontier only allows https:// redirect_uri values. This static GitHub Pages relay
    // (docs/callback.html) forwards code + state to local loopback http://127.0.0.1:{port}/callback.
    public static string PublicRedirectUri => HttpClientHeaderDefaults.FrontierCallbackUrl;
    private const string AuthBaseUrl = "https://auth.frontierstore.net";
    private const string CapiBaseUrl = "https://companion.orerve.net";
    private static readonly TimeSpan AutoSyncCooldown = TimeSpan.FromMinutes(5);

    private readonly ConfigService _configService;
    private readonly EliteFileWatcherService _fileWatcher;
    private readonly GameEventHub _gameEvents;
    private readonly HttpClient _httpClient;
    private readonly string _tokenFilePath;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly SemaphoreSlim _tokenRefreshLock = new(1, 1);

    private CapiTokenStore? _tokens;
    private DateTimeOffset _lastAutoSyncUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _maintenanceUntilUtc = DateTimeOffset.MinValue;

    public bool IsConnected => _tokens is not null && !string.IsNullOrWhiteSpace(_tokens.RefreshToken);
    public string LastStatus { get; private set; } = "";
    public event Action? StatusChanged;

    private sealed class CapiTokenStore
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }

    public FrontierCapiService(
        ConfigService configService,
        EliteFileWatcherService fileWatcher,
        GameEventHub gameEvents)
    {
        _configService = configService;
        _fileWatcher = fileWatcher;
        _gameEvents = gameEvents;
        _httpClient = HttpClientHeaderDefaults.CreateHttpClient(TimeSpan.FromSeconds(20));
        _tokenFilePath = HttpClientHeaderDefaults.GetLocalDataPath("frontier_capi.dat");

        LoadTokensFromDisk();
        _gameEvents.JournalEventReceived += OnJournalEventReceived;

        if (_configService.Current.EnableFrontierCapi && IsConnected && HttpClientHeaderDefaults.TestHttpHandlerFactory is null)
        {
            _lastAutoSyncUtc = DateTimeOffset.UtcNow;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1200).ConfigureAwait(false);
                    await SyncAllAsync(includeDockedEndpoints: _fileWatcher.State.Docked).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppDiagnosticsLogger.Warn("FrontierCapi", $"Startup auto-sync failed: {ex.Message}");
                }
            });
        }
    }

    private void OnJournalEventReceived(GameJournalEvent gameEvent)
    {
        var cfg = _configService.Current;
        if (!cfg.EnableFrontierCapi || !IsConnected)
            return;

        // Allow the very first LoadGame event after startup (even from initial journal read) to trigger auto-sync
        if (gameEvent.IsHistorical && (_lastAutoSyncUtc != DateTimeOffset.MinValue || gameEvent.EventType != "LoadGame" || HttpClientHeaderDefaults.TestHttpHandlerFactory is not null))
            return;

        if (gameEvent.EventType is "LoadGame" or "Docked" &&
            DateTimeOffset.UtcNow - _lastAutoSyncUtc >= AutoSyncCooldown)
        {
            _lastAutoSyncUtc = DateTimeOffset.UtcNow;
            _ = SyncAllAsync(includeDockedEndpoints: gameEvent.EventType == "Docked");
        }
    }

    // =========================================================================
    // OAuth 2.0 + PKCE (S256) + Loopback RFC 8252
    // =========================================================================

    public string GetEffectiveClientId()
    {
        string custom = (_configService.Current.FrontierCapiClientId ?? "").Trim();
        return Guid.TryParse(custom, out _) ? custom : DefaultClientId;
    }

    public async Task<(bool Success, string Message)> AuthorizeAsync(CancellationToken ct = default)
    {
        var cfg = _configService.Current;
        string clientId = GetEffectiveClientId();

        int port = Math.Clamp(cfg.FrontierCapiCallbackPort, 1024, 65535);
        string redirectUri = PublicRedirectUri;
        string listenerPrefix = $"http://127.0.0.1:{port}/callback/";

        string codeVerifier = GenerateBase64UrlRandom(64);
        string codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        // The port is prefixed to state so callback.html knows which loopback port to forward to.
        string stateToken = $"{port}.{GenerateBase64UrlRandom(24)}";

        string authUrl =
            $"{AuthBaseUrl}/auth?response_type=code" +
            $"&audience=frontier" +
            $"&scope=auth%20capi" +
            $"&client_id={Uri.EscapeDataString(clientId)}" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            $"&code_challenge_method=S256" +
            $"&state={Uri.EscapeDataString(stateToken)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";

        using var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add(listenerPrefix);
            listener.Start();
        }
        catch (Exception ex)
        {
            return (false, $"No se pudo abrir el puerto loopback {port}: {ex.Message}");
        }

        UpdateStatus("Esperando inicio de sesión en el navegador (Frontier)...");
        try
        {
            Process.Start(new ProcessStartInfo { FileName = authUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            return (false, $"No se pudo abrir el navegador: {ex.Message}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        string? receivedCode = null;
        string? receivedState = null;
        string? receivedError = null;

        try
        {
            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));
            if (completed != contextTask)
                return (false, "Tiempo de espera agotado al autenticar con Frontier.");

            var context = await contextTask;
            receivedCode = context.Request.QueryString["code"];
            receivedState = context.Request.QueryString["state"];
            receivedError = context.Request.QueryString["error"];

            string html = string.IsNullOrWhiteSpace(receivedCode)
                ? "<html><body style='background:#080a0e;color:#ff5555;font-family:sans-serif;padding:32px'><h2>HUDnopy — Autenticación cancelada</h2><p>Ya puedes cerrar esta pestaña.</p></body></html>"
                : "<html><body style='background:#080a0e;color:#ff7100;font-family:sans-serif;padding:32px'><h2>HUDnopy — Conectado con Frontier</h2><p>Autenticación completada correctamente. Ya puedes cerrar esta pestaña y volver a HUDnopy.</p></body></html>";
            byte[] buf = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buf.Length;
            await context.Response.OutputStream.WriteAsync(buf, ct);
            context.Response.Close();
        }
        catch (Exception ex)
        {
            return (false, $"Error recibiendo respuesta de Frontier: {ex.Message}");
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }

        if (!string.IsNullOrWhiteSpace(receivedError))
            return (false, $"Frontier devolvió error: {receivedError}");
        if (string.IsNullOrWhiteSpace(receivedCode) || !string.Equals(receivedState, stateToken, StringComparison.Ordinal))
            return (false, "Código o estado OAuth inválido.");

        // Intercambiar code por access_token + refresh_token
        try
        {
            using var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("code", receivedCode),
                new KeyValuePair<string, string>("code_verifier", codeVerifier),
                new KeyValuePair<string, string>("redirect_uri", redirectUri)
            });

            using var resp = await _httpClient.PostAsync($"{AuthBaseUrl}/token", form, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return (false, $"Token Frontier HTTP {(int)resp.StatusCode}");

            if (!ParseAndSaveTokens(body))
                return (false, "Respuesta de token inválida de Frontier.");

            UpdateStatus("Conectado con Frontier cAPI (tokens cifrados con DPAPI).");
            var (_, syncMsg) = await SyncAllAsync(includeDockedEndpoints: _fileWatcher.State.Docked, ct);
            return (true, syncMsg);
        }
        catch (Exception ex)
        {
            return (false, $"Error obteniendo token de Frontier: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        _tokens = null;
        try { if (File.Exists(_tokenFilePath)) File.Delete(_tokenFilePath); } catch { }
        UpdateStatus("Desconectado de Frontier cAPI.");
    }

    private async Task<string?> EnsureValidAccessTokenAsync(CancellationToken ct)
    {
        if (_tokens is null)
            return null;

        if (!string.IsNullOrWhiteSpace(_tokens.AccessToken) &&
            DateTimeOffset.UtcNow < _tokens.ExpiresAtUtc.AddMinutes(-2))
        {
            return _tokens.AccessToken;
        }

        await _tokenRefreshLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring lock in case another caller just refreshed the rotating token
            if (_tokens is null)
                return null;
            if (!string.IsNullOrWhiteSpace(_tokens.AccessToken) &&
                DateTimeOffset.UtcNow < _tokens.ExpiresAtUtc.AddMinutes(-2))
            {
                return _tokens.AccessToken;
            }

            string clientId = GetEffectiveClientId();
            if (string.IsNullOrWhiteSpace(_tokens.RefreshToken))
                return null;

            using var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("refresh_token", _tokens.RefreshToken)
            });
            using var resp = await _httpClient.PostAsync($"{AuthBaseUrl}/token", form, ct);
            if (!resp.IsSuccessStatusCode)
                return null;
            string body = await resp.Content.ReadAsStringAsync(ct);
            return ParseAndSaveTokens(body) ? _tokens?.AccessToken : null;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("FrontierCapi", $"OAuth2 token refresh failed: {ex.Message}");
            return null;
        }
        finally
        {
            _tokenRefreshLock.Release();
        }
    }

    private bool ParseAndSaveTokens(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string access = root.TryGetProperty("access_token", out var ap) ? (ap.GetString() ?? "") : "";
            string refresh = root.TryGetProperty("refresh_token", out var rp) ? (rp.GetString() ?? "") : (_tokens?.RefreshToken ?? "");
            int expiresIn = root.TryGetProperty("expires_in", out var ep) && ep.ValueKind == JsonValueKind.Number ? ep.GetInt32() : 3600;
            if (string.IsNullOrWhiteSpace(access))
                return false;

            _tokens = new CapiTokenStore
            {
                AccessToken = access,
                RefreshToken = refresh,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn))
            };
            SaveTokensToDisk();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // =========================================================================
    // Synchronize all 5 cAPI endpoints (/profile, /fleetcarrier, /cg, /market, /shipyard)
    // =========================================================================

    public async Task<(bool Success, string Summary)> SyncAllAsync(bool includeDockedEndpoints = false, CancellationToken ct = default)
    {
        if (!_configService.Current.EnableFrontierCapi || !IsConnected)
            return (false, "Frontier cAPI no conectado.");

        if (DateTimeOffset.UtcNow < _maintenanceUntilUtc)
            return (false, "Servidores Frontier cAPI en mantenimiento (pausa automática).");

        if (!await _syncLock.WaitAsync(0, ct))
            return (true, LastStatus);

        try
        {
            string? token = await EnsureValidAccessTokenAsync(ct);
            if (string.IsNullOrWhiteSpace(token))
            {
                UpdateStatus("Token de Frontier caducado. Vuelve a conectar.");
                return (false, LastStatus);
            }

            var syncedParts = new List<string>();

            // 1. /profile (comandante + flota almacenada)
            var (pOk, pDoc, pErr) = await GetCapiJsonAsync("/profile", token, ct);
            if (pOk && pDoc is not null)
            {
                using (pDoc) ApplyProfileJson(pDoc.RootElement);
                syncedParts.Add("Perfil/Flota");
            }
            else if (pErr == "MAINTENANCE")
            {
                return (false, LastStatus);
            }

            // 2. /fleetcarrier (portanaves completo + bodega en remoto)
            var (fcOk, fcDoc, fcErr) = await GetCapiJsonAsync("/fleetcarrier", token, ct);
            if (fcOk && fcDoc is not null)
            {
                using (fcDoc) ApplyFleetCarrierJson(fcDoc.RootElement);
                syncedParts.Add("Portanaves");
            }
            else if (fcErr == "MAINTENANCE")
            {
                return (false, LastStatus);
            }

            // 3. /communitygoals
            var (cgOk, cgDoc, _) = await GetCapiJsonAsync("/communitygoals", token, ct);
            if (cgOk && cgDoc is not null)
            {
                using (cgDoc) ApplyCommunityGoalsJson(cgDoc.RootElement);
                syncedParts.Add("CGs");
            }

            // 4 & 5. /market and /shipyard when the commander is docked
            if (includeDockedEndpoints || _fileWatcher.State.Docked)
            {
                var (mOk, mDoc, _) = await GetCapiJsonAsync("/market", token, ct);
                if (mOk && mDoc is not null)
                {
                    using (mDoc) ApplyMarketJson(mDoc.RootElement);
                    syncedParts.Add("Mercado");
                }

                var (sOk, sDoc, _) = await GetCapiJsonAsync("/shipyard", token, ct);
                if (sOk && sDoc is not null)
                {
                    using (sDoc) ApplyShipyardJson(sDoc.RootElement);
                    syncedParts.Add("Astillero");
                }
            }

            string summary = syncedParts.Count > 0
                ? $"Frontier cAPI OK: {string.Join(", ", syncedParts)} ({DateTime.Now:HH:mm:ss})"
                : $"Frontier cAPI sin datos nuevos ({DateTime.Now:HH:mm:ss})";
            UpdateStatus(summary);
            return (syncedParts.Count > 0, summary);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<(bool Ok, JsonDocument? Doc, string Error)> GetCapiJsonAsync(string endpoint, string token, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, CapiBaseUrl + endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _httpClient.SendAsync(req, ct);

            if ((int)resp.StatusCode == 418)
            {
                _maintenanceUntilUtc = DateTimeOffset.UtcNow.AddMinutes(10);
                UpdateStatus("Frontier cAPI en mantenimiento (HTTP 418).");
                return (false, null, "MAINTENANCE");
            }
            if ((int)resp.StatusCode == 429)
            {
                _maintenanceUntilUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                UpdateStatus("Frontier cAPI rate-limit (HTTP 429).");
                return (false, null, "RATELIMIT");
            }
            if (!resp.IsSuccessStatusCode)
                return (false, null, $"HTTP {(int)resp.StatusCode}");

            string body = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
                return (false, null, "EMPTY");

            return (true, JsonDocument.Parse(body), "");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private void ApplyProfileJson(JsonElement root)
    {
        var ships = new List<StoredShipItem>();
        if (root.TryGetProperty("ships", out var shipsProp))
        {
            IEnumerable<JsonElement> entries = shipsProp.ValueKind switch
            {
                JsonValueKind.Array => shipsProp.EnumerateArray(),
                JsonValueKind.Object => shipsProp.EnumerateObject().Select(static p => p.Value),
                _ => Enumerable.Empty<JsonElement>()
            };

            foreach (var sh in entries)
            {
                if (sh.ValueKind != JsonValueKind.Object) continue;
                long id = GetLong(sh, "id") ?? 0;
                string type = GetStr(sh, "name") ?? "";
                string shipName = GetStr(sh, "shipName") ?? "";
                string sys = sh.TryGetProperty("starsystem", out var sy) ? (GetStr(sy, "name") ?? "") : "";
                string sta = sh.TryGetProperty("station", out var st) ? (GetStr(st, "name") ?? "") : "";
                long val = sh.TryGetProperty("value", out var vo) ? (GetLong(vo, "total") ?? 0) : 0;
                if (!string.IsNullOrWhiteSpace(type))
                {
                    ships.Add(new StoredShipItem
                    {
                        Id = id,
                        ShipType = type,
                        ShipName = string.IsNullOrWhiteSpace(shipName) ? "—" : shipName,
                        StarSystem = string.IsNullOrWhiteSpace(sys) ? "—" : sys,
                        StationName = string.IsNullOrWhiteSpace(sta) ? "—" : sta,
                        Value = val
                    });
                }
            }
        }

        var storedModules = new List<StoredModuleItem>();
        if (root.TryGetProperty("modules", out var modsProp))
        {
            IEnumerable<JsonElement> modEntries = modsProp.ValueKind switch
            {
                JsonValueKind.Array => modsProp.EnumerateArray(),
                JsonValueKind.Object => modsProp.EnumerateObject().Select(static p => p.Value),
                _ => Enumerable.Empty<JsonElement>()
            };

            foreach (var m in modEntries)
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                string rawName = GetStr(m, "name") ?? "";
                if (string.IsNullOrWhiteSpace(rawName)) continue;
                string locName = GetStr(m, "locName") ?? ModuleDisplayCatalog.Format(rawName, _configService.Current.InterfaceLanguage);
                string sys = m.TryGetProperty("starsystem", out var sy) ? (GetStr(sy, "name") ?? "") : "";
                string sta = m.TryGetProperty("station", out var st) ? (GetStr(st, "name") ?? "") : "";
                long price = GetLong(m, "value") ?? GetLong(m, "cost") ?? 0;
                string eng = "";
                if (m.TryGetProperty("recipeName", out var rn) && rn.ValueKind == JsonValueKind.String)
                {
                    eng = rn.GetString() ?? "";
                    if (GetLong(m, "recipeLevel") is long lvl && lvl > 0)
                        eng = $"{eng} G{lvl}";
                }

                storedModules.Add(new StoredModuleItem
                {
                    Name = rawName,
                    DisplayName = string.IsNullOrWhiteSpace(locName) ? rawName : locName,
                    StarSystem = string.IsNullOrWhiteSpace(sys) ? "—" : sys,
                    StationName = string.IsNullOrWhiteSpace(sta) ? "—" : sta,
                    BuyPrice = price,
                    Engineering = eng
                });
            }
        }

        string? capiSuit = null;
        string? capiLoadout = null;
        if (root.TryGetProperty("suit", out var suitObj) && suitObj.ValueKind == JsonValueKind.Object)
            capiSuit = GetStr(suitObj, "locName") ?? GetStr(suitObj, "name");
        if (root.TryGetProperty("loadout", out var loadoutObj) && loadoutObj.ValueKind == JsonValueKind.Object)
            capiLoadout = GetStr(loadoutObj, "name");

        _fileWatcher.ApplyCapiEnrichment(state =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool localShipsFresh = state.IsGameDetected &&
                state.StoredShips.Count > 0 &&
                state.LastStoredShipsJournalUtc.HasValue &&
                now - state.LastStoredShipsJournalUtc.Value < TimeSpan.FromMinutes(10);
            if (ships.Count > 0 && !localShipsFresh)
            {
                state.StoredShips = ships
                    .Where(s => !state.ShipId.HasValue || s.Id != state.ShipId.Value)
                    .GroupBy(static s => s.Id > 0 ? $"id:{s.Id}" : $"name:{s.ShipType}:{s.ShipName}:{s.StarSystem}", StringComparer.OrdinalIgnoreCase)
                    .Select(static g => g.First())
                    .OrderBy(static s => s.StarSystem, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(static s => s.ShipType)
                    .ToList();
            }

            bool localModulesFresh = state.IsGameDetected &&
                state.StoredModules.Count > 0 &&
                state.LastStoredModulesJournalUtc.HasValue &&
                now - state.LastStoredModulesJournalUtc.Value < TimeSpan.FromMinutes(10);
            if (storedModules.Count > 0 && !localModulesFresh)
                state.StoredModules = storedModules.OrderBy(static m => m.StarSystem, StringComparer.CurrentCultureIgnoreCase).ThenBy(static m => m.DisplayName).ToList();
            if (!string.IsNullOrWhiteSpace(capiSuit) && string.IsNullOrWhiteSpace(state.ActiveSuit))
                state.ActiveSuit = capiSuit;
            if (!string.IsNullOrWhiteSpace(capiLoadout) && string.IsNullOrWhiteSpace(state.ActiveSuitLoadout))
                state.ActiveSuitLoadout = capiLoadout;

            // If the game is closed, populate basic commander data from /profile
            if (!state.IsGameDetected)
            {
                if (root.TryGetProperty("commander", out var cmdr))
                {
                    string? name = GetStr(cmdr, "name");
                    if (!string.IsNullOrWhiteSpace(name))
                        state.CommanderName = "CMDR " + name;
                    if (GetLong(cmdr, "credits") is long cr)
                        state.Credits = cr;
                }
                if (root.TryGetProperty("lastSystem", out var ls) && GetStr(ls, "name") is string sysName && !string.IsNullOrWhiteSpace(sysName))
                    state.CurrentSystem = sysName;
                if (root.TryGetProperty("lastStarport", out var lsp) && GetStr(lsp, "name") is string staName && !string.IsNullOrWhiteSpace(staName))
                    state.StationName = staName;
                if (root.TryGetProperty("ship", out var curShip) && GetStr(curShip, "name") is string shType && !string.IsNullOrWhiteSpace(shType))
                    state.ShipType = shType;
            }
        });
    }

    private void ApplyFleetCarrierJson(JsonElement root)
    {
        string callsign = GetStr(root, "name", "callsign") ?? GetStr(root, "callsign") ?? "";
        string nameHex = GetStr(root, "name", "vanityName") ?? GetStr(root, "vanityName") ?? "";
        string decodedName = DecodeHexUtf8(nameHex);
        long id = GetLong(root, "marketId")
                  ?? GetLong(root, "carrierId")
                  ?? (root.TryGetProperty("market", out var mktIdObj) && mktIdObj.ValueKind == JsonValueKind.Object ? GetLong(mktIdObj, "id") : null)
                  ?? _fileWatcher.State.Carrier?.Id
                  ?? 0;
        if (id <= 0 && string.IsNullOrWhiteSpace(callsign) && string.IsNullOrWhiteSpace(decodedName))
            return;

        string sys = GetStr(root, "currentStarSystem") ?? GetStr(root, "starsystem") ?? "";
        long? fuel = GetLong(root, "fuel");
        long? balance = GetLong(root, "balance")
                        ?? (root.TryGetProperty("finance", out var finBal) && finBal.ValueKind == JsonValueKind.Object ? GetLong(finBal, "bankBalance") : null);
        long? reserve = root.TryGetProperty("finance", out var fin) && fin.ValueKind == JsonValueKind.Object
            ? (GetLong(fin, "bankReservedBalance") ?? GetLong(fin, "reserveBalance"))
            : null;
        long? available = (balance.HasValue && reserve.HasValue && balance.Value >= reserve.Value)
            ? balance.Value - reserve.Value
            : balance;
        double? reservePct = (balance.HasValue && balance.Value > 0 && reserve.HasValue && reserve.Value >= 0)
            ? Math.Round(100.0 * reserve.Value / balance.Value, 1)
            : null;
        double? taxRate = root.TryGetProperty("finance", out var finTax) && finTax.ValueKind == JsonValueKind.Object
            ? (GetDouble(finTax, "taxRate") ?? GetDouble(finTax, "serviceTax"))
            : null;
        long? weeklyUpkeep = root.TryGetProperty("finance", out var fin3) && fin3.ValueKind == JsonValueKind.Object
            ? (GetLong(fin3, "maintenance") ?? GetLong(fin3, "maintenanceToDate") ?? GetLong(fin3, "coreCost"))
            : null;
        string dockingAccess = GetStr(root, "dockingAccess") ?? "";
        bool? allowNotorious = root.TryGetProperty("notoriousAccess", out var na) && na.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? na.GetBoolean()
            : (na.ValueKind == JsonValueKind.Number && na.TryGetInt32(out int naInt) ? naInt != 0 : null);

        long? freeSpace = null, cargoUsed = null, totalCap = null;
        if (root.TryGetProperty("capacity", out var cap) && cap.ValueKind == JsonValueKind.Object)
        {
            long shipPacks = GetLong(cap, "shipPacks") ?? 0;
            long modPacks = GetLong(cap, "modulePacks") ?? 0;
            long cargoForSale = GetLong(cap, "cargoForSale") ?? 0;
            long cargoNotForSale = GetLong(cap, "cargoNotForSale") ?? 0;
            long cargoReserved = GetLong(cap, "cargoSpaceReserved") ?? 0;
            long crew = GetLong(cap, "crew") ?? 0;
            freeSpace = GetLong(cap, "freeSpace");
            cargoUsed = cargoForSale + cargoNotForSale + cargoReserved;
            if (freeSpace.HasValue)
                totalCap = freeSpace.Value + shipPacks + modPacks + cargoUsed.Value + crew;
        }

        var cargoItems = new List<CarrierCargoItem>();
        if (root.TryGetProperty("cargo", out var cargoArr) && cargoArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cargoArr.EnumerateArray())
            {
                string comm = GetStr(c, "locName") ?? GetStr(c, "commodity") ?? "";
                int qty = (int)(GetLong(c, "qty") ?? 0);
                if (string.IsNullOrWhiteSpace(comm) || qty <= 0) continue;
                cargoItems.Add(new CarrierCargoItem
                {
                    Commodity = comm,
                    Quantity = qty,
                    Stolen = c.TryGetProperty("stolen", out var st) && st.ValueKind == JsonValueKind.True,
                    OriginSystem = GetStr(c, "originSystem") ?? ""
                });
            }
        }

        var marketOrders = new List<CarrierMarketOrderItem>();
        if (root.TryGetProperty("market", out var mktObj) && mktObj.ValueKind == JsonValueKind.Object &&
            mktObj.TryGetProperty("commodities", out var mktArr) && mktArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in mktArr.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object) continue;
                string comm = GetStr(c, "locName") ?? GetStr(c, "name") ?? "";
                if (string.IsNullOrWhiteSpace(comm)) continue;
                int stock = (int)(GetLong(c, "stock") ?? 0);
                int demand = (int)(GetLong(c, "demand") ?? 0);
                long buyPrice = GetLong(c, "buyPrice") ?? 0;
                long sellPrice = GetLong(c, "sellPrice") ?? 0;
                if (stock > 0 && buyPrice > 0)
                    marketOrders.Add(new CarrierMarketOrderItem { Commodity = comm, OrderType = "Sell", Quantity = stock, Price = buyPrice });
                if (demand > 0 && sellPrice > 0)
                    marketOrders.Add(new CarrierMarketOrderItem { Commodity = comm, OrderType = "Buy", Quantity = demand, Price = sellPrice });
            }
        }

        var carrierServices = new List<string>();
        if (root.TryGetProperty("servicesCrew", out var crewObj) && crewObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var svc in crewObj.EnumerateObject())
            {
                if (svc.Value.ValueKind == JsonValueKind.Object &&
                    GetStr(svc.Value, "status")?.Equals("active", StringComparison.OrdinalIgnoreCase) == true)
                {
                    carrierServices.Add(svc.Name);
                }
            }
        }

        var bartenderOrders = new List<CarrierMaterialItem>();
        if (root.TryGetProperty("orders", out var ordersObj) && ordersObj.ValueKind == JsonValueKind.Object &&
            ordersObj.TryGetProperty("onfootmicroresources", out var microObj) && microObj.ValueKind == JsonValueKind.Object)
        {
            if (microObj.TryGetProperty("sales", out var salesObj) && salesObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in salesObj.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    string itemName = GetStr(prop.Value, "locName") ?? GetStr(prop.Value, "name") ?? prop.Name;
                    int stock = (int)(GetLong(prop.Value, "stock") ?? 0);
                    long price = GetLong(prop.Value, "price") ?? 0;
                    if (!string.IsNullOrWhiteSpace(itemName) && stock > 0)
                        bartenderOrders.Add(new CarrierMaterialItem { Name = prop.Name, DisplayName = itemName, Stock = stock, Demand = 0, Price = price });
                }
            }
            if (microObj.TryGetProperty("purchases", out var purchObj) && purchObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in purchObj.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    string itemName = GetStr(prop.Value, "locName") ?? GetStr(prop.Value, "name") ?? prop.Name;
                    int demand = (int)(GetLong(prop.Value, "outstanding") ?? GetLong(prop.Value, "total") ?? 0);
                    long price = GetLong(prop.Value, "price") ?? 0;
                    if (!string.IsNullOrWhiteSpace(itemName) && demand > 0)
                        bartenderOrders.Add(new CarrierMaterialItem { Name = prop.Name, DisplayName = itemName, Stock = 0, Demand = demand, Price = price });
                }
            }
        }

        _fileWatcher.ApplyCapiEnrichment(state =>
        {
            bool localCarrierFresh = state.IsGameDetected &&
                state.Carrier?.Id == id &&
                state.LastCarrierJournalUtc.HasValue &&
                DateTimeOffset.UtcNow - state.LastCarrierJournalUtc.Value < TimeSpan.FromMinutes(10);

            var carrier = state.Carrier is not null && (state.Carrier.Id == id || id <= 0 || state.Carrier.Id <= 0)
                ? state.Carrier.Snapshot()
                : new CarrierState { Id = id };
            if (id > 0) carrier.Id = id;
            if (!string.IsNullOrWhiteSpace(decodedName)) carrier.Name = decodedName;
            if (!string.IsNullOrWhiteSpace(callsign)) carrier.Callsign = callsign;
            if (!localCarrierFresh || string.IsNullOrWhiteSpace(carrier.System))
            {
                if (!string.IsNullOrWhiteSpace(sys)) carrier.System = sys;
            }
            if (!localCarrierFresh || !carrier.Fuel.HasValue)
            {
                if (fuel.HasValue) carrier.Fuel = fuel;
            }
            if (!localCarrierFresh || !carrier.Balance.HasValue)
            {
                if (balance.HasValue) carrier.Balance = balance;
            }
            if (reserve.HasValue) carrier.Reserve = reserve;
            if (available.HasValue) carrier.Available = available;
            if (reservePct.HasValue) carrier.ReservePercent = reservePct;
            if (taxRate.HasValue) carrier.TaxRate = taxRate;
            if (weeklyUpkeep.HasValue && weeklyUpkeep.Value > 0) carrier.WeeklyUpkeep = weeklyUpkeep;
            if (!string.IsNullOrWhiteSpace(dockingAccess)) carrier.DockingAccess = dockingAccess;
            if (allowNotorious.HasValue) carrier.AllowNotorious = allowNotorious;
            if (totalCap.HasValue) carrier.Capacity = totalCap;
            if (freeSpace.HasValue) carrier.FreeSpace = freeSpace;
            if (cargoUsed.HasValue) carrier.Cargo = cargoUsed;
            carrier.UpdatedAt = DateTimeOffset.UtcNow;

            state.Carrier = carrier;
            state.CarrierCargo = cargoItems
                .GroupBy(static i => i.Commodity, StringComparer.OrdinalIgnoreCase)
                .Select(static g => new CarrierCargoItem
                {
                    Commodity = g.First().Commodity,
                    Quantity = g.Sum(static x => x.Quantity),
                    Stolen = g.Any(static x => x.Stolen),
                    OriginSystem = g.First().OriginSystem
                })
                .OrderByDescending(static i => i.Quantity)
                .ToList();
            state.CarrierMarketOrders = marketOrders
                .OrderBy(static o => o.OrderType, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(static o => o.Quantity)
                .ToList();
            if (carrierServices.Count > 0)
                state.CarrierServices = carrierServices;
            if (bartenderOrders.Count > 0 && state.CarrierMaterials.Count == 0)
            {
                state.CarrierMaterials = bartenderOrders;
                state.CarrierMaterialsStation = !string.IsNullOrWhiteSpace(decodedName) ? decodedName : callsign;
            }
        });
    }

    /// <summary>
    /// Fetches official Frontier cAPI /journal history for a given UTC date (default: today).
    /// Requires EnableFrontierCapi to be enabled and connected.
    /// </summary>
    public async Task<(bool Success, int LineCount, string Message)> FetchJournalHistoryAsync(DateTime? dateUtc = null, CancellationToken ct = default)
    {
        if (!_configService.Current.EnableFrontierCapi || !IsConnected)
            return (false, 0, "Frontier cAPI is disabled or not connected.");

        string? token = await EnsureValidAccessTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token))
            return (false, 0, "Frontier cAPI token expired.");

        DateTime target = (dateUtc ?? DateTime.UtcNow).Date;
        string endpoint = $"/journal/{target:yyyy/MM/dd}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, CapiBaseUrl + endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _httpClient.SendAsync(req, ct);
            if ((int)resp.StatusCode == 204)
                return (true, 0, $"Frontier /journal ({target:yyyy-MM-dd}): no entries.");
            if (!resp.IsSuccessStatusCode)
                return (false, 0, $"Frontier /journal HTTP {(int)resp.StatusCode}");

            string raw = await resp.Content.ReadAsStringAsync(ct);
            var (lines, summary) = SummarizeJournalLines(raw, target);
            UpdateStatus(summary);
            return (true, lines, summary);
        }
        catch (Exception ex)
        {
            return (false, 0, $"Frontier /journal error: {ex.Message}");
        }
    }

    internal static (int LineCount, string Summary) SummarizeJournalLines(string rawJsonl, DateTime dateUtc)
    {
        string[] rawLines = rawJsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int jumps = 0, scans = 0, organics = 0, missions = 0;
        long earnedCredits = 0;
        var systems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in rawLines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string evt = GetStr(root, "event") ?? "";
                switch (evt)
                {
                    case "FSDJump":
                        jumps++;
                        string? sys = GetStr(root, "StarSystem");
                        if (!string.IsNullOrWhiteSpace(sys)) systems.Add(sys);
                        break;
                    case "Scan":
                        scans++;
                        break;
                    case "ScanOrganic":
                        if (string.Equals(GetStr(root, "ScanType"), "Analyse", StringComparison.OrdinalIgnoreCase))
                            organics++;
                        break;
                    case "MissionCompleted":
                        missions++;
                        earnedCredits += GetLong(root, "Reward") ?? 0;
                        break;
                    case "RedeemVoucher":
                        earnedCredits += GetLong(root, "Amount") ?? 0;
                        break;
                    case "MultiSellExplorationData" or "SellExplorationData" or "SellOrganicData":
                        earnedCredits += GetLong(root, "TotalEarnings") ?? 0;
                        break;
                }
            }
            catch
            {
                // Skip malformed line
            }
        }

        string summary = $"Frontier /journal ({dateUtc:yyyy-MM-dd}): {rawLines.Length} events · {jumps} jumps ({systems.Count} systems) · {scans} scans · {organics} exo · {missions} missions · {earnedCredits:N0} CR earned";
        return (rawLines.Length, summary);
    }

    private void ApplyCommunityGoalsJson(JsonElement root)
    {
        var goals = new List<CommunityGoalItem>();
        if (root.TryGetProperty("activeInitiatives", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in arr.EnumerateArray())
            {
                string title = GetStr(g, "title") ?? "";
                if (string.IsNullOrWhiteSpace(title)) continue;
                string expiryRaw = GetStr(g, "expiry") ?? "";
                string expiryDisp = DateTimeOffset.TryParse(expiryRaw, out var dt)
                    ? dt.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.CurrentCulture)
                    : expiryRaw;

                goals.Add(new CommunityGoalItem
                {
                    Id = GetLong(g, "id") ?? 0,
                    Title = title,
                    StarSystem = GetStr(g, "starsystem_name") ?? "",
                    StationName = GetStr(g, "market_name") ?? "",
                    ExpiryDisplay = expiryDisp,
                    CurrentTotal = GetLong(g, "qty") ?? 0,
                    PlayerContribution = GetLong(g, "plr_contribution") ?? 0
                });
            }
        }

        _fileWatcher.MergeCommunityGoals(goals, isAuthoritative: true);
    }

    private void ApplyMarketJson(JsonElement root)
    {
        if (!root.TryGetProperty("commodities", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;

        string station = GetStr(root, "name") ?? _fileWatcher.State.StationName;
        var items = new List<MarketCommodityItem>();
        foreach (var c in arr.EnumerateArray())
        {
            string name = GetStr(c, "name") ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;
            items.Add(new MarketCommodityItem
            {
                Name = name,
                DisplayName = GetStr(c, "locName") ?? name,
                Category = GetStr(c, "categoryname") ?? "",
                DisplayCategory = GetStr(c, "categoryname") ?? "",
                BuyPrice = GetLong(c, "buyPrice") ?? 0,
                SellPrice = GetLong(c, "sellPrice") ?? 0,
                MeanPrice = GetLong(c, "meanPrice") ?? 0,
                Stock = GetLong(c, "stock") ?? 0,
                Demand = GetLong(c, "demand") ?? 0,
                StockBracket = (int)(GetLong(c, "stockBracket") ?? 0),
                DemandBracket = (int)(GetLong(c, "demandBracket") ?? 0)
            });
        }

        if (items.Count == 0) return;
        _fileWatcher.ApplyCapiEnrichment(state =>
        {
            if (state.Market.Items.Count == 0 ||
                !string.Equals(state.Market.StationName, station, StringComparison.OrdinalIgnoreCase))
            {
                state.Market = new MarketSnapshot
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    MarketId = GetLong(root, "id"),
                    DataSource = "cAPI",
                    StationName = station,
                    StarSystem = state.CurrentSystem,
                    Items = items
                };
                _fileWatcher.SyncMarketColonisationNeeds(force: true);
            }
        });
    }

    private void ApplyShipyardJson(JsonElement root)
    {
        if (!root.TryGetProperty("ships", out var shipsObj) ||
            !shipsObj.TryGetProperty("shipyard_list", out var listObj))
            return;

        string station = GetStr(root, "name") ?? _fileWatcher.State.StationName;
        var items = new List<ShipyardItem>();
        IEnumerable<JsonElement> entries = listObj.ValueKind switch
        {
            JsonValueKind.Object => listObj.EnumerateObject().Select(static p => p.Value),
            JsonValueKind.Array => listObj.EnumerateArray(),
            _ => Enumerable.Empty<JsonElement>()
        };

        foreach (var sh in entries)
        {
            string name = GetStr(sh, "name") ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;
            items.Add(new ShipyardItem
            {
                ShipType = name,
                DisplayName = name,
                ShipPrice = GetLong(sh, "basevalue") ?? 0
            });
        }

        if (items.Count == 0) return;
        _fileWatcher.ApplyCapiEnrichment(state =>
        {
            if (state.Shipyard.Items.Count <= 1 ||
                !string.Equals(state.Shipyard.StationName, station, StringComparison.OrdinalIgnoreCase))
            {
                state.Shipyard = new ShipyardSnapshot
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    MarketId = GetLong(root, "id"),
                    DataSource = "cAPI",
                    StationName = station,
                    StarSystem = state.CurrentSystem,
                    Items = items
                };
            }
        });
    }

    // =========================================================================
    // Windows CurrentUser DPAPI token protection (System.Security.Cryptography.ProtectedData)
    // =========================================================================

    private void SaveTokensToDisk()
    {
        if (_tokens is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_tokenFilePath)!);
            byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_tokens));
            byte[] encrypted = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_tokenFilePath, encrypted);
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("FrontierCapi", $"Failed to save DPAPI-protected cAPI tokens: {ex.Message}");
        }
    }

    private void LoadTokensFromDisk()
    {
        try
        {
            if (!File.Exists(_tokenFilePath)) return;
            byte[] encrypted = File.ReadAllBytes(_tokenFilePath);
            byte[] plain = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            _tokens = JsonSerializer.Deserialize<CapiTokenStore>(Encoding.UTF8.GetString(plain));
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("FrontierCapi", $"Failed to load DPAPI-protected cAPI tokens: {ex.Message}");
            _tokens = null;
        }
    }

    private static string GenerateBase64UrlRandom(int byteCount) =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string DecodeHexUtf8(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length % 2 != 0)
            return hex ?? "";
        try
        {
            byte[] bytes = Convert.FromHexString(hex);
            return Encoding.UTF8.GetString(bytes).Trim();
        }
        catch
        {
            return hex;
        }
    }

    private void UpdateStatus(string status)
    {
        LastStatus = status;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _gameEvents.JournalEventReceived -= OnJournalEventReceived;
        _httpClient.Dispose();
        _syncLock.Dispose();
        _tokenRefreshLock.Dispose();
    }
}
