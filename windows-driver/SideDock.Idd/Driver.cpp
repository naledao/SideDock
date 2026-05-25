/*++

Copyright (c) SideDock contributors.

Abstract:

    Minimal UMDF + IddCx indirect display driver for the SideDock prototype.
    The driver reports a small fixed mode list and exports IddCx swap-chain
    frames through shared memory for the Windows host process.

Environment:

    User Mode, UMDF

--*/

#include "Driver.h"
#include "Driver.tmh"

#include <iterator>

using namespace Microsoft::WRL;
using namespace SideDock::Idd;

extern "C" DRIVER_INITIALIZE DriverEntry;

EVT_WDF_DRIVER_DEVICE_ADD SideDockDeviceAdd;
EVT_WDF_OBJECT_CONTEXT_CLEANUP SideDockDriverContextCleanup;
EVT_WDF_DEVICE_D0_ENTRY SideDockDeviceD0Entry;

EVT_IDD_CX_ADAPTER_INIT_FINISHED SideDockAdapterInitFinished;
EVT_IDD_CX_ADAPTER_COMMIT_MODES SideDockAdapterCommitModes;
EVT_IDD_CX_PARSE_MONITOR_DESCRIPTION SideDockParseMonitorDescription;
EVT_IDD_CX_MONITOR_GET_DEFAULT_DESCRIPTION_MODES SideDockMonitorGetDefaultModes;
EVT_IDD_CX_MONITOR_QUERY_TARGET_MODES SideDockMonitorQueryTargetModes;
EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN SideDockMonitorAssignSwapChain;
EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN SideDockMonitorUnassignSwapChain;

static PDRIVER_OBJECT g_driverObject = nullptr;

static constexpr LPCWSTR SharedFrameBufferName = L"Global\\SideDockFrameBuffer";
static constexpr LPCWSTR SharedFrameReadyName = L"Global\\SideDockFrameReady";
static constexpr LPCWSTR SharedFrameConsumerAliveName = L"Global\\SideDockFrameConsumerAlive";
static constexpr UINT MaxSharedFrameStride = MaxVirtualDisplayWidth * 4;
static constexpr UINT MaxSharedFrameSlotSize = MaxSharedFrameStride * MaxVirtualDisplayHeight;
static constexpr UINT SharedFrameMappingSize =
    sizeof(SharedFrameHeader) + SharedFrameSlotCount * (sizeof(SharedFrameSlotHeader) + MaxSharedFrameSlotSize);

struct DeviceContextWrapper
{
    DeviceContext* Context = nullptr;

    void Cleanup()
    {
        delete Context;
        Context = nullptr;
    }
};

struct MonitorContextWrapper
{
    MonitorContext* Context = nullptr;

    void Cleanup()
    {
        delete Context;
        Context = nullptr;
    }
};

WDF_DECLARE_CONTEXT_TYPE(DeviceContextWrapper);
WDF_DECLARE_CONTEXT_TYPE(MonitorContextWrapper);

extern "C" BOOL WINAPI DllMain(
    _In_ HINSTANCE hInstance,
    _In_ UINT reason,
    _In_opt_ LPVOID reserved)
{
    UNREFERENCED_PARAMETER(hInstance);
    UNREFERENCED_PARAMETER(reason);
    UNREFERENCED_PARAMETER(reserved);
    return TRUE;
}

static void FillSignalInfo(
    DISPLAYCONFIG_VIDEO_SIGNAL_INFO& mode,
    DWORD width,
    DWORD height,
    DWORD refreshRate,
    bool monitorMode)
{
    mode.totalSize.cx = mode.activeSize.cx = width;
    mode.totalSize.cy = mode.activeSize.cy = height;

    mode.AdditionalSignalInfo.vSyncFreqDivider = monitorMode ? 0 : 1;
    mode.AdditionalSignalInfo.videoStandard = 255;

    mode.vSyncFreq.Numerator = refreshRate;
    mode.vSyncFreq.Denominator = 1;
    mode.hSyncFreq.Numerator = refreshRate * height;
    mode.hSyncFreq.Denominator = 1;
    mode.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
    mode.pixelRate = static_cast<UINT64>(refreshRate) * width * height;
}

static IDDCX_MONITOR_MODE CreateMonitorMode(
    DWORD width,
    DWORD height,
    DWORD refreshRate,
    IDDCX_MONITOR_MODE_ORIGIN origin)
{
    IDDCX_MONITOR_MODE mode = {};
    mode.Size = sizeof(mode);
    mode.Origin = origin;
    FillSignalInfo(mode.MonitorVideoSignalInfo, width, height, refreshRate, true);
    return mode;
}

static IDDCX_TARGET_MODE CreateTargetMode(DWORD width, DWORD height, DWORD refreshRate)
{
    IDDCX_TARGET_MODE mode = {};
    mode.Size = sizeof(mode);
    FillSignalInfo(mode.TargetVideoSignalInfo.targetVideoSignalInfo, width, height, refreshRate, false);
    return mode;
}

