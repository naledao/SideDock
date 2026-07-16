package com.sidedock.client;

import android.os.Handler;
import android.os.Looper;
import org.json.JSONObject;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.SocketTimeoutException;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class LanDiscoveryClient {
    private static final int DISCOVERY_PORT = 27182;
    private static final int MAX_PACKET_BYTES = 8192;

    public interface Listener {
        void onHostFound(DiscoveredHost host);
        void onDiscoveryError(String message);
    }

    public static final class DiscoveredHost {
        public final String hostId;
        public final String hostName;
        public final String address;
        public final int controlPort;
        public final String certificateFingerprint;
        public final long lastSeenAtMs;

        private DiscoveredHost(
            String hostId,
            String hostName,
            String address,
            int controlPort,
            String certificateFingerprint,
            long lastSeenAtMs
        ) {
            this.hostId = hostId;
            this.hostName = hostName;
            this.address = address;
            this.controlPort = controlPort;
            this.certificateFingerprint = certificateFingerprint;
            this.lastSeenAtMs = lastSeenAtMs;
        }

        public String displayName() {
            return hostName + "  " + address + ":" + controlPort;
        }
    }

    private final Listener listener;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final ExecutorService executor = Executors.newSingleThreadExecutor();
    private final Map<String, DiscoveredHost> hosts = new LinkedHashMap<>();
    private volatile boolean running;
    private DatagramSocket socket;

    public LanDiscoveryClient(Listener listener) {
        this.listener = listener;
    }

    public synchronized void start() {
        if (running) {
            sendQuery();
            return;
        }
        running = true;
        executor.execute(new Runnable() {
            @Override
            public void run() {
                runDiscovery();
            }
        });
    }

    public synchronized void stop() {
        running = false;
        if (socket != null) {
            socket.close();
            socket = null;
        }
    }

    public synchronized void shutdown() {
        stop();
        executor.shutdownNow();
    }

    public synchronized DiscoveredHost[] snapshot() {
        return hosts.values().toArray(new DiscoveredHost[0]);
    }

    public void sendQuery() {
        executor.execute(new Runnable() {
            @Override
            public void run() {
                DatagramSocket active = socket;
                if (active == null || active.isClosed()) {
                    return;
                }
                try {
                    byte[] payload = new JSONObject()
                        .put("service", "SideDock")
                        .put("query", true)
                        .toString()
                        .getBytes(StandardCharsets.UTF_8);
                    DatagramPacket packet = new DatagramPacket(
                        payload,
                        payload.length,
                        InetAddress.getByName("255.255.255.255"),
                        DISCOVERY_PORT);
                    active.send(packet);
                } catch (Exception ex) {
                    emitError("设备发现请求失败：" + safeMessage(ex));
                }
            }
        });
    }

    private void runDiscovery() {
        try {
            DatagramSocket nextSocket = new DatagramSocket(null);
            nextSocket.setReuseAddress(true);
            nextSocket.setBroadcast(true);
            nextSocket.bind(new InetSocketAddress(DISCOVERY_PORT));
            nextSocket.setSoTimeout(1000);
            synchronized (this) {
                if (!running) {
                    nextSocket.close();
                    return;
                }
                socket = nextSocket;
            }
            sendQueryDirect(nextSocket);

            byte[] buffer = new byte[MAX_PACKET_BYTES];
            while (running && !nextSocket.isClosed()) {
                DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
                try {
                    nextSocket.receive(packet);
                } catch (SocketTimeoutException ex) {
                    continue;
                }

                JSONObject json = new JSONObject(new String(
                    packet.getData(),
                    packet.getOffset(),
                    packet.getLength(),
                    StandardCharsets.UTF_8));
                if (!"SideDock".equalsIgnoreCase(json.optString("service", ""))
                    || json.optBoolean("query", false)) {
                    continue;
                }

                DiscoveredHost host = new DiscoveredHost(
                    json.optString("hostId", ""),
                    json.optString("hostName", "SideDock Host"),
                    packet.getAddress().getHostAddress(),
                    json.optInt("controlPort", 27183),
                    json.optString("certificateFingerprint", ""),
                    System.currentTimeMillis());
                synchronized (this) {
                    hosts.put(host.hostId.isEmpty() ? host.address : host.hostId, host);
                }
                mainHandler.post(new Runnable() {
                    @Override
                    public void run() {
                        listener.onHostFound(host);
                    }
                });
            }
        } catch (Exception ex) {
            if (running) {
                emitError("设备发现不可用：" + safeMessage(ex));
            }
        } finally {
            synchronized (this) {
                if (socket != null) {
                    socket.close();
                    socket = null;
                }
            }
        }
    }

    private static void sendQueryDirect(DatagramSocket socket) throws Exception {
        byte[] payload = "{\"service\":\"SideDock\",\"query\":true}".getBytes(StandardCharsets.UTF_8);
        socket.send(new DatagramPacket(
            payload,
            payload.length,
            InetAddress.getByName("255.255.255.255"),
            DISCOVERY_PORT));
    }

    private void emitError(String message) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                listener.onDiscoveryError(message);
            }
        });
    }

    private static String safeMessage(Exception ex) {
        return ex.getMessage() == null ? ex.getClass().getSimpleName() : ex.getMessage();
    }
}
