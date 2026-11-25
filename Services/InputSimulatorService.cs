// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/InputSimulatorService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Low-level Windows SendInput keyboard/macro dispatcher targeting Elite Dangerous.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using HUDnopy.Models;
using HUDnopy.Localization;

namespace HUDnopy.Services;

/// <summary>
/// Low-level Windows SendInput keyboard/macro dispatcher targeting Elite Dangerous.
/// </summary>
public class InputSimulatorService
{
    private readonly BindingsService _bindingsService;
    private readonly ConfigService _configService;

    public Func<bool> IsGameDetected { get; set; } = static () => false;
    public event Action<string, string>? CommandExecuted;

    public InputSimulatorService(BindingsService bindingsService, ConfigService configService)
    {
        _bindingsService = bindingsService;
        _configService = configService;
    }

    public async Task<(bool Success, string Message)> ExecuteActionAsync(string actionId)
    {
        var language = _configService.Current.InterfaceLanguage;
        var text = InputTranslations.ForLanguage(language);
        if (!IsGameDetected())
            return (false, text["gameInactive"]);

        if (actionId.StartsWith("Macro:", StringComparison.OrdinalIgnoreCase))
            return await ExecuteMacroAsync(actionId, language, text);

        if (actionId.Contains(',', StringComparison.Ordinal) ||
            actionId.StartsWith("Seq:", StringComparison.OrdinalIgnoreCase))
            return await ExecuteInlineSequenceAsync(actionId, language, text);

        var binding = _bindingsService.Bindings.FirstOrDefault(b =>
            string.Equals(b.ActionId, actionId, StringComparison.OrdinalIgnoreCase));

        if (binding == null)
        {
            return (false, string.Format(text["actionMissing"], actionId));
        }

        var presentation = DeckTranslations.Present(binding, language);
        string keyToSend = "";
        List<string> modifiers = new();

        if (binding.PrimaryDevice == "Keyboard" && !string.IsNullOrEmpty(binding.PrimaryKey))
        {
            keyToSend = binding.PrimaryKey;
            modifiers = binding.PrimaryModifiers;
        }
        else if (binding.SecondaryDevice == "Keyboard" && !string.IsNullOrEmpty(binding.SecondaryKey))
        {
            keyToSend = binding.SecondaryKey;
            modifiers = binding.SecondaryModifiers;
        }

        if (string.IsNullOrEmpty(keyToSend))
        {
            return (false, string.Format(text["noKey"], presentation.DisplayName));
        }

        if (!TryGetVirtualKey(keyToSend, out ushort mainVk, out bool mainExtended))
        {
            return (false, string.Format(text["unknownKey"], keyToSend));
        }

        if (_configService.Current.FocusGameWindowOnInput)
        {
            var (focused, focusMessage) = await FocusEliteDangerousWindowAsync(text);
            if (!focused)
                return (false, focusMessage);
        }

        var modVks = new List<(ushort Vk, bool Extended)>();
        foreach (var mod in modifiers)
        {
            if (TryGetVirtualKey(mod, out ushort mVk, out bool mExt))
            {
                modVks.Add((mVk, mExt));
            }
        }

        // Pulsar modificadores
        var pressedModifiers = new List<(ushort Vk, bool Extended)>();
        foreach (var (vk, ext) in modVks)
        {
            if (!SendKey(vk, ext, keyUp: false, out int error))
            {
                string releaseError = ReleaseModifiers(pressedModifiers, text);
                return (false, FormatInputError(string.Format(text["inputRejected"], error), releaseError));
            }
            pressedModifiers.Add((vk, ext));
        }

        if (modVks.Count > 0)
        {
            await Task.Delay(25);
        }

        // Pulsar tecla principal y mantener 55ms para que DirectInput de Elite Dangerous lo detecte
        if (!SendKey(mainVk, mainExtended, keyUp: false, out int keyDownError))
        {
            string releaseError = ReleaseModifiers(pressedModifiers, text);
            return (false, FormatInputError(string.Format(text["inputRejected"], keyDownError), releaseError));
        }

        await Task.Delay(55);
        bool keyReleased = SendKey(mainVk, mainExtended, keyUp: true, out int keyUpError);

        // Soltar modificadores en orden inverso
        string modifierReleaseError = ReleaseModifiers(pressedModifiers, text);
        if (!keyReleased)
        {
            return (false, FormatInputError(string.Format(text["releaseKey"], keyUpError), modifierReleaseError));
        }
        if (!string.IsNullOrEmpty(modifierReleaseError))
            return (false, modifierReleaseError);

        CommandExecuted?.Invoke(binding.ActionId, binding.DisplayName);
        return (true, string.Format(text["sent"], presentation.DisplayName, presentation.FormattedBind));
    }

