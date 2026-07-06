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
import android.os.Build;
import android.os.Bundle;
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
import java.util.Locale;
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
    private static final byte[] ANNEX_B_START_CODE = new byte[] { 0, 0, 0, 1 };
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
    private String facing = "back";

    public CameraCaptureClient(Context context, Listener listener) {
        this.context = context.getApplicationContext();
        this.listener = listener;
    }

    public void start(int nextPort, int nextWidth, int nextHeight, int nextFps, String nextCodec, String nextFacing) {
        synchronized (lifecycleLock) {
            int normalizedWidth = nextWidth > 0 ? nextWidth : 1280;
            int normalizedHeight = nextHeight > 0 ? nextHeight : 720;
            int normalizedFps = nextFps > 0 ? nextFps : 30;
            String normalizedCodec = nextCodec == null || nextCodec.trim().isEmpty() ? DEFAULT_CODEC : nextCodec.trim();
            String normalizedFacing = normalizeFacing(nextFacing);
            if (running
                && port == nextPort
                && width == normalizedWidth
                && height == normalizedHeight
                && fps == normalizedFps
                && codec.equals(normalizedCodec)
                && facing.equals(normalizedFacing)) {
                return;
            }

            stopLocked();
            port = nextPort;
            width = normalizedWidth;
            height = normalizedHeight;
            fps = normalizedFps;
            codec = normalizedCodec;
            facing = normalizedFacing;
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

                String cameraId = findCameraId(cameraManager, facing);
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

                emitState("capturing", "Camera capture started (" + facing + ").");
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
        long lastSyncFrameRequestMs = 0L;
        byte[] codecConfig = new byte[0];

        while (running && isCurrentGeneration(runGeneration)) {
            long loopNowMs = System.currentTimeMillis();
            if (loopNowMs - lastSyncFrameRequestMs >= 1000L) {
                lastSyncFrameRequestMs = loopNowMs;
                requestSyncFrame(encoder);
            }

            int outputIndex = encoder.dequeueOutputBuffer(bufferInfo, 10000L);
            if (outputIndex == MediaCodec.INFO_TRY_AGAIN_LATER) {
                continue;
            }

            if (outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                byte[] config = codecConfigFromFormat(encoder.getOutputFormat());
                if (config.length > 0) {
                    codecConfig = config;
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
                    byte[] packetPayload = normalizeH264AccessUnit(payload, bufferInfo.size);
                    if ((flags & FLAG_CODEC_CONFIG) != 0) {
                        codecConfig = packetPayload;
                    } else if ((flags & FLAG_KEY_FRAME) != 0 && codecConfig.length > 0 && !containsParameterSet(packetPayload)) {
                        packetPayload = prependParameterSets(codecConfig, packetPayload);
                    }

                    sequence += 1L;
                    writePacket(output, header, packetPayload, packetPayload.length, sequence, bufferInfo.presentationTimeUs, flags, runGeneration);
                    packetsSent += 1L;
                    bytesSent += packetPayload.length;
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

    private static void requestSyncFrame(MediaCodec encoder) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.KITKAT) {
            return;
        }

        try {
            Bundle parameters = new Bundle();
            parameters.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0);
            encoder.setParameters(parameters);
        } catch (Exception ignored) {
        }
    }

    private MediaCodec createEncoder(String mime, int encodeWidth, int encodeHeight, int encodeFps) throws Exception {
        if (!DEFAULT_CODEC.equals(mime)) {
            throw new IllegalArgumentException("Unsupported camera codec: " + mime);
        }

        MediaCodec encoder = MediaCodec.createEncoderByType(mime);
        MediaFormat format = createEncoderFormat(mime, encodeWidth, encodeHeight, encodeFps, encoder.getCodecInfo(), true);
        try {
            encoder.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            return encoder;
        } catch (Exception ex) {
            releaseEncoder(encoder);
            encoder = MediaCodec.createEncoderByType(mime);
            format = createEncoderFormat(mime, encodeWidth, encodeHeight, encodeFps, encoder.getCodecInfo(), false);
            encoder.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            return encoder;
        }
    }

    private MediaFormat createEncoderFormat(
        String mime,
        int encodeWidth,
        int encodeHeight,
        int encodeFps,
        MediaCodecInfo codecInfo,
        boolean lowLatency
    ) {
        MediaFormat format = MediaFormat.createVideoFormat(mime, encodeWidth, encodeHeight);
        format.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
        format.setInteger(MediaFormat.KEY_BIT_RATE, recommendedBitrate(encodeWidth, encodeHeight, encodeFps));
        format.setInteger(MediaFormat.KEY_FRAME_RATE, encodeFps);
        format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);
        configureAvcEncoderFormat(format, codecInfo, mime, encodeFps, lowLatency);
        return format;
    }

    private static void configureAvcEncoderFormat(
        MediaFormat format,
        MediaCodecInfo codecInfo,
        String mime,
        int encodeFps,
        boolean lowLatency
    ) {
        if (!DEFAULT_CODEC.equals(mime) || codecInfo == null) {
            return;
        }

        int profile = selectAvcProfile(codecInfo, mime);
        if (profile != 0) {
            format.setInteger(MediaFormat.KEY_PROFILE, profile);
        }

        int level = selectAvcLevel(codecInfo, mime, profile);
        if (level != 0) {
            format.setInteger(MediaFormat.KEY_LEVEL, level);
        }

        try {
            format.setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR);
        } catch (Exception ignored) {
        }

        if (lowLatency) {
            configureAvcLowLatencyEncoderFormat(format, encodeFps);
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            try {
                format.setInteger(MediaFormat.KEY_COLOR_RANGE, MediaFormat.COLOR_RANGE_LIMITED);
                format.setInteger(MediaFormat.KEY_COLOR_STANDARD, MediaFormat.COLOR_STANDARD_BT709);
                format.setInteger(MediaFormat.KEY_COLOR_TRANSFER, MediaFormat.COLOR_TRANSFER_SDR_VIDEO);
            } catch (Exception ignored) {
            }
        }
    }

    private static void configureAvcLowLatencyEncoderFormat(MediaFormat format, int encodeFps) {
        trySetInteger(format, MediaFormat.KEY_PRIORITY, 0);
        trySetFloat(format, MediaFormat.KEY_OPERATING_RATE, (float) Math.max(1, encodeFps));
        trySetInteger(format, MediaFormat.KEY_LATENCY, 0);
        trySetInteger(format, MediaFormat.KEY_PREPEND_HEADER_TO_SYNC_FRAMES, 1);

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            trySetInteger(format, MediaFormat.KEY_MAX_B_FRAMES, 0);
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            trySetInteger(format, MediaFormat.KEY_LOW_LATENCY, 1);
        }
    }

    private static void trySetInteger(MediaFormat format, String key, int value) {
        try {
            format.setInteger(key, value);
        } catch (Exception ignored) {
        }
    }

    private static void trySetFloat(MediaFormat format, String key, float value) {
        try {
            format.setFloat(key, value);
        } catch (Exception ignored) {
        }
    }

    private static int selectAvcProfile(MediaCodecInfo codecInfo, String mime) {
        MediaCodecInfo.CodecCapabilities capabilities;
        try {
            capabilities = codecInfo.getCapabilitiesForType(mime);
        } catch (Exception ex) {
            return 0;
        }

        boolean supportsBaseline = false;
        for (MediaCodecInfo.CodecProfileLevel profileLevel : capabilities.profileLevels) {
            if (profileLevel.profile == MediaCodecInfo.CodecProfileLevel.AVCProfileConstrainedBaseline) {
                return MediaCodecInfo.CodecProfileLevel.AVCProfileConstrainedBaseline;
            }

            if (profileLevel.profile == MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline) {
                supportsBaseline = true;
            }
        }

        return supportsBaseline ? MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline : 0;
    }

    private static int selectAvcLevel(MediaCodecInfo codecInfo, String mime, int profile) {
        MediaCodecInfo.CodecCapabilities capabilities;
        try {
            capabilities = codecInfo.getCapabilitiesForType(mime);
        } catch (Exception ex) {
            return 0;
        }

        int preferredLevel = MediaCodecInfo.CodecProfileLevel.AVCLevel31;
        int fallbackLevel = 0;
        for (MediaCodecInfo.CodecProfileLevel profileLevel : capabilities.profileLevels) {
            if (profile != 0 && profileLevel.profile != profile) {
                continue;
            }

            if (profileLevel.level == preferredLevel) {
                return preferredLevel;
            }

            if (profileLevel.level > fallbackLevel) {
                fallbackLevel = profileLevel.level;
            }
        }

        return fallbackLevel >= preferredLevel ? preferredLevel : fallbackLevel;
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
        configureLowLatencyCaptureRequest(builder, characteristics);
        Range<Integer> fpsRange = chooseFpsRange(characteristics, targetFps);
        if (fpsRange != null) {
            builder.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, fpsRange);
        }
        session.setRepeatingRequest(builder.build(), null, null);
    }

    private static void configureLowLatencyCaptureRequest(
        CaptureRequest.Builder builder,
        CameraCharacteristics characteristics
    ) {
        setIfSupported(
            builder,
            CaptureRequest.CONTROL_AF_MODE,
            characteristics.get(CameraCharacteristics.CONTROL_AF_AVAILABLE_MODES),
            CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO);
        setIfSupported(
            builder,
            CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE,
            characteristics.get(CameraCharacteristics.CONTROL_AVAILABLE_VIDEO_STABILIZATION_MODES),
            CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE_OFF);
        setIfSupported(
            builder,
            CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE,
            characteristics.get(CameraCharacteristics.LENS_INFO_AVAILABLE_OPTICAL_STABILIZATION),
            CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE_OFF);
        setPreferredIfSupported(
            builder,
            CaptureRequest.NOISE_REDUCTION_MODE,
            characteristics.get(CameraCharacteristics.NOISE_REDUCTION_AVAILABLE_NOISE_REDUCTION_MODES),
            CaptureRequest.NOISE_REDUCTION_MODE_FAST,
            CaptureRequest.NOISE_REDUCTION_MODE_OFF);
        setPreferredIfSupported(
            builder,
            CaptureRequest.EDGE_MODE,
            characteristics.get(CameraCharacteristics.EDGE_AVAILABLE_EDGE_MODES),
            CaptureRequest.EDGE_MODE_FAST,
            CaptureRequest.EDGE_MODE_OFF);
        setPreferredIfSupported(
            builder,
            CaptureRequest.HOT_PIXEL_MODE,
            characteristics.get(CameraCharacteristics.HOT_PIXEL_AVAILABLE_HOT_PIXEL_MODES),
            CaptureRequest.HOT_PIXEL_MODE_FAST,
            CaptureRequest.HOT_PIXEL_MODE_OFF);
        setIfSupported(
            builder,
            CaptureRequest.TONEMAP_MODE,
            characteristics.get(CameraCharacteristics.TONEMAP_AVAILABLE_TONE_MAP_MODES),
            CaptureRequest.TONEMAP_MODE_FAST);

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            setPreferredIfSupported(
                builder,
                CaptureRequest.DISTORTION_CORRECTION_MODE,
                characteristics.get(CameraCharacteristics.DISTORTION_CORRECTION_AVAILABLE_MODES),
                CaptureRequest.DISTORTION_CORRECTION_MODE_OFF,
                CaptureRequest.DISTORTION_CORRECTION_MODE_FAST);
        }
    }

    private static void setPreferredIfSupported(
        CaptureRequest.Builder builder,
        CaptureRequest.Key<Integer> key,
        int[] supportedValues,
        int preferredValue,
        int fallbackValue
    ) {
        if (contains(supportedValues, preferredValue)) {
            setCaptureRequestValue(builder, key, preferredValue);
        } else if (contains(supportedValues, fallbackValue)) {
            setCaptureRequestValue(builder, key, fallbackValue);
        }
    }

    private static void setIfSupported(
        CaptureRequest.Builder builder,
        CaptureRequest.Key<Integer> key,
        int[] supportedValues,
        int value
    ) {
        if (contains(supportedValues, value)) {
            setCaptureRequestValue(builder, key, value);
        }
    }

    private static void setCaptureRequestValue(
        CaptureRequest.Builder builder,
        CaptureRequest.Key<Integer> key,
        int value
    ) {
        try {
            builder.set(key, value);
        } catch (Exception ignored) {
        }
    }

    private static boolean contains(int[] values, int target) {
        if (values == null) {
            return false;
        }

        for (int value : values) {
            if (value == target) {
                return true;
            }
        }

        return false;
    }

    private static String findCameraId(CameraManager cameraManager, String facing) throws CameraAccessException {
        String[] cameraIds = cameraManager.getCameraIdList();
        String fallback = cameraIds.length > 0 ? cameraIds[0] : null;
        int requestedFacing = "front".equals(facing)
            ? CameraCharacteristics.LENS_FACING_FRONT
            : CameraCharacteristics.LENS_FACING_BACK;
        for (String cameraId : cameraIds) {
            CameraCharacteristics characteristics = cameraManager.getCameraCharacteristics(cameraId);
            Integer cameraFacing = characteristics.get(CameraCharacteristics.LENS_FACING);
            if (cameraFacing != null && cameraFacing == requestedFacing) {
                return cameraId;
            }
        }

        if (fallback == null) {
            throw new IllegalStateException("No camera is available.");
        }

        return fallback;
    }

    private static String normalizeFacing(String value) {
        if (value == null) {
            return "back";
        }

        String normalized = value.trim().toLowerCase(Locale.ROOT);
        return "front".equals(normalized) ? "front" : "back";
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
        byte[] sps = normalizeH264AccessUnit(csd0);
        byte[] pps = normalizeH264AccessUnit(csd1);
        if (sps.length == 0 && pps.length == 0) {
            return new byte[0];
        }

        byte[] config = new byte[sps.length + pps.length];
        System.arraycopy(sps, 0, config, 0, sps.length);
        System.arraycopy(pps, 0, config, sps.length, pps.length);
        return config;
    }

    private static byte[] normalizeH264AccessUnit(ByteBuffer buffer) {
        if (buffer == null) {
            return new byte[0];
        }

        ByteBuffer duplicate = buffer.duplicate();
        int length = duplicate.remaining();
        if (length <= 0) {
            return new byte[0];
        }

        byte[] bytes = new byte[length];
        duplicate.get(bytes);
        return normalizeH264AccessUnit(bytes, bytes.length);
    }

    private static byte[] normalizeH264AccessUnit(byte[] source, int length) {
        if (length <= 0) {
            return new byte[0];
        }

        if (startsWithStartCode(source, length)) {
            byte[] copy = new byte[length];
            System.arraycopy(source, 0, copy, 0, length);
            return sanitizeH264SpsInAnnexB(copy);
        }

        byte[] avcc = tryConvertAvccToAnnexB(source, length);
        if (avcc != null) {
            return sanitizeH264SpsInAnnexB(avcc);
        }

        byte[] annexB = new byte[ANNEX_B_START_CODE.length + length];
        System.arraycopy(ANNEX_B_START_CODE, 0, annexB, 0, ANNEX_B_START_CODE.length);
        System.arraycopy(source, 0, annexB, ANNEX_B_START_CODE.length, length);
        return sanitizeH264SpsInAnnexB(annexB);
    }

    private static byte[] tryConvertAvccToAnnexB(byte[] source, int length) {
        int offset = 0;
        int totalPayloadLength = 0;
        int nalCount = 0;
        while (offset + 4 <= length) {
            int nalLength = ((source[offset] & 0xff) << 24)
                | ((source[offset + 1] & 0xff) << 16)
                | ((source[offset + 2] & 0xff) << 8)
                | (source[offset + 3] & 0xff);
            offset += 4;
            if (nalLength <= 0 || offset + nalLength > length) {
                return null;
            }

            totalPayloadLength += nalLength;
            nalCount += 1;
            offset += nalLength;
        }

        if (offset != length || nalCount == 0) {
            return null;
        }

        byte[] annexB = new byte[nalCount * ANNEX_B_START_CODE.length + totalPayloadLength];
        offset = 0;
        int outOffset = 0;
        while (offset + 4 <= length) {
            int nalLength = ((source[offset] & 0xff) << 24)
                | ((source[offset + 1] & 0xff) << 16)
                | ((source[offset + 2] & 0xff) << 8)
                | (source[offset + 3] & 0xff);
            offset += 4;
            System.arraycopy(ANNEX_B_START_CODE, 0, annexB, outOffset, ANNEX_B_START_CODE.length);
            outOffset += ANNEX_B_START_CODE.length;
            System.arraycopy(source, offset, annexB, outOffset, nalLength);
            outOffset += nalLength;
            offset += nalLength;
        }

        return annexB;
    }

    private static boolean startsWithStartCode(byte[] data, int length) {
        if (length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1) {
            return true;
        }

        return length >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1;
    }

    private static boolean containsParameterSet(byte[] annexB) {
        for (int offset = nextStartCode(annexB, 0); offset >= 0; offset = nextStartCode(annexB, offset + 3)) {
            int nalOffset = nalPayloadOffset(annexB, offset);
            if (nalOffset >= annexB.length) {
                continue;
            }

            int nalType = annexB[nalOffset] & 0x1f;
            if (nalType == 7 || nalType == 8) {
                return true;
            }
        }

        return false;
    }

    private static byte[] prependParameterSets(byte[] codecConfig, byte[] accessUnit) {
        byte[] output = new byte[codecConfig.length + accessUnit.length];
        System.arraycopy(codecConfig, 0, output, 0, codecConfig.length);
        System.arraycopy(accessUnit, 0, output, codecConfig.length, accessUnit.length);
        return output;
    }

    private static byte[] sanitizeH264SpsInAnnexB(byte[] annexB) {
        int firstStartCode = nextStartCode(annexB, 0);
        if (firstStartCode < 0) {
            return annexB;
        }

        ByteArrayBuilder output = new ByteArrayBuilder(annexB.length);
        int startCode = firstStartCode;
        int copiedUntil = 0;
        while (startCode >= 0) {
            int nalOffset = nalPayloadOffset(annexB, startCode);
            int nextStartCode = nextStartCode(annexB, nalOffset + 1);
            int nalEnd = nextStartCode >= 0 ? nextStartCode : annexB.length;
            output.append(annexB, copiedUntil, nalOffset - copiedUntil);

            int nalLength = nalEnd - nalOffset;
            if (nalLength > 0 && (annexB[nalOffset] & 0x1f) == 7) {
                byte[] sanitized = stripSpsVui(annexB, nalOffset, nalLength);
                output.append(sanitized, 0, sanitized.length);
            } else {
                output.append(annexB, nalOffset, nalLength);
            }

            copiedUntil = nalEnd;
            startCode = nextStartCode;
        }

        if (copiedUntil < annexB.length) {
            output.append(annexB, copiedUntil, annexB.length - copiedUntil);
        }

        return output.toByteArray();
    }

    private static byte[] stripSpsVui(byte[] data, int offset, int length) {
        if (length <= 1) {
            return copyOfRange(data, offset, length);
        }

        try {
            byte[] rbsp = ebspToRbsp(data, offset + 1, length - 1);
            BitReader reader = new BitReader(rbsp);
            int profileIdc = reader.readBits(8);
            reader.skipBits(8);
            reader.skipBits(8);
            reader.readUnsignedExpGolomb();

            if (isExtendedAvcProfile(profileIdc)) {
                int chromaFormatIdc = reader.readUnsignedExpGolomb();
                if (chromaFormatIdc == 3) {
                    reader.skipBits(1);
                }

                reader.readUnsignedExpGolomb();
                reader.readUnsignedExpGolomb();
                reader.skipBits(1);
                if (reader.readBit()) {
                    int scalingListCount = chromaFormatIdc != 3 ? 8 : 12;
                    for (int index = 0; index < scalingListCount; index++) {
                        if (reader.readBit()) {
                            skipScalingList(reader, index < 6 ? 16 : 64);
                        }
                    }
                }
            }

            reader.readUnsignedExpGolomb();
            int picOrderCntType = reader.readUnsignedExpGolomb();
            if (picOrderCntType == 0) {
                reader.readUnsignedExpGolomb();
            } else if (picOrderCntType == 1) {
                reader.skipBits(1);
                reader.readSignedExpGolomb();
                reader.readSignedExpGolomb();
                int cycleCount = reader.readUnsignedExpGolomb();
                for (int index = 0; index < cycleCount; index++) {
                    reader.readSignedExpGolomb();
                }
            }

            reader.readUnsignedExpGolomb();
            reader.skipBits(1);
            reader.readUnsignedExpGolomb();
            reader.readUnsignedExpGolomb();
            boolean frameMbsOnly = reader.readBit();
            if (!frameMbsOnly) {
                reader.skipBits(1);
            }

            reader.skipBits(1);
            if (reader.readBit()) {
                reader.readUnsignedExpGolomb();
                reader.readUnsignedExpGolomb();
                reader.readUnsignedExpGolomb();
                reader.readUnsignedExpGolomb();
            }

            int vuiFlagBitPosition = reader.bitPosition();
            reader.skipBits(1);

            BitWriter writer = new BitWriter(rbsp.length);
            writer.writeBitsFrom(rbsp, vuiFlagBitPosition);
            writer.writeBit(false);
            writer.writeRbspTrailingBits();

            byte[] cleanRbsp = writer.toByteArray();
            byte[] cleanEbsp = rbspToEbsp(cleanRbsp);
            byte[] output = new byte[1 + cleanEbsp.length];
            output[0] = data[offset];
            System.arraycopy(cleanEbsp, 0, output, 1, cleanEbsp.length);
            return output;
        } catch (RuntimeException ex) {
            return copyOfRange(data, offset, length);
        }
    }

    private static void skipScalingList(BitReader reader, int size) {
        int lastScale = 8;
        int nextScale = 8;
        for (int index = 0; index < size; index++) {
            if (nextScale != 0) {
                int deltaScale = reader.readSignedExpGolomb();
                nextScale = (lastScale + deltaScale + 256) % 256;
            }

            lastScale = nextScale == 0 ? lastScale : nextScale;
        }
    }

    private static boolean isExtendedAvcProfile(int profileIdc) {
        return profileIdc == 100
            || profileIdc == 110
            || profileIdc == 122
            || profileIdc == 244
            || profileIdc == 44
            || profileIdc == 83
            || profileIdc == 86
            || profileIdc == 118
            || profileIdc == 128
            || profileIdc == 138
            || profileIdc == 139
            || profileIdc == 134
            || profileIdc == 135;
    }

    private static byte[] ebspToRbsp(byte[] data, int offset, int length) {
        ByteArrayBuilder output = new ByteArrayBuilder(length);
        int zeroCount = 0;
        for (int index = 0; index < length; index++) {
            byte value = data[offset + index];
            if (zeroCount >= 2 && value == 0x03) {
                zeroCount = 0;
                continue;
            }

            output.append(value);
            if (value == 0) {
                zeroCount += 1;
            } else {
                zeroCount = 0;
            }
        }

        return output.toByteArray();
    }

    private static byte[] rbspToEbsp(byte[] rbsp) {
        ByteArrayBuilder output = new ByteArrayBuilder(rbsp.length + 8);
        int zeroCount = 0;
        for (byte value : rbsp) {
            int unsigned = value & 0xff;
            if (zeroCount >= 2 && unsigned <= 0x03) {
                output.append((byte) 0x03);
                zeroCount = 0;
            }

            output.append(value);
            if (value == 0) {
                zeroCount += 1;
            } else {
                zeroCount = 0;
            }
        }

        return output.toByteArray();
    }

    private static byte[] copyOfRange(byte[] data, int offset, int length) {
        byte[] output = new byte[length];
        System.arraycopy(data, offset, output, 0, length);
        return output;
    }

    private static final class BitReader {
        private final byte[] data;
        private int bitPosition;

        BitReader(byte[] data) {
            this.data = data;
        }

        int bitPosition() {
            return bitPosition;
        }

        boolean readBit() {
            return readBits(1) != 0;
        }

        int readBits(int count) {
            if (count < 0 || count > 31 || bitPosition + count > data.length * 8) {
                throw new IllegalStateException("Invalid H.264 bit read.");
            }

            int value = 0;
            for (int index = 0; index < count; index++) {
                int byteIndex = bitPosition / 8;
                int bitIndex = 7 - (bitPosition % 8);
                value = (value << 1) | ((data[byteIndex] >> bitIndex) & 0x01);
                bitPosition += 1;
            }

            return value;
        }

        void skipBits(int count) {
            readBits(count);
        }

        int readUnsignedExpGolomb() {
            int leadingZeroBits = 0;
            while (!readBit()) {
                leadingZeroBits += 1;
                if (leadingZeroBits > 31) {
                    throw new IllegalStateException("Invalid H.264 Exp-Golomb code.");
                }
            }

            return leadingZeroBits == 0
                ? 0
                : ((1 << leadingZeroBits) - 1) + readBits(leadingZeroBits);
        }

        int readSignedExpGolomb() {
            int codeNum = readUnsignedExpGolomb();
            int value = (codeNum + 1) / 2;
            return (codeNum & 1) == 0 ? -value : value;
        }
    }

    private static final class BitWriter {
        private final ByteArrayBuilder output;
        private int currentByte;
        private int bitCount;

        BitWriter(int capacity) {
            output = new ByteArrayBuilder(capacity);
        }

        void writeBitsFrom(byte[] source, int bitLength) {
            for (int bit = 0; bit < bitLength; bit++) {
                int byteIndex = bit / 8;
                int bitIndex = 7 - (bit % 8);
                writeBit(((source[byteIndex] >> bitIndex) & 0x01) != 0);
            }
        }

        void writeBit(boolean value) {
            currentByte = (currentByte << 1) | (value ? 1 : 0);
            bitCount += 1;
            if (bitCount == 8) {
                output.append((byte) currentByte);
                currentByte = 0;
                bitCount = 0;
            }
        }

        void writeRbspTrailingBits() {
            writeBit(true);
            while (bitCount != 0) {
                writeBit(false);
            }
        }

        byte[] toByteArray() {
            if (bitCount != 0) {
                currentByte <<= 8 - bitCount;
                output.append((byte) currentByte);
                currentByte = 0;
                bitCount = 0;
            }

            return output.toByteArray();
        }
    }

    private static final class ByteArrayBuilder {
        private byte[] data;
        private int length;

        ByteArrayBuilder(int capacity) {
            data = new byte[Math.max(16, capacity)];
        }

        void append(byte value) {
            ensureCapacity(length + 1);
            data[length] = value;
            length += 1;
        }

        void append(byte[] source, int offset, int count) {
            if (count <= 0) {
                return;
            }

            ensureCapacity(length + count);
            System.arraycopy(source, offset, data, length, count);
            length += count;
        }

        byte[] toByteArray() {
            byte[] output = new byte[length];
            System.arraycopy(data, 0, output, 0, length);
            return output;
        }

        private void ensureCapacity(int required) {
            if (required <= data.length) {
                return;
            }

            int next = data.length;
            while (next < required) {
                next *= 2;
            }

            byte[] replacement = new byte[next];
            System.arraycopy(data, 0, replacement, 0, length);
            data = replacement;
        }
    }

    private static int nextStartCode(byte[] data, int offset) {
        for (int index = Math.max(0, offset); index + 3 <= data.length; index++) {
            if (data[index] == 0 && data[index + 1] == 0) {
                if (data[index + 2] == 1) {
                    return index;
                }

                if (index + 4 <= data.length && data[index + 2] == 0 && data[index + 3] == 1) {
                    return index;
                }
            }
        }

        return -1;
    }

    private static int nalPayloadOffset(byte[] data, int startCodeOffset) {
        return startCodeOffset + (startCodeOffset + 3 < data.length && data[startCodeOffset + 2] == 1 ? 3 : 4);
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
