package com.sidedock.client;

import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import org.json.JSONObject;
import java.io.BufferedReader;
import java.io.BufferedWriter;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.atomic.AtomicLong;

public final class ControlClient {
    public interface Listener {
        void onStateChanged(ConnectionState state);
        void onLog(String message);
        void onStats(long sent, long received);
        void onVideoStart(int port, int width, int height, int fps);
        void onAudioConfig(AudioConfig config);
        void onAudioTestRequest(AudioTestRequest request);
        void onCameraConfig(CameraConfig config);
        void onClockSync(ClockSync clockSync);
        void onCaptureStatus(CaptureStatus status);
        void onEncoderStatus(EncoderStatus status);
        void onDisplayLayout(DisplayLayout layout);
        void onDisplayMetrics(DisplayMetrics metrics);
        void onDisplayModeChanged(DisplayModeChanged mode);
        void onCursorShape(String kind, boolean visible);
        void onCursorState(CursorState state);
    }

    public static final class AudioConfig {
        public final boolean audioEnabled;
        public final boolean microphoneEnabled;
        public final boolean speakerEnabled;
        public final int port;
        public final int sampleRate;
        public final int channels;
        public final int microphoneChannels;
        public final int speakerChannels;
        public final int bitsPerSample;

        public AudioConfig(
            boolean audioEnabled,
            boolean microphoneEnabled,
            boolean speakerEnabled,
            int port,
            int sampleRate,
            int channels,
            int microphoneChannels,
            int speakerChannels,
            int bitsPerSample
        ) {
            this.audioEnabled = audioEnabled;
            this.microphoneEnabled = microphoneEnabled;
            this.speakerEnabled = speakerEnabled;
            this.port = port;
            this.sampleRate = sampleRate;
            this.channels = channels;
            this.microphoneChannels = microphoneChannels;
            this.speakerChannels = speakerChannels;
            this.bitsPerSample = bitsPerSample;
        }
    }

    public static final class CameraConfig {
        public final boolean enabled;
        public final int port;
        public final int width;
        public final int height;
        public final int fps;
        public final String codec;
        public final String facing;

        public CameraConfig(
            boolean enabled,
            int port,
            int width,
            int height,
            int fps,
            String codec,
            String facing
        ) {
            this.enabled = enabled;
            this.port = port;
            this.width = width;
            this.height = height;
            this.fps = fps;
            this.codec = codec;
            this.facing = facing;
        }
    }

    public static final class AudioTestRequest {
        public final String testId;
        public final String kind;
        public final int durationMs;
        public final int timeoutMs;

        public AudioTestRequest(
            String testId,
            String kind,
            int durationMs,
            int timeoutMs
        ) {
            this.testId = testId;
            this.kind = kind;
            this.durationMs = durationMs;
            this.timeoutMs = timeoutMs;
        }
    }

    public static final class ClockSync {
        public final long serverTimeMs;
        public final long clientSentAtMs;
        public final long clientReceivedAtMs;
        public final long offsetMs;
        public final long rttMs;
        public final long errorBoundMs;

        public ClockSync(
            long serverTimeMs,
            long clientSentAtMs,
            long clientReceivedAtMs,
            long offsetMs,
            long rttMs,
            long errorBoundMs
        ) {
            this.serverTimeMs = serverTimeMs;
            this.clientSentAtMs = clientSentAtMs;
            this.clientReceivedAtMs = clientReceivedAtMs;
            this.offsetMs = offsetMs;
            this.rttMs = rttMs;
            this.errorBoundMs = errorBoundMs;
        }
    }

    public static final class CaptureStatus {
        public final String state;
        public final String source;
        public final String target;
        public final long framesCaptured;
        public final long framesConverted;
        public final long captureErrors;
        public final double avgCaptureMs;
        public final double avgConvertMs;
        public final double gpuConvertMs;
        public final long framesDropped;
        public final double lastFrameAgeMs;
        public final boolean gpuPath;
        public final String fallback;
        public final String errorCode;
        public final String errorMessage;

        public CaptureStatus(
            String state,
            String source,
            String target,
            long framesCaptured,
            long framesConverted,
            long captureErrors,
            double avgCaptureMs,
            double avgConvertMs,
            double gpuConvertMs,
            long framesDropped,
            double lastFrameAgeMs,
            boolean gpuPath,
            String fallback,
            String errorCode,
            String errorMessage
        ) {
            this.state = state;
            this.source = source;
            this.target = target;
            this.framesCaptured = framesCaptured;
            this.framesConverted = framesConverted;
            this.captureErrors = captureErrors;
            this.avgCaptureMs = avgCaptureMs;
            this.avgConvertMs = avgConvertMs;
            this.gpuConvertMs = gpuConvertMs;
            this.framesDropped = framesDropped;
            this.lastFrameAgeMs = lastFrameAgeMs;
            this.gpuPath = gpuPath;
            this.fallback = fallback;
            this.errorCode = errorCode;
            this.errorMessage = errorMessage;
        }
    }

    public static final class EncoderStatus {
        public final long framesGenerated;
        public final long framesEncoded;
        public final long framesSent;
        public final long framesDropped;
        public final double avgEncodeMs;
        public final double maxEncodeMs;
        public final double p50EncodeMs;
        public final double p95EncodeMs;
        public final double p99EncodeMs;
        public final double avgSendMs;
        public final double maxSendMs;
        public final double p50SendMs;
        public final double p95SendMs;
        public final double p99SendMs;
        public final double outputKbps;
        public final double streamFps;
        public final long newFramesSent;
        public final long repeatFramesSent;
        public final long blackFramesSent;
        public final long keepaliveFramesSent;
        public final long lastKeyFrameSeq;
        public final boolean gpuPath;

