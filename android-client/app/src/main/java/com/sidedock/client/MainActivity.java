package com.sidedock.client;

import android.app.Activity;
import android.content.res.Configuration;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
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

    private ControlClient controlClient;
    private VideoClient videoClient;
    private InputCollector inputCollector;
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
    private ConnectionState controlConnectionState = ConnectionState.DISCONNECTED;
    private String controlState = "已断开";
    private String videoState = "STOPPED";
    private String lastVideoError = "";
    private long controlSent;
    private long controlReceived;
    private long serverTimeOffsetMs;
    private long lastRenderedFramesSeen;
    private boolean waitingForVideoFrame = true;
    private VideoClient.VideoStats lastVideoStats;
    private InputCollector.InputStats lastInputStats;
    private ControlClient.CaptureStatus lastCaptureStatus;
    private ControlClient.DisplayLayout lastDisplayLayout;
    private ControlClient.DisplayMetrics lastDisplayMetrics;
    private ControlClient.DisplayModeChanged lastDisplayModeChanged;
    private String cursorKind = "arrow";
    private int videoRectLeft;
    private int videoRectTop;
    private int videoRectWidth = DEFAULT_VIDEO_WIDTH;
    private int videoRectHeight = DEFAULT_VIDEO_HEIGHT;
    private int contentRectLeft;
    private int contentRectTop;
    private int contentRectWidth = DEFAULT_VIDEO_WIDTH;
    private int contentRectHeight = DEFAULT_VIDEO_HEIGHT;
    private int overlayMode = OVERLAY_MODE_DETAILED;
    private int selectedModeWidth = DEFAULT_VIDEO_WIDTH;
    private int selectedModeHeight = DEFAULT_VIDEO_HEIGHT;
    private int selectedModeRefresh = DEFAULT_VIDEO_FPS;
    private float overlayTapDownX;
    private float overlayTapDownY;
    private long overlayTapDownAtMs;
    private boolean overlayTapCandidate;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        controlClient = new ControlClient(this);
        videoClient = new VideoClient(this);
        inputCollector = new InputCollector(this);
        setContentView(buildContentView());
        enterImmersiveMode();
        hideSystemPointerIcon(getWindow().getDecorView());
        controlClient.start();
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
        videoClient.stop();
        controlClient.shutdown();
        super.onDestroy();
    }

    @Override
    public void surfaceCreated(SurfaceHolder holder) {
        activeSurface = holder.getSurface();
        surfaceReady = activeSurface != null && activeSurface.isValid();
        waitingForVideoFrame = true;
        sendVideoReadyIfSurfaceReady();
        addLog("Surface 已创建，准备接收视频");
        Log.i(TAG, "surfaceCreated ready=" + surfaceReady);
        maybeStartVideo();
        updateOverlay();
    }

    @Override
    public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
        activeSurface = holder.getSurface();
        surfaceReady = activeSurface != null && activeSurface.isValid();
        updateVideoRectForSurfaceView();
        sendVideoReadyIfSurfaceReady();
        Log.i(TAG, "surfaceChanged ready=" + surfaceReady + " size=" + width + "x" + height);
        maybeStartVideo();
    }

    @Override
    public void surfaceDestroyed(SurfaceHolder holder) {
        surfaceReady = false;
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
            maybeStartVideo();
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
        selectedModeWidth = videoWidth;
        selectedModeHeight = videoHeight;
        selectedModeRefresh = normalizeObservedRefresh(videoFps);
        surfaceView.getHolder().setFixedSize(videoWidth, videoHeight);
        updateVideoRectForSurfaceView();
        updateModeControls();
        addLog("收到 video_start " + videoWidth + "x" + videoHeight + "@" + videoFps + " port=" + videoPort);
        Log.i(TAG, "onVideoStart port=" + videoPort + " size=" + videoWidth + "x" + videoHeight + " fps=" + videoFps);
        maybeStartVideo();
    }

    @Override
    public void onServerTime(long serverTimeMs) {
        serverTimeOffsetMs = serverTimeMs - System.currentTimeMillis();
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
    public void onDisplayLayout(ControlClient.DisplayLayout layout) {
        lastDisplayLayout = layout;
        if (layout.videoWidth > 0 && layout.videoHeight > 0) {
            videoWidth = layout.videoWidth;
            videoHeight = layout.videoHeight;
            selectedModeWidth = videoWidth;
            selectedModeHeight = videoHeight;
            surfaceView.getHolder().setFixedSize(videoWidth, videoHeight);
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
            surfaceView.getHolder().setFixedSize(videoWidth, videoHeight);
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
            if (surfaceView != null) {
                surfaceView.getHolder().setFixedSize(videoWidth, videoHeight);
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
        if (cursorOverlayView != null && !visible) {
            cursorOverlayView.setCursorVisible(false);
        }
        updateOverlay();
    }

    @Override
    public void onCursorState(boolean visible, int x, int y) {
        if (cursorOverlayView != null) {
            if (!visible) {
                cursorOverlayView.setCursorVisible(false);
                return;
            }

            if (cursorOverlayView.isCursorVisible() && videoWidth > 0 && videoHeight > 0) {
                float viewX = contentRectLeft + (x / (float) videoWidth) * contentRectWidth;
                float viewY = contentRectTop + (y / (float) videoHeight) * contentRectHeight;
                cursorOverlayView.updateCursor(viewX, viewY);
            }
        }
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
        if (stats.framesDecoded > lastRenderedFramesSeen) {
            lastRenderedFramesSeen = stats.framesDecoded;
            waitingForVideoFrame = false;
            lastVideoError = "";
        }
        controlClient.sendVideoStats(
            stats.framesDecoded,
            stats.packetsReceived,
            stats.decodeErrors,
            stats.droppedFrames,
            stats.reconnects,
            stats.roughLatencyMs,
            stats.state
        );
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
        if (cursorOverlayView != null) {
            cursorOverlayView.setCursorVisible(true);
            cursorOverlayView.updateCursor(viewX, viewY);
        }
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
            inputCollector.flushPendingPointerAbs();
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
            inputCollector.flushPendingPointerAbs();
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

        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(0xFF050607);
        root.setLayoutParams(new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.MATCH_PARENT
        ));
        hideSystemPointerIcon(root);

        surfaceView = new SurfaceView(this);
        surfaceView.getHolder().setFixedSize(videoWidth, videoHeight);
        surfaceView.getHolder().addCallback(this);
        surfaceView.setFocusable(true);
        surfaceView.setFocusableInTouchMode(true);
        hideSystemPointerIcon(surfaceView);
        surfaceView.requestFocus();
        root.addView(surfaceView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.MATCH_PARENT
        ));

        cursorOverlayView = new CursorOverlayView(this);
        hideSystemPointerIcon(cursorOverlayView);
        root.addView(cursorOverlayView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.MATCH_PARENT
        ));

        connectionStatusLayer = createConnectionStatusLayer(density);
        root.addView(connectionStatusLayer, new FrameLayout.LayoutParams(
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
        root.addView(overlayText, overlayParams);

        modeToggleText = createModeToggle(density);
        FrameLayout.LayoutParams modeToggleParams = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT,
            dp(44, density),
            Gravity.END | Gravity.TOP
        );
        modeToggleParams.setMargins(dp(12, density), dp(12, density), dp(12, density), dp(12, density));
        root.addView(modeToggleText, modeToggleParams);

        modePanel = createModePanel(density);
        FrameLayout.LayoutParams modePanelParams = new FrameLayout.LayoutParams(
            dp(268, density),
            ViewGroup.LayoutParams.WRAP_CONTENT,
            Gravity.END | Gravity.TOP
        );
        modePanelParams.setMargins(dp(12, density), dp(64, density), dp(12, density), dp(12, density));
        root.addView(modePanel, modePanelParams);
        updateModeControls();

        updateOverlay();
        root.post(new Runnable() {
            @Override
            public void run() {
                updateVideoRectForSurfaceView();
            }
        });
        return root;
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

        controlClient.sendVideoReady(videoWidth, videoHeight, "video/avc");
        Log.i(TAG, "video_ready sent");
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

        updateContentRectFromMetrics();
        inputCollector.setVideoRect(contentRectLeft, contentRectTop, contentRectWidth, contentRectHeight);
        resetCursorToCenter();
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

        int viewWidth = surfaceView.getWidth();
        int viewHeight = surfaceView.getHeight();
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
                .append("  包: ").append(lastVideoStats.packetsReceived)
                .append("  错误: ").append(lastVideoStats.decodeErrors)
                .append("  丢弃: ").append(lastVideoStats.droppedFrames)
                .append("  重连: ").append(lastVideoStats.reconnects)
                .append("  延迟≈").append(lastVideoStats.roughLatencyMs).append("ms");
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
            if (lastCaptureStatus.errorCode.length() > 0) {
                builder.append("  ").append(lastCaptureStatus.errorCode);
            }
            builder.append('\n');
        }

        InputCollector.InputStats inputStats = lastInputStats != null ? lastInputStats : inputCollector == null ? null : inputCollector.snapshot();
        if (inputStats != null) {
            builder
                .append("输入: 键 ").append(inputStats.keyboardEvents)
                .append("  绝 ").append(inputStats.pointerAbsEvents)
                .append("  移 ").append(inputStats.mouseMoveEvents)
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
                .append("   重连 ").append(lastVideoStats.reconnects);
        }
        if (lastCaptureStatus != null) {
            detail
                .append('\n')
                .append("采集: ").append(lastCaptureStatus.state)
                .append("   帧 ").append(lastCaptureStatus.framesCaptured)
                .append("   错 ").append(lastCaptureStatus.captureErrors);
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

        long framesDecoded = lastVideoStats == null ? 0L : lastVideoStats.framesDecoded;
        return waitingForVideoFrame || framesDecoded == 0L;
    }

    private void appendCompactOverlay(StringBuilder builder) {
        builder
            .append("SideDock  ").append(controlState).append('\n')
            .append("Video: ").append(videoState)
            .append("  ").append(videoWidth).append('x').append(videoHeight).append('@').append(videoFps);

        if (lastVideoStats != null) {
            builder.append("  latency~").append(lastVideoStats.roughLatencyMs).append("ms");
        }
        builder.append('\n');

        if (lastCaptureStatus != null) {
            builder
                .append("Capture: ").append(lastCaptureStatus.source.length() == 0 ? "-" : lastCaptureStatus.source)
                .append(' ').append(lastCaptureStatus.state);

            long captureErrors = lastCaptureStatus.captureErrors;
            long decodeErrors = lastVideoStats == null ? 0L : lastVideoStats.decodeErrors;
            long droppedFrames = lastVideoStats == null ? 0L : lastVideoStats.droppedFrames;
            long reconnects = lastVideoStats == null ? 0L : lastVideoStats.reconnects;
            if (captureErrors > 0 || decodeErrors > 0 || droppedFrames > 0 || reconnects > 0) {
                builder
                    .append("  issues c=").append(captureErrors)
                    .append(" d=").append(decodeErrors)
                    .append(" drop=").append(droppedFrames)
                    .append(" retry=").append(reconnects);
            }
            if (lastCaptureStatus.errorCode.length() > 0) {
                builder.append("  ").append(lastCaptureStatus.errorCode);
            }
            builder.append('\n');
        }

        if (lastDisplayMetrics != null) {
            builder
                .append("Display: ").append(lastDisplayMetrics.source)
                .append("  ").append(lastDisplayMetrics.displayWidth).append('x').append(lastDisplayMetrics.displayHeight)
                .append('@').append(lastDisplayMetrics.refreshHz);
        } else if (lastDisplayLayout != null) {
            builder
                .append("Display: ").append(lastDisplayLayout.source)
                .append("  ").append(lastDisplayLayout.width).append('x').append(lastDisplayLayout.height);
        }
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
