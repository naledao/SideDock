package com.sidedock.client;

import android.content.Context;
import android.content.SharedPreferences;
import java.util.UUID;

public final class ConnectionProfile {
    public enum Mode {
        USB,
        LAN
    }

    private static final String PREFS_NAME = "connection_profile";
    private static final String KEY_MODE = "mode";
    private static final String KEY_HOST = "host";
    private static final String KEY_CONTROL_PORT = "control_port";
    private static final String KEY_DEVICE_ID = "device_id";
    private static final String KEY_HOST_ID = "host_id";
    private static final String KEY_FINGERPRINT = "certificate_fingerprint";
    private static final String KEY_PAIRING_TOKEN = "pairing_token";

    private final SharedPreferences preferences;
    private final String deviceId;
    private volatile Mode mode;
    private volatile String host;
    private volatile int controlPort;
    private volatile String pairingCode = "";
    private volatile String hostId;
    private volatile String certificateFingerprint;
    private volatile String pairingToken;
    private volatile String sessionToken = "";

    public ConnectionProfile(Context context) {
        preferences = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        mode = parseMode(preferences.getString(KEY_MODE, "usb"));
        host = normalizeHost(preferences.getString(KEY_HOST, "127.0.0.1"), mode);
        controlPort = normalizePort(preferences.getInt(KEY_CONTROL_PORT, 27183));
        hostId = safe(preferences.getString(KEY_HOST_ID, ""));
        certificateFingerprint = normalizeFingerprint(preferences.getString(KEY_FINGERPRINT, ""));
        pairingToken = safe(preferences.getString(KEY_PAIRING_TOKEN, ""));

        String storedDeviceId = safe(preferences.getString(KEY_DEVICE_ID, ""));
        if (storedDeviceId.isEmpty()) {
            storedDeviceId = UUID.randomUUID().toString();
            preferences.edit().putString(KEY_DEVICE_ID, storedDeviceId).apply();
        }
        deviceId = storedDeviceId;
    }

    public synchronized void configure(Mode nextMode, String nextHost, int nextControlPort, String nextPairingCode) {
        Mode effectiveMode = nextMode == null ? Mode.USB : nextMode;
        String effectiveHost = normalizeHost(nextHost, effectiveMode);
        int effectivePort = normalizePort(nextControlPort);
        boolean changedHost = mode != effectiveMode
            || !host.equalsIgnoreCase(effectiveHost)
            || controlPort != effectivePort;

        mode = effectiveMode;
        host = effectiveHost;
        controlPort = effectivePort;
        pairingCode = safe(nextPairingCode).replaceAll("\\s+", "");
        sessionToken = "";
        if (changedHost) {
            hostId = "";
            certificateFingerprint = "";
            pairingToken = "";
        }

        SharedPreferences.Editor editor = preferences.edit()
            .putString(KEY_MODE, mode == Mode.LAN ? "lan" : "usb")
            .putString(KEY_HOST, host)
            .putInt(KEY_CONTROL_PORT, controlPort);
        if (changedHost) {
            editor.remove(KEY_HOST_ID)
                .remove(KEY_FINGERPRINT)
                .remove(KEY_PAIRING_TOKEN);
        }
        editor.apply();
    }

    public synchronized void saveAuthenticatedSession(
        String nextHostId,
        String fingerprint,
        String nextPairingToken,
        String nextSessionToken
    ) {
        String normalizedFingerprint = normalizeFingerprint(fingerprint);
        if (!certificateFingerprint.isEmpty()
            && !certificateFingerprint.equals(normalizedFingerprint)) {
            throw new SecurityException("Windows host certificate changed. Remove the saved host and pair again.");
        }

        hostId = safe(nextHostId);
        certificateFingerprint = normalizedFingerprint;
        pairingToken = safe(nextPairingToken);
        sessionToken = safe(nextSessionToken);
        pairingCode = "";
        preferences.edit()
            .putString(KEY_HOST_ID, hostId)
            .putString(KEY_FINGERPRINT, certificateFingerprint)
            .putString(KEY_PAIRING_TOKEN, pairingToken)
            .apply();
    }

    public synchronized void clearPairing() {
        hostId = "";
        certificateFingerprint = "";
        pairingToken = "";
        sessionToken = "";
        preferences.edit()
            .remove(KEY_HOST_ID)
            .remove(KEY_FINGERPRINT)
            .remove(KEY_PAIRING_TOKEN)
            .apply();
    }

    public synchronized void setExpectedCertificateFingerprint(String fingerprint) {
        certificateFingerprint = normalizeFingerprint(fingerprint);
        if (certificateFingerprint.isEmpty()) {
            preferences.edit().remove(KEY_FINGERPRINT).apply();
        } else {
            preferences.edit().putString(KEY_FINGERPRINT, certificateFingerprint).apply();
        }
    }

    public Mode getMode() {
        return mode;
    }

    public boolean isLan() {
        return mode == Mode.LAN;
    }

    public String getHost() {
        return mode == Mode.LAN ? host : "127.0.0.1";
    }

    public int getControlPort() {
        return controlPort;
    }

    public String getDeviceId() {
        return deviceId;
    }

    public String getDeviceName() {
        return android.os.Build.MANUFACTURER + " " + android.os.Build.MODEL;
    }

    public String getHostId() {
        return hostId;
    }

    public String getPairingCode() {
        return pairingCode;
    }

    public String getCertificateFingerprint() {
        return certificateFingerprint;
    }

    public String getPairingToken() {
        return pairingToken;
    }

    public String getSessionToken() {
        return sessionToken;
    }

    public boolean hasPairing() {
        return !certificateFingerprint.isEmpty() && !pairingToken.isEmpty();
    }

    private static Mode parseMode(String value) {
        return "lan".equalsIgnoreCase(safe(value)) ? Mode.LAN : Mode.USB;
    }

    private static String normalizeHost(String value, Mode mode) {
        if (mode != Mode.LAN) {
            return "127.0.0.1";
        }
        String normalized = safe(value).trim();
        return normalized.isEmpty() ? "127.0.0.1" : normalized;
    }

    private static int normalizePort(int value) {
        return value > 0 && value <= 65535 ? value : 27183;
    }

    private static String normalizeFingerprint(String value) {
        return safe(value).replace(":", "").replace(" ", "").toUpperCase(java.util.Locale.ROOT);
    }

    private static String safe(String value) {
        return value == null ? "" : value.trim();
    }
}
