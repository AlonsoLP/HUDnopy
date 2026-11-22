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
        try
        {
            if (File.Exists(_configPath))
            {
                string json = File.ReadAllText(_configPath);
                var cfg = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
                if (cfg != null)
                {
                    Current = cfg;
                    return cfg;
                }
            }
        }
        catch
        {
            // Fall back to default configuration if reading fails
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
        catch
        {
            // Ignorar errores de escritura menores
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

    private void WriteConfig()
    {
        string json = JsonSerializer.Serialize(Current, AppConfigJsonContext.Default.AppConfig);
        File.WriteAllText(_configPath, json);
    }

    private static SolidColorBrush CreateFrozenBrush(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void ApplyThemeToApplication()
    {
        if (System.Windows.Application.Current == null) return;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
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
            catch
            {
                // Invalid color, keep the current one
            }
        });
    }
}