        public EncoderStatus(
            long framesGenerated,
            long framesEncoded,
            long framesSent,
            long framesDropped,
            double avgEncodeMs,
            double maxEncodeMs,
            double p50EncodeMs,
            double p95EncodeMs,
            double p99EncodeMs,
            double avgSendMs,
            double maxSendMs,
            double p50SendMs,
            double p95SendMs,
            double p99SendMs,
            double outputKbps,
            double streamFps,
            long newFramesSent,
            long repeatFramesSent,
            long blackFramesSent,
            long keepaliveFramesSent,
            long lastKeyFrameSeq,
            boolean gpuPath
        ) {
            this.framesGenerated = framesGenerated;
            this.framesEncoded = framesEncoded;
            this.framesSent = framesSent;
            this.framesDropped = framesDropped;
            this.avgEncodeMs = avgEncodeMs;
            this.maxEncodeMs = maxEncodeMs;
            this.p50EncodeMs = p50EncodeMs;
            this.p95EncodeMs = p95EncodeMs;
            this.p99EncodeMs = p99EncodeMs;
            this.avgSendMs = avgSendMs;
            this.maxSendMs = maxSendMs;
            this.p50SendMs = p50SendMs;
            this.p95SendMs = p95SendMs;
            this.p99SendMs = p99SendMs;
            this.outputKbps = outputKbps;
            this.streamFps = streamFps;
            this.newFramesSent = newFramesSent;
            this.repeatFramesSent = repeatFramesSent;
            this.blackFramesSent = blackFramesSent;
            this.keepaliveFramesSent = keepaliveFramesSent;
            this.lastKeyFrameSeq = lastKeyFrameSeq;
            this.gpuPath = gpuPath;
        }
    }

    public static final class DisplayLayout {
        public final String source;
        public final int x;
        public final int y;
        public final int width;
        public final int height;
        public final int videoWidth;
        public final int videoHeight;
        public final double scale;
        public final String displayName;
        public final String devicePath;

        public DisplayLayout(
            String source,
            int x,
            int y,
            int width,
            int height,
            int videoWidth,
            int videoHeight,
            double scale,
            String displayName,
            String devicePath
        ) {
            this.source = source;
            this.x = x;
            this.y = y;
            this.width = width;
            this.height = height;
            this.videoWidth = videoWidth;
            this.videoHeight = videoHeight;
            this.scale = scale;
            this.displayName = displayName;
            this.devicePath = devicePath;
        }
    }

    public static final class DisplayMetrics {
        public final String source;
        public final double dpiScale;
        public final int desktopX;
        public final int desktopY;
        public final int desktopWidth;
        public final int desktopHeight;
        public final int displayX;
        public final int displayY;
        public final int displayWidth;
        public final int displayHeight;
        public final int refreshHz;
        public final int orientation;
        public final int videoWidth;
        public final int videoHeight;
        public final int videoRectX;
        public final int videoRectY;
        public final int videoRectWidth;
        public final int videoRectHeight;
        public final String fitMode;

        public DisplayMetrics(
            String source,
            double dpiScale,
            int desktopX,
            int desktopY,
            int desktopWidth,
            int desktopHeight,
            int displayX,
            int displayY,
            int displayWidth,
            int displayHeight,
            int refreshHz,
            int orientation,
            int videoWidth,
            int videoHeight,
            int videoRectX,
            int videoRectY,
            int videoRectWidth,
            int videoRectHeight,
            String fitMode
        ) {
            this.source = source;
            this.dpiScale = dpiScale;
            this.desktopX = desktopX;
            this.desktopY = desktopY;
            this.desktopWidth = desktopWidth;
            this.desktopHeight = desktopHeight;
            this.displayX = displayX;
            this.displayY = displayY;
            this.displayWidth = displayWidth;
            this.displayHeight = displayHeight;
            this.refreshHz = refreshHz;
            this.orientation = orientation;
            this.videoWidth = videoWidth;
            this.videoHeight = videoHeight;
            this.videoRectX = videoRectX;
            this.videoRectY = videoRectY;
            this.videoRectWidth = videoRectWidth;
            this.videoRectHeight = videoRectHeight;
            this.fitMode = fitMode;
        }
    }

    public static final class DisplayModeChanged {
        public final int width;
        public final int height;
        public final int refreshHz;
        public final int requestedRefreshHz;
        public final int displayRefreshHz;
        public final int videoWidth;
        public final int videoHeight;
        public final int videoFps;
        public final boolean success;
        public final String code;
        public final String message;

        public DisplayModeChanged(
            int width,
            int height,
            int refreshHz,
            int requestedRefreshHz,
            int displayRefreshHz,
            int videoWidth,
            int videoHeight,
            int videoFps,
            boolean success,
            String code,
            String message
        ) {
            this.width = width;
            this.height = height;
            this.refreshHz = refreshHz;
            this.requestedRefreshHz = requestedRefreshHz;
            this.displayRefreshHz = displayRefreshHz;
            this.videoWidth = videoWidth;
            this.videoHeight = videoHeight;
            this.videoFps = videoFps;
            this.success = success;
            this.code = code;
            this.message = message;
        }
    }

    public static final class CursorState {
        public final boolean visible;
        public final int x;
        public final int y;
        public final int displayWidth;
        public final int displayHeight;
        public final double nx;
        public final double ny;
        public final int desktopX;
        public final int desktopY;
        public final String source;

        public CursorState(
            boolean visible,
            int x,
            int y,
            int displayWidth,
            int displayHeight,
            double nx,
            double ny,
            int desktopX,
            int desktopY,
            String source
        ) {
            this.visible = visible;
            this.x = x;
            this.y = y;
            this.displayWidth = displayWidth;
            this.displayHeight = displayHeight;
            this.nx = nx;
            this.ny = ny;
            this.desktopX = desktopX;
            this.desktopY = desktopY;
            this.source = source;
        }
    }

