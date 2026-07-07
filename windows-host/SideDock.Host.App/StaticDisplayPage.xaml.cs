using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;

namespace SideDock.Host.App;

public sealed partial class StaticDisplayPage : UserControl
{
    private const int MaxActivityLogEntries = 50;
    private const int VisibleActivityLogEntries = 5;
    private const double PreviewPadding = 14;

    private readonly Brush _primaryBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 8, 124, 137));
    private readonly Brush _textBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 48, 54, 61));
    private readonly Brush _mutedBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 91, 101, 112));
    private readonly Brush _strokeBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 216, 222, 228));
    private readonly Brush _whiteBrush = new SolidColorBrush(Colors.White);
    private readonly Brush _transparentBrush = new SolidColorBrush(Colors.Transparent);
    private readonly Brush _infoBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 11, 103, 163));
    private readonly Brush _successBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 18, 132, 86));
    private readonly Brush _warningBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 157, 93, 0));
    private readonly Brush _failureBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 43, 28));
    private readonly Brush _sideDockBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 11, 103, 227));
    private readonly Brush _sideDockBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 234, 246, 255));
    private readonly Brush _primaryDisplayBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 232, 247, 248));
    private readonly Brush _displayBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 255, 255));
    private readonly Brush _placeholderBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 248, 250, 252));
    private readonly List<ActivityLogEntry> _activityLogEntries = new();

    private string _selectedResolution = "1080p";
    private string _selectedRefreshRate = "120";
    private bool _autoManageEnabled = true;
    private bool _autostartEnabled;
    private bool _syncingSettings;
    private bool _syncingAutostart;
    private bool _displayOptionsEnabled = true;
    private bool _displayOptionsAvailable = true;
    private bool _displayModeApplyInProgress;
    private readonly DispatcherTimer _secondaryOnlyRollbackTimer = new();
    private bool _secondaryOnlyKeepConfirmationActive;
    private bool _secondaryOnlyPendingActionInProgress;
    private int _secondaryOnlyRollbackRemainingSeconds;
    private string? _lastPresentationDiagnostics;
    private VirtualDisplayPresentationMode _currentPresentationMode = VirtualDisplayPresentationMode.Unknown;
    private bool _statusBannerDismissed;
    private string? _lastLoggedStatus;
    private DisplayLayoutSnapshot _displayLayoutSnapshot = new(Array.Empty<DisplayLayoutMonitor>());
    private bool _virtualDisplayRunning;

    public StaticDisplayPage()
    {
        InitializeComponent();
        StaticDisplaySidebarVersionText.Text = AppVersionInfo.DisplayVersion;
        _secondaryOnlyRollbackTimer.Interval = TimeSpan.FromSeconds(1);
        _secondaryOnlyRollbackTimer.Tick += SecondaryOnlyRollbackTimer_Tick;
        UpdatePresentationDiagnostics(null);
        HideSecondaryOnlyKeepConfirmation();
        AddActivityLog("等待检测虚拟显示器状态。", StaticDisplayActivityKind.Info);
        SetSelectedDisplayOptions(_selectedResolution, _selectedRefreshRate);
        SetCurrentPresentationMode(VirtualDisplayPresentationMode.Unknown);
    }

    public event EventHandler? StartRequested;

    public event EventHandler? StopRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler? InstallDriverRequested;

    public event EventHandler? OpenDisplaySettingsRequested;

    public event EventHandler? ShowLogsRequested;

    internal event Func<object, StaticDisplayModeApplyRequestedEventArgs, Task<StaticDisplayModeApplyResult>>? DisplayModeApplyRequested;

    internal event Func<object, StaticDisplayPresentationModeApplyRequestedEventArgs, Task<StaticDisplayPresentationModeApplyResult>>? PresentationModeApplyRequested;

    internal event Func<object, StaticDisplayPresentationModePendingActionRequestedEventArgs, Task<StaticDisplayPresentationModePendingActionResult>>? PresentationModePendingActionRequested;

    internal event EventHandler<StaticDisplaySettingsChangedEventArgs>? SettingsChanged;

    internal event Func<object, StaticDisplayAutostartChangedEventArgs, Task<StaticDisplayAutostartChangeResult>>? AutostartChangeRequested;

    internal void ApplySettings(AppSettings settings)
    {
        _syncingSettings = true;
        try
        {
            _autoManageEnabled = settings.StartVirtualDisplayWithHost;
            _statusBannerDismissed = settings.StaticDisplayStatusBannerDismissed;
            AutoManageDisplaySwitch.IsOn = _autoManageEnabled;
            SetSelectedDisplayOptions(settings.VirtualDisplayResolution, settings.VirtualDisplayRefreshRate);
            SetCurrentPresentationMode(settings.VirtualDisplayPresentationMode);
        }
        finally
        {
            _syncingSettings = false;
        }
    }

    internal void ApplyAppearance(AppAppearancePalette palette, AppInterfaceDensity density)
    {
        RequestedTheme = palette.Theme;
        AppAppearance.ApplyPageResources(Resources, palette);
        AppAppearance.SetBrushColor(_primaryBrush, palette.Primary);
        AppAppearance.SetBrushColor(_textBrush, palette.Body);
        AppAppearance.SetBrushColor(_mutedBrush, palette.Muted);
        AppAppearance.SetBrushColor(_strokeBrush, palette.Stroke);
        AppAppearance.SetBrushColor(_whiteBrush, palette.PrimaryContrast);
        AppAppearance.SetBrushColor(_sideDockBrush, palette.Primary);
        AppAppearance.SetBrushColor(_sideDockBackgroundBrush, palette.InfoSoft);
        AppAppearance.SetBrushColor(_primaryDisplayBackgroundBrush, palette.InfoSoft);
        AppAppearance.SetBrushColor(_displayBackgroundBrush, palette.PanelBackground);
        AppAppearance.SetBrushColor(_placeholderBackgroundBrush, palette.SubtleBackground);
        AppAppearance.ApplyPalette(this, palette);
        AppAppearance.ApplyDensity(this, density);
    }

    internal void UpdateVirtualDisplayState(StaticDisplayPageState state)
    {
        if (state.BannerSeverity is StaticDisplayBannerSeverity.Warning or StaticDisplayBannerSeverity.Error
            && _statusBannerDismissed)
        {
            _statusBannerDismissed = false;
            NotifySettingsChanged(StaticDisplaySettingsChangeKind.Banner);
        }

        StatusBanner.Visibility = _statusBannerDismissed ? Visibility.Collapsed : Visibility.Visible;
        StatusBanner.Background = state.BannerBackground;
        StatusBanner.BorderBrush = state.BannerBorderBrush;
        StatusBannerIconBackground.Background = state.BannerIconBackground;
        StatusBannerIcon.Glyph = state.BannerIconGlyph;
        StatusBannerTitleText.Text = state.StatusText;
        StatusBannerTitleText.Foreground = state.StatusBrush;
        StatusBannerMessageText.Text = state.StatusDetail;

        DriverStatusText.Text = state.DriverStatusText;
        DriverStatusText.Foreground = state.DriverStatusBrush;
        DeviceToolStatusText.Text = state.DeviceToolStatusText;
        DeviceToolStatusText.Foreground = state.DeviceToolStatusBrush;
        SystemPermissionStatusText.Text = state.SystemPermissionStatusText;
        SystemPermissionStatusText.Foreground = state.SystemPermissionStatusBrush;
        UpdateAutostartState(
            state.AutostartEnabled,
            BuildAutostartStatusText(state.AutostartEnabled, state.CanChangeAutostart),
            state.CanChangeAutostart);

        TopStartDisplayButton.IsEnabled = state.CanStart;
        SideStartDisplayButton.IsEnabled = state.CanStart;
        SideStopDisplayButton.IsEnabled = state.CanStop;
        TopInstallDriverButton.IsEnabled = state.CanInstallDriver;
        SideInstallDriverButton.IsEnabled = state.CanInstallDriver;
        TopRefreshButton.IsEnabled = state.CanRefresh;
        SideRefreshButton.IsEnabled = state.CanRefresh;
        TopDisplaySettingsButton.IsEnabled = state.CanOpenDisplaySettings;

        SetDisplayOptionsEnabled(state.CanChangeDisplayOptions);
        SetSelectedDisplayOptions(state.Resolution, state.RefreshRate);
        SetCurrentPresentationMode(state.PresentationMode, state.PresentationModeMessage);
        UpdateFooter(state.FooterHostText, state.FooterOsText, state.FooterNetworkText, state.FooterNetworkBrush);
        _displayLayoutSnapshot = state.DisplayLayout;
        _virtualDisplayRunning = state.VirtualDisplayRunning;
        RenderDisplayLayoutPreview();

        var statusLogKey = $"{state.StatusText}|{state.StatusDetail}";
        if (!string.Equals(_lastLoggedStatus, statusLogKey, StringComparison.Ordinal))
        {
            _lastLoggedStatus = statusLogKey;
            AddActivityLog($"状态：{state.StatusText}", ActivityKindFromBannerSeverity(state.BannerSeverity));
        }
    }

    internal void AddActivityLog(string message, StaticDisplayActivityKind kind = StaticDisplayActivityKind.Info)
    {
        var (brush, glyph) = ActivityVisual(kind);
        AddActivityLog(message, brush, glyph, kind);
    }

    private void AddActivityLog(string message, Brush brush, string glyph, StaticDisplayActivityKind kind)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _activityLogEntries.Insert(0, new ActivityLogEntry(message, DateTimeOffset.Now, brush, glyph, kind));
        if (_activityLogEntries.Count > MaxActivityLogEntries)
        {
            _activityLogEntries.RemoveRange(MaxActivityLogEntries, _activityLogEntries.Count - MaxActivityLogEntries);
        }

        RenderActivityLogs();
    }

    private void NotifySettingsChanged(StaticDisplaySettingsChangeKind changeKind)
    {
        if (_syncingSettings)
        {
            return;
        }

        SettingsChanged?.Invoke(
            this,
            new StaticDisplaySettingsChangedEventArgs(
                _selectedResolution,
                _selectedRefreshRate,
                _currentPresentationMode,
                _autoManageEnabled,
                _statusBannerDismissed,
                changeKind));
    }

    private void UpdateAutostartState(bool isEnabled, string statusText, bool canChange)
    {
        _syncingAutostart = true;
        try
        {
            _autostartEnabled = isEnabled;
            AutostartStatusText.Text = statusText;
            AutostartSwitch.IsOn = isEnabled;
            AutostartSwitch.IsEnabled = canChange;
        }
        finally
        {
            _syncingAutostart = false;
        }
    }

    private void StartDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        AddActivityLog("已请求启动虚拟显示器。", StaticDisplayActivityKind.Info);
        StartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void StopDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        AddActivityLog("已请求停止虚拟显示器。", StaticDisplayActivityKind.Info);
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        AddActivityLog("正在重新检测虚拟显示器状态。", StaticDisplayActivityKind.Info);
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void InstallDriverButton_Click(object sender, RoutedEventArgs e)
    {
        AddActivityLog("已请求安装/修复虚拟显示器驱动。", StaticDisplayActivityKind.Info);
        InstallDriverRequested?.Invoke(this, EventArgs.Empty);
    }

    private void DisplaySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        AddActivityLog("正在打开 Windows 显示设置。", StaticDisplayActivityKind.Info);
        OpenDisplaySettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void LogsButton_Click(object sender, RoutedEventArgs e)
    {
        AddActivityLog("已打开完整日志。", StaticDisplayActivityKind.Info);
        ShowLogsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void StatusBannerCloseButton_Click(object sender, RoutedEventArgs e)
    {
        _statusBannerDismissed = true;
        StatusBanner.Visibility = Visibility.Collapsed;
        NotifySettingsChanged(StaticDisplaySettingsChangeKind.Banner);
        AddActivityLog("提示条已关闭，状态变为警告或错误时会自动重新显示。", StaticDisplayActivityKind.Info);
    }

    private void AutoManageDisplaySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings)
        {
            return;
        }

        _autoManageEnabled = AutoManageDisplaySwitch.IsOn;
        NotifySettingsChanged(StaticDisplaySettingsChangeKind.AutoManage);
        AddActivityLog(
            _autoManageEnabled
                ? "自动管理已开启，后续设置变更允许执行安全应用。"
                : "自动管理已关闭，后续设置变更仅保存 UI 状态。",
            StaticDisplayActivityKind.Info);
    }

    private async void AutostartSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingAutostart)
        {
            return;
        }

        var requestedEnabled = AutostartSwitch.IsOn;
        var handler = AutostartChangeRequested;
        if (handler is null)
        {
            UpdateAutostartState(_autostartEnabled, AutostartStatusText.Text, canChange: true);
            AddActivityLog("无法更新开机自启：页面尚未连接到主窗口。", StaticDisplayActivityKind.Failure);
            return;
        }

        AutostartSwitch.IsEnabled = false;
        AddActivityLog(
            requestedEnabled ? "正在开启当前用户开机自启。" : "正在关闭当前用户开机自启。",
            StaticDisplayActivityKind.Info);

        try
        {
            var result = await handler(this, new StaticDisplayAutostartChangedEventArgs(requestedEnabled));
            UpdateAutostartState(result.IsEnabled, result.StatusText, canChange: true);
            AddActivityLog(
                result.Success
                    ? result.Message
                    : $"开机自启更新失败：{result.Message}",
                result.Success ? StaticDisplayActivityKind.Success : StaticDisplayActivityKind.Failure);
        }
        catch (Exception ex)
        {
            UpdateAutostartState(_autostartEnabled, AutostartStatusText.Text, canChange: true);
            AddActivityLog($"开机自启更新失败：{ex.Message}", StaticDisplayActivityKind.Failure);
        }
    }

    private async void ResolutionOption_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!_displayOptionsEnabled || sender is not FrameworkElement { Tag: string value })
        {
            return;
        }

        var normalized = NormalizeResolution(value);
        if (string.Equals(_selectedResolution, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetSelectedResolution(normalized);
        NotifySettingsChanged(StaticDisplaySettingsChangeKind.Selection);

        if (!_autoManageEnabled)
        {
            AddActivityLog($"分辨率已保存为 {ResolutionLabel(normalized)}，自动管理关闭，未修改 Windows 显示拓扑。", StaticDisplayActivityKind.Info);
            return;
        }

        await RequestDisplayModeApplyAsync(normalized, _selectedRefreshRate);
    }

    private async void RefreshRateOption_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!_displayOptionsEnabled || sender is not FrameworkElement { Tag: string value })
        {
            return;
        }

        var normalized = NormalizeRefreshRate(value);
        if (string.Equals(_selectedRefreshRate, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetSelectedRefreshRate(normalized);
        NotifySettingsChanged(StaticDisplaySettingsChangeKind.Selection);

        if (!_autoManageEnabled)
        {
            AddActivityLog($"刷新率已保存为 {normalized} Hz，自动管理关闭，未修改 Windows 显示拓扑。", StaticDisplayActivityKind.Info);
            return;
        }

        await RequestDisplayModeApplyAsync(_selectedResolution, normalized);
    }

    private async void PresentationModeOption_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!_displayOptionsEnabled
            || sender is not FrameworkElement { Tag: string value }
            || !TryParsePresentationMode(value, out var mode))
        {
            return;
        }

        if (!_autoManageEnabled)
        {
            SetCurrentPresentationMode(mode);
            NotifySettingsChanged(StaticDisplaySettingsChangeKind.Selection);
            AddActivityLog($"{PresentationModeLabel(mode)} saved. Auto manage is off, Windows display topology was not changed.", StaticDisplayActivityKind.Info);
            return;
        }

        if (mode == VirtualDisplayPresentationMode.SecondaryOnly
            && !await ConfirmSecondaryOnlySwitchAsync())
        {
            AddActivityLog("已取消仅副屏模式切换，未修改系统显示拓扑。", StaticDisplayActivityKind.Info);
            return;
        }

        await RequestPresentationModeApplyAsync(mode);
    }

    private async Task RequestDisplayModeApplyAsync(string resolution, string refreshRate)
    {
        var handler = DisplayModeApplyRequested;
        if (handler is null)
        {
            AddActivityLog("无法应用显示模式：页面尚未连接到主窗口。", StaticDisplayActivityKind.Failure);
            return;
        }

        var requestText = BuildDisplayModeText(resolution, refreshRate);
        SetDisplayOptionsApplying(true);
        AddActivityLog($"正在应用显示模式：{requestText}。", StaticDisplayActivityKind.Info);

        try
        {
            var result = await handler(this, new StaticDisplayModeApplyRequestedEventArgs(resolution, refreshRate));
            if (!string.IsNullOrWhiteSpace(result.DisplayedResolution)
                && !string.IsNullOrWhiteSpace(result.DisplayedRefreshRate))
            {
                SetSelectedDisplayOptions(result.DisplayedResolution, result.DisplayedRefreshRate);
            }

            AddActivityLog(
                result.Success
                    ? $"显示模式已应用：{result.CurrentModeText ?? requestText}。"
                    : $"显示模式应用失败：{result.Message}",
                result.Success ? StaticDisplayActivityKind.Success : StaticDisplayActivityKind.Failure);
        }
        catch (Exception ex)
        {
            AddActivityLog($"显示模式应用失败：{ex.Message}", StaticDisplayActivityKind.Failure);
        }
        finally
        {
            SetDisplayOptionsApplying(false);
        }
    }

    private async Task RequestPresentationModeApplyAsync(VirtualDisplayPresentationMode mode)
    {
        var handler = PresentationModeApplyRequested;
        if (handler is null)
        {
            AddActivityLog("无法切换显示模式：页面尚未连接到主窗口。", StaticDisplayActivityKind.Failure);
            return;
        }

        var label = PresentationModeLabel(mode);
        SetDisplayOptionsApplying(true);
        ShowPresentationModeDetail(mode, $"正在切换为{label}。");
        AddActivityLog($"正在切换为{label}。", StaticDisplayActivityKind.Info);

        try
        {
            var result = await handler(this, new StaticDisplayPresentationModeApplyRequestedEventArgs(mode));
            UpdatePresentationDiagnostics(result.DiagnosticSummary);
            SetCurrentPresentationMode(
                result.CurrentMode,
                result.Success ? result.Message : $"{label}切换失败：{result.Message}");
            if (result.Success && result.RequiresKeepConfirmation)
            {
                StartSecondaryOnlyKeepConfirmation(result.KeepConfirmationSeconds, result.Message);
            }
            else if (result.Success)
            {
                HideSecondaryOnlyKeepConfirmation();
                NotifySettingsChanged(StaticDisplaySettingsChangeKind.Selection);
            }

            AddActivityLog(
                result.Success ? result.Message : $"{label}切换失败：{result.Message}",
                result.Success ? StaticDisplayActivityKind.Success : StaticDisplayActivityKind.Failure);
        }
        catch (Exception ex)
        {
            UpdatePresentationDiagnostics(null);
            ShowPresentationModeDetail(mode, $"{label}切换失败：{ex.Message}");
            AddActivityLog($"{label}切换失败：{ex.Message}", StaticDisplayActivityKind.Failure);
        }
        finally
        {
            SetDisplayOptionsApplying(false);
        }
    }

    private async Task<bool> ConfirmSecondaryOnlySwitchAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "切换为仅副屏？",
            Content = "这会临时关闭主屏和其它非 SideDock 输出，只保留 SideDock 虚拟显示器。切换成功后需要在倒计时内确认保留，否则会自动恢复切换前拓扑。",
            PrimaryButtonText = "继续切换",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void StartSecondaryOnlyKeepConfirmation(int seconds, string message)
    {
        _secondaryOnlyKeepConfirmationActive = true;
        _secondaryOnlyPendingActionInProgress = false;
        _secondaryOnlyRollbackRemainingSeconds = Math.Max(5, seconds);
        SecondaryOnlyRollbackPanel.Visibility = Visibility.Visible;
        KeepSecondaryOnlyButton.IsEnabled = true;
        RestoreSecondaryOnlyButton.IsEnabled = true;
        UpdateSecondaryOnlyRollbackText(message);
        UpdateDisplayOptionsAvailability();
        _secondaryOnlyRollbackTimer.Stop();
        _secondaryOnlyRollbackTimer.Start();
    }

    private void HideSecondaryOnlyKeepConfirmation()
    {
        _secondaryOnlyRollbackTimer.Stop();
        _secondaryOnlyKeepConfirmationActive = false;
        _secondaryOnlyPendingActionInProgress = false;
        SecondaryOnlyRollbackPanel.Visibility = Visibility.Collapsed;
        KeepSecondaryOnlyButton.IsEnabled = true;
        RestoreSecondaryOnlyButton.IsEnabled = true;
        UpdateDisplayOptionsAvailability();
    }

    private void UpdateSecondaryOnlyRollbackText(string? message = null)
    {
        var prefix = string.IsNullOrWhiteSpace(message)
            ? "仅副屏已临时启用"
            : message.TrimEnd('。');
        SecondaryOnlyRollbackText.Text = $"{prefix}。请在 {_secondaryOnlyRollbackRemainingSeconds} 秒内保留此设置，否则自动恢复。";
    }

    private async void SecondaryOnlyRollbackTimer_Tick(object? sender, object e)
    {
        if (!_secondaryOnlyKeepConfirmationActive || _secondaryOnlyPendingActionInProgress)
        {
            return;
        }

        _secondaryOnlyRollbackRemainingSeconds--;
        if (_secondaryOnlyRollbackRemainingSeconds <= 0)
        {
            _secondaryOnlyRollbackTimer.Stop();
            await CompleteSecondaryOnlyPendingActionAsync(
                StaticDisplayPresentationModePendingAction.Restore,
                "倒计时结束，正在恢复切换前拓扑。");
            return;
        }

        UpdateSecondaryOnlyRollbackText();
    }

    private async void KeepSecondaryOnlyButton_Click(object sender, RoutedEventArgs e)
    {
        await CompleteSecondaryOnlyPendingActionAsync(
            StaticDisplayPresentationModePendingAction.Keep,
            "正在保留仅副屏模式。");
    }

    private async void RestoreSecondaryOnlyButton_Click(object sender, RoutedEventArgs e)
    {
        await CompleteSecondaryOnlyPendingActionAsync(
            StaticDisplayPresentationModePendingAction.Restore,
            "正在恢复切换前显示拓扑。");
    }

    private async Task CompleteSecondaryOnlyPendingActionAsync(
        StaticDisplayPresentationModePendingAction action,
        string activityMessage)
    {
        var handler = PresentationModePendingActionRequested;
        if (handler is null)
        {
            AddActivityLog("无法处理仅副屏确认：页面尚未连接到主窗口。", StaticDisplayActivityKind.Failure);
            return;
        }

        _secondaryOnlyRollbackTimer.Stop();
        _secondaryOnlyPendingActionInProgress = true;
        KeepSecondaryOnlyButton.IsEnabled = false;
        RestoreSecondaryOnlyButton.IsEnabled = false;
        AddActivityLog(activityMessage, StaticDisplayActivityKind.Info);

        try
        {
            var result = await handler(this, new StaticDisplayPresentationModePendingActionRequestedEventArgs(action));
            UpdatePresentationDiagnostics(result.DiagnosticSummary);
            SetCurrentPresentationMode(
                result.CurrentMode,
                result.Success ? result.Message : $"仅副屏确认失败：{result.Message}");

            if (result.Success)
            {
                HideSecondaryOnlyKeepConfirmation();
                if (action == StaticDisplayPresentationModePendingAction.Keep)
                {
                    NotifySettingsChanged(StaticDisplaySettingsChangeKind.Selection);
                }
            }
            else
            {
                _secondaryOnlyPendingActionInProgress = false;
                KeepSecondaryOnlyButton.IsEnabled = true;
                RestoreSecondaryOnlyButton.IsEnabled = true;
                SecondaryOnlyRollbackPanel.Visibility = Visibility.Visible;
                SecondaryOnlyRollbackText.Text = result.Message;
            }

            AddActivityLog(
                result.Success ? result.Message : $"仅副屏确认失败：{result.Message}",
                result.Success ? StaticDisplayActivityKind.Success : StaticDisplayActivityKind.Failure);
        }
        catch (Exception ex)
        {
            _secondaryOnlyPendingActionInProgress = false;
            KeepSecondaryOnlyButton.IsEnabled = true;
            RestoreSecondaryOnlyButton.IsEnabled = true;
            SecondaryOnlyRollbackText.Text = $"仅副屏确认失败：{ex.Message}";
            AddActivityLog($"仅副屏确认失败：{ex.Message}", StaticDisplayActivityKind.Failure);
        }
    }

    private void UpdatePresentationDiagnostics(string? diagnostics)
    {
        _lastPresentationDiagnostics = string.IsNullOrWhiteSpace(diagnostics) ? null : diagnostics;
        if (CopyPresentationDiagnosticsButton is not null)
        {
            CopyPresentationDiagnosticsButton.Visibility = _lastPresentationDiagnostics is null
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void CopyPresentationDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastPresentationDiagnostics))
        {
            return;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(_lastPresentationDiagnostics);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        AddActivityLog("显示拓扑诊断摘要已复制。", StaticDisplayActivityKind.Success);
    }

    private void SetSelectedDisplayOptions(string resolution, string refreshRate)
    {
        SetSelectedResolution(resolution);
        SetSelectedRefreshRate(refreshRate);
    }

    private void SetSelectedResolution(string resolution)
    {
        _selectedResolution = NormalizeResolution(resolution);
        SetResolutionOption(Resolution720pOption, Resolution720pLabel, Resolution720pDetail, Resolution720pRadio, Resolution720pRadioDot, _selectedResolution == "720p");
        SetResolutionOption(Resolution1080pOption, Resolution1080pLabel, Resolution1080pDetail, Resolution1080pRadio, Resolution1080pRadioDot, _selectedResolution == "1080p");
        SetResolutionOption(Resolution2kOption, Resolution2kLabel, Resolution2kDetail, Resolution2kRadio, Resolution2kRadioDot, _selectedResolution == "2k");
        UpdatePreviewResolutionText();
    }

    private void SetSelectedRefreshRate(string refreshRate)
    {
        _selectedRefreshRate = NormalizeRefreshRate(refreshRate);
        SetRefreshRateOption(RefreshRate30Option, RefreshRate30Text, _selectedRefreshRate == "30");
        SetRefreshRateOption(RefreshRate60Option, RefreshRate60Text, _selectedRefreshRate == "60");
        SetRefreshRateOption(RefreshRate120Option, RefreshRate120Text, _selectedRefreshRate == "120");
        UpdatePreviewResolutionText();
    }

    private void SetDisplayOptionsEnabled(bool enabled)
    {
        _displayOptionsAvailable = enabled;
        UpdateDisplayOptionsAvailability();
    }

    private void SetDisplayOptionsApplying(bool applying)
    {
        _displayModeApplyInProgress = applying;
        UpdateDisplayOptionsAvailability();
    }

    private void UpdateDisplayOptionsAvailability()
    {
        _displayOptionsEnabled = _displayOptionsAvailable
            && !_displayModeApplyInProgress
            && !_secondaryOnlyKeepConfirmationActive;
        var opacity = _displayOptionsEnabled ? 1 : 0.56;
        DisplayPresentationOptionsPanel.Opacity = opacity;
        DisplayPresentationExtendOption.IsHitTestVisible = _displayOptionsEnabled;
        DisplayPresentationMirrorOption.IsHitTestVisible = _displayOptionsEnabled;
        DisplayPresentationSecondaryOnlyOption.IsHitTestVisible = _displayOptionsEnabled;
        Resolution720pOption.Opacity = opacity;
        Resolution1080pOption.Opacity = opacity;
        Resolution2kOption.Opacity = opacity;
        RefreshRate30Option.Opacity = opacity;
        RefreshRate60Option.Opacity = opacity;
        RefreshRate120Option.Opacity = opacity;
    }

    private void SetCurrentPresentationMode(VirtualDisplayPresentationMode mode, string? message = null)
    {
        _currentPresentationMode = mode;
        SetPresentationModeOption(
            DisplayPresentationExtendOption,
            DisplayPresentationExtendText,
            mode == VirtualDisplayPresentationMode.Extend);
        SetPresentationModeOption(
            DisplayPresentationMirrorOption,
            DisplayPresentationMirrorText,
            mode == VirtualDisplayPresentationMode.Mirror);
        SetPresentationModeOption(
            DisplayPresentationSecondaryOnlyOption,
            DisplayPresentationSecondaryOnlyText,
            mode == VirtualDisplayPresentationMode.SecondaryOnly);
        ShowPresentationModeDetail(mode, message);
    }

    private void SetPresentationModeOption(Border option, TextBlock text, bool selected)
    {
        option.Background = selected ? _primaryBrush : _transparentBrush;
        text.Foreground = selected ? _whiteBrush : _textBrush;
        text.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void ShowUnsupportedPresentationMode(VirtualDisplayPresentationMode mode)
    {
        var label = PresentationModeLabel(mode);
        var message = mode == VirtualDisplayPresentationMode.Mirror
            ? "镜像模式暂未支持，需要后续安全确认流程。"
            : "仅副屏模式暂未支持，需要后续安全确认流程。";
        ShowPresentationModeDetail(mode, message);
        AddActivityLog($"{label}暂未支持，未修改系统显示拓扑。", StaticDisplayActivityKind.Info);
    }

    private void ShowPresentationModeDetail(VirtualDisplayPresentationMode mode, string? message = null)
    {
        var (title, description, glyph, brush) = mode switch
        {
            VirtualDisplayPresentationMode.Extend => (
                "扩展模式",
                "将虚拟显示器作为扩展桌面使用，可独立设置分辨率与刷新率。",
                "\uE7F4",
                _primaryBrush),
            VirtualDisplayPresentationMode.Mirror => (
                "镜像模式",
                "仅将主屏和 SideDock 虚拟显示器组成镜像，其它显示器保持独立。",
                "\uE7F4",
                _primaryBrush),
            VirtualDisplayPresentationMode.SecondaryOnly => (
                "仅副屏模式",
                "临时关闭非 SideDock 输出，只保留 SideDock 虚拟显示器；未确认会自动恢复。",
                "\uE7F4",
                _warningBrush),
            _ => (
                "显示模式",
                "未检测到 SideDock 扩展桌面，可在虚拟显示器可用后切换为扩展模式。",
                "\uE946",
                _mutedBrush)
        };

        DisplayPresentationModeTitleText.Text = title;
        DisplayPresentationModeDescriptionText.Text = string.IsNullOrWhiteSpace(message) ? description : message;
        DisplayPresentationModeIcon.Glyph = glyph;
        DisplayPresentationModeIcon.Foreground = brush;
    }

    private void SetResolutionOption(
        Border option,
        TextBlock label,
        TextBlock detail,
        Border radio,
        Ellipse radioDot,
        bool selected)
    {
        option.Background = selected ? _primaryBrush : _whiteBrush;
        option.BorderBrush = selected ? _primaryBrush : _strokeBrush;
        label.Foreground = selected ? _whiteBrush : _textBrush;
        label.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        detail.Foreground = selected ? _whiteBrush : _mutedBrush;
        radio.BorderBrush = selected ? _whiteBrush : new SolidColorBrush(ColorHelper.FromArgb(255, 156, 163, 175));
        radioDot.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetRefreshRateOption(Border option, TextBlock text, bool selected)
    {
        option.Background = selected ? _primaryBrush : _transparentBrush;
        text.Foreground = selected ? _whiteBrush : _textBrush;
        text.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void UpdatePreviewResolutionText()
    {
        RenderDisplayLayoutPreview();
    }

    private void UpdateFooter(string hostText, string osText, string networkText, Brush networkBrush)
    {
        FooterHostText.Text = string.IsNullOrWhiteSpace(hostText) ? "本机：暂无数据" : hostText;
        FooterOsText.Text = string.IsNullOrWhiteSpace(osText) ? "Windows" : osText;
        FooterNetworkText.Text = string.IsNullOrWhiteSpace(networkText) ? "网络：暂无数据" : networkText;
        FooterNetworkStatusDot.Fill = networkBrush;
    }

    private void DisplayLayoutPreviewSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderDisplayLayoutPreview();
    }

    private void RenderDisplayLayoutPreview()
    {
        DisplayLayoutCanvas.Children.Clear();

        var canvasWidth = DisplayLayoutCanvas.ActualWidth;
        var canvasHeight = DisplayLayoutCanvas.ActualHeight;
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_displayLayoutSnapshot.QueryError))
        {
            ShowDisplayLayoutPreviewStatus($"无法读取显示器布局：{_displayLayoutSnapshot.QueryError}");
            return;
        }

        var monitors = _displayLayoutSnapshot.Monitors;
        if (monitors.Count == 0)
        {
            ShowDisplayLayoutPreviewStatus("未读取到活动显示器。");
            return;
        }

        DisplayLayoutPreviewStatusText.Visibility = Visibility.Collapsed;
        var previewItems = monitors
            .Select((monitor, index) => DisplayPreviewItem.FromMonitor(monitor, index + 1))
            .ToList();

        if (!_displayLayoutSnapshot.HasSideDockVirtualDisplay)
        {
            previewItems.Add(BuildMissingSideDockPreviewItem(monitors));
        }

        var minX = previewItems.Min(item => item.X);
        var minY = previewItems.Min(item => item.Y);
        var maxX = previewItems.Max(item => item.X + item.Width);
        var maxY = previewItems.Max(item => item.Y + item.Height);
        var boundsWidth = Math.Max(1, maxX - minX);
        var boundsHeight = Math.Max(1, maxY - minY);
        var availableWidth = Math.Max(1, canvasWidth - PreviewPadding * 2);
        var availableHeight = Math.Max(1, canvasHeight - PreviewPadding * 2);
        var scale = Math.Min(availableWidth / boundsWidth, availableHeight / boundsHeight);
        var offsetX = (canvasWidth - boundsWidth * scale) / 2;
        var offsetY = (canvasHeight - boundsHeight * scale) / 2;

        foreach (var item in previewItems)
        {
            var itemWidth = Math.Max(28, item.Width * scale);
            var itemHeight = Math.Max(22, item.Height * scale);
            var left = offsetX + (item.X - minX) * scale;
            var top = offsetY + (item.Y - minY) * scale;
            var element = item.IsPlaceholder
                ? CreateMissingSideDockPreviewElement(itemWidth, itemHeight)
                : CreateMonitorPreviewElement(item, itemWidth, itemHeight);

            Canvas.SetLeft(element, left);
            Canvas.SetTop(element, top);
            DisplayLayoutCanvas.Children.Add(element);
        }
    }

    private void ShowDisplayLayoutPreviewStatus(string message)
    {
        DisplayLayoutCanvas.Children.Clear();
        DisplayLayoutPreviewStatusText.Text = message;
        DisplayLayoutPreviewStatusText.Visibility = Visibility.Visible;
    }

    private DisplayPreviewItem BuildMissingSideDockPreviewItem(IReadOnlyList<DisplayLayoutMonitor> monitors)
    {
        var primary = monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
        var maxRight = monitors.Max(monitor => monitor.X + monitor.Width);
        var gap = Math.Max(80, primary.Width * 0.05);
        var width = Math.Max(480, primary.Width * 0.42);
        var height = Math.Max(270, width * 9 / 16);
        var y = primary.Y + (primary.Height - height) / 2;

        return DisplayPreviewItem.MissingSideDock(maxRight + gap, y, width, height);
    }

    private FrameworkElement CreateMonitorPreviewElement(DisplayPreviewItem item, double width, double height)
    {
        var monitor = item.Monitor!;
        var borderBrush = monitor.IsSideDockVirtualDisplay
            ? _sideDockBrush
            : monitor.IsPrimary ? _primaryBrush : _strokeBrush;
        var background = monitor.IsSideDockVirtualDisplay
            ? _sideDockBackgroundBrush
            : monitor.IsPrimary ? _primaryDisplayBackgroundBrush : _displayBackgroundBrush;
        var textBrush = monitor.IsSideDockVirtualDisplay
            ? _sideDockBrush
            : monitor.IsPrimary ? _primaryBrush : _textBrush;
        var compact = width < 170 || height < 105;
        var padding = compact ? 8 : 12;
        var maxTextWidth = Math.Max(24, width - padding * 2);

        var border = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(7),
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(monitor.IsPrimary || monitor.IsSideDockVirtualDisplay ? 2 : 1),
            Background = background
        };

        var content = new Grid
        {
            Padding = new Thickness(padding)
        };

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = compact ? 3 : 6
        };

        if (height >= 76)
        {
            panel.Children.Add(CreatePreviewText(
                monitor.IsSideDockVirtualDisplay ? "SideDock" : monitor.IsPrimary ? "主屏" : $"显示器 {item.Number}",
                maxTextWidth,
                compact ? 11 : 12,
                textBrush,
                FontWeights.SemiBold));
        }

        panel.Children.Add(CreatePreviewText(
            monitor.IsSideDockVirtualDisplay ? "SideDock 虚拟显示器" : monitor.IsPrimary ? "主屏幕" : monitor.DisplayName,
            maxTextWidth,
            compact ? 13 : 17,
            _textBrush,
            FontWeights.SemiBold));

        if (height >= 68)
        {
            panel.Children.Add(CreatePreviewText(
                $"{monitor.ResolutionText}  {monitor.RefreshRateText}",
                maxTextWidth,
                compact ? 11 : 12,
                _mutedBrush,
                FontWeights.Normal));
        }

        if (height >= 104)
        {
            panel.Children.Add(CreatePreviewText(
                $"{monitor.PositionText}  {monitor.DeviceName}",
                maxTextWidth,
                compact ? 10 : 11,
                _mutedBrush,
                FontWeights.Normal));
        }

        content.Children.Add(panel);
        border.Child = content;
        ToolTipService.SetToolTip(border, BuildMonitorToolTip(monitor));
        return border;
    }

    private FrameworkElement CreateMissingSideDockPreviewElement(double width, double height)
    {
        var grid = new Grid
        {
            Width = width,
            Height = height,
            Background = _placeholderBackgroundBrush
        };

        grid.Children.Add(new Rectangle
        {
            Width = width,
            Height = height,
            RadiusX = 7,
            RadiusY = 7,
            Stroke = _mutedBrush,
            StrokeThickness = 1.4,
            StrokeDashArray = new DoubleCollection { 5, 3 },
            Fill = _transparentBrush
        });

        var compact = width < 170 || height < 95;
        var maxTextWidth = Math.Max(24, width - 24);
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = compact ? 3 : 6
        };
        panel.Children.Add(CreatePreviewText(
            "未检测到",
            maxTextWidth,
            compact ? 11 : 12,
            _warningBrush,
            FontWeights.SemiBold));
        panel.Children.Add(CreatePreviewText(
            "SideDock 虚拟显示器",
            maxTextWidth,
            compact ? 13 : 16,
            _textBrush,
            FontWeights.SemiBold));

        if (height >= 78)
        {
            panel.Children.Add(CreatePreviewText(
                _virtualDisplayRunning ? "工具运行中，等待系统显示列表更新" : "启动后显示真实布局",
                maxTextWidth,
                compact ? 10 : 12,
                _mutedBrush,
                FontWeights.Normal));
        }

        grid.Children.Add(panel);
        ToolTipService.SetToolTip(
            grid,
            "系统显示器列表中未发现匹配 SideDock Virtual Display / SideDockIdd / SideDock 的设备。");
        return grid;
    }

    private TextBlock CreatePreviewText(
        string text,
        double maxWidth,
        double fontSize,
        Brush foreground,
        Windows.UI.Text.FontWeight fontWeight)
    {
        return new TextBlock
        {
            Text = text,
            MaxWidth = maxWidth,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            FontSize = fontSize,
            FontWeight = fontWeight,
            Foreground = foreground
        };
    }

    private static string BuildMonitorToolTip(DisplayLayoutMonitor monitor)
    {
        var type = monitor.IsSideDockVirtualDisplay
            ? "SideDock 虚拟显示器"
            : monitor.IsPrimary ? "主屏幕" : "普通显示器";
        return string.Join(
            Environment.NewLine,
            type,
            $"名称: {monitor.DisplayName}",
            $"DeviceName: {monitor.DeviceName}",
            $"DeviceString: {FormatDisplayValue(monitor.DeviceString)}",
            $"DeviceID: {FormatDisplayValue(monitor.DeviceId)}",
            $"分辨率: {monitor.ResolutionText}",
            $"刷新率: {monitor.RefreshRateText}",
            $"坐标: {monitor.PositionText}");
    }

    private void RenderActivityLogs()
    {
        ActivityLogStackPanel.Children.Clear();

        foreach (var entry in _activityLogEntries.Take(VisibleActivityLogEntries))
        {
            ActivityLogStackPanel.Children.Add(CreateActivityLogRow(entry));
            ActivityLogStackPanel.Children.Add(new Rectangle
            {
                Height = 1,
                Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 238, 241, 244))
            });
        }
    }

    private UIElement CreateActivityLogRow(ActivityLogEntry entry)
    {
        var row = new Grid
        {
            Height = 30,
            ColumnSpacing = 10
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = entry.Glyph,
            FontSize = 14,
            Foreground = entry.Brush
        };
        ToolTipService.SetToolTip(icon, ActivityKindLabel(entry.Kind));
        row.Children.Add(icon);

        var message = new TextBlock
        {
            Text = entry.Message,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = _textBrush,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(message, 1);
        row.Children.Add(message);

        var time = new TextBlock
        {
            Text = entry.Timestamp.ToString("HH:mm:ss"),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = _mutedBrush
        };
        Grid.SetColumn(time, 2);
        row.Children.Add(time);

        return row;
    }

    private (Brush Brush, string Glyph) ActivityVisual(StaticDisplayActivityKind kind)
    {
        return kind switch
        {
            StaticDisplayActivityKind.Success => (_successBrush, "\uE73E"),
            StaticDisplayActivityKind.Warning => (_warningBrush, "\uE7BA"),
            StaticDisplayActivityKind.Failure => (_failureBrush, "\uE783"),
            _ => (_infoBrush, "\uE946")
        };
    }

    private static StaticDisplayActivityKind ActivityKindFromBannerSeverity(StaticDisplayBannerSeverity severity)
    {
        return severity switch
        {
            StaticDisplayBannerSeverity.Ready => StaticDisplayActivityKind.Success,
            StaticDisplayBannerSeverity.Warning => StaticDisplayActivityKind.Warning,
            StaticDisplayBannerSeverity.Error => StaticDisplayActivityKind.Failure,
            _ => StaticDisplayActivityKind.Info
        };
    }

    private static string ActivityKindLabel(StaticDisplayActivityKind kind)
    {
        return kind switch
        {
            StaticDisplayActivityKind.Success => "成功",
            StaticDisplayActivityKind.Warning => "警告",
            StaticDisplayActivityKind.Failure => "失败",
            _ => "信息"
        };
    }

    private static string FormatDisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(空)" : value;
    }

    private static string BuildAutostartStatusText(bool isEnabled, bool canChange)
    {
        if (!canChange)
        {
            return "\u8bfb\u53d6\u5931\u8d25";
        }

        return isEnabled
            ? "\u5df2\u5f00\u542f"
            : "\u5df2\u5173\u95ed";
    }

    private static string NormalizeResolution(string? value)
    {
        var cleaned = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "1080p";
        }

        var compact = cleaned
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("×", "x", StringComparison.Ordinal)
            .ToLowerInvariant();
        return compact switch
        {
            "720" or "720p" => "720p",
            "1280x720" => "720p",
            "1080" or "1080p" => "1080p",
            "1920x1080" => "1080p",
            "2k" or "1440p" => "2k",
            "2560x1440" => "2k",
            _ => cleaned
        };
    }

    private static string NormalizeRefreshRate(string? value)
    {
        var cleaned = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "120";
        }

        var numericText = cleaned
            .Replace("Hz", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        return numericText switch
        {
            "30" => "30",
            "60" => "60",
            "120" => "120",
            _ => int.TryParse(numericText, out var refreshRate) && refreshRate > 0
                ? refreshRate.ToString()
                : cleaned
        };
    }

    private static string ResolutionLabel(string resolution)
    {
        var normalized = NormalizeResolution(resolution);
        return normalized == "2k" ? "2K" : normalized;
    }

    private static string BuildDisplayModeText(string resolution, string refreshRate)
    {
        return $"{ResolutionSizeText(resolution)} @ {NormalizeRefreshRate(refreshRate)} Hz";
    }

    private static bool TryParsePresentationMode(string value, out VirtualDisplayPresentationMode mode)
    {
        return Enum.TryParse(value, ignoreCase: true, out mode)
            && mode is VirtualDisplayPresentationMode.Extend
                or VirtualDisplayPresentationMode.Mirror
                or VirtualDisplayPresentationMode.SecondaryOnly;
    }

    private static string PresentationModeLabel(VirtualDisplayPresentationMode mode)
    {
        return mode switch
        {
            VirtualDisplayPresentationMode.Mirror => "镜像模式",
            VirtualDisplayPresentationMode.SecondaryOnly => "仅副屏模式",
            _ => "扩展模式"
        };
    }

    private static string ResolutionSizeText(string resolution)
    {
        return NormalizeResolution(resolution) switch
        {
            "720p" => "1280 × 720",
            "1080p" => "1920 × 1080",
            "2k" => "2560 × 1440",
            var custom => custom
        };
    }

    private sealed record ActivityLogEntry(
        string Message,
        DateTimeOffset Timestamp,
        Brush Brush,
        string Glyph,
        StaticDisplayActivityKind Kind);

    private sealed record DisplayPreviewItem(
        DisplayLayoutMonitor? Monitor,
        bool IsPlaceholder,
        int Number,
        double X,
        double Y,
        double Width,
        double Height)
    {
        public static DisplayPreviewItem FromMonitor(DisplayLayoutMonitor monitor, int number)
        {
            return new DisplayPreviewItem(
                monitor,
                false,
                number,
                monitor.X,
                monitor.Y,
                monitor.Width,
                monitor.Height);
        }

        public static DisplayPreviewItem MissingSideDock(double x, double y, double width, double height)
        {
            return new DisplayPreviewItem(
                null,
                true,
                0,
                x,
                y,
                width,
                height);
        }
    }
}

