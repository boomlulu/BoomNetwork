# 帧同步统一投递架构 — 从根本上消除 Replay/实时帧竞态

**日期**: 2026-04-10
**Status**: Proposed
**前置文档**: desync_duplicate_frame_4003_v2.md

---

## 1. 问题本质

v2 方案用 `PlayerReplaying` 状态关闭了竞争窗口，是正确且高效的短期修复。但从架构角度看，真正的问题不是缺少一个状态值，而是**帧投递存在两条独立的写入路径**：

```
路径 A: stepFrame() ticker → broadcastSlice → conn.Send()
路径 B: replayGoroutine()  → 遍历历史帧   → conn.Send()
```

两条路径各自独立地向同一个 conn 写入，靠"调用方保证时序互斥"来避免冲突。这种架构下，每新增一条写入路径（比如未来加 reliable 消息补发、服务端回放、旁观者快进），都要重新审视所有路径的时序关系。复杂度是 O(n²)——n 条路径需要 n(n-1)/2 对互斥保证。

---

## 2. 架构目标

| 目标 | 说明 |
|------|------|
| 单写者原则 | 每个玩家的 conn 只有一个 goroutine 在写，从设计上消除竞态 |
| 统一投递模型 | replay 和实时帧不再是两条路径，而是同一个投递循环的不同阶段 |
| 生命周期可控 | 玩家断线时，投递 goroutine 被确定性地取消，不存在泄漏 |
| 背压感知 | 慢客户端不会导致无界内存增长或阻塞全局 tick |
| 向后兼容 | 客户端协议不变，仍然收到 CmdPushFrames |

---

## 3. 核心设计：Per-Player Delivery Loop

### 3.1 概念模型

抛弃"replay 是一个独立阶段"的心智模型。每个玩家只有一个概念：

> **我还没看到的下一帧是几号？从那里开始，把所有帧按顺序发给我。**

```
              ┌────────────────────────────────────────────┐
              │           Room Frame Buffer                │
              │  [frame 1] [frame 2] ... [frame N] [live]  │
              └──────────────────┬─────────────────────────┘
                                 │
                    ┌────────────┼────────────┐
                    ▼            ▼            ▼
              ┌──────────┐ ┌──────────┐ ┌──────────┐
              │ Player A │ │ Player B │ │ Player C │
              │ cursor=N │ │ cursor=1 │ │ cursor=50│
              │ (live)   │ │ (replay) │ │ (catch-up)│
              └────┬─────┘ └────┬─────┘ └────┬─────┘
                   │            │            │
                   ▼            ▼            ▼
              delivery()   delivery()   delivery()
              单 goroutine  单 goroutine  单 goroutine
```

"replay" 和 "实时" 的区别只是 cursor 的起始位置不同，投递逻辑完全相同。

### 3.2 Player 结构

```go
type Player struct {
    ID        int32
    Conn      PlayerConn
    State     PlayerState  // Online / Disconnected（不再需要 Replaying）

    // 投递相关
    cursor    int64           // 下一帧编号，原子读写
    frameCh   chan *FrameData // 有界 channel，stepFrame 向此投递
    cancelFn  context.CancelFunc
}
```

### 3.3 Delivery Loop（每个玩家一个 goroutine）

```go
func (r *Room) deliveryLoop(ctx context.Context, p *Player) {
    defer r.onDeliveryExit(p)

    // ── 阶段 1: 追帧（原来的 "replay"）──
    // 从 cursor 开始，批量读 frame buffer 中的历史帧并发送
    catchUpTo := atomic.LoadInt64(&r.currentFrame)
    if p.cursor < catchUpTo {
        frames := r.getFrameRange(p.cursor, catchUpTo)
        for i, f := range frames {
            select {
            case <-ctx.Done():
                return
            default:
            }
            if err := p.Conn.Send(f.EncodedData); err != nil {
                return // conn 断开，退出
            }
            p.cursor = f.FrameID + 1

            // 批量节奏控制
            if (i+1)%replayBatchSize == 0 && i+1 < len(frames) {
                select {
                case <-ctx.Done():
                    return
                case <-time.After(replayBatchDelay):
                }
            }
        }
    }

    // ── 阶段 2: 实时（原来的 "broadcast"）──
    // 从 channel 中逐帧读取并发送，天然有序
    for {
        select {
        case <-ctx.Done():
            return
        case frame, ok := <-p.frameCh:
            if !ok {
                return // channel 关闭，房间销毁
            }
            if err := p.Conn.Send(frame.EncodedData); err != nil {
                return
            }
            p.cursor = frame.FrameID + 1
        }
    }
}
```

