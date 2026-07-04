package com.sidedock.client;

import android.Manifest;
import android.app.Activity;
import android.app.KeyguardManager;
import android.content.Context;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.content.res.Configuration;
import android.graphics.drawable.GradientDrawable;
import android.media.MediaCodecInfo;
import android.media.MediaCodecList;
import android.os.Build;
import android.os.Handler;
import android.os.Bundle;
import android.os.Looper;
import android.util.Log;
import android.view.Choreographer;
import android.view.Display;
import android.view.Gravity;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewConfiguration;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.TextView;
import java.text.SimpleDateFormat;
import java.util.ArrayDeque;
import java.util.Date;
import java.util.Locale;

public final class MainActivity extends Activity implements ControlClient.Listener, VideoClient.Listener, AudioCaptureClient.Listener, SurfaceHolder.Callback, InputCollector.Listener {
    private static final String TAG = "SideDock";
    private static final int DEFAULT_VIDEO_PORT = 27184;
    private static final int DEFAULT_AUDIO_PORT = 27185;
    private static final int DEFAULT_VIDEO_WIDTH = 1280;
    private static final int DEFAULT_VIDEO_HEIGHT = 720;
    private static final int DEFAULT_VIDEO_FPS = 120;
    private static final int DEFAULT_AUDIO_SAMPLE_RATE = 48000;
    private static final int DEFAULT_AUDIO_CHANNELS = 1;
    private static final int REQUEST_RECORD_AUDIO = 5010;
    private static final String VIDEO_CODEC_AVC = "video/avc";
    private static final String ERROR_DECODER_UNSUPPORTED = "DECODER_UNSUPPORTED";
    private static final int OVERLAY_MODE_DETAILED = 0;
    private static final int OVERLAY_MODE_COMPACT = 1;
    private static final int OVERLAY_MODE_HIDDEN = 2;
    private static final long OVERLAY_TAP_MAX_DURATION_MS = 500L;
    private static final String[] RESOLUTION_LABELS = new String[] { "720p", "1080p", "2K" };
    private static final int[][] RESOLUTION_PRESETS = new int[][] {
        { 1280, 720 },
        { 1920, 1080 },
        { 2560, 1440 }
    };
    private static final int[] REFRESH_PRESETS = new int[] { 30, 60, 120 };
    private static final String AUDIO_PREFS_NAME = "audio_controls";
    private static final String AUDIO_PREF_MIC_MUTED = "mic_muted";
    private static final String AUDIO_PREF_SPEAKER_MUTED = "speaker_muted";
    private static final String AUDIO_PREF_STOPPED = "stopped";

    private ControlClient controlClient;
    private VideoClient videoClient;
    private AudioCaptureClient audioCaptureClient;
    private InputCollector inputCollector;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final Runnable pointerAbsFlushRunnable = new Runnable() {
        @Override
        public void run() {
            flushPendingPointerAbs();
        }
    };
    private final Choreographer.FrameCallback displayFrameCallback = new Choreographer.FrameCallback() {
        @Override
        public void doFrame(long frameTimeNanos) {
            if (!displayFrameCallbacksActive) {
                return;
            }

            recordDisplayFrameCallback(frameTimeNanos);
            if (displayFrameCallbacksActive) {
                Choreographer.getInstance().postFrameCallback(this);
            }
        }
    };
    private FrameLayout rootView;
    private SurfaceView surfaceView;
    private TextView overlayText;
    private TextView modeToggleText;
    private LinearLayout modePanel;
    private LinearLayout audioPanel;
    private TextView audioMicStatusText;
    private TextView audioSpeakerStatusText;
    private TextView audioPermissionText;
    private TextView audioHintText;
    private TextView micMuteButton;
    private TextView speakerMuteButton;
    private TextView stopAudioButton;
    private FrameLayout connectionStatusLayer;
    private ProgressBar connectionStatusProgress;
    private TextView connectionStatusTitle;
    private TextView connectionStatusDetail;
    private TextView connectionStatusHint;
    private final TextView[] resolutionButtons = new TextView[RESOLUTION_LABELS.length];
    private final TextView[] refreshButtons = new TextView[REFRESH_PRESETS.length];
    private final ArrayDeque<String> logLines = new ArrayDeque<>();
    private final SimpleDateFormat timeFormatter = new SimpleDateFormat("HH:mm:ss", Locale.ROOT);
    private Surface activeSurface;
    private boolean surfaceReady;
    private int videoPort = DEFAULT_VIDEO_PORT;
    private int videoWidth = DEFAULT_VIDEO_WIDTH;
    private int videoHeight = DEFAULT_VIDEO_HEIGHT;
    private int videoFps = DEFAULT_VIDEO_FPS;
    private boolean videoStartReceived;
    private ConnectionState controlConnectionState = ConnectionState.DISCONNECTED;
    private String controlState = "已断开";
    private String videoState = "STOPPED";
    private String lastVideoError = "";
    private long controlSent;
    private long controlReceived;
    private long serverTimeOffsetMs;
    private boolean serverTimeOffsetInitialized;
    private long clockSyncRttMs = Long.MAX_VALUE;
    private long clockSyncErrorBoundMs = Long.MAX_VALUE;
    private long lastRenderedFramesSeen;
    private long lastVideoStatsSummaryLogAtMs;
    private float displayRefreshHz;
    private double displayCallbackFps;
    private boolean displayFrameCallbacksActive;
    private long displayFrameCallbacks;
    private long lastDisplayRateSnapshotNanos;
    private long lastDisplayRateFrameCallbacks;
    private boolean waitingForVideoFrame = true;
    private VideoClient.VideoStats lastVideoStats;
    private InputCollector.InputStats lastInputStats;
    private ControlClient.CaptureStatus lastCaptureStatus;
    private ControlClient.EncoderStatus lastEncoderStatus;
    private ControlClient.DisplayLayout lastDisplayLayout;
    private ControlClient.DisplayMetrics lastDisplayMetrics;
    private ControlClient.DisplayModeChanged lastDisplayModeChanged;
    private ControlClient.CursorState lastCursorState;
    private String cursorKind = "arrow";
    private int videoRectLeft;
    private int videoRectTop;
    private int videoRectWidth = DEFAULT_VIDEO_WIDTH;
    private int videoRectHeight = DEFAULT_VIDEO_HEIGHT;
    private int contentRectLeft;
    private int contentRectTop;
    private int contentRectWidth = DEFAULT_VIDEO_WIDTH;
    private int contentRectHeight = DEFAULT_VIDEO_HEIGHT;
    private int surfaceFixedWidth = -1;
    private int surfaceFixedHeight = -1;
    private int overlayMode = OVERLAY_MODE_DETAILED;
    private int selectedModeWidth = DEFAULT_VIDEO_WIDTH;
    private int selectedModeHeight = DEFAULT_VIDEO_HEIGHT;
    private int selectedModeRefresh = DEFAULT_VIDEO_FPS;
    private float overlayTapDownX;
    private float overlayTapDownY;
    private long overlayTapDownAtMs;
    private long lastCursorDebugAtMs;
    private boolean overlayTapCandidate;
    private boolean audioStopped;
    private boolean micMuted;
    private boolean speakerMuted;
    private boolean hostAudioEnabled = true;
    private boolean hostMicrophoneEnabled = true;
    private boolean hostSpeakerEnabled;
    private boolean micPermissionRequestedInSession;
    private int audioPort = DEFAULT_AUDIO_PORT;
    private int audioSampleRate = DEFAULT_AUDIO_SAMPLE_RATE;
    private int audioChannels = DEFAULT_AUDIO_CHANNELS;
    private int audioSpeakerChannels = 2;
    private String microphoneRuntimeState = "waiting_device";
    private String speakerRuntimeState = "waiting_device";
    private String lastAudioHint = "等待电脑音频状态。";
    private long speakerPacketsReceived;
    private long speakerBytesReceived;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        keepWindowReadyForVideoSurface();

