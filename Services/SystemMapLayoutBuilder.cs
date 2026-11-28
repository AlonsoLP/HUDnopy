// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/SystemMapLayoutBuilder.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Computes Elite Dangerous-style 2D orbital tree layout using Journal
//              Parents hierarchy (left-side barycentre tree bus, circumbinary X
//              barycentres positioned immediately after the inner binary star pair
//              A+B, stellar asteroid belts, planets & orbiting brown dwarfs on main
//              horizontal axis, exclusive binary pair brackets, vertical moons,
//              left-side moon binary brackets, left-shifted sub-moons, and station
//              structural icons) shared between Desktop WPF and Web HUD.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HUDnopy.Models;

namespace HUDnopy.Services;

/// <summary>
/// Positioned 2D node representing a celestial body, circumbinary barycentre (×),
/// or stellar asteroid belt in the interactive System Map.
/// </summary>
public sealed record SystemMapNode(
    ExplorationBodyItem Body,
    double X,
    double Y,
    double RadiusPx,
    string FillHex,
    string StrokeHex,
    string? AtmosphereHaloHex,
    bool IsEarthLike,
    bool IsStar,
    bool IsRootStar,
    bool HasRings,
    bool HasBioHalo,
    bool IsHighGravity,
    bool IsLandable,
    bool IsSpanshOnly,
    string ShortLabel,
    int ParentIndex,
    int StarBandIndex,
    int IndentLevel,
    bool IsBarycentre = false,
    bool IsAsteroidBelt = false,
    int StationCount = 0,
    string? StationsSummary = null);

/// <summary>
/// Top orbital bracket connecting an exclusive binary pair of horizontal bodies sharing a Null barycentre.
/// </summary>
public sealed record SystemMapBinaryBracket(
    double X1,
    double X2,
    double TopY,
    double AnchorY1,
    double AnchorY2);

/// <summary>
/// Left-side vertical bracket connecting an exclusive binary pair of moons sharing a Null barycentre.
/// </summary>
public sealed record SystemMapMoonBracket(
    double LeftX,
    double AnchorX1,
    double Y1,
    double AnchorX2,
    double Y2);

/// <summary>
/// Orthogonal line segment in the left-hand stellar barycentre tree bus.
/// </summary>
public sealed record SystemMapSpineLine(
    double X1,
    double Y1,
    double X2,
    double Y2);

/// <summary>
/// Complete 2D schematic layout for the interactive System Map.
/// </summary>
public sealed record SystemMapLayoutResult(
    List<SystemMapNode> Nodes,
    List<SystemMapBinaryBracket> Brackets,
    double CanvasWidth,
    double CanvasHeight,
    List<SystemMapSpineLine>? SpineLines = null,
    List<SystemMapMoonBracket>? MoonBrackets = null)
{
    public IReadOnlyList<SystemMapSpineLine> SafeSpineLines =>
        (IReadOnlyList<SystemMapSpineLine>?)SpineLines ?? Array.Empty<SystemMapSpineLine>();

    public IReadOnlyList<SystemMapMoonBracket> SafeMoonBrackets =>
        (IReadOnlyList<SystemMapMoonBracket>?)MoonBrackets ?? Array.Empty<SystemMapMoonBracket>();
}

/// <summary>
/// Transforms unified ShipState.ExplorationBodies into an Elite Dangerous-style 2D orbital diagram:
/// root stars grouped with their inner binary partners at the top-left, circumbinary barycentres (×)
/// placed immediately below the inner binary pair (before outer companion stars like C/D), stellar
/// asteroid belts, planets &amp; orbiting stars horizontally to their right (with top brackets only
/// for exclusive 2-body binary pairs), moons hanging vertically below their direct parent (with
/// left-side brackets for binary moon pairs), sub-moons shifted to the left with a side bracket,
/// and orbital/surface station structural badges.
/// </summary>
public static partial class SystemMapLayoutBuilder
{
    private const double StarColumnX = 52;
    private const double FirstPlanetX = 122;
    private const double PlanetStepX = 52;
    private const double BinaryPairStepX = 44;
    private const double MoonStepY = 38;
    private const double SubMoonStepX = 28;
    private const double MinBandHeight = 86;
    private const double CompactStarOnlyBandHeight = 68;

    [GeneratedRegex(@"^([A-Z]+)(?:\s+|$)", RegexOptions.CultureInvariant)]
    private static partial Regex StarLetterPrefixRegex();

