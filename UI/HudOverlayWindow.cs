// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/HudOverlayWindow.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Transparent always-on-top Win32 click-through tactical HUD overlay
//              for Elite Dangerous Borderless Windowed mode.
// -----------------------------------------------------------------------------

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using HUDnopy.Models;
using HUDnopy.Services;

namespace HUDnopy.UI;

/// <summary>
/// Frameless translucent tactical HUD strip rendered over Elite Dangerous (Borderless Windowed).
/// Supports Win32 WS_EX_TRANSPARENT click-through locking and unlockable drag repositioning.
/// </summary>
public sealed class HudOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly ConfigService _configService;
    private readonly Border _shellBorder;
    private readonly TextBlock _txtHeader;
    private readonly TextBlock _txtVitals;
    private readonly TextBlock _txtContext;
    private readonly TextBlock _txtLockHint;
    private bool _clickThrough;

    public bool IsClickThrough => _clickThrough;

    public HudOverlayWindow(ConfigService configService)
    {
        _configService = configService;
        _clickThrough = configService.Current.OverlayClickThrough;

        Title = "HUDnopy Overlay";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Opacity = Math.Clamp(configService.Current.OverlayOpacity, 0.25, 1.0);

        if (configService.Current.OverlayLeft.HasValue && configService.Current.OverlayTop.HasValue)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = configService.Current.OverlayLeft.Value;
            Top = configService.Current.OverlayTop.Value;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 24;
            Top = 24;
        }

        _txtHeader = new TextBlock
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Text = "HUDNOPY // OVERLAY"
        };
        _txtHeader.SetResourceReference(TextBlock.ForegroundProperty, "HudAccentBrush");

        _txtLockHint = new TextBlock
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 10,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _txtLockHint.SetResourceReference(TextBlock.ForegroundProperty, "HudMutedBrush");

        var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(_txtLockHint, Dock.Right);
        headerRow.Children.Add(_txtLockHint);
        headerRow.Children.Add(_txtHeader);

        _txtVitals = new TextBlock
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Text = "SHD --- · HULL --- · FUEL ---"
        };
        _txtVitals.SetResourceReference(TextBlock.ForegroundProperty, "HudTextBrush");

        _txtContext = new TextBlock
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11.5,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed
        };
        _txtContext.SetResourceReference(TextBlock.ForegroundProperty, "HudSecondaryBrush");

        var stack = new StackPanel { MinWidth = 340, MaxWidth = 560 };
        stack.Children.Add(headerRow);
        stack.Children.Add(_txtVitals);
        stack.Children.Add(_txtContext);

        _shellBorder = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(205, 10, 14, 20)),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8),
            Child = stack
        };
        _shellBorder.SetResourceReference(Border.BorderBrushProperty, "HudAccentBrush");

        Content = _shellBorder;

        MouseLeftButtonDown += (_, _) =>
        {
            if (!_clickThrough)
            {
                try { DragMove(); } catch { }
            }
        };

        LocationChanged += (_, _) =>
        {
            if (IsLoaded && !_clickThrough)
            {
                _configService.Current.OverlayLeft = Left;
                _configService.Current.OverlayTop = Top;
            }
        };

        SourceInitialized += (_, _) => ApplyWin32ExtendedStyle();
        UpdateLockVisuals();
    }

    public void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        _configService.Current.OverlayClickThrough = enabled;
        ApplyWin32ExtendedStyle();
        UpdateLockVisuals();
    }

    public void UpdateTelemetry(ShipState s, string uiLanguage)
    {
        string sys = string.IsNullOrWhiteSpace(s.CurrentSystem) ? "NO SIGNAL" : s.CurrentSystem.ToUpperInvariant();
        string ship = string.IsNullOrWhiteSpace(s.ShipName) ? s.ShipType : s.ShipName;
        _txtHeader.Text = string.IsNullOrWhiteSpace(ship) ? $"◈ {sys}" : $"◈ {sys} · {ship}";

        string fuel = s.FuelMax > 0
            ? $"{Math.Clamp(s.FuelMain / s.FuelMax * 100.0, 0, 100):0}%"
            : "---";
        string hull = $"{Math.Clamp(s.HullHealth * 100.0, 0, 100):0}%";
        string shd = s.ShieldsUp ? "UP" : "DOWN";
        string pips = $"S{s.PipsSys:0.#} E{s.PipsEng:0.#} W{s.PipsWep:0.#}";
        _txtVitals.Text = $"SHD {shd} · HULL {hull} · FUEL {fuel} · {pips}";

        string context = "";
        if (s.AssignedLandingPad is int pad && pad > 0)
        {
            var padInfo = LandingPadLocatorService.Resolve(pad, s.AssignedLandingStationType);
            context = $"🛬 {padInfo.FormatGuidance(uiLanguage)}";
        }
        else if (s.ApproachBodyGravityG is >= 1.5 && !string.IsNullOrWhiteSpace(s.ApproachBodyName))
        {
            context = $"⚠ HIGH-G {s.ApproachBodyName}: {s.ApproachBodyGravityG.Value.ToString("0.00", CultureInfo.InvariantCulture)} G";
        }
        else if (s.ExobiologyTracker is { SamplesCollected: > 0 and < 3 } exo)
        {
            string ready = exo.CanSampleNext ? "✓ READY" : $"{exo.CurrentDistanceMeters:N0}/{exo.RequiredDistanceMeters} m";
            context = $"🧬 {exo.Species} ({exo.SamplesCollected}/3) · {ready}";
        }
        else if (s.NavRoute.Count > 1)
        {
            context = $"🧭 NEXT: {s.NavRoute[1].StarSystem} ({s.NavRoute.Count - 1} jumps)";
        }

        _txtContext.Text = context;
        _txtContext.Visibility = string.IsNullOrWhiteSpace(context) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateLockVisuals()
    {
        string hk = string.IsNullOrWhiteSpace(_configService.Current.OverlayToggleHotkey)
            ? "Ctrl+Alt+O"
            : _configService.Current.OverlayToggleHotkey;
        _txtLockHint.Text = _clickThrough ? $"🔒 {hk}" : $"🔓 DRAG · {hk}";
        _shellBorder.BorderThickness = _clickThrough ? new Thickness(1) : new Thickness(2);
    }

    private void ApplyWin32ExtendedStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        int exStyle = GetWindowLong(hwnd, GwlExStyle) | WsExLayered | WsExToolWindow;
        if (_clickThrough)
            exStyle |= WsExTransparent;
        else
            exStyle &= ~WsExTransparent;
        SetWindowLong(hwnd, GwlExStyle, exStyle);
    }
}
