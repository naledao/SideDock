using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SideDock.Host;

internal static partial class Program
{
    private static class AudioDefaults
    {
        public const int SampleRate = 48000;
        public const int MicChannels = 1;
        public const int SpeakerChannels = 2;
        public const int BitsPerSample = 16;
        public const int MicFrameBytes = MicChannels * (BitsPerSample / 8);
        public const int SpeakerFrameBytes = SpeakerChannels * (BitsPerSample / 8);
    }

    private sealed class AudioServer(IPAddress address, HostOptions options, ControlMessagePublisher controlPublisher)
    {
        private const int HeaderSize = 36;
        private const int MaxMicPayloadBytes = AudioDefaults.SampleRate * AudioDefaults.MicFrameBytes;
        private const int SpeakerChunkBytes = AudioDefaults.SampleRate / 50 * AudioDefaults.SpeakerFrameBytes;
        private static readonly byte[] MicMagic = "SDAM"u8.ToArray();
        private static readonly byte[] SpeakerMagic = "SDAS"u8.ToArray();

        private readonly TcpListener _listener = new(address, options.AudioPort);
        private readonly object _connectionLock = new();
        private readonly IAudioBridgeBackend _audioBackend = CreateAudioBackend(options);
        private readonly AudioRuntimeTelemetryState _telemetryState = new(options.AudioPort);
        private CancellationTokenSource? _activeConnectionCts;
        private Task? _activeConnectionTask;
        private int _connectionSerial;
        private long _reconnectCount;
        private long _disconnectCount;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            InitializeTelemetryState();
            using var telemetryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var telemetryTask = Task.Run(() => PublishTelemetryLoopAsync(telemetryCts.Token), CancellationToken.None);

            try
            {
                if (!options.AudioDeviceEnabled || (!options.MicrophoneEnabled && !options.SpeakerEnabled))
                {
                    Log("AUDIO", "mic-state=disabled speaker-state=disabled reason=disabled_by_options");
                    UpdateDirectionTelemetry(
                        AudioRuntimeDirection.Microphone,
                        state: "disabled",
                        message: "SideDock 麦克风已关闭。",
                        endpointReady: false);
                    UpdateDirectionTelemetry(
                        AudioRuntimeDirection.Speaker,
                        state: "disabled",
                        message: "SideDock 音响已关闭。",
                        endpointReady: false);
                    await PublishMicStatusAsync("disabled", "SideDock 麦克风已关闭。", cancellationToken);
                    await PublishSpeakerStatusAsync("disabled", "SideDock 音响已关闭。", cancellationToken);
                    await PublishAudioRuntimeTelemetryAsync(cancellationToken);
                    await WaitUntilCanceledAsync(cancellationToken);
                    return;
                }

                _listener.Start();
                Log("AUDIO", $"listening address={address} port={options.AudioPort}");
                await PublishInitialStatusAsync(cancellationToken);

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
                        var reconnectCount = Interlocked.Increment(ref _reconnectCount);
                        UpdateReconnectCount(reconnectCount);
                        Log("AUDIO", "检测到新的音频连接，关闭旧连接。");
                        await CancelPreviousConnectionAsync(previousConnectionCts);
                        await WaitForPreviousConnectionAsync(previousConnectionTask, cancellationToken);
                    }

