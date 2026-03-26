# BoomNetwork 网络带宽分析报告

> 测试日期: 2026-03-26 | 协议版本: dev1.0 | 测试工具: `svr/cmd/stress`

## 1. 带宽公式

### 1.1 消息线格式

每条消息: `[FlagsCmd:1B][BodyLen:2B][Payload:NB]`

| 消息类型 | 方向 | 场景 | Wire 大小 |
|----------|------|------|-----------|
| CmdPushFrames (帧广播) | S→C | N 人房间, 每人 D 字节输入 | `9 + N × (6 + D)` |
| CmdPushFrames (空帧) | S→C | 无输入 | `9` |
| CmdFrameInput (玩家输入) | C→S | D 字节输入 | `3 + D` |
| CmdHeartbeat | C→S | 每 3 秒 | `3` |
| CmdHeartbeatRsp | S→C | 心跳响应 | `3` |

### 1.2 帧广播 Payload 编码

```
[FrameNumber: 4B LE]
[InputCount:  2B LE]
for each input:
    [PlayerId:  4B LE]
    [DataLen:   2B LE]
    [Data:      D bytes]
```

固定开销: 6 bytes (FrameNumber + InputCount)
每个输入: `6 + D` bytes (PlayerId + DataLen + Data)

### 1.3 通用公式

设 N = 房间人数, D = 输入字节数, F = 帧率 (fps):

```
单客户端下载 (B/s) = [9 + N × (6 + D)] × F    (帧广播)
                    + 1                          (心跳响应, 可忽略)

单客户端上传 (B/s) = (3 + D) × F               (每帧发输入)
                    + 1                          (心跳, 可忽略)

服务器总出带宽     = 单客户端下载 × 总人数
服务器总入带宽     = 单客户端上传 × 总人数
```

> **注**: Dashboard Game Traffic 统计的是 payload only (不含 3B header), 实际 wire bytes 需加上 header。

---

## 2. 生产场景带宽估算 (1000 人同时在线)

### 2.1 默认参数

- 帧率: **20 fps** (50ms/帧)
- 输入大小: **32 bytes** (典型游戏输入: 位置+方向+按键)
- 心跳: 3 秒/次, 3 bytes (带宽可忽略)

### 2.2 Active 场景 (所有玩家持续发送输入, D=32)

| 场景 | 房间数 | 人/房 | 总人数 | 单客户端 ↓ | 单客户端 ↑ | 服务器总 ↓ (出) | 服务器总 ↑ (入) |
|------|--------|-------|--------|-----------|-----------|----------------|----------------|
| A | 250 | 4 | 1000 | **3.14 KB/s** | 0.68 KB/s | **3.22 MB/s** | 0.70 MB/s |
| B | 100 | 10 | 1000 | **7.60 KB/s** | 0.68 KB/s | **7.78 MB/s** | 0.70 MB/s |
| C | 10 | 100 | 1000 | **74.39 KB/s** | 0.68 KB/s | **76.18 MB/s** | 0.70 MB/s |

**关键发现: 上传带宽与房间大小无关, 下载带宽与房间人数成正比。**

### 2.3 Idle 场景 (所有玩家发送 0 字节输入, D=0)

| 场景 | 房间数 | 人/房 | 总人数 | 单客户端 ↓ | 单客户端 ↑ | 服务器总 ↓ (出) | 服务器总 ↑ (入) |
|------|--------|-------|--------|-----------|-----------|----------------|----------------|
| A-idle | 250 | 4 | 1000 | **0.64 KB/s** | 0.06 KB/s | **0.66 MB/s** | 0.06 MB/s |
| B-idle | 100 | 10 | 1000 | **1.35 KB/s** | 0.06 KB/s | **1.38 MB/s** | 0.06 MB/s |
| C-idle | 10 | 100 | 1000 | **11.89 KB/s** | 0.06 KB/s | **12.18 MB/s** | 0.06 MB/s |

### 2.4 理论值推导

以场景 A (4 人房, D=32) 为例:

```
S→C 每帧 = 9 + 4 × (6 + 32) = 9 + 152 = 161 bytes
S→C 每秒 = 161 × 20 = 3,220 B/s = 3.14 KB/s ✓

C→S 每帧 = 3 + 32 = 35 bytes
C→S 每秒 = 35 × 20 = 700 B/s = 0.68 KB/s ✓

服务器总出 = 3,220 × 1000 = 3,220,000 B/s = 3.22 MB/s ✓
```