**关键特性**：阶段 1 和阶段 2 是同一个 goroutine 内的顺序执行，不存在两个 writer 并发写 conn 的可能。阶段 1 结束后自然流入阶段 2，没有状态切换的原子性问题。

### 3.4 stepFrame() 改造

```go
func (r *Room) stepFrame() {
    // ... 生成 frame，写入 buffer（不变） ...

    r.mu.RLock()
    for _, p := range r.players {
        if p.State == PlayerOnline && p.frameCh != nil {
            select {
            case p.frameCh <- frame:
                // 投递成功
            default:
                // channel 满 → 客户端太慢，标记需要踢出或降级
                r.markSlowClient(p)
            }
        }
    }
    r.mu.RUnlock()
}
```

`stepFrame()` 不再直接写 conn，只负责向 channel 投递。写 conn 的工作完全由 `deliveryLoop` 承担。

### 3.5 AddPlayer / 重连流程

```go
func (r *Room) AddPlayer(id int32, conn PlayerConn, startFrame int64) {
    r.mu.Lock()
    // ... 常规 player 初始化 ...

    p.Conn = conn
    p.State = PlayerOnline
    p.cursor = startFrame
    p.frameCh = make(chan *FrameData, frameChBuffer) // 有界 buffer，如 64

    ctx, cancel := context.WithCancel(r.ctx)
    p.cancelFn = cancel

    r.mu.Unlock()

    go r.deliveryLoop(ctx, p)
}
```

不再需要 `replaying` 参数。不再需要 `SetPlayerLive`。AddPlayer 之后 `deliveryLoop` 自动从 `startFrame` 开始追帧，追完自动切到实时。

### 3.6 断线处理

```go
func (r *Room) DisconnectPlayer(id int32) {
    r.mu.Lock()
    p, ok := r.players[id]
    if !ok {
        r.mu.Unlock()
        return
    }
    p.State = PlayerDisconnected

    // 取消 delivery goroutine，确定性退出
    if p.cancelFn != nil {
        p.cancelFn()
        p.cancelFn = nil
    }

    // 关闭 channel，防止 stepFrame 继续投递
    if p.frameCh != nil {
        close(p.frameCh)
        p.frameCh = nil
    }

    r.mu.Unlock()
}
```

断线时 `cancelFn()` 保证 `deliveryLoop` 一定退出，没有 goroutine 泄漏。重连时创建新的 `deliveryLoop`，旧的已经被取消，不会出现"两个 replay goroutine 同时跑"的问题。

---

## 4. Frame Buffer 设计

`deliveryLoop` 的阶段 1 需要从 buffer 中随机读取历史帧。当前实现可能是数组或 slice，这里建议用 ring buffer：

```go
type FrameBuffer struct {
    mu     sync.RWMutex
    frames []FrameData   // 固定大小 ring buffer
    head   int64         // 最早可读帧号
    tail   int64         // 下一个写入位置
    cap    int64
}

func (b *FrameBuffer) GetRange(from, to int64) ([]FrameData, error) {
    b.mu.RLock()
    defer b.mu.RUnlock()

    if from < b.head {
        return nil, ErrFrameExpired // 太老了，已被覆盖
    }
    // ... 按 ring buffer 索引读取 ...
}
```

ring buffer 的好处：内存有界，不会因为长时间运行导致 frame slice 无限增长。如果客户端要从 buffer 已过期的帧开始 replay，说明断线太久，应该直接踢出重新加载。

---

## 5. 背压与慢客户端处理

当前架构中 `stepFrame()` 直接写 conn，如果 conn 的写缓冲满了，会阻塞整个 tick——一个慢客户端会拖慢所有人。

新架构中 `frameCh` 是有界 channel，`stepFrame()` 用 `select default` 非阻塞投递：

```
慢客户端 → frameCh 满 → stepFrame 投不进去 → markSlowClient
→ 给客户端一个宽限期（如 3 秒）
→ 仍然追不上 → 踢出或降级为旁观者
```

这比当前的"卡住整个 tick"或"无界 buffer OOM"都要好。

---

## 6. 与 v2 方案的对比

