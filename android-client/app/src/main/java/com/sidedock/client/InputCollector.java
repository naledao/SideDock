package com.sidedock.client;

import android.os.SystemClock;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.View;

public final class InputCollector {
    public interface Listener {
        void onKeyboardInput(String action, int androidKeyCode, int scanCode, int metaState, int repeatCount);
        void onPointerAbsInput(float nx, float ny, int buttons, float viewX, float viewY);
        void onMouseMoveInput(int dx, int dy);
        void onMouseButtonInput(String button, String action);
        void onMouseWheelInput(int dx, int dy);
        void onInputStats(InputStats stats);
    }

    public static final class InputStats {
        public final long keyboardEvents;
        public final long pointerAbsEvents;
        public final long mouseMoveEvents;
        public final long mouseButtonEvents;
        public final long mouseWheelEvents;
        public final String lastInputType;

        private InputStats(
            long keyboardEvents,
            long pointerAbsEvents,
            long mouseMoveEvents,
            long mouseButtonEvents,
            long mouseWheelEvents,
            String lastInputType
        ) {
            this.keyboardEvents = keyboardEvents;
            this.pointerAbsEvents = pointerAbsEvents;
            this.mouseMoveEvents = mouseMoveEvents;
            this.mouseButtonEvents = mouseButtonEvents;
            this.mouseWheelEvents = mouseWheelEvents;
            this.lastInputType = lastInputType;
        }
    }

    private static final int WHEEL_DELTA = 120;
    private static final long POINTER_ABS_MIN_INTERVAL_MS = 8L;

    private final Listener listener;
    private float lastMouseX;
    private float lastMouseY;
    private boolean hasLastMousePosition;
    private int lastButtonState;
    private int videoRectLeft;
    private int videoRectTop;
    private int videoRectWidth;
    private int videoRectHeight;
    private long lastPointerAbsSentAtMs;
    private float pendingPointerNx;
    private float pendingPointerNy;
    private float pendingPointerViewX;
    private float pendingPointerViewY;
    private int pendingPointerButtons;
    private boolean hasPendingPointerAbs;
    private long keyboardEvents;
    private long pointerAbsEvents;
    private long mouseMoveEvents;
    private long mouseButtonEvents;
    private long mouseWheelEvents;
    private long lastStatsAtMs;
    private String lastInputType = "none";

    public InputCollector(Listener listener) {
        this.listener = listener;
        setVideoRectToView(1, 1);
    }

    public void setVideoRect(int left, int top, int width, int height) {
        videoRectLeft = left;
        videoRectTop = top;
        videoRectWidth = Math.max(1, width);
        videoRectHeight = Math.max(1, height);
    }

    public void setVideoRectToView(int width, int height) {
        setVideoRect(0, 0, width, height);
    }

    public boolean handleKeyEvent(KeyEvent event) {
        if (handleDirectionalWheelKey(event)) {
            return true;
        }

        int actionCode = event.getAction();
        if (actionCode != KeyEvent.ACTION_DOWN && actionCode != KeyEvent.ACTION_UP) {
            return false;
        }

        String action = actionCode == KeyEvent.ACTION_DOWN ? "down" : "up";
        keyboardEvents += 1;
        lastInputType = "keyboard";
        listener.onKeyboardInput(
            action,
            event.getKeyCode(),
            event.getScanCode(),
            event.getMetaState(),
            event.getRepeatCount()
        );
        emitStatsIfNeeded();
        return true;
    }

    private boolean handleDirectionalWheelKey(KeyEvent event) {
        int source = event.getSource();
        boolean pointerLikeSource =
            (source & InputDevice.SOURCE_MOUSE) == InputDevice.SOURCE_MOUSE
                || (source & InputDevice.SOURCE_TRACKBALL) == InputDevice.SOURCE_TRACKBALL;
        if (!pointerLikeSource || event.getAction() != KeyEvent.ACTION_DOWN || event.getRepeatCount() > 0) {
            return false;
        }

        int dx = 0;
        int dy = 0;
        switch (event.getKeyCode()) {
            case KeyEvent.KEYCODE_DPAD_UP:
                dy = WHEEL_DELTA;
                break;
            case KeyEvent.KEYCODE_DPAD_DOWN:
                dy = -WHEEL_DELTA;
                break;
            case KeyEvent.KEYCODE_DPAD_LEFT:
                dx = -WHEEL_DELTA;
                break;
            case KeyEvent.KEYCODE_DPAD_RIGHT:
                dx = WHEEL_DELTA;
                break;
            default:
                return false;
        }

        mouseWheelEvents += 1;
        lastInputType = "mouse_wheel";
        listener.onMouseWheelInput(dx, dy);
        emitStatsIfNeeded();
        return true;
    }

