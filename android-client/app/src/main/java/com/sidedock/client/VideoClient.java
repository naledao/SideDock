package com.sidedock.client;

import android.media.MediaCodec;
import android.media.MediaFormat;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.view.Surface;
import java.io.EOFException;
import java.io.InputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.util.ArrayDeque;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ThreadFactory;

public final class VideoClient {
    private static final String TAG = "SideDock.Video";

    public interface Listener {
        void onVideoState(String state);
        void onVideoLog(String message);
        void onVideoStats(VideoStats stats);
        void onVideoError(String code, String message);
    }

    public static final class VideoStats {
        public final long framesDecoded;
        public final long packetsReceived;
        public final long decodeErrors;
        public final long droppedFrames;
        public final long reconnects;
        public final long roughLatencyMs;
        public final String state;

        private VideoStats(
            long framesDecoded,
            long packetsReceived,
            long decodeErrors,
            long droppedFrames,
            long reconnects,
            long roughLatencyMs,
            String state
        ) {
            this.framesDecoded = framesDecoded;
            this.packetsReceived = packetsReceived;
            this.decodeErrors = decodeErrors;
            this.droppedFrames = droppedFrames;
            this.reconnects = reconnects;
            this.roughLatencyMs = roughLatencyMs;
            this.state = state;
        }
    }

    private static final int HEADER_SIZE = 24;
    private static final int MAX_PAYLOAD_LENGTH = 8 * 1024 * 1024;
    private static final long INPUT_TIMEOUT_US = 10000L;
    private static final byte[] MAGIC = new byte[] { 'S', 'D', 'K', 'V' };

    private final String host;
    private final Listener listener;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final Object lifecycleLock = new Object();

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
    private long packetsReceived;
    private long decodeErrors;
    private long droppedFrames;
    private long reconnects;
    private long roughLatencyMs;
    private long lastFrameStatsEmitAtMs;
    private volatile long serverTimeOffsetMs;

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

