package com.sidedock.client;

import android.app.Activity;
import android.app.KeyguardManager;
import android.content.Context;
import android.content.res.Configuration;
import android.graphics.drawable.GradientDrawable;
import android.media.MediaCodecInfo;
import android.media.MediaCodecList;
import android.os.Build;
import android.os.Handler;
import android.os.Bundle;
import android.os.Looper;
import android.util.Log;
import android.view.Gravity;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.PointerIcon;
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

public final class MainActivity extends Activity implements ControlClient.Listener, VideoClient.Listener, SurfaceHolder.Callback, InputCollector.Listener {
    private static final String TAG = "SideDock";
    private static final int DEFAULT_VIDEO_PORT = 27184;
    private static final int DEFAULT_VIDEO_WIDTH = 1280;
    private static final int DEFAULT_VIDEO_HEIGHT = 720;
    private static final int DEFAULT_VIDEO_FPS = 30;
    private static final String VIDEO_CODEC_AVC = "video/avc";
    private static final String ERROR_DECODER_UNSUPPORTED = "DECODER_UNSUPPORTED";
    private static final int OVERLAY_MODE_DETAILED = 0;
    private static final int OVERLAY_MODE_COMPACT = 1;
    private static final int OVERLAY_MODE_HIDDEN = 2;
    private static final long OVERLAY_TAP_MAX_DURATION_MS = 500L;
    private static final long LOCAL_POINTER_PREVIEW_TIMEOUT_MS = 120L;
    private static final String[] RESOLUTION_LABELS = new String[] { "720p", "1080p", "2K" };
    private static final int[][] RESOLUTION_PRESETS = new int[][] {
        { 1280, 720 },
        { 1920, 1080 },
        { 2560, 1440 }
    };
    private static final int[] REFRESH_PRESETS = new int[] { 30, 60, 120 };

    private ControlClient controlClient;
    private VideoClient videoClient;
    private InputCollector inputCollector;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final Runnable pointerAbsFlushRunnable = new Runnable() {
        @Override
        public void run() {
            flushPendingPointerAbs();
        }
    };
    private final Runnable localPointerPreviewTimeoutRunnable = new Runnable() {
        @Override
        public void run() {
            expireLocalPointerPreview();
        }
    };
    private FrameLayout rootView;
    private SurfaceView surfaceView;
    private CursorOverlayView cursorOverlayView;
    private TextView overlayText;
    private TextView modeToggleText;
    private LinearLayout modePanel;
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
    private long lastRenderedFramesSeen;
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
    private String cursorOverlayState = "hidden";
    private boolean localPointerPreviewActive;
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
    private long lastCursorOverlayDebugAtMs;
    private boolean overlayTapCandidate;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        keepWindowReadyForVideoSurface();

