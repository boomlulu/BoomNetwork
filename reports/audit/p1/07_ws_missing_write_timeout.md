# P1-07: WsServer 创建 Conn 时未设置 writeTimeout

**严重程度**: P1 — 高风险
**状态**: 待执行
**影响**: WebSocket 慢客户端的 Send 可能无限阻塞，拖慢广播循环

---

## 根因

`transport/ws_server.go:254-259`:

```go
c := &Conn{
    ID:          s.nextID,
    conn:        adapted,
    writer:      codec.NewFrameWriter(adapted),
    rateLimiter: NewRateLimiter(s.security.MaxMessagesPerSec),
    // 缺少: writeTimeout
}
```

TcpServer 在 `handleConn` 中创建 Conn 时传入了 `writeTimeout: s.config.WriteTimeout`（tcp_server.go:198），但 WsServer 漏掉了。

## 涉及文件

| 文件 | 行号（约） | 改动类型 |
|------|-----------|----------|
| `svr/transport/ws_server.go` | 254-259 | 添加一行 |

## 执行计划

### Step 1: 一行修复

在 `ws_server.go:254-259` 的 Conn 初始化中添加 `writeTimeout`:

```go
c := &Conn{
    ID:           s.nextID,
    conn:         adapted,
    writer:       codec.NewFrameWriter(adapted),
    rateLimiter:  NewRateLimiter(s.security.MaxMessagesPerSec),
    writeTimeout: s.config.WriteTimeout,  // ← 添加此行
}
```

### Step 2: 验证

- `go vet ./...`
- `go test ./...`
- 确认 `wsConnAdapter` 的 `SetWriteDeadline` 实现正确（已有，line 86-88）

---

## 验收标准

### 编译与测试

- [ ] `go build ./...` 编译通过
- [ ] `go test ./...` 全部通过

### 代码审查检查项

- [ ] WsServer 创建的 Conn 的 `writeTimeout` 字段值等于 `s.config.WriteTimeout`
- [ ] 与 TcpServer（tcp_server.go:198）行为完全一致

### 预期结果

| 场景 | 修复前 | 修复后 |
|------|--------|--------|
| WebSocket 慢客户端 | Send 无限阻塞 | writeTimeout（默认 10s）后返回 error |
| 正常 WebSocket 客户端 | 正常 | 无行为变化 |

**核心指标**: WebSocket 连接的 `Send` 不会阻塞超过 writeTimeout（默认 10 秒）。
