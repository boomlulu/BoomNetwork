# BoomNetwork Go 服务器二轮审计 — 性能优化 · 架构改进 · 隐患排查

**审计日期**: 2026-04-11
**审计范围**: `svr/` 全部 Go 源码（修复后版本）
**审计类型**: 性能优化 + 架构改进 + 残留/新增隐患

---

## 一、首轮修复验收

### 修复状态总览

| 编号 | 问题 | 状态 | 备注 |
|------|------|------|------|
| P0-01 | Replay/实时帧竞态 | ✅ 已修复 | deliveryLoop 两阶段 + 单写者原则 |
| P0-02 | matchRoom 无超时 | ✅ 已修复 | context.WithTimeout(30s) |
| P0-03 | KV 同步竞态 | ✅ 已修复 | context-guarded GetDataSnapshot |
| P1-04 | playerCounter 溢出 | ✅ 已修复 | atomic wraparound to 1 |
| P1-05 | 重连旧连接竞态 | ✅ 已修复 | 先更新映射再关闭旧连接 |
| P1-06 | IP 限流表泄漏 | ✅ 已修复 | Cleanup() + 5min 定期清扫 |
| P1-07 | WS 缺写超时 | ✅ 已修复 | writeTimeout 注入 wsConn |
| P1-08 | frameHashes 泄漏 | ⚠️ 部分修复 | 每 100 帧清理，但依赖帧号触发 |
| P2-09 | RequestStart sleep | ✅ 已修复 | 直接 room.Start() |
| P2-10 | Encode pool 安全 | ✅ 已修复 | — |
| P2-11 | 全局 MaxMessageSize | ⚠️ 部分修复 | FrameReader 有实例字段，但全局变量仍存在 |
| P2-12 | Start 广播竞态 | ✅ 已修复 | running 状态机串行化 |
| P2-13 | TOCTOU | ⚠️ 改善 | handleReconnect 用 GetState()，joinRoom/matchRoom 未统一 |
| P3-14 | sendMsg 忽略 error | ✅ 已修复 | — |
| P3-15 | sync.Map 类型安全 | 📋 长期 | 辅助函数保护，泛型替换可排期 |
| P3-16 | /health 信息泄露 | ✅ 已修复 | 仅返回 status/rooms/players/uptime |
| P3-17 | MatchKey 重复赋值 | ✅ 已修复 | — |
| P3-18 | 多余 atomic | ✅ 已修复 | — |

**结论**: 18 项中 15 项完全修复，2 项部分修复需跟进，1 项长期排期。整体质量大幅提升。

---

## 二、性能优化建议

### PERF-01: stepFrame 编码期间不必要的 copy（中等收益）

**文件**: `room.go:1001-1020`
**现状**: stepFrame 在锁外编码到 `r.frameBuf`，然后重新获锁 copy 到 frameRing slot。

```go
// 锁外编码
EncodeFrameData(frame, r.frameBuf)

// 重新获锁后 copy
r.mu.Lock()
copy(slot.EncodedData, r.frameBuf[:size])
```

**问题**: 每帧一次 `copy()`，帧大小通常 100~500 bytes，20fps = 每秒 2000~10000 bytes 的多余拷贝。虽然单次 copy 开销低，但在高频热路径上可以优化。

**优化方案**: 使用 ping-pong 双缓冲直接编码到 ring slot，省掉中间 copy：

```go
// 方案 A：直接编码到 slot（需要预获锁确定 slot 位置）
r.mu.Lock()
slot := &r.frameRing[r.frameRingPos]
if cap(slot.EncodedData) >= size {
    slot.EncodedData = slot.EncodedData[:size]
} else {
    slot.EncodedData = make([]byte, size)
}
EncodeFrameData(frame, slot.EncodedData) // 直接编码到目标
// ... 推进 ringPos, 投递 frameCh ...
r.mu.Unlock()
```

**权衡**: 编码回到锁内执行，增加持锁时间 ~1μs（编码极轻量），但消除了锁外→锁内的 copy。对于小帧（< 1KB），两者差距可忽略。大帧（快照级帧 > 4KB）收益更明显。

**改动量**: ~10 行
**收益**: 消除热路径 copy，减少 ~20% 的 stepFrame CPU 开销（大帧场景）

---

### PERF-02: deliveryLoop 的 time.After 在追帧阶段创建大量临时 Timer（中等收益）

**文件**: `room.go:1075-1081`
**现状**:

```go
if (i+1)%replayBatchSize == 0 && i+1 < len(frames) {
    select {
    case <-ctx.Done():
        return
    case <-time.After(replayBatchDelay):
    }
}
```

**问题**: `time.After()` 每次调用创建一个新的 `time.Timer`，直到 GC 回收前不会释放。在追帧大量历史帧（如 2400 帧 / batch 50 = 48 次 timer 创建）时，会产生短暂的 timer 堆积。

**优化方案**:

```go
// 在 deliveryLoop 开头创建一个复用 timer
replayTimer := time.NewTimer(0)
if !replayTimer.Stop() {
    <-replayTimer.C
}
defer replayTimer.Stop()

// 使用时 Reset 而非新建
if (i+1)%replayBatchSize == 0 && i+1 < len(frames) {
    replayTimer.Reset(replayBatchDelay)
    select {
    case <-ctx.Done():
        return
    case <-replayTimer.C:
    }
}
```

**改动量**: ~8 行
**收益**: 消除追帧阶段的 timer 对象分配，减少 GC 压力

---

### PERF-03: BroadcastReliable 多次锁获取可合并为一次（低-中收益）

**文件**: `room.go:609-631`
**现状**: BroadcastReliable 先获锁收集 player 列表，释放锁，然后对每个 player 调用 SendReliableToPlayer（每次重新获锁）。

```go
func (r *Room) BroadcastReliable(excludePlayerId int32, inner *codec.Message) {
    // 获锁收集 players...
    r.mu.Unlock()
    for _, p := range players {
        r.SendReliableToPlayer(p.ID, inner) // 每次重新获锁
    }
}
```

**问题**: N 个玩家 = N+1 次锁获取/释放。在 8 人房间，每次 BroadcastReliable 有 9 次 mutex 操作。

**优化方案**: 内联 SendReliableToPlayer 的逻辑，在一次锁内完成所有操作：

```go
func (r *Room) BroadcastReliable(excludePlayerId int32, inner *codec.Message) {
    r.mu.Lock()
    type pending struct {
        conn PlayerConn
        msg  *codec.Message
    }
    var sends []pending
    for _, p := range r.players {
        if p.ID == excludePlayerId || p.State != PlayerOnline {
            continue
        }
        p.s2cSeq++
        data := EncodeReliableMsgData(p.s2cSeq, inner)
        msg := codec.NewExtMessage(ExtCmdReliableMsg, data)
        slot := p.s2cSeq % s2cBufSize
        p.s2cBuf[slot] = cachedS2CMsg{seq: p.s2cSeq, msg: msg}
        if p.s2cSeq >= s2cBufSize {
            p.s2cBufHead = p.s2cSeq - s2cBufSize + 1
        }
        if p.Conn != nil {
            sends = append(sends, pending{conn: p.Conn, msg: msg})
        }
    }
    r.mu.Unlock()
    for _, s := range sends {
        _ = s.conn.Send(s.msg)
    }
}
```

**改动量**: ~25 行
**收益**: 锁操作从 N+1 次降为 1 次，减少 mutex 竞争

---

### PERF-04: handleFrameInput 每帧调用 roomMgr.GetRoom 进行 map 查找（高收益）

**文件**: `main.go:574-587`
**现状**:

```go
func handleFrameInput(conn *transport.Conn, msg *codec.Message) *codec.Message {
    ctx, ok := loadConnContext(conn.ID)
    if !ok { return nil }
    // H6: 每帧验证房间仍在 RoomManager 中
    if roomMgr != nil && roomMgr.GetRoom(ctx.room.ID) == nil {
        connContextMap.Delete(conn.ID)
        return nil
    }
    ctx.room.OnInput(ctx.playerId, msg.Data)
    ...
}
```

**问题**: `roomMgr.GetRoom()` 内部获取 RoomManager 的 `mu.RLock()`。每帧每个客户端都调用一次。20fps × 50 玩家 = 1000 次/秒的 RoomManager RLock，而房间移除只发生在 Reconciler 每 5 秒一次。这是典型的 hot-path cold-check。

**优化方案 A** — 在 Room 上添加 atomic "alive" 标志:

```go
// Room 增加字段
alive atomic.Bool // 在 createRoomLocked 时设 true，RemoveRoom 时设 false

// handleFrameInput 用 O(1) atomic 检查替代 map 查找
if !ctx.room.IsAlive() {
    connContextMap.Delete(conn.ID)
    return nil
}
```

