# BoomNetwork 性能优化追踪报告

> 测试环境：Apple M silicon, macOS, arm64 / Go 1.24 / `-benchtime=2s`
> 本文件只记录**服务器微优化（P0/P1 迭代）的 before/after 数据**。
> 宏观压测数据（3000/4000 人带宽、KCP/TCP 对比）见 [`doc/benchmark-report.md`](doc/benchmark-report.md)。
> 整体性能概览（TL;DR）见 [`doc/performance.md`](doc/performance.md)。

---

## 优化时间线

| 日期 | 批次 | Commit | 内容摘要 |
|------|------|--------|---------|
| 2026-04-03 | P0 | `1737229` | 4 项热路径零分配优化 |
| 2026-04-04 | P1 | `441eb59` | 6 项内存/Pool/Buffer 优化 |

---

## P0 优化（2026-04-03 · commit `1737229`）

> 测试文件：`svr/cmd/framesync/perf_test.go`、`svr/framesync/room_p0_test.go`

### P0-1 · `nextPlayerId()` Mutex → `atomic.AddInt32`

**文件：** `svr/cmd/framesync/main.go`

**改动：**
```go
// Before
var playerMu sync.Mutex
func nextPlayerId() int32 {
    playerMu.Lock()
    playerCounter++
    id := playerCounter
    playerMu.Unlock()
    return id
}

// After
func nextPlayerId() int32 {
    return atomic.AddInt32(&playerCounter, 1)
}
```

| Benchmark | ns/op | B/op | allocs/op |
|-----------|-------|------|-----------|
| `BenchmarkNextPlayerId` (串行) | **1.75** | 0 | 0 |
| `BenchmarkNextPlayerId_Parallel` (并发) | **44.9** | 0 | 0 |

**单元测试：** `TestNextPlayerId_Concurrent_NoDuplicates`（1000 goroutine 并发，排序验证无重复无间隔）

---

### P0-2 · `handleFrameInput` 双 `sync.Map` 查找 → 单次查找

**文件：** `svr/cmd/framesync/main.go`

**改动：** 新增 `connContext{playerId, *Room}` + `connContextMap sync.Map`，连接绑定时一次性存入，`handleFrameInput` 从 2 次 Map 查找降至 1 次。

```go
// Before: 2 次 sync.Map.Load
playerId := connPlayerMap.Load(conn.ID)   // 查找 1
room := playerRoomMap.Load(playerId)       // 查找 2

// After: 1 次 sync.Map.Load
val, ok := connContextMap.Load(conn.ID)   // 唯一查找
ctx := val.(*connContext)
ctx.room.OnInput(ctx.playerId, msg.Data)
```

| Benchmark | ns/op | B/op | allocs/op |
|-----------|-------|------|-----------|
| `BenchmarkHandleFrameInput` (串行) | **54.6** | 168 | 0 |
| `BenchmarkHandleFrameInput_Parallel` (并发，每 goroutine 独立 room) | **49.9** | 187 | 0 |

**单元测试：** 绑定/读回、断线清理、重连覆盖、miss/hit 路径、`InputsReceived` 计数器精确 +1

---

### P0-3 · `pendingInputs/Events = nil` → 双缓冲 swap

**文件：** `svr/framesync/room.go`

**改动：**
```go
// Before
r.pendingInputs = nil   // 每帧释放底层数组，下帧重新分配

// After（swap，复用底层数组）
inputs := r.pendingInputs
r.pendingInputs = r.pendingInputsBuf[:0]
r.pendingInputsBuf = inputs
```
`NewRoomWithConfig` 两侧均预分配（双端各 `make([]T, 0, N)`），确保 swap 后 cap 永不为 0。

| Benchmark | ns/op | B/op | allocs/op |
|-----------|-------|------|-----------|
| `BenchmarkStepFrame_AllocsPerOp`（稳态，含输入） | **90.9** | 48 | 1 |
| `BenchmarkStepFrame_NoInput`（稳态，空帧） | **87.5** | 48 | 1 |

> 仅剩的 1 alloc 来自 `FrameData` 结构体逃逸分析，双缓冲 swap 本身零分配。

**Bug（测试发现）：** 原实现仅预分配 `pendingInputsBuf`/`pendingEventsBuf` 一侧，首帧 swap 后另一侧 cap=0 再次触发分配。`TestPendingBuf_CapNeverDropsToZero` 暴露并修复（两侧均 `make`）。

**单元测试：** 双缓冲 swap cap 验证（50 帧持续检查）、data 内容正确性

---

### P0-4 · `PlayerCount()` O(n) 遍历 → `onlineCount int32` 原子计数

**文件：** `svr/framesync/room.go`

**改动：** 新增 `onlineCount int32` 字段，`AddPlayer`/`DisconnectPlayer`/`removePlayerLocked` 中原子 ±1，`PlayerCount()` 改为 `atomic.LoadInt32`。

