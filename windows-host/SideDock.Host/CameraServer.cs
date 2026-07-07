using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace SideDock.Host;

#pragma warning disable CA1416

internal static partial class Program
{
    private sealed class CameraServer(
        IPAddress address,
        HostOptions options,
        ControlMessagePublisher controlPublisher,
        CameraRuntimeState cameraRuntimeState)
    {
        private const int HeaderSize = 40;
        private const int Version = 1;
        private const int MaxPayloadBytes = 8 * 1024 * 1024;
        private const int FlagKeyFrame = 1;
        private const int FlagCodecConfig = 2;
        private static readonly byte[] Magic = "SDCM"u8.ToArray();

        private readonly TcpListener _listener = new(address, options.CameraPort);
        private readonly CameraLatestFrameCache _latestFrameCache = new(cameraRuntimeState.Current.Width, cameraRuntimeState.Current.Height);
        private readonly object _connectionLock = new();
        private readonly object _stabilityLock = new();
        private CancellationTokenSource? _activeConnectionCts;
        private Task? _activeConnectionTask;
        private int _connectionSerial;
        private long _reconnectCount;
        private long _recoveryAttemptCount;
        private long _consecutiveFailureCount;
        private long _lastRecoveryDurationMs;
        private DateTimeOffset? _recoveryStartedAt;
        private string _lastDisconnectReason = "";

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            if (!options.CameraEnabled)
            {
                Log("CAMERA", "camera-state=disabled reason=disabled_by_options");
                await PublishServerStatusAsync("disabled", "camera disabled by host options", cancellationToken);
                await WaitUntilCanceledAsync(cancellationToken);
                return;
            }

            try
            {
                _listener.Start();
                Log(
                    "CAMERA",
                    $"camera-state=listening address={address} port={options.CameraPort} "
                    + $"config={FormatCameraConfig(cameraRuntimeState.Current.WithEffectiveEnabled(options))}");
                await PublishServerStatusAsync("listening", "waiting for Android camera stream", cancellationToken);

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
                        Log("CAMERA", "检测到新的摄像头连接，关闭旧连接。");
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
                Log("CAMERA", "正在关闭。");
            }
            catch (SocketException ex)
            {
                Log("CAMERA", $"camera-state=unavailable reason=listen_failed message={ex.Message}");
                await PublishServerStatusAsync("unavailable", ex.Message, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log("CAMERA", $"camera-state=unavailable reason=run_failed exception={ex.GetType().Name} message={ex.Message}");
                await PublishServerStatusAsync("unavailable", ex.Message, CancellationToken.None);
            }
            finally
            {
                _latestFrameCache.Dispose();
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
                var remote = SafeEndpoint(client, remote: true);
                var local = SafeEndpoint(client, remote: false);
                Log(
                    $"CAMERA {connectionId}",
                    $"camera-state=connected remote={remote} local={local} socket={DescribeSocket(client)}");
                var recoverySuccess = MarkRecoverySucceeded();
                if (!string.IsNullOrWhiteSpace(recoverySuccess))
                {
                    Log("CAMERA", recoverySuccess);
                }

                await PublishServerStatusAsync("connected", "Android camera socket connected", connectionCts.Token);

                var disconnectReason = "socket_closed";
                try
                {
                    client.NoDelay = true;
                    client.ReceiveBufferSize = 1024 * 1024;
                    await ReceivePacketsAsync(connectionId, client.GetStream(), connectionCts.Token);
                }
                catch (OperationCanceledException) when (appToken.IsCancellationRequested || connectionCts.IsCancellationRequested)
                {
                    disconnectReason = appToken.IsCancellationRequested ? "host_stopping" : "superseded_connection";
                }
                catch (EndOfStreamException ex)
                {
                    disconnectReason = "eof: " + ex.Message;
                    Log($"CAMERA {connectionId}", $"camera-state=disconnected endType=EOF message={ex.Message}");
                }
                catch (IOException ex)
                {
                    disconnectReason = "socket_disconnected: " + ex.Message;
                    Log($"CAMERA {connectionId}", $"camera-state=disconnected endType=IOException exception={ex.GetType().Name} message={ex.Message}");
                }
                catch (InvalidDataException ex)
                {
                    disconnectReason = "invalid_stream: " + ex.Message;
                    Log($"CAMERA {connectionId}", $"camera-state=unavailable endType=InvalidDataException exception={ex.GetType().Name} message={ex.Message}");
                    await PublishServerStatusAsync("unavailable", ex.Message, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    disconnectReason = "receive_error: " + ex.Message;
                    Log($"CAMERA {connectionId}", $"camera-state=unavailable endType=Exception exception={ex.GetType().Name} message={ex.Message}");
                    await PublishServerStatusAsync("unavailable", ex.Message, CancellationToken.None);
                }
                finally
                {
                    var wasActiveConnection = false;
                    lock (_connectionLock)
                    {
                        if (ReferenceEquals(_activeConnectionCts, connectionCts))
                        {
                            wasActiveConnection = true;
                            _activeConnectionCts = null;
                            _activeConnectionTask = null;
                        }
                    }

                    CloseSocket(client);
                    Log($"CAMERA {connectionId}", $"camera-state=disconnected remote={remote}");
                    if (wasActiveConnection && !appToken.IsCancellationRequested)
                    {
                        var recoveryStart = MarkRecoveryStarted(disconnectReason);
                        Log("CAMERA", recoveryStart);
                        await PublishServerStatusAsync("disconnected", disconnectReason, CancellationToken.None);
                        Log("CAMERA", $"camera-state=listening reason=awaiting_reconnect {StabilityLogFields()}");
                        await PublishServerStatusAsync("listening", "waiting for Android camera stream after disconnect", CancellationToken.None);
                    }
                }
            }
        }

        private async Task ReceivePacketsAsync(int connectionId, NetworkStream stream, CancellationToken cancellationToken)
        {
            var header = ArrayPool<byte>.Shared.Rent(HeaderSize);
            var stats = new CameraReceiveStats();
            var lastLogAt = DateTimeOffset.UtcNow;
            long lastLogPackets = 0;
            long lastLogFrames = 0;
            long lastLogBytes = 0;
            double lastApproxFps = 0.0;
            double lastApproxKbps = 0.0;
            byte[]? codecConfig = null;
            byte[]? payloadBuffer = null;
            var needsKeyFrame = true;
            var debugDumpPath = Environment.GetEnvironmentVariable("SIDEDOCK_CAMERA_DUMP");

            using var decoder = CreateDecoder(connectionId);
            await using var debugDump = OpenCameraDebugDump(debugDumpPath);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header.AsMemory(0, HeaderSize), cancellationToken);
                    var packet = ParseHeader(header.AsSpan(0, HeaderSize));
                    payloadBuffer = EnsurePayloadBuffer(payloadBuffer, packet.PayloadLength);
                    await stream.ReadExactlyAsync(payloadBuffer.AsMemory(0, packet.PayloadLength), cancellationToken);
                    stats.Record(packet);
                    if (debugDump is not null)
                    {
                        await debugDump.WriteAsync(payloadBuffer.AsMemory(0, packet.PayloadLength), cancellationToken);
                        if (stats.PacketCount <= 8)
                        {
                            Log(
                                $"CAMERA {connectionId}",
                                $"debug-dump packet={stats.PacketCount} seq={packet.Sequence} flags={FormatFlags(packet.Flags)} "
                                + $"payload={packet.PayloadLength} nalTypes={FormatH264NalTypes(payloadBuffer, packet.PayloadLength)}");
                        }
                    }

                    var containsPicture = H264PayloadContainsPicture(payloadBuffer, packet.PayloadLength);
                    var isCodecConfigPacket = (packet.Flags & FlagCodecConfig) != 0;
                    var isKeyFramePacket = (packet.Flags & FlagKeyFrame) != 0 || H264PayloadContainsNalType(payloadBuffer, packet.PayloadLength, 5);

                    if (isCodecConfigPacket && !containsPicture)
                    {
                        codecConfig = payloadBuffer.AsSpan(0, packet.PayloadLength).ToArray();
                        needsKeyFrame = true;
                        needsKeyFrame = DecodePacket(decoder, stats, payloadBuffer, packet.PayloadLength, packet, isCodecConfig: true, needsKeyFrame, codecConfig);
                    }
                    else if (isKeyFramePacket)
                    {
                        needsKeyFrame = DecodePacket(decoder, stats, payloadBuffer, packet.PayloadLength, packet, isCodecConfig: false, needsKeyFrame, codecConfig);
                    }
                    else if (!needsKeyFrame)
                    {
                        needsKeyFrame = DecodePacket(decoder, stats, payloadBuffer, packet.PayloadLength, packet, isCodecConfig: false, needsKeyFrame, codecConfig);
                    }
                    else
                    {
                        stats.RecordDecodeWait("waiting for keyframe after stream start or decoder reset");
                    }

                    var now = DateTimeOffset.UtcNow;
                    if (stats.PacketCount == 1 || now - lastLogAt >= TimeSpan.FromSeconds(1))
                    {
                        var elapsedSeconds = Math.Max(0.001, (now - lastLogAt).TotalSeconds);
                        var packetDelta = stats.PacketCount - lastLogPackets;
                        var frameDelta = stats.FrameCount - lastLogFrames;
                        var byteDelta = stats.ByteCount - lastLogBytes;
                        var fps = frameDelta / elapsedSeconds;
                        var kbps = byteDelta * 8.0 / 1000.0 / elapsedSeconds;
                        var fpsJitter = JitterPercent(lastApproxFps, fps);
                        var bitrateJitter = JitterPercent(lastApproxKbps, kbps);

                        Log(
                            "CAMERA",
                            $"camera-state=receiving connection={connectionId} packets={stats.PacketCount} frames={stats.FrameCount} bytes={stats.ByteCount} "
                            + $"deltaPackets={packetDelta} approxFps={fps:F1} approxKbps={kbps:F0} "
                            + $"fpsJitter={fpsJitter:F1} bitrateJitter={bitrateJitter:F1} {StabilityLogFields()} "
                            + $"lastSeq={packet.Sequence} flags={FormatFlags(packet.Flags)} payload={packet.PayloadLength} "
                            + $"keyFrames={stats.KeyFrameCount} codecConfigPackets={stats.CodecConfigPacketCount} "
                            + $"decodedFrames={stats.DecodedFrameCount} decodeErrors={stats.DecodeErrorCount} "
                            + $"previewSeq={_latestFrameCache.PublishedFrameSequence} decodeLagMs={stats.LastDecodeLagMs:F1} "
                            + $"lastFrameAt={FormatTimestamp(stats.LastFrameAt)} lastDecodedFrameAt={FormatTimestamp(stats.LastDecodedFrameAt)}"
                            + (string.IsNullOrWhiteSpace(stats.LastDecodeError) ? string.Empty : $" lastError={stats.LastDecodeError}"));
                        await PublishServerStatusAsync(
                            "receiving",
                            $"packets={stats.PacketCount} fps={fps:F1} kbps={kbps:F0}",
                            cancellationToken,
                            stats,
                            fps,
                            kbps,
                            fpsJitter,
                            bitrateJitter);

                        lastLogAt = now;
                        lastLogPackets = stats.PacketCount;
                        lastLogFrames = stats.FrameCount;
                        lastLogBytes = stats.ByteCount;
                        lastApproxFps = fps;
                        lastApproxKbps = kbps;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(header);
                if (payloadBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(payloadBuffer);
                }
            }
        }

        private static FileStream? OpenCameraDebugDump(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                Log("CAMERA", $"camera-debug-dump path={fullPath}");
                return new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Log("CAMERA", $"camera-debug-dump unavailable: {ex.Message}");
                return null;
            }
        }