_Use_decl_annotations_
extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT driverObject, PUNICODE_STRING registryPath)
{
    g_driverObject = driverObject;
    WPP_INIT_TRACING(driverObject, registryPath);
    TraceEvents(TRACE_LEVEL_INFORMATION, TRACE_DRIVER, "%!FUNC! DriverEntry");

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.EvtCleanupCallback = SideDockDriverContextCleanup;

    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, SideDockDeviceAdd);

    NTSTATUS status = WdfDriverCreate(driverObject, registryPath, &attributes, &config, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status))
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_DRIVER, "%!FUNC! WdfDriverCreate failed: 0x%08x", status);
        WPP_CLEANUP(driverObject);
    }

    return status;
}

_Use_decl_annotations_
void SideDockDriverContextCleanup(WDFOBJECT driverObject)
{
    UNREFERENCED_PARAMETER(driverObject);
    TraceEvents(TRACE_LEVEL_INFORMATION, TRACE_DRIVER, "%!FUNC! DriverUnload");
    WPP_CLEANUP(g_driverObject);
}

_Use_decl_annotations_
NTSTATUS SideDockDeviceAdd(WDFDRIVER driver, PWDFDEVICE_INIT deviceInit)
{
    UNREFERENCED_PARAMETER(driver);
    TraceEvents(TRACE_LEVEL_INFORMATION, TRACE_DEVICE, "%!FUNC! DeviceAdd");

    WDF_PNPPOWER_EVENT_CALLBACKS pnpPowerCallbacks;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnpPowerCallbacks);
    pnpPowerCallbacks.EvtDeviceD0Entry = SideDockDeviceD0Entry;
    WdfDeviceInitSetPnpPowerEventCallbacks(deviceInit, &pnpPowerCallbacks);

    IDD_CX_CLIENT_CONFIG iddConfig;
    IDD_CX_CLIENT_CONFIG_INIT(&iddConfig);
    iddConfig.EvtIddCxAdapterInitFinished = SideDockAdapterInitFinished;
    iddConfig.EvtIddCxAdapterCommitModes = SideDockAdapterCommitModes;
    iddConfig.EvtIddCxParseMonitorDescription = SideDockParseMonitorDescription;
    iddConfig.EvtIddCxMonitorGetDefaultDescriptionModes = SideDockMonitorGetDefaultModes;
    iddConfig.EvtIddCxMonitorQueryTargetModes = SideDockMonitorQueryTargetModes;
    iddConfig.EvtIddCxMonitorAssignSwapChain = SideDockMonitorAssignSwapChain;
    iddConfig.EvtIddCxMonitorUnassignSwapChain = SideDockMonitorUnassignSwapChain;

    NTSTATUS status = IddCxDeviceInitConfig(deviceInit, &iddConfig);
    if (!NT_SUCCESS(status))
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_DEVICE, "%!FUNC! IddCxDeviceInitConfig failed: 0x%08x", status);
        return status;
    }

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DeviceContextWrapper);
    attributes.EvtCleanupCallback = [](WDFOBJECT object)
    {
        if (auto* wrapper = WdfObjectGet_DeviceContextWrapper(object))
        {
            wrapper->Cleanup();
        }
    };

    WDFDEVICE device = nullptr;
    status = WdfDeviceCreate(&deviceInit, &attributes, &device);
    if (!NT_SUCCESS(status))
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_DEVICE, "%!FUNC! WdfDeviceCreate failed: 0x%08x", status);
        return status;
    }

    status = IddCxDeviceInitialize(device);
    if (!NT_SUCCESS(status))
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_DEVICE, "%!FUNC! IddCxDeviceInitialize failed: 0x%08x", status);
        return status;
    }

    auto* wrapper = WdfObjectGet_DeviceContextWrapper(device);
    wrapper->Context = new DeviceContext(device);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS SideDockDeviceD0Entry(WDFDEVICE device, WDF_POWER_DEVICE_STATE previousState)
{
    UNREFERENCED_PARAMETER(previousState);
    TraceEvents(TRACE_LEVEL_INFORMATION, TRACE_DEVICE, "%!FUNC! DeviceD0Entry");

    auto* wrapper = WdfObjectGet_DeviceContextWrapper(device);
    wrapper->Context->InitAdapter();
    return STATUS_SUCCESS;
}

Direct3DDevice::Direct3DDevice(LUID adapterLuid) :
    AdapterLuid(adapterLuid)
{
}

HRESULT Direct3DDevice::Init()
{
    HRESULT hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&DxgiFactory));
    if (FAILED(hr))
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_SWAPCHAIN, "%!FUNC! CreateDXGIFactory2 failed: 0x%08x", hr);
        return hr;
    }

    hr = DxgiFactory->EnumAdapterByLuid(AdapterLuid, IID_PPV_ARGS(&Adapter));
    if (FAILED(hr))
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_SWAPCHAIN, "%!FUNC! EnumAdapterByLuid failed: 0x%08x", hr);
        return hr;
    }

    hr = D3D11CreateDevice(
        Adapter.Get(),
        D3D_DRIVER_TYPE_UNKNOWN,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        &Device,
        nullptr,
        &DeviceContext);

    if (FAILED(hr))
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_SWAPCHAIN, "%!FUNC! D3D11CreateDevice failed: 0x%08x", hr);
        return hr;
    }

    return S_OK;
}