    private async Task<(bool Success, string Message)> ExecuteMacroAsync(
        string actionId,
        string language,
        IReadOnlyDictionary<string, string> text)
    {
        string[] steps = actionId switch
        {
            "Macro:CombatPosture" => new[] { "ResetPowerDistribution", "IncreaseSystemsPower", "IncreaseSystemsPower", "IncreaseWeaponsPower", "IncreaseWeaponsPower", "DeployHardpointToggle" },
            "Macro:EscapePosture" => new[] { "ResetPowerDistribution", "IncreaseSystemsPower", "IncreaseSystemsPower", "IncreaseEnginesPower", "IncreaseEnginesPower", "UseBoostJuice" },
            "Macro:ExplorationPosture" => new[] { "ResetPowerDistribution", "IncreaseEnginesPower", "IncreaseEnginesPower", "PlayerHUDModeToggle" },
            "Macro:SilentEscape" => new[] { "ToggleButtonUpInput", "DeployHeatSink", "UseBoostJuice" },
            _ => (_configService.Current.CustomMacros ?? Enumerable.Empty<DeckMacroDefinition>())
                .FirstOrDefault(m => string.Equals(
                    m.Id.StartsWith("Macro:", StringComparison.OrdinalIgnoreCase) ? m.Id : "Macro:" + m.Id,
                    actionId,
                    StringComparison.OrdinalIgnoreCase))?.Steps
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? Array.Empty<string>()
        };

        if (steps.Length == 0)
            return (false, string.Format(text["actionMissing"], actionId));

        foreach (string step in steps)
        {
            if (step.StartsWith("Macro:", StringComparison.OrdinalIgnoreCase))
                continue;
            var (ok, msg) = await ExecuteActionAsync(step);
            if (!ok)
                return (false, msg);
            await Task.Delay(90);
        }

        string label = _bindingsService.Bindings.FirstOrDefault(b =>
            string.Equals(b.ActionId, actionId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? actionId;
        CommandExecuted?.Invoke(actionId, label);
        return (true, string.Format(text["sent"], label, "MACRO"));
    }

    private async Task<(bool Success, string Message)> ExecuteInlineSequenceAsync(
        string sequence,
        string language,
        IReadOnlyDictionary<string, string> text)
    {
        string raw = sequence.StartsWith("Seq:", StringComparison.OrdinalIgnoreCase)
            ? sequence[4..]
            : sequence;
        string[] steps = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (steps.Length == 0)
            return (false, string.Format(text["actionMissing"], sequence));

        foreach (string step in steps.Take(16))
        {
            if (step.StartsWith("DELAY:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(step[6..].Trim(), out int ms))
                    await Task.Delay(Math.Clamp(ms, 20, 3000));
                continue;
            }
            if (step.Contains(',', StringComparison.Ordinal) || step.StartsWith("Seq:", StringComparison.OrdinalIgnoreCase))
                continue;
            var (ok, msg) = await ExecuteActionAsync(step);
            if (!ok)
                return (false, msg);
            await Task.Delay(90);
        }

        CommandExecuted?.Invoke(sequence, "Custom Macro");
        return (true, string.Format(text["sent"], "Custom Macro", "MACRO"));
    }

    private static async Task<(bool Success, string Message)> FocusEliteDangerousWindowAsync(IReadOnlyDictionary<string, string> text)
    {
        IntPtr windowHandle = FindEliteDangerousWindow();
        if (windowHandle == IntPtr.Zero)
        {
            return (false, text["gameWindowMissing"]);
        }

        if (GetForegroundWindow() == windowHandle && !IsIconic(windowHandle))
        {
            return (true, "");
        }

        if (IsIconic(windowHandle))
            ShowWindow(windowHandle, SW_RESTORE);

        SetForegroundWindow(windowHandle);
        await Task.Delay(100);

        if (GetForegroundWindow() != windowHandle)
        {
            return (false, text["focusDenied"]);
        }

        return (true, "");
    }

    private static IntPtr _cachedWindowHandle;

    private static IntPtr FindEliteDangerousWindow()
    {
        IntPtr cached = _cachedWindowHandle;
        if (cached != IntPtr.Zero && IsWindow(cached))
            return cached;

        IntPtr found = IntPtr.Zero;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (found != IntPtr.Zero)
                    continue;

                try
                {
                    IntPtr windowHandle = process.MainWindowHandle;
                    if (windowHandle == IntPtr.Zero)
                        continue;

                    string processName = process.ProcessName;
                    string windowTitle = process.MainWindowTitle;
                    bool processMatches = EliteFileWatcherService.IsEliteProcessName(processName);
                    bool titleMatches =
                        windowTitle.Contains("Elite", StringComparison.OrdinalIgnoreCase) &&
                        windowTitle.Contains("Dangerous", StringComparison.OrdinalIgnoreCase);

                    if (processMatches || titleMatches)
                        found = windowHandle;
                }
                catch (InvalidOperationException)
                {
                    // El proceso pudo cerrarse mientras se enumeraban sus ventanas.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Windows may deny access to protected process information.
                }
            }
        }

        _cachedWindowHandle = found;
        return found;
    }

    private static string ReleaseModifiers(List<(ushort Vk, bool Extended)> pressedModifiers, IReadOnlyDictionary<string, string> text)
    {
        for (int i = pressedModifiers.Count - 1; i >= 0; i--)
        {
            var (vk, extended) = pressedModifiers[i];
            if (!SendKey(vk, extended, keyUp: true, out int error))
                return string.Format(text["releaseModifier"], error);
        }

        return "";
    }

    private static string FormatInputError(string message, string releaseError)
    {
        return string.IsNullOrEmpty(releaseError) ? message : $"{message} {releaseError}";
    }

    private static bool SendKey(ushort vk, bool isExtended, bool keyUp, out int error)
    {
        ushort scanCode = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
        uint flags = KEYEVENTF_SCANCODE;
        if (isExtended) flags |= KEYEVENTF_EXTENDEDKEY;
        if (keyUp) flags |= KEYEVENTF_KEYUP;

        INPUT[] inputs = new INPUT[1];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].U.ki.wVk = vk;
        inputs[0].U.ki.wScan = scanCode;
        inputs[0].U.ki.dwFlags = flags;
        inputs[0].U.ki.time = 0;
        inputs[0].U.ki.dwExtraInfo = IntPtr.Zero;

        bool sent = SendInput(1, inputs, Marshal.SizeOf<INPUT>()) == 1;
        error = sent ? 0 : Marshal.GetLastWin32Error();
        return sent;
    }

