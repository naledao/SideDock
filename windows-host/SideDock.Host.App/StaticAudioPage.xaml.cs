using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SideDock.Host.App;

public sealed partial class StaticAudioPage : UserControl
{
    private const int VisibleLogEntries = 5;

    private readonly Brush _textBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 48, 54, 61));
    private readonly Brush _mutedBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 91, 101, 112));
    private readonly Brush _strokeBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 216, 222, 228));
    private readonly Brush _infoBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 11, 103, 227));
    private readonly Brush _successBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 18, 132, 86));
    private readonly Brush _warningBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 157, 93, 0));
    private readonly Brush _failureBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 43, 28));

    private bool _syncingState;
    private bool _statusBannerDismissed;

    public StaticAudioPage()
    {
        InitializeComponent();

        SpeakerEndpointCombo.DisplayMemberPath = "DisplayLabel";
        MicrophoneEndpointCombo.DisplayMemberPath = "DisplayLabel";
        UpdateRecentLogs(Array.Empty<string>());
    }

    public event EventHandler? RefreshRequested;

    public event EventHandler? InstallVirtualAudioCableRequested;

    public event EventHandler? CopyLogRequested;

    public event EventHandler? ShowLogsRequested;

    public event EventHandler? OpenSoundSettingsRequested;

    public event EventHandler? AutoBindEndpointsRequested;

    public event EventHandler? ApplyAudioChangesRequested;

    public event EventHandler<StaticAudioEndpointChangedEventArgs>? SpeakerEndpointChanged;

    public event EventHandler<StaticAudioEndpointChangedEventArgs>? MicrophoneEndpointChanged;

    public event EventHandler<StaticAudioSwitchChangedEventArgs>? SpeakerEnabledChanged;

    public event EventHandler<StaticAudioSwitchChangedEventArgs>? MicrophoneEnabledChanged;

    internal void UpdateState(StaticAudioPageState state)
    {
        if (state.BannerSeverity is StaticAudioBannerSeverity.Warning or StaticAudioBannerSeverity.Error)
        {
            _statusBannerDismissed = false;
        }

        AudioStatusBanner.Visibility = state.ShowBanner && !_statusBannerDismissed
            ? Visibility.Visible
            : Visibility.Collapsed;
        AudioStatusBanner.Background = state.BannerBackground;
        AudioStatusBanner.BorderBrush = state.BannerBorderBrush;
        AudioStatusBannerIconBackground.Background = state.BannerIconBackground;
        AudioStatusBannerIcon.Glyph = state.BannerIconGlyph;
        AudioStatusBannerTitleText.Text = state.BannerTitle;
        AudioStatusBannerTitleText.Foreground = state.BannerTextBrush;
        AudioStatusBannerMessageText.Text = state.BannerMessage;

        _syncingState = true;
        try
        {
            SpeakerOutputToggle.IsOn = state.SpeakerToggleEnabled;
            MicrophoneInputToggle.IsOn = state.MicrophoneToggleEnabled;
        }
        finally
        {
            _syncingState = false;
        }

        SpeakerOutputToggle.IsEnabled = state.CanChangeSwitches;
        MicrophoneInputToggle.IsEnabled = state.CanChangeSwitches;
        SpeakerToggleStatusText.Text = state.SpeakerToggleEnabled ? "已启用" : "已关闭";
        MicrophoneToggleStatusText.Text = state.MicrophoneToggleEnabled ? "已启用" : "已关闭";

        SpeakerTargetDeviceText.Text = state.CurrentDeviceText;
        MicrophoneTargetDeviceText.Text = state.CurrentDeviceText;
        SpeakerLevelText.Text = state.SpeakerLevelText;
        MicrophoneLevelText.Text = state.MicrophoneLevelText;
        SpeakerLevelBar.Value = Math.Clamp(state.SpeakerLevelPercent, 0, 100);
        MicrophoneLevelBar.Value = Math.Clamp(state.MicrophoneLevelPercent, 0, 100);
        SpeakerLatencyText.Text = state.SpeakerLatencyText;
        MicrophoneLatencyText.Text = state.MicrophoneLatencyText;
        SpeakerCardStatusText.Text = state.SpeakerStatusText;
        SpeakerCardStatusText.Foreground = state.SpeakerStatusBrush;
        MicrophoneCardStatusText.Text = state.MicrophoneStatusText;
        MicrophoneCardStatusText.Foreground = state.MicrophoneStatusBrush;
        SpeakerSummaryStatusText.Text = state.SpeakerStatusText;
        SpeakerSummaryStatusText.Foreground = state.SpeakerStatusBrush;
        MicrophoneSummaryStatusText.Text = state.MicrophoneStatusText;
        MicrophoneSummaryStatusText.Foreground = state.MicrophoneStatusBrush;
        SetStatusIcon(SpeakerCardStatusIcon, state.SpeakerStatusIconGlyph, state.SpeakerStatusBrush);
        SetStatusIcon(MicrophoneCardStatusIcon, state.MicrophoneStatusIconGlyph, state.MicrophoneStatusBrush);
        SetStatusIcon(SpeakerSummaryStatusIcon, state.SpeakerStatusIconGlyph, state.SpeakerStatusBrush);
        SetStatusIcon(MicrophoneSummaryStatusIcon, state.MicrophoneStatusIconGlyph, state.MicrophoneStatusBrush);

        SpeakerEndpointSummaryText.Text = state.SpeakerEndpointSummary;
        MicrophoneEndpointSummaryText.Text = state.MicrophoneEndpointSummary;
        SpeakerFormatText.Text = state.SpeakerFormatText;
        MicrophoneFormatText.Text = state.MicrophoneFormatText;
        SpeakerLevelSummaryText.Text = state.SpeakerLevelText;
        MicrophoneLevelSummaryText.Text = state.MicrophoneLevelText;
        VirtualCableStatusText.Text = state.VirtualCableStatusText;
        VirtualCableStatusText.Foreground = state.VirtualCableStatusBrush;
        VirtualCableVersionText.Text = state.VirtualCableVersionText;
        VirtualCableEndpointText.Text = state.VirtualCableEndpointText;
        SetStatusIcon(VirtualCableStatusIcon, state.VirtualCableStatusIconGlyph, state.VirtualCableStatusBrush);

        AudioInlineHintText.Text = state.HintText;
        AudioInlineHintText.Foreground = state.HintBrush;
        AudioPendingChangesPanel.Visibility = state.ShowPendingChanges ? Visibility.Visible : Visibility.Collapsed;
        AudioPendingChangesText.Text = state.PendingChangesText;
        WizardRepairHintText.Text = state.RepairHintText;

        RefreshEndpointsButton.IsEnabled = state.CanRefreshEndpoints;
        RefreshMicrophoneEndpointsButton.IsEnabled = state.CanRefreshEndpoints;
        RefreshMicrophoneRenderButton.IsEnabled = state.CanRefreshEndpoints;
        AutoBindEndpointsButton.IsEnabled = state.CanAutoBindEndpoints;
        ApplyAudioChangesButton.IsEnabled = state.CanApplyPendingChanges;
        RepairEndpointsButton.IsEnabled = state.CanInstallVirtualAudioCable;
        ReloadVirtualCableButton.IsEnabled = state.CanInstallVirtualAudioCable;
        WizardOpenSoundSettingsButton.IsEnabled = state.CanOpenSoundSettings;
        MicrophoneOpenSoundSettingsButton.IsEnabled = state.CanOpenSoundSettings;
        SpeakerOpenSoundSettingsButton.IsEnabled = state.CanOpenSoundSettings;

        SetWizardStep(WizardInstallStepIcon, state.InstallStepState);
        SetWizardStep(WizardSpeakerStepIcon, state.SpeakerStepState);
        SetWizardStep(WizardMicrophoneStepIcon, state.MicrophoneStepState);
        SetWizardStep(WizardTestStepIcon, StaticAudioStepState.Unavailable);
    }

    internal void UpdateEndpointChoices(
        object? speakerItemsSource,
        object? speakerSelectedItem,
        object? microphoneItemsSource,
        object? microphoneSelectedItem,
        string speakerStatus,
        Brush speakerStatusBrush,
        string microphoneStatus,
        Brush microphoneStatusBrush)
    {
        _syncingState = true;
        try
        {
            SpeakerEndpointCombo.ItemsSource = speakerItemsSource;
            SpeakerEndpointCombo.SelectedItem = speakerSelectedItem;
            MicrophoneEndpointCombo.ItemsSource = microphoneItemsSource;
            MicrophoneEndpointCombo.SelectedItem = microphoneSelectedItem;
        }
        finally
        {
            _syncingState = false;
        }

        SpeakerEndpointStatusText.Text = speakerStatus;
        SpeakerEndpointStatusText.Foreground = speakerStatusBrush;
        MicrophoneEndpointStatusText.Text = microphoneStatus;
        MicrophoneEndpointStatusText.Foreground = microphoneStatusBrush;
    }

    internal void UpdateRecentLogs(IReadOnlyList<string> logLines)
    {
        RecentAudioLogStackPanel.Children.Clear();

        var visibleLines = logLines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Reverse()
            .Take(VisibleLogEntries)
            .ToArray();
        if (visibleLines.Length == 0)
        {
            RecentAudioLogStackPanel.Children.Add(new TextBlock
            {
                Text = "暂无 AUDIO 日志",
                FontSize = 12,
                Foreground = _mutedBrush,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var line in visibleLines)
        {
            RecentAudioLogStackPanel.Children.Add(CreateLogRow(line));
        }
    }

    private UIElement CreateLogRow(string rawLine)
    {
        var (timeText, messageText) = SplitLogLine(rawLine);
        var kind = DetectLogKind(messageText);
        var (brush, glyph) = ActivityVisual(kind);

        var row = new Grid
        {
            ColumnSpacing = 8,
            MinHeight = 24
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = timeText,
            FontSize = 12,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        });

        var message = new TextBlock
        {
            Text = messageText,
            FontSize = 12,
            Foreground = _textBrush,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(message, 1);
        row.Children.Add(message);

        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 13,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 2);
        row.Children.Add(icon);

        return row;
    }

    private static (string TimeText, string MessageText) SplitLogLine(string rawLine)
    {
        var line = rawLine.Trim();
        var closingIndex = line.IndexOf(']');
        if (line.StartsWith("[", StringComparison.Ordinal) && closingIndex > 1)
        {
            var timeText = line[1..closingIndex];
            var dotIndex = timeText.IndexOf('.', StringComparison.Ordinal);
            if (dotIndex > 0)
            {
                timeText = timeText[..dotIndex];
            }

            return (timeText, line[(closingIndex + 1)..].Trim());
        }

        return ("--", line);
    }

    private static StaticAudioActivityKind DetectLogKind(string message)
    {
        if (message.Contains("error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("失败", StringComparison.OrdinalIgnoreCase)
            || message.Contains("错误", StringComparison.OrdinalIgnoreCase))
        {
            return StaticAudioActivityKind.Failure;
        }

        if (message.Contains("warn", StringComparison.OrdinalIgnoreCase)
            || message.Contains("missing", StringComparison.OrdinalIgnoreCase)
            || message.Contains("未", StringComparison.OrdinalIgnoreCase)
            || message.Contains("需要", StringComparison.OrdinalIgnoreCase))
        {
            return StaticAudioActivityKind.Warning;
        }

        if (message.Contains("ready", StringComparison.OrdinalIgnoreCase)
            || message.Contains("started", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connected", StringComparison.OrdinalIgnoreCase)
            || message.Contains("就绪", StringComparison.OrdinalIgnoreCase)
            || message.Contains("已启动", StringComparison.OrdinalIgnoreCase))
        {
            return StaticAudioActivityKind.Success;
        }

        return StaticAudioActivityKind.Info;
    }

    private (Brush Brush, string Glyph) ActivityVisual(StaticAudioActivityKind kind)
    {
        return kind switch
        {
            StaticAudioActivityKind.Success => (_successBrush, "\uE73E"),
            StaticAudioActivityKind.Warning => (_warningBrush, "\uE7BA"),
            StaticAudioActivityKind.Failure => (_failureBrush, "\uE783"),
            _ => (_infoBrush, "\uE946")
        };
    }

    private void SetWizardStep(FontIcon icon, StaticAudioStepState state)
    {
        var (brush, glyph) = state switch
        {
            StaticAudioStepState.Ready => (_successBrush, "\uE73E"),
            StaticAudioStepState.Warning => (_warningBrush, "\uE7BA"),
            StaticAudioStepState.Error => (_failureBrush, "\uE783"),
            StaticAudioStepState.Unavailable => (_mutedBrush, "\uE711"),
            _ => (_strokeBrush, "\uE10A")
        };

        icon.Glyph = glyph;
        icon.Foreground = brush;
    }

    private static void SetStatusIcon(FontIcon icon, string glyph, Brush brush)
    {
        icon.Glyph = glyph;
        icon.Foreground = brush;
    }

    private void SpeakerOutputToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingState)
        {
            return;
        }

        SpeakerEnabledChanged?.Invoke(
            this,
            new StaticAudioSwitchChangedEventArgs(SpeakerOutputToggle.IsOn, MicrophoneInputToggle.IsOn));
    }

    private void MicrophoneInputToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingState)
        {
            return;
        }

        MicrophoneEnabledChanged?.Invoke(
            this,
            new StaticAudioSwitchChangedEventArgs(SpeakerOutputToggle.IsOn, MicrophoneInputToggle.IsOn));
    }

    private void SpeakerEndpointCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingState)
        {
            return;
        }

        SpeakerEndpointChanged?.Invoke(this, new StaticAudioEndpointChangedEventArgs(SpeakerEndpointCombo.SelectedItem));
    }

    private void MicrophoneEndpointCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingState)
        {
            return;
        }

        MicrophoneEndpointChanged?.Invoke(this, new StaticAudioEndpointChangedEventArgs(MicrophoneEndpointCombo.SelectedItem));
    }

    private void RefreshEndpointsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void InstallVirtualAudioCableButton_Click(object sender, RoutedEventArgs e)
    {
        InstallVirtualAudioCableRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        CopyLogRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ShowLogsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowLogsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenSoundSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSoundSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void AutoBindEndpointsButton_Click(object sender, RoutedEventArgs e)
    {
        AutoBindEndpointsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyAudioChangesButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyAudioChangesRequested?.Invoke(this, EventArgs.Empty);
    }

    private void StatusBannerCloseButton_Click(object sender, RoutedEventArgs e)
    {
        _statusBannerDismissed = true;
        AudioStatusBanner.Visibility = Visibility.Collapsed;
    }
}

