# 05. 拆分 host pipeline

## 状态

已完成（2026-05-29）。

## 目标

把采集、转换、编码、发送从单串行循环拆开，减少任意一步峰值把整条链路拖死。

## 范围

- `windows-host/SideDock.Host/Program.cs`

## 要做的事

- [x] 把 acquire / convert / encode / send 拆成独立阶段。
- [x] 每个阶段保留自己的 bounded queue。
- [x] 每个阶段都输出独立 FPS 和掉帧统计。
- [x] 高压下优先保最新帧，不让队列无限堆积。

## 验收

- [x] 不是单个 `while` 循环串到底。
- [x] 每个阶段的瓶颈都能被单独看到。
- [x] 峰值不会直接把整条链路打成持续掉帧。