SharedFrameBuffer::~SharedFrameBuffer()
{
    if (m_view)
    {
        UnmapViewOfFile(m_view);
        m_view = nullptr;
    }

    if (m_consumerAliveEvent)
    {
        CloseHandle(m_consumerAliveEvent);
        m_consumerAliveEvent = nullptr;
    }

    if (m_frameReadyEvent)
    {
        CloseHandle(m_frameReadyEvent);
        m_frameReadyEvent = nullptr;
    }

    if (m_mapping)
    {
        CloseHandle(m_mapping);
        m_mapping = nullptr;
    }
}

bool SharedFrameBuffer::EnsureInitialized()
{
    if (m_view)
    {
        return true;
    }

    SECURITY_ATTRIBUTES securityAttributes = {};
    SECURITY_DESCRIPTOR securityDescriptor = {};
    PACL securityAcl = nullptr;
    SECURITY_ATTRIBUTES* securityAttributesPointer = nullptr;
    if (CreateSecurityAttributes(securityAttributes, securityDescriptor, securityAcl))
    {
        securityAttributesPointer = &securityAttributes;
    }
    else
    {
        TraceEvents(TRACE_LEVEL_WARNING, TRACE_SWAPCHAIN, "%!FUNC! security descriptor creation failed: %lu", GetLastError());
    }

    m_mapping = CreateFileMappingW(
        INVALID_HANDLE_VALUE,
        securityAttributesPointer,
        PAGE_READWRITE,
        0,
        SharedFrameMappingSize,
        SharedFrameBufferName);

    if (!m_mapping)
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_SWAPCHAIN, "%!FUNC! CreateFileMapping failed: %lu", GetLastError());
        if (securityAcl)
        {
            LocalFree(securityAcl);
        }
        return false;
    }

    m_view = static_cast<BYTE*>(MapViewOfFile(m_mapping, FILE_MAP_ALL_ACCESS, 0, 0, SharedFrameMappingSize));
    if (!m_view)
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_SWAPCHAIN, "%!FUNC! MapViewOfFile failed: %lu", GetLastError());
        CloseHandle(m_mapping);
        m_mapping = nullptr;
        if (securityAcl)
        {
            LocalFree(securityAcl);
        }
        return false;
    }

    m_frameReadyEvent = CreateEventW(securityAttributesPointer, FALSE, FALSE, SharedFrameReadyName);
    if (!m_frameReadyEvent)
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_SWAPCHAIN, "%!FUNC! CreateEvent frame ready failed: %lu", GetLastError());
        UnmapViewOfFile(m_view);
        m_view = nullptr;
        CloseHandle(m_mapping);
        m_mapping = nullptr;
        if (securityAcl)
        {
            LocalFree(securityAcl);
        }
        return false;
    }

    m_consumerAliveEvent = CreateEventW(securityAttributesPointer, TRUE, FALSE, SharedFrameConsumerAliveName);
    if (!m_consumerAliveEvent)
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_SWAPCHAIN, "%!FUNC! CreateEvent consumer alive failed: %lu", GetLastError());
        CloseHandle(m_frameReadyEvent);
        m_frameReadyEvent = nullptr;
        UnmapViewOfFile(m_view);
        m_view = nullptr;
        CloseHandle(m_mapping);
        m_mapping = nullptr;
        if (securityAcl)
        {
            LocalFree(securityAcl);
        }
        return false;
    }

    InitializeHeader();
    TraceEvents(
        TRACE_LEVEL_INFORMATION,
        TRACE_SWAPCHAIN,
        "%!FUNC! shared frame buffer ready bytes=%u slots=%u",
        SharedFrameMappingSize,
        SharedFrameSlotCount);

    if (securityAcl)
    {
        LocalFree(securityAcl);
    }

    return true;
}

bool SharedFrameBuffer::IsConsumerAlive() const
{
    if (!m_consumerAliveEvent)
    {
        return false;
    }

    return WaitForSingleObject(m_consumerAliveEvent, 0) == WAIT_OBJECT_0;
}

