// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/JsonElementExtensions.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Shared JsonElement property accessors and HTTP User-Agent
//              defaults used across telemetry and external integration services.
// -----------------------------------------------------------------------------

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HUDnopy.Services;

/// <summary>
/// Shared extension and static helpers for safely reading typed properties from <see cref="JsonElement"/> objects.
/// </summary>
internal static class JsonElementExtensions
{
    public static string? GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string? GetStr(JsonElement el, string parent, string prop) =>
        el.TryGetProperty(parent, out var p) && p.ValueKind == JsonValueKind.Object ? GetStr(p, prop) : null;

    public static long? GetLong(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n))
            return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long sn))
            return sn;
        return null;
    }

    public static int? GetInt(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n))
            return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int sn))
            return sn;
        return null;
    }

    public static double? GetDouble(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double n))
            return n;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sn))
            return sn;
        return null;
    }
}

/// <summary>
/// Centralized application version and HTTP User-Agent metadata read dynamically from <c>HUDnopy.csproj</c> assembly attributes.
/// </summary>
internal static class HttpClientHeaderDefaults
{
    public const string ProductName = "HUDnopy";

    /// <summary>
    /// Semantic version resolved from <see cref="AssemblyInformationalVersionAttribute"/> (defined in <c>HUDnopy.csproj</c>).
    /// </summary>
    public static string ProductVersion { get; } = ResolveAssemblyVersion();

    public static string Authors { get; } = ResolveAssemblyMetadata("Authors", "AlonsoLP");
    public static string Copyright { get; } = ResolveAssemblyCopyright();
    public static string License { get; } = ResolveAssemblyMetadata("License", "GNU GPL-3.0-only");
    public static string WebsiteUrl { get; } = ResolveAssemblyMetadata("WebsiteUrl", "https://alonsolp.github.io/HUDnopy/");
    public static string RepositoryUrl { get; } = ResolveAssemblyMetadata("RepositoryUrl", "https://github.com/AlonsoLP/HUDnopy");
    public static string WikiUrl => $"{RepositoryUrl.TrimEnd('/')}/wiki";
    public static string ReleasesUrl => $"{RepositoryUrl.TrimEnd('/')}/releases";
    public static string IssuesUrl => $"{RepositoryUrl.TrimEnd('/')}/issues";
    public static string FrontierCallbackUrl => $"{WebsiteUrl.TrimEnd('/')}/callback.html";

    public static ProductInfoHeaderValue UserAgent => new(ProductName, ProductVersion);

    /// <summary>
    /// Root per-user local data directory (<c>%LocalAppData%\HUDnopy</c>).
    /// </summary>
    public static string LocalDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductName);

    /// <summary>
    /// Builds a path under <see cref="LocalDataDirectory"/>.
    /// </summary>
    public static string GetLocalDataPath(params string[] segments)
    {
        if (segments is null || segments.Length == 0)
            return LocalDataDirectory;
        string[] all = new string[segments.Length + 1];
        all[0] = LocalDataDirectory;
        Array.Copy(segments, 0, all, 1, segments.Length);
        return Path.Combine(all);
    }

    /// <summary>
    /// Optional test-only handler factory used by automated tests to intercept outbound HTTP calls deterministically.
    /// </summary>
    internal static Func<HttpMessageHandler>? TestHttpHandlerFactory { get; set; }

    /// <summary>
    /// Optional time provider for <see cref="TransientHttpRetryHandler"/> backoff delays (defaults to <see cref="TimeProvider.System"/>).
    /// </summary>
    internal static TimeProvider RetryTimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Creates an <see cref="HttpClient"/> configured with the specified timeout, automatic GZip/Brotli decompression,
    /// DNS-safe connection pooling, transient idempotent GET/HEAD retries, and the unified HUDnopy User-Agent.
    /// </summary>
    public static HttpClient CreateHttpClient(TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        HttpMessageHandler innerHandler = handler
            ?? TestHttpHandlerFactory?.Invoke()
            ?? new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
        var retryHandler = new TransientHttpRetryHandler(innerHandler);
        var client = new HttpClient(retryHandler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
        return client;
    }

    /// <summary>
    /// Zero-dependency HTTP retry handler for idempotent GET/HEAD requests on transient network or 5xx/408 gateway errors.
    /// Never retries POST/PUT/DELETE requests so telemetry uploads (EDDN/Inara/Discord) cannot be duplicated.
    /// </summary>
    private sealed class TransientHttpRetryHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        private const int MaxRetries = 2;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bool isIdempotent = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
            if (!isIdempotent)
                return await base.SendAsync(request, cancellationToken);

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var response = await base.SendAsync(request, cancellationToken);
                    if (attempt >= MaxRetries || !IsTransientStatus(response.StatusCode))
                        return response;

                    TimeSpan delay = ResolveDelay(response, attempt);
                    response.Dispose();
                    await Task.Delay(delay, RetryTimeProvider, cancellationToken);
                }
                catch (HttpRequestException) when (attempt < MaxRetries && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(ComputeBackoff(attempt), RetryTimeProvider, cancellationToken);
                }
            }
        }

        private static bool IsTransientStatus(HttpStatusCode code) => code is
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

        private static TimeSpan ResolveDelay(HttpResponseMessage response, int attempt)
        {
            if (response.Headers.RetryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero && delta <= TimeSpan.FromSeconds(5))
                return delta;
            return ComputeBackoff(attempt);
        }

        private static TimeSpan ComputeBackoff(int attempt)
        {
            int baseMs = attempt == 0 ? 350 : 800;
            int jitterMs = Random.Shared.Next(0, 120);
            return TimeSpan.FromMilliseconds(baseMs + jitterMs);
        }
    }

    /// <summary>
    /// Shared <see cref="JsonSerializerOptions"/> instances to avoid per-call metadata cache allocations.
    /// </summary>
    public static JsonSerializerOptions IndentedJsonOptions { get; } = new() { WriteIndented = true };
    public static JsonSerializerOptions CaseInsensitiveJsonOptions { get; } = new() { PropertyNameCaseInsensitive = true };
    public static JsonSerializerOptions CamelCaseJsonOptions { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Strips any leading <c>CMDR </c> prefix and surrounding whitespace from a commander name.
    /// </summary>
    public static string CleanCommanderName(string? raw) =>
        (raw ?? "").Replace("CMDR ", "", StringComparison.OrdinalIgnoreCase).Trim();

    private static string ResolveAssemblyVersion()
    {
        var asm = typeof(HttpClientHeaderDefaults).Assembly;
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plusIdx = info.IndexOf('+');
            string clean = (plusIdx >= 0 ? info[..plusIdx] : info).Trim();
            if (!string.IsNullOrWhiteSpace(clean))
                return clean;
        }

        Version? v = asm.GetName().Version;
        return v is not null ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : "0.0.0";
    }

    private static string ResolveAssemblyMetadata(string key, string fallback)
    {
        var asm = typeof(HttpClientHeaderDefaults).Assembly;
        foreach (var attr in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attr.Key, key, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(attr.Value))
                return attr.Value.Trim();
        }
        return fallback;
    }

    private static string ResolveAssemblyCopyright()
    {
        var asm = typeof(HttpClientHeaderDefaults).Assembly;
        string? copy = asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
        return !string.IsNullOrWhiteSpace(copy) ? copy.Trim() : "Copyright (c) 2026 AlonsoLP";
    }
}
