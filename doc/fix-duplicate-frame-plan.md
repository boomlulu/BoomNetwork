# DuplicateFrame 修复方案：连接级 Gate + 客户端防御兜底

**Status:** 部分完成（客户端路径已修复；服务端 Gate 方案未执行，被替代方案覆盖）  
**Date:** 2026-04-11 | **Updated:** 2026-04-13  
**根因:** 重连期间客户端跨 attempt 帧号未同步，导致 attempt 2 触发服务器重放已收帧，客户端报 `DuplicateFrame` 致命错误。

**2026-04-13 实际修复路径（与本方案不同）：**
- 客户端 `ConnectionManager.cs`（commit 8acbc5c）：添加 `_activeContext` 同步 `UpdateFrameNumber()`，跨 attempt 帧号问题在源头解决
- 服务端 `session_store.go`（commit 85b59ba）：修复 `byPlayer` 提前删除导致所有重连瞬间失败
- 客户端架构重构（commit 5a26a9d）：`ReconnectContext` → `ReconnectState + ReconnectOutcome`，彻底消除帧号过期问题
- 本方案的服务端 Gate（`replayGate` / `pendingFrames`）**未执行**，客户端兜底降级（Section 3.2）亦**未执行**，因根因已在更上游修复

**原始根因分析（保留供参考）：** 重连期间 `PlayerReplaying=true` 时，服务器 `Room.BroadcastFrame` 仍向该连接推送 live 帧，导致同一帧号通过 replay 和 live 两条路径投递，客户端触发 `DuplicateFrame` 致命错误。

---

## 一、修改总览

| 层 | 文件 | 改动性质 |
|----|------|----------|
| Go 服务端 | `svr/framesync/room.go` | 新增 `replayGate` + `pendingFrames` 字段、广播过滤、replay 流程改造 |
| Go 服务端 | `svr/framesync/player.go`（或 player 所在文件） | `PlayerConn` 结构体新增字段和方法 |
| C# 客户端 | `Runtime/Client/FrameSync/FrameSyncClient.cs` | `HandlePushFrames` 第 807-815 行降级为 warn |

---

## 二、服务端改动（Go）

### 2.1 定位文件

```
/Users/boom/Demo/BoomNetwork/svr/framesync/
```

找到以下关键结构体和函数（具体文件名根据实际代码，通常是 `room.go` 和 `player.go`）：

- `Room` 结构体（含 `frameBuffer []*CachedFrame`）
- `PlayerConn`（或 `Player`、`RoomPlayer`）结构体 — 代表房间内的一个玩家连接
- `Room.broadcastFrame()` / `Room.tick()` — 20fps 广播循环中调用的帧推送函数
- `handleReconnect()` — 消息路由 `router.On(CmdReconnect, handleReconnect)` 的处理函数

### 2.2 PlayerConn 新增字段

在代表房间内玩家连接的结构体上添加：

```go
type PlayerConn struct {
    // ... 现有字段 ...

    // ===== DuplicateFrame Fix: Replay Gate =====
    replayGate    atomic.Bool        // true = 正在 replay，live 广播跳过此连接
    pendingFrames []*CachedFrame     // gate 期间积攒的 live 帧（有界）
    pendingMu     sync.Mutex         // 保护 pendingFrames
    resumeFrame   uint32             // replay 最后一帧的帧号，flush 时跳过 <= 此值的帧
}

const maxPendingFrames = 500 // gate 期间最多缓存 500 帧（25 秒），超限 → 降级快照重连
```

### 2.3 PlayerConn 新增方法