bool SharedFrameBuffer::WriteFrame(const BYTE* bgra, UINT width, UINT height, UINT stride, UINT64 timestampQpc)
{
    if (!m_view && !EnsureInitialized())
    {
        return false;
    }

    const UINT sharedFrameStride = width * 4;
    const UINT sharedFrameSlotSize = sharedFrameStride * height;

    if (width == 0 ||
        height == 0 ||
        width > MaxVirtualDisplayWidth ||
        height > MaxVirtualDisplayHeight ||
        stride < sharedFrameStride ||
        sharedFrameSlotSize > MaxSharedFrameSlotSize)
    {
        TraceEvents(
            TRACE_LEVEL_WARNING,
            TRACE_SWAPCHAIN,
            "%!FUNC! unsupported frame layout width=%u height=%u stride=%u",
            width,
            height,
            stride);
        return false;
    }

    const UINT64 seq = ++m_writeSeq;
    const UINT slotIndex = static_cast<UINT>((seq - 1) % SharedFrameSlotCount);
    BYTE* slotBase = m_view + sizeof(SharedFrameHeader) + slotIndex * (sizeof(SharedFrameSlotHeader) + MaxSharedFrameSlotSize);
    auto* slotHeader = reinterpret_cast<SharedFrameSlotHeader*>(slotBase);
    BYTE* payload = slotBase + sizeof(SharedFrameSlotHeader);
    if (!payload)
    {
        return false;
    }

    for (UINT row = 0; row < height; ++row)
    {
        CopyMemory(payload + row * sharedFrameStride, bgra + row * stride, sharedFrameStride);
    }

    slotHeader->TimestampQpc = timestampQpc;
    slotHeader->Length = sharedFrameSlotSize;
    slotHeader->Reserved = 0;
    MemoryBarrier();
    slotHeader->Seq = seq;

    auto* header = reinterpret_cast<SharedFrameHeader*>(m_view);
    header->Width = width;
    header->Height = height;
    header->Stride = sharedFrameStride;
    header->SlotSize = MaxSharedFrameSlotSize;
    header->TimestampQpc = timestampQpc;
    MemoryBarrier();
    header->WriteSeq = seq;

    SetEvent(m_frameReadyEvent);
    return true;
}

void SharedFrameBuffer::InitializeHeader()
{
    ZeroMemory(m_view, SharedFrameMappingSize);

    auto* header = reinterpret_cast<SharedFrameHeader*>(m_view);
    header->Magic = SharedFrameMagic;
    header->Version = SharedFrameVersion;
    header->Width = VirtualDisplayWidth;
    header->Height = VirtualDisplayHeight;
    header->Format = SharedFrameFormatBgra;
    header->Stride = VirtualDisplayWidth * 4;
    header->SlotCount = SharedFrameSlotCount;
    header->SlotSize = MaxSharedFrameSlotSize;
    header->WriteSeq = 0;
    header->TimestampQpc = 0;
}

bool SharedFrameBuffer::CreateSecurityAttributes(SECURITY_ATTRIBUTES& attributes, SECURITY_DESCRIPTOR& descriptor, PACL& acl)
{
    acl = nullptr;
    if (!InitializeSecurityDescriptor(&descriptor, SECURITY_DESCRIPTOR_REVISION))
    {
        return false;
    }

    BYTE localSystemSid[SECURITY_MAX_SID_SIZE] = {};
    BYTE administratorsSid[SECURITY_MAX_SID_SIZE] = {};
    BYTE authenticatedUsersSid[SECURITY_MAX_SID_SIZE] = {};
    DWORD localSystemSidSize = sizeof(localSystemSid);
    DWORD administratorsSidSize = sizeof(administratorsSid);
    DWORD authenticatedUsersSidSize = sizeof(authenticatedUsersSid);

    if (!CreateWellKnownSid(WinLocalSystemSid, nullptr, localSystemSid, &localSystemSidSize) ||
        !CreateWellKnownSid(WinBuiltinAdministratorsSid, nullptr, administratorsSid, &administratorsSidSize) ||
        !CreateWellKnownSid(WinAuthenticatedUserSid, nullptr, authenticatedUsersSid, &authenticatedUsersSidSize))
    {
        return false;
    }

    EXPLICIT_ACCESSW entries[3] = {};
    entries[0].grfAccessPermissions = GENERIC_ALL;
    entries[0].grfAccessMode = SET_ACCESS;
    entries[0].grfInheritance = NO_INHERITANCE;
    entries[0].Trustee.TrusteeForm = TRUSTEE_IS_SID;
    entries[0].Trustee.TrusteeType = TRUSTEE_IS_USER;
    entries[0].Trustee.ptstrName = reinterpret_cast<LPWSTR>(localSystemSid);

    entries[1].grfAccessPermissions = GENERIC_ALL;
    entries[1].grfAccessMode = SET_ACCESS;
    entries[1].grfInheritance = NO_INHERITANCE;
    entries[1].Trustee.TrusteeForm = TRUSTEE_IS_SID;
    entries[1].Trustee.TrusteeType = TRUSTEE_IS_GROUP;
    entries[1].Trustee.ptstrName = reinterpret_cast<LPWSTR>(administratorsSid);

    entries[2].grfAccessPermissions = GENERIC_READ | GENERIC_WRITE | SYNCHRONIZE;
    entries[2].grfAccessMode = SET_ACCESS;
    entries[2].grfInheritance = NO_INHERITANCE;
    entries[2].Trustee.TrusteeForm = TRUSTEE_IS_SID;
    entries[2].Trustee.TrusteeType = TRUSTEE_IS_GROUP;
    entries[2].Trustee.ptstrName = reinterpret_cast<LPWSTR>(authenticatedUsersSid);

    if (SetEntriesInAclW(static_cast<ULONG>(std::size(entries)), entries, nullptr, &acl) != ERROR_SUCCESS)
    {
        return false;
    }

    if (!SetSecurityDescriptorDacl(&descriptor, TRUE, acl, FALSE))
    {
        LocalFree(acl);
        acl = nullptr;
        return false;
    }

    attributes.nLength = sizeof(attributes);
    attributes.lpSecurityDescriptor = &descriptor;
    attributes.bInheritHandle = FALSE;
    return true;
}

