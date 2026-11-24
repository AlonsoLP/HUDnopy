// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/FirstRunWizardWindow.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Guided 4-step First-Run Wizard (Language, Paths, Theme/Web HUD, Optional AI/Voice).
// -----------------------------------------------------------------------------

using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HUDnopy.Localization;
using HUDnopy.Models;
using HUDnopy.Services;

namespace HUDnopy.UI;

/// <summary>
/// Sleek 4-step dark HUD onboarding modal shown on fresh installs (or reopened from Settings -> About).
/// </summary>
public sealed class FirstRunWizardWindow : Window
{
    private readonly ConfigService _configService;
    private readonly WebServerService? _webServer;
    private readonly System.Windows.Controls.ComboBox _cmbLang;
    private readonly System.Windows.Controls.TextBox _txtJournal;
    private readonly System.Windows.Controls.TextBox _txtBinds;
    private readonly System.Windows.Controls.ComboBox _cmbTheme;
    private readonly System.Windows.Controls.CheckBox _chkWebHud;
    private readonly System.Windows.Controls.CheckBox _chkUpdateCheck;
    private readonly System.Windows.Controls.CheckBox _chkAiCopilot;
    private readonly TextBlock _txtStepTitle;
    private readonly TextBlock _txtThemeImportStatus;
    private readonly UIElement[] _steps;
    private readonly System.Windows.Controls.Button _btnBack;
    private readonly System.Windows.Controls.Button _btnNext;
    private int _stepIndex;
    private bool _importedGameTheme;

    public FirstRunWizardWindow(ConfigService configService, Window? owner = null, WebServerService? webServer = null)
    {
        _configService = configService;
        _webServer = webServer;
        if (owner is not null) Owner = owner;

        Title = $"HUDnopy v{HttpClientHeaderDefaults.ProductVersion} — Setup Wizard";
        Width = 600;
        Height = 470;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = owner is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "HudBgBrush");
        SetResourceReference(ForegroundProperty, "HudTextBrush");

        var root = new DockPanel { Margin = new Thickness(20) };

