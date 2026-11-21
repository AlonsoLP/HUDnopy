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

namespace HUDnopy.Services;

/// <summary>
/// Frontier Companion API (cAPI) client with OAuth 2.0 + PKCE and Windows DPAPI token storage.
/// </summary>
public sealed class FrontierCapiService : IDisposable
{
    public const string DefaultClientId = "77f5fc55-aefa-491d-94c0-b0b4aea27552";
    // Frontier only allows https:// redirect_uri values. This static GitHub Pages relay
    // (docs/callback.html) forwards code + state to local loopback http://127.0.0.1:{port}/callback.
    public const string PublicRedirectUri = "https://alonsolp.github.io/HUDnopy/callback.html";
    private const string AuthBaseUrl = "https://auth.frontierstore.net";
    private const string CapiBaseUrl = "https://companion.orerve.net";
    private static readonly TimeSpan AutoSyncCooldown = TimeSpan.FromMinutes(5);

    private readonly ConfigService _configService;
    private readonly EliteFileWatcherService _fileWatcher;
    private readonly HttpClient _httpClient;
    private readonly string _tokenFilePath;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

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
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HUDnopy", "0.1"));

        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HUDnopy");
        _tokenFilePath = Path.Combine(dataDir, "frontier_capi.dat");

        LoadTokensFromDisk();
        gameEvents.JournalEventReceived += OnJournalEventReceived;
    }

    public string GetRedirectUri() => PublicRedirectUri;

    public int GetCallbackPort() => Math.Clamp(_configService.Current.FrontierCapiCallbackPort, 1024, 65535);

    private void OnJournalEventReceived(GameJournalEvent gameEvent)
    {
        if (gameEvent.IsHistorical)
            return;

        var cfg = _configService.Current;
        if (!cfg.EnableFrontierCapi || !IsConnected)
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

        string clientId = GetEffectiveClientId();
        if (string.IsNullOrWhiteSpace(_tokens.RefreshToken))
            return null;

        try
        {
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
        catch
        {
            return null;
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

        _fileWatcher.ApplyCapiEnrichment(state =>
        {
            if (ships.Count > 0)
                state.StoredShips = ships.OrderBy(static s => s.StarSystem, StringComparer.CurrentCultureIgnoreCase).ThenBy(static s => s.ShipType).ToList();

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
        long id = GetLong(root, "marketId") ?? 0;
        if (id <= 0) return;

        string callsign = GetStr(root, "name", "callsign") ?? "";
        string nameHex = GetStr(root, "name", "vanityName") ?? "";
        string decodedName = DecodeHexUtf8(nameHex);
        string sys = GetStr(root, "currentStarSystem") ?? "";
        long? fuel = GetLong(root, "fuel");
        long? balance = GetLong(root, "balance");
        long? reserve = root.TryGetProperty("finance", out var fin) ? GetLong(fin, "bankReservedBalance") : null;
        long? available = root.TryGetProperty("finance", out var fin2) ? GetLong(fin2, "bankBalance") : balance;
        string dockingAccess = GetStr(root, "dockingAccess") ?? "";
        bool? allowNotorious = root.TryGetProperty("notoriousAccess", out var na) && na.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? na.GetBoolean() : null;

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

        _fileWatcher.ApplyCapiEnrichment(state =>
        {
            var carrier = state.Carrier?.Id == id ? state.Carrier.Snapshot() : new CarrierState { Id = id };
            if (!string.IsNullOrWhiteSpace(decodedName)) carrier.Name = decodedName;
            if (!string.IsNullOrWhiteSpace(callsign)) carrier.Callsign = callsign;
            if (!string.IsNullOrWhiteSpace(sys)) carrier.System = sys;
            if (fuel.HasValue) carrier.Fuel = fuel;
            if (balance.HasValue) carrier.Balance = balance;
            if (reserve.HasValue) carrier.Reserve = reserve;
            if (available.HasValue) carrier.Available = available;
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
        });
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

        _fileWatcher.ApplyCapiEnrichment(state => state.CommunityGoals = goals);
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
                    StationName = station,
                    StarSystem = state.CurrentSystem,
                    Items = items
                };
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
            if (state.Shipyard.Items.Count == 0 ||
                !string.Equals(state.Shipyard.StationName, station, StringComparison.OrdinalIgnoreCase))
            {
                state.Shipyard = new ShipyardSnapshot
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    MarketId = GetLong(root, "id"),
                    StationName = station,
                    StarSystem = state.CurrentSystem,
                    Items = items
                };
            }
        });
    }

    // =========================================================================
    // Cifrado DPAPI nativo de Windows (crypt32.dll — sin dependencias NuGet)
    // =========================================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] ProtectDpapi(byte[] plain)
    {
        var input = new DATA_BLOB { cbData = plain.Length, pbData = Marshal.AllocHGlobal(plain.Length) };
        try
        {
            Marshal.Copy(plain, 0, input.pbData, plain.Length);
            if (!CryptProtectData(ref input, "HUDnopy Frontier cAPI", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                throw new InvalidOperationException("DPAPI CryptProtectData failed.");
            try
            {
                byte[] encrypted = new byte[output.cbData];
                Marshal.Copy(output.pbData, encrypted, 0, output.cbData);
                return encrypted;
            }
            finally { LocalFree(output.pbData); }
        }
        finally { Marshal.FreeHGlobal(input.pbData); }
    }

    private static byte[] UnprotectDpapi(byte[] encrypted)
    {
        var input = new DATA_BLOB { cbData = encrypted.Length, pbData = Marshal.AllocHGlobal(encrypted.Length) };
        try
        {
            Marshal.Copy(encrypted, 0, input.pbData, encrypted.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                throw new InvalidOperationException("DPAPI CryptUnprotectData failed.");
            try
            {
                byte[] plain = new byte[output.cbData];
                Marshal.Copy(output.pbData, plain, 0, output.cbData);
                return plain;
            }
            finally { LocalFree(output.pbData); }
        }
        finally { Marshal.FreeHGlobal(input.pbData); }
    }

    private void SaveTokensToDisk()
    {
        if (_tokens is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_tokenFilePath)!);
            byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_tokens));
            byte[] encrypted = ProtectDpapi(plain);
            File.WriteAllBytes(_tokenFilePath, encrypted);
        }
        catch
        {
        }
    }

    private void LoadTokensFromDisk()
    {
        try
        {
            if (!File.Exists(_tokenFilePath)) return;
            byte[] encrypted = File.ReadAllBytes(_tokenFilePath);
            byte[] plain = UnprotectDpapi(encrypted);
            _tokens = JsonSerializer.Deserialize<CapiTokenStore>(Encoding.UTF8.GetString(plain));
        }
        catch
        {
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

    private static string? GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? GetStr(JsonElement el, string parent, string prop) =>
        el.TryGetProperty(parent, out var p) && p.ValueKind == JsonValueKind.Object ? GetStr(p, prop) : null;

    private static long? GetLong(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;

    private void UpdateStatus(string status)
    {
        LastStatus = status;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _syncLock.Dispose();
    }
}
