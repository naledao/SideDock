# 120fps 链路执行拆分

来源：[`../120fps链路改造方案.md`](../120fps链路改造方案.md)

这组文档把原方案拆成最小可执行任务。建议按顺序执行，前一个没完成时，后一个可以先读，但不要先动。

## 顺序

1. [00-判定标准.md](00-判定标准.md) - 先判定是不是 120fps 问题。
2. [01-metrics.md](01-metrics.md) - 再把 fps 口径说清楚。
3. [x] [02-pacer.md](02-pacer.md) - 再把 host 的 120 节拍稳住。
4. [03-synthetic-nv12.md](03-synthetic-nv12.md) - 用纯 NV12 基准源排除转换干扰。
5. [04-idd-gpu-default.md](04-idd-gpu-default.md) - 默认切到性能路径。
6. [05-pipeline-decouple.md](05-pipeline-decouple.md) - 把采集、转换、编码、发送拆开。
7. [06-remove-readback.md](06-remove-readback.md) - 尽量去掉热路径 readback。
8. [07-encoder-tuning.md](07-encoder-tuning.md) - 调编码器参数。
9. [08-ring-pool.md](08-ring-pool.md) - 调 ring / pool / queue 容量。
10. [09-android-diagnostics.md](09-android-diagnostics.md) - 把 Android 侧诊断补齐。
11. [10-validation-script.md](10-validation-script.md) - 固化联调脚本和产物。
12. [11-hevc-av1.md](11-hevc-av1.md) - 可选的后续编码器扩展。

## 执行原则

- 每个文件只做一件事。
- 每个文件都要有明确验收标准。
- 先让 `streamFps` / `decodeFps` 可信，再谈优化。
- 60Hz 面板不作为 120fps 链路失败的依据。
