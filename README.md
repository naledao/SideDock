**English** | [中文](#chinese)

# SideDock

SideDock is a prototype that turns an Android tablet into a low-latency secondary display for Windows. The Windows host captures the screen, encodes it to H.264, and streams it to the Android client over USB (ADB reverse), while input from the tablet is sent back to Windows.

The project currently contains three main parts:

- `windows-host/SideDock.Host`: Windows host service for control, video streaming, capture, encoding, and input handling.
- `windows-driver/SideDock.Idd`: Windows IddCx virtual display driver prototype.
- `android-client`: Android client for receiving control messages, decoding H.264 video, displaying the stream, and sending input events back to Windows.

## Low-latency video pipeline

SideDock targets high-refresh streaming (up to 2K @ 120 fps). The whole capture → encode → transport → decode chain is built around a single rule: **never let a backlog build up.** Every buffer between the GPU and the Android decoder is a short, bounded queue that, under load, drops frames to keep the newest one and never blocks its producer. Latency stays bounded instead of growing a queue of stale frames — at the cost of dropping frames when a stage cannot keep up.

The chain, from capture to display:

1. **Driver GPU ring (`windows-driver/SideDock.Idd`).** The IddCx driver copies each captured frame into a small ring of shared GPU textures (default **6** slots, configurable **1–12** via the `GpuRingSlots` registry value) and publishes the index of the latest slot to the host. Each slot is guarded by a keyed mutex; if the host is still reading and no slot is free, the incoming frame is dropped rather than stalling the GPU. The host always reads the most recent completed slot.

2. **Host pipeline (`windows-host/SideDock.Host`).** Capture, color conversion, encoding, and sending run as decoupled stages, so a spike in one stage does not serialize the rest. The inter-stage hand-offs keep only the latest frame (older frames are drained and discarded), the NV12 conversion rents textures from a bounded pool, and the encoded-packet queue drops its oldest packet when full (`BoundedChannelFullMode.DropOldest`). When a fresh frame is not ready, the host emits a *repeat* frame so the stream holds its target cadence instead of stalling.

3. **Client decode queue (`android-client`).** The client reads packets on the socket thread and pushes them into a **12**-slot queue that drops the oldest packet when full; a dedicated decoder thread drains the queue into `MediaCodec`. Decoupling the socket from the decoder means a momentary decode stall throttles through dropped frames instead of backing up the network read. If the hardware decoder fails the client retries with a software decoder, and if decoding fails entirely it keeps receiving so the channel can recover.

### Tuning the queue depths

Short queues favor latency; deeper queues absorb bursts at the cost of latency. The depth of each stage is tunable:

| Layer | Knob | Default | Notes |
| --- | --- | --- | --- |
| Driver GPU ring | `GpuRingSlots` (registry) | 6 | Valid range 1–12; out-of-range values fall back to 6 |
| Host NV12 texture pool | `--nv12-pool-size` | 4 | Aliases: `--nv12-texture-pool`, `--host-nv12-pool` |
| Host encoded-packet queue | `--encoded-packet-queue` | 2 | Alias: `--packet-queue`; `--max-video-queue` sets both |
| Client decode queue | `DECODE_QUEUE_CAPACITY` | 12 | Compile-time constant in `VideoClient` |

### Observability

Drops are counted, never silent. The host reports `framesDropped` alongside per-stage throughput (`captureFps`, `convertFps`, `streamFps`) and a `newFramesSent` / `repeatFramesSent` breakdown; the client reports `droppedFrames`, `decodeErrors`, and a `decodeFps` / `newFrameFps` / `repeatFrameFps` split. Because the host pads the stream with repeat frames, the `new`-frame counters are what tell you how many *distinct* frames per second actually reached the decoder — a 60 Hz panel can still receive ~120 new frames per second even though it only displays 60.

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

The host defaults to the `idd-gpu` source at 720p @ 120 fps. Common options include `--video-source` (`idd-gpu`, `realtime`, `synthetic-nv12`, ...), `--resolution` (`720p`, `1080p`, `2k`), `--refresh-rate` (`30`, `60`, `120`), and the queue-depth knobs in the table above.

Install and open the Android client, then keep the Windows host running while the device is connected.

## Status

This is still an early prototype, intended for local experimentation with USB transport, H.264 streaming, Android decoding, Windows capture, virtual display output, and input round-tripping. Current work focuses on the high-refresh, low-latency video pipeline described above — holding the end-to-end path at its target cadence under load by bounding every queue and dropping stale frames rather than buffering them.

---

<a id="chinese"></a>

[English](#sidedock) | **中文**

# SideDock

SideDock 是一个把 Android 平板变成 Windows 低延迟副屏的原型。Windows 主机负责采集屏幕、编码为 H.264,并通过 USB(ADB reverse)把视频流推送到 Android 客户端;同时平板上的输入会回传到 Windows。

项目目前包含三个主要部分:

- `windows-host/SideDock.Host`:Windows 主机服务,负责控制、视频推流、采集、编码与输入处理。
- `windows-driver/SideDock.Idd`:Windows IddCx 虚拟显示驱动原型。
- `android-client`:Android 客户端,负责接收控制消息、解码 H.264 视频、显示画面,并把输入事件回传到 Windows。

## 低延迟视频链路

SideDock 面向高刷新率推流(最高 2K @ 120fps)。整条「采集 → 编码 → 传输 → 解码」链路只围绕一条原则构建:**绝不让队列积压。** 从 GPU 到 Android 解码器之间的每一个缓冲都是一个短的有界队列——在高压下丢帧以保留最新的一帧,且永不阻塞它的生产者。于是延迟保持有界,而不是攒出一队过期帧;代价是当某一级跟不上时会丢帧。

从采集到显示,这条链路依次是:

1. **驱动 GPU 环形缓冲(`windows-driver/SideDock.Idd`)。** IddCx 驱动把每一帧采集结果拷贝进一个小的共享 GPU 纹理环(默认 **6** 个槽,可通过注册表值 `GpuRingSlots` 配置为 **1–12**),并把最新槽的索引发布给主机。每个槽由一个 keyed mutex 保护;如果主机仍在读取且没有空闲槽,新进来的这一帧会被丢弃,而不是让 GPU 卡住。主机始终读取最近写完的那个槽。

2. **主机流水线(`windows-host/SideDock.Host`)。** 采集、色彩转换、编码、发送作为相互解耦的阶段运行,这样某一级出现尖峰时不会把其余各级串成一条线一起拖死。各阶段之间的交接只保留最新一帧(更旧的帧会被排空丢弃),NV12 转换从一个有界纹理池中租借纹理,而编码包队列在满时丢弃最旧的包(`BoundedChannelFullMode.DropOldest`)。当没有新帧就绪时,主机会发出一帧**重复帧**,让码流维持目标节拍而不是停顿。

3. **客户端解码队列(`android-client`)。** 客户端在 socket 线程上读取数据包,并把它们压入一个最多 **12** 槽的队列,满时丢弃最旧的包;一个专用的解码线程把队列排空喂给 `MediaCodec`。把 socket 与解码解耦,意味着一次瞬时的解码停顿是通过丢帧来卸压,而不会把网络读取堵住。如果硬件解码器失败,客户端会改用软件解码器重试;如果解码彻底失败,它也会继续接收,以便链路能够自行恢复。

### 调整队列深度

短队列偏向低延迟;更深的队列能吸收突发,但要以延迟为代价。各级的深度均可调:

| 层 | 旋钮 | 默认值 | 说明 |
| --- | --- | --- | --- |
| 驱动 GPU 环 | `GpuRingSlots`(注册表) | 6 | 有效范围 1–12;越界值回退为 6 |
| 主机 NV12 纹理池 | `--nv12-pool-size` | 4 | 别名:`--nv12-texture-pool`、`--host-nv12-pool` |
| 主机编码包队列 | `--encoded-packet-queue` | 2 | 别名:`--packet-queue`;`--max-video-queue` 会同时设置两者 |
| 客户端解码队列 | `DECODE_QUEUE_CAPACITY` | 12 | `VideoClient` 中的编译期常量 |

### 可观测性

丢帧会被计数,绝不静默。主机会上报 `framesDropped`,以及各阶段吞吐(`captureFps`、`convertFps`、`streamFps`)和 `newFramesSent` / `repeatFramesSent` 的拆分;客户端会上报 `droppedFrames`、`decodeErrors`,以及 `decodeFps` / `newFrameFps` / `repeatFrameFps` 的拆分。由于主机会用重复帧把码流补满,`new`(新帧)计数才是真正反映每秒有多少**不同**帧到达解码器的指标——即便面板只有 60Hz、只能显示 60 帧,它仍可能每秒收到约 120 个新帧。

## 环境要求

- Windows 10/11
- .NET 8 SDK
- 已开启 USB 调试的 Android 设备
- Android SDK 平台工具(`adb`)
- Android Gradle / Gradle 构建环境
- Visual Studio 与 Windows Driver Kit,仅在构建虚拟显示驱动时需要

## 构建

构建 Windows 主机:

```powershell
dotnet build .\windows-host\SideDock.Host\SideDock.Host.csproj
```

构建 Android 客户端:

```powershell
gradle.bat -p .\android-client :app:assembleDebug
```

使用 Visual Studio 构建 Windows 驱动:

```text
windows-driver\SideDock.Driver.sln
```

## 运行

通过 USB 连接 Android 设备,开启 USB 调试,并配置 ADB reverse 端口:

```powershell
adb reverse tcp:27183 tcp:27183
adb reverse tcp:27184 tcp:27184
```

启动 Windows 主机:

```powershell
dotnet run --project .\windows-host\SideDock.Host\SideDock.Host.csproj
```

主机默认使用 `idd-gpu` 源,分辨率 720p、120fps。常用选项包括 `--video-source`(`idd-gpu`、`realtime`、`synthetic-nv12` 等)、`--resolution`(`720p`、`1080p`、`2k`)、`--refresh-rate`(`30`、`60`、`120`),以及上表中的队列深度旋钮。

安装并打开 Android 客户端,设备保持连接期间让 Windows 主机持续运行即可。

## 状态

这仍是一个早期原型,用于在本地实验 USB 传输、H.264 推流、Android 解码、Windows 采集、虚拟显示输出以及输入回环。当前工作聚焦于上文所述的高刷新率、低延迟视频链路——通过给每个队列设界、丢弃过期帧而不是缓存它们,让端到端链路在高压下仍维持目标节拍。
