# DuplicateFrame 4003 — 架构修复方案 v2

**日期**: 2026-04-10

---

## 根因

服务端 `Player` 状态机只有两个状态：

```
PlayerOnline = 0
PlayerDisconnected = 1
```

不存在"正在补帧"的中间状态。`AddPlayer()` 在 Replay goroutine 启动之前就将玩家置为 `PlayerOnline`，导致 `stepFrame()` 在 Replay 未完成时已将该玩家加入实时广播列表，两路 `CmdPushFrames` 同时推向客户端，帧序被破坏。

---

## 竞争时序（精确）

```
handleReconnect / handleJoinRoom / handleMatchRoom
│
├─ room.AddPlayer()          → State = PlayerOnline   ← 竞争窗口开始
│
├─ go replayGoroutine() {
│      send frame 1
│      send frame 2
│      ...
│      send frame 244                                 ← 仍在 Replay 中
│  }
│
└─ stepFrame() ticker [独立 goroutine]
       broadcastSlice includes this player            ← 因为已 Online
       send frame 245 → client                        ← 实时帧抢先到达
                                   client: LastFrame = 245
       replayGoroutine sends frame 244 → client
                                   client: 244 ≤ 245 → FATAL 4003
```

三条入口（`handleReconnect`、`handleJoinRoom`、`handleMatchRoom`）的竞争结构完全相同，只有超时保护有差异（`handleMatchRoom` 连超时都没有）。

---

## 架构修复

### 核心：在状态机中增加 `PlayerReplaying`

```
PlayerOnline       = 0   ← 接收实时帧，参与广播
PlayerDisconnected = 1   ← 断线保留
PlayerReplaying    = 2   ← 正在接收历史帧，不参与广播   ← 新增
```

这一个状态将竞争窗口**从协议层彻底关闭**：`stepFrame()` 的 broadcast filter 已经写死只广播 `PlayerOnline`，加一个状态值就够了，不需要任何新的同步原语。

---

## 改动清单

### 1. `svr/framesync/room.go`

**PlayerState 增加一个常量：**

```go
const (
    PlayerOnline       PlayerState = 0
    PlayerDisconnected PlayerState = 1
    PlayerReplaying    PlayerState = 2   // 新增
)
```

**`AddPlayer` 增加 `replaying bool` 参数：**

```go
func (r *Room) AddPlayer(id int32, conn PlayerConn, replaying bool) {
    r.mu.Lock()
    isReconnect := false
    var player *Player

    initialState := PlayerOnline
    if replaying {
        initialState = PlayerReplaying
    }

    if existing, ok := r.players[id]; ok {
        // onlineCount 只在 Disconnected→(Online|Replaying) 时递增
        // Replaying 也算在线人数（已连接），只是不收实时帧
        if existing.State == PlayerDisconnected {
            atomic.AddInt32(&r.onlineCount, 1)
        }
        existing.Conn = conn
        existing.State = initialState
        isReconnect = true
        player = existing
    } else {
        atomic.AddInt32(&r.onlineCount, 1)
        player = &Player{ID: id, Conn: conn, State: initialState, JoinedAt: time.Now()}
        r.players[id] = player
    }

    r.hadPlayer.Store(true)
    r.emptyAt = time.Time{}
    if r.hostPlayerId == 0 {
        r.hostPlayerId = id
    }
    d := r.delegate
    r.mu.Unlock()

    if d != nil {
        if isReconnect {
            d.OnPlayerReconnected(r, player)
        } else {
            d.OnPlayerJoined(r, player)
        }
    }
}
```

**新增 `SetPlayerLive`，Replay 完成后调用：**

```go
// SetPlayerLive 将玩家从 Replaying 切换为 Online，进入实时广播。
// 必须在 Replay goroutine 发完最后一帧之后调用。
func (r *Room) SetPlayerLive(id int32) {
    r.mu.Lock()
    if p, ok := r.players[id]; ok && p.State == PlayerReplaying {
        p.State = PlayerOnline
    }
    r.mu.Unlock()
}
```

**`removePlayerLocked` 修正 onlineCount 判断（兼容新状态）：**

```go
func (r *Room) removePlayerLocked(id int32) {
    if p, ok := r.players[id]; ok {
        // Replaying 也占一个在线计数
        if p.State == PlayerOnline || p.State == PlayerReplaying {
            atomic.AddInt32(&r.onlineCount, -1)
        }
    }
    delete(r.players, id)
    // ... 其余不变
}
```