internal sealed class StaticDisplayModeApplyRequestedEventArgs : EventArgs
{
    public StaticDisplayModeApplyRequestedEventArgs(string resolution, string refreshRate)
    {
        Resolution = resolution;
        RefreshRate = refreshRate;
    }

    public string Resolution { get; }

    public string RefreshRate { get; }
}

internal sealed class StaticDisplayModeApplyResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? DisplayedResolution { get; init; }

    public string? DisplayedRefreshRate { get; init; }

    public string? CurrentModeText { get; init; }
}

internal sealed class StaticDisplayPresentationModeApplyRequestedEventArgs : EventArgs
{
    public StaticDisplayPresentationModeApplyRequestedEventArgs(VirtualDisplayPresentationMode mode)
    {
        Mode = mode;
    }

    public VirtualDisplayPresentationMode Mode { get; }
}

internal sealed class StaticDisplayPresentationModeApplyResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public VirtualDisplayPresentationMode CurrentMode { get; init; } = VirtualDisplayPresentationMode.Unknown;

    public string? DiagnosticSummary { get; init; }

    public bool RequiresKeepConfirmation { get; init; }

    public int KeepConfirmationSeconds { get; init; } = 20;
}

internal sealed class StaticDisplayPresentationModePendingActionRequestedEventArgs : EventArgs
{
    public StaticDisplayPresentationModePendingActionRequestedEventArgs(
        StaticDisplayPresentationModePendingAction action)
    {
        Action = action;
    }