| Benchmark | ns/op | B/op | allocs/op |
|-----------|-------|------|-----------|
| `BenchmarkPlayerCount`（串行） | **0.28** | 0 | 0 |
| `BenchmarkPlayerCount_Parallel`（并发） | **0.11** | 0 | 0 |

**单元测试：** AddPlayer/DisconnectPlayer/RemovePlayer 全组合（含断线后移除不双减）、64 玩家并发 Add/Disconnect

---

## P1 优化（2026-04-04 · commit `441eb59`）

> 测试文件：`svr/codec/pool_test.go`、`svr/framesync/room_p1_test.go`

### P1-1 · `Decode()` 每次分配新 `*Message` → `msgPool` 复用

**文件：** `svr/codec/message.go`

**改动：** 提取 `decodeInto()` 共享解码逻辑；新增 `DecodePooled()`、`GetMessage()`、`PutMessage()`。`PutMessage` 执行 `*m = Message{}` 清零防 Data 引用泄漏。

```go
// Before
msg := &Message{}   // 每次解码分配新对象

// After（Pool 复用路径）
msg, _ := DecodePooled(buf)
defer PutMessage(msg)
```

| Benchmark | ns/op | B/op | allocs/op |
|-----------|-------|------|-----------|
| `BenchmarkDecode_Allocating`（基线，每次 new） | 15.2 | 48 | **1** |
| `BenchmarkDecodePooled_Reuse`（Pool 稳态） | **8.7** | 0 | **0** |
| `BenchmarkDecodePooled_Parallel`（并发稳态） | **1.98** | 0 | **0** |

**单元测试：** Core/Extended/Game 三路结果一致性、PutMessage 全字段清零、错误路径归还不泄漏、32 goroutine 并发无竞争

---

### P1-2 · `broadcast()` 每次 `make([]*Player, ...)` → 复用 `broadcastBuf`

**文件：** `svr/framesync/room.go`

**改动：** Room struct 新增 `broadcastBuf []*Player`（预分配 cap=16），取代 `make`；与热路径 `broadcastSlice` 独立，互不干扰。

```go
// Before
players := make([]*Player, 0, len(r.players))

// After
r.broadcastBuf = r.broadcastBuf[:0]
// append 到 r.broadcastBuf，cap 不缩小
```

> `broadcast()` 用于非热路径（Start/Stop/Pause/Resume），调用方通过 `running` 状态机天然串行化，无需额外锁。

---

### P1-3 · `ForEachOnlinePlayer()` 匿名 struct slice → `playerSlicePool` 复用

**文件：** `svr/framesync/room.go`

**改动：** 原来每次分配 `[]struct{ id int32; conn PlayerConn }`；改为 `playerSlicePool sync.Pool` 复用 `[]*Player`，归还前逐元素置 nil 防 GC 泄漏。

| Benchmark | ns/op | B/op | allocs/op |
|-----------|-------|------|-----------|
| `BenchmarkForEachOnlinePlayer`（4 玩家，Pool 稳态） | **52.6** | 0 | **0** |

**单元测试：** 结果正确性、空房间不 panic、稳态 0 allocs（`testing.AllocsPerRun`）、20 goroutine 并发安全

---

### P1-4 · `GetFramesSince()` N 帧独立分配 → 单 backing buffer

**文件：** `svr/framesync/room.go`

**改动：** 两次遍历：第一遍统计总字节数，第二遍 `copy` 到单 `backing` buffer，各帧 `EncodedData` 引用其子切片。

```go
// Before: O(N) 独立分配
data := make([]byte, len(cf.EncodedData))   // 每帧一次

// After: 2 次分配（backing + result slice）
backing := make([]byte, totalBytes)          // 一次性
result  := make([]CachedFrame, 0, count)    // 一次性
```

| Benchmark | ns/op | B/op | allocs/op |
|-----------|-------|------|-----------|
| `BenchmarkGetFramesSince_100Frames`（100 帧） | 743 ns | 5,248 | **2** |
| `BenchmarkGetFramesSince_2400Frames`（满缓冲 2400 帧） | 16,978 ns | 122,882 | **2** |

> 优化前 allocs ≈ N 帧数；优化后恒为 2（无论 N 多大）。

**单元测试：** `unsafe.Pointer` 相邻帧地址连续性校验（验证真正共享 backing）、N=50 帧 allocs ≤ 3

---

### P1-5 · `SendFrameHash` 每帧 `new byte[8]` → 类字段缓存

**文件：** `cli/Client/FrameSync/FrameSyncClient.cs`（同步至 `unity/com.boom.boomnetwork/`）

```csharp
// Before
var buf = new byte[8];   // 每帧 20fps × N 玩家

// After
private readonly byte[] _hashBuf = new byte[8];   // 类字段，一次分配
```

---

### P1-6 · `SentBuffer` `LinkedList<T>` → `Queue<T>`

**文件：** `cli/Client/Session/NetworkSession.cs`（同步至 `unity/com.boom.boomnetwork/`）

