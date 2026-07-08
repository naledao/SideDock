package com.sidedock.client;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaCodecList;
import android.media.MediaFormat;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.view.Surface;
import java.io.EOFException;
import java.io.InputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.nio.ByteBuffer;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ThreadFactory;

public final class VideoClient {
    private static final String TAG = "SideDock.Video";
    private static final String ERROR_DECODER_UNSUPPORTED = "DECODER_UNSUPPORTED";
    private static final String ERROR_VIDEO_FAILED = "VIDEO_FAILED";

    public interface Listener {
        void onVideoState(String state);
        void onVideoLog(String message);
        void onVideoStats(VideoStats stats);
        void onVideoError(String code, String message);
    }

    public static final class VideoStats {
        public final long framesDecoded;
        public final long framesRendered;
        public final long packetsReceived;
        public final long decodeErrors;
        public final long droppedFrames;
        public final long reconnects;
        public final long roughLatencyMs;
        public final double decodeFps;
        public final double renderFps;
        public final double newFrameFps;
        public final double repeatFrameFps;
        public final long newFramesReceived;
        public final long repeatFramesReceived;
        public final long blackFramesReceived;
        public final long keepaliveFramesReceived;
        public final String lastFrameKind;
        public final long lastSourceSeq;
        public final int lastSourceAgeMs;
        public final double lastReceiveToQueueMs;
        public final double lastQueueToOutputMs;
        public final double lastOutputToRenderMs;
        public final double lastQueueToRenderMs;
        public final double p50QueueToOutputMs;
        public final double p95QueueToOutputMs;
        public final double p99QueueToOutputMs;
        public final double p50OutputToRenderMs;
        public final double p95OutputToRenderMs;
        public final double p99OutputToRenderMs;
        public final double p50QueueToRenderMs;
        public final double p95QueueToRenderMs;
        public final double p99QueueToRenderMs;
        public final long localPipelineLatencyMs;
        public final double lastEncodeMs;
        public final long latencyErrorBoundMs;
        public final String state;

        private VideoStats(
            long framesDecoded,
            long framesRendered,
            long packetsReceived,
            long decodeErrors,
            long droppedFrames,
            long reconnects,
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
            this.framesDecoded = framesDecoded;
            this.framesRendered = framesRendered;
            this.packetsReceived = packetsReceived;
            this.decodeErrors = decodeErrors;
            this.droppedFrames = droppedFrames;
            this.reconnects = reconnects;
            this.roughLatencyMs = roughLatencyMs;
            this.decodeFps = decodeFps;
            this.renderFps = renderFps;
            this.newFrameFps = newFrameFps;
            this.repeatFrameFps = repeatFrameFps;
            this.newFramesReceived = newFramesReceived;
            this.repeatFramesReceived = repeatFramesReceived;
            this.blackFramesReceived = blackFramesReceived;
            this.keepaliveFramesReceived = keepaliveFramesReceived;
            this.lastFrameKind = lastFrameKind;
            this.lastSourceSeq = lastSourceSeq;
            this.lastSourceAgeMs = lastSourceAgeMs;
            this.lastReceiveToQueueMs = lastReceiveToQueueMs;
            this.lastQueueToOutputMs = lastQueueToOutputMs;
            this.lastOutputToRenderMs = lastOutputToRenderMs;
            this.lastQueueToRenderMs = lastQueueToRenderMs;
            this.p50QueueToOutputMs = p50QueueToOutputMs;
            this.p95QueueToOutputMs = p95QueueToOutputMs;
            this.p99QueueToOutputMs = p99QueueToOutputMs;
            this.p50OutputToRenderMs = p50OutputToRenderMs;
            this.p95OutputToRenderMs = p95OutputToRenderMs;
            this.p99OutputToRenderMs = p99OutputToRenderMs;
            this.p50QueueToRenderMs = p50QueueToRenderMs;
            this.p95QueueToRenderMs = p95QueueToRenderMs;
            this.p99QueueToRenderMs = p99QueueToRenderMs;
            this.localPipelineLatencyMs = localPipelineLatencyMs;
            this.lastEncodeMs = lastEncodeMs;
            this.latencyErrorBoundMs = latencyErrorBoundMs;
            this.state = state;
        }
    }

    private static final int HEADER_SIZE = 24;
    private static final int EXTENDED_HEADER_SIZE = 16;
    private static final int MAX_PAYLOAD_LENGTH = 8 * 1024 * 1024;
    private static final long INPUT_TIMEOUT_US = 10000L;
    private static final byte[] MAGIC = new byte[] { 'S', 'D', 'K', 'V' };
    private static final int FRAME_KIND_NEW = 0;
    private static final int FRAME_KIND_REPEAT = 1;
    private static final int FRAME_KIND_BLACK = 2;
    private static final int FRAME_KIND_KEEPALIVE = 3;
    private static final long FRAME_STATS_INTERVAL_MS = 500L;
    private static final int DECODE_QUEUE_CAPACITY = 12;

    private final String host;
    private final Listener listener;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final Object lifecycleLock = new Object();
    private final Object timingLock = new Object();
    private final HashMap<Long, PacketTiming> packetTimings = new HashMap<>();
    private final ArrayList<Double> queueToOutputSamples = new ArrayList<>();
    private final ArrayList<Double> outputToRenderSamples = new ArrayList<>();
    private final ArrayList<Double> queueToRenderSamples = new ArrayList<>();