SwapChainProcessor::SwapChainProcessor(IDDCX_SWAPCHAIN swapChain, std::shared_ptr<Direct3DDevice> device, HANDLE newFrameEvent) :
    m_swapChain(swapChain),
    m_device(std::move(device)),
    m_availableBufferEvent(newFrameEvent)
{
    m_terminateEvent.Attach(CreateEvent(nullptr, FALSE, FALSE, nullptr));
    m_thread.Attach(CreateThread(nullptr, 0, RunThread, this, 0, nullptr));
    TraceEvents(TRACE_LEVEL_INFORMATION, TRACE_SWAPCHAIN, "%!FUNC! SwapChainAssigned");
}

SwapChainProcessor::~SwapChainProcessor()
{
    if (m_terminateEvent.Get())
    {
        SetEvent(m_terminateEvent.Get());
    }

    if (m_thread.Get())
    {
        WaitForSingleObject(m_thread.Get(), INFINITE);
    }

    TraceEvents(
        TRACE_LEVEL_INFORMATION,
        TRACE_SWAPCHAIN,
        "%!FUNC! SwapChainReleased framesReceived=%llu framesExported=%llu framesDropped=%llu exportErrors=%llu framesDiscarded=%llu",
        m_framesReceived,
        m_framesExported,
        m_framesDropped,
        m_exportErrors,
        m_framesDiscarded);
}

DWORD CALLBACK SwapChainProcessor::RunThread(LPVOID argument)
{
    reinterpret_cast<SwapChainProcessor*>(argument)->Run();
    return 0;
}

void SwapChainProcessor::Run()
{
    DWORD avTask = 0;
    HANDLE avTaskHandle = AvSetMmThreadCharacteristicsW(L"Distribution", &avTask);

    RunCore();

    if (m_swapChain)
    {
        WdfObjectDelete(reinterpret_cast<WDFOBJECT>(m_swapChain));
        m_swapChain = nullptr;
    }

    if (avTaskHandle)
    {
        AvRevertMmThreadCharacteristics(avTaskHandle);
    }
}

void SwapChainProcessor::RunCore()
{
    ComPtr<IDXGIDevice> dxgiDevice;
    HRESULT hr = m_device->Device.As(&dxgiDevice);
    if (FAILED(hr))
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_SWAPCHAIN, "%!FUNC! Query IDXGIDevice failed: 0x%08x", hr);
        return;
    }

    IDARG_IN_SWAPCHAINSETDEVICE setDevice = {};
    setDevice.pDevice = dxgiDevice.Get();

    hr = IddCxSwapChainSetDevice(m_swapChain, &setDevice);
    if (FAILED(hr))
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_SWAPCHAIN, "%!FUNC! IddCxSwapChainSetDevice failed: 0x%08x", hr);
        return;
    }

    for (;;)
    {
        IDARG_OUT_RELEASEANDACQUIREBUFFER buffer = {};
        hr = IddCxSwapChainReleaseAndAcquireBuffer(m_swapChain, &buffer);

        if (hr == E_PENDING)
        {
            HANDLE waitHandles[] =
            {
                m_availableBufferEvent,
                m_terminateEvent.Get()
            };

            DWORD waitResult = WaitForMultipleObjects(ARRAYSIZE(waitHandles), waitHandles, FALSE, 16);
            if (waitResult == WAIT_OBJECT_0 || waitResult == WAIT_TIMEOUT)
            {
                continue;
            }

            if (waitResult == WAIT_OBJECT_0 + 1)
            {
                break;
            }

            TraceEvents(TRACE_LEVEL_ERROR, TRACE_SWAPCHAIN, "%!FUNC! WaitForMultipleObjects failed: 0x%08x", waitResult);
            break;
        }

        if (FAILED(hr))
        {
            TraceEvents(TRACE_LEVEL_WARNING, TRACE_SWAPCHAIN, "%!FUNC! acquire failed or swap chain abandoned: 0x%08x", hr);
            break;
        }

        ++m_framesReceived;

        ComPtr<IDXGIResource> acquiredBuffer;
        acquiredBuffer.Attach(buffer.MetaData.pSurface);
        ProcessFrame(buffer.MetaData);
        acquiredBuffer.Reset();

        hr = IddCxSwapChainFinishedProcessingFrame(m_swapChain);
        if (FAILED(hr))
        {
            TraceEvents(TRACE_LEVEL_ERROR, TRACE_SWAPCHAIN, "%!FUNC! IddCxSwapChainFinishedProcessingFrame failed: 0x%08x", hr);
            break;
        }

        if ((m_framesReceived % 300) == 0)
        {
            TraceEvents(
                TRACE_LEVEL_INFORMATION,
                TRACE_SWAPCHAIN,
                "%!FUNC! framesReceived=%llu framesExported=%llu framesDropped=%llu exportErrors=%llu",
                m_framesReceived,
                m_framesExported,
                m_framesDropped,
                m_exportErrors);
        }
    }
}