    [GeneratedRegex(@"^[A-Z]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PureStarLettersRegex();

    private sealed class TreeEntry
    {
        public ExplorationBodyItem Body { get; init; } = null!;
        public string RelativeName { get; init; } = "";
        public bool IsStar { get; init; }
        public bool IsRootStar { get; set; }
        public List<TreeEntry> Children { get; } = new();
    }

    private sealed class LeftSpineBand
    {
        public string Key { get; init; } = "";
        public TreeEntry? RootStar { get; init; }
        public bool IsBarycentre { get; init; }
        public int? BarycentreId { get; set; }
        public List<TreeEntry> Horizontals { get; } = new();
        public int LeftNodeIndex { get; set; } = -1;
        public double CenterY { get; set; }
        public double AnchorLeftX { get; set; }
    }

    public static SystemMapLayoutResult Build(
        IReadOnlyList<ExplorationBodyItem> bodies,
        string? systemName = null)
    {
        var nodes = new List<SystemMapNode>();
        var brackets = new List<SystemMapBinaryBracket>();
        var spineLines = new List<SystemMapSpineLine>();
        var moonBrackets = new List<SystemMapMoonBracket>();
        if (bodies is null || bodies.Count == 0)
            return new SystemMapLayoutResult(nodes, brackets, 420, 150, spineLines, moonBrackets);

        string sysPrefix = string.IsNullOrWhiteSpace(systemName)
            ? (bodies[0].SystemName ?? "")
            : systemName;

        var entries = bodies
            .Take(80)
            .OrderBy(static b => b.BodyId ?? int.MaxValue)
            .ThenBy(static b => b.BodyName, StringComparer.OrdinalIgnoreCase)
            .Select(b => new TreeEntry
            {
                Body = b,
                RelativeName = StripSystemPrefix(b.BodyName, sysPrefix),
                IsStar = IsStarBody(b)
            })
            .ToList();

        var byBodyId = new Dictionary<int, TreeEntry>();
        var byRelName = new Dictionary<string, TreeEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (e.Body.BodyId.HasValue && !byBodyId.ContainsKey(e.Body.BodyId.Value))
                byBodyId[e.Body.BodyId.Value] = e;
            if (!string.IsNullOrEmpty(e.RelativeName) && !byRelName.ContainsKey(e.RelativeName))
                byRelName[e.RelativeName] = e;
        }

        // Step 1: Identify Root Stars (left vertical column).
        // Numbered orbiting stars/brown dwarfs (e.g., "Exphiay 7" or "69 Upsilon Tauri ABC 1") are NOT root stars;
        // they sit on their host star's / barycentre's main horizontal axis just like in Elite Dangerous!
        var rootStars = new List<TreeEntry>();
        foreach (var e in entries)
        {
            if (!e.IsStar)
                continue;

            bool hasParentStarOrPlanet =
                e.Body.ParentPlanetBodyId.HasValue ||
                (e.Body.HostStarBodyId.HasValue && e.Body.HostStarBodyId != e.Body.BodyId);

            bool looksLikeNumberedOrbitingStar =
                !string.IsNullOrEmpty(e.RelativeName) &&
                !PureStarLettersRegex().IsMatch(e.RelativeName) &&
                char.IsDigit(e.RelativeName[^1]);

            if (!hasParentStarOrPlanet && !looksLikeNumberedOrbitingStar)
            {
                e.IsRootStar = true;
                rootStars.Add(e);
            }
        }

        // Fallback: if no root star matched (e.g. only planets scanned), use first star or create a virtual band
        if (rootStars.Count == 0 && entries.Any(static e => e.IsStar))
        {
            var firstStar = entries.First(static e => e.IsStar);
            firstStar.IsRootStar = true;
            rootStars.Add(firstStar);
        }

        var bands = new List<LeftSpineBand>();
        var rootStarBandByKey = new Dictionary<string, LeftSpineBand>(StringComparer.OrdinalIgnoreCase);
        var rootStarBandById = new Dictionary<int, LeftSpineBand>();

        if (rootStars.Count == 0)
        {
            bands.Add(new LeftSpineBand { Key = "A" });
        }
        else
        {
            for (int i = 0; i < rootStars.Count; i++)
            {
                var rs = rootStars[i];
                string key = ExtractStarKey(rs.RelativeName);
                if (string.IsNullOrEmpty(key))
                    key = ((char)('A' + Math.Min(i, 25))).ToString();

                var band = new LeftSpineBand
                {
                    Key = key,
                    RootStar = rs,
                    IsBarycentre = false,
                    BarycentreId = GetFirstNullBarycentreId(rs.Body)
                };
                bands.Add(band);
                if (!rootStarBandByKey.ContainsKey(key))
                    rootStarBandByKey[key] = band;
                if (rs.Body.BodyId.HasValue)
                    rootStarBandById[rs.Body.BodyId.Value] = band;
            }
        }

        // Map shared Null Barycentre IDs across multiple root stars (e.g., Null:1 shared by Star A and Star B -> "AB",
        // Null:0 shared by A, B, C -> "ABC")
        var nullIdToStarKeys = new Dictionary<int, List<string>>();
        foreach (var b in bands)
        {
            if (b.RootStar is null)
                continue;
            foreach (int nullId in GetNullBarycentreIds(b.RootStar.Body))
            {
                if (!nullIdToStarKeys.TryGetValue(nullId, out var keys))
                {
                    keys = new List<string>();
                    nullIdToStarKeys[nullId] = keys;
                }
                if (!keys.Contains(b.Key, StringComparer.OrdinalIgnoreCase))
                    keys.Add(b.Key);
            }
        }

        var barycentreBandByKey = new Dictionary<string, LeftSpineBand>(StringComparer.OrdinalIgnoreCase);

        LeftSpineBand GetOrCreateBarycentreBand(string baryKey, int? baryId)
        {
            if (barycentreBandByKey.TryGetValue(baryKey, out var existingBand))
            {
                existingBand.BarycentreId ??= baryId;
                return existingBand;
            }

            var newBand = new LeftSpineBand
            {
                Key = baryKey,
                IsBarycentre = true,
                BarycentreId = baryId
            };
            barycentreBandByKey[baryKey] = newBand;

            // Placement rule matching Elite Dangerous (e.g., Carener and 69 Upsilon Tauri):
            // 1. If the constituent stars start with an inner binary pair (e.g., Stars A and B sharing a direct
            //    inner Null barycentre like Null:1, while Star C only joins at outer Null:0), Elite Dangerous
            //    places Stars A and B together at the top-left, then places the circumbinary × row immediately
            //    BELOW Star B (the 2nd star of the inner binary pair), and places Star C below the circumbinary block!
            // 2. Otherwise, place the × row after the last constituent star of the binary pair.
            int anchorIdx = -1;
            if (baryKey.Length >= 2)
            {
                string firstKey = baryKey[0].ToString();
                string secondKey = baryKey[1].ToString();
                int idx1 = bands.FindIndex(b => !b.IsBarycentre && string.Equals(b.Key, firstKey, StringComparison.OrdinalIgnoreCase));
                int idx2 = bands.FindIndex(b => !b.IsBarycentre && string.Equals(b.Key, secondKey, StringComparison.OrdinalIgnoreCase));
                if (idx1 >= 0 && idx2 > idx1)
                {
                    var b1 = bands[idx1];
                    var b2 = bands[idx2];
                    if (b1.BarycentreId.HasValue && b1.BarycentreId == b2.BarycentreId)
                    {
                        anchorIdx = idx2;
                    }
                }
            }

            if (anchorIdx < 0)
            {
                int maxCheck = Math.Min(2, baryKey.Length);
                for (int k = maxCheck - 1; k >= 0; k--)
                {
                    string starLetter = baryKey[k].ToString();
                    int idx = bands.FindIndex(b => !b.IsBarycentre && string.Equals(b.Key, starLetter, StringComparison.OrdinalIgnoreCase));
                    if (idx > anchorIdx)
                        anchorIdx = idx;
                }
            }

            if (anchorIdx >= 0)
            {
                int pos = anchorIdx + 1;
                while (pos < bands.Count && bands[pos].IsBarycentre)
                    pos++;
                bands.Insert(pos, newBand);
            }
            else
            {
                bands.Add(newBand);
            }

            return newBand;
        }

        // Step 2: Attach every non-root body to either a direct parent body (moon/sub-moon)
        // or a LeftSpineBand (Root Star band or circumbinary × Barycentre band).
        foreach (var e in entries)
        {
            if (e.IsRootStar)
                continue;

            TreeEntry? directParent = ResolveDirectNonRootParent(e, entries, byBodyId, byRelName);
            if (directParent is not null && !directParent.IsRootStar && !ReferenceEquals(directParent, e))
            {
                directParent.Children.Add(e);
                continue;
            }

            // Top-level horizontal body: check if it orbits a multi-star barycentre (e.g. "69 Upsilon Tauri ABC 1")
            LeftSpineBand targetBand = ResolveTargetLeftSpineBand(
                e,
                bands,
                rootStarBandById,
                rootStarBandByKey,
                nullIdToStarKeys,
                GetOrCreateBarycentreBand);

            targetBand.Horizontals.Add(e);
        }

        // Step 3: Lay out each left-column band (Root Stars and circumbinary × Barycentres)
        double currentBandY = 54;
        double maxX = 420;
        double maxY = 150;

        for (int bIdx = 0; bIdx < bands.Count; bIdx++)
        {
            var band = bands[bIdx];
            band.CenterY = currentBandY;
            int leftNodeIdx = -1;

            if (band.IsBarycentre)
            {
                leftNodeIdx = nodes.Count;
                band.LeftNodeIndex = leftNodeIdx;
                band.AnchorLeftX = StarColumnX - 7;
                var syntheticBaryBody = new ExplorationBodyItem
                {
                    Id = $"bary:{band.Key}",
                    SystemName = sysPrefix,
                    BodyName = $"{sysPrefix} {band.Key} Barycentre".Trim(),
                    PlanetClass = "Barycentre",
                    DataSource = "Local"
                };
                nodes.Add(new SystemMapNode(
                    Body: syntheticBaryBody,
                    X: StarColumnX,
                    Y: currentBandY,
                    RadiusPx: 7,
                    FillHex: "#0F172A",
                    StrokeHex: "#E2E8F0",
                    AtmosphereHaloHex: null,
                    IsEarthLike: false,
                    IsStar: false,
                    IsRootStar: false,
                    HasRings: false,
                    HasBioHalo: false,
                    IsHighGravity: false,
                    IsLandable: false,
                    IsSpanshOnly: false,
                    ShortLabel: band.Key,
                    ParentIndex: -1,
                    StarBandIndex: bIdx,
                    IndentLevel: 0,
                    IsBarycentre: true,
                    IsAsteroidBelt: false));
            }
            else if (band.RootStar is not null)
            {
                var rs = band.RootStar;
                leftNodeIdx = nodes.Count;
                band.LeftNodeIndex = leftNodeIdx;
                var starNode = CreateNode(rs.Body, StarColumnX, currentBandY, isStar: true, isRootStar: true, isMoon: false,
                    sysPrefix, parentIdx: -1, starBandIdx: bIdx, indentLevel: 0);
                band.AnchorLeftX = StarColumnX - starNode.RadiusPx - 3;
                nodes.Add(starNode);
            }

            double colX = FirstPlanetX;
            double bandMaxBottom = currentBandY + 38;
            bool hasBeltOrHorizontals = false;

            // Step 3a: If this root star has one or more Stellar Asteroid Belts (in body.Rings with "Belt"),
            // render an Asteroid Belt node immediately to the right of the star before its first planet!
            if (band.RootStar is not null)
            {
                var beltRing = GetFirstAsteroidBeltRing(band.RootStar.Body);
                if (beltRing is not null)
                {
                    hasBeltOrHorizontals = true;
                    string beltName = !string.IsNullOrWhiteSpace(beltRing.Name)
                        ? beltRing.Name
                        : $"{band.RootStar.Body.BodyName} Belt".Trim();
                    string shortBeltLabel = StripSystemPrefix(beltName, sysPrefix);
                    if (string.IsNullOrWhiteSpace(shortBeltLabel))
                        shortBeltLabel = "Belt";
                    if (shortBeltLabel.Length > 10)
                        shortBeltLabel = shortBeltLabel[..10];

                    var syntheticBeltBody = new ExplorationBodyItem
                    {
                        Id = $"belt:{band.RootStar.Body.Id}:{beltName}",
                        SystemName = sysPrefix,
                        BodyName = beltName,
                        PlanetClass = string.IsNullOrWhiteSpace(beltRing.RingClass) ? "Asteroid Belt" : beltRing.RingClass,
                        DataSource = band.RootStar.Body.DataSource
                    };

                    nodes.Add(new SystemMapNode(
                        Body: syntheticBeltBody,
                        X: colX,
                        Y: currentBandY,
                        RadiusPx: 11,
                        FillHex: "#94A3B8",
                        StrokeHex: "#CBD5E1",
                        AtmosphereHaloHex: null,
                        IsEarthLike: false,
                        IsStar: false,
                        IsRootStar: false,
                        HasRings: false,
                        HasBioHalo: false,
                        IsHighGravity: false,
                        IsLandable: false,
                        IsSpanshOnly: band.RootStar.Body.IsExternalOnly,
                        ShortLabel: shortBeltLabel,
                        ParentIndex: leftNodeIdx,
                        StarBandIndex: bIdx,
                        IndentLevel: 0,
                        IsBarycentre: false,
                        IsAsteroidBelt: true));

                    colX += PlanetStepX;
                }
            }

            var horiz = band.Horizontals;
            if (horiz.Count > 0)
                hasBeltOrHorizontals = true;

            // Precompute how many horizontal bodies in this band share each direct Null BarycentreId.
            // A top binary bracket (┌───┐) is drawn ONLY when two adjacent bodies share an EXCLUSIVE
            // local binary Null barycentre (count == 2 and NOT the band's own stellar barycentre!).
            var horizNullCounts = new Dictionary<int, int>();
            foreach (var h in horiz)
            {
                if (h.Body.BarycentreId.HasValue)
                {
                    int bid = h.Body.BarycentreId.Value;
                    horizNullCounts[bid] = horizNullCounts.GetValueOrDefault(bid) + 1;
                }
            }

            bool IsExclusiveHorizontalBinaryPair(int idxA, int idxB)
            {
                if (idxA < 0 || idxB >= horiz.Count)
                    return false;
                var a = horiz[idxA].Body;
                var b = horiz[idxB].Body;
                if (!a.BarycentreId.HasValue || a.BarycentreId != b.BarycentreId)
                    return false;
                int sharedNull = a.BarycentreId.Value;
                if (band.BarycentreId.HasValue && sharedNull == band.BarycentreId.Value)
                    return false;
                if (nullIdToStarKeys.ContainsKey(sharedNull))
                    return false;
                return horizNullCounts.GetValueOrDefault(sharedNull) == 2;
            }

            for (int hIdx = 0; hIdx < horiz.Count; hIdx++)
            {
                var p = horiz[hIdx];
                bool isBinaryPartnerOfPrev = hIdx > 0 && IsExclusiveHorizontalBinaryPair(hIdx - 1, hIdx);

                int planetNodeIdx = nodes.Count;
                var curNode = CreateNode(p.Body, colX, currentBandY, isStar: p.IsStar, isRootStar: false, isMoon: false,
                    sysPrefix, parentIdx: leftNodeIdx, starBandIdx: bIdx, indentLevel: 0);
                nodes.Add(curNode);

                if (isBinaryPartnerOfPrev)
                {
                    var prevNode = nodes.LastOrDefault(n => ReferenceEquals(n.Body, horiz[hIdx - 1].Body));
                    if (prevNode is not null)
                    {
                        double curR = curNode.RadiusPx;
                        double topY = Math.Max(4.0, currentBandY - Math.Max(prevNode.RadiusPx, curR) - 13.0);
                        brackets.Add(new SystemMapBinaryBracket(
                            X1: prevNode.X,
                            X2: colX,
                            TopY: topY,
                            AnchorY1: prevNode.Y - prevNode.RadiusPx - 2,
                            AnchorY2: currentBandY - curR - 2));
                    }
                }

                // Lay out moons (indent 1) and sub-moons (indent 2+, shifted left) vertically underneath this body
                double subY = currentBandY;
                double maxSubRightX = colX;
                LayoutChildrenRecursive(p, planetNodeIdx, colX, ref subY, ref maxSubRightX, bIdx, indentLevel: 1, sysPrefix, nodes, moonBrackets);

                if (subY + 42 > bandMaxBottom)
                    bandMaxBottom = subY + 42;

                double rightmostX = Math.Max(colX, maxSubRightX);
                if (rightmostX + 70 > maxX)
                    maxX = rightmostX + 70;

                // Advance X: binary pairs sit slightly closer unless the first member has wide sub-columns
                bool nextIsBinaryPartner = hIdx + 1 < horiz.Count && IsExclusiveHorizontalBinaryPair(hIdx, hIdx + 1);

                double step = nextIsBinaryPartner && maxSubRightX <= colX
                    ? BinaryPairStepX
                    : PlanetStepX;
                colX = rightmostX + step;
            }

            if (bandMaxBottom > maxY)
                maxY = bandMaxBottom;

            double minH = hasBeltOrHorizontals ? MinBandHeight : CompactStarOnlyBandHeight;
            double bandHeight = Math.Max(minH, (bandMaxBottom - currentBandY) + 42);
            currentBandY += bandHeight;
        }

        // Step 4: Build the left-hand orthogonal barycentre tree bus (connecting root stars & circumbinary barycentres)
        BuildLeftBarycentreSpineLines(bands, spineLines);

        return new SystemMapLayoutResult(nodes, brackets, Math.Max(420, maxX), Math.Max(150, maxY), spineLines, moonBrackets);
    }

    private static void BuildLeftBarycentreSpineLines(
        List<LeftSpineBand> bands,
        List<SystemMapSpineLine> spineLines)
    {
        var activeBands = bands.Where(static b => b.LeftNodeIndex >= 0).ToList();
        if (activeBands.Count < 2)
            return;

        const double innerBusX = 18.0;
        const double outerBusX = 6.0;

        var connectedBands = new HashSet<LeftSpineBand>();

        // 1. Identify if the top two root stars form a closed inner binary pair (e.g. A & B sharing Null:1)
        var starBands = activeBands.Where(static b => !b.IsBarycentre).ToList();
        LeftSpineBand? innerPairA = null;
        LeftSpineBand? innerPairB = null;
        if (starBands.Count >= 2 &&
            starBands[0].BarycentreId.HasValue &&
            starBands[0].BarycentreId == starBands[1].BarycentreId)
        {
            innerPairA = starBands[0];
            innerPairB = starBands[1];
            spineLines.Add(new SystemMapSpineLine(innerBusX, innerPairA.CenterY, innerPairA.AnchorLeftX, innerPairA.CenterY));
            spineLines.Add(new SystemMapSpineLine(innerBusX, innerPairB.CenterY, innerPairB.AnchorLeftX, innerPairB.CenterY));
            spineLines.Add(new SystemMapSpineLine(innerBusX, innerPairA.CenterY, innerBusX, innerPairB.CenterY));
            connectedBands.Add(innerPairA);
            connectedBands.Add(innerPairB);
        }

        // 2. Wire circumbinary × barycentre bands:
        //    - If the × barycentre belongs specifically to the inner pair ("AB"), drop vertically from innerPairB to the × node on innerBusX.
        //    - If the × barycentre is an outer circumbinary node ("ABC"), connect it directly to outerBusX!
        var outerUnits = new List<(double TapX, double TapY)>();
        if (innerPairA is not null && innerPairB is not null)
        {
            double innerMidY = (innerPairA.CenterY + innerPairB.CenterY) / 2.0;
            outerUnits.Add((innerBusX, innerMidY));
        }

        foreach (var b in activeBands)
        {
            if (ReferenceEquals(b, innerPairA) || ReferenceEquals(b, innerPairB))
                continue;

            if (b.IsBarycentre &&
                innerPairA is not null &&
                innerPairB is not null &&
                b.Key.Length == 2 &&
                b.Key.Contains(innerPairA.Key, StringComparison.OrdinalIgnoreCase) &&
                b.Key.Contains(innerPairB.Key, StringComparison.OrdinalIgnoreCase))
            {
                // "AB" barycentre right below Star B: connect directly to innerBusX
                double topY = Math.Min(innerPairB.CenterY, b.CenterY);
                double botY = Math.Max(innerPairB.CenterY, b.CenterY);
                spineLines.Add(new SystemMapSpineLine(innerBusX, topY, innerBusX, botY));
                spineLines.Add(new SystemMapSpineLine(innerBusX, b.CenterY, b.AnchorLeftX, b.CenterY));
                connectedBands.Add(b);
                continue;
            }

            outerUnits.Add((b.AnchorLeftX, b.CenterY));
        }

        if (outerUnits.Count >= 2)
        {
            double busX = (innerPairA is not null) ? outerBusX : innerBusX;
            double minY = outerUnits.Min(static u => u.TapY);
            double maxY = outerUnits.Max(static u => u.TapY);
            spineLines.Add(new SystemMapSpineLine(busX, minY, busX, maxY));
            foreach (var u in outerUnits)
            {
                spineLines.Add(new SystemMapSpineLine(busX, u.TapY, u.TapX, u.TapY));
            }
        }
    }

    private static LeftSpineBand ResolveTargetLeftSpineBand(
        TreeEntry entry,
        List<LeftSpineBand> bands,
        Dictionary<int, LeftSpineBand> rootStarBandById,
        Dictionary<string, LeftSpineBand> rootStarBandByKey,
        Dictionary<int, List<string>> nullIdToStarKeys,
        Func<string, int?, LeftSpineBand> getOrCreateBarycentreBand)
    {
        string starKey = ExtractStarKey(entry.RelativeName);

        // 1. Circumbinary / multi-star prefix in body name (e.g., "69 Upsilon Tauri ABC 1" -> starKey = "ABC")
        if (starKey.Length > 1 && !rootStarBandByKey.ContainsKey(starKey))
        {
            int? sharedBaryId = null;
            foreach (var kv in nullIdToStarKeys)
            {
                if (string.Equals(string.Concat(kv.Value), starKey, StringComparison.OrdinalIgnoreCase))
                {
                    sharedBaryId = kv.Key;
                    break;
                }
            }
            return getOrCreateBarycentreBand(starKey, sharedBaryId ?? entry.Body.BarycentreId);
        }

        // 2. Circumbinary body identified via Journal Parents: no HostStarBodyId, and either its direct
        //    Null BarycentreId or an ancestor Null in Parents is shared by 2+ root stars!
        if (!entry.Body.HostStarBodyId.HasValue)
        {
            foreach (int nullId in GetNullBarycentreIds(entry.Body))
            {
                if (nullIdToStarKeys.TryGetValue(nullId, out var sharedStarKeys) && sharedStarKeys.Count >= 2)
                {
                    string combinedKey = string.Concat(sharedStarKeys);
                    return getOrCreateBarycentreBand(combinedKey, nullId);
                }
            }
        }

        // 3. Direct HostStarBodyId match
        if (entry.Body.HostStarBodyId.HasValue &&
            rootStarBandById.TryGetValue(entry.Body.HostStarBodyId.Value, out var byIdBand))
            return byIdBand;

        // 4. Single-star key match ("A", "B", "C", "D")
        if (!string.IsNullOrEmpty(starKey) &&
            rootStarBandByKey.TryGetValue(starKey, out var exactBand))
            return exactBand;

        return bands[0];
    }

    private static void LayoutChildrenRecursive(
        TreeEntry parent,
        int parentNodeIdx,
        double baseColX,
        ref double currentY,
        ref double maxRightX,
        int starBandIdx,
        int indentLevel,
        string sysPrefix,
        List<SystemMapNode> nodes,
        List<SystemMapMoonBracket> moonBrackets)
    {
        // Sort direct children by orbital order (SemiMajorAxisMeters when present, otherwise natural relative name
        // token order "a", "b", "c"... and BodyId) so sibling moons always render in strict top-to-bottom orbital sequence!
        if (parent.Children.Count > 1)
        {
            parent.Children.Sort(static (a, b) =>
            {
                if (a.Body.SemiMajorAxisMeters is double smaA && b.Body.SemiMajorAxisMeters is double smaB &&
                    smaA > 0 && smaB > 0 && Math.Abs(smaA - smaB) > 1.0)
                {
                    return smaA.CompareTo(smaB);
                }
                int idA = a.Body.BodyId ?? int.MaxValue;
                int idB = b.Body.BodyId ?? int.MaxValue;
                int idCmp = idA.CompareTo(idB);
                return idCmp != 0 ? idCmp : string.Compare(a.RelativeName, b.RelativeName, StringComparison.OrdinalIgnoreCase);
            });
        }

        // Count shared Null BarycentreIds among direct sibling moons of this parent
        // so binary moon pairs (e.g., 69 Upsilon Tauri ABC 3 c & 3 d, or ABC 5 a & 5 b, ABC 5 f & 5 g)
        // stay aligned at the same X and get a left-side vertical bracket!
        var siblingNullCounts = new Dictionary<int, int>();
        foreach (var c in parent.Children)
        {
            if (c.Body.DirectParentType.Equals("Null", StringComparison.OrdinalIgnoreCase) &&
                c.Body.BarycentreId.HasValue)
            {
                int bid = c.Body.BarycentreId.Value;
                siblingNullCounts[bid] = siblingNullCounts.GetValueOrDefault(bid) + 1;
            }
        }

        for (int cIdx = 0; cIdx < parent.Children.Count; cIdx++)
        {
            var child = parent.Children[cIdx];

            // Primary moons (indentLevel == 1, including binary moon pairs sharing a Null barycentre)
            // step downward by MoonStepY and align vertically at baseColX.
            // Sub-moons (indentLevel >= 2, whose direct parent is another moon, like ABC 5 i a -> ABC 5 i)
            // branch HORIZONTALLY to the RIGHT of their parent moon (same Y for the first sub-moon, X + SubMoonStepX),
            // matching Elite Dangerous's System Map!
            if (indentLevel <= 1 || cIdx > 0)
            {
                currentY += MoonStepY;
            }

            double childX = indentLevel <= 1
                ? baseColX
                : baseColX + (indentLevel - 1) * SubMoonStepX;
            if (childX > maxRightX)
                maxRightX = childX;

            int childNodeIdx = nodes.Count;
            var childNode = CreateNode(child.Body, childX, currentY, isStar: child.IsStar, isRootStar: false, isMoon: true,
                sysPrefix, parentIdx: parentNodeIdx, starBandIdx: starBandIdx, indentLevel: indentLevel);
            nodes.Add(childNode);

            // Check if this moon forms an exclusive binary moon pair with the previous sibling moon
            if (indentLevel == 1 &&
                cIdx > 0 &&
                child.Body.DirectParentType.Equals("Null", StringComparison.OrdinalIgnoreCase) &&
                child.Body.BarycentreId.HasValue &&
                parent.Children[cIdx - 1].Body.BarycentreId == child.Body.BarycentreId &&
                siblingNullCounts.GetValueOrDefault(child.Body.BarycentreId.Value) == 2)
            {
                var prevSiblingBody = parent.Children[cIdx - 1].Body;
                var prevMoonNode = nodes.LastOrDefault(n => ReferenceEquals(n.Body, prevSiblingBody));
                if (prevMoonNode is not null)
                {
                    double leftBracketX = Math.Min(prevMoonNode.X, childX) - Math.Max(prevMoonNode.RadiusPx, childNode.RadiusPx) - 7;
                    moonBrackets.Add(new SystemMapMoonBracket(
                        LeftX: leftBracketX,
                        AnchorX1: prevMoonNode.X - prevMoonNode.RadiusPx - 2,
                        Y1: prevMoonNode.Y,
                        AnchorX2: childX - childNode.RadiusPx - 2,
                        Y2: currentY));
                }
            }

            if (child.Children.Count > 0)
            {
                LayoutChildrenRecursive(child, childNodeIdx, baseColX, ref currentY, ref maxRightX,
                    starBandIdx, indentLevel + 1, sysPrefix, nodes, moonBrackets);
            }
        }
    }

    private static TreeEntry? ResolveDirectNonRootParent(
        TreeEntry entry,
        List<TreeEntry> allEntries,
        Dictionary<int, TreeEntry> byBodyId,
        Dictionary<string, TreeEntry> byRelName)
    {
        var b = entry.Body;

        // 0. Check hierarchical relative name first for moons and sub-moons (e.g., "AB 1 a a" -> "AB 1 a",
        //    "ABC 5 i a" -> "ABC 5 i"):
        //    In Stellar Forge, when a moon and its sub-moon orbit a local barycentre outside the moon's radius,
        //    or when a moon orbits a brown dwarf ("Star" in Parents) whose moons have sub-moons,
        //    checking the parent token first guarantees the sub-moon attaches to its parent moon
        //    (IndentLevel 2+) instead of flattening onto the host planet/brown dwarf (IndentLevel 1)!
        TreeEntry? namedMoonParent = TryResolveMoonParentByRelativeName(entry.RelativeName, byRelName);
        if (namedMoonParent is not null)
            return namedMoonParent;

        // 1. Authoritative Journal Parents array:
        if (!string.IsNullOrEmpty(b.DirectParentType))
        {
            if ((b.DirectParentType.Equals("Planet", StringComparison.OrdinalIgnoreCase) ||
                 b.DirectParentType.Equals("Star", StringComparison.OrdinalIgnoreCase)) &&
                b.DirectParentBodyId.HasValue &&
                byBodyId.TryGetValue(b.DirectParentBodyId.Value, out var direct))
            {
                return direct.IsRootStar ? null : direct;
            }

            if (b.DirectParentType.Equals("Null", StringComparison.OrdinalIgnoreCase))
            {
                // If another non-root body shares the exact same Null BarycentreId and has a shorter hierarchical
                // name prefix, attach the deeper sub-moon to the primary moon.
                if (b.BarycentreId.HasValue)
                {
                    var peerMoon = allEntries.FirstOrDefault(other =>
                        !ReferenceEquals(other, entry) &&
                        !other.IsRootStar &&
                        other.Body.BarycentreId == b.BarycentreId &&
                        other.RelativeName.Length > 0 &&
                        entry.RelativeName.StartsWith(other.RelativeName + " ", StringComparison.OrdinalIgnoreCase));
                    if (peerMoon is not null)
                        return peerMoon;
                }

                // Otherwise, walk the full Parents array after the leading Null entry(ies) to find the first
                // non-root Planet or orbiting non-root Star (e.g. a brown dwarf like "69 Upsilon Tauri ABC 3" or "ABC 5"
                // whose binary moons have Parents: [{"Null": X}, {"Star": 31}, {"Null": 0}]!).
                if (b.Parents.Count > 1)
                {
                    for (int i = 1; i < b.Parents.Count; i++)
                    {
                        var pRef = b.Parents[i];
                        if ((pRef.Type.Equals("Planet", StringComparison.OrdinalIgnoreCase) ||
                             pRef.Type.Equals("Star", StringComparison.OrdinalIgnoreCase)) &&
                            byBodyId.TryGetValue(pRef.BodyId, out var ancestorBody) &&
                            !ancestorBody.IsRootStar)
                        {
                            return ancestorBody;
                        }
                    }
                }

                if (b.ParentPlanetBodyId.HasValue && byBodyId.TryGetValue(b.ParentPlanetBodyId.Value, out var hostPlanet))
                    return hostPlanet;

                if (b.HostStarBodyId.HasValue &&
                    byBodyId.TryGetValue(b.HostStarBodyId.Value, out var hostOrbitingStar) &&
                    !hostOrbitingStar.IsRootStar)
                {
                    return hostOrbitingStar;
                }

                // Circumbinary planets/brown dwarfs around a stellar Null barycentre stay on the horizontal axis!
                return null;
            }
        }

        return null;
    }

    private static TreeEntry? TryResolveMoonParentByRelativeName(
        string rel,
        Dictionary<string, TreeEntry> byRelName)
    {
        if (string.IsNullOrEmpty(rel))
            return null;

        string[] parts = rel.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            string last = parts[^1];
            if (last.Length == 1 && char.IsLetter(last[0]))
            {
                string parentRel = string.Join(' ', parts[..^1]);
                if (byRelName.TryGetValue(parentRel, out var namedParent) && !namedParent.IsRootStar)
                    return namedParent;
            }
        }

        return null;
    }

