// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/ShipLoadoutExportService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Generates compressed import URLs for EDSY and Coriolis from the
//              latest Elite Dangerous Journal Loadout JSON event.
// -----------------------------------------------------------------------------

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace HUDnopy.Services;

/// <summary>
/// Builds compressed EDSY and Coriolis ship-import URLs from raw Journal Loadout JSON.
/// </summary>
public static class ShipLoadoutExportService
{
    private const string EdsyImportPrefix = "https://edsy.org/#/I=";
    private const string CoriolisImportPrefix = "https://coriolis.io/import?data=";

    /// <summary>
    /// Builds an EDSY import URL for the given raw Loadout Journal JSON line.
    /// </summary>
    public static string? BuildEdsyUrl(string? loadoutJson)
    {
        string? encoded = CompressAndEncodeLoadout(loadoutJson);
        return encoded is null ? null : EdsyImportPrefix + encoded;
    }

    /// <summary>
    /// Builds a Coriolis import URL for the given raw Loadout Journal JSON line.
    /// </summary>
    public static string? BuildCoriolisUrl(string? loadoutJson)
    {
        string? encoded = CompressAndEncodeLoadout(loadoutJson);
        return encoded is null ? null : CoriolisImportPrefix + encoded;
    }

    /// <summary>
    /// Minifies, GZip-compresses, Base64-encodes, and URI-escapes the raw Loadout JSON payload.
    /// </summary>
    private static string? CompressAndEncodeLoadout(string? loadoutJson)
    {
        if (string.IsNullOrWhiteSpace(loadoutJson))
            return null;

        try
        {
            // Validate and minify the JSON payload to keep the resulting URL compact.
            using var doc = JsonDocument.Parse(loadoutJson);
            byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(doc.RootElement);

            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(utf8, 0, utf8.Length);
            }

            string base64 = Convert.ToBase64String(output.ToArray());
            return Uri.EscapeDataString(base64);
        }
        catch
        {
            return null;
        }
    }
}