```go
// SetReplayGate 开启/关闭 gate
func (pc *PlayerConn) SetReplayGate(on bool) {
    pc.replayGate.Store(on)
    if on {
        pc.pendingMu.Lock()
        pc.pendingFrames = pc.pendingFrames[:0] // 清空旧数据
        pc.pendingMu.Unlock()
    }
}

// EnqueuePendingFrame 在 gate 开启期间由广播循环调用
// 返回 false 表示缓冲区已满，调用方应降级处理
func (pc *PlayerConn) EnqueuePendingFrame(frame *CachedFrame) bool {
    pc.pendingMu.Lock()
    defer pc.pendingMu.Unlock()
    if len(pc.pendingFrames) >= maxPendingFrames {
        return false // 缓冲区满
    }
    pc.pendingFrames = append(pc.pendingFrames, frame)
    return true
}

// FlushPendingFrames 在 replay 完成后调用，发送 > resumeFrame 的所有积攒帧
func (pc *PlayerConn) FlushPendingFrames() {
    pc.pendingMu.Lock()
    frames := make([]*CachedFrame, len(pc.pendingFrames))
    copy(frames, pc.pendingFrames)
    pc.pendingFrames = pc.pendingFrames[:0]
    pc.pendingMu.Unlock()

    for _, f := range frames {
        if f.FrameNumber > pc.resumeFrame {
            pc.Send(CmdPushFrames, f.Encoded)
            pc.resumeFrame = f.FrameNumber
        }
    }
}
```

### 2.4 Room.BroadcastFrame 增加 Gate 检查

找到广播循环中遍历所有玩家发送帧的代码，改为：

```go
func (r *Room) broadcastFrame(frame *CachedFrame) {
    for _, pc := range r.players {
        if pc == nil || !pc.IsOnline() {
            continue
        }

        // ===== DuplicateFrame Fix: gate 检查 =====
        if pc.replayGate.Load() {
            if !pc.EnqueuePendingFrame(frame) {
                // pending 缓冲区满 → 该玩家 replay 太慢，强制中断 replay
                // 后续该玩家会收到 SnapshotStale/断线，由客户端重走快照重连
                pc.SetReplayGate(false)
                log.Printf("[Room %d] Player %d pending buffer overflow, gate released", r.id, pc.PlayerId)
            }
            continue // 不发 live 帧
        }

        pc.Send(CmdPushFrames, frame.Encoded)
    }
}
```

### 2.5 handleReconnect 改造

找到 `handleReconnect`（对应 `router.On(CmdReconnect, handleReconnect)`），在向重连玩家补发帧之前开启 gate，补发完毕后关闭：

```go
func handleReconnect(sess *Session, msg *Message) {
    // ... 解析 playerId, lastFrame, lastS2CSeq ...
    // ... 查找 room 和 playerConn ...

    pc := room.FindPlayer(playerId)
    if pc == nil {
        // 发送 ReconnectRsp(Fail)
        return
    }

    // ===== DuplicateFrame Fix: 开启 gate =====
    pc.SetReplayGate(true)
    pc.resumeFrame = 0 // 重置

    // 判断走快速重连还是快照重连
    if isQuickReconnect(lastFrame, room) {
        // --- 快速重连路径 ---
        replayFrames := room.GetFramesFrom(lastFrame + 1)

        // 补发帧（在当前 goroutine 同步执行，因为帧数据已在内存）
        for _, f := range replayFrames {
            pc.Send(CmdPushFrames, f.Encoded)
            pc.resumeFrame = f.FrameNumber
        }

        // 发送 ReconnectRsp(Success, serverFrame, ...)
        sendReconnectRsp(sess, ReconnectResult_Success, room, pc)

        // 补发完毕 → flush pending → 关闭 gate
        pc.FlushPendingFrames()
        pc.SetReplayGate(false)

    } else {
        // --- 快照重连路径 ---
        snapshotData, snapshotFrame := room.GetSnapshot()

        // 发送 ReconnectRsp(Success, serverFrame, snapshotFrame, snapshotData)
        sendReconnectRspWithSnapshot(sess, room, pc, snapshotFrame, snapshotData)

        // 快照重连：客户端从 snapshotFrame 开始，补发 snapshotFrame 之后的缓冲帧
        replayFrames := room.GetFramesFrom(snapshotFrame + 1)
        for _, f := range replayFrames {
            pc.Send(CmdPushFrames, f.Encoded)
            pc.resumeFrame = f.FrameNumber
        }

        // flush pending → 关闭 gate
        pc.FlushPendingFrames()
        pc.SetReplayGate(false)
    }
}
```

