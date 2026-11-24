// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/BindingsService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Parser for Elite Dangerous .binds XML control profiles and action catalogs.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using HUDnopy.Models;

namespace HUDnopy.Services;

/// <summary>
/// Parser for Elite Dangerous .binds XML control profiles and action catalogs.
/// </summary>
public class BindingsService
{
    private readonly ConfigService _configService;
    public List<GameBinding> Bindings { get; private set; } = new();
    public string ActiveBindsFilePath { get; private set; } = "No detectado";
    public string ActivePresetName { get; private set; } = "Estándar (Por defecto)";
    public bool UsingDefaultBindings { get; private set; }

    public event Action<List<GameBinding>>? BindingsUpdated;

    private record KnownAction(string ActionId, string DisplayName, string Category, string IconGlyph, string DefaultKey, string? DefaultMod = null);

    private static readonly List<KnownAction> KnownActions = new()
    {
        // Flight and Navigation
        new("Supercruise", "Supercrucero", "Vuelo y Navegación", "🚀", "Key_J"),
        new("HyperSuperCombination", "Salto Hiperespacial / FSD", "Vuelo y Navegación", "✦", "Key_J", "Key_LeftShift"),
        new("TargetNextRouteSystem", "Siguiente Sistema en Ruta", "Vuelo y Navegación", "🧭", "Key_N"),
        new("ToggleFlightAssist", "Asistencia de Vuelo (FA Off/On)", "Vuelo y Navegación", "🔄", "Key_Z"),
        new("UseBoostJuice", "Impulsores (Boost)", "Vuelo y Navegación", "⚡", "Key_Tab"),
        new("SetSpeedZero", "Velocidad 0% (Parada)", "Vuelo y Navegación", "⏹", "Key_X"),
        new("SetSpeed50", "Velocidad 50% (Zona Azul)", "Vuelo y Navegación", "▶", "Key_Numpad_5"),
        new("SetSpeed75", "Velocidad 75% (Aproximación)", "Vuelo y Navegación", "⏩", "Key_Numpad_7"),
        new("SetSpeed100", "Velocidad 100% (Máxima)", "Vuelo y Navegación", "⏫", "Key_Numpad_8"),
        new("OrbitLinesToggle", "Líneas Orbitales", "Vuelo y Navegación", "🌐", "Key_Equals"),

        // Sistemas de Nave
        new("LandingGearToggle", "Tren de Aterrizaje", "Sistemas de Nave", "🛬", "Key_L"),
        new("ToggleCargoScoop", "Colector de Carga (Cargo Scoop)", "Sistemas de Nave", "📦", "Key_Home"),
        new("ShipSpotLightToggle", "Luces de la Nave", "Sistemas de Nave", "💡", "Key_Insert"),
        new("NightVisionToggle", "Visión Nocturna", "Sistemas de Nave", "👁", "Key_Insert", "Key_LeftAlt"),
        new("PlayerHUDModeToggle", "Modo HUD (Combate / Análisis)", "Sistemas de Nave", "🎛", "Key_M"),
        new("ToggleButtonUpInput", "Navegación Silenciosa (Silent Running)", "Sistemas de Nave", "🔇", "Key_Delete"),

        // Combate y Defensas
        new("DeployHardpointToggle", "Desplegar / Plegar Anclajes", "Combate y Defensas", "⚔", "Key_U"),
        new("DeployHeatSink", "Eyector Térmico (Heatsink)", "Combate y Defensas", "❄", "Key_V"),
        new("UseShieldCell", "Célula de Escudo (SCB)", "Combate y Defensas", "🛡", "Key_C"),
        new("FireChaffLauncher", "Lanzador de Señuelos (Chaff)", "Combate y Defensas", "✨", "Key_B"),
        new("SelectTargetsThreat", "Seleccionar Mayor Amenaza", "Combate y Defensas", "🎯", "Key_H"),
        new("CycleNextTarget", "Siguiente Objetivo", "Combate y Defensas", "⏭", "Key_G"),
        new("CycleFireGroupNext", "Siguiente Grupo de Acción", "Combate y Defensas", "🔫", "Key_N"),

        // Power (Pips)
        new("IncreaseSystemsPower", "Energía a Sistemas (SYS +)", "Energía (Pips)", "🛡", "Key_LeftArrow"),
        new("IncreaseEnginesPower", "Energía a Motores (ENG +)", "Energía (Pips)", "🚀", "Key_UpArrow"),
        new("IncreaseWeaponsPower", "Energía a Armas (WEP +)", "Energía (Pips)", "⚔", "Key_RightArrow"),
        new("ResetPowerDistribution", "Equilibrar Energía (RST)", "Energía (Pips)", "⚖", "Key_DownArrow"),

        // Paneles e Interfaz
        new("GalaxyMapOpen", "Mapa de la Galaxia", "Paneles e Interfaz", "🌌", "Key_1", "Key_LeftShift"),
        new("SystemMapOpen", "Mapa del Sistema", "Paneles e Interfaz", "🪐", "Key_2", "Key_LeftShift"),
        new("ExplorationFSSEnter", "Escáner Espectro Completo (FSS)", "Paneles e Interfaz", "📡", "Key_Apostrophe"),
        new("FocusLeftPanel", "Panel Izquierdo (Navegación)", "Paneles e Interfaz", "◧", "Key_1"),
        new("FocusCommsPanel", "Panel Superior (Comunicaciones)", "Paneles e Interfaz", "💬", "Key_2"),
        new("FocusRadarPanel", "Panel Inferior (Rol / VRE)", "Paneles e Interfaz", "◩", "Key_3"),
        new("FocusRightPanel", "Panel Derecho (Sistemas / Nave)", "Paneles e Interfaz", "◨", "Key_4"),

        // SRV Vehicle
        new("AutoBreakBuggyButton", "Freno de mano SRV", "Vehículo SRV", "🅿", "Key_X"),
        new("HeadlightsBuggyButton", "Luces SRV", "Vehículo SRV", "💡", "Key_Insert"),
        new("ToggleBuggyTurretButton", "Torreta SRV", "Vehículo SRV", "🎯", "Key_U"),
        new("ToggleDriveAssist", "Asistencia de conducción SRV", "Vehículo SRV", "🔄", "Key_Z"),
        new("ToggleCargoScoop_Buggy", "Colector de carga SRV", "Vehículo SRV", "📦", "Key_Home"),
        new("RecallDismissShip", "Llamar / Despedir Nave", "Vehículo SRV", "🚀", ""),

        // A Pie (Odyssey)
        new("HumanoidToggleFlashlightButton", "Linterna del traje", "A Pie (Odyssey)", "🔦", "Key_T"),
        new("HumanoidToggleNightVisionButton", "Visión nocturna traje", "A Pie (Odyssey)", "👁", "Key_N"),
        new("HumanoidToggleShieldsButton", "Escudos del traje", "A Pie (Odyssey)", "🛡", "Key_C"),
        new("HumanoidSwitchWeaponButton", "Cambiar arma", "A Pie (Odyssey)", "🔫", "Key_1"),
        new("HumanoidOpenAccessPanelButton", "Rueda de acceso", "A Pie (Odyssey)", "⚙", "Key_Q"),
        new("HumanoidClearAuthorityLevel", "Limpiar perfil escaneado", "A Pie (Odyssey)", "🧹", ""),

        // DSS y Caza (SLF)
        new("ExplorationSAAExitThirdPerson", "Salir de escáner DSS", "DSS y Caza (SLF)", "📡", "Key_Backspace"),
        new("ExplorationSAAChangeScannedAreaViewToggle", "Alternar vista DSS", "DSS y Caza (SLF)", "🪐", "Key_M"),
        new("OrderAggressiveBehaviour", "Caza: Atacar objetivo", "DSS y Caza (SLF)", "⚔", ""),
        new("OrderDefensiveBehaviour", "Caza: Defender nave", "DSS y Caza (SLF)", "🛡", ""),
        new("OrderHoldPosition", "Caza: Mantener posición", "DSS y Caza (SLF)", "⏹", ""),
        new("OrderRequestDock", "Caza: Solicitar atraque", "DSS y Caza (SLF)", "🛬", ""),

        // Tactical Macros
        new("Macro:CombatPosture", "Macro: Postura de combate", "Macros Tácticas", "⚔", "MACRO"),
        new("Macro:EscapePosture", "Macro: Evasión máxima", "Macros Tácticas", "⚡", "MACRO"),
        new("Macro:ExplorationPosture", "Macro: Modo exploración", "Macros Tácticas", "🔭", "MACRO"),
        new("Macro:SilentEscape", "Macro: Escape silencioso", "Macros Tácticas", "❄", "MACRO")
    };

