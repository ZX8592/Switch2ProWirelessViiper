using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Switch2ProWirelessViiper.Core;
using Windows.Devices.Bluetooth;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI.Text;
using WinRT.Interop;
using MediaBrush = Microsoft.UI.Xaml.Media.Brush;
using XamlEllipse = Microsoft.UI.Xaml.Shapes.Ellipse;

namespace Switch2ProWirelessViiper;

public sealed partial class MainWindow : Window
{
    private const int MaxLogCharacters = 64 * 1024;
    private const long MaxDiagnosticLogBytes = 2 * 1024 * 1024;

    private readonly BleScanner _scanner = new();
    private readonly Fd2ReportParser _parser = new();
    private readonly HdRumbleEncoder _rumble = new();
    private readonly Switch2State _state = new();
    private readonly Switch2State _submitState = new();
    private readonly Switch2State _viewState = new();
    private readonly object _stateLock = new();
    private readonly object _rumbleLock = new();
    private readonly SemaphoreSlim _submitSignal = new(0, int.MaxValue);
    private readonly StringBuilder _logBuffer = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly AppSettings _settings;
    private readonly FrameworkElement[] _menuPanels;
    private string _currentPanelName = "HomePanel";
    private bool _navigationLoaded;
    private bool _timerResolutionSet;
    private bool _loadingSettings;
    private bool _allowExit;
    private bool _isHiddenToTray;
    private bool _stickCalibrationRunning;
    private CancellationTokenSource? _backgroundTrimCts;
    private IntPtr _hwnd;

    private Grid RootGrid = null!;
    private Grid AppTitleBar = null!;
    private XamlEllipse StatusDot = null!;
    private TextBlock StatusText = null!;
    private TextBlock AppTitleText = null!;
    private TextBlock AppSubtitleText = null!;
    private TextBlock MainHintText = null!;
    private TextBlock TrayHintText = null!;
    private Button ConnectButton = null!;
    private FontIcon ConnectGlyph = null!;
    private TextBlock ConnectButtonText = null!;
    private NavigationView AppNavigation = null!;
    private Grid NavigationContentHost = null!;
    private NavigationViewItem HomeNavItem = null!;
    private NavigationViewItem SetupNavItem = null!;
    private NavigationViewItem StatusNavItem = null!;
    private NavigationViewItem PerformanceNavItem = null!;
    private NavigationViewItem SettingsNavItem = null!;
    private NavigationViewItem LogNavItem = null!;
    private Grid HomePanel = null!;
    private ScrollViewer SetupPanel = null!;
    private ScrollViewer StatusPanel = null!;
    private ScrollViewer PerformancePanel = null!;
    private ScrollViewer SettingsPanel = null!;
    private Grid LogPanel = null!;
    private TextBox AddressBox = null!;
    private TextBox ViiperAddressBox = null!;
    private TextBox ViiperExeBox = null!;
    private TextBox ScanSecondsBox = null!;
    private ComboBox CandidatesBox = null!;
    private ComboBox LanguageCombo = null!;
    private ComboBox OnboardingLanguageCombo = null!;
    private CheckBox MinimizeToTrayCheckBox = null!;
    private CheckBox StartupCheckBox = null!;
    private CheckBox StartToTrayCheckBox = null!;
    private CheckBox PreloadViiperCheckBox = null!;
    private TextBlock BluetoothTitleText = null!;
    private Expander BluetoothSection = null!;
    private TextBlock ControllerTitleText = null!;
    private Expander ControllerSection = null!;
    private TextBlock BleAddressLabel = null!;
    private Button ScanButton = null!;
    private Button StickCalibrateButton = null!;
    private TextBlock StickCalibrationText = null!;
    private TextBlock ViiperTitleText = null!;
    private Expander ViiperSection = null!;
    private TextBlock ApiAddressLabel = null!;
    private TextBlock ViiperExeLabel = null!;
    private Button BrowseViiperButton = null!;
    private TextBlock InputsTitleText = null!;
    private TextBlock MotionTitleText = null!;
    private TextBlock LanguageLabel = null!;
    private TextBlock ConfigPathLabel = null!;
    private TextBlock ConfigPathText = null!;
    private Button OpenConfigFolderButton = null!;
    private TextBlock FeedbackTitleText = null!;
    private TextBlock FeedbackDescriptionText = null!;
    private Button FeedbackButton = null!;
    private Button ClearLogButton = null!;
    private Grid OnboardingOverlay = null!;
    private TextBlock OnboardingTitleText = null!;
    private TextBlock OnboardingSubtitleText = null!;
    private TextBlock OnboardingLanguageLabel = null!;
    private TextBlock EnvironmentTitleText = null!;
    private TextBlock EnvironmentStatusText = null!;
    private Button OnboardingScanButton = null!;
    private TextBlock ViiperStatusText = null!;
    private TextBlock BleStatusText = null!;
    private TextBlock FramesText = null!;
    private TextBlock ButtonsText = null!;
    private TextBlock LeftXBar = null!;
    private TextBlock LeftYBar = null!;
    private TextBlock RightXBar = null!;
    private TextBlock RightYBar = null!;
    private TextBlock MotionText = null!;
    private TextBlock BleLinkText = null!;
    private TextBlock BleRateText = null!;
    private TextBlock SubmitRateText = null!;
    private TextBlock BridgeLatencyText = null!;
    private TextBlock BleIntervalText = null!;
    private TextBlock LastInputAgeText = null!;
    private TextBlock BacklogText = null!;
    private Button TestRumbleButton = null!;
    private TextBlock RumbleWritesText = null!;
    private TextBlock RumbleErrorsText = null!;
    private TextBox LogBox = null!;

    private BleClient? _ble;
    private ViiperBridge? _viiper;
    private ViiperServerWarmup? _viiperWarmup;
    private Task? _viiperWarmupTask;
    private CancellationTokenSource? _viiperWarmupCts;
    private string? _viiperWarmupKey;
    private WndProcDelegate? _windowProc;
    private IntPtr _oldWindowProc;
    private bool _trayIconAdded;
    private string _trayConnectText = "Connect";
    private string _trayExitText = "Exit";
    private CancellationTokenSource? _sessionCts;
    private Task? _submitTask;
    private Task? _lowLatencyRefreshTask;
    private Task? _rumbleTask;
    private byte[]? _currentRumblePacket;
    private DateTimeOffset _rumbleUntil;
    private int _stopPacketsPending;
    private long _parsedFrames;
    private long _submittedFrames;
    private long _rumbleWrites;
    private long _rumbleErrors;
    private long _submitErrors;
    private long _firstFrameTicks;
    private long _lastFrameTicks;
    private long _lastInterReportTicks;
    private long _lastBridgeLatencyTicks;
    private long _lastViiperSubmitTicks;
    private long _totalBridgeLatencyTicks;
    private long _bridgeLatencySamples;
    private long _maxBridgeLatencyTicks;
    private long _stateVersion;
    private long _lastStateNotificationTicks;
    private long _rawInputNotifications;
    private long _rejectedInputNotifications;
    private long _lastPerformanceLogTicks;
    private long _lastParsedRateSample;
    private long _lastSubmittedRateSample;
    private long _lastRateSampleTicks = Stopwatch.GetTimestamp();
    private int _submitSignalPending;
    private double _bleRateHz;
    private double _submitRateHz;

    public bool ShouldStartToTray { get; }

    public MainWindow()
    {
        BuildUi();
        _scanner.Trace += (_, message) => RunOnUi(() => Log(message));
        _menuPanels =
        [
            HomePanel,
            SetupPanel,
            StatusPanel,
            PerformancePanel,
            SettingsPanel,
            LogPanel,
        ];

        _settings = AppSettings.Load();
        InitializeDiagnosticLog();
        _parser.ApplyStickCalibration(_settings.StickCalibration);
        ShouldStartToTray = ShouldLaunchToTray(_settings);
        ConfigureWindow();
        EnableProcessPerformanceMode();
        _loadingSettings = true;
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _uiTimer.Tick += (_, _) => UpdateLiveView();
        _uiTimer.Start();

        LeftXBar.Text = Switch2State.StickCenter.ToString();
        LeftYBar.Text = Switch2State.StickCenter.ToString();
        RightXBar.Text = Switch2State.StickCenter.ToString();
        RightYBar.Text = Switch2State.StickCenter.ToString();

        var localViiper = Path.Combine(AppContext.BaseDirectory, "viiper.exe");
        if (File.Exists(localViiper))
        {
            ViiperExeBox.Text = localViiper;
        }

        ApplySettingsToUi(localViiper);
        ResizeMainWindow();
        SetupTrayIcon();
        ShowMenuPanel(HomePanel);
        ApplyLanguage();
        SetStatus(T("statusIdle"), StatusBrush("neutral"));
        UpdateEnvironmentStatus();
        UpdateConnectionUi();
        _loadingSettings = false;
        SaveSettingsFromUi();
        if (_settings.PreloadViiper && _settings.FirstRunCompleted)
        {
            StartViiperWarmup();
            if (ShouldStartToTray)
            {
                _ = LoadViiperAsync();
            }
        }
        else
        {
            ScheduleIdleMemoryTrim();
        }

        RootGrid.Loaded += (_, _) =>
        {
            ResizeMainWindow();
            if (!ShouldStartToTray && _settings.PreloadViiper && _settings.FirstRunCompleted)
            {
                _ = LoadViiperAsync();
            }
            var args = Environment.GetCommandLineArgs();
            if (args.Any(arg => arg.Equals("--test-setup-page", StringComparison.OrdinalIgnoreCase)))
            {
                ShowSecondaryMenu();
            }
            else if (args.Any(arg => arg.Equals("--test-status-page", StringComparison.OrdinalIgnoreCase)))
            {
                ShowSecondaryMenu(1);
            }
            else if (args.Any(arg => arg.Equals("--test-settings-page", StringComparison.OrdinalIgnoreCase)))
            {
                ShowSecondaryMenu(4);
            }
            else if (!_settings.FirstRunCompleted)
            {
                ShowOnboarding();
            }
        };

        Log("Ready. This app only uses BLE + VIIPER ns2pro.");
        LogStartupDiagnostics();
    }

