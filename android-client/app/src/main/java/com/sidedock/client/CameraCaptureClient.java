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
import android.media.MediaCodecList;
import android.media.MediaFormat;
import android.media.MediaRecorder;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Range;
import android.util.Size;
import android.view.Surface;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

public final class CameraCaptureClient {
    public interface Listener {
        void onCameraCaptureState(String state, String message);
        void onCameraCaptureConfigApplied(int port, int width, int height, int fps, String codec, String facing);
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
    private static final String HEVC_CODEC = "video/hevc";
    private static final String[] CAMERA_ENCODER_CODECS = new String[] { DEFAULT_CODEC, HEVC_CODEC };
    private static final int[] PREFERRED_TARGET_FPS = new int[] { 30, 60, 120, 24, 15 };

    private final Context context;
    private final Listener listener;
    private final Object lifecycleLock = new Object();

    private ExecutorService executor;
    private Socket socket;
    private CountDownLatch stopLatch;
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
        StopToken previousStop;
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

            emitState("restarting", "Restarting camera capture with new config.");
            previousStop = stopLocked();
            port = nextPort;
            width = normalizedWidth;
            height = normalizedHeight;
            fps = normalizedFps;
            codec = normalizedCodec;
            facing = normalizedFacing;
        }

        waitForStop(previousStop);