    private final String host;
    private final int port;
    private final Listener listener;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final ExecutorService networkExecutor = Executors.newSingleThreadExecutor(new NamedThreadFactory("SideDock-ControlClient"));
    private final ScheduledExecutorService heartbeatExecutor = Executors.newSingleThreadScheduledExecutor(new NamedThreadFactory("SideDock-Heartbeat"));
    private final AtomicLong seq = new AtomicLong();
    private final Object writeLock = new Object();

    private Socket socket;
    private BufferedWriter writer;
    private ScheduledFuture<?> heartbeatTask;
    private boolean running;
    private boolean manuallyStopped;
    private int reconnectCount;
    private int missedPongs;
    private long sentCount;
    private long receivedCount;
    private long lastStatsEmitAtMs;

    public ControlClient(Listener listener) {
        this("127.0.0.1", 27183, listener);
    }

    public ControlClient(String host, int port, Listener listener) {
        this.host = host;
        this.port = port;
        this.listener = listener;
    }

    public void start() {
        networkExecutor.execute(new Runnable() {
            @Override
            public void run() {
            if (running) {
                    return;
            }
            manuallyStopped = false;
            running = true;
            reconnectCount = 0;
            connectLoop(true);
            }
        });
    }

    public void reconnectNow() {
        networkExecutor.execute(new Runnable() {
            @Override
            public void run() {
            closeSocket();
            if (!running) {
                manuallyStopped = false;
                running = true;
            }
            connectLoop(false);
            }
        });
    }

    public void stop() {
        networkExecutor.execute(new Runnable() {
            @Override
            public void run() {
            manuallyStopped = true;
            running = false;
            if (heartbeatTask != null) {
                heartbeatTask.cancel(true);
            }
            closeSocket();
            emitState(ConnectionState.DISCONNECTED);
            }
        });
    }

    public void shutdown() {
        stop();
        networkExecutor.shutdownNow();
        heartbeatExecutor.shutdownNow();
    }

    public void sendVideoReady(int width, int height, String codec) {
        sendFromAnyThread("video_ready", payload(
            "width", width,
            "height", height,
            "codec", codec
        ));
    }

    public void sendVideoStats(
        long framesDecoded,
        long framesRendered,
        long packetsReceived,
        long decodeErrors,
        long droppedFrames,
        long videoReconnects,
        long roughLatencyMs,
        double decodeFps,
        double renderFps,
        double newFrameFps,
        double repeatFrameFps,
        long newFramesReceived,
        long repeatFramesReceived,
        long blackFramesReceived,
        long keepaliveFramesReceived,
        String lastFrameKind,
        long lastSourceSeq,
        int lastSourceAgeMs,
        double lastReceiveToQueueMs,
        double lastQueueToOutputMs,
        double lastOutputToRenderMs,
        double lastQueueToRenderMs,
        double p50QueueToOutputMs,
        double p95QueueToOutputMs,
        double p99QueueToOutputMs,
        double p50OutputToRenderMs,
        double p95OutputToRenderMs,
        double p99OutputToRenderMs,
        double p50QueueToRenderMs,
        double p95QueueToRenderMs,
        double p99QueueToRenderMs,
        long localPipelineLatencyMs,
        double lastEncodeMs,
        long latencyErrorBoundMs,
        String state
    ) {
        sendFromAnyThread("video_stats", payload(
            "framesDecoded", framesDecoded,
            "framesRendered", framesRendered,
            "packetsReceived", packetsReceived,
            "decodeErrors", decodeErrors,
            "droppedFrames", droppedFrames,
            "videoReconnects", videoReconnects,
            "roughLatencyMs", roughLatencyMs,
            "decodeFps", decodeFps,
            "renderFps", renderFps,
            "newFrameFps", newFrameFps,
            "repeatFrameFps", repeatFrameFps,
            "newFramesReceived", newFramesReceived,
            "repeatFramesReceived", repeatFramesReceived,
            "blackFramesReceived", blackFramesReceived,
            "keepaliveFramesReceived", keepaliveFramesReceived,
            "lastFrameKind", lastFrameKind == null ? "" : lastFrameKind,
            "lastSourceSeq", lastSourceSeq,
            "lastSourceAgeMs", lastSourceAgeMs,
            "receiveToQueueMs", lastReceiveToQueueMs,
            "queueToOutputMs", lastQueueToOutputMs,
            "outputToRenderMs", lastOutputToRenderMs,
            "queueToRenderMs", lastQueueToRenderMs,
            "p50QueueToOutputMs", p50QueueToOutputMs,
            "p95QueueToOutputMs", p95QueueToOutputMs,
            "p99QueueToOutputMs", p99QueueToOutputMs,
            "p50OutputToRenderMs", p50OutputToRenderMs,
            "p95OutputToRenderMs", p95OutputToRenderMs,
            "p99OutputToRenderMs", p99OutputToRenderMs,
            "p50QueueToRenderMs", p50QueueToRenderMs,
            "p95QueueToRenderMs", p95QueueToRenderMs,
            "p99QueueToRenderMs", p99QueueToRenderMs,
            "localPipelineLatencyMs", localPipelineLatencyMs,
            "encodeMs", lastEncodeMs,
            "latencyErrorBoundMs", latencyErrorBoundMs,
            "state", state
        ));
    }

    public void sendVideoError(String code, String message) {
        sendFromAnyThread("video_error", payload(
            "code", code,
            "message", message == null ? "" : message
        ));
    }

