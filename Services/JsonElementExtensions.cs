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

    public static long? GetLong(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;

    public static int? GetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;

    public static double? GetDouble(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double n) ? n : null;

    // C# 14 extension block for fluent instance-style access on JsonElement values
    extension(JsonElement element)
    {
        public string? ReadStr(string prop) => GetStr(element, prop);
        public long? ReadLong(string prop) => GetLong(element, prop);
        public int? ReadInt(string prop) => GetInt(element, prop);
        public double? ReadDouble(string prop) => GetDouble(element, prop);
        public bool IsNonEmptyString => element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString());
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
    /// Creates an <see cref="HttpClient"/> configured with the specified timeout, automatic GZip/Brotli decompression,
    /// DNS-safe connection pooling, and the unified HUDnopy User-Agent.
    /// </summary>
    public static HttpClient CreateHttpClient(TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        HttpMessageHandler effectiveHandler = handler
            ?? TestHttpHandlerFactory?.Invoke()
            ?? new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
        var client = new HttpClient(effectiveHandler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
        return client;
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
}
