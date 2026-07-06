using System.Globalization;
using System.Runtime.InteropServices;

namespace SideDock.Host.App;

internal static class VirtualDisplayModeService
{
    private const int EnumCurrentSettings = -1;
    private const int DisplayDeviceActive = 0x00000001;
    private const int DisplayDevicePrimaryDevice = 0x00000004;
    private const int DisplayDeviceMirroringDriver = 0x00000008;
    private const int DispChangeSuccessful = 0;
    private const int ErrorSuccess = 0;
    private const uint CdsUpdateRegistry = 0x00000001;
    private const uint CdsNoReset = 0x10000000;
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const uint SdcUseSuppliedDisplayConfig = 0x00000020;
    private const uint SdcValidate = 0x00000040;
    private const uint SdcApply = 0x00000080;
    private const uint SdcSaveToDatabase = 0x00000200;
    private const uint SdcAllowChanges = 0x00000400;
    private const uint DisplayConfigPathActive = 0x00000001;
    private const uint DisplayConfigModeInfoTypeSource = 1;
    private const uint DisplayConfigModeInfoTypeTarget = 2;
    private const uint DisplayConfigPixelFormat32Bpp = 4;
    private const int DmPosition = 0x00000020;
    private const int DmDisplayOrientation = 0x00000080;
    private const int DmBitsPerPel = 0x00040000;
    private const int DmPelsWidth = 0x00080000;
    private const int DmPelsHeight = 0x00100000;
    private const int DmDisplayFrequency = 0x00400000;

    private static readonly string[] SideDockKeywords =
    [
        "SideDock Virtual Display",
        "SideDockIdd",
        "SideDock"
    ];

    public static VirtualDisplayModeApplyResult Apply(VirtualDisplayModeRequest request)
    {
        if (!OperatingSystem.IsWindows())
        {
            return VirtualDisplayModeApplyResult.Failed("显示模式切换仅支持 Windows。", null);
        }

        if (request.Width <= 0 || request.Height <= 0 || request.RefreshRate <= 0)
        {
            return VirtualDisplayModeApplyResult.Failed($"显示模式无效：{FormatRequest(request)}。", null);
        }

        var search = FindSideDockDisplay();
        if (search.Display is null)
        {
            return VirtualDisplayModeApplyResult.Failed(search.FailureSummary, null);
        }

        var display = search.Display;
        if (IsModeMatch(display.CurrentMode, request))
        {
            return VirtualDisplayModeApplyResult.Succeeded("显示模式已是当前真实模式。", display.CurrentMode);
        }

        var advertisedModes = EnumerateDisplayModes(display.DeviceName);
        var selectedMode = SelectDisplayMode(advertisedModes, request);
        if (selectedMode is null)
        {
            return VirtualDisplayModeApplyResult.Failed(
                $"请求的显示模式不受 SideDock 虚拟显示器支持。当前真实模式：{FormatMode(display.CurrentMode)}。{FormatAdvertisedModes(advertisedModes, request.Width, request.Height)}",
                display.CurrentMode);
        }

        var currentMode = display.CurrentDevMode;
        var preparedMode = selectedMode.Value;
        PrepareDisplayModeForApply(ref preparedMode, currentMode, request);

        var attempts = new List<string>();
        if (TryApplyDisplayMode(display.DeviceName, preparedMode, request, flags: 0, "dynamic", attempts, out var changedDisplay))
        {
            return VirtualDisplayModeApplyResult.Succeeded("显示模式已应用。", changedDisplay?.CurrentMode ?? display.CurrentMode);
        }

        preparedMode = selectedMode.Value;
        PrepareDisplayModeForApply(ref preparedMode, currentMode, request);
        if (TryApplyDisplayMode(display.DeviceName, preparedMode, request, CdsUpdateRegistry, "registry", attempts, out changedDisplay))
        {
            return VirtualDisplayModeApplyResult.Succeeded("显示模式已应用。", changedDisplay?.CurrentMode ?? display.CurrentMode);
        }

        preparedMode = selectedMode.Value;
        PrepareDisplayModeForApply(ref preparedMode, currentMode, request);
        if (TryStageAndApplyDisplayMode(display.DeviceName, preparedMode, request, attempts, out changedDisplay))
        {
            return VirtualDisplayModeApplyResult.Succeeded("显示模式已应用。", changedDisplay?.CurrentMode ?? display.CurrentMode);
        }

        if (TryDisplayConfigChangeMode(display, currentMode, request, attempts, out changedDisplay))
        {
            return VirtualDisplayModeApplyResult.Succeeded("显示模式已应用。", changedDisplay?.CurrentMode ?? display.CurrentMode);
        }

        var currentDisplay = changedDisplay?.CurrentMode ?? FindSideDockDisplay().Display?.CurrentMode ?? display.CurrentMode;
        return VirtualDisplayModeApplyResult.Failed(
            $"显示模式应用失败：{string.Join("; ", attempts)}。当前真实模式：{FormatMode(currentDisplay)}。",
            currentDisplay);
    }

