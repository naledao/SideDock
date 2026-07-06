using System.Text;
using System.Text.Json;

namespace SideDock.Host.App;

internal sealed class AppSettings
{
    public const int DefaultControlPort = 27183;
    public const int DefaultVideoPort = 27184;
    public const int DefaultAudioPort = 27185;
    public const int DefaultCameraPort = 27186;
    public const int DefaultNv12PoolSize = 4;
    public const int DefaultEncodedPacketQueue = 2;
    public const string DefaultAdbSerialValue = "HA1K3AX0";

    public bool StartWithWindows { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool StartVirtualDisplayWithHost { get; set; } = true;
    public bool ConfigureAdbReverseOnHostStart { get; set; } = true;
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

    public static AppSettings CreateDefault()
    {
        return new AppSettings();
    }

    public AppSettings Normalize()
    {
        AdbPath = (AdbPath ?? string.Empty).Trim();
        DefaultAdbSerial = NormalizeOptional(DefaultAdbSerial);
        ControlPort = NormalizePort(ControlPort, DefaultControlPort);
        VideoPort = NormalizePort(VideoPort, DefaultVideoPort);
        AudioPort = NormalizePort(AudioPort, DefaultAudioPort);
        CameraPort = NormalizePort(CameraPort, DefaultCameraPort);
        Nv12PoolSize = Clamp(Nv12PoolSize, 1, 16, DefaultNv12PoolSize);
        EncodedPacketQueue = Clamp(EncodedPacketQueue, 1, 8, DefaultEncodedPacketQueue);
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
}

internal static class AppSettingsStore
{
    private const string SettingsFileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SideDock",
        "HostApp");

    public static string SettingsPath => Path.Combine(SettingsDirectory, SettingsFileName);

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return AppSettings.CreateDefault();
            }

            var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            return (JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? AppSettings.CreateDefault()).Normalize();
        }
        catch
        {
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