                    var connectionTask = Task.Run(
                        () => HandleClientAsync(connectionId, client, connectionCts, cancellationToken),
                        cancellationToken);
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
                Log("AUDIO", "正在关闭。");
            }
            catch (SocketException ex)
            {
                Log("AUDIO", $"mic-state=unavailable speaker-state=unavailable reason=listen_failed message={ex.Message}");
                UpdateDirectionTelemetry(
                    AudioRuntimeDirection.Microphone,
                    state: "unavailable",
                    message: $"SideDock 麦克风监听失败：{ex.Message}",
                    endpointReady: false,
                    lastError: ex.Message);
                UpdateDirectionTelemetry(
                    AudioRuntimeDirection.Speaker,
                    state: "unavailable",
                    message: $"SideDock 音响监听失败：{ex.Message}",
                    endpointReady: false,
                    lastError: ex.Message);
                await PublishMicStatusAsync("unavailable", $"SideDock 麦克风监听失败：{ex.Message}", CancellationToken.None);
                await PublishSpeakerStatusAsync("unavailable", $"SideDock 音响监听失败：{ex.Message}", CancellationToken.None);
                await PublishAudioRuntimeTelemetryAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log("AUDIO", $"mic-state=unavailable speaker-state=unavailable reason=run_failed exception={ex.GetType().Name} message={LogValue(ex.Message)}");
                UpdateDirectionTelemetry(
                    AudioRuntimeDirection.Microphone,
                    state: "unavailable",
                    message: $"SideDock 麦克风服务失败：{ex.Message}",
                    endpointReady: false,
                    lastError: ex.Message);
                UpdateDirectionTelemetry(
                    AudioRuntimeDirection.Speaker,
                    state: "unavailable",
                    message: $"SideDock 音响服务失败：{ex.Message}",
                    endpointReady: false,
                    lastError: ex.Message);
                await PublishMicStatusAsync("unavailable", $"SideDock 麦克风服务失败：{ex.Message}", CancellationToken.None);
                await PublishSpeakerStatusAsync("unavailable", $"SideDock 音响服务失败：{ex.Message}", CancellationToken.None);
                await PublishAudioRuntimeTelemetryAsync(CancellationToken.None);
            }
            finally
            {
                await telemetryCts.CancelAsync();
                await WaitForTelemetryLoopAsync(telemetryTask);
                _listener.Stop();
                _audioBackend.Dispose();
            }
        }

        private async Task PublishInitialStatusAsync(CancellationToken cancellationToken)
        {
            if (options.MicrophoneEnabled)
            {
                var micReady = _audioBackend.EnsureMicrophoneReady(out var micMessage);
                Log(
                    "AUDIO",
                    $"mic-state={(micReady ? "available" : "unavailable")} backend={_audioBackend.Name} port={options.AudioPort} format=pcm_s16le/{AudioDefaults.SampleRate}/mono system-endpoint={(micReady ? "ready" : "missing")}"
                    + $" systemEndpointMessage={LogValue(micMessage)}"
                    + (micReady ? string.Empty : $" message={micMessage}"));
                UpdateDirectionTelemetry(
                    AudioRuntimeDirection.Microphone,
                    state: micReady ? "available" : "unavailable",
                    message: micReady ? "等待 Android 麦克风采集。" : micMessage,
                    endpointReady: micReady,
                    lastError: micReady ? null : micMessage);
                await PublishMicStatusAsync(
                    micReady ? "available" : "unavailable",
                    micReady ? "SideDock 麦克风可在 Windows 或应用中选择。" : micMessage,
                    cancellationToken);
            }
            else
            {
                Log("AUDIO", "mic-state=disabled reason=disabled_by_options");
                UpdateDirectionTelemetry(
                    AudioRuntimeDirection.Microphone,
                    state: "disabled",
                    message: "SideDock 麦克风已关闭。",
                    endpointReady: false);
                await PublishMicStatusAsync("disabled", "SideDock 麦克风已关闭。", cancellationToken);
            }

            if (options.SpeakerEnabled)
            {
                var speakerReady = _audioBackend.EnsureSpeakerReady(out var speakerMessage);
                Log(
                    "AUDIO",
                    $"speaker-state={(speakerReady ? "available" : "unavailable")} backend={_audioBackend.Name} port={options.AudioPort} format=pcm_s16le/{AudioDefaults.SampleRate}/stereo system-endpoint={(speakerReady ? "ready" : "missing")}"
                    + $" systemEndpointMessage={LogValue(speakerMessage)}"
                    + (speakerReady ? string.Empty : $" message={speakerMessage}"));
                UpdateDirectionTelemetry(
                    AudioRuntimeDirection.Speaker,
                    state: speakerReady ? "available" : "unavailable",
                    message: speakerReady ? "等待所选 Windows 输出设备产生声音。" : speakerMessage,
                    endpointReady: speakerReady,
                    lastError: speakerReady ? null : speakerMessage);
                await PublishSpeakerStatusAsync(
                    speakerReady ? "available" : "unavailable",
                    speakerReady ? "电脑声音 loopback 捕获已准备，等待 Android 播放。" : speakerMessage,
                    cancellationToken);
            }
            else
            {
                Log("AUDIO", "speaker-state=disabled reason=disabled_by_options");
                UpdateDirectionTelemetry(
                    AudioRuntimeDirection.Speaker,
                    state: "disabled",
                    message: "SideDock 音响已关闭。",
                    endpointReady: false);
                await PublishSpeakerStatusAsync("disabled", "SideDock 音响已关闭。", cancellationToken);
            }

            await PublishAudioRuntimeTelemetryAsync(cancellationToken);
        }

        private async Task HandleClientAsync(
            int connectionId,
            TcpClient client,
            CancellationTokenSource connectionCts,
            CancellationToken appToken)
        {
            var directionTasks = new List<Task<AudioDirectionCompletion>>(2);
            using (client)
            using (connectionCts)
            {
                var remote = SafeEndpoint(client, remote: true);
                var local = SafeEndpoint(client, remote: false);
                Log(
                    $"AUDIO {connectionId}",
                    "音频通道已连接 "
                    + $"remote={remote} local={local} "
                    + $"microphone={(options.MicrophoneEnabled ? "enabled" : "disabled")} "
                    + $"speaker={(options.SpeakerEnabled ? "enabled" : "disabled")} "
                    + $"socket={DescribeSocket(client)}");

                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    if (options.MicrophoneEnabled)
                    {
                        directionTasks.Add(MonitorAudioDirectionAsync(
                            "ReceiveMicrophoneAsync",
                            token => ReceiveMicrophoneAsync(connectionId, stream, token),
                            connectionCts.Token));
                    }

                    if (options.SpeakerEnabled)
                    {
                        directionTasks.Add(MonitorAudioDirectionAsync(
                            "SendSpeakerAsync",
                            token => SendSpeakerAsync(connectionId, stream, token),
                            connectionCts.Token));
                    }

                    if (directionTasks.Count == 0)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, connectionCts.Token);
                    }
                    else
                    {
                        var firstTask = await Task.WhenAny(directionTasks);
                        var firstCompletion = await firstTask;
                        Log(
                            $"AUDIO {connectionId}",
                            "audio-task-ended first=true "
                            + FormatAudioDirectionCompletion(firstCompletion));

                        if (firstCompletion.Kind == AudioDirectionEndKind.EndOfStream)
                        {
                            Log(
                                $"AUDIO {connectionId}",
                                $"Android closed audio socket source={firstCompletion.TaskName} remote={remote}");
                        }
                        else if (firstCompletion.Kind is not AudioDirectionEndKind.OperationCanceled)
                        {
                            var message = $"音频连接已关闭：{firstCompletion.TaskName} {firstCompletion.Kind} {firstCompletion.Message}";
                            UpdateDirectionTelemetry(
                                AudioRuntimeDirection.Microphone,
                                state: "unavailable",
                                message: message,
                                endpointReady: _audioBackend.IsMicrophoneReady,
                                lastError: firstCompletion.Message);
                            UpdateDirectionTelemetry(
                                AudioRuntimeDirection.Speaker,
                                state: "unavailable",
                                message: message,
                                endpointReady: _audioBackend.IsSpeakerReady,
                                lastError: firstCompletion.Message);
                            await PublishMicStatusAsync("unavailable", message, CancellationToken.None);
                            await PublishSpeakerStatusAsync("unavailable", message, CancellationToken.None);
                            await PublishAudioRuntimeTelemetryAsync(CancellationToken.None);
                        }
                    }
                }
                catch (OperationCanceledException) when (appToken.IsCancellationRequested || connectionCts.IsCancellationRequested)
                {
                    Log(
                        $"AUDIO {connectionId}",
                        $"audio-task-ended first=true task=HandleClientAsync endType=OperationCanceled socket={DescribeSocket(client)}");
                }
                catch (EndOfStreamException ex)
                {
                    Log(
                        $"AUDIO {connectionId}",
                        $"Android closed audio socket task=HandleClientAsync endType=EOF message={ex.Message}");
                }
                catch (IOException ex)
                {
                    Log(
                        $"AUDIO {connectionId}",
                        $"audio-state=unavailable task=HandleClientAsync endType=IOException exception={ex.GetType().Name} message={ex.Message}");
                }
                catch (InvalidDataException ex)
                {
                    Log(
                        $"AUDIO {connectionId}",
                        $"audio-state=unavailable task=HandleClientAsync endType=InvalidDataException exception={ex.GetType().Name} message={ex.Message}");
                }
                catch (Exception ex)
                {
                    Log(
                        $"AUDIO {connectionId}",
                        $"audio-state=unavailable task=HandleClientAsync endType=Exception exception={ex.GetType().Name} message={ex.Message}");
                    UpdateDirectionTelemetry(
                        AudioRuntimeDirection.Microphone,
                        state: "unavailable",
                        message: $"SideDock 麦克风暂不可用：{ex.Message}",
                        endpointReady: _audioBackend.IsMicrophoneReady,
                        lastError: ex.Message);
                    UpdateDirectionTelemetry(
                        AudioRuntimeDirection.Speaker,
                        state: "unavailable",
                        message: $"SideDock 音响暂不可用：{ex.Message}",
                        endpointReady: _audioBackend.IsSpeakerReady,
                        lastError: ex.Message);
                    await PublishMicStatusAsync("unavailable", $"SideDock 麦克风暂不可用：{ex.Message}", CancellationToken.None);
                    await PublishSpeakerStatusAsync("unavailable", $"SideDock 音响暂不可用：{ex.Message}", CancellationToken.None);
                    await PublishAudioRuntimeTelemetryAsync(CancellationToken.None);
                }
                finally
                {
                    var disconnectCount = Interlocked.Increment(ref _disconnectCount);
                    UpdateDisconnectCount(disconnectCount);
                    Log($"AUDIO {connectionId}", $"准备关闭音频 socket beforeCancel={DescribeSocket(client)}");
                    await connectionCts.CancelAsync();
                    Log($"AUDIO {connectionId}", $"关闭音频 socket 前状态 {DescribeSocket(client)}");
                    CloseAudioSocket(client);
                    Log($"AUDIO {connectionId}", $"关闭音频 socket 后状态 {DescribeSocket(client)}");
                    await DrainAudioDirectionTasksAsync(connectionId, directionTasks);

                    lock (_connectionLock)
                    {
                        if (ReferenceEquals(_activeConnectionCts, connectionCts))
                        {
                            _activeConnectionCts = null;
                            _activeConnectionTask = null;
                        }
                    }

                    Log($"AUDIO {connectionId}", $"音频通道已断开 remote={remote}");
                    await PublishInitialStatusAsync(CancellationToken.None);
                }
            }
        }

        private async Task ReceiveMicrophoneAsync(int connectionId, Stream stream, CancellationToken cancellationToken)
        {
            var endpointReady = _audioBackend.EnsureMicrophoneReady(out var endpointMessage);
            await PublishMicStatusAsync(
                endpointReady ? "available" : "unavailable",
                endpointReady ? "等待 Android 麦克风采集。" : endpointMessage,
                cancellationToken);
            UpdateDirectionTelemetry(
                AudioRuntimeDirection.Microphone,
                state: endpointReady ? "available" : "unavailable",
                message: endpointReady ? "等待 Android 麦克风采集。" : endpointMessage,
                endpointReady: endpointReady,
                lastError: endpointReady ? null : endpointMessage);

            var header = ArrayPool<byte>.Shared.Rent(HeaderSize);
            var payload = ArrayPool<byte>.Shared.Rent(MaxMicPayloadBytes);
            long packetCount = 0;
            long byteCount = 0;
            var lastStatsAt = DateTimeOffset.UtcNow;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header.AsMemory(0, HeaderSize), cancellationToken);
                    ValidateHeader(header, MicMagic, "microphone");
                    var sequence = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8, 8));
                    var timestampMs = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16, 8));
                    var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24, 4));
                    var channels = BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(28, 2));
                    var bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(30, 2));
                    var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(32, 4));

                    if (payloadLength <= 0 || payloadLength > MaxMicPayloadBytes)
                    {
                        throw new InvalidDataException($"Invalid microphone payload length: {payloadLength}.");
                    }

                    if (sampleRate != AudioDefaults.SampleRate
                        || channels != AudioDefaults.MicChannels
                        || bitsPerSample != AudioDefaults.BitsPerSample)
                    {
                        throw new InvalidDataException($"Unsupported microphone format: {sampleRate}/{channels}/{bitsPerSample}.");
                    }

                    await stream.ReadExactlyAsync(payload.AsMemory(0, payloadLength), cancellationToken);
                    endpointReady = _audioBackend.WriteMicrophone(payload, payloadLength, timestampMs, out endpointMessage);
                    packetCount += 1;
                    byteCount += payloadLength;

                    var now = DateTimeOffset.UtcNow;
                    var sourceAgeMs = Math.Max(0, now.ToUnixTimeMilliseconds() - timestampMs);
                    UpdateDirectionTelemetry(
                        AudioRuntimeDirection.Microphone,
                        state: endpointReady ? "capturing" : "unavailable",
                        message: endpointReady ? "Android 麦克风正在采集并写入 Windows。" : endpointMessage,
                        packets: packetCount,
                        bytes: byteCount,
                        levelPercent: CalculatePcm16LevelPercent(payload, payloadLength),
                        lastPacketUnixMs: now.ToUnixTimeMilliseconds(),
                        sourceAgeMs: sourceAgeMs,
                        approximateLatencyMs: sourceAgeMs,
                        endpointReady: endpointReady,
                        lastError: endpointReady ? null : endpointMessage);
                    if (packetCount == 1 || now - lastStatsAt >= TimeSpan.FromSeconds(1))
                    {
                        Log(
                            "AUDIO",
                            $"mic-state={(endpointReady ? "capturing" : "unavailable")} packets={packetCount} bytes={byteCount} lastSeq={sequence} sourceAgeMs={sourceAgeMs} system-endpoint={(endpointReady ? "ready" : "missing")}"
                            + (endpointReady ? string.Empty : $" message={endpointMessage}"));
                        await PublishMicStatusAsync(
                            endpointReady ? "capturing" : "unavailable",
                            endpointReady ? "Android 麦克风正在采集中。" : endpointMessage,
                            cancellationToken,
                            packetCount,
                            byteCount);
                        lastStatsAt = now;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (EndOfStreamException ex)
            {
                Log($"AUDIO {connectionId}", $"mic-state=unavailable task=ReceiveMicrophoneAsync endType=EOF exception={ex.GetType().Name} message={ex.Message}");
                throw;
            }
            catch (InvalidDataException ex)
            {
                Log($"AUDIO {connectionId}", $"mic-state=unavailable task=ReceiveMicrophoneAsync endType=InvalidDataException exception={ex.GetType().Name} message={ex.Message}");
                throw;
            }
            catch (IOException ex)
            {
                Log($"AUDIO {connectionId}", $"mic-state=unavailable task=ReceiveMicrophoneAsync endType=IOException exception={ex.GetType().Name} message={ex.Message}");
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(header);
                ArrayPool<byte>.Shared.Return(payload);
            }
        }

        private async Task SendSpeakerAsync(int connectionId, Stream stream, CancellationToken cancellationToken)
        {
            var header = ArrayPool<byte>.Shared.Rent(HeaderSize);
            var payload = ArrayPool<byte>.Shared.Rent(SpeakerChunkBytes);
            long packetCount = 0;
            long byteCount = 0;
            var lastStatsAt = DateTimeOffset.MinValue;
            var lastUnavailableAt = DateTimeOffset.MinValue;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var readResult = _audioBackend.ReadSpeaker(payload, SpeakerChunkBytes);
                    if (!readResult.EndpointReady)
                    {
                        var unavailableNow = DateTimeOffset.UtcNow;
                        if (unavailableNow - lastUnavailableAt >= TimeSpan.FromSeconds(1))
                        {
                            lastUnavailableAt = unavailableNow;
                            Log("AUDIO", $"speaker-state=unavailable backend={_audioBackend.Name} system-endpoint=missing message={readResult.Message}");
                            UpdateDirectionTelemetry(
                                AudioRuntimeDirection.Speaker,
                                state: "unavailable",
                                message: readResult.Message,
                                endpointReady: false,
                                lastError: readResult.Message);
                            await PublishSpeakerStatusAsync("unavailable", readResult.Message, cancellationToken, packetCount, byteCount);
                        }

                        await Task.Delay(250, cancellationToken);
                        continue;
                    }

                    var bytesRead = readResult.BytesRead;
                    if (bytesRead <= 0)
                    {
                        var waitingNow = DateTimeOffset.UtcNow;
                        if (lastStatsAt == DateTimeOffset.MinValue || waitingNow - lastStatsAt >= TimeSpan.FromSeconds(1))
                        {
                            lastStatsAt = waitingNow;
                            Log("AUDIO", $"speaker-state=available backend={_audioBackend.Name} system-endpoint=ready");
                            UpdateDirectionTelemetry(
                                AudioRuntimeDirection.Speaker,
                                state: "available",
                                message: "等待所选 Windows 输出设备产生声音。",
                                packets: packetCount,
                                bytes: byteCount,
                                levelPercent: 0,
                                endpointReady: true);
                            await PublishSpeakerStatusAsync("available", "等待所选 Windows 输出设备产生声音。", cancellationToken);
                        }

                        await Task.Delay(10, cancellationToken);
                        continue;
                    }

                    packetCount += 1;
                    byteCount += bytesRead;
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    WriteHeader(header, SpeakerMagic, packetCount, nowMs, AudioDefaults.SpeakerChannels, bytesRead);
                    await stream.WriteAsync(header.AsMemory(0, HeaderSize), cancellationToken);
                    await stream.WriteAsync(payload.AsMemory(0, bytesRead), cancellationToken);
                    await stream.FlushAsync(cancellationToken);

                    var now = DateTimeOffset.UtcNow;
                    UpdateDirectionTelemetry(
                        AudioRuntimeDirection.Speaker,
                        state: "playing",
                        message: "电脑输出正在发送到 Android 播放。",
                        packets: packetCount,
                        bytes: byteCount,
                        levelPercent: CalculatePcm16LevelPercent(payload, bytesRead),
                        lastPacketUnixMs: now.ToUnixTimeMilliseconds(),
                        sourceAgeMs: 0,
                        approximateLatencyMs: 0,
                        endpointReady: true);
                    if (packetCount == 1 || now - lastStatsAt >= TimeSpan.FromSeconds(1))
                    {
                        Log("AUDIO", $"speaker-state=playing backend={_audioBackend.Name} packets={packetCount} bytes={byteCount} system-endpoint=ready");
                        await PublishSpeakerStatusAsync(
                            "playing",
                            "电脑输出正在发送到 Android 播放。",
                            cancellationToken,
                            packetCount,
                            byteCount);
                        lastStatsAt = now;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (EndOfStreamException ex)
            {
                Log($"AUDIO {connectionId}", $"speaker-state=unavailable task=SendSpeakerAsync endType=EOF exception={ex.GetType().Name} message={ex.Message}");
                throw;
            }
            catch (InvalidDataException ex)
            {
                Log($"AUDIO {connectionId}", $"speaker-state=unavailable task=SendSpeakerAsync endType=InvalidDataException exception={ex.GetType().Name} message={ex.Message}");
                throw;
            }
            catch (IOException ex)
            {
                Log($"AUDIO {connectionId}", $"speaker-state=unavailable task=SendSpeakerAsync endType=IOException exception={ex.GetType().Name} message={ex.Message}");
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(header);
                ArrayPool<byte>.Shared.Return(payload);
            }
        }

        private enum AudioDirectionEndKind
        {
            Normal,
            EndOfStream,
            IOException,
            InvalidDataException,
            OperationCanceled,
            Exception
        }

        private sealed record AudioDirectionCompletion(
            string TaskName,
            AudioDirectionEndKind Kind,
            string ExceptionType,
            string Message);

        private static async Task<AudioDirectionCompletion> MonitorAudioDirectionAsync(
            string taskName,
            Func<CancellationToken, Task> action,
            CancellationToken cancellationToken)
        {
            try
            {
                await action(cancellationToken);
                return new AudioDirectionCompletion(taskName, AudioDirectionEndKind.Normal, string.Empty, "正常结束");
            }
            catch (EndOfStreamException ex)
            {
                return new AudioDirectionCompletion(taskName, AudioDirectionEndKind.EndOfStream, ex.GetType().Name, ex.Message);
            }
            catch (InvalidDataException ex)
            {
                return new AudioDirectionCompletion(taskName, AudioDirectionEndKind.InvalidDataException, ex.GetType().Name, ex.Message);
            }
            catch (OperationCanceledException ex)
            {
                return new AudioDirectionCompletion(taskName, AudioDirectionEndKind.OperationCanceled, ex.GetType().Name, ex.Message);
            }
            catch (IOException ex)
            {
                return new AudioDirectionCompletion(taskName, AudioDirectionEndKind.IOException, ex.GetType().Name, ex.Message);
            }
            catch (Exception ex)
            {
                return new AudioDirectionCompletion(taskName, AudioDirectionEndKind.Exception, ex.GetType().Name, ex.Message);
            }
        }

        private static async Task DrainAudioDirectionTasksAsync(
            int connectionId,
            IReadOnlyCollection<Task<AudioDirectionCompletion>> directionTasks)
        {
            if (directionTasks.Count == 0)
            {
                return;
            }

            try
            {
                var completions = await Task.WhenAll(directionTasks).WaitAsync(TimeSpan.FromSeconds(2));
                foreach (var completion in completions)
                {
                    Log(
                        $"AUDIO {connectionId}",
                        "audio-task-ended first=false " + FormatAudioDirectionCompletion(completion));
                }
            }
            catch (TimeoutException)
            {
                var pending = directionTasks.Count(task => !task.IsCompleted);
                Log($"AUDIO {connectionId}", $"audio-task-drain timeout pending={pending}");
            }
            catch (Exception ex)
            {
                Log(
                    $"AUDIO {connectionId}",
                    $"audio-task-drain failed exception={ex.GetType().Name} message={LogValue(ex.Message)}");
            }
        }

        private static string FormatAudioDirectionCompletion(AudioDirectionCompletion completion)
        {
            return $"task={completion.TaskName} endType={completion.Kind} exception={LogValue(completion.ExceptionType)} message={LogValue(completion.Message)}";
        }

        private static string SafeEndpoint(TcpClient client, bool remote)
        {
            try
            {
                var endpoint = remote ? client.Client.RemoteEndPoint : client.Client.LocalEndPoint;
                return endpoint?.ToString() ?? "unknown";
            }
            catch (ObjectDisposedException)
            {
                return "disposed";
            }
            catch (SocketException ex)
            {
                return $"socket-error:{ex.SocketErrorCode}";
            }
        }

        private static string DescribeSocket(TcpClient client)
        {
            try
            {
                var socket = client.Client;
                return $"connected={socket.Connected} available={socket.Available} local={socket.LocalEndPoint?.ToString() ?? "unknown"} remote={socket.RemoteEndPoint?.ToString() ?? "unknown"}";
            }
            catch (ObjectDisposedException)
            {
                return "disposed=true";
            }
            catch (SocketException ex)
            {
                return $"socket-error={ex.SocketErrorCode}";
            }
            catch (InvalidOperationException ex)
            {
                return $"socket-invalid={LogValue(ex.Message)}";
            }
        }

        private static void CloseAudioSocket(TcpClient client)
        {
            try
            {
                client.Client.Shutdown(SocketShutdown.Both);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // The socket may already be closed by Android or by the opposite audio worker.
            }

            try
            {
                client.Close();
            }
            catch
            {
                // Best effort during connection cleanup.
            }
        }

        private static string LogValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "(none)"
                : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private void InitializeTelemetryState()
        {
            UpdateDirectionTelemetry(
                AudioRuntimeDirection.Microphone,
                state: options.AudioDeviceEnabled && options.MicrophoneEnabled ? "preparing" : "disabled",
                message: options.AudioDeviceEnabled && options.MicrophoneEnabled
                    ? "正在准备 Android 麦克风写入 Windows。"
                    : "SideDock 麦克风已关闭。",
                endpointReady: _audioBackend.IsMicrophoneReady);
            UpdateDirectionTelemetry(
                AudioRuntimeDirection.Speaker,
                state: options.AudioDeviceEnabled && options.SpeakerEnabled ? "preparing" : "disabled",
                message: options.AudioDeviceEnabled && options.SpeakerEnabled
                    ? "正在准备电脑声音发送到 Android。"
                    : "SideDock 音响已关闭。",
                endpointReady: _audioBackend.IsSpeakerReady);
        }

        private async Task PublishTelemetryLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                await PublishAudioRuntimeTelemetryAsync(cancellationToken);
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    await PublishAudioRuntimeTelemetryAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }

        private static async Task WaitForTelemetryLoopAsync(Task telemetryTask)
        {
            try
            {
                await telemetryTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Best effort during shutdown.
            }
        }

        private ValueTask PublishAudioRuntimeTelemetryAsync(CancellationToken cancellationToken)
        {
            return controlPublisher.PublishAsync(
                "audio_runtime_telemetry",
                _telemetryState.CreatePayload(_audioBackend),
                cancellationToken);
        }

        private void UpdateDirectionTelemetry(
            AudioRuntimeDirection direction,
            string state,
            string message,
            long? packets = null,
            long? bytes = null,
            int? levelPercent = null,
            long? lastPacketUnixMs = null,
            long? sourceAgeMs = null,
            long? approximateLatencyMs = null,
            bool? endpointReady = null,
            string? lastError = null)
        {
            _telemetryState.Update(
                direction,
                entry =>
                {
                    entry.State = state;
                    entry.Message = message;
                    entry.SampleRate = AudioDefaults.SampleRate;
                    entry.BitsPerSample = AudioDefaults.BitsPerSample;
                    entry.Channels = direction == AudioRuntimeDirection.Microphone
                        ? AudioDefaults.MicChannels
                        : AudioDefaults.SpeakerChannels;
                    entry.Backend = _audioBackend.Name;
                    entry.EndpointReady = endpointReady ?? (direction == AudioRuntimeDirection.Microphone
                        ? _audioBackend.IsMicrophoneReady
                        : _audioBackend.IsSpeakerReady);
                    entry.EndpointId = direction == AudioRuntimeDirection.Microphone
                        ? _audioBackend.MicrophoneEndpointId
                        : _audioBackend.SpeakerEndpointId;
                    entry.EndpointName = direction == AudioRuntimeDirection.Microphone
                        ? _audioBackend.MicrophoneEndpointName
                        : _audioBackend.SpeakerEndpointName;
                    entry.LastError = string.IsNullOrWhiteSpace(lastError) ? null : lastError;
                    entry.ReconnectCount = Interlocked.Read(ref _reconnectCount);
                    entry.DisconnectCount = Interlocked.Read(ref _disconnectCount);

                    if (packets.HasValue)
                    {
                        entry.Packets = packets.Value;
                    }

                    if (bytes.HasValue)
                    {
                        entry.Bytes = bytes.Value;
                    }

                    if (levelPercent.HasValue)
                    {
                        entry.LevelPercent = Math.Clamp(levelPercent.Value, 0, 100);
                    }

                    if (lastPacketUnixMs.HasValue)
                    {
                        entry.LastPacketUnixMs = lastPacketUnixMs.Value;
                    }

                    entry.SourceAgeMs = sourceAgeMs;
                    entry.ApproximateLatencyMs = approximateLatencyMs;
                });
        }

        private void UpdateReconnectCount(long reconnectCount)
        {
            _telemetryState.UpdateAll(entry => entry.ReconnectCount = reconnectCount);
        }

        private void UpdateDisconnectCount(long disconnectCount)
        {
            _telemetryState.UpdateAll(entry => entry.DisconnectCount = disconnectCount);
        }

        private static int CalculatePcm16LevelPercent(byte[] buffer, int byteCount)
        {
            var boundedByteCount = Math.Min(buffer.Length, byteCount);
            boundedByteCount -= boundedByteCount % 2;
            var peak = 0;
            for (var offset = 0; offset < boundedByteCount; offset += 2)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, 2));
                var absolute = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
                if (absolute > peak)
                {
                    peak = absolute;
                }
            }

            return Math.Clamp((int)Math.Round(peak * 100.0 / short.MaxValue), 0, 100);
        }

        private async Task PublishMicStatusAsync(
            string state,
            string message,
            CancellationToken cancellationToken,
            long packets = 0,
            long bytes = 0)
        {
            await controlPublisher.PublishAsync("audio_mic_status", new JsonObject
            {
                ["state"] = state,
                ["message"] = message,
                ["packets"] = packets,
                ["bytes"] = bytes,
                ["sampleRate"] = AudioDefaults.SampleRate,
                ["channels"] = AudioDefaults.MicChannels,
                ["bitsPerSample"] = AudioDefaults.BitsPerSample,
                ["backend"] = _audioBackend.Name,
                ["systemEndpoint"] = _audioBackend.IsMicrophoneReady,
                ["systemEndpointMessage"] = _audioBackend.MicrophoneStatusMessage
            }, cancellationToken);
        }

        private async Task PublishSpeakerStatusAsync(
            string state,
            string message,
            CancellationToken cancellationToken,
            long packets = 0,
            long bytes = 0)
        {
            await controlPublisher.PublishAsync("audio_speaker_status", new JsonObject
            {
                ["state"] = state,
                ["message"] = message,
                ["packets"] = packets,
                ["bytes"] = bytes,
                ["sampleRate"] = AudioDefaults.SampleRate,
                ["channels"] = AudioDefaults.SpeakerChannels,
                ["bitsPerSample"] = AudioDefaults.BitsPerSample,
                ["backend"] = _audioBackend.Name,
                ["systemEndpoint"] = _audioBackend.IsSpeakerReady,
                ["systemEndpointMessage"] = _audioBackend.SpeakerStatusMessage
            }, cancellationToken);
        }

        private static void ValidateHeader(byte[] header, byte[] magic, string direction)
        {
            if (!header.AsSpan(0, 4).SequenceEqual(magic))
            {
                throw new InvalidDataException($"Invalid {direction} packet magic.");
            }

            var version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            if (version != 1)
            {
                throw new InvalidDataException($"Unsupported {direction} packet version: {version}.");
            }
        }

        private static void WriteHeader(byte[] header, byte[] magic, long sequence, long timestampMs, int channels, int payloadLength)
        {
            magic.CopyTo(header, 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 1);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8, 8), sequence);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16, 8), timestampMs);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), AudioDefaults.SampleRate);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(28, 2), (short)channels);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(30, 2), AudioDefaults.BitsPerSample);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32, 4), payloadLength);
        }

        private static IAudioBridgeBackend CreateAudioBackend(HostOptions hostOptions)
        {
            return hostOptions.AudioBackend switch
            {
                AudioBackendKind.WasapiVirtualCable => new WasapiVirtualCableAudioBackend(
                    hostOptions.AudioOutputLoopbackEndpointId,
                    hostOptions.AudioMicrophoneRenderEndpointId),
                _ => new LegacySharedMemoryAudioBackend()
            };
        }

        private interface IAudioBridgeBackend : IDisposable
        {
            string Name { get; }

            bool IsMicrophoneReady { get; }

            string MicrophoneStatusMessage { get; }

            string? MicrophoneEndpointId { get; }

            string? MicrophoneEndpointName { get; }

            bool IsSpeakerReady { get; }

            string SpeakerStatusMessage { get; }

            string? SpeakerEndpointId { get; }

            string? SpeakerEndpointName { get; }

            bool EnsureMicrophoneReady(out string message);

            bool WriteMicrophone(byte[] buffer, int byteCount, long timestampMs, out string message);

            bool EnsureSpeakerReady(out string message);

            AudioReadResult ReadSpeaker(byte[] destination, int maxByteCount);
        }

        private sealed record AudioReadResult(bool EndpointReady, int BytesRead, string Message);

        private enum AudioRuntimeDirection
        {
            Microphone,
            Speaker
        }

        private sealed class AudioRuntimeTelemetryState(int audioPort)
        {
            private readonly object _lock = new();
            private readonly AudioRuntimeDirectionEntry _microphone = new("microphone");
            private readonly AudioRuntimeDirectionEntry _speaker = new("speaker");

            public void Update(AudioRuntimeDirection direction, Action<AudioRuntimeDirectionEntry> update)
            {
                lock (_lock)
                {
                    update(EntryFor(direction));
                }
            }

            public void UpdateAll(Action<AudioRuntimeDirectionEntry> update)
            {
                lock (_lock)
                {
                    update(_microphone);
                    update(_speaker);
                }
            }

            public JsonObject CreatePayload(IAudioBridgeBackend backend)
            {
                lock (_lock)
                {
                    var now = DateTimeOffset.UtcNow;
                    RefreshRates(_microphone, now);
                    RefreshRates(_speaker, now);
                    RefreshEndpoint(_microphone, backend, AudioRuntimeDirection.Microphone);
                    RefreshEndpoint(_speaker, backend, AudioRuntimeDirection.Speaker);

                    return new JsonObject
                    {
                        ["origin"] = "host",
                        ["telemetryUnixMs"] = now.ToUnixTimeMilliseconds(),
                        ["audioPort"] = audioPort,
                        ["backend"] = backend.Name,
                        ["sampleRate"] = AudioDefaults.SampleRate,
                        ["bitsPerSample"] = AudioDefaults.BitsPerSample,
                        ["microphone"] = ToJson(_microphone, now),
                        ["speaker"] = ToJson(_speaker, now)
                    };
                }
            }

            private AudioRuntimeDirectionEntry EntryFor(AudioRuntimeDirection direction)
            {
                return direction == AudioRuntimeDirection.Microphone ? _microphone : _speaker;
            }

            private static void RefreshRates(AudioRuntimeDirectionEntry entry, DateTimeOffset now)
            {
                if (entry.LastRateAt == DateTimeOffset.MinValue)
                {
                    entry.LastRateAt = now;
                    entry.LastRatePackets = entry.Packets;
                    entry.LastRateBytes = entry.Bytes;
                    entry.PacketsPerSecond = 0;
                    entry.BytesPerSecond = 0;
                    return;
                }

                var elapsedSeconds = (now - entry.LastRateAt).TotalSeconds;
                if (elapsedSeconds <= 0)
                {
                    return;
                }

                entry.PacketsPerSecond = Math.Max(0, (entry.Packets - entry.LastRatePackets) / elapsedSeconds);
                entry.BytesPerSecond = Math.Max(0, (entry.Bytes - entry.LastRateBytes) / elapsedSeconds);
                entry.LastRateAt = now;
                entry.LastRatePackets = entry.Packets;
                entry.LastRateBytes = entry.Bytes;
            }

            private static void RefreshEndpoint(
                AudioRuntimeDirectionEntry entry,
                IAudioBridgeBackend backend,
                AudioRuntimeDirection direction)
            {
                if (direction == AudioRuntimeDirection.Microphone)
                {
                    entry.EndpointReady = backend.IsMicrophoneReady;
                    entry.EndpointId = backend.MicrophoneEndpointId;
                    entry.EndpointName = backend.MicrophoneEndpointName;
                    entry.Backend = backend.Name;
                    if (string.IsNullOrWhiteSpace(entry.Message))
                    {
                        entry.Message = backend.MicrophoneStatusMessage;
                    }
                }
                else
                {
                    entry.EndpointReady = backend.IsSpeakerReady;
                    entry.EndpointId = backend.SpeakerEndpointId;
                    entry.EndpointName = backend.SpeakerEndpointName;
                    entry.Backend = backend.Name;
                    if (string.IsNullOrWhiteSpace(entry.Message))
                    {
                        entry.Message = backend.SpeakerStatusMessage;
                    }
                }
            }

            private static JsonObject ToJson(AudioRuntimeDirectionEntry entry, DateTimeOffset now)
            {
                var lastPacketAgeMs = entry.LastPacketUnixMs.HasValue
                    ? Math.Max(0, now.ToUnixTimeMilliseconds() - entry.LastPacketUnixMs.Value)
                    : (long?)null;

                return new JsonObject
                {
                    ["direction"] = entry.Direction,
                    ["state"] = entry.State,
                    ["message"] = entry.Message,
                    ["packets"] = entry.Packets,
                    ["bytes"] = entry.Bytes,
                    ["packetsPerSecond"] = Math.Round(entry.PacketsPerSecond, 2),
                    ["bytesPerSecond"] = Math.Round(entry.BytesPerSecond, 2),
                    ["levelPercent"] = entry.LevelPercent,
                    ["peakLevel"] = entry.LevelPercent,
                    ["lastPacketUnixMs"] = NumberOrNull(entry.LastPacketUnixMs),
                    ["lastPacketAgeMs"] = NumberOrNull(lastPacketAgeMs),
                    ["sourceAgeMs"] = NumberOrNull(entry.SourceAgeMs),
                    ["approximateLatencyMs"] = NumberOrNull(entry.ApproximateLatencyMs),
                    ["endpointId"] = StringOrNull(entry.EndpointId),
                    ["endpointName"] = StringOrNull(entry.EndpointName),
                    ["endpointReady"] = entry.EndpointReady,
                    ["sampleRate"] = entry.SampleRate,
                    ["channels"] = entry.Channels,
                    ["bitsPerSample"] = entry.BitsPerSample,
                    ["backend"] = entry.Backend,
                    ["lastError"] = StringOrNull(entry.LastError),
                    ["reconnectCount"] = entry.ReconnectCount,
                    ["disconnectCount"] = entry.DisconnectCount
                };
            }

            private static JsonNode? NumberOrNull(long? value)
            {
                return value.HasValue ? JsonValue.Create(value.Value) : null;
            }

            private static JsonNode? StringOrNull(string? value)
            {
                return string.IsNullOrWhiteSpace(value) ? null : JsonValue.Create(value);
            }
        }

        private sealed class AudioRuntimeDirectionEntry(string direction)
        {
            public string Direction { get; } = direction;

            public string State { get; set; } = "preparing";

            public string Message { get; set; } = "";

            public long Packets { get; set; }

            public long Bytes { get; set; }

            public double PacketsPerSecond { get; set; }

            public double BytesPerSecond { get; set; }

            public int LevelPercent { get; set; }

            public long? LastPacketUnixMs { get; set; }

            public long? SourceAgeMs { get; set; }

            public long? ApproximateLatencyMs { get; set; }

            public string? EndpointId { get; set; }

            public string? EndpointName { get; set; }

            public bool EndpointReady { get; set; }

            public string? LastError { get; set; }

            public long ReconnectCount { get; set; }

            public long DisconnectCount { get; set; }

            public int SampleRate { get; set; } = AudioDefaults.SampleRate;

            public int Channels { get; set; }

            public int BitsPerSample { get; set; } = AudioDefaults.BitsPerSample;

            public string Backend { get; set; } = "";

            public DateTimeOffset LastRateAt { get; set; } = DateTimeOffset.MinValue;

            public long LastRatePackets { get; set; }

            public long LastRateBytes { get; set; }
        }

        private sealed class LegacySharedMemoryAudioBackend : IAudioBridgeBackend
        {
            private readonly AudioMicSharedRing _micRing = new();
            private readonly AudioSpeakerSharedRing _speakerRing = new();
            private long _speakerReadPosition;

            public string Name => "legacy-shared-memory";

            public bool IsMicrophoneReady => _micRing.IsReady;

            public string MicrophoneStatusMessage => _micRing.StatusMessage;

            public string? MicrophoneEndpointId => "legacy-shared-memory-microphone";

            public string? MicrophoneEndpointName => "SideDock legacy microphone shared ring";

            public bool IsSpeakerReady => _speakerRing.IsReady;

            public string SpeakerStatusMessage => _speakerRing.StatusMessage;

            public string? SpeakerEndpointId => "legacy-shared-memory-speaker";

            public string? SpeakerEndpointName => "SideDock legacy speaker shared ring";

            public bool EnsureMicrophoneReady(out string message)
            {
                return _micRing.EnsureReady(out message);
            }

            public bool WriteMicrophone(byte[] buffer, int byteCount, long timestampMs, out string message)
            {
                return _micRing.Write(buffer, byteCount, timestampMs, out message);
            }

            public bool EnsureSpeakerReady(out string message)
            {
                return _speakerRing.EnsureReady(out message);
            }

            public AudioReadResult ReadSpeaker(byte[] destination, int maxByteCount)
            {
                var endpointReady = _speakerRing.TryRead(
                    destination,
                    maxByteCount,
                    ref _speakerReadPosition,
                    out var bytesRead,
                    out var message);

                return new AudioReadResult(endpointReady, bytesRead, message);
            }

            public void Dispose()
            {
                _micRing.Dispose();
                _speakerRing.Dispose();
            }
        }

        private sealed class WasapiVirtualCableAudioBackend : IAudioBridgeBackend
        {
            private const int WasapiLatencyMilliseconds = 50;
            private const int MaxSpeakerQueueBytes = AudioDefaults.SampleRate * AudioDefaults.SpeakerFrameBytes;

            private readonly string? _speakerOutputLoopbackEndpointId;
            private readonly string? _microphoneRenderEndpointId;
            private readonly object _microphoneLock = new();
            private readonly object _speakerLock = new();
            private readonly ConcurrentQueue<byte[]> _speakerPackets = new();
            private readonly object _speakerPacketLock = new();

            private MMDeviceEnumerator? _deviceEnumerator;
            private MMDevice? _microphoneRenderDevice;
            private BufferedWaveProvider? _microphoneSourceBuffer;
            private WasapiOut? _microphoneRenderOutput;
            private WaveFormat? _microphoneRenderFormat;
            private DateTimeOffset _lastMicrophoneOpenFailureLoggedAt = DateTimeOffset.MinValue;
            private string? _lastMicrophoneOpenFailureLogMessage;
            private string _microphoneStatusMessage = "Android 麦克风写入端点未初始化。";

            private MMDevice? _speakerCaptureDevice;
            private WasapiCapture? _speakerCapture;
            private WaveFormat? _speakerCaptureFormat;
            private string _speakerStatusMessage = "电脑声音 loopback 输出端点未初始化。";
            private int _queuedSpeakerBytes;
            private byte[]? _speakerPendingPacket;
            private int _speakerPendingOffset;
            private bool _disposed;

            public WasapiVirtualCableAudioBackend(string? speakerOutputLoopbackEndpointId, string? microphoneRenderEndpointId)
            {
                _speakerOutputLoopbackEndpointId = NormalizeEndpointId(speakerOutputLoopbackEndpointId);
                _microphoneRenderEndpointId = NormalizeEndpointId(microphoneRenderEndpointId);
            }

            public string Name => "wasapi-virtual-cable";

            public bool IsMicrophoneReady
            {
                get
                {
                    lock (_microphoneLock)
                    {
                        return _microphoneRenderOutput is not null && _microphoneSourceBuffer is not null;
                    }
                }
            }

            public string MicrophoneStatusMessage
            {
                get
                {
                    lock (_microphoneLock)
                    {
                        return _microphoneStatusMessage;
                    }
                }
            }

            public string? MicrophoneEndpointId => _microphoneRenderEndpointId;

            public string? MicrophoneEndpointName
            {
                get
                {
                    lock (_microphoneLock)
                    {
                        return _microphoneRenderDevice?.FriendlyName;
                    }
                }
            }

            public bool IsSpeakerReady
            {
                get
                {
                    lock (_speakerLock)
                    {
                        return _speakerCapture is not null;
                    }
                }
            }

            public string SpeakerStatusMessage
            {
                get
                {
                    lock (_speakerLock)
                    {
                        return _speakerStatusMessage;
                    }
                }
            }

            public string? SpeakerEndpointId => _speakerOutputLoopbackEndpointId;

            public string? SpeakerEndpointName
            {
                get
                {
                    lock (_speakerLock)
                    {
                        return _speakerCaptureDevice?.FriendlyName;
                    }
                }
            }

            public bool EnsureMicrophoneReady(out string message)
            {
                lock (_microphoneLock)
                {
                    ThrowIfDisposed();
                    if (_microphoneRenderOutput is not null && _microphoneSourceBuffer is not null)
                    {
                        message = _microphoneStatusMessage;
                        return true;
                    }

                    ResetMicrophoneRenderCore();
                    try
                    {
                        if (!OperatingSystem.IsWindows())
                        {
                            _microphoneStatusMessage = "WASAPI 麦克风写入端点仅支持 Windows Host。";
                            message = _microphoneStatusMessage;
                            return false;
                        }

                        if (string.IsNullOrWhiteSpace(_microphoneRenderEndpointId))
                        {
                            _microphoneStatusMessage = "未配置 Android 麦克风写入端点，请选择虚拟线缆的 Input/播放端。";
                            message = _microphoneStatusMessage;
                            return false;
                        }

                        var device = GetEndpoint(_microphoneRenderEndpointId, DataFlow.Render, "Android 麦克风写入端点");
                        var mixFormat = device.AudioClient.MixFormat;
                        var renderFormat = SelectMicrophoneRenderFormat(device);
                        var sourceFormat = new WaveFormat(AudioDefaults.SampleRate, AudioDefaults.BitsPerSample, AudioDefaults.MicChannels);
                        var sourceBuffer = new BufferedWaveProvider(sourceFormat)
                        {
                            BufferDuration = TimeSpan.FromMilliseconds(500),
                            DiscardOnBufferOverflow = true,
                            ReadFully = true
                        };
                        var renderProvider = CreateMicrophoneRenderProvider(sourceBuffer, renderFormat);
                        var output = new WasapiOut(device, AudioClientShareMode.Shared, true, WasapiLatencyMilliseconds);
                        output.PlaybackStopped += (_, args) =>
                        {
                            if (args.Exception is not null)
                            {
                                lock (_microphoneLock)
                                {
                                    _microphoneStatusMessage = $"Android 麦克风写入端点已停止：{args.Exception.Message}";
                                    ResetMicrophoneRenderCore();
                                }
                            }
                        };
                        output.Init(renderProvider);
                        output.Play();

                        _microphoneRenderDevice = device;
                        _microphoneSourceBuffer = sourceBuffer;
                        _microphoneRenderOutput = output;
                        _microphoneRenderFormat = renderFormat;
                        _lastMicrophoneOpenFailureLogMessage = null;
                        _microphoneStatusMessage = $"Android 麦克风写入端点已就绪：{device.FriendlyName}；direction={device.DataFlow}；sourceFormat={FormatWaveFormat(sourceFormat)}；selectedFormat={FormatWaveFormat(renderFormat)}；mixFormat={FormatWaveFormat(mixFormat)}。";
                        Log(
                            "AUDIO",
                            $"wasapi-endpoint-opened role=microphone-render friendlyName={LogValue(device.FriendlyName)} direction={device.DataFlow} sourceFormat={LogValue(FormatWaveFormat(sourceFormat))} selectedFormat={LogValue(FormatWaveFormat(renderFormat))} mixFormat={LogValue(FormatWaveFormat(mixFormat))}");
                        message = _microphoneStatusMessage;
                        return true;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException or UnauthorizedAccessException)
                    {
                        ResetMicrophoneRenderCore();
                        _microphoneStatusMessage = $"Android 麦克风写入端点不可用：{ex.Message}";
                        LogMicrophoneOpenFailure(ex);
                        message = _microphoneStatusMessage;
                        return false;
                    }
                }
            }

            public bool WriteMicrophone(byte[] buffer, int byteCount, long timestampMs, out string message)
            {
                if (buffer is null || byteCount <= 0)
                {
                    message = MicrophoneStatusMessage;
                    return IsMicrophoneReady;
                }

                lock (_microphoneLock)
                {
                    if ((_microphoneRenderOutput is null || _microphoneSourceBuffer is null || _microphoneRenderFormat is null)
                        && !EnsureMicrophoneReady(out message))
                    {
                        return false;
                    }

                    if (_microphoneSourceBuffer is null || _microphoneRenderFormat is null)
                    {
                        message = _microphoneStatusMessage;
                        return false;
                    }

                    try
                    {
                        var boundedByteCount = byteCount - (byteCount % AudioDefaults.MicFrameBytes);
                        if (boundedByteCount <= 0)
                        {
                            message = _microphoneStatusMessage;
                            return true;
                        }

                        _microphoneSourceBuffer.AddSamples(buffer, 0, boundedByteCount);
                        _microphoneStatusMessage = "Android 麦克风正在写入 Windows 虚拟线缆。";
                        message = _microphoneStatusMessage;
                        return true;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or COMException)
                    {
                        ResetMicrophoneRenderCore();
                        _microphoneStatusMessage = $"Android 麦克风写入端点不可用：{ex.Message}";
                        message = _microphoneStatusMessage;
                        return false;
                    }
                }
            }

            public bool EnsureSpeakerReady(out string message)
            {
                lock (_speakerLock)
                {
                    ThrowIfDisposed();
                    if (_speakerCapture is not null)
                    {
                        message = _speakerStatusMessage;
                        return true;
                    }

                    ResetSpeakerCaptureCore();
                    try
                    {
                        if (!OperatingSystem.IsWindows())
                        {
                            _speakerStatusMessage = "WASAPI 电脑声音 loopback 输出端点仅支持 Windows Host。";
                            message = _speakerStatusMessage;
                            return false;
                        }

                        if (string.IsNullOrWhiteSpace(_speakerOutputLoopbackEndpointId))
                        {
                            _speakerStatusMessage = "未配置电脑声音 loopback 输出端点，请选择扬声器、Voicemeeter Input、CABLE Input 等 Windows 输出/播放设备。";
                            message = _speakerStatusMessage;
                            return false;
                        }

                        var device = GetEndpoint(_speakerOutputLoopbackEndpointId, DataFlow.Render, "电脑声音 loopback 输出端点");
                        var mixFormat = device.AudioClient.MixFormat;
                        var format = new WaveFormat(AudioDefaults.SampleRate, AudioDefaults.BitsPerSample, AudioDefaults.SpeakerChannels);
                        EnsureFormatSupported(device, format, "电脑声音 loopback 输出端点");
                        var capture = new WasapiLoopbackCapture(device)
                        {
                            WaveFormat = format
                        };
                        capture.DataAvailable += OnSpeakerDataAvailable;
                        capture.RecordingStopped += OnSpeakerRecordingStopped;
                        capture.StartRecording();

                        _speakerCaptureDevice = device;
                        _speakerCapture = capture;
                        _speakerCaptureFormat = format;
                        _speakerStatusMessage = $"电脑声音 loopback 输出端点已就绪：{device.FriendlyName}；direction={device.DataFlow}；selectedFormat={FormatWaveFormat(format)}；mixFormat={FormatWaveFormat(mixFormat)}。";
                        Log(
                            "AUDIO",
                            $"wasapi-endpoint-opened role=speaker-loopback friendlyName={LogValue(device.FriendlyName)} direction={device.DataFlow} selectedFormat={LogValue(FormatWaveFormat(format))} mixFormat={LogValue(FormatWaveFormat(mixFormat))}");
                        message = _speakerStatusMessage;
                        return true;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException or UnauthorizedAccessException)
                    {
                        ResetSpeakerCaptureCore();
                        _speakerStatusMessage = $"电脑声音 loopback 输出端点不可用：{ex.Message}";
                        Log(
                            "AUDIO",
                            $"wasapi-endpoint-open-failed role=speaker-loopback exception={ex.GetType().Name} message={LogValue(ex.Message)}");
                        message = _speakerStatusMessage;
                        return false;
                    }
                }
            }

            public AudioReadResult ReadSpeaker(byte[] destination, int maxByteCount)
            {
                if (destination is null || maxByteCount <= 0)
                {
                    var message = SpeakerStatusMessage;
                    return new AudioReadResult(IsSpeakerReady, 0, message);
                }

                if (!EnsureSpeakerReady(out var readyMessage))
                {
                    return new AudioReadResult(false, 0, readyMessage);
                }

                var bytesRead = DequeueSpeakerBytes(destination, maxByteCount);
                if (bytesRead > 0)
                {
                    lock (_speakerLock)
                    {
                        _speakerStatusMessage = "正在 loopback 捕获电脑输出并发送到 Android。";
                        readyMessage = _speakerStatusMessage;
                    }
                }

                return new AudioReadResult(true, bytesRead, readyMessage);
            }

            public void Dispose()
            {
                lock (_microphoneLock)
                lock (_speakerLock)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    ResetMicrophoneRenderCore();
                    ResetSpeakerCaptureCore();
                    _deviceEnumerator?.Dispose();
                    _deviceEnumerator = null;
                }
            }

            private MMDevice GetEndpoint(string endpointId, DataFlow expectedDataFlow, string role)
            {
                _deviceEnumerator ??= new MMDeviceEnumerator();
                var device = _deviceEnumerator.GetDevice(endpointId);
                if (device.DataFlow != expectedDataFlow)
                {
                    device.Dispose();
                    throw new InvalidOperationException(BuildDirectionMismatchMessage(role, expectedDataFlow, device.DataFlow));
                }

                if (device.State != DeviceState.Active)
                {
                    var state = device.State;
                    var name = device.FriendlyName;
                    device.Dispose();
                    throw new InvalidOperationException($"{role}未启用：{name} ({state})。");
                }

                return device;
            }

            private static string BuildDirectionMismatchMessage(string role, DataFlow expectedDataFlow, DataFlow actualDataFlow)
            {
                if (role.Contains("loopback", StringComparison.OrdinalIgnoreCase)
                    || role.Contains("输出端点", StringComparison.Ordinal))
                {
                    return $"{role}方向不匹配：请选择 Windows 输出/播放设备，例如扬声器、Voicemeeter Input 或 CABLE Input；不要选择 Voicemeeter Out 或 CABLE Output 这类录制端。实际端点方向为 {actualDataFlow}，期望 {expectedDataFlow}。";
                }

                if (role.Contains("麦克风写入", StringComparison.Ordinal))
                {
                    return $"{role}方向不匹配：请选择虚拟线缆/Voicemeeter 的 Input/播放端；通话软件麦克风再选择同一链路对应的 Output/录制端。实际端点方向为 {actualDataFlow}，期望 {expectedDataFlow}。";
                }

                return $"{role}方向不匹配：期望 {expectedDataFlow}，实际 {actualDataFlow}。";
            }

            private static WaveFormat SelectMicrophoneRenderFormat(MMDevice device)
            {
                foreach (var candidate in EnumerateMicrophoneRenderFormatCandidates(device.AudioClient.MixFormat))
                {
                    if (IsFormatSupported(device, candidate))
                    {
                        return candidate;
                    }
                }

                throw new InvalidOperationException(
                    $"Android 麦克风写入端点不支持 SideDock 可转换的共享模式格式；当前混音格式为 {FormatWaveFormat(device.AudioClient.MixFormat)}。请尝试在 Windows 声音设置里把虚拟线缆默认格式改为 48 kHz 或 44.1 kHz / 16-bit / stereo。");
            }

            private static IEnumerable<WaveFormat> EnumerateMicrophoneRenderFormatCandidates(WaveFormat mixFormat)
            {
                var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var candidate in EnumerateMicrophoneRenderFormatCandidatesCore(mixFormat))
                {
                    var key = $"{candidate.Encoding}:{candidate.SampleRate}:{candidate.BitsPerSample}:{candidate.Channels}";
                    if (emitted.Add(key))
                    {
                        yield return candidate;
                    }
                }
            }

            private static IEnumerable<WaveFormat> EnumerateMicrophoneRenderFormatCandidatesCore(WaveFormat mixFormat)
            {
                yield return new WaveFormat(AudioDefaults.SampleRate, AudioDefaults.BitsPerSample, AudioDefaults.MicChannels);
                yield return new WaveFormat(AudioDefaults.SampleRate, AudioDefaults.BitsPerSample, AudioDefaults.SpeakerChannels);

                var mixChannels = NormalizeMicrophoneRenderChannels(mixFormat.Channels);
                yield return new WaveFormat(mixFormat.SampleRate, AudioDefaults.BitsPerSample, mixChannels);
                yield return new WaveFormat(mixFormat.SampleRate, AudioDefaults.BitsPerSample, AudioDefaults.SpeakerChannels);
                yield return WaveFormat.CreateIeeeFloatWaveFormat(mixFormat.SampleRate, mixChannels);
                yield return WaveFormat.CreateIeeeFloatWaveFormat(mixFormat.SampleRate, AudioDefaults.SpeakerChannels);

                yield return new WaveFormat(44100, AudioDefaults.BitsPerSample, AudioDefaults.SpeakerChannels);
                yield return WaveFormat.CreateIeeeFloatWaveFormat(44100, AudioDefaults.SpeakerChannels);
            }

            private static int NormalizeMicrophoneRenderChannels(int channels)
            {
                return channels == AudioDefaults.MicChannels
                    ? AudioDefaults.MicChannels
                    : AudioDefaults.SpeakerChannels;
            }

            private static IWaveProvider CreateMicrophoneRenderProvider(BufferedWaveProvider sourceBuffer, WaveFormat renderFormat)
            {
                ISampleProvider sampleProvider = new Pcm16BitToSampleProvider(sourceBuffer);

                if (renderFormat.Channels == AudioDefaults.SpeakerChannels)
                {
                    sampleProvider = new MonoToStereoSampleProvider(sampleProvider);
                }
                else if (renderFormat.Channels != AudioDefaults.MicChannels)
                {
                    throw new InvalidOperationException($"不支持的 Android 麦克风写入声道数：{renderFormat.Channels}。");
                }

                if (renderFormat.SampleRate != AudioDefaults.SampleRate)
                {
                    sampleProvider = new WdlResamplingSampleProvider(sampleProvider, renderFormat.SampleRate);
                }

                if (IsPcm16WaveFormat(renderFormat))
                {
                    return new SampleToWaveProvider16(sampleProvider);
                }

                if (IsIeeeFloat32WaveFormat(renderFormat))
                {
                    return new SampleToWaveProvider(sampleProvider);
                }

                throw new InvalidOperationException($"不支持的 Android 麦克风写入格式：{FormatWaveFormat(renderFormat)}。");
            }

            private static bool IsPcm16WaveFormat(WaveFormat format)
            {
                return format.Encoding == WaveFormatEncoding.Pcm
                    && format.BitsPerSample == AudioDefaults.BitsPerSample;
            }

            private static bool IsIeeeFloat32WaveFormat(WaveFormat format)
            {
                return format.Encoding == WaveFormatEncoding.IeeeFloat
                    && format.BitsPerSample == 32;
            }

            private static void EnsureFormatSupported(MMDevice device, WaveFormat format, string role)
            {
                if (!IsFormatSupported(device, format))
                {
                    throw new InvalidOperationException(
                        $"{role}不支持 {FormatWaveFormat(format)}；当前混音格式为 {FormatWaveFormat(device.AudioClient.MixFormat)}。请在 Windows 声音设置里把对应端点默认格式改为 48 kHz / 16-bit / stereo。");
                }
            }

            private static bool IsFormatSupported(MMDevice device, WaveFormat format)
            {
                try
                {
                    return device.AudioClient.IsFormatSupported(AudioClientShareMode.Shared, format);
                }
                catch (COMException)
                {
                    return false;
                }
            }

            private void OnSpeakerDataAvailable(object? sender, WaveInEventArgs args)
            {
                var byteCount = args.BytesRecorded - (args.BytesRecorded % AudioDefaults.SpeakerFrameBytes);
                if (byteCount <= 0)
                {
                    return;
                }

                var copy = new byte[byteCount];
                Buffer.BlockCopy(args.Buffer, 0, copy, 0, byteCount);

                lock (_speakerPacketLock)
                {
                    while (_queuedSpeakerBytes + copy.Length > MaxSpeakerQueueBytes && _speakerPackets.TryDequeue(out var dropped))
                    {
                        _queuedSpeakerBytes -= dropped.Length;
                    }

                    _speakerPackets.Enqueue(copy);
                    _queuedSpeakerBytes += copy.Length;
                }
            }

            private void OnSpeakerRecordingStopped(object? sender, StoppedEventArgs args)
            {
                if (args.Exception is null)
                {
                    return;
                }

                lock (_speakerLock)
                {
                    _speakerStatusMessage = $"电脑声音 loopback 输出端点已停止：{args.Exception.Message}";
                    ResetSpeakerCaptureCore();
                }
            }

            private int DequeueSpeakerBytes(byte[] destination, int maxByteCount)
            {
                lock (_speakerPacketLock)
                {
                    if (_speakerPendingPacket is null || _speakerPendingOffset >= _speakerPendingPacket.Length)
                    {
                        if (!_speakerPackets.TryDequeue(out _speakerPendingPacket))
                        {
                            _speakerPendingOffset = 0;
                            return 0;
                        }

                        _queuedSpeakerBytes -= _speakerPendingPacket.Length;
                        _speakerPendingOffset = 0;
                    }

                    var available = Math.Min(maxByteCount, destination.Length);
                    available = Math.Min(available, _speakerPendingPacket.Length - _speakerPendingOffset);
                    available -= available % AudioDefaults.SpeakerFrameBytes;
                    if (available <= 0)
                    {
                        return 0;
                    }

                    Buffer.BlockCopy(_speakerPendingPacket, _speakerPendingOffset, destination, 0, available);
                    _speakerPendingOffset += available;
                    if (_speakerPendingOffset >= _speakerPendingPacket.Length)
                    {
                        _speakerPendingPacket = null;
                        _speakerPendingOffset = 0;
                    }

                    return available;
                }
            }

            private void ResetMicrophoneRenderCore()
            {
                try
                {
                    _microphoneRenderOutput?.Stop();
                }
                catch
                {
                    // Best effort during endpoint reset.
                }

                _microphoneRenderOutput?.Dispose();
                _microphoneRenderDevice?.Dispose();
                _microphoneRenderOutput = null;
                _microphoneSourceBuffer = null;
                _microphoneRenderDevice = null;
                _microphoneRenderFormat = null;
            }

            private void LogMicrophoneOpenFailure(Exception ex)
            {
                var logMessage = $"wasapi-endpoint-open-failed role=microphone-render exception={ex.GetType().Name} message={LogValue(ex.Message)}";
                var now = DateTimeOffset.UtcNow;
                if (string.Equals(logMessage, _lastMicrophoneOpenFailureLogMessage, StringComparison.Ordinal)
                    && now - _lastMicrophoneOpenFailureLoggedAt < TimeSpan.FromSeconds(5))
                {
                    return;
                }

                _lastMicrophoneOpenFailureLogMessage = logMessage;
                _lastMicrophoneOpenFailureLoggedAt = now;
                Log("AUDIO", logMessage);
            }

            private void ResetSpeakerCaptureCore()
            {
                if (_speakerCapture is not null)
                {
                    _speakerCapture.DataAvailable -= OnSpeakerDataAvailable;
                    _speakerCapture.RecordingStopped -= OnSpeakerRecordingStopped;
                    try
                    {
                        _speakerCapture.StopRecording();
                    }
                    catch
                    {
                        // Best effort during endpoint reset.
                    }

                    _speakerCapture.Dispose();
                }

                _speakerCaptureDevice?.Dispose();
                _speakerCapture = null;
                _speakerCaptureDevice = null;
                _speakerCaptureFormat = null;
                lock (_speakerPacketLock)
                {
                    while (_speakerPackets.TryDequeue(out _))
                    {
                    }

                    _queuedSpeakerBytes = 0;
                    _speakerPendingPacket = null;
                    _speakerPendingOffset = 0;
                }
            }

            private void ThrowIfDisposed()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            private static string? NormalizeEndpointId(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                var endpointId = value.Trim();
                const string deviceInterfaceMarker = "MMDEVAPI#";
                var markerIndex = endpointId.IndexOf(deviceInterfaceMarker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    return endpointId;
                }

                var compactStart = markerIndex + deviceInterfaceMarker.Length;
                if (compactStart >= endpointId.Length)
                {
                    return endpointId;
                }

                var compactEnd = endpointId.IndexOf('#', compactStart);
                var compactEndpointId = compactEnd < 0
                    ? endpointId[compactStart..]
                    : endpointId[compactStart..compactEnd];
                return string.IsNullOrWhiteSpace(compactEndpointId) ? endpointId : compactEndpointId;
            }

            private static string FormatWaveFormat(WaveFormat format)
            {
                return $"{format.Encoding} {format.SampleRate} Hz / {format.BitsPerSample}-bit / {format.Channels}ch";
            }

        }

        private sealed class AudioMicSharedRing : IDisposable
        {
            private const string MapName = @"Global\SideDockAudioMicBuffer";
            private const int MagicValue = 0x4D414453;
            private const int Version = 1;
            private const int HeaderBytes = 64;
            private const int BufferBytes = AudioDefaults.SampleRate * AudioDefaults.MicFrameBytes * 4;
            private const int TotalBytes = HeaderBytes + BufferBytes;
            private const int OffsetMagic = 0;
            private const int OffsetVersion = 4;
            private const int OffsetHeaderBytes = 8;
            private const int OffsetBufferBytes = 12;
            private const int OffsetSampleRate = 16;
            private const int OffsetChannels = 20;
            private const int OffsetBitsPerSample = 22;
            private const int OffsetFlags = 24;
            private const int OffsetReserved0 = 28;
            private const int OffsetWritePosition = 32;
            private const int OffsetLastWriteTimestampMs = 40;

            private readonly object _lock = new();
            private MemoryMappedFile? _mapping;
            private MemoryMappedViewAccessor? _accessor;
            private long _writePosition;
            private string _statusMessage = "SideDock Microphone 驱动未就绪。";

            public bool IsReady
            {
                get
                {
                    lock (_lock)
                    {
                        return _accessor is not null;
                    }
                }
            }

            public string StatusMessage
            {
                get
                {
                    lock (_lock)
                    {
                        return _statusMessage;
                    }
                }
            }

            public bool EnsureReady(out string message)
            {
                lock (_lock)
                {
                    if (_accessor is not null && ValidateHeader(_accessor))
                    {
                        message = _statusMessage;
                        return true;
                    }

                    DisposeCore();
                    try
                    {
                        if (!OperatingSystem.IsWindows())
                        {
                            _statusMessage = "SideDock Microphone 仅支持 Windows Host。";
                            message = _statusMessage;
                            return false;
                        }

                        _mapping = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
                        _accessor = _mapping.CreateViewAccessor(0, TotalBytes, MemoryMappedFileAccess.ReadWrite);
                        if (!ValidateHeader(_accessor))
                        {
                            InitializeHeader(_accessor);
                        }

                        _writePosition = _accessor.ReadInt64(OffsetWritePosition);
                        _statusMessage = "Windows SideDock Microphone 端点已就绪。";
                        message = _statusMessage;
                        return true;
                    }
                    catch (FileNotFoundException)
                    {
                        _statusMessage = "SideDock Microphone 驱动未就绪，请安装或重启 Windows 麦克风驱动。";
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _statusMessage = $"无法访问 SideDock Microphone 驱动缓冲区：{ex.Message}";
                    }
                    catch (IOException ex)
                    {
                        _statusMessage = $"SideDock Microphone 驱动缓冲区不可用：{ex.Message}";
                    }

                    message = _statusMessage;
                    return false;
                }
            }

            public bool Write(byte[] buffer, int byteCount, long timestampMs, out string message)
            {
                if (buffer is null || byteCount <= 0)
                {
                    message = StatusMessage;
                    return IsReady;
                }

                lock (_lock)
                {
                    if ((_accessor is null || !ValidateHeader(_accessor)) && !EnsureReady(out message))
                    {
                        return false;
                    }

                    if (_accessor is null)
                    {
                        message = _statusMessage;
                        return false;
                    }

                    var boundedByteCount = Math.Min(byteCount, BufferBytes);
                    var sourceOffset = byteCount - boundedByteCount;
                    var ringOffset = (int)(_writePosition % BufferBytes);
                    var firstChunk = Math.Min(boundedByteCount, BufferBytes - ringOffset);
                    _accessor.WriteArray(HeaderBytes + ringOffset, buffer, sourceOffset, firstChunk);

                    var remaining = boundedByteCount - firstChunk;
                    if (remaining > 0)
                    {
                        _accessor.WriteArray(HeaderBytes, buffer, sourceOffset + firstChunk, remaining);
                    }

                    var nextWritePosition = _writePosition + boundedByteCount;
                    Thread.MemoryBarrier();
                    _accessor.Write(OffsetLastWriteTimestampMs, timestampMs);
                    _accessor.Write(OffsetWritePosition, nextWritePosition);
                    _writePosition = nextWritePosition;

                    _statusMessage = "Windows SideDock Microphone 端点已就绪。";
                    message = _statusMessage;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    DisposeCore();
                }
            }

            private static bool ValidateHeader(MemoryMappedViewAccessor accessor)
            {
                return accessor.ReadInt32(OffsetMagic) == MagicValue
                    && accessor.ReadInt32(OffsetVersion) == Version
                    && accessor.ReadInt32(OffsetHeaderBytes) == HeaderBytes
                    && accessor.ReadInt32(OffsetBufferBytes) == BufferBytes
                    && accessor.ReadInt32(OffsetSampleRate) == AudioDefaults.SampleRate
                    && accessor.ReadInt16(OffsetChannels) == AudioDefaults.MicChannels
                    && accessor.ReadInt16(OffsetBitsPerSample) == AudioDefaults.BitsPerSample;
            }

            private static void InitializeHeader(MemoryMappedViewAccessor accessor)
            {
                accessor.Write(OffsetMagic, MagicValue);
                accessor.Write(OffsetVersion, Version);
                accessor.Write(OffsetHeaderBytes, HeaderBytes);
                accessor.Write(OffsetBufferBytes, BufferBytes);
                accessor.Write(OffsetSampleRate, AudioDefaults.SampleRate);
                accessor.Write(OffsetChannels, (short)AudioDefaults.MicChannels);
                accessor.Write(OffsetBitsPerSample, AudioDefaults.BitsPerSample);
                accessor.Write(OffsetFlags, 0);
                accessor.Write(OffsetReserved0, 0);
                accessor.Write(OffsetWritePosition, 0L);
                accessor.Write(OffsetLastWriteTimestampMs, 0L);

                var zeroChunk = new byte[8192];
                var remaining = BufferBytes;
                var offset = HeaderBytes;
                while (remaining > 0)
                {
                    var chunk = Math.Min(remaining, zeroChunk.Length);
                    accessor.WriteArray(offset, zeroChunk, 0, chunk);
                    offset += chunk;
                    remaining -= chunk;
                }
            }

            private void DisposeCore()
            {
                _accessor?.Dispose();
                _mapping?.Dispose();
                _accessor = null;
                _mapping = null;
            }
        }

        private sealed class AudioSpeakerSharedRing : IDisposable
        {
            private const string MapName = @"Global\SideDockAudioSpeakerBuffer";
            private const int MagicValue = 0x53414453;
            private const int Version = 1;
            private const int HeaderBytes = 64;
            private const int BufferBytes = AudioDefaults.SampleRate * AudioDefaults.SpeakerFrameBytes * 4;
            private const int TotalBytes = HeaderBytes + BufferBytes;
            private const int OffsetMagic = 0;
            private const int OffsetVersion = 4;
            private const int OffsetHeaderBytes = 8;
            private const int OffsetBufferBytes = 12;
            private const int OffsetSampleRate = 16;
            private const int OffsetChannels = 20;
            private const int OffsetBitsPerSample = 22;
            private const int OffsetWritePosition = 32;

            private readonly object _lock = new();
            private MemoryMappedFile? _mapping;
            private MemoryMappedViewAccessor? _accessor;
            private string _statusMessage = "SideDock Speaker 驱动未就绪。";

            public bool IsReady
            {
                get
                {
                    lock (_lock)
                    {
                        return _accessor is not null;
                    }
                }
            }

            public string StatusMessage
            {
                get
                {
                    lock (_lock)
                    {
                        return _statusMessage;
                    }
                }
            }

            public bool EnsureReady(out string message)
            {
                lock (_lock)
                {
                    if (_accessor is not null && ValidateHeader(_accessor))
                    {
                        message = _statusMessage;
                        return true;
                    }

                    DisposeCore();
                    try
                    {
                        if (!OperatingSystem.IsWindows())
                        {
                            _statusMessage = "SideDock Speaker 仅支持 Windows Host。";
                            message = _statusMessage;
                            return false;
                        }

                        _mapping = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
                        _accessor = _mapping.CreateViewAccessor(0, TotalBytes, MemoryMappedFileAccess.ReadWrite);
                        if (!ValidateHeader(_accessor))
                        {
                            _statusMessage = "SideDock Speaker 驱动缓冲区格式不匹配。";
                            DisposeCore();
                            message = _statusMessage;
                            return false;
                        }

                        _statusMessage = "Windows SideDock Speaker 端点已就绪。";
                        message = _statusMessage;
                        return true;
                    }
                    catch (FileNotFoundException)
                    {
                        _statusMessage = "SideDock Speaker 驱动未就绪，请安装或重启 Windows 音频驱动。";
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _statusMessage = $"无法访问 SideDock Speaker 驱动缓冲区：{ex.Message}";
                    }
                    catch (IOException ex)
                    {
                        _statusMessage = $"SideDock Speaker 驱动缓冲区不可用：{ex.Message}";
                    }

                    message = _statusMessage;
                    return false;
                }
            }

            public bool TryRead(byte[] destination, int maxByteCount, ref long readPosition, out int bytesRead, out string message)
            {
                bytesRead = 0;
                if (destination is null || maxByteCount <= 0)
                {
                    message = StatusMessage;
                    return IsReady;
                }

                lock (_lock)
                {
                    if ((_accessor is null || !ValidateHeader(_accessor)) && !EnsureReady(out message))
                    {
                        return false;
                    }

                    if (_accessor is null)
                    {
                        message = _statusMessage;
                        return false;
                    }

                    var writePosition = _accessor.ReadInt64(OffsetWritePosition);
                    if (readPosition == 0 || readPosition > writePosition)
                    {
                        readPosition = writePosition;
                    }

                    if (writePosition - readPosition > BufferBytes)
                    {
                        readPosition = writePosition - BufferBytes;
                    }

                    var available = (int)Math.Min(writePosition - readPosition, Math.Min(maxByteCount, destination.Length));
                    available -= available % AudioDefaults.SpeakerFrameBytes;
                    if (available <= 0)
                    {
                        message = _statusMessage;
                        return true;
                    }

                    var ringOffset = (int)(readPosition % BufferBytes);
                    var firstChunk = Math.Min(available, BufferBytes - ringOffset);
                    _accessor.ReadArray(HeaderBytes + ringOffset, destination, 0, firstChunk);

                    var remaining = available - firstChunk;
                    if (remaining > 0)
                    {
                        _accessor.ReadArray(HeaderBytes, destination, firstChunk, remaining);
                    }

                    readPosition += available;
                    bytesRead = available;
                    _statusMessage = "Windows SideDock Speaker 端点已就绪。";
                    message = _statusMessage;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    DisposeCore();
                }
            }

            private static bool ValidateHeader(MemoryMappedViewAccessor accessor)
            {
                return accessor.ReadInt32(OffsetMagic) == MagicValue
                    && accessor.ReadInt32(OffsetVersion) == Version
                    && accessor.ReadInt32(OffsetHeaderBytes) == HeaderBytes
                    && accessor.ReadInt32(OffsetBufferBytes) == BufferBytes
                    && accessor.ReadInt32(OffsetSampleRate) == AudioDefaults.SampleRate
                    && accessor.ReadInt16(OffsetChannels) == AudioDefaults.SpeakerChannels
                    && accessor.ReadInt16(OffsetBitsPerSample) == AudioDefaults.BitsPerSample;
            }

            private void DisposeCore()
            {
                _accessor?.Dispose();
                _mapping?.Dispose();
                _accessor = null;
                _mapping = null;
            }
        }

        private static async Task WaitForPreviousConnectionAsync(Task? previousConnectionTask, CancellationToken cancellationToken)
        {
            if (previousConnectionTask is null)
            {
                return;
            }

            try
            {
                await previousConnectionTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                Log("AUDIO", "旧音频连接尚未完全退出，新连接继续接管。");
            }
            catch (Exception ex)
            {
                Log("AUDIO", $"旧音频连接退出异常，新连接继续接管。 exception={ex.GetType().Name} message={LogValue(ex.Message)}");
            }
        }

        private static async Task CancelPreviousConnectionAsync(CancellationTokenSource previousConnectionCts)
        {
            try
            {
                await previousConnectionCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                Log("AUDIO", "旧音频连接已释放，新连接继续接管。");
            }
        }

        private static async Task WaitUntilCanceledAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Log("AUDIO", "正在关闭。");
            }
        }
    }
}
