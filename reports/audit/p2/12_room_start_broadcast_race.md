# P2-12: Room.Start() 在锁外广播，可能与 tickLoop 的第一帧竞态

**严重程度**: P2 — 中风险
**状态**: 待执行
**影响**: 客户端可能在收到 CmdStartFrameSync 之前就收到 frame 1

---

## 根因

`room.go:748-783`:

```go
func (r *Room) Start() {
    r.mu.Lock()
    // ... set running=true ...
    r.mu.Unlock()

    r.broadcast(CmdStartFrameSync, ...)  // 锁外广播
    go r.tickLoop()                       // tickLoop 也可能比 broadcast 先执行
}
```

`broadcast` 和 `go r.tickLoop()` 都在锁外。如果 goroutine 调度器立即运行 tickLoop，`stepFrame()` 可能在 `broadcast(CmdStartFrameSync)` 完成之前就广播了 frame 1。

## 涉及文件

| 文件 | 行号（约） | 改动类型 |
|------|-----------|----------|
| `svr/framesync/room.go` | 748-783 | 调整顺序 |

## 执行计划

### 方案 A: 先广播再启动 tickLoop（最小改动）

确保 `broadcast` 在 `go r.tickLoop()` 之前完成即可。当前代码已经是这个顺序，但 `broadcast` 内部释放锁后遍历玩家发送，期间 tickLoop 的 goroutine 可能已经被调度。

改为在锁内发送 StartFrameSync（因为此时 tickLoop 还没启动，不会有 stepFrame 并发）:

```go
func (r *Room) Start() {
    r.mu.Lock()
    if r.running {
        r.mu.Unlock()
        return
    }
    r.running = true
    // ... 初始化 ...

    // 在锁内收集广播目标
    targets := make([]*Player, 0, len(r.players))
    for _, p := range r.players {
        if p.State == PlayerOnline && p.Conn != nil {
            targets = append(targets, p)
        }
    }
    r.stopCh = make(chan struct{})
    d := r.delegate
    r.mu.Unlock()

    // 先广播 StartFrameSync（tickLoop 尚未启动，不存在竞态）
    msg := codec.NewCoreMessage(CmdStartFrameSync, EncodeInitData(initData))
    for _, p := range targets {
        p.Conn.Send(msg)
    }

    // 再启动 tickLoop
    go r.tickLoop()

    if d != nil {
        d.OnRoomStarted(r)
    }
}
```

### 验证

- `go test ./...`
- 特别关注 `room_test.go` 中 Start 相关的测试

---

## 验收标准

### 编译与测试

- [ ] `go build ./...` 编译通过
- [ ] `go test ./...` 全部通过

### 代码审查检查项

- [ ] CmdStartFrameSync 广播在 `go r.tickLoop()` 之前完成
- [ ] tickLoop 不可能在 StartFrameSync 广播完成前执行 stepFrame

### 预期结果

| 场景 | 修复前 | 修复后 |
|------|--------|--------|
| Start 后立即 stepFrame | 客户端可能先收到 frame1 再收到 StartFrameSync | 一定先收 StartFrameSync 再收 frame1 |

**核心指标**: 客户端收到的第一条消息一定是 CmdStartFrameSync，不会是 CmdPushFrames。
