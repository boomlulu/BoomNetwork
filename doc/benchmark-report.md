# BoomNetwork 性能与压测报告

> 测试环境：Apple M silicon, macOS, arm64 / Go 1.24 / .NET 8
> 最后更新：2026-04-05

---

## TL;DR — 核心指标

| 指标 | 数值 |
|------|------|
| 最大并发玩家 | **4,000**（1000 房 × 4 人，15 分钟零掉线） |
| 帧率 | **20 fps**（服务器稳定 tick，零漂移） |
| 每客户端带宽 | **0.68 KB/s 上行，3.14 KB/s 下行** |
| 服务器堆内存（4000 人） | **200 MB** |
| Codec 编码（Go 零分配） | **3.6 ns / 0 alloc** |
| 热路径 GC 分配 | **0 bytes**（帧路径稳态） |
| `PlayerCount()` | **0.28 ns**（原子读，无锁） |
| `handleFrameInput` | **54.6 ns / 0 alloc**（单次 sync.Map 查找） |
| `GetFramesSince` 2400 帧补帧 | **2 allocs**（单 backing buffer） |
| 连续压测（15 分钟） | 0 掉线，0.0009% 丢帧率 |

---

## 一、Codec 性能基准（最新：2026-03-27）

### C#（.NET 8，BenchmarkDotNet）

| 操作 | 耗时 | 内存分配 |
|------|------|---------|
| Encode 小消息（41B payload） | 3.8 ns | 0 B |
| Encode 大消息（1KB payload） | 20.3 ns | 0 B |
| Decode 小消息 | 6.9 ns | 72 B |
| Decode 大消息（1KB） | 47.2 ns | 1048 B |
| Decode 小消息（ArrayPool） | 12.1 ns | 0 B |
| Framing 100 条粘包拆包 | 4.0 μs | 0 B |

### Go（go test -bench）

| 操作 | 耗时 | 内存分配 |
|------|------|---------|
| Encode 小消息（sync.Pool） | 23.1 ns | 24 B / 1 alloc |
| Encode 大消息（sync.Pool） | 32.5 ns | 24 B / 1 alloc |
| EncodeTo 零分配版 | 3.6 ns | 0 B / 0 alloc |
| Decode 小消息（零拷贝） | 16.0 ns | 48 B / 1 alloc |
| Decode 大消息（零拷贝） | 15.3 ns | 48 B / 1 alloc |
| FrameReader 10000 条 | 381 μs | 489 KB |
| FrameWriter 10000 条 | 91 μs | 9.3 KB / 3 alloc |

### Codec 历史对比

| 操作 | v0.1（03-19） | v0.2 未优化 | v0.2 最终 | 变化 |
|------|-------------|-----------|----------|------|
| C# Encode 小消息 | 3.6 ns | 15.1 ns ⚠️ | **3.8 ns** | +6% ✅ |
| C# Encode 大消息 | 20 ns | 21.3 ns | 20.3 ns | +1% ✅ |
| C# Decode 小消息 | 6.4 ns | 7.3 ns | **6.9 ns** | +8% ✅ |
| C# Decode Pooled | 12.6 ns | 13.7 ns | **12.1 ns** | -4% ✅ 改善 |
| Go Encode（Pool） | 24 ns | 25.1 ns | **23.1 ns** | -4% ✅ 改善 |
| Go EncodeTo | 3.4 ns | 3.9 ns | **3.6 ns** | +5% ✅ |
| Go Decode 大消息 | 18 ns | 15.1 ns | 15.3 ns | -15% ✅ 改善 |

**优化措施：**
1. **C# Encode（15.1ns → 3.8ns）：** `CmdExtraSize` 属性 → `[AggressiveInlining] GetCmdExtraSize()` 方法，消除三次重复计算
2. **C# Decode（7.3ns → 6.9ns）：** `AggressiveInlining` + switch → if/else，ARM64 JIT 生成单条 `cbz`
3. **Go EncodeTo（3.9ns → 3.6ns）：** Core 消息独立快速路径，不走 switch

---

## 二、服务器微优化追踪

### P0 优化（2026-04-03 · commit `1737229`）

> 测试文件：`svr/cmd/framesync/perf_test.go`、`svr/framesync/room_p0_test.go`

#### P0-1 · `nextPlayerId()` Mutex → `atomic.AddInt32`

```go
// Before: sync.Mutex 串行化
// After:  return atomic.AddInt32(&playerCounter, 1)
```

| Benchmark | ns/op | allocs/op |
|-----------|-------|-----------|
| `BenchmarkNextPlayerId`（串行） | **1.75** | 0 |
| `BenchmarkNextPlayerId_Parallel`（并发） | **44.9** | 0 |

#### P0-2 · `handleFrameInput` 双 sync.Map 查找 → 单次查找

