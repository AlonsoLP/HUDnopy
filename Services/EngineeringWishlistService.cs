// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/EngineeringWishlistService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Curated Elite Dangerous engineering blueprint catalog and live
//              material wishlist planner evaluated against ship and Odyssey inventories.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using HUDnopy.Models;

namespace HUDnopy.Services;

/// <summary>
/// Blueprint definition containing a display label, category, and required material counts.
/// </summary>
public sealed record EngineeringBlueprint(
    string Id,
    string DisplayName,
    string Category,
    IReadOnlyDictionary<string, int> Ingredients);

/// <summary>
/// Live progress row comparing required engineering materials against commander inventory.
/// </summary>
public sealed class WishlistMaterialRow
{
    public string MaterialName { get; set; } = "";
    public string Category { get; set; } = "";
    public int Required { get; set; }
    public int Owned { get; set; }
    public int Missing => Math.Max(0, Required - Owned);
    public string StatusDisplay => Missing == 0 ? "✓ Ready" : $"⚠ -{Missing}";
}

/// <summary>
/// Evaluates engineering blueprint wishlists against live ship and Odyssey inventories.
/// </summary>
public static class EngineeringWishlistService
{
    public static IReadOnlyList<EngineeringBlueprint> Catalog { get; } = new[]
    {
        new EngineeringBlueprint("fsd_range_g5", "FSD — Increased Range (G5, 5 rolls)", "Ship",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Arsenic"] = 5,
                ["Chemical Manipulators"] = 5,
                ["Datamined Wake Exceptions"] = 5,
                ["Magnetic Emitter Coil"] = 2
            }),
        new EngineeringBlueprint("fsd_mass_manager", "FSD — Mass Manager (Experimental)", "Ship",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Atypical Disrupted Wake Echoes"] = 5,
                ["Galvanising Alloys"] = 3,
                ["Eccentric Hyperspace Trajectories"] = 1
            }),
        new EngineeringBlueprint("thrusters_dirty_g5", "Thrusters — Dirty Drive Tuning (G5, 5 rolls)", "Ship",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cadmium"] = 5,
                ["Pharmaceutical Isolators"] = 5,
                ["Cracked Industrial Firmware"] = 5
            }),
        new EngineeringBlueprint("pp_armoured_g5", "Power Plant — Armoured (G5, 5 rolls)", "Ship",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Vanadium"] = 5,
                ["Military Grade Alloys"] = 5,
                ["Security Firmware Patch"] = 5
            }),
        new EngineeringBlueprint("pp_overcharged_g5", "Power Plant — Overcharged (G5, 5 rolls)", "Ship",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tellurium"] = 5,
                ["Conductive Polymers"] = 5,
                ["Modified Embedded Firmware"] = 5
            }),
        new EngineeringBlueprint("pd_charge_g5", "Power Distributor — Charge Enhanced (G5, 5 rolls)", "Ship",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Selenium"] = 5,
                ["Exquisite Focus Crystals"] = 5,
                ["Cracked Industrial Firmware"] = 5
            }),
        new EngineeringBlueprint("shield_thermal_g5", "Shield Generator — Thermal Resistant (G5, 5 rolls)", "Ship",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Imperial Shielding"] = 5,
                ["Military Supercapacitors"] = 5,
                ["Modified Consumer Firmware"] = 5
            }),
        new EngineeringBlueprint("mc_overcharged_g5", "Multi-Cannon — Overcharged (G5, 5 rolls)", "Ship",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Zirconium"] = 5,
                ["Conductive Ceramics"] = 5,
                ["Modified Embedded Firmware"] = 5
            }),
        new EngineeringBlueprint("hull_heavy_g5", "Hull Reinforcement — Heavy Duty (G5, 5 rolls)", "Ship",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tungsten"] = 5,
                ["Core Dynamics Composites"] = 5,
                ["Proprietary Composites"] = 5
            }),
        new EngineeringBlueprint("odyssey_suit_g5", "Odyssey Suit — Grade 4 → 5 Upgrade", "Odyssey",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Suit Schematic"] = 10,
                ["Health Monitor"] = 10,
                ["Manufacturing Instructions"] = 15,
                ["Carbon Fibre Plating"] = 25,
                ["Graphene"] = 25
            }),
        new EngineeringBlueprint("odyssey_weapon_g5", "Odyssey Weapon — Grade 4 → 5 Upgrade", "Odyssey",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Weapon Schematic"] = 10,
                ["Ionised Gas"] = 10,
                ["Manufacturing Instructions"] = 15,
                ["Titanium Plating"] = 25,
                ["Microelectrode"] = 25
            })
    };

    /// <summary>
    /// Aggregates all selected blueprint ingredients and matches them against live inventories.
    /// </summary>
    public static List<WishlistMaterialRow> Evaluate(IEnumerable<string> selectedBlueprintIds, ShipState state)
    {
        var requiredTotals = new Dictionary<string, (int Count, string Category)>(StringComparer.OrdinalIgnoreCase);

        foreach (string id in selectedBlueprintIds ?? Array.Empty<string>())
        {
            var bp = Catalog.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
            if (bp is null)
                continue;

            foreach (var kv in bp.Ingredients)
            {
                if (requiredTotals.TryGetValue(kv.Key, out var existing))
                    requiredTotals[kv.Key] = (existing.Count + kv.Value, existing.Category);
                else
                    requiredTotals[kv.Key] = (kv.Value, bp.Category);
            }
        }

        if (requiredTotals.Count == 0)
            return new List<WishlistMaterialRow>();

        var rows = new List<WishlistMaterialRow>(requiredTotals.Count);
        foreach (var kv in requiredTotals)
        {
            int owned = CountOwned(kv.Key, state);
            rows.Add(new WishlistMaterialRow
            {
                MaterialName = kv.Key,
                Category = kv.Value.Category,
                Required = kv.Value.Count,
                Owned = owned
            });
        }

        return rows
            .OrderByDescending(static r => r.Missing)
            .ThenBy(static r => r.MaterialName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int CountOwned(string materialName, ShipState state)
    {
        int shipOwned = state.MaterialsInventory
            .Where(m => MatchesName(m.Name, materialName))
            .Sum(static m => m.Count);

        int lockerOwned = state.ShipLockerInventory
            .Where(b => MatchesName(b.Name, materialName))
            .Sum(static b => b.Count);

        int backpackOwned = state.BackpackInventory
            .Where(b => MatchesName(b.Name, materialName))
            .Sum(static b => b.Count);

        return shipOwned + lockerOwned + backpackOwned;
    }

    private static bool MatchesName(string? candidate, string target)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        string normCand = candidate.Replace("_", " ", StringComparison.Ordinal).Trim();
        return string.Equals(normCand, target, StringComparison.OrdinalIgnoreCase);
    }
}
