// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/SystemMapLayoutBuilder.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Computes Elite Dangerous-style 2D orbital tree layout using Journal
//              Parents hierarchy (root stars on left, planets & orbiting brown dwarfs
//              on main horizontal axis, binary pair brackets, vertical moons, and
//              L-indented sub-moons) shared between Desktop WPF and Web HUD.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HUDnopy.Models;

namespace HUDnopy.Services;

/// <summary>
/// Positioned 2D node representing a celestial body in the interactive System Map.
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
    string BadgeText,
    int ParentIndex,
    int StarBandIndex,
    int IndentLevel);

/// <summary>
/// Top orbital bracket connecting a binary pair of bodies sharing a Null barycentre.
/// </summary>
public sealed record SystemMapBinaryBracket(
    double X1,
    double X2,
    double TopY,
    double AnchorY1,
    double AnchorY2);

/// <summary>
/// Complete 2D schematic layout for the interactive System Map.
/// </summary>
public sealed record SystemMapLayoutResult(
    List<SystemMapNode> Nodes,
    List<SystemMapBinaryBracket> Brackets,
    double CanvasWidth,
    double CanvasHeight);

/// <summary>
/// Transforms unified ShipState.ExplorationBodies into an Elite Dangerous-style 2D orbital diagram:
/// root stars stacked vertically on the left, planets &amp; orbiting stars horizontally to their right
/// (with top brackets for binary pairs), moons hanging vertically below their direct parent, and
/// sub-moons indented in an L-shape to the right.
/// </summary>
public static partial class SystemMapLayoutBuilder
{
    private const double StarColumnX = 46;
    private const double FirstPlanetX = 112;
    private const double PlanetStepX = 48;
    private const double BinaryPairStepX = 40;
    private const double MoonStepY = 38;
    private const double SubMoonIndentX = 25;
    private const double MinBandHeight = 96;

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