新增 `connContext{playerId, *Room}` + `connContextMap sync.Map`，连接绑定时一次性存入，查找从 2 次降至 1 次。

| Benchmark | ns/op | allocs/op |
|-----------|-------|-----------|
| `BenchmarkHandleFrameInput`（串行） | **54.6** | 0 |
| `BenchmarkHandleFrameInput_Parallel`（并发） | **49.9** | 0 |

#### P0-3 · `pendingInputs/Events = nil` → 双缓冲 swap

```go
// Before: r.pendingInputs = nil  （每帧释放 + 重分配）
// After:  inputs := r.pendingInputs
//         r.pendingInputs = r.pendingInputsBuf[:0]
//         r.pendingInputsBuf = inputs
```
两侧均预分配（`NewRoomWithConfig` 中双端 `make([]T, 0, N)`），cap 永不为 0。

> **Bug（测试发现）：** 原实现只预分配 Buf 侧，首帧 swap 后另一侧 cap=0。`TestPendingBuf_CapNeverDropsToZero` 暴露并修复。

| Benchmark | ns/op | allocs/op |
|-----------|-------|-----------|
| `BenchmarkStepFrame_AllocsPerOp`（稳态含输入） | **90.9** | 1 |
| `BenchmarkStepFrame_NoInput`（稳态空帧） | **87.5** | 1 |

> 仅剩的 1 alloc 来自 `FrameData` 结构体逃逸，双缓冲 swap 本身零分配。

#### P0-4 · `PlayerCount()` O(n) 遍历 → `onlineCount int32` 原子计数

新增 `onlineCount int32` 字段，`AddPlayer`/`DisconnectPlayer`/`removePlayerLocked` 原子 ±1。

| Benchmark | ns/op | allocs/op |
|-----------|-------|-----------|
| `BenchmarkPlayerCount`（串行） | **0.28** | 0 |
| `BenchmarkPlayerCount_Parallel`（并发） | **0.11** | 0 |

---

### P1 优化（2026-04-04 · commit `441eb59`）

> 测试文件：`svr/codec/pool_test.go`、`svr/framesync/room_p1_test.go`

#### P1-1 · `Decode()` 每次 new → `msgPool` 复用

提取 `decodeInto()` 共享逻辑；新增 `DecodePooled()` + `PutMessage()`。

| Benchmark | ns/op | allocs/op |
|-----------|-------|-----------|
| `BenchmarkDecode_Allocating`（基线） | 15.2 | **1** |
| `BenchmarkDecodePooled_Reuse`（Pool 稳态） | **8.7** | **0** |
| `BenchmarkDecodePooled_Parallel`（并发稳态） | **1.98** | **0** |

#### P1-2 · `broadcast()` 每次 `make([]*Player)` → 复用 `broadcastBuf`

Room struct 新增 `broadcastBuf []*Player`（预分配 cap=16），与热路径 `broadcastSlice` 独立。

#### P1-3 · `ForEachOnlinePlayer()` 匿名 struct slice → `playerSlicePool` 复用

原每次分配 `[]struct{id, conn}`；改为 `sync.Pool` 复用 `[]*Player`，归还前置 nil 防 GC 泄漏。

| Benchmark | ns/op | allocs/op |
|-----------|-------|-----------|
| `BenchmarkForEachOnlinePlayer`（4 玩家，Pool 稳态） | **52.6** | **0** |

#### P1-4 · `GetFramesSince()` N 帧独立分配 → 单 backing buffer

两次遍历：第一遍统计总字节，第二遍 copy 到单 `backing` buffer，各帧 EncodedData 引用子切片。

| Benchmark | ns/op | allocs/op |
|-----------|-------|-----------|
| `BenchmarkGetFramesSince_100Frames` | 743 ns | **2** |
| `BenchmarkGetFramesSince_2400Frames` | 16,978 ns | **2** |

> 优化前 allocs ≈ N；优化后恒为 2（无论 N 多大）。

#### P1-5 · C# `SendFrameHash` 每帧 `new byte[8]` → 类字段缓存

```csharp
// Before: var buf = new byte[8];   // 20fps × N 玩家
// After:  private readonly byte[] _hashBuf = new byte[8];  // 类字段，一次分配
```

#### P1-6 · C# `SentBuffer` `LinkedList<T>` → `Queue<T>`

```csharp
// Before: LinkedList<SentMessage>（每节点独立堆分配，缓存不友好）
// After:  Queue<SentMessage>（循环数组，O(1) Enqueue/Dequeue，内存连续）
```

---

### P2 优化（2026-04-04 · commit `185de96`）

> 测试文件：`svr/framesync/room_p2_test.go`、`svr/framesync/room_manager_p2_test.go`

#### P2-1 · `ReportFrameHash` 每次 O(n) 清理 → 每 100 帧清理一次

原实现在每次 `ReportFrameHash` 调用时都遍历全表删除旧 hash，N 玩家 × 每帧上报 = O(N×帧数) 的清理代价。

