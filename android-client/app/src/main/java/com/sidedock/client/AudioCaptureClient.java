package com.sidedock.client;

import android.Manifest;
import android.content.Context;
import android.content.pm.PackageManager;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioRecord;
import android.media.AudioTrack;
import android.media.MediaRecorder;
import android.os.Process;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.Arrays;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

public final class AudioCaptureClient {
    public interface Listener {
        void onAudioCaptureState(String state, String message);
        void onAudioCaptureStats(long packetsSent, long bytesSent, int peakSample, long silentPackets, String audioSourceName);
        void onAudioPlaybackState(String state, String message);
        void onAudioPlaybackStats(long packetsReceived, long bytesReceived, int peakSample, long sourceAgeMs, int playState);
        void onAudioTestStatus(AudioTestStatus status);
    }

    public static final class AudioTestStatus {
        public final String testId;
        public final String kind;
        public final String status;
        public final boolean ok;
        public final String phase;
        public final String message;
        public final long startedAtMs;
        public final long completedAtMs;
        public final long packetsSent;
        public final long bytesSent;
        public final long packetsReceived;
        public final long bytesReceived;
        public final int peakSample;
        public final int peakLevelPercent;
        public final long silentPackets;
        public final double silentRatio;
        public final boolean permissionGranted;
        public final boolean muted;
        public final boolean stopped;
        public final int playState;
        public final long writeErrors;
        public final String error;

        private AudioTestStatus(AudioTestSession session, String status, boolean ok, String phase, String message, String error) {
            this.testId = session.testId;
            this.kind = session.kind;
            this.status = status;
            this.ok = ok;
            this.phase = phase;
            this.message = message;
            this.startedAtMs = session.startedAtMs;
            this.completedAtMs = session.completedAtMs > 0L ? session.completedAtMs : System.currentTimeMillis();
            this.packetsSent = session.packetsSent;
            this.bytesSent = session.bytesSent;
            this.packetsReceived = session.packetsReceived;
            this.bytesReceived = session.bytesReceived;
            this.peakSample = session.peakSample;
            this.peakLevelPercent = peakSampleToPercent(session.peakSample);
            this.silentPackets = session.silentPackets;
            long packetCount = "recording".equals(session.kind) ? session.packetsSent : session.packetsReceived;
            this.silentRatio = packetCount <= 0L ? 0.0d : session.silentPackets / (double) packetCount;
            this.permissionGranted = session.permissionGranted;
            this.muted = session.muted;
            this.stopped = session.stopped;
            this.playState = session.playState;
            this.writeErrors = session.writeErrors;
            this.error = error == null ? "" : error;
        }
    }

    private static final int HEADER_SIZE = 36;
    private static final int VERSION = 1;
    private static final int BITS_PER_SAMPLE = 16;
    private static final int MAX_SPEAKER_PAYLOAD_BYTES = 48000 * 2 * 2;
    private static final int SILENCE_PEAK_THRESHOLD = 128;
    private static final byte[] MIC_MAGIC = new byte[] { 'S', 'D', 'A', 'M' };
    private static final byte[] SPEAKER_MAGIC = new byte[] { 'S', 'D', 'A', 'S' };

    private final Context context;
    private final Listener listener;
    private final Object lifecycleLock = new Object();
    private final Object testLock = new Object();
    private final ScheduledExecutorService testExecutor = Executors.newSingleThreadScheduledExecutor(new NamedThreadFactory("SideDock-AudioTest"));

    private ExecutorService executor;
    private Socket socket;
    private volatile boolean running;
    private long generation;
    private int port;
    private int sampleRate = 48000;
    private int microphoneChannels = 1;
    private int speakerChannels = 2;
    private boolean microphoneActive;
    private boolean speakerEnabled;
    private boolean speakerMuted;
    private AudioTestSession activePlaybackTest;
    private AudioTestSession activeRecordingTest;

    public AudioCaptureClient(Context context, Listener listener) {
        this.context = context.getApplicationContext();
        this.listener = listener;
    }