    private static bool TryApplyDisplayMode(
        string deviceName,
        NativeDevMode devMode,
        VirtualDisplayModeRequest requestedMode,
        uint flags,
        string attemptName,
        List<string> attempts,
        out SideDockDisplay? changedDisplay)
    {
        var result = ChangeDisplaySettingsExW(deviceName, ref devMode, IntPtr.Zero, flags, IntPtr.Zero);
        var win32Error = Marshal.GetLastWin32Error();
        changedDisplay = WaitForAppliedMode(requestedMode, result == DispChangeSuccessful);
        var changed = result == DispChangeSuccessful && IsModeMatch(changedDisplay?.CurrentMode, requestedMode);
        attempts.Add(
            $"{attemptName}={DescribeDispChangeResult(result)}({result}) "
            + $"win32={win32Error} "
            + $"mode={FormatMode(changedDisplay?.CurrentMode)}");
        return changed;
    }

    private static bool TryStageAndApplyDisplayMode(
        string deviceName,
        NativeDevMode devMode,
        VirtualDisplayModeRequest requestedMode,
        List<string> attempts,
        out SideDockDisplay? changedDisplay)
    {
        var stageResult = ChangeDisplaySettingsExW(
            deviceName,
            ref devMode,
            IntPtr.Zero,
            CdsUpdateRegistry | CdsNoReset,
            IntPtr.Zero);
        var stageWin32Error = Marshal.GetLastWin32Error();
        if (stageResult != DispChangeSuccessful)
        {
            changedDisplay = WaitForAppliedMode(requestedMode, poll: false);
            attempts.Add(
                $"staged={DescribeDispChangeResult(stageResult)}({stageResult}) "
                + $"win32={stageWin32Error} "
                + $"mode={FormatMode(changedDisplay?.CurrentMode)}");
            return false;
        }

        var applyResult = ChangeDisplaySettingsExW(
            null,
            IntPtr.Zero,
            IntPtr.Zero,
            0,
            IntPtr.Zero);
        var applyWin32Error = Marshal.GetLastWin32Error();
        changedDisplay = WaitForAppliedMode(requestedMode, applyResult == DispChangeSuccessful);
        var changed = applyResult == DispChangeSuccessful && IsModeMatch(changedDisplay?.CurrentMode, requestedMode);
        attempts.Add(
            $"staged={DescribeDispChangeResult(stageResult)}({stageResult}) "
            + $"stageWin32={stageWin32Error} "
            + $"global={DescribeDispChangeResult(applyResult)}({applyResult}) "
            + $"globalWin32={applyWin32Error} "
            + $"mode={FormatMode(changedDisplay?.CurrentMode)}");
        return changed;
    }