```go
// Before: 每次调用都清理
if frameNumber > 200 { for fn := range r.frameHashes { ... } }

// After: 每 100 帧才触发一次
if frameNumber > 200 && frameNumber%100 == 0 { for fn := range r.frameHashes { ... } }
```

| Benchmark | ns/op | allocs/op | 说明 |
|-----------|-------|-----------|------|
| `BenchmarkReportFrameHash`（稳态，4 玩家，99:1 非清理:清理帧比） | **28.9** | 0 | 大多数帧跳过清理 |

#### P2-2 · 重连补帧无流控 → 批量发送（100 帧/批，批间 5ms）

原来一次性写入最多 2400 帧，可能撑爆 TCP 发送缓冲区。

```go
const replayBatchSize = 100
const replayBatchDelay = 5 * time.Millisecond
// 每 100 帧 sleep 5ms，让 ticker goroutine 有机会发实时帧
```

适用于 `handleReconnect` 和 `handleJoinRoom`（迟到者）两处补帧路径。

#### P2-3 · `time.Sleep` 保序 hack → FIFO 顺序保证

原来两个 goroutine 各自 `time.Sleep(10ms / 15ms)` 等待 `JoinRoomRsp` 先到，脆弱且有延迟。

```go
// Before: 两个独立 goroutine 各自 sleep
go func() { time.Sleep(10ms); sendSnapshot(); sendStart(); sendFrames() }()
go func() { time.Sleep(15ms); sendKV() }()

// After: 直接 sendMsg(conn, joinRsp)，合并为单 goroutine 无 sleep
sendMsg(conn, joinRsp)          // JoinRoomRsp 先入 FIFO 队列
go func() {
    sendSnapshot(); sendStart(); sendFrames()  // 后续消息保证在 joinRsp 之后
    sendKV()
}()
return nil
```

TCP/KCP 的 per-connection FIFO 保证顺序，彻底消除 sleep。

#### P2-4 · `GetRoomInfo()` 两次加锁 → 单次加锁

`IsRunning()` 单独加锁；现改为在一次锁内同时读 `running` + `onlineCount`。

| Benchmark | ns/op | allocs/op |
|-----------|-------|-----------|
| `BenchmarkGetRoomInfo`（串行） | **4.4** | 0 |
| `BenchmarkGetRoomInfo_Parallel`（并发） | **80.8** | 0 |

#### P2-5 · `MatchRoom` O(n) 线性扫描 → `matchIndex` 二级索引 O(1)

`RoomManager` 新增 `matchIndex map[string][]*Room`，`createRoomLocked`/`CreateRoomWithMaxPlayers`/`MatchRoom`/`RemoveRoom`/`StopAll` 均维护索引一致性。

```go
// Before: 遍历全部 rooms
for _, r := range rm.rooms { if r.MatchKey == matchKey ... }

// After: 直接查索引
for _, r := range rm.matchIndex[matchKey] { ... }
```

| Benchmark | ns/op | allocs/op | 说明 |
|-----------|-------|-----------|------|
| `BenchmarkMatchRoom_IndexPath`（1000 房间，指定 key） | **67.7** | 1 | 索引直接命中，不遍历全部 1000 房 |
| `BenchmarkMatchRoom_SameKey`（同 key 50 满房间） | **200.6** | 0 | 最差情况：扫完 50 个才新建 |

---

### 微优化汇总对比（Apple M silicon，`-benchtime=2s`）

| Benchmark | ns/op | allocs/op | 优化批次 |
|-----------|-------|-----------|---------|
| `BenchmarkPlayerCount`（串行） | **0.28** | 0 | P0-4 |
| `BenchmarkPlayerCount_Parallel` | **0.11** | 0 | P0-4 |
| `BenchmarkDecodePooled_Parallel`（并发稳态） | **1.98** | 0 | P1-1 |
| `BenchmarkNextPlayerId`（串行） | **1.75** | 0 | P0-1 |
| `BenchmarkNextPlayerId_Parallel` | **44.9** | 0 | P0-1 |
| `BenchmarkGetRoomInfo`（串行） | **4.4** | 0 | P2-4 |
| `BenchmarkDecodePooled_Reuse`（串行稳态） | **8.7** | 0 | P1-1 |
| `BenchmarkDecode_Allocating`（基线对照） | 15.2 | **1** | — |
| `BenchmarkReportFrameHash`（稳态） | **28.9** | 0 | P2-1 |
| `BenchmarkHandleFrameInput`（串行） | **54.6** | 0 | P0-2 |
| `BenchmarkHandleFrameInput_Parallel` | **49.9** | 0 | P0-2 |
| `BenchmarkForEachOnlinePlayer`（4 玩家） | **52.6** | 0 | P1-3 |
| `BenchmarkMatchRoom_IndexPath`（1000 房间） | **67.7** | 1 | P2-5 |
| `BenchmarkStepFrame_AllocsPerOp`（含输入） | **90.9** | 1 | P0-3 |
| `BenchmarkStepFrame_NoInput`（空帧） | **87.5** | 1 | P0-3 |
| `BenchmarkGetFramesSince_100Frames` | 743 ns | **2** | P1-4 |
| `BenchmarkGetFramesSince_2400Frames` | 16,978 ns | **2** | P1-4 |