    private ExecutorService executor;
    private Socket socket;
    private volatile boolean running;
    private volatile Surface surface;
    private volatile int port;
    private volatile int width;
    private volatile int height;
    private volatile int fps;
    private volatile String state = "STOPPED";
    private long generation;
    private long framesDecoded;
    private long framesRendered;
    private long packetsReceived;
    private long newFramesReceived;
    private long repeatFramesReceived;
    private long blackFramesReceived;
    private long keepaliveFramesReceived;
    private long decodeErrors;
    private long droppedFrames;
    private long reconnects;
    private long roughLatencyMs;
    private long lastFrameStatsEmitAtMs;
    private long lastRenderedAtNanos;
    private double instantRenderFps;
    private long lastRateSnapshotNanos;
    private long lastRateDecodedFrames;
    private long lastRateRenderedFrames;
    private long lastRateNewFrames;
    private long lastRateRepeatFrames;
    private double decodeFps;
    private double renderFps;
    private double newFrameFps;
    private double repeatFrameFps;
    private String lastFrameKind = "new";
    private long lastSourceSeq;
    private int lastSourceAgeMs;
    private double lastReceiveToQueueMs;
    private double lastQueueToOutputMs;
    private double lastOutputToRenderMs;
    private double lastQueueToRenderMs;
    private double p50QueueToOutputMs;
    private double p95QueueToOutputMs;
    private double p99QueueToOutputMs;
    private double p50OutputToRenderMs;
    private double p95OutputToRenderMs;
    private double p99OutputToRenderMs;
    private double p50QueueToRenderMs;
    private double p95QueueToRenderMs;
    private double p99QueueToRenderMs;
    private long localPipelineLatencyMs;
    private double lastEncodeMs;
    private volatile long serverTimeOffsetMs;
    private volatile long latencyErrorBoundMs = Long.MAX_VALUE;

    public VideoClient(Listener listener) {
        this("127.0.0.1", listener);
    }

    public VideoClient(String host, Listener listener) {
        this.host = host;
        this.listener = listener;
    }

    public void start(Surface nextSurface, int nextPort, int nextWidth, int nextHeight, int nextFps) {
        synchronized (lifecycleLock) {
            if (running
                && surface == nextSurface
                && port == nextPort
                && width == nextWidth
                && height == nextHeight
                && fps == nextFps) {
                return;
            }

            stopLocked();
            surface = nextSurface;
            port = nextPort;
            width = nextWidth;
            height = nextHeight;
            fps = Math.max(1, nextFps);
            resetStats();
            running = true;
            final long runGeneration = ++generation;
            executor = Executors.newSingleThreadExecutor(new NamedThreadFactory("SideDock-VideoClient"));
            executor.execute(new Runnable() {
                @Override
                public void run() {
                    connectLoop(runGeneration);
                }
            });
        }
    }

    public void stop() {
        synchronized (lifecycleLock) {
            stopLocked();
        }
    }

    public boolean isRunningFor(Surface nextSurface, int nextPort, int nextWidth, int nextHeight, int nextFps) {
        return running
            && surface == nextSurface
            && port == nextPort
            && width == nextWidth
            && height == nextHeight
            && fps == Math.max(1, nextFps);
    }

    public void setServerTimeOffsetMs(long offsetMs, long errorBoundMs) {
        serverTimeOffsetMs = offsetMs;
        latencyErrorBoundMs = errorBoundMs;
    }

    private void stopLocked() {
        running = false;
        generation += 1;
        closeSocket();
        if (executor != null) {
            executor.shutdownNow();
            executor = null;
        }
        emitState("STOPPED");
    }

    private void resetStats() {
        framesDecoded = 0L;
        synchronized (timingLock) {
            framesRendered = 0L;
            packetTimings.clear();
            queueToOutputSamples.clear();
            outputToRenderSamples.clear();
            queueToRenderSamples.clear();
        }
        packetsReceived = 0L;
        newFramesReceived = 0L;
        repeatFramesReceived = 0L;
        blackFramesReceived = 0L;
        keepaliveFramesReceived = 0L;
        decodeErrors = 0L;
        droppedFrames = 0L;
        reconnects = 0L;
        roughLatencyMs = 0L;
        lastFrameStatsEmitAtMs = 0L;
        lastRenderedAtNanos = 0L;
        instantRenderFps = 0.0;
        lastRateSnapshotNanos = 0L;
        lastRateDecodedFrames = 0L;
        lastRateRenderedFrames = 0L;
        lastRateNewFrames = 0L;
        lastRateRepeatFrames = 0L;
        decodeFps = 0.0;
        renderFps = 0.0;
        newFrameFps = 0.0;
        repeatFrameFps = 0.0;
        lastFrameKind = "new";
        lastSourceSeq = 0L;
        lastSourceAgeMs = 0;
        lastReceiveToQueueMs = 0.0;
        lastQueueToOutputMs = 0.0;
        lastOutputToRenderMs = 0.0;
        lastQueueToRenderMs = 0.0;
        p50QueueToOutputMs = 0.0;
        p95QueueToOutputMs = 0.0;
        p99QueueToOutputMs = 0.0;
        p50OutputToRenderMs = 0.0;
        p95OutputToRenderMs = 0.0;
        p99OutputToRenderMs = 0.0;
        p50QueueToRenderMs = 0.0;
        p95QueueToRenderMs = 0.0;
        p99QueueToRenderMs = 0.0;
        localPipelineLatencyMs = 0L;
        lastEncodeMs = 0.0;
    }

    private boolean isCurrentGeneration(long runGeneration) {
        synchronized (lifecycleLock) {
            return generation == runGeneration;
        }
    }

    private boolean isSupersededGeneration(long runGeneration) {
        synchronized (lifecycleLock) {
            return generation != runGeneration;
        }
    }

