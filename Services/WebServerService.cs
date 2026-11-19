// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/WebServerService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Embedded Kestrel HTTPS/WebSocket server powering the local Web HUD and remote controls.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using HUDnopy.Models;
using HUDnopy.Localization;

namespace HUDnopy.Services;

/// <summary>
/// Embedded Kestrel HTTPS/WebSocket server powering the local Web HUD and remote controls.
/// </summary>
public sealed record WebNetworkInterfaceOption(string Id, string DisplayName, IPAddress[] Addresses);

public class WebServerService : IDisposable
{
    private readonly ConfigService _configService;
    private readonly EliteFileWatcherService _fileWatcher;
    private readonly BindingsService _bindingsService;
    private readonly InputSimulatorService _inputSimulator;
    private readonly AlertService _alertService;
    private readonly SpanshSystemDataService _spanshSystemDataService;
    private readonly AiCopilotService _aiCopilot;

    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private readonly SemaphoreSlim _socketSendLock = new(1, 1);
    private int _broadcastRequested;
    private int _broadcastRunning;
    private (IReadOnlyList<GameBinding>? Source, string Language, DeckBindingView[] Views) _cachedDeckBindings;
    private (SpanshSystemDataState? Source, string Language, SpanshSystemDisplay View) _cachedSpanshView;
    private (CarrierState? Source, string Language, CarrierDisplay View)? _cachedCarrierView;
    private (IReadOnlyList<MarketCommodityItem>? Source, string Language, string[] Flags) _cachedMarketFlags;
    private (IReadOnlyList<ExplorationBodyItem>? Source, string Language, ExplorationBodyView[] Views) _cachedExplorationBodies;
    private (IReadOnlyList<ProfileValueItem>? Source, string Language, ProfileValueView[] Views) _cachedProfile;
    private (IReadOnlyList<JournalEntryItem>? Source, string Language, JournalEntryView[] Views) _cachedJournalEvents;
    private X509Certificate2? _certificate;
    private WebAccessControl? _accessControl;
    private string[] _activeUrls = [];

    public bool IsRunning { get; private set; }
    public int CurrentPort { get; private set; }
    public string StatusMessage { get; private set; } = "Detenido";
    public string? SessionCode => _accessControl?.Code;
    public string? CertificateFingerprint => _certificate is null ? null : Convert.ToHexString(_certificate.GetCertHash(HashAlgorithmName.SHA256));

    public event Action? ServerStatusChanged;

    public WebServerService(
        ConfigService configService,
        EliteFileWatcherService fileWatcher,
        BindingsService bindingsService,
        InputSimulatorService inputSimulator,
        AlertService alertService,
        SpanshSystemDataService spanshSystemDataService,
        AiCopilotService aiCopilot)
    {
        _configService = configService;
        _fileWatcher = fileWatcher;
        _bindingsService = bindingsService;
        _inputSimulator = inputSimulator;
        _alertService = alertService;
        _spanshSystemDataService = spanshSystemDataService;
        _aiCopilot = aiCopilot;

        _fileWatcher.StateUpdated += state => { _ = BroadcastStateAsync(); };
        _bindingsService.BindingsUpdated += binds => { _ = BroadcastStateAsync(); };
        _alertService.AlertsChanged += () => { _ = BroadcastStateAsync(); };
        _spanshSystemDataService.StateChanged += state => { _ = BroadcastStateAsync(); };
        _configService.ConfigChanged += config => { _ = BroadcastStateAsync(); };
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;

        int port = _configService.Current.WebServerPort;
        CurrentPort = port;
        _cts = new CancellationTokenSource();

        try
        {
            string webUiHtml = WebUiDocument.Load();
            string mode = _configService.Current.WebAccessMode;
            if (mode is not ("local" or "open" or "code" or "vpn"))
                throw new InvalidOperationException("Modo de acceso web no válido.");
            var interfaces = GetNetworkInterfaces();
            var selectedInterface = mode == "vpn"
                ? interfaces.FirstOrDefault(item => item.Id == _configService.Current.WebVpnInterfaceId)
                : null;
            if (mode == "vpn" && selectedInterface is null)
                throw new InvalidOperationException("La interfaz VPN seleccionada no está disponible.");

            var allAddresses = interfaces.SelectMany(item => item.Addresses).Distinct().ToArray();
            _certificate = WebHttpsCertificate.LoadOrCreate(allAddresses);
            _accessControl = mode == "code" ? new WebAccessControl() : null;
            var access = _accessControl;
            var urls = new List<string> { $"https://localhost:{port}" };
            if (mode is "open" or "code")
                urls.AddRange(allAddresses.Select(address => $"https://{address}:{port}"));
            else if (selectedInterface is not null)
                urls.AddRange(selectedInterface.Addresses.Select(address => $"https://{address}:{port}"));
            _activeUrls = urls.Distinct(StringComparer.Ordinal).ToArray();

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = 4096;
                if (mode is "open" or "code")
                    options.ListenAnyIP(port, endpoint => endpoint.UseHttps(_certificate));
                else
                {
                    options.ListenLocalhost(port, endpoint => endpoint.UseHttps(_certificate));
                    if (selectedInterface is not null)
                        foreach (var address in selectedInterface.Addresses.Distinct())
                            options.Listen(address, port, endpoint => endpoint.UseHttps(_certificate));
                }
            });