---

### C1 修复（2026-04-05 · commit `1efb9fa`）

**问题：** `NetworkSession._pendingRequests`（`Dictionary<int,PendingRequest>`）被主线程（`CheckTimeouts`）和收包线程（`DispatchMessage`）并发读写，无任何同步 → 未定义行为。

**方案选型：SpinLock + 普通 Dictionary（新建 `PendingRequestTable` 类）**

| 方案 | 无竞争开销 | 额外分配 | 语义变化 |
|---|---|---|---|
| Monitor lock | ~15–20 ns | 无 | 无 |
| ConcurrentDictionary | ~30–50 ns | struct 更新需 TryUpdate loop | 无 |
| ConcurrentQueue（主线程消费） | 0 ns | Message 需 copy（ArrayPool） | callback 延迟到下帧 |
| **SpinLock（本方案）** | **~1–2 ns（单次 CAS）** | **无** | **无** |

选 SpinLock 的关键依据：
- 临界区极短（纯 dict Add/Remove，n≤3，稳态 n=0）
- 无竞争概率 >99.99%（recv 线程只在 `HasSeq` 消息时才进 Remove）
- callback 必须在锁外调用（`PendingRequestTable` 设计强制此约束）

#### 实测数据（.NET 8，Apple M silicon，NUnit 内嵌 Stopwatch）

| Benchmark | 结果 | 说明 |
|---|---|---|
| `Add + TryComplete` pair（无竞争） | **68.7 ns/op** | 含 2 次 SpinLock Enter/Exit |
| `DrainTimeouts` 空表快路径 | **11.0 ns/op** | `Count==0` 立即返回，稳态帧开销 |
| `Concurrent_TryComplete_vs_DrainTimeouts` | **50000 次/80ms** | 5万次竞态迭代，callback 总数精确=50000 |

**结构变更：**
- 新文件 `cli/Client/Session/PendingRequestTable.cs`（含 SpinLock，禁 readonly）
- `NetworkSession` 中 `_pendingRequests` 从 `Dictionary<>` → `PendingRequestTable`
- `CheckTimeouts` 逻辑内聚到 `DrainTimeouts(delta, timedOut)`，锁外触发 callback
- `CancelAllPending` 改为 `CancelAll(cancelled)` 锁外触发

---

### C2 修复（2026-04-05 · commit `807b6b4`）

**问题：** `TcpClientTransport._stream` 和 `WebSocketClientTransport._ws` 在 `Send()` 中于 `_sendLock` **外**读取，`Disconnect()` 置 null 和关流时不持锁，三条竞态路径：

1. `Send()` 锁外 null 检查（TOCTOU） → 进锁后流已关 → Write 抛 `ObjectDisposedException`，靠 catch 兜底（不干净）
2. `RecvLoop`（recv 线程）读 `_stream` 无 volatile → `Disconnect()` 置 null 后可能不可见，多跑一轮才靠 IOException 退出
3. `Disconnect()` 关流时不持 `_sendLock` → 与正在进行的 `Write()` 并发撕裂流状态

**修复（两个文件对称）：**

| 变更点 | 修复前 | 修复后 |
|---|---|---|
| `_stream`/`_ws` 声明 | 普通字段 | `volatile`（recv 线程即时可见） |
| `Send()` 捕获时机 | `_sendLock` **外** read + check | `_sendLock` **内** read + check（消除 TOCTOU）|
| `Disconnect()` 置空 | 直接赋值 `= null` | `lock(_sendLock)` 内 swap，Close 在锁外执行 |

```csharp
// Send()：修复后
lock (_sendLock) {
    var stream = _stream;   // capture 在锁内
    if (stream == null) return;
    stream.Write(...);
}

// Disconnect()：修复后
NetworkStream? stream;
lock (_sendLock) { stream = _stream; _stream = null; }   // 极短临界区，仅 swap
try { stream?.Close(); } catch { }                        // Close 在锁外
```

#### 实测数据（.NET 8，Apple M silicon，NUnit 内嵌 Stopwatch）

| Benchmark | 结果 | 说明 |
|---|---|---|
| `Send()` 热路径（已连接，含 TCP write） | **2161 ns/op** | 与修复前完全相同（热路径始终持锁，无额外开销） |
| `Disconnect()` 已断开快路径 | **11.6 ns/op** | 多了一次 `lock(_sendLock)` 获取（无竞争 ~1 ns） |
| `Concurrent_Send_And_Disconnect` | 20 轮 × 4线程 × 200次 **0 NRE** | 并发压力测试，NullReferenceException 完全消除 |

