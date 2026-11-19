// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/Controls/UiText.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Attached WPF properties binding text and tooltips to localization catalog keys.
// -----------------------------------------------------------------------------

using System.Windows;

namespace HUDnopy;

/// <summary>
/// Attached WPF properties binding text and tooltips to localization catalog keys.
/// </summary>
public static class UiText
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(UiText));

    public static void SetKey(DependencyObject target, string value) => target.SetValue(KeyProperty, value);
    public static string? GetKey(DependencyObject target) => (string?)target.GetValue(KeyProperty);

    public static readonly DependencyProperty TooltipKeyProperty = DependencyProperty.RegisterAttached(
        "TooltipKey", typeof(string), typeof(UiText));

    public static void SetTooltipKey(DependencyObject target, string value) => target.SetValue(TooltipKeyProperty, value);
    public static string? GetTooltipKey(DependencyObject target) => (string?)target.GetValue(TooltipKeyProperty);
}
