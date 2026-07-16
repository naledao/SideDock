package com.sidedock.client;

import java.io.ByteArrayOutputStream;
import java.io.EOFException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.Locale;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

public final class SocketTransport {
    private static final int CONNECT_TIMEOUT_MS = 4000;
    private static final int HANDSHAKE_TIMEOUT_MS = 8000;
    private static final int MAX_LINE_BYTES = 16 * 1024;

    public static final class ConnectedSocket {
        public final Socket socket;
        public final String certificateFingerprint;

        private ConnectedSocket(Socket socket, String certificateFingerprint) {
            this.socket = socket;
            this.certificateFingerprint = certificateFingerprint;
        }
    }

    private SocketTransport() {
    }

    public static ConnectedSocket connectControl(ConnectionProfile profile) throws Exception {
        if (!profile.isLan()) {
            Socket socket = createPlainSocket(profile.getHost(), profile.getControlPort());
            return new ConnectedSocket(socket, "");
        }
        return createTlsSocket(profile, profile.getControlPort());
    }

    public static Socket connectData(ConnectionProfile profile, int port, String channel) throws Exception {
        if (!profile.isLan()) {
            return createPlainSocket(profile.getHost(), port);
        }
        if (profile.getSessionToken().isEmpty()) {
            throw new SecurityException("The encrypted control session is not authenticated.");
        }

        ConnectedSocket connected = createTlsSocket(profile, port);
        try {
            OutputStream output = connected.socket.getOutputStream();
            output.write(("SIDEDOCK/1 " + channel + " " + profile.getSessionToken() + "\n")
                .getBytes(StandardCharsets.UTF_8));
            output.flush();
            String response = readLine(connected.socket.getInputStream());
            if (!"OK".equals(response)) {
                throw new SecurityException("Windows host rejected the " + channel + " channel: " + response);
            }
            connected.socket.setSoTimeout(0);
            return connected.socket;
        } catch (Exception ex) {
            closeQuietly(connected.socket);
            throw ex;
        }
    }

    private static Socket createPlainSocket(String host, int port) throws Exception {
        Socket socket = new Socket();
        socket.setTcpNoDelay(true);
        socket.connect(new InetSocketAddress(host, port), CONNECT_TIMEOUT_MS);
        return socket;
    }

    private static ConnectedSocket createTlsSocket(ConnectionProfile profile, int port) throws Exception {
        TrustManager[] trustManagers = new TrustManager[] { new PairingTrustManager() };
        SSLContext context = SSLContext.getInstance("TLS");
        context.init(null, trustManagers, new SecureRandom());
        SSLSocket socket = (SSLSocket) context.getSocketFactory().createSocket();
        socket.setUseClientMode(true);
        socket.setTcpNoDelay(true);
        socket.setSoTimeout(HANDSHAKE_TIMEOUT_MS);
        socket.connect(new InetSocketAddress(profile.getHost(), port), CONNECT_TIMEOUT_MS);
        enableSupportedTlsProtocols(socket);
        socket.startHandshake();

        X509Certificate certificate = (X509Certificate) socket.getSession().getPeerCertificates()[0];
        String fingerprint = toHex(MessageDigest.getInstance("SHA-256").digest(certificate.getEncoded()));
        String expected = profile.getCertificateFingerprint();
        if (!expected.isEmpty() && !MessageDigest.isEqual(
            expected.getBytes(StandardCharsets.US_ASCII),
            fingerprint.getBytes(StandardCharsets.US_ASCII))) {
            closeQuietly(socket);
            throw new SecurityException("Windows host certificate does not match the saved pairing.");
        }
        return new ConnectedSocket(socket, fingerprint);
    }

    private static void enableSupportedTlsProtocols(SSLSocket socket) {
        ArrayList<String> enabled = new ArrayList<>();
        for (String protocol : socket.getSupportedProtocols()) {
            if ("TLSv1.3".equals(protocol) || "TLSv1.2".equals(protocol)) {
                enabled.add(protocol);
            }
        }
        socket.setEnabledProtocols(enabled.toArray(new String[0]));
    }

    private static String readLine(InputStream input) throws Exception {
        ByteArrayOutputStream buffer = new ByteArrayOutputStream();
        while (buffer.size() < MAX_LINE_BYTES) {
            int value = input.read();
            if (value < 0) {
                throw new EOFException("Connection closed during channel authentication.");
            }
            if (value == '\n') {
                String line = new String(buffer.toByteArray(), StandardCharsets.UTF_8);
                return line.endsWith("\r") ? line.substring(0, line.length() - 1) : line;
            }
            buffer.write(value);
        }
        throw new SecurityException("Authentication response is too large.");
    }

    private static String toHex(byte[] bytes) {
        StringBuilder builder = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) {
            builder.append(String.format(Locale.ROOT, "%02X", value & 0xFF));
        }
        return builder.toString();
    }

    private static void closeQuietly(Socket socket) {
        try {
            socket.close();
        } catch (Exception ignored) {
        }
    }

    private static final class PairingTrustManager implements X509TrustManager {
        @Override
        public void checkClientTrusted(X509Certificate[] chain, String authType) {
        }

        @Override
        public void checkServerTrusted(X509Certificate[] chain, String authType) {
            if (chain == null || chain.length == 0) {
                throw new IllegalArgumentException("Windows host did not provide a certificate.");
            }
        }

        @Override
        public X509Certificate[] getAcceptedIssuers() {
            return new X509Certificate[0];
        }
    }
}
