using System.Diagnostics;
using Microsoft.Win32;

namespace SideDock.Host.App;

internal static class StartupTaskService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "SideDockHostApp";

    public static StartupTaskState GetState()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var command = key?.GetValue(EntryName) as string;
            return StartupTaskState.Success(!string.IsNullOrWhiteSpace(command), command);
        }
        catch (Exception ex)
        {
            return StartupTaskState.Failed(ex.Message);
        }
    }

    public static StartupTaskState SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                    ?? throw new InvalidOperationException("Unable to open current-user Run registry key.");
                var command = BuildStartupCommand();
                key.SetValue(EntryName, command, RegistryValueKind.String);
                return StartupTaskState.Success(isEnabled: true, command);
            }

            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            {
                key?.DeleteValue(EntryName, throwOnMissingValue: false);
            }

            return StartupTaskState.Success(isEnabled: false, command: null);
        }
        catch (Exception ex)
        {
            return StartupTaskState.Failed(ex.Message);
        }
    }

    private static string BuildStartupCommand()
    {
        return $"{Quote(GetExecutablePath())} --minimized";
    }

    private static string GetExecutablePath()
    {
        return Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Unable to resolve current executable path.");
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}

internal sealed record StartupTaskState(
    bool Succeeded,
    bool IsEnabled,
    string? Command,
    string? Error)
{
    public static StartupTaskState Success(bool isEnabled, string? command)
    {
        return new StartupTaskState(true, isEnabled, command, null);
    }

    public static StartupTaskState Failed(string error)
    {
        return new StartupTaskState(false, false, null, error);
    }
}