> **关键不变量:** `SetReplayGate(true)` 必须在任何补发帧操作之前；`SetReplayGate(false)` 必须在 `FlushPendingFrames()` 之后。三步顺序不可打乱。

### 2.6 并发安全分析

| 操作 | 执行线程 | 访问字段 | 同步机制 |
|------|----------|----------|----------|
| `replayGate.Load()` | 广播 goroutine (Room.tick) | `replayGate` | `atomic.Bool` 无锁读 |
| `replayGate.Store()` | 重连 handler goroutine | `replayGate` | `atomic.Bool` 无锁写 |
| `EnqueuePendingFrame` | 广播 goroutine | `pendingFrames` | `pendingMu` 互斥 |
| `FlushPendingFrames` | 重连 handler goroutine | `pendingFrames` | `pendingMu` 互斥 |
| `pc.Send()` | 两个 goroutine 都调用 | 连接写缓冲 | 依赖现有 `Send` 的线程安全性 |

> **前提：** 现有的 `pc.Send()` 必须已经是线程安全的（通常通过写缓冲队列或写锁实现）。如果不是，需要额外加锁。

### 2.7 Gate 期间的边界情况处理

| 场景 | 处理 |
|------|------|
| replay 中玩家再次断线 | `handleDisconnect` 应调用 `pc.SetReplayGate(false)` 清理状态 |
| replay 中玩家主动 LeaveRoom | `handleLeaveRoom` 应调用 `pc.SetReplayGate(false)` |
| pendingFrames 溢出 | `EnqueuePendingFrame` 返回 false → 释放 gate → 玩家此后收到的帧号跳跃 → 客户端触发帧间隙错误 → 重新走完整重连 |
| Room 被销毁 | Room 销毁时所有 PlayerConn 被清理，gate 自然失效 |

---

## 三、客户端改动（C#）

### 3.1 定位文件

```
com.boom.boomnetwork@7b5bdaf0db/Runtime/Client/FrameSync/FrameSyncClient.cs
```

### 3.2 修改 HandlePushFrames（第 807-815 行）

**修改前（当前代码）：**

```csharp
// 第 807-815 行
if (frame.FrameNumber <= LastFrameNumber)
{
    var err = new NetworkError(ErrorCode.DuplicateFrame,
        $"Duplicate frame received: frame={frame.FrameNumber} lastFrame={LastFrameNumber}. " +
        "Live feed and replay delivered the same frame simultaneously.");
    Log($"[FATAL] {err}");
    OnError?.Invoke(err);
    HandleStopFrameSync();
    return;
}
```

**修改后：**

```csharp
// 第 807-815 行 — 降级为 warn，静默丢弃
if (frame.FrameNumber <= LastFrameNumber)
{
    Log($"[WARN] Duplicate frame dropped: frame={frame.FrameNumber} lastFrame={LastFrameNumber}");
    return;  // 静默丢弃，不中断帧同步
}
```

### 3.3 改动说明

- 移除 `OnError?.Invoke(err)` — 不再向游戏层报告致命错误
- 移除 `HandleStopFrameSync()` — 不再中断帧同步
- 保留 `Log` 输出（降级为 WARN）— 便于排查，确认服务端 gate 是否生效
- 保留 `return` — 丢弃重复帧，不处理

> **为什么需要这个兜底？** 服务端 gate 机制在 99.9% 的情况下会消除重复帧。但在极端并发时序下（gate 设置和广播发送之间的微小窗口），仍可能有 1 帧泄漏。客户端降级为 warn 而非 fatal 可以保证游戏不崩。

---

## 四、不需要改动的部分

| 组件 | 原因 |
|------|------|
| `QuickReconnectStrategy.cs` | 客户端侧重连策略不变，只是发 Reconnect 请求 |
| `SnapshotReconnectStrategy.cs` | 同上 |
| `ConnectionManager.cs` | 连接状态机不变 |
| `FrameSyncProtocol.cs` | 协议不变，不加新 Cmd |
| `DemoManagerBase.cs` | 游戏层 `_lastProcessedFrame` 去重保留作为第三层防御 |