void SwapChainProcessor::ProcessFrame(const IDDCX_METADATA& metadata)
{
    if (!m_sharedFrameBuffer.EnsureInitialized())
    {
        ++m_exportErrors;
        ++m_framesDiscarded;
        return;
    }

    if (!m_sharedFrameBuffer.IsConsumerAlive())
    {
        ++m_framesDropped;
        ++m_framesDiscarded;
        return;
    }

    if (!metadata.pSurface)
    {
        ++m_exportErrors;
        ++m_framesDiscarded;
        TraceEvents(TRACE_LEVEL_WARNING, TRACE_SWAPCHAIN, "%!FUNC! acquired frame has no surface");
        return;
    }

    ComPtr<ID3D11Texture2D> sourceTexture;
    HRESULT hr = metadata.pSurface->QueryInterface(IID_PPV_ARGS(&sourceTexture));
    if (FAILED(hr))
    {
        ++m_exportErrors;
        ++m_framesDiscarded;
        TraceEvents(TRACE_LEVEL_WARNING, TRACE_SWAPCHAIN, "%!FUNC! Query ID3D11Texture2D failed: 0x%08x", hr);
        return;
    }

    if (!EnsureStagingTexture(sourceTexture.Get()))
    {
        ++m_exportErrors;
        ++m_framesDiscarded;
        return;
    }

    auto context = m_device->DeviceContext.Get();
    context->CopyResource(m_stagingTexture.Get(), sourceTexture.Get());

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    hr = context->Map(m_stagingTexture.Get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr))
    {
        ++m_exportErrors;
        ++m_framesDiscarded;
        TraceEvents(TRACE_LEVEL_WARNING, TRACE_SWAPCHAIN, "%!FUNC! Map staging texture failed: 0x%08x", hr);
        return;
    }

    D3D11_TEXTURE2D_DESC desc = {};
    m_stagingTexture->GetDesc(&desc);
    const bool exported = m_sharedFrameBuffer.WriteFrame(
        static_cast<const BYTE*>(mapped.pData),
        desc.Width,
        desc.Height,
        mapped.RowPitch,
        metadata.PresentDisplayQPCTime);

    context->Unmap(m_stagingTexture.Get(), 0);

    if (exported)
    {
        ++m_framesExported;
        if ((m_framesExported % 300) == 0)
        {
            TraceEvents(TRACE_LEVEL_INFORMATION, TRACE_SWAPCHAIN, "%!FUNC! frame exported seq=%llu", m_framesExported);
        }
    }
    else
    {
        ++m_exportErrors;
        ++m_framesDiscarded;
    }
}

bool SwapChainProcessor::EnsureStagingTexture(ID3D11Texture2D* sourceTexture)
{
    D3D11_TEXTURE2D_DESC desc = {};
    sourceTexture->GetDesc(&desc);

    if (m_stagingTexture)
    {
        D3D11_TEXTURE2D_DESC existingDesc = {};
        m_stagingTexture->GetDesc(&existingDesc);
        if (existingDesc.Width == desc.Width && existingDesc.Height == desc.Height)
        {
            return true;
        }

        m_stagingTexture.Reset();
    }

    if (desc.Width > MaxVirtualDisplayWidth || desc.Height > MaxVirtualDisplayHeight)
    {
        TraceEvents(
            TRACE_LEVEL_WARNING,
            TRACE_SWAPCHAIN,
            "%!FUNC! unsupported swap chain texture size width=%u height=%u",
            desc.Width,
            desc.Height);
        return false;
    }

    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    desc.MiscFlags = 0;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.SampleDesc.Count = 1;
    desc.SampleDesc.Quality = 0;

    HRESULT hr = m_device->Device->CreateTexture2D(&desc, nullptr, &m_stagingTexture);
    if (FAILED(hr))
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_SWAPCHAIN, "%!FUNC! staging texture creation failed: 0x%08x", hr);
        return false;
    }

    TraceEvents(
        TRACE_LEVEL_INFORMATION,
        TRACE_SWAPCHAIN,
        "%!FUNC! staging texture ready width=%u height=%u format=%u",
        desc.Width,
        desc.Height,
        desc.Format);
    return true;
}

DeviceContext::DeviceContext(WDFDEVICE wdfDevice) :
    m_wdfDevice(wdfDevice)
{
}

DeviceContext::~DeviceContext() = default;

