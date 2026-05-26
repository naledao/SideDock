# SideDock.Idd

Minimal UMDF + IddCx virtual display driver for the SideDock prototype.

Current scope:

- Reports one virtual monitor named `SideDock Virtual Display`.
- Exposes `720p`, `1080p`, and `2K` modes at `30Hz`, `60Hz`, and `120Hz`.
- Accepts the IddCx swap chain.
- Exports swap-chain frames through a 6-slot GPU shared D3D11 texture ring when `SideDock.Host --video-source idd-gpu` is running.
- Keeps the CPU-readable staging texture path as the `SideDock.Host --video-source idd` fallback.
- Exports BGRA frames through `Global\SideDockFrameBuffer`; the shared buffer can hold up to `2560x1440`.
- Signals `Global\SideDockFrameReady` after writing a frame.
- Uses `Global\SideDockFrameConsumerAlive` to skip export when `SideDock.Host --video-source idd` is not running.
- Publishes `Global\SideDockGpuFrameMetadata`, `Global\SideDockGpuFrameReady`, `Global\SideDockGpuConsumerAlive`, and `Global\SideDockGpuFrameSlot0..5` for the GPU path.
- Creates the GPU ring textures as `B8G8R8A8_UNorm` with `SHADER_RESOURCE | RENDER_TARGET` bind flags and keyed mutex synchronization.

Verified locally on 2026-05-25:

- The driver solution builds in `Debug|x64`.
- ApiValidator, Inf2Cat, and test signing pass.
- The validation package installed as `oem92.inf`.
- `SideDock.Idd.DeviceTool.exe` keeps the software device alive.
- Windows Display Settings shows `SideDock Virtual Display`.
- The original smoke test reported `1280 x 720 (32 bit) (30Hz)`; the current driver advertises `1280x720`, `1920x1080`, and `2560x1440` at `30/60/120Hz`.
- Uninstalling the package and removing the software device makes the virtual display disappear again.
- `SideDock.Host --video-source idd` reads the shared BGRA frames and streams the virtual desktop to Android.
- Lenovo TB-J706F displays the real `SideDock Virtual Display` desktop content.
- 10-minute Android stability validation passed from `2026-05-25 16:13:38` to `16:24:04`.
- Removing `adb reverse tcp:27183` and `tcp:27184` is recovered automatically by Host; Android video continues decoding after the mapping is rebuilt.

Verified locally on 2026-05-26:

- The full driver solution builds in `Debug|x64` with `/p:SkipPackageVerification=true /m`.
- `SideDock.Idd.dll` links, packages, passes ApiValidator and Inf2Cat, and is test-signed with 0 warnings / 0 errors.
- The latest validation package installed as `oem95.inf`, `DriverVer=05/26/2026 15.31.52.112`.
- A later local rebuild stamped the package output as `DriverVer=05/26/2026 16.15.6.595`; that output builds and signs cleanly, while the installed validation device is still using `oem95.inf`.
- `SideDock.Idd.DeviceTool.exe` keeps `SWD\SideDockIdd\SideDockIdd` alive for the virtual display.
- `SideDock.Host --video-source idd-gpu --resolution 1080p --refresh-rate 60` connects to the GPU texture ring and streams to Android with `videoReconnects=0` and `droppedFrames=0`; artifact: `artifacts/validation/idd-gpu-20260526-1080p60-clean-hostfirst/`.
- `SideDock.Host --video-source idd-gpu --resolution 2k --refresh-rate 60` passes a short smoke run with a `2560x1440` GPU ring and Android decode continuing; artifact: `artifacts/validation/idd-gpu-20260526-2k60-smoke-startupfill-20260526-160744/`.
- GPU frame dump proof is under `artifacts/validation/idd-gpu-20260526-1080p60-run4/gpu-dump/` and includes BGRA BMP plus NV12 raw/preview output.
- On this machine, the Microsoft H.264 Media Foundation MFT returns `E_NOTIMPL` for `MFT_MESSAGE_SET_D3D_MANAGER`; Host therefore uses the supported `GPU BGRA->NV12 -> NV12 readback -> Media Foundation byte[] input` fallback instead of direct D3D11 encoder input.
- The default DeviceTool output can be locked while an existing virtual display instance is running; close that process before rebuilding the full solution into the default `x64\Debug` directory.
- On this machine, WDK `10.0.26100.0` is missing `InfVerif.dll`; use `/p:SkipPackageVerification=true` for local iterative builds, or repair the WDK install before running full package verification.

