using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

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
    private bool _displayOptionsEnabled = true;
    private bool _statusBannerDismissed;
    private string? _lastLoggedStatus;
    private DisplayLayoutSnapshot _displayLayoutSnapshot = new(Array.Empty<DisplayLayoutMonitor>());
    private bool _virtualDisplayRunning;

    public StaticDisplayPage()
    {
        InitializeComponent();
        AddActivityLog("等待检测虚拟显示器状态。", StaticDisplayActivityKind.Info);
        SetSelectedDisplayOptions(_selectedResolution, _selectedRefreshRate);
    }

    public event EventHandler? StartRequested;

    public event EventHandler? StopRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler? InstallDriverRequested;

    public event EventHandler? OpenDisplaySettingsRequested;

    public event EventHandler? ShowLogsRequested;

    public event EventHandler<StaticDisplayOptionChangedEventArgs>? ResolutionChanged;

    public event EventHandler<StaticDisplayOptionChangedEventArgs>? RefreshRateChanged;

    internal void UpdateVirtualDisplayState(StaticDisplayPageState state)
    {
        if (state.BannerSeverity is StaticDisplayBannerSeverity.Warning or StaticDisplayBannerSeverity.Error)
        {
            _statusBannerDismissed = false;
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
        AutostartStatusText.Text = state.AutostartStatusText;

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
    }

    private void ResolutionOption_Tapped(object sender, TappedRoutedEventArgs e)
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
        AddActivityLog($"分辨率已选择为 {ResolutionLabel(normalized)}。", StaticDisplayActivityKind.Info);
        ResolutionChanged?.Invoke(this, new StaticDisplayOptionChangedEventArgs(normalized));
    }

    private void RefreshRateOption_Tapped(object sender, TappedRoutedEventArgs e)
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
        AddActivityLog($"刷新率已选择为 {normalized} Hz。", StaticDisplayActivityKind.Info);
        RefreshRateChanged?.Invoke(this, new StaticDisplayOptionChangedEventArgs(normalized));
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
        _displayOptionsEnabled = enabled;
        var opacity = enabled ? 1 : 0.56;
        Resolution720pOption.Opacity = opacity;
        Resolution1080pOption.Opacity = opacity;
        Resolution2kOption.Opacity = opacity;
        RefreshRate30Option.Opacity = opacity;
        RefreshRate60Option.Opacity = opacity;
        RefreshRate120Option.Opacity = opacity;
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

    private static string NormalizeResolution(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "720" or "720p" => "720p",
            "2k" or "1440p" => "2k",
            _ => "1080p"
        };
    }

    private static string NormalizeRefreshRate(string? value)
    {
        return (value ?? string.Empty).Trim() switch
        {
            "30" or "30 Hz" => "30",
            "60" or "60 Hz" => "60",
            _ => "120"
        };
    }

    private static string ResolutionLabel(string resolution)
    {
        return NormalizeResolution(resolution) == "2k"
            ? "2K"
            : NormalizeResolution(resolution);
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

public sealed class StaticDisplayOptionChangedEventArgs : EventArgs
{
    public StaticDisplayOptionChangedEventArgs(string value)
    {
        Value = value;
    }

    public string Value { get; }
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

    public bool CanStart { get; init; }

    public bool CanStop { get; init; }

    public bool CanInstallDriver { get; init; }

    public bool CanRefresh { get; init; } = true;

    public bool CanOpenDisplaySettings { get; init; } = true;

    public bool CanChangeDisplayOptions { get; init; }

    public bool VirtualDisplayRunning { get; init; }

    public DisplayLayoutSnapshot DisplayLayout { get; init; } = new(Array.Empty<DisplayLayoutMonitor>());

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