    private static int? GetFirstNullBarycentreId(ExplorationBodyItem body)
    {
        if (body.BarycentreId.HasValue)
            return body.BarycentreId.Value;
        var firstNull = body.Parents.FirstOrDefault(static p => p.Type.Equals("Null", StringComparison.OrdinalIgnoreCase));
        return firstNull is not null ? firstNull.BodyId : null;
    }

    private static IEnumerable<int> GetNullBarycentreIds(ExplorationBodyItem body)
    {
        if (body.Parents.Count > 0)
        {
            foreach (var p in body.Parents)
            {
                if (p.Type.Equals("Null", StringComparison.OrdinalIgnoreCase))
                    yield return p.BodyId;
            }
            yield break;
        }

        if (body.BarycentreId.HasValue)
            yield return body.BarycentreId.Value;
    }

    private static ExplorationRingItem? GetFirstAsteroidBeltRing(ExplorationBodyItem starBody)
    {
        if (starBody.Rings is null || starBody.Rings.Count == 0)
            return null;
        return starBody.Rings.FirstOrDefault(static r =>
            r.Name.Contains("Belt", StringComparison.OrdinalIgnoreCase) ||
            r.RingClass.Contains("Belt", StringComparison.OrdinalIgnoreCase));
    }

    private static SystemMapNode CreateNode(
        ExplorationBodyItem body,
        double x,
        double y,
        bool isStar,
        bool isRootStar,
        bool isMoon,
        string sysPrefix,
        int parentIdx,
        int starBandIdx,
        int indentLevel)
    {
        double radius = ResolveBodyRadiusPx(body, isStar, isRootStar, isMoon);
        double gravityG = body.SurfaceGravity.HasValue ? body.SurfaceGravity.Value / 9.80665 : 0;
        bool isHighG = !isStar && body.Landable == true && gravityG >= 1.5;
        bool hasBio = !isStar && body.BiologicalSignalCount > 0;
        bool isTerraformable = !isStar &&
                               !string.IsNullOrWhiteSpace(body.TerraformState) &&
                               !body.TerraformState.Equals("Not terraformable", StringComparison.OrdinalIgnoreCase);

        bool isEarthLike = !isStar &&
                           (body.BodyType.Contains("Earth-like", StringComparison.OrdinalIgnoreCase) ||
                            body.BodyType.Contains("Earthlike", StringComparison.OrdinalIgnoreCase));
        bool hasRings = HasRealRings(body);
        string? atmosphereHalo = isStar ? null : ResolveAtmosphereHaloHex(body.AtmosphereType, body.Atmosphere);

        string fill = ResolveBodyFillHex(body, isStar, isTerraformable);
        string stroke = isStar
            ? ResolveStarStrokeHex(body.StarType)
            : isHighG ? "#FF2E4C"
            : hasBio ? "#00E676"
            : body.WasMapped == true ? "#38BDF8"
            : body.IsExternalOnly ? "#64748B"
            : "#94A3B8";

        string shortLabel = StripSystemPrefix(body.BodyName, sysPrefix);
        if (string.IsNullOrWhiteSpace(shortLabel))
            shortLabel = isStar ? (string.IsNullOrWhiteSpace(body.StarType) ? "★" : body.StarType) : body.BodyName;
        if (shortLabel.Length > 10)
            shortLabel = shortLabel[..10];

        int stationCount = body.Stations?.Count ?? 0;
        string? stationsSummary = stationCount > 0
            ? string.Join(", ", body.Stations!.Take(4))
            : null;

        return new SystemMapNode(
            Body: body,
            X: x,
            Y: y,
            RadiusPx: radius,
            FillHex: fill,
            StrokeHex: stroke,
            AtmosphereHaloHex: atmosphereHalo,
            IsEarthLike: isEarthLike,
            IsStar: isStar,
            IsRootStar: isRootStar,
            HasRings: hasRings,
            HasBioHalo: hasBio,
            IsHighGravity: isHighG,
            IsLandable: !isStar && body.Landable == true,
            IsSpanshOnly: body.IsExternalOnly,
            ShortLabel: shortLabel,
            ParentIndex: parentIdx,
            StarBandIndex: starBandIdx,
            IndentLevel: indentLevel,
            IsBarycentre: false,
            IsAsteroidBelt: false,
            StationCount: stationCount,
            StationsSummary: stationsSummary);
    }

