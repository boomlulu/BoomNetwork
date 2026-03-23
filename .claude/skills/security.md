# 安全加固技能

## 已实施的安全措施

### 1. 消息大小限制
```
位置: svr/transport/security.go → SecurityConfig.MaxMessageSize
默认: 64 KB (65536 bytes)
行为: 超过 → 断开连接 + 日志
应用: FrameReader.ReadFrame + standalone ReadFrame
```

### 2. 消息速率限制
```
位置: svr/transport/security.go → RateLimiter
默认: 500 msg/sec + 20 burst
行为: 超过 → 断开连接 + 日志 "rate limited"
应用: 每个 TCP 连接独立计数
注意: 客户端输入已节流到 20fps，正常不会触发
```

### 3. Goroutine Panic Recovery
```
位置: svr/framesync/room.go → tickLoop()
实现: defer func() { if r := recover(); r != nil { ... } }()
行为: 单个房间 panic → 只停止该房间，不 crash 进程
日志: "[Room N] PANIC recovered: ..."
```

### 4. 连接鉴权
```
位置: svr/cmd/framesync/main.go → -token 参数
实现: SessionBind 时验证 token
行为: token 不匹配 → playerId=0 响应
默认: 空 token = 不鉴权
```

## 安全配置

```go
// svr/transport/security.go
type SecurityConfig struct {
    MaxMessageSize    int           // 64KB
    MaxMessagesPerSec int           // 500
    BurstAllowance    int           // 20
    RequireAuth       bool          // false（默认不鉴权）
    AuthToken         string        // ""
}

func DefaultSecurityConfig() SecurityConfig {
    return SecurityConfig{
        MaxMessageSize:    65536,
        MaxMessagesPerSec: 500,
        BurstAllowance:    20,
    }
}
```

## 未实施（生产前需要）

| 措施 | 说明 | 优先级 |
|------|------|--------|
| TLS/SSL | 传输层加密 | 高 |
| Token 过期 | 当前 token 是静态的 | 高 |
| IP 黑名单 | 防 DDoS | 中 |
| 消息内容校验 | 验证 Data 格式合法性 | 中 |
| 输入频率校验 | 服务端检测异常高频输入 | 中 |
| 重放攻击防护 | Seq 单调递增校验 | 低 |
| 玩家身份验证 | 对接外部账号系统 | 取决于业务 |

## 安全相关的踩坑

### 客户端 60fps 触发限流
```
原因: SendInput 在 Unity Update 中调用
修复: 输入节流到 20fps（服务器帧率）
      服务器限流提高到 500 + burst 20
```

### 恶意大包 OOM
```
风险: 客户端发 1GB Data → 服务器 OOM
防护: MaxMessageSize = 64KB，超出立即断开
```

### Room panic crash 进程
```
风险: 一个房间的逻辑错误导致整个服务器崩溃
防护: tickLoop 内 defer recover()
```
