// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Models/ExternalServiceHealthItem.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Live health status model and evaluator for opt-in external integrations.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using HUDnopy.Services;
using HUDnopy.Services.Integrations;

namespace HUDnopy.Models;

/// <summary>
/// Live health snapshot for a single external integration displayed in Desktop and Web LED strips.
/// </summary>
public sealed record ExternalServiceHealthItem(
    string Id,
    string Name,
    bool Enabled,
    string Status,
    string Detail);

/// <summary>
/// Evaluates live connectivity and recent operation status for all opt-in external services
/// without triggering any network requests.
/// </summary>
public static class ExternalServiceHealthEvaluator
{
    public static List<ExternalServiceHealthItem> Build(
        AppConfig cfg,
        ShipState state,
        SpanshSystemDataService? spansh = null,
        CommunitySyncService? communitySync = null,
        FrontierCapiService? frontierCapi = null,
        EliteBgsWatchlistService? eliteBgs = null,
        ArdentMarketService? ardent = null,
        EdastroCanonnService? edastroCanonn = null,
        DiscordIntegrationService? discord = null)
    {
        var items = new List<ExternalServiceHealthItem>(9);

        // 1. Frontier cAPI
        if (cfg.EnableFrontierCapi)
        {
            string st = frontierCapi?.IsConnected == true ? "ok"
                : HasError(frontierCapi?.LastStatus) ? "error" : "warn";
            string detail = !string.IsNullOrWhiteSpace(frontierCapi?.LastStatus)
                ? frontierCapi!.LastStatus
                : (frontierCapi?.IsConnected == true ? "Connected" : "Awaiting sign-in");
            items.Add(new("capi", "cAPI", true, st, detail));
        }
        else items.Add(new("capi", "cAPI", false, "off", "Disabled"));

        // 2. EDDN
        if (cfg.EnableEddnUpload)
        {
            string raw = communitySync?.LastEddnStatus ?? "";
            int count = communitySync?.EddnUploadedCount ?? 0;
            string st = HasError(raw) ? "error" : (count > 0 || raw.Contains("OK", StringComparison.OrdinalIgnoreCase) ? "ok" : "warn");
            string detail = count > 0 ? $"{count} sent" : (string.IsNullOrWhiteSpace(raw) ? "Ready (waiting events)" : raw);
            items.Add(new("eddn", "EDDN", true, st, detail));
        }
        else items.Add(new("eddn", "EDDN", false, "off", "Disabled"));

        // 3. EDSM
        if (cfg.EnableEdsmSync)
        {
            string syncSt = communitySync?.LastSyncStatus ?? "";
            bool hasIntel = state.EdsmDiscoveryIntel is not null;
            string st = HasError(syncSt) && syncSt.Contains("EDSM", StringComparison.OrdinalIgnoreCase) ? "error"
                : (hasIntel || syncSt.Contains("EDSM OK", StringComparison.OrdinalIgnoreCase) ? "ok" : "warn");
            string detail = hasIntel ? $"{state.EdsmDiscoveryIntel!.SystemName} ({state.EdsmDiscoveryIntel.KnownBodiesCount} bodies)"
                : (string.IsNullOrWhiteSpace(cfg.EdsmApiKey) ? "Read-only (no API key)" : "Ready");
            items.Add(new("edsm", "EDSM", true, st, detail));
        }
        else items.Add(new("edsm", "EDSM", false, "off", "Disabled"));

        // 4. Inara
        if (cfg.EnableInaraSync)
        {
            string syncSt = communitySync?.LastSyncStatus ?? "";
            int sent = communitySync?.InaraUploadedCount ?? 0;
            bool hasProfile = state.InaraProfile is not null;
            string st = HasError(syncSt) && syncSt.Contains("Inara", StringComparison.OrdinalIgnoreCase) ? "error"
                : (sent > 0 || hasProfile || syncSt.Contains("Inara OK", StringComparison.OrdinalIgnoreCase) ? "ok" : "warn");
            string detail = sent > 0 ? $"{sent} ev sent"
                : hasProfile ? $"CMDR {state.InaraProfile!.CommanderName}"
                : (string.IsNullOrWhiteSpace(cfg.InaraApiKey) ? "Read-only (no API key)" : "Ready");
            items.Add(new("inara", "Inara", true, st, detail));
        }
        else items.Add(new("inara", "Inara", false, "off", "Disabled"));

        // 5. Spansh
        if (cfg.EnableSpanshSystemData)
        {
            var spState = spansh?.State;
            string st = spState?.Status switch
            {
                SpanshSystemDataStatus.Ready => "ok",
                SpanshSystemDataStatus.Error => "error",
                _ => "warn"
            };
            string detail = spState?.Snapshot is { } snap
                ? $"{snap.Name} ({snap.BodyCount} bodies)"
                : (spState?.Status.ToString() ?? "Waiting system");
            items.Add(new("spansh", "Spansh", true, st, detail));
        }
        else items.Add(new("spansh", "Spansh", false, "off", "Disabled"));

        // 6. EliteBGS
        if (cfg.EnableEliteBgsWatchlist)
        {
            string raw = eliteBgs?.LastStatus ?? "";
            int cnt = state.WatchedBgsFactions.Count;
            string st = HasError(raw) ? "error" : (cnt > 0 ? "ok" : "warn");
            string detail = cnt > 0 ? $"{cnt} tracked" : (string.IsNullOrWhiteSpace(raw) ? "Waiting refresh" : raw);
            items.Add(new("bgs", "BGS", true, st, detail));
        }
        else items.Add(new("bgs", "BGS", false, "off", "Disabled"));

        // 7. Ardent Market
        if (cfg.EnableArdentMarketApi)
        {
            string raw = ardent?.LastStatus ?? "";
            string st = HasError(raw) ? "error" : (state.ArdentMarketOffers.Count > 0 ? "ok" : "warn");
            string detail = string.IsNullOrWhiteSpace(raw) ? "On-demand ready" : raw;
            items.Add(new("ardent", "Ardent", true, st, detail));
        }
        else items.Add(new("ardent", "Ardent", false, "off", "Disabled"));

        // 8. EDAstro DSSA / Canonn
        if (cfg.EnableEdastroDssa || cfg.EnableCanonnUpload)
        {
            int dssa = state.NearestDssaCarriers.Count;
            int canonn = edastroCanonn?.CanonnUploadedCount ?? 0;
            string st = (dssa > 0 || canonn > 0) ? "ok" : "warn";
            string detail = dssa > 0 ? $"DSSA: {dssa} cached" : (canonn > 0 ? $"Canonn: {canonn} sent" : "Ready");
            items.Add(new("dssa", "DSSA", true, st, detail));
        }
        else items.Add(new("dssa", "DSSA", false, "off", "Disabled"));

        // 9. Discord
        if (cfg.EnableDiscord)
        {
            string raw = discord?.LastStatus ?? "";
            bool ipcOk = discord?.IsIpcConnected == true;
            bool hasWebhook = !string.IsNullOrWhiteSpace(cfg.DiscordWebhookUrl);
            string st = HasError(raw) ? "error" : (ipcOk || hasWebhook ? "ok" : "warn");
            string detail = ipcOk ? "Rich Presence OK" : (hasWebhook ? "Webhook ready" : (string.IsNullOrWhiteSpace(raw) ? "Waiting Discord" : raw));
            items.Add(new("discord", "Discord", true, st, detail));
        }
        else items.Add(new("discord", "Discord", false, "off", "Disabled"));

        return items;
    }

    private static bool HasError(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("HTTP 4", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("HTTP 5", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("expir", StringComparison.OrdinalIgnoreCase));
}
