// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/LandingPadLocatorService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Maps Elite Dangerous starport landing pad numbers (1-45) to
//              clock orientation, bay depth, pad size, and visual layout cues.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace HUDnopy.Services;

/// <summary>
/// Spatial metadata for a standard starport landing pad inside a Coriolis/Orbis/Ocellus bay.
/// </summary>
public sealed record LandingPadInfo(
    int PadNumber,
    int ClockHour,
    int DepthRow,
    string PadSize,
    bool IsStandardStarport)
{
    /// <summary>
    /// Formats a localized directional summary (clock position, depth row, and pad size).
    /// </summary>
    public string FormatGuidance(string language)
    {
        bool isEs = language.StartsWith("es", StringComparison.OrdinalIgnoreCase);
        bool isFr = language.StartsWith("fr", StringComparison.OrdinalIgnoreCase);
        bool isDe = language.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        bool isRu = language.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
        bool isPt = language.StartsWith("pt", StringComparison.OrdinalIgnoreCase);

        if (!IsStandardStarport)
        {
            return isEs ? $"Plataforma {PadNumber}" :
                   isFr ? $"Plateforme {PadNumber}" :
                   isDe ? $"Landeplatz {PadNumber}" :
                   isRu ? $"Площадка {PadNumber}" :
                   isPt ? $"Plataforma {PadNumber}" :
                          $"Landing Pad {PadNumber}";
        }

        string depth = DepthRow switch
        {
            1 => isEs ? "Entrada (delante)" : isFr ? "Entrée (avant)" : isDe ? "Eingang (vorne)" : isRu ? "У входа (ближняя)" : isPt ? "Entrada (frente)" : "Near entrance (front)",
            2 => isEs ? "Mitad delantera" : isFr ? "Milieu avant" : isDe ? "Vordere Mitte" : isRu ? "Ближе к входу" : isPt ? "Meio-frente" : "Mid-front",
            3 => isEs ? "Mitad trasera" : isFr ? "Milieu arrière" : isDe ? "Hintere Mitte" : isRu ? "Ближе к концу" : isPt ? "Meio-fundo" : "Mid-back",
            _ => isEs ? "Fondo (atrás)" : isFr ? "Fond (arrière)" : isDe ? "Hinten (Ende)" : isRu ? "В глубине (дальняя)" : isPt ? "Fundo (atrás)" : "Far end (back)"
        };

        string clock = isEs ? $"a las {ClockHour}" :
                       isFr ? $"à {ClockHour} h" :
                       isDe ? $"auf {ClockHour} Uhr" :
                       isRu ? $"на {ClockHour} часов" :
                       isPt ? $"às {ClockHour}h" :
                              $"at {ClockHour} o'clock";

        return isEs ? $"Pad {PadNumber} ({PadSize}) — {clock}, {depth}" :
               isFr ? $"Pad {PadNumber} ({PadSize}) — {clock}, {depth}" :
               isDe ? $"Pad {PadNumber} ({PadSize}) — {clock}, {depth}" :
               isRu ? $"Площадка {PadNumber} ({PadSize}) — {clock}, {depth}" :
               isPt ? $"Pad {PadNumber} ({PadSize}) — {clock}, {depth}" :
                      $"Pad {PadNumber} ({PadSize}) — {clock}, {depth}";
    }

    /// <summary>
    /// Builds a compact visual bay diagram highlighting the target depth row (1=entrance .. 4=back).
    /// </summary>
    public string BuildBayIndicator()
    {
        if (!IsStandardStarport)
            return $"[ PAD {PadNumber:00} ]";

        string[] rows = { "·", "·", "·", "·" };
        int idx = Math.Clamp(DepthRow - 1, 0, 3);
        rows[idx] = $"[{PadNumber:00}]";
        return $"MAILSLOT ▸ {rows[0]}  {rows[1]}  {rows[2]}  {rows[3]} ◂ BACK  |  CLOCK: {ClockHour:00}h  |  SIZE: {PadSize}";
    }
}

/// <summary>
/// Resolves Elite Dangerous landing pad positions inside standard rotational starports.
/// </summary>
public static class LandingPadLocatorService
{
    // Official 45-pad Coriolis/Orbis/Ocellus docking bay layout (green light on starboard/right):
    // Clock hour (1-12), depth row (1=front/mailslot .. 4=back), size (S/M/L).
    private static readonly Dictionary<int, (int Clock, int Depth, string Size)> StarportPads = new()
    {
        [1] = (6, 1, "S"),  [2] = (6, 2, "L"),  [3] = (6, 3, "M"),  [4] = (6, 4, "S"),
        [5] = (7, 1, "S"),  [6] = (7, 2, "M"),  [7] = (7, 3, "S"),  [8] = (7, 4, "S"),
        [9] = (8, 1, "L"),  [10] = (8, 2, "L"), [11] = (8, 3, "M"), [12] = (8, 4, "S"),
        [13] = (9, 1, "S"), [14] = (9, 2, "S"), [15] = (9, 3, "M"),
        [16] = (10, 1, "S"), [17] = (10, 2, "L"), [18] = (10, 3, "M"), [19] = (10, 4, "S"),
        [20] = (11, 1, "S"), [21] = (11, 2, "M"), [22] = (11, 3, "S"), [23] = (11, 4, "S"),
        [24] = (12, 1, "L"), [25] = (12, 2, "L"), [26] = (12, 3, "M"), [27] = (12, 4, "S"),
        [28] = (1, 1, "S"),  [29] = (1, 2, "S"),  [30] = (1, 3, "M"),
        [31] = (2, 1, "S"),  [32] = (2, 2, "L"),  [33] = (2, 3, "M"),  [34] = (2, 4, "S"),
        [35] = (3, 1, "S"),  [36] = (3, 2, "M"),  [37] = (3, 3, "S"),  [38] = (3, 4, "S"),
        [39] = (4, 1, "L"),  [40] = (4, 2, "L"),  [41] = (4, 3, "M"),  [42] = (4, 4, "S"),
        [43] = (5, 1, "S"),  [44] = (5, 2, "S"),  [45] = (5, 3, "M"),
    };

    /// <summary>
    /// Returns spatial guidance for the assigned landing pad and station type.
    /// </summary>
    public static LandingPadInfo Resolve(int padNumber, string? stationType)
    {
        bool isLargeStarport = string.IsNullOrWhiteSpace(stationType) ||
            stationType.Contains("Coriolis", StringComparison.OrdinalIgnoreCase) ||
            stationType.Contains("Orbis", StringComparison.OrdinalIgnoreCase) ||
            stationType.Contains("Ocellus", StringComparison.OrdinalIgnoreCase) ||
            stationType.Contains("Asteroid", StringComparison.OrdinalIgnoreCase) ||
            stationType.Contains("Bernal", StringComparison.OrdinalIgnoreCase);

        if (isLargeStarport && StarportPads.TryGetValue(padNumber, out var pad))
            return new LandingPadInfo(padNumber, pad.Clock, pad.Depth, pad.Size, true);

        return new LandingPadInfo(padNumber, 12, 1, "?", false);
    }
}