---

## 3. 压力测试校准

### 3.1 测试环境

- **机器**: macOS Darwin 25.3.0, 11 Core CPU
- **工具**: `svr/cmd/stress` (嵌入式 TCP 服务器 + 模拟客户端)
- **持续时间**: 10s (避免 15s snapshot pause)
- **统计口径**: 完整 wire bytes (含 3B Core header)

### 3.2 理论 vs 实测对照

| 场景 | 指标 | 理论值 | 实测值 | 偏差 |
|------|------|--------|--------|------|
| **A (250×4, D=32)** | 客户端 ↓ | 3.14 KB/s | 3.14 KB/s | **0.0%** |
| | 客户端 ↑ | 0.68 KB/s | 0.68 KB/s | **0.0%** |
| | 服务器总 ↓ | 3.22 MB/s | 3.22 MB/s | **0.0%** |
| | 服务器总 ↑ | 0.70 MB/s | 0.70 MB/s | **0.0%** |
| **A-idle (250×4, D=0)** | 客户端 ↓ | 0.64 KB/s | 0.64 KB/s | **0.0%** |
| | 客户端 ↑ | 0.06 KB/s | 0.06 KB/s | **0.0%** |
| | 服务器总 ↓ | 0.66 MB/s | 0.66 MB/s | **0.0%** |
| **B (100×10, D=32)** | 客户端 ↓ | 7.60 KB/s | 7.60 KB/s | **0.0%** |
| | 服务器总 ↓ | 7.78 MB/s | 7.78 MB/s | **0.0%** |
| **B-idle (100×10, D=0)** | 客户端 ↓ | 1.35 KB/s | 1.35 KB/s | **0.0%** |
| | 服务器总 ↓ | 1.38 MB/s | 1.38 MB/s | **0.0%** |
| **C (10×100, D=32)** | 客户端 ↓ | 74.39 KB/s | 74.38 KB/s | **<0.1%** |
| | 服务器总 ↓ | 76.18 MB/s | 76.17 MB/s | **<0.1%** |
| **C-idle (10×100, D=0)** | 客户端 ↓ | 11.89 KB/s | 11.89 KB/s | **0.0%** |
| | 服务器总 ↓ | 12.18 MB/s | 12.18 MB/s | **0.0%** |

**校准结论: 理论公式与实测偏差 < 0.1%, 带宽模型完全可靠。**

### 3.3 完整测试日志

<details>
<summary>场景 A: 250 rooms × 4 players, D=32 (点击展开)</summary>

```
Frames received:     200015 total (20002/s)
Inputs sent:         200003 total (20000/s)
Upload (all):        0.70 MB/s
Download (all):      3.22 MB/s
Per client upload:   0.68 KB/s
Per client download: 3.14 KB/s
Heap in use: 43.47 MB | GC: 8 | Goroutines: 252
```
</details>

<details>
<summary>场景 A-idle: 250 rooms × 4 players, D=0</summary>

```
Frames received:     199852 total (19985/s)
Inputs sent:         199868 total (19987/s)
Upload (all):        0.06 MB/s
Download (all):      0.66 MB/s
Per client upload:   0.06 KB/s
Per client download: 0.64 KB/s
Heap in use: 27.66 MB | GC: 7 | Goroutines: 252
```
</details>

<details>
<summary>场景 B: 100 rooms × 10 players, D=32</summary>

```
Frames received:     200019 total (20002/s)
Inputs sent:         200000 total (20000/s)
Upload (all):        0.70 MB/s
Download (all):      7.78 MB/s
Per client upload:   0.68 KB/s
Per client download: 7.60 KB/s
Heap in use: 53.48 MB | GC: 10 | Goroutines: 102
```
</details>

<details>
<summary>场景 B-idle: 100 rooms × 10 players, D=0</summary>

```
Frames received:     200060 total (20006/s)
Inputs sent:         200001 total (20000/s)
Upload (all):        0.06 MB/s
Download (all):      1.38 MB/s
Per client upload:   0.06 KB/s
Per client download: 1.35 KB/s
Heap in use: 30.36 MB | GC: 8 | Goroutines: 102
```
</details>

<details>
<summary>场景 C: 10 rooms × 100 players, D=32</summary>