| 维度 | v2 (PlayerReplaying) | 本方案 (Unified Delivery) |
|------|---------------------|--------------------------|
| 改动量 | 极小（~30 行） | 中等（~200 行核心 + 调用方适配） |
| 解决的问题范围 | 仅解决 replay/实时重叠 | 消除整个"多 writer 竞态"类别 |
| 新增写入路径的成本 | 每条新路径都要审查互斥 | 只需往 frameCh 投递，天然安全 |
| goroutine 泄漏风险 | 依赖手动 SetPlayerLive | context cancel 确定性回收 |
| 二次断线重连 | 旧 goroutine 无取消机制 | cancelFn 保证旧 goroutine 退出 |
| 背压 | 无（直写 conn，可能阻塞 tick） | 有界 channel + 慢客户端检测 |
| 客户端改动 | 无 | 无 |
| 上线风险 | 低 | 中（需要更完整的测试） |

---

## 7. 迁移策略

建议分三步走，每步都可以独立上线和回滚：

### Phase 1: 先上 v2 修复（1-2 天）

v2 方案作为 hotfix 立即上线止血。它的改动小、风险低、效果确定。

### Phase 2: 引入 per-player channel + delivery loop（1 周）

- 新增 `Player.frameCh` 和 `deliveryLoop`
- `stepFrame()` 改为向 channel 投递而非直写 conn
- 保留 `PlayerReplaying` 状态作为 fallback flag
- **feature flag 控制**：可以按房间粒度灰度，对照组走旧路径

### Phase 3: 清理旧路径（Phase 2 稳定后）

- 移除 `PlayerReplaying` 状态（不再需要）
- 移除 `SetPlayerLive`
- 移除三条入口中的 replay goroutine（deliveryLoop 已接管）
- 引入 ring buffer 替换当前 frame 存储
- 添加慢客户端检测和踢出逻辑

---

## 8. 关键风险与缓解

### 风险 1: channel buffer 大小的调优

frameCh 太小会导致正常客户端偶尔被误判为"慢"；太大会增加内存消耗（每个玩家一个 buffer）。

**缓解**: 初始值设为 64（约 1 秒的帧数据），配合监控 channel 使用率，运行时可调。

### 风险 2: 阶段 1→阶段 2 过渡期的帧间隙

deliveryLoop 追完历史帧后进入 channel 读取，但在追帧期间 stepFrame 已经在往 channel 投递了。如果追帧的终点和 channel 中最早的帧之间有间隙，就会漏帧。

**缓解**: 追帧的终止条件不是"追到 currentFrame"，而是"追到 channel 中有数据为止"。具体实现：

```go
// 阶段 1 的退出条件
for p.cursor <= catchUpTo {
    // 发送历史帧...
}
// 此时 channel 中应该已经有 catchUpTo+1 及之后的帧
// 自然流入阶段 2 的 for-select
```

需要保证 `stepFrame` 在 `AddPlayer` 之后就开始往 channel 投递（即使玩家还在追帧阶段），这样 channel 中一定有接续的帧。

### 风险 3: 内存峰值

N 个玩家 × channel buffer 大小 × 帧数据大小。假设 100 人房间、channel 64、每帧 200 bytes → 100 × 64 × 200 = 1.25 MB，完全可接受。

### 风险 4: 改动面相对较大

涉及 `stepFrame()` 的核心广播逻辑改造，是帧同步最关键的热路径。

**缓解**: Phase 2 用 feature flag 灰度，先在测试房间验证，再逐步放量。保留旧路径至少一个版本周期。

---

## 9. 改动范围估算

```
svr/framesync/room.go
  + FrameBuffer ring buffer 实现
  + deliveryLoop()
  + onDeliveryExit()
  + markSlowClient()
  ~ Player struct: 增加 cursor, frameCh, cancelFn
  ~ AddPlayer(): 启动 deliveryLoop 替代外部 replay goroutine
  ~ DisconnectPlayer(): cancel + close channel
  ~ stepFrame(): 改为 channel 投递

svr/cmd/framesync/main.go
  ~ handleReconnect: 删除 replay goroutine，传 startFrame 给 AddPlayer
  ~ handleJoinRoom: 同上
  ~ handleMatchRoom: 同上
  ~ bindPlayerToRoom: 简化，不再需要 replaying 参数
  - 三条入口的 replay goroutine 代码删除

客户端: 零改动
协议: 零改动
```

---

## 10. 总结

v2 是正确的止血方案，应该立即上线。但长期来看，"单 writer per player"的统一投递架构才能从根本上消灭这一类竞态问题——不是修一个 bug，而是让这类 bug 在架构上无法发生。

核心理念只有一句话：**不要让两个 goroutine 写同一个 conn。**