    public boolean handleGenericMotionEvent(MotionEvent event) {
        if (!isMouseEvent(event)) {
            return false;
        }

        boolean handled = false;
        int action = event.getActionMasked();
        if (action == MotionEvent.ACTION_HOVER_MOVE || action == MotionEvent.ACTION_MOVE) {
            handled |= handleMouseMove(event);
        } else if (action == MotionEvent.ACTION_BUTTON_PRESS || action == MotionEvent.ACTION_BUTTON_RELEASE) {
            handled |= handleButtonState(event);
        } else if (action == MotionEvent.ACTION_HOVER_ENTER) {
            rememberMousePosition(event);
            emitPointerAbsIfNeeded(event, true);
            handled = true;
        } else if (action == MotionEvent.ACTION_HOVER_EXIT) {
            hasLastMousePosition = false;
            handled = true;
        }

        handled |= handleMouseWheel(event);
        if (handled) {
            emitStatsIfNeeded();
        }
        return handled;
    }

    public boolean handleTouchEvent(MotionEvent event) {
        if (!isMouseEvent(event)) {
            return false;
        }

        boolean handled = false;
        int action = event.getActionMasked();
        if (action == MotionEvent.ACTION_MOVE) {
            handled |= handleMouseMove(event);
        } else if (action == MotionEvent.ACTION_DOWN || action == MotionEvent.ACTION_UP) {
            handled |= handleButtonState(event);
            if (event.getButtonState() == 0) {
                String button = buttonNameForActionButton(event.getActionButton());
                if (button == null) {
                    button = "left";
                }
                if (button != null) {
                    recordMouseButton(button, action == MotionEvent.ACTION_DOWN ? "down" : "up");
                    handled = true;
                }
            }
        }

        if (handled) {
            emitStatsIfNeeded();
        }
        return handled;
    }

    public InputStats snapshot() {
        return new InputStats(
            keyboardEvents,
            pointerAbsEvents,
            mouseMoveEvents,
            mouseButtonEvents,
            mouseWheelEvents,
            lastInputType
        );
    }

    private boolean handleMouseMove(MotionEvent event) {
        float x = event.getX();
        float y = event.getY();
        if (!hasLastMousePosition) {
            rememberMousePosition(event);
            emitPointerAbsIfNeeded(event, true);
            return true;
        }

        int dx = Math.round(x - lastMouseX);
        int dy = Math.round(y - lastMouseY);
        rememberMousePosition(event);
        if (dx == 0 && dy == 0) {
            return true;
        }

        mouseMoveEvents += 1;
        lastInputType = "mouse_move";
        emitPointerAbsIfNeeded(event, false);
        listener.onMouseMoveInput(dx, dy);
        return true;
    }

    private boolean handleButtonState(MotionEvent event) {
        int nextButtonState = event.getButtonState();
        int changed = lastButtonState ^ nextButtonState;
        boolean handled = false;

        handled |= emitButtonIfChanged(changed, nextButtonState, MotionEvent.BUTTON_PRIMARY, "left");
        handled |= emitButtonIfChanged(changed, nextButtonState, MotionEvent.BUTTON_SECONDARY, "right");
        handled |= emitButtonIfChanged(changed, nextButtonState, MotionEvent.BUTTON_TERTIARY, "middle");

        if (!handled) {
            String button = buttonNameForActionButton(event.getActionButton());
            if (button != null) {
                recordMouseButton(button, event.getActionMasked() == MotionEvent.ACTION_BUTTON_RELEASE ? "up" : "down");
                handled = true;
            }
        }

        lastButtonState = nextButtonState;
        rememberMousePosition(event);
        emitPointerAbsIfNeeded(event, true);
        return handled;
    }

    private boolean emitButtonIfChanged(int changed, int nextButtonState, int mask, String button) {
        if ((changed & mask) == 0) {
            return false;
        }

        recordMouseButton(button, (nextButtonState & mask) != 0 ? "down" : "up");
        return true;
    }

    private void recordMouseButton(String button, String action) {
        mouseButtonEvents += 1;
        lastInputType = "mouse_button";
        listener.onMouseButtonInput(button, action);
    }