Run the Android visual path with:

```powershell
dotnet run --project .\windows-host\SideDock.Host\SideDock.Host.csproj -- --video-source idd
```

Run the GPU shared-texture path with automatic CPU/MFT fallback:

```powershell
dotnet run --project .\windows-host\SideDock.Host\SideDock.Host.csproj -- --video-source idd-gpu --resolution 1080p --refresh-rate 60
```

Select a quality/refresh preset with:

```powershell
dotnet run --project .\windows-host\SideDock.Host\SideDock.Host.csproj -- --video-source idd --resolution 1080p --refresh-rate 60
dotnet run --project .\windows-host\SideDock.Host\SideDock.Host.csproj -- --video-source idd --resolution 2k --refresh-rate 120
dotnet run --project .\windows-host\SideDock.Host\SideDock.Host.csproj -- --video-source idd-gpu --resolution 2k --refresh-rate 120
```

This project is intentionally based on Microsoft Windows-driver-samples `video/IndirectDisplay/IddSampleDriver`, then reduced to the SideDock one-monitor path. The original sample is distributed under MS-PL; keep [../LICENSE-MS-PL.txt](../LICENSE-MS-PL.txt) with this source tree.

## Build

Install Visual Studio with C++ desktop workload plus the Windows Driver Kit that includes the IddCx headers and driver MSBuild targets.

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe" `
  .\windows-driver\SideDock.Driver.sln `
  /p:Configuration=Debug `
  /p:Platform=x64
```

For local iterative driver builds when the installed WDK package verifier is incomplete:

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" `
  .\windows-driver\SideDock.Idd\SideDock.Idd.vcxproj `
  /p:Configuration=Debug `
  /p:Platform=x64 `
  /p:SkipPackageVerification=true
```

Expected driver package output is under:

```text
windows-driver\SideDock.Idd\x64\Debug\
```

## Test Install

Run an elevated PowerShell session.

```powershell
bcdedit /set testsigning on
```

Reboot after enabling test signing. Then add the driver package:

```powershell
pnputil /add-driver .\windows-driver\SideDock.Idd\x64\Debug\SideDock.Idd.inf /install
```

Create a software device instance and keep it alive:

```powershell
.\windows-driver\SideDock.Idd.DeviceTool\x64\Debug\SideDock.Idd.DeviceTool.exe
```

While the tool is running, check:

```powershell
pnputil /enum-devices /class Display /deviceids /drivers
```

Windows Display Settings should show an additional display that can be extended.

Press `x` in the device tool console to close the software device.

On the current development machine, the validation package has been verified end to end with `oem92.inf`, `SWD\SideDockIdd\SideDockIdd`, and `SideDock Virtual Display`. The Android validation artifacts are under `artifacts/validation/idd-smoke-20260525-160926/`.

## Uninstall

Remove the software device first by closing `SideDock.Idd.DeviceTool.exe`.

Find the published driver package:

```powershell
pnputil /enum-drivers /class Display /files | Select-String -Context 3,8 "SideDock.Idd"
```

Then delete the matching `oemXX.inf`:

```powershell
pnputil /delete-driver oemXX.inf /uninstall /force
```

If the software device entry remains listed after package removal, run:

```powershell
pnputil /remove-device SWD\SIDEDOCKIDD\SIDEDOCKIDD
```

Disable test signing when finished:

```powershell
bcdedit /set testsigning off
```

Reboot after changing test signing.

## Logs

The driver uses WPP tracing with provider GUID:

```text
1D37EF59-31CB-4D8A-9B97-0C8E7569C6D6
```

Key log points are:

- `DriverEntry`
- `DeviceAdd`
- `DeviceD0Entry`
- `AdapterInit`
- `MonitorArrival`
- `ModeQuery`
- `SwapChainAssigned`
- `shared frame buffer ready`
- `staging texture ready`
- `frame exported`
- `framesReceived` / `framesExported` / `framesDropped` / `exportErrors`
- `SwapChainReleased`
- `DriverUnload`

The simplest first diagnostic pass is Device Manager plus `pnputil`. For deeper driver tracing, use WDK tracing tools such as TraceView or `traceview.exe` if present in the local WDK installation.