    public void start(
        int nextPort,
        int nextSampleRate,
        int nextMicrophoneChannels,
        int nextSpeakerChannels,
        boolean nextMicrophoneActive,
        boolean nextSpeakerEnabled,
        boolean nextSpeakerMuted
    ) {
        synchronized (lifecycleLock) {
            int normalizedSampleRate = nextSampleRate > 0 ? nextSampleRate : 48000;
            int normalizedMicrophoneChannels = Math.max(1, nextMicrophoneChannels);
            int normalizedSpeakerChannels = Math.max(1, nextSpeakerChannels);
            if (running
                && port == nextPort
                && sampleRate == normalizedSampleRate
                && microphoneChannels == normalizedMicrophoneChannels
                && speakerChannels == normalizedSpeakerChannels
                && microphoneActive == nextMicrophoneActive
                && speakerEnabled == nextSpeakerEnabled
                && speakerMuted == nextSpeakerMuted) {
                return;
            }

            stopLocked();
            port = nextPort;
            sampleRate = normalizedSampleRate;
            microphoneChannels = normalizedMicrophoneChannels;
            speakerChannels = normalizedSpeakerChannels;
            microphoneActive = nextMicrophoneActive;
            speakerEnabled = nextSpeakerEnabled;
            speakerMuted = nextSpeakerMuted;
            running = true;
            final long runGeneration = ++generation;
            executor = Executors.newFixedThreadPool(3, new NamedThreadFactory("SideDock-Audio"));
            executor.execute(new Runnable() {
                @Override
                public void run() {
                    audioLoop(runGeneration);
                }
            });
        }
    }

    public void stop() {
        synchronized (lifecycleLock) {
            stopLocked();
        }
        failActiveTests("audio_stopped", "Audio was stopped before the test completed.");
    }

    public void shutdown() {
        stop();
        testExecutor.shutdownNow();
    }

    public boolean isRunning() {
        return running;
    }