    public void sendAudioMicrophoneStatus(
        String state,
        boolean muted,
        boolean stopped,
        boolean permissionGranted,
        int port,
        int sampleRate,
        int channels,
        String message,
        long packets,
        long bytes,
        int peakSample,
        long silentPackets,
        String audioSource
    ) {
        sendFromAnyThread("audio_mic_status", payload(
            "state", state == null ? "" : state,
            "muted", muted,
            "stopped", stopped,
            "permissionGranted", permissionGranted,
            "port", port,
            "sampleRate", sampleRate,
            "channels", channels,
            "message", message == null ? "" : message,
            "packets", packets,
            "bytes", bytes,
            "peakSample", peakSample,
            "silentPackets", silentPackets,
            "audioSource", audioSource == null ? "" : audioSource
        ));
    }

    public void sendAudioSpeakerStatus(
        String state,
        boolean muted,
        boolean stopped,
        int port,
        int sampleRate,
        int channels,
        long packets,
        long bytes,
        String message
    ) {
        sendFromAnyThread("audio_speaker_status", payload(
            "state", state == null ? "" : state,
            "muted", muted,
            "stopped", stopped,
            "port", port,
            "sampleRate", sampleRate,
            "channels", channels,
            "packets", packets,
            "bytes", bytes,
            "message", message == null ? "" : message
        ));
    }

    public void sendAudioRuntimeTelemetry(
        String microphoneState,
        boolean microphoneMuted,
        boolean stopped,
        boolean permissionGranted,
        int port,
        int microphoneSampleRate,
        int microphoneChannels,
        long microphonePackets,
        long microphoneBytes,
        double microphonePacketsPerSecond,
        double microphoneBytesPerSecond,
        int microphonePeakSample,
        long microphoneSilentPackets,
        long microphoneLastPacketUnixMs,
        String audioSource,
        String microphoneLastError,
        String speakerState,
        boolean speakerMuted,
        int speakerSampleRate,
        int speakerChannels,
        long speakerPackets,
        long speakerBytes,
        double speakerPacketsPerSecond,
        double speakerBytesPerSecond,
        int speakerPeakSample,
        long speakerSourceAgeMs,
        long speakerLastPacketUnixMs,
        int speakerPlayState,
        String speakerLastError
    ) {
        long now = System.currentTimeMillis();
        JSONObject microphone = payload(
            "direction", "microphone",
            "state", microphoneState == null ? "" : microphoneState,
            "muted", microphoneMuted,
            "stopped", stopped,
            "permissionGranted", permissionGranted,
            "port", port,
            "sampleRate", microphoneSampleRate,
            "channels", microphoneChannels,
            "bitsPerSample", 16,
            "packets", microphonePackets,
            "bytes", microphoneBytes,
            "packetsPerSecond", microphonePacketsPerSecond,
            "bytesPerSecond", microphoneBytesPerSecond,
            "peakSample", microphonePeakSample,
            "levelPercent", peakSampleToPercent(microphonePeakSample),
            "silentPackets", microphoneSilentPackets,
            "audioSource", audioSource == null ? "" : audioSource,
            "lastPacketUnixMs", microphoneLastPacketUnixMs > 0L ? microphoneLastPacketUnixMs : JSONObject.NULL,
            "lastPacketAgeMs", microphoneLastPacketUnixMs > 0L ? Math.max(0L, now - microphoneLastPacketUnixMs) : JSONObject.NULL,
            "lastError", microphoneLastError == null || microphoneLastError.isEmpty() ? JSONObject.NULL : microphoneLastError
        );
        JSONObject speaker = payload(
            "direction", "speaker",
            "state", speakerState == null ? "" : speakerState,
            "muted", speakerMuted,
            "stopped", stopped,
            "port", port,
            "sampleRate", speakerSampleRate,
            "channels", speakerChannels,
            "bitsPerSample", 16,
            "packets", speakerPackets,
            "bytes", speakerBytes,
            "packetsPerSecond", speakerPacketsPerSecond,
            "bytesPerSecond", speakerBytesPerSecond,
            "peakSample", speakerPeakSample,
            "levelPercent", peakSampleToPercent(speakerPeakSample),
            "sourceAgeMs", speakerSourceAgeMs,
            "approximateLatencyMs", speakerSourceAgeMs,
            "lastPacketUnixMs", speakerLastPacketUnixMs > 0L ? speakerLastPacketUnixMs : JSONObject.NULL,
            "lastPacketAgeMs", speakerLastPacketUnixMs > 0L ? Math.max(0L, now - speakerLastPacketUnixMs) : JSONObject.NULL,
            "playState", speakerPlayState,
            "lastError", speakerLastError == null || speakerLastError.isEmpty() ? JSONObject.NULL : speakerLastError
        );

        sendFromAnyThread("audio_runtime_telemetry", payload(
            "origin", "android",
            "telemetryUnixMs", now,
            "audioPort", port,
            "microphone", microphone,
            "speaker", speaker
        ));
    }

    public void sendAudioTestStatus(
        String testId,
        String kind,
        String status,
        boolean ok,
        String phase,
        String message,
        long startedAtMs,
        long completedAtMs,
        long packetsSent,
        long bytesSent,
        long packetsReceived,
        long bytesReceived,
        int peakSample,
        int peakLevelPercent,
        long silentPackets,
        double silentRatio,
        boolean permissionGranted,
        boolean muted,
        boolean stopped,
        int playState,
        long writeErrors,
        String error
    ) {
        sendFromAnyThread("audio_test_status", payload(
            "testId", testId == null ? "" : testId,
            "kind", kind == null ? "" : kind,
            "status", status == null ? "" : status,
            "ok", ok,
            "phase", phase == null ? "" : phase,
            "message", message == null ? "" : message,
            "startedAtUnixMs", startedAtMs,
            "completedAtUnixMs", completedAtMs,
            "packetsSent", packetsSent,
            "bytesSent", bytesSent,
            "packetsReceived", packetsReceived,
            "bytesReceived", bytesReceived,
            "peakSample", peakSample,
            "peakLevelPercent", peakLevelPercent,
            "silentPackets", silentPackets,
            "silentRatio", silentRatio,
            "permissionGranted", permissionGranted,
            "muted", muted,
            "stopped", stopped,
            "playState", playState,
            "writeErrors", writeErrors,
            "error", error == null ? "" : error
        ));
    }

