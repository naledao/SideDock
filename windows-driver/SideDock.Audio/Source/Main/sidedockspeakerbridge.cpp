/*++

Copyright (c) SideDock

Module Name:

    sidedockspeakerbridge.cpp

Abstract:

    Kernel-side writer for the SideDock speaker shared PCM ring.
--*/

#include <ntddk.h>
#include "definitions.h"
#include "sidedockspeakerbridge.h"

static PVOID g_SideDockSpeakerSectionObject = NULL;
static HANDLE g_SideDockSpeakerSectionHandle = NULL;
static PSIDEDOCK_SPEAKER_RING_HEADER g_SideDockSpeakerHeader = NULL;
static SIZE_T g_SideDockSpeakerViewSize = 0;
static FAST_MUTEX g_SideDockSpeakerMapLock;
static volatile LONG g_SideDockSpeakerMapLockReady = 0;

static VOID SideDockSpeakerBridgeInitializeHeader(_Inout_ PSIDEDOCK_SPEAKER_RING_HEADER Header)
{
    Header->Magic = SIDEDOCK_SPEAKER_RING_MAGIC;
    Header->Version = SIDEDOCK_SPEAKER_RING_VERSION;
    Header->HeaderBytes = SIDEDOCK_SPEAKER_RING_HEADER_BYTES;
    Header->BufferBytes = SIDEDOCK_SPEAKER_RING_BUFFER_BYTES;
    Header->SampleRate = SIDEDOCK_SPEAKER_SAMPLE_RATE;
    Header->Channels = SIDEDOCK_SPEAKER_CHANNELS;
    Header->BitsPerSample = SIDEDOCK_SPEAKER_BITS_PER_SAMPLE;
    Header->Flags = 0;
    Header->Reserved0 = 0;
    Header->WritePosition = 0;
    Header->LastWriteTimestampMs = 0;
    RtlZeroMemory(Header->Reserved, sizeof(Header->Reserved));
    RtlZeroMemory(((PUCHAR)Header) + SIDEDOCK_SPEAKER_RING_HEADER_BYTES, SIDEDOCK_SPEAKER_RING_BUFFER_BYTES);
}

