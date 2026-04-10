# BoomNetwork Desync 根因报告
## ErrorCode 4003 — DuplicateFrame

**日期**: 2026-04-10  
**触发错误**: `[VS][Desync] [NetworkError] [4003 DuplicateFrame] Duplicate frame received: frame=244 lastFrame=245. Live feed and replay delivered the same frame simultaneously.`  
**严重级别**: Fatal — 帧同步立即终止，全员 Desync

---

## 1. 触发路径（完整代码链路）

### 1.1 服务端：三条入口，同一个竞争窗口

三条进入运行中房间的路径均存在相同的竞争结构：

```
handleReconnect (main.go:642)
  └─ room.AddPlayer(playerId, conn)          ← 立即 State=PlayerOnline
  └─ sendMsg(conn, ReconnectRsp)
  └─ go func() {                             ← goroutine A：补帧
         GetFramesSince(replayFrom)
         for cf in frames:
             sendMsg(conn, CmdPushFrames)    ← 历史帧（unicast）
     }()

handleJoinRoom (main.go:778)
  └─ bindPlayerToRoom(playerId, conn, room)  ← 内部调用 AddPlayer → State=PlayerOnline
  └─ sendMsg(conn, JoinRoomRsp)
  └─ go func() {                             ← goroutine A：补帧（30s ctx 超时）
         sendMsg(conn, RoomSnapshot)
         sendMsg(conn, StartFrameSync)
         for cf in frames:
             sendMsg(conn, CmdPushFrames)    ← 历史帧（unicast）
     }()

handleMatchRoom (main.go:950)
  └─ bindPlayerToRoom(playerId, conn, room)  ← 同上，State=PlayerOnline
  └─ go func() {                             ← goroutine A：补帧（无超时保护）
         time.Sleep(10ms)
         for cf in frames:
             sendMsg(conn, CmdPushFrames)    ← 历史帧（unicast）
     }()
```

> `bindPlayerToRoom` → `room.AddPlayer` → `room.go:272`: `existing.State = PlayerOnline`  
> `room.go:277`: `player = &Player{..., State: PlayerOnline, ...}`

**关键事实**：`AddPlayer()` 在补帧 goroutine 启动**之前**就把玩家置为 `PlayerOnline`。

---

### 1.2 服务端：Live Ticker 广播

```go
// room.go:861 — stepFrame()，每 tick 执行（20fps = 50ms 一次）

r.broadcastSlice = r.broadcastSlice[:0]
for _, p := range r.players {
    if p.State == PlayerOnline && p.Conn != nil {   // ← 行 920
        r.broadcastSlice = append(r.broadcastSlice, p)
    }
}
// ...
msg := codec.NewCoreMessage(CmdPushFrames, r.frameBuf[:size])  // ← 行 955
for _, p := range r.broadcastSlice {
    c.Send(msg)   // ← broadcast，含刚重连/加入的玩家
}
```

由于 `AddPlayer` 已将玩家置为 `PlayerOnline`，下一个 ticker tick（最迟 50ms 后）就会把该玩家加入 `broadcastSlice`，开始发送实时帧。

---

### 1.3 客户端：单一入口，无路由区分

```csharp
// FrameSyncClient.cs:663-664
case FrameSyncCmd.PushFrames:
    HandlePushFrames(msg);   // ← 历史帧和实时帧走同一个 handler

// FrameSyncClient.cs:798-816
private void HandlePushFrames(Message msg)
{
    var frame = FrameDataCodec.Decode(msg.DataSpan);

    if (frame.FrameNumber <= LastFrameNumber)      // ← 行 807：单调性断言
    {
        var err = new NetworkError(ErrorCode.DuplicateFrame,
            $"Duplicate frame received: frame={frame.FrameNumber} lastFrame={LastFrameNumber}. " +
            "Live feed and replay delivered the same frame simultaneously.");
        Log($"[FATAL] {err}");
        OnError?.Invoke(err);
        HandleStopFrameSync();   // ← 立即停止
        return;
    }

    LastFrameNumber = frame.FrameNumber;   // ← 行 818：更新最新帧号
    OnFrame?.Invoke(frame);
}
```

