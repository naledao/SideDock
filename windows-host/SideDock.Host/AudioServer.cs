using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

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
        private readonly AudioMicSharedRing _micRing = new();
        private readonly AudioSpeakerSharedRing _speakerRing = new();
        private CancellationTokenSource? _activeConnectionCts;
        private Task? _activeConnectionTask;
        private int _connectionSerial;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            if (!options.AudioDeviceEnabled || (!options.MicrophoneEnabled && !options.SpeakerEnabled))
            {
                Log("AUDIO", "mic-state=disabled speaker-state=disabled reason=disabled_by_options");
                await PublishMicStatusAsync("disabled", "SideDock 麦克风已关闭。", cancellationToken);
                await PublishSpeakerStatusAsync("disabled", "SideDock 音响已关闭。", cancellationToken);
                await WaitUntilCanceledAsync(cancellationToken);
                return;
            }

            try
            {
                _listener.Start();
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
                        Log("AUDIO", "检测到新的音频连接，关闭旧连接。");
                        await previousConnectionCts.CancelAsync();
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
                await PublishMicStatusAsync("unavailable", $"SideDock 麦克风监听失败：{ex.Message}", CancellationToken.None);
                await PublishSpeakerStatusAsync("unavailable", $"SideDock 音响监听失败：{ex.Message}", CancellationToken.None);
            }
            finally
            {
                _listener.Stop();
                _micRing.Dispose();
                _speakerRing.Dispose();
            }
        }

        private async Task PublishInitialStatusAsync(CancellationToken cancellationToken)
        {
            if (options.MicrophoneEnabled)
            {
                var micReady = _micRing.EnsureReady(out var micMessage);
                Log("AUDIO", $"mic-state={(micReady ? "available" : "unavailable")} port={options.AudioPort} format=pcm_s16le/{AudioDefaults.SampleRate}/mono system-endpoint={(micReady ? "ready" : "missing")}");
                await PublishMicStatusAsync(
                    micReady ? "available" : "unavailable",
                    micReady ? "SideDock 麦克风可在 Windows 或应用中选择。" : micMessage,
                    cancellationToken);
            }
            else
            {
                Log("AUDIO", "mic-state=disabled reason=disabled_by_options");
                await PublishMicStatusAsync("disabled", "SideDock 麦克风已关闭。", cancellationToken);
            }

            if (options.SpeakerEnabled)
            {
                var speakerReady = _speakerRing.EnsureReady(out var speakerMessage);
                Log("AUDIO", $"speaker-state={(speakerReady ? "available" : "unavailable")} port={options.AudioPort} format=pcm_s16le/{AudioDefaults.SampleRate}/stereo system-endpoint={(speakerReady ? "ready" : "missing")}");
                await PublishSpeakerStatusAsync(
                    speakerReady ? "available" : "unavailable",
                    speakerReady ? "SideDock 音响可在 Windows 或应用中选择。" : speakerMessage,
                    cancellationToken);
            }
            else
            {
                Log("AUDIO", "speaker-state=disabled reason=disabled_by_options");
                await PublishSpeakerStatusAsync("disabled", "SideDock 音响已关闭。", cancellationToken);
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
                Log($"AUDIO {connectionId}", $"音频通道已连接: {remote}");

                try
                {
                    client.NoDelay = true;
                    await using var stream = client.GetStream();
                    var tasks = new List<Task>(2);
                    if (options.MicrophoneEnabled)
                    {
                        tasks.Add(ReceiveMicrophoneAsync(connectionId, stream, connectionCts.Token));
                    }

                    if (options.SpeakerEnabled)
                    {
                        tasks.Add(SendSpeakerAsync(connectionId, stream, connectionCts.Token));
                    }

                    if (tasks.Count == 0)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, connectionCts.Token);
                    }
                    else
                    {
                        var completed = await Task.WhenAny(tasks);
                        await completed;
                    }
                }
                catch (OperationCanceledException) when (appToken.IsCancellationRequested || connectionCts.IsCancellationRequested)
                {
                    // Application shutdown or superseded connection.
                }
                catch (EndOfStreamException)
                {
                    Log($"AUDIO {connectionId}", "音频通道已由 Android 关闭。");
                }
                catch (IOException ex)
                {
                    Log($"AUDIO {connectionId}", $"audio-state=unavailable reason=io message={ex.Message}");
                }
                catch (Exception ex)
                {
                    Log($"AUDIO {connectionId}", $"audio-state=unavailable reason=exception message={ex.Message}");
                    await PublishMicStatusAsync("unavailable", $"SideDock 麦克风暂不可用：{ex.Message}", CancellationToken.None);
                    await PublishSpeakerStatusAsync("unavailable", $"SideDock 音响暂不可用：{ex.Message}", CancellationToken.None);
                }
                finally
                {
                    await connectionCts.CancelAsync();
                    lock (_connectionLock)
                    {
                        if (ReferenceEquals(_activeConnectionCts, connectionCts))
                        {
                            _activeConnectionCts = null;
                            _activeConnectionTask = null;
                        }
                    }

                    Log($"AUDIO {connectionId}", "音频通道已断开");
                    await PublishInitialStatusAsync(CancellationToken.None);
                }
            }
        }

        private async Task ReceiveMicrophoneAsync(int connectionId, Stream stream, CancellationToken cancellationToken)
        {
            var endpointReady = _micRing.EnsureReady(out var endpointMessage);
            await PublishMicStatusAsync(
                endpointReady ? "available" : "unavailable",
                endpointReady ? "等待 Android 麦克风采集。" : endpointMessage,
                cancellationToken);

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
                    endpointReady = _micRing.Write(payload, payloadLength, timestampMs, out endpointMessage);
                    packetCount += 1;
                    byteCount += payloadLength;

                    var now = DateTimeOffset.UtcNow;
                    if (packetCount == 1 || now - lastStatsAt >= TimeSpan.FromSeconds(1))
                    {
                        var sourceAgeMs = Math.Max(0, now.ToUnixTimeMilliseconds() - timestampMs);
                        Log(
                            "AUDIO",
                            $"mic-state={(endpointReady ? "capturing" : "unavailable")} packets={packetCount} bytes={byteCount} lastSeq={sequence} sourceAgeMs={sourceAgeMs} system-endpoint={(endpointReady ? "ready" : "missing")}");
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
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Log($"AUDIO {connectionId}", $"mic-state=unavailable reason={ex.GetType().Name} message={ex.Message}");
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
            long readPosition = 0;
            var lastStatsAt = DateTimeOffset.MinValue;
            var lastUnavailableAt = DateTimeOffset.MinValue;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!_speakerRing.TryRead(payload, SpeakerChunkBytes, ref readPosition, out var bytesRead, out var message))
                    {
                        var unavailableNow = DateTimeOffset.UtcNow;
                        if (unavailableNow - lastUnavailableAt >= TimeSpan.FromSeconds(1))
                        {
                            lastUnavailableAt = unavailableNow;
                            Log("AUDIO", $"speaker-state=unavailable system-endpoint=missing message={message}");
                            await PublishSpeakerStatusAsync("unavailable", message, cancellationToken, packetCount, byteCount);
                        }

                        await Task.Delay(250, cancellationToken);
                        continue;
                    }

                    if (bytesRead <= 0)
                    {
                        if (packetCount == 0 && lastStatsAt == DateTimeOffset.MinValue)
                        {
                            lastStatsAt = DateTimeOffset.UtcNow;
                            Log("AUDIO", "speaker-state=available system-endpoint=ready");
                            await PublishSpeakerStatusAsync("available", "等待 Windows 播放到 SideDock 音响。", cancellationToken);
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
                    if (packetCount == 1 || now - lastStatsAt >= TimeSpan.FromSeconds(1))
                    {
                        Log("AUDIO", $"speaker-state=playing packets={packetCount} bytes={byteCount} system-endpoint=ready");
                        await PublishSpeakerStatusAsync(
                            "playing",
                            "SideDock 音响正在播放。",
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
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Log($"AUDIO {connectionId}", $"speaker-state=unavailable reason={ex.GetType().Name} message={ex.Message}");
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(header);
                ArrayPool<byte>.Shared.Return(payload);
            }
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
                ["systemEndpoint"] = _micRing.IsReady,
                ["systemEndpointMessage"] = _micRing.StatusMessage
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
                ["systemEndpoint"] = _speakerRing.IsReady,
                ["systemEndpointMessage"] = _speakerRing.StatusMessage
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