        _txtStepTitle = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 15, Margin = new Thickness(0, 0, 0, 12) };
        _txtStepTitle.SetResourceReference(TextBlock.ForegroundProperty, "HudAccentBrush");
        DockPanel.SetDock(_txtStepTitle, Dock.Top);
        root.Children.Add(_txtStepTitle);

        // Navigation footer
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        _btnBack = new System.Windows.Controls.Button { Content = "← Back", MinWidth = 96, MinHeight = 38, Padding = new Thickness(12, 6, 12, 6) };
        _btnBack.SetResourceReference(StyleProperty, "HudButtonStyle");
        _btnBack.Click += (_, _) => ShowStep(_stepIndex - 1);
        DockPanel.SetDock(_btnBack, Dock.Left);

        _btnNext = new System.Windows.Controls.Button { Content = "Next →", MinWidth = 110, MinHeight = 38, Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        _btnNext.SetResourceReference(StyleProperty, "HudButtonStyle");
        _btnNext.Click += (_, _) =>
        {
            if (_steps is not null && _stepIndex < _steps.Length - 1) ShowStep(_stepIndex + 1);
            else SaveAndClose();
        };
        DockPanel.SetDock(_btnNext, Dock.Right);

        footer.Children.Add(_btnBack);
        footer.Children.Add(_btnNext);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // Step 1: Language (same ComboBoxItem pattern as Settings so closed & open states render cleanly)
        _cmbLang = new System.Windows.Controls.ComboBox
        {
            MinHeight = 38,
            Width = 240,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            SelectedValuePath = "Tag"
        };
        (string Code, string Label)[] languages =
        [
            ("es-ES", "Español"),
            ("en-GB", "English"),
            ("fr-FR", "Français"),
            ("de-DE", "Deutsch"),
            ("ru-RU", "Русский"),
            ("pt-BR", "Português (Brasil)")
        ];
        foreach (var (code, label) in languages)
            _cmbLang.Items.Add(new ComboBoxItem { Content = label, Tag = code });
        _cmbLang.SelectedValue = _configService.Current.InterfaceLanguage;
        if (_cmbLang.SelectedIndex < 0)
            _cmbLang.SelectedIndex = 1;
        var step1 = BuildStepPanel(
            "Select your preferred interface and COVAS language:",
            _cmbLang);

        // Step 2: Elite Dangerous paths with native FolderBrowserDialog pickers
        _txtJournal = MakeTextBox(_configService.Current.JournalFolderPath);
        _txtBinds = MakeTextBox(_configService.Current.BindingsFolderPath);
        var lblJournal = MakeMutedLabel("");
        var lblBinds = MakeMutedLabel("");
        void RefreshPathStatus()
        {
            lblJournal.Text = $"Elite Dangerous Journal folder ({(Directory.Exists(_txtJournal.Text.Trim()) ? "✓ Detected" : "⚠ Folder not found yet")}):";
            lblBinds.Text = $"Bindings folder ({(Directory.Exists(_txtBinds.Text.Trim()) ? "✓ Detected" : "⚠ Folder not found yet")}):";
        }
        _txtJournal.TextChanged += (_, _) => RefreshPathStatus();
        _txtBinds.TextChanged += (_, _) => RefreshPathStatus();
        RefreshPathStatus();

        var journalRow = BuildFolderPickerRow(_txtJournal, "Select Elite Dangerous Journal folder", RefreshPathStatus);
        var bindsRow = BuildFolderPickerRow(_txtBinds, "Select Elite Dangerous Bindings folder", RefreshPathStatus);

        var step2 = new StackPanel();
        step2.Children.Add(lblJournal);
        step2.Children.Add(journalRow);
        step2.Children.Add(lblBinds);
        step2.Children.Add(bindsRow);

        // Step 3: Theme + Web HUD (ComboBoxItem pattern so closed state shows clean theme name)
        _cmbTheme = new System.Windows.Controls.ComboBox
        {
            MinHeight = 38,
            Width = 260,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        int matchedPresetIdx = 0;
        for (int i = 0; i < ConfigService.Presets.Count; i++)
        {
            var preset = ConfigService.Presets[i];
            _cmbTheme.Items.Add(new ComboBoxItem { Content = preset.Name, Tag = preset });
            if (string.Equals(preset.AccentHex, _configService.Current.ThemeAccentHex, StringComparison.OrdinalIgnoreCase))
                matchedPresetIdx = i;
        }
        _cmbTheme.SelectedIndex = matchedPresetIdx;
        _cmbTheme.SelectionChanged += (_, _) => _importedGameTheme = false;
        _txtThemeImportStatus = MakeMutedLabel("");
        var btnImportGame = new System.Windows.Controls.Button { Content = "🎨 Import from Elite HUD / EDHM", MinHeight = 38, Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 4) };
        btnImportGame.SetResourceReference(StyleProperty, "HudButtonStyle");
        btnImportGame.Click += (_, _) =>
        {
            if (GameHudThemeImportService.TryImportGameTheme(out string acc, out string sec, out string src))
            {
                _importedGameTheme = true;
                _configService.Current.ThemeAccentHex = acc;
                _configService.Current.ThemeSecondaryHex = sec;
                _configService.ApplyThemeToApplication();
                _txtThemeImportStatus.Text = $"✓ Imported {acc} / {sec} from {src}";
            }
            else
            {
                _importedGameTheme = false;
                _txtThemeImportStatus.Text = "No GraphicsConfigurationOverride.xml or EDHM UI profile found.";
            }
        };
        _chkWebHud = new System.Windows.Controls.CheckBox
        {
            Content = "Auto-start embedded Web HUD server (for tablet / second monitor)",
            IsChecked = _configService.Current.AutoStartWebServer,
            Margin = new Thickness(0, 12, 0, 0)
        };
        _chkWebHud.SetResourceReference(ForegroundProperty, "HudTextBrush");

        var txtCertStatus = MakeMutedLabel("");
        var btnTrustCert = new System.Windows.Controls.Button
        {
            Content = "🔒 Trust HTTPS Cert (PWA)",
            MinHeight = 38,
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 2)
        };
        btnTrustCert.SetResourceReference(StyleProperty, "HudButtonStyle");
        void RefreshWizardCertUi()
        {
            bool trusted = _webServer?.IsCertificateTrusted == true;
            btnTrustCert.Content = trusted ? "🔓 Remove Trusted HTTPS Cert" : "🔒 Trust HTTPS Cert (PWA)";
            if (trusted)
                txtCertStatus.Text = "✓ Local HTTPS certificate is trusted in Windows (PWA ready).";
        }
        RefreshWizardCertUi();
        btnTrustCert.Click += (_, _) =>
        {
            try
            {
                bool ok;
                string msg;
                if (_webServer is { IsRunning: true })
                {
                    ok = _webServer.IsCertificateTrusted
                        ? _webServer.TryUntrustCertificate(out msg)
                        : _webServer.TryTrustCertificate(out msg);
                }
                else
                {
                    var cert = WebHttpsCertificate.LoadOrCreate(Array.Empty<System.Net.IPAddress>());
                    ok = WebHttpsCertificate.IsTrustedInCurrentUserRoot(cert)
                        ? WebHttpsCertificate.UntrustFromCurrentUserRoot(cert, out msg)
                        : WebHttpsCertificate.TrustInCurrentUserRoot(cert, out msg);
                }
                txtCertStatus.Text = ok ? $"✓ {msg}" : $"⚠ {msg}";
                RefreshWizardCertUi();
            }
            catch (Exception ex)
            {
                AppDiagnosticsLogger.Warn("Wizard.Cert", $"Failed to update HTTPS certificate trust: {ex.Message}");
                txtCertStatus.Text = $"⚠ {ex.Message}";
            }
        };
        var step3 = BuildStepPanel("Choose a cockpit HUD theme or import your in-game colors:", _cmbTheme, btnImportGame, _txtThemeImportStatus, _chkWebHud, btnTrustCert, txtCertStatus);

        // Step 4: Optional AI & Updates (100% opt-in)
        _chkAiCopilot = new System.Windows.Controls.CheckBox
        {
            Content = "Enable COVAS AI Copilot (configure Ollama/Gemini/OpenAI later in Settings)",
            IsChecked = _configService.Current.EnableAiCopilot,
            Margin = new Thickness(0, 8, 0, 8)
        };
        _chkAiCopilot.SetResourceReference(ForegroundProperty, "HudTextBrush");
        _chkUpdateCheck = new System.Windows.Controls.CheckBox
        {
            Content = "Check GitHub Releases on startup for new HUDnopy versions (opt-in)",
            IsChecked = _configService.Current.EnableAutoUpdateCheck,
            Margin = new Thickness(0, 4, 0, 0)
        };
        _chkUpdateCheck.SetResourceReference(ForegroundProperty, "HudTextBrush");
        var step4 = BuildStepPanel(
            "All external integrations (Spansh, EDSM, Inara, Frontier cAPI, Discord, AI) are strictly opt-in and can be toggled anytime in Settings:",
            _chkAiCopilot, _chkUpdateCheck);

        _steps = [step1, step2, step3, step4];
        var bodyHost = new Grid();
        foreach (var s in _steps) bodyHost.Children.Add(s);
        root.Children.Add(bodyHost);

        Content = root;
        ShowStep(0);
    }

    private void ShowStep(int idx)
    {
        _stepIndex = Math.Clamp(idx, 0, _steps.Length - 1);
        string[] titles =
        [
            "STEP 1/4 — LANGUAGE",
            "STEP 2/4 — ELITE DANGEROUS PATHS",
            "STEP 3/4 — HUD THEME & WEB HUD",
            "STEP 4/4 — OPTIONAL AI & UPDATES"
        ];
        _txtStepTitle.Text = titles[_stepIndex];
        for (int i = 0; i < _steps.Length; i++)
            _steps[i].Visibility = i == _stepIndex ? Visibility.Visible : Visibility.Collapsed;
        _btnBack.IsEnabled = _stepIndex > 0;
        _btnNext.Content = _stepIndex == _steps.Length - 1 ? "✓ Finish" : "Next →";
    }

    private void SaveAndClose()
    {
        var cfg = _configService.Current;
        if (_cmbLang.SelectedValue is string lang && !string.IsNullOrWhiteSpace(lang))
            cfg.InterfaceLanguage = lang;
        if (!string.IsNullOrWhiteSpace(_txtJournal.Text))
            cfg.JournalFolderPath = _txtJournal.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_txtBinds.Text))
        {
            string newBindsFolder = _txtBinds.Text.Trim();
            if (!string.Equals(cfg.BindingsFolderPath, newBindsFolder, StringComparison.OrdinalIgnoreCase))
                cfg.SelectedBindsFile = "";
            cfg.BindingsFolderPath = newBindsFolder;
        }
        if (!_importedGameTheme &&
            _cmbTheme.SelectedItem is ComboBoxItem { Tag: HudThemePreset preset })
        {
            cfg.ThemeAccentHex = preset.AccentHex;
            cfg.ThemeSecondaryHex = preset.SecondaryHex;
        }
        cfg.AutoStartWebServer = _chkWebHud.IsChecked == true;
        cfg.EnableAiCopilot = _chkAiCopilot.IsChecked == true;
        cfg.EnableAutoUpdateCheck = _chkUpdateCheck.IsChecked == true;
        cfg.HasCompletedFirstRunWizard = true;
        _configService.Save();
        DialogResult = true;
        Close();
    }

    private static StackPanel BuildStepPanel(string intro, params UIElement[] controls)
    {
        var sp = new StackPanel();
        sp.Children.Add(MakeMutedLabel(intro));
        foreach (var c in controls) sp.Children.Add(c);
        return sp;
    }

    private static TextBlock MakeMutedLabel(string text)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6), FontSize = 12 };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "HudMutedBrush");
        return tb;
    }

    private static System.Windows.Controls.TextBox MakeTextBox(string initial)
    {
        var tb = new System.Windows.Controls.TextBox
        {
            Text = initial ?? "",
            MinHeight = 38,
            Padding = new Thickness(8, 5, 8, 5),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 10, 14)),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        tb.SetResourceReference(ForegroundProperty, "HudTextBrush");
        tb.SetResourceReference(BorderBrushProperty, "HudBorderBrush");
        return tb;
    }

    private static Grid BuildFolderPickerRow(System.Windows.Controls.TextBox targetBox, string description, Action onChanged)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(targetBox, 0);
        row.Children.Add(targetBox);

        var btnBrowse = new System.Windows.Controls.Button
        {
            Content = "📂",
            ToolTip = description,
            MinWidth = 42,
            MinHeight = 38,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(8, 0, 0, 0)
        };
        btnBrowse.SetResourceReference(StyleProperty, "HudButtonStyle");
        btnBrowse.Click += (_, _) =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = description,
                SelectedPath = Directory.Exists(targetBox.Text.Trim())
                    ? targetBox.Text.Trim()
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                targetBox.Text = dialog.SelectedPath;
                onChanged();
            }
        };
        Grid.SetColumn(btnBrowse, 1);
        row.Children.Add(btnBrowse);
        return row;
    }
}
