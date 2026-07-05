package com.sidedock.client;

import android.Manifest;
import android.annotation.SuppressLint;
import android.content.Context;
import android.content.pm.PackageManager;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.hardware.camera2.params.StreamConfigurationMap;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.MediaRecorder;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Range;
import android.util.Size;
import android.view.Surface;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.Arrays;
import java.util.Collections;
import java.util.Comparator;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

public final class CameraCaptureClient {
    public interface Listener {
        void onCameraCaptureState(String state, String message);
        void onCameraCaptureStats(long packetsSent, long bytesSent, long keyFrames, long codecConfigPackets);
    }

    private static final int HEADER_SIZE = 40;
    private static final int VERSION = 1;
    private static final int FLAG_KEY_FRAME = 1;
    private static final int FLAG_CODEC_CONFIG = 2;
    private static final int FLAG_END_OF_STREAM = 4;
    private static final int DEFAULT_BITRATE = 4_000_000;
    private static final byte[] MAGIC = new byte[] { 'S', 'D', 'C', 'M' };
    private static final String DEFAULT_CODEC = "video/avc";

    private final Context context;
    private final Listener listener;
    private final Object lifecycleLock = new Object();

    private ExecutorService executor;
    private Socket socket;
    private volatile boolean running;
    private long generation;
    private int port = 27186;
    private int width = 1280;
    private int height = 720;
    private int fps = 30;
    private String codec = DEFAULT_CODEC;

    public CameraCaptureClient(Context context, Listener listener) {
        this.context = context.getApplicationContext();
        this.listener = listener;
    }

    public void start(int nextPort, int nextWidth, int nextHeight, int nextFps, String nextCodec) {
        synchronized (lifecycleLock) {
            int normalizedWidth = nextWidth > 0 ? nextWidth : 1280;
            int normalizedHeight = nextHeight > 0 ? nextHeight : 720;
            int normalizedFps = nextFps > 0 ? nextFps : 30;
            String normalizedCodec = nextCodec == null || nextCodec.trim().isEmpty() ? DEFAULT_CODEC : nextCodec.trim();
            if (running
                && port == nextPort
                && width == normalizedWidth
                && height == normalizedHeight
                && fps == normalizedFps
                && codec.equals(normalizedCodec)) {
                return;
            }

            stopLocked();
            port = nextPort;
            width = normalizedWidth;
            height = normalizedHeight;
            fps = normalizedFps;
            codec = normalizedCodec;
            running = true;
            final long runGeneration = ++generation;
            executor = Executors.newSingleThreadExecutor(new NamedThreadFactory("SideDock-Camera"));
            executor.execute(new Runnable() {
                @Override
                public void run() {
                    cameraLoop(runGeneration);
                }
            });
        }
    }

    public void stop() {
        synchronized (lifecycleLock) {
            stopLocked();
        }
    }

    public boolean isRunning() {
        return running;
    }

    private void stopLocked() {
        running = false;
        generation += 1;
        closeSocket();
        if (executor != null) {
            executor.shutdownNow();
            executor = null;
        }
    }

