# P0-02: handleMatchRoom replay goroutine 无超时保护

**严重程度**: P0 — 生产事故级
**状态**: 待执行
**影响**: 慢客户端可导致 goroutine 永远阻塞，积累后耗尽内存

---

## 根因

`handleMatchRoom`（main.go:965-1001）的 replay goroutine 没有 context 超时保护。对比 `handleJoinRoom` 使用了 `context.WithTimeout(30s)`，这里完全没有。如果客户端网络极慢导致 TCP 写缓冲满，`sendMsg` 会一直阻塞。

## 涉及文件

| 文件 | 行号（约） | 改动类型 |
|------|-----------|----------|
| `svr/cmd/framesync/main.go` | 965-1001 | 添加超时 |

## 执行计划

### Step 1: 给 replay goroutine 加 context 超时

找到 `handleMatchRoom` 函数中的 goroutine（约 line 965）:

**改前**:
```go
go func() {
    time.Sleep(10 * time.Millisecond)
    // ... replay 逻辑，无超时 ...
}()
```

**改后**:
```go
go func() {
    ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
    defer cancel()

    // ... 所有 sendMsg 调用前检查 ctx ...
    // replay 帧循环中加 select:
    for i, cf := range frames {
        select {
        case <-ctx.Done():
            slog.Warn("late-join (matchRoom) replay timed out", "playerId", playerId, "sentFrames", i, "totalFrames", len(frames))
            return
        default:
        }
        sendMsg(conn, codec.NewCoreMessage(framesync.CmdPushFrames, cf.EncodedData))
    }
}()
```

### Step 2: 移除 `time.Sleep(10ms)`

P0-01 的 `PlayerReplaying` 状态已经保证时序安全，不再需要靠 sleep 规避竞态。删除 `time.Sleep(10 * time.Millisecond)`。

### Step 3: 超时后的兜底

如果同时执行了 P0-01，需要在 `defer cancel()` 之后加 `defer room.SetPlayerLive(playerId)`，确保超时退出后玩家不会永远卡在 `PlayerReplaying`。

### Step 4: 验证

- `go vet ./...`
- `go test ./...`
- 对比 handleJoinRoom 的超时逻辑，确认一致性

---

## 验收标准

### 编译与测试

- [ ] `go build ./...` 编译通过
- [ ] `go vet ./...` 零 warning
- [ ] `go test ./...` 全部通过

### 代码审查检查项

- [ ] handleMatchRoom 的 replay goroutine 中有 `context.WithTimeout(30s)`
- [ ] 帧 replay 循环中有 `select { case <-ctx.Done(): return }` 检查
- [ ] `time.Sleep(10ms)` 已被移除
- [ ] 超时退出路径调用了 `room.SetPlayerLive(playerId)`（如果 P0-01 已实施）
- [ ] goroutine 结构与 handleJoinRoom 一致（`defer cancel()` 在首行）

### 预期结果

| 场景 | 修复前 | 修复后 |
|------|--------|--------|
| 慢客户端加入运行中房间 | goroutine 永远阻塞 | 30 秒后超时退出，日志 warn |
| 正常客户端加入 | 补帧正常（但有 10ms 无意义延迟） | 补帧正常（无额外延迟） |
| 超时后玩家状态 | N/A（永远不超时） | PlayerReplaying → PlayerOnline（兜底） |

**核心指标**: `go tool pprof` 中不再出现长期存活的 matchRoom replay goroutine。
