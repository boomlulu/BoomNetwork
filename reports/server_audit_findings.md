# BoomNetwork Go 服务器隐患审计报告

**日期**: 2026-04-10
**范围**: `svr/framesync/`, `svr/cmd/framesync/`, `svr/transport/`, `svr/codec/`, `svr/session/`

---

## 严重程度说明

- **P0 — 生产事故级**: 已知或高概率导致数据错误、崩溃、安全漏洞
- **P1 — 高风险**: 特定条件下触发，影响可用性或数据一致性
- **P2 — 中风险**: 资源泄漏、性能退化、运维盲区
- **P3 — 低风险**: 代码质量、防御性编程、未来维护负担

---

## P0 — 生产事故级

### 1. Replay/实时帧竞态（已知，desync_duplicate_frame_4003_v2.md）

**位置**: `main.go` handleReconnect / handleJoinRoom / handleMatchRoom

已有详细分析文档，此处不再赘述。v2 修复方案已评审通过，建议立即上线。

### 2. handleMatchRoom 的 replay goroutine 无超时、无 context 取消

**位置**: `main.go:965-1001`

```go
go func() {
    time.Sleep(10 * time.Millisecond)
    // ... replay 全部帧，无超时保护 ...
}()
```

handleJoinRoom 的 goroutine 有 `context.WithTimeout(30s)`，但 handleMatchRoom 完全没有。如果客户端 conn 的写缓冲阻塞（慢网络 + 大量帧），这个 goroutine 会永远卡在 `sendMsg` 上。积累足够多后会耗尽内存或 goroutine。

**修复**: 与 handleJoinRoom 保持一致，加 `context.WithTimeout`。

### 3. handleMatchRoom 的 KV 同步 goroutine 存在数据竞态

**位置**: `main.go:1005-1011`

```go
go func() {
    time.Sleep(15 * time.Millisecond)
    entries, version := room.GetDataSnapshot()
    sendMsg(conn, ...)
}()
```

这个 goroutine 独立于上面的 replay goroutine，它俩同时向同一个 `conn` 写数据（一个发帧，一个发 KV）。虽然 `conn.Send()` 内部有 mutex 保证单条消息的原子写入，但两个 goroutine 的消息交错顺序不可控——客户端可能在收到 StartFrameSync 之前就收到了 KV 数据。

对比 handleJoinRoom 将 KV 同步放在 replay goroutine 内部（串行），handleMatchRoom 拆成了两个并发 goroutine，行为不一致。

**修复**: 将 KV 同步合并到 replay goroutine 内，与 handleJoinRoom 保持一致。

---

## P1 — 高风险

### 4. playerCounter 溢出后 playerId 冲突

**位置**: `main.go:454`

```go
var playerCounter int32

func nextPlayerId() int32 {
    return atomic.AddInt32(&playerCounter, 1)
}
```

int32 最大值 2,147,483,647。对于长期运行的服务器（不重启），如果累计连接数超过 21 亿，playerId 会溢出变为负数，与现有玩家冲突，导致映射表（connPlayerMap、playerRoomMap 等）被覆盖——玩家 A 的操作会被路由到玩家 B 的房间。

对于帧同步游戏服务器这种生命周期可能很长的进程来说，这不是理论风险。假设每秒 100 个新连接，248 天就会溢出。

**修复**: 改为 `int64` 或 `uint64`，或在服务器维护窗口期间定期重启清零。

### 5. handleReconnect 中旧连接关闭的竞态窗口

**位置**: `main.go:630-636`

```go
if oldConn, ok := playerConnMap.Load(playerId); ok {
    oldC := oldConn.(*transport.Conn)
    if oldC != conn {
        connPlayerMap.Delete(oldC.ID)
        oldC.Close()
    }
}
// 更新连接映射
connPlayerMap.Store(conn.ID, playerId)
playerConnMap.Store(playerId, conn)
```

`oldC.Close()` 会触发 `onClientDisconnect` 回调。虽然回调中有 CAS 检查（`currentConn == conn`），但 `connPlayerMap.Delete(oldC.ID)` 在 `oldC.Close()` 之前执行——如果在 Delete 和 Close 之间，旧连接恰好收到最后一条消息并被 dispatch，dispatch 会发现 connId 已不存在映射，消息被静默丢弃。这不算严重，但如果消息恰好是 FrameInput，客户端会丢一帧输入。

**建议**: 先 Close，再 Delete。或者接受当前行为作为 known limitation。

### 6. IPRateLimiter 的 sync.Map 永远不会被清理

**位置**: `transport/security.go:128-155`

```go
type IPRateLimiter struct {
    m sync.Map // IP string → *ipRate
}
```