    private void cameraLoop(long runGeneration) {
        long reconnectDelayMs = 300L;
        while (running && isCurrentGeneration(runGeneration)) {
            if (!hasCameraPermission()) {
                emitState("waiting_permission", "Camera permission is required.");
                return;
            }

            Socket nextSocket = null;
            HandlerThread cameraThread = null;
            MediaCodec encoder = null;
            Surface encoderSurface = null;
            CameraDevice cameraDevice = null;
            CameraCaptureSession captureSession = null;

            try {
                emitState("preparing", "Preparing camera uplink.");

                nextSocket = new Socket();
                nextSocket.setTcpNoDelay(true);
                nextSocket.setSendBufferSize(512 * 1024);
                nextSocket.connect(new InetSocketAddress("127.0.0.1", port), 3000);
                synchronized (lifecycleLock) {
                    if (!running || generation != runGeneration) {
                        closeQuietly(nextSocket);
                        return;
                    }

                    socket = nextSocket;
                }

                CameraManager cameraManager = (CameraManager) context.getSystemService(Context.CAMERA_SERVICE);
                if (cameraManager == null) {
                    throw new IllegalStateException("CameraManager is unavailable.");
                }

                String cameraId = findBackCameraId(cameraManager);
                CameraCharacteristics characteristics = cameraManager.getCameraCharacteristics(cameraId);
                Size captureSize = chooseCaptureSize(characteristics, width, height);

                encoder = createEncoder(codec, captureSize.getWidth(), captureSize.getHeight(), fps);
                encoderSurface = encoder.createInputSurface();
                encoder.start();

                cameraThread = new HandlerThread("SideDock-Camera2");
                cameraThread.start();
                Handler cameraHandler = new Handler(cameraThread.getLooper());

                cameraDevice = openCamera(cameraManager, cameraId, cameraHandler);
                captureSession = createCaptureSession(cameraDevice, encoderSurface, cameraHandler);
                startRepeatingCapture(cameraDevice, captureSession, encoderSurface, characteristics, fps);

                emitState("capturing", "Camera capture started.");
                reconnectDelayMs = 300L;
                drainEncoder(encoder, nextSocket.getOutputStream(), runGeneration);
            } catch (Exception ex) {
                if (running && isCurrentGeneration(runGeneration)) {
                    String state = isNetworkException(ex) ? "disconnected" : "unavailable";
                    emitState(state, exceptionSummary(ex));
                    sleepQuietly(reconnectDelayMs);
                    reconnectDelayMs = Math.min(reconnectDelayMs * 2L, 3000L);
                }
            } finally {
                closeCaptureSession(captureSession);
                closeCamera(cameraDevice);
                releaseSurface(encoderSurface);
                releaseEncoder(encoder);
                stopCameraThread(cameraThread);
                closeSocket(nextSocket);
            }
        }

        if (isCurrentGeneration(runGeneration)) {
            emitState("disconnected", "Camera capture stopped.");
        }
    }

    private void drainEncoder(MediaCodec encoder, OutputStream output, long runGeneration) throws Exception {
        MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();
        byte[] header = new byte[HEADER_SIZE];
        byte[] payload = new byte[0];
        long sequence = 0L;
        long packetsSent = 0L;
        long bytesSent = 0L;
        long keyFrames = 0L;
        long codecConfigPackets = 0L;
        long lastStatsAtMs = 0L;

        while (running && isCurrentGeneration(runGeneration)) {
            int outputIndex = encoder.dequeueOutputBuffer(bufferInfo, 10000L);
            if (outputIndex == MediaCodec.INFO_TRY_AGAIN_LATER) {
                continue;
            }

            if (outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                byte[] config = codecConfigFromFormat(encoder.getOutputFormat());
                if (config.length > 0) {
                    sequence += 1L;
                    writePacket(output, header, config, config.length, sequence, 0L, FLAG_CODEC_CONFIG, runGeneration);
                    packetsSent += 1L;
                    bytesSent += config.length;
                    codecConfigPackets += 1L;
                }
                continue;
            }

            if (outputIndex < 0) {
                continue;
            }

            ByteBuffer outputBuffer = encoder.getOutputBuffer(outputIndex);
            try {
                if (outputBuffer != null && bufferInfo.size > 0) {
                    if (payload.length < bufferInfo.size) {
                        payload = new byte[bufferInfo.size];
                    }
                    outputBuffer.position(bufferInfo.offset);
                    outputBuffer.limit(bufferInfo.offset + bufferInfo.size);
                    outputBuffer.get(payload, 0, bufferInfo.size);

                    int flags = packetFlags(bufferInfo.flags);
                    sequence += 1L;
                    writePacket(output, header, payload, bufferInfo.size, sequence, bufferInfo.presentationTimeUs, flags, runGeneration);
                    packetsSent += 1L;
                    bytesSent += bufferInfo.size;
                    if ((flags & FLAG_KEY_FRAME) != 0) {
                        keyFrames += 1L;
                    }
                    if ((flags & FLAG_CODEC_CONFIG) != 0) {
                        codecConfigPackets += 1L;
                    }

                    long now = System.currentTimeMillis();
                    if (now - lastStatsAtMs >= 1000L) {
                        lastStatsAtMs = now;
                        emitStats(packetsSent, bytesSent, keyFrames, codecConfigPackets);
                    }
                }
            } finally {
                encoder.releaseOutputBuffer(outputIndex, false);
            }

            if ((bufferInfo.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) {
                break;
            }
        }
    }

    private MediaCodec createEncoder(String mime, int encodeWidth, int encodeHeight, int encodeFps) throws Exception {
        if (!DEFAULT_CODEC.equals(mime)) {
            throw new IllegalArgumentException("Unsupported camera codec: " + mime);
        }

        MediaFormat format = MediaFormat.createVideoFormat(mime, encodeWidth, encodeHeight);
        format.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
        format.setInteger(MediaFormat.KEY_BIT_RATE, recommendedBitrate(encodeWidth, encodeHeight, encodeFps));
        format.setInteger(MediaFormat.KEY_FRAME_RATE, encodeFps);
        format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);

        MediaCodec encoder = MediaCodec.createEncoderByType(mime);
        encoder.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
        return encoder;
    }