    public BindingsService(ConfigService configService)
    {
        _configService = configService;
        ReloadBindings();
    }

    public void ReloadBindings()
    {
        var list = KnownActions.Select(ka => new GameBinding
        {
            ActionId = ka.ActionId,
            DisplayName = ka.DisplayName,
            Category = ka.Category,
            IconGlyph = ka.IconGlyph,
            PrimaryDevice = "Keyboard",
            PrimaryKey = ka.DefaultKey,
            PrimaryModifiers = ka.DefaultMod != null ? new List<string> { ka.DefaultMod } : new List<string>()
        }).ToList();

        string? bindsFile = ResolveActiveBindsFile();
        if (!string.IsNullOrEmpty(bindsFile) && File.Exists(bindsFile))
        {
            UsingDefaultBindings = false;
            ActiveBindsFilePath = bindsFile;
            ParseBindsXml(bindsFile, list);
        }
        else
        {
            UsingDefaultBindings = true;
            ActiveBindsFilePath = "Usando perfil estándar (sin archivo .binds detectado)";
            ActivePresetName = "Teclado Estándar ED";
        }

        foreach (var macro in _configService.Current.CustomMacros ?? Enumerable.Empty<DeckMacroDefinition>())
        {
            if (string.IsNullOrWhiteSpace(macro.Id) || string.IsNullOrWhiteSpace(macro.Name))
                continue;
            string id = macro.Id.StartsWith("Macro:", StringComparison.OrdinalIgnoreCase) ? macro.Id : "Macro:" + macro.Id;
            if (list.Any(b => string.Equals(b.ActionId, id, StringComparison.OrdinalIgnoreCase)))
                continue;
            list.Add(new GameBinding
            {
                ActionId = id,
                DisplayName = macro.Name,
                Category = "Macros Tácticas",
                IconGlyph = "⚙",
                PrimaryDevice = "Macro",
                PrimaryKey = "MACRO"
            });
        }

        Bindings = list;
        BindingsUpdated?.Invoke(Bindings);
    }