    private static bool ShouldLaunchToTray(AppSettings settings)
    {
        var args = Environment.GetCommandLineArgs();
        if (!settings.FirstRunCompleted ||
            args.Any(arg => arg.StartsWith("--test-", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return settings.StartToTray ||
               args.Any(arg => arg.Equals("--tray", StringComparison.OrdinalIgnoreCase));
    }

    private void BuildUi()
    {
        RootGrid = new Grid
        {
            
        };
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Content = RootGrid;

        BuildTitleBar();
        BuildNavigation();
        BuildOnboardingNew();
    }

    private void BuildTitleBar()
    {
        AppTitleBar = new Grid
        {
            Height = 48,
            Padding = new Thickness(16, 0, 200, 0),
        };
        RootGrid.Children.Add(AppTitleBar);

        var title = Text("Switch 2 Pro Wireless VIIPER", 13, Weight(400), null);
        title.VerticalAlignment = VerticalAlignment.Center;
        AppTitleBar.Children.Add(title);

        var statusStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        StatusDot = new XamlEllipse
        {
            Width = 9,
            Height = 9,
            Style = (Style)Application.Current.Resources["StatusNeutralStyle"],
            Margin = new Thickness(0, 0, 10, 0),
        };
        StatusText = Text("Idle", 13, Weight(600), null);
        statusStack.Children.Add(StatusDot);
        statusStack.Children.Add(StatusText);
        AppTitleBar.Children.Add(statusStack);
    }

    private Grid BuildHomePanel()
    {
        var main = new Grid
        {
            Name = "HomePanel",
            Padding = new Thickness(12, 24, 12, 24),
        };
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var center = new Grid
        {
            Width = 560,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.Children.Add(center);

        AppTitleText = Text("Switch 2 Pro", 28, Weight(600), null);
        AppTitleText.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(AppTitleText, 0);
        AppSubtitleText = Text("Wireless VIIPER bridge", 14, Weight(400), null);
        AppSubtitleText.HorizontalAlignment = HorizontalAlignment.Center;
        AppSubtitleText.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(AppSubtitleText, 1);
        center.Children.Add(AppTitleText);
        center.Children.Add(AppSubtitleText);

        ConnectGlyph = new FontIcon
        {
            FontFamily = SymbolIconFontFamily(),
            Glyph = "\uE768",
            FontSize = 18,
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ConnectButtonText = Text("Connect", 15, Weight(600), null);
        ConnectButtonText.VerticalAlignment = VerticalAlignment.Center;
        ConnectButtonText.TextWrapping = TextWrapping.NoWrap;
        var connectContent = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ColumnSpacing = 10,
        };
        connectContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        connectContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        connectContent.Children.Add(ConnectGlyph);
        Grid.SetColumn(ConnectButtonText, 1);
        connectContent.Children.Add(ConnectButtonText);
        ConnectButton = new Button
        {
            Width = 248,
            Height = 56,
            Margin = new Thickness(0, 24, 0, 0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = connectContent,
        };
        ApplyStyle(ConnectButton, "AccentButtonStyle");
        ConnectButton.Click += Connect_Click;
        Grid.SetRow(ConnectButton, 2);
        center.Children.Add(ConnectButton);

        MainHintText = Text("Open menu for setup and diagnostics.", 12, Weight(400), null);
        MainHintText.HorizontalAlignment = HorizontalAlignment.Center;
        MainHintText.TextAlignment = TextAlignment.Center;
        MainHintText.TextWrapping = TextWrapping.Wrap;
        MainHintText.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(MainHintText, 3);
        center.Children.Add(MainHintText);

        TrayHintText = Text("Closing the window keeps the bridge in the system tray.", 12, Weight(400), null);
        TrayHintText.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(TrayHintText, 1);
        main.Children.Add(TrayHintText);

        return main;
    }

    private void BuildNavigation()
    {
        AppNavigation = new NavigationView
        {
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = true,
            IsSettingsVisible = false,
            PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact,
            OpenPaneLength = 280,
            CompactPaneLength = 48,
            IsPaneOpen = false,
            SelectionFollowsFocus = NavigationViewSelectionFollowsFocus.Disabled,
            
        };
        Grid.SetRow(AppNavigation, 1);
        RootGrid.Children.Add(AppNavigation);
        AppNavigation.ItemInvoked += Navigation_ItemInvoked;
        AppNavigation.Loaded += (_, _) =>
        {
            _navigationLoaded = true;
            TrySelectNavigationItem(_currentPanelName);
        };

        HomeNavItem = NavItem("Home", "HomePanel", "\uE80F");
        SetupNavItem = NavItem("Setup", "SetupPanel", "\uE713");
        StatusNavItem = NavItem("Status", "StatusPanel", "\uE946");
        PerformanceNavItem = NavItem("Performance", "PerformancePanel", "\uE9D2");
        SettingsNavItem = NavItem("Settings", "SettingsPanel", "\uE713");
        LogNavItem = NavItem("Log", "LogPanel", "\uE8A5");

        AppNavigation.MenuItems.Add(HomeNavItem);
        AppNavigation.MenuItems.Add(SetupNavItem);
        AppNavigation.MenuItems.Add(StatusNavItem);
        AppNavigation.MenuItems.Add(PerformanceNavItem);
        AppNavigation.MenuItems.Add(LogNavItem);
        AppNavigation.FooterMenuItems.Add(SettingsNavItem);

        NavigationContentHost = new Grid
        {
            
        };
        AppNavigation.Content = NavigationContentHost;

        HomePanel = BuildHomePanel();
        SetupPanel = ScrollPanel("SetupPanel", BuildSetupPanel());
        StatusPanel = ScrollPanel("StatusPanel", BuildStatusPanel());
        PerformancePanel = ScrollPanel("PerformancePanel", BuildPerformancePanel());
        SettingsPanel = ScrollPanel("SettingsPanel", BuildSettingsPanel());
        LogPanel = BuildLogPanel();
    }



    private StackPanel BuildSetupPanel()
    {
        var root = ContentStack();

        var bluetooth = new StackPanel();
        BluetoothTitleText = SectionTitle("Bluetooth");
        BleAddressLabel = Label("BLE address");
        AddressBox = new TextBox();
        bluetooth.Children.Add(BleAddressLabel);
        bluetooth.Children.Add(AddressBox);

        var scanGrid = new Grid { Margin = new Thickness(0, 12, 0, 0), ColumnSpacing = 8 };
        scanGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scanGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        scanGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        CandidatesBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "DisplayText" };
        CandidatesBox.SelectionChanged += CandidatesBox_SelectionChanged;
        ScanSecondsBox = new TextBox { Text = "12" };
        ScanButton = new Button { Content = "Scan" };
        ScanButton.Click += Scan_Click;
        scanGrid.Children.Add(CandidatesBox);
        Grid.SetColumn(ScanSecondsBox, 1);
        scanGrid.Children.Add(ScanSecondsBox);
        Grid.SetColumn(ScanButton, 2);
        scanGrid.Children.Add(ScanButton);
        bluetooth.Children.Add(scanGrid);

        AutoDisconnectLabel = Label("Auto disconnect minutes (0 = disable)");
        AutoDisconnectLabel.Margin = new Thickness(0, 14, 0, 6);
        AutoDisconnectBox = new TextBox
        {
            Margin = new Thickness(0, 0, 0, 8),
            MaxWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            PlaceholderText = "30",
        };
        AutoDisconnectBox.TextChanged += (s, e) => SaveSettingsFromUi();
        bluetooth.Children.Add(AutoDisconnectLabel);
        bluetooth.Children.Add(AutoDisconnectBox);

        BluetoothSection = Section("Bluetooth", bluetooth);
        root.Children.Add(BluetoothSection);

        var viiper = new StackPanel();
        ViiperTitleText = SectionTitle("VIIPER");
        ApiAddressLabel = Label("API address");
        ViiperAddressBox = new TextBox { Text = "localhost:3242" };
        ViiperExeLabel = Label("viiper.exe");
        ViiperExeLabel.Margin = new Thickness(0, 14, 0, 6);
        var fileGrid = new Grid { ColumnSpacing = 8 };
        fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ViiperExeBox = new TextBox();
        BrowseViiperButton = new Button { Content = "Browse" };
        BrowseViiperButton.Click += BrowseViiper_Click;
        fileGrid.Children.Add(ViiperExeBox);
        Grid.SetColumn(BrowseViiperButton, 1);
        fileGrid.Children.Add(BrowseViiperButton);
        viiper.Children.Add(ApiAddressLabel);
        viiper.Children.Add(ViiperAddressBox);
        viiper.Children.Add(ViiperExeLabel);
        viiper.Children.Add(fileGrid);
        PreloadViiperCheckBox = SettingCheckBox("Preload VIIPER");
        viiper.Children.Add(PreloadViiperCheckBox);
        ViiperSection = Section("VIIPER", viiper);
        root.Children.Add(ViiperSection);

        var controller = new StackPanel();
        ControllerTitleText = SectionTitle("Controller");
        var rumbleButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        TestRumbleButton = new Button { Content = "Test", IsEnabled = false };
        TestRumbleButton.Click += TestRumble_Click;
        rumbleButtons.Children.Add(TestRumbleButton);
        controller.Children.Add(rumbleButtons);

        RumbleWritesText = Value("0");
        RumbleErrorsText = Value("0");
        RumbleWritesLabel = AddPair(controller, "Writes", RumbleWritesText);
        RumbleErrorsLabel = AddPair(controller, "Errors", RumbleErrorsText);

        StickCalibrateButton = new Button { Content = "Calibrate sticks", Margin = new Thickness(0, 14, 0, 0) };
        StickCalibrateButton.Click += StickCalibrate_Click;
        StickCalibrationText = Text("Not calibrated", 12, Weight(400), UiBrush(Color(0x60, 0x60, 0x60)));
        StickCalibrationText.TextWrapping = TextWrapping.Wrap;
        StickCalibrationText.Margin = new Thickness(0, 8, 0, 0);
        controller.Children.Add(StickCalibrateButton);
        controller.Children.Add(StickCalibrationText);
        ControllerSection = Section("Controller", controller);
        root.Children.Add(ControllerSection);

        return root;
    }

    private StackPanel BuildStatusPanel()
    {
        var root = ContentStack();

        var statusCard = Card();
        var status = new StackPanel();
        ViiperStatusText = Value("not loaded");
        BleStatusText = Value("idle");
        FramesText = Value("0 parsed / 0 submitted");
        ButtonsText = Value("None");
        StatusViiperLabel = AddPair(status, "VIIPER", ViiperStatusText);
        StatusBleLabel = AddPair(status, "BLE", BleStatusText);
        StatusFramesLabel = AddPair(status, "Frames", FramesText);
        StatusButtonsLabel = AddPair(status, "Buttons", ButtonsText);
        statusCard.Child = status;
        root.Children.Add(statusCard);

        var inputCard = Card();
        var input = new StackPanel();
        InputsTitleText = SectionTitle("Inputs");
        input.Children.Add(InputsTitleText);
        LeftXBar = Value(Switch2State.StickCenter.ToString());
        LeftYBar = Value(Switch2State.StickCenter.ToString());
        RightXBar = Value(Switch2State.StickCenter.ToString());
        RightYBar = Value(Switch2State.StickCenter.ToString());
        InputLeftXLabel = AddPair(input, "Left X", LeftXBar);
        InputLeftYLabel = AddPair(input, "Left Y", LeftYBar);
        InputRightXLabel = AddPair(input, "Right X", RightXBar);
        InputRightYLabel = AddPair(input, "Right Y", RightYBar);
        inputCard.Child = input;
        root.Children.Add(inputCard);

        var motionCard = Card();
        var motion = new StackPanel();
        MotionTitleText = SectionTitle("Motion sample");
        MotionText = Text("none", 12, Weight(400), UiBrush(Color(0x1F, 0x1F, 0x1F)));
        MotionText.TextWrapping = TextWrapping.Wrap;
        MotionText.FontFamily = new FontFamily("Consolas");
        motion.Children.Add(MotionTitleText);
        motion.Children.Add(MotionText);
        motionCard.Child = motion;
        root.Children.Add(motionCard);

        return root;
    }

    private StackPanel BuildPerformancePanel()
    {
        var root = ContentStack();
        var card = Card();
        var stack = new StackPanel();
        BleLinkText = Value("not requested");
        BleRateText = Value("0.0 Hz");
        SubmitRateText = Value("0.0 Hz");
        BridgeLatencyText = Value("waiting");
        BleIntervalText = Value("waiting");
        LastInputAgeText = Value("none");
        BacklogText = Value("0 / 0");
        PerfBleLinkLabel = AddPair(stack, "BLE link", BleLinkText);
        PerfBleRateLabel = AddPair(stack, "BLE report rate", BleRateText);
        PerfSubmitRateLabel = AddPair(stack, "VIIPER submit rate", SubmitRateText);
        PerfLatencyLabel = AddPair(stack, "Bridge latency", BridgeLatencyText);
        PerfBleIntervalLabel = AddPair(stack, "BLE interval", BleIntervalText);
        PerfInputAgeLabel = AddPair(stack, "Last input age", LastInputAgeText);
        PerfBacklogLabel = AddPair(stack, "Backlog / errors", BacklogText);
        card.Child = stack;
        root.Children.Add(card);
        return root;
    }

    private StackPanel BuildSettingsPanel()
    {
        var root = ContentStack();
        var card = Card();
        var stack = new StackPanel();
        LanguageLabel = Label("Language");
        LanguageCombo = LanguageSelector();
        LanguageCombo.SelectionChanged += LanguageCombo_SelectionChanged;
        MinimizeToTrayCheckBox = SettingCheckBox("Close to system tray");
        StartupCheckBox = SettingCheckBox("Start with Windows");
        StartToTrayCheckBox = SettingCheckBox("Start hidden in tray");
        ConfigPathLabel = Label("Config file");
        ConfigPathLabel.Margin = new Thickness(0, 18, 0, 6);
        ConfigPathText = Text(string.Empty, 12, Weight(400), UiBrush(Color(0x60, 0x60, 0x60)));
        ConfigPathText.TextWrapping = TextWrapping.Wrap;
        OpenConfigFolderButton = new Button { Content = "Open folder", Margin = new Thickness(0, 10, 0, 0) };
        OpenConfigFolderButton.Click += OpenConfigFolderButton_Click;
        stack.Children.Add(LanguageLabel);
        stack.Children.Add(LanguageCombo);
        stack.Children.Add(MinimizeToTrayCheckBox);
        stack.Children.Add(StartupCheckBox);
        stack.Children.Add(StartToTrayCheckBox);
        stack.Children.Add(ConfigPathLabel);
        stack.Children.Add(ConfigPathText);
        stack.Children.Add(OpenConfigFolderButton);
        card.Child = stack;
        root.Children.Add(card);

        var feedbackCard = Card();
        var feedbackStack = new StackPanel { Spacing = 8 };
        FeedbackTitleText = Text("Feedback", 18, Weight(600), null);
        FeedbackDescriptionText = Text(
            "Send a problem report or suggestion.",
            13,
            Weight(400),
            UiBrush(Color(0x60, 0x60, 0x60)));
        FeedbackDescriptionText.TextWrapping = TextWrapping.Wrap;
        FeedbackButton = new Button
        {
            Content = "Send feedback",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
        };
        FeedbackButton.Click += FeedbackButton_Click;
        feedbackStack.Children.Add(FeedbackTitleText);
        feedbackStack.Children.Add(FeedbackDescriptionText);
        feedbackStack.Children.Add(FeedbackButton);
        feedbackCard.Child = feedbackStack;
        root.Children.Add(feedbackCard);
        return root;
    }

    private Grid BuildLogPanel()
    {
        var grid = new Grid
        {
            Name = "LogPanel",
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(36, 24, 36, 24),
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ClearLogButton = new Button { Content = "Clear", Margin = new Thickness(0, 0, 0, 12) };
        ClearLogButton.Click += ClearLog_Click;
        LogBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
        };
        grid.Children.Add(ClearLogButton);
        Grid.SetRow(LogBox, 1);
        grid.Children.Add(LogBox);
        return grid;
    }

    private static StackPanel ContentStack() => new()
    {
        Padding = new Thickness(12, 24, 12, 24),
        Spacing = 24,
        MaxWidth = 760,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static Border Card() => new()
    {
        Style = (Style)Application.Current.Resources["CardStyle"],
        
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16),
    };

    private static Expander Section(string header, UIElement content) => new()
    {
        Header = header,
        IsExpanded = true,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Content = new Border
        {
            Padding = new Thickness(0, 12, 0, 0),
            Child = content,
        },
    };

    private static ScrollViewer ScrollPanel(string name, StackPanel content)
    {
        return new ScrollViewer
        {
            Name = name,
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

        private static TextBlock Text(string value, double size, FontWeight weight, MediaBrush? brush)
    {
        var tb = new TextBlock
        {
            Text = value,
            FontSize = size,
            FontWeight = weight,
        };
        if (brush != null) tb.Foreground = brush;
        return tb;
    }

    private static TextBlock Label(string value)
    {
        return new TextBlock
        {
            Text = value,
            FontSize = 12,
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            Margin = new Thickness(0, 0, 0, 6),
        };
    }

    private static TextBlock SectionTitle(string value)
    {
        return new TextBlock
        {
            Text = value,
            FontSize = 14,
            FontWeight = Weight(600),
            Style = (Style)Application.Current.Resources["PrimaryTextStyle"],
            Margin = new Thickness(0, 0, 0, 12),
        };
    }

    private static TextBlock Value(string value)
    {
        return new TextBlock
        {
            Text = value,
            FontWeight = Weight(600),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["PrimaryTextStyle"],
            Margin = new Thickness(0, 2, 0, 10),
        };
    }

    private CheckBox SettingCheckBox(string content)
    {
        var checkBox = new CheckBox { Content = content, Margin = new Thickness(0, 10, 0, 0) };
        checkBox.Checked += SettingsCheckBox_Changed;
        checkBox.Unchecked += SettingsCheckBox_Changed;
        return checkBox;
    }

    private static ComboBox LanguageSelector()
    {
        var combo = new ComboBox();
        combo.Items.Add(new ComboBoxItem { Tag = "zh-CN", Content = "\u4e2d\u6587" });
        combo.Items.Add(new ComboBoxItem { Tag = "ja-JP", Content = "\u65e5\u672c\u8a9e" });
        combo.Items.Add(new ComboBoxItem { Tag = "en-US", Content = "English" });
        return combo;
    }

    private static Button IconButton(string glyph)
    {
        return new Button
        {
            Width = 40,
            Height = 40,
            Content = new FontIcon
            {
                FontFamily = SymbolIconFontFamily(),
                Glyph = glyph,
                FontSize = 16,
            },
        };
    }



    private static NavigationViewItem NavItem(string content, string tag, string glyph)
    {
        return new NavigationViewItem
        {
            Content = content,
            Tag = tag,
            Icon = new FontIcon
            {
                FontFamily = SymbolIconFontFamily(),
                Glyph = glyph,
            }
        };
    }

    private static FontFamily SymbolIconFontFamily()
    {
        try
        {
            if (Application.Current?.Resources.TryGetValue("SymbolThemeFontFamily", out var value) == true)
            {
                if (value is FontFamily fontFamily)
                {
                    return fontFamily;
                }

                if (value is string source && !string.IsNullOrWhiteSpace(source))
                {
                    return new FontFamily(source);
                }
            }
        }
        catch
        {
        }

        return new FontFamily("Segoe MDL2 Assets");
    }

    private static void SetNavItemText(NavigationViewItem item, string text)
    {
        item.Content = text;
    }



    private static TextBlock AddPair(StackPanel stack, string label, TextBlock value)
    {
        var lbl = Label(label); stack.Children.Add(lbl);
        stack.Children.Add(value); return lbl;
    }

    private static Windows.UI.Color Color(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);

    private static FontWeight Weight(ushort value) => new() { Weight = value };

    private void ConfigureWindow()
    {
        Title = "Switch 2 Pro Wireless VIIPER";
        _hwnd = WindowNative.GetWindowHandle(this);
        AppWindow.Closing += OnAppWindowClosing;

        try
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.BackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.InactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        }
        catch
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }

        try
        {
            AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico"));
        }
        catch
        {
        }

        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
        }

        try
        {
            AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x20, 0, 0, 0);
            AppWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x30, 0, 0, 0);
        }
        catch
        {
        }

        Closed += OnWindowClosed;
    }

    private void ResizeMainWindow()
    {
        try
        {
            var dpi = GetDpiForWindow(_hwnd);
            var scale = dpi == 0 ? 1.0 : dpi / 96.0;
            var width = (int)(Math.Max(_settings.WindowWidth > 0 ? _settings.WindowWidth : 980, 480) * scale);
            var height = (int)(Math.Max(_settings.WindowHeight > 0 ? _settings.WindowHeight : 680, 640) * scale);
            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            var workArea = displayArea.WorkArea;
            width = Math.Min(width, Math.Max((int)(480 * scale), workArea.Width - 48));
            height = Math.Min(height, Math.Max((int)(640 * scale), workArea.Height - 48));
            var x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
            var y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        catch
        {
            AppWindow.Resize(new SizeInt32(980, 680));
        }
    }

    private void SaveWindowSize()
    {
        if (!AppWindow.IsVisible) return;
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Restored)
        {
            var dpi = GetDpiForWindow(_hwnd);
            var scale = dpi == 0 ? 1.0 : dpi / 96.0;
            _settings.WindowWidth = (int)(AppWindow.Size.Width / scale);
            _settings.WindowHeight = (int)(AppWindow.Size.Height / scale);
        }
    }
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowExit && _settings.MinimizeToTray)
        {
            args.Cancel = true;
            SaveWindowSize();
            SaveSettingsFromUi();
            HideToTray();
        }
        else
        {
            SaveWindowSize();
            SaveSettingsFromUi();
        }
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _uiTimer.Stop();
        _backgroundTrimCts?.Cancel();
        _backgroundTrimCts?.Dispose();
        _backgroundTrimCts = null;
        DisableProcessPerformanceMode();
        RemoveTrayIcon();
        DetachWindowProc();

