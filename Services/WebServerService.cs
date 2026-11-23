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
using System.IO;
using System.IO.Compression;
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
using HUDnopy.Services.Integrations;

namespace HUDnopy.Services;

/// <summary>
/// Embedded Kestrel HTTPS/WebSocket server powering the local Web HUD and remote controls.
/// </summary>
public sealed record WebNetworkInterfaceOption(string Id, string DisplayName, IPAddress[] Addresses);

public class WebServerService : IDisposable
{
    private static readonly JsonSerializerOptions CamelCaseJsonOptions = HttpClientHeaderDefaults.CamelCaseJsonOptions;
    private readonly ConfigService _configService;
    private readonly EliteFileWatcherService _fileWatcher;
    private readonly BindingsService _bindingsService;
    private readonly InputSimulatorService _inputSimulator;
    private readonly AlertService _alertService;
    private readonly SpanshSystemDataService _spanshSystemDataService;
    private readonly AiCopilotService _aiCopilot;
    private readonly GalaxyStatusService _galaxyStatus;
    private readonly GalnetService _galnetService;
    private readonly EliteBgsWatchlistService? _eliteBgsWatchlist;
    private readonly ArdentMarketService? _ardentMarket;
    private readonly EdastroCanonnService? _edastroCanonn;
    private readonly CommunitySyncService? _communitySync;
    private readonly AlertSpeechService? _alertSpeech;
    public CockpitRadioService? CockpitRadio { get; set; }