    private static bool TryDisplayConfigChangeMode(
        SideDockDisplay display,
        NativeDevMode currentMode,
        VirtualDisplayModeRequest requestedMode,
        List<string> attempts,
        out SideDockDisplay? changedDisplay)
    {
        var queryResult = TryQueryActiveDisplayConfig(out var paths, out var modes, out var queryMessage);
        if (queryResult != ErrorSuccess)
        {
            changedDisplay = WaitForAppliedMode(requestedMode, poll: false);
            attempts.Add($"displayConfigQuery={queryMessage} mode={FormatMode(changedDisplay?.CurrentMode)}");
            return false;
        }

        var sourceNameResult = TryFindDisplayConfigPath(display.DeviceName, paths, out var pathIndex, out var sourceNameMessage);
        if (sourceNameResult != ErrorSuccess)
        {
            changedDisplay = WaitForAppliedMode(requestedMode, poll: false);
            attempts.Add($"displayConfigFind={sourceNameMessage} mode={FormatMode(changedDisplay?.CurrentMode)}");
            return false;
        }

        var path = paths[pathIndex];
        var sourceModeIndex = EnsureDisplayConfigSourceMode(modes, path.sourceInfo.adapterId, path.sourceInfo.id);
        var targetModeIndex = EnsureDisplayConfigTargetMode(modes, path.targetInfo.adapterId, path.targetInfo.id);
        if (sourceModeIndex < 0 || targetModeIndex < 0)
        {
            changedDisplay = WaitForAppliedMode(requestedMode, poll: false);
            attempts.Add(
                $"displayConfigModeIdx=missing sourceIdx={sourceModeIndex} targetIdx={targetModeIndex} "
                + $"path={FormatDisplayConfigPath(path)} mode={FormatMode(changedDisplay?.CurrentMode)}");
            return false;
        }

        var updatedModes = modes.ToArray();
        updatedModes[sourceModeIndex].sourceMode.width = (uint)requestedMode.Width;
        updatedModes[sourceModeIndex].sourceMode.height = (uint)requestedMode.Height;
        updatedModes[sourceModeIndex].sourceMode.pixelFormat = DisplayConfigPixelFormat32Bpp;
        updatedModes[sourceModeIndex].sourceMode.position.X = currentMode.PositionX;
        updatedModes[sourceModeIndex].sourceMode.position.Y = currentMode.PositionY;

        var targetSignal = updatedModes[targetModeIndex].targetMode.targetVideoSignalInfo;
        targetSignal.activeSize.cx = (uint)requestedMode.Width;
        targetSignal.activeSize.cy = (uint)requestedMode.Height;
        if (targetSignal.totalSize.cx == 0 || targetSignal.totalSize.cx < targetSignal.activeSize.cx)
        {
            targetSignal.totalSize.cx = targetSignal.activeSize.cx;
        }

        if (targetSignal.totalSize.cy == 0 || targetSignal.totalSize.cy < targetSignal.activeSize.cy)
        {
            targetSignal.totalSize.cy = targetSignal.activeSize.cy;
        }

        targetSignal.vSyncFreq.Numerator = (uint)requestedMode.RefreshRate;
        targetSignal.vSyncFreq.Denominator = 1;
        targetSignal.hSyncFreq.Numerator = (uint)(requestedMode.RefreshRate * Math.Max(1, requestedMode.Height));
        targetSignal.hSyncFreq.Denominator = 1;
        targetSignal.pixelRate = (ulong)requestedMode.RefreshRate * (ulong)requestedMode.Width * (ulong)requestedMode.Height;
        targetSignal.scanLineOrdering = DisplayConfigScanlineOrderingProgressive;
        updatedModes[targetModeIndex].targetMode.targetVideoSignalInfo = targetSignal;

        var updatedPaths = paths.ToArray();
        updatedPaths[pathIndex].sourceInfo.modeInfoIdx = (uint)sourceModeIndex;
        updatedPaths[pathIndex].targetInfo.modeInfoIdx = (uint)targetModeIndex;
        updatedPaths[pathIndex].targetInfo.refreshRate.Numerator = (uint)requestedMode.RefreshRate;
        updatedPaths[pathIndex].targetInfo.refreshRate.Denominator = 1;
        updatedPaths[pathIndex].targetInfo.scanLineOrdering = DisplayConfigScanlineOrderingProgressive;
        updatedPaths[pathIndex].flags |= DisplayConfigPathActive;

        var validateResult = SetDisplayConfig(
            (uint)updatedPaths.Length,
            updatedPaths,
            (uint)updatedModes.Length,
            updatedModes,
            SdcUseSuppliedDisplayConfig | SdcValidate | SdcAllowChanges);
        if (validateResult != ErrorSuccess)
        {
            changedDisplay = WaitForAppliedMode(requestedMode, poll: false);
            attempts.Add(
                $"displayConfigValidate={validateResult} "
                + $"path={FormatDisplayConfigPath(updatedPaths[pathIndex])} "
                + $"sourceIdx={sourceModeIndex} targetIdx={targetModeIndex} mode={FormatMode(changedDisplay?.CurrentMode)}");
            return false;
        }

        var applyResult = SetDisplayConfig(
            (uint)updatedPaths.Length,
            updatedPaths,
            (uint)updatedModes.Length,
            updatedModes,
            SdcUseSuppliedDisplayConfig | SdcApply | SdcSaveToDatabase | SdcAllowChanges);
        changedDisplay = WaitForAppliedMode(requestedMode, applyResult == ErrorSuccess);
        var changed = applyResult == ErrorSuccess && IsModeMatch(changedDisplay?.CurrentMode, requestedMode);
        attempts.Add(
            $"displayConfigValidate={validateResult} displayConfigApply={applyResult} "
            + $"path={FormatDisplayConfigPath(updatedPaths[pathIndex])} "
            + $"sourceIdx={sourceModeIndex} targetIdx={targetModeIndex} mode={FormatMode(changedDisplay?.CurrentMode)}");
        return changed;
    }