---

## 五、验收测试矩阵

### 5.1 测试环境准备

```bash
# 启动服务器
cd /Users/boom/Demo/BoomNetwork/svr
go run ./cmd/framesync/ -addr=:9000 -proto=tcp -ppr=2

# 客户端使用 Demo01（2人匹配，帧同步移动方块）
```

### 5.2 功能测试用例

#### TC-1: 正常游戏 — 无回归

| 项 | 内容 |
|----|------|
| **前置** | 2 名玩家加入房间，帧同步运行中 |
| **操作** | 双方持续发送移动输入 60 秒 |
| **预期** | 无 `[WARN] Duplicate frame`、无 `[FATAL]`、帧号严格递增 |
| **验证** | 客户端日志 grep `Duplicate`，结果为空 |

#### TC-2: 快速重连 — Gate 有效

| 项 | 内容 |
|----|------|
| **前置** | 2 名玩家帧同步中，帧号 > 200 |
| **操作** | 玩家 A 调用 `SimulateNetworkDrop()`，等待自动重连成功 |
| **预期** | 重连成功，客户端日志无 `Duplicate frame`，帧号连续 |
| **验证** | 1) 客户端日志：`QuickReconnect` 成功 2) 无 WARN/FATAL 3) 玩家 B 看到玩家 A 恢复移动，无闪烁 |

#### TC-3: 快照重连 — Gate 有效

| 项 | 内容 |
|----|------|
| **前置** | 2 名玩家帧同步中，帧号 > 200 |
| **操作** | 玩家 A 调用 `SimulateNetworkDropAndPause()`，等待 15 秒（超过帧缓冲 10 秒），调用 `ResumeReconnect()` |
| **预期** | 走 SnapshotReconnect 路径成功，客户端日志无 `Duplicate frame` |
| **验证** | 1) 客户端日志：`SnapshotReconnect` 成功 2) `Snapshot restored` 日志出现 3) 无 WARN/FATAL |

#### TC-4: 重连中再次断线 — Gate 清理

| 项 | 内容 |
|----|------|
| **前置** | 2 名玩家帧同步中 |
| **操作** | 玩家 A 断线 → 重连过程中再次断开网络 → 等待第二次重连完成 |
| **预期** | gate 被正确清理，最终重连成功，无残留 gate 导致的帧丢失 |
| **验证** | 1) 最终帧号连续 2) 无永久性帧暂停 3) 游戏正常运行 |

#### TC-5: 高频重连压测 — Gate 无泄漏

| 项 | 内容 |
|----|------|
| **前置** | 2 名玩家帧同步中 |
| **操作** | 玩家 A 循环执行 10 次：`SimulateNetworkDrop()` → 等待重连 → 等 3 秒 |
| **预期** | 每次重连均成功，gate 计数最终归零 |
| **验证** | 1) 服务端日志：每次 `replayGate=true` 后都有对应 `replayGate=false` 2) 无 `pending buffer overflow` 3) 客户端最终帧号正确 |

#### TC-6: 客户端兜底验证（故意不修服务端时）

| 项 | 内容 |
|----|------|
| **前置** | 仅应用客户端改动（不应用服务端 gate），2 名玩家帧同步中 |
| **操作** | 玩家 A 断线重连 |
| **预期** | 客户端日志出现 `[WARN] Duplicate frame dropped`，但游戏不崩、帧同步继续 |
| **验证** | 1) 有 WARN 日志 2) 无 FATAL 3) `HandleStopFrameSync` 未被调用 4) 游戏可继续 |

### 5.3 性能测试

#### TC-7: Gate 对广播延迟的影响

| 项 | 内容 |
|----|------|
| **操作** | 使用 `go run ./cmd/stress/ -rooms=750 -players=4 -duration=30s` |
| **预期** | `atomic.Bool.Load()` 在广播热路径上增加 < 1ns/player，总体广播延迟无可测量变化 |
| **验证** | `./bench.sh` 结果与改动前对比，P99 帧推送延迟偏差 < 5% |