**优化方案 B** — 移除热路径检查，依赖 disconnect 清理:

房间被移除时，Reconciler 会驱逐玩家 → 触发 OnPlayerRemoved → 清理映射。handleFrameInput 对已移除房间写入 OnInput 是无害的（inputs 不会被消费，因为 tickLoop 已停止）。可以完全移除这个检查。

**改动量**: 方案 A ~5 行，方案 B 删 4 行
**收益**: 消除热路径上 1000 次/秒的 RLock（50 玩家场景），是最大的单点性能优化

---

### PERF-05: netsim simMu 全局互斥锁瓶颈（低收益，仅在启用网络模拟时生效）

**文件**: `netsim.go:41-52`
**现状**: 每次 `simConn.Send()` 调用 `simRandIntn()` 时获取全局 `simMu` 互斥锁生成随机数。

**优化方案**: 使用 `math/rand/v2` 的并发安全全局函数，或 per-goroutine `rand.Rand`（Go 1.22+ 的全局 rand 已是并发安全的）：

```go
// Go 1.22+：直接用全局 rand，无需锁
func simRandIntn(n int) int { return rand.Intn(n) }
```

**改动量**: 3 行
**收益**: 仅在网络模拟启用时有效果，生产环境无收益

---

### PERF-06: ReadMemStats STW 已有 5s 缓存，但可进一步优化（低收益）

**文件**: `admin.go:591`
**现状**: `handlePerf` 调用 `runtime.ReadMemStats()`，已有 5 秒缓存。

**优化建议**: 如果 /perf 端点被频繁调用（如 Grafana 1 秒抓取），5 秒缓存足够。但 ReadMemStats 本身会 STW ~100μs，可以考虑：
1. 将 ReadMemStats 移到后台 goroutine 定期执行，/perf 只读缓存
2. 替换为 `runtime.MemStats` 的部分字段（如 HeapAlloc 可通过 runtime/metrics 无 STW 获取）

**改动量**: ~15 行（后台 goroutine 方案）
**收益**: 消除 admin API 调用导致的偶发 STW

---

### 性能优化优先级排序

| 优先级 | 编号 | 收益 | 改动量 | 推荐 |
|--------|------|------|--------|------|
| 🔴 高 | PERF-04 | 消除热路径 RLock | 5 行 | **立即执行** |
| 🟡 中 | PERF-01 | 消除热路径 copy | 10 行 | 本周执行 |
| 🟡 中 | PERF-02 | 减少 GC 压力 | 8 行 | 本周执行 |
| 🟡 中 | PERF-03 | 减少锁竞争 | 25 行 | 本周执行 |
| ⚪ 低 | PERF-05 | 仅 netsim 场景 | 3 行 | 有空再做 |
| ⚪ 低 | PERF-06 | 消除偶发 STW | 15 行 | 有空再做 |

---

## 三、架构改进建议

### ARCH-01: 用 atomic 标志替代 sync.Map 存活检查（推荐）

**现状**: 多处代码通过 `roomMgr.GetRoom(id) == nil` 判断房间是否存活。RoomManager 内部用 `map[int32]*Room` + `sync.Mutex`，每次查找需获锁。

**问题**: 
1. 热路径（handleFrameInput）不应依赖全局锁
2. 房间删除是低频操作，但存活检查是高频操作

**建议**: Room 增加 `alive atomic.Bool`，RoomManager.RemoveRoom 时原子设 false。所有"房间是否还活着"的检查改用 `room.IsAlive()`。

```go
type Room struct {
    ...
    alive atomic.Bool
}
func (r *Room) IsAlive() bool { return r.alive.Load() }
```

**影响面**: room.go + room_manager.go + main.go
**收益**: 消除热路径锁依赖，O(1) 原子检查

---

### ARCH-02: 分离 Room 的"帧推送"和"状态管理"职责（中长期）

**现状**: `Room` struct 承载了过多职责：帧推送、快照管理、KV 存储、实体权威、Desync 检测、可靠通道。197 行的 struct 定义，1400+ 行的方法。

**建议**: 将 Room 拆分为多个组合模块：

```
Room (核心帧推送 + 玩家管理)
├── FrameBuffer     (环形帧缓冲区)
├── SnapshotStore   (快照存储 + 新鲜度检测)
├── DataStore       (KV 存储)
├── EntityAuthority (实体权威表)
├── DesyncDetector  (帧哈希收集 + 不一致检测)
└── ReliableChannel (S2C 可靠通道)
```

**收益**:
1. 每个模块可独立测试
2. 不需要的功能可以不初始化（如不需 KV 的房间不分配 DataStore）
3. 锁粒度可以细化（DesyncDetector 用自己的锁，不阻塞 stepFrame）
4. 代码可读性大幅提升

**改动量**: 大（重构级别）
**风险**: 低（纯结构重组，不改变行为）

---

### ARCH-03: 引入连接级 Context 替代多个 sync.Map（中期）

**现状**: 4 个 sync.Map 存储连接/玩家/房间映射：

```go
var connPlayerMap sync.Map // connID → int32
var playerRoomMap sync.Map // int32 → *Room
var playerConnMap sync.Map // int32 → *Conn
var connContextMap sync.Map // connID → *connContext (快速路径缓存)
```

`connContextMap` 已经是第一步优化（将 2 次 sync.Map 查找合并为 1 次）。但 4 个 map 之间的一致性仍然是手动维护的。

**建议**: 使用统一的 `PlayerSession` 结构体 + 一个 sharded map：

```go
type PlayerSession struct {
    PlayerID int32
    Room     *Room
    Conn     atomic.Pointer[transport.Conn] // 支持原子替换（重连）
    State    atomic.Int32                   // PlayerOnline/Disconnected
}

type SessionStore struct {
    byConn   [N]shard // connID → *PlayerSession
    byPlayer [N]shard // playerID → *PlayerSession
}
```

**收益**:
1. 映射一致性由结构保证，不再需要手动同步 4 个 map
2. Sharded map 比 sync.Map 在高写入场景下性能更好（sync.Map 优化的是 read-heavy）
3. 减少类型断言（编译期安全）
4. 重连只需 CAS 替换 `Conn` 指针，无需同时更新 3 个 map

**改动量**: 大（但可以渐进式替换）
**时机**: 与 P3-15（sync.Map 类型安全）合并执行

---

### ARCH-04: 帧推送改用 io.Writer 流式编码（小幅架构优化）

**现状**: stepFrame 的编码路径：

```
pendingInputs → FrameData struct → EncodeFrameData(frameBuf) → copy(slot.EncodedData)
```

中间经过 FrameData 临时结构体 + 手动 size 计算 + 编码到 scratch buffer + copy 到 ring。

**建议**: 直接用 `EncodeFrameDataTo(w io.Writer)` 模式编码到目标 buffer：

```go
slot.EncodedData = slot.EncodedData[:0]
slot.EncodedData = EncodeFrameDataAppend(slot.EncodedData, frameNum, inputs, events)
```

**收益**: 消除中间 FrameData 分配 + copy，一步到位
**改动量**: ~20 行
**配合**: 与 PERF-01 同时执行

---

### ARCH-05: Graceful Shutdown 缺少 WebSocket 服务关闭（隐患）

**文件**: `main.go:355-382`
**现状**: 关机顺序是 broadcast → cancel → roomMgr.StopAll → server.Close → Wait。

但 `wsServer` 没有被显式关闭。cancel() 取消了 admin server 的 context，但 wsServer 的 listener 没有被 Close()。

```go
// 当前代码
server.Close()  // 只关闭 TCP server
// wsServer.Close() 缺失！
```

**问题**: WebSocket 客户端在关机期间可能继续发送消息到已停止的房间。

**修复**:

```go
// 在 server.Close() 之后
if wsServer != nil {
    wsServer.Close()
}
```

**改动量**: 3 行
**优先级**: P1（生产关机场景可能导致错误日志或 goroutine 泄漏）

---

### ARCH-06: Admin Server 无 Graceful Shutdown（改善建议）

**文件**: `admin.go:40-93`
**现状**: admin HTTP server 通过 `go startAdminServer(ctx, ...)` 启动，context 取消时 `http.Server.Shutdown(shutdownCtx)` 被调用。但 shutdown timeout 是 5 秒硬编码，且 GM WebSocket hub 的关闭没有等待。

**建议**: 将 shutdown timeout 改为可配置，并确保 GM hub 的所有 WebSocket 连接被正常关闭。

---

## 四、新发现的隐患