        private static CameraPacketHeader ParseHeader(ReadOnlySpan<byte> header)
        {
            if (!header[..4].SequenceEqual(Magic))
            {
                throw new InvalidDataException("Invalid camera packet magic.");
            }

            var version = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
            if (version != Version)
            {
                throw new InvalidDataException($"Unsupported camera packet version: {version}.");
            }

            var headerSize = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
            if (headerSize != HeaderSize)
            {
                throw new InvalidDataException($"Unsupported camera packet header size: {headerSize}.");
            }

            var flags = BinaryPrimitives.ReadInt32LittleEndian(header[12..16]);
            var sequence = BinaryPrimitives.ReadInt64LittleEndian(header[16..24]);
            var timestampUs = BinaryPrimitives.ReadInt64LittleEndian(header[24..32]);
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header[32..36]);
            if (payloadLength <= 0 || payloadLength > MaxPayloadBytes)
            {
                throw new InvalidDataException($"Invalid camera payload length: {payloadLength}.");
            }

            return new CameraPacketHeader(flags, sequence, timestampUs, payloadLength);
        }

        private static byte[] EnsurePayloadBuffer(byte[]? buffer, int payloadLength)
        {
            if (buffer is not null && buffer.Length >= payloadLength)
            {
                return buffer;
            }

            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return ArrayPool<byte>.Shared.Rent(payloadLength);
        }

        private CameraH264Decoder? CreateDecoder(int connectionId)
        {
            if (!OperatingSystem.IsWindows())
            {
                Log("CAMERA", "camera-decode-state=disabled reason=media_foundation_requires_windows");
                return null;
            }

            try
            {
                var config = cameraRuntimeState.Current;
                var decoder = new CameraH264Decoder(config.Width, config.Height, config.Fps);
                decoder.Start();
                Log(
                    "CAMERA",
                    $"camera-decode-state=ready connection={connectionId} decoder={decoder.SelectedMftName} "
                    + $"config={config.Width}x{config.Height}@{config.Fps} codec={config.Codec} facing={config.Facing}");
                return decoder;
            }
            catch (Exception ex)
            {
                Log("CAMERA", $"camera-decode-state=unavailable connection={connectionId} message={ex.Message}");
                return null;
            }
        }

        private bool DecodePacket(
            CameraH264Decoder? decoder,
            CameraReceiveStats stats,
            byte[] payloadBuffer,
            int payloadLength,
            CameraPacketHeader packet,
            bool isCodecConfig,
            bool needsKeyFrame,
            byte[]? codecConfig)
        {
            if (decoder is null)
            {
                stats.RecordDecodeWait("decoder unavailable");
                return needsKeyFrame;
            }

            try
            {
                if (!isCodecConfig && needsKeyFrame && codecConfig is not null && codecConfig.Length > 0)
                {
                    foreach (var configFrame in decoder.Decode(codecConfig, packet.TimestampUs))
                    {
                        PublishDecodedFrame(stats, configFrame, packet.TimestampUs);
                    }
                }

                var payload = payloadBuffer.AsSpan(0, payloadLength);
                foreach (var frame in decoder.Decode(payload, packet.TimestampUs))
                {
                    PublishDecodedFrame(stats, frame, packet.TimestampUs);
                }

                return isCodecConfig;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidDataException)
            {
                stats.RecordDecodeError(ex.Message);
                decoder.Reset();
                return true;
            }
        }

        private void PublishDecodedFrame(CameraReceiveStats stats, CameraDecodedFrame frame, long sourceTimestampUs)
        {
            _latestFrameCache.Write(frame.Bgra, frame.Width, frame.Height, frame.Stride, sourceTimestampUs);
            stats.RecordDecodedFrame();
        }

        private static string FormatFlags(int flags)
        {
            if (flags == 0)
            {
                return "none";
            }

            var parts = new List<string>(2);
            if ((flags & FlagKeyFrame) != 0)
            {
                parts.Add("keyframe");
            }

            if ((flags & FlagCodecConfig) != 0)
            {
                parts.Add("codec-config");
            }

            return parts.Count == 0
                ? $"0x{flags:X}"
                : string.Join("|", parts);
        }

        private static bool H264PayloadContainsPicture(byte[] payload, int payloadLength)
        {
            return H264PayloadContainsPicture(payload.AsSpan(0, payloadLength));
        }

        private static bool H264PayloadContainsNalType(byte[] payload, int payloadLength, int nalType)
        {
            return H264PayloadContainsNalType(payload.AsSpan(0, payloadLength), nalType);
        }

        private static bool H264PayloadContainsPicture(ReadOnlySpan<byte> payload)
        {
            return H264PayloadContainsNalType(payload, 1, 5);
        }

        private static bool H264PayloadContainsNalType(ReadOnlySpan<byte> payload, int nalType)
        {
            return H264PayloadContainsNalType(payload, nalType, nalType);
        }

        private static bool H264PayloadContainsNalType(ReadOnlySpan<byte> payload, int minNalType, int maxNalType)
        {
            if (payload.IsEmpty)
            {
                return false;
            }

            var startCode = FindStartCode(payload, 0);
            if (startCode >= 0)
            {
                while (startCode >= 0)
                {
                    var nalOffset = NalOffsetAfterStartCode(payload, startCode);
                    if (nalOffset < payload.Length)
                    {
                        var nalType = payload[nalOffset] & 0x1F;
                        if (nalType >= minNalType && nalType <= maxNalType)
                        {
                            return true;
                        }
                    }

                    startCode = FindStartCode(payload, Math.Max(nalOffset + 1, startCode + 3));
                }

                return false;
            }

            if (TryLengthPrefixedPayloadContainsNalType(payload, minNalType, maxNalType, out var containsNalType))
            {
                return containsNalType;
            }

            var rawNalType = payload[0] & 0x1F;
            return rawNalType >= minNalType && rawNalType <= maxNalType;
        }

        private static string FormatH264NalTypes(byte[] payload, int payloadLength)
        {
            var types = EnumerateH264NalTypes(payload.AsSpan(0, payloadLength)).Take(16).ToArray();
            return types.Length == 0 ? "none" : string.Join(",", types);
        }

        private static IReadOnlyList<int> EnumerateH264NalTypes(ReadOnlySpan<byte> payload)
        {
            var types = new List<int>();
            if (payload.IsEmpty)
            {
                return types;
            }

            var startCode = FindStartCode(payload, 0);
            if (startCode >= 0)
            {
                while (startCode >= 0)
                {
                    var nalOffset = NalOffsetAfterStartCode(payload, startCode);
                    if (nalOffset < payload.Length)
                    {
                        types.Add(payload[nalOffset] & 0x1F);
                    }

                    startCode = FindStartCode(payload, Math.Max(nalOffset + 1, startCode + 3));
                }

                return types;
            }

            if (TryEnumerateLengthPrefixedNalTypes(payload, out var lengthPrefixedTypes))
            {
                return lengthPrefixedTypes;
            }

            types.Add(payload[0] & 0x1F);
            return types;
        }

        private static bool TryEnumerateLengthPrefixedNalTypes(ReadOnlySpan<byte> payload, out int[] nalTypes)
        {
            var types = new List<int>();
            var offset = 0;
            while (offset + 4 <= payload.Length)
            {
                var nalLength = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(offset, 4));
                offset += 4;
                if (nalLength <= 0 || offset + nalLength > payload.Length)
                {
                    nalTypes = Array.Empty<int>();
                    return false;
                }

                types.Add(payload[offset] & 0x1F);
                offset += nalLength;
            }

            if (offset != payload.Length || types.Count == 0)
            {
                nalTypes = Array.Empty<int>();
                return false;
            }

            nalTypes = types.ToArray();
            return true;
        }

        private static bool TryLengthPrefixedPayloadContainsNalType(
            ReadOnlySpan<byte> payload,
            int minNalType,
            int maxNalType,
            out bool containsNalType)
        {
            containsNalType = false;
            var offset = 0;
            var nalCount = 0;
            while (offset + 4 <= payload.Length)
            {
                var nalLength = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(offset, 4));
                offset += 4;
                if (nalLength <= 0 || offset + nalLength > payload.Length)
                {
                    containsNalType = false;
                    return false;
                }

                nalCount++;
                var nalType = payload[offset] & 0x1F;
                if (nalType >= minNalType && nalType <= maxNalType)
                {
                    containsNalType = true;
                }

                offset += nalLength;
            }

            if (offset != payload.Length || nalCount == 0)
            {
                containsNalType = false;
                return false;
            }

            return true;
        }

        private static int FindStartCode(ReadOnlySpan<byte> payload, int start)
        {
            for (var index = Math.Max(0, start); index + 3 <= payload.Length; index++)
            {
                if (payload[index] != 0 || payload[index + 1] != 0)
                {
                    continue;
                }

                if (payload[index + 2] == 1)
                {
                    return index;
                }

                if (index + 4 <= payload.Length && payload[index + 2] == 0 && payload[index + 3] == 1)
                {
                    return index;
                }
            }

            return -1;
        }

        private static int NalOffsetAfterStartCode(ReadOnlySpan<byte> payload, int startCodeOffset)
        {
            return startCodeOffset + (startCodeOffset + 2 < payload.Length && payload[startCodeOffset + 2] == 1 ? 3 : 4);
        }

        private ValueTask PublishServerStatusAsync(
            string state,
            string message,
            CancellationToken cancellationToken,
            CameraReceiveStats? stats = null,
            double approxFps = 0.0,
            double approxKbps = 0.0,
            double fpsJitter = 0.0,
            double bitrateJitter = 0.0)
        {
            var config = cameraRuntimeState.Current.WithEffectiveEnabled(options);
            var stability = StabilitySnapshot();
            return controlPublisher.PublishAsync("camera_server_status", new JsonObject
            {
                ["state"] = state,
                ["message"] = message,
                ["port"] = config.Port,
                ["width"] = config.Width,
                ["height"] = config.Height,
                ["fps"] = config.Fps,
                ["codec"] = config.Codec,
                ["facing"] = config.Facing,
                ["packets"] = stats?.PacketCount ?? 0,
                ["frames"] = stats?.FrameCount ?? 0,
                ["bytes"] = stats?.ByteCount ?? 0,
                ["keyFrames"] = stats?.KeyFrameCount ?? 0,
                ["codecConfigPackets"] = stats?.CodecConfigPacketCount ?? 0,
                ["decodedFrames"] = stats?.DecodedFrameCount ?? 0,
                ["decodeErrors"] = stats?.DecodeErrorCount ?? 0,
                ["approxFps"] = approxFps,
                ["approxKbps"] = approxKbps,
                ["fpsJitter"] = fpsJitter,
                ["bitrateJitter"] = bitrateJitter,
                ["reconnectCount"] = stability.ReconnectCount,
                ["recoveryAttemptCount"] = stability.RecoveryAttemptCount,
                ["consecutiveFailureCount"] = stability.ConsecutiveFailureCount,
                ["lastRecoveryDurationMs"] = stability.LastRecoveryDurationMs,
                ["lastDisconnectReason"] = stability.LastDisconnectReason,
                ["decodeLagMs"] = stats?.LastDecodeLagMs ?? 0.0,
                ["lastFrameAt"] = FormatTimestamp(stats?.LastFrameAt),
                ["lastDecodedFrameAt"] = FormatTimestamp(stats?.LastDecodedFrameAt),
                ["lastError"] = stats?.LastDecodeError ?? "",
                ["previewFrameSequence"] = _latestFrameCache.PublishedFrameSequence,
                ["previewMapName"] = CameraLatestFrameCache.MapName
            }, cancellationToken);
        }

        private static string FormatCameraConfig(EffectiveCameraRuntimeConfig config)
        {
            return $"{config.Width}x{config.Height}@{config.Fps} codec={config.Codec} facing={config.Facing} enabled={config.Enabled}";
        }

        private string MarkRecoveryStarted(string reason)
        {
            var now = DateTimeOffset.UtcNow;
            lock (_stabilityLock)
            {
                _recoveryAttemptCount++;
                _consecutiveFailureCount++;
                _lastDisconnectReason = string.IsNullOrWhiteSpace(reason) ? "socket_disconnected" : reason;
                _recoveryStartedAt ??= now;
                return $"camera-recovery-event=recovery_start attempts={_recoveryAttemptCount} "
                    + $"consecutiveFailures={_consecutiveFailureCount} lastDisconnectReason={SanitizeLogValue(_lastDisconnectReason)}";
            }
        }

        private string MarkRecoverySucceeded()
        {
            lock (_stabilityLock)
            {
                if (_recoveryStartedAt is null)
                {
                    _consecutiveFailureCount = 0;
                    return string.Empty;
                }

                _reconnectCount++;
                _lastRecoveryDurationMs = (long)Math.Max(
                    0.0,
                    (DateTimeOffset.UtcNow - _recoveryStartedAt.Value).TotalMilliseconds);
                _recoveryStartedAt = null;
                _consecutiveFailureCount = 0;
                return $"camera-recovery-event=recovery_success reconnects={_reconnectCount} "
                    + $"lastRecoveryDurationMs={_lastRecoveryDurationMs} lastDisconnectReason={SanitizeLogValue(_lastDisconnectReason)}";
            }
        }

        private string StabilityLogFields()
        {
            var snapshot = StabilitySnapshot();
            return $"reconnects={snapshot.ReconnectCount} recoveryAttempts={snapshot.RecoveryAttemptCount} "
                + $"consecutiveFailures={snapshot.ConsecutiveFailureCount} lastRecoveryDurationMs={snapshot.LastRecoveryDurationMs} "
                + $"lastDisconnectReason={SanitizeLogValue(snapshot.LastDisconnectReason)}";
        }

        private CameraStabilitySnapshot StabilitySnapshot()
        {
            lock (_stabilityLock)
            {
                return new CameraStabilitySnapshot(
                    _reconnectCount,
                    _recoveryAttemptCount,
                    _consecutiveFailureCount,
                    _lastRecoveryDurationMs,
                    _lastDisconnectReason);
            }
        }

        private static double JitterPercent(double previous, double current)
        {
            return previous <= 0.0 || double.IsNaN(previous) || double.IsInfinity(previous)
                ? 0.0
                : Math.Abs(current - previous) * 100.0 / previous;
        }

        private static string SanitizeLogValue(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? ""
                : value.Replace('\r', ' ').Replace('\n', ' ').Replace(' ', '_');
        }

        private static string FormatTimestamp(DateTimeOffset? value)
        {
            return value is null || value.Value == default
                ? ""
                : value.Value.ToString("O");
        }

        private static string SafeEndpoint(TcpClient client, bool remote)
        {
            try
            {
                return (remote ? client.Client.RemoteEndPoint : client.Client.LocalEndPoint)?.ToString() ?? "unknown";
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return "unknown";
            }
        }

        private static string DescribeSocket(TcpClient client)
        {
            try
            {
                return $"connected={client.Connected} local={client.Client.LocalEndPoint} remote={client.Client.RemoteEndPoint}";
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return $"socket-invalid={ex.Message}";
            }
        }

        private static void CloseSocket(TcpClient client)
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                Log("CAMERA", "旧摄像头连接尚未完全退出，新连接继续接管。");
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
                Log("CAMERA", "旧摄像头连接已释放，新连接继续接管。");
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
                Log("CAMERA", "正在关闭。");
            }
        }

        private sealed class CameraReceiveStats
        {
            public long PacketCount { get; private set; }

            public long FrameCount { get; private set; }

            public long ByteCount { get; private set; }

            public long KeyFrameCount { get; private set; }

            public long CodecConfigPacketCount { get; private set; }

            public DateTimeOffset LastFrameAt { get; private set; }

            public long DecodedFrameCount { get; private set; }

            public long DecodeErrorCount { get; private set; }

            public DateTimeOffset LastDecodedFrameAt { get; private set; }

            public double LastDecodeLagMs { get; private set; }

            public string LastDecodeError { get; private set; } = string.Empty;

            public void Record(CameraPacketHeader packet)
            {
                PacketCount += 1;
                ByteCount += packet.PayloadLength;

                if ((packet.Flags & FlagCodecConfig) != 0)
                {
                    CodecConfigPacketCount += 1;
                }
                else
                {
                    FrameCount += 1;
                    LastFrameAt = DateTimeOffset.UtcNow;
                }

                if ((packet.Flags & FlagKeyFrame) != 0)
                {
                    KeyFrameCount += 1;
                }
            }

            public void RecordDecodedFrame()
            {
                DecodedFrameCount += 1;
                var now = DateTimeOffset.UtcNow;
                LastDecodedFrameAt = now;
                if (LastFrameAt != default)
                {
                    LastDecodeLagMs = Math.Max(0.0, (now - LastFrameAt).TotalMilliseconds);
                }
            }

            public void RecordDecodeError(string message)
            {
                DecodeErrorCount += 1;
                LastDecodeError = message.ReplaceLineEndings(" ");
            }

            public void RecordDecodeWait(string message)
            {
                LastDecodeError = message;
            }
        }

        private readonly record struct CameraPacketHeader(
            int Flags,
            long Sequence,
            long TimestampUs,
            int PayloadLength);

        private readonly record struct CameraStabilitySnapshot(
            long ReconnectCount,
            long RecoveryAttemptCount,
            long ConsecutiveFailureCount,
            long LastRecoveryDurationMs,
            string LastDisconnectReason);
    }

    private sealed class CameraLatestFrameCache : IDisposable
    {
        public const string MapName = @"Local\SideDockCameraPreviewFrame";
        public const string GlobalMapName = @"Global\SideDockCameraPreviewFrame";
        private const int HeaderSize = 128;
        private const int Version = 1;
        private const int FormatBgra32 = 1;
        private const int MaxWidth = 2560;
        private const int MaxHeight = 1440;
        private const int MaxFrameBytes = MaxWidth * MaxHeight * 4;
        private const int TotalBytes = HeaderSize + MaxFrameBytes;
        private const int Magic = 0x46434453; // SDCF

        private readonly object _lock = new();
        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _view;
        private IntPtr _globalMappingHandle;
        private IntPtr _globalViewPointer;
        private long _frameSequence;

        public CameraLatestFrameCache(int width, int height)
        {
            _mapping = MemoryMappedFile.CreateOrOpen(MapName, TotalBytes, MemoryMappedFileAccess.ReadWrite);
            _view = _mapping.CreateViewAccessor(0, TotalBytes, MemoryMappedFileAccess.ReadWrite);
            InitializeHeader(_view, width, height);

            try
            {
                (_globalMappingHandle, _globalViewPointer) = CreateGlobalFrameMapping();
                InitializeNativeHeader(_globalViewPointer, width, height);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ExternalException)
            {
                DisposeGlobalMapping();
            }
        }

        public long PublishedFrameSequence => Interlocked.Read(ref _frameSequence);

        public void Write(byte[] bgra, int width, int height, int stride, long sourceTimestampUs)
        {
            if (width <= 0 || height <= 0 || stride < width * 4)
            {
                throw new ArgumentException("Invalid BGRA frame layout.");
            }

            var frameBytes = checked(stride * height);
            if (frameBytes > MaxFrameBytes || bgra.Length < frameBytes)
            {
                throw new ArgumentException("Camera preview frame is larger than the shared cache.");
            }

            lock (_lock)
            {
                var nextSequence = Interlocked.Read(ref _frameSequence) + 1;
                var busySequence = checked(nextSequence * 2 - 1);
                var readySequence = checked(nextSequence * 2);

                WriteToView(_view, bgra, width, height, stride, frameBytes, sourceTimestampUs, busySequence, readySequence);
                if (_globalViewPointer != IntPtr.Zero)
                {
                    try
                    {
                        WriteToNativeView(_globalViewPointer, bgra, width, height, stride, frameBytes, sourceTimestampUs, busySequence, readySequence);
                    }
                    catch (Exception ex) when (ex is ExternalException or AccessViolationException)
                    {
                        // The session-local preview map remains authoritative; global mirror failure is non-fatal.
                        DisposeGlobalMapping();
                    }
                }

                Interlocked.Exchange(ref _frameSequence, nextSequence);
            }
        }

        public void Dispose()
        {
            DisposeGlobalMapping();
            _view.Dispose();
            _mapping.Dispose();
        }

        private static void InitializeHeader(MemoryMappedViewAccessor view, int width, int height)
        {
            view.Write(0, Magic);
            view.Write(4, Version);
            view.Write(8, HeaderSize);
            view.Write(12, Math.Max(1, width));
            view.Write(16, Math.Max(1, height));
            view.Write(20, Math.Max(1, width) * 4);
            view.Write(24, FormatBgra32);
            view.Write(28, 0);
            view.Write(32, 0L);
            view.Write(40, 0L);
            view.Write(48, 0L);
            view.Write(56, 0L);
        }

        private static (IntPtr Handle, IntPtr View) CreateGlobalFrameMapping()
        {
            var securityDescriptor = IntPtr.Zero;
            var handle = IntPtr.Zero;
            var view = IntPtr.Zero;
            try
            {
                var sddl = CreateGlobalFrameSecurityDescriptor();
                if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptor(
                    sddl,
                    Native.SecurityDescriptorRevision,
                    out securityDescriptor,
                    out _))
                {
                    throw new ExternalException("Unable to create camera frame security descriptor.", Marshal.GetHRForLastWin32Error());
                }

                var securityAttributes = new SecurityAttributes
                {
                    Length = Marshal.SizeOf<SecurityAttributes>(),
                    SecurityDescriptor = securityDescriptor,
                    InheritHandle = false
                };

                handle = Native.CreateFileMapping(
                    Native.InvalidHandleValue,
                    ref securityAttributes,
                    Native.PageReadWrite,
                    0,
                    TotalBytes,
                    GlobalMapName);
                if (handle == IntPtr.Zero)
                {
                    throw new ExternalException("Unable to create global camera frame mapping.", Marshal.GetHRForLastWin32Error());
                }

                view = Native.MapViewOfFile(
                    handle,
                    Native.FileMapRead | Native.FileMapWrite,
                    0,
                    0,
                    UIntPtr.Add(UIntPtr.Zero, TotalBytes));
                if (view == IntPtr.Zero)
                {
                    throw new ExternalException("Unable to map global camera frame view.", Marshal.GetHRForLastWin32Error());
                }

                return (handle, view);
            }
            catch
            {
                if (view != IntPtr.Zero)
                {
                    _ = Native.UnmapViewOfFile(view);
                }

                if (handle != IntPtr.Zero)
                {
                    _ = Native.CloseHandle(handle);
                }

                throw;
            }
            finally
            {
                if (securityDescriptor != IntPtr.Zero)
                {
                    _ = Native.LocalFree(securityDescriptor);
                }
            }
        }

        private static string CreateGlobalFrameSecurityDescriptor()
        {
            var currentUserSid = WindowsIdentity.GetCurrent().User?.Value;
            var userAce = string.IsNullOrWhiteSpace(currentUserSid)
                ? string.Empty
                : $"(A;;GA;;;{currentUserSid})";

            return "D:P"
                + "(A;;GA;;;SY)"
                + "(A;;GA;;;BA)"
                + userAce
                + "(A;;GR;;;LS)"
                + "(A;;GR;;;AC)"
                + "(A;;GR;;;WD)";
        }

        private static void WriteToView(
            MemoryMappedViewAccessor view,
            byte[] bgra,
            int width,
            int height,
            int stride,
            int frameBytes,
            long sourceTimestampUs,
            long busySequence,
            long readySequence)
        {
            view.Write(32, busySequence);
            view.WriteArray(HeaderSize, bgra, 0, frameBytes);
            view.Write(0, Magic);
            view.Write(4, Version);
            view.Write(8, HeaderSize);
            view.Write(12, width);
            view.Write(16, height);
            view.Write(20, stride);
            view.Write(24, FormatBgra32);
            view.Write(28, frameBytes);
            view.Write(40, sourceTimestampUs);
            view.Write(48, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            view.Write(56, 0L);
            view.Write(32, readySequence);
        }

        private void DisposeGlobalMapping()
        {
            if (_globalViewPointer != IntPtr.Zero)
            {
                _ = Native.UnmapViewOfFile(_globalViewPointer);
                _globalViewPointer = IntPtr.Zero;
            }

            if (_globalMappingHandle != IntPtr.Zero)
            {
                _ = Native.CloseHandle(_globalMappingHandle);
                _globalMappingHandle = IntPtr.Zero;
            }
        }

        private static void InitializeNativeHeader(IntPtr view, int width, int height)
        {
            WriteNativeInt32(view, 0, Magic);
            WriteNativeInt32(view, 4, Version);
            WriteNativeInt32(view, 8, HeaderSize);
            WriteNativeInt32(view, 12, Math.Max(1, width));
            WriteNativeInt32(view, 16, Math.Max(1, height));
            WriteNativeInt32(view, 20, Math.Max(1, width) * 4);
            WriteNativeInt32(view, 24, FormatBgra32);
            WriteNativeInt32(view, 28, 0);
            WriteNativeInt64(view, 32, 0L);
            WriteNativeInt64(view, 40, 0L);
            WriteNativeInt64(view, 48, 0L);
            WriteNativeInt64(view, 56, 0L);
        }

        private static void WriteToNativeView(
            IntPtr view,
            byte[] bgra,
            int width,
            int height,
            int stride,
            int frameBytes,
            long sourceTimestampUs,
            long busySequence,
            long readySequence)
        {
            WriteNativeInt64(view, 32, busySequence);
            Marshal.Copy(bgra, 0, IntPtr.Add(view, HeaderSize), frameBytes);
            WriteNativeInt32(view, 0, Magic);
            WriteNativeInt32(view, 4, Version);
            WriteNativeInt32(view, 8, HeaderSize);
            WriteNativeInt32(view, 12, width);
            WriteNativeInt32(view, 16, height);
            WriteNativeInt32(view, 20, stride);
            WriteNativeInt32(view, 24, FormatBgra32);
            WriteNativeInt32(view, 28, frameBytes);
            WriteNativeInt64(view, 40, sourceTimestampUs);
            WriteNativeInt64(view, 48, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            WriteNativeInt64(view, 56, 0L);
            WriteNativeInt64(view, 32, readySequence);
        }

        private static void WriteNativeInt32(IntPtr view, int offset, int value)
        {
            Marshal.WriteInt32(IntPtr.Add(view, offset), value);
        }

        private static void WriteNativeInt64(IntPtr view, int offset, long value)
        {
            Marshal.WriteInt64(IntPtr.Add(view, offset), value);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool InheritHandle;
        }

        private static class Native
        {
            public const int SecurityDescriptorRevision = 1;
            public const int PageReadWrite = 0x04;
            public const int FileMapWrite = 0x0002;
            public const int FileMapRead = 0x0004;
            public static readonly IntPtr InvalidHandleValue = new(-1);

            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
                string stringSecurityDescriptor,
                int stringSDRevision,
                out IntPtr securityDescriptor,
                out int securityDescriptorSize);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr CreateFileMapping(
                IntPtr fileHandle,
                ref SecurityAttributes securityAttributes,
                int protect,
                int maximumSizeHigh,
                int maximumSizeLow,
                string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr MapViewOfFile(
                IntPtr fileMappingObject,
                int desiredAccess,
                int fileOffsetHigh,
                int fileOffsetLow,
                UIntPtr numberOfBytesToMap);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool UnmapViewOfFile(IntPtr baseAddress);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr LocalFree(IntPtr memory);
        }
    }

    private static class CameraDebugReplayClient
    {
        private const int HeaderSize = 40;
        private const int Version = 1;
        private const int FlagKeyFrame = 1;
        private const int FlagCodecConfig = 2;
        private static readonly byte[] Magic = "SDCM"u8.ToArray();

        public static async Task RunAsync(IPAddress address, HostOptions options, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(options.CameraReplayFilePath))
            {
                return;
            }

            try
            {
                var stream = H264AnnexBStream.Load(options.CameraReplayFilePath);
                Log(
                    "CAMERA REPLAY",
                    $"camera-replay-state=loading file={options.CameraReplayFilePath} packets={stream.PacketCount} frames={stream.FramePacketCount}");

                using var client = await ConnectWithRetryAsync(address, options.CameraPort, cancellationToken);
                await using var networkStream = client.GetStream();
                await SendReplayAsync(networkStream, stream, options.CameraFps, cancellationToken);
                Log("CAMERA REPLAY", "camera-replay-state=completed");
            }
            catch (OperationCanceledException)
            {
                Log("CAMERA REPLAY", "camera-replay-state=stopped");
            }
            catch (Exception ex)
            {
                Log("CAMERA REPLAY", $"camera-replay-state=failed message={ex.Message}");
            }
        }

        private static async Task<TcpClient> ConnectWithRetryAsync(IPAddress address, int port, CancellationToken cancellationToken)
        {
            var startedAt = Stopwatch.StartNew();
            Exception? lastError = null;
            while (startedAt.Elapsed < TimeSpan.FromSeconds(10))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var client = new TcpClient();
                try
                {
                    client.NoDelay = true;
                    await client.ConnectAsync(address, port, cancellationToken);
                    Log("CAMERA REPLAY", $"camera-replay-state=connected endpoint={address}:{port}");
                    return client;
                }
                catch (Exception ex) when (ex is SocketException or IOException)
                {
                    lastError = ex;
                    client.Dispose();
                    await Task.Delay(200, cancellationToken);
                }
            }

            throw new IOException($"Unable to connect camera replay to {address}:{port}: {lastError?.Message}");
        }

        private static async Task SendReplayAsync(
            NetworkStream stream,
            H264AnnexBStream replay,
            int fps,
            CancellationToken cancellationToken)
        {
            var frameInterval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, fps));
            var header = new byte[HeaderSize];
            long sequence = 0;
            long frameIndex = 0;

            foreach (var packet in replay.Packets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sequence++;
                var flags = 0;
                if (packet.IsKeyFrame)
                {
                    flags |= FlagKeyFrame;
                }

                if (!packet.ContainsPicture && (H264AnnexBStream.ContainsNalType(packet.Payload, 7) || H264AnnexBStream.ContainsNalType(packet.Payload, 8)))
                {
                    flags |= FlagCodecConfig;
                }

                var timestampUs = frameIndex * 1_000_000L / Math.Max(1, fps);
                WriteHeader(header, flags, sequence, timestampUs, packet.Payload.Length);
                await stream.WriteAsync(header, cancellationToken);
                await stream.WriteAsync(packet.Payload, cancellationToken);
                await stream.FlushAsync(cancellationToken);

                if (packet.ContainsPicture)
                {
                    frameIndex++;
                    await Task.Delay(frameInterval, cancellationToken);
                }
            }
        }

        private static void WriteHeader(byte[] header, int flags, long sequence, long timestampUs, int payloadLength)
        {
            Magic.CopyTo(header, 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), Version);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), HeaderSize);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), flags);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16, 8), sequence);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24, 8), timestampUs);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32, 4), payloadLength);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(36, 4), 0);
        }
    }

    private sealed record CameraDecodedFrame(byte[] Bgra, int Width, int Height, int Stride);

    private sealed class CameraH264Decoder : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int _fps;
        private DecoderMft? _decoder;

        public CameraH264Decoder(int width, int height, int fps)
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _fps = Math.Max(1, fps);
        }

        public string SelectedMftName => _decoder?.SelectedMftName ?? string.Empty;

        public void Start()
        {
            _decoder ??= new DecoderMft(_width, _height, _fps);
            _decoder.Start();
        }

        public IReadOnlyList<CameraDecodedFrame> Decode(ReadOnlySpan<byte> payload, long timestampUs)
        {
            var decoder = _decoder ?? throw new InvalidOperationException("Camera decoder is not started.");
            return decoder.Decode(payload, timestampUs);
        }

        public void Reset()
        {
            _decoder?.Dispose();
            _decoder = new DecoderMft(_width, _height, _fps);
            _decoder.Start();
        }

        public void Dispose()
        {
            _decoder?.Dispose();
            _decoder = null;
        }

        private sealed class DecoderMft : IDisposable
        {
            private const int OutputFormatNv12 = 0;
            private const int OutputFormatRgb32 = 1;
            private readonly int _width;
            private readonly int _height;
            private readonly int _fps;
            private IMFTransform? _transform;
            private int _outputBufferSize;
            private bool _outputProvidesSamples;
            private int _outputFormat;
            private bool _started;
            private bool _mfStarted;
            private bool _comInitialized;

            public DecoderMft(int width, int height, int fps)
            {
                _width = width;
                _height = height;
                _fps = fps;
            }

            public string SelectedMftName { get; private set; } = string.Empty;

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
                ThrowIfFailed(GetTransform().ProcessMessage(Native.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero), "Unable to begin Media Foundation camera decoding.");
                ThrowIfFailed(GetTransform().ProcessMessage(Native.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero), "Unable to start Media Foundation camera decoding.");
                RefreshOutputInfo();
                _started = true;
            }

            public IReadOnlyList<CameraDecodedFrame> Decode(ReadOnlySpan<byte> payload, long timestampUs)
            {
                if (!_started)
                {
                    throw new InvalidOperationException("Media Foundation camera decoder has not started.");
                }

                if (payload.Length == 0)
                {
                    return Array.Empty<CameraDecodedFrame>();
                }

                var transform = GetTransform();
                using var inputSample = CreateInputSample(payload, timestampUs);
                var hr = transform.ProcessInput(0, inputSample.Sample, 0);
                if (hr == Native.MF_E_NOTACCEPTING)
                {
                    var drained = DrainOutput(transform);
                    ThrowIfFailed(transform.ProcessInput(0, inputSample.Sample, 0), "Media Foundation camera decoder did not accept input after draining output.");
                    var decoded = DrainOutput(transform);
                    if (drained.Count == 0)
                    {
                        return decoded;
                    }

                    drained.AddRange(decoded);
                    return drained;
                }

                ThrowIfFailed(hr, "Media Foundation camera decoder rejected an input packet.");
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

            private void CreateAndConfigureTransform()
            {
                var failures = new List<string>();
                foreach (var candidate in EnumerateH264DecoderCandidates())
                {
                    try
                    {
                        CreateTransform(candidate);
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

                throw new InvalidOperationException($"Unable to initialize an H.264 Media Foundation decoder. {string.Join("; ", failures)}");
            }

            private void CreateTransform(H264DecoderMftCandidate candidate)
            {
                var clsid = candidate.Clsid;
                var iid = Native.IID_IMFTransform;
                ThrowIfFailed(
                    Native.CoCreateInstance(ref clsid, IntPtr.Zero, Native.CLSCTX_INPROC_SERVER, ref iid, out _transform),
                    $"Unable to create H.264 Media Foundation decoder MFT '{candidate.Name}'.");
            }

            private IReadOnlyList<H264DecoderMftCandidate> EnumerateH264DecoderCandidates()
            {
                var candidates = new List<H264DecoderMftCandidate>();
                var category = Native.MFT_CATEGORY_VIDEO_DECODER;
                var inputType = new MFTRegisterTypeInfo
                {
                    MajorType = Native.MFMediaType_Video,
                    Subtype = Native.MFVideoFormat_H264
                };
                var outputType = new MFTRegisterTypeInfo
                {
                    MajorType = Native.MFMediaType_Video,
                    Subtype = Native.MFVideoFormat_NV12
                };
                var flags = Native.MFT_ENUM_FLAG_SYNCMFT
                    | Native.MFT_ENUM_FLAG_ASYNCMFT
                    | Native.MFT_ENUM_FLAG_HARDWARE
                    | Native.MFT_ENUM_FLAG_SORTANDFILTER;

                var hr = Native.MFTEnumEx(ref category, flags, ref inputType, ref outputType, out var activateArray, out var count);
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
                                var name = GetStringAttribute(attributes, Native.MFT_FRIENDLY_NAME_Attribute) ?? $"H.264 decoder MFT {index}";
                                var clsid = GetGuidAttribute(attributes, Native.MFT_TRANSFORM_CLSID_Attribute);
                                if (clsid != Guid.Empty)
                                {
                                    candidates.Add(new H264DecoderMftCandidate(name, clsid, index));
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

                if (!candidates.Any(candidate => candidate.Clsid == Native.CLSID_CMSH264DecoderMFT))
                {
                    candidates.Add(new H264DecoderMftCandidate("H264 Decoder MFT", Native.CLSID_CMSH264DecoderMFT, int.MaxValue));
                }

                return candidates
                    .OrderByDescending(candidate => candidate.Clsid == Native.CLSID_CMSH264DecoderMFT ? 100 : 10)
                    .ThenBy(candidate => candidate.OriginalIndex)
                    .ToArray();
            }

            private void ConfigureMediaTypes()
            {
                using var inputType = CreateInputMediaType();
                ThrowIfFailed(GetTransform().SetInputType(0, inputType.MediaType, 0), "Unable to set Media Foundation H.264 camera input type.");

                if (TrySetOutputMediaType(Native.MFVideoFormat_NV12, OutputFormatNv12))
                {
                    return;
                }

                if (TrySetOutputMediaType(Native.MFVideoFormat_RGB32, OutputFormatRgb32))
                {
                    return;
                }

                throw new InvalidOperationException("Media Foundation H.264 decoder does not expose RGB32 or NV12 output.");
            }

            private bool TrySetOutputMediaType(Guid subtype, int format)
            {
                using var outputType = CreateOutputMediaType(subtype, format);
                var hr = GetTransform().SetOutputType(0, outputType.MediaType, 0);
                if (hr < 0)
                {
                    return false;
                }

                _outputFormat = format;
                return true;
            }

            private MediaTypeHandle CreateInputMediaType()
            {
                ThrowIfFailed(Native.MFCreateMediaType(out var mediaType), "Unable to create Media Foundation camera input media type.");
                var attributes = (IMFAttributes)mediaType;

                SetGuid(attributes, Native.MF_MT_MAJOR_TYPE, Native.MFMediaType_Video);
                SetGuid(attributes, Native.MF_MT_SUBTYPE, Native.MFVideoFormat_H264);
                SetUInt32(attributes, Native.MF_MT_INTERLACE_MODE, Native.MFVideoInterlace_Progressive);
                SetPackedUInt32Pair(attributes, Native.MF_MT_FRAME_SIZE, _width, _height);
                SetPackedUInt32Pair(attributes, Native.MF_MT_FRAME_RATE, _fps, 1);
                SetPackedUInt32Pair(attributes, Native.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
                return new MediaTypeHandle(mediaType);
            }

            private MediaTypeHandle CreateOutputMediaType(Guid subtype, int format)
            {
                ThrowIfFailed(Native.MFCreateMediaType(out var mediaType), "Unable to create Media Foundation camera output media type.");
                var attributes = (IMFAttributes)mediaType;
                var sampleSize = format == OutputFormatRgb32
                    ? checked(_width * _height * 4)
                    : checked(_width * _height * 3 / 2);

                SetGuid(attributes, Native.MF_MT_MAJOR_TYPE, Native.MFMediaType_Video);
                SetGuid(attributes, Native.MF_MT_SUBTYPE, subtype);
                SetUInt32(attributes, Native.MF_MT_INTERLACE_MODE, Native.MFVideoInterlace_Progressive);
                SetUInt32(attributes, Native.MF_MT_FIXED_SIZE_SAMPLES, 1);
                SetUInt32(attributes, Native.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
                SetUInt32(attributes, Native.MF_MT_SAMPLE_SIZE, sampleSize);
                SetPackedUInt32Pair(attributes, Native.MF_MT_FRAME_SIZE, _width, _height);
                SetPackedUInt32Pair(attributes, Native.MF_MT_FRAME_RATE, _fps, 1);
                SetPackedUInt32Pair(attributes, Native.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
                return new MediaTypeHandle(mediaType);
            }

            private InputSampleHandle CreateInputSample(ReadOnlySpan<byte> payload, long timestampUs)
            {
                ThrowIfFailed(Native.MFCreateSample(out var sample), "Unable to create Media Foundation camera input sample.");
                ThrowIfFailed(Native.MFCreateMemoryBuffer(payload.Length, out var buffer), "Unable to create Media Foundation camera input buffer.");

                IntPtr bufferPtr = IntPtr.Zero;
                try
                {
                    ThrowIfFailed(buffer.Lock(out bufferPtr, out var maxLength, out _), "Unable to lock Media Foundation camera input buffer.");
                    if (maxLength < payload.Length)
                    {
                        throw new InvalidOperationException("Media Foundation camera input buffer is too small.");
                    }

                    var bytes = payload.ToArray();
                    Marshal.Copy(bytes, 0, bufferPtr, bytes.Length);
                }
                finally
                {
                    if (bufferPtr != IntPtr.Zero)
                    {
                        buffer.Unlock();
                    }
                }

                ThrowIfFailed(buffer.SetCurrentLength(payload.Length), "Unable to set Media Foundation camera input buffer length.");
                ThrowIfFailed(sample.AddBuffer(buffer), "Unable to attach camera input buffer to Media Foundation sample.");
                ThrowIfFailed(sample.SetSampleTime(timestampUs * 10), "Unable to set camera sample time.");
                ThrowIfFailed(sample.SetSampleDuration(10_000_000L / Math.Max(1, _fps)), "Unable to set camera sample duration.");
                return new InputSampleHandle(sample, buffer);
            }

            private List<CameraDecodedFrame> DrainOutput(IMFTransform transform)
            {
                var frames = new List<CameraDecodedFrame>();

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
                            return frames;
                        }

                        if (hr == Native.MF_E_TRANSFORM_STREAM_CHANGE || (outputBuffer.Status & Native.MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE) != 0)
                        {
                            ConfigureMediaTypes();
                            RefreshOutputInfo();
                            continue;
                        }

                        ThrowIfFailed(hr, "Media Foundation camera decoder failed while producing output.");

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
                                var frame = ConvertOutputToBgra(bytes);
                                if (frame is not null)
                                {
                                    frames.Add(frame);
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

                ThrowIfFailed(Native.MFCreateSample(out var sample), "Unable to create Media Foundation camera output sample.");
                var bufferSize = Math.Max(_outputBufferSize, _width * _height * 4);
                ThrowIfFailed(Native.MFCreateMemoryBuffer(bufferSize, out var buffer), "Unable to create Media Foundation camera output buffer.");
                ThrowIfFailed(sample.AddBuffer(buffer), "Unable to attach camera output buffer to Media Foundation sample.");
                return new OutputSampleHandle(sample, buffer);
            }

            private void RefreshOutputInfo()
            {
                ThrowIfFailed(GetTransform().GetOutputStreamInfo(0, out var streamInfo), "Unable to read Media Foundation camera output stream info.");
                _outputProvidesSamples = (streamInfo.Flags & Native.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
                _outputBufferSize = streamInfo.Size > 0
                    ? streamInfo.Size
                    : Math.Max(64 * 1024, _width * _height * 4);
            }

            private CameraDecodedFrame? ConvertOutputToBgra(byte[] output)
            {
                if (_outputFormat == OutputFormatRgb32)
                {
                    var frameBytes = checked(_width * _height * 4);
                    if (output.Length < frameBytes)
                    {
                        return null;
                    }

                    var bgra = new byte[frameBytes];
                    Buffer.BlockCopy(output, 0, bgra, 0, frameBytes);
                    EnsureAlpha(bgra);
                    return new CameraDecodedFrame(bgra, _width, _height, _width * 4);
                }

                var nv12Bytes = checked(_width * _height * 3 / 2);
                if (output.Length < nv12Bytes)
                {
                    return null;
                }

                var converted = new byte[checked(_width * _height * 4)];
                ConvertNv12ToBgra(output, converted, _width, _height);
                return new CameraDecodedFrame(converted, _width, _height, _width * 4);
            }

            private static void ConvertNv12ToBgra(byte[] nv12, byte[] bgra, int width, int height)
            {
                var yPlaneSize = width * height;
                for (var y = 0; y < height; y++)
                {
                    var yOffset = y * width;
                    var uvOffset = yPlaneSize + (y / 2) * width;
                    var bgraOffset = yOffset * 4;
                    for (var x = 0; x < width; x++)
                    {
                        var yy = Math.Max(0, nv12[yOffset + x] - 16);
                        var u = nv12[uvOffset + (x & ~1)] - 128;
                        var v = nv12[uvOffset + (x & ~1) + 1] - 128;
                        bgra[bgraOffset] = ClampToByte((298 * yy + 516 * u + 128) >> 8);
                        bgra[bgraOffset + 1] = ClampToByte((298 * yy - 100 * u - 208 * v + 128) >> 8);
                        bgra[bgraOffset + 2] = ClampToByte((298 * yy + 409 * v + 128) >> 8);
                        bgra[bgraOffset + 3] = 255;
                        bgraOffset += 4;
                    }
                }
            }

            private static void EnsureAlpha(byte[] bgra)
            {
                for (var offset = 3; offset < bgra.Length; offset += 4)
                {
                    bgra[offset] = 255;
                }
            }

            private void ReleaseActiveTransform()
            {
                if (_transform is not null)
                {
                    ReleaseComObject(_transform);
                    _transform = null;
                }

                _outputBufferSize = 0;
                _outputProvidesSamples = false;
                _outputFormat = OutputFormatNv12;
                SelectedMftName = string.Empty;
            }

            private IMFTransform GetTransform()
            {
                return _transform ?? throw new InvalidOperationException("Media Foundation camera decoder is not initialized.");
            }
        }

        private static byte[] ReadSampleBytes(IMFSample sample)
        {
            IMFMediaBuffer? buffer = null;
            try
            {
                ThrowIfFailed(sample.ConvertToContiguousBuffer(out buffer), "Unable to get contiguous Media Foundation camera output buffer.");
                ThrowIfFailed(buffer.Lock(out var data, out _, out var currentLength), "Unable to lock Media Foundation camera output buffer.");
                try
                {
                    if (currentLength == 0)
                    {
                        ThrowIfFailed(buffer.GetCurrentLength(out currentLength), "Unable to read Media Foundation camera output buffer length.");
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
            int GetItemType(ref Guid guidKey, out int valueType);

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
            int GetItemType(ref Guid guidKey, out int valueType);

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

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct H264DecoderMftCandidate(string Name, Guid Clsid, int OriginalIndex);

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

        private static class Native
        {
            public const int S_OK = 0;
            public const int S_FALSE = 1;
            public const int MF_VERSION = 0x00020070;
            public const int MFSTARTUP_FULL = 0;
            public const int COINIT_MULTITHREADED = 0;
            public const int CLSCTX_INPROC_SERVER = 1;
            public const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
            public const int MFVideoInterlace_Progressive = 2;
            public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
            public const int MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
            public const int MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
            public const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
            public const int MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE = 0x00000100;
            public const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;
            public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
            public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
            public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
            public const int MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
            public const int MFT_ENUM_FLAG_ASYNCMFT = 0x00000002;
            public const int MFT_ENUM_FLAG_HARDWARE = 0x00000004;
            public const int MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;

            public static readonly Guid CLSID_CMSH264DecoderMFT = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
            public static readonly Guid IID_IMFTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");
            public static Guid MFT_CATEGORY_VIDEO_DECODER = new("D6C02D4B-6833-45B4-971A-05A4B04BAB91");
            public static Guid MFT_FRIENDLY_NAME_Attribute = new("314FFBAE-5B41-4C95-9C19-4E7D586FACE3");
            public static Guid MFT_TRANSFORM_CLSID_Attribute = new("6821C42B-65A4-4E82-99BC-9A88205ECD0C");
            public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
            public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
            public static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
            public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
            public static Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
            public static Guid MF_MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
            public static Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("C9173739-5E56-461C-B713-46FB995CB95F");
            public static Guid MF_MT_FIXED_SIZE_SAMPLES = new("B8EBEFAF-B718-4E04-B0A9-116775E3321B");
            public static Guid MF_MT_SAMPLE_SIZE = new("DAD3AB78-1990-408B-BCE2-EBA673DACC10");
            public static Guid MF_MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D");
            public static Guid MF_MT_FRAME_RATE = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
            public static Guid MF_MT_PIXEL_ASPECT_RATIO = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
            public static Guid MF_MT_INTERLACE_MODE = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");

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
                ref MFTRegisterTypeInfo inputType,
                ref MFTRegisterTypeInfo outputType,
                out IntPtr activateArray,
                out int count);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFCreateMediaType(out IMFMediaType mediaType);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFCreateSample(out IMFSample sample);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);
        }
    }
}