void DeviceContext::InitAdapter()
{
    TraceEvents(TRACE_LEVEL_INFORMATION, TRACE_ADAPTER, "%!FUNC! AdapterInit");

    IDDCX_ADAPTER_CAPS adapterCaps = {};
    adapterCaps.Size = sizeof(adapterCaps);
    adapterCaps.MaxMonitorsSupported = 1;
    adapterCaps.EndPointDiagnostics.Size = sizeof(adapterCaps.EndPointDiagnostics);
    adapterCaps.EndPointDiagnostics.GammaSupport = IDDCX_FEATURE_IMPLEMENTATION_NONE;
    adapterCaps.EndPointDiagnostics.TransmissionType = IDDCX_TRANSMISSION_TYPE_WIRED_OTHER;
    adapterCaps.EndPointDiagnostics.pEndPointFriendlyName = L"SideDock Virtual Display";
    adapterCaps.EndPointDiagnostics.pEndPointManufacturerName = L"SideDock";
    adapterCaps.EndPointDiagnostics.pEndPointModelName = L"SideDock IddCx Prototype";

    IDDCX_ENDPOINT_VERSION version = {};
    version.Size = sizeof(version);
    version.MajorVer = 0;
    version.MinorVer = 1;
    adapterCaps.EndPointDiagnostics.pFirmwareVersion = &version;
    adapterCaps.EndPointDiagnostics.pHardwareVersion = &version;

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DeviceContextWrapper);

    IDARG_IN_ADAPTER_INIT adapterInit = {};
    adapterInit.WdfDevice = m_wdfDevice;
    adapterInit.pCaps = &adapterCaps;
    adapterInit.ObjectAttributes = &attributes;

    IDARG_OUT_ADAPTER_INIT adapterInitOut = {};
    NTSTATUS status = IddCxAdapterInitAsync(&adapterInit, &adapterInitOut);
    if (!NT_SUCCESS(status))
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_ADAPTER, "%!FUNC! IddCxAdapterInitAsync failed: 0x%08x", status);
        return;
    }

    m_adapter = adapterInitOut.AdapterObject;
    auto* wrapper = WdfObjectGet_DeviceContextWrapper(adapterInitOut.AdapterObject);
    wrapper->Context = this;
}

void DeviceContext::ReportVirtualMonitor()
{
    if (m_monitorReported)
    {
        return;
    }

    TraceEvents(TRACE_LEVEL_INFORMATION, TRACE_MONITOR, "%!FUNC! MonitorArrival");

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, MonitorContextWrapper);
    attributes.EvtCleanupCallback = [](WDFOBJECT object)
    {
        if (auto* wrapper = WdfObjectGet_MonitorContextWrapper(object))
        {
            wrapper->Cleanup();
        }
    };

    IDDCX_MONITOR_INFO monitorInfo = {};
    monitorInfo.Size = sizeof(monitorInfo);
    monitorInfo.MonitorType = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI;
    monitorInfo.ConnectorIndex = VirtualMonitorConnectorIndex;
    monitorInfo.MonitorDescription.Size = sizeof(monitorInfo.MonitorDescription);
    monitorInfo.MonitorDescription.Type = IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
    monitorInfo.MonitorDescription.DataSize = 0;
    monitorInfo.MonitorDescription.pData = nullptr;

    // Stable enough for the one-monitor prototype. Production builds should use a per-device container id.
    monitorInfo.MonitorContainerId = { 0xa8155d92, 0x6a0a, 0x4ed7, { 0x9c, 0xf5, 0x2a, 0x1b, 0x4b, 0x33, 0x71, 0x01 } };

    IDARG_IN_MONITORCREATE monitorCreate = {};
    monitorCreate.ObjectAttributes = &attributes;
    monitorCreate.pMonitorInfo = &monitorInfo;

    IDARG_OUT_MONITORCREATE monitorCreateOut = {};
    NTSTATUS status = IddCxMonitorCreate(m_adapter, &monitorCreate, &monitorCreateOut);
    if (!NT_SUCCESS(status))
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_MONITOR, "%!FUNC! IddCxMonitorCreate failed: 0x%08x", status);
        return;
    }

    auto* wrapper = WdfObjectGet_MonitorContextWrapper(monitorCreateOut.MonitorObject);
    wrapper->Context = new MonitorContext(monitorCreateOut.MonitorObject);

    IDARG_OUT_MONITORARRIVAL arrivalOut = {};
    status = IddCxMonitorArrival(monitorCreateOut.MonitorObject, &arrivalOut);
    if (!NT_SUCCESS(status))
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_MONITOR, "%!FUNC! IddCxMonitorArrival failed: 0x%08x", status);
        return;
    }

    m_monitorReported = true;
}

MonitorContext::MonitorContext(IDDCX_MONITOR monitor) :
    m_monitor(monitor)
{
}

MonitorContext::~MonitorContext()
{
    m_processor.reset();
}

void MonitorContext::AssignSwapChain(IDDCX_SWAPCHAIN swapChain, LUID renderAdapter, HANDLE newFrameEvent)
{
    m_processor.reset();

    auto device = std::make_shared<Direct3DDevice>(renderAdapter);
    if (FAILED(device->Init()))
    {
        WdfObjectDelete(reinterpret_cast<WDFOBJECT>(swapChain));
        return;
    }

    m_processor = std::make_unique<SwapChainProcessor>(swapChain, device, newFrameEvent);
}

void MonitorContext::UnassignSwapChain()
{
    m_processor.reset();
}