### NEW-01: frameHashes 清理依赖帧号取模，低活跃房间可能不触发（P2）

**文件**: `room.go:1322`
**现状**:

```go
if frameNumber > maxHashHistory && frameNumber%100 == 0 {
    // 清理旧 hash...
}
```

**问题**: 如果房间被暂停（gamePaused 或 snapshotPaused），帧号不递增，清理永远不触发。在暂停前已收集的 frameHashes 会一直驻留内存。虽然每个 hash entry 只有 ~20 bytes，但长时间暂停的房间（如等待玩家重连）可能积累数千条。

**修复方案**: 在 GamePause 和 SnapshotPause 时主动清空 frameHashes：

```go
func (r *Room) GamePause() {
    r.mu.Lock()
    r.gamePaused = true
    r.frameHashes = make(map[uint32]map[int32]uint32) // 暂停时释放
    r.mu.Unlock()
    // ...
}
```

**改动量**: ~3 行
**优先级**: P2

---

### NEW-02: framing.go 全局 MaxMessageSize 仍存在竞态（P2 残留）

**文件**: `framing.go:11, 177`
**现状**: FrameReader 已有实例级 `maxMessageSize` 字段（部分修复了 P2-11），但：
1. 全局变量 `MaxMessageSize` 仍然存在
2. `NewFrameReader()` 以全局变量作为默认值
3. 独立函数 `ReadFrame()` 仍然直接读取全局变量

**问题**: 如果 TCP 和 WS 服务设置不同的 MaxMessageSize，存在竞态。

**修复方案**: 将 MaxMessageSize 改为 NewFrameReader 的构造参数：

```go
func NewFrameReader(r io.Reader, maxMsgSize int) *FrameReader {
    return &FrameReader{rd: bufio.NewReader(r), maxMessageSize: maxMsgSize}
}
```

**改动量**: ~10 行（改构造签名 + 所有调用方）
**优先级**: P2

---

### NEW-03: handleReconnect 多次非原子读取房间状态（P2）

**文件**: `main.go:611-695`
**现状**: handleReconnect 在 line 611 从 playerRoomMap 加载 room，然后分别调用：
- `room.CurrentFrameNumber()` (line 632)
- `room.GetSnapshot()` (line 633)
- `room.OldestBufferedFrame()` (line 637)
- `room.GetState()` (line 695)

这些调用之间房间可能被 Reconciler 销毁或状态变化。

**风险**: GetSnapshot 可能返回 nil（房间正在被清理），导致后续 nil 引用。

**修复方案**: 在一次锁内获取所有需要的快照数据：

```go
type ReconnectSnapshot struct {
    Running     bool
    GamePaused  bool
    Frame       uint32
    OldestFrame uint32
    Snapshot    []byte
    SnapshotFrame uint32
}
func (r *Room) GetReconnectSnapshot() ReconnectSnapshot { ... }
```

**改动量**: ~30 行
**优先级**: P2

---

### NEW-04: handleJoinRoom 容量检查与 AddPlayer 之间无原子保证（P2）

**文件**: `main.go:785-791`
**现状**:

```go
if room.PlayerCount() >= room.MaxPlayers() {  // line 785: check
    // ...
}
// ... 十几行其他逻辑 ...
room.AddPlayer(...)  // line 791: act
```

**问题**: TOCTOU — 在 check 和 act 之间，其他 goroutine 可能也通过了容量检查。最终可能超过 MaxPlayers。

**修复方案**: AddPlayer 内部原子检查容量，返回 error：

```go
func (r *Room) AddPlayer(...) error {
    r.mu.Lock()
    if len(r.players) >= r.config.MaxPlayers {
        r.mu.Unlock()
        return ErrRoomFull
    }
    // ... add player ...
}
```

**改动量**: ~15 行
**优先级**: P2

---

### NEW-05: admin.go JSON 编码错误被静默忽略（P3）

**文件**: `admin.go:190, 235`
**现状**:

```go
byMKJSON, _ := json.Marshal(byMK)    // line 190
respJSON, _ := json.Marshal(resp)     // line 235
```

**问题**: 如果 marshal 失败（例如数据包含非法浮点数 NaN），`byMKJSON` 为 nil，后续字符串拼接产生损坏的 JSON 响应。

**修复方案**: 检查 error 并返回 500：

```go
byMKJSON, err := json.Marshal(byMK)
if err != nil {
    jsonError(w, "internal error", http.StatusInternalServerError)
    return
}
```

**改动量**: ~10 行
**优先级**: P3

---

### NEW-06: deliveryLoop preamble 发送失败后未通知房间（P3）

**文件**: `room.go:1046-1059`
**现状**: deliveryLoop 的 preamble 发送如果失败（conn.Send error），直接 return。defer 中 onDeliveryExit 会检查 isOnline 并关闭连接。

**问题**: 如果 preamble 只发了一半就失败（比如发了快照但没发 StartFrameSync），客户端会处于不一致状态 — 有快照但不知道帧同步已开始。

**影响**: 低 — 连接关闭后客户端会重连，重连会重新发送完整 preamble。但客户端可能在关闭前尝试解析不完整的消息序列。

**建议**: 在 preamble 失败时增加日志，帮助排查客户端不一致问题。

**改动量**: ~3 行
**优先级**: P3

---

### NEW-07: perfCachedJSON 变量非并发安全（P3）

**文件**: `admin.go:585-606`
**现状**:

```go
// 读
if atomic.LoadInt64(&perfCacheTime)+perfCacheTTL > now && perfCachedJSON != nil {
    w.Write(perfCachedJSON)  // 读 perfCachedJSON（无同步）
}

// 写
perfCachedJSON = json  // 写 perfCachedJSON（无同步）
atomic.StoreInt64(&perfCacheTime, now)
```

**问题**: `perfCachedJSON` 是一个 `[]byte`，多个 goroutine 可能同时读写。虽然 Go 的 slice 赋值是原子的（指针宽度），但 slice header 包含 len+cap+ptr 三个字段，不是原子赋值。

**修复方案**: 使用 `atomic.Pointer[[]byte]` 或 `sync.Mutex`。

```go
var perfCache atomic.Value // stores []byte

// 读
if cached, ok := perfCache.Load().([]byte); ok && ... {
    w.Write(cached)
}
// 写
perfCache.Store(json)
```

**改动量**: ~5 行
**优先级**: P3（实际崩溃概率极低，但属于未定义行为）

---

### 新隐患优先级排序

| 优先级 | 编号 | 问题 | 改动量 |
|--------|------|------|--------|
| P2 | NEW-01 | frameHashes 暂停时不清理 | 3 行 |
| P2 | NEW-02 | MaxMessageSize 全局变量残留 | 10 行 |
| P2 | NEW-03 | handleReconnect 非原子状态读取 | 30 行 |
| P2 | NEW-04 | joinRoom 容量检查 TOCTOU | 15 行 |
| P3 | NEW-05 | admin JSON 编码错误静默 | 10 行 |
| P3 | NEW-06 | preamble 半发送无日志 | 3 行 |
| P3 | NEW-07 | perfCachedJSON 非并发安全 | 5 行 |
| P1 | ARCH-05 | wsServer 未关闭（Graceful Shutdown） | 3 行 |

---

## 五、执行建议

### 立即执行（本次 PR）

1. **PERF-04** — 移除 handleFrameInput 的 GetRoom 检查（或用 atomic alive 替代）
2. **ARCH-05** — 关机时关闭 wsServer

### 本周执行

3. **PERF-01** — stepFrame 编码直接到 ring slot
4. **PERF-02** — deliveryLoop 复用 timer
5. **PERF-03** — BroadcastReliable 合并锁
6. **NEW-01** — 暂停时清空 frameHashes
7. **NEW-02** — 消除全局 MaxMessageSize
8. **NEW-04** — AddPlayer 内部容量检查

### 排期执行

9. **NEW-03** — handleReconnect 原子快照
10. **NEW-05** — admin JSON error 处理
11. **NEW-07** — perfCachedJSON 并发安全

### 中长期

12. **ARCH-02** — Room 职责拆分
13. **ARCH-03** — 统一 SessionStore 替代 4 个 sync.Map
14. **ARCH-04** — 流式编码

---

## 5.1 验收标准 — 逐项验收清单