    private void connectLoop(long runGeneration) {
        boolean firstAttempt = true;
        while (running && isCurrentGeneration(runGeneration) && surface != null && surface.isValid()) {
            emitState(firstAttempt ? "CONNECTING" : "RECONNECTING");
            firstAttempt = false;
            Socket attemptSocket = null;

            try {
                attemptSocket = new Socket();
                openDecodeRead(attemptSocket, runGeneration);
            } catch (Exception ex) {
                if (running && !isSupersededGeneration(runGeneration)) {
                    decodeErrors += 1;
                    String message = ex.getMessage() == null ? ex.getClass().getSimpleName() : ex.getMessage();
                    String code = isDecoderUnsupported(ex) ? ERROR_DECODER_UNSUPPORTED : ERROR_VIDEO_FAILED;
                    Log.e(TAG, "video failed", ex);
                    emitError(code, message);
                    emitLog("Video channel disconnected: " + message);
                }
            } finally {
                closeSocket(attemptSocket);
                if (!isSupersededGeneration(runGeneration)) {
                    emitStats();
                }
            }

            if (running && isCurrentGeneration(runGeneration) && surface != null && surface.isValid()) {
                reconnects += 1;
                sleepQuietly(Math.min(1000L * reconnects, 5000L));
            }
        }

        if (isCurrentGeneration(runGeneration)) {
            emitState("STOPPED");
        }
    }

    private void openDecodeRead(Socket nextSocket, long runGeneration) throws Exception {
        nextSocket.setTcpNoDelay(true);
        nextSocket.connect(new InetSocketAddress(host, port), 3000);
        nextSocket.setSoTimeout(5000);
        synchronized (lifecycleLock) {
            if (!running || generation != runGeneration) {
                nextSocket.close();
                throw new IllegalStateException("Video client generation stopped before socket registration");
            }

            socket = nextSocket;
        }
        emitState("CONNECTED");
        emitLog("Video channel connected " + host + ":" + port);
        Log.i(TAG, "connected " + host + ":" + port);

        InputStream input = nextSocket.getInputStream();
        LatestVideoPacketQueue decodeQueue = new LatestVideoPacketQueue(DECODE_QUEUE_CAPACITY);
        Thread decoderThread = new Thread(new Runnable() {
            @Override
            public void run() {
                decodeLoop(decodeQueue);
            }
        }, "SideDock-VideoDecoder");
        decoderThread.setDaemon(true);
        decoderThread.start();
        long lastStatsAt = System.currentTimeMillis();

        try {
            while (running && !nextSocket.isClosed()) {
                VideoPacket packet = readPacket(input);
                packetsReceived += 1;
                recordPacketKind(packet);
                droppedFrames += decodeQueue.offerLatest(packet);
                long now = System.currentTimeMillis();
                if (now - lastStatsAt >= 2000L) {
                    emitStats();
                    lastStatsAt = now;
                }
            }
        } finally {
            decodeQueue.close();
            decoderThread.interrupt();
            joinQuietly(decoderThread, 1000L);
        }
    }

    private void decodeLoop(LatestVideoPacketQueue decodeQueue) {
        ArrayDeque<VideoPacket> pendingPackets = new ArrayDeque<>();
        CodecConfig codecConfig = new CodecConfig();
        MediaCodec codec = null;
        MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();
        long submittedPackets = 0L;
        long lastDecodeSeq = -1L;
        boolean waitingForKeyFrame = false;
        boolean decodeDisabled = false;
        boolean forceSoftwareDecoder = false;
        String decoderName = "";

        try {
            while (running) {
                VideoPacket packet = decodeQueue.take(250L);
                if (packet == null) {
                    if (decodeQueue.isClosed()) {
                        break;
                    }
                    if (codec != null) {
                        drainOutput(codec, bufferInfo, 0L);
                    }
                    continue;
                }

                if (decodeDisabled) {
                    continue;
                }

                boolean sequenceGap = lastDecodeSeq >= 0L && !isNextSequence(lastDecodeSeq, packet.seq);
                lastDecodeSeq = packet.seq;
                if (sequenceGap) {
                    pendingPackets.clear();
                    if (!waitingForKeyFrame) {
                        emitLog("Video packet gap detected; waiting for key frame");
                    }
                    waitingForKeyFrame = true;
                }

                if (waitingForKeyFrame && !packet.isKeyFrame) {
                    droppedFrames += 1;
                    continue;
                }

                if (waitingForKeyFrame) {
                    waitingForKeyFrame = false;
                    emitLog("Video key frame received; decoder stream recovered");
                }

                codecConfig.scan(packet.payload);
                pendingPackets.add(packet);

                try {
                    if (codec == null && (codecConfig.isReady() || pendingPackets.size() >= 30)) {
                        codec = createDecoder(codecConfig, forceSoftwareDecoder);
                        decoderName = codec.getName();
                        emitLog("MediaCodec started, csd=" + codecConfig.isReady());
                        Log.i(TAG, "MediaCodec started csd=" + codecConfig.isReady());
                    }

                    while (codec != null && !pendingPackets.isEmpty()) {
                        long nextSubmittedPacket = submittedPackets + 1L;
                        if (queuePacket(codec, bufferInfo, pendingPackets.removeFirst(), nextSubmittedPacket)) {
                            submittedPackets = nextSubmittedPacket;
                        } else {
                            pendingPackets.clear();
                            if (!waitingForKeyFrame) {
                                emitLog("MediaCodec input queue dropped a packet; waiting for key frame");
                            }
                            waitingForKeyFrame = true;
                        }
                    }
                } catch (Exception ex) {
                    decodeErrors += 1;
                    pendingPackets.clear();
                    releaseCodecQuietly(codec);
                    codec = null;
                    String message = ex.getMessage() == null ? ex.getClass().getSimpleName() : ex.getMessage();
                    if (!forceSoftwareDecoder && shouldRetryWithSoftwareDecoder(decoderName, ex)) {
                        forceSoftwareDecoder = true;
                        decoderName = "";
                        Log.w(TAG, "hardware decoder failed; retrying software decoder", ex);
                        emitLog("Hardware decoder failed; retrying software decoder: " + message);
                        continue;
                    }

                    decodeDisabled = true;
                    Log.e(TAG, "decoder disabled; continuing receive-only", ex);
                    emitLog("Decoder disabled; continuing receive-only: " + message);
                }
            }
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
        } finally {
            releaseCodecQuietly(codec);
        }
    }

