// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/ConfigService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Thread-safe loader, validator, and saver for persistent application settings.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using HUDnopy.Models;
using HUDnopy.Localization;

namespace HUDnopy.Services;

/// <summary>
/// Thread-safe loader, validator, and saver for persistent application settings.
/// </summary>
public class ConfigService
{
    private readonly string _configPath;
    public AppConfig Current { get; private set; }
    public event Action<AppConfig>? ConfigChanged;

    /// <summary>
    /// Validates a 6-digit (<c>#RRGGBB</c>) or 8-digit (<c>#AARRGGBB</c>) hexadecimal color string.
    /// </summary>
    public static bool IsValidHexColor(string? hex, bool allowAlpha = true)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return false;
        string trimmed = hex.Trim();
        if (trimmed[0] != '#' || (trimmed.Length != 7 && (!allowAlpha || trimmed.Length != 9)))
            return false;
        for (int i = 1; i < trimmed.Length; i++)
        {
            if (!Uri.IsHexDigit(trimmed[i]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Normalizes a 6-digit hex color string (with or without leading <c>#</c>) to uppercase <c>#RRGGBB</c>, or returns <see langword="null"/> if invalid.
    /// </summary>
    public static string? NormalizeHex6(string? raw)
    {
        string trimmed = (raw ?? "").Trim();
        if (trimmed.StartsWith('#'))
            trimmed = trimmed[1..];
        if (trimmed.Length != 6)
            return null;
        foreach (char c in trimmed)
        {
            if (!Uri.IsHexDigit(c))
                return null;
        }
        return "#" + trimmed.ToUpperInvariant();
    }

    public static readonly List<HudThemePreset> Presets = new()
    {
        new HudThemePreset { Name = "Naranja HUD (Clásico)", AccentHex = "#FF7100", SecondaryHex = "#FFB300" },
        new HudThemePreset { Name = "Ámbar Profundo", AccentHex = "#FF9800", SecondaryHex = "#FFD54F" },
        new HudThemePreset { Name = "Cian Imperial", AccentHex = "#00E5FF", SecondaryHex = "#80D8FF" },
        new HudThemePreset { Name = "Azul Federación", AccentHex = "#2979FF", SecondaryHex = "#82B1FF" },
        new HudThemePreset { Name = "Verde Alianza", AccentHex = "#00E676", SecondaryHex = "#69F0AE" },
        new HudThemePreset { Name = "Rojo Alerta Táctica", AccentHex = "#FF1744", SecondaryHex = "#FF8A80" },
        new HudThemePreset { Name = "Violeta Guardian", AccentHex = "#D500F9", SecondaryHex = "#EA80FC" }
    };

    public ConfigService()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _configPath = Path.Combine(baseDir, "config.json");
        Current = Load();
    }

    public AppConfig Load()
    {
        // Try the main file first, then the automatic backup written by WriteConfig.
        foreach (string path in new[] { _configPath, _configPath + ".bak" })
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                string json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
                if (cfg != null)
                {
                    if (!json.Contains("HasCompletedFirstRunWizard", StringComparison.OrdinalIgnoreCase))
                        cfg.HasCompletedFirstRunWizard = true;
                    if (!string.Equals(path, _configPath, StringComparison.OrdinalIgnoreCase))
                        AppDiagnosticsLogger.Warn("ConfigService", "config.json was unreadable; settings restored from config.json.bak.");
                    Current = cfg;
                    return cfg;
                }
            }
            catch (Exception ex)
            {
                AppDiagnosticsLogger.Warn("ConfigService", $"Failed to load '{Path.GetFileName(path)}': {ex.Message}");
            }
        }

        Current = new AppConfig();
        return Current;
    }

    public void Save()
    {
        try
        {
            WriteConfig();
            ApplyThemeToApplication();
            ConfigChanged?.Invoke(Current);
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("ConfigService", $"Failed to save config.json: {ex.Message}");
        }
    }

    public void SetInterfaceLanguage(string language)
    {
        if (!LanguageCatalog.TryNormalize(language, out string normalized))
            throw new ArgumentException($"Idioma no compatible: {language}", nameof(language));

        string previous = Current.InterfaceLanguage;
        Current.InterfaceLanguage = normalized;
        try
        {
            WriteConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Current.InterfaceLanguage = previous;
            throw;
        }

        ConfigChanged?.Invoke(Current);
    }

    public void SetHeaderModules(IEnumerable<string> modules)
    {
        var selected = modules.Distinct(StringComparer.Ordinal).ToList();
        if (selected.Any(id => !HeaderTelemetry.Ids.Contains(id, StringComparer.Ordinal)))
            throw new ArgumentException("Invalid header module.");
        var previous = Current.HeaderModules;
        Current.HeaderModules = selected;
        try
        {
            WriteConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Current.HeaderModules = previous;
            throw;
        }
        ConfigChanged?.Invoke(Current);
    }

    public void SetSpeechRate(int rate)
    {
        if (rate is < -5 or > 5)
            throw new ArgumentOutOfRangeException(nameof(rate));
        int previous = Current.SpeechRate;
        Current.SpeechRate = rate;
        try
        {
            WriteConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Current.SpeechRate = previous;
            throw;
        }
        ConfigChanged?.Invoke(Current);
    }

    public void SetGamePaths(string journalFolder, string bindingsFolder, string selectedBindsFile)
    {
        var previous = (Current.JournalFolderPath, Current.BindingsFolderPath, Current.SelectedBindsFile);
        Current.JournalFolderPath = journalFolder;
        Current.BindingsFolderPath = bindingsFolder;
        Current.SelectedBindsFile = selectedBindsFile;
        try
        {
            WriteConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            (Current.JournalFolderPath, Current.BindingsFolderPath, Current.SelectedBindsFile) = previous;
            throw;
        }
        ConfigChanged?.Invoke(Current);
    }

    public void SetWebSettings(int port, string mode, string vpnInterfaceId)
    {
        if (port is < 1025 or > 65534)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (mode is not ("local" or "open" or "code" or "vpn"))
            throw new ArgumentException("Invalid web access mode.", nameof(mode));
        if (mode == "vpn" && string.IsNullOrWhiteSpace(vpnInterfaceId))
            throw new ArgumentException("A VPN interface is required.", nameof(vpnInterfaceId));

        var previous = (Current.WebServerPort, Current.WebAccessMode, Current.WebVpnInterfaceId);
        Current.WebServerPort = port;
        Current.WebAccessMode = mode;
        Current.WebVpnInterfaceId = vpnInterfaceId;
        try
        {
            WriteConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            (Current.WebServerPort, Current.WebAccessMode, Current.WebVpnInterfaceId) = previous;
            throw;
        }
        ConfigChanged?.Invoke(Current);
    }

    public void SetWebServerEnabled(bool enabled)
    {
        bool previous = Current.AutoStartWebServer;
        Current.AutoStartWebServer = enabled;
        try
        {
            WriteConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Current.AutoStartWebServer = previous;
            throw;
        }
        ConfigChanged?.Invoke(Current);
    }

    public void SetAlertSoundDirectories(IEnumerable<string> directories)
    {
        var selected = directories.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var previous = Current.AlertSoundDirectories;
        Current.AlertSoundDirectories = selected;
        try
        {
            WriteConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Current.AlertSoundDirectories = previous;
            throw;
        }
        ConfigChanged?.Invoke(Current);
    }

    public void SaveChecked()
    {
        WriteConfig();
        ApplyThemeToApplication();
        ConfigChanged?.Invoke(Current);
    }

    private readonly object _writeLock = new();

    /// <summary>
    /// Serializes the configuration and writes it atomically (temp file + replace) so a crash or a
    /// concurrent save can never leave a truncated config.json. The previous version is kept as config.json.bak.
    /// </summary>
    private void WriteConfig()
    {
        lock (_writeLock)
        {
            string json = JsonSerializer.Serialize(Current, AppConfigJsonContext.Default.AppConfig);
            string tempPath = _configPath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(_configPath))
                File.Replace(tempPath, _configPath, _configPath + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(tempPath, _configPath);
        }
    }

    private static SolidColorBrush CreateFrozenBrush(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void ApplyThemeToApplication()
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;

        // Never block a background caller (web endpoints, timers) on the UI thread: marshal asynchronously.
        if (app.Dispatcher.CheckAccess())
            ApplyThemeCore();
        else
            app.Dispatcher.BeginInvoke(ApplyThemeCore);
    }

    private void ApplyThemeCore()
    {
        {
            try
            {
                var accentColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Current.ThemeAccentHex);
                var secondaryColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Current.ThemeSecondaryHex);

                var dimColor = System.Windows.Media.Color.FromArgb(45, accentColor.R, accentColor.G, accentColor.B);
                var borderColor = System.Windows.Media.Color.FromArgb(140, accentColor.R, accentColor.G, accentColor.B);
                var glowColor = System.Windows.Media.Color.FromArgb(90, accentColor.R, accentColor.G, accentColor.B);

                var res = System.Windows.Application.Current.Resources;
                res["HudAccentColor"] = accentColor;
                res["HudAccentBrush"] = CreateFrozenBrush(accentColor);
                res["HudSecondaryBrush"] = CreateFrozenBrush(secondaryColor);
                res["HudAccentDimBrush"] = CreateFrozenBrush(dimColor);
                res["HudBorderBrush"] = CreateFrozenBrush(borderColor);
                res["HudGlowBrush"] = CreateFrozenBrush(glowColor);
            }
            catch (Exception ex)
            {
                // Invalid color, keep the current one
                AppDiagnosticsLogger.Warn("ConfigService", $"Invalid HUD theme color HEX: {ex.Message}");
            }
        }
    }
}