客户端**没有任何机制区分**历史帧（来自 Replay goroutine）和实时帧（来自 Live Ticker），二者使用完全相同的 `CmdPushFrames` 命令，由同一个 `HandlePushFrames` 处理。

---

## 2. 消息流与竞争时序

### 2.1 正常 Replay（无竞争，理想情况）

```
Server Replay goroutine          Client
─────────────────────────────    ──────────────────────────────
CmdStartFrameSync           →    HandleStartFrameSync → LastFrame=0
CmdPushFrames [frame=1]     →    LastFrame=1
CmdPushFrames [frame=2]     →    LastFrame=2
...
CmdPushFrames [frame=244]   →    LastFrame=244
CmdPushFrames [frame=245]   →    LastFrame=245
                                 (replay done)

Server Live Ticker               
─────────────────────────────    
CmdPushFrames [frame=246]   →    LastFrame=246  ← 正常接续
```

### 2.2 竞争触发 4003（实际发生路径）

```
时间轴           Server Replay goroutine       Server Live Ticker (20fps)     Client
─────────────────────────────────────────────────────────────────────────────────────────
t=0ms            AddPlayer → State=Online
t=0ms            sendMsg(ReconnectRsp/JoinRsp)                                收到 Rsp
t=0ms            go replayGoroutine()
t=10ms           CmdPushFrames [frame=1]   →                                  LastFrame=1
t=20ms           CmdPushFrames [frame=2]   →                                  LastFrame=2
...
t=X ms           (goroutine 正在发 frame=244)
t=X+ε ms                                        stepFrame()
                                                broadcastSlice = [reconnPlayer, ...]
                                                CmdPushFrames [frame=245] →   LastFrame=245
t=X+ε+δ ms      CmdPushFrames [frame=244] →                                  244 ≤ 245
                                                                               ↓
                                                                               FATAL 4003
                                                                               OnError invoked
                                                                               HandleStopFrameSync()
```

> `ε` = TCP 传输抖动（可为 0–若干 ms）  
> `δ` = Replay goroutine 发送延迟（批量分包时更长）

**本次案例**：`frame=244, lastFrame=245`  
实时帧 245 比历史帧 244 先到达客户端，原因是 TCP 缓冲区压力或批量发送的 `replayBatchDelay` 拖延了历史帧 244 的到达。

---

## 3. 根因分析

### 根因 R1：AddPlayer 过早将玩家置为 PlayerOnline【核心】

```
room.AddPlayer()
  → existing.State = PlayerOnline   (room.go:272)
  → player = &Player{State: PlayerOnline}  (room.go:277)
```

这一行为立即生效，与 Replay goroutine 的启动没有任何同步关系。从 `AddPlayer` 返回到下一个 `stepFrame` tick 之间（最长 50ms）就存在一个竞争窗口——在此窗口内玩家既在接收历史帧，又会被加入实时广播列表。

### 根因 R2：Live Ticker 与 Replay Goroutine 无协调机制

`stepFrame()` 是一个独立 goroutine（ticker），Replay goroutine 是另一个独立 goroutine，二者之间**无任何同步原语**（无 channel、无锁、无 WaitGroup）。服务器不知道某个玩家是否正在接受 Replay，也不存在"Replay 进行中"的状态。

### 根因 R3：两条推帧路径使用同一个命令码，客户端无法区分

服务器 Replay 和 Live Ticker 都用 `CmdPushFrames`，客户端只有一个 handler `HandlePushFrames`。即使客户端想要区分，协议层也没有提供任何标志位（如 `IsReplay`）。

### 根因 R4：handleMatchRoom 的 Replay goroutine 无超时保护

```go
// main.go:965-1001
go func() {
    time.Sleep(10 * time.Millisecond)   // ← 仅延迟 10ms（无 ctx）
    // ...
    for _, cf := range frames {
        sendMsg(conn, CmdPushFrames, cf.EncodedData)   // 无超时限制
    }
}()
```