    private void releaseCodecQuietly(MediaCodec codec) {
        if (codec == null) {
            return;
        }
        try {
            codec.stop();
        } catch (Exception ignored) {
        }
        try {
            codec.release();
        } catch (Exception ignored) {
        }
    }

    private MediaCodec createDecoder(CodecConfig codecConfig, boolean forceSoftwareDecoder) throws Exception {
        MediaFormat format = createDecoderFormat(codecConfig, true);
        MediaCodec codec = createDecoderInstance(forceSoftwareDecoder);
        String decoderName = codec.getName();
        try {
            Log.i(TAG, "configuring decoder name=" + decoderName + " format=" + format);
            codec.configure(format, surface, null, 0);
        } catch (Exception ex) {
            Log.w(TAG, "decoder configure with low-latency failed name=" + decoderName, ex);
            codec.release();
            MediaFormat fallbackFormat = createDecoderFormat(codecConfig, false);
            codec = createDecoderInstance(forceSoftwareDecoder);
            decoderName = codec.getName();
            Log.i(TAG, "configuring decoder fallback name=" + decoderName + " format=" + fallbackFormat);
            codec.configure(fallbackFormat, surface, null, 0);
            emitLog("MediaCodec low-latency keys ignored: " + ex.getClass().getSimpleName());
        }

        codec.setOnFrameRenderedListener(new MediaCodec.OnFrameRenderedListener() {
            @Override
            public void onFrameRendered(MediaCodec codec, long presentationTimeUs, long nanoTime) {
                recordFrameRendered(presentationTimeUs, nanoTime);
            }
        }, mainHandler);
        codec.start();
        Log.i(TAG, "MediaCodec started name=" + decoderName + " csd=" + codecConfig.isReady());
        return codec;
    }

    private MediaCodec createDecoderInstance(boolean forceSoftwareDecoder) throws Exception {
        if (forceSoftwareDecoder) {
            String softwareDecoder = findSoftwareAvcDecoder();
            if (softwareDecoder != null) {
                Log.i(TAG, "selected software decoder name=" + softwareDecoder);
                return MediaCodec.createByCodecName(softwareDecoder);
            }

            Log.w(TAG, "software AVC decoder not found; falling back to decoder by type");
        }

        return MediaCodec.createDecoderByType("video/avc");
    }

    private String findSoftwareAvcDecoder() {
        try {
            MediaCodecList codecList = new MediaCodecList(MediaCodecList.REGULAR_CODECS);
            MediaCodecInfo[] codecInfos = codecList.getCodecInfos();
            for (MediaCodecInfo codecInfo : codecInfos) {
                if (codecInfo.isEncoder() || !isSoftwareDecoder(codecInfo)) {
                    continue;
                }

                String[] supportedTypes = codecInfo.getSupportedTypes();
                for (String type : supportedTypes) {
                    if ("video/avc".equalsIgnoreCase(type)) {
                        return codecInfo.getName();
                    }
                }
            }
        } catch (Exception ex) {
            Log.w(TAG, "unable to enumerate software AVC decoders", ex);
        }

        return null;
    }

    private boolean shouldRetryWithSoftwareDecoder(String currentDecoderName, Throwable throwable) {
        return !isSoftwareDecoderName(currentDecoderName) && isDecoderUnsupported(throwable);
    }

    private static boolean isSoftwareDecoder(MediaCodecInfo codecInfo) {
        if (Build.VERSION.SDK_INT >= 29) {
            return codecInfo.isSoftwareOnly();
        }

        return isSoftwareDecoderName(codecInfo.getName());
    }

    private static boolean isSoftwareDecoderName(String decoderName) {
        if (decoderName == null) {
            return false;
        }

        String normalized = decoderName.toLowerCase();
        return normalized.contains("google")
            || normalized.contains("android")
            || normalized.contains("software")
            || normalized.startsWith("c2.android");
    }