public sealed class StaticAudioEndpointChangedEventArgs : EventArgs
{
    public StaticAudioEndpointChangedEventArgs(object? selectedItem)
    {
        SelectedItem = selectedItem;
    }

    public object? SelectedItem { get; }
}

public sealed class StaticAudioSwitchChangedEventArgs : EventArgs
{
    public StaticAudioSwitchChangedEventArgs(bool speakerEnabled, bool microphoneEnabled)
    {
        SpeakerEnabled = speakerEnabled;
        MicrophoneEnabled = microphoneEnabled;
    }

    public bool SpeakerEnabled { get; }

    public bool MicrophoneEnabled { get; }
}

internal sealed class StaticAudioPageState
{
    public bool ShowBanner { get; init; } = true;

    public string BannerTitle { get; init; } = "音频状态";

    public string BannerMessage { get; init; } = "等待音频状态刷新。";

    public Brush BannerTextBrush { get; init; } = new SolidColorBrush(Colors.Black);

    public Brush BannerBackground { get; init; } = new SolidColorBrush(Colors.White);

    public Brush BannerBorderBrush { get; init; } = new SolidColorBrush(Colors.Transparent);

    public string BannerIconGlyph { get; init; } = "\uE946";

    public Brush BannerIconBackground { get; init; } = new SolidColorBrush(Colors.Gray);