    private sealed class WebSocketClient(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private sealed class PrecompressedAsset
    {
        public byte[] Raw { get; }
        public byte[] Brotli { get; }
        public byte[] Gzip { get; }
        public string ContentType { get; }

        public PrecompressedAsset(string text, string contentType)
        {
            ContentType = contentType;
            Raw = Encoding.UTF8.GetBytes(text);
            Brotli = Compress(Raw, static s => new BrotliStream(s, CompressionLevel.Optimal, leaveOpen: true));
            Gzip = Compress(Raw, static s => new GZipStream(s, CompressionLevel.Optimal, leaveOpen: true));
        }

        private static byte[] Compress(byte[] input, Func<Stream, Stream> factory)
        {
            using var ms = new MemoryStream();
            using (var cs = factory(ms))
                cs.Write(input, 0, input.Length);
            return ms.ToArray();
        }

        public Task WriteAsync(HttpContext context)
        {
            context.Response.ContentType = ContentType;
            context.Response.Headers.Vary = "Accept-Encoding";
            string accept = context.Request.Headers.AcceptEncoding.ToString();
            if (accept.Contains("br", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers.ContentEncoding = "br";
                return context.Response.Body.WriteAsync(Brotli, context.RequestAborted).AsTask();
            }
            if (accept.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers.ContentEncoding = "gzip";
                return context.Response.Body.WriteAsync(Gzip, context.RequestAborted).AsTask();
            }
            return context.Response.Body.WriteAsync(Raw, context.RequestAborted).AsTask();
        }
    }

    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<Guid, WebSocketClient> _sockets = new();
    private byte[]? _lastBroadcastBytes;
    private int _broadcastRequested;
    private int _broadcastRunning;
    private long _clientErrorWindowMinute;
    private int _clientErrorWindowCount;
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
    public bool IsCertificateTrusted => WebHttpsCertificate.IsTrustedInCurrentUserRoot(_certificate);

    public bool TryTrustCertificate(out string message)
    {
        if (_certificate is null)
        {
            message = "Start the Web HUD server first to generate the HTTPS certificate.";
            return false;
        }
        return WebHttpsCertificate.TrustInCurrentUserRoot(_certificate, out message);
    }

    public bool TryUntrustCertificate(out string message)
    {
        if (_certificate is null)
        {
            message = "No active HTTPS certificate.";
            return false;
        }
        return WebHttpsCertificate.UntrustFromCurrentUserRoot(_certificate, out message);
    }

    public event Action? ServerStatusChanged;

    public WebServerService(
        ConfigService configService,
        EliteFileWatcherService fileWatcher,
        BindingsService bindingsService,
        InputSimulatorService inputSimulator,
        AlertService alertService,
        SpanshSystemDataService spanshSystemDataService,
        AiCopilotService aiCopilot,
        GalaxyStatusService galaxyStatus,
        GalnetService galnetService,
        EliteBgsWatchlistService? eliteBgsWatchlist = null,
        ArdentMarketService? ardentMarket = null,
        EdastroCanonnService? edastroCanonn = null,
        CommunitySyncService? communitySync = null,
        AlertSpeechService? alertSpeech = null)
    {
        _configService = configService;
        _fileWatcher = fileWatcher;
        _bindingsService = bindingsService;
        _inputSimulator = inputSimulator;
        _alertService = alertService;
        _spanshSystemDataService = spanshSystemDataService;
        _aiCopilot = aiCopilot;
        _galaxyStatus = galaxyStatus;
        _galnetService = galnetService;
        _eliteBgsWatchlist = eliteBgsWatchlist;
        _ardentMarket = ardentMarket;
        _edastroCanonn = edastroCanonn;
        _communitySync = communitySync;
        _alertSpeech = alertSpeech;

        _fileWatcher.StateUpdated += OnShipStateUpdated;
        _bindingsService.BindingsUpdated += OnBindingsUpdated;
        _alertService.AlertsChanged += OnBroadcastRequested;
        _spanshSystemDataService.StateChanged += OnSpanshStateChanged;
        _configService.ConfigChanged += OnConfigChanged;
        _galaxyStatus.StatusUpdated += OnGalaxyStatusUpdated;
        _galnetService.ArticlesUpdated += OnBroadcastRequested;
        if (_eliteBgsWatchlist is not null)
            _eliteBgsWatchlist.WatchlistUpdated += OnBgsWatchlistUpdated;
        if (_edastroCanonn is not null)
            _edastroCanonn.StatusChanged += OnBroadcastRequested;
    }

    private void OnBroadcastRequested() => _ = BroadcastStateAsync();
    private void OnShipStateUpdated(ShipState state) => OnBroadcastRequested();
    private void OnBindingsUpdated(List<GameBinding> bindings) => OnBroadcastRequested();
    private void OnSpanshStateChanged(SpanshSystemDataState state) => OnBroadcastRequested();
    private void OnConfigChanged(AppConfig config) => OnBroadcastRequested();
    private void OnGalaxyStatusUpdated(GalaxyStatusSnapshot snapshot) => OnBroadcastRequested();
    private void OnBgsWatchlistUpdated(IReadOnlyList<EliteBgsFactionWatchItem> items) => OnBroadcastRequested();

    private static Task WriteJsonAsync<T>(HttpContext context, T payload, bool camelCase = false)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        string json = camelCase
            ? JsonSerializer.Serialize(payload, CamelCaseJsonOptions)
            : JsonSerializer.Serialize(payload);
        return context.Response.WriteAsync(json);
    }

    private static async Task<JsonDocument?> TryReadJsonBodyAsync(HttpContext context)
    {
        try
        {
            return await JsonDocument.ParseAsync(context.Request.Body);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsValidHexColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return false;
        string trimmed = hex.Trim();
        if (trimmed[0] != '#' || (trimmed.Length != 7 && trimmed.Length != 9))
            return false;
        for (int i = 1; i < trimmed.Length; i++)
        {
            if (!Uri.IsHexDigit(trimmed[i]))
                return false;
        }
        return true;
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;

        int port = _configService.Current.WebServerPort;
        CurrentPort = port;
        _cts = new CancellationTokenSource();

        try
        {
            var webUiHtml = new PrecompressedAsset(WebUiDocument.Load(), "text/html; charset=utf-8");
            var webUiCss = new PrecompressedAsset(WebUiDocument.LoadAsset("styles.css"), "text/css; charset=utf-8");
            var webUiJs = new PrecompressedAsset(WebUiDocument.LoadAsset("app.js"), "application/javascript; charset=utf-8");
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
                    context.Request.Path != "/manifest.json" && context.Request.Path != "/sw.js" && context.Request.Path != "/icon.svg" &&
                    access.GetExpiry(context.Request.Cookies["hudnopy-session"]) is null)
                {
                    if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/")
                        context.Response.Redirect("/login");
                    else
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                try
                {
                    await next(context);
                }
                catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
                {
                    // Log only Method + Path (never QueryString, Body, or Cookies) to protect privacy
                    AppDiagnosticsLogger.Error("WebServer.HTTP", $"Unhandled error on {context.Request.Method} {context.Request.Path}.", ex);
                    if (!context.Response.HasStarted)
                    {
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await WriteJsonAsync(context, new { ok = false, message = "Internal server error." });
                    }
                }
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
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    IsEssential = true,
                    Path = "/",
                    MaxAge = TimeSpan.FromHours(12)
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

            app.MapGet("/", context => webUiHtml.WriteAsync(context));

            app.MapGet("/styles.css", context => webUiCss.WriteAsync(context));

            app.MapGet("/app.js", context => webUiJs.WriteAsync(context));

            string webDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Web");
            app.MapGet("/manifest.json", async context =>
            {
                string path = Path.Combine(webDir, "manifest.json");
                if (!File.Exists(path)) { context.Response.StatusCode = 404; return; }
                context.Response.ContentType = "application/manifest+json; charset=utf-8";
                string text = await File.ReadAllTextAsync(path, context.RequestAborted);
                text = text.Replace("#FF7100", _configService.Current.ThemeAccentHex ?? "#FF7100", StringComparison.OrdinalIgnoreCase);
                await context.Response.WriteAsync(text, context.RequestAborted);
            });
            app.MapGet("/sw.js", async context =>
            {
                string path = Path.Combine(webDir, "sw.js");
                if (!File.Exists(path)) { context.Response.StatusCode = 404; return; }
                context.Response.ContentType = "application/javascript; charset=utf-8";
                context.Response.Headers["Service-Worker-Allowed"] = "/";
                await context.Response.SendFileAsync(path, context.RequestAborted);
            });
            app.MapGet("/icon.svg", async context =>
            {
                string path = Path.Combine(webDir, "icon.svg");
                if (!File.Exists(path)) { context.Response.StatusCode = 404; return; }
                context.Response.ContentType = "image/svg+xml; charset=utf-8";
                await context.Response.SendFileAsync(path, context.RequestAborted);
            });

            app.MapPost("/api/radio", async (HttpContext context) =>
            {
                if (CockpitRadio is null)
                {
                    await WriteJsonAsync(context, new { success = false, message = "Cockpit Radio unavailable." });
                    return;
                }
                string action = "stop";
                string stationId = "";
                int? volume = null;
                string customName = "";
                string customUrl = "";
                using (var doc = await TryReadJsonBodyAsync(context))
                {
                    if (doc is not null)
                    {
                        action = (JsonElementExtensions.GetStr(doc.RootElement, "action") ?? "stop").Trim().ToLowerInvariant();
                        stationId = (JsonElementExtensions.GetStr(doc.RootElement, "stationId") ?? "").Trim();
                        volume = JsonElementExtensions.GetInt(doc.RootElement, "volume");
                        customName = (JsonElementExtensions.GetStr(doc.RootElement, "name") ?? "").Trim();
                        customUrl = (JsonElementExtensions.GetStr(doc.RootElement, "url") ?? "").Trim();
                    }
                }
                if (action == "play")
                {
                    var (ok, msg) = await CockpitRadio.PlayAsync(stationId);
                    await BroadcastStateAsync();
                    await WriteJsonAsync(context, new { success = ok, message = msg, status = CockpitRadio.GetStatus() }, camelCase: true);
                }
                else if (action == "volume" && volume.HasValue)
                {
                    CockpitRadio.SetVolumePercent(volume.Value);
                    _configService.Save();
                    await BroadcastStateAsync();
                    await WriteJsonAsync(context, new { success = true, status = CockpitRadio.GetStatus() }, camelCase: true);
                }
                else if (action == "add" && !string.IsNullOrWhiteSpace(customName) && CockpitRadioService.IsValidStreamUrl(customUrl))
                {
                    _configService.Current.CustomRadioStations ??= new List<RadioStationItem>();
                    _configService.Current.CustomRadioStations.Add(new RadioStationItem
                    {
                        Id = $"custom_{Guid.NewGuid():N}"[..14],
                        Name = customName,
                        StreamUrl = customUrl,
                        Genre = "Custom"
                    });
                    _configService.Save();
                    await BroadcastStateAsync();
                    await WriteJsonAsync(context, new { success = true, stations = CockpitRadio.GetAllStations() }, camelCase: true);
                }
                else
                {
                    CockpitRadio.Stop();
                    await BroadcastStateAsync();
                    await WriteJsonAsync(context, new { success = true, status = CockpitRadio.GetStatus() }, camelCase: true);
                }
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

            app.MapGet("/api/spansh/nearest", async (HttpContext context) =>
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                string serviceKey = context.Request.Query["service"].ToString();
                var st = _fileWatcher.State;
                var (ok, results, msg) = await _spanshSystemDataService.SearchNearestServicesAsync(serviceKey, st.CurrentSystem, st.StarPos);
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = ok, message = msg, results }, CamelCaseJsonOptions));
            });

            app.MapGet("/api/spansh/bodies", async (HttpContext context) =>
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                string query = context.Request.Query["query"].ToString();
                var st = _fileWatcher.State;
                var (ok, results, msg) = await _spanshSystemDataService.SearchBodiesAsync(query, st.CurrentSystem, st.StarPos);
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = ok, message = msg, results }, CamelCaseJsonOptions));
            });

            app.MapGet("/api/spansh/carriers", async (HttpContext context) =>
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                string service = context.Request.Query["service"].ToString();
                var st = _fileWatcher.State;
                var (ok, results, msg) = await _spanshSystemDataService.SearchFleetCarriersAsync(service, st.CurrentSystem, st.StarPos);
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = ok, message = msg, results }, CamelCaseJsonOptions));
            });

            app.MapGet("/api/ardent/market", async (HttpContext context) =>
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                if (_ardentMarket is null)
                {
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = false, message = "Ardent Market service unavailable.", offers = Array.Empty<object>() }));
                    return;
                }
                string commodity = context.Request.Query["commodity"].ToString();
                string mode = context.Request.Query["mode"].ToString();
                var (ok, offers, msg) = await _ardentMarket.SearchCommodityAsync(commodity, string.IsNullOrWhiteSpace(mode) ? "sell" : mode);
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = ok, message = msg, offers }, CamelCaseJsonOptions));
            });

            app.MapGet("/api/dssa/nearest", async (HttpContext context) =>
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                if (_edastroCanonn is null)
                {
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = false, message = "EDAstro DSSA service unavailable.", carriers = Array.Empty<object>() }));
                    return;
                }
                var (ok, carriers, msg) = await _edastroCanonn.GetNearestDssaCarriersAsync();
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = ok, message = msg, carriers }, CamelCaseJsonOptions));
            });

            app.MapPost("/api/bgs/refresh", async (HttpContext context) =>
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                if (_eliteBgsWatchlist is null)
                {
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = false, message = "EliteBGS Watchlist unavailable." }));
                    return;
                }
                var (ok, items, msg) = await _eliteBgsWatchlist.RefreshAsync();
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = ok, message = msg, count = items.Count }));
            });

            app.MapGet("/api/edsm/intel", async (HttpContext context) =>
            {
                if (_communitySync is null || !_configService.Current.EnableEdsmSync)
                {
                    await WriteJsonAsync(context, new { success = false, message = "EDSM integration is disabled." });
                    return;
                }
                string sys = _fileWatcher.State.CurrentSystem;
                var intel = await _communitySync.FetchEdsmSystemIntelAsync(sys);
                await WriteJsonAsync(context, new { success = intel is not null, intel }, camelCase: true);
            });

            app.MapPost("/api/system-note", async (HttpContext context) =>
            {
                string note = "";
                using (var doc = await TryReadJsonBodyAsync(context))
                {
                    if (doc is not null)
                        note = JsonElementExtensions.GetStr(doc.RootElement, "note") ?? "";
                }

                string sys = _fileWatcher.State.CurrentSystem;
                if (string.IsNullOrWhiteSpace(sys) || sys == "---")
                {
                    await WriteJsonAsync(context, new { success = false, message = "No active star system." });
                    return;
                }
                if (string.IsNullOrWhiteSpace(note))
                    _configService.Current.SystemNotes.Remove(sys);
                else
                    _configService.Current.SystemNotes[sys] = note.Trim();
                _configService.Save();
                _fileWatcher.SyncCurrentSystemNote();
                if (_communitySync is not null && _configService.Current.EnableEdsmSync)
                    _ = _communitySync.SetEdsmSystemCommentAsync(sys, note.Trim());
                await WriteJsonAsync(context, new { success = true, system = sys });
            });

            app.MapGet("/api/spansh/route", async (HttpContext context) =>
            {
                string mode = context.Request.Query["mode"].ToString();
                string toSys = context.Request.Query["to"].ToString();
                double range = double.TryParse(context.Request.Query["range"].ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double r) ? r : 50;
                var st = _fileWatcher.State;
                long usedMass = st.Carrier is { Capacity: > 0, FreeSpace: >= 0 } fc ? Math.Max(0, fc.Capacity.Value - fc.FreeSpace.Value) : 0;
                var (ok, waypoints, msg) = await _spanshSystemDataService.PlotSpanshRouteAsync(string.IsNullOrWhiteSpace(mode) ? "neutron" : mode, st.CurrentSystem, toSys, range, usedMass);
                await WriteJsonAsync(context, new { success = ok, message = msg, waypoints }, camelCase: true);
            });

            app.MapGet("/api/spansh/item", async (HttpContext context) =>
            {
                string query = context.Request.Query["query"].ToString();
                bool isShip = string.Equals(context.Request.Query["isShip"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
                bool lyr = string.Equals(context.Request.Query["lyr"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
                var st = _fileWatcher.State;
                var (ok, results, msg) = await _spanshSystemDataService.SearchShipOrModuleAsync(query, isShip, lyr, st.CurrentSystem, st.StarPos);
                await WriteJsonAsync(context, new { success = ok, message = msg, results }, camelCase: true);
            });

            app.MapGet("/api/spansh/trade", async (HttpContext context) =>
            {
                var st = _fileWatcher.State;
                var (ok, hops, msg) = await _spanshSystemDataService.PlotTradeRouteAsync(st.CurrentSystem, st.StationName, st.CargoCapacity, st.Credits, 30, true);
                await WriteJsonAsync(context, new { success = ok, message = msg, hops }, camelCase: true);
            });

            app.MapGet("/api/spansh/mining", async (HttpContext context) =>
            {
                string mineral = context.Request.Query["mineral"].ToString();
                var st = _fileWatcher.State;
                var (ok, results, msg) = await _spanshSystemDataService.SearchMiningRingsAsync(string.IsNullOrWhiteSpace(mineral) ? "Platinum" : mineral, st.CurrentSystem, st.StarPos);
                await WriteJsonAsync(context, new { success = ok, message = msg, results }, camelCase: true);
            });

            app.MapGet("/api/spansh/colonisation", async (HttpContext context) =>
            {
                string commodity = context.Request.Query["commodity"].ToString();
                var st = _fileWatcher.State;
                long minSupply = 100;
                if (string.IsNullOrWhiteSpace(commodity))
                {
                    var nextRes = st.Colonisation.Resources.FirstOrDefault(static x => x.RequiredAmount > x.ProvidedAmount);
                    if (nextRes is not null)
                    {
                        commodity = nextRes.DisplayName;
                        minSupply = Math.Max(1, nextRes.RequiredAmount - nextRes.ProvidedAmount);
                    }
                }
                var (ok, results, msg) = await _spanshSystemDataService.SearchCommoditySupplierAsync(commodity, minSupply, true, st.CurrentSystem, st.StarPos);
                await WriteJsonAsync(context, new { success = ok, commodity, message = msg, results }, camelCase: true);
            });

            app.MapPost("/api/surface/bookmarks", async (HttpContext context) =>
            {
                string action = "add";
                string label = "";
                using (var doc = await TryReadJsonBodyAsync(context))
                {
                    if (doc is not null)
                    {
                        action = JsonElementExtensions.GetStr(doc.RootElement, "action") ?? "add";
                        label = (JsonElementExtensions.GetStr(doc.RootElement, "label") ?? "").Trim();
                    }
                }

                var st = _fileWatcher.State;
                _configService.Current.SurfaceBookmarks ??= new List<SurfaceBookmarkItem>();
                if (action.Equals("delete", StringComparison.OrdinalIgnoreCase))
                {
                    _configService.Current.SurfaceBookmarks.RemoveAll(b => string.Equals(b.Label, label, StringComparison.OrdinalIgnoreCase));
                    _configService.Save();
                    await WriteJsonAsync(context, new { success = true, bookmarks = _configService.Current.SurfaceBookmarks }, camelCase: true);
                    return;
                }

                if (!st.HasLatLong)
                {
                    await WriteJsonAsync(context, new { success = false, message = "No active planetary coordinates." });
                    return;
                }
                _configService.Current.SurfaceBookmarks.Add(new SurfaceBookmarkItem
                {
                    SystemName = st.CurrentSystem,
                    BodyName = st.BodyName,
                    Label = string.IsNullOrWhiteSpace(label) ? $"Point {DateTime.Now:HH:mm}" : label,
                    Latitude = Math.Round(st.Latitude, 4),
                    Longitude = Math.Round(st.Longitude, 4)
                });
                _configService.Save();
                await WriteJsonAsync(context, new { success = true, bookmarks = _configService.Current.SurfaceBookmarks }, camelCase: true);
            });

            app.MapPost("/api/ai/quick", async (HttpContext context) =>
            {
                string action = "";
                using (var doc = await TryReadJsonBodyAsync(context))
                {
                    if (doc is not null)
                        action = (JsonElementExtensions.GetStr(doc.RootElement, "action") ?? "").Trim().ToLowerInvariant();
                }

                if (action == "clear")
                {
                    _aiCopilot.ClearHistory();
                    await WriteJsonAsync(context, new { success = true, reply = "" });
                    return;
                }

                var (ok, reply) = action switch
                {
                    "preflight" => await _aiCopilot.RunPreFlightCheckAsync(),
                    "galnet" => await _aiCopilot.SummarizeGalnetAsync(),
                    "session" => await _aiCopilot.SummarizeSessionAsync(),
                    _ => await _aiCopilot.AskAsync(OperationsTranslations.ForLanguage(_configService.Current.InterfaceLanguage)["aiQuickStatusPrompt"])
                };
                await WriteJsonAsync(context, new { success = ok, reply });
            });

            app.MapPost("/api/galnet/speak", async (HttpContext context) =>
            {
                if (_alertSpeech is null)
                {
                    await WriteJsonAsync(context, new { success = false, message = "Speech service unavailable." });
                    return;
                }
                bool stop = false;
                int index = 0;
                using (var doc = await TryReadJsonBodyAsync(context))
                {
                    if (doc is not null)
                    {
                        if (doc.RootElement.TryGetProperty("stop", out var sp) && sp.ValueKind == JsonValueKind.True)
                            stop = true;
                        index = JsonElementExtensions.GetInt(doc.RootElement, "index") ?? 0;
                    }
                }

                if (stop)
                {
                    _alertSpeech.StopSpeaking();
                    await WriteJsonAsync(context, new { success = true });
                    return;
                }

                var articles = _galnetService.Articles;
                if (index >= 0 && index < articles.Count)
                {
                    var a = articles[index];
                    _alertSpeech.SpeakCustomText($"{a.Title}. {a.Body}");
                    await WriteJsonAsync(context, new { success = true });
                    return;
                }
                await WriteJsonAsync(context, new { success = false, message = "Article not found." });
            });

            app.MapPost("/api/expedition/analyze", async (HttpContext context) =>
            {
                var stats = await ExpeditionAnalyticsService.AnalyzeFolderAsync(_configService.Current.JournalFolderPath, maxFiles: 100);
                await WriteJsonAsync(context, new
                {
                    success = true,
                    stats,
                    markdown = stats.ToMarkdownReport(_fileWatcher.State.CommanderName)
                }, camelCase: true);
            });

            app.MapPost("/api/session/reset", async (HttpContext context) =>
            {
                _fileWatcher.ResetSessionStats();
                await WriteJsonAsync(context, new { success = true });
            });

            app.MapGet("/api/journal/files", async (HttpContext context) =>
            {
                string folder = _configService.Current.JournalFolderPath;
                var files = new List<object>();
                if (Directory.Exists(folder))
                {
                    try
                    {
                        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("Journal.*.log")
                            .OrderByDescending(static f => f.LastWriteTimeUtc)
                            .Take(30))
                        {
                            files.Add(new { name = file.Name, label = $"{file.Name} ({file.LastWriteTime:dd/MM HH:mm})" });
                        }
                    }
                    catch
                    {
                    }
                }
                await WriteJsonAsync(context, new { success = true, files });
            });

            app.MapGet("/api/journal/events", async (HttpContext context) =>
            {
                string fileName = context.Request.Query["file"].ToString().Trim();
                string language = _configService.Current.InterfaceLanguage;
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    var liveEvents = _fileWatcher.State.RecentEvents
                        .Select(entry => JournalTranslations.Present(entry, language))
                        .ToList();
                    await WriteJsonAsync(context, new { success = true, events = liveEvents });
                    return;
                }

                if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
                    || !fileName.StartsWith("Journal.", StringComparison.OrdinalIgnoreCase)
                    || !fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 400;
                    await WriteJsonAsync(context, new { success = false, message = "Invalid journal filename." });
                    return;
                }

                string fullPath = Path.Combine(_configService.Current.JournalFolderPath, fileName);
                if (!File.Exists(fullPath))
                {
                    context.Response.StatusCode = 404;
                    await WriteJsonAsync(context, new { success = false, message = "Journal file not found." });
                    return;
                }

                var entries = EliteFileWatcherService.LoadHistoricalJournal(fullPath)
                    .Select(entry => JournalTranslations.Present(entry, language))
                    .ToList();
                await WriteJsonAsync(context, new { success = true, events = entries });
            });

            app.MapPost("/api/combat/reset", async (HttpContext context) =>
            {
                _fileWatcher.ResetCombatSession();
                await WriteJsonAsync(context, new { success = true });
            });

            app.MapPost("/api/mining/reset", async (HttpContext context) =>
            {
                _fileWatcher.ResetMiningSession();
                await WriteJsonAsync(context, new { success = true });
            });

            app.MapPost("/api/mining/filter", async (HttpContext context) =>
            {
                using (var doc = await TryReadJsonBodyAsync(context))
                {
                    if (doc is not null)
                    {
                        string? targets = JsonElementExtensions.GetStr(doc.RootElement, "targets");
                        if (targets is not null)
                            _configService.Current.MiningTargetMinerals = targets.Trim();
                        int? minPct = JsonElementExtensions.GetInt(doc.RootElement, "minPercent");
                        if (minPct is not null)
                            _configService.Current.MiningMinPercent = Math.Clamp(minPct.Value, 1, 90);
                        _configService.Save();
                    }
                }
                await WriteJsonAsync(context, new
                {
                    success = true,
                    targets = _configService.Current.MiningTargetMinerals,
                    minPercent = _configService.Current.MiningMinPercent
                });
            });

            app.MapPost("/api/materials/wishlist/toggle", async (HttpContext context) =>
            {
                string materialId = "";
                int target = 50;
                using (var doc = await TryReadJsonBodyAsync(context))
                {
                    if (doc is not null)
                    {
                        materialId = (JsonElementExtensions.GetStr(doc.RootElement, "materialId") ?? "").Trim();
                        if (doc.RootElement.TryGetProperty("target", out var tEl) && tEl.TryGetInt32(out int parsed) && parsed > 0)
                            target = parsed;
                    }
                }
                if (string.IsNullOrWhiteSpace(materialId))
                {
                    context.Response.StatusCode = 400;
                    await WriteJsonAsync(context, new { success = false });
                    return;
                }

                var wishlist = _configService.Current.MaterialWishlist ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var item = _fileWatcher.State.MaterialsInventory.FirstOrDefault(m =>
                    string.Equals(m.Id, materialId, StringComparison.OrdinalIgnoreCase));
                if (wishlist.ContainsKey(materialId))
                {
                    wishlist.Remove(materialId);
                    if (item is not null) item.WishlistTarget = 0;
                }
                else
                {
                    int clamped = item is not null ? Math.Min(target, item.MaxCapacity) : target;
                    wishlist[materialId] = clamped;
                    if (item is not null) item.WishlistTarget = clamped;
                }
                _configService.Save();
                await BroadcastStateAsync();
                await WriteJsonAsync(context, new { success = true });
            });

            app.MapPost("/api/deck/auto-context", async (HttpContext context) =>
            {
                bool enabled = false;
                using (var doc = await TryReadJsonBodyAsync(context))
                {
                    if (doc is not null && doc.RootElement.TryGetProperty("enabled", out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        enabled = el.GetBoolean();
                }
                _configService.Current.AutoContextDeck = enabled;
                _configService.Save();
                await BroadcastStateAsync();
                await WriteJsonAsync(context, new { success = true, enabled });
            });

            app.MapPost("/api/wishlist/update", async (HttpContext context) =>
            {
                string action = "";
                string blueprintId = "";
                using (var doc = await TryReadJsonBodyAsync(context))
                {
                    if (doc is not null)
                    {
                        action = (JsonElementExtensions.GetStr(doc.RootElement, "action") ?? "").Trim().ToLowerInvariant();
                        blueprintId = (JsonElementExtensions.GetStr(doc.RootElement, "blueprintId") ?? "").Trim();
                    }
                }

                _configService.Current.EngineeringWishlistBlueprints ??= new List<string>();
                if (action == "clear")
                {
                    _configService.Current.EngineeringWishlistBlueprints.Clear();
                    _configService.Save();
                }
                else if (action == "add" && !string.IsNullOrWhiteSpace(blueprintId))
                {
                    var match = EngineeringWishlistService.Catalog.FirstOrDefault(b =>
                        string.Equals(b.Id, blueprintId, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        _configService.Current.EngineeringWishlistBlueprints.Add(match.Id);
                        _configService.Save();
                    }
                }
                var rows = EngineeringWishlistService.Evaluate(_configService.Current.EngineeringWishlistBlueprints, _fileWatcher.State);
                await WriteJsonAsync(context, new { success = true, rows }, camelCase: true);
            });

            app.MapPost("/api/ai/vision", async (HttpContext context) =>
            {
                string prompt = "";
                using (var doc = await TryReadJsonBodyAsync(context))
                {
                    if (doc is not null)
                        prompt = JsonElementExtensions.GetStr(doc.RootElement, "prompt") ?? "";
                }

                var (success, reply) = await _aiCopilot.AskWithScreenVisionAsync(prompt);
                await WriteJsonAsync(context, new
                {
                    success,
                    reply,
                    provider = _aiCopilot.GetActiveProviderSummary()
                });
            });

            app.MapPost("/api/ai/ask", async (HttpContext context) =>
            {
                string prompt = "";
                using (var doc = await TryReadJsonBodyAsync(context))
                {
                    if (doc is not null)
                        prompt = JsonElementExtensions.GetStr(doc.RootElement, "prompt") ?? "";
                }

                var (success, reply) = await _aiCopilot.AskAsync(prompt);
                await WriteJsonAsync(context, new
                {
                    success,
                    reply,
                    provider = _aiCopilot.GetActiveProviderSummary()
                });
            });

            app.MapPost("/api/galnet/refresh", async (HttpContext context) =>
            {
                var (ok, message) = await _galnetService.RefreshAsync(_configService.Current.InterfaceLanguage);
                await WriteJsonAsync(context, new { ok, message, count = _galnetService.Articles.Count });
            });

            app.MapPost("/api/galaxy-status/refresh", async (HttpContext context) =>
            {
                var snap = await _galaxyStatus.RefreshAsync();
                await WriteJsonAsync(context, new { ok = true, serverMessage = snap.ServerMessage });
            });

            app.MapPost("/api/theme", async (HttpContext context) =>
            {
                using var doc = await TryReadJsonBodyAsync(context);
                if (doc is null)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await WriteJsonAsync(context, new { ok = false, message = "Invalid JSON payload." });
                    return;
                }

                string? hex = JsonElementExtensions.GetStr(doc.RootElement, "accentHex")?.Trim();
                if (!IsValidHexColor(hex))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await WriteJsonAsync(context, new { ok = false, message = "Invalid hex color." });
                    return;
                }

                _configService.Current.ThemeAccentHex = hex!;
                _configService.Current.ThemeSecondaryHex = hex!;
                _configService.Save();
                _fileWatcher.RefreshState();
                await WriteJsonAsync(context, new { ok = true });
            });

            app.MapPost("/api/client-error", async (HttpContext context) =>
            {
                long currentMinute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
                if (Interlocked.Exchange(ref _clientErrorWindowMinute, currentMinute) != currentMinute)
                    Interlocked.Exchange(ref _clientErrorWindowCount, 0);

                if (Interlocked.Increment(ref _clientErrorWindowCount) > 20)
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return;
                }

                using var doc = await TryReadJsonBodyAsync(context);
                if (doc is null)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                string msg = (JsonElementExtensions.GetStr(doc.RootElement, "message") ?? "Unknown Web HUD error").Trim();
                string source = (JsonElementExtensions.GetStr(doc.RootElement, "source") ?? "").Trim();
                string stack = (JsonElementExtensions.GetStr(doc.RootElement, "stack") ?? "").Trim();
                if (msg.Length > 300) msg = msg[..300];
                if (source.Length > 160) source = source[..160];
                if (stack.Length > 800) stack = stack[..800];

                AppDiagnosticsLogger.Warn(
                    "WebHUD",
                    string.IsNullOrEmpty(stack)
                        ? $"{msg} ({source})"
                        : $"{msg} ({source})\n  {stack.Replace("\n", "\n  ")}");
                await WriteJsonAsync(context, new { ok = true });
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
                var client = new WebSocketClient(ws);
                _sockets[id] = client;

                try
                {
                    await SendToSocketAsync(client, BuildFullPayload());
                    var buffer = new byte[1024];
                    while (ws.State == WebSocketState.Open && !socketCts.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), socketCts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            if (ws.State == WebSocketState.CloseReceived)
                                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
                            break;
                        }
                    }
                }
                catch
                {
                    // Client disconnected
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
            AppDiagnosticsLogger.Error("WebServer", $"Failed to start Kestrel on port {port}.", ex);
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
                    await kvp.Value.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Server stopping", closeTimeout.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
                {
                    kvp.Value.Socket.Abort();
                }
            }
            _sockets.Clear();
            _lastBroadcastBytes = null;
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
            enabledIntegrations = new
            {
                frontierCapi = _configService.Current.EnableFrontierCapi,
                eddn = _configService.Current.EnableEddnUpload,
                edsm = _configService.Current.EnableEdsmSync,
                inara = _configService.Current.EnableInaraSync,
                spansh = _configService.Current.EnableSpanshSystemData,
                eliteBgsWatchlist = _configService.Current.EnableEliteBgsWatchlist,
                ardentMarketApi = _configService.Current.EnableArdentMarketApi,
                edastroDssa = _configService.Current.EnableEdastroDssa,
                canonnUpload = _configService.Current.EnableCanonnUpload,
                discord = _configService.Current.EnableDiscord
            },
            serviceHealth = ExternalServiceHealthEvaluator.Build(
                _configService.Current,
                state,
                _spanshSystemDataService,
                _communitySync,
                eliteBgs: _eliteBgsWatchlist,
                ardent: _ardentMarket,
                edastroCanonn: _edastroCanonn),
            inaraSystemUrl = _configService.Current.EnableInaraSync && !string.IsNullOrWhiteSpace(state.CurrentSystem) && state.CurrentSystem != "---"
                ? CommunitySyncService.BuildInaraDeepLink("system", state.CurrentSystem)
                : "",
            surfaceBookmarks = _configService.Current.SurfaceBookmarks ?? new List<SurfaceBookmarkItem>(),
            miningTargets = _configService.Current.MiningTargetMinerals ?? "",
            miningMinPercent = _configService.Current.MiningMinPercent,
            autoContextDeck = _configService.Current.AutoContextDeck,
            deckTabs = DeckLayoutService.BuildResolvedTabs(_configService.Current, _bindingsService, state, language, _configService.Current.HideUnassignedBindings),
            radioStatus = CockpitRadio?.GetStatus(),
            radioStations = CockpitRadio?.GetAllStations(),
            engineeringCatalog = EngineeringWishlistService.Catalog.Select(static b => new { b.Id, b.DisplayName }).ToArray(),
            engineeringWishlistBlueprints = _configService.Current.EngineeringWishlistBlueprints ?? new List<string>(),
            carrierView = GetCarrierView(state.Carrier, language),
            languageSettings = LanguageSettingsTranslations.ForLanguage(language),
            presetName = _bindingsService.UsingDefaultBindings ? DeckTranslations.ForLanguage(language)["defaultProfile"] : _bindingsService.ActivePresetName,
            alerts = _alertService.Alerts,
            activeAlerts = _alertService.ActiveAlerts,
            galaxyStatus = _galaxyStatus.Current,
            landingPad = state.AssignedLandingPad is int pad && pad > 0
                ? BuildLandingPadWebView(pad, state.AssignedLandingStation, state.AssignedLandingStationType, state.CurrentSystem, language)
                : null,
            systemMap = BuildSystemMapWebView(state.ExplorationBodies, state.CurrentSystem),
            loadoutLinks = string.IsNullOrWhiteSpace(state.LastLoadoutJson)
                ? null
                : new
                {
                    edsyUrl = ShipLoadoutExportService.BuildEdsyUrl(state.LastLoadoutJson),
                    coriolisUrl = ShipLoadoutExportService.BuildCoriolisUrl(state.LastLoadoutJson)
                },
            engineeringWishlist = EngineeringWishlistService.Evaluate(_configService.Current.EngineeringWishlistBlueprints ?? [], state),
            galnetArticles = _galnetService.Articles.Take(15).Select(static a => new
            {
                a.Title,
                Date = a.DateDisplay,
                a.Body
            }).ToArray(),
            appVersion = HttpClientHeaderDefaults.ProductVersion
        };
    }

    private static object BuildLandingPadWebView(int pad, string station, string stationType, string currentSystem, string language)
    {
        var info = LandingPadLocatorService.Resolve(pad, stationType);
        return new
        {
            padNumber = pad,
            station = string.IsNullOrWhiteSpace(station) ? currentSystem : station,
            guidance = info.FormatGuidance(language),
            bayIndicator = info.BuildBayIndicator()
        };
    }

    private static object? BuildSystemMapWebView(IReadOnlyList<ExplorationBodyItem> bodies, string currentSystem)
    {
        if (bodies is null || bodies.Count == 0)
            return null;

        var layout = SystemMapLayoutBuilder.Build(bodies, currentSystem);
        return new
        {
            width = layout.CanvasWidth,
            height = layout.CanvasHeight,
            brackets = layout.Brackets.Select(static b => new
            {
                x1 = b.X1,
                x2 = b.X2,
                topY = b.TopY,
                anchorY1 = b.AnchorY1,
                anchorY2 = b.AnchorY2
            }).ToArray(),
            nodes = layout.Nodes.Select(static n => new
            {
                x = n.X,
                y = n.Y,
                radiusPx = n.RadiusPx,
                fillHex = n.FillHex,
                strokeHex = n.StrokeHex,
                atmosphereHaloHex = n.AtmosphereHaloHex,
                isEarthLike = n.IsEarthLike,
                isStar = n.IsStar,
                isRootStar = n.IsRootStar,
                hasRings = n.HasRings,
                hasBioHalo = n.HasBioHalo,
                isHighGravity = n.IsHighGravity,
                isLandable = n.IsLandable,
                isSpanshOnly = n.IsSpanshOnly,
                shortLabel = n.ShortLabel,
                badgeText = n.BadgeText,
                parentIndex = n.ParentIndex,
                starBandIndex = n.StarBandIndex,
                indentLevel = n.IndentLevel,
                bodyName = n.Body.BodyName,
                bodyType = n.Body.BodyType,
                distanceLs = n.Body.DistanceFromArrivalLs,
                gravityG = n.Body.SurfaceGravity.HasValue ? Math.Round(n.Body.SurfaceGravity.Value / 9.80665, 2) : (double?)null,
                bioSignals = n.Body.BiologicalSignalCount,
                geoSignals = n.Body.GeologicalSignalCount,
                estimatedValue = n.Body.EstimatedCredits ?? 0,
                dataSource = n.Body.DataSource
            }).ToArray()
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

                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(BuildFullPayload());
                if (_lastBroadcastBytes is not null && bytes.AsSpan().SequenceEqual(_lastBroadcastBytes))
                    continue;
                _lastBroadcastBytes = bytes;

                var snapshot = _sockets.ToArray();
                if (snapshot.Length == 0) return;

                // Broadcast in parallel with per-socket locks and individual send timeouts so a slow client never stalls others
                var sendTasks = new Task[snapshot.Length];
                for (int i = 0; i < snapshot.Length; i++)
                {
                    var (id, client) = snapshot[i];
                    sendTasks[i] = SendBytesToClientAsync(id, client, bytes);
                }
                await Task.WhenAll(sendTasks);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _broadcastRunning, 0);
        }
    }

    private async Task SendBytesToClientAsync(Guid id, WebSocketClient client, byte[] bytes)
    {
        if (client.Socket.State != WebSocketState.Open)
        {
            _sockets.TryRemove(id, out _);
            return;
        }

        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool locked = false;
        try
        {
            await client.SendLock.WaitAsync(sendTimeout.Token);
            locked = true;
            if (client.Socket.State != WebSocketState.Open)
            {
                _sockets.TryRemove(id, out _);
                return;
            }
            await client.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, sendTimeout.Token);
        }
        catch
        {
            _sockets.TryRemove(id, out _);
        }
        finally
        {
            if (locked)
                client.SendLock.Release();
        }
    }

    private static async Task SendToSocketAsync(WebSocketClient client, object payload)
    {
        if (client.Socket.State != WebSocketState.Open) return;
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.SendLock.WaitAsync(sendTimeout.Token);
        try
        {
            if (client.Socket.State != WebSocketState.Open) return;

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            await client.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, sendTimeout.Token);
        }
        finally
        {
            client.SendLock.Release();
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
            <input id="code" name="code" type="password" inputmode="numeric" pattern="[0-9]{8}" maxlength="8" required autocomplete="one-time-code" oninput="this.value=this.value.replace(/[^0-9]/g,'')">
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
        _fileWatcher.StateUpdated -= OnShipStateUpdated;
        _bindingsService.BindingsUpdated -= OnBindingsUpdated;
        _alertService.AlertsChanged -= OnBroadcastRequested;
        _spanshSystemDataService.StateChanged -= OnSpanshStateChanged;
        _configService.ConfigChanged -= OnConfigChanged;
        _galaxyStatus.StatusUpdated -= OnGalaxyStatusUpdated;
        _galnetService.ArticlesUpdated -= OnBroadcastRequested;
        if (_eliteBgsWatchlist is not null)
            _eliteBgsWatchlist.WatchlistUpdated -= OnBgsWatchlistUpdated;
        if (_edastroCanonn is not null)
            _edastroCanonn.StatusChanged -= OnBroadcastRequested;
        try
        {
            _cts?.Cancel();
            if (_app is not null)
                Task.Run(() => _app.StopAsync()).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("WebServer", $"Dispose stop warning: {ex.Message}");
        }
    }

}
