# P0-01: Replay/实时帧竞态（DuplicateFrame 4003）

**严重程度**: P0 — 生产事故级
**状态**: 方案已评审，待执行
**影响**: 客户端收到乱序帧 → FATAL 4003 崩溃

---

## 根因

`AddPlayer()` 在 replay goroutine 启动前就将玩家置为 `PlayerOnline`，导致 `stepFrame()` 在 replay 未完成时将该玩家加入实时广播列表。两路 `CmdPushFrames` 同时推向客户端，帧序被破坏。

## 涉及文件

| 文件 | 改动类型 |
|------|----------|
| `svr/framesync/room.go` | 新增状态常量 + 方法 |
| `svr/cmd/framesync/main.go` | 三条入口 + bindPlayerToRoom |

## 执行计划

### Step 1: room.go — 新增 PlayerReplaying 状态

在 `svr/framesync/room.go` 的 PlayerState 常量块中新增：

```go
const (
    PlayerOnline       PlayerState = 0
    PlayerDisconnected PlayerState = 1
    PlayerReplaying    PlayerState = 2   // 新增：正在接收历史帧，不参与广播
)
```

### Step 2: room.go — 修改 AddPlayer 签名

`AddPlayer` 增加 `replaying bool` 参数：

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
    // ... 其余逻辑不变
}
```

### Step 3: room.go — 新增 SetPlayerLive

```go
func (r *Room) SetPlayerLive(id int32) {
    r.mu.Lock()
    if p, ok := r.players[id]; ok && p.State == PlayerReplaying {
        p.State = PlayerOnline
    }
    r.mu.Unlock()
}
```

### Step 4: room.go — 修改 removePlayerLocked

```go
func (r *Room) removePlayerLocked(id int32) {
    if p, ok := r.players[id]; ok && (p.State == PlayerOnline || p.State == PlayerReplaying) {
        atomic.AddInt32(&r.onlineCount, -1)
    }
    delete(r.players, id)
    // ... 其余不变
}
```

### Step 5: main.go — 修改 bindPlayerToRoom

增加 `replaying bool` 参数，传递给 `room.AddPlayer`：

```go
func bindPlayerToRoom(playerId int32, conn *transport.Conn, room *framesync.Room, replaying bool) {
    // ... 映射存储不变 ...
    room.AddPlayer(playerId, &statsConn{...}, replaying)
}
```

### Step 6: main.go — 修改三条入口

**handleReconnect（~line 642）**: 
- `room.AddPlayer(playerId, ..., true)` 替换原调用（通过 bindPlayerToRoom 或直接调用）
- replay goroutine 末尾（补帧 + reliable 补发结束后）加 `room.SetPlayerLive(playerId)`

**handleJoinRoom（~line 778）**:
- `bindPlayerToRoom(playerId, conn, room, true)` 
- goroutine 末尾加 `room.SetPlayerLive(playerId)`

**handleMatchRoom（~line 950）**:
- `bindPlayerToRoom(playerId, conn, room, true)`
- goroutine 末尾加 `room.SetPlayerLive(playerId)`

### Step 7: main.go — 所有非迟到加入的调用方

`handleSessionBind` 中的 autoRoom 路径：`bindPlayerToRoom(playerId, conn, room, false)`

### Step 8: 验证

- `go vet ./...` 确认无编译错误
- `go test ./...` 运行全部测试
- 搜索所有 `AddPlayer` 和 `bindPlayerToRoom` 调用点，确认参数已更新
- 搜索所有 `PlayerOnline` / `PlayerDisconnected` 引用点，确认语义兼容 `PlayerReplaying`

---

## 验收标准

### 编译与测试

- [ ] `go build ./...` 编译通过，零 error
- [ ] `go vet ./...` 零 warning
- [ ] `go test ./...` 全部通过，无新增 fail
- [ ] `grep -rn 'AddPlayer' svr/` 所有调用点都已传递 `replaying` 参数
- [ ] `grep -rn 'PlayerOnline\|PlayerDisconnected' svr/` 所有引用点语义兼容 `PlayerReplaying`

### 功能验证（自动化测试）

- [ ] **新增单元测试**: `room_test.go` 中添加 `TestPlayerReplayingState`
  - AddPlayer(replaying=true) → 玩家 State == PlayerReplaying
  - PlayerReplaying 不出现在 stepFrame 的 broadcastSlice 中
  - SetPlayerLive 后 State == PlayerOnline，出现在 broadcastSlice
  - SetPlayerLive 对不存在的玩家或已 Online 的玩家是 no-op
- [ ] **新增单元测试**: `TestReplayingOnlineCount`
  - PlayerReplaying 计入 onlineCount
  - removePlayerLocked 正确递减 PlayerReplaying 的 onlineCount
- [ ] **现有测试**: `room_s1_reconnect_test.go` 通过（重连场景）

### 预期结果

修复后的时序应为：
```
AddPlayer(replaying=true) → State = PlayerReplaying
    → stepFrame 广播列表不包含该玩家
    → replay goroutine 发完所有历史帧
    → SetPlayerLive() → State = PlayerOnline
    → stepFrame 广播列表开始包含该玩家
    → 客户端帧序连续，无重叠
```

**核心指标**: 客户端不再触发 FATAL 4003 错误码。

### 回归风险检查

- [ ] 非迟到加入（正常首次加入、autoRoom）传 `replaying=false`，行为与修改前完全一致
- [ ] Room.Stop() 后清理 PlayerReplaying 状态的玩家不会 panic
- [ ] 连续两次快速重连不会导致 SetPlayerLive 被调用两次后 crash

## 参考文档

- `reports/desync_duplicate_frame_4003_v2.md`（完整根因分析和修复后时序图）
