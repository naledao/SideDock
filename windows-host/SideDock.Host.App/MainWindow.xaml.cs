using System.Diagnostics;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using XamlRectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using XamlShape = Microsoft.UI.Xaml.Shapes.Shape;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Microsoft.Win32;

namespace SideDock.Host.App;

public sealed partial class MainWindow : Window
{
    private const string HostExe = "SideDock.Host.exe";
    private const string DeviceToolExe = "SideDock.Idd.DeviceTool.exe";
    private const string SideDockDriverInf = "SideDock.Idd.inf";
    private const string SideDockDriverBinary = "SideDock.Idd.dll";
    private const string VirtualCameraToolExe = "SideDock.VirtualCamera.Tool.exe";
    private const string VirtualCameraMediaSourceDll = "SideDock.VirtualCamera.MediaSource.dll";
    private const string VirtualCameraMediaSourceClsid = "{951EE24C-E200-4E62-8035-F76214F695D2}";
    private const string DriverInstallerExe = "SideDock.Driver.Installer.exe";
    private const string VirtualAudioCableSetupX64Exe = "VBCABLE_Setup_x64.exe";
    private const string VirtualAudioCableSetupX86Exe = "VBCABLE_Setup.exe";
    private const string VirtualAudioCablePayloadZip = "VirtualAudioCablePayload.zip";
    private const int DefaultControlPort = 27183;
    private const int DefaultVideoPort = 27184;
    private const int DefaultAudioPort = 27185;
    private const int DefaultCameraPort = 27186;
    private const int DefaultCameraCommandPort = 27187;
    private const int DesiredWindowWidth = 1440;
    private const int DesiredWindowHeight = 980;
    private const string AdbExe = "adb.exe";
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint TrayIconId = 1;
    private const uint WmNull = 0x0000;
    private const uint WmUser = 0x0400;
    private const uint WmApp = 0x8000;
    private const uint WmTrayIcon = WmApp + 1;
    private const uint WmContextMenu = 0x007B;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint NinSelect = WmUser;
    private const uint NinKeySelect = WmUser + 1;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NotifyIconVersion4 = 4;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmNonotify = 0x0080;
    private const uint TpmReturnCmd = 0x0100;
    private const uint ImageIcon = 1;
    private const uint LrShared = 0x00008000;
    private const int IdiApplication = 32512;
    private const int TrayMenuOpen = 1001;
    private const int TrayMenuExit = 1002;
    private const int MaxRecentAudioLogLines = 80;
    private const int MaxRecentCameraLogLines = 80;
    private const int CameraPreviewIntervalMs = 33;
    private const int OverviewPreviewIntervalMs = 33;
    private const int MaxOverviewDiagnosticsSamples = 11;
    private const string AudioPreferencesFileName = "audio-preferences.json";
    private static readonly TimeSpan OverviewPreviewStaleAfter = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan VirtualDisplayStatusCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly string DeviceToolProcessName = Path.GetFileNameWithoutExtension(DeviceToolExe);
    private static readonly UIntPtr WindowSubclassId = new(1);
    private static readonly bool StaticOverviewUi = true;

    private readonly DispatcherTimer _displayStatusTimer = new();
    private readonly DispatcherTimer _cameraPreviewTimer = new();
    private readonly DispatcherTimer _overviewPreviewTimer = new();
    private readonly DispatcherTimer _virtualCameraStatusTimer = new();
    private readonly DispatcherTimer _runtimeDiagnosticsTimer = new();
    private readonly object _audioLogGate = new();
    private readonly object _cameraLogGate = new();
    private readonly Queue<string> _recentAudioLogLines = new();
    private readonly Queue<string> _recentCameraLogLines = new();
    private readonly Queue<double> _overviewCpuSamples = new();
    private readonly Queue<double> _overviewMemorySamples = new();
    private readonly CameraDiagnosticsState _cameraDiagnostics = new();
    private readonly VirtualCameraDiagnosticsState _virtualCameraDiagnostics = new();
    private readonly VideoDiagnosticsState _videoDiagnostics = new();
    private readonly Brush _successBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 18, 132, 86));
    private readonly Brush _dangerBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 43, 28));
    private readonly Brush _warningBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 157, 93, 0));
    private readonly Brush _secondaryBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 96, 96, 96));
    private readonly Brush _overviewPrimaryBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 8, 124, 137));
    private readonly Brush _overviewNeutralBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 107, 114, 128));
    private readonly Brush _overviewMutedBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 163, 170, 178));
    private readonly Brush _overviewNavActiveBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 230, 235, 239));
    private readonly Brush _overviewTransparentBrush = new SolidColorBrush(Colors.Transparent);
    private readonly Brush _overviewReadyBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 240, 250, 236));
    private readonly Brush _overviewReadyBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 98, 179, 96));
    private readonly Brush _overviewNeutralBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 248, 250, 252));
    private readonly Brush _overviewNeutralBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 216, 222, 228));
    private readonly Brush _overviewWarningBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 248, 237));
    private readonly Brush _overviewWarningBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 245, 158, 11));
    private readonly Brush _overviewErrorBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 253, 242, 242));
    private readonly Brush _overviewErrorBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 248, 113, 113));
    private readonly Brush _overviewPreviewNeutralBadgeBrush = new SolidColorBrush(ColorHelper.FromArgb(221, 17, 24, 39));
    private readonly Brush _overviewPreviewReceivingBadgeBrush = new SolidColorBrush(ColorHelper.FromArgb(221, 18, 132, 86));
    private readonly Brush _overviewPreviewPausedBadgeBrush = new SolidColorBrush(ColorHelper.FromArgb(221, 157, 93, 0));
    private readonly Brush _overviewPreviewErrorBadgeBrush = new SolidColorBrush(ColorHelper.FromArgb(221, 196, 43, 28));
    private readonly IntPtr _windowHandle;

    private Process? _hostProcess;
    private Process? _deviceToolProcess;
    private SubclassProc? _subclassProc;
    private IntPtr _trayIconHandle;
    private string? _payloadRoot;
    private string? _hostPath;
    private string? _deviceToolPath;
    private string? _virtualCameraToolPath;
    private string? _virtualCameraMediaSourcePath;
    private string? _driverInstallerPath;
    private string? _virtualAudioCableSetupPath;
    private HostProcessLog? _currentHostLog;
    private bool _hostOwnsVirtualDisplay;
    private int? _hostStopRequestedProcessId;
    private bool _exitRequested;
    private bool _trayIconAdded;
    private bool _windowSubclassed;
    private bool _ownsTrayIconHandle;
    private bool _loadingAudioPreferences;
    private AudioCapabilityStatus? _audioOverrideStatus;
    private AudioCapabilityStatus? _microphoneRuntimeStatus;
    private AudioCapabilityStatus? _speakerRuntimeStatus;
    private string _lastAudioHint = "等待 Android 设备连接。";
    private string? _lastMicrophoneStatusLine;
    private string? _lastSpeakerStatusLine;
    private string? _lastMicrophoneErrorLine;
    private string? _lastSpeakerErrorLine;
    private string? _lastMicrophoneErrorMessage;
    private string? _lastSpeakerErrorMessage;
    private string? _lastMicrophoneSystemEndpointMessage;
    private string? _lastSpeakerSystemEndpointMessage;
    private string? _lastCameraStatusLine;
    private string? _lastCameraErrorLine;
    private string? _lastCameraErrorMessage;
    private string? _lastVideoStatsLine;
    private string? _lastEncoderStatsLine;
    private int? _lastHostCpuSampleProcessId;
    private TimeSpan _lastHostCpuSampleProcessorTime;
    private DateTimeOffset? _lastHostCpuSampleAt;
    private string? _lastNetworkInterfaceId;
    private string _lastNetworkInterfaceName = "";
    private long? _lastNetworkLinkSpeedBps;
    private long _lastNetworkBytesSent;
    private long _lastNetworkBytesReceived;
    private DateTimeOffset? _lastNetworkSampleAt;
    private double? _lastNetworkSendBps;
    private double? _lastNetworkReceiveBps;
    private string _lastOverviewCpuText = "未运行";
    private string _lastOverviewMemoryText = "未运行";
    private string _lastOverviewNetworkText = "暂无数据";
    private string _lastOverviewPacketLossText = "暂无数据";
    private string _lastOverviewLatencyText = "暂无数据";
    private string _lastOverviewLatencyDetailText = "等待视频统计";
    private bool _uiReady;
    private OverviewNavigationItem _overviewNavigationItem = OverviewNavigationItem.Overview;
    private bool _overviewSidebarCollapsed;
    private bool _overviewRefreshInProgress;
    private bool _overviewEnvironmentBannerDismissed;
    private bool _syncingOverviewConnectionControls;
    private bool _syncingAdbDeviceSelection;
    private DateTimeOffset? _lastAdbRefreshCompletedAt;
    private bool? _lastAdbReverseConfigured;
    private string? _lastAdbReverseSerial;
    private string _lastAdbReverseDetail = "启动主机时自动配置 ADB reverse。";
    private bool _restartingForCameraFacing;
    private bool _cameraPreviewEnabled = true;
    private bool _loadingAudioEndpointChoices;
    private string? _boundMicrophoneRenderEndpointId;
    private string? _boundMicrophoneRenderEndpointName;
    private string? _boundSpeakerCaptureEndpointId;
    private string? _boundSpeakerCaptureEndpointName;
    private bool _audioEndpointChoicesReady;
    private AudioEndpointDiagnostics _microphoneRenderEndpointDiagnostics = AudioEndpointDiagnostics.Unknown(AudioEndpointRole.MicrophoneRender);
    private AudioEndpointDiagnostics _speakerCaptureEndpointDiagnostics = AudioEndpointDiagnostics.Unknown(AudioEndpointRole.SpeakerCapture);
    private CameraPreviewFrameReader? _cameraPreviewReader;
    private WriteableBitmap? _cameraPreviewBitmap;
    private long _lastCameraPreviewSequence;
    private DateTimeOffset? _lastCameraPreviewAt;
    private OverviewPreviewFrameReader? _overviewPreviewReader;
    private WriteableBitmap? _overviewPreviewBitmap;
    private long _lastOverviewPreviewSequence;
    private DateTimeOffset? _lastOverviewPreviewAt;
    private OverviewPreviewState _overviewPreviewState = OverviewPreviewState.HostNotStarted;
    private bool _overviewPreviewFillMode;
    private bool _overviewPreviewOverlayVisible = true;
    private OverviewHostServiceState _overviewHostServiceState = OverviewHostServiceState.NotStarted;
    private bool _hostHasStarted;
    private bool _adbRefreshInProgress;
    private IReadOnlyList<AdbDeviceRow> _lastAdbDeviceRows = Array.Empty<AdbDeviceRow>();
    private string _lastAdbStatusText = "等待刷新 Android 设备。";
    private string _overviewHostDetailText = "等待启动";
    private bool _syncingVirtualDisplayOptions;
    private bool _updatingOverviewVirtualDisplaySwitch;
    private bool _virtualDisplayOperationInProgress;
    private bool _virtualDisplayModeApplyInProgress;
    private bool _driverInstallInProgress;
    private bool _syncingOverviewCameraOptions;
    private bool _updatingOverviewCameraSwitch;
    private bool _updatingOverviewAudioSwitch;
    private bool _overviewCameraOperationInProgress;
    private bool _overviewCameraRequestedEnabled = true;
    private bool _overviewCameraBannerDismissed;
    private VirtualDisplayOverviewState? _virtualDisplayTransientState;
    private string? _virtualDisplayLastError;
    private string? _driverInstallLastError;
    private bool? _virtualDisplayDriverInstalledCache;
    private DateTimeOffset _virtualDisplayDriverInstalledCheckedAt;
    private bool? _virtualDisplayToolAvailableCache;
    private DateTimeOffset _virtualDisplayToolAvailableCheckedAt;

    private enum OverviewHostServiceState
    {
        NotStarted,
        Starting,
        Running,
        Stopped,
        Error
    }

    private enum OverviewStepState
    {
        Waiting,
        Active,
        Complete,
        Warning,
        Error
    }

    private enum OverviewNavigationItem
    {
        Overview,
        Connection,
        Display,
        Camera,
        Audio,
        Diagnostics,
        Settings
    }

    private enum OverviewEnvironmentBannerSeverity
    {
        Neutral,
        Ready,
        Warning,
        Error
    }

    private enum VirtualDisplayOverviewState
    {
        DriverMissing,
        DriverInstalling,
        ToolStopped,
        Starting,
        Running,
        Stopping,
        Error
    }

    private enum OverviewPreviewState
    {
        HostNotStarted,
        WaitingSource,
        Receiving,
        Paused,
        Unavailable,
        Error
    }

    public MainWindow()
    {
        InitializeComponent();
        _uiReady = true;
        WireStaticDisplayPage();
        WireStaticDiagnosticsPage();
        StaticOverviewShell.Visibility = StaticOverviewUi ? Visibility.Visible : Visibility.Collapsed;
        LegacyShell.Visibility = StaticOverviewUi ? Visibility.Collapsed : Visibility.Visible;
        if (StaticOverviewUi)
        {
            UpdateOverviewSidebarLayout();
            SetOverviewNavigationItem(OverviewNavigationItem.Connection);
            OverviewMainScrollViewer.SizeChanged += (_, _) => UpdateOverviewMainContentMinHeight();
        }

        _windowHandle = WindowNative.GetWindowHandle(this);
        ApplyWindowIcon();
        if (StaticOverviewUi)
        {
            Title = "SideDock Host";
            ExtendsContentIntoTitleBar = false;
        }
        else
        {
            UpdateDpiDiagnosticTitle();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(null);
        }

        ResizeWindowForCurrentDpi();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        if (!StaticOverviewUi)
        {
            RegisterCardWheelScrolling();
            InitializeTrayIcon();
            AppWindow.Closing += OnAppWindowClosing;
        }

        Closed += (_, _) =>
        {
            _runtimeDiagnosticsTimer.Stop();
            _displayStatusTimer.Stop();
            _cameraPreviewTimer.Stop();
            _overviewPreviewTimer.Stop();
            _virtualCameraStatusTimer.Stop();
            _cameraPreviewReader?.Dispose();
            ResetOverviewPreview(clearImage: true);
            DisposeTrayIcon();
            StopHost();
        };

        InitializeAdbDeviceCombo();
        InitializeOverviewConnectionControls();
        InitializeAudioEndpointCombos();
        InitializeOverviewVirtualDisplayOptions();
        InitializeOverviewCameraOptions();
        LoadAudioPreferences();
        SetRunningState(false);
        UpdateAudioState();
        UpdateCameraStatusView();
        RefreshVirtualCameraStatusFromFiles();

        _displayStatusTimer.Interval = TimeSpan.FromSeconds(2);
        _displayStatusTimer.Tick += (_, _) => RefreshVirtualDisplayState();
        _displayStatusTimer.Start();
        RefreshVirtualDisplayState();

        _cameraPreviewTimer.Interval = TimeSpan.FromMilliseconds(CameraPreviewIntervalMs);
        _cameraPreviewTimer.Tick += (_, _) => UpdateCameraPreview();
        if (!StaticOverviewUi)
        {
            _cameraPreviewTimer.Start();
        }

        UpdateCameraPreviewToggleView();

        _overviewPreviewTimer.Interval = TimeSpan.FromMilliseconds(OverviewPreviewIntervalMs);
        _overviewPreviewTimer.Tick += (_, _) => UpdateOverviewPreview();
        if (StaticOverviewUi)
        {
            _overviewPreviewTimer.Start();
        }

        UpdateOverviewPreviewChrome();
        SetOverviewPreviewState(OverviewPreviewState.HostNotStarted);

        _virtualCameraStatusTimer.Interval = TimeSpan.FromSeconds(2);
        _virtualCameraStatusTimer.Tick += (_, _) => RefreshVirtualCameraStatusFromFiles();
        _virtualCameraStatusTimer.Start();

        _runtimeDiagnosticsTimer.Interval = TimeSpan.FromSeconds(2);
        _runtimeDiagnosticsTimer.Tick += (_, _) => UpdateOverviewRuntimeDiagnostics();
        _runtimeDiagnosticsTimer.Start();
        UpdateOverviewRuntimeDiagnostics();

        _ = RefreshAdbDevicesAsync(showErrors: false);
        _ = RefreshAudioEndpointsAsync(showHint: false);
        _ = RefreshVirtualCameraStatusAsync();
        UpdateOverviewConnectionPage();
        DispatcherQueue.TryEnqueue(UpdateOverviewMainContentMinHeight);
    }

    private void WireStaticDisplayPage()
    {
        OverviewDisplayPage.StartRequested += StaticDisplayPage_StartRequested;
        OverviewDisplayPage.StopRequested += StaticDisplayPage_StopRequested;
        OverviewDisplayPage.RefreshRequested += StaticDisplayPage_RefreshRequested;
        OverviewDisplayPage.InstallDriverRequested += StaticDisplayPage_InstallDriverRequested;
        OverviewDisplayPage.OpenDisplaySettingsRequested += StaticDisplayPage_OpenDisplaySettingsRequested;
        OverviewDisplayPage.ShowLogsRequested += StaticDisplayPage_ShowLogsRequested;
        OverviewDisplayPage.DisplayModeApplyRequested += StaticDisplayPage_DisplayModeApplyRequested;
        OverviewDisplayPage.PresentationModeApplyRequested += StaticDisplayPage_PresentationModeApplyRequested;
    }

    private void WireStaticDiagnosticsPage()
    {
        OverviewDiagnosticsPage.CopyAllRequested += StaticDiagnosticsPage_CopyAllRequested;
        OverviewDiagnosticsPage.ExportLogsRequested += StaticDiagnosticsPage_ExportLogsRequested;
        OverviewDiagnosticsPage.RefreshRequested += StaticDiagnosticsPage_RefreshRequested;
        OverviewDiagnosticsPage.RecheckRequested += StaticDiagnosticsPage_RecheckRequested;
    }

    private void UpdateOverviewMainContentMinHeight()
    {
        if (!StaticOverviewUi)
        {
            return;
        }

        var viewportHeight = OverviewMainScrollViewer.ActualHeight
            - OverviewMainScrollViewer.Padding.Top
            - OverviewMainScrollViewer.Padding.Bottom;
        if (viewportHeight > 0)
        {
            OverviewMainContentGrid.MinHeight = viewportHeight;
        }
    }

    private void ApplyWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SideDock.ico");
        if (!File.Exists(iconPath))
        {
            iconPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "..",
                "assets",
                "SideDock.ico"));
        }

        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private void UpdateOverviewSidebarLayout()
    {
        if (!StaticOverviewUi)
        {
            return;
        }

        OverviewSidebarColumn.Width = new GridLength(_overviewSidebarCollapsed ? 76 : 238);
        OverviewSidebarContent.Padding = _overviewSidebarCollapsed
            ? new Thickness(12, 18, 12, 22)
            : new Thickness(18, 18, 18, 22);

        var textVisibility = _overviewSidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        OverviewAppTitleText.Visibility = textVisibility;
        OverviewSidebarHostStatusPanel.Visibility = textVisibility;
        OverviewSidebarToggleText.Visibility = textVisibility;
        OverviewSidebarToggleIcon.Glyph = _overviewSidebarCollapsed ? "\uE76C" : "\uE72B";
        ToolTipService.SetToolTip(OverviewSidebarToggleButton, _overviewSidebarCollapsed ? "展开菜单" : "折叠菜单");

        foreach (var (_, container, _, _, text) in OverviewNavigationVisuals())
        {
            container.Padding = _overviewSidebarCollapsed
                ? new Thickness(10, 0, 10, 0)
                : new Thickness(14, 0, 14, 0);
            text.Visibility = textVisibility;
        }
    }

    private (OverviewNavigationItem Item, Border Container, Border Indicator, FontIcon Icon, TextBlock Text)[] OverviewNavigationVisuals()
    {
        return new[]
        {
            (OverviewNavigationItem.Overview, OverviewNavOverviewItem, OverviewNavOverviewIndicator, OverviewNavOverviewIcon, OverviewNavOverviewText),
            (OverviewNavigationItem.Connection, OverviewNavConnectionItem, OverviewNavConnectionIndicator, OverviewNavConnectionIcon, OverviewNavConnectionText),
            (OverviewNavigationItem.Display, OverviewNavDisplayItem, OverviewNavDisplayIndicator, OverviewNavDisplayIcon, OverviewNavDisplayText),
            (OverviewNavigationItem.Camera, OverviewNavCameraItem, OverviewNavCameraIndicator, OverviewNavCameraIcon, OverviewNavCameraText),
            (OverviewNavigationItem.Audio, OverviewNavAudioItem, OverviewNavAudioIndicator, OverviewNavAudioIcon, OverviewNavAudioText),
            (OverviewNavigationItem.Diagnostics, OverviewNavDiagnosticsItem, OverviewNavDiagnosticsIndicator, OverviewNavDiagnosticsIcon, OverviewNavDiagnosticsText),
            (OverviewNavigationItem.Settings, OverviewNavSettingsItem, OverviewNavSettingsIndicator, OverviewNavSettingsIcon, OverviewNavSettingsText)
        };
    }

    private void SetOverviewNavigationItem(OverviewNavigationItem item)
    {
        if (!StaticOverviewUi)
        {
            return;
        }

        _overviewNavigationItem = item;
        foreach (var (navItem, container, indicator, icon, text) in OverviewNavigationVisuals())
        {
            var active = navItem == item;
            container.Background = active ? _overviewNavActiveBackgroundBrush : _overviewTransparentBrush;
            indicator.Opacity = active ? 1 : 0;
            icon.Foreground = active ? _overviewPrimaryBrush : new SolidColorBrush(ColorHelper.FromArgb(255, 48, 54, 61));
            text.Foreground = active ? new SolidColorBrush(ColorHelper.FromArgb(255, 17, 24, 39)) : new SolidColorBrush(ColorHelper.FromArgb(255, 48, 54, 61));
            text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        UpdateOverviewPageChrome(item);
    }

    private void UpdateOverviewPageChrome(OverviewNavigationItem item)
    {
        if (!StaticOverviewUi)
        {
            return;
        }

        OverviewPageTitleText.Text = item switch
        {
            OverviewNavigationItem.Connection => "连接",
            OverviewNavigationItem.Display => "虚拟显示器",
            OverviewNavigationItem.Camera => "摄像头",
            OverviewNavigationItem.Audio => "音频",
            OverviewNavigationItem.Diagnostics => "诊断",
            OverviewNavigationItem.Settings => "设置",
            _ => "设备总览"
        };

        var showDisplayPage = item == OverviewNavigationItem.Display;
        var showCameraPage = item == OverviewNavigationItem.Camera;
        var showAudioPage = item == OverviewNavigationItem.Audio;
        var showDiagnosticsPage = item == OverviewNavigationItem.Diagnostics;
        var showSettingsPage = item == OverviewNavigationItem.Settings;
        OverviewRightShell.Visibility = Visibility.Visible;
        OverviewDisplayPage.Visibility = showDisplayPage ? Visibility.Visible : Visibility.Collapsed;
        OverviewCameraScrollViewer.Visibility = showCameraPage ? Visibility.Visible : Visibility.Collapsed;
        OverviewAudioPage.Visibility = showAudioPage ? Visibility.Visible : Visibility.Collapsed;
        OverviewDiagnosticsPage.Visibility = showDiagnosticsPage ? Visibility.Visible : Visibility.Collapsed;
        OverviewSettingsPage.Visibility = showSettingsPage ? Visibility.Visible : Visibility.Collapsed;

        OverviewStartHostButtonText.Text = showCameraPage ? "启动虚拟相机" : "启动主机";
        OverviewPrimaryActionIcon.Glyph = showCameraPage ? "\uE722" : "\uE768";
        OverviewStartHostButton.Visibility = showAudioPage || showDiagnosticsPage || showSettingsPage ? Visibility.Collapsed : Visibility.Visible;
        OverviewDefaultHeaderActions.Visibility = showAudioPage || showDiagnosticsPage || showSettingsPage ? Visibility.Collapsed : Visibility.Visible;
        OverviewAudioHeaderActions.Visibility = showAudioPage ? Visibility.Visible : Visibility.Collapsed;
        OverviewSettingsHeaderActions.Visibility = showSettingsPage ? Visibility.Visible : Visibility.Collapsed;

        var showConnectionPage = item == OverviewNavigationItem.Connection;
        OverviewConnectionScrollViewer.Visibility = showConnectionPage ? Visibility.Visible : Visibility.Collapsed;
        OverviewMainScrollViewer.Visibility = showConnectionPage || showDisplayPage || showCameraPage || showAudioPage || showDiagnosticsPage || showSettingsPage ? Visibility.Collapsed : Visibility.Visible;

        if (showCameraPage)
        {
            if (_cameraPreviewEnabled)
            {
                _cameraPreviewTimer.Start();
                UpdateCameraPreview();
            }

            UpdateCameraStatusView();
        }
    }

    private void OpenOverviewNavigationItem(OverviewNavigationItem item)
    {
        SetOverviewNavigationItem(item);
        if (item == OverviewNavigationItem.Overview)
        {
            OverviewNavigationDetailPanel.Visibility = Visibility.Collapsed;
            OverviewMainScrollViewer.ChangeView(null, 0, null);
            return;
        }

        if (item == OverviewNavigationItem.Connection)
        {
            OverviewNavigationDetailPanel.Visibility = Visibility.Collapsed;
            OverviewConnectionScrollViewer.ChangeView(null, 0, null);
            return;
        }

        if (item == OverviewNavigationItem.Camera)
        {
            OverviewNavigationDetailPanel.Visibility = Visibility.Collapsed;
            OverviewCameraScrollViewer.ChangeView(null, 0, null);
            return;
        }

        if (item == OverviewNavigationItem.Settings)
        {
            OverviewNavigationDetailPanel.Visibility = Visibility.Collapsed;
            OverviewSettingsPage.ScrollToTop();
            return;
        }

        if (item == OverviewNavigationItem.Diagnostics)
        {
            OverviewNavigationDetailPanel.Visibility = Visibility.Collapsed;
            UpdateStaticDiagnosticsPage();
            return;
        }

        SetOverviewNavigationDetail(item);
        OverviewNavigationDetailPanel.Visibility = Visibility.Visible;
        OverviewNavigationDetailPanel.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = true,
            VerticalAlignmentRatio = 0
        });
    }

    private void SetOverviewNavigationDetail(OverviewNavigationItem item)
    {
        var (title, detail, glyph) = item switch
        {
            OverviewNavigationItem.Connection => (
                "连接设置",
                "连接设置区域已打开。设备选择、ADB reverse 和主机连接状态目前由下方连接向导承载，这里作为新版设置入口占位。",
                "\uE71B"),
            OverviewNavigationItem.Display => (
                "虚拟显示器设置",
                "虚拟显示器设置区域已打开。分辨率、刷新率和驱动修复入口已接入下方虚拟显示器卡片，这里作为后续集中设置占位。",
                "\uE7F4"),
            OverviewNavigationItem.Camera => (
                "摄像头设置",
                "摄像头设置区域已打开。镜头方向、分辨率和帧率入口已接入摄像头设置对话框，这里作为后续集中设置占位。",
                "\uE722"),
            OverviewNavigationItem.Audio => (
                "音频设置",
                "音频设置区域已打开。Windows 输出 loopback 和 Android 麦克风写入端点已接入音频设置对话框，这里作为后续集中设置占位。",
                "\uE995"),
            OverviewNavigationItem.Diagnostics => (
                "详细诊断",
                "详细诊断区域已打开。运行指标、视频链路、虚拟显示器、摄像头、音频和日志可从诊断详情中查看与复制。",
                "\uE9D9"),
            OverviewNavigationItem.Settings => (
                "综合设置",
                "综合设置入口已打开。后续会把主机、ADB、显示、摄像头和音频偏好整合到这里。",
                "\uE713"),
            _ => (
                "设备总览",
                "当前停留在设备总览。",
                "\uE80F")
        };

        OverviewNavigationDetailTitleText.Text = title;
        OverviewNavigationDetailText.Text = detail;
        OverviewNavigationDetailIcon.Glyph = glyph;
    }

    private void InitializeOverviewConnectionControls()
    {
        if (!StaticOverviewUi)
        {
            return;
        }

        _syncingOverviewConnectionControls = true;
        try
        {
            OverviewControlPortBox.Value = double.IsNaN(ControlPortBox.Value) ? DefaultControlPort : ControlPortBox.Value;
            OverviewVideoPortBox.Value = double.IsNaN(VideoPortBox.Value) ? DefaultVideoPort : VideoPortBox.Value;
            OverviewAudioPortBox.Value = DefaultAudioPort;
            OverviewCameraPortBox.Value = DefaultCameraPort;
            OverviewCameraPagePortBox.Value = OverviewCameraPortBox.Value;
            OverviewAdbPathBox.Text = AdbPathBox.Text;
            OverviewInputInjectionSwitch.IsOn = InputInjectionSwitch.IsOn;
            OverviewInputInjectionStatusText.Text = OverviewInputInjectionSwitch.IsOn ? "已启用" : "未启用";
        }
        finally
        {
            _syncingOverviewConnectionControls = false;
        }

        UpdateOverviewFooterMachineInfo();
        UpdateOverviewConnectionPage();
    }

    private bool CanStartOverviewHost()
    {
        return _overviewHostServiceState is OverviewHostServiceState.NotStarted
            or OverviewHostServiceState.Stopped
            or OverviewHostServiceState.Error;
    }

    private void SyncOverviewConnectionControlsToLegacy()
    {
        if (!StaticOverviewUi)
        {
            return;
        }

        ControlPortBox.Value = OverviewControlPortBox.Value;
        VideoPortBox.Value = OverviewVideoPortBox.Value;
        AdbPathBox.Text = OverviewAdbPathBox.Text;
        InputInjectionSwitch.IsOn = OverviewInputInjectionSwitch.IsOn;
    }

    private void UpdateOverviewConnectionPage()
    {
        if (!StaticOverviewUi)
        {
            return;
        }

        SyncOverviewConnectionControlsToLegacy();
        UpdateOverviewConnectionButtons();
        UpdateOverviewConnectionDeviceSummary();
        UpdateOverviewConnectionDeviceList();
        UpdateOverviewConnectionPortStatus();
        UpdateOverviewConnectionChecklist();
        UpdateOverviewFooterMachineInfo();
        UpdateStaticDiagnosticsPage();
    }

    private void UpdateOverviewConnectionButtons()
    {
        var hostRunning = _hostProcess is { HasExited: false };
        var starting = _overviewHostServiceState == OverviewHostServiceState.Starting;
        var busy = _overviewRefreshInProgress || _adbRefreshInProgress || starting;
        var canEditStartupSettings = !hostRunning && !starting;

        OverviewConnectionStartHostButton.IsEnabled = CanStartOverviewHost() && !busy;
        OverviewConnectionMoreActionsButton.IsEnabled = true;
        OverviewConnectionAdbDeviceCombo.IsEnabled = canEditStartupSettings && !_adbRefreshInProgress;
        OverviewConnectionDeviceListView.IsEnabled = canEditStartupSettings && !_adbRefreshInProgress;
        OverviewRestoreDefaultPortsButton.IsEnabled = canEditStartupSettings;
        OverviewAdvancedConnectionOptionsButton.IsEnabled = true;
        OverviewAdbPathBox.IsEnabled = canEditStartupSettings;
        OverviewAdbPathBrowseButton.IsEnabled = canEditStartupSettings;
        OverviewControlPortBox.IsEnabled = canEditStartupSettings;
        OverviewVideoPortBox.IsEnabled = canEditStartupSettings;
        OverviewAudioPortBox.IsEnabled = canEditStartupSettings;
        OverviewCameraPortBox.IsEnabled = canEditStartupSettings;
        OverviewInputInjectionSwitch.IsEnabled = canEditStartupSettings;
    }

    private void UpdateOverviewConnectionDeviceSummary()
    {
        var selectedChoice = SelectedAdbDeviceChoice();
        var selectedRow = SelectedAdbDeviceRow();
        var authorizedRows = _lastAdbDeviceRows
            .Where(row => row.State.Equals("device", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (selectedRow is not null)
        {
            var status = BuildAdbDeviceStatusView(selectedRow.State);
            OverviewConnectionSelectedDeviceNameText.Text = FormatAdbDeviceDisplayName(selectedRow);
            OverviewConnectionSelectedDeviceStatusText.Text = status.Text;
            OverviewConnectionSelectedDeviceStatusText.Foreground = status.Brush;
            OverviewConnectionSelectedDeviceStatusBadge.Background = status.Background;
            OverviewConnectionSelectedDeviceStatusBadge.BorderBrush = status.Border;
            OverviewConnectionSelectedDeviceDetailText.Text = BuildAdbDeviceDetailText(selectedRow);
            OverviewConnectionSelectedDeviceTransportText.Text = FormatAdbTransport(selectedRow);
            OverviewConnectionSelectedDeviceCard.BorderBrush = selectedRow.State.Equals("device", StringComparison.OrdinalIgnoreCase)
                ? _overviewReadyBorderBrush
                : _overviewWarningBorderBrush;
            OverviewConnectionDeviceIconBorder.BorderBrush = status.Brush;
            OverviewConnectionSelectedDeviceBatteryPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (selectedChoice is { Serial.Length: > 0 })
        {
            OverviewConnectionSelectedDeviceNameText.Text = selectedChoice.Serial;
            OverviewConnectionSelectedDeviceStatusText.Text = "未检测到";
            OverviewConnectionSelectedDeviceStatusText.Foreground = _warningBrush;
            OverviewConnectionSelectedDeviceStatusBadge.Background = _overviewWarningBackgroundBrush;
            OverviewConnectionSelectedDeviceStatusBadge.BorderBrush = _overviewWarningBorderBrush;
            OverviewConnectionSelectedDeviceDetailText.Text = "所选设备不在当前 ADB 列表中，请刷新后重试。";
            OverviewConnectionSelectedDeviceTransportText.Text = "暂无数据";
            OverviewConnectionSelectedDeviceCard.BorderBrush = _overviewWarningBorderBrush;
            OverviewConnectionDeviceIconBorder.BorderBrush = _warningBrush;
            OverviewConnectionSelectedDeviceBatteryPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (authorizedRows.Length > 1)
        {
            OverviewConnectionSelectedDeviceNameText.Text = "请选择设备";
            OverviewConnectionSelectedDeviceStatusText.Text = "多设备";
            OverviewConnectionSelectedDeviceStatusText.Foreground = _warningBrush;
            OverviewConnectionSelectedDeviceStatusBadge.Background = _overviewWarningBackgroundBrush;
            OverviewConnectionSelectedDeviceStatusBadge.BorderBrush = _overviewWarningBorderBrush;
            OverviewConnectionSelectedDeviceDetailText.Text = $"检测到 {authorizedRows.Length} 台已授权设备，请从右侧下拉框选择一台。";
            OverviewConnectionSelectedDeviceTransportText.Text = "ADB";
            OverviewConnectionSelectedDeviceCard.BorderBrush = _overviewWarningBorderBrush;
            OverviewConnectionDeviceIconBorder.BorderBrush = _warningBrush;
            OverviewConnectionSelectedDeviceBatteryPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var firstRow = _lastAdbDeviceRows.FirstOrDefault();
        if (firstRow is not null)
        {
            var status = BuildAdbDeviceStatusView(firstRow.State);
            OverviewConnectionSelectedDeviceNameText.Text = FormatAdbDeviceDisplayName(firstRow);
            OverviewConnectionSelectedDeviceStatusText.Text = status.Text;
            OverviewConnectionSelectedDeviceStatusText.Foreground = status.Brush;
            OverviewConnectionSelectedDeviceStatusBadge.Background = status.Background;
            OverviewConnectionSelectedDeviceStatusBadge.BorderBrush = status.Border;
            OverviewConnectionSelectedDeviceDetailText.Text = BuildAdbDeviceDetailText(firstRow);
            OverviewConnectionSelectedDeviceTransportText.Text = FormatAdbTransport(firstRow);
            OverviewConnectionSelectedDeviceCard.BorderBrush = _overviewWarningBorderBrush;
            OverviewConnectionDeviceIconBorder.BorderBrush = status.Brush;
            OverviewConnectionSelectedDeviceBatteryPanel.Visibility = Visibility.Collapsed;
            return;
        }

        OverviewConnectionSelectedDeviceNameText.Text = _adbRefreshInProgress ? "正在刷新" : "暂无设备";
        OverviewConnectionSelectedDeviceStatusText.Text = _adbRefreshInProgress ? "刷新中" : "等待刷新";
        OverviewConnectionSelectedDeviceStatusText.Foreground = _overviewNeutralBrush;
        OverviewConnectionSelectedDeviceStatusBadge.Background = _overviewNeutralBackgroundBrush;
        OverviewConnectionSelectedDeviceStatusBadge.BorderBrush = _overviewNeutralBorderBrush;
        OverviewConnectionSelectedDeviceDetailText.Text = _adbRefreshInProgress
            ? "正在读取 adb devices -l。"
            : "暂无数据，请连接 USB 并刷新设备。";
        OverviewConnectionSelectedDeviceTransportText.Text = "暂无数据";
        OverviewConnectionSelectedDeviceCard.BorderBrush = _overviewNeutralBorderBrush;
        OverviewConnectionDeviceIconBorder.BorderBrush = _overviewNeutralBrush;
        OverviewConnectionSelectedDeviceBatteryPanel.Visibility = Visibility.Collapsed;
    }

    private void UpdateOverviewConnectionDeviceList()
    {
        var items = _lastAdbDeviceRows
            .Select(row => new OverviewConnectionDeviceItem(
                row.Serial,
                FormatAdbDeviceDisplayName(row),
                FormatAdbDeviceState(row.State),
                BuildAdbDeviceStatusView(row.State).Brush,
                BuildAdbDeviceStatusView(row.State).DotBrush,
                FormatAdbTransport(row),
                FormatLastAdbRefreshText()))
            .ToArray();

        _syncingAdbDeviceSelection = true;
        try
        {
            OverviewConnectionDeviceListView.ItemsSource = items;
            OverviewConnectionDeviceListView.Visibility = items.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            OverviewConnectionDeviceEmptyText.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            OverviewConnectionDeviceEmptyText.Text = _adbRefreshInProgress
                ? "正在刷新设备..."
                : "暂无数据，等待刷新";

            var selectedSerial = SelectedAdbSerial();
            OverviewConnectionDeviceListView.SelectedItem = items.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(selectedSerial)
                && item.Serial.Equals(selectedSerial, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _syncingAdbDeviceSelection = false;
        }
    }

    private void UpdateOverviewConnectionChecklist()
    {
        var (usbState, usbDetail) = BuildOverviewConnectionUsbStep();
        SetOverviewConnectionChecklistStep(
            OverviewConnectionUsbBadge,
            OverviewConnectionUsbIcon,
            OverviewConnectionUsbDetailText,
            OverviewConnectionUsbStatusText,
            usbDetail,
            usbState);

        var (authState, authDetail) = BuildOverviewConnectionAuthStep();
        SetOverviewConnectionChecklistStep(
            OverviewConnectionAuthBadge,
            OverviewConnectionAuthIcon,
            OverviewConnectionAuthDetailText,
            OverviewConnectionAuthStatusText,
            authDetail,
            authState);

        var (reverseState, reverseDetail) = BuildOverviewConnectionReverseStep(authState);
        SetOverviewConnectionChecklistStep(
            OverviewConnectionReverseBadge,
            OverviewConnectionReverseIcon,
            OverviewConnectionReverseDetailText,
            OverviewConnectionReverseStatusText,
            reverseDetail,
            reverseState);

        var (portState, portDetail) = BuildOverviewConnectionPortStep();
        SetOverviewConnectionChecklistStep(
            OverviewConnectionPortBadge,
            OverviewConnectionPortIcon,
            OverviewConnectionPortDetailText,
            OverviewConnectionPortStatusText,
            portDetail,
            portState);

        var (readyState, readyDetail) = BuildOverviewConnectionReadyStep(authState, portState);
        SetOverviewConnectionChecklistStep(
            OverviewConnectionReadyBadge,
            OverviewConnectionReadyIcon,
            OverviewConnectionReadyDetailText,
            OverviewConnectionReadyStatusText,
            readyDetail,
            readyState);
    }

    private (OverviewStepState State, string Detail) BuildOverviewConnectionUsbStep()
    {
        if (_adbRefreshInProgress)
        {
            return (OverviewStepState.Active, "正在刷新 ADB 设备列表");
        }

        if (_lastAdbDeviceRows.Count == 0)
        {
            return (OverviewStepState.Waiting, "暂无数据，请刷新设备");
        }

        var selectedRow = SelectedAdbDeviceRow();
        if (selectedRow is not null)
        {
            if (IsAdbNetworkSerial(selectedRow.Serial))
            {
                return (OverviewStepState.Warning, $"当前选择网络 ADB：{selectedRow.Serial}");
            }

            return IsUsbAdbRow(selectedRow)
                ? (OverviewStepState.Complete, $"已检测到 USB 设备：{selectedRow.Serial}")
                : (OverviewStepState.Warning, $"已检测到 ADB 设备，未确认 USB 通道：{selectedRow.Serial}");
        }

        var usbCount = _lastAdbDeviceRows.Count(IsUsbAdbRow);
        if (usbCount > 0)
        {
            return (OverviewStepState.Complete, $"检测到 {usbCount} 台 USB/ADB 设备");
        }

        return (OverviewStepState.Warning, "检测到 ADB 设备，但连接方式未知");
    }

    private (OverviewStepState State, string Detail) BuildOverviewConnectionAuthStep()
    {
        var selectedRow = SelectedAdbDeviceRow();
        if (selectedRow is not null)
        {
            if (selectedRow.State.Equals("device", StringComparison.OrdinalIgnoreCase))
            {
                return (OverviewStepState.Complete, $"已授权：{selectedRow.Serial}");
            }

            return selectedRow.State.Equals("offline", StringComparison.OrdinalIgnoreCase)
                ? (OverviewStepState.Error, $"设备离线：{selectedRow.Serial}")
                : (OverviewStepState.Warning, $"设备状态为 {selectedRow.State}：{selectedRow.Serial}");
        }

        var authorizedRows = _lastAdbDeviceRows
            .Where(row => row.State.Equals("device", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (authorizedRows.Length == 1)
        {
            return (OverviewStepState.Complete, $"已授权：{authorizedRows[0].Serial}");
        }

        if (authorizedRows.Length > 1)
        {
            return (OverviewStepState.Warning, $"检测到 {authorizedRows.Length} 台已授权设备，请选择一台");
        }

        var unauthorizedRow = _lastAdbDeviceRows.FirstOrDefault(row =>
            row.State.Equals("unauthorized", StringComparison.OrdinalIgnoreCase));
        if (unauthorizedRow is not null)
        {
            return (OverviewStepState.Warning, $"请在设备上允许 USB 调试：{unauthorizedRow.Serial}");
        }

        var offlineRow = _lastAdbDeviceRows.FirstOrDefault(row =>
            row.State.Equals("offline", StringComparison.OrdinalIgnoreCase));
        if (offlineRow is not null)
        {
            return (OverviewStepState.Error, $"设备离线：{offlineRow.Serial}");
        }

        return _lastAdbDeviceRows.Count > 0
            ? (OverviewStepState.Warning, "未检测到已授权设备")
            : (OverviewStepState.Waiting, "等待设备授权状态");
    }

    private (OverviewStepState State, string Detail) BuildOverviewConnectionReverseStep(OverviewStepState authState)
    {
        if (_overviewHostServiceState == OverviewHostServiceState.Starting)
        {
            return (OverviewStepState.Active, _lastAdbStatusText);
        }

        if (_lastAdbReverseConfigured == true)
        {
            return (OverviewStepState.Complete, _lastAdbReverseDetail);
        }

        if (_lastAdbReverseConfigured == false)
        {
            return (OverviewStepState.Error, _lastAdbReverseDetail);
        }

        if (authState == OverviewStepState.Complete)
        {
            return (OverviewStepState.Waiting, "启动主机时自动配置 ADB reverse");
        }

        if (authState == OverviewStepState.Warning)
        {
            return (OverviewStepState.Warning, "请先选择并授权设备");
        }

        return (OverviewStepState.Waiting, "等待授权设备");
    }

    private (OverviewStepState State, string Detail) BuildOverviewConnectionPortStep()
    {
        return BuildOverviewConnectionPortSummary();
    }

    private (OverviewStepState State, string Detail) BuildOverviewConnectionReadyStep(
        OverviewStepState authState,
        OverviewStepState portState)
    {
        return _overviewHostServiceState switch
        {
            OverviewHostServiceState.Running => (OverviewStepState.Complete, "会话正在运行"),
            OverviewHostServiceState.Starting => (OverviewStepState.Active, "正在启动会话"),
            OverviewHostServiceState.Error => (OverviewStepState.Error, _overviewHostDetailText),
            _ when portState == OverviewStepState.Error => (OverviewStepState.Error, "请修正端口配置后再启动"),
            _ when authState == OverviewStepState.Complete => (OverviewStepState.Complete, "可以启动会话"),
            _ when authState == OverviewStepState.Warning => (OverviewStepState.Warning, "请先处理设备选择或授权"),
            _ => (OverviewStepState.Waiting, "等待设备和端口检查")
        };
    }

    private void UpdateStaticDiagnosticsPage()
    {
        if (!StaticOverviewUi || !_uiReady || OverviewDiagnosticsPage is null)
        {
            return;
        }

        OverviewDiagnosticsPage.UpdateState(BuildStaticDiagnosticsPageState());
    }

    private DiagnosticsPageState BuildStaticDiagnosticsPageState()
    {
        var host = BuildDiagnosticsHostCard();
        var adbReverse = BuildDiagnosticsAdbReverseCard();
        var packetLoss = BuildDiagnosticsPacketLossCard();
        var latency = BuildDiagnosticsLatencyCard();

        var (authState, authDetail) = BuildOverviewConnectionAuthStep();
        var androidAuthorization = ToDiagnosticsHealthCheck(authState, authDetail);
        var portListening = BuildDiagnosticsPortListeningHealth();

        var (reverseState, reverseDetail) = BuildOverviewConnectionReverseStep(authState);
        var adbReverseHealth = ToDiagnosticsHealthCheck(reverseState, reverseDetail);

        var virtualDisplay = BuildDiagnosticsVirtualDisplayHealth();
        var virtualCamera = BuildDiagnosticsVirtualCameraHealth();
        var audioEndpoint = BuildDiagnosticsAudioEndpointHealth();

        var issues = new (string Label, DiagnosticsStatusKind Status, string Detail)[]
        {
            ("Host", host.Status, host.Detail),
            ("Android 授权", androidAuthorization.Status, androidAuthorization.Detail),
            ("端口监听", portListening.Status, portListening.Detail),
            ("ADB reverse", adbReverseHealth.Status, adbReverseHealth.Detail),
            ("虚拟显示器", virtualDisplay.Status, virtualDisplay.Detail),
            ("虚拟相机", virtualCamera.Status, virtualCamera.Detail),
            ("音频端点", audioEndpoint.Status, audioEndpoint.Detail),
            ("丢帧/丢包率", packetLoss.Status, packetLoss.Detail),
            ("延迟", latency.Status, latency.Detail)
        };
        var overallStatus = WorstDiagnosticsStatus(issues.Select(issue => issue.Status));
        var firstIssue = issues.FirstOrDefault(issue => issue.Status == overallStatus && issue.Status != DiagnosticsStatusKind.Normal);

        return new DiagnosticsPageState
        {
            OverallStatus = overallStatus,
            OverallTitle = DiagnosticsOverallTitle(overallStatus),
            OverallDetail = overallStatus == DiagnosticsStatusKind.Normal
                ? "所有关键服务运行正常，系统性能良好。"
                : string.IsNullOrWhiteSpace(firstIssue.Label)
                    ? "请点击刷新或启动主机后查看实时诊断。"
                    : $"{firstIssue.Label}：{firstIssue.Detail}",
            Host = host,
            AdbReverse = adbReverse,
            PacketLoss = packetLoss,
            Latency = latency,
            AndroidAuthorization = androidAuthorization,
            PortListening = portListening,
            AdbReverseHealth = adbReverseHealth,
            VirtualDisplay = virtualDisplay,
            VirtualCamera = virtualCamera,
            AudioEndpoint = audioEndpoint
        };
    }

    private DiagnosticsStatusCardState BuildDiagnosticsHostCard()
    {
        if (TryGetRunningHostProcess(out _))
        {
            return new DiagnosticsStatusCardState
            {
                Status = DiagnosticsStatusKind.Normal,
                Value = "运行中",
                Detail = $"{FormatHostProcessState()} · CPU {_lastOverviewCpuText} · 内存 {_lastOverviewMemoryText}"
            };
        }

        return _overviewHostServiceState switch
        {
            OverviewHostServiceState.Starting => new DiagnosticsStatusCardState
            {
                Status = DiagnosticsStatusKind.Unknown,
                Value = "启动中",
                Detail = _overviewHostDetailText
            },
            OverviewHostServiceState.Error => new DiagnosticsStatusCardState
            {
                Status = DiagnosticsStatusKind.Error,
                Value = "错误",
                Detail = _overviewHostDetailText
            },
            OverviewHostServiceState.Stopped => new DiagnosticsStatusCardState
            {
                Status = DiagnosticsStatusKind.Warning,
                Value = "已停止",
                Detail = "主机已停止"
            },
            _ => new DiagnosticsStatusCardState
            {
                Status = DiagnosticsStatusKind.Warning,
                Value = "未启动",
                Detail = "等待启动"
            }
        };
    }

    private DiagnosticsStatusCardState BuildDiagnosticsAdbReverseCard()
    {
        if (_lastAdbReverseConfigured == true)
        {
            return new DiagnosticsStatusCardState
            {
                Status = DiagnosticsStatusKind.Normal,
                Value = "正常",
                Detail = _lastAdbReverseDetail
            };
        }

        if (_lastAdbReverseConfigured == false)
        {
            return new DiagnosticsStatusCardState
            {
                Status = DiagnosticsStatusKind.Error,
                Value = "错误",
                Detail = _lastAdbReverseDetail
            };
        }

        return new DiagnosticsStatusCardState
        {
            Status = DiagnosticsStatusKind.Unknown,
            Value = "暂无数据",
            Detail = "启动主机时自动配置 ADB reverse"
        };
    }

    private DiagnosticsStatusCardState BuildDiagnosticsPacketLossCard()
    {
        if (!TryGetRunningHostProcess(out _))
        {
            return new DiagnosticsStatusCardState
            {
                Status = DiagnosticsStatusKind.Unknown,
                Value = "暂无数据",
                Detail = "主机未运行"
            };
        }

        if (_videoDiagnostics.TryGetDroppedFrameRate(out var droppedFrameRate))
        {
            return new DiagnosticsStatusCardState
            {
                Status = droppedFrameRate >= 20
                    ? DiagnosticsStatusKind.Error
                    : droppedFrameRate >= 5
                        ? DiagnosticsStatusKind.Warning
                        : DiagnosticsStatusKind.Normal,
                Value = _lastOverviewPacketLossText,
                Detail = droppedFrameRate >= 5 ? "丢帧偏高" : "良好"
            };
        }

        return new DiagnosticsStatusCardState
        {
            Status = DiagnosticsStatusKind.Unknown,
            Value = _lastOverviewPacketLossText,
            Detail = "等待视频统计"
        };
    }

    private DiagnosticsStatusCardState BuildDiagnosticsLatencyCard()
    {
        if (!TryGetRunningHostProcess(out _))
        {
            return new DiagnosticsStatusCardState
            {
                Status = DiagnosticsStatusKind.Unknown,
                Value = "暂无数据",
                Detail = "主机未运行"
            };
        }

        if (TryGetOverviewLatencyMilliseconds(out var latencyMs))
        {
            return new DiagnosticsStatusCardState
            {
                Status = latencyMs > 100
                    ? DiagnosticsStatusKind.Error
                    : latencyMs > 50
                        ? DiagnosticsStatusKind.Warning
                        : DiagnosticsStatusKind.Normal,
                Value = _lastOverviewLatencyText,
                Detail = _lastOverviewLatencyDetailText
            };
        }

        return new DiagnosticsStatusCardState
        {
            Status = DiagnosticsStatusKind.Unknown,
            Value = _lastOverviewLatencyText,
            Detail = _lastOverviewLatencyDetailText
        };
    }

    private bool TryGetOverviewLatencyMilliseconds(out double latencyMs)
    {
        if (_videoDiagnostics.HasRecentVideoStats && _videoDiagnostics.RoughLatencyMs > 0)
        {
            latencyMs = _videoDiagnostics.RoughLatencyMs;
            return true;
        }

        if (_videoDiagnostics.HasRecentVideoStats && _videoDiagnostics.LocalPipelineLatencyMs > 0)
        {
            latencyMs = _videoDiagnostics.LocalPipelineLatencyMs;
            return true;
        }

        if (IsCameraReceiving(_cameraDiagnostics) && _cameraDiagnostics.DecodeLagMs > 0)
        {
            latencyMs = _cameraDiagnostics.DecodeLagMs;
            return true;
        }

        latencyMs = 0;
        return false;
    }

    private DiagnosticsHealthCheckState BuildDiagnosticsPortListeningHealth()
    {
        var (portState, portDetail) = BuildOverviewConnectionPortSummary();
        if (portState == OverviewStepState.Error)
        {
            return ToDiagnosticsHealthCheck(portState, portDetail);
        }

        var portValues = OverviewPortStatusControls()
            .Select(port => (port.Name, Valid: TryReadPort(port.NumberBox, out var value), Value: value))
            .ToArray();
        if (portValues.Any(port => !port.Valid))
        {
            return ToDiagnosticsHealthCheck(OverviewStepState.Error, portDetail);
        }

        if (!TryGetRunningHostProcess(out _))
        {
            return new DiagnosticsHealthCheckState
            {
                Status = DiagnosticsStatusKind.Unknown,
                Detail = $"主机未运行，端口尚未监听。{portDetail}"
            };
        }

        var activePorts = GetActiveTcpListenerPorts();
        var listeningPorts = portValues
            .Where(port => activePorts.Contains(port.Value))
            .ToArray();
        if (listeningPorts.Length == portValues.Length)
        {
            return new DiagnosticsHealthCheckState
            {
                Status = DiagnosticsStatusKind.Normal,
                Detail = $"{listeningPorts.Length}/{portValues.Length} 端口正在监听"
            };
        }

        if (listeningPorts.Length > 0)
        {
            return new DiagnosticsHealthCheckState
            {
                Status = DiagnosticsStatusKind.Warning,
                Detail = $"{listeningPorts.Length}/{portValues.Length} 端口正在监听：{string.Join("、", listeningPorts.Select(port => port.Value))}"
            };
        }

        return new DiagnosticsHealthCheckState
        {
            Status = DiagnosticsStatusKind.Warning,
            Detail = "主机运行中，暂未检测到配置端口监听"
        };
    }

    private DiagnosticsHealthCheckState BuildDiagnosticsVirtualDisplayHealth()
    {
        var running = IsVirtualDisplayToolRunning();
        var state = DetermineVirtualDisplayOverviewState(running, out var driverInstalled, out var toolAvailable);
        var (statusText, detail, _) = BuildVirtualDisplayStatusView(state, driverInstalled, toolAvailable);

        var status = state switch
        {
            VirtualDisplayOverviewState.Running => DiagnosticsStatusKind.Normal,
            VirtualDisplayOverviewState.Error => DiagnosticsStatusKind.Error,
            VirtualDisplayOverviewState.DriverMissing => DiagnosticsStatusKind.Warning,
            VirtualDisplayOverviewState.ToolStopped => DiagnosticsStatusKind.Warning,
            _ => DiagnosticsStatusKind.Unknown
        };

        return new DiagnosticsHealthCheckState
        {
            Status = status,
            Detail = $"{statusText}：{detail}"
        };
    }

    private DiagnosticsHealthCheckState BuildDiagnosticsVirtualCameraHealth()
    {
        var (statusText, hintText, _) = BuildOverviewCameraStatusView();
        if (_overviewCameraOperationInProgress)
        {
            return new DiagnosticsHealthCheckState
            {
                Status = DiagnosticsStatusKind.Unknown,
                Detail = $"{statusText}：{hintText}"
            };
        }

        if (!_overviewCameraRequestedEnabled)
        {
            return new DiagnosticsHealthCheckState
            {
                Status = DiagnosticsStatusKind.Warning,
                Detail = "摄像头管线未启用"
            };
        }

        if (HasCameraError())
        {
            return new DiagnosticsHealthCheckState
            {
                Status = _hostProcess is { HasExited: false } ? DiagnosticsStatusKind.Error : DiagnosticsStatusKind.Warning,
                Detail = FirstNonEmpty(_cameraDiagnostics.LastError, _virtualCameraDiagnostics.LastError, hintText)
            };
        }

        if (_virtualCameraDiagnostics.Running && _virtualCameraDiagnostics.Registered)
        {
            return new DiagnosticsHealthCheckState
            {
                Status = DiagnosticsStatusKind.Normal,
                Detail = $"已注册并运行，设备数 {_virtualCameraDiagnostics.DeviceCount}，供帧 {FormatVirtualCameraServedAt(_virtualCameraDiagnostics.LastServedAt)}"
            };
        }

        if (_virtualCameraDiagnostics.Running)
        {
            return new DiagnosticsHealthCheckState
            {
                Status = DiagnosticsStatusKind.Warning,
                Detail = $"虚拟相机运行中，注册状态：{_virtualCameraDiagnostics.RegistrationText}"
            };
        }

        if (!_virtualCameraDiagnostics.Registered)
        {
            return new DiagnosticsHealthCheckState
            {
                Status = DiagnosticsStatusKind.Warning,
                Detail = "虚拟相机未注册"
            };
        }

        return new DiagnosticsHealthCheckState
        {
            Status = DiagnosticsStatusKind.Unknown,
            Detail = $"{statusText}：{hintText}"
        };
    }

    private DiagnosticsHealthCheckState BuildDiagnosticsAudioEndpointHealth()
    {
        if (AudioDeviceSwitch is null || MicrophoneSwitch is null || SpeakerSwitch is null)
        {
            return new DiagnosticsHealthCheckState
            {
                Status = DiagnosticsStatusKind.Unknown,
                Detail = "音频控件尚未初始化"
            };
        }

        if (!AudioDeviceSwitch.IsOn)
        {
            return new DiagnosticsHealthCheckState
            {
                Status = DiagnosticsStatusKind.Warning,
                Detail = "音频桥接未启用"
            };
        }

        var microphoneIntent = MicrophoneSwitch.IsOn;
        var speakerIntent = SpeakerSwitch.IsOn;
        if (!microphoneIntent && !speakerIntent)
        {
            return new DiagnosticsHealthCheckState
            {
                Status = DiagnosticsStatusKind.Warning,
                Detail = "麦克风和音响均未启用"
            };
        }

        var endpointIssue = BuildAudioEndpointIssueHint(microphoneIntent, speakerIntent);
        if (!string.IsNullOrWhiteSpace(endpointIssue))
        {
            return new DiagnosticsHealthCheckState
            {
                Status = HasHardAudioEndpointFailure(microphoneIntent, speakerIntent)
                    ? DiagnosticsStatusKind.Error
                    : DiagnosticsStatusKind.Warning,
                Detail = endpointIssue
            };
        }

        if ((microphoneIntent && _microphoneRenderEndpointDiagnostics.Health == AudioEndpointBindingHealth.Unknown)
            || (speakerIntent && _speakerCaptureEndpointDiagnostics.Health == AudioEndpointBindingHealth.Unknown))
        {
            return new DiagnosticsHealthCheckState
            {
                Status = DiagnosticsStatusKind.Unknown,
                Detail = "正在枚举音频端点"
            };
        }

        if (_audioOverrideStatus == AudioCapabilityStatus.Error
            || _microphoneRuntimeStatus == AudioCapabilityStatus.Error
            || _speakerRuntimeStatus == AudioCapabilityStatus.Error)
        {
            return new DiagnosticsHealthCheckState
            {
                Status = DiagnosticsStatusKind.Error,
                Detail = _lastAudioHint
            };
        }

        return new DiagnosticsHealthCheckState
        {
            Status = DiagnosticsStatusKind.Normal,
            Detail = BuildAudioEndpointReadyDetail(microphoneIntent, speakerIntent)
        };
    }

    private bool HasHardAudioEndpointFailure(bool microphoneIntent, bool speakerIntent)
    {
        return (microphoneIntent && IsHardAudioEndpointFailure(_microphoneRenderEndpointDiagnostics.Health))
            || (speakerIntent && IsHardAudioEndpointFailure(_speakerCaptureEndpointDiagnostics.Health));
    }

    private static bool IsHardAudioEndpointFailure(AudioEndpointBindingHealth health)
    {
        return health is AudioEndpointBindingHealth.Unsupported or AudioEndpointBindingHealth.EnumerationFailed;
    }

    private static string BuildAudioEndpointReadyDetail(bool microphoneIntent, bool speakerIntent)
    {
        if (microphoneIntent && speakerIntent)
        {
            return "电脑声音和 Android 麦克风端点已就绪";
        }

        return microphoneIntent
            ? "Android 麦克风写入端点已就绪"
            : "电脑声音 loopback 输出端点已就绪";
    }

    private static DiagnosticsHealthCheckState ToDiagnosticsHealthCheck(OverviewStepState state, string detail)
    {
        return new DiagnosticsHealthCheckState
        {
            Status = ToDiagnosticsStatus(state),
            Detail = detail
        };
    }

    private static DiagnosticsStatusKind ToDiagnosticsStatus(OverviewStepState state)
    {
        return state switch
        {
            OverviewStepState.Complete => DiagnosticsStatusKind.Normal,
            OverviewStepState.Warning => DiagnosticsStatusKind.Warning,
            OverviewStepState.Error => DiagnosticsStatusKind.Error,
            _ => DiagnosticsStatusKind.Unknown
        };
    }

    private static DiagnosticsStatusKind WorstDiagnosticsStatus(IEnumerable<DiagnosticsStatusKind> statuses)
    {
        if (statuses.Contains(DiagnosticsStatusKind.Error))
        {
            return DiagnosticsStatusKind.Error;
        }

        if (statuses.Contains(DiagnosticsStatusKind.Warning))
        {
            return DiagnosticsStatusKind.Warning;
        }

        if (statuses.Contains(DiagnosticsStatusKind.Unknown))
        {
            return DiagnosticsStatusKind.Unknown;
        }

        return DiagnosticsStatusKind.Normal;
    }

    private static string DiagnosticsOverallTitle(DiagnosticsStatusKind status)
    {
        return status switch
        {
            DiagnosticsStatusKind.Normal => "运行诊断正常",
            DiagnosticsStatusKind.Warning => "运行诊断需要检查",
            DiagnosticsStatusKind.Error => "运行诊断发现错误",
            _ => "等待诊断数据"
        };
    }

    private void SetOverviewConnectionChecklistStep(
        Border badge,
        FontIcon icon,
        TextBlock detailText,
        TextBlock statusText,
        string detail,
        OverviewStepState state)
    {
        detailText.Text = detail;

        var (status, foreground) = state switch
        {
            OverviewStepState.Complete => ("正常", _successBrush),
            OverviewStepState.Active => ("进行中", _overviewPrimaryBrush),
            OverviewStepState.Warning => ("警告", _warningBrush),
            OverviewStepState.Error => ("错误", _dangerBrush),
            _ => ("待处理", _overviewNeutralBrush)
        };
        statusText.Text = status;
        statusText.Foreground = foreground;

        switch (state)
        {
            case OverviewStepState.Complete:
                badge.Background = _successBrush;
                badge.BorderBrush = _successBrush;
                badge.BorderThickness = new Thickness(0);
                icon.Glyph = "\uE73E";
                icon.Foreground = new SolidColorBrush(Colors.White);
                detailText.Foreground = _secondaryBrush;
                break;
            case OverviewStepState.Active:
                badge.Background = _overviewPrimaryBrush;
                badge.BorderBrush = _overviewPrimaryBrush;
                badge.BorderThickness = new Thickness(0);
                icon.Glyph = "\uE768";
                icon.Foreground = new SolidColorBrush(Colors.White);
                detailText.Foreground = _overviewPrimaryBrush;
                break;
            case OverviewStepState.Warning:
                badge.Background = _overviewWarningBackgroundBrush;
                badge.BorderBrush = _overviewWarningBorderBrush;
                badge.BorderThickness = new Thickness(1);
                icon.Glyph = "\uE7BA";
                icon.Foreground = _warningBrush;
                detailText.Foreground = _warningBrush;
                break;
            case OverviewStepState.Error:
                badge.Background = _overviewErrorBackgroundBrush;
                badge.BorderBrush = _overviewErrorBorderBrush;
                badge.BorderThickness = new Thickness(1);
                icon.Glyph = "\uE783";
                icon.Foreground = _dangerBrush;
                detailText.Foreground = _dangerBrush;
                break;
            default:
                badge.Background = _overviewNeutralBackgroundBrush;
                badge.BorderBrush = _overviewNeutralBorderBrush;
                badge.BorderThickness = new Thickness(1);
                icon.Glyph = "\uE711";
                icon.Foreground = _overviewNeutralBrush;
                detailText.Foreground = _secondaryBrush;
                break;
        }
    }

    private (TextBlock TextBlock, XamlShape Dot, NumberBox NumberBox, string Name)[] OverviewPortStatusControls()
    {
        return new (TextBlock TextBlock, XamlShape Dot, NumberBox NumberBox, string Name)[]
        {
            (OverviewControlPortStatusText, OverviewControlPortStatusDot, OverviewControlPortBox, "控制"),
            (OverviewVideoPortStatusText, OverviewVideoPortStatusDot, OverviewVideoPortBox, "视频"),
            (OverviewAudioPortStatusText, OverviewAudioPortStatusDot, OverviewAudioPortBox, "音频"),
            (OverviewCameraPortStatusText, OverviewCameraPortStatusDot, OverviewCameraPortBox, "摄像头")
        };
    }

    private void UpdateOverviewConnectionPortStatus()
    {
        var activePorts = GetActiveTcpListenerPorts();
        var hostRunning = _hostProcess is { HasExited: false };
        var portValues = OverviewPortStatusControls()
            .Select(port => (port, Valid: TryReadPort(port.NumberBox, out var value), Value: value))
            .ToArray();
        var duplicatePorts = portValues
            .Where(item => item.Valid)
            .GroupBy(item => item.Value)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();

        foreach (var (port, valid, value) in portValues)
        {
            string statusText;
            Brush statusBrush;

            if (!valid)
            {
                statusText = "无效";
                statusBrush = _dangerBrush;
            }
            else if (duplicatePorts.Contains(value))
            {
                statusText = "重复";
                statusBrush = _dangerBrush;
            }
            else if (activePorts.Contains(value))
            {
                statusText = hostRunning ? "使用中" : "被占用";
                statusBrush = hostRunning ? _successBrush : _dangerBrush;
            }
            else
            {
                statusText = "可用";
                statusBrush = _successBrush;
            }

            port.TextBlock.Text = statusText;
            port.TextBlock.Foreground = statusBrush;
            port.Dot.Fill = statusBrush;
        }
    }

    private (OverviewStepState State, string Detail) BuildOverviewConnectionPortSummary()
    {
        var activePorts = GetActiveTcpListenerPorts();
        var hostRunning = _hostProcess is { HasExited: false };
        var portValues = OverviewPortStatusControls()
            .Select(port => (port.Name, Valid: TryReadPort(port.NumberBox, out var value), Value: value))
            .ToArray();

        var invalidPorts = portValues.Where(port => !port.Valid).Select(port => port.Name).ToArray();
        if (invalidPorts.Length > 0)
        {
            return (OverviewStepState.Error, $"端口无效：{string.Join("、", invalidPorts)}");
        }

        var duplicateGroups = portValues
            .GroupBy(port => port.Value)
            .Where(group => group.Count() > 1)
            .ToArray();
        if (duplicateGroups.Length > 0)
        {
            var duplicateText = string.Join("、", duplicateGroups.Select(group => group.Key.ToString(CultureInfo.InvariantCulture)));
            return (OverviewStepState.Error, $"端口不能重复：{duplicateText}");
        }

        var occupied = portValues
            .Where(port => activePorts.Contains(port.Value))
            .ToArray();
        if (occupied.Length > 0 && !hostRunning)
        {
            var occupiedText = string.Join("、", occupied.Select(port => $"{port.Name} {port.Value}"));
            return (OverviewStepState.Error, $"端口被占用：{occupiedText}");
        }

        var ports = string.Join("/", portValues.Select(port => port.Value.ToString(CultureInfo.InvariantCulture)));
        return hostRunning && occupied.Length > 0
            ? (OverviewStepState.Complete, $"主机正在使用端口 {ports}")
            : (OverviewStepState.Complete, $"端口可用：{ports}");
    }

    private static HashSet<int> GetActiveTcpListenerPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(endpoint => endpoint.Port)
                .ToHashSet();
        }
        catch
        {
            return new HashSet<int>();
        }
    }

    private static bool TryReadPort(NumberBox numberBox, out int port)
    {
        port = 0;
        if (double.IsNaN(numberBox.Value))
        {
            return false;
        }

        port = (int)numberBox.Value;
        return port is >= 1 and <= 65535;
    }

    private void UpdateOverviewFooterMachineInfo()
    {
        if (!StaticOverviewUi)
        {
            return;
        }

        var footer = BuildOverviewFooterSnapshot();
        OverviewFooterHostText.Text = footer.HostText;
        OverviewFooterOsText.Text = footer.OsText;
        OverviewFooterNetworkText.Text = footer.NetworkText;
        OverviewFooterNetworkStatusDot.Fill = footer.NetworkBrush;
    }

    private (string HostText, string OsText, string NetworkText, Brush NetworkBrush) BuildOverviewFooterSnapshot()
    {
        var hostText = $"本机：{Environment.MachineName}";
        var osText = RuntimeInformation.OSDescription;
        var address = TryGetPrimaryNetworkAddress(out var interfaceName);
        if (address is null)
        {
            return (
                hostText,
                osText,
                string.IsNullOrWhiteSpace(interfaceName) ? "网络：暂无数据" : $"{interfaceName}：暂无数据",
                _overviewMutedBrush);
        }

        return (
            hostText,
            osText,
            string.IsNullOrWhiteSpace(interfaceName) ? address.ToString() : $"{interfaceName}：{address}",
            _successBrush);
    }

    private static IPAddress? TryGetPrimaryNetworkAddress(out string interfaceName)
    {
        interfaceName = string.Empty;
        try
        {
            var networkInterface = FindPrimaryNetworkInterface();
            if (networkInterface is null)
            {
                return null;
            }

            interfaceName = networkInterface.Name;
            return networkInterface
                .GetIPProperties()
                .UnicastAddresses
                .Select(address => address.Address)
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
        }
        catch
        {
            return null;
        }
    }

    private (string Text, Brush Brush, Brush DotBrush, Brush Background, Brush Border) BuildAdbDeviceStatusView(string state)
    {
        if (state.Equals("device", StringComparison.OrdinalIgnoreCase))
        {
            return ("已授权", _successBrush, _successBrush, _overviewReadyBackgroundBrush, _overviewReadyBorderBrush);
        }

        if (state.Equals("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return ("未授权", _warningBrush, _warningBrush, _overviewWarningBackgroundBrush, _overviewWarningBorderBrush);
        }

        if (state.Equals("offline", StringComparison.OrdinalIgnoreCase))
        {
            return ("离线", _dangerBrush, _dangerBrush, _overviewErrorBackgroundBrush, _overviewErrorBorderBrush);
        }

        return (state, _overviewNeutralBrush, _overviewMutedBrush, _overviewNeutralBackgroundBrush, _overviewNeutralBorderBrush);
    }

    private static string FormatAdbDeviceState(string state)
    {
        if (state.Equals("device", StringComparison.OrdinalIgnoreCase))
        {
            return "已授权";
        }

        if (state.Equals("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "未授权";
        }

        if (state.Equals("offline", StringComparison.OrdinalIgnoreCase))
        {
            return "离线";
        }

        return state;
    }

    private static string FormatAdbTransport(AdbDeviceRow row)
    {
        if (IsAdbNetworkSerial(row.Serial))
        {
            return "Wi-Fi/网络";
        }

        return IsUsbAdbRow(row) ? "USB" : "ADB";
    }

    private static bool IsUsbAdbRow(AdbDeviceRow row)
    {
        return row.RawLine.Contains("usb:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAdbNetworkSerial(string serial)
    {
        return serial.Contains(':', StringComparison.Ordinal);
    }

    private string BuildAdbDeviceDetailText(AdbDeviceRow row)
    {
        var model = TryGetAdbDetail(row.RawLine, "model")?.Replace('_', ' ');
        var product = TryGetAdbDetail(row.RawLine, "product")?.Replace('_', ' ');
        var androidVersion = "Android 版本：暂无数据";
        var ip = IsAdbNetworkSerial(row.Serial)
            ? $"IP：{row.Serial.Split(':', 2)[0]}"
            : "IP：暂无数据";
        var deviceInfo = FirstNonEmpty(model, product, "设备信息暂无数据");
        return $"{deviceInfo} · 序列号：{row.Serial} · {androidVersion} · {ip}";
    }

    private string FormatLastAdbRefreshText()
    {
        if (_lastAdbRefreshCompletedAt is not { } refreshAt)
        {
            return "暂无数据";
        }

        var elapsed = DateTimeOffset.Now - refreshAt;
        if (elapsed.TotalSeconds < 10)
        {
            return "刚刚";
        }

        if (elapsed.TotalMinutes < 1)
        {
            return $"{Math.Max(1, (int)elapsed.TotalSeconds)} 秒前";
        }

        if (elapsed.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)} 分钟前";
        }

        return refreshAt.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    }

    private void UpdateOverviewActionMenuItems()
    {
        if (!StaticOverviewUi)
        {
            return;
        }

        var canStart = CanStartOverviewHost();
        var running = _overviewHostServiceState == OverviewHostServiceState.Running;
        var busy = _overviewRefreshInProgress || _adbRefreshInProgress;

        OverviewActionStartHostMenuItem.IsEnabled = canStart && !busy;
        OverviewActionStopHostMenuItem.IsEnabled = running;
        OverviewActionRefreshDevicesMenuItem.IsEnabled = !busy;
        OverviewActionRestartAdbMenuItem.IsEnabled = canStart && !busy;
        OverviewActionRepairEndpointsMenuItem.IsEnabled = !_overviewRefreshInProgress;
        OverviewActionOpenDisplaySettingsMenuItem.IsEnabled = true;
        OverviewActionCopyDiagnosticsMenuItem.IsEnabled = !_overviewRefreshInProgress;
        OverviewActionOpenLogsMenuItem.IsEnabled = true;
        OverviewRepairEndpointsButton.IsEnabled = !_overviewRefreshInProgress;
    }

    private void UpdateOverviewRefreshState()
    {
        if (!StaticOverviewUi)
        {
            return;
        }

        var refreshing = _overviewRefreshInProgress || _adbRefreshInProgress;
        OverviewRefreshAdbDevicesButton.IsEnabled = !refreshing;
        OverviewRefreshButtonText.Text = refreshing ? "刷新中" : "刷新";
        OverviewRefreshIcon.Foreground = refreshing ? _overviewNeutralBrush : new SolidColorBrush(ColorHelper.FromArgb(255, 17, 24, 39));
        UpdateOverviewActionMenuItems();
    }

    private void SetOverviewHostState(OverviewHostServiceState state, string? detail = null)
    {
        _overviewHostServiceState = state;
        _overviewHostDetailText = detail ?? state switch
        {
            OverviewHostServiceState.NotStarted => "等待启动",
            OverviewHostServiceState.Starting => "正在启动主机",
            OverviewHostServiceState.Running => "SideDock.Host.exe 正在运行",
            OverviewHostServiceState.Stopped => "主机已停止",
            OverviewHostServiceState.Error => "请查看错误提示后重试",
            _ => "等待启动"
        };

        if (!StaticOverviewUi)
        {
            return;
        }

        var (statusText, statusBrush, dotBrush) = state switch
        {
            OverviewHostServiceState.NotStarted => ("未启动", _dangerBrush, _overviewMutedBrush),
            OverviewHostServiceState.Starting => ("启动中", _overviewPrimaryBrush, _overviewPrimaryBrush),
            OverviewHostServiceState.Running => ("运行中", _successBrush, _successBrush),
            OverviewHostServiceState.Stopped => ("已停止", _secondaryBrush, _overviewMutedBrush),
            OverviewHostServiceState.Error => ("错误", _dangerBrush, _dangerBrush),
            _ => ("未启动", _dangerBrush, _overviewMutedBrush)
        };

        OverviewHostStatusText.Text = statusText;
        OverviewHostStatusText.Foreground = statusBrush;
        OverviewHostStatusSubtext.Text = _overviewHostDetailText;
        OverviewHostStatusDot.Fill = dotBrush;
        OverviewSidebarHostStatusText.Text = statusText;
        OverviewSidebarHostStatusText.Foreground = statusBrush;
        OverviewSidebarHostStatusDot.Fill = dotBrush;

        UpdateOverviewActionButtons();
        UpdateOverviewConnectionGuide();
        UpdateOverviewEnvironmentBanner();
    }

    private void UpdateOverviewActionButtons()
    {
        if (!StaticOverviewUi)
        {
            return;
        }

        var canStart = CanStartOverviewHost();
        var running = _overviewHostServiceState == OverviewHostServiceState.Running;

        OverviewStartHostButton.IsEnabled = canStart && !_overviewRefreshInProgress;
        OverviewStartHostButtonText.Text = _overviewHostServiceState == OverviewHostServiceState.Starting
            ? "启动中"
            : "启动主机";
        OverviewDisconnectButton.IsEnabled = running;
        UpdateOverviewRefreshState();
        UpdateOverviewConnectionPage();
    }

    private void UpdateOverviewAndroidDeviceState()
    {
        if (!StaticOverviewUi)
        {
            return;
        }

        var (stateText, detailText, stateBrush) = BuildOverviewAndroidState();
        OverviewAndroidStatusText.Text = stateText;
        OverviewAndroidStatusText.Foreground = stateBrush;
        OverviewAndroidDetailText.Text = detailText;
        OverviewAndroidBatteryPanel.Visibility = Visibility.Collapsed;

        UpdateOverviewConnectionGuide();
        UpdateOverviewEnvironmentBanner();
    }

    private (string StateText, string DetailText, Brush StateBrush) BuildOverviewAndroidState()
    {
        var selectedSerial = SelectedAdbSerial();
        if (!string.IsNullOrWhiteSpace(selectedSerial))
        {
            var selectedRow = SelectedAdbDeviceRow();
            if (selectedRow is null)
            {
                return ("未检测到", $"{selectedSerial} 不在当前设备列表中", _warningBrush);
            }

            if (selectedRow.State.Equals("device", StringComparison.OrdinalIgnoreCase))
            {
                return ("已选择设备", selectedSerial, _successBrush);
            }

            return selectedRow.State.Equals("offline", StringComparison.OrdinalIgnoreCase)
                ? ("设备离线", selectedSerial, _dangerBrush)
                : ("设备未就绪", $"{selectedSerial}: {selectedRow.State}", _warningBrush);
        }

        var authorizedRows = _lastAdbDeviceRows
            .Where(row => row.State.Equals("device", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (authorizedRows.Length > 1)
        {
            return ("需选择设备", $"检测到 {authorizedRows.Length} 台已授权设备", _warningBrush);
        }

        var unauthorizedRow = _lastAdbDeviceRows.FirstOrDefault(row =>
            row.State.Equals("unauthorized", StringComparison.OrdinalIgnoreCase));
        if (unauthorizedRow is not null)
        {
            return ("未授权", unauthorizedRow.Serial, _warningBrush);
        }

        var firstUnavailable = _lastAdbDeviceRows.FirstOrDefault();
        if (firstUnavailable is not null)
        {
            return ("不可用", $"{firstUnavailable.Serial}: {firstUnavailable.State}", _warningBrush);
        }

        return ("未检测到", "请连接 USB 并开启 USB 调试", _overviewNeutralBrush);
    }

    private void UpdateOverviewConnectionGuide()
    {
        if (!StaticOverviewUi)
        {
            return;
        }

        var (deviceStepState, deviceDetail) = BuildOverviewDeviceStepState();
        SetOverviewWizardStep(
            OverviewWizardDeviceBadge,
            OverviewWizardDeviceNumberText,
            OverviewWizardDeviceDetailText,
            OverviewWizardDeviceIcon,
            "1",
            deviceDetail,
            deviceStepState);

        var (hostStepState, hostDetail) = _overviewHostServiceState switch
        {
            OverviewHostServiceState.Starting => (OverviewStepState.Active, _overviewHostDetailText),
            OverviewHostServiceState.Running => (OverviewStepState.Complete, "主机服务运行中"),
            OverviewHostServiceState.Error => (OverviewStepState.Error, _overviewHostDetailText),
            OverviewHostServiceState.Stopped => (OverviewStepState.Waiting, "主机已停止，可重新启动"),
            _ => (OverviewStepState.Waiting, "等待启动主机")
        };
        SetOverviewWizardStep(
            OverviewWizardHostBadge,
            OverviewWizardHostNumberText,
            OverviewWizardHostDetailText,
            OverviewWizardHostIcon,
            "3",
            hostDetail,
            hostStepState);

        var selectedSerial = SelectedAdbSerial();
        var (linkStepState, linkDetail) = _overviewHostServiceState switch
        {
            OverviewHostServiceState.Running when !string.IsNullOrWhiteSpace(selectedSerial)
                => (OverviewStepState.Complete, $"链路已建立：{selectedSerial}"),
            OverviewHostServiceState.Running
                => (OverviewStepState.Warning, "主机运行中，等待设备选择"),
            OverviewHostServiceState.Starting
                => (OverviewStepState.Active, "正在建立连接链路"),
            OverviewHostServiceState.Error
                => (OverviewStepState.Error, "链路未建立"),
            _ => (OverviewStepState.Waiting, "等待主机运行")
        };
        SetOverviewWizardStep(
            OverviewWizardLinkBadge,
            OverviewWizardLinkNumberText,
            OverviewWizardLinkDetailText,
            OverviewWizardLinkIcon,
            "4",
            linkDetail,
            linkStepState);
    }

    private (OverviewStepState State, string Detail) BuildOverviewDeviceStepState()
    {
        var selectedSerial = SelectedAdbSerial();
        if (!string.IsNullOrWhiteSpace(selectedSerial))
        {
            var selectedRow = SelectedAdbDeviceRow();
            if (selectedRow is null)
            {
                return (OverviewStepState.Warning, $"未检测到已选择设备：{selectedSerial}");
            }

            if (selectedRow.State.Equals("device", StringComparison.OrdinalIgnoreCase))
            {
                return (OverviewStepState.Complete, $"已选择：{selectedSerial}");
            }

            return selectedRow.State.Equals("offline", StringComparison.OrdinalIgnoreCase)
                ? (OverviewStepState.Error, $"设备离线：{selectedSerial}")
                : (OverviewStepState.Warning, $"设备状态为 {selectedRow.State}：{selectedSerial}");
        }

        var authorizedCount = _lastAdbDeviceRows.Count(row =>
            row.State.Equals("device", StringComparison.OrdinalIgnoreCase));
        if (authorizedCount > 1)
        {
            return (OverviewStepState.Warning, $"检测到 {authorizedCount} 台设备，请选择一台");
        }

        var unauthorizedRow = _lastAdbDeviceRows.FirstOrDefault(row =>
            row.State.Equals("unauthorized", StringComparison.OrdinalIgnoreCase));
        if (unauthorizedRow is not null)
        {
            return (OverviewStepState.Warning, $"未授权：{unauthorizedRow.Serial}");
        }

        return _lastAdbDeviceRows.Count > 0
            ? (OverviewStepState.Warning, "未检测到可用授权设备")
            : (OverviewStepState.Waiting, "等待刷新设备");
    }

    private void SetOverviewWizardStep(
        Border badge,
        TextBlock numberText,
        TextBlock detailText,
        FontIcon icon,
        string number,
        string detail,
        OverviewStepState state)
    {
        detailText.Text = detail;
        numberText.Text = number;

        switch (state)
        {
            case OverviewStepState.Complete:
                badge.Background = _successBrush;
                badge.BorderBrush = _successBrush;
                badge.BorderThickness = new Thickness(0);
                numberText.Foreground = new SolidColorBrush(Colors.White);
                icon.Glyph = "\uE73E";
                icon.Foreground = _successBrush;
                detailText.Foreground = _secondaryBrush;
                break;
            case OverviewStepState.Active:
                badge.Background = _overviewPrimaryBrush;
                badge.BorderBrush = _overviewPrimaryBrush;
                badge.BorderThickness = new Thickness(0);
                numberText.Foreground = new SolidColorBrush(Colors.White);
                icon.Glyph = "\uE768";
                icon.Foreground = _overviewPrimaryBrush;
                detailText.Foreground = _overviewPrimaryBrush;
                break;
            case OverviewStepState.Warning:
                badge.Background = _overviewWarningBackgroundBrush;
                badge.BorderBrush = _overviewWarningBorderBrush;
                badge.BorderThickness = new Thickness(1);
                numberText.Foreground = _warningBrush;
                icon.Glyph = "\uE7BA";
                icon.Foreground = _warningBrush;
                detailText.Foreground = _warningBrush;
                break;
            case OverviewStepState.Error:
                badge.Background = _overviewErrorBackgroundBrush;
                badge.BorderBrush = _overviewErrorBorderBrush;
                badge.BorderThickness = new Thickness(1);
                numberText.Foreground = _dangerBrush;
                icon.Glyph = "\uE783";
                icon.Foreground = _dangerBrush;
                detailText.Foreground = _dangerBrush;
                break;
            default:
                badge.Background = _overviewNeutralBackgroundBrush;
                badge.BorderBrush = _overviewNeutralBorderBrush;
                badge.BorderThickness = new Thickness(1);
                numberText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 17, 24, 39));
                icon.Glyph = "\uE711";
                icon.Foreground = _overviewNeutralBrush;
                detailText.Foreground = _secondaryBrush;
                break;
        }
    }

    private void UpdateOverviewEnvironmentBanner()
    {
        if (!StaticOverviewUi)
        {
            return;
        }

        if (_overviewRefreshInProgress)
        {
            SetOverviewEnvironmentBanner(
                "正在刷新环境状态",
                "正在统一刷新 ADB 设备、主机状态、虚拟显示器、摄像头、音频和诊断信息。",
                _overviewPrimaryBrush,
                _overviewNeutralBackgroundBrush,
                _overviewNeutralBorderBrush,
                "\uE72C",
                _overviewPrimaryBrush,
                OverviewEnvironmentBannerSeverity.Neutral);
            return;
        }

        var selectedSerial = SelectedAdbSerial();
        var unauthorizedRow = _lastAdbDeviceRows.FirstOrDefault(row =>
            row.State.Equals("unauthorized", StringComparison.OrdinalIgnoreCase));

        if (_overviewHostServiceState == OverviewHostServiceState.Starting)
        {
            SetOverviewEnvironmentBanner(
                "正在启动主机",
                _lastAdbStatusText,
                _overviewPrimaryBrush,
                _overviewNeutralBackgroundBrush,
                _overviewNeutralBorderBrush,
                "\uE768",
                _overviewPrimaryBrush,
                OverviewEnvironmentBannerSeverity.Neutral);
            return;
        }

        if (_overviewHostServiceState == OverviewHostServiceState.Error)
        {
            SetOverviewEnvironmentBanner(
                "主机服务错误",
                _overviewHostDetailText,
                _dangerBrush,
                _overviewErrorBackgroundBrush,
                _overviewErrorBorderBrush,
                "\uE783",
                _dangerBrush,
                OverviewEnvironmentBannerSeverity.Error);
            return;
        }

        if (unauthorizedRow is not null && string.IsNullOrWhiteSpace(selectedSerial))
        {
            SetOverviewEnvironmentBanner(
                "Android 设备未授权",
                $"请在设备 {unauthorizedRow.Serial} 上允许 USB 调试后刷新。",
                _warningBrush,
                _overviewWarningBackgroundBrush,
                _overviewWarningBorderBrush,
                "\uE7BA",
                _warningBrush,
                OverviewEnvironmentBannerSeverity.Warning);
            return;
        }

        if (!string.IsNullOrWhiteSpace(selectedSerial)
            && TryBuildOverviewEnvironmentIssue(out var issueTitle, out var issueDetail, out var issueSeverity))
        {
            SetOverviewEnvironmentBanner(
                issueTitle,
                issueDetail,
                issueSeverity == OverviewEnvironmentBannerSeverity.Error ? _dangerBrush : _warningBrush,
                issueSeverity == OverviewEnvironmentBannerSeverity.Error ? _overviewErrorBackgroundBrush : _overviewWarningBackgroundBrush,
                issueSeverity == OverviewEnvironmentBannerSeverity.Error ? _overviewErrorBorderBrush : _overviewWarningBorderBrush,
                issueSeverity == OverviewEnvironmentBannerSeverity.Error ? "\uE783" : "\uE7BA",
                issueSeverity == OverviewEnvironmentBannerSeverity.Error ? _dangerBrush : _warningBrush,
                issueSeverity);
            return;
        }

        if (_overviewHostServiceState == OverviewHostServiceState.Running
            && !string.IsNullOrWhiteSpace(selectedSerial))
        {
            SetOverviewEnvironmentBanner(
                "连接环境已就绪",
                $"主机运行中，Android 设备 {selectedSerial} 已选择。",
                _successBrush,
                _overviewReadyBackgroundBrush,
                _overviewReadyBorderBrush,
                "\uE73E",
                _successBrush,
                OverviewEnvironmentBannerSeverity.Ready);
            return;
        }

        if (!string.IsNullOrWhiteSpace(selectedSerial))
        {
            SetOverviewEnvironmentBanner(
                "Android 设备已就绪",
                $"已选择 {selectedSerial}，可以启动主机服务。",
                _overviewPrimaryBrush,
                _overviewNeutralBackgroundBrush,
                _overviewNeutralBorderBrush,
                "\uE8EA",
                _overviewPrimaryBrush,
                OverviewEnvironmentBannerSeverity.Neutral);
            return;
        }

        SetOverviewEnvironmentBanner(
            "等待 Android 设备",
            _lastAdbStatusText,
            _overviewNeutralBrush,
            _overviewNeutralBackgroundBrush,
            _overviewNeutralBorderBrush,
            "\uE711",
            _overviewNeutralBrush,
            OverviewEnvironmentBannerSeverity.Neutral);
    }

    private bool TryBuildOverviewEnvironmentIssue(
        out string title,
        out string detail,
        out OverviewEnvironmentBannerSeverity severity)
    {
        var virtualDisplayRunning = IsVirtualDisplayToolRunning();
        var virtualDisplayState = DetermineVirtualDisplayOverviewState(
            virtualDisplayRunning,
            out var driverInstalled,
            out var toolAvailable);
        var (displayStatus, displayDetail, _) = BuildVirtualDisplayStatusView(
            virtualDisplayState,
            driverInstalled,
            toolAvailable);

        if (virtualDisplayState == VirtualDisplayOverviewState.Error)
        {
            title = "虚拟显示器异常";
            detail = displayDetail;
            severity = OverviewEnvironmentBannerSeverity.Error;
            return true;
        }

        if (virtualDisplayState == VirtualDisplayOverviewState.DriverMissing)
        {
            title = displayStatus;
            detail = displayDetail;
            severity = OverviewEnvironmentBannerSeverity.Warning;
            return true;
        }

        if (_overviewCameraRequestedEnabled && HasCameraError())
        {
            title = "摄像头状态异常";
            detail = FirstNonEmpty(_cameraDiagnostics.LastError, _virtualCameraDiagnostics.LastError, "摄像头或虚拟摄像头需要检查。");
            severity = _hostProcess is { HasExited: false }
                ? OverviewEnvironmentBannerSeverity.Error
                : OverviewEnvironmentBannerSeverity.Warning;
            return true;
        }

        var audioEnabled = AudioDeviceSwitch.IsOn;
        var audioIssue = audioEnabled
            ? BuildAudioEndpointIssueHint(MicrophoneSwitch.IsOn, SpeakerSwitch.IsOn)
            : null;
        if (!string.IsNullOrWhiteSpace(audioIssue))
        {
            title = "音频端点需要设置";
            detail = audioIssue;
            severity = OverviewEnvironmentBannerSeverity.Warning;
            return true;
        }

        if (_audioOverrideStatus == AudioCapabilityStatus.Error
            || _microphoneRuntimeStatus == AudioCapabilityStatus.Error
            || _speakerRuntimeStatus == AudioCapabilityStatus.Error)
        {
            title = "音频桥接异常";
            detail = _lastAudioHint;
            severity = OverviewEnvironmentBannerSeverity.Error;
            return true;
        }

        title = string.Empty;
        detail = string.Empty;
        severity = OverviewEnvironmentBannerSeverity.Neutral;
        return false;
    }

    private void SetOverviewEnvironmentBanner(
        string title,
        string detail,
        Brush foreground,
        Brush background,
        Brush border,
        string iconGlyph,
        Brush iconBackground,
        OverviewEnvironmentBannerSeverity severity = OverviewEnvironmentBannerSeverity.Neutral)
    {
        if (severity is OverviewEnvironmentBannerSeverity.Warning or OverviewEnvironmentBannerSeverity.Error)
        {
            _overviewEnvironmentBannerDismissed = false;
        }

        OverviewEnvironmentStatusBanner.Visibility = _overviewEnvironmentBannerDismissed
            ? Visibility.Collapsed
            : Visibility.Visible;
        OverviewEnvironmentStatusBanner.Background = background;
        OverviewEnvironmentStatusBanner.BorderBrush = border;
        OverviewEnvironmentStatusIconBackground.Background = iconBackground;
        OverviewEnvironmentStatusIcon.Glyph = iconGlyph;
        OverviewEnvironmentStatusTitleText.Text = title;
        OverviewEnvironmentStatusTitleText.Foreground = foreground;
        OverviewEnvironmentStatusDetailText.Text = detail;
        OverviewEnvironmentStatusDetailText.Foreground = foreground;

        OverviewConnectionEnvironmentStatusBanner.Visibility = _overviewEnvironmentBannerDismissed
            ? Visibility.Collapsed
            : Visibility.Visible;
        OverviewConnectionEnvironmentStatusBanner.Background = background;
        OverviewConnectionEnvironmentStatusBanner.BorderBrush = border;
        OverviewConnectionEnvironmentStatusIconBackground.Background = iconBackground;
        OverviewConnectionEnvironmentStatusIcon.Glyph = iconGlyph;
        OverviewConnectionEnvironmentStatusTitleText.Text = title;
        OverviewConnectionEnvironmentStatusTitleText.Foreground = foreground;
        OverviewConnectionEnvironmentStatusDetailText.Text = detail;
        OverviewConnectionEnvironmentStatusDetailText.Foreground = foreground;
    }

    private void RegisterCardWheelScrolling()
    {
        RegisterCardWheelScrolling(ConnectionSessionScrollViewer);
        RegisterCardWheelScrolling(DisplayCardScrollViewer);
        RegisterCardWheelScrolling(AudioCardScrollViewer);
        RegisterCardWheelScrolling(RuntimeCardScrollViewer);
    }

    private static void RegisterCardWheelScrolling(ScrollViewer scrollViewer)
    {
        scrollViewer.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnCardPointerWheelChanged),
            handledEventsToo: true);
    }

    private static void OnCardPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var delta = args.GetCurrentPoint(scrollViewer).Properties.MouseWheelDelta;
        if (delta == 0 || scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var nextOffset = Math.Clamp(
            scrollViewer.VerticalOffset - delta,
            0,
            scrollViewer.ScrollableHeight);

        if (Math.Abs(nextOffset - scrollViewer.VerticalOffset) < 0.1)
        {
            return;
        }

        scrollViewer.ChangeView(null, nextOffset, null, disableAnimation: true);
        args.Handled = true;
    }

    private void InitializeTrayIcon()
    {
        _trayIconHandle = LoadTrayIconHandle();
        if (_trayIconHandle == IntPtr.Zero)
        {
            return;
        }

        _subclassProc = TrayWindowSubclassProc;
        _windowSubclassed = SetWindowSubclass(_windowHandle, _subclassProc, WindowSubclassId, UIntPtr.Zero);
        if (!_windowSubclassed)
        {
            return;
        }

        var data = CreateNotifyIconData(NifMessage | NifIcon | NifTip);
        _trayIconAdded = Shell_NotifyIcon(NimAdd, ref data);
        if (_trayIconAdded)
        {
            data.uTimeoutOrVersion = NotifyIconVersion4;
            Shell_NotifyIcon(NimSetVersion, ref data);
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
        {
            return;
        }

        if (!_trayIconAdded)
        {
            return;
        }

        args.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        ShowWindow(_windowHandle, SwHide);
    }

    private void RestoreFromTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ShowWindow(_windowHandle, SwShow);
            ShowWindow(_windowHandle, SwRestore);
            Activate();
            SetForegroundWindow(_windowHandle);
        });
    }

    private void ExitFromTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _exitRequested = true;
            DisposeTrayIcon();
            Close();
        });
    }

    private void DisposeTrayIcon()
    {
        if (_trayIconAdded)
        {
            var data = CreateNotifyIconData(0);
            Shell_NotifyIcon(NimDelete, ref data);
            _trayIconAdded = false;
        }

        if (_windowSubclassed && _subclassProc is not null)
        {
            RemoveWindowSubclass(_windowHandle, _subclassProc, WindowSubclassId);
            _windowSubclassed = false;
            _subclassProc = null;
        }

        if (_ownsTrayIconHandle && _trayIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_trayIconHandle);
        }

        _trayIconHandle = IntPtr.Zero;
        _ownsTrayIconHandle = false;
    }

    private IntPtr TrayWindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        if (message == WmTrayIcon)
        {
            HandleTrayIconMessage(unchecked((uint)((long)lParam & 0xffff)));
            return IntPtr.Zero;
        }

        if (message == WmDpiChanged)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateDpiDiagnosticTitle();
                ResizeWindowForCurrentDpi();
            });
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void ResizeWindowForCurrentDpi()
    {
        var scale = CurrentDpiScale();
        var width = (int)Math.Round(DesiredWindowWidth * scale);
        var height = (int)Math.Round(DesiredWindowHeight * scale);

        var displayArea = DisplayArea.GetFromWindowId(
            Win32Interop.GetWindowIdFromWindow(_windowHandle),
            DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        width = Math.Min(width, Math.Max(DesiredWindowWidth, workArea.Width));
        height = Math.Min(height, Math.Max(DesiredWindowHeight, workArea.Height));

        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
    }

    private double CurrentDpiScale()
    {
        var dpi = GetDpiForWindow(_windowHandle);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    private void UpdateDpiDiagnosticTitle()
    {
        var dpi = GetDpiForWindow(_windowHandle);
        if (dpi == 0)
        {
            Title = "SideDock Host - DPI unknown";
            return;
        }

        var scalePercent = (int)Math.Round(dpi / 96.0 * 100);
        Title = $"SideDock Host - DPI {dpi} ({scalePercent}%)";
    }

    private void HandleTrayIconMessage(uint message)
    {
        switch (message)
        {
            case WmLButtonUp:
            case WmLButtonDoubleClick:
            case NinSelect:
            case NinKeySelect:
                RestoreFromTray();
                break;
            case WmRButtonUp:
            case WmContextMenu:
                ShowTrayMenu();
                break;
        }
    }

    private void ShowTrayMenu()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                return;
            }

            try
            {
                AppendMenu(menu, MfString, new UIntPtr(TrayMenuOpen), "打开 SideDock");
                AppendMenu(menu, MfSeparator, UIntPtr.Zero, string.Empty);
                AppendMenu(menu, MfString, new UIntPtr(TrayMenuExit), "退出");

                if (!GetCursorPos(out var point))
                {
                    return;
                }

                SetForegroundWindow(_windowHandle);
                var command = TrackPopupMenu(
                    menu,
                    TpmRightButton | TpmNonotify | TpmReturnCmd,
                    point.X,
                    point.Y,
                    0,
                    _windowHandle,
                    IntPtr.Zero);
                PostMessage(_windowHandle, WmNull, UIntPtr.Zero, IntPtr.Zero);

                switch (command)
                {
                    case TrayMenuOpen:
                        RestoreFromTray();
                        break;
                    case TrayMenuExit:
                        ExitFromTray();
                        break;
                }
            }
            finally
            {
                DestroyMenu(menu);
            }
        });
    }

    private NotifyIconData CreateNotifyIconData(uint flags)
    {
        return new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _windowHandle,
            uID = TrayIconId,
            uFlags = flags,
            uCallbackMessage = WmTrayIcon,
            hIcon = _trayIconHandle,
            szTip = "SideDock",
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private IntPtr LoadTrayIconHandle()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            var smallIcons = new IntPtr[1];
            var count = ExtractIconEx(processPath, 0, null, smallIcons, 1);
            if (count > 0 && smallIcons[0] != IntPtr.Zero)
            {
                _ownsTrayIconHandle = true;
                return smallIcons[0];
            }
        }

        return LoadImage(IntPtr.Zero, new IntPtr(IdiApplication), ImageIcon, 0, 0, LrShared);
    }

    private async void StartHostButton_Click(object sender, RoutedEventArgs e)
    {
        await StartHostAsync();
    }

    private async void OverviewPrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (StaticOverviewUi && _overviewNavigationItem == OverviewNavigationItem.Camera)
        {
            await SetOverviewCameraEnabledAsync(true);
            return;
        }

        await StartHostAsync();
    }

    private void OverviewSidebarToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _overviewSidebarCollapsed = !_overviewSidebarCollapsed;
        UpdateOverviewSidebarLayout();
        SetOverviewNavigationItem(_overviewNavigationItem);
    }

    private void OverviewNavOverviewButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOverviewNavigationItem(OverviewNavigationItem.Overview);
    }

    private void OverviewNavConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOverviewNavigationItem(OverviewNavigationItem.Connection);
    }

    private void OverviewNavDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOverviewNavigationItem(OverviewNavigationItem.Display);
    }

    private void OverviewNavCameraButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOverviewNavigationItem(OverviewNavigationItem.Camera);
    }

    private void OverviewNavAudioButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOverviewNavigationItem(OverviewNavigationItem.Audio);
    }

    private void OverviewNavDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOverviewNavigationItem(OverviewNavigationItem.Diagnostics);
    }

    private void OverviewNavSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOverviewNavigationItem(OverviewNavigationItem.Settings);
    }

    private async void OverviewRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshOverviewAsync(showErrors: true);
    }

    private async void OverviewAudioHeaderRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAudioEndpointsAsync(showHint: true);
        UpdateAudioState();
    }

    private async void OverviewAudioHeaderInstallButton_Click(object sender, RoutedEventArgs e)
    {
        await InstallVirtualAudioCableAsync();
    }

    private void OverviewAudioHeaderCopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        CopyAudioDiagnosticsToClipboard();
    }

    private void OverviewActionsMenuFlyout_Opening(object sender, object e)
    {
        UpdateOverviewActionButtons();
    }

    private async void OverviewActionStartHostMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await StartHostAsync();
    }

    private void OverviewActionStopHostMenuItem_Click(object sender, RoutedEventArgs e)
    {
        StopHost();
    }

    private async void OverviewActionRefreshDevicesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAdbDevicesAsync(showErrors: true);
    }

    private async void OverviewActionRestartAdbMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RestartAdbAsync(showErrors: true);
    }

    private async void OverviewActionRepairEndpointsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RepairOverviewEndpointsAsync(openSettings: true);
    }

    private void OverviewActionOpenDisplaySettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenWindowsDisplaySettings();
    }

    private void OverviewActionCopyDiagnosticsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CopyOverviewDiagnosticsToClipboard();
    }

    private async void OverviewActionOpenLogsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowOverviewLogsDialogAsync();
    }

    private void StaticDiagnosticsPage_CopyAllRequested(object? sender, EventArgs e)
    {
        CopyOverviewDiagnosticsToClipboard();
    }

    private async void StaticDiagnosticsPage_ExportLogsRequested(object? sender, EventArgs e)
    {
        await ExportOverviewLogsAsync();
    }

    private async void StaticDiagnosticsPage_RefreshRequested(object? sender, EventArgs e)
    {
        await RefreshOverviewAsync(showErrors: true);
    }

    private async void StaticDiagnosticsPage_RecheckRequested(object? sender, EventArgs e)
    {
        await RefreshOverviewAsync(showErrors: true);
    }

    private void OverviewSettingsSaveButton_Click(object sender, RoutedEventArgs e)
    {
        OverviewSettingsPage.SaveChanges();
    }

    private void OverviewSettingsRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        OverviewSettingsPage.RestoreDefaults();
    }

    private void OverviewSettingsOpenDataDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SideDock");
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError("无法打开数据目录", ex.Message);
        }
    }

    private void OverviewEnvironmentBannerDismissButton_Click(object sender, RoutedEventArgs e)
    {
        _overviewEnvironmentBannerDismissed = true;
        OverviewEnvironmentStatusBanner.Visibility = Visibility.Collapsed;
        OverviewConnectionEnvironmentStatusBanner.Visibility = Visibility.Collapsed;
    }

    private async void StaticDisplayPage_StartRequested(object? sender, EventArgs e)
    {
        var succeeded = await SetOverviewVirtualDisplayEnabledAsync(true);
        OverviewDisplayPage.AddActivityLog(
            succeeded
                ? "虚拟显示器已启动。"
                : $"启动虚拟显示器失败：{FailureSummary(_virtualDisplayLastError)}",
            succeeded ? StaticDisplayActivityKind.Success : StaticDisplayActivityKind.Failure);
    }

    private async void StaticDisplayPage_StopRequested(object? sender, EventArgs e)
    {
        var succeeded = await SetOverviewVirtualDisplayEnabledAsync(false);
        OverviewDisplayPage.AddActivityLog(
            succeeded
                ? "虚拟显示器已停止。"
                : "停止虚拟显示器失败：仍检测到 DeviceTool 进程。",
            succeeded ? StaticDisplayActivityKind.Success : StaticDisplayActivityKind.Failure);
    }

    private void StaticDisplayPage_RefreshRequested(object? sender, EventArgs e)
    {
        var displayLayout = RefreshVirtualDisplayState();
        var refreshFailed = !string.IsNullOrWhiteSpace(displayLayout.QueryError);
        OverviewDisplayPage.AddActivityLog(
            BuildDisplayLayoutRefreshSummary(displayLayout),
            refreshFailed
                ? StaticDisplayActivityKind.Failure
                : displayLayout.HasSideDockVirtualDisplay ? StaticDisplayActivityKind.Success : StaticDisplayActivityKind.Warning);
    }

    private async void StaticDisplayPage_InstallDriverRequested(object? sender, EventArgs e)
    {
        var succeeded = await InstallDriverAsync();
        OverviewDisplayPage.AddActivityLog(
            succeeded
                ? "驱动安装/修复流程已完成。"
                : $"驱动安装/修复未完成：{FailureSummary(_driverInstallLastError)}",
            succeeded ? StaticDisplayActivityKind.Success : StaticDisplayActivityKind.Failure);
    }

    private void StaticDisplayPage_OpenDisplaySettingsRequested(object? sender, EventArgs e)
    {
        var result = OpenWindowsDisplaySettings();
        OverviewDisplayPage.AddActivityLog(
            result.Success
                ? "Windows 显示设置已打开。"
                : $"打开显示设置失败：{FailureSummary(result.Error)}",
            result.Success ? StaticDisplayActivityKind.Success : StaticDisplayActivityKind.Failure);
    }

    private async void StaticDisplayPage_ShowLogsRequested(object? sender, EventArgs e)
    {
        await ShowOverviewLogsDialogAsync();
    }

    private Task<StaticDisplayModeApplyResult> StaticDisplayPage_DisplayModeApplyRequested(
        object sender,
        StaticDisplayModeApplyRequestedEventArgs e)
    {
        return ApplyVirtualDisplayModeSelectionAsync(e.Resolution, e.RefreshRate);
    }

    private Task<StaticDisplayPresentationModeApplyResult> StaticDisplayPage_PresentationModeApplyRequested(
        object sender,
        StaticDisplayPresentationModeApplyRequestedEventArgs e)
    {
        return ApplyVirtualDisplayPresentationModeSelectionAsync(e.Mode);
    }

    private static string BuildDisplayLayoutRefreshSummary(DisplayLayoutSnapshot displayLayout)
    {
        if (!string.IsNullOrWhiteSpace(displayLayout.QueryError))
        {
            return $"重新检测失败：{displayLayout.QueryError}";
        }

        var sideDockText = displayLayout.HasSideDockVirtualDisplay
            ? "已检测到 SideDock 虚拟显示器"
            : "未检测到 SideDock 虚拟显示器";
        return $"重新检测完成：{displayLayout.Monitors.Count} 个活动显示器，{sideDockText}。";
    }

    private static string FailureSummary(string? message)
    {
        return string.IsNullOrWhiteSpace(message) ? "请查看弹窗详情或完整日志。" : message;
    }

    private void StopHostButton_Click(object sender, RoutedEventArgs e)
    {
        StopHost();
    }

    private async void RefreshAdbDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAdbDevicesAsync(showErrors: true);
    }

    private async void RestartAdbButton_Click(object sender, RoutedEventArgs e)
    {
        if (StaticOverviewUi)
        {
            return;
        }

        await RestartAdbAsync(showErrors: true);
    }

    private void AdbDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncAdbDeviceSelectionFrom(AdbDeviceCombo);
    }

    private async void CameraFacingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StaticOverviewUi || !_uiReady)
        {
            return;
        }

        _cameraDiagnostics.Facing = NormalizeCameraFacing(Selected(CameraFacingCombo));
        UpdateCameraStatusView();

        if (_restartingForCameraFacing || _hostProcess is not { HasExited: false })
        {
            return;
        }

        _restartingForCameraFacing = true;
        CameraFacingCombo.IsEnabled = false;
        CameraStatusText.Text = $"Server: restarting  Android: {_cameraDiagnostics.ClientState}  权限: {_cameraDiagnostics.PermissionText}";
        try
        {
            StopHost();
            await StartHostAsync();
        }
        finally
        {
            _restartingForCameraFacing = false;
            CameraFacingCombo.IsEnabled = true;
            UpdateCameraStatusView();
        }
    }

    private void AudioSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (StaticOverviewUi || _loadingAudioPreferences)
        {
            return;
        }

        ApplyAudioSwitchChange(sender, AudioToggleHint(sender));
    }

    private void ApplyAudioSwitchChange(object sender, string hint)
    {
        _audioOverrideStatus = null;
        SaveAudioPreferences();
        UpdateAudioState(hint);
    }

    private async void RefreshAudioEndpointsButton_Click(object sender, RoutedEventArgs e)
    {
        if (StaticOverviewUi)
        {
            return;
        }

        await RefreshAudioEndpointsAsync(showHint: true);
    }

    private void AudioEndpointCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StaticOverviewUi || _loadingAudioEndpointChoices || !_audioEndpointChoicesReady)
        {
            return;
        }

        if (ReferenceEquals(sender, SpeakerCaptureEndpointCombo))
        {
            SaveAudioEndpointBinding(AudioEndpointRole.SpeakerCapture, SpeakerCaptureEndpointCombo.SelectedItem as AudioEndpointChoice);
        }
        else if (ReferenceEquals(sender, MicrophoneRenderEndpointCombo))
        {
            SaveAudioEndpointBinding(AudioEndpointRole.MicrophoneRender, MicrophoneRenderEndpointCombo.SelectedItem as AudioEndpointChoice);
        }
    }

    private void CopyAudioLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (StaticOverviewUi)
        {
            return;
        }

        try
        {
            var details = BuildAudioDiagnosticsReport();
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(details);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            CopyAudioLogButtonText.Text = "已复制";
        }
        catch
        {
            CopyAudioLogButtonText.Text = "复制失败";
        }
    }

    private void CopyAudioDiagnosticsToClipboard()
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(BuildAudioDiagnosticsReport());
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            ShowError("无法复制音频日志", ex.Message);
        }
    }

    private void CopyCameraDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (StaticOverviewUi)
        {
            return;
        }

        try
        {
            var details = BuildCameraDiagnosticsReport();
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(details);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            CopyCameraDiagnosticsButtonText.Text = "已复制";
        }
        catch
        {
            CopyCameraDiagnosticsButtonText.Text = "复制失败";
        }
    }

    private void ToggleCameraPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        SetCameraPreviewEnabled(!_cameraPreviewEnabled);
    }

    private async void StartVirtualCameraButton_Click(object sender, RoutedEventArgs e)
    {
        await SetOverviewCameraEnabledAsync(true);
    }

    private async void StopVirtualCameraButton_Click(object sender, RoutedEventArgs e)
    {
        await SetOverviewCameraEnabledAsync(false);
    }

    private async void RefreshVirtualCameraButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshVirtualCameraStatusAsync();
    }

    private void StartDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        if (StaticOverviewUi)
        {
            return;
        }

        StartVirtualDisplay(failureAction: "启动虚拟显示器失败");
    }

    private async void OverviewVirtualDisplaySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingOverviewVirtualDisplaySwitch || !_uiReady)
        {
            return;
        }

        await SetOverviewVirtualDisplayEnabledAsync(OverviewVirtualDisplaySwitch.IsOn);
    }

    private async void OverviewVirtualDisplayResolutionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingVirtualDisplayOptions || !_uiReady)
        {
            return;
        }

        await ApplyOverviewVirtualDisplayModeSelectionAsync();
    }

    private async void OverviewVirtualDisplayRefreshRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingVirtualDisplayOptions || !_uiReady)
        {
            return;
        }

        await ApplyOverviewVirtualDisplayModeSelectionAsync();
    }

    private async void OverviewCameraSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingOverviewCameraSwitch || !_uiReady)
        {
            return;
        }

        await SetOverviewCameraEnabledAsync(OverviewCameraSwitch.IsOn);
    }

    private void OverviewCameraResolutionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncOverviewCameraComboSelection(OverviewCameraResolutionCombo, OverviewCameraPageResolutionCombo);
        SyncOverviewCameraOptionsToDiagnostics();
    }

    private void OverviewCameraFrameRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncOverviewCameraComboSelection(OverviewCameraFrameRateCombo, OverviewCameraPageFrameRateCombo);
        SyncOverviewCameraOptionsToDiagnostics();
    }

    private void OverviewCameraPageFacingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncOverviewCameraComboSelection(OverviewCameraPageFacingCombo, CameraFacingCombo);
        SyncOverviewCameraOptionsToDiagnostics();
    }

    private void OverviewCameraPageResolutionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncOverviewCameraComboSelection(OverviewCameraPageResolutionCombo, OverviewCameraResolutionCombo);
        SyncOverviewCameraOptionsToDiagnostics();
    }

    private void OverviewCameraPageFrameRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncOverviewCameraComboSelection(OverviewCameraPageFrameRateCombo, OverviewCameraFrameRateCombo);
        SyncOverviewCameraOptionsToDiagnostics();
    }

    private void OverviewCameraPagePortBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingOverviewCameraOptions || !_uiReady)
        {
            return;
        }

        _syncingOverviewCameraOptions = true;
        try
        {
            OverviewCameraPortBox.Value = sender.Value;
        }
        finally
        {
            _syncingOverviewCameraOptions = false;
        }

        _lastAdbReverseConfigured = null;
        SyncOverviewCameraOptionsToDiagnostics();
        UpdateOverviewConnectionPage();
    }

    private void OverviewCameraPreviewStartButton_Click(object sender, RoutedEventArgs e)
    {
        SetCameraPreviewEnabled(true);
    }

    private void OverviewCameraPreviewStopButton_Click(object sender, RoutedEventArgs e)
    {
        SetCameraPreviewEnabled(false);
    }

    private async void OverviewCameraReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await RestartCameraPipelineFromUiAsync();
    }

    private async void OverviewCameraShowEventsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowOverviewLogsDialogAsync();
    }

    private void OverviewCameraLinkBannerCloseButton_Click(object sender, RoutedEventArgs e)
    {
        _overviewCameraBannerDismissed = true;
        OverviewCameraLinkBanner.Visibility = Visibility.Collapsed;
    }

    private async void InstallDriverButton_Click(object sender, RoutedEventArgs e)
    {
        if (StaticOverviewUi)
        {
            return;
        }

        await InstallDriverAsync();
    }

    private async void OverviewInstallDriverButton_Click(object sender, RoutedEventArgs e)
    {
        await InstallDriverAsync();
    }

    private void OverviewDisplaySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenWindowsDisplaySettings();
    }

    private void OverviewPreviewFitButton_Click(object sender, RoutedEventArgs e)
    {
        _overviewPreviewFillMode = !_overviewPreviewFillMode;
        UpdateOverviewPreviewChrome();
    }

    private void OverviewPreviewOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        _overviewPreviewOverlayVisible = !_overviewPreviewOverlayVisible;
        UpdateOverviewPreviewChrome();
    }

    private void OverviewPreviewSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenWindowsDisplaySettings();
    }

    private async void OverviewCameraSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowOverviewCameraSettingsDialogAsync();
    }

    private void OverviewAudioSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingOverviewAudioSwitch || !_uiReady)
        {
            return;
        }

        SetOverviewAudioEnabled(OverviewAudioSwitch.IsOn);
    }

    private async void OverviewAudioSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowOverviewAudioSettingsDialogAsync();
    }

    private async void OverviewDiagnosticsDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowOverviewDiagnosticsDialogAsync();
    }

    private async void OverviewLogsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowOverviewLogsDialogAsync();
    }

    private void OverviewConnectionAdbDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncAdbDeviceSelectionFrom(OverviewConnectionAdbDeviceCombo);
    }

    private void OverviewConnectionDeviceListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingAdbDeviceSelection
            || OverviewConnectionDeviceListView.SelectedItem is not OverviewConnectionDeviceItem item)
        {
            return;
        }

        var choice = FindAdbDeviceChoice(OverviewConnectionAdbDeviceCombo, item.Serial);
        if (choice is not null)
        {
            OverviewConnectionAdbDeviceCombo.SelectedItem = choice;
        }
    }

    private void OverviewConnectionPortBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingOverviewConnectionControls || !_uiReady)
        {
            return;
        }

        _lastAdbReverseConfigured = null;
        SyncOverviewCameraPagePortFromConnection();
        SyncOverviewConnectionControlsToLegacy();
        UpdateOverviewConnectionPage();
    }

    private void OverviewAdbPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingOverviewConnectionControls || !_uiReady)
        {
            return;
        }

        AdbPathBox.Text = OverviewAdbPathBox.Text;
        _lastAdbReverseConfigured = null;
        UpdateOverviewConnectionPage();
    }

    private void OverviewInputInjectionSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingOverviewConnectionControls || !_uiReady)
        {
            return;
        }

        InputInjectionSwitch.IsOn = OverviewInputInjectionSwitch.IsOn;
        OverviewInputInjectionStatusText.Text = OverviewInputInjectionSwitch.IsOn ? "已启用" : "未启用";
        UpdateOverviewConnectionPage();
    }

    private void OverviewRestoreDefaultPortsButton_Click(object sender, RoutedEventArgs e)
    {
        _syncingOverviewConnectionControls = true;
        try
        {
            OverviewControlPortBox.Value = DefaultControlPort;
            OverviewVideoPortBox.Value = DefaultVideoPort;
            OverviewAudioPortBox.Value = DefaultAudioPort;
            OverviewCameraPortBox.Value = DefaultCameraPort;
        }
        finally
        {
            _syncingOverviewConnectionControls = false;
        }

        _lastAdbReverseConfigured = null;
        SyncOverviewCameraPagePortFromConnection();
        SyncOverviewConnectionControlsToLegacy();
        UpdateOverviewConnectionPage();
    }

    private async void OverviewAdbPathBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, _windowHandle);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            OverviewAdbPathBox.Text = file.Path;
            await RefreshAdbDevicesAsync(showErrors: true);
        }
        catch (Exception ex)
        {
            await ShowOverviewAdbPathHelpDialogAsync(ex.Message);
        }
    }

    private async void OverviewAdvancedConnectionOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowCopyableDetailsDialogAsync(
            "高级连接选项",
            "当前连接页使用这些参数启动主机并配置 ADB reverse。",
            BuildConnectionConfigurationReport(),
            "复制配置");
    }

    private async void OverviewConnectionHelpButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowUsbDebuggingHelpDialogAsync();
    }

    private async void OverviewRepairEndpointsButton_Click(object sender, RoutedEventArgs e)
    {
        await RepairOverviewEndpointsAsync(openSettings: true);
    }

    private async Task RefreshOverviewAsync(bool showErrors)
    {
        if (_overviewRefreshInProgress)
        {
            return;
        }

        _overviewRefreshInProgress = true;
        UpdateOverviewRefreshState();
        UpdateOverviewEnvironmentBanner();

        try
        {
            RefreshOverviewHostStateSnapshot();
            await RefreshAdbDevicesAsync(showErrors);
            RefreshVirtualDisplayState();
            await RefreshVirtualCameraStatusAsync();
            await RefreshAudioEndpointsAsync(showHint: false);
            UpdateAudioState();
            UpdateCameraStatusView();
            UpdateOverviewRuntimeDiagnostics();
            UpdateOverviewPreview();
            UpdateOverviewEnvironmentBanner();
        }
        finally
        {
            _overviewRefreshInProgress = false;
            UpdateOverviewActionButtons();
            UpdateOverviewEnvironmentBanner();
            UpdateStaticDiagnosticsPage();
        }
    }

    private void RefreshOverviewHostStateSnapshot()
    {
        if (_overviewHostServiceState == OverviewHostServiceState.Starting)
        {
            return;
        }

        if (_hostProcess is { HasExited: false })
        {
            SetOverviewHostState(OverviewHostServiceState.Running, "SideDock.Host.exe 正在运行");
            return;
        }

        SetOverviewHostState(
            _hostHasStarted ? OverviewHostServiceState.Stopped : OverviewHostServiceState.NotStarted,
            _hostHasStarted ? "主机已停止" : "等待启动");
    }

    private async Task RepairOverviewEndpointsAsync(bool openSettings)
    {
        if (_overviewRefreshInProgress)
        {
            return;
        }

        OverviewRepairEndpointsButton.IsEnabled = false;
        OverviewActionRepairEndpointsMenuItem.IsEnabled = false;
        try
        {
            await RefreshAudioEndpointsAsync(showHint: true);
            await RefreshVirtualCameraStatusAsync();
            UpdateOverviewEnvironmentBanner();
            if (openSettings)
            {
                await ShowOverviewAudioSettingsDialogAsync();
            }
        }
        finally
        {
            UpdateOverviewActionMenuItems();
        }
    }

    private void CopyOverviewDiagnosticsToClipboard()
    {
        try
        {
            UpdateOverviewRuntimeDiagnostics();
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(BuildOverviewDiagnosticsReport());
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            ShowError("无法复制诊断", ex.Message);
        }
    }

    private async void InstallVirtualAudioCableButton_Click(object sender, RoutedEventArgs e)
    {
        if (StaticOverviewUi)
        {
            return;
        }

        await InstallVirtualAudioCableAsync();
    }

    private void StopDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        if (StaticOverviewUi)
        {
            return;
        }

        StopVirtualDisplay();
    }

    private async Task<bool> SetOverviewVirtualDisplayEnabledAsync(bool enabled)
    {
        if (_virtualDisplayOperationInProgress || _driverInstallInProgress)
        {
            RefreshVirtualDisplayState();
            return false;
        }

        _virtualDisplayOperationInProgress = true;
        _virtualDisplayTransientState = enabled
            ? VirtualDisplayOverviewState.Starting
            : VirtualDisplayOverviewState.Stopping;
        _virtualDisplayLastError = null;
        RefreshVirtualDisplayState();
        var succeeded = false;

        try
        {
            await Task.Yield();

            if (enabled)
            {
                ManageDisplaySwitch.IsOn = true;
                if (!StartVirtualDisplay(failureAction: "启动虚拟显示器失败"))
                {
                    ManageDisplaySwitch.IsOn = false;
                    _virtualDisplayLastError ??= "启动虚拟显示器失败。";
                }

                succeeded = IsVirtualDisplayToolRunning() && string.IsNullOrWhiteSpace(_virtualDisplayLastError);
            }
            else
            {
                ManageDisplaySwitch.IsOn = false;
                _hostOwnsVirtualDisplay = false;
                StopVirtualDisplay();
                succeeded = !IsVirtualDisplayToolRunning();
            }
        }
        finally
        {
            _virtualDisplayTransientState = null;
            _virtualDisplayOperationInProgress = false;
            RefreshVirtualDisplayState();
        }

        return succeeded;
    }

    private async Task SetOverviewCameraEnabledAsync(bool enabled)
    {
        if (_overviewCameraOperationInProgress)
        {
            UpdateOverviewCameraState();
            return;
        }

        _overviewCameraRequestedEnabled = enabled;
        _overviewCameraOperationInProgress = true;
        UpdateOverviewCameraState();

        try
        {
            if (enabled)
            {
                SyncOverviewCameraOptionsToDiagnostics();
                await StartCameraPipelineAsync();
            }
            else
            {
                await StopCameraPipelineAsync();
            }

            await RefreshVirtualCameraStatusAsync();
        }
        finally
        {
            _overviewCameraOperationInProgress = false;
            UpdateOverviewCameraState();
        }
    }

    private (bool Success, string? Error) OpenWindowsDisplaySettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:display",
                UseShellExecute = true
            });
            return (true, null);
        }
        catch (Exception firstEx)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "desk.cpl",
                    UseShellExecute = true
                });
                return (true, null);
            }
            catch (Exception ex)
            {
                ShowError("无法打开显示设置", ex.Message);
                return (false, $"{firstEx.Message}; {ex.Message}");
            }
        }
    }

    private async Task StartHostAsync()
    {
        if (_hostProcess is { HasExited: false })
        {
            return;
        }

        SyncOverviewConnectionControlsToLegacy();

        string? hostPath = null;
        string? arguments = null;
        string? workingDirectory = null;
        string? adbPath = null;
        string? adbSerial = null;
        HostProcessLog? hostLog = null;

        try
        {
            ClearAudioDiagnostics();
            _currentHostLog = null;
            _hostStopRequestedProcessId = null;
            StartHostButton.IsEnabled = false;
            StopHostButton.IsEnabled = false;
            AdbDeviceCombo.IsEnabled = false;
            RefreshAdbDevicesButton.IsEnabled = false;
            RestartAdbButton.IsEnabled = false;
            CameraFacingCombo.IsEnabled = false;
            OverallStatusText.Text = "启动中";
            OverallStatusText.Foreground = _secondaryBrush;
            SetAdbStatus("正在检查 ADB reverse...", _secondaryBrush);
            SetOverviewHostState(OverviewHostServiceState.Starting, "正在检查 ADB reverse...");
            UpdateOverviewConnectionPage();

            adbPath = ResolveAdbPath(AdbPathBox.Text.Trim());
            var explicitAdbSerial = SelectedAdbSerial();
            await RefreshAdbDevicesAsync(showErrors: false, resolvedAdbPath: adbPath);
            var selectedAdbSerial = explicitAdbSerial ?? SelectedAdbSerial();
            var reversePorts = GetConfiguredReversePorts();
            var adbPreflight = await ConfigureAdbReverseBeforeHostStartAsync(adbPath, reversePorts, selectedAdbSerial);
            if (!adbPreflight.Success)
            {
                _lastAdbReverseConfigured = false;
                _lastAdbReverseSerial = adbPreflight.Serial;
                _lastAdbReverseDetail = adbPreflight.Summary;
                SetRunningState(false);
                SetAdbStatus(adbPreflight.Summary, _dangerBrush);
                SetOverviewHostState(OverviewHostServiceState.Error, adbPreflight.Summary);
                UpdateOverviewConnectionPage();
                ShowErrorWithDetails(
                    "无法配置 ADB reverse",
                    adbPreflight.Summary,
                    adbPreflight.Details);
                return;
            }

            adbSerial = adbPreflight.Serial;
            _lastAdbReverseConfigured = true;
            _lastAdbReverseSerial = adbPreflight.Serial;
            _lastAdbReverseDetail = adbPreflight.Summary;
            SetAdbStatus(adbPreflight.Summary, _successBrush);
            UpdateOverviewConnectionPage();

            if (ShouldManageVirtualDisplayWithHost())
            {
                var displayWasRunning = IsVirtualDisplayToolRunning();
                if (!StartVirtualDisplay(failureAction: "启动主机时无法启动虚拟显示器"))
                {
                    SetRunningState(false);
                    SetOverviewHostState(OverviewHostServiceState.Error, "启动主机时无法启动虚拟显示器。");
                    return;
                }

                _hostOwnsVirtualDisplay = !displayWasRunning && IsVirtualDisplayToolRunning();
            }

            hostPath = _hostPath ??= ResolveHostPath();
            arguments = BuildArguments();
            workingDirectory = Path.GetDirectoryName(hostPath) ?? Environment.CurrentDirectory;
            var startInfo = new ProcessStartInfo
            {
                FileName = hostPath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (!string.IsNullOrWhiteSpace(adbPath))
            {
                startInfo.Environment["SIDEDOCK_ADB"] = adbPath;
            }

            if (!string.IsNullOrWhiteSpace(adbSerial))
            {
                startInfo.Environment["ANDROID_SERIAL"] = adbSerial;
            }

            hostLog = new HostProcessLog(hostPath, arguments, workingDirectory, adbPath, adbSerial);
            _currentHostLog = hostLog;
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) =>
            {
                hostLog.Append("stdout", args.Data);
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    DispatcherQueue.TryEnqueue(() => HandleHostOutputLine(args.Data));
                }
            };
            process.ErrorDataReceived += (_, args) => hostLog.Append("stderr", args.Data);
            process.Exited += (_, _) => DispatcherQueue.TryEnqueue(() => HandleHostExited(process, hostLog));

            if (!process.Start())
            {
                throw new InvalidOperationException($"无法启动 {HostExe}。");
            }

            _hostProcess = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            SetRunningState(true);
        }
        catch (Exception ex)
        {
            StopHostOwnedVirtualDisplay();
            _hostProcess = null;
            var details = BuildHostFailureDetails(
                "启动主机时发生异常",
                hostPath,
                arguments,
                workingDirectory,
                adbPath,
                adbSerial,
                exitCode: null,
                hostLog,
                ex);
            ShowErrorWithDetails(
                "无法启动 SideDock 主机",
                $"启动 SideDock 主机时出错：{ex.Message}。下面是详细诊断信息，点“复制详情”可一键复制后发给开发者排查。",
                details);
            SetRunningState(false);
            SetOverviewHostState(OverviewHostServiceState.Error, ex.Message);
            _audioOverrideStatus = AudioCapabilityStatus.Error;
            UpdateAudioState("音频设备暂不可用，主机未启动。");
        }
    }

    private string BuildArguments()
    {
        var (cameraWidth, cameraHeight) = SelectedOverviewCameraResolution();
        var cameraFps = SelectedOverviewCameraFps();
        var args = new List<string>
        {
            "--video-source", Selected(VideoSourceCombo),
            "--resolution", Selected(ResolutionCombo),
            "--refresh-rate", Selected(RefreshRateCombo),
            "--control-port", ConfiguredControlPortNumber().ToString(CultureInfo.InvariantCulture),
            "--video-port", ConfiguredVideoPortNumber().ToString(CultureInfo.InvariantCulture),
            "--audio-port", ConfiguredAudioPortNumber().ToString(CultureInfo.InvariantCulture),
            "--camera-port", ConfiguredCameraPortNumber().ToString(CultureInfo.InvariantCulture),
            "--camera-width", cameraWidth.ToString(CultureInfo.InvariantCulture),
            "--camera-height", cameraHeight.ToString(CultureInfo.InvariantCulture),
            "--camera-fps", cameraFps.ToString(CultureInfo.InvariantCulture),
            "--camera-facing", Selected(CameraFacingCombo),
            "--audio-backend", "wasapi-virtual-cable"
        };

        if (InputInjectionSwitch.IsOn)
        {
            args.Add("--enable-input-injection");
        }

        if (!AudioDeviceSwitch.IsOn)
        {
            args.Add("--disable-audio");
        }

        if (!MicrophoneSwitch.IsOn)
        {
            args.Add("--disable-microphone");
        }

        if (!SpeakerSwitch.IsOn)
        {
            args.Add("--disable-speaker");
        }

        if (!string.IsNullOrWhiteSpace(_boundSpeakerCaptureEndpointId))
        {
            args.Add("--audio-output-loopback-endpoint-id");
            args.Add(_boundSpeakerCaptureEndpointId);
        }

        if (!string.IsNullOrWhiteSpace(_boundMicrophoneRenderEndpointId))
        {
            args.Add("--audio-microphone-render-endpoint-id");
            args.Add(_boundMicrophoneRenderEndpointId);
        }

        return string.Join(" ", args.Select(QuoteArgument));
    }

    private IReadOnlyList<int> GetConfiguredReversePorts()
    {
        var controlPort = ConfiguredControlPortNumber();
        var videoPort = ConfiguredVideoPortNumber();
        var ports = new List<int> { controlPort, videoPort };
        if (AudioDeviceSwitch.IsOn && (MicrophoneSwitch.IsOn || SpeakerSwitch.IsOn))
        {
            ports.Add(ConfiguredAudioPortNumber());
        }

        ports.Add(ConfiguredCameraPortNumber());
        return ports.Distinct().ToArray();
    }

    private int ConfiguredControlPortNumber()
    {
        return PortNumber(StaticOverviewUi ? OverviewControlPortBox : ControlPortBox, "control");
    }

    private int ConfiguredVideoPortNumber()
    {
        return PortNumber(StaticOverviewUi ? OverviewVideoPortBox : VideoPortBox, "video");
    }

    private int ConfiguredAudioPortNumber()
    {
        return StaticOverviewUi ? PortNumber(OverviewAudioPortBox, "audio") : DefaultAudioPort;
    }

    private int ConfiguredCameraPortNumber()
    {
        return StaticOverviewUi ? PortNumber(OverviewCameraPortBox, "camera") : DefaultCameraPort;
    }

    private void InitializeAdbDeviceCombo()
    {
        AdbDeviceCombo.DisplayMemberPath = nameof(AdbDeviceChoice.DisplayName);
        OverviewConnectionAdbDeviceCombo.DisplayMemberPath = nameof(AdbDeviceChoice.DisplayName);
        SetAdbDeviceChoices(Array.Empty<AdbDeviceRow>(), selectedSerial: null);
    }

    private void InitializeAudioEndpointCombos()
    {
        SpeakerCaptureEndpointCombo.DisplayMemberPath = nameof(AudioEndpointChoice.DisplayLabel);
        MicrophoneRenderEndpointCombo.DisplayMemberPath = nameof(AudioEndpointChoice.DisplayLabel);
        _loadingAudioEndpointChoices = true;
        try
        {
            SpeakerCaptureEndpointCombo.ItemsSource = new[]
            {
                AudioEndpointChoice.Unbound(AudioEndpointRole.SpeakerCapture)
            };
            MicrophoneRenderEndpointCombo.ItemsSource = new[]
            {
                AudioEndpointChoice.Unbound(AudioEndpointRole.MicrophoneRender)
            };
            SpeakerCaptureEndpointCombo.SelectedIndex = 0;
            MicrophoneRenderEndpointCombo.SelectedIndex = 0;
        }
        finally
        {
            _loadingAudioEndpointChoices = false;
        }
    }

    private void InitializeOverviewVirtualDisplayOptions()
    {
        _syncingVirtualDisplayOptions = true;
        try
        {
            SelectComboBoxValue(OverviewVirtualDisplayResolutionCombo, Selected(ResolutionCombo));
            SelectComboBoxValue(OverviewVirtualDisplayRefreshRateCombo, Selected(RefreshRateCombo));
        }
        finally
        {
            _syncingVirtualDisplayOptions = false;
        }
    }

    private async Task ApplyOverviewVirtualDisplayModeSelectionAsync()
    {
        var result = await ApplyVirtualDisplayModeSelectionAsync(
            Selected(OverviewVirtualDisplayResolutionCombo),
            Selected(OverviewVirtualDisplayRefreshRateCombo));

        OverviewDisplayPage.AddActivityLog(
            result.Success
                ? $"显示模式已应用：{result.CurrentModeText ?? "当前请求"}。"
                : $"显示模式应用失败：{result.Message}",
            result.Success ? StaticDisplayActivityKind.Success : StaticDisplayActivityKind.Failure);

        if (!result.Success)
        {
            RollBackOverviewVirtualDisplaySelection(result);
        }
    }

    private async Task<StaticDisplayModeApplyResult> ApplyVirtualDisplayModeSelectionAsync(string resolution, string refreshRate)
    {
        if (_virtualDisplayModeApplyInProgress || _virtualDisplayOperationInProgress || _driverInstallInProgress)
        {
            var busyMode = CurrentSideDockModeFromLayout(DisplayLayoutQuery.GetCurrent());
            return BuildStaticDisplayModeApplyResult(false, "显示器操作正在进行中，请稍后再试。", busyMode, null);
        }

        if (!TryCreateVirtualDisplayModeRequest(resolution, refreshRate, out var request, out var requestError))
        {
            var currentMode = CurrentSideDockModeFromLayout(DisplayLayoutQuery.GetCurrent());
            return BuildStaticDisplayModeApplyResult(false, requestError, currentMode, null);
        }

        _virtualDisplayModeApplyInProgress = true;
        RefreshVirtualDisplayState();

        try
        {
            var serviceResult = await Task.Run(() => VirtualDisplayModeService.Apply(request));
            if (serviceResult.Success)
            {
                SyncVirtualDisplayModeSelection(request.Resolution, request.RefreshRateValue);
            }

            return BuildStaticDisplayModeApplyResult(
                serviceResult.Success,
                serviceResult.Summary,
                serviceResult.CurrentMode,
                serviceResult.Success ? request : null);
        }
        catch (Exception ex)
        {
            var currentMode = CurrentSideDockModeFromLayout(DisplayLayoutQuery.GetCurrent());
            return BuildStaticDisplayModeApplyResult(false, ex.Message, currentMode, null);
        }
        finally
        {
            _virtualDisplayModeApplyInProgress = false;
            RefreshVirtualDisplayState();
        }
    }

    private async Task<StaticDisplayPresentationModeApplyResult> ApplyVirtualDisplayPresentationModeSelectionAsync(
        VirtualDisplayPresentationMode mode)
    {
        if (_virtualDisplayModeApplyInProgress || _virtualDisplayOperationInProgress || _driverInstallInProgress)
        {
            var busyState = VirtualDisplayModeService.GetPresentationState();
            return new StaticDisplayPresentationModeApplyResult
            {
                Success = false,
                Message = "显示器操作正在进行中，请稍后再试。",
                CurrentMode = busyState.Mode
            };
        }

        if (mode is VirtualDisplayPresentationMode.Mirror or VirtualDisplayPresentationMode.SecondaryOnly)
        {
            var currentState = VirtualDisplayModeService.GetPresentationState();
            return new StaticDisplayPresentationModeApplyResult
            {
                Success = false,
                Message = "该显示模式暂未支持，需要后续安全确认流程。",
                CurrentMode = currentState.Mode
            };
        }

        _virtualDisplayModeApplyInProgress = true;
        RefreshVirtualDisplayState();

        try
        {
            var serviceResult = await Task.Run(() => VirtualDisplayModeService.ApplyPresentationMode(mode));
            return new StaticDisplayPresentationModeApplyResult
            {
                Success = serviceResult.Success,
                Message = serviceResult.Summary,
                CurrentMode = serviceResult.CurrentMode
            };
        }
        catch (Exception ex)
        {
            var currentState = VirtualDisplayModeService.GetPresentationState();
            return new StaticDisplayPresentationModeApplyResult
            {
                Success = false,
                Message = ex.Message,
                CurrentMode = currentState.Mode
            };
        }
        finally
        {
            _virtualDisplayModeApplyInProgress = false;
            RefreshVirtualDisplayState();
        }
    }

    private void SyncVirtualDisplayModeSelection(string resolution, string refreshRate)
    {
        _syncingVirtualDisplayOptions = true;
        try
        {
            SelectComboBoxValue(ResolutionCombo, resolution);
            SelectComboBoxValue(RefreshRateCombo, refreshRate);
            SelectComboBoxValue(OverviewVirtualDisplayResolutionCombo, resolution);
            SelectComboBoxValue(OverviewVirtualDisplayRefreshRateCombo, refreshRate);
        }
        finally
        {
            _syncingVirtualDisplayOptions = false;
        }
    }

    private void RollBackOverviewVirtualDisplaySelection(StaticDisplayModeApplyResult result)
    {
        _syncingVirtualDisplayOptions = true;
        try
        {
            var resolution = result.DisplayedResolution ?? Selected(ResolutionCombo);
            if (!SelectComboBoxValue(OverviewVirtualDisplayResolutionCombo, resolution))
            {
                OverviewVirtualDisplayResolutionCombo.SelectedIndex = -1;
            }

            var refreshRate = result.DisplayedRefreshRate ?? Selected(RefreshRateCombo);
            if (!SelectComboBoxValue(OverviewVirtualDisplayRefreshRateCombo, refreshRate))
            {
                OverviewVirtualDisplayRefreshRateCombo.SelectedIndex = -1;
            }
        }
        finally
        {
            _syncingVirtualDisplayOptions = false;
        }
    }

    private static StaticDisplayModeApplyResult BuildStaticDisplayModeApplyResult(
        bool success,
        string message,
        VirtualDisplayMode? currentMode,
        VirtualDisplayModeRequest? successfulRequest)
    {
        return new StaticDisplayModeApplyResult
        {
            Success = success,
            Message = message,
            CurrentModeText = currentMode is null ? null : VirtualDisplayModeService.FormatMode(currentMode),
            DisplayedResolution = successfulRequest?.Resolution ?? DisplayResolutionValueFromMode(currentMode),
            DisplayedRefreshRate = successfulRequest?.RefreshRateValue ?? DisplayRefreshRateValueFromMode(currentMode)
        };
    }

    private bool TryCreateVirtualDisplayModeRequest(
        string resolution,
        string refreshRate,
        out VirtualDisplayModeRequest request,
        out string error)
    {
        var normalizedResolution = NormalizeVirtualDisplayResolutionSelection(resolution)
            ?? NormalizeVirtualDisplayResolutionSelection(Selected(ResolutionCombo));
        var normalizedRefreshRate = NormalizeVirtualDisplayRefreshRateSelection(refreshRate)
            ?? NormalizeVirtualDisplayRefreshRateSelection(Selected(RefreshRateCombo));

        request = new VirtualDisplayModeRequest("1080p", 1920, 1080, "120", 120);
        if (normalizedResolution is null)
        {
            error = "请选择 720p、1080p 或 2K 分辨率。";
            return false;
        }

        if (normalizedRefreshRate is null)
        {
            error = "请选择 30、60 或 120 Hz 刷新率。";
            return false;
        }

        var (width, height) = normalizedResolution switch
        {
            "720p" => (1280, 720),
            "2k" => (2560, 1440),
            _ => (1920, 1080)
        };
        var refreshRateValue = int.Parse(normalizedRefreshRate, NumberStyles.Integer, CultureInfo.InvariantCulture);
        request = new VirtualDisplayModeRequest(
            normalizedResolution,
            width,
            height,
            normalizedRefreshRate,
            refreshRateValue);
        error = string.Empty;
        return true;
    }

    private static string? NormalizeVirtualDisplayResolutionSelection(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "720" or "720p" => "720p",
            "1080" or "1080p" => "1080p",
            "2k" or "2K" or "1440p" => "2k",
            _ => null
        };
    }

    private static string? NormalizeVirtualDisplayRefreshRateSelection(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Replace("Hz", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        return normalized switch
        {
            "30" => "30",
            "60" => "60",
            "120" => "120",
            _ => null
        };
    }

    private static VirtualDisplayMode? CurrentSideDockModeFromLayout(DisplayLayoutSnapshot displayLayout)
    {
        var monitor = displayLayout.SideDockMonitor;
        return monitor is null
            ? null
            : new VirtualDisplayMode(monitor.Width, monitor.Height, monitor.RefreshRate);
    }

    private static string? DisplayResolutionValueFromMode(VirtualDisplayMode? mode)
    {
        return mode is null
            ? null
            : (mode.Width, mode.Height) switch
            {
                (1280, 720) => "720p",
                (1920, 1080) => "1080p",
                (2560, 1440) => "2k",
                _ => $"{mode.Width.ToString(CultureInfo.InvariantCulture)} × {mode.Height.ToString(CultureInfo.InvariantCulture)}"
            };
    }

    private static string? DisplayRefreshRateValueFromMode(VirtualDisplayMode? mode)
    {
        if (mode is not { RefreshRate: > 0 })
        {
            return null;
        }

        return mode.RefreshRate switch
        {
            >= 29 and <= 31 => "30",
            >= 59 and <= 61 => "60",
            >= 119 and <= 121 => "120",
            _ => mode.RefreshRate.ToString(CultureInfo.InvariantCulture)
        };
    }

    private void InitializeOverviewCameraOptions()
    {
        _syncingOverviewCameraOptions = true;
        try
        {
            SelectComboBoxValue(OverviewCameraPageFacingCombo, Selected(CameraFacingCombo));
            SelectComboBoxValue(OverviewCameraResolutionCombo, CameraResolutionValue(_cameraDiagnostics.Width, _cameraDiagnostics.Height));
            SelectComboBoxValue(OverviewCameraFrameRateCombo, _cameraDiagnostics.Fps.ToString(CultureInfo.InvariantCulture));
            SelectComboBoxValue(OverviewCameraPageResolutionCombo, Selected(OverviewCameraResolutionCombo));
            SelectComboBoxValue(OverviewCameraPageFrameRateCombo, Selected(OverviewCameraFrameRateCombo));
            SelectComboBoxValue(OverviewCameraPageCodecCombo, _cameraDiagnostics.Codec);
            OverviewCameraPagePortBox.Value = OverviewCameraPortBox.Value;
        }
        finally
        {
            _syncingOverviewCameraOptions = false;
        }

        SyncOverviewCameraOptionsToDiagnostics();
        UpdateOverviewCameraState();
    }

    private void SyncOverviewCameraOptionsToDiagnostics()
    {
        if (_syncingOverviewCameraOptions || !_uiReady)
        {
            return;
        }

        if (_hostProcess is { HasExited: false })
        {
            UpdateOverviewCameraState();
            return;
        }

        var (width, height) = SelectedOverviewCameraResolution();
        _cameraDiagnostics.Width = width;
        _cameraDiagnostics.Height = height;
        _cameraDiagnostics.Fps = SelectedOverviewCameraFps();
        _cameraDiagnostics.Facing = NormalizeCameraFacing(Selected(CameraFacingCombo));
        if (TryReadPort(OverviewCameraPortBox, out var cameraPort))
        {
            _cameraDiagnostics.Port = cameraPort;
        }

        UpdateCameraStatusView();
    }

    private void SyncOverviewCameraComboSelection(ComboBox source, ComboBox target)
    {
        if (_syncingOverviewCameraOptions || !_uiReady)
        {
            return;
        }

        _syncingOverviewCameraOptions = true;
        try
        {
            SelectComboBoxValue(target, Selected(source));
        }
        finally
        {
            _syncingOverviewCameraOptions = false;
        }
    }

    private void SyncOverviewCameraPagePortFromConnection()
    {
        if (_syncingOverviewCameraOptions || OverviewCameraPagePortBox is null)
        {
            return;
        }

        _syncingOverviewCameraOptions = true;
        try
        {
            OverviewCameraPagePortBox.Value = OverviewCameraPortBox.Value;
        }
        finally
        {
            _syncingOverviewCameraOptions = false;
        }
    }

    private (int Width, int Height) SelectedOverviewCameraResolution()
    {
        return Selected(OverviewCameraResolutionCombo).Trim().ToLowerInvariant() switch
        {
            "1080p" => (1920, 1080),
            "2k" => (2560, 1440),
            _ => (1280, 720)
        };
    }

    private int SelectedOverviewCameraFps()
    {
        return int.TryParse(Selected(OverviewCameraFrameRateCombo), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fps) && fps > 0
            ? fps
            : 30;
    }

    private static string CameraResolutionValue(int width, int height)
    {
        return (width, height) switch
        {
            (1920, 1080) => "1080p",
            (2560, 1440) => "2k",
            _ => "720p"
        };
    }

    private async Task ShowOverviewCameraSettingsDialogAsync()
    {
        var hostRunning = _hostProcess is { HasExited: false };
        var facingCombo = CreateOverviewCameraDialogComboBox(
            ("后置", "back"),
            ("前置", "front"));
        var resolutionCombo = CreateOverviewCameraDialogComboBox(
            ("720p", "720p"),
            ("1080p", "1080p"),
            ("2K", "2k"));
        var frameRateCombo = CreateOverviewCameraDialogComboBox(
            ("30", "30"),
            ("60", "60"),
            ("120", "120"));

        SelectComboBoxValue(facingCombo, Selected(CameraFacingCombo));
        SelectComboBoxValue(resolutionCombo, Selected(OverviewCameraResolutionCombo));
        SelectComboBoxValue(frameRateCombo, Selected(OverviewCameraFrameRateCombo));

        facingCombo.IsEnabled = !hostRunning;
        resolutionCombo.IsEnabled = !hostRunning;
        frameRateCombo.IsEnabled = !hostRunning;

        var content = new StackPanel { Spacing = 10, MinWidth = 280 };
        AddCameraDialogField(content, "镜头", facingCombo);
        AddCameraDialogField(content, "分辨率", resolutionCombo);
        AddCameraDialogField(content, "帧率", frameRateCombo);
        if (hostRunning)
        {
            content.Children.Add(new TextBlock
            {
                Text = "主机运行中，摄像头配置会在下次启动时生效。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = _secondaryBrush
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = StaticOverviewShell.XamlRoot,
            Title = "摄像头设置",
            CloseButtonText = hostRunning ? "关闭" : "取消",
            Content = content
        };
        if (!hostRunning)
        {
            dialog.PrimaryButtonText = "保存";
            dialog.DefaultButton = ContentDialogButton.Primary;
        }

        try
        {
            var result = await dialog.ShowAsync();
            if (hostRunning || result != ContentDialogResult.Primary)
            {
                return;
            }

            _syncingOverviewCameraOptions = true;
            try
            {
                SelectComboBoxValue(CameraFacingCombo, Selected(facingCombo));
                SelectComboBoxValue(OverviewCameraResolutionCombo, Selected(resolutionCombo));
                SelectComboBoxValue(OverviewCameraFrameRateCombo, Selected(frameRateCombo));
                SelectComboBoxValue(OverviewCameraPageFacingCombo, Selected(facingCombo));
                SelectComboBoxValue(OverviewCameraPageResolutionCombo, Selected(resolutionCombo));
                SelectComboBoxValue(OverviewCameraPageFrameRateCombo, Selected(frameRateCombo));
            }
            finally
            {
                _syncingOverviewCameraOptions = false;
            }

            _cameraDiagnostics.Facing = NormalizeCameraFacing(Selected(CameraFacingCombo));
            SyncOverviewCameraOptionsToDiagnostics();
        }
        catch (Exception ex)
        {
            ShowError("无法打开摄像头设置", ex.Message);
        }
    }

    private static ComboBox CreateOverviewCameraDialogComboBox(params (string Content, string Tag)[] items)
    {
        var comboBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 220
        };
        foreach (var (content, tag) in items)
        {
            comboBox.Items.Add(new ComboBoxItem { Content = content, Tag = tag });
        }

        comboBox.SelectedIndex = 0;
        return comboBox;
    }

    private static void AddCameraDialogField(StackPanel content, string label, ComboBox comboBox)
    {
        content.Children.Add(new TextBlock { Text = label });
        content.Children.Add(comboBox);
    }

    private static bool SelectComboBoxValue(ComboBox comboBox, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        for (var index = 0; index < comboBox.Items.Count; index++)
        {
            if (comboBox.Items[index] is ComboBoxItem item
                && string.Equals(ComboBoxItemValue(item), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedIndex = index;
                return true;
            }
        }

        return false;
    }

    private static string ComboBoxItemValue(ComboBoxItem item)
    {
        return item.Tag?.ToString() ?? item.Content?.ToString() ?? string.Empty;
    }

    private void SetOverviewAudioEnabled(bool enabled)
    {
        if (AudioDeviceSwitch is null)
        {
            return;
        }

        AudioDeviceSwitch.IsOn = enabled;
        ApplyAudioSwitchChange(AudioDeviceSwitch, OverviewAudioToggleHint(enabled));
    }

    private string OverviewAudioToggleHint(bool enabled)
    {
        var hostRunning = _hostProcess is { HasExited: false };
        if (enabled)
        {
            return hostRunning
                ? "音频桥接已启用，正在准备音频设备。"
                : "音频桥接已启用，将在下次启动主机时生效。";
        }

        return hostRunning
            ? "音频桥接已关闭，偏好已保存；重启主机后将按关闭状态启动。"
            : "音频桥接已关闭，将在下次启动主机时保持关闭。";
    }

    private async Task ShowOverviewAudioSettingsDialogAsync()
    {
        if (!_audioEndpointChoicesReady)
        {
            await RefreshAudioEndpointsAsync(showHint: false);
        }

        var speakerEndpointCombo = CreateOverviewAudioEndpointCombo(SpeakerCaptureEndpointCombo);
        var microphoneEndpointCombo = CreateOverviewAudioEndpointCombo(MicrophoneRenderEndpointCombo);
        var speakerEndpointStatusText = CreateOverviewAudioEndpointStatusText();
        var microphoneEndpointStatusText = CreateOverviewAudioEndpointStatusText();
        var refreshButton = new Button
        {
            Padding = new Thickness(10, 6, 10, 6),
            Content = CreateIconButtonContent("\uE72C", "刷新端点")
        };
        var installButton = new Button
        {
            Padding = new Thickness(10, 6, 10, 6),
            Content = CreateIconButtonContent("\uE896", "安装/修复虚拟音频线")
        };

        var syncingEndpointCombos = false;
        void SyncFromMainEndpointCombos()
        {
            syncingEndpointCombos = true;
            try
            {
                SyncOverviewAudioEndpointCombo(speakerEndpointCombo, SpeakerCaptureEndpointCombo);
                SyncOverviewAudioEndpointCombo(microphoneEndpointCombo, MicrophoneRenderEndpointCombo);
            }
            finally
            {
                syncingEndpointCombos = false;
            }

            SetAudioEndpointStatusText(speakerEndpointStatusText, _speakerCaptureEndpointDiagnostics);
            SetAudioEndpointStatusText(microphoneEndpointStatusText, _microphoneRenderEndpointDiagnostics);
        }

        speakerEndpointCombo.SelectionChanged += (_, _) =>
        {
            if (syncingEndpointCombos)
            {
                return;
            }

            SetAudioEndpointStatusText(
                speakerEndpointStatusText,
                BuildAudioEndpointDiagnosticsFromCombo(AudioEndpointRole.SpeakerCapture, speakerEndpointCombo));
            SaveAudioEndpointBinding(AudioEndpointRole.SpeakerCapture, speakerEndpointCombo.SelectedItem as AudioEndpointChoice);
        };
        microphoneEndpointCombo.SelectionChanged += (_, _) =>
        {
            if (syncingEndpointCombos)
            {
                return;
            }

            SetAudioEndpointStatusText(
                microphoneEndpointStatusText,
                BuildAudioEndpointDiagnosticsFromCombo(AudioEndpointRole.MicrophoneRender, microphoneEndpointCombo));
            SaveAudioEndpointBinding(AudioEndpointRole.MicrophoneRender, microphoneEndpointCombo.SelectedItem as AudioEndpointChoice);
        };
        refreshButton.Click += async (_, _) =>
        {
            refreshButton.IsEnabled = false;
            try
            {
                await RefreshAudioEndpointsAsync(showHint: true);
                SyncFromMainEndpointCombos();
            }
            finally
            {
                refreshButton.IsEnabled = true;
            }
        };
        installButton.Click += async (_, _) =>
        {
            installButton.IsEnabled = false;
            try
            {
                await InstallVirtualAudioCableAsync();
                SyncFromMainEndpointCombos();
            }
            finally
            {
                installButton.IsEnabled = true;
            }
        };

        SyncFromMainEndpointCombos();

        var content = new StackPanel
        {
            MinWidth = 520,
            Spacing = 14
        };
        content.Children.Add(new TextBlock
        {
            Text = "复用旧音频端点配置。电脑声音从 Windows 输出设备 loopback 捕获；Android 麦克风写入到所选虚拟音频端点。",
            Foreground = _secondaryBrush,
            TextWrapping = TextWrapping.Wrap
        });
        AddOverviewAudioEndpointField(content, "Windows 输出 loopback 端点", speakerEndpointCombo, speakerEndpointStatusText);
        AddOverviewAudioEndpointField(content, "Android 麦克风写入端点", microphoneEndpointCombo, microphoneEndpointStatusText);
        content.Children.Add(new TextBlock
        {
            Text = "当前后端固定为立体声 / 48 kHz，环绕声和 96 kHz 暂不可用。",
            Foreground = _secondaryBrush,
            TextWrapping = TextWrapping.Wrap
        });

        var actionPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actionPanel.Children.Add(refreshButton);
        actionPanel.Children.Add(installButton);
        content.Children.Add(actionPanel);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "音频设置",
            Content = content,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    private static ComboBox CreateOverviewAudioEndpointCombo(ComboBox sourceCombo)
    {
        var comboBox = new ComboBox
        {
            DisplayMemberPath = nameof(AudioEndpointChoice.DisplayLabel),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 420
        };
        SyncOverviewAudioEndpointCombo(comboBox, sourceCombo);
        return comboBox;
    }

    private static void SyncOverviewAudioEndpointCombo(ComboBox targetCombo, ComboBox sourceCombo)
    {
        targetCombo.ItemsSource = sourceCombo.ItemsSource;
        targetCombo.SelectedItem = sourceCombo.SelectedItem;
    }

    private TextBlock CreateOverviewAudioEndpointStatusText()
    {
        return new TextBlock
        {
            Foreground = _secondaryBrush,
            TextWrapping = TextWrapping.Wrap
        };
    }

    private static void AddOverviewAudioEndpointField(
        StackPanel content,
        string label,
        ComboBox comboBox,
        TextBlock statusText)
    {
        content.Children.Add(new TextBlock { Text = label });
        content.Children.Add(comboBox);
        content.Children.Add(statusText);
    }

    private void UpdateOverviewRuntimeDiagnostics()
    {
        if (!StaticOverviewUi || OverviewDiagnosticsCpuText is null)
        {
            return;
        }

        var hostRunning = TryGetRunningHostProcess(out var hostProcess);
        UpdateOverviewHostProcessDiagnostics(hostRunning ? hostProcess : null);
        UpdateOverviewNetworkDiagnostics();
        UpdateOverviewPacketLossDiagnostics(hostRunning);
        UpdateOverviewLatencyDiagnostics(hostRunning);
        UpdateStaticDiagnosticsPage();
    }

    private bool TryGetRunningHostProcess(out Process? process)
    {
        process = _hostProcess;
        if (process is null)
        {
            return false;
        }

        try
        {
            if (process.HasExited)
            {
                process = null;
                return false;
            }

            return true;
        }
        catch
        {
            process = null;
            return false;
        }
    }

    private void UpdateOverviewHostProcessDiagnostics(Process? process)
    {
        if (process is null)
        {
            ResetHostRuntimeSampling();
            _lastOverviewCpuText = "未运行";
            _lastOverviewMemoryText = "未运行";
            OverviewDiagnosticsCpuText.Text = _lastOverviewCpuText;
            OverviewDiagnosticsCpuText.Foreground = _overviewNeutralBrush;
            OverviewDiagnosticsMemoryText.Text = _lastOverviewMemoryText;
            OverviewDiagnosticsMemoryText.Foreground = _overviewNeutralBrush;
            UpdateOverviewTrendBar(OverviewDiagnosticsCpuTrendBar, _overviewCpuSamples, 100, hasData: false);
            UpdateOverviewTrendBar(OverviewDiagnosticsMemoryTrendBar, _overviewMemorySamples, 1024, hasData: false);
            return;
        }

        try
        {
            process.Refresh();
            var processId = TryGetProcessId(process);
            var now = DateTimeOffset.UtcNow;
            var processorTime = process.TotalProcessorTime;
            double? cpuPercent = null;

            if (processId.HasValue
                && _lastHostCpuSampleProcessId == processId
                && _lastHostCpuSampleAt is { } lastSampleAt)
            {
                var elapsedSeconds = Math.Max(0, (now - lastSampleAt).TotalSeconds);
                var processorSeconds = Math.Max(0, (processorTime - _lastHostCpuSampleProcessorTime).TotalSeconds);
                if (elapsedSeconds > 0.1)
                {
                    cpuPercent = Math.Clamp(
                        processorSeconds / elapsedSeconds / Math.Max(1, Environment.ProcessorCount) * 100.0,
                        0,
                        100);
                }
            }

            _lastHostCpuSampleProcessId = processId;
            _lastHostCpuSampleProcessorTime = processorTime;
            _lastHostCpuSampleAt = now;

            if (cpuPercent.HasValue)
            {
                AppendOverviewSample(_overviewCpuSamples, cpuPercent.Value);
                _lastOverviewCpuText = $"{cpuPercent.Value:F0}%";
                OverviewDiagnosticsCpuText.Foreground = CpuBrush(cpuPercent.Value);
            }
            else
            {
                _lastOverviewCpuText = "采样中";
                OverviewDiagnosticsCpuText.Foreground = _overviewNeutralBrush;
            }

            var workingSetBytes = Math.Max(0, process.WorkingSet64);
            var memoryMb = workingSetBytes / 1024.0 / 1024.0;
            AppendOverviewSample(_overviewMemorySamples, memoryMb);
            _lastOverviewMemoryText = FormatByteSize(workingSetBytes);

            OverviewDiagnosticsCpuText.Text = _lastOverviewCpuText;
            OverviewDiagnosticsMemoryText.Text = _lastOverviewMemoryText;
            OverviewDiagnosticsMemoryText.Foreground = _successBrush;
            UpdateOverviewTrendBar(OverviewDiagnosticsCpuTrendBar, _overviewCpuSamples, 100, hasData: _overviewCpuSamples.Count > 0);
            UpdateOverviewTrendBar(
                OverviewDiagnosticsMemoryTrendBar,
                _overviewMemorySamples,
                Math.Max(256, _overviewMemorySamples.DefaultIfEmpty(memoryMb).Max()),
                hasData: _overviewMemorySamples.Count > 0);
        }
        catch
        {
            _lastOverviewCpuText = "不可读";
            _lastOverviewMemoryText = "不可读";
            OverviewDiagnosticsCpuText.Text = _lastOverviewCpuText;
            OverviewDiagnosticsCpuText.Foreground = _warningBrush;
            OverviewDiagnosticsMemoryText.Text = _lastOverviewMemoryText;
            OverviewDiagnosticsMemoryText.Foreground = _warningBrush;
        }
    }

    private void UpdateOverviewNetworkDiagnostics()
    {
        var sample = TryReadPrimaryNetworkDiagnostics();
        if (sample is null || sample.LinkSpeedBps <= 0)
        {
            _lastOverviewNetworkText = "暂无数据";
            _lastNetworkInterfaceName = sample?.Name ?? "";
            _lastNetworkLinkSpeedBps = null;
            _lastNetworkSendBps = sample?.SendBps;
            _lastNetworkReceiveBps = sample?.ReceiveBps;
            OverviewDiagnosticsNetworkText.Text = _lastOverviewNetworkText;
            OverviewDiagnosticsNetworkText.Foreground = _overviewNeutralBrush;
            UpdateOverviewFooterMachineInfo();
            return;
        }

        _lastNetworkInterfaceName = sample.Name;
        _lastNetworkLinkSpeedBps = sample.LinkSpeedBps;
        _lastNetworkSendBps = sample.SendBps;
        _lastNetworkReceiveBps = sample.ReceiveBps;
        _lastOverviewNetworkText = $"链路 {FormatBitRate(sample.LinkSpeedBps)}";
        OverviewDiagnosticsNetworkText.Text = _lastOverviewNetworkText;
        OverviewDiagnosticsNetworkText.Foreground = _successBrush;
        UpdateOverviewFooterMachineInfo();
    }

    private NetworkDiagnosticsSample? TryReadPrimaryNetworkDiagnostics()
    {
        try
        {
            var networkInterface = FindPrimaryNetworkInterface();
            if (networkInterface is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var statistics = networkInterface.GetIPv4Statistics();
            var bytesSent = Math.Max(0, statistics.BytesSent);
            var bytesReceived = Math.Max(0, statistics.BytesReceived);
            double? sendBps = null;
            double? receiveBps = null;

            if (string.Equals(_lastNetworkInterfaceId, networkInterface.Id, StringComparison.Ordinal)
                && _lastNetworkSampleAt is { } lastSampleAt)
            {
                var elapsedSeconds = Math.Max(0, (now - lastSampleAt).TotalSeconds);
                if (elapsedSeconds > 0.1)
                {
                    sendBps = Math.Max(0, bytesSent - _lastNetworkBytesSent) * 8.0 / elapsedSeconds;
                    receiveBps = Math.Max(0, bytesReceived - _lastNetworkBytesReceived) * 8.0 / elapsedSeconds;
                }
            }

            _lastNetworkInterfaceId = networkInterface.Id;
            _lastNetworkBytesSent = bytesSent;
            _lastNetworkBytesReceived = bytesReceived;
            _lastNetworkSampleAt = now;

            return new NetworkDiagnosticsSample(
                networkInterface.Id,
                networkInterface.Name,
                networkInterface.Description,
                networkInterface.Speed,
                sendBps,
                receiveBps);
        }
        catch
        {
            return null;
        }
    }

    private static NetworkInterface? FindPrimaryNetworkInterface()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsUsableNetworkInterface)
                .OrderByDescending(HasGateway)
                .ThenByDescending(networkInterface => networkInterface.Speed)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUsableNetworkInterface(NetworkInterface networkInterface)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up
            || networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        try
        {
            return networkInterface.GetIPProperties().UnicastAddresses.Any(address =>
                address.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasGateway(NetworkInterface networkInterface)
    {
        try
        {
            return networkInterface.GetIPProperties().GatewayAddresses.Any(gateway =>
                !IPAddress.Any.Equals(gateway.Address)
                && !IPAddress.IPv6Any.Equals(gateway.Address));
        }
        catch
        {
            return false;
        }
    }

    private void UpdateOverviewPacketLossDiagnostics(bool hostRunning)
    {
        if (!hostRunning)
        {
            _lastOverviewPacketLossText = "暂无数据";
            OverviewDiagnosticsPacketLossText.Text = _lastOverviewPacketLossText;
            OverviewDiagnosticsPacketLossText.Foreground = _overviewNeutralBrush;
            return;
        }

        if (_videoDiagnostics.TryGetDroppedFrameRate(out var droppedFrameRate))
        {
            _lastOverviewPacketLossText = $"丢帧 {droppedFrameRate:F1}%";
            OverviewDiagnosticsPacketLossText.Text = _lastOverviewPacketLossText;
            OverviewDiagnosticsPacketLossText.Foreground = droppedFrameRate >= 5
                ? _warningBrush
                : _successBrush;
            return;
        }

        _lastOverviewPacketLossText = "暂无数据";
        OverviewDiagnosticsPacketLossText.Text = _lastOverviewPacketLossText;
        OverviewDiagnosticsPacketLossText.Foreground = _overviewNeutralBrush;
    }

    private void UpdateOverviewLatencyDiagnostics(bool hostRunning)
    {
        if (!hostRunning)
        {
            SetOverviewLatencyUnavailable("主机未运行");
            return;
        }

        if (_videoDiagnostics.HasRecentVideoStats
            && _videoDiagnostics.RoughLatencyMs > 0)
        {
            _lastOverviewLatencyText = $"~{_videoDiagnostics.RoughLatencyMs:F0} ms";
            _lastOverviewLatencyDetailText = _videoDiagnostics.LatencyErrorBoundMs > 0
                ? $"端到端估算 ±{_videoDiagnostics.LatencyErrorBoundMs:F0} ms"
                : "端到端估算";
            SetOverviewLatencyView(_videoDiagnostics.RoughLatencyMs, _lastOverviewLatencyDetailText);
            return;
        }

        if (_videoDiagnostics.HasRecentVideoStats
            && _videoDiagnostics.LocalPipelineLatencyMs > 0)
        {
            _lastOverviewLatencyText = $"{_videoDiagnostics.LocalPipelineLatencyMs:F0} ms";
            _lastOverviewLatencyDetailText = "Android 本地链路";
            SetOverviewLatencyView(_videoDiagnostics.LocalPipelineLatencyMs, _lastOverviewLatencyDetailText);
            return;
        }

        if (IsCameraReceiving(_cameraDiagnostics) && _cameraDiagnostics.DecodeLagMs > 0)
        {
            _lastOverviewLatencyText = $"{_cameraDiagnostics.DecodeLagMs:F0} ms";
            _lastOverviewLatencyDetailText = "摄像头解码滞后";
            SetOverviewLatencyView(_cameraDiagnostics.DecodeLagMs, _lastOverviewLatencyDetailText);
            return;
        }

        SetOverviewLatencyUnavailable("等待视频统计");
    }

    private void SetOverviewLatencyView(double latencyMs, string detail)
    {
        OverviewLatencyValueText.Text = _lastOverviewLatencyText;
        OverviewLatencyDetailText.Text = detail;

        var brush = latencyMs <= 50
            ? _successBrush
            : latencyMs <= 100
                ? _warningBrush
                : _dangerBrush;
        OverviewLatencyValueText.Foreground = brush;
        OverviewLatencyDetailText.Foreground = _overviewNeutralBrush;
        OverviewLatencyStatusDot.Fill = brush;
    }

    private void SetOverviewLatencyUnavailable(string detail)
    {
        _lastOverviewLatencyText = "暂无数据";
        _lastOverviewLatencyDetailText = detail;
        OverviewLatencyValueText.Text = _lastOverviewLatencyText;
        OverviewLatencyValueText.Foreground = _overviewNeutralBrush;
        OverviewLatencyDetailText.Text = detail;
        OverviewLatencyDetailText.Foreground = _overviewNeutralBrush;
        OverviewLatencyStatusDot.Fill = _overviewMutedBrush;
    }

    private void ResetHostRuntimeSampling()
    {
        _lastHostCpuSampleProcessId = null;
        _lastHostCpuSampleProcessorTime = TimeSpan.Zero;
        _lastHostCpuSampleAt = null;
        _overviewCpuSamples.Clear();
        _overviewMemorySamples.Clear();
    }

    private static void AppendOverviewSample(Queue<double> samples, double value)
    {
        samples.Enqueue(Math.Max(0, value));
        while (samples.Count > MaxOverviewDiagnosticsSamples)
        {
            samples.Dequeue();
        }
    }

    private void UpdateOverviewTrendBar(StackPanel trendBar, Queue<double> samples, double scaleMax, bool hasData)
    {
        var bars = trendBar.Children.OfType<XamlRectangle>().ToArray();
        var values = samples.ToArray();
        var firstValueIndex = Math.Max(0, bars.Length - values.Length);
        var safeScaleMax = Math.Max(1, scaleMax);

        for (var index = 0; index < bars.Length; index++)
        {
            var bar = bars[index];
            var valueIndex = index - firstValueIndex;
            if (!hasData || valueIndex < 0 || valueIndex >= values.Length)
            {
                bar.Height = 4;
                bar.Fill = _overviewNeutralBorderBrush;
                continue;
            }

            var value = values[valueIndex];
            var ratio = Math.Clamp(value / safeScaleMax, 0, 1);
            bar.Height = 4 + (ratio * 12);
            bar.Fill = value >= 85
                ? _dangerBrush
                : value >= 65
                    ? _warningBrush
                    : _successBrush;
        }
    }

    private Brush CpuBrush(double cpuPercent)
    {
        if (cpuPercent >= 85)
        {
            return _dangerBrush;
        }

        return cpuPercent >= 65 ? _warningBrush : _successBrush;
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB";
        }

        return $"{bytes / 1024.0 / 1024.0:F0} MB";
    }

    private static string FormatBitRate(double bitsPerSecond)
    {
        if (bitsPerSecond >= 1_000_000_000)
        {
            return $"{bitsPerSecond / 1_000_000_000:F1} Gbps";
        }

        if (bitsPerSecond >= 1_000_000)
        {
            return $"{bitsPerSecond / 1_000_000:F0} Mbps";
        }

        if (bitsPerSecond >= 1_000)
        {
            return $"{bitsPerSecond / 1_000:F0} Kbps";
        }

        return $"{bitsPerSecond:F0} bps";
    }

    private async Task ShowOverviewDiagnosticsDialogAsync()
    {
        UpdateOverviewRuntimeDiagnostics();
        await ShowCopyableDetailsDialogAsync(
            "运行诊断",
            "综合当前运行指标、视频链路统计、虚拟显示器、摄像头、音频和主机日志。",
            BuildOverviewDiagnosticsReport(),
            "复制诊断");
    }

    private async Task ShowOverviewLogsDialogAsync()
    {
        var details = BuildHostLogReport();
        var hasHostLog = _currentHostLog is not null;
        await ShowCopyableDetailsDialogAsync(
            "主机日志",
            hasHostLog ? "当前或最近一次 Host stdout/stderr 日志。" : "暂无主机日志。",
            details,
            "复制日志");
    }

    private async Task ExportOverviewLogsAsync()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SideDock",
                "logs");
            Directory.CreateDirectory(directory);

            var exportPath = Path.Combine(
                directory,
                $"sidedock-host-log-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(exportPath, BuildHostLogReport(), Encoding.UTF8);

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{exportPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError("无法导出日志", ex.Message);
        }
    }

    private async Task ShowOverviewAdbPathHelpDialogAsync(string reason)
    {
        var content = new TextBlock
        {
            Text = $"无法打开文件选择器：{reason}\n\n可以直接在 ADB 路径输入框中填写 adb.exe 完整路径，或填写 Android SDK / platform-tools 所在目录。留空时会自动查找随程序打包的 adb、ANDROID_HOME、ANDROID_SDK_ROOT 和本机 Android SDK。",
            TextWrapping = TextWrapping.Wrap
        };

        var dialog = new ContentDialog
        {
            XamlRoot = StaticOverviewShell.XamlRoot,
            Title = "设置 ADB 路径",
            Content = content,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private async Task ShowUsbDebuggingHelpDialogAsync()
    {
        var readmePath = FindRepositoryReadmePath();
        var details = new StringBuilder();
        details.AppendLine("USB 调试授权检查");
        details.AppendLine();
        details.AppendLine("1. 使用 USB 连接 Android 设备。");
        details.AppendLine("2. 在设备开发者选项中打开 USB 调试。");
        details.AppendLine("3. 当设备弹出“允许 USB 调试”时选择允许。");
        details.AppendLine("4. 回到 SideDock Host 点击“刷新设备”。");
        details.AppendLine("5. 多台已授权设备同时连接时，请在连接页下拉框选择目标设备。");
        details.AppendLine();
        details.AppendLine($"README: {FormatOptional(readmePath)}");

        await ShowCopyableDetailsDialogAsync(
            "帮助文档",
            "连接页只显示真实 ADB 检测结果；未授权、离线和多设备场景都需要先在这里处理。",
            details.ToString(),
            "复制说明");
    }

    private string BuildConnectionConfigurationReport()
    {
        SyncOverviewConnectionControlsToLegacy();

        var report = new StringBuilder();
        report.AppendLine("SideDock 连接配置");
        report.AppendLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"Host 状态: {_overviewHostServiceState}");
        report.AppendLine($"ADB 状态: {_lastAdbStatusText}");
        report.AppendLine($"ADB 路径输入: {FormatOptional(OverviewAdbPathBox.Text.Trim())}");
        report.AppendLine($"ADB 实际路径: {ResolveAdbPath(OverviewAdbPathBox.Text.Trim())}");
        report.AppendLine($"已选择设备: {FormatOptional(SelectedAdbSerial())}");
        report.AppendLine($"控制端口: {FormatNumberBox(OverviewControlPortBox)}");
        report.AppendLine($"视频端口: {FormatNumberBox(OverviewVideoPortBox)}");
        report.AppendLine($"音频端口: {FormatNumberBox(OverviewAudioPortBox)}");
        report.AppendLine($"摄像头端口: {FormatNumberBox(OverviewCameraPortBox)}");
        report.AppendLine($"启用触控输入: {OverviewInputInjectionSwitch.IsOn}");
        report.AppendLine($"ADB reverse: {(_lastAdbReverseConfigured.HasValue ? _lastAdbReverseDetail : "待启动时配置")}");
        report.AppendLine();
        report.AppendLine("---- ADB 设备 ----");
        if (_lastAdbDeviceRows.Count == 0)
        {
            report.AppendLine("暂无数据。");
        }
        else
        {
            foreach (var row in _lastAdbDeviceRows)
            {
                report.AppendLine(row.RawLine);
            }
        }

        return report.ToString();
    }

    private static string? FindRepositoryReadmePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var readmePath = Path.Combine(directory.FullName, "README.md");
            if (File.Exists(readmePath))
            {
                return readmePath;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private string BuildOverviewDiagnosticsReport()
    {
        var report = new StringBuilder();
        report.AppendLine("SideDock 运行诊断报告");
        report.AppendLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine();

        report.AppendLine("---- 运行指标 ----");
        report.AppendLine($"主机进程: {FormatHostProcessState()}");
        report.AppendLine($"CPU: {_lastOverviewCpuText}");
        report.AppendLine($"内存: {_lastOverviewMemoryText}");
        report.AppendLine($"网络: {_lastOverviewNetworkText}");
        report.AppendLine($"主要网卡: {FormatOptional(_lastNetworkInterfaceName)}");
        report.AppendLine($"链路速度: {(_lastNetworkLinkSpeedBps.HasValue ? FormatBitRate(_lastNetworkLinkSpeedBps.Value) : "暂无数据")}");
        report.AppendLine($"发送速率: {(_lastNetworkSendBps.HasValue ? FormatBitRate(_lastNetworkSendBps.Value) : "暂无数据")}");
        report.AppendLine($"接收速率: {(_lastNetworkReceiveBps.HasValue ? FormatBitRate(_lastNetworkReceiveBps.Value) : "暂无数据")}");
        report.AppendLine($"丢包/丢帧: {_lastOverviewPacketLossText}");
        report.AppendLine($"延迟: {_lastOverviewLatencyText} ({_lastOverviewLatencyDetailText})");
        report.AppendLine($"ADB 状态: {_lastAdbStatusText}");
        report.AppendLine($"ADB 设备: {FormatOptional(SelectedAdbSerial())}");
        report.AppendLine();

        AppendOverviewVideoDiagnostics(report);
        report.AppendLine();
        AppendVirtualDisplayDiagnostics(report);
        report.AppendLine();

        report.AppendLine("---- 摄像头诊断报告 ----");
        report.Append(BuildCameraDiagnosticsReport());
        report.AppendLine();
        report.AppendLine("---- 音频诊断报告 ----");
        report.Append(BuildAudioDiagnosticsReport());

        return report.ToString();
    }

    private void AppendOverviewVideoDiagnostics(StringBuilder report)
    {
        report.AppendLine("---- 视频链路统计 ----");
        if (!_videoDiagnostics.HasVideoStats && !_videoDiagnostics.HasEncoderStats)
        {
            report.AppendLine("暂无视频链路统计。");
            return;
        }

        if (_videoDiagnostics.HasVideoStats)
        {
            report.AppendLine($"最近视频统计时间: {_videoDiagnostics.LastVideoStatsAt:yyyy-MM-dd HH:mm:ss zzz}");
            report.AppendLine($"Android 解码/渲染帧: {_videoDiagnostics.FramesDecoded}/{_videoDiagnostics.FramesRendered}");
            report.AppendLine($"Android 收包: {_videoDiagnostics.PacketsReceived}");
            report.AppendLine($"解码/渲染 fps: {_videoDiagnostics.DecodeFps:F1}/{_videoDiagnostics.RenderFps:F1}");
            report.AppendLine($"新帧/重复帧 fps: {_videoDiagnostics.NewFrameFps:F1}/{_videoDiagnostics.RepeatFrameFps:F1}");
            report.AppendLine($"端到端估算延迟: {(_videoDiagnostics.RoughLatencyMs > 0 ? $"{_videoDiagnostics.RoughLatencyMs:F0} ms" : "暂无数据")}");
            report.AppendLine($"Android 本地链路延迟: {(_videoDiagnostics.LocalPipelineLatencyMs > 0 ? $"{_videoDiagnostics.LocalPipelineLatencyMs:F0} ms" : "暂无数据")}");
            report.AppendLine($"延迟误差范围: {(_videoDiagnostics.LatencyErrorBoundMs > 0 ? $"±{_videoDiagnostics.LatencyErrorBoundMs:F0} ms" : "暂无数据")}");
            report.AppendLine($"解码错误/重连: {_videoDiagnostics.DecodeErrors}/{_videoDiagnostics.VideoReconnects}");
            report.AppendLine($"最近视频统计日志: {FormatOptional(_lastVideoStatsLine)}");
        }

        if (_videoDiagnostics.HasEncoderStats)
        {
            report.AppendLine($"编码生成/已编码/已发送/丢弃帧: {_videoDiagnostics.FramesGenerated}/{_videoDiagnostics.FramesEncoded}/{_videoDiagnostics.FramesSent}/{_videoDiagnostics.FramesDropped}");
            report.AppendLine($"编码输出码率: {(_videoDiagnostics.OutputKbps > 0 ? $"{_videoDiagnostics.OutputKbps:F0} kbps" : "暂无数据")}");
            report.AppendLine($"Host 本地延迟 P95: {(_videoDiagnostics.LocalLatencyP95Ms > 0 ? $"{_videoDiagnostics.LocalLatencyP95Ms:F1} ms" : "暂无数据")}");
            report.AppendLine($"最近编码统计日志: {FormatOptional(_lastEncoderStatsLine)}");
        }
    }

    private void AppendVirtualDisplayDiagnostics(StringBuilder report)
    {
        var running = IsVirtualDisplayToolRunning();
        var state = DetermineVirtualDisplayOverviewState(running, out var driverInstalled, out var toolAvailable);
        var (statusText, subtext, _) = BuildVirtualDisplayStatusView(state, driverInstalled, toolAvailable);

        report.AppendLine("---- 虚拟显示器诊断 ----");
        report.AppendLine($"状态: {statusText}");
        report.AppendLine($"详情: {subtext}");
        report.AppendLine($"驱动已安装: {driverInstalled}");
        report.AppendLine($"工具可用: {toolAvailable}");
        report.AppendLine($"工具正在运行: {running}");
        report.AppendLine($"DeviceTool 路径: {FormatOptional(_deviceToolPath)}");
        report.AppendLine($"进程名: {DeviceToolProcessName}");
        report.AppendLine($"视频源: {Selected(VideoSourceCombo)}");
        report.AppendLine($"分辨率/刷新率: {Selected(ResolutionCombo)} / {Selected(RefreshRateCombo)}fps");
        report.AppendLine($"最后错误: {FormatOptional(_virtualDisplayLastError)}");
    }

    private string BuildHostLogReport()
    {
        var hostLog = _currentHostLog;
        var recentAudioLines = SnapshotRecentAudioLogLines();
        var recentCameraLines = SnapshotRecentCameraLogLines();
        var hasRecentLines = recentAudioLines.Length > 0 || recentCameraLines.Length > 0;
        if (hostLog is null && !hasRecentLines)
        {
            return "暂无主机日志";
        }

        var report = new StringBuilder();
        report.AppendLine("SideDock 主机日志");
        report.AppendLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"主机进程: {FormatHostProcessState()}");
        report.AppendLine();

        report.AppendLine("---- Host stdout/stderr ----");
        if (hostLog is null)
        {
            report.AppendLine("暂无主机日志");
        }
        else
        {
            report.Append(hostLog.Snapshot());
        }

        report.AppendLine();
        report.AppendLine("---- 最近摄像头日志 ----");
        if (recentCameraLines.Length == 0)
        {
            report.AppendLine("(还没有捕获到 [CAMERA] 日志)");
        }
        else
        {
            foreach (var line in recentCameraLines)
            {
                report.AppendLine(line);
            }
        }

        report.AppendLine();
        report.AppendLine("---- 最近音频日志 ----");
        if (recentAudioLines.Length == 0)
        {
            report.AppendLine("(还没有捕获到 [AUDIO] 日志)");
        }
        else
        {
            foreach (var line in recentAudioLines)
            {
                report.AppendLine(line);
            }
        }

        return report.ToString();
    }

    private async Task ShowCopyableDetailsDialogAsync(
        string title,
        string summary,
        string details,
        string copyButtonText)
    {
        var summaryText = new TextBlock
        {
            Text = summary,
            TextWrapping = TextWrapping.Wrap
        };

        var detailBox = new TextBox
        {
            Text = details,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            IsSpellCheckEnabled = false,
            MinWidth = 640,
            MaxHeight = 520
        };
        ScrollViewer.SetVerticalScrollBarVisibility(detailBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(detailBox, ScrollBarVisibility.Auto);

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(summaryText);
        panel.Children.Add(detailBox);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = copyButtonText,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Resources["ContentDialogMaxWidth"] = 900.0;

        dialog.PrimaryButtonClick += (sender, args) =>
        {
            args.Cancel = true;
            try
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(details);
                Clipboard.SetContent(package);
                Clipboard.Flush();
                sender.PrimaryButtonText = "已复制";
            }
            catch
            {
                sender.PrimaryButtonText = "复制失败，请手动选择文本复制";
            }
        };

        await dialog.ShowAsync();
    }

    private static StackPanel CreateIconButtonContent(string glyph, string text)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = text });
        return content;
    }

    private static AudioEndpointDiagnostics BuildAudioEndpointDiagnosticsFromCombo(
        AudioEndpointRole role,
        ComboBox comboBox)
    {
        var endpointCount = 0;
        foreach (var item in comboBox.Items)
        {
            if (item is AudioEndpointChoice { IsBound: true, IsPresent: true })
            {
                endpointCount++;
            }
        }

        return AudioEndpointDiagnostics.FromSelection(
            role,
            comboBox.SelectedItem as AudioEndpointChoice,
            endpointCount);
    }

    private async Task RefreshAudioEndpointsAsync(bool showHint)
    {
        if (SpeakerCaptureEndpointCombo is null
            || MicrophoneRenderEndpointCombo is null
            || SpeakerCaptureEndpointStatusText is null
            || MicrophoneRenderEndpointStatusText is null)
        {
            return;
        }

        if (RefreshAudioEndpointsButton is not null)
        {
            RefreshAudioEndpointsButton.IsEnabled = false;
        }

        _audioEndpointChoicesReady = false;
        _loadingAudioEndpointChoices = true;

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                _speakerCaptureEndpointDiagnostics = AudioEndpointDiagnostics.Unsupported(AudioEndpointRole.SpeakerCapture);
                _microphoneRenderEndpointDiagnostics = AudioEndpointDiagnostics.Unsupported(AudioEndpointRole.MicrophoneRender);
                SetAudioEndpointStatusTexts();
                UpdateAudioState(showHint ? "当前系统不支持 Windows 音频端点枚举。" : null);
                return;
            }

            var renderOperation = DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
            var captureOperation = DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());
            var renderEndpoints = await renderOperation;
            var captureEndpoints = await captureOperation;
            var speakerCaptureEndpoints = ToAudioEndpointChoices(AudioEndpointRole.SpeakerCapture, renderEndpoints);
            var microphoneRenderEndpoints = ToMicrophoneEndpointChoices(captureEndpoints, renderEndpoints);

            _loadingAudioEndpointChoices = true;
            _speakerCaptureEndpointDiagnostics = ApplyAudioEndpointChoices(
                AudioEndpointRole.SpeakerCapture,
                SpeakerCaptureEndpointCombo,
                speakerCaptureEndpoints,
                _boundSpeakerCaptureEndpointId,
                _boundSpeakerCaptureEndpointName);

            _microphoneRenderEndpointDiagnostics = ApplyAudioEndpointChoices(
                AudioEndpointRole.MicrophoneRender,
                MicrophoneRenderEndpointCombo,
                microphoneRenderEndpoints,
                _boundMicrophoneRenderEndpointId,
                _boundMicrophoneRenderEndpointName);

            SetAudioEndpointStatusTexts();
            if (!DispatcherQueue.TryEnqueue(() =>
                {
                    _loadingAudioEndpointChoices = false;
                    _audioEndpointChoicesReady = true;
                }))
            {
                _loadingAudioEndpointChoices = false;
                _audioEndpointChoicesReady = true;
            }
            UpdateAudioState(showHint ? "音频端点列表已刷新。" : null);
        }
        catch (Exception ex)
        {
            _loadingAudioEndpointChoices = false;
            _speakerCaptureEndpointDiagnostics = AudioEndpointDiagnostics.EnumerationFailed(AudioEndpointRole.SpeakerCapture, ex.Message);
            _microphoneRenderEndpointDiagnostics = AudioEndpointDiagnostics.EnumerationFailed(AudioEndpointRole.MicrophoneRender, ex.Message);
            SetAudioEndpointStatusTexts();
            UpdateAudioState(showHint ? $"无法枚举 Windows 音频端点：{ex.Message}" : null);
        }
        finally
        {
            if (RefreshAudioEndpointsButton is not null)
            {
                RefreshAudioEndpointsButton.IsEnabled = true;
            }
        }
    }

    private static IReadOnlyList<AudioEndpointChoice> ToAudioEndpointChoices(
        AudioEndpointRole role,
        IEnumerable<DeviceInformation> devices)
    {
        return devices
            .Select(device => AudioEndpointChoice.FromDevice(role, device))
            .Where(choice => !string.IsNullOrWhiteSpace(choice.EndpointId))
            .OrderByDescending(choice => choice.IsEnabled)
            .ThenBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(choice => choice.EndpointId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<AudioEndpointChoice> ToMicrophoneEndpointChoices(
        IEnumerable<DeviceInformation> captureDevices,
        IEnumerable<DeviceInformation> renderDevices)
    {
        var renderEndpoints = renderDevices
            .Select(AudioEndpointCandidate.FromDevice)
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.EndpointId))
            .ToArray();

        return captureDevices
            .Select(device => AudioEndpointChoice.FromMicrophoneDevice(device, renderEndpoints))
            .Where(choice => choice is not null)
            .Select(choice => choice!)
            .OrderByDescending(choice => choice.IsEnabled)
            .ThenBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(choice => choice.EndpointId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private AudioEndpointDiagnostics ApplyAudioEndpointChoices(
        AudioEndpointRole role,
        ComboBox comboBox,
        IReadOnlyList<AudioEndpointChoice> endpoints,
        string? boundEndpointId,
        string? boundDisplayName)
    {
        var choices = new List<AudioEndpointChoice>(endpoints.Count + 2)
        {
            AudioEndpointChoice.Unbound(role)
        };
        choices.AddRange(endpoints);

        AudioEndpointChoice? selectedChoice = null;
        if (!string.IsNullOrWhiteSpace(boundEndpointId))
        {
            selectedChoice = choices.FirstOrDefault(choice =>
                string.Equals(choice.EndpointId, boundEndpointId, StringComparison.OrdinalIgnoreCase));

            if (selectedChoice is null)
            {
                selectedChoice = AudioEndpointChoice.Missing(role, boundEndpointId, boundDisplayName);
                choices.Insert(1, selectedChoice);
            }
        }

        comboBox.ItemsSource = choices;
        comboBox.SelectedItem = selectedChoice ?? choices[0];

        return AudioEndpointDiagnostics.FromSelection(role, selectedChoice, endpoints.Count);
    }

    private void SaveAudioEndpointBinding(AudioEndpointRole role, AudioEndpointChoice? choice)
    {
        if (choice is null || !choice.IsBound)
        {
            SetBoundAudioEndpoint(role, endpointId: null, displayName: null);
        }
        else if (choice.IsPresent)
        {
            SetBoundAudioEndpoint(role, choice.EndpointId, choice.DisplayName);
        }
        else
        {
            return;
        }

        SaveAudioPreferences();
        _ = RefreshAudioEndpointsAsync(showHint: false);
        UpdateAudioState(role == AudioEndpointRole.MicrophoneRender
            ? "Android 麦克风写入端点绑定已更新。"
            : "电脑声音 loopback 输出端点绑定已更新。");
    }

    private void SetBoundAudioEndpoint(AudioEndpointRole role, string? endpointId, string? displayName)
    {
        if (role == AudioEndpointRole.MicrophoneRender)
        {
            _boundMicrophoneRenderEndpointId = endpointId;
            _boundMicrophoneRenderEndpointName = displayName;
        }
        else
        {
            _boundSpeakerCaptureEndpointId = endpointId;
            _boundSpeakerCaptureEndpointName = displayName;
        }
    }

    private void SetAudioEndpointStatusTexts()
    {
        SetAudioEndpointStatusText(SpeakerCaptureEndpointStatusText, _speakerCaptureEndpointDiagnostics);
        SetAudioEndpointStatusText(MicrophoneRenderEndpointStatusText, _microphoneRenderEndpointDiagnostics);
    }

    private void SetAudioEndpointStatusText(TextBlock? textBlock, AudioEndpointDiagnostics diagnostics)
    {
        if (textBlock is null)
        {
            return;
        }

        textBlock.Text = diagnostics.Summary;
        textBlock.Foreground = diagnostics.Health switch
        {
            AudioEndpointBindingHealth.Ready => _successBrush,
            AudioEndpointBindingHealth.Unknown => _secondaryBrush,
            _ => _warningBrush
        };
    }

    private async Task RefreshAdbDevicesAsync(bool showErrors, string? resolvedAdbPath = null)
    {
        var selectedSerial = SelectedAdbSerial();
        _adbRefreshInProgress = true;
        UpdateOverviewActionButtons();
        UpdateOverviewConnectionPage();
        RefreshAdbDevicesButton.IsEnabled = false;
        RestartAdbButton.IsEnabled = false;

        try
        {
            var adbPath = resolvedAdbPath ?? ResolveAdbPath(AdbPathBox.Text.Trim());
            var devices = await RunAdbAsync(adbPath, "devices -l", TimeSpan.FromSeconds(8));
            if (devices.TimedOut)
            {
                SetAdbDeviceChoices(Array.Empty<AdbDeviceRow>(), selectedSerial);
                SetAdbStatus("ADB devices 检查超时。", _dangerBrush);
                if (showErrors)
                {
                    ShowError("无法刷新 Android 设备", "ADB devices 检查超时。");
                }

                return;
            }

            if (devices.ExitCode != 0)
            {
                SetAdbDeviceChoices(Array.Empty<AdbDeviceRow>(), selectedSerial);
                var message = string.IsNullOrWhiteSpace(devices.Stderr)
                    ? $"ADB devices 执行失败（退出码 {devices.ExitCode}）。"
                    : devices.Stderr;
                SetAdbStatus(message, _dangerBrush);
                if (showErrors)
                {
                    ShowError("无法刷新 Android 设备", message);
                }

                return;
            }

            var rows = ParseAdbDevices(devices.Stdout).ToArray();
            _lastAdbRefreshCompletedAt = DateTimeOffset.Now;
            _lastAdbReverseConfigured = null;
            SetAdbDeviceChoices(rows, selectedSerial);
            var authorizedCount = rows.Count(row => row.State.Equals("device", StringComparison.OrdinalIgnoreCase));
            var unauthorizedDevice = rows.FirstOrDefault(row =>
                row.State.Equals("unauthorized", StringComparison.OrdinalIgnoreCase));
            if (authorizedCount == 0 && unauthorizedDevice is not null)
            {
                SetAdbStatus($"检测到 Android 设备但未授权：{unauthorizedDevice.Serial}", _warningBrush);
            }
            else if (authorizedCount == 0)
            {
                SetAdbStatus("未检测到已授权 Android 设备。", _secondaryBrush);
            }
            else if (authorizedCount == 1)
            {
                var serial = rows.First(row => row.State.Equals("device", StringComparison.OrdinalIgnoreCase)).Serial;
                SetAdbStatus($"已检测到 Android 设备：{serial}", _secondaryBrush);
            }
            else if (SelectedAdbSerial() is { Length: > 0 } serial)
            {
                SetAdbStatus($"已选择 Android 设备：{serial}", _secondaryBrush);
            }
            else
            {
                SetAdbStatus($"检测到 {authorizedCount} 台 Android 设备，请选择后启动。", _secondaryBrush);
            }
        }
        catch (Exception ex)
        {
            SetAdbDeviceChoices(Array.Empty<AdbDeviceRow>(), selectedSerial);
            SetAdbStatus($"刷新 Android 设备失败：{ex.Message}", _dangerBrush);
            if (showErrors)
            {
                ShowError("无法刷新 Android 设备", ex.Message);
            }
        }
        finally
        {
            _adbRefreshInProgress = false;
            var controlsEnabled = CanUseAdbControls();
            RefreshAdbDevicesButton.IsEnabled = controlsEnabled;
            RestartAdbButton.IsEnabled = controlsEnabled;
            UpdateOverviewActionButtons();
            UpdateOverviewConnectionPage();
        }
    }

    private async Task RestartAdbAsync(bool showErrors)
    {
        if (!CanUseAdbControls())
        {
            return;
        }

        var selectedSerial = SelectedAdbSerial();
        AdbDeviceCombo.IsEnabled = false;
        RefreshAdbDevicesButton.IsEnabled = false;
        RestartAdbButton.IsEnabled = false;
        UpdateOverviewConnectionPage();

        try
        {
            var adbPath = ResolveAdbPath(AdbPathBox.Text.Trim());
            SetAdbDeviceChoices(Array.Empty<AdbDeviceRow>(), selectedSerial);
            SetAdbStatus("正在重启 ADB...", _secondaryBrush);

            var killResult = await RunAdbAsync(adbPath, "kill-server", TimeSpan.FromSeconds(8));
            if (killResult.TimedOut || killResult.ExitCode != 0)
            {
                var killedCount = TryKillAdbProcesses();
                var reason = killResult.TimedOut
                    ? "kill-server 超时"
                    : $"kill-server 退出码 {killResult.ExitCode}";
                SetAdbStatus($"{reason}，已强制结束 {killedCount} 个 adb.exe，正在启动 ADB...", _warningBrush);
            }

            await Task.Delay(500);

            var startResult = await RunAdbAsync(adbPath, "start-server", TimeSpan.FromSeconds(12));
            if (startResult.TimedOut)
            {
                SetAdbStatus("ADB start-server 超时。", _dangerBrush);
                if (showErrors)
                {
                    ShowError("无法重启 ADB", "ADB start-server 超时。");
                }

                return;
            }

            if (startResult.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(startResult.Stderr)
                    ? $"ADB start-server 执行失败（退出码 {startResult.ExitCode}）。"
                    : startResult.Stderr;
                SetAdbStatus(message, _dangerBrush);
                if (showErrors)
                {
                    ShowError("无法重启 ADB", message);
                }

                return;
            }

            SetAdbStatus("ADB 已重启，正在刷新 Android 设备...", _successBrush);
            await RefreshAdbDevicesAsync(showErrors, adbPath);
        }
        catch (Exception ex)
        {
            SetAdbDeviceChoices(Array.Empty<AdbDeviceRow>(), selectedSerial);
            SetAdbStatus($"重启 ADB 失败：{ex.Message}", _dangerBrush);
            if (showErrors)
            {
                ShowError("无法重启 ADB", ex.Message);
            }
        }
        finally
        {
            var controlsEnabled = CanUseAdbControls();
            AdbDeviceCombo.IsEnabled = controlsEnabled;
            RefreshAdbDevicesButton.IsEnabled = controlsEnabled;
            RestartAdbButton.IsEnabled = controlsEnabled;
            UpdateOverviewConnectionPage();
        }
    }

    private bool CanUseAdbControls()
    {
        return StartHostButton.IsEnabled && _hostProcess is not { HasExited: false };
    }

    private void SetAdbDeviceChoices(IReadOnlyList<AdbDeviceRow> rows, string? selectedSerial)
    {
        _lastAdbDeviceRows = rows.ToArray();
        var choices = new List<AdbDeviceChoice>
        {
            new(null, "自动选择（仅一台设备时）", string.Empty, string.Empty)
        };

        choices.AddRange(rows.Select(row => new AdbDeviceChoice(
            row.Serial,
            FormatAdbDeviceDisplayName(row),
            row.State,
            row.RawLine)));

        var selectedChoice = choices.FirstOrDefault(choice =>
            !string.IsNullOrWhiteSpace(choice.Serial) &&
            choice.Serial.Equals(selectedSerial, StringComparison.OrdinalIgnoreCase));
        if (selectedChoice is null)
        {
            var authorizedChoices = choices
                .Where(choice => choice.State.Equals("device", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            selectedChoice = authorizedChoices.Length == 1 ? authorizedChoices[0] : choices[0];
        }

        _syncingAdbDeviceSelection = true;
        try
        {
            AdbDeviceCombo.ItemsSource = choices;
            OverviewConnectionAdbDeviceCombo.ItemsSource = choices;
            AdbDeviceCombo.SelectedItem = selectedChoice;
            OverviewConnectionAdbDeviceCombo.SelectedItem = selectedChoice;
        }
        finally
        {
            _syncingAdbDeviceSelection = false;
        }

        UpdateAudioState();
        UpdateOverviewAndroidDeviceState();
        UpdateOverviewConnectionPage();
    }

    private string? SelectedAdbSerial()
    {
        return SelectedAdbDeviceChoice() is { Serial.Length: > 0 } choice
            ? choice.Serial
            : null;
    }

    private AdbDeviceChoice? SelectedAdbDeviceChoice()
    {
        if (AdbDeviceCombo.SelectedItem is AdbDeviceChoice choice)
        {
            return choice;
        }

        return OverviewConnectionAdbDeviceCombo.SelectedItem as AdbDeviceChoice;
    }

    private AdbDeviceRow? SelectedAdbDeviceRow()
    {
        var selectedSerial = SelectedAdbSerial();
        return string.IsNullOrWhiteSpace(selectedSerial)
            ? null
            : _lastAdbDeviceRows.FirstOrDefault(row =>
                row.Serial.Equals(selectedSerial, StringComparison.OrdinalIgnoreCase));
    }

    private void SyncAdbDeviceSelectionFrom(ComboBox source)
    {
        if (_syncingAdbDeviceSelection)
        {
            return;
        }

        var selectedChoice = source.SelectedItem as AdbDeviceChoice;
        _syncingAdbDeviceSelection = true;
        try
        {
            if (!ReferenceEquals(source, AdbDeviceCombo))
            {
                AdbDeviceCombo.SelectedItem = FindAdbDeviceChoice(AdbDeviceCombo, selectedChoice?.Serial)
                    ?? AdbDeviceCombo.Items.OfType<AdbDeviceChoice>().FirstOrDefault();
            }

            if (!ReferenceEquals(source, OverviewConnectionAdbDeviceCombo))
            {
                OverviewConnectionAdbDeviceCombo.SelectedItem = FindAdbDeviceChoice(
                    OverviewConnectionAdbDeviceCombo,
                    selectedChoice?.Serial)
                    ?? OverviewConnectionAdbDeviceCombo.Items.OfType<AdbDeviceChoice>().FirstOrDefault();
            }

            var selectedSerial = selectedChoice?.Serial;
            if (OverviewConnectionDeviceListView.ItemsSource is IEnumerable<OverviewConnectionDeviceItem> items)
            {
                OverviewConnectionDeviceListView.SelectedItem = items.FirstOrDefault(item =>
                    !string.IsNullOrWhiteSpace(selectedSerial)
                    && item.Serial.Equals(selectedSerial, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _syncingAdbDeviceSelection = false;
        }

        _lastAdbReverseConfigured = null;
        UpdateAudioState();
        UpdateOverviewAndroidDeviceState();
        UpdateOverviewConnectionPage();
    }

    private static AdbDeviceChoice? FindAdbDeviceChoice(ComboBox comboBox, string? serial)
    {
        foreach (var item in comboBox.Items.OfType<AdbDeviceChoice>())
        {
            if (string.IsNullOrWhiteSpace(serial))
            {
                if (string.IsNullOrWhiteSpace(item.Serial))
                {
                    return item;
                }
            }
            else if (item.Serial?.Equals(serial, StringComparison.OrdinalIgnoreCase) == true)
            {
                return item;
            }
        }

        return null;
    }

    private static string FormatAdbDeviceDisplayName(AdbDeviceRow row)
    {
        var model = TryGetAdbDetail(row.RawLine, "model")?.Replace('_', ' ');
        var product = TryGetAdbDetail(row.RawLine, "product")?.Replace('_', ' ');
        var label = FirstNonEmpty(model, product, row.Serial);
        var stateSuffix = row.State.Equals("device", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" [{row.State}]";

        return label.Equals(row.Serial, StringComparison.OrdinalIgnoreCase)
            ? $"{row.Serial}{stateSuffix}"
            : $"{label} - {row.Serial}{stateSuffix}";
    }

    private static string? TryGetAdbDetail(string rawLine, string key)
    {
        var prefix = key + ":";
        foreach (var part in rawLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return part[prefix.Length..];
            }
        }

        return null;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string Selected(ComboBox comboBox)
    {
        if (comboBox.SelectedItem is not ComboBoxItem item)
        {
            return string.Empty;
        }

        return item.Tag?.ToString() ?? item.Content?.ToString() ?? string.Empty;
    }

    private static string Port(NumberBox numberBox, string name)
    {
        return PortNumber(numberBox, name).ToString(CultureInfo.InvariantCulture);
    }

    private static int PortNumber(NumberBox numberBox, string name)
    {
        if (double.IsNaN(numberBox.Value))
        {
            throw new InvalidOperationException($"{name} 端口无效。");
        }

        var port = (int)numberBox.Value;
        if (port < 1 || port > 65535)
        {
            throw new InvalidOperationException($"{name} 端口无效: {port}");
        }

        return port;
    }

    private static string QuoteArgument(string argument)
    {
        return argument.Contains(' ') || argument.Contains('"')
            ? "\"" + argument.Replace("\"", "\\\"") + "\""
            : argument;
    }

    private static string ResolveAdbPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return ResolveExplicitAdbPath(configuredPath);
        }

        foreach (var adbPath in EnumerateBundledAdbCandidates())
        {
            if (File.Exists(adbPath))
            {
                return adbPath;
            }
        }

        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android",
                "Sdk")
        };

        foreach (var sdkRoot in candidates)
        {
            if (string.IsNullOrWhiteSpace(sdkRoot))
            {
                continue;
            }

            var adbPath = Path.Combine(sdkRoot, "platform-tools", AdbExe);
            if (File.Exists(adbPath))
            {
                return adbPath;
            }
        }

        return "adb";
    }

    private static string ResolveExplicitAdbPath(string configuredPath)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
        if (Directory.Exists(expandedPath))
        {
            foreach (var adbPath in EnumerateAdbCandidatesFromRoot(expandedPath))
            {
                if (File.Exists(adbPath))
                {
                    return adbPath;
                }
            }
        }

        return expandedPath;
    }

    private static IEnumerable<string> EnumerateBundledAdbCandidates()
    {
        var roots = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty),
            Environment.CurrentDirectory
        };

        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var adbPath in EnumerateAdbCandidatesFromRoot(root!))
            {
                yield return adbPath;
            }
        }
    }

    private static IEnumerable<string> EnumerateAdbCandidatesFromRoot(string root)
    {
        yield return Path.Combine(root, "platform-tools", "win-x64", AdbExe);
        yield return Path.Combine(root, "platform-tools", AdbExe);
        yield return Path.Combine(root, AdbExe);
    }

    private async Task<AdbReversePreflight> ConfigureAdbReverseBeforeHostStartAsync(
        string adbPath,
        IReadOnlyList<int> ports,
        string? selectedSerial)
    {
        var report = new StringBuilder();
        report.AppendLine("SideDock ADB reverse 诊断报告");
        report.AppendLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"ADB 路径: {adbPath}");
        report.AppendLine($"选择设备: {FormatOptional(selectedSerial)}");
        report.AppendLine($"端口: {string.Join(", ", ports)}");
        report.AppendLine();

        var devices = await RunAdbAsync(adbPath, "devices -l", TimeSpan.FromSeconds(8));
        AppendAdbCommand(report, adbPath, "devices -l", devices);
        if (devices.TimedOut)
        {
            return new AdbReversePreflight(false, "ADB devices 检查超时，未建立 reverse。", report.ToString(), null);
        }

        if (devices.ExitCode != 0)
        {
            return new AdbReversePreflight(false, $"ADB devices 执行失败（退出码 {devices.ExitCode}），未建立 reverse。", report.ToString(), null);
        }

        var deviceRows = ParseAdbDevices(devices.Stdout).ToArray();
        var authorizedDevices = deviceRows
            .Where(row => row.State.Equals("device", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (authorizedDevices.Length == 0)
        {
            var summary = deviceRows.Any(row => row.State.Equals("unauthorized", StringComparison.OrdinalIgnoreCase))
                ? "Android 设备未授权 USB 调试，ADB reverse 没有建立。请在设备上允许 USB 调试后重新启动主机。"
                : "未检测到已授权 Android 设备，ADB reverse 没有建立。请连接 USB 并开启 USB 调试后重新启动主机。";
            return new AdbReversePreflight(false, summary, report.ToString(), null);
        }

        AdbDeviceRow selectedDevice;
        if (!string.IsNullOrWhiteSpace(selectedSerial))
        {
            var selectedRows = deviceRows
                .Where(row => row.Serial.Equals(selectedSerial, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (selectedRows.Length == 0)
            {
                return new AdbReversePreflight(false, $"未检测到已选择的 ADB 设备 {selectedSerial}，请刷新设备列表后重试。", report.ToString(), selectedSerial);
            }

            if (!selectedRows[0].State.Equals("device", StringComparison.OrdinalIgnoreCase))
            {
                return new AdbReversePreflight(false, $"已选择的 ADB 设备 {selectedSerial} 当前状态为 {selectedRows[0].State}，无法配置 reverse。", report.ToString(), selectedSerial);
            }

            selectedDevice = selectedRows[0];
        }
        else if (authorizedDevices.Length > 1)
        {
            return new AdbReversePreflight(
                false,
                "检测到多个已授权 ADB 设备，请在 Android 设备下拉框中选择一台后再启动主机。",
                report.ToString(),
                null);
        }
        else
        {
            selectedDevice = authorizedDevices[0];
        }

        var serial = selectedDevice.Serial;
        foreach (var port in ports.Distinct())
        {
            var arguments = $"-s {QuoteArgument(serial)} reverse tcp:{port} tcp:{port}";
            var reverse = await RunAdbAsync(adbPath, arguments, TimeSpan.FromSeconds(8));
            AppendAdbCommand(report, adbPath, arguments, reverse);
            if (reverse.TimedOut)
            {
                return new AdbReversePreflight(false, $"ADB reverse tcp:{port} 配置超时。", report.ToString(), serial);
            }

            if (reverse.ExitCode != 0)
            {
                return new AdbReversePreflight(false, $"ADB reverse tcp:{port} 配置失败（退出码 {reverse.ExitCode}）。", report.ToString(), serial);
            }
        }

        var listArguments = $"-s {QuoteArgument(serial)} reverse --list";
        var reverseList = await RunAdbAsync(adbPath, listArguments, TimeSpan.FromSeconds(8));
        AppendAdbCommand(report, adbPath, listArguments, reverseList);

        var summaryPorts = string.Join("/", ports.Distinct().Select(port => $"tcp:{port}"));
        return new AdbReversePreflight(true, $"ADB reverse 已配置：{serial} {summaryPorts}", report.ToString(), serial);
    }

    private static async Task<AdbCommandResult> RunAdbAsync(string adbPath, string arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(adbPath, arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new AdbCommandResult(-1, string.Empty, "无法启动 adb", TimedOut: false);
            }

            using var timeoutCts = new CancellationTokenSource(timeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return new AdbCommandResult(-1, string.Empty, "adb command timed out", TimedOut: true);
            }

            return new AdbCommandResult(process.ExitCode, (await stdoutTask).Trim(), (await stderrTask).Trim(), TimedOut: false);
        }
        catch (Exception ex)
        {
            return new AdbCommandResult(-1, string.Empty, ex.Message, TimedOut: false);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup after an adb timeout.
        }
    }

    private static int TryKillAdbProcesses()
    {
        var killedCount = 0;
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("adb");
        }
        catch
        {
            return killedCount;
        }

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    killedCount++;
                }
            }
            catch
            {
                // Best effort cleanup when adb server is wedged.
            }
            finally
            {
                process.Dispose();
            }
        }

        return killedCount;
    }

    private static IEnumerable<AdbDeviceRow> ParseAdbDevices(string stdout)
    {
        foreach (var rawLine in stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) ||
                line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                yield return new AdbDeviceRow(parts[0], parts[1], line);
            }
        }
    }

    private static void AppendAdbCommand(StringBuilder report, string adbPath, string arguments, AdbCommandResult result)
    {
        report.AppendLine($"> {adbPath} {arguments}");
        report.AppendLine($"exitCode={result.ExitCode}, timedOut={result.TimedOut}");
        if (!string.IsNullOrWhiteSpace(result.Stdout))
        {
            report.AppendLine("stdout:");
            report.AppendLine(result.Stdout);
        }

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            report.AppendLine("stderr:");
            report.AppendLine(result.Stderr);
        }

        report.AppendLine();
    }

    private string ResolveHostPath()
    {
        foreach (var candidate in EnumerateHostCandidates())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var extracted = TryExtractHostPayload();
        if (!string.IsNullOrWhiteSpace(extracted))
        {
            return extracted;
        }

        throw new FileNotFoundException(
            $"未找到 {HostExe}。请先构建 SideDock.Host，或把 HostPayload.zip 打进桌面端包。");
    }

    private string TryExtractHostPayload()
    {
        var resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(".HostPayload.zip", StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return string.Empty;
        }

        _payloadRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideDock",
            "HostApp",
            GetBuildKey());

        var hostPath = FindExtractedExecutable(_payloadRoot, HostExe);
        if (!string.IsNullOrWhiteSpace(hostPath))
        {
            return hostPath;
        }

        Directory.CreateDirectory(_payloadRoot);
        var zipPath = Path.Combine(_payloadRoot, "HostPayload.zip");

        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded host payload stream was not found."))
        using (var output = File.Create(zipPath))
        {
            resource.CopyTo(output);
        }

        ZipFile.ExtractToDirectory(zipPath, _payloadRoot, overwriteFiles: true);
        File.Delete(zipPath);

        return FindExtractedExecutable(_payloadRoot, HostExe);
    }

    private IEnumerable<string> EnumerateHostCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, HostExe);
        yield return Path.Combine(baseDirectory, "SideDock.Host", HostExe);
        yield return Path.Combine(baseDirectory, "SideDock.Host", "x64", "Release", HostExe);
        yield return Path.Combine(baseDirectory, "SideDock.Host", "x64", "Debug", HostExe);
        yield return Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "SideDock.Host",
            "bin",
            "Debug",
            "net8.0",
            HostExe));
        yield return Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "SideDock.Host",
            "bin",
            "Release",
            "net8.0",
            HostExe));

        if (_payloadRoot is not null)
        {
            yield return Path.Combine(_payloadRoot, HostExe);
            yield return Path.Combine(_payloadRoot, "SideDock.Host", HostExe);
            yield return Path.Combine(_payloadRoot, "SideDock.Host", "x64", "Release", HostExe);
            yield return Path.Combine(_payloadRoot, "SideDock.Host", "x64", "Debug", HostExe);
        }

        yield return Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "windows-host",
            "SideDock.Host",
            "bin",
            "Debug",
            "net8.0",
            HostExe));
        yield return Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "windows-host",
            "SideDock.Host",
            "bin",
            "Release",
            "net8.0",
            HostExe));
    }

    private void StopHost()
    {
        var process = _hostProcess;
        try
        {
            if (process is not null && !process.HasExited)
            {
                _hostStopRequestedProcessId = TryGetProcessId(process);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
        }
        finally
        {
            if (ReferenceEquals(_hostProcess, process))
            {
                _hostProcess = null;
            }

            StopHostOwnedVirtualDisplay();
            SetRunningState(false);
        }
    }

    private bool StartVirtualDisplay(bool showCopyableError = true, string failureAction = "启动虚拟显示器失败")
    {
        if (IsVirtualDisplayToolRunning())
        {
            _virtualDisplayLastError = null;
            RefreshVirtualDisplayState();
            return true;
        }

        int? exitCode = null;
        DeviceToolDiagnostics? diagnostics = null;

        try
        {
            _deviceToolPath ??= ResolveDeviceToolPath();
            var workingDirectory = Path.GetDirectoryName(_deviceToolPath) ?? Environment.CurrentDirectory;
            var startInfo = new ProcessStartInfo
            {
                FileName = _deviceToolPath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            _deviceToolProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Unable to start {DeviceToolExe}.");

            if (_deviceToolProcess.WaitForExit(2500))
            {
                exitCode = _deviceToolProcess.ExitCode;
                _deviceToolProcess = null;
                diagnostics = CaptureDeviceToolDiagnostics(_deviceToolPath, workingDirectory);
                throw new InvalidOperationException($"{DeviceToolExe} exited with code {exitCode}.");
            }

            SaveVirtualDisplayLog(BuildVirtualDisplayStartupDetails());
            _virtualDisplayLastError = null;
            return true;
        }
        catch (Exception ex)
        {
            _virtualDisplayLastError = ex.Message;
            InvalidateVirtualDisplayAvailabilityCaches();
            var details = BuildVirtualDisplayFailureDetails(ex, exitCode, diagnostics);
            SaveVirtualDisplayLog(details);

            if (showCopyableError)
            {
                ShowErrorWithDetails(
                    "无法启动虚拟显示器",
                    $"{failureAction}：{ex.Message}。下面是详细诊断信息，点“复制详情”可一键复制。",
                    details);
            }
            else
            {
                ShowError("无法启动虚拟显示器", ex.Message);
            }

            return false;
        }
        finally
        {
            RefreshVirtualDisplayState();
        }
    }

    private void StopVirtualDisplay()
    {
        var processes = GetVirtualDisplayToolProcesses();
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        _deviceToolProcess = null;
        _virtualDisplayLastError = null;
        RefreshVirtualDisplayState();
    }

    private void StopHostOwnedVirtualDisplay()
    {
        if (!_hostOwnsVirtualDisplay)
        {
            return;
        }

        StopVirtualDisplay();
        _hostOwnsVirtualDisplay = false;
    }

    private string ResolveDeviceToolPath()
    {
        foreach (var candidate in EnumerateDeviceToolCandidates())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        foreach (var root in EnumerateDeviceToolSearchRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var match = Directory.GetFiles(root, DeviceToolExe, SearchOption.AllDirectories)
                .OrderByDescending(path => path.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => path.Length)
                .FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        throw new FileNotFoundException(
            $"未找到 {DeviceToolExe}。请先安装 SideDock 驱动包，或先构建 SideDock.Idd.DeviceTool。");
    }

    private void SetDriverInstallButtonsEnabled(bool enabled)
    {
        InstallDriverButton.IsEnabled = enabled;
        OverviewInstallDriverButton.IsEnabled = enabled && !_virtualDisplayOperationInProgress;
    }

    private void SetDriverInstallStatus(string text)
    {
        DriverInstallStatusText.Text = text;
        OverviewVirtualDisplayHintText.Text = text;
    }

    private void InvalidateVirtualDisplayAvailabilityCaches()
    {
        _virtualDisplayDriverInstalledCache = null;
        _virtualDisplayToolAvailableCache = null;
        _virtualDisplayDriverInstalledCheckedAt = DateTimeOffset.MinValue;
        _virtualDisplayToolAvailableCheckedAt = DateTimeOffset.MinValue;
    }

    private async Task<bool> InstallDriverAsync()
    {
        if (_driverInstallInProgress)
        {
            return false;
        }

        _driverInstallInProgress = true;
        _driverInstallLastError = null;
        _virtualDisplayTransientState = VirtualDisplayOverviewState.DriverInstalling;
        SetDriverInstallButtonsEnabled(false);
        SetDriverInstallStatus("正在启动驱动安装器，请在管理员权限弹窗中选择“是”。");
        RefreshVirtualDisplayState();
        var succeeded = false;

        // The installer runs elevated (runas), so we cannot capture its stdout/stderr
        // directly. Instead we hand it a result-file path; it writes a full diagnostic
        // report there that we read back after it exits.
        var reportPath = BuildDriverInstallLogPath();

        try
        {
            _driverInstallerPath ??= ResolveDriverInstallerPath();
            var startInfo = new ProcessStartInfo
            {
                FileName = _driverInstallerPath,
                Arguments = $"--from-app --result {QuoteArgument(reportPath)}",
                WorkingDirectory = Path.GetDirectoryName(_driverInstallerPath) ?? Environment.CurrentDirectory,
                UseShellExecute = true,
                Verb = "runas"
            };

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"无法启动 {DriverInstallerExe}。");

            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                _deviceToolPath = null;
                _virtualDisplayLastError = null;
                InvalidateVirtualDisplayAvailabilityCaches();
                SetDriverInstallStatus("驱动安装流程已完成。若显示器没有立即出现，请打开虚拟显示器开关。");
                TryDeleteFile(reportPath);
                succeeded = true;
            }
            else
            {
                _driverInstallLastError = $"安装器退出码 {process.ExitCode}";
                SetDriverInstallStatus($"驱动安装未完成（退出码 {process.ExitCode}）。点开详情可一键复制。");
                var report = TryReadFile(reportPath);
                var summary = $"安装器以退出码 {process.ExitCode} 结束。下面是详细诊断信息，点“复制详情”可一键复制后发给开发者排查。";
                var details = !string.IsNullOrWhiteSpace(report)
                    ? $"日志文件: {reportPath}{Environment.NewLine}{Environment.NewLine}{report}"
                    : $"安装器以退出码 {process.ExitCode} 结束，但未生成诊断报告（{reportPath} 不存在）。{Environment.NewLine}"
                      + $"可能原因：驱动安装器版本过旧，或无法写入日志目录。{Environment.NewLine}"
                      + $"安装器路径: {_driverInstallerPath}";
                ShowErrorWithDetails("驱动安装未完成", summary, details);
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _driverInstallLastError = "用户取消了管理员权限授权";
            SetDriverInstallStatus("驱动安装已取消（未授予管理员权限）。");
            ShowError("驱动安装已取消", "你在管理员权限弹窗中选择了“否”，安装未开始。请重新点击“安装/修复驱动”，并在弹窗中选择“是”。");
        }
        catch (Exception ex)
        {
            _driverInstallLastError = ex.Message;
            SetDriverInstallStatus("驱动安装未完成。");
            var report = TryReadFile(reportPath);
            var details = !string.IsNullOrWhiteSpace(report)
                ? $"日志文件: {reportPath}{Environment.NewLine}{Environment.NewLine}{report}"
                : ex.ToString();
            ShowErrorWithDetails("无法安装驱动", $"启动或执行驱动安装器时出错：{ex.Message}", details);
        }
        finally
        {
            _driverInstallInProgress = false;
            _virtualDisplayTransientState = null;
            SetDriverInstallButtonsEnabled(true);
            InvalidateVirtualDisplayAvailabilityCaches();
            RefreshVirtualDisplayState();
        }

        return succeeded;
    }

    private async Task InstallVirtualAudioCableAsync()
    {
        InstallVirtualAudioCableButton.IsEnabled = false;
        VirtualAudioCableInstallStatusText.Text = "正在启动 VB-CABLE 安装器，请在管理员权限弹窗中选择“是”。";

        try
        {
            _virtualAudioCableSetupPath ??= ResolveVirtualAudioCableSetupPath();
            var startInfo = new ProcessStartInfo
            {
                FileName = _virtualAudioCableSetupPath,
                WorkingDirectory = Path.GetDirectoryName(_virtualAudioCableSetupPath) ?? Environment.CurrentDirectory,
                UseShellExecute = true,
                Verb = "runas"
            };

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(_virtualAudioCableSetupPath)}。");

            await process.WaitForExitAsync();
            _virtualAudioCableSetupPath = null;

            await RefreshAudioEndpointsAsync(showHint: false);

            if (process.ExitCode == 0)
            {
                VirtualAudioCableInstallStatusText.Text = "VB-CABLE 安装器已关闭。若系统提示重启，请重启后再刷新端点并选择 CABLE Output。";
            }
            else
            {
                VirtualAudioCableInstallStatusText.Text = $"VB-CABLE 安装器已退出（退出码 {process.ExitCode}）。如果端点未出现，请重新运行安装或重启电脑。";
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            VirtualAudioCableInstallStatusText.Text = "VB-CABLE 安装已取消（未授予管理员权限）。";
            ShowError("VB-CABLE 安装已取消", "你在管理员权限弹窗中选择了“否”，安装未开始。请重新点击“安装/修复虚拟音频线”，并在弹窗中选择“是”。");
        }
        catch (Exception ex)
        {
            VirtualAudioCableInstallStatusText.Text = "VB-CABLE 安装未完成。";
            ShowErrorWithDetails(
                "无法安装 VB-CABLE",
                $"启动或执行 VB-CABLE 安装器时出错：{ex.Message}",
                ex.ToString());
        }
        finally
        {
            InstallVirtualAudioCableButton.IsEnabled = true;
        }
    }

    private string ResolveDriverInstallerPath()
    {
        foreach (var candidate in EnumerateDriverInstallerCandidates())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var extracted = TryExtractDriverInstallerPayload();
        if (!string.IsNullOrWhiteSpace(extracted))
        {
            return extracted;
        }

        throw new FileNotFoundException(
            $"未找到 {DriverInstallerExe}。请使用包含驱动安装器的 SideDock 桌面端发布包。");
    }

    private string ResolveVirtualAudioCableSetupPath()
    {
        foreach (var candidate in EnumerateVirtualAudioCableSetupCandidates())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var extracted = TryExtractVirtualAudioCablePayload();
        if (!string.IsNullOrWhiteSpace(extracted))
        {
            return extracted;
        }

        throw new FileNotFoundException(
            $"未找到 {VirtualAudioCableSetupExe()}。请使用包含 VB-CABLE payload 的 SideDock 桌面端发布包。");
    }

    private string TryExtractDriverInstallerPayload()
    {
        var resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(".DriverInstallerPayload.zip", StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return string.Empty;
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideDock",
            "DriverInstaller",
            GetBuildKey());

        var driverInstallerPath = FindExtractedExecutable(root, DriverInstallerExe);
        if (!string.IsNullOrWhiteSpace(driverInstallerPath))
        {
            return driverInstallerPath;
        }

        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "DriverInstallerPayload.zip");

        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("未找到内置驱动安装器资源流。"))
        using (var output = File.Create(zipPath))
        {
            resource.CopyTo(output);
        }

        ZipFile.ExtractToDirectory(zipPath, root, overwriteFiles: true);
        File.Delete(zipPath);

        return FindExtractedExecutable(root, DriverInstallerExe);
    }

    private string TryExtractVirtualAudioCablePayload()
    {
        var resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith($".{VirtualAudioCablePayloadZip}", StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return string.Empty;
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideDock",
            "VirtualAudioCable",
            GetBuildKey());

        var setupPath = FindVirtualAudioCableSetupExecutable(root);
        if (!string.IsNullOrWhiteSpace(setupPath))
        {
            return setupPath;
        }

        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, VirtualAudioCablePayloadZip);

        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("未找到内置 VB-CABLE 资源流。"))
        using (var output = File.Create(zipPath))
        {
            resource.CopyTo(output);
        }

        ZipFile.ExtractToDirectory(zipPath, root, overwriteFiles: true);
        File.Delete(zipPath);

        return FindVirtualAudioCableSetupExecutable(root);
    }

    private static string FindExtractedExecutable(string root, string executableName)
    {
        if (!Directory.Exists(root))
        {
            return string.Empty;
        }

        var directPath = Path.Combine(root, executableName);
        if (File.Exists(directPath))
        {
            return directPath;
        }

        return Directory.GetFiles(root, executableName, SearchOption.AllDirectories).FirstOrDefault()
            ?? string.Empty;
    }

    private static string FindVirtualAudioCableSetupExecutable(string root)
    {
        if (!Directory.Exists(root))
        {
            return string.Empty;
        }

        var executableName = VirtualAudioCableSetupExe();
        var directPath = Path.Combine(root, executableName);
        if (File.Exists(directPath))
        {
            return directPath;
        }

        return Directory.GetFiles(root, executableName, SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .FirstOrDefault()
            ?? string.Empty;
    }

    private static string VirtualAudioCableSetupExe()
    {
        return Environment.Is64BitOperatingSystem
            ? VirtualAudioCableSetupX64Exe
            : VirtualAudioCableSetupX86Exe;
    }

    private static string GetBuildKey()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            var info = new FileInfo(processPath);
            return $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}";
        }

        return "unknown";
    }

    private IEnumerable<string> EnumerateDriverInstallerCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, DriverInstallerExe);
        yield return Path.Combine(baseDirectory, "SideDock.Driver.Installer", DriverInstallerExe);

        if (_payloadRoot is not null)
        {
            yield return Path.Combine(_payloadRoot, DriverInstallerExe);
            yield return Path.Combine(_payloadRoot, "SideDock.Driver.Installer", DriverInstallerExe);
        }

        yield return Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "windows-driver",
            "SideDock.Driver.Installer",
            "bin",
            "Release",
            "net8.0-windows",
            "win-x64",
            "publish",
            DriverInstallerExe));
    }

    private IEnumerable<string> EnumerateVirtualAudioCableSetupCandidates()
    {
        var setupExe = VirtualAudioCableSetupExe();
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, setupExe);
        yield return Path.Combine(baseDirectory, "VBCABLE_Driver_Pack45", setupExe);
        yield return Path.Combine(baseDirectory, "VB-CABLE", setupExe);
        yield return Path.Combine(baseDirectory, "VirtualAudioCable", setupExe);

        if (_payloadRoot is not null)
        {
            yield return Path.Combine(_payloadRoot, setupExe);
            yield return Path.Combine(_payloadRoot, "VBCABLE_Driver_Pack45", setupExe);
            yield return Path.Combine(_payloadRoot, "VB-CABLE", setupExe);
            yield return Path.Combine(_payloadRoot, "VirtualAudioCable", setupExe);
        }

        yield return Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "third_party",
            "vb-cable",
            "VBCABLE_Driver_Pack45",
            setupExe));
        yield return Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "third_party",
            "vb-cable",
            "VBCABLE_Driver_Pack45",
            setupExe));
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "VBCABLE_Driver_Pack45",
            setupExe);
    }

    private IEnumerable<string> EnumerateDeviceToolCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, DeviceToolExe);
        yield return Path.Combine(baseDirectory, "SideDock.Idd.DeviceTool", DeviceToolExe);
        yield return Path.Combine(baseDirectory, "SideDock.Idd.DeviceTool", "x64", "Release", DeviceToolExe);
        yield return Path.Combine(baseDirectory, "SideDock.Idd.DeviceTool", "x64", "Debug", DeviceToolExe);

        if (_payloadRoot is not null)
        {
            yield return Path.Combine(_payloadRoot, DeviceToolExe);
            yield return Path.Combine(_payloadRoot, "SideDock.Idd.DeviceTool", DeviceToolExe);
            yield return Path.Combine(_payloadRoot, "SideDock.Idd.DeviceTool", "x64", "Release", DeviceToolExe);
            yield return Path.Combine(_payloadRoot, "SideDock.Idd.DeviceTool", "x64", "Debug", DeviceToolExe);
        }

        yield return Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "windows-driver",
            "SideDock.Idd.DeviceTool",
            "x64",
            "Release",
            DeviceToolExe));
        yield return Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "windows-driver",
            "SideDock.Idd.DeviceTool",
            "x64",
            "Debug",
            DeviceToolExe));
    }

    private IEnumerable<string> EnumerateDeviceToolSearchRoots()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SideDock",
            "Driver",
            "payload");

        if (_payloadRoot is not null)
        {
            yield return _payloadRoot;
        }
    }

    private bool ShouldManageVirtualDisplayWithHost()
    {
        return ManageDisplaySwitch.IsOn && RequiresVirtualDisplay();
    }

    private bool RequiresVirtualDisplay()
    {
        var videoSource = Selected(VideoSourceCombo);
        return videoSource.Equals("idd", StringComparison.OrdinalIgnoreCase)
            || videoSource.Equals("idd-gpu", StringComparison.OrdinalIgnoreCase);
    }

    private void HandleHostExited(Process process, HostProcessLog hostLog)
    {
        var isCurrentHost = ReferenceEquals(_hostProcess, process);
        var processId = TryGetProcessId(process);
        var expectedStop = processId.HasValue && _hostStopRequestedProcessId == processId;
        if (expectedStop)
        {
            _hostStopRequestedProcessId = null;
        }

        if (!isCurrentHost)
        {
            return;
        }

        _hostProcess = null;
        StopHostOwnedVirtualDisplay();
        SetRunningState(false);

        if (expectedStop)
        {
            return;
        }

        _audioOverrideStatus = AudioCapabilityStatus.Error;
        UpdateAudioState("音频设备暂不可用，主机进程已退出。");
        SetOverviewHostState(OverviewHostServiceState.Error, "主机进程已退出。");

        var exitCode = TryGetExitCode(process);
        var summary = exitCode.HasValue
            ? $"SideDock 主机进程已退出（退出码 {exitCode.Value}）。下面是详细诊断信息，点“复制详情”可一键复制后发给开发者排查。"
            : "SideDock 主机进程已退出。下面是详细诊断信息，点“复制详情”可一键复制后发给开发者排查。";
        var details = BuildHostFailureDetails(
            "主机进程退出",
            hostLog.HostPath,
            hostLog.Arguments,
            hostLog.WorkingDirectory,
            hostLog.AdbPath,
            hostLog.AdbSerial,
            exitCode,
            hostLog,
            exception: null);

        ShowErrorWithDetails("SideDock 主机已退出", summary, details);
    }

    private string BuildHostFailureDetails(
        string reason,
        string? hostPath,
        string? arguments,
        string? workingDirectory,
        string? adbPath,
        string? adbSerial,
        int? exitCode,
        HostProcessLog? hostLog,
        Exception? exception)
    {
        var report = new StringBuilder();
        report.AppendLine("SideDock 主机启动失败诊断报告");
        report.AppendLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"原因: {reason}");
        if (exitCode.HasValue)
        {
            report.AppendLine($"退出码: {exitCode.Value}");
        }

        report.AppendLine($"Host 路径: {FormatOptional(hostPath)}");
        report.AppendLine($"工作目录: {FormatOptional(workingDirectory)}");
        report.AppendLine($"启动参数: {FormatOptional(arguments)}");
        report.AppendLine($"ADB 路径: {FormatOptional(adbPath)}");
        report.AppendLine($"ADB 设备: {FormatOptional(adbSerial)}");
        report.AppendLine($"视频源: {Selected(VideoSourceCombo)}");
        report.AppendLine($"分辨率: {Selected(ResolutionCombo)}");
        report.AppendLine($"刷新率: {Selected(RefreshRateCombo)}");
        report.AppendLine($"控制端口: {FormatNumberBox(ControlPortBox)}");
        report.AppendLine($"视频端口: {FormatNumberBox(VideoPortBox)}");
        report.AppendLine($"音频端口: {(StaticOverviewUi ? FormatNumberBox(OverviewAudioPortBox) : DefaultAudioPort.ToString(CultureInfo.InvariantCulture))}");
        report.AppendLine($"摄像头端口: {(StaticOverviewUi ? FormatNumberBox(OverviewCameraPortBox) : DefaultCameraPort.ToString(CultureInfo.InvariantCulture))}");
        report.AppendLine($"启用触控输入: {InputInjectionSwitch.IsOn}");
        report.AppendLine($"自动管理虚拟显示器: {ManageDisplaySwitch.IsOn}");

        if (exception is not null)
        {
            report.AppendLine();
            report.AppendLine("---- 异常 ----");
            report.AppendLine(exception.ToString());
        }

        if (hostLog is not null)
        {
            report.AppendLine();
            report.AppendLine("---- stdout/stderr ----");
            report.Append(hostLog.Snapshot());
        }

        return report.ToString();
    }

    private DeviceToolDiagnostics CaptureDeviceToolDiagnostics(string deviceToolPath, string workingDirectory)
    {
        var output = new StringBuilder();
        var outputGate = new object();
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = deviceToolPath,
                Arguments = "--oneshot",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, args) => AppendDiagnosticLine(output, outputGate, "stdout", args.Data);
            process.ErrorDataReceived += (_, args) => AppendDiagnosticLine(output, outputGate, "stderr", args.Data);

            if (!process.Start())
            {
                return new DeviceToolDiagnostics(null, "无法启动诊断进程。", TimedOut: false);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(12_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
                return new DeviceToolDiagnostics(TryGetExitCode(process), SnapshotOutput(output, outputGate), TimedOut: true);
            }

            process.WaitForExit();
            return new DeviceToolDiagnostics(TryGetExitCode(process), SnapshotOutput(output, outputGate), TimedOut: false);
        }
        catch (Exception ex)
        {
            AppendDiagnosticLine(output, outputGate, "exception", ex.ToString());
            return new DeviceToolDiagnostics(null, SnapshotOutput(output, outputGate), TimedOut: false);
        }
    }

    private string BuildVirtualDisplayFailureDetails(
        Exception exception,
        int? exitCode,
        DeviceToolDiagnostics? diagnostics)
    {
        var report = new StringBuilder();
        report.AppendLine("SideDock 虚拟显示器启动失败诊断报告");
        report.AppendLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"DeviceTool 路径: {FormatOptional(_deviceToolPath)}");
        report.AppendLine($"进程名: {DeviceToolProcessName}");
        if (exitCode.HasValue)
        {
            report.AppendLine($"原始退出码: {exitCode.Value}");
        }

        report.AppendLine($"视频源: {Selected(VideoSourceCombo)}");
        report.AppendLine($"自动管理虚拟显示器: {ManageDisplaySwitch.IsOn}");
        report.AppendLine();
        report.AppendLine("---- 异常 ----");
        report.AppendLine(exception.ToString());
        if (diagnostics is not null)
        {
            report.AppendLine();
            report.AppendLine("---- DeviceTool --oneshot 诊断输出 ----");
            if (diagnostics.ExitCode.HasValue)
            {
                report.AppendLine($"诊断退出码: {diagnostics.ExitCode.Value}");
            }

            if (diagnostics.TimedOut)
            {
                report.AppendLine("诊断运行超时，已停止诊断进程。");
            }

            report.AppendLine(string.IsNullOrWhiteSpace(diagnostics.Output)
                ? "(没有捕获到 stdout/stderr 输出)"
                : diagnostics.Output.TrimEnd());
        }

        return report.ToString();
    }

    private static void SaveVirtualDisplayLog(string details)
    {
        try
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                $"virtual-display-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, details, System.Text.Encoding.UTF8);
        }
        catch
        {
        }
    }

    private string BuildVirtualDisplayStartupDetails()
    {
        var report = new StringBuilder();
        report.AppendLine("SideDock 虚拟显示器启动日志");
        report.AppendLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"结果: 成功");
        report.AppendLine($"DeviceTool 路径: {FormatOptional(_deviceToolPath)}");
        report.AppendLine($"进程名: {DeviceToolProcessName}");
        report.AppendLine($"进程 ID: {_deviceToolProcess?.Id}");
        report.AppendLine($"视频源: {Selected(VideoSourceCombo)}");
        report.AppendLine($"自动管理虚拟显示器: {ManageDisplaySwitch.IsOn}");
        return report.ToString();
    }

    private static void AppendDiagnosticLine(StringBuilder output, object gate, string stream, string? line)
    {
        if (line is not null)
        {
            lock (gate)
            {
                output.AppendLine($"{stream}: {line}");
            }
        }
    }

    private static string SnapshotOutput(StringBuilder output, object gate)
    {
        lock (gate)
        {
            return output.ToString();
        }
    }

    private static int? TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return null;
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(空)" : value;
    }

    private static string FormatNumberBox(NumberBox numberBox)
    {
        return double.IsNaN(numberBox.Value) ? "(无效)" : ((int)numberBox.Value).ToString();
    }

    private DisplayLayoutSnapshot RefreshVirtualDisplayState()
    {
        var running = IsVirtualDisplayToolRunning();
        var displayLayout = DisplayLayoutQuery.GetCurrent();
        var state = DetermineVirtualDisplayOverviewState(running, out var driverInstalled, out var toolAvailable);
        var (statusText, subtext, statusBrush) = BuildVirtualDisplayStatusView(state, driverInstalled, toolAvailable);
        var displayOperationEnabled = !_virtualDisplayOperationInProgress
            && !_virtualDisplayModeApplyInProgress
            && !_driverInstallInProgress;

        StartDisplayButton.IsEnabled = displayOperationEnabled && !running;
        StopDisplayButton.IsEnabled = displayOperationEnabled && running;
        DisplayStatusText.Text = statusText;
        DisplayStatusText.Foreground = statusBrush;
        DisplayStatusSubtext.Text = subtext;

        _updatingOverviewVirtualDisplaySwitch = true;
        try
        {
            OverviewVirtualDisplaySwitch.IsOn = state == VirtualDisplayOverviewState.Starting
                || (running && state != VirtualDisplayOverviewState.Stopping);
        }
        finally
        {
            _updatingOverviewVirtualDisplaySwitch = false;
        }

        OverviewVirtualDisplaySwitch.IsEnabled = displayOperationEnabled
            && state is not VirtualDisplayOverviewState.DriverInstalling;
        OverviewVirtualDisplayStatusText.Text = statusText;
        OverviewVirtualDisplayStatusText.Foreground = statusBrush;
        OverviewVirtualDisplayHintText.Text = BuildOverviewVirtualDisplayHint(state, subtext, running);
        OverviewInstallDriverButton.IsEnabled = displayOperationEnabled;
        UpdateOverviewVirtualDisplayOptionsEnabled(running, displayLayout.HasSideDockVirtualDisplay);
        UpdateStaticDisplayPageState(
            state,
            running,
            driverInstalled,
            toolAvailable,
            statusText,
            subtext,
            statusBrush,
            displayOperationEnabled,
            displayLayout);
        UpdateStaticDiagnosticsPage();

        return displayLayout;
    }

    private void UpdateStaticDisplayPageState(
        VirtualDisplayOverviewState state,
        bool running,
        bool driverInstalled,
        bool toolAvailable,
        string statusText,
        string subtext,
        Brush statusBrush,
        bool displayOperationEnabled,
        DisplayLayoutSnapshot displayLayout)
    {
        var (bannerBackground, bannerBorder, bannerGlyph, bannerIconBackground, bannerSeverity) = BuildStaticDisplayBannerView(state);
        var (driverStatusText, driverStatusBrush) = BuildStaticDisplayDriverStatus(state, running, driverInstalled);
        var (toolStatusText, toolStatusBrush) = BuildStaticDisplayToolStatus(state, running, toolAvailable);
        var (permissionStatusText, permissionStatusBrush) = BuildStaticDisplayPermissionStatus(state);
        var footer = BuildOverviewFooterSnapshot();
        var currentMode = CurrentSideDockModeFromLayout(displayLayout);
        var presentationState = VirtualDisplayModeService.GetPresentationState();
        var canChangeDisplayOptions = displayOperationEnabled
            && (running || displayLayout.HasSideDockVirtualDisplay);

        OverviewDisplayPage.UpdateVirtualDisplayState(new StaticDisplayPageState
        {
            StatusText = statusText,
            StatusDetail = BuildOverviewVirtualDisplayHint(state, subtext, running),
            StatusBrush = statusBrush,
            BannerBackground = bannerBackground,
            BannerBorderBrush = bannerBorder,
            BannerIconGlyph = bannerGlyph,
            BannerIconBackground = bannerIconBackground,
            BannerSeverity = bannerSeverity,
            DriverStatusText = driverStatusText,
            DriverStatusBrush = driverStatusBrush,
            DeviceToolStatusText = toolStatusText,
            DeviceToolStatusBrush = toolStatusBrush,
            SystemPermissionStatusText = permissionStatusText,
            SystemPermissionStatusBrush = permissionStatusBrush,
            AutostartStatusText = "未管理",
            CanStart = displayOperationEnabled && !running,
            CanStop = displayOperationEnabled && running,
            CanInstallDriver = displayOperationEnabled,
            CanRefresh = !_driverInstallInProgress,
            CanOpenDisplaySettings = true,
            CanChangeDisplayOptions = canChangeDisplayOptions,
            VirtualDisplayRunning = running,
            DisplayLayout = displayLayout,
            PresentationMode = presentationState.Mode,
            PresentationModeMessage = presentationState.Summary,
            Resolution = DisplayResolutionValueFromMode(currentMode) ?? Selected(ResolutionCombo),
            RefreshRate = DisplayRefreshRateValueFromMode(currentMode) ?? Selected(RefreshRateCombo),
            FooterHostText = footer.HostText,
            FooterOsText = footer.OsText,
            FooterNetworkText = footer.NetworkText,
            FooterNetworkBrush = footer.NetworkBrush
        });
    }

    private (Brush Background, Brush Border, string Glyph, Brush IconBackground, StaticDisplayBannerSeverity Severity) BuildStaticDisplayBannerView(
        VirtualDisplayOverviewState state)
    {
        return state switch
        {
            VirtualDisplayOverviewState.Running => (
                _overviewReadyBackgroundBrush,
                _overviewReadyBorderBrush,
                "\uE73E",
                _successBrush,
                StaticDisplayBannerSeverity.Ready),
            VirtualDisplayOverviewState.Error => (
                _overviewErrorBackgroundBrush,
                _overviewErrorBorderBrush,
                "\uE783",
                _dangerBrush,
                StaticDisplayBannerSeverity.Error),
            VirtualDisplayOverviewState.DriverMissing => (
                _overviewWarningBackgroundBrush,
                _overviewWarningBorderBrush,
                "\uE7BA",
                _warningBrush,
                StaticDisplayBannerSeverity.Warning),
            VirtualDisplayOverviewState.DriverInstalling
                or VirtualDisplayOverviewState.Starting
                or VirtualDisplayOverviewState.Stopping => (
                    _overviewNeutralBackgroundBrush,
                    _overviewNeutralBorderBrush,
                    "\uE768",
                    _overviewPrimaryBrush,
                    StaticDisplayBannerSeverity.Neutral),
            _ => (
                _overviewNeutralBackgroundBrush,
                _overviewNeutralBorderBrush,
                "\uE946",
                _overviewPrimaryBrush,
                StaticDisplayBannerSeverity.Neutral)
        };
    }

    private (string Text, Brush Brush) BuildStaticDisplayDriverStatus(
        VirtualDisplayOverviewState state,
        bool running,
        bool driverInstalled)
    {
        if (state == VirtualDisplayOverviewState.DriverInstalling)
        {
            return ("安装中", _overviewPrimaryBrush);
        }

        if (running || driverInstalled)
        {
            return ("已安装", _successBrush);
        }

        return ("未安装", _warningBrush);
    }

    private (string Text, Brush Brush) BuildStaticDisplayToolStatus(
        VirtualDisplayOverviewState state,
        bool running,
        bool toolAvailable)
    {
        if (state is VirtualDisplayOverviewState.Starting or VirtualDisplayOverviewState.Stopping)
        {
            return ("处理中", _overviewPrimaryBrush);
        }

        if (running)
        {
            return ("运行中", _successBrush);
        }

        if (toolAvailable)
        {
            return ("可用", _successBrush);
        }

        return ("缺失", _warningBrush);
    }

    private (string Text, Brush Brush) BuildStaticDisplayPermissionStatus(VirtualDisplayOverviewState state)
    {
        return state switch
        {
            VirtualDisplayOverviewState.DriverInstalling => ("等待授权", _overviewPrimaryBrush),
            VirtualDisplayOverviewState.DriverMissing => ("需要管理员安装", _warningBrush),
            VirtualDisplayOverviewState.Error => ("需要检查", _dangerBrush),
            VirtualDisplayOverviewState.Starting or VirtualDisplayOverviewState.Stopping => ("处理中", _overviewPrimaryBrush),
            _ => ("正常", _successBrush)
        };
    }

    private VirtualDisplayOverviewState DetermineVirtualDisplayOverviewState(
        bool running,
        out bool driverInstalled,
        out bool toolAvailable)
    {
        if (_virtualDisplayTransientState is { } transientState)
        {
            driverInstalled = running || _virtualDisplayDriverInstalledCache == true;
            toolAvailable = running || _virtualDisplayToolAvailableCache == true;
            return transientState;
        }

        if (_driverInstallInProgress)
        {
            driverInstalled = running || _virtualDisplayDriverInstalledCache == true;
            toolAvailable = running || _virtualDisplayToolAvailableCache == true;
            return VirtualDisplayOverviewState.DriverInstalling;
        }

        driverInstalled = running || IsVirtualDisplayDriverInstalledCached();
        toolAvailable = running || IsVirtualDisplayToolAvailableCached();

        if (running)
        {
            return VirtualDisplayOverviewState.Running;
        }

        if (!driverInstalled || !toolAvailable)
        {
            return VirtualDisplayOverviewState.DriverMissing;
        }

        return string.IsNullOrWhiteSpace(_virtualDisplayLastError)
            ? VirtualDisplayOverviewState.ToolStopped
            : VirtualDisplayOverviewState.Error;
    }

    private (string StatusText, string Subtext, Brush StatusBrush) BuildVirtualDisplayStatusView(
        VirtualDisplayOverviewState state,
        bool driverInstalled,
        bool toolAvailable)
    {
        return state switch
        {
            VirtualDisplayOverviewState.DriverInstalling => (
                "安装中",
                "正在启动驱动安装器，请在管理员权限弹窗中选择“是”。",
                _overviewPrimaryBrush),
            VirtualDisplayOverviewState.Starting => (
                "启动中",
                "正在启动 SideDock 虚拟显示器工具。",
                _overviewPrimaryBrush),
            VirtualDisplayOverviewState.Running => (
                "运行中",
                "SideDock 虚拟显示器工具正在运行。",
                _successBrush),
            VirtualDisplayOverviewState.Stopping => (
                "停止中",
                "正在停止 SideDock 虚拟显示器工具。",
                _overviewPrimaryBrush),
            VirtualDisplayOverviewState.Error => (
                "启动失败",
                string.IsNullOrWhiteSpace(_virtualDisplayLastError)
                    ? "虚拟显示器启动失败，请安装/修复驱动后重试。"
                    : $"虚拟显示器启动失败：{_virtualDisplayLastError}",
                _dangerBrush),
            VirtualDisplayOverviewState.DriverMissing when !driverInstalled => (
                "未安装驱动",
                "需要先安装/修复 SideDock 虚拟显示驱动。",
                _warningBrush),
            VirtualDisplayOverviewState.DriverMissing when !toolAvailable => (
                "需要修复驱动",
                $"未找到 {DeviceToolExe}，请使用安装/修复驱动恢复工具组件。",
                _warningBrush),
            _ => (
                "未运行",
                "驱动已安装，虚拟显示器工具未运行。",
                _secondaryBrush)
        };
    }

    private string BuildOverviewVirtualDisplayHint(
        VirtualDisplayOverviewState state,
        string subtext,
        bool running)
    {
        if (running)
        {
            return $"{subtext} 可实时应用分辨率和刷新率，成功后也会同步下次启动参数。";
        }

        if (_hostProcess is { HasExited: false })
        {
            return $"{subtext} 主机运行中，启动参数会在下次启动主机时生效。";
        }

        return state == VirtualDisplayOverviewState.ToolStopped
            ? $"{subtext} 当前参数：{Selected(ResolutionCombo)} / {Selected(RefreshRateCombo)}fps。"
            : subtext;
    }

    private void UpdateOverviewVirtualDisplayOptionsEnabled(bool virtualDisplayRunning, bool hasSideDockDisplay)
    {
        var enabled = !_virtualDisplayOperationInProgress
            && !_virtualDisplayModeApplyInProgress
            && !_driverInstallInProgress
            && (virtualDisplayRunning || hasSideDockDisplay);

        OverviewVirtualDisplayResolutionCombo.IsEnabled = enabled;
        OverviewVirtualDisplayRefreshRateCombo.IsEnabled = enabled;
    }

    private bool IsVirtualDisplayDriverInstalledCached()
    {
        var now = DateTimeOffset.UtcNow;
        if (_virtualDisplayDriverInstalledCache.HasValue
            && now - _virtualDisplayDriverInstalledCheckedAt < VirtualDisplayStatusCacheDuration)
        {
            return _virtualDisplayDriverInstalledCache.Value;
        }

        var installed = TryCheckSideDockDisplayDriverPackageInstalled();
        _virtualDisplayDriverInstalledCache = installed;
        _virtualDisplayDriverInstalledCheckedAt = now;
        return installed;
    }

    private bool IsVirtualDisplayToolAvailableCached()
    {
        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(_deviceToolPath) && File.Exists(_deviceToolPath))
        {
            _virtualDisplayToolAvailableCache = true;
            _virtualDisplayToolAvailableCheckedAt = now;
            return true;
        }

        if (_virtualDisplayToolAvailableCache.HasValue
            && now - _virtualDisplayToolAvailableCheckedAt < VirtualDisplayStatusCacheDuration)
        {
            return _virtualDisplayToolAvailableCache.Value;
        }

        try
        {
            _deviceToolPath = ResolveDeviceToolPath();
            _virtualDisplayToolAvailableCache = true;
        }
        catch
        {
            _deviceToolPath = null;
            _virtualDisplayToolAvailableCache = false;
        }

        _virtualDisplayToolAvailableCheckedAt = now;
        return _virtualDisplayToolAvailableCache.Value;
    }

    private static bool TryCheckSideDockDisplayDriverPackageInstalled()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = "/enum-drivers",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1000);
                }
                catch
                {
                }

                return false;
            }

            var output = stdoutTask.GetAwaiter().GetResult()
                + Environment.NewLine
                + stderrTask.GetAwaiter().GetResult();

            return output.Contains(SideDockDriverInf, StringComparison.OrdinalIgnoreCase)
                || output.Contains(SideDockDriverBinary, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsVirtualDisplayToolRunning()
    {
        var processes = GetVirtualDisplayToolProcesses();
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    return true;
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static Process[] GetVirtualDisplayToolProcesses()
    {
        try
        {
            return Process.GetProcessesByName(DeviceToolProcessName);
        }
        catch
        {
            return Array.Empty<Process>();
        }
    }

    private void SetRunningState(bool running)
    {
        if (running)
        {
            _hostHasStarted = true;
        }

        StartHostButton.IsEnabled = !running;
        StopHostButton.IsEnabled = running;
        AdbDeviceCombo.IsEnabled = !running;
        RefreshAdbDevicesButton.IsEnabled = !running;
        RestartAdbButton.IsEnabled = !running;
        CameraFacingCombo.IsEnabled = !_restartingForCameraFacing;
        OverallStatusText.Text = running ? "运行中" : "未启动";
        OverallStatusText.Foreground = running ? _successBrush : _dangerBrush;
        if (running)
        {
            _audioOverrideStatus = null;
            _microphoneRuntimeStatus = AudioCapabilityStatus.Preparing;
            _speakerRuntimeStatus = AudioCapabilityStatus.Preparing;
        }
        else
        {
            _microphoneRuntimeStatus = null;
            _speakerRuntimeStatus = null;
        }

        SetOverviewHostState(
            running
                ? OverviewHostServiceState.Running
                : _hostHasStarted
                    ? OverviewHostServiceState.Stopped
                    : OverviewHostServiceState.NotStarted,
            running
                ? "SideDock.Host.exe 正在运行"
                : _hostHasStarted
                    ? "主机已停止"
                    : "等待启动");
        UpdateAudioState(running ? "正在准备音频设备。" : "等待 Android 设备连接。");
        RefreshVirtualDisplayState();
        UpdateOverviewCameraState();
        UpdateOverviewRuntimeDiagnostics();
        if (running)
        {
            UpdateOverviewPreview();
        }
        else
        {
            ResetOverviewPreview(clearImage: true);
            SetOverviewPreviewState(OverviewPreviewState.HostNotStarted);
        }

        UpdateOverviewConnectionPage();
    }

    private void SetAdbStatus(string text, Brush brush)
    {
        _lastAdbStatusText = text;
        AdbStatusText.Text = text;
        AdbStatusText.Foreground = brush;
        UpdateOverviewAndroidDeviceState();
        UpdateOverviewConnectionPage();
    }

    private void UpdateOverviewPreview()
    {
        if (!StaticOverviewUi)
        {
            return;
        }

        if (!IsHostRunningForPreview())
        {
            ResetOverviewPreview(clearImage: true);
            SetOverviewPreviewState(OverviewPreviewState.HostNotStarted);
            return;
        }

        try
        {
            _overviewPreviewReader ??= OverviewPreviewFrameReader.TryOpen();
            if (_overviewPreviewReader is null)
            {
                ResetOverviewPreview(clearImage: true, keepReader: true);
                SetOverviewPreviewState(OverviewPreviewState.WaitingSource);
                return;
            }

            var frame = _overviewPreviewReader.TryReadLatest(_lastOverviewPreviewSequence);
            if (frame is null)
            {
                UpdateOverviewPreviewStaleness();
                return;
            }

            if (_overviewPreviewBitmap is null
                || _overviewPreviewBitmap.PixelWidth != frame.Width
                || _overviewPreviewBitmap.PixelHeight != frame.Height)
            {
                _overviewPreviewBitmap = new WriteableBitmap(frame.Width, frame.Height);
                OverviewPreviewImage.Source = _overviewPreviewBitmap;
            }

            using (var stream = _overviewPreviewBitmap.PixelBuffer.AsStream())
            {
                stream.Seek(0, SeekOrigin.Begin);
                stream.Write(frame.Bgra, 0, frame.Bgra.Length);
            }

            _overviewPreviewBitmap.Invalidate();
            _lastOverviewPreviewSequence = frame.Sequence;
            _lastOverviewPreviewAt = DateTimeOffset.FromUnixTimeMilliseconds(frame.WrittenAtUnixMs);
            SetOverviewPreviewState(OverviewPreviewState.Receiving);
        }
        catch (FileNotFoundException)
        {
            ResetOverviewPreview(clearImage: true);
            SetOverviewPreviewState(OverviewPreviewState.WaitingSource);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or ArgumentException)
        {
            ResetOverviewPreview(clearImage: true);
            SetOverviewPreviewState(OverviewPreviewState.Error, ex.Message);
        }
    }

    private bool IsHostRunningForPreview()
    {
        return _overviewHostServiceState == OverviewHostServiceState.Running
            && _hostProcess is { HasExited: false };
    }

    private void UpdateOverviewPreviewStaleness()
    {
        if (_lastOverviewPreviewAt is null)
        {
            SetOverviewPreviewState(OverviewPreviewState.WaitingSource);
            return;
        }

        var age = DateTimeOffset.UtcNow - _lastOverviewPreviewAt.Value.ToUniversalTime();
        SetOverviewPreviewState(age > OverviewPreviewStaleAfter
            ? OverviewPreviewState.Paused
            : OverviewPreviewState.Receiving);
    }

    private void ResetOverviewPreview(bool clearImage, bool keepReader = false)
    {
        if (!keepReader)
        {
            _overviewPreviewReader?.Dispose();
            _overviewPreviewReader = null;
        }

        _lastOverviewPreviewSequence = 0;
        _lastOverviewPreviewAt = null;
        if (!clearImage)
        {
            return;
        }

        _overviewPreviewBitmap = null;
        if (OverviewPreviewImage is not null)
        {
            OverviewPreviewImage.Source = null;
        }
    }

    private void SetOverviewPreviewState(OverviewPreviewState state, string? detail = null)
    {
        _overviewPreviewState = state;
        if (OverviewPreviewStatusText is null)
        {
            return;
        }

        OverviewPreviewStatusText.Text = state switch
        {
            OverviewPreviewState.HostNotStarted => "主机未启动",
            OverviewPreviewState.WaitingSource => "等待视频源",
            OverviewPreviewState.Receiving => "正在接收画面",
            OverviewPreviewState.Paused => "画面暂停",
            OverviewPreviewState.Unavailable => "预览不可用",
            OverviewPreviewState.Error => "预览错误",
            _ => "等待预览"
        };

        OverviewPreviewStatusBadge.Background = state switch
        {
            OverviewPreviewState.Receiving => _overviewPreviewReceivingBadgeBrush,
            OverviewPreviewState.Paused => _overviewPreviewPausedBadgeBrush,
            OverviewPreviewState.Error => _overviewPreviewErrorBadgeBrush,
            OverviewPreviewState.Unavailable => _overviewPreviewErrorBadgeBrush,
            _ => _overviewPreviewNeutralBadgeBrush
        };

        var hasImage = OverviewPreviewImage?.Source is not null;
        var showEmpty = state is OverviewPreviewState.HostNotStarted or OverviewPreviewState.WaitingSource or OverviewPreviewState.Unavailable or OverviewPreviewState.Error
            || !hasImage;
        OverviewPreviewEmptyState.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
        OverviewPreviewEmptyStateText.Text = detail ?? state switch
        {
            OverviewPreviewState.HostNotStarted => "启动主机后显示副屏实时画面",
            OverviewPreviewState.WaitingSource => "等待虚拟显示器或 Android 视频连接",
            OverviewPreviewState.Receiving => "正在显示来自 SideDock Host 的实时帧",
            OverviewPreviewState.Paused => hasImage ? "画面暂无新帧" : "画面暂停，暂无可显示帧",
            OverviewPreviewState.Unavailable => "当前预览帧源不可用",
            OverviewPreviewState.Error => "预览缓存暂不可读",
            _ => "等待预览"
        };
    }

    private void UpdateOverviewPreviewChrome()
    {
        if (OverviewPreviewImage is not null)
        {
            OverviewPreviewImage.Stretch = _overviewPreviewFillMode ? Stretch.UniformToFill : Stretch.Uniform;
        }

        if (OverviewPreviewFitIcon is not null)
        {
            OverviewPreviewFitIcon.Glyph = _overviewPreviewFillMode ? "\uE73F" : "\uE740";
        }

        if (OverviewPreviewFitButton is not null)
        {
            ToolTipService.SetToolTip(
                OverviewPreviewFitButton,
                _overviewPreviewFillMode ? "切换为完整适配" : "切换为填充裁切");
        }

        if (OverviewPreviewOverlay is not null)
        {
            OverviewPreviewOverlay.Visibility = _overviewPreviewOverlayVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        if (OverviewPreviewOverlayRestoreButton is not null)
        {
            OverviewPreviewOverlayRestoreButton.Visibility = _overviewPreviewOverlayVisible ? Visibility.Collapsed : Visibility.Visible;
        }

        if (OverviewPreviewOverlayIcon is not null)
        {
            OverviewPreviewOverlayIcon.Glyph = _overviewPreviewOverlayVisible ? "\uE890" : "\uED1A";
        }
    }

    private void UpdateCameraPreview()
    {
        if (!_cameraPreviewEnabled)
        {
            return;
        }

        try
        {
            _cameraPreviewReader ??= CameraPreviewFrameReader.TryOpen();
            if (_cameraPreviewReader is null)
            {
                SetCameraPreviewPlaceholder("等待摄像头解码帧", visible: true);
                return;
            }

            var frame = _cameraPreviewReader.TryReadLatest(_lastCameraPreviewSequence);
            if (frame is null)
            {
                UpdateCameraPreviewStaleness();
                return;
            }

            if (_cameraPreviewBitmap is null
                || _cameraPreviewBitmap.PixelWidth != frame.Width
                || _cameraPreviewBitmap.PixelHeight != frame.Height)
            {
                _cameraPreviewBitmap = new WriteableBitmap(frame.Width, frame.Height);
                SetCameraPreviewImageSource(_cameraPreviewBitmap);
            }

            using (var stream = _cameraPreviewBitmap.PixelBuffer.AsStream())
            {
                stream.Seek(0, SeekOrigin.Begin);
                stream.Write(frame.Bgra, 0, frame.Bgra.Length);
            }

            _cameraPreviewBitmap.Invalidate();
            _lastCameraPreviewSequence = frame.Sequence;
            _lastCameraPreviewAt = DateTimeOffset.FromUnixTimeMilliseconds(frame.WrittenAtUnixMs);
            _cameraDiagnostics.PreviewFrameSequence = Math.Max(_cameraDiagnostics.PreviewFrameSequence, frame.Sequence);
            SetCameraPreviewPlaceholder("", visible: false);
            UpdateCameraStatusView();
        }
        catch (FileNotFoundException)
        {
            _cameraPreviewReader?.Dispose();
            _cameraPreviewReader = null;
            SetCameraPreviewPlaceholder("等待摄像头解码帧", visible: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or ArgumentException)
        {
            _cameraPreviewReader?.Dispose();
            _cameraPreviewReader = null;
            _cameraDiagnostics.LastError = ex.Message;
            SetCameraPreviewPlaceholder("预览缓存暂不可读", visible: true);
            UpdateCameraStatusView();
        }
    }

    private void UpdateCameraPreviewStaleness()
    {
        if (_lastCameraPreviewAt is null)
        {
            SetCameraPreviewPlaceholder("等待摄像头解码帧", CameraPreviewImage.Source is null);
            return;
        }

        UpdateCameraStatusView();
    }

    private void SetCameraPreviewEnabled(bool enabled)
    {
        if (_cameraPreviewEnabled == enabled)
        {
            if (enabled)
            {
                _cameraPreviewTimer.Start();
                SetCameraPreviewPlaceholder("等待摄像头解码帧", CameraPreviewImage.Source is null);
                UpdateCameraPreview();
            }

            UpdateCameraPreviewToggleView();
            UpdateCameraStatusView();
            return;
        }

        _cameraPreviewEnabled = enabled;
        if (enabled)
        {
            SetCameraPreviewPlaceholder("等待摄像头解码帧", CameraPreviewImage.Source is null);
            _cameraPreviewTimer.Start();
            UpdateCameraPreview();
        }
        else
        {
            _cameraPreviewTimer.Stop();
            _cameraPreviewReader?.Dispose();
            _cameraPreviewReader = null;
            _cameraPreviewBitmap = null;
            SetCameraPreviewImageSource(null);
            SetCameraPreviewPlaceholder("Windows 预览已关闭", visible: true);
        }

        UpdateCameraPreviewToggleView();
        UpdateCameraStatusView();
    }

    private void UpdateCameraPreviewToggleView()
    {
        if (ToggleCameraPreviewButtonText is null || ToggleCameraPreviewIcon is null)
        {
            return;
        }

        ToggleCameraPreviewButtonText.Text = _cameraPreviewEnabled ? "关闭预览" : "打开预览";
        ToggleCameraPreviewIcon.Glyph = _cameraPreviewEnabled ? "\uE890" : "\uE7B3";
    }

    private string FormatCameraPreviewState()
    {
        return _cameraPreviewEnabled
            ? FormatCameraAge(_lastCameraPreviewAt?.ToString("O") ?? string.Empty)
            : "已关闭";
    }

    private void SetCameraPreviewImageSource(ImageSource? source)
    {
        CameraPreviewImage.Source = source;
        if (StaticOverviewUi && OverviewCameraPreviewImage is not null)
        {
            OverviewCameraPreviewImage.Source = source;
        }
    }

    private void SetCameraPreviewPlaceholder(string text, bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CameraPreviewPlaceholderText.Text = text;
        CameraPreviewPlaceholderText.Visibility = visibility;

        if (StaticOverviewUi && OverviewCameraPreviewPlaceholderText is not null)
        {
            OverviewCameraPreviewPlaceholderText.Text = text;
            OverviewCameraPreviewPlaceholderPanel.Visibility = visibility;
        }
    }

    private void UpdateCameraStatusView()
    {
        if (CameraStatusText is null)
        {
            UpdateOverviewCameraState();
            UpdateOverviewCameraPageView();
            return;
        }

        var camera = _cameraDiagnostics;
        CameraStatusText.Text = $"Server: {camera.ServerState}  Android: {camera.ClientState}  权限: {camera.PermissionText}";
        CameraConfigText.Text = $"port {camera.Port} · {camera.Width}x{camera.Height}@{camera.Fps} · {camera.Codec} · {camera.Facing}";
        CameraMetricsText.Text =
            $"{camera.ApproxFps:F1} fps · {camera.ApproxKbps:F0} kbps · packets {camera.Packets} · frames {camera.Frames} · decoded {camera.DecodedFrames} · "
            + $"decode {camera.DecodeLagMs:F0}ms · preview {FormatCameraPreviewState()} · last {FormatCameraAge(camera.LastFrameAt)}";
        var errorText = string.IsNullOrWhiteSpace(camera.LastError) ? "无" : camera.LastError;
        CameraErrorText.Text = errorText;
        CameraErrorText.Foreground = string.IsNullOrWhiteSpace(camera.LastError) ? _secondaryBrush : _warningBrush;

        var virtualCamera = _virtualCameraDiagnostics;
        VirtualCameraStatusText.Text =
            $"注册: {virtualCamera.RegistrationText} · 运行: {virtualCamera.RunningText} · "
            + $"供帧: {FormatVirtualCameraServedAt(virtualCamera.LastServedAt)} · 源帧 {virtualCamera.SourceFrameSequence}";
        if (!string.IsNullOrWhiteSpace(virtualCamera.LastError) && string.IsNullOrWhiteSpace(camera.LastError))
        {
            CameraErrorText.Text = virtualCamera.LastError;
            CameraErrorText.Foreground = _warningBrush;
        }

        UpdateOverviewCameraState();
        UpdateOverviewCameraPageView();
    }

    private void UpdateOverviewCameraPageView()
    {
        if (!StaticOverviewUi || OverviewCameraLinkBanner is null)
        {
            return;
        }

        UpdateOverviewCameraLinkBanner();
        UpdateOverviewCameraPreviewStats();
        UpdateOverviewCameraReceiveStatus();
        UpdateOverviewCameraEncodingStatus();
        UpdateOverviewCameraErrorStatus();
        UpdateOverviewVirtualCameraCard();
        UpdateOverviewCameraRecentEvents();
    }

    private void UpdateOverviewCameraLinkBanner()
    {
        var camera = _cameraDiagnostics;
        var virtualCamera = _virtualCameraDiagnostics;
        var hostRunning = _hostProcess is { HasExited: false };
        var error = FirstNonEmpty(camera.LastError, virtualCamera.LastError, _lastCameraErrorMessage);

        string title;
        string detail;
        string glyph;
        Brush foreground;
        Brush background;
        Brush border;

        if (!string.IsNullOrWhiteSpace(error) || IsCameraErrorState(camera.ServerState) || IsCameraErrorState(camera.ClientState))
        {
            title = "摄像头链路异常";
            detail = string.IsNullOrWhiteSpace(error) ? "摄像头或虚拟相机报告了错误状态。" : error;
            glyph = "\uE783";
            foreground = _dangerBrush;
            background = _overviewErrorBackgroundBrush;
            border = _overviewErrorBorderBrush;
        }
        else if (IsCameraReceiving(camera) && virtualCamera.Running)
        {
            title = "摄像头链路正常";
            detail = $"视频流正在接收，虚拟相机{virtualCamera.RunningText}，供帧 {FormatVirtualCameraServedAt(virtualCamera.LastServedAt)}。";
            glyph = "\uE73E";
            foreground = _successBrush;
            background = _overviewReadyBackgroundBrush;
            border = _overviewReadyBorderBrush;
        }
        else if (IsCameraPermissionMissing(camera))
        {
            title = "等待 Android 摄像头权限";
            detail = "请在 Android 设备上允许 SideDock 使用摄像头。";
            glyph = "\uE7BA";
            foreground = _warningBrush;
            background = _overviewWarningBackgroundBrush;
            border = _overviewWarningBorderBrush;
        }
        else if (!hostRunning)
        {
            title = virtualCamera.Running ? "虚拟相机已启动" : "摄像头未启动";
            detail = virtualCamera.Running
                ? "SideDock Camera 已在 Windows 中运行；主机启动后会等待 Android 供帧。"
                : $"当前启动前配置：{FormatSelectedOverviewCameraConfig()}，端口 {FormatConfiguredCameraPortText()}。";
            glyph = virtualCamera.Running ? "\uE722" : "\uE7BA";
            foreground = virtualCamera.Running ? _successBrush : _overviewNeutralBrush;
            background = virtualCamera.Running ? _overviewReadyBackgroundBrush : _overviewNeutralBackgroundBrush;
            border = virtualCamera.Running ? _overviewReadyBorderBrush : _overviewNeutralBorderBrush;
        }
        else if (IsCameraReceiving(camera))
        {
            title = "正在接收摄像头流";
            detail = $"已接收 {camera.Frames} 帧，已解码 {camera.DecodedFrames} 帧，虚拟相机{virtualCamera.RunningText}。";
            glyph = "\uE73E";
            foreground = _successBrush;
            background = _overviewReadyBackgroundBrush;
            border = _overviewReadyBorderBrush;
        }
        else
        {
            title = "等待 Android 摄像头流";
            detail = $"主机已运行，正在等待 Android 摄像头状态。当前配置：{FormatSelectedOverviewCameraConfig()}。";
            glyph = "\uE7BA";
            foreground = _warningBrush;
            background = _overviewWarningBackgroundBrush;
            border = _overviewWarningBorderBrush;
        }

        OverviewCameraLinkBanner.Visibility = _overviewCameraBannerDismissed ? Visibility.Collapsed : Visibility.Visible;
        OverviewCameraLinkBanner.Background = background;
        OverviewCameraLinkBanner.BorderBrush = border;
        OverviewCameraLinkIconHost.Background = foreground;
        OverviewCameraLinkIcon.Glyph = glyph;
        OverviewCameraLinkTitleText.Text = title;
        OverviewCameraLinkTitleText.Foreground = foreground;
        OverviewCameraLinkDetailText.Text = detail;
        OverviewCameraLinkDetailText.Foreground = _secondaryBrush;
    }

    private void UpdateOverviewCameraPreviewStats()
    {
        var camera = _cameraDiagnostics;
        OverviewCameraPreviewFpsText.Text = camera.ApproxFps > 0
            ? camera.ApproxFps.ToString("F1", CultureInfo.InvariantCulture)
            : "--";
        OverviewCameraPreviewBitrateText.Text = camera.ApproxKbps > 0
            ? (camera.ApproxKbps / 1000d).ToString("F1", CultureInfo.InvariantCulture)
            : "--";
        OverviewCameraPreviewFramesText.Text = FormatCompactCount(camera.Frames);
        OverviewCameraPreviewDecodedText.Text = FormatCompactCount(camera.DecodedFrames);
        OverviewCameraPreviewStartButton.IsEnabled = !_cameraPreviewEnabled;
        OverviewCameraPreviewStopButton.IsEnabled = _cameraPreviewEnabled;
        OverviewCameraReconnectButton.IsEnabled = !_overviewCameraOperationInProgress;
    }

    private void UpdateOverviewCameraReceiveStatus()
    {
        var camera = _cameraDiagnostics;
        var (statusText, statusBrush) = BuildCameraReceiveStatus();
        OverviewCameraReceiveStatusText.Text = statusText;
        OverviewCameraReceiveStatusText.Foreground = statusBrush;
        OverviewCameraReceiveStatusDot.Fill = statusBrush;
        OverviewCameraReceiveProtocolText.Text = "TCP";
        OverviewCameraReceiveAddressText.Text = $"127.0.0.1:{FormatConfiguredCameraPortText()}";
        OverviewCameraReceivePacketsText.Text = $"{FormatCompactCount(camera.Packets)} / {FormatByteCount(camera.Bytes)}";
        OverviewCameraReceiveLastFrameText.Text = FormatCameraAge(camera.LastFrameAt);
    }

    private (string Text, Brush Brush) BuildCameraReceiveStatus()
    {
        var camera = _cameraDiagnostics;
        var hostRunning = _hostProcess is { HasExited: false };

        if (!string.IsNullOrWhiteSpace(camera.LastError) || IsCameraErrorState(camera.ServerState) || IsCameraErrorState(camera.ClientState))
        {
            return ("错误", _dangerBrush);
        }

        if (!_overviewCameraRequestedEnabled || IsCameraDisabledState(camera.ServerState) || IsCameraDisabledState(camera.ClientState))
        {
            return ("未启动", _secondaryBrush);
        }

        if (IsCameraPermissionMissing(camera))
        {
            return ("未授权", _warningBrush);
        }

        if (IsCameraReceiving(camera))
        {
            return ("接收中", _successBrush);
        }

        return hostRunning ? ("等待 Android", _warningBrush) : ("未启动", _secondaryBrush);
    }

    private void UpdateOverviewCameraEncodingStatus()
    {
        var camera = _cameraDiagnostics;
        SelectComboBoxValue(OverviewCameraPageCodecCombo, camera.Codec);
        OverviewCameraEncodingCodecText.Text = FormatCameraCodec(camera.Codec);
        OverviewCameraEncodingResolutionText.Text = $"{camera.Width}×{camera.Height}";
        OverviewCameraEncodingFpsText.Text = camera.ApproxFps > 0
            ? $"{camera.ApproxFps:F1} fps"
            : $"{camera.Fps} fps 配置";
        OverviewCameraEncodingBitrateText.Text = FormatCameraBitrate(camera.ApproxKbps);
        OverviewCameraEncodingDecodeLagText.Text = camera.DecodeLagMs > 0
            ? $"{camera.DecodeLagMs:F0} ms"
            : "--";
    }

    private void UpdateOverviewCameraErrorStatus()
    {
        var camera = _cameraDiagnostics;
        var error = FirstNonEmpty(camera.LastError, _virtualCameraDiagnostics.LastError, _lastCameraErrorMessage);
        if (!string.IsNullOrWhiteSpace(error))
        {
            OverviewCameraErrorIconBorder.BorderBrush = _dangerBrush;
            OverviewCameraErrorIcon.Foreground = _dangerBrush;
            OverviewCameraErrorIcon.Glyph = "\uE783";
            OverviewCameraErrorSummaryText.Text = "需要检查";
            OverviewCameraErrorSummaryText.Foreground = _dangerBrush;
            OverviewCameraErrorDetailText.Text = error;
            OverviewCameraErrorDetailText.Foreground = _dangerBrush;
            return;
        }

        if (camera.DecodeErrors > 0)
        {
            OverviewCameraErrorIconBorder.BorderBrush = _warningBrush;
            OverviewCameraErrorIcon.Foreground = _warningBrush;
            OverviewCameraErrorIcon.Glyph = "\uE7BA";
            OverviewCameraErrorSummaryText.Text = "有警告";
            OverviewCameraErrorSummaryText.Foreground = _warningBrush;
            OverviewCameraErrorDetailText.Text = $"累计解码错误 {camera.DecodeErrors} 次。";
            OverviewCameraErrorDetailText.Foreground = _warningBrush;
            return;
        }

        OverviewCameraErrorIconBorder.BorderBrush = _successBrush;
        OverviewCameraErrorIcon.Foreground = _successBrush;
        OverviewCameraErrorIcon.Glyph = "\uE73E";
        OverviewCameraErrorSummaryText.Text = "无";
        OverviewCameraErrorSummaryText.Foreground = _successBrush;
        OverviewCameraErrorDetailText.Text = "未检测到任何错误或警告。";
        OverviewCameraErrorDetailText.Foreground = _secondaryBrush;
    }

    private void UpdateOverviewVirtualCameraCard()
    {
        var virtualCamera = _virtualCameraDiagnostics;
        OverviewVirtualCameraRegistrationText.Text = virtualCamera.Registered
            ? string.IsNullOrWhiteSpace(virtualCamera.RegisteredScopeSummary)
                ? "已注册"
                : $"已注册 ({virtualCamera.RegisteredScopeSummary})"
            : "未注册";
        OverviewVirtualCameraRegistrationText.Foreground = virtualCamera.Registered ? _successBrush : _secondaryBrush;

        if (IsVirtualCameraServing(virtualCamera))
        {
            OverviewVirtualCameraServingText.Text = $"供帧中 · 源帧 {virtualCamera.SourceFrameSequence}";
            OverviewVirtualCameraServingText.Foreground = _successBrush;
        }
        else if (virtualCamera.Running)
        {
            OverviewVirtualCameraServingText.Text = $"等待供帧 · {FormatVirtualCameraServedAt(virtualCamera.LastServedAt)}";
            OverviewVirtualCameraServingText.Foreground = _warningBrush;
        }
        else
        {
            OverviewVirtualCameraServingText.Text = "已停止";
            OverviewVirtualCameraServingText.Foreground = _secondaryBrush;
        }
    }

    private static bool IsVirtualCameraServing(VirtualCameraDiagnosticsState virtualCamera)
    {
        if (virtualCamera.LastServedAt is null)
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow - virtualCamera.LastServedAt.Value.ToUniversalTime();
        return virtualCamera.Running && age.TotalSeconds <= 5;
    }

    private void UpdateOverviewCameraRecentEvents()
    {
        var events = SnapshotRecentCameraLogLines()
            .Reverse()
            .Take(5)
            .Select(line =>
            {
                var (time, text) = FormatCameraEventLine(line);
                return (Text: text, Time: time, Brush: CameraEventBrush(text));
            })
            .ToList();

        if (events.Count == 0)
        {
            AddCameraSyntheticEvents(events);
        }

        var dots = new[] { OverviewCameraEventDot0, OverviewCameraEventDot1, OverviewCameraEventDot2, OverviewCameraEventDot3, OverviewCameraEventDot4 };
        var texts = new[] { OverviewCameraEventText0, OverviewCameraEventText1, OverviewCameraEventText2, OverviewCameraEventText3, OverviewCameraEventText4 };
        var times = new[] { OverviewCameraEventTime0, OverviewCameraEventTime1, OverviewCameraEventTime2, OverviewCameraEventTime3, OverviewCameraEventTime4 };

        for (var index = 0; index < dots.Length; index++)
        {
            if (index < events.Count)
            {
                SetOverviewCameraEventRow(dots[index], texts[index], times[index], events[index].Text, events[index].Time, events[index].Brush, visible: true);
            }
            else
            {
                SetOverviewCameraEventRow(dots[index], texts[index], times[index], "", "", _overviewMutedBrush, visible: false);
            }
        }
    }

    private void AddCameraSyntheticEvents(List<(string Text, string Time, Brush Brush)> events)
    {
        if (!string.IsNullOrWhiteSpace(_lastCameraStatusLine))
        {
            events.Add(("最近摄像头状态已更新", "--", _successBrush));
        }

        if (_virtualCameraDiagnostics.LastServedAt is not null)
        {
            events.Add(($"虚拟相机最近供帧，源帧 {_virtualCameraDiagnostics.SourceFrameSequence}", _virtualCameraDiagnostics.LastServedAt.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), _successBrush));
        }

        if (!string.IsNullOrWhiteSpace(_virtualCameraDiagnostics.LastToolState))
        {
            events.Add(($"虚拟相机工具状态：{_virtualCameraDiagnostics.LastToolState}", "--", _overviewNeutralBrush));
        }

        events.Add((_cameraPreviewEnabled ? $"Windows 预览：{FormatCameraPreviewState()}" : "Windows 预览已关闭", "--", _cameraPreviewEnabled ? _overviewNeutralBrush : _warningBrush));

        if (events.Count == 0)
        {
            events.Add(("暂无摄像头事件", "--", _overviewMutedBrush));
        }
    }

    private void SetOverviewCameraEventRow(
        XamlShape dot,
        TextBlock textBlock,
        TextBlock timeBlock,
        string text,
        string time,
        Brush brush,
        bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        dot.Visibility = visibility;
        textBlock.Visibility = visibility;
        timeBlock.Visibility = visibility;
        dot.Fill = brush;
        textBlock.Text = text;
        timeBlock.Text = time;
    }

    private (string Time, string Text) FormatCameraEventLine(string line)
    {
        var text = line.Trim();
        var time = "--";
        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            var end = text.IndexOf(']');
            if (end > 1)
            {
                time = text[1..end];
                if (time.Length > 8)
                {
                    time = time[..8];
                }

                text = text[(end + 1)..].Trim();
            }
        }

        text = text
            .Replace("[camera_status]", "", StringComparison.OrdinalIgnoreCase)
            .Replace("[camera_error]", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return (time, string.IsNullOrWhiteSpace(text) ? "摄像头事件已更新" : text);
    }

    private Brush CameraEventBrush(string text)
    {
        if (ContainsAny(text, "error", "failed", "exception", "denied", "错误", "失败", "异常"))
        {
            return _dangerBrush;
        }

        if (ContainsAny(text, "waiting", "preparing", "stopped", "等待", "准备", "停止"))
        {
            return _warningBrush;
        }

        return _successBrush;
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        return values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private string FormatConfiguredCameraPortText()
    {
        return TryReadPort(OverviewCameraPortBox, out var port)
            ? port.ToString(CultureInfo.InvariantCulture)
            : "端口无效";
    }

    private static string FormatCameraCodec(string codec)
    {
        return codec.Trim().Equals("video/avc", StringComparison.OrdinalIgnoreCase)
            ? "video/avc (H.264)"
            : string.IsNullOrWhiteSpace(codec) ? "--" : codec;
    }

    private static string FormatCameraBitrate(double kbps)
    {
        if (kbps <= 0)
        {
            return "--";
        }

        return kbps >= 1000
            ? $"{kbps / 1000d:F1} Mbps"
            : $"{kbps:F0} Kbps";
    }

    private static string FormatCompactCount(long value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:F1}M";
        }

        return value >= 10_000
            ? $"{value / 1_000d:F1}K"
            : value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatByteCount(long bytes)
    {
        if (bytes <= 0)
        {
            return "--";
        }

        string[] units = { "B", "KB", "MB", "GB" };
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:F0} {units[unitIndex]}"
            : $"{value:F1} {units[unitIndex]}";
    }

    private void UpdateOverviewCameraState()
    {
        if (!StaticOverviewUi || OverviewCameraStatusText is null)
        {
            return;
        }

        var (statusText, hintText, statusBrush) = BuildOverviewCameraStatusView();
        var hostRunning = _hostProcess is { HasExited: false };

        _updatingOverviewCameraSwitch = true;
        try
        {
            OverviewCameraSwitch.IsOn = _overviewCameraRequestedEnabled;
        }
        finally
        {
            _updatingOverviewCameraSwitch = false;
        }

        OverviewCameraSwitch.IsEnabled = !_overviewCameraOperationInProgress;
        OverviewCameraStatusText.Text = statusText;
        OverviewCameraStatusText.Foreground = statusBrush;
        OverviewCameraHintText.Text = hintText;
        UpdateOverviewCameraOptionsEnabled(hostRunning);
        UpdateStaticDiagnosticsPage();
    }

    private (string StatusText, string HintText, Brush StatusBrush) BuildOverviewCameraStatusView()
    {
        var camera = _cameraDiagnostics;
        var virtualCamera = _virtualCameraDiagnostics;
        var hostRunning = _hostProcess is { HasExited: false };
        var selectedConfig = FormatSelectedOverviewCameraConfig();

        if (_overviewCameraOperationInProgress)
        {
            return _overviewCameraRequestedEnabled
                ? ("启动中", $"正在启动摄像头管线，配置：{selectedConfig}。", _overviewPrimaryBrush)
                : ("停止中", "正在停止摄像头管线和虚拟摄像头。", _overviewPrimaryBrush);
        }

        if (hostRunning
            && _overviewCameraRequestedEnabled
            && (IsCameraErrorState(camera.ServerState)
                || IsCameraErrorState(camera.ClientState)
                || HasCameraError()))
        {
            var error = FirstNonEmpty(camera.LastError, virtualCamera.LastError, "摄像头启动失败或运行异常。");
            return ("启动失败/错误", error, _dangerBrush);
        }

        if (!hostRunning)
        {
            if (virtualCamera.Running && _overviewCameraRequestedEnabled)
            {
                return ("虚拟摄像头运行中", "SideDock Camera 已在 Windows 中运行；主机未启动，Android 暂不会供帧。", _successBrush);
            }

            return ("未启动", $"主机未启动。当前摄像头配置将在下次启动生效：{selectedConfig}。", _secondaryBrush);
        }

        if (IsCameraDisabledState(camera.ServerState)
            || IsCameraDisabledState(camera.ClientState)
            || !_overviewCameraRequestedEnabled)
        {
            return ("未启动", $"摄像头管线已关闭。下次启动配置：{selectedConfig}。", _secondaryBrush);
        }

        if (IsCameraPermissionMissing(camera))
        {
            return ("Android 未授权摄像头权限", "请在 Android 设备上允许 SideDock 使用摄像头。", _warningBrush);
        }

        if (IsCameraReceiving(camera))
        {
            return ("接收中", $"Android 摄像头流正在接收，虚拟摄像头{virtualCamera.RunningText}。", _successBrush);
        }

        if (virtualCamera.Running)
        {
            return ("虚拟摄像头运行中", "SideDock Camera 已在 Windows 中运行，正在等待 Android 摄像头供帧。", _successBrush);
        }

        if (IsCameraWaitingForAndroid(camera.ServerState)
            || IsCameraWaitingForAndroid(camera.ClientState))
        {
            return ("等待 Android 设备", $"等待 Android 连接并发送摄像头流。当前配置：{selectedConfig}。", _warningBrush);
        }

        return ("等待 Android 设备", $"主机已运行，正在等待 Android 摄像头状态。当前配置：{selectedConfig}。", _warningBrush);
    }

    private void UpdateOverviewCameraOptionsEnabled(bool hostRunning)
    {
        var enabled = !hostRunning && !_overviewCameraOperationInProgress;
        SyncOverviewCameraPagePortFromConnection();
        OverviewCameraResolutionCombo.IsEnabled = enabled;
        OverviewCameraFrameRateCombo.IsEnabled = enabled;
        OverviewCameraPageFacingCombo.IsEnabled = enabled;
        OverviewCameraPageResolutionCombo.IsEnabled = enabled;
        OverviewCameraPageFrameRateCombo.IsEnabled = enabled;
        OverviewCameraPageCodecCombo.IsEnabled = false;
        OverviewCameraPagePortBox.IsEnabled = enabled;
        OverviewCameraSettingsButton.IsEnabled = !_overviewCameraOperationInProgress;
        OverviewCameraPageConfigHintText.Text = hostRunning
            ? "主机运行中，镜头、分辨率、帧率和端口会在下次启动摄像头管线时生效。"
            : "镜头、分辨率、帧率和端口会在下次启动摄像头管线时生效。编码格式由当前链路上报。";
    }

    private bool HasCameraError()
    {
        return !string.IsNullOrWhiteSpace(_cameraDiagnostics.LastError)
            || !string.IsNullOrWhiteSpace(_virtualCameraDiagnostics.LastError);
    }

    private static bool IsCameraErrorState(string state)
    {
        return StateEquals(state, "unavailable")
            || StateEquals(state, "error")
            || StateEquals(state, "failed");
    }

    private static bool IsCameraDisabledState(string state)
    {
        return StateEquals(state, "disabled")
            || StateEquals(state, "idle");
    }

    private static bool IsCameraWaitingForAndroid(string state)
    {
        return StateEquals(state, "listening")
            || StateEquals(state, "connected")
            || StateEquals(state, "disconnected")
            || StateEquals(state, "preparing")
            || StateEquals(state, "unknown");
    }

    private static bool IsCameraPermissionMissing(CameraDiagnosticsState camera)
    {
        return StateEquals(camera.ServerState, "waiting_permission")
            || StateEquals(camera.ClientState, "waiting_permission")
            || StateEquals(camera.ServerState, "authorization_required")
            || StateEquals(camera.ClientState, "authorization_required");
    }

    private static bool IsCameraReceiving(CameraDiagnosticsState camera)
    {
        return StateEquals(camera.ServerState, "receiving")
            || StateEquals(camera.ClientState, "capturing");
    }

    private static bool StateEquals(string state, string expected)
    {
        return state.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private string FormatSelectedOverviewCameraConfig()
    {
        var (width, height) = SelectedOverviewCameraResolution();
        return $"{width}x{height}@{SelectedOverviewCameraFps()}";
    }

    private async Task RunVirtualCameraCommandAsync(string command)
    {
        SetVirtualCameraButtonsEnabled(false);
        _virtualCameraDiagnostics.LastError = "";
        _virtualCameraDiagnostics.LastToolState = command;
        UpdateCameraStatusView();

        try
        {
            var actualCommand = command;
            if (command.Equals("ensure-start", StringComparison.OrdinalIgnoreCase))
            {
                await EnsureVirtualCameraMachineRegistrationAsync();
                actualCommand = "start";
            }

            var result = await RunVirtualCameraToolAsync(actualCommand, scope: "machine");
            if (result.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(result.Stderr)
                    ? $"虚拟摄像头工具退出码 {result.ExitCode}"
                    : result.Stderr.Trim();
                _virtualCameraDiagnostics.LastError = message;
                UpdateCameraStatusView();
                return;
            }

            ApplyVirtualCameraStatusJson(result.Stdout);
            RefreshVirtualCameraStatusFromFiles();
        }
        catch (Exception ex)
        {
            _virtualCameraDiagnostics.LastError = ex.Message;
            UpdateCameraStatusView();
        }
        finally
        {
            SetVirtualCameraButtonsEnabled(true);
        }
    }

    private async Task StartCameraPipelineAsync()
    {
        SetCameraPreviewEnabled(true);
        await SendHostCameraConfigCommandAsync(enabled: true);
        await RunVirtualCameraCommandAsync("ensure-start");
    }

    private async Task StopCameraPipelineAsync()
    {
        SetCameraPreviewEnabled(false);
        await SendHostCameraConfigCommandAsync(enabled: false);
        await RunVirtualCameraCommandAsync("stop");
    }

    private async Task RestartCameraPipelineFromUiAsync()
    {
        if (_overviewCameraOperationInProgress)
        {
            UpdateOverviewCameraState();
            return;
        }

        _overviewCameraRequestedEnabled = true;
        _overviewCameraOperationInProgress = true;
        UpdateOverviewCameraState();
        try
        {
            await StopCameraPipelineAsync();
            await StartCameraPipelineAsync();
            await RefreshVirtualCameraStatusAsync();
        }
        finally
        {
            _overviewCameraOperationInProgress = false;
            UpdateOverviewCameraState();
            UpdateCameraStatusView();
        }
    }

    private async Task SendHostCameraConfigCommandAsync(bool enabled)
    {
        if (_hostProcess is not { HasExited: false })
        {
            return;
        }

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, DefaultCameraCommandPort);
            await using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

            var request = JsonSerializer.Serialize(new
            {
                v = 1,
                type = "host_camera_config",
                seq = 1,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                payload = new { enabled }
            });
            await writer.WriteLineAsync(request);

            var response = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (string.IsNullOrWhiteSpace(response))
            {
                throw new IOException("摄像头控制通道没有返回响应。");
            }

            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var ok = root.TryGetProperty("ok", out var okProperty)
                && okProperty.ValueKind is JsonValueKind.True or JsonValueKind.False
                && okProperty.GetBoolean();
            if (!ok)
            {
                var message = root.TryGetProperty("message", out var messageProperty)
                    ? messageProperty.GetString()
                    : "摄像头控制命令失败。";
                throw new IOException(string.IsNullOrWhiteSpace(message) ? "摄像头控制命令失败。" : message);
            }

            _cameraDiagnostics.ClientState = enabled ? "preparing" : "disabled";
            _cameraDiagnostics.LastError = "";
            UpdateCameraStatusView();
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or JsonException)
        {
            _cameraDiagnostics.LastError = ex.Message;
            UpdateCameraStatusView();
        }
    }

    private async Task RefreshVirtualCameraStatusAsync()
    {
        try
        {
            var result = await RunVirtualCameraToolAsync("status", scope: "machine");
            if (result.ExitCode == 0)
            {
                ApplyVirtualCameraStatusJson(result.Stdout);
            }
            else if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                _virtualCameraDiagnostics.LastError = result.Stderr.Trim();
            }
        }
        catch (Exception ex)
        {
            _virtualCameraDiagnostics.LastError = ex.Message;
        }

        RefreshVirtualCameraStatusFromFiles();
        UpdateCameraStatusView();
    }

    private async Task EnsureVirtualCameraMachineRegistrationAsync()
    {
        _virtualCameraToolPath ??= ResolveVirtualCameraToolPath();
        _virtualCameraMediaSourcePath ??= ResolveVirtualCameraMediaSourcePath();

        if (IsMachineVirtualCameraRegistered(_virtualCameraMediaSourcePath))
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _virtualCameraToolPath,
            Arguments = string.Join(" ", new[]
            {
                "install",
                "--scope",
                "machine",
                "--dll",
                _virtualCameraMediaSourcePath
            }.Select(QuoteArgument)),
            WorkingDirectory = Path.GetDirectoryName(_virtualCameraToolPath) ?? Environment.CurrentDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 {VirtualCameraToolExe}。");
        if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(60)))
        {
            throw new TimeoutException("虚拟摄像头机器级注册超时。");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"虚拟摄像头机器级注册失败，退出码 {process.ExitCode}。");
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Process may have exited between timeout and kill.
            }

            return false;
        }
    }

    private static bool IsMachineVirtualCameraRegistered(string expectedDllPath)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey($@"Software\Classes\CLSID\{VirtualCameraMediaSourceClsid}\InProcServer32");
            var registeredPath = key?.GetValue(null) as string;
            return !string.IsNullOrWhiteSpace(registeredPath)
                && File.Exists(registeredPath)
                && string.Equals(
                    Path.GetFullPath(registeredPath),
                    Path.GetFullPath(expectedDllPath),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<VirtualCameraToolResult> RunVirtualCameraToolAsync(string command, string scope)
    {
        _virtualCameraToolPath ??= ResolveVirtualCameraToolPath();
        var args = new List<string>
        {
            command,
            "--scope",
            scope,
            "--lifetime",
            "system",
            "--access",
            "currentUser"
        };

        if (command.Equals("install", StringComparison.OrdinalIgnoreCase)
            || command.Equals("register", StringComparison.OrdinalIgnoreCase)
            || command.Equals("ensure-start", StringComparison.OrdinalIgnoreCase))
        {
            _virtualCameraMediaSourcePath ??= ResolveVirtualCameraMediaSourcePath();
            args.Add("--dll");
            args.Add(_virtualCameraMediaSourcePath);
        }

        var workingDirectory = Path.GetDirectoryName(_virtualCameraToolPath) ?? Environment.CurrentDirectory;
        var startInfo = new ProcessStartInfo
        {
            FileName = _virtualCameraToolPath,
            Arguments = string.Join(" ", args.Select(QuoteArgument)),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 {VirtualCameraToolExe}。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new VirtualCameraToolResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Process may have exited between timeout and kill.
            }

            return new VirtualCameraToolResult(-1, "", "虚拟摄像头工具超时。");
        }
    }

    private void RefreshVirtualCameraStatusFromFiles()
    {
        TryApplyVirtualCameraServedStatusFile();
        TryApplyVirtualCameraToolStatusFile();
        UpdateCameraStatusView();
    }

    private void TryApplyVirtualCameraServedStatusFile()
    {
        try
        {
            var path = VirtualCameraServedStatusPath;
            if (!File.Exists(path))
            {
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            ApplyServedFrameElement(document.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _virtualCameraDiagnostics.LastError = ex.Message;
        }
    }

    private void TryApplyVirtualCameraToolStatusFile()
    {
        try
        {
            var path = VirtualCameraToolStatusPath;
            if (!File.Exists(path))
            {
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (TryGetString(document.RootElement, "state", out var state))
            {
                _virtualCameraDiagnostics.LastToolState = state;
                if (state.Equals("started", StringComparison.OrdinalIgnoreCase))
                {
                    _virtualCameraDiagnostics.Running = true;
                }
                else if (state.Equals("stopped", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("removed", StringComparison.OrdinalIgnoreCase))
                {
                    _virtualCameraDiagnostics.Running = false;
                }
            }

            if (TryGetString(document.RootElement, "error", out var error) && !string.IsNullOrWhiteSpace(error))
            {
                _virtualCameraDiagnostics.LastError = error;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _virtualCameraDiagnostics.LastError = ex.Message;
        }
    }

    private void ApplyVirtualCameraStatusJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (TryGetBoolean(root, "registered", out var registered))
        {
            _virtualCameraDiagnostics.Registered = registered;
        }

        if (TryGetBoolean(root, "running", out var running))
        {
            _virtualCameraDiagnostics.Running = running;
        }

        if (root.TryGetProperty("registeredScopes", out var scopes))
        {
            _virtualCameraDiagnostics.RegisteredScopeSummary = FormatVirtualCameraRegistrationScopes(scopes);
        }

        if (root.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
        {
            _virtualCameraDiagnostics.DeviceCount = devices.GetArrayLength();
        }

        if (root.TryGetProperty("servedFrame", out var servedFrame) && servedFrame.ValueKind == JsonValueKind.Object)
        {
            ApplyServedFrameElement(servedFrame);
        }

        if (root.TryGetProperty("lastToolState", out var toolState) && toolState.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(toolState, "state", out var state))
            {
                _virtualCameraDiagnostics.LastToolState = state;
            }

            if (TryGetString(toolState, "error", out var error) && !string.IsNullOrWhiteSpace(error))
            {
                _virtualCameraDiagnostics.LastError = error;
            }
        }
    }

    private void ApplyServedFrameElement(JsonElement element)
    {
        if (TryGetInt64(element, "servedAtUnixMs", out var servedAtUnixMs) && servedAtUnixMs > 0)
        {
            _virtualCameraDiagnostics.LastServedAt = DateTimeOffset.FromUnixTimeMilliseconds(servedAtUnixMs);
        }

        if (TryGetInt64(element, "sourceFrameSequence", out var sourceSequence))
        {
            _virtualCameraDiagnostics.SourceFrameSequence = sourceSequence;
        }

        if (TryGetString(element, "frameKind", out var frameKind))
        {
            _virtualCameraDiagnostics.FrameKind = frameKind;
        }

        if (TryGetString(element, "lastError", out var error) && !string.IsNullOrWhiteSpace(error))
        {
            _virtualCameraDiagnostics.LastError = error;
        }
    }

    private static string FormatVirtualCameraRegistrationScopes(JsonElement scopes)
    {
        var parts = new List<string>();
        if (scopes.TryGetProperty("user", out var user)
            && TryGetBoolean(user, "registered", out var userRegistered)
            && userRegistered)
        {
            parts.Add("user");
        }

        if (scopes.TryGetProperty("machine", out var machine)
            && TryGetBoolean(machine, "registered", out var machineRegistered)
            && machineRegistered)
        {
            parts.Add("machine");
        }

        return parts.Count == 0 ? "" : string.Join(",", parts);
    }

    private static string FormatVirtualCameraServedAt(DateTimeOffset? value)
    {
        if (value is null)
        {
            return "--";
        }

        var age = DateTimeOffset.UtcNow - value.Value.ToUniversalTime();
        if (age.TotalSeconds < 1)
        {
            return "刚刚";
        }

        if (age.TotalSeconds < 60)
        {
            return $"{age.TotalSeconds:F0}s";
        }

        return value.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private void SetVirtualCameraButtonsEnabled(bool enabled)
    {
        StartVirtualCameraButton.IsEnabled = enabled;
        StopVirtualCameraButton.IsEnabled = enabled;
        RefreshVirtualCameraButton.IsEnabled = enabled;
        OverviewStartVirtualCameraButton.IsEnabled = enabled;
        OverviewStopVirtualCameraButton.IsEnabled = enabled;
    }

    private string ResolveVirtualCameraToolPath()
    {
        foreach (var candidate in EnumerateVirtualCameraToolCandidates())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException(
            $"未找到 {VirtualCameraToolExe}。请先构建 SideDock.VirtualCamera.Tool。");
    }

    private string ResolveVirtualCameraMediaSourcePath()
    {
        foreach (var candidate in EnumerateVirtualCameraMediaSourceCandidates())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException(
            $"未找到 {VirtualCameraMediaSourceDll}。请先构建 SideDock.VirtualCamera.MediaSource。");
    }

    private IEnumerable<string> EnumerateVirtualCameraToolCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, VirtualCameraToolExe);
        yield return Path.Combine(baseDirectory, "SideDock.VirtualCamera.Tool", VirtualCameraToolExe);

        if (_payloadRoot is not null)
        {
            yield return Path.Combine(_payloadRoot, VirtualCameraToolExe);
            yield return Path.Combine(_payloadRoot, "SideDock.VirtualCamera.Tool", VirtualCameraToolExe);
        }

        foreach (var configuration in new[] { "Release", "Debug" })
        {
            yield return Path.GetFullPath(Path.Combine(
                baseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "SideDock.VirtualCamera.Tool",
                "bin",
                configuration,
                "net8.0-windows10.0.22000.0",
                "win-x64",
                VirtualCameraToolExe));
            yield return Path.GetFullPath(Path.Combine(
                baseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "..",
                "windows-host",
                "SideDock.VirtualCamera.Tool",
                "bin",
                configuration,
                "net8.0-windows10.0.22000.0",
                "win-x64",
                VirtualCameraToolExe));
        }
    }

    private IEnumerable<string> EnumerateVirtualCameraMediaSourceCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, VirtualCameraMediaSourceDll);
        if (!string.IsNullOrWhiteSpace(_virtualCameraToolPath))
        {
            yield return Path.Combine(Path.GetDirectoryName(_virtualCameraToolPath) ?? baseDirectory, VirtualCameraMediaSourceDll);
        }

        if (_payloadRoot is not null)
        {
            yield return Path.Combine(_payloadRoot, VirtualCameraMediaSourceDll);
            yield return Path.Combine(_payloadRoot, "SideDock.VirtualCamera.MediaSource", VirtualCameraMediaSourceDll);
        }

        foreach (var configuration in new[] { "Release", "Debug" })
        {
            yield return Path.GetFullPath(Path.Combine(
                baseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "SideDock.VirtualCamera.MediaSource",
                "x64",
                configuration,
                VirtualCameraMediaSourceDll));
            yield return Path.GetFullPath(Path.Combine(
                baseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "..",
                "windows-host",
                "SideDock.VirtualCamera.MediaSource",
                "x64",
                configuration,
                VirtualCameraMediaSourceDll));
        }
    }

    private static bool TryGetBoolean(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        return false;
    }

    private static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt64(out value);
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = "";
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        return true;
    }

    private static string VirtualCameraStatusDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SideDock");

    private static string VirtualCameraServedStatusPath =>
        Path.Combine(VirtualCameraStatusDirectory, "virtual-camera-status.json");

    private static string VirtualCameraToolStatusPath =>
        Path.Combine(VirtualCameraStatusDirectory, "virtual-camera-tool-status.json");

    private static string FormatCameraAge(string isoTimestamp)
    {
        if (string.IsNullOrWhiteSpace(isoTimestamp)
            || !DateTimeOffset.TryParse(isoTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            return "--";
        }

        var age = DateTimeOffset.UtcNow - timestamp.ToUniversalTime();
        if (age.TotalSeconds < 1)
        {
            return "刚刚";
        }

        if (age.TotalSeconds < 60)
        {
            return $"{age.TotalSeconds:F0}s";
        }

        return timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private string BuildAudioDiagnosticsReport()
    {
        var hostLog = _currentHostLog;
        var recentAudioLines = SnapshotRecentAudioLogLines();
        var recentCameraLines = SnapshotRecentCameraLogLines();
        var report = new StringBuilder();
        report.AppendLine("SideDock 音频设备诊断报告");
        report.AppendLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine();

        report.AppendLine("---- 当前状态 ----");
        report.AppendLine($"主机状态: {OverallStatusText.Text}");
        report.AppendLine($"主机进程: {FormatHostProcessState()}");
        report.AppendLine($"音频总状态: {AudioOverallStatusText.Text}");
        report.AppendLine($"Android 设备: {AudioCurrentDeviceText.Text}");
        report.AppendLine($"麦克风状态: {MicrophoneStatusText.Text}");
        report.AppendLine($"音响状态: {SpeakerStatusText.Text}");
        report.AppendLine($"提示: {_lastAudioHint}");
        report.AppendLine($"最后麦克风错误: {FormatOptional(_lastMicrophoneErrorMessage)}");
        report.AppendLine($"最后音响错误: {FormatOptional(_lastSpeakerErrorMessage)}");
        report.AppendLine($"最后麦克风系统端点消息: {FormatOptional(_lastMicrophoneSystemEndpointMessage)}");
        report.AppendLine($"最后音响系统端点消息: {FormatOptional(_lastSpeakerSystemEndpointMessage)}");
        report.AppendLine($"最后麦克风状态日志: {FormatOptional(_lastMicrophoneStatusLine)}");
        report.AppendLine($"最后音响状态日志: {FormatOptional(_lastSpeakerStatusLine)}");
        report.AppendLine($"最后麦克风错误日志: {FormatOptional(_lastMicrophoneErrorLine)}");
        report.AppendLine($"最后音响错误日志: {FormatOptional(_lastSpeakerErrorLine)}");
        report.AppendLine($"最后摄像头状态日志: {FormatOptional(_lastCameraStatusLine)}");
        report.AppendLine($"最后摄像头错误日志: {FormatOptional(_lastCameraErrorLine)}");
        report.AppendLine($"最后摄像头错误: {FormatOptional(_lastCameraErrorMessage)}");
        AppendCameraDiagnosticsSummary(report);
        report.AppendLine();

        report.AppendLine("---- 配置 ----");
        report.AppendLine($"启用音频设备: {AudioDeviceSwitch.IsOn}");
        report.AppendLine($"启用麦克风: {MicrophoneSwitch.IsOn}");
        report.AppendLine($"启用音响: {SpeakerSwitch.IsOn}");
        report.AppendLine("音频后端: wasapi-virtual-cable");
        report.AppendLine($"电脑声音 loopback 输出端点状态: {_speakerCaptureEndpointDiagnostics.Summary}");
        report.AppendLine($"电脑声音 loopback 输出 endpoint id: {FormatOptional(_boundSpeakerCaptureEndpointId)}");
        report.AppendLine($"电脑声音 loopback 输出端点名称: {FormatOptional(_boundSpeakerCaptureEndpointName)}");
        report.AppendLine($"Android 麦克风写入端点状态: {_microphoneRenderEndpointDiagnostics.Summary}");
        report.AppendLine($"Android 麦克风写入 endpoint id: {FormatOptional(_boundMicrophoneRenderEndpointId)}");
        report.AppendLine($"Android 麦克风写入端点名称: {FormatOptional(_boundMicrophoneRenderEndpointName)}");
        var audioPortText = StaticOverviewUi ? FormatNumberBox(OverviewAudioPortBox) : DefaultAudioPort.ToString(CultureInfo.InvariantCulture);
        var cameraPortText = StaticOverviewUi ? FormatNumberBox(OverviewCameraPortBox) : DefaultCameraPort.ToString(CultureInfo.InvariantCulture);
        report.AppendLine($"音频端口: {audioPortText}");
        report.AppendLine($"摄像头端口: {cameraPortText}");
        report.AppendLine($"摄像头方向: {Selected(CameraFacingCombo)}");
        report.AppendLine($"摄像头 reverse: tcp:{cameraPortText} -> tcp:{cameraPortText}");
        report.AppendLine($"控制端口: {FormatNumberBox(ControlPortBox)}");
        report.AppendLine($"视频端口: {FormatNumberBox(VideoPortBox)}");
        report.AppendLine($"视频源: {Selected(VideoSourceCombo)}");
        report.AppendLine($"分辨率: {Selected(ResolutionCombo)}");
        report.AppendLine($"刷新率: {Selected(RefreshRateCombo)}");
        report.AppendLine($"ADB 路径输入: {FormatOptional(AdbPathBox.Text.Trim())}");
        report.AppendLine($"ADB 选择设备: {FormatOptional(SelectedAdbSerial())}");
        report.AppendLine();

        report.AppendLine("---- Host 进程 ----");
        report.AppendLine($"Host 路径: {FormatOptional(hostLog?.HostPath ?? _hostPath)}");
        report.AppendLine($"工作目录: {FormatOptional(hostLog?.WorkingDirectory)}");
        report.AppendLine($"启动参数: {FormatOptional(hostLog?.Arguments ?? TryBuildArgumentsForDiagnostics())}");
        report.AppendLine($"ADB 路径: {FormatOptional(hostLog?.AdbPath)}");
        report.AppendLine($"ADB 设备: {FormatOptional(hostLog?.AdbSerial)}");
        report.AppendLine();

        report.AppendLine("---- 最近摄像头日志 ----");
        if (recentCameraLines.Length == 0)
        {
            report.AppendLine("(还没有捕获到 [CAMERA] 日志)");
        }
        else
        {
            foreach (var line in recentCameraLines)
            {
                report.AppendLine(line);
            }
        }

        report.AppendLine();

        report.AppendLine("---- 最近音频日志 ----");
        if (recentAudioLines.Length == 0)
        {
            report.AppendLine("(还没有捕获到 [AUDIO] 日志)");
        }
        else
        {
            foreach (var line in recentAudioLines)
            {
                report.AppendLine(line);
            }
        }

        report.AppendLine();
        report.AppendLine("---- 主机 stdout/stderr ----");
        if (hostLog is null)
        {
            report.AppendLine("(当前没有主机日志缓存)");
        }
        else
        {
            report.Append(hostLog.Snapshot());
        }

        return report.ToString();
    }

    private string BuildCameraDiagnosticsReport()
    {
        var hostLog = _currentHostLog;
        var recentCameraLines = SnapshotRecentCameraLogLines();
        var report = new StringBuilder();
        report.AppendLine("SideDock 摄像头诊断报告");
        report.AppendLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine();

        report.AppendLine("---- 当前状态 ----");
        report.AppendLine($"主机状态: {OverallStatusText.Text}");
        report.AppendLine($"主机进程: {FormatHostProcessState()}");
        report.AppendLine($"ADB reverse 状态: {AdbStatusText.Text}");
        var cameraPortText = StaticOverviewUi ? FormatNumberBox(OverviewCameraPortBox) : DefaultCameraPort.ToString(CultureInfo.InvariantCulture);
        report.AppendLine($"摄像头 reverse: tcp:{cameraPortText} -> tcp:{cameraPortText}");
        AppendCameraDiagnosticsSummary(report);
        report.AppendLine($"最后摄像头状态日志: {FormatOptional(_lastCameraStatusLine)}");
        report.AppendLine($"最后摄像头错误日志: {FormatOptional(_lastCameraErrorLine)}");
        report.AppendLine($"最后摄像头错误: {FormatOptional(_lastCameraErrorMessage)}");
        report.AppendLine();

        report.AppendLine("---- Host 进程 ----");
        report.AppendLine($"Host 路径: {FormatOptional(hostLog?.HostPath ?? _hostPath)}");
        report.AppendLine($"工作目录: {FormatOptional(hostLog?.WorkingDirectory)}");
        report.AppendLine($"启动参数: {FormatOptional(hostLog?.Arguments ?? TryBuildArgumentsForDiagnostics())}");
        report.AppendLine($"ADB 路径: {FormatOptional(hostLog?.AdbPath)}");
        report.AppendLine($"ADB 设备: {FormatOptional(hostLog?.AdbSerial)}");
        report.AppendLine();

        report.AppendLine("---- 最近摄像头日志 ----");
        if (recentCameraLines.Length == 0)
        {
            report.AppendLine("(还没有捕获到 [CAMERA] 日志)");
        }
        else
        {
            foreach (var line in recentCameraLines)
            {
                report.AppendLine(line);
            }
        }

        report.AppendLine();
        report.AppendLine("---- 主机 stdout/stderr ----");
        if (hostLog is null)
        {
            report.AppendLine("(当前没有主机日志缓存)");
        }
        else
        {
            report.Append(hostLog.Snapshot());
        }

        return report.ToString();
    }

    private void AppendCameraDiagnosticsSummary(StringBuilder report)
    {
        var camera = _cameraDiagnostics;
        report.AppendLine($"Camera server 状态: {FormatOptional(camera.ServerState)}");
        report.AppendLine($"Camera client 状态: {FormatOptional(camera.ClientState)}");
        report.AppendLine($"Android 权限: {FormatOptional(camera.PermissionText)}");
        report.AppendLine($"端口: {camera.Port}");
        report.AppendLine($"配置: {camera.Width}x{camera.Height}@{camera.Fps} {camera.Codec}");
        report.AppendLine($"接收包/帧/字节: {camera.Packets}/{camera.Frames}/{camera.Bytes}");
        report.AppendLine($"Keyframe/config 包: {camera.KeyFrames}/{camera.CodecConfigPackets}");
        report.AppendLine($"实际接收 fps: {camera.ApproxFps:F1}");
        report.AppendLine($"码率 kbps: {camera.ApproxKbps:F0}");
        report.AppendLine($"解码帧/错误: {camera.DecodedFrames}/{camera.DecodeErrors}");
        report.AppendLine($"最近解码滞后: {camera.DecodeLagMs:F1} ms");
        report.AppendLine($"Windows 预览: {(_cameraPreviewEnabled ? "启用" : "关闭")}");
        report.AppendLine($"共享预览帧序号: {_lastCameraPreviewSequence}");
        report.AppendLine($"共享预览最近时间: {FormatOptional(_lastCameraPreviewAt?.ToString("O"))}");
        report.AppendLine($"最近接收帧时间: {FormatOptional(camera.LastFrameAt)}");
        report.AppendLine($"最近解码帧时间: {FormatOptional(camera.LastDecodedFrameAt)}");
        report.AppendLine($"最近错误: {FormatOptional(camera.LastError)}");
        report.AppendLine($"虚拟摄像头注册: {_virtualCameraDiagnostics.RegistrationText}");
        report.AppendLine($"虚拟摄像头注册范围: {FormatOptional(_virtualCameraDiagnostics.RegisteredScopeSummary)}");
        report.AppendLine($"虚拟摄像头运行: {_virtualCameraDiagnostics.RunningText}");
        report.AppendLine($"虚拟摄像头设备数: {_virtualCameraDiagnostics.DeviceCount}");
        report.AppendLine($"虚拟摄像头最近供帧: {FormatOptional(_virtualCameraDiagnostics.LastServedAt?.ToString("O"))}");
        report.AppendLine($"虚拟摄像头供帧类型: {FormatOptional(_virtualCameraDiagnostics.FrameKind)}");
        report.AppendLine($"虚拟摄像头源帧序号: {_virtualCameraDiagnostics.SourceFrameSequence}");
        report.AppendLine($"虚拟摄像头最后工具状态: {FormatOptional(_virtualCameraDiagnostics.LastToolState)}");
        report.AppendLine($"虚拟摄像头最近错误: {FormatOptional(_virtualCameraDiagnostics.LastError)}");
        report.AppendLine($"虚拟摄像头状态文件: {VirtualCameraServedStatusPath}");
    }

    private string? TryBuildArgumentsForDiagnostics()
    {
        try
        {
            return BuildArguments();
        }
        catch (Exception ex)
        {
            return $"(无法生成启动参数：{ex.Message})";
        }
    }

    private void AppendRecentAudioLogLine(string line)
    {
        var entry = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {line}";
        lock (_audioLogGate)
        {
            _recentAudioLogLines.Enqueue(entry);
            while (_recentAudioLogLines.Count > MaxRecentAudioLogLines)
            {
                _recentAudioLogLines.Dequeue();
            }
        }
    }

    private string[] SnapshotRecentAudioLogLines()
    {
        lock (_audioLogGate)
        {
            return _recentAudioLogLines.ToArray();
        }
    }

    private void AppendRecentCameraLogLine(string line)
    {
        var entry = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {line}";
        lock (_cameraLogGate)
        {
            _recentCameraLogLines.Enqueue(entry);
            while (_recentCameraLogLines.Count > MaxRecentCameraLogLines)
            {
                _recentCameraLogLines.Dequeue();
            }
        }
    }

    private string[] SnapshotRecentCameraLogLines()
    {
        lock (_cameraLogGate)
        {
            return _recentCameraLogLines.ToArray();
        }
    }

    private void ClearAudioDiagnostics()
    {
        lock (_audioLogGate)
        {
            _recentAudioLogLines.Clear();
        }
        lock (_cameraLogGate)
        {
            _recentCameraLogLines.Clear();
        }

        _lastMicrophoneStatusLine = null;
        _lastSpeakerStatusLine = null;
        _lastMicrophoneErrorLine = null;
        _lastSpeakerErrorLine = null;
        _lastMicrophoneErrorMessage = null;
        _lastSpeakerErrorMessage = null;
        _lastMicrophoneSystemEndpointMessage = null;
        _lastSpeakerSystemEndpointMessage = null;
        _lastCameraStatusLine = null;
        _lastCameraErrorLine = null;
        _lastCameraErrorMessage = null;
        _lastVideoStatsLine = null;
        _lastEncoderStatsLine = null;
        _cameraDiagnostics.Reset();
        _videoDiagnostics.Reset();
        ResetHostRuntimeSampling();
        _cameraDiagnostics.Facing = NormalizeCameraFacing(Selected(CameraFacingCombo));
        var (cameraWidth, cameraHeight) = SelectedOverviewCameraResolution();
        _cameraDiagnostics.Width = cameraWidth;
        _cameraDiagnostics.Height = cameraHeight;
        _cameraDiagnostics.Fps = SelectedOverviewCameraFps();

        if (CopyAudioLogButtonText is not null)
        {
            CopyAudioLogButtonText.Text = "复制错误日志";
        }

        if (CopyCameraDiagnosticsButtonText is not null)
        {
            CopyCameraDiagnosticsButtonText.Text = "复制诊断";
        }

        UpdateCameraStatusView();
        UpdateOverviewRuntimeDiagnostics();
    }

    private string FormatHostProcessState()
    {
        var process = _hostProcess;
        if (process is null)
        {
            return "未运行";
        }

        try
        {
            return process.HasExited
                ? $"已退出，退出码 {process.ExitCode}"
                : $"运行中，PID {process.Id}";
        }
        catch
        {
            return "状态不可读取";
        }
    }

    private void HandleHostOutputLine(string line)
    {
        if (line.Contains("[CAMERA", StringComparison.OrdinalIgnoreCase))
        {
            HandleCameraHostOutputLine(line);
        }

        if (line.Contains("video stats", StringComparison.OrdinalIgnoreCase))
        {
            HandleVideoStatsHostOutputLine(line);
        }

        if (line.Contains("[ENCODER", StringComparison.OrdinalIgnoreCase)
            && (line.Contains(" stats ", StringComparison.OrdinalIgnoreCase)
                || line.Contains("stop generated=", StringComparison.OrdinalIgnoreCase)))
        {
            HandleEncoderStatsHostOutputLine(line);
        }

        if (!line.Contains("[AUDIO", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AppendRecentAudioLogLine(line);

        if (!line.Contains("mic-state=", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("mic-client-state=", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("speaker-state=", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("speaker-client-state=", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var isSpeaker = line.Contains("speaker-state=", StringComparison.OrdinalIgnoreCase)
            || line.Contains("speaker-client-state=", StringComparison.OrdinalIgnoreCase);
        var key = isSpeaker
            ? (line.Contains("speaker-client-state=", StringComparison.OrdinalIgnoreCase) ? "speaker-client-state=" : "speaker-state=")
            : (line.Contains("mic-client-state=", StringComparison.OrdinalIgnoreCase) ? "mic-client-state=" : "mic-state=");
        var state = ExtractLogValue(line, key);
        var nextStatus = state switch
        {
            "disabled" => AudioCapabilityStatus.Closed,
            "authorization_required" => AudioCapabilityStatus.AuthorizationRequired,
            "preparing" => AudioCapabilityStatus.Preparing,
            "available" => AudioCapabilityStatus.Available,
            "capturing" => AudioCapabilityStatus.Capturing,
            "playing" => AudioCapabilityStatus.Playing,
            "muted" => AudioCapabilityStatus.Muted,
            "reconnecting" => AudioCapabilityStatus.Reconnecting,
            "unavailable" => AudioCapabilityStatus.Error,
            _ => (AudioCapabilityStatus?)null
        };

        if (nextStatus is null)
        {
            return;
        }

        var errorMessage = ExtractLogTail(line, " message=");
        var systemEndpointMessage = ExtractLogFieldBefore(line, " systemEndpointMessage=", " message=");
        if (isSpeaker)
        {
            _speakerRuntimeStatus = nextStatus.Value;
            _lastSpeakerStatusLine = line;
            if (!string.IsNullOrWhiteSpace(systemEndpointMessage))
            {
                _lastSpeakerSystemEndpointMessage = systemEndpointMessage;
            }

            if (nextStatus == AudioCapabilityStatus.Error)
            {
                _lastSpeakerErrorLine = line;
                _lastSpeakerErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage;
            }
        }
        else
        {
            _microphoneRuntimeStatus = nextStatus.Value;
            _lastMicrophoneStatusLine = line;
            if (!string.IsNullOrWhiteSpace(systemEndpointMessage))
            {
                _lastMicrophoneSystemEndpointMessage = systemEndpointMessage;
            }

            if (nextStatus == AudioCapabilityStatus.Error)
            {
                _lastMicrophoneErrorLine = line;
                _lastMicrophoneErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage;
            }
        }

        if (nextStatus != AudioCapabilityStatus.Error && _audioOverrideStatus == AudioCapabilityStatus.Error)
        {
            _audioOverrideStatus = null;
        }

        var direction = isSpeaker ? AudioDirection.Speaker : AudioDirection.Microphone;
        var hint = nextStatus == AudioCapabilityStatus.Error && !string.IsNullOrWhiteSpace(errorMessage)
            ? AudioUnavailableHint(direction, errorMessage)
            : AudioStateHint(direction, nextStatus.Value);
        UpdateAudioState(hint);
    }

    private void HandleVideoStatsHostOutputLine(string line)
    {
        _lastVideoStatsLine = line;
        _videoDiagnostics.HasVideoStats = true;
        _videoDiagnostics.LastVideoStatsAt = DateTimeOffset.Now;
        _videoDiagnostics.FramesDecoded = ExtractLogLong(line, "decoded=", _videoDiagnostics.FramesDecoded);
        _videoDiagnostics.FramesRendered = ExtractLogLong(line, "rendered=", _videoDiagnostics.FramesRendered);
        _videoDiagnostics.PacketsReceived = ExtractLogLong(line, "packets=", _videoDiagnostics.PacketsReceived);
        _videoDiagnostics.DecodeFps = ExtractLogDouble(line, "decode=", _videoDiagnostics.DecodeFps);
        _videoDiagnostics.RenderFps = ExtractLogDouble(line, "render=", _videoDiagnostics.RenderFps);
        _videoDiagnostics.NewFrameFps = ExtractLogDouble(line, "new=", _videoDiagnostics.NewFrameFps);
        _videoDiagnostics.RepeatFrameFps = ExtractLogDouble(line, "repeat=", _videoDiagnostics.RepeatFrameFps);
        _videoDiagnostics.DecodeErrors = ExtractLogLong(line, "decodeErrors=", _videoDiagnostics.DecodeErrors);
        _videoDiagnostics.VideoReconnects = ExtractLogLong(line, "reconnects=", _videoDiagnostics.VideoReconnects);

        if (TryExtractLogDouble(line, "local=", out var localLatencyMs))
        {
            _videoDiagnostics.LocalPipelineLatencyMs = localLatencyMs;
        }

        if (TryExtractLogDouble(line, "e2e=", out var roughLatencyMs))
        {
            _videoDiagnostics.RoughLatencyMs = roughLatencyMs;
        }

        if (TryExtractLogDouble(line, "err=", out var latencyErrorBoundMs))
        {
            _videoDiagnostics.LatencyErrorBoundMs = latencyErrorBoundMs;
        }

        if (TryExtractLogLong(line, "droppedFrames=", out var droppedFrames))
        {
            _videoDiagnostics.HasDroppedFrameStats = true;
            _videoDiagnostics.DroppedFrames = droppedFrames;
        }

        UpdateOverviewRuntimeDiagnostics();
    }

    private void HandleEncoderStatsHostOutputLine(string line)
    {
        _lastEncoderStatsLine = line;
        _videoDiagnostics.HasEncoderStats = true;
        _videoDiagnostics.LastEncoderStatsAt = DateTimeOffset.Now;

        _videoDiagnostics.FramesGenerated = ExtractLogLong(line, "generated=", _videoDiagnostics.FramesGenerated);
        _videoDiagnostics.FramesEncoded = ExtractLogLong(line, "encoded=", _videoDiagnostics.FramesEncoded);
        _videoDiagnostics.FramesSent = ExtractLogLong(line, "sent=", _videoDiagnostics.FramesSent);
        _videoDiagnostics.FramesDropped = ExtractLogLong(line, "dropped=", _videoDiagnostics.FramesDropped);
        _videoDiagnostics.LateFrames = ExtractLogLong(line, "late=", _videoDiagnostics.LateFrames);
        _videoDiagnostics.StreamFps = ExtractLogDouble(line, "streamFps=", _videoDiagnostics.StreamFps);

        if (TryExtractLogDouble(line, "localLatencyP95=", out var localLatencyP95Ms))
        {
            _videoDiagnostics.LocalLatencyP95Ms = localLatencyP95Ms;
        }

        if (TryExtractLogDouble(line, "kbps=", out var outputKbps))
        {
            _videoDiagnostics.OutputKbps = outputKbps;
        }

        UpdateOverviewRuntimeDiagnostics();
    }

    private void HandleCameraHostOutputLine(string line)
    {
        AppendRecentCameraLogLine(line);

        if (!line.Contains("camera-state=", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("camera-client-state=", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var key = line.Contains("camera-client-state=", StringComparison.OrdinalIgnoreCase)
            ? "camera-client-state="
            : "camera-state=";
        var state = ExtractLogValue(line, key);
        _lastCameraStatusLine = line;
        var isClient = key.StartsWith("camera-client", StringComparison.OrdinalIgnoreCase);

        if (isClient)
        {
            _cameraDiagnostics.ClientState = state;
            _cameraDiagnostics.PermissionGranted = ExtractLogValue(line, "permission=");
            _cameraDiagnostics.Port = ExtractLogInt(line, "port=", _cameraDiagnostics.Port);
            ApplyCameraSize(ExtractLogValue(line, "size="));
            _cameraDiagnostics.Fps = ExtractLogInt(line, "fps=", _cameraDiagnostics.Fps);
            _cameraDiagnostics.Codec = NonEmpty(ExtractLogValue(line, "codec="), _cameraDiagnostics.Codec);
            _cameraDiagnostics.Facing = NormalizeCameraFacing(NonEmpty(ExtractLogValue(line, "facing="), _cameraDiagnostics.Facing));
            _cameraDiagnostics.ClientPackets = ExtractLogLong(line, "packets=", _cameraDiagnostics.ClientPackets);
            _cameraDiagnostics.ClientBytes = ExtractLogLong(line, "bytes=", _cameraDiagnostics.ClientBytes);
            _cameraDiagnostics.ClientKeyFrames = ExtractLogLong(line, "keyFrames=", _cameraDiagnostics.ClientKeyFrames);
            _cameraDiagnostics.ClientCodecConfigPackets = ExtractLogLong(line, "codecConfigPackets=", _cameraDiagnostics.ClientCodecConfigPackets);
        }
        else
        {
            _cameraDiagnostics.ServerState = state;
            _cameraDiagnostics.Port = ExtractLogInt(line, "port=", _cameraDiagnostics.Port);
            ApplyCameraConfig(ExtractLogValue(line, "config="));
            _cameraDiagnostics.Codec = NonEmpty(ExtractLogValue(line, "codec="), _cameraDiagnostics.Codec);
            _cameraDiagnostics.Facing = NormalizeCameraFacing(NonEmpty(ExtractLogValue(line, "facing="), _cameraDiagnostics.Facing));
            _cameraDiagnostics.Packets = ExtractLogLong(line, "packets=", _cameraDiagnostics.Packets);
            _cameraDiagnostics.Frames = ExtractLogLong(line, "frames=", _cameraDiagnostics.Frames);
            _cameraDiagnostics.Bytes = ExtractLogLong(line, "bytes=", _cameraDiagnostics.Bytes);
            _cameraDiagnostics.KeyFrames = ExtractLogLong(line, "keyFrames=", _cameraDiagnostics.KeyFrames);
            _cameraDiagnostics.CodecConfigPackets = ExtractLogLong(line, "codecConfigPackets=", _cameraDiagnostics.CodecConfigPackets);
            _cameraDiagnostics.DecodedFrames = ExtractLogLong(line, "decodedFrames=", _cameraDiagnostics.DecodedFrames);
            _cameraDiagnostics.DecodeErrors = ExtractLogLong(line, "decodeErrors=", _cameraDiagnostics.DecodeErrors);
            _cameraDiagnostics.PreviewFrameSequence = ExtractLogLong(line, "previewSeq=", _cameraDiagnostics.PreviewFrameSequence);
            _cameraDiagnostics.DecodeLagMs = ExtractLogDouble(line, "decodeLagMs=", _cameraDiagnostics.DecodeLagMs);
            _cameraDiagnostics.ApproxFps = ExtractLogDouble(line, "approxFps=", _cameraDiagnostics.ApproxFps);
            _cameraDiagnostics.ApproxKbps = ExtractLogDouble(line, "approxKbps=", _cameraDiagnostics.ApproxKbps);
            _cameraDiagnostics.LastFrameAt = NonEmpty(ExtractLogValue(line, "lastFrameAt="), _cameraDiagnostics.LastFrameAt);
            _cameraDiagnostics.LastDecodedFrameAt = NonEmpty(ExtractLogValue(line, "lastDecodedFrameAt="), _cameraDiagnostics.LastDecodedFrameAt);
        }

        var decodeError = ExtractLogTail(line, " lastError=");
        if (!string.IsNullOrWhiteSpace(decodeError))
        {
            _cameraDiagnostics.LastError = decodeError;
        }

        if (state is "unavailable" or "disconnected")
        {
            _lastCameraErrorLine = line;
            var errorMessage = ExtractLogTail(line, " message=");
            _lastCameraErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage;
            _cameraDiagnostics.LastError = string.IsNullOrWhiteSpace(errorMessage) ? state : errorMessage;
        }

        UpdateCameraStatusView();
    }

    private static string ExtractLogValue(string line, string key)
    {
        var start = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        start += key.Length;
        var end = line.IndexOf(' ', start);
        return (end < 0 ? line[start..] : line[start..end]).Trim();
    }

    private static string ExtractLogTail(string line, string key)
    {
        var start = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        start += key.Length;
        return line[start..].Trim();
    }

    private static string ExtractLogFieldBefore(string line, string key, string nextKey)
    {
        var start = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        start += key.Length;
        var end = line.IndexOf(nextKey, start, StringComparison.OrdinalIgnoreCase);
        return (end < 0 ? line[start..] : line[start..end]).Trim();
    }

    private static int ExtractLogInt(string line, string key, int fallback)
    {
        var value = ExtractLogValue(line, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static long ExtractLogLong(string line, string key, long fallback)
    {
        var value = ExtractLogValue(line, key);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static double ExtractLogDouble(string line, string key, double fallback)
    {
        var value = ExtractLogValue(line, key);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static bool TryExtractLogLong(string line, string key, out long parsed)
    {
        var value = ExtractLogValue(line, key);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }

    private static bool TryExtractLogDouble(string line, string key, out double parsed)
    {
        var value = ExtractLogValue(line, key);
        return TryParseLogDouble(value, out parsed);
    }

    private static bool TryParseLogDouble(string value, out double parsed)
    {
        parsed = 0;
        value = value.Trim();
        if (value.StartsWith("+/-", StringComparison.Ordinal))
        {
            value = value[3..];
        }

        var end = 0;
        while (end < value.Length)
        {
            var c = value[end];
            if (!char.IsDigit(c) && c is not '.' and not '-' and not '+')
            {
                break;
            }

            end++;
        }

        return end > 0
            && double.TryParse(value[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
    }

    private void ApplyCameraSize(string value)
    {
        var parts = value.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return;
        }

        if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) && width > 0)
        {
            _cameraDiagnostics.Width = width;
        }

        if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) && height > 0)
        {
            _cameraDiagnostics.Height = height;
        }
    }

    private void ApplyCameraConfig(string value)
    {
        var atIndex = value.IndexOf('@', StringComparison.Ordinal);
        var size = atIndex >= 0 ? value[..atIndex] : value;
        ApplyCameraSize(size);
        if (atIndex >= 0
            && int.TryParse(value[(atIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fps)
            && fps > 0)
        {
            _cameraDiagnostics.Fps = fps;
        }
    }

    private static string NonEmpty(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string NormalizeCameraFacing(string value)
    {
        return value.Trim().Equals("front", StringComparison.OrdinalIgnoreCase) ? "front" : "back";
    }

    private static string AudioStateHint(AudioDirection direction, AudioCapabilityStatus status)
    {
        var isMicrophone = direction == AudioDirection.Microphone;
        return status switch
        {
            AudioCapabilityStatus.Closed => isMicrophone ? "SideDock 麦克风已关闭，副屏仍在运行。" : "SideDock 音响已关闭，副屏仍在运行。",
            AudioCapabilityStatus.AuthorizationRequired => "需要在 Android 端允许麦克风权限。",
            AudioCapabilityStatus.Preparing => isMicrophone ? "正在准备 SideDock 麦克风。" : "正在准备 SideDock 音响。",
            AudioCapabilityStatus.Available => isMicrophone ? "SideDock 麦克风可在 Windows 或应用中选择。" : "SideDock 音响可在 Windows 或应用中选择。",
            AudioCapabilityStatus.Capturing => "Android 麦克风正在采集中。",
            AudioCapabilityStatus.Playing => "SideDock 音响正在播放。",
            AudioCapabilityStatus.Muted => isMicrophone ? "SideDock 麦克风已静音。" : "SideDock 音响已静音。",
            AudioCapabilityStatus.Reconnecting => "正在恢复音频连接。",
            AudioCapabilityStatus.Error => isMicrophone ? "SideDock 麦克风暂不可用。" : "SideDock 音响暂不可用。",
            _ => "等待 Android 设备连接。"
        };
    }

    private static string AudioUnavailableHint(AudioDirection direction, string message)
    {
        return direction == AudioDirection.Microphone
            ? $"SideDock 麦克风暂不可用：{message}"
            : $"SideDock 音响暂不可用：{message}";
    }

    private void LoadAudioPreferences()
    {
        _loadingAudioPreferences = true;
        try
        {
            var path = BuildAudioPreferencesPath();
            if (!File.Exists(path))
            {
                return;
            }

            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            var preferences = JsonSerializer.Deserialize<AudioPreferences>(json);
            if (preferences is null)
            {
                return;
            }

            AudioDeviceSwitch.IsOn = preferences.AudioDeviceEnabled;
            MicrophoneSwitch.IsOn = preferences.MicrophoneEnabled;
            SpeakerSwitch.IsOn = preferences.SpeakerEnabled;
            _boundMicrophoneRenderEndpointId = preferences.MicrophoneRenderEndpoint?.EndpointId;
            _boundMicrophoneRenderEndpointName = preferences.MicrophoneRenderEndpoint?.DisplayName;
            var speakerLoopbackEndpoint = preferences.SpeakerOutputLoopbackEndpoint ?? preferences.SpeakerCaptureEndpoint;
            _boundSpeakerCaptureEndpointId = speakerLoopbackEndpoint?.EndpointId;
            _boundSpeakerCaptureEndpointName = speakerLoopbackEndpoint?.DisplayName;
        }
        catch
        {
            // Keep default-on intent if the local preferences file cannot be read.
        }
        finally
        {
            _loadingAudioPreferences = false;
        }
    }

    private void SaveAudioPreferences()
    {
        if (_loadingAudioPreferences)
        {
            return;
        }

        try
        {
            var path = BuildAudioPreferencesPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var existingPreferences = !_audioEndpointChoicesReady
                ? TryReadAudioPreferences(path)
                : null;
            var microphoneRenderEndpoint = BuildAudioEndpointBinding(
                _boundMicrophoneRenderEndpointId,
                _boundMicrophoneRenderEndpointName);
            var speakerOutputLoopbackEndpoint = BuildAudioEndpointBinding(
                _boundSpeakerCaptureEndpointId,
                _boundSpeakerCaptureEndpointName);
            if (!_audioEndpointChoicesReady)
            {
                microphoneRenderEndpoint = PreserveExistingBindingWhenEmpty(
                    microphoneRenderEndpoint,
                    existingPreferences?.MicrophoneRenderEndpoint);
                speakerOutputLoopbackEndpoint = PreserveExistingBindingWhenEmpty(
                    speakerOutputLoopbackEndpoint,
                    existingPreferences?.SpeakerOutputLoopbackEndpoint ?? existingPreferences?.SpeakerCaptureEndpoint);
            }

            var preferences = new AudioPreferences
            {
                AudioDeviceEnabled = AudioDeviceSwitch.IsOn,
                MicrophoneEnabled = MicrophoneSwitch.IsOn,
                SpeakerEnabled = SpeakerSwitch.IsOn,
                MicrophoneRenderEndpoint = microphoneRenderEndpoint,
                SpeakerOutputLoopbackEndpoint = speakerOutputLoopbackEndpoint,
                SpeakerCaptureEndpoint = speakerOutputLoopbackEndpoint
            };
            var json = JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, System.Text.Encoding.UTF8);
        }
        catch
        {
            // Preference persistence is best-effort and must not affect host startup.
        }
    }

    private static string BuildAudioPreferencesPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideDock",
            "HostApp",
            AudioPreferencesFileName);
    }

    private static AudioPreferences? TryReadAudioPreferences(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AudioPreferences>(File.ReadAllText(path, System.Text.Encoding.UTF8))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static AudioEndpointBinding BuildAudioEndpointBinding(string? endpointId, string? displayName)
    {
        return new AudioEndpointBinding
        {
            EndpointId = endpointId,
            DisplayName = displayName
        };
    }

    private static AudioEndpointBinding PreserveExistingBindingWhenEmpty(
        AudioEndpointBinding current,
        AudioEndpointBinding? existing)
    {
        return string.IsNullOrWhiteSpace(current.EndpointId) && !string.IsNullOrWhiteSpace(existing?.EndpointId)
            ? existing
            : current;
    }

    private string AudioToggleHint(object sender)
    {
        if (ReferenceEquals(sender, AudioDeviceSwitch))
        {
            return AudioDeviceSwitch.IsOn
                ? "音频设备已启用，等待 Android 设备连接。"
                : "音频设备已关闭，副屏仍在运行。";
        }

        if (ReferenceEquals(sender, MicrophoneSwitch))
        {
            return MicrophoneSwitch.IsOn
                ? "SideDock 麦克风已启用。"
                : "SideDock 麦克风已关闭。";
        }

        if (ReferenceEquals(sender, SpeakerSwitch))
        {
            return SpeakerSwitch.IsOn
                ? "SideDock 音响已启用。"
                : "SideDock 音响已关闭。";
        }

        return "音频状态已更新。";
    }

    private void UpdateAudioState(string? hint = null)
    {
        if (AudioDeviceSwitch is null
            || MicrophoneSwitch is null
            || SpeakerSwitch is null
            || AudioOverallStatusText is null
            || AudioCurrentDeviceText is null
            || MicrophoneStatusText is null
            || SpeakerStatusText is null
            || AudioHintText is null)
        {
            return;
        }

        var audioEnabled = AudioDeviceSwitch.IsOn;
        var microphoneIntent = MicrophoneSwitch.IsOn;
        var speakerIntent = SpeakerSwitch.IsOn;
        var baseStatus = _audioOverrideStatus ?? CurrentAudioBaseStatus();
        var microphoneEndpointBlocked = microphoneIntent && _microphoneRenderEndpointDiagnostics.BlocksAudio;
        var speakerEndpointBlocked = speakerIntent && _speakerCaptureEndpointDiagnostics.BlocksAudio;

        MicrophoneSwitch.IsEnabled = audioEnabled;
        SpeakerSwitch.IsEnabled = audioEnabled;
        AudioCurrentDeviceText.Text = CurrentAudioDeviceLabel();

        AudioCapabilityStatus overallStatus;
        AudioCapabilityStatus microphoneStatus;
        AudioCapabilityStatus speakerStatus;

        if (!audioEnabled)
        {
            overallStatus = AudioCapabilityStatus.Closed;
            microphoneStatus = AudioCapabilityStatus.Closed;
            speakerStatus = AudioCapabilityStatus.Closed;
        }
        else
        {
            microphoneStatus = microphoneIntent
                ? (_microphoneRuntimeStatus ?? baseStatus)
                : AudioCapabilityStatus.Closed;
            speakerStatus = speakerIntent
                ? (_speakerRuntimeStatus ?? baseStatus)
                : AudioCapabilityStatus.Closed;

            if (microphoneEndpointBlocked)
            {
                microphoneStatus = AudioCapabilityStatus.Error;
            }

            if (speakerEndpointBlocked)
            {
                speakerStatus = AudioCapabilityStatus.Error;
            }

            if (!microphoneIntent && !speakerIntent)
            {
                overallStatus = AudioCapabilityStatus.Closed;
            }
            else if (microphoneEndpointBlocked && speakerEndpointBlocked)
            {
                overallStatus = AudioCapabilityStatus.Error;
            }
            else if (_audioOverrideStatus.HasValue)
            {
                overallStatus = _audioOverrideStatus.Value;
            }
            else if (_speakerRuntimeStatus == AudioCapabilityStatus.Playing)
            {
                overallStatus = AudioCapabilityStatus.Playing;
            }
            else if (_microphoneRuntimeStatus == AudioCapabilityStatus.Capturing)
            {
                overallStatus = AudioCapabilityStatus.Capturing;
            }
            else if ((microphoneIntent && microphoneStatus == AudioCapabilityStatus.Available)
                || (speakerIntent && speakerStatus == AudioCapabilityStatus.Available))
            {
                overallStatus = microphoneIntent && speakerIntent
                    && (microphoneStatus != AudioCapabilityStatus.Available || speakerStatus != AudioCapabilityStatus.Available)
                        ? AudioCapabilityStatus.PartialAvailable
                        : AudioCapabilityStatus.Available;
            }
            else if (microphoneIntent && microphoneStatus != AudioCapabilityStatus.Closed)
            {
                overallStatus = microphoneStatus;
            }
            else if (speakerIntent && speakerStatus != AudioCapabilityStatus.Closed)
            {
                overallStatus = speakerStatus;
            }
            else
            {
                overallStatus = baseStatus;
            }
        }

        SetAudioStatusText(AudioOverallStatusText, AudioOverallText(overallStatus), overallStatus);
        SetAudioStatusText(MicrophoneStatusText, AudioDirectionText(AudioDirection.Microphone, microphoneStatus), microphoneStatus);
        SetAudioStatusText(SpeakerStatusText, AudioDirectionText(AudioDirection.Speaker, speakerStatus), speakerStatus);

        var audioHint = BuildAudioHint(audioEnabled, microphoneIntent, speakerIntent, overallStatus, hint);
        _lastAudioHint = audioHint;
        AudioHintText.Text = audioHint;
        UpdateOverviewAudioState(
            audioEnabled,
            microphoneIntent,
            speakerIntent,
            overallStatus,
            microphoneStatus,
            speakerStatus,
            audioHint);
        UpdateStaticDiagnosticsPage();
    }

    private void UpdateOverviewAudioState(
        bool audioEnabled,
        bool microphoneIntent,
        bool speakerIntent,
        AudioCapabilityStatus overallStatus,
        AudioCapabilityStatus microphoneStatus,
        AudioCapabilityStatus speakerStatus,
        string audioHint)
    {
        if (!StaticOverviewUi || OverviewAudioStatusText is null)
        {
            return;
        }

        _updatingOverviewAudioSwitch = true;
        try
        {
            OverviewAudioSwitch.IsOn = audioEnabled;
        }
        finally
        {
            _updatingOverviewAudioSwitch = false;
        }

        OverviewAudioSwitch.IsEnabled = !_loadingAudioPreferences;
        OverviewAudioSettingsButton.IsEnabled = true;
        UpdateOverviewAudioCapabilityOptions(audioEnabled);

        var (statusText, hintText, statusBrush) = BuildOverviewAudioStatusView(
            audioEnabled,
            microphoneIntent,
            speakerIntent,
            overallStatus,
            microphoneStatus,
            speakerStatus,
            audioHint);
        OverviewAudioStatusText.Text = statusText;
        OverviewAudioStatusText.Foreground = statusBrush;
        OverviewAudioHintText.Text = hintText;
        OverviewAudioHintText.Foreground = statusBrush == _dangerBrush ? _dangerBrush : _overviewNeutralBrush;
    }

    private void UpdateOverviewAudioCapabilityOptions(bool audioEnabled)
    {
        if (OverviewAudioModeOptions is null || OverviewAudioSampleRateOptions is null)
        {
            return;
        }

        var optionsOpacity = audioEnabled ? 1.0 : 0.7;
        OverviewAudioModeOptions.Opacity = optionsOpacity;
        OverviewAudioSampleRateOptions.Opacity = optionsOpacity;
        OverviewAudioSurroundModeOption.Opacity = 0.55;
        OverviewAudio96KSampleRateOption.Opacity = 0.55;
        OverviewAudioSurroundModeOption.IsHitTestVisible = false;
        OverviewAudio96KSampleRateOption.IsHitTestVisible = false;
    }

    private (string StatusText, string HintText, Brush StatusBrush) BuildOverviewAudioStatusView(
        bool audioEnabled,
        bool microphoneIntent,
        bool speakerIntent,
        AudioCapabilityStatus overallStatus,
        AudioCapabilityStatus microphoneStatus,
        AudioCapabilityStatus speakerStatus,
        string audioHint)
    {
        var hostRunning = _hostProcess is { HasExited: false };

        if (!audioEnabled || (!microphoneIntent && !speakerIntent))
        {
            return (
                "未启用",
                hostRunning
                    ? "音频桥接已关闭；当前偏好会用于下次启动主机。"
                    : "音频桥接已关闭，将在下次启动主机时保持关闭。",
                _secondaryBrush);
        }

        var endpointIssue = BuildAudioEndpointIssueHint(microphoneIntent, speakerIntent);
        if (!string.IsNullOrWhiteSpace(endpointIssue))
        {
            return ("音频设备暂不可用", endpointIssue, _warningBrush);
        }

        if (!hostRunning && overallStatus == AudioCapabilityStatus.WaitingDevice)
        {
            return ("等待 Android 设备", "音频桥接已启用，将在下次启动主机时生效。", _secondaryBrush);
        }

        if (microphoneStatus == AudioCapabilityStatus.AuthorizationRequired)
        {
            return ("需要 Android 麦克风权限", "请在 Android 设备上允许 SideDock 使用麦克风。", _dangerBrush);
        }

        if (speakerStatus == AudioCapabilityStatus.Playing)
        {
            return ("音响播放中", audioHint, _successBrush);
        }

        if (microphoneStatus == AudioCapabilityStatus.Capturing)
        {
            return ("麦克风采集中", audioHint, _successBrush);
        }

        return overallStatus switch
        {
            AudioCapabilityStatus.Closed => ("未启用", audioHint, _secondaryBrush),
            AudioCapabilityStatus.WaitingDevice => ("等待 Android 设备", audioHint, _warningBrush),
            AudioCapabilityStatus.Preparing => ("正在准备音频设备", audioHint, _overviewPrimaryBrush),
            AudioCapabilityStatus.Available => ("音频设备可用", audioHint, _successBrush),
            AudioCapabilityStatus.PartialAvailable => ("音频设备可用", audioHint, _successBrush),
            AudioCapabilityStatus.Capturing => ("麦克风采集中", audioHint, _successBrush),
            AudioCapabilityStatus.Playing => ("音响播放中", audioHint, _successBrush),
            AudioCapabilityStatus.AuthorizationRequired => ("需要 Android 麦克风权限", audioHint, _dangerBrush),
            AudioCapabilityStatus.Reconnecting => ("等待 Android 设备", audioHint, _warningBrush),
            AudioCapabilityStatus.Error => ("音频设备暂不可用", audioHint, _dangerBrush),
            AudioCapabilityStatus.Muted => ("音频设备已静音", audioHint, _warningBrush),
            _ => ("等待 Android 设备", audioHint, _secondaryBrush)
        };
    }

    private AudioCapabilityStatus CurrentAudioBaseStatus()
    {
        return _hostProcess is { HasExited: false }
            ? AudioCapabilityStatus.Preparing
            : AudioCapabilityStatus.WaitingDevice;
    }

    private string CurrentAudioDeviceLabel()
    {
        if (AdbDeviceCombo.SelectedItem is AdbDeviceChoice { Serial.Length: > 0 } choice)
        {
            return choice.DisplayName;
        }

        return _hostProcess is { HasExited: false }
            ? "自动选择 Android 设备"
            : "等待 Android 设备";
    }

    private string BuildAudioHint(
        bool audioEnabled,
        bool microphoneIntent,
        bool speakerIntent,
        AudioCapabilityStatus overallStatus,
        string? requestedHint)
    {
        if (!audioEnabled)
        {
            return "音频设备已关闭，副屏仍在运行。";
        }

        if (!microphoneIntent && !speakerIntent)
        {
            return "音频设备已关闭，副屏仍在运行。";
        }

        var endpointIssue = BuildAudioEndpointIssueHint(microphoneIntent, speakerIntent);
        if (!string.IsNullOrWhiteSpace(endpointIssue))
        {
            return endpointIssue;
        }

        if (!string.IsNullOrWhiteSpace(requestedHint))
        {
            return requestedHint;
        }

        if (!microphoneIntent)
        {
            return "当前只启用了音响，麦克风已关闭。";
        }

        if (!speakerIntent)
        {
            return "当前只启用了麦克风，音响已关闭。";
        }

        return overallStatus switch
        {
            AudioCapabilityStatus.Preparing => "正在准备音频设备。",
            AudioCapabilityStatus.Available => "音频设备可用。电脑声音会从所选 Windows 输出设备 loopback 捕获；通话软件麦克风请选择同一虚拟链路对应的 Output/录制端。",
            AudioCapabilityStatus.Capturing => "Android 麦克风正在采集中。",
            AudioCapabilityStatus.Playing => "SideDock 音响正在播放。",
            AudioCapabilityStatus.PartialAvailable => "部分音频能力可用。",
            AudioCapabilityStatus.AuthorizationRequired => "需要在 Android 端允许麦克风权限。",
            AudioCapabilityStatus.Reconnecting => "正在恢复音频连接。",
            AudioCapabilityStatus.Error => "音频设备暂不可用。",
            AudioCapabilityStatus.Muted => "SideDock 麦克风已静音。",
            _ => "等待 Android 设备连接。"
        };
    }

    private string? BuildAudioEndpointIssueHint(bool microphoneIntent, bool speakerIntent)
    {
        var microphoneIssue = microphoneIntent && _microphoneRenderEndpointDiagnostics.BlocksAudio
            ? _microphoneRenderEndpointDiagnostics.Summary
            : null;
        var speakerIssue = speakerIntent && _speakerCaptureEndpointDiagnostics.BlocksAudio
            ? _speakerCaptureEndpointDiagnostics.Summary
            : null;

        if (!string.IsNullOrWhiteSpace(microphoneIssue) && !string.IsNullOrWhiteSpace(speakerIssue))
        {
            return $"{microphoneIssue}；{speakerIssue}";
        }

        return microphoneIssue ?? speakerIssue;
    }

    private void SetAudioStatusText(TextBlock textBlock, string text, AudioCapabilityStatus status)
    {
        textBlock.Text = text;
        textBlock.Foreground = AudioBrush(status);
    }

    private Brush AudioBrush(AudioCapabilityStatus status)
    {
        return status switch
        {
            AudioCapabilityStatus.Available or AudioCapabilityStatus.PartialAvailable or AudioCapabilityStatus.Capturing or AudioCapabilityStatus.Playing => _successBrush,
            AudioCapabilityStatus.Muted => _warningBrush,
            AudioCapabilityStatus.AuthorizationRequired or AudioCapabilityStatus.Error => _dangerBrush,
            _ => _secondaryBrush
        };
    }

    private static string AudioOverallText(AudioCapabilityStatus status)
    {
        return status switch
        {
            AudioCapabilityStatus.Closed => "音频设备已关闭",
            AudioCapabilityStatus.WaitingDevice => "等待 Android 设备连接",
            AudioCapabilityStatus.Preparing => "正在准备音频设备",
            AudioCapabilityStatus.Available => "音频设备可用",
            AudioCapabilityStatus.Capturing => "SideDock 麦克风正在使用",
            AudioCapabilityStatus.Playing => "SideDock 音响正在播放",
            AudioCapabilityStatus.PartialAvailable => "部分音频能力可用",
            AudioCapabilityStatus.Muted => "音频设备已静音",
            AudioCapabilityStatus.AuthorizationRequired => "等待 Android 端麦克风授权",
            AudioCapabilityStatus.Reconnecting => "音频连接中断，正在重连",
            AudioCapabilityStatus.Error => "音频设备暂不可用",
            _ => "等待 Android 设备连接"
        };
    }

    private static string AudioDirectionText(AudioDirection direction, AudioCapabilityStatus status)
    {
        var isMicrophone = direction == AudioDirection.Microphone;
        return status switch
        {
            AudioCapabilityStatus.Closed => isMicrophone ? "麦克风已关闭" : "音响已关闭",
            AudioCapabilityStatus.WaitingDevice => "等待 Android 设备",
            AudioCapabilityStatus.Preparing => isMicrophone ? "正在准备麦克风" : "正在准备音响",
            AudioCapabilityStatus.Available => isMicrophone ? "SideDock 麦克风可选择" : "SideDock 音响可选择",
            AudioCapabilityStatus.Capturing => isMicrophone ? "SideDock 麦克风正在使用" : "SideDock 音响正在播放",
            AudioCapabilityStatus.Playing => isMicrophone ? "SideDock 麦克风正在使用" : "SideDock 音响正在播放",
            AudioCapabilityStatus.PartialAvailable => isMicrophone ? "SideDock 麦克风可选择" : "SideDock 音响可选择",
            AudioCapabilityStatus.Muted => isMicrophone ? "SideDock 麦克风已静音" : "SideDock 音响已静音",
            AudioCapabilityStatus.AuthorizationRequired => isMicrophone ? "需要在 Android 端允许麦克风权限" : "音响等待 Android 设备",
            AudioCapabilityStatus.Reconnecting => isMicrophone ? "麦克风正在重连" : "音响正在重连",
            AudioCapabilityStatus.Error => isMicrophone ? "SideDock 麦克风暂不可用" : "SideDock 音响暂不可用",
            AudioCapabilityStatus.NotImplemented => isMicrophone ? "SideDock 麦克风暂不可用" : "SideDock 音响暂不可用",
            _ => "等待 Android 设备"
        };
    }

    private async void ShowError(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    private static string BuildDriverInstallLogPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SideDock",
            "Logs");

        // Pre-create the directory from the (non-elevated) host so the elevated installer
        // only needs to write the file into it, and so this process reliably owns and can
        // read the report back even when elevation runs under a different admin account.
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            // The installer also creates this directory; ignore and let it try.
        }

        return Path.Combine(directory, $"driver-install-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
    }

    private static string? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, System.Text.Encoding.UTF8) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort: a leftover success log is harmless.
        }
    }

    private async void ShowErrorWithDetails(string title, string summary, string details)
    {
        var summaryText = new TextBlock
        {
            Text = summary,
            TextWrapping = TextWrapping.Wrap
        };

        // A read-only, selectable multiline TextBox: the 复制详情 button copies it in one
        // click, and the user can still select + Ctrl+C manually if the clipboard API fails.
        var detailBox = new TextBox
        {
            Text = details,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            IsSpellCheckEnabled = false,
            MinWidth = 560,
            MaxHeight = 480
        };
        ScrollViewer.SetVerticalScrollBarVisibility(detailBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(detailBox, ScrollBarVisibility.Auto);

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(summaryText);
        panel.Children.Add(detailBox);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "复制详情",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Resources["ContentDialogMaxWidth"] = 820.0;

        dialog.PrimaryButtonClick += (sender, args) =>
        {
            args.Cancel = true; // Keep the dialog open so the user can copy again / read on.
            try
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(details);
                Clipboard.SetContent(package);
                Clipboard.Flush();
                sender.PrimaryButtonText = "已复制 ✓";
            }
            catch
            {
                sender.PrimaryButtonText = "复制失败，请手动选择文本复制";
            }
        };

        await dialog.ShowAsync();
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconEx(
        string szFileName,
        int nIconIndex,
        IntPtr[]? phiconLarge,
        IntPtr[]? phiconSmall,
        uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr hInst,
        IntPtr name,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenu(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        int nReserved,
        IntPtr hWnd,
        IntPtr prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    private delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData);

    private enum AudioDirection
    {
        Microphone,
        Speaker
    }

    private enum AudioEndpointRole
    {
        SpeakerCapture,
        MicrophoneRender
    }

    private enum AudioCapabilityStatus
    {
        Closed,
        WaitingDevice,
        Preparing,
        Available,
        Capturing,
        Playing,
        PartialAvailable,
        Muted,
        AuthorizationRequired,
        Reconnecting,
        Error,
        NotImplemented
    }

    private enum AudioEndpointBindingHealth
    {
        Unknown,
        Unconfigured,
        Ready,
        Disabled,
        Missing,
        Unsupported,
        EnumerationFailed
    }

    private sealed record NetworkDiagnosticsSample(
        string Id,
        string Name,
        string Description,
        long LinkSpeedBps,
        double? SendBps,
        double? ReceiveBps);

    private sealed record VirtualCameraToolResult(int ExitCode, string Stdout, string Stderr);

    private sealed class VideoDiagnosticsState
    {
        private static readonly TimeSpan RecentStatsWindow = TimeSpan.FromSeconds(10);

        public bool HasVideoStats { get; set; }

        public bool HasEncoderStats { get; set; }

        public bool HasDroppedFrameStats { get; set; }

        public DateTimeOffset LastVideoStatsAt { get; set; }

        public DateTimeOffset LastEncoderStatsAt { get; set; }

        public long FramesDecoded { get; set; }

        public long FramesRendered { get; set; }

        public long PacketsReceived { get; set; }

        public long DroppedFrames { get; set; }

        public long DecodeErrors { get; set; }

        public long VideoReconnects { get; set; }

        public double DecodeFps { get; set; }

        public double RenderFps { get; set; }

        public double NewFrameFps { get; set; }

        public double RepeatFrameFps { get; set; }

        public double LocalPipelineLatencyMs { get; set; }

        public double RoughLatencyMs { get; set; }

        public double LatencyErrorBoundMs { get; set; }

        public long FramesGenerated { get; set; }

        public long FramesEncoded { get; set; }

        public long FramesSent { get; set; }

        public long FramesDropped { get; set; }

        public long LateFrames { get; set; }

        public double StreamFps { get; set; }

        public double LocalLatencyP95Ms { get; set; }

        public double OutputKbps { get; set; }

        public bool HasRecentVideoStats =>
            HasVideoStats && DateTimeOffset.Now - LastVideoStatsAt <= RecentStatsWindow;

        public bool TryGetDroppedFrameRate(out double rate)
        {
            if (HasDroppedFrameStats)
            {
                var totalClientFrames = FramesDecoded + DroppedFrames;
                if (totalClientFrames <= 0)
                {
                    totalClientFrames = PacketsReceived + DroppedFrames;
                }

                if (totalClientFrames > 0)
                {
                    rate = DroppedFrames * 100.0 / totalClientFrames;
                    return true;
                }
            }

            if (HasEncoderStats)
            {
                var totalHostFrames = FramesSent + FramesDropped;
                if (totalHostFrames > 0)
                {
                    rate = FramesDropped * 100.0 / totalHostFrames;
                    return true;
                }
            }

            rate = 0;
            return false;
        }

        public void Reset()
        {
            HasVideoStats = false;
            HasEncoderStats = false;
            HasDroppedFrameStats = false;
            LastVideoStatsAt = default;
            LastEncoderStatsAt = default;
            FramesDecoded = 0;
            FramesRendered = 0;
            PacketsReceived = 0;
            DroppedFrames = 0;
            DecodeErrors = 0;
            VideoReconnects = 0;
            DecodeFps = 0;
            RenderFps = 0;
            NewFrameFps = 0;
            RepeatFrameFps = 0;
            LocalPipelineLatencyMs = 0;
            RoughLatencyMs = 0;
            LatencyErrorBoundMs = 0;
            FramesGenerated = 0;
            FramesEncoded = 0;
            FramesSent = 0;
            FramesDropped = 0;
            LateFrames = 0;
            StreamFps = 0;
            LocalLatencyP95Ms = 0;
            OutputKbps = 0;
        }
    }

    private sealed class VirtualCameraDiagnosticsState
    {
        public bool Registered { get; set; }

        public bool Running { get; set; }

        public string RegisteredScopeSummary { get; set; } = "";

        public int DeviceCount { get; set; }

        public DateTimeOffset? LastServedAt { get; set; }

        public long SourceFrameSequence { get; set; }

        public string FrameKind { get; set; } = "";

        public string LastToolState { get; set; } = "";

        public string LastError { get; set; } = "";

        public string RegistrationText => Registered ? "已注册" : "未注册";

        public string RunningText => Running ? "运行中" : "已停止";
    }

    private sealed class CameraDiagnosticsState
    {
        public string ServerState { get; set; } = "idle";

        public string ClientState { get; set; } = "unknown";

        public string PermissionGranted { get; set; } = "";

        public int Port { get; set; } = DefaultCameraPort;

        public int Width { get; set; } = 1280;

        public int Height { get; set; } = 720;

        public int Fps { get; set; } = 30;

        public string Codec { get; set; } = "video/avc";

        public string Facing { get; set; } = "back";

        public long Packets { get; set; }

        public long Frames { get; set; }

        public long Bytes { get; set; }

        public long KeyFrames { get; set; }

        public long CodecConfigPackets { get; set; }

        public long DecodedFrames { get; set; }

        public long DecodeErrors { get; set; }

        public long PreviewFrameSequence { get; set; }

        public double DecodeLagMs { get; set; }

        public double ApproxFps { get; set; }

        public double ApproxKbps { get; set; }

        public string LastFrameAt { get; set; } = "";

        public string LastDecodedFrameAt { get; set; } = "";

        public string LastError { get; set; } = "";

        public long ClientPackets { get; set; }

        public long ClientBytes { get; set; }

        public long ClientKeyFrames { get; set; }

        public long ClientCodecConfigPackets { get; set; }

        public string PermissionText => string.IsNullOrWhiteSpace(PermissionGranted) ? "unknown" : PermissionGranted;

        public void Reset()
        {
            ServerState = "idle";
            ClientState = "unknown";
            PermissionGranted = "";
            Port = DefaultCameraPort;
            Width = 1280;
            Height = 720;
            Fps = 30;
            Codec = "video/avc";
            Facing = "back";
            Packets = 0;
            Frames = 0;
            Bytes = 0;
            KeyFrames = 0;
            CodecConfigPackets = 0;
            DecodedFrames = 0;
            DecodeErrors = 0;
            PreviewFrameSequence = 0;
            DecodeLagMs = 0;
            ApproxFps = 0;
            ApproxKbps = 0;
            LastFrameAt = "";
            LastDecodedFrameAt = "";
            LastError = "";
            ClientPackets = 0;
            ClientBytes = 0;
            ClientKeyFrames = 0;
            ClientCodecConfigPackets = 0;
        }
    }

    private sealed class OverviewPreviewFrameReader : IDisposable
    {
        private const string MapName = @"Local\SideDockOverviewPreviewFrame";
        private const int HeaderSize = 128;
        private const int Magic = 0x50464453; // SDFP
        private const int Version = 1;
        private const int FormatBgra32 = 1;
        private const int MaxFrameBytes = 3840 * 2160 * 4;

        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _view;

        private OverviewPreviewFrameReader(MemoryMappedFile mapping, MemoryMappedViewAccessor view)
        {
            _mapping = mapping;
            _view = view;
        }

        public static OverviewPreviewFrameReader? TryOpen()
        {
            try
            {
                var mapping = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                return new OverviewPreviewFrameReader(mapping, view);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        public OverviewPreviewFrame? TryReadLatest(long lastSeenSequence)
        {
            var sequenceBefore = _view.ReadInt64(32);
            if (sequenceBefore <= 0 || (sequenceBefore & 1) != 0)
            {
                return null;
            }

            var frameSequence = sequenceBefore / 2;
            if (frameSequence <= lastSeenSequence)
            {
                return null;
            }

            var magic = _view.ReadInt32(0);
            var version = _view.ReadInt32(4);
            var headerSize = _view.ReadInt32(8);
            var width = _view.ReadInt32(12);
            var height = _view.ReadInt32(16);
            var stride = _view.ReadInt32(20);
            var format = _view.ReadInt32(24);
            var frameBytes = _view.ReadInt32(28);
            var writtenAtUnixMs = _view.ReadInt64(48);
            if (magic != Magic
                || version != Version
                || headerSize != HeaderSize
                || format != FormatBgra32
                || width <= 0
                || height <= 0
                || stride < width * 4
                || frameBytes <= 0
                || frameBytes > MaxFrameBytes
                || frameBytes != stride * height)
            {
                throw new InvalidDataException("预览帧缓存格式不匹配。");
            }

            var raw = new byte[frameBytes];
            _view.ReadArray(HeaderSize, raw, 0, raw.Length);

            var sequenceAfter = _view.ReadInt64(32);
            if (sequenceAfter != sequenceBefore || (sequenceAfter & 1) != 0)
            {
                return null;
            }

            var bgra = stride == width * 4
                ? raw
                : CompactRows(raw, width, height, stride);
            return new OverviewPreviewFrame(frameSequence, width, height, writtenAtUnixMs, bgra);
        }

        public void Dispose()
        {
            _view.Dispose();
            _mapping.Dispose();
        }

        private static byte[] CompactRows(byte[] raw, int width, int height, int stride)
        {
            var rowBytes = width * 4;
            var compact = new byte[rowBytes * height];
            for (var y = 0; y < height; y++)
            {
                Buffer.BlockCopy(raw, y * stride, compact, y * rowBytes, rowBytes);
            }

            return compact;
        }
    }

    private sealed record OverviewPreviewFrame(long Sequence, int Width, int Height, long WrittenAtUnixMs, byte[] Bgra);

    private sealed class CameraPreviewFrameReader : IDisposable
    {
        private const string MapName = @"Local\SideDockCameraPreviewFrame";
        private const int HeaderSize = 128;
        private const int Magic = 0x46434453; // SDCF
        private const int Version = 1;
        private const int FormatBgra32 = 1;
        private const int MaxFrameBytes = 2560 * 1440 * 4;

        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _view;

        private CameraPreviewFrameReader(MemoryMappedFile mapping, MemoryMappedViewAccessor view)
        {
            _mapping = mapping;
            _view = view;
        }

        public static CameraPreviewFrameReader? TryOpen()
        {
            try
            {
                var mapping = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                return new CameraPreviewFrameReader(mapping, view);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        public CameraPreviewFrame? TryReadLatest(long lastSeenSequence)
        {
            var sequenceBefore = _view.ReadInt64(32);
            if (sequenceBefore <= 0 || (sequenceBefore & 1) != 0)
            {
                return null;
            }

            var frameSequence = sequenceBefore / 2;
            if (frameSequence <= lastSeenSequence)
            {
                return null;
            }

            var magic = _view.ReadInt32(0);
            var version = _view.ReadInt32(4);
            var headerSize = _view.ReadInt32(8);
            var width = _view.ReadInt32(12);
            var height = _view.ReadInt32(16);
            var stride = _view.ReadInt32(20);
            var format = _view.ReadInt32(24);
            var frameBytes = _view.ReadInt32(28);
            var writtenAtUnixMs = _view.ReadInt64(48);
            if (magic != Magic
                || version != Version
                || headerSize != HeaderSize
                || format != FormatBgra32
                || width <= 0
                || height <= 0
                || stride < width * 4
                || frameBytes <= 0
                || frameBytes > MaxFrameBytes
                || frameBytes != stride * height)
            {
                return null;
            }

            var raw = new byte[frameBytes];
            _view.ReadArray(HeaderSize, raw, 0, raw.Length);

            var sequenceAfter = _view.ReadInt64(32);
            if (sequenceAfter != sequenceBefore || (sequenceAfter & 1) != 0)
            {
                return null;
            }

            var bgra = stride == width * 4
                ? raw
                : CompactRows(raw, width, height, stride);
            return new CameraPreviewFrame(frameSequence, width, height, writtenAtUnixMs, bgra);
        }

        public void Dispose()
        {
            _view.Dispose();
            _mapping.Dispose();
        }

        private static byte[] CompactRows(byte[] raw, int width, int height, int stride)
        {
            var rowBytes = width * 4;
            var compact = new byte[rowBytes * height];
            for (var y = 0; y < height; y++)
            {
                Buffer.BlockCopy(raw, y * stride, compact, y * rowBytes, rowBytes);
            }

            return compact;
        }
    }

    private sealed record CameraPreviewFrame(long Sequence, int Width, int Height, long WrittenAtUnixMs, byte[] Bgra);

    private sealed class AudioPreferences
    {
        public bool AudioDeviceEnabled { get; set; } = true;

        public bool MicrophoneEnabled { get; set; } = true;

        public bool SpeakerEnabled { get; set; } = true;

        public AudioEndpointBinding? MicrophoneRenderEndpoint { get; set; }

        public AudioEndpointBinding? SpeakerOutputLoopbackEndpoint { get; set; }

        public AudioEndpointBinding? SpeakerCaptureEndpoint { get; set; }
    }

    private sealed class AudioEndpointBinding
    {
        public string? EndpointId { get; set; }

        public string? DisplayName { get; set; }
    }

    private sealed record AudioEndpointCandidate(
        string EndpointId,
        string DisplayName,
        bool IsEnabled)
    {
        public static AudioEndpointCandidate FromDevice(DeviceInformation device)
        {
            return new AudioEndpointCandidate(
                device.Id,
                string.IsNullOrWhiteSpace(device.Name) ? "(未命名音频端点)" : device.Name,
                device.IsEnabled);
        }
    }

    private sealed record AudioEndpointChoice(
        AudioEndpointRole Role,
        string EndpointId,
        string DisplayName,
        bool IsEnabled,
        bool IsPresent)
    {
        public bool IsBound => !string.IsNullOrWhiteSpace(EndpointId);

        public string DisplayLabel
        {
            get
            {
                if (!IsBound)
                {
                    return Role == AudioEndpointRole.SpeakerCapture
                        ? "未绑定电脑声音 loopback 输出端点"
                        : "未绑定 Android 麦克风写入端点";
                }

                if (!IsPresent)
                {
                    return $"{DisplayName}（当前不可用）";
                }

                return IsEnabled ? DisplayName : $"{DisplayName}（已禁用）";
            }
        }

        public static AudioEndpointChoice Unbound(AudioEndpointRole role)
        {
            return new AudioEndpointChoice(role, string.Empty, string.Empty, IsEnabled: true, IsPresent: true);
        }

        public static AudioEndpointChoice Missing(AudioEndpointRole role, string endpointId, string? displayName)
        {
            return new AudioEndpointChoice(
                role,
                endpointId,
                string.IsNullOrWhiteSpace(displayName) ? "已绑定端点" : displayName,
                IsEnabled: false,
                IsPresent: false);
        }

        public static AudioEndpointChoice FromDevice(AudioEndpointRole role, DeviceInformation device)
        {
            return new AudioEndpointChoice(
                role,
                device.Id,
                string.IsNullOrWhiteSpace(device.Name) ? "(未命名音频端点)" : device.Name,
                device.IsEnabled,
                IsPresent: true);
        }

        public static AudioEndpointChoice? FromMicrophoneDevice(
            DeviceInformation device,
            IReadOnlyList<AudioEndpointCandidate> renderEndpoints)
        {
            var displayName = string.IsNullOrWhiteSpace(device.Name) ? "(未命名麦克风端点)" : device.Name;
            var renderEndpoint = FindMicrophoneRenderEndpoint(displayName, renderEndpoints);
            if (renderEndpoint is null)
            {
                return null;
            }

            return new AudioEndpointChoice(
                AudioEndpointRole.MicrophoneRender,
                renderEndpoint.EndpointId,
                displayName,
                device.IsEnabled && renderEndpoint.IsEnabled,
                IsPresent: true);
        }

        private static AudioEndpointCandidate? FindMicrophoneRenderEndpoint(
            string captureName,
            IReadOnlyList<AudioEndpointCandidate> renderEndpoints)
        {
            if (TryFindVoicemeeterRenderEndpoint(captureName, renderEndpoints, out var voicemeeterEndpoint))
            {
                return voicemeeterEndpoint;
            }

            return TryFindKnownVirtualRenderEndpoint(captureName, renderEndpoints);
        }

        private static bool TryFindVoicemeeterRenderEndpoint(
            string captureName,
            IReadOnlyList<AudioEndpointCandidate> renderEndpoints,
            out AudioEndpointCandidate? renderEndpoint)
        {
            renderEndpoint = null;
            if (!captureName.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (TryReadVoicemeeterOutNumber(captureName, "B", out var busNumber))
            {
                renderEndpoint = busNumber switch
                {
                    1 => FindVoicemeeterRenderEndpoint(renderEndpoints, "Voicemeeter Input", "AUX", "VAIO3", "In "),
                    2 => FindVoicemeeterRenderEndpoint(renderEndpoints, "Voicemeeter AUX Input"),
                    3 => FindVoicemeeterRenderEndpoint(renderEndpoints, "Voicemeeter VAIO3 Input"),
                    _ => null
                };
                return renderEndpoint is not null;
            }

            return false;
        }

        private static bool TryReadVoicemeeterOutNumber(string captureName, string busPrefix, out int number)
        {
            number = 0;
            var marker = $"Voicemeeter Out {busPrefix}";
            var markerIndex = captureName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return false;
            }

            var numberStart = markerIndex + marker.Length;
            if (numberStart >= captureName.Length || !char.IsDigit(captureName[numberStart]))
            {
                return false;
            }

            var numberEnd = numberStart;
            while (numberEnd < captureName.Length && char.IsDigit(captureName[numberEnd]))
            {
                numberEnd++;
            }

            return int.TryParse(captureName[numberStart..numberEnd], NumberStyles.None, CultureInfo.InvariantCulture, out number);
        }

        private static AudioEndpointCandidate? FindVoicemeeterRenderEndpoint(
            IReadOnlyList<AudioEndpointCandidate> renderEndpoints,
            string requiredPrefix,
            params string[] excludedParts)
        {
            return renderEndpoints.FirstOrDefault(endpoint =>
                endpoint.DisplayName.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase)
                && endpoint.DisplayName.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase)
                && !excludedParts.Any(part => endpoint.DisplayName.Contains(part, StringComparison.OrdinalIgnoreCase)));
        }

        private static AudioEndpointCandidate? TryFindKnownVirtualRenderEndpoint(
            string captureName,
            IReadOnlyList<AudioEndpointCandidate> renderEndpoints)
        {
            if (captureName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)
                && captureName.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase))
            {
                return renderEndpoints.FirstOrDefault(endpoint =>
                    endpoint.DisplayName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)
                    && endpoint.DisplayName.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase));
            }

            if (captureName.Contains("ToDesk Virtual Audio", StringComparison.OrdinalIgnoreCase))
            {
                return renderEndpoints.FirstOrDefault(endpoint =>
                    endpoint.DisplayName.Contains("ToDesk Virtual Audio", StringComparison.OrdinalIgnoreCase)
                    && endpoint.DisplayName.Contains("扬声器", StringComparison.OrdinalIgnoreCase));
            }

            if (captureName.Contains("Steam Streaming Speakers", StringComparison.OrdinalIgnoreCase))
            {
                return renderEndpoints.FirstOrDefault(endpoint =>
                    endpoint.DisplayName.Contains("Steam Streaming Speakers", StringComparison.OrdinalIgnoreCase)
                    && endpoint.DisplayName.Contains("扬声器", StringComparison.OrdinalIgnoreCase));
            }

            if (captureName.Contains("AudioRelay", StringComparison.OrdinalIgnoreCase))
            {
                return renderEndpoints.FirstOrDefault(endpoint =>
                    endpoint.DisplayName.Contains("AudioRelay", StringComparison.OrdinalIgnoreCase)
                    && endpoint.DisplayName.Contains("Virtual Speakers", StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }
    }

    private sealed record AudioEndpointDiagnostics(
        AudioEndpointBindingHealth Health,
        string Summary,
        string? EndpointId,
        string? DisplayName,
        int AvailableEndpointCount)
    {
        public bool BlocksAudio => Health is
            AudioEndpointBindingHealth.Unconfigured
            or AudioEndpointBindingHealth.Disabled
            or AudioEndpointBindingHealth.Missing
            or AudioEndpointBindingHealth.Unsupported
            or AudioEndpointBindingHealth.EnumerationFailed;

        public static AudioEndpointDiagnostics Unknown(AudioEndpointRole role)
        {
            return new AudioEndpointDiagnostics(
                AudioEndpointBindingHealth.Unknown,
                role == AudioEndpointRole.SpeakerCapture
                    ? "正在枚举电脑声音 loopback 输出端点..."
                    : "正在枚举 Android 麦克风写入端点...",
                null,
                null,
                0);
        }

        public static AudioEndpointDiagnostics Unsupported(AudioEndpointRole role)
        {
            return new AudioEndpointDiagnostics(
                AudioEndpointBindingHealth.Unsupported,
                role == AudioEndpointRole.SpeakerCapture
                    ? "当前系统不支持枚举 Windows 输出端点。"
                    : "当前系统不支持枚举 Windows 播放端点。",
                null,
                null,
                0);
        }

        public static AudioEndpointDiagnostics EnumerationFailed(AudioEndpointRole role, string message)
        {
            return new AudioEndpointDiagnostics(
                AudioEndpointBindingHealth.EnumerationFailed,
                role == AudioEndpointRole.SpeakerCapture
                    ? $"电脑声音 loopback 输出端点枚举失败：{message}"
                    : $"Android 麦克风写入端点枚举失败：{message}",
                null,
                null,
                0);
        }

        public static AudioEndpointDiagnostics FromSelection(
            AudioEndpointRole role,
            AudioEndpointChoice? selectedChoice,
            int availableEndpointCount)
        {
            var roleName = role == AudioEndpointRole.SpeakerCapture ? "电脑声音 loopback 输出" : "Android 麦克风写入";
            if (selectedChoice is null || !selectedChoice.IsBound)
            {
                return new AudioEndpointDiagnostics(
                    AudioEndpointBindingHealth.Unconfigured,
                    $"{roleName}端点未绑定，请选择已安装虚拟线缆对应端点。",
                    null,
                    null,
                    availableEndpointCount);
            }

            if (!selectedChoice.IsPresent)
            {
                return new AudioEndpointDiagnostics(
                    AudioEndpointBindingHealth.Missing,
                    $"{roleName}端点丢失，请重新安装虚拟线缆或重新绑定。",
                    selectedChoice.EndpointId,
                    selectedChoice.DisplayName,
                    availableEndpointCount);
            }

            if (!selectedChoice.IsEnabled)
            {
                return new AudioEndpointDiagnostics(
                    AudioEndpointBindingHealth.Disabled,
                    $"{roleName}端点已禁用，请在 Windows 声音设置中启用后刷新。",
                    selectedChoice.EndpointId,
                    selectedChoice.DisplayName,
                    availableEndpointCount);
            }

            return new AudioEndpointDiagnostics(
                AudioEndpointBindingHealth.Ready,
                $"{roleName}端点已绑定：{selectedChoice.DisplayName}",
                selectedChoice.EndpointId,
                selectedChoice.DisplayName,
                availableEndpointCount);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
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
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private sealed class HostProcessLog(
        string hostPath,
        string arguments,
        string workingDirectory,
        string? adbPath,
        string? adbSerial)
    {
        private const int MaxCharacters = 128 * 1024;
        private readonly object _gate = new();
        private readonly StringBuilder _buffer = new();
        private bool _truncated;

        public string HostPath { get; } = hostPath;
        public string Arguments { get; } = arguments;
        public string WorkingDirectory { get; } = workingDirectory;
        public string? AdbPath { get; } = adbPath;
        public string? AdbSerial { get; } = adbSerial;

        public void Append(string stream, string? line)
        {
            if (line is null)
            {
                return;
            }

            var entry = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {stream}: {line}{Environment.NewLine}";
            lock (_gate)
            {
                if (_buffer.Length + entry.Length > MaxCharacters)
                {
                    var overflow = _buffer.Length + entry.Length - MaxCharacters;
                    _buffer.Remove(0, Math.Min(overflow, _buffer.Length));
                    _truncated = true;
                }

                _buffer.Append(entry);
            }
        }

        public string Snapshot()
        {
            lock (_gate)
            {
                if (_buffer.Length == 0)
                {
                    return "(没有捕获到 stdout/stderr 输出)" + Environment.NewLine;
                }

                var prefix = _truncated
                    ? $"(日志较长，已保留最后 {MaxCharacters / 1024} KB){Environment.NewLine}"
                    : string.Empty;
                return prefix + _buffer;
            }
        }
    }

    private sealed record AdbReversePreflight(bool Success, string Summary, string Details, string? Serial);

    private sealed record AdbCommandResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

    private sealed record AdbDeviceChoice(string? Serial, string DisplayName, string State, string RawLine);

    private sealed record AdbDeviceRow(string Serial, string State, string RawLine);

    private sealed record OverviewConnectionDeviceItem(
        string Serial,
        string Name,
        string StatusText,
        Brush StatusBrush,
        Brush StatusDotBrush,
        string TransportText,
        string LastCheckedText);

    private sealed record DeviceToolDiagnostics(int? ExitCode, string Output, bool TimedOut);
}