**结构变更：**
- `cli/Client/Transport/TcpClientTransport.cs`：`volatile _stream` + Send/Disconnect 改造
- `cli/Client/Transport/WebSocketClientTransport.cs`：`volatile _ws` + Send/Disconnect 改造（对称）
- 同步到 `unity/com.boom.boomnetwork/Runtime/Client/Transport/`

### C3 修复（2026-04-04）

**问题：** `FrameSyncProtocol` 的 11 个解码方法均无边界检查：直接按字节偏移读取，截断数据触发 `IndexOutOfRangeException`；`inputCount`（ushort）× `dataLen`（ushort）最大可构造 4 GB 单包导致 OOM。

**攻击面（修复前）：**
- 任意网络节点发送截断帧 → 服务器/客户端 panic
- 构造 `inputCount=65535, dataLen=65535` → 4 GB 单包 OOM（Go 和 C# 两端均受影响）
- `FrameHashMismatch`, `StateSyncCodec`, `RoomCodec` 等所有解码路径均裸访问

**修复方案（C# 和 Go 对称）：**

| 防护点 | 方法 |
|---|---|
| 入口最小长度检查 | 所有 11 个 C# 解码方法 + Go `DecodeInitData`/`DecodeFrameData` |
| 循环内每次迭代前检查 | `FrameData` inputs 循环、events 循环、`RoomList` 循环、`PushDataSync` entries 循环 |
| 上限常量（OOM 防护） | C#: `ProtocolLimits`（`MaxFrameInputs=256`, `MaxInputDataLen=4096`, `MaxFrameEvents=32`）<br>Go: 包级常量（对称） |
| 错误类型 | C# → `InvalidDataException`（不改返回类型，调用方无感），Go → `(*T, error)` |

```csharp
// C# ProtocolLimits（集中定义，防 OOM）
internal static class ProtocolLimits
{
    public const int MaxFrameInputs  = 256;
    public const int MaxInputDataLen = 4096;
    public const int MaxFrameEvents  = 32;
    public const int MaxRooms        = 1000;
    // ...
}
```

```go
// Go 对称常量
const (
    maxFrameInputs  = 256
    maxInputDataLen = 4096
    maxFrameEvents  = 32
)
```

**测试覆盖（新增）：**

| 测试文件 | 测试数 | 覆盖内容 |
|---|---|---|
| `cli/Tests/ProtocolBoundsTests.cs` | 24 | 合法 roundtrip、空缓冲区、截断、上限拒绝 |
| `svr/framesync/protocol_bounds_test.go` | 12 | DecodeInitData + DecodeFrameData 全场景 |

全部 24 C# 测试通过，全部 Go framesync 测试通过（含新增 12 个）。

**结构变更：**
- `cli/Core/FrameSync/FrameSyncProtocol.cs`：新增 `ProtocolLimits` 类，11 方法全量加固
- `svr/framesync/protocol.go`：`DecodeInitData` 加入口检查，`DecodeFrameData` 改 `(*FrameData, error)` 签名
- `svr/framesync/protocol_test.go` + `room_p0/p1_test.go` + `room_test.go`：7 处调用更新
- 同步到 `unity/com.boom.boomnetwork/Runtime/Core/FrameSync/`

---

### H/M/L 全量修复（2026-04-05）

**修复范围：** 20 个安全/稳定性问题（H×6、M×7、L×7），零架构变更，全部向后兼容。

#### High 级（6 个）

| 编号 | 问题 | 文件 | 修复 |
|---|---|---|---|
| H1 | `handleJoinRoom` 补帧 goroutine 无超时 | `main.go` | `context.WithTimeout(30s)` + `select ctx.Done()` |
| H2 | `stepFrame` 广播前未检查 `p.Conn != nil` | `room.go` | `if c := p.Conn; c != nil { c.Send(...) }` |
| H3 | `FrameSyncClient`/`RoomClient` 事件订阅不取消 | `FrameSyncClient.cs`, `RoomClient.cs` | `DestroyNetworkStack` 中 `-=` 取消；lambda 改命名方法；`RoomClient: IDisposable` |
| H4 | `NetworkSession.OnTransportData` ArrayPool buffer 异常时不归还 | `NetworkSession.cs` | `try/finally { MessageCodec.ReturnData(ref msg); }` |
| H5 | `ServerHealthChecker` coroutine 在 scene unload 后继续触发 | `ServerHealthChecker.cs` | `_destroyed` flag + `OnDestroy` → `StopAllCoroutines` + yield break guard |
| H6 | `handleFrameInput` 未验证房间是否仍在 RoomManager 中 | `main.go` | `roomMgr.GetRoom(ctx.room.ID) == nil` 时删除 connContext 并 return |

