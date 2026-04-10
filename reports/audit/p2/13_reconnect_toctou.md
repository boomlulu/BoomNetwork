# P2-13: reconnect 中 IsRunning/IsGamePaused 的 TOCTOU

**严重程度**: P2 — 中风险
**状态**: ✅ 已完成（2026-04-10）
**影响**: 房间状态在两次独立读取之间可能变化，导致事件丢失或多余的暂停通知

---

## 根因

`main.go:667-679`:

```go
if room.IsRunning() {           // 读 1：加锁→读→解锁
    room.EnqueueEvent(...)
} else {
    broadcastToRoom(...)
}
if room.IsGamePaused() {        // 读 2：加锁→读→解锁（状态可能已变）
    sendMsg(conn, ...)
}
```

两次读取之间房间可能被 Stop。EnqueueEvent 写入的事件不会被消费（tickLoop 已退出）。

## 涉及文件

| 文件 | 行号（约） | 改动类型 |
|------|-----------|----------|
| `svr/framesync/room.go` | 新增方法 | 原子读取多个状态 |
| `svr/cmd/framesync/main.go` | handleReconnect ~667 | 使用新方法 |

## 执行计划

### Step 1: 在 Room 上新增原子状态读取方法

```go
// RoomState 房间状态快照
type RoomState struct {
    Running    bool
    GamePaused bool
}

// GetState 原子读取房间状态（一次加锁，读取所有需要的状态）
func (r *Room) GetState() RoomState {
    r.mu.Lock()
    defer r.mu.Unlock()
    return RoomState{
        Running:    r.running,
        GamePaused: r.gamePaused,
    }
}
```

### Step 2: 在 handleReconnect 中使用

```go
state := room.GetState()
if state.Running {
    room.EnqueueEvent(framesync.FrameEventPlayerOnline, playerId)
} else {
    broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPlayerOnline, ...))
}
if state.GamePaused {
    sendMsg(conn, codec.NewExtMessage(framesync.ExtCmdFrameSyncPaused, ...))
}
```

### Step 3: 同样的模式也出现在其他 handler 中

搜索所有连续调用 `room.IsRunning()` + 其他 `room.Is*()` 的地方，统一改为 `room.GetState()`。

### 验证

- `go test ./...`
- `go vet ./...`

---

## 验收标准

### 编译与测试

- [ ] `go build ./...` 编译通过
- [ ] `go test ./...` 全部通过

### 代码审查检查项

- [ ] `GetState()` 方法存在且一次加锁读取所有需要的状态
- [ ] handleReconnect 中不再连续调用 `room.IsRunning()` + `room.IsGamePaused()`
- [ ] `grep -rn 'IsRunning.*IsGamePaused\|IsGamePaused.*IsRunning' svr/cmd/` 确认不再有连续调用

### 预期结果

| 场景 | 修复前 | 修复后 |
|------|--------|--------|
| 两次读取之间房间被 Stop | EnqueueEvent 写入已停止房间，事件丢失 | 一次读取，状态一致 |
| 正常运行 | 无可观测差异 | 无可观测差异 |

**核心指标**: 重连后的事件通知（PlayerOnline / GamePaused）与房间实际状态一致，无事件丢失。