```csharp
// Before
private readonly LinkedList<SentMessage> _sentBuffer = new();
_sentBuffer.AddLast(...);
_sentBuffer.RemoveFirst();

// After
private readonly Queue<SentMessage> _sentBuffer = new();
_sentBuffer.Enqueue(...);
_sentBuffer.Dequeue();
```

> `LinkedList<T>` 每节点独立堆分配 + 双向指针，缓存不友好。
> `Queue<T>` 内部循环数组，Enqueue/Dequeue O(1)，内存连续，GC 压力更低。

---

## 汇总对比表

### Go 服务器 Benchmark（Apple M silicon，`-benchtime=2s`）

| Benchmark | ns/op | allocs/op | 优化批次 |
|-----------|-------|-----------|---------|
| `BenchmarkPlayerCount`（串行） | **0.28** | 0 | P0-4 |
| `BenchmarkPlayerCount_Parallel` | **0.11** | 0 | P0-4 |
| `BenchmarkNextPlayerId`（串行） | **1.75** | 0 | P0-1 |
| `BenchmarkNextPlayerId_Parallel` | **44.9** | 0 | P0-1 |
| `BenchmarkDecodePooled_Parallel`（并发稳态） | **1.98** | 0 | P1-1 |
| `BenchmarkDecodePooled_Reuse`（串行稳态） | **8.7** | 0 | P1-1 |
| `BenchmarkDecode_Allocating`（基线对照） | 15.2 | **1** | — |
| `BenchmarkHandleFrameInput`（串行） | **54.6** | 0 | P0-2 |
| `BenchmarkHandleFrameInput_Parallel` | **49.9** | 0 | P0-2 |
| `BenchmarkForEachOnlinePlayer`（4 玩家） | **52.6** | 0 | P1-3 |
| `BenchmarkStepFrame_AllocsPerOp`（稳态含输入） | **90.9** | 1 | P0-3 |
| `BenchmarkStepFrame_NoInput`（稳态空帧） | **87.5** | 1 | P0-3 |
| `BenchmarkGetFramesSince_100Frames` | 743 ns | **2** | P1-4 |
| `BenchmarkGetFramesSince_2400Frames` | 16,978 ns | **2** | P1-4 |

### 关键改善汇总

| 路径 | 优化前 | 优化后 | 改善 |
|------|--------|--------|------|
| `PlayerCount()` | O(n) map 遍历 | **0.28 ns** 原子读 | 数量级↑ |
| `Decode()` 高并发 | 15.2 ns / 1 alloc | **1.98 ns / 0 alloc**（Pool 并发） | -87% ns，-100% alloc |
| `handleFrameInput` | 2 次 sync.Map 查找 | **1 次查找** | -50% Map 开销 |
| `GetFramesSince` 2400 帧 | ~2400 allocs | **2 allocs** | -99.9% alloc |
| `ForEachOnlinePlayer` | 1 alloc/call | **0 alloc**（Pool 稳态） | -100% alloc |
| `stepFrame` 双缓冲 | N alloc/帧（nil 释放） | **1 alloc/帧**（FrameData 逃逸） | 稳态零额外分配 |

---

## Benchmark 运行指南

```bash
# 全量单元测试
cd svr && go test ./...

# 全部 Benchmark（含 P0 + P1）
cd svr && go test -bench=. -benchmem -benchtime=2s ./...

# 只跑 P0 相关
cd svr && go test -bench='BenchmarkPlayerCount|BenchmarkNextPlayerId|BenchmarkHandleFrameInput|BenchmarkStepFrame' \
    -benchmem -benchtime=2s ./framesync/ ./cmd/framesync/

# 只跑 P1 相关
cd svr && go test -bench='BenchmarkDecode|BenchmarkDecodePooled|BenchmarkForEach|BenchmarkGetFramesSince' \
    -benchmem -benchtime=2s ./codec/ ./framesync/

# 竞争检测（并发安全验证）
cd svr && go test -race ./...
```

---

## 后续待优化项（P2）

> 以下为候选项，按预估收益排序。实施前需 profiling 确认真实瓶颈。

| 优先级 | 位置 | 问题 | 方向 |
|--------|------|------|------|
| P2-1 | `room.go: stepFrame` | `FrameData` 结构体仍有 1 alloc/帧（逃逸到堆） | 传 `*FrameData` 改为栈上复用，或 pool 化 |
| P2-2 | `room.go: tickLoop` | `time.NewTicker` 产生 goroutine per room | 共享全局 ticker wheel（房间数极多时） |
| P2-3 | `codec/message.go` | `FrameWriter` 10K 条有 9.3 KB alloc | `bufio.Writer` buf 复用（非热路径） |
| P2-4 | `transport/tcp.go` | 每连接独立 goroutine 模型 | epoll/netpoller 优化（仅超 1 万连接时有意义） |
| P2-5 | C# `TcpClientTransport` | `ArrayPool<byte>` 切片仍有 per-chunk 分配 | 环形 receive buffer（消除 chunk 拷贝） |
| P2-6 | `room.go: ForEachPlayer` | GM 查询路径有 `[]PlayerInfo` 临时分配 | Pool 化（低频，低优先） |