**`stepFrame()` broadcast filter 不变**（已经是 `PlayerOnline`，`PlayerReplaying` 自动被排除）：

```go
// room.go:920 — 无需改动
if p.State == PlayerOnline && p.Conn != nil {
    r.broadcastSlice = append(r.broadcastSlice, p)
}
```

---

### 2. `svr/cmd/framesync/main.go`

三条入口的改动模式完全相同：

**① `handleReconnect`（line 642）**

```go
// 改前
room.AddPlayer(playerId, &statsConn{...})

// 改后
room.AddPlayer(playerId, &statsConn{...}, true /* replaying */)
```

Replay goroutine 末尾：

```go
go func() {
    if replayFrom > 0 && replayFrom < currentFrame {
        frames := room.GetFramesSince(replayFrom)
        for i, cf := range frames {
            sendMsg(conn, codec.NewCoreMessage(framesync.CmdPushFrames, cf.EncodedData))
            if (i+1)%replayBatchSize == 0 && i+1 < len(frames) {
                time.Sleep(replayBatchDelay)
            }
        }
    }
    room.SetPlayerLive(playerId)   // ← 新增：Replay 完成，进入实时广播
    // S→C reliable 补发（不变）
    ...
}()
```

**② `handleJoinRoom`（line 778）**

```go
// bindPlayerToRoom 内部调用 AddPlayer，需传递 replaying=true
// bindPlayerToRoom 改为接收 replaying 参数（见下方）

bindPlayerToRoom(playerId, conn, room, true /* replaying */)
```

goroutine 末尾：

```go
go func() {
    ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
    defer cancel()

    // Snapshot + StartFrameSync + Replay frames（不变）
    ...

    room.SetPlayerLive(playerId)   // ← 新增
}()
```

**③ `handleMatchRoom`（line 950）**

```go
bindPlayerToRoom(playerId, conn, room, true /* replaying */)

go func() {
    // 去掉 time.Sleep(10ms)，PlayerReplaying 已保证顺序，不需要靠 sleep 规避
    ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)   // ← 补加超时
    defer cancel()

    // Snapshot + StartFrameSync + Replay frames（不变）
    ...

    room.SetPlayerLive(playerId)   // ← 新增
}()
```

> `handleMatchRoom` 原本没有超时保护，一并补上，超时后同样调用 `SetPlayerLive` 避免玩家永远卡在 `Replaying` 状态。

**`bindPlayerToRoom` 增加参数：**

```go
func bindPlayerToRoom(playerId int32, conn *transport.Conn, room *framesync.Room, replaying bool) {
    connPlayerMap.Store(conn.ID, playerId)
    playerConnMap.Store(playerId, conn)
    connContextMap.Store(conn.ID, &connContext{playerId: playerId, room: room})
    room.AddPlayer(playerId, &statsConn{inner: &simConn{inner: conn, cfg: GlobalNetSim}, pid: playerId}, replaying)
}
```

所有非迟到场景（正常首次加入、非运行中房间）传 `replaying=false`，行为与现在完全一致。

---

## 修复后时序

```
handleReconnect / handleJoinRoom / handleMatchRoom
│
├─ room.AddPlayer(replaying=true)   → State = PlayerReplaying
│
├─ go replayGoroutine() {
│      send frame 1 ... frame 244
│      room.SetPlayerLive()         → State = PlayerOnline
│  }
│
└─ stepFrame() ticker
       broadcastSlice: PlayerReplaying 不在列表
       [replay 期间不发任何实时帧给该玩家]
                      │
                      └─ SetPlayerLive 之后
                             broadcastSlice: 玩家进入
                             send frame 245, 246, ...   ← 有序接续，无重叠
```

---

## 改动范围

```
svr/framesync/room.go
  + PlayerReplaying = 2
  ~ AddPlayer()：增加 replaying bool 参数
  + SetPlayerLive(id int32)
  ~ removePlayerLocked()：onlineCount 兼容 PlayerReplaying

svr/cmd/framesync/main.go
  ~ bindPlayerToRoom()：增加 replaying bool 参数
  ~ handleReconnect：AddPlayer 传 replaying=true，goroutine 末尾 SetPlayerLive
  ~ handleJoinRoom：bindPlayerToRoom 传 replaying=true，goroutine 末尾 SetPlayerLive
  ~ handleMatchRoom：bindPlayerToRoom 传 replaying=true，goroutine 末尾 SetPlayerLive，补加 ctx 超时
```

客户端零改动。