    private MediaFormat createDecoderFormat(CodecConfig codecConfig, boolean lowLatency) {
        MediaFormat format = MediaFormat.createVideoFormat("video/avc", width, height);
        format.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
        format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, MAX_PAYLOAD_LENGTH);
        if (lowLatency) {
            format.setInteger(MediaFormat.KEY_PRIORITY, 0);
            format.setFloat(MediaFormat.KEY_OPERATING_RATE, (float) Math.max(1, fps));
            if (Build.VERSION.SDK_INT >= 30) {
                format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);
            }
        }
        if (codecConfig.sps != null) {
            format.setByteBuffer("csd-0", ByteBuffer.wrap(codecConfig.sps));
        }
        if (codecConfig.pps != null) {
            format.setByteBuffer("csd-1", ByteBuffer.wrap(codecConfig.pps));
        }
        return format;
    }

    private boolean queuePacket(
        MediaCodec codec,
        MediaCodec.BufferInfo bufferInfo,
        VideoPacket packet,
        long submittedPackets
    ) throws Exception {
        drainOutput(codec, bufferInfo, 0L);

        int inputIndex = codec.dequeueInputBuffer(INPUT_TIMEOUT_US);
        int attempts = 0;
        while (inputIndex < 0 && attempts < 5) {
            drainOutput(codec, bufferInfo, INPUT_TIMEOUT_US);
            inputIndex = codec.dequeueInputBuffer(INPUT_TIMEOUT_US);
            attempts += 1;
        }

        if (inputIndex < 0) {
            droppedFrames += 1;
            return false;
        }

        ByteBuffer inputBuffer = codec.getInputBuffer(inputIndex);
        if (inputBuffer == null) {
            throw new IllegalStateException("MediaCodec input buffer is null");
        }
        if (packet.payload.length > inputBuffer.capacity()) {
            throw new IllegalStateException("Video packet too large: " + packet.payload.length);
        }

        inputBuffer.clear();
        inputBuffer.put(packet.payload);
        long presentationTimeUs = submittedPackets * (1000000L / Math.max(1, fps));
        long queueInputAtNanos = System.nanoTime();
        lastReceiveToQueueMs = nanosToMs(queueInputAtNanos - packet.receiveElapsedRealtimeNanos);
        codec.queueInputBuffer(inputIndex, 0, packet.payload.length, presentationTimeUs, 0);
        synchronized (timingLock) {
            packetTimings.put(presentationTimeUs, new PacketTiming(packet, queueInputAtNanos));
            trimPacketTimingsLocked();
        }
        drainOutput(codec, bufferInfo, 0L);
        return true;
    }

    private void drainOutput(MediaCodec codec, MediaCodec.BufferInfo bufferInfo, long timeoutUs) {
        while (running) {
            int outputIndex = codec.dequeueOutputBuffer(bufferInfo, timeoutUs);
            if (outputIndex >= 0) {
                boolean render = bufferInfo.size > 0;
                if (render) {
                    recordFrameOutput(bufferInfo.presentationTimeUs);
                }
                codec.releaseOutputBuffer(outputIndex, render);
                if (render) {
                    framesDecoded += 1;
                    long now = System.currentTimeMillis();
                    if (framesDecoded == 1L || now - lastFrameStatsEmitAtMs >= FRAME_STATS_INTERVAL_MS) {
                        lastFrameStatsEmitAtMs = now;
                        emitStats();
                    }
                }
                timeoutUs = 0L;
                continue;
            }
            if (outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                emitLog("Output format: " + codec.getOutputFormat());
                timeoutUs = 0L;
                continue;
            }
            break;
        }
    }

    private void recordFrameOutput(long presentationTimeUs) {
        long outputAtNanos = System.nanoTime();
        synchronized (timingLock) {
            PacketTiming timing = packetTimings.get(presentationTimeUs);
            if (timing != null) {
                timing.outputElapsedRealtimeNanos = outputAtNanos;
                lastQueueToOutputMs = nanosToMs(outputAtNanos - timing.queueInputElapsedRealtimeNanos);
                addSample(queueToOutputSamples, lastQueueToOutputMs);
            }
        }
    }

    private void recordFrameRendered(long presentationTimeUs, long renderedFrameNanos) {
        long callbackAtNanos = System.nanoTime();
        long renderAtNanos = renderedFrameNanos > 0L ? renderedFrameNanos : callbackAtNanos;
        synchronized (timingLock) {
            framesRendered += 1;
            PacketTiming timing = packetTimings.remove(presentationTimeUs);
            if (timing != null) {
                if (timing.outputElapsedRealtimeNanos > 0L) {
                    lastOutputToRenderMs = nanosToMs(renderAtNanos - timing.outputElapsedRealtimeNanos);
                    addSample(outputToRenderSamples, lastOutputToRenderMs);
                }
                lastQueueToRenderMs = nanosToMs(renderAtNanos - timing.queueInputElapsedRealtimeNanos);
                addSample(queueToRenderSamples, lastQueueToRenderMs);
                localPipelineLatencyMs = Math.round(nanosToMs(renderAtNanos - timing.receiveElapsedRealtimeNanos));
                roughLatencyMs = renderedLatencySinceHostTimestampMs(timing, renderAtNanos);
            }
            updateInstantRenderFpsLocked(renderAtNanos);
        }

        long now = System.currentTimeMillis();
        if (framesRendered == 1L || now - lastFrameStatsEmitAtMs >= FRAME_STATS_INTERVAL_MS) {
            lastFrameStatsEmitAtMs = now;
            emitStats();
        }
    }

    private VideoPacket readPacket(InputStream input) throws Exception {
        byte[] header = new byte[HEADER_SIZE];
        readFully(input, header, 0, header.length);
        if (header[0] != MAGIC[0] || header[1] != MAGIC[1] || header[2] != MAGIC[2] || header[3] != MAGIC[3]) {
            throw new IllegalStateException("video magic mismatch");
        }
        int version = header[4] & 0xFF;
        if (version != 1 && version != 2) {
            throw new IllegalStateException("unsupported video packet version: " + version);
        }

        boolean isKeyFrame = (header[5] & 0xFF) != 0;
        long seq = readUInt32Le(header, 8);
        long timestampMs = readInt64Le(header, 12);
        int length = readInt32Le(header, 20);
        int frameKind = FRAME_KIND_NEW;
        long sourceSeq = seq;
        int sourceAgeMs = 0;
        int encodeUs = 0;
        if (version >= 2) {
            frameKind = header[6] & 0xFF;
            byte[] extended = new byte[EXTENDED_HEADER_SIZE];
            readFully(input, extended, 0, extended.length);
            sourceSeq = readInt64Le(extended, 0);
            sourceAgeMs = Math.max(0, readInt32Le(extended, 8));
            encodeUs = Math.max(0, readInt32Le(extended, 12));
        }
        if (length <= 0 || length > MAX_PAYLOAD_LENGTH) {
            throw new IllegalStateException("invalid video payload length: " + length);
        }

        byte[] payload = new byte[length];
        readFully(input, payload, 0, payload.length);
        long receiveAtMs = System.currentTimeMillis();
        long receiveAtNanos = System.nanoTime();
        return new VideoPacket(
            seq,
            timestampMs,
            payload,
            isKeyFrame,
            frameKind,
            sourceSeq,
            sourceAgeMs,
            encodeUs / 1000.0,
            receiveAtMs,
            receiveAtNanos
        );
    }

    private static void readFully(InputStream input, byte[] buffer, int offset, int length) throws Exception {
        int readTotal = 0;
        while (readTotal < length) {
            int read;
            try {
                read = input.read(buffer, offset + readTotal, length - readTotal);
            } catch (SocketTimeoutException ex) {
                continue;
            }
            if (read < 0) {
                throw new EOFException();
            }
            readTotal += read;
        }
    }

    private static int readInt32Le(byte[] data, int offset) {
        return (data[offset] & 0xFF)
            | ((data[offset + 1] & 0xFF) << 8)
            | ((data[offset + 2] & 0xFF) << 16)
            | ((data[offset + 3] & 0xFF) << 24);
    }

    private static long readUInt32Le(byte[] data, int offset) {
        return readInt32Le(data, offset) & 0xFFFFFFFFL;
    }

    private static boolean isNextSequence(long previous, long current) {
        return (((previous + 1L) & 0xFFFFFFFFL) == (current & 0xFFFFFFFFL));
    }

    private static long readInt64Le(byte[] data, int offset) {
        return ((long) data[offset] & 0xFFL)
            | (((long) data[offset + 1] & 0xFFL) << 8)
            | (((long) data[offset + 2] & 0xFFL) << 16)
            | (((long) data[offset + 3] & 0xFFL) << 24)
            | (((long) data[offset + 4] & 0xFFL) << 32)
            | (((long) data[offset + 5] & 0xFFL) << 40)
            | (((long) data[offset + 6] & 0xFFL) << 48)
            | (((long) data[offset + 7] & 0xFFL) << 56);
    }

    private boolean isDecoderUnsupported(Throwable throwable) {
        Throwable current = throwable;
        while (current != null) {
            if (current instanceof MediaCodec.CodecException) {
                MediaCodec.CodecException codecException = (MediaCodec.CodecException) current;
                if (!codecException.isRecoverable() && !codecException.isTransient()) {
                    return true;
                }
            }
            if (current instanceof IllegalStateException
                && width >= 2560
                && height >= 1440
                && fps > 60
                && hasMediaCodecStack(current)) {
                return true;
            }

            String message = current.getMessage();
            if (message != null) {
                String normalized = message.toLowerCase();
                if (normalized.contains("insufficient")
                    || normalized.contains("resource")
                    || normalized.contains("hardware")
                    || normalized.contains("unsupported")
                    || normalized.contains("released state")
                    || normalized.contains("executing state")
                    || normalized.contains("pending dequeue input buffer")
                    || normalized.contains("0x80001001")
                    || normalized.contains("0x80001000")) {
                    return true;
                }
            }

            current = current.getCause();
        }

        return false;
    }

    private static boolean hasMediaCodecStack(Throwable throwable) {
        StackTraceElement[] stackTrace = throwable.getStackTrace();
        for (StackTraceElement element : stackTrace) {
            if (element.getClassName().startsWith("android.media.MediaCodec")) {
                return true;
            }
        }

        return false;
    }

    private void closeSocket() {
        Socket currentSocket;
        synchronized (lifecycleLock) {
            currentSocket = socket;
            socket = null;
        }

        closeSocketQuietly(currentSocket);
    }

    private void closeSocket(Socket targetSocket) {
        if (targetSocket == null) {
            return;
        }

        synchronized (lifecycleLock) {
            if (socket == targetSocket) {
                socket = null;
            }
        }

        closeSocketQuietly(targetSocket);
    }

    private static void closeSocketQuietly(Socket targetSocket) {
        if (targetSocket == null) {
            return;
        }

        try {
            targetSocket.close();
        } catch (Exception ignored) {
        }
    }

    private void sleepQuietly(long delayMs) {
        try {
            Thread.sleep(delayMs);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
        }
    }

    private void joinQuietly(Thread thread, long timeoutMs) {
        try {
            thread.join(timeoutMs);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
        }
    }

    private void emitState(String nextState) {
        state = nextState;
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onVideoState(nextState);
            }
        });
    }

    private void emitLog(String message) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onVideoLog(message);
            }
        });
    }

    private void emitError(String code, String message) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onVideoError(code, message);
            }
        });
    }

    private void emitStats() {
        updateRateSnapshot();
        long renderedSnapshot;
        synchronized (timingLock) {
            renderedSnapshot = framesRendered;
            double[] queueToOutput = percentiles(queueToOutputSamples);
            double[] outputToRender = percentiles(outputToRenderSamples);
            double[] queueToRender = percentiles(queueToRenderSamples);
            p50QueueToOutputMs = queueToOutput[0];
            p95QueueToOutputMs = queueToOutput[1];
            p99QueueToOutputMs = queueToOutput[2];
            p50OutputToRenderMs = outputToRender[0];
            p95OutputToRenderMs = outputToRender[1];
            p99OutputToRenderMs = outputToRender[2];
            p50QueueToRenderMs = queueToRender[0];
            p95QueueToRenderMs = queueToRender[1];
            p99QueueToRenderMs = queueToRender[2];
            queueToOutputSamples.clear();
            outputToRenderSamples.clear();
            queueToRenderSamples.clear();
        }
        VideoStats stats = new VideoStats(
            framesDecoded,
            renderedSnapshot,
            packetsReceived,
            decodeErrors,
            droppedFrames,
            reconnects,
            roughLatencyMs,
            decodeFps,
            renderFps,
            newFrameFps,
            repeatFrameFps,
            newFramesReceived,
            repeatFramesReceived,
            blackFramesReceived,
            keepaliveFramesReceived,
            lastFrameKind,
            lastSourceSeq,
            lastSourceAgeMs,
            lastReceiveToQueueMs,
            lastQueueToOutputMs,
            lastOutputToRenderMs,
            lastQueueToRenderMs,
            p50QueueToOutputMs,
            p95QueueToOutputMs,
            p99QueueToOutputMs,
            p50OutputToRenderMs,
            p95OutputToRenderMs,
            p99OutputToRenderMs,
            p50QueueToRenderMs,
            p95QueueToRenderMs,
            p99QueueToRenderMs,
            localPipelineLatencyMs,
            lastEncodeMs,
            latencyErrorBoundMs,
            state
        );
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onVideoStats(stats);
            }
        });
    }

    private void recordPacketKind(VideoPacket packet) {
        lastFrameKind = frameKindName(packet.frameKind);
        lastSourceSeq = packet.sourceSeq;
        lastSourceAgeMs = packet.sourceAgeMs;
        lastEncodeMs = packet.encodeMs;
        switch (packet.frameKind) {
            case FRAME_KIND_REPEAT:
                repeatFramesReceived += 1;
                break;
            case FRAME_KIND_BLACK:
                blackFramesReceived += 1;
                break;
            case FRAME_KIND_KEEPALIVE:
                keepaliveFramesReceived += 1;
                break;
            case FRAME_KIND_NEW:
            default:
                newFramesReceived += 1;
                break;
        }
    }

    private void updateRateSnapshot() {
        long nowNanos = System.nanoTime();
        if (lastRateSnapshotNanos == 0L) {
            lastRateSnapshotNanos = nowNanos;
            lastRateDecodedFrames = framesDecoded;
            synchronized (timingLock) {
                lastRateRenderedFrames = framesRendered;
            }
            lastRateNewFrames = newFramesReceived;
            lastRateRepeatFrames = repeatFramesReceived;
            return;
        }

        long elapsedNanos = nowNanos - lastRateSnapshotNanos;
        if (elapsedNanos < 500_000_000L) {
            return;
        }

        double seconds = elapsedNanos / 1_000_000_000.0;
        long rendered;
        synchronized (timingLock) {
            rendered = framesRendered;
        }
        long renderedDelta = rendered - lastRateRenderedFrames;
        decodeFps = (framesDecoded - lastRateDecodedFrames) / seconds;
        renderFps = renderedDelta / seconds;
        if (renderedDelta > 0L && instantRenderFps > 0.0) {
            renderFps = instantRenderFps;
        }
        newFrameFps = (newFramesReceived - lastRateNewFrames) / seconds;
        repeatFrameFps = (repeatFramesReceived - lastRateRepeatFrames) / seconds;
        lastRateSnapshotNanos = nowNanos;
        lastRateDecodedFrames = framesDecoded;
        lastRateRenderedFrames = rendered;
        lastRateNewFrames = newFramesReceived;
        lastRateRepeatFrames = repeatFramesReceived;
    }

    private static String frameKindName(int frameKind) {
        switch (frameKind) {
            case FRAME_KIND_REPEAT:
                return "repeat";
            case FRAME_KIND_BLACK:
                return "black";
            case FRAME_KIND_KEEPALIVE:
                return "keepalive";
            case FRAME_KIND_NEW:
            default:
                return "new";
        }
    }

    private void trimPacketTimingsLocked() {
        if (packetTimings.size() <= 240) {
            return;
        }

        Long oldestKey = null;
        long oldestNanos = Long.MAX_VALUE;
        for (Long key : packetTimings.keySet()) {
            PacketTiming timing = packetTimings.get(key);
            if (timing != null && timing.queueInputElapsedRealtimeNanos < oldestNanos) {
                oldestNanos = timing.queueInputElapsedRealtimeNanos;
                oldestKey = key;
            }
        }
        if (oldestKey != null) {
            packetTimings.remove(oldestKey);
        }
    }

    private static double nanosToMs(long nanos) {
        return Math.max(0.0, nanos / 1_000_000.0);
    }

    private long renderedLatencySinceHostTimestampMs(PacketTiming timing, long renderAtNanos) {
        long renderDeltaMs = Math.round(nanosToMs(renderAtNanos - timing.receiveElapsedRealtimeNanos));
        long renderedWallClockMs = timing.receiveWallClockMs + renderDeltaMs;
        long renderedOnHostClockMs = renderedWallClockMs + serverTimeOffsetMs;
        return Math.max(0L, renderedOnHostClockMs - timing.timestampMs);
    }

    private void updateInstantRenderFpsLocked(long renderAtNanos) {
        if (lastRenderedAtNanos > 0L && renderAtNanos > lastRenderedAtNanos) {
            double sampleFps = 1_000_000_000.0 / (renderAtNanos - lastRenderedAtNanos);
            if (!Double.isNaN(sampleFps) && !Double.isInfinite(sampleFps) && sampleFps > 0.0) {
                instantRenderFps = instantRenderFps <= 0.0
                    ? sampleFps
                    : (instantRenderFps * 0.75) + (sampleFps * 0.25);
            }
        }
        lastRenderedAtNanos = renderAtNanos;
    }

    private static void addSample(ArrayList<Double> samples, double value) {
        if (Double.isNaN(value) || Double.isInfinite(value)) {
            return;
        }
        samples.add(Math.max(0.0, value));
        if (samples.size() > 240) {
            samples.remove(0);
        }
    }

    private static double[] percentiles(List<Double> samples) {
        if (samples.isEmpty()) {
            return new double[] { 0.0, 0.0, 0.0 };
        }

        ArrayList<Double> sorted = new ArrayList<>(samples);
        Collections.sort(sorted);
        return new double[] {
            percentile(sorted, 0.50),
            percentile(sorted, 0.95),
            percentile(sorted, 0.99)
        };
    }

    private static double percentile(List<Double> sorted, double percentile) {
        if (sorted.isEmpty()) {
            return 0.0;
        }

        double position = percentile * (sorted.size() - 1);
        int lower = (int) Math.floor(position);
        int upper = (int) Math.ceil(position);
        if (lower == upper) {
            return sorted.get(lower);
        }

        double fraction = position - lower;
        return sorted.get(lower) + (sorted.get(upper) - sorted.get(lower)) * fraction;
    }

    private static final class CodecConfig {
        private byte[] sps;
        private byte[] pps;

        private boolean isReady() {
            return sps != null && pps != null;
        }

        private void scan(byte[] payload) {
            int offset = 0;
            while (offset < payload.length - 4) {
                int startCodeOffset = findStartCode(payload, offset);
                if (startCodeOffset < 0) {
                    return;
                }

                int nalOffset = startCodeOffset + startCodeLength(payload, startCodeOffset);
                if (nalOffset >= payload.length) {
                    return;
                }

                int nextStartCodeOffset = findStartCode(payload, nalOffset + 1);
                int endOffset = nextStartCodeOffset < 0 ? payload.length : nextStartCodeOffset;
                int nalType = payload[nalOffset] & 0x1F;
                if (nalType == 7 && sps == null) {
                    sps = copyRange(payload, startCodeOffset, endOffset);
                } else if (nalType == 8 && pps == null) {
                    pps = copyRange(payload, startCodeOffset, endOffset);
                }

                offset = endOffset;
            }
        }

        private static int findStartCode(byte[] data, int offset) {
            int index = offset;
            while (index < data.length - 3) {
                if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1) {
                    return index;
                }
                if (index < data.length - 4
                    && data[index] == 0
                    && data[index + 1] == 0
                    && data[index + 2] == 0
                    && data[index + 3] == 1) {
                    return index;
                }
                index += 1;
            }
            return -1;
        }

        private static int startCodeLength(byte[] data, int offset) {
            if (offset < data.length - 3 && data[offset] == 0 && data[offset + 1] == 0 && data[offset + 2] == 1) {
                return 3;
            }
            return 4;
        }

        private static byte[] copyRange(byte[] data, int start, int end) {
            byte[] copy = new byte[end - start];
            System.arraycopy(data, start, copy, 0, copy.length);
            return copy;
        }
    }

    private static final class VideoPacket {
        private final long seq;
        private final long timestampMs;
        private final byte[] payload;
        private final boolean isKeyFrame;
        private final int frameKind;
        private final long sourceSeq;
        private final int sourceAgeMs;
        private final double encodeMs;
        private final long receiveWallClockMs;
        private final long receiveElapsedRealtimeNanos;

        private VideoPacket(
            long seq,
            long timestampMs,
            byte[] payload,
            boolean isKeyFrame,
            int frameKind,
            long sourceSeq,
            int sourceAgeMs,
            double encodeMs,
            long receiveWallClockMs,
            long receiveElapsedRealtimeNanos
        ) {
            this.seq = seq;
            this.timestampMs = timestampMs;
            this.payload = payload;
            this.isKeyFrame = isKeyFrame;
            this.frameKind = frameKind;
            this.sourceSeq = sourceSeq;
            this.sourceAgeMs = sourceAgeMs;
            this.encodeMs = encodeMs;
            this.receiveWallClockMs = receiveWallClockMs;
            this.receiveElapsedRealtimeNanos = receiveElapsedRealtimeNanos;
        }
    }

    private static final class PacketTiming {
        private final long queueInputElapsedRealtimeNanos;
        private final long receiveElapsedRealtimeNanos;
        private final long receiveWallClockMs;
        private final long timestampMs;
        private long outputElapsedRealtimeNanos;

        private PacketTiming(VideoPacket packet, long queueInputElapsedRealtimeNanos) {
            this.queueInputElapsedRealtimeNanos = queueInputElapsedRealtimeNanos;
            this.receiveElapsedRealtimeNanos = packet.receiveElapsedRealtimeNanos;
            this.receiveWallClockMs = packet.receiveWallClockMs;
            this.timestampMs = packet.timestampMs;
        }
    }

    private static final class LatestVideoPacketQueue {
        private final ArrayDeque<VideoPacket> packets = new ArrayDeque<>();
        private final int capacity;
        private boolean closed;

        private LatestVideoPacketQueue(int capacity) {
            this.capacity = Math.max(1, capacity);
        }

        private int offerLatest(VideoPacket packet) {
            synchronized (this) {
                if (closed) {
                    return 0;
                }

                int dropped = 0;
                while (packets.size() >= capacity) {
                    packets.removeFirst();
                    dropped += 1;
                }
                packets.addLast(packet);
                notifyAll();
                return dropped;
            }
        }

        private VideoPacket take(long timeoutMs) throws InterruptedException {
            synchronized (this) {
                if (packets.isEmpty() && !closed) {
                    wait(timeoutMs);
                }
                return packets.isEmpty() ? null : packets.removeFirst();
            }
        }

        private boolean isClosed() {
            synchronized (this) {
                return closed;
            }
        }

        private void close() {
            synchronized (this) {
                closed = true;
                packets.clear();
                notifyAll();
            }
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
