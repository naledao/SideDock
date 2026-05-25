package com.sidedock.client;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.Path;
import android.view.View;

public final class CursorOverlayView extends View {
    private final Paint strokePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint fillPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path cursorPath = new Path();
    private float cursorX;
    private float cursorY;
    private boolean visible;
    private boolean hasPosition;

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
    }

    public void setCursorVisible(boolean nextVisible) {
        visible = nextVisible;
        invalidate();
    }

    public boolean isCursorVisible() {
        return visible;
    }

    public void updateCursor(float x, float y) {
        cursorX = x;
        cursorY = y;
        hasPosition = true;
        invalidate();
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        if (!visible || !hasPosition) {
            return;
        }

        float density = getResources().getDisplayMetrics().density;
        float size = 24f * density;
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
}