    @SuppressLint("MissingPermission")
    private CameraDevice openCamera(CameraManager cameraManager, String cameraId, Handler cameraHandler) throws Exception {
        CountDownLatch latch = new CountDownLatch(1);
        AtomicReference<CameraDevice> deviceRef = new AtomicReference<>();
        AtomicReference<Exception> errorRef = new AtomicReference<>();

        cameraManager.openCamera(cameraId, new CameraDevice.StateCallback() {
            @Override
            public void onOpened(CameraDevice camera) {
                deviceRef.set(camera);
                latch.countDown();
            }

            @Override
            public void onDisconnected(CameraDevice camera) {
                errorRef.compareAndSet(null, new IllegalStateException("Camera disconnected."));
                closeCamera(camera);
                latch.countDown();
            }

            @Override
            public void onError(CameraDevice camera, int error) {
                errorRef.compareAndSet(null, new IllegalStateException("Camera error " + error + "."));
                closeCamera(camera);
                latch.countDown();
            }
        }, cameraHandler);

        if (!latch.await(5, TimeUnit.SECONDS)) {
            throw new IllegalStateException("Timed out opening camera.");
        }

        Exception error = errorRef.get();
        if (error != null) {
            throw error;
        }

        CameraDevice device = deviceRef.get();
        if (device == null) {
            throw new IllegalStateException("Camera open returned no device.");
        }

        return device;
    }

    private CameraCaptureSession createCaptureSession(
        CameraDevice cameraDevice,
        Surface encoderSurface,
        Handler cameraHandler
    ) throws Exception {
        CountDownLatch latch = new CountDownLatch(1);
        AtomicReference<CameraCaptureSession> sessionRef = new AtomicReference<>();
        AtomicReference<Exception> errorRef = new AtomicReference<>();

        cameraDevice.createCaptureSession(Collections.singletonList(encoderSurface), new CameraCaptureSession.StateCallback() {
            @Override
            public void onConfigured(CameraCaptureSession session) {
                sessionRef.set(session);
                latch.countDown();
            }

            @Override
            public void onConfigureFailed(CameraCaptureSession session) {
                errorRef.compareAndSet(null, new IllegalStateException("Camera capture session configure failed."));
                latch.countDown();
            }
        }, cameraHandler);

        if (!latch.await(5, TimeUnit.SECONDS)) {
            throw new IllegalStateException("Timed out configuring camera capture session.");
        }

        Exception error = errorRef.get();
        if (error != null) {
            throw error;
        }

        CameraCaptureSession session = sessionRef.get();
        if (session == null) {
            throw new IllegalStateException("Capture session returned null.");
        }

        return session;
    }

