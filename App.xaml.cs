// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        App.xaml.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: WPF application entry point, startup service composition, and integrated Crash Dialog.
// -----------------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HUDnopy.Models;
using HUDnopy.Services;

namespace HUDnopy;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private int _crashDialogShown;
    private AppConfig? _activeConfig;

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterGlobalExceptionHandlers();
        base.OnStartup(e);

        try
        {
            var services = new ApplicationServices();
            _activeConfig = services.Configuration.Current;
            AppDiagnosticsLogger.LogStartupBanner(_activeConfig);
            var mainWindow = new MainWindow(services);
            MainWindow = mainWindow;
            mainWindow.Show();

            if (!_activeConfig.HasCompletedFirstRunWizard)
            {
                var wizard = new HUDnopy.UI.FirstRunWizardWindow(services.Configuration, mainWindow, services.WebServer);
                wizard.ShowDialog();
                mainWindow.RefreshAfterWizard();
            }

            if (services.Configuration.Current.EnableAutoUpdateCheck)
            {
                _ = mainWindow.CheckForUpdatesInBackgroundAsync();
            }
        }
        catch (Exception ex)
        {
            HandleFatalException(ex, "App.OnStartup");
            Shutdown(1);
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            HandleFatalException(args.Exception, "WPF.Dispatcher");
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? "Unknown fatal error");
            HandleFatalException(ex, $"AppDomain(Terminating={args.IsTerminating})");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppDiagnosticsLogger.Error("TaskScheduler", "Unobserved background task exception.", args.Exception);
            args.SetObserved();
        };
    }

    private void HandleFatalException(Exception ex, string source)
    {
        AppDiagnosticsLogger.Error(source, "Unhandled fatal exception.", ex);
        if (Interlocked.Exchange(ref _crashDialogShown, 1) != 0)
            return;

        // Detach any partially initialized / broken MainWindow before pumping ShowDialog()
        // so WPF's ContextLayoutManager does not re-trigger the same layout crash.
        if (Current is { MainWindow: { } brokenWin })
        {
            try { brokenWin.Content = null; } catch { }
            try { brokenWin.Hide(); } catch { }
            try { Current.MainWindow = null; } catch { }
        }

        ShowExceptionDialog(ex, source, _activeConfig, isFatal: true);
    }

    /// <summary>
    /// Displays the unified themed HUDnopy exception dialog with automatic personal-data redaction,
    /// 1-click Support ZIP export, and pre-filled GitHub Issue creation.
    /// </summary>
    public static void ShowExceptionDialog(Exception ex, string source, AppConfig? cfg = null, bool isFatal = false)
    {
        var effectiveCfg = cfg ?? (Current as App)?._activeConfig ?? new AppConfig();
        string sanitizedReport = AppDiagnosticsLogger.BuildSanitizedCrashReport(ex, source, effectiveCfg);

        try
        {
            var disp = Current?.Dispatcher;
            if (disp is null || disp.CheckAccess())
                ShowCrashDialog(ex, source, sanitizedReport, effectiveCfg, isFatal);
            else
                disp.Invoke(() => ShowCrashDialog(ex, source, sanitizedReport, effectiveCfg, isFatal));
        }
        catch (Exception dlgEx)
        {
            AppDiagnosticsLogger.Error("CrashDialog", "Themed exception dialog failed, falling back to native MessageBox.", dlgEx);
            try
            {
                System.Windows.MessageBox.Show(
                    $"HUDnopy encountered an unexpected error:\n\n{ex.GetType().Name}: {AppDiagnosticsLogger.SanitizeText(ex.Message)}\n\n" +
                    $"Diagnostic log:\n{AppDiagnosticsLogger.CurrentLogFilePath}",
                    $"HUDnopy v{HttpClientHeaderDefaults.ProductVersion} — Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // Never throw from fallback crash dialog
            }
        }
    }

    private static void ShowCrashDialog(Exception ex, string source, string sanitizedReport, AppConfig cfg, bool isFatal)
    {
        System.Windows.Media.Brush ResBrush(string key, byte r, byte g, byte b) =>
            Current?.TryFindResource(key) as System.Windows.Media.Brush
            ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));

        var bg = ResBrush("HudBgBrush", 10, 14, 20);
        var panel = ResBrush("HudPanelAltBrush", 18, 24, 34);
        var accent = ResBrush("HudAccentBrush", 255, 113, 0);
        var danger = ResBrush("HudDangerBrush", 255, 46, 76);
        var textBrush = ResBrush("HudTextBrush", 242, 245, 250);
        var mutedBrush = ResBrush("HudMutedBrush", 138, 150, 168);

        var dialog = new Window
        {
            Title = $"HUDnopy v{HttpClientHeaderDefaults.ProductVersion} — {(isFatal ? "Error" : "Warning")} ({source})",
            Width = 680,
            Height = 460,
            MinWidth = 520,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = bg,
            Foreground = textBrush,
            Topmost = true
        };

        var root = new DockPanel { Margin = new Thickness(16) };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock
        {
            Text = $"⚠ {ex.GetType().Name}: {AppDiagnosticsLogger.SanitizeText(ex.Message)}",
            Foreground = danger,
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        });
        header.Children.Add(new TextBlock
        {
            Text = "Personal data (user paths, API keys, tokens) has been automatically redacted below.",
            Foreground = mutedBrush,
            FontSize = 11.5,
            Margin = new Thickness(0, 4, 0, 0)
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var statusText = new TextBlock
        {
            Foreground = accent,
            FontSize = 11.5,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(statusText, Dock.Bottom);
        root.Children.Add(statusText);

        var buttons = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        System.Windows.Controls.Button MakeBtn(string label, System.Windows.Media.Brush borderBrush) => new()
        {
            Content = label,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
            Background = panel,
            Foreground = textBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var btnZipIssue = MakeBtn("📦 Export ZIP + GitHub Issue", accent);
        btnZipIssue.Click += (_, _) =>
        {
            try
            {
                string zipPath = AppDiagnosticsLogger.ExportSupportBundle(cfg, crashReportText: sanitizedReport);
                statusText.Text = $"ZIP exported: {zipPath}";
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"") { UseShellExecute = true }); } catch { }
                string issueUrl = AppDiagnosticsLogger.BuildGitHubIssueUrl(ex, source, cfg);
                Process.Start(new ProcessStartInfo(issueUrl) { UseShellExecute = true });
            }
            catch (Exception exportEx)
            {
                statusText.Text = $"Export failed: {exportEx.Message}";
            }
        };

        var btnCopy = MakeBtn("📋 Copy Report", mutedBrush);
        btnCopy.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(sanitizedReport);
                statusText.Text = "Sanitized crash report copied to clipboard.";
            }
            catch { }
        };

        var btnLogs = MakeBtn("📂 Open Logs", mutedBrush);
        btnLogs.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(AppDiagnosticsLogger.LogsDirectory);
                Process.Start(new ProcessStartInfo(AppDiagnosticsLogger.LogsDirectory) { UseShellExecute = true });
            }
            catch { }
        };

        var btnClose = MakeBtn("Close", danger);
        btnClose.Click += (_, _) => dialog.Close();

        buttons.Children.Add(btnZipIssue);
        buttons.Children.Add(btnCopy);
        buttons.Children.Add(btnLogs);
        buttons.Children.Add(btnClose);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var reportBox = new System.Windows.Controls.TextBox
        {
            Text = sanitizedReport,
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11.5,
            Background = panel,
            Foreground = textBrush,
            BorderBrush = mutedBrush,
            Padding = new Thickness(8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        root.Children.Add(reportBox);

        dialog.Content = root;
        dialog.ShowDialog();
    }
}