### PERF-01: stepFrame 编码消除 copy

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | `frameBuf` 中间缓冲区在 stepFrame 中不再使用 | `grep -n 'frameBuf' room.go` | 仅保留在 Room struct 声明（可移除）或测试中 |
| 2 | frameRing slot 的 EncodedData 由 EncodeFrameData 直接写入 | Code review | `EncodeFrameData(frame, slot.EncodedData)` 存在 |
| 3 | 单帧编码性能不退化 | `go test -bench BenchmarkStepFrame -benchtime 10s -count 5` | P99 ≤ 修复前 P99（允许 ±5% 抖动） |
| 4 | 帧数据完整性不受影响 | 集成测试：8 人房 2400 帧完整对局 | 所有客户端收到相同帧序列，无 desync |
| 5 | `go vet ./framesync/...` 通过 | CI | 零 warning |

### PERF-02: deliveryLoop Timer 复用

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | `time.After` 不出现在 deliveryLoop 中 | `grep -n 'time.After' room.go` | 零匹配（deliveryLoop 范围内） |
| 2 | 替换为 `time.NewTimer` + `Reset` | Code review | timer 在 deliveryLoop 开头创建，循环中 Reset |
| 3 | timer 在函数退出时 Stop | Code review | `defer replayTimer.Stop()` 存在 |
| 4 | 追帧 2400 帧无 goroutine 泄漏 | `go test -run TestReplayLargeHistory` + goroutine count | 测试前后 goroutine 差 ≤ 1 |
| 5 | GC 压力降低 | `go test -bench BenchmarkReplay -benchmem` | allocs/op 降低 ≥ 30 个（timer 对象） |

### PERF-03: BroadcastReliable 锁合并

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | BroadcastReliable 内只有 1 次 Lock/Unlock | Code review | 方法体内 `r.mu.Lock()` 出现 1 次 |
| 2 | 不再调用 SendReliableToPlayer（内联） | `grep -n 'SendReliableToPlayer' room.go` | BroadcastReliable 内零匹配 |
| 3 | 8 人房可靠消息全员收到 | `go test -run TestBroadcastReliable8Players` | 每人收到正确 seq 的消息 |
| 4 | 网络发送在锁外执行 | Code review | `conn.Send()` 在 `r.mu.Unlock()` 之后 |
| 5 | 锁竞争降低 | `go test -bench BenchmarkBroadcastReliable -cpu 4` | ns/op 降低 ≥ 20% |

### PERF-04: handleFrameInput 消除 RLock

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | handleFrameInput 不调用 roomMgr.GetRoom | `grep -n 'GetRoom' main.go` | handleFrameInput 函数体内零匹配 |
| 2 | Room.alive 原子标志存在 | Code review | `alive atomic.Bool` 在 Room struct 中 |
| 3 | RemoveRoom 设 alive=false | Code review | `room.alive.Store(false)` 在 RemoveRoom 中 |
| 4 | 热路径延迟降低 | `go test -bench BenchmarkHandleFrameInput -cpu 4` | ns/op 降低 ≥ 40% |
| 5 | 房间移除后输入不再被处理 | `go test -run TestInputAfterRoomRemoved` | OnInput 不被调用 |

### PERF-05: netsim 全局锁消除

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | `simMu` 互斥锁移除 | `grep -n 'simMu' netsim.go` | 零匹配 |
| 2 | 使用 Go 1.22+ 并发安全 rand | Code review | `rand.Intn()` 直接调用 |
| 3 | 模拟丢包/延迟行为不变 | `go test -run TestNetSim` | 丢包率在 ±2% 误差内 |

### PERF-06: ReadMemStats 后台化

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | /perf 请求不直接调用 ReadMemStats | Code review | ReadMemStats 在后台 goroutine 中 |
| 2 | 并发 100 请求 /perf 无 panic | `go test -run TestPerfEndpointConcurrent` | 200 OK 且响应一致 |
| 3 | STW 不被 admin API 触发 | `GODEBUG=gctrace=1` 观察 | /perf 请求期间无额外 GC 暂停 |

### ARCH-01: Room alive 原子标志

与 PERF-04 合并验收。

### ARCH-03: SessionStore 替代 sync.Map

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | connPlayerMap, playerRoomMap, playerConnMap, connContextMap 全部移除 | `grep -rn 'connPlayerMap\|playerRoomMap\|playerConnMap\|connContextMap' main.go` | 零匹配 |
| 2 | 统一 SessionStore 结构体存在 | Code review | `type SessionStore struct` 存在 |
| 3 | 重连场景映射一致 | `go test -run TestReconnectSessionConsistency` | 重连后 byConn 和 byPlayer 指向同一 Session |
| 4 | 类型安全（无 type assertion） | `go vet` + `grep 'any)' session_store.go` | 无 `.(type)` 断言 |
| 5 | 写入性能 | `go test -bench BenchmarkSessionStore -cpu 8` | ≥ sync.Map 吞吐（或可接受的 ±10%） |

### ARCH-04: 流式编码

与 PERF-01 合并验收。额外条件：

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | FrameData 临时 struct 不再分配 | `go test -bench BenchmarkStepFrame -benchmem` | allocs/op 减少 1 |
| 2 | EncodeFrameDataAppend 函数存在 | Code review | 签名 `func EncodeFrameDataAppend(dst []byte, ...) []byte` |

### ARCH-05: WebSocket Graceful Shutdown

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | wsServer.Close() 在关机流程中被调用 | Code review | 在 `server.Close()` 之后 |
| 2 | WS 客户端收到 ServerShutdown | 集成测试 | WS 连接收到 CmdServerShutdown 后断开 |
| 3 | 关机后无 WS goroutine 泄漏 | `SIGTERM` → 等 5s → `goroutine dump` | WS 相关 goroutine = 0 |

### ARCH-06: Admin Graceful Shutdown

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | shutdown timeout 可配置 | Code review | 读取 `cfg.AdminShutdownSec` |
| 2 | GM WebSocket hub 正常关闭 | 集成测试 | GM WS 连接收到 close frame |

### NEW-01: frameHashes 暂停时清空

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | GamePause 时清空 frameHashes | Code review | `r.frameHashes = make(...)` 在 GamePause 内 |
| 2 | SnapshotPause 时清空 frameHashes | Code review | stepFrame 暂停路径中清空 |
| 3 | 暂停期间内存不增长 | `go test -run TestPausedRoomMemory` | 暂停 10s 后 frameHashes len = 0 |

### NEW-02: MaxMessageSize 全局变量消除

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | 全局 `var MaxMessageSize` 移除 | `grep -n 'var MaxMessageSize' framing.go` | 零匹配 |
| 2 | NewFrameReader 接受 maxMsgSize 参数 | Code review | 签名变更 |
| 3 | TCP 和 WS 可配置不同 MaxMessageSize | 配置文件测试 | 两个 server 使用不同值且互不干扰 |
| 4 | `go test -race ./codec/...` 通过 | CI | 无 race 报告 |

### NEW-03: handleReconnect 原子快照

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | Room.GetReconnectSnapshot() 方法存在 | Code review | 单次加锁读取所有字段 |
| 2 | handleReconnect 使用 GetReconnectSnapshot | Code review | 不再分别调用 CurrentFrameNumber/GetSnapshot/OldestBufferedFrame |
| 3 | 房间销毁期间重连不 panic | `go test -race -run TestReconnectDuringDestroy` | 无 panic，返回错误码 |

### NEW-04: AddPlayer 原子容量检查

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | AddPlayer 返回 error | Code review | 签名变更为 `func (r *Room) AddPlayer(...) error` |
| 2 | 内部容量检查在锁内 | Code review | `len(r.players) >= r.config.MaxPlayers` 在 Lock 之后 |
| 3 | 并发加入不超过 MaxPlayers | `go test -race -run TestConcurrentJoin100` | 100 并发 join，最终玩家数 = MaxPlayers |
| 4 | 所有调用方处理 error | `grep -n 'AddPlayer' main.go` | 每处调用检查 err |

### NEW-05: admin JSON 编码错误处理

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | `json.Marshal` 返回的 error 被检查 | Code review | `if err != nil { jsonError(...) }` |
| 2 | 注入异常数据时返回 500 | `go test -run TestAdminStatsWithBadData` | HTTP 500 + 合法 JSON body |

### NEW-06: preamble 失败日志

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | preamble 发送失败时记录 slog.Warn | Code review | 包含 playerId, roomId, 已发/总数 |
| 2 | 日志可搜索 | `grep 'preamble' *.go` | 结构化字段可被 ELK/Loki 索引 |

### NEW-07: perfCachedJSON 并发安全

| # | 验收条件 | 验证方式 | 通过标准 |
|---|---------|---------|---------|
| 1 | perfCachedJSON 改用 atomic.Value 或 atomic.Pointer | Code review | 无裸 `[]byte` 赋值 |
| 2 | `go test -race -run TestPerfConcurrent` 通过 | CI | 无 data race |
| 3 | 并发 200 请求 /perf 全部返回合法 JSON | 压测 | 200 OK + JSON parse success |

