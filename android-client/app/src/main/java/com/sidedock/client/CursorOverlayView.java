package com.sidedock.client;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.PorterDuff;
import android.graphics.Rect;
import android.view.View;

public final class CursorOverlayView extends View {
    private final Paint strokePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint fillPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path cursorPath = new Path();
    private final Rect currentBounds = new Rect();
    private final Rect previousBounds = new Rect();
    private float cursorX;
    private float cursorY;
    private boolean visible;
    private boolean hasPosition;
    private final float cursorSizePx;
    private final float cursorPaddingPx;

    public CursorOverlayView(Context context) {
        super(context);
        setWillNotDraw(false);
        setClickable(false);
        setFocusable(false);

        float density = getResources().getDisplayMetrics().density;
        strokePaint.setStyle(Paint.Style.STROKE);
        strokePaint.setStrokeWidth(Math.max(2f, 2f * density));
        strokePaint.setStrokeJoin(Paint.Join.ROUND);
        strokePaint.setStrokeCap(Paint.Cap.ROUND);
        strokePaint.setColor(0xFF000000);

        fillPaint.setStyle(Paint.Style.FILL);
        fillPaint.setColor(0xFFFFFFFF);
        cursorSizePx = 24f * density;
        cursorPaddingPx = Math.max(strokePaint.getStrokeWidth() * 2f, 4f * density);
    }

    public void setCursorVisible(boolean nextVisible) {
        if (visible == nextVisible) {
            return;
        }

        Rect dirty = null;
        if (hasPosition) {
            dirty = new Rect(currentBounds);
        }
        visible = nextVisible;
        if (dirty != null && !dirty.isEmpty()) {
            postInvalidateOnAnimation(dirty.left, dirty.top, dirty.right, dirty.bottom);
        } else {
            postInvalidateOnAnimation();
        }
    }

    public boolean isCursorVisible() {
        return visible;
    }

    public void updateCursor(float x, float y) {
        previousBounds.set(currentBounds);
        cursorX = x;
        cursorY = y;
        hasPosition = true;
        updateBounds(currentBounds, x, y);

        if (visible) {
            Rect dirty = new Rect(previousBounds);
            dirty.union(currentBounds);
            if (dirty.isEmpty()) {
                dirty.set(currentBounds);
            }
            postInvalidateOnAnimation(dirty.left, dirty.top, dirty.right, dirty.bottom);
        }
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        canvas.drawColor(Color.TRANSPARENT, PorterDuff.Mode.CLEAR);
        if (!visible || !hasPosition) {
            return;
        }

        float size = cursorSizePx;
        cursorPath.reset();
        cursorPath.moveTo(cursorX, cursorY);
        cursorPath.lineTo(cursorX, cursorY + size);
        cursorPath.lineTo(cursorX + size * 0.28f, cursorY + size * 0.72f);
        cursorPath.lineTo(cursorX + size * 0.48f, cursorY + size * 1.15f);
        cursorPath.lineTo(cursorX + size * 0.66f, cursorY + size * 1.06f);
        cursorPath.lineTo(cursorX + size * 0.46f, cursorY + size * 0.64f);
        cursorPath.lineTo(cursorX + size * 0.86f, cursorY + size * 0.64f);
        cursorPath.close();

        canvas.drawPath(cursorPath, strokePaint);
        canvas.drawPath(cursorPath, fillPaint);
    }

    private void updateBounds(Rect target, float x, float y) {
        int left = (int) Math.floor(x - cursorPaddingPx);
        int top = (int) Math.floor(y - cursorPaddingPx);
        int right = (int) Math.ceil(x + cursorSizePx * 0.95f + cursorPaddingPx);
        int bottom = (int) Math.ceil(y + cursorSizePx * 1.25f + cursorPaddingPx);
        target.set(left, top, right, bottom);
    }
}