    public static SystemMapLayoutResult Build(
        IReadOnlyList<ExplorationBodyItem> bodies,
        string? systemName = null)
    {
        var nodes = new List<SystemMapNode>();
        var brackets = new List<SystemMapBinaryBracket>();
        if (bodies is null || bodies.Count == 0)
            return new SystemMapLayoutResult(nodes, brackets, 420, 150);

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
        // Numbered orbiting stars/brown dwarfs (e.g., "Exphiay 7" orbiting Star 0) are NOT root stars;
        // they sit on their host star's main horizontal axis just like in Elite Dangerous!
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

        // Map root stars by letter ("A", "B", ...) and BodyId for fast lookup
        var rootStarByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rootStarById = new Dictionary<int, int>();
        var bandHorizontals = new List<List<TreeEntry>>();

        if (rootStars.Count == 0)
        {
            bandHorizontals.Add(new List<TreeEntry>());
        }
        else
        {
            for (int i = 0; i < rootStars.Count; i++)
            {
                var rs = rootStars[i];
                string key = ExtractStarKey(rs.RelativeName);
                if (string.IsNullOrEmpty(key) && i == 0) key = "A";
                if (!string.IsNullOrEmpty(key) && !rootStarByKey.ContainsKey(key))
                    rootStarByKey[key] = i;
                if (rs.Body.BodyId.HasValue)
                    rootStarById[rs.Body.BodyId.Value] = i;
                bandHorizontals.Add(new List<TreeEntry>());
            }
        }

        // Step 2: Attach every non-root body to either a Root Star's horizontal axis or its direct parent body
        foreach (var e in entries)
        {
            if (e.IsRootStar)
                continue;

            TreeEntry? directParent = ResolveDirectNonRootParent(e, byBodyId, byRelName);
            if (directParent is not null && !directParent.IsRootStar && !ReferenceEquals(directParent, e))
            {
                directParent.Children.Add(e);
                continue;
            }

            // Otherwise, this body belongs on the main horizontal orbital axis of its host root star
            int bandIdx = ResolveHostStarBandIndex(e, rootStarById, rootStarByKey);
            bandHorizontals[bandIdx].Add(e);
        }

        // Step 3: Lay out each star band cleanly
        double currentBandY = 62;
        double maxX = 420;
        double maxY = 150;
        int bandCount = bandHorizontals.Count;

        for (int bIdx = 0; bIdx < bandCount; bIdx++)
        {
            int starNodeIdx = -1;
            if (bIdx < rootStars.Count)
            {
                var rs = rootStars[bIdx];
                starNodeIdx = nodes.Count;
                nodes.Add(CreateNode(rs.Body, StarColumnX, currentBandY, isStar: true, isRootStar: true, isMoon: false,
                    sysPrefix, parentIdx: -1, starBandIdx: bIdx, indentLevel: 0));
            }

            double colX = FirstPlanetX;
            double bandMaxBottom = currentBandY + 46;
            var horiz = bandHorizontals[bIdx];

            for (int hIdx = 0; hIdx < horiz.Count; hIdx++)
            {
                var p = horiz[hIdx];

                // Check if this body forms a binary pair with the previous horizontal body (same Null BarycentreId)
                bool isBinaryPartnerOfPrev =
                    hIdx > 0 &&
                    p.Body.BarycentreId.HasValue &&
                    horiz[hIdx - 1].Body.BarycentreId == p.Body.BarycentreId;

                int planetNodeIdx = nodes.Count;
                nodes.Add(CreateNode(p.Body, colX, currentBandY, isStar: p.IsStar, isRootStar: false, isMoon: false,
                    sysPrefix, parentIdx: starNodeIdx, starBandIdx: bIdx, indentLevel: 0));

                if (isBinaryPartnerOfPrev)
                {
                    // Find the node index of horiz[hIdx - 1]
                    var prevNode = nodes.LastOrDefault(n => ReferenceEquals(n.Body, horiz[hIdx - 1].Body));
                    if (prevNode is not null)
                    {
                        double curR = nodes[planetNodeIdx].RadiusPx;
                        double topY = currentBandY - Math.Max(prevNode.RadiusPx, curR) - 16;
                        brackets.Add(new SystemMapBinaryBracket(
                            X1: prevNode.X,
                            X2: colX,
                            TopY: topY,
                            AnchorY1: prevNode.Y - prevNode.RadiusPx - 2,
                            AnchorY2: currentBandY - curR - 2));
                    }
                }

                // Lay out moons (indent 1) and sub-moons (indent 2+) vertically underneath this body
                double subY = currentBandY;
                double maxSubRightX = colX;
                LayoutChildrenRecursive(p, planetNodeIdx, colX, ref subY, ref maxSubRightX, bIdx, indentLevel: 1, sysPrefix, nodes);

                if (subY + 46 > bandMaxBottom)
                    bandMaxBottom = subY + 46;

                double rightmostX = Math.Max(colX, maxSubRightX);
                if (rightmostX + 70 > maxX)
                    maxX = rightmostX + 70;

                // Advance X: binary pairs sit slightly closer unless the first member has indented sub-moons
                bool nextIsBinaryPartner =
                    hIdx + 1 < horiz.Count &&
                    p.Body.BarycentreId.HasValue &&
                    horiz[hIdx + 1].Body.BarycentreId == p.Body.BarycentreId;

                double step = nextIsBinaryPartner && maxSubRightX <= colX
                    ? BinaryPairStepX
                    : PlanetStepX;
                colX = rightmostX + step;
            }

            if (bandMaxBottom > maxY)
                maxY = bandMaxBottom;

            double bandHeight = Math.Max(MinBandHeight, (bandMaxBottom - currentBandY) + 48);
            currentBandY += bandHeight;
        }

        return new SystemMapLayoutResult(nodes, brackets, Math.Max(420, maxX), Math.Max(150, maxY));
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
        List<SystemMapNode> nodes)
    {
        foreach (var child in parent.Children)
        {
            currentY += MoonStepY;
            double childX = baseColX + Math.Max(0, indentLevel - 1) * SubMoonIndentX;
            if (childX > maxRightX)
                maxRightX = childX;

            int childNodeIdx = nodes.Count;
            nodes.Add(CreateNode(child.Body, childX, currentY, isStar: child.IsStar, isRootStar: false, isMoon: true,
                sysPrefix, parentIdx: parentNodeIdx, starBandIdx: starBandIdx, indentLevel: indentLevel));

            if (child.Children.Count > 0)
            {
                LayoutChildrenRecursive(child, childNodeIdx, baseColX, ref currentY, ref maxRightX,
                    starBandIdx, indentLevel + 1, sysPrefix, nodes);
            }
        }
    }