#### Medium 级（7 个）

| 编号 | 问题 | 文件 | 修复 |
|---|---|---|---|
| M1 | `uint32` 帧号溢出无保护 | `room.go` | `math.MaxUint32` 检测 → 主动停房间 |
| M2 | (已在 C3 中修复) | — | — |
| M3 | `UpdateSnapshot` delegate TOCTOU | `room.go` | 锁内捕获 `d := r.delegate`，锁外调用 |
| M4 | `playerSlicePool` 过大 slice 不归还 | `room.go` | `cap > 64` 时丢弃，不放回池 |
| M5 | WebSocket origin 线性扫描 | `admin_ws.go` | `map[string]struct{}` O(1) 查找 |
| M6 | `BoomNetworkManager` 闭包订阅泄漏 | `BoomNetworkManager.cs` | lambda → 命名方法 + `OnDestroy` 取消订阅 |
| M7 | `StateSyncCodec.DecodePushData` `ToArray()` 分配 | `FrameSyncProtocol.cs` | (已在 C3 加固时一并改为 `ReadOnlySpan<byte>`) |

#### Low 级（7 个）

| 编号 | 问题 | 文件 | 修复 |
|---|---|---|---|
| L1 | `sync.Map` 裸类型断言（16 处） | `main.go`, `admin_ws.go` | 5 个类型安全辅助函数（`loadConnPlayerId` 等），`ok2` 双重检查 |
| L2 | `conn.Write("READY=1")` 不检查错误 | `main.go` | `if _, err := conn.Write(...); err != nil` |
| L3 | `SnapshotIntervalFrames=0` 无默认值 | `room.go` | `RoomConfig.Validate()` 填充零值 |
| L4 | `EncodeInitData` 每次 `make([]byte, 24)` | `protocol.go` | (低频路径，暂跳过) |
| L5 | `BoomNetworkManager.host` 预填生产地址 | `BoomNetworkManager.cs` | 改为空字符串 |
| L6 | `FrameSyncClient._entityStateBuf` 字段驻留 | `FrameSyncClient.cs` | `ArrayPool<byte>.Shared.Rent/Return` 按需借还 |
| L7 | 环形缓冲区取模防负数无注释 | `room.go` | 添加 `// +len(r.frameRing) 防负数` 注释 |

#### 测试结果（修复后）

| 套件 | 通过 | 失败 |
|---|---|---|
| Go `./...` | 全部通过 | 0 |
| C# `dotnet test` | 108 | 0（含 1 处预存 bug 同步修复）|

#### 新增基准（`-benchtime=3s`，Apple M silicon）

| Benchmark | ns/op | allocs/op |
|---|---|---|
| `BenchmarkStepFrame_AllocsPerOp`（含输入） | **74.5** | 0 |
| `BenchmarkStepFrame_NoInput`（空帧） | **2.97** | 0 |
| `BenchmarkForEachOnlinePlayer` | **55.7** | 0 |
| `BenchmarkMatchRoom_IndexPath` | **67.5** | 1 |
| `BenchmarkMatchRoom_SameKey` | **200.1** | 0 |
| `BenchmarkReportFrameHash` | **27.7** | 0 |
| `BenchmarkGetFramesSince_2400Frames` | **3,110** | 2 |
| `BenchmarkGetRoomInfo` | **4.5** | 0 |

---

## 三、包头格式优化效果（2026-03-27）

三层分级包头：Core 3B / Extended 5B / Game 7B，带 Seq 各 +4B（原固定 17B）。

| 消息类型 | 旧包头 | 新包头 | 节省 |
|---------|--------|--------|------|
| 推帧/心跳（Core，最高频） | 17B | 3B | 82% |
| 房间/实体同步（Extended） | 17B | 5B | 71% |
| 请求/响应（Core+Seq） | 17B | 7B | 59% |
| 大包（>64KB，+4B BodyLen） | 17B | 9B | 47% |

| 指标 | 旧包头 | 新包头 | 变化 |
|------|--------|--------|------|
| 上行带宽 | 2.94 MB/s | 2.10 MB/s | **-29%** |
| 下行带宽 | 10.50 MB/s | 9.66 MB/s | **-8%** |
| 每客户端上行 | 0.96 KB/s | 0.68 KB/s | **-29%** |
| 每客户端下行 | 3.42 KB/s | 3.14 KB/s | **-8%** |

---

## 四、TCP 压测报告

### 4000 人，15 分钟连续压测（2026-03-28）

```
配置：1000 房 × 4 人 = 4000 人，20fps，32B 输入，15 分钟
```

