using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SideDock.Host.App;

public sealed partial class StaticSettingsPage : UserControl
{
    public event EventHandler? BrowseAdbPathRequested;

    public StaticSettingsPage()
    {
        InitializeComponent();
    }

    public void ScrollToTop()
    {
        SettingsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
    }

    internal void ApplySettings(AppSettings settings)
    {
        StartWithWindowsSwitch.IsOn = settings.StartWithWindows;
        MinimizeToTraySwitch.IsOn = settings.MinimizeToTrayOnClose;
        AutoDisplaySwitch.IsOn = settings.StartVirtualDisplayWithHost;
        AdbReverseSwitch.IsOn = settings.ConfigureAdbReverseOnHostStart;
        SelectDefaultDevice(settings.DefaultAdbSerial);
        AdbPathBox.Text = settings.AdbPath;
        ControlPortBox.Value = settings.ControlPort;
        VideoPortBox.Value = settings.VideoPort;
        AudioPortBox.Value = settings.AudioPort;
        CameraPortBox.Value = settings.CameraPort;
        RecentLogsSwitch.IsOn = settings.RetainRecentLogs;
        IncludePortInfoSwitch.IsOn = settings.IncludePortInfoInDiagnostics;
        Nv12ThreadsBox.Value = settings.Nv12PoolSize;
        EncoderQueueBox.Value = settings.EncodedPacketQueue;
    }

    internal bool TryBuildSettings(out AppSettings settings)
    {
        settings = AppSettings.CreateDefault();

        if (!TryReadInt(ControlPortBox, "控制端口", 1, 65535, out var controlPort, out var error)
            || !TryReadInt(VideoPortBox, "视频端口", 1, 65535, out var videoPort, out error)
            || !TryReadInt(AudioPortBox, "音频端口", 1, 65535, out var audioPort, out error)
            || !TryReadInt(CameraPortBox, "摄像头端口", 1, 65535, out var cameraPort, out error)
            || !TryReadInt(Nv12ThreadsBox, "NV12 处理池", 1, 16, out var nv12PoolSize, out error)
            || !TryReadInt(EncoderQueueBox, "编码包队列", 1, 8, out var encodedPacketQueue, out error))
        {
            ShowBanner("设置未保存", error);
            return false;
        }

        var ports = new[] { controlPort, videoPort, audioPort, cameraPort };
        if (ports.Distinct().Count() != ports.Length)
        {
            ShowBanner("设置未保存", "控制、视频、音频和摄像头端口不能重复。");
            return false;
        }

        settings = new AppSettings
        {
            StartWithWindows = StartWithWindowsSwitch.IsOn,
            MinimizeToTrayOnClose = MinimizeToTraySwitch.IsOn,
            StartVirtualDisplayWithHost = AutoDisplaySwitch.IsOn,
            ConfigureAdbReverseOnHostStart = AdbReverseSwitch.IsOn,
            DefaultAdbSerial = SelectedDefaultAdbSerial(),
            AdbPath = AdbPathBox.Text.Trim(),
            ControlPort = controlPort,
            VideoPort = videoPort,
            AudioPort = audioPort,
            CameraPort = cameraPort,
            RetainRecentLogs = RecentLogsSwitch.IsOn,
            IncludePortInfoInDiagnostics = IncludePortInfoSwitch.IsOn,
            Nv12PoolSize = nv12PoolSize,
            EncodedPacketQueue = encodedPacketQueue
        };
        return true;
    }

    internal AppSettings RestoreDefaults()
    {
        var settings = AppSettings.CreateDefault();
        ApplySettings(settings);
        return settings;
    }

    public void SetAdbPath(string path)
    {
        AdbPathBox.Text = path;
    }

    public void ShowSettingsSaved()
    {
        ShowBanner("设置已保存", "已保存到本地配置，已接入的启动参数会在后续启动主机时生效。");
    }

    public void ShowDefaultsRestored()
    {
        ShowBanner("已恢复默认设置", "默认设置已保存到本地配置。");
    }

    public void ShowSaveFailed(string detail)
    {
        ShowBanner("设置未保存", detail);
    }

    private void RestorePortsButton_Click(object sender, RoutedEventArgs e)
    {
        RestorePorts();
        ShowBanner("端口已恢复", "控制、视频、音频和摄像头端口已恢复为默认值。");
    }

    private void CleanLogsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowBanner("清理日志暂未接入", "日志清理会在后续阶段接入真实文件清理。");
    }

    private void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "检查更新暂未接入";
        ShowBanner("检查更新暂未接入", "更新检查会在后续阶段接入真实更新源。");
    }

    private void DismissSavedBannerButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsSavedBanner.Visibility = Visibility.Collapsed;
    }

    private void AdbPathBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseAdbPathRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RestorePorts()
    {
        ControlPortBox.Value = AppSettings.DefaultControlPort;
        VideoPortBox.Value = AppSettings.DefaultVideoPort;
        AudioPortBox.Value = AppSettings.DefaultAudioPort;
        CameraPortBox.Value = AppSettings.DefaultCameraPort;
    }

    private void SelectDefaultDevice(string? serial)
    {
        var expected = string.IsNullOrWhiteSpace(serial) ? "Android Debug Bridge" : serial.Trim();
        foreach (var item in DefaultDeviceCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(ItemText(item), expected, StringComparison.OrdinalIgnoreCase))
            {
                DefaultDeviceCombo.SelectedItem = item;
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(serial))
        {
            var item = new ComboBoxItem { Content = serial.Trim() };
            DefaultDeviceCombo.Items.Insert(0, item);
            DefaultDeviceCombo.SelectedItem = item;
            return;
        }

        DefaultDeviceCombo.SelectedIndex = Math.Max(0, DefaultDeviceCombo.Items.Count - 1);
    }

    private string? SelectedDefaultAdbSerial()
    {
        var text = ItemText(DefaultDeviceCombo.SelectedItem).Trim();
        if (string.IsNullOrWhiteSpace(text)
            || text.Equals("Android Debug Bridge", StringComparison.OrdinalIgnoreCase)
            || text.Contains(' '))
        {
            return null;
        }

        return text;
    }

    private static string ItemText(object? item)
    {
        return item switch
        {
            ComboBoxItem comboBoxItem => comboBoxItem.Content?.ToString() ?? string.Empty,
            null => string.Empty,
            _ => item.ToString() ?? string.Empty
        };
    }

    private static bool TryReadInt(
        NumberBox numberBox,
        string label,
        int min,
        int max,
        out int value,
        out string error)
    {
        value = 0;
        error = string.Empty;

        var raw = numberBox.Value;
        if (double.IsNaN(raw) || double.IsInfinity(raw) || Math.Abs(raw - Math.Round(raw)) > 0.0001)
        {
            error = $"{label}必须是整数。";
            return false;
        }

        value = (int)Math.Round(raw);
        if (value < min || value > max)
        {
            error = $"{label}必须在 {min}-{max} 之间。";
            return false;
        }

        return true;
    }

    private void ShowBanner(string title, string detail)
    {
        SettingsSavedTitleText.Text = title;
        SettingsSavedDetailText.Text = detail;
        SettingsSavedBanner.Visibility = Visibility.Visible;
    }
}
