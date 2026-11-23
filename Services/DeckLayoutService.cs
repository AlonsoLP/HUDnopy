// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/DeckLayoutService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Resolves customizable Control Deck tabs, Favorites tab, live
//              telemetry button states, per-button scaling (0.5x-4.0x),
//              auto-context matching, and JSON profile import/export.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HUDnopy.Localization;
using HUDnopy.Models;

namespace HUDnopy.Services;

/// <summary>
/// Runtime-resolved Control Deck button ready for Desktop WPF and Web HUD rendering.
/// </summary>
public sealed record ResolvedDeckButton(
    string Id,
    string Label,
    string Icon,
    string ActionId,
    string FormattedBind,
    string AccentHex,
    bool IsFavorite,
    bool RequireConfirmation,
    double SizeScale,
    bool IsActive,
    bool IsAssigned);

/// <summary>
/// Runtime-resolved Control Deck tab (including the synthetic ★ FAVORITES tab).
/// </summary>
public sealed record ResolvedDeckTab(
    string Id,
    string Name,
    string Icon,
    string AutoContext,
    bool IsFavoritesTab,
    List<ResolvedDeckButton> Buttons);

/// <summary>
/// Manages customizable Control Deck layouts, default cloning from BindingsService,
/// live telemetry active-state evaluation, and JSON import/export.
/// </summary>
public static class DeckLayoutService
{
    public const string FavoritesTabId = "__favorites__";

