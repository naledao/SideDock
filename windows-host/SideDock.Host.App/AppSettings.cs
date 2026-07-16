using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideDock.Host.App;

internal enum AppThemeMode
{
    System,
    Light,
    Dark
}

internal enum AppConnectionMode
{
    Usb,
    Lan
}

internal enum AppInterfaceDensity
{
    Standard,
    Compact
}

internal enum AppUpdateSourceKind
{
    None,
    GitHubReleases,
    Manifest
}

internal enum AppReleaseChannel
{
    Stable,
    Preview
}

internal sealed class AppSettings
{
    public const int DefaultControlPort = 27183;
    public const int DefaultVideoPort = 27184;
    public const int DefaultAudioPort = 27185;
    public const int DefaultCameraPort = 27186;
    public const int DefaultNv12PoolSize = 4;
    public const int DefaultEncodedPacketQueue = 2;
    public const int DefaultAndroidCursorScalePercent = 100;
    public const string DefaultAdbSerialValue = "HA1K3AX0";
    public const string DefaultUpdateGitHubRepository = "naledao/SideDock";

    public bool StartWithWindows { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool StartVirtualDisplayWithHost { get; set; } = true;
    public bool ConfigureAdbReverseOnHostStart { get; set; } = true;
    public AppConnectionMode ConnectionMode { get; set; } = AppConnectionMode.Usb;
    public string VirtualDisplayResolution { get; set; } = "1080p";
    public string VirtualDisplayRefreshRate { get; set; } = "120";
    public VirtualDisplayPresentationMode VirtualDisplayPresentationMode { get; set; } = global::SideDock.Host.App.VirtualDisplayPresentationMode.Extend;
    public bool StaticDisplayStatusBannerDismissed { get; set; }
    public string LastAppliedVirtualDisplayResolution { get; set; } = "1080p";
    public string LastAppliedVirtualDisplayRefreshRate { get; set; } = "120";
    public VirtualDisplayPresentationMode LastAppliedVirtualDisplayPresentationMode { get; set; } = global::SideDock.Host.App.VirtualDisplayPresentationMode.Unknown;
    public string? DefaultAdbSerial { get; set; } = DefaultAdbSerialValue;
    public string AdbPath { get; set; } = string.Empty;
    public int ControlPort { get; set; } = DefaultControlPort;
    public int VideoPort { get; set; } = DefaultVideoPort;
    public int AudioPort { get; set; } = DefaultAudioPort;
    public int CameraPort { get; set; } = DefaultCameraPort;
    public bool RetainRecentLogs { get; set; } = true;
    public bool IncludePortInfoInDiagnostics { get; set; } = true;
    public int Nv12PoolSize { get; set; } = DefaultNv12PoolSize;
    public int EncodedPacketQueue { get; set; } = DefaultEncodedPacketQueue;
    public int AndroidCursorScalePercent { get; set; } = DefaultAndroidCursorScalePercent;
    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;
    public AppInterfaceDensity InterfaceDensity { get; set; } = AppInterfaceDensity.Standard;
    public AppUpdateSourceKind UpdateSourceKind { get; set; } = AppUpdateSourceKind.GitHubReleases;
    public string UpdateGitHubRepository { get; set; } = DefaultUpdateGitHubRepository;
    public string UpdateManifestUrl { get; set; } = string.Empty;
    public AppReleaseChannel ReleaseChannel { get; set; } = AppReleaseChannel.Stable;

    public static AppSettings CreateDefault()
    {
        return new AppSettings();
    }

    public AppSettings Normalize()
    {
        AdbPath = (AdbPath ?? string.Empty).Trim();
        ConnectionMode = ConnectionMode is AppConnectionMode.Usb or AppConnectionMode.Lan
            ? ConnectionMode
            : AppConnectionMode.Usb;
        DefaultAdbSerial = NormalizeOptional(DefaultAdbSerial);
        ControlPort = NormalizePort(ControlPort, DefaultControlPort);
        VideoPort = NormalizePort(VideoPort, DefaultVideoPort);
        AudioPort = NormalizePort(AudioPort, DefaultAudioPort);
        CameraPort = NormalizePort(CameraPort, DefaultCameraPort);
        VirtualDisplayResolution = NormalizeVirtualDisplayResolution(VirtualDisplayResolution);
        VirtualDisplayRefreshRate = NormalizeVirtualDisplayRefreshRate(VirtualDisplayRefreshRate);
        VirtualDisplayPresentationMode = NormalizePresentationMode(
            VirtualDisplayPresentationMode,
            global::SideDock.Host.App.VirtualDisplayPresentationMode.Extend);
        LastAppliedVirtualDisplayResolution = NormalizeVirtualDisplayResolution(LastAppliedVirtualDisplayResolution);
        LastAppliedVirtualDisplayRefreshRate = NormalizeVirtualDisplayRefreshRate(LastAppliedVirtualDisplayRefreshRate);
        LastAppliedVirtualDisplayPresentationMode = NormalizePresentationMode(
            LastAppliedVirtualDisplayPresentationMode,
            global::SideDock.Host.App.VirtualDisplayPresentationMode.Unknown,
            allowUnknown: true);
        Nv12PoolSize = Clamp(Nv12PoolSize, 1, 16, DefaultNv12PoolSize);
        EncodedPacketQueue = Clamp(EncodedPacketQueue, 1, 8, DefaultEncodedPacketQueue);
        AndroidCursorScalePercent = Clamp(
            AndroidCursorScalePercent,
            50,
            200,
            DefaultAndroidCursorScalePercent);
        ThemeMode = NormalizeThemeMode(ThemeMode);
        InterfaceDensity = NormalizeInterfaceDensity(InterfaceDensity);
        UpdateSourceKind = NormalizeUpdateSourceKind(UpdateSourceKind);
        UpdateGitHubRepository = NormalizeGitHubRepository(UpdateGitHubRepository);
        UpdateManifestUrl = (UpdateManifestUrl ?? string.Empty).Trim();
        ReleaseChannel = NormalizeReleaseChannel(ReleaseChannel);
        return this;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int NormalizePort(int value, int fallback)
    {
        return value is >= 1 and <= 65535 ? value : fallback;
    }

    private static int Clamp(int value, int min, int max, int fallback)
    {
        return value < min || value > max ? fallback : value;
    }

    private static AppThemeMode NormalizeThemeMode(AppThemeMode value)
    {
        return value switch
        {
            AppThemeMode.Light or AppThemeMode.Dark or AppThemeMode.System => value,
            _ => AppThemeMode.System
        };
    }

    private static AppInterfaceDensity NormalizeInterfaceDensity(AppInterfaceDensity value)
    {
        return value switch
        {
            AppInterfaceDensity.Standard or AppInterfaceDensity.Compact => value,
            _ => AppInterfaceDensity.Standard
        };
    }

    private static AppUpdateSourceKind NormalizeUpdateSourceKind(AppUpdateSourceKind value)
    {
        return value switch
        {
            AppUpdateSourceKind.None or AppUpdateSourceKind.GitHubReleases or AppUpdateSourceKind.Manifest => value,
            _ => AppUpdateSourceKind.None
        };
    }

    private static AppReleaseChannel NormalizeReleaseChannel(AppReleaseChannel value)
    {
        return value switch
        {
            AppReleaseChannel.Stable or AppReleaseChannel.Preview => value,
            _ => AppReleaseChannel.Stable
        };
    }

    private static string NormalizeGitHubRepository(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().Trim('/');
        if (normalized.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["https://github.com/".Length..].Trim('/');
        }

        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized;
    }

    private static string NormalizeVirtualDisplayResolution(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "720" or "720p" => "720p",
            "1080" or "1080p" => "1080p",
            "2k" or "1440p" => "2k",
            _ => "1080p"
        };
    }

    private static string NormalizeVirtualDisplayRefreshRate(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Replace("Hz", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        return normalized switch
        {
            "30" => "30",
            "60" => "60",
            "120" => "120",
            _ => "120"
        };
    }

    private static VirtualDisplayPresentationMode NormalizePresentationMode(
        VirtualDisplayPresentationMode value,
        VirtualDisplayPresentationMode fallback,
        bool allowUnknown = false)
    {
        return value switch
        {
            global::SideDock.Host.App.VirtualDisplayPresentationMode.Extend
                or global::SideDock.Host.App.VirtualDisplayPresentationMode.Mirror
                or global::SideDock.Host.App.VirtualDisplayPresentationMode.SecondaryOnly => value,
            global::SideDock.Host.App.VirtualDisplayPresentationMode.Unknown when allowUnknown => value,
            _ => fallback
        };
    }
}

internal static class AppSettingsStore
{
    private const string SettingsFileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string? LastLoadError { get; private set; }

    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SideDock",
        "HostApp");

    public static string SettingsPath => Path.Combine(SettingsDirectory, SettingsFileName);

    public static AppSettings Load()
    {
        LastLoadError = null;
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return AppSettings.CreateDefault();
            }

            var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            return (JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? AppSettings.CreateDefault()).Normalize();
        }
        catch (Exception ex)
        {
            LastLoadError = ex.Message;
            return AppSettings.CreateDefault();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(settings.Normalize(), JsonOptions);
        File.WriteAllText(SettingsPath, json, Encoding.UTF8);
    }
}
