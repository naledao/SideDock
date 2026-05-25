package com.sidedock.client;

import org.json.JSONObject;

public final class ProtocolMessage {
    public final int v;
    public final String type;
    public final long seq;
    public final long ts;
    public final JSONObject payload;

    public ProtocolMessage(int v, String type, long seq, long ts, JSONObject payload) {
        this.v = v;
        this.type = type;
        this.seq = seq;
        this.ts = ts;
        this.payload = payload == null ? new JSONObject() : payload;
    }

    public String toJsonLine() {
        try {
            return new JSONObject()
                .put("v", v)
                .put("type", type)
                .put("seq", seq)
                .put("ts", ts)
                .put("payload", payload)
                .toString();
        } catch (Exception ex) {
            return "{\"v\":1,\"type\":\"log\",\"seq\":0,\"ts\":0,\"payload\":{\"error\":\"json encode failed\"}}";
        }
    }

    public static ProtocolMessage fromJsonLine(String line) throws Exception {
        JSONObject json = new JSONObject(line);
        JSONObject payload = json.optJSONObject("payload");
        return new ProtocolMessage(
            json.optInt("v", 1),
            json.getString("type"),
            json.optLong("seq", 0L),
            json.optLong("ts", 0L),
            payload == null ? new JSONObject() : payload
        );
    }
}