`handleJoinRoom` 有 `context.WithTimeout(30s)`，`handleMatchRoom` 没有，慢速客户端可能导致 Replay goroutine 运行数分钟，期间竞争窗口持续存在。

---

## 4. 架构问题

| 编号 | 问题 | 位置 | 影响 |
|------|------|------|------|
| A1 | 玩家状态只有 Online/Disconnected，缺少 Replaying 中间状态 | room.go | Replay 期间无法阻止 Live 广播 |
| A2 | Live Ticker 和 Replay Goroutine 无任何同步机制 | room.go / main.go | 竞争窗口无法消除 |
| A3 | CmdPushFrames 历史帧与实时帧使用同一命令码 | 协议层 | 客户端无法路由，无法建立接收序 |
| A4 | 客户端仅有严格单调性断言，无任何容错缓冲 | FrameSyncClient.cs | 任何乱序直接 Fatal |
| A5 | handleMatchRoom 补帧无超时保护 | main.go:965 | 慢客户端竞争窗口无上界 |
| A6 | Replay 结束帧未提前锁定快照点 | main.go | Replay 范围与 Live 起点无明确分界 |

---

## 5. 修复方案

### Fix 1 — 服务端：新增 PlayerReplaying 状态【P0，根治】

**位置**：`svr/framesync/room.go`

```go
// 现有（room.go:41）
PlayerOnline       PlayerState = 0
PlayerDisconnected PlayerState = 1

// 新增
PlayerReplaying    PlayerState = 2   // 正在接收历史帧，不加入 live broadcast
```

`AddPlayer` 改为根据场景设置状态：

```go
// AddPlayer 增加 isLateJoin 参数
func (r *Room) AddPlayer(id int32, conn PlayerConn, isLateJoin bool) {
    r.mu.Lock()
    if existing, ok := r.players[id]; ok {
        existing.Conn = conn
        if isLateJoin {
            existing.State = PlayerReplaying   // ← Replay 期间不广播
        } else {
            existing.State = PlayerOnline
        }
    } else {
        state := PlayerOnline
        if isLateJoin {
            state = PlayerReplaying
        }
        r.players[id] = &Player{ID: id, Conn: conn, State: state, JoinedAt: time.Now()}
    }
    r.mu.Unlock()
}

// Replay 完成后切换（新增方法）
func (r *Room) SetPlayerOnline(id int32) {
    r.mu.Lock()
    if p, ok := r.players[id]; ok && p.State == PlayerReplaying {
        p.State = PlayerOnline
    }
    r.mu.Unlock()
}
```

`stepFrame()` broadcast filter 不变（`PlayerOnline` 才广播），`PlayerReplaying` 自动被排除。

Replay goroutine 末尾调用 `room.SetPlayerOnline(playerId)`：

```go
// main.go — 三条路径的 replay goroutine 末尾
for i, cf := range frames {
    sendMsg(conn, codec.NewCoreMessage(framesync.CmdPushFrames, cf.EncodedData))
    // ...
}
room.SetPlayerOnline(playerId)   // ← Replay 完成，进入 Live Feed
```

---

### Fix 2 — 服务端：Replay 前锁定终止帧【P0，防止边界超出】

**位置**：`main.go` 三条路径的 goroutine 头部

```go
// 在 goroutine 启动前记录此刻的终止帧（快照之外）
room.mu.RLock()
replayEndFrame := room.frameNumber   // ← 本次 Replay 只到这里
room.mu.RUnlock()

// goroutine 内部
frames := room.GetFramesSince(replayFrom)
// 过滤：只发 <= replayEndFrame 的帧
for _, cf := range frames {
    if cf.FrameNumber > replayEndFrame { break }
    sendMsg(conn, codec.NewCoreMessage(framesync.CmdPushFrames, cf.EncodedData))
}
room.SetPlayerOnline(playerId)
// Live Feed 从 replayEndFrame+1 开始（由 stepFrame ticker 负责）
```

这样 Replay 范围和 Live Feed 起点之间存在明确分界，不重叠。

---

### Fix 3 — 客户端：历史帧降级为 WARN + 静默丢弃【P1，防御层】