#### TC-8: Pending Buffer 内存上限

| 项 | 内容 |
|----|------|
| **操作** | 在重连 handler 中故意 `time.Sleep(30s)` 模拟超长 replay |
| **预期** | pendingFrames 达到 500 帧上限后 gate 被释放，`pending buffer overflow` 日志出现 |
| **验证** | 1) 服务端日志出现 overflow 提示 2) 内存未持续增长 3) 玩家最终通过重新重连恢复 |

### 5.4 自动化测试集成

在现有 `test.sh` 中添加测试项：

```bash
# TC-9: 自动化重连去重测试
echo "=== Test: Reconnect no DuplicateFrame ==="
# 启动服务器 → 启动 2 个 Go stress 客户端 → 运行 5 秒 → 
# 断开客户端 1 → 等待重连 → 检查客户端日志无 DuplicateFrame → 
# 断开客户端 1 并等待 15 秒（快照重连）→ 恢复 → 检查日志
```

---

## 六、服务端可观测性（建议同步添加）

在服务端添加以下日志/指标，方便生产监控：

```go
// 日志（在 handleReconnect 中）
log.Printf("[Room %d] Player %d replay gate ON (lastFrame=%d)", room.id, pc.PlayerId, lastFrame)
log.Printf("[Room %d] Player %d replayed %d frames [%d..%d]", room.id, pc.PlayerId, count, startFrame, endFrame)
log.Printf("[Room %d] Player %d flushed %d pending frames", room.id, pc.PlayerId, flushedCount)
log.Printf("[Room %d] Player %d replay gate OFF", room.id, pc.PlayerId)

// 指标（如果有 metrics 系统）
metrics.Gauge("replay_gate_active_count", activeGateCount)
metrics.Histogram("replay_pending_high_watermark", len(pc.pendingFrames))
metrics.Histogram("replay_duration_ms", replayDurationMs)
metrics.Counter("replay_pending_overflow_total", 1)  // 溢出计数
```

---

## 七、回滚方案

| 风险 | 回滚操作 |
|------|----------|
| Gate 导致帧丢失（逻辑 bug） | 服务端回滚 gate 代码，客户端 WARN 兜底会捕获重复帧 |
| Pending buffer 内存问题 | 降低 `maxPendingFrames` 到 200，或临时关闭 gate（设 `replayGate` 永远 false） |
| 客户端降级影响判断 | 客户端还原为 FATAL（恢复原代码） |

**最小风险部署顺序：** 先部署客户端（C# WARN 兜底）→ 观察 1 天 → 再部署服务端（Go Gate）→ 观察 WARN 日志是否归零。

---

## 八、执行 Checklist

- [ ] **服务端** — `PlayerConn` 新增 `replayGate`, `pendingFrames`, `pendingMu`, `resumeFrame` 字段
- [ ] **服务端** — `PlayerConn` 新增 `SetReplayGate()`, `EnqueuePendingFrame()`, `FlushPendingFrames()` 方法
- [ ] **服务端** — `Room.broadcastFrame()` 增加 gate 检查分支
- [ ] **服务端** — `handleReconnect()` 改造：gate ON → replay → flush → gate OFF
- [ ] **服务端** — `handleDisconnect()` / `handleLeaveRoom()` 增加 `SetReplayGate(false)` 清理
- [ ] **服务端** — 添加日志：gate ON/OFF、replay 帧范围、flush 帧数、overflow 告警
- [ ] **服务端** — `go test ./framesync/...` 通过
- [ ] **客户端** — `FrameSyncClient.cs` 第 807-815 行替换为 WARN + return
- [ ] **联调** — 运行 TC-1 ~ TC-8 全部通过
- [ ] **自动化** — TC-9 集成到 `test.sh`
- [ ] **性能** — `bench.sh` 对比无退化
- [ ] **部署** — 先客户端后服务端，分阶段上线