    private void startRepeatingCapture(
        CameraDevice cameraDevice,
        CameraCaptureSession session,
        Surface encoderSurface,
        CameraCharacteristics characteristics,
        int targetFps
    ) throws CameraAccessException {
        CaptureRequest.Builder builder = cameraDevice.createCaptureRequest(CameraDevice.TEMPLATE_RECORD);
        builder.addTarget(encoderSurface);
        builder.set(CaptureRequest.CONTROL_MODE, CaptureRequest.CONTROL_MODE_AUTO);
        Range<Integer> fpsRange = chooseFpsRange(characteristics, targetFps);
        if (fpsRange != null) {
            builder.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, fpsRange);
        }
        session.setRepeatingRequest(builder.build(), null, null);
    }

    private static String findBackCameraId(CameraManager cameraManager) throws CameraAccessException {
        String[] cameraIds = cameraManager.getCameraIdList();
        String fallback = cameraIds.length > 0 ? cameraIds[0] : null;
        for (String cameraId : cameraIds) {
            CameraCharacteristics characteristics = cameraManager.getCameraCharacteristics(cameraId);
            Integer facing = characteristics.get(CameraCharacteristics.LENS_FACING);
            if (facing != null && facing == CameraCharacteristics.LENS_FACING_BACK) {
                return cameraId;
            }
        }

        if (fallback == null) {
            throw new IllegalStateException("No camera is available.");
        }

        return fallback;
    }

    private static Size chooseCaptureSize(CameraCharacteristics characteristics, int requestedWidth, int requestedHeight) {
        StreamConfigurationMap map = characteristics.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
        if (map == null) {
            return new Size(requestedWidth, requestedHeight);
        }

        Size[] sizes = map.getOutputSizes(MediaCodec.class);
        if (sizes == null || sizes.length == 0) {
            sizes = map.getOutputSizes(MediaRecorder.class);
        }
        if (sizes == null || sizes.length == 0) {
            return new Size(requestedWidth, requestedHeight);
        }

        for (Size size : sizes) {
            if (size.getWidth() == requestedWidth && size.getHeight() == requestedHeight) {
                return size;
            }
        }

        Arrays.sort(sizes, new Comparator<Size>() {
            @Override
            public int compare(Size left, Size right) {
                int leftScore = sizeScore(left, requestedWidth, requestedHeight);
                int rightScore = sizeScore(right, requestedWidth, requestedHeight);
                return leftScore - rightScore;
            }
        });
        return sizes[0];
    }

    private static int sizeScore(Size size, int requestedWidth, int requestedHeight) {
        int areaDelta = Math.abs(size.getWidth() * size.getHeight() - requestedWidth * requestedHeight);
        int ratioDelta = Math.abs(size.getWidth() * requestedHeight - requestedWidth * size.getHeight());
        return areaDelta + ratioDelta;
    }

    private static Range<Integer> chooseFpsRange(CameraCharacteristics characteristics, int targetFps) {
        Range<Integer>[] ranges = characteristics.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES);
        if (ranges == null || ranges.length == 0) {
            return null;
        }

        Range<Integer> best = null;
        int bestScore = Integer.MAX_VALUE;
        for (Range<Integer> range : ranges) {
            int upper = range.getUpper();
            int lower = range.getLower();
            int score = Math.abs(upper - targetFps) * 4 + Math.abs(lower - targetFps);
            if (upper >= targetFps && lower <= targetFps) {
                score -= 1000;
            }
            if (score < bestScore) {
                bestScore = score;
                best = range;
            }
        }

        return best;
    }

    private static int recommendedBitrate(int encodeWidth, int encodeHeight, int encodeFps) {
        if (encodeWidth >= 1920 || encodeHeight >= 1080) {
            return Math.max(DEFAULT_BITRATE, encodeFps >= 30 ? 8_000_000 : 5_000_000);
        }

        return DEFAULT_BITRATE;
    }

    private static int packetFlags(int bufferFlags) {
        int flags = 0;
        if ((bufferFlags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0) {
            flags |= FLAG_KEY_FRAME;
        }
        if ((bufferFlags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
            flags |= FLAG_CODEC_CONFIG;
        }
        if ((bufferFlags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) {
            flags |= FLAG_END_OF_STREAM;
        }
        return flags;
    }

    private static byte[] codecConfigFromFormat(MediaFormat format) {
        ByteBuffer csd0 = format.containsKey("csd-0") ? format.getByteBuffer("csd-0") : null;
        ByteBuffer csd1 = format.containsKey("csd-1") ? format.getByteBuffer("csd-1") : null;
        int length = remaining(csd0) + remaining(csd1);
        if (length <= 0) {
            return new byte[0];
        }

        byte[] config = new byte[length];
        int offset = copyBuffer(csd0, config, 0);
        copyBuffer(csd1, config, offset);
        return config;
    }

    private static int remaining(ByteBuffer buffer) {
        return buffer == null ? 0 : buffer.duplicate().remaining();
    }

    private static int copyBuffer(ByteBuffer source, byte[] destination, int offset) {
        if (source == null) {
            return offset;
        }

        ByteBuffer duplicate = source.duplicate();
        int length = duplicate.remaining();
        duplicate.get(destination, offset, length);
        return offset + length;
    }

    private void writePacket(
        OutputStream output,
        byte[] header,
        byte[] payload,
        int payloadLength,
        long sequence,
        long timestampUs,
        int flags,
        long runGeneration
    ) throws Exception {
        ByteBuffer buffer = ByteBuffer.wrap(header).order(ByteOrder.LITTLE_ENDIAN);
        buffer.put(MAGIC);
        buffer.putInt(VERSION);
        buffer.putInt(HEADER_SIZE);
        buffer.putInt(flags);
        buffer.putLong(sequence);
        buffer.putLong(timestampUs);
        buffer.putInt(payloadLength);
        buffer.putInt(0);

        try {
            output.write(header);
            output.write(payload, 0, payloadLength);
            output.flush();
        } catch (Exception ex) {
            throw new IllegalStateException("Camera packet write failed: " + exceptionSummary(ex) + "; " + generationDetails(runGeneration), ex);
        }
    }

    private boolean isCurrentGeneration(long runGeneration) {
        synchronized (lifecycleLock) {
            return generation == runGeneration;
        }
    }

    private boolean hasCameraPermission() {
        return context.checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED;
    }

    private void closeSocket() {
        Socket current;
        synchronized (lifecycleLock) {
            current = socket;
            socket = null;
        }
        closeQuietly(current);
    }

    private void closeSocket(Socket target) {
        if (target == null) {
            return;
        }
        synchronized (lifecycleLock) {
            if (socket == target) {
                socket = null;
            }
        }
        closeQuietly(target);
    }

    private static void closeQuietly(Socket target) {
        if (target == null) {
            return;
        }

        try {
            target.close();
        } catch (Exception ignored) {
        }
    }

    private static void closeCaptureSession(CameraCaptureSession session) {
        if (session == null) {
            return;
        }

        try {
            session.stopRepeating();
        } catch (Exception ignored) {
        }
        try {
            session.abortCaptures();
        } catch (Exception ignored) {
        }
        try {
            session.close();
        } catch (Exception ignored) {
        }
    }

    private static void closeCamera(CameraDevice camera) {
        if (camera == null) {
            return;
        }

        try {
            camera.close();
        } catch (Exception ignored) {
        }
    }

    private static void releaseSurface(Surface surface) {
        if (surface == null) {
            return;
        }

        try {
            surface.release();
        } catch (Exception ignored) {
        }
    }

    private static void releaseEncoder(MediaCodec encoder) {
        if (encoder == null) {
            return;
        }

        try {
            encoder.stop();
        } catch (Exception ignored) {
        }
        try {
            encoder.release();
        } catch (Exception ignored) {
        }
    }

    private static void stopCameraThread(HandlerThread thread) {
        if (thread == null) {
            return;
        }

        thread.quitSafely();
        try {
            thread.join(1000L);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
        }
    }

    private static boolean isNetworkException(Exception ex) {
        Throwable current = ex;
        while (current != null) {
            String name = current.getClass().getName();
            if (name.startsWith("java.net.") || name.startsWith("java.io.")) {
                return true;
            }
            current = current.getCause();
        }
        return false;
    }

    private String generationDetails(long runGeneration) {
        synchronized (lifecycleLock) {
            return "generation=" + runGeneration
                + " currentGeneration=" + generation
                + " expired=" + (generation != runGeneration);
        }
    }

    private static String exceptionSummary(Exception ex) {
        String message = ex.getMessage();
        String type = ex.getClass().getSimpleName();
        return message == null || message.trim().isEmpty()
            ? type
            : type + ": " + message;
    }

    private static void sleepQuietly(long delayMs) {
        try {
            Thread.sleep(delayMs);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
        }
    }

    private void emitState(String state, String message) {
        listener.onCameraCaptureState(state, message);
    }

    private void emitStats(long packetsSent, long bytesSent, long keyFrames, long codecConfigPackets) {
        listener.onCameraCaptureStats(packetsSent, bytesSent, keyFrames, codecConfigPackets);
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