    private static DisplaySearchResult FindSideDockDisplay()
    {
        var candidates = new List<SideDockDisplayCandidate>();
        var primaryMatches = new List<string>();

        for (uint index = 0; ; index++)
        {
            var adapter = NativeDisplayDevice.Create();
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

            var mode = NativeDevMode.Create();
            if (!EnumDisplaySettingsW(adapter.DeviceName, EnumCurrentSettings, ref mode)
                || mode.PelsWidth == 0
                || mode.PelsHeight == 0)
            {
                continue;
            }

            var monitor = NativeDisplayDevice.Create();
            var hasMonitor = EnumDisplayDevicesW(adapter.DeviceName, 0, ref monitor, 0);
            var displayName = FirstNonEmpty(
                hasMonitor ? monitor.DeviceString : null,
                adapter.DeviceString,
                adapter.DeviceName,
                "Unknown Display");
            var score = ScoreSideDockCandidate(adapter, hasMonitor ? monitor : null);
            if (score <= 0)
            {
                continue;
            }

            var isPrimary = (adapter.StateFlags & DisplayDevicePrimaryDevice) != 0;
            var currentMode = new VirtualDisplayMode(
                (int)mode.PelsWidth,
                (int)mode.PelsHeight,
                mode.DisplayFrequency > 0 ? (int)mode.DisplayFrequency : 0);
            var display = new SideDockDisplay(
                CleanString(adapter.DeviceName),
                displayName,
                currentMode,
                mode);

            if (isPrimary)
            {
                primaryMatches.Add($"{display.DisplayName} {display.DeviceName} {FormatMode(currentMode)}");
                continue;
            }

            candidates.Add(new SideDockDisplayCandidate(display, score));
        }

        var selected = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Display.DeviceName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (selected is not null)
        {
            return DisplaySearchResult.Success(selected.Display);
        }

        if (primaryMatches.Count > 0)
        {
            return DisplaySearchResult.Failure(
                "检测到 SideDock 匹配项，但它当前是主屏。为避免误改主屏，已拒绝修改显示模式。"
                + $" 匹配项：{string.Join("; ", primaryMatches)}。");
        }

        return DisplaySearchResult.Failure("未检测到 SideDock 虚拟显示器，无法应用显示模式。");
    }

    private static SideDockDisplay? WaitForAppliedMode(VirtualDisplayModeRequest requestedMode, bool poll)
    {
        SideDockDisplay? display = null;
        var attempts = poll ? 8 : 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(125);
            }

