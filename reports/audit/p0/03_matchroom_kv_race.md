# P0-03: handleMatchRoom KV 同步与 replay 并发写 conn

**严重程度**: P0 — 生产事故级
**状态**: ✅ 已完成（2026-04-10）
**影响**: 客户端可能在收到 StartFrameSync 之前就收到 KV 数据，时序混乱

---

## 根因

`handleMatchRoom` 中，KV 全量同步是独立 goroutine（main.go:1005-1011），与 replay goroutine（main.go:965-1001）并发执行。两者同时向同一个 `conn` 发送数据。

虽然 `conn.Send()` 内部有 mutex 保证单条消息原子性，但两个 goroutine 的消息交错顺序不可控。客户端可能收到：`KV数据 → StartFrameSync → Frame1`（期望是 `StartFrameSync → Frame1...N → KV数据`）。

对比 `handleJoinRoom` 将 KV 同步放在 replay goroutine 内部（串行执行），handleMatchRoom 拆成两个并发 goroutine，行为不一致。

## 涉及文件

| 文件 | 行号（约） | 改动类型 |
|------|-----------|----------|
| `svr/cmd/framesync/main.go` | 960-1012 | 合并 goroutine |

## 执行计划

### Step 1: 将 KV 同步合并到 replay goroutine 内

删除独立的 KV 同步 goroutine（约 line 1005-1011）:

```go
// 删除这段
if !room.DataStoreEmpty() {
    go func() {
        time.Sleep(15 * time.Millisecond)
        entries, version := room.GetDataSnapshot()
        sendMsg(conn, codec.NewExtMessage(framesync.ExtCmdPushDataSync, framesync.EncodePushDataSync(version, entries)))
    }()
}
```

在 replay goroutine 末尾（补帧完成后）加入 KV 同步，与 handleJoinRoom 保持一致:

```go
go func() {
    ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
    defer cancel()

    // 1. Snapshot + StartFrameSync + 补帧（不变）
    // ...

    // 2. KV 全量同步（串行，在补帧之后）
    if !room.DataStoreEmpty() {
        select {
        case <-ctx.Done():
            slog.Warn("late-join (matchRoom) KV sync timed out", "playerId", playerId)
            return
        default:
        }
        entries, version := room.GetDataSnapshot()
        sendMsg(conn, codec.NewExtMessage(framesync.ExtCmdPushDataSync, framesync.EncodePushDataSync(version, entries)))
    }

    // 3. SetPlayerLive（如果 P0-01 已实施）
    room.SetPlayerLive(playerId)
}()
```

### Step 2: 处理房间未运行但有 KV 数据的情况

当前 `handleMatchRoom` 的 KV 同步 goroutine 在房间未运行时也会触发。合并后需要确保：
- 房间运行中 → replay goroutine 内串行发 KV
- 房间未运行但有 KV → 仍需要发 KV，可在 replay goroutine 外单独处理（此时无竞态，因为没有 replay）

### Step 3: 验证

- `go vet ./...`
- `go test ./...`
- 对比 handleJoinRoom 的完整 goroutine 逻辑，确认结构一致
- 特别关注：房间未运行 + 有 KV 数据时，KV 同步是否仍然能正常发送

---

## 验收标准

### 编译与测试

- [ ] `go build ./...` 编译通过
- [ ] `go vet ./...` 零 warning
- [ ] `go test ./...` 全部通过

### 代码审查检查项

- [ ] handleMatchRoom 中不再有独立的 KV 同步 goroutine（`go func() { time.Sleep(15ms)...` 已删除）
- [ ] KV 同步代码位于 replay goroutine 内部，在补帧完成之后串行执行
- [ ] 房间运行中 + 有 KV：replay → KV → SetPlayerLive（全部在同一 goroutine 内串行）
- [ ] 房间未运行 + 有 KV：KV 同步仍然能正常发送（不被 isRunning 条件跳过）
- [ ] handleMatchRoom 的 goroutine 结构与 handleJoinRoom 结构一致

### 预期结果

客户端收到消息的严格顺序（房间运行中）:
```
1. MatchRoomRsp           ← 同步返回
2. RoomSnapshot           ← goroutine 内串行
3. CmdStartFrameSync      ← goroutine 内串行
4. CmdPushFrames × N      ← goroutine 内串行（补帧）
5. PushDataSync            ← goroutine 内串行（KV 全量同步）
6. [实时帧开始]            ← SetPlayerLive 后
```

**核心指标**: 客户端不再在 StartFrameSync 之前收到 KV 数据。可通过在客户端加断言验证——收到 PushDataSync 时 isStarted 必须为 true。
