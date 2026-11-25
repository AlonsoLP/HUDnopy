// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        MainWindow.xaml.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Main WPF window lifecycle, system tray, Mini-HUD overlay, and telemetry dispatch.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using HUDnopy.Models;
using HUDnopy.Services;
using HUDnopy.Services.Integrations;
using Forms = System.Windows.Forms;
using WpfBinding = System.Windows.Data.Binding;
using WpfControl = System.Windows.Controls.Control;
using HUDnopy.Localization;

namespace HUDnopy;

/// <summary>
/// Main WPF window lifecycle, system tray, Mini-HUD overlay, and telemetry dispatch.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ConfigService _configService;
    private readonly BindingsService _bindingsService;
    private readonly InputSimulatorService _inputSimulator;
    private readonly EliteFileWatcherService _fileWatcher;
    private readonly AlertService _alertService;
    private readonly AlertSoundService _alertSoundService;
    private readonly PiperTtsService _piperTts;
    private readonly WhisperSttService _whisperStt;
    private readonly CockpitRadioService _cockpitRadio;
    private HUDnopy.UI.HudOverlayWindow? _overlayWindow;
    private readonly AlertSpeechService _alertSpeechService;
    private readonly SpanshSystemDataService _spanshSystemDataService;
    private readonly CommunitySyncService _communitySync;
    private readonly FrontierCapiService _frontierCapi;
    private readonly GalaxyStatusService _galaxyStatus;
    private readonly GalnetService _galnetService;
    private readonly EliteBgsWatchlistService _eliteBgsWatchlist;
    private readonly ArdentMarketService _ardentMarket;
    private readonly EdastroCanonnService _edastroCanonn;
    private readonly DiscordIntegrationService _discord;
    private readonly AiCopilotService _aiCopilot;
    private readonly WebServerService _webServer;
    private string _lastSystemNoteLoaded = "";
    private string _lastEdsmIntelSystem = "";
    private string _lastEdsmDiscoverySystem = "";
    private List<SpanshRouteWaypoint> _spanshRouteWaypoints = new();
    private string _lastSpanshRouteAutoCopied = "";
    private bool _viewingHistoricalJournal;
    private NAudio.Wave.WaveIn? _aiVoiceRecorder;
    private MemoryStream? _aiVoiceStream;
    private NAudio.Wave.WaveFileWriter? _aiVoiceWriter;
    private bool _isRecordingAiVoice;
    private NAudio.Wave.WaveIn? _aiVadRecorder;
    private NAudio.CoreAudioApi.MMDeviceEnumerator? _audioDeviceEnumerator;
    private NAudio.CoreAudioApi.MMDeviceNotificationClient? _audioDeviceNotifications;
    private bool _aiHandsFreeActive;
    private volatile bool _aiHandsFreeProcessing;
    private readonly Lock _vadLock = new();
    private readonly Queue<byte[]> _vadPreBuffer = new();
    private MemoryStream? _vadUtterancePcm;
    private bool _vadInSpeech;
    private int _vadSpeechFrames;
    private int _vadSilenceFrames;
    private double _vadNoiseFloor = 0.008;
    private int _vadBargeInFrames;
    private readonly Lock _shipUiUpdateLock = new();
    private ShipState? _pendingShipUiState;
    private bool _shipUiUpdateQueued;
    private int _fontZoomWheelDelta;
    private System.Windows.Threading.DispatcherTimer? _fontZoomApplyTimer;
    private System.Windows.Threading.DispatcherTimer? _fontZoomSaveTimer;
    private int _pendingFontScalePercent;
    private System.Windows.Threading.DispatcherTimer? _searchDebounceTimer;
    private Action? _pendingSearchAction;

    private void DebounceSearch(Action action, int delayMs = 150)
    {
        _pendingSearchAction = action;
        if (_searchDebounceTimer is null)
        {
            _searchDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(delayMs)
            };
            _searchDebounceTimer.Tick += (_, _) =>
            {
                _searchDebounceTimer.Stop();
                var run = _pendingSearchAction;
                _pendingSearchAction = null;
                run?.Invoke();
            };
        }
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _searchDebounceTimer.Start();
    }
    private System.Windows.Threading.DispatcherTimer? _alertToastTimer;
    private string? _alertToastId;

    private Forms.NotifyIcon? _notifyIcon;
    private bool _isExplicitExit = false;
    private bool _trayBalloonShown = false;
    private bool _suppressEvents = false;
    private bool _refreshingVpnInterfaces;
    private bool _applyingWebSettings;
    private bool _materialFilterControlsReady;
    private bool _updatingTableFilters;
    private bool _isFullscreen;
    private bool _isMiniHud;
    private Rect _miniHudBounds;
    private WindowState _miniHudState;
    private bool _miniHudTopmost;
    private double _miniHudOpacity = 1.0;
    private string? _selectedBindingCategory;
    private Rect _windowedBounds;
    private WindowState _windowedState;
    private WindowStyle _windowedStyle;
    private ResizeMode _windowedResizeMode;
    private readonly ConditionalWeakTable<FrameworkElement, FontSizeBaseline> _desktopFontBaseSizes = new();
    private double _desktopFontScale = 1;
    private string _uiLanguage = "en-GB";
    private System.Globalization.CultureInfo UiCulture => System.Globalization.CultureInfo.GetCultureInfo(_uiLanguage);
    private string _readyStatusText = "";
    private string? _displayedBindsFile;
    private IReadOnlyList<string>? _installedVoiceNames;
    private readonly Dictionary<DataGridColumn, string> _tableHeaderKeys = new();
    private (uint Flags, double FontScale, System.Windows.Media.Brush Accent, System.Windows.Media.Brush Dim,
        System.Windows.Media.Brush Danger, System.Windows.Media.Brush Muted, System.Windows.Media.Brush Panel)? _cockpitCardsKey;
    private IReadOnlyList<ProfileValueItem>? _lastProfileSource;
    private IReadOnlyList<ExplorationBodyItem>? _lastExplorationBodiesSource;
    private IReadOnlyList<JournalEntryItem>? _lastRecentEventsSource;
    private (CarrierState? Source, string Language)? _lastCarrierState;
    private int _lastMissionRefreshMinute = -1;

    private sealed record FontSizeBaseline(double BaseSize, double? LineHeight);

    public MainWindow(ApplicationServices services)
    {
        InitializeComponent();
        Title = $"{HttpClientHeaderDefaults.ProductName} v{HttpClientHeaderDefaults.ProductVersion} // SHIP CONTROL SYSTEM";
        _readyStatusText = TxtStatusBarLeft.Text;
        _materialFilterControlsReady = true;

        _configService = services.Configuration;
        _configService.ApplyThemeToApplication();

        _bindingsService = services.Bindings;
        _inputSimulator = services.InputSimulator;
        _fileWatcher = services.ShipState;
        _alertService = services.Alerts;
        _alertSoundService = services.AlertSounds;
        _piperTts = services.PiperTts;
        _whisperStt = services.WhisperStt;
        _cockpitRadio = services.CockpitRadio;
        _alertSpeechService = services.AlertSpeech;
        _spanshSystemDataService = services.SpanshSystemData;
        _communitySync = services.CommunitySync;
        _frontierCapi = services.FrontierCapi;
        _galaxyStatus = services.GalaxyStatus;
        _galnetService = services.Galnet;
        _eliteBgsWatchlist = services.EliteBgsWatchlist;
        _ardentMarket = services.ArdentMarket;
        _edastroCanonn = services.EdastroCanonn;
        _discord = services.Discord;
        _aiCopilot = services.AiCopilot;
        _webServer = services.WebServer;
        InitializeTableHeaderKeys();

        SldDesktopFontScale.ValueChanged += SldDesktopFontScale_ValueChanged;
        if (SystemMapControl is not null)
            SystemMapControl.BodySelected += OnSystemMapBodySelected;
        AddHandler(DataGridRow.MouseLeftButtonUpEvent, new System.Windows.Input.MouseButtonEventHandler(OnAnyDataGridRowTapped), handledEventsToo: true);
        InitializeTrayIcon();
        _alertSpeechService.SpeechFailed += message => Dispatcher.BeginInvoke(() => SetStatusBarMessage($"⚠ {message}"));
        _communitySync.SyncStatusChanged += () => Dispatcher.BeginInvoke(RefreshCommunitySyncStatus);
        _communitySync.EdsmSystemIntelUpdated += intel => Dispatcher.BeginInvoke(() => ApplyEdsmSystemIntel(intel));
        _communitySync.EdsmDiscoveryUpdated += intel => Dispatcher.BeginInvoke(() => ApplyEdsmDiscoveryIntel(intel));
        _frontierCapi.StatusChanged += () => Dispatcher.BeginInvoke(RefreshFrontierCapiUi);
        _galaxyStatus.StatusUpdated += snap => Dispatcher.BeginInvoke(() => UpdateGalaxyStatusUi(snap));
        _galnetService.ArticlesUpdated += () => Dispatcher.BeginInvoke(RefreshGalnetUi);
        _eliteBgsWatchlist.WatchlistUpdated += items => Dispatcher.BeginInvoke(() => UpdateEliteBgsWatchlistUi(items));
        _discord.StatusChanged += () => Dispatcher.BeginInvoke(RefreshV3IntegrationsStatus);
        _aiCopilot.CopilotMessageAdded += (speaker, msg) => Dispatcher.BeginInvoke(() => AppendAiChatLine(speaker, msg));
        _aiCopilot.CopilotStreamChunk += chunk => Dispatcher.BeginInvoke(() => AppendAiStreamChunk(chunk));
        _aiCopilot.SpanshRouteRequested += (mode, dest) => Dispatcher.BeginInvoke(() => ExecuteSpanshRouteFromAi(mode, dest));
        _aiCopilot.SpanshServiceRequested += svc => Dispatcher.BeginInvoke(() => ExecuteSpanshServiceFromAi(svc));
        _aiCopilot.CopySystemRequested += sys => Dispatcher.BeginInvoke(() => CopySystemToClipboard(sys));
        _aiCopilot.SurfaceBookmarkSaveRequested += label => Dispatcher.BeginInvoke(() => HandleAiSurfaceBookmarkSave(label));
        _aiCopilot.SurfaceNavigateRequested += target => Dispatcher.BeginInvoke(() => HandleAiSurfaceNavigate(target));
        _aiCopilot.SpanshItemRequested += (query, lyr) => Dispatcher.BeginInvoke(() => HandleAiSpanshItemSearch(query, lyr));
        _aiCopilot.ExportShipRequested += target => Dispatcher.BeginInvoke(() => HandleAiExportShip(target));
        _aiCopilot.UiNavigationRequested += target => Dispatcher.BeginInvoke(() => HandleAiUiNavigation(target));
        _aiCopilot.SpanshTradeRequested += () => Dispatcher.BeginInvoke(() => BtnPlotSpanshTrade_Click(this, new RoutedEventArgs()));
        _aiCopilot.SpanshMiningRequested += mineral => Dispatcher.BeginInvoke(() => HandleAiSpanshMining(mineral));
        _aiCopilot.ColonisationSupplierRequested += () => Dispatcher.BeginInvoke(() => BtnFindColonisationSupplier_Click(this, new RoutedEventArgs()));
        _aiCopilot.WishlistAddRequested += bpId => Dispatcher.BeginInvoke(() => HandleAiWishlistAdd(bpId));
        _aiCopilot.TritiumCalcRequested += ly => Dispatcher.BeginInvoke(() => HandleAiTritiumCalc(ly));
        _aiCopilot.VoiceRateAdjustRequested += delta => Dispatcher.BeginInvoke(() => HandleAiVoiceRateAdjust(delta));
        _aiCopilot.MemoryChanged += () => Dispatcher.BeginInvoke(RefreshAiMemoryPreview);
        _cockpitRadio.StatusChanged += status => Dispatcher.BeginInvoke(() => RefreshCockpitRadioUi(status));
        SourceInitialized += (_, _) => RegisterGlobalPttHotkey();
        LoadConfigToUi();
        RefreshJournalHistoryList();
        ApplyNavigationLabels();
        ApplyDashboardLabels();
        ApplyTableHeaders();
        ApplyMaterialLabels();
        ApplyFlightLabels();
        ApplySettingsLabels();
        RefreshWebServerUi();
        RefreshSoundDirectories();
        ApplyOperationsLabels();
        ApplyDeckLabels();

        _fileWatcher.StateUpdated += OnShipStateUpdated;
        _fileWatcher.UnhandledMessagesUpdated += () => Dispatcher.BeginInvoke(RefreshUnhandledMessagesUi);
        AppDiagnosticsLogger.TechnicalStateSnapshotProvider = () =>
        {
            var st = _fileWatcher.State;
            string mode = st.OnFoot ? "OnFoot" : st.InSRV ? "SRV" : st.InFighter ? "Fighter" : "Ship";
            string flight = st.Docked ? "Docked" : st.Landed ? "Landed" : st.Supercruise ? "Supercruise" : "NormalSpace";
            return $"Game={st.IsGameDetected}, Mode={mode}, Flight={flight}, WebHUD={_webServer.IsRunning}, Overlay={_overlayWindow?.IsVisible == true}";
        };
        AppDiagnosticsLogger.RecentIssuesChanged += () => Dispatcher.BeginInvoke(RefreshDiagnosticIssuesUi);
        _spanshSystemDataService.StateChanged += OnSpanshDataChanged;
        _alertService.AlertsChanged += () => Dispatcher.BeginInvoke(RefreshAlertsUi);
        _alertService.AlertRaised += alert => Dispatcher.BeginInvoke(() => ShowAlertToast(alert));
        _alertSoundService.PlaybackFailed += message => Dispatcher.BeginInvoke(() => SetStatusBarMessage($"⚠ {message}"));
        _bindingsService.BindingsUpdated += _ => Dispatcher.BeginInvoke(RefreshBindingsUi);
        _webServer.ServerStatusChanged += () => Dispatcher.BeginInvoke(RefreshWebServerUi);
        _configService.ConfigChanged += _ => Dispatcher.BeginInvoke(() =>
        {
            RefreshHeaderOptions();
            RefreshHeaderModules(_fileWatcher.State);
            RefreshSurfaceBookmarksUi();
            _lastWishlistSignature = null;
            UpdateEngineeringWishlistUi(_fileWatcher.State);
            SyncIntegrationsUiFromConfig();
            if (_webServer.IsRunning)
                UpdateWebQrCode(_webServer.GetAccessUrls().ToArray());
        });

        RefreshBindingsUi();
        RefreshHeaderOptions();
        UpdateShipUi(_fileWatcher.State);
        RefreshAlertsUi();
        RefreshDiagnosticIssuesUi();
        SwitchView(0);

        Loaded += async (_, _) => await RunUiAsync("MainWindow Loaded", async () =>
        {
            Task loadVoicesTask = LoadAlertSpeechVoicesAsync(_configService.Current.SpeechVoiceName);
            if (_configService.Current.AutoStartWebServer)
            {
                try
                {
                    await _webServer.StartAsync();
                }
                catch (Exception webEx)
                {
                    AppDiagnosticsLogger.Error("WebServer", "Failed to auto-start Kestrel web server on startup.", webEx);
                    SetStatusBarMessage($"⚠ Web HUD: {webEx.Message}");
                }
            }

            try
            {
                await loadVoicesTask;
            }
            catch (Exception voiceEx)
            {
                AppDiagnosticsLogger.Warn("Speech", $"Failed to enumerate system speech voices: {voiceEx.Message}");
            }

            RefreshWebServerUi();
            ApplyDesktopFontScale(_configService.Current.DesktopFontScalePercent / 100d);
            ObserveBackgroundTask("Startup Integrations Sync", TriggerStartupIntegrationsSyncAsync());
            if (_configService.Current.StartMinimizedToTray)
            {
                MinimizeToTray();
            }
        });
    }

    private void InitializeTrayIcon()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = CreateHudTrayIcon(),
            Visible = true,
            Text = "HUDnopy"
        };

        var contextMenu = new Forms.ContextMenuStrip();

        var itemOpen = new Forms.ToolStripMenuItem("Abrir Panel Principal");
        itemOpen.Font = new Font(itemOpen.Font, System.Drawing.FontStyle.Bold);
        itemOpen.Click += (_, _) => Dispatcher.Invoke(RestoreFromTray);

        var itemOpenWeb = new Forms.ToolStripMenuItem("Abrir Interfaz Web en Navegador");
        itemOpenWeb.Click += (_, _) => Dispatcher.Invoke(() => BtnOpenWeb_Click(this, new RoutedEventArgs()));

        var itemReload = new Forms.ToolStripMenuItem("Recargar Archivos del Juego");
        itemReload.Click += (_, _) => Dispatcher.Invoke(() => BtnReloadFiles_Click(this, new RoutedEventArgs()));

        var itemExit = new Forms.ToolStripMenuItem("Salir");
        itemExit.ForeColor = System.Drawing.Color.FromArgb(200, 20, 40);
        itemExit.Click += (_, _) => Dispatcher.Invoke(ExitApplicationDefinitively);

        contextMenu.Items.Add(itemOpen);
        contextMenu.Items.Add(itemOpenWeb);
        contextMenu.Items.Add(itemReload);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add(itemExit);

        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private static Icon CreateHudTrayIcon()
    {
        try
        {
            var streamInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/HUDnopy.ico"));
            if (streamInfo?.Stream != null)
            {
                using (streamInfo.Stream)
                {
                    return new System.Drawing.Icon(streamInfo.Stream, Forms.SystemInformation.SmallIconSize);
                }
            }
        }
        catch
        {
            // Usar icono vectorial de respaldo si falla la carga del recurso.
        }

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.FromArgb(12, 15, 20));

            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 113, 0), 2.5f);
            g.DrawPolygon(pen, new System.Drawing.Point[]
            {
                new(16, 3),
                new(29, 25),
                new(21, 27),
                new(16, 21),
                new(11, 27),
                new(3, 25)
            });

            using var brush = new SolidBrush(System.Drawing.Color.FromArgb(255, 179, 0));
            g.FillEllipse(brush, 13, 12, 6, 6);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var tempIcon = System.Drawing.Icon.FromHandle(hIcon);
            return (System.Drawing.Icon)tempIcon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private void MinimizeToTray()
    {
        Hide();
        if (!_trayBalloonShown && _notifyIcon != null)
        {
            _trayBalloonShown = true;
            var text = SettingsTranslations.ForLanguage(_uiLanguage);
            _notifyIcon.ShowBalloonTip(3000, text["trayBalloonTitle"], text["trayBalloon"], Forms.ToolTipIcon.Info);
        }
    }

    private async void ExitApplicationDefinitively()
    {
        _isExplicitExit = true;
        try
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            _spanshSystemDataService.Dispose();
            _communitySync.Dispose();
            _frontierCapi.Dispose();
            _galaxyStatus.Dispose();
            _galnetService.Dispose();
            _eliteBgsWatchlist.Dispose();
            _ardentMarket.Dispose();
            _edastroCanonn.Dispose();
            _discord.Dispose();
            StopAiHandsFree();
            CleanupAiVoiceRecorder();
            UnregisterGlobalPttHotkey();
            CloseOverlayWindow();
            _cockpitRadio.Dispose();
            _whisperStt.Dispose();
            _aiCopilot.Dispose();
            _alertSpeechService.StopSpeaking();
            _piperTts.Dispose();
            _fileWatcher.Dispose();
            await _webServer.StopAsync();
            _webServer.Dispose();
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("Shutdown", $"Error during application exit: {ex.Message}");
        }
        System.Windows.Application.Current.Shutdown();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            MinimizeToTray();
        }
    }

    private void LoadConfigToUi()
    {
        _suppressEvents = true;
        var cfg = _configService.Current;

        if (!LanguageCatalog.TryNormalize(cfg.InterfaceLanguage, out _uiLanguage))
        {
            SetStatusBarMessage($"⚠ {string.Format(
                SettingsTranslations.ForLanguage("en-GB")["invalidLanguage"],
                cfg.InterfaceLanguage)}");
            _uiLanguage = "en-GB";
        }
        cfg.InterfaceLanguage = _uiLanguage;
        CmbUiLanguage.SelectedValue = _uiLanguage;
        ApplyLanguageSettingsLabels();

        TxtConfigPort.Text = cfg.WebServerPort.ToString();
        ChkEnableWebServer.IsChecked = cfg.AutoStartWebServer;
        CmbWebAccessMode.SelectedValue = cfg.WebAccessMode;
        RefreshVpnInterfaces(cfg.WebVpnInterfaceId);
        UpdateVpnOptionsVisibility();
        ChkEnableSpanshSystemData.IsChecked = cfg.EnableSpanshSystemData;
        ChkEnableEddnUpload.IsChecked = cfg.EnableEddnUpload;
        ChkEnableEdsmSync.IsChecked = cfg.EnableEdsmSync;
        TxtEdsmCommander.Text = cfg.EdsmCommanderName;
        TxtEdsmApiKey.Text = cfg.EdsmApiKey;
        ChkEnableInaraSync.IsChecked = cfg.EnableInaraSync;
        TxtInaraCommander.Text = cfg.InaraCommanderName;
        TxtInaraApiKey.Text = cfg.InaraApiKey;
        UpdateCommunitySyncVisibility();
        RefreshCommunitySyncStatus();
        ChkEnableFrontierCapi.IsChecked = cfg.EnableFrontierCapi;
        TxtFrontierClientId.Text = Guid.TryParse(cfg.FrontierCapiClientId?.Trim(), out _) ? cfg.FrontierCapiClientId.Trim() : "";
        TxtFrontierCallbackPort.Text = Math.Clamp(cfg.FrontierCapiCallbackPort, 1024, 65535).ToString();
        RefreshFrontierCapiUi();
        LoadV3IntegrationsConfigToUi(cfg);
        if (CmbEngineeringBlueprint is not null)
        {
            CmbEngineeringBlueprint.ItemsSource = EngineeringWishlistService.Catalog;
            if (CmbEngineeringBlueprint.SelectedIndex < 0)
                CmbEngineeringBlueprint.SelectedIndex = 0;
        }
        ChkEnableAiCopilot.IsChecked = cfg.EnableAiCopilot;
        CmbAiActiveProvider.SelectedValue = string.IsNullOrWhiteSpace(cfg.AiActiveProvider) ? "ollama" : cfg.AiActiveProvider;
        CmbAiPersonality.SelectedValue = string.IsNullOrWhiteSpace(cfg.AiPersonality) ? "covas" : cfg.AiPersonality;
        TxtAiOllamaEndpoint.Text = cfg.AiOllamaEndpoint;
        SetAiModelComboInitial(CmbAiOllamaModel, cfg.AiOllamaModel);
        TxtAiGeminiApiKey.Text = cfg.AiGeminiApiKey;
        SetAiModelComboInitial(CmbAiGeminiModel, cfg.AiGeminiModel);
        TxtAiOpenAiEndpoint.Text = cfg.AiOpenAiEndpoint;
        TxtAiOpenAiApiKey.Text = cfg.AiOpenAiApiKey;
        SetAiModelComboInitial(CmbAiOpenAiModel, cfg.AiOpenAiModel);
        ChkAiSpeakReplies.IsChecked = cfg.AiSpeakReplies;
        ChkAiAllowActions.IsChecked = cfg.AiAllowActions;
        ChkAiDynamicEvents.IsChecked = cfg.AiDynamicEvents;
        CmbAiVerbosity.SelectedValue = string.IsNullOrWhiteSpace(cfg.AiVerbosity) ? "normal" : cfg.AiVerbosity;
        CmbAiThinkingMode.SelectedValue = string.IsNullOrWhiteSpace(cfg.AiThinkingMode) ? "auto" : cfg.AiThinkingMode;
        TxtAiCustomPersonality.Text = cfg.AiCustomPersonality ?? "";
        ChkAiRequireWakeWord.IsChecked = cfg.AiRequireWakeWord;
        TxtAiWakeWord.Text = string.IsNullOrWhiteSpace(cfg.AiWakeWord) ? "COVAS" : cfg.AiWakeWord;
        ChkAiArrivalBriefing.IsChecked = cfg.AiSystemArrivalBriefing;
        ChkAiTacticalHighGravity.IsChecked = cfg.AiTacticalHighGravity;
        ChkAiTacticalValuableSignals.IsChecked = cfg.AiTacticalValuableSignals;
        ChkAiTacticalMiningProspector.IsChecked = cfg.AiTacticalMiningProspector;
        ChkAiTacticalRadioChatter.IsChecked = cfg.AiTacticalRadioChatter;
        ChkAiVoiceMacrosEnabled.IsChecked = cfg.AiVoiceMacrosEnabled;
        RefreshAiVoiceMacrosUi();
        ChkAiGlobalPttHotkey.IsChecked = cfg.AiGlobalPttHotkeyEnabled;
        CmbAiGlobalPttHotkey.SelectedValue = string.IsNullOrWhiteSpace(cfg.AiGlobalPttHotkey) ? "Ctrl+Alt+V" : cfg.AiGlobalPttHotkey;
        RefreshAiMemoryPreview();
        UpdateAiConfigVisibility();
        CmbSpeechEngine.SelectedValue = string.IsNullOrWhiteSpace(cfg.SpeechEngine) ? "sapi" : cfg.SpeechEngine;
        RefreshPiperVoicesUi();
        TxtConfigJournalPath.Text = cfg.JournalFolderPath;
        TxtConfigBindingsPath.Text = cfg.BindingsFolderPath;
        TxtConfigSelectedBindsFile.Text = cfg.SelectedBindsFile;
        ChkHideUnassignedBindings.IsChecked = cfg.HideUnassignedBindings;
        ChkAutoContextDeck.IsChecked = cfg.AutoContextDeck;
        ChkHideNavigationMenuText.IsChecked = cfg.HideNavigationMenuText;
        ChkFocusGameWindow.IsChecked = cfg.FocusGameWindowOnInput;
        ChkStartMinimizedToTray.IsChecked = cfg.StartMinimizedToTray;
        CmbNavigationMenuPosition.SelectedValue = cfg.NavigationMenuPosition;
        TxtProcessExitGraceSeconds.Text = cfg.GameProcessExitGraceSeconds.ToString();
        TxtCustomAccentHex.Text = cfg.ThemeAccentHex;
        TxtCustomSecondaryHex.Text = cfg.ThemeSecondaryHex;
        if (TxtAboutVersion is not null)
            TxtAboutVersion.Text = $"v{HttpClientHeaderDefaults.ProductVersion}";
        if (TxtAboutAuthor is not null)
            TxtAboutAuthor.Text = $"{HttpClientHeaderDefaults.Authors} ({HttpClientHeaderDefaults.Copyright})";
        if (TxtAboutLicense is not null)
            TxtAboutLicense.Text = HttpClientHeaderDefaults.License;
        if (BtnAboutWeb is not null) BtnAboutWeb.Tag = HttpClientHeaderDefaults.WebsiteUrl;
        if (BtnAboutGitHub is not null) BtnAboutGitHub.Tag = HttpClientHeaderDefaults.RepositoryUrl;
        if (BtnAboutWiki is not null) BtnAboutWiki.Tag = HttpClientHeaderDefaults.WikiUrl;
        if (BtnAboutReleases is not null) BtnAboutReleases.Tag = HttpClientHeaderDefaults.ReleasesUrl;
        if (BtnAboutIssues is not null) BtnAboutIssues.Tag = HttpClientHeaderDefaults.IssuesUrl;
        if (ChkEnableAutoUpdateCheck is not null) ChkEnableAutoUpdateCheck.IsChecked = cfg.EnableAutoUpdateCheck;
        if (CmbAiSttEngine is not null) CmbAiSttEngine.SelectedValue = string.IsNullOrWhiteSpace(cfg.AiSttEngine) ? "auto" : cfg.AiSttEngine;
        if (CmbWhisperModel is not null) CmbWhisperModel.SelectedValue = string.IsNullOrWhiteSpace(cfg.WhisperModelId) ? "tiny" : cfg.WhisperModelId;
        if (SldRadioVolume is not null) SldRadioVolume.Value = Math.Clamp(cfg.RadioVolumePercent, 0, 100);
        if (ChkRadioAutoDuck is not null) ChkRadioAutoDuck.IsChecked = cfg.RadioAutoDuckEnabled;
        RefreshWhisperStatusUi();
        RefreshCockpitRadioStationsUi();
        InitializeDeckEditorUi();
        RefreshWebCertificateTrustUi();
        TxtAiDynamicCooldown.Text = cfg.AiDynamicEventCooldownSeconds.ToString();
        TxtFuelLowThreshold.Text = cfg.FuelLowThresholdPercent.ToString();
        TxtFuelCriticalThreshold.Text = cfg.FuelCriticalThresholdPercent.ToString();
        TxtHullCriticalThreshold.Text = cfg.HullCriticalThresholdPercent.ToString();
        TxtMissionExpiryWarningHours.Text = cfg.MissionExpiryWarningHours.ToString();
        TxtMissionExpiryCriticalHours.Text = cfg.MissionExpiryCriticalHours.ToString();
        SldAlertSoundVolume.Value = Math.Clamp(cfg.AlertSoundVolumePercent, 0, 100);
        int speechRate = Math.Clamp(cfg.SpeechRate, -5, 5);
        if (speechRate != cfg.SpeechRate)
        {
            var text = SettingsTranslations.ForLanguage(_uiLanguage);
            string warning = string.Format(text["voiceRateInvalid"], cfg.SpeechRate, speechRate);
            try
            {
                _configService.SetSpeechRate(speechRate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                cfg.SpeechRate = speechRate;
                warning += " " + string.Format(text["voiceRateSaveError"], ex.Message);
            }
            SetStatusBarMessage($"⚠ {warning}");
        }
        SldSpeechRate.Value = speechRate;
        TxtSpeechRate.Text = speechRate.ToString("+0;-0;0");
        string speechQueueMode = cfg.SpeechQueueMode is "immediate" or "sequential" ? cfg.SpeechQueueMode : "tactical";
        cfg.SpeechQueueMode = speechQueueMode;
        CmbSpeechQueueMode.SelectedValue = speechQueueMode;
        int speechExpirationSeconds = Math.Clamp(cfg.SpeechQueueExpirationSeconds, 2, 15);
        cfg.SpeechQueueExpirationSeconds = speechExpirationSeconds;
        SldSpeechExpiration.Value = speechExpirationSeconds;
        UpdateSpeechQueueUi();
        ChkCaptureUnhandledEvents.IsChecked = cfg.CaptureUnhandledEvents;
        ChkWriteUnhandledEventsFile.IsChecked = cfg.WriteUnhandledEventsToFile;
        ChkGroupUnhandledEvents.IsChecked = cfg.GroupUnhandledEvents;
        TxtMiningTargets.Text = cfg.MiningTargetMinerals;
        TxtMiningMinPercent.Text = Math.Clamp(cfg.MiningMinPercent, 1, 100).ToString();
        RefreshSurfaceBookmarksUi();
        SldDesktopFontScale.Value = Math.Clamp(cfg.DesktopFontScalePercent, 50, 200);
        BuildAlertSettingsGrid();

        UpdateThemePresetOptions();

        _suppressEvents = false;
        ApplyNavigationMenuPosition(cfg.NavigationMenuPosition);
        ApplyNavigationMenuTextVisibility(cfg.HideNavigationMenuText);
        ApplyDesktopFontScale(SldDesktopFontScale.Value / 100);
    }

    private void ApplyNavigationLabels()
    {
        var text = NavigationTranslations.ForLanguage(_uiLanguage);
        (System.Windows.Controls.RadioButton Button, string Key)[] buttons =
        {
            (NavDashboard, "dashboard"), (NavCockpit, "flight"), (NavNavigation, "navigation"),
            (NavOperations, "operations"), (NavInventory, "inventory"), (NavActivity, "activity"), (NavControls, "controls"),
            (NavConfig, "settings")
        };
        foreach (var (button, key) in buttons)
        {
            var entry = text[key];
            if (button.Content is not StackPanel item || item.Children.Count < 2 || item.Children[1] is not TextBlock label)
                throw new InvalidOperationException($"Contenido de navegación no válido: {key}.");
            label.Text = entry.Label;
            button.Language = System.Windows.Markup.XmlLanguage.GetLanguage(_uiLanguage);
            button.ToolTip = entry.Tooltip;
            System.Windows.Automation.AutomationProperties.SetName(button, entry.AccessibleName);
        }
    }


    private void ApplyNavigationMenuPosition(string position)
    {
        string normalizedPosition = position is "left" or "right" or "top" or "bottom"
            ? position
            : "left";

        MainLayoutGrid.ColumnDefinitions.Clear();
        MainLayoutGrid.RowDefinitions.Clear();

        switch (normalizedPosition)
        {
            case "right":
                MainLayoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                MainLayoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(MainContentGrid, 0);
                Grid.SetRow(MainContentGrid, 0);
                Grid.SetColumn(NavigationMenu, 1);
                Grid.SetRow(NavigationMenu, 0);
                NavigationMenu.BorderThickness = new Thickness(1, 0, 0, 0);
                SetNavigationBorderThickness(new Thickness(0, 0, 4, 0));
                break;
            case "top":
                MainLayoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                MainLayoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                MainLayoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(NavigationMenu, 0);
                Grid.SetRow(NavigationMenu, 0);
                Grid.SetColumn(MainContentGrid, 0);
                Grid.SetRow(MainContentGrid, 1);
                NavigationMenu.BorderThickness = new Thickness(0, 0, 0, 1);
                SetNavigationBorderThickness(new Thickness(0, 0, 0, 4));
                break;
            case "bottom":
                MainLayoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                MainLayoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                MainLayoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(NavigationMenu, 0);
                Grid.SetRow(NavigationMenu, 1);
                Grid.SetColumn(MainContentGrid, 0);
                Grid.SetRow(MainContentGrid, 0);
                NavigationMenu.BorderThickness = new Thickness(0, 1, 0, 0);
                SetNavigationBorderThickness(new Thickness(0, 4, 0, 0));
                break;
            default:
                MainLayoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                MainLayoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(NavigationMenu, 0);
                Grid.SetRow(NavigationMenu, 0);
                Grid.SetColumn(MainContentGrid, 1);
                Grid.SetRow(MainContentGrid, 0);
                NavigationMenu.BorderThickness = new Thickness(0, 0, 1, 0);
                SetNavigationBorderThickness(new Thickness(4, 0, 0, 0));
                break;
        }

        bool horizontalNavigation = normalizedPosition is "top" or "bottom";
        NavigationMenu.Width = !horizontalNavigation && _configService.Current.HideNavigationMenuText
            ? 76
            : double.NaN;
        if (!horizontalNavigation)
        {
            int menuColumn = normalizedPosition == "right" ? 1 : 0;
            MainLayoutGrid.ColumnDefinitions[menuColumn].Width =
                _configService.Current.HideNavigationMenuText ? new GridLength(76) : GridLength.Auto;
        }
        NavigationItems.Orientation = horizontalNavigation
            ? System.Windows.Controls.Orientation.Horizontal
            : System.Windows.Controls.Orientation.Vertical;
        NavigationScrollViewer.HorizontalScrollBarVisibility = horizontalNavigation
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Disabled;
        NavigationScrollViewer.VerticalScrollBarVisibility = horizontalNavigation
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
    }

    private void SetNavigationBorderThickness(Thickness thickness)
    {
        NavDashboard.BorderThickness = thickness;
        NavCockpit.BorderThickness = thickness;
        NavNavigation.BorderThickness = thickness;
        NavOperations.BorderThickness = thickness;
        NavInventory.BorderThickness = thickness;
        NavActivity.BorderThickness = thickness;
        NavControls.BorderThickness = thickness;
        NavConfig.BorderThickness = thickness;
    }

    private void ApplyNavigationMenuTextVisibility(bool hideText)
    {
        foreach (var radioButton in NavigationItems.Children.OfType<System.Windows.Controls.RadioButton>())
        {
            if (radioButton.Content is not StackPanel item)
                continue;

            radioButton.HorizontalContentAlignment = hideText
                ? System.Windows.HorizontalAlignment.Center
                : System.Windows.HorizontalAlignment.Left;
            radioButton.Padding = hideText
                ? new Thickness(0, 12 * _desktopFontScale, 0, 12 * _desktopFontScale)
                : new Thickness(16 * _desktopFontScale, 12 * _desktopFontScale, 16 * _desktopFontScale, 12 * _desktopFontScale);

            var icon = item.Children.OfType<TextBlock>().FirstOrDefault();
            if (icon != null)
                icon.Margin = hideText ? new Thickness(0) : new Thickness(0, 0, 10, 0);

            foreach (var label in item.Children.OfType<TextBlock>().Skip(1))
                label.Visibility = hideText ? Visibility.Collapsed : Visibility.Visible;
        }

        bool horizontalNavigation = _configService.Current.NavigationMenuPosition is "top" or "bottom";
        NavigationMenu.Width = hideText && !horizontalNavigation ? 76 : double.NaN;
        if (!horizontalNavigation)
        {
            int menuColumn = _configService.Current.NavigationMenuPosition == "right" ? 1 : 0;
            MainLayoutGrid.ColumnDefinitions[menuColumn].Width =
                hideText ? new GridLength(76) : GridLength.Auto;
        }
    }

    private void ApplyDesktopFontScale(double scale)
    {
        ApplyDesktopFontScale(scale, this);
    }

    private void ApplyDesktopFontScale(double scale, DependencyObject root)
    {
        double newScale = Math.Clamp(scale, 0.5, 2.0);
        var elements = EnumerateVisualElements(root).ToArray();

        foreach (FrameworkElement element in elements)
        {
            if (_desktopFontBaseSizes.TryGetValue(element, out _) ||
                BindingOperations.IsDataBound(element, TextElement.FontSizeProperty))
            {
                continue;
            }

            double currentSize = TextElement.GetFontSize(element);
            var source = DependencyPropertyHelper.GetValueSource(element, TextElement.FontSizeProperty);
            double baseSize = source.BaseValueSource == BaseValueSource.Inherited
                ? currentSize / _desktopFontScale
                : currentSize;
            double? lineHeight = element is TextBlock block && !double.IsNaN(block.LineHeight)
                ? block.LineHeight
                : null;
            _desktopFontBaseSizes.Add(element, new FontSizeBaseline(baseSize, lineHeight));
        }

        foreach (FrameworkElement element in elements)
        {
            if (_desktopFontBaseSizes.TryGetValue(element, out FontSizeBaseline? baseline))
            {
                TextElement.SetFontSize(element, baseline.BaseSize * newScale);
                if (element is TextBlock block && baseline.LineHeight is double lineHeight)
                    block.LineHeight = lineHeight * newScale;
            }
        }

        foreach (var radioButton in NavigationItems.Children.OfType<System.Windows.Controls.RadioButton>())
        {
            radioButton.FontSize = 13 * newScale;
            radioButton.Padding = _configService.Current.HideNavigationMenuText
                ? new Thickness(0, 12 * newScale, 0, 12 * newScale)
                : new Thickness(16 * newScale, 12 * newScale, 16 * newScale, 12 * newScale);
        }

        TxtMaterialSearch.Width = 190 * newScale;
        CmbMaterialCategory.Width = 180 * newScale;
        CmbMaterialRarity.Width = 175 * newScale;
        TxtMarketSearch.Width = TxtProfileSearch.Width = 190 * newScale;
        CmbMarketCategory.Width = CmbProfileCategory.Width = 180 * newScale;
        TxtCustomAccentHex.Width = TxtCustomSecondaryHex.Width = 105 * newScale;
        TxtAiDynamicCooldown.Width = 56 * newScale;
        TxtUnhandledSearch.Width = 220 * newScale;
        TxtShipLockerSearch.Width = 210 * newScale;
        TxtWishlistTarget.Width = 62 * newScale;
        TxtTargetLat.Width = TxtTargetLon.Width = 90 * newScale;
        TxtBookmarkLabel.Width = 210 * newScale;
        TxtMiningTargets.Width = 280 * newScale;
        TxtMiningMinPercent.Width = 52 * newScale;
        SizeComboBoxToLongestItem(CmbAlertVoice,
            CmbAlertVoice.Items.OfType<SpeechVoiceOption>().Select(option => option.DisplayName));
        SizeComboBoxToLongestItem(CmbSpeechQueueMode,
            CmbSpeechQueueMode.Items.OfType<System.Windows.Controls.ComboBoxItem>().Select(item => item.Content as string ?? ""));
        SizeComboBoxToLongestItem(CmbThemePresets,
            CmbThemePresets.Items.OfType<ThemePresetOption>().Select(option => option.Name));
        UpdateWebUrlListHeight(LstWebUrls.Items.Count);

        _desktopFontScale = newScale;
        UpdateAllResponsiveSplits(root);
        UpdateNavigationActionsColumns();
    }

    private void ScaledTemplateText_Loaded(object sender, RoutedEventArgs e)
    {
        var text = (TextBlock)sender;
        if (!_desktopFontBaseSizes.TryGetValue(text, out FontSizeBaseline? baseline))
        {
            baseline = new FontSizeBaseline(text.FontSize, null);
            _desktopFontBaseSizes.Add(text, baseline);
        }
        text.FontSize = baseline.BaseSize * _desktopFontScale;
    }

    private static IEnumerable<FrameworkElement> EnumerateVisualElements(DependencyObject node)
    {
        if (node is FrameworkElement element)
        {
            if (element.Visibility == Visibility.Collapsed)
                yield break;
            yield return element;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            foreach (FrameworkElement child in EnumerateVisualElements(VisualTreeHelper.GetChild(node, i)))
                yield return child;
        }
    }

    private async void ChkHideNavigationMenuText_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Nav Menu Text Toggle", async () =>
        {
            if (_suppressEvents)
                return;

            bool hideText = ChkHideNavigationMenuText.IsChecked == true;
            _configService.Current.HideNavigationMenuText = hideText;
            _configService.Save();
            ApplyNavigationMenuTextVisibility(hideText);
            await _webServer.BroadcastStateAsync();
        });

    private void SldDesktopFontScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtDesktopFontScale is null || SldDesktopFontScale is null)
            return;

        int percent = (int)SldDesktopFontScale.Value;
        TxtDesktopFontScale.Text = $"{percent} %";
        _pendingFontScalePercent = percent;

        if (_suppressEvents)
        {
            ApplyDesktopFontScale(percent / 100d);
            return;
        }

        // Coalesce rapid Ctrl+Wheel / slider drags (45 ms) so WPF doesn't re-layout on every wheel tick
        if (_fontZoomApplyTimer is null)
        {
            _fontZoomApplyTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(45)
            };
            _fontZoomApplyTimer.Tick += (_, _) =>
            {
                _fontZoomApplyTimer.Stop();
                ApplyDesktopFontScale(_pendingFontScalePercent / 100d);
            };
        }
        _fontZoomApplyTimer.Stop();
        _fontZoomApplyTimer.Start();

        // Debounce config.json disk writes (400 ms) so zooming never stalls on file I/O
        _configService.Current.DesktopFontScalePercent = percent;
        if (_fontZoomSaveTimer is null)
        {
            _fontZoomSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _fontZoomSaveTimer.Tick += (_, _) =>
            {
                _fontZoomSaveTimer.Stop();
                _configService.Save();
            };
        }
        _fontZoomSaveTimer.Stop();
        _fontZoomSaveTimer.Start();
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            _fontZoomWheelDelta = 0;
            DependencyObject? source = e.OriginalSource as DependencyObject;
            while (source is not null && source is not (System.Windows.Controls.DataGrid or System.Windows.Controls.ListBox))
                source = GetVisualParent(source);
            if (source is FrameworkElement listOrGrid)
            {
                var inner = EnumerateVisualElements(listOrGrid).OfType<ScrollViewer>().FirstOrDefault();
                bool canScroll = inner is not null &&
                    inner.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled &&
                    (e.Delta > 0
                        ? inner.VerticalOffset > 0
                        : inner.VerticalOffset < inner.ScrollableHeight);
                if (!canScroll)
                {
                    DependencyObject? parent = GetVisualParent(listOrGrid);
                    while (parent is not null && parent is not ScrollViewer)
                        parent = GetVisualParent(parent);
                    if (parent is ScrollViewer outer)
                    {
                        e.Handled = true;
                        outer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                        {
                            RoutedEvent = Mouse.MouseWheelEvent,
                            Source = outer
                        });
                    }
                }
            }
            return;
        }

        e.Handled = true;
        _fontZoomWheelDelta += e.Delta;
        int steps = _fontZoomWheelDelta / 120;
        _fontZoomWheelDelta %= 120;
        if (steps != 0)
        {
            SldDesktopFontScale.Value = Math.Clamp(
                SldDesktopFontScale.Value + steps * SldDesktopFontScale.TickFrequency,
                SldDesktopFontScale.Minimum,
                SldDesktopFontScale.Maximum);
        }
    }

    private static DependencyObject? GetVisualParent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : (node as FrameworkContentElement)?.Parent;

    private void AlertSettings_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source != null && source != sender)
        {
            if (source is ScrollViewer { VerticalScrollBarVisibility: not ScrollBarVisibility.Disabled })
                return;

            source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : (source as FrameworkContentElement)?.Parent;
        }

        e.Handled = true;
        ViewConfig.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent
        });
    }

    private async void CmbNavigationMenuPosition_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await RunUiAsync("Nav Menu Position", async () =>
        {
            if (_suppressEvents || CmbNavigationMenuPosition.SelectedValue is not string position)
                return;

            ApplyNavigationMenuPosition(position);
            _configService.Current.NavigationMenuPosition = position;
            _configService.Save();
            await _webServer.BroadcastStateAsync();
        });


    private void OnShipStateUpdated(ShipState state)
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        lock (_shipUiUpdateLock)
        {
            _pendingShipUiState = state;
            if (_shipUiUpdateQueued)
                return;
            _shipUiUpdateQueued = true;
        }

        Dispatcher.BeginInvoke(() =>
        {
            ShipState? latest;
            lock (_shipUiUpdateLock)
            {
                latest = _pendingShipUiState;
                _pendingShipUiState = null;
                _shipUiUpdateQueued = false;
            }

            if (latest != null)
                UpdateShipUi(latest);
        });
    }


    private void UpdateShipUi(ShipState s)
    {
        UpdateDashboardUi(s);
        UpdateFlightUi(s);

        static (int, string, string, double, double, bool) RouteRowKey(RouteWaypoint row) =>
            (row.Index, row.StarSystem, row.StarClass, row.StepDistanceLy, row.DistanceFromStartLy, row.IsCurrentSystem);
        if (GridNavRoute.ItemsSource is not IReadOnlyList<RouteWaypoint> currentRoute ||
            (!ReferenceEquals(currentRoute, s.NavRoute) &&
             !currentRoute.Select(RouteRowKey).SequenceEqual(s.NavRoute.Select(RouteRowKey))))
            GridNavRoute.ItemsSource = s.NavRoute;

        // Bodega y Diario
        if (!ReferenceEquals(GridCargo.ItemsSource, s.CargoInventory))
            GridCargo.ItemsSource = s.CargoInventory;

        var targetMaterials = s.HasMaterialsTelemetry ? s.MaterialsInventory : null;
        if (!ReferenceEquals(GridMaterials.ItemsSource, targetMaterials) && !GridMaterials.IsKeyboardFocusWithin)
        {
            GridMaterials.ItemsSource = targetMaterials;
            ApplyMaterialFilters();
        }
        UpdateMaterialTraderSummary(s);
        UpdateShipLockerUi(s);
        UpdateEngineersUi(s);
        UpdateMiningUi(s);
        UpdateTargetAndCombatUi(s);
        UpdateCarrierAdvancedUi(s);
        UpdateLandingPadUi(s);
        UpdateMiniHudContextUi(s);
        UpdateCarrierCountdownAndRunway(s);
        UpdateMissionsGroupedSummary(s);
        UpdateCargoTradeProfitUi(s);
        UpdateEngineeringWishlistUi(s);
        UpdateFrontierCapiDataUi(s);
        UpdateBgsAndSystemNoteUi(s);
        UpdateSessionStatsUi(s);
        AdvanceSpanshRouteOnSystemChange(s);
        UpdateSurfaceNavigationUi(s);
        UpdateMaterialsStatus(s);

        int currentMinute = DateTimeOffset.Now.Minute;
        if (!ReferenceEquals(GridMissions.ItemsSource, s.ActiveMissions))
        {
            GridMissions.ItemsSource = s.ActiveMissions;
            _lastMissionRefreshMinute = currentMinute;
        }
        else if (s.ActiveMissions.Count > 0 && currentMinute != _lastMissionRefreshMinute)
        {
            _lastMissionRefreshMinute = currentMinute;
            System.Windows.Data.CollectionViewSource.GetDefaultView(GridMissions.ItemsSource)?.Refresh();
        }

        if (!ReferenceEquals(GridPassengers.ItemsSource, s.PassengerManifest))
            GridPassengers.ItemsSource = s.PassengerManifest;
        if (!ReferenceEquals(GridBackpack.ItemsSource, s.BackpackInventory))
            GridBackpack.ItemsSource = s.BackpackInventory;
        if (!ReferenceEquals(GridMarket.ItemsSource, s.Market.Items))
            UpdateMarketUi(s);
        if (!ReferenceEquals(GridOutfitting.ItemsSource, s.Outfitting.Items))
            GridOutfitting.ItemsSource = s.Outfitting.Items;
        if (!ReferenceEquals(GridShipyard.ItemsSource, s.Shipyard.Items))
            GridShipyard.ItemsSource = s.Shipyard.Items;
        if (!ReferenceEquals(_lastProfileSource, s.CommanderProfile))
            UpdateProfileUi(s);
        UpdateOperationsStatus(s);
        if (!ReferenceEquals(GridColonisationResources.ItemsSource, s.Colonisation.Resources))
            GridColonisationResources.ItemsSource = s.Colonisation.Resources;

        if (!ReferenceEquals(_lastRecentEventsSource, s.RecentEvents))
            UpdateJournalUi(s);
        if (!ReferenceEquals(_lastExplorationBodiesSource, s.ExplorationBodies))
            UpdateExplorationUi(s);
        if (_lastCarrierState is not { } carrierKey ||
            !ReferenceEquals(carrierKey.Source, s.Carrier) ||
            !string.Equals(carrierKey.Language, _uiLanguage, StringComparison.Ordinal))
            UpdateCarrierUi(s);
    }

    private void UpdateJournalUi(ShipState s)
    {
        _lastRecentEventsSource = s.RecentEvents;
        var journalSelected = (GridJournal.SelectedItem as JournalEntryView)?.Entry;
        var explorationSelected = (GridExplorationEvents.SelectedItem as JournalEntryView)?.Entry;
        var rows = s.RecentEvents.Select(entry => JournalTranslations.Present(entry, _uiLanguage)).ToList();
        if (!_viewingHistoricalJournal)
        {
            GridJournal.ItemsSource = rows;
            GridJournal.SelectedItem = rows.FirstOrDefault(row => ReferenceEquals(row.Entry, journalSelected));
        }
        string[] explorationEventTypes =
        {
            "FSSDiscoveryScan",
            "FSSAllBodiesFound",
            "SAAScanComplete",
            "ScanOrganic",
            "CodexEntry",
            "SellExplorationData",
            "MultiSellExplorationData"
        };
        var explorationRows = rows
            .Where(entry => explorationEventTypes.Contains(entry.EventType, StringComparer.Ordinal))
            .ToList();
        GridExplorationEvents.ItemsSource = explorationRows;
        GridExplorationEvents.SelectedItem = explorationRows.FirstOrDefault(row => ReferenceEquals(row.Entry, explorationSelected));
        var explorationText = ExplorationTranslations.ForLanguage(_uiLanguage);
        TxtExplorationEventsStatus.Text = explorationRows.Count == 0
            ? explorationText["milestonesEmpty"]
            : string.Format(explorationText["milestonesCount"], explorationRows.Count);
    }

}
