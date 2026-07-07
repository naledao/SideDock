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
    private const uint QdcDatabaseCurrent = 0x00000004;
    private const uint SdcTopologyClone = 0x00000002;
    private const uint SdcTopologyExtend = 0x00000004;
    private const uint SdcTopologyExternal = 0x00000008;
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

    public static VirtualDisplayPresentationState GetPresentationState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new VirtualDisplayPresentationState(
                VirtualDisplayPresentationMode.Unknown,
                "显示模式检测仅支持 Windows。");
        }

        var snapshotResult = TryCaptureActiveDisplayTopology(out var snapshot, out var message);
        if (snapshotResult != ErrorSuccess || snapshot is null)
        {
            return new VirtualDisplayPresentationState(
                VirtualDisplayPresentationMode.Unknown,
                $"无法读取当前显示拓扑：{message}");
        }

        return BuildPresentationState(snapshot);
    }

    public static VirtualDisplayPresentationApplyResult ApplyPresentationMode(VirtualDisplayPresentationMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                "显示模式切换仅支持 Windows。",
                VirtualDisplayPresentationMode.Unknown);
        }

        if (mode == VirtualDisplayPresentationMode.Mirror)
        {
            return ApplyMirrorPresentationMode();
        }

        if (mode == VirtualDisplayPresentationMode.SecondaryOnly)
        {
            return ApplySecondaryOnlyPresentationMode();
        }

        if (mode != VirtualDisplayPresentationMode.Extend)
        {
            var currentState = GetPresentationState();
            return VirtualDisplayPresentationApplyResult.Failed(
                "未知显示模式，已拒绝修改显示拓扑。",
                currentState.Mode);
        }

        var beforeResult = TryCaptureActiveDisplayTopology(out var before, out var beforeMessage);
        if (beforeResult != ErrorSuccess || before is null)
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                $"扩展模式切换前无法读取当前显示拓扑：{beforeMessage}",
                VirtualDisplayPresentationMode.Unknown);
        }

        var adapterSearch = FindSideDockAdapter(includeInactive: true);
        if (adapterSearch.Adapter is null)
        {
            return VirtualDisplayPresentationApplyResult.Failed(adapterSearch.FailureSummary, before.Mode);
        }

        if (adapterSearch.Adapter.IsPrimary)
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                $"检测到 SideDock 匹配项当前是主屏（{adapterSearch.Adapter.DisplayName} / {adapterSearch.Adapter.DeviceName}），已拒绝修改显示拓扑。",
                before.Mode);
        }

        if (before.IsExtendedDesktopWithSideDock)
        {
            return VirtualDisplayPresentationApplyResult.Succeeded(
                "当前已是扩展模式。",
                VirtualDisplayPresentationMode.Extend);
        }

        if (before.ProtectedSourceNames.Count == 0)
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                "未检测到可保护的当前主屏或其它活动显示器，已拒绝切换为扩展模式。",
                before.Mode);
        }

        if (string.IsNullOrWhiteSpace(before.PrimaryDeviceName)
            || !before.HasActiveSourceName(before.PrimaryDeviceName))
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                $"切换前无法确认当前主屏仍处于活动路径中，已拒绝修改显示拓扑。当前路径：{before.FormatActivePaths()}",
                before.Mode);
        }

        var applyResult = SetDisplayConfig(
            0,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            SdcTopologyExtend | SdcApply | SdcSaveToDatabase | SdcAllowChanges);
        if (applyResult != ErrorSuccess)
        {
            var restoreSummary = TryRestoreDisplayTopology(before);
            var currentState = GetPresentationState();
            return VirtualDisplayPresentationApplyResult.Failed(
                $"扩展模式切换失败：SetDisplayConfig={applyResult}。{restoreSummary}",
                currentState.Mode);
        }

        var afterResult = WaitForActiveDisplayTopology(out var after, out var afterMessage);
        if (afterResult != ErrorSuccess || after is null)
        {
            var restoreSummary = TryRestoreDisplayTopology(before);
            var currentState = GetPresentationState();
            return VirtualDisplayPresentationApplyResult.Failed(
                $"扩展模式切换后无法确认显示拓扑：{afterMessage}。{restoreSummary}",
                currentState.Mode);
        }

        var validationFailure = ValidateExtendedTopologyResult(before, after);
        if (!string.IsNullOrWhiteSpace(validationFailure))
        {
            var restoreSummary = TryRestoreDisplayTopology(before);
            var currentState = GetPresentationState();
            return VirtualDisplayPresentationApplyResult.Failed(
                $"{validationFailure}。{restoreSummary}",
                currentState.Mode);
        }

        return VirtualDisplayPresentationApplyResult.Succeeded(
            "已切换为扩展模式。",
            VirtualDisplayPresentationMode.Extend);
    }

    public static VirtualDisplayPresentationApplyResult RestorePresentationMode(
        PresentationRollbackToken rollbackToken,
        string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                "显示拓扑恢复仅支持 Windows。",
                VirtualDisplayPresentationMode.Unknown,
                BuildDiagnosticSummary(
                    rollbackToken.TargetMode,
                    rollbackToken.OriginalTopologySummary,
                    "当前系统不是 Windows，未执行恢复。",
                    null,
                    rollbackToken.SideDockMatchSummary,
                    reason));
        }

        var restoreResult = rollbackToken.Restore();
        var currentState = GetPresentationState();
        var currentSummary = GetCurrentTopologyDiagnosticSummary();
        var diagnostic = BuildDiagnosticSummary(
            rollbackToken.TargetMode,
            rollbackToken.OriginalTopologySummary,
            currentSummary,
            restoreResult.WinApiReturnCode,
            rollbackToken.SideDockMatchSummary,
            $"{reason}{Environment.NewLine}{restoreResult.Summary}");

        return restoreResult.Success
            ? VirtualDisplayPresentationApplyResult.Succeeded(
                "已恢复切换前的显示拓扑。",
                currentState.Mode,
                diagnostic)
            : VirtualDisplayPresentationApplyResult.Failed(
                $"恢复切换前显示拓扑失败：{restoreResult.Summary}",
                currentState.Mode,
                diagnostic);
    }

    public static VirtualDisplayPresentationApplyResult CommitPresentationMode(
        PresentationRollbackToken rollbackToken,
        string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                "显示拓扑保存仅支持 Windows。",
                VirtualDisplayPresentationMode.Unknown,
                BuildDiagnosticSummary(
                    rollbackToken.TargetMode,
                    rollbackToken.OriginalTopologySummary,
                    "当前系统不是 Windows，未执行保存。",
                    null,
                    rollbackToken.SideDockMatchSummary,
                    reason));
        }

        var captureResult = TryCaptureActiveDisplayTopology(out var current, out var currentMessage);
        if (captureResult != ErrorSuccess || current is null)
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                $"无法读取当前显示拓扑，未保存临时拓扑：{currentMessage}",
                VirtualDisplayPresentationMode.Unknown,
                BuildDiagnosticSummary(
                    rollbackToken.TargetMode,
                    rollbackToken.OriginalTopologySummary,
                    $"读取当前拓扑失败：{currentMessage}",
                    captureResult,
                    rollbackToken.SideDockMatchSummary,
                    reason));
        }

        if (current.Mode != rollbackToken.TargetMode)
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                $"当前拓扑不是待确认的 {PresentationModeDiagnosticLabel(rollbackToken.TargetMode)}，未保存临时拓扑。",
                current.Mode,
                BuildDiagnosticSummary(
                    rollbackToken.TargetMode,
                    rollbackToken.OriginalTopologySummary,
                    current.FormatDiagnosticSummary(),
                    null,
                    rollbackToken.SideDockMatchSummary,
                    reason));
        }

        var validateResult = SetDisplayConfig(
            (uint)current.Paths.Length,
            current.Paths,
            (uint)current.Modes.Length,
            current.Modes,
            SdcUseSuppliedDisplayConfig | SdcValidate | SdcAllowChanges);
        if (validateResult != ErrorSuccess)
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                $"当前临时拓扑预验证失败，未保存：SetDisplayConfig(validate)={validateResult}。",
                current.Mode,
                BuildDiagnosticSummary(
                    rollbackToken.TargetMode,
                    rollbackToken.OriginalTopologySummary,
                    current.FormatDiagnosticSummary(),
                    validateResult,
                    rollbackToken.SideDockMatchSummary,
                    $"{reason}{Environment.NewLine}Validate 失败，未调用保存。"));
        }

        var saveResult = SetDisplayConfig(
            (uint)current.Paths.Length,
            current.Paths,
            (uint)current.Modes.Length,
            current.Modes,
            SdcUseSuppliedDisplayConfig | SdcApply | SdcSaveToDatabase | SdcAllowChanges);
        var savedState = GetPresentationState();
        var diagnostic = BuildDiagnosticSummary(
            rollbackToken.TargetMode,
            rollbackToken.OriginalTopologySummary,
            GetCurrentTopologyDiagnosticSummary(),
            saveResult,
            rollbackToken.SideDockMatchSummary,
            $"{reason}{Environment.NewLine}Validate={validateResult}; Save={saveResult}.");
        return saveResult == ErrorSuccess
            ? VirtualDisplayPresentationApplyResult.Succeeded(
                $"已保存{PresentationModeDiagnosticLabel(rollbackToken.TargetMode)}模式。",
                rollbackToken.TargetMode,
                diagnostic)
            : VirtualDisplayPresentationApplyResult.Failed(
                $"保存临时拓扑失败：SetDisplayConfig(save)={saveResult}。",
                savedState.Mode,
                diagnostic);
    }

    private static VirtualDisplayPresentationApplyResult ApplyMirrorPresentationMode()
    {
        var beforeResult = TryCaptureActiveDisplayTopology(out var before, out var beforeMessage);
        if (beforeResult != ErrorSuccess || before is null)
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                $"镜像模式切换前无法读取当前显示拓扑：{beforeMessage}",
                VirtualDisplayPresentationMode.Unknown,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.Mirror,
                    $"读取原拓扑失败：{beforeMessage}",
                    GetCurrentTopologyDiagnosticSummary(),
                    beforeResult,
                    "SideDock 匹配信息不可用。",
                    "预检失败，未调用 SetDisplayConfig。"));
        }

        if (!TryResolvePresentationTargets(
                before,
                requirePrimary: true,
                out var primaryPath,
                out var sideDockPath,
                out var targetFailure,
                out var sideDockMatchSummary)
            || primaryPath is null
            || sideDockPath is null)
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                targetFailure,
                before.Mode,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.Mirror,
                    before.FormatDiagnosticSummary(),
                    GetCurrentTopologyDiagnosticSummary(),
                    null,
                    sideDockMatchSummary,
                    "预检失败，未调用 SetDisplayConfig。"));
        }

        var unsafeClone = before.ActivePaths.Any(path =>
            !path.IsSideDock
            && path.TargetKey != primaryPath.TargetKey
            && path.SourceKey == primaryPath.SourceKey);
        if (unsafeClone)
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                "检测到其它非 SideDock 显示器已经与主屏共享同一个源，无法保证只镜像主屏和 SideDock，已拒绝切换。",
                before.Mode,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.Mirror,
                    before.FormatDiagnosticSummary(),
                    GetCurrentTopologyDiagnosticSummary(),
                    null,
                    sideDockMatchSummary,
                    "预检失败，未调用 SetDisplayConfig。"));
        }

        if (IsPrimarySideDockMirror(before, primaryPath, sideDockPath))
        {
            return VirtualDisplayPresentationApplyResult.Succeeded(
                "当前已是镜像模式。",
                VirtualDisplayPresentationMode.Mirror,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.Mirror,
                    before.FormatDiagnosticSummary(),
                    before.FormatDiagnosticSummary(),
                    null,
                    sideDockMatchSummary,
                    "未调用 SetDisplayConfig。"));
        }

        var compatibilityFailure = ValidateSideDockCanMirrorPrimary(
            primaryPath,
            sideDockPath,
            out var primaryModeRequest,
            out var modeMatchSummary);
        sideDockMatchSummary = $"{sideDockMatchSummary}{Environment.NewLine}{modeMatchSummary}";
        if (!string.IsNullOrWhiteSpace(compatibilityFailure) || primaryModeRequest is null)
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                compatibilityFailure ?? "无法确认 SideDock 与主屏模式兼容，已拒绝切换。",
                before.Mode,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.Mirror,
                    before.FormatDiagnosticSummary(),
                    GetCurrentTopologyDiagnosticSummary(),
                    null,
                    sideDockMatchSummary,
                    "预检失败，未调用 SetDisplayConfig。"));
        }

        if (!TryBuildMirrorDisplayConfig(
                before,
                primaryPath,
                sideDockPath,
                primaryModeRequest,
                out var updatedPaths,
                out var updatedModes,
                out var buildFailure))
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                buildFailure,
                before.Mode,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.Mirror,
                    before.FormatDiagnosticSummary(),
                    GetCurrentTopologyDiagnosticSummary(),
                    null,
                    sideDockMatchSummary,
                    "预检失败，未调用 SetDisplayConfig。"));
        }

        var validateResult = SetDisplayConfig(
            (uint)updatedPaths.Length,
            updatedPaths,
            (uint)updatedModes.Length,
            updatedModes,
            SdcUseSuppliedDisplayConfig | SdcValidate | SdcAllowChanges);
        if (validateResult != ErrorSuccess)
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                $"镜像模式预验证失败：SetDisplayConfig(validate)={validateResult}。",
                before.Mode,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.Mirror,
                    before.FormatDiagnosticSummary(),
                    GetCurrentTopologyDiagnosticSummary(),
                    validateResult,
                    sideDockMatchSummary,
                    "Validate 失败，未调用 Apply。"));
        }

        var applyResult = SetDisplayConfig(
            (uint)updatedPaths.Length,
            updatedPaths,
            (uint)updatedModes.Length,
            updatedModes,
            SdcUseSuppliedDisplayConfig | SdcApply | SdcSaveToDatabase | SdcAllowChanges);
        if (applyResult != ErrorSuccess)
        {
            var restoreResult = RestoreDisplayTopology(before);
            var currentState = GetPresentationState();
            return VirtualDisplayPresentationApplyResult.Failed(
                $"镜像模式切换失败：SetDisplayConfig(apply)={applyResult}。{restoreResult.Summary}",
                currentState.Mode,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.Mirror,
                    before.FormatDiagnosticSummary(),
                    GetCurrentTopologyDiagnosticSummary(),
                    applyResult,
                    sideDockMatchSummary,
                    $"Apply 失败，随后尝试恢复：{restoreResult.Summary}"));
        }

        var afterResult = WaitForActiveDisplayTopology(out var after, out var afterMessage);
        if (afterResult != ErrorSuccess || after is null)
        {
            var restoreResult = RestoreDisplayTopology(before);
            var currentState = GetPresentationState();
            return VirtualDisplayPresentationApplyResult.Failed(
                $"镜像模式切换后无法确认显示拓扑：{afterMessage}。{restoreResult.Summary}",
                currentState.Mode,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.Mirror,
                    before.FormatDiagnosticSummary(),
                    GetCurrentTopologyDiagnosticSummary(),
                    afterResult,
                    sideDockMatchSummary,
                    $"Apply 已返回成功，但确认拓扑失败，随后尝试恢复：{restoreResult.Summary}"));
        }

        var validationFailure = ValidateMirrorTopologyResult(before, after, primaryPath, sideDockPath);
        if (!string.IsNullOrWhiteSpace(validationFailure))
        {
            var restoreResult = RestoreDisplayTopology(before);
            var currentState = GetPresentationState();
            return VirtualDisplayPresentationApplyResult.Failed(
                $"{validationFailure}。{restoreResult.Summary}",
                currentState.Mode,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.Mirror,
                    before.FormatDiagnosticSummary(),
                    after.FormatDiagnosticSummary(),
                    applyResult,
                    sideDockMatchSummary,
                    $"Apply 后安全校验失败，随后尝试恢复：{restoreResult.Summary}"));
        }

        return VirtualDisplayPresentationApplyResult.Succeeded(
            "已切换为镜像模式。",
            VirtualDisplayPresentationMode.Mirror,
            BuildDiagnosticSummary(
                VirtualDisplayPresentationMode.Mirror,
                before.FormatDiagnosticSummary(),
                after.FormatDiagnosticSummary(),
                applyResult,
                sideDockMatchSummary,
                "Validate 和 Apply 均成功。"));
    }

    private static VirtualDisplayPresentationApplyResult ApplySecondaryOnlyPresentationMode()
    {
        var beforeResult = TryCaptureActiveDisplayTopology(out var before, out var beforeMessage);
        if (beforeResult != ErrorSuccess || before is null)
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                $"仅副屏模式切换前无法读取当前显示拓扑：{beforeMessage}",
                VirtualDisplayPresentationMode.Unknown,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.SecondaryOnly,
                    $"读取原拓扑失败：{beforeMessage}",
                    GetCurrentTopologyDiagnosticSummary(),
                    beforeResult,
                    "SideDock 匹配信息不可用。",
                    "预检失败，未调用 SetDisplayConfig。"));
        }

        if (before.Mode == VirtualDisplayPresentationMode.SecondaryOnly)
        {
            return VirtualDisplayPresentationApplyResult.Succeeded(
                "当前已是仅副屏模式。",
                VirtualDisplayPresentationMode.SecondaryOnly,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.SecondaryOnly,
                    before.FormatDiagnosticSummary(),
                    before.FormatDiagnosticSummary(),
                    null,
                    FormatSideDockMatchSummary(before, before.SideDockPaths.ToArray()),
                    "未调用 SetDisplayConfig。"));
        }

        if (!TryResolvePresentationTargets(
                before,
                requirePrimary: true,
                out var primaryPath,
                out var sideDockPath,
                out var targetFailure,
                out var sideDockMatchSummary)
            || primaryPath is null
            || sideDockPath is null)
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                targetFailure,
                before.Mode,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.SecondaryOnly,
                    before.FormatDiagnosticSummary(),
                    GetCurrentTopologyDiagnosticSummary(),
                    null,
                    sideDockMatchSummary,
                    "预检失败，未调用 SetDisplayConfig。"));
        }

        var sideDockDisplaySearch = FindSideDockDisplay();
        if (sideDockDisplaySearch.Display is null
            || !sideDockDisplaySearch.Display.DeviceName.Equals(sideDockPath.SourceName, StringComparison.OrdinalIgnoreCase))
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                "SideDock 当前不是独立活动扩展源，无法安全切换为仅副屏。请先切换为扩展模式并确认 SideDock 可见。",
                before.Mode,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.SecondaryOnly,
                    before.FormatDiagnosticSummary(),
                    GetCurrentTopologyDiagnosticSummary(),
                    null,
                    sideDockMatchSummary,
                    $"预检失败，未调用 SetDisplayConfig。SideDock 源匹配：{sideDockDisplaySearch.FailureSummary}"));
        }

        if (!TryBuildSinglePathDisplayConfig(
                before,
                sideDockPath,
                out var updatedPaths,
                out var updatedModes,
                out var buildFailure))
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                buildFailure,
                before.Mode,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.SecondaryOnly,
                    before.FormatDiagnosticSummary(),
                    GetCurrentTopologyDiagnosticSummary(),
                    null,
                    sideDockMatchSummary,
                    "预检失败，未调用 SetDisplayConfig。"));
        }

        var validateResult = SetDisplayConfig(
            (uint)updatedPaths.Length,
            updatedPaths,
            (uint)updatedModes.Length,
            updatedModes,
            SdcUseSuppliedDisplayConfig | SdcValidate | SdcAllowChanges);
        if (validateResult != ErrorSuccess)
        {
            return VirtualDisplayPresentationApplyResult.Failed(
                $"仅副屏模式预验证失败：SetDisplayConfig(validate)={validateResult}。",
                before.Mode,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.SecondaryOnly,
                    before.FormatDiagnosticSummary(),
                    GetCurrentTopologyDiagnosticSummary(),
                    validateResult,
                    sideDockMatchSummary,
                    "Validate 失败，未调用 Apply。"));
        }

        var applyResult = SetDisplayConfig(
            (uint)updatedPaths.Length,
            updatedPaths,
            (uint)updatedModes.Length,
            updatedModes,
            SdcUseSuppliedDisplayConfig | SdcApply | SdcAllowChanges);
        if (applyResult != ErrorSuccess)
        {
            var restoreResult = RestoreDisplayTopology(before);
            var currentState = GetPresentationState();
            return VirtualDisplayPresentationApplyResult.Failed(
                $"仅副屏模式切换失败：SetDisplayConfig(apply)={applyResult}。{restoreResult.Summary}",
                currentState.Mode,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.SecondaryOnly,
                    before.FormatDiagnosticSummary(),
                    GetCurrentTopologyDiagnosticSummary(),
                    applyResult,
                    sideDockMatchSummary,
                    $"Apply 失败，随后尝试恢复：{restoreResult.Summary}"));
        }

        var afterResult = WaitForActiveDisplayTopology(out var after, out var afterMessage);
        if (afterResult != ErrorSuccess || after is null)
        {
            var restoreResult = RestoreDisplayTopology(before);
            var currentState = GetPresentationState();
            return VirtualDisplayPresentationApplyResult.Failed(
                $"仅副屏模式切换后无法确认显示拓扑：{afterMessage}。{restoreResult.Summary}",
                currentState.Mode,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.SecondaryOnly,
                    before.FormatDiagnosticSummary(),
                    GetCurrentTopologyDiagnosticSummary(),
                    afterResult,
                    sideDockMatchSummary,
                    $"Apply 已返回成功，但确认拓扑失败，随后尝试恢复：{restoreResult.Summary}"));
        }

        var validationFailure = ValidateSecondaryOnlyTopologyResult(after, sideDockPath);
        if (!string.IsNullOrWhiteSpace(validationFailure))
        {
            var restoreResult = RestoreDisplayTopology(before);
            var currentState = GetPresentationState();
            return VirtualDisplayPresentationApplyResult.Failed(
                $"{validationFailure}。{restoreResult.Summary}",
                currentState.Mode,
                BuildDiagnosticSummary(
                    VirtualDisplayPresentationMode.SecondaryOnly,
                    before.FormatDiagnosticSummary(),
                    after.FormatDiagnosticSummary(),
                    applyResult,
                    sideDockMatchSummary,
                    $"Apply 后安全校验失败，随后尝试恢复：{restoreResult.Summary}"));
        }

        var rollbackToken = new PresentationRollbackToken(
            VirtualDisplayPresentationMode.SecondaryOnly,
            before,
            sideDockMatchSummary);
        return VirtualDisplayPresentationApplyResult.Succeeded(
            "已临时切换为仅副屏模式，请确认是否保留此设置。",
            VirtualDisplayPresentationMode.SecondaryOnly,
            BuildDiagnosticSummary(
                VirtualDisplayPresentationMode.SecondaryOnly,
                before.FormatDiagnosticSummary(),
                after.FormatDiagnosticSummary(),
                applyResult,
                sideDockMatchSummary,
                "Validate 和 Apply 均成功，等待用户确认保留。"),
            rollbackToken);
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

    private static bool TryResolvePresentationTargets(
        DisplayTopologySnapshot snapshot,
        bool requirePrimary,
        out ActiveDisplayPathSnapshot? primaryPath,
        out ActiveDisplayPathSnapshot? sideDockPath,
        out string failure,
        out string sideDockMatchSummary)
    {
        primaryPath = snapshot.PrimaryPath;
        var sideDockPaths = snapshot.SideDockPaths.ToArray();
        sideDockPath = sideDockPaths.Length == 1 ? sideDockPaths[0] : null;
        sideDockMatchSummary = FormatSideDockMatchSummary(snapshot, sideDockPaths);

        if (sideDockPaths.Length == 0)
        {
            var adapterSearch = FindSideDockAdapter(includeInactive: true);
            failure = adapterSearch.Adapter is null
                ? "未检测到 SideDock 虚拟显示器，已拒绝修改显示拓扑。"
                : $"检测到 SideDock 虚拟显示器（{adapterSearch.Adapter.DisplayName} / {adapterSearch.Adapter.DeviceName}），但它当前未启用，已拒绝修改显示拓扑。";
            sideDockMatchSummary = adapterSearch.Adapter is null
                ? $"{sideDockMatchSummary}{Environment.NewLine}{adapterSearch.FailureSummary}"
                : $"{sideDockMatchSummary}{Environment.NewLine}EnumDisplayDevices: {adapterSearch.Adapter.DisplayName} / {adapterSearch.Adapter.DeviceName}, active={adapterSearch.Adapter.IsActive}, primary={adapterSearch.Adapter.IsPrimary}.";
            return false;
        }

        if (sideDockPaths.Length > 1)
        {
            failure = "检测到多个 SideDock 活动路径，无法唯一确认目标虚拟显示器，已拒绝修改显示拓扑。";
            return false;
        }

        if (sideDockPath?.IsPrimary == true)
        {
            failure = $"SideDock 当前被识别为主屏（{sideDockPath.DisplayName} / {sideDockPath.SourceName}），已拒绝修改显示拓扑。";
            return false;
        }

        if (requirePrimary && primaryPath is null)
        {
            failure = $"未能在活动路径中确认当前主屏（PrimaryDeviceName={snapshot.PrimaryDeviceName}），已拒绝修改显示拓扑。";
            return false;
        }

        if (requirePrimary
            && primaryPath is not null
            && sideDockPath is not null
            && primaryPath.TargetKey == sideDockPath.TargetKey)
        {
            failure = "主屏和 SideDock 指向同一个显示目标，无法安全修改显示拓扑。";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static bool IsPrimarySideDockMirror(
        DisplayTopologySnapshot snapshot,
        ActiveDisplayPathSnapshot primaryPath,
        ActiveDisplayPathSnapshot sideDockPath)
    {
        return snapshot.Mode == VirtualDisplayPresentationMode.Mirror
            && primaryPath.SourceKey == sideDockPath.SourceKey
            && !snapshot.ActivePaths.Any(path =>
                !path.IsSideDock
                && path.TargetKey != primaryPath.TargetKey
                && path.SourceKey == primaryPath.SourceKey);
    }

    private static string? ValidateSideDockCanMirrorPrimary(
        ActiveDisplayPathSnapshot primaryPath,
        ActiveDisplayPathSnapshot sideDockPath,
        out VirtualDisplayModeRequest? primaryModeRequest,
        out string sideDockModeSummary)
    {
        primaryModeRequest = null;
        sideDockModeSummary = "SideDock 模式兼容性尚未确认。";

        if (!TryGetCurrentDevMode(primaryPath.SourceName, out var primaryMode, out var primaryModeMessage))
        {
            return $"无法读取当前主屏模式：{primaryModeMessage}";
        }

        if (primaryMode.PelsWidth == 0 || primaryMode.PelsHeight == 0 || primaryMode.DisplayFrequency == 0)
        {
            return $"当前主屏模式不完整，无法确认 SideDock 是否兼容：{FormatDevMode(primaryMode)}";
        }

        primaryModeRequest = new VirtualDisplayModeRequest(
            "primary",
            (int)primaryMode.PelsWidth,
            (int)primaryMode.PelsHeight,
            primaryMode.DisplayFrequency.ToString(CultureInfo.InvariantCulture),
            (int)primaryMode.DisplayFrequency);

        var sideDockDisplaySearch = FindSideDockDisplay();
        if (sideDockDisplaySearch.Display is null)
        {
            sideDockModeSummary = $"无法枚举 SideDock 当前显示源：{sideDockDisplaySearch.FailureSummary}";
            return "SideDock 当前不是独立活动扩展源，无法安全验证镜像模式兼容性。请先切换为扩展模式并确认 SideDock 可见。";
        }

        if (!sideDockDisplaySearch.Display.DeviceName.Equals(sideDockPath.SourceName, StringComparison.OrdinalIgnoreCase))
        {
            sideDockModeSummary =
                $"SideDock 活动目标={sideDockPath.DisplayName}/{sideDockPath.SourceName}; "
                + $"EnumDisplayDevices 匹配={sideDockDisplaySearch.Display.DisplayName}/{sideDockDisplaySearch.Display.DeviceName}.";
            return "SideDock 当前不是独立活动扩展源，无法安全验证镜像模式兼容性。请先切换为扩展模式并确认 SideDock 可见。";
        }

        var advertisedModes = EnumerateDisplayModes(sideDockDisplaySearch.Display.DeviceName);
        var selectedMode = SelectDisplayMode(advertisedModes, primaryModeRequest);
        sideDockModeSummary =
            $"SideDock 匹配={sideDockDisplaySearch.Display.DisplayName}/{sideDockDisplaySearch.Display.DeviceName}; "
            + $"current={FormatMode(sideDockDisplaySearch.Display.CurrentMode)}; "
            + $"target={FormatRequest(primaryModeRequest)}; "
            + FormatAdvertisedModes(advertisedModes, primaryModeRequest.Width, primaryModeRequest.Height);
        if (selectedMode is null)
        {
            return $"主屏当前模式 {FormatRequest(primaryModeRequest)} 不受 SideDock 虚拟显示器支持，已拒绝镜像。";
        }

        return null;
    }

    private static bool TryBuildMirrorDisplayConfig(
        DisplayTopologySnapshot snapshot,
        ActiveDisplayPathSnapshot primaryPathSnapshot,
        ActiveDisplayPathSnapshot sideDockPathSnapshot,
        VirtualDisplayModeRequest primaryModeRequest,
        out DisplayConfigPathInfo[] paths,
        out DisplayConfigModeInfo[] modes,
        out string failure)
    {
        paths = snapshot.Paths.ToArray();
        modes = snapshot.Modes.ToArray();

        if (!TryGetPathAt(snapshot, primaryPathSnapshot, out var primaryPath)
            || !TryGetPathAt(snapshot, sideDockPathSnapshot, out var sideDockPath))
        {
            failure = "无法从原始拓扑中定位主屏或 SideDock 活动路径。";
            return false;
        }

        var primarySourceModeIndex = EnsureDisplayConfigSourceMode(
            modes,
            primaryPath.sourceInfo.adapterId,
            primaryPath.sourceInfo.id);
        var sideDockTargetModeIndex = EnsureDisplayConfigTargetMode(
            modes,
            sideDockPath.targetInfo.adapterId,
            sideDockPath.targetInfo.id);
        if (primarySourceModeIndex < 0 || sideDockTargetModeIndex < 0)
        {
            failure =
                "无法定位镜像所需的源/目标模式索引："
                + $"primarySourceIdx={primarySourceModeIndex}, sideDockTargetIdx={sideDockTargetModeIndex}。";
            return false;
        }

        sideDockPath.sourceInfo.adapterId = primaryPath.sourceInfo.adapterId;
        sideDockPath.sourceInfo.id = primaryPath.sourceInfo.id;
        sideDockPath.sourceInfo.modeInfoIdx = (uint)primarySourceModeIndex;
        sideDockPath.sourceInfo.statusFlags = primaryPath.sourceInfo.statusFlags;
        sideDockPath.targetInfo.modeInfoIdx = (uint)sideDockTargetModeIndex;
        sideDockPath.targetInfo.refreshRate.Numerator = (uint)primaryModeRequest.RefreshRate;
        sideDockPath.targetInfo.refreshRate.Denominator = 1;
        sideDockPath.targetInfo.scanLineOrdering = DisplayConfigScanlineOrderingProgressive;
        sideDockPath.flags |= DisplayConfigPathActive;
        paths[sideDockPathSnapshot.Index] = sideDockPath;

        modes[primarySourceModeIndex].sourceMode.width = (uint)primaryModeRequest.Width;
        modes[primarySourceModeIndex].sourceMode.height = (uint)primaryModeRequest.Height;
        modes[primarySourceModeIndex].sourceMode.pixelFormat = DisplayConfigPixelFormat32Bpp;
        UpdateTargetModeSignal(
            ref modes[sideDockTargetModeIndex],
            primaryModeRequest.Width,
            primaryModeRequest.Height,
            primaryModeRequest.RefreshRate);

        failure = string.Empty;
        return true;
    }

    private static bool TryBuildSinglePathDisplayConfig(
        DisplayTopologySnapshot snapshot,
        ActiveDisplayPathSnapshot pathSnapshot,
        out DisplayConfigPathInfo[] paths,
        out DisplayConfigModeInfo[] modes,
        out string failure)
    {
        paths = [];
        modes = [];

        if (!TryGetPathAt(snapshot, pathSnapshot, out var path))
        {
            failure = "无法从原始拓扑中定位 SideDock 活动路径。";
            return false;
        }

        var sourceModeIndex = EnsureDisplayConfigSourceMode(
            snapshot.Modes,
            path.sourceInfo.adapterId,
            path.sourceInfo.id);
        var targetModeIndex = EnsureDisplayConfigTargetMode(
            snapshot.Modes,
            path.targetInfo.adapterId,
            path.targetInfo.id);
        if (sourceModeIndex < 0 || targetModeIndex < 0)
        {
            failure =
                "无法定位仅副屏所需的源/目标模式索引："
                + $"sourceIdx={sourceModeIndex}, targetIdx={targetModeIndex}。";
            return false;
        }

        path.sourceInfo.modeInfoIdx = 0;
        path.targetInfo.modeInfoIdx = 1;
        path.flags |= DisplayConfigPathActive;
        paths = [path];
        modes = [snapshot.Modes[sourceModeIndex], snapshot.Modes[targetModeIndex]];
        failure = string.Empty;
        return true;
    }

    private static bool TryGetPathAt(
        DisplayTopologySnapshot snapshot,
        ActiveDisplayPathSnapshot pathSnapshot,
        out DisplayConfigPathInfo path)
    {
        if (pathSnapshot.Index >= 0 && pathSnapshot.Index < snapshot.Paths.Length)
        {
            path = snapshot.Paths[pathSnapshot.Index];
            return true;
        }

        path = default;
        return false;
    }

    private static string? ValidateMirrorTopologyResult(
        DisplayTopologySnapshot before,
        DisplayTopologySnapshot after,
        ActiveDisplayPathSnapshot beforePrimaryPath,
        ActiveDisplayPathSnapshot beforeSideDockPath)
    {
        var afterPrimaryPath = after.FindByTargetKey(beforePrimaryPath.TargetKey);
        if (afterPrimaryPath is null)
        {
            return $"镜像模式切换后未检测到原主屏目标路径（{beforePrimaryPath.DisplayName} / {beforePrimaryPath.TargetKey}）";
        }

        var afterSideDockPath = after.FindByTargetKey(beforeSideDockPath.TargetKey);
        if (afterSideDockPath is null || !afterSideDockPath.IsSideDock)
        {
            return $"镜像模式切换后未检测到 SideDock 目标路径（{beforeSideDockPath.DisplayName} / {beforeSideDockPath.TargetKey}）";
        }

        if (afterPrimaryPath.SourceKey != afterSideDockPath.SourceKey)
        {
            return $"镜像模式切换后主屏和 SideDock 未共享同一源：主屏={afterPrimaryPath.SourceKey}, SideDock={afterSideDockPath.SourceKey}";
        }

        var externalInCloneGroup = after.ActivePaths
            .Where(path =>
                !path.IsSideDock
                && path.TargetKey != afterPrimaryPath.TargetKey
                && path.SourceKey == afterPrimaryPath.SourceKey)
            .Select(path => $"{path.DisplayName}/{path.TargetKey}")
            .ToArray();
        if (externalInCloneGroup.Length > 0)
        {
            return $"镜像模式切换后检测到其它显示器被纳入主屏镜像组：{string.Join(", ", externalInCloneGroup)}";
        }

        var missingProtectedTargets = before.ProtectedTargetKeys
            .Where(targetKey => !after.HasActiveTargetKey(targetKey))
            .ToArray();
        if (missingProtectedTargets.Length > 0)
        {
            return $"镜像模式切换后原有非 SideDock 显示目标消失：{string.Join(", ", missingProtectedTargets)}";
        }

        if (after.Mode != VirtualDisplayPresentationMode.Mirror)
        {
            return $"镜像模式切换后未处于可确认的镜像拓扑，当前路径：{after.FormatActivePaths()}";
        }

        return null;
    }

    private static string? ValidateSecondaryOnlyTopologyResult(
        DisplayTopologySnapshot after,
        ActiveDisplayPathSnapshot beforeSideDockPath)
    {
        if (after.ActivePaths.Count != 1 || after.SideDockPaths.Count != 1)
        {
            return $"仅副屏模式切换后活动路径不是唯一 SideDock：{after.FormatActivePaths()}";
        }

        if (!after.HasActiveTargetKey(beforeSideDockPath.TargetKey))
        {
            return $"仅副屏模式切换后未检测到原 SideDock 目标路径（{beforeSideDockPath.TargetKey}）";
        }

        if (after.Mode != VirtualDisplayPresentationMode.SecondaryOnly)
        {
            return $"仅副屏模式切换后未处于可确认的仅副屏拓扑，当前路径：{after.FormatActivePaths()}";
        }

        return null;
    }

    private static void UpdateTargetModeSignal(
        ref DisplayConfigModeInfo modeInfo,
        int width,
        int height,
        int refreshRate)
    {
        var targetSignal = modeInfo.targetMode.targetVideoSignalInfo;
        targetSignal.activeSize.cx = (uint)width;
        targetSignal.activeSize.cy = (uint)height;
        if (targetSignal.totalSize.cx == 0 || targetSignal.totalSize.cx < targetSignal.activeSize.cx)
        {
            targetSignal.totalSize.cx = targetSignal.activeSize.cx;
        }

        if (targetSignal.totalSize.cy == 0 || targetSignal.totalSize.cy < targetSignal.activeSize.cy)
        {
            targetSignal.totalSize.cy = targetSignal.activeSize.cy;
        }

        targetSignal.vSyncFreq.Numerator = (uint)refreshRate;
        targetSignal.vSyncFreq.Denominator = 1;
        targetSignal.hSyncFreq.Numerator = (uint)(refreshRate * Math.Max(1, height));
        targetSignal.hSyncFreq.Denominator = 1;
        targetSignal.pixelRate = (ulong)refreshRate * (ulong)width * (ulong)height;
        targetSignal.scanLineOrdering = DisplayConfigScanlineOrderingProgressive;
        modeInfo.targetMode.targetVideoSignalInfo = targetSignal;
    }

    private static bool TryGetCurrentDevMode(string deviceName, out NativeDevMode mode, out string message)
    {
        mode = NativeDevMode.Create();
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            message = "设备名为空。";
            return false;
        }

        if (!EnumDisplaySettingsW(deviceName, EnumCurrentSettings, ref mode))
        {
            message = $"EnumDisplaySettingsW({deviceName}) 失败，win32={Marshal.GetLastWin32Error()}。";
            return false;
        }

        message = FormatDevMode(mode);
        return true;
    }

    private static DisplayTopologyRestoreResult RestoreDisplayTopology(DisplayTopologySnapshot snapshot)
    {
        try
        {
            var restoreResult = SetDisplayConfig(
                (uint)snapshot.Paths.Length,
                snapshot.Paths,
                (uint)snapshot.Modes.Length,
                snapshot.Modes,
                SdcUseSuppliedDisplayConfig | SdcApply | SdcSaveToDatabase | SdcAllowChanges);
            return restoreResult == ErrorSuccess
                ? new DisplayTopologyRestoreResult(true, "已尝试恢复切换前的显示拓扑。", restoreResult)
                : new DisplayTopologyRestoreResult(false, $"恢复切换前显示拓扑失败：SetDisplayConfig={restoreResult}。", restoreResult);
        }
        catch (Exception ex)
        {
            return new DisplayTopologyRestoreResult(false, $"恢复切换前显示拓扑时出错：{ex.Message}", null);
        }
    }

    private static string BuildDiagnosticSummary(
        VirtualDisplayPresentationMode targetMode,
        string originalTopologySummary,
        string currentTopologySummary,
        int? winApiReturnCode,
        string sideDockMatchSummary,
        string detail)
    {
        return string.Join(
            Environment.NewLine,
            $"切换目标：{PresentationModeDiagnosticLabel(targetMode)}",
            $"原拓扑摘要：{originalTopologySummary}",
            $"当前拓扑摘要：{currentTopologySummary}",
            $"WinAPI 返回码：{(winApiReturnCode.HasValue ? winApiReturnCode.Value.ToString(CultureInfo.InvariantCulture) : "未调用")}",
            $"SideDock 匹配信息：{sideDockMatchSummary}",
            $"诊断详情：{detail}");
    }

    private static string GetCurrentTopologyDiagnosticSummary()
    {
        var result = TryCaptureActiveDisplayTopology(out var snapshot, out var message);
        return result == ErrorSuccess && snapshot is not null
            ? snapshot.FormatDiagnosticSummary()
            : $"读取当前拓扑失败：{message} (code={result})";
    }

    private static string FormatSideDockMatchSummary(
        DisplayTopologySnapshot snapshot,
        IReadOnlyList<ActiveDisplayPathSnapshot> sideDockPaths)
    {
        var activeSummary = sideDockPaths.Count == 0
            ? "活动路径中未发现 SideDock。"
            : string.Join("; ", sideDockPaths.Select(path =>
                $"{path.DisplayName}, source={path.SourceName}, sourceKey={path.SourceKey}, target={path.TargetName}, targetKey={path.TargetKey}, primary={path.IsPrimary}"));
        return $"PrimaryDeviceName={snapshot.PrimaryDeviceName}; {activeSummary}";
    }

    private static string PresentationModeDiagnosticLabel(VirtualDisplayPresentationMode mode)
    {
        return mode switch
        {
            VirtualDisplayPresentationMode.Mirror => "镜像",
            VirtualDisplayPresentationMode.SecondaryOnly => "仅副屏",
            VirtualDisplayPresentationMode.Extend => "扩展",
            _ => "未知"
        };
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

    private static VirtualDisplayPresentationState BuildPresentationState(DisplayTopologySnapshot snapshot)
    {
        return snapshot.Mode switch
        {
            VirtualDisplayPresentationMode.Extend => new VirtualDisplayPresentationState(
                VirtualDisplayPresentationMode.Extend,
                "当前已是扩展桌面。"),
            VirtualDisplayPresentationMode.Mirror => new VirtualDisplayPresentationState(
                VirtualDisplayPresentationMode.Mirror,
                "当前处于镜像拓扑。"),
            VirtualDisplayPresentationMode.SecondaryOnly => new VirtualDisplayPresentationState(
                VirtualDisplayPresentationMode.SecondaryOnly,
                "当前仅 SideDock 虚拟显示器处于活动状态。"),
            _ => new VirtualDisplayPresentationState(
                VirtualDisplayPresentationMode.Unknown,
                snapshot.HasActiveSideDockPath
                    ? $"SideDock 虚拟显示器已启用，但当前拓扑不是可确认的扩展桌面：{snapshot.FormatActivePaths()}"
                    : "SideDock 虚拟显示器尚未作为活动扩展屏。")
        };
    }

    private static int TryCaptureActiveDisplayTopology(
        out DisplayTopologySnapshot? snapshot,
        out string message)
    {
        snapshot = null;
        var queryResult = TryQueryActiveDisplayConfig(out var paths, out var modes, out var queryMessage);
        if (queryResult != ErrorSuccess)
        {
            message = queryMessage;
            return queryResult;
        }

        var adapters = EnumerateDisplayAdapters();
        var activePaths = new List<ActiveDisplayPathSnapshot>();
        for (var index = 0; index < paths.Length; index++)
        {
            var path = paths[index];
            if ((path.flags & DisplayConfigPathActive) == 0)
            {
                continue;
            }

            var sourceNameResult = TryGetDisplayConfigSourceName(path, out var sourceName);
            if (sourceNameResult != ErrorSuccess)
            {
                sourceName = $"source:{FormatLuid(path.sourceInfo.adapterId)}:{path.sourceInfo.id}";
            }

            adapters.TryGetValue(sourceName, out var adapter);
            var targetNameResult = TryGetDisplayConfigTargetName(path, out var targetName, out var targetDevicePath);
            if (targetNameResult != ErrorSuccess)
            {
                targetName = $"target:{FormatLuid(path.targetInfo.adapterId)}:{path.targetInfo.id}";
                targetDevicePath = string.Empty;
            }

            var isSideDock = adapter?.SideDockScore > 0 || IsSideDockVirtualDisplay(targetName, targetDevicePath);
            var displayName = isSideDock
                ? FirstNonEmpty(targetName, adapter?.DisplayName, sourceName)
                : FirstNonEmpty(adapter?.DisplayName, targetName, sourceName);
            activePaths.Add(new ActiveDisplayPathSnapshot(
                index,
                sourceName,
                FormatSourceKey(path.sourceInfo.adapterId, path.sourceInfo.id),
                FormatSourceKey(path.targetInfo.adapterId, path.targetInfo.id),
                displayName,
                targetName,
                targetDevicePath,
                isSideDock,
                adapter?.IsPrimary == true,
                FormatDisplayConfigPath(path)));
        }

        var primaryDeviceName = adapters.Values.FirstOrDefault(adapter => adapter.IsPrimary)?.DeviceName ?? string.Empty;
        var topologyId = TryQueryCurrentTopologyId(out var topologyMessage);
        snapshot = new DisplayTopologySnapshot(
            paths,
            modes,
            activePaths,
            topologyId,
            topologyMessage,
            primaryDeviceName);
        message = $"{queryMessage}; topology={FormatTopologyId(topologyId)}; paths={snapshot.FormatActivePaths()}";
        return ErrorSuccess;
    }

    private static int WaitForActiveDisplayTopology(
        out DisplayTopologySnapshot? snapshot,
        out string message)
    {
        snapshot = null;
        message = string.Empty;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(150);
            }

            var result = TryCaptureActiveDisplayTopology(out snapshot, out message);
            if (result != ErrorSuccess)
            {
                continue;
            }

            if (snapshot?.HasActiveSideDockPath == true)
            {
                return ErrorSuccess;
            }
        }

        return snapshot is null ? -1 : ErrorSuccess;
    }

    private static string? ValidateExtendedTopologyResult(
        DisplayTopologySnapshot before,
        DisplayTopologySnapshot after)
    {
        if (!after.HasActiveSourceName(before.PrimaryDeviceName))
        {
            return $"扩展模式切换后未检测到原主屏活动路径（{before.PrimaryDeviceName}）";
        }

        var missingProtected = before.ProtectedSourceNames
            .Where(sourceName => !after.HasActiveSourceName(sourceName))
            .ToArray();
        if (missingProtected.Length > 0)
        {
            return $"扩展模式切换后原有非 SideDock 活动路径消失：{string.Join(", ", missingProtected)}";
        }

        var addedProtected = after.ProtectedSourceNames
            .Where(sourceName => !before.HasActiveSourceName(sourceName))
            .ToArray();
        if (addedProtected.Length > 0)
        {
            return $"扩展模式切换后检测到新的非 SideDock 活动路径：{string.Join(", ", addedProtected)}";
        }

        if (!after.HasActiveSideDockPath)
        {
            return "扩展模式切换后仍未检测到 SideDock 虚拟显示器活动路径";
        }

        if (!after.IsExtendedDesktopWithSideDock)
        {
            return $"扩展模式切换后未处于可确认的扩展桌面，当前路径：{after.FormatActivePaths()}";
        }

        return null;
    }

    private static string TryRestoreDisplayTopology(DisplayTopologySnapshot snapshot)
    {
        return RestoreDisplayTopology(snapshot).Summary;
    }

    private static DisplayAdapterSearchResult FindSideDockAdapter(bool includeInactive)
    {
        var candidates = EnumerateDisplayAdapters()
            .Values
            .Where(adapter =>
                adapter.SideDockScore > 0
                && !adapter.IsMirroringDriver
                && (includeInactive || adapter.IsActive))
            .OrderByDescending(adapter => adapter.SideDockScore)
            .ThenBy(adapter => adapter.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var primaryMatches = candidates
            .Where(adapter => adapter.IsPrimary)
            .Select(adapter => $"{adapter.DisplayName} {adapter.DeviceName}")
            .ToArray();
        if (primaryMatches.Length > 0)
        {
            return DisplayAdapterSearchResult.Failure(
                "检测到 SideDock 匹配项，但它当前是主屏。为避免误改主屏，已拒绝修改显示拓扑。"
                + $" 匹配项：{string.Join("; ", primaryMatches)}。");
        }

        var selected = candidates.FirstOrDefault();
        if (selected is not null)
        {
            return DisplayAdapterSearchResult.Success(selected);
        }

        return DisplayAdapterSearchResult.Failure("未检测到 SideDock 虚拟显示器，无法切换为扩展模式。");
    }

    private static IReadOnlyDictionary<string, DisplayAdapterSnapshot> EnumerateDisplayAdapters()
    {
        var adapters = new Dictionary<string, DisplayAdapterSnapshot>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; ; index++)
        {
            var adapter = NativeDisplayDevice.Create();
            if (!EnumDisplayDevicesW(null, index, ref adapter, 0))
            {
                break;
            }

            var deviceName = CleanString(adapter.DeviceName);
            if (string.IsNullOrWhiteSpace(deviceName))
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
            adapters[deviceName] = new DisplayAdapterSnapshot(
                deviceName,
                displayName,
                (adapter.StateFlags & DisplayDeviceActive) != 0,
                (adapter.StateFlags & DisplayDevicePrimaryDevice) != 0,
                (adapter.StateFlags & DisplayDeviceMirroringDriver) != 0,
                ScoreSideDockCandidate(adapter, hasMonitor ? monitor : null));
        }

        return adapters;
    }

    private static int TryGetDisplayConfigSourceName(DisplayConfigPathInfo path, out string sourceName)
    {
        var sourceNamePacket = DisplayConfigSourceDeviceName.Create(path.sourceInfo.adapterId, path.sourceInfo.id);
        var result = DisplayConfigGetDeviceInfo(ref sourceNamePacket);
        sourceName = result == ErrorSuccess ? CleanString(sourceNamePacket.viewGdiDeviceName) : string.Empty;
        return result;
    }

    private static int TryGetDisplayConfigTargetName(
        DisplayConfigPathInfo path,
        out string targetName,
        out string targetDevicePath)
    {
        var targetNamePacket = DisplayConfigTargetDeviceName.Create(path.targetInfo.adapterId, path.targetInfo.id);
        var result = DisplayConfigGetDeviceInfo(ref targetNamePacket);
        targetName = result == ErrorSuccess ? CleanString(targetNamePacket.monitorFriendlyDeviceName) : string.Empty;
        targetDevicePath = result == ErrorSuccess ? CleanString(targetNamePacket.monitorDevicePath) : string.Empty;
        return result;
    }

    private static uint? TryQueryCurrentTopologyId(out string message)
    {
        var sizeResult = GetDisplayConfigBufferSizes(
            QdcDatabaseCurrent,
            out var pathCount,
            out var modeCount);
        if (sizeResult != ErrorSuccess)
        {
            message = $"topologySize={sizeResult}";
            return null;
        }

        var paths = new DisplayConfigPathInfo[pathCount];
        var modes = new DisplayConfigModeInfo[modeCount];
        var queryPathCount = pathCount;
        var queryModeCount = modeCount;
        var topologyId = 0u;
        var queryResult = QueryDisplayConfig(
            QdcDatabaseCurrent,
            ref queryPathCount,
            paths,
            ref queryModeCount,
            modes,
            ref topologyId);
        if (queryResult != ErrorSuccess)
        {
            message = $"topologyQuery={queryResult}";
            return null;
        }

        message = $"topologyQuery=0";
        return topologyId;
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

    private static bool IsSideDockVirtualDisplay(params string?[] values)
    {
        return values.Any(value =>
            !string.IsNullOrWhiteSpace(value)
            && SideDockKeywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
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

    private static string FormatDevMode(NativeDevMode mode)
    {
        return $"{mode.PelsWidth.ToString(CultureInfo.InvariantCulture)} × {mode.PelsHeight.ToString(CultureInfo.InvariantCulture)} @ {mode.DisplayFrequency.ToString(CultureInfo.InvariantCulture)} Hz pos=({mode.PositionX.ToString(CultureInfo.InvariantCulture)}, {mode.PositionY.ToString(CultureInfo.InvariantCulture)}) bpp={mode.BitsPerPel.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string FormatSourceKey(Luid adapterId, uint id)
    {
        return $"{FormatLuid(adapterId)}:{id}";
    }

    private static string FormatLuid(Luid adapterId)
    {
        return $"{adapterId.HighPart:X8}:{adapterId.LowPart:X8}";
    }

    private static string FormatTopologyId(uint? topologyId)
    {
        return topologyId switch
        {
            1 => "internal",
            SdcTopologyClone => "clone",
            SdcTopologyExtend => "extend",
            SdcTopologyExternal => "external",
            null => "unknown",
            _ => $"0x{topologyId.Value:X}"
        };
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
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        ref uint currentTopologyId);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SetDisplayConfig(
        uint numPathArrayElements,
        [In] DisplayConfigPathInfo[] pathArray,
        uint numModeInfoArrayElements,
        [In] DisplayConfigModeInfo[] modeInfoArray,
        uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SetDisplayConfig(
        uint numPathArrayElements,
        IntPtr pathArray,
        uint numModeInfoArrayElements,
        IntPtr modeInfoArray,
        uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);

    internal sealed class PresentationRollbackToken
    {
        private readonly object _snapshot;

        internal PresentationRollbackToken(
            VirtualDisplayPresentationMode targetMode,
            object snapshot,
            string sideDockMatchSummary)
        {
            TargetMode = targetMode;
            _snapshot = snapshot;
            OriginalTopologySummary = ((DisplayTopologySnapshot)snapshot).FormatDiagnosticSummary();
            SideDockMatchSummary = sideDockMatchSummary;
        }

        public VirtualDisplayPresentationMode TargetMode { get; }

        public string OriginalTopologySummary { get; }

        public string SideDockMatchSummary { get; }

        internal (bool Success, string Summary, int? WinApiReturnCode) Restore()
        {
            var result = RestoreDisplayTopology((DisplayTopologySnapshot)_snapshot);
            return (result.Success, result.Summary, result.WinApiReturnCode);
        }
    }

    private sealed class DisplayTopologySnapshot
    {
        public DisplayTopologySnapshot(
            DisplayConfigPathInfo[] paths,
            DisplayConfigModeInfo[] modes,
            IReadOnlyList<ActiveDisplayPathSnapshot> activePaths,
            uint? topologyId,
            string topologyQueryMessage,
            string primaryDeviceName)
        {
            Paths = paths;
            Modes = modes;
            ActivePaths = activePaths;
            TopologyId = topologyId;
            TopologyQueryMessage = topologyQueryMessage;
            PrimaryDeviceName = primaryDeviceName;
            ProtectedSourceNames = activePaths
                .Where(path => !path.IsSideDock)
                .Select(path => path.SourceName)
                .Where(sourceName => !string.IsNullOrWhiteSpace(sourceName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ProtectedTargetKeys = activePaths
                .Where(path => !path.IsSideDock)
                .Select(path => path.TargetKey)
                .Where(targetKey => !string.IsNullOrWhiteSpace(targetKey))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        public DisplayConfigPathInfo[] Paths { get; }

        public DisplayConfigModeInfo[] Modes { get; }

        public IReadOnlyList<ActiveDisplayPathSnapshot> ActivePaths { get; }

        public uint? TopologyId { get; }

        public string TopologyQueryMessage { get; }

        public string PrimaryDeviceName { get; }

        public IReadOnlyList<string> ProtectedSourceNames { get; }

        public IReadOnlyList<string> ProtectedTargetKeys { get; }

        public bool HasActiveSideDockPath => ActivePaths.Any(path => path.IsSideDock);

        public bool IsExtendedDesktopWithSideDock => Mode == VirtualDisplayPresentationMode.Extend;

        public IReadOnlyList<ActiveDisplayPathSnapshot> SideDockPaths =>
            ActivePaths.Where(path => path.IsSideDock).ToArray();

        public ActiveDisplayPathSnapshot? PrimaryPath =>
            ActivePaths.FirstOrDefault(path => !path.IsSideDock && path.IsPrimary)
            ?? ActivePaths.FirstOrDefault(path =>
                !path.IsSideDock
                && path.SourceName.Equals(PrimaryDeviceName, StringComparison.OrdinalIgnoreCase));

        public VirtualDisplayPresentationMode Mode
        {
            get
            {
                if (!HasActiveSideDockPath)
                {
                    return VirtualDisplayPresentationMode.Unknown;
                }

                if (ActivePaths.Count == 1 && ActivePaths[0].IsSideDock)
                {
                    return VirtualDisplayPresentationMode.SecondaryOnly;
                }

                var distinctSourceCount = ActivePaths
                    .Select(path => path.SourceKey)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                var hasProtectedPath = ActivePaths.Any(path => !path.IsSideDock);
                if (hasProtectedPath && distinctSourceCount == ActivePaths.Count)
                {
                    return VirtualDisplayPresentationMode.Extend;
                }

                if (TopologyId == SdcTopologyClone || distinctSourceCount < ActivePaths.Count)
                {
                    return VirtualDisplayPresentationMode.Mirror;
                }

                return VirtualDisplayPresentationMode.Unknown;
            }
        }

        public bool HasActiveSourceName(string sourceName)
        {
            return !string.IsNullOrWhiteSpace(sourceName)
                && ActivePaths.Any(path => path.SourceName.Equals(sourceName, StringComparison.OrdinalIgnoreCase));
        }

        public bool HasActiveTargetKey(string targetKey)
        {
            return !string.IsNullOrWhiteSpace(targetKey)
                && ActivePaths.Any(path => path.TargetKey.Equals(targetKey, StringComparison.Ordinal));
        }

        public ActiveDisplayPathSnapshot? FindByTargetKey(string targetKey)
        {
            return string.IsNullOrWhiteSpace(targetKey)
                ? null
                : ActivePaths.FirstOrDefault(path => path.TargetKey.Equals(targetKey, StringComparison.Ordinal));
        }

        public string FormatActivePaths()
        {
            return ActivePaths.Count == 0
                ? "(none)"
                : string.Join(
                    "; ",
                    ActivePaths.Select(path =>
                        $"#{path.Index}:{path.SourceName}/{path.DisplayName} target={path.TargetName}/{path.TargetKey} sideDock={path.IsSideDock} primary={path.IsPrimary} {path.PathSummary}"));
        }

        public string FormatDiagnosticSummary()
        {
            return $"topology={FormatTopologyId(TopologyId)} ({TopologyQueryMessage}); primary={PrimaryDeviceName}; activePaths={FormatActivePaths()}";
        }
    }

    private sealed record ActiveDisplayPathSnapshot(
        int Index,
        string SourceName,
        string SourceKey,
        string TargetKey,
        string DisplayName,
        string TargetName,
        string TargetDevicePath,
        bool IsSideDock,
        bool IsPrimary,
        string PathSummary);

    private sealed record DisplayTopologyRestoreResult(bool Success, string Summary, int? WinApiReturnCode);

    private sealed record DisplayAdapterSnapshot(
        string DeviceName,
        string DisplayName,
        bool IsActive,
        bool IsPrimary,
        bool IsMirroringDriver,
        int SideDockScore);

    private sealed record DisplayAdapterSearchResult(DisplayAdapterSnapshot? Adapter, string FailureSummary)
    {
        public static DisplayAdapterSearchResult Success(DisplayAdapterSnapshot adapter)
        {
            return new DisplayAdapterSearchResult(adapter, string.Empty);
        }

        public static DisplayAdapterSearchResult Failure(string summary)
        {
            return new DisplayAdapterSearchResult(null, summary);
        }
    }

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
    private struct DisplayConfigTargetDeviceName
    {
        private const uint DisplayConfigDeviceInfoGetTargetName = 2;

        public DisplayConfigDeviceInfoHeader header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;

        public static DisplayConfigTargetDeviceName Create(Luid adapterId, uint targetId)
        {
            return new DisplayConfigTargetDeviceName
            {
                header = new DisplayConfigDeviceInfoHeader
                {
                    type = DisplayConfigDeviceInfoGetTargetName,
                    size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                    adapterId = adapterId,
                    id = targetId
                },
                monitorFriendlyDeviceName = string.Empty,
                monitorDevicePath = string.Empty
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

internal sealed record VirtualDisplayPresentationState(
    VirtualDisplayPresentationMode Mode,
    string Summary);

internal sealed record VirtualDisplayPresentationApplyResult(
    bool Success,
    string Summary,
    VirtualDisplayPresentationMode CurrentMode,
    string? DiagnosticSummary = null,
    VirtualDisplayModeService.PresentationRollbackToken? RollbackToken = null)
{
    public static VirtualDisplayPresentationApplyResult Succeeded(
        string summary,
        VirtualDisplayPresentationMode currentMode,
        string? diagnosticSummary = null,
        VirtualDisplayModeService.PresentationRollbackToken? rollbackToken = null)
    {
        return new VirtualDisplayPresentationApplyResult(true, summary, currentMode, diagnosticSummary, rollbackToken);
    }

    public static VirtualDisplayPresentationApplyResult Failed(
        string summary,
        VirtualDisplayPresentationMode currentMode,
        string? diagnosticSummary = null)
    {
        return new VirtualDisplayPresentationApplyResult(false, summary, currentMode, diagnosticSummary);
    }
}