        await DisconnectAsync().ConfigureAwait(true);
                if (_viiper is not null)
        {
            _viiper.HidOutputReceived -= OnViiperHidOutput;
            try { await _viiper.DisposeAsync().ConfigureAwait(true); } catch { }
        }
        await StopViiperWarmupAsync().ConfigureAwait(true);
    }

    private void ApplySettingsToUi(string localViiper)
    {
        AddressBox.Text = _settings.BluetoothAddress;
        ViiperAddressBox.Text = string.IsNullOrWhiteSpace(_settings.ViiperAddress)
            ? "localhost:3242"
            : _settings.ViiperAddress;
        ViiperExeBox.Text = !string.IsNullOrWhiteSpace(_settings.ViiperExePath)
            ? _settings.ViiperExePath
            : localViiper;
        MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTray;
        StartupCheckBox.IsChecked = _settings.StartWithWindows;
        StartToTrayCheckBox.IsChecked = _settings.StartToTray;
        PreloadViiperCheckBox.IsChecked = _settings.PreloadViiper;
        AutoDisconnectBox.Text = _settings.AutoDisconnectMinutes.ToString("0.###", CultureInfo.CurrentCulture);
        SelectLanguageCombo(LanguageCombo, _settings.Language);
        SelectLanguageCombo(OnboardingLanguageCombo, _settings.Language);
    }

        private TextBlock StatusViiperLabel = null!;
    private TextBlock StatusBleLabel = null!;
    private TextBlock StatusFramesLabel = null!;
    private TextBlock StatusButtonsLabel = null!;
    
    private TextBlock InputLeftXLabel = null!;
    private TextBlock InputLeftYLabel = null!;
    private TextBlock InputRightXLabel = null!;
    private TextBlock InputRightYLabel = null!;
    private TextBlock PerfBleLinkLabel = null!;
    private TextBlock PerfBleRateLabel = null!;
    private TextBlock PerfSubmitRateLabel = null!;
    private TextBlock PerfLatencyLabel = null!;
    private TextBlock PerfBleIntervalLabel = null!;
    private TextBlock PerfInputAgeLabel = null!;
    private TextBlock PerfBacklogLabel = null!;
    
    private TextBox AutoDisconnectBox = null!;
    private TextBlock AutoDisconnectLabel = null!;

    private long _lastActiveTicks = Stopwatch.GetTimestamp();
    private Switch2Button _lastActiveButtons;
    private ushort _lastActiveLeftX = Switch2State.StickCenter;
    private ushort _lastActiveLeftY = Switch2State.StickCenter;
    private ushort _lastActiveRightX = Switch2State.StickCenter;
    private ushort _lastActiveRightY = Switch2State.StickCenter;

    private TextBlock RumbleWritesLabel = null!;
    private TextBlock RumbleErrorsLabel = null!;
    private void SaveSettingsFromUi()
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.BluetoothAddress = AddressBox.Text.Trim();
        _settings.ViiperAddress = ViiperAddressBox.Text.Trim();
        _settings.ViiperExePath = ViiperExeBox.Text.Trim();
        _settings.MinimizeToTray = MinimizeToTrayCheckBox.IsChecked == true;
        _settings.StartWithWindows = StartupCheckBox.IsChecked == true;
        _settings.StartToTray = StartToTrayCheckBox.IsChecked == true;
        _settings.PreloadViiper = PreloadViiperCheckBox.IsChecked == true;
        if (TryParseAutoDisconnectMinutes(AutoDisconnectBox.Text, out var minutes))
        {
            _settings.AutoDisconnectMinutes = minutes;
        }
        _settings.Language = SelectedLanguage(LanguageCombo);

