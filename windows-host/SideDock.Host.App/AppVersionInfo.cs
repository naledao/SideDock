using System.Diagnostics;
using System.Reflection;

namespace SideDock.Host.App;

internal static class AppVersionInfo
{
    public static string CurrentVersion { get; } = ResolveVersion();

    public static string DisplayVersion { get; } = $"v{CurrentVersion}";

    private static string ResolveVersion()
    {
        var assembly = typeof(AppVersionInfo).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return StripBuildMetadata(informationalVersion);
        }

        var fileVersion = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
        if (!string.IsNullOrWhiteSpace(fileVersion))
        {
            return TrimVersion(fileVersion);
        }

        return TrimVersion(assembly.GetName().Version?.ToString() ?? "0.0.0");
    }

    private static string StripBuildMetadata(string value)
    {
        var trimmed = value.Trim();
        var metadataIndex = trimmed.IndexOf('+', StringComparison.Ordinal);
        return metadataIndex > 0 ? trimmed[..metadataIndex] : trimmed;
    }

    private static string TrimVersion(string value)
    {
        var trimmed = StripBuildMetadata(value);
        return trimmed.EndsWith(".0", StringComparison.Ordinal)
            ? trimmed[..^2]
            : trimmed;
    }
}