每个新 IP 会在 sync.Map 中创建一个条目，但没有任何清理机制。对于暴露在公网的服务器，扫描器会不断从不同 IP 连接，导致 sync.Map 无限增长。

**修复**: 加一个后台 goroutine，定期（如每 5 分钟）遍历并删除超过 10 分钟未活跃的 IP 条目。

### 7. WsServer 未传递 WriteTimeout

**位置**: `transport/ws_server.go:254-259`

```go
c := &Conn{
    ID:          s.nextID,
    conn:        adapted,
    writer:      codec.NewFrameWriter(adapted),
    rateLimiter: NewRateLimiter(s.security.MaxMessagesPerSec),
    // 缺少: writeTimeout: s.config.WriteTimeout
}
```

TcpServer 在创建 Conn 时设置了 `writeTimeout`（C1 fix），但 WsServer 没有。WebSocket 客户端如果发送缓冲区满，`Send()` 会无限阻塞，最终拖慢 `stepFrame()` 的广播循环（虽然 `stepFrame` 收集失败后会断开，但阻塞本身就是问题）。

**修复**: WsServer 创建 Conn 时也传入 `writeTimeout`。

### 8. frameHashes map 无限增长

**位置**: `room.go:174`

```go
frameHashes map[uint32]map[int32]uint32 // frameNumber → playerId → hash
```

每帧的 hash 被收集后从未清理。一个运行 2400 帧（120 秒 @ 20fps）的房间会积累 2400 个 map 条目，每个条目又包含一个 playerId→hash 的子 map。对于长时间运行的房间（比如 30 分钟 = 36000 帧），内存消耗不可忽略。

**修复**: 只保留最近 N 帧的 hash（如 200 帧），超出的自动清理。

---

## P2 — 中风险

### 9. handleRequestStart 的 `time.Sleep(10ms)` 是脆弱的时序依赖

**位置**: `main.go:1052-1055`

```go
go func() {
    time.Sleep(10 * time.Millisecond) // 确保本消息处理完
    room.Start()
}()
```

用 sleep 来保证"当前消息处理完"是不可靠的。在高负载下，handler 的执行时间可能超过 10ms（GC pause、调度延迟），导致 `room.Start()` 在 handler 返回之前就执行了。虽然 `room.Start()` 内部有 `running` 状态检查，不会 panic，但客户端可能在收到 RequestStart 的响应之前就收到了 StartFrameSync 广播，导致时序混乱。

**修复**: 不用 sleep，让 handler 先返回响应，再通过 channel 或 defer 触发 Start。

### 10. Encode 返回的 buffer 归还到 Pool 后可能被并发覆盖

**位置**: `codec/message.go:121-138`

```go
func Encode(msg *Message) []byte {
    bufPtr := bufPool.Get().(*[]byte)
    buf := *bufPtr
    // ... encode ...
    return buf  // 返回 pool buffer 的引用
}

func PutBuf(buf []byte) {
    buf = buf[:0]
    bufPool.Put(&buf)
}
```

`Encode` 返回 pool buffer 的直接引用。如果调用方在 `PutBuf` 之后仍然持有 buf 的子切片，而另一个 goroutine 从 pool 取出同一块 buffer 并覆盖，就会产生数据竞态。当前唯一的调用路径 `WriteMessage` 是安全的（Write 后立即 PutBuf），但这个 API 设计对未来的调用方来说是一个陷阱。

**建议**: 在 `Encode` 的文档注释中明确标注"返回的 buffer 必须通过 PutBuf 归还，归还后不得再访问"。

### 11. `MaxMessageSize` 是全局可变变量，多处写入

**位置**: `codec/framing.go:10`, `transport/tcp_server.go:106`, `transport/ws_server.go:158`

```go
var MaxMessageSize = 65536

// tcp_server.go
func (s *TcpServer) SetSecurity(cfg SecurityConfig) {
    codec.MaxMessageSize = cfg.MaxMessageSize
}

// ws_server.go（同样写）
```

TcpServer 和 WsServer 的 `SetSecurity` 都会写这个全局变量。如果两者设置了不同的值，后写入的会覆盖前一个。虽然当前代码中两者使用相同的 `secCfg`，但这个设计对未来是个隐患。

**修复**: 将 `MaxMessageSize` 从全局变量改为 `FrameReader` 的实例字段（当前已经有 `fr.maxMessageSize`），去掉全局变量的写入。

### 12. Room.Start() 在锁外调用 broadcast 和 delegate

**位置**: `room.go:748-783`

```go
func (r *Room) Start() {
    r.mu.Lock()
    // ... set running=true, init state ...
    d := r.delegate
    r.mu.Unlock()

    r.broadcast(...)   // 锁外广播
    go r.tickLoop()

    if d != nil {
        d.OnRoomStarted(r)  // 锁外回调
    }
}
```

