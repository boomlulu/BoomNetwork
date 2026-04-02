# Security — 认证 / 限流 / 白名单

## 文件索引

| File | 职责 |
|---|---|
| `svr/transport/security.go` | SecurityConfig + RateLimiter + IPRateLimiter |
| `svr/cmd/framesync/main.go` | 认证 Handler (handleSessionBind) + 安全配置装配 |
| `svr/cmd/framesync/admin.go` | Admin HTTP Bearer Token 中间件 |
| `svr/cmd/framesync/admin_ws.go` | WebSocket 认证握手 |

## 认证机制

### 1. 游戏客户端认证

```
CmdSessionBind (Cmd=2):
  C→S: Data = UTF-8 token 字符串
  S→C: 成功返回空响应 / 失败返回 CmdError + 100ms 后断开
```

- `authToken` 为空 → 跳过认证（开发模式）
- Token 不匹配 → `boom_auth_failures_total++` → 断开
- 环境变量 `BOOM_AUTH_TOKEN` 覆盖配置文件值

### 2. Admin HTTP 认证

```
withAuth() 中间件:
  Header: Authorization: Bearer <adminToken>
  缺失/不匹配 → 401 Unauthorized
  /health 端点豁免
```

- 环境变量 `BOOM_ADMIN_TOKEN` 覆盖配置文件值

### 3. Admin WebSocket 认证

```
WS 连接后 5 秒内必须发送 auth 消息:
  GMEnvelope{Type: "auth", Payload: token}
  成功 → auth_ok + 加入 GMHub
  失败/超时 → auth_err + 关闭连接
```

## 限流三层

### Layer 1: Per-IP 连接速率

```go
// transport/security.go
type IPRateLimiter struct {
    rates sync.Map   // IP → *ipRate
    limit int        // 默认 10 连接/秒/IP
}
```

检查点：TCP/KCP Accept 后，Conn 分配前。
超限：直接关闭 net.Conn，不分配资源。

### Layer 2: Per-Connection 消息速率

```go
// transport/security.go
type RateLimiter struct {
    maxPerSec   int
    count       int32
    windowStart int64
}
```

检查点：每条消息读取后，Handler 调用前。
超限：断开连接 + 调用 `OnRateLimited` 回调 + `boom_rate_limited_total++`。
默认：100 msg/sec（可通过 `maxMessagesPerSec` 配置）。

### Layer 3: 全局容量限制

| 限制 | 配置项 | 默认 |
|---|---|---|
| 最大连接数 | `maxConnections` | 0 (无限) |
| 最大房间数 | `maxRooms` | 0 (无限) |
| 最大消息大小 | `maxMessageSize` | 65536 (64KB) |

超限行为：连接拒绝 / 创建失败返回错误 / 断开连接。

## WebSocket Origin 白名单

```yaml
allowedOrigins:
  - "http://localhost:3000"
  - "https://admin.example.com"
```

空列表 = 允许所有 Origin（向后兼容）。
非空时，WS Upgrade 请求的 Origin 必须在白名单中，否则拒绝升级。

## 安全设计要点

1. **Token 通过环境变量优先**：适合容器/systemd 部署，避免配置文件泄露
2. **认证失败延迟断开**：100ms 延迟让错误响应有时间刷到客户端
3. **限流在 Handler 前执行**：恶意流量不消耗业务逻辑资源
4. **IP 限流无锁（sync.Map）**：高并发 Accept 路径不阻塞
5. **/health 无需认证**：Docker HEALTHCHECK / 负载均衡器 / Kubernetes 探针可用