```
Frames received:     200016 total (20002/s)
Inputs sent:         200000 total (20000/s)
Upload (all):        0.70 MB/s
Download (all):      76.17 MB/s
Per client upload:   0.68 KB/s
Per client download: 74.38 KB/s
Heap in use: 38.36 MB | GC: 24 | Goroutines: 12
```
</details>

<details>
<summary>场景 C-idle: 10 rooms × 100 players, D=0</summary>

```
Frames received:     200000 total (20000/s)
Inputs sent:         200000 total (20000/s)
Upload (all):        0.06 MB/s
Download (all):      12.18 MB/s
Per client upload:   0.06 KB/s
Per client download: 11.89 KB/s
Heap in use: 29.19 MB | GC: 11 | Goroutines: 12
```
</details>

---

## 4. 服务器资源消耗

| 场景 | Heap 使用 | GC 次数 (10s) | Goroutines |
|------|-----------|---------------|------------|
| A (250×4) | 43 MB | 8 | 252 |
| A-idle | 28 MB | 7 | 252 |
| B (100×10) | 53 MB | 10 | 102 |
| C (10×100) | 38 MB | 24 | 12 |

**发现: Goroutines 数 ≈ 房间数 + 少量管理协程。** 250 房间 = 252 goroutines, 100 房间 = 102, 10 房间 = 12。

---

## 5. 生产环境规划建议

### 5.1 云服务器带宽选型 (1000 人)

| 房间模式 | 推荐带宽 (出) | 月流量估算 (出) | 典型云服务器 |
|----------|--------------|----------------|-------------|
| 4 人房 (Active) | 5 Mbps | ~1 TB/月 | 腾讯云 5M 轻量 |
| 10 人房 (Active) | 10 Mbps | ~2.4 TB/月 | 腾讯云 10M 标准 |
| 100 人房 (Active) | 100 Mbps | ~24 TB/月 | 专用服务器 / 按量计费 |

> 计算基准: `服务器总出带宽 × 8 bits / 1,000,000 = Mbps`, 假设 50% 活跃率

### 5.2 瓶颈分析

```
                    4人房        10人房       100人房
                    ─────        ──────       ───────
服务器出带宽        3.2 MB/s     7.8 MB/s     76 MB/s      ← 主要瓶颈
服务器入带宽        0.7 MB/s     0.7 MB/s     0.7 MB/s     ← 与房间大小无关
服务器内存          ~43 MB       ~53 MB       ~38 MB       ← 非瓶颈
Goroutines         ~252         ~102         ~12          ← 与房间数成正比
```

**核心结论: 出带宽是唯一瓶颈, 且与 `N × 总人数` 成正比。**

### 5.3 优化建议 (优先级排序)

| 优先级 | 优化项 | 预期效果 | 实现难度 |
|--------|--------|----------|----------|
| P0 | **空帧抑制**: 无输入时不广播 | idle 带宽降至 ~0 | 低 |
| P1 | **输入压缩**: 对 PlayerInput.Data 做 delta 编码 | 减少 30-50% 数据量 | 中 |
| P2 | **降帧率广播**: idle 时降到 5fps | idle 带宽降 75% | 低 |
| P3 | **大房间分区**: 100 人房间按 AOI 只广播可见玩家 | 大房间带宽降 80%+ | 高 |
| P4 | **输入预测**: 重复输入不重传 | 减少 C→S 和帧 payload | 中 |

### 5.4 Dashboard 统计与实际 Wire 的差异

Dashboard Game Traffic 显示 **payload only** (不含 header):

| 指标 | Dashboard 显示 | 实际 Wire |
|------|---------------|-----------|
| 空帧 S→C | 6 B/frame | 9 B/frame |
| 满帧 S→C (4人,D=32) | 158 B/frame | 161 B/frame |
| 心跳 C→S | 0 B | 3 B |
| 差异比例 | | ~2-3% 低估 |

---

## 6. 快速参考卡

### 单客户端带宽速算

```
下载 KB/s ≈ [9 + N × (6 + D)] × F / 1024

  4人房, D=32:   3.1 KB/s
  10人房, D=32:  7.6 KB/s
  100人房, D=32: 74.4 KB/s

上传 KB/s ≈ (3 + D) × F / 1024

  D=32: 0.68 KB/s (与房间大小无关)
```

### 服务器总出带宽速算

```
总出 MB/s ≈ 单客户端下载 KB/s × 总人数 / 1024

  1000人, 4人房:   3.1 MB/s
  1000人, 10人房:  7.6 MB/s
  1000人, 100人房: 74.4 MB/s
```