    private static bool TryGetVirtualKey(string edKey, out ushort vk, out bool extended)
    {
        extended = false;
        vk = 0;
        string clean = edKey.StartsWith("Key_", StringComparison.OrdinalIgnoreCase)
            ? edKey.Substring(4)
            : edKey;

        if (clean.Length == 1)
        {
            char c = char.ToUpperInvariant(clean[0]);
            if (c >= 'A' && c <= 'Z') { vk = c; return true; }
            if (c >= '0' && c <= '9') { vk = c; return true; }
        }

        switch (clean)
        {
            case "LeftShift": vk = 0xA0; return true;
            case "RightShift": vk = 0xA1; extended = true; return true;
            case "LeftControl": vk = 0xA2; return true;
            case "RightControl": vk = 0xA3; extended = true; return true;
            case "LeftAlt": vk = 0xA4; return true;
            case "RightAlt": vk = 0xA5; extended = true; return true;
            case "Space": vk = 0x20; return true;
            case "Enter": vk = 0x0D; return true;
            case "Backspace": vk = 0x08; return true;
            case "Tab": vk = 0x09; return true;
            case "Escape": vk = 0x1B; return true;
            case "Insert": vk = 0x2D; extended = true; return true;
            case "Delete": vk = 0x2E; extended = true; return true;
            case "Home": vk = 0x24; extended = true; return true;
            case "End": vk = 0x23; extended = true; return true;
            case "PageUp": vk = 0x21; extended = true; return true;
            case "PageDown": vk = 0x22; extended = true; return true;
            case "UpArrow": vk = 0x26; extended = true; return true;
            case "DownArrow": vk = 0x28; extended = true; return true;
            case "LeftArrow": vk = 0x25; extended = true; return true;
            case "RightArrow": vk = 0x27; extended = true; return true;
            case "Equals": vk = 0xBB; return true;
            case "Minus": vk = 0xBD; return true;
            case "Comma": vk = 0xBC; return true;
            case "Period": vk = 0xBE; return true;
            case "Slash": vk = 0xBF; return true;
            case "SemiColon": vk = 0xBA; return true;
            case "Apostrophe": vk = 0xDE; return true;
            case "LeftBracket": vk = 0xDB; return true;
            case "RightBracket": vk = 0xDD; return true;
            case "BackSlash": vk = 0xDC; return true;
            case "Grave": vk = 0xC0; return true;
            case "Numpad_0": vk = 0x60; return true;
            case "Numpad_1": vk = 0x61; return true;
            case "Numpad_2": vk = 0x62; return true;
            case "Numpad_3": vk = 0x63; return true;
            case "Numpad_4": vk = 0x64; return true;
            case "Numpad_5": vk = 0x65; return true;
            case "Numpad_6": vk = 0x66; return true;
            case "Numpad_7": vk = 0x67; return true;
            case "Numpad_8": vk = 0x68; return true;
            case "Numpad_9": vk = 0x69; return true;
            case "Numpad_Add": vk = 0x6B; return true;
            case "Numpad_Subtract": vk = 0x6D; return true;
            case "Numpad_Multiply": vk = 0x6A; return true;
            case "Numpad_Divide": vk = 0x6F; extended = true; return true;
            case "Numpad_Enter": vk = 0x0D; extended = true; return true;
            case "F1": vk = 0x70; return true;
            case "F2": vk = 0x71; return true;
            case "F3": vk = 0x72; return true;
            case "F4": vk = 0x73; return true;
            case "F5": vk = 0x74; return true;
            case "F6": vk = 0x75; return true;
            case "F7": vk = 0x76; return true;
            case "F8": vk = 0x77; return true;
            case "F9": vk = 0x78; return true;
            case "F10": vk = 0x79; return true;
            case "F11": vk = 0x7A; return true;
            case "F12": vk = 0x7B; return true;
            default:
                return false;
        }
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint MAPVK_VK_TO_VSC = 0;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
        [FieldOffset(0)]
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
