using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
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

namespace SideDock.Host;

internal static class Program
{
    private const int DefaultControlPort = 27183;
    private const int DefaultVideoPort = 27184;
    private const int DefaultVideoWidth = 1280;
    private const int DefaultVideoHeight = 720;
    private const int DefaultVideoFps = 30;
    private const int DefaultVideoBitrate = 4_000_000;
    private const int DefaultVideoGop = 30;
    private const int DefaultMaxVideoQueue = 2;
    private const string DefaultVideoFile = "artifacts/test-videos/sidedock-720p30.h264";
    private const string DefaultResolutionPreset = "720p";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
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
        if (options.VideoSource == VideoSourceKind.Idd || options.InputTarget == InputTargetKind.Idd)
        {
            displayLayoutProvider.LogCurrentLayout();
            if (options.RequestedDisplayMode is not null)
            {
                var modeResult = displayLayoutProvider.TryChangeMode(options.RequestedDisplayMode, out var modeMessage);
                Log("DISPLAY", $"startup display mode {options.RequestedDisplayMode}: {(modeResult ? "success" : "failed")} {modeMessage}");
            }
        }

        var adbPath = ResolveAdbPath();
        Log("ADB", $"使用 adb: {adbPath}");

        foreach (var port in options.ReversePorts)
        {
            await ConfigureAdbReverseAsync(adbPath, port, appCts.Token);
        }

        _ = Task.Run(() => KeepAdbReverseAliveAsync(adbPath, options.ReversePorts, appCts.Token), appCts.Token);

        var controlPublisher = new ControlMessagePublisher();
        var controlServer = new ControlServer(IPAddress.Loopback, options, videoModeState, controlPublisher, displayLayoutProvider);
        var videoServer = new VideoServer(IPAddress.Loopback, options, videoModeState, controlPublisher);

        await Task.WhenAll(
            controlServer.RunAsync(appCts.Token),
            videoServer.RunAsync(appCts.Token));

