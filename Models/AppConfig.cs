// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Models/AppConfig.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Persistent user configuration model serialized to config.json.
// -----------------------------------------------------------------------------

using System;
using System.IO;
using System.Text.Json.Serialization;

namespace HUDnopy.Models;

/// <summary>
/// Persistent user configuration model serialized to config.json.
/// </summary>
public class AppConfig
{
    public int WebServerPort { get; set; } = 5050;
    public bool AutoStartWebServer { get; set; } = true;
    public string WebAccessMode { get; set; } = "local";
    public string WebVpnInterfaceId { get; set; } = "";
    public string JournalFolderPath { get; set; } = GetDefaultJournalPath();
    public string BindingsFolderPath { get; set; } = GetDefaultBindingsPath();
    public string SelectedBindsFile { get; set; } = ""; // Empty = auto-detect from StartPreset
    public string ThemeName { get; set; } = "Naranja HUD (Clásico)";
    public string ThemeAccentHex { get; set; } = "#FF7100";
    public string ThemeSecondaryHex { get; set; } = "#FFB300";
    public bool FocusGameWindowOnInput { get; set; } = true;
    public bool HideUnassignedBindings { get; set; }
    public bool HideNavigationMenuText { get; set; }
    public string InterfaceLanguage { get; set; } = "en-GB";
    public bool EnableSpanshSystemData { get; set; }
    public int DesktopFontScalePercent { get; set; } = 100;
    public bool StartMinimizedToTray { get; set; }
    public string NavigationMenuPosition { get; set; } = "left";
    public List<string> HeaderModules { get; set; } = ["system", "credits"];
    public int GameProcessExitGraceSeconds { get; set; } = 15;
    public int FuelLowThresholdPercent { get; set; } = 20;
    public int FuelCriticalThresholdPercent { get; set; } = 10;
    public int HullCriticalThresholdPercent { get; set; } = 50;
    public int MissionExpiryWarningHours { get; set; } = 24;
    public int MissionExpiryCriticalHours { get; set; } = 1;
    public int AlertSoundVolumePercent { get; set; } = 100;
    public List<string> AlertSoundDirectories { get; set; } = [];
    public string SpeechVoiceName { get; set; } = "";
    public string SpeechEngine { get; set; } = "sapi"; // "sapi" | "piper"
    public string PiperVoiceId { get; set; } = "auto";
    public int SpeechRate { get; set; }
    public string SpeechQueueMode { get; set; } = "tactical";
    public int SpeechQueueExpirationSeconds { get; set; } = 5;
    public bool CaptureUnhandledEvents { get; set; } = true;
    public bool WriteUnhandledEventsToFile { get; set; } = true;
    public bool GroupUnhandledEvents { get; set; } = true;
    public Dictionary<string, int> MaterialWishlist { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string MiningTargetMinerals { get; set; } = "Platinum, Osmium, Painite, LowTemperatureDiamond, Opal, Monazite, Musgravite, Alexandrite, Benitoite, Grandidierite, Rhodplumsite, Serendibite";
    public int MiningMinPercent { get; set; } = 25;
    public List<SurfaceBookmarkItem> SurfaceBookmarks { get; set; } = new();
    public bool AutoContextDeck { get; set; }
    public List<DeckMacroDefinition> CustomMacros { get; set; } = new();
    public Dictionary<string, string> SystemNotes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool EnableEddnUpload { get; set; }
    public bool EnableEdsmSync { get; set; }
    public string EdsmCommanderName { get; set; } = "";
    public string EdsmApiKey { get; set; } = "";
    public bool EnableInaraSync { get; set; }
    public string InaraCommanderName { get; set; } = "";
    public string InaraApiKey { get; set; } = "";
    public bool EnableFrontierCapi { get; set; }
    public string FrontierCapiClientId { get; set; } = "";
    public int FrontierCapiCallbackPort { get; set; } = 5055;
    public List<string> EngineeringWishlistBlueprints { get; set; } = new();
    public bool EnableAiCopilot { get; set; }
    public string AiActiveProvider { get; set; } = "ollama"; // "ollama" | "gemini" | "openai"
    public string AiOllamaEndpoint { get; set; } = "http://localhost:11434/v1";
    public string AiOllamaModel { get; set; } = "qwen2.5:7b";
    public string AiGeminiApiKey { get; set; } = "";
    public string AiGeminiModel { get; set; } = "gemini-flash-lite-latest";
    public string AiOpenAiEndpoint { get; set; } = "https://api.groq.com/openai/v1";
    public string AiOpenAiApiKey { get; set; } = "";
    public string AiOpenAiModel { get; set; } = "llama-3.3-70b-versatile";
    public string AiPersonality { get; set; } = "covas"; // "covas" | "tactical" | "scientist" | "sarcastic"
    public bool AiSpeakReplies { get; set; } = true;
    public bool AiAllowActions { get; set; } = true;
    public bool AiDynamicEvents { get; set; }
    public int AiDynamicEventCooldownSeconds { get; set; } = 45;
    public bool AiRequireWakeWord { get; set; }
    public string AiWakeWord { get; set; } = "COVAS";
    public string AiCustomPersonality { get; set; } = "";
    public string AiVerbosity { get; set; } = "normal";
    public string AiThinkingMode { get; set; } = "auto"; // "no" | "auto" | "yes"
    public bool AiSystemArrivalBriefing { get; set; }
    public bool AiGlobalPttHotkeyEnabled { get; set; }
    public string AiGlobalPttHotkey { get; set; } = "Ctrl+Alt+V";
    public bool AiTacticalHighGravity { get; set; } = true;
    public bool AiTacticalValuableSignals { get; set; } = true;
    public bool AiTacticalMiningProspector { get; set; } = true;
    public bool AiTacticalRadioChatter { get; set; }
    public bool AiVoiceMacrosEnabled { get; set; } = true;
    public List<VoiceMacroDefinition> AiVoiceMacros { get; set; } = VoiceMacroDefinition.CreateDefaults();
    public List<string> AiCommanderMemory { get; set; } = new();
    public Dictionary<string, AlertChannelPreferences> AlertPreferences { get; set; } =
        AlertDefinitions.CreateDefaultPreferences();

    [JsonPropertyName("StatusFileFreshnessSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyStatusFileFreshnessSeconds
    {
        get => null;
        set
        {
            if (value.HasValue)
                GameProcessExitGraceSeconds = value.Value;
        }
    }

    public static string GetDefaultJournalPath()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Saved Games", "Frontier Developments", "Elite Dangerous");
    }

    public static string GetDefaultBindingsPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Frontier Developments", "Elite Dangerous", "Options", "Bindings");
    }
}

public class HudThemePreset
{
    public string Name { get; set; } = "";
    public string AccentHex { get; set; } = "#FF7100";
    public string SecondaryHex { get; set; } = "#FFB300";
}

/// <summary>
/// Instant zero-LLM voice macro mapping spoken trigger phrases to keyboard/macro action sequences.
/// </summary>
public class VoiceMacroDefinition
{
    public string TriggerPhrase { get; set; } = "";
    public string Steps { get; set; } = "";
    public string SpokenConfirmation { get; set; } = "";

    public static List<VoiceMacroDefinition> CreateDefaults() =>
    [
        new()
        {
            TriggerPhrase = "modo combate, postura de combate, combat mode, combat posture",
            Steps = "Macro:CombatPosture",
            SpokenConfirmation = "Postura de combate activada."
        },
        new()
        {
            TriggerPhrase = "evasión máxima, modo escape, escape mode, evasive maneuvers",
            Steps = "Macro:EscapePosture",
            SpokenConfirmation = "Maniobra de evasión máxima iniciada."
        },
        new()
        {
            TriggerPhrase = "escape silencioso, modo sigilo, silent escape, silent running",
            Steps = "Macro:SilentEscape",
            SpokenConfirmation = "Navegación silenciosa y eyector térmico activados."
        },
        new()
        {
            TriggerPhrase = "preparar aterrizaje, landing prep, tren de aterrizaje",
            Steps = "SetSpeedZero, DELAY:150, LandingGearToggle, DELAY:150, ShipSpotLightToggle",
            SpokenConfirmation = "Secuencia de aproximación y tren de aterrizaje ejecutada."
        }
    ];
}