**位置**：`cli/Client/FrameSync/FrameSyncClient.cs`（及 unity 镜像同步修改）

当 Fix 1+2 上线后，客户端理论上不再收到乱序帧。但作为防御层，将 Fatal 改为 WARN 并丢弃，避免极端网络下一次抖动就终止帧同步。

```csharp
if (frame.FrameNumber <= LastFrameNumber)
{
    // Fix 1+2 上线后这里不应再触发；如果触发说明服务端仍有边界问题
    Log($"[WARN] Stale frame dropped: frame={frame.FrameNumber} lastFrame={LastFrameNumber}");
    return;   // 静默丢弃，不 Fatal
}
```

> 保留 WARN 日志：如果 Fix 1+2 实施后日志中仍出现 WARN，说明服务端还有未覆盖的竞争路径。

---

### Fix 4 — 服务端：handleMatchRoom 补帧增加超时保护【P1】

**位置**：`main.go:965`

```go
go func() {
    ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
    defer cancel()

    // 去掉 time.Sleep(10ms)，因为 PlayerReplaying 状态已保证顺序
    // ...
    for i, cf := range frames {
        select {
        case <-ctx.Done():
            slog.Warn("late-join (matchRoom) replay timed out", "playerId", playerId, "sent", i)
            room.SetPlayerOnline(playerId)   // 超时也要切换，否则永远无法进入 Live
            return
        default:
        }
        sendMsg(conn, codec.NewCoreMessage(framesync.CmdPushFrames, cf.EncodedData))
    }
    room.SetPlayerOnline(playerId)
}()
```

---

## 6. 修复后消息流（对比）

```
修复前（有竞争）
─────────────────────────────────────────────────────────────────────
Server:  AddPlayer→Online  →  [Replay goroutine: f1,f2,...f244]
                               [Live Ticker: f245]                    ← 同时到达
Client:  收到f245(lastFrame=245), 收到f244(244≤245) → FATAL

修复后（Fix1+Fix2）
─────────────────────────────────────────────────────────────────────
Server:  AddPlayer→Replaying
         replayEndFrame=244（锁定快照点）
         [Replay goroutine: f1,f2,...f244]  →  SetPlayerOnline
         [Live Ticker: 此期间不广播该玩家]
                                             [Live Ticker: f245,f246,...] ← 有序接续
Client:  收到f1...f244（顺序）, 收到f245,f246...（顺序）→ 无4003
```

---

## 7. 受影响文件

```
svr/framesync/room.go
  - PlayerState 新增 PlayerReplaying = 2
  - AddPlayer() 增加 isLateJoin 参数
  - 新增 SetPlayerOnline(id int32) 方法

svr/cmd/framesync/main.go
  - handleReconnect: goroutine 末尾调用 SetPlayerOnline
  - handleJoinRoom:  goroutine 内锁定 replayEndFrame + 末尾调用 SetPlayerOnline
  - handleMatchRoom: goroutine 内锁定 replayEndFrame + 增加 30s ctx + 末尾调用 SetPlayerOnline
  - bindPlayerToRoom: 传递 isLateJoin 参数

cli/Client/FrameSync/FrameSyncClient.cs
  - HandlePushFrames: Fatal → WARN + 静默丢弃

unity/com.boom.boomnetwork/Runtime/Client/FrameSync/FrameSyncClient.cs
  - 同上（镜像同步）
```

---

## 8. 验证方案

| 场景 | 操作 | 预期结果 |
|------|------|----------|
| 快速重连 | VS Demo 中第 200 帧断线重连 | 客户端无 WARN/Fatal，补帧后正常恢复 |
| 迟到加入 | 第 100 帧后新玩家 JoinRoom | 无 4003，帧序连续 |
| matchRoom 迟到 | 已运行房间 MatchRoom | 无 4003，补帧完成后进入 Live |
| 服务端压力测试 | Go 单测：并发 Replay + stepFrame 1000帧 | SetPlayerOnline 之前无 broadcastSlice 写入 |
| 防御层验证 | Fix1+2 上线后监控 | 客户端日志无任何 `[WARN] Stale frame dropped` |