        return 0;
    }

    private static void PrintHeader(HostOptions options)
    {
        Console.WriteLine("SideDock Windows 服务");
        Console.WriteLine($"控制通道: 127.0.0.1:{options.ControlPort}");
        Console.WriteLine($"视频通道: 127.0.0.1:{options.VideoPort}");
        Console.WriteLine($"视频源: {options.VideoSource}");
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

        Console.WriteLine($"ç”»è´¨æ¡£ä½: {options.ResolutionPreset} @ {options.VideoFps}Hz");
        Console.WriteLine($"ç¼–ç è§„æ ¼: {options.VideoWidth}x{options.VideoHeight} bitrate={options.VideoBitrate} ({(options.AutoVideoBitrate ? "auto" : "manual")})");

        if (options.RequestedDisplayMode is not null)
        {
            Console.WriteLine($"display mode request: {options.RequestedDisplayMode}");
        }

        if (!string.IsNullOrWhiteSpace(options.DumpEncodedPath))
        {
            Console.WriteLine($"编码输出 dump: {options.DumpEncodedPath}");
        }

        Console.WriteLine("退出方式: Ctrl+C");
        Console.WriteLine();
    }

    private static async Task KeepAdbReverseAliveAsync(string adbPath, IReadOnlyList<int> ports, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var reverseList = await RunAdbAsync(adbPath, "reverse --list", cancellationToken);
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
                    await ConfigureAdbReverseAsync(adbPath, port, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Application shutdown.
        }
    }

    private static async Task ConfigureAdbReverseAsync(string adbPath, int port, CancellationToken cancellationToken)
    {
        var arguments = $"reverse tcp:{port} tcp:{port}";

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
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, (await stdoutTask).Trim(), (await stderrTask).Trim());
    }

    private static string ResolveAdbPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("SIDEDOCK_ADB");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
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

            var adbPath = Path.Combine(sdkRoot, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb");
            if (File.Exists(adbPath))
            {
                return adbPath;
            }
        }

        return "adb";
    }

    private static void Log(string scope, string message)
    {
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} [{scope}] {message}");
    }

    private sealed class ControlMessagePublisher
    {
        private readonly object _lock = new();
        private ControlConnection? _activeConnection;

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

            return connection is null
                ? ValueTask.CompletedTask
                : connection.SendAsync(type, payload, cancellationToken);
        }
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

            Log($"CONN {_connectionId}", $"发送 {type} seq={message.Seq}");
        }
    }

    private sealed class ControlServer(
        IPAddress address,
        HostOptions options,
        VideoModeState videoModeState,
        ControlMessagePublisher publisher,
        DisplayLayoutProvider displayLayoutProvider)
    {
        private readonly TcpListener _listener = new(address, options.ControlPort);
        private readonly HostOptions _options = options;
        private readonly VideoModeState _videoModeState = videoModeState;
        private readonly ControlMessagePublisher _publisher = publisher;
        private readonly DisplayLayoutProvider _displayLayoutProvider = displayLayoutProvider;
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
                var session = new ControlSession(connectionId, client, connectionCts, _options, _videoModeState, _publisher, _displayLayoutProvider);
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
        private readonly HostInputController _inputController;
        private readonly PeriodicTimer _heartbeatTimer = new(TimeSpan.FromSeconds(2));
        private readonly PeriodicTimer _inputStatsTimer = new(TimeSpan.FromSeconds(1));
        private readonly PeriodicTimer _displayLayoutTimer = new(TimeSpan.FromSeconds(2));
        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private DisplayMetrics? _lastPublishedMetrics;
        private int _missedPongs;
        private DateTimeOffset _lastPong = DateTimeOffset.UtcNow;

        public ControlSession(
            int connectionId,
            TcpClient client,
            CancellationTokenSource connectionCts,
            HostOptions options,
            VideoModeState videoModeState,
            ControlMessagePublisher publisher,
            DisplayLayoutProvider displayLayoutProvider)
        {
            _connectionId = connectionId;
            _client = client;
            _connectionCts = connectionCts;
            _options = options;
            _videoModeState = videoModeState;
            _publisher = publisher;
            _displayLayoutProvider = displayLayoutProvider;
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
                var readTask = ReadLoopAsync(reader, connection, _connectionCts.Token);
                await Task.WhenAny(heartbeatTask, readTask);
                await _connectionCts.CancelAsync();
                await Task.WhenAll(heartbeatTask, inputStatsTask, displayLayoutTask, readTask);
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
                        ["videoSource"] = _options.VideoSource.ToString().ToLowerInvariant(),
                        ["encoder"] = _options.Encoder.ToString().ToLowerInvariant(),
                        ["inputTarget"] = _options.InputTarget.ToString().ToLowerInvariant()
                    }, cancellationToken);
                    await PublishDisplayMetricsIfChangedAsync(connection, force: true, cancellationToken);
                    break;

                case "ping":
                    await connection.SendAsync("pong", new JsonObject
                    {
                        ["replyTo"] = message.Seq,
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

                case "input_keyboard":
                case "input_pointer_abs":
                case "input_mouse_move":
                case "input_mouse_button":
                case "input_mouse_wheel":
                    await HandleInputMessageAsync(message, connection, cancellationToken);
                    break;

                case "status":
                case "log":
                case "video_stats":
                case "video_error":
                case "input_stats":
                case "input_error":
                    if (message.Payload is not null)
                    {
                        Log(Scope, $"{message.Type} payload={message.Payload}");
                    }

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
            var changedWidth = layout?.Width ?? width;
            var changedHeight = layout?.Height ?? height;
            var changedRefresh = layout?.RefreshRate ?? refreshHz;
            var videoRefresh = NormalizeObservedRefresh(changedRefresh, refreshHz);
            var appliedMode = success
                ? _videoModeState.Set(changedWidth, changedHeight, videoRefresh)
                : _videoModeState.Current;

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
                ["success"] = success,
                ["code"] = success ? "OK" : "DISPLAY_MODE_CHANGE_FAILED",
                ["message"] = resultMessage,
                ["videoWidth"] = appliedMode.Width,
                ["videoHeight"] = appliedMode.Height,
                ["videoFps"] = appliedMode.Fps,
                ["videoBitrate"] = appliedMode.Bitrate,
                ["sourceSeq"] = message.Seq
            }, cancellationToken);

            if (success)
            {
                await connection.SendAsync("video_start", new JsonObject
                {
                    ["videoPort"] = _options.VideoPort,
                    ["width"] = appliedMode.Width,
                    ["height"] = appliedMode.Height,
                    ["fps"] = appliedMode.Fps,
                    ["codec"] = "video/avc",
                    ["format"] = "annexb"
                }, cancellationToken);
            }

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

        private static int NormalizeObservedRefresh(int observedRefreshHz, int requestedRefreshHz)
        {
            return Math.Abs(observedRefreshHz - requestedRefreshHz) <= 1
                ? requestedRefreshHz
                : observedRefreshHz;
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
            if (_options.VideoSource != VideoSourceKind.Idd && _options.InputTarget != InputTargetKind.Idd)
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

            var videoMode = _videoModeState.Current;
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
                ["visible"] = false
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

            var devMode = DisplayNative.DevMode.Create();
            if (!DisplayNative.EnumDisplaySettingsW(layout.DeviceName, EnumCurrentSettings, ref devMode))
            {
                message = $"Unable to read current mode for {layout.DeviceName}: {Marshal.GetLastWin32Error()}.";
                return false;
            }

            devMode.Fields |= DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
            devMode.PelsWidth = (uint)mode.Width;
            devMode.PelsHeight = (uint)mode.Height;
            devMode.DisplayFrequency = (uint)mode.RefreshHz;

            var result = DisplayNative.ChangeDisplaySettingsExW(layout.DeviceName, ref devMode, IntPtr.Zero, 0, IntPtr.Zero);
            _cachedLayout = null;
            _lastQueryTicks = 0;
            var changedLayout = GetLayout(force: true);
            var changed = changedLayout is not null
                && changedLayout.Width == mode.Width
                && changedLayout.Height == mode.Height
                && Math.Abs(changedLayout.RefreshRate - mode.RefreshHz) <= 1;

            if (result != DispChangeSuccessful)
            {
                message = $"ChangeDisplaySettingsEx returned {result}.";
                return false;
            }

            message = changed
                ? "Display mode applied."
                : $"Windows accepted the request but current mode is {changedLayout?.Width ?? 0}x{changedLayout?.Height ?? 0}@{changedLayout?.RefreshRate ?? 0}.";
            return changed;
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
            return candidates
                .Where(candidate =>
                    candidate.Layout.Width == videoMode.Width
                    && candidate.Layout.Height == videoMode.Height
                    && Math.Abs(candidate.Layout.RefreshRate - videoMode.Fps) <= 1
                    && !candidate.Layout.IsPrimary)
                .OrderByDescending(candidate => candidate.Score)
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
                    await using var source = CreateVideoSource(connectionId, videoOptions);
                    var session = new VideoSession(connectionId, client, source, videoOptions, _controlPublisher);
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

        private IEncodedVideoSource CreateVideoSource(int connectionId, HostOptions videoOptions)
        {
            if (videoOptions.VideoSource == VideoSourceKind.File)
            {
                return new FileH264VideoSource(videoOptions.VideoFilePath, videoOptions.VideoFps);
            }

            if (videoOptions.VideoSource == VideoSourceKind.Realtime && videoOptions.Encoder == H264EncoderKind.Ffmpeg)
            {
                return new FfmpegRealtimeVideoSource(connectionId, videoOptions, _controlPublisher);
            }

            return new MediaFoundationBgraVideoSource(
                connectionId,
                videoOptions,
                _controlPublisher,
                CreateBgraFrameSource(connectionId, videoOptions));
        }

        private IBgraFrameSource CreateBgraFrameSource(int connectionId, HostOptions videoOptions)
        {
            return videoOptions.VideoSource switch
            {
                VideoSourceKind.Realtime => new TestPatternBgraFrameSource(videoOptions.VideoWidth, videoOptions.VideoHeight),
                VideoSourceKind.Region => new RegionBgraFrameSource(videoOptions),
                VideoSourceKind.Window => new WindowBgraFrameSource(videoOptions, message => Log($"CAPTURE {connectionId}", message)),
                VideoSourceKind.Idd => new IddBgraFrameSource(videoOptions, message => Log($"CAPTURE {connectionId}", message)),
                _ => throw new InvalidOperationException($"Unsupported BGRA video source: {videoOptions.VideoSource}")
            };
        }
    }

    private sealed class VideoSession(
        int connectionId,
        TcpClient client,
        IEncodedVideoSource videoSource,
        HostOptions options,
        ControlMessagePublisher controlPublisher)
    {
        private static readonly object DumpFileLock = new();
        private static readonly HashSet<string> InitializedDumpPaths = new(StringComparer.OrdinalIgnoreCase);

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            client.NoDelay = true;
            client.SendBufferSize = 1024 * 1024;

            await using var stream = client.GetStream();
            long packetsSent = 0;
            long framesSent = 0;
            long bytesSent = 0;
            var lastLog = Stopwatch.StartNew();
            var stats = new EncoderStats();
            await using var dumpStream = OpenDumpStream(options.DumpEncodedPath);

            await videoSource.StartAsync(cancellationToken);

            await foreach (var packet in videoSource.ReadPacketsAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sendStopwatch = Stopwatch.StartNew();
                await VideoPacketWriter.WriteAsync(stream, packet, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                sendStopwatch.Stop();

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
                    Log($"VIDEO {connectionId}", $"已发送 packets={packetsSent} frames={framesSent} bytes={mb:F1}MB");
                    lastLog.Restart();
                }
            }
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
        private static readonly byte[] Magic = "SDKV"u8.ToArray();

        public static async ValueTask WriteAsync(Stream stream, EncodedVideoPacket packet, CancellationToken cancellationToken)
        {
            var header = new byte[HeaderSize];
            Magic.CopyTo(header, 0);
            header[4] = 1;
            header[5] = packet.IsKeyFrame ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), unchecked((uint)packet.Sequence));
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(12, 8), packet.TimestampMs);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), packet.Payload.Length);

            await stream.WriteAsync(header, cancellationToken);
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
    }

    private interface IBgraFrameSource : IDisposable
    {
        string SourceName { get; }

        string SourceDescription { get; }

        void Start(CancellationToken cancellationToken);

        void Capture(byte[] bgraFrame, int outputWidth, int outputHeight, CancellationToken cancellationToken);
    }

    private sealed class FileH264VideoSource : IEncodedVideoSource
    {
        private readonly string _path;
        private readonly TimeSpan _frameDuration;
        private H264AnnexBStream? _videoStream;
        private long _sequence;
        private long _frameId;

        public FileH264VideoSource(string path, int fps)
        {
            _path = path;
            _frameDuration = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, fps));
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
            var nextFrameAt = DateTimeOffset.UtcNow;

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
                        0);

                    if (packet.ContainsPicture)
                    {
                        nextFrameAt += _frameDuration;
                        var delay = nextFrameAt - DateTimeOffset.UtcNow;
                        if (delay > TimeSpan.Zero)
                        {
                            await Task.Delay(delay, cancellationToken);
                        }
                        else if (delay < TimeSpan.FromSeconds(-1))
                        {
                            nextFrameAt = DateTimeOffset.UtcNow;
                        }
                    }
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
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

    private sealed class FfmpegRealtimeVideoSource : IEncodedVideoSource, IVideoSendStatsSink
    {
        private readonly int _connectionId;
        private readonly HostOptions _options;
        private readonly ControlMessagePublisher _controlPublisher;
        private readonly TestPatternGenerator _patternGenerator;
        private readonly RealtimeEncoderStats _stats;
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

            _sourceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _inputTask = Task.Run(() => WriteInputFramesAsync(_process.StandardInput.BaseStream, _sourceCts.Token), _sourceCts.Token);
            _stderrTask = Task.Run(() => DrainStderrAsync(_process.StandardError, _sourceCts.Token), _sourceCts.Token);
            _statsTask = Task.Run(() => PublishStatsLoopAsync(_sourceCts.Token), _sourceCts.Token);

            Log($"ENCODER {_connectionId}", $"start source=realtime encoder=ffmpeg {_options.VideoWidth}x{_options.VideoHeight}@{_options.VideoFps} bitrate={_options.VideoBitrate}");
            await _controlPublisher.PublishAsync("encoder_start", new JsonObject
            {
                ["source"] = "realtime_test_pattern",
                ["encoder"] = "ffmpeg",
                ["width"] = _options.VideoWidth,
                ["height"] = _options.VideoHeight,
                ["fps"] = _options.VideoFps,
                ["bitrate"] = _options.VideoBitrate,
                ["codec"] = "h264",
                ["format"] = "annexb"
            }, cancellationToken);
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
                _stats.GetFrameEncodeMs(frameId));
        }

        private async Task WriteInputFramesAsync(Stream stdin, CancellationToken cancellationToken)
        {
            var frameDuration = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _options.VideoFps));
            var nextFrameAt = DateTimeOffset.UtcNow;
            var frameBuffer = new byte[_options.VideoWidth * _options.VideoHeight * 4];
            long frameId = 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var generatedAt = DateTimeOffset.UtcNow;
                    _patternGenerator.Render(frameBuffer, frameId, generatedAt);
                    _stats.RecordGenerated(frameId, generatedAt.ToUnixTimeMilliseconds());

                    await stdin.WriteAsync(frameBuffer, cancellationToken);
                    await stdin.FlushAsync(cancellationToken);

                    frameId++;
                    nextFrameAt += frameDuration;
                    var delay = nextFrameAt - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken);
                    }
                    else if (delay < TimeSpan.FromSeconds(-1))
                    {
                        nextFrameAt = DateTimeOffset.UtcNow;
                        _stats.RecordDropped();
                    }
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
                    Log($"ENCODER {_connectionId}", $"stats generated={snapshot.FramesGenerated} encoded={snapshot.FramesEncoded} sent={snapshot.FramesSent} dropped={snapshot.FramesDropped} avgEncode={snapshot.AvgEncodeMs:F1}ms maxEncode={snapshot.MaxEncodeMs:F1}ms avgSend={snapshot.AvgSendMs:F1}ms kbps={snapshot.OutputKbps:F0}");
                    await _controlPublisher.PublishAsync("encoder_stats", new JsonObject
                    {
                        ["framesGenerated"] = snapshot.FramesGenerated,
                        ["framesEncoded"] = snapshot.FramesEncoded,
                        ["framesSent"] = snapshot.FramesSent,
                        ["framesDropped"] = snapshot.FramesDropped,
                        ["avgEncodeMs"] = Math.Round(snapshot.AvgEncodeMs, 2),
                        ["maxEncodeMs"] = Math.Round(snapshot.MaxEncodeMs, 2),
                        ["avgSendMs"] = Math.Round(snapshot.AvgSendMs, 2),
                        ["maxSendMs"] = Math.Round(snapshot.MaxSendMs, 2),
                        ["outputKbps"] = Math.Round(snapshot.OutputKbps, 0),
                        ["lastKeyFrameSeq"] = snapshot.LastKeyFrameSeq
                    }, cancellationToken);
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
                "-pix_fmt yuv420p",
                $"-b:v {_options.VideoBitrate}",
                $"-maxrate {_options.VideoBitrate}",
                $"-bufsize {_options.VideoBitrate}",
                $"-g {_options.VideoGop}",
                "-bf 0",
                $"-x264-params keyint={_options.VideoGop}:min-keyint={_options.VideoGop}:scenecut=0:repeat-headers=1",
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

        private static async Task WhenAllIgnoringCancellation(params Task?[] tasks)
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

    private sealed class MediaFoundationBgraVideoSource : IEncodedVideoSource, IVideoSendStatsSink
    {
        private readonly int _connectionId;
        private readonly HostOptions _options;
        private readonly ControlMessagePublisher _controlPublisher;
        private readonly IBgraFrameSource _frameSource;
        private readonly RealtimeEncoderStats _stats;
        private readonly CaptureStats _captureStats = new();
        private readonly Channel<EncodedVideoPacket> _packets;
        private CancellationTokenSource? _sourceCts;
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
            _packets = Channel.CreateBounded<EncodedVideoPacket>(new BoundedChannelOptions(Math.Max(1, options.MaxVideoQueue))
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

            _encoderTask = Task.Run(() => EncodeLoopAsync(_sourceCts.Token), _sourceCts.Token);
            _statsTask = Task.Run(() => PublishStatsLoopAsync(_sourceCts.Token), _sourceCts.Token);
            _captureStatsTask = Task.Run(() => PublishCaptureStatsLoopAsync(_sourceCts.Token), _sourceCts.Token);

            Log($"ENCODER {_connectionId}", $"start source={_frameSource.SourceName} encoder=mediafoundation {_options.VideoWidth}x{_options.VideoHeight}@{_options.VideoFps} bitrate={_options.VideoBitrate}");
            await _controlPublisher.PublishAsync("encoder_start", new JsonObject
            {
                ["source"] = _frameSource.SourceName,
                ["encoder"] = "mediafoundation",
                ["width"] = _options.VideoWidth,
                ["height"] = _options.VideoHeight,
                ["fps"] = _options.VideoFps,
                ["bitrate"] = _options.VideoBitrate,
                ["codec"] = "h264",
                ["format"] = "annexb"
            }, cancellationToken);
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

            await WhenAllIgnoringCancellation(_encoderTask, _statsTask, _captureStatsTask);
            _packets.Writer.TryComplete();
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

        private async Task EncodeLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Yield();
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("Media Foundation encoder is only available on Windows.");
                }

                var bgraFrame = new byte[_options.VideoWidth * _options.VideoHeight * 4];
                var nv12Frame = new byte[MediaFoundationH264Encoder.GetNv12FrameSize(_options.VideoWidth, _options.VideoHeight)];
                var frameDuration = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _options.VideoFps));
                var nextFrameAt = DateTimeOffset.UtcNow;
                long frameId = 0;

                using var encoder = new MediaFoundationH264Encoder(_options);
                encoder.Start();

                while (!cancellationToken.IsCancellationRequested)
                {
                    var generatedAt = DateTimeOffset.UtcNow;
                    var captureStopwatch = Stopwatch.StartNew();
                    try
                    {
                        _frameSource.Capture(bgraFrame, _options.VideoWidth, _options.VideoHeight, cancellationToken);
                        captureStopwatch.Stop();
                        _captureStats.RecordCaptured(captureStopwatch.Elapsed.TotalMilliseconds);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
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
                        await PublishCaptureErrorAsync("CAPTURE_FAILED", ex.Message, CancellationToken.None);
                        throw;
                    }

                    _stats.RecordGenerated(frameId, generatedAt.ToUnixTimeMilliseconds());
                    var convertStopwatch = Stopwatch.StartNew();
                    MediaFoundationH264Encoder.ConvertBgraToNv12(bgraFrame, nv12Frame, _options.VideoWidth, _options.VideoHeight);
                    convertStopwatch.Stop();
                    _captureStats.RecordConverted(convertStopwatch.Elapsed.TotalMilliseconds);

                    foreach (var payload in encoder.EncodeFrame(nv12Frame, frameId))
                    {
                        var packet = CreatePacket(payload);
                        if (!_packets.Writer.TryWrite(packet))
                        {
                            _stats.RecordDropped();
                        }
                    }

                    frameId++;
                    nextFrameAt += frameDuration;
                    var delay = nextFrameAt - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken);
                    }
                    else if (delay < TimeSpan.FromSeconds(-1))
                    {
                        nextFrameAt = DateTimeOffset.UtcNow;
                        _stats.RecordDropped();
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
                _packets.Writer.TryComplete(ex);
                return;
            }

            _packets.Writer.TryComplete();
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
                _stats.GetFrameEncodeMs(frameId));
        }

        private async Task PublishStatsLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    var snapshot = _stats.SnapshotAndResetWindow();
                    Log($"ENCODER {_connectionId}", $"stats generated={snapshot.FramesGenerated} encoded={snapshot.FramesEncoded} sent={snapshot.FramesSent} dropped={snapshot.FramesDropped} avgEncode={snapshot.AvgEncodeMs:F1}ms maxEncode={snapshot.MaxEncodeMs:F1}ms avgSend={snapshot.AvgSendMs:F1}ms kbps={snapshot.OutputKbps:F0}");
                    await _controlPublisher.PublishAsync("encoder_stats", new JsonObject
                    {
                        ["framesGenerated"] = snapshot.FramesGenerated,
                        ["framesEncoded"] = snapshot.FramesEncoded,
                        ["framesSent"] = snapshot.FramesSent,
                        ["framesDropped"] = snapshot.FramesDropped,
                        ["avgEncodeMs"] = Math.Round(snapshot.AvgEncodeMs, 2),
                        ["maxEncodeMs"] = Math.Round(snapshot.MaxEncodeMs, 2),
                        ["avgSendMs"] = Math.Round(snapshot.AvgSendMs, 2),
                        ["maxSendMs"] = Math.Round(snapshot.MaxSendMs, 2),
                        ["outputKbps"] = Math.Round(snapshot.OutputKbps, 0),
                        ["lastKeyFrameSeq"] = snapshot.LastKeyFrameSeq
                    }, cancellationToken);
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
                    Log($"CAPTURE {_connectionId}", $"stats captured={snapshot.FramesCaptured} converted={snapshot.FramesConverted} errors={snapshot.CaptureErrors} avgCapture={snapshot.AvgCaptureMs:F1}ms avgConvert={snapshot.AvgConvertMs:F1}ms");
                    var payload = new JsonObject
                    {
                        ["source"] = _frameSource.SourceName,
                        ["framesCaptured"] = snapshot.FramesCaptured,
                        ["framesConverted"] = snapshot.FramesConverted,
                        ["captureErrors"] = snapshot.CaptureErrors,
                        ["avgCaptureMs"] = Math.Round(snapshot.AvgCaptureMs, 2),
                        ["avgConvertMs"] = Math.Round(snapshot.AvgConvertMs, 2)
                    };

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

        private static async Task WhenAllIgnoringCancellation(params Task?[] tasks)
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

    [SupportedOSPlatform("windows")]
    private sealed class MediaFoundationH264Encoder : IDisposable
    {
        private readonly HostOptions _options;
        private IMFTransform? _transform;
        private int _outputBufferSize;
        private bool _outputProvidesSamples;
        private bool _started;
        private bool _mfStarted;
        private bool _comInitialized;
        private int _nalLengthSize = 4;
        private byte[]? _parameterSets;

        public MediaFoundationH264Encoder(HostOptions options)
        {
            _options = options;
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

            var clsid = Native.CLSID_CMSH264EncoderMFT;
            var iid = Native.IID_IMFTransform;
            ThrowIfFailed(
                Native.CoCreateInstance(ref clsid, IntPtr.Zero, Native.CLSCTX_INPROC_SERVER, ref iid, out _transform),
                "Unable to create the Microsoft H.264 Media Foundation encoder MFT.");

            ConfigureCodecApi();
            ConfigureMediaTypes();

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

        public void Dispose()
        {
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
        }

        private MediaTypeHandle CreateOutputMediaType(bool includeProfile)
        {
            ThrowIfFailed(Native.MFCreateMediaType(out var mediaType), "Unable to create Media Foundation output media type.");
            var attributes = (IMFAttributes)mediaType;

            SetGuid(attributes, Native.MF_MT_MAJOR_TYPE, Native.MFMediaType_Video);
            SetGuid(attributes, Native.MF_MT_SUBTYPE, Native.MFVideoFormat_H264);
            SetUInt32(attributes, Native.MF_MT_AVG_BITRATE, _options.VideoBitrate);
            SetUInt32(attributes, Native.MF_MT_INTERLACE_MODE, Native.MFVideoInterlace_Progressive);
            SetUInt32(attributes, Native.MF_MT_MAX_KEYFRAME_SPACING, _options.VideoGop);
            SetUInt32(attributes, Native.MF_NALU_LENGTH_SET, 4);

            if (includeProfile)
            {
                SetUInt32(attributes, Native.MF_MT_MPEG2_PROFILE, Native.H264ProfileBaseline);
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

            TrySetCodecValue(codecApi, Native.CODECAPI_AVLowLatencyMode, Variant.FromBool(true));
            TrySetCodecValue(codecApi, Native.CODECAPI_AVEncCommonRateControlMode, Variant.FromUInt32(Native.eAVEncCommonRateControlMode_CBR));
            TrySetCodecValue(codecApi, Native.CODECAPI_AVEncCommonMeanBitRate, Variant.FromUInt32((uint)_options.VideoBitrate));
            TrySetCodecValue(codecApi, Native.CODECAPI_AVEncMPVGOPSize, Variant.FromUInt32((uint)_options.VideoGop));
            TrySetCodecValue(codecApi, Native.CODECAPI_AVEncMPVDefaultBPictureCount, Variant.FromUInt32(0));
            TrySetCodecValue(codecApi, Native.CODECAPI_AVEncMPVProfile, Variant.FromUInt32(Native.H264ProfileBaseline));
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

            var frameDuration = 10_000_000L / Math.Max(1, _options.VideoFps);
            ThrowIfFailed(sample.SetSampleTime(frameId * frameDuration), "Unable to set input sample time.");
            ThrowIfFailed(sample.SetSampleDuration(frameDuration), "Unable to set input sample duration.");

            if (frameId == 0 || (_options.VideoGop > 0 && frameId % _options.VideoGop == 0))
            {
                SetUInt32((IMFAttributes)sample, Native.MFSampleExtension_VideoEncodePictureType, Native.eAVEncH264PictureType_IDR);
                ForceNextKeyFrame();
            }

            return new InputSampleHandle(sample, buffer);
        }

        private void ForceNextKeyFrame()
        {
            if (_transform is not ICodecAPI codecApi)
            {
                return;
            }

            TrySetCodecValue(codecApi, Native.CODECAPI_AVEncVideoForceKeyFrame, Variant.FromBool(true));
        }

        private List<byte[]> DrainOutput(IMFTransform transform)
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
            public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
            public const int MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
            public const int MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
            public const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
            public const int MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE = 0x00000100;
            public const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;
            public const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36B4);
            public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
            public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
            public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
            public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
            public static readonly byte[] AnnexBStartCode = [0, 0, 0, 1];

            public static readonly Guid CLSID_CMSH264EncoderMFT = new("6CA50344-051A-4DED-9779-A43305165E35");
            public static readonly Guid IID_IMFTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");
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
            public static extern int MFCreateMediaType(out IMFMediaType mediaType);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFCreateSample(out IMFSample sample);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);
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

            var timePhase = (int)((generatedAt.ToUnixTimeMilliseconds() / 20) % 256);
            for (var y = 0; y < _height; y++)
            {
                for (var x = 0; x < _width; x++)
                {
                    var offset = (y * _width + x) * 4;
                    var bar = Bars[(x * Bars.Length) / Math.Max(1, _width)];
                    var stripe = (byte)((y / 24) % 2 == 0 ? 28 : 0);
                    bgra[offset] = (byte)Math.Min(255, (bar.B / 3) + stripe + timePhase / 8);
                    bgra[offset + 1] = (byte)Math.Min(255, (bar.G / 3) + stripe + ((timePhase + 85) % 256) / 8);
                    bgra[offset + 2] = (byte)Math.Min(255, (bar.R / 3) + stripe + ((timePhase + 170) % 256) / 8);
                    bgra[offset + 3] = 255;
                }
            }

            DrawColorBars(bgra);
            DrawMovingBlocks(bgra, frameId);
            DrawFlashMarker(bgra, generatedAt);
            DrawFrameCode(bgra, frameId);
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
            string lpszDeviceName,
            ref DevMode lpDevMode,
            IntPtr hwnd,
            uint dwflags,
            IntPtr lParam);

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

    private sealed class CaptureStats
    {
        private readonly object _lock = new();
        private long _framesCaptured;
        private long _framesConverted;
        private long _captureErrors;
        private long _windowFramesCaptured;
        private long _windowFramesConverted;
        private double _captureMsTotal;
        private double _convertMsTotal;
        private double _windowCaptureMsTotal;
        private double _windowConvertMsTotal;

        public void RecordCaptured(double captureMs)
        {
            lock (_lock)
            {
                _framesCaptured++;
                _windowFramesCaptured++;
                _captureMsTotal += captureMs;
                _windowCaptureMsTotal += captureMs;
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
                var snapshot = new CaptureStatsSnapshot(
                    _framesCaptured,
                    _framesConverted,
                    _captureErrors,
                    _windowFramesCaptured == 0 ? 0 : _windowCaptureMsTotal / _windowFramesCaptured,
                    _windowFramesConverted == 0 ? 0 : _windowConvertMsTotal / _windowFramesConverted);

                _windowFramesCaptured = 0;
                _windowFramesConverted = 0;
                _windowCaptureMsTotal = 0;
                _windowConvertMsTotal = 0;
                return snapshot;
            }
        }
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
        private long _framesGenerated;
        private long _framesEncoded;
        private long _framesSent;
        private long _framesDropped;
        private long _encodedBytes;
        private long _windowEncodedBytes;
        private long _lastEncodedFrameId = -1;
        private long _lastKeyFrameSeq = -1;
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
                _frameTimings[frameId] = new FrameTiming(timestampMs, Stopwatch.GetTimestamp(), 0);

                foreach (var oldFrameId in _frameTimings.Keys.Where(id => id < frameId - MaxTrackedFrames).ToArray())
                {
                    _frameTimings.Remove(oldFrameId);
                }
            }
        }

        public void RecordDropped()
        {
            lock (_lock)
            {
                _framesDropped++;
            }
        }

        public long RecordEncoded(int payloadLength)
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

                var observedMs = (Stopwatch.GetTimestamp() - timing.GeneratedStopwatchTicks) * 1000.0 / Stopwatch.Frequency;
                var encodeMs = Math.Max(0, observedMs - _frameDurationMs);
                _frameTimings[_lastEncodedFrameId] = timing with { EncodeMs = encodeMs };
                _encodeMsTotal += encodeMs;
                _encodeMsMax = Math.Max(_encodeMsMax, encodeMs);
                _windowEncodeMsTotal += encodeMs;
                _windowEncodeMsMax = Math.Max(_windowEncodeMsMax, encodeMs);
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
                return CreateSnapshot(
                    framesEncodedForAverage: _framesEncoded,
                    framesSentForAverage: _framesSent,
                    encodeMsTotal: _encodeMsTotal,
                    encodeMsMax: _encodeMsMax,
                    sendMsTotal: _sendMsTotal,
                    sendMsMax: _sendMsMax,
                    encodedBytes: _encodedBytes,
                    seconds: Math.Max(1, _framesGenerated / 30.0));
            }
        }

        public RealtimeEncoderStatsSnapshot SnapshotAndResetWindow()
        {
            lock (_lock)
            {
                var snapshot = CreateSnapshot(
                    framesEncodedForAverage: _windowFramesEncoded,
                    framesSentForAverage: _windowFramesSent,
                    encodeMsTotal: _windowEncodeMsTotal,
                    encodeMsMax: _windowEncodeMsMax,
                    sendMsTotal: _windowSendMsTotal,
                    sendMsMax: _windowSendMsMax,
                    encodedBytes: _windowEncodedBytes,
                    seconds: 1);

                _windowFramesEncoded = 0;
                _windowFramesSent = 0;
                _windowEncodedBytes = 0;
                _windowEncodeMsTotal = 0;
                _windowEncodeMsMax = 0;
                _windowSendMsTotal = 0;
                _windowSendMsMax = 0;
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
            double seconds)
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
                avgSendMs,
                sendMsMax,
                outputKbps,
                _lastKeyFrameSeq);
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

    private sealed record HostOptions(
        int ControlPort,
        int VideoPort,
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
        int VideoGop,
        string? DumpEncodedPath,
        int MaxVideoQueue,
        bool EnableInputInjection,
        InputTargetKind InputTarget,
        bool ListWindows,
        CaptureRectangle? CaptureRegion,
        string? WindowTitle,
        string? ProcessName,
        DisplayModeRequest? RequestedDisplayMode)
    {
        public IReadOnlyList<int> ReversePorts { get; } = new[] { ControlPort, VideoPort };

        public static HostOptions Parse(string[] args)
        {
            var controlPort = DefaultControlPort;
            var videoPort = DefaultVideoPort;
            var videoFile = Path.GetFullPath(DefaultVideoFile);
            var videoWidth = DefaultVideoWidth;
            var videoHeight = DefaultVideoHeight;
            var videoFps = DefaultVideoFps;
            var resolutionPreset = DefaultResolutionPreset;
            var videoSource = VideoSourceKind.File;
            var encoder = H264EncoderKind.MediaFoundation;
            string? ffmpegPath = null;
            var videoBitrate = DefaultVideoBitrate;
            var autoVideoBitrate = true;
            var videoGop = DefaultVideoGop;
            string? dumpEncodedPath = null;
            var maxVideoQueue = DefaultMaxVideoQueue;
            var enableInputInjection = false;
            InputTargetKind? inputTarget = null;
            var listWindows = false;
            CaptureRectangle? captureRegion = null;
            string? windowTitle = null;
            string? processName = null;
            DisplayModeRequest? requestedDisplayMode = null;

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
                        }

                        break;

                    case "--dump-encoded":
                        dumpEncodedPath = Path.GetFullPath(args[index + 1]);
                        break;

                    case "--max-video-queue":
                        if (int.TryParse(args[index + 1], out var parsedMaxVideoQueue) && parsedMaxVideoQueue > 0)
                        {
                            maxVideoQueue = parsedMaxVideoQueue;
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

            if (videoSource == VideoSourceKind.Idd)
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

            inputTarget ??= videoSource == VideoSourceKind.Idd ? InputTargetKind.Idd : InputTargetKind.System;

            return new HostOptions(
                controlPort,
                videoPort,
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
                videoGop,
                dumpEncodedPath,
                maxVideoQueue,
                enableInputInjection,
                inputTarget.Value,
                listWindows,
                captureRegion,
                windowTitle,
                processName,
                requestedDisplayMode);
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
            var pixelsPerSecond = width * (double)height * refreshRate;
            var bitrate = pixelsPerSecond * 0.145;
            return (int)Math.Clamp(Math.Round(bitrate / 1_000_000.0) * 1_000_000, 4_000_000, 64_000_000);
        }

        private static VideoSourceKind ParseVideoSource(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "file" => VideoSourceKind.File,
                "realtime" => VideoSourceKind.Realtime,
                "region" => VideoSourceKind.Region,
                "window" => VideoSourceKind.Window,
                "idd" => VideoSourceKind.Idd,
                _ => throw new ArgumentException($"Unsupported --video-source value: {value}")
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
            var next = new VideoMode(
                width,
                height,
                normalizedFps,
                bitrate,
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
            var pixelsPerSecond = width * (double)height * refreshRate;
            var bitrate = pixelsPerSecond * 0.145;
            return (int)Math.Clamp(Math.Round(bitrate / 1_000_000.0) * 1_000_000, 4_000_000, 64_000_000);
        }
    }

    private readonly record struct VideoMode(
        int Width,
        int Height,
        int Fps,
        int Bitrate,
        string ResolutionPreset)
    {
        public static VideoMode FromOptions(HostOptions options)
        {
            return new VideoMode(
                options.VideoWidth,
                options.VideoHeight,
                options.VideoFps,
                options.VideoBitrate,
                options.ResolutionPreset);
        }
    }

    private enum VideoSourceKind
    {
        File,
        Realtime,
        Region,
        Window,
        Idd
    }

    private enum InputTargetKind
    {
        System,
        Idd
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
        double EncodeMs);

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
        double AvgConvertMs);

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

    private sealed record RealtimeEncoderStatsSnapshot(
        long FramesGenerated,
        long FramesEncoded,
        long FramesSent,
        long FramesDropped,
        double AvgEncodeMs,
        double MaxEncodeMs,
        double AvgSendMs,
        double MaxSendMs,
        double OutputKbps,
        long LastKeyFrameSeq);

    private sealed record ProtocolMessage(
        int V,
        string Type,
        long Seq,
        long Ts,
        JsonNode? Payload);

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
