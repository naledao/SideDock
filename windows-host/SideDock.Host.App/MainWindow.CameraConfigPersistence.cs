using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SideDock.Host.App;

public sealed partial class MainWindow
{
    private const string CameraConfigPreferencesFileName = "camera-config-preferences.json";

    private CameraConfigPreferencesDocument _cameraConfigPreferences = new();
    private CameraConfigSource _cameraConfigSource = CameraConfigSource.Recommended;
    private string _cameraConfigSourceDetail = "Using the default recommended camera config.";
    private string? _cameraConfigSourceDeviceKey;
    private bool _cameraAutomaticConfigSelectionInProgress;
    private CameraConfigSource _pendingCameraRuntimeConfigSource = CameraConfigSource.UserManual;
    private bool _pendingCameraRuntimeConfigShouldSave;
    private string? _lastCameraPreferenceSaveSignature;

    private enum CameraConfigSource
    {
        Recommended,
        Restored,
        Fallback,
        UserManual
    }

    private void LoadCameraConfigPreferences()
    {
        try
        {
            var path = BuildCameraConfigPreferencesPath();
            if (!File.Exists(path))
            {
                _cameraConfigPreferences = new CameraConfigPreferencesDocument();
                return;
            }

            var document = JsonSerializer.Deserialize<CameraConfigPreferencesDocument>(
                File.ReadAllText(path, Encoding.UTF8));
            _cameraConfigPreferences = document?.Normalize() ?? new CameraConfigPreferencesDocument();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _cameraConfigPreferences = new CameraConfigPreferencesDocument();
            AppendRecentCameraLogLine($"camera-config-preferences ignored damaged file reason={ex.Message}");
        }
    }

    private void SaveCameraConfigPreferences()
    {
        try
        {
            var path = BuildCameraConfigPreferencesPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _cameraConfigPreferences.Version = 1;
            _cameraConfigPreferences.UpdatedAt = DateTimeOffset.UtcNow;
            var json = JsonSerializer.Serialize(
                _cameraConfigPreferences.Normalize(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            AppendRecentCameraLogLine($"camera-config-preferences save failed reason={ex.Message}");
        }
    }

    private static string BuildCameraConfigPreferencesPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideDock",
            "HostApp",
            CameraConfigPreferencesFileName);
    }

    private void MarkCameraConfigUserSelection()
    {
        if (_cameraAutomaticConfigSelectionInProgress || _syncingOverviewCameraOptions || !_uiReady)
        {
            return;
        }

        _deferredCameraCapabilityOptionsRefresh = null;
        var selected = SelectedOverviewCameraConfig(_overviewCameraRequestedEnabled);
        SetCameraConfigSource(
            CameraConfigSource.UserManual,
            $"User selected {selected.Summary}.",
            logEvent: true);
    }

    private void ResetCameraConfigForDeviceSelectionChanged()
    {
        if (!_uiReady || _hostProcess is { HasExited: false })
        {
            return;
        }

        var plan = BuildCameraConfigPreferencePlan(
            NormalizeCameraFacing(Selected(CameraFacingCombo)),
            forceRecommended: true,
            recommendedDetail: "Android device selection changed; using the recommended config until capabilities arrive.");
        ApplyCameraPreferencePlanLocally(plan, logEvent: true);
    }