_settings.Save();
        ApplyStartupRegistration();
        UpdateTrayMenu();
    }

    private static bool TryParseAutoDisconnectMinutes(string text, out double minutes)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out minutes) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out minutes))
        {
            minutes = Math.Clamp(minutes, 0, 24 * 60);
            return true;
        }

        minutes = 0;
        return false;
    }

    private void SetupTrayIcon()
    {
        AttachWindowProc();
        var data = CreateNotifyIconData(NotifyIconFlags.Message | NotifyIconFlags.Icon | NotifyIconFlags.Tip);
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
        var hIcon = LoadImage(IntPtr.Zero, iconPath, 1, 0, 0, 0x00000010);
        data.hIcon = hIcon != IntPtr.Zero ? hIcon : LoadIcon(IntPtr.Zero, SystemIconApplication);
        data.uCallbackMessage = WindowMessage.TrayIcon;
        data.szTip = "Switch 2 Pro Wireless VIIPER";
        _trayIconAdded = Shell_NotifyIcon(NotifyIconMessage.Add, ref data);

        UpdateTrayMenu();
    }

    private void BeginOnUi(Func<Task> action)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await action().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log("Tray action failed: " + ex.Message);
            }
        });
    }

    private void HideToTray()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(_hwnd, ShowWindowCommand.Hide);
        _isHiddenToTray = true;
        ScheduleBackgroundMemoryTrim();
    }

    private void ShowFromTray(bool openMenu)
    {
        _isHiddenToTray = false;
        _backgroundTrimCts?.Cancel();
        SyncLogBox();
        if (_hwnd != IntPtr.Zero)
        {
            ShowWindow(_hwnd, ShowWindowCommand.Show);
            ShowWindow(_hwnd, ShowWindowCommand.Restore);
        }

        Activate();
        UpdateLiveView();
        if (openMenu)
        {
            ShowSecondaryMenu();
        }
    }

    private async Task ExitApplicationAsync()
    {
        _allowExit = true;
        SaveWindowSize();
        SaveSettingsFromUi();
        RemoveTrayIcon();

        await DisconnectAsync().ConfigureAwait(true);
        Close();
    }

    private void ApplyStartupRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                writable: true);
            if (key is null)
            {
                return;
            }

            const string valueName = "Switch2ProWirelessViiper";
            if (_settings.StartWithWindows)
            {
                var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    var args = _settings.StartToTray ? " --tray" : string.Empty;
                    key.SetValue(valueName, $"\"{exe}\"{args}");
                }
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log("Startup registration failed: " + ex.Message);
        }
    }

    private void ShowOnboarding()
    {
        UpdateEnvironmentStatus();
        OnboardingOverlay.Visibility = Visibility.Visible;
    }

    private void ShowSecondaryMenu(int pageIndex = 0)
    {
        var panelName = pageIndex switch
        {
            1 => "StatusPanel",
            2 => "PerformancePanel",
            3 => "SettingsPanel",
            4 => "SettingsPanel",
            5 => "LogPanel",
            _ => "SetupPanel",
        };
        ShowMenuPanel(panelName);
    }

    private void HideSecondaryMenu()
    {
        ShowMenuPanel("HomePanel");
    }

    private void ShowMenuPanel(object? panelTag)
    {
        var targetName = panelTag is FrameworkElement element
            ? element.Name
            : panelTag?.ToString() ?? SetupPanel.Name;
        if (_menuPanels is null)
        {
            return;
        }

        _currentPanelName = targetName;
        var targetPanel = PanelForName(targetName);
        if (targetPanel is null)
        {
            return;
        }

        NavigationContentHost.Children.Clear();
        foreach (var panel in _menuPanels)
        {
            panel.Visibility = ReferenceEquals(panel, targetPanel)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        NavigationContentHost.Children.Add(targetPanel);
        TrySelectNavigationItem(targetName);
    }

    private void Navigation_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item)
        {
            ShowMenuPanel(item.Tag);
        }
    }

    private FrameworkElement? PanelForName(string panelName) => panelName switch
    {
        "HomePanel" => HomePanel,
        "SetupPanel" => SetupPanel,
        "StatusPanel" => StatusPanel,
        "PerformancePanel" => PerformancePanel,
        "SettingsPanel" => SettingsPanel,
        "LogPanel" => LogPanel,
        _ => null,
    };

    private void TrySelectNavigationItem(string panelName)
    {
        if (!_navigationLoaded)
        {
            return;
        }

        var selectedItem = NavItemForPanel(panelName);
        if (selectedItem is not null && !ReferenceEquals(AppNavigation.SelectedItem, selectedItem))
        {
            try
            {
                AppNavigation.SelectedItem = selectedItem;
            }
            catch (Exception ex)
            {
                Log("Navigation selection sync failed: " + ex.Message);
            }
        }
    }

    private NavigationViewItem? NavItemForPanel(string panelName) => panelName switch
    {
        "HomePanel" => HomeNavItem,
        "SetupPanel" => SetupNavItem,
        "StatusPanel" => StatusNavItem,
        "PerformancePanel" => PerformanceNavItem,
        "SettingsPanel" => SettingsNavItem,
        "LogPanel" => LogNavItem,
        _ => null,
    };

    private string PageTitleForPanel(string panelName) => panelName switch
    {
        "HomePanel" => T("home"),
        "SetupPanel" => T("setup"),
        "StatusPanel" => T("status"),
        "PerformancePanel" => T("performance"),
        "SettingsPanel" => T("settings"),
        "LogPanel" => T("log"),
        _ => string.Empty,
    };

    private void SettingsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        SaveSettingsFromUi();
        if (ReferenceEquals(sender, PreloadViiperCheckBox))
        {
            if (_settings.PreloadViiper)
            {
                StartViiperWarmup(force: true);
            }
            else
            {
                _ = ReleasePreloadedViiperAsync();
            }
        }
    }

    private void StartViiperWarmup(bool force = false)
    {
        var key = CurrentViiperWarmupKey();
        if (_viiperWarmupTask is { IsCompleted: false })
        {
            return;
        }

        if (!force && _viiperWarmup is not null && string.Equals(_viiperWarmupKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _viiperWarmupCts?.Cancel();
        _viiperWarmupCts?.Dispose();
        _viiperWarmupCts = new CancellationTokenSource();
        _viiperWarmupKey = key;
        _viiperWarmupTask = WarmupViiperServerAsync(_viiperWarmupCts.Token);
    }

    private string CurrentViiperWarmupKey() =>
        $"{ViiperAddressBox.Text.Trim()}\u0000{ViiperExeBox.Text.Trim()}";

    private async Task WarmupViiperServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            ViiperStatusText.Text = T("preloading");
            Log("Preloading VIIPER server...");
            var warmup = await ViiperBridge.WarmupServerAsync(
                    ViiperAddressBox.Text.Trim(),
                    string.IsNullOrWhiteSpace(ViiperExeBox.Text) ? null : ViiperExeBox.Text.Trim(),
                    cancellationToken,
                    message => RunOnUi(() => Log(message)))
                .ConfigureAwait(false);

            var old = _viiperWarmup;
            _viiperWarmup = warmup;
            if (old is not null)
            {
                await old.DisposeAsync().ConfigureAwait(false);
            }

            RunOnUi(() =>
            {
                ViiperStatusText.Text = warmup.Description;
                Log(warmup.Description);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                ViiperStatusText.Text = T("notLoaded");
                Log("VIIPER preload failed: " + ex.Message);
            });
        }
    }

    private async Task EnsureViiperWarmupAsync(CancellationToken cancellationToken)
    {
        var task = _viiperWarmupTask;
        if (task is null)
        {
            StartViiperWarmup();
            task = _viiperWarmupTask;
        }

        if (task is null)
        {
            return;
        }

        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StopViiperWarmupAsync()
    {
        try
        {
            _viiperWarmupCts?.Cancel();
            if (_viiperWarmupTask is not null)
            {
                await _viiperWarmupTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                _viiperWarmupTask = null;
            }

            if (_viiperWarmup is not null)
            {
                await _viiperWarmup.DisposeAsync().ConfigureAwait(false);
                _viiperWarmup = null;
            }
        }
        catch
        {
        }
        finally
        {
            _viiperWarmupCts?.Dispose();
            _viiperWarmupCts = null;
        }
    }

    private async Task ReleasePreloadedViiperAsync()
    {
        await StopViiperWarmupAsync().ConfigureAwait(true);
        if (!IsBleConnected && _viiper is not null)
        {
            _viiper.HidOutputReceived -= OnViiperHidOutput;
            try { await _viiper.DisposeAsync().ConfigureAwait(true); } catch { }
            _viiper = null;
            ViiperStatusText.Text = T("notLoaded");
            UpdateConnectionUi();
        }

        ScheduleIdleMemoryTrim();
    }

    private static void SelectLanguageCombo(ComboBox combo, string language)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), language, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = language.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? 2 :
            language.Equals("ja-JP", StringComparison.OrdinalIgnoreCase) || language.Equals("ja", StringComparison.OrdinalIgnoreCase) ? 1 :
            0;
    }

    private static string SelectedLanguage(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is not null)
        {
            return item.Tag.ToString() ?? "zh-CN";
        }

        return "zh-CN";
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.Language = SelectedLanguage(LanguageCombo);
        _loadingSettings = true;
        SelectLanguageCombo(OnboardingLanguageCombo, _settings.Language);
        _loadingSettings = false;
        ApplyLanguage();
        SaveSettingsFromUi();
    }

    private void OnboardingLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.Language = SelectedLanguage(OnboardingLanguageCombo);
        _loadingSettings = true;
        SelectLanguageCombo(LanguageCombo, _settings.Language);
        _loadingSettings = false;
        ApplyLanguage();
        SaveSettingsFromUi();
    }

    private void ApplyLanguage()
    {
        AppTitleText.Text = T("appTitle");
        AppSubtitleText.Text = T("appSubtitle");
        MainHintText.Text = T("mainHint");
        TrayHintText.Text = T("trayHint");
        SetNavItemText(HomeNavItem, T("home"));
        SetNavItemText(SetupNavItem, T("setup"));
        SetNavItemText(StatusNavItem, T("status"));
        SetNavItemText(PerformanceNavItem, T("performance"));
        SetNavItemText(SettingsNavItem, T("settings"));
        SetNavItemText(LogNavItem, T("log"));
        BluetoothTitleText.Text = T("bluetooth");
        BluetoothSection.Header = T("bluetooth");
        ControllerTitleText.Text = T("controller");
        ControllerSection.Header = T("controller");
        BleAddressLabel.Text = T("bleAddress");
        ScanButton.Content = T("scan");
        StickCalibrateButton.Content = T("calibrateSticks");
        if (!_stickCalibrationRunning)
        {
            StickCalibrationText.Text = _settings.StickCalibration?.IsUsable == true
                ? T("stickCalibrationSaved")
                : T("stickCalibrationNotSaved");
        }
        ViiperTitleText.Text = T("viiper");
        ViiperSection.Header = T("viiper");
        ApiAddressLabel.Text = T("apiAddress");
        ViiperExeLabel.Text = T("viiperExe");
        BrowseViiperButton.Content = T("browse");
        InputsTitleText.Text = T("inputs");
        MotionTitleText.Text = T("motion");
        TestRumbleButton.Content = T("test");
        LanguageLabel.Text = T("language");
        MinimizeToTrayCheckBox.Content = T("closeToTray");
        StartupCheckBox.Content = T("startWithWindows");
        StartToTrayCheckBox.Content = T("startToTray");
        PreloadViiperCheckBox.Content = T("preloadViiper");
        AutoDisconnectLabel.Text = T("autoDisconnect");
        ConfigPathLabel.Text = T("configFile");
        UpdateConnectionUi();
        UpdateTrayMenu();
        ConfigPathText.Text = AppSettings.SettingsPath;
        OpenConfigFolderButton.Content = T("openFolder");
        FeedbackTitleText.Text = T("feedbackTitle");
        FeedbackDescriptionText.Text = T("feedbackDescription");
        FeedbackButton.Content = T("feedbackButton");
        ClearLogButton.Content = T("clear");
        OnboardingTitleText.Text = T("onboardingTitle");
        UpdateOnboardingStepUi();
        OnboardingLanguageLabel.Text = T("language");
        StatusViiperLabel.Text = T("viiper");
        StatusBleLabel.Text = "BLE";
        StatusFramesLabel.Text = T("frames");
        StatusButtonsLabel.Text = T("buttons");
        InputLeftXLabel.Text = T("leftX");
        InputLeftYLabel.Text = T("leftY");
        InputRightXLabel.Text = T("rightX");
        InputRightYLabel.Text = T("rightY");
        PerfBleLinkLabel.Text = T("bleLink");
        PerfBleRateLabel.Text = T("bleRate");
        PerfSubmitRateLabel.Text = T("submitRate");
        PerfLatencyLabel.Text = T("latency");
        PerfBleIntervalLabel.Text = T("bleInterval");
        PerfInputAgeLabel.Text = T("inputAge");
        PerfBacklogLabel.Text = T("backlog");
        RumbleWritesLabel.Text = T("writes");
        RumbleErrorsLabel.Text = T("errors");
        StatusText.Text = IsBleConnected ? T("statusConnected") : T("statusIdle");
        
        if (OnboardingEnvDesc != null)
        {
            OnboardingEnvDesc.Text = T("envDesc");
            OnboardingScanTitle.Text = T("scanTitle");
            OnboardingScanDesc.Text = T("scanDesc");
            OnboardingSettingsTitle.Text = T("startupSettings");
            OnboardingStartupCheckBox.Content = T("startWithWindows");
            OnboardingStartToTrayCheckBox.Content = T("startToTray");
            OnboardingPreloadCheckBox.Content = T("preloadViiper");
            OnboardingCloseToTrayCheckBox.Content = T("closeToTray");
            OnboardingUsbipButton.Content = T("downloadUsbip");
            OnboardingBackButton.Content = T("back");
            OnboardingNextButton.Content = _onboardingStep == OnboardingStep.Settings ? T("finish") : T("next");
        }
        EnvironmentTitleText.Text = T("environment");
        OnboardingScanButton.Content = T("scan");
        UpdateConnectionUi();
        UpdateTrayMenu();
        UpdateEnvironmentStatus();
        UpdateLiveView();
        UpdateHomeHints();
    }

    private void UpdateHomeHints()
    {
        if (MainHintText != null)
        {
            var random = new Random();
            var hints = new[] { T("mainHint"), T("hint1"), T("hint2"), T("hint3"), T("hint4") };
            MainHintText.Text = hints[random.Next(hints.Length)];
        }
    }

    private string T(string key)
    {
        var lang = SelectedLanguage(LanguageCombo);
        if (lang == "zh-CN" || lang == "zh")
        {
            return key switch
            {
                "home" => "主页",
                "setup" => "连接设置",
                "status" => "状态",
                "performance" => "性能",
                "rumble" => "震动",
                "settings" => "设置",
                "log" => "日志",
                "bluetooth" => "蓝牙",
                "controller" => "手柄",
                "bleAddress" => "蓝牙地址",
                "viiper" => "VIIPER",
                "apiAddress" => "API 地址",
                "viiperExe" => "viiper.exe",
                "inputs" => "输入",
                "motion" => "体感",
                "test" => "测试震动",
                "scan" => "扫描",
                "calibrateSticks" => "校准摇杆",
                "stickCalibrationNotSaved" => "未校准。默认使用 1.6x 摇杆输出归一化。",
                "stickCalibrationConnectFirst" => "请先连接手柄，再开始校准摇杆。",
                "stickCalibrationCenter" => "请松开两个摇杆，正在采集中点...",
                "stickCalibrationMove" => "请将左右摇杆沿最大边缘缓慢转动几圈，正在采集端点...",
                "stickCalibrationSaved" => "摇杆校准已保存。",
                "stickCalibrationFailed" => "校准失败：没有采集到完整端点。请重新校准，并把两个摇杆都推到四周最大边缘。",
                "language" => "语言",
                "frames" => "帧数",
                "buttons" => "按键",
                "leftX" => "左摇杆X",
                "leftY" => "左摇杆Y",
                "rightX" => "右摇杆X",
                "rightY" => "右摇杆Y",
                "target" => "目标",
                "bleLink" => "蓝牙链路",
                "bleRate" => "蓝牙报告率",
                "submitRate" => "VIIPER 提交率",
                "latency" => "桥接延迟",
                "bleInterval" => "蓝牙报告间隔",
                "inputAge" => "距上次输入",
                "backlog" => "待提交 / 错误",
                "writes" => "写入次数",
                "errors" => "错误次数",
                "envDesc" => "请确认 usbip-win2 驱动已安装；未安装时 VIIPER 无法创建虚拟手柄。",
                "scanTitle" => "配对手柄",
                "scanDesc" => "按住手柄顶部配对键，直到指示灯快速闪烁，然后点击“扫描”。",
                "stepLanguage" => "语言",
                "stepEnvironment" => "环境检查",
                "stepScan" => "扫描手柄",
                "stepSettings" => "启动设置",
                "onboardingStepFormat" => "第 {0}/4 步：{1}",
                "startupSettings" => "启动设置",
                "downloadUsbip" => "下载 usbip-win2",
                "back" => "上一步",
                "next" => "下一步",
                "closeToTray" => "关闭窗口时隐藏到系统托盘",
                "startWithWindows" => "开机自启动",
                "startToTray" => "启动后隐藏到托盘",
                "preloadViiper" => "启动时预先加载 VIIPER",
                "autoDisconnect" => "闲置后自动断开 (分钟，0 表示禁用)",
                "configFile" => "配置文件",
                "openFolder" => "打开目录",
                "feedbackTitle" => "用户反馈",
                "feedbackDescription" => "发送问题报告或建议，并可选择附带诊断日志。",
                "feedbackButton" => "提交反馈",
                "feedbackDialogTitle" => "用户反馈",
                "feedbackMessage" => "反馈内容",
                "feedbackPlaceholder" => "请描述问题发生前后的操作、实际结果和预期结果。",
                "feedbackIncludeDiagnostics" => "附带诊断日志（不包含配置文件）",
                "feedbackPrivacyHint" => "诊断日志可能包含蓝牙设备标识和本地路径。取消勾选时只发送基础系统信息。",
                "feedbackSend" => "发送",
                "feedbackCancel" => "取消",
                "feedbackClose" => "关闭",
                "feedbackSending" => "正在发送，邮件服务器响应可能需要一些时间...",
                "feedbackSuccessFormat" => "反馈已发送。请求编号：{0}",
                "feedbackInvalid" => "反馈内容为空或超过 32 KiB 限制。",
                "feedbackBadRequest" => "反馈内容或诊断附件格式不符合接口要求。",
                "feedbackTooLarge" => "反馈内容或诊断附件超过大小限制。",
                "feedbackRateLimited" => "发送次数过多，请稍后再试。",
                "feedbackUnavailable" => "反馈服务暂时不可用，请稍后再试。",
                "feedbackFailedFormat" => "发送失败：{0}",
                "browse" => "浏览",
                "clear" => "清空",
                "loadingViiper" => "正在加载 VIIPER...",
                "loadViiper" => "加载 VIIPER",
                "onboardingTitle" => "初次启动向导",
                "onboardingSubtitle" => "选择语言、检查驱动、扫描手柄并完成启动设置。",
                "environment" => "环境检查",
                "finish" => "完成",
                "connect" => "连接",
                "disconnect" => "断开",
                "connecting" => "连接中...",
                "disconnecting" => "断开连接中...",
                "preloading" => "预加载中",
                "open" => "打开面板",
                "exit" => "退出程序",
                "statusIdle" => "空闲",
                "statusScanning" => "扫描中",
                "statusLoadingViiper" => "加载 VIIPER",
                "statusConnectingBle" => "连接蓝牙",
                "statusConnected" => "已连接",
                "statusNeedsSetup" => "需要配置",
                "statusConnectFailed" => "连接失败",
                "statusScanFailed" => "扫描失败",
                "statusDisconnected" => "已断开",
                "statusBleDisconnected" => "蓝牙已断开",
                "trayHint" => "关闭窗口后将根据设置决定是否驻留托盘。",
                "trayHintEnabled" => "关闭窗口后仍会驻留托盘，可从托盘菜单连接、断开或退出。",
                "trayHintDisabled" => "关闭窗口会断开手柄并退出程序。",
                "notRequested" => "尚未请求",
                "notLoaded" => "尚未加载",
                "envOk" => "已找到",
                "envMissing" => "未找到",
                "waiting" => "等待中",
                "noneValue" => "无",
                "connectedValue" => "已连接",
                "disconnectedValue" => "已断开",
                "framesFormat" => "{0} 已解析 / {1} 已提交",
                "rateFormat" => "{0:F1} Hz / 平均 {1:F1} Hz",
                "latencyFormat" => "最近 {0:F2} ms / 平均 {1:F2} ms / 最高 {2:F2} ms",
                "envStatusFormat" => "Windows: {0}{1}VIIPER: {2}{1}配置文件: {3}",
                "checkingUsbip" => "正在检查 usbip-win2...",
                "usbipReady" => "usbip-win2 已安装，可以继续。",
                "usbipMissing" => "未检测到 usbip-win2。请下载安装后再继续，否则 VIIPER 可能无法创建虚拟手柄。",
                "scanSuccessFormat" => "已找到 {0} ({1})。",
                "scanNotFound" => "未找到手柄。请确认手柄处于配对模式，指示灯正在闪烁。",
                "scanFailedFormat" => "扫描失败：{0}",
                "appTitle" => "Switch 2 Pro",
                "appSubtitle" => "无线 VIIPER 桥接",
                "mainHint" => "主界面只保留连接控制；更多状态、设置和日志在左侧菜单中。",
                "hint1" => "连接前请先在设置流程中扫描或填写手柄蓝牙地址。",
                "hint2" => "可以在设置中调整自动断开时间。",
                "hint3" => "状态页可以查看按键、摇杆和体感数据。",
                "hint4" => "遇到问题可查看日志面板。",
                _ => key,
            };
        }

        if (lang == "ja-JP" || lang == "ja")
        {
            return key switch
            {
                "home" => "ホーム",
                "setup" => "接続設定",
                "status" => "状態",
                "performance" => "パフォーマンス",
                "rumble" => "振動",
                "settings" => "設定",
                "log" => "ログ",
                "bluetooth" => "Bluetooth",
                "controller" => "コントローラー",
                "bleAddress" => "Bluetooth アドレス",
                "viiper" => "VIIPER",
                "apiAddress" => "API アドレス",
                "viiperExe" => "viiper.exe",
                "inputs" => "入力",
                "motion" => "モーション",
                "test" => "振動テスト",
                "scan" => "スキャン",
                "calibrateSticks" => "スティックを調整",
                "stickCalibrationNotSaved" => "未調整です。既定の 1.6x スティック正規化を使用します。",
                "stickCalibrationConnectFirst" => "スティックを調整する前にコントローラーを接続してください。",
                "stickCalibrationCenter" => "両方のスティックから手を離してください。中心を取得しています...",
                "stickCalibrationMove" => "左右のスティックを外周に沿ってゆっくり数回回してください。端点を取得しています...",
                "stickCalibrationSaved" => "スティック調整を保存しました。",
                "stickCalibrationFailed" => "調整に失敗しました: 十分な端点を取得できませんでした。もう一度、両方のスティックを外周全体まで倒してください。",
                "language" => "言語",
                "frames" => "フレーム",
                "buttons" => "ボタン",
                "leftX" => "左スティック X",
                "leftY" => "左スティック Y",
                "rightX" => "右スティック X",
                "rightY" => "右スティック Y",
                "target" => "目標",
                "bleLink" => "Bluetooth リンク",
                "bleRate" => "Bluetooth レポートレート",
                "submitRate" => "VIIPER 送信レート",
                "latency" => "ブリッジ遅延",
                "bleInterval" => "Bluetooth レポート間隔",
                "inputAge" => "最後の入力から",
                "backlog" => "待機 / エラー",
                "writes" => "書き込み回数",
                "errors" => "エラー回数",
                "envDesc" => "usbip-win2 ドライバーがインストールされていることを確認してください。未インストールの場合、VIIPER は仮想コントローラーを作成できません。",
                "scanTitle" => "コントローラーをペアリング",
                "scanDesc" => "ランプが速く点滅するまでコントローラー上部のペアリングボタンを押し続けてから、「スキャン」をクリックしてください。",
                "stepLanguage" => "言語",
                "stepEnvironment" => "環境チェック",
                "stepScan" => "コントローラーをスキャン",
                "stepSettings" => "起動設定",
                "onboardingStepFormat" => "ステップ {0}/4: {1}",
                "startupSettings" => "起動設定",
                "downloadUsbip" => "usbip-win2 をダウンロード",
                "back" => "戻る",
                "next" => "次へ",
                "closeToTray" => "ウィンドウを閉じたらシステムトレイに隠す",
                "startWithWindows" => "Windows 起動時に開始",
                "startToTray" => "起動時にトレイへ隠す",
                "preloadViiper" => "起動時に VIIPER を事前読み込み",
                "autoDisconnect" => "アイドル後に自動切断 (分、0 で無効)",
                "configFile" => "設定ファイル",
                "openFolder" => "フォルダーを開く",
                "feedbackTitle" => "フィードバック",
                "feedbackDescription" => "問題の報告や提案を送信し、必要に応じて診断ログを添付できます。",
                "feedbackButton" => "フィードバックを送信",
                "feedbackDialogTitle" => "フィードバック",
                "feedbackMessage" => "内容",
                "feedbackPlaceholder" => "問題が発生する前後の操作、実際の結果、期待する結果を入力してください。",
                "feedbackIncludeDiagnostics" => "診断ログを添付する（設定ファイルは含みません）",
                "feedbackPrivacyHint" => "診断ログには Bluetooth デバイス識別子やローカルパスが含まれる場合があります。オフにすると基本的なシステム情報のみ送信します。",
                "feedbackSend" => "送信",
                "feedbackCancel" => "キャンセル",
                "feedbackClose" => "閉じる",
                "feedbackSending" => "送信中です。メールサーバーの応答に時間がかかる場合があります...",
                "feedbackSuccessFormat" => "フィードバックを送信しました。リクエスト ID: {0}",
                "feedbackInvalid" => "内容が空か、32 KiB の上限を超えています。",
                "feedbackBadRequest" => "内容または診断ファイルの形式が正しくありません。",
                "feedbackTooLarge" => "内容または診断ファイルがサイズ上限を超えています。",
                "feedbackRateLimited" => "送信回数が多すぎます。しばらくしてから再試行してください。",
                "feedbackUnavailable" => "フィードバックサービスを一時的に利用できません。後でもう一度お試しください。",
                "feedbackFailedFormat" => "送信に失敗しました: {0}",
                "browse" => "参照",
                "clear" => "クリア",
                "loadingViiper" => "VIIPER を読み込み中...",
                "loadViiper" => "VIIPER を読み込む",
                "onboardingTitle" => "初回セットアップ",
                "onboardingSubtitle" => "言語を選択し、環境を確認して、コントローラーをスキャンします。",
                "environment" => "環境チェック",
                "finish" => "完了",
                "connect" => "接続",
                "disconnect" => "切断",
                "connecting" => "接続中...",
                "disconnecting" => "切断中...",
                "preloading" => "事前読み込み中",
                "open" => "パネルを開く",
                "exit" => "終了",
                "statusIdle" => "待機中",
                "statusScanning" => "スキャン中",
                "statusLoadingViiper" => "VIIPER 読み込み中",
                "statusConnectingBle" => "Bluetooth 接続中",
                "statusConnected" => "接続済み",
                "statusNeedsSetup" => "設定が必要",
                "statusConnectFailed" => "接続失敗",
                "statusScanFailed" => "スキャン失敗",
                "statusDisconnected" => "切断済み",
                "statusBleDisconnected" => "Bluetooth が切断されました",
                "trayHint" => "ウィンドウを閉じたときの動作は設定に従います。",
                "trayHintEnabled" => "ウィンドウを閉じてもトレイに常駐します。トレイメニューから接続、切断、終了できます。",
                "trayHintDisabled" => "ウィンドウを閉じるとコントローラーを切断してアプリを終了します。",
                "notRequested" => "未要求",
                "notLoaded" => "未読み込み",
                "envOk" => "検出済み",
                "envMissing" => "未検出",
                "waiting" => "待機中",
                "noneValue" => "なし",
                "connectedValue" => "接続済み",
                "disconnectedValue" => "切断済み",
                "framesFormat" => "{0} 解析済み / {1} 送信済み",
                "rateFormat" => "{0:F1} Hz / 平均 {1:F1} Hz",
                "latencyFormat" => "直近 {0:F2} ms / 平均 {1:F2} ms / 最大 {2:F2} ms",
                "envStatusFormat" => "Windows: {0}{1}VIIPER: {2}{1}設定ファイル: {3}",
                "checkingUsbip" => "usbip-win2 を確認しています...",
                "usbipReady" => "usbip-win2 はインストール済みです。続行できます。",
                "usbipMissing" => "usbip-win2 が見つかりません。インストールしないと VIIPER が仮想コントローラーを作成できない可能性があります。",
                "scanSuccessFormat" => "{0} ({1}) が見つかりました。",
                "scanNotFound" => "コントローラーが見つかりません。ペアリングモードでランプが点滅していることを確認してください。",
                "scanFailedFormat" => "スキャン失敗: {0}",
                "appTitle" => "Switch 2 Pro",
                "appSubtitle" => "ワイヤレス VIIPER ブリッジ",
                "mainHint" => "ホーム画面には接続操作だけを表示します。状態、設定、ログはサイドメニューにあります。",
                "hint1" => "接続する前に、コントローラーの Bluetooth アドレスをスキャンまたは入力してください。",
                "hint2" => "接続設定で自動切断時間を変更できます。",
                "hint3" => "状態ページでボタン、スティック、モーションデータを確認できます。",
                "hint4" => "問題がある場合はログパネルを確認してください。",
                _ => key,
            };
        }

        return key switch
        {
            "home" => "Home",
            "setup" => "Setup",
            "status" => "Status",
            "performance" => "Performance",
            "rumble" => "Rumble",
            "settings" => "Settings",
            "log" => "Log",
            "bluetooth" => "Bluetooth",
            "controller" => "Controller",
            "bleAddress" => "BLE address",
            "viiper" => "VIIPER",
            "apiAddress" => "API address",
            "viiperExe" => "viiper.exe",
            "inputs" => "Inputs",
            "motion" => "Motion",
            "test" => "Test rumble",
            "scan" => "Scan",
            "calibrateSticks" => "Calibrate sticks",
            "stickCalibrationNotSaved" => "Not calibrated. Default 1.6x stick normalization is used.",
            "stickCalibrationConnectFirst" => "Connect the controller before calibrating sticks.",
            "stickCalibrationCenter" => "Release both sticks. Capturing center...",
            "stickCalibrationMove" => "Move both sticks slowly around their outer edges. Capturing endpoints...",
            "stickCalibrationSaved" => "Stick calibration saved.",
            "stickCalibrationFailed" => "Calibration failed: full endpoints were not captured. Try again and push both sticks to every outer edge.",
            "language" => "Language",
            "frames" => "Frames",
            "buttons" => "Buttons",
            "leftX" => "Left X",
            "leftY" => "Left Y",
            "rightX" => "Right X",
            "rightY" => "Right Y",
            "target" => "Target",
            "bleLink" => "BLE Link",
            "bleRate" => "BLE Rate",
            "submitRate" => "Submit Rate",
            "latency" => "Est. Latency",
            "bleInterval" => "BLE Interval",
            "inputAge" => "Input Age",
            "backlog" => "Backlog/Drop",
            "writes" => "Writes",
            "errors" => "Errors",
            "envDesc" => "Ensure usbip-win2 is installed.",
            "scanTitle" => "Ready to scan",
            "scanDesc" => "Hold the sync button until LEDs flash, then click scan.",
            "stepLanguage" => "Language",
            "stepEnvironment" => "Environment",
            "stepScan" => "Scan controller",
            "stepSettings" => "Startup settings",
            "onboardingStepFormat" => "Step {0} of 4: {1}",
            "startupSettings" => "Startup settings",
            "downloadUsbip" => "Download usbip-win2",
            "back" => "Back",
            "next" => "Next",
            "closeToTray" => "Close to system tray",
            "startWithWindows" => "Start with Windows",
            "startToTray" => "Start hidden in tray",
            "preloadViiper" => "Preload VIIPER",
            "autoDisconnect" => "Auto disconnect (idle minutes, 0=off)",
            "configFile" => "Config file",
            "openFolder" => "Open folder",
            "feedbackTitle" => "Feedback",
            "feedbackDescription" => "Send a problem report or suggestion, with optional diagnostic logs.",
            "feedbackButton" => "Send feedback",
            "feedbackDialogTitle" => "Feedback",
            "feedbackMessage" => "Feedback",
            "feedbackPlaceholder" => "Describe what you did, what happened, and what you expected.",
            "feedbackIncludeDiagnostics" => "Include diagnostic logs (settings are never included)",
            "feedbackPrivacyHint" => "Diagnostic logs may contain Bluetooth device identifiers and local paths. Clear this option to send only basic system information.",
            "feedbackSend" => "Send",
            "feedbackCancel" => "Cancel",
            "feedbackClose" => "Close",
            "feedbackSending" => "Sending. The mail server may take a little while to respond...",
            "feedbackSuccessFormat" => "Feedback sent. Request ID: {0}",
            "feedbackInvalid" => "Feedback is empty or exceeds the 32 KiB limit.",
            "feedbackBadRequest" => "The feedback or diagnostic attachment format was rejected.",
            "feedbackTooLarge" => "The feedback or diagnostic attachment exceeds the size limit.",
            "feedbackRateLimited" => "Too many submissions. Please try again later.",
            "feedbackUnavailable" => "The feedback service is temporarily unavailable. Please try again later.",
            "feedbackFailedFormat" => "Could not send feedback: {0}",
            "browse" => "Browse",
            "clear" => "Clear",
            "loadingViiper" => "Loading VIIPER...",
            "loadViiper" => "Load VIIPER",
            "onboardingTitle" => "First run setup",
            "onboardingSubtitle" => "Choose language, check the environment, and scan for the controller.",
            "environment" => "Environment",
            "finish" => "Finish",
            "connect" => "Connect",
            "disconnect" => "Disconnect",
            "connecting" => "Connecting...",
            "disconnecting" => "Disconnecting...",
            "preloading" => "Preloading",
            "open" => "Open",
            "exit" => "Exit",
            "statusIdle" => "Idle",
            "statusScanning" => "Scanning",
            "statusLoadingViiper" => "Loading VIIPER",
            "statusConnectingBle" => "Connecting BLE",
            "statusConnected" => "Connected",
            "statusNeedsSetup" => "Needs setup",
            "statusConnectFailed" => "Connect failed",
            "statusScanFailed" => "Scan failed",
            "statusDisconnected" => "Disconnected",
            "statusBleDisconnected" => "BLE disconnected",
            "trayHint" => "Closing the window follows the tray behavior in Settings.",
            "trayHintEnabled" => "Closing the window keeps the app in the tray. Use the tray menu to connect, disconnect, or exit.",
            "trayHintDisabled" => "Closing the window disconnects the controller and exits the app.",
            "notRequested" => "Not requested",
            "notLoaded" => "Not loaded",
            "envOk" => "Found",
            "envMissing" => "Missing",
            "waiting" => "Waiting",
            "noneValue" => "None",
            "connectedValue" => "Connected",
            "disconnectedValue" => "Disconnected",
            "framesFormat" => "{0} parsed / {1} submitted",
            "rateFormat" => "{0:F1} Hz / avg {1:F1} Hz",
            "latencyFormat" => "last {0:F2} ms / avg {1:F2} ms / max {2:F2} ms",
            "envStatusFormat" => "Windows: {0}{1}VIIPER: {2}{1}Config: {3}",
            "checkingUsbip" => "Checking for usbip-win2...",
            "usbipReady" => "usbip-win2 is installed and ready.",
            "usbipMissing" => "usbip-win2 was not found. Install it before continuing or VIIPER may not be able to create the virtual controller.",
            "scanSuccessFormat" => "Found {0} ({1}).",
            "scanNotFound" => "No controllers found. Make sure the controller is in pairing mode with LEDs flashing.",
            "scanFailedFormat" => "Scan failed: {0}",
            "appTitle" => "Switch 2 Pro",
            "appSubtitle" => "Wireless VIIPER bridge",
            "mainHint" => "The home screen keeps connection controls simple. Status, settings, and logs are in the side menu.",
            "hint1" => "Scan or enter the controller BLE address before connecting.",
            "hint2" => "Adjust auto disconnect in settings.",
            "hint3" => "Use the status page to inspect buttons, sticks, and motion data.",
            "hint4" => "See log panel if issues occur.",
            _ => key,
        };
    }

    private void UpdateEnvironmentStatus()
    {
        var viiperPath = string.IsNullOrWhiteSpace(ViiperExeBox.Text)
            ? Path.Combine(AppContext.BaseDirectory, "viiper.exe")
            : ViiperExeBox.Text.Trim();
        var viiperOk = File.Exists(viiperPath);
        var os = Environment.OSVersion.VersionString;
        EnvironmentStatusText.Text =
            string.Format(T("envStatusFormat"), os, Environment.NewLine, viiperOk ? T("envOk") : T("envMissing"), AppSettings.SettingsPath);
    }

    private bool IsBleConnected => _ble is not null;

    private async Task ToggleConnectionAsync()
    {
        if (IsBleConnected)
        {
            await DisconnectAsync().ConfigureAwait(true);
        }
        else if (_viiper is null)
        {
            await LoadViiperAsync().ConfigureAwait(true);
        }
        else
        {
            await ConnectBleAsync().ConfigureAwait(true);
        }
    }

    private void UpdateConnectionUi(bool busy = false)
    {
        if (ConnectButton is null)
        {
            return;
        }

        if (TrayHintText != null)
        {
            TrayHintText.Text = _settings.MinimizeToTray ? T("trayHintEnabled") : T("trayHintDisabled");
        }

        ConnectButton.IsEnabled = !busy;
        if (busy)
        {
            ConnectButtonText.Text = _viiper is null ? T("loadingViiper") : (IsBleConnected ? T("disconnecting") : T("connecting"));
        }
        else
        {
            ConnectButtonText.Text = _viiper is null ? T("loadViiper") : (IsBleConnected ? T("disconnect") : T("connect"));
        }
        ConnectGlyph.Glyph = IsBleConnected ? "\uE711" : "\uE768";
        TestRumbleButton.IsEnabled = IsBleConnected;
        UpdateTrayMenu();
    }

    private void UpdateTrayMenu()
    {
        _trayConnectText = _viiper is null ? T("loadViiper") : T(IsBleConnected ? "disconnect" : "connect");
        _trayExitText = T("exit");
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        await ScanToComboAsync(CandidatesBox, ScanSecondsBox, AddressBox).ConfigureAwait(true);
    }

    private async void StickCalibrate_Click(object sender, RoutedEventArgs e)
    {
        if (_stickCalibrationRunning)
        {
            return;
        }

        if (!IsBleConnected)
        {
            StickCalibrationText.Text = T("stickCalibrationConnectFirst");
            Log("Stick calibration skipped: controller is not connected.");
            return;
        }

        var previousCalibration = _settings.StickCalibration;
        _stickCalibrationRunning = true;
        StickCalibrateButton.IsEnabled = false;

        try
        {
            _settings.StickCalibration = null;
            _parser.ResetStickCalibration();
            StickCalibrationText.Text = T("stickCalibrationCenter");
            Log("Stick calibration: capturing center.");

            var centerStart = Stopwatch.GetTimestamp();
            while (!_parser.HasStickCenterCalibration &&
                   Stopwatch.GetTimestamp() - centerStart < Stopwatch.Frequency * 3)
            {
                await Task.Delay(100).ConfigureAwait(true);
            }

            if (!_parser.HasStickCenterCalibration)
            {
                throw new InvalidOperationException(T("stickCalibrationFailed"));
            }

            StickCalibrationText.Text = T("stickCalibrationMove");
            Log("Stick calibration: capturing endpoints.");
            _parser.BeginStickRangeCalibration();
            await Task.Delay(8000).ConfigureAwait(true);

            var profile = _parser.EndStickRangeCalibration();
            if (profile is null)
            {
                throw new InvalidOperationException(T("stickCalibrationFailed"));
            }

            _settings.StickCalibration = profile;
            _settings.Save();
            StickCalibrationText.Text = T("stickCalibrationSaved");
            Log("Stick calibration saved.");
        }
        catch (Exception ex)
        {
            if (previousCalibration?.IsUsable == true)
            {
                _settings.StickCalibration = previousCalibration;
                _parser.ApplyStickCalibration(previousCalibration);
            }

            StickCalibrationText.Text = T("stickCalibrationFailed");
            Log("Stick calibration failed: " + ex.Message);
        }
        finally
        {
            _stickCalibrationRunning = false;
            StickCalibrateButton.IsEnabled = true;
        }
    }

    private async Task ScanToComboAsync(ComboBox candidatesBox, TextBox secondsBox, TextBox addressBox)
    {
        SetStatus(T("statusScanning"), StatusBrush("info"));
        ScanButton.IsEnabled = false;
        candidatesBox.ItemsSource = null;
        try
        {
            await Task.Yield();
            var seconds = int.TryParse(secondsBox.Text, out var parsed) ? Math.Clamp(parsed, 1, 60) : 12;
            Log($"Scanning for Nintendo BLE candidates for {seconds}s...");
            var candidates = await _scanner.ScanAsync(TimeSpan.FromSeconds(seconds), CancellationToken.None);
            var items = candidates.Select(candidate => new CandidateItem(candidate)).ToArray();
            candidatesBox.ItemsSource = items;
            candidatesBox.SelectedItem = items.FirstOrDefault();
            if (items.FirstOrDefault() is { } item)
            {
                addressBox.Text = item.BluetoothAddress.ToString("X12");
            }

            Log($"Scan complete: {items.Length} candidate(s).");
            SetStatus(T("statusIdle"), StatusBrush("neutral"));
            SaveSettingsFromUi();
        }
        catch (Exception ex)
        {
            Log("Scan failed: " + ex.Message);
            SetStatus(T("statusScanFailed"), StatusBrush("error"));
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ToggleConnectionAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log("Connection action failed: " + ex.Message);
            SetStatus(T("statusConnectFailed"), StatusBrush("error"));
            UpdateConnectionUi();
        }
    }

        private async Task LoadViiperAsync()
    {
        SaveSettingsFromUi();
        StartViiperWarmup();
        UpdateConnectionUi(busy: true);
        try
        {
            SetStatus(T("statusLoadingViiper"), StatusBrush("warning"));
            Log("Loading VIIPER output...");
            await EnsureViiperWarmupAsync(CancellationToken.None).ConfigureAwait(true);
            _viiper = await ViiperBridge.ConnectAsync(
                ViiperAddressBox.Text.Trim(),
                string.IsNullOrWhiteSpace(ViiperExeBox.Text) ? null : ViiperExeBox.Text.Trim(),
                CancellationToken.None,
                message => RunOnUi(() => Log(message)));
            if (!_viiper.VirtualDeviceReady)
            {
                throw new InvalidOperationException("VIIPER did not confirm the virtual USB controller.");
            }
            _viiper.HidOutputReceived += OnViiperHidOutput;
            ViiperStatusText.Text = _viiper.Description;
            Log("VIIPER ready: " + _viiper.Description);
            SetStatus(T("statusIdle"), StatusBrush("neutral"));
        }
        catch (Exception ex)
        {
            ViiperStatusText.Text = ex.Message;
            Log("VIIPER load failed: " + ex.Message);
            SetStatus(T("statusConnectFailed"), StatusBrush("error"));
        }
        finally
        {
            UpdateConnectionUi();
        }
    }

    private async Task ConnectBleAsync()
    {
        if (_viiper?.VirtualDeviceReady != true)
        {
            ViiperStatusText.Text = "Virtual USB controller is not ready.";
            Log("BLE connection blocked: virtual USB controller is not ready.");
            SetStatus(T("statusConnectFailed"), StatusBrush("error"));
            return;
        }

        if (!TryParseAddress(out var address))
        {
            Log("Enter or scan a 12-digit BLE address first.");
            SetStatus(T("statusNeedsSetup"), StatusBrush("warning"));
            ShowSecondaryMenu();
            return;
        }

        SaveSettingsFromUi();
        UpdateConnectionUi(busy: true);
        _sessionCts = new CancellationTokenSource();
        ResetMetrics();
        ResetActivityBaseline();

        try
        {
            _submitTask = Task.Factory
                .StartNew(
                    () => SubmitLoop(_sessionCts.Token),
                    _sessionCts.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

            SetStatus(T("statusConnectingBle"), StatusBrush("warning"));
            await _scanner.LogEnvironmentAsync(_sessionCts.Token);
            _ble = new BleClient();
            _ble.Trace += (_, message) => RunOnUi(() => Log(message));
            _ble.LinkDiagnosticsChanged += OnBleLinkDiagnosticsChanged;
            _ble.NotificationReceived += OnBleNotification;
            _ble.ConnectionStatusChanged += OnBleStatusChanged;
            await _ble.ConnectAsync(address, _sessionCts.Token);
            ResetActivityBaseline();
            BleStatusText.Text = T("connectedValue");
            Log($"BLE connected: {address:X12}");
            SetStatus(T("statusConnected"), StatusBrush("success"));

            _lowLatencyRefreshTask = Task.Run(() => LowLatencyRefreshLoopAsync(_sessionCts.Token), _sessionCts.Token);
            _rumbleTask = Task.Run(() => RumbleLoopAsync(_sessionCts.Token), _sessionCts.Token);
            UpdateConnectionUi();
        }
        catch (Exception ex)
        {
            Log(
                $"Connect failed: {ex.GetType().FullName}: {ex.Message} " +
                $"(HRESULT 0x{ex.HResult:X8})" +
                (ex.InnerException is null
                    ? string.Empty
                    : $"; inner={ex.InnerException.GetType().FullName}: {ex.InnerException.Message}"));
            await DisconnectAsync().ConfigureAwait(true);
            SetStatus(T("statusConnectFailed"), StatusBrush("error"));
            UpdateConnectionUi();
        }
    }

    private async Task DisconnectAsync()
    {
        var hadSession = IsBleConnected;
        if (hadSession)
        {
            UpdateConnectionUi(busy: true);
        }

        try
        {
            _sessionCts?.Cancel();

            await AwaitAndClearTaskAsync(_lowLatencyRefreshTask, "low latency refresh").ConfigureAwait(true);
            _lowLatencyRefreshTask = null;
            await AwaitAndClearTaskAsync(_submitTask, "submit loop").ConfigureAwait(true);
            _submitTask = null;
            await AwaitAndClearTaskAsync(_rumbleTask, "rumble loop").ConfigureAwait(true);
            _rumbleTask = null;

            if (_ble is not null)
            {
                _ble.NotificationReceived -= OnBleNotification;
                _ble.ConnectionStatusChanged -= OnBleStatusChanged;
                _ble.LinkDiagnosticsChanged -= OnBleLinkDiagnosticsChanged;
                try
                {
                    await _ble.DisconnectControllerAsync(CancellationToken.None).ConfigureAwait(true);
                    await _ble.DisposeAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Log("BLE cleanup failed: " + ex.Message);
                }

                _ble = null;
            }

            
        }
        finally
        {
            _sessionCts?.Dispose();
            _sessionCts = null;
        }

        ResetRumble();

        if (!_settings.PreloadViiper && _viiper is not null)
        {
            _viiper.HidOutputReceived -= OnViiperHidOutput;
            try { await _viiper.DisposeAsync().ConfigureAwait(true); } catch { }
            _viiper = null;
        }

        BleStatusText.Text = T("statusIdle");
        BleLinkText.Text = T("notRequested");
        ViiperStatusText.Text = _viiperWarmup?.Description ?? T("notLoaded");
        SetStatus(T("statusDisconnected"), StatusBrush("neutral"));
        UpdateConnectionUi();
        ScheduleIdleMemoryTrim();
    }

    private async Task AwaitAndClearTaskAsync(Task? task, string label)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        catch (Exception ex)
        {
            Log($"{label} cleanup failed: {ex.Message}");
        }
    }

    private void OnBleNotification(object? sender, BleNotificationEventArgs args)
    {
        var notificationTicks = Stopwatch.GetTimestamp();
        var isInput = args.CharacteristicUuid.Equals(BleClient.InputReportUuid, StringComparison.OrdinalIgnoreCase) ||
                      args.CharacteristicUuid.Equals(Fd2ReportParser.LegacyNotifyUuid, StringComparison.OrdinalIgnoreCase);
        if (isInput)
        {
            Interlocked.Increment(ref _rawInputNotifications);
        }

        try
        {
            var parsed = false;
            lock (_stateLock)
            {
                if (_parser.TryParse(args.CharacteristicUuid, args.Data, _state))
                {
                    _stateVersion++;
                    _lastStateNotificationTicks = notificationTicks;
                    parsed = true;

                    if (_state.Buttons != _lastActiveButtons ||
                        Math.Abs(_state.LeftX - _lastActiveLeftX) > 50 ||
                        Math.Abs(_state.LeftY - _lastActiveLeftY) > 50 ||
                        Math.Abs(_state.RightX - _lastActiveRightX) > 50 ||
                        Math.Abs(_state.RightY - _lastActiveRightY) > 50)
                    {
                        _lastActiveTicks = notificationTicks;
                        _lastActiveButtons = _state.Buttons;
                        _lastActiveLeftX = _state.LeftX;
                        _lastActiveLeftY = _state.LeftY;
                        _lastActiveRightX = _state.RightX;
                        _lastActiveRightY = _state.RightY;
                    }
                }
            }

            if (!parsed)
            {
                if (isInput)
                {
                    Interlocked.Increment(ref _rejectedInputNotifications);
                }
                return;
            }

            RecordInputFrame(Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _parsedFrames);
            SignalSubmitLoop();
        }
        catch (Exception ex)
        {
            RunOnUi(() => Log("Input parse failed: " + ex.Message));
        }
    }

    private void SubmitLoop(CancellationToken cancellationToken)
    {
        try
        {
            Thread.CurrentThread.Name ??= "Switch2Pro VIIPER submit";
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        }
        catch
        {
        }

        var lastSubmittedVersion = 0L;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _submitSignal.Wait(cancellationToken);
                Interlocked.Exchange(ref _submitSignalPending, 0);

                long version;
                long sourceTicks;
                lock (_stateLock)
                {
                    version = _stateVersion;
                    if (version == 0)
                    {
                        continue;
                    }

                    sourceTicks = _lastStateNotificationTicks;
                    _submitState.CopyFrom(_state);
                }

                var viiper = _viiper;
                if (viiper is null)
                {
                    continue;
                }

                viiper.Submit(_submitState, cancellationToken);
                Interlocked.Increment(ref _submittedFrames);

                var submittedTicks = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref _lastViiperSubmitTicks, submittedTicks);
                if (version != lastSubmittedVersion && sourceTicks > 0)
                {
                    RecordBridgeLatency(submittedTicks - sourceTicks);
                }

                lastSubmittedVersion = version;
                if (Volatile.Read(ref _stateVersion) != lastSubmittedVersion)
                {
                    SignalSubmitLoop();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _submitErrors);
                RunOnUi(() => Log("Input submit failed: " + ex.Message));
            }
        }
    }

    private void SignalSubmitLoop()
    {
        if (_viiper is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _submitSignalPending, 1) == 0)
        {
            _submitSignal.Release();
        }
    }

    private async Task LowLatencyRefreshLoopAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(i == 0 ? 2000 : 3000, cancellationToken).ConfigureAwait(false);
            _ble?.ReassertLowLatencyConnectionParameters($"periodic-{i + 1}");
        }
    }

    private void OnViiperHidOutput(object? sender, byte[] report)
    {
        var frame = _rumble.TryBuildFromHidOutput(report);
        if (frame is null)
        {
            return;
        }

        lock (_rumbleLock)
        {
            _currentRumblePacket = frame.Packet;
            if (frame.Active)
            {
                _rumbleUntil = DateTimeOffset.UtcNow.AddMilliseconds(180);
                _stopPacketsPending = 3;
            }
            else
            {
                _rumbleUntil = DateTimeOffset.MinValue;
                _stopPacketsPending = 3;
            }
        }
    }

    private async Task RumbleLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? packet = null;
            lock (_rumbleLock)
            {
                var now = DateTimeOffset.UtcNow;
                if (_currentRumblePacket is not null && now <= _rumbleUntil)
                {
                    packet = _currentRumblePacket;
                }
                else if (_stopPacketsPending > 0)
                {
                    packet = _rumble.BuildStopPacket();
                    _stopPacketsPending--;
                }
            }

            if (packet is not null && _ble is not null)
            {
                try
                {
                    await _ble.WriteRumbleAsync(packet, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _rumbleWrites);
                }
                catch
                {
                    Interlocked.Increment(ref _rumbleErrors);
                }
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    private void TestRumble_Click(object sender, RoutedEventArgs e)
    {
        lock (_rumbleLock)
        {
            _currentRumblePacket = _rumble.BuildSelfTestPacket();
            _rumbleUntil = DateTimeOffset.UtcNow.AddMilliseconds(300);
            _stopPacketsPending = 3;
        }

        Log("Queued physical controller rumble test.");
    }

    private async void BrowseViiper_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop,
            };
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, _hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            ViiperExeBox.Text = file.Path;
            UpdateEnvironmentStatus();
            SaveSettingsFromUi();
            if (_settings.PreloadViiper)
            {
                StartViiperWarmup(force: true);
            }
        }
        catch (Exception ex)
        {
            Log("Browse viiper.exe failed: " + ex.Message);
        }
    }

    private void CandidatesBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CandidatesBox.SelectedItem is CandidateItem item)
        {
            AddressBox.Text = item.BluetoothAddress.ToString("X12");
            SaveSettingsFromUi();
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logBuffer.Clear();
        LogBox.Text = string.Empty;
    }

    private void OpenConfigFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettingsFromUi();
            var directory = Path.GetDirectoryName(AppSettings.SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{directory}\"",
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            Log("Open config folder failed: " + ex.Message);
        }
    }

    private async void FeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        if (RootGrid.XamlRoot is null)
        {
            return;
        }

        var feedbackBox = new TextBox
        {
            Header = T("feedbackMessage"),
            PlaceholderText = T("feedbackPlaceholder"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 150,
            MaxHeight = 280,
            MaxLength = 32768,
        };
        var includeDiagnostics = new CheckBox
        {
            Content = T("feedbackIncludeDiagnostics"),
            IsChecked = true,
        };
        var privacyHint = Text(
            T("feedbackPrivacyHint"),
            12,
            Weight(400),
            UiBrush(Color(0x60, 0x60, 0x60)));
        privacyHint.TextWrapping = TextWrapping.Wrap;
        var status = new InfoBar
        {
            IsOpen = false,
            IsClosable = false,
        };
        var content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 360,
            MaxWidth = 520,
        };
        content.Children.Add(feedbackBox);
        content.Children.Add(includeDiagnostics);
        content.Children.Add(privacyHint);
        content.Children.Add(status);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = T("feedbackDialogTitle"),
            Content = content,
            PrimaryButtonText = T("feedbackSend"),
            CloseButtonText = T("feedbackCancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        var sent = false;
        var sending = false;
        feedbackBox.TextChanged += (_, _) =>
        {
            if (!sent && !sending)
            {
                dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(feedbackBox.Text);
            }
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (sent)
            {
                return;
            }

            args.Cancel = true;
            if (sending)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(feedbackBox.Text))
            {
                status.Severity = InfoBarSeverity.Warning;
                status.Message = T("feedbackInvalid");
                status.IsOpen = true;
                return;
            }

            var deferral = args.GetDeferral();
            sending = true;
            dialog.IsPrimaryButtonEnabled = false;
            feedbackBox.IsEnabled = false;
            includeDiagnostics.IsEnabled = false;
            status.Severity = InfoBarSeverity.Informational;
            status.Message = T("feedbackSending");
            status.IsOpen = true;
            try
            {
                var result = await FeedbackClient.SubmitAsync(
                    feedbackBox.Text,
                    includeDiagnostics.IsChecked == true,
                    SelectedLanguage(LanguageCombo),
                    message => RunOnUi(() => Log(message)),
                    CancellationToken.None);
                sent = true;
                status.Severity = InfoBarSeverity.Success;
                status.Message = string.Format(T("feedbackSuccessFormat"), result.RequestId);
                dialog.PrimaryButtonText = T("feedbackClose");
                dialog.CloseButtonText = string.Empty;
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (Exception ex)
            {
                Log(
                    $"Feedback submission failed: {ex.GetType().FullName}: {ex.Message} " +
                    $"(HRESULT 0x{ex.HResult:X8}).");
                status.Severity = InfoBarSeverity.Error;
                status.Message = FormatFeedbackError(ex);
                feedbackBox.IsEnabled = true;
                includeDiagnostics.IsEnabled = true;
                dialog.IsPrimaryButtonEnabled = true;
            }
            finally
            {
                sending = false;
                deferral.Complete();
            }
        };

        FeedbackButton.IsEnabled = false;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            FeedbackButton.IsEnabled = true;
        }
    }

    private string FormatFeedbackError(Exception exception)
    {
        if (exception is ArgumentException or InvalidOperationException)
        {
            return T("feedbackInvalid");
        }

        if (exception is FeedbackSubmissionException feedbackException)
        {
            return feedbackException.StatusCode switch
            {
                HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType => T("feedbackBadRequest"),
                HttpStatusCode.RequestEntityTooLarge => T("feedbackTooLarge"),
                HttpStatusCode.TooManyRequests => T("feedbackRateLimited"),
                HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => T("feedbackUnavailable"),
                _ => string.Format(T("feedbackFailedFormat"), $"HTTP {(int)feedbackException.StatusCode}"),
            };
        }

        if (exception is HttpRequestException or TaskCanceledException)
        {
            return T("feedbackUnavailable");
        }

        return string.Format(T("feedbackFailedFormat"), exception.Message);
    }

    private void OnBleStatusChanged(object? sender, BluetoothConnectionStatus status)
    {
        RunOnUi(() =>
        {
            BleStatusText.Text = FormatBleStatus(status);
            Log("BLE status: " + status);
            if (status == BluetoothConnectionStatus.Disconnected)
            {
                SetStatus(T("statusBleDisconnected"), StatusBrush("neutral"));
                UpdateConnectionUi();
            }
        });
    }

    private void OnBleLinkDiagnosticsChanged(object? sender, string message)
    {
        RunOnUi(() => BleLinkText.Text = LocalizeBleLinkDiagnostics(message));
    }

    private string FormatBleStatus(BluetoothConnectionStatus status) => status switch
    {
        BluetoothConnectionStatus.Connected => T("connectedValue"),
        BluetoothConnectionStatus.Disconnected => T("disconnectedValue"),
        _ => status.ToString(),
    };

    private bool IsChineseLanguage()
    {
        var lang = SelectedLanguage(LanguageCombo);
        return lang.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ||
               lang.Equals("zh", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsJapaneseLanguage()
    {
        var lang = SelectedLanguage(LanguageCombo);
        return lang.Equals("ja-JP", StringComparison.OrdinalIgnoreCase) ||
               lang.Equals("ja", StringComparison.OrdinalIgnoreCase);
    }

    private string LocalizeBleLinkDiagnostics(string message)
    {
        var isChinese = IsChineseLanguage();
        var isJapanese = IsJapaneseLanguage();
        if (!isChinese && !isJapanese)
        {
            return message;
        }

        if (message.StartsWith("Preferred BLE params requested", StringComparison.Ordinal))
        {
            var detailsStart = message.IndexOf(':');
            var details = detailsStart >= 0 ? message[detailsStart..] : string.Empty;
            return isJapanese
                ? "優先 Bluetooth 接続パラメーターを要求済み" + details
                : "已请求首选蓝牙连接参数" + details;
        }

        return isJapanese
            ? message
                .Replace("GATT session", "GATT セッション", StringComparison.Ordinal)
                .Replace("maintain=true", "接続維持=true", StringComparison.Ordinal)
                .Replace("error=", "エラー=", StringComparison.Ordinal)
            : message
                .Replace("GATT session", "GATT 会话", StringComparison.Ordinal)
                .Replace("maintain=true", "保持连接=true", StringComparison.Ordinal)
                .Replace("error=", "错误=", StringComparison.Ordinal);
    }

    private bool TryParseAddress(out ulong address)
    {
        var text = AddressBox.Text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out address);
    }

    private void UpdateStateView()
    {
        lock (_stateLock)
        {
            _viewState.CopyFrom(_state);
        }

        LeftXBar.Text = _viewState.LeftX.ToString();
        LeftYBar.Text = _viewState.LeftY.ToString();
        RightXBar.Text = _viewState.RightX.ToString();
        RightYBar.Text = _viewState.RightY.ToString();
        ButtonsText.Text = _viewState.Buttons == Switch2Button.None ? T("noneValue") : _viewState.Buttons.ToString();
        MotionText.Text = _viewState.MotionValid ? Convert.ToHexString(_viewState.Motion) : T("noneValue");
        FramesText.Text = string.Format(T("framesFormat"), Interlocked.Read(ref _parsedFrames), Interlocked.Read(ref _submittedFrames));
    }

    private void UpdateLiveView()
    {
        if (_isHiddenToTray)
        {
            CheckAutoDisconnect();
            return;
        }

        UpdateStateView();
        UpdateRumbleView();
        UpdatePerformanceView();
        CheckAutoDisconnect();
    }

    private void CheckAutoDisconnect()
    {
        if (IsBleConnected && _settings.AutoDisconnectMinutes > 0)
        {
            var idleTicks = Stopwatch.GetTimestamp() - _lastActiveTicks;
            var limitTicks = _settings.AutoDisconnectMinutes * 60.0 * Stopwatch.Frequency;
            if (idleTicks > limitTicks)
            {
                Log($"Auto disconnecting after {_settings.AutoDisconnectMinutes:0.###} minutes of inactivity.");
                _lastActiveTicks = Stopwatch.GetTimestamp();
                BeginOnUi(() => _ = DisconnectAsync());
            }
        }
    }

    private void ResetActivityBaseline()
    {
        _lastActiveTicks = Stopwatch.GetTimestamp();
        lock (_stateLock)
        {
            _lastActiveButtons = _state.Buttons;
            _lastActiveLeftX = _state.LeftX;
            _lastActiveLeftY = _state.LeftY;
            _lastActiveRightX = _state.RightX;
            _lastActiveRightY = _state.RightY;
        }
    }

    private void UpdatePerformanceView()
    {
        var nowTicks = Stopwatch.GetTimestamp();
        var parsed = Interlocked.Read(ref _parsedFrames);
        var submitted = Interlocked.Read(ref _submittedFrames);
        var elapsedTicks = nowTicks - _lastRateSampleTicks;

        if (elapsedTicks > 0)
        {
            _bleRateHz = (parsed - _lastParsedRateSample) * Stopwatch.Frequency / (double)elapsedTicks;
            _submitRateHz = (submitted - _lastSubmittedRateSample) * Stopwatch.Frequency / (double)elapsedTicks;
            _lastParsedRateSample = parsed;
            _lastSubmittedRateSample = submitted;
            _lastRateSampleTicks = nowTicks;
        }

        var firstTicks = Volatile.Read(ref _firstFrameTicks);
        var averageBleRate = parsed > 1 && firstTicks > 0
            ? parsed * Stopwatch.Frequency / (double)(nowTicks - firstTicks)
            : 0.0;
        var averageSubmitRate = submitted > 1 && firstTicks > 0
            ? submitted * Stopwatch.Frequency / (double)(nowTicks - firstTicks)
            : 0.0;

        BleRateText.Text = string.Format(T("rateFormat"), _bleRateHz, averageBleRate);
        SubmitRateText.Text = string.Format(T("rateFormat"), _submitRateHz, averageSubmitRate);

        var samples = Interlocked.Read(ref _bridgeLatencySamples);
        if (samples > 0)
        {
            var lastMs = TicksToMilliseconds(Volatile.Read(ref _lastBridgeLatencyTicks));
            var avgMs = TicksToMilliseconds(Interlocked.Read(ref _totalBridgeLatencyTicks) / (double)samples);
            var maxMs = TicksToMilliseconds(Volatile.Read(ref _maxBridgeLatencyTicks));
            BridgeLatencyText.Text = string.Format(T("latencyFormat"), lastMs, avgMs, maxMs);
            BridgeLatencyText.Foreground = LatencyBrush(avgMs);
        }
        else
        {
            BridgeLatencyText.Text = T("waiting");
            BridgeLatencyText.Foreground = StatusBrush("neutral");
        }

        var interReportTicks = Volatile.Read(ref _lastInterReportTicks);
        BleIntervalText.Text = interReportTicks > 0
            ? $"{TicksToMilliseconds(interReportTicks):F2} ms"
            : T("waiting");

        var lastFrameTicks = Volatile.Read(ref _lastFrameTicks);
        LastInputAgeText.Text = lastFrameTicks > 0
            ? FormatDuration(TicksToMilliseconds(nowTicks - lastFrameTicks))
            : T("noneValue");

        var errors = Interlocked.Read(ref _submitErrors);
        var backlog = Math.Max(0, parsed - submitted - errors);
        BacklogText.Text = $"{backlog} / {errors}";

        BleRateText.Foreground = RateBrush(_bleRateHz);
        SubmitRateText.Foreground = RateBrush(_submitRateHz);
        BleIntervalText.Foreground = interReportTicks > 0
            ? IntervalBrush(TicksToMilliseconds(interReportTicks))
            : StatusBrush("neutral");
        LastInputAgeText.Foreground = lastFrameTicks > 0 && TicksToMilliseconds(nowTicks - lastFrameTicks) < 250
            ? StatusBrush("success")
            : StatusBrush("warning");
        BacklogText.Foreground = backlog == 0 && errors == 0 ? StatusBrush("success") : StatusBrush("error");

        var lastPerformanceLogTicks = Volatile.Read(ref _lastPerformanceLogTicks);
        if (IsBleConnected && nowTicks - lastPerformanceLogTicks >= Stopwatch.Frequency * 5L &&
            Interlocked.CompareExchange(ref _lastPerformanceLogTicks, nowTicks, lastPerformanceLogTicks) == lastPerformanceLogTicks)
        {
            var rawInput = Interlocked.Read(ref _rawInputNotifications);
            var rejectedInput = Interlocked.Read(ref _rejectedInputNotifications);
            var inputAgeMs = lastFrameTicks > 0 ? TicksToMilliseconds(nowTicks - lastFrameTicks) : -1;
            Log(
                $"Pipeline diagnostics: rawInput={rawInput}, parsed={parsed}, rejected={rejectedInput}, " +
                $"submitted={submitted}, submitErrors={errors}, currentRate={_bleRateHz:F1}/{_submitRateHz:F1} Hz, " +
                $"averageRate={averageBleRate:F1}/{averageSubmitRate:F1} Hz, " +
                $"lastParsedIntervalMs={(interReportTicks > 0 ? TicksToMilliseconds(interReportTicks).ToString("F2", CultureInfo.InvariantCulture) : "n/a")}, " +
                $"inputAgeMs={(inputAgeMs >= 0 ? inputAgeMs.ToString("F1", CultureInfo.InvariantCulture) : "n/a")}.");
        }
    }

    private void UpdateRumbleView()
    {
        RumbleWritesText.Text = Interlocked.Read(ref _rumbleWrites).ToString();
        RumbleErrorsText.Text = Interlocked.Read(ref _rumbleErrors).ToString();
    }

    private void ResetMetrics()
    {
        Interlocked.Exchange(ref _parsedFrames, 0);
        Interlocked.Exchange(ref _submittedFrames, 0);
        Interlocked.Exchange(ref _rumbleWrites, 0);
        Interlocked.Exchange(ref _rumbleErrors, 0);
        Interlocked.Exchange(ref _submitErrors, 0);
        Interlocked.Exchange(ref _firstFrameTicks, 0);
        Interlocked.Exchange(ref _lastFrameTicks, 0);
        Interlocked.Exchange(ref _lastInterReportTicks, 0);
        Interlocked.Exchange(ref _lastBridgeLatencyTicks, 0);
        Interlocked.Exchange(ref _lastViiperSubmitTicks, 0);
        Interlocked.Exchange(ref _totalBridgeLatencyTicks, 0);
        Interlocked.Exchange(ref _bridgeLatencySamples, 0);
        Interlocked.Exchange(ref _maxBridgeLatencyTicks, 0);
        Interlocked.Exchange(ref _stateVersion, 0);
        Interlocked.Exchange(ref _lastStateNotificationTicks, 0);
        Interlocked.Exchange(ref _rawInputNotifications, 0);
        Interlocked.Exchange(ref _rejectedInputNotifications, 0);
        Interlocked.Exchange(ref _lastPerformanceLogTicks, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _submitSignalPending, 0);
        while (_submitSignal.Wait(0))
        {
        }

        _lastParsedRateSample = 0;
        _lastSubmittedRateSample = 0;
        _lastRateSampleTicks = Stopwatch.GetTimestamp();
        _bleRateHz = 0;
        _submitRateHz = 0;
        UpdateLiveView();
    }

    private void RecordInputFrame(long parsedTicks)
    {
        Interlocked.CompareExchange(ref _firstFrameTicks, parsedTicks, 0);
        var previousTicks = Interlocked.Exchange(ref _lastFrameTicks, parsedTicks);
        if (previousTicks > 0)
        {
            Interlocked.Exchange(ref _lastInterReportTicks, parsedTicks - previousTicks);
        }
    }

    private void RecordBridgeLatency(long latencyTicks)
    {
        Interlocked.Exchange(ref _lastBridgeLatencyTicks, latencyTicks);
        Interlocked.Add(ref _totalBridgeLatencyTicks, latencyTicks);
        Interlocked.Increment(ref _bridgeLatencySamples);
        UpdateMax(ref _maxBridgeLatencyTicks, latencyTicks);
    }

    private static void UpdateMax(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var original = Interlocked.CompareExchange(ref target, value, current);
            if (original == current)
            {
                return;
            }

            current = original;
        }
    }

    private static double TicksToMilliseconds(double ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static string FormatDuration(double milliseconds) =>
        milliseconds >= 1000 ? $"{milliseconds / 1000:F2} s" : $"{milliseconds:F0} ms";

    private static MediaBrush RateBrush(double hz) =>
        hz <= 0 ? StatusBrush("neutral") :
        hz >= 120 ? StatusBrush("success") :
        hz >= 90 ? StatusBrush("warning") :
        StatusBrush("error");

    private static MediaBrush IntervalBrush(double milliseconds) =>
        milliseconds <= 9 ? StatusBrush("success") :
        milliseconds <= 16 ? StatusBrush("warning") :
        StatusBrush("error");

    private static MediaBrush LatencyBrush(double milliseconds) =>
        milliseconds < 2 ? StatusBrush("success") :
        milliseconds < 8 ? StatusBrush("warning") :
        StatusBrush("error");

    private void ResetRumble()
    {
        lock (_rumbleLock)
        {
            _currentRumblePacket = null;
            _rumbleUntil = DateTimeOffset.MinValue;
            _stopPacketsPending = 0;
        }
    }

    private void ScheduleIdleMemoryTrim()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500).ConfigureAwait(false);
            if (IsBleConnected || _viiper is not null || _viiperWarmupTask is { IsCompleted: false })
            {
                return;
            }

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
            TrimProcessWorkingSet();
        });
    }

    private void ScheduleBackgroundMemoryTrim()
    {
        _backgroundTrimCts?.Cancel();
        _backgroundTrimCts?.Dispose();
        _backgroundTrimCts = new CancellationTokenSource();
        var token = _backgroundTrimCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000, token).ConfigureAwait(false);
                if (token.IsCancellationRequested || !_isHiddenToTray)
                {
                    return;
                }

                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: true, compacting: true);
                TrimProcessWorkingSet();
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private static void TrimProcessWorkingSet()
    {
        try
        {
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
        }
    }

    private void SyncLogBox()
    {
        LogBox.Text = _logBuffer.ToString();
        LogBox.SelectionStart = LogBox.Text.Length;
    }

    private void SetStatus(string text, MediaBrush brush)
    {
        StatusText.Text = text;
        StatusDot.Fill = brush;
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        _logBuffer.Append(line);
        if (_logBuffer.Length > MaxLogCharacters)
        {
            var removeCount = _logBuffer.Length - MaxLogCharacters;
            var nextLine = _logBuffer.ToString().IndexOf('\n', removeCount);
            _logBuffer.Remove(0, nextLine >= 0 ? nextLine + 1 : removeCount);
        }

        if (!_isHiddenToTray)
        {
            SyncLogBox();
        }

        try
        {
            File.AppendAllText(DiagnosticLogPath, line, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static string DiagnosticLogPath =>
        Path.Combine(
            Path.GetDirectoryName(AppSettings.SettingsPath) ?? AppContext.BaseDirectory,
            "diagnostics.log");

    private static void InitializeDiagnosticLog()
    {
        try
        {
            var directory = Path.GetDirectoryName(DiagnosticLogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(DiagnosticLogPath) && new FileInfo(DiagnosticLogPath).Length >= MaxDiagnosticLogBytes)
            {
                var previousPath = Path.Combine(directory ?? AppContext.BaseDirectory, "diagnostics.previous.log");
                File.Move(DiagnosticLogPath, previousPath, overwrite: true);
            }

            File.AppendAllText(
                DiagnosticLogPath,
                $"{Environment.NewLine}===== session {DateTimeOffset.Now:O} ====={Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
        }
    }

    private void LogStartupDiagnostics()
    {
        var assemblyVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown";
        Log(
            $"Runtime environment: appVersion={assemblyVersion}, OS='{RuntimeInformation.OSDescription}', " +
            $"framework='{RuntimeInformation.FrameworkDescription}', processArch={RuntimeInformation.ProcessArchitecture}, " +
            $"osArch={RuntimeInformation.OSArchitecture}, culture={CultureInfo.CurrentCulture.Name}, " +
            $"uiCulture={CultureInfo.CurrentUICulture.Name}, elevated={IsProcessElevated()}.");
        Log($"Diagnostic log file: '{DiagnosticLogPath}'.");

        object? symbolThemeFont = null;
        var hasSymbolThemeFont = Application.Current?.Resources.TryGetValue("SymbolThemeFontFamily", out symbolThemeFont) == true;
        var navFont = (HomeNavItem.Icon as FontIcon)?.FontFamily?.Source ?? "n/a";
        var windowsFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        Log(
            $"Icon diagnostics: SymbolThemeFontFamily present={hasSymbolThemeFont}, " +
            $"resource='{symbolThemeFont ?? "n/a"}', navFont='{navFont}', " +
            $"SegoeIcons.ttf={File.Exists(Path.Combine(windowsFonts, "SegoeIcons.ttf"))}, " +
            $"segmdl2.ttf={File.Exists(Path.Combine(windowsFonts, "segmdl2.ttf"))}.");
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private void RunOnUi(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => action());
        }
    }

    private void AttachWindowProc()
    {
        if (_hwnd == IntPtr.Zero || _oldWindowProc != IntPtr.Zero)
        {
            return;
        }

        _windowProc = WindowProc;
        _oldWindowProc = SetWindowLongPtr(
            _hwnd,
            WindowLongIndex.WindowProc,
            Marshal.GetFunctionPointerForDelegate(_windowProc));
    }

    private void DetachWindowProc()
    {
        if (_hwnd == IntPtr.Zero || _oldWindowProc == IntPtr.Zero)
        {
            return;
        }

        SetWindowLongPtr(_hwnd, WindowLongIndex.WindowProc, _oldWindowProc);
        _oldWindowProc = IntPtr.Zero;
        _windowProc = null;
    }

    private void RemoveTrayIcon()
    {
        if (!_trayIconAdded)
        {
            return;
        }

        var data = CreateNotifyIconData(0);
        Shell_NotifyIcon(NotifyIconMessage.Delete, ref data);
        _trayIconAdded = false;
    }

    private NotifyIconData CreateNotifyIconData(NotifyIconFlags flags)
    {
        return new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = flags,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
    }

    private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WindowMessage.GETMINMAXINFO)
        {
            var dpi = GetDpiForWindow(hWnd);
            var scale = dpi == 0 ? 1.0 : dpi / 96.0;
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMinTrackSize.X = (int)(480 * scale);
            mmi.ptMinTrackSize.Y = (int)(640 * scale);
            Marshal.StructureToPtr(mmi, lParam, false);
            return IntPtr.Zero;
        }
        if (message == WindowMessage.TrayIcon)
        {
            var trayMessage = unchecked((uint)lParam.ToInt64());
            if (trayMessage is WindowMessage.LeftButtonDoubleClick)
            {
                RunOnUi(() => ShowFromTray(openMenu: false));
                return IntPtr.Zero;
            }

            if (trayMessage is WindowMessage.RightButtonUp or WindowMessage.ContextMenu)
            {
                ShowTrayMenu();
                return IntPtr.Zero;
            }
        }
        else if (message == WindowMessage.Command)
        {
            DispatchTrayCommand((int)(wParam.ToInt64() & 0xffff));
            return IntPtr.Zero;
        }

        return CallWindowProc(_oldWindowProc, hWnd, message, wParam, lParam);
    }

    private void ShowTrayMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MenuFlags.String, TrayCommandConnect, _trayConnectText);
            AppendMenu(menu, MenuFlags.Separator, UIntPtr.Zero, null);
            AppendMenu(menu, MenuFlags.String, TrayCommandExit, _trayExitText);

            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd);
            TrackPopupMenu(menu, TrackPopupMenuFlags.RightButton, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void DispatchTrayCommand(int commandId)
    {
        switch (commandId)
        {
            case TrayCommandConnectValue:
                BeginOnUi(ToggleConnectionAsync);
                break;
            case TrayCommandShowValue:
                RunOnUi(() => ShowFromTray(openMenu: false));
                break;
            case TrayCommandMenuValue:
                RunOnUi(() => ShowFromTray(openMenu: true));
                break;
            case TrayCommandExitValue:
                BeginOnUi(ExitApplicationAsync);
                break;
        }
    }

    private void EnableProcessPerformanceMode()
    {
        try
        {
            ThreadPool.GetMinThreads(out var workerThreads, out var completionThreads);
            ThreadPool.SetMinThreads(Math.Max(workerThreads, Environment.ProcessorCount * 4), completionThreads);
        }
        catch
        {
        }

        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        }
        catch
        {
        }

        try
        {
            _timerResolutionSet = TimeBeginPeriod(1) == 0;
        }
        catch
        {
            _timerResolutionSet = false;
        }
    }

    private void DisableProcessPerformanceMode()
    {
        if (!_timerResolutionSet)
        {
            return;
        }

        try
        {
            TimeEndPeriod(1);
        }
        catch
        {
        }

        _timerResolutionSet = false;
    }


    private static void ApplyStyle(Control control, string key)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var value) && value is Style style)
            {
                control.Style = style;
            }
        }
        catch
        {
        }
    }

        private static MediaBrush ThemeBrush(string key, Windows.UI.Color fallback)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var value) && value is MediaBrush brush)
            {
                return brush;
            }
        }
        catch
        {
        }
        return UiBrush(fallback);
    }

    private static MediaBrush StatusBrush(string semantic) => semantic switch
    {
        "success" => ThemeBrush("SystemFillColorSuccessBrush", Windows.UI.Color.FromArgb(0xFF, 0x10, 0x7C, 0x10)),
        "warning" => ThemeBrush("SystemFillColorCautionBrush", Windows.UI.Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00)),
        "error" => ThemeBrush("SystemFillColorCriticalBrush", Windows.UI.Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)),
        "info" => ThemeBrush("AccentFillColorDefaultBrush", Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4)),
        _ => ThemeBrush("TextFillColorSecondaryBrush", Windows.UI.Color.FromArgb(0xFF, 0x60, 0x60, 0x60)),
    };

    private static SolidColorBrush UiBrush(Windows.UI.Color color) => new(color);
    private const uint TrayIconId = 1;
    private const int TrayCommandConnectValue = 1001;
    private const int TrayCommandShowValue = 1002;
    private const int TrayCommandMenuValue = 1003;
    private const int TrayCommandExitValue = 1004;
    private static readonly IntPtr SystemIconApplication = new(32512);
    private static readonly UIntPtr TrayCommandConnect = new(TrayCommandConnectValue);
    private static readonly UIntPtr TrayCommandShow = new(TrayCommandShowValue);
    private static readonly UIntPtr TrayCommandMenu = new(TrayCommandMenuValue);
    private static readonly UIntPtr TrayCommandExit = new(TrayCommandExitValue);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    [DllImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSize")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowWindow(IntPtr hWnd, ShowWindowCommand command);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, WindowLongIndex nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(
        IntPtr lpPrevWndFunc,
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(NotifyIconMessage dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", EntryPoint = "CreatePopupMenu")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, MenuFlags uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", EntryPoint = "TrackPopupMenu")]
    private static extern bool TrackPopupMenu(
        IntPtr hMenu,
        TrackPopupMenuFlags uFlags,
        int x,
        int y,
        int nReserved,
        IntPtr hWnd,
        IntPtr prcRect);

    [DllImport("user32.dll", EntryPoint = "DestroyMenu")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public NativePoint ptReserved;
        public NativePoint ptMaxSize;
        public NativePoint ptMaxPosition;
        public NativePoint ptMinTrackSize;
        public NativePoint ptMaxTrackSize;
    }

    private static class WindowMessage
    {
        public const uint GETMINMAXINFO = 0x0024;
        public const uint SIZE = 0x0005;
        public const uint Command = 0x0111;
        public const uint ContextMenu = 0x007B;
        public const uint LeftButtonDoubleClick = 0x0203;
        public const uint RightButtonUp = 0x0205;
        public const uint TrayIcon = 0x8000 + 1;
    }

    private enum WindowLongIndex
    {
        WindowProc = -4,
    }

    private enum ShowWindowCommand
    {
        Hide = 0,
        Show = 5,
        Restore = 9,
    }

    [Flags]
    private enum NotifyIconFlags : uint
    {
        Message = 0x00000001,
        Icon = 0x00000002,
        Tip = 0x00000004,
    }

    private enum NotifyIconMessage : uint
    {
        Add = 0x00000000,
        Delete = 0x00000002,
        SetVersion = 0x00000004,
    }

    private static class NotifyIconVersion
    {
        public const uint Version4 = 4;
    }

    [Flags]
    private enum MenuFlags : uint
    {
        String = 0x00000000,
        Separator = 0x00000800,
    }

    [Flags]
    private enum TrackPopupMenuFlags : uint
    {
        RightButton = 0x00000002,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public NotifyIconFlags uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }
}

public sealed class CandidateItem
{
    public CandidateItem(BleDeviceCandidate candidate)
    {
        BluetoothAddress = candidate.BluetoothAddress;
        DisplayText = candidate.ToString();
    }

    public ulong BluetoothAddress { get; }
    public string DisplayText { get; }
}