_Use_decl_annotations_
NTSTATUS SideDockAdapterInitFinished(IDDCX_ADAPTER adapterObject, const IDARG_IN_ADAPTER_INIT_FINISHED* inArgs)
{
    auto* wrapper = WdfObjectGet_DeviceContextWrapper(adapterObject);
    if (NT_SUCCESS(inArgs->AdapterInitStatus))
    {
        wrapper->Context->ReportVirtualMonitor();
    }
    else
    {
        TraceEvents(TRACE_LEVEL_ERROR, TRACE_ADAPTER, "%!FUNC! adapter init failed: 0x%08x", inArgs->AdapterInitStatus);
    }

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS SideDockAdapterCommitModes(IDDCX_ADAPTER adapterObject, const IDARG_IN_COMMITMODES* inArgs)
{
    UNREFERENCED_PARAMETER(adapterObject);
    UNREFERENCED_PARAMETER(inArgs);
    TraceEvents(TRACE_LEVEL_INFORMATION, TRACE_ADAPTER, "%!FUNC! AdapterCommitModes");
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS SideDockParseMonitorDescription(
    const IDARG_IN_PARSEMONITORDESCRIPTION* inArgs,
    IDARG_OUT_PARSEMONITORDESCRIPTION* outArgs)
{
    UNREFERENCED_PARAMETER(inArgs);
    TraceEvents(TRACE_LEVEL_INFORMATION, TRACE_MONITOR, "%!FUNC! ModeQuery ParseMonitorDescription");

    constexpr UINT modeCount = static_cast<UINT>(std::size(VirtualDisplayModes));
    outArgs->MonitorModeBufferOutputCount = modeCount;
    if (inArgs->MonitorModeBufferInputCount == 0)
    {
        return STATUS_SUCCESS;
    }

    if (inArgs->MonitorModeBufferInputCount < modeCount)
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    for (UINT index = 0; index < modeCount; ++index)
    {
        const auto mode = VirtualDisplayModes[index];
        inArgs->pMonitorModes[index] = CreateMonitorMode(
            mode.Width,
            mode.Height,
            mode.RefreshRate,
            IDDCX_MONITOR_MODE_ORIGIN_DRIVER);
    }
    outArgs->PreferredMonitorModeIdx = 0;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS SideDockMonitorGetDefaultModes(
    IDDCX_MONITOR monitorObject,
    const IDARG_IN_GETDEFAULTDESCRIPTIONMODES* inArgs,
    IDARG_OUT_GETDEFAULTDESCRIPTIONMODES* outArgs)
{
    UNREFERENCED_PARAMETER(monitorObject);
    TraceEvents(TRACE_LEVEL_INFORMATION, TRACE_MONITOR, "%!FUNC! ModeQuery DefaultModes");

    constexpr UINT modeCount = static_cast<UINT>(std::size(VirtualDisplayModes));
    outArgs->DefaultMonitorModeBufferOutputCount = modeCount;
    if (inArgs->DefaultMonitorModeBufferInputCount == 0)
    {
        return STATUS_SUCCESS;
    }

    if (inArgs->DefaultMonitorModeBufferInputCount < modeCount)
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    for (UINT index = 0; index < modeCount; ++index)
    {
        const auto mode = VirtualDisplayModes[index];
        inArgs->pDefaultMonitorModes[index] = CreateMonitorMode(
            mode.Width,
            mode.Height,
            mode.RefreshRate,
            IDDCX_MONITOR_MODE_ORIGIN_DRIVER);
    }
    outArgs->PreferredMonitorModeIdx = 0;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS SideDockMonitorQueryTargetModes(
    IDDCX_MONITOR monitorObject,
    const IDARG_IN_QUERYTARGETMODES* inArgs,
    IDARG_OUT_QUERYTARGETMODES* outArgs)
{
    UNREFERENCED_PARAMETER(monitorObject);
    TraceEvents(TRACE_LEVEL_INFORMATION, TRACE_MONITOR, "%!FUNC! ModeQuery TargetModes");

    constexpr UINT modeCount = static_cast<UINT>(std::size(VirtualDisplayModes));
    outArgs->TargetModeBufferOutputCount = modeCount;
    if (inArgs->TargetModeBufferInputCount == 0)
    {
        return STATUS_SUCCESS;
    }

    if (inArgs->TargetModeBufferInputCount < modeCount)
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    for (UINT index = 0; index < modeCount; ++index)
    {
        const auto mode = VirtualDisplayModes[index];
        inArgs->pTargetModes[index] = CreateTargetMode(
            mode.Width,
            mode.Height,
            mode.RefreshRate);
    }

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS SideDockMonitorAssignSwapChain(IDDCX_MONITOR monitorObject, const IDARG_IN_SETSWAPCHAIN* inArgs)
{
    auto* wrapper = WdfObjectGet_MonitorContextWrapper(monitorObject);
    wrapper->Context->AssignSwapChain(inArgs->hSwapChain, inArgs->RenderAdapterLuid, inArgs->hNextSurfaceAvailable);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS SideDockMonitorUnassignSwapChain(IDDCX_MONITOR monitorObject)
{
    auto* wrapper = WdfObjectGet_MonitorContextWrapper(monitorObject);
    wrapper->Context->UnassignSwapChain();
    return STATUS_SUCCESS;
}