    public StaticDisplayPresentationModePendingAction Action { get; }
}

internal sealed class StaticDisplayPresentationModePendingActionResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public VirtualDisplayPresentationMode CurrentMode { get; init; } = VirtualDisplayPresentationMode.Unknown;

    public string? DiagnosticSummary { get; init; }
}

internal sealed class StaticDisplaySettingsChangedEventArgs : EventArgs
{
    public StaticDisplaySettingsChangedEventArgs(
        string resolution,
        string refreshRate,
        VirtualDisplayPresentationMode presentationMode,
        bool autoManageEnabled,
        bool statusBannerDismissed,
        StaticDisplaySettingsChangeKind changeKind)
    {
        Resolution = resolution;
        RefreshRate = refreshRate;
        PresentationMode = presentationMode;
        AutoManageEnabled = autoManageEnabled;
        StatusBannerDismissed = statusBannerDismissed;
        ChangeKind = changeKind;
    }

    public string Resolution { get; }

    public string RefreshRate { get; }

    public VirtualDisplayPresentationMode PresentationMode { get; }

    public bool AutoManageEnabled { get; }

    public bool StatusBannerDismissed { get; }

    public StaticDisplaySettingsChangeKind ChangeKind { get; }
}

internal sealed class StaticDisplayAutostartChangedEventArgs : EventArgs
{
    public StaticDisplayAutostartChangedEventArgs(bool enabled)
    {
        Enabled = enabled;
    }

