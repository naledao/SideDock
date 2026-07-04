/*++

Copyright (c) SideDock

Module Name:

    sidedockspeakerbridge.h

Abstract:

    Shared PCM ring used by the SideDock speaker render endpoint.
--*/

#ifndef _SIDEDOCK_SPEAKER_BRIDGE_H_
#define _SIDEDOCK_SPEAKER_BRIDGE_H_

#define SIDEDOCK_SPEAKER_RING_MAGIC          0x53414453UL // "SDAS" little-endian.
#define SIDEDOCK_SPEAKER_RING_VERSION        1
#define SIDEDOCK_SPEAKER_RING_HEADER_BYTES   64
#define SIDEDOCK_SPEAKER_RING_BUFFER_BYTES   (48000 * 2 * 2 * 4)
#define SIDEDOCK_SPEAKER_SAMPLE_RATE         48000
#define SIDEDOCK_SPEAKER_CHANNELS            2
#define SIDEDOCK_SPEAKER_BITS_PER_SAMPLE     16

typedef struct _SIDEDOCK_SPEAKER_RING_HEADER
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
    UCHAR Reserved[SIDEDOCK_SPEAKER_RING_HEADER_BYTES - 48];
} SIDEDOCK_SPEAKER_RING_HEADER, *PSIDEDOCK_SPEAKER_RING_HEADER;

NTSTATUS SideDockSpeakerBridgeEnsureMapped();

VOID SideDockSpeakerBridgeRelease();

VOID SideDockSpeakerBridgeWrite(
    _In_reads_bytes_(ByteCount) const UCHAR* Source,
    _In_ ULONG ByteCount
    );

#endif // _SIDEDOCK_SPEAKER_BRIDGE_H_