        controlClient = new ControlClient(this);
        videoClient = new VideoClient(this);
        audioCaptureClient = new AudioCaptureClient(this, this);
        inputCollector = new InputCollector(this);
        loadAudioPreferences();
        setContentView(buildContentView());
        enterImmersiveMode();
        useNativePointerIcon(getWindow().getDecorView());
        controlClient.start();
    }

    private void keepWindowReadyForVideoSurface() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) {
            setShowWhenLocked(true);
            setTurnScreenOn(true);
        } else {
            getWindow().addFlags(
                WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED
                    | WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON
            );
        }
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        KeyguardManager keyguardManager = (KeyguardManager) getSystemService(Context.KEYGUARD_SERVICE);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && keyguardManager != null) {
            keyguardManager.requestDismissKeyguard(this, null);
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        enterImmersiveMode();
        startDisplayFrameSampling();
        applyDisplayTimingHints();
        maybeStartVideo();
    }

    @Override
    protected void onPause() {
        stopDisplayFrameSampling();
        super.onPause();
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) {
            enterImmersiveMode();
            useNativePointerIcon(getWindow().getDecorView());
            updateDisplayRefreshHz();
            applyDisplayTimingHints();
        }
    }

    @Override
    public void onConfigurationChanged(Configuration newConfig) {
        super.onConfigurationChanged(newConfig);
        updateVideoRectForSurfaceView();
        updateDisplayRefreshHz();
        addLog("Configuration changed, recalculated video rect");
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != REQUEST_RECORD_AUDIO) {
            return;
        }

        boolean granted = grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED;
        lastAudioHint = granted
            ? "麦克风权限已允许，正在准备采集。"
            : "需要允许麦克风权限后才能作为电脑麦克风。";
        applyAudioCaptureIntent(lastAudioHint);
        updateOverlay();
    }

    @Override
    protected void onDestroy() {
        mainHandler.removeCallbacks(pointerAbsFlushRunnable);
        stopDisplayFrameSampling();
        clearDisplayTimingHints();
        audioCaptureClient.stop();
        videoClient.stop();
        controlClient.shutdown();
        super.onDestroy();
    }

    @Override
    public void surfaceCreated(SurfaceHolder holder) {
        activeSurface = holder.getSurface();
        surfaceReady = activeSurface != null && activeSurface.isValid();
        waitingForVideoFrame = true;
        updateDisplayRefreshHz();
        applyDisplayTimingHints();
        sendVideoReadyIfSurfaceReady();
        addLog("Surface 已创建，准备接收视频");
        Log.i(TAG, "surfaceCreated ready=" + surfaceReady);
        updateOverlay();
    }

    @Override
    public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
        activeSurface = holder.getSurface();
        surfaceReady = activeSurface != null && activeSurface.isValid();
        updateVideoRectForSurfaceView();
        updateDisplayRefreshHz();
        applyDisplayTimingHints();
        sendVideoReadyIfSurfaceReady();
        Log.i(TAG, "surfaceChanged ready=" + surfaceReady + " size=" + width + "x" + height);
    }

    @Override
    public void surfaceDestroyed(SurfaceHolder holder) {
        surfaceReady = false;
        clearDisplayTimingHints();
        activeSurface = null;
        waitingForVideoFrame = true;
        videoClient.stop();
        addLog("Surface 已销毁，停止视频通道");
        Log.i(TAG, "surfaceDestroyed");
    }

    @Override
    public void onStateChanged(ConnectionState state) {
        controlConnectionState = state;
        controlState = labelFor(state);
        if (state != ConnectionState.CONNECTED) {
            waitingForVideoFrame = true;
        }
        if (state == ConnectionState.CONNECTED) {
            sendVideoReadyIfSurfaceReady();
            applyAudioCaptureIntent("电脑音频已连接。");
        } else {
            audioCaptureClient.stop();
            microphoneRuntimeState = state == ConnectionState.RECONNECTING ? "reconnecting" : "waiting_device";
            speakerRuntimeState = state == ConnectionState.RECONNECTING ? "reconnecting" : "waiting_device";
            publishAudioMicrophoneStatus(audioStatusWireState(currentMicrophoneAudioStatus()), audioStatusText(AudioEndpoint.MICROPHONE, currentMicrophoneAudioStatus()));
            publishAudioSpeakerStatus(audioStatusWireState(AudioEndpoint.SPEAKER, currentSpeakerAudioStatus()), audioStatusText(AudioEndpoint.SPEAKER, currentSpeakerAudioStatus()));
        }
        updateOverlay();
    }

    @Override
    public void onLog(String message) {
        addLog("控制 " + message);
    }

    @Override
    public void onStats(long sent, long received) {
        controlSent = sent;
        controlReceived = received;
        updateOverlay();
    }

    @Override
    public void onVideoStart(int port, int width, int height, int fps) {
        videoPort = port;
        videoWidth = width;
        videoHeight = height;
        videoFps = Math.max(1, fps);
        waitingForVideoFrame = true;
        lastVideoError = "";
        lastVideoStats = null;
        lastRenderedFramesSeen = 0L;
        lastVideoStatsSummaryLogAtMs = 0L;
        videoStartReceived = true;
        selectedModeWidth = videoWidth;
        selectedModeHeight = videoHeight;
        selectedModeRefresh = normalizeObservedRefresh(videoFps);
        applySurfaceFixedSize(videoWidth, videoHeight);
        updateVideoRectForSurfaceView();
        updateModeControls();
        addLog("收到 video_start " + videoWidth + "x" + videoHeight + "@" + videoFps + " port=" + videoPort);
        Log.i(TAG, "onVideoStart port=" + videoPort + " size=" + videoWidth + "x" + videoHeight + " fps=" + videoFps);
        if (shouldRejectAvcBeforeStart(videoWidth, videoHeight, videoFps)) {
            handlePreflightDecoderUnsupported();
            return;
        }

        maybeStartVideo();
    }

    @Override
    public void onAudioConfig(ControlClient.AudioConfig config) {
        hostAudioEnabled = config.audioEnabled;
        hostMicrophoneEnabled = config.microphoneEnabled;
        hostSpeakerEnabled = config.speakerEnabled;
        audioPort = config.port > 0 ? config.port : DEFAULT_AUDIO_PORT;
        audioSampleRate = config.sampleRate > 0 ? config.sampleRate : DEFAULT_AUDIO_SAMPLE_RATE;
        audioChannels = config.microphoneChannels > 0 ? config.microphoneChannels : DEFAULT_AUDIO_CHANNELS;
        audioSpeakerChannels = config.speakerChannels > 0 ? config.speakerChannels : 2;
        lastAudioHint = hostAudioEnabled
            ? "正在准备电脑音频。"
            : "电脑未启用 SideDock 音频。";
        applyAudioCaptureIntent(lastAudioHint);
        updateOverlay();
    }

    @Override
    public void onAudioCaptureState(String state, String message) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                microphoneRuntimeState = state == null ? "unavailable" : state;
                if (message != null && !message.isEmpty()) {
                    lastAudioHint = message;
                }
                publishAudioMicrophoneStatus(microphoneRuntimeState, lastAudioHint);
                updateOverlay();
            }
        });
    }

    @Override
    public void onAudioCaptureStats(long packetsSent, long bytesSent) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                publishAudioMicrophoneStatus("capturing", "麦克风正在采集中。");
            }
        });
    }

    @Override
    public void onAudioPlaybackState(String state, String message) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                speakerRuntimeState = state == null ? "unavailable" : state;
                if (message != null && !message.isEmpty()) {
                    lastAudioHint = message;
                }
                publishAudioSpeakerStatus(speakerRuntimeState, lastAudioHint);
                updateOverlay();
            }
        });
    }

    @Override
    public void onAudioPlaybackStats(long packetsReceived, long bytesReceived) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                speakerPacketsReceived = packetsReceived;
                speakerBytesReceived = bytesReceived;
                publishAudioSpeakerStatus(speakerMuted ? "muted" : "playing",
                    speakerMuted ? "本机音响已静音。" : "正在播放电脑声音。");
            }
        });
    }

    @Override
    public void onClockSync(ControlClient.ClockSync clockSync) {
        if (clockSync.errorBoundMs == Long.MAX_VALUE) {
            return;
        }

        boolean betterSample = !serverTimeOffsetInitialized || clockSync.rttMs <= clockSyncRttMs + 2L;
        if (betterSample) {
            serverTimeOffsetMs = serverTimeOffsetInitialized
                ? Math.round((serverTimeOffsetMs * 0.85) + (clockSync.offsetMs * 0.15))
                : clockSync.offsetMs;
            clockSyncRttMs = serverTimeOffsetInitialized
                ? Math.min(clockSyncRttMs, clockSync.rttMs)
                : clockSync.rttMs;
            clockSyncErrorBoundMs = serverTimeOffsetInitialized
                ? Math.min(clockSyncErrorBoundMs, clockSync.errorBoundMs)
                : clockSync.errorBoundMs;
            serverTimeOffsetInitialized = true;
        }
        videoClient.setServerTimeOffsetMs(serverTimeOffsetMs, clockSyncErrorBoundMs);
        updateOverlay();
    }

    @Override
    public void onCaptureStatus(ControlClient.CaptureStatus status) {
        lastCaptureStatus = mergeCaptureStatus(lastCaptureStatus, status);
        if ("ERROR".equals(status.state)) {
            addLog("采集错误 " + status.errorCode + ": " + status.errorMessage);
        } else if ("RUNNING".equals(status.state) && status.framesCaptured == 0L) {
            addLog("采集开始 source=" + status.source);
        } else {
            updateOverlay();
        }
    }

    @Override
    public void onEncoderStatus(ControlClient.EncoderStatus status) {
        lastEncoderStatus = status;
        updateOverlay();
    }

    @Override
    public void onDisplayLayout(ControlClient.DisplayLayout layout) {
        lastDisplayLayout = layout;
        if (layout.videoWidth > 0 && layout.videoHeight > 0) {
            videoWidth = layout.videoWidth;
            videoHeight = layout.videoHeight;
            selectedModeWidth = videoWidth;
            selectedModeHeight = videoHeight;
            applySurfaceFixedSize(videoWidth, videoHeight);
            updateVideoRectForSurfaceView();
        }
        updateModeControls();
        addLog("显示布局 " + layout.width + "x" + layout.height + " @(" + layout.x + "," + layout.y + ")");
        updateOverlay();
    }

    @Override
    public void onDisplayMetrics(ControlClient.DisplayMetrics metrics) {
        lastDisplayMetrics = metrics;
        if (metrics.videoWidth > 0 && metrics.videoHeight > 0) {
            videoWidth = metrics.videoWidth;
            videoHeight = metrics.videoHeight;
            selectedModeWidth = videoWidth;
            selectedModeHeight = videoHeight;
            applySurfaceFixedSize(videoWidth, videoHeight);
        }
        updateVideoRectForSurfaceView();
        updateModeControls();
        addLog("Display metrics dpi=" + String.format(Locale.ROOT, "%.2f", metrics.dpiScale)
            + " display=" + metrics.displayWidth + "x" + metrics.displayHeight
            + " mode=" + metrics.fitMode);
        updateOverlay();
    }

    @Override
    public void onDisplayModeChanged(ControlClient.DisplayModeChanged mode) {
        lastDisplayModeChanged = mode;
        if (mode.videoWidth > 0 && mode.videoHeight > 0) {
            selectedModeWidth = mode.videoWidth;
            selectedModeHeight = mode.videoHeight;
            selectedModeRefresh = mode.videoFps > 0 ? normalizeObservedRefresh(mode.videoFps) : selectedModeRefresh;
            videoWidth = selectedModeWidth;
            videoHeight = selectedModeHeight;
            videoFps = selectedModeRefresh;
            waitingForVideoFrame = true;
            if (surfaceView != null) {
                applySurfaceFixedSize(videoWidth, videoHeight);
            }
            updateVideoRectForSurfaceView();
            videoClient.stop();
            sendVideoReadyIfSurfaceReady();
        } else if (mode.success) {
            selectedModeWidth = mode.width > 0 ? mode.width : selectedModeWidth;
            selectedModeHeight = mode.height > 0 ? mode.height : selectedModeHeight;
        }
        addLog("Display mode " + (mode.success ? "changed " : "failed ")
            + mode.width + "x" + mode.height + "@" + mode.refreshHz
            + " video=" + videoWidth + "x" + videoHeight + "@" + videoFps
            + (mode.message.length() == 0 ? "" : " " + mode.message));
        updateModeControls();
        updateOverlay();
    }

    @Override
    public void onCursorShape(String kind, boolean visible) {
        cursorKind = kind == null || kind.length() == 0 ? "arrow" : kind;
        updateOverlay();
    }

    @Override
    public void onCursorState(ControlClient.CursorState state) {
        lastCursorState = state;
        updateCursorDebugOverlay();
    }

    @Override
    public void onLocalPointerPreview(float viewX, float viewY) {
        schedulePointerAbsFlush();
    }

    @Override
    public void onLocalPointerExit() {
        schedulePointerAbsFlush();
    }

    @Override
    public void onVideoState(String state) {
        videoState = state;
        if ("CONNECTED".equals(state)) {
            lastVideoError = "";
        }
        if (!"CONNECTED".equals(state)) {
            waitingForVideoFrame = true;
        }
        updateOverlay();
    }

    @Override
    public void onVideoLog(String message) {
        addLog("视频 " + message);
    }

    @Override
    public void onVideoStats(VideoClient.VideoStats stats) {
        lastVideoStats = stats;
        if (stats.framesRendered > lastRenderedFramesSeen) {
            lastRenderedFramesSeen = stats.framesRendered;
            waitingForVideoFrame = false;
            lastVideoError = "";
        }
        controlClient.sendVideoStats(
            stats.framesDecoded,
            stats.framesRendered,
            stats.packetsReceived,
            stats.decodeErrors,
            stats.droppedFrames,
            stats.reconnects,
            stats.roughLatencyMs,
            stats.decodeFps,
            stats.renderFps,
            stats.newFrameFps,
            stats.repeatFrameFps,
            stats.newFramesReceived,
            stats.repeatFramesReceived,
            stats.blackFramesReceived,
            stats.keepaliveFramesReceived,
            stats.lastFrameKind,
            stats.lastSourceSeq,
            stats.lastSourceAgeMs,
            stats.lastReceiveToQueueMs,
            stats.lastQueueToOutputMs,
            stats.lastOutputToRenderMs,
            stats.lastQueueToRenderMs,
            stats.p50QueueToOutputMs,
            stats.p95QueueToOutputMs,
            stats.p99QueueToOutputMs,
            stats.p50OutputToRenderMs,
            stats.p95OutputToRenderMs,
            stats.p99OutputToRenderMs,
            stats.p50QueueToRenderMs,
            stats.p95QueueToRenderMs,
            stats.p99QueueToRenderMs,
            stats.localPipelineLatencyMs,
            stats.lastEncodeMs,
            stats.latencyErrorBoundMs,
            stats.state
        );
        logVideoStatsSummary(stats);
        updateOverlay();
    }

    @Override
    public void onVideoError(String code, String message) {
        lastVideoError = code + ": " + message;
        waitingForVideoFrame = true;
        controlClient.sendVideoError(code, message);
        addLog("视频错误 " + code + ": " + message);
    }

    @Override
    public void onKeyboardInput(String action, int androidKeyCode, int scanCode, int metaState, int repeatCount) {
        controlClient.sendKeyboardInput(action, androidKeyCode, scanCode, metaState, repeatCount);
    }

    @Override
    public void onPointerAbsInput(float nx, float ny, int buttons, float viewX, float viewY) {
        controlClient.sendPointerAbsInput(nx, ny, buttons);
        schedulePointerAbsFlush();
    }

    @Override
    public void onMouseMoveInput(int dx, int dy) {
        controlClient.sendMouseMoveInput(dx, dy);
    }

    @Override
    public void onMouseButtonInput(String button, String action) {
        controlClient.sendMouseButtonInput(button, action);
    }

    @Override
    public void onMouseWheelInput(int dx, int dy) {
        controlClient.sendMouseWheelInput(dx, dy);
    }

    @Override
    public void onInputStats(InputCollector.InputStats stats) {
        lastInputStats = stats;
        controlClient.sendInputStats(
            stats.keyboardEvents,
            stats.pointerAbsEvents,
            stats.mouseMoveEvents,
            stats.localPointerUpdates,
            stats.mouseButtonEvents,
            stats.mouseWheelEvents,
            stats.lastInputType
        );
        updateOverlay();
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        if (inputCollector != null && inputCollector.handleKeyEvent(event)) {
            return true;
        }

        return super.dispatchKeyEvent(event);
    }

    @Override
    public boolean dispatchGenericMotionEvent(MotionEvent event) {
        if (inputCollector != null && inputCollector.handleGenericMotionEvent(event)) {
            schedulePointerAbsFlush();
            return true;
        }

        return super.dispatchGenericMotionEvent(event);
    }

    @Override
    public boolean dispatchTouchEvent(MotionEvent event) {
        if (isPointInsideView(audioPanel, event)) {
            return super.dispatchTouchEvent(event);
        }

        if (!isMouseLikeEvent(event) && isModeControlTouch(event)) {
            return super.dispatchTouchEvent(event);
        }

        if (handleOverlayToggleTouch(event)) {
            return true;
        }

        if (inputCollector != null && inputCollector.handleTouchEvent(event)) {
            schedulePointerAbsFlush();
            return true;
        }

        return super.dispatchTouchEvent(event);
    }

    private boolean isModeControlTouch(MotionEvent event) {
        return isPointInsideView(modeToggleText, event) || isPointInsideView(modePanel, event);
    }

    private boolean handleOverlayToggleTouch(MotionEvent event) {
        if (isMouseLikeEvent(event)) {
            return false;
        }

        if (isModeControlTouch(event)) {
            overlayTapCandidate = false;
            return false;
        }

        int action = event.getActionMasked();
        if (action == MotionEvent.ACTION_DOWN) {
            overlayTapDownX = event.getX();
            overlayTapDownY = event.getY();
            overlayTapDownAtMs = event.getEventTime();
            overlayTapCandidate = event.getPointerCount() == 1;
            return true;
        }

        if (!overlayTapCandidate) {
            return action == MotionEvent.ACTION_MOVE
                || action == MotionEvent.ACTION_UP
                || action == MotionEvent.ACTION_CANCEL;
        }

        if (action == MotionEvent.ACTION_MOVE) {
            if (movedBeyondTapSlop(event)) {
                overlayTapCandidate = false;
            }
            return true;
        }

        if (action == MotionEvent.ACTION_UP) {
            boolean isTap = event.getEventTime() - overlayTapDownAtMs <= OVERLAY_TAP_MAX_DURATION_MS
                && !movedBeyondTapSlop(event);
            overlayTapCandidate = false;
            if (isTap) {
                toggleOverlayControls();
            }
            return true;
        }

        if (action == MotionEvent.ACTION_CANCEL) {
            overlayTapCandidate = false;
            return true;
        }

        return true;
    }

    private boolean movedBeyondTapSlop(MotionEvent event) {
        float dx = event.getX() - overlayTapDownX;
        float dy = event.getY() - overlayTapDownY;
        int slop = ViewConfiguration.get(this).getScaledTouchSlop();
        return dx * dx + dy * dy > slop * slop;
    }

    private void toggleOverlayControls() {
        overlayMode = overlayMode == OVERLAY_MODE_HIDDEN ? OVERLAY_MODE_DETAILED : OVERLAY_MODE_HIDDEN;
        updateOverlay();
    }

    private boolean isMouseLikeEvent(MotionEvent event) {
        if (event.isFromSource(InputDevice.SOURCE_MOUSE)) {
            return true;
        }

        return event.getPointerCount() > 0 && event.getToolType(0) == MotionEvent.TOOL_TYPE_MOUSE;
    }

    private boolean isPointInsideView(View view, MotionEvent event) {
        if (view == null || view.getVisibility() != View.VISIBLE) {
            return false;
        }

        int[] location = new int[2];
        view.getLocationOnScreen(location);
        float rawX = event.getRawX();
        float rawY = event.getRawY();
        return rawX >= location[0]
            && rawX <= location[0] + view.getWidth()
            && rawY >= location[1]
            && rawY <= location[1] + view.getHeight();
    }

    private View buildContentView() {
        float density = getResources().getDisplayMetrics().density;

        rootView = new FrameLayout(this);
        rootView.setBackgroundColor(0xFF050607);
        rootView.setLayoutParams(new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.MATCH_PARENT
        ));
        useNativePointerIcon(rootView);
        rootView.addOnLayoutChangeListener(new View.OnLayoutChangeListener() {
            @Override
            public void onLayoutChange(
                View v,
                int left,
                int top,
                int right,
                int bottom,
                int oldLeft,
                int oldTop,
                int oldRight,
                int oldBottom
            ) {
                if (right - left != oldRight - oldLeft || bottom - top != oldBottom - oldTop) {
                    rootView.post(new Runnable() {
                        @Override
                        public void run() {
                            updateVideoRectForSurfaceView();
                        }
                    });
                }
            }
        });

        surfaceView = new SurfaceView(this);
        surfaceView.setZOrderOnTop(false);
        surfaceView.setZOrderMediaOverlay(false);
        applySurfaceFixedSize(videoWidth, videoHeight);
        surfaceView.getHolder().addCallback(this);
        surfaceView.setFocusable(true);
        surfaceView.setFocusableInTouchMode(true);
        useNativePointerIcon(surfaceView);
        surfaceView.requestFocus();
        rootView.addView(surfaceView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.MATCH_PARENT
        ));

        connectionStatusLayer = createConnectionStatusLayer(density);
        rootView.addView(connectionStatusLayer, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.MATCH_PARENT
        ));

        modeToggleText = createModeToggle(density);
        FrameLayout.LayoutParams modeToggleParams = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT,
            dp(44, density),
            Gravity.END | Gravity.TOP
        );
        modeToggleParams.setMargins(dp(12, density), dp(12, density), dp(12, density), dp(12, density));
        rootView.addView(modeToggleText, modeToggleParams);

        modePanel = createModePanel(density);
        FrameLayout.LayoutParams modePanelParams = new FrameLayout.LayoutParams(
            dp(268, density),
            ViewGroup.LayoutParams.WRAP_CONTENT,
            Gravity.END | Gravity.TOP
        );
        modePanelParams.setMargins(dp(12, density), dp(64, density), dp(12, density), dp(12, density));
        rootView.addView(modePanel, modePanelParams);
        updateModeControls();

        audioPanel = createAudioPanel(density);
        FrameLayout.LayoutParams audioPanelParams = new FrameLayout.LayoutParams(
            dp(316, density),
            ViewGroup.LayoutParams.WRAP_CONTENT,
            Gravity.START | Gravity.BOTTOM
        );
        audioPanelParams.setMargins(dp(12, density), dp(12, density), dp(12, density), dp(12, density));
        rootView.addView(audioPanel, audioPanelParams);
        updateAudioPanel();

        updateOverlay();
        rootView.post(new Runnable() {
            @Override
            public void run() {
                updateVideoRectForSurfaceView();
            }
        });
        return rootView;
    }

    private FrameLayout createConnectionStatusLayer(float density) {
        FrameLayout layer = new FrameLayout(this);
        layer.setBackgroundColor(0xCC050607);
        layer.setVisibility(View.VISIBLE);
        useNativePointerIcon(layer);

        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setGravity(Gravity.CENTER_HORIZONTAL);
        panel.setPadding(dp(22, density), dp(22, density), dp(22, density), dp(22, density));
        panel.setBackground(makeRoundedBackground(0xE6111820, 0xFF314555, dp(8, density)));
        useNativePointerIcon(panel);

        connectionStatusProgress = new ProgressBar(this);
        connectionStatusProgress.setIndeterminate(true);
        useNativePointerIcon(connectionStatusProgress);
        panel.addView(connectionStatusProgress, new LinearLayout.LayoutParams(
            dp(34, density),
            dp(34, density)
        ));

        connectionStatusTitle = new TextView(this);
        connectionStatusTitle.setTextColor(0xFFFFFFFF);
        connectionStatusTitle.setTextSize(20f);
        connectionStatusTitle.setGravity(Gravity.CENTER);
        connectionStatusTitle.setPadding(0, dp(14, density), 0, 0);
        useNativePointerIcon(connectionStatusTitle);
        panel.addView(connectionStatusTitle, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        ));

        connectionStatusDetail = new TextView(this);
        connectionStatusDetail.setTextColor(0xFFD7E1E6);
        connectionStatusDetail.setTextSize(14f);
        connectionStatusDetail.setGravity(Gravity.CENTER);
        connectionStatusDetail.setPadding(0, dp(10, density), 0, 0);
        connectionStatusDetail.setLineSpacing(dp(2, density), 1.0f);
        useNativePointerIcon(connectionStatusDetail);
        panel.addView(connectionStatusDetail, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        ));

        connectionStatusHint = new TextView(this);
        connectionStatusHint.setTextColor(0xFF9DAEB8);
        connectionStatusHint.setTextSize(12f);
        connectionStatusHint.setGravity(Gravity.CENTER);
        connectionStatusHint.setPadding(0, dp(12, density), 0, 0);
        connectionStatusHint.setLineSpacing(dp(2, density), 1.0f);
        useNativePointerIcon(connectionStatusHint);
        panel.addView(connectionStatusHint, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        ));

        FrameLayout.LayoutParams panelParams = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT,
            Gravity.CENTER
        );
        panelParams.setMargins(dp(28, density), 0, dp(28, density), 0);
        layer.addView(panel, panelParams);
        return layer;
    }

    private LinearLayout createAudioPanel(float density) {
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(10, density), dp(10, density), dp(10, density), dp(10, density));
        panel.setBackground(makeRoundedBackground(0xE6111820, 0xFF46657A, dp(8, density)));
        useNativePointerIcon(panel);

        TextView title = new TextView(this);
        title.setText("电脑音频");
        title.setTextColor(0xFFFFFFFF);
        title.setTextSize(14f);
        title.setGravity(Gravity.START);
        title.setPadding(0, 0, 0, dp(8, density));
        useNativePointerIcon(title);
        panel.addView(title, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        ));

        audioMicStatusText = createAudioValueText();
        panel.addView(createAudioStatusRow("麦克风", audioMicStatusText, density), new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        ));

        audioSpeakerStatusText = createAudioValueText();
        LinearLayout.LayoutParams speakerParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        );
        speakerParams.topMargin = dp(4, density);
        panel.addView(createAudioStatusRow("音响", audioSpeakerStatusText, density), speakerParams);

        audioPermissionText = createAudioValueText();
        audioPermissionText.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                requestRecordAudioPermissionIfNeeded();
            }
        });
        LinearLayout.LayoutParams permissionParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        );
        permissionParams.topMargin = dp(4, density);
        panel.addView(createAudioStatusRow("权限", audioPermissionText, density), permissionParams);

        audioHintText = createAudioValueText();
        audioHintText.setTextColor(0xFFB9C6CC);
        LinearLayout.LayoutParams hintParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        );
        hintParams.topMargin = dp(8, density);
        panel.addView(audioHintText, hintParams);

        LinearLayout controls = createSegmentRow();
        LinearLayout.LayoutParams controlsParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            dp(38, density)
        );
        controlsParams.topMargin = dp(10, density);
        panel.addView(controls, controlsParams);

        micMuteButton = createSegmentButton("麦克风静音", density);
        controls.addView(micMuteButton, segmentParams(0, 2, density));
        micMuteButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                micMuted = !micMuted;
                lastAudioHint = micMuted ? "本机麦克风已静音。" : "麦克风静音已取消。";
                saveAudioPreferences();
                applyAudioCaptureIntent(lastAudioHint);
                updateOverlay();
            }
        });

        speakerMuteButton = createSegmentButton("音响静音", density);
        controls.addView(speakerMuteButton, segmentParams(1, 2, density));
        speakerMuteButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                speakerMuted = !speakerMuted;
                lastAudioHint = speakerMuted ? "本机音响已静音。" : "音响静音已取消。";
                saveAudioPreferences();
                applyAudioCaptureIntent(lastAudioHint);
                updateOverlay();
            }
        });

        stopAudioButton = createActionButton("停止音频", density);
        LinearLayout.LayoutParams stopParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            dp(40, density)
        );
        stopParams.topMargin = dp(8, density);
        panel.addView(stopAudioButton, stopParams);
        stopAudioButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                audioStopped = !audioStopped;
                lastAudioHint = audioStopped
                    ? "音频已停止，副屏仍在运行。"
                    : "音频控制已恢复，正在准备音频状态。";
                saveAudioPreferences();
                applyAudioCaptureIntent(lastAudioHint);
                updateOverlay();
            }
        });

        return panel;
    }

    private LinearLayout createAudioStatusRow(String label, TextView value, float density) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        useNativePointerIcon(row);

        TextView labelView = new TextView(this);
        labelView.setText(label);
        labelView.setTextColor(0xFF9DAEB8);
        labelView.setTextSize(12f);
        labelView.setGravity(Gravity.START);
        useNativePointerIcon(labelView);
        row.addView(labelView, new LinearLayout.LayoutParams(
            dp(52, density),
            ViewGroup.LayoutParams.WRAP_CONTENT
        ));

        row.addView(value, new LinearLayout.LayoutParams(
            0,
            ViewGroup.LayoutParams.WRAP_CONTENT,
            1f
        ));
        return row;
    }

    private TextView createAudioValueText() {
        TextView view = new TextView(this);
        view.setTextColor(0xFFE9F0F2);
        view.setTextSize(12f);
        view.setGravity(Gravity.START);
        view.setLineSpacing(2f, 1.0f);
        useNativePointerIcon(view);
        return view;
    }

    private TextView createModeToggle(float density) {
        TextView button = new TextView(this);
        button.setTextColor(0xFFFFFFFF);
        button.setTextSize(13f);
        button.setGravity(Gravity.CENTER);
        button.setMinWidth(dp(116, density));
        button.setPadding(dp(14, density), 0, dp(14, density), 0);
        button.setBackground(makeRoundedBackground(0xCC111820, 0xFF46657A, dp(8, density)));
        button.setText("Mode");
        useNativePointerIcon(button);
        button.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                toggleModePanel();
            }
        });
        return button;
    }

    private LinearLayout createModePanel(float density) {
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setVisibility(View.GONE);
        panel.setPadding(dp(10, density), dp(10, density), dp(10, density), dp(10, density));
        panel.setBackground(makeRoundedBackground(0xE6111820, 0xFF46657A, dp(8, density)));
        useNativePointerIcon(panel);

        TextView resolutionLabel = createPanelLabel("Resolution");
        panel.addView(resolutionLabel, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        ));
        panel.addView(createResolutionRow(density), new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            dp(40, density)
        ));

        TextView refreshLabel = createPanelLabel("Refresh");
        LinearLayout.LayoutParams refreshLabelParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        );
        refreshLabelParams.topMargin = dp(10, density);
        panel.addView(refreshLabel, refreshLabelParams);
        panel.addView(createRefreshRow(density), new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            dp(40, density)
        ));

        TextView applyButton = createActionButton("Apply", density);
        LinearLayout.LayoutParams applyParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            dp(42, density)
        );
        applyParams.topMargin = dp(12, density);
        panel.addView(applyButton, applyParams);
        applyButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                applySelectedDisplayMode();
            }
        });

        return panel;
    }

    private LinearLayout createResolutionRow(float density) {
        LinearLayout row = createSegmentRow();
        for (int index = 0; index < RESOLUTION_LABELS.length; index++) {
            final int buttonIndex = index;
            TextView button = createSegmentButton(RESOLUTION_LABELS[index], density);
            resolutionButtons[index] = button;
            row.addView(button, segmentParams(index, RESOLUTION_LABELS.length, density));
            button.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View view) {
                    selectedModeWidth = RESOLUTION_PRESETS[buttonIndex][0];
                    selectedModeHeight = RESOLUTION_PRESETS[buttonIndex][1];
                    updateModeControls();
                }
            });
        }

        return row;
    }

    private LinearLayout createRefreshRow(float density) {
        LinearLayout row = createSegmentRow();
        for (int index = 0; index < REFRESH_PRESETS.length; index++) {
            final int refreshHz = REFRESH_PRESETS[index];
            TextView button = createSegmentButton(refreshHz + "Hz", density);
            refreshButtons[index] = button;
            row.addView(button, segmentParams(index, REFRESH_PRESETS.length, density));
            button.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View view) {
                    selectedModeRefresh = refreshHz;
                    updateModeControls();
                }
            });
        }

        return row;
    }

    private LinearLayout createSegmentRow() {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        useNativePointerIcon(row);
        return row;
    }

    private TextView createPanelLabel(String label) {
        TextView view = new TextView(this);
        view.setText(label);
        view.setTextColor(0xFFB9C6CC);
        view.setTextSize(11f);
        view.setGravity(Gravity.START);
        useNativePointerIcon(view);
        return view;
    }

    private TextView createSegmentButton(String label, float density) {
        TextView button = new TextView(this);
        button.setText(label);
        button.setTextColor(0xFFE9F0F2);
        button.setTextSize(13f);
        button.setGravity(Gravity.CENTER);
        button.setPadding(dp(4, density), 0, dp(4, density), 0);
        useNativePointerIcon(button);
        return button;
    }

    private TextView createActionButton(String label, float density) {
        TextView button = createSegmentButton(label, density);
        button.setTextColor(0xFFFFFFFF);
        button.setBackground(makeRoundedBackground(0xFF2678C9, 0xFF72AEEB, dp(6, density)));
        return button;
    }

    private LinearLayout.LayoutParams segmentParams(int index, int count, float density) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
            0,
            ViewGroup.LayoutParams.MATCH_PARENT,
            1f
        );
        if (index > 0) {
            params.leftMargin = dp(6, density);
        }
        if (index + 1 < count) {
            params.rightMargin = dp(0, density);
        }
        return params;
    }

    private GradientDrawable makeRoundedBackground(int color, int strokeColor, int radiusPx) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(color);
        drawable.setCornerRadius(radiusPx);
        drawable.setStroke(1, strokeColor);
        return drawable;
    }

    private void toggleModePanel() {
        if (modePanel == null) {
            return;
        }

        modePanel.setVisibility(modePanel.getVisibility() == View.VISIBLE ? View.GONE : View.VISIBLE);
    }

    private void applySelectedDisplayMode() {
        selectedModeRefresh = normalizeSelectedRefresh(selectedModeRefresh);
        controlClient.sendDisplayModeChange(selectedModeWidth, selectedModeHeight, selectedModeRefresh);
        addLog("Request display mode " + selectedModeWidth + "x" + selectedModeHeight + "@" + selectedModeRefresh);
        if (modePanel != null) {
            modePanel.setVisibility(View.GONE);
        }
        updateModeControls();
    }

    private void updateModeControls() {
        if (modeToggleText != null) {
            modeToggleText.setText(modeLabel(selectedModeWidth, selectedModeHeight) + " " + selectedModeRefresh + "Hz");
        }

        float density = getResources().getDisplayMetrics().density;
        for (int index = 0; index < resolutionButtons.length; index++) {
            TextView button = resolutionButtons[index];
            if (button == null) {
                continue;
            }

            boolean selected = selectedModeWidth == RESOLUTION_PRESETS[index][0]
                && selectedModeHeight == RESOLUTION_PRESETS[index][1];
            styleSegmentButton(button, selected, density);
        }

        for (int index = 0; index < refreshButtons.length; index++) {
            TextView button = refreshButtons[index];
            if (button == null) {
                continue;
            }

            styleSegmentButton(button, selectedModeRefresh == REFRESH_PRESETS[index], density);
        }

        applyDisplayTimingHints();
    }

    private void updateModeControlVisibility() {
        boolean visible = overlayMode != OVERLAY_MODE_HIDDEN;
        if (modeToggleText != null) {
            modeToggleText.setVisibility(visible ? View.VISIBLE : View.GONE);
        }
        if (!visible && modePanel != null) {
            modePanel.setVisibility(View.GONE);
        }
    }

    private void styleSegmentButton(TextView button, boolean selected, float density) {
        button.setTextColor(selected ? 0xFFFFFFFF : 0xFFE9F0F2);
        button.setBackground(makeRoundedBackground(
            selected ? 0xFF2678C9 : 0x551B2630,
            selected ? 0xFF72AEEB : 0xFF314555,
            dp(6, density)
        ));
    }

    private void loadAudioPreferences() {
        SharedPreferences preferences = getSharedPreferences(AUDIO_PREFS_NAME, MODE_PRIVATE);
        micMuted = preferences.getBoolean(AUDIO_PREF_MIC_MUTED, false);
        speakerMuted = preferences.getBoolean(AUDIO_PREF_SPEAKER_MUTED, false);
        audioStopped = preferences.getBoolean(AUDIO_PREF_STOPPED, false);
        if (audioStopped) {
            lastAudioHint = "音频已停止，副屏仍在运行。";
        } else if (micMuted) {
            lastAudioHint = "本机麦克风已静音。";
        } else if (speakerMuted) {
            lastAudioHint = "本机音响已静音。";
        }
    }

    private void saveAudioPreferences() {
        getSharedPreferences(AUDIO_PREFS_NAME, MODE_PRIVATE)
            .edit()
            .putBoolean(AUDIO_PREF_MIC_MUTED, micMuted)
            .putBoolean(AUDIO_PREF_SPEAKER_MUTED, speakerMuted)
            .putBoolean(AUDIO_PREF_STOPPED, audioStopped)
            .apply();
    }

    private void applyAudioCaptureIntent(String message) {
        boolean connected = controlConnectionState == ConnectionState.CONNECTED;
        boolean microphoneConfigured = hostAudioEnabled && hostMicrophoneEnabled && !audioStopped && connected;
        boolean speakerConfigured = hostAudioEnabled && hostSpeakerEnabled && !audioStopped && connected;
        boolean microphoneCanCapture = microphoneConfigured && !micMuted && hasRecordAudioPermission();

        if (!microphoneConfigured) {
            microphoneRuntimeState = audioStatusWireState(AudioEndpoint.MICROPHONE, currentMicrophoneAudioStatus());
            publishAudioMicrophoneStatus(microphoneRuntimeState, message);
        } else if (!hasRecordAudioPermission()) {
            microphoneRuntimeState = "authorization_required";
            publishAudioMicrophoneStatus("authorization_required", "需要允许麦克风权限。");
            if (!micPermissionRequestedInSession) {
                requestRecordAudioPermissionIfNeeded();
            }
        } else if (micMuted) {
            microphoneRuntimeState = "muted";
            publishAudioMicrophoneStatus("muted", "本机麦克风已静音。");
        } else {
            microphoneRuntimeState = audioCaptureClient.isRunning() ? "capturing" : "preparing";
            publishAudioMicrophoneStatus(microphoneRuntimeState, message);
        }

        if (!speakerConfigured) {
            speakerRuntimeState = audioStatusWireState(AudioEndpoint.SPEAKER, currentSpeakerAudioStatus());
            publishAudioSpeakerStatus(speakerRuntimeState, message);
        } else if (speakerMuted) {
            speakerRuntimeState = "muted";
            publishAudioSpeakerStatus("muted", "本机音响已静音。");
        } else {
            speakerRuntimeState = audioCaptureClient.isRunning() && !"muted".equals(speakerRuntimeState)
                ? speakerRuntimeState
                : "preparing";
            publishAudioSpeakerStatus(speakerRuntimeState, message);
        }

        if (!microphoneCanCapture && !speakerConfigured) {
            audioCaptureClient.stop();
            return;
        }

        audioCaptureClient.start(
            audioPort,
            audioSampleRate,
            audioChannels,
            audioSpeakerChannels,
            microphoneCanCapture,
            speakerConfigured,
            speakerMuted);
    }

    private void requestRecordAudioPermissionIfNeeded() {
        if (hasRecordAudioPermission()) {
            applyAudioCaptureIntent("麦克风权限已允许。");
            return;
        }

        micPermissionRequestedInSession = true;
        requestPermissions(new String[] { Manifest.permission.RECORD_AUDIO }, REQUEST_RECORD_AUDIO);
    }

    private void publishAudioMicrophoneStatus(String state, String message) {
        if (controlClient == null) {
            return;
        }

        controlClient.sendAudioMicrophoneStatus(
            state,
            micMuted,
            audioStopped,
            hasRecordAudioPermission(),
            audioPort,
            audioSampleRate,
            audioChannels,
            message
        );
    }

    private void publishAudioSpeakerStatus(String state, String message) {
        if (controlClient == null) {
            return;
        }

        controlClient.sendAudioSpeakerStatus(
            state,
            speakerMuted,
            audioStopped,
            audioPort,
            audioSampleRate,
            audioSpeakerChannels,
            speakerPacketsReceived,
            speakerBytesReceived,
            message
        );
    }

    private String audioStatusWireState(AudioStatus status) {
        return audioStatusWireState(AudioEndpoint.MICROPHONE, status);
    }

    private String audioStatusWireState(AudioEndpoint endpoint, AudioStatus status) {
        switch (status) {
            case DISABLED:
                return "disabled";
            case WAITING_DEVICE:
                return "waiting_device";
            case PREPARING:
                return "preparing";
            case AVAILABLE:
                return "available";
            case CAPTURING:
                return endpoint == AudioEndpoint.SPEAKER ? "playing" : "capturing";
            case MUTED:
                return "muted";
            case AUTHORIZATION_REQUIRED:
                return "authorization_required";
            case RECONNECTING:
                return "reconnecting";
            case ERROR:
            case NOT_IMPLEMENTED:
            default:
                return "unavailable";
        }
    }

    private void updateAudioPanel() {
        if (audioPanel == null
            || audioMicStatusText == null
            || audioSpeakerStatusText == null
            || audioPermissionText == null
            || audioHintText == null
            || micMuteButton == null
            || speakerMuteButton == null
            || stopAudioButton == null) {
            return;
        }

        audioPanel.setVisibility(overlayMode == OVERLAY_MODE_HIDDEN ? View.GONE : View.VISIBLE);

        float density = getResources().getDisplayMetrics().density;
        AudioStatus micStatus = currentMicrophoneAudioStatus();
        AudioStatus speakerStatus = currentSpeakerAudioStatus();

        audioMicStatusText.setText(audioStatusText(AudioEndpoint.MICROPHONE, micStatus));
        audioMicStatusText.setTextColor(audioStatusColor(micStatus));
        audioSpeakerStatusText.setText(audioStatusText(AudioEndpoint.SPEAKER, speakerStatus));
        audioSpeakerStatusText.setTextColor(audioStatusColor(speakerStatus));
        audioPermissionText.setText(hasRecordAudioPermission()
            ? "麦克风权限已允许"
            : "需要允许麦克风权限后才能作为电脑麦克风");
        audioPermissionText.setTextColor(hasRecordAudioPermission() ? 0xFF77D59B : 0xFFFFB86B);
        audioHintText.setText(audioStopped ? "音频已停止，副屏仍在运行。" : lastAudioHint);

        micMuteButton.setText(micMuted ? "取消麦克风静音" : "麦克风静音");
        speakerMuteButton.setText(speakerMuted ? "取消音响静音" : "音响静音");
        stopAudioButton.setText(audioStopped ? "恢复音频" : "停止音频");

        styleAudioToggleButton(micMuteButton, micMuted, canToggleMicrophoneMute(), density);
        styleAudioToggleButton(speakerMuteButton, speakerMuted, canToggleSpeakerMute(), density);
        styleAudioStopButton(stopAudioButton, audioStopped, density);
    }

    private AudioStatus currentMicrophoneAudioStatus() {
        if (!hostAudioEnabled || !hostMicrophoneEnabled || audioStopped) {
            return AudioStatus.DISABLED;
        }
        if (!hasRecordAudioPermission()) {
            return AudioStatus.AUTHORIZATION_REQUIRED;
        }
        if (micMuted) {
            return AudioStatus.MUTED;
        }

        AudioStatus baseStatus = currentAudioBaseStatus();
        if (baseStatus != AudioStatus.AVAILABLE) {
            return baseStatus;
        }

        if ("capturing".equals(microphoneRuntimeState)) {
            return AudioStatus.CAPTURING;
        }
        if ("preparing".equals(microphoneRuntimeState)) {
            return AudioStatus.PREPARING;
        }
        if ("unavailable".equals(microphoneRuntimeState)) {
            return AudioStatus.ERROR;
        }

        return AudioStatus.AVAILABLE;
    }

    private AudioStatus currentSpeakerAudioStatus() {
        if (!hostAudioEnabled || !hostSpeakerEnabled || audioStopped) {
            return AudioStatus.DISABLED;
        }
        if (speakerMuted) {
            return AudioStatus.MUTED;
        }

        AudioStatus baseStatus = currentAudioBaseStatus();
        if (baseStatus != AudioStatus.AVAILABLE) {
            return baseStatus;
        }

        if ("playing".equals(speakerRuntimeState)) {
            return AudioStatus.CAPTURING;
        }
        if ("preparing".equals(speakerRuntimeState)) {
            return AudioStatus.PREPARING;
        }
        if ("unavailable".equals(speakerRuntimeState)) {
            return AudioStatus.ERROR;
        }
        if ("muted".equals(speakerRuntimeState)) {
            return AudioStatus.MUTED;
        }

        return AudioStatus.AVAILABLE;
    }

    private AudioStatus currentAudioBaseStatus() {
        switch (controlConnectionState) {
            case CONNECTED:
                return AudioStatus.AVAILABLE;
            case CONNECTING:
                return AudioStatus.PREPARING;
            case RECONNECTING:
                return AudioStatus.RECONNECTING;
            case FAILED:
                return AudioStatus.ERROR;
            case DISCONNECTED:
            default:
                return AudioStatus.WAITING_DEVICE;
        }
    }

    private String audioStatusText(AudioEndpoint endpoint, AudioStatus status) {
        boolean microphone = endpoint == AudioEndpoint.MICROPHONE;
        switch (status) {
            case DISABLED:
                return microphone ? "电脑未启用 SideDock 麦克风" : "电脑未启用 SideDock 音响";
            case WAITING_DEVICE:
                return "等待电脑连接";
            case PREPARING:
                return microphone ? "正在准备麦克风" : "正在准备音响";
            case AVAILABLE:
                return microphone ? "麦克风数据通道可用" : "电脑可以选择本机音响";
            case CAPTURING:
                return microphone ? "电脑正在使用本机麦克风" : "正在播放电脑声音";
            case MUTED:
                return microphone ? "本机麦克风已静音" : "本机音响已静音";
            case AUTHORIZATION_REQUIRED:
                return microphone ? "需要允许麦克风权限" : "等待音响准备";
            case RECONNECTING:
                return microphone ? "麦克风正在重连" : "音响正在重连";
            case ERROR:
                return microphone ? "麦克风暂不可用" : "音响暂不可用";
            case NOT_IMPLEMENTED:
                return microphone ? "麦克风暂不可用" : "音响暂不可用";
            default:
                return "等待电脑连接";
        }
    }

    private int audioStatusColor(AudioStatus status) {
        switch (status) {
            case AVAILABLE:
            case CAPTURING:
                return 0xFF77D59B;
            case MUTED:
            case AUTHORIZATION_REQUIRED:
                return 0xFFFFB86B;
            case ERROR:
                return 0xFFFF8A80;
            default:
                return 0xFFE9F0F2;
        }
    }

    private boolean canToggleMicrophoneMute() {
        return !audioStopped
            && hostAudioEnabled
            && hostMicrophoneEnabled
            && hasRecordAudioPermission()
            && controlConnectionState == ConnectionState.CONNECTED;
    }

    private boolean canToggleSpeakerMute() {
        return !audioStopped
            && hostAudioEnabled
            && hostSpeakerEnabled
            && controlConnectionState == ConnectionState.CONNECTED;
    }

    private boolean hasRecordAudioPermission() {
        return checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED;
    }

    private void styleAudioToggleButton(TextView button, boolean active, boolean enabled, float density) {
        button.setEnabled(enabled);
        button.setAlpha(enabled ? 1.0f : 0.5f);
        button.setTextColor(active ? 0xFFFFFFFF : 0xFFE9F0F2);
        button.setBackground(makeRoundedBackground(
            active ? 0xFF9D5D00 : 0x551B2630,
            active ? 0xFFFFB86B : 0xFF314555,
            dp(6, density)
        ));
    }

    private void styleAudioStopButton(TextView button, boolean stopped, float density) {
        button.setTextColor(0xFFFFFFFF);
        button.setBackground(makeRoundedBackground(
            stopped ? 0xFF2678C9 : 0xFF8C2D24,
            stopped ? 0xFF72AEEB : 0xFFFF8A80,
            dp(6, density)
        ));
    }

    private enum AudioEndpoint {
        MICROPHONE,
        SPEAKER
    }

    private enum AudioStatus {
        DISABLED,
        WAITING_DEVICE,
        PREPARING,
        AVAILABLE,
        CAPTURING,
        MUTED,
        AUTHORIZATION_REQUIRED,
        RECONNECTING,
        ERROR,
        NOT_IMPLEMENTED
    }

    private String modeLabel(int width, int height) {
        for (int index = 0; index < RESOLUTION_PRESETS.length; index++) {
            if (width == RESOLUTION_PRESETS[index][0] && height == RESOLUTION_PRESETS[index][1]) {
                return RESOLUTION_LABELS[index];
            }
        }

        return width + "x" + height;
    }

    private int normalizeSelectedRefresh(int refreshHz) {
        for (int preset : REFRESH_PRESETS) {
            if (preset == refreshHz) {
                return refreshHz;
            }
        }

        return DEFAULT_VIDEO_FPS;
    }

    private int normalizeObservedRefresh(int refreshHz) {
        for (int preset : REFRESH_PRESETS) {
            if (Math.abs(preset - refreshHz) <= 1) {
                return preset;
            }
        }

        return refreshHz > 0 ? refreshHz : DEFAULT_VIDEO_FPS;
    }

    private int targetFps() {
        return videoFps > 0 ? videoFps : DEFAULT_VIDEO_FPS;
    }

    private float targetRefreshHz() {
        return selectedModeRefresh > 0
            ? selectedModeRefresh
            : targetFps();
    }

    private void applyDisplayTimingHints() {
        updateDisplayRefreshHz();
        float refreshHz = targetRefreshHz();
        applyWindowRefreshHint(refreshHz);
        applySurfaceRefreshHint(refreshHz);
    }

    private void clearDisplayTimingHints() {
        applyWindowRefreshHint(0f);
        applySurfaceRefreshHint(0f);
    }

    private void applyWindowRefreshHint(float refreshHz) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.LOLLIPOP) {
            return;
        }

        Window window = getWindow();
        if (window == null) {
            return;
        }

        WindowManager.LayoutParams params = window.getAttributes();
        if (Float.compare(params.preferredRefreshRate, refreshHz) == 0) {
            return;
        }

        params.preferredRefreshRate = refreshHz;
        window.setAttributes(params);
    }

    private void applySurfaceRefreshHint(float refreshHz) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S) {
            return;
        }

        Surface surface = activeSurface;
        if (surface == null && surfaceView != null && surfaceView.getHolder() != null) {
            surface = surfaceView.getHolder().getSurface();
        }

        if (surface == null || !surface.isValid()) {
            return;
        }

        surface.setFrameRate(
            refreshHz,
            Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE,
            Surface.CHANGE_FRAME_RATE_ONLY_IF_SEAMLESS
        );
    }

    private void maybeStartVideo() {
        refreshSurfaceState();

        if (!videoStartReceived) {
            Log.i(TAG, "maybeStartVideo skipped, waiting for video_start");
            return;
        }

        if (!surfaceReady || activeSurface == null || !activeSurface.isValid()) {
            Log.i(TAG, "maybeStartVideo skipped, surface not ready");
            return;
        }

        if (videoClient.isRunningFor(activeSurface, videoPort, videoWidth, videoHeight, videoFps)) {
            Log.i(TAG, "maybeStartVideo skipped, already running");
            return;
        }

        Log.i(TAG, "starting VideoClient port=" + videoPort);
        videoClient.start(activeSurface, videoPort, videoWidth, videoHeight, videoFps);
    }

    private void sendVideoReadyIfSurfaceReady() {
        refreshSurfaceState();
        if (!surfaceReady) {
            Log.i(TAG, "video_ready skipped, surface not ready");
            return;
        }

        controlClient.sendVideoReady(videoWidth, videoHeight, VIDEO_CODEC_AVC);
        Log.i(TAG, "video_ready sent");
        maybeStartVideo();
    }

    private void handlePreflightDecoderUnsupported() {
        videoStartReceived = false;
        String message = "Preflight decoder capability rejected " + VIDEO_CODEC_AVC
            + " " + videoWidth + "x" + videoHeight + "@" + videoFps;
        lastVideoError = ERROR_DECODER_UNSUPPORTED + ": " + message;
        waitingForVideoFrame = true;
        controlClient.sendVideoError(ERROR_DECODER_UNSUPPORTED, message);
        addLog("video preflight unsupported " + videoWidth + "x" + videoHeight + "@" + videoFps);
        Log.w(TAG, message);
        updateOverlay();
    }

    private boolean shouldRejectAvcBeforeStart(int width, int height, int fps) {
        if (width <= 0 || height <= 0 || fps <= 60) {
            return false;
        }

        Boolean supported = queryAvcDecoderSupport(width, height, fps);
        if (supported == null) {
            Log.w(TAG, "Unable to query AVC decoder support, allowing startup for " + width + "x" + height + "@" + fps);
            return false;
        }

        Log.i(TAG, "AVC decoder support " + width + "x" + height + "@" + fps + "=" + supported);
        return !supported.booleanValue();
    }

    private Boolean queryAvcDecoderSupport(int width, int height, int fps) {
        try {
            MediaCodecList codecList = new MediaCodecList(MediaCodecList.REGULAR_CODECS);
            MediaCodecInfo[] codecInfos = codecList.getCodecInfos();
            for (MediaCodecInfo codecInfo : codecInfos) {
                if (codecInfo.isEncoder()) {
                    continue;
                }

                String[] supportedTypes = codecInfo.getSupportedTypes();
                for (String type : supportedTypes) {
                    if (!VIDEO_CODEC_AVC.equalsIgnoreCase(type)) {
                        continue;
                    }

                    MediaCodecInfo.CodecCapabilities capabilities = codecInfo.getCapabilitiesForType(type);
                    MediaCodecInfo.VideoCapabilities videoCapabilities = capabilities.getVideoCapabilities();
                    boolean sizeRateSupported = videoCapabilities != null
                        && videoCapabilities.areSizeAndRateSupported(width, height, fps);
                    String acceleration = Build.VERSION.SDK_INT >= 29
                        ? (" hardware=" + codecInfo.isHardwareAccelerated() + " software=" + codecInfo.isSoftwareOnly())
                        : "";
                    Log.i(TAG, "AVC decoder candidate name=" + codecInfo.getName()
                        + acceleration
                        + " sizeRateSupported=" + sizeRateSupported
                        + " bitrateRange=" + (videoCapabilities == null ? "unknown" : videoCapabilities.getBitrateRange()));
                    if (sizeRateSupported) {
                        return Boolean.TRUE;
                    }
                }
            }

            return Boolean.FALSE;
        } catch (Exception ex) {
            Log.w(TAG, "Unable to query AVC decoder capabilities", ex);
            return null;
        }
    }

    private void refreshSurfaceState() {
        if (surfaceView == null) {
            surfaceReady = false;
            activeSurface = null;
            return;
        }

        Surface holderSurface = surfaceView.getHolder().getSurface();
        if (holderSurface != null && holderSurface.isValid()) {
            activeSurface = holderSurface;
            surfaceReady = true;
            return;
        }

        surfaceReady = activeSurface != null && activeSurface.isValid();
    }

    private void updateVideoRect(int viewWidth, int viewHeight) {
        if (viewWidth <= 0 || viewHeight <= 0 || videoWidth <= 0 || videoHeight <= 0) {
            return;
        }

        float viewAspect = viewWidth / (float) viewHeight;
        float videoAspect = videoWidth / (float) videoHeight;
        if (viewAspect > videoAspect) {
            videoRectHeight = viewHeight;
            videoRectWidth = Math.round(viewHeight * videoAspect);
            videoRectLeft = (viewWidth - videoRectWidth) / 2;
            videoRectTop = 0;
        } else {
            videoRectWidth = viewWidth;
            videoRectHeight = Math.round(viewWidth / videoAspect);
            videoRectLeft = 0;
            videoRectTop = (viewHeight - videoRectHeight) / 2;
        }

        applyVideoSurfaceLayout();
        updateContentRectFromMetrics();
        inputCollector.setVideoRect(contentRectLeft, contentRectTop, contentRectWidth, contentRectHeight);
    }

    private void applyVideoSurfaceLayout() {
        if (surfaceView == null || videoRectWidth <= 0 || videoRectHeight <= 0 || !videoStartReceived) {
            return;
        }

        ViewGroup.LayoutParams currentParams = surfaceView.getLayoutParams();
        if (!(currentParams instanceof FrameLayout.LayoutParams)) {
            return;
        }

        FrameLayout.LayoutParams params = (FrameLayout.LayoutParams) currentParams;
        boolean changed = params.width != videoRectWidth
            || params.height != videoRectHeight
            || params.leftMargin != videoRectLeft
            || params.topMargin != videoRectTop
            || params.gravity != (Gravity.START | Gravity.TOP);
        if (!changed) {
            return;
        }

        params.width = videoRectWidth;
        params.height = videoRectHeight;
        params.gravity = Gravity.START | Gravity.TOP;
        params.setMargins(videoRectLeft, videoRectTop, 0, 0);
        surfaceView.setLayoutParams(params);
    }

    private void updateContentRectFromMetrics() {
        if (lastDisplayMetrics == null || videoWidth <= 0 || videoHeight <= 0) {
            contentRectLeft = videoRectLeft;
            contentRectTop = videoRectTop;
            contentRectWidth = videoRectWidth;
            contentRectHeight = videoRectHeight;
            return;
        }

        float scaleX = videoRectWidth / (float) videoWidth;
        float scaleY = videoRectHeight / (float) videoHeight;
        int sourceX = clampInt(lastDisplayMetrics.videoRectX, 0, videoWidth);
        int sourceY = clampInt(lastDisplayMetrics.videoRectY, 0, videoHeight);
        int sourceW = clampInt(lastDisplayMetrics.videoRectWidth, 1, videoWidth - sourceX);
        int sourceH = clampInt(lastDisplayMetrics.videoRectHeight, 1, videoHeight - sourceY);
        contentRectLeft = videoRectLeft + Math.round(sourceX * scaleX);
        contentRectTop = videoRectTop + Math.round(sourceY * scaleY);
        contentRectWidth = Math.max(1, Math.round(sourceW * scaleX));
        contentRectHeight = Math.max(1, Math.round(sourceH * scaleY));
    }

    private void updateVideoRectForSurfaceView() {
        if (surfaceView == null) {
            return;
        }

        View parent = rootView != null ? rootView : (View) surfaceView.getParent();
        int viewWidth = parent == null ? surfaceView.getWidth() : parent.getWidth();
        int viewHeight = parent == null ? surfaceView.getHeight() : parent.getHeight();
        if (viewWidth <= 0 || viewHeight <= 0) {
            return;
        }

        updateVideoRect(viewWidth, viewHeight);
    }

    private void applySurfaceFixedSize(int width, int height) {
        if (surfaceView == null || width <= 0 || height <= 0 || !videoStartReceived) {
            return;
        }

        if (surfaceReady && activeSurface != null && activeSurface.isValid()) {
            return;
        }

        if (surfaceFixedWidth == width && surfaceFixedHeight == height) {
            return;
        }

        surfaceFixedWidth = width;
        surfaceFixedHeight = height;
        surfaceView.getHolder().setFixedSize(width, height);
    }

    private void updateCursorDebugOverlay() {
        long now = System.currentTimeMillis();
        if (now - lastCursorDebugAtMs < 250L) {
            return;
        }

        lastCursorDebugAtMs = now;
        updateOverlay();
    }

    private void schedulePointerAbsFlush() {
        if (inputCollector == null) {
            return;
        }

        mainHandler.removeCallbacks(pointerAbsFlushRunnable);
        if (!inputCollector.hasPendingPointerAbs()) {
            return;
        }

        long delayMs = inputCollector.millisUntilNextPointerAbsFlush();
        mainHandler.postDelayed(pointerAbsFlushRunnable, delayMs);
    }

    private void flushPendingPointerAbs() {
        if (inputCollector == null) {
            return;
        }

        inputCollector.flushPendingPointerAbs();
        if (inputCollector.hasPendingPointerAbs()) {
            schedulePointerAbsFlush();
        }
    }

    private void enterImmersiveMode() {
        View decorView = getWindow().getDecorView();
        decorView.setSystemUiVisibility(
            View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                | View.SYSTEM_UI_FLAG_FULLSCREEN
                | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
        );
        useNativePointerIcon(decorView);
    }

    private void useNativePointerIcon(View view) {
        if (view == null) {
            return;
        }

        view.setPointerIcon(null);
    }

    private void startDisplayFrameSampling() {
        updateDisplayRefreshHz();
        if (displayFrameCallbacksActive) {
            return;
        }

        displayFrameCallbacksActive = true;
        displayFrameCallbacks = 0L;
        lastDisplayRateSnapshotNanos = 0L;
        lastDisplayRateFrameCallbacks = 0L;
        displayCallbackFps = 0.0;
        Choreographer.getInstance().postFrameCallback(displayFrameCallback);
    }

    private void stopDisplayFrameSampling() {
        if (!displayFrameCallbacksActive) {
            return;
        }

        displayFrameCallbacksActive = false;
        Choreographer.getInstance().removeFrameCallback(displayFrameCallback);
    }

    private void recordDisplayFrameCallback(long frameTimeNanos) {
        displayFrameCallbacks += 1L;
        if (lastDisplayRateSnapshotNanos == 0L) {
            lastDisplayRateSnapshotNanos = frameTimeNanos;
            lastDisplayRateFrameCallbacks = displayFrameCallbacks;
            return;
        }

        long elapsedNanos = frameTimeNanos - lastDisplayRateSnapshotNanos;
        if (elapsedNanos < 500_000_000L) {
            return;
        }

        long frameDelta = displayFrameCallbacks - lastDisplayRateFrameCallbacks;
        displayCallbackFps = frameDelta / (elapsedNanos / 1_000_000_000.0);
        lastDisplayRateSnapshotNanos = frameTimeNanos;
        lastDisplayRateFrameCallbacks = displayFrameCallbacks;
        updateDisplayRefreshHz();
        updateOverlay();
    }

    private void updateDisplayRefreshHz() {
        float refreshHz = readDisplayRefreshHz();
        if (refreshHz > 0f) {
            displayRefreshHz = refreshHz;
        }
    }

    @SuppressWarnings("deprecation")
    private float readDisplayRefreshHz() {
        Display display = null;
        if (surfaceView != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            display = surfaceView.getDisplay();
        }
        if (display == null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            display = getDisplay();
        }
        if (display == null) {
            WindowManager windowManager = (WindowManager) getSystemService(Context.WINDOW_SERVICE);
            if (windowManager != null) {
                display = windowManager.getDefaultDisplay();
            }
        }

        return display == null ? 0f : display.getRefreshRate();
    }

    private void addLog(String message) {
        String line = timeFormatter.format(new Date()) + "  " + message;
        logLines.addFirst(line);
        while (logLines.size() > 6) {
            logLines.removeLast();
        }
        updateOverlay();
    }

    private void updateOverlay() {
        updateModeControlVisibility();
        updateConnectionStatusLayer();
        updateAudioPanel();

        if (overlayText == null) {
            return;
        }

        if (overlayMode == OVERLAY_MODE_HIDDEN) {
            overlayText.setVisibility(View.GONE);
            overlayText.setText("");
            return;
        }

        overlayText.setVisibility(View.VISIBLE);
        if (overlayMode == OVERLAY_MODE_COMPACT) {
            StringBuilder compactBuilder = new StringBuilder();
            appendCompactOverlay(compactBuilder);
            overlayText.setText(compactBuilder.toString().trim());
            return;
        }

        StringBuilder builder = new StringBuilder();
        builder
            .append("SideDock 视频测试\n")
            .append("控制: ").append(controlState)
            .append("  发/收 ").append(controlSent).append('/').append(controlReceived).append('\n')
            .append("视频: ").append(videoState)
            .append("  ").append(videoWidth).append('x').append(videoHeight).append('@').append(videoFps)
            .append("  port ").append(videoPort).append('\n');
        builder.append("吞吐FPS: ");
        appendFpsFields(builder);
        builder.append('\n');
        builder.append("显示回调: ");
        appendDisplayTimingFields(builder);
        builder.append('\n');

        if (lastVideoStats != null) {
            builder
                .append("帧: ").append(lastVideoStats.framesDecoded)
                .append('/').append(lastVideoStats.framesRendered)
                .append("  包: ").append(lastVideoStats.packetsReceived)
                .append("  错误: ").append(lastVideoStats.decodeErrors)
                .append("  丢弃: ").append(lastVideoStats.droppedFrames)
                .append("  重连: ").append(lastVideoStats.reconnects)
                .append("  local=").append(lastVideoStats.localPipelineLatencyMs).append("ms")
                .append("  e2e~").append(lastVideoStats.roughLatencyMs).append("ms")
                .append("  throughput ")
                .append(String.format(Locale.ROOT, "%.0f/%.0f", lastVideoStats.newFrameFps, lastVideoStats.decodeFps))
                .append("fps new/decode")
                .append("  renderCb ")
                .append(formatFpsValue(lastVideoStats.renderFps))
                .append("  new/repeat ")
                .append(String.format(Locale.ROOT, "%.0f/%.0f", lastVideoStats.newFrameFps, lastVideoStats.repeatFrameFps));
            builder
                .append('\n')
                .append("阶段: ").append(lastVideoStats.lastFrameKind)
                .append(" seq=").append(lastVideoStats.lastSourceSeq)
                .append(" age=").append(lastVideoStats.lastSourceAgeMs).append("ms")
                .append(" enc=").append(String.format(Locale.ROOT, "%.1f", lastVideoStats.lastEncodeMs)).append("ms")
                .append(" q=").append(String.format(Locale.ROOT, "%.1f", lastVideoStats.lastReceiveToQueueMs)).append("ms")
                .append(" dec=").append(String.format(Locale.ROOT, "%.1f", lastVideoStats.lastQueueToOutputMs)).append("ms")
                .append(" render=").append(String.format(Locale.ROOT, "%.1f", lastVideoStats.lastOutputToRenderMs)).append("ms")
                .append("  render p95/p99=")
                .append(String.format(Locale.ROOT, "%.1f/%.1f", lastVideoStats.p95QueueToRenderMs, lastVideoStats.p99QueueToRenderMs)).append("ms");
            if (serverTimeOffsetMs != 0) {
                builder.append("  clock").append(serverTimeOffsetMs >= 0 ? "+" : "").append(serverTimeOffsetMs).append("ms");
                if (clockSyncErrorBoundMs != Long.MAX_VALUE) {
                    builder.append("+/-").append(clockSyncErrorBoundMs).append("ms");
                }
                if (clockSyncRttMs != Long.MAX_VALUE) {
                    builder.append(" rtt=").append(clockSyncRttMs).append("ms");
                }
            }
            builder.append('\n');
        }

        if (lastCaptureStatus != null) {
            builder
                .append("采集: ").append(lastCaptureStatus.source.length() == 0 ? "-" : lastCaptureStatus.source)
                .append("  ").append(lastCaptureStatus.state)
                .append("  帧 ").append(lastCaptureStatus.framesCaptured)
                .append("  转 ").append(lastCaptureStatus.framesConverted)
                .append("  错 ").append(lastCaptureStatus.captureErrors)
                .append("  取 ").append(String.format(Locale.ROOT, "%.1f", lastCaptureStatus.avgCaptureMs)).append("ms")
                .append("  转 ").append(String.format(Locale.ROOT, "%.1f", lastCaptureStatus.avgConvertMs)).append("ms");
            builder.append("  ").append(lastCaptureStatus.gpuPath ? "GPU" : "CPU");
            if (lastCaptureStatus.gpuConvertMs > 0.0) {
                builder.append("  GPU=").append(String.format(Locale.ROOT, "%.1f", lastCaptureStatus.gpuConvertMs)).append("ms");
            }
            if (lastCaptureStatus.framesDropped > 0L) {
                builder.append("  drop=").append(lastCaptureStatus.framesDropped);
            }
            if (lastCaptureStatus.lastFrameAgeMs > 0.0) {
                builder.append("  age=").append(String.format(Locale.ROOT, "%.0f", lastCaptureStatus.lastFrameAgeMs)).append("ms");
            }
            if (lastCaptureStatus.fallback.length() > 0 && !"none".equals(lastCaptureStatus.fallback)) {
                builder.append("  fallback=").append(lastCaptureStatus.fallback);
            }
            if (lastCaptureStatus.errorCode.length() > 0) {
                builder.append("  ").append(lastCaptureStatus.errorCode);
            }
            builder.append('\n');
        }

        if (lastEncoderStatus != null) {
            builder
                .append("Encoder: sent ").append(lastEncoderStatus.framesSent)
                .append("  stream ").append(String.format(Locale.ROOT, "%.1f", lastEncoderStatus.streamFps)).append("fps")
                .append("  new/repeat ").append(lastEncoderStatus.newFramesSent).append('/').append(lastEncoderStatus.repeatFramesSent)
                .append("  drop ").append(lastEncoderStatus.framesDropped)
                .append("  enc p95/p99 ")
                .append(String.format(Locale.ROOT, "%.1f/%.1f", lastEncoderStatus.p95EncodeMs, lastEncoderStatus.p99EncodeMs)).append("ms")
                .append("  send p95/p99 ")
                .append(String.format(Locale.ROOT, "%.1f/%.1f", lastEncoderStatus.p95SendMs, lastEncoderStatus.p99SendMs)).append("ms")
                .append("  kbps ").append(String.format(Locale.ROOT, "%.0f", lastEncoderStatus.outputKbps));
            if (lastEncoderStatus.gpuPath) {
                builder.append("  GPU");
            }
            builder.append('\n');
        }

        InputCollector.InputStats inputStats = lastInputStats != null ? lastInputStats : inputCollector == null ? null : inputCollector.snapshot();
        if (inputStats != null) {
            builder
                .append("输入: 键 ").append(inputStats.keyboardEvents)
                .append("  绝 ").append(inputStats.pointerAbsEvents)
                .append("  移 ").append(inputStats.mouseMoveEvents)
                .append("  预 ").append(inputStats.localPointerUpdates)
                .append("  键鼠 ").append(inputStats.mouseButtonEvents)
                .append("  滚 ").append(inputStats.mouseWheelEvents)
                .append("  最近 ").append(inputStats.lastInputType)
                .append('\n');
        }

        if (lastDisplayLayout != null) {
            builder
                .append("显示: ").append(lastDisplayLayout.source)
                .append("  ").append(lastDisplayLayout.width).append('x').append(lastDisplayLayout.height)
                .append(" @ ").append(lastDisplayLayout.x).append(',').append(lastDisplayLayout.y)
                .append("  videoRect ").append(videoRectWidth).append('x').append(videoRectHeight)
                .append('\n');
        }

        if (lastDisplayMetrics != null) {
            builder
                .append("DPI: ").append(String.format(Locale.ROOT, "%.2f", lastDisplayMetrics.dpiScale))
                .append("  display=").append(lastDisplayMetrics.displayWidth).append('x').append(lastDisplayMetrics.displayHeight)
                .append("  mode=").append(lastDisplayMetrics.displayWidth).append('x').append(lastDisplayMetrics.displayHeight)
                .append('@').append(lastDisplayMetrics.refreshHz)
                .append("  rect=").append(contentRectLeft).append(',').append(contentRectTop)
                .append(' ').append(contentRectWidth).append('x').append(contentRectHeight)
                .append("  cursor=").append(cursorKind)
                .append('\n');
        }

        if (lastCursorState != null) {
            builder
                .append("Cursor: native")
                .append("  visible=").append(lastCursorState.visible)
                .append("  pos=").append(lastCursorState.x).append(',').append(lastCursorState.y)
                .append("  basis=").append(lastCursorState.displayWidth).append('x').append(lastCursorState.displayHeight)
                .append("  n=").append(String.format(Locale.ROOT, "%.3f,%.3f", lastCursorState.nx, lastCursorState.ny));
            if (lastCursorState.desktopX != 0 || lastCursorState.desktopY != 0) {
                builder.append("  desktop=").append(lastCursorState.desktopX).append(',').append(lastCursorState.desktopY);
            }
            builder.append('\n');
        }

        if (lastDisplayModeChanged != null) {
            builder
                .append("切换: ").append(lastDisplayModeChanged.success ? "OK" : lastDisplayModeChanged.code)
                .append("  ").append(lastDisplayModeChanged.width).append('x').append(lastDisplayModeChanged.height)
                .append('@').append(lastDisplayModeChanged.refreshHz)
                .append('\n');
        }

        for (String line : logLines) {
            builder.append(line).append('\n');
        }
        overlayText.setText(builder.toString().trim());
    }

    private void updateConnectionStatusLayer() {
        if (connectionStatusLayer == null
            || connectionStatusProgress == null
            || connectionStatusTitle == null
            || connectionStatusDetail == null
            || connectionStatusHint == null) {
            return;
        }

        if (!shouldShowConnectionStatus()) {
            connectionStatusLayer.setVisibility(View.GONE);
            return;
        }

        connectionStatusLayer.setVisibility(View.VISIBLE);

        String title;
        String hint;
        boolean showProgress = true;
        if (lastCaptureStatus != null && "ERROR".equals(lastCaptureStatus.state)) {
            title = "采集异常";
            hint = lastCaptureStatus.errorMessage.length() == 0
                ? "请查看 Windows 端日志，确认采集源或虚拟屏是否可用。"
                : lastCaptureStatus.errorMessage;
            showProgress = false;
        } else if (!surfaceReady) {
            title = "正在准备显示层";
            hint = "Surface 创建完成后会自动请求视频流。";
        } else if (controlConnectionState == ConnectionState.DISCONNECTED) {
            title = "等待 Windows 主机";
            hint = "请确认 Host 正在运行，并已配置 adb reverse tcp:27183/tcp:27184。";
        } else if (controlConnectionState == ConnectionState.CONNECTING) {
            title = "正在连接 Windows 主机";
            hint = "控制通道连接中。";
        } else if (controlConnectionState == ConnectionState.RECONNECTING) {
            title = "正在重连";
            hint = "连接中断，客户端会自动重试。";
        } else if (!"CONNECTED".equals(videoState)) {
            title = "控制通道已连接";
            hint = lastVideoError.length() == 0 ? "正在等待视频通道。" : "正在重试视频通道: " + lastVideoError;
        } else if (isReceiveOnlyVideoMode()) {
            title = "正在接收视频流";
            hint = "Android 解码器没有输出画面，当前保持视频通道接收。";
            showProgress = false;
        } else if (lastVideoError.length() > 0) {
            title = "视频连接异常";
            hint = lastVideoError;
            showProgress = false;
        } else {
            title = "正在等待画面";
            hint = "视频通道已连接，等待首帧渲染。";
        }

        StringBuilder detail = new StringBuilder();
        detail
            .append("控制: ").append(controlState)
            .append("   视频: ").append(videoState).append('\n')
            .append("画面: ").append(videoWidth).append('x').append(videoHeight)
            .append('@').append(videoFps)
            .append("   port ").append(videoPort);
        if (lastVideoStats != null) {
            detail
                .append('\n')
                .append("已收包 ").append(lastVideoStats.packetsReceived)
                .append("   已解码帧 ").append(lastVideoStats.framesDecoded)
                .append("   已渲染帧 ").append(lastVideoStats.framesRendered)
                .append("   重连 ").append(lastVideoStats.reconnects);
        }
        if (lastCaptureStatus != null) {
            detail
                .append('\n')
                .append("采集: ").append(lastCaptureStatus.state)
                .append("   帧 ").append(lastCaptureStatus.framesCaptured)
                .append("   错 ").append(lastCaptureStatus.captureErrors);
        }

        if (lastEncoderStatus != null) {
            detail
                .append('\n')
                .append("Encoder: sent ").append(lastEncoderStatus.framesSent)
                .append("   stream ").append(String.format(Locale.ROOT, "%.1f", lastEncoderStatus.streamFps)).append("fps")
                .append("   p95 ").append(String.format(Locale.ROOT, "%.1f", lastEncoderStatus.p95EncodeMs)).append("ms");
        }

        connectionStatusProgress.setVisibility(showProgress ? View.VISIBLE : View.GONE);
        connectionStatusTitle.setText(title);
        connectionStatusDetail.setText(detail.toString());
        connectionStatusHint.setText(hint);
    }

    private boolean shouldShowConnectionStatus() {
        if (!surfaceReady) {
            return true;
        }
        if (controlConnectionState != ConnectionState.CONNECTED) {
            return true;
        }
        if (!"CONNECTED".equals(videoState)) {
            return true;
        }
        if (lastCaptureStatus != null && "ERROR".equals(lastCaptureStatus.state)) {
            return true;
        }
        if (lastVideoError.length() > 0) {
            return true;
        }
        if (isReceiveOnlyVideoMode()) {
            return true;
        }

        long framesRendered = lastVideoStats == null ? 0L : lastVideoStats.framesRendered;
        return waitingForVideoFrame || framesRendered == 0L;
    }

    private boolean isReceiveOnlyVideoMode() {
        return "CONNECTED".equals(videoState)
            && lastVideoStats != null
            && lastVideoStats.packetsReceived > 0L
            && lastVideoStats.framesDecoded == 0L
            && lastVideoStats.framesRendered == 0L
            && lastVideoStats.decodeErrors > 0L;
    }

    private void appendCompactOverlay(StringBuilder builder) {
        builder
            .append("时延 ")
            .append(currentLatencyMs())
            .append("ms  帧率 ")
            .append(formatFpsValue(currentOverlayFps()))
            .append("fps");
    }

    private long currentLatencyMs() {
        if (lastVideoStats != null) {
            return Math.max(0L, lastVideoStats.localPipelineLatencyMs);
        }

        return 0L;
    }

    private double currentOverlayFps() {
        double renderFps = currentRenderFps();
        if (!isUsableFps(renderFps)) {
            renderFps = currentStreamFps();
        }

        return renderFps;
    }

    private boolean isUsableFps(double fps) {
        return !Double.isNaN(fps) && !Double.isInfinite(fps) && fps >= 0.0;
    }

    private void logVideoStatsSummary(VideoClient.VideoStats stats) {
        long nowMs = System.currentTimeMillis();
        if (lastVideoStatsSummaryLogAtMs != 0L && nowMs - lastVideoStatsSummaryLogAtMs < 1000L) {
            return;
        }

        lastVideoStatsSummaryLogAtMs = nowMs;
        Log.i(TAG,
            "video stats "
                + "target=" + targetFps()
                + " displayHz=" + formatRefreshValue(currentDisplayRefreshHz())
                + " stream=" + formatFpsValueDetailed(currentStreamFps())
                + " new=" + formatFpsValueDetailed(stats.newFrameFps)
                + " decode=" + formatFpsValueDetailed(stats.decodeFps)
                + " renderCallback=" + formatFpsValueDetailed(stats.renderFps)
                + " vsyncCallback=" + formatFpsValueDetailed(currentDisplayCallbackFps())
                + " latency=" + Math.max(0L, stats.localPipelineLatencyMs) + "ms"
                + " state=" + (stats.state == null ? "" : stats.state)
                + " packets=" + stats.packetsReceived
                + " decoded=" + stats.framesDecoded
                + " rendered=" + stats.framesRendered
                + " errors=" + stats.decodeErrors
                + " drops=" + stats.droppedFrames);
    }

    private void appendFpsFields(StringBuilder builder) {
        appendFpsField(builder, "stream", currentStreamFps());
        builder.append("  ");
        appendFpsField(builder, "new", currentNewFrameFps());
        builder.append("  ");
        appendFpsField(builder, "decode", currentDecodeFps());
    }

    private void appendDisplayTimingFields(StringBuilder builder) {
        appendFpsField(builder, "renderCb", currentRenderFps());
        builder.append("  ");
        appendFpsField(builder, "vsyncCb", currentDisplayCallbackFps());
        builder.append("  displayHz ").append(formatRefreshValue(currentDisplayRefreshHz()));
        builder.append("  targetFps ").append(targetFps());
    }

    private void appendFpsField(StringBuilder builder, String label, double value) {
        builder.append(label).append(' ').append(formatFpsValue(value));
    }

    private double currentStreamFps() {
        return lastEncoderStatus == null ? Double.NaN : lastEncoderStatus.streamFps;
    }

    private double currentNewFrameFps() {
        return lastVideoStats == null ? Double.NaN : lastVideoStats.newFrameFps;
    }

    private double currentDecodeFps() {
        return lastVideoStats == null ? Double.NaN : lastVideoStats.decodeFps;
    }

    private double currentRenderFps() {
        return lastVideoStats == null ? Double.NaN : lastVideoStats.renderFps;
    }

    private double currentDisplayCallbackFps() {
        return displayCallbackFps <= 0.0 ? Double.NaN : displayCallbackFps;
    }

    private float currentDisplayRefreshHz() {
        return displayRefreshHz > 0f ? displayRefreshHz : readDisplayRefreshHz();
    }

    private String formatFpsValue(double fps) {
        if (Double.isNaN(fps) || Double.isInfinite(fps) || fps < 0.0) {
            return "--";
        }

        return String.format(Locale.ROOT, "%.0f", Math.max(0.0, fps));
    }

    private String formatRefreshValue(float refreshHz) {
        if (Float.isNaN(refreshHz) || Float.isInfinite(refreshHz) || refreshHz <= 0f) {
            return "--";
        }

        return String.format(Locale.ROOT, "%.0f", refreshHz);
    }

    private String formatFpsValueDetailed(double fps) {
        if (Double.isNaN(fps) || Double.isInfinite(fps) || fps < 0.0) {
            return "--";
        }

        return String.format(Locale.ROOT, "%.1f", Math.max(0.0, fps));
    }

    private ControlClient.CaptureStatus mergeCaptureStatus(ControlClient.CaptureStatus previous, ControlClient.CaptureStatus next) {
        if (previous == null) {
            return next;
        }

        String source = next.source.length() > 0 ? next.source : previous.source;
        String target = next.target.length() > 0 ? next.target : previous.target;
        long framesCaptured = next.framesCaptured > 0L ? next.framesCaptured : previous.framesCaptured;
        long framesConverted = next.framesConverted > 0L ? next.framesConverted : previous.framesConverted;
        long captureErrors = next.captureErrors > 0L ? next.captureErrors : previous.captureErrors;
        double avgCaptureMs = next.avgCaptureMs > 0.0 ? next.avgCaptureMs : previous.avgCaptureMs;
        double avgConvertMs = next.avgConvertMs > 0.0 ? next.avgConvertMs : previous.avgConvertMs;
        double gpuConvertMs = next.gpuConvertMs > 0.0 ? next.gpuConvertMs : previous.gpuConvertMs;
        long framesDropped = next.framesDropped > 0L ? next.framesDropped : previous.framesDropped;
        double lastFrameAgeMs = next.lastFrameAgeMs > 0.0 ? next.lastFrameAgeMs : previous.lastFrameAgeMs;
        boolean gpuPath = next.gpuPath || previous.gpuPath && next.source.length() == 0;
        String fallback = next.fallback.length() > 0 ? next.fallback : previous.fallback;
        String errorCode = next.errorCode.length() > 0 ? next.errorCode : ("ERROR".equals(next.state) ? previous.errorCode : "");
        String errorMessage = next.errorMessage.length() > 0 ? next.errorMessage : ("ERROR".equals(next.state) ? previous.errorMessage : "");
        return new ControlClient.CaptureStatus(
            next.state,
            source,
            target,
            framesCaptured,
            framesConverted,
            captureErrors,
            avgCaptureMs,
            avgConvertMs,
            gpuConvertMs,
            framesDropped,
            lastFrameAgeMs,
            gpuPath,
            fallback,
            errorCode,
            errorMessage
        );
    }

    private int dp(int value, float density) {
        return (int) (value * density + 0.5f);
    }

    private int clampInt(int value, int min, int max) {
        return Math.max(min, Math.min(max, value));
    }

    private String labelFor(ConnectionState state) {
        switch (state) {
            case CONNECTING:
                return "连接中";
            case CONNECTED:
                return "已连接";
            case RECONNECTING:
                return "正在重连";
            case FAILED:
                return "连接失败";
            case DISCONNECTED:
            default:
                return "已断开";
        }
    }
}
