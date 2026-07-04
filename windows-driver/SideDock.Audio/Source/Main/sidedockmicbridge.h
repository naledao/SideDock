/*++

Copyright (c) SideDock

Module Name:

    sidedockmicbridge.h

Abstract:

    Shared PCM ring used by the SideDock microphone capture endpoint.
--*/

#ifndef _SIDEDOCK_MIC_BRIDGE_H_
#define _SIDEDOCK_MIC_BRIDGE_H_

#define SIDEDOCK_MIC_RING_MAGIC          0x4D414453UL // "SDAM" little-endian.
#define SIDEDOCK_MIC_RING_VERSION        1
#define SIDEDOCK_MIC_RING_HEADER_BYTES   64
#define SIDEDOCK_MIC_RING_BUFFER_BYTES   (48000 * 2 * 4)
#define SIDEDOCK_MIC_SAMPLE_RATE         48000
#define SIDEDOCK_MIC_CHANNELS            1
#define SIDEDOCK_MIC_BITS_PER_SAMPLE     16

typedef struct _SIDEDOCK_MIC_RING_HEADER
{
    ULONG Magic;
    ULONG Version;
    ULONG HeaderBytes;
    ULONG BufferBytes;
    ULONG SampleRate;
    USHORT Channels;
    USHORT BitsPerSample;
    volatile LONG Flags;
    LONG Reserved0;
    volatile LONG64 WritePosition;
    volatile LONG64 LastWriteTimestampMs;
    UCHAR Reserved[SIDEDOCK_MIC_RING_HEADER_BYTES - 48];
} SIDEDOCK_MIC_RING_HEADER, *PSIDEDOCK_MIC_RING_HEADER;

NTSTATUS SideDockMicBridgeEnsureMapped();

VOID SideDockMicBridgeRelease();

VOID SideDockMicBridgeRead(
    _Inout_ ULONGLONG* ReadPosition,
    _Out_writes_bytes_(ByteCount) PUCHAR Destination,
    _In_ ULONG ByteCount
    );

#endif // _SIDEDOCK_MIC_BRIDGE_H_