    public void sendCameraStatus(
        String state,
        String message,
        boolean permissionGranted,
        int port,
        int width,
        int height,
        int fps,
        String codec,
        String facing,
        long packets,
        long bytes,
        long keyFrames,
        long codecConfigPackets,
        long reconnectCount,
        long recoveryAttemptCount,
        long consecutiveFailureCount,
        long lastRecoveryDurationMs,
        String lastDisconnectReason,
        double actualFps,
        double actualKbps,
        double fpsJitter,
        double bitrateJitter
    ) {
        sendFromAnyThread("camera_status", payload(
            "state", state == null ? "" : state,
            "message", message == null ? "" : message,
            "permissionGranted", permissionGranted,
            "port", port,
            "width", width,
            "height", height,
            "fps", fps,
            "codec", codec == null ? "" : codec,
            "facing", facing == null ? "back" : facing,
            "packets", packets,
            "bytes", bytes,
            "keyFrames", keyFrames,
            "codecConfigPackets", codecConfigPackets,
            "reconnectCount", reconnectCount,
            "recoveryAttemptCount", recoveryAttemptCount,
            "consecutiveFailureCount", consecutiveFailureCount,
            "lastRecoveryDurationMs", lastRecoveryDurationMs,
            "lastDisconnectReason", lastDisconnectReason == null ? "" : lastDisconnectReason,
            "actualFps", actualFps,
            "actualKbps", actualKbps,
            "fpsJitter", fpsJitter,
            "bitrateJitter", bitrateJitter
        ));
    }

    private static int peakSampleToPercent(int peakSample) {
        int normalized = Math.max(0, Math.min(Short.MAX_VALUE, peakSample));
        return Math.max(0, Math.min(100, (int) Math.round((normalized * 100.0d) / Short.MAX_VALUE)));
    }

    public void sendCameraCapabilities(JSONObject capabilities) {
        sendFromAnyThread("camera_capabilities", capabilities == null ? new JSONObject() : capabilities);
    }

    public void sendKeyboardInput(
        String action,
        int androidKeyCode,
        int scanCode,
        int metaState,
        int repeatCount
    ) {
        sendFromAnyThread("input_keyboard", payload(
            "action", action,
            "androidKeyCode", androidKeyCode,
            "scanCode", scanCode,
            "metaState", metaState,
            "repeatCount", repeatCount
        ));
    }

    public void sendMouseMoveInput(int dx, int dy) {
        sendFromAnyThread("input_mouse_move", payload(
            "mode", "relative",
            "dx", dx,
            "dy", dy
        ));
    }

    public void sendPointerAbsInput(float nx, float ny, int buttons) {
        sendFromAnyThread("input_pointer_abs", payload(
            "nx", nx,
            "ny", ny,
            "buttons", buttons
        ));
    }

    public void sendMouseButtonInput(String button, String action) {
        sendFromAnyThread("input_mouse_button", payload(
            "button", button,
            "action", action
        ));
    }

    public void sendMouseWheelInput(int dx, int dy) {
        sendFromAnyThread("input_mouse_wheel", payload(
            "dx", dx,
            "dy", dy
        ));
    }

    public void sendInputStats(
        long keyboardEvents,
        long pointerAbsEvents,
        long mouseMoveEvents,
        long localPointerUpdates,
        long mouseButtonEvents,
        long mouseWheelEvents,
        String lastInputType
    ) {
        sendFromAnyThread("input_stats", payload(
            "keyboardEvents", keyboardEvents,
            "pointerAbsEvents", pointerAbsEvents,
            "mouseMoveEvents", mouseMoveEvents,
            "localPointerUpdates", localPointerUpdates,
            "mouseButtonEvents", mouseButtonEvents,
            "mouseWheelEvents", mouseWheelEvents,
            "inputErrors", 0,
            "lastInputType", lastInputType == null ? "none" : lastInputType
        ));
    }

    public void sendDisplayModeChange(int width, int height, int refreshHz) {
        sendFromAnyThread("display_mode_change", payload(
            "width", width,
            "height", height,
            "refreshHz", refreshHz
        ));
    }

    private void connectLoop(boolean firstAttempt) {
        boolean first = firstAttempt;
        while (running && !manuallyStopped) {
            emitState(first ? ConnectionState.CONNECTING : ConnectionState.RECONNECTING);
            first = false;

            try {
                openAndRead();
            } catch (Exception ex) {
                log("连接断开: " + (ex.getMessage() == null ? ex.getClass().getSimpleName() : ex.getMessage()));
            } finally {
                if (heartbeatTask != null) {
                    heartbeatTask.cancel(true);
                }
                closeSocket();
            }

            if (running && !manuallyStopped) {
                reconnectCount += 1;
                long delayMs = Math.min(1000L * reconnectCount, 5000L);
                log("准备重连，第 " + reconnectCount + " 次，" + delayMs + "ms 后重试");
                sleepQuietly(delayMs);
            }
        }
    }

