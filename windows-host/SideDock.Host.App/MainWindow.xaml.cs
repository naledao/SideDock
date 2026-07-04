using System.Diagnostics;
using System.IO.Compression;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using WinRT.Interop;

namespace SideDock.Host.App;

public sealed partial class MainWindow : Window
{
    private const string HostExe = "SideDock.Host.exe";
    private const string DeviceToolExe = "SideDock.Idd.DeviceTool.exe";
    private const string DriverInstallerExe = "SideDock.Driver.Installer.exe";
    private const int DefaultAudioPort = 27185;
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
    private const string AudioPreferencesFileName = "audio-preferences.json";
    private static readonly string DeviceToolProcessName = Path.GetFileNameWithoutExtension(DeviceToolExe);
    private static readonly UIntPtr WindowSubclassId = new(1);

    private readonly DispatcherTimer _displayStatusTimer = new();
    private readonly object _audioLogGate = new();
    private readonly Queue<string> _recentAudioLogLines = new();
    private readonly Brush _successBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 18, 132, 86));
    private readonly Brush _dangerBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 43, 28));
    private readonly Brush _warningBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 157, 93, 0));
    private readonly Brush _secondaryBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 96, 96, 96));
    private readonly IntPtr _windowHandle;

    private Process? _hostProcess;
    private Process? _deviceToolProcess;
    private SubclassProc? _subclassProc;
    private IntPtr _trayIconHandle;
    private string? _payloadRoot;
    private string? _hostPath;
    private string? _deviceToolPath;
    private string? _driverInstallerPath;
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
    private bool _loadingAudioEndpointChoices;
    private string? _boundMicrophoneRenderEndpointId;
    private string? _boundMicrophoneRenderEndpointName;
    private string? _boundSpeakerCaptureEndpointId;
    private string? _boundSpeakerCaptureEndpointName;
    private AudioEndpointDiagnostics _microphoneRenderEndpointDiagnostics = AudioEndpointDiagnostics.Unknown(AudioEndpointRole.MicrophoneRender);
    private AudioEndpointDiagnostics _speakerCaptureEndpointDiagnostics = AudioEndpointDiagnostics.Unknown(AudioEndpointRole.SpeakerCapture);

    public MainWindow()
    {
        InitializeComponent();

        _windowHandle = WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(null);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1080, 760));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        RegisterCardWheelScrolling();
        InitializeTrayIcon();
        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) =>
        {
            DisposeTrayIcon();
            StopHost();
        };

        InitializeAdbDeviceCombo();
        InitializeAudioEndpointCombos();
        LoadAudioPreferences();
        SetRunningState(false);
        RefreshVirtualDisplayState();
        UpdateAudioState();

        _displayStatusTimer.Interval = TimeSpan.FromSeconds(2);
        _displayStatusTimer.Tick += (_, _) => RefreshVirtualDisplayState();
        _displayStatusTimer.Start();

        _ = RefreshAdbDevicesAsync(showErrors: false);
        _ = RefreshAudioEndpointsAsync(showHint: false);
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

        return DefSubclassProc(hWnd, message, wParam, lParam);
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

    private void StopHostButton_Click(object sender, RoutedEventArgs e)
    {
        StopHost();
    }

    private async void RefreshAdbDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAdbDevicesAsync(showErrors: true);
    }

    private void AdbDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAudioState();
    }

    private void AudioSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingAudioPreferences)
        {
            return;
        }

        _audioOverrideStatus = null;
        var hint = AudioToggleHint(sender);
        SaveAudioPreferences();
        UpdateAudioState(hint);
    }

    private async void RefreshAudioEndpointsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAudioEndpointsAsync(showHint: true);
    }

    private void AudioEndpointCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAudioEndpointChoices)
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

    private void StartDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        StartVirtualDisplay(failureAction: "启动虚拟显示器失败");
    }

    private async void InstallDriverButton_Click(object sender, RoutedEventArgs e)
    {
        await InstallDriverAsync();
    }

    private void StopDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        StopVirtualDisplay();
    }

    private async Task StartHostAsync()
    {
        if (_hostProcess is { HasExited: false })
        {
            return;
        }

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
            OverallStatusText.Text = "启动中";
            OverallStatusText.Foreground = _secondaryBrush;
            SetAdbStatus("正在检查 ADB reverse...", _secondaryBrush);

            adbPath = ResolveAdbPath(AdbPathBox.Text.Trim());
            var explicitAdbSerial = SelectedAdbSerial();
            await RefreshAdbDevicesAsync(showErrors: false, resolvedAdbPath: adbPath);
            var selectedAdbSerial = explicitAdbSerial ?? SelectedAdbSerial();
            var reversePorts = GetConfiguredReversePorts();
            var adbPreflight = await ConfigureAdbReverseBeforeHostStartAsync(adbPath, reversePorts, selectedAdbSerial);
            if (!adbPreflight.Success)
            {
                SetRunningState(false);
                SetAdbStatus(adbPreflight.Summary, _dangerBrush);
                ShowErrorWithDetails(
                    "无法配置 ADB reverse",
                    adbPreflight.Summary,
                    adbPreflight.Details);
                return;
            }

            adbSerial = adbPreflight.Serial;
            SetAdbStatus(adbPreflight.Summary, _successBrush);

            if (ShouldManageVirtualDisplayWithHost())
            {
                var displayWasRunning = IsVirtualDisplayToolRunning();
                if (!StartVirtualDisplay(failureAction: "启动主机时无法启动虚拟显示器"))
                {
                    SetRunningState(false);
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
            _audioOverrideStatus = AudioCapabilityStatus.Error;
            UpdateAudioState("音频设备暂不可用，主机未启动。");
        }
    }

    private string BuildArguments()
    {
        var args = new List<string>
        {
            "--video-source", Selected(VideoSourceCombo),
            "--resolution", Selected(ResolutionCombo),
            "--refresh-rate", Selected(RefreshRateCombo),
            "--control-port", Port(ControlPortBox, "control"),
            "--video-port", Port(VideoPortBox, "video"),
            "--audio-port", DefaultAudioPort.ToString(CultureInfo.InvariantCulture),
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
            args.Add("--audio-speaker-capture-endpoint-id");
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
        var controlPort = PortNumber(ControlPortBox, "control");
        var videoPort = PortNumber(VideoPortBox, "video");
        var ports = new List<int> { controlPort, videoPort };
        if (AudioDeviceSwitch.IsOn && (MicrophoneSwitch.IsOn || SpeakerSwitch.IsOn))
        {
            ports.Add(DefaultAudioPort);
        }

        return ports.Distinct().ToArray();
    }

    private void InitializeAdbDeviceCombo()
    {
        AdbDeviceCombo.DisplayMemberPath = nameof(AdbDeviceChoice.DisplayName);
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

            var captureOperation = DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());
            var renderOperation = DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
            var speakerCaptureEndpoints = ToAudioEndpointChoices(AudioEndpointRole.SpeakerCapture, await captureOperation);
            var microphoneRenderEndpoints = ToAudioEndpointChoices(AudioEndpointRole.MicrophoneRender, await renderOperation);

            _loadingAudioEndpointChoices = true;
            try
            {
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
            }
            finally
            {
                _loadingAudioEndpointChoices = false;
            }

            SetAudioEndpointStatusTexts();
            UpdateAudioState(showHint ? "音频端点列表已刷新。" : null);
        }
        catch (Exception ex)
        {
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
            : "电脑声音捕获端点绑定已更新。");
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
        RefreshAdbDevicesButton.IsEnabled = false;

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
            SetAdbDeviceChoices(rows, selectedSerial);
            var authorizedCount = rows.Count(row => row.State.Equals("device", StringComparison.OrdinalIgnoreCase));
            if (authorizedCount == 0)
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
            RefreshAdbDevicesButton.IsEnabled = StartHostButton.IsEnabled && _hostProcess is not { HasExited: false };
        }
    }

    private void SetAdbDeviceChoices(IReadOnlyList<AdbDeviceRow> rows, string? selectedSerial)
    {
        var choices = new List<AdbDeviceChoice>
        {
            new(null, "自动选择（仅一台设备时）", string.Empty, string.Empty)
        };

        choices.AddRange(rows.Select(row => new AdbDeviceChoice(
            row.Serial,
            FormatAdbDeviceDisplayName(row),
            row.State,
            row.RawLine)));

        AdbDeviceCombo.ItemsSource = choices;

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

        AdbDeviceCombo.SelectedItem = selectedChoice;
        UpdateAudioState();
    }

    private string? SelectedAdbSerial()
    {
        return AdbDeviceCombo.SelectedItem is AdbDeviceChoice { Serial.Length: > 0 } choice
            ? choice.Serial
            : null;
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
        return (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
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
            return true;
        }
        catch (Exception ex)
        {
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

    private async Task InstallDriverAsync()
    {
        InstallDriverButton.IsEnabled = false;
        DriverInstallStatusText.Text = "正在启动驱动安装器，请在管理员权限弹窗中选择“是”。";

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
                DriverInstallStatusText.Text = "驱动安装流程已完成。若显示器没有立即出现，请点“启动显示器”。";
                TryDeleteFile(reportPath);
            }
            else
            {
                DriverInstallStatusText.Text = $"驱动安装未完成（退出码 {process.ExitCode}）。点开详情可一键复制。";
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
            DriverInstallStatusText.Text = "驱动安装已取消（未授予管理员权限）。";
            ShowError("驱动安装已取消", "你在管理员权限弹窗中选择了“否”，安装未开始。请重新点击“安装/修复驱动”，并在弹窗中选择“是”。");
        }
        catch (Exception ex)
        {
            DriverInstallStatusText.Text = "驱动安装未完成。";
            var report = TryReadFile(reportPath);
            var details = !string.IsNullOrWhiteSpace(report)
                ? $"日志文件: {reportPath}{Environment.NewLine}{Environment.NewLine}{report}"
                : ex.ToString();
            ShowErrorWithDetails("无法安装驱动", $"启动或执行驱动安装器时出错：{ex.Message}", details);
        }
        finally
        {
            InstallDriverButton.IsEnabled = true;
            RefreshVirtualDisplayState();
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

    private void RefreshVirtualDisplayState()
    {
        var running = IsVirtualDisplayToolRunning();
        StartDisplayButton.IsEnabled = !running;
        StopDisplayButton.IsEnabled = running;
        DisplayStatusText.Text = running ? "显示器已启动" : "显示器未启动";
        DisplayStatusText.Foreground = running ? _successBrush : _dangerBrush;
        DisplayStatusSubtext.Text = running
            ? "SideDock 虚拟显示器工具正在运行。"
            : "虚拟显示器工具未运行。";
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
        StartHostButton.IsEnabled = !running;
        StopHostButton.IsEnabled = running;
        AdbDeviceCombo.IsEnabled = !running;
        RefreshAdbDevicesButton.IsEnabled = !running;
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

        UpdateAudioState(running ? "正在准备音频设备。" : "等待 Android 设备连接。");
    }

    private void SetAdbStatus(string text, Brush brush)
    {
        AdbStatusText.Text = text;
        AdbStatusText.Foreground = brush;
    }

    private string BuildAudioDiagnosticsReport()
    {
        var hostLog = _currentHostLog;
        var recentAudioLines = SnapshotRecentAudioLogLines();
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
        report.AppendLine($"最后麦克风状态日志: {FormatOptional(_lastMicrophoneStatusLine)}");
        report.AppendLine($"最后音响状态日志: {FormatOptional(_lastSpeakerStatusLine)}");
        report.AppendLine($"最后麦克风错误日志: {FormatOptional(_lastMicrophoneErrorLine)}");
        report.AppendLine($"最后音响错误日志: {FormatOptional(_lastSpeakerErrorLine)}");
        report.AppendLine();

        report.AppendLine("---- 配置 ----");
        report.AppendLine($"启用音频设备: {AudioDeviceSwitch.IsOn}");
        report.AppendLine($"启用麦克风: {MicrophoneSwitch.IsOn}");
        report.AppendLine($"启用音响: {SpeakerSwitch.IsOn}");
        report.AppendLine("音频后端: wasapi-virtual-cable");
        report.AppendLine($"电脑声音捕获端点状态: {_speakerCaptureEndpointDiagnostics.Summary}");
        report.AppendLine($"电脑声音捕获 endpoint id: {FormatOptional(_boundSpeakerCaptureEndpointId)}");
        report.AppendLine($"电脑声音捕获端点名称: {FormatOptional(_boundSpeakerCaptureEndpointName)}");
        report.AppendLine($"Android 麦克风写入端点状态: {_microphoneRenderEndpointDiagnostics.Summary}");
        report.AppendLine($"Android 麦克风写入 endpoint id: {FormatOptional(_boundMicrophoneRenderEndpointId)}");
        report.AppendLine($"Android 麦克风写入端点名称: {FormatOptional(_boundMicrophoneRenderEndpointName)}");
        report.AppendLine($"音频端口: {DefaultAudioPort}");
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
        report.AppendLine($"启动参数: {FormatOptional(hostLog?.Arguments)}");
        report.AppendLine($"ADB 路径: {FormatOptional(hostLog?.AdbPath)}");
        report.AppendLine($"ADB 设备: {FormatOptional(hostLog?.AdbSerial)}");
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

    private void ClearAudioDiagnostics()
    {
        lock (_audioLogGate)
        {
            _recentAudioLogLines.Clear();
        }

        _lastMicrophoneStatusLine = null;
        _lastSpeakerStatusLine = null;
        _lastMicrophoneErrorLine = null;
        _lastSpeakerErrorLine = null;
        _lastMicrophoneErrorMessage = null;
        _lastSpeakerErrorMessage = null;

        if (CopyAudioLogButtonText is not null)
        {
            CopyAudioLogButtonText.Text = "复制错误日志";
        }
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
        if (!line.Contains("[AUDIO]", StringComparison.OrdinalIgnoreCase))
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
        if (isSpeaker)
        {
            _speakerRuntimeStatus = nextStatus.Value;
            _lastSpeakerStatusLine = line;
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
            _boundSpeakerCaptureEndpointId = preferences.SpeakerCaptureEndpoint?.EndpointId;
            _boundSpeakerCaptureEndpointName = preferences.SpeakerCaptureEndpoint?.DisplayName;
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
            var preferences = new AudioPreferences
            {
                AudioDeviceEnabled = AudioDeviceSwitch.IsOn,
                MicrophoneEnabled = MicrophoneSwitch.IsOn,
                SpeakerEnabled = SpeakerSwitch.IsOn,
                MicrophoneRenderEndpoint = new AudioEndpointBinding
                {
                    EndpointId = _boundMicrophoneRenderEndpointId,
                    DisplayName = _boundMicrophoneRenderEndpointName
                },
                SpeakerCaptureEndpoint = new AudioEndpointBinding
                {
                    EndpointId = _boundSpeakerCaptureEndpointId,
                    DisplayName = _boundSpeakerCaptureEndpointName
                }
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

        _lastAudioHint = BuildAudioHint(audioEnabled, microphoneIntent, speakerIntent, overallStatus, hint);
        AudioHintText.Text = _lastAudioHint;
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
            AudioCapabilityStatus.Available => "音频设备可用。Windows/应用输出选择电脑声音线缆的 Input；会议软件麦克风选择麦克风线缆的 Output。",
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

    private sealed class AudioPreferences
    {
        public bool AudioDeviceEnabled { get; set; } = true;

        public bool MicrophoneEnabled { get; set; } = true;

        public bool SpeakerEnabled { get; set; } = true;

        public AudioEndpointBinding? MicrophoneRenderEndpoint { get; set; }

        public AudioEndpointBinding? SpeakerCaptureEndpoint { get; set; }
    }

    private sealed class AudioEndpointBinding
    {
        public string? EndpointId { get; set; }

        public string? DisplayName { get; set; }
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
                        ? "未绑定电脑声音捕获端点"
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
                    ? "正在枚举电脑声音捕获端点..."
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
                    ? "当前系统不支持枚举 Windows 录制端点。"
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
                    ? $"电脑声音捕获端点枚举失败：{message}"
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
            var roleName = role == AudioEndpointRole.SpeakerCapture ? "电脑声音捕获" : "Android 麦克风写入";
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

    private sealed record DeviceToolDiagnostics(int? ExitCode, string Output, bool TimedOut);
}
