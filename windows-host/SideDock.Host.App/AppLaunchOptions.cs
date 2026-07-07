namespace SideDock.Host.App;

public sealed record AppLaunchOptions(bool StartMinimized, bool StartInTray)
{
    public bool StartQuietly => StartMinimized || StartInTray;

    public static AppLaunchOptions FromLaunchArguments(string? launchArguments)
    {
        var rawArguments = string.Join(' ', Environment.GetCommandLineArgs().Skip(1));
        if (!string.IsNullOrWhiteSpace(launchArguments))
        {
            rawArguments = string.IsNullOrWhiteSpace(rawArguments)
                ? launchArguments
                : $"{rawArguments} {launchArguments}";
        }

        return new AppLaunchOptions(
            ContainsSwitch(rawArguments, "--minimized"),
            ContainsSwitch(rawArguments, "--tray"));
    }

    private static bool ContainsSwitch(string arguments, string name)
    {
        return arguments.Contains(name, StringComparison.OrdinalIgnoreCase);
    }
}
