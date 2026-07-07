using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SideDock.Host.App;

public sealed partial class StaticSettingsPage : UserControl
{
    public event EventHandler? BrowseAdbPathRequested;
    internal event EventHandler<AppearanceSettingsChangedEventArgs>? AppearanceChanged;

    private bool _syncingAppearanceOptions;

    public StaticSettingsPage()
    {
        InitializeComponent();
        SettingsAboutVersionText.Text = $"SideDock Host  {AppVersionInfo.DisplayVersion}";
        Loaded += StaticSettingsPage_Loaded;
    }

    public void ScrollToTop()
    {
        SettingsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
    }

    internal void ApplySettings(AppSettings settings)
    {
        _syncingAppearanceOptions = true;
        try
        {
            SelectThemeMode(settings.ThemeMode);
            SelectInterfaceDensity(settings.InterfaceDensity);
        }
        finally
        {
            _syncingAppearanceOptions = false;
        }

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

    internal void ApplyAppearance(AppAppearancePalette palette, AppInterfaceDensity density)
    {
        RequestedTheme = palette.Theme;
        AppAppearance.ApplyPageResources(Resources, palette);
        AppAppearance.ApplyPalette(this, palette);
        AppAppearance.ApplyDensity(this, density);
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
            EncodedPacketQueue = encodedPacketQueue,
            ThemeMode = SelectedThemeMode(),
            InterfaceDensity = SelectedInterfaceDensity()
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

    internal async Task RefreshLogSizeAsync()
    {
        LogSizeText.Text = "当前日志大小：正在计算...";
        var result = await Task.Run(SideDockLogMaintenance.CalculateSize);
        UpdateLogSizeText(result);
    }

    private async void StaticSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshLogSizeAsync();
    }

    private void RestorePortsButton_Click(object sender, RoutedEventArgs e)
    {
        RestorePorts();
        ShowBanner("端口已恢复", "控制、视频、音频和摄像头端口已恢复为默认值。");
    }

    private async void CleanLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.IsEnabled = false;
        }

        try
        {
            LogSizeText.Text = "当前日志大小：正在清理...";
            var result = await Task.Run(() => SideDockLogMaintenance.Clean(RecentLogsSwitch.IsOn));
            UpdateLogSizeText(result.After);
            ShowLogCleanupResult(result);
        }
        catch (Exception ex)
        {
            ShowBanner("清理日志失败", $"日志清理未完成：{ex.Message}");
            await RefreshLogSizeAsync();
        }
        finally
        {
            if (sender is Button cleanupButton)
            {
                cleanupButton.IsEnabled = true;
            }
        }
    }

    private void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "未配置更新源";
        ShowBanner("未配置更新源", "当前没有配置 GitHub Releases、更新 manifest 或其它真实发布源，因此不会执行假的更新检查。");
    }

    private void AppearanceOption_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingAppearanceOptions)
        {
            return;
        }

        AppearanceChanged?.Invoke(
            this,
            new AppearanceSettingsChangedEventArgs(SelectedThemeMode(), SelectedInterfaceDensity()));
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

    private void SelectThemeMode(AppThemeMode themeMode)
    {
        LightThemeRadio.IsChecked = themeMode == AppThemeMode.Light;
        DarkThemeRadio.IsChecked = themeMode == AppThemeMode.Dark;
        SystemThemeRadio.IsChecked = themeMode == AppThemeMode.System;
    }

    private AppThemeMode SelectedThemeMode()
    {
        if (LightThemeRadio.IsChecked == true)
        {
            return AppThemeMode.Light;
        }

        if (DarkThemeRadio.IsChecked == true)
        {
            return AppThemeMode.Dark;
        }

        return AppThemeMode.System;
    }

    private void SelectInterfaceDensity(AppInterfaceDensity density)
    {
        StandardDensityRadio.IsChecked = density == AppInterfaceDensity.Standard;
        CompactDensityRadio.IsChecked = density == AppInterfaceDensity.Compact;
    }

    private AppInterfaceDensity SelectedInterfaceDensity()
    {
        return CompactDensityRadio.IsChecked == true
            ? AppInterfaceDensity.Compact
            : AppInterfaceDensity.Standard;
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

    private void UpdateLogSizeText(LogScanResult result)
    {
        var suffix = result switch
        {
            { FileCount: 0, ExistingLocationCount: 0 } => "（日志目录不存在）",
            { FileCount: 0, MissingLocationCount: > 0 } => "（日志目录不存在或暂无日志文件）",
            { FileCount: 0 } => "（暂无日志文件）",
            { HasWarnings: true } => $"（{result.FileCount} 个文件，部分目录不可访问）",
            _ => $"（{result.FileCount} 个文件）"
        };

        LogSizeText.Text = $"当前日志大小：{FormatFileSize(result.TotalBytes)} {suffix}";
    }

    private void ShowLogCleanupResult(LogCleanupResult result)
    {
        if (result.Before.FileCount == 0
            && (result.Before.ExistingLocationCount == 0 || result.Before.MissingLocationCount > 0))
        {
            ShowBanner("没有可清理的日志", "日志目录不存在或尚未生成日志文件，启动或导出日志后这里会自动显示真实大小。");
            return;
        }

        if (result.Before.FileCount == 0)
        {
            var emptyWarning = FormatWarningSummary(result.Warnings);
            ShowBanner(
                result.HasWarnings ? "日志目录检查完成" : "没有可清理的日志",
                string.IsNullOrWhiteSpace(emptyWarning)
                    ? "未发现可清理的日志文件。"
                    : $"未发现可清理的日志文件。{emptyWarning}");
            return;
        }

        var title = result.HasWarnings ? "日志已部分清理" : "日志已清理";
        var detail = $"已删除 {result.DeletedFiles} 个日志文件（{FormatFileSize(result.DeletedBytes)}），"
            + $"剩余 {result.After.FileCount} 个文件（{FormatFileSize(result.After.TotalBytes)}）。";

        if (!string.IsNullOrWhiteSpace(result.RetentionDescription))
        {
            detail += result.RetentionDescription;
        }

        if (result.SkippedFiles > 0)
        {
            detail += $"跳过 {result.SkippedFiles} 个正在使用或不可访问的日志文件。";
        }

        var cleanupWarning = FormatWarningSummary(result.Warnings);
        if (!string.IsNullOrWhiteSpace(cleanupWarning))
        {
            detail += cleanupWarning;
        }

        ShowBanner(title, detail);
    }

    private static string FormatWarningSummary(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return string.Empty;
        }

        var preview = string.Join("；", warnings.Take(2));
        if (warnings.Count > 2)
        {
            preview += $"；另有 {warnings.Count - 2} 项。";
        }

        return $"提示：{preview}";
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024L * 1024L)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        if (bytes < 1024L * 1024L * 1024L)
        {
            return $"{bytes / 1024.0 / 1024.0:F1} MB";
        }

        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB";
    }

    private void ShowBanner(string title, string detail)
    {
        SettingsSavedTitleText.Text = title;
        SettingsSavedDetailText.Text = detail;
        SettingsSavedBanner.Visibility = Visibility.Visible;
    }
}

internal sealed class AppearanceSettingsChangedEventArgs : EventArgs
{
    public AppearanceSettingsChangedEventArgs(AppThemeMode themeMode, AppInterfaceDensity interfaceDensity)
    {
        ThemeMode = themeMode;
        InterfaceDensity = interfaceDensity;
    }

    public AppThemeMode ThemeMode { get; }

    public AppInterfaceDensity InterfaceDensity { get; }
}