    public void startAudioTest(String testId, String kind, int durationMs, int timeoutMs) {
        String normalizedTestId = testId == null ? "" : testId.trim();
        String normalizedKind = kind == null ? "" : kind.trim().toLowerCase();
        if (normalizedTestId.isEmpty()
            || (!"playback".equals(normalizedKind) && !"recording".equals(normalizedKind))) {
            emitImmediateTestStatus(
                normalizedTestId,
                normalizedKind,
                "failed",
                false,
                "rejected",
                "Invalid audio test request.",
                "invalid_request");
            return;
        }

        int safeDurationMs = Math.max(500, Math.min(durationMs, 4000));
        int safeTimeoutMs = Math.max(safeDurationMs + 1000, Math.min(Math.max(timeoutMs, 3000), 12000));
        AudioTestSession session;
        synchronized (testLock) {
            if ("playback".equals(normalizedKind) && activePlaybackTest != null) {
                emitImmediateTestStatus(normalizedTestId, normalizedKind, "failed", false, "rejected", "A playback test is already running.", "busy");
                return;
            }
            if ("recording".equals(normalizedKind) && activeRecordingTest != null) {
                emitImmediateTestStatus(normalizedTestId, normalizedKind, "failed", false, "rejected", "A recording test is already running.", "busy");
                return;
            }

            boolean permissionGranted = context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED;
            if ("recording".equals(normalizedKind) && (!running || !microphoneActive || !permissionGranted)) {
                emitImmediateTestStatus(
                    normalizedTestId,
                    normalizedKind,
                    "failed",
                    false,
                    "preflight",
                    permissionGranted ? "Android microphone capture is not active." : "Android microphone permission is missing.",
                    permissionGranted ? "microphone_inactive" : "permission_missing");
                return;
            }

            if ("playback".equals(normalizedKind) && (!running || !speakerEnabled || speakerMuted)) {
                emitImmediateTestStatus(
                    normalizedTestId,
                    normalizedKind,
                    "failed",
                    false,
                    "preflight",
                    speakerMuted ? "Android speaker playback is muted." : "Android speaker playback is not active.",
                    speakerMuted ? "speaker_muted" : "speaker_inactive");
                return;
            }

            session = new AudioTestSession(
                normalizedTestId,
                normalizedKind,
                safeDurationMs,
                safeTimeoutMs,
                permissionGranted,
                "playback".equals(normalizedKind) ? speakerMuted : false,
                !running);
            if ("playback".equals(normalizedKind)) {
                activePlaybackTest = session;
            } else {
                activeRecordingTest = session;
            }
        }

        emitTestStatus(session, "running", false, "running", "Audio test is running.", "");
        testExecutor.schedule(new Runnable() {
            @Override
            public void run() {
                finishAudioTest(normalizedTestId, normalizedKind, false);
            }
        }, safeDurationMs, TimeUnit.MILLISECONDS);
        testExecutor.schedule(new Runnable() {
            @Override
            public void run() {
                finishAudioTest(normalizedTestId, normalizedKind, true);
            }
        }, safeTimeoutMs, TimeUnit.MILLISECONDS);
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

    private void audioLoop(long runGeneration) {
        Process.setThreadPriority(Process.THREAD_PRIORITY_AUDIO);
        long reconnectDelayMs = 300L;

        while (running && isCurrentGeneration(runGeneration)) {
            if (!microphoneActive && !speakerEnabled) {
                emitCaptureState("disabled", "麦克风已关闭。");
                emitPlaybackState("disabled", "音响已关闭。");
                return;
            }

            boolean canCapture = microphoneActive
                && context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED;
            if (microphoneActive && !canCapture) {
                emitCaptureState("authorization_required", "需要允许麦克风权限。");
                if (!speakerEnabled) {
                    return;
                }
            }

            AudioRecord recorder = null;
            AudioRecordSource recorderSource = null;
            Socket nextSocket = null;
            Future<?> captureFuture = null;
            Future<?> playbackFuture = null;
            AtomicReference<WorkerFailure> failure = new AtomicReference<>();

            try {
                emitCaptureState(canCapture ? "preparing" : "disabled", canCapture ? "正在准备麦克风采集。" : "麦克风未启用。");
                emitPlaybackState(speakerEnabled ? (speakerMuted ? "muted" : "preparing") : "disabled",
                    speakerEnabled ? (speakerMuted ? "本机音响已静音。" : "正在准备音响播放。") : "音响已关闭。");

                nextSocket = new Socket();
                nextSocket.setTcpNoDelay(true);
                nextSocket.connect(new InetSocketAddress("127.0.0.1", port), 3000);
                synchronized (lifecycleLock) {
                    if (!running || generation != runGeneration) {
                        closeQuietly(nextSocket);
                        return;
                    }
                    socket = nextSocket;
                }

                final Socket activeSocket = nextSocket;
                String socketMessage = "socket connected " + socketSummary(activeSocket) + "; " + generationDetails(runGeneration);
                if (canCapture) {
                    emitCaptureState("preparing", socketMessage);
                }
                if (speakerEnabled) {
                    emitPlaybackState(speakerMuted ? "muted" : "preparing", socketMessage);
                }

                if (canCapture) {
                    recorderSource = createAudioRecord();
                    recorder = recorderSource.recorder;
                    recorder.startRecording();
                    final AudioRecord activeRecorder = recorder;
                    final String activeAudioSourceName = recorderSource.audioSourceName;
                    captureFuture = executor.submit(new Runnable() {
                        @Override
                        public void run() {
                            try {
                                emitCaptureState("capturing", "麦克风正在采集中。");
                                writeCaptureStream(activeRecorder, activeSocket.getOutputStream(), runGeneration, activeAudioSourceName);
                            } catch (Exception ex) {
                                String message = "capture worker exception: " + exceptionSummary(ex) + "; " + generationDetails(runGeneration);
                                emitCaptureState("unavailable", message);
                                failure.compareAndSet(null, new WorkerFailure("capture", ex));
                                closeQuietly(activeSocket);
                            }
                        }
                    });
                }

                if (speakerEnabled) {
                    playbackFuture = executor.submit(new Runnable() {
                        @Override
                        public void run() {
                            try {
                                readPlaybackStream(activeSocket.getInputStream(), runGeneration);
                            } catch (Exception ex) {
                                String message = "playback worker exception: " + exceptionSummary(ex) + "; " + generationDetails(runGeneration);
                                emitPlaybackState("unavailable", message);
                                failure.compareAndSet(null, new WorkerFailure("playback", ex));
                                closeQuietly(activeSocket);
                            }
                        }
                    });
                }

                while (running && isCurrentGeneration(runGeneration)) {
                    WorkerFailure workerFailure = failure.get();
                    if (workerFailure != null) {
                        throw new IllegalStateException(
                            workerFailure.worker + " worker exception: " + exceptionSummary(workerFailure.exception)
                                + "; " + generationDetails(runGeneration),
                            workerFailure.exception);
                    }

                    if (captureFuture != null && captureFuture.isDone()) {
                        throw new IllegalStateException("capture worker stopped without exception; " + generationDetails(runGeneration));
                    }
                    if (playbackFuture != null && playbackFuture.isDone()) {
                        throw new IllegalStateException("playback worker stopped without exception; " + generationDetails(runGeneration));
                    }

                    sleepQuietly(50L);
                }
            } catch (Exception ex) {
                if (running && isCurrentGeneration(runGeneration)) {
                    String message = "audioLoop exception: " + exceptionSummary(ex) + "; " + generationDetails(runGeneration);
                    emitCaptureState(canCapture ? "unavailable" : "disabled", message);
                    emitPlaybackState(speakerEnabled ? "unavailable" : "disabled", message);
                    sleepQuietly(reconnectDelayMs);
                    reconnectDelayMs = Math.min(reconnectDelayMs * 2L, 3000L);
                }
            } finally {
                if (captureFuture != null) {
                    captureFuture.cancel(true);
                }
                if (playbackFuture != null) {
                    playbackFuture.cancel(true);
                }
                if (recorder != null) {
                    stopRecorderQuietly(recorder);
                    recorder.release();
                }
                closeSocket(nextSocket);
            }
        }
    }

    private void writeCaptureStream(AudioRecord recorder, OutputStream output, long runGeneration, String audioSourceName) throws Exception {
        int bytesPerSample = BITS_PER_SAMPLE / 8;
        int frameBytes = microphoneChannels * bytesPerSample;
        int targetChunkBytes = Math.max(frameBytes, (sampleRate / 50) * frameBytes);
        int minBufferBytes = AudioRecord.getMinBufferSize(
            sampleRate,
            microphoneChannels == 1 ? AudioFormat.CHANNEL_IN_MONO : AudioFormat.CHANNEL_IN_STEREO,
            AudioFormat.ENCODING_PCM_16BIT);
        byte[] pcm = new byte[Math.max(targetChunkBytes, minBufferBytes)];
        byte[] header = new byte[HEADER_SIZE];
        long sequence = 0L;
        long packetsSent = 0L;
        long bytesSent = 0L;
        long silentPackets = 0L;
        int peakSample = 0;
        long lastStatsAtMs = 0L;

        while (running && isCurrentGeneration(runGeneration)) {
            int read = recorder.read(pcm, 0, targetChunkBytes);
            if (read <= 0) {
                throw new IllegalStateException("AudioRecord read failed: " + read + "; " + generationDetails(runGeneration));
            }

            int packetPeakSample = calculatePeakSample(pcm, read);
            peakSample = Math.max(peakSample, packetPeakSample);
            if (packetPeakSample <= SILENCE_PEAK_THRESHOLD) {
                silentPackets += 1L;
            }

            sequence += 1L;
            writeHeader(header, MIC_MAGIC, sequence, System.currentTimeMillis(), microphoneChannels, read);
            try {
                output.write(header);
            } catch (Exception ex) {
                throw phaseException("write capture header", ex, runGeneration);
            }
            try {
                output.write(pcm, 0, read);
            } catch (Exception ex) {
                throw phaseException("write capture payload", ex, runGeneration);
            }
            try {
                output.flush();
            } catch (Exception ex) {
                throw phaseException("flush capture stream", ex, runGeneration);
            }

            packetsSent += 1L;
            bytesSent += read;
            recordCaptureTestPacket(read, packetPeakSample, audioSourceName);
            long now = System.currentTimeMillis();
            if (now - lastStatsAtMs >= 1000L) {
                lastStatsAtMs = now;
                emitCaptureStats(packetsSent, bytesSent, peakSample, silentPackets, audioSourceName);
            }
        }
    }

    private void readPlaybackStream(InputStream input, long runGeneration) throws Exception {
        int channelConfig = speakerChannels == 1 ? AudioFormat.CHANNEL_OUT_MONO : AudioFormat.CHANNEL_OUT_STEREO;
        int minBufferBytes = AudioTrack.getMinBufferSize(sampleRate, channelConfig, AudioFormat.ENCODING_PCM_16BIT);
        if (minBufferBytes <= 0) {
            throw new IllegalStateException("Unsupported speaker format; " + generationDetails(runGeneration));
        }

        int playbackBufferBytes = Math.max(minBufferBytes * 2, (sampleRate / 5) * speakerChannels * (BITS_PER_SAMPLE / 8));
        AudioTrack track = new AudioTrack(
            AudioManager.STREAM_MUSIC,
            sampleRate,
            channelConfig,
            AudioFormat.ENCODING_PCM_16BIT,
            playbackBufferBytes,
            AudioTrack.MODE_STREAM);

        byte[] header = new byte[HEADER_SIZE];
        byte[] pcm = new byte[Math.max(MAX_SPEAKER_PAYLOAD_BYTES, playbackBufferBytes)];
        long packetsReceived = 0L;
        long bytesReceived = 0L;
        long lastStatsAtMs = 0L;
        int peakSample = 0;
        long lastSourceAgeMs = 0L;
        boolean playbackStarted = false;

        try {
            emitPlaybackState(speakerMuted ? "muted" : "available",
                speakerMuted ? "本机音响已静音。" : "音响可用，等待电脑声音。");

            while (running && isCurrentGeneration(runGeneration)) {
                readExactly(input, header, 0, HEADER_SIZE, "read speaker header", runGeneration);
                validateHeader(header, SPEAKER_MAGIC, "speaker");
                long timestampMs = readLong(header, 16);
                int receivedSampleRate = readInt(header, 24);
                int receivedChannels = readShort(header, 28);
                int receivedBits = readShort(header, 30);
                int payloadLength = readInt(header, 32);

                if (payloadLength <= 0 || payloadLength > pcm.length) {
                    throw new IllegalStateException("Invalid speaker payload length: " + payloadLength + "; " + generationDetails(runGeneration));
                }
                if (receivedSampleRate != sampleRate
                    || receivedChannels != speakerChannels
                    || receivedBits != BITS_PER_SAMPLE) {
                    throw new IllegalStateException("Unsupported speaker format: "
                        + receivedSampleRate + "/" + receivedChannels + "/" + receivedBits + "; " + generationDetails(runGeneration));
                }

                readExactly(input, pcm, 0, payloadLength, "read speaker payload", runGeneration);
                packetsReceived += 1L;
                bytesReceived += payloadLength;
                peakSample = Math.max(peakSample, calculatePeakSample(pcm, payloadLength));

                if (!speakerMuted) {
                    if (!playbackStarted) {
                        track.play();
                        playbackStarted = true;
                    }
                    try {
                        writeTrack(track, pcm, payloadLength, runGeneration);
                        recordPlaybackTestPacket(payloadLength, calculatePeakSample(pcm, payloadLength), track.getPlayState());
                    } catch (Exception ex) {
                        failPlaybackTest("audio_track_write_failed", "AudioTrack write failed: " + exceptionSummary(ex));
                        throw ex;
                    }
                } else if (playbackStarted) {
                    track.pause();
                    track.flush();
                    playbackStarted = false;
                }

                long now = System.currentTimeMillis();
                lastSourceAgeMs = Math.max(0L, now - timestampMs);
                if (packetsReceived == 1L || now - lastStatsAtMs >= 1000L) {
                    lastStatsAtMs = now;
                    emitPlaybackState(speakerMuted ? "muted" : "playing",
                        speakerMuted ? "本机音响已静音。" : "正在播放电脑声音。");
                    emitPlaybackStats(packetsReceived, bytesReceived, peakSample, lastSourceAgeMs, track.getPlayState());
                }
            }
        } finally {
            try {
                track.stop();
            } catch (Exception ignored) {
            }
            track.release();
        }
    }

    private AudioRecordSource createAudioRecord() {
        int channelConfig = microphoneChannels == 1 ? AudioFormat.CHANNEL_IN_MONO : AudioFormat.CHANNEL_IN_STEREO;
        int minBufferBytes = AudioRecord.getMinBufferSize(sampleRate, channelConfig, AudioFormat.ENCODING_PCM_16BIT);
        if (minBufferBytes <= 0) {
            throw new IllegalStateException("Unsupported microphone format.");
        }

        int bufferBytes = Math.max(minBufferBytes * 2, (sampleRate / 10) * microphoneChannels * (BITS_PER_SAMPLE / 8));
        int[] audioSources = new int[] {
            MediaRecorder.AudioSource.MIC,
            MediaRecorder.AudioSource.UNPROCESSED,
            MediaRecorder.AudioSource.VOICE_COMMUNICATION
        };
        RuntimeException lastFailure = null;
        for (int audioSource : audioSources) {
            AudioRecord recorder = null;
            try {
                recorder = new AudioRecord(
                    audioSource,
                    sampleRate,
                    channelConfig,
                    AudioFormat.ENCODING_PCM_16BIT,
                    bufferBytes);
                if (recorder.getState() == AudioRecord.STATE_INITIALIZED) {
                    return new AudioRecordSource(recorder, audioSourceName(audioSource));
                }

                lastFailure = new IllegalStateException("AudioRecord source " + audioSourceName(audioSource) + " was not initialized.");
            } catch (RuntimeException ex) {
                lastFailure = ex;
            }

            if (recorder != null) {
                recorder.release();
            }
        }

        throw new IllegalStateException(
            "AudioRecord initialization failed for MIC, UNPROCESSED and VOICE_COMMUNICATION.",
            lastFailure);
    }

    private void recordCaptureTestPacket(int byteCount, int peakSample, String audioSourceName) {
        synchronized (testLock) {
            AudioTestSession session = activeRecordingTest;
            if (session == null) {
                return;
            }

            session.packetsSent += 1L;
            session.bytesSent += Math.max(0, byteCount);
            session.peakSample = Math.max(session.peakSample, peakSample);
            session.audioSourceName = audioSourceName == null ? "" : audioSourceName;
            if (peakSample <= SILENCE_PEAK_THRESHOLD) {
                session.silentPackets += 1L;
            }
        }
    }

    private void recordPlaybackTestPacket(int byteCount, int peakSample, int playState) {
        synchronized (testLock) {
            AudioTestSession session = activePlaybackTest;
            if (session == null) {
                return;
            }

            session.packetsReceived += 1L;
            session.bytesReceived += Math.max(0, byteCount);
            session.peakSample = Math.max(session.peakSample, peakSample);
            session.playState = playState;
        }
    }

    private void finishAudioTest(String testId, String kind, boolean timeout) {
        AudioTestSession session;
        synchronized (testLock) {
            session = "playback".equals(kind) ? activePlaybackTest : activeRecordingTest;
            if (session == null || !session.testId.equals(testId) || session.completed) {
                return;
            }

            if (!timeout && session.completedAtMs > 0L) {
                return;
            }

            session.completed = true;
            session.completedAtMs = System.currentTimeMillis();
            if ("playback".equals(kind)) {
                activePlaybackTest = null;
            } else {
                activeRecordingTest = null;
            }
        }

        AudioTestStatus status = evaluateTestSession(session, timeout);
        listener.onAudioTestStatus(status);
    }

    private AudioTestStatus evaluateTestSession(AudioTestSession session, boolean timeout) {
        if (timeout) {
            return new AudioTestStatus(session, "failed", false, "timeout", "Audio test timed out on Android.", "timeout");
        }

        if ("playback".equals(session.kind)) {
            if (session.muted) {
                return new AudioTestStatus(session, "failed", false, "preflight", "Android speaker playback is muted.", "speaker_muted");
            }
            if (session.writeErrors > 0L) {
                return new AudioTestStatus(session, "failed", false, "playback", "AudioTrack write failed during playback test.", "audio_track_write_failed");
            }
            if (session.packetsReceived <= 0L || session.bytesReceived <= 0L) {
                return new AudioTestStatus(session, "failed", false, "playback", "Android did not receive playback test packets.", "no_playback_packets");
            }

            return new AudioTestStatus(session, "passed", true, "playback", "Android received playback packets and wrote them to AudioTrack.", "");
        }

        if (!session.permissionGranted) {
            return new AudioTestStatus(session, "failed", false, "preflight", "Android microphone permission is missing.", "permission_missing");
        }
        if (session.packetsSent <= 0L || session.bytesSent <= 0L) {
            return new AudioTestStatus(session, "failed", false, "recording", "Android did not capture microphone packets.", "no_recording_packets");
        }

        double silentRatio = session.packetsSent <= 0L ? 1.0d : session.silentPackets / (double) session.packetsSent;
        if (peakSampleToPercent(session.peakSample) <= 1 || silentRatio >= 0.95d) {
            return new AudioTestStatus(session, "passed_silent", true, "recording", "Microphone link is active, but input level is silent or very low.", "");
        }

        return new AudioTestStatus(session, "passed", true, "recording", "Android captured and sent microphone packets.", "");
    }

    private void failPlaybackTest(String error, String message) {
        AudioTestSession session;
        synchronized (testLock) {
            session = activePlaybackTest;
            if (session == null || session.completed) {
                return;
            }

            session.writeErrors += 1L;
            session.completed = true;
            session.completedAtMs = System.currentTimeMillis();
            activePlaybackTest = null;
        }

        listener.onAudioTestStatus(new AudioTestStatus(session, "failed", false, "playback", message, error));
    }

    private void failActiveTests(String error, String message) {
        AudioTestSession playback;
        AudioTestSession recording;
        synchronized (testLock) {
            playback = activePlaybackTest;
            recording = activeRecordingTest;
            activePlaybackTest = null;
            activeRecordingTest = null;
            long now = System.currentTimeMillis();
            if (playback != null) {
                playback.completed = true;
                playback.completedAtMs = now;
            }
            if (recording != null) {
                recording.completed = true;
                recording.completedAtMs = now;
            }
        }

        if (playback != null) {
            listener.onAudioTestStatus(new AudioTestStatus(playback, "failed", false, "stopped", message, error));
        }
        if (recording != null) {
            listener.onAudioTestStatus(new AudioTestStatus(recording, "failed", false, "stopped", message, error));
        }
    }

    private void emitTestStatus(AudioTestSession session, String status, boolean ok, String phase, String message, String error) {
        listener.onAudioTestStatus(new AudioTestStatus(session, status, ok, phase, message, error));
    }

    private void emitImmediateTestStatus(
        String testId,
        String kind,
        String status,
        boolean ok,
        String phase,
        String message,
        String error
    ) {
        AudioTestSession session = new AudioTestSession(
            testId == null ? "" : testId,
            kind == null ? "" : kind,
            0,
            0,
            context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED,
            speakerMuted,
            !running);
        session.completed = true;
        session.completedAtMs = System.currentTimeMillis();
        listener.onAudioTestStatus(new AudioTestStatus(session, status, ok, phase, message, error));
    }

    private static int calculatePeakSample(byte[] pcm, int byteCount) {
        int peak = 0;
        int end = byteCount - (byteCount % 2);
        for (int offset = 0; offset < end; offset += 2) {
            int low = pcm[offset] & 0xFF;
            int high = pcm[offset + 1];
            int sample = (short) ((high << 8) | low);
            int abs = sample == Short.MIN_VALUE ? Short.MAX_VALUE : Math.abs(sample);
            if (abs > peak) {
                peak = abs;
            }
        }

        return peak;
    }

    private static int peakSampleToPercent(int peakSample) {
        int normalized = Math.max(0, Math.min(Short.MAX_VALUE, peakSample));
        return Math.max(0, Math.min(100, (int) Math.round((normalized * 100.0d) / Short.MAX_VALUE)));
    }

    private static String audioSourceName(int audioSource) {
        switch (audioSource) {
            case MediaRecorder.AudioSource.MIC:
                return "MIC";
            case MediaRecorder.AudioSource.UNPROCESSED:
                return "UNPROCESSED";
            case MediaRecorder.AudioSource.VOICE_COMMUNICATION:
                return "VOICE_COMMUNICATION";
            default:
                return "source-" + audioSource;
        }
    }

    private void writeHeader(byte[] header, byte[] magic, long sequence, long timestampMs, int channels, int payloadLength) {
        ByteBuffer buffer = ByteBuffer.wrap(header).order(ByteOrder.LITTLE_ENDIAN);
        buffer.put(magic);
        buffer.putInt(VERSION);
        buffer.putLong(sequence);
        buffer.putLong(timestampMs);
        buffer.putInt(sampleRate);
        buffer.putShort((short) channels);
        buffer.putShort((short) BITS_PER_SAMPLE);
        buffer.putInt(payloadLength);
    }

    private static void validateHeader(byte[] header, byte[] magic, String direction) {
        if (!Arrays.equals(Arrays.copyOfRange(header, 0, 4), magic)) {
            throw new IllegalStateException("Invalid " + direction + " packet magic.");
        }
        int version = readInt(header, 4);
        if (version != VERSION) {
            throw new IllegalStateException("Unsupported " + direction + " packet version: " + version);
        }
    }

    private static int readInt(byte[] buffer, int offset) {
        return ByteBuffer.wrap(buffer, offset, 4).order(ByteOrder.LITTLE_ENDIAN).getInt();
    }

    private static long readLong(byte[] buffer, int offset) {
        return ByteBuffer.wrap(buffer, offset, 8).order(ByteOrder.LITTLE_ENDIAN).getLong();
    }

    private static int readShort(byte[] buffer, int offset) {
        return ByteBuffer.wrap(buffer, offset, 2).order(ByteOrder.LITTLE_ENDIAN).getShort() & 0xFFFF;
    }

    private void readExactly(
        InputStream input,
        byte[] buffer,
        int offset,
        int length,
        String phase,
        long runGeneration
    ) throws Exception {
        int readTotal = 0;
        while (readTotal < length) {
            int read;
            try {
                read = input.read(buffer, offset + readTotal, length - readTotal);
            } catch (Exception ex) {
                throw phaseException(phase, ex, runGeneration);
            }
            if (read < 0) {
                throw new IllegalStateException(phase + " EOF: Audio stream closed; " + generationDetails(runGeneration));
            }
            readTotal += read;
        }
    }

    private void writeTrack(AudioTrack track, byte[] pcm, int byteCount, long runGeneration) {
        int offset = 0;
        while (offset < byteCount) {
            int written;
            try {
                written = track.write(pcm, offset, byteCount - offset);
            } catch (Exception ex) {
                throw phaseException("write playback AudioTrack", ex, runGeneration);
            }
            if (written <= 0) {
                throw new IllegalStateException("AudioTrack write failed: " + written + "; " + generationDetails(runGeneration));
            }
            offset += written;
        }
    }

    private boolean isCurrentGeneration(long runGeneration) {
        synchronized (lifecycleLock) {
            return generation == runGeneration;
        }
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

    private static void stopRecorderQuietly(AudioRecord recorder) {
        try {
            recorder.stop();
        } catch (Exception ignored) {
        }
    }

    private RuntimeException phaseException(String phase, Exception ex, long runGeneration) {
        return new IllegalStateException(
            phase + " failed: " + exceptionSummary(ex) + "; " + generationDetails(runGeneration),
            ex);
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

    private static String socketSummary(Socket socket) {
        if (socket == null) {
            return "socket=null";
        }

        try {
            return "local=" + socket.getLocalSocketAddress()
                + " remote=" + socket.getRemoteSocketAddress()
                + " connected=" + socket.isConnected()
                + " closed=" + socket.isClosed();
        } catch (Exception ex) {
            return "socketSummaryError=" + exceptionSummary(ex);
        }
    }

    private static void sleepQuietly(long delayMs) {
        try {
            Thread.sleep(delayMs);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
        }
    }

    private void emitCaptureState(String state, String message) {
        listener.onAudioCaptureState(state, message);
    }

    private void emitCaptureStats(long packetsSent, long bytesSent, int peakSample, long silentPackets, String audioSourceName) {
        listener.onAudioCaptureStats(packetsSent, bytesSent, peakSample, silentPackets, audioSourceName);
    }

    private void emitPlaybackState(String state, String message) {
        listener.onAudioPlaybackState(state, message);
    }

    private void emitPlaybackStats(long packetsReceived, long bytesReceived, int peakSample, long sourceAgeMs, int playState) {
        listener.onAudioPlaybackStats(packetsReceived, bytesReceived, peakSample, sourceAgeMs, playState);
    }

    private static final class WorkerFailure {
        private final String worker;
        private final Exception exception;

        private WorkerFailure(String worker, Exception exception) {
            this.worker = worker;
            this.exception = exception;
        }
    }

    private static final class AudioTestSession {
        private final String testId;
        private final String kind;
        private final int durationMs;
        private final int timeoutMs;
        private final long startedAtMs;
        private final boolean permissionGranted;
        private final boolean muted;
        private final boolean stopped;
        private long completedAtMs;
        private boolean completed;
        private long packetsSent;
        private long bytesSent;
        private long packetsReceived;
        private long bytesReceived;
        private int peakSample;
        private long silentPackets;
        private int playState;
        private long writeErrors;
        private String audioSourceName = "";

        private AudioTestSession(
            String testId,
            String kind,
            int durationMs,
            int timeoutMs,
            boolean permissionGranted,
            boolean muted,
            boolean stopped
        ) {
            this.testId = testId;
            this.kind = kind;
            this.durationMs = durationMs;
            this.timeoutMs = timeoutMs;
            this.permissionGranted = permissionGranted;
            this.muted = muted;
            this.stopped = stopped;
            this.startedAtMs = System.currentTimeMillis();
        }
    }

    private static final class AudioRecordSource {
        private final AudioRecord recorder;
        private final String audioSourceName;

        private AudioRecordSource(AudioRecord recorder, String audioSourceName) {
            this.recorder = recorder;
            this.audioSourceName = audioSourceName;
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
