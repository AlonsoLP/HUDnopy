// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        App.xaml.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: WPF application entry point and startup service composition.
// -----------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using HUDnopy.Services;

namespace HUDnopy;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private int _crashDialogShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterGlobalExceptionHandlers();
        base.OnStartup(e);

        var services = new ApplicationServices();
        AppDiagnosticsLogger.LogStartupBanner(services.Configuration.Current);
        var mainWindow = new MainWindow(services);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppDiagnosticsLogger.Error("WPF.Dispatcher", "Unhandled UI thread exception.", args.Exception);
            if (Interlocked.Exchange(ref _crashDialogShown, 1) == 0)
            {
                try
                {
                    string logPath = AppDiagnosticsLogger.CurrentLogFilePath;
                    System.Windows.MessageBox.Show(
                        $"HUDnopy encountered an unexpected error:\n\n{args.Exception.GetType().Name}: {args.Exception.Message}\n\n" +
                        $"Diagnostic log saved to:\n{logPath}",
                        $"HUDnopy v{HttpClientHeaderDefaults.ProductVersion} — Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch
                {
                    // Never throw from crash dialog
                }
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppDiagnosticsLogger.Error(
                "AppDomain",
                $"Unhandled AppDomain exception (IsTerminating={args.IsTerminating}).",
                args.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppDiagnosticsLogger.Error("TaskScheduler", "Unobserved background task exception.", args.Exception);
            args.SetObserved();
        };
    }
}