    private string? ResolveActiveBindsFile()
    {
        string folder = _configService.Current.BindingsFolderPath;
        if (!string.IsNullOrWhiteSpace(_configService.Current.SelectedBindsFile) &&
            File.Exists(_configService.Current.SelectedBindsFile))
        {
            return _configService.Current.SelectedBindsFile;
        }

        if (!Directory.Exists(folder)) return null;

        try
        {
            // Intentar leer StartPreset.4.start o StartPreset.start
            string[] startFiles = { "StartPreset.4.start", "StartPreset.start" };
            foreach (var sf in startFiles)
            {
                string startPath = Path.Combine(folder, sf);
                if (File.Exists(startPath))
                {
                    var lines = File.ReadAllLines(startPath)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Select(l => l.Trim())
                        .ToList();

                    foreach (var presetName in lines)
                    {
                        var matching = Directory.GetFiles(folder, $"{presetName}*.binds")
                            .OrderByDescending(File.GetLastWriteTimeUtc)
                            .FirstOrDefault();
                        if (matching != null) return matching;
                    }
                }
            }

            // If not found via StartPreset, pick the most recently modified .binds file
            return Directory.GetFiles(folder, "*.binds")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("Bindings", $"Failed to resolve active .binds file in '{folder}': {ex.Message}");
            return null;
        }
    }

    private void ParseBindsXml(string filePath, List<GameBinding> targetList)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var doc = XDocument.Load(stream);
            var root = doc.Root;
            if (root == null) return;

            ActivePresetName = root.Attribute("PresetName")?.Value ?? Path.GetFileNameWithoutExtension(filePath);
            var dict = targetList.ToDictionary(b => b.ActionId, StringComparer.OrdinalIgnoreCase);

            foreach (var element in root.Elements())
            {
                string actionName = element.Name.LocalName;
                var primary = element.Element("Primary");
                var secondary = element.Element("Secondary");

                if (primary == null && secondary == null) continue;

                if (!dict.TryGetValue(actionName, out var binding))
                {
                    // Only add extra actions if they have at least one button or key bound
                    string pDev = primary?.Attribute("Device")?.Value ?? "{NoDevice}";
                    string sDev = secondary?.Attribute("Device")?.Value ?? "{NoDevice}";
                    if (pDev == "{NoDevice}" && sDev == "{NoDevice}") continue;

                    binding = new GameBinding
                    {
                        ActionId = actionName,
                        DisplayName = FormatActionId(actionName),
                        Category = "Otros Controles del Juego",
                        IconGlyph = "⚙"
                    };
                    targetList.Add(binding);
                    dict[actionName] = binding;
                }

                if (primary != null)
                {
                    binding.PrimaryDevice = primary.Attribute("Device")?.Value ?? "";
                    binding.PrimaryKey = primary.Attribute("Key")?.Value ?? "";
                    binding.PrimaryModifiers = primary.Elements("Modifier")
                        .Select(m => m.Attribute("Key")?.Value ?? "")
                        .Where(k => !string.IsNullOrEmpty(k))
                        .ToList();
                }

                if (secondary != null)
                {
                    binding.SecondaryDevice = secondary.Attribute("Device")?.Value ?? "";
                    binding.SecondaryKey = secondary.Attribute("Key")?.Value ?? "";
                    binding.SecondaryModifiers = secondary.Elements("Modifier")
                        .Select(m => m.Attribute("Key")?.Value ?? "")
                        .Where(k => !string.IsNullOrEmpty(k))
                        .ToList();
                }
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("Bindings", $"Failed to parse '{Path.GetFileName(filePath)}': {ex.Message}");
        }
    }

    private static string FormatActionId(string actionId)
    {
        var sb = new System.Text.StringBuilder(actionId.Length + 4);
        for (int i = 0; i < actionId.Length; i++)
        {
            if (i > 0 && char.IsUpper(actionId[i]) && !char.IsUpper(actionId[i - 1]))
            {
                sb.Append(' ');
            }
            sb.Append(actionId[i]);
        }
        return sb.ToString();
    }
}

