/*++

Copyright (c) SideDock

Module Name:

    sidedockmicbridge.cpp

Abstract:

    Kernel-side access to the SideDock microphone shared PCM ring.
--*/

#include <ntddk.h>
#include "definitions.h"
#include "sidedockmicbridge.h"

#define SIDEDOCKMICBRIDGE_POOLTAG 'BMSD'

static PVOID g_SideDockMicSectionObject = NULL;
static HANDLE g_SideDockMicSectionHandle = NULL;
static PSIDEDOCK_MIC_RING_HEADER g_SideDockMicHeader = NULL;
static SIZE_T g_SideDockMicViewSize = 0;
static FAST_MUTEX g_SideDockMicMapLock;
static volatile LONG g_SideDockMicMapLockReady = 0;

static VOID SideDockMicBridgeInitializeHeader(_Inout_ PSIDEDOCK_MIC_RING_HEADER Header)
{
    Header->Magic = SIDEDOCK_MIC_RING_MAGIC;
    Header->Version = SIDEDOCK_MIC_RING_VERSION;
    Header->HeaderBytes = SIDEDOCK_MIC_RING_HEADER_BYTES;
    Header->BufferBytes = SIDEDOCK_MIC_RING_BUFFER_BYTES;
    Header->SampleRate = SIDEDOCK_MIC_SAMPLE_RATE;
    Header->Channels = SIDEDOCK_MIC_CHANNELS;
    Header->BitsPerSample = SIDEDOCK_MIC_BITS_PER_SAMPLE;
    Header->Flags = 0;
    Header->Reserved0 = 0;
    Header->WritePosition = 0;
    Header->LastWriteTimestampMs = 0;
    RtlZeroMemory(Header->Reserved, sizeof(Header->Reserved));
    RtlZeroMemory(((PUCHAR)Header) + SIDEDOCK_MIC_RING_HEADER_BYTES, SIDEDOCK_MIC_RING_BUFFER_BYTES);
}