| 指标 | 数值 |
|------|------|
| 连接成功率 | 4000/4000（0 失败） |
| 帧率 | 20.0 fps（全程零漂移） |
| 帧接收率 | 71,999,366 / 72,000,000（**99.999%**） |
| 全局上行 | 2.80 MB/s |
| 全局下行 | 12.88 MB/s |
| 每客户端上行 | 0.68 KB/s |
| 每客户端下行 | 3.14 KB/s |
| 总传输量 | 14.1 GB |
| 堆内存（HeapAlloc） | **200 MB** |
| GC 次数 | 126（约 7.1s / 次） |
| Goroutines | 1,002 |
| 掉线数 | **0** |

### 3000 人，10 秒压测（2026-03-27，历史对比用）

```
配置：750 房 × 4 人 = 3000 人，20fps，32B 输入，10 秒
```

| 指标 | v0.1（03-19） | v0.2（03-27） | 变化 |
|------|-------------|-------------|------|
| 连接成功率 | 3000/3000 | 3000/3000 | ✅ |
| 帧率 | 20.0 fps | 20.0 fps | ✅ |
| 带宽（上/下） | 2.10 / 9.66 MB/s | 2.10 / 9.66 MB/s | 不变 ✅ |
| 堆内存 | 52 MB | 68 MB | +31% ⚠️ 见注 |
| 总 Sys | 299 MB | 312 MB | +4% |
| Goroutines | 752 | 752 | ✅ |
| GC 次数 | 10 | 10 | ✅ |

> ⚠️ **堆内存 52MB → 68MB 分析：** v0.2 新增 RoomManager、EntityState 编解码缓冲、netsim 中间件、Admin HTTP/WebSocket 等常驻子系统，+16MB 为一次性固定开销，不影响扩展性（每连接内存未增长）。

---

## 五、Soak Test — 内存泄漏检测（2026-03-28）

```
配置：100 房 × 4 人 = 400 人，5 分钟，每 30 秒采样
```

| 时间 | Heap | GC 次数 | Goroutines |
|------|------|---------|------------|
| 0:30 | 36 MB | 10 | 1,302 |
| 1:30 | 40 MB | 18 | 1,302 |
| 3:00 | 42 MB（峰值） | 30 | 1,302 |
| 5:00 | 36 MB | 46 | 1,302 |

**结论：无内存泄漏。** 堆升后回落基线，GC 频率稳定（约 6.5s / 次），Goroutine 数量全程不变。

---

## 六、KCP 压测报告（2026-03-27）

```
配置：250 房 × 4 人 = 1000 人，20fps，32B 输入，10 秒
```

| 指标 | v0.1（03-19） | v0.2（03-27） | 变化 |
|------|-------------|-------------|------|
| 帧率 | 20.0 fps | 20.0 fps | ✅ |
| 带宽（上/下） | 0.70 / 3.22 MB/s | 0.70 / 3.22 MB/s | 不变 ✅ |
| 堆内存 | 331 MB | 323 MB | -2% ✅ |
| 总 Sys | 909 MB | 917 MB | +1%（噪声） |
| Goroutines | 2265 | 2265 | ✅ |

### KCP 单元测试覆盖

| 测试 | 说明 | 结果 |
|------|------|------|
| Basic Send/Recv | 基本收发 | PASS |
| Small Packet | 1 byte payload | PASS |
| Large Packet | 32KB payload | PASS |
| High Frequency | 100 条无间隔连发 | PASS |
| Sticky Packets | 50 条快速连发，验证粘包拆分 | PASS |
| Sticky Content | 粘包后内容正确性 | PASS |
| Mixed Sizes | 大小交替发送 | PASS |

---

## 七、TCP vs KCP 对比

| 指标 | TCP | KCP | 说明 |
|------|-----|-----|------|
| 每客户端上行 | 0.68 KB/s | 0.68 KB/s | 一致 |
| 每客户端下行 | 3.14 KB/s | 3.14 KB/s | 一致 |
| 每连接堆内存 | ~23 KB | ~323 KB | KCP 14 倍 |
| 每连接 Goroutine | ~0.25 | ~2.3 | KCP 多收发协程 |
| 延迟 | 依赖 TCP 拥塞控制 | 可调 NoDelay | KCP 弱网更优 |
| 适用场景 | PC / 局域网 / 大规模 | 移动端 / 弱网 / 延迟敏感 | |

### 带宽容量估算（TCP）

| 规模 | 上行 | 下行 | 服务器内存 |
|------|------|------|-----------|
| 3000 人 | 2.1 MB/s | 9.7 MB/s | ~310 MB |
| 4000 人 | 2.8 MB/s | 12.9 MB/s | ~200 MB |
| 10000 人 | 7.0 MB/s | 32 MB/s | ~1 GB |
| 100 Mbps 网卡上限 | ~8500 人（下行瓶颈） | | |
| 1 Gbps 网卡上限 | ~85000 人 | | |

---

## 八、Silent When Idle — 带宽节省效果