    public StaticAudioBannerSeverity BannerSeverity { get; init; } = StaticAudioBannerSeverity.Neutral;

    public bool SpeakerToggleEnabled { get; init; }

    public bool MicrophoneToggleEnabled { get; init; }

    public bool CanChangeSwitches { get; init; } = true;

    public bool CanRefreshEndpoints { get; init; } = true;

    public bool CanInstallVirtualAudioCable { get; init; } = true;

    public bool CanOpenSoundSettings { get; init; } = true;

    public bool CanAutoBindEndpoints { get; init; } = true;

    public bool ShowPendingChanges { get; init; }

    public bool CanApplyPendingChanges { get; init; }

    public string PendingChangesText { get; init; } = "有未应用的音频更改。";

    public string CurrentDeviceText { get; init; } = "等待 Android 设备";

    public string HintText { get; init; } = "暂无数据。";

    public Brush HintBrush { get; init; } = new SolidColorBrush(ColorHelper.FromArgb(255, 91, 101, 112));

    public string SpeakerStatusText { get; init; } = "暂无数据";

    public Brush SpeakerStatusBrush { get; init; } = new SolidColorBrush(ColorHelper.FromArgb(255, 91, 101, 112));

    public string SpeakerStatusIconGlyph { get; init; } = "\uE946";