    private boolean handleMouseWheel(MotionEvent event) {
        float vertical = event.getAxisValue(MotionEvent.AXIS_VSCROLL);
        float horizontal = event.getAxisValue(MotionEvent.AXIS_HSCROLL);
        if (vertical == 0f && horizontal == 0f) {
            return false;
        }

        int dx = Math.round(horizontal * WHEEL_DELTA);
        int dy = Math.round(vertical * WHEEL_DELTA);
        if (dx == 0 && horizontal != 0f) {
            dx = horizontal > 0f ? WHEEL_DELTA : -WHEEL_DELTA;
        }
        if (dy == 0 && vertical != 0f) {
            dy = vertical > 0f ? WHEEL_DELTA : -WHEEL_DELTA;
        }

        mouseWheelEvents += 1;
        lastInputType = "mouse_wheel";
        emitPointerAbsIfNeeded(event, true);
        listener.onMouseWheelInput(dx, dy);
        return true;
    }

    private void emitPointerAbsIfNeeded(MotionEvent event, boolean force) {
        if (videoRectWidth <= 0 || videoRectHeight <= 0) {
            return;
        }

        float x = event.getX();
        float y = event.getY();
        float clampedX = clamp(x, videoRectLeft, videoRectLeft + videoRectWidth);
        float clampedY = clamp(y, videoRectTop, videoRectTop + videoRectHeight);
        float nx = clamp((clampedX - videoRectLeft) / (float) videoRectWidth, 0f, 1f);
        float ny = clamp((clampedY - videoRectTop) / (float) videoRectHeight, 0f, 1f);
        long now = SystemClock.uptimeMillis();

        if (!force && now - lastPointerAbsSentAtMs < POINTER_ABS_MIN_INTERVAL_MS) {
            pendingPointerNx = nx;
            pendingPointerNy = ny;
            pendingPointerViewX = clampedX;
            pendingPointerViewY = clampedY;
            pendingPointerButtons = toButtonMask(event.getButtonState());
            hasPendingPointerAbs = true;
            return;
        }

        sendPointerAbs(nx, ny, toButtonMask(event.getButtonState()), clampedX, clampedY, now);
    }

    public void flushPendingPointerAbs() {
        if (!hasPendingPointerAbs) {
            return;
        }

        long now = SystemClock.uptimeMillis();
        if (now - lastPointerAbsSentAtMs < POINTER_ABS_MIN_INTERVAL_MS) {
            return;
        }

        hasPendingPointerAbs = false;
        sendPointerAbs(
            pendingPointerNx,
            pendingPointerNy,
            pendingPointerButtons,
            pendingPointerViewX,
            pendingPointerViewY,
            now
        );
    }

    private void sendPointerAbs(float nx, float ny, int buttons, float viewX, float viewY, long nowMs) {
        lastPointerAbsSentAtMs = nowMs;
        pointerAbsEvents += 1;
        lastInputType = "pointer_abs";
        listener.onPointerAbsInput(nx, ny, buttons, viewX, viewY);
    }

    private void emitStatsIfNeeded() {
        long now = SystemClock.uptimeMillis();
        if (now - lastStatsAtMs < 1000L) {
            return;
        }

        lastStatsAtMs = now;
        listener.onInputStats(snapshot());
    }

    private void rememberMousePosition(MotionEvent event) {
        lastMouseX = event.getX();
        lastMouseY = event.getY();
        hasLastMousePosition = true;
    }

    private int toButtonMask(int buttonState) {
        int mask = 0;
        if ((buttonState & MotionEvent.BUTTON_PRIMARY) != 0) {
            mask |= 1;
        }
        if ((buttonState & MotionEvent.BUTTON_SECONDARY) != 0) {
            mask |= 2;
        }
        if ((buttonState & MotionEvent.BUTTON_TERTIARY) != 0) {
            mask |= 4;
        }
        return mask;
    }

    private float clamp(float value, float min, float max) {
        return Math.max(min, Math.min(max, value));
    }

    private boolean isMouseEvent(MotionEvent event) {
        if (event.isFromSource(InputDevice.SOURCE_MOUSE)) {
            return true;
        }

        if (event.getPointerCount() == 0) {
            return false;
        }

        return event.getToolType(0) == MotionEvent.TOOL_TYPE_MOUSE;
    }

    private String buttonNameForActionButton(int actionButton) {
        if ((actionButton & MotionEvent.BUTTON_PRIMARY) != 0) {
            return "left";
        }
        if ((actionButton & MotionEvent.BUTTON_SECONDARY) != 0) {
            return "right";
        }
        if ((actionButton & MotionEvent.BUTTON_TERTIARY) != 0) {
            return "middle";
        }
        return null;
    }
}
