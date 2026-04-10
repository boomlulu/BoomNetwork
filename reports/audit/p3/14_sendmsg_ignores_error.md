# P3-14: sendMsg 忽略 conn.Send 返回的错误

**严重程度**: P3 — 低风险
**状态**: 待执行
**影响**: 发送失败不被感知，replay 路径持续向坏连接写数据浪费 CPU

---

## 根因

`main.go:1080-1085`:

```go
func sendMsg(conn *transport.Conn, msg *codec.Message) {
    GameStats.RecordTx(int64(len(msg.Data)))
    framesync.Metrics.BytesSent.Add(float64(len(msg.Data)))
    logMsgFromMsg("tx", msg, connPid(conn), "")
    conn.Send(msg)  // 返回值被忽略
}
```

## 执行计划

### Step 1: sendMsg 返回 error

```go
func sendMsg(conn *transport.Conn, msg *codec.Message) error {
    GameStats.RecordTx(int64(len(msg.Data)))
    framesync.Metrics.BytesSent.Add(float64(len(msg.Data)))
    logMsgFromMsg("tx", msg, connPid(conn), "")
    return conn.Send(msg)
}
```

### Step 2: 关键调用方检查错误

特别是 replay goroutine 中的循环 sendMsg，如果返回 error 应该提前退出：

```go
for i, cf := range frames {
    if err := sendMsg(conn, ...); err != nil {
        slog.Warn("replay send failed, aborting", "playerId", playerId, "err", err)
        return
    }
}
```

### Step 3: 非关键调用方可忽略

非循环的单次 sendMsg（如 sendMsg(conn, joinRsp)）可以用 `_ = sendMsg(conn, ...)` 显式忽略。

### 验证

- `go vet ./...`（会检查未处理的 error）
- `go test ./...`

---

## 验收标准

- [ ] `go build ./...` 编译通过
- [ ] `go test ./...` 全部通过
- [ ] `sendMsg` 签名变为 `func sendMsg(...) error`
- [ ] replay 路径中的循环 sendMsg 在 error 时提前 return
- [ ] 非关键路径的 sendMsg 调用用 `_ =` 显式忽略 error

**核心指标**: replay goroutine 在连接断开后立即退出，不再持续向坏连接写数据。
