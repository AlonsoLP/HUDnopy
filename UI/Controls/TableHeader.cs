// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/Controls/TableHeader.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Attached WPF property binding DataGrid columns to localized table header keys.
// -----------------------------------------------------------------------------

using System.Windows;

namespace HUDnopy;

/// <summary>
/// Attached WPF property binding DataGrid columns to localized table header keys.
/// </summary>
public static class TableHeader
{
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached("Key", typeof(string), typeof(TableHeader), new PropertyMetadata(null));

    public static string? GetKey(DependencyObject target) => (string?)target.GetValue(KeyProperty);

    public static void SetKey(DependencyObject target, string value) => target.SetValue(KeyProperty, value);
}