| 场景 | 优化前 | 优化后 | 变化 |
|------|--------|--------|------|
| 空闲玩家发包频率 | 20 msg/s | 0 msg/s | **-100%** |
| 3000 空闲玩家带宽 | 480 KB/s | 0 KB/s | **-100%** |
| 主动输入频率 | 60 fps | 20 fps | **-67%** |

---

## 九、后续待优化项（P2 候选）

> 实施前需 profiling 确认真实瓶颈。

| 优先级 | 位置 | 问题 | 方向 |
|--------|------|------|------|
| P2-1 | `room.go: stepFrame` | `FrameData` 仍有 1 alloc/帧（逃逸到堆） | pool 化或改为栈上复用 |
| P2-2 | `room.go: tickLoop` | 每 room 独立 `time.Ticker` goroutine | 共享全局 ticker wheel（房间数极多时） |
| P2-3 | `codec: FrameWriter` | 10K 条有 9.3 KB alloc | `bufio.Writer` buf 复用（非热路径） |
| P2-4 | `transport/tcp.go` | 每连接独立 goroutine 模型 | epoll/netpoller（仅超 1 万连接时有意义） |
| P2-5 | C# `TcpClientTransport` | per-chunk ArrayPool 分配 | 环形 receive buffer（消除 chunk 拷贝） |
| P2-6 | `room.go: ForEachPlayer` | GM 查询 `[]PlayerInfo` 临时分配 | Pool 化（低频，低优先） |

---

## 十、Benchmark 运行指南

```bash
# 一键全量测试（单元 + 跨语言 + TCP 联调）
./test.sh

# 性能基准（Codec/Framing）
./bench.sh

# Go 全部 Benchmark（含 P0 + P1 微优化）
cd svr && go test -bench=. -benchmem -benchtime=2s ./...

# 只跑 P0
cd svr && go test -bench='BenchmarkPlayerCount|BenchmarkNextPlayerId|BenchmarkHandleFrameInput|BenchmarkStepFrame' \
    -benchmem -benchtime=2s ./framesync/ ./cmd/framesync/

# 只跑 P1
cd svr && go test -bench='BenchmarkDecode|BenchmarkDecodePooled|BenchmarkForEach|BenchmarkGetFramesSince' \
    -benchmem -benchtime=2s ./codec/ ./framesync/

# 竞争检测
cd svr && go test -race ./...

# TCP 压测（4000 人，15 分钟）
cd svr && go run ./cmd/stress/ -rooms=1000 -players=4 -duration=15m -addr=127.0.0.1:9000

# TCP 压测（3000 人，10 秒，快速）
cd svr && go run ./cmd/stress/ -rooms=750 -players=4 -duration=10s

# KCP 压测（1000 人）
cd svr && go run ./cmd/kcpstress/ -rooms=250 -players=4 -duration=10s

# KCP 单元测试（需先启动 echo server）
cd svr && go run ./cmd/echo/ -proto=kcp :9000 &
cd cli && dotnet run --project KcpTest

# C# Codec 基准
cd cli/Benchmark && dotnet run -c Release
```

---

<details>
<summary>历史快照 — v0.2 优化前（2026-03-27，三层 Cmd 重构后）</summary>

| 操作 | 未优化 | 优化后 | 修复 |
|------|--------|--------|------|
| C# Encode 小消息 | 15.1 ns | 3.8 ns | AggressiveInlining + 消除三次重复计算 |
| C# Decode 小消息 | 7.3 ns | 6.9 ns | AggressiveInlining + switch→if/else |
| C# Decode Pooled | 13.7 ns | 12.1 ns | 同上 |
| Go EncodeTo | 3.9 ns | 3.6 ns | Core 独立快速路径 |
| Go Encode（Pool） | 25.1 ns | 23.1 ns | 同上 |

</details>

<details>
<summary>历史快照 — v0.1 基线（2026-03-19）</summary>

**C#：**

| 操作 | 耗时 | 内存分配 |
|------|------|---------|
| Encode 小消息 | 3.6 ns | 0 B |
| Encode 大消息 | 20 ns | 0 B |
| Decode 小消息 | 6.4 ns | 72 B |
| Decode Pooled | 12.6 ns | 0 B |
| Framing 100 条 | 3.9 μs | 0 B |

**Go：**

| 操作 | 耗时 | 内存分配 |
|------|------|---------|
| EncodeTo | 3.4 ns | 0 B |
| Encode（Pool） | 24 ns | 24 B |
| Decode 小消息 | 15 ns | 48 B |
| Decode 大消息 | 18 ns | 48 B |

**TCP 3000 人：**

| 指标 | 数值 |
|------|------|
| 连接成功率 | 3000/3000 |
| 帧率 | 20.0 fps |
| 全局带宽 | 2.10 / 9.66 MB/s |
| 堆内存 | 52 MB |
| Goroutines | 752 |

</details>
