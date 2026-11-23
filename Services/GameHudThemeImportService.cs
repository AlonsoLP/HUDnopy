// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/GameHudThemeImportService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Imports cockpit HUD colors from Elite Dangerous GraphicsConfigurationOverride.xml
//              or EDHM UI theme files and maps them to HUDnopy Accent/Secondary HEX colors.
// -----------------------------------------------------------------------------

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

namespace HUDnopy.Services;

/// <summary>
/// Reads Elite Dangerous HUD color matrices (GraphicsConfigurationOverride.xml) or EDHM UI themes
/// to synchronize HUDnopy's primary and secondary HEX colors with the player's cockpit.
/// </summary>
public static class GameHudThemeImportService
{
    /// <summary>
    /// Attempts to detect and import cockpit HUD colors from EDHM UI or Elite Dangerous XML overrides.
    /// </summary>
    public static bool TryImportGameTheme(out string accentHex, out string secondaryHex, out string sourceLabel)
    {
        // 1. Check Elite Dangerous GraphicsConfigurationOverride.xml (standard Frontier path)
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string overrideXmlPath = Path.Combine(
            localAppData, "Frontier Developments", "Elite Dangerous", "Options", "Graphics", "GraphicsConfigurationOverride.xml");

        if (File.Exists(overrideXmlPath))
        {
            try
            {
                string xml = File.ReadAllText(overrideXmlPath);
                if (TryParseGraphicsOverrideXml(xml, out accentHex, out secondaryHex))
                {
                    sourceLabel = "GraphicsConfigurationOverride.xml";
                    return true;
                }
            }
            catch
            {
                // Continue to EDHM fallback
            }
        }

        // 2. Check common EDHM UI theme folders (CurrentTheme / ThemeSettings.json)
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] edhmCandidates =
        [
            Path.Combine(localAppData, "EDHM_UI", "ODYSS", "Themes", "CurrentSettings.json"),
            Path.Combine(userProfile, "EDHM_UI", "ODYSS", "Themes", "CurrentSettings.json"),
            Path.Combine(localAppData, "EDHM_UI", "CurrentSettings.json")
        ];

        foreach (string path in edhmCandidates)
        {
            if (!File.Exists(path))
                continue;
            try
            {
                string json = File.ReadAllText(path);
                if (TryParseEdhmThemeJson(json, out accentHex, out secondaryHex))
                {
                    sourceLabel = "EDHM UI";
                    return true;
                }
            }
            catch
            {
                // Ignore malformed EDHM file
            }
        }

        accentHex = "#FF7100";
        secondaryHex = "#FFB300";
        sourceLabel = "";
        return false;
    }

    /// <summary>
    /// Parses an Elite Dangerous GraphicsConfigurationOverride.xml payload and applies its 3x3
    /// MatrixRed/MatrixGreen/MatrixBlue transform to the default Elite orange (#FF7100) and amber (#FFB300).
    /// </summary>
    public static bool TryParseGraphicsOverrideXml(string xmlContent, out string accentHex, out string secondaryHex)
    {
        accentHex = "#FF7100";
        secondaryHex = "#FFB300";
        if (string.IsNullOrWhiteSpace(xmlContent))
            return false;

        try
        {
            var doc = XDocument.Parse(xmlContent);
            var guiNode = doc.Descendants("Default").FirstOrDefault()
                          ?? doc.Descendants("GUIColour").FirstOrDefault();
            if (guiNode is null)
                return false;

            if (!TryParseVector3(guiNode.Element("MatrixRed")?.Value, out var mR) ||
                !TryParseVector3(guiNode.Element("MatrixGreen")?.Value, out var mG) ||
                !TryParseVector3(guiNode.Element("MatrixBlue")?.Value, out var mB))
            {
                return false;
            }

            accentHex = ApplyColorMatrix(1.0, 0.443, 0.0, mR, mG, mB);      // #FF7100
            secondaryHex = ApplyColorMatrix(1.0, 0.702, 0.0, mR, mG, mB);   // #FFB300
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses an EDHM UI JSON theme snippet looking for primary/accent HEX color properties.
    /// </summary>
    public static bool TryParseEdhmThemeJson(string jsonContent, out string accentHex, out string secondaryHex)
    {
        accentHex = "#FF7100";
        secondaryHex = "#FFB300";
        if (string.IsNullOrWhiteSpace(jsonContent))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            string? foundPrimary = FindHexByKeyHint(doc.RootElement, "accent", "primary", "hud", "orange", "main");
            if (string.IsNullOrWhiteSpace(foundPrimary))
                return false;

            string? foundSecondary = FindHexByKeyHint(doc.RootElement, "secondary", "highlight", "radar", "amber");
            accentHex = foundPrimary;
            secondaryHex = !string.IsNullOrWhiteSpace(foundSecondary) && !string.Equals(foundSecondary, foundPrimary, StringComparison.OrdinalIgnoreCase)
                ? foundSecondary
                : LightenHex(foundPrimary, 0.28);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseVector3(string? raw, out (double X, double Y, double Z) vec)
    {
        vec = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string[] parts = raw.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            return false;

        if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
        {
            vec = (x, y, z);
            return true;
        }
        return false;
    }

    private static string ApplyColorMatrix(
        double r, double g, double b,
        (double X, double Y, double Z) mR,
        (double X, double Y, double Z) mG,
        (double X, double Y, double Z) mB)
    {
        // Elite Dangerous row-vector color matrix: [R', G', B'] = [R, G, B] * [mR; mG; mB]
        double outR = r * mR.X + g * mG.X + b * mB.X;
        double outG = r * mR.Y + g * mG.Y + b * mB.Y;
        double outB = r * mR.Z + g * mG.Z + b * mB.Z;

        int cr = (int)Math.Round(Math.Clamp(outR, 0.0, 1.0) * 255.0);
        int cg = (int)Math.Round(Math.Clamp(outG, 0.0, 1.0) * 255.0);
        int cb = (int)Math.Round(Math.Clamp(outB, 0.0, 1.0) * 255.0);
        return $"#{cr:X2}{cg:X2}{cb:X2}";
    }

    private static string? FindHexByKeyHint(JsonElement el, params string[] hints)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    string val = (prop.Value.GetString() ?? "").Trim();
                    if (IsValidHex6(val) && hints.Any(h => prop.Name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                        return val.ToUpperInvariant();
                }
                else if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    string? nested = FindHexByKeyHint(prop.Value, hints);
                    if (nested is not null)
                        return nested;
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                string? nested = FindHexByKeyHint(item, hints);
                if (nested is not null)
                    return nested;
            }
        }
        return null;
    }

    private static bool IsValidHex6(string value)
    {
        if (value.Length != 7 || value[0] != '#')
            return false;
        for (int i = 1; i < 7; i++)
        {
            char c = value[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }

    private static string LightenHex(string hex, double amount)
    {
        if (!IsValidHex6(hex))
            return "#FFB300";
        int r = Convert.ToInt32(hex.Substring(1, 2), 16);
        int g = Convert.ToInt32(hex.Substring(3, 2), 16);
        int b = Convert.ToInt32(hex.Substring(5, 2), 16);
        r = (int)Math.Clamp(r + (255 - r) * amount, 0, 255);
        g = (int)Math.Clamp(g + (255 - g) * amount, 0, 255);
        b = (int)Math.Clamp(b + (255 - b) * amount, 0, 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}