        controlClient = new ControlClient(this);
        videoClient = new VideoClient(this);
        inputCollector = new InputCollector(this);
        setContentView(buildContentView());
        enterImmersiveMode();
        hideSystemPointerIcon(getWindow().getDecorView());
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
        maybeStartVideo();
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) {
            enterImmersiveMode();
            hideSystemPointerIcon(getWindow().getDecorView());
        }
    }

    @Override
    public void onConfigurationChanged(Configuration newConfig) {
        super.onConfigurationChanged(newConfig);
        updateVideoRectForSurfaceView();
        resetCursorToCenter();
        addLog("Configuration changed, recalculated video rect");
    }

    @Override
    protected void onDestroy() {
        mainHandler.removeCallbacks(pointerAbsFlushRunnable);
        mainHandler.removeCallbacks(localPointerPreviewTimeoutRunnable);
        videoClient.stop();
        controlClient.shutdown();
        super.onDestroy();
    }

    @Override
    public void surfaceCreated(SurfaceHolder holder) {
        activeSurface = holder.getSurface();
        surfaceReady = activeSurface != null && activeSurface.isValid();
        waitingForVideoFrame = true;
        hideLocalCursorOverlay();
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
        sendVideoReadyIfSurfaceReady();
        Log.i(TAG, "surfaceChanged ready=" + surfaceReady + " size=" + width + "x" + height);
    }

    @Override
    public void surfaceDestroyed(SurfaceHolder holder) {
        surfaceReady = false;
        activeSurface = null;
        waitingForVideoFrame = true;
        videoClient.stop();
        hideLocalCursorOverlay();
        addLog("Surface 已销毁，停止视频通道");
        Log.i(TAG, "surfaceDestroyed");
    }

    @Override
    public void onStateChanged(ConnectionState state) {
        controlConnectionState = state;
        controlState = labelFor(state);
        if (state != ConnectionState.CONNECTED) {
            waitingForVideoFrame = true;
            hideLocalCursorOverlay();
        }
        if (state == ConnectionState.CONNECTED) {
            sendVideoReadyIfSurfaceReady();
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
        videoStartReceived = true;
        hideLocalCursorOverlay();
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
    public void onServerTime(long serverTimeMs) {
        long nextOffsetMs = serverTimeMs - System.currentTimeMillis();
        serverTimeOffsetMs = serverTimeOffsetInitialized
            ? Math.round((serverTimeOffsetMs * 0.75) + (nextOffsetMs * 0.25))
            : nextOffsetMs;
        serverTimeOffsetInitialized = true;
        videoClient.setServerTimeOffsetMs(serverTimeOffsetMs);
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
        if (metrics.refreshHz > 0) {
            selectedModeRefresh = normalizeObservedRefresh(metrics.refreshHz);
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
        if (mode.success) {
            selectedModeWidth = mode.width > 0 ? mode.width : selectedModeWidth;
            selectedModeHeight = mode.height > 0 ? mode.height : selectedModeHeight;
            selectedModeRefresh = mode.refreshHz > 0 ? normalizeObservedRefresh(mode.refreshHz) : selectedModeRefresh;
            videoWidth = selectedModeWidth;
            videoHeight = selectedModeHeight;
            videoFps = selectedModeRefresh;
            waitingForVideoFrame = true;
            hideLocalCursorOverlay();
            if (surfaceView != null) {
                applySurfaceFixedSize(videoWidth, videoHeight);
            }
            updateVideoRectForSurfaceView();
            videoClient.stop();
            sendVideoReadyIfSurfaceReady();
        }
        addLog("Display mode " + (mode.success ? "changed " : "failed ")
            + mode.width + "x" + mode.height + "@" + mode.refreshHz
            + (mode.message.length() == 0 ? "" : " " + mode.message));
        updateModeControls();
        updateOverlay();
    }

    @Override
    public void onCursorShape(String kind, boolean visible) {
        cursorKind = kind == null || kind.length() == 0 ? "arrow" : kind;
        // Host-side cursor_shape.visible is metadata for the remote cursor shape.
        // It must not suppress the Android local cursor overlay.
        updateOverlay();
    }

    @Override
    public void onCursorState(ControlClient.CursorState state) {
        lastCursorState = state;
        if (cursorOverlayView != null && !localPointerPreviewActive) {
            renderRemoteCursorState(state);
        }
    }

    @Override
    public void onLocalPointerPreview(float viewX, float viewY) {
        if (cursorOverlayView == null) {
            return;
        }

        if (!isLocalCursorOverlayAllowed()) {
            hideLocalCursorOverlay();
            cursorOverlayState = "blocked:" + localCursorOverlayBlockReason();
            updateOverlayForCursorDebug();
            schedulePointerAbsFlush();
            return;
        }

        localPointerPreviewActive = true;
        cursorOverlayState = String.format(Locale.ROOT, "local %.0f,%.0f", viewX, viewY);
        cursorOverlayView.setCursorVisible(true);
        cursorOverlayView.updateCursor(viewX, viewY);
        scheduleLocalPointerPreviewTimeout();
        schedulePointerAbsFlush();
    }

    @Override
    public void onLocalPointerExit() {
        hideLocalCursorOverlay();
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
            hideLocalCursorOverlay();
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
            stats.lastEncodeMs,
            stats.state
        );
        updateOverlay();
    }

    @Override
    public void onVideoError(String code, String message) {
        lastVideoError = code + ": " + message;
        waitingForVideoFrame = true;
        hideLocalCursorOverlay();
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
                cycleOverlayMode();
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

    private boolean isMouseLikeEvent(MotionEvent event) {
        if (event.isFromSource(InputDevice.SOURCE_MOUSE)) {
            return true;
        }

        return event.getPointerCount() > 0 && event.getToolType(0) == MotionEvent.TOOL_TYPE_MOUSE;
    }

    private void cycleOverlayMode() {
        overlayMode = (overlayMode + 1) % 3;
        updateOverlay();
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
        hideSystemPointerIcon(rootView);
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
        hideSystemPointerIcon(surfaceView);
        surfaceView.requestFocus();
        rootView.addView(surfaceView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.MATCH_PARENT
        ));

        cursorOverlayView = new CursorOverlayView(this);
        hideSystemPointerIcon(cursorOverlayView);
        rootView.addView(cursorOverlayView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.MATCH_PARENT
        ));
        cursorOverlayView.bringToFront();

        connectionStatusLayer = createConnectionStatusLayer(density);
        rootView.addView(connectionStatusLayer, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.MATCH_PARENT
        ));

        overlayText = new TextView(this);
        overlayText.setTextColor(0xFFE9F0F2);
        overlayText.setTextSize(12f);
        overlayText.setGravity(Gravity.START);
        overlayText.setBackgroundColor(0x99000000);
        overlayText.setPadding(dp(10, density), dp(8, density), dp(10, density), dp(8, density));
        overlayText.setText("SideDock");
        hideSystemPointerIcon(overlayText);

        FrameLayout.LayoutParams overlayParams = new FrameLayout.LayoutParams(
            dp(360, density),
            ViewGroup.LayoutParams.WRAP_CONTENT,
            Gravity.START | Gravity.TOP
        );
        overlayParams.setMargins(dp(12, density), dp(12, density), dp(12, density), dp(12, density));
        rootView.addView(overlayText, overlayParams);

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
        hideSystemPointerIcon(layer);

        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setGravity(Gravity.CENTER_HORIZONTAL);
        panel.setPadding(dp(22, density), dp(22, density), dp(22, density), dp(22, density));
        panel.setBackground(makeRoundedBackground(0xE6111820, 0xFF314555, dp(8, density)));
        hideSystemPointerIcon(panel);

        connectionStatusProgress = new ProgressBar(this);
        connectionStatusProgress.setIndeterminate(true);
        hideSystemPointerIcon(connectionStatusProgress);
        panel.addView(connectionStatusProgress, new LinearLayout.LayoutParams(
            dp(34, density),
            dp(34, density)
        ));

        connectionStatusTitle = new TextView(this);
        connectionStatusTitle.setTextColor(0xFFFFFFFF);
        connectionStatusTitle.setTextSize(20f);
        connectionStatusTitle.setGravity(Gravity.CENTER);
        connectionStatusTitle.setPadding(0, dp(14, density), 0, 0);
        hideSystemPointerIcon(connectionStatusTitle);
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
        hideSystemPointerIcon(connectionStatusDetail);
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
        hideSystemPointerIcon(connectionStatusHint);
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

    private TextView createModeToggle(float density) {
        TextView button = new TextView(this);
        button.setTextColor(0xFFFFFFFF);
        button.setTextSize(13f);
        button.setGravity(Gravity.CENTER);
        button.setMinWidth(dp(116, density));
        button.setPadding(dp(14, density), 0, dp(14, density), 0);
        button.setBackground(makeRoundedBackground(0xCC111820, 0xFF46657A, dp(8, density)));
        button.setText("Mode");
        hideSystemPointerIcon(button);
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
        hideSystemPointerIcon(panel);

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
        hideSystemPointerIcon(row);
        return row;
    }

    private TextView createPanelLabel(String label) {
        TextView view = new TextView(this);
        view.setText(label);
        view.setTextColor(0xFFB9C6CC);
        view.setTextSize(11f);
        view.setGravity(Gravity.START);
        hideSystemPointerIcon(view);
        return view;
    }

    private TextView createSegmentButton(String label, float density) {
        TextView button = new TextView(this);
        button.setText(label);
        button.setTextColor(0xFFE9F0F2);
        button.setTextSize(13f);
        button.setGravity(Gravity.CENTER);
        button.setPadding(dp(4, density), 0, dp(4, density), 0);
        hideSystemPointerIcon(button);
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
        if (modePanel == null || overlayMode == OVERLAY_MODE_HIDDEN) {
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

        if (width >= 2560 || height >= 1440) {
            return fps > 72;
        }

        Boolean supported = queryAvcDecoderSupport(width, height, fps);
        return supported != null && !supported.booleanValue();
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
                    if (videoCapabilities != null
                        && videoCapabilities.areSizeAndRateSupported(width, height, fps)) {
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
        resetCursorToCenter();
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

    private void resetCursorToCenter() {
        if (cursorOverlayView == null || videoRectWidth <= 0 || videoRectHeight <= 0) {
            return;
        }
        if (!cursorOverlayView.isCursorVisible()) {
            return;
        }

        cursorOverlayView.updateCursor(
            contentRectLeft + contentRectWidth / 2f,
            contentRectTop + contentRectHeight / 2f
        );
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

    private boolean isLocalCursorOverlayAllowed() {
        return cursorOverlayView != null
            && surfaceReady
            && activeSurface != null
            && activeSurface.isValid()
            && controlConnectionState == ConnectionState.CONNECTED
            && "CONNECTED".equals(videoState)
            && !waitingForVideoFrame
            && lastVideoError.length() == 0;
    }

    private void hideLocalCursorOverlay() {
        mainHandler.removeCallbacks(localPointerPreviewTimeoutRunnable);
        localPointerPreviewActive = false;
        cursorOverlayState = "hidden";
        if (cursorOverlayView != null) {
            cursorOverlayView.setCursorVisible(false);
        }
    }

    private void scheduleLocalPointerPreviewTimeout() {
        mainHandler.removeCallbacks(localPointerPreviewTimeoutRunnable);
        mainHandler.postDelayed(localPointerPreviewTimeoutRunnable, LOCAL_POINTER_PREVIEW_TIMEOUT_MS);
    }

    private void expireLocalPointerPreview() {
        if (!localPointerPreviewActive) {
            return;
        }

        localPointerPreviewActive = false;
        if (lastCursorState != null) {
            renderRemoteCursorState(lastCursorState);
            return;
        }

        if (cursorOverlayView != null) {
            cursorOverlayView.setCursorVisible(false);
        }
        cursorOverlayState = "local-timeout";
        updateOverlayForCursorDebug();
    }

    private void renderRemoteCursorState(ControlClient.CursorState state) {
        if (cursorOverlayView == null) {
            return;
        }

        if (!isLocalCursorOverlayAllowed()) {
            hideLocalCursorOverlay();
            cursorOverlayState = "blocked:" + localCursorOverlayBlockReason();
            updateOverlayForCursorDebug();
            return;
        }
        if (!state.visible) {
            cursorOverlayView.setCursorVisible(false);
            cursorOverlayState = "remote-hidden";
            updateOverlayForCursorDebug();
            return;
        }

        float[] normalized = normalizedCursorPosition(state);
        if (normalized != null) {
            cursorOverlayView.setCursorVisible(true);
            float nx = normalized[0];
            float ny = normalized[1];
            float viewX = contentRectLeft + nx * contentRectWidth;
            float viewY = contentRectTop + ny * contentRectHeight;
            cursorOverlayView.updateCursor(viewX, viewY);
            cursorOverlayState = String.format(Locale.ROOT, "remote %.3f,%.3f", nx, ny);
        } else {
            cursorOverlayState = "blocked:no-mapping";
            cursorOverlayView.setCursorVisible(false);
        }
        updateOverlayForCursorDebug();
    }

    private float[] normalizedCursorPosition(ControlClient.CursorState state) {
        if (Double.isFinite(state.nx) && Double.isFinite(state.ny)) {
            return new float[] { clamp01((float) state.nx), clamp01((float) state.ny) };
        }

        int remoteWidth = state.displayWidth > 0
            ? state.displayWidth
            : lastDisplayMetrics != null && lastDisplayMetrics.displayWidth > 0
                ? lastDisplayMetrics.displayWidth
                : videoWidth;
        int remoteHeight = state.displayHeight > 0
            ? state.displayHeight
            : lastDisplayMetrics != null && lastDisplayMetrics.displayHeight > 0
                ? lastDisplayMetrics.displayHeight
                : videoHeight;
        if (remoteWidth <= 0 || remoteHeight <= 0) {
            return null;
        }

        float nx = remoteWidth <= 1 ? 0f : state.x / (float) (remoteWidth - 1);
        float ny = remoteHeight <= 1 ? 0f : state.y / (float) (remoteHeight - 1);
        return new float[] { clamp01(nx), clamp01(ny) };
    }

    private String localCursorOverlayBlockReason() {
        if (cursorOverlayView == null) {
            return "no-overlay";
        }
        if (!surfaceReady) {
            return "surface";
        }
        if (activeSurface == null || !activeSurface.isValid()) {
            return "surface-invalid";
        }
        if (controlConnectionState != ConnectionState.CONNECTED) {
            return "control-" + controlConnectionState;
        }
        if (!"CONNECTED".equals(videoState)) {
            return "video-" + videoState;
        }
        if (waitingForVideoFrame) {
            return "waiting-frame";
        }
        if (lastVideoError.length() > 0) {
            return "video-error";
        }

        return "unknown";
    }

    private void updateOverlayForCursorDebug() {
        long now = System.currentTimeMillis();
        if (now - lastCursorOverlayDebugAtMs < 250L) {
            return;
        }

        lastCursorOverlayDebugAtMs = now;
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
        hideSystemPointerIcon(decorView);
    }

    private void hideSystemPointerIcon(View view) {
        if (view == null) {
            return;
        }

        view.setPointerIcon(PointerIcon.getSystemIcon(this, PointerIcon.TYPE_NULL));
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

        if (lastVideoStats != null) {
            builder
                .append("帧: ").append(lastVideoStats.framesDecoded)
                .append('/').append(lastVideoStats.framesRendered)
                .append("  包: ").append(lastVideoStats.packetsReceived)
                .append("  错误: ").append(lastVideoStats.decodeErrors)
                .append("  丢弃: ").append(lastVideoStats.droppedFrames)
                .append("  重连: ").append(lastVideoStats.reconnects)
                .append("  延迟≈").append(lastVideoStats.roughLatencyMs).append("ms")
                .append("  dec/render ")
                .append(String.format(Locale.ROOT, "%.0f/%.0f", lastVideoStats.decodeFps, lastVideoStats.renderFps))
                .append("fps")
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
                builder.append("  时差").append(serverTimeOffsetMs >= 0 ? "+" : "").append(serverTimeOffsetMs).append("ms");
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
                .append("Cursor: ").append(cursorOverlayState)
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

        long framesRendered = lastVideoStats == null ? 0L : lastVideoStats.framesRendered;
        return waitingForVideoFrame || framesRendered == 0L;
    }

    private void appendCompactOverlay(StringBuilder builder) {
        if (lastVideoStats == null || lastVideoStats.framesRendered == 0L) {
            builder.append("延迟 --ms  帧率 --fps");
            return;
        }

        double currentFps = lastVideoStats.renderFps > 0.0
            ? lastVideoStats.renderFps
            : lastVideoStats.decodeFps;
        builder
            .append("延迟 ").append(Math.max(0L, lastVideoStats.roughLatencyMs)).append("ms")
            .append("  帧率 ")
            .append(String.format(Locale.ROOT, "%.0f", Math.max(0.0, currentFps)))
            .append("fps");
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

    private float clamp01(float value) {
        return Math.max(0f, Math.min(1f, value));
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