static VOID SideDockMicBridgeEnsureLock()
{
    if (InterlockedCompareExchange(&g_SideDockMicMapLockReady, 1, 0) == 0)
    {
        ExInitializeFastMutex(&g_SideDockMicMapLock);
    }
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS SideDockMicBridgeEnsureMapped()
{
    NTSTATUS status = STATUS_SUCCESS;

    PAGED_CODE();
    SideDockMicBridgeEnsureLock();
    ExAcquireFastMutex(&g_SideDockMicMapLock);

    if (g_SideDockMicHeader != NULL)
    {
        ExReleaseFastMutex(&g_SideDockMicMapLock);
        return STATUS_SUCCESS;
    }

    UNICODE_STRING sectionName;
    RtlInitUnicodeString(&sectionName, L"\\BaseNamedObjects\\SideDockAudioMicBuffer");

    UCHAR securityDescriptorBuffer[64];
    PSECURITY_DESCRIPTOR securityDescriptor = (PSECURITY_DESCRIPTOR)securityDescriptorBuffer;
    status = RtlCreateSecurityDescriptor(securityDescriptor, SECURITY_DESCRIPTOR_REVISION);
    if (!NT_SUCCESS(status))
    {
        ExReleaseFastMutex(&g_SideDockMicMapLock);
        return status;
    }

    status = RtlSetDaclSecurityDescriptor(securityDescriptor, TRUE, NULL, FALSE);
    if (!NT_SUCCESS(status))
    {
        ExReleaseFastMutex(&g_SideDockMicMapLock);
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
    maximumSize.QuadPart = SIDEDOCK_MIC_RING_HEADER_BYTES + SIDEDOCK_MIC_RING_BUFFER_BYTES;

    status = ZwCreateSection(
        &g_SideDockMicSectionHandle,
        SECTION_MAP_READ | SECTION_MAP_WRITE | SECTION_QUERY,
        &attributes,
        &maximumSize,
        PAGE_READWRITE,
        SEC_COMMIT,
        NULL);
    if (!NT_SUCCESS(status))
    {
        ExReleaseFastMutex(&g_SideDockMicMapLock);
        return status;
    }

    status = ObReferenceObjectByHandle(
        g_SideDockMicSectionHandle,
        SECTION_MAP_READ | SECTION_MAP_WRITE,
        NULL,
        KernelMode,
        &g_SideDockMicSectionObject,
        NULL);
    if (!NT_SUCCESS(status))
    {
        ZwClose(g_SideDockMicSectionHandle);
        g_SideDockMicSectionHandle = NULL;
        ExReleaseFastMutex(&g_SideDockMicMapLock);
        return status;
    }

    PVOID mappedBase = NULL;
    SIZE_T viewSize = 0;
    status = MmMapViewInSystemSpace(g_SideDockMicSectionObject, &mappedBase, &viewSize);
    if (!NT_SUCCESS(status))
    {
        ObDereferenceObject(g_SideDockMicSectionObject);
        g_SideDockMicSectionObject = NULL;
        ZwClose(g_SideDockMicSectionHandle);
        g_SideDockMicSectionHandle = NULL;
        ExReleaseFastMutex(&g_SideDockMicMapLock);
        return status;
    }

    g_SideDockMicHeader = (PSIDEDOCK_MIC_RING_HEADER)mappedBase;
    g_SideDockMicViewSize = viewSize;
    if (g_SideDockMicHeader->Magic != SIDEDOCK_MIC_RING_MAGIC ||
        g_SideDockMicHeader->Version != SIDEDOCK_MIC_RING_VERSION ||
        g_SideDockMicHeader->HeaderBytes != SIDEDOCK_MIC_RING_HEADER_BYTES ||
        g_SideDockMicHeader->BufferBytes != SIDEDOCK_MIC_RING_BUFFER_BYTES ||
        g_SideDockMicHeader->SampleRate != SIDEDOCK_MIC_SAMPLE_RATE ||
        g_SideDockMicHeader->Channels != SIDEDOCK_MIC_CHANNELS ||
        g_SideDockMicHeader->BitsPerSample != SIDEDOCK_MIC_BITS_PER_SAMPLE)
    {
        SideDockMicBridgeInitializeHeader(g_SideDockMicHeader);
    }

    ExReleaseFastMutex(&g_SideDockMicMapLock);
    return STATUS_SUCCESS;
}

VOID SideDockMicBridgeRelease()
{
    SideDockMicBridgeEnsureLock();
    ExAcquireFastMutex(&g_SideDockMicMapLock);

    if (g_SideDockMicHeader != NULL)
    {
        MmUnmapViewInSystemSpace(g_SideDockMicHeader);
        g_SideDockMicHeader = NULL;
        g_SideDockMicViewSize = 0;
    }

    if (g_SideDockMicSectionObject != NULL)
    {
        ObDereferenceObject(g_SideDockMicSectionObject);
        g_SideDockMicSectionObject = NULL;
    }

    if (g_SideDockMicSectionHandle != NULL)
    {
        ZwClose(g_SideDockMicSectionHandle);
        g_SideDockMicSectionHandle = NULL;
    }

    ExReleaseFastMutex(&g_SideDockMicMapLock);
}

VOID SideDockMicBridgeRead(
    _Inout_ ULONGLONG* ReadPosition,
    _Out_writes_bytes_(ByteCount) PUCHAR Destination,
    _In_ ULONG ByteCount
    )
{
    PSIDEDOCK_MIC_RING_HEADER header = g_SideDockMicHeader;
    if (header == NULL ||
        ReadPosition == NULL ||
        Destination == NULL ||
        ByteCount == 0 ||
        header->Magic != SIDEDOCK_MIC_RING_MAGIC ||
        header->Version != SIDEDOCK_MIC_RING_VERSION)
    {
        if (Destination != NULL && ByteCount > 0)
        {
            RtlZeroMemory(Destination, ByteCount);
        }
        return;
    }

    ULONG bufferBytes = header->BufferBytes;
    if (bufferBytes != SIDEDOCK_MIC_RING_BUFFER_BYTES ||
        g_SideDockMicViewSize < SIDEDOCK_MIC_RING_HEADER_BYTES + bufferBytes)
    {
        RtlZeroMemory(Destination, ByteCount);
        return;
    }

    PUCHAR ring = ((PUCHAR)header) + SIDEDOCK_MIC_RING_HEADER_BYTES;
    ULONGLONG writePosition = (ULONGLONG)InterlockedCompareExchange64(&header->WritePosition, 0, 0);
    ULONGLONG readPosition = *ReadPosition;

    if (readPosition == 0 || readPosition > writePosition)
    {
        readPosition = writePosition;
    }

    if (writePosition - readPosition > bufferBytes)
    {
        readPosition = writePosition - bufferBytes;
    }

    ULONG available = (ULONG)min(writePosition - readPosition, (ULONGLONG)ByteCount);
    ULONG copied = 0;
    while (copied < available)
    {
        ULONG ringOffset = (ULONG)(readPosition % bufferBytes);
        ULONG chunk = min(available - copied, bufferBytes - ringOffset);
        RtlCopyMemory(Destination + copied, ring + ringOffset, chunk);
        readPosition += chunk;
        copied += chunk;
    }

    if (copied < ByteCount)
    {
        RtlZeroMemory(Destination + copied, ByteCount - copied);
    }

    *ReadPosition = readPosition;
}
