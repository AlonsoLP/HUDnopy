// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/Integrations/ArdentMarketService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Queries the public Ardent Insight v2 open market API for live
//              nearest buy/sell commodity prices around the commander's system.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HUDnopy.Models;
using static HUDnopy.Services.JsonElementExtensions;

namespace HUDnopy.Services.Integrations;

/// <summary>
/// Queries the Ardent Insight v2 open market API for live nearest buy/sell offers.
/// </summary>
public sealed class ArdentMarketService : IDisposable
{
    private const string ArdentBaseUrl = "https://api.ardent-insight.com/v2";

    private readonly ConfigService _configService;
    private readonly EliteFileWatcherService _fileWatcher;
    private readonly HttpClient _httpClient;

    public string LastStatus { get; private set; } = "";

    public ArdentMarketService(ConfigService configService, EliteFileWatcherService fileWatcher)
    {
        _configService = configService;
        _fileWatcher = fileWatcher;

        _httpClient = HttpClientHeaderDefaults.CreateHttpClient(TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Searches nearby stations buying ("sell" = station imports) or selling ("buy" = station exports) a commodity.
    /// </summary>
    public async Task<(bool Success, List<ArdentMarketOfferItem> Offers, string Message)> SearchCommodityAsync(
        string commodityName,
        string mode = "sell",
        string? referenceSystem = null,
        bool includeCarriers = false,
        CancellationToken ct = default)
    {
        if (!_configService.Current.EnableArdentMarketApi)
        {
            LastStatus = "Ardent Insight Market API is disabled.";
            return (false, new List<ArdentMarketOfferItem>(), LastStatus);
        }

        string cleanCommodity = (commodityName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleanCommodity))
            return (false, new List<ArdentMarketOfferItem>(), "Enter a commodity name.");

        string system = string.IsNullOrWhiteSpace(referenceSystem)
            ? _fileWatcher.State.CurrentSystem
            : referenceSystem.Trim();
        if (string.IsNullOrWhiteSpace(system) || system == "---")
            system = "Sol";

        bool isSellMode = !string.Equals(mode, "buy", StringComparison.OrdinalIgnoreCase);
        // "imports" = stations buying from commander (commander sells); "exports" = stations selling to commander (commander buys)
        string direction = isSellMode ? "imports" : "exports";
        string normalizedCommodity = cleanCommodity.ToLowerInvariant().Replace(" ", "");

        string url = $"{ArdentBaseUrl}/system/name/{Uri.EscapeDataString(system)}/commodity/name/{Uri.EscapeDataString(normalizedCommodity)}/{direction}?maxDistance=150&fleetCarriers={(includeCarriers ? "1" : "0")}";

        try
        {
            using var resp = await _httpClient.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Retry with raw escaped commodity name if stripped form returned 404
                string fallbackUrl = $"{ArdentBaseUrl}/system/name/{Uri.EscapeDataString(system)}/commodity/name/{Uri.EscapeDataString(cleanCommodity)}/{direction}?maxDistance=150&fleetCarriers={(includeCarriers ? "1" : "0")}";
                using var retryResp = await _httpClient.GetAsync(fallbackUrl, ct);
                if (!retryResp.IsSuccessStatusCode)
                {
                    LastStatus = $"Ardent API HTTP {(int)retryResp.StatusCode}";
                    return (false, new List<ArdentMarketOfferItem>(), LastStatus);
                }
                string retryJson = await retryResp.Content.ReadAsStringAsync(ct);
                return FinalizeOffers(cleanCommodity, isSellMode ? "sell" : "buy", retryJson);
            }

            string json = await resp.Content.ReadAsStringAsync(ct);
            return FinalizeOffers(cleanCommodity, isSellMode ? "sell" : "buy", json);
        }
        catch (Exception ex)
        {
            LastStatus = $"Ardent API error: {ex.Message}";
            AppDiagnosticsLogger.Warn("Ardent", LastStatus);
            return (false, new List<ArdentMarketOfferItem>(), LastStatus);
        }
    }

    private (bool Success, List<ArdentMarketOfferItem> Offers, string Message) FinalizeOffers(string commodity, string mode, string json)
    {
        var offers = ParseOffers(commodity, mode, json);
        _fileWatcher.ApplyCapiEnrichment(state => state.ArdentMarketOffers = offers);
        LastStatus = $"Ardent: {offers.Count} offers ({commodity})";
        return (true, offers, LastStatus);
    }

    internal static List<ArdentMarketOfferItem> ParseOffers(string commodity, string mode, string json)
    {
        var list = new List<ArdentMarketOfferItem>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return list;

            bool isSell = string.Equals(mode, "sell", StringComparison.OrdinalIgnoreCase);

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                string systemName = GetStr(item, "systemName") ?? GetStr(item, "system") ?? "";
                string stationName = GetStr(item, "stationName") ?? GetStr(item, "station") ?? "";
                if (string.IsNullOrWhiteSpace(stationName))
                    continue;

                double dist = GetDouble(item, "distance") ?? GetDouble(item, "distanceToSystem") ?? 0;
                long price = isSell
                    ? (GetLong(item, "sellPrice") ?? GetLong(item, "price") ?? 0)
                    : (GetLong(item, "buyPrice") ?? GetLong(item, "price") ?? 0);
                long qty = isSell
                    ? (GetLong(item, "demand") ?? GetLong(item, "stock") ?? 0)
                    : (GetLong(item, "stock") ?? GetLong(item, "supply") ?? 0);
                string pad = GetStr(item, "maxLandingPadSize") ?? GetStr(item, "landingPad") ?? "—";
                string updatedRaw = GetStr(item, "updatedAt") ?? "";
                string updatedDisp = DateTimeOffset.TryParse(updatedRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
                    ? dt.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.CurrentCulture)
                    : "—";

                if (price <= 0)
                    continue;

                list.Add(new ArdentMarketOfferItem(
                    commodity,
                    mode,
                    systemName,
                    stationName,
                    Math.Round(dist, 1),
                    price,
                    qty,
                    pad,
                    updatedDisp));
            }

            return isSell
                ? list.OrderByDescending(static o => o.Price).ThenBy(static o => o.DistanceLy).Take(25).ToList()
                : list.OrderBy(static o => o.Price).ThenBy(static o => o.DistanceLy).Take(25).ToList();
        }
        catch
        {
            return list;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
