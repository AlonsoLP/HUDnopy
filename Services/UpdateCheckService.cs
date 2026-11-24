// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/UpdateCheckService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Opt-in GitHub Releases version checker comparing latest published tag
//              against the running HUDnopy semantic version.
// -----------------------------------------------------------------------------

using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HUDnopy.Services;

/// <summary>
/// Result of checking GitHub Releases for a newer HUDnopy version.
/// </summary>
public sealed record UpdateCheckResult(
    bool Success,
    bool UpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string Message);

/// <summary>
/// Checks GitHub Releases API for newer versions of HUDnopy (strictly opt-in or manual trigger).
/// </summary>
public static class UpdateCheckService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/AlonsoLP/HUDnopy/releases/latest";
    private const string ReleasesPageUrl = "https://github.com/AlonsoLP/HUDnopy/releases/latest";

    private static readonly HttpClient SharedHttp = HttpClientHeaderDefaults.CreateHttpClient(TimeSpan.FromSeconds(10));

    /// <summary>
    /// Queries GitHub Releases for the latest published tag and compares it with the running version.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckLatestReleaseAsync(CancellationToken ct = default)
    {
        string current = HttpClientHeaderDefaults.ProductVersion;
        try
        {
            using var resp = await SharedHttp.GetAsync(LatestReleaseApiUrl, ct);
            if (!resp.IsSuccessStatusCode)
                return new(false, false, current, current, ReleasesPageUrl, $"GitHub HTTP {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            string tag = doc.RootElement.TryGetProperty("tag_name", out var tp) && tp.ValueKind == JsonValueKind.String
                ? (tp.GetString() ?? "").Trim()
                : "";
            string htmlUrl = doc.RootElement.TryGetProperty("html_url", out var up) && up.ValueKind == JsonValueKind.String
                ? (up.GetString() ?? ReleasesPageUrl)
                : ReleasesPageUrl;

            if (string.IsNullOrWhiteSpace(tag))
                return new(false, false, current, current, ReleasesPageUrl, "No release tag found.");

            bool newer = IsNewerVersion(current, tag);
            string cleanLatest = tag.TrimStart('v', 'V');
            string msg = newer
                ? $"v{cleanLatest} available (current v{current})"
                : $"Up to date (v{current})";
            return new(true, newer, current, cleanLatest, htmlUrl, msg);
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("UpdateCheck", $"Release check failed: {ex.Message}");
            return new(false, false, current, current, ReleasesPageUrl, ex.Message);
        }
    }

    /// <summary>
    /// Compares two semantic version strings (ignoring leading 'v' and pre-release suffixes).
    /// </summary>
    public static bool IsNewerVersion(string currentVersion, string latestTag)
    {
        if (!TryParseCoreVersion(currentVersion, out var cur) || !TryParseCoreVersion(latestTag, out var lat))
            return false;
        return lat > cur;
    }

    private static bool TryParseCoreVersion(string raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        string clean = raw.Trim().TrimStart('v', 'V');
        int dash = clean.IndexOf('-');
        if (dash >= 0) clean = clean[..dash];
        int plus = clean.IndexOf('+');
        if (plus >= 0) clean = clean[..plus];
        return Version.TryParse(clean, out version!);
    }
}
