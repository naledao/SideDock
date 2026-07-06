using System.Runtime.InteropServices;

namespace SideDock.Host.App;

internal static class DisplayLayoutQuery
{
    private const int EnumCurrentSettings = -1;
    private const int DisplayDeviceActive = 0x00000001;
    private const int DisplayDevicePrimaryDevice = 0x00000004;
    private const int DisplayDeviceMirroringDriver = 0x00000008;

    private static readonly string[] SideDockKeywords =
    [
        "SideDock Virtual Display",
        "SideDockIdd",
        "SideDock"
    ];

    public static DisplayLayoutSnapshot GetCurrent()
    {
        try
        {
            var displays = new List<DisplayLayoutMonitor>();
            for (uint index = 0; ; index++)
            {
                var adapter = DisplayDevice.Create();
                if (!EnumDisplayDevicesW(null, index, ref adapter, 0))
                {
                    break;
                }

                if ((adapter.StateFlags & DisplayDeviceActive) == 0
                    || (adapter.StateFlags & DisplayDeviceMirroringDriver) != 0
                    || string.IsNullOrWhiteSpace(adapter.DeviceName))
                {
                    continue;
                }

                var mode = DevMode.Create();
                if (!EnumDisplaySettingsW(adapter.DeviceName, EnumCurrentSettings, ref mode)
                    || mode.PelsWidth == 0
                    || mode.PelsHeight == 0)
                {
                    continue;
                }

                var monitor = DisplayDevice.Create();
                var hasMonitor = EnumDisplayDevicesW(adapter.DeviceName, 0, ref monitor, 0);
                var displayName = FirstNonEmpty(
                    hasMonitor ? monitor.DeviceString : null,
                    adapter.DeviceString,
                    adapter.DeviceName,
                    "Unknown Display");
                var deviceString = FirstNonEmpty(
                    hasMonitor ? monitor.DeviceString : null,
                    adapter.DeviceString,
                    string.Empty);
                var deviceId = FirstNonEmpty(
                    hasMonitor ? monitor.DeviceID : null,
                    adapter.DeviceID,
                    string.Empty);
                var deviceKey = FirstNonEmpty(
                    hasMonitor ? monitor.DeviceKey : null,
                    adapter.DeviceKey,
                    string.Empty);
                var refreshRate = mode.DisplayFrequency > 0 ? (int)mode.DisplayFrequency : 0;
                var isPrimary = (adapter.StateFlags & DisplayDevicePrimaryDevice) != 0;
                var isSideDock = IsSideDockVirtualDisplay(
                    displayName,
                    adapter.DeviceName,
                    adapter.DeviceString,
                    adapter.DeviceID,
                    adapter.DeviceKey,
                    deviceString,
                    deviceId,
                    deviceKey);

                displays.Add(new DisplayLayoutMonitor(
                    displayName,
                    CleanString(adapter.DeviceName),
                    CleanString(deviceString),
                    CleanString(deviceId),
                    (int)mode.PositionX,
                    (int)mode.PositionY,
                    (int)mode.PelsWidth,
                    (int)mode.PelsHeight,
                    refreshRate,
                    isPrimary,
                    isSideDock));
            }

            return new DisplayLayoutSnapshot(displays);
        }
        catch (Exception ex)
        {
            return new DisplayLayoutSnapshot(Array.Empty<DisplayLayoutMonitor>(), ex.Message);
        }
    }

    private static bool IsSideDockVirtualDisplay(params string?[] values)
    {
        return values.Any(value =>
            !string.IsNullOrWhiteSpace(value)
            && SideDockKeywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var cleaned = CleanString(value);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                return cleaned;
            }
        }

        return string.Empty;
    }

    private static string CleanString(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.TrimEnd('\0').Trim();
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(
        string? lpDevice,
        uint iDevNum,
        ref DisplayDevice lpDisplayDevice,
        uint dwFlags);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(
        string lpszDeviceName,
        int iModeNum,
        ref DevMode lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;

        public static DisplayDevice Create()
        {
            return new DisplayDevice
            {
                Cb = Marshal.SizeOf<DisplayDevice>(),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceID = string.Empty,
                DeviceKey = string.Empty
            };
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        private const int CchDevName = 32;
        private const int CchFormName = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDevName)]
        public string DeviceName;

        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchFormName)]
        public string FormName;

        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint ICMMethod;
        public uint ICMIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;

        public static DevMode Create()
        {
            return new DevMode
            {
                Size = (ushort)Marshal.SizeOf<DevMode>(),
                DeviceName = string.Empty,
                FormName = string.Empty
            };
        }
    }
}

internal sealed class DisplayLayoutSnapshot
{
    public DisplayLayoutSnapshot(IReadOnlyList<DisplayLayoutMonitor> monitors, string? queryError = null)
    {
        Monitors = monitors;
        QueryError = queryError;
        SideDockMonitor = monitors.FirstOrDefault(monitor => monitor.IsSideDockVirtualDisplay);
    }

    public IReadOnlyList<DisplayLayoutMonitor> Monitors { get; }

    public DisplayLayoutMonitor? SideDockMonitor { get; }

    public string? QueryError { get; }

    public bool HasSideDockVirtualDisplay => SideDockMonitor is not null;
}

internal sealed record DisplayLayoutMonitor(
    string DisplayName,
    string DeviceName,
    string DeviceString,
    string DeviceId,
    int X,
    int Y,
    int Width,
    int Height,
    int RefreshRate,
    bool IsPrimary,
    bool IsSideDockVirtualDisplay)
{
    public string ResolutionText => $"{Width} x {Height}";

    public string RefreshRateText => RefreshRate > 0 ? $"{RefreshRate} Hz" : "刷新率未知";

    public string PositionText => $"({X}, {Y})";
}
