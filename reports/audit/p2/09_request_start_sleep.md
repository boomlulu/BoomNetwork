# P2-09: handleRequestStart 用 sleep 保证时序不可靠

**严重程度**: P2 — 中风险
**状态**: 待执行
**影响**: 高负载下客户端可能在 RequestStart 响应之前收到 StartFrameSync 广播

---

## 根因

`main.go:1052-1055`:

```go
go func() {
    time.Sleep(10 * time.Millisecond) // 确保本消息处理完
    room.Start()
}()
return nil
```

用 sleep 来保证 handler 返回后再执行 Start 是不可靠的。在 GC pause 或高负载下 handler 执行可能超过 10ms。

## 涉及文件

| 文件 | 行号（约） | 改动类型 |
|------|-----------|----------|
| `svr/cmd/framesync/main.go` | 1052-1057 | 改用 channel 或重构 |

## 执行计划

### 方案 A: handler 直接返回响应 + 异步 Start（推荐）

handleRequestStart 当前返回 nil（不回复客户端）。改为：先回复一个确认消息，再同步调用 `room.Start()`（不用 goroutine）：

```go
func handleRequestStart(conn *transport.Conn, msg *codec.Message) *codec.Message {
    // ... 验证 playerId、room ...
    
    // room.Start() 内部已有重入保护（running=true 时直接返回）
    // Start 会广播 CmdStartFrameSync，在当前 goroutine 中执行
    // 由于 handler 返回 nil，dispatch 层不会再发额外响应
    room.Start()
    return nil
}
```

但这里有一个问题：`room.Start()` 中的 `broadcast(CmdStartFrameSync)` 会先于 handler 的响应发出。不过当前 handler 返回 nil（无响应），所以实际上没有时序冲突。直接去掉 goroutine 和 sleep 即可。

### 方案 B: 如果需要先回复再 Start

如果未来 handleRequestStart 需要先返回一个 Rsp 再执行 Start：

```go
func handleRequestStart(conn *transport.Conn, msg *codec.Message) *codec.Message {
    // ... 验证 ...
    
    // 先发送响应
    sendMsg(conn, codec.NewCoreMessage(framesync.CmdRequestStartRsp, []byte{1}))
    
    // 再同步 Start（TCP FIFO 保证 Rsp 先到达客户端）
    room.Start()
    return nil  // 已手动发送响应，返回 nil
}
```

### 验证

- `go test ./...`
- 压力测试：100 个客户端同时发 RequestStart，确认无 panic 和乱序

---

## 验收标准

### 编译与测试

- [ ] `go build ./...` 编译通过
- [ ] `go test ./...` 全部通过

### 代码审查检查项

- [ ] handleRequestStart 中不再有 `time.Sleep`
- [ ] handleRequestStart 中不再有 `go func()` 包裹 room.Start()
- [ ] room.Start() 的重入保护（running=true 跳过）仍然有效

### 预期结果

| 场景 | 修复前 | 修复后 |
|------|--------|--------|
| 正常 RequestStart | 10ms 延迟后 Start | 立即 Start，无额外延迟 |
| 高负载 GC pause > 10ms | 可能在 handler 返回前 Start | 不存在时序问题 |
| 重复 RequestStart | running=true 跳过 | 行为不变 |

**核心指标**: 客户端从发送 RequestStart 到收到 StartFrameSync 的延迟减少 10ms。
