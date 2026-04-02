# Transport Layer — TCP/KCP/Conn

## 文件索引

| File | 职责 |
|---|---|
| `svr/transport/tcp_server.go` | TCP 服务器：acceptLoop、handleConn、连接管理 |
| `svr/transport/kcp_server.go` | KCP 服务器：镜像 TCP 结构，UDP 上的可靠传输 |
| `svr/transport/security.go` | SecurityConfig + 两级限流器 |

## Server 接口

```go
type Server interface {
    Listen(addr string) error
    Close() error
    Wait()
    ConnCount() int
    SetOnDisconnect(func(conn *Conn))
    SetOnRateLimited(func(conn *Conn))
    SetSecurity(cfg SecurityConfig)
    SetMaxConns(n int)
}
```

`NewServer(proto string, handler Handler)` 根据 `proto` ("tcp"/"kcp") 创建对应实现。

## Conn 结构

```go
type Conn struct {
    ID     int
    conn   net.Conn
    writer *codec.FrameWriter  // 带缓冲的帧写入器
    mu     sync.Mutex          // 保护 writer 的并发写
    rateLimiter *RateLimiter   // 每连接限流
}
```

- `Send(msg *codec.Message) error` — 加锁写入 + Flush
- `Close()` — 关闭底层 net.Conn
- `RemoteAddr()` — 返回对端地址

## TCP Server 细节

| 参数 | 值 | 说明 |
|---|---|---|
| TCP_NODELAY | true | 禁用 Nagle，降低延迟 |
| TCP_KEEPALIVE | 30s | OS 层保活 |
| ReadDeadline | 60s | 无消息则断开（靠心跳续命） |
| AcceptLoop | 每连接一个 goroutine | `go handleConn(conn)` |

### 连接生命周期

```
Accept → IPRateLimit 检查 → MaxConns 检查 → 分配 Conn
  → 读循环: FrameReader.ReadMessageCopy() → handler(conn, msg)
  → 读错误/超时 → 关闭连接 → OnDisconnect 回调
```

## KCP Server 细节

| 参数 | 值 | 说明 |
|---|---|---|
| nodelay | 1 | 立即发送 |
| interval | 10ms | 内部更新间隔 |
| resend | 2 | 快速重传阈值 |
| nc | 1 | 无拥塞控制 |
| sndwnd/rcvwnd | 256/256 | 发送/接收窗口 |
| MTU | 1400 | 适配大多数网络 |
| FEC | 无 | 未启用前向纠错 |

KCP 基于 UDP，无 OS 层 KeepAlive，完全依赖 ReadDeadline + 客户端心跳。

## 限流机制

### Per-Connection RateLimiter

```go
type RateLimiter struct {
    maxPerSec int
    count     int32     // 当前窗口计数
    windowStart int64   // 窗口起始时间（UnixNano）
}
```

滑动 1 秒窗口。超限返回 `false`，调用方断开连接。

### Per-IP IPRateLimiter

```go
type IPRateLimiter struct {
    rates sync.Map  // IP(string) → *ipRate
    limit int       // 默认 10 连接/秒/IP
}
```

在 Accept 后、分配 Conn 前检查。防止单 IP 刷连接。

## PlayerConn 装饰器链

```
transport.Conn (原始连接)
    → statsConn (cmd/framesync/stats.go — 记录 TX 字节到 GameStats + MsgLog)
        → simConn (cmd/framesync/netsim.go — 注入延迟/抖动/丢包)
```

`framesync.PlayerConn` 接口：`Send(msg *codec.Message) error`
三层都实现该接口，在 main.go 中按需包装。