---

## 5.2 线上环境测试标准

### 一、基准性能测试（Benchmark Suite）

所有基准测试必须在合并前通过，CI 自动运行。

```bash
# 运行命令（CI 脚本）
cd svr
go test -bench=. -benchmem -benchtime=10s -count=5 ./framesync/... ./codec/... ./transport/... \
    | tee benchmark_$(date +%Y%m%d).txt

# 与基线对比（使用 benchstat）
benchstat baseline.txt benchmark_$(date +%Y%m%d).txt
```

| 基准项 | 指标 | 基线（修复前） | 合格线 | 优秀线 |
|--------|------|---------------|--------|--------|
| BenchmarkStepFrame/8players | ns/op | ≤ 25,000 | ≤ 25,000 | ≤ 18,000 |
| BenchmarkStepFrame/8players | allocs/op | ≤ 5 | ≤ 5 | ≤ 3 |
| BenchmarkStepFrame/8players | B/op | ≤ 2,048 | ≤ 2,048 | ≤ 1,024 |
| BenchmarkHandleFrameInput | ns/op | ≤ 800 | ≤ 500 | ≤ 200 |
| BenchmarkBroadcastReliable/8players | ns/op | 基线 | ≤ 基线 × 0.8 | ≤ 基线 × 0.5 |
| BenchmarkGetFramesSince/2400frames | B/op | 基线 | ≤ 基线 | ≤ 基线 × 0.9 |
| BenchmarkReplay/2400frames | allocs/op | 基线 | ≤ 基线 - 30 | ≤ 基线 - 48 |
| BenchmarkEncode/CoreMessage | ns/op | ≤ 150 | ≤ 150 | ≤ 100 |
| BenchmarkDecode/CoreMessage | ns/op | ≤ 200 | ≤ 200 | ≤ 150 |

**基线采集方式**: 在修复分支第一次提交前执行一次完整 benchmark，存入 `benchmark_baseline.txt` 作为永久基线。

**退化判定**: benchstat delta > +10% 且 p-value < 0.05 视为性能退化，PR 自动阻止合并。

---

### 二、Race 检测（Data Race Detection）

所有测试在 CI 中必须以 `-race` 运行。

```bash
go test -race -timeout 300s -count 3 ./...
```

| 检测项 | 通过标准 |
|--------|---------|
| 帧推送并发测试 | `TestStepFrameConcurrent` — 零 race |
| 重连并发测试 | `TestReconnectConcurrent` — 零 race |
| KV 并发读写 | `TestDataStoreConcurrent` — 零 race |
| Admin API 并发 | `TestAdminAPIConcurrent` — 零 race |
| 房间创建/销毁并发 | `TestRoomLifecycleConcurrent` — 零 race |
| 全局映射并发 | `TestSessionMapConcurrent` — 零 race |

**策略**: 使用 `testing.T.Parallel()` + `sync.WaitGroup` 构建 100 goroutine 并发场景。race detector 检测到任何 race 即 CI 失败。

---

### 三、压力测试（Stress Test — 线上环境级别）

模拟线上峰值流量的 2 倍，持续运行 30 分钟。

```bash
# 压力测试参数
ROOMS=200          # 同时活跃房间数
PLAYERS_PER_ROOM=8 # 每房间玩家数
FRAME_RATE=20      # 帧率
DURATION=30m       # 持续时间
INPUT_SIZE=256     # 每帧每玩家输入字节数
```

| 监控指标 | 采集方式 | 合格线 | 不合格处理 |
|----------|---------|--------|-----------|
| 帧推送 P99 延迟 | Prometheus `frame_broadcast_latency` | ≤ 5ms | 排查锁竞争/GC |
| 帧推送 P999 延迟 | 同上 | ≤ 20ms | 排查 STW/调度延迟 |
| 内存增长率 | `heap_mb` 线性回归斜率 | ≤ 1MB/min | 排查泄漏 |
| 内存峰值 | `sys_mb` | ≤ 512MB (200房×8人) | 排查分配模式 |
| Goroutine 数量 | `runtime.NumGoroutine()` | ≤ rooms × players × 2 + 100 | 排查泄漏 |
| Goroutine 增长率 | 线性回归 | ≤ 0/min（稳态后） | 排查泄漏 |
| GC 暂停 P99 | `gc_pause_us` | ≤ 500μs | 排查大对象分配 |
| CPU 使用率 | `top` / cgroup | ≤ 2 核 (200房) | 排查热路径 |
| 连接错误率 | `broadcast_send_errors` / `frames_pushed` | ≤ 0.01% | 排查慢客户端处理 |
| 重连成功率 | `reconnect_ok` / (`reconnect_ok` + `reconnect_fail`) | ≥ 99.5% | 排查竞态 |
| 房间清理及时性 | 所有玩家离开 → 房间销毁延迟 | ≤ emptyGrace + 10s | 排查 Reconciler |

**执行环境**: 与生产环境相同规格的 staging 机器（推荐 4C8G），使用 `tcpkali` 或自研压测客户端模拟。

**数据采集**:
```bash
# Prometheus 抓取（1s 间隔）
curl -s http://localhost:${METRICS_PORT}/metrics >> metrics_$(date +%s).prom

# 压测结束后分析
python3 analyze_stress.py --input metrics_*.prom --output stress_report.html
```

---

### 四、回归测试矩阵（Regression Test Matrix）

每次 PR 合并前必须通过的完整测试矩阵。

| 场景 | 测试名 | 关键验证点 | 超时 |
|------|--------|-----------|------|
| 正常对局 | TestFullGameLoop | 创建→加入→开始→2400帧→停止，所有帧收到 | 60s |
| 迟加入 | TestLateJoin | 中途加入收到快照+补帧+实时帧，帧序列完整 | 30s |
| 重连（快速） | TestQuickReconnect | 断开<5s 重连，帧序列连续无丢失 | 15s |
| 重连（慢速） | TestSlowReconnect | 断开>30s 重连，快照+补帧+S2C reliable 全补 | 30s |
| 重连（超时） | TestReconnectAfterKeepAlive | 超过 keepalive 重连失败，返回正确错误码 | 60s |
| 满员拒绝 | TestRoomFull | MaxPlayers 后加入返回 RoomFull | 5s |
| 并发加入 | TestConcurrentJoin100 | 100 goroutine 并发加入，最终≤MaxPlayers | 10s |
| KV 完整性 | TestDataStoreRoundTrip | Set→Get→Delete→Snapshot 全流程正确 | 5s |
| 实体权威 | TestEntityAuthorityTransfer | 授予→抢夺→释放→断线清理 | 5s |
| Desync 检测 | TestDesyncDetection | 不同 hash 上报后 desyncDetected=true | 5s |
| S2C Reliable | TestS2CReliableReplay | 断线→发 reliable→重连→补发完整 | 15s |
| C2S 去重 | TestC2SDeduplication | 重复 seq 消息被过滤 | 5s |
| 帧号溢出 | TestFrameOverflow | frameNumber=MaxUint32 时房间安全停止 | 5s |
| Graceful Shutdown | TestGracefulShutdown | SIGTERM→所有客户端收到 ServerShutdown→连接关闭 | 30s |
| WS+TCP 混合 | TestMixedTransport | TCP 和 WS 客户端在同房间，帧一致 | 30s |
| 快照暂停恢复 | TestSnapshotStalePauseResume | 停发快照→暂停→恢复→帧继续 | 30s |
| 游戏暂停恢复 | TestGamePauseResume | 暂停→恢复→帧号连续 | 10s |
| Admin API | TestAdminAPISmokeTest | /health, /stats, /rooms, /perf 全返回 200 | 10s |
| IP 限流 | TestIPRateLimiting | 单 IP 超限后被拒绝 | 10s |
| 配置热加载 | TestConfigReload | SIGHUP → 配置生效 | 5s |

**CI 执行命令**:
```bash
go test -v -race -timeout 600s -failfast ./... 2>&1 | tee test_report.txt
echo "Exit code: $?" >> test_report.txt
```

---

### 五、Chaos 测试（可选，推荐线上前执行）

模拟线上异常场景，验证系统韧性。