    private void openAndRead() throws Exception {
        Socket nextSocket = new Socket();
        nextSocket.setTcpNoDelay(true);
        nextSocket.connect(new InetSocketAddress(host, port), 3000);
        nextSocket.setSoTimeout(5000);

        socket = nextSocket;
        writer = new BufferedWriter(new OutputStreamWriter(nextSocket.getOutputStream(), StandardCharsets.UTF_8));
        BufferedReader reader = new BufferedReader(new InputStreamReader(nextSocket.getInputStream(), StandardCharsets.UTF_8));
        reconnectCount = 0;
        missedPongs = 0;
        emitState(ConnectionState.CONNECTED);
        log("已连接 " + host + ":" + port);

        send("hello", payload("client", "SideDock.Android", "androidApi", Build.VERSION.SDK_INT));
        send("status", payload("state", "CONNECTED"));
        startHeartbeat();

        while (running && !manuallyStopped && !nextSocket.isClosed()) {
            String line;
            try {
                line = reader.readLine();
            } catch (SocketTimeoutException ex) {
                continue;
            }

            if (line == null) {
                break;
            }
            if (line.trim().isEmpty()) {
                continue;
            }

            handleMessage(ProtocolMessage.fromJsonLine(line));
        }
    }

    private void handleMessage(ProtocolMessage message) {
        receivedCount += 1;
        emitStats();
        log("收到 " + message.type + " seq=" + message.seq);

        switch (message.type) {
            case "hello_ack":
                log("握手完成 heartbeatMs=" + message.payload.optLong("heartbeatMs", 2000));
                if (message.payload.has("videoPort")) {
                    emitVideoStart(
                        message.payload.optInt("videoPort", 27184),
                        message.payload.optInt("videoWidth", 1280),
                        message.payload.optInt("videoHeight", 720),
                        message.payload.optInt("videoFps", 30)
                    );
                }
                if (message.payload.has("audioPort")) {
                    emitAudioConfig(audioConfigFromPayload(message.payload));
                }
                if (message.payload.has("cameraPort") || message.payload.has("cameraEnabled")) {
                    emitCameraConfig(cameraConfigFromHelloPayload(message.payload));
                }
                break;
            case "audio_config":
                emitAudioConfig(audioConfigFromPayload(message.payload));
                break;
            case "audio_test_request":
                emitAudioTestRequest(audioTestRequestFromPayload(message.payload));
                break;
            case "camera_config":
                emitCameraConfig(cameraConfigFromPayload(message.payload));
                break;
            case "ping":
                send("pong", payload("replyTo", message.seq));
                break;
            case "pong":
                missedPongs = 0;
                if (message.payload.has("serverTimeMs")) {
                    long clientSentAtMs = message.payload.optLong("clientSentAtMs", 0L);
                    long receivedAtMs = System.currentTimeMillis();
                    long serverTimeMs = message.payload.optLong("serverTimeMs", 0L);
                    emitClockSync(createClockSync(serverTimeMs, clientSentAtMs, receivedAtMs));
                }
                break;
            case "close":
                log("收到 close，断开连接");
                closeSocket();
                break;
            case "video_start":
                emitVideoStart(
                    message.payload.optInt("videoPort", 27184),
                    message.payload.optInt("width", 1280),
                    message.payload.optInt("height", 720),
                    message.payload.optInt("fps", 30)
                );
                break;
            case "capture_start":
                emitCaptureStatus(new CaptureStatus(
                    "RUNNING",
                    message.payload.optString("source", ""),
                    message.payload.optString("target", ""),
                    0L,
                    0L,
                    0L,
                    0.0,
                    0.0,
                    0.0,
                    0L,
                    0.0,
                    message.payload.optBoolean("gpuPath", false),
                    message.payload.optString("fallback", ""),
                    "",
                    ""
                ));
                break;
            case "capture_stats":
                emitCaptureStatus(new CaptureStatus(
                    "RUNNING",
                    message.payload.optString("source", ""),
                    "",
                    message.payload.optLong("framesCaptured", 0L),
                    message.payload.optLong("framesConverted", 0L),
                    message.payload.optLong("captureErrors", 0L),
                    message.payload.optDouble("avgCaptureMs", 0.0),
                    message.payload.optDouble("avgConvertMs", 0.0),
                    message.payload.optDouble("gpuConvertMs", 0.0),
                    message.payload.optLong("framesDropped", 0L),
                    message.payload.optDouble("lastFrameAgeMs", 0.0),
                    message.payload.optBoolean("gpuPath", false),
                    message.payload.optString("fallback", ""),
                    "",
                    ""
                ));
                break;
            case "encoder_stats":
                emitEncoderStatus(new EncoderStatus(
                    message.payload.optLong("framesGenerated", 0L),
                    message.payload.optLong("framesEncoded", 0L),
                    message.payload.optLong("framesSent", 0L),
                    message.payload.optLong("framesDropped", 0L),
                    message.payload.optDouble("avgEncodeMs", 0.0),
                    message.payload.optDouble("maxEncodeMs", 0.0),
                    message.payload.optDouble("p50EncodeMs", 0.0),
                    message.payload.optDouble("p95EncodeMs", 0.0),
                    message.payload.optDouble("p99EncodeMs", 0.0),
                    message.payload.optDouble("avgSendMs", 0.0),
                    message.payload.optDouble("maxSendMs", 0.0),
                    message.payload.optDouble("p50SendMs", 0.0),
                    message.payload.optDouble("p95SendMs", 0.0),
                    message.payload.optDouble("p99SendMs", 0.0),
                    message.payload.optDouble("outputKbps", 0.0),
                    message.payload.optDouble("streamFps", 0.0),
                    message.payload.optLong("newFramesSent", 0L),
                    message.payload.optLong("repeatFramesSent", 0L),
                    message.payload.optLong("blackFramesSent", 0L),
                    message.payload.optLong("keepaliveFramesSent", 0L),
                    message.payload.optLong("lastKeyFrameSeq", -1L),
                    message.payload.optBoolean("gpuPath", false)
                ));
                break;
            case "capture_error":
                emitCaptureStatus(new CaptureStatus(
                    "ERROR",
                    message.payload.optString("source", ""),
                    "",
                    0L,
                    0L,
                    0L,
                    0.0,
                    0.0,
                    0.0,
                    0L,
                    0.0,
                    message.payload.optBoolean("gpuPath", false),
                    message.payload.optString("fallback", ""),
                    message.payload.optString("code", "CAPTURE_ERROR"),
                    message.payload.optString("message", "")
                ));
                break;
            case "capture_stop":
                emitCaptureStatus(new CaptureStatus(
                    "STOPPED",
                    message.payload.optString("source", ""),
                    "",
                    0L,
                    0L,
                    0L,
                    0.0,
                    0.0,
                    0.0,
                    0L,
                    0.0,
                    message.payload.optBoolean("gpuPath", false),
                    message.payload.optString("fallback", ""),
                    "",
                    message.payload.optString("reason", "")
                ));
                break;
            case "display_layout":
                emitDisplayLayout(new DisplayLayout(
                    message.payload.optString("source", ""),
                    message.payload.optInt("x", 0),
                    message.payload.optInt("y", 0),
                    message.payload.optInt("width", 1280),
                    message.payload.optInt("height", 720),
                    message.payload.optInt("videoWidth", 1280),
                    message.payload.optInt("videoHeight", 720),
                    message.payload.optDouble("scale", 1.0),
                    message.payload.optString("displayName", ""),
                    message.payload.optString("devicePath", "")
                ));
                break;
            case "display_metrics":
                JSONObject videoRect = message.payload.optJSONObject("videoRect");
                emitDisplayMetrics(new DisplayMetrics(
                    message.payload.optString("source", ""),
                    message.payload.optDouble("dpiScale", 1.0),
                    message.payload.optInt("desktopX", 0),
                    message.payload.optInt("desktopY", 0),
                    message.payload.optInt("desktopWidth", 0),
                    message.payload.optInt("desktopHeight", 0),
                    message.payload.optInt("displayX", 0),
                    message.payload.optInt("displayY", 0),
                    message.payload.optInt("displayWidth", 1280),
                    message.payload.optInt("displayHeight", 720),
                    message.payload.optInt("refreshHz", 30),
                    message.payload.optInt("orientation", 0),
                    message.payload.optInt("videoWidth", 1280),
                    message.payload.optInt("videoHeight", 720),
                    videoRect == null ? 0 : videoRect.optInt("x", 0),
                    videoRect == null ? 0 : videoRect.optInt("y", 0),
                    videoRect == null ? message.payload.optInt("videoWidth", 1280) : videoRect.optInt("w", message.payload.optInt("videoWidth", 1280)),
                    videoRect == null ? message.payload.optInt("videoHeight", 720) : videoRect.optInt("h", message.payload.optInt("videoHeight", 720)),
                    message.payload.optString("fitMode", "letterbox")
                ));
                break;
            case "display_mode_changed":
                emitDisplayModeChanged(new DisplayModeChanged(
                    message.payload.optInt("width", 0),
                    message.payload.optInt("height", 0),
                    message.payload.optInt("refreshHz", 0),
                    message.payload.optInt("requestedRefreshHz", 0),
                    message.payload.optInt("displayRefreshHz", message.payload.optInt("refreshHz", 0)),
                    message.payload.optInt("videoWidth", 0),
                    message.payload.optInt("videoHeight", 0),
                    message.payload.optInt("videoFps", 0),
                    message.payload.optBoolean("success", false),
                    message.payload.optString("code", ""),
                    message.payload.optString("message", "")
                ));
                break;
            case "cursor_state":
                emitCursorState(new CursorState(
                    message.payload.optBoolean("visible", true),
                    message.payload.optInt("x", 0),
                    message.payload.optInt("y", 0),
                    message.payload.optInt("displayWidth", 0),
                    message.payload.optInt("displayHeight", 0),
                    message.payload.optDouble("nx", Double.NaN),
                    message.payload.optDouble("ny", Double.NaN),
                    message.payload.optInt("desktopX", 0),
                    message.payload.optInt("desktopY", 0),
                    message.payload.optString("source", "")
                ));
                break;
            case "cursor_shape":
                emitCursorShape(
                    message.payload.optString("kind", "arrow"),
                    message.payload.optBoolean("visible", true)
                );
                break;
            default:
                if (message.payload.length() > 0) {
                    log(message.type + " payload=" + message.payload);
                }
                break;
        }
    }