static VOID SideDockSpeakerBridgeEnsureLock()
{
    if (InterlockedCompareExchange(&g_SideDockSpeakerMapLockReady, 1, 0) == 0)
    {
        ExInitializeFastMutex(&g_SideDockSpeakerMapLock);
    }
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS SideDockSpeakerBridgeEnsureMapped()
{
    NTSTATUS status = STATUS_SUCCESS;

    PAGED_CODE();
    SideDockSpeakerBridgeEnsureLock();
    ExAcquireFastMutex(&g_SideDockSpeakerMapLock);

    if (g_SideDockSpeakerHeader != NULL)
    {
        ExReleaseFastMutex(&g_SideDockSpeakerMapLock);
        return STATUS_SUCCESS;
    }

    UNICODE_STRING sectionName;
    RtlInitUnicodeString(&sectionName, L"\\BaseNamedObjects\\SideDockAudioSpeakerBuffer");

    UCHAR securityDescriptorBuffer[64];
    PSECURITY_DESCRIPTOR securityDescriptor = (PSECURITY_DESCRIPTOR)securityDescriptorBuffer;
    status = RtlCreateSecurityDescriptor(securityDescriptor, SECURITY_DESCRIPTOR_REVISION);
    if (!NT_SUCCESS(status))
    {
        ExReleaseFastMutex(&g_SideDockSpeakerMapLock);
        return status;
    }

    status = RtlSetDaclSecurityDescriptor(securityDescriptor, TRUE, NULL, FALSE);
    if (!NT_SUCCESS(status))
    {
        ExReleaseFastMutex(&g_SideDockSpeakerMapLock);
        return status;
    }

    OBJECT_ATTRIBUTES attributes;
    InitializeObjectAttributes(
        &attributes,
        &sectionName,
        OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE | OBJ_OPENIF,
        NULL,
        securityDescriptor);

    LARGE_INTEGER maximumSize;
    maximumSize.QuadPart = SIDEDOCK_SPEAKER_RING_HEADER_BYTES + SIDEDOCK_SPEAKER_RING_BUFFER_BYTES;

    status = ZwCreateSection(
        &g_SideDockSpeakerSectionHandle,
        SECTION_MAP_READ | SECTION_MAP_WRITE | SECTION_QUERY,
        &attributes,
        &maximumSize,
        PAGE_READWRITE,
        SEC_COMMIT,
        NULL);
    if (!NT_SUCCESS(status))
    {
        ExReleaseFastMutex(&g_SideDockSpeakerMapLock);
        return status;
    }

    status = ObReferenceObjectByHandle(
        g_SideDockSpeakerSectionHandle,
        SECTION_MAP_READ | SECTION_MAP_WRITE,
        NULL,
        KernelMode,
        &g_SideDockSpeakerSectionObject,
        NULL);
    if (!NT_SUCCESS(status))
    {
        ZwClose(g_SideDockSpeakerSectionHandle);
        g_SideDockSpeakerSectionHandle = NULL;
        ExReleaseFastMutex(&g_SideDockSpeakerMapLock);
        return status;
    }

    PVOID mappedBase = NULL;
    SIZE_T viewSize = 0;
    status = MmMapViewInSystemSpace(g_SideDockSpeakerSectionObject, &mappedBase, &viewSize);
    if (!NT_SUCCESS(status))
    {
        ObDereferenceObject(g_SideDockSpeakerSectionObject);
        g_SideDockSpeakerSectionObject = NULL;
        ZwClose(g_SideDockSpeakerSectionHandle);
        g_SideDockSpeakerSectionHandle = NULL;
        ExReleaseFastMutex(&g_SideDockSpeakerMapLock);
        return status;
    }

    g_SideDockSpeakerHeader = (PSIDEDOCK_SPEAKER_RING_HEADER)mappedBase;
    g_SideDockSpeakerViewSize = viewSize;
    if (g_SideDockSpeakerHeader->Magic != SIDEDOCK_SPEAKER_RING_MAGIC ||
        g_SideDockSpeakerHeader->Version != SIDEDOCK_SPEAKER_RING_VERSION ||
        g_SideDockSpeakerHeader->HeaderBytes != SIDEDOCK_SPEAKER_RING_HEADER_BYTES ||
        g_SideDockSpeakerHeader->BufferBytes != SIDEDOCK_SPEAKER_RING_BUFFER_BYTES ||
        g_SideDockSpeakerHeader->SampleRate != SIDEDOCK_SPEAKER_SAMPLE_RATE ||
        g_SideDockSpeakerHeader->Channels != SIDEDOCK_SPEAKER_CHANNELS ||
        g_SideDockSpeakerHeader->BitsPerSample != SIDEDOCK_SPEAKER_BITS_PER_SAMPLE)
    {
        SideDockSpeakerBridgeInitializeHeader(g_SideDockSpeakerHeader);
    }

    ExReleaseFastMutex(&g_SideDockSpeakerMapLock);
    return STATUS_SUCCESS;
}

VOID SideDockSpeakerBridgeRelease()
{
    SideDockSpeakerBridgeEnsureLock();
    ExAcquireFastMutex(&g_SideDockSpeakerMapLock);

    if (g_SideDockSpeakerHeader != NULL)
    {
        MmUnmapViewInSystemSpace(g_SideDockSpeakerHeader);
        g_SideDockSpeakerHeader = NULL;
        g_SideDockSpeakerViewSize = 0;
    }

    if (g_SideDockSpeakerSectionObject != NULL)
    {
        ObDereferenceObject(g_SideDockSpeakerSectionObject);
        g_SideDockSpeakerSectionObject = NULL;
    }

    if (g_SideDockSpeakerSectionHandle != NULL)
    {
        ZwClose(g_SideDockSpeakerSectionHandle);
        g_SideDockSpeakerSectionHandle = NULL;
    }

    ExReleaseFastMutex(&g_SideDockSpeakerMapLock);
}

VOID SideDockSpeakerBridgeWrite(
    _In_reads_bytes_(ByteCount) const UCHAR* Source,
    _In_ ULONG ByteCount
    )
{
    PSIDEDOCK_SPEAKER_RING_HEADER header = g_SideDockSpeakerHeader;
    if (header == NULL ||
        Source == NULL ||
        ByteCount == 0 ||
        header->Magic != SIDEDOCK_SPEAKER_RING_MAGIC ||
        header->Version != SIDEDOCK_SPEAKER_RING_VERSION ||
        header->BufferBytes != SIDEDOCK_SPEAKER_RING_BUFFER_BYTES ||
        g_SideDockSpeakerViewSize < SIDEDOCK_SPEAKER_RING_HEADER_BYTES + SIDEDOCK_SPEAKER_RING_BUFFER_BYTES)
    {
        return;
    }

    ULONG byteCount = min(ByteCount, SIDEDOCK_SPEAKER_RING_BUFFER_BYTES);
    ULONG sourceOffset = ByteCount - byteCount;
    PUCHAR ring = ((PUCHAR)header) + SIDEDOCK_SPEAKER_RING_HEADER_BYTES;
    ULONGLONG writePosition = (ULONGLONG)InterlockedCompareExchange64(&header->WritePosition, 0, 0);
    ULONG ringOffset = (ULONG)(writePosition % SIDEDOCK_SPEAKER_RING_BUFFER_BYTES);
    ULONG copied = 0;

    while (copied < byteCount)
    {
        ULONG chunk = min(byteCount - copied, SIDEDOCK_SPEAKER_RING_BUFFER_BYTES - ringOffset);
        RtlCopyMemory(ring + ringOffset, Source + sourceOffset + copied, chunk);
        copied += chunk;
        ringOffset = (ringOffset + chunk) % SIDEDOCK_SPEAKER_RING_BUFFER_BYTES;
    }

    KeMemoryBarrier();
    InterlockedExchange64(&header->WritePosition, writePosition + byteCount);
}