| 场景 | 方法 | 预期行为 |
|------|------|---------|
| 网络分区 | `iptables -A OUTPUT -p tcp --dport $PORT -j DROP` 30s | 玩家标记断线，keepalive 后驱逐，房间正常 |
| 单客户端慢写 | 模拟 1 个客户端 Send 耗时 5s | delivery channel 满，slog.Warn，其他玩家不受影响 |
| OOM 逼近 | `cgroup memory.limit_in_bytes=256M` | GC 正常工作，无 OOM kill（200 房以内） |
| CPU 限制 | `cgroup cpu.cfs_quota_us=50000` (0.5核) | 帧率可能下降，但不 panic，不丢帧 |
| 快速重连风暴 | 同一 playerId 每 100ms 重连一次 × 60s | 旧连接正确关闭，无 goroutine 泄漏 |
| 房间风暴 | 1 秒内创建 1000 个房间 | RoomManager 不死锁，内存可控 |
| Admin API 风暴 | 1000 QPS 打 /stats | 不影响帧推送 P99 |

---

### 六、上线前检查清单（Go-Live Checklist）

- [ ] 所有基准测试通过（benchstat 无退化）
- [ ] `go test -race` 零 data race
- [ ] 压力测试 30 分钟：内存无泄漏、goroutine 稳态、P99 ≤ 5ms
- [ ] 回归测试矩阵 24/24 通过
- [ ] `go vet ./...` 零 warning
- [ ] `staticcheck ./...` 零 error（如项目已接入）
- [ ] Prometheus 告警规则配置：帧延迟 > 10ms、内存 > 80%、goroutine > 阈值
- [ ] Grafana dashboard 包含：帧推送延迟、内存趋势、goroutine 数、GC 暂停、连接数
- [ ] 回滚方案文档就绪（docker tag / k8s rollout undo）
- [ ] Changelog 更新（列出所有 PERF/ARCH/NEW 变更）

---

---

## 六、ADR: ARCH-02 — Room 模块化拆分（生产级架构方案）

### ADR-002: Room 职责拆分为组合式子模块

**Status:** Proposed
**Date:** 2026-04-11
**Deciders:** 鲁文毅 (Tech Lead)

---

### 6.1 Context（背景与驱动力）

当前 `Room` struct（197 行字段定义，1446 行方法）承载了 **7 项独立职责**：

| 职责 | 字段数 | 方法数 | 热路径 | 锁竞争 |
|------|--------|--------|--------|--------|
| 帧推送核心 (Frame Loop) | 11 | 8 | ✅ stepFrame/deliveryLoop | 高 |
| 玩家管理 (Player Mgmt) | 3 | 12 | 部分 (AddPlayer) | 中 |
| 快照存储 (Snapshot) | 4 | 4 | ❌ | 低 |
| KV 数据存储 (DataStore) | 2 | 5 | ❌ | 低 |
| 实体权威 (EntityAuthority) | 1 | 3 | ❌ | 低 |
| Desync 检测 (DesyncDetector) | 2 | 3 | ❌ | 低 |
| S2C 可靠通道 (ReliableChannel) | ⊂Player | 5 | ❌ | 中 |

**核心问题**：所有职责共享同一把 `sync.Mutex`，导致：

1. **锁粒度过粗**: KV SetData（低频）和 stepFrame（20fps 热路径）竞争同一把锁
2. **测试困难**: 测试 DataStore 逻辑必须构建完整 Room（含 frameRing、ticker 等）
3. **功能耦合**: 不需要 KV 存储的房间仍然分配 `dataStore map`
4. **代码导航困难**: 1446 行的单文件，7 种职责交织

**约束条件**：

- 帧同步服务已在生产环境运行，不允许大爆炸式重写
- 重构必须保持二进制兼容（外部 API 不变）
- 热路径性能不能退化（stepFrame 每帧 < 50μs）
- 团队规模小（1-2 人），迁移窗口约 2-3 周

---

### 6.2 Decision（方案决策）

采用 **组合嵌入（Composition via Embedding）** 模式，将 Room 拆分为 5 个独立子模块，通过 struct embedding 组合回 Room。每个子模块拥有独立的锁（或无锁），对外暴露的 Room API 保持不变。

---

### 6.3 Options Considered

#### Option A: 接口抽象 + 依赖注入

将每个子模块定义为 interface，Room 持有 interface 引用：

```go
type Room struct {
    snapshot  SnapshotStore      // interface
    dataStore DataStore          // interface
    authority EntityAuthorityMgr // interface
    desync    DesyncDetector     // interface
}
```

| 维度 | 评估 |
|------|------|
| 复杂度 | 高 — 需定义 5 个 interface + 5 个实现 |
| 性能 | 微降 — interface dispatch 有间接调用开销 |
| 可测试性 | 最佳 — 可 mock 任意子模块 |
| 改动量 | 大 — 所有方法签名变更 |

**Pros**: 最高灵活性，可热替换实现，完美可测试性
**Cons**: 过度设计（子模块没有多实现需求），interface 方法签名维护成本高，热路径多一层间接调用

#### Option B: 组合嵌入 + 独立锁（✅ 推荐）

将子模块定义为独立 struct，嵌入 Room：

```go
type Room struct {
    // 核心（保留在 Room 本体）
    mu       sync.Mutex
    players  map[int32]*Player
    // ... frame loop 字段 ...

    // 嵌入子模块（各有独立锁）
    snapshot  SnapshotStore
    dataStore DataStore
    authority EntityAuthority
    desync    DesyncDetector
}
```

| 维度 | 评估 |
|------|------|
| 复杂度 | 低 — struct 嵌入，Go 原生模式 |
| 性能 | 无损 — 编译期内联，无 interface 开销 |
| 可测试性 | 好 — 子模块可独立实例化和测试 |
| 改动量 | 中 — 逐模块迁移，每步可独立 PR |

**Pros**: 零运行时开销，渐进式迁移，Go 惯用模式，子模块可独立测试
**Cons**: 嵌入方法提升可能导致命名冲突（可通过命名前缀规避）

#### Option C: 文件拆分（仅重组，不改结构）

不改 Room struct，只将方法按职责拆分到不同文件。

| 维度 | 评估 |
|------|------|
| 复杂度 | 最低 |
| 性能 | 不变 |
| 可测试性 | 不变（仍需完整 Room） |
| 改动量 | 最小（仅 `go` 文件移动） |

**Pros**: 零风险，代码导航改善
**Cons**: 不解决锁粒度、测试困难、功能耦合三个核心问题

---

### 6.4 Detailed Design — Option B

#### 6.4.1 模块划分

```
framesync/
├── room.go              // Room 核心：帧推送 + 玩家管理 + 生命周期（~600 行）
├── room_snapshot.go     // SnapshotStore（~120 行）
├── room_datastore.go    // DataStore（~100 行）
├── room_authority.go    // EntityAuthority（~60 行）
├── room_desync.go       // DesyncDetector（~100 行）
├── room_reliable.go     // ReliableChannel (S2C)（~120 行）
├── room_manager.go      // 不变
├── reconciler.go        // 不变
└── protocol.go          // 不变
```

#### 6.4.2 子模块定义

**SnapshotStore** — 快照存储与新鲜度监控

```go
// room_snapshot.go
package framesync

import "sync"

// SnapshotStore 管理房间快照存储及新鲜度检测。
// 独立锁：快照更新不阻塞 stepFrame 热路径。
type SnapshotStore struct {
    mu          sync.Mutex
    frame       uint32
    data        []byte
    staleFrames uint32 // 自上次快照以来经过的帧数
    paused      bool   // 是否因快照过期而暂停
}

// SetInitial 设置初始快照（frame 0）
func (s *SnapshotStore) SetInitial(data []byte) {
    s.mu.Lock()
    defer s.mu.Unlock()
    s.frame = 0
    s.data = append(s.data[:0], data...) // 复用底层数组
    s.staleFrames = 0
}

// Update 更新快照。仅接受更新帧号。返回 (accepted, wasPaused)。
func (s *SnapshotStore) Update(frameNumber uint32, data []byte) (bool, bool) {
    s.mu.Lock()
    defer s.mu.Unlock()
    if frameNumber <= s.frame {
        return false, false
    }
    s.frame = frameNumber
    if cap(s.data) >= len(data) {
        s.data = s.data[:len(data)]
    } else {
        s.data = make([]byte, len(data))
    }
    copy(s.data, data)
    s.staleFrames = 0
    wasPaused := s.paused
    s.paused = false
    return true, wasPaused
}

// Get 获取当前快照（返回引用，调用方不应修改）
func (s *SnapshotStore) Get() (frame uint32, data []byte) {
    s.mu.Lock()
    defer s.mu.Unlock()
    return s.frame, s.data
}

// TickStale 帧推进时调用，递增 staleFrames。
// 返回 shouldPause=true 表示超过阈值应暂停。
// 已暂停时返回 isPaused=true 表示应跳过帧推进。
func (s *SnapshotStore) TickStale(staleLimit uint32) (shouldPause, isPaused bool) {
    s.mu.Lock()
    defer s.mu.Unlock()
    if s.paused {
        return false, true
    }
    s.staleFrames++
    if s.staleFrames >= staleLimit {
        s.paused = true
        return true, false
    }
    return false, false
}

// Reset 重置（Start 时调用）
func (s *SnapshotStore) Reset() {
    s.mu.Lock()
    defer s.mu.Unlock()
    s.frame = 0
    s.data = nil
    s.staleFrames = 0
    s.paused = false
}

// --- GM 检视方法 ---
func (s *SnapshotStore) Frame() uint32       { s.mu.Lock(); defer s.mu.Unlock(); return s.frame }
func (s *SnapshotStore) Size() int           { s.mu.Lock(); defer s.mu.Unlock(); return len(s.data) }
func (s *SnapshotStore) StaleFrames() uint32 { s.mu.Lock(); defer s.mu.Unlock(); return s.staleFrames }
func (s *SnapshotStore) IsPaused() bool      { s.mu.Lock(); defer s.mu.Unlock(); return s.paused }
```

