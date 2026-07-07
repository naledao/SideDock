using System.Buffers.Binary;
using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SideDock.Host;

internal static partial class Program
{
    private const int DefaultControlPort = 27183;
    private const int DefaultVideoPort = 27184;
    private const int DefaultAudioPort = 27185;
    private const int DefaultCameraPort = 27186;
    private const int DefaultCameraCommandPort = 27187;
    private const int DefaultVideoWidth = 1280;
    private const int DefaultVideoHeight = 720;
    private const int DefaultVideoFps = 120;
    private const int DefaultCameraWidth = 1280;
    private const int DefaultCameraHeight = 720;
    private const int DefaultCameraFps = 30;
    private const string DefaultCameraFacing = "back";
    private const int DefaultVideoBitrate = 4_000_000;
    private const int DefaultVideoGop = 30;
    private const int DefaultMaxVideoQueue = 2;
    private const int DefaultNv12PoolSize = 4;
    private static readonly TimeSpan AdbCommandTimeout = TimeSpan.FromSeconds(6);
    private const VideoSourceKind DefaultVideoSource = VideoSourceKind.IddGpu;
    private const string DefaultVideoFile = "artifacts/test-videos/sidedock-720p30.h264";
    private const string DefaultResolutionPreset = "720p";
    private static readonly string AdbExecutableName = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var timerResolution = EnableHighResolutionTimer();
        if (OperatingSystem.IsWindows())
        {
            DpiAwareness.TryEnablePerMonitorV2(message => Log("DPI", message));
        }

        var options = HostOptions.Parse(args);
        if (options.ListWindows)
        {
            WindowEnumerator.PrintWindows();
            return 0;
        }

        using var appCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            appCts.Cancel();
        };

        var videoModeState = new VideoModeState(options);
        var displayLayoutProvider = new DisplayLayoutProvider(options, videoModeState, message => Log("DISPLAY", message));

        PrintHeader(options);
        Log("VIDEO", $"startup video-source={FormatVideoSource(options.VideoSource)} input-target={options.InputTarget.ToString().ToLowerInvariant()}");
        if (IsIddVideoSource(options.VideoSource) || options.InputTarget == InputTargetKind.Idd)
        {
            displayLayoutProvider.LogCurrentLayout();
            if (options.RequestedDisplayMode is not null)
            {
                var modeResult = displayLayoutProvider.TryChangeMode(options.RequestedDisplayMode, out var modeMessage);
                Log("DISPLAY", $"startup display mode {options.RequestedDisplayMode}: {(modeResult ? "success" : "failed")} {modeMessage}");
            }
        }

        var adbPath = ResolveAdbPath();
        var adbSerial = ResolveAdbSerial();
        Log("ADB", $"使用 adb: {adbPath}");
        if (!string.IsNullOrWhiteSpace(adbSerial))
        {
            Log("ADB", $"目标设备: {adbSerial}");
        }

        var skipAdbReverse = IsTruthy(Environment.GetEnvironmentVariable("SIDEDOCK_SKIP_ADB_REVERSE"));
        if (skipAdbReverse)
        {
            Log("ADB", "跳过自动配置 reverse: SIDEDOCK_SKIP_ADB_REVERSE 已启用");
        }
        else
        {
            foreach (var port in options.ReversePorts)
            {
                await ConfigureAdbReverseAsync(adbPath, adbSerial, port, appCts.Token);
            }

            _ = Task.Run(() => KeepAdbReverseAliveAsync(adbPath, adbSerial, options.ReversePorts, appCts.Token), appCts.Token);
        }

        var controlPublisher = new ControlMessagePublisher();
        var audioTestCoordinator = new AudioTestCoordinator(options, controlPublisher);
        var cameraRuntimeState = new CameraRuntimeState(CameraRuntimeConfig.FromOptions(options));
        var controlServer = new ControlServer(IPAddress.Loopback, options, videoModeState, controlPublisher, displayLayoutProvider, cameraRuntimeState, audioTestCoordinator);
        var cameraCommandServer = new CameraCommandServer(IPAddress.Loopback, DefaultCameraCommandPort, options, controlPublisher, cameraRuntimeState, audioTestCoordinator);
        var videoServer = new VideoServer(IPAddress.Loopback, options, videoModeState, controlPublisher);
        var audioServer = new AudioServer(IPAddress.Loopback, options, controlPublisher, audioTestCoordinator);
        var cameraServer = new CameraServer(IPAddress.Loopback, options, controlPublisher, cameraRuntimeState);
        if (!string.IsNullOrWhiteSpace(options.CameraReplayFilePath))
        {
            _ = Task.Run(
                () => CameraDebugReplayClient.RunAsync(IPAddress.Loopback, options, appCts.Token),
                appCts.Token);
        }

        await Task.WhenAll(
            controlServer.RunAsync(appCts.Token),
            cameraCommandServer.RunAsync(appCts.Token),
            videoServer.RunAsync(appCts.Token),
            audioServer.RunAsync(appCts.Token),
            cameraServer.RunAsync(appCts.Token));

        return 0;
    }

    private static void PrintHeader(HostOptions options)
    {
        Console.WriteLine("SideDock Windows 服务");
        Console.WriteLine($"控制通道: 127.0.0.1:{options.ControlPort}");
        Console.WriteLine($"视频通道: 127.0.0.1:{options.VideoPort}");
        Console.WriteLine($"麦克风通道: 127.0.0.1:{options.AudioPort}");
        Console.WriteLine($"摄像头通道: 127.0.0.1:{options.CameraPort}");
        Console.WriteLine($"视频源: {FormatVideoSource(options.VideoSource)}");
        Console.WriteLine($"测试视频: {options.VideoFilePath}");
        Console.WriteLine($"实时编码器: {options.Encoder}");
        Console.WriteLine($"输入目标: {options.InputTarget}");
        Console.WriteLine($"输入注入: {(options.EnableInputInjection ? "已启用 SendInput" : "仅记录，不注入")}");
        if (options.VideoSource == VideoSourceKind.Region)
        {
            Console.WriteLine($"capture region: {options.CaptureRegion}");
        }
        else if (options.VideoSource == VideoSourceKind.Window)
        {
            Console.WriteLine($"window title: {options.WindowTitle ?? "(not set)"}");
            Console.WriteLine($"process name: {options.ProcessName ?? "(not set)"}");
        }

        Console.WriteLine($"画质档位: {options.ResolutionPreset} @ {options.VideoFps}Hz");
        Console.WriteLine($"编码规格: {options.VideoWidth}x{options.VideoHeight} bitrate={options.VideoBitrate} ({(options.AutoVideoBitrate ? "auto" : "manual")})");
        Console.WriteLine($"编码调优: {FormatEncoderTuningForLog(options)}");
        Console.WriteLine($"链路容量: nv12Pool={options.Nv12PoolSize} encodedPacketQueue={options.EncodedPacketQueue}");
        Console.WriteLine($"音频能力: microphone={(options.AudioDeviceEnabled && options.MicrophoneEnabled ? "enabled" : "disabled")} speaker={(options.AudioDeviceEnabled && options.SpeakerEnabled ? "enabled" : "disabled")}");
        Console.WriteLine($"摄像头能力: {(options.CameraEnabled ? "enabled" : "disabled")} {options.CameraWidth}x{options.CameraHeight}@{options.CameraFps} {options.CameraCodec} facing={options.CameraFacing}");
        if (!string.IsNullOrWhiteSpace(options.CameraReplayFilePath))
        {
            Console.WriteLine($"摄像头回放输入: {options.CameraReplayFilePath}");
        }

        Console.WriteLine($"音频后端: {FormatAudioBackend(options.AudioBackend)}");
        Console.WriteLine($"电脑声音 loopback 输出端点: {FormatOptionalForLog(options.AudioOutputLoopbackEndpointId)}");
        Console.WriteLine($"Android 麦克风写入端点: {FormatOptionalForLog(options.AudioMicrophoneRenderEndpointId)}");

        if (options.RequestedDisplayMode is not null)
        {
            Console.WriteLine($"display mode request: {options.RequestedDisplayMode}");
        }

        if (!string.IsNullOrWhiteSpace(options.DumpEncodedPath))
        {
            Console.WriteLine($"编码输出 dump: {options.DumpEncodedPath}");
        }

        if (!string.IsNullOrWhiteSpace(options.DumpGpuFrameDirectory))
        {
            Console.WriteLine($"GPU frame dump: {options.DumpGpuFrameDirectory}");
        }

        Console.WriteLine("退出方式: Ctrl+C");
        Console.WriteLine();
    }

    private static string FormatVideoSource(VideoSourceKind value)
    {
        return value switch
        {
            VideoSourceKind.SyntheticNv12 => "synthetic-nv12",
            VideoSourceKind.IddGpu => "idd-gpu",
            _ => value.ToString().ToLowerInvariant()
        };
    }

    private static string FormatAudioBackend(AudioBackendKind value)
    {
        return value switch
        {
            AudioBackendKind.LegacySharedMemory => "legacy-shared-memory",
            AudioBackendKind.WasapiVirtualCable => "wasapi-virtual-cable",
            _ => value.ToString()
        };
    }

    private static string FormatOptionalForLog(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(not set)" : value;
    }

    private static bool IsIddVideoSource(VideoSourceKind value)
    {
        return value is VideoSourceKind.Idd or VideoSourceKind.IddGpu;
    }

    private static bool IsTruthy(string? value)
    {
        return value is not null
            && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task KeepAdbReverseAliveAsync(
        string adbPath,
        string? adbSerial,
        IReadOnlyList<int> ports,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var listArguments = BuildAdbDeviceArguments(adbSerial, "reverse --list");
                var reverseList = await RunAdbAsync(adbPath, listArguments, cancellationToken);
                if (reverseList.ExitCode != 0)
                {
                    Log("ADB", $"读取 reverse 列表失败，退出码 {reverseList.ExitCode}: {reverseList.Stderr}");
                    continue;
                }

                foreach (var port in ports)
                {
                    if (reverseList.Stdout.Contains($"tcp:{port} tcp:{port}", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Log("ADB", $"未检测到 tcp:{port} reverse 映射，尝试重新配置。");
                    await ConfigureAdbReverseAsync(adbPath, adbSerial, port, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Application shutdown.
        }
    }

    private static async Task ConfigureAdbReverseAsync(
        string adbPath,
        string? adbSerial,
        int port,
        CancellationToken cancellationToken)
    {
        var reverseArguments = $"reverse tcp:{port} tcp:{port}";
        var arguments = BuildAdbDeviceArguments(adbSerial, reverseArguments);

        try
        {
            var result = await RunAdbAsync(adbPath, arguments, cancellationToken);
            if (result.ExitCode == 0)
            {
                Log("ADB", $"已配置 {arguments}");
                if (!string.IsNullOrWhiteSpace(result.Stdout))
                {
                    Log("ADB", result.Stdout);
                }
            }
            else
            {
                Log("ADB", $"自动配置失败，退出码 {result.ExitCode}。请确认设备已连接并打开 USB 调试。");
                if (!string.IsNullOrWhiteSpace(result.Stderr))
                {
                    Log("ADB", result.Stderr);
                }

                Log("ADB", $"可手动执行: adb {arguments}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log("ADB", $"自动配置失败: {ex.Message}");
            Log("ADB", $"可手动执行: adb {arguments}");
        }
    }

    private static string BuildAdbDeviceArguments(string? adbSerial, string command)
    {
        return string.IsNullOrWhiteSpace(adbSerial)
            ? command
            : $"-s {QuoteAdbArgument(adbSerial)} {command}";
    }

    private static string QuoteAdbArgument(string argument)
    {
        return argument.Contains(' ') || argument.Contains('"')
            ? "\"" + argument.Replace("\"", "\\\"") + "\""
            : argument;
    }

    private static async Task<ProcessResult> RunAdbAsync(string adbPath, string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(adbPath, arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new ProcessResult(-1, string.Empty, "无法启动 adb");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var adbTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        adbTimeoutCts.CancelAfter(AdbCommandTimeout);

        try
        {
            await process.WaitForExitAsync(adbTimeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }

            return new ProcessResult(
                -1,
                await ReadProcessOutputAsync(stdoutTask),
                $"adb command timed out after {AdbCommandTimeout.TotalSeconds:F0}s: adb {arguments}");
        }

        return new ProcessResult(process.ExitCode, (await stdoutTask).Trim(), (await stderrTask).Trim());
    }

    private static async Task<string> ReadProcessOutputAsync(Task<string> outputTask)
    {
        try
        {
            return await outputTask.WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }

    private static string ResolveAdbPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("SIDEDOCK_ADB");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return ResolveExplicitAdbPath(configuredPath);
        }

        foreach (var adbPath in EnumerateBundledAdbCandidates())
        {
            if (File.Exists(adbPath))
            {
                return adbPath;
            }
        }

        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android",
                "Sdk")
        };

        foreach (var sdkRoot in candidates)
        {
            if (string.IsNullOrWhiteSpace(sdkRoot))
            {
                continue;
            }

            var adbPath = Path.Combine(sdkRoot, "platform-tools", AdbExecutableName);
            if (File.Exists(adbPath))
            {
                return adbPath;
            }
        }

        return "adb";
    }

    private static string? ResolveAdbSerial()
    {
        var serial = Environment.GetEnvironmentVariable("ANDROID_SERIAL");
        return string.IsNullOrWhiteSpace(serial) ? null : serial.Trim();
    }

    private static string ResolveExplicitAdbPath(string configuredPath)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
        if (Directory.Exists(expandedPath))
        {
            foreach (var adbPath in EnumerateAdbCandidatesFromRoot(expandedPath))
            {
                if (File.Exists(adbPath))
                {
                    return adbPath;
                }
            }
        }

        return expandedPath;
    }

    private static IEnumerable<string> EnumerateBundledAdbCandidates()
    {
        var roots = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty),
            Environment.CurrentDirectory
        };

        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var adbPath in EnumerateAdbCandidatesFromRoot(root!))
            {
                yield return adbPath;
            }
        }
    }

    private static IEnumerable<string> EnumerateAdbCandidatesFromRoot(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(root, "platform-tools", "win-x64", AdbExecutableName);
        }

        yield return Path.Combine(root, "platform-tools", AdbExecutableName);
        yield return Path.Combine(root, AdbExecutableName);
    }

    private static void Log(string scope, string message)
    {
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} [{scope}] {message}");
    }

    private static IDisposable EnableHighResolutionTimer()
    {
        return OperatingSystem.IsWindows()
            ? HighResolutionTimerPeriod.Start(1, message => Log("TIMER", message))
            : NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class HighResolutionTimerPeriod : IDisposable
    {
        private readonly uint _periodMs;
        private readonly Action<string> _log;
        private bool _enabled;

        private HighResolutionTimerPeriod(uint periodMs, Action<string> log)
        {
            _periodMs = periodMs;
            _log = log;
        }

        [SupportedOSPlatform("windows")]
        public static HighResolutionTimerPeriod Start(uint periodMs, Action<string> log)
        {
            var timer = new HighResolutionTimerPeriod(periodMs, log);
            var result = timeBeginPeriod(periodMs);
            if (result == 0)
            {
                timer._enabled = true;
                log($"enabled {periodMs}ms timer resolution");
            }
            else
            {
                log($"failed to enable {periodMs}ms timer resolution, result={result}");
            }

            return timer;
        }

        public void Dispose()
        {
            if (!_enabled)
            {
                return;
            }

            var result = timeEndPeriod(_periodMs);
            _enabled = false;
            _log(result == 0
                ? $"released {_periodMs}ms timer resolution"
                : $"failed to release {_periodMs}ms timer resolution, result={result}");
        }

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);
    }

    private sealed class FramePacer
    {
        private static readonly long LateThresholdTicks = MillisecondsToTicks(0.5);
        private static readonly long ResyncThresholdTicks = MillisecondsToTicks(1000);
        private readonly int _targetFps;
        private readonly long _frameIntervalTicks;
        private readonly Action<double>? _recordFrameIntervalMs;
        private readonly Action? _recordLateFrame;
        private readonly Action? _recordScheduleReset;
        private long _nextFrameTicks;
        private long _lastFrameTicks;
        private long _lateFrames;

        public FramePacer(
            int targetFps,
            Action<double>? recordFrameIntervalMs = null,
            Action? recordLateFrame = null,
            Action? recordScheduleReset = null)
        {
            _targetFps = Math.Max(1, targetFps);
            _frameIntervalTicks = Math.Max(1, (long)Math.Round(Stopwatch.Frequency / (double)_targetFps));
            _nextFrameTicks = Stopwatch.GetTimestamp();
            _recordFrameIntervalMs = recordFrameIntervalMs;
            _recordLateFrame = recordLateFrame;
            _recordScheduleReset = recordScheduleReset;
        }

        public int TargetFps => _targetFps;

        public long NextFrameTicks => Interlocked.Read(ref _nextFrameTicks);

        public long LateFrames => Interlocked.Read(ref _lateFrames);

        public DateTimeOffset MarkFrameStart()
        {
            var nowTicks = Stopwatch.GetTimestamp();
            var previousTicks = Interlocked.Exchange(ref _lastFrameTicks, nowTicks);
            if (previousTicks != 0)
            {
                _recordFrameIntervalMs?.Invoke(TicksToMilliseconds(nowTicks - previousTicks));
            }

            return DateTimeOffset.UtcNow;
        }

        public async ValueTask WaitForNextFrameAsync(CancellationToken cancellationToken)
        {
            var targetTicks = Interlocked.Add(ref _nextFrameTicks, _frameIntervalTicks);
            var nowTicks = Stopwatch.GetTimestamp();
            var remainingTicks = targetTicks - nowTicks;
            if (remainingTicks <= 0)
            {
                RecordLateFrame();
                ResyncIfNeeded(targetTicks, nowTicks);
                return;
            }

            await DelayUntilAsync(targetTicks, cancellationToken);

            var completedTicks = Stopwatch.GetTimestamp();
            if (completedTicks - targetTicks > LateThresholdTicks)
            {
                RecordLateFrame();
                ResyncIfNeeded(targetTicks, completedTicks);
            }
        }

        private void ResyncIfNeeded(long targetTicks, long nowTicks)
        {
            if (nowTicks - targetTicks <= ResyncThresholdTicks)
            {
                return;
            }

            Interlocked.Exchange(ref _nextFrameTicks, nowTicks);
            _recordScheduleReset?.Invoke();
        }

        private void RecordLateFrame()
        {
            Interlocked.Increment(ref _lateFrames);
            _recordLateFrame?.Invoke();
        }

        private static async ValueTask DelayUntilAsync(long targetTicks, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remainingTicks = targetTicks - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    return;
                }

                var remainingMs = TicksToMilliseconds(remainingTicks);
                if (remainingMs > 15.0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1.0, remainingMs - 2.0)), cancellationToken);
                }
                else
                {
                    Thread.SpinWait(100);
                }
            }
        }

        private static long MillisecondsToTicks(double milliseconds)
        {
            return Math.Max(1, (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0));
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }
    }

    private sealed class ControlMessagePublisher
    {
        private readonly object _lock = new();
        private ControlConnection? _activeConnection;

        public bool HasActiveConnection
        {
            get
            {
                lock (_lock)
                {
                    return _activeConnection is not null;
                }
            }
        }

        public void SetActive(ControlConnection connection)
        {
            lock (_lock)
            {
                _activeConnection = connection;
            }
        }

        public void ClearActive(ControlConnection connection)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_activeConnection, connection))
                {
                    _activeConnection = null;
                }
            }
        }

        public ValueTask PublishAsync(string type, JsonNode? payload, CancellationToken cancellationToken)
        {
            ControlConnection? connection;
            lock (_lock)
            {
                connection = _activeConnection;
            }

            if (type == "audio_runtime_telemetry" && payload is not null)
            {
                if (payload is JsonObject telemetryPayload)
                {
                    telemetryPayload["androidControlConnected"] = connection is not null;
                }

                Log("CONTROL", $"publish {type} payload={payload.ToJsonString(JsonOptions)}");
            }

            return connection is null
                ? ValueTask.CompletedTask
                : connection.SendAsync(type, payload, cancellationToken);
        }
    }

    private sealed class CameraRuntimeState(CameraRuntimeConfig initialConfig)
    {
        private readonly object _lock = new();
        private CameraRuntimeConfig _current = initialConfig;

        public CameraRuntimeConfig Current
        {
            get
            {
                lock (_lock)
                {
                    return _current;
                }
            }
        }

        public bool IsEnabled(HostOptions options)
        {
            lock (_lock)
            {
                return _current.IsEffectivelyEnabled(options);
            }
        }

        public CameraConfigApplyResult Apply(JsonObject payload, HostOptions options)
        {
            lock (_lock)
            {
                if (!TryCreateRequestedConfig(payload, _current, out var requested, out var error, out var warning))
                {
                    return new CameraConfigApplyResult(
                        Ok: false,
                        Message: error,
                        EffectiveConfig: _current.WithEffectiveEnabled(options));
                }

                _current = requested;
                var effective = _current.WithEffectiveEnabled(options);
                if (requested.RequestedEnabled && !options.CameraEnabled)
                {
                    return new CameraConfigApplyResult(
                        Ok: false,
                        Message: "camera is disabled by startup options",
                        EffectiveConfig: effective);
                }

                return new CameraConfigApplyResult(
                    Ok: true,
                    Message: !string.IsNullOrWhiteSpace(warning)
                        ? warning
                        : effective.Enabled ? "camera config applied; Android will restart capture" : "camera disabled",
                    EffectiveConfig: effective);
            }
        }

        private static bool TryCreateRequestedConfig(
            JsonObject payload,
            CameraRuntimeConfig current,
            out CameraRuntimeConfig config,
            out string error,
            out string warning)
        {
            config = current;
            error = string.Empty;
            warning = string.Empty;

            var requestedEnabled = current.RequestedEnabled;
            if (payload.ContainsKey("enabled") && !TryReadCommandBool(payload, "enabled", out requestedEnabled))
            {
                error = "enabled must be true or false";
                return false;
            }

            var width = current.Width;
            if (payload.ContainsKey("width") && !TryReadCommandInt(payload, "width", minValue: 1, out width))
            {
                error = "width must be a positive integer";
                return false;
            }

            var height = current.Height;
            if (payload.ContainsKey("height") && !TryReadCommandInt(payload, "height", minValue: 1, out height))
            {
                error = "height must be a positive integer";
                return false;
            }

            var fps = current.Fps;
            if (payload.ContainsKey("fps") && !TryReadCommandInt(payload, "fps", minValue: 1, out fps))
            {
                error = "fps must be a positive integer";
                return false;
            }

            var codec = current.Codec;
            if (payload.ContainsKey("codec"))
            {
                var requestedCodec = NormalizeCameraCodec(ReadCommandString(payload, "codec"));
                if (IsHostSupportedCameraCodec(requestedCodec))
                {
                    codec = requestedCodec;
                }
                else
                {
                    codec = "video/avc";
                    warning = $"camera codec {requestedCodec} is not available on the Windows receiver; falling back to video/avc";
                }
            }

            var facing = current.Facing;
            if (payload.ContainsKey("facing"))
            {
                var requestedFacing = ReadCommandString(payload, "facing");
                if (!TryNormalizeCameraFacing(requestedFacing, out facing))
                {
                    error = $"unsupported camera facing: {requestedFacing}";
                    return false;
                }
            }

            config = current with
            {
                RequestedEnabled = requestedEnabled,
                Width = width,
                Height = height,
                Fps = fps,
                Codec = codec,
                Facing = facing
            };
            return true;
        }
    }

    private sealed record CameraConfigApplyResult(bool Ok, string Message, EffectiveCameraRuntimeConfig EffectiveConfig);

    private sealed record CameraRuntimeConfig(
        bool RequestedEnabled,
        int Port,
        int Width,
        int Height,
        int Fps,
        string Codec,
        string Facing)
    {
        public static CameraRuntimeConfig FromOptions(HostOptions options)
        {
            return new CameraRuntimeConfig(
                options.CameraEnabled,
                options.CameraPort,
                options.CameraWidth,
                options.CameraHeight,
                options.CameraFps,
                NormalizeHostCameraCodec(options.CameraCodec),
                options.CameraFacing);
        }

        public bool IsEffectivelyEnabled(HostOptions options)
        {
            return options.CameraEnabled && RequestedEnabled;
        }

        public EffectiveCameraRuntimeConfig WithEffectiveEnabled(HostOptions options)
        {
            return new EffectiveCameraRuntimeConfig(
                IsEffectivelyEnabled(options),
                Port,
                Width,
                Height,
                Fps,
                Codec,
                Facing);
        }
    }

    private sealed record EffectiveCameraRuntimeConfig(
        bool Enabled,
        int Port,
        int Width,
        int Height,
        int Fps,
        string Codec,
        string Facing)
    {
        public string Summary => $"{Width}x{Height}@{Fps} codec={Codec} facing={Facing} enabled={Enabled}";
    }

    private sealed class CameraCommandServer(
        IPAddress address,
        int port,
        HostOptions options,
        ControlMessagePublisher publisher,
        CameraRuntimeState cameraRuntimeState,
        AudioTestCoordinator audioTestCoordinator)
    {
        private readonly TcpListener _listener = new(address, port);

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                _listener.Start();
                Log("CAMERA CMD", $"listening address={address} port={port}");

                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                Log("CAMERA CMD", "正在关闭。");
            }
            catch (SocketException ex)
            {
                Log("CAMERA CMD", $"listen_failed message={ex.Message}");
            }
            finally
            {
                _listener.Stop();
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            await using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            })
            {
                client.NoDelay = true;
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                {
                    await WriteCommandResponseAsync(writer, ok: false, "empty command", cancellationToken);
                    return;
                }

                ProtocolMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<ProtocolMessage>(line, JsonOptions);
                }
                catch (JsonException ex)
                {
                    await WriteCommandResponseAsync(writer, ok: false, $"invalid json: {ex.Message}", cancellationToken);
                    return;
                }

                if (message?.Payload is not JsonObject payload)
                {
                    await WriteCommandResponseAsync(writer, ok: false, "unsupported command", cancellationToken);
                    return;
                }

                if (message.Type == "host_audio_test")
                {
                    var audioTestResult = await audioTestCoordinator.RunCommandAsync(payload, cancellationToken);
                    await WriteAudioTestCommandResponseAsync(writer, audioTestResult, cancellationToken);
                    return;
                }

                if (message.Type != "host_camera_config")
                {
                    await WriteCommandResponseAsync(writer, ok: false, "unsupported command", cancellationToken);
                    return;
                }

                var result = cameraRuntimeState.Apply(payload, options);
                await publisher.PublishAsync(
                    "camera_config",
                    CreateCameraConfigPayload(result.EffectiveConfig),
                    cancellationToken);

                Log(
                    "CAMERA CMD",
                    $"camera-config-change ok={result.Ok} config={result.EffectiveConfig.Width}x{result.EffectiveConfig.Height}@{result.EffectiveConfig.Fps} "
                    + $"codec={result.EffectiveConfig.Codec} facing={result.EffectiveConfig.Facing} enabled={result.EffectiveConfig.Enabled} message={result.Message}");
                await WriteCommandResponseAsync(writer, result, cancellationToken);
            }
        }

        private static async Task WriteCommandResponseAsync(
            StreamWriter writer,
            bool ok,
            string message,
            CancellationToken cancellationToken)
        {
            var response = JsonSerializer.Serialize(CreateCommandResponsePayload(ok, message, effectiveConfig: null), JsonOptions);
            await writer.WriteLineAsync(response.AsMemory(), cancellationToken);
        }

        private static async Task WriteCommandResponseAsync(
            StreamWriter writer,
            CameraConfigApplyResult result,
            CancellationToken cancellationToken)
        {
            var response = JsonSerializer.Serialize(
                CreateCommandResponsePayload(result.Ok, result.Message, result.EffectiveConfig),
                JsonOptions);
            await writer.WriteLineAsync(response.AsMemory(), cancellationToken);
        }

        private static async Task WriteAudioTestCommandResponseAsync(
            StreamWriter writer,
            AudioTestCommandResult result,
            CancellationToken cancellationToken)
        {
            var response = JsonSerializer.Serialize(CreateAudioTestCommandResponsePayload(result), JsonOptions);
            await writer.WriteLineAsync(response.AsMemory(), cancellationToken);
        }
    }

    private static JsonObject CreateCommandResponsePayload(bool ok, string message, EffectiveCameraRuntimeConfig? effectiveConfig)
    {
        var response = new JsonObject
        {
            ["ok"] = ok,
            ["message"] = message
        };

        if (effectiveConfig is not null)
        {
            response["effective"] = CreateCameraConfigPayload(effectiveConfig);
        }

        return response;
    }

    private static JsonObject CreateAudioTestCommandResponsePayload(AudioTestCommandResult result)
    {
        return new JsonObject
        {
            ["ok"] = result.Ok,
            ["status"] = result.Status,
            ["kind"] = result.Kind,
            ["testId"] = result.TestId,
            ["message"] = result.Message,
            ["startedAtUnixMs"] = NumberOrNull(result.StartedAtUnixMs),
            ["completedAtUnixMs"] = NumberOrNull(result.CompletedAtUnixMs),
            ["stats"] = CloneJsonObject(result.Stats)
        };
    }

    private static JsonObject CloneJsonObject(JsonObject value)
    {
        return JsonNode.Parse(value.ToJsonString(JsonOptions))?.AsObject() ?? new JsonObject();
    }

    private static JsonNode? NumberOrNull(long? value)
    {
        return value.HasValue ? JsonValue.Create(value.Value) : null;
    }

    private static JsonObject CreateCameraConfigPayload(EffectiveCameraRuntimeConfig config)
    {
        return new JsonObject
        {
            ["enabled"] = config.Enabled,
            ["port"] = config.Port,
            ["width"] = config.Width,
            ["height"] = config.Height,
            ["fps"] = config.Fps,
            ["codec"] = config.Codec,
            ["facing"] = config.Facing
        };
    }

    private sealed class AudioTestCoordinator(HostOptions options, ControlMessagePublisher publisher)
    {
        private const int DefaultPlaybackDurationMs = 1600;
        private const int DefaultRecordingDurationMs = 2500;
        private const int DefaultTimeoutMs = 8000;
        private const int MinUsefulPacketCount = 3;
        private const int SilentLevelPercentThreshold = 1;
        private const double TestToneFrequencyHz = 880.0;
        private const double TestToneAmplitude = 0.24;

        private readonly object _lock = new();
        private AudioTestSession? _activeSession;
        private AudioConnectionSnapshot _connection = AudioConnectionSnapshot.Disconnected;
        private AudioDirectionSnapshot _microphone = AudioDirectionSnapshot.Unknown("microphone");
        private AudioDirectionSnapshot _speaker = AudioDirectionSnapshot.Unknown("speaker");

        public async Task<AudioTestCommandResult> RunCommandAsync(JsonObject payload, CancellationToken cancellationToken)
        {
            var kindText = ReadCommandString(payload, "kind").Trim().ToLowerInvariant();
            if (!TryParseKind(kindText, out var kind))
            {
                return CreateImmediateResult(
                    ok: false,
                    status: "failed",
                    kind: string.IsNullOrWhiteSpace(kindText) ? "unknown" : kindText,
                    testId: "",
                    message: "Unsupported audio test kind. Expected playback or recording.");
            }

            var durationMs = ReadDurationMs(payload, kind);
            var timeoutMs = ReadTimeoutMs(payload);
            if (!TryCreateSession(kind, durationMs, timeoutMs, out var session, out var preflightFailure))
            {
                return preflightFailure;
            }

            try
            {
                await publisher.PublishAsync(
                    "audio_test_request",
                    CreateAudioTestRequestPayload(session),
                    cancellationToken);
                Log(
                    "AUDIO TEST",
                    $"request kind={session.KindText} testId={session.TestId} durationMs={session.DurationMs} timeoutMs={session.TimeoutMs}");

                try
                {
                    return await session.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(session.TimeoutMs), cancellationToken);
                }
                catch (TimeoutException)
                {
                    return CompleteTimeout(session);
                }
            }
            finally
            {
                ClearActiveSession(session);
            }
        }

        public void UpdateAudioConnectionState(int connectionId, bool connected, bool microphoneEnabled, bool speakerEnabled)
        {
            lock (_lock)
            {
                _connection = connected
                    ? new AudioConnectionSnapshot(true, connectionId, microphoneEnabled, speakerEnabled)
                    : AudioConnectionSnapshot.Disconnected;
            }
        }

        public void UpdateDirectionSnapshot(AudioTestDirection direction, string state, string message, bool endpointReady)
        {
            var snapshot = new AudioDirectionSnapshot(
                direction == AudioTestDirection.Microphone ? "microphone" : "speaker",
                string.IsNullOrWhiteSpace(state) ? "unknown" : state,
                string.IsNullOrWhiteSpace(message) ? "" : message,
                endpointReady);

            lock (_lock)
            {
                if (direction == AudioTestDirection.Microphone)
                {
                    _microphone = snapshot;
                }
                else
                {
                    _speaker = snapshot;
                }
            }
        }

        public bool TryReadPlaybackTone(byte[] destination, int maxByteCount, out int byteCount)
        {
            byteCount = 0;
            AudioTestSession? session;
            lock (_lock)
            {
                session = _activeSession;
                if (session is null || session.Kind != AudioTestKind.Playback || session.IsHostPlaybackComplete)
                {
                    return false;
                }

                var totalFrames = AudioDefaults.SampleRate * session.DurationMs / 1000;
                if (session.PlaybackFramesGenerated >= totalFrames)
                {
                    session.HostStats.CompletedAtUnixMs ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    session.IsHostPlaybackComplete = true;
                    return false;
                }

                var frameBytes = AudioDefaults.SpeakerFrameBytes;
                var maxFrames = Math.Max(1, Math.Min(maxByteCount, destination.Length) / frameBytes);
                var remainingFrames = totalFrames - session.PlaybackFramesGenerated;
                var frames = (int)Math.Min(maxFrames, remainingFrames);
                byteCount = frames * frameBytes;
                session.HostStats.StartedAtUnixMs ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                GeneratePlaybackTone(destination, byteCount, session.PlaybackFramesGenerated);
                session.PlaybackFramesGenerated += frames;
                session.HostStats.Packets += 1;
                session.HostStats.Bytes += byteCount;
                session.HostStats.PeakLevelPercent = Math.Max(session.HostStats.PeakLevelPercent, 24);
                if (session.PlaybackFramesGenerated >= totalFrames)
                {
                    session.HostStats.CompletedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    session.IsHostPlaybackComplete = true;
                }

                return true;
            }
        }

        public void RecordMicrophonePacket(int byteCount, int levelPercent, bool endpointReady, string endpointMessage)
        {
            lock (_lock)
            {
                var session = _activeSession;
                if (session is null || session.Kind != AudioTestKind.Recording)
                {
                    return;
                }

                session.HostStats.StartedAtUnixMs ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                session.HostStats.Packets += 1;
                session.HostStats.Bytes += Math.Max(0, byteCount);
                session.HostStats.PeakLevelPercent = Math.Max(session.HostStats.PeakLevelPercent, Math.Clamp(levelPercent, 0, 100));
                if (levelPercent <= SilentLevelPercentThreshold)
                {
                    session.HostStats.SilentPackets += 1;
                }

                session.HostStats.EndpointReady = endpointReady;
                if (!endpointReady)
                {
                    session.HostStats.EndpointFailures += 1;
                    session.HostStats.EndpointMessage = string.IsNullOrWhiteSpace(endpointMessage) ? "Windows endpoint is unavailable." : endpointMessage;
                }
                else if (string.IsNullOrWhiteSpace(session.HostStats.EndpointMessage))
                {
                    session.HostStats.EndpointMessage = endpointMessage;
                }
            }
        }

        public void HandleAndroidStatus(JsonObject payload)
        {
            var testId = ReadString(payload, "testId");
            if (string.IsNullOrWhiteSpace(testId))
            {
                return;
            }

            AudioTestSession? session;
            AudioTestCommandResult? result = null;
            lock (_lock)
            {
                session = _activeSession;
                if (session is null || !string.Equals(session.TestId, testId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                session.AndroidPayload = CloneJsonObject(payload);
                session.AndroidUpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var status = ReadString(payload, "status");
                if (IsTerminalStatus(status))
                {
                    session.HostStats.CompletedAtUnixMs ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    result = BuildFinalResultLocked(session);
                    session.Completion.TrySetResult(result);
                }
            }

            if (result is not null)
            {
                Log(
                    "AUDIO TEST",
                    $"complete kind={result.Kind} testId={result.TestId} status={result.Status} ok={result.Ok} message={result.Message}");
            }
        }

        private bool TryCreateSession(
            AudioTestKind kind,
            int durationMs,
            int timeoutMs,
            out AudioTestSession session,
            out AudioTestCommandResult failure)
        {
            session = null!;
            failure = null!;
            var kindText = KindText(kind);

            if (!options.AudioDeviceEnabled)
            {
                failure = CreateImmediateResult(false, "failed", kindText, "", "Audio is disabled. Enable SideDock audio first.");
                return false;
            }

            if (kind == AudioTestKind.Playback && !options.SpeakerEnabled)
            {
                failure = CreateImmediateResult(false, "failed", kindText, "", "Playback test requires the computer sound -> Android direction to be enabled.");
                return false;
            }

            if (kind == AudioTestKind.Recording && !options.MicrophoneEnabled)
            {
                failure = CreateImmediateResult(false, "failed", kindText, "", "Recording test requires the Android microphone -> Windows direction to be enabled.");
                return false;
            }

            if (!publisher.HasActiveConnection)
            {
                failure = CreateImmediateResult(false, "failed", kindText, "", "Android control channel is not connected.");
                return false;
            }

            lock (_lock)
            {
                if (_activeSession is not null)
                {
                    failure = CreateImmediateResult(false, "failed", kindText, "", "Another audio test is already running.");
                    return false;
                }

                if (!_connection.Connected)
                {
                    failure = CreateImmediateResult(false, "failed", kindText, "", "Android audio socket is not connected yet.");
                    return false;
                }

                if (kind == AudioTestKind.Playback && !_connection.SpeakerEnabled)
                {
                    failure = CreateImmediateResult(false, "failed", kindText, "", "Speaker audio stream is not available on the current Android connection.");
                    return false;
                }

                if (kind == AudioTestKind.Recording && !_connection.MicrophoneEnabled)
                {
                    failure = CreateImmediateResult(false, "failed", kindText, "", "Microphone audio stream is not available on the current Android connection.");
                    return false;
                }

                var directionSnapshot = kind == AudioTestKind.Playback ? _speaker : _microphone;
                if (!directionSnapshot.EndpointReady)
                {
                    failure = CreateImmediateResult(
                        false,
                        "failed",
                        kindText,
                        "",
                        string.IsNullOrWhiteSpace(directionSnapshot.Message)
                            ? "Selected Windows audio endpoint is not ready."
                            : directionSnapshot.Message);
                    return false;
                }

                session = new AudioTestSession(Guid.NewGuid().ToString("N"), kind, durationMs, timeoutMs);
                _activeSession = session;
                return true;
            }
        }

        private AudioTestCommandResult CompleteTimeout(AudioTestSession session)
        {
            var result = BuildFailureResult(
                session,
                "timeout",
                $"Audio {session.KindText} test timed out after {session.TimeoutMs} ms.");
            session.Completion.TrySetResult(result);
            return result;
        }

        private void ClearActiveSession(AudioTestSession session)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_activeSession, session))
                {
                    _activeSession = null;
                }
            }
        }

        private AudioTestCommandResult BuildFinalResultLocked(AudioTestSession session)
        {
            if (session.AndroidPayload is null)
            {
                return BuildFailureResult(session, "failed", "Android did not return an audio test result.");
            }

            return session.Kind == AudioTestKind.Playback
                ? BuildPlaybackResultLocked(session, session.AndroidPayload)
                : BuildRecordingResultLocked(session, session.AndroidPayload);
        }

        private AudioTestCommandResult BuildPlaybackResultLocked(AudioTestSession session, JsonObject android)
        {
            var androidStatus = ReadString(android, "status");
            var androidOk = ReadBool(android, "ok") || androidStatus.Equals("passed", StringComparison.OrdinalIgnoreCase);
            var androidMessage = ReadString(android, "message");
            var packetsReceived = ReadLong(android, "packetsReceived");
            var bytesReceived = ReadLong(android, "bytesReceived");
            var writeErrors = ReadLong(android, "writeErrors");

            if (session.HostStats.Packets <= 0 || session.HostStats.Bytes <= 0)
            {
                return BuildFailureResult(session, "failed", "Host did not send any playback test packets to Android.");
            }

            if (!androidOk)
            {
                return BuildFailureResult(
                    session,
                    "failed",
                    string.IsNullOrWhiteSpace(androidMessage) ? "Android playback test failed." : androidMessage);
            }

            if (packetsReceived <= 0 || bytesReceived <= 0)
            {
                return BuildFailureResult(session, "failed", "Android did not receive playback test packets.");
            }

            if (writeErrors > 0)
            {
                return BuildFailureResult(session, "failed", "Android AudioTrack reported write errors during playback test.");
            }

            return BuildResult(
                session,
                ok: true,
                status: "passed",
                message: $"Playback test passed: Android received {packetsReceived} packets / {bytesReceived} bytes and wrote them to AudioTrack.");
        }

        private AudioTestCommandResult BuildRecordingResultLocked(AudioTestSession session, JsonObject android)
        {
            var androidStatus = ReadString(android, "status");
            var androidOk = ReadBool(android, "ok") || androidStatus.Equals("passed", StringComparison.OrdinalIgnoreCase);
            var androidMessage = ReadString(android, "message");
            var permissionGranted = ReadNullableBool(android, "permissionGranted");
            var packetsSent = ReadLong(android, "packetsSent");
            var bytesSent = ReadLong(android, "bytesSent");
            var androidPeakLevel = ReadLong(android, "peakLevelPercent");

            if (permissionGranted == false)
            {
                return BuildFailureResult(session, "failed", "Android microphone permission is missing.");
            }

            if (!androidOk)
            {
                return BuildFailureResult(
                    session,
                    "failed",
                    string.IsNullOrWhiteSpace(androidMessage) ? "Android recording test failed." : androidMessage);
            }

            if (packetsSent <= 0 || bytesSent <= 0)
            {
                return BuildFailureResult(session, "failed", "Android did not send microphone packets during the recording test.");
            }

            if (session.HostStats.Packets < MinUsefulPacketCount || session.HostStats.Bytes <= 0)
            {
                return BuildFailureResult(session, "failed", "Host did not receive enough microphone packets from Android.");
            }

            if (session.HostStats.EndpointFailures > 0 || session.HostStats.EndpointReady == false)
            {
                return BuildFailureResult(
                    session,
                    "failed",
                    string.IsNullOrWhiteSpace(session.HostStats.EndpointMessage)
                        ? "Host microphone render endpoint is not ready."
                        : session.HostStats.EndpointMessage);
            }

            var silentRatio = session.HostStats.Packets <= 0
                ? 1.0
                : session.HostStats.SilentPackets / (double)session.HostStats.Packets;
            var peakLevel = Math.Max(session.HostStats.PeakLevelPercent, (int)Math.Clamp(androidPeakLevel, 0, 100));
            if (peakLevel <= SilentLevelPercentThreshold || silentRatio >= 0.95)
            {
                return BuildResult(
                    session,
                    ok: true,
                    status: "passed_silent",
                    message: "Recording link passed, but the input level was silent or very low. Speak near the Android microphone and test again if the call app still hears nothing.");
            }

            return BuildResult(
                session,
                ok: true,
                status: "passed",
                message: $"Recording test passed: Host received {session.HostStats.Packets} packets / {session.HostStats.Bytes} bytes and wrote them to the Windows endpoint.");
        }

        private AudioTestCommandResult BuildFailureResult(AudioTestSession session, string status, string message)
        {
            return BuildResult(session, ok: false, status, message);
        }

        private AudioTestCommandResult BuildResult(AudioTestSession session, bool ok, string status, string message)
        {
            var completedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var stats = new JsonObject
            {
                ["host"] = session.HostStats.ToJson(),
                ["android"] = session.AndroidPayload is null ? null : CloneJsonObject(session.AndroidPayload)
            };

            return new AudioTestCommandResult(
                Ok: ok,
                Status: status,
                Kind: session.KindText,
                TestId: session.TestId,
                Message: message,
                StartedAtUnixMs: session.StartedAtUnixMs,
                CompletedAtUnixMs: completedAt,
                Stats: stats);
        }

        private static AudioTestCommandResult CreateImmediateResult(
            bool ok,
            string status,
            string kind,
            string testId,
            string message)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return new AudioTestCommandResult(
                ok,
                status,
                kind,
                testId,
                message,
                StartedAtUnixMs: now,
                CompletedAtUnixMs: now,
                Stats: new JsonObject());
        }

        private static JsonObject CreateAudioTestRequestPayload(AudioTestSession session)
        {
            return new JsonObject
            {
                ["testId"] = session.TestId,
                ["kind"] = session.KindText,
                ["durationMs"] = session.DurationMs,
                ["timeoutMs"] = session.TimeoutMs,
                ["sampleRate"] = AudioDefaults.SampleRate,
                ["microphoneChannels"] = AudioDefaults.MicChannels,
                ["speakerChannels"] = AudioDefaults.SpeakerChannels,
                ["bitsPerSample"] = AudioDefaults.BitsPerSample,
                ["startedAtUnixMs"] = session.StartedAtUnixMs
            };
        }

        private static void GeneratePlaybackTone(byte[] destination, int byteCount, long startFrame)
        {
            var frameCount = byteCount / AudioDefaults.SpeakerFrameBytes;
            for (var frame = 0; frame < frameCount; frame++)
            {
                var sample = (short)Math.Round(Math.Sin((startFrame + frame) * Math.Tau * TestToneFrequencyHz / AudioDefaults.SampleRate)
                    * short.MaxValue
                    * TestToneAmplitude);
                var offset = frame * AudioDefaults.SpeakerFrameBytes;
                BinaryPrimitives.WriteInt16LittleEndian(destination.AsSpan(offset, 2), sample);
                BinaryPrimitives.WriteInt16LittleEndian(destination.AsSpan(offset + 2, 2), sample);
            }
        }

        private static string ReadString(JsonObject payload, string name)
        {
            if (!payload.TryGetPropertyValue(name, out var node) || node is null)
            {
                return "";
            }

            try
            {
                return node.GetValue<string>() ?? "";
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                return "";
            }
        }

        private static long ReadLong(JsonObject payload, string name)
        {
            if (!payload.TryGetPropertyValue(name, out var node) || node is null)
            {
                return 0;
            }

            try
            {
                return node.GetValue<long>();
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                return 0;
            }
        }

        private static bool ReadBool(JsonObject payload, string name)
        {
            return ReadNullableBool(payload, name) == true;
        }

        private static bool? ReadNullableBool(JsonObject payload, string name)
        {
            if (!payload.TryGetPropertyValue(name, out var node) || node is null)
            {
                return null;
            }

            try
            {
                return node.GetValue<bool>();
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                return null;
            }
        }

        private static int ReadDurationMs(JsonObject payload, AudioTestKind kind)
        {
            var fallback = kind == AudioTestKind.Playback ? DefaultPlaybackDurationMs : DefaultRecordingDurationMs;
            return TryReadCommandInt(payload, "durationMs", 1, out var value)
                ? Math.Clamp(value, 500, 4000)
                : fallback;
        }

        private static int ReadTimeoutMs(JsonObject payload)
        {
            return TryReadCommandInt(payload, "timeoutMs", 1, out var value)
                ? Math.Clamp(value, 3000, 12000)
                : DefaultTimeoutMs;
        }

        private static bool TryParseKind(string value, out AudioTestKind kind)
        {
            if (value.Equals("playback", StringComparison.OrdinalIgnoreCase))
            {
                kind = AudioTestKind.Playback;
                return true;
            }

            if (value.Equals("recording", StringComparison.OrdinalIgnoreCase))
            {
                kind = AudioTestKind.Recording;
                return true;
            }

            kind = AudioTestKind.Playback;
            return false;
        }

        private static bool IsTerminalStatus(string status)
        {
            return status.Equals("passed", StringComparison.OrdinalIgnoreCase)
                || status.Equals("passed_silent", StringComparison.OrdinalIgnoreCase)
                || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                || status.Equals("completed", StringComparison.OrdinalIgnoreCase);
        }

        private static string KindText(AudioTestKind kind)
        {
            return kind == AudioTestKind.Playback ? "playback" : "recording";
        }

        private sealed class AudioTestSession(string testId, AudioTestKind kind, int durationMs, int timeoutMs)
        {
            public string TestId { get; } = testId;

            public AudioTestKind Kind { get; } = kind;

            public string KindText => AudioTestCoordinator.KindText(Kind);

            public int DurationMs { get; } = durationMs;

            public int TimeoutMs { get; } = timeoutMs;

            public long StartedAtUnixMs { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            public TaskCompletionSource<AudioTestCommandResult> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public AudioTestHostStats HostStats { get; } = new();

            public JsonObject? AndroidPayload { get; set; }

            public long? AndroidUpdatedAtUnixMs { get; set; }

            public long PlaybackFramesGenerated { get; set; }

            public bool IsHostPlaybackComplete { get; set; }
        }

        private sealed class AudioTestHostStats
        {
            public long Packets { get; set; }

            public long Bytes { get; set; }

            public int PeakLevelPercent { get; set; }

            public long SilentPackets { get; set; }

            public bool? EndpointReady { get; set; }

            public long EndpointFailures { get; set; }

            public string EndpointMessage { get; set; } = "";

            public long? StartedAtUnixMs { get; set; }

            public long? CompletedAtUnixMs { get; set; }

            public JsonObject ToJson()
            {
                var silentRatio = Packets <= 0 ? 0 : SilentPackets / (double)Packets;
                return new JsonObject
                {
                    ["packets"] = Packets,
                    ["bytes"] = Bytes,
                    ["peakLevelPercent"] = PeakLevelPercent,
                    ["silentPackets"] = SilentPackets,
                    ["silentRatio"] = Math.Round(silentRatio, 3),
                    ["endpointReady"] = EndpointReady.HasValue ? JsonValue.Create(EndpointReady.Value) : null,
                    ["endpointFailures"] = EndpointFailures,
                    ["endpointMessage"] = string.IsNullOrWhiteSpace(EndpointMessage) ? null : EndpointMessage,
                    ["startedAtUnixMs"] = NumberOrNull(StartedAtUnixMs),
                    ["completedAtUnixMs"] = NumberOrNull(CompletedAtUnixMs)
                };
            }
        }
    }

    private enum AudioTestKind
    {
        Playback,
        Recording
    }

    private enum AudioTestDirection
    {
        Microphone,
        Speaker
    }

    private sealed record AudioConnectionSnapshot(
        bool Connected,
        int ConnectionId,
        bool MicrophoneEnabled,
        bool SpeakerEnabled)
    {
        public static AudioConnectionSnapshot Disconnected { get; } = new(false, 0, false, false);
    }

    private sealed record AudioDirectionSnapshot(
        string Direction,
        string State,
        string Message,
        bool EndpointReady)
    {
        public static AudioDirectionSnapshot Unknown(string direction)
        {
            return new AudioDirectionSnapshot(direction, "unknown", "", false);
        }
    }

    private sealed record AudioTestCommandResult(
        bool Ok,
        string Status,
        string Kind,
        string TestId,
        string Message,
        long? StartedAtUnixMs,
        long? CompletedAtUnixMs,
        JsonObject Stats);

    private static bool TryReadCommandBool(JsonObject payload, string name, out bool value)
    {
        value = false;
        if (!payload.TryGetPropertyValue(name, out var node) || node is null)
        {
            return false;
        }

        try
        {
            value = node.GetValue<bool>();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static bool TryReadCommandInt(JsonObject payload, string name, int minValue, out int value)
    {
        value = 0;
        if (!payload.TryGetPropertyValue(name, out var node) || node is null)
        {
            return false;
        }

        try
        {
            value = node.GetValue<int>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            try
            {
                value = (int)Math.Round(node.GetValue<double>());
            }
            catch (Exception innerEx) when (innerEx is InvalidOperationException or FormatException)
            {
                return false;
            }
        }

        return value >= minValue;
    }

    private static string ReadCommandString(JsonObject payload, string name)
    {
        if (!payload.TryGetPropertyValue(name, out var node) || node is null)
        {
            return string.Empty;
        }

        try
        {
            return node.GetValue<string>() ?? string.Empty;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return string.Empty;
        }
    }

    private static string NormalizeCameraCodec(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "video/avc";
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "avc" or "h264" or "h.264" or "video/h264" => "video/avc",
            "hevc" or "h265" or "h.265" or "video/h265" => "video/hevc",
            var codec => codec
        };
    }

    private static string NormalizeHostCameraCodec(string? value)
    {
        var codec = NormalizeCameraCodec(value);
        return IsHostSupportedCameraCodec(codec) ? codec : "video/avc";
    }

    private static bool IsHostSupportedCameraCodec(string codec)
    {
        return codec.Trim().Equals("video/avc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeCameraFacing(string? value, out string facing)
    {
        facing = "back";
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is "back" or "rear")
        {
            facing = "back";
            return true;
        }

        if (normalized is "front")
        {
            facing = "front";
            return true;
        }

        return false;
    }

    private sealed class ControlConnection
    {
        private readonly int _connectionId;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private long _seq;

        public ControlConnection(int connectionId, StreamWriter writer)
        {
            _connectionId = connectionId;
            _writer = writer;
        }

        public async ValueTask SendAsync(string type, JsonNode? payload, CancellationToken cancellationToken)
        {
            var message = new ProtocolMessage(
                V: 1,
                Type: type,
                Seq: Interlocked.Increment(ref _seq),
                Ts: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload: payload);

            var line = JsonSerializer.Serialize(message, JsonOptions);
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }

            if (!IsHighFrequencyServerMessage(type))
            {
                Log($"CONN {_connectionId}", $"发送 {type} seq={message.Seq}");
            }
        }

        private static bool IsHighFrequencyServerMessage(string type)
        {
            return type is "cursor_state" or "encoder_stats" or "capture_stats" or "pipeline_stats";
        }
    }

    private sealed class ControlServer(
        IPAddress address,
        HostOptions options,
        VideoModeState videoModeState,
        ControlMessagePublisher publisher,
        DisplayLayoutProvider displayLayoutProvider,
        CameraRuntimeState cameraRuntimeState,
        AudioTestCoordinator audioTestCoordinator)
    {
        private readonly TcpListener _listener = new(address, options.ControlPort);
        private readonly HostOptions _options = options;
        private readonly VideoModeState _videoModeState = videoModeState;
        private readonly ControlMessagePublisher _publisher = publisher;
        private readonly DisplayLayoutProvider _displayLayoutProvider = displayLayoutProvider;
        private readonly CameraRuntimeState _cameraRuntimeState = cameraRuntimeState;
        private readonly object _connectionLock = new();
        private CancellationTokenSource? _activeConnectionCts;
        private int _connectionSerial;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                _listener.Start();
                Log("CONTROL", "等待 Android 控制通道连接...");

                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    var connectionId = Interlocked.Increment(ref _connectionSerial);
                    var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    CancellationTokenSource? previousConnectionCts;
                    lock (_connectionLock)
                    {
                        previousConnectionCts = _activeConnectionCts;
                        _activeConnectionCts = connectionCts;
                    }

                    if (previousConnectionCts is not null)
                    {
                        Log("CONTROL", "检测到新的 Android 控制连接，关闭旧连接。");
                        await previousConnectionCts.CancelAsync();
                    }

                    _ = Task.Run(() => HandleClientAsync(connectionId, client, connectionCts, cancellationToken), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                Log("CONTROL", "正在关闭。");
            }
            catch (SocketException ex)
            {
                Log("CONTROL", $"监听失败: {ex.Message}");
            }
            finally
            {
                _listener.Stop();
            }
        }

        private async Task HandleClientAsync(
            int connectionId,
            TcpClient client,
            CancellationTokenSource connectionCts,
            CancellationToken appToken)
        {
            using (client)
            using (connectionCts)
            {
                var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                var session = new ControlSession(connectionId, client, connectionCts, _options, _videoModeState, _publisher, _displayLayoutProvider, _cameraRuntimeState, audioTestCoordinator);
                Log($"CONN {connectionId}", $"控制通道已连接: {remote}");

                try
                {
                    await session.RunAsync(appToken);
                }
                catch (OperationCanceledException) when (appToken.IsCancellationRequested)
                {
                    // Application shutdown.
                }
                catch (Exception ex)
                {
                    Log($"CONN {connectionId}", $"控制通道异常: {ex.Message}");
                }
                finally
                {
                    lock (_connectionLock)
                    {
                        if (ReferenceEquals(_activeConnectionCts, connectionCts))
                        {
                            _activeConnectionCts = null;
                        }
                    }

                    Log($"CONN {connectionId}", "控制通道已断开");
                }
            }
        }
    }

    private sealed class ControlSession
    {
        private readonly int _connectionId;
        private readonly TcpClient _client;
        private readonly CancellationTokenSource _connectionCts;
        private readonly HostOptions _options;
        private readonly VideoModeState _videoModeState;
        private readonly ControlMessagePublisher _publisher;
        private readonly DisplayLayoutProvider _displayLayoutProvider;
        private readonly CameraRuntimeState _cameraRuntimeState;
        private readonly AudioTestCoordinator _audioTestCoordinator;
        private readonly HostInputController _inputController;
        private readonly PeriodicTimer _heartbeatTimer = new(TimeSpan.FromSeconds(2));
        private readonly PeriodicTimer _inputStatsTimer = new(TimeSpan.FromSeconds(1));
        private readonly PeriodicTimer _displayLayoutTimer = new(TimeSpan.FromSeconds(2));
        private readonly PeriodicTimer _cursorStateTimer = new(TimeSpan.FromMilliseconds(16));
        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private DisplayMetrics? _lastPublishedMetrics;
        private DisplayModeRequest? _lastObservedDisplayMode;
        private CursorState? _lastPublishedCursorState;
        private long _cursorStateLogCounter;
        private int _missedPongs;
        private DateTimeOffset _lastPong = DateTimeOffset.UtcNow;

        public ControlSession(
            int connectionId,
            TcpClient client,
            CancellationTokenSource connectionCts,
            HostOptions options,
            VideoModeState videoModeState,
            ControlMessagePublisher publisher,
            DisplayLayoutProvider displayLayoutProvider,
            CameraRuntimeState cameraRuntimeState,
            AudioTestCoordinator audioTestCoordinator)
        {
            _connectionId = connectionId;
            _client = client;
            _connectionCts = connectionCts;
            _options = options;
            _videoModeState = videoModeState;
            _publisher = publisher;
            _displayLayoutProvider = displayLayoutProvider;
            _cameraRuntimeState = cameraRuntimeState;
            _audioTestCoordinator = audioTestCoordinator;
            _client.NoDelay = true;
            _inputController = new HostInputController(
                options.EnableInputInjection,
                options.InputTarget,
                _displayLayoutProvider,
                message => Log(Scope, message));
        }

        public async Task RunAsync(CancellationToken appToken)
        {
            await using var stream = _client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 8192, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            var connection = new ControlConnection(_connectionId, writer);
            _publisher.SetActive(connection);

            try
            {
                var heartbeatTask = HeartbeatAsync(connection, _connectionCts.Token);
                var inputStatsTask = InputStatsAsync(_connectionCts.Token);
                var displayLayoutTask = DisplayLayoutAsync(connection, _connectionCts.Token);
                var cursorStateTask = CursorStateAsync(connection, _connectionCts.Token);
                var readTask = ReadLoopAsync(reader, connection, _connectionCts.Token);
                await Task.WhenAny(heartbeatTask, readTask);
                await _connectionCts.CancelAsync();
                await Task.WhenAll(heartbeatTask, inputStatsTask, displayLayoutTask, cursorStateTask, readTask);
            }
            catch (OperationCanceledException)
            {
                // Expected when either side closes the connection.
            }
            finally
            {
                _publisher.ClearActive(connection);
            }
        }

        private async Task ReadLoopAsync(StreamReader reader, ControlConnection connection, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    Log(Scope, "对端关闭控制通道");
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ProtocolMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<ProtocolMessage>(line, JsonOptions);
                }
                catch (JsonException ex)
                {
                    Log(Scope, $"JSON 解析失败: {ex.Message}; 原始内容: {line}");
                    continue;
                }

                if (message is null)
                {
                    continue;
                }

                await HandleMessageAsync(message, connection, cancellationToken);
            }

            await _connectionCts.CancelAsync();
        }

        private async Task HandleMessageAsync(ProtocolMessage message, ControlConnection connection, CancellationToken cancellationToken)
        {
            if (!message.Type.StartsWith("input_", StringComparison.Ordinal))
            {
                Log(Scope, $"收到 {message.Type} seq={message.Seq}");
            }

            switch (message.Type)
            {
                case "hello":
                    var helloVideoMode = _videoModeState.Current;
                    var cameraConfig = _cameraRuntimeState.Current.WithEffectiveEnabled(_options);
                    await connection.SendAsync("hello_ack", new JsonObject
                    {
                        ["server"] = "SideDock.Host",
                        ["serverTimeMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ["heartbeatMs"] = 2000,
                        ["missLimit"] = 3,
                        ["videoPort"] = _options.VideoPort,
                        ["videoWidth"] = helloVideoMode.Width,
                        ["videoHeight"] = helloVideoMode.Height,
                        ["videoFps"] = helloVideoMode.Fps,
                        ["videoSource"] = FormatVideoSource(_options.VideoSource),
                        ["encoder"] = _options.Encoder.ToString().ToLowerInvariant(),
                        ["inputTarget"] = _options.InputTarget.ToString().ToLowerInvariant(),
                        ["audioPort"] = _options.AudioPort,
                        ["audioEnabled"] = _options.AudioDeviceEnabled,
                        ["microphoneEnabled"] = _options.MicrophoneEnabled,
                        ["speakerEnabled"] = _options.SpeakerEnabled,
                        ["audioSampleRate"] = AudioDefaults.SampleRate,
                        ["audioChannels"] = AudioDefaults.MicChannels,
                        ["microphoneChannels"] = AudioDefaults.MicChannels,
                        ["speakerChannels"] = AudioDefaults.SpeakerChannels,
                        ["audioBitsPerSample"] = AudioDefaults.BitsPerSample,
                        ["cameraEnabled"] = cameraConfig.Enabled,
                        ["cameraPort"] = cameraConfig.Port,
                        ["cameraWidth"] = cameraConfig.Width,
                        ["cameraHeight"] = cameraConfig.Height,
                        ["cameraFps"] = cameraConfig.Fps,
                        ["cameraCodec"] = cameraConfig.Codec,
                        ["cameraFacing"] = cameraConfig.Facing
                    }, cancellationToken);
                    await connection.SendAsync("camera_config", CreateCameraConfigPayload(cameraConfig), cancellationToken);
                    await PublishDisplayMetricsIfChangedAsync(connection, force: true, cancellationToken);
                    break;

                case "ping":
                    var clientSentAtMs = message.Payload is JsonObject pingPayload
                        ? ReadLong(pingPayload, "clientSentAtMs")
                        : 0;
                    await connection.SendAsync("pong", new JsonObject
                    {
                        ["replyTo"] = message.Seq,
                        ["clientSentAtMs"] = clientSentAtMs,
                        ["serverTimeMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ["uptimeMs"] = _uptime.ElapsedMilliseconds
                    }, cancellationToken);
                    break;

                case "pong":
                    _missedPongs = 0;
                    _lastPong = DateTimeOffset.UtcNow;
                    break;

                case "video_ready":
                    if (message.Payload is not null)
                    {
                        Log(Scope, $"video_ready payload={message.Payload}");
                    }

                    var readyVideoMode = _videoModeState.Current;
                    if (message.Payload is JsonObject readyPayload)
                    {
                        var readyWidth = ReadLong(readyPayload, "width");
                        var readyHeight = ReadLong(readyPayload, "height");
                        if (readyWidth > 0
                            && readyHeight > 0
                            && (readyWidth != readyVideoMode.Width || readyHeight != readyVideoMode.Height))
                        {
                            Log(Scope, $"video_ready surface={readyWidth}x{readyHeight}; starting mode {readyVideoMode.Width}x{readyVideoMode.Height}");
                        }
                    }

                    await connection.SendAsync("video_start", new JsonObject
                    {
                        ["videoPort"] = _options.VideoPort,
                        ["width"] = readyVideoMode.Width,
                        ["height"] = readyVideoMode.Height,
                        ["fps"] = readyVideoMode.Fps,
                        ["codec"] = "video/avc",
                        ["format"] = "annexb"
                    }, cancellationToken);
                    await PublishDisplayMetricsIfChangedAsync(connection, force: true, cancellationToken);
                    break;

                case "display_mode_change":
                    await HandleDisplayModeChangeAsync(message, connection, cancellationToken);
                    break;

                case "camera_capabilities":
                    if (message.Payload is not null)
                    {
                        LogCameraCapabilities(message.Payload);
                    }

                    break;

                case "input_keyboard":
                case "input_pointer_abs":
                case "input_mouse_move":
                case "input_mouse_button":
                case "input_mouse_wheel":
                    await HandleInputMessageAsync(message, connection, cancellationToken);
                    break;

                case "status":
                case "log":
                case "audio_mic_status":
                case "audio_speaker_status":
                case "audio_runtime_telemetry":
                case "camera_status":
                case "input_stats":
                case "input_error":
                    if (message.Payload is not null)
                    {
                        Log(Scope, $"{message.Type} payload={message.Payload}");
                        if (message.Type == "audio_mic_status")
                        {
                            LogAudioMicrophoneStatus(message.Payload);
                        }
                        else if (message.Type == "audio_speaker_status")
                        {
                            LogAudioSpeakerStatus(message.Payload);
                        }
                        else if (message.Type == "camera_status")
                        {
                            LogCameraStatus(message.Payload);
                        }
                    }

                    break;

                case "audio_test_status":
                    if (message.Payload is JsonObject audioTestPayload)
                    {
                        Log(Scope, $"audio_test_status payload={audioTestPayload}");
                        _audioTestCoordinator.HandleAndroidStatus(audioTestPayload);
                    }

                    break;

                case "video_error":
                    await HandleVideoErrorAsync(message, connection, cancellationToken);
                    break;

                case "video_stats":
                    LogVideoStats(message);
                    break;

                case "close":
                    Log(Scope, "收到 close，准备断开");
                    await _connectionCts.CancelAsync();
                    break;

                default:
                    Log(Scope, $"未知消息类型: {message.Type}");
                    break;
            }
        }

        private void LogVideoStats(ProtocolMessage message)
        {
            if (message.Payload is not JsonObject payload)
            {
                return;
            }

            var frameKind = payload.TryGetPropertyValue("lastFrameKind", out var frameKindNode)
                ? frameKindNode?.GetValue<string>() ?? ""
                : "";
            Log(
                Scope,
                "video stats "
                + $"decoded={ReadLong(payload, "framesDecoded")} rendered={ReadLong(payload, "framesRendered")} packets={ReadLong(payload, "packetsReceived")} "
                + $"fps decode={ReadDouble(payload, "decodeFps"):F1} render={ReadDouble(payload, "renderFps"):F1} new={ReadDouble(payload, "newFrameFps"):F1} repeat={ReadDouble(payload, "repeatFrameFps"):F1} "
                + $"latency local={ReadDouble(payload, "localPipelineLatencyMs"):F0}ms e2e={ReadLong(payload, "roughLatencyMs")}ms err=+/-{ReadLong(payload, "latencyErrorBoundMs")}ms "
                + $"kind={frameKind} sourceSeq={ReadLong(payload, "lastSourceSeq")} sourceAge={ReadDouble(payload, "lastSourceAgeMs"):F0}ms "
                + $"android receiveQueue={ReadDouble(payload, "receiveToQueueMs"):F1}ms queueOutput={ReadDouble(payload, "queueToOutputMs"):F1}ms outputRender={ReadDouble(payload, "outputToRenderMs"):F1}ms queueRender={ReadDouble(payload, "queueToRenderMs"):F1}ms "
                + $"androidP95 queueOutput={ReadDouble(payload, "p95QueueToOutputMs"):F1}ms outputRender={ReadDouble(payload, "p95OutputToRenderMs"):F1}ms queueRender={ReadDouble(payload, "p95QueueToRenderMs"):F1}ms "
                + $"androidP99 queueOutput={ReadDouble(payload, "p99QueueToOutputMs"):F1}ms outputRender={ReadDouble(payload, "p99OutputToRenderMs"):F1}ms queueRender={ReadDouble(payload, "p99QueueToRenderMs"):F1}ms "
                + $"decodeErrors={ReadLong(payload, "decodeErrors")} reconnects={ReadLong(payload, "videoReconnects")}");
        }

        private void LogAudioMicrophoneStatus(JsonNode payloadNode)
        {
            if (payloadNode is not JsonObject payload)
            {
                return;
            }

            var state = ReadString(payload, "state");
            var message = ReadString(payload, "message");
            var logKey = state is "capturing" or "available" or "preparing"
                ? "mic-client-state"
                : "mic-state";
            Log(
                "AUDIO",
                logKey + "=" + (string.IsNullOrWhiteSpace(state) ? "unknown" : state)
                + $" muted={ReadBool(payload, "muted")}"
                + $" stopped={ReadBool(payload, "stopped")}"
                + $" permission={ReadBool(payload, "permissionGranted")}"
                + $" packets={ReadLong(payload, "packets")}"
                + $" bytes={ReadLong(payload, "bytes")}"
                + $" peak={ReadLong(payload, "peakSample")}"
                + $" silentPackets={ReadLong(payload, "silentPackets")}"
                + $" source={ReadString(payload, "audioSource")}"
                + $" port={ReadLong(payload, "port")}"
                + $" sampleRate={ReadLong(payload, "sampleRate")}"
                + $" channels={ReadLong(payload, "channels")}"
                + (string.IsNullOrWhiteSpace(message) ? string.Empty : $" message={message}"));
        }

        private void LogAudioSpeakerStatus(JsonNode payloadNode)
        {
            if (payloadNode is not JsonObject payload)
            {
                return;
            }

            var state = ReadString(payload, "state");
            var message = ReadString(payload, "message");
            var logKey = state is "playing" or "available" or "preparing"
                ? "speaker-client-state"
                : "speaker-state";
            Log(
                "AUDIO",
                logKey + "=" + (string.IsNullOrWhiteSpace(state) ? "unknown" : state)
                + $" muted={ReadBool(payload, "muted")}"
                + $" stopped={ReadBool(payload, "stopped")}"
                + $" packets={ReadLong(payload, "packets")}"
                + $" bytes={ReadLong(payload, "bytes")}"
                + $" port={ReadLong(payload, "port")}"
                + $" sampleRate={ReadLong(payload, "sampleRate")}"
                + $" channels={ReadLong(payload, "channels")}"
                + (string.IsNullOrWhiteSpace(message) ? string.Empty : $" message={message}"));
        }

        private void LogCameraStatus(JsonNode payloadNode)
        {
            if (payloadNode is not JsonObject payload)
            {
                return;
            }

            var state = ReadString(payload, "state");
            var message = ReadString(payload, "message");
            var logKey = "camera-client-state";
            Log(
                "CAMERA",
                logKey + "=" + (string.IsNullOrWhiteSpace(state) ? "unknown" : state)
                + $" permission={ReadBool(payload, "permissionGranted")}"
                + $" port={ReadLong(payload, "port")}"
                + $" size={ReadLong(payload, "width")}x{ReadLong(payload, "height")}"
                + $" fps={ReadLong(payload, "fps")}"
                + $" codec={ReadString(payload, "codec")}"
                + $" facing={ReadString(payload, "facing")}"
                + $" packets={ReadLong(payload, "packets")}"
                + $" bytes={ReadLong(payload, "bytes")}"
                + $" keyFrames={ReadLong(payload, "keyFrames")}"
                + $" codecConfigPackets={ReadLong(payload, "codecConfigPackets")}"
                + $" reconnects={ReadLong(payload, "reconnectCount")}"
                + $" recoveryAttempts={ReadLong(payload, "recoveryAttemptCount")}"
                + $" consecutiveFailures={ReadLong(payload, "consecutiveFailureCount")}"
                + $" lastRecoveryDurationMs={ReadLong(payload, "lastRecoveryDurationMs")}"
                + $" lastDisconnectReason={SanitizeLogValue(ReadString(payload, "lastDisconnectReason"))}"
                + $" actualFps={ReadDouble(payload, "actualFps"):F1}"
                + $" actualKbps={ReadDouble(payload, "actualKbps"):F0}"
                + $" fpsJitter={ReadDouble(payload, "fpsJitter"):F1}"
                + $" bitrateJitter={ReadDouble(payload, "bitrateJitter"):F1}"
                + (string.IsNullOrWhiteSpace(message) ? string.Empty : $" message={message}"));
        }

        private void LogCameraCapabilities(JsonNode payloadNode)
        {
            if (payloadNode is null)
            {
                return;
            }

            var payload = payloadNode.ToJsonString(JsonOptions);
            Log("CAMERA", $"camera-capabilities payload={payload}");
        }

        private ValueTask HandleVideoErrorAsync(
            ProtocolMessage message,
            ControlConnection connection,
            CancellationToken cancellationToken)
        {
            if (message.Payload is not JsonObject payload)
            {
                Log(Scope, "video_error payload missing");
                return ValueTask.CompletedTask;
            }

            Log(Scope, $"video_error payload={payload}");
            var code = ReadString(payload, "code");
            if (!string.Equals(code, "DECODER_UNSUPPORTED", StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.CompletedTask;
            }

            var currentMode = _videoModeState.Current;
            Log(Scope, $"decoder unsupported reported; keeping video mode {currentMode.Width}x{currentMode.Height}@{currentMode.Fps}");
            return ValueTask.CompletedTask;
        }

        private static int SelectDecoderUnsupportedFallbackFps(VideoMode currentMode)
        {
            var candidates = currentMode.Width >= 2560 || currentMode.Height >= 1440
                ? new[] { 60 }
                : new[] { 90, 72, 60 };

            foreach (var candidate in candidates)
            {
                if (candidate < currentMode.Fps)
                {
                    return candidate;
                }
            }

            return 60;
        }

        private static long ReadLong(JsonObject payload, string name)
        {
            if (!payload.TryGetPropertyValue(name, out var node) || node is null)
            {
                return 0;
            }

            try
            {
                return node.GetValue<long>();
            }
            catch (InvalidOperationException)
            {
                return (long)Math.Round(ReadDouble(payload, name));
            }
            catch (FormatException)
            {
                return 0;
            }
        }

        private static bool ReadBool(JsonObject payload, string name)
        {
            if (!payload.TryGetPropertyValue(name, out var node) || node is null)
            {
                return false;
            }

            try
            {
                return node.GetValue<bool>();
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static double ReadDouble(JsonObject payload, string name)
        {
            if (!payload.TryGetPropertyValue(name, out var node) || node is null)
            {
                return 0;
            }

            try
            {
                return node.GetValue<double>();
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
            catch (FormatException)
            {
                return 0;
            }
        }

        private static string ReadString(JsonObject payload, string name)
        {
            if (!payload.TryGetPropertyValue(name, out var node) || node is null)
            {
                return string.Empty;
            }

            try
            {
                return node.GetValue<string>();
            }
            catch (InvalidOperationException)
            {
                return string.Empty;
            }
            catch (FormatException)
            {
                return string.Empty;
            }
        }

        private static string SanitizeLogValue(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Replace('\r', ' ').Replace('\n', ' ').Replace(' ', '_');
        }

        private async ValueTask HandleInputMessageAsync(
            ProtocolMessage message,
            ControlConnection connection,
            CancellationToken cancellationToken)
        {
            var error = _inputController.Handle(message);
            if (error is null)
            {
                return;
            }

            await connection.SendAsync("input_error", new JsonObject
            {
                ["code"] = error.Code,
                ["message"] = error.Message,
                ["sourceType"] = message.Type,
                ["sourceSeq"] = message.Seq
            }, cancellationToken);
        }

        private async Task HeartbeatAsync(ControlConnection connection, CancellationToken cancellationToken)
        {
            while (await _heartbeatTimer.WaitForNextTickAsync(cancellationToken))
            {
                _missedPongs++;
                if (_missedPongs > 3)
                {
                    Log(Scope, $"连续 {_missedPongs - 1} 次未收到 pong，上次 pong: {_lastPong:HH:mm:ss}");
                    await _connectionCts.CancelAsync();
                    return;
                }

                await connection.SendAsync("ping", new JsonObject
                {
                    ["missedPongs"] = _missedPongs - 1
                }, cancellationToken);
            }
        }

        private async Task InputStatsAsync(CancellationToken cancellationToken)
        {
            while (await _inputStatsTimer.WaitForNextTickAsync(cancellationToken))
            {
                var snapshot = _inputController.SnapshotAndResetWindow();
                if (!snapshot.HasActivity)
                {
                    continue;
                }

                Log(
                    Scope,
                    "input stats 1s "
                    + $"keyboard={snapshot.KeyboardEvents} "
                    + $"mouseMove={snapshot.MouseMoveEvents} "
                    + $"mouseButton={snapshot.MouseButtonEvents} "
                    + $"mouseWheel={snapshot.MouseWheelEvents} "
                    + $"pointerAbs={snapshot.PointerAbsEvents} "
                    + $"pointerMapped={snapshot.PointerMappedEvents} "
                    + $"pointerOutOfBounds={snapshot.PointerOutOfBounds} "
                    + $"absoluteInjectErrors={snapshot.AbsoluteInjectErrors} "
                    + $"errors={snapshot.InputErrors} "
                    + $"target={_options.InputTarget.ToString().ToLowerInvariant()} "
                    + $"bounds={snapshot.DisplayBounds} "
                    + $"inject={(_options.EnableInputInjection ? "on" : "off")}");
            }
        }

        private async Task DisplayLayoutAsync(ControlConnection connection, CancellationToken cancellationToken)
        {
            while (await _displayLayoutTimer.WaitForNextTickAsync(cancellationToken))
            {
                await PublishDisplayMetricsIfChangedAsync(connection, force: false, cancellationToken);
            }
        }

        private async Task CursorStateAsync(ControlConnection connection, CancellationToken cancellationToken)
        {
            while (await _cursorStateTimer.WaitForNextTickAsync(cancellationToken))
            {
                await PublishCursorStateIfChangedAsync(connection, cancellationToken);
            }
        }

        private async Task HandleDisplayModeChangeAsync(
            ProtocolMessage message,
            ControlConnection connection,
            CancellationToken cancellationToken)
        {
            if (!TryGetObjectPayload(message, out var payload, out var payloadError))
            {
                await connection.SendAsync("display_mode_changed", new JsonObject
                {
                    ["success"] = false,
                    ["code"] = "DISPLAY_MODE_PAYLOAD_INVALID",
                    ["message"] = payloadError,
                    ["sourceSeq"] = message.Seq
                }, cancellationToken);
                return;
            }

            if (!TryReadInt(payload, "width", out var width)
                || !TryReadInt(payload, "height", out var height))
            {
                await connection.SendAsync("display_mode_changed", new JsonObject
                {
                    ["success"] = false,
                    ["code"] = "DISPLAY_MODE_FIELD_MISSING",
                    ["message"] = "Display mode payload requires width and height.",
                    ["sourceSeq"] = message.Seq
                }, cancellationToken);
                return;
            }

            var refreshHz = TryReadInt(payload, "refreshHz", out var requestedRefreshHz)
                ? requestedRefreshHz
                : _options.VideoFps;
            var mode = new DisplayModeRequest(width, height, refreshHz);
            var success = _displayLayoutProvider.TryChangeMode(mode, out var resultMessage);
            var layout = _displayLayoutProvider.GetLayout(force: true);
            var currentMode = _videoModeState.Current;
            var changedWidth = layout?.Width ?? width;
            var changedHeight = layout?.Height ?? height;
            var changedRefresh = layout?.RefreshRate ?? refreshHz;
            var streamWidth = layout?.Width > 0
                ? changedWidth
                : success ? width : currentMode.Width;
            var streamHeight = layout?.Height > 0
                ? changedHeight
                : success ? height : currentMode.Height;
            var streamFps = refreshHz > 0 ? refreshHz : currentMode.Fps;
            var appliedMode = _videoModeState.Set(streamWidth, streamHeight, streamFps);

            Log(
                Scope,
                "display mode change "
                + $"requested={mode} "
                + $"result={changedWidth}x{changedHeight}@{changedRefresh} "
                + $"success={success} "
                + $"message={resultMessage}");

            await connection.SendAsync("display_mode_changed", new JsonObject
            {
                ["width"] = changedWidth,
                ["height"] = changedHeight,
                ["refreshHz"] = changedRefresh,
                ["requestedRefreshHz"] = refreshHz,
                ["displayRefreshHz"] = changedRefresh,
                ["success"] = success,
                ["code"] = success ? "OK" : "DISPLAY_MODE_CHANGE_FAILED",
                ["message"] = resultMessage,
                ["videoWidth"] = appliedMode.Width,
                ["videoHeight"] = appliedMode.Height,
                ["videoFps"] = appliedMode.Fps,
                ["videoBitrate"] = appliedMode.Bitrate,
                ["sourceSeq"] = message.Seq
            }, cancellationToken);

            await connection.SendAsync("video_start", new JsonObject
            {
                ["videoPort"] = _options.VideoPort,
                ["width"] = appliedMode.Width,
                ["height"] = appliedMode.Height,
                ["fps"] = appliedMode.Fps,
                ["codec"] = "video/avc",
                ["format"] = "annexb",
                ["displayRefreshHz"] = changedRefresh,
                ["displayModeChanged"] = success
            }, cancellationToken);

            await PublishDisplayMetricsIfChangedAsync(connection, force: true, cancellationToken);
        }

        private static bool TryGetObjectPayload(ProtocolMessage message, out JsonObject payload, out string error)
        {
            if (message.Payload is JsonObject jsonObject)
            {
                payload = jsonObject;
                error = string.Empty;
                return true;
            }

            payload = new JsonObject();
            error = $"Message {message.Type} requires an object payload.";
            return false;
        }

        private static bool TryReadInt(JsonObject payload, string name, out int value)
        {
            value = 0;
            if (!payload.TryGetPropertyValue(name, out var node) || node is null)
            {
                return false;
            }

            try
            {
                value = node.GetValue<int>();
                return true;
            }
            catch (InvalidOperationException)
            {
                return TryReadDoubleAsInt(node, out value);
            }
            catch (FormatException)
            {
                return TryReadDoubleAsInt(node, out value);
            }
        }

        private static bool TryReadDoubleAsInt(JsonNode node, out int value)
        {
            value = 0;
            try
            {
                value = (int)Math.Round(node.GetValue<double>());
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private async ValueTask PublishDisplayMetricsIfChangedAsync(
            ControlConnection connection,
            bool force,
            CancellationToken cancellationToken)
        {
            if (!IsIddVideoSource(_options.VideoSource) && _options.InputTarget != InputTargetKind.Idd)
            {
                return;
            }

            var layout = _displayLayoutProvider.GetLayout(force);
            if (layout is null)
            {
                if (force)
                {
                    await connection.SendAsync("cursor_error", new JsonObject
                    {
                        ["code"] = "DISPLAY_LAYOUT_UNAVAILABLE",
                        ["message"] = "SideDock display layout is not available yet."
                    }, cancellationToken);
                }

                return;
            }

            var previousObservedMode = _lastObservedDisplayMode;
            _lastObservedDisplayMode = new DisplayModeRequest(layout.Width, layout.Height, layout.RefreshRate);
            var videoMode = await SynchronizeVideoModeWithDisplayLayoutAsync(
                connection,
                layout,
                _videoModeState.Current,
                previousObservedMode,
                cancellationToken);
            var metrics = layout.ToMetrics(videoMode.Width, videoMode.Height);
            if (!force && _lastPublishedMetrics is not null && metrics.Equals(_lastPublishedMetrics))
            {
                return;
            }

            _lastPublishedMetrics = metrics;
            await connection.SendAsync("display_layout", metrics.ToDisplayLayoutPayload(), cancellationToken);
            await connection.SendAsync("display_metrics", metrics.ToPayload(), cancellationToken);
            await connection.SendAsync("cursor_shape", new JsonObject
            {
                ["kind"] = "arrow",
                ["visible"] = false,
                ["source"] = "local-overlay"
            }, cancellationToken);
        }

        private async ValueTask<VideoMode> SynchronizeVideoModeWithDisplayLayoutAsync(
            ControlConnection connection,
            DisplayLayout layout,
            VideoMode currentMode,
            DisplayModeRequest? previousObservedMode,
            CancellationToken cancellationToken)
        {
            if (!IsIddVideoSource(_options.VideoSource))
            {
                return currentMode;
            }

            var displayModeChanged = previousObservedMode is not null
                && (previousObservedMode.Width != layout.Width
                    || previousObservedMode.Height != layout.Height
                    || Math.Abs(previousObservedMode.RefreshHz - layout.RefreshRate) > 1);
            var sizeMismatch = currentMode.Width != layout.Width || currentMode.Height != layout.Height;
            var refreshMismatchFromDisplayChange = displayModeChanged
                && Math.Abs(currentMode.Fps - layout.RefreshRate) > 1;
            if (!sizeMismatch && !refreshMismatchFromDisplayChange)
            {
                return currentMode;
            }

            var synchronizedMode = _videoModeState.Set(layout.Width, layout.Height, layout.RefreshRate);
            _lastPublishedMetrics = null;
            Log(
                Scope,
                "display layout mode sync "
                + $"previous={currentMode.Width}x{currentMode.Height}@{currentMode.Fps} "
                + $"layout={layout.Width}x{layout.Height}@{layout.RefreshRate} "
                + $"video={synchronizedMode.Width}x{synchronizedMode.Height}@{synchronizedMode.Fps}");

            await connection.SendAsync("display_mode_changed", new JsonObject
            {
                ["width"] = layout.Width,
                ["height"] = layout.Height,
                ["refreshHz"] = layout.RefreshRate,
                ["requestedRefreshHz"] = layout.RefreshRate,
                ["displayRefreshHz"] = layout.RefreshRate,
                ["success"] = true,
                ["code"] = "DISPLAY_MODE_SYNCED",
                ["message"] = "Display layout changed externally; video mode synchronized.",
                ["videoWidth"] = synchronizedMode.Width,
                ["videoHeight"] = synchronizedMode.Height,
                ["videoFps"] = synchronizedMode.Fps,
                ["videoBitrate"] = synchronizedMode.Bitrate,
                ["external"] = true,
                ["sourceSeq"] = 0
            }, cancellationToken);

            await connection.SendAsync("video_start", new JsonObject
            {
                ["videoPort"] = _options.VideoPort,
                ["width"] = synchronizedMode.Width,
                ["height"] = synchronizedMode.Height,
                ["fps"] = synchronizedMode.Fps,
                ["codec"] = "video/avc",
                ["format"] = "annexb",
                ["displayRefreshHz"] = layout.RefreshRate,
                ["displayModeChanged"] = true,
                ["source"] = "display-layout-sync"
            }, cancellationToken);

            return synchronizedMode;
        }

        private async ValueTask PublishCursorStateIfChangedAsync(
            ControlConnection connection,
            CancellationToken cancellationToken)
        {
            if (_options.InputTarget != InputTargetKind.Idd && _options.VideoSource != VideoSourceKind.Idd && _options.VideoSource != VideoSourceKind.IddGpu)
            {
                return;
            }

            var layout = _displayLayoutProvider.GetLayout(force: false);
            DpiAwareness.TryEnableCurrentThreadPerMonitorV2();
            if (layout is null || !DisplayNative.GetCursorPos(out var point))
            {
                await SendCursorStateIfChangedAsync(connection, CursorState.Hidden(), cancellationToken);
                return;
            }

            var insideDisplay = point.X >= layout.X
                && point.X < layout.X + layout.Width
                && point.Y >= layout.Y
                && point.Y < layout.Y + layout.Height;
            if (!insideDisplay)
            {
                var outsideLogCounter = Interlocked.Increment(ref _cursorStateLogCounter);
                if (outsideLogCounter <= 5 || outsideLogCounter % 120 == 0)
                {
                    Log(
                        Scope,
                        "cursor outside display "
                        + $"desktop=({point.X},{point.Y}) "
                        + $"display={layout.BoundsString} "
                        + $"virtual={layout.VirtualBoundsString} "
                        + $"displayName={layout.DisplayName} "
                        + $"device={layout.DeviceName} "
                        + $"dpiScale={layout.DpiScale:F2} "
                        + $"dpi={layout.DpiX}x{layout.DpiY} "
                        + $"awareness={layout.ProcessDpiAwareness}");
                }
                await SendCursorStateIfChangedAsync(connection, CursorState.Hidden(layout.Width, layout.Height), cancellationToken);
                return;
            }

            var displayX = Math.Clamp(point.X - layout.X, 0, Math.Max(0, layout.Width - 1));
            var displayY = Math.Clamp(point.Y - layout.Y, 0, Math.Max(0, layout.Height - 1));
            var cursorLogCounter = Interlocked.Increment(ref _cursorStateLogCounter);
            if (cursorLogCounter <= 5 || cursorLogCounter % 120 == 0)
            {
                Log(
                    Scope,
                    "cursor state "
                    + $"desktop=({point.X},{point.Y}) "
                    + $"display=({displayX},{displayY}) "
                    + $"n=({(layout.Width <= 1 ? 0.0 : displayX / (double)(layout.Width - 1)):F4},{(layout.Height <= 1 ? 0.0 : displayY / (double)(layout.Height - 1)):F4}) "
                    + $"bounds={layout.BoundsString} "
                    + $"virtual={layout.VirtualBoundsString} "
                    + $"displayName={layout.DisplayName} "
                    + $"device={layout.DeviceName} "
                    + $"dpiScale={layout.DpiScale:F2} "
                    + $"dpi={layout.DpiX}x{layout.DpiY} "
                    + $"awareness={layout.ProcessDpiAwareness}");
            }
            await SendCursorStateIfChangedAsync(
                connection,
                CursorState.Shown(displayX, displayY, layout.Width, layout.Height, point.X, point.Y),
                cancellationToken);
        }

        private async ValueTask SendCursorStateIfChangedAsync(
            ControlConnection connection,
            CursorState state,
            CancellationToken cancellationToken)
        {
            if (_lastPublishedCursorState == state)
            {
                return;
            }

            _lastPublishedCursorState = state;
            await connection.SendAsync("cursor_state", new JsonObject
            {
                ["visible"] = state.Visible,
                ["x"] = state.X,
                ["y"] = state.Y,
                ["displayWidth"] = state.DisplayWidth,
                ["displayHeight"] = state.DisplayHeight,
                ["nx"] = state.Nx,
                ["ny"] = state.Ny,
                ["desktopX"] = state.DesktopX,
                ["desktopY"] = state.DesktopY,
                ["source"] = "host-cursor"
            }, cancellationToken);
        }

        private string Scope => $"CONN {_connectionId}";
    }

    private sealed class DisplayLayoutProvider
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
        private const uint DisplayConfigPathModeIdxInvalid = 0xffffffff;
        private const uint DisplayConfigModeInfoTypeSource = 1;
        private const uint DisplayConfigModeInfoTypeTarget = 2;
        private const uint DisplayConfigPixelFormat32Bpp = 4;
        private const int DmPosition = 0x00000020;
        private const int DmDisplayOrientation = 0x00000080;
        private const int DmBitsPerPel = 0x00040000;
        private const int DmPelsWidth = 0x00080000;
        private const int DmPelsHeight = 0x00100000;
        private const int DmDisplayFrequency = 0x00400000;
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

        private readonly HostOptions _options;
        private readonly VideoModeState _videoModeState;
        private readonly Action<string> _log;
        private readonly object _lock = new();
        private DisplayLayout? _cachedLayout;
        private long _lastQueryTicks;
        private string _lastLogKey = string.Empty;

        public DisplayLayoutProvider(HostOptions options, VideoModeState videoModeState, Action<string> log)
        {
            _options = options;
            _videoModeState = videoModeState;
            _log = log;
        }

        public DisplayLayout? GetLayout()
        {
            return GetLayout(force: false);
        }

        public DisplayLayout? GetLayout(bool force)
        {
            lock (_lock)
            {
                var nowTicks = Stopwatch.GetTimestamp();
                if (!force
                    && _lastQueryTicks != 0
                    && (nowTicks - _lastQueryTicks) * 1000.0 / Stopwatch.Frequency < RefreshInterval.TotalMilliseconds)
                {
                    return _cachedLayout;
                }

                _lastQueryTicks = nowTicks;
                var nextLayout = QueryLayout();
                _cachedLayout = nextLayout;
                LogLayoutChange(nextLayout);
                return _cachedLayout;
            }
        }

        public void LogCurrentLayout()
        {
            var layout = GetLayout(force: true);
            if (layout is null)
            {
                _log("SideDock Virtual Display not found. Absolute pointer mapping will wait for display layout.");
                return;
            }

            _log(
                "display metrics "
                + $"dpiScale={layout.DpiScale:F2} "
                + $"dpi={layout.DpiX}x{layout.DpiY} "
                + $"orientation={layout.Orientation} "
                + $"bounds={layout.BoundsString} "
                + $"virtual={layout.VirtualBoundsString}");
        }

        public bool TryChangeMode(DisplayModeRequest mode, out string message)
        {
            if (!OperatingSystem.IsWindows())
            {
                message = "Display mode changes are only available on Windows.";
                return false;
            }

            if (mode.Width <= 0 || mode.Height <= 0 || mode.RefreshHz <= 0)
            {
                message = $"Invalid display mode {mode}.";
                return false;
            }

            var layout = GetLayout(force: true);
            if (layout is null || string.IsNullOrWhiteSpace(layout.DeviceName))
            {
                message = "SideDock display layout is not available.";
                return false;
            }

            if (IsLayoutMatch(layout, mode))
            {
                message = "Display mode already active.";
                return true;
            }

            var currentMode = DisplayNative.DevMode.Create();
            if (!DisplayNative.EnumDisplaySettingsW(layout.DeviceName, EnumCurrentSettings, ref currentMode))
            {
                message = $"Unable to read current mode for {layout.DeviceName}: {Marshal.GetLastWin32Error()}.";
                return false;
            }

            var advertisedModes = EnumerateDisplayModes(layout.DeviceName);
            var selectedMode = SelectDisplayMode(advertisedModes, mode);
            if (selectedMode is null)
            {
                message = $"Requested mode is not advertised by {layout.DeviceName}. {FormatAdvertisedModes(advertisedModes, mode.Width, mode.Height)}";
                return false;
            }

            var preparedMode = selectedMode.Value;
            PrepareDisplayModeForApply(ref preparedMode, currentMode, mode);
            _log(
                "display mode apply "
                + $"request={mode} "
                + $"current={FormatDevMode(currentMode)} "
                + $"selected={FormatDevMode(preparedMode)} "
                + FormatAdvertisedModes(advertisedModes, mode.Width, mode.Height));

            var attempts = new List<string>();
            if (TryApplyDisplayMode(layout.DeviceName, preparedMode, mode, flags: 0, "dynamic", attempts, out var changedLayout))
            {
                message = $"Display mode applied. Current mode is {FormatLayoutMode(changedLayout)}.";
                return true;
            }

            preparedMode = selectedMode.Value;
            PrepareDisplayModeForApply(ref preparedMode, currentMode, mode);
            if (TryApplyDisplayMode(layout.DeviceName, preparedMode, mode, CdsUpdateRegistry, "registry", attempts, out changedLayout))
            {
                message = $"Display mode applied. Current mode is {FormatLayoutMode(changedLayout)}.";
                return true;
            }

            preparedMode = selectedMode.Value;
            PrepareDisplayModeForApply(ref preparedMode, currentMode, mode);
            if (TryStageAndApplyDisplayMode(layout.DeviceName, preparedMode, mode, attempts, out changedLayout))
            {
                message = $"Display mode applied. Current mode is {FormatLayoutMode(changedLayout)}.";
                return true;
            }

            if (TryDisplayConfigChangeMode(layout, currentMode, mode, attempts, out changedLayout))
            {
                message = $"Display mode applied. Current mode is {FormatLayoutMode(changedLayout)}.";
                return true;
            }

            message = string.Join("; ", attempts) + $". Current mode is {FormatLayoutMode(changedLayout)}.";
            return false;
        }

        private bool TryApplyDisplayMode(
            string deviceName,
            DisplayNative.DevMode devMode,
            DisplayModeRequest requestedMode,
            uint flags,
            string attemptName,
            List<string> attempts,
            out DisplayLayout? changedLayout)
        {
            var result = DisplayNative.ChangeDisplaySettingsExW(deviceName, ref devMode, IntPtr.Zero, flags, IntPtr.Zero);
            var win32Error = Marshal.GetLastWin32Error();
            changedLayout = WaitForAppliedLayout(requestedMode, result == DispChangeSuccessful);
            var changed = result == DispChangeSuccessful && IsLayoutMatch(changedLayout, requestedMode);
            attempts.Add(
                $"{attemptName}={DescribeDispChangeResult(result)}({result}) "
                + $"win32={win32Error} "
                + $"mode={FormatLayoutMode(changedLayout)}");
            return changed;
        }

        private bool TryStageAndApplyDisplayMode(
            string deviceName,
            DisplayNative.DevMode devMode,
            DisplayModeRequest requestedMode,
            List<string> attempts,
            out DisplayLayout? changedLayout)
        {
            var stageResult = DisplayNative.ChangeDisplaySettingsExW(
                deviceName,
                ref devMode,
                IntPtr.Zero,
                CdsUpdateRegistry | CdsNoReset,
                IntPtr.Zero);
            var stageWin32Error = Marshal.GetLastWin32Error();
            if (stageResult != DispChangeSuccessful)
            {
                changedLayout = WaitForAppliedLayout(requestedMode, poll: false);
                attempts.Add(
                    $"staged={DescribeDispChangeResult(stageResult)}({stageResult}) "
                    + $"win32={stageWin32Error} "
                    + $"mode={FormatLayoutMode(changedLayout)}");
                return false;
            }

            var applyResult = DisplayNative.ChangeDisplaySettingsExW(
                null,
                IntPtr.Zero,
                IntPtr.Zero,
                0,
                IntPtr.Zero);
            var applyWin32Error = Marshal.GetLastWin32Error();
            changedLayout = WaitForAppliedLayout(requestedMode, applyResult == DispChangeSuccessful);
            var changed = applyResult == DispChangeSuccessful && IsLayoutMatch(changedLayout, requestedMode);
            attempts.Add(
                $"staged={DescribeDispChangeResult(stageResult)}({stageResult}) "
                + $"stageWin32={stageWin32Error} "
                + $"global={DescribeDispChangeResult(applyResult)}({applyResult}) "
                + $"globalWin32={applyWin32Error} "
                + $"mode={FormatLayoutMode(changedLayout)}");
            return changed;
        }

        private bool TryDisplayConfigChangeMode(
            DisplayLayout layout,
            DisplayNative.DevMode currentMode,
            DisplayModeRequest requestedMode,
            List<string> attempts,
            out DisplayLayout? changedLayout)
        {
            var queryResult = TryQueryActiveDisplayConfig(out var paths, out var modes, out var queryMessage);
            if (queryResult != ErrorSuccess)
            {
                changedLayout = WaitForAppliedLayout(requestedMode, poll: false);
                attempts.Add($"displayConfigQuery={queryMessage} mode={FormatLayoutMode(changedLayout)}");
                return false;
            }

            var sourceNameResult = TryFindDisplayConfigPath(layout.DeviceName, paths, out var pathIndex, out var sourceNameMessage);
            if (sourceNameResult != ErrorSuccess)
            {
                changedLayout = WaitForAppliedLayout(requestedMode, poll: false);
                attempts.Add($"displayConfigFind={sourceNameMessage} mode={FormatLayoutMode(changedLayout)}");
                return false;
            }

            var path = paths[pathIndex];
            var sourceModeIndex = EnsureDisplayConfigSourceMode(modes, path.sourceInfo.adapterId, path.sourceInfo.id);
            var targetModeIndex = EnsureDisplayConfigTargetMode(modes, path.targetInfo.adapterId, path.targetInfo.id);
            if (sourceModeIndex < 0 || targetModeIndex < 0)
            {
                changedLayout = WaitForAppliedLayout(requestedMode, poll: false);
                attempts.Add(
                    $"displayConfigModeIdx=missing sourceIdx={sourceModeIndex} targetIdx={targetModeIndex} "
                    + $"path={FormatDisplayConfigPath(path)} mode={FormatLayoutMode(changedLayout)}");
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

            targetSignal.vSyncFreq.Numerator = (uint)requestedMode.RefreshHz;
            targetSignal.vSyncFreq.Denominator = 1;
            targetSignal.hSyncFreq.Numerator = (uint)(requestedMode.RefreshHz * Math.Max(1, requestedMode.Height));
            targetSignal.hSyncFreq.Denominator = 1;
            targetSignal.pixelRate = (ulong)requestedMode.RefreshHz * (ulong)requestedMode.Width * (ulong)requestedMode.Height;
            targetSignal.scanLineOrdering = DisplayNative.DisplayConfigScanlineOrderingProgressive;
            updatedModes[targetModeIndex].targetMode.targetVideoSignalInfo = targetSignal;

            var updatedPaths = paths.ToArray();
            updatedPaths[pathIndex].sourceInfo.modeInfoIdx = (uint)sourceModeIndex;
            updatedPaths[pathIndex].targetInfo.modeInfoIdx = (uint)targetModeIndex;
            updatedPaths[pathIndex].targetInfo.refreshRate.Numerator = (uint)requestedMode.RefreshHz;
            updatedPaths[pathIndex].targetInfo.refreshRate.Denominator = 1;
            updatedPaths[pathIndex].targetInfo.scanLineOrdering = DisplayNative.DisplayConfigScanlineOrderingProgressive;
            updatedPaths[pathIndex].flags |= DisplayConfigPathActive;

            var validateFlags = SdcUseSuppliedDisplayConfig | SdcValidate | SdcAllowChanges;
            var validateResult = DisplayNative.SetDisplayConfig(
                (uint)updatedPaths.Length,
                updatedPaths,
                (uint)updatedModes.Length,
                updatedModes,
                validateFlags);
            if (validateResult != ErrorSuccess)
            {
                changedLayout = WaitForAppliedLayout(requestedMode, poll: false);
                attempts.Add(
                    $"displayConfigValidate={validateResult} "
                    + $"path={FormatDisplayConfigPath(updatedPaths[pathIndex])} "
                    + $"sourceIdx={sourceModeIndex} targetIdx={targetModeIndex} mode={FormatLayoutMode(changedLayout)}");
                return false;
            }

            var applyFlags = SdcUseSuppliedDisplayConfig | SdcApply | SdcSaveToDatabase | SdcAllowChanges;
            var applyResult = DisplayNative.SetDisplayConfig(
                (uint)updatedPaths.Length,
                updatedPaths,
                (uint)updatedModes.Length,
                updatedModes,
                applyFlags);
            changedLayout = WaitForAppliedLayout(requestedMode, applyResult == ErrorSuccess);
            var changed = applyResult == ErrorSuccess && IsLayoutMatch(changedLayout, requestedMode);
            attempts.Add(
                $"displayConfigValidate={validateResult} displayConfigApply={applyResult} "
                + $"path={FormatDisplayConfigPath(updatedPaths[pathIndex])} "
                + $"sourceIdx={sourceModeIndex} targetIdx={targetModeIndex} mode={FormatLayoutMode(changedLayout)}");
            return changed;
        }

        private static int TryQueryActiveDisplayConfig(
            out DisplayNative.DisplayConfigPathInfo[] paths,
            out DisplayNative.DisplayConfigModeInfo[] modes,
            out string message)
        {
            paths = [];
            modes = [];

            var sizeResult = DisplayNative.GetDisplayConfigBufferSizes(
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
                paths = new DisplayNative.DisplayConfigPathInfo[pathCount];
                modes = new DisplayNative.DisplayConfigModeInfo[modeCount];
                var queryPathCount = pathCount;
                var queryModeCount = modeCount;
                var queryResult = DisplayNative.QueryDisplayConfig(
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

                sizeResult = DisplayNative.GetDisplayConfigBufferSizes(
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
            IReadOnlyList<DisplayNative.DisplayConfigPathInfo> paths,
            out int pathIndex,
            out string message)
        {
            pathIndex = -1;
            var inspected = new List<string>();
            for (var index = 0; index < paths.Count; index++)
            {
                var path = paths[index];
                var sourceName = DisplayNative.DisplayConfigSourceDeviceName.Create(path.sourceInfo.adapterId, path.sourceInfo.id);
                var result = DisplayNative.DisplayConfigGetDeviceInfo(ref sourceName);
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
            IReadOnlyList<DisplayNative.DisplayConfigModeInfo> modes,
            DisplayNative.Luid adapterId,
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
            IReadOnlyList<DisplayNative.DisplayConfigModeInfo> modes,
            DisplayNative.Luid adapterId,
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

        private static string FormatDisplayConfigPath(DisplayNative.DisplayConfigPathInfo path)
        {
            return $"source={path.sourceInfo.id}/{path.sourceInfo.modeInfoIdx} target={path.targetInfo.id}/{path.targetInfo.modeInfoIdx} refresh={FormatRational(path.targetInfo.refreshRate)} flags=0x{path.flags:X}";
        }

        private static string FormatRational(DisplayNative.DisplayConfigRational rational)
        {
            return rational.Denominator == 0
                ? $"{rational.Numerator}/0"
                : $"{rational.Numerator / (double)rational.Denominator:F3}";
        }

        private DisplayLayout? WaitForAppliedLayout(DisplayModeRequest requestedMode, bool poll)
        {
            DisplayLayout? layout = null;
            var attempts = poll ? 8 : 1;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0)
                {
                    Thread.Sleep(125);
                }

                _cachedLayout = null;
                _lastQueryTicks = 0;
                layout = GetLayout(force: true);
                if (IsLayoutMatch(layout, requestedMode))
                {
                    break;
                }
            }

            return layout;
        }

        private static IReadOnlyList<DisplayNative.DevMode> EnumerateDisplayModes(string deviceName)
        {
            var modes = new List<DisplayNative.DevMode>();
            for (var index = 0; ; index++)
            {
                var mode = DisplayNative.DevMode.Create();
                if (!DisplayNative.EnumDisplaySettingsW(deviceName, index, ref mode))
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

        private static DisplayNative.DevMode? SelectDisplayMode(IReadOnlyList<DisplayNative.DevMode> modes, DisplayModeRequest requestedMode)
        {
            return modes
                .Where(mode =>
                    mode.PelsWidth == requestedMode.Width
                    && mode.PelsHeight == requestedMode.Height
                    && (mode.DisplayFrequency == 0 || Math.Abs((int)mode.DisplayFrequency - requestedMode.RefreshHz) <= 1))
                .OrderBy(mode => mode.DisplayFrequency == requestedMode.RefreshHz ? 0 : mode.DisplayFrequency == 0 ? 2 : 1)
                .ThenBy(mode => mode.DisplayFrequency == 0 ? int.MaxValue : Math.Abs((int)mode.DisplayFrequency - requestedMode.RefreshHz))
                .ThenByDescending(mode => mode.BitsPerPel)
                .Select(mode => (DisplayNative.DevMode?)mode)
                .FirstOrDefault();
        }

        private static void PrepareDisplayModeForApply(
            ref DisplayNative.DevMode devMode,
            DisplayNative.DevMode currentMode,
            DisplayModeRequest requestedMode)
        {
            devMode.Size = (ushort)Marshal.SizeOf<DisplayNative.DevMode>();
            devMode.DriverExtra = 0;
            devMode.Fields = DmPosition | DmBitsPerPel | DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
            devMode.PelsWidth = (uint)requestedMode.Width;
            devMode.PelsHeight = (uint)requestedMode.Height;
            devMode.DisplayFrequency = (uint)requestedMode.RefreshHz;
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

        private static bool IsLayoutMatch(DisplayLayout? layout, DisplayModeRequest mode)
        {
            return layout is not null
                && layout.Width == mode.Width
                && layout.Height == mode.Height
                && Math.Abs(layout.RefreshRate - mode.RefreshHz) <= 1;
        }

        private static string FormatAdvertisedModes(IReadOnlyList<DisplayNative.DevMode> modes, int width, int height)
        {
            var matchingRefreshRates = modes
                .Where(mode => mode.PelsWidth == width && mode.PelsHeight == height)
                .Select(mode => mode.DisplayFrequency == 0 ? "default" : $"{mode.DisplayFrequency}Hz")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (matchingRefreshRates.Length > 0)
            {
                return $"{width}x{height} advertised refresh=[{string.Join(", ", matchingRefreshRates)}].";
            }

            var advertisedModes = modes
                .GroupBy(mode => $"{mode.PelsWidth}x{mode.PelsHeight}@{mode.DisplayFrequency}Hz")
                .Select(group => group.Key)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Take(12)
                .ToArray();
            return advertisedModes.Length == 0
                ? "No display modes were advertised."
                : $"Advertised modes=[{string.Join(", ", advertisedModes)}].";
        }

        private static string FormatDevMode(DisplayNative.DevMode mode)
        {
            return $"{mode.PelsWidth}x{mode.PelsHeight}@{mode.DisplayFrequency}Hz bpp={mode.BitsPerPel} pos=({mode.PositionX},{mode.PositionY}) fields=0x{mode.Fields:X}";
        }

        private static string FormatLayoutMode(DisplayLayout? layout)
        {
            return layout is null ? "missing" : $"{layout.Width}x{layout.Height}@{layout.RefreshRate}Hz";
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

        private void LogLayoutChange(DisplayLayout? layout)
        {
            var key = layout?.LogKey ?? "missing";
            if (key.Equals(_lastLogKey, StringComparison.Ordinal))
            {
                return;
            }

            _lastLogKey = key;
            if (layout is null)
            {
                _log("SideDock Virtual Display layout unavailable.");
                return;
            }

            _log(
                $"{layout.DisplayName} bounds=({layout.X},{layout.Y},{layout.Width},{layout.Height}) "
                + $"refresh={layout.RefreshRate}Hz "
                + $"primary={layout.IsPrimary} "
                + $"device={layout.DeviceName} "
                + $"path={layout.DevicePath}");
        }

        private DisplayLayout? QueryLayout()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            DpiAwareness.TryEnableCurrentThreadPerMonitorV2();
            var virtualLeft = DisplayNative.GetSystemMetrics(DisplayNative.SM_XVIRTUALSCREEN);
            var virtualTop = DisplayNative.GetSystemMetrics(DisplayNative.SM_YVIRTUALSCREEN);
            var virtualWidth = DisplayNative.GetSystemMetrics(DisplayNative.SM_CXVIRTUALSCREEN);
            var virtualHeight = DisplayNative.GetSystemMetrics(DisplayNative.SM_CYVIRTUALSCREEN);
            if (virtualWidth <= 0 || virtualHeight <= 0)
            {
                return null;
            }

            var candidates = EnumerateCandidates(virtualLeft, virtualTop, virtualWidth, virtualHeight);
            var strongCandidate = candidates
                .Where(candidate => candidate.Score >= 20)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Layout.IsPrimary)
                .FirstOrDefault();

            if (strongCandidate is not null)
            {
                return strongCandidate.Layout;
            }

            var videoMode = _videoModeState.Current;
            var matchingMode = candidates
                .Where(candidate =>
                    candidate.Layout.Width == videoMode.Width
                    && candidate.Layout.Height == videoMode.Height
                    && Math.Abs(candidate.Layout.RefreshRate - videoMode.Fps) <= 1
                    && !candidate.Layout.IsPrimary)
                .OrderByDescending(candidate => candidate.Score)
                .Select(candidate => candidate.Layout)
                .FirstOrDefault();

            if (matchingMode is not null)
            {
                return matchingMode;
            }

            return candidates
                .Where(candidate =>
                    candidate.Layout.Width == videoMode.Width
                    && candidate.Layout.Height == videoMode.Height
                    && !candidate.Layout.IsPrimary)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => Math.Abs(candidate.Layout.RefreshRate - videoMode.Fps))
                .Select(candidate => candidate.Layout)
                .FirstOrDefault();
        }

        private IReadOnlyList<DisplayCandidate> EnumerateCandidates(
            int virtualLeft,
            int virtualTop,
            int virtualWidth,
            int virtualHeight)
        {
            var candidates = new List<DisplayCandidate>();
            for (uint index = 0; ; index++)
            {
                var adapter = DisplayNative.DisplayDevice.Create();
                if (!DisplayNative.EnumDisplayDevicesW(null, index, ref adapter, 0))
                {
                    break;
                }

                if ((adapter.StateFlags & DisplayDeviceActive) == 0
                    || (adapter.StateFlags & DisplayDeviceMirroringDriver) != 0
                    || string.IsNullOrWhiteSpace(adapter.DeviceName))
                {
                    continue;
                }

                var mode = DisplayNative.DevMode.Create();
                if (!DisplayNative.EnumDisplaySettingsW(adapter.DeviceName, EnumCurrentSettings, ref mode))
                {
                    continue;
                }

                if (mode.PelsWidth == 0 || mode.PelsHeight == 0)
                {
                    continue;
                }

                var monitor = DisplayNative.DisplayDevice.Create();
                var hasMonitor = DisplayNative.EnumDisplayDevicesW(adapter.DeviceName, 0, ref monitor, 0);
                var displayName = FirstNonEmpty(monitor.DeviceString, adapter.DeviceString, "Unknown Display");
                var devicePath = FirstNonEmpty(monitor.DeviceID, adapter.DeviceID, string.Empty);
                var deviceName = CleanString(adapter.DeviceName);
                var videoMode = _videoModeState.Current;
                var refreshRate = mode.DisplayFrequency == 0 ? videoMode.Fps : (int)mode.DisplayFrequency;
                var isPrimary = (adapter.StateFlags & DisplayDevicePrimaryDevice) != 0;
                var dpi = QueryDpi(mode.PositionX, mode.PositionY, (int)mode.PelsWidth, (int)mode.PelsHeight);
                var processAwareness = QueryProcessDpiAwareness();
                var layout = new DisplayLayout(
                    "idd",
                    displayName,
                    deviceName,
                    devicePath,
                    mode.PositionX,
                    mode.PositionY,
                    (int)mode.PelsWidth,
                    (int)mode.PelsHeight,
                    refreshRate,
                    isPrimary,
                    virtualLeft,
                    virtualTop,
                    virtualWidth,
                    virtualHeight,
                    dpi.DpiX,
                    dpi.DpiY,
                    dpi.Scale,
                    (int)mode.DisplayOrientation,
                    processAwareness);
                var score = ScoreCandidate(adapter, hasMonitor ? monitor : null, layout);
                candidates.Add(new DisplayCandidate(layout, score));
            }

            return candidates;
        }

        private int ScoreCandidate(DisplayNative.DisplayDevice adapter, DisplayNative.DisplayDevice? monitor, DisplayLayout layout)
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

            var videoMode = _videoModeState.Current;
            if (layout.Width == videoMode.Width && layout.Height == videoMode.Height)
            {
                score += 5;
            }

            if (Math.Abs(layout.RefreshRate - videoMode.Fps) <= 1)
            {
                score += 2;
            }

            if (!layout.IsPrimary)
            {
                score += 1;
            }

            return score;
        }

        private static string FirstNonEmpty(params string[] values)
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

        private static DisplayDpi QueryDpi(int x, int y, int width, int height)
        {
            var dpiX = 96u;
            var dpiY = 96u;
            if (OperatingSystem.IsWindows())
            {
                var rect = new DisplayNative.Rect
                {
                    Left = x,
                    Top = y,
                    Right = x + Math.Max(1, width),
                    Bottom = y + Math.Max(1, height)
                };
                var monitor = DisplayNative.MonitorFromRect(ref rect, DisplayNative.MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero
                    && DisplayNative.GetDpiForMonitor(monitor, DisplayNative.MDT_EFFECTIVE_DPI, out var queriedDpiX, out var queriedDpiY) == 0)
                {
                    dpiX = queriedDpiX;
                    dpiY = queriedDpiY;
                }
                else
                {
                    dpiX = DisplayNative.GetDpiForSystem();
                    dpiY = dpiX;
                }
            }

            return new DisplayDpi((int)dpiX, (int)dpiY, dpiX / 96.0);
        }

        private static string QueryProcessDpiAwareness()
        {
            if (!OperatingSystem.IsWindows())
            {
                return "unknown";
            }

            try
            {
                var context = DisplayNative.GetThreadDpiAwarenessContext();
                return DisplayNative.GetAwarenessFromDpiAwarenessContext(context) switch
                {
                    0 => "unaware",
                    1 => "system",
                    2 => "per-monitor",
                    _ => "unknown"
                };
            }
            catch (EntryPointNotFoundException)
            {
                return "unknown";
            }
        }

        private sealed record DisplayCandidate(DisplayLayout Layout, int Score);
    }

    private static class DpiAwareness
    {
        private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);
        private const int ProcessPerMonitorDpiAware = 2;

        public static void TryEnablePerMonitorV2(Action<string> log)
        {
            try
            {
                if (!SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 0 && error != 5)
                    {
                        log($"SetProcessDpiAwarenessContext failed: {error}.");
                    }
                }

                if (!TryEnableCurrentThreadPerMonitorV2())
                {
                    log($"SetThreadDpiAwarenessContext failed: {Marshal.GetLastWin32Error()}.");
                }
            }
            catch (EntryPointNotFoundException)
            {
                try
                {
                    var hr = SetProcessDpiAwareness(ProcessPerMonitorDpiAware);
                    if (hr != 0 && hr != unchecked((int)0x80070005))
                    {
                        log($"SetProcessDpiAwareness failed: 0x{hr:X8}.");
                    }
                }
                catch (EntryPointNotFoundException)
                {
                    log("per-monitor DPI awareness APIs are unavailable.");
                }
            }
        }

        public static bool TryEnableCurrentThreadPerMonitorV2()
        {
            try
            {
                return SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2) != IntPtr.Zero;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("shcore.dll", ExactSpelling = true)]
        private static extern int SetProcessDpiAwareness(int processDpiAwareness);
    }

    private sealed record DisplayDpi(int DpiX, int DpiY, double Scale);

    private sealed record DisplayLayout(
        string Source,
        string DisplayName,
        string DeviceName,
        string DevicePath,
        int X,
        int Y,
        int Width,
        int Height,
        int RefreshRate,
        bool IsPrimary,
        int VirtualLeft,
        int VirtualTop,
        int VirtualWidth,
        int VirtualHeight,
        int DpiX,
        int DpiY,
        double DpiScale,
        int Orientation,
        string ProcessDpiAwareness)
    {
        public string BoundsString => $"({X},{Y},{Width},{Height})";

        public string VirtualBoundsString => $"({VirtualLeft},{VirtualTop},{VirtualWidth},{VirtualHeight})";

        public string LogKey => $"{DeviceName}|{DevicePath}|{BoundsString}|{RefreshRate}|{IsPrimary}|{VirtualBoundsString}|{DpiScale:F3}|{Orientation}|{ProcessDpiAwareness}";

        public DisplayMetrics ToMetrics(int videoWidth, int videoHeight)
        {
            var videoRect = ComputeVideoRect(videoWidth, videoHeight, Width, Height);
            return new DisplayMetrics(
                Source,
                DisplayName,
                DeviceName,
                DevicePath,
                X,
                Y,
                Width,
                Height,
                RefreshRate,
                IsPrimary,
                VirtualLeft,
                VirtualTop,
                VirtualWidth,
                VirtualHeight,
                DpiX,
                DpiY,
                DpiScale,
                Orientation,
                ProcessDpiAwareness,
                videoWidth,
                videoHeight,
                videoRect.X,
                videoRect.Y,
                videoRect.Width,
                videoRect.Height,
                "letterbox");
        }

        private static VideoRect ComputeVideoRect(int videoWidth, int videoHeight, int displayWidth, int displayHeight)
        {
            if (videoWidth <= 0 || videoHeight <= 0 || displayWidth <= 0 || displayHeight <= 0)
            {
                return new VideoRect(0, 0, Math.Max(1, videoWidth), Math.Max(1, videoHeight));
            }

            var scale = Math.Min(videoWidth / (double)displayWidth, videoHeight / (double)displayHeight);
            var rectWidth = Math.Max(1, Math.Min(videoWidth, (int)Math.Round(displayWidth * scale)));
            var rectHeight = Math.Max(1, Math.Min(videoHeight, (int)Math.Round(displayHeight * scale)));
            return new VideoRect(
                (videoWidth - rectWidth) / 2,
                (videoHeight - rectHeight) / 2,
                rectWidth,
                rectHeight);
        }
    }

    private sealed record VideoRect(int X, int Y, int Width, int Height);

    private sealed record CursorState(
        bool Visible,
        int X,
        int Y,
        int DisplayWidth,
        int DisplayHeight,
        double Nx,
        double Ny,
        int DesktopX,
        int DesktopY)
    {
        public static CursorState Hidden(int displayWidth = 0, int displayHeight = 0)
        {
            return new CursorState(false, 0, 0, displayWidth, displayHeight, 0, 0, 0, 0);
        }

        public static CursorState Shown(int x, int y, int displayWidth, int displayHeight, int desktopX, int desktopY)
        {
            var nx = displayWidth <= 1 ? 0.0 : x / (double)(displayWidth - 1);
            var ny = displayHeight <= 1 ? 0.0 : y / (double)(displayHeight - 1);
            return new CursorState(
                true,
                x,
                y,
                displayWidth,
                displayHeight,
                Math.Clamp(nx, 0.0, 1.0),
                Math.Clamp(ny, 0.0, 1.0),
                desktopX,
                desktopY);
        }
    }

    private sealed record DisplayMetrics(
        string Source,
        string DisplayName,
        string DeviceName,
        string DevicePath,
        int X,
        int Y,
        int Width,
        int Height,
        int RefreshRate,
        bool IsPrimary,
        int VirtualLeft,
        int VirtualTop,
        int VirtualWidth,
        int VirtualHeight,
        int DpiX,
        int DpiY,
        double DpiScale,
        int Orientation,
        string ProcessDpiAwareness,
        int VideoWidth,
        int VideoHeight,
        int VideoRectX,
        int VideoRectY,
        int VideoRectWidth,
        int VideoRectHeight,
        string FitMode)
    {
        public JsonObject ToDisplayLayoutPayload()
        {
            return new JsonObject
            {
                ["source"] = Source,
                ["displayName"] = DisplayName,
                ["deviceName"] = DeviceName,
                ["devicePath"] = DevicePath,
                ["x"] = X,
                ["y"] = Y,
                ["width"] = Width,
                ["height"] = Height,
                ["videoWidth"] = VideoWidth,
                ["videoHeight"] = VideoHeight,
                ["scale"] = DpiScale,
                ["refreshRate"] = RefreshRate,
                ["primary"] = IsPrimary,
                ["virtualLeft"] = VirtualLeft,
                ["virtualTop"] = VirtualTop,
                ["virtualWidth"] = VirtualWidth,
                ["virtualHeight"] = VirtualHeight,
                ["dpiScale"] = DpiScale,
                ["dpiX"] = DpiX,
                ["dpiY"] = DpiY,
                ["orientation"] = Orientation,
                ["processDpiAwareness"] = ProcessDpiAwareness
            };
        }

        public JsonObject ToPayload()
        {
            return new JsonObject
            {
                ["source"] = Source,
                ["displayName"] = DisplayName,
                ["deviceName"] = DeviceName,
                ["devicePath"] = DevicePath,
                ["dpiScale"] = DpiScale,
                ["dpiX"] = DpiX,
                ["dpiY"] = DpiY,
                ["processDpiAwareness"] = ProcessDpiAwareness,
                ["desktopX"] = VirtualLeft,
                ["desktopY"] = VirtualTop,
                ["desktopWidth"] = VirtualWidth,
                ["desktopHeight"] = VirtualHeight,
                ["displayX"] = X,
                ["displayY"] = Y,
                ["displayWidth"] = Width,
                ["displayHeight"] = Height,
                ["refreshHz"] = RefreshRate,
                ["orientation"] = Orientation,
                ["fitMode"] = FitMode,
                ["videoWidth"] = VideoWidth,
                ["videoHeight"] = VideoHeight,
                ["videoRect"] = new JsonObject
                {
                    ["x"] = VideoRectX,
                    ["y"] = VideoRectY,
                    ["w"] = VideoRectWidth,
                    ["h"] = VideoRectHeight
                }
            };
        }
    }

    private sealed class HostInputController
    {
        private const uint InputMouse = 0;
        private const uint InputKeyboard = 1;
        private const uint KeyeventfExtendedKey = 0x0001;
        private const uint KeyeventfKeyUp = 0x0002;
        private const uint KeyeventfScancode = 0x0008;
        private const uint MouseeventfMove = 0x0001;
        private const uint MouseeventfLeftDown = 0x0002;
        private const uint MouseeventfLeftUp = 0x0004;
        private const uint MouseeventfRightDown = 0x0008;
        private const uint MouseeventfRightUp = 0x0010;
        private const uint MouseeventfMiddleDown = 0x0020;
        private const uint MouseeventfMiddleUp = 0x0040;
        private const uint MouseeventfWheel = 0x0800;
        private const uint MouseeventfHWheel = 0x1000;
        private const uint MouseeventfVirtualDesk = 0x4000;
        private const uint MouseeventfAbsolute = 0x8000;
        private static readonly IReadOnlyDictionary<int, WindowsScan> AndroidKeyMap = CreateAndroidKeyMap();

        private readonly bool _enableInjection;
        private readonly InputTargetKind _inputTarget;
        private readonly DisplayLayoutProvider _displayLayoutProvider;
        private readonly Action<string> _log;
        private readonly InputStats _stats = new();
        private long _mouseMoveLogCounter;
        private long _pointerAbsLogCounter;

        public HostInputController(
            bool enableInjection,
            InputTargetKind inputTarget,
            DisplayLayoutProvider displayLayoutProvider,
            Action<string> log)
        {
            _enableInjection = enableInjection;
            _inputTarget = inputTarget;
            _displayLayoutProvider = displayLayoutProvider;
            _log = log;
        }

        public InputError? Handle(ProtocolMessage message)
        {
            return message.Type switch
            {
                "input_keyboard" => HandleKeyboard(message),
                "input_pointer_abs" => HandlePointerAbs(message),
                "input_mouse_move" => HandleMouseMove(message),
                "input_mouse_button" => HandleMouseButton(message),
                "input_mouse_wheel" => HandleMouseWheel(message),
                _ => Fail("INPUT_TYPE_UNSUPPORTED", $"Unsupported input message type: {message.Type}")
            };
        }

        public InputStatsSnapshot SnapshotAndResetWindow()
        {
            var displayBounds = _inputTarget == InputTargetKind.Idd
                ? _displayLayoutProvider.GetLayout()?.BoundsString ?? "unavailable"
                : "system";
            return _stats.SnapshotAndResetWindow(displayBounds);
        }

        private InputError? HandleKeyboard(ProtocolMessage message)
        {
            _stats.RecordKeyboard();
            if (!TryGetPayload(message, out var payload, out var payloadError))
            {
                return Fail("INPUT_PAYLOAD_INVALID", payloadError);
            }

            if (!TryGetString(payload, "action", out var action)
                || !TryGetInt(payload, "androidKeyCode", out var androidKeyCode)
                || !TryGetInt(payload, "scanCode", out var scanCode)
                || !TryGetInt(payload, "metaState", out var metaState)
                || !TryGetInt(payload, "repeatCount", out var repeatCount))
            {
                return Fail("INPUT_FIELD_MISSING", "Keyboard payload requires action, androidKeyCode, scanCode, metaState, and repeatCount.");
            }

            action = action.ToLowerInvariant();
            if (action is not ("down" or "up"))
            {
                return Fail("INPUT_ACTION_UNSUPPORTED", $"Unsupported keyboard action: {action}");
            }

            if (!TryMapAndroidKey(androidKeyCode, scanCode, out var windowsScan))
            {
                return Fail("KEY_MAP_FAILED", $"Unsupported Android keyCode {androidKeyCode}, scanCode {scanCode}.");
            }

            if (action == "down" && repeatCount > 0)
            {
                _log($"input keyboard repeat ignored keyCode={androidKeyCode} scanCode={scanCode} repeat={repeatCount}");
                return null;
            }

            _log(
                "input keyboard "
                + $"action={action} "
                + $"androidKeyCode={androidKeyCode} "
                + $"scanCode={scanCode} "
                + $"winScan=0x{windowsScan.ScanCode:X2} "
                + $"extended={windowsScan.Extended} "
                + $"meta={metaState} "
                + $"inject={(_enableInjection ? "on" : "off")}");

            if (!_enableInjection)
            {
                return null;
            }

            return SendKeyboard(windowsScan, isKeyUp: action == "up");
        }

        private InputError? HandlePointerAbs(ProtocolMessage message)
        {
            _stats.RecordPointerAbs();
            if (!TryGetPayload(message, out var payload, out var payloadError))
            {
                return Fail("INPUT_PAYLOAD_INVALID", payloadError);
            }

            if (!TryGetDouble(payload, "nx", out var nx)
                || !TryGetDouble(payload, "ny", out var ny))
            {
                return Fail("INPUT_FIELD_MISSING", "Pointer absolute payload requires nx and ny.");
            }

            if (double.IsNaN(nx) || double.IsNaN(ny) || double.IsInfinity(nx) || double.IsInfinity(ny))
            {
                return Fail("POINTER_ABS_INVALID", $"Pointer absolute coordinates must be finite, got nx={nx}, ny={ny}.");
            }

            var clampedNx = Math.Clamp(nx, 0.0, 1.0);
            var clampedNy = Math.Clamp(ny, 0.0, 1.0);
            if (clampedNx != nx || clampedNy != ny)
            {
                _stats.RecordPointerOutOfBounds();
            }

            if (_inputTarget != InputTargetKind.Idd)
            {
                var logCounter = Interlocked.Increment(ref _pointerAbsLogCounter);
                if (logCounter <= 5 || logCounter % 60 == 0)
                {
                    _log($"input pointer abs ignored target=system nx={clampedNx:F4} ny={clampedNy:F4}");
                }

                return null;
            }

            var layout = _displayLayoutProvider.GetLayout();
            if (layout is null)
            {
                return Fail("DISPLAY_LAYOUT_UNAVAILABLE", "SideDock display layout is not available.");
            }

            var desktopX = layout.X + (int)Math.Round(clampedNx * Math.Max(0, layout.Width - 1));
            var desktopY = layout.Y + (int)Math.Round(clampedNy * Math.Max(0, layout.Height - 1));
            _stats.RecordPointerMapped();

            var pointerLogCounter = Interlocked.Increment(ref _pointerAbsLogCounter);
            if (pointerLogCounter <= 5 || pointerLogCounter % 60 == 0)
            {
                _log(
                    "input pointer abs "
                    + $"nx={clampedNx:F4} ny={clampedNy:F4} "
                    + $"desktop=({desktopX},{desktopY}) "
                    + $"display={layout.BoundsString} "
                    + $"virtual={layout.VirtualBoundsString} "
                    + $"inject={(_enableInjection ? "on" : "off")}");
            }

            if (!_enableInjection)
            {
                return null;
            }

            var error = SendAbsoluteMouseMove(desktopX, desktopY, layout);
            if (error is not null)
            {
                _stats.RecordAbsoluteInjectError();
            }

            return error;
        }

        private InputError? HandleMouseMove(ProtocolMessage message)
        {
            _stats.RecordMouseMove();
            if (!TryGetPayload(message, out var payload, out var payloadError))
            {
                return Fail("INPUT_PAYLOAD_INVALID", payloadError);
            }

            if (!TryGetString(payload, "mode", out var mode)
                || !TryGetInt(payload, "dx", out var dx)
                || !TryGetInt(payload, "dy", out var dy))
            {
                return Fail("INPUT_FIELD_MISSING", "Mouse move payload requires mode, dx, and dy.");
            }

            if (!mode.Equals("relative", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("MOUSE_MODE_UNSUPPORTED", $"Unsupported mouse move mode: {mode}");
            }

            if (_inputTarget == InputTargetKind.Idd)
            {
                var ignoredLogCounter = Interlocked.Increment(ref _mouseMoveLogCounter);
                if (ignoredLogCounter <= 5 || ignoredLogCounter % 60 == 0)
                {
                    _log($"input mouse move ignored target=idd dx={dx} dy={dy}");
                }

                return null;
            }

            var moveLogCounter = Interlocked.Increment(ref _mouseMoveLogCounter);
            if (moveLogCounter <= 5 || moveLogCounter % 30 == 0)
            {
                _log($"input mouse move dx={dx} dy={dy} inject={(_enableInjection ? "on" : "off")}");
            }

            if (!_enableInjection || (dx == 0 && dy == 0))
            {
                return null;
            }

            return SendMouse(dx, dy, 0, MouseeventfMove);
        }

        private InputError? HandleMouseButton(ProtocolMessage message)
        {
            _stats.RecordMouseButton();
            if (!TryGetPayload(message, out var payload, out var payloadError))
            {
                return Fail("INPUT_PAYLOAD_INVALID", payloadError);
            }

            if (!TryGetString(payload, "button", out var button)
                || !TryGetString(payload, "action", out var action))
            {
                return Fail("INPUT_FIELD_MISSING", "Mouse button payload requires button and action.");
            }

            button = button.ToLowerInvariant();
            action = action.ToLowerInvariant();
            var flags = (button, action) switch
            {
                ("left", "down") => MouseeventfLeftDown,
                ("left", "up") => MouseeventfLeftUp,
                ("right", "down") => MouseeventfRightDown,
                ("right", "up") => MouseeventfRightUp,
                ("middle", "down") => MouseeventfMiddleDown,
                ("middle", "up") => MouseeventfMiddleUp,
                _ => 0u
            };

            if (flags == 0)
            {
                return Fail("MOUSE_BUTTON_UNSUPPORTED", $"Unsupported mouse button/action: {button}/{action}");
            }

            _log($"input mouse button button={button} action={action} inject={(_enableInjection ? "on" : "off")}");
            return _enableInjection ? SendMouse(0, 0, 0, flags) : null;
        }

        private InputError? HandleMouseWheel(ProtocolMessage message)
        {
            _stats.RecordMouseWheel();
            if (!TryGetPayload(message, out var payload, out var payloadError))
            {
                return Fail("INPUT_PAYLOAD_INVALID", payloadError);
            }

            if (!TryGetInt(payload, "dx", out var dx)
                || !TryGetInt(payload, "dy", out var dy))
            {
                return Fail("INPUT_FIELD_MISSING", "Mouse wheel payload requires dx and dy.");
            }

            _log($"input mouse wheel dx={dx} dy={dy} inject={(_enableInjection ? "on" : "off")}");
            if (!_enableInjection || (dx == 0 && dy == 0))
            {
                return null;
            }

            InputError? verticalError = null;
            if (dy != 0)
            {
                verticalError = SendMouse(0, 0, unchecked((uint)dy), MouseeventfWheel);
            }

            if (verticalError is not null)
            {
                return verticalError;
            }

            return dx != 0
                ? SendMouse(0, 0, unchecked((uint)dx), MouseeventfHWheel)
                : null;
        }

        private InputError? SendKeyboard(WindowsScan scan, bool isKeyUp)
        {
            if (!OperatingSystem.IsWindows())
            {
                return Fail("INJECT_UNSUPPORTED_OS", "SendInput is only available on Windows.");
            }

            var flags = KeyeventfScancode;
            if (scan.Extended)
            {
                flags |= KeyeventfExtendedKey;
            }

            if (isKeyUp)
            {
                flags |= KeyeventfKeyUp;
            }

            var input = new Input
            {
                Type = InputKeyboard,
                U = new InputUnion
                {
                    Ki = new KeyboardInput
                    {
                        WScan = scan.ScanCode,
                        DwFlags = flags
                    }
                }
            };

            return SendInputChecked(input);
        }

        private InputError? SendMouse(int dx, int dy, uint mouseData, uint flags)
        {
            if (!OperatingSystem.IsWindows())
            {
                return Fail("INJECT_UNSUPPORTED_OS", "SendInput is only available on Windows.");
            }

            var input = new Input
            {
                Type = InputMouse,
                U = new InputUnion
                {
                    Mi = new MouseInput
                    {
                        Dx = dx,
                        Dy = dy,
                        MouseData = mouseData,
                        DwFlags = flags
                    }
                }
            };

            return SendInputChecked(input);
        }

        private InputError? SendAbsoluteMouseMove(int desktopX, int desktopY, DisplayLayout layout)
        {
            if (!OperatingSystem.IsWindows())
            {
                return Fail("INJECT_UNSUPPORTED_OS", "SendInput is only available on Windows.");
            }

            if (layout.VirtualWidth <= 1 || layout.VirtualHeight <= 1)
            {
                return Fail("VIRTUAL_DESKTOP_INVALID", $"Invalid virtual desktop bounds: {layout.VirtualBoundsString}.");
            }

            var absX = (int)Math.Round((desktopX - layout.VirtualLeft) * 65535.0 / (layout.VirtualWidth - 1));
            var absY = (int)Math.Round((desktopY - layout.VirtualTop) * 65535.0 / (layout.VirtualHeight - 1));
            absX = Math.Clamp(absX, 0, 65535);
            absY = Math.Clamp(absY, 0, 65535);
            return SendMouse(absX, absY, 0, MouseeventfMove | MouseeventfAbsolute | MouseeventfVirtualDesk);
        }

        private InputError? SendInputChecked(Input input)
        {
            var sent = SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
            if (sent == 1)
            {
                return null;
            }

            return Fail("INJECT_FAILED", $"SendInput returned {sent}, lastError={Marshal.GetLastWin32Error()}.");
        }

        private InputError Fail(string code, string message)
        {
            _stats.RecordError();
            _log($"input error {code}: {message}");
            return new InputError(code, message);
        }

        private static bool TryGetPayload(ProtocolMessage message, out JsonObject payload, out string error)
        {
            if (message.Payload is JsonObject jsonObject)
            {
                payload = jsonObject;
                error = string.Empty;
                return true;
            }

            payload = new JsonObject();
            error = $"Message {message.Type} requires an object payload.";
            return false;
        }

        private static bool TryGetString(JsonObject payload, string name, out string value)
        {
            value = string.Empty;
            if (!payload.TryGetPropertyValue(name, out var node) || node is null)
            {
                return false;
            }

            try
            {
                value = node.GetValue<string>();
                return !string.IsNullOrWhiteSpace(value);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool TryGetInt(JsonObject payload, string name, out int value)
        {
            value = 0;
            if (!payload.TryGetPropertyValue(name, out var node) || node is null)
            {
                return false;
            }

            try
            {
                value = node.GetValue<int>();
                return true;
            }
            catch (InvalidOperationException)
            {
                return TryGetDoubleAsInt(node, out value);
            }
            catch (FormatException)
            {
                return TryGetDoubleAsInt(node, out value);
            }
        }

        private static bool TryGetDouble(JsonObject payload, string name, out double value)
        {
            value = 0;
            if (!payload.TryGetPropertyValue(name, out var node) || node is null)
            {
                return false;
            }

            try
            {
                value = node.GetValue<double>();
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool TryGetDoubleAsInt(JsonNode node, out int value)
        {
            value = 0;
            try
            {
                value = (int)Math.Round(node.GetValue<double>());
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool TryMapAndroidKey(int androidKeyCode, int scanCode, out WindowsScan windowsScan)
        {
            if (AndroidKeyMap.TryGetValue(androidKeyCode, out windowsScan))
            {
                return true;
            }

            if (scanCode is > 0 and <= byte.MaxValue)
            {
                windowsScan = new WindowsScan((ushort)scanCode, Extended: false);
                return true;
            }

            windowsScan = default;
            return false;
        }

        private static IReadOnlyDictionary<int, WindowsScan> CreateAndroidKeyMap()
        {
            var map = new Dictionary<int, WindowsScan>
            {
                [7] = Scan(0x0B), // 0
                [8] = Scan(0x02),
                [9] = Scan(0x03),
                [10] = Scan(0x04),
                [11] = Scan(0x05),
                [12] = Scan(0x06),
                [13] = Scan(0x07),
                [14] = Scan(0x08),
                [15] = Scan(0x09),
                [16] = Scan(0x0A),
                [19] = Scan(0x48, extended: true),
                [20] = Scan(0x50, extended: true),
                [21] = Scan(0x4B, extended: true),
                [22] = Scan(0x4D, extended: true),
                [55] = Scan(0x33), // ,
                [56] = Scan(0x34), // .
                [57] = Scan(0x38),
                [58] = Scan(0x38, extended: true),
                [59] = Scan(0x2A),
                [60] = Scan(0x36),
                [61] = Scan(0x0F),
                [62] = Scan(0x39),
                [66] = Scan(0x1C),
                [67] = Scan(0x0E),
                [68] = Scan(0x29), // `
                [69] = Scan(0x0C), // -
                [70] = Scan(0x0D), // =
                [71] = Scan(0x1A), // [
                [72] = Scan(0x1B), // ]
                [73] = Scan(0x2B), // \
                [74] = Scan(0x27), // ;
                [75] = Scan(0x28), // '
                [76] = Scan(0x35), // /
                [92] = Scan(0x49, extended: true),
                [93] = Scan(0x51, extended: true),
                [111] = Scan(0x01),
                [112] = Scan(0x53, extended: true),
                [113] = Scan(0x1D),
                [114] = Scan(0x1D, extended: true),
                [117] = Scan(0x5B, extended: true),
                [118] = Scan(0x5C, extended: true),
                [122] = Scan(0x47, extended: true),
                [123] = Scan(0x4F, extended: true),
                [124] = Scan(0x52, extended: true)
            };

            for (var keyCode = 29; keyCode <= 54; keyCode++)
            {
                map[keyCode] = LetterScan(keyCode);
            }

            for (var keyCode = 131; keyCode <= 142; keyCode++)
            {
                map[keyCode] = Scan(FKeyScanCode(keyCode));
            }

            return map;
        }

        private static WindowsScan LetterScan(int androidKeyCode)
        {
            return androidKeyCode switch
            {
                29 => Scan(0x1E), // A
                30 => Scan(0x30),
                31 => Scan(0x2E),
                32 => Scan(0x20),
                33 => Scan(0x12),
                34 => Scan(0x21),
                35 => Scan(0x22),
                36 => Scan(0x23),
                37 => Scan(0x17),
                38 => Scan(0x24),
                39 => Scan(0x25),
                40 => Scan(0x26),
                41 => Scan(0x32),
                42 => Scan(0x31),
                43 => Scan(0x18),
                44 => Scan(0x19),
                45 => Scan(0x10),
                46 => Scan(0x13),
                47 => Scan(0x1F),
                48 => Scan(0x14),
                49 => Scan(0x16),
                50 => Scan(0x2F),
                51 => Scan(0x11),
                52 => Scan(0x2D),
                53 => Scan(0x15),
                54 => Scan(0x2C),
                _ => Scan(0)
            };
        }

        private static ushort FKeyScanCode(int androidKeyCode)
        {
            return androidKeyCode switch
            {
                131 => 0x3B,
                132 => 0x3C,
                133 => 0x3D,
                134 => 0x3E,
                135 => 0x3F,
                136 => 0x40,
                137 => 0x41,
                138 => 0x42,
                139 => 0x43,
                140 => 0x44,
                141 => 0x57,
                142 => 0x58,
                _ => 0
            };
        }

        private static WindowsScan Scan(ushort scanCode, bool extended = false)
        {
            return new WindowsScan(scanCode, extended);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MouseInput Mi;

            [FieldOffset(0)]
            public KeyboardInput Ki;

            [FieldOffset(0)]
            public HardwareInput Hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int Dx;
            public int Dy;
            public uint MouseData;
            public uint DwFlags;
            public uint Time;
            public UIntPtr DwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort WVk;
            public ushort WScan;
            public uint DwFlags;
            public uint Time;
            public UIntPtr DwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HardwareInput
        {
            public uint UMsg;
            public ushort WParamL;
            public ushort WParamH;
        }

        private readonly record struct WindowsScan(ushort ScanCode, bool Extended);
    }

    private sealed class InputStats
    {
        private readonly object _lock = new();
        private long _keyboardEvents;
        private long _mouseMoveEvents;
        private long _mouseButtonEvents;
        private long _mouseWheelEvents;
        private long _pointerAbsEvents;
        private long _pointerMappedEvents;
        private long _pointerOutOfBounds;
        private long _absoluteInjectErrors;
        private long _inputErrors;
        private long _windowKeyboardEvents;
        private long _windowMouseMoveEvents;
        private long _windowMouseButtonEvents;
        private long _windowMouseWheelEvents;
        private long _windowPointerAbsEvents;
        private long _windowPointerMappedEvents;
        private long _windowPointerOutOfBounds;
        private long _windowAbsoluteInjectErrors;
        private long _windowInputErrors;

        public void RecordKeyboard()
        {
            lock (_lock)
            {
                _keyboardEvents++;
                _windowKeyboardEvents++;
            }
        }

        public void RecordMouseMove()
        {
            lock (_lock)
            {
                _mouseMoveEvents++;
                _windowMouseMoveEvents++;
            }
        }

        public void RecordMouseButton()
        {
            lock (_lock)
            {
                _mouseButtonEvents++;
                _windowMouseButtonEvents++;
            }
        }

        public void RecordMouseWheel()
        {
            lock (_lock)
            {
                _mouseWheelEvents++;
                _windowMouseWheelEvents++;
            }
        }

        public void RecordPointerAbs()
        {
            lock (_lock)
            {
                _pointerAbsEvents++;
                _windowPointerAbsEvents++;
            }
        }

        public void RecordPointerMapped()
        {
            lock (_lock)
            {
                _pointerMappedEvents++;
                _windowPointerMappedEvents++;
            }
        }

        public void RecordPointerOutOfBounds()
        {
            lock (_lock)
            {
                _pointerOutOfBounds++;
                _windowPointerOutOfBounds++;
            }
        }

        public void RecordAbsoluteInjectError()
        {
            lock (_lock)
            {
                _absoluteInjectErrors++;
                _windowAbsoluteInjectErrors++;
            }
        }

        public void RecordError()
        {
            lock (_lock)
            {
                _inputErrors++;
                _windowInputErrors++;
            }
        }

        public InputStatsSnapshot SnapshotAndResetWindow(string displayBounds)
        {
            lock (_lock)
            {
                var snapshot = new InputStatsSnapshot(
                    _windowKeyboardEvents,
                    _windowMouseMoveEvents,
                    _windowMouseButtonEvents,
                    _windowMouseWheelEvents,
                    _windowPointerAbsEvents,
                    _windowPointerMappedEvents,
                    _windowPointerOutOfBounds,
                    _windowAbsoluteInjectErrors,
                    _windowInputErrors,
                    _keyboardEvents,
                    _mouseMoveEvents,
                    _mouseButtonEvents,
                    _mouseWheelEvents,
                    _pointerAbsEvents,
                    _pointerMappedEvents,
                    _pointerOutOfBounds,
                    _absoluteInjectErrors,
                    _inputErrors,
                    displayBounds);

                _windowKeyboardEvents = 0;
                _windowMouseMoveEvents = 0;
                _windowMouseButtonEvents = 0;
                _windowMouseWheelEvents = 0;
                _windowPointerAbsEvents = 0;
                _windowPointerMappedEvents = 0;
                _windowPointerOutOfBounds = 0;
                _windowAbsoluteInjectErrors = 0;
                _windowInputErrors = 0;
                return snapshot;
            }
        }
    }

    private sealed record InputError(string Code, string Message);

    private sealed record InputStatsSnapshot(
        long KeyboardEvents,
        long MouseMoveEvents,
        long MouseButtonEvents,
        long MouseWheelEvents,
        long PointerAbsEvents,
        long PointerMappedEvents,
        long PointerOutOfBounds,
        long AbsoluteInjectErrors,
        long InputErrors,
        long TotalKeyboardEvents,
        long TotalMouseMoveEvents,
        long TotalMouseButtonEvents,
        long TotalMouseWheelEvents,
        long TotalPointerAbsEvents,
        long TotalPointerMappedEvents,
        long TotalPointerOutOfBounds,
        long TotalAbsoluteInjectErrors,
        long TotalInputErrors,
        string DisplayBounds)
    {
        public bool HasActivity => KeyboardEvents > 0
            || MouseMoveEvents > 0
            || MouseButtonEvents > 0
            || MouseWheelEvents > 0
            || PointerAbsEvents > 0
            || PointerMappedEvents > 0
            || PointerOutOfBounds > 0
            || AbsoluteInjectErrors > 0
            || InputErrors > 0;
    }

    private sealed class VideoServer(IPAddress address, HostOptions options, VideoModeState videoModeState, ControlMessagePublisher controlPublisher)
    {
        private readonly TcpListener _listener = new(address, options.VideoPort);
        private readonly HostOptions _options = options;
        private readonly VideoModeState _videoModeState = videoModeState;
        private readonly ControlMessagePublisher _controlPublisher = controlPublisher;
        private readonly object _connectionLock = new();
        private CancellationTokenSource? _activeConnectionCts;
        private Task? _activeConnectionTask;
        private int _connectionSerial;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                _listener.Start();
                Log("VIDEO", "等待 Android 视频通道连接...");

                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    var connectionId = Interlocked.Increment(ref _connectionSerial);
                    var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    CancellationTokenSource? previousConnectionCts;
                    Task? previousConnectionTask;
                    lock (_connectionLock)
                    {
                        previousConnectionCts = _activeConnectionCts;
                        previousConnectionTask = _activeConnectionTask;
                        _activeConnectionCts = connectionCts;
                        _activeConnectionTask = null;
                    }

                    if (previousConnectionCts is not null)
                    {
                        Log("VIDEO", "检测到新的视频连接，关闭旧连接。");
                        await previousConnectionCts.CancelAsync();
                        await WaitForPreviousVideoConnectionAsync(previousConnectionTask, cancellationToken);
                    }

                    var connectionTask = Task.Run(() => HandleClientAsync(connectionId, client, connectionCts, cancellationToken), cancellationToken);
                    lock (_connectionLock)
                    {
                        if (ReferenceEquals(_activeConnectionCts, connectionCts))
                        {
                            _activeConnectionTask = connectionTask;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log("VIDEO", "正在关闭。");
            }
            catch (SocketException ex)
            {
                Log("VIDEO", $"监听失败: {ex.Message}");
            }
            finally
            {
                _listener.Stop();
            }
        }

        private async Task HandleClientAsync(
            int connectionId,
            TcpClient client,
            CancellationTokenSource connectionCts,
            CancellationToken appToken)
        {
            using (client)
            using (connectionCts)
            {
                var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                Log($"VIDEO {connectionId}", $"视频通道已连接: {remote}");

                try
                {
                    var videoOptions = _videoModeState.CreateOptionsSnapshot();
                    Log($"VIDEO {connectionId}", $"creating video source={FormatVideoSource(videoOptions.VideoSource)} {videoOptions.VideoWidth}x{videoOptions.VideoHeight}@{videoOptions.VideoFps}");
                    await using var source = OperatingSystem.IsWindows()
                        ? CreateVideoSource(connectionId, videoOptions)
                        : CreateNonWindowsVideoSource(connectionId, videoOptions);
                    var session = new VideoSession(connectionId, client, source, videoOptions, _videoModeState, _controlPublisher);
                    await session.RunAsync(connectionCts.Token);
                }
                catch (FileNotFoundException)
                {
                    Log($"VIDEO {connectionId}", $"找不到测试视频文件: {_options.VideoFilePath}");
                    Log("VIDEO", "可执行 tools/generate-test-video.ps1 生成 artifacts/test-videos/sidedock-720p30.h264。");
                }
                catch (InvalidDataException ex)
                {
                    Log($"VIDEO {connectionId}", $"测试视频格式无效: {ex.Message}");
                }
                catch (OperationCanceledException) when (appToken.IsCancellationRequested || connectionCts.IsCancellationRequested)
                {
                    // Application shutdown or superseded connection.
                }
                catch (Exception ex)
                {
                    Log($"VIDEO {connectionId}", $"视频通道异常: {ex.Message}");
                }
                finally
                {
                    lock (_connectionLock)
                    {
                        if (ReferenceEquals(_activeConnectionCts, connectionCts))
                        {
                            _activeConnectionCts = null;
                            _activeConnectionTask = null;
                        }
                    }

                    Log($"VIDEO {connectionId}", "视频通道已断开");
                }
            }
        }

        private static async Task WaitForPreviousVideoConnectionAsync(Task? previousConnectionTask, CancellationToken cancellationToken)
        {
            if (previousConnectionTask is null)
            {
                return;
            }

            try
            {
                await previousConnectionTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                Log("VIDEO", "旧视频连接尚未完全退出，新连接继续接管。");
            }
        }

        [SupportedOSPlatform("windows")]
        private IEncodedVideoSource CreateVideoSource(int connectionId, HostOptions videoOptions)
        {
            if (videoOptions.VideoSource == VideoSourceKind.File)
            {
                return new FileH264VideoSource(videoOptions.VideoFilePath, videoOptions.VideoFps);
            }

            if (videoOptions.VideoSource == VideoSourceKind.SyntheticNv12)
            {
                return new MediaFoundationSyntheticNv12VideoSource(connectionId, videoOptions, _controlPublisher);
            }

            if (videoOptions.VideoSource == VideoSourceKind.Realtime && videoOptions.Encoder == H264EncoderKind.Ffmpeg)
            {
                return new FfmpegRealtimeVideoSource(connectionId, videoOptions, _controlPublisher);
            }

            if (videoOptions.VideoSource == VideoSourceKind.IddGpu)
            {
                Log($"VIDEO {connectionId}", "active video-source=idd-gpu direct-input-probe=startup source-fallback=idd-on-gpu-init-failure");
                return new FallbackEncodedVideoSource(
                    new MediaFoundationGpuVideoSource(
                        connectionId,
                        videoOptions,
                        _controlPublisher,
                        new IddGpuFrameSource(videoOptions, message => Log($"CAPTURE {connectionId}", message))),
                    () => new MediaFoundationBgraVideoSource(
                        connectionId,
                        videoOptions with { VideoSource = VideoSourceKind.Idd },
                        _controlPublisher,
                        new IddBgraFrameSource(videoOptions with { VideoSource = VideoSourceKind.Idd }, message => Log($"CAPTURE {connectionId}", message))),
                    async (ex, cancellationToken) =>
                    {
                        Log($"VIDEO {connectionId}", $"active video-source=idd fallback-from=idd-gpu reason={ex.Message}");
                        await _controlPublisher.PublishAsync("encoder_error", new JsonObject
                        {
                            ["code"] = "GPU_CAPTURE_INIT_FAILED",
                            ["message"] = ex.Message,
                            ["fallback"] = "idd",
                            ["gpuPath"] = true
                        }, cancellationToken);
                    });
            }

            return new MediaFoundationBgraVideoSource(
                connectionId,
                videoOptions,
                _controlPublisher,
                CreateBgraFrameSource(connectionId, videoOptions));
        }

        private IEncodedVideoSource CreateNonWindowsVideoSource(int connectionId, HostOptions videoOptions)
        {
            if (videoOptions.VideoSource == VideoSourceKind.File)
            {
                return new FileH264VideoSource(videoOptions.VideoFilePath, videoOptions.VideoFps);
            }

            if (videoOptions.VideoSource == VideoSourceKind.SyntheticNv12)
            {
                throw new PlatformNotSupportedException("synthetic-nv12 uses the Windows Media Foundation encoder.");
            }

            if (videoOptions.VideoSource == VideoSourceKind.Realtime && videoOptions.Encoder == H264EncoderKind.Ffmpeg)
            {
                return new FfmpegRealtimeVideoSource(connectionId, videoOptions, _controlPublisher);
            }

            throw new PlatformNotSupportedException("Realtime Media Foundation capture is only available on Windows.");
        }

        private IBgraFrameSource CreateBgraFrameSource(int connectionId, HostOptions videoOptions)
        {
            return videoOptions.VideoSource switch
            {
                VideoSourceKind.Realtime => new TestPatternBgraFrameSource(videoOptions.VideoWidth, videoOptions.VideoHeight),
                VideoSourceKind.Region => new RegionBgraFrameSource(videoOptions),
                VideoSourceKind.Window => new WindowBgraFrameSource(videoOptions, message => Log($"CAPTURE {connectionId}", message)),
                VideoSourceKind.Idd => new IddBgraFrameSource(videoOptions, message => Log($"CAPTURE {connectionId}", message)),
                VideoSourceKind.IddGpu => throw new InvalidOperationException("idd-gpu uses a GPU frame source."),
                VideoSourceKind.SyntheticNv12 => throw new InvalidOperationException("synthetic-nv12 bypasses BGRA frame sources."),
                _ => throw new InvalidOperationException($"Unsupported BGRA video source: {videoOptions.VideoSource}")
            };
        }
    }

    private sealed class VideoSession(
        int connectionId,
        TcpClient client,
        IEncodedVideoSource videoSource,
        HostOptions options,
        VideoModeState videoModeState,
        ControlMessagePublisher controlPublisher)
    {
        private static readonly TimeSpan SocketWriteTimeout = TimeSpan.FromSeconds(1);
        private static readonly object DumpFileLock = new();
        private static readonly HashSet<string> InitializedDumpPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly AdaptiveFrameRateController _adaptiveController = new(videoModeState, controlPublisher, options.VideoPort, $"VIDEO {connectionId}");
        private readonly IAdaptiveVideoStatsSource? _adaptiveStatsSource = videoSource as IAdaptiveVideoStatsSource;
        private readonly LatestFrameQueue<EncodedVideoPacket> _sendQueue = new(
            Math.Max(1, options.EncodedPacketQueue),
            singleReader: true,
            singleWriter: true);
        private readonly PipelineStageStats _sendStageStats = new("send");

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            client.NoDelay = true;
            client.SendBufferSize = 1024 * 1024;

            await using var stream = client.GetStream();
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task? producerTask = null;
            long packetsSent = 0;
            long framesSent = 0;
            long bytesSent = 0;
            var lastLog = Stopwatch.StartNew();
            var stats = new EncoderStats();
            await using var dumpStream = OpenDumpStream(options.DumpEncodedPath);

            await videoSource.StartAsync(cancellationToken);
            producerTask = Task.Run(() => PumpEncodedPacketsAsync(sessionCts.Token), sessionCts.Token);

            try
            {
                await foreach (var packet in _sendQueue.Reader.ReadAllAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sendStopwatch = Stopwatch.StartNew();
                    await WritePacketWithTimeoutAsync(stream, packet, cancellationToken);
                    sendStopwatch.Stop();
                    _sendStageStats.RecordProcessed(sendStopwatch.Elapsed.TotalMilliseconds);

                    if (dumpStream is not null)
                    {
                        await dumpStream.WriteAsync(packet.Payload, cancellationToken);
                    }

                    packetsSent++;
                    bytesSent += packet.Payload.Length;
                    if (packet.ContainsPicture)
                    {
                        framesSent++;
                    }

                    stats.RecordSent(packet, sendStopwatch.Elapsed.TotalMilliseconds);
                    if (videoSource is IVideoSendStatsSink sendStatsSink)
                    {
                        sendStatsSink.RecordSent(packet, sendStopwatch.Elapsed.TotalMilliseconds);
                    }

                    if (_adaptiveStatsSource is not null)
                    {
                        await _adaptiveController.ObserveCaptureAsync(_adaptiveStatsSource.LatestCaptureSnapshot, cancellationToken);
                        await _adaptiveController.ObserveEncoderAsync(_adaptiveStatsSource.LatestEncoderSnapshot, cancellationToken);
                    }

                    if (sendStopwatch.Elapsed >= TimeSpan.FromMilliseconds(100))
                    {
                        await controlPublisher.PublishAsync("encoder_error", new JsonObject
                        {
                            ["code"] = "VIDEO_SEND_BLOCKED",
                            ["message"] = $"Socket write took {sendStopwatch.Elapsed.TotalMilliseconds:F1}ms"
                        }, cancellationToken);
                    }

                    if (lastLog.Elapsed >= TimeSpan.FromSeconds(5))
                    {
                        var mb = bytesSent / 1024.0 / 1024.0;
                        var sendSnapshot = _sendStageStats.SnapshotAndResetWindow();
                        Log($"VIDEO {connectionId}", $"已发送 packets={packetsSent} frames={framesSent} bytes={mb:F1}MB pipeline {FormatPipelineStageForLog(sendSnapshot)}");
                        await controlPublisher.PublishAsync(
                            "pipeline_stats",
                            CreatePipelineStatsPayload("socket", gpuPath: false, sendSnapshot),
                            cancellationToken);
                        lastLog.Restart();
                    }
                }
            }
            finally
            {
                await sessionCts.CancelAsync();
                await MediaFoundationBgraVideoSource.WhenAllIgnoringCancellation(producerTask);
            }
        }

        private async Task WritePacketWithTimeoutAsync(NetworkStream stream, EncodedVideoPacket packet, CancellationToken cancellationToken)
        {
            var writeTask = WritePacketAsync(stream, packet, cancellationToken);
            var timeoutTask = Task.Delay(SocketWriteTimeout, cancellationToken);
            var completed = await Task.WhenAny(writeTask, timeoutTask);
            if (ReferenceEquals(completed, writeTask))
            {
                await writeTask;
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            AbortClientSocket();
            _ = writeTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new TimeoutException($"Video socket write timed out after {SocketWriteTimeout.TotalMilliseconds:F0}ms.");
        }

        private static async Task WritePacketAsync(NetworkStream stream, EncodedVideoPacket packet, CancellationToken cancellationToken)
        {
            try
            {
                await VideoPacketWriter.WriteAsync(stream, packet, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            catch (ObjectDisposedException ex)
            {
                throw new IOException("Video socket was closed while writing.", ex);
            }
        }

        private void AbortClientSocket()
        {
            try
            {
                client.Client.Shutdown(SocketShutdown.Both);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
            }

            try
            {
                client.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task PumpEncodedPacketsAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var packet in videoSource.ReadPacketsAsync(cancellationToken))
                {
                    var dropped = _sendQueue.WriteLatest(packet);
                    if (dropped > 0)
                    {
                        _sendStageStats.RecordDropped(dropped);
                        if (videoSource is IVideoSendStatsSink sendStatsSink)
                        {
                            sendStatsSink.RecordDropped(dropped);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
            catch (Exception ex)
            {
                _sendQueue.Complete(ex);
                Log($"VIDEO {connectionId}", $"send queue producer failed: {ex.Message}");
                return;
            }

            _sendQueue.Complete();
        }

        private static FileStream? OpenDumpStream(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            FileMode mode;
            lock (DumpFileLock)
            {
                mode = InitializedDumpPaths.Add(fullPath) ? FileMode.Create : FileMode.Append;
            }

            return new FileStream(fullPath, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1024 * 1024, useAsync: true);
        }
    }

    private static class VideoPacketWriter
    {
        private const int HeaderSize = 24;
        private const int ExtendedHeaderSize = 16;
        private static readonly byte[] Magic = "SDKV"u8.ToArray();

        public static async ValueTask WriteAsync(Stream stream, EncodedVideoPacket packet, CancellationToken cancellationToken)
        {
            var header = new byte[HeaderSize];
            Magic.CopyTo(header, 0);
            header[4] = 2;
            header[5] = packet.IsKeyFrame ? (byte)1 : (byte)0;
            header[6] = (byte)packet.FrameKind;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), unchecked((uint)packet.Sequence));
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(12, 8), packet.TimestampMs);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), packet.Payload.Length);

            var extendedHeader = new byte[ExtendedHeaderSize];
            BinaryPrimitives.WriteInt64LittleEndian(extendedHeader.AsSpan(0, 8), packet.SourceSequence);
            BinaryPrimitives.WriteInt32LittleEndian(extendedHeader.AsSpan(8, 4), Math.Max(0, (int)Math.Round(packet.SourceAgeMs)));
            BinaryPrimitives.WriteInt32LittleEndian(extendedHeader.AsSpan(12, 4), Math.Max(0, (int)Math.Round(packet.EncodeMs * 1000.0)));

            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(extendedHeader, cancellationToken);
            await stream.WriteAsync(packet.Payload, cancellationToken);
        }
    }

    private interface IEncodedVideoSource : IAsyncDisposable
    {
        ValueTask StartAsync(CancellationToken cancellationToken);

        IAsyncEnumerable<EncodedVideoPacket> ReadPacketsAsync(CancellationToken cancellationToken);
    }

    private interface IVideoSendStatsSink
    {
        void RecordSent(EncodedVideoPacket packet, double sendMs);

        void RecordDropped(long count);
    }

        private interface IAdaptiveVideoStatsSource
        {
            CaptureStatsSnapshot LatestCaptureSnapshot { get; }

            RealtimeEncoderStatsSnapshot LatestEncoderSnapshot { get; }
        }

        private static JsonObject CreateEncoderStatsPayload(RealtimeEncoderStatsSnapshot snapshot, bool gpuPath)
        {
            var payload = new JsonObject
            {
                ["framesGenerated"] = snapshot.FramesGenerated,
                ["framesEncoded"] = snapshot.FramesEncoded,
                ["framesSent"] = snapshot.FramesSent,
                ["framesDropped"] = snapshot.FramesDropped,
                ["avgEncodeMs"] = Math.Round(snapshot.AvgEncodeMs, 2),
                ["maxEncodeMs"] = Math.Round(snapshot.MaxEncodeMs, 2),
                ["p50EncodeMs"] = Math.Round(snapshot.P50EncodeMs, 2),
                ["p95EncodeMs"] = Math.Round(snapshot.P95EncodeMs, 2),
                ["p99EncodeMs"] = Math.Round(snapshot.P99EncodeMs, 2),
                ["avgSendMs"] = Math.Round(snapshot.AvgSendMs, 2),
                ["maxSendMs"] = Math.Round(snapshot.MaxSendMs, 2),
                ["p50SendMs"] = Math.Round(snapshot.P50SendMs, 2),
                ["p95SendMs"] = Math.Round(snapshot.P95SendMs, 2),
                ["p99SendMs"] = Math.Round(snapshot.P99SendMs, 2),
                ["outputKbps"] = Math.Round(snapshot.OutputKbps, 0),
                ["streamFps"] = Math.Round(snapshot.StreamFps, 1),
                ["p50LocalLatencyMs"] = Math.Round(snapshot.P50LocalLatencyMs, 2),
                ["p95LocalLatencyMs"] = Math.Round(snapshot.P95LocalLatencyMs, 2),
                ["p99LocalLatencyMs"] = Math.Round(snapshot.P99LocalLatencyMs, 2),
                ["p50FrameIntervalMs"] = Math.Round(snapshot.P50FrameIntervalMs, 2),
                ["p95FrameIntervalMs"] = Math.Round(snapshot.P95FrameIntervalMs, 2),
                ["p99FrameIntervalMs"] = Math.Round(snapshot.P99FrameIntervalMs, 2),
                ["lateFrames"] = snapshot.LateFrames,
                ["newFramesSent"] = snapshot.NewFramesSent,
                ["repeatFramesSent"] = snapshot.RepeatFramesSent,
                ["blackFramesSent"] = snapshot.BlackFramesSent,
                ["keepaliveFramesSent"] = snapshot.KeepaliveFramesSent,
                ["lastKeyFrameSeq"] = snapshot.LastKeyFrameSeq
            };

            if (gpuPath)
            {
                payload["gpuPath"] = true;
            }

            return payload;
        }

        private static void AddEncoderTuningPayload(JsonObject payload, HostOptions options)
        {
            var tuning = options.EncoderTuning;
            payload["bitrate"] = tuning.Bitrate;
            payload["gop"] = tuning.Gop;
            payload["h264Profile"] = tuning.Profile;
            payload["rateControl"] = tuning.RateControl;
            payload["lowLatency"] = tuning.LowLatency;
            payload["bFrames"] = tuning.BFrames;
            payload["maxBitrate"] = tuning.MaxBitrate;
            payload["vbvBuffer"] = tuning.BufferSize;
            payload["tuningPreset"] = tuning.PresetName;
            payload["tuningResolution"] = tuning.ResolutionPreset;
            payload["bitrateSource"] = tuning.BitrateSource;
            payload["gopSource"] = tuning.GopSource;
            payload["recommendedBitrate"] = tuning.RecommendedBitrate;
            payload["legacyAutoBitrate"] = tuning.LegacyAutoBitrate;
            payload["bitrateDeltaFromLegacy"] = tuning.BitrateDeltaFromLegacy;
            payload["nv12PoolSize"] = options.Nv12PoolSize;
            payload["encodedPacketQueue"] = options.EncodedPacketQueue;
        }

        private static string FormatEncoderTuningForLog(HostOptions options)
        {
            var tuning = options.EncoderTuning;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"preset={tuning.PresetName} profile={tuning.Profile} lowLatency={tuning.LowLatency.ToString().ToLowerInvariant()} rateControl={tuning.RateControl} gop={tuning.Gop} bFrames={tuning.BFrames} bitrate={tuning.Bitrate} maxrate={tuning.MaxBitrate} buffer={tuning.BufferSize} recommended={tuning.RecommendedBitrate} legacyAuto={tuning.LegacyAutoBitrate} delta={tuning.BitrateDeltaFromLegacy} nv12Pool={options.Nv12PoolSize} encodedPacketQueue={options.EncodedPacketQueue}");
        }

        private static void AddCapturePercentiles(JsonObject payload, CaptureStatsSnapshot snapshot)
        {
            payload["p50CaptureMs"] = Math.Round(snapshot.P50CaptureMs, 2);
            payload["p95CaptureMs"] = Math.Round(snapshot.P95CaptureMs, 2);
            payload["p99CaptureMs"] = Math.Round(snapshot.P99CaptureMs, 2);
            payload["p50ConvertMs"] = Math.Round(snapshot.P50ConvertMs, 2);
            payload["p95ConvertMs"] = Math.Round(snapshot.P95ConvertMs, 2);
            payload["p99ConvertMs"] = Math.Round(snapshot.P99ConvertMs, 2);
            payload["captureFps"] = Math.Round(snapshot.CaptureFps, 1);
            payload["convertFps"] = Math.Round(snapshot.ConvertFps, 1);
        }

        private static JsonObject CreatePipelineStatsPayload(
            string source,
            bool gpuPath,
            params PipelineStageStatsSnapshot[] snapshots)
        {
            var stages = new JsonArray();
            foreach (var snapshot in snapshots)
            {
                stages.Add(new JsonObject
                {
                    ["name"] = snapshot.Name,
                    ["fps"] = Math.Round(snapshot.Fps, 1),
                    ["processed"] = snapshot.TotalProcessed,
                    ["dropped"] = snapshot.TotalDropped,
                    ["windowProcessed"] = snapshot.WindowProcessed,
                    ["windowDropped"] = snapshot.WindowDropped,
                    ["avgMs"] = Math.Round(snapshot.AvgMs, 2),
                    ["p50Ms"] = Math.Round(snapshot.P50Ms, 2),
                    ["p95Ms"] = Math.Round(snapshot.P95Ms, 2),
                    ["p99Ms"] = Math.Round(snapshot.P99Ms, 2)
                });
            }

            return new JsonObject
            {
                ["source"] = source,
                ["gpuPath"] = gpuPath,
                ["stages"] = stages
            };
        }

        private static string FormatPipelineStageForLog(PipelineStageStatsSnapshot snapshot)
        {
            return $"{snapshot.Name}={snapshot.Fps:F1}fps drop={snapshot.WindowDropped}/{snapshot.TotalDropped} p95={snapshot.P95Ms:F1}ms";
        }

    private interface IBgraFrameSource : IDisposable
    {
        string SourceName { get; }

        string SourceDescription { get; }

        void Start(CancellationToken cancellationToken);

        void Capture(byte[] bgraFrame, int outputWidth, int outputHeight, CancellationToken cancellationToken);
    }

    private sealed class FallbackEncodedVideoSource : IEncodedVideoSource, IVideoSendStatsSink, IAdaptiveVideoStatsSource
    {
        private readonly IEncodedVideoSource _primary;
        private readonly Func<IEncodedVideoSource> _fallbackFactory;
        private readonly Func<Exception, CancellationToken, ValueTask> _onFallbackAsync;
        private IEncodedVideoSource? _active;
        private bool _usingFallback;
        private bool _primaryDisposed;

        public FallbackEncodedVideoSource(
            IEncodedVideoSource primary,
            Func<IEncodedVideoSource> fallbackFactory,
            Func<Exception, CancellationToken, ValueTask> onFallbackAsync)
        {
            _primary = primary;
            _fallbackFactory = fallbackFactory;
            _onFallbackAsync = onFallbackAsync;
        }

        public async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                _active = _primary;
                await _primary.StartAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await SwitchToFallbackAsync(ex, cancellationToken);
            }
        }

        public async IAsyncEnumerable<EncodedVideoPacket> ReadPacketsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (true)
            {
                var active = _active ?? throw new InvalidOperationException("Video source has not started.");
                IAsyncEnumerator<EncodedVideoPacket>? enumerator = null;
                Exception? fallbackException = null;
                try
                {
                    enumerator = active.ReadPacketsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
                    while (true)
                    {
                        EncodedVideoPacket packet;
                        try
                        {
                            if (!await enumerator.MoveNextAsync())
                            {
                                yield break;
                            }

                            packet = enumerator.Current;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex) when (!_usingFallback && ReferenceEquals(active, _primary))
                        {
                            fallbackException = ex;
                            break;
                        }

                        yield return packet;
                    }
                }
                finally
                {
                    if (enumerator is not null)
                    {
                        await enumerator.DisposeAsync();
                    }
                }

                if (fallbackException is not null)
                {
                    await SwitchToFallbackAsync(fallbackException, cancellationToken);
                    continue;
                }

                if (fallbackException is null)
                {
                    yield break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_active is not null)
            {
                await _active.DisposeAsync();
            }

            if (!_primaryDisposed && !ReferenceEquals(_active, _primary))
            {
                await _primary.DisposeAsync();
            }
        }

        public void RecordSent(EncodedVideoPacket packet, double sendMs)
        {
            if (_active is IVideoSendStatsSink sink)
            {
                sink.RecordSent(packet, sendMs);
            }
        }

        public void RecordDropped(long count)
        {
            if (_active is IVideoSendStatsSink sink)
            {
                sink.RecordDropped(count);
            }
        }

        public CaptureStatsSnapshot LatestCaptureSnapshot => _active is IAdaptiveVideoStatsSource statsSource
            ? statsSource.LatestCaptureSnapshot
            : CreateEmptyCaptureSnapshot();

        public RealtimeEncoderStatsSnapshot LatestEncoderSnapshot => _active is IAdaptiveVideoStatsSource statsSource
            ? statsSource.LatestEncoderSnapshot
            : CreateEmptyEncoderSnapshot();

        private static CaptureStatsSnapshot CreateEmptyCaptureSnapshot()
        {
            return new CaptureStatsSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        private static RealtimeEncoderStatsSnapshot CreateEmptyEncoderSnapshot()
        {
            return new RealtimeEncoderStatsSnapshot(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        private async ValueTask SwitchToFallbackAsync(Exception ex, CancellationToken cancellationToken)
        {
            _usingFallback = true;
            await _onFallbackAsync(ex, cancellationToken);
            await _primary.DisposeAsync();
            _primaryDisposed = true;

            var fallback = _fallbackFactory();
            _active = fallback;
            await fallback.StartAsync(cancellationToken);
        }
    }

    private interface IGpuFrameSource : IDisposable
    {
        string SourceName { get; }

        string SourceDescription { get; }

        ID3D11Device Device { get; }

        ID3D11DeviceContext Context { get; }

        int Width { get; }

        int Height { get; }

        int SlotCount { get; }

        long FramesDropped { get; }

        double LastFrameAgeMs { get; }

        void Start(CancellationToken cancellationToken);

        GpuFrameLease AcquireLatestFrame(CancellationToken cancellationToken);

        bool TryAcquireLatestFrame(out GpuFrameLease? frame);
    }

    private sealed class GpuFrameLease : IDisposable
    {
        private readonly Action? _release;
        private bool _disposed;

        public GpuFrameLease(ID3D11Texture2D texture, long sequence, long timestampQpc, Action release)
        {
            Texture = texture;
            Sequence = sequence;
            TimestampQpc = timestampQpc;
            _release = release;
        }

        public ID3D11Texture2D Texture { get; }

        public long Sequence { get; }

        public long TimestampQpc { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _release?.Invoke();
            }
            catch (Exception ex) when (ex is SharpGenException or COMException or ObjectDisposedException or InvalidOperationException)
            {
                Debug.WriteLine($"GpuFrameLease release failed: {ex.Message}");
            }
        }
    }

    private enum EncodedFrameKind
    {
        New = 0,
        Repeat = 1,
        Black = 2,
        Keepalive = 3
    }

    private sealed class LatestFrameQueue<T>
    {
        private readonly Channel<T> _channel;
        private readonly Action<T>? _disposeDropped;

        public LatestFrameQueue(int capacity, bool singleReader = true, bool singleWriter = true, Action<T>? disposeDropped = null)
        {
            _disposeDropped = disposeDropped;
            _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(Math.Max(1, capacity))
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = singleReader,
                SingleWriter = singleWriter
            });
        }

        public ChannelReader<T> Reader => _channel.Reader;

        public void Complete(Exception? error = null)
        {
            _channel.Writer.TryComplete(error);
        }

        public int WriteLatest(T item)
        {
            if (_channel.Writer.TryWrite(item))
            {
                return 0;
            }

            var dropped = 0;
            while (_channel.Reader.TryRead(out var stale))
            {
                dropped++;
                _disposeDropped?.Invoke(stale);
            }

            if (_channel.Writer.TryWrite(item))
            {
                return dropped;
            }

            dropped++;
            _disposeDropped?.Invoke(item);
            return dropped;
        }
    }

    private sealed class PipelineStageStats(string name)
    {
        private readonly object _lock = new();
        private readonly string _name = name;
        private long _processed;
        private long _dropped;
        private long _windowProcessed;
        private long _windowDropped;
        private double _durationMsTotal;
        private double _windowDurationMsTotal;
        private long _windowStartedAtTicks = Stopwatch.GetTimestamp();
        private readonly List<double> _samples = new();
        private readonly List<double> _windowSamples = new();

        public void RecordProcessed(double durationMs)
        {
            if (double.IsNaN(durationMs) || double.IsInfinity(durationMs) || durationMs < 0)
            {
                durationMs = 0;
            }

            lock (_lock)
            {
                _processed++;
                _windowProcessed++;
                _durationMsTotal += durationMs;
                _windowDurationMsTotal += durationMs;
                _samples.Add(durationMs);
                _windowSamples.Add(durationMs);
            }
        }

        public void RecordDropped(long count = 1)
        {
            if (count <= 0)
            {
                return;
            }

            lock (_lock)
            {
                _dropped += count;
                _windowDropped += count;
            }
        }

        public PipelineStageStatsSnapshot SnapshotAndResetWindow()
        {
            lock (_lock)
            {
                var percentiles = SampleStatistics.Percentiles(_windowSamples);
                var elapsedSeconds = Math.Max(0.001, (Stopwatch.GetTimestamp() - _windowStartedAtTicks) / (double)Stopwatch.Frequency);
                var snapshot = new PipelineStageStatsSnapshot(
                    _name,
                    _processed,
                    _dropped,
                    _windowProcessed,
                    _windowDropped,
                    _windowProcessed == 0 ? 0 : _windowDurationMsTotal / _windowProcessed,
                    percentiles.P50,
                    percentiles.P95,
                    percentiles.P99,
                    _windowProcessed / elapsedSeconds);

                _windowProcessed = 0;
                _windowDropped = 0;
                _windowDurationMsTotal = 0;
                _windowSamples.Clear();
                _windowStartedAtTicks = Stopwatch.GetTimestamp();
                return snapshot;
            }
        }
    }

    private sealed record PipelineStageStatsSnapshot(
        string Name,
        long TotalProcessed,
        long TotalDropped,
        long WindowProcessed,
        long WindowDropped,
        double AvgMs,
        double P50Ms,
        double P95Ms,
        double P99Ms,
        double Fps);

    private sealed class BgraFrame : IDisposable
    {
        private bool _disposed;

        public BgraFrame(byte[] data, long frameId, long timestampMs)
        {
            Data = data;
            FrameId = frameId;
            TimestampMs = timestampMs;
        }

        public byte[] Data { get; }

        public long FrameId { get; }

        public long TimestampMs { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ArrayPool<byte>.Shared.Return(Data);
        }
    }

    private sealed class Nv12Frame : IDisposable
    {
        private readonly bool _returnToPool;
        private bool _disposed;

        public Nv12Frame(
            byte[] data,
            long frameId,
            EncodedFrameKind frameKind,
            long sourceSequence,
            double sourceAgeMs,
            long generatedTimestampMs,
            bool returnToPool)
        {
            Data = data;
            FrameId = frameId;
            FrameKind = frameKind;
            SourceSequence = sourceSequence;
            SourceAgeMs = sourceAgeMs;
            GeneratedTimestampMs = generatedTimestampMs;
            _returnToPool = returnToPool;
        }

        public byte[] Data { get; }

        public long FrameId { get; }

        public EncodedFrameKind FrameKind { get; }

        public long SourceSequence { get; }

        public double SourceAgeMs { get; }

        public long GeneratedTimestampMs { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_returnToPool)
            {
                ArrayPool<byte>.Shared.Return(Data);
            }
        }
    }

    private sealed class GpuAcquiredFrame : IDisposable
    {
        private bool _disposed;

        public GpuAcquiredFrame(
            GpuFrameLease? lease,
            long frameId,
            EncodedFrameKind frameKind,
            long sourceSequence,
            double sourceAgeMs,
            long generatedTimestampMs)
        {
            Lease = lease;
            FrameId = frameId;
            FrameKind = frameKind;
            SourceSequence = sourceSequence;
            SourceAgeMs = sourceAgeMs;
            GeneratedTimestampMs = generatedTimestampMs;
        }

        public GpuFrameLease? Lease { get; }

        public long FrameId { get; }

        public EncodedFrameKind FrameKind { get; }

        public long SourceSequence { get; }

        public double SourceAgeMs { get; }

        public long GeneratedTimestampMs { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Lease?.Dispose();
        }
    }

    private sealed class GpuConvertedFrame : IDisposable
    {
        private readonly Action? _release;
        private bool _disposed;

        public GpuConvertedFrame(
            ID3D11Texture2D texture,
            long frameId,
            EncodedFrameKind frameKind,
            long sourceSequence,
            double sourceAgeMs,
            long generatedTimestampMs,
            Action release)
        {
            Texture = texture;
            FrameId = frameId;
            FrameKind = frameKind;
            SourceSequence = sourceSequence;
            SourceAgeMs = sourceAgeMs;
            GeneratedTimestampMs = generatedTimestampMs;
            _release = release;
        }

        public ID3D11Texture2D Texture { get; }

        public long FrameId { get; }

        public EncodedFrameKind FrameKind { get; }

        public long SourceSequence { get; }

        public double SourceAgeMs { get; }

        public long GeneratedTimestampMs { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _release?.Invoke();
        }
    }

    private sealed class FileH264VideoSource : IEncodedVideoSource, IAdaptiveVideoStatsSource
    {
        private readonly string _path;
        private readonly int _targetFps;
        private H264AnnexBStream? _videoStream;
        private long _sequence;
        private long _frameId;

        public FileH264VideoSource(string path, int fps)
        {
            _path = path;
            _targetFps = Math.Max(1, fps);
        }

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            _videoStream = H264AnnexBStream.Load(_path);
            Log("VIDEO", $"已加载 {_videoStream.PacketCount} 个视频包，{_videoStream.FramePacketCount} 个图像包。");
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<EncodedVideoPacket> ReadPacketsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var videoStream = _videoStream ?? throw new InvalidOperationException("Video source has not started.");
            var pacer = new FramePacer(_targetFps);

            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var packet in videoStream.Packets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var frameId = packet.ContainsPicture ? ++_frameId : _frameId;

                    yield return new EncodedVideoPacket(
                        packet.Payload,
                        packet.IsKeyFrame,
                        packet.ContainsPicture,
                        frameId,
                        Interlocked.Increment(ref _sequence) - 1,
                        nowMs,
                        0,
                        EncodedFrameKind.New,
                        frameId,
                        0);

                    if (packet.ContainsPicture)
                    {
                        await pacer.WaitForNextFrameAsync(cancellationToken);
                    }
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public CaptureStatsSnapshot LatestCaptureSnapshot => CreateEmptyCaptureSnapshot();

        public RealtimeEncoderStatsSnapshot LatestEncoderSnapshot => CreateEmptyEncoderSnapshot();

        private static CaptureStatsSnapshot CreateEmptyCaptureSnapshot()
        {
            return new CaptureStatsSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        private static RealtimeEncoderStatsSnapshot CreateEmptyEncoderSnapshot()
        {
            return new RealtimeEncoderStatsSnapshot(
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0);
        }
    }

    private sealed class EncoderStats
    {
        private readonly object _lock = new();
        private long _framesSent;
        private long _bytesSent;
        private long _lastKeyFrameSeq = -1;
        private double _sendMsTotal;
        private double _sendMsMax;

        public void RecordSent(EncodedVideoPacket packet, double sendMs)
        {
            lock (_lock)
            {
                if (packet.ContainsPicture)
                {
                    _framesSent++;
                }

                _bytesSent += packet.Payload.Length;
                _sendMsTotal += sendMs;
                _sendMsMax = Math.Max(_sendMsMax, sendMs);

                if (packet.IsKeyFrame)
                {
                    _lastKeyFrameSeq = packet.Sequence;
                }
            }
        }

        public (long FramesSent, long BytesSent, double AvgSendMs, double MaxSendMs, long LastKeyFrameSeq) Snapshot()
        {
            lock (_lock)
            {
                var avgSendMs = _framesSent == 0 ? 0 : _sendMsTotal / _framesSent;
                return (_framesSent, _bytesSent, avgSendMs, _sendMsMax, _lastKeyFrameSeq);
            }
        }
    }

    private sealed class FfmpegRealtimeVideoSource : IEncodedVideoSource, IVideoSendStatsSink, IAdaptiveVideoStatsSource
    {
        private readonly int _connectionId;
        private readonly HostOptions _options;
        private readonly ControlMessagePublisher _controlPublisher;
        private readonly TestPatternGenerator _patternGenerator;
        private readonly RealtimeEncoderStats _stats;
        private CaptureStatsSnapshot _latestCaptureSnapshot = CreateEmptyCaptureSnapshot();
        private RealtimeEncoderStatsSnapshot _latestEncoderSnapshot = CreateEmptyEncoderSnapshot();
        private Process? _process;
        private CancellationTokenSource? _sourceCts;
        private Task? _inputTask;
        private Task? _stderrTask;
        private Task? _statsTask;
        private long _sequence;

        public FfmpegRealtimeVideoSource(
            int connectionId,
            HostOptions options,
            ControlMessagePublisher controlPublisher)
        {
            _connectionId = connectionId;
            _options = options;
            _controlPublisher = controlPublisher;
            _patternGenerator = new TestPatternGenerator(options.VideoWidth, options.VideoHeight);
            _stats = new RealtimeEncoderStats(options.VideoFps);
        }

        public async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            var ffmpegPath = ResolveFfmpegPath(_options.FfmpegPath);
            var startInfo = new ProcessStartInfo(ffmpegPath, BuildArguments())
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _process = Process.Start(startInfo);
            if (_process is null)
            {
                await PublishErrorAsync("ENCODER_INIT_FAILED", "Unable to start ffmpeg.", cancellationToken);
                throw new InvalidOperationException("Unable to start ffmpeg.");
            }

            if (!OperatingSystem.IsWindows())
            {
                await PublishErrorAsync("ENCODER_INIT_FAILED", "Media Foundation encoder is only available on Windows.", cancellationToken);
                throw new PlatformNotSupportedException("Media Foundation encoder is only available on Windows.");
            }

            _sourceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _inputTask = Task.Run(() => WriteInputFramesAsync(_process.StandardInput.BaseStream, _sourceCts.Token), _sourceCts.Token);
            _stderrTask = Task.Run(() => DrainStderrAsync(_process.StandardError, _sourceCts.Token), _sourceCts.Token);
            _statsTask = Task.Run(() => PublishStatsLoopAsync(_sourceCts.Token), _sourceCts.Token);

            Log($"ENCODER {_connectionId}", $"start source=realtime encoder=ffmpeg {_options.VideoWidth}x{_options.VideoHeight}@{_options.VideoFps} {FormatEncoderTuningForLog(_options)}");
            var encoderStartPayload = new JsonObject
            {
                ["source"] = "realtime_test_pattern",
                ["encoder"] = "ffmpeg",
                ["width"] = _options.VideoWidth,
                ["height"] = _options.VideoHeight,
                ["fps"] = _options.VideoFps,
                ["codec"] = "h264",
                ["format"] = "annexb"
            };
            AddEncoderTuningPayload(encoderStartPayload, _options);
            await _controlPublisher.PublishAsync("encoder_start", encoderStartPayload, cancellationToken);
        }

        public async IAsyncEnumerable<EncodedVideoPacket> ReadPacketsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var process = _process ?? throw new InvalidOperationException("ffmpeg has not started.");
            var parser = new StreamingAnnexBAccessUnitParser();
            var buffer = new byte[64 * 1024];

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    if (process.HasExited)
                    {
                        await PublishErrorAsync("ENCODER_OUTPUT_FAILED", $"ffmpeg exited with code {process.ExitCode}.", cancellationToken);
                    }

                    break;
                }

                foreach (var accessUnit in parser.Append(buffer.AsSpan(0, read)))
                {
                    yield return CreatePacket(accessUnit);
                }
            }

            foreach (var accessUnit in parser.Flush())
            {
                yield return CreatePacket(accessUnit);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_sourceCts is not null)
            {
                await _sourceCts.CancelAsync();
            }

            if (_process is not null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.StandardInput.Close();
                        await Task.WhenAny(_process.WaitForExitAsync(), Task.Delay(1000));
                    }

                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex)
                {
                    Log($"ENCODER {_connectionId}", $"停止 ffmpeg 时出现异常: {ex.Message}");
                }
            }

            await WhenAllIgnoringCancellation(_inputTask, _stderrTask, _statsTask);

            var snapshot = _stats.Snapshot();
            Log($"ENCODER {_connectionId}", $"stop generated={snapshot.FramesGenerated} encoded={snapshot.FramesEncoded} sent={snapshot.FramesSent} dropped={snapshot.FramesDropped}");
            await _controlPublisher.PublishAsync("encoder_stop", new JsonObject
            {
                ["reason"] = "video_client_disconnected",
                ["framesGenerated"] = snapshot.FramesGenerated,
                ["framesEncoded"] = snapshot.FramesEncoded,
                ["framesSent"] = snapshot.FramesSent
            }, CancellationToken.None);

            _sourceCts?.Dispose();
            _process?.Dispose();
        }

        public void RecordSent(EncodedVideoPacket packet, double sendMs)
        {
            _stats.RecordSent(packet, sendMs);
        }

        public void RecordDropped(long count)
        {
            _stats.RecordDropped(count);
        }

        public CaptureStatsSnapshot LatestCaptureSnapshot => _latestCaptureSnapshot;

        public RealtimeEncoderStatsSnapshot LatestEncoderSnapshot => _latestEncoderSnapshot;

        private static CaptureStatsSnapshot CreateEmptyCaptureSnapshot()
        {
            return new CaptureStatsSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        private static RealtimeEncoderStatsSnapshot CreateEmptyEncoderSnapshot()
        {
            return new RealtimeEncoderStatsSnapshot(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        private EncodedVideoPacket CreatePacket(byte[] payload)
        {
            var containsPicture = H264AnnexBStream.ContainsPicture(payload);
            var isKeyFrame = H264AnnexBStream.ContainsNalType(payload, 5);
            var frameId = containsPicture ? _stats.RecordEncoded(payload.Length) : _stats.LastEncodedFrameId;

            return new EncodedVideoPacket(
                payload,
                isKeyFrame,
                containsPicture,
                frameId,
                Interlocked.Increment(ref _sequence) - 1,
                _stats.GetFrameTimestamp(frameId),
                _stats.GetFrameEncodeMs(frameId),
                EncodedFrameKind.New,
                frameId,
                0);
        }

        private async Task WriteInputFramesAsync(Stream stdin, CancellationToken cancellationToken)
        {
            var pacer = new FramePacer(
                _options.VideoFps,
                _stats.RecordFrameInterval,
                _stats.RecordLateFrame,
                () => _stats.RecordDropped());
            var frameBuffer = new byte[_options.VideoWidth * _options.VideoHeight * 4];
            long frameId = 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var generatedAt = pacer.MarkFrameStart();
                    _patternGenerator.Render(frameBuffer, frameId, generatedAt);
                    _stats.RecordGenerated(frameId, generatedAt.ToUnixTimeMilliseconds());

                    await stdin.WriteAsync(frameBuffer, cancellationToken);
                    await stdin.FlushAsync(cancellationToken);

                    frameId++;
                    await pacer.WaitForNextFrameAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
            catch (Exception ex)
            {
                await PublishErrorAsync("ENCODER_INPUT_FAILED", ex.Message, CancellationToken.None);
                throw;
            }
        }

        private async Task DrainStderrAsync(StreamReader stderr, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await stderr.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        Log($"FFMPEG {_connectionId}", line);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
        }

        private async Task PublishStatsLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    var snapshot = _stats.SnapshotAndResetWindow();
                    _latestEncoderSnapshot = snapshot;
                    Log($"ENCODER {_connectionId}", $"stats generated={snapshot.FramesGenerated} encoded={snapshot.FramesEncoded} sent={snapshot.FramesSent} dropped={snapshot.FramesDropped} late={snapshot.LateFrames} streamFps={snapshot.StreamFps:F1} localLatencyP95={snapshot.P95LocalLatencyMs:F1}ms new={snapshot.NewFramesSent} repeat={snapshot.RepeatFramesSent} avgFrameInterval={snapshot.P50FrameIntervalMs:F2}/{snapshot.P95FrameIntervalMs:F2}/{snapshot.P99FrameIntervalMs:F2}ms avgEncode={snapshot.AvgEncodeMs:F1}ms p95Encode={snapshot.P95EncodeMs:F1}ms p99Encode={snapshot.P99EncodeMs:F1}ms avgSend={snapshot.AvgSendMs:F1}ms p95Send={snapshot.P95SendMs:F1}ms kbps={snapshot.OutputKbps:F0}");
                    var payload = CreateEncoderStatsPayload(snapshot, gpuPath: false);
                    AddEncoderTuningPayload(payload, _options);
                    await _controlPublisher.PublishAsync("encoder_stats", payload, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
        }

        private Task PublishErrorAsync(string code, string message, CancellationToken cancellationToken)
        {
            Log($"ENCODER {_connectionId}", $"{code}: {message}");
            return _controlPublisher.PublishAsync("encoder_error", new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }, cancellationToken).AsTask();
        }

        private string BuildArguments()
        {
            var tuning = _options.EncoderTuning;
            return string.Join(" ", new[]
            {
                "-hide_banner",
                "-loglevel warning",
                "-f rawvideo",
                "-pix_fmt bgra",
                $"-s:v {_options.VideoWidth}x{_options.VideoHeight}",
                $"-r {_options.VideoFps}",
                "-i pipe:0",
                "-an",
                "-c:v libx264",
                "-preset ultrafast",
                "-tune zerolatency",
                $"-profile:v {tuning.Profile}",
                "-pix_fmt yuv420p",
                $"-b:v {tuning.Bitrate}",
                $"-maxrate {tuning.MaxBitrate}",
                $"-bufsize {tuning.BufferSize}",
                $"-g {tuning.Gop}",
                $"-bf {tuning.BFrames}",
                "-refs 1",
                $"-x264-params keyint={tuning.Gop}:min-keyint={tuning.Gop}:scenecut=0:repeat-headers=1:bframes={tuning.BFrames}:rc-lookahead=0:force-cfr=1:nal-hrd=cbr",
                "-bsf:v h264_metadata=aud=insert",
                "-f h264",
                "pipe:1"
            });
        }

        private static string ResolveFfmpegPath(string? configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                if (File.Exists(configuredPath))
                {
                    return configuredPath;
                }

                throw new FileNotFoundException("Configured ffmpeg path was not found.", configuredPath);
            }

            return "ffmpeg";
        }

        public static async Task WhenAllIgnoringCancellation(params Task?[] tasks)
        {
            foreach (var task in tasks)
            {
                if (task is null)
                {
                    continue;
                }

                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                    // Expected during teardown.
                }
                catch (Exception ex)
                {
                    Log("ENCODER", $"后台任务结束异常: {ex.Message}");
                }
            }
        }
    }

    private sealed class MediaFoundationSyntheticNv12VideoSource : IEncodedVideoSource, IVideoSendStatsSink, IAdaptiveVideoStatsSource
    {
        private readonly int _connectionId;
        private readonly HostOptions _options;
        private readonly ControlMessagePublisher _controlPublisher;
        private readonly SyntheticNv12FramePool _framePool;
        private readonly RealtimeEncoderStats _stats;
        private readonly Channel<EncodedVideoPacket> _packets;
        private CaptureStatsSnapshot _latestCaptureSnapshot = CreateEmptyCaptureSnapshot();
        private RealtimeEncoderStatsSnapshot _latestEncoderSnapshot = CreateEmptyEncoderSnapshot();
        private CancellationTokenSource? _sourceCts;
        private Task? _encoderTask;
        private Task? _statsTask;
        private bool _encoderStarted;
        private long _sequence;

        public MediaFoundationSyntheticNv12VideoSource(
            int connectionId,
            HostOptions options,
            ControlMessagePublisher controlPublisher)
        {
            _connectionId = connectionId;
            _options = options;
            _controlPublisher = controlPublisher;
            _framePool = new SyntheticNv12FramePool(options.VideoWidth, options.VideoHeight, options.Nv12PoolSize);
            _stats = new RealtimeEncoderStats(options.VideoFps);
            _packets = Channel.CreateBounded<EncodedVideoPacket>(new BoundedChannelOptions(Math.Max(1, options.EncodedPacketQueue))
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
        }

        public async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                await PublishErrorAsync("ENCODER_INIT_FAILED", "Media Foundation encoder is only available on Windows.", cancellationToken);
                throw new PlatformNotSupportedException("Media Foundation encoder is only available on Windows.");
            }

            _sourceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _encoderTask = Task.Run(
                () =>
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        throw new PlatformNotSupportedException("Media Foundation encoder is only available on Windows.");
                    }

                    return EncodeLoopAsync(_sourceCts.Token);
                },
                _sourceCts.Token);
            _statsTask = Task.Run(() => PublishStatsLoopAsync(_sourceCts.Token), _sourceCts.Token);

            Log($"ENCODER {_connectionId}", $"start source=synthetic-nv12 encoder=mediafoundation input=nv12 {_options.VideoWidth}x{_options.VideoHeight}@{_options.VideoFps} {FormatEncoderTuningForLog(_options)}");
            var encoderStartPayload = new JsonObject
            {
                ["source"] = "synthetic-nv12",
                ["encoder"] = "mediafoundation",
                ["width"] = _options.VideoWidth,
                ["height"] = _options.VideoHeight,
                ["fps"] = _options.VideoFps,
                ["codec"] = "h264",
                ["format"] = "annexb",
                ["inputFormat"] = "nv12",
                ["synthetic"] = true
            };
            AddEncoderTuningPayload(encoderStartPayload, _options);
            await _controlPublisher.PublishAsync("encoder_start", encoderStartPayload, cancellationToken);
            _encoderStarted = true;
        }

        public async IAsyncEnumerable<EncodedVideoPacket> ReadPacketsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (await _packets.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_packets.Reader.TryRead(out var packet))
                {
                    yield return packet;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_sourceCts is not null)
            {
                await _sourceCts.CancelAsync();
            }

            await MediaFoundationBgraVideoSource.WhenAllIgnoringCancellation(_encoderTask, _statsTask);
            _packets.Writer.TryComplete();

            var snapshot = _stats.Snapshot();
            if (_encoderStarted)
            {
                Log($"ENCODER {_connectionId}", $"stop generated={snapshot.FramesGenerated} encoded={snapshot.FramesEncoded} sent={snapshot.FramesSent} dropped={snapshot.FramesDropped}");
                await _controlPublisher.PublishAsync("encoder_stop", new JsonObject
                {
                    ["reason"] = "video_client_disconnected",
                    ["source"] = "synthetic-nv12",
                    ["framesGenerated"] = snapshot.FramesGenerated,
                    ["framesEncoded"] = snapshot.FramesEncoded,
                    ["framesSent"] = snapshot.FramesSent
                }, CancellationToken.None);
            }

            _sourceCts?.Dispose();
        }

        public void RecordSent(EncodedVideoPacket packet, double sendMs)
        {
            _stats.RecordSent(packet, sendMs);
        }

        public void RecordDropped(long count)
        {
            _stats.RecordDropped(count);
        }

        public CaptureStatsSnapshot LatestCaptureSnapshot => _latestCaptureSnapshot;

        public RealtimeEncoderStatsSnapshot LatestEncoderSnapshot => _latestEncoderSnapshot;

        [SupportedOSPlatform("windows")]
        private async Task EncodeLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Yield();
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("Media Foundation encoder is only available on Windows.");
                }

                using var encoder = new MediaFoundationH264Encoder(_options);
                encoder.Start();
                var pacer = new FramePacer(
                    _options.VideoFps,
                    _stats.RecordFrameInterval,
                    _stats.RecordLateFrame,
                    () => _stats.RecordDropped());
                long frameId = 0;

                while (!cancellationToken.IsCancellationRequested)
                {
                    var generatedAt = pacer.MarkFrameStart();
                    var nv12Frame = _framePool.GetFrame(frameId);
                    _stats.RecordGenerated(frameId, generatedAt.ToUnixTimeMilliseconds());

                    var encodeStopwatch = Stopwatch.StartNew();
                    var payloads = encoder.EncodeFrame(nv12Frame, frameId);
                    encodeStopwatch.Stop();

                    foreach (var payload in payloads)
                    {
                        var packet = CreatePacket(payload, frameId, encodeStopwatch.Elapsed.TotalMilliseconds);
                        if (!_packets.Writer.TryWrite(packet))
                        {
                            _stats.RecordDropped();
                        }
                    }

                    frameId++;
                    await pacer.WaitForNextFrameAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
            catch (Exception ex)
            {
                await PublishErrorAsync("ENCODER_FAILED", ex.Message, CancellationToken.None);
                _packets.Writer.TryComplete(ex);
                return;
            }

            _packets.Writer.TryComplete();
        }

        private EncodedVideoPacket CreatePacket(byte[] payload, long sourceSequence, double encodeMs)
        {
            var containsPicture = H264AnnexBStream.ContainsPicture(payload);
            var isKeyFrame = H264AnnexBStream.ContainsNalType(payload, 5);
            var frameId = containsPicture
                ? _stats.RecordEncoded(payload.Length, encodeMs)
                : _stats.LastEncodedFrameId;

            return new EncodedVideoPacket(
                payload,
                isKeyFrame,
                containsPicture,
                frameId,
                Interlocked.Increment(ref _sequence) - 1,
                frameId >= 0 ? _stats.GetFrameTimestamp(frameId) : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                frameId >= 0 ? _stats.GetFrameEncodeMs(frameId) : 0,
                EncodedFrameKind.New,
                sourceSequence,
                0);
        }

        private async Task PublishStatsLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    var snapshot = _stats.SnapshotAndResetWindow();
                    _latestEncoderSnapshot = snapshot;
                    Log($"ENCODER {_connectionId}", $"stats generated={snapshot.FramesGenerated} encoded={snapshot.FramesEncoded} sent={snapshot.FramesSent} dropped={snapshot.FramesDropped} late={snapshot.LateFrames} streamFps={snapshot.StreamFps:F1} localLatencyP95={snapshot.P95LocalLatencyMs:F1}ms new={snapshot.NewFramesSent} repeat={snapshot.RepeatFramesSent} avgFrameInterval={snapshot.P50FrameIntervalMs:F2}/{snapshot.P95FrameIntervalMs:F2}/{snapshot.P99FrameIntervalMs:F2}ms avgEncode={snapshot.AvgEncodeMs:F1}ms p95Encode={snapshot.P95EncodeMs:F1}ms p99Encode={snapshot.P99EncodeMs:F1}ms avgSend={snapshot.AvgSendMs:F1}ms p95Send={snapshot.P95SendMs:F1}ms kbps={snapshot.OutputKbps:F0}");
                    var payload = CreateEncoderStatsPayload(snapshot, gpuPath: false);
                    AddEncoderTuningPayload(payload, _options);
                    payload["source"] = "synthetic-nv12";
                    payload["inputFormat"] = "nv12";
                    payload["synthetic"] = true;
                    await _controlPublisher.PublishAsync("encoder_stats", payload, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
        }

        private Task PublishErrorAsync(string code, string message, CancellationToken cancellationToken)
        {
            Log($"ENCODER {_connectionId}", $"{code}: {message}");
            return _controlPublisher.PublishAsync("encoder_error", new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["source"] = "synthetic-nv12"
            }, cancellationToken).AsTask();
        }

        private static CaptureStatsSnapshot CreateEmptyCaptureSnapshot()
        {
            return new CaptureStatsSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        private static RealtimeEncoderStatsSnapshot CreateEmptyEncoderSnapshot()
        {
            return new RealtimeEncoderStatsSnapshot(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    private sealed class SyntheticNv12FramePool
    {
        private static readonly YuvColor White = FromRgb(255, 255, 255);
        private static readonly YuvColor Yellow = FromRgb(255, 255, 0);
        private static readonly YuvColor Cyan = FromRgb(0, 255, 255);
        private static readonly YuvColor Green = FromRgb(0, 255, 0);
        private static readonly YuvColor Magenta = FromRgb(255, 0, 255);
        private static readonly YuvColor Red = FromRgb(255, 0, 0);
        private static readonly YuvColor Blue = FromRgb(0, 0, 255);
        private static readonly YuvColor Black = FromRgb(0, 0, 0);
        private static readonly YuvColor Orange = FromRgb(255, 160, 32);
        private static readonly YuvColor Teal = FromRgb(32, 220, 220);
        private static readonly YuvColor DarkGray = FromRgb(32, 32, 32);
        private static readonly YuvColor[] Bars = [White, Yellow, Cyan, Green, Magenta, Red, Blue, Black];

        private readonly int _width;
        private readonly int _height;
        private readonly int _yPlaneSize;
        private readonly byte[][] _frames;

        public SyntheticNv12FramePool(int width, int height, int poolSize)
        {
            if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
            {
                throw new ArgumentException("synthetic-nv12 requires positive even dimensions.");
            }

            _width = width;
            _height = height;
            _yPlaneSize = checked(width * height);
            var frameSize = checked(width * height * 3 / 2);
            _frames = new byte[Math.Max(1, poolSize)][];
            for (var index = 0; index < _frames.Length; index++)
            {
                var frame = new byte[frameSize];
                RenderFrame(frame, index);
                _frames[index] = frame;
            }
        }

        public byte[] GetFrame(long frameId)
        {
            return _frames[(int)(frameId % _frames.Length)];
        }

        private void RenderFrame(byte[] nv12, int variant)
        {
            FillBackground(nv12);
            DrawColorBars(nv12);
            DrawMovingBlocks(nv12, variant);
            DrawFlashMarker(nv12, variant);
            DrawFrameCode(nv12, variant);
        }

        private void FillBackground(byte[] nv12)
        {
            for (var y = 0; y < _height; y++)
            {
                var stripe = (y / 24) % 2 == 0 ? 10 : 0;
                var rowOffset = y * _width;
                for (var x = 0; x < _width; x++)
                {
                    var bar = Bars[(x * Bars.Length) / _width];
                    nv12[rowOffset + x] = ClampToByte(bar.Y / 3 + 32 + stripe);
                }
            }

            for (var uvY = 0; uvY < _height / 2; uvY++)
            {
                var rowOffset = _yPlaneSize + uvY * _width;
                for (var x = 0; x < _width; x += 2)
                {
                    var bar = Bars[(x * Bars.Length) / _width];
                    nv12[rowOffset + x] = AverageChroma(bar.U, 128);
                    nv12[rowOffset + x + 1] = AverageChroma(bar.V, 128);
                }
            }
        }

        private void DrawColorBars(byte[] nv12)
        {
            var top = _height / 3;
            var barHeight = Math.Max(60, _height / 6);
            for (var index = 0; index < Bars.Length; index++)
            {
                var x0 = index * _width / Bars.Length;
                var x1 = (index + 1) * _width / Bars.Length;
                FillRect(nv12, x0, top, x1 - x0, barHeight, Bars[index]);
            }
        }

        private void DrawMovingBlocks(byte[] nv12, int variant)
        {
            var blockWidth = MakeEven(Math.Max(96, _width / 10));
            var blockHeight = MakeEven(Math.Max(54, _height / 12));
            var horizontalRange = Math.Max(1, _width - blockWidth);
            var verticalRange = Math.Max(1, _height - blockHeight - 40);
            var x = (variant * Math.Max(32, _width / 5)) % horizontalRange;
            var y = 96 + (variant * Math.Max(24, _height / 13)) % Math.Max(1, _height / 5);
            FillRect(nv12, x, y, blockWidth, blockHeight, Orange);

            var vx = Math.Max(0, _width - blockWidth - 48);
            var vy = 40 + (variant * Math.Max(30, _height / 9)) % verticalRange;
            FillRect(nv12, vx, vy, blockWidth, blockHeight, Teal);
        }

        private void DrawFlashMarker(byte[] nv12, int variant)
        {
            var markerSize = MakeEven(Math.Max(72, _height / 9));
            FillRect(
                nv12,
                48,
                Math.Max(0, _height - markerSize - 48),
                markerSize,
                markerSize,
                (variant & 1) == 0 ? Green : DarkGray);
        }

        private void DrawFrameCode(byte[] nv12, int variant)
        {
            var cell = MakeEven(Math.Max(10, _width / 128));
            var x = 48;
            var y = 36;
            DrawBits(nv12, x, y, cell, unchecked((uint)(0x5A17_0000 | variant)));
            DrawBits(nv12, x, y + cell * 6, cell, unchecked((uint)(_frames.Length << 8 | variant)));
        }

        private void DrawBits(byte[] nv12, int x, int y, int cell, uint value)
        {
            for (var bit = 0; bit < 32; bit++)
            {
                var on = ((value >> bit) & 1) != 0;
                FillRect(
                    nv12,
                    x + bit * (cell + 2),
                    y,
                    cell,
                    cell * 4,
                    on ? White : Black);
            }
        }

        private void FillRect(byte[] nv12, int x, int y, int width, int height, YuvColor color)
        {
            var x0 = Math.Clamp(x, 0, _width);
            var y0 = Math.Clamp(y, 0, _height);
            var x1 = Math.Clamp(x + width, 0, _width);
            var y1 = Math.Clamp(y + height, 0, _height);
            if (x1 <= x0 || y1 <= y0)
            {
                return;
            }

            for (var row = y0; row < y1; row++)
            {
                nv12.AsSpan(row * _width + x0, x1 - x0).Fill(color.Y);
            }

            var uvX0 = x0 & ~1;
            var uvX1 = Math.Min(_width, (x1 + 1) & ~1);
            var uvY0 = y0 / 2;
            var uvY1 = Math.Min(_height / 2, (y1 + 1) / 2);
            for (var row = uvY0; row < uvY1; row++)
            {
                var offset = _yPlaneSize + row * _width + uvX0;
                for (var col = uvX0; col < uvX1; col += 2)
                {
                    nv12[offset] = color.U;
                    nv12[offset + 1] = color.V;
                    offset += 2;
                }
            }
        }

        private static YuvColor FromRgb(byte r, byte g, byte b)
        {
            return new YuvColor(
                ClampToByte(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16),
                ClampToByte(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128),
                ClampToByte(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128));
        }

        private static byte AverageChroma(byte value, byte neutral)
        {
            return (byte)((value + neutral) / 2);
        }

        private static int MakeEven(int value)
        {
            return Math.Max(2, value & ~1);
        }

        private static byte ClampToByte(int value)
        {
            return (byte)Math.Clamp(value, 0, 255);
        }

        private readonly record struct YuvColor(byte Y, byte U, byte V);
    }

    private sealed class MediaFoundationBgraVideoSource : IEncodedVideoSource, IVideoSendStatsSink, IAdaptiveVideoStatsSource
    {
        private readonly int _connectionId;
        private readonly HostOptions _options;
        private readonly ControlMessagePublisher _controlPublisher;
        private readonly IBgraFrameSource _frameSource;
        private readonly RealtimeEncoderStats _stats;
        private readonly CaptureStats _captureStats = new();
        private readonly PipelineStageStats _acquireStageStats = new("acquire");
        private readonly PipelineStageStats _convertStageStats = new("convert");
        private readonly PipelineStageStats _encodeStageStats = new("encode");
        private CaptureStatsSnapshot _latestCaptureSnapshot = CreateEmptyCaptureSnapshot();
        private RealtimeEncoderStatsSnapshot _latestEncoderSnapshot = CreateEmptyEncoderSnapshot();
        private readonly LatestFrameQueue<BgraFrame> _capturedFrames;
        private readonly LatestFrameQueue<Nv12Frame> _convertedFrames;
        private readonly LatestFrameQueue<EncodedVideoPacket> _packets;
        private readonly OverviewPreviewFramePublisher _previewPublisher;
        private CancellationTokenSource? _sourceCts;
        private Task? _captureTask;
        private Task? _convertTask;
        private Task? _encoderTask;
        private Task? _statsTask;
        private Task? _captureStatsTask;
        private bool _encoderStarted;
        private bool _captureStarted;
        private long _sequence;

        public MediaFoundationBgraVideoSource(
            int connectionId,
            HostOptions options,
            ControlMessagePublisher controlPublisher,
            IBgraFrameSource frameSource)
        {
            _connectionId = connectionId;
            _options = options;
            _controlPublisher = controlPublisher;
            _frameSource = frameSource;
            _stats = new RealtimeEncoderStats(options.VideoFps);
            var queueCapacity = Math.Max(1, options.Nv12PoolSize);
            var packetQueueCapacity = Math.Max(1, options.EncodedPacketQueue);
            _capturedFrames = new LatestFrameQueue<BgraFrame>(queueCapacity, disposeDropped: frame => frame.Dispose());
            _convertedFrames = new LatestFrameQueue<Nv12Frame>(queueCapacity, disposeDropped: frame => frame.Dispose());
            _packets = new LatestFrameQueue<EncodedVideoPacket>(packetQueueCapacity);
            _previewPublisher = new OverviewPreviewFramePublisher(message => Log($"PREVIEW {connectionId}", message));
        }

        public async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                await PublishErrorAsync("ENCODER_INIT_FAILED", "Media Foundation encoder is only available on Windows.", cancellationToken);
                throw new PlatformNotSupportedException("Media Foundation encoder is only available on Windows.");
            }

            _sourceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                _frameSource.Start(cancellationToken);
            }
            catch (CaptureException ex)
            {
                await PublishCaptureErrorAsync(ex.Code, ex.Message, cancellationToken);
                throw;
            }
            catch (Exception ex)
            {
                await PublishCaptureErrorAsync("CAPTURE_INIT_FAILED", ex.Message, cancellationToken);
                throw;
            }

            _captureTask = Task.Run(
                () =>
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        throw new PlatformNotSupportedException("Media Foundation capture is only available on Windows.");
                    }

                    return CaptureLoopAsync(_sourceCts.Token);
                },
                _sourceCts.Token);
            _convertTask = Task.Run(
                () =>
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        throw new PlatformNotSupportedException("Media Foundation conversion is only available on Windows.");
                    }

                    return ConvertLoopAsync(_sourceCts.Token);
                },
                _sourceCts.Token);
            _encoderTask = Task.Run(
                () =>
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        throw new PlatformNotSupportedException("Media Foundation encoder is only available on Windows.");
                    }

                    return EncodeLoopAsync(_sourceCts.Token);
                },
                _sourceCts.Token);
            _statsTask = Task.Run(() => PublishStatsLoopAsync(_sourceCts.Token), _sourceCts.Token);
            _captureStatsTask = Task.Run(() => PublishCaptureStatsLoopAsync(_sourceCts.Token), _sourceCts.Token);

            Log($"ENCODER {_connectionId}", $"start source={_frameSource.SourceName} encoder=mediafoundation {_options.VideoWidth}x{_options.VideoHeight}@{_options.VideoFps} {FormatEncoderTuningForLog(_options)}");
            var encoderStartPayload = new JsonObject
            {
                ["source"] = _frameSource.SourceName,
                ["encoder"] = "mediafoundation",
                ["width"] = _options.VideoWidth,
                ["height"] = _options.VideoHeight,
                ["fps"] = _options.VideoFps,
                ["codec"] = "h264",
                ["format"] = "annexb"
            };
            AddEncoderTuningPayload(encoderStartPayload, _options);
            await _controlPublisher.PublishAsync("encoder_start", encoderStartPayload, cancellationToken);
            _encoderStarted = true;

            if (_options.VideoSource is VideoSourceKind.Window or VideoSourceKind.Region or VideoSourceKind.Idd)
            {
                await _controlPublisher.PublishAsync("capture_start", new JsonObject
                {
                    ["source"] = _frameSource.SourceName,
                    ["width"] = _options.VideoWidth,
                    ["height"] = _options.VideoHeight,
                    ["fps"] = _options.VideoFps,
                    ["target"] = _frameSource.SourceDescription
                }, cancellationToken);
                _captureStarted = true;
            }
        }

        public async IAsyncEnumerable<EncodedVideoPacket> ReadPacketsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (await _packets.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_packets.Reader.TryRead(out var packet))
                {
                    yield return packet;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_sourceCts is not null)
            {
                await _sourceCts.CancelAsync();
            }

            await WhenAllIgnoringCancellation(_captureTask, _convertTask, _encoderTask, _statsTask, _captureStatsTask);
            _packets.Complete();
            _previewPublisher.Dispose();
            _frameSource.Dispose();

            var snapshot = _stats.Snapshot();
            if (_encoderStarted)
            {
                Log($"ENCODER {_connectionId}", $"stop generated={snapshot.FramesGenerated} encoded={snapshot.FramesEncoded} sent={snapshot.FramesSent} dropped={snapshot.FramesDropped}");
                await _controlPublisher.PublishAsync("encoder_stop", new JsonObject
                {
                    ["reason"] = "video_client_disconnected",
                    ["framesGenerated"] = snapshot.FramesGenerated,
                    ["framesEncoded"] = snapshot.FramesEncoded,
                    ["framesSent"] = snapshot.FramesSent
                }, CancellationToken.None);
            }

            if (_captureStarted)
            {
                await _controlPublisher.PublishAsync("capture_stop", new JsonObject
                {
                    ["reason"] = "video_client_disconnected",
                    ["source"] = _frameSource.SourceName
                }, CancellationToken.None);
            }

            _sourceCts?.Dispose();
        }

        public void RecordSent(EncodedVideoPacket packet, double sendMs)
        {
            _stats.RecordSent(packet, sendMs);
        }

        public void RecordDropped(long count)
        {
            _stats.RecordDropped(count);
        }

        public CaptureStatsSnapshot LatestCaptureSnapshot => _latestCaptureSnapshot;

        public RealtimeEncoderStatsSnapshot LatestEncoderSnapshot => _latestEncoderSnapshot;

        [SupportedOSPlatform("windows")]
        private async Task CaptureLoopAsync(CancellationToken cancellationToken)
        {
            var frameLength = checked(_options.VideoWidth * _options.VideoHeight * 4);
            var pacer = new FramePacer(
                _options.VideoFps,
                _stats.RecordFrameInterval,
                _stats.RecordLateFrame,
                () => _stats.RecordDropped());
            long frameId = 0;

            try
            {
                await Task.Yield();

                while (!cancellationToken.IsCancellationRequested)
                {
                    var generatedAt = pacer.MarkFrameStart();
                    var bgraFrame = ArrayPool<byte>.Shared.Rent(frameLength);
                    var captureStopwatch = Stopwatch.StartNew();
                    try
                    {
                        _frameSource.Capture(bgraFrame, _options.VideoWidth, _options.VideoHeight, cancellationToken);
                        captureStopwatch.Stop();
                        _captureStats.RecordCaptured(captureStopwatch.Elapsed.TotalMilliseconds);
                        _acquireStageStats.RecordProcessed(captureStopwatch.Elapsed.TotalMilliseconds);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(bgraFrame);
                        throw;
                    }

                    var timestampMs = generatedAt.ToUnixTimeMilliseconds();
                    _previewPublisher.TryPublishBgra(
                        bgraFrame,
                        _options.VideoWidth,
                        _options.VideoHeight,
                        _options.VideoWidth * 4,
                        frameId,
                        timestampMs);
                    _stats.RecordGenerated(frameId, timestampMs);
                    var dropped = _capturedFrames.WriteLatest(new BgraFrame(bgraFrame, frameId, timestampMs));
                    if (dropped > 0)
                    {
                        _acquireStageStats.RecordDropped(dropped);
                        _stats.RecordDropped(dropped);
                    }

                    frameId++;
                    await pacer.WaitForNextFrameAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
            catch (CaptureException ex)
            {
                _captureStats.RecordError();
                await PublishCaptureErrorAsync(ex.Code, ex.Message, CancellationToken.None);
                _capturedFrames.Complete(ex);
                return;
            }
            catch (Exception ex)
            {
                _captureStats.RecordError();
                await PublishCaptureErrorAsync("CAPTURE_FAILED", ex.Message, CancellationToken.None);
                _capturedFrames.Complete(ex);
                return;
            }

            _capturedFrames.Complete();
        }

        [SupportedOSPlatform("windows")]
        private async Task ConvertLoopAsync(CancellationToken cancellationToken)
        {
            var nv12Length = MediaFoundationH264Encoder.GetNv12FrameSize(_options.VideoWidth, _options.VideoHeight);

            try
            {
                await Task.Yield();
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("Media Foundation encoder is only available on Windows.");
                }

                await foreach (var bgraFrame in _capturedFrames.Reader.ReadAllAsync(cancellationToken))
                {
                    byte[]? nv12Frame = null;
                    try
                    {
                        nv12Frame = ArrayPool<byte>.Shared.Rent(nv12Length);
                        var convertStopwatch = Stopwatch.StartNew();
                        MediaFoundationH264Encoder.ConvertBgraToNv12(bgraFrame.Data, nv12Frame, _options.VideoWidth, _options.VideoHeight);
                        convertStopwatch.Stop();
                        _captureStats.RecordConverted(convertStopwatch.Elapsed.TotalMilliseconds);
                        _convertStageStats.RecordProcessed(convertStopwatch.Elapsed.TotalMilliseconds);

                        var converted = new Nv12Frame(
                            nv12Frame,
                            bgraFrame.FrameId,
                            EncodedFrameKind.New,
                            bgraFrame.FrameId,
                            0,
                            bgraFrame.TimestampMs,
                            returnToPool: true);
                        nv12Frame = null;

                        var dropped = _convertedFrames.WriteLatest(converted);
                        if (dropped > 0)
                        {
                            _convertStageStats.RecordDropped(dropped);
                            _stats.RecordDropped(dropped);
                        }
                    }
                    finally
                    {
                        if (nv12Frame is not null)
                        {
                            ArrayPool<byte>.Shared.Return(nv12Frame);
                        }

                        bgraFrame.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
            catch (Exception ex)
            {
                _captureStats.RecordError();
                await PublishErrorAsync("CONVERT_FAILED", ex.Message, CancellationToken.None);
                _convertedFrames.Complete(ex);
                return;
            }

            _convertedFrames.Complete();
        }

        [SupportedOSPlatform("windows")]
        private async Task EncodeLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Yield();
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("Media Foundation encoder is only available on Windows.");
                }

                using var encoder = new MediaFoundationH264Encoder(_options);
                encoder.Start();

                await foreach (var frame in _convertedFrames.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        var encodeStopwatch = Stopwatch.StartNew();
                        var payloads = encoder.EncodeFrame(frame.Data, frame.SourceSequence);
                        encodeStopwatch.Stop();
                        _encodeStageStats.RecordProcessed(encodeStopwatch.Elapsed.TotalMilliseconds);

                        foreach (var payload in payloads)
                        {
                            var packet = CreatePacket(
                                payload,
                                frame.FrameKind,
                                frame.FrameId,
                                frame.SourceSequence,
                                frame.SourceAgeMs,
                                encodeStopwatch.Elapsed.TotalMilliseconds);
                            var dropped = _packets.WriteLatest(packet);
                            if (dropped > 0)
                            {
                                _encodeStageStats.RecordDropped(dropped);
                                _stats.RecordDropped(dropped);
                            }
                        }
                    }
                    finally
                    {
                        frame.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
            catch (Exception ex)
            {
                await PublishErrorAsync("ENCODER_FAILED", ex.Message, CancellationToken.None);
                _packets.Complete(ex);
                return;
            }

            _packets.Complete();
        }

        private EncodedVideoPacket CreatePacket(byte[] payload, EncodedFrameKind frameKind, long frameId, long sourceSequence, double sourceAgeMs, double encodeMs)
        {
            var containsPicture = H264AnnexBStream.ContainsPicture(payload);
            var isKeyFrame = H264AnnexBStream.ContainsNalType(payload, 5);
            var encodedFrameId = containsPicture
                ? _stats.RecordEncodedFrame(frameId, payload.Length, encodeMs)
                : _stats.LastEncodedFrameId;

            return new EncodedVideoPacket(
                payload,
                isKeyFrame,
                containsPicture,
                encodedFrameId,
                Interlocked.Increment(ref _sequence) - 1,
                _stats.GetFrameTimestamp(encodedFrameId),
                _stats.GetFrameEncodeMs(encodedFrameId),
                frameKind,
                sourceSequence,
                sourceAgeMs);
        }

        private async Task PublishStatsLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    var snapshot = _stats.SnapshotAndResetWindow();
                    _latestEncoderSnapshot = snapshot;
                    Log($"ENCODER {_connectionId}", $"stats generated={snapshot.FramesGenerated} encoded={snapshot.FramesEncoded} sent={snapshot.FramesSent} dropped={snapshot.FramesDropped} late={snapshot.LateFrames} streamFps={snapshot.StreamFps:F1} localLatencyP95={snapshot.P95LocalLatencyMs:F1}ms new={snapshot.NewFramesSent} repeat={snapshot.RepeatFramesSent} avgFrameInterval={snapshot.P50FrameIntervalMs:F2}/{snapshot.P95FrameIntervalMs:F2}/{snapshot.P99FrameIntervalMs:F2}ms avgEncode={snapshot.AvgEncodeMs:F1}ms p95Encode={snapshot.P95EncodeMs:F1}ms p99Encode={snapshot.P99EncodeMs:F1}ms avgSend={snapshot.AvgSendMs:F1}ms p95Send={snapshot.P95SendMs:F1}ms kbps={snapshot.OutputKbps:F0}");
                    var payload = CreateEncoderStatsPayload(snapshot, gpuPath: false);
                    AddEncoderTuningPayload(payload, _options);
                    await _controlPublisher.PublishAsync("encoder_stats", payload, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
        }

        private async Task PublishCaptureStatsLoopAsync(CancellationToken cancellationToken)
        {
            if (_options.VideoSource is not (VideoSourceKind.Window or VideoSourceKind.Region or VideoSourceKind.Idd))
            {
                return;
            }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    var snapshot = _captureStats.SnapshotAndResetWindow();
                    _latestCaptureSnapshot = snapshot;
                    Log($"CAPTURE {_connectionId}", $"stats captured={snapshot.FramesCaptured} converted={snapshot.FramesConverted} errors={snapshot.CaptureErrors} captureFps={snapshot.CaptureFps:F1} convertFps={snapshot.ConvertFps:F1} avgCapture={snapshot.AvgCaptureMs:F1}ms p95Capture={snapshot.P95CaptureMs:F1}ms avgConvert={snapshot.AvgConvertMs:F1}ms p95Convert={snapshot.P95ConvertMs:F1}ms");
                    var payload = new JsonObject
                    {
                        ["source"] = _frameSource.SourceName,
                        ["framesCaptured"] = snapshot.FramesCaptured,
                        ["framesConverted"] = snapshot.FramesConverted,
                        ["captureErrors"] = snapshot.CaptureErrors,
                        ["avgCaptureMs"] = Math.Round(snapshot.AvgCaptureMs, 2),
                        ["avgConvertMs"] = Math.Round(snapshot.AvgConvertMs, 2)
                    };
                    AddCapturePercentiles(payload, snapshot);

                    if (_frameSource is IddBgraFrameSource iddFrameSource)
                    {
                        payload["framesDropped"] = iddFrameSource.FramesDropped;
                        payload["lastFrameAgeMs"] = Math.Round(iddFrameSource.LastFrameAgeMs, 0);
                    }

                    await _controlPublisher.PublishAsync("capture_stats", payload, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
        }

        private Task PublishErrorAsync(string code, string message, CancellationToken cancellationToken)
        {
            Log($"ENCODER {_connectionId}", $"{code}: {message}");
            return _controlPublisher.PublishAsync("encoder_error", new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }, cancellationToken).AsTask();
        }

        private Task PublishCaptureErrorAsync(string code, string message, CancellationToken cancellationToken)
        {
            Log($"CAPTURE {_connectionId}", $"{code}: {message}");
            return _controlPublisher.PublishAsync("capture_error", new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }, cancellationToken).AsTask();
        }

        private static CaptureStatsSnapshot CreateEmptyCaptureSnapshot()
        {
            return new CaptureStatsSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        private static RealtimeEncoderStatsSnapshot CreateEmptyEncoderSnapshot()
        {
            return new RealtimeEncoderStatsSnapshot(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        public static async Task WhenAllIgnoringCancellation(params Task?[] tasks)
        {
            foreach (var task in tasks)
            {
                if (task is null)
                {
                    continue;
                }

                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                    // Expected during teardown.
                }
                catch (Exception ex)
                {
                    Log("ENCODER", $"后台任务结束异常: {ex.Message}");
                }
            }
        }
    }

    private sealed class MediaFoundationGpuVideoSource : IEncodedVideoSource, IVideoSendStatsSink, IAdaptiveVideoStatsSource
    {
        private readonly int _connectionId;
        private readonly HostOptions _options;
        private readonly ControlMessagePublisher _controlPublisher;
        private readonly IGpuFrameSource _frameSource;
        private readonly RealtimeEncoderStats _stats;
        private readonly CaptureStats _captureStats = new();
        private readonly PipelineStageStats _acquireStageStats = new("acquire");
        private readonly PipelineStageStats _convertStageStats = new("convert");
        private readonly PipelineStageStats _encodeStageStats = new("encode");
        private CaptureStatsSnapshot _latestCaptureSnapshot = CreateEmptyCaptureSnapshot();
        private RealtimeEncoderStatsSnapshot _latestEncoderSnapshot = CreateEmptyEncoderSnapshot();
        private readonly LatestFrameQueue<EncodedVideoPacket> _packets;
        private readonly LatestFrameQueue<GpuAcquiredFrame> _gpuFrames;
        private readonly LatestFrameQueue<GpuConvertedFrame> _gpuConvertedFrames;
        private readonly LatestFrameQueue<Nv12Frame> _nv12Frames;
        private readonly OverviewPreviewFramePublisher _previewPublisher;
        private CancellationTokenSource? _sourceCts;
        private Task? _captureTask;
        private Task? _convertTask;
        private Task? _encoderTask;
        private Task? _statsTask;
        private Task? _captureStatsTask;
        private bool _encoderStarted;
        private bool _captureStarted;
        private GpuBgraToNv12Converter? _converter;
        private MediaFoundationH264Encoder? _encoder;
        private GpuNv12Readback? _nv12Readback;
        private bool _encoderUsesD3DInput;
        private string? _encoderFallbackReason;
        private string? _encoderMftName;
        private long _sequence;
        private int _gpuFrameDumped;
        private readonly object _encoderLock = new();
        private int _encoderWatchdogStrikeCount;
        private const string DirectEncoderFallback = "none";
        private const string ReadbackEncoderFallback = "gpu-convert-nv12-readback";
        private const int EncoderWatchdogStrikeThreshold = 2;

        public MediaFoundationGpuVideoSource(
            int connectionId,
            HostOptions options,
            ControlMessagePublisher controlPublisher,
            IGpuFrameSource frameSource)
        {
            _connectionId = connectionId;
            _options = options;
            _controlPublisher = controlPublisher;
            _frameSource = frameSource;
            _stats = new RealtimeEncoderStats(options.VideoFps);
            var queueCapacity = Math.Max(1, options.Nv12PoolSize);
            var packetQueueCapacity = Math.Max(1, options.EncodedPacketQueue);
            _packets = new LatestFrameQueue<EncodedVideoPacket>(packetQueueCapacity);
            _gpuFrames = new LatestFrameQueue<GpuAcquiredFrame>(queueCapacity, disposeDropped: frame => frame.Dispose());
            _gpuConvertedFrames = new LatestFrameQueue<GpuConvertedFrame>(queueCapacity, disposeDropped: frame => frame.Dispose());
            _nv12Frames = new LatestFrameQueue<Nv12Frame>(queueCapacity, disposeDropped: frame => frame.Dispose());
            _previewPublisher = new OverviewPreviewFramePublisher(message => Log($"PREVIEW {connectionId}", message));
        }

        public async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                await PublishErrorAsync("ENCODER_INIT_FAILED", "Media Foundation GPU encoder is only available on Windows.", cancellationToken);
                throw new PlatformNotSupportedException("Media Foundation GPU encoder is only available on Windows.");
            }

            _sourceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                _frameSource.Start(cancellationToken);
                var probe = ProbeD3D11EncoderInput(_options, _frameSource.Device);
                _encoderUsesD3DInput = probe.SupportsDirectInput;
                _encoderFallbackReason = probe.FallbackReason;
                _encoderMftName = probe.EncoderMftName;
                _converter = new GpuBgraToNv12Converter(_frameSource.Device, _frameSource.Context, _options.VideoWidth, _options.VideoHeight, _options.VideoFps, _options.Nv12PoolSize);

                if (_encoderUsesD3DInput)
                {
                    try
                    {
                        _encoder = CreateStartedEncoder(d3dInput: true);
                        _encoderMftName = _encoder.SelectedMftName;
                        Log($"ENCODER {_connectionId}", $"D3D11 input probe supported=true fallback=none mft={_encoderMftName}");
                    }
                    catch (Exception ex) when (IsD3D11EncoderInputFailure(ex))
                    {
                        _encoder?.Dispose();
                        _encoder = null;
                        _encoderUsesD3DInput = false;
                        _encoderFallbackReason = FormatExceptionReason(ex);
                        Log($"ENCODER {_connectionId}", $"D3D11 input probe passed but active encoder init failed; fallback={ReadbackEncoderFallback} reason={_encoderFallbackReason}");
                    }
                }

                if (!_encoderUsesD3DInput)
                {
                    if (!string.IsNullOrWhiteSpace(_encoderFallbackReason))
                    {
                        Log($"ENCODER {_connectionId}", $"D3D11 input probe supported=false fallback={ReadbackEncoderFallback} reason={_encoderFallbackReason}");
                    }

                    _encoder = CreateStartedEncoder(d3dInput: false);
                    _encoderMftName = _encoder.SelectedMftName;
                    _nv12Readback = new GpuNv12Readback(_frameSource.Device, _frameSource.Context, _options.VideoWidth, _options.VideoHeight);
                }
            }
            catch (CaptureException ex)
            {
                await PublishCaptureErrorAsync(ex.Code, ex.Message, cancellationToken);
                throw;
            }
            catch (Exception ex)
            {
                await PublishCaptureErrorAsync("GPU_CAPTURE_INIT_FAILED", ex.Message, cancellationToken);
                throw;
            }

            if (_encoderUsesD3DInput)
            {
                _captureTask = Task.Run(
                    () =>
                    {
                        if (!OperatingSystem.IsWindows())
                        {
                            throw new PlatformNotSupportedException("Media Foundation GPU capture is only available on Windows.");
                        }

                        return CaptureLoopAsync(_sourceCts.Token);
                    },
                    _sourceCts.Token);
                _convertTask = Task.Run(
                    () =>
                    {
                        if (!OperatingSystem.IsWindows())
                        {
                            throw new PlatformNotSupportedException("Media Foundation GPU capture is only available on Windows.");
                        }

                        return ConvertGpuTextureLoopAsync(_sourceCts.Token);
                    },
                    _sourceCts.Token);
                _encoderTask = Task.Run(
                    () =>
                    {
                        if (!OperatingSystem.IsWindows())
                        {
                            throw new PlatformNotSupportedException("Media Foundation GPU encoder is only available on Windows.");
                        }

                        return EncodeLoopAsync(_sourceCts.Token);
                    },
                    _sourceCts.Token);
            }
            else
            {
                _captureTask = Task.Run(
                    () =>
                    {
                        if (!OperatingSystem.IsWindows())
                        {
                            throw new PlatformNotSupportedException("Media Foundation GPU capture is only available on Windows.");
                        }

                        return CaptureLoopAsync(_sourceCts.Token);
                    },
                    _sourceCts.Token);
                _convertTask = Task.Run(
                    () =>
                    {
                        if (!OperatingSystem.IsWindows())
                        {
                            throw new PlatformNotSupportedException("Media Foundation GPU capture is only available on Windows.");
                        }

                        return ConvertReadbackLoopAsync(_sourceCts.Token);
                    },
                    _sourceCts.Token);
                _encoderTask = Task.Run(() => EncodeReadbackLoopAsync(_sourceCts.Token), _sourceCts.Token);
            }
            _statsTask = Task.Run(() => PublishStatsLoopAsync(_sourceCts.Token), _sourceCts.Token);
            _captureStatsTask = Task.Run(() => PublishCaptureStatsLoopAsync(_sourceCts.Token), _sourceCts.Token);

            var encoderName = _encoderUsesD3DInput
                ? "mediafoundation-d3d11"
                : "mediafoundation-nv12-readback";
            Log($"ENCODER {_connectionId}", $"start source={_frameSource.SourceName} encoder={encoderName} mft={_encoderMftName ?? "unknown"} input={EncoderInputFormat} fallback={EncoderFallbackName}{FallbackReasonForLog} {_options.VideoWidth}x{_options.VideoHeight}@{_options.VideoFps} {FormatEncoderTuningForLog(_options)}");
            var encoderStartPayload = new JsonObject
            {
                ["source"] = _frameSource.SourceName,
                ["encoder"] = encoderName,
                ["encoderMft"] = _encoderMftName ?? string.Empty,
                ["width"] = _options.VideoWidth,
                ["height"] = _options.VideoHeight,
                ["fps"] = _options.VideoFps,
                ["codec"] = "h264",
                ["format"] = "annexb",
                ["gpuPath"] = true,
                ["inputFormat"] = EncoderInputFormat,
                ["directD3DInput"] = _encoderUsesD3DInput,
                ["fallback"] = EncoderFallbackName,
                ["driverGpuRingSlots"] = _frameSource.SlotCount
            };
            AddEncoderTuningPayload(encoderStartPayload, _options);
            AddFallbackReason(encoderStartPayload);
            await _controlPublisher.PublishAsync("encoder_start", encoderStartPayload, cancellationToken);
            _encoderStarted = true;

            var captureStartPayload = new JsonObject
            {
                ["source"] = _frameSource.SourceName,
                ["width"] = _options.VideoWidth,
                ["height"] = _options.VideoHeight,
                ["fps"] = _options.VideoFps,
                ["target"] = _frameSource.SourceDescription,
                ["gpuPath"] = true,
                ["directD3DInput"] = _encoderUsesD3DInput,
                ["fallback"] = EncoderFallbackName,
                ["driverGpuRingSlots"] = _frameSource.SlotCount
            };
            AddFallbackReason(captureStartPayload);
            await _controlPublisher.PublishAsync("capture_start", captureStartPayload, cancellationToken);
            _captureStarted = true;
        }

        public async IAsyncEnumerable<EncodedVideoPacket> ReadPacketsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (await _packets.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_packets.Reader.TryRead(out var packet))
                {
                    yield return packet;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_sourceCts is not null)
            {
                await _sourceCts.CancelAsync();
            }

            await MediaFoundationBgraVideoSource.WhenAllIgnoringCancellation(_captureTask, _convertTask, _encoderTask, _statsTask, _captureStatsTask);
            _packets.Complete();
            if (OperatingSystem.IsWindows())
            {
                _previewPublisher.Dispose();
                _encoder?.Dispose();
                _converter?.Dispose();
                _nv12Readback?.Dispose();
            }

            _encoder = null;
            _converter = null;
            _nv12Readback = null;
            _frameSource.Dispose();

            var snapshot = _stats.Snapshot();
            if (_encoderStarted)
            {
                Log($"ENCODER {_connectionId}", $"stop generated={snapshot.FramesGenerated} encoded={snapshot.FramesEncoded} sent={snapshot.FramesSent} dropped={snapshot.FramesDropped}");
                await _controlPublisher.PublishAsync("encoder_stop", new JsonObject
                {
                    ["reason"] = "video_client_disconnected",
                    ["framesGenerated"] = snapshot.FramesGenerated,
                    ["framesEncoded"] = snapshot.FramesEncoded,
                    ["framesSent"] = snapshot.FramesSent,
                    ["fallback"] = EncoderFallbackName
                }, CancellationToken.None);
            }

            if (_captureStarted)
            {
                await _controlPublisher.PublishAsync("capture_stop", new JsonObject
                {
                    ["reason"] = "video_client_disconnected",
                    ["source"] = _frameSource.SourceName,
                    ["gpuPath"] = true,
                    ["fallback"] = EncoderFallbackName
                }, CancellationToken.None);
            }

            _sourceCts?.Dispose();
        }

        public void RecordSent(EncodedVideoPacket packet, double sendMs)
        {
            _stats.RecordSent(packet, sendMs);
        }

        public void RecordDropped(long count)
        {
            _stats.RecordDropped(count);
        }

        public CaptureStatsSnapshot LatestCaptureSnapshot => _latestCaptureSnapshot;

        public RealtimeEncoderStatsSnapshot LatestEncoderSnapshot => _latestEncoderSnapshot;

        [SupportedOSPlatform("windows")]
        private MediaFoundationH264Encoder CreateStartedEncoder(bool d3dInput)
        {
            var encoder = d3dInput
                ? new MediaFoundationH264Encoder(_options, _frameSource.Device)
                : new MediaFoundationH264Encoder(_options);
            encoder.Start();
            return encoder;
        }

        [SupportedOSPlatform("windows")]
        private void RestartEncoder(string reason)
        {
            lock (_encoderLock)
            {
                var previous = _encoder;
                var replacement = CreateStartedEncoder(_encoderUsesD3DInput);
                _encoder = replacement;
                _encoderMftName = replacement.SelectedMftName;
                previous?.Dispose();
                _encoderWatchdogStrikeCount = 0;
                Log($"ENCODER {_connectionId}", $"watchdog restarted encoder reason={reason} mft={_encoderMftName}");
            }
        }

        private async Task CaptureLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Yield();
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("Media Foundation GPU capture is only available on Windows.");
                }

                var pacer = new FramePacer(
                    _options.VideoFps,
                    _stats.RecordFrameInterval,
                    _stats.RecordLateFrame,
                    () => _stats.RecordDropped());
                long frameId = 0;
                long lastSourceSequence = 0;

                while (!cancellationToken.IsCancellationRequested)
                {
                    var generatedAt = pacer.MarkFrameStart();
                    GpuFrameLease? frame = null;
                    var frameKind = EncodedFrameKind.New;
                    var captureStopwatch = Stopwatch.StartNew();
                    try
                    {
                        if (!_frameSource.TryAcquireLatestFrame(out frame))
                        {
                            frameKind = lastSourceSequence == 0
                                ? EncodedFrameKind.Black
                                : EncodedFrameKind.Repeat;
                        }

                        captureStopwatch.Stop();
                        if (frame is not null)
                        {
                            _captureStats.RecordCaptured(captureStopwatch.Elapsed.TotalMilliseconds);
                            _acquireStageStats.RecordProcessed(captureStopwatch.Elapsed.TotalMilliseconds);
                            lastSourceSequence = frame.Sequence;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (CaptureException ex) when (IsTransientGpuCaptureMiss(ex.Code))
                    {
                        captureStopwatch.Stop();

                        if (lastSourceSequence == 0)
                        {
                            frameKind = EncodedFrameKind.Black;
                        }
                        else
                        {
                            frameKind = EncodedFrameKind.Repeat;
                        }
                    }
                    catch (CaptureException ex)
                    {
                        captureStopwatch.Stop();
                        _captureStats.RecordError();
                        await PublishCaptureErrorAsync(ex.Code, ex.Message, CancellationToken.None);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        captureStopwatch.Stop();
                        _captureStats.RecordError();
                        await PublishCaptureErrorAsync("GPU_CAPTURE_FAILED", FormatExceptionForLog(ex), CancellationToken.None);
                        throw;
                    }

                    var timestampMs = generatedAt.ToUnixTimeMilliseconds();
                    if (frame is not null)
                    {
                        _previewPublisher.TryPublishTexture(
                            _frameSource.Device,
                            _frameSource.Context,
                            frame.Texture,
                            frame.Sequence,
                            timestampMs);
                    }

                    var queued = new GpuAcquiredFrame(
                        frame,
                        frameId,
                        frameKind,
                        frame?.Sequence ?? lastSourceSequence,
                        frame is null ? _frameSource.LastFrameAgeMs : 0,
                        timestampMs);
                    frame = null;

                    _stats.RecordGenerated(frameId, timestampMs);
                    var dropped = _gpuFrames.WriteLatest(queued);
                    if (dropped > 0)
                    {
                        _acquireStageStats.RecordDropped(dropped);
                        _stats.RecordDropped(dropped);
                    }

                    frameId++;
                    await pacer.WaitForNextFrameAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
            catch (Exception ex)
            {
                await PublishErrorAsync("ENCODER_CAPTURE_FAILED", FormatExceptionForLog(ex), CancellationToken.None);
                _gpuFrames.Complete(ex);
                return;
            }

            _gpuFrames.Complete();
        }

        private async Task ConvertGpuTextureLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Yield();
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("Media Foundation GPU capture is only available on Windows.");
                }

                var converter = _converter ?? throw new InvalidOperationException("GPU converter has not started.");
                ID3D11Texture2D? lastNv12Texture = null;
                ID3D11Texture2D? blackNv12Texture = null;
                ID3D11Texture2D[]? repeatScratchTextures = null;
                var nextRepeatScratchTexture = 0;
                long lastSourceSequence = 0;

                try
                {
                    await foreach (var acquired in _gpuFrames.Reader.ReadAllAsync(cancellationToken))
                    {
                        ID3D11Texture2D? pendingTexture = null;
                        Action releaseNv12Texture = static () => { };
                        try
                        {
                            if (acquired.Lease is not null)
                            {
                                var convertStopwatch = Stopwatch.StartNew();
                                pendingTexture = converter.Convert(acquired.Lease.Texture, cancellationToken, out releaseNv12Texture);
                                convertStopwatch.Stop();
                                _captureStats.RecordConverted(convertStopwatch.Elapsed.TotalMilliseconds);
                                _convertStageStats.RecordProcessed(convertStopwatch.Elapsed.TotalMilliseconds);
                                TryDumpGpuFrame(acquired.Lease.Texture, pendingTexture);

                                lastNv12Texture?.Dispose();
                                lastNv12Texture = pendingTexture.QueryInterface<ID3D11Texture2D>();
                                lastSourceSequence = acquired.SourceSequence;
                            }
                            else if (lastNv12Texture is not null)
                            {
                                repeatScratchTextures ??= CreateRepeatScratchTextures(converter);
                                var repeatScratchTexture = repeatScratchTextures[nextRepeatScratchTexture];
                                nextRepeatScratchTexture = (nextRepeatScratchTexture + 1) % repeatScratchTextures.Length;
                                converter.CopyNv12Texture(lastNv12Texture, repeatScratchTexture);
                                pendingTexture = repeatScratchTexture.QueryInterface<ID3D11Texture2D>();
                            }
                            else
                            {
                                blackNv12Texture ??= converter.CreateBlackNv12Texture();
                                repeatScratchTextures ??= CreateRepeatScratchTextures(converter);
                                var repeatScratchTexture = repeatScratchTextures[nextRepeatScratchTexture];
                                nextRepeatScratchTexture = (nextRepeatScratchTexture + 1) % repeatScratchTextures.Length;
                                converter.CopyNv12Texture(blackNv12Texture, repeatScratchTexture);
                                pendingTexture = repeatScratchTexture.QueryInterface<ID3D11Texture2D>();
                            }

                            var nv12Texture = pendingTexture;
                            pendingTexture = null;
                            var converted = new GpuConvertedFrame(
                                nv12Texture,
                                acquired.FrameId,
                                acquired.FrameKind,
                                acquired.SourceSequence == 0 ? lastSourceSequence : acquired.SourceSequence,
                                acquired.SourceAgeMs,
                                acquired.GeneratedTimestampMs,
                                () =>
                                {
                                    nv12Texture.Dispose();
                                    releaseNv12Texture();
                                });
                            var dropped = _gpuConvertedFrames.WriteLatest(converted);
                            if (dropped > 0)
                            {
                                _convertStageStats.RecordDropped(dropped);
                                _stats.RecordDropped(dropped);
                            }
                        }
                        catch (SharpGenException ex) when (IsTransientGpuPipelineFailure(ex))
                        {
                            _captureStats.RecordError();
                            await PublishCaptureErrorAsync("GPU_PIPELINE_TRANSIENT", FormatExceptionForLog(ex), CancellationToken.None);
                        }
                        finally
                        {
                            pendingTexture?.Dispose();
                            acquired.Dispose();
                        }
                    }
                }
                finally
                {
                    lastNv12Texture?.Dispose();
                    blackNv12Texture?.Dispose();
                    if (repeatScratchTextures is not null)
                    {
                        foreach (var repeatScratchTexture in repeatScratchTextures)
                        {
                            repeatScratchTexture.Dispose();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
            catch (Exception ex)
            {
                await PublishErrorAsync("ENCODER_CONVERT_FAILED", FormatExceptionForLog(ex), CancellationToken.None);
                _gpuConvertedFrames.Complete(ex);
                return;
            }

            _gpuConvertedFrames.Complete();
        }

        private async Task ConvertReadbackLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Yield();
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("Media Foundation GPU capture is only available on Windows.");
                }

                var converter = _converter ?? throw new InvalidOperationException("GPU converter has not started.");
                var readback = _nv12Readback ?? throw new InvalidOperationException("GPU NV12 readback fallback has not started.");
                byte[]? lastNv12Frame = null;
                byte[]? blackNv12Frame = null;
                long lastSourceSequence = 0;

                await foreach (var acquired in _gpuFrames.Reader.ReadAllAsync(cancellationToken))
                {
                    byte[]? nv12Frame = null;
                    try
                    {
                        if (acquired.Lease is not null)
                        {
                            var convertStopwatch = Stopwatch.StartNew();
                            var nv12Texture = converter.Convert(acquired.Lease.Texture, cancellationToken, out var releaseNv12Texture);
                            try
                            {
                                var nv12Data = readback.Read(nv12Texture);
                                convertStopwatch.Stop();
                                _captureStats.RecordConverted(convertStopwatch.Elapsed.TotalMilliseconds);
                                _convertStageStats.RecordProcessed(convertStopwatch.Elapsed.TotalMilliseconds);
                                TryDumpGpuFrame(acquired.Lease.Texture, nv12Texture);
                                nv12Frame = nv12Data.ToArray();
                                lastNv12Frame = nv12Frame;
                                lastSourceSequence = acquired.SourceSequence;
                            }
                            finally
                            {
                                releaseNv12Texture();
                            }
                        }
                        else if (lastNv12Frame is not null)
                        {
                            nv12Frame = lastNv12Frame;
                        }
                        else
                        {
                            blackNv12Frame ??= CreateBlackNv12Frame(_options.VideoWidth, _options.VideoHeight);
                            nv12Frame = blackNv12Frame;
                        }

                        var queued = new Nv12Frame(
                            nv12Frame,
                            acquired.FrameId,
                            acquired.FrameKind,
                            acquired.SourceSequence == 0 ? lastSourceSequence : acquired.SourceSequence,
                            acquired.SourceAgeMs,
                            acquired.GeneratedTimestampMs,
                            returnToPool: false);
                        var dropped = _nv12Frames.WriteLatest(queued);
                        if (dropped > 0)
                        {
                            _convertStageStats.RecordDropped(dropped);
                            _stats.RecordDropped(dropped);
                        }
                    }
                    catch (SharpGenException ex) when (IsTransientGpuPipelineFailure(ex))
                    {
                        _captureStats.RecordError();
                        await PublishCaptureErrorAsync("GPU_PIPELINE_TRANSIENT", FormatExceptionForLog(ex), CancellationToken.None);
                    }
                    finally
                    {
                        acquired.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
            catch (Exception ex)
            {
                await PublishErrorAsync("ENCODER_CONVERT_FAILED", FormatExceptionForLog(ex), CancellationToken.None);
                _nv12Frames.Complete(ex);
                return;
            }

            _nv12Frames.Complete();
        }

        private static ID3D11Texture2D[] CreateRepeatScratchTextures(GpuBgraToNv12Converter converter)
        {
            var textures = new ID3D11Texture2D[3];
            for (var index = 0; index < textures.Length; index++)
            {
                textures[index] = converter.CreateNv12Texture();
            }

            return textures;
        }

        private async Task EncodeLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Yield();
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("Media Foundation GPU encoder is only available on Windows.");
                }

                await foreach (var frame in _gpuConvertedFrames.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        var encodeStopwatch = Stopwatch.StartNew();
                        IReadOnlyList<byte[]> payloads;
                        lock (_encoderLock)
                        {
                            var encoder = _encoder ?? throw new InvalidOperationException("GPU encoder has not started.");
                            payloads = encoder.EncodeFrame(frame.Texture, frame.FrameId);
                        }

                        encodeStopwatch.Stop();
                        _encodeStageStats.RecordProcessed(encodeStopwatch.Elapsed.TotalMilliseconds);

                        foreach (var payload in payloads)
                        {
                            var packet = CreatePacket(
                                payload,
                                frame.FrameKind,
                                frame.FrameId,
                                frame.SourceSequence,
                                frame.SourceAgeMs,
                                encodeStopwatch.Elapsed.TotalMilliseconds);
                            var dropped = _packets.WriteLatest(packet);
                            if (dropped > 0)
                            {
                                _encodeStageStats.RecordDropped(dropped);
                                _stats.RecordDropped(dropped);
                            }
                        }
                    }
                    finally
                    {
                        frame.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
            catch (Exception ex)
            {
                await PublishErrorAsync("ENCODER_FAILED", FormatExceptionForLog(ex), CancellationToken.None);
                _packets.Complete(ex);
                return;
            }

            _packets.Complete();
        }

        private async Task EncodeReadbackLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Yield();
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("Media Foundation GPU encoder is only available on Windows.");
                }

                await foreach (var frame in _nv12Frames.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        var encodeStopwatch = Stopwatch.StartNew();
                        IReadOnlyList<byte[]> payloads;
                        lock (_encoderLock)
                        {
                            var encoder = _encoder ?? throw new InvalidOperationException("GPU encoder has not started.");
                            payloads = encoder.EncodeFrame(frame.Data, frame.FrameId);
                        }

                        encodeStopwatch.Stop();
                        _encodeStageStats.RecordProcessed(encodeStopwatch.Elapsed.TotalMilliseconds);

                        foreach (var payload in payloads)
                        {
                            var packet = CreatePacket(
                                payload,
                                frame.FrameKind,
                                frame.FrameId,
                                frame.SourceSequence,
                                frame.SourceAgeMs,
                                encodeStopwatch.Elapsed.TotalMilliseconds);
                            var dropped = _packets.WriteLatest(packet);
                            if (dropped > 0)
                            {
                                _encodeStageStats.RecordDropped(dropped);
                                _stats.RecordDropped(dropped);
                            }
                        }
                    }
                    finally
                    {
                        frame.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
            catch (Exception ex)
            {
                await PublishErrorAsync("ENCODER_FAILED", FormatExceptionForLog(ex), CancellationToken.None);
                _packets.Complete(ex);
                return;
            }

            _packets.Complete();
        }

        private EncodedVideoPacket CreatePacket(byte[] payload, EncodedFrameKind frameKind, long frameId, long sourceSequence, double sourceAgeMs, double encodeMs)
        {
            var containsPicture = H264AnnexBStream.ContainsPicture(payload);
            var isKeyFrame = H264AnnexBStream.ContainsNalType(payload, 5);
            var encodedFrameId = containsPicture
                ? _stats.RecordEncodedFrame(frameId, payload.Length, encodeMs)
                : _stats.LastEncodedFrameId;

            return new EncodedVideoPacket(
                payload,
                isKeyFrame,
                containsPicture,
                encodedFrameId,
                Interlocked.Increment(ref _sequence) - 1,
                _stats.GetFrameTimestamp(encodedFrameId),
                _stats.GetFrameEncodeMs(encodedFrameId),
                frameKind,
                sourceSequence,
                sourceAgeMs);
        }

        private void TryDumpGpuFrame(ID3D11Texture2D bgraTexture, ID3D11Texture2D nv12Texture)
        {
            if (string.IsNullOrWhiteSpace(_options.DumpGpuFrameDirectory) ||
                Interlocked.Exchange(ref _gpuFrameDumped, 1) != 0)
            {
                return;
            }

            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    return;
                }

                var dumpDirectory = _options.DumpGpuFrameDirectory;
                Directory.CreateDirectory(dumpDirectory);
                GpuFrameDebugDumper.Dump(_frameSource.Device, _frameSource.Context, bgraTexture, nv12Texture, dumpDirectory);
                Log($"CAPTURE {_connectionId}", $"gpu frame debug dump written to {dumpDirectory}");
            }
            catch (Exception ex)
            {
                Log($"CAPTURE {_connectionId}", $"GPU_FRAME_DUMP_FAILED: {ex.Message}");
            }
        }

        private async Task PublishStatsLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    var snapshot = _stats.SnapshotAndResetWindow();
                    _latestEncoderSnapshot = snapshot;
                    var acquireSnapshot = _acquireStageStats.SnapshotAndResetWindow();
                    var convertSnapshot = _convertStageStats.SnapshotAndResetWindow();
                    var encodeSnapshot = _encodeStageStats.SnapshotAndResetWindow();
                    MaybeRestartEncoderForCadenceLoss(snapshot);
                    Log($"ENCODER {_connectionId}", $"stats fallback={EncoderFallbackName} generated={snapshot.FramesGenerated} encoded={snapshot.FramesEncoded} sent={snapshot.FramesSent} dropped={snapshot.FramesDropped} late={snapshot.LateFrames} streamFps={snapshot.StreamFps:F1} localLatencyP95={snapshot.P95LocalLatencyMs:F1}ms new={snapshot.NewFramesSent} repeat={snapshot.RepeatFramesSent} avgFrameInterval={snapshot.P50FrameIntervalMs:F2}/{snapshot.P95FrameIntervalMs:F2}/{snapshot.P99FrameIntervalMs:F2}ms avgEncode={snapshot.AvgEncodeMs:F1}ms p95Encode={snapshot.P95EncodeMs:F1}ms p99Encode={snapshot.P99EncodeMs:F1}ms maxEncode={snapshot.MaxEncodeMs:F1}ms avgSend={snapshot.AvgSendMs:F1}ms p95Send={snapshot.P95SendMs:F1}ms kbps={snapshot.OutputKbps:F0} pipeline {FormatPipelineStageForLog(acquireSnapshot)} {FormatPipelineStageForLog(convertSnapshot)} {FormatPipelineStageForLog(encodeSnapshot)}");
                    var encoderStatsPayload = CreateEncoderStatsPayload(snapshot, gpuPath: true);
                    AddEncoderTuningPayload(encoderStatsPayload, _options);
                    AddEncoderPathPayload(encoderStatsPayload);
                    await _controlPublisher.PublishAsync("encoder_stats", encoderStatsPayload, cancellationToken);

                    var pipelineStatsPayload = CreatePipelineStatsPayload(_frameSource.SourceName, gpuPath: true, acquireSnapshot, convertSnapshot, encodeSnapshot);
                    AddEncoderPathPayload(pipelineStatsPayload);
                    await _controlPublisher.PublishAsync("pipeline_stats", pipelineStatsPayload, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
        }

        private void MaybeRestartEncoderForCadenceLoss(RealtimeEncoderStatsSnapshot snapshot)
        {
            if (!_encoderStarted || !OperatingSystem.IsWindows() || _options.VideoFps < 90)
            {
                return;
            }

            var targetFps = Math.Max(1, _options.VideoFps);
            var lowStreamFps = snapshot.StreamFps > 0 && snapshot.StreamFps < targetFps * 0.92;
            var sendHealthy = snapshot.P95SendMs < 2.0;
            var cadenceLooks60Hz = snapshot.P50FrameIntervalMs >= 14.0 || snapshot.P95FrameIntervalMs >= 15.0;
            var encoderLooks60Hz = snapshot.AvgEncodeMs >= 10.0 || snapshot.P95EncodeMs >= 14.0 || snapshot.P99EncodeMs >= 15.0;
            if (lowStreamFps && sendHealthy && (cadenceLooks60Hz || encoderLooks60Hz))
            {
                _encoderWatchdogStrikeCount++;
                if (_encoderWatchdogStrikeCount >= EncoderWatchdogStrikeThreshold)
                {
                    RestartEncoder(
                        $"cadence_loss streamFps={snapshot.StreamFps:F1} frameInterval={snapshot.P50FrameIntervalMs:F2}/{snapshot.P95FrameIntervalMs:F2}/{snapshot.P99FrameIntervalMs:F2}ms encode={snapshot.AvgEncodeMs:F1}/{snapshot.P95EncodeMs:F1}/{snapshot.P99EncodeMs:F1}ms sendP95={snapshot.P95SendMs:F1}ms");
                }

                return;
            }

            if (snapshot.StreamFps >= targetFps * 0.96 || !sendHealthy)
            {
                _encoderWatchdogStrikeCount = 0;
            }
        }

        private async Task PublishCaptureStatsLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    var snapshot = _captureStats.SnapshotAndResetWindow();
                    _latestCaptureSnapshot = snapshot;
                    Log($"CAPTURE {_connectionId}", $"gpu stats fallback={EncoderFallbackName} ringSlots={_frameSource.SlotCount} captured={snapshot.FramesCaptured} converted={snapshot.FramesConverted} errors={snapshot.CaptureErrors} captureFps={snapshot.CaptureFps:F1} convertFps={snapshot.ConvertFps:F1} avgAcquire={snapshot.AvgCaptureMs:F1}ms p95Acquire={snapshot.P95CaptureMs:F1}ms avgConvert={snapshot.AvgConvertMs:F1}ms p95Convert={snapshot.P95ConvertMs:F1}ms dropped={_frameSource.FramesDropped}");
                    var payload = new JsonObject
                    {
                        ["source"] = _frameSource.SourceName,
                        ["framesCaptured"] = snapshot.FramesCaptured,
                        ["framesConverted"] = snapshot.FramesConverted,
                        ["captureErrors"] = snapshot.CaptureErrors,
                        ["avgCaptureMs"] = Math.Round(snapshot.AvgCaptureMs, 2),
                        ["avgConvertMs"] = Math.Round(snapshot.AvgConvertMs, 2),
                        ["gpuConvertMs"] = Math.Round(snapshot.AvgConvertMs, 2),
                        ["framesDropped"] = _frameSource.FramesDropped,
                        ["lastFrameAgeMs"] = Math.Round(_frameSource.LastFrameAgeMs, 0),
                        ["gpuPath"] = true,
                        ["directD3DInput"] = _encoderUsesD3DInput,
                        ["fallback"] = EncoderFallbackName,
                        ["driverGpuRingSlots"] = _frameSource.SlotCount
                    };
                    AddFallbackReason(payload);
                    AddCapturePercentiles(payload, snapshot);
                    await _controlPublisher.PublishAsync("capture_stats", payload, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
        }

        private Task PublishErrorAsync(string code, string message, CancellationToken cancellationToken)
        {
            Log($"ENCODER {_connectionId}", $"{code}: {message}");
            return _controlPublisher.PublishAsync("encoder_error", new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["gpuPath"] = true,
                ["fallback"] = EncoderFallbackName
            }, cancellationToken).AsTask();
        }

        private Task PublishCaptureErrorAsync(string code, string message, CancellationToken cancellationToken)
        {
            Log($"CAPTURE {_connectionId}", $"{code}: {message}");
            return _controlPublisher.PublishAsync("capture_error", new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["source"] = _frameSource.SourceName,
                ["gpuPath"] = true,
                ["fallback"] = EncoderFallbackName
            }, cancellationToken).AsTask();
        }

        private string EncoderFallbackName => _encoderUsesD3DInput ? DirectEncoderFallback : ReadbackEncoderFallback;

        private string EncoderInputFormat => _encoderUsesD3DInput ? "d3d11-nv12-texture" : "nv12-readback";

        private string FallbackReasonForLog => string.IsNullOrWhiteSpace(_encoderFallbackReason)
            ? string.Empty
            : $" reason={_encoderFallbackReason}";

        private void AddEncoderPathPayload(JsonObject payload)
        {
            payload["source"] = _frameSource.SourceName;
            payload["inputFormat"] = EncoderInputFormat;
            payload["directD3DInput"] = _encoderUsesD3DInput;
            payload["fallback"] = EncoderFallbackName;
            payload["driverGpuRingSlots"] = _frameSource.SlotCount;
            if (!string.IsNullOrWhiteSpace(_encoderMftName))
            {
                payload["encoderMft"] = _encoderMftName;
            }

            AddFallbackReason(payload);
        }

        private void AddFallbackReason(JsonObject payload)
        {
            if (!string.IsNullOrWhiteSpace(_encoderFallbackReason))
            {
                payload["fallbackReason"] = _encoderFallbackReason;
            }
        }

        [SupportedOSPlatform("windows")]
        private static D3D11EncoderInputProbeResult ProbeD3D11EncoderInput(HostOptions options, ID3D11Device device)
        {
            try
            {
                using var encoder = new MediaFoundationH264Encoder(options, device);
                encoder.Start();
                using var probeTexture = CreateD3D11EncoderProbeTexture(device, options.VideoWidth, options.VideoHeight);
                encoder.EncodeFrame(probeTexture, 0);
                return new D3D11EncoderInputProbeResult(true, null, encoder.SelectedMftName);
            }
            catch (Exception ex) when (IsD3D11EncoderInputFailure(ex))
            {
                return new D3D11EncoderInputProbeResult(false, FormatExceptionReason(ex), null);
            }
        }

        [SupportedOSPlatform("windows")]
        private static ID3D11Texture2D CreateD3D11EncoderProbeTexture(ID3D11Device device, int width, int height)
        {
            return device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.VideoEncoder,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            });
        }

        private static bool IsD3D11EncoderInputFailure(Exception ex)
        {
            return ex is COMException or SharpGenException or InvalidOperationException or ArgumentException;
        }

        private static string FormatExceptionReason(Exception ex)
        {
            var message = ex switch
            {
                COMException comException => $"HRESULT=0x{comException.HResult:X8}: {comException.Message}",
                SharpGenException sharpGenException => $"HRESULT=0x{sharpGenException.HResult:X8}: {sharpGenException.Message}",
                _ => ex.Message
            };

            return message.ReplaceLineEndings(" ");
        }

        private static bool IsTransientGpuCaptureMiss(string code)
        {
            return code is "IDD_GPU_FRAME_TIMEOUT" or "IDD_GPU_FRAME_STALE" or "IDD_GPU_SLOT_BUSY";
        }

        private readonly record struct D3D11EncoderInputProbeResult(bool SupportsDirectInput, string? FallbackReason, string? EncoderMftName);

        private static bool IsTransientGpuPipelineFailure(SharpGenException ex)
        {
            return ex.HResult is
                unchecked((int)0x887A0001) or // DXGI_ERROR_INVALID_CALL
                unchecked((int)0x887A0026) or // DXGI_ERROR_ACCESS_LOST
                unchecked((int)0x887A0027) or // DXGI_ERROR_WAIT_TIMEOUT
                unchecked((int)0x887A0005) or // DXGI_ERROR_DEVICE_REMOVED
                unchecked((int)0x887A0007);   // DXGI_ERROR_DEVICE_RESET
        }

        private static string FormatExceptionForLog(Exception ex)
        {
            var message = ex.Message;
            if (ex is SharpGenException sharpGenException)
            {
                message = $"HRESULT=0x{sharpGenException.HResult:X8}: {message}";
            }

            return string.IsNullOrWhiteSpace(ex.StackTrace)
                ? message
                : $"{message}{Environment.NewLine}{ex.StackTrace}";
        }

        private static byte[] CreateBlackNv12Frame(int width, int height)
        {
            var frame = new byte[checked(width * height * 3 / 2)];
            var yPlaneSize = checked(width * height);
            Array.Fill<byte>(frame, 16, 0, yPlaneSize);
            Array.Fill<byte>(frame, 128, yPlaneSize, frame.Length - yPlaneSize);
            return frame;
        }

        private static CaptureStatsSnapshot CreateEmptyCaptureSnapshot()
        {
            return new CaptureStatsSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        private static RealtimeEncoderStatsSnapshot CreateEmptyEncoderSnapshot()
        {
            return new RealtimeEncoderStatsSnapshot(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class GpuBgraToNv12Converter : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly ID3D11VideoDevice _videoDevice;
        private readonly ID3D11VideoContext _videoContext;
        private readonly ID3D11VideoProcessorEnumerator _enumerator;
        private readonly ID3D11VideoProcessor _processor;
        private readonly int _width;
        private readonly int _height;
        private readonly ID3D11Texture2D[] _nv12TexturePool;
        private readonly Channel<int> _availableNv12Textures;

        public GpuBgraToNv12Converter(ID3D11Device device, ID3D11DeviceContext context, int width, int height, int fps, int nv12TexturePoolSize)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "Video dimensions must be positive.");
            }

            if ((width & 1) != 0 || (height & 1) != 0)
            {
                throw new ArgumentException("NV12 requires even video dimensions.");
            }

            _device = device;
            _context = context;
            _width = width;
            _height = height;
            _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
            _videoContext = _context.QueryInterface<ID3D11VideoContext>();
            _nv12TexturePool = new ID3D11Texture2D[Math.Max(1, nv12TexturePoolSize)];
            _availableNv12Textures = Channel.CreateBounded<int>(new BoundedChannelOptions(_nv12TexturePool.Length)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });

            var description = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational((uint)Math.Max(1, fps), 1),
                InputWidth = (uint)width,
                InputHeight = (uint)height,
                OutputFrameRate = new Rational((uint)Math.Max(1, fps), 1),
                OutputWidth = (uint)width,
                OutputHeight = (uint)height,
                Usage = VideoUsage.OptimalSpeed
            };

            _enumerator = _videoDevice.CreateVideoProcessorEnumerator(description);
            _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
            for (var index = 0; index < _nv12TexturePool.Length; index++)
            {
                _nv12TexturePool[index] = CreateNv12Texture();
                _availableNv12Textures.Writer.TryWrite(index);
            }
        }

        public ID3D11Texture2D Convert(ID3D11Texture2D bgraTexture, CancellationToken cancellationToken, out Action release)
        {
            var description = bgraTexture.Description;
            if (description.Width != _width || description.Height != _height || description.Format != Format.B8G8R8A8_UNorm)
            {
                throw new CaptureException("GPU_FRAME_LAYOUT_UNSUPPORTED", $"Unexpected GPU frame layout {description.Width}x{description.Height} {description.Format}.");
            }

            var slotIndex = RentNv12Texture(cancellationToken);
            release = () => ReleaseNv12Texture(slotIndex);
            var nv12Texture = _nv12TexturePool[slotIndex];
            using var inputView = _videoDevice.CreateVideoProcessorInputView(
                bgraTexture,
                _enumerator,
                new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView
                    {
                        MipSlice = 0,
                        ArraySlice = 0
                    }
                });

            using var outputView = _videoDevice.CreateVideoProcessorOutputView(
                nv12Texture,
                _enumerator,
                new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorOutputView
                    {
                        MipSlice = 0
                    }
                });

            var stream = new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                PastFrames = 0,
                FutureFrames = 0,
                InputSurface = inputView
            };

            _videoContext.VideoProcessorBlt(_processor, outputView, 0, 1, [stream]).CheckError();
            _context.Flush();
            return nv12Texture.QueryInterface<ID3D11Texture2D>();
        }

        public ID3D11Texture2D CreateBlackNv12Texture()
        {
            var uploadDescription = CreateNv12TextureDescription();
            uploadDescription.Usage = ResourceUsage.Staging;
            uploadDescription.BindFlags = BindFlags.None;
            uploadDescription.CPUAccessFlags = CpuAccessFlags.Write;

            using var uploadTexture = _device.CreateTexture2D(in uploadDescription);
            MappedSubresource mapped = default;
            _context.Map(uploadTexture, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None, out mapped).CheckError();
            try
            {
                FillBlackNv12(mapped, _width, _height);
            }
            finally
            {
                _context.Unmap(uploadTexture, 0);
            }

            var nv12Texture = CreateNv12Texture();
            _context.CopyResource(nv12Texture, uploadTexture);
            _context.Flush();
            return nv12Texture;
        }

        public ID3D11Texture2D CreateNv12Texture()
        {
            return _device.CreateTexture2D(CreateNv12TextureDescription());
        }

        public void CopyNv12Texture(ID3D11Texture2D source, ID3D11Texture2D destination)
        {
            ValidateNv12Texture(source, nameof(source));
            ValidateNv12Texture(destination, nameof(destination));
            _context.CopyResource(destination, source);
            _context.Flush();
        }

        private void ValidateNv12Texture(ID3D11Texture2D texture, string parameterName)
        {
            var description = texture.Description;
            if (description.Width != _width || description.Height != _height || description.Format != Format.NV12)
            {
                throw new ArgumentException("Texture must be an NV12 texture matching the configured video dimensions.", parameterName);
            }
        }

        private int RentNv12Texture(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_availableNv12Textures.Reader.TryRead(out var slotIndex))
                {
                    return slotIndex;
                }

                if (cancellationToken.WaitHandle.WaitOne(1))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        private void ReleaseNv12Texture(int slotIndex)
        {
            _availableNv12Textures.Writer.TryWrite(slotIndex);
        }

        private Texture2DDescription CreateNv12TextureDescription()
        {
            return new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.VideoEncoder,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };
        }

        private static void FillBlackNv12(MappedSubresource mapped, int width, int height)
        {
            var rowPitch = checked((int)mapped.RowPitch);
            var yRow = new byte[width];
            var uvRow = new byte[width];
            Array.Fill<byte>(yRow, 16);
            Array.Fill<byte>(uvRow, 128);

            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(yRow, 0, IntPtr.Add(mapped.DataPointer, checked(y * rowPitch)), width);
            }

            var uvBase = IntPtr.Add(mapped.DataPointer, checked(rowPitch * height));
            for (var y = 0; y < height / 2; y++)
            {
                Marshal.Copy(uvRow, 0, IntPtr.Add(uvBase, checked(y * rowPitch)), width);
            }
        }

        public void Dispose()
        {
            foreach (var texture in _nv12TexturePool)
            {
                texture.Dispose();
            }

            _processor.Dispose();
            _enumerator.Dispose();
            _videoContext.Dispose();
            _videoDevice.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static class GpuFrameDebugDumper
    {
        public static void Dump(
            ID3D11Device device,
            ID3D11DeviceContext context,
            ID3D11Texture2D bgraTexture,
            ID3D11Texture2D nv12Texture,
            string directory)
        {
            var bgra = ReadTexture(device, context, bgraTexture, Format.B8G8R8A8_UNorm);
            var nv12 = ReadTexture(device, context, nv12Texture, Format.NV12);
            var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

            var bgraBmp = Path.Combine(directory, $"idd-gpu-bgra-{bgra.Width}x{bgra.Height}-{timestamp}.bmp");
            var nv12Raw = Path.Combine(directory, $"idd-gpu-nv12-{nv12.Width}x{nv12.Height}-{timestamp}.nv12");
            var nv12Preview = Path.Combine(directory, $"idd-gpu-nv12-preview-{nv12.Width}x{nv12.Height}-{timestamp}.ppm");
            var info = Path.Combine(directory, $"idd-gpu-dump-{timestamp}.txt");

            WriteBgraBmp(bgraBmp, bgra.Width, bgra.Height, bgra.Bytes);
            File.WriteAllBytes(nv12Raw, nv12.Bytes);
            WriteNv12Ppm(nv12Preview, nv12.Width, nv12.Height, nv12.Bytes);
            File.WriteAllText(info, string.Join(Environment.NewLine,
            [
                $"timestamp={timestamp}",
                $"bgra={bgraBmp}",
                $"nv12={nv12Raw}",
                $"nv12Preview={nv12Preview}",
                $"width={bgra.Width}",
                $"height={bgra.Height}",
                $"bgraBytes={bgra.Bytes.Length}",
                $"nv12Bytes={nv12.Bytes.Length}"
            ]), Encoding.UTF8);
        }

        private static TextureReadback ReadTexture(
            ID3D11Device device,
            ID3D11DeviceContext context,
            ID3D11Texture2D source,
            Format expectedFormat)
        {
            var description = source.Description;
            if (description.Format != expectedFormat)
            {
                throw new InvalidOperationException($"Expected {expectedFormat} texture but got {description.Format}.");
            }

            var readbackDescription = description;
            readbackDescription.BindFlags = BindFlags.None;
            readbackDescription.MiscFlags = ResourceOptionFlags.None;
            readbackDescription.Usage = ResourceUsage.Staging;
            readbackDescription.CPUAccessFlags = CpuAccessFlags.Read;

            using var staging = device.CreateTexture2D(in readbackDescription);
            context.CopyResource(staging, source);
            context.Flush();

            MappedSubresource mapped = default;
            context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out mapped).CheckError();
            try
            {
                var width = checked((int)description.Width);
                var height = checked((int)description.Height);
                return expectedFormat == Format.NV12
                    ? new TextureReadback(width, height, ReadNv12(mapped, width, height))
                    : new TextureReadback(width, height, ReadBgra(mapped, width, height));
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }

        private static byte[] ReadBgra(MappedSubresource mapped, int width, int height)
        {
            var output = new byte[checked(width * height * 4)];
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(mapped.DataPointer, checked(y * (int)mapped.RowPitch)), output, y * rowBytes, rowBytes);
            }

            return output;
        }

        private static byte[] ReadNv12(MappedSubresource mapped, int width, int height)
        {
            var output = new byte[checked(width * height * 3 / 2)];
            var yPlaneSize = width * height;
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(mapped.DataPointer, checked(y * (int)mapped.RowPitch)), output, y * width, width);
            }

            var uvBase = IntPtr.Add(mapped.DataPointer, checked((int)mapped.RowPitch * height));
            for (var y = 0; y < height / 2; y++)
            {
                Marshal.Copy(IntPtr.Add(uvBase, checked(y * (int)mapped.RowPitch)), output, yPlaneSize + y * width, width);
            }

            return output;
        }

        private static void WriteBgraBmp(string path, int width, int height, byte[] bgra)
        {
            var rowBytes = width * 4;
            var pixelDataSize = rowBytes * height;
            var fileSize = 14 + 40 + pixelDataSize;
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write(fileSize);
            writer.Write(0);
            writer.Write(14 + 40);
            writer.Write(40);
            writer.Write(width);
            writer.Write(-height);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(0);
            writer.Write(pixelDataSize);
            writer.Write(2835);
            writer.Write(2835);
            writer.Write(0);
            writer.Write(0);
            writer.Write(bgra);
        }

        private static void WriteNv12Ppm(string path, int width, int height, byte[] nv12)
        {
            using var stream = File.Create(path);
            var header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
            stream.Write(header);
            var yPlaneSize = width * height;
            Span<byte> rgb = stackalloc byte[3];
            for (var y = 0; y < height; y++)
            {
                var yRow = y * width;
                var uvRow = yPlaneSize + (y / 2) * width;
                for (var x = 0; x < width; x++)
                {
                    var yValue = nv12[yRow + x];
                    var uvOffset = uvRow + (x & ~1);
                    var uValue = nv12[uvOffset];
                    var vValue = nv12[uvOffset + 1];
                    ConvertYuvToRgb(yValue, uValue, vValue, rgb);
                    stream.Write(rgb);
                }
            }
        }

        private static void ConvertYuvToRgb(byte yValue, byte uValue, byte vValue, Span<byte> rgb)
        {
            var c = yValue - 16;
            var d = uValue - 128;
            var e = vValue - 128;
            rgb[0] = ClampToByte((298 * c + 409 * e + 128) >> 8);
            rgb[1] = ClampToByte((298 * c - 100 * d - 208 * e + 128) >> 8);
            rgb[2] = ClampToByte((298 * c + 516 * d + 128) >> 8);
        }

        private static byte ClampToByte(int value)
        {
            return (byte)Math.Clamp(value, 0, 255);
        }

        private sealed record TextureReadback(int Width, int Height, byte[] Bytes);
    }

    private sealed class OverviewPreviewFramePublisher : IDisposable
    {
        private const string MapName = @"Local\SideDockOverviewPreviewFrame";
        private const int HeaderSize = 128;
        private const int Magic = 0x50464453; // SDFP
        private const int Version = 1;
        private const int FormatBgra32 = 1;
        private const int MaxFrameBytes = 3840 * 2160 * 4;
        private const int MaxPreviewFps = 15;
        private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromSeconds(5);

        private readonly Action<string> _log;
        private readonly long _minPublishTicks = Stopwatch.Frequency / MaxPreviewFps;
        private MemoryMappedFile? _mapping;
        private MemoryMappedViewAccessor? _view;
        private ID3D11Texture2D? _stagingTexture;
        private byte[]? _rowBuffer;
        private int _stagingWidth;
        private int _stagingHeight;
        private long _frameSequence;
        private long _lastPublishTicks;
        private long _lastErrorLogTicks;

        public OverviewPreviewFramePublisher(Action<string> log)
        {
            _log = log;
        }

        public void TryPublishBgra(byte[] bgra, int width, int height, int stride, long sourceSequence, long timestampMs)
        {
            if (!ShouldPublish())
            {
                return;
            }

            try
            {
                ValidateFrameLayout(width, height, stride);
                var rowBytes = checked(width * 4);
                var requiredLength = checked((height - 1) * stride + rowBytes);
                if (bgra.Length < requiredLength)
                {
                    return;
                }

                var view = EnsureView();
                BeginFrame(view, width, height, rowBytes, checked(rowBytes * height), sourceSequence, timestampMs);
                if (stride == rowBytes)
                {
                    view.WriteArray(HeaderSize, bgra, 0, rowBytes * height);
                }
                else
                {
                    for (var y = 0; y < height; y++)
                    {
                        view.WriteArray(HeaderSize + y * rowBytes, bgra, y * stride, rowBytes);
                    }
                }

                CommitFrame(view);
            }
            catch (Exception ex) when (IsPublishFailure(ex))
            {
                LogThrottled($"overview preview publish skipped: {FormatPublishException(ex)}");
            }
        }

        public void TryPublishTexture(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D texture, long sourceSequence, long timestampMs)
        {
            if (!ShouldPublish())
            {
                return;
            }

            MappedSubresource mapped = default;
            var mappedTexture = false;
            try
            {
                var description = texture.Description;
                if (description.Format != Format.B8G8R8A8_UNorm)
                {
                    return;
                }

                var width = checked((int)description.Width);
                var height = checked((int)description.Height);
                var rowBytes = checked(width * 4);
                ValidateFrameLayout(width, height, rowBytes);

                var staging = EnsureStagingTexture(device, description);
                context.CopyResource(staging, texture);
                context.Flush();
                context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out mapped).CheckError();
                mappedTexture = true;

                var view = EnsureView();
                BeginFrame(view, width, height, rowBytes, checked(rowBytes * height), sourceSequence, timestampMs);
                var row = EnsureRowBuffer(rowBytes);
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(mapped.DataPointer, checked(y * (int)mapped.RowPitch)), row, 0, rowBytes);
                    view.WriteArray(HeaderSize + y * rowBytes, row, 0, rowBytes);
                }

                CommitFrame(view);
            }
            catch (Exception ex) when (IsPublishFailure(ex))
            {
                LogThrottled($"overview preview GPU readback skipped: {FormatPublishException(ex)}");
            }
            finally
            {
                if (mappedTexture)
                {
                    try
                    {
                        context.Unmap(_stagingTexture, 0);
                    }
                    catch (Exception ex) when (ex is SharpGenException or COMException or ObjectDisposedException or InvalidOperationException)
                    {
                    }
                }
            }
        }

        public void Dispose()
        {
            _stagingTexture?.Dispose();
            _stagingTexture = null;
            _view?.Dispose();
            _view = null;
            _mapping?.Dispose();
            _mapping = null;
        }

        private bool ShouldPublish()
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref _lastPublishTicks);
            if (previous != 0 && now - previous < _minPublishTicks)
            {
                return false;
            }

            Interlocked.Exchange(ref _lastPublishTicks, now);
            return true;
        }

        private MemoryMappedViewAccessor EnsureView()
        {
            if (_view is not null)
            {
                return _view;
            }

            _mapping = MemoryMappedFile.CreateOrOpen(
                MapName,
                HeaderSize + MaxFrameBytes,
                MemoryMappedFileAccess.ReadWrite);
            _view = _mapping.CreateViewAccessor(0, HeaderSize + MaxFrameBytes, MemoryMappedFileAccess.ReadWrite);
            var existingSequence = _view.ReadInt64(32);
            if (existingSequence > 0)
            {
                _frameSequence = Math.Max(_frameSequence, existingSequence / 2);
            }

            return _view;
        }

        private ID3D11Texture2D EnsureStagingTexture(ID3D11Device device, Texture2DDescription sourceDescription)
        {
            var width = checked((int)sourceDescription.Width);
            var height = checked((int)sourceDescription.Height);
            if (_stagingTexture is not null && _stagingWidth == width && _stagingHeight == height)
            {
                return _stagingTexture;
            }

            _stagingTexture?.Dispose();
            var stagingDescription = sourceDescription;
            stagingDescription.BindFlags = BindFlags.None;
            stagingDescription.MiscFlags = ResourceOptionFlags.None;
            stagingDescription.Usage = ResourceUsage.Staging;
            stagingDescription.CPUAccessFlags = CpuAccessFlags.Read;
            _stagingTexture = device.CreateTexture2D(in stagingDescription);
            _stagingWidth = width;
            _stagingHeight = height;
            return _stagingTexture;
        }

        private byte[] EnsureRowBuffer(int rowBytes)
        {
            if (_rowBuffer is null || _rowBuffer.Length < rowBytes)
            {
                _rowBuffer = new byte[rowBytes];
            }

            return _rowBuffer;
        }

        private void BeginFrame(
            MemoryMappedViewAccessor view,
            int width,
            int height,
            int stride,
            int frameBytes,
            long sourceSequence,
            long timestampMs)
        {
            var frameSequence = Interlocked.Increment(ref _frameSequence);
            view.Write(32, checked(frameSequence * 2 - 1));
            view.Write(0, Magic);
            view.Write(4, Version);
            view.Write(8, HeaderSize);
            view.Write(12, width);
            view.Write(16, height);
            view.Write(20, stride);
            view.Write(24, FormatBgra32);
            view.Write(28, frameBytes);
            view.Write(40, sourceSequence);
            view.Write(48, timestampMs > 0 ? timestampMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        private void CommitFrame(MemoryMappedViewAccessor view)
        {
            var frameSequence = Interlocked.Read(ref _frameSequence);
            view.Write(32, checked(frameSequence * 2));
        }

        private static void ValidateFrameLayout(int width, int height, int stride)
        {
            if (width <= 0 || height <= 0 || stride < width * 4)
            {
                throw new ArgumentException($"Invalid preview frame layout {width}x{height} stride={stride}.");
            }

            var frameBytes = checked(width * height * 4);
            if (frameBytes <= 0 || frameBytes > MaxFrameBytes)
            {
                throw new ArgumentException($"Preview frame size is not supported: {width}x{height}.");
            }
        }

        private void LogThrottled(string message)
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref _lastErrorLogTicks);
            if (previous != 0 && (now - previous) / (double)Stopwatch.Frequency < ErrorLogInterval.TotalSeconds)
            {
                return;
            }

            Interlocked.Exchange(ref _lastErrorLogTicks, now);
            _log(message);
        }

        private static bool IsPublishFailure(Exception ex)
        {
            return ex is IOException
                or UnauthorizedAccessException
                or ObjectDisposedException
                or ArgumentException
                or InvalidOperationException
                or COMException
                or SharpGenException;
        }

        private static string FormatPublishException(Exception ex)
        {
            return ex is COMException or SharpGenException
                ? $"HRESULT=0x{ex.HResult:X8}: {ex.Message}"
                : ex.Message;
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class GpuNv12Readback : IDisposable
    {
        private readonly ID3D11DeviceContext _context;
        private readonly ID3D11Texture2D _stagingTexture;
        private readonly byte[] _buffer;
        private readonly int _width;
        private readonly int _height;

        public GpuNv12Readback(ID3D11Device device, ID3D11DeviceContext context, int width, int height)
        {
            if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
            {
                throw new ArgumentException("NV12 readback requires positive even dimensions.");
            }

            _context = context;
            _width = width;
            _height = height;
            _buffer = new byte[MediaFoundationH264Encoder.GetNv12FrameSize(width, height)];
            var description = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };
            _stagingTexture = device.CreateTexture2D(in description);
        }

        public byte[] Read(ID3D11Texture2D nv12Texture)
        {
            var description = nv12Texture.Description;
            if (description.Width != _width || description.Height != _height || description.Format != Format.NV12)
            {
                throw new CaptureException("GPU_NV12_LAYOUT_UNSUPPORTED", $"Unexpected GPU NV12 layout {description.Width}x{description.Height} {description.Format}.");
            }

            _context.CopyResource(_stagingTexture, nv12Texture);
            _context.Flush();

            MappedSubresource mapped = default;
            _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out mapped).CheckError();
            try
            {
                var yPlaneSize = _width * _height;
                for (var y = 0; y < _height; y++)
                {
                    Marshal.Copy(IntPtr.Add(mapped.DataPointer, checked(y * (int)mapped.RowPitch)), _buffer, y * _width, _width);
                }

                var uvBase = IntPtr.Add(mapped.DataPointer, checked((int)mapped.RowPitch * _height));
                for (var y = 0; y < _height / 2; y++)
                {
                    Marshal.Copy(IntPtr.Add(uvBase, checked(y * (int)mapped.RowPitch)), _buffer, yPlaneSize + y * _width, _width);
                }

                return _buffer;
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
            }
        }

        public void Dispose()
        {
            _stagingTexture.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static class KeyedMutexNative
    {
        private const int AcquireSyncSlot = 8;
        private const int ReleaseSyncSlot = 9;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AcquireSyncDelegate(IntPtr thisPtr, ulong key, int milliseconds);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReleaseSyncDelegate(IntPtr thisPtr, ulong key);

        public static int AcquireSync(IDXGIKeyedMutex mutex, ulong key, int milliseconds)
        {
            var thisPtr = mutex.NativePointer;
            var vtbl = Marshal.ReadIntPtr(thisPtr);
            var method = Marshal.ReadIntPtr(vtbl, AcquireSyncSlot * IntPtr.Size);
            var call = Marshal.GetDelegateForFunctionPointer<AcquireSyncDelegate>(method);
            return call(thisPtr, key, milliseconds);
        }

        public static int ReleaseSync(IDXGIKeyedMutex mutex, ulong key)
        {
            var thisPtr = mutex.NativePointer;
            var vtbl = Marshal.ReadIntPtr(thisPtr);
            var method = Marshal.ReadIntPtr(vtbl, ReleaseSyncSlot * IntPtr.Size);
            var call = Marshal.GetDelegateForFunctionPointer<ReleaseSyncDelegate>(method);
            return call(thisPtr, key);
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class IddGpuFrameSource : IGpuFrameSource
    {
        private const string MetadataName = "Global\\SideDockGpuFrameMetadata";
        private const string FrameReadyName = "Global\\SideDockGpuFrameReady";
        private const string ConsumerAliveName = "Global\\SideDockGpuConsumerAlive";
        private const uint FrameMagic = 0x474B4453; // SDKG
        private const int FrameVersion = 1;
        private const int FrameFormatBgra = 1;
        private const int SlotCountMax = 12;
        private const int MetadataHeaderSize = 72;
        private const int SlotHeaderSize = 32;
        private const int FrameReadyTimeoutMs = 500;
        private const int PollFrameReadyTimeoutMs = 0;
        private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
        private static readonly string[] SlotNames =
        [
            "Global\\SideDockGpuFrameSlot0",
            "Global\\SideDockGpuFrameSlot1",
            "Global\\SideDockGpuFrameSlot2",
            "Global\\SideDockGpuFrameSlot3",
            "Global\\SideDockGpuFrameSlot4",
            "Global\\SideDockGpuFrameSlot5",
            "Global\\SideDockGpuFrameSlot6",
            "Global\\SideDockGpuFrameSlot7",
            "Global\\SideDockGpuFrameSlot8",
            "Global\\SideDockGpuFrameSlot9",
            "Global\\SideDockGpuFrameSlot10",
            "Global\\SideDockGpuFrameSlot11"
        ];

        private readonly HostOptions _options;
        private readonly Action<string> _log;
        private MemoryMappedFile? _mapping;
        private MemoryMappedViewAccessor? _view;
        private EventWaitHandle? _frameReady;
        private EventWaitHandle? _consumerAlive;
        private IDXGIFactory1? _factory;
        private IDXGIAdapter1? _adapter;
        private ID3D11Device1? _device1;
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private readonly ID3D11Texture2D?[] _textures = new ID3D11Texture2D?[SlotCountMax];
        private readonly IDXGIKeyedMutex?[] _mutexes = new IDXGIKeyedMutex?[SlotCountMax];
        private long _lastSeq;
        private int _generation = -1;
        private int _slotCount;
        private int _width;
        private int _height;
        private long _lastFrameStopwatchTicks;
        private long _framesCaptured;
        private long _framesDropped;
        private long _staleSlotsReleased;
        private long _staleSlotReleaseFailures;

        public IddGpuFrameSource(HostOptions options, Action<string> log)
        {
            _options = options;
            _log = log;
        }

        public string SourceName => "idd-gpu";

        public string SourceDescription => "SideDock IddCx virtual display GPU texture ring";

        public ID3D11Device Device => _device ?? throw new InvalidOperationException("Idd GPU device has not started.");

        public ID3D11DeviceContext Context => _context ?? throw new InvalidOperationException("Idd GPU device context has not started.");

        public int Width => _width;

        public int Height => _height;

        public int SlotCount => _slotCount;

        public long FramesDropped => Interlocked.Read(ref _framesDropped);

        public double LastFrameAgeMs
        {
            get
            {
                var lastFrameTicks = Interlocked.Read(ref _lastFrameStopwatchTicks);
                if (lastFrameTicks == 0)
                {
                    return 0;
                }

                return (Stopwatch.GetTimestamp() - lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
            }
        }

        public void Start(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Idd GPU frame capture is only available on Windows.");
            }

            var waitStartedAt = Stopwatch.StartNew();
            var nextLogAt = TimeSpan.Zero;
            while (!TryOpenSharedObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (waitStartedAt.Elapsed >= StartupTimeout)
                {
                    throw new CaptureException("IDD_GPU_SHARED_OBJECTS_UNAVAILABLE", "Timed out waiting for Idd GPU shared texture ring.");
                }

                if (waitStartedAt.Elapsed >= nextLogAt)
                {
                    _log("waiting for Idd GPU shared texture ring...");
                    nextLogAt = waitStartedAt.Elapsed + TimeSpan.FromSeconds(2);
                }

                if (cancellationToken.WaitHandle.WaitOne(250))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            var consumerAlive = _consumerAlive ?? throw new InvalidOperationException("Idd GPU consumer-alive event has not opened.");
            consumerAlive.Set();

            var metadata = WaitForReadyMetadata(waitStartedAt, cancellationToken);
            _lastFrameStopwatchTicks = Stopwatch.GetTimestamp();
            _log($"connected GPU texture ring width={metadata.Width} height={metadata.Height} slots={metadata.SlotCount} generation={metadata.Generation} adapterLuid=0x{metadata.AdapterLuidHigh:X8}{metadata.AdapterLuidLow:X8}");
        }

        public GpuFrameLease AcquireLatestFrame(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameReady = _frameReady ?? throw new InvalidOperationException("Idd GPU frame-ready event has not opened.");

            var waitMilliseconds = GetFrameReadyWaitMilliseconds();
            var signaled = frameReady.WaitOne(waitMilliseconds);

            var metadata = ReadMetadata();
            ValidateMetadata(metadata);
            if (metadata.Generation != _generation || metadata.Width != _width || metadata.Height != _height || metadata.SlotCount != _slotCount)
            {
                OpenDeviceAndTextures(metadata);
            }

            var previousSeq = Volatile.Read(ref _lastSeq);
            var slotIndex = FindLatestReadableSlot(metadata, previousSeq);
            if (slotIndex < 0 || slotIndex >= _slotCount)
            {
                if (!signaled)
                {
                    throw new CaptureException("IDD_GPU_FRAME_TIMEOUT", "Timed out waiting for an Idd GPU frame.");
                }

                throw new CaptureException("IDD_GPU_FRAME_STALE", "Idd GPU did not publish a newer frame.");
            }

            return AcquireSlotFrame(metadata, previousSeq, slotIndex, waitMilliseconds);
        }

        private int GetFrameReadyWaitMilliseconds()
        {
            return Math.Clamp(1000 / Math.Max(1, _options.VideoFps), 1, FrameReadyTimeoutMs);
        }

        public bool TryAcquireLatestFrame(out GpuFrameLease? frame)
        {
            frame = null;
            var metadata = ReadMetadata();
            ValidateMetadata(metadata);
            if (metadata.Generation != _generation || metadata.Width != _width || metadata.Height != _height || metadata.SlotCount != _slotCount)
            {
                OpenDeviceAndTextures(metadata);
            }

            var previousSeq = Volatile.Read(ref _lastSeq);
            var slotIndex = FindLatestReadableSlot(metadata, previousSeq);
            if (slotIndex < 0 || slotIndex >= _slotCount)
            {
                return false;
            }

            frame = AcquireSlotFrame(metadata, previousSeq, slotIndex, PollFrameReadyTimeoutMs);
            return true;
        }

        private int FindLatestReadableSlot(IddGpuFrameMetadata metadata, long previousSeq)
        {
            var bestSlotIndex = -1;
            var bestSeq = previousSeq;
            for (var slotIndex = 0; slotIndex < _slotCount; slotIndex++)
            {
                var slot = ReadSlot(slotIndex);
                if (slot.Seq <= bestSeq ||
                    slot.Width != metadata.Width ||
                    slot.Height != metadata.Height ||
                    slot.Format != FrameFormatBgra ||
                    slot.State == 0)
                {
                    continue;
                }

                bestSeq = slot.Seq;
                bestSlotIndex = slotIndex;
            }

            return bestSlotIndex;
        }

        private GpuFrameLease AcquireSlotFrame(IddGpuFrameMetadata metadata, long previousSeq, int slotIndex, int acquireTimeoutMs)
        {
            var slot = ReadSlot(slotIndex);
            if (slot.Seq <= previousSeq)
            {
                Interlocked.Increment(ref _framesDropped);
                throw new CaptureException("IDD_GPU_FRAME_STALE", "Idd GPU did not publish a newer frame.");
            }

            if (slot.Width != metadata.Width || slot.Height != metadata.Height || slot.Format != FrameFormatBgra || slot.State == 0)
            {
                Interlocked.Increment(ref _framesDropped);
                throw new CaptureException("IDD_GPU_SLOT_LAYOUT_INVALID", $"Invalid Idd GPU slot layout {slot.Width}x{slot.Height} format={slot.Format} state={slot.State}.");
            }

            var mutex = _mutexes[slotIndex] ?? throw new InvalidOperationException($"Idd GPU slot {slotIndex} mutex has not opened.");
            var texture = _textures[slotIndex] ?? throw new InvalidOperationException($"Idd GPU slot {slotIndex} texture has not opened.");
            var context = _context ?? throw new InvalidOperationException("Idd GPU device context has not started.");
            ID3D11Texture2D? frameTexture = null;
            var acquireHr = KeyedMutexNative.AcquireSync(mutex, 1, acquireTimeoutMs);
            if (acquireHr < 0)
            {
                Interlocked.Increment(ref _framesDropped);
                throw new CaptureException("IDD_GPU_SLOT_BUSY", $"Timed out acquiring Idd GPU slot {slotIndex}: HRESULT=0x{acquireHr:X8}.");
            }

            try
            {
                frameTexture = CreateFrameCopyTexture(texture);
                context.CopyResource(frameTexture, texture);
                context.Flush();
            }
            catch
            {
                frameTexture?.Dispose();
                throw;
            }
            finally
            {
                ReleaseSlotMutex(mutex, slotIndex, slot.Seq);
            }

            var releasedSkipped = ReleaseSkippedSlots(metadata, previousSeq, slot.Seq, slotIndex);
            var skipped = slot.Seq - previousSeq - 1;
            if (skipped > 0)
            {
                Interlocked.Add(ref _framesDropped, skipped);
            }

            if (releasedSkipped > skipped)
            {
                Interlocked.Add(ref _framesDropped, releasedSkipped - skipped);
            }

            Volatile.Write(ref _lastSeq, slot.Seq);
            Interlocked.Increment(ref _framesCaptured);
            _lastFrameStopwatchTicks = Stopwatch.GetTimestamp();
            if ((_framesCaptured % 300) == 0)
            {
                _log($"gpu frames captured={_framesCaptured} dropped={_framesDropped} seq={slot.Seq} timestampQpc={slot.TimestampQpc}");
            }

            var ownedTexture = frameTexture;
            frameTexture = null;
            return new GpuFrameLease(ownedTexture, slot.Seq, slot.TimestampQpc, () => ownedTexture.Dispose());
        }

        public void Dispose()
        {
            try
            {
                _consumerAlive?.Reset();
            }
            catch (ObjectDisposedException)
            {
            }

            CloseSharedObjects();
            CloseTextures();
            _context?.Dispose();
            _context = null;
            _device1?.Dispose();
            _device1 = null;
            _device?.Dispose();
            _device = null;
            _adapter?.Dispose();
            _adapter = null;
            _factory?.Dispose();
            _factory = null;
        }

        [SupportedOSPlatform("windows")]
        private bool TryOpenSharedObjects()
        {
            try
            {
                _mapping = MemoryMappedFile.OpenExisting(MetadataName, MemoryMappedFileRights.Read);
                _view = _mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                _frameReady = EventWaitHandle.OpenExisting(FrameReadyName);
                _consumerAlive = EventWaitHandle.OpenExisting(ConsumerAliveName);
                return true;
            }
            catch (FileNotFoundException)
            {
                CloseSharedObjects();
                return false;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                CloseSharedObjects();
                return false;
            }
        }

        private void CloseSharedObjects()
        {
            _consumerAlive?.Dispose();
            _consumerAlive = null;
            _frameReady?.Dispose();
            _frameReady = null;
            _view?.Dispose();
            _view = null;
            _mapping?.Dispose();
            _mapping = null;
        }

        private IddGpuFrameMetadata ReadMetadata()
        {
            var view = _view ?? throw new InvalidOperationException("Idd GPU metadata is not open.");
            return new IddGpuFrameMetadata(
                Magic: view.ReadUInt32(0),
                Version: view.ReadInt32(4),
                Width: view.ReadInt32(8),
                Height: view.ReadInt32(12),
                Format: view.ReadInt32(16),
                SlotCount: view.ReadInt32(20),
                LatestSlot: view.ReadInt32(24),
                Generation: view.ReadInt32(28),
                WriteSeq: view.ReadInt64(32),
                TimestampQpc: view.ReadInt64(40),
                AdapterLuidLow: view.ReadUInt32(48),
                AdapterLuidHigh: view.ReadInt32(52),
                FrameDuration100ns: view.ReadInt64(56),
                ModeRefreshHz: view.ReadInt32(64),
                Flags: view.ReadInt32(68));
        }

        private IddGpuFrameSlot ReadSlot(int slotIndex)
        {
            var view = _view ?? throw new InvalidOperationException("Idd GPU metadata is not open.");
            var offset = MetadataHeaderSize + slotIndex * SlotHeaderSize;
            return new IddGpuFrameSlot(
                Seq: view.ReadInt64(offset),
                TimestampQpc: view.ReadInt64(offset + 8),
                Width: view.ReadInt32(offset + 16),
                Height: view.ReadInt32(offset + 20),
                Format: view.ReadInt32(offset + 24),
                State: view.ReadInt32(offset + 28));
        }

        private void ValidateMetadata(IddGpuFrameMetadata metadata)
        {
            if (metadata.Magic != FrameMagic)
            {
                throw new CaptureException("IDD_GPU_FRAME_MAGIC_INVALID", $"Unexpected Idd GPU frame magic 0x{metadata.Magic:X8}.");
            }

            if (metadata.Version != FrameVersion)
            {
                throw new CaptureException("IDD_GPU_FRAME_VERSION_UNSUPPORTED", $"Unsupported Idd GPU frame version {metadata.Version}.");
            }

            if (metadata.Width != _options.VideoWidth || metadata.Height != _options.VideoHeight)
            {
                throw new CaptureException("IDD_GPU_FRAME_SIZE_UNSUPPORTED", $"Idd GPU frame size {metadata.Width}x{metadata.Height} does not match requested {_options.VideoWidth}x{_options.VideoHeight}.");
            }

            if (metadata.Format != FrameFormatBgra)
            {
                throw new CaptureException("IDD_GPU_FRAME_FORMAT_UNSUPPORTED", $"Unsupported Idd GPU frame format {metadata.Format}.");
            }

            if (metadata.SlotCount <= 0 || metadata.SlotCount > SlotCountMax || metadata.LatestSlot < 0 || metadata.LatestSlot >= metadata.SlotCount)
            {
                throw new CaptureException("IDD_GPU_FRAME_LAYOUT_INVALID", $"Invalid Idd GPU frame layout slots={metadata.SlotCount} latest={metadata.LatestSlot}.");
            }
        }

        private void OpenDeviceAndTextures(IddGpuFrameMetadata metadata)
        {
            CloseTextures();
            if (_device is null || _context is null || _device1 is null || _adapter is null || !AdapterMatches(metadata))
            {
                _context?.Dispose();
                _context = null;
                _device1?.Dispose();
                _device1 = null;
                _device?.Dispose();
                _device = null;
                _adapter?.Dispose();
                _adapter = null;
                _factory?.Dispose();

                _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                _adapter = FindAdapter(_factory, metadata);
                var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
                var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
                D3D11.D3D11CreateDevice(
                    _adapter,
                    DriverType.Unknown,
                    flags,
                    featureLevels,
                    out _device,
                    out _,
                    out _context).CheckError();
                _device1 = _device.QueryInterface<ID3D11Device1>();
                EnableMultithreadProtection(_device);
            }

            for (var slotIndex = 0; slotIndex < metadata.SlotCount; slotIndex++)
            {
                var texture = _device1.OpenSharedResourceByName<ID3D11Texture2D>(
                    SlotNames[slotIndex],
                    Vortice.Direct3D11.SharedResourceFlags.Read | Vortice.Direct3D11.SharedResourceFlags.Write);
                var description = texture.Description;
                if (description.Width != metadata.Width || description.Height != metadata.Height || description.Format != Format.B8G8R8A8_UNorm)
                {
                    texture.Dispose();
                    throw new CaptureException("IDD_GPU_TEXTURE_LAYOUT_INVALID", $"Invalid Idd GPU texture layout slot={slotIndex} {description.Width}x{description.Height} format={description.Format}.");
                }

                _textures[slotIndex] = texture;
                _mutexes[slotIndex] = texture.QueryInterface<IDXGIKeyedMutex>();
            }

            _width = metadata.Width;
            _height = metadata.Height;
            _slotCount = metadata.SlotCount;
            _generation = metadata.Generation;
            Volatile.Write(ref _lastSeq, 0);
        }

        private bool AdapterMatches(IddGpuFrameMetadata metadata)
        {
            if (_adapter is null)
            {
                return false;
            }

            var luid = _adapter.Description1.Luid;
            return luid.LowPart == metadata.AdapterLuidLow && luid.HighPart == metadata.AdapterLuidHigh;
        }

        private static IDXGIAdapter1 FindAdapter(IDXGIFactory1 factory, IddGpuFrameMetadata metadata)
        {
            for (uint index = 0; ; index++)
            {
                var result = factory.EnumAdapters1(index, out var adapter);
                if (result.Failure)
                {
                    break;
                }

                var luid = adapter.Description1.Luid;
                if (luid.LowPart == metadata.AdapterLuidLow && luid.HighPart == metadata.AdapterLuidHigh)
                {
                    return adapter;
                }

                adapter.Dispose();
            }

            throw new CaptureException("IDD_GPU_ADAPTER_NOT_FOUND", $"Unable to find DXGI adapter LUID=0x{metadata.AdapterLuidHigh:X8}{metadata.AdapterLuidLow:X8}.");
        }

        private void EnableMultithreadProtection(ID3D11Device device)
        {
            try
            {
                using var multithread = device.QueryInterface<ID3D11Multithread>();
                multithread.SetMultithreadProtected(true);
                _log("enabled D3D11 multithread protection");
            }
            catch (SharpGenException ex)
            {
                _log($"D3D11 multithread protection unavailable: {ex.Message}");
            }
        }

        private ID3D11Texture2D CreateFrameCopyTexture(ID3D11Texture2D sourceTexture)
        {
            var device = _device ?? throw new InvalidOperationException("Idd GPU device has not started.");
            var description = sourceTexture.Description;
            description.Usage = ResourceUsage.Default;
            description.BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget;
            description.CPUAccessFlags = CpuAccessFlags.None;
            description.MiscFlags = ResourceOptionFlags.None;
            return device.CreateTexture2D(in description);
        }

        private void ReleaseSlotMutex(IDXGIKeyedMutex mutex, int slotIndex, long sequence)
        {
            var hr = KeyedMutexNative.ReleaseSync(mutex, 0);
            if (hr < 0)
            {
                Interlocked.Increment(ref _staleSlotReleaseFailures);
                _log($"gpu slot release ignored slot={slotIndex} seq={sequence}: HRESULT=0x{hr:X8}");
            }
        }

        private int ReleaseSkippedSlots(IddGpuFrameMetadata metadata, long previousSeq, long currentSeq, int currentSlotIndex)
        {
            var released = 0;
            var failures = 0;
            for (var slotIndex = 0; slotIndex < _slotCount; slotIndex++)
            {
                if (slotIndex == currentSlotIndex)
                {
                    continue;
                }

                var slot = ReadSlot(slotIndex);
                if (slot.Seq <= previousSeq ||
                    slot.Seq >= currentSeq ||
                    slot.Width != metadata.Width ||
                    slot.Height != metadata.Height ||
                    slot.Format != FrameFormatBgra ||
                    slot.State == 0)
                {
                    continue;
                }

                var mutex = _mutexes[slotIndex];
                if (mutex is null)
                {
                    continue;
                }

                var acquireHr = KeyedMutexNative.AcquireSync(mutex, 1, 0);
                if (acquireHr < 0)
                {
                    if (acquireHr != DxgiErrorWaitTimeout)
                    {
                        failures++;
                    }

                    continue;
                }

                var releaseHr = KeyedMutexNative.ReleaseSync(mutex, 0);
                if (releaseHr < 0)
                {
                    failures++;
                    continue;
                }

                released++;
            }

            if (released > 0)
            {
                var total = Interlocked.Add(ref _staleSlotsReleased, released);
                if ((total % 300) < released)
                {
                    _log($"gpu skipped slots released total={total}");
                }
            }

            if (failures > 0)
            {
                var total = Interlocked.Add(ref _staleSlotReleaseFailures, failures);
                if ((total % 300) < failures)
                {
                    _log($"gpu skipped slot release failures total={total}");
                }
            }

            return released;
        }

        private IddGpuFrameMetadata WaitForReadyMetadata(Stopwatch waitStartedAt, CancellationToken cancellationToken)
        {
            CaptureException? lastError = null;
            var nextLogAt = TimeSpan.Zero;
            while (waitStartedAt.Elapsed < StartupTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var metadata = ReadMetadata();
                    ValidateMetadata(metadata);
                    OpenDeviceAndTextures(metadata);
                    return metadata;
                }
                catch (CaptureException ex) when (IsStartupTransient(ex.Code))
                {
                    lastError = ex;
                    if (waitStartedAt.Elapsed >= nextLogAt)
                    {
                        _log($"waiting for initialized Idd GPU frame ring: {ex.Message}");
                        nextLogAt = waitStartedAt.Elapsed + TimeSpan.FromSeconds(1);
                    }

                    var frameReady = _frameReady;
                    if (frameReady is not null)
                    {
                        frameReady.WaitOne(250);
                    }
                    else if (cancellationToken.WaitHandle.WaitOne(250))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
            }

            throw lastError ?? new CaptureException("IDD_GPU_FRAME_TIMEOUT", "Timed out waiting for initialized Idd GPU frame ring.");
        }

        private static bool IsStartupTransient(string code)
        {
            return code is
                "IDD_GPU_FRAME_MAGIC_INVALID" or
                "IDD_GPU_FRAME_SIZE_UNSUPPORTED" or
                "IDD_GPU_FRAME_LAYOUT_INVALID" or
                "IDD_GPU_TEXTURE_OPEN_FAILED" or
                "IDD_GPU_TEXTURE_LAYOUT_INVALID";
        }

        private void CloseTextures()
        {
            for (var index = 0; index < SlotCountMax; index++)
            {
                _mutexes[index]?.Dispose();
                _mutexes[index] = null;
                _textures[index]?.Dispose();
                _textures[index] = null;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class MediaFoundationH264Encoder : IDisposable
    {
        private readonly HostOptions _options;
        private readonly ID3D11Device? _d3dDevice;
        private IMFTransform? _transform;
        private IMFMediaEventGenerator? _eventGenerator;
        private IMFDXGIDeviceManager? _dxgiDeviceManager;
        private uint _dxgiResetToken;
        private int _outputBufferSize;
        private bool _outputProvidesSamples;
        private const int AsyncMftInputWaitMs = 1000;
        private bool _asyncMft;
        private bool _asyncMftNeedsInput;
        private bool _started;
        private bool _mfStarted;
        private bool _comInitialized;
        private int _nalLengthSize = 4;
        private byte[]? _parameterSets;

        public string SelectedMftName { get; private set; } = string.Empty;

        public MediaFoundationH264Encoder(HostOptions options, ID3D11Device? d3dDevice = null)
        {
            _options = options;
            _d3dDevice = d3dDevice;
            if (_options.VideoWidth <= 0 || _options.VideoHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Video dimensions must be positive.");
            }

            if ((_options.VideoWidth & 1) != 0 || (_options.VideoHeight & 1) != 0)
            {
                throw new ArgumentException("Media Foundation NV12 input requires even video dimensions.", nameof(options));
            }
        }

        public void Start()
        {
            if (_started)
            {
                return;
            }

            var coHr = Native.CoInitializeEx(IntPtr.Zero, Native.COINIT_MULTITHREADED);
            if (coHr == Native.S_OK || coHr == Native.S_FALSE)
            {
                _comInitialized = true;
            }
            else if (coHr != Native.RPC_E_CHANGED_MODE)
            {
                ThrowIfFailed(coHr, "CoInitializeEx failed.");
            }

            ThrowIfFailed(Native.MFStartup(Native.MF_VERSION, Native.MFSTARTUP_FULL), "MFStartup failed.");
            _mfStarted = true;

            CreateAndConfigureTransform();

            var transform = GetTransform();
            ThrowIfFailed(transform.ProcessMessage(Native.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero), "Unable to begin Media Foundation streaming.");
            ThrowIfFailed(transform.ProcessMessage(Native.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero), "Unable to start Media Foundation stream.");

            RefreshOutputInfo();
            RefreshParameterSets();
            _started = true;
        }

        public IReadOnlyList<byte[]> EncodeFrame(byte[] nv12Frame, long frameId)
        {
            if (!_started)
            {
                throw new InvalidOperationException("Media Foundation encoder has not started.");
            }

            var expectedLength = GetNv12FrameSize(_options.VideoWidth, _options.VideoHeight);
            if (nv12Frame.Length < expectedLength)
            {
                throw new ArgumentException("NV12 frame buffer is smaller than the configured video dimensions.", nameof(nv12Frame));
            }

            var transform = GetTransform();
            using var inputSample = CreateInputSample(nv12Frame, expectedLength, frameId);
            if (_asyncMft)
            {
                return EncodeAsyncMftFrame(transform, inputSample.Sample);
            }

            var hr = transform.ProcessInput(0, inputSample.Sample, 0);
            if (hr == Native.MF_E_NOTACCEPTING)
            {
                var drained = DrainOutput(transform);
                ThrowIfFailed(transform.ProcessInput(0, inputSample.Sample, 0), "Media Foundation encoder did not accept input after draining output.");
                var encoded = DrainOutput(transform);
                if (drained.Count == 0)
                {
                    return encoded;
                }

                drained.AddRange(encoded);
                return drained;
            }

            ThrowIfFailed(hr, "Media Foundation encoder rejected an input frame.");
            return DrainOutput(transform);
        }

        public IReadOnlyList<byte[]> EncodeFrame(ID3D11Texture2D nv12Texture, long frameId)
        {
            if (!_started)
            {
                throw new InvalidOperationException("Media Foundation encoder has not started.");
            }

            if (_d3dDevice is null)
            {
                throw new InvalidOperationException("D3D11 surface encoding requires a D3D11 device.");
            }

            var description = nv12Texture.Description;
            if (description.Width != _options.VideoWidth || description.Height != _options.VideoHeight || description.Format != Format.NV12)
            {
                throw new ArgumentException("D3D11 input texture must be an NV12 texture matching the configured video dimensions.", nameof(nv12Texture));
            }

            var transform = GetTransform();
            using var inputSample = CreateInputSample(nv12Texture, frameId);
            if (_asyncMft)
            {
                return EncodeAsyncMftFrame(transform, inputSample.Sample);
            }

            var hr = transform.ProcessInput(0, inputSample.Sample, 0);
            if (hr == Native.MF_E_NOTACCEPTING)
            {
                var drained = DrainOutput(transform);
                ThrowIfFailed(transform.ProcessInput(0, inputSample.Sample, 0), "Media Foundation GPU encoder did not accept input after draining output.");
                var encoded = DrainOutput(transform);
                if (drained.Count == 0)
                {
                    return encoded;
                }

                drained.AddRange(encoded);
                return drained;
            }

            ThrowIfFailed(hr, "Media Foundation GPU encoder rejected an input frame.");
            return DrainOutput(transform);
        }

        public void Dispose()
        {
            _eventGenerator = null;

            if (_transform is not null)
            {
                try
                {
                    _transform.ProcessMessage(Native.MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero);
                    _transform.ProcessMessage(Native.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero);
                }
                catch (COMException)
                {
                    // Best-effort shutdown.
                }

                ReleaseComObject(_transform);
                _transform = null;
            }

            if (_dxgiDeviceManager is not null)
            {
                ReleaseComObject(_dxgiDeviceManager);
                _dxgiDeviceManager = null;
            }

            if (_mfStarted)
            {
                Native.MFShutdown();
                _mfStarted = false;
            }

            if (_comInitialized)
            {
                Native.CoUninitialize();
                _comInitialized = false;
            }
        }

        public static int GetNv12FrameSize(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "Video dimensions must be positive.");
            }

            if ((width & 1) != 0 || (height & 1) != 0)
            {
                throw new ArgumentException("NV12 requires even width and height.");
            }

            return checked(width * height * 3 / 2);
        }

        public static void ConvertBgraToNv12(byte[] bgra, byte[] nv12, int width, int height)
        {
            var bgraLength = checked(width * height * 4);
            var nv12Length = GetNv12FrameSize(width, height);
            if (bgra.Length < bgraLength)
            {
                throw new ArgumentException("BGRA frame buffer is smaller than the configured video dimensions.", nameof(bgra));
            }

            if (nv12.Length < nv12Length)
            {
                throw new ArgumentException("NV12 frame buffer is smaller than the configured video dimensions.", nameof(nv12));
            }

            var yPlaneSize = width * height;
            for (var y = 0; y < height; y++)
            {
                var yOffset = y * width;
                var bgraOffset = yOffset * 4;
                for (var x = 0; x < width; x++)
                {
                    var b = bgra[bgraOffset];
                    var g = bgra[bgraOffset + 1];
                    var r = bgra[bgraOffset + 2];
                    nv12[yOffset + x] = ClampToByte(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                    bgraOffset += 4;
                }
            }

            for (var y = 0; y < height; y += 2)
            {
                var uvOffset = yPlaneSize + (y / 2) * width;
                for (var x = 0; x < width; x += 2)
                {
                    var r = 0;
                    var g = 0;
                    var b = 0;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        var bgraOffset = ((y + dy) * width + x) * 4;
                        for (var dx = 0; dx < 2; dx++)
                        {
                            b += bgra[bgraOffset];
                            g += bgra[bgraOffset + 1];
                            r += bgra[bgraOffset + 2];
                            bgraOffset += 4;
                        }
                    }

                    r /= 4;
                    g /= 4;
                    b /= 4;
                    nv12[uvOffset + x] = ClampToByte(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    nv12[uvOffset + x + 1] = ClampToByte(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            }
        }

        private void CreateAndConfigureTransform()
        {
            var failures = new List<string>();
            foreach (var candidate in EnumerateH264EncoderCandidates())
            {
                try
                {
                    CreateTransform(candidate);
                    ConfigureAsyncUnlock();
                    ConfigureCodecApi();
                    ConfigureD3DManager();
                    ConfigureMediaTypes();
                    SelectedMftName = candidate.Name;
                    return;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    failures.Add($"{candidate.Name}: {FormatComFailure(ex)}");
                    ReleaseActiveTransform();
                }
            }

            throw new InvalidOperationException($"Unable to initialize an H.264 Media Foundation encoder. {string.Join("; ", failures)}");
        }

        private void CreateTransform(H264EncoderMftCandidate candidate)
        {
            var clsid = candidate.Clsid;
            var iid = Native.IID_IMFTransform;
            ThrowIfFailed(
                Native.CoCreateInstance(ref clsid, IntPtr.Zero, Native.CLSCTX_INPROC_SERVER, ref iid, out _transform),
                $"Unable to create H.264 Media Foundation encoder MFT '{candidate.Name}'.");
        }

        private void ConfigureAsyncUnlock()
        {
            if (_transform is null)
            {
                return;
            }

            var hr = _transform.GetAttributes(out var attributes);
            if (hr < 0 || attributes is null)
            {
                return;
            }

            try
            {
                var asyncAttribute = Native.MF_TRANSFORM_ASYNC;
                _asyncMft = attributes.GetUINT32(ref asyncAttribute, out var asyncValue) >= 0 && asyncValue != 0;
                if (_asyncMft)
                {
                    SetUInt32(attributes, Native.MF_TRANSFORM_ASYNC_UNLOCK, 1);
                }
            }
            finally
            {
                ReleaseComObject(attributes);
            }
        }

        private IReadOnlyList<H264EncoderMftCandidate> EnumerateH264EncoderCandidates()
        {
            var candidates = new List<H264EncoderMftCandidate>();
            var category = Native.MFT_CATEGORY_VIDEO_ENCODER;
            var outputType = new MFTRegisterTypeInfo
            {
                MajorType = Native.MFMediaType_Video,
                Subtype = Native.MFVideoFormat_H264
            };
            var flags = Native.MFT_ENUM_FLAG_SYNCMFT
                | Native.MFT_ENUM_FLAG_ASYNCMFT
                | Native.MFT_ENUM_FLAG_HARDWARE
                | Native.MFT_ENUM_FLAG_SORTANDFILTER;

            var hr = Native.MFTEnumEx(ref category, flags, IntPtr.Zero, ref outputType, out var activateArray, out var count);
            if (hr >= 0 && activateArray != IntPtr.Zero)
            {
                try
                {
                    for (var index = 0; index < count; index++)
                    {
                        var activatePtr = Marshal.ReadIntPtr(activateArray, index * IntPtr.Size);
                        if (activatePtr == IntPtr.Zero)
                        {
                            continue;
                        }

                        IMFAttributes? attributes = null;
                        try
                        {
                            attributes = (IMFAttributes)Marshal.GetObjectForIUnknown(activatePtr);
                            var name = GetStringAttribute(attributes, Native.MFT_FRIENDLY_NAME_Attribute) ?? $"H.264 encoder MFT {index}";
                            var clsid = GetGuidAttribute(attributes, Native.MFT_TRANSFORM_CLSID_Attribute);
                            if (clsid != Guid.Empty)
                            {
                                candidates.Add(new H264EncoderMftCandidate(name, clsid, index));
                            }
                        }
                        finally
                        {
                            ReleaseComObject(attributes);
                            Marshal.Release(activatePtr);
                        }
                    }
                }
                finally
                {
                    Native.CoTaskMemFree(activateArray);
                }
            }

            if (!candidates.Any(candidate => candidate.Clsid == Native.CLSID_CMSH264EncoderMFT))
            {
                candidates.Add(new H264EncoderMftCandidate("H264 Encoder MFT", Native.CLSID_CMSH264EncoderMFT, int.MaxValue));
            }

            return candidates
                .OrderByDescending(ScoreEncoderCandidate)
                .ThenBy(candidate => candidate.OriginalIndex)
                .ToArray();
        }

        private int ScoreEncoderCandidate(H264EncoderMftCandidate candidate)
        {
            var name = candidate.Name;
            if (_d3dDevice is null)
            {
                return candidate.Clsid == Native.CLSID_CMSH264EncoderMFT ? 100 : 10;
            }

            if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            {
                return 200;
            }

            return candidate.Clsid == Native.CLSID_CMSH264EncoderMFT ? 0 : 100;
        }

        private void ReleaseActiveTransform()
        {
            if (_dxgiDeviceManager is not null)
            {
                ReleaseComObject(_dxgiDeviceManager);
                _dxgiDeviceManager = null;
            }

            if (_transform is not null)
            {
                ReleaseComObject(_transform);
                _transform = null;
            }

            _dxgiResetToken = 0;
            _outputBufferSize = 0;
            _outputProvidesSamples = false;
            _asyncMft = false;
            _asyncMftNeedsInput = false;
            _eventGenerator = null;
            _nalLengthSize = 4;
            _parameterSets = null;
            SelectedMftName = string.Empty;
        }

        private static string? GetStringAttribute(IMFAttributes attributes, Guid key)
        {
            var attribute = key;
            if (attributes.GetStringLength(ref attribute, out var length) < 0 || length <= 0)
            {
                return null;
            }

            var value = new StringBuilder(length + 1);
            attribute = key;
            return attributes.GetString(ref attribute, value, value.Capacity, out _) < 0
                ? null
                : value.ToString();
        }

        private static Guid GetGuidAttribute(IMFAttributes attributes, Guid key)
        {
            var attribute = key;
            return attributes.GetGUID(ref attribute, out var value) < 0 ? Guid.Empty : value;
        }

        private static string FormatComFailure(Exception ex)
        {
            var message = ex switch
            {
                COMException comException => $"HRESULT=0x{comException.HResult:X8}: {comException.Message}",
                _ => ex.Message
            };

            return message.ReplaceLineEndings(" ");
        }

        private void ConfigureMediaTypes()
        {
            var transform = GetTransform();

            using var outputType = CreateOutputMediaType(includeProfile: true);
            var hr = transform.SetOutputType(0, outputType.MediaType, 0);
            if (hr == Native.MF_E_INVALIDMEDIATYPE)
            {
                using var fallbackOutputType = CreateOutputMediaType(includeProfile: false);
                ThrowIfFailed(transform.SetOutputType(0, fallbackOutputType.MediaType, 0), "Media Foundation H.264 output type is not supported.");
            }
            else
            {
                ThrowIfFailed(hr, "Unable to set Media Foundation H.264 output type.");
            }

            using var inputType = CreateInputMediaType();
            ThrowIfFailed(transform.SetInputType(0, inputType.MediaType, 0), "Unable to set Media Foundation NV12 input type.");

            if (_asyncMft)
            {
                _eventGenerator = (IMFMediaEventGenerator)transform;
            }
        }

        private MediaTypeHandle CreateOutputMediaType(bool includeProfile)
        {
            ThrowIfFailed(Native.MFCreateMediaType(out var mediaType), "Unable to create Media Foundation output media type.");
            var attributes = (IMFAttributes)mediaType;
            var tuning = _options.EncoderTuning;

            SetGuid(attributes, Native.MF_MT_MAJOR_TYPE, Native.MFMediaType_Video);
            SetGuid(attributes, Native.MF_MT_SUBTYPE, Native.MFVideoFormat_H264);
            SetUInt32(attributes, Native.MF_MT_AVG_BITRATE, tuning.Bitrate);
            SetUInt32(attributes, Native.MF_MT_INTERLACE_MODE, Native.MFVideoInterlace_Progressive);
            SetUInt32(attributes, Native.MF_MT_MAX_KEYFRAME_SPACING, tuning.Gop);
            SetUInt32(attributes, Native.MF_NALU_LENGTH_SET, 4);

            if (includeProfile)
            {
                SetUInt32(attributes, Native.MF_MT_MPEG2_PROFILE, tuning.ProfileValue);
            }

            SetPackedUInt32Pair(attributes, Native.MF_MT_FRAME_SIZE, _options.VideoWidth, _options.VideoHeight);
            SetPackedUInt32Pair(attributes, Native.MF_MT_FRAME_RATE, _options.VideoFps, 1);
            SetPackedUInt32Pair(attributes, Native.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            return new MediaTypeHandle(mediaType);
        }

        private MediaTypeHandle CreateInputMediaType()
        {
            ThrowIfFailed(Native.MFCreateMediaType(out var mediaType), "Unable to create Media Foundation input media type.");
            var attributes = (IMFAttributes)mediaType;

            SetGuid(attributes, Native.MF_MT_MAJOR_TYPE, Native.MFMediaType_Video);
            SetGuid(attributes, Native.MF_MT_SUBTYPE, Native.MFVideoFormat_NV12);
            SetUInt32(attributes, Native.MF_MT_INTERLACE_MODE, Native.MFVideoInterlace_Progressive);
            SetUInt32(attributes, Native.MF_MT_FIXED_SIZE_SAMPLES, 1);
            SetUInt32(attributes, Native.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            SetUInt32(attributes, Native.MF_MT_SAMPLE_SIZE, GetNv12FrameSize(_options.VideoWidth, _options.VideoHeight));
            SetPackedUInt32Pair(attributes, Native.MF_MT_FRAME_SIZE, _options.VideoWidth, _options.VideoHeight);
            SetPackedUInt32Pair(attributes, Native.MF_MT_FRAME_RATE, _options.VideoFps, 1);
            SetPackedUInt32Pair(attributes, Native.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            return new MediaTypeHandle(mediaType);
        }

        private void ConfigureCodecApi()
        {
            if (_transform is not ICodecAPI codecApi)
            {
                return;
            }

            var tuning = _options.EncoderTuning;
            TrySetCodecValue(codecApi, Native.CODECAPI_AVLowLatencyMode, Variant.FromBool(tuning.LowLatency));
            TrySetCodecValue(codecApi, Native.CODECAPI_AVEncCommonRateControlMode, Variant.FromUInt32(Native.eAVEncCommonRateControlMode_CBR));
            TrySetCodecValue(codecApi, Native.CODECAPI_AVEncCommonMeanBitRate, Variant.FromUInt32((uint)tuning.Bitrate));
            TrySetCodecValue(codecApi, Native.CODECAPI_AVEncMPVGOPSize, Variant.FromUInt32((uint)tuning.Gop));
            TrySetCodecValue(codecApi, Native.CODECAPI_AVEncMPVDefaultBPictureCount, Variant.FromUInt32((uint)tuning.BFrames));
            TrySetCodecValue(codecApi, Native.CODECAPI_AVEncMPVProfile, Variant.FromUInt32((uint)tuning.ProfileValue));
        }

        private void ConfigureD3DManager()
        {
            if (_d3dDevice is null)
            {
                return;
            }

            ThrowIfFailed(Native.MFCreateDXGIDeviceManager(out _dxgiResetToken, out _dxgiDeviceManager), "Unable to create Media Foundation DXGI device manager.");
            var deviceManager = _dxgiDeviceManager ?? throw new InvalidOperationException("Media Foundation DXGI device manager was not created.");
            var deviceUnknown = Marshal.GetObjectForIUnknown(_d3dDevice.NativePointer);
            try
            {
                ThrowIfFailed(deviceManager.ResetDevice(deviceUnknown, _dxgiResetToken), "Unable to reset Media Foundation DXGI device manager.");
            }
            finally
            {
                ReleaseComObject(deviceUnknown);
            }

            var managerUnknown = Marshal.GetIUnknownForObject(deviceManager);
            try
            {
                ThrowIfFailed(GetTransform().ProcessMessage(Native.MFT_MESSAGE_SET_D3D_MANAGER, managerUnknown), "Unable to attach DXGI device manager to Media Foundation encoder.");
            }
            finally
            {
                Marshal.Release(managerUnknown);
            }
        }

        private static void TrySetCodecValue(ICodecAPI codecApi, Guid key, Variant value)
        {
            var api = key;
            if (codecApi.IsSupported(ref api) < 0)
            {
                return;
            }

            codecApi.SetValue(ref api, ref value);
        }

        private InputSampleHandle CreateInputSample(byte[] nv12Frame, int length, long frameId)
        {
            ThrowIfFailed(Native.MFCreateSample(out var sample), "Unable to create Media Foundation input sample.");
            ThrowIfFailed(Native.MFCreateMemoryBuffer(length, out var buffer), "Unable to create Media Foundation input buffer.");

            IntPtr bufferPtr = IntPtr.Zero;
            try
            {
                ThrowIfFailed(buffer.Lock(out bufferPtr, out var maxLength, out _), "Unable to lock Media Foundation input buffer.");
                if (maxLength < length)
                {
                    throw new InvalidOperationException("Media Foundation input buffer is smaller than the NV12 frame.");
                }

                Marshal.Copy(nv12Frame, 0, bufferPtr, length);
            }
            finally
            {
                if (bufferPtr != IntPtr.Zero)
                {
                    buffer.Unlock();
                }
            }

            ThrowIfFailed(buffer.SetCurrentLength(length), "Unable to set Media Foundation input buffer length.");
            ThrowIfFailed(sample.AddBuffer(buffer), "Unable to attach input buffer to Media Foundation sample.");

            ApplyInputSampleTiming(sample, frameId);

            var gop = _options.EncoderTuning.Gop;
            if (frameId == 0 || (gop > 0 && frameId % gop == 0))
            {
                SetUInt32((IMFAttributes)sample, Native.MFSampleExtension_VideoEncodePictureType, Native.eAVEncH264PictureType_IDR);
                ForceNextKeyFrame();
            }

            return new InputSampleHandle(sample, buffer);
        }

        private InputSampleHandle CreateInputSample(ID3D11Texture2D nv12Texture, long frameId)
        {
            ThrowIfFailed(Native.MFCreateSample(out var sample), "Unable to create Media Foundation GPU input sample.");
            var surfaceUnknown = Marshal.GetObjectForIUnknown(nv12Texture.NativePointer);
            try
            {
                var textureGuid = Native.IID_ID3D11Texture2D;
                ThrowIfFailed(
                    Native.MFCreateDXGISurfaceBuffer(ref textureGuid, surfaceUnknown, 0, false, out var buffer),
                    "Unable to create Media Foundation DXGI surface buffer.");
                ThrowIfFailed(sample.AddBuffer(buffer), "Unable to attach GPU input buffer to Media Foundation sample.");
                ApplyInputSampleTiming(sample, frameId);

                var gop = _options.EncoderTuning.Gop;
                if (frameId == 0 || (gop > 0 && frameId % gop == 0))
                {
                    SetUInt32((IMFAttributes)sample, Native.MFSampleExtension_VideoEncodePictureType, Native.eAVEncH264PictureType_IDR);
                    ForceNextKeyFrame();
                }

                return new InputSampleHandle(sample, buffer);
            }
            finally
            {
                ReleaseComObject(surfaceUnknown);
            }
        }

        private void ApplyInputSampleTiming(IMFSample sample, long frameId)
        {
            var frameDuration = 10_000_000L / Math.Max(1, _options.VideoFps);
            ThrowIfFailed(sample.SetSampleTime(frameId * frameDuration), "Unable to set input sample time.");
            ThrowIfFailed(sample.SetSampleDuration(frameDuration), "Unable to set input sample duration.");
        }

        private void ForceNextKeyFrame()
        {
            if (_transform is not ICodecAPI codecApi)
            {
                return;
            }

            TrySetCodecValue(codecApi, Native.CODECAPI_AVEncVideoForceKeyFrame, Variant.FromBool(true));
        }

        private List<byte[]> EncodeAsyncMftFrame(IMFTransform transform, IMFSample sample)
        {
            var eventGenerator = _eventGenerator ?? throw new InvalidOperationException("Media Foundation async encoder event generator is not initialized.");
            var packets = new List<byte[]>();
            var inputSubmitted = false;
            var inputDeadline = Stopwatch.GetTimestamp() + MillisecondsToStopwatchTicks(AsyncMftInputWaitMs);

            while (!inputSubmitted)
            {
                DrainPendingAsyncMftEvents(transform, eventGenerator, sample, ref inputSubmitted, packets);
                if (inputSubmitted)
                {
                    break;
                }

                if (_asyncMftNeedsInput && TrySubmitAsyncMftInput(transform, sample))
                {
                    inputSubmitted = true;
                    break;
                }

                if (TrySubmitAsyncMftInput(transform, sample))
                {
                    inputSubmitted = true;
                    break;
                }

                if (Stopwatch.GetTimestamp() >= inputDeadline)
                {
                    throw new TimeoutException($"Media Foundation async encoder did not accept input within {AsyncMftInputWaitMs}ms.");
                }

                Thread.Sleep(1);
            }

            var outputDeadline = Stopwatch.GetTimestamp() + MillisecondsToStopwatchTicks(GetAsyncOutputWaitMilliseconds());
            while (true)
            {
                var processedEvent = DrainPendingAsyncMftEvents(transform, eventGenerator, null, ref inputSubmitted, packets);
                if (packets.Count > 0 || Stopwatch.GetTimestamp() >= outputDeadline)
                {
                    return packets;
                }

                if (!processedEvent)
                {
                    Thread.Sleep(1);
                }
            }
        }

        private bool DrainPendingAsyncMftEvents(
            IMFTransform transform,
            IMFMediaEventGenerator eventGenerator,
            IMFSample? pendingInputSample,
            ref bool inputSubmitted,
            List<byte[]> packets)
        {
            var processedEvent = false;
            while (TryGetAsyncMftEvent(eventGenerator, out var mediaEvent))
            {
                processedEvent = true;
                try
                {
                    ThrowIfFailed(mediaEvent.GetStatus(out var eventStatus), "Unable to read Media Foundation async encoder event status.");
                    ThrowIfFailed(eventStatus, "Media Foundation async encoder event reported failure.");
                    ThrowIfFailed(mediaEvent.GetType(out var eventType), "Unable to read Media Foundation async encoder event type.");

                    switch (eventType)
                    {
                        case Native.METransformNeedInput:
                            _asyncMftNeedsInput = true;
                            if (!inputSubmitted && pendingInputSample is not null && TrySubmitAsyncMftInput(transform, pendingInputSample))
                            {
                                inputSubmitted = true;
                            }

                            break;
                        case Native.METransformHaveOutput:
                            packets.AddRange(DrainOutput(transform, singleOutput: true));
                            break;
                    }
                }
                finally
                {
                    ReleaseComObject(mediaEvent);
                }
            }

            return processedEvent;
        }

        private bool TrySubmitAsyncMftInput(IMFTransform transform, IMFSample sample)
        {
            var hr = transform.ProcessInput(0, sample, 0);
            if (hr == Native.MF_E_NOTACCEPTING)
            {
                return false;
            }

            ThrowIfFailed(hr, "Media Foundation async encoder rejected an input frame.");
            _asyncMftNeedsInput = false;
            return true;
        }

        private static bool TryGetAsyncMftEvent(IMFMediaEventGenerator eventGenerator, out IMFMediaEvent mediaEvent)
        {
            var hr = eventGenerator.GetEvent(Native.MF_EVENT_FLAG_NO_WAIT, out mediaEvent);
            if (hr == Native.MF_E_NO_EVENTS_AVAILABLE)
            {
                mediaEvent = null!;
                return false;
            }

            ThrowIfFailed(hr, "Unable to read Media Foundation async encoder event.");
            return true;
        }

        private int GetAsyncOutputWaitMilliseconds()
        {
            return Math.Clamp(1000 / Math.Max(1, _options.VideoFps), 1, 8);
        }

        private static long MillisecondsToStopwatchTicks(int milliseconds)
        {
            return Math.Max(1, (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0));
        }

        private List<byte[]> DrainOutput(IMFTransform transform, bool singleOutput = false)
        {
            var packets = new List<byte[]>();

            while (true)
            {
                OutputSampleHandle? sampleHandle = null;
                var inputSamplePtr = IntPtr.Zero;
                var outputBufferPtr = IntPtr.Zero;
                var outputBuffer = default(MFTOutputDataBuffer);
                try
                {
                    sampleHandle = CreateOutputSample();
                    inputSamplePtr = sampleHandle is null
                        ? IntPtr.Zero
                        : Marshal.GetIUnknownForObject(sampleHandle.Sample);
                    outputBuffer = new MFTOutputDataBuffer
                    {
                        StreamID = 0,
                        Sample = inputSamplePtr,
                        Status = 0,
                        Events = IntPtr.Zero
                    };

                    outputBufferPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<MFTOutputDataBuffer>());
                    Marshal.StructureToPtr(outputBuffer, outputBufferPtr, fDeleteOld: false);

                    var hr = transform.ProcessOutput(0, 1, outputBufferPtr, out _);
                    outputBuffer = Marshal.PtrToStructure<MFTOutputDataBuffer>(outputBufferPtr);
                    if (hr == Native.MF_E_TRANSFORM_NEED_MORE_INPUT)
                    {
                        return packets;
                    }

                    if (hr == Native.MF_E_TRANSFORM_STREAM_CHANGE || (outputBuffer.Status & Native.MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE) != 0)
                    {
                        RefreshOutputTypeAfterStreamChange();
                        RefreshOutputInfo();
                        RefreshParameterSets();
                        continue;
                    }

                    ThrowIfFailed(hr, "Media Foundation encoder failed while producing output.");
                    RefreshParameterSets();

                    var sample = sampleHandle?.Sample;
                    var outputSamplePtr = outputBuffer.Sample;
                    if (sample is null && outputSamplePtr != IntPtr.Zero)
                    {
                        sample = (IMFSample)Marshal.GetObjectForIUnknown(outputSamplePtr);
                    }

                    try
                    {
                        if (sample is not null)
                        {
                            var bytes = ReadSampleBytes(sample);
                            if (bytes.Length > 0)
                            {
                                packets.Add(NormalizeOutputSample(bytes));
                            }
                        }

                        if (singleOutput)
                        {
                            return packets;
                        }
                    }
                    finally
                    {
                        if (sampleHandle is null && sample is not null)
                        {
                            ReleaseComObject(sample);
                        }
                    }
                }
                finally
                {
                    if (outputBuffer.Sample != IntPtr.Zero && outputBuffer.Sample != inputSamplePtr)
                    {
                        Marshal.Release(outputBuffer.Sample);
                    }

                    if (outputBuffer.Events != IntPtr.Zero)
                    {
                        Marshal.Release(outputBuffer.Events);
                    }

                    if (inputSamplePtr != IntPtr.Zero)
                    {
                        Marshal.Release(inputSamplePtr);
                    }

                    if (outputBufferPtr != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(outputBufferPtr);
                    }

                    sampleHandle?.Dispose();
                }
            }
        }

        private OutputSampleHandle? CreateOutputSample()
        {
            if (_outputProvidesSamples)
            {
                return null;
            }

            ThrowIfFailed(Native.MFCreateSample(out var sample), "Unable to create Media Foundation output sample.");
            var bufferSize = Math.Max(_outputBufferSize, _options.VideoWidth * _options.VideoHeight * 4);
            ThrowIfFailed(Native.MFCreateMemoryBuffer(bufferSize, out var buffer), "Unable to create Media Foundation output buffer.");
            ThrowIfFailed(sample.AddBuffer(buffer), "Unable to attach output buffer to Media Foundation sample.");
            return new OutputSampleHandle(sample, buffer);
        }

        private void RefreshOutputInfo()
        {
            var transform = GetTransform();
            ThrowIfFailed(transform.GetOutputStreamInfo(0, out var streamInfo), "Unable to read Media Foundation output stream info.");
            _outputProvidesSamples = (streamInfo.Flags & Native.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
            _outputBufferSize = streamInfo.Size > 0
                ? streamInfo.Size
                : Math.Max(64 * 1024, _options.VideoWidth * _options.VideoHeight);
        }

        private void RefreshOutputTypeAfterStreamChange()
        {
            var transform = GetTransform();
            var hr = transform.GetOutputAvailableType(0, 0, out var mediaType);
            if (hr == Native.MF_E_NO_MORE_TYPES)
            {
                return;
            }

            ThrowIfFailed(hr, "Media Foundation encoder did not provide a replacement output type.");
            try
            {
                ThrowIfFailed(transform.SetOutputType(0, mediaType, 0), "Unable to apply Media Foundation output stream change.");
            }
            finally
            {
                ReleaseComObject(mediaType);
            }
        }

        private void RefreshParameterSets()
        {
            IMFMediaType? mediaType = null;
            var hr = GetTransform().GetOutputCurrentType(0, out mediaType);
            if (hr < 0 || mediaType is null)
            {
                return;
            }

            try
            {
                var attributes = (IMFAttributes)mediaType;
                var key = Native.MF_MT_MPEG_SEQUENCE_HEADER;
                if (attributes.GetBlobSize(ref key, out var blobSize) < 0 || blobSize <= 0)
                {
                    return;
                }

                var blobPtr = Marshal.AllocCoTaskMem(blobSize);
                try
                {
                    ThrowIfFailed(attributes.GetBlob(ref key, blobPtr, blobSize, out _), "Unable to read H.264 sequence header.");
                    var blob = new byte[blobSize];
                    Marshal.Copy(blobPtr, blob, 0, blob.Length);
                    var parameterSets = ConvertSequenceHeaderToAnnexB(blob);
                    if (parameterSets.Length > 0)
                    {
                        _parameterSets = parameterSets;
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(blobPtr);
                }
            }
            finally
            {
                ReleaseComObject(mediaType);
            }
        }

        private byte[] NormalizeOutputSample(byte[] sampleBytes)
        {
            var accessUnit = StartsWithAnnexBStartCode(sampleBytes)
                ? sampleBytes
                : ConvertSamplePayloadToAnnexB(sampleBytes);

            var isIdr = H264AnnexBStream.ContainsNalType(accessUnit, 5);
            if (!isIdr)
            {
                return accessUnit;
            }

            var hasSps = H264AnnexBStream.ContainsNalType(accessUnit, 7);
            var hasPps = H264AnnexBStream.ContainsNalType(accessUnit, 8);
            if (hasSps && hasPps || _parameterSets is null || _parameterSets.Length == 0)
            {
                return accessUnit;
            }

            var combined = new byte[_parameterSets.Length + accessUnit.Length];
            Buffer.BlockCopy(_parameterSets, 0, combined, 0, _parameterSets.Length);
            Buffer.BlockCopy(accessUnit, 0, combined, _parameterSets.Length, accessUnit.Length);
            return combined;
        }

        private byte[] ConvertSamplePayloadToAnnexB(byte[] sampleBytes)
        {
            if (TryConvertLengthPrefixedToAnnexB(sampleBytes, _nalLengthSize, out var annexB))
            {
                return annexB;
            }

            foreach (var lengthSize in new[] { 4, 2, 1 })
            {
                if (lengthSize != _nalLengthSize && TryConvertLengthPrefixedToAnnexB(sampleBytes, lengthSize, out annexB))
                {
                    _nalLengthSize = lengthSize;
                    return annexB;
                }
            }

            if (sampleBytes.Length > 0 && IsLikelyH264NalType(sampleBytes[0] & 0x1F))
            {
                var singleNal = new byte[Native.AnnexBStartCode.Length + sampleBytes.Length];
                Buffer.BlockCopy(Native.AnnexBStartCode, 0, singleNal, 0, Native.AnnexBStartCode.Length);
                Buffer.BlockCopy(sampleBytes, 0, singleNal, Native.AnnexBStartCode.Length, sampleBytes.Length);
                return singleNal;
            }

            throw new InvalidDataException("Media Foundation H.264 output was neither Annex B nor length-prefixed NAL units.");
        }

        private static byte[] ConvertSequenceHeaderToAnnexB(byte[] header)
        {
            if (header.Length == 0)
            {
                return Array.Empty<byte>();
            }

            if (StartsWithAnnexBStartCode(header))
            {
                return header;
            }

            if (header.Length > 6 && header[0] == 1)
            {
                using var stream = new MemoryStream();
                var offset = 5;
                var spsCount = header[offset++] & 0x1F;
                for (var index = 0; index < spsCount; index++)
                {
                    if (!TryReadBigEndianLength(header, ref offset, 2, out var length) || offset + length > header.Length)
                    {
                        return Array.Empty<byte>();
                    }

                    stream.Write(Native.AnnexBStartCode);
                    stream.Write(header.AsSpan(offset, length));
                    offset += length;
                }

                if (offset >= header.Length)
                {
                    return stream.ToArray();
                }

                var ppsCount = header[offset++];
                for (var index = 0; index < ppsCount; index++)
                {
                    if (!TryReadBigEndianLength(header, ref offset, 2, out var length) || offset + length > header.Length)
                    {
                        return stream.ToArray();
                    }

                    stream.Write(Native.AnnexBStartCode);
                    stream.Write(header.AsSpan(offset, length));
                    offset += length;
                }

                return stream.ToArray();
            }

            return Array.Empty<byte>();
        }

        private static bool TryConvertLengthPrefixedToAnnexB(byte[] data, int lengthSize, out byte[] annexB)
        {
            annexB = Array.Empty<byte>();
            if (lengthSize <= 0 || data.Length <= lengthSize)
            {
                return false;
            }

            using var stream = new MemoryStream();
            var offset = 0;
            var nalCount = 0;
            while (offset < data.Length)
            {
                if (!TryReadBigEndianLength(data, ref offset, lengthSize, out var nalLength) ||
                    nalLength <= 0 ||
                    offset + nalLength > data.Length)
                {
                    return false;
                }

                var nalType = data[offset] & 0x1F;
                if (!IsLikelyH264NalType(nalType))
                {
                    return false;
                }

                stream.Write(Native.AnnexBStartCode);
                stream.Write(data.AsSpan(offset, nalLength));
                offset += nalLength;
                nalCount++;
            }

            if (nalCount == 0)
            {
                return false;
            }

            annexB = stream.ToArray();
            return true;
        }

        private static bool TryReadBigEndianLength(byte[] data, ref int offset, int lengthSize, out int value)
        {
            value = 0;
            if (offset + lengthSize > data.Length)
            {
                return false;
            }

            for (var index = 0; index < lengthSize; index++)
            {
                value = (value << 8) | data[offset + index];
            }

            offset += lengthSize;
            return true;
        }

        private static bool StartsWithAnnexBStartCode(byte[] data)
        {
            return data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1 ||
                   data.Length >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1;
        }

        private static bool IsLikelyH264NalType(int nalType)
        {
            return nalType is >= 1 and <= 12;
        }

        private static byte[] ReadSampleBytes(IMFSample sample)
        {
            IMFMediaBuffer? buffer = null;
            try
            {
                ThrowIfFailed(sample.ConvertToContiguousBuffer(out buffer), "Unable to get contiguous Media Foundation output buffer.");
                ThrowIfFailed(buffer.Lock(out var data, out _, out var currentLength), "Unable to lock Media Foundation output buffer.");
                try
                {
                    if (currentLength == 0)
                    {
                        ThrowIfFailed(buffer.GetCurrentLength(out currentLength), "Unable to read Media Foundation output buffer length.");
                    }

                    var bytes = new byte[currentLength];
                    if (currentLength > 0)
                    {
                        Marshal.Copy(data, bytes, 0, currentLength);
                    }

                    return bytes;
                }
                finally
                {
                    buffer.Unlock();
                }
            }
            finally
            {
                if (buffer is not null)
                {
                    ReleaseComObject(buffer);
                }
            }
        }

        private IMFTransform GetTransform()
        {
            return _transform ?? throw new InvalidOperationException("Media Foundation encoder is not initialized.");
        }

        private static void SetGuid(IMFAttributes attributes, Guid key, Guid value)
        {
            ThrowIfFailed(attributes.SetGUID(ref key, ref value), $"Unable to set Media Foundation GUID attribute {key}.");
        }

        private static void SetUInt32(IMFAttributes attributes, Guid key, int value)
        {
            ThrowIfFailed(attributes.SetUINT32(ref key, value), $"Unable to set Media Foundation UINT32 attribute {key}.");
        }

        private static void SetPackedUInt32Pair(IMFAttributes attributes, Guid key, int high, int low)
        {
            var value = unchecked((long)(((ulong)(uint)high << 32) | (uint)low));
            ThrowIfFailed(attributes.SetUINT64(ref key, value), $"Unable to set Media Foundation packed UINT64 attribute {key}.");
        }

        private static void ThrowIfFailed(int hr, string message)
        {
            if (hr >= 0)
            {
                return;
            }

            throw new COMException($"{message} HRESULT=0x{hr:X8}", hr);
        }

        private static void ReleaseComObject(object? comObject)
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }

        private static byte ClampToByte(int value)
        {
            return (byte)Math.Clamp(value, 0, 255);
        }

        private sealed class MediaTypeHandle : IDisposable
        {
            public MediaTypeHandle(IMFMediaType mediaType)
            {
                MediaType = mediaType;
            }

            public IMFMediaType MediaType { get; }

            public void Dispose()
            {
                ReleaseComObject(MediaType);
            }
        }

        private sealed class InputSampleHandle : IDisposable
        {
            private readonly IMFMediaBuffer _buffer;

            public InputSampleHandle(IMFSample sample, IMFMediaBuffer buffer)
            {
                Sample = sample;
                _buffer = buffer;
            }

            public IMFSample Sample { get; }

            public void Dispose()
            {
                ReleaseComObject(_buffer);
                ReleaseComObject(Sample);
            }
        }

        [ComImport]
        [Guid("EB533D5D-2DB6-40F8-97A9-494692014F07")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFDXGIDeviceManager
        {
            [PreserveSig]
            int CloseDeviceHandle(IntPtr device);

            [PreserveSig]
            int GetVideoService(IntPtr device, ref Guid riid, out IntPtr service);

            [PreserveSig]
            int LockDevice(IntPtr device, ref Guid riid, out IntPtr unknownDevice, [MarshalAs(UnmanagedType.Bool)] bool block);

            [PreserveSig]
            int OpenDeviceHandle(out IntPtr device);

            [PreserveSig]
            int ResetDevice([MarshalAs(UnmanagedType.IUnknown)] object device, uint resetToken);

            [PreserveSig]
            int TestDevice(IntPtr device);

            [PreserveSig]
            int UnlockDevice(IntPtr device, [MarshalAs(UnmanagedType.Bool)] bool saveState);
        }

        private sealed class OutputSampleHandle : IDisposable
        {
            private readonly IMFMediaBuffer _buffer;

            public OutputSampleHandle(IMFSample sample, IMFMediaBuffer buffer)
            {
                Sample = sample;
                _buffer = buffer;
            }

            public IMFSample Sample { get; }

            public void Dispose()
            {
                ReleaseComObject(_buffer);
                ReleaseComObject(Sample);
            }
        }

        [ComImport]
        [Guid("DF598932-F10C-4E39-BBA2-C308F101DAA3")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaEvent
        {
            [PreserveSig]
            int GetItem(ref Guid guidKey, IntPtr pValue);

            [PreserveSig]
            int GetItemType(ref Guid guidKey, out int pType);

            [PreserveSig]
            int CompareItem(ref Guid guidKey, IntPtr value, out int result);

            [PreserveSig]
            int Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes theirs, int matchType, out int result);

            [PreserveSig]
            int GetUINT32(ref Guid guidKey, out int value);

            [PreserveSig]
            int GetUINT64(ref Guid guidKey, out long value);

            [PreserveSig]
            int GetDouble(ref Guid guidKey, out double value);

            [PreserveSig]
            int GetGUID(ref Guid guidKey, out Guid value);

            [PreserveSig]
            int GetStringLength(ref Guid guidKey, out int length);

            [PreserveSig]
            int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int bufferSize, out int length);

            [PreserveSig]
            int GetAllocatedString(ref Guid guidKey, out IntPtr value, out int length);

            [PreserveSig]
            int GetBlobSize(ref Guid guidKey, out int blobSize);

            [PreserveSig]
            int GetBlob(ref Guid guidKey, IntPtr buffer, int bufferSize, out int blobSize);

            [PreserveSig]
            int GetAllocatedBlob(ref Guid guidKey, out IntPtr buffer, out int size);

            [PreserveSig]
            int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr unknown);

            [PreserveSig]
            int SetItem(ref Guid guidKey, IntPtr value);

            [PreserveSig]
            int DeleteItem(ref Guid guidKey);

            [PreserveSig]
            int DeleteAllItems();

            [PreserveSig]
            int SetUINT32(ref Guid guidKey, int value);

            [PreserveSig]
            int SetUINT64(ref Guid guidKey, long value);

            [PreserveSig]
            int SetDouble(ref Guid guidKey, double value);

            [PreserveSig]
            int SetGUID(ref Guid guidKey, ref Guid value);

            [PreserveSig]
            int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);

            [PreserveSig]
            int SetBlob(ref Guid guidKey, IntPtr buffer, int bufferSize);

            [PreserveSig]
            int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object unknown);

            [PreserveSig]
            int LockStore();

            [PreserveSig]
            int UnlockStore();

            [PreserveSig]
            int GetCount(out int items);

            [PreserveSig]
            int GetItemByIndex(int index, out Guid guidKey, IntPtr value);

            [PreserveSig]
            int CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes destination);

            [PreserveSig]
            int GetType(out int mediaEventType);

            [PreserveSig]
            int GetExtendedType(out Guid extendedType);

            [PreserveSig]
            int GetStatus(out int status);

            [PreserveSig]
            int GetValue(IntPtr value);
        }

        [ComImport]
        [Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaEventGenerator
        {
            [PreserveSig]
            int GetEvent(int flags, out IMFMediaEvent mediaEvent);

            [PreserveSig]
            int BeginGetEvent(IntPtr callback, IntPtr state);

            [PreserveSig]
            int EndGetEvent(IntPtr result, out IMFMediaEvent mediaEvent);

            [PreserveSig]
            int QueueEvent(int mediaEventType, ref Guid extendedType, int status, IntPtr value);
        }

        [ComImport]
        [Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFAttributes
        {
            [PreserveSig]
            int GetItem(ref Guid guidKey, IntPtr pValue);

            [PreserveSig]
            int GetItemType(ref Guid guidKey, out int pType);

            [PreserveSig]
            int CompareItem(ref Guid guidKey, IntPtr value, out int result);

            [PreserveSig]
            int Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes theirs, int matchType, out int result);

            [PreserveSig]
            int GetUINT32(ref Guid guidKey, out int value);

            [PreserveSig]
            int GetUINT64(ref Guid guidKey, out long value);

            [PreserveSig]
            int GetDouble(ref Guid guidKey, out double value);

            [PreserveSig]
            int GetGUID(ref Guid guidKey, out Guid value);

            [PreserveSig]
            int GetStringLength(ref Guid guidKey, out int length);

            [PreserveSig]
            int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int bufferSize, out int length);

            [PreserveSig]
            int GetAllocatedString(ref Guid guidKey, out IntPtr value, out int length);

            [PreserveSig]
            int GetBlobSize(ref Guid guidKey, out int blobSize);

            [PreserveSig]
            int GetBlob(ref Guid guidKey, IntPtr buffer, int bufferSize, out int blobSize);

            [PreserveSig]
            int GetAllocatedBlob(ref Guid guidKey, out IntPtr buffer, out int size);

            [PreserveSig]
            int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr unknown);

            [PreserveSig]
            int SetItem(ref Guid guidKey, IntPtr value);

            [PreserveSig]
            int DeleteItem(ref Guid guidKey);

            [PreserveSig]
            int DeleteAllItems();

            [PreserveSig]
            int SetUINT32(ref Guid guidKey, int value);

            [PreserveSig]
            int SetUINT64(ref Guid guidKey, long value);

            [PreserveSig]
            int SetDouble(ref Guid guidKey, double value);

            [PreserveSig]
            int SetGUID(ref Guid guidKey, ref Guid value);

            [PreserveSig]
            int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);

            [PreserveSig]
            int SetBlob(ref Guid guidKey, IntPtr buffer, int bufferSize);

            [PreserveSig]
            int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object unknown);

            [PreserveSig]
            int LockStore();

            [PreserveSig]
            int UnlockStore();

            [PreserveSig]
            int GetCount(out int items);

            [PreserveSig]
            int GetItemByIndex(int index, out Guid guidKey, IntPtr value);

            [PreserveSig]
            int CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes destination);
        }

        [ComImport]
        [Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType : IMFAttributes
        {
            [PreserveSig]
            int GetMajorType(out Guid majorType);

            [PreserveSig]
            int IsCompressedFormat(out int compressed);

            [PreserveSig]
            int IsEqual([MarshalAs(UnmanagedType.Interface)] IMFMediaType mediaType, out int flags);

            [PreserveSig]
            int GetRepresentation(ref Guid representation, out IntPtr data);

            [PreserveSig]
            int FreeRepresentation(ref Guid representation, IntPtr data);
        }

        [ComImport]
        [Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            [PreserveSig]
            int Lock(out IntPtr buffer, out int maxLength, out int currentLength);

            [PreserveSig]
            int Unlock();

            [PreserveSig]
            int GetCurrentLength(out int currentLength);

            [PreserveSig]
            int SetCurrentLength(int currentLength);

            [PreserveSig]
            int GetMaxLength(out int maxLength);
        }

        [ComImport]
        [Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            [PreserveSig]
            int GetItem(ref Guid guidKey, IntPtr pValue);

            [PreserveSig]
            int GetItemType(ref Guid guidKey, out int pType);

            [PreserveSig]
            int CompareItem(ref Guid guidKey, IntPtr value, out int result);

            [PreserveSig]
            int Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes theirs, int matchType, out int result);

            [PreserveSig]
            int GetUINT32(ref Guid guidKey, out int value);

            [PreserveSig]
            int GetUINT64(ref Guid guidKey, out long value);

            [PreserveSig]
            int GetDouble(ref Guid guidKey, out double value);

            [PreserveSig]
            int GetGUID(ref Guid guidKey, out Guid value);

            [PreserveSig]
            int GetStringLength(ref Guid guidKey, out int length);

            [PreserveSig]
            int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int bufferSize, out int length);

            [PreserveSig]
            int GetAllocatedString(ref Guid guidKey, out IntPtr value, out int length);

            [PreserveSig]
            int GetBlobSize(ref Guid guidKey, out int blobSize);

            [PreserveSig]
            int GetBlob(ref Guid guidKey, IntPtr buffer, int bufferSize, out int blobSize);

            [PreserveSig]
            int GetAllocatedBlob(ref Guid guidKey, out IntPtr buffer, out int size);

            [PreserveSig]
            int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr unknown);

            [PreserveSig]
            int SetItem(ref Guid guidKey, IntPtr value);

            [PreserveSig]
            int DeleteItem(ref Guid guidKey);

            [PreserveSig]
            int DeleteAllItems();

            [PreserveSig]
            int SetUINT32(ref Guid guidKey, int value);

            [PreserveSig]
            int SetUINT64(ref Guid guidKey, long value);

            [PreserveSig]
            int SetDouble(ref Guid guidKey, double value);

            [PreserveSig]
            int SetGUID(ref Guid guidKey, ref Guid value);

            [PreserveSig]
            int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);

            [PreserveSig]
            int SetBlob(ref Guid guidKey, IntPtr buffer, int bufferSize);

            [PreserveSig]
            int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object unknown);

            [PreserveSig]
            int LockStore();

            [PreserveSig]
            int UnlockStore();

            [PreserveSig]
            int GetCount(out int items);

            [PreserveSig]
            int GetItemByIndex(int index, out Guid guidKey, IntPtr value);

            [PreserveSig]
            int CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes destination);

            [PreserveSig]
            int GetSampleFlags(out int sampleFlags);

            [PreserveSig]
            int SetSampleFlags(int sampleFlags);

            [PreserveSig]
            int GetSampleTime(out long sampleTime);

            [PreserveSig]
            int SetSampleTime(long sampleTime);

            [PreserveSig]
            int GetSampleDuration(out long sampleDuration);

            [PreserveSig]
            int SetSampleDuration(long sampleDuration);

            [PreserveSig]
            int GetBufferCount(out int bufferCount);

            [PreserveSig]
            int GetBufferByIndex(int index, out IMFMediaBuffer buffer);

            [PreserveSig]
            int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);

            [PreserveSig]
            int AddBuffer([MarshalAs(UnmanagedType.Interface)] IMFMediaBuffer buffer);

            [PreserveSig]
            int RemoveBufferByIndex(int index);

            [PreserveSig]
            int RemoveAllBuffers();

            [PreserveSig]
            int GetTotalLength(out int totalLength);

            [PreserveSig]
            int CopyToBuffer([MarshalAs(UnmanagedType.Interface)] IMFMediaBuffer buffer);
        }

        [ComImport]
        [Guid("BF94C121-5B05-4E6F-8000-BA598961414D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFTransform
        {
            [PreserveSig]
            int GetStreamLimits(out int inputMinimum, out int inputMaximum, out int outputMinimum, out int outputMaximum);

            [PreserveSig]
            int GetStreamCount(out int inputStreams, out int outputStreams);

            [PreserveSig]
            int GetStreamIDs(int inputIdArraySize, [Out] int[] inputIds, int outputIdArraySize, [Out] int[] outputIds);

            [PreserveSig]
            int GetInputStreamInfo(int inputStreamId, out MFTInputStreamInfo streamInfo);

            [PreserveSig]
            int GetOutputStreamInfo(int outputStreamId, out MFTOutputStreamInfo streamInfo);

            [PreserveSig]
            int GetAttributes(out IMFAttributes attributes);

            [PreserveSig]
            int GetInputStreamAttributes(int inputStreamId, out IMFAttributes attributes);

            [PreserveSig]
            int GetOutputStreamAttributes(int outputStreamId, out IMFAttributes attributes);

            [PreserveSig]
            int DeleteInputStream(int streamId);

            [PreserveSig]
            int AddInputStreams(int streams, [In] int[] streamIds);

            [PreserveSig]
            int GetInputAvailableType(int inputStreamId, int typeIndex, out IMFMediaType mediaType);

            [PreserveSig]
            int GetOutputAvailableType(int outputStreamId, int typeIndex, out IMFMediaType mediaType);

            [PreserveSig]
            int SetInputType(int inputStreamId, [MarshalAs(UnmanagedType.Interface)] IMFMediaType mediaType, int flags);

            [PreserveSig]
            int SetOutputType(int outputStreamId, [MarshalAs(UnmanagedType.Interface)] IMFMediaType mediaType, int flags);

            [PreserveSig]
            int GetInputCurrentType(int inputStreamId, out IMFMediaType mediaType);

            [PreserveSig]
            int GetOutputCurrentType(int outputStreamId, out IMFMediaType mediaType);

            [PreserveSig]
            int GetInputStatus(int inputStreamId, out int flags);

            [PreserveSig]
            int GetOutputStatus(out int flags);

            [PreserveSig]
            int SetOutputBounds(long lowerBound, long upperBound);

            [PreserveSig]
            int ProcessEvent(int inputStreamId, IntPtr mediaEvent);

            [PreserveSig]
            int ProcessMessage(int message, IntPtr param);

            [PreserveSig]
            int ProcessInput(int inputStreamId, [MarshalAs(UnmanagedType.Interface)] IMFSample sample, int flags);

            [PreserveSig]
            int ProcessOutput(int flags, int outputBufferCount, IntPtr outputSamples, out int status);
        }

        [ComImport]
        [Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICodecAPI
        {
            [PreserveSig]
            int IsSupported(ref Guid api);

            [PreserveSig]
            int IsModifiable(ref Guid api);

            [PreserveSig]
            int GetParameterRange(ref Guid api, out Variant minimum, out Variant maximum, out Variant steppingDelta);

            [PreserveSig]
            int GetParameterValues(ref Guid api, out IntPtr values, out int valuesCount);

            [PreserveSig]
            int GetDefaultValue(ref Guid api, out Variant value);

            [PreserveSig]
            int GetValue(ref Guid api, out Variant value);

            [PreserveSig]
            int SetValue(ref Guid api, ref Variant value);

            [PreserveSig]
            int RegisterForEvent(ref Guid api, IntPtr userData);

            [PreserveSig]
            int UnregisterForEvent(ref Guid api);

            [PreserveSig]
            int SetAllDefaults();

            [PreserveSig]
            int SetValueWithNotify(ref Guid api, ref Variant value, out IntPtr changedParam, out int changedParamCount);

            [PreserveSig]
            int SetAllDefaultsWithNotify(out IntPtr changedParam, out int changedParamCount);

            [PreserveSig]
            int GetAllSettings(IntPtr stream);

            [PreserveSig]
            int SetAllSettings(IntPtr stream);

            [PreserveSig]
            int SetAllSettingsWithNotify(IntPtr stream, out IntPtr changedParam, out int changedParamCount);
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct H264EncoderMftCandidate(string Name, Guid Clsid, int OriginalIndex);

        [StructLayout(LayoutKind.Sequential)]
        private struct MFTRegisterTypeInfo
        {
            public Guid MajorType;

            public Guid Subtype;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MFTInputStreamInfo
        {
            public long MaxLatency;
            public int Flags;
            public int Size;
            public int MaxLookahead;
            public int Alignment;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MFTOutputStreamInfo
        {
            public int Flags;
            public int Size;
            public int Alignment;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MFTOutputDataBuffer
        {
            public int StreamID;

            public IntPtr Sample;

            public int Status;

            public IntPtr Events;
        }

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct Variant
        {
            [FieldOffset(0)]
            public ushort Vt;

            [FieldOffset(8)]
            public int IntValue;

            [FieldOffset(8)]
            public uint UIntValue;

            [FieldOffset(8)]
            public short BoolValue;

            public static Variant FromUInt32(uint value)
            {
                return new Variant
                {
                    Vt = Native.VT_UI4,
                    UIntValue = value
                };
            }

            public static Variant FromBool(bool value)
            {
                return new Variant
                {
                    Vt = Native.VT_BOOL,
                    BoolValue = value ? (short)-1 : (short)0
                };
            }
        }

        private static class Native
        {
            public const int S_OK = 0;
            public const int S_FALSE = 1;
            public const int MF_VERSION = 0x00020070;
            public const int MFSTARTUP_FULL = 0;
            public const int COINIT_MULTITHREADED = 0;
            public const int CLSCTX_INPROC_SERVER = 1;
            public const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
            public const int VT_BOOL = 11;
            public const int VT_UI4 = 19;
            public const int MFVideoInterlace_Progressive = 2;
            public const int H264ProfileBaseline = 66;
            public const int eAVEncCommonRateControlMode_CBR = 0;
            public const int eAVEncH264PictureType_IDR = 0;
            public const int MFT_MESSAGE_SET_D3D_MANAGER = 0x00000002;
            public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
            public const int MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
            public const int MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
            public const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
            public const int MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE = 0x00000100;
            public const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;
            public const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36B4);
            public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
            public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
            public const int MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80);
            public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
            public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
            public const int MF_EVENT_FLAG_NO_WAIT = 0x00000001;
            public const int METransformNeedInput = 601;
            public const int METransformHaveOutput = 602;
            public const int MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
            public const int MFT_ENUM_FLAG_ASYNCMFT = 0x00000002;
            public const int MFT_ENUM_FLAG_HARDWARE = 0x00000004;
            public const int MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;
            public static readonly byte[] AnnexBStartCode = [0, 0, 0, 1];

            public static readonly Guid CLSID_CMSH264EncoderMFT = new("6CA50344-051A-4DED-9779-A43305165E35");
            public static readonly Guid IID_IMFTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");
            public static Guid MFT_CATEGORY_VIDEO_ENCODER = new("F79EAC7D-E545-4387-BDEE-D647D7BDE42A");
            public static Guid MFT_FRIENDLY_NAME_Attribute = new("314FFBAE-5B41-4C95-9C19-4E7D586FACE3");
            public static Guid MFT_TRANSFORM_CLSID_Attribute = new("6821C42B-65A4-4E82-99BC-9A88205ECD0C");
            public static Guid MF_TRANSFORM_ASYNC = new("F81A699A-649A-497D-8C73-29F8FED6AD7A");
            public static Guid MF_TRANSFORM_ASYNC_UNLOCK = new("E5666D6B-3422-4EB6-A421-DA7DB1F8E207");
            public static Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
            public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
            public static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
            public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
            public static Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
            public static Guid MF_MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
            public static Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("C9173739-5E56-461C-B713-46FB995CB95F");
            public static Guid MF_MT_FIXED_SIZE_SAMPLES = new("B8EBEFAF-B718-4E04-B0A9-116775E3321B");
            public static Guid MF_MT_SAMPLE_SIZE = new("DAD3AB78-1990-408B-BCE2-EBA673DACC10");
            public static Guid MF_MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D");
            public static Guid MF_MT_FRAME_RATE = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
            public static Guid MF_MT_PIXEL_ASPECT_RATIO = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
            public static Guid MF_MT_INTERLACE_MODE = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
            public static Guid MF_MT_AVG_BITRATE = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
            public static Guid MF_MT_MPEG2_PROFILE = new("AD76A80B-2D5C-4E0B-B375-64E520137036");
            public static Guid MF_MT_MAX_KEYFRAME_SPACING = new("C16EB52B-73A1-476F-8D62-839D6A020652");
            public static Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3C036DE7-3AD0-4C9E-9216-EE6D6AC21CB3");
            public static Guid MF_NALU_LENGTH_SET = new("A7911D53-12A4-4965-AE70-6EADD6FF0551");
            public static Guid MFSampleExtension_VideoEncodePictureType = new("973704E6-CD14-483C-8F20-C9FC0928BAD5");
            public static Guid CODECAPI_AVLowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
            public static Guid CODECAPI_AVEncCommonRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");
            public static Guid CODECAPI_AVEncCommonMeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");
            public static Guid CODECAPI_AVEncMPVGOPSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
            public static Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");
            public static Guid CODECAPI_AVEncMPVProfile = new("DABB534A-1D99-4284-975A-D90E2239BAA1");
            public static Guid CODECAPI_AVEncVideoForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345");

            [DllImport("ole32.dll", ExactSpelling = true)]
            public static extern int CoInitializeEx(IntPtr reserved, int coInit);

            [DllImport("ole32.dll", ExactSpelling = true)]
            public static extern void CoUninitialize();

            [DllImport("ole32.dll", ExactSpelling = true)]
            public static extern void CoTaskMemFree(IntPtr value);

            [DllImport("ole32.dll", ExactSpelling = true)]
            public static extern int CoCreateInstance(
                ref Guid clsid,
                IntPtr outer,
                int context,
                ref Guid iid,
                [MarshalAs(UnmanagedType.Interface)] out IMFTransform instance);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFStartup(int version, int flags);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFShutdown();

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFTEnumEx(
                ref Guid category,
                int flags,
                IntPtr inputType,
                ref MFTRegisterTypeInfo outputType,
                out IntPtr activateArray,
                out int count);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFCreateMediaType(out IMFMediaType mediaType);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFCreateSample(out IMFSample sample);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IMFDXGIDeviceManager deviceManager);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFCreateDXGISurfaceBuffer(
                ref Guid riid,
                [MarshalAs(UnmanagedType.IUnknown)] object surface,
                uint subresourceIndex,
                [MarshalAs(UnmanagedType.Bool)] bool bottomUpWhenLinear,
                out IMFMediaBuffer buffer);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private sealed class TestPatternGenerator
    {
        private static readonly (byte B, byte G, byte R)[] Bars =
        [
            (255, 255, 255),
            (0, 255, 255),
            (255, 255, 0),
            (0, 255, 0),
            (255, 0, 255),
            (0, 0, 255),
            (255, 0, 0),
            (0, 0, 0)
        ];

        private readonly int _width;
        private readonly int _height;
        private byte[]? _baseFrame;

        public TestPatternGenerator(int width, int height)
        {
            _width = width;
            _height = height;
        }

        public void Render(byte[] bgra, long frameId, DateTimeOffset generatedAt)
        {
            if (bgra.Length < _width * _height * 4)
            {
                throw new ArgumentException("Frame buffer is smaller than the configured video dimensions.", nameof(bgra));
            }

            var baseFrame = _baseFrame ??= CreateBaseFrame();
            Buffer.BlockCopy(baseFrame, 0, bgra, 0, baseFrame.Length);

            DrawMovingBlocks(bgra, frameId);
            DrawFlashMarker(bgra, generatedAt);
            DrawFrameCode(bgra, frameId);
        }

        private byte[] CreateBaseFrame()
        {
            var bgra = new byte[checked(_width * _height * 4)];
            for (var y = 0; y < _height; y++)
            {
                for (var x = 0; x < _width; x++)
                {
                    var offset = (y * _width + x) * 4;
                    var bar = Bars[(x * Bars.Length) / Math.Max(1, _width)];
                    var stripe = (byte)((y / 24) % 2 == 0 ? 28 : 0);
                    bgra[offset] = (byte)Math.Min(255, (bar.B / 3) + stripe);
                    bgra[offset + 1] = (byte)Math.Min(255, (bar.G / 3) + stripe);
                    bgra[offset + 2] = (byte)Math.Min(255, (bar.R / 3) + stripe);
                    bgra[offset + 3] = 255;
                }
            }

            DrawColorBars(bgra);
            return bgra;
        }

        private void DrawColorBars(byte[] bgra)
        {
            var top = _height / 3;
            var barHeight = Math.Max(60, _height / 6);
            for (var index = 0; index < Bars.Length; index++)
            {
                var x0 = index * _width / Bars.Length;
                var x1 = (index + 1) * _width / Bars.Length;
                var color = Bars[index];
                FillRect(bgra, x0, top, x1 - x0, barHeight, color.B, color.G, color.R);
            }
        }

        private void DrawMovingBlocks(byte[] bgra, long frameId)
        {
            var blockWidth = Math.Max(96, _width / 10);
            var blockHeight = Math.Max(54, _height / 12);
            var horizontalRange = Math.Max(1, _width - blockWidth);
            var verticalRange = Math.Max(1, _height - blockHeight - 40);
            var x = (int)((frameId * 11) % horizontalRange);
            var y = 96 + (int)((frameId * 7) % Math.Max(1, _height / 5));
            FillRect(bgra, x, y, blockWidth, blockHeight, 40, 230, 255);

            var vx = Math.Max(0, _width - blockWidth - 48);
            var vy = 40 + (int)((frameId * 9) % verticalRange);
            FillRect(bgra, vx, vy, blockWidth, blockHeight, 255, 110, 40);
        }

        private void DrawFlashMarker(byte[] bgra, DateTimeOffset generatedAt)
        {
            var flashOn = generatedAt.Millisecond < 500;
            var markerSize = Math.Max(72, _height / 9);
            FillRect(
                bgra,
                48,
                Math.Max(0, _height - markerSize - 48),
                markerSize,
                markerSize,
                flashOn ? (byte)0 : (byte)40,
                flashOn ? (byte)255 : (byte)40,
                flashOn ? (byte)120 : (byte)40);
        }

        private void DrawFrameCode(byte[] bgra, long frameId)
        {
            var cell = Math.Max(10, _width / 128);
            var x = 48;
            var y = 36;
            DrawBits(bgra, x, y, cell, unchecked((uint)frameId));
            DrawBits(bgra, x, y + cell * 6, cell, unchecked((uint)(frameId >> 16)));
        }

        private void DrawBits(byte[] bgra, int x, int y, int cell, uint value)
        {
            for (var bit = 0; bit < 32; bit++)
            {
                var on = ((value >> bit) & 1) != 0;
                var cx = x + bit * (cell + 2);
                FillRect(
                    bgra,
                    cx,
                    y,
                    cell,
                    cell * 4,
                    on ? (byte)255 : (byte)18,
                    on ? (byte)255 : (byte)18,
                    on ? (byte)255 : (byte)18);
            }
        }

        private void FillRect(byte[] bgra, int x, int y, int width, int height, byte b, byte g, byte r)
        {
            var x0 = Math.Clamp(x, 0, _width);
            var y0 = Math.Clamp(y, 0, _height);
            var x1 = Math.Clamp(x + width, 0, _width);
            var y1 = Math.Clamp(y + height, 0, _height);
            for (var row = y0; row < y1; row++)
            {
                var offset = (row * _width + x0) * 4;
                for (var col = x0; col < x1; col++)
                {
                    bgra[offset] = b;
                    bgra[offset + 1] = g;
                    bgra[offset + 2] = r;
                    bgra[offset + 3] = 255;
                    offset += 4;
                }
            }
        }
    }

    private sealed class TestPatternBgraFrameSource(int width, int height) : IBgraFrameSource
    {
        private readonly TestPatternGenerator _generator = new(width, height);
        private long _frameId;

        public string SourceName => "realtime_test_pattern";

        public string SourceDescription => "realtime test pattern";

        public void Start(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        public void Capture(byte[] bgraFrame, int outputWidth, int outputHeight, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _generator.Render(bgraFrame, _frameId++, DateTimeOffset.UtcNow);
        }

        public void Dispose()
        {
        }
    }

    private sealed class RegionBgraFrameSource : IBgraFrameSource
    {
        private readonly CaptureRectangle _region;
        private GdiCaptureSession? _capture;

        public RegionBgraFrameSource(HostOptions options)
        {
            _region = options.CaptureRegion ?? throw new ArgumentException("--capture-region is required for --video-source region.");
        }

        public string SourceName => "region";

        public string SourceDescription => _region.ToString();

        public void Start(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Region capture is only available on Windows.");
            }

            ValidateRegion(_region);
            _capture = new GdiCaptureSession(_region.Width, _region.Height);
        }

        public void Capture(byte[] bgraFrame, int outputWidth, int outputHeight, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capture = _capture ?? throw new InvalidOperationException("Region capture has not started.");
            using var screenDc = GdiSafeHandle.FromScreen();
            capture.CaptureFromDc(screenDc.DangerousGetHandle(), _region.X, _region.Y, bgraFrame, outputWidth, outputHeight);
        }

        public void Dispose()
        {
            _capture?.Dispose();
        }

        private static void ValidateRegion(CaptureRectangle region)
        {
            var screenWidth = CaptureNative.GetSystemMetrics(CaptureNative.SM_CXSCREEN);
            var screenHeight = CaptureNative.GetSystemMetrics(CaptureNative.SM_CYSCREEN);
            if (region.Width <= 0 || region.Height <= 0 || region.X < 0 || region.Y < 0 ||
                region.X + region.Width > screenWidth || region.Y + region.Height > screenHeight)
            {
                throw new CaptureException(
                    "REGION_OUT_OF_BOUNDS",
                    $"Region {region} is outside the primary screen bounds 0,0,{screenWidth},{screenHeight}.");
            }
        }
    }

    private sealed class WindowBgraFrameSource : IBgraFrameSource
    {
        private readonly HostOptions _options;
        private readonly Action<string> _log;
        private IntPtr _hwnd;
        private string _title = string.Empty;
        private string _processName = string.Empty;
        private GdiCaptureSession? _capture;

        public WindowBgraFrameSource(HostOptions options, Action<string> log)
        {
            _options = options;
            _log = log;
        }

        public string SourceName => "window";

        public string SourceDescription => string.IsNullOrWhiteSpace(_title)
            ? _options.WindowTitle ?? _options.ProcessName ?? "window"
            : $"{_title} ({_processName})";

        public void Start(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Window capture is only available on Windows.");
            }

            var matches = WindowEnumerator.FindWindows(_options.WindowTitle, _options.ProcessName);
            if (matches.Count == 0)
            {
                throw new CaptureException("WINDOW_NOT_FOUND", "No visible window matched the requested title or process name.");
            }

            foreach (var match in matches.Take(10))
            {
                _log($"candidate hwnd=0x{match.Handle.ToInt64():X} process={match.ProcessName} size={match.Width}x{match.Height} title=\"{match.Title}\"");
            }

            var selected = matches[0];
            _hwnd = selected.Handle;
            _title = selected.Title;
            _processName = selected.ProcessName;
            _capture = new GdiCaptureSession(selected.Width, selected.Height);
            _log($"selected hwnd=0x{selected.Handle.ToInt64():X} process={selected.ProcessName} size={selected.Width}x{selected.Height} title=\"{selected.Title}\"");
        }

        public void Capture(byte[] bgraFrame, int outputWidth, int outputHeight, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_hwnd == IntPtr.Zero || !CaptureNative.IsWindow(_hwnd))
            {
                throw new CaptureException("WINDOW_CLOSED", "The captured window is no longer available.");
            }

            if (CaptureNative.IsIconic(_hwnd))
            {
                throw new CaptureException("WINDOW_MINIMIZED", "The captured window is minimized.");
            }

            if (!CaptureNative.GetWindowRect(_hwnd, out var rect))
            {
                throw new CaptureException("WINDOW_RECT_FAILED", $"GetWindowRect failed: {Marshal.GetLastWin32Error()}.");
            }

            var width = rect.Width;
            var height = rect.Height;
            if (width <= 0 || height <= 0)
            {
                throw new CaptureException("WINDOW_EMPTY", "The captured window has no visible size.");
            }

            if (_capture is null || _capture.Width != width || _capture.Height != height)
            {
                _capture?.Dispose();
                _capture = new GdiCaptureSession(width, height);
                _log($"resize hwnd=0x{_hwnd.ToInt64():X} size={width}x{height}");
            }

            using var windowDc = GdiSafeHandle.FromWindow(_hwnd);
            _capture.CaptureFromDc(windowDc.DangerousGetHandle(), 0, 0, bgraFrame, outputWidth, outputHeight);
        }

        public void Dispose()
        {
            _capture?.Dispose();
        }
    }

    private sealed class IddBgraFrameSource : IBgraFrameSource
    {
        private const string FrameBufferName = "Global\\SideDockFrameBuffer";
        private const string FrameReadyName = "Global\\SideDockFrameReady";
        private const string ConsumerAliveName = "Global\\SideDockFrameConsumerAlive";
        private const uint FrameMagic = 0x464B4453; // SDKF
        private const int FrameVersion = 1;
        private const int FrameFormatBgra = 1;
        private const int HeaderSize = 48;
        private const int SlotHeaderSize = 24;
        private const int FrameReadyTimeoutMs = 500;

        private readonly HostOptions _options;
        private readonly Action<string> _log;
        private MemoryMappedFile? _mapping;
        private MemoryMappedViewAccessor? _view;
        private EventWaitHandle? _frameReady;
        private EventWaitHandle? _consumerAlive;
        private long _lastSeq;
        private byte[]? _lastFrame;
        private long _lastFrameStopwatchTicks;
        private long _framesCaptured;
        private long _framesDropped;

        public IddBgraFrameSource(HostOptions options, Action<string> log)
        {
            _options = options;
            _log = log;
        }

        public string SourceName => "idd";

        public string SourceDescription => "SideDock IddCx virtual display";

        public long FramesDropped => Interlocked.Read(ref _framesDropped);

        public double LastFrameAgeMs
        {
            get
            {
                var lastFrameTicks = Interlocked.Read(ref _lastFrameStopwatchTicks);
                if (lastFrameTicks == 0)
                {
                    return 0;
                }

                return (Stopwatch.GetTimestamp() - lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
            }
        }

        public void Start(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Idd frame capture is only available on Windows.");
            }

            var waitStartedAt = Stopwatch.StartNew();
            var nextLogAt = TimeSpan.Zero;
            while (!TryOpenSharedObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (waitStartedAt.Elapsed >= nextLogAt)
                {
                    _log("waiting for Idd shared frame buffer...");
                    nextLogAt = waitStartedAt.Elapsed + TimeSpan.FromSeconds(2);
                }

                if (cancellationToken.WaitHandle.WaitOne(250))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            var consumerAlive = _consumerAlive ?? throw new InvalidOperationException("Idd consumer-alive event has not opened.");
            consumerAlive.Set();

            var header = ReadHeader();
            ValidateHeader(header);
            _lastFrame = new byte[header.Width * header.Height * 4];
            _lastFrameStopwatchTicks = Stopwatch.GetTimestamp();
            _log($"connected shared frame buffer width={header.Width} height={header.Height} stride={header.Stride} slots={header.SlotCount}");
        }

        public void Capture(byte[] bgraFrame, int outputWidth, int outputHeight, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var view = _view ?? throw new InvalidOperationException("Idd frame capture has not started.");
            var frameReady = _frameReady ?? throw new InvalidOperationException("Idd frame-ready event has not opened.");

            var signaled = frameReady.WaitOne(FrameReadyTimeoutMs);
            if (!signaled)
            {
                CopyLastFrameOrBlack(bgraFrame, outputWidth, outputHeight);
                return;
            }

            var header = ReadHeader();
            ValidateHeader(header);
            var sequence = Volatile.Read(ref _lastSeq);
            var bestSlotIndex = -1;
            long bestSeq = sequence;
            long bestTimestampQpc = 0;
            int bestLength = 0;

            for (var slotIndex = 0; slotIndex < header.SlotCount; slotIndex++)
            {
                var slotOffset = HeaderSize + (long)slotIndex * (SlotHeaderSize + header.SlotSize);
                var slotSeq = view.ReadInt64(slotOffset);
                if (slotSeq <= bestSeq)
                {
                    continue;
                }

                var timestampQpc = view.ReadInt64(slotOffset + 8);
                var length = view.ReadInt32(slotOffset + 16);
                if (length <= 0 || length > header.SlotSize)
                {
                    continue;
                }

                bestSeq = slotSeq;
                bestTimestampQpc = timestampQpc;
                bestLength = length;
                bestSlotIndex = slotIndex;
            }

            if (bestSlotIndex < 0)
            {
                CopyLastFrameOrBlack(bgraFrame, outputWidth, outputHeight);
                return;
            }

            var expectedLength = header.Height * header.Stride;
            if (bestLength < expectedLength)
            {
                Interlocked.Increment(ref _framesDropped);
                CopyLastFrameOrBlack(bgraFrame, outputWidth, outputHeight);
                return;
            }

            var payloadOffset = HeaderSize + (long)bestSlotIndex * (SlotHeaderSize + header.SlotSize) + SlotHeaderSize;
            if (outputWidth == header.Width && outputHeight == header.Height && header.Stride == outputWidth * 4)
            {
                view.ReadArray(payloadOffset, bgraFrame, 0, outputWidth * outputHeight * 4);
                RememberFrame(bgraFrame, outputWidth, outputHeight);
            }
            else
            {
                CopyFrameLetterboxed(view, payloadOffset, header, bgraFrame, outputWidth, outputHeight);
                RememberFrame(bgraFrame, outputWidth, outputHeight);
            }

            var skipped = bestSeq - sequence - 1;
            if (skipped > 0)
            {
                Interlocked.Add(ref _framesDropped, skipped);
            }

            Volatile.Write(ref _lastSeq, bestSeq);
            Interlocked.Increment(ref _framesCaptured);
            _lastFrameStopwatchTicks = Stopwatch.GetTimestamp();

            if ((_framesCaptured % 300) == 0)
            {
                _log($"frames captured={_framesCaptured} dropped={_framesDropped} seq={bestSeq} timestampQpc={bestTimestampQpc}");
            }
        }

        public void Dispose()
        {
            try
            {
                _consumerAlive?.Reset();
            }
            catch (ObjectDisposedException)
            {
            }

            CloseSharedObjects();
        }

        private IddSharedFrameHeader ReadHeader()
        {
            var view = _view ?? throw new InvalidOperationException("Idd shared frame buffer is not open.");
            return new IddSharedFrameHeader(
                Magic: view.ReadUInt32(0),
                Version: view.ReadInt32(4),
                Width: view.ReadInt32(8),
                Height: view.ReadInt32(12),
                Format: view.ReadInt32(16),
                Stride: view.ReadInt32(20),
                SlotCount: view.ReadInt32(24),
                SlotSize: view.ReadInt32(28),
                WriteSeq: view.ReadInt64(32),
                TimestampQpc: view.ReadInt64(40));
        }

        [SupportedOSPlatform("windows")]
        private bool TryOpenSharedObjects()
        {
            try
            {
                _mapping = MemoryMappedFile.OpenExisting(FrameBufferName, MemoryMappedFileRights.Read);
                _view = _mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                _frameReady = EventWaitHandle.OpenExisting(FrameReadyName);
                _consumerAlive = EventWaitHandle.OpenExisting(ConsumerAliveName);
                return true;
            }
            catch (FileNotFoundException)
            {
                CloseSharedObjects();
                return false;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                CloseSharedObjects();
                return false;
            }
        }

        private void CloseSharedObjects()
        {
            _consumerAlive?.Dispose();
            _consumerAlive = null;
            _frameReady?.Dispose();
            _frameReady = null;
            _view?.Dispose();
            _view = null;
            _mapping?.Dispose();
            _mapping = null;
        }

        private void ValidateHeader(IddSharedFrameHeader header)
        {
            if (header.Magic != FrameMagic)
            {
                throw new CaptureException("IDD_FRAME_MAGIC_INVALID", $"Unexpected Idd frame magic 0x{header.Magic:X8}.");
            }

            if (header.Version != FrameVersion)
            {
                throw new CaptureException("IDD_FRAME_VERSION_UNSUPPORTED", $"Unsupported Idd frame version {header.Version}.");
            }

            if (header.Width != _options.VideoWidth || header.Height != _options.VideoHeight)
            {
                if (header.Width <= 0 || header.Height <= 0)
                {
                    throw new CaptureException("IDD_FRAME_SIZE_UNSUPPORTED", $"Invalid Idd frame size {header.Width}x{header.Height}.");
                }
            }

            if (header.Format != FrameFormatBgra)
            {
                throw new CaptureException("IDD_FRAME_FORMAT_UNSUPPORTED", $"Unsupported Idd frame format {header.Format}.");
            }

            if (header.Stride < header.Width * 4 || header.SlotCount <= 0 || header.SlotCount > 8 || header.SlotSize < header.Height * header.Stride)
            {
                throw new CaptureException("IDD_FRAME_LAYOUT_INVALID", $"Invalid Idd frame layout stride={header.Stride} slots={header.SlotCount} slotSize={header.SlotSize}.");
            }
        }

        private void CopyLastFrameOrBlack(byte[] bgraFrame, int outputWidth, int outputHeight)
        {
            var lastFrame = _lastFrame;
            if (lastFrame is not null && lastFrame.Length >= outputWidth * outputHeight * 4)
            {
                Buffer.BlockCopy(lastFrame, 0, bgraFrame, 0, outputWidth * outputHeight * 4);
                return;
            }

            Array.Clear(bgraFrame);
        }

        private void RememberFrame(byte[] bgraFrame, int outputWidth, int outputHeight)
        {
            var length = outputWidth * outputHeight * 4;
            if (_lastFrame is null || _lastFrame.Length != length)
            {
                _lastFrame = new byte[length];
            }

            Buffer.BlockCopy(bgraFrame, 0, _lastFrame, 0, length);
        }

        private static void CopyFrameLetterboxed(MemoryMappedViewAccessor view, long payloadOffset, IddSharedFrameHeader header, byte[] outputBgra, int outputWidth, int outputHeight)
        {
            Array.Clear(outputBgra);
            var scale = Math.Min(outputWidth / (double)header.Width, outputHeight / (double)header.Height);
            var scaledWidth = Math.Max(1, Math.Min(outputWidth, (int)Math.Round(header.Width * scale)));
            var scaledHeight = Math.Max(1, Math.Min(outputHeight, (int)Math.Round(header.Height * scale)));
            var offsetX = (outputWidth - scaledWidth) / 2;
            var offsetY = (outputHeight - scaledHeight) / 2;
            var sourceRow = new byte[header.Stride];

            for (var y = 0; y < scaledHeight; y++)
            {
                var sourceY = Math.Min(header.Height - 1, (int)(y / scale));
                view.ReadArray(payloadOffset + (long)sourceY * header.Stride, sourceRow, 0, header.Stride);
                var destinationOffset = ((y + offsetY) * outputWidth + offsetX) * 4;
                for (var x = 0; x < scaledWidth; x++)
                {
                    var sourceX = Math.Min(header.Width - 1, (int)(x / scale));
                    var sourceOffset = sourceX * 4;
                    outputBgra[destinationOffset] = sourceRow[sourceOffset];
                    outputBgra[destinationOffset + 1] = sourceRow[sourceOffset + 1];
                    outputBgra[destinationOffset + 2] = sourceRow[sourceOffset + 2];
                    outputBgra[destinationOffset + 3] = 255;
                    destinationOffset += 4;
                }
            }
        }
    }

    private sealed class GdiCaptureSession : IDisposable
    {
        private readonly GdiSafeHandle _memoryDc;
        private readonly GdiSafeHandle _bitmap;
        private readonly IntPtr _bits;

        public GdiCaptureSession(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "Capture dimensions must be positive.");
            }

            Width = width;
            Height = height;
            using var screenDc = GdiSafeHandle.FromScreen();
            _memoryDc = GdiSafeHandle.CreateCompatibleDc(screenDc.DangerousGetHandle());
            var bitmapInfo = CaptureNative.BitmapInfo.CreateTopDownBgra(width, height);
            _bitmap = GdiSafeHandle.CreateDibSection(screenDc.DangerousGetHandle(), ref bitmapInfo, out _bits);
            if (CaptureNative.SelectObject(_memoryDc.DangerousGetHandle(), _bitmap.DangerousGetHandle()) == IntPtr.Zero)
            {
                throw new CaptureException("GDI_SELECT_FAILED", $"SelectObject failed: {Marshal.GetLastWin32Error()}.");
            }
        }

        public int Width { get; }

        public int Height { get; }

        public void CaptureFromDc(IntPtr sourceDc, int sourceX, int sourceY, byte[] outputBgra, int outputWidth, int outputHeight)
        {
            if (outputBgra.Length < outputWidth * outputHeight * 4)
            {
                throw new ArgumentException("Output BGRA frame buffer is too small.", nameof(outputBgra));
            }

            if (!CaptureNative.BitBlt(
                    _memoryDc.DangerousGetHandle(),
                    0,
                    0,
                    Width,
                    Height,
                    sourceDc,
                    sourceX,
                    sourceY,
                    CaptureNative.SRCCOPY | CaptureNative.CAPTUREBLT))
            {
                throw new CaptureException("GDI_BITBLT_FAILED", $"BitBlt failed: {Marshal.GetLastWin32Error()}.");
            }

            CopyLetterboxed(outputBgra, outputWidth, outputHeight);
        }

        public void Dispose()
        {
            _bitmap.Dispose();
            _memoryDc.Dispose();
        }

        private unsafe void CopyLetterboxed(byte[] outputBgra, int outputWidth, int outputHeight)
        {
            Array.Clear(outputBgra);
            var scale = Math.Min(outputWidth / (double)Width, outputHeight / (double)Height);
            var scaledWidth = Math.Max(1, Math.Min(outputWidth, (int)Math.Round(Width * scale)));
            var scaledHeight = Math.Max(1, Math.Min(outputHeight, (int)Math.Round(Height * scale)));
            var offsetX = (outputWidth - scaledWidth) / 2;
            var offsetY = (outputHeight - scaledHeight) / 2;
            var source = (byte*)_bits;

            for (var y = 0; y < scaledHeight; y++)
            {
                var sourceY = Math.Min(Height - 1, (int)(y / scale));
                var destinationOffset = ((y + offsetY) * outputWidth + offsetX) * 4;
                for (var x = 0; x < scaledWidth; x++)
                {
                    var sourceX = Math.Min(Width - 1, (int)(x / scale));
                    var sourceOffset = (sourceY * Width + sourceX) * 4;
                    outputBgra[destinationOffset] = source[sourceOffset];
                    outputBgra[destinationOffset + 1] = source[sourceOffset + 1];
                    outputBgra[destinationOffset + 2] = source[sourceOffset + 2];
                    outputBgra[destinationOffset + 3] = 255;
                    destinationOffset += 4;
                }
            }
        }
    }

    private sealed class GdiSafeHandle : SafeHandle
    {
        private readonly GdiHandleKind _kind;

        private GdiSafeHandle(IntPtr handle, GdiHandleKind kind)
            : base(IntPtr.Zero, ownsHandle: true)
        {
            SetHandle(handle);
            _kind = kind;
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        public static GdiSafeHandle FromScreen()
        {
            var handle = CaptureNative.GetDC(IntPtr.Zero);
            if (handle == IntPtr.Zero)
            {
                throw new CaptureException("GDI_GETDC_FAILED", $"GetDC(screen) failed: {Marshal.GetLastWin32Error()}.");
            }

            return new GdiSafeHandle(handle, GdiHandleKind.ScreenDc);
        }

        public static GdiSafeHandle FromWindow(IntPtr hwnd)
        {
            var handle = CaptureNative.GetWindowDC(hwnd);
            if (handle == IntPtr.Zero)
            {
                throw new CaptureException("GDI_GETWINDOWDC_FAILED", $"GetWindowDC failed: {Marshal.GetLastWin32Error()}.");
            }

            return new GdiSafeHandle(handle, GdiHandleKind.WindowDc)
            {
                WindowHandle = hwnd
            };
        }

        public static GdiSafeHandle CreateCompatibleDc(IntPtr sourceDc)
        {
            var handle = CaptureNative.CreateCompatibleDC(sourceDc);
            if (handle == IntPtr.Zero)
            {
                throw new CaptureException("GDI_CREATE_DC_FAILED", $"CreateCompatibleDC failed: {Marshal.GetLastWin32Error()}.");
            }

            return new GdiSafeHandle(handle, GdiHandleKind.MemoryDc);
        }

        public static GdiSafeHandle CreateDibSection(IntPtr sourceDc, ref CaptureNative.BitmapInfo bitmapInfo, out IntPtr bits)
        {
            var handle = CaptureNative.CreateDIBSection(sourceDc, ref bitmapInfo, CaptureNative.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
            if (handle == IntPtr.Zero || bits == IntPtr.Zero)
            {
                throw new CaptureException("GDI_CREATE_DIB_FAILED", $"CreateDIBSection failed: {Marshal.GetLastWin32Error()}.");
            }

            return new GdiSafeHandle(handle, GdiHandleKind.Bitmap);
        }

        private IntPtr WindowHandle { get; init; }

        protected override bool ReleaseHandle()
        {
            return _kind switch
            {
                GdiHandleKind.ScreenDc => CaptureNative.ReleaseDC(IntPtr.Zero, handle) != 0,
                GdiHandleKind.WindowDc => CaptureNative.ReleaseDC(WindowHandle, handle) != 0,
                GdiHandleKind.MemoryDc => CaptureNative.DeleteDC(handle),
                GdiHandleKind.Bitmap => CaptureNative.DeleteObject(handle),
                _ => true
            };
        }
    }

    private enum GdiHandleKind
    {
        ScreenDc,
        WindowDc,
        MemoryDc,
        Bitmap
    }

    private static class CaptureNative
    {
        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;
        public const int SRCCOPY = 0x00CC0020;
        public const int CAPTUREBLT = 0x40000000;
        public const uint BI_RGB = 0;
        public const uint DIB_RGB_COLORS = 0;

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int GetWindowTextLengthW(IntPtr hwnd);

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr GetWindowDC(IntPtr hwnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int GetSystemMetrics(int index);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr CreateDIBSection(
            IntPtr hdc,
            ref BitmapInfo bitmapInfo,
            uint usage,
            out IntPtr bits,
            IntPtr section,
            uint offset);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BitBlt(
            IntPtr destinationDc,
            int x,
            int y,
            int width,
            int height,
            IntPtr sourceDc,
            int sourceX,
            int sourceY,
            int rop);

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeRect
        {
            public int Left;

            public int Top;

            public int Right;

            public int Bottom;

            public int Width => Right - Left;

            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfo
        {
            public BitmapInfoHeader Header;

            public uint Colors;

            public static BitmapInfo CreateTopDownBgra(int width, int height)
            {
                return new BitmapInfo
                {
                    Header = new BitmapInfoHeader
                    {
                        Size = Marshal.SizeOf<BitmapInfoHeader>(),
                        Width = width,
                        Height = -height,
                        Planes = 1,
                        BitCount = 32,
                        Compression = BI_RGB,
                        SizeImage = width * height * 4
                    },
                    Colors = 0
                };
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
        {
            public int Size;

            public int Width;

            public int Height;

            public ushort Planes;

            public ushort BitCount;

            public uint Compression;

            public int SizeImage;

            public int XPelsPerMeter;

            public int YPelsPerMeter;

            public uint ClrUsed;

            public uint ClrImportant;
        }
    }

    private static class DisplayNative
    {
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayDevicesW(
            string? lpDevice,
            uint iDevNum,
            ref DisplayDevice lpDisplayDevice,
            uint dwFlags);

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplaySettingsW(
            string lpszDeviceName,
            int iModeNum,
            ref DevMode lpDevMode);

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int ChangeDisplaySettingsExW(
            string? lpszDeviceName,
            ref DevMode lpDevMode,
            IntPtr hwnd,
            uint dwflags,
            IntPtr lParam);

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int ChangeDisplaySettingsExW(
            string? lpszDeviceName,
            IntPtr lpDevMode,
            IntPtr hwnd,
            uint dwflags,
            IntPtr lParam);

        public const uint DisplayConfigScanlineOrderingProgressive = 1;

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int GetDisplayConfigBufferSizes(
            uint flags,
            out uint numPathArrayElements,
            out uint numModeInfoArrayElements);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DisplayConfigPathInfo[] pathArray,
            ref uint numModeInfoArrayElements,
            [Out] DisplayConfigModeInfo[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int SetDisplayConfig(
            uint numPathArrayElements,
            [In] DisplayConfigPathInfo[] pathArray,
            uint numModeInfoArrayElements,
            [In] DisplayConfigModeInfo[] modeInfoArray,
            uint flags);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

        public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr MonitorFromRect(ref Rect lprc, uint dwFlags);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern uint GetDpiForSystem();

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr GetThreadDpiAwarenessContext();

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

        public const int MDT_EFFECTIVE_DPI = 0;

        [DllImport("shcore.dll", ExactSpelling = true)]
        public static extern int GetDpiForMonitor(
            IntPtr hmonitor,
            int dpiType,
            out uint dpiX,
            out uint dpiY);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Luid : IEquatable<Luid>
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
        public struct PointL
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RectL
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigRational
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfig2DRegion
        {
            public uint cx;
            public uint cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigVideoSignalInfo
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
        public struct DisplayConfigTargetMode
        {
            public DisplayConfigVideoSignalInfo targetVideoSignalInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigSourceMode
        {
            public uint width;
            public uint height;
            public uint pixelFormat;
            public PointL position;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigDesktopImageInfo
        {
            public PointL PathSourceSize;
            public RectL DesktopImageRegion;
            public RectL DesktopImageClip;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct DisplayConfigModeInfo
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
        public struct DisplayConfigPathSourceInfo
        {
            public Luid adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigPathTargetInfo
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
        public struct DisplayConfigPathInfo
        {
            public DisplayConfigPathSourceInfo sourceInfo;
            public DisplayConfigPathTargetInfo targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigDeviceInfoHeader
        {
            public uint type;
            public uint size;
            public Luid adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DisplayConfigSourceDeviceName
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
        public struct DisplayDevice
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
        public struct DevMode
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

    private sealed class CaptureException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private static class SampleStatistics
    {
        public static (double P50, double P95, double P99) Percentiles(IReadOnlyList<double> samples)
        {
            if (samples.Count == 0)
            {
                return (0, 0, 0);
            }

            var sorted = samples.ToArray();
            Array.Sort(sorted);
            return (
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.95),
                Percentile(sorted, 0.99));
        }

        private static double Percentile(IReadOnlyList<double> sortedSamples, double percentile)
        {
            if (sortedSamples.Count == 0)
            {
                return 0;
            }

            if (sortedSamples.Count == 1)
            {
                return sortedSamples[0];
            }

            var clamped = Math.Clamp(percentile, 0.0, 1.0);
            var rank = clamped * (sortedSamples.Count - 1);
            var lowerIndex = (int)Math.Floor(rank);
            var upperIndex = (int)Math.Ceiling(rank);
            if (lowerIndex == upperIndex)
            {
                return sortedSamples[lowerIndex];
            }

            var weight = rank - lowerIndex;
            return sortedSamples[lowerIndex] * (1 - weight) + sortedSamples[upperIndex] * weight;
        }
    }

    private sealed class CaptureStats
    {
        private readonly object _lock = new();
        private readonly long _startedAtTicks = Stopwatch.GetTimestamp();
        private long _windowStartedAtTicks = Stopwatch.GetTimestamp();
        private long _framesCaptured;
        private long _framesConverted;
        private long _captureErrors;
        private long _windowFramesCaptured;
        private long _windowFramesConverted;
        private double _captureMsTotal;
        private double _convertMsTotal;
        private double _windowCaptureMsTotal;
        private double _windowConvertMsTotal;
        private readonly List<double> _captureSamples = new();
        private readonly List<double> _convertSamples = new();
        private readonly List<double> _windowCaptureSamples = new();
        private readonly List<double> _windowConvertSamples = new();

        public void RecordCaptured(double captureMs)
        {
            lock (_lock)
            {
                _framesCaptured++;
                _windowFramesCaptured++;
                _captureMsTotal += captureMs;
                _windowCaptureMsTotal += captureMs;
                _captureSamples.Add(captureMs);
                _windowCaptureSamples.Add(captureMs);
            }
        }

        public void RecordConverted(double convertMs)
        {
            lock (_lock)
            {
                _framesConverted++;
                _windowFramesConverted++;
                _convertMsTotal += convertMs;
                _windowConvertMsTotal += convertMs;
                _convertSamples.Add(convertMs);
                _windowConvertSamples.Add(convertMs);
            }
        }

        public void RecordError()
        {
            lock (_lock)
            {
                _captureErrors++;
            }
        }

        public CaptureStatsSnapshot SnapshotAndResetWindow()
        {
            lock (_lock)
            {
                var capturePercentiles = SampleStatistics.Percentiles(_windowCaptureSamples);
                var convertPercentiles = SampleStatistics.Percentiles(_windowConvertSamples);
                var elapsedSeconds = Math.Max(0.001, (Stopwatch.GetTimestamp() - _windowStartedAtTicks) / (double)Stopwatch.Frequency);
                var snapshot = new CaptureStatsSnapshot(
                    _framesCaptured,
                    _framesConverted,
                    _captureErrors,
                    _windowFramesCaptured == 0 ? 0 : _windowCaptureMsTotal / _windowFramesCaptured,
                    _windowFramesConverted == 0 ? 0 : _windowConvertMsTotal / _windowFramesConverted,
                    capturePercentiles.P50,
                    capturePercentiles.P95,
                    capturePercentiles.P99,
                    convertPercentiles.P50,
                    convertPercentiles.P95,
                    convertPercentiles.P99,
                    _windowFramesCaptured / elapsedSeconds,
                    _windowFramesConverted / elapsedSeconds);

                _windowFramesCaptured = 0;
                _windowFramesConverted = 0;
                _windowCaptureMsTotal = 0;
                _windowConvertMsTotal = 0;
                _windowCaptureSamples.Clear();
                _windowConvertSamples.Clear();
                _windowStartedAtTicks = Stopwatch.GetTimestamp();
                return snapshot;
            }
        }
    }

    private sealed class AdaptiveFrameRateController
    {
        private static readonly int[] FrameRateLevels = [120, 90, 72];
        private readonly object _lock = new();
        private readonly VideoModeState _videoModeState;
        private readonly ControlMessagePublisher _controlPublisher;
        private readonly int _videoPort;
        private readonly string _scope;
        private CaptureStatsSnapshot? _latestCaptureSnapshot;
        private RealtimeEncoderStatsSnapshot? _latestEncoderSnapshot;
        private RealtimeEncoderStatsSnapshot? _lastEvaluatedEncoderSnapshot;
        private int _currentLevelIndex;
        private int _badWindowStreak;
        private int _goodWindowStreak;
        private DateTimeOffset _lastChangeAt = DateTimeOffset.MinValue;

        public AdaptiveFrameRateController(
            VideoModeState videoModeState,
            ControlMessagePublisher controlPublisher,
            int videoPort,
            string scope)
        {
            _videoModeState = videoModeState;
            _controlPublisher = controlPublisher;
            _videoPort = videoPort;
            _scope = scope;
            _currentLevelIndex = LevelIndexFor(videoModeState.Current.Fps);
        }

        public bool IsEnabled
        {
            get
            {
                lock (_lock)
                {
                    return _currentLevelIndex >= 0;
                }
            }
        }

        public async ValueTask ObserveCaptureAsync(CaptureStatsSnapshot snapshot, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _latestCaptureSnapshot = snapshot;
            }

            await ValueTask.CompletedTask;
        }

        public async ValueTask ObserveEncoderAsync(RealtimeEncoderStatsSnapshot snapshot, CancellationToken cancellationToken)
        {
            AdaptiveDecision? decision = null;
            lock (_lock)
            {
                _latestEncoderSnapshot = snapshot;
                if (!ReferenceEquals(_lastEvaluatedEncoderSnapshot, snapshot))
                {
                    _lastEvaluatedEncoderSnapshot = snapshot;
                    decision = EvaluateLocked();
                }
            }

            if (decision is not null)
            {
                await ApplyDecisionAsync(decision, cancellationToken);
            }
        }

        private AdaptiveDecision? EvaluateLocked()
        {
            var encoderSnapshot = _latestEncoderSnapshot;
            if (encoderSnapshot is null)
            {
                return null;
            }

            var currentMode = _videoModeState.Current;
            if (currentMode.Fps >= FrameRateLevels[0])
            {
                _badWindowStreak = 0;
                _goodWindowStreak = 0;
                return null;
            }

            var liveIndex = LevelIndexFor(currentMode.Fps);
            if (liveIndex < 0)
            {
                _currentLevelIndex = -1;
                _badWindowStreak = 0;
                _goodWindowStreak = 0;
                return null;
            }

            if (_currentLevelIndex != liveIndex)
            {
                _currentLevelIndex = liveIndex;
                _badWindowStreak = 0;
                _goodWindowStreak = 0;
            }

            if (_currentLevelIndex < 0 || encoderSnapshot.FramesEncoded < 12)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _lastChangeAt < TimeSpan.FromSeconds(5))
            {
                return null;
            }

            var captureSnapshot = _latestCaptureSnapshot;
            var overloaded = IsOverloaded(captureSnapshot, encoderSnapshot, currentMode.Fps);
            var idle = IsIdle(captureSnapshot, encoderSnapshot, currentMode.Fps);
            var healthy = IsHealthy(captureSnapshot, encoderSnapshot, currentMode.Fps);

            if ((_currentLevelIndex < FrameRateLevels.Length - 1) && (overloaded || idle))
            {
                _badWindowStreak++;
                _goodWindowStreak = 0;
                var immediate = encoderSnapshot.FramesDropped > 0
                    || encoderSnapshot.LateFrames > Math.Max(2, currentMode.Fps / 20)
                    || encoderSnapshot.P99EncodeMs >= 12.0
                    || encoderSnapshot.P99SendMs >= 8.0
                    || (captureSnapshot is not null && captureSnapshot.CaptureErrors > 0);

                if (_badWindowStreak >= 2 || immediate)
                {
                    _currentLevelIndex++;
                    _lastChangeAt = now;
                    _badWindowStreak = 0;
                    return new AdaptiveDecision(
                        currentMode,
                        FrameRateLevels[_currentLevelIndex],
                        overloaded ? "encode_pressure" : "static_scene");
                }
            }
            else if (_currentLevelIndex > 0 && healthy && encoderSnapshot.NewFramesSent >= encoderSnapshot.RepeatFramesSent)
            {
                _goodWindowStreak++;
                _badWindowStreak = 0;

                if (_goodWindowStreak >= 5)
                {
                    _currentLevelIndex--;
                    _lastChangeAt = now;
                    _goodWindowStreak = 0;
                    return new AdaptiveDecision(
                        currentMode,
                        FrameRateLevels[_currentLevelIndex],
                        "stable_recovery");
                }
            }
            else
            {
                _badWindowStreak = 0;
                _goodWindowStreak = 0;
            }

            return null;
        }

        private async ValueTask ApplyDecisionAsync(AdaptiveDecision decision, CancellationToken cancellationToken)
        {
            var changedMode = _videoModeState.Set(decision.PreviousMode.Width, decision.PreviousMode.Height, decision.NextFps);
            Log(
                _scope,
                $"adaptive fps {decision.PreviousMode.Fps}->{changedMode.Fps} reason={decision.Reason} " +
                $"new={decision.PreviousMode.Width}x{decision.PreviousMode.Height} bitrate={changedMode.Bitrate}");
            await _controlPublisher.PublishAsync("video_start", new JsonObject
            {
                ["videoPort"] = _videoPort,
                ["width"] = changedMode.Width,
                ["height"] = changedMode.Height,
                ["fps"] = changedMode.Fps,
                ["codec"] = "video/avc",
                ["format"] = "annexb",
                ["adaptive"] = true,
                ["reason"] = decision.Reason,
                ["previousFps"] = decision.PreviousMode.Fps,
                ["bitrate"] = changedMode.Bitrate
            }, cancellationToken);
        }

        private static bool IsOverloaded(
            CaptureStatsSnapshot? captureSnapshot,
            RealtimeEncoderStatsSnapshot encoderSnapshot,
            int currentFps)
        {
            var frameBudgetMs = 1000.0 / Math.Max(1, currentFps);
            var hasActiveNewFrames = encoderSnapshot.NewFramesSent >= Math.Max(12, encoderSnapshot.RepeatFramesSent);
            var capturePressure = captureSnapshot is not null
                && (captureSnapshot.CaptureErrors > 0
                    || captureSnapshot.P95CaptureMs > Math.Max(2.5, frameBudgetMs * 0.35)
                    || captureSnapshot.P95ConvertMs > Math.Max(2.0, frameBudgetMs * 0.35)
                    || (hasActiveNewFrames && captureSnapshot.CaptureFps < currentFps * 0.85)
                    || (hasActiveNewFrames && captureSnapshot.ConvertFps < currentFps * 0.85));

            return encoderSnapshot.FramesDropped > 0
                || encoderSnapshot.LateFrames > Math.Max(2, currentFps / 20)
                || encoderSnapshot.P95EncodeMs > Math.Max(6.0, frameBudgetMs * 0.9)
                || encoderSnapshot.P99EncodeMs > Math.Max(10.0, frameBudgetMs * 1.2)
                || encoderSnapshot.P95SendMs > Math.Max(4.0, frameBudgetMs * 0.45)
                || encoderSnapshot.P99SendMs > Math.Max(8.0, frameBudgetMs * 0.75)
                || (hasActiveNewFrames && encoderSnapshot.StreamFps < currentFps * 0.85)
                || capturePressure;
        }

        private static bool IsIdle(
            CaptureStatsSnapshot? captureSnapshot,
            RealtimeEncoderStatsSnapshot encoderSnapshot,
            int currentFps)
        {
            if (currentFps < 72)
            {
                return false;
            }

            var captureIdle = captureSnapshot is not null
                && captureSnapshot.CaptureErrors == 0
                && captureSnapshot.CaptureFps > 0
                && captureSnapshot.CaptureFps < currentFps * 0.92
                && captureSnapshot.P95CaptureMs < 4.0
                && captureSnapshot.P95ConvertMs < 4.0;

            var encoderIdle = encoderSnapshot.NewFramesSent > 0
                && encoderSnapshot.RepeatFramesSent >= Math.Max(encoderSnapshot.NewFramesSent * 2, 12)
                && encoderSnapshot.P95EncodeMs < 6.0
                && encoderSnapshot.P95SendMs < 4.0
                && encoderSnapshot.LateFrames <= Math.Max(2, currentFps / 30)
                && encoderSnapshot.FramesDropped == 0;

            return captureIdle || encoderIdle;
        }

        private static bool IsHealthy(
            CaptureStatsSnapshot? captureSnapshot,
            RealtimeEncoderStatsSnapshot encoderSnapshot,
            int currentFps)
        {
            var frameBudgetMs = 1000.0 / Math.Max(1, currentFps);
            var captureHealthy = captureSnapshot is null
                || (captureSnapshot.CaptureErrors == 0
                    && captureSnapshot.P95CaptureMs < Math.Max(2.0, frameBudgetMs * 0.3)
                    && captureSnapshot.P95ConvertMs < Math.Max(2.0, frameBudgetMs * 0.3)
                    && captureSnapshot.CaptureFps >= currentFps * 0.9
                    && captureSnapshot.ConvertFps >= currentFps * 0.9);

            return captureHealthy
                && encoderSnapshot.FramesDropped == 0
                && encoderSnapshot.P95EncodeMs < Math.Max(4.0, frameBudgetMs * 0.6)
                && encoderSnapshot.P99EncodeMs < Math.Max(8.0, frameBudgetMs * 0.9)
                && encoderSnapshot.P95SendMs < Math.Max(3.0, frameBudgetMs * 0.35)
                && encoderSnapshot.LateFrames <= Math.Max(2, currentFps / 30)
                && encoderSnapshot.StreamFps >= currentFps * 0.9;
        }

        private static int LevelIndexFor(int fps)
        {
            for (var index = 0; index < FrameRateLevels.Length; index++)
            {
                if (FrameRateLevels[index] == fps)
                {
                    return index;
                }
            }

            return -1;
        }

        private sealed record AdaptiveDecision(VideoMode PreviousMode, int NextFps, string Reason);
    }

    private static class WindowEnumerator
    {
        public static void PrintWindows()
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("--list-windows is only available on Windows.");
                return;
            }

            var windows = EnumerateWindows();
            Console.WriteLine($"Visible capturable windows: {windows.Count}");
            foreach (var window in windows)
            {
                Console.WriteLine(
                    $"hwnd=0x{window.Handle.ToInt64():X} pid={window.ProcessId} process={window.ProcessName} " +
                    $"size={window.Width}x{window.Height} title=\"{window.Title}\"");
            }
        }

        public static IReadOnlyList<WindowInfo> FindWindows(string? title, string? processName)
        {
            var normalizedTitle = title?.Trim();
            var normalizedProcess = NormalizeProcessName(processName);

            return EnumerateWindows()
                .Where(window =>
                    (string.IsNullOrWhiteSpace(normalizedTitle) ||
                     window.Title.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrWhiteSpace(normalizedProcess) ||
                     string.Equals(window.ProcessName, normalizedProcess, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        private static IReadOnlyList<WindowInfo> EnumerateWindows()
        {
            var windows = new List<WindowInfo>();
            CaptureNative.EnumWindows((hwnd, _) =>
            {
                if (!TryGetWindowInfo(hwnd, out var info))
                {
                    return true;
                }

                windows.Add(info);
                return true;
            }, IntPtr.Zero);

            return windows
                .OrderBy(window => window.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool TryGetWindowInfo(IntPtr hwnd, out WindowInfo info)
        {
            info = default;
            if (!CaptureNative.IsWindowVisible(hwnd) || CaptureNative.IsIconic(hwnd))
            {
                return false;
            }

            var titleLength = CaptureNative.GetWindowTextLengthW(hwnd);
            var titleBuilder = new StringBuilder(Math.Max(1, titleLength + 1));
            CaptureNative.GetWindowTextW(hwnd, titleBuilder, titleBuilder.Capacity);
            var title = titleBuilder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            if (!CaptureNative.GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            {
                return false;
            }

            CaptureNative.GetWindowThreadProcessId(hwnd, out var processId);
            var processName = GetProcessName(processId);
            info = new WindowInfo(hwnd, processId, processName, title, rect.Left, rect.Top, rect.Width, rect.Height);
            return true;
        }

        private static string GetProcessName(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.ProcessName;
            }
            catch
            {
                return $"pid-{processId}";
            }
        }

        private static string? NormalizeProcessName(string? processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return null;
            }

            var trimmed = processName.Trim();
            return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? trimmed[..^4]
                : trimmed;
        }
    }

    private sealed class StreamingAnnexBAccessUnitParser
    {
        private readonly List<byte> _buffer = new();
        private bool _sawAud;

        public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> chunk)
        {
            for (var index = 0; index < chunk.Length; index++)
            {
                _buffer.Add(chunk[index]);
            }

            return Drain(keepTrailing: true);
        }

        public IReadOnlyList<byte[]> Flush()
        {
            return Drain(keepTrailing: false);
        }

        private IReadOnlyList<byte[]> Drain(bool keepTrailing)
        {
            var nalUnits = FindNalUnits(_buffer);
            if (nalUnits.Count < 2)
            {
                TrimLeadingNoise(nalUnits);
                return Array.Empty<byte[]>();
            }

            if (nalUnits.Any(nal => nal.Type == 9))
            {
                _sawAud = true;
            }

            var boundaries = _sawAud
                ? FindAudAccessUnitBoundaries(nalUnits)
                : FindFallbackPictureBoundaries(nalUnits);
            if (boundaries.Count == 0)
            {
                return Array.Empty<byte[]>();
            }

            var packets = new List<byte[]>();
            var removeUntil = 0;

            foreach (var boundary in boundaries)
            {
                var startNal = boundary.StartNalIndex;
                var endNalExclusive = boundary.EndNalIndexExclusive;
                if (keepTrailing && endNalExclusive >= nalUnits.Count)
                {
                    break;
                }

                var startOffset = nalUnits[startNal].StartCodeOffset;
                var endOffset = endNalExclusive < nalUnits.Count
                    ? nalUnits[endNalExclusive].StartCodeOffset
                    : _buffer.Count;
                if (endOffset <= startOffset)
                {
                    continue;
                }

                packets.Add(_buffer.GetRange(startOffset, endOffset - startOffset).ToArray());
                removeUntil = endOffset;
            }

            if (removeUntil > 0)
            {
                _buffer.RemoveRange(0, removeUntil);
            }

            return packets;
        }

        private static IReadOnlyList<AccessUnitBoundary> FindAudAccessUnitBoundaries(IReadOnlyList<NalUnit> nalUnits)
        {
            var boundaries = new List<AccessUnitBoundary>();
            var currentAud = -1;

            for (var index = 0; index < nalUnits.Count; index++)
            {
                if (nalUnits[index].Type != 9)
                {
                    continue;
                }

                if (currentAud >= 0)
                {
                    boundaries.Add(new AccessUnitBoundary(currentAud, index));
                }

                currentAud = index;
            }

            return boundaries;
        }

        private static IReadOnlyList<AccessUnitBoundary> FindFallbackPictureBoundaries(IReadOnlyList<NalUnit> nalUnits)
        {
            var boundaries = new List<AccessUnitBoundary>();
            var currentStart = 0;
            var sawPicture = false;

            for (var index = 0; index < nalUnits.Count; index++)
            {
                var nal = nalUnits[index];
                if (H264AnnexBStream.IsPictureNal(nal))
                {
                    if (sawPicture && H264AnnexBStream.IsFirstSlice(nal))
                    {
                        boundaries.Add(new AccessUnitBoundary(currentStart, index));
                        currentStart = index;
                    }

                    sawPicture = true;
                }
            }

            return boundaries;
        }

        private void TrimLeadingNoise(IReadOnlyList<NalUnit> nalUnits)
        {
            if (nalUnits.Count == 0)
            {
                var startCode = FindStartCode(_buffer, 0);
                if (startCode > 0)
                {
                    _buffer.RemoveRange(0, startCode);
                }

                return;
            }

            if (nalUnits[0].StartCodeOffset > 0)
            {
                _buffer.RemoveRange(0, nalUnits[0].StartCodeOffset);
            }
        }

        private static IReadOnlyList<NalUnit> FindNalUnits(IReadOnlyList<byte> data)
        {
            var starts = new List<(int StartCodeOffset, int NalOffset)>();
            var index = 0;
            while (index < data.Count - 3)
            {
                if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 0 && data[index + 3] == 1)
                {
                    starts.Add((index, index + 4));
                    index += 4;
                    continue;
                }

                if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1)
                {
                    starts.Add((index, index + 3));
                    index += 3;
                    continue;
                }

                index++;
            }

            var nals = new List<NalUnit>(starts.Count);
            for (var startIndex = 0; startIndex < starts.Count; startIndex++)
            {
                var start = starts[startIndex];
                if (start.NalOffset >= data.Count)
                {
                    continue;
                }

                var endOffset = startIndex + 1 < starts.Count ? starts[startIndex + 1].StartCodeOffset : data.Count;
                if (endOffset <= start.NalOffset)
                {
                    continue;
                }

                var nalType = data[start.NalOffset] & 0x1F;
                nals.Add(new NalUnit(start.StartCodeOffset, start.NalOffset, endOffset, nalType));
            }

            return nals;
        }

        private static int FindStartCode(IReadOnlyList<byte> data, int offset)
        {
            for (var index = offset; index < data.Count - 3; index++)
            {
                if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1)
                {
                    return index;
                }

                if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 0 && data[index + 3] == 1)
                {
                    return index;
                }
            }

            return -1;
        }
    }

    private sealed class RealtimeEncoderStats
    {
        private const int MaxTrackedFrames = 120;
        private readonly object _lock = new();
        private readonly double _frameDurationMs;
        private readonly Dictionary<long, FrameTiming> _frameTimings = new();
        private readonly long _startedAtTicks = Stopwatch.GetTimestamp();
        private long _windowStartedAtTicks = Stopwatch.GetTimestamp();
        private long _framesGenerated;
        private long _framesEncoded;
        private long _framesSent;
        private long _framesDropped;
        private long _windowFramesGenerated;
        private long _encodedBytes;
        private long _windowEncodedBytes;
        private long _lastEncodedFrameId = -1;
        private long _lastKeyFrameSeq = -1;
        private long _newFramesSent;
        private long _repeatFramesSent;
        private long _blackFramesSent;
        private long _keepaliveFramesSent;
        private long _windowNewFramesSent;
        private long _windowRepeatFramesSent;
        private long _windowBlackFramesSent;
        private long _windowKeepaliveFramesSent;
        private double _encodeMsTotal;
        private double _encodeMsMax;
        private double _windowEncodeMsTotal;
        private double _windowEncodeMsMax;
        private double _sendMsTotal;
        private double _sendMsMax;
        private double _windowSendMsTotal;
        private double _windowSendMsMax;
        private long _windowFramesEncoded;
        private long _windowFramesSent;
        private long _lateFrames;
        private long _windowLateFrames;
        private readonly List<double> _encodeSamples = new();
        private readonly List<double> _windowEncodeSamples = new();
        private readonly List<double> _sendSamples = new();
        private readonly List<double> _windowSendSamples = new();
        private readonly List<double> _localLatencySamples = new();
        private readonly List<double> _windowLocalLatencySamples = new();
        private readonly List<double> _frameIntervalSamples = new();
        private readonly List<double> _windowFrameIntervalSamples = new();

        public RealtimeEncoderStats(int fps)
        {
            _frameDurationMs = 1000.0 / Math.Max(1, fps);
        }

        public long LastEncodedFrameId
        {
            get
            {
                lock (_lock)
                {
                    return _lastEncodedFrameId;
                }
            }
        }

        public void RecordGenerated(long frameId, long timestampMs)
        {
            lock (_lock)
            {
                _framesGenerated++;
                _windowFramesGenerated++;
                _frameTimings[frameId] = new FrameTiming(timestampMs, Stopwatch.GetTimestamp(), 0);

                foreach (var oldFrameId in _frameTimings.Keys.Where(id => id < frameId - MaxTrackedFrames).ToArray())
                {
                    _frameTimings.Remove(oldFrameId);
                }
            }
        }

        public void RecordDropped(long count = 1)
        {
            if (count <= 0)
            {
                return;
            }

            lock (_lock)
            {
                _framesDropped += count;
            }
        }

        public long RecordEncodedFrame(long frameId, int payloadLength, double encodeMs = double.NaN)
        {
            lock (_lock)
            {
                _framesEncoded++;
                _windowFramesEncoded++;
                _encodedBytes += payloadLength;
                _windowEncodedBytes += payloadLength;
                _lastEncodedFrameId = Math.Max(_lastEncodedFrameId, frameId);

                var timing = _frameTimings.TryGetValue(frameId, out var existing)
                    ? existing
                    : new FrameTiming(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Stopwatch.GetTimestamp(), 0);

                var measuredEncodeMs = double.IsNaN(encodeMs) || encodeMs < 0
                    ? Math.Max(0, ((Stopwatch.GetTimestamp() - timing.GeneratedStopwatchTicks) * 1000.0 / Stopwatch.Frequency) - _frameDurationMs)
                    : encodeMs;
                _frameTimings[frameId] = timing with { EncodeMs = measuredEncodeMs };
                _encodeMsTotal += measuredEncodeMs;
                _encodeMsMax = Math.Max(_encodeMsMax, measuredEncodeMs);
                _windowEncodeMsTotal += measuredEncodeMs;
                _windowEncodeMsMax = Math.Max(_windowEncodeMsMax, measuredEncodeMs);
                _encodeSamples.Add(measuredEncodeMs);
                _windowEncodeSamples.Add(measuredEncodeMs);
                return frameId;
            }
        }

        public void RecordLateFrame()
        {
            lock (_lock)
            {
                _lateFrames++;
                _windowLateFrames++;
            }
        }

        public void RecordFrameInterval(double frameIntervalMs)
        {
            if (frameIntervalMs <= 0 || double.IsNaN(frameIntervalMs) || double.IsInfinity(frameIntervalMs))
            {
                return;
            }

            lock (_lock)
            {
                _frameIntervalSamples.Add(frameIntervalMs);
                _windowFrameIntervalSamples.Add(frameIntervalMs);
            }
        }

        public long RecordEncoded(int payloadLength, double encodeMs = double.NaN)
        {
            lock (_lock)
            {
                _framesEncoded++;
                _windowFramesEncoded++;
                _encodedBytes += payloadLength;
                _windowEncodedBytes += payloadLength;
                _lastEncodedFrameId++;

                var timing = _frameTimings.TryGetValue(_lastEncodedFrameId, out var existing)
                    ? existing
                    : new FrameTiming(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Stopwatch.GetTimestamp(), 0);

                var measuredEncodeMs = double.IsNaN(encodeMs) || encodeMs < 0
                    ? Math.Max(0, ((Stopwatch.GetTimestamp() - timing.GeneratedStopwatchTicks) * 1000.0 / Stopwatch.Frequency) - _frameDurationMs)
                    : encodeMs;
                _frameTimings[_lastEncodedFrameId] = timing with { EncodeMs = measuredEncodeMs };
                _encodeMsTotal += measuredEncodeMs;
                _encodeMsMax = Math.Max(_encodeMsMax, measuredEncodeMs);
                _windowEncodeMsTotal += measuredEncodeMs;
                _windowEncodeMsMax = Math.Max(_windowEncodeMsMax, measuredEncodeMs);
                _encodeSamples.Add(measuredEncodeMs);
                _windowEncodeSamples.Add(measuredEncodeMs);
                return _lastEncodedFrameId;
            }
        }

        public void RecordSent(EncodedVideoPacket packet, double sendMs)
        {
            lock (_lock)
            {
                if (packet.ContainsPicture)
                {
                    _framesSent++;
                    _windowFramesSent++;
                    _sendMsTotal += sendMs;
                    _sendMsMax = Math.Max(_sendMsMax, sendMs);
                    _windowSendMsTotal += sendMs;
                    _windowSendMsMax = Math.Max(_windowSendMsMax, sendMs);
                    _sendSamples.Add(sendMs);
                    _windowSendSamples.Add(sendMs);
                    var localLatencyMs = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - packet.TimestampMs);
                    _localLatencySamples.Add(localLatencyMs);
                    _windowLocalLatencySamples.Add(localLatencyMs);
                }

                switch (packet.FrameKind)
                {
                    case EncodedFrameKind.New:
                        _newFramesSent++;
                        _windowNewFramesSent++;
                        break;
                    case EncodedFrameKind.Repeat:
                        _repeatFramesSent++;
                        _windowRepeatFramesSent++;
                        break;
                    case EncodedFrameKind.Black:
                        _blackFramesSent++;
                        _windowBlackFramesSent++;
                        break;
                    case EncodedFrameKind.Keepalive:
                        _keepaliveFramesSent++;
                        _windowKeepaliveFramesSent++;
                        break;
                }

                if (packet.IsKeyFrame)
                {
                    _lastKeyFrameSeq = packet.Sequence;
                }
            }
        }

        public long GetFrameTimestamp(long frameId)
        {
            lock (_lock)
            {
                return _frameTimings.TryGetValue(frameId, out var timing)
                    ? timing.TimestampMs
                    : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }

        public double GetFrameEncodeMs(long frameId)
        {
            lock (_lock)
            {
                return _frameTimings.TryGetValue(frameId, out var timing) ? timing.EncodeMs : 0;
            }
        }

        public RealtimeEncoderStatsSnapshot Snapshot()
        {
            lock (_lock)
            {
                var encodePercentiles = SampleStatistics.Percentiles(_encodeSamples);
                var sendPercentiles = SampleStatistics.Percentiles(_sendSamples);
                var localLatencyPercentiles = SampleStatistics.Percentiles(_localLatencySamples);
                var frameIntervalPercentiles = SampleStatistics.Percentiles(_frameIntervalSamples);
                var elapsedSeconds = Math.Max(0.001, (Stopwatch.GetTimestamp() - _startedAtTicks) / (double)Stopwatch.Frequency);
                return CreateSnapshot(
                    framesEncodedForAverage: _framesEncoded,
                    framesSentForAverage: _framesSent,
                    encodeMsTotal: _encodeMsTotal,
                    encodeMsMax: _encodeMsMax,
                    sendMsTotal: _sendMsTotal,
                    sendMsMax: _sendMsMax,
                    encodedBytes: _encodedBytes,
                    seconds: elapsedSeconds,
                    encodeP50: encodePercentiles.P50,
                    encodeP95: encodePercentiles.P95,
                    encodeP99: encodePercentiles.P99,
                    sendP50: sendPercentiles.P50,
                    sendP95: sendPercentiles.P95,
                    sendP99: sendPercentiles.P99,
                    localLatencyP50: localLatencyPercentiles.P50,
                    localLatencyP95: localLatencyPercentiles.P95,
                    localLatencyP99: localLatencyPercentiles.P99,
                    frameIntervalP50: frameIntervalPercentiles.P50,
                    frameIntervalP95: frameIntervalPercentiles.P95,
                    frameIntervalP99: frameIntervalPercentiles.P99,
                    streamFps: _framesGenerated / elapsedSeconds,
                    lateFrames: _lateFrames,
                    newFramesSent: _newFramesSent,
                    repeatFramesSent: _repeatFramesSent,
                    blackFramesSent: _blackFramesSent,
                    keepaliveFramesSent: _keepaliveFramesSent);
            }
        }

        public RealtimeEncoderStatsSnapshot SnapshotAndResetWindow()
        {
            lock (_lock)
            {
                var encodePercentiles = SampleStatistics.Percentiles(_windowEncodeSamples);
                var sendPercentiles = SampleStatistics.Percentiles(_windowSendSamples);
                var localLatencyPercentiles = SampleStatistics.Percentiles(_windowLocalLatencySamples);
                var frameIntervalPercentiles = SampleStatistics.Percentiles(_windowFrameIntervalSamples);
                var elapsedSeconds = Math.Max(0.001, (Stopwatch.GetTimestamp() - _windowStartedAtTicks) / (double)Stopwatch.Frequency);
                var snapshot = CreateSnapshot(
                    framesEncodedForAverage: _windowFramesEncoded,
                    framesSentForAverage: _windowFramesSent,
                    encodeMsTotal: _windowEncodeMsTotal,
                    encodeMsMax: _windowEncodeMsMax,
                    sendMsTotal: _windowSendMsTotal,
                    sendMsMax: _windowSendMsMax,
                    encodedBytes: _windowEncodedBytes,
                    seconds: elapsedSeconds,
                    encodeP50: encodePercentiles.P50,
                    encodeP95: encodePercentiles.P95,
                    encodeP99: encodePercentiles.P99,
                    sendP50: sendPercentiles.P50,
                    sendP95: sendPercentiles.P95,
                    sendP99: sendPercentiles.P99,
                    localLatencyP50: localLatencyPercentiles.P50,
                    localLatencyP95: localLatencyPercentiles.P95,
                    localLatencyP99: localLatencyPercentiles.P99,
                    frameIntervalP50: frameIntervalPercentiles.P50,
                    frameIntervalP95: frameIntervalPercentiles.P95,
                    frameIntervalP99: frameIntervalPercentiles.P99,
                    streamFps: _windowFramesGenerated / elapsedSeconds,
                    lateFrames: _windowLateFrames,
                    newFramesSent: _windowNewFramesSent,
                    repeatFramesSent: _windowRepeatFramesSent,
                    blackFramesSent: _windowBlackFramesSent,
                    keepaliveFramesSent: _windowKeepaliveFramesSent);

                _windowFramesEncoded = 0;
                _windowFramesGenerated = 0;
                _windowFramesSent = 0;
                _windowLateFrames = 0;
                _windowEncodedBytes = 0;
                _windowEncodeMsTotal = 0;
                _windowEncodeMsMax = 0;
                _windowSendMsTotal = 0;
                _windowSendMsMax = 0;
                _windowNewFramesSent = 0;
                _windowRepeatFramesSent = 0;
                _windowBlackFramesSent = 0;
                _windowKeepaliveFramesSent = 0;
                _windowEncodeSamples.Clear();
                _windowSendSamples.Clear();
                _windowLocalLatencySamples.Clear();
                _windowFrameIntervalSamples.Clear();
                _windowStartedAtTicks = Stopwatch.GetTimestamp();
                return snapshot;
            }
        }

        private RealtimeEncoderStatsSnapshot CreateSnapshot(
            long framesEncodedForAverage,
            long framesSentForAverage,
            double encodeMsTotal,
            double encodeMsMax,
            double sendMsTotal,
            double sendMsMax,
            long encodedBytes,
            double seconds,
            double encodeP50,
            double encodeP95,
            double encodeP99,
            double sendP50,
            double sendP95,
            double sendP99,
            double localLatencyP50,
            double localLatencyP95,
            double localLatencyP99,
            double frameIntervalP50,
            double frameIntervalP95,
            double frameIntervalP99,
            double streamFps,
            long lateFrames,
            long newFramesSent,
            long repeatFramesSent,
            long blackFramesSent,
            long keepaliveFramesSent)
        {
            var avgEncodeMs = framesEncodedForAverage == 0 ? 0 : encodeMsTotal / framesEncodedForAverage;
            var avgSendMs = framesSentForAverage == 0 ? 0 : sendMsTotal / framesSentForAverage;
            var outputKbps = seconds <= 0 ? 0 : encodedBytes * 8.0 / seconds / 1000.0;
            return new RealtimeEncoderStatsSnapshot(
                _framesGenerated,
                _framesEncoded,
                _framesSent,
                _framesDropped,
                avgEncodeMs,
                encodeMsMax,
                encodeP50,
                encodeP95,
                encodeP99,
                avgSendMs,
                sendMsMax,
                sendP50,
                sendP95,
                sendP99,
                outputKbps,
                _lastKeyFrameSeq,
                streamFps,
                localLatencyP50,
                localLatencyP95,
                localLatencyP99,
                frameIntervalP50,
                frameIntervalP95,
                frameIntervalP99,
                lateFrames,
                newFramesSent,
                repeatFramesSent,
                blackFramesSent,
                keepaliveFramesSent);
        }

        private sealed record FrameTiming(long TimestampMs, long GeneratedStopwatchTicks, double EncodeMs);
    }

    private sealed class H264AnnexBStream
    {
        public H264AnnexBStream(IReadOnlyList<VideoPacket> packets)
        {
            Packets = packets;
            PacketCount = packets.Count;
            FramePacketCount = packets.Count(packet => packet.ContainsPicture);
        }

        public IReadOnlyList<VideoPacket> Packets { get; }

        public int PacketCount { get; }

        public int FramePacketCount { get; }

        public static H264AnnexBStream Load(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("H.264 test stream not found.", path);
            }

            var data = File.ReadAllBytes(path);
            var nals = FindNalUnits(data);
            if (nals.Count == 0)
            {
                throw new InvalidDataException("未找到 Annex B start code。");
            }

            var packets = BuildAccessUnitPackets(data, nals);
            if (packets.Count == 0)
            {
                throw new InvalidDataException("未能从 H.264 文件中拆出可发送的视频包。");
            }

            return new H264AnnexBStream(packets);
        }

        private static IReadOnlyList<NalUnit> FindNalUnits(byte[] data)
        {
            var starts = new List<(int StartCodeOffset, int PrefixLength, int NalOffset)>();
            var index = 0;
            while (index < data.Length - 3)
            {
                if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 0 && data[index + 3] == 1)
                {
                    starts.Add((index, 4, index + 4));
                    index += 4;
                    continue;
                }

                if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1)
                {
                    starts.Add((index, 3, index + 3));
                    index += 3;
                    continue;
                }

                index++;
            }

            var nals = new List<NalUnit>(starts.Count);
            for (var startIndex = 0; startIndex < starts.Count; startIndex++)
            {
                var start = starts[startIndex];
                if (start.NalOffset >= data.Length)
                {
                    continue;
                }

                var endOffset = startIndex + 1 < starts.Count ? starts[startIndex + 1].StartCodeOffset : data.Length;
                if (endOffset <= start.NalOffset)
                {
                    continue;
                }

                var nalType = data[start.NalOffset] & 0x1F;
                nals.Add(new NalUnit(start.StartCodeOffset, start.NalOffset, endOffset, nalType));
            }

            return nals;
        }

        private static IReadOnlyList<VideoPacket> BuildAccessUnitPackets(byte[] data, IReadOnlyList<NalUnit> nals)
        {
            if (nals.Any(nal => nal.Type == 9))
            {
                return BuildAudDelimitedPackets(data, nals);
            }

            var packets = new List<VideoPacket>(nals.Count);
            foreach (var nal in nals)
            {
                packets.Add(CreatePacket(data, nal.StartCodeOffset, nal.EndOffset, new[] { nal }));
            }

            return packets;
        }

        private static IReadOnlyList<VideoPacket> BuildAudDelimitedPackets(byte[] data, IReadOnlyList<NalUnit> nals)
        {
            var packets = new List<VideoPacket>();
            var groupStart = 0;

            for (var index = 0; index < nals.Count; index++)
            {
                if (nals[index].Type == 9 && index > groupStart && ContainsPicture(nals, groupStart, index - 1))
                {
                    packets.Add(CreatePacket(data, nals[groupStart].StartCodeOffset, nals[index].StartCodeOffset, Slice(nals, groupStart, index - 1)));
                    groupStart = index;
                }
            }

            if (groupStart < nals.Count)
            {
                packets.Add(CreatePacket(data, nals[groupStart].StartCodeOffset, nals[^1].EndOffset, Slice(nals, groupStart, nals.Count - 1)));
            }

            return packets;
        }

        private static VideoPacket CreatePacket(byte[] data, int startOffset, int endOffset, IReadOnlyList<NalUnit> nals)
        {
            var payload = data[startOffset..endOffset];
            var containsPicture = nals.Any(IsPictureNal);
            var isKeyFrame = nals.Any(nal => nal.Type == 5);
            return new VideoPacket(payload, isKeyFrame, containsPicture);
        }

        private static IReadOnlyList<NalUnit> Slice(IReadOnlyList<NalUnit> nals, int start, int endInclusive)
        {
            var result = new List<NalUnit>(endInclusive - start + 1);
            for (var index = start; index <= endInclusive; index++)
            {
                result.Add(nals[index]);
            }

            return result;
        }

        public static bool ContainsPicture(byte[] payload)
        {
            var nals = FindNalUnits(payload);
            return nals.Any(IsPictureNal);
        }

        public static bool ContainsNalType(byte[] payload, int nalType)
        {
            var nals = FindNalUnits(payload);
            return nals.Any(nal => nal.Type == nalType);
        }

        private static bool ContainsPicture(IReadOnlyList<NalUnit> nals, int start, int endInclusive)
        {
            for (var index = start; index <= endInclusive; index++)
            {
                if (IsPictureNal(nals[index]))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsPictureNal(NalUnit nal)
        {
            return nal.Type is >= 1 and <= 5;
        }

        public static bool IsFirstSlice(NalUnit nal)
        {
            return IsPictureNal(nal) && nal.NalOffset + 1 < nal.EndOffset && (nal.Type == 5 || (nal.StartCodeOffset >= 0));
        }
    }

    private sealed record H264EncoderTuning(
        string PresetName,
        string ResolutionPreset,
        int Bitrate,
        int MaxBitrate,
        int BufferSize,
        int Gop,
        int BFrames,
        string Profile,
        int ProfileValue,
        string RateControl,
        bool LowLatency,
        int RecommendedBitrate,
        int LegacyAutoBitrate,
        string BitrateSource,
        string GopSource)
    {
        private const double LegacyBitsPerPixel = 0.145;
        private const int H264BaselineProfileValue = 66;
        private static readonly H264ResolutionTuning[] ResolutionTunings =
        [
            new("720p", 1280, 720, 4_000_000, 6_000_000, 12_000_000, 60),
            new("1080p", 1920, 1080, 8_000_000, 14_000_000, 28_000_000, 60),
            new("2k", 2560, 1440, 16_000_000, 28_000_000, 56_000_000, 120)
        ];

        public int BitrateDeltaFromLegacy => Bitrate - LegacyAutoBitrate;

        public static H264EncoderTuning FromOptions(HostOptions options)
        {
            var recommendedBitrate = RecommendedBitrateForMode(options.VideoWidth, options.VideoHeight, options.VideoFps);
            var legacyAutoBitrate = LegacyAutoBitrateForMode(options.VideoWidth, options.VideoHeight, options.VideoFps);
            var recommendedGop = RecommendedGop(options.VideoWidth, options.VideoHeight, options.VideoFps);
            var bitrate = options.AutoVideoBitrate ? recommendedBitrate : options.VideoBitrate;
            var gop = options.AutoVideoGop ? recommendedGop : options.VideoGop;
            var resolutionPreset = ResolutionPresetNameFor(options.VideoWidth, options.VideoHeight);
            var presetName = $"{resolutionPreset}@{FpsTierName(options.VideoFps)}-low-latency";

            return new H264EncoderTuning(
                presetName,
                resolutionPreset,
                bitrate,
                bitrate,
                LowLatencyBufferSize(bitrate),
                gop,
                BFrames: 0,
                Profile: "baseline",
                ProfileValue: H264BaselineProfileValue,
                RateControl: "cbr",
                LowLatency: true,
                recommendedBitrate,
                legacyAutoBitrate,
                options.AutoVideoBitrate ? "preset" : "manual",
                options.AutoVideoGop ? "preset" : "manual");
        }

        public static int RecommendedBitrateForMode(int width, int height, int fps)
        {
            var preset = FindResolutionTuning(width, height);
            if (preset is not null)
            {
                return SelectBitrateForFps(preset, fps);
            }

            var pixelsPerSecond = width * (double)height * Math.Max(1, fps);
            var bitsPerPixel = fps >= 90 ? 0.11 : fps >= 50 ? 0.115 : 0.14;
            var bitrate = pixelsPerSecond * bitsPerPixel;
            return RoundBitrate(Math.Clamp((int)Math.Round(bitrate), 4_000_000, 64_000_000));
        }

        public static int RecommendedGop(int width, int height, int fps)
        {
            var preset = FindResolutionTuning(width, height);
            if (preset is not null && fps >= 90)
            {
                return preset.Gop120Fps;
            }

            return fps >= 90 ? 60 : 30;
        }

        private static int LegacyAutoBitrateForMode(int width, int height, int fps)
        {
            var pixelsPerSecond = width * (double)height * fps;
            var bitrate = pixelsPerSecond * LegacyBitsPerPixel;
            return (int)Math.Clamp(Math.Round(bitrate / 1_000_000.0) * 1_000_000, 4_000_000, 64_000_000);
        }

        private static int SelectBitrateForFps(H264ResolutionTuning tuning, int fps)
        {
            if (fps >= 90)
            {
                return tuning.Bitrate120Fps;
            }

            return fps >= 50 ? tuning.Bitrate60Fps : tuning.Bitrate30Fps;
        }

        private static H264ResolutionTuning? FindResolutionTuning(int width, int height)
        {
            return ResolutionTunings.FirstOrDefault(tuning => tuning.Width == width && tuning.Height == height);
        }

        private static int LowLatencyBufferSize(int bitrate)
        {
            return RoundBitrate(Math.Max(2_000_000, bitrate / 2));
        }

        private static int RoundBitrate(int bitrate)
        {
            return (int)Math.Max(1_000_000, Math.Round(bitrate / 1_000_000.0) * 1_000_000);
        }

        private static string ResolutionPresetNameFor(int width, int height)
        {
            return (width, height) switch
            {
                (1280, 720) => "720p",
                (1920, 1080) => "1080p",
                (2560, 1440) => "2k",
                _ => "custom"
            };
        }

        private static string FpsTierName(int fps)
        {
            if (fps >= 90)
            {
                return "120fps";
            }

            return fps >= 50 ? "60fps" : "30fps";
        }
    }

    private sealed record H264ResolutionTuning(
        string Name,
        int Width,
        int Height,
        int Bitrate30Fps,
        int Bitrate60Fps,
        int Bitrate120Fps,
        int Gop120Fps);

    private sealed record HostOptions(
        int ControlPort,
        int VideoPort,
        int AudioPort,
        int CameraPort,
        string VideoFilePath,
        VideoSourceKind VideoSource,
        H264EncoderKind Encoder,
        string? FfmpegPath,
        int VideoWidth,
        int VideoHeight,
        int VideoFps,
        int VideoBitrate,
        string ResolutionPreset,
        bool AutoVideoBitrate,
        bool AutoVideoGop,
        int VideoGop,
        string? DumpEncodedPath,
        int MaxVideoQueue,
        int Nv12PoolSize,
        int EncodedPacketQueue,
        bool EnableInputInjection,
        InputTargetKind InputTarget,
        bool ListWindows,
        CaptureRectangle? CaptureRegion,
        string? WindowTitle,
        string? ProcessName,
        DisplayModeRequest? RequestedDisplayMode,
        string? DumpGpuFrameDirectory,
        bool AudioDeviceEnabled,
        bool MicrophoneEnabled,
        bool SpeakerEnabled,
        bool CameraEnabled,
        int CameraWidth,
        int CameraHeight,
        int CameraFps,
        string CameraCodec,
        string CameraFacing,
        AudioBackendKind AudioBackend,
        string? AudioOutputLoopbackEndpointId,
        string? AudioMicrophoneRenderEndpointId,
        string? CameraReplayFilePath)
    {
        public IReadOnlyList<int> ReversePorts { get; } = new[]
            {
                ControlPort,
                VideoPort,
                AudioDeviceEnabled && (MicrophoneEnabled || SpeakerEnabled) ? AudioPort : 0,
                CameraEnabled ? CameraPort : 0
            }
            .Where(port => port > 0)
            .Distinct()
            .ToArray();

        public H264EncoderTuning EncoderTuning => H264EncoderTuning.FromOptions(this);

        public static HostOptions Parse(string[] args)
        {
            var controlPort = DefaultControlPort;
            var videoPort = DefaultVideoPort;
            var audioPort = DefaultAudioPort;
            var cameraPort = DefaultCameraPort;
            var videoFile = Path.GetFullPath(DefaultVideoFile);
            var videoWidth = DefaultVideoWidth;
            var videoHeight = DefaultVideoHeight;
            var videoFps = DefaultVideoFps;
            var cameraWidth = DefaultCameraWidth;
            var cameraHeight = DefaultCameraHeight;
            var cameraFps = DefaultCameraFps;
            var cameraCodec = "video/avc";
            var cameraFacing = DefaultCameraFacing;
            var resolutionPreset = DefaultResolutionPreset;
            var videoSource = DefaultVideoSource;
            var encoder = H264EncoderKind.MediaFoundation;
            string? ffmpegPath = null;
            var videoBitrate = DefaultVideoBitrate;
            var autoVideoBitrate = true;
            var autoVideoGop = true;
            var videoGop = DefaultVideoGop;
            string? dumpEncodedPath = null;
            var maxVideoQueue = DefaultMaxVideoQueue;
            var nv12PoolSize = DefaultNv12PoolSize;
            var encodedPacketQueue = DefaultMaxVideoQueue;
            var enableInputInjection = false;
            InputTargetKind? inputTarget = null;
            var listWindows = false;
            CaptureRectangle? captureRegion = null;
            string? windowTitle = null;
            string? processName = null;
            DisplayModeRequest? requestedDisplayMode = null;
            string? dumpGpuFrameDirectory = null;
            var audioDeviceEnabled = true;
            var microphoneEnabled = true;
            var speakerEnabled = true;
            var cameraEnabled = true;
            var audioBackend = AudioBackendKind.LegacySharedMemory;
            string? audioOutputLoopbackEndpointId = null;
            string? audioMicrophoneRenderEndpointId = null;
            string? cameraReplayFilePath = null;

            foreach (var arg in args)
            {
                if (arg.Equals("--enable-input-injection", StringComparison.OrdinalIgnoreCase))
                {
                    enableInputInjection = true;
                }
                else if (arg.Equals("--list-windows", StringComparison.OrdinalIgnoreCase))
                {
                    listWindows = true;
                }
                else if (arg.Equals("--disable-audio", StringComparison.OrdinalIgnoreCase))
                {
                    audioDeviceEnabled = false;
                }
                else if (arg.Equals("--disable-microphone", StringComparison.OrdinalIgnoreCase))
                {
                    microphoneEnabled = false;
                }
                else if (arg.Equals("--disable-speaker", StringComparison.OrdinalIgnoreCase))
                {
                    speakerEnabled = false;
                }
                else if (arg.Equals("--disable-camera", StringComparison.OrdinalIgnoreCase))
                {
                    cameraEnabled = false;
                }
            }

            for (var index = 0; index < args.Length - 1; index++)
            {
                switch (args[index])
                {
                    case "--port":
                    case "--control-port":
                        if (int.TryParse(args[index + 1], out var parsedControlPort))
                        {
                            controlPort = parsedControlPort;
                        }

                        break;

                    case "--video-port":
                        if (int.TryParse(args[index + 1], out var parsedVideoPort))
                        {
                            videoPort = parsedVideoPort;
                        }

                        break;

                    case "--audio-port":
                        if (int.TryParse(args[index + 1], out var parsedAudioPort))
                        {
                            audioPort = parsedAudioPort;
                        }

                        break;

                    case "--camera-port":
                        if (int.TryParse(args[index + 1], out var parsedCameraPort))
                        {
                            cameraPort = parsedCameraPort;
                        }

                        break;

                    case "--camera-width":
                        if (int.TryParse(args[index + 1], out var parsedCameraWidth) && parsedCameraWidth > 0)
                        {
                            cameraWidth = parsedCameraWidth;
                        }

                        break;

                    case "--camera-height":
                        if (int.TryParse(args[index + 1], out var parsedCameraHeight) && parsedCameraHeight > 0)
                        {
                            cameraHeight = parsedCameraHeight;
                        }

                        break;

                    case "--camera-fps":
                        if (int.TryParse(args[index + 1], out var parsedCameraFps) && parsedCameraFps > 0)
                        {
                            cameraFps = parsedCameraFps;
                        }

                        break;

                    case "--camera-codec":
                        if (!string.IsNullOrWhiteSpace(args[index + 1]))
                        {
                            cameraCodec = args[index + 1].Trim();
                        }

                        break;

                    case "--camera-facing":
                    case "--camera-lens-facing":
                        cameraFacing = NormalizeCameraFacing(args[index + 1]);
                        break;

                    case "--camera-replay":
                    case "--camera-replay-file":
                        cameraReplayFilePath = Path.GetFullPath(args[index + 1]);
                        break;

                    case "--audio-backend":
                        audioBackend = ParseAudioBackend(args[index + 1]);
                        break;

                    case "--audio-output-loopback-endpoint-id":
                    case "--audio-speaker-render-endpoint-id":
                    case "--audio-speaker-capture-endpoint-id":
                        audioOutputLoopbackEndpointId = args[index + 1];
                        break;

                    case "--audio-microphone-render-endpoint-id":
                        audioMicrophoneRenderEndpointId = args[index + 1];
                        break;

                    case "--video-file":
                        videoFile = Path.GetFullPath(args[index + 1]);
                        break;

                    case "--video-source":
                        videoSource = ParseVideoSource(args[index + 1]);
                        break;

                    case "--encoder":
                        encoder = ParseEncoder(args[index + 1]);
                        break;

                    case "--ffmpeg-path":
                        ffmpegPath = args[index + 1];
                        break;

                    case "--resolution":
                    case "--resolution-preset":
                        var resolution = ParseResolutionPreset(args[index + 1]);
                        resolutionPreset = resolution.Name;
                        videoWidth = resolution.Width;
                        videoHeight = resolution.Height;
                        break;

                    case "--refresh-rate":
                    case "--refresh-hz":
                        if (int.TryParse(args[index + 1].TrimEnd('h', 'z', 'H', 'Z'), out var parsedRefreshRate))
                        {
                            videoFps = ValidateRefreshRate(parsedRefreshRate);
                        }

                        break;

                    case "--video-width":
                        if (int.TryParse(args[index + 1], out var parsedVideoWidth))
                        {
                            videoWidth = parsedVideoWidth;
                            resolutionPreset = "custom";
                        }

                        break;

                    case "--video-height":
                        if (int.TryParse(args[index + 1], out var parsedVideoHeight))
                        {
                            videoHeight = parsedVideoHeight;
                            resolutionPreset = "custom";
                        }

                        break;

                    case "--video-fps":
                        if (int.TryParse(args[index + 1], out var parsedVideoFps) && parsedVideoFps > 0)
                        {
                            videoFps = parsedVideoFps;
                        }

                        break;

                    case "--video-bitrate":
                        if (int.TryParse(args[index + 1], out var parsedVideoBitrate) && parsedVideoBitrate > 0)
                        {
                            videoBitrate = parsedVideoBitrate;
                            autoVideoBitrate = false;
                        }

                        break;

                    case "--video-gop":
                        if (int.TryParse(args[index + 1], out var parsedVideoGop) && parsedVideoGop > 0)
                        {
                            videoGop = parsedVideoGop;
                            autoVideoGop = false;
                        }

                        break;

                    case "--dump-encoded":
                        dumpEncodedPath = Path.GetFullPath(args[index + 1]);
                        break;

                    case "--dump-gpu-frame":
                        dumpGpuFrameDirectory = Path.GetFullPath(args[index + 1]);
                        break;

                    case "--max-video-queue":
                        if (int.TryParse(args[index + 1], out var parsedMaxVideoQueue) && parsedMaxVideoQueue > 0)
                        {
                            maxVideoQueue = parsedMaxVideoQueue;
                            encodedPacketQueue = parsedMaxVideoQueue;
                        }

                        break;

                    case "--nv12-pool-size":
                    case "--nv12-texture-pool":
                    case "--host-nv12-pool":
                        if (int.TryParse(args[index + 1], out var parsedNv12PoolSize) && parsedNv12PoolSize > 0)
                        {
                            nv12PoolSize = parsedNv12PoolSize;
                        }

                        break;

                    case "--encoded-packet-queue":
                    case "--packet-queue":
                        if (int.TryParse(args[index + 1], out var parsedEncodedPacketQueue) && parsedEncodedPacketQueue > 0)
                        {
                            encodedPacketQueue = parsedEncodedPacketQueue;
                            maxVideoQueue = parsedEncodedPacketQueue;
                        }

                        break;

                    case "--input-target":
                        inputTarget = ParseInputTarget(args[index + 1]);
                        break;

                    case "--capture-region":
                        captureRegion = ParseCaptureRegion(args[index + 1]);
                        break;

                    case "--window-title":
                        windowTitle = args[index + 1];
                        break;

                    case "--process-name":
                        processName = args[index + 1];
                        break;

                    case "--display-mode":
                        requestedDisplayMode = ParseDisplayMode(args[index + 1], videoFps);
                        videoWidth = requestedDisplayMode.Width;
                        videoHeight = requestedDisplayMode.Height;
                        videoFps = requestedDisplayMode.RefreshHz;
                        resolutionPreset = ResolutionPresetNameFor(videoWidth, videoHeight);
                        break;
                }
            }

            if (autoVideoBitrate)
            {
                videoBitrate = RecommendedBitrate(videoWidth, videoHeight, videoFps);
            }

            if (autoVideoGop)
            {
                videoGop = H264EncoderTuning.RecommendedGop(videoWidth, videoHeight, videoFps);
            }

            if (IsIddVideoSource(videoSource))
            {
                requestedDisplayMode = new DisplayModeRequest(videoWidth, videoHeight, videoFps);
            }

            if (videoSource == VideoSourceKind.Region && captureRegion is null)
            {
                throw new ArgumentException("--capture-region x,y,width,height is required when --video-source region is used.");
            }

            if (videoSource == VideoSourceKind.Window &&
                string.IsNullOrWhiteSpace(windowTitle) &&
                string.IsNullOrWhiteSpace(processName))
            {
                throw new ArgumentException("--window-title or --process-name is required when --video-source window is used.");
            }

            inputTarget ??= IsIddVideoSource(videoSource) ? InputTargetKind.Idd : InputTargetKind.System;

            return new HostOptions(
                controlPort,
                videoPort,
                audioPort,
                cameraPort,
                videoFile,
                videoSource,
                encoder,
                ffmpegPath,
                videoWidth,
                videoHeight,
                videoFps,
                videoBitrate,
                resolutionPreset,
                autoVideoBitrate,
                autoVideoGop,
                videoGop,
                dumpEncodedPath,
                maxVideoQueue,
                nv12PoolSize,
                encodedPacketQueue,
                enableInputInjection,
                inputTarget.Value,
                listWindows,
                captureRegion,
                windowTitle,
                processName,
                requestedDisplayMode,
                dumpGpuFrameDirectory,
                audioDeviceEnabled,
                microphoneEnabled,
                speakerEnabled,
                cameraEnabled,
                cameraWidth,
                cameraHeight,
                cameraFps,
                cameraCodec,
                cameraFacing,
                audioBackend,
                NormalizeOptionalAudioEndpointId(audioOutputLoopbackEndpointId),
                NormalizeOptionalAudioEndpointId(audioMicrophoneRenderEndpointId),
                NormalizeOptionalPath(cameraReplayFilePath));
        }

        private static string? NormalizeOptionalAudioEndpointId(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string NormalizeCameraFacing(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return DefaultCameraFacing;
            }

            return value.Trim().Equals("front", StringComparison.OrdinalIgnoreCase) ? "front" : "back";
        }

        private static string? NormalizeOptionalPath(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim());
        }

        private static DisplayModeRequest ParseDisplayMode(string value, int fallbackRefreshHz)
        {
            var normalized = value.Trim().ToLowerInvariant();
            var refreshHz = fallbackRefreshHz;
            var size = normalized;
            var atIndex = normalized.IndexOf('@', StringComparison.Ordinal);
            if (atIndex >= 0)
            {
                size = normalized[..atIndex];
                var refreshPart = normalized[(atIndex + 1)..].TrimEnd('h', 'z');
                if (!int.TryParse(refreshPart, out refreshHz) || refreshHz <= 0)
                {
                    throw new ArgumentException("--display-mode refresh must be a positive integer, for example 1600x900@30.");
                }
            }

            var parts = size.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var width)
                || !int.TryParse(parts[1], out var height)
                || width <= 0
                || height <= 0)
            {
                throw new ArgumentException("--display-mode must use widthxheight or widthxheight@refresh, for example 1600x900@30.");
            }

            return new DisplayModeRequest(width, height, refreshHz);
        }

        private static ResolutionPreset ParseResolutionPreset(string value)
        {
            var normalized = value.Trim().ToLowerInvariant();
            return normalized switch
            {
                "720" or "720p" or "hd" => new ResolutionPreset("720p", 1280, 720),
                "1080" or "1080p" or "fhd" or "fullhd" => new ResolutionPreset("1080p", 1920, 1080),
                "2k" or "1440" or "1440p" or "qhd" => new ResolutionPreset("2k", 2560, 1440),
                _ => throw new ArgumentException($"Unsupported --resolution value: {value}. Use 720p, 1080p, or 2k.")
            };
        }

        private static int ValidateRefreshRate(int refreshRate)
        {
            return refreshRate switch
            {
                30 or 60 or 120 => refreshRate,
                _ => throw new ArgumentException($"Unsupported --refresh-rate value: {refreshRate}. Use 30, 60, or 120.")
            };
        }

        private static string ResolutionPresetNameFor(int width, int height)
        {
            return (width, height) switch
            {
                (1280, 720) => "720p",
                (1920, 1080) => "1080p",
                (2560, 1440) => "2k",
                _ => "custom"
            };
        }

        private static int RecommendedBitrate(int width, int height, int refreshRate)
        {
            return H264EncoderTuning.RecommendedBitrateForMode(width, height, refreshRate);
        }

        private static VideoSourceKind ParseVideoSource(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "file" => VideoSourceKind.File,
                "realtime" => VideoSourceKind.Realtime,
                "synthetic-nv12" => VideoSourceKind.SyntheticNv12,
                "region" => VideoSourceKind.Region,
                "window" => VideoSourceKind.Window,
                "idd" => VideoSourceKind.Idd,
                "idd-gpu" => VideoSourceKind.IddGpu,
                _ => throw new ArgumentException($"Unsupported --video-source value: {value}")
            };
        }

        private static AudioBackendKind ParseAudioBackend(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "legacy" or "legacy-shared-memory" or "shared-memory" => AudioBackendKind.LegacySharedMemory,
                "wasapi" or "wasapi-virtual-cable" or "virtual-cable" => AudioBackendKind.WasapiVirtualCable,
                _ => throw new ArgumentException(
                    $"Unsupported --audio-backend value: {value}. Use legacy-shared-memory or wasapi-virtual-cable.")
            };
        }

        private static InputTargetKind ParseInputTarget(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "idd" => InputTargetKind.Idd,
                "system" => InputTargetKind.System,
                _ => throw new ArgumentException($"Unsupported --input-target value: {value}")
            };
        }

        private static CaptureRectangle ParseCaptureRegion(string value)
        {
            var parts = value.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 4 ||
                !int.TryParse(parts[0], out var x) ||
                !int.TryParse(parts[1], out var y) ||
                !int.TryParse(parts[2], out var width) ||
                !int.TryParse(parts[3], out var height) ||
                width <= 0 ||
                height <= 0)
            {
                throw new ArgumentException("--capture-region must use x,y,width,height with positive width and height.");
            }

            return new CaptureRectangle(x, y, width, height);
        }

        private static H264EncoderKind ParseEncoder(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "mediafoundation" => H264EncoderKind.MediaFoundation,
                "ffmpeg" => H264EncoderKind.Ffmpeg,
                _ => throw new ArgumentException($"Unsupported --encoder value: {value}")
            };
        }

        public HostOptions WithVideoMode(VideoMode mode)
        {
            return this with
            {
                VideoWidth = mode.Width,
                VideoHeight = mode.Height,
                VideoFps = mode.Fps,
                VideoBitrate = mode.Bitrate,
                VideoGop = mode.Gop,
                ResolutionPreset = mode.ResolutionPreset
            };
        }
    }

    private sealed class VideoModeState
    {
        private readonly object _lock = new();
        private readonly HostOptions _baseOptions;
        private VideoMode _current;

        public VideoModeState(HostOptions options)
        {
            _baseOptions = options;
            _current = VideoMode.FromOptions(options);
        }

        public VideoMode Current
        {
            get
            {
                lock (_lock)
                {
                    return _current;
                }
            }
        }

        public VideoMode Set(int width, int height, int fps)
        {
            var normalizedFps = fps > 0 ? fps : Current.Fps;
            var bitrate = _baseOptions.AutoVideoBitrate
                ? RecommendedBitrate(width, height, normalizedFps)
                : _baseOptions.VideoBitrate;
            var gop = _baseOptions.AutoVideoGop
                ? H264EncoderTuning.RecommendedGop(width, height, normalizedFps)
                : _baseOptions.VideoGop;
            var next = new VideoMode(
                width,
                height,
                normalizedFps,
                bitrate,
                gop,
                ResolutionPresetNameFor(width, height));

            lock (_lock)
            {
                _current = next;
                return _current;
            }
        }

        public HostOptions CreateOptionsSnapshot()
        {
            lock (_lock)
            {
                return _baseOptions.WithVideoMode(_current);
            }
        }

        private static string ResolutionPresetNameFor(int width, int height)
        {
            return (width, height) switch
            {
                (1280, 720) => "720p",
                (1920, 1080) => "1080p",
                (2560, 1440) => "2k",
                _ => "custom"
            };
        }

        private static int RecommendedBitrate(int width, int height, int refreshRate)
        {
            return H264EncoderTuning.RecommendedBitrateForMode(width, height, refreshRate);
        }
    }

    private readonly record struct VideoMode(
        int Width,
        int Height,
        int Fps,
        int Bitrate,
        int Gop,
        string ResolutionPreset)
    {
        public static VideoMode FromOptions(HostOptions options)
        {
            return new VideoMode(
                options.VideoWidth,
                options.VideoHeight,
                options.VideoFps,
                options.VideoBitrate,
                options.VideoGop,
                options.ResolutionPreset);
        }
    }

    private enum VideoSourceKind
    {
        File,
        Realtime,
        SyntheticNv12,
        Region,
        Window,
        Idd,
        IddGpu
    }

    private enum InputTargetKind
    {
        System,
        Idd
    }

    private enum AudioBackendKind
    {
        LegacySharedMemory,
        WasapiVirtualCable
    }

    private enum H264EncoderKind
    {
        MediaFoundation,
        Ffmpeg
    }

    private sealed record ResolutionPreset(string Name, int Width, int Height);

    private sealed record EncodedVideoPacket(
        byte[] Payload,
        bool IsKeyFrame,
        bool ContainsPicture,
        long FrameId,
        long Sequence,
        long TimestampMs,
        double EncodeMs,
        EncodedFrameKind FrameKind,
        long SourceSequence,
        double SourceAgeMs);

    private sealed record VideoPacket(byte[] Payload, bool IsKeyFrame, bool ContainsPicture);

    private sealed record NalUnit(int StartCodeOffset, int NalOffset, int EndOffset, int Type);

    private sealed record AccessUnitBoundary(int StartNalIndex, int EndNalIndexExclusive);

    private sealed record CaptureRectangle(int X, int Y, int Width, int Height)
    {
        public override string ToString()
        {
            return $"{X},{Y},{Width},{Height}";
        }
    }

    private sealed record DisplayModeRequest(int Width, int Height, int RefreshHz)
    {
        public override string ToString()
        {
            return $"{Width}x{Height}@{RefreshHz}";
        }
    }

    private readonly record struct WindowInfo(
        IntPtr Handle,
        int ProcessId,
        string ProcessName,
        string Title,
        int X,
        int Y,
        int Width,
        int Height);

    private sealed record CaptureStatsSnapshot(
        long FramesCaptured,
        long FramesConverted,
        long CaptureErrors,
        double AvgCaptureMs,
        double AvgConvertMs,
        double P50CaptureMs,
        double P95CaptureMs,
        double P99CaptureMs,
        double P50ConvertMs,
        double P95ConvertMs,
        double P99ConvertMs,
        double CaptureFps,
        double ConvertFps);

    private sealed record IddSharedFrameHeader(
        uint Magic,
        int Version,
        int Width,
        int Height,
        int Format,
        int Stride,
        int SlotCount,
        int SlotSize,
        long WriteSeq,
        long TimestampQpc);

    private sealed record IddGpuFrameMetadata(
        uint Magic,
        int Version,
        int Width,
        int Height,
        int Format,
        int SlotCount,
        int LatestSlot,
        int Generation,
        long WriteSeq,
        long TimestampQpc,
        uint AdapterLuidLow,
        int AdapterLuidHigh,
        long FrameDuration100ns,
        int ModeRefreshHz,
        int Flags);

    private sealed record IddGpuFrameSlot(
        long Seq,
        long TimestampQpc,
        int Width,
        int Height,
        int Format,
        int State);

    private sealed record RealtimeEncoderStatsSnapshot(
        long FramesGenerated,
        long FramesEncoded,
        long FramesSent,
        long FramesDropped,
        double AvgEncodeMs,
        double MaxEncodeMs,
        double P50EncodeMs,
        double P95EncodeMs,
        double P99EncodeMs,
        double AvgSendMs,
        double MaxSendMs,
        double P50SendMs,
        double P95SendMs,
        double P99SendMs,
        double OutputKbps,
        long LastKeyFrameSeq,
        double StreamFps,
        double P50LocalLatencyMs,
        double P95LocalLatencyMs,
        double P99LocalLatencyMs,
        double P50FrameIntervalMs,
        double P95FrameIntervalMs,
        double P99FrameIntervalMs,
        long LateFrames,
        long NewFramesSent,
        long RepeatFramesSent,
        long BlackFramesSent,
        long KeepaliveFramesSent);

    private sealed record ProtocolMessage(
        int V,
        string Type,
        long Seq,
        long Ts,
        JsonNode? Payload);

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