            var app = builder.Build();
            _app = app;
            app.UseWebSockets();
            app.Use(async (context, next) =>
            {
                string host = context.Request.Host.Host;
                if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) && !IPAddress.TryParse(host, out _))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers.ContentSecurityPolicy =
                    "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self' wss:; base-uri 'none'; frame-ancestors 'none'";
                if ((!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) ||
                    context.WebSockets.IsWebSocketRequest)
                {
                    string origin = context.Request.Headers.Origin.ToString();
                    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                        uri.Scheme != Uri.UriSchemeHttps ||
                        !string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }
                }
                if (access is not null && context.Request.Path != "/login" && context.Request.Path != "/favicon.ico" &&
                    access.GetExpiry(context.Request.Cookies["hudnopy-session"]) is null)
                {
                    if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/")
                        context.Response.Redirect("/login");
                    else
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                await next(context);
            });

            app.MapGet("/login", async context =>
            {
                if (access is null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await WriteLoginPageAsync(context, null);
            });
            app.MapPost("/login", async context =>
            {
                if (access is null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                if (!context.Request.HasFormContentType)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                if (!access.TryLogin(form["code"].ToString(), out string? token, out bool blocked))
                {
                    context.Response.StatusCode = blocked ? StatusCodes.Status429TooManyRequests : StatusCodes.Status401Unauthorized;
                    await WriteLoginPageAsync(context, blocked ? "accessBlocked" : "accessInvalid");
                    return;
                }
                context.Response.Cookies.Append("hudnopy-session", token!, new CookieOptions
                {
                    HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, IsEssential = true,
                    Path = "/", MaxAge = TimeSpan.FromHours(12)
                });
                context.Response.Redirect("/");
            });

            byte[]? faviconBytes = LoadFaviconBytes();
            app.MapGet("/favicon.ico", async context =>
            {
                if (faviconBytes is null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                context.Response.ContentType = "image/x-icon";
                await context.Response.Body.WriteAsync(faviconBytes, context.RequestAborted);
            });

            app.MapGet("/", async context =>
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(webUiHtml);
            });

            app.MapGet("/api/state", async context =>
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                var payload = BuildFullPayload();
                await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
            });

            app.MapPost("/api/command/{actionId}", async (HttpContext context, string actionId) =>
            {
                var (success, message) = await _inputSimulator.ExecuteActionAsync(actionId);
                context.Response.ContentType = "application/json; charset=utf-8";
                var state = _fileWatcher.State;
                string language = _configService.Current.InterfaceLanguage;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    success,
                    message,
                    state,
                    sectionLanguage = language,
                    onFootStatus = FlightTranslations.OnFootStatus(state, language),
                    operationsView = BuildOperationsView(state, language),
                    carrierView = GetCarrierView(state.Carrier, language)
                }));
            });

            app.MapPost("/api/ai/ask", async (HttpContext context) =>
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                string prompt = "";
                try
                {
                    using var doc = await JsonDocument.ParseAsync(context.Request.Body);
                    if (doc.RootElement.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String)
                        prompt = p.GetString() ?? "";
                }
                catch
                {
                }

                var (success, reply) = await _aiCopilot.AskAsync(prompt);
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    success,
                    reply,
                    provider = _aiCopilot.GetActiveProviderSummary()
                }));
            });

            app.MapPost("/api/theme", async (HttpContext context) =>
            {
                using var doc = await JsonDocument.ParseAsync(context.Request.Body);
                if (doc.RootElement.TryGetProperty("accentHex", out var accProp))
                {
                    string hex = accProp.GetString() ?? "#FF7100";
                    _configService.Current.ThemeAccentHex = hex;
                    _configService.Current.ThemeSecondaryHex = hex;
                    _configService.Save();
                    _fileWatcher.RefreshState();
                }
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync("{\"ok\":true}");
            });

            app.Map("/ws", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                DateTimeOffset? expiry = access?.GetExpiry(context.Request.Cookies["hudnopy-session"]);
                if (access is not null && expiry is null)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                using var socketCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                if (expiry is not null)
                {
                    TimeSpan remaining = expiry.Value - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                    socketCts.CancelAfter(remaining);
                }
                using var ws = await context.WebSockets.AcceptWebSocketAsync();
                var id = Guid.NewGuid();
                _sockets[id] = ws;

                try
                {
                    await SendToSocketAsync(ws, BuildFullPayload());
                    var buffer = new byte[1024];
                    while (ws.State == WebSocketState.Open && !socketCts.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), socketCts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                            break;
                    }
                }
                catch
                {
                    // Cliente desconectado
                }
                finally
                {
                    _sockets.TryRemove(id, out _);
                }
            });

            await _app.StartAsync(_cts.Token);
            IsRunning = true;
            StatusMessage = $"Activo en puerto {port}";
            ServerStatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            if (_app is not null)
            {
                await _app.DisposeAsync();
                _app = null;
            }
            _certificate?.Dispose();
            _certificate = null;
            _accessControl = null;
            _activeUrls = [];
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            StatusMessage = $"Error en puerto {port}: {ex.Message}";
            ServerStatusChanged?.Invoke();
        }
    }

    public async Task StopAsync()
    {
        if (!IsRunning || _app == null) return;
        try
        {
            _cts?.Cancel();
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            foreach (var kvp in _sockets)
            {
                try
                {
                    await kvp.Value.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Server stopping", closeTimeout.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
                {
                    kvp.Value.Abort();
                }
            }
            _sockets.Clear();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        catch
        {
            // Ignorar errores al cerrar
        }
        finally
        {
            _app = null;
            _certificate?.Dispose();
            _certificate = null;
            _accessControl = null;
            _activeUrls = [];
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            StatusMessage = "Detenido";
            ServerStatusChanged?.Invoke();
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    private DeckBindingView[] GetDeckBindings(string language)
    {
        var source = _bindingsService.Bindings;
        var cached = _cachedDeckBindings;
        if (ReferenceEquals(cached.Source, source) && string.Equals(cached.Language, language, StringComparison.Ordinal))
            return cached.Views;

        var views = source.Select(binding => DeckTranslations.Present(binding, language)).ToArray();
        _cachedDeckBindings = (source, language, views);
        return views;
    }

    private SpanshSystemDisplay GetSpanshView(string language)
    {
        var source = _spanshSystemDataService.State;
        var cached = _cachedSpanshView;
        if (ReferenceEquals(cached.Source, source) && string.Equals(cached.Language, language, StringComparison.Ordinal))
            return cached.View;

        var view = SpanshTranslations.Present(source, language);
        _cachedSpanshView = (source, language, view);
        return view;
    }

    private CarrierDisplay GetCarrierView(CarrierState? carrier, string language)
    {
        if (_cachedCarrierView is { } cached &&
            ReferenceEquals(cached.Source, carrier) &&
            string.Equals(cached.Language, language, StringComparison.Ordinal))
            return cached.View;

        var view = CarrierTranslations.Present(carrier, language);
        _cachedCarrierView = (carrier, language, view);
        return view;
    }

    private object BuildFullPayload()
    {
        string language = _configService.Current.InterfaceLanguage;
        var state = _fileWatcher.State;
        return new
        {
            state,
            headerModules = HeaderTelemetry.Present(state, _configService.Current, language),
            bindings = _bindingsService.Bindings,
            deckBindings = GetDeckBindings(language),
            deckTranslations = DeckTranslations.ForLanguage(language),
            hideUnassignedBindings = _configService.Current.HideUnassignedBindings,
            hideNavigationMenuText = _configService.Current.HideNavigationMenuText,
            navigationMenuPosition = _configService.Current.NavigationMenuPosition,
            missionExpiryWarningHours = _configService.Current.MissionExpiryWarningHours,
            missionExpiryCriticalHours = _configService.Current.MissionExpiryCriticalHours,
            sectionLanguage = language,
            sectionTranslations = SectionTranslations.ForLanguage(language),
            navigationTranslations = NavigationTranslations.ForLanguage(language),
            dashboardTranslations = DashboardTranslations.ForLanguage(language),
            coreAlertTranslations = CoreAlertTranslations.ForLanguage(language),
            tableHeaders = TableHeaderTranslations.ForLanguage(language),
            materialTranslations = MaterialTranslations.ForLanguage(language),
            flightTranslations = FlightTranslations.ForLanguage(language),
            onFootStatus = FlightTranslations.OnFootStatus(state, language),
            operationsTranslations = OperationsTranslations.ForLanguage(language),
            explorationTranslations = ExplorationTranslations.ForLanguage(language),
            inputTranslations = InputTranslations.ForLanguage(language),
            operationsView = BuildOperationsView(state, language),
            spanshView = GetSpanshView(language),
            spanshEnabled = _configService.Current.EnableSpanshSystemData,
            carrierView = GetCarrierView(state.Carrier, language),
            languageSettings = LanguageSettingsTranslations.ForLanguage(language),
            presetName = _bindingsService.UsingDefaultBindings ? DeckTranslations.ForLanguage(language)["defaultProfile"] : _bindingsService.ActivePresetName,
            alerts = _alertService.Alerts,
            activeAlerts = _alertService.ActiveAlerts
        };
    }

    private object BuildOperationsView(ShipState state, string language)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo(language);
        string progress = state.Colonisation.ConstructionProgress.HasValue
            ? $"{(state.Colonisation.ConstructionProgress.Value * 100).ToString("0.#", culture)}%"
            : "---";

        var marketFlagsCache = _cachedMarketFlags;
        if (!ReferenceEquals(marketFlagsCache.Source, state.Market.Items) ||
            !string.Equals(marketFlagsCache.Language, language, StringComparison.Ordinal))
        {
            marketFlagsCache = (state.Market.Items, language,
                state.Market.Items.Select(item => OperationsTranslations.MarketFlags(item, language)).ToArray());
            _cachedMarketFlags = marketFlagsCache;
        }

        var explorationCache = _cachedExplorationBodies;
        if (!ReferenceEquals(explorationCache.Source, state.ExplorationBodies) ||
            !string.Equals(explorationCache.Language, language, StringComparison.Ordinal))
        {
            explorationCache = (state.ExplorationBodies, language,
                state.ExplorationBodies.Select(body => ExplorationTranslations.Present(body, language)).ToArray());
            _cachedExplorationBodies = explorationCache;
        }

        var profileCache = _cachedProfile;
        if (!ReferenceEquals(profileCache.Source, state.CommanderProfile) ||
            !string.Equals(profileCache.Language, language, StringComparison.Ordinal))
        {
            profileCache = (state.CommanderProfile, language,
                state.CommanderProfile.Select(item => ProfileTranslations.Present(item, language)).ToArray());
            _cachedProfile = profileCache;
        }

        var journalCache = _cachedJournalEvents;
        if (!ReferenceEquals(journalCache.Source, state.RecentEvents) ||
            !string.Equals(journalCache.Language, language, StringComparison.Ordinal))
        {
            journalCache = (state.RecentEvents, language,
                state.RecentEvents.Select(entry => JournalTranslations.Present(entry, language)).ToArray());
            _cachedJournalEvents = journalCache;
        }

        return new
        {
            cargoStates = state.CargoInventory.Select(item => OperationsTranslations.CargoState(item, language)).ToArray(),
            missions = state.ActiveMissions.Select(item => new
            {
                name = OperationsTranslations.MissionName(item, language),
                expiry = OperationsTranslations.Expiry(item.Expiry, language),
                remaining = OperationsTranslations.Remaining(item.Expiry, language)
            }).ToArray(),
            passengerExpiries = state.PassengerManifest.Select(item => OperationsTranslations.Expiry(item.Expiry, language)).ToArray(),
            passengerTypes = state.PassengerManifest.Select(item => OperationsTranslations.PassengerType(item, language)).ToArray(),
            backpackTypes = state.BackpackInventory.Select(item => OperationsTranslations.BackpackType(item, language)).ToArray(),
            marketFlags = marketFlagsCache.Flags,
            marketStatus = OperationsTranslations.Snapshot(state.Market.Timestamp, state.Market.StationName, state.Market.StarSystem, "marketUnavailable", language),
            outfittingStatus = OperationsTranslations.Snapshot(state.Outfitting.Timestamp, state.Outfitting.StationName, state.Outfitting.StarSystem, "outfittingUnavailable", language),
            shipyardStatus = OperationsTranslations.Snapshot(state.Shipyard.Timestamp, state.Shipyard.StationName, state.Shipyard.StarSystem, "shipyardUnavailable", language),
            constructionStatus = OperationsTranslations.Construction(state.Colonisation, language),
            constructionProgress = string.Format(OperationsTranslations.ForLanguage(language)["progress"], progress),
            explorationBodies = explorationCache.Views,
            profile = profileCache.Views,
            journalEvents = journalCache.Views
        };
    }

    public async Task BroadcastStateAsync()
    {
        if (!IsRunning || _sockets.IsEmpty) return;

        Interlocked.Exchange(ref _broadcastRequested, 1);
        if (Interlocked.CompareExchange(ref _broadcastRunning, 1, 0) != 0)
            return;

        try
        {
            while (Interlocked.Exchange(ref _broadcastRequested, 0) == 1)
            {
                if (!IsRunning || _sockets.IsEmpty) return;

                await _socketSendLock.WaitAsync();
                try
                {
                    if (!IsRunning || _sockets.IsEmpty) return;

                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(BuildFullPayload());
                    foreach (var kvp in _sockets)
                    {
                        var ws = kvp.Value;
                        if (ws.State == WebSocketState.Open)
                        {
                            try
                            {
                                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                            }
                            catch
                            {
                                _sockets.TryRemove(kvp.Key, out _);
                            }
                        }
                        else
                        {
                            _sockets.TryRemove(kvp.Key, out _);
                        }
                    }
                }
                finally
                {
                    _socketSendLock.Release();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _broadcastRunning, 0);
        }
    }

    private async Task SendToSocketAsync(WebSocket ws, object payload)
    {
        if (ws.State != WebSocketState.Open) return;
        await _socketSendLock.WaitAsync();
        try
        {
            if (ws.State != WebSocketState.Open) return;

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _socketSendLock.Release();
        }
    }

    private async Task WriteLoginPageAsync(HttpContext context, string? errorKey)
    {
        var text = SettingsTranslations.ForLanguage(_configService.Current.InterfaceLanguage);
        string Encode(string value) => HtmlEncoder.Default.Encode(value);
        string error = errorKey is null ? "" : $"<p role=\"alert\">{Encode(text[errorKey])}</p>";
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync($$"""
            <!doctype html><html lang="{{Encode(_configService.Current.InterfaceLanguage)}}"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1"><link rel="icon" href="/favicon.ico" type="image/x-icon"><title>HUDnopy</title>
            <style>body{font:16px sans-serif;background:#090c10;color:#eee;display:grid;place-items:center;min-height:90vh}
            main{border:1px solid #ff7100;padding:24px;max-width:28rem}input,button{font:inherit;padding:10px;margin-top:12px}
            label{display:block}p{color:#ff8585}</style></head><body><main>
            <h1>{{Encode(text["accessLoginTitle"])}}</h1>{{error}}
            <form method="post" action="/login"><label for="code">{{Encode(text["accessCodeLabel"])}}</label>
            <input id="code" name="code" type="password" inputmode="numeric" pattern="[0-9]{8}" maxlength="8" required autocomplete="one-time-code">
            <button type="submit">{{Encode(text["accessLoginButton"])}}</button></form></main></body></html>
            """, context.RequestAborted);
    }

    private static byte[]? LoadFaviconBytes()
    {
        try
        {
            var streamInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/HUDnopy.ico"));
            if (streamInfo?.Stream != null)
            {
                using var stream = streamInfo.Stream;
                using var ms = new System.IO.MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }
        catch
        {
        }
        return null;
    }

    public static IReadOnlyList<WebNetworkInterfaceOption> GetNetworkInterfaces()
    {
        var interfaces = new List<WebNetworkInterfaceOption>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var addresses = ni.GetIPProperties().UnicastAddresses.Select(item => item.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                .Distinct().ToArray();
            if (addresses.Length > 0)
                interfaces.Add(new WebNetworkInterfaceOption(ni.Id, $"{ni.Name} ({string.Join(", ", addresses.Select(a => a.ToString()))})", addresses));
        }
        return interfaces;
    }

    public IReadOnlyList<string> GetAccessUrls() => _activeUrls;

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _app?.StopAsync().GetAwaiter().GetResult();
        }
        catch { }
    }

}