    private void PrepareCameraConfigForHostStart()
    {
        var identity = ResolveCurrentCameraDeviceIdentity();
        if (identity is null
            || string.IsNullOrWhiteSpace(_cameraConfigSourceDeviceKey)
            || identity.DeviceKey.Equals(_cameraConfigSourceDeviceKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var plan = BuildCameraConfigPreferencePlan(
            NormalizeCameraFacing(Selected(CameraFacingCombo)),
            forceRecommended: true,
            recommendedDetail: "Android device changed; previous device camera config was not reused.");
        ApplyCameraPreferencePlanLocally(plan, logEvent: true);
    }

    private async Task RestoreCameraConfigAfterCapabilitiesAsync()
    {
        try
        {
            if (!_uiReady
                || _cameraAutomaticConfigSelectionInProgress
                || _cameraRuntimeConfigApplyInProgress
                || _overviewCameraOperationInProgress
                || _pendingCameraRuntimeConfig is not null
                || _cameraCapabilities is not { HasReportedLenses: true })
            {
                return;
            }

            var plan = BuildCameraConfigPreferencePlan(
                NormalizeCameraFacing(Selected(CameraFacingCombo)),
                forceRecommended: false,
                recommendedDetail: "No saved config matched this Android camera; using the recommended config.");
            await ApplyCameraPreferencePlanAsync(plan, saveWhenApplied: plan.Source is CameraConfigSource.Fallback);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException)
        {
            AppendRecentCameraLogLine($"camera-config-restore failed reason={ex.Message}");
        }
    }

    private async Task HandleOverviewCameraFacingSelectionChangedAsync()
    {
        if (_syncingOverviewCameraOptions || !_uiReady)
        {
            return;
        }

        var plan = BuildCameraConfigPreferencePlan(
            NormalizeCameraFacing(Selected(CameraFacingCombo)),
            forceRecommended: false,
            recommendedDetail: "Camera changed; using the recommended config for this lens.");
        await ApplyCameraPreferencePlanAsync(plan, saveWhenApplied: plan.Source is not CameraConfigSource.Restored);
    }

    private async Task RestoreRecommendedCameraConfigFromUiAsync()
    {
        if (!_uiReady)
        {
            return;
        }

        var plan = BuildCameraConfigPreferencePlan(
            NormalizeCameraFacing(Selected(CameraFacingCombo)),
            forceRecommended: true,
            recommendedDetail: "Recommended camera config restored by user.");
        await ApplyCameraPreferencePlanAsync(plan, saveWhenApplied: true);
    }

    private async Task ApplyCameraPreferencePlanAsync(CameraConfigPreferencePlan plan, bool saveWhenApplied)
    {
        _cameraAutomaticConfigSelectionInProgress = true;
        try
        {
            var previousConfig = CurrentCameraConfigSelection(_overviewCameraRequestedEnabled);
            var shouldApplyRuntime = _hostProcess is { HasExited: false }
                && !CameraConfigMatches(plan.Config, previousConfig);
            ApplyCameraPreferencePlanLocally(plan, logEvent: true);

            if (_hostProcess is not { HasExited: false })
            {
                ClearCameraRuntimeApplyState();
                SyncOverviewCameraOptionsToDiagnostics();
                return;
            }

            if (!shouldApplyRuntime)
            {
                if (saveWhenApplied && IsCameraReceiving(_cameraDiagnostics))
                {
                    SaveEffectiveCameraConfigIfUsable(plan.Config, plan.Source);
                }

                UpdateCameraStatusView();
                return;
            }

            await ApplyOverviewCameraRuntimeConfigSelectionAsync(plan.Source, saveWhenApplied);
        }
        finally
        {
            _cameraAutomaticConfigSelectionInProgress = false;
        }
    }

    private void ApplyCameraPreferencePlanLocally(CameraConfigPreferencePlan plan, bool logEvent)
    {
        RefreshOverviewCameraCapabilityOptions(plan.Config);
        SyncOverviewCameraSelectionsFromConfig(plan.Config);
        ApplyCameraConfigSelectionToDiagnostics(plan.Config);
        SetCameraConfigSource(plan.Source, plan.Detail, logEvent);
        UpdateCameraStatusView();
    }

    private CameraConfigPreferencePlan BuildCameraConfigPreferencePlan(
        string requestedFacing,
        bool forceRecommended,
        string recommendedDetail)
    {
        var capabilities = _cameraCapabilities;
        var usingReportedCapabilities = capabilities?.HasReportedLenses == true;
        var lenses = capabilities?.Lenses.Count > 0
            ? capabilities.Lenses
            : CreateDefaultCameraLenses();
        var lens = FindCameraLens(lenses, requestedFacing) ?? lenses[0];
        var baseConfig = CurrentCameraConfigSelection(_overviewCameraRequestedEnabled);

        var recommended = BuildRecommendedCameraConfig(lens, baseConfig);
        if (forceRecommended)
        {
            return new CameraConfigPreferencePlan(
                recommended,
                CameraConfigSource.Recommended,
                lens,
                recommendedDetail);
        }

        if (!usingReportedCapabilities)
        {
            return new CameraConfigPreferencePlan(
                recommended,
                CameraConfigSource.Recommended,
                lens,
                "Android camera capabilities are not available yet; using the recommended config.");
        }

        if (!TryResolveCurrentCameraDeviceIdentity(out var identity))
        {
            return new CameraConfigPreferencePlan(
                recommended,
                CameraConfigSource.Recommended,
                lens,
                "No stable Android device identity is available yet; using the recommended config.");
        }

        if (!TryGetCameraPreference(identity, lens, out var preference))
        {
            return new CameraConfigPreferencePlan(
                recommended,
                CameraConfigSource.Recommended,
                lens,
                recommendedDetail);
        }

        var savedConfig = preference.ToSelection(baseConfig.Enabled, baseConfig.Port, lens.Facing);
        if (IsCameraConfigSupportedByLens(savedConfig, lens, out var unsupportedReason))
        {
            return new CameraConfigPreferencePlan(
                savedConfig,
                CameraConfigSource.Restored,
                lens,
                $"Restored saved config {savedConfig.Summary} for {FormatCameraPreferenceIdentity(identity, lens)}.");
        }

        return new CameraConfigPreferencePlan(
            recommended,
            CameraConfigSource.Fallback,
            lens,
            $"Saved config {savedConfig.Summary} is no longer supported ({unsupportedReason}); fell back to {recommended.Summary}.");
    }

    private CameraConfigSelection BuildRecommendedCameraConfig(CameraLensCapability lens, CameraConfigSelection baseConfig)
    {
        var size = SelectRecommendedCameraSize(lens.Sizes);
        var fps = SelectRecommendedCameraFps(size.Fps);
        var codec = SelectRecommendedCameraCodec(size.Codecs);
        return new CameraConfigSelection(
            baseConfig.Enabled,
            baseConfig.Port,
            size.Width,
            size.Height,
            fps,
            codec,
            lens.Facing);
    }

    private static CameraSizeCapability SelectRecommendedCameraSize(IReadOnlyList<CameraSizeCapability> sizes)
    {
        var usableSizes = sizes
            .Where(size => size.Codecs.Count == 0 || size.Codecs.Any(IsHostSupportedCameraCodec))
            .ToArray();
        var candidates = usableSizes.Length > 0 ? usableSizes : sizes.ToArray();
        if (candidates.Length == 0)
        {
            return new CameraSizeCapability(1280, 720, new[] { 30 }, new[] { "video/avc" });
        }

        var preferred = candidates.FirstOrDefault(size => size.Width == 1280 && size.Height == 720);
        if (preferred is not null)
        {
            return preferred;
        }

        var stableCandidates = candidates
            .Where(size => size.Width <= 1920 && size.Height <= 1080)
            .ToArray();
        var pool = stableCandidates.Length > 0 ? stableCandidates : candidates;
        const double targetAspect = 16d / 9d;
        const int targetPixels = 1280 * 720;

        return pool
            .OrderBy(size => Math.Abs(((double)size.Width / size.Height) - targetAspect))
            .ThenBy(size => Math.Abs((size.Width * size.Height) - targetPixels))
            .ThenBy(size => size.Width * size.Height)
            .First();
    }

    private static int SelectRecommendedCameraFps(IReadOnlyList<int> fpsValues)
    {
        if (fpsValues.Count == 0)
        {
            return 30;
        }

        if (fpsValues.Contains(30))
        {
            return 30;
        }

        var stableFps = fpsValues.Where(fps => fps <= 60).ToArray();
        var candidates = stableFps.Length > 0 ? stableFps : fpsValues.ToArray();
        return candidates
            .OrderBy(fps => Math.Abs(fps - 30))
            .ThenBy(fps => fps > 60 ? 1 : 0)
            .ThenByDescending(fps => fps)
            .First();
    }

    private static string SelectRecommendedCameraCodec(IReadOnlyList<string> codecs)
    {
        if (codecs.Any(codec => NormalizeCameraCodec(codec).Equals("video/avc", StringComparison.OrdinalIgnoreCase)))
        {
            return "video/avc";
        }

        var hostSupported = codecs
            .Select(NormalizeCameraCodec)
            .FirstOrDefault(IsHostSupportedCameraCodec);
        return string.IsNullOrWhiteSpace(hostSupported) ? "video/avc" : hostSupported;
    }

    private static bool IsCameraConfigSupportedByLens(
        CameraConfigSelection config,
        CameraLensCapability lens,
        out string reason)
    {
        if (!lens.Facing.Equals(NormalizeCameraFacing(config.Facing), StringComparison.OrdinalIgnoreCase))
        {
            reason = $"facing changed from {config.Facing} to {lens.Facing}";
            return false;
        }

        var size = FindCameraSize(lens, config.Width, config.Height);
        if (size is null)
        {
            reason = $"size {config.Width}x{config.Height} is unavailable";
            return false;
        }

        if (!size.Fps.Contains(config.Fps))
        {
            reason = $"fps {config.Fps} is unavailable for {config.Width}x{config.Height}";
            return false;
        }

        var normalizedCodec = NormalizeCameraCodec(config.Codec);
        if (!size.Codecs.Any(codec => NormalizeCameraCodec(codec).Equals(normalizedCodec, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"codec {normalizedCodec} is unavailable for {config.Width}x{config.Height}";
            return false;
        }

        if (!IsHostSupportedCameraCodec(normalizedCodec))
        {
            reason = $"codec {normalizedCodec} is not supported by the Windows receiver";
            return false;
        }

        reason = "";
        return true;
    }

    private void SaveEffectiveCameraConfigIfUsable(CameraConfigSelection config, CameraConfigSource source)
    {
        if (!config.Enabled || config.Width <= 0 || config.Height <= 0 || config.Fps <= 0)
        {
            return;
        }

        if (!TryResolveCurrentCameraDeviceIdentity(out var identity))
        {
            AppendRecentCameraLogLine($"camera-config-save skipped reason=no-stable-device-id config={config.Summary}");
            return;
        }

        var lenses = _cameraCapabilities?.Lenses.Count > 0
            ? _cameraCapabilities.Lenses
            : CreateDefaultCameraLenses();
        var lens = FindCameraLens(lenses, config.Facing) ?? lenses[0];
        var key = BuildCameraPreferenceKey(identity, lens, preferFacingOnly: false);
        var signature = $"{key}|{config.Width}x{config.Height}@{config.Fps}|{NormalizeCameraCodec(config.Codec)}";
        if (signature.Equals(_lastCameraPreferenceSaveSignature, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var entry = CameraConfigPreferenceEntry.FromConfig(identity, lens, config, source);
        _cameraConfigPreferences.Configs[key] = entry;

        var facingKey = BuildCameraPreferenceKey(identity, lens, preferFacingOnly: true);
        if (!key.Equals(facingKey, StringComparison.OrdinalIgnoreCase))
        {
            _cameraConfigPreferences.Configs[facingKey] = entry with
            {
                CameraKey = BuildCameraPreferenceCameraKey(lens, preferFacingOnly: true)
            };
        }

        SaveCameraConfigPreferences();
        _lastCameraPreferenceSaveSignature = signature;
        SetCameraConfigSource(source, $"Saved {config.Summary} for {FormatCameraPreferenceIdentity(identity, lens)}.", logEvent: false);
        AppendRecentCameraLogLine($"camera-config-saved key={key} source={source} config={config.Summary}");
    }

    private bool TryGetCameraPreference(
        CameraPreferenceIdentity identity,
        CameraLensCapability lens,
        out CameraConfigPreferenceEntry preference)
    {
        foreach (var key in BuildCameraPreferenceLookupKeys(identity, lens))
        {
            if (_cameraConfigPreferences.Configs.TryGetValue(key, out var candidate)
                && candidate.HasConfig)
            {
                preference = candidate;
                return true;
            }
        }

        preference = new CameraConfigPreferenceEntry();
        return false;
    }

    private static IEnumerable<string> BuildCameraPreferenceLookupKeys(
        CameraPreferenceIdentity identity,
        CameraLensCapability lens)
    {
        yield return BuildCameraPreferenceKey(identity, lens, preferFacingOnly: false);
        yield return BuildCameraPreferenceKey(identity, lens, preferFacingOnly: true);
    }

    private static string BuildCameraPreferenceKey(
        CameraPreferenceIdentity identity,
        CameraLensCapability lens,
        bool preferFacingOnly)
    {
        return $"{identity.DeviceKey}|{BuildCameraPreferenceCameraKey(lens, preferFacingOnly)}";
    }

    private static string BuildCameraPreferenceCameraKey(CameraLensCapability lens, bool preferFacingOnly)
    {
        return !preferFacingOnly && !string.IsNullOrWhiteSpace(lens.CameraId)
            ? $"camera-id:{lens.CameraId.Trim()}"
            : $"facing:{NormalizeCameraFacing(lens.Facing)}";
    }

    private bool TryResolveCurrentCameraDeviceIdentity(out CameraPreferenceIdentity identity)
    {
        var resolved = ResolveCurrentCameraDeviceIdentity();
        if (resolved is not null)
        {
            identity = resolved;
            return true;
        }

        identity = new CameraPreferenceIdentity("", null, null, null, null, null);
        return false;
    }

    private CameraPreferenceIdentity? ResolveCurrentCameraDeviceIdentity()
    {
        var selectedSerial = FirstNonEmpty(SelectedOrKnownDefaultAdbSerial(), _lastAdbReverseSerial);
        if (!string.IsNullOrWhiteSpace(selectedSerial))
        {
            return new CameraPreferenceIdentity(
                $"adb:{selectedSerial.Trim()}",
                selectedSerial.Trim(),
                _cameraCapabilities?.DeviceId,
                _cameraCapabilities?.Manufacturer,
                _cameraCapabilities?.Model,
                _cameraCapabilities?.AndroidSdk);
        }

        if (!string.IsNullOrWhiteSpace(_cameraCapabilities?.DeviceId))
        {
            var deviceId = _cameraCapabilities.DeviceId.Trim();
            return new CameraPreferenceIdentity(
                $"android-id:{deviceId}",
                null,
                deviceId,
                _cameraCapabilities.Manufacturer,
                _cameraCapabilities.Model,
                _cameraCapabilities.AndroidSdk);
        }

        return null;
    }

    private static string FormatCameraPreferenceIdentity(CameraPreferenceIdentity identity, CameraLensCapability lens)
    {
        var device = !string.IsNullOrWhiteSpace(identity.Serial)
            ? identity.Serial
            : !string.IsNullOrWhiteSpace(identity.DeviceId)
                ? identity.DeviceId
                : identity.DeviceKey;
        var camera = !string.IsNullOrWhiteSpace(lens.CameraId)
            ? $"{lens.Facing}/{lens.CameraId}"
            : lens.Facing;
        return $"{device} {camera}";
    }

    private void SetCameraConfigSource(CameraConfigSource source, string detail, bool logEvent)
    {
        _cameraConfigSource = source;
        _cameraConfigSourceDetail = string.IsNullOrWhiteSpace(detail) ? FormatCameraConfigSourceLabel(source) : detail;
        _cameraConfigSourceDeviceKey = ResolveCurrentCameraDeviceIdentity()?.DeviceKey;
        if (logEvent)
        {
            AppendRecentCameraLogLine($"camera-config-source source={source} detail={_cameraConfigSourceDetail}");
        }

        UpdateCameraConfigSourceView();
    }

    private void UpdateCameraConfigSourceView()
    {
        var text = BuildCameraConfigSourceText();
        if (OverviewCameraPageConfigSourceText is not null)
        {
            OverviewCameraPageConfigSourceText.Text = text;
            OverviewCameraPageConfigSourceText.Foreground = _cameraConfigSource switch
            {
                CameraConfigSource.Fallback => _warningBrush,
                CameraConfigSource.Restored => _successBrush,
                CameraConfigSource.UserManual => _overviewPrimaryBrush,
                _ => _secondaryBrush
            };
        }
    }

    private string BuildCameraConfigSourceText()
    {
        var label = FormatCameraConfigSourceLabel(_cameraConfigSource);
        return string.IsNullOrWhiteSpace(_cameraConfigSourceDetail)
            ? label
            : $"{label}: {_cameraConfigSourceDetail}";
    }

    private static string FormatCameraConfigSourceLabel(CameraConfigSource source)
    {
        return source switch
        {
            CameraConfigSource.Restored => "已恢复上次配置",
            CameraConfigSource.Fallback => "已回退到兼容配置",
            CameraConfigSource.UserManual => "用户手动选择",
            _ => "推荐配置"
        };
    }

    private sealed record CameraConfigPreferencePlan(
        CameraConfigSelection Config,
        CameraConfigSource Source,
        CameraLensCapability Lens,
        string Detail);

    private sealed record CameraPreferenceIdentity(
        string DeviceKey,
        string? Serial,
        string? DeviceId,
        string? Manufacturer,
        string? Model,
        int? AndroidSdk);

    private sealed class CameraConfigPreferencesDocument
    {
        public int Version { get; set; } = 1;

        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public Dictionary<string, CameraConfigPreferenceEntry> Configs { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public CameraConfigPreferencesDocument Normalize()
        {
            Configs = Configs
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value.HasConfig)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return this;
        }
    }

    private sealed record CameraConfigPreferenceEntry
    {
        public string DeviceKey { get; init; } = "";

        public string? Serial { get; init; }

        public string? DeviceId { get; init; }

        public string? Manufacturer { get; init; }

        public string? Model { get; init; }

        public int? AndroidSdk { get; init; }

        public string CameraKey { get; init; } = "";

        public string CameraId { get; init; } = "";

        public string Facing { get; init; } = "back";

        public int Width { get; init; }

        public int Height { get; init; }

        public int Fps { get; init; }

        public string Codec { get; init; } = "video/avc";

        public string Source { get; init; } = "";

        public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.UtcNow;

        public bool HasConfig => Width > 0 && Height > 0 && Fps > 0;

        public CameraConfigSelection ToSelection(bool enabled, int port, string fallbackFacing)
        {
            return new CameraConfigSelection(
                enabled,
                port,
                Math.Max(1, Width),
                Math.Max(1, Height),
                Math.Max(1, Fps),
                NormalizeCameraCodec(Codec),
                NormalizeCameraFacing(string.IsNullOrWhiteSpace(Facing) ? fallbackFacing : Facing));
        }

        public static CameraConfigPreferenceEntry FromConfig(
            CameraPreferenceIdentity identity,
            CameraLensCapability lens,
            CameraConfigSelection config,
            CameraConfigSource source)
        {
            return new CameraConfigPreferenceEntry
            {
                DeviceKey = identity.DeviceKey,
                Serial = identity.Serial,
                DeviceId = identity.DeviceId,
                Manufacturer = identity.Manufacturer,
                Model = identity.Model,
                AndroidSdk = identity.AndroidSdk,
                CameraKey = BuildCameraPreferenceCameraKey(lens, preferFacingOnly: false),
                CameraId = lens.CameraId,
                Facing = NormalizeCameraFacing(config.Facing),
                Width = config.Width,
                Height = config.Height,
                Fps = config.Fps,
                Codec = NormalizeCameraCodec(config.Codec),
                Source = source.ToString(),
                SavedAt = DateTimeOffset.UtcNow
            };
        }
    }
}
