using System.Globalization;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace SideDock.Host.App;

public sealed partial class StaticDiagnosticsPage : UserControl
{
    private const int MaxDiagnosticsLogEntries = 1000;
    private const int MaxDiagnosticsEventEntries = 200;
    private static readonly TimeSpan MaxPerformanceTrendRange = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RecentDiagnosticsEventRange = TimeSpan.FromHours(24);
    private static readonly string[] DiagnosticsEventKeywords = new[]
    {
        "ERROR",
        "WARN",
        "Exception",
        "failed",
        "unauthorized",
        "offline",
        "timeout",
        "denied",
        "port",
        "reverse"
    };

    private readonly Brush _successBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 22, 138, 26));
    private readonly Brush _warningBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 215, 120, 0));
    private readonly Brush _errorBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 43, 28));
    private readonly Brush _mutedBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 102, 113, 125));
    private readonly Brush _bodyBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 48, 54, 61));
    private readonly Brush _softStrokeBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 231, 235, 239));
    private readonly Brush _cardBrush = new SolidColorBrush(Colors.White);
    private readonly Brush _primaryBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 8, 124, 137));
    private readonly Brush _cpuTrendBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 15, 163, 177));
    private readonly Brush _memoryTrendBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 46, 109, 235));
    private readonly Brush _networkTrendBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 74, 174, 82));
    private readonly Brush _networkBarBrush = new SolidColorBrush(ColorHelper.FromArgb(140, 130, 216, 150));
    private readonly Brush _successBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 242, 255, 240));
    private readonly Brush _warningBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 244, 229));
    private readonly Brush _errorBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 253, 242, 242));
    private readonly Brush _unknownBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 251, 252, 253));
    private readonly Brush _successBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 120, 189, 120));
    private readonly Brush _warningBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 240, 179, 90));
    private readonly Brush _errorBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 248, 113, 113));
    private readonly Brush _unknownBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 216, 222, 228));
    private readonly Brush _whiteBrush = new SolidColorBrush(Colors.White);
    private readonly FontFamily _logFontFamily = new("Consolas");
    private readonly List<DiagnosticsLogEntry> _logEntries = new();
    private readonly List<DiagnosticsEventEntry> _diagnosticEvents = new();
    private readonly List<DiagnosticsPerformanceSample> _performanceSamples = new();
    private IReadOnlyList<DiagnosticsPortState> _portStates = Array.Empty<DiagnosticsPortState>();

    private DiagnosticsLogSource _selectedLogSource = DiagnosticsLogSource.All;
    private DiagnosticsTrendRange _selectedTrendRange = DiagnosticsTrendRange.OneMinute;
    private DiagnosticsOperationKind? _busyOperation;
    private bool _isHostRunning;

    public StaticDiagnosticsPage()
    {
        InitializeComponent();
        UpdateLogFilterButtons();
        UpdateTrendRangeButtons();
        RenderLogRows();
        RenderPerformanceTrend();
        RenderPortSummaryRows();
        UpdateRecentErrorCard();
        UpdateState(new DiagnosticsPageState());
    }

    public event EventHandler? CopyAllRequested;

    public event EventHandler? ExportLogsRequested;

    public event EventHandler? ExportDiagnosticsPackageRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler? RecheckRequested;

    public event EventHandler? RefreshAdbDevicesRequested;

    public event EventHandler? ConfigureAdbReverseRequested;

    public event EventHandler? OpenLogDirectoryRequested;

    public event EventHandler? PortDetailsRequested;

    public void UpdateState(DiagnosticsPageState state)
    {
        var wasHostRunning = _isHostRunning;
        _isHostRunning = state.IsHostRunning;
        if (wasHostRunning && !_isHostRunning)
        {
            _performanceSamples.Clear();
            RenderPerformanceTrend();
        }

        SetOverallStatus(state.OverallStatus, state.OverallTitle, state.OverallDetail);
        SetStatusCard(HostStatusValueText, HostStatusDetailText, HostStatusDot, state.Host);
        SetStatusCard(AdbReverseStatusValueText, AdbReverseStatusDetailText, AdbReverseStatusDot, state.AdbReverse);
        SetStatusCard(PacketLossValueText, PacketLossDetailText, PacketLossStatusDot, state.PacketLoss);
        SetStatusCard(LatencyValueText, LatencyDetailText, LatencyStatusDot, state.Latency);

        SetHealthCheck(AndroidAuthDetailText, AndroidAuthStatusText, AndroidAuthStatusIcon, state.AndroidAuthorization);
        SetHealthCheck(PortListeningDetailText, PortListeningStatusText, PortListeningStatusIcon, state.PortListening);
        SetHealthCheck(AdbReverseHealthDetailText, AdbReverseHealthStatusText, AdbReverseHealthStatusIcon, state.AdbReverseHealth);
        SetHealthCheck(VirtualDisplayDetailText, VirtualDisplayStatusText, VirtualDisplayStatusIcon, state.VirtualDisplay);
        SetHealthCheck(VirtualCameraDetailText, VirtualCameraStatusText, VirtualCameraStatusIcon, state.VirtualCamera);
        SetHealthCheck(AudioEndpointDetailText, AudioEndpointStatusText, AudioEndpointStatusIcon, state.AudioEndpoint);
        _portStates = state.Ports.Count > 0 ? state.Ports : DiagnosticsPortState.DefaultPorts;
        RenderPortSummaryRows();
        UpdateLogEmptyState();
    }

    public void AppendLog(
        DiagnosticsLogSource source,
        string? message,
        DiagnosticsLogSeverity severity = DiagnosticsLogSeverity.Info,
        DateTimeOffset? timestamp = null,
        bool isHostPipe = false)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => AppendLog(source, message, severity, timestamp, isHostPipe));
            return;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (source == DiagnosticsLogSource.All)
        {
            source = DiagnosticsLogSource.Host;
        }

        var entry = new DiagnosticsLogEntry(
            timestamp ?? DateTimeOffset.Now,
            source,
            severity,
            message.TrimEnd(),
            isHostPipe);
        _logEntries.Add(entry);
        TrackDiagnosticEvent(entry);

        while (_logEntries.Count > MaxDiagnosticsLogEntries)
        {
            var removed = _logEntries[0];
            _logEntries.RemoveAt(0);
            if (MatchesSelectedLogSource(removed) && DiagnosticsLogPanel.Children.Count > 0)
            {
                DiagnosticsLogPanel.Children.RemoveAt(0);
            }
        }

        if (MatchesSelectedLogSource(entry))
        {
            DiagnosticsLogPanel.Children.Add(CreateLogRow(entry));
            ScrollLogToEndIfNeeded();
        }

        UpdateLogEmptyState();
    }

    public void AppendPerformanceSample(DiagnosticsPerformanceSample sample)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => AppendPerformanceSample(sample));
            return;
        }

        _isHostRunning = sample.IsHostRunning;
        if (!sample.IsHostRunning)
        {
            if (_performanceSamples.Count > 0)
            {
                _performanceSamples.Clear();
            }

            RenderPerformanceTrend();
            UpdateLogEmptyState();
            return;
        }

        _performanceSamples.Add(sample);
        var cutoff = sample.Timestamp - MaxPerformanceTrendRange;
        _performanceSamples.RemoveAll(existing => existing.Timestamp < cutoff);
        RenderPerformanceTrend();
        UpdateLogEmptyState();
    }

    private void RenderLogRows()
    {
        DiagnosticsLogPanel.Children.Clear();
        foreach (var entry in _logEntries.Where(MatchesSelectedLogSource))
        {
            DiagnosticsLogPanel.Children.Add(CreateLogRow(entry));
        }

        ScrollLogToEndIfNeeded();
        UpdateLogEmptyState();
    }

    private Grid CreateLogRow(DiagnosticsLogEntry entry)
    {
        var row = new Grid
        {
            MinHeight = 24,
            ColumnSpacing = 8
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(164) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddLogText(row, entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), 0, _bodyBrush);
        AddLogText(row, SeverityLabel(entry.Severity), 1, SeverityBrush(entry.Severity));
        AddLogText(row, LogSourceLabel(entry.Source), 2, _bodyBrush);
        AddLogText(row, entry.Message, 3, _bodyBrush, trim: true);

        return row;
    }

    private void AddLogText(Grid row, string text, int column, Brush foreground, bool trim = false)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontFamily = _logFontFamily,
            FontSize = 12,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None
        };
        Grid.SetColumn(textBlock, column);
        row.Children.Add(textBlock);
    }

    private void TrackDiagnosticEvent(DiagnosticsLogEntry entry)
    {
        if (!TryCreateDiagnosticEvent(entry, out var diagnosticEvent))
        {
            return;
        }

        _diagnosticEvents.Add(diagnosticEvent);
        while (_diagnosticEvents.Count > MaxDiagnosticsEventEntries)
        {
            _diagnosticEvents.RemoveAt(0);
        }

        UpdateRecentErrorCard();
    }

    private static bool TryCreateDiagnosticEvent(DiagnosticsLogEntry entry, out DiagnosticsEventEntry diagnosticEvent)
    {
        var keyword = FirstMatchingDiagnosticsKeyword(entry.Message);
        if (entry.Severity is not (DiagnosticsLogSeverity.Error or DiagnosticsLogSeverity.Warning)
            && keyword is null)
        {
            diagnosticEvent = default!;
            return false;
        }

        var severity = NormalizeEventSeverity(entry.Severity, entry.Message, keyword);
        diagnosticEvent = new DiagnosticsEventEntry(
            entry.Timestamp,
            entry.Source,
            severity,
            keyword ?? SeverityLabel(entry.Severity).Trim('[', ']'),
            entry.Message);
        return true;
    }

    private static DiagnosticsLogSeverity NormalizeEventSeverity(
        DiagnosticsLogSeverity severity,
        string message,
        string? keyword)
    {
        if (severity == DiagnosticsLogSeverity.Error
            || ContainsAny(message, "ERROR", "Exception", "failed", "failure", "denied", "timeout"))
        {
            return DiagnosticsLogSeverity.Error;
        }

        if (severity == DiagnosticsLogSeverity.Warning
            || ContainsAny(message, "WARN", "unauthorized", "offline"))
        {
            return DiagnosticsLogSeverity.Warning;
        }

        if (keyword is not null)
        {
            return DiagnosticsLogSeverity.Info;
        }

        return severity;
    }

    private static string? FirstMatchingDiagnosticsKeyword(string message)
    {
        return DiagnosticsEventKeywords.FirstOrDefault(keyword =>
            message.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAny(string message, params string[] values)
    {
        return values.Any(value => message.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateRecentErrorCard()
    {
        var cutoff = DateTimeOffset.Now - RecentDiagnosticsEventRange;
        var recentEvents = _diagnosticEvents
            .Where(entry => entry.Timestamp >= cutoff)
            .ToArray();

        if (recentEvents.Length == 0)
        {
            SetRecentErrorCard(
                DiagnosticsStatusKind.Normal,
                "无严重错误",
                "最近 24 小时内未检测到错误或警告事件。");
            return;
        }

        var errors = recentEvents.Count(entry => entry.Severity == DiagnosticsLogSeverity.Error);
        var warnings = recentEvents.Count(entry => entry.Severity == DiagnosticsLogSeverity.Warning);
        var latest = recentEvents.LastOrDefault(entry => entry.Severity != DiagnosticsLogSeverity.Info)
            ?? recentEvents[^1];
        var status = errors > 0
            ? DiagnosticsStatusKind.Error
            : warnings > 0
                ? DiagnosticsStatusKind.Warning
                : DiagnosticsStatusKind.Normal;
        var title = errors > 0
            ? $"{errors} 个错误"
            : warnings > 0
                ? $"{warnings} 个警告"
                : $"{recentEvents.Length} 条诊断事件";
        var detail = $"{latest.Timestamp:HH:mm:ss} · {LogSourceLabel(latest.Source)} · {latest.Message}";
        SetRecentErrorCard(status, title, detail);
    }

    private void SetRecentErrorCard(DiagnosticsStatusKind status, string title, string detail)
    {
        var brush = StatusBrush(status);
        RecentErrorIconBorder.BorderBrush = brush;
        RecentErrorIcon.Glyph = StatusGlyph(status);
        RecentErrorIcon.Foreground = brush;
        RecentErrorTitleText.Text = title;
        RecentErrorTitleText.Foreground = brush;
        RecentErrorDetailText.Text = detail;
    }

    private void RenderPortSummaryRows()
    {
        var ports = _portStates.Count > 0 ? _portStates : DiagnosticsPortState.DefaultPorts;
        SetPortSummaryRow(ports.ElementAtOrDefault(0), ControlPortValueText, ControlPortLocalText, ControlPortLocalDot, ControlPortReverseText, ControlPortReverseDot);
        SetPortSummaryRow(ports.ElementAtOrDefault(1), VideoPortValueText, VideoPortLocalText, VideoPortLocalDot, VideoPortReverseText, VideoPortReverseDot);
        SetPortSummaryRow(ports.ElementAtOrDefault(2), AudioPortValueText, AudioPortLocalText, AudioPortLocalDot, AudioPortReverseText, AudioPortReverseDot);
        SetPortSummaryRow(ports.ElementAtOrDefault(3), CameraPortValueText, CameraPortLocalText, CameraPortLocalDot, CameraPortReverseText, CameraPortReverseDot);
    }

    private void SetPortSummaryRow(
        DiagnosticsPortState? port,
        TextBlock portText,
        TextBlock localText,
        Ellipse localDot,
        TextBlock reverseText,
        Ellipse reverseDot)
    {
        if (port is null)
        {
            portText.Text = "--";
            localText.Text = "待检测";
            reverseText.Text = "待检测";
            localDot.Fill = _mutedBrush;
            reverseDot.Fill = _mutedBrush;
            return;
        }

        portText.Text = port.ConfiguredPort?.ToString(CultureInfo.InvariantCulture) ?? "--";
        localText.Text = ShortStatusText(port.LocalStatusText);
        reverseText.Text = ShortStatusText(port.ReverseStatusText);
        localText.Foreground = StatusBrush(port.LocalStatus);
        reverseText.Foreground = StatusBrush(port.ReverseStatus);
        localDot.Fill = StatusBrush(port.LocalStatus);
        reverseDot.Fill = StatusBrush(port.ReverseStatus);
    }

    private static string ShortStatusText(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "待检测" : value;
    }

    private bool MatchesSelectedLogSource(DiagnosticsLogEntry entry)
    {
        return _selectedLogSource == DiagnosticsLogSource.All
            || entry.Source == _selectedLogSource
            || (_selectedLogSource == DiagnosticsLogSource.Host && entry.IsHostPipe);
    }

    private void UpdateLogEmptyState()
    {
        var hasVisibleLogs = DiagnosticsLogPanel.Children.Count > 0;
        DiagnosticsLogEmptyText.Visibility = hasVisibleLogs ? Visibility.Collapsed : Visibility.Visible;
        if (hasVisibleLogs)
        {
            return;
        }

        if (_logEntries.Count == 0)
        {
            DiagnosticsLogEmptyText.Text = _isHostRunning
                ? "Host 已启动，等待实时日志输出。"
                : "Host 未启动，暂无实时日志。";
            return;
        }

        DiagnosticsLogEmptyText.Text = $"当前筛选暂无{LogSourceLabel(_selectedLogSource)}日志。";
    }

    private void ScrollLogToEndIfNeeded()
    {
        if (DiagnosticsAutoScrollSwitch.IsOn)
        {
            DiagnosticsLogScrollViewer.ChangeView(null, double.MaxValue, null, disableAnimation: true);
        }
    }

    private void UpdateLogFilterButtons()
    {
        UpdateSegmentButton(LogFilterAllButton, _selectedLogSource == DiagnosticsLogSource.All);
        UpdateSegmentButton(LogFilterHostButton, _selectedLogSource == DiagnosticsLogSource.Host);
        UpdateSegmentButton(LogFilterAdbButton, _selectedLogSource == DiagnosticsLogSource.Adb);
        UpdateSegmentButton(LogFilterCameraButton, _selectedLogSource == DiagnosticsLogSource.Camera);
        UpdateSegmentButton(LogFilterAudioButton, _selectedLogSource == DiagnosticsLogSource.Audio);
        UpdateSegmentButton(LogFilterDisplayButton, _selectedLogSource == DiagnosticsLogSource.Display);
    }

    private void UpdateTrendRangeButtons()
    {
        UpdateSegmentButton(TrendRangeOneMinuteButton, _selectedTrendRange == DiagnosticsTrendRange.OneMinute);
        UpdateSegmentButton(TrendRangeFiveMinutesButton, _selectedTrendRange == DiagnosticsTrendRange.FiveMinutes);
        UpdateSegmentButton(TrendRangeFifteenMinutesButton, _selectedTrendRange == DiagnosticsTrendRange.FifteenMinutes);
    }

    private void UpdateSegmentButton(Button button, bool selected)
    {
        button.Background = selected ? _primaryBrush : _cardBrush;
        button.BorderBrush = selected ? _primaryBrush : _unknownBorderBrush;
        button.BorderThickness = new Thickness(1);
        button.Foreground = selected ? _whiteBrush : _bodyBrush;

        if (button.Content is TextBlock textBlock)
        {
            textBlock.Foreground = selected ? _whiteBrush : _bodyBrush;
            textBlock.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void RenderPerformanceTrend()
    {
        UpdateTrendRangeButtons();
        DiagnosticsTrendCanvas.Children.Clear();

        var now = DateTimeOffset.Now;
        var range = SelectedTrendDuration();
        var start = now - range;
        var visibleSamples = _performanceSamples
            .Where(sample => sample.Timestamp >= start && sample.Timestamp <= now)
            .OrderBy(sample => sample.Timestamp)
            .ToArray();
        var hasData = _isHostRunning
            && visibleSamples.Any(sample =>
                sample.CpuPercent.HasValue
                || sample.MemoryBytes.HasValue
                || sample.NetworkMbps.HasValue);

        UpdateTrendLegend(visibleSamples.LastOrDefault());
        TrendStartTimeText.Text = hasData ? start.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "--:--:--";
        TrendEndTimeText.Text = hasData ? now.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "--:--:--";

        var width = Math.Max(1, DiagnosticsTrendCanvas.ActualWidth);
        var height = Math.Max(1, DiagnosticsTrendCanvas.ActualHeight);
        DrawTrendGrid(width, height);

        if (!hasData || width <= 1 || height <= 1)
        {
            DiagnosticsTrendEmptyText.Text = _isHostRunning
                ? "正在采样性能数据..."
                : "Host 未运行，暂无性能数据。";
            DiagnosticsTrendEmptyText.Visibility = Visibility.Visible;
            TrendRightTopLabel.Text = "1";
            TrendRightMiddleLabel.Text = "0.5";
            TrendRightBottomLabel.Text = "0";
            TrendScaleHintText.Text = "内存按当前范围工作集峰值缩放";
            return;
        }

        DiagnosticsTrendEmptyText.Visibility = Visibility.Collapsed;

        var memoryScaleBytes = Math.Max(
            256L * 1024L * 1024L,
            visibleSamples
                .Where(sample => sample.MemoryBytes.HasValue)
                .Select(sample => sample.MemoryBytes!.Value)
                .DefaultIfEmpty(0)
                .Max());
        var networkScaleMbps = NiceCeiling(visibleSamples
            .Where(sample => sample.NetworkMbps.HasValue)
            .Select(sample => sample.NetworkMbps!.Value)
            .DefaultIfEmpty(0)
            .Max());

        TrendRightTopLabel.Text = FormatAxisValue(networkScaleMbps);
        TrendRightMiddleLabel.Text = FormatAxisValue(networkScaleMbps / 2);
        TrendRightBottomLabel.Text = "0";
        TrendScaleHintText.Text = $"内存按峰值 {FormatByteSize(memoryScaleBytes)} 缩放";

        DrawNetworkBars(visibleSamples, start, now, width, height, networkScaleMbps);
        DrawTrendLine(visibleSamples, start, now, width, height, sample => sample.MemoryBytes, memoryScaleBytes, _memoryTrendBrush);
        DrawTrendLine(visibleSamples, start, now, width, height, sample => sample.CpuPercent, 100, _cpuTrendBrush);
    }

    private void DrawTrendGrid(double width, double height)
    {
        AddTrendGridLine(0, width);
        AddTrendGridLine(height / 2, width);
        AddTrendGridLine(Math.Max(0, height - 1), width);
    }

    private void AddTrendGridLine(double y, double width)
    {
        DiagnosticsTrendCanvas.Children.Add(new Line
        {
            X1 = 0,
            Y1 = y,
            X2 = width,
            Y2 = y,
            Stroke = _softStrokeBrush,
            StrokeThickness = 1
        });
    }

    private void DrawNetworkBars(
        IReadOnlyList<DiagnosticsPerformanceSample> samples,
        DateTimeOffset start,
        DateTimeOffset end,
        double width,
        double height,
        double scaleMax)
    {
        var networkSamples = samples.Where(sample => sample.NetworkMbps.HasValue).ToArray();
        if (networkSamples.Length == 0)
        {
            return;
        }

        var barWidth = Math.Clamp(width / Math.Max(30, networkSamples.Length * 1.8), 2, 7);
        foreach (var sample in networkSamples)
        {
            var ratio = Math.Clamp(sample.NetworkMbps!.Value / Math.Max(1, scaleMax), 0, 1);
            var barHeight = Math.Max(1, ratio * height);
            var x = ScaleTime(sample.Timestamp, start, end, width) - (barWidth / 2);
            var rectangle = new Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                Fill = _networkBarBrush,
                RadiusX = 1,
                RadiusY = 1
            };
            Canvas.SetLeft(rectangle, Math.Clamp(x, 0, Math.Max(0, width - barWidth)));
            Canvas.SetTop(rectangle, height - barHeight);
            DiagnosticsTrendCanvas.Children.Add(rectangle);
        }
    }

    private void DrawTrendLine(
        IReadOnlyList<DiagnosticsPerformanceSample> samples,
        DateTimeOffset start,
        DateTimeOffset end,
        double width,
        double height,
        Func<DiagnosticsPerformanceSample, double?> valueSelector,
        double scaleMax,
        Brush stroke)
    {
        var points = new PointCollection();
        foreach (var sample in samples)
        {
            var value = valueSelector(sample);
            if (!value.HasValue)
            {
                continue;
            }

            var ratio = Math.Clamp(value.Value / Math.Max(1, scaleMax), 0, 1);
            points.Add(new Point(
                ScaleTime(sample.Timestamp, start, end, width),
                height - (ratio * height)));
        }

        if (points.Count < 2)
        {
            return;
        }

        DiagnosticsTrendCanvas.Children.Add(new Polyline
        {
            Points = points,
            Stroke = stroke,
            StrokeThickness = 2
        });
    }

    private void UpdateTrendLegend(DiagnosticsPerformanceSample? latestSample)
    {
        TrendCpuValueText.Text = latestSample?.CpuPercent is { } cpuPercent
            ? $"CPU {cpuPercent:F0}%"
            : "CPU --";
        TrendMemoryValueText.Text = latestSample?.MemoryBytes is { } memoryBytes
            ? $"内存 {FormatByteSize(memoryBytes)}"
            : "内存 --";
        TrendNetworkValueText.Text = latestSample?.NetworkMbps is { } networkMbps
            ? $"网络 {networkMbps:F2} Mbps"
            : "网络 -- Mbps";
    }

    private TimeSpan SelectedTrendDuration()
    {
        return _selectedTrendRange switch
        {
            DiagnosticsTrendRange.FiveMinutes => TimeSpan.FromMinutes(5),
            DiagnosticsTrendRange.FifteenMinutes => TimeSpan.FromMinutes(15),
            _ => TimeSpan.FromMinutes(1)
        };
    }

    private static double ScaleTime(DateTimeOffset timestamp, DateTimeOffset start, DateTimeOffset end, double width)
    {
        var totalSeconds = Math.Max(1, (end - start).TotalSeconds);
        var elapsedSeconds = Math.Clamp((timestamp - start).TotalSeconds, 0, totalSeconds);
        return elapsedSeconds / totalSeconds * width;
    }

    private static double NiceCeiling(double value)
    {
        if (value <= 1)
        {
            return 1;
        }

        var exponent = Math.Floor(Math.Log10(value));
        var magnitude = Math.Pow(10, exponent);
        var normalized = value / magnitude;
        var ceiling = normalized <= 2
            ? 2
            : normalized <= 5
                ? 5
                : 10;
        return ceiling * magnitude;
    }

    private static string FormatAxisValue(double value)
    {
        if (value >= 10)
        {
            return value.ToString("F0", CultureInfo.InvariantCulture);
        }

        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB";
        }

        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / 1024.0 / 1024.0:F0} MB";
        }

        if (bytes >= 1024L)
        {
            return $"{bytes / 1024.0:F0} KB";
        }

        return $"{bytes} B";
    }

    public string BuildErrorSummary()
    {
        var events = _diagnosticEvents
            .OrderByDescending(entry => entry.Timestamp)
            .Take(80)
            .Reverse()
            .ToArray();

        if (events.Length == 0)
        {
            return "最近错误：暂无错误或警告事件。" + Environment.NewLine;
        }

        var report = new StringBuilder();
        report.AppendLine("最近错误 / 错误历史");
        report.AppendLine($"事件数量: {events.Length}");
        report.AppendLine();
        foreach (var entry in events)
        {
            report.AppendLine(FormatDiagnosticEventSummary(entry));
        }

        return report.ToString();
    }

    public string BuildPortDiagnosticsSummary()
    {
        var ports = _portStates.Count > 0 ? _portStates : DiagnosticsPortState.DefaultPorts;
        var report = new StringBuilder();
        report.AppendLine("完整端口信息");
        report.AppendLine($"生成时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine();
        foreach (var port in ports)
        {
            report.AppendLine($"{port.Module}");
            report.AppendLine($"  启用: {(port.IsEnabled ? "是" : "否")}");
            report.AppendLine($"  配置端口: {FormatPort(port.ConfiguredPort)}");
            report.AppendLine($"  实际本地端口: {FormatPort(port.ActualLocalPort)}");
            report.AppendLine($"  实际 reverse 端口: {FormatPort(port.ActualReversePort)}");
            report.AppendLine($"  本地监听: {port.LocalStatusText}");
            report.AppendLine($"  ADB reverse: {port.ReverseStatusText}");
            report.AppendLine($"  详情: {port.Detail}");
            report.AppendLine();
        }

        return report.ToString();
    }

    public async Task ShowErrorHistoryDialogAsync()
    {
        var filterCombo = new ComboBox
        {
            Width = 190,
            DisplayMemberPath = nameof(DiagnosticsSourceFilterItem.Label),
            ItemsSource = DiagnosticsSourceFilterItem.All
        };
        filterCombo.SelectedIndex = 0;

        var rowsPanel = new StackPanel { Spacing = 8 };
        var emptyText = new TextBlock
        {
            Text = "暂无错误历史。",
            Foreground = _mutedBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 28, 0, 28)
        };

        void RenderRows()
        {
            var selectedSource = filterCombo.SelectedItem is DiagnosticsSourceFilterItem item
                ? item.Source
                : DiagnosticsLogSource.All;
            var events = _diagnosticEvents
                .Where(entry => selectedSource == DiagnosticsLogSource.All || entry.Source == selectedSource)
                .OrderByDescending(entry => entry.Timestamp)
                .Take(120)
                .ToArray();

            rowsPanel.Children.Clear();
            emptyText.Visibility = events.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var entry in events)
            {
                rowsPanel.Children.Add(CreateDiagnosticEventRow(entry));
            }
        }

        filterCombo.SelectionChanged += (_, _) => RenderRows();
        RenderRows();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = $"{_diagnosticEvents.Count} 条诊断事件",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _bodyBrush
        });
        Grid.SetColumn(filterCombo, 1);
        header.Children.Add(filterCombo);

        var scrollViewer = new ScrollViewer
        {
            MaxHeight = 500,
            Content = new Grid
            {
                Children =
                {
                    rowsPanel,
                    emptyText
                }
            }
        };
        ScrollViewer.SetVerticalScrollBarVisibility(scrollViewer, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(scrollViewer, ScrollBarVisibility.Disabled);

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(header);
        panel.Children.Add(scrollViewer);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "错误历史",
            Content = panel,
            PrimaryButtonText = "复制全部摘要",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Resources["ContentDialogMaxWidth"] = 920.0;
        dialog.PrimaryButtonClick += (sender, args) =>
        {
            args.Cancel = true;
            CopyTextToClipboard(BuildErrorSummary());
            sender.PrimaryButtonText = "已复制";
        };

        await dialog.ShowAsync();
    }

    public async Task ShowPortDetailsDialogAsync()
    {
        var rowsPanel = new StackPanel { Spacing = 8 };
        foreach (var port in _portStates.Count > 0 ? _portStates : DiagnosticsPortState.DefaultPorts)
        {
            rowsPanel.Children.Add(CreatePortDetailsRow(port));
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        actions.Children.Add(CreateDialogActionButton("刷新 ADB 设备", "\uE8B7", () => RefreshAdbDevicesRequested?.Invoke(this, EventArgs.Empty)));
        actions.Children.Add(CreateDialogActionButton("重新配置 reverse", "\uE71B", () => ConfigureAdbReverseRequested?.Invoke(this, EventArgs.Empty)));
        actions.Children.Add(CreateDialogActionButton("重新检查", "\uE72C", () => RecheckRequested?.Invoke(this, EventArgs.Empty)));
        actions.Children.Add(CreateDialogActionButton("打开日志目录", "\uED25", () => OpenLogDirectoryRequested?.Invoke(this, EventArgs.Empty)));

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "端口详情会显示配置端口、实际监听、ADB reverse 映射和当前异常判断。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = _bodyBrush
        });
        panel.Children.Add(actions);
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 480,
            Content = rowsPanel
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "完整端口信息",
            Content = panel,
            PrimaryButtonText = "复制端口诊断",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Resources["ContentDialogMaxWidth"] = 920.0;
        dialog.PrimaryButtonClick += (sender, args) =>
        {
            args.Cancel = true;
            CopyTextToClipboard(BuildPortDiagnosticsSummary());
            sender.PrimaryButtonText = "已复制";
        };

        await dialog.ShowAsync();
    }

    public void SetOperationBusy(DiagnosticsOperationKind operation, bool isBusy)
    {
        _busyOperation = isBusy ? operation : null;
        var busy = _busyOperation.HasValue;

        CopyAllDiagnosticsButton.IsEnabled = !busy;
        ExportLogsButton.IsEnabled = !busy;
        ExportDiagnosticsPackageButton.IsEnabled = !busy;
        RefreshDiagnosticsButton.IsEnabled = !busy;
        RecheckDiagnosticsButton.IsEnabled = !busy;
        RefreshAdbDevicesButton.IsEnabled = !busy;
        ConfigureAdbReverseButton.IsEnabled = !busy;
        OpenLogDirectoryButton.IsEnabled = !busy;
        ViewFullPortInfoButton.IsEnabled = !busy;
        ViewErrorHistoryButton.IsEnabled = !busy;

        RefreshDiagnosticsButtonText.Text = operation == DiagnosticsOperationKind.Refresh && isBusy ? "刷新中" : "刷新";
        RecheckDiagnosticsButtonText.Text = operation == DiagnosticsOperationKind.Recheck && isBusy ? "检查中" : "重新检查";
        RefreshAdbDevicesButtonText.Text = operation == DiagnosticsOperationKind.RefreshAdb && isBusy ? "刷新中" : "刷新 ADB";
        ConfigureAdbReverseButtonText.Text = operation == DiagnosticsOperationKind.ConfigureReverse && isBusy ? "配置中" : "重配 reverse";
        ExportDiagnosticsPackageButtonText.Text = operation == DiagnosticsOperationKind.ExportPackage && isBusy ? "导出中" : "导出诊断包";
    }

    private Border CreateDiagnosticEventRow(DiagnosticsEventEntry entry)
    {
        var row = new Grid
        {
            ColumnSpacing = 10
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        AddDialogText(row, entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), 0, _bodyBrush, useLogFont: true);
        AddDialogText(row, SeverityLabel(entry.Severity), 1, SeverityBrush(entry.Severity), useLogFont: true);
        AddDialogText(row, LogSourceLabel(entry.Source), 2, _bodyBrush);
        AddDialogText(row, entry.Message, 3, _bodyBrush, trim: true);

        var copyButton = new Button
        {
            Content = "复制",
            MinWidth = 58,
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0)
        };
        copyButton.Click += (_, _) => CopyTextToClipboard(FormatDiagnosticEventDetails(entry));
        Grid.SetColumn(copyButton, 4);
        row.Children.Add(copyButton);

        return new Border
        {
            BorderBrush = _softStrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Background = _cardBrush,
            Child = row
        };
    }

    private Border CreatePortDetailsRow(DiagnosticsPortState port)
    {
        var row = new Grid
        {
            ColumnSpacing = 10
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddDialogText(row, port.Module, 0, _bodyBrush);
        AddDialogText(row, $"配置 {FormatPort(port.ConfiguredPort)}", 1, _bodyBrush);
        AddStatusPill(row, 2, port.LocalStatus, port.LocalStatusText);
        AddStatusPill(row, 3, port.ReverseStatus, port.ReverseStatusText);
        AddDialogText(row, port.Detail, 4, _bodyBrush, trim: true);

        return new Border
        {
            BorderBrush = StatusBorderBrush(port.OverallStatus),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Background = StatusBackgroundBrush(port.OverallStatus),
            Child = row
        };
    }

    private Button CreateDialogActionButton(string text, string glyph, Action action)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = _bodyBrush });
        content.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = _bodyBrush, VerticalAlignment = VerticalAlignment.Center });

        var button = new Button
        {
            Content = content,
            Height = 34,
            Padding = new Thickness(10, 0, 10, 0)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void AddDialogText(Grid row, string text, int column, Brush foreground, bool useLogFont = false, bool trim = false)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontFamily = useLogFont ? _logFontFamily : new FontFamily("Segoe UI"),
            FontSize = 12,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None
        };
        Grid.SetColumn(textBlock, column);
        row.Children.Add(textBlock);
    }

    private void AddStatusPill(Grid row, int column, DiagnosticsStatusKind status, string text)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        panel.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = StatusBrush(status),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = ShortStatusText(text),
            FontSize = 12,
            Foreground = StatusBrush(status),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(panel, column);
        row.Children.Add(panel);
    }

    private static string FormatDiagnosticEventSummary(DiagnosticsEventEntry entry)
    {
        return $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} {SeverityLabel(entry.Severity)} {LogSourceLabel(entry.Source)} keyword={entry.Keyword} {entry.Message}";
    }

    private static string FormatDiagnosticEventDetails(DiagnosticsEventEntry entry)
    {
        var report = new StringBuilder();
        report.AppendLine("SideDock 错误详情");
        report.AppendLine($"时间: {entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}");
        report.AppendLine($"级别: {SeverityLabel(entry.Severity)}");
        report.AppendLine($"模块: {LogSourceLabel(entry.Source)}");
        report.AppendLine($"关键字: {entry.Keyword}");
        report.AppendLine("日志:");
        report.AppendLine(entry.Message);
        return report.ToString();
    }

    private static string FormatPort(int? port)
    {
        return port.HasValue ? port.Value.ToString(CultureInfo.InvariantCulture) : "--";
    }

    private static void CopyTextToClipboard(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private void ClearDiagnosticsLogsButton_Click(object sender, RoutedEventArgs e)
    {
        _logEntries.Clear();
        RenderLogRows();
    }

    private void DiagnosticsAutoScrollSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        ScrollLogToEndIfNeeded();
    }

    private void LogFilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string sourceName }
            || !Enum.TryParse(sourceName, out DiagnosticsLogSource source))
        {
            return;
        }

        _selectedLogSource = source;
        UpdateLogFilterButtons();
        RenderLogRows();
    }

    private void TrendRangeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string rangeName }
            || !Enum.TryParse(rangeName, out DiagnosticsTrendRange range))
        {
            return;
        }

        _selectedTrendRange = range;
        RenderPerformanceTrend();
    }

    private void DiagnosticsTrendCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderPerformanceTrend();
    }

    private Brush SeverityBrush(DiagnosticsLogSeverity severity)
    {
        return severity switch
        {
            DiagnosticsLogSeverity.Warning => _warningBrush,
            DiagnosticsLogSeverity.Error => _errorBrush,
            DiagnosticsLogSeverity.Debug => _mutedBrush,
            _ => _successBrush
        };
    }

    private static string SeverityLabel(DiagnosticsLogSeverity severity)
    {
        return severity switch
        {
            DiagnosticsLogSeverity.Warning => "[WARN]",
            DiagnosticsLogSeverity.Error => "[ERROR]",
            DiagnosticsLogSeverity.Debug => "[DEBUG]",
            _ => "[INFO]"
        };
    }

    private static string LogSourceLabel(DiagnosticsLogSource source)
    {
        return source switch
        {
            DiagnosticsLogSource.Host => "Host",
            DiagnosticsLogSource.Adb => "ADB",
            DiagnosticsLogSource.Camera => "摄像头",
            DiagnosticsLogSource.Audio => "音频",
            DiagnosticsLogSource.Display => "显示器",
            _ => "全部"
        };
    }

    private void SetOverallStatus(DiagnosticsStatusKind status, string title, string detail)
    {
        OverallStatusBanner.Background = StatusBackgroundBrush(status);
        OverallStatusBanner.BorderBrush = StatusBorderBrush(status);
        OverallStatusIconBackground.Background = StatusBrush(status);
        OverallStatusIcon.Glyph = StatusGlyph(status);
        OverallStatusIcon.Foreground = _whiteBrush;
        OverallStatusTitleText.Text = string.IsNullOrWhiteSpace(title) ? DefaultOverallTitle(status) : title;
        OverallStatusTitleText.Foreground = StatusBrush(status);
        OverallStatusDetailText.Text = string.IsNullOrWhiteSpace(detail) ? DefaultOverallDetail(status) : detail;
        OverallStatusDetailText.Foreground = _bodyBrush;
    }

    private void SetStatusCard(TextBlock valueText, TextBlock detailText, Ellipse dot, DiagnosticsStatusCardState state)
    {
        valueText.Text = string.IsNullOrWhiteSpace(state.Value) ? "暂无数据" : state.Value;
        valueText.Foreground = StatusBrush(state.Status);
        detailText.Text = string.IsNullOrWhiteSpace(state.Detail) ? "等待检测" : state.Detail;
        detailText.Foreground = _bodyBrush;
        dot.Fill = StatusBrush(state.Status);
    }

    private void SetHealthCheck(
        TextBlock detailText,
        TextBlock statusText,
        FontIcon statusIcon,
        DiagnosticsHealthCheckState state)
    {
        detailText.Text = string.IsNullOrWhiteSpace(state.Detail) ? "等待检测" : state.Detail;
        detailText.Foreground = state.Status == DiagnosticsStatusKind.Unknown ? _mutedBrush : StatusBrush(state.Status);
        statusText.Text = string.IsNullOrWhiteSpace(state.StatusText) ? StatusLabel(state.Status) : state.StatusText;
        statusText.Foreground = StatusBrush(state.Status);
        statusIcon.Glyph = StatusGlyph(state.Status);
        statusIcon.Foreground = StatusBrush(state.Status);
    }

    private void CopyAllDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        CopyAllRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExportLogsButton_Click(object sender, RoutedEventArgs e)
    {
        ExportLogsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExportDiagnosticsPackageButton_Click(object sender, RoutedEventArgs e)
    {
        ExportDiagnosticsPackageRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RecheckDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        RecheckRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshAdbDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshAdbDevicesRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ConfigureAdbReverseButton_Click(object sender, RoutedEventArgs e)
    {
        ConfigureAdbReverseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenLogDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        OpenLogDirectoryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ViewFullPortInfoButton_Click(object sender, RoutedEventArgs e)
    {
        PortDetailsRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void ViewErrorHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowErrorHistoryDialogAsync();
    }

    private Brush StatusBrush(DiagnosticsStatusKind status)
    {
        return status switch
        {
            DiagnosticsStatusKind.Normal => _successBrush,
            DiagnosticsStatusKind.Warning => _warningBrush,
            DiagnosticsStatusKind.Error => _errorBrush,
            _ => _mutedBrush
        };
    }

    private Brush StatusBackgroundBrush(DiagnosticsStatusKind status)
    {
        return status switch
        {
            DiagnosticsStatusKind.Normal => _successBackgroundBrush,
            DiagnosticsStatusKind.Warning => _warningBackgroundBrush,
            DiagnosticsStatusKind.Error => _errorBackgroundBrush,
            _ => _unknownBackgroundBrush
        };
    }

    private Brush StatusBorderBrush(DiagnosticsStatusKind status)
    {
        return status switch
        {
            DiagnosticsStatusKind.Normal => _successBorderBrush,
            DiagnosticsStatusKind.Warning => _warningBorderBrush,
            DiagnosticsStatusKind.Error => _errorBorderBrush,
            _ => _unknownBorderBrush
        };
    }

    private static string StatusGlyph(DiagnosticsStatusKind status)
    {
        return status switch
        {
            DiagnosticsStatusKind.Normal => "\uE73E",
            DiagnosticsStatusKind.Warning => "\uE7BA",
            DiagnosticsStatusKind.Error => "\uE783",
            _ => "\uE711"
        };
    }

    private static string StatusLabel(DiagnosticsStatusKind status)
    {
        return status switch
        {
            DiagnosticsStatusKind.Normal => "正常",
            DiagnosticsStatusKind.Warning => "警告",
            DiagnosticsStatusKind.Error => "错误",
            _ => "暂无数据"
        };
    }

    private static string DefaultOverallTitle(DiagnosticsStatusKind status)
    {
        return status switch
        {
            DiagnosticsStatusKind.Normal => "运行诊断正常",
            DiagnosticsStatusKind.Warning => "运行诊断需要检查",
            DiagnosticsStatusKind.Error => "运行诊断发现错误",
            _ => "等待诊断数据"
        };
    }

    private static string DefaultOverallDetail(DiagnosticsStatusKind status)
    {
        return status switch
        {
            DiagnosticsStatusKind.Normal => "所有关键服务运行正常，系统性能良好。",
            DiagnosticsStatusKind.Warning => "部分项目需要确认，请查看下方健康检查。",
            DiagnosticsStatusKind.Error => "检测到影响 SideDock 运行的错误，请查看下方详情。",
            _ => "请点击刷新或启动主机后查看实时诊断。"
        };
    }
}

public enum DiagnosticsStatusKind
{
    Unknown,
    Normal,
    Warning,
    Error
}

public sealed class DiagnosticsPageState
{
    public bool IsHostRunning { get; init; }

    public DiagnosticsStatusKind OverallStatus { get; init; } = DiagnosticsStatusKind.Unknown;

    public string OverallTitle { get; init; } = string.Empty;

    public string OverallDetail { get; init; } = string.Empty;

    public DiagnosticsStatusCardState Host { get; init; } = new();

    public DiagnosticsStatusCardState AdbReverse { get; init; } = new();

    public DiagnosticsStatusCardState PacketLoss { get; init; } = new();

    public DiagnosticsStatusCardState Latency { get; init; } = new();

    public DiagnosticsHealthCheckState AndroidAuthorization { get; init; } = new();

    public DiagnosticsHealthCheckState PortListening { get; init; } = new();

    public DiagnosticsHealthCheckState AdbReverseHealth { get; init; } = new();

    public DiagnosticsHealthCheckState VirtualDisplay { get; init; } = new();

    public DiagnosticsHealthCheckState VirtualCamera { get; init; } = new();

    public DiagnosticsHealthCheckState AudioEndpoint { get; init; } = new();

    public IReadOnlyList<DiagnosticsPortState> Ports { get; init; } = DiagnosticsPortState.DefaultPorts;
}

public sealed class DiagnosticsStatusCardState
{
    public DiagnosticsStatusKind Status { get; init; } = DiagnosticsStatusKind.Unknown;

    public string Value { get; init; } = "暂无数据";

    public string Detail { get; init; } = "等待检测";
}

public sealed class DiagnosticsHealthCheckState
{
    public DiagnosticsStatusKind Status { get; init; } = DiagnosticsStatusKind.Unknown;

    public string StatusText { get; init; } = string.Empty;

    public string Detail { get; init; } = "等待检测";
}

public sealed class DiagnosticsPortState
{
    public static IReadOnlyList<DiagnosticsPortState> DefaultPorts { get; } = new[]
    {
        new DiagnosticsPortState { Module = "控制", ConfiguredPort = 27183, ActualLocalPort = 27183, ActualReversePort = 27183 },
        new DiagnosticsPortState { Module = "视频", ConfiguredPort = 27184, ActualLocalPort = 27184, ActualReversePort = 27184 },
        new DiagnosticsPortState { Module = "音频", ConfiguredPort = 27185, ActualLocalPort = 27185, ActualReversePort = 27185 },
        new DiagnosticsPortState { Module = "摄像头", ConfiguredPort = 27186, ActualLocalPort = 27186, ActualReversePort = 27186 }
    };

    public string Module { get; init; } = string.Empty;

    public bool IsEnabled { get; init; } = true;

    public int? ConfiguredPort { get; init; }

    public int? ActualLocalPort { get; init; }

    public int? ActualReversePort { get; init; }

    public DiagnosticsStatusKind LocalStatus { get; init; } = DiagnosticsStatusKind.Unknown;

    public string LocalStatusText { get; init; } = "待检测";

    public DiagnosticsStatusKind ReverseStatus { get; init; } = DiagnosticsStatusKind.Unknown;

    public string ReverseStatusText { get; init; } = "待检测";

    public DiagnosticsStatusKind OverallStatus { get; init; } = DiagnosticsStatusKind.Unknown;

    public string Detail { get; init; } = "等待检测";
}

public enum DiagnosticsLogSource
{
    All,
    Host,
    Adb,
    Camera,
    Audio,
    Display
}

public enum DiagnosticsLogSeverity
{
    Info,
    Warning,
    Error,
    Debug
}

public enum DiagnosticsOperationKind
{
    Refresh,
    Recheck,
    RefreshAdb,
    ConfigureReverse,
    ExportPackage
}

public sealed class DiagnosticsPerformanceSample
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public bool IsHostRunning { get; init; }

    public double? CpuPercent { get; init; }

    public long? MemoryBytes { get; init; }

    public double? NetworkMbps { get; init; }
}

internal enum DiagnosticsTrendRange
{
    OneMinute,
    FiveMinutes,
    FifteenMinutes
}

internal sealed record DiagnosticsLogEntry(
    DateTimeOffset Timestamp,
    DiagnosticsLogSource Source,
    DiagnosticsLogSeverity Severity,
    string Message,
    bool IsHostPipe);

internal sealed record DiagnosticsEventEntry(
    DateTimeOffset Timestamp,
    DiagnosticsLogSource Source,
    DiagnosticsLogSeverity Severity,
    string Keyword,
    string Message);

internal sealed record DiagnosticsSourceFilterItem(DiagnosticsLogSource Source, string Label)
{
    public static IReadOnlyList<DiagnosticsSourceFilterItem> All { get; } = new[]
    {
        new DiagnosticsSourceFilterItem(DiagnosticsLogSource.All, "全部"),
        new DiagnosticsSourceFilterItem(DiagnosticsLogSource.Host, "Host"),
        new DiagnosticsSourceFilterItem(DiagnosticsLogSource.Adb, "ADB"),
        new DiagnosticsSourceFilterItem(DiagnosticsLogSource.Camera, "摄像头"),
        new DiagnosticsSourceFilterItem(DiagnosticsLogSource.Audio, "音频"),
        new DiagnosticsSourceFilterItem(DiagnosticsLogSource.Display, "显示器")
    };
}
