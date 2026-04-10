# P2-11: MaxMessageSize 全局可变变量多处写入

**严重程度**: P2 — 中风险
**状态**: ✅ 已完成（2026-04-10）
**影响**: TcpServer 和 WsServer 若设不同值，后写者覆盖前者

---

## 根因

`codec/framing.go:10`:
```go
var MaxMessageSize = 65536
```

`tcp_server.go:106` 和 `ws_server.go:158` 的 `SetSecurity` 都写这个全局变量。

## 涉及文件

| 文件 | 改动类型 |
|------|----------|
| `svr/codec/framing.go` | 移除全局变量写入的依赖 |
| `svr/transport/tcp_server.go` | 不再写全局变量 |
| `svr/transport/ws_server.go` | 不再写全局变量 |

## 执行计划

### Step 1: 移除 SetSecurity 中对全局变量的写入

`FrameReader` 已经有实例字段 `fr.maxMessageSize`，在 `handleConn` 中通过 `reader.SetMaxMessageSize()` 设置。全局变量的写入是多余的。

删除 `tcp_server.go:106` 和 `ws_server.go:158` 中的：
```go
codec.MaxMessageSize = cfg.MaxMessageSize
```

### Step 2: 确认影响

搜索所有读取 `codec.MaxMessageSize` 的地方，确认移除全局写入后不会遗漏。特别关注：
- `codec.ReadFrame`（非 FrameReader 版本）使用全局 `MaxMessageSize`
- 如果有代码直接调用 `codec.ReadFrame` 而非 `FrameReader.ReadFrame`，需要传参

### 验证

- `go test ./...`
- `grep -rn "MaxMessageSize" svr/` 确认所有引用点

---

## 验收标准

### 编译与测试

- [ ] `go build ./...` 编译通过
- [ ] `go test ./...` 全部通过

### 代码审查检查项

- [ ] `SetSecurity` 方法中不再写 `codec.MaxMessageSize`
- [ ] 全局 `codec.MaxMessageSize` 仍作为默认值存在，但不被运行时覆盖
- [ ] `FrameReader.SetMaxMessageSize()` 是唯一的消息大小限制配置入口
- [ ] `grep -rn 'codec.MaxMessageSize\s*=' svr/` 返回零结果（除了 var 声明）

### 预期结果

TcpServer 和 WsServer 可以配置不同的 MaxMessageSize 而互不干扰。