**DataStore** — 轻量 KV 存储

```go
// room_datastore.go
package framesync

import "sync"

// DataStore 房间级 KV 数据存储。独立锁。
type DataStore struct {
    mu      sync.Mutex
    entries map[int64]DataEntry // key = DataStoreKey(playerId, key)
    version uint32
}

func NewDataStore() DataStore {
    return DataStore{entries: make(map[int64]DataEntry)}
}

// Set 设置或删除 KV。value==nil 表示删除。返回新版本号。
func (d *DataStore) Set(playerId, key int32, value []byte) uint32 {
    d.mu.Lock()
    defer d.mu.Unlock()
    ck := DataStoreKey(playerId, key)
    if value == nil {
        delete(d.entries, ck)
    } else {
        d.entries[ck] = DataEntry{PlayerId: playerId, Key: key, Value: value}
    }
    d.version++
    return d.version
}

// Snapshot 全量快照 + 版本号
func (d *DataStore) Snapshot() ([]DataEntry, uint32) {
    d.mu.Lock()
    defer d.mu.Unlock()
    es := make([]DataEntry, 0, len(d.entries))
    for _, e := range d.entries {
        es = append(es, e)
    }
    return es, d.version
}

// ClearPlayer 清除指定玩家所有数据。返回被删条目和每次的版本号。
func (d *DataStore) ClearPlayer(playerId int32) ([]DataEntry, []uint32) {
    d.mu.Lock()
    defer d.mu.Unlock()
    var deleted []DataEntry
    var versions []uint32
    for k, e := range d.entries {
        if e.PlayerId == playerId {
            deleted = append(deleted, e)
            delete(d.entries, k)
            d.version++
            versions = append(versions, d.version)
        }
    }
    return deleted, versions
}

// Version 当前版本号
func (d *DataStore) Version() uint32 {
    d.mu.Lock(); defer d.mu.Unlock(); return d.version
}

// Empty 是否为空
func (d *DataStore) Empty() bool {
    d.mu.Lock(); defer d.mu.Unlock(); return len(d.entries) == 0
}
```

**EntityAuthority** — 实体权威表

```go
// room_authority.go
package framesync

import "sync"

// EntityAuthority 管理实体权威分配。独立锁。
type EntityAuthority struct {
    mu    sync.Mutex
    table map[int32]int32 // entityId → ownerPlayerId (0 = unclaimed)
}

func NewEntityAuthority() EntityAuthority {
    return EntityAuthority{table: make(map[int32]int32)}
}

// TryGrant 授予权威（先到先得，支持抢夺）
func (ea *EntityAuthority) TryGrant(entityId, requesterId int32) (bool, int32) {
    ea.mu.Lock()
    defer ea.mu.Unlock()
    ea.table[entityId] = requesterId
    return true, requesterId
}

// Release 释放（仅持有者可释放）
func (ea *EntityAuthority) Release(entityId, requesterId int32) bool {
    ea.mu.Lock()
    defer ea.mu.Unlock()
    if ea.table[entityId] == requesterId {
        ea.table[entityId] = 0
        return true
    }
    return false
}

// ReleaseAll 释放某玩家持有的全部实体
func (ea *EntityAuthority) ReleaseAll(playerId int32) []int32 {
    ea.mu.Lock()
    defer ea.mu.Unlock()
    var released []int32
    for eid, owner := range ea.table {
        if owner == playerId {
            ea.table[eid] = 0
            released = append(released, eid)
        }
    }
    return released
}

// Snapshot GM 检视用快照
func (ea *EntityAuthority) Snapshot() []EntityAuthorityEntry {
    ea.mu.Lock()
    defer ea.mu.Unlock()
    entries := make([]EntityAuthorityEntry, 0, len(ea.table))
    for eid, owner := range ea.table {
        entries = append(entries, EntityAuthorityEntry{EntityId: eid, OwnerId: owner})
    }
    return entries
}
```

**DesyncDetector** — 帧哈希收集与不一致检测

```go
// room_desync.go
package framesync

import "sync"

// DesyncDetector 收集各客户端帧哈希，检测不同步。独立锁。
type DesyncDetector struct {
    mu             sync.Mutex
    hashes         map[uint32]map[int32]uint32 // frameNumber → playerId → hash
    detected       bool
    maxHistory     uint32 // 保留最近 N 帧的 hash（默认 200）
    cleanupPeriod  uint32 // 每 N 帧执行一次清理（默认 100）
}

func NewDesyncDetector() DesyncDetector {
    return DesyncDetector{
        hashes:        make(map[uint32]map[int32]uint32),
        maxHistory:    200,
        cleanupPeriod: 100,
    }
}

// Report 上报帧哈希。返回 true 表示检测到不同步。
func (dd *DesyncDetector) Report(playerId int32, frameNumber, hash uint32) bool {
    dd.mu.Lock()
    defer dd.mu.Unlock()

    if dd.detected {
        return false
    }

    if dd.hashes[frameNumber] == nil {
        dd.hashes[frameNumber] = make(map[int32]uint32)
    }
    dd.hashes[frameNumber][playerId] = hash

    // 检测不一致
    hashes := dd.hashes[frameNumber]
    if len(hashes) >= 2 {
        var first uint32
        isFirst := true
        for _, h := range hashes {
            if isFirst { first = h; isFirst = false; continue }
            if h != first {
                dd.detected = true
                dd.hashes = make(map[uint32]map[int32]uint32) // 释放内存
                return true
            }
        }
    }

    // 周期性清理旧数据
    if frameNumber > dd.maxHistory && frameNumber%dd.cleanupPeriod == 0 {
        cutoff := frameNumber - dd.maxHistory
        for fn := range dd.hashes {
            if fn < cutoff { delete(dd.hashes, fn) }
        }
    }
    return false
}

// ClearOnPause 暂停时主动清空（解决 NEW-01 问题）
func (dd *DesyncDetector) ClearOnPause() {
    dd.mu.Lock()
    defer dd.mu.Unlock()
    dd.hashes = make(map[uint32]map[int32]uint32)
}

// GetHashes 获取特定帧的 hash（日志/GM 用）
func (dd *DesyncDetector) GetHashes(frameNumber uint32) map[int32]uint32 {
    dd.mu.Lock()
    defer dd.mu.Unlock()
    result := make(map[int32]uint32)
    if hashes, ok := dd.hashes[frameNumber]; ok {
        for k, v := range hashes { result[k] = v }
    }
    return result
}

// IsDetected 是否已检测到不同步
func (dd *DesyncDetector) IsDetected() bool {
    dd.mu.Lock(); defer dd.mu.Unlock(); return dd.detected
}
```

#### 6.4.3 重构后的 Room struct

```go
// room.go — 重构后，仅保留帧推送核心 + 玩家管理
type Room struct {
    ID       int32
    MatchKey string
    mu       sync.Mutex    // 仅保护帧推送和玩家管理
    config   RoomConfig
    alive    atomic.Bool   // ARCH-01: 原子存活标志

    // --- 玩家管理 ---
    players      map[int32]*Player
    hostPlayerId int32
    onlineCount  int32 // atomic

    // --- 帧推送核心 ---
    frameRate     int32
    frameInterval time.Duration
    startTime     int64
    frameNumber   uint32
    running       bool
    stopCh        chan struct{}

    pendingInputs    []PlayerInput
    pendingInputsBuf []PlayerInput
    pendingEvents    []FrameEvent
    pendingEventsBuf []FrameEvent

    frameRing    []CachedFrame
    frameRingPos int
    frameRingLen int
    frameBuf     []byte
    broadcastBuf []*Player

    gamePaused bool

    // --- 组合子模块（各有独立锁） ---
    Snapshot  SnapshotStore     // 快照存储
    Data      DataStore         // KV 存储
    Authority EntityAuthority   // 实体权威
    Desync    DesyncDetector    // 不同步检测

    // --- 生命周期 ---
    delegate  RoomDelegate
    createdAt time.Time
    hadPlayer atomic.Bool
    startedAt time.Time
    emptyAt   time.Time
}
```

