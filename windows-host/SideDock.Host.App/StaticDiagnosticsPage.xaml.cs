using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace SideDock.Host.App;

public sealed partial class StaticDiagnosticsPage : UserControl
{
    private readonly Brush _successBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 22, 138, 26));
    private readonly Brush _warningBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 215, 120, 0));
    private readonly Brush _errorBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 43, 28));
    private readonly Brush _mutedBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 102, 113, 125));
    private readonly Brush _bodyBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 48, 54, 61));
    private readonly Brush _successBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 242, 255, 240));
    private readonly Brush _warningBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 244, 229));
    private readonly Brush _errorBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 253, 242, 242));
    private readonly Brush _unknownBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 251, 252, 253));
    private readonly Brush _successBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 120, 189, 120));
    private readonly Brush _warningBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 240, 179, 90));
    private readonly Brush _errorBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 248, 113, 113));
    private readonly Brush _unknownBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 216, 222, 228));
    private readonly Brush _whiteBrush = new SolidColorBrush(Colors.White);

    public StaticDiagnosticsPage()
    {
        InitializeComponent();
        UpdateState(new DiagnosticsPageState());
    }

    public event EventHandler? CopyAllRequested;

    public event EventHandler? ExportLogsRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler? RecheckRequested;

    public void UpdateState(DiagnosticsPageState state)
    {
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

    private void RefreshDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RecheckDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        RecheckRequested?.Invoke(this, EventArgs.Empty);
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
