using Microsoft.UI.Xaml;

namespace SideDock.Host.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var launchOptions = AppLaunchOptions.FromLaunchArguments(args.Arguments);
        _window = new MainWindow(launchOptions);
        _window.Activate();
        _window.ApplyLaunchOptions();
    }
}