        synchronized (lifecycleLock) {
            running = true;
            final long runGeneration = ++generation;
            final CountDownLatch runStopLatch = new CountDownLatch(1);
            stopLatch = runStopLatch;
            executor = Executors.newSingleThreadExecutor(new NamedThreadFactory("SideDock-Camera"));
            executor.execute(new Runnable() {
                @Override
                public void run() {
                    cameraLoop(runGeneration, runStopLatch);
                }
            });
        }
    }

    public void stop() {
        StopToken previousStop;
        synchronized (lifecycleLock) {
            previousStop = stopLocked();
        }
        waitForStop(previousStop);
    }

    public boolean isRunning() {
        return running;
    }

    public JSONObject buildCapabilitiesSnapshot(
        String reason,
        boolean enabled,
        String state,
        int currentPort,
        int currentWidth,
        int currentHeight,
        int currentFps,
        String currentCodec,
        String currentFacing
    ) {
        JSONObject root = new JSONObject();
        try {
            root.put("schema", 1);
            root.put("reason", reason == null ? "" : reason);
            root.put("queriedAtMs", System.currentTimeMillis());
            root.put("manufacturer", Build.MANUFACTURER == null ? "" : Build.MANUFACTURER);
            root.put("model", Build.MODEL == null ? "" : Build.MODEL);
            root.put("androidSdk", Build.VERSION.SDK_INT);
            root.put("streamCodecs", stringArrayJson(availableEncoderCodecsForStream()));
            root.put("current", currentConfigJson(
                enabled,
                state,
                currentPort,
                currentWidth,
                currentHeight,
                currentFps,
                currentCodec,
                currentFacing
            ));

            JSONArray lenses = new JSONArray();
            CameraManager cameraManager = (CameraManager) context.getSystemService(Context.CAMERA_SERVICE);
            if (cameraManager == null) {
                root.put("error", "CameraManager is unavailable.");
                root.put("lenses", lenses);
                return root;
            }

            String[] cameraIds = cameraManager.getCameraIdList();
            for (String cameraId : cameraIds) {
                try {
                    CameraCharacteristics characteristics = cameraManager.getCameraCharacteristics(cameraId);
                    lenses.put(cameraCapabilityJson(cameraId, characteristics, currentWidth, currentHeight, currentFps));
                } catch (Exception ex) {
                    JSONObject lensError = new JSONObject();
                    lensError.put("cameraId", cameraId == null ? "" : cameraId);
                    lensError.put("error", exceptionSummary(ex));
                    lenses.put(lensError);
                }
            }

            root.put("lenses", lenses);
            if (lenses.length() == 0) {
                root.put("error", "No camera is available.");
            }
        } catch (Exception ex) {
            try {
                root.put("error", exceptionSummary(ex));
            } catch (Exception ignored) {
            }
        }

        return root;
    }

    private StopToken stopLocked() {
        ExecutorService previousExecutor = executor;
        CountDownLatch previousStopLatch = stopLatch;
        running = false;
        generation += 1;
        closeSocket();
        if (executor != null) {
            executor.shutdownNow();
            executor = null;
        }
        stopLatch = null;
        return new StopToken(previousExecutor, previousStopLatch);
    }

    private void waitForStop(StopToken stopToken) {
        if (stopToken.executor == null) {
            return;
        }

        try {
            if (stopToken.stopLatch != null) {
                stopToken.stopLatch.await(2500L, TimeUnit.MILLISECONDS);
            }
            stopToken.executor.awaitTermination(500L, TimeUnit.MILLISECONDS);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
        }
    }

    private void cameraLoop(long runGeneration, CountDownLatch runStopLatch) {
        try {
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
                    String effectiveCodec = selectEffectiveCameraCodec(codec);
                    Size captureSize = chooseCaptureSize(characteristics, width, height, effectiveCodec);
                    int effectiveFps = chooseEffectiveFps(characteristics, captureSize, fps, effectiveCodec);
                    Range<Integer> fpsRange = chooseFpsRange(characteristics, effectiveFps);
                    String effectiveFacing = cameraFacingName(characteristics, facing);

                    encoder = createEncoder(effectiveCodec, captureSize.getWidth(), captureSize.getHeight(), effectiveFps);
                    encoderSurface = encoder.createInputSurface();
                    encoder.start();

                    cameraThread = new HandlerThread("SideDock-Camera2");
                    cameraThread.start();
                    Handler cameraHandler = new Handler(cameraThread.getLooper());

                    cameraDevice = openCamera(cameraManager, cameraId, cameraHandler);
                    captureSession = createCaptureSession(cameraDevice, encoderSurface, cameraHandler);
                    startRepeatingCapture(cameraDevice, captureSession, encoderSurface, characteristics, fpsRange);

                    emitConfigApplied(port, captureSize.getWidth(), captureSize.getHeight(), effectiveFps, effectiveCodec, effectiveFacing);
                    emitState("capturing", "Camera capture started (" + effectiveFacing + ").");
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
        } finally {
            runStopLatch.countDown();
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
        Range<Integer> fpsRange
    ) throws CameraAccessException {
        CaptureRequest.Builder builder = cameraDevice.createCaptureRequest(CameraDevice.TEMPLATE_RECORD);
        builder.addTarget(encoderSurface);
        builder.set(CaptureRequest.CONTROL_MODE, CaptureRequest.CONTROL_MODE_AUTO);
        configureLowLatencyCaptureRequest(builder, characteristics);
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

    private static JSONObject currentConfigJson(
        boolean enabled,
        String state,
        int currentPort,
        int currentWidth,
        int currentHeight,
        int currentFps,
        String currentCodec,
        String currentFacing
    ) throws Exception {
        JSONObject current = new JSONObject();
        current.put("enabled", enabled);
        current.put("state", state == null ? "" : state);
        current.put("port", currentPort);
        current.put("width", currentWidth);
        current.put("height", currentHeight);
        current.put("fps", currentFps);
        current.put("codec", normalizeCodec(currentCodec));
        current.put("facing", normalizeFacing(currentFacing));
        return current;
    }

    private static JSONObject cameraCapabilityJson(
        String cameraId,
        CameraCharacteristics characteristics,
        int fallbackWidth,
        int fallbackHeight,
        int fallbackFps
    ) throws Exception {
        String facingName = cameraFacingName(characteristics, "back");
        Range<Integer>[] ranges = characteristics.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES);
        List<Integer> targetFps = targetFpsFromRanges(ranges);
        List<Size> sizes = supportedOutputSizes(characteristics, fallbackWidth, fallbackHeight);
        JSONArray sizesJson = new JSONArray();
        Set<String> lensCodecs = new HashSet<>();

        for (Size size : sizes) {
            List<String> codecs = supportedCodecsForSize(size, targetFps);
            List<Integer> fpsValues = supportedFpsForSize(size, targetFps, codecs);
            if (codecs.isEmpty() || fpsValues.isEmpty()) {
                continue;
            }

            for (String codec : codecs) {
                lensCodecs.add(codec);
            }

            JSONObject sizeJson = new JSONObject();
            sizeJson.put("width", size.getWidth());
            sizeJson.put("height", size.getHeight());
            sizeJson.put("fps", intArrayJson(fpsValues));
            sizeJson.put("codecs", stringArrayJson(codecs));
            sizesJson.put(sizeJson);
        }

        if (sizesJson.length() == 0) {
            JSONObject fallbackSize = new JSONObject();
            fallbackSize.put("width", Math.max(1, fallbackWidth));
            fallbackSize.put("height", Math.max(1, fallbackHeight));
            fallbackSize.put("fps", intArrayJson(Collections.singletonList(Math.max(1, fallbackFps))));
            fallbackSize.put("codecs", stringArrayJson(availableEncoderCodecsForStream()));
            sizesJson.put(fallbackSize);
            lensCodecs.add(DEFAULT_CODEC);
        }

        JSONObject lens = new JSONObject();
        lens.put("facing", facingName);
        lens.put("cameraId", cameraId == null ? "" : cameraId);
        lens.put("hardwareLevel", hardwareLevelName(characteristics));
        lens.put("fpsRanges", fpsRangesJson(ranges));
        lens.put("targetFps", intArrayJson(targetFps));
        lens.put("codecs", stringArrayJson(sortedStrings(lensCodecs)));
        lens.put("sizes", sizesJson);
        return lens;
    }

    private static List<Size> supportedOutputSizes(
        CameraCharacteristics characteristics,
        int fallbackWidth,
        int fallbackHeight
    ) {
        StreamConfigurationMap map = characteristics.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
        Size[] sizes = null;
        if (map != null) {
            sizes = map.getOutputSizes(MediaCodec.class);
            if (sizes == null || sizes.length == 0) {
                sizes = map.getOutputSizes(MediaRecorder.class);
            }
        }

        List<Size> output = new ArrayList<>();
        Set<String> seen = new HashSet<>();
        if (sizes != null) {
            for (Size size : sizes) {
                if (size == null || size.getWidth() <= 0 || size.getHeight() <= 0) {
                    continue;
                }

                String key = size.getWidth() + "x" + size.getHeight();
                if (seen.add(key)) {
                    output.add(size);
                }
            }
        }

        if (output.isEmpty()) {
            output.add(new Size(Math.max(1, fallbackWidth), Math.max(1, fallbackHeight)));
        }

        Collections.sort(output, new Comparator<Size>() {
            @Override
            public int compare(Size left, Size right) {
                int areaCompare = Integer.compare(left.getWidth() * left.getHeight(), right.getWidth() * right.getHeight());
                if (areaCompare != 0) {
                    return areaCompare;
                }

                return Integer.compare(left.getWidth(), right.getWidth());
            }
        });
        return output;
    }

    private static List<Integer> targetFpsFromRanges(Range<Integer>[] ranges) {
        Set<Integer> values = new HashSet<>();
        if (ranges != null) {
            for (Range<Integer> range : ranges) {
                if (range == null) {
                    continue;
                }

                int lower = Math.max(1, range.getLower());
                int upper = Math.max(lower, range.getUpper());
                values.add(upper);
                if (lower == upper) {
                    values.add(lower);
                }

                for (int preferred : PREFERRED_TARGET_FPS) {
                    if (preferred >= lower && preferred <= upper) {
                        values.add(preferred);
                    }
                }
            }
        }

        if (values.isEmpty()) {
            values.add(30);
        }

        List<Integer> output = new ArrayList<>(values);
        Collections.sort(output);
        return output;
    }

    private static List<String> supportedCodecsForSize(Size size, List<Integer> targetFps) {
        List<String> codecs = new ArrayList<>();
        for (String codec : CAMERA_ENCODER_CODECS) {
            if (!isEncoderCodecUsable(codec)) {
                continue;
            }

            for (int fpsValue : targetFps) {
                if (isEncoderSizeRateSupported(codec, size.getWidth(), size.getHeight(), fpsValue)) {
                    codecs.add(codec);
                    break;
                }
            }
        }

        return codecs;
    }

    private static List<Integer> supportedFpsForSize(Size size, List<Integer> targetFps, List<String> codecs) {
        List<Integer> values = new ArrayList<>();
        for (int fpsValue : targetFps) {
            for (String codec : codecs) {
                if (isEncoderSizeRateSupported(codec, size.getWidth(), size.getHeight(), fpsValue)) {
                    values.add(fpsValue);
                    break;
                }
            }
        }

        if (values.isEmpty()) {
            values.add(30);
        }

        return values;
    }

    private static JSONArray fpsRangesJson(Range<Integer>[] ranges) throws Exception {
        JSONArray output = new JSONArray();
        if (ranges == null) {
            return output;
        }

        for (Range<Integer> range : ranges) {
            if (range == null) {
                continue;
            }

            JSONObject rangeJson = new JSONObject();
            rangeJson.put("min", range.getLower());
            rangeJson.put("max", range.getUpper());
            output.put(rangeJson);
        }

        return output;
    }

    private static JSONArray intArrayJson(List<Integer> values) {
        JSONArray array = new JSONArray();
        if (values == null) {
            return array;
        }

        for (Integer value : values) {
            if (value != null && value > 0) {
                array.put(value);
            }
        }

        return array;
    }

    private static JSONArray stringArrayJson(List<String> values) {
        JSONArray array = new JSONArray();
        if (values == null) {
            return array;
        }

        for (String value : values) {
            if (value != null && value.length() > 0) {
                array.put(value);
            }
        }

        return array;
    }

    private static List<String> sortedStrings(Set<String> values) {
        List<String> output = new ArrayList<>(values);
        Collections.sort(output);
        return output;
    }

    private static List<String> availableEncoderCodecsForStream() {
        List<String> codecs = new ArrayList<>();
        for (String codec : CAMERA_ENCODER_CODECS) {
            if (isEncoderCodecUsable(codec)) {
                codecs.add(codec);
            }
        }

        if (codecs.isEmpty()) {
            codecs.add(DEFAULT_CODEC);
        }

        return codecs;
    }

    private static boolean isEncoderCodecUsable(String mime) {
        return findEncoderInfo(mime) != null;
    }

    private static boolean isEncoderSizeRateSupported(String mime, int width, int height, int fpsValue) {
        MediaCodecInfo codecInfo = findEncoderInfo(mime);
        if (codecInfo == null) {
            return false;
        }

        try {
            MediaCodecInfo.CodecCapabilities capabilities = codecInfo.getCapabilitiesForType(mime);
            MediaCodecInfo.VideoCapabilities videoCapabilities = capabilities.getVideoCapabilities();
            if (videoCapabilities == null) {
                return true;
            }

            if (fpsValue > 0) {
                return videoCapabilities.areSizeAndRateSupported(width, height, fpsValue);
            }

            return videoCapabilities.isSizeSupported(width, height);
        } catch (Exception ex) {
            return false;
        }
    }

    private static MediaCodecInfo findEncoderInfo(String mime) {
        if (mime == null || mime.length() == 0) {
            return null;
        }

        try {
            MediaCodecList codecList = new MediaCodecList(MediaCodecList.REGULAR_CODECS);
            MediaCodecInfo[] codecInfos = codecList.getCodecInfos();
            for (MediaCodecInfo codecInfo : codecInfos) {
                if (codecInfo == null || !codecInfo.isEncoder()) {
                    continue;
                }

                String[] supportedTypes = codecInfo.getSupportedTypes();
                for (String type : supportedTypes) {
                    if (!mime.equalsIgnoreCase(type)) {
                        continue;
                    }

                    if (supportsSurfaceInput(codecInfo, mime)) {
                        return codecInfo;
                    }
                }
            }
        } catch (Exception ignored) {
        }

        return null;
    }

    private static boolean supportsSurfaceInput(MediaCodecInfo codecInfo, String mime) {
        try {
            MediaCodecInfo.CodecCapabilities capabilities = codecInfo.getCapabilitiesForType(mime);
            for (int colorFormat : capabilities.colorFormats) {
                if (colorFormat == MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface) {
                    return true;
                }
            }
        } catch (Exception ignored) {
        }

        return false;
    }

    private static String hardwareLevelName(CameraCharacteristics characteristics) {
        Integer level = characteristics.get(CameraCharacteristics.INFO_SUPPORTED_HARDWARE_LEVEL);
        if (level == null) {
            return "";
        }

        switch (level) {
            case CameraCharacteristics.INFO_SUPPORTED_HARDWARE_LEVEL_LEGACY:
                return "legacy";
            case CameraCharacteristics.INFO_SUPPORTED_HARDWARE_LEVEL_LIMITED:
                return "limited";
            case CameraCharacteristics.INFO_SUPPORTED_HARDWARE_LEVEL_FULL:
                return "full";
            case CameraCharacteristics.INFO_SUPPORTED_HARDWARE_LEVEL_3:
                return "level_3";
            case CameraCharacteristics.INFO_SUPPORTED_HARDWARE_LEVEL_EXTERNAL:
                return "external";
            default:
                return String.valueOf(level);
        }
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

    private static String normalizeCodec(String value) {
        if (value == null) {
            return DEFAULT_CODEC;
        }

        String normalized = value.trim().toLowerCase(Locale.ROOT);
        if ("avc".equals(normalized)
            || "h264".equals(normalized)
            || "h.264".equals(normalized)
            || "video/h264".equals(normalized)) {
            return DEFAULT_CODEC;
        }

        if ("hevc".equals(normalized)
            || "h265".equals(normalized)
            || "h.265".equals(normalized)
            || "video/h265".equals(normalized)) {
            return HEVC_CODEC;
        }

        return normalized.length() == 0 ? DEFAULT_CODEC : normalized;
    }

    private static String selectEffectiveCameraCodec(String requestedCodec) {
        String normalized = normalizeCodec(requestedCodec);
        if (DEFAULT_CODEC.equals(normalized) && isEncoderCodecUsable(DEFAULT_CODEC)) {
            return DEFAULT_CODEC;
        }

        // The current Windows receiver decodes AVC. HEVC-capable devices still fall back safely.
        return DEFAULT_CODEC;
    }

    private static Size chooseCaptureSize(
        CameraCharacteristics characteristics,
        int requestedWidth,
        int requestedHeight,
        String codec
    ) {
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
            if (size.getWidth() == requestedWidth
                && size.getHeight() == requestedHeight
                && isEncoderSizeSupported(codec, size.getWidth(), size.getHeight())) {
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

        for (Size size : sizes) {
            if (isEncoderSizeSupported(codec, size.getWidth(), size.getHeight())) {
                return size;
            }
        }

        return sizes[0];
    }

    private static int sizeScore(Size size, int requestedWidth, int requestedHeight) {
        int areaDelta = Math.abs(size.getWidth() * size.getHeight() - requestedWidth * requestedHeight);
        int ratioDelta = Math.abs(size.getWidth() * requestedHeight - requestedWidth * size.getHeight());
        return areaDelta + ratioDelta;
    }

    private static boolean isEncoderSizeSupported(String mime, int width, int height) {
        MediaCodecInfo codecInfo = findEncoderInfo(mime);
        if (codecInfo == null) {
            return DEFAULT_CODEC.equals(mime);
        }

        try {
            MediaCodecInfo.CodecCapabilities capabilities = codecInfo.getCapabilitiesForType(mime);
            MediaCodecInfo.VideoCapabilities videoCapabilities = capabilities.getVideoCapabilities();
            return videoCapabilities == null || videoCapabilities.isSizeSupported(width, height);
        } catch (Exception ex) {
            return false;
        }
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

    private static int chooseEffectiveFps(
        CameraCharacteristics characteristics,
        Size size,
        int requestedFps,
        String codec
    ) {
        Range<Integer>[] ranges = characteristics.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES);
        if (isCameraFpsSupported(ranges, requestedFps)
            && isEncoderSizeRateSupported(codec, size.getWidth(), size.getHeight(), requestedFps)) {
            return Math.max(1, requestedFps);
        }

        List<Integer> candidates = targetFpsFromRanges(ranges);
        Collections.sort(candidates, new Comparator<Integer>() {
            @Override
            public int compare(Integer left, Integer right) {
                int leftPenalty = left > requestedFps ? 10000 : 0;
                int rightPenalty = right > requestedFps ? 10000 : 0;
                int leftScore = leftPenalty + Math.abs(left - requestedFps);
                int rightScore = rightPenalty + Math.abs(right - requestedFps);
                if (leftScore != rightScore) {
                    return leftScore - rightScore;
                }

                return right - left;
            }
        });

        for (int candidate : candidates) {
            if (isCameraFpsSupported(ranges, candidate)
                && isEncoderSizeRateSupported(codec, size.getWidth(), size.getHeight(), candidate)) {
                return Math.max(1, candidate);
            }
        }

        return effectiveFpsForRange(chooseFpsRange(characteristics, requestedFps), requestedFps);
    }

    private static boolean isCameraFpsSupported(Range<Integer>[] ranges, int fpsValue) {
        if (fpsValue <= 0) {
            return false;
        }

        if (ranges == null || ranges.length == 0) {
            return true;
        }

        for (Range<Integer> range : ranges) {
            if (range != null && fpsValue >= range.getLower() && fpsValue <= range.getUpper()) {
                return true;
            }
        }

        return false;
    }

    private static int effectiveFpsForRange(Range<Integer> range, int requestedFps) {
        if (range == null) {
            return Math.max(1, requestedFps);
        }

        int lower = range.getLower();
        int upper = range.getUpper();
        if (requestedFps >= lower && requestedFps <= upper) {
            return requestedFps;
        }

        return Math.max(1, upper);
    }

    private static String cameraFacingName(CameraCharacteristics characteristics, String fallback) {
        Integer cameraFacing = characteristics.get(CameraCharacteristics.LENS_FACING);
        if (cameraFacing != null) {
            if (cameraFacing == CameraCharacteristics.LENS_FACING_FRONT) {
                return "front";
            }

            if (cameraFacing == CameraCharacteristics.LENS_FACING_BACK) {
                return "back";
            }
        }

        return normalizeFacing(fallback);
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

    private void emitConfigApplied(int port, int width, int height, int fps, String codec, String facing) {
        listener.onCameraCaptureConfigApplied(port, width, height, fps, codec, facing);
    }

    private void emitStats(long packetsSent, long bytesSent, long keyFrames, long codecConfigPackets) {
        listener.onCameraCaptureStats(packetsSent, bytesSent, keyFrames, codecConfigPackets);
    }

    private static final class StopToken {
        final ExecutorService executor;
        final CountDownLatch stopLatch;

        StopToken(ExecutorService executor, CountDownLatch stopLatch) {
            this.executor = executor;
            this.stopLatch = stopLatch;
        }
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