    public void setServerTimeOffsetMs(long offsetMs) {
        serverTimeOffsetMs = offsetMs;
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

    private boolean isCurrentGeneration(long runGeneration) {
        synchronized (lifecycleLock) {
            return generation == runGeneration;
        }
    }

    private void connectLoop(long runGeneration) {
        boolean firstAttempt = true;
        while (running && isCurrentGeneration(runGeneration) && surface != null && surface.isValid()) {
            emitState(firstAttempt ? "CONNECTING" : "RECONNECTING");
            firstAttempt = false;

            try {
                openDecodeRead();
            } catch (Exception ex) {
                if (running) {
                    decodeErrors += 1;
                    String message = ex.getMessage() == null ? ex.getClass().getSimpleName() : ex.getMessage();
                    Log.e(TAG, "video failed", ex);
                    emitError("VIDEO_FAILED", message);
                    emitLog("视频通道断开: " + message);
                }
            } finally {
                closeSocket();
                emitStats();
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

    private void openDecodeRead() throws Exception {
        Socket nextSocket = new Socket();
        nextSocket.setTcpNoDelay(true);
        nextSocket.connect(new InetSocketAddress(host, port), 3000);
        nextSocket.setSoTimeout(5000);
        socket = nextSocket;
        emitState("CONNECTED");
        emitLog("视频通道已连接 " + host + ":" + port);
        Log.i(TAG, "connected " + host + ":" + port);

        InputStream input = nextSocket.getInputStream();
        ArrayDeque<VideoPacket> pendingPackets = new ArrayDeque<>();
        CodecConfig codecConfig = new CodecConfig();
        MediaCodec codec = null;
        MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();
        long submittedPackets = 0L;
        long lastStatsAt = System.currentTimeMillis();

        try {
            while (running && !nextSocket.isClosed()) {
                VideoPacket packet = readPacket(input);
                packetsReceived += 1;
                roughLatencyMs = Math.max(0L, System.currentTimeMillis() - packet.timestampMs + serverTimeOffsetMs);
                codecConfig.scan(packet.payload);
                pendingPackets.add(packet);

                if (codec == null && (codecConfig.isReady() || pendingPackets.size() >= 30)) {
                    codec = createDecoder(codecConfig);
                    emitLog("MediaCodec 已启动，csd=" + codecConfig.isReady());
                    Log.i(TAG, "MediaCodec started csd=" + codecConfig.isReady());
                }

                while (codec != null && !pendingPackets.isEmpty()) {
                    submittedPackets += 1L;
                    queuePacket(codec, bufferInfo, pendingPackets.removeFirst(), submittedPackets);
                }

                long now = System.currentTimeMillis();
                if (now - lastStatsAt >= 2000L) {
                    emitStats();
                    lastStatsAt = now;
                }
            }
        } finally {
            if (codec != null) {
                try {
                    codec.stop();
                } catch (Exception ignored) {
                }
                codec.release();
            }
        }
    }

    private MediaCodec createDecoder(CodecConfig codecConfig) throws Exception {
        MediaFormat format = MediaFormat.createVideoFormat("video/avc", width, height);
        format.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
        format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, MAX_PAYLOAD_LENGTH);
        if (codecConfig.sps != null) {
            format.setByteBuffer("csd-0", ByteBuffer.wrap(codecConfig.sps));
        }
        if (codecConfig.pps != null) {
            format.setByteBuffer("csd-1", ByteBuffer.wrap(codecConfig.pps));
        }

        MediaCodec codec = MediaCodec.createDecoderByType("video/avc");
        codec.configure(format, surface, null, 0);
        codec.start();
        return codec;
    }

    private void queuePacket(
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
            return;
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
        codec.queueInputBuffer(inputIndex, 0, packet.payload.length, presentationTimeUs, 0);
        drainOutput(codec, bufferInfo, 0L);
    }

    private void drainOutput(MediaCodec codec, MediaCodec.BufferInfo bufferInfo, long timeoutUs) {
        while (running) {
            int outputIndex = codec.dequeueOutputBuffer(bufferInfo, timeoutUs);
            if (outputIndex >= 0) {
                boolean render = bufferInfo.size > 0;
                codec.releaseOutputBuffer(outputIndex, render);
                if (render) {
                    framesDecoded += 1;
                    long now = System.currentTimeMillis();
                    if (framesDecoded == 1L || now - lastFrameStatsEmitAtMs >= 1000L) {
                        lastFrameStatsEmitAtMs = now;
                        emitStats();
                    }
                }
                timeoutUs = 0L;
                continue;
            }
            if (outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                emitLog("输出格式: " + codec.getOutputFormat());
                timeoutUs = 0L;
                continue;
            }
            break;
        }
    }

    private VideoPacket readPacket(InputStream input) throws Exception {
        byte[] header = new byte[HEADER_SIZE];
        readFully(input, header, 0, header.length);
        if (header[0] != MAGIC[0] || header[1] != MAGIC[1] || header[2] != MAGIC[2] || header[3] != MAGIC[3]) {
            throw new IllegalStateException("video magic mismatch");
        }
        if ((header[4] & 0xFF) != 1) {
            throw new IllegalStateException("unsupported video packet version: " + (header[4] & 0xFF));
        }

        long seq = readUInt32Le(header, 8);
        long timestampMs = readInt64Le(header, 12);
        int length = readInt32Le(header, 20);
        if (length <= 0 || length > MAX_PAYLOAD_LENGTH) {
            throw new IllegalStateException("invalid video payload length: " + length);
        }

        byte[] payload = new byte[length];
        readFully(input, payload, 0, payload.length);
        return new VideoPacket(seq, timestampMs, payload);
    }

    private static void readFully(InputStream input, byte[] buffer, int offset, int length) throws Exception {
        int readTotal = 0;
        while (readTotal < length) {
            int read = input.read(buffer, offset + readTotal, length - readTotal);
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

    private void closeSocket() {
        try {
            if (socket != null) {
                socket.close();
            }
        } catch (Exception ignored) {
        } finally {
            socket = null;
        }
    }

    private void sleepQuietly(long delayMs) {
        try {
            Thread.sleep(delayMs);
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
        VideoStats stats = new VideoStats(
            framesDecoded,
            packetsReceived,
            decodeErrors,
            droppedFrames,
            reconnects,
            roughLatencyMs,
            state
        );
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onVideoStats(stats);
            }
        });
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

        private VideoPacket(long seq, long timestampMs, byte[] payload) {
            this.seq = seq;
            this.timestampMs = timestampMs;
            this.payload = payload;
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