    public bool Enabled { get; }
}

internal sealed class StaticDisplayAutostartChangeResult
{
    public bool Success { get; init; }

    public bool IsEnabled { get; init; }

    public string StatusText { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

internal sealed class StaticDisplayPageState
{
    public string StatusText { get; init; } = string.Empty;

    public string StatusDetail { get; init; } = string.Empty;

    public Brush StatusBrush { get; init; } = new SolidColorBrush(Colors.Black);

    public Brush BannerBackground { get; init; } = new SolidColorBrush(Colors.White);

    public Brush BannerBorderBrush { get; init; } = new SolidColorBrush(Colors.Transparent);

    public string BannerIconGlyph { get; init; } = "\uE946";

    public Brush BannerIconBackground { get; init; } = new SolidColorBrush(Colors.Gray);

    public StaticDisplayBannerSeverity BannerSeverity { get; init; }

    public string DriverStatusText { get; init; } = string.Empty;

    public Brush DriverStatusBrush { get; init; } = new SolidColorBrush(Colors.Black);

    public string DeviceToolStatusText { get; init; } = string.Empty;

    public Brush DeviceToolStatusBrush { get; init; } = new SolidColorBrush(Colors.Black);

    public string SystemPermissionStatusText { get; init; } = string.Empty;

    public Brush SystemPermissionStatusBrush { get; init; } = new SolidColorBrush(Colors.Black);

    public string AutostartStatusText { get; init; } = "未管理";

    public bool AutostartEnabled { get; init; }

    public bool CanChangeAutostart { get; init; } = true;

    public bool CanStart { get; init; }

    public bool CanStop { get; init; }

    public bool CanInstallDriver { get; init; }

    public bool CanRefresh { get; init; } = true;

    public bool CanOpenDisplaySettings { get; init; } = true;

    public bool CanChangeDisplayOptions { get; init; }

    public bool VirtualDisplayRunning { get; init; }

    public DisplayLayoutSnapshot DisplayLayout { get; init; } = new(Array.Empty<DisplayLayoutMonitor>());

    public VirtualDisplayPresentationMode PresentationMode { get; init; } = VirtualDisplayPresentationMode.Unknown;

    public string? PresentationModeMessage { get; init; }

    public string Resolution { get; init; } = "1080p";

    public string RefreshRate { get; init; } = "120";

    public string FooterHostText { get; init; } = "本机：暂无数据";

    public string FooterOsText { get; init; } = "Windows";

    public string FooterNetworkText { get; init; } = "网络：暂无数据";

    public Brush FooterNetworkBrush { get; init; } = new SolidColorBrush(ColorHelper.FromArgb(255, 163, 170, 178));
}

internal enum StaticDisplayBannerSeverity
{
    Neutral,
    Ready,
    Warning,
    Error
}

internal enum StaticDisplayActivityKind
{
    Success,
    Info,
    Warning,
    Failure
}

internal enum StaticDisplaySettingsChangeKind
{
    Selection,
    AutoManage,
    Banner
}

internal enum StaticDisplayPresentationModePendingAction
{
    Keep,
    Restore
}

internal enum VirtualDisplayPresentationMode
{
    Unknown,
    Extend,
    Mirror,
    SecondaryOnly
}