    private void startHeartbeat() {
        if (heartbeatTask != null) {
            heartbeatTask.cancel(true);
        }
        heartbeatTask = heartbeatExecutor.scheduleAtFixedRate(new Runnable() {
            @Override
            public void run() {
            if (!running || manuallyStopped) {
                return;
            }

            missedPongs += 1;
            if (missedPongs > 3) {
                log("连续 " + (missedPongs - 1) + " 次未收到 pong，主动断开");
                closeSocket();
                return;
            }

            send("ping", payload(
                "missedPongs", missedPongs - 1,
                "clientSentAtMs", System.currentTimeMillis()
            ));
            }
        }, 2, 2, TimeUnit.SECONDS);
    }

    private void send(String type, JSONObject payload) {
        ProtocolMessage message = new ProtocolMessage(
            1,
            type,
            seq.incrementAndGet(),
            System.currentTimeMillis(),
            payload
        );

        try {
            synchronized (writeLock) {
                if (writer == null) {
                    return;
                }
                writer.write(message.toJsonLine());
                writer.newLine();
                writer.flush();
            }
            sentCount += 1;
            emitStatsForType(type);
            if (!isInputMessage(type)) {
                log("发送 " + type + " seq=" + message.seq);
            }
        } catch (Exception ex) {
            log("发送失败: " + ex.getMessage());
            closeSocket();
        }
    }

    private void sendFromAnyThread(String type, JSONObject payload) {
        try {
            heartbeatExecutor.execute(new Runnable() {
                @Override
                public void run() {
                    send(type, payload);
                }
            });
        } catch (RejectedExecutionException ex) {
            log("丢弃发送 " + type + ": " + ex.getMessage());
        }
    }