`r.broadcast` 在锁外执行，此时 `tickLoop` goroutine 也已经启动。如果 tickLoop 立即执行 `stepFrame()` 并开始广播帧，客户端可能在收到 `CmdStartFrameSync` 之前就收到了 frame 1。这与 `handleRequestStart` 的 10ms sleep 问题叠加，进一步增大了时序混乱的概率。

**修复**: 在锁内广播 StartFrameSync，或在 tickLoop 中添加一个初始延迟（比如跳过第一个 tick）。

### 13. reconnect 中 `room.IsRunning()` 和 `room.IsGamePaused()` 的 TOCTOU

**位置**: `main.go:667-679`

```go
if room.IsRunning() {
    room.EnqueueEvent(...)
} else {
    broadcastToRoom(...)
}
// ...
if room.IsGamePaused() {
    sendMsg(conn, ...)
}
```

`IsRunning()` 和 `IsGamePaused()` 各自加锁读取，但两次读取之间房间状态可能已经改变。例如：读到 `IsRunning()=true` 后，房间恰好被停止，EnqueueEvent 写入了一个已停止房间的 pendingEvents——这些事件永远不会被消费（tickLoop 已退出），造成静默的事件丢失。

**建议**: 对于需要原子判断多个状态的场景，在一次加锁内完成所有读取。

---

## P3 — 低风险 / 代码质量

### 14. `sendMsg` 忽略了 `conn.Send` 的错误

**位置**: `main.go:1084`

```go
func sendMsg(conn *transport.Conn, msg *codec.Message) {
    GameStats.RecordTx(...)
    framesync.Metrics.BytesSent.Add(...)
    logMsgFromMsg(...)
    conn.Send(msg)  // 返回值被忽略
}
```

发送失败不会被感知。对于 replay 路径中的 sendMsg 调用，如果连接已断开，会持续往一个坏连接写数据直到系统报错，浪费 CPU。

### 15. `connPlayerMap` 使用 `sync.Map` 且 key 类型不一致

`connPlayerMap` 的 key 是 `int`（conn.ID），`playerRoomMap` 的 key 是 `int32`（playerId）。虽然有 `loadConnPlayerId` 等辅助函数做类型安全检查，但 `sync.Map` 本身的 key 类型不受编译器约束，直接 `.Store(conn.ID, playerId)` 很容易传错类型。

**建议**: 长期来看，用泛型 map + mutex 替代 sync.Map（Go 1.18+），获得编译期类型检查。

### 16. admin /health 端点暴露 Go 版本信息

**位置**: `admin.go:153`

```go
fmt.Fprintf(w, `{..."goVersion":%q}`, runtime.Version())
```

公开 Go 运行时版本可能帮助攻击者精确定位已知漏洞。/health 不鉴权，任何人都能查到。

**建议**: 从 /health 响应中移除 `goVersion` 和 `buildHash`，或将其移到鉴权后的 /stats 端点。

### 17. handleAdminCreateRoom 中 MatchKey 被赋值两次

**位置**: `admin.go:482-487`

```go
room := roomMgr.CreateRoomWithMaxPlayers(maxPlayers, matchKey)
// ...
room.MatchKey = matchKey  // 重复赋值
```

`CreateRoomWithMaxPlayers` 内部已经设置了 `room.MatchKey = matchKey`（room_manager.go:159），外层又赋值一次。无害但多余，且没有锁保护。

### 18. countOnlinePlayers 使用 atomic.AddInt64 做计数是多余的

**位置**: `admin.go:497-504`

```go
func countOnlinePlayers() int {
    var count int64
    connPlayerMap.Range(func(_, _ any) bool {
        atomic.AddInt64(&count, 1)  // count 是局部变量，不会被并发访问
        return true
    })
    return int(count)
}
```

`count` 是栈上局部变量，只在 Range 的回调中使用（回调是串行的），不需要 atomic。直接 `count++` 即可。

---

## 总结

| 级别 | 数量 | 关键项 |
|------|------|--------|
| P0 | 3 | replay/实时帧竞态、matchRoom 无超时、matchRoom KV 数据竞态 |
| P1 | 5 | playerCounter 溢出、IP limiter 泄漏、WS 无写超时、frameHashes 泄漏、重连竞态窗口 |
| P2 | 5 | Start 时序、Encode pool 风险、全局 MaxMessageSize、TOCTOU |
| P3 | 5 | sendMsg 忽略错误、类型安全、信息泄露 |

**建议优先级**:
1. 立即修复 P0 #1（v2 方案上线）+ P0 #2 + P0 #3（都是几行的改动）
2. 本周修复 P1 #4（playerCounter 溢出）和 P1 #7（WS writeTimeout）
3. 排期修复剩余 P1 和 P2
