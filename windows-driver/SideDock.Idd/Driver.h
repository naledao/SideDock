#pragma once

#define NOMINMAX

#include <windows.h>
#include <bugcodes.h>
#include <wudfwdm.h>
#include <wdf.h>
#include <iddcx.h>

#include <avrt.h>
#include <d3d11_2.h>
#include <dxgi1_5.h>
#include <wrl.h>

#include <aclapi.h>
#include <cstdint>
#include <memory>

#include "Trace.h"

namespace Microsoft::WRL::Wrappers
{
    typedef HandleT<HandleTraits::HANDLENullTraits> Thread;
}

namespace SideDock::Idd
{
    struct VirtualDisplayMode
    {
        DWORD Width;
        DWORD Height;
        DWORD RefreshRate;
    };

    inline constexpr VirtualDisplayMode VirtualDisplayModes[] =
    {
        { 1280, 720, 30 },
        { 1280, 720, 60 },
        { 1280, 720, 120 },
        { 1920, 1080, 30 },
        { 1920, 1080, 60 },
        { 1920, 1080, 120 },
        { 2560, 1440, 30 },
        { 2560, 1440, 60 },
        { 2560, 1440, 120 }
    };
    inline constexpr DWORD VirtualDisplayWidth = VirtualDisplayModes[0].Width;
    inline constexpr DWORD VirtualDisplayHeight = VirtualDisplayModes[0].Height;
    inline constexpr DWORD VirtualDisplayRefreshRate = VirtualDisplayModes[0].RefreshRate;
    inline constexpr DWORD MaxVirtualDisplayWidth = 2560;
    inline constexpr DWORD MaxVirtualDisplayHeight = 1440;
    inline constexpr UINT VirtualMonitorConnectorIndex = 0;
    inline constexpr UINT SharedFrameSlotCount = 3;
    inline constexpr UINT SharedFrameFormatBgra = 1;
    inline constexpr UINT SharedFrameMagic = 0x464B4453; // SDKF
    inline constexpr UINT SharedFrameVersion = 1;

    struct Direct3DDevice
    {
        explicit Direct3DDevice(LUID adapterLuid);
        HRESULT Init();

        LUID AdapterLuid = {};
        Microsoft::WRL::ComPtr<IDXGIFactory5> DxgiFactory;
        Microsoft::WRL::ComPtr<IDXGIAdapter1> Adapter;
        Microsoft::WRL::ComPtr<ID3D11Device> Device;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> DeviceContext;
    };

#pragma pack(push, 1)
    struct SharedFrameHeader
    {
        UINT32 Magic;
        UINT32 Version;
        UINT32 Width;
        UINT32 Height;
        UINT32 Format;
        UINT32 Stride;
        UINT32 SlotCount;
        UINT32 SlotSize;
        UINT64 WriteSeq;
        UINT64 TimestampQpc;
    };

    struct SharedFrameSlotHeader
    {
        UINT64 Seq;
        UINT64 TimestampQpc;
        UINT32 Length;
        UINT32 Reserved;
    };
#pragma pack(pop)

    class SharedFrameBuffer
    {
    public:
        SharedFrameBuffer() = default;
        ~SharedFrameBuffer();

        SharedFrameBuffer(const SharedFrameBuffer&) = delete;
        SharedFrameBuffer& operator=(const SharedFrameBuffer&) = delete;

        bool EnsureInitialized();
        bool IsConsumerAlive() const;
        bool WriteFrame(const BYTE* bgra, UINT width, UINT height, UINT stride, UINT64 timestampQpc);

    private:
        void InitializeHeader();
        bool CreateSecurityAttributes(SECURITY_ATTRIBUTES& attributes, SECURITY_DESCRIPTOR& descriptor, PACL& acl);

        HANDLE m_mapping = nullptr;
        HANDLE m_frameReadyEvent = nullptr;
        HANDLE m_consumerAliveEvent = nullptr;
        BYTE* m_view = nullptr;
        UINT64 m_writeSeq = 0;
    };

    class SwapChainProcessor
    {
    public:
        SwapChainProcessor(IDDCX_SWAPCHAIN swapChain, std::shared_ptr<Direct3DDevice> device, HANDLE newFrameEvent);
        ~SwapChainProcessor();

        SwapChainProcessor(const SwapChainProcessor&) = delete;
        SwapChainProcessor& operator=(const SwapChainProcessor&) = delete;

    private:
        static DWORD CALLBACK RunThread(LPVOID argument);

        void Run();
        void RunCore();
        void ProcessFrame(const IDDCX_METADATA& metadata);
        bool EnsureStagingTexture(ID3D11Texture2D* sourceTexture);

        IDDCX_SWAPCHAIN m_swapChain = nullptr;
        std::shared_ptr<Direct3DDevice> m_device;
        HANDLE m_availableBufferEvent = nullptr;
        Microsoft::WRL::Wrappers::Thread m_thread;
        Microsoft::WRL::Wrappers::Event m_terminateEvent;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> m_stagingTexture;
        SharedFrameBuffer m_sharedFrameBuffer;
        UINT64 m_framesReceived = 0;
        UINT64 m_framesExported = 0;
        UINT64 m_framesDropped = 0;
        UINT64 m_exportErrors = 0;
        UINT64 m_framesDiscarded = 0;
    };

    class DeviceContext
    {
    public:
        explicit DeviceContext(WDFDEVICE wdfDevice);
        ~DeviceContext();

        DeviceContext(const DeviceContext&) = delete;
        DeviceContext& operator=(const DeviceContext&) = delete;

        void InitAdapter();
        void ReportVirtualMonitor();

    private:
        WDFDEVICE m_wdfDevice = nullptr;
        IDDCX_ADAPTER m_adapter = nullptr;
        bool m_monitorReported = false;
    };

    class MonitorContext
    {
    public:
        explicit MonitorContext(IDDCX_MONITOR monitor);
        ~MonitorContext();

        MonitorContext(const MonitorContext&) = delete;
        MonitorContext& operator=(const MonitorContext&) = delete;

        void AssignSwapChain(IDDCX_SWAPCHAIN swapChain, LUID renderAdapter, HANDLE newFrameEvent);
        void UnassignSwapChain();

    private:
        IDDCX_MONITOR m_monitor = nullptr;
        std::unique_ptr<SwapChainProcessor> m_processor;
    };
}