    private void emitStatsForType(String type) {
        if (!isInputMessage(type)) {
            emitStats();
            return;
        }

        long now = System.currentTimeMillis();
        if (now - lastStatsEmitAtMs >= 250L) {
            lastStatsEmitAtMs = now;
            emitStats();
        }
    }

    private boolean isInputMessage(String type) {
        return type != null && type.startsWith("input_");
    }

    private void closeSocket() {
        try {
            if (socket != null) {
                socket.close();
            }
        } catch (Exception ignored) {
        } finally {
            socket = null;
            writer = null;
        }
    }

    private JSONObject payload(Object... keyValues) {
        JSONObject json = new JSONObject();
        for (int index = 0; index < keyValues.length - 1; index += 2) {
            try {
                json.put(String.valueOf(keyValues[index]), keyValues[index + 1]);
            } catch (Exception ignored) {
            }
        }
        return json;
    }

    private void sleepQuietly(long delayMs) {
        try {
            Thread.sleep(delayMs);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
        }
    }

    private void emitState(ConnectionState state) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onStateChanged(state);
            }
        });
    }

    private void log(String message) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onLog(message);
            }
        });
    }

    private void emitStats() {
        long sent = sentCount;
        long received = receivedCount;
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onStats(sent, received);
            }
        });
    }

    private void emitVideoStart(int videoPort, int width, int height, int fps) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onVideoStart(videoPort, width, height, fps);
            }
        });
    }

    private AudioConfig audioConfigFromPayload(JSONObject payload) {
        int channels = payload.optInt("audioChannels", 1);
        return new AudioConfig(
            payload.optBoolean("audioEnabled", false),
            payload.optBoolean("microphoneEnabled", false),
            payload.optBoolean("speakerEnabled", false),
            payload.optInt("audioPort", 27185),
            payload.optInt("audioSampleRate", 48000),
            channels,
            payload.optInt("microphoneChannels", channels),
            payload.optInt("speakerChannels", channels),
            payload.optInt("audioBitsPerSample", 16)
        );
    }

    private AudioTestRequest audioTestRequestFromPayload(JSONObject payload) {
        return new AudioTestRequest(
            payload.optString("testId", ""),
            payload.optString("kind", ""),
            Math.max(1, payload.optInt("durationMs", 2000)),
            Math.max(1, payload.optInt("timeoutMs", 8000))
        );
    }

    private CameraConfig cameraConfigFromHelloPayload(JSONObject payload) {
        return new CameraConfig(
            payload.optBoolean("cameraEnabled", false),
            payload.optInt("cameraPort", 27186),
            payload.optInt("cameraWidth", 1280),
            payload.optInt("cameraHeight", 720),
            payload.optInt("cameraFps", 30),
            payload.optString("cameraCodec", "video/avc"),
            payload.optString("cameraFacing", "back")
        );
    }

    private CameraConfig cameraConfigFromPayload(JSONObject payload) {
        return new CameraConfig(
            payload.optBoolean("enabled", false),
            payload.optInt("port", 27186),
            payload.optInt("width", 1280),
            payload.optInt("height", 720),
            payload.optInt("fps", 30),
            payload.optString("codec", "video/avc"),
            payload.optString("facing", "back")
        );
    }

    private void emitAudioConfig(AudioConfig config) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onAudioConfig(config);
            }
        });
    }

    private void emitAudioTestRequest(AudioTestRequest request) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onAudioTestRequest(request);
            }
        });
    }

    private void emitCameraConfig(CameraConfig config) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onCameraConfig(config);
            }
        });
    }

    private void emitCaptureStatus(CaptureStatus status) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onCaptureStatus(status);
            }
        });
    }

    private void emitEncoderStatus(EncoderStatus status) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onEncoderStatus(status);
            }
        });
    }

    private void emitDisplayLayout(DisplayLayout layout) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onDisplayLayout(layout);
            }
        });
    }

    private void emitDisplayMetrics(DisplayMetrics metrics) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onDisplayMetrics(metrics);
            }
        });
    }

    private void emitDisplayModeChanged(DisplayModeChanged mode) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onDisplayModeChanged(mode);
            }
        });
    }

    private void emitCursorShape(String kind, boolean visible) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onCursorShape(kind, visible);
            }
        });
    }

    private void emitCursorState(CursorState state) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onCursorState(state);
            }
        });
    }

    private ClockSync createClockSync(long serverTimeMs, long clientSentAtMs, long clientReceivedAtMs) {
        if (serverTimeMs <= 0L || clientSentAtMs <= 0L || clientReceivedAtMs <= 0L) {
            long now = System.currentTimeMillis();
            return new ClockSync(0L, now, now, 0L, Long.MAX_VALUE, Long.MAX_VALUE);
        }

        long sentAtMs = clientSentAtMs > 0L ? clientSentAtMs : clientReceivedAtMs;
        long receivedAtMs = Math.max(clientReceivedAtMs, sentAtMs);
        long rttMs = Math.max(0L, receivedAtMs - sentAtMs);
        long midpointMs = sentAtMs + (rttMs / 2L);
        long offsetMs = serverTimeMs - midpointMs;
        long errorBoundMs = (rttMs + 1L) / 2L;
        return new ClockSync(serverTimeMs, sentAtMs, receivedAtMs, offsetMs, rttMs, errorBoundMs);
    }

    private void emitClockSync(ClockSync clockSync) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onClockSync(clockSync);
            }
        });
    }

    private static final class NamedThreadFactory implements ThreadFactory {
        private final String name;

        private NamedThreadFactory(String name) {
            this.name = name;
        }

        @Override
        public Thread newThread(Runnable runnable) {
            Thread thread = new Thread(runnable, name);
            thread.setDaemon(true);
            return thread;
        }
    }
}