            display = FindSideDockDisplay().Display;
            if (IsModeMatch(display?.CurrentMode, requestedMode))
            {
                break;
            }
        }

        return display;
    }

    private static IReadOnlyList<NativeDevMode> EnumerateDisplayModes(string deviceName)
    {
        var modes = new List<NativeDevMode>();
        for (var index = 0; ; index++)
        {
            var mode = NativeDevMode.Create();
            if (!EnumDisplaySettingsW(deviceName, index, ref mode))
            {
                break;
            }

            if (mode.PelsWidth == 0 || mode.PelsHeight == 0)
            {
                continue;
            }

            modes.Add(mode);
        }

        return modes;
    }

    private static NativeDevMode? SelectDisplayMode(IReadOnlyList<NativeDevMode> modes, VirtualDisplayModeRequest requestedMode)
    {
        return modes
            .Where(mode =>
                mode.PelsWidth == requestedMode.Width
                && mode.PelsHeight == requestedMode.Height
                && (mode.DisplayFrequency == 0 || Math.Abs((int)mode.DisplayFrequency - requestedMode.RefreshRate) <= 1))
            .OrderBy(mode => mode.DisplayFrequency == requestedMode.RefreshRate ? 0 : mode.DisplayFrequency == 0 ? 2 : 1)
            .ThenBy(mode => mode.DisplayFrequency == 0 ? int.MaxValue : Math.Abs((int)mode.DisplayFrequency - requestedMode.RefreshRate))
            .ThenByDescending(mode => mode.BitsPerPel)
            .Select(mode => (NativeDevMode?)mode)
            .FirstOrDefault();
    }

    private static void PrepareDisplayModeForApply(
        ref NativeDevMode devMode,
        NativeDevMode currentMode,
        VirtualDisplayModeRequest requestedMode)
    {
        devMode.Size = (ushort)Marshal.SizeOf<NativeDevMode>();
        devMode.DriverExtra = 0;
        devMode.Fields = DmPosition | DmBitsPerPel | DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
        devMode.PelsWidth = (uint)requestedMode.Width;
        devMode.PelsHeight = (uint)requestedMode.Height;
        devMode.DisplayFrequency = (uint)requestedMode.RefreshRate;
        if (devMode.BitsPerPel == 0)
        {
            devMode.BitsPerPel = currentMode.BitsPerPel == 0 ? 32u : currentMode.BitsPerPel;
        }

        devMode.PositionX = currentMode.PositionX;
        devMode.PositionY = currentMode.PositionY;

        if ((currentMode.Fields & DmDisplayOrientation) != 0)
        {
            devMode.Fields |= DmDisplayOrientation;
            devMode.DisplayOrientation = currentMode.DisplayOrientation;
        }
    }

    private static int ScoreSideDockCandidate(NativeDisplayDevice adapter, NativeDisplayDevice? monitor)
    {
        var haystack = string.Join(
            " ",
            CleanString(adapter.DeviceName),
            CleanString(adapter.DeviceString),
            CleanString(adapter.DeviceID),
            CleanString(adapter.DeviceKey),
            monitor.HasValue ? CleanString(monitor.Value.DeviceString) : string.Empty,
            monitor.HasValue ? CleanString(monitor.Value.DeviceID) : string.Empty,
            monitor.HasValue ? CleanString(monitor.Value.DeviceKey) : string.Empty);

        var score = 0;
        if (haystack.Contains("SideDock Virtual Display", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (haystack.Contains("SideDockIdd", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("SIDEDOCKIDD", StringComparison.OrdinalIgnoreCase))
        {
            score += 80;
        }

        if (haystack.Contains("SideDock", StringComparison.OrdinalIgnoreCase))
        {
            score += 70;
        }

        if (SideDockKeywords.Any(keyword => haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            score += 1;
        }

        return score;
    }

    private static bool IsModeMatch(VirtualDisplayMode? currentMode, VirtualDisplayModeRequest requestedMode)
    {
        return currentMode is not null
            && currentMode.Width == requestedMode.Width
            && currentMode.Height == requestedMode.Height
            && Math.Abs(currentMode.RefreshRate - requestedMode.RefreshRate) <= 1;
    }

    private static int TryQueryActiveDisplayConfig(
        out DisplayConfigPathInfo[] paths,
        out DisplayConfigModeInfo[] modes,
        out string message)
    {
        paths = [];
        modes = [];

        var sizeResult = GetDisplayConfigBufferSizes(
            QdcOnlyActivePaths,
            out var pathCount,
            out var modeCount);
        if (sizeResult != ErrorSuccess)
        {
            message = $"GetDisplayConfigBufferSizes={sizeResult}";
            return sizeResult;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            paths = new DisplayConfigPathInfo[pathCount];
            modes = new DisplayConfigModeInfo[modeCount];
            var queryPathCount = pathCount;
            var queryModeCount = modeCount;
            var queryResult = QueryDisplayConfig(
                QdcOnlyActivePaths,
                ref queryPathCount,
                paths,
                ref queryModeCount,
                modes,
                IntPtr.Zero);
            if (queryResult == ErrorSuccess)
            {
                if (queryPathCount != paths.Length)
                {
                    Array.Resize(ref paths, (int)queryPathCount);
                }

                if (queryModeCount != modes.Length)
                {
                    Array.Resize(ref modes, (int)queryModeCount);
                }

                message = $"paths={paths.Length} modes={modes.Length}";
                return ErrorSuccess;
            }

            sizeResult = GetDisplayConfigBufferSizes(
                QdcOnlyActivePaths,
                out pathCount,
                out modeCount);
            if (sizeResult != ErrorSuccess)
            {
                message = $"QueryDisplayConfig={queryResult}; resize={sizeResult}";
                return sizeResult;
            }
        }

        message = "QueryDisplayConfig retried without a stable path list.";
        return -1;
    }

    private static int TryFindDisplayConfigPath(
        string deviceName,
        IReadOnlyList<DisplayConfigPathInfo> paths,
        out int pathIndex,
        out string message)
    {
        pathIndex = -1;
        var inspected = new List<string>();
        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            var sourceName = DisplayConfigSourceDeviceName.Create(path.sourceInfo.adapterId, path.sourceInfo.id);
            var result = DisplayConfigGetDeviceInfo(ref sourceName);
            if (result != ErrorSuccess)
            {
                inspected.Add($"#{index}:{FormatDisplayConfigPath(path)} sourceNameResult={result}");
                continue;
            }

            var viewName = CleanString(sourceName.viewGdiDeviceName);
            inspected.Add($"#{index}:{viewName} {FormatDisplayConfigPath(path)}");
            if (viewName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
            {
                pathIndex = index;
                message = $"found {viewName} at #{index}";
                return ErrorSuccess;
            }
        }

        message = $"device={deviceName} not found; inspected=[{string.Join("; ", inspected)}]";
        return -1;
    }

    private static int EnsureDisplayConfigSourceMode(
        IReadOnlyList<DisplayConfigModeInfo> modes,
        Luid adapterId,
        uint sourceId)
    {
        for (var index = 0; index < modes.Count; index++)
        {
            if (modes[index].infoType == DisplayConfigModeInfoTypeSource
                && modes[index].id == sourceId
                && modes[index].adapterId == adapterId)
            {
                return index;
            }
        }

        return -1;
    }

    private static int EnsureDisplayConfigTargetMode(
        IReadOnlyList<DisplayConfigModeInfo> modes,
        Luid adapterId,
        uint targetId)
    {
        for (var index = 0; index < modes.Count; index++)
        {
            if (modes[index].infoType == DisplayConfigModeInfoTypeTarget
                && modes[index].id == targetId
                && modes[index].adapterId == adapterId)
            {
                return index;
            }
        }

        return -1;
    }

    private static string FormatAdvertisedModes(IReadOnlyList<NativeDevMode> modes, int width, int height)
    {
        var matchingRefreshRates = modes
            .Where(mode => mode.PelsWidth == width && mode.PelsHeight == height)
            .Select(mode => mode.DisplayFrequency == 0 ? "default" : $"{mode.DisplayFrequency}Hz")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (matchingRefreshRates.Length > 0)
        {
            return $"{width}x{height} 支持的刷新率：[{string.Join(", ", matchingRefreshRates)}]。";
        }

        var advertisedModes = modes
            .GroupBy(mode => $"{mode.PelsWidth}x{mode.PelsHeight}@{mode.DisplayFrequency}Hz")
            .Select(group => group.Key)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(12)
            .ToArray();
        return advertisedModes.Length == 0
            ? "该显示器没有公布可用显示模式。"
            : $"已公布模式示例：[{string.Join(", ", advertisedModes)}]。";
    }

    private static string FormatDisplayConfigPath(DisplayConfigPathInfo path)
    {
        return $"source={path.sourceInfo.id}/{path.sourceInfo.modeInfoIdx} target={path.targetInfo.id}/{path.targetInfo.modeInfoIdx} refresh={FormatRational(path.targetInfo.refreshRate)} flags=0x{path.flags:X}";
    }

    private static string FormatRational(DisplayConfigRational rational)
    {
        return rational.Denominator == 0
            ? $"{rational.Numerator}/0"
            : $"{rational.Numerator / (double)rational.Denominator:F3}";
    }

    internal static string FormatMode(VirtualDisplayMode? mode)
    {
        return mode is null
            ? "未检测到"
            : $"{mode.Width.ToString(CultureInfo.InvariantCulture)} × {mode.Height.ToString(CultureInfo.InvariantCulture)} @ {mode.RefreshRate.ToString(CultureInfo.InvariantCulture)} Hz";
    }

    private static string FormatRequest(VirtualDisplayModeRequest request)
    {
        return $"{request.Width.ToString(CultureInfo.InvariantCulture)} × {request.Height.ToString(CultureInfo.InvariantCulture)} @ {request.RefreshRate.ToString(CultureInfo.InvariantCulture)} Hz";
    }

    private static string DescribeDispChangeResult(int result)
    {
        return result switch
        {
            0 => "DISP_CHANGE_SUCCESSFUL",
            1 => "DISP_CHANGE_RESTART",
            -1 => "DISP_CHANGE_FAILED",
            -2 => "DISP_CHANGE_BADMODE",
            -3 => "DISP_CHANGE_NOTUPDATED",
            -4 => "DISP_CHANGE_BADFLAGS",
            -5 => "DISP_CHANGE_BADPARAM",
            -6 => "DISP_CHANGE_BADDUALVIEW",
            _ => "DISP_CHANGE_UNKNOWN"
        };
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
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var nullIndex = value.IndexOf('\0');
        var trimmed = nullIndex >= 0 ? value[..nullIndex] : value;
        return trimmed.Trim();
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(
        string? lpDevice,
        uint iDevNum,
        ref NativeDisplayDevice lpDisplayDevice,
        uint dwFlags);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(
        string lpszDeviceName,
        int iModeNum,
        ref NativeDevMode lpDevMode);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ChangeDisplaySettingsExW(
        string? lpszDeviceName,
        ref NativeDevMode lpDevMode,
        IntPtr hwnd,
        uint dwflags,
        IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ChangeDisplaySettingsExW(
        string? lpszDeviceName,
        IntPtr lpDevMode,
        IntPtr hwnd,
        uint dwflags,
        IntPtr lParam);

    private const uint DisplayConfigScanlineOrderingProgressive = 1;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SetDisplayConfig(
        uint numPathArrayElements,
        [In] DisplayConfigPathInfo[] pathArray,
        uint numModeInfoArrayElements,
        [In] DisplayConfigModeInfo[] modeInfoArray,
        uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

    private sealed record DisplaySearchResult(SideDockDisplay? Display, string FailureSummary)
    {
        public static DisplaySearchResult Success(SideDockDisplay display)
        {
            return new DisplaySearchResult(display, string.Empty);
        }

        public static DisplaySearchResult Failure(string summary)
        {
            return new DisplaySearchResult(null, summary);
        }
    }

    private sealed record SideDockDisplayCandidate(SideDockDisplay Display, int Score);

    private sealed record SideDockDisplay(
        string DeviceName,
        string DisplayName,
        VirtualDisplayMode CurrentMode,
        NativeDevMode CurrentDevMode);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid : IEquatable<Luid>
    {
        public uint LowPart;
        public int HighPart;

        public bool Equals(Luid other)
        {
            return LowPart == other.LowPart && HighPart == other.HighPart;
        }

        public override bool Equals(object? obj)
        {
            return obj is Luid other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(LowPart, HighPart);
        }

        public static bool operator ==(Luid left, Luid right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(Luid left, Luid right)
        {
            return !left.Equals(right);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectL
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfig2DRegion
    {
        public uint cx;
        public uint cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigVideoSignalInfo
    {
        public ulong pixelRate;
        public DisplayConfigRational hSyncFreq;
        public DisplayConfigRational vSyncFreq;
        public DisplayConfig2DRegion activeSize;
        public DisplayConfig2DRegion totalSize;
        public uint videoStandard;
        public uint scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigTargetMode
    {
        public DisplayConfigVideoSignalInfo targetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSourceMode
    {
        public uint width;
        public uint height;
        public uint pixelFormat;
        public PointL position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDesktopImageInfo
    {
        public PointL PathSourceSize;
        public RectL DesktopImageRegion;
        public RectL DesktopImageClip;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct DisplayConfigModeInfo
    {
        [FieldOffset(0)]
        public uint infoType;

        [FieldOffset(4)]
        public uint id;

        [FieldOffset(8)]
        public Luid adapterId;

        [FieldOffset(16)]
        public DisplayConfigTargetMode targetMode;

        [FieldOffset(16)]
        public DisplayConfigSourceMode sourceMode;

        [FieldOffset(16)]
        public DisplayConfigDesktopImageInfo desktopImageInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DisplayConfigRational refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo sourceInfo;
        public DisplayConfigPathTargetInfo targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint type;
        public uint size;
        public Luid adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        private const uint DisplayConfigDeviceInfoGetSourceName = 1;

        public DisplayConfigDeviceInfoHeader header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;

        public static DisplayConfigSourceDeviceName Create(Luid adapterId, uint sourceId)
        {
            return new DisplayConfigSourceDeviceName
            {
                header = new DisplayConfigDeviceInfoHeader
                {
                    type = DisplayConfigDeviceInfoGetSourceName,
                    size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                    adapterId = adapterId,
                    id = sourceId
                },
                viewGdiDeviceName = string.Empty
            };
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeDisplayDevice
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

        public static NativeDisplayDevice Create()
        {
            return new NativeDisplayDevice
            {
                Cb = Marshal.SizeOf<NativeDisplayDevice>(),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceID = string.Empty,
                DeviceKey = string.Empty
            };
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeDevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
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

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
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

        public static NativeDevMode Create()
        {
            return new NativeDevMode
            {
                Size = (ushort)Marshal.SizeOf<NativeDevMode>(),
                DeviceName = string.Empty,
                FormName = string.Empty
            };
        }
    }
}

internal sealed record VirtualDisplayModeRequest(
    string Resolution,
    int Width,
    int Height,
    string RefreshRateValue,
    int RefreshRate);

internal sealed record VirtualDisplayMode(int Width, int Height, int RefreshRate);

internal sealed record VirtualDisplayModeApplyResult(
    bool Success,
    string Summary,
    VirtualDisplayMode? CurrentMode)
{
    public static VirtualDisplayModeApplyResult Succeeded(string summary, VirtualDisplayMode currentMode)
    {
        return new VirtualDisplayModeApplyResult(true, summary, currentMode);
    }

    public static VirtualDisplayModeApplyResult Failed(string summary, VirtualDisplayMode? currentMode)
    {
        return new VirtualDisplayModeApplyResult(false, summary, currentMode);
    }
}
