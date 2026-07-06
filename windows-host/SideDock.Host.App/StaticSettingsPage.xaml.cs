using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SideDock.Host.App;

public sealed partial class StaticSettingsPage : UserControl
{
    public StaticSettingsPage()
    {
        InitializeComponent();
    }

    public void ScrollToTop()
    {
        SettingsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
    }

    public void SaveChanges()
    {
        ShowBanner("设置已保存", "所有更改已成功保存到本地配置。");
    }

    public void RestoreDefaults()
    {
        StartWithWindowsSwitch.IsOn = true;
        MinimizeToTraySwitch.IsOn = true;
        AutoDisplaySwitch.IsOn = true;
        AdbReverseSwitch.IsOn = true;
        DefaultDeviceCombo.SelectedIndex = 0;
        AdbPathBox.Text = @"C:\platform-tools\adb.exe";
        RestorePorts();
        RecentLogsSwitch.IsOn = true;
        IncludePortInfoSwitch.IsOn = true;
        Nv12ThreadsBox.Value = 4;
        EncoderQueueBox.Value = 2;
        LogSizeText.Text = "当前日志大小： 24.7 MB";
        UpdateStatusText.Text = "当前已是最新版本";
        ShowBanner("已恢复默认设置", "示例设置已恢复为推荐默认值。");
    }

    private void RestorePortsButton_Click(object sender, RoutedEventArgs e)
    {
        RestorePorts();
        ShowBanner("端口已恢复", "控制、视频、音频和摄像头端口已恢复为默认值。");
    }

    private void CleanLogsButton_Click(object sender, RoutedEventArgs e)
    {
        LogSizeText.Text = "当前日志大小： 0 KB";
        ShowBanner("日志已清理", "示例日志统计已重置。");
    }

    private void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "当前已是最新版本";
        ShowBanner("已完成检查", "SideDock Host v1.3.0 当前已是最新版本。");
    }

    private void DismissSavedBannerButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsSavedBanner.Visibility = Visibility.Collapsed;
    }

    private void RestorePorts()
    {
        ControlPortBox.Value = 27183;
        VideoPortBox.Value = 27184;
        AudioPortBox.Value = 27185;
        CameraPortBox.Value = 27186;
    }

    private void ShowBanner(string title, string detail)
    {
        SettingsSavedTitleText.Text = title;
        SettingsSavedDetailText.Text = detail;
        SettingsSavedBanner.Visibility = Visibility.Visible;
    }
}
