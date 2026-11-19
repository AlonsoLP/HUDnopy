// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        App.xaml.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: WPF application entry point and startup service composition.
// -----------------------------------------------------------------------------

using System.Configuration;
using System.Data;
using System.Windows;

namespace HUDnopy;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new HUDnopy.Services.ApplicationServices();
        var mainWindow = new MainWindow(services);
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
