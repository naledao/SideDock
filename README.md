# SideDock

SideDock is a prototype that uses an Android tablet as a secondary display for Windows.

The project currently contains three main parts:

- `windows-host/SideDock.Host`: Windows host service for control, video streaming, capture, encoding, and input handling.
- `windows-driver/SideDock.Idd`: Windows IddCx virtual display driver prototype.
- `android-client`: Android client for receiving control messages, decoding H.264 video, displaying the stream, and sending input events back to Windows.

## Requirements

- Windows 10/11
- .NET 8 SDK
- Android device with USB debugging enabled
- Android SDK platform tools (`adb`)
- Android Gradle/Gradle build environment
- Visual Studio and Windows Driver Kit, required only when building the virtual display driver

## Build

Build the Windows host:

```powershell
dotnet build .\windows-host\SideDock.Host\SideDock.Host.csproj
```

Build the Android client:

```powershell
gradle.bat -p .\android-client :app:assembleDebug
```

Build the Windows driver with Visual Studio using:

```text
windows-driver\SideDock.Driver.sln
```

## Run

Connect the Android device over USB, enable USB debugging, and configure ADB reverse ports:

```powershell
adb reverse tcp:27183 tcp:27183
adb reverse tcp:27184 tcp:27184
```

Start the Windows host:

```powershell
dotnet run --project .\windows-host\SideDock.Host\SideDock.Host.csproj
```

Install and open the Android client, then keep the Windows host running while the device is connected.

## Status

This is an early prototype. It is intended for local experimentation with USB transport, H.264 streaming, Android decoding, Windows capture, virtual display output, and input round-tripping.