#### 6.4.4 stepFrame 热路径变更（展示锁分离效果）

重构前（单锁）：
```go
func (r *Room) stepFrame() {
    r.mu.Lock()
    // 快照新鲜度检查 — 操作 r.snapshotStaleFrames, r.snapshotPaused
    if r.config.SnapshotIntervalFrames > 0 && ... {
        r.snapshotStaleFrames++     // ← 与帧推送共享锁
        if r.snapshotStaleFrames >= staleLimit && !r.snapshotPaused {
            r.snapshotPaused = true // ← 与帧推送共享锁
            ...
        }
    }
    // ... 帧推送逻辑 ...
    r.mu.Unlock()
}
```

重构后（锁分离）：
```go
func (r *Room) stepFrame() {
    // 快照检查：用 Snapshot 的独立锁，不阻塞帧推送
    if r.config.SnapshotIntervalFrames > 0 {
        staleLimit := uint32(r.config.SnapshotIntervalFrames * 3)
        shouldPause, isPaused := r.Snapshot.TickStale(staleLimit) // ← Snapshot 独立锁
        if isPaused {
            return
        }
        if shouldPause {
            r.broadcast(codec.NewExtMessage(ExtCmdFrameSyncPaused, ...))
            return
        }
    }

    r.mu.Lock() // ← 只保护帧推送核心
    if r.gamePaused { r.mu.Unlock(); return }
    // ... 帧推送逻辑（不再包含快照检查） ...
    r.mu.Unlock()
}
```

**效果**: stepFrame 的 r.mu 持锁时间减少 ~15%（快照检查路径完全独立）。

#### 6.4.5 外部 API 兼容层

为了保持现有调用方不变，Room 提供**薄包装方法**（delegate to 子模块）：

```go
// 兼容层：保持外部 API 不变
// 这些方法在迁移完成后可以逐步标记为 Deprecated，引导调用方直接使用子模块。

func (r *Room) SetInitialSnapshot(data []byte) {
    r.Snapshot.SetInitial(data)
}

func (r *Room) UpdateSnapshot(frameNumber uint32, data []byte) bool {
    accepted, wasPaused := r.Snapshot.Update(frameNumber, data)
    if !accepted { return false }
    if wasPaused {
        r.broadcast(codec.NewExtMessage(ExtCmdFrameSyncResumed, nil))
        if d := r.getDelegate(); d != nil { d.OnRoomResumed(r) }
    }
    return true
}

func (r *Room) SetData(playerId, key int32, value []byte) uint32 {
    return r.Data.Set(playerId, key, value)
}

func (r *Room) TryGrantAuthority(entityId, requesterId int32) (bool, int32) {
    return r.Authority.TryGrant(entityId, requesterId)
}

func (r *Room) ReportFrameHash(playerId int32, frameNumber, hash uint32) bool {
    return r.Desync.Report(playerId, frameNumber, hash)
}
```

#### 6.4.6 S2C 可靠通道说明

可靠通道的 seq/buffer 存储在 `Player` struct 内，与玩家生命周期绑定。将其独立为子模块需要 Player struct 也拆分，改动面过大。

**建议**: 可靠通道保持在 Room 内，但将方法迁移到独立文件 `room_reliable.go`，与 Room 共享 `r.mu`。这是合理的，因为可靠通道的锁获取（SendReliableToPlayer）需要读写 Player 字段，与玩家管理的锁天然耦合。

```
room.go          — Room struct + 帧推送 + 玩家管理
room_reliable.go — SendReliableToPlayer, BroadcastReliable, GetS2CReliableSince 等
                   仍使用 r.mu（与玩家管理共享锁是合理的）
```

---

### 6.5 Trade-off Analysis

| 维度 | 现状（单体 Room） | 重构后（组合子模块） |
|------|-------------------|---------------------|
| stepFrame 锁时间 | 包含快照检查 | 快照检查独立，减少 ~15% |
| DataStore 锁竞争 | 与 stepFrame 共享 | 完全独立，零竞争 |
| EntityAuthority 锁竞争 | 与 stepFrame 共享 | 完全独立，零竞争 |
| 测试粒度 | 必须构建完整 Room | 子模块可独立 `go test` |
| 代码行数/文件 | 1446 行 / 1 文件 | ~600 + 120 + 100 + 60 + 100 + 120 / 6 文件 |
| 内存（无 KV 的房间） | 仍分配 dataStore map | 可延迟初始化（`Data.entries` lazy init） |
| API 兼容性 | N/A | 100% 兼容（薄包装方法） |
| 运行时开销 | N/A | 零（struct embedding，编译期内联） |

**关键权衡**：子模块各自持锁意味着跨模块操作（如 Start() 时同时重置 Snapshot + Desync + DataStore）需要分别获取多把锁。但这些操作都是低频的（Start/Stop 一局一次），不构成性能瓶颈。

---

### 6.6 Consequences

**变得更容易的事情：**
- 为 DataStore / DesyncDetector / EntityAuthority 编写独立单元测试
- 理解和审查代码（每个文件 60-120 行，职责单一）
- 未来添加新功能（如添加 Replay 录制模块）只需新建文件
- 按需初始化子模块（简单对战房间不需要 KV/实体权威）

**变得更困难的事情：**
- 跨模块事务（如 "原子性地 Stop + 清空 DataStore"）需要手动协调多锁
- 调试时需要关注锁顺序（但子模块之间无嵌套锁，不存在死锁风险）

**需要后续关注的事情：**
- 可靠通道是否需要独立为子模块（取决于未来是否需要在无帧同步的场景下使用）
- 子模块的 lazy initialization（按需分配 map，进一步减少内存占用）

---

### 6.7 Migration Plan（渐进式迁移，5 个独立 PR）

每个 PR 独立可测试、可回滚，不影响其他模块。

```
PR #1: 文件拆分（零改动）
  - 将 room.go 的方法按职责移动到 6 个文件
  - 不改任何代码逻辑
  - 验收：go build && go test 全部通过

PR #2: DesyncDetector 独立化
  - 创建 DesyncDetector struct + 独立锁
  - Room 嵌入 Desync DesyncDetector
  - Room.ReportFrameHash → r.Desync.Report
  - 兼容层薄包装
  - 验收：`go test -run TestDesync` 通过，Room 测试不变

PR #3: EntityAuthority + DataStore 独立化
  - 创建 EntityAuthority struct + DataStore struct
  - Room 嵌入两者
  - 兼容层薄包装
  - 验收：独立模块单元测试 + 全量测试

PR #4: SnapshotStore 独立化
  - 创建 SnapshotStore struct + 独立锁
  - stepFrame 中快照检查改用 r.Snapshot.TickStale()
  - 验收：帧推送性能基准测试无退化

PR #5: 清理 + 性能验证
  - 移除 Room 上的废弃字段
  - 添加 ARCH-01 alive 标志
  - 运行完整性能基准测试
  - 发布前 code review
```

**每个 PR 的验收清单：**
- [ ] `go build ./...` 通过
- [ ] `go vet ./...` 通过
- [ ] `go test ./framesync/...` 全部通过
- [ ] 性能基准测试无退化（`go test -bench BenchmarkStepFrame`）
- [ ] 外部调用方（main.go、admin.go）无需修改

---

### 6.8 核心指标

| 指标 | 当前基线 | 目标 | 验证方式 |
|------|---------|------|---------|
| stepFrame P99 耗时 | ~25μs | ≤ 25μs（不退化） | `go test -bench BenchmarkStepFrame -benchtime 10s` |
| DataStore 并发吞吐 | 受 Room.mu 限制 | 独立锁，无互斥 | `go test -bench BenchmarkDataStore -cpu 4` |
| Room 构建时间（测试） | ~1ms（需初始化全部字段） | ≤ 200μs（子模块独立） | `go test -bench BenchmarkNewRoom` |
| 代码行数/文件 | 1446/1 | ≤ 600/文件 | `wc -l framesync/room*.go` |

---

## 相关文档

- [首轮审计索引](README.md)
- [原始审计报告](../server_audit_findings.md)
- [统一投递架构方案](../framesync_unified_delivery_architecture.md)