    public static readonly double[] SizeScalePresets = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0];

    private static readonly JsonSerializerOptions ProfileJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string GetFavoritesTabLabel(string uiLanguage) => uiLanguage switch
    {
        "es-ES" => "FAVORITOS",
        "fr-FR" => "FAVORIS",
        "de-DE" => "FAVORITEN",
        "ru-RU" => "ИЗБРАННОЕ",
        "pt-BR" => "FAVORITOS",
        _ => "FAVORITES"
    };

    /// <summary>
    /// Clones the standard BindingsService categories into editable DeckTabDefinition entries.
    /// </summary>
    public static List<DeckTabDefinition> CreateDefaultTabs(BindingsService bindingsService, string uiLanguage)
    {
        var tabs = new List<DeckTabDefinition>();
        var grouped = bindingsService.Bindings
            .Select(b => (Raw: b, View: DeckTranslations.Present(b, uiLanguage)))
            .GroupBy(x => x.View.CategoryId, StringComparer.OrdinalIgnoreCase);

        int tabOrder = 0;
        foreach (var group in grouped)
        {
            string catId = group.Key;
            string catName = group.First().View.Category;
            string autoCtx = InferDefaultAutoContext(catId);
            string icon = InferDefaultTabIcon(catId);

            var buttons = new List<CustomDeckButtonItem>();
            int btnOrder = 0;
            foreach (var (raw, view) in group)
            {
                buttons.Add(new CustomDeckButtonItem
                {
                    Id = $"def_{raw.ActionId}",
                    Label = view.DisplayName,
                    Icon = string.IsNullOrWhiteSpace(raw.IconGlyph) ? "⚡" : raw.IconGlyph,
                    ActionId = raw.ActionId,
                    AccentHex = "",
                    Order = btnOrder++,
                    IsVisible = true,
                    IsFavorite = false,
                    RequireConfirmation = IsDangerousAction(raw.ActionId),
                    SizeScale = 1.0
                });
            }

            tabs.Add(new DeckTabDefinition
            {
                Id = catId,
                Name = catName,
                Icon = icon,
                AutoContext = autoCtx,
                Order = tabOrder++,
                IsVisible = true,
                Buttons = buttons
            });
        }

        return tabs;
    }

    /// <summary>
    /// Ensures AppConfig.DeckTabs is initialized from defaults (with zero favorites selected by default).
    /// </summary>
    public static List<DeckTabDefinition> EnsureEditableTabs(AppConfig cfg, BindingsService bindingsService)
    {
        if (cfg.DeckTabs is null || cfg.DeckTabs.Count == 0)
            cfg.DeckTabs = CreateDefaultTabs(bindingsService, cfg.InterfaceLanguage);
        return cfg.DeckTabs;
    }

    /// <summary>
    /// Builds the final ordered tabs for Desktop and Web HUD, prepending ★ FAVORITES when any favorite exists.
    /// </summary>
    public static List<ResolvedDeckTab> BuildResolvedTabs(
        AppConfig cfg,
        BindingsService bindingsService,
        ShipState state,
        string uiLanguage,
        bool hideUnassigned,
        string? searchQuery = null)
    {
        var sourceTabs = cfg.UseCustomDeckLayout && cfg.DeckTabs is { Count: > 0 }
            ? cfg.DeckTabs.Where(static t => t.IsVisible).OrderBy(static t => t.Order).ToList()
            : CreateDefaultTabs(bindingsService, uiLanguage);

        // If not yet using full custom layout, honor only explicit favorite flags saved in DeckTabs
        HashSet<string>? externalFavorites = null;
        if (!cfg.UseCustomDeckLayout && cfg.DeckTabs is { Count: > 0 })
        {
            externalFavorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in cfg.DeckTabs.SelectMany(static t => t.Buttons).Where(static b => b.IsFavorite))
                externalFavorites.Add(b.ActionId);
        }

        var bindingLookup = bindingsService.Bindings
            .GroupBy(static b => b.ActionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, g => DeckTranslations.Present(g.First(), uiLanguage), StringComparer.OrdinalIgnoreCase);

        string q = (searchQuery ?? "").Trim();
        var resolvedTabs = new List<ResolvedDeckTab>();
        var favorites = new List<ResolvedDeckButton>();
        var favoriteSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tab in sourceTabs)
        {
            var resolvedButtons = new List<ResolvedDeckButton>();
            foreach (var b in tab.Buttons.Where(static x => x.IsVisible && !string.IsNullOrWhiteSpace(x.ActionId)).OrderBy(static x => x.Order))
            {
                bool isMacro = b.ActionId.StartsWith("Macro:", StringComparison.OrdinalIgnoreCase) ||
                               b.ActionId.StartsWith("Seq:", StringComparison.OrdinalIgnoreCase) ||
                               b.ActionId.Contains(',', StringComparison.Ordinal);

                bool hasBinding = bindingLookup.TryGetValue(b.ActionId, out var presented);
                bool isAssigned = isMacro || (hasBinding && presented!.IsAssigned);
                if (hideUnassigned && !isAssigned)
                    continue;

                string formattedBind = isMacro ? "MACRO" : (hasBinding ? presented!.FormattedBind : "—");
                string label = string.IsNullOrWhiteSpace(b.Label)
                    ? (hasBinding ? presented!.DisplayName : b.ActionId)
                    : b.Label;

                if (!string.IsNullOrEmpty(q) &&
                    !label.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !b.ActionId.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !formattedBind.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !tab.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isFav = b.IsFavorite || (externalFavorites is not null && externalFavorites.Contains(b.ActionId));
                var rb = new ResolvedDeckButton(
                    Id: b.Id,
                    Label: label,
                    Icon: string.IsNullOrWhiteSpace(b.Icon) ? "⚡" : b.Icon,
                    ActionId: b.ActionId,
                    FormattedBind: formattedBind,
                    AccentHex: b.AccentHex ?? "",
                    IsFavorite: isFav,
                    RequireConfirmation: b.RequireConfirmation,
                    SizeScale: ClampSizeScale(b.SizeScale),
                    IsActive: EvaluateLiveActiveState(b.ActionId, state),
                    IsAssigned: isAssigned);

                resolvedButtons.Add(rb);
                if (isFav && favoriteSeen.Add(rb.ActionId + "|" + rb.Label))
                    favorites.Add(rb);
            }

            if (resolvedButtons.Count > 0 || string.IsNullOrEmpty(q))
            {
                resolvedTabs.Add(new ResolvedDeckTab(
                    Id: tab.Id,
                    Name: tab.Name,
                    Icon: string.IsNullOrWhiteSpace(tab.Icon) ? "🎛" : tab.Icon,
                    AutoContext: tab.AutoContext ?? "",
                    IsFavoritesTab: false,
                    Buttons: resolvedButtons));
            }
        }

        if (favorites.Count > 0)
        {
            resolvedTabs.Insert(0, new ResolvedDeckTab(
                Id: FavoritesTabId,
                Name: GetFavoritesTabLabel(uiLanguage),
                Icon: "★",
                AutoContext: "",
                IsFavoritesTab: true,
                Buttons: favorites));
        }

        return resolvedTabs;
    }

    public static double ClampSizeScale(double scale) =>
        double.IsFinite(scale) && scale > 0 ? Math.Clamp(Math.Round(scale, 2), 0.5, 4.0) : 1.0;

    /// <summary>
    /// Evaluates whether a button's bound ship/SRV system is currently active in live Status.json telemetry.
    /// </summary>
    public static bool EvaluateLiveActiveState(string? actionId, ShipState s)
    {
        if (string.IsNullOrWhiteSpace(actionId) || s is null)
            return false;

        return actionId.ToLowerInvariant() switch
        {
            "landinggeartoggle" => s.LandingGearDown,
            "togglecargoscoop" or "togglecargoscoop_buggy" => s.CargoScoopDeployed,
            "shipspotlighttoggle" => s.LightsOn,
            "nightvisiontoggle" => s.NightVision,
            "deployhardpointtoggle" => s.HardpointsDeployed,
            "togglebuttonupinput" => s.SilentRunning,
            "toggleflightassist" => s.FlightAssistOff,
            "autobreakbuggybutton" => s.SrvHandbrake,
            "headlightsbuggybutton" => s.SrvHighBeam,
            "togglebuggyturretbutton" => s.SrvUsingTurretView,
            "playerhudmodetoggle" => s.AnalysisMode,
            _ => false
        };
    }

    /// <summary>
    /// Resolves the best matching tab Id for the commander's live game context (on-foot, SRV, landing, combat).
    /// </summary>
    public static string? ResolveAutoContextTabId(IReadOnlyList<ResolvedDeckTab> tabs, ShipState s)
    {
        if (tabs is null || tabs.Count == 0 || s is null)
            return null;

        string targetCtx = s.OnFoot ? "onfoot"
            : s.InSRV ? "srv"
            : (s.LandingGearDown || s.Docked || s.Landed) ? "landing"
            : (s.HardpointsDeployed || s.IsInDanger) ? "combat"
            : "";

        if (string.IsNullOrEmpty(targetCtx))
            return null;

        return tabs.FirstOrDefault(t =>
            string.Equals(t.AutoContext, targetCtx, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    public static string ExportTabsJson(IReadOnlyList<DeckTabDefinition> tabs) =>
        JsonSerializer.Serialize(tabs, ProfileJsonOptions);

    public static bool TryImportTabsJson(string json, out List<DeckTabDefinition> tabs, out string error)
    {
        tabs = new List<DeckTabDefinition>();
        error = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Empty deck profile JSON.";
            return false;
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<List<DeckTabDefinition>>(json, ProfileJsonOptions);
            if (parsed is null || parsed.Count == 0)
            {
                error = "No valid deck tabs found in JSON file.";
                return false;
            }
            for (int i = 0; i < parsed.Count; i++)
            {
                var t = parsed[i];
                t.Id = string.IsNullOrWhiteSpace(t.Id) ? Guid.NewGuid().ToString("N")[..8] : t.Id.Trim();
                t.Name = string.IsNullOrWhiteSpace(t.Name) ? $"Tab {i + 1}" : t.Name.Trim();
                t.Order = i;
                t.Buttons ??= new List<CustomDeckButtonItem>();
                for (int j = 0; j < t.Buttons.Count; j++)
                {
                    var b = t.Buttons[j];
                    b.Order = j;
                    b.SizeScale = ClampSizeScale(b.SizeScale);
                }
            }
            tabs = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Invalid deck JSON: {ex.Message}";
            return false;
        }
    }

    private static string InferDefaultAutoContext(string catId)
    {
        string c = (catId ?? "").ToLowerInvariant();
        if (c.Contains("pie") || c.Contains("foot") || c.Contains("odyssey")) return "onfoot";
        if (c.Contains("srv") || c.Contains("buggy") || c.Contains("vehic")) return "srv";
        if (c.Contains("combat") || c.Contains("arma") || c.Contains("weapon")) return "combat";
        return "";
    }

    private static string InferDefaultTabIcon(string catId)
    {
        string c = (catId ?? "").ToLowerInvariant();
        if (c.Contains("energ") || c.Contains("pip")) return "⚡";
        if (c.Contains("combat") || c.Contains("arma")) return "⚔";
        if (c.Contains("srv")) return "🚙";
        if (c.Contains("pie") || c.Contains("foot")) return "🥾";
        if (c.Contains("macro")) return "🤖";
        if (c.Contains("nav") || c.Contains("vuelo") || c.Contains("flight")) return "🚀";
        return "🎛";
    }

    private static bool IsDangerousAction(string actionId) =>
        actionId.Equals("EjectAllCargo", StringComparison.OrdinalIgnoreCase) ||
        actionId.Equals("ToggleButtonUpInput", StringComparison.OrdinalIgnoreCase);
}