    private static TreeEntry? ResolveDirectNonRootParent(
        TreeEntry entry,
        Dictionary<int, TreeEntry> byBodyId,
        Dictionary<string, TreeEntry> byRelName)
    {
        var b = entry.Body;

        // 1. Authoritative Journal Parents array:
        if (!string.IsNullOrEmpty(b.DirectParentType))
        {
            // Direct child of a Planet or orbiting non-root Star (e.g., Exphiay 7 a -> Exphiay 7; 7 e a -> 7 e; Rakhiot -> Discovery)
            if ((b.DirectParentType.Equals("Planet", StringComparison.OrdinalIgnoreCase) ||
                 b.DirectParentType.Equals("Star", StringComparison.OrdinalIgnoreCase)) &&
                b.DirectParentBodyId.HasValue &&
                byBodyId.TryGetValue(b.DirectParentBodyId.Value, out var direct))
            {
                return direct.IsRootStar ? null : direct;
            }

            // Binary pair under a Null barycentre:
            // If it has a ParentPlanetBodyId (binary moons around a planet), attach to that planet.
            // Otherwise (binary planets around a star, like 4-5, 9-10, 11-12), it stays on the main horizontal axis!
            if (b.DirectParentType.Equals("Null", StringComparison.OrdinalIgnoreCase))
            {
                if (b.ParentPlanetBodyId.HasValue && byBodyId.TryGetValue(b.ParentPlanetBodyId.Value, out var hostPlanet))
                    return hostPlanet;
                return null;
            }
        }

        // 2. Fallback for Spansh/EDSM-only bodies without Parents: derive parent strictly from relative name tokens
        //    "7 e a" -> parent "7 e"; "7 a" -> parent "7"; "10 b" -> parent "10"
        string rel = entry.RelativeName;
        if (!string.IsNullOrEmpty(rel))
        {
            string[] parts = rel.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                string last = parts[^1];
                if (last.Length == 1 && char.IsLower(last[0]))
                {
                    string parentRel = string.Join(' ', parts[..^1]);
                    if (byRelName.TryGetValue(parentRel, out var namedParent))
                        return namedParent.IsRootStar ? null : namedParent;
                }
            }
        }

        return null;
    }

    private static int ResolveHostStarBandIndex(
        TreeEntry entry,
        Dictionary<int, int> rootStarById,
        Dictionary<string, int> rootStarByKey)
    {
        if (entry.Body.HostStarBodyId.HasValue &&
            rootStarById.TryGetValue(entry.Body.HostStarBodyId.Value, out int byId))
            return byId;

        string starKey = ExtractStarKey(entry.RelativeName);
        if (!string.IsNullOrEmpty(starKey))
        {
            if (rootStarByKey.TryGetValue(starKey, out int exact))
                return exact;
            if (starKey.Length > 1 && rootStarByKey.TryGetValue(starKey[..1], out int circumbinary))
                return circumbinary;
        }

        return 0;
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

        string badge = "";
        if (isStar && !string.IsNullOrWhiteSpace(body.StarType))
            badge = body.StarType.Length <= 5 ? body.StarType : body.StarType[..5];
        else if (hasBio) badge = $"{body.BiologicalSignalCount}B";
        else if (isHighG) badge = $"{gravityG:0.0}G";
        else if (body.WasMapped == true) badge = "DSS";
        else if (body.IsExternalOnly) badge = body.DataSource == "EDSM" ? "EDSM" : "SP";

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
            BadgeText: badge,
            ParentIndex: parentIdx,
            StarBandIndex: starBandIdx,
            IndentLevel: indentLevel);
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
