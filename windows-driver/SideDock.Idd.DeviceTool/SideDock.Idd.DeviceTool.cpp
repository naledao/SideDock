#include <conio.h>
#include <stdio.h>
#include <windows.h>
#include <swdevice.h>

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
    for (int i = 1; i < argc; ++i)
    {
        if (_wcsicmp(argv[i], L"--oneshot") == 0)
        {
            waitUntilKeyPress = false;
        }
    }

    HANDLE eventHandle = CreateEvent(nullptr, FALSE, FALSE, nullptr);
    if (eventHandle == nullptr)
    {
        printf("CreateEvent failed: %lu\n", GetLastError());
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
        return 1;
    }

    printf("Waiting for SideDockIdd software device creation...\n");
    DWORD waitResult = WaitForSingleObject(eventHandle, 10 * 1000);
    CloseHandle(eventHandle);

    if (waitResult != WAIT_OBJECT_0)
    {
        printf("Timed out waiting for SideDockIdd software device creation.\n");
        SwDeviceClose(softwareDevice);
        return 1;
    }

    printf("SideDock Virtual Display device is present.\n");

    if (waitUntilKeyPress)
    {
        printf("Keep this process running while testing. Press x to remove the software device.\n");
        for (;;)
        {
            int key = _getch();
            if (key == 'x' || key == 'X')
            {
                break;
            }
        }
    }

    SwDeviceClose(softwareDevice);
    printf("SideDock Virtual Display device removed.\n");
    return 0;
}