    /// <summary>
    /// Returns true when the body has real planetary/brown-dwarf rings (excluding stellar asteroid belts).
    /// </summary>
    internal static bool HasRealRings(ExplorationBodyItem body)
    {
        if (body.Rings is null || body.Rings.Count == 0)
            return false;
        return body.Rings.Any(static r =>
            !r.Name.Contains("Belt", StringComparison.OrdinalIgnoreCase) &&
            !r.RingClass.Contains("Belt", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsStarBody(ExplorationBodyItem body) =>
        !string.IsNullOrWhiteSpace(body.StarType) ||
        body.BodyType.Contains("Star", StringComparison.OrdinalIgnoreCase) ||
        body.BodyType.Contains("Black Hole", StringComparison.OrdinalIgnoreCase) ||
        body.BodyType.Contains("White Dwarf", StringComparison.OrdinalIgnoreCase);

    private static string StripSystemPrefix(string bodyName, string sysPrefix)
    {
        if (string.IsNullOrWhiteSpace(bodyName))
            return "";
        if (!string.IsNullOrWhiteSpace(sysPrefix) &&
            bodyName.StartsWith(sysPrefix, StringComparison.OrdinalIgnoreCase) &&
            bodyName.Length > sysPrefix.Length)
        {
            return bodyName[sysPrefix.Length..].Trim();
        }
        return bodyName.Trim();
    }

    private static string ExtractStarKey(string relativeName)
    {
        if (string.IsNullOrWhiteSpace(relativeName))
            return "";
        var match = StarLetterPrefixRegex().Match(relativeName);
        return match.Success ? match.Groups[1].Value : "";
    }

    internal static double ResolveBodyRadiusPx(ExplorationBodyItem b, bool isStar, bool isRootStar, bool isMoon)
    {
        if (isStar)
        {
            string st = (b.StarType ?? "").Trim().ToUpperInvariant();
            if (!isRootStar)
                return 17.5; // Orbiting brown dwarfs / secondary stars on planet axis
            if (st is "N" || st.StartsWith("D", StringComparison.Ordinal))
                return 19;
            if (st is "H" or "SUPERMASSIVEBLACKHOLE" or "L" or "T" or "Y")
                return 22;
            if (st is "M" or "TTS" or "AEBE")
                return 25;
            if (st is "O" or "B" || st.StartsWith("W", StringComparison.Ordinal) || st.Contains("SUPERGIANT", StringComparison.Ordinal))
                return 31;
            return 28;
        }

        if (isMoon)
            return 7.5;

        if (b.BodyType.Contains("Gas giant", StringComparison.OrdinalIgnoreCase))
            return 16;

        return 11.5;
    }

    internal static string ResolveBodyFillHex(ExplorationBodyItem b, bool isStar, bool isTerraformable)
    {
        if (isStar)
            return ResolveStarFillHex(b.StarType);

        string cls = b.BodyType;
        bool hasAtmosphere = !string.IsNullOrWhiteSpace(b.AtmosphereType) &&
                             !b.AtmosphereType.Equals("None", StringComparison.OrdinalIgnoreCase);

        // High-value terrestrial worlds
        if (cls.Contains("Earth-like", StringComparison.OrdinalIgnoreCase) || cls.Contains("Earthlike", StringComparison.OrdinalIgnoreCase))
            return "#1E6BD6";
        if (cls.Contains("Water world", StringComparison.OrdinalIgnoreCase))
            return b.AtmosphereType.Contains("CarbonDioxide", StringComparison.OrdinalIgnoreCase) ? "#4B86A8" : "#0E6BA8";
        if (cls.Contains("Ammonia world", StringComparison.OrdinalIgnoreCase))
            return "#7D6B3A";

        // Gas & Water giants (specific Sudarsky classes first)
        if (cls.Contains("Water giant", StringComparison.OrdinalIgnoreCase) ||
            cls.Contains("water-based life", StringComparison.OrdinalIgnoreCase))
            return "#4FA3C7";
        if (cls.Contains("ammonia-based life", StringComparison.OrdinalIgnoreCase))
            return "#8F8C5E";
        if (cls.Contains("class V", StringComparison.OrdinalIgnoreCase))
            return "#B84A27";
        if (cls.Contains("class IV", StringComparison.OrdinalIgnoreCase))
            return "#6E352C";
        if (cls.Contains("class III", StringComparison.OrdinalIgnoreCase))
            return "#3B4D61";
        if (cls.Contains("class II", StringComparison.OrdinalIgnoreCase))
            return "#E6D8B5";
        if (cls.Contains("class I", StringComparison.OrdinalIgnoreCase))
            return "#D9C5A0";
        if (cls.Contains("Helium", StringComparison.OrdinalIgnoreCase) && cls.Contains("gas giant", StringComparison.OrdinalIgnoreCase))
            return "#A9B4C2";
        if (cls.Contains("Gas giant", StringComparison.OrdinalIgnoreCase))
            return "#C4A882";

        // Rocky, Metal & Icy bodies
        if (cls.Contains("Metal rich", StringComparison.OrdinalIgnoreCase) || cls.Contains("Metal-rich", StringComparison.OrdinalIgnoreCase))
            return "#6E655F";
        if (cls.Contains("High metal", StringComparison.OrdinalIgnoreCase))
            return hasAtmosphere ? "#8C7A6B" : "#C2B280";
        if (cls.Contains("Rocky ice", StringComparison.OrdinalIgnoreCase))
            return "#9BB0B8";
        if (cls.Contains("Rocky", StringComparison.OrdinalIgnoreCase))
            return "#8A8279";
        if (cls.Contains("Icy", StringComparison.OrdinalIgnoreCase))
            return "#CBE3EE";

        return isTerraformable ? "#C2A65A" : "#7A746C";
    }

    internal static string? ResolveAtmosphereHaloHex(string? atmosphereType, string? atmosphere)
    {
        string atm = !string.IsNullOrWhiteSpace(atmosphereType) ? atmosphereType : (atmosphere ?? "");
        if (string.IsNullOrWhiteSpace(atm) || atm.Equals("None", StringComparison.OrdinalIgnoreCase))
            return null;

        if (atm.Contains("Water", StringComparison.OrdinalIgnoreCase) ||
            atm.Contains("Oxygen", StringComparison.OrdinalIgnoreCase) ||
            atm.Contains("Nitrogen", StringComparison.OrdinalIgnoreCase) ||
            atm.Contains("Argon", StringComparison.OrdinalIgnoreCase))
            return "#7DD3FC";
        if (atm.Contains("CarbonDioxide", StringComparison.OrdinalIgnoreCase) ||
            atm.Contains("Carbon dioxide", StringComparison.OrdinalIgnoreCase))
            return "#CBD5E1";
        if (atm.Contains("Methane", StringComparison.OrdinalIgnoreCase) ||
            atm.Contains("Helium", StringComparison.OrdinalIgnoreCase))
            return "#5EEAD4";
        if (atm.Contains("Ammonia", StringComparison.OrdinalIgnoreCase) ||
            atm.Contains("Sulphur", StringComparison.OrdinalIgnoreCase) ||
            atm.Contains("Sulfur", StringComparison.OrdinalIgnoreCase) ||
            atm.Contains("Silicate", StringComparison.OrdinalIgnoreCase))
            return "#FBBF24";
        return "#94A3B8";
    }

    internal static string ResolveStarFillHex(string? starType)
    {
        string st = (starType ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(st)) return "#F5D042";
        if (st == "O") return "#7EB8FF";
        if (st == "B" || st.StartsWith("B_", StringComparison.Ordinal)) return "#B3D4FF";
        if (st == "A" || st.StartsWith("A_", StringComparison.Ordinal)) return "#E8F0FF";
        if (st == "F" || st.StartsWith("F_", StringComparison.Ordinal)) return "#FFF6D6";
        if (st == "G" || st.StartsWith("G_", StringComparison.Ordinal)) return "#F5D042";
        if (st == "K" || st.StartsWith("K_", StringComparison.Ordinal)) return "#FF9E3D";
        if (st == "M" || st.StartsWith("M_", StringComparison.Ordinal)) return "#FF5733";
        if (st == "L") return "#A8324A";
        if (st == "T") return "#9E2A5E";
        if (st == "Y") return "#7E1D5C";
        if (st == "N") return "#66F0FF";
        if (st == "H" || st.Contains("BLACKHOLE", StringComparison.Ordinal)) return "#0B0E17";
        if (st.StartsWith("D", StringComparison.Ordinal)) return "#D8F8FF";
        if (st.StartsWith("W", StringComparison.Ordinal)) return "#A78BFA";
        if (st is "C" or "S" or "MS" or "CN" or "CJ" || st.StartsWith("C_", StringComparison.Ordinal)) return "#DC3545";
        if (st is "TTS" or "AEBE") return "#FFB74D";
        return "#F5D042";
    }

    private static string ResolveStarStrokeHex(string? starType)
    {
        string st = (starType ?? "").Trim().ToUpperInvariant();
        if (st == "H" || st.Contains("BLACKHOLE", StringComparison.Ordinal)) return "#A87CFF";
        if (st == "N" || st.StartsWith("D", StringComparison.Ordinal)) return "#66F0FF";
        if (st is "L" or "T" or "Y") return "#D9468F";
        return "#FFF3C4";
    }
}
