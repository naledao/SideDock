#include <conio.h>
#include <stdio.h>
#include <windows.h>
#include <swdevice.h>
#include <sddl.h>

constexpr wchar_t kStopEventName[] = L"Local\\SideDockIddDeviceToolStop";

HANDLE CreateStopEvent()
{
    PSECURITY_DESCRIPTOR securityDescriptor = nullptr;
    SECURITY_ATTRIBUTES securityAttributes = {};
    securityAttributes.nLength = sizeof(securityAttributes);

    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"D:(A;;GA;;;WD)S:(ML;;NW;;;LW)",
            SDDL_REVISION_1,
            &securityDescriptor,
            nullptr))
    {
        securityAttributes.lpSecurityDescriptor = securityDescriptor;
    }

    HANDLE eventHandle = CreateEventW(
        securityDescriptor == nullptr ? nullptr : &securityAttributes,
        TRUE,
        FALSE,
        kStopEventName);

    if (securityDescriptor != nullptr)
    {
        LocalFree(securityDescriptor);
    }

    return eventHandle;
}

int RequestStop()
{
    HANDLE eventHandle = OpenEventW(EVENT_MODIFY_STATE, FALSE, kStopEventName);
    if (eventHandle == nullptr)
    {
        printf("Open stop event failed: %lu\n", GetLastError());
        return 2;
    }

    if (!SetEvent(eventHandle))
    {
        printf("Set stop event failed: %lu\n", GetLastError());
        CloseHandle(eventHandle);
        return 3;
    }

    CloseHandle(eventHandle);
    printf("Stop request sent.\n");
    return 0;
}

VOID WINAPI CreationCallback(
    _In_ HSWDEVICE softwareDevice,
    _In_ HRESULT createResult,
    _In_opt_ PVOID context,
    _In_opt_ PCWSTR deviceInstanceId)
{
    UNREFERENCED_PARAMETER(softwareDevice);

    auto eventHandle = static_cast<HANDLE>(context);
    printf("SwDeviceCreate callback hr=0x%08lx\n", createResult);
    if (deviceInstanceId != nullptr)
    {
        wprintf(L"SwDeviceCreate instance id=%ls\n", deviceInstanceId);
    }

    SetEvent(eventHandle);
}

int __cdecl wmain(int argc, wchar_t* argv[])
{
    bool waitUntilKeyPress = true;
    bool stopRequested = false;
    for (int i = 1; i < argc; ++i)
    {
        if (_wcsicmp(argv[i], L"--oneshot") == 0)
        {
            waitUntilKeyPress = false;
        }
        else if (_wcsicmp(argv[i], L"--stop") == 0)
        {
            stopRequested = true;
        }
    }

    if (stopRequested)
    {
        return RequestStop();
    }

    HANDLE stopEventHandle = CreateStopEvent();
    if (stopEventHandle == nullptr)
    {
        printf("Create stop event failed: %lu\n", GetLastError());
        return 1;
    }
    ResetEvent(stopEventHandle);

    HANDLE eventHandle = CreateEvent(nullptr, FALSE, FALSE, nullptr);
    if (eventHandle == nullptr)
    {
        printf("CreateEvent failed: %lu\n", GetLastError());
        CloseHandle(stopEventHandle);
        return 1;
    }

    HSWDEVICE softwareDevice = nullptr;
    SW_DEVICE_CREATE_INFO createInfo = {};
    createInfo.cbSize = sizeof(createInfo);
    createInfo.pszInstanceId = L"SideDockIdd";
    createInfo.pszzHardwareIds = L"SideDockIdd\0SWD\\SideDockIdd\0SWD\\SideDockIdd\\SideDockIdd\0Root\\SideDockIdd\0\0";
    createInfo.pszzCompatibleIds = L"SideDockIdd\0SWD\\SideDockIdd\0SWD\\SideDockIdd\\SideDockIdd\0Root\\SideDockIdd\0\0";
    createInfo.pszDeviceDescription = L"SideDock Virtual Display";
    createInfo.CapabilityFlags =
        SWDeviceCapabilitiesRemovable |
        SWDeviceCapabilitiesSilentInstall |
        SWDeviceCapabilitiesDriverRequired;

    HRESULT hr = SwDeviceCreate(
        L"SideDockIdd",
        L"HTREE\\ROOT\\0",
        &createInfo,
        0,
        nullptr,
        CreationCallback,
        eventHandle,
        &softwareDevice);

    if (FAILED(hr))
    {
        printf("SwDeviceCreate failed: 0x%08lx\n", hr);
        CloseHandle(eventHandle);
        CloseHandle(stopEventHandle);
        return 1;
    }

    printf("Waiting for SideDockIdd software device creation...\n");
    DWORD waitResult = WaitForSingleObject(eventHandle, 10 * 1000);
    CloseHandle(eventHandle);

    if (waitResult != WAIT_OBJECT_0)
    {
        printf("Timed out waiting for SideDockIdd software device creation.\n");
        SwDeviceClose(softwareDevice);
        CloseHandle(stopEventHandle);
        return 1;
    }

    printf("SideDock Virtual Display device is present.\n");

    if (waitUntilKeyPress)
    {
        printf("Keep this process running while testing. Press x to remove the software device, or run with --stop.\n");
        for (;;)
        {
            DWORD stopWait = WaitForSingleObject(stopEventHandle, 100);
            if (stopWait == WAIT_OBJECT_0)
            {
                printf("Stop request received.\n");
                break;
            }

            if (_kbhit())
            {
                int key = _getch();
                if (key == 'x' || key == 'X')
                {
                    break;
                }
            }
        }
    }

    SwDeviceClose(softwareDevice);
    CloseHandle(stopEventHandle);
    printf("SideDock Virtual Display device removed.\n");
    return 0;
}