    public string MicrophoneStatusText { get; init; } = "暂无数据";

    public Brush MicrophoneStatusBrush { get; init; } = new SolidColorBrush(ColorHelper.FromArgb(255, 91, 101, 112));

    public string MicrophoneStatusIconGlyph { get; init; } = "\uE946";

    public string SpeakerEndpointSummary { get; init; } = "未绑定";

    public string MicrophoneEndpointSummary { get; init; } = "未绑定";

    public string SpeakerFormatText { get; init; } = "48 kHz, 16-bit, 2ch";

    public string MicrophoneFormatText { get; init; } = "48 kHz, 16-bit, 2ch";

    public string SpeakerLevelText { get; init; } = "暂无实时数据";

    public double SpeakerLevelPercent { get; init; }

    public string MicrophoneLevelText { get; init; } = "暂无实时数据";

    public double MicrophoneLevelPercent { get; init; }

    public string SpeakerLatencyText { get; init; } = "暂无数据";

    public string MicrophoneLatencyText { get; init; } = "暂无数据";

    public string VirtualCableStatusText { get; init; } = "暂无数据";

    public Brush VirtualCableStatusBrush { get; init; } = new SolidColorBrush(ColorHelper.FromArgb(255, 91, 101, 112));

    public string VirtualCableStatusIconGlyph { get; init; } = "\uE946";

    public string VirtualCableVersionText { get; init; } = "不可用";

    public string VirtualCableEndpointText { get; init; } = "暂无数据";

    public string RepairHintText { get; init; } = "修复端点会启动已有的虚拟音频线安装/修复流程。";

    public StaticAudioStepState InstallStepState { get; init; } = StaticAudioStepState.Neutral;

    public StaticAudioStepState SpeakerStepState { get; init; } = StaticAudioStepState.Neutral;

    public StaticAudioStepState MicrophoneStepState { get; init; } = StaticAudioStepState.Neutral;
}

internal enum StaticAudioBannerSeverity
{
    Neutral,
    Ready,
    Warning,
    Error
}

internal enum StaticAudioStepState
{
    Neutral,
    Ready,
    Warning,
    Error,
    Unavailable
}

internal enum StaticAudioActivityKind
{
    Success,
    Info,
    Warning,
    Failure
}
