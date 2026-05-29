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
#include <array>
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
    inline constexpr UINT MaxHardwareCursorWidth = 256;
    inline constexpr UINT MaxHardwareCursorHeight = 256;
    inline constexpr UINT MaxHardwareCursorShapeBytes = MaxHardwareCursorWidth * MaxHardwareCursorHeight * 4;
    inline constexpr UINT VirtualMonitorConnectorIndex = 0;
    inline constexpr UINT SharedFrameSlotCount = 3;
    inline constexpr UINT DefaultSharedGpuFrameSlotCount = 6;
    inline constexpr UINT MaxSharedGpuFrameSlotCount = 12;
    inline constexpr UINT SharedFrameFormatBgra = 1;
    inline constexpr UINT SharedFrameMagic = 0x464B4453; // SDKF
    inline constexpr UINT SharedFrameVersion = 1;
    inline constexpr UINT SharedGpuFrameMagic = 0x474B4453; // SDKG
    inline constexpr UINT SharedGpuFrameVersion = 1;

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

    struct SharedGpuFrameSlotHeader
    {
        UINT64 Seq;
        UINT64 TimestampQpc;
        UINT32 Width;
        UINT32 Height;
        UINT32 Format;
        UINT32 State;
    };

    struct SharedGpuFrameMetadata
    {
        UINT32 Magic;
        UINT32 Version;
        UINT32 Width;
        UINT32 Height;
        UINT32 Format;
        UINT32 SlotCount;
        UINT32 LatestSlot;
        UINT32 Generation;
        UINT64 WriteSeq;
        UINT64 TimestampQpc;
        UINT32 AdapterLuidLow;
        INT32 AdapterLuidHigh;
        UINT64 FrameDuration100ns;
        UINT32 ModeRefreshHz;
        UINT32 Flags;
        SharedGpuFrameSlotHeader Slots[MaxSharedGpuFrameSlotCount];
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

    class SharedGpuFrameRing
    {
    public:
        SharedGpuFrameRing() = default;
        ~SharedGpuFrameRing();

        SharedGpuFrameRing(const SharedGpuFrameRing&) = delete;
        SharedGpuFrameRing& operator=(const SharedGpuFrameRing&) = delete;

        bool EnsureInitialized(Direct3DDevice& device, ID3D11Texture2D* sourceTexture, UINT slotCount);
        bool IsConsumerAlive() const;
        bool WriteFrame(Direct3DDevice& device, ID3D11Texture2D* sourceTexture, UINT64 timestampQpc, UINT slotCount);

    private:
        void Close();
        void CloseTextures();
        bool CreateSecurityAttributes(SECURITY_ATTRIBUTES& attributes, SECURITY_DESCRIPTOR& descriptor, PACL& acl);
        bool RecreateTextures(Direct3DDevice& device, const D3D11_TEXTURE2D_DESC& sourceDesc, SECURITY_ATTRIBUTES* securityAttributes);
        void InitializeMetadata(Direct3DDevice& device, const D3D11_TEXTURE2D_DESC& sourceDesc);
        bool HasMatchingTextures(const D3D11_TEXTURE2D_DESC& sourceDesc) const;

        HANDLE m_mapping = nullptr;
        HANDLE m_frameReadyEvent = nullptr;
        HANDLE m_consumerAliveEvent = nullptr;
        SharedGpuFrameMetadata* m_metadata = nullptr;
        void SetSlotCount(UINT slotCount);

        Microsoft::WRL::ComPtr<ID3D11Texture2D> m_textures[MaxSharedGpuFrameSlotCount];
        Microsoft::WRL::ComPtr<IDXGIKeyedMutex> m_mutexes[MaxSharedGpuFrameSlotCount];
        HANDLE m_sharedHandles[MaxSharedGpuFrameSlotCount] = {};
        UINT m_slotCount = DefaultSharedGpuFrameSlotCount;
        UINT m_width = 0;
        UINT m_height = 0;
        UINT m_generation = 0;
        UINT64 m_writeSeq = 0;
    };

    class SwapChainProcessor
    {
    public:
        SwapChainProcessor(
            IDDCX_SWAPCHAIN swapChain,
            std::shared_ptr<Direct3DDevice> device,
            HANDLE newFrameEvent,
            class MonitorContext* monitorContext);
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
        class MonitorContext* m_monitorContext = nullptr;
        HANDLE m_availableBufferEvent = nullptr;
        Microsoft::WRL::Wrappers::Thread m_thread;
        Microsoft::WRL::Wrappers::Event m_terminateEvent;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> m_stagingTexture;
        SharedFrameBuffer m_sharedFrameBuffer;
        SharedGpuFrameRing m_sharedGpuFrameRing;
        UINT64 m_framesReceived = 0;
        UINT64 m_framesExported = 0;
        UINT64 m_gpuFramesExported = 0;
        UINT64 m_framesDropped = 0;
        UINT64 m_gpuFramesDropped = 0;
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
        UINT GpuRingSlotCount() const;
        void ReportVirtualMonitor();
        void HandleCommitModes(const IDARG_IN_COMMITMODES* inArgs);

    private:
        WDFDEVICE m_wdfDevice = nullptr;
        IDDCX_ADAPTER m_adapter = nullptr;
        class MonitorContext* m_monitorContext = nullptr;
        bool m_monitorReported = false;
        UINT m_gpuRingSlotCount = DefaultSharedGpuFrameSlotCount;
    };

    class MonitorContext
    {
    public:
        MonitorContext(IDDCX_MONITOR monitor, UINT gpuRingSlotCount);
        ~MonitorContext();

        MonitorContext(const MonitorContext&) = delete;
        MonitorContext& operator=(const MonitorContext&) = delete;

        bool EnableHardwareCursor();
        IDDCX_MONITOR GetMonitorObject() const;
        HANDLE GetHardwareCursorEvent() const;
        bool HandleHardwareCursorUpdate();
        UINT GpuRingSlotCount() const;

        void AssignSwapChain(IDDCX_SWAPCHAIN swapChain, LUID renderAdapter, HANDLE newFrameEvent);
        void UnassignSwapChain();

    private:
        IDDCX_MONITOR m_monitor = nullptr;
        HANDLE m_hardwareCursorEvent = nullptr;
        bool m_hardwareCursorEnabled = false;
        DWORD m_lastCursorShapeId = 0;
        BOOL m_lastCursorVisible = FALSE;
        INT m_lastCursorX = 0;
        INT m_lastCursorY = 0;
        IDDCX_CURSOR_SHAPE_INFO m_lastCursorShapeInfo = {};
        std::array<BYTE, MaxHardwareCursorShapeBytes> m_cursorShapeBuffer = {};
        std::unique_ptr<SwapChainProcessor> m_processor;
        UINT m_gpuRingSlotCount = DefaultSharedGpuFrameSlotCount;
    };
}
