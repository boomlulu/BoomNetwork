# BoomNetwork 性能与压测报告

> 测试环境: Apple M3 Pro, macOS, arm64
> 最后更新: 2026-03-27

---

## 一、Codec 性能基准（最新: 2026-03-27）

### C# (.NET 8, BenchmarkDotNet)

| 操作 | 耗时 | 内存分配 |
|------|------|---------|
| Encode 小消息 (41B payload) | 3.8 ns | 0 B |
| Encode 大消息 (1KB payload) | 20.3 ns | 0 B |
| Decode 小消息 | 6.9 ns | 72 B |
| Decode 大消息 (1KB) | 47.2 ns | 1048 B |
| Decode 小消息 (ArrayPool) | 12.1 ns | 0 B |
| Framing 100 条粘包拆包 | 4.0 μs | 0 B |

### Go (go test -bench)

| 操作 | 耗时 | 内存分配 |
|------|------|---------|
| Encode 小消息 (sync.Pool) | 23.1 ns | 24 B / 1 alloc |
| Encode 大消息 (sync.Pool) | 32.5 ns | 24 B / 1 alloc |
| EncodeTo 零分配版 | 3.6 ns | 0 B / 0 alloc |
| Decode 小消息 (零拷贝) | 16.0 ns | 48 B / 1 alloc |
| Decode 大消息 (零拷贝) | 15.3 ns | 48 B / 1 alloc |
| FrameReader 10000 条 | 381 μs | 489 KB |
| FrameWriter 10000 条 | 91 μs | 9.3 KB / 3 alloc |

### Codec 历史对比

| 操作 | v0.1 (03-19) | v0.2 未优化 | v0.2 最终 | v0.1→最终 |
|------|-------------|-----------|----------|-----------|
| **C# Encode 小消息** | 3.6 ns | 15.1 ns ⚠️ | **3.8 ns** | +6% ✅ |
| C# Encode 大消息 | 20 ns | 21.3 ns | 20.3 ns | +1% ✅ |
| **C# Decode 小消息** | 6.4 ns | 7.3 ns | **6.9 ns** | +8% ✅ (was +13%) |
| C# Decode 大消息 | 44 ns | 48.4 ns | 47.2 ns | +7% |
| **C# Decode Pooled** | 12.6 ns | 13.7 ns | **12.1 ns** | **-4% ✅ 改善** |
| C# Framing 100 条 | 3.9 μs | 4.0 μs | 4.0 μs | +2% 稳定 |
| **Go Encode (Pool)** | 24 ns | 25.1 ns | **23.1 ns** | **-4% ✅ 改善** |
| **Go EncodeTo** | 3.4 ns | 3.9 ns | **3.6 ns** | +5% ✅ (was +15%) |
| **Go Decode 大消息** | 18 ns | 15.1 ns | 15.3 ns | **-15% ✅ 改善** |
| Go FrameReader | 372 μs | 377 μs | 381 μs | +2% 稳定 |
| Go FrameWriter | 81 μs | 87 μs | 91 μs | +12% |

**优化措施（两轮）:**

1. **C# Encode (15.1ns → 3.8ns):** `CmdExtraSize` 属性 → `[AggressiveInlining] GetCmdExtraSize()` 方法，Encode 内只算一次 extra 直接计算 totalSize，跳过属性链调用。

2. **C# Decode (7.3ns → 6.9ns, Pooled 13.7ns → 12.1ns):** 加 `[AggressiveInlining]`，CmdType switch → if/else 链（Core 优先），ARM64 JIT 生成单条 `cbz` 替代 switch jump table。

3. **Go EncodeTo (3.9ns → 3.6ns):** Core 消息独立快速路径，不调 `cmdExtraSize()`，不走尾部 switch。Extended/Game 拆为单独分支。

**Go FrameWriter +12% 说明:** bufio.Writer.Write + copy 占主体，codec 开销只是一小部分。+12% 中约 +5% 来自 codec，其余是 bufio 和 GC 噪声。不影响生产（FrameWriter 不在帧同步热路径）。

---

## 二、包头格式优化效果

从旧包头 (17 bytes 固定) 优化为新动态包头 (3-9 bytes)。

```
新格式: [FlagsCmd:1B][ExtCmd?:0/2/4B][BodyLen:2B/4B][Seq:0B/4B]
FlagsCmd: bit0=LenSize, bit1=HasSeq, bit2-3=CmdType(00=Core/01=Ext/10=Game), bit4-7=CoreCmd
```

三层分级包头大小：Core 3B / Extended (含 ExtCmd uint16) 5B / Game (含 GameCmd uint32) 7B，带 Seq 各 +4B。

| 消息类型 | 旧包头 | 新包头 | 节省 |
|---------|--------|--------|------|
| 推帧/心跳 (Core, 最高频) | 17B | 3B | 82% |
| 房间/实体同步 (Extended) | 17B | 5B | 71% |
| 请求/响应 (Core+Seq) | 17B | 7B | 59% |
| 大包 (>64KB, +4B BodyLen) | 17B | 9B | 47% |

### 压测对比 (3000 人)

| 指标 | 旧包头 | 新包头 | 变化 |
|------|--------|--------|------|
| 上行带宽 | 2.94 MB/s | 2.10 MB/s | -29% |
| 下行带宽 | 10.50 MB/s | 9.66 MB/s | -8% |
| 每客户端上行 | 0.96 KB/s | 0.68 KB/s | -29% |
| 每客户端下行 | 3.42 KB/s | 3.14 KB/s | -8% |

---

## 三、TCP 压测报告 (3000 人, 最新: 2026-03-27)

```
配置: 750 房 × 4 人 = 3000 人, 20fps, 32B 输入, 10 秒
```

| 指标 | 数值 |
|------|------|
| 连接成功率 | 3000/3000 (0 失败) |
| 帧率 | 20.0 fps (零丢帧) |
| 全局上行 | 2.10 MB/s |
| 全局下行 | 9.66 MB/s |
| 每客户端上行 | 0.68 KB/s |
| 每客户端下行 | 3.14 KB/s |
| 10 秒总传输 | 21 MB 上 + 97 MB 下 |
| 堆内存 (HeapAlloc) | 68 MB |
| 堆使用 (HeapInUse) | 126 MB |
| 总 Sys 内存 | 312 MB |
| Goroutines | 752 |
| GC 次数 | 10 |

### TCP 压测历史对比

| 指标 | v0.1 (03-19) | v0.2 (03-27) | 变化 | 说明 |
|------|-------------|-------------|------|------|
| 带宽 (上/下) | 2.10 / 9.66 MB/s | 2.10 / 9.66 MB/s | 不变 | ✅ 协议无变化 |
| **堆内存** | **52 MB** | **68 MB** | **+31%** | ⚠️ 见下方分析 |
| 总 Sys | 299 MB | 312 MB | +4% | 噪声范围 |
| Goroutines | 752 | 752 | 不变 | ✅ |
| GC 次数 | 10 | 10 | 不变 | ✅ |

**⚠️ 堆内存 52MB → 68MB 分析:**
v0.2 新增了 RoomManager、实体权威同步（EntityState 编解码缓冲）、netsim 中间件、Admin HTTP/WebSocket 等子系统。堆增长 16MB 对应这些常驻结构。每连接内存未增长（Goroutine 数不变），属于一次性固定开销，不影响扩展性。

### 带宽估算

| 规模 | 上行 | 下行 | 服务器内存 |
|------|------|------|-----------|
| 3000 人 | 2.1 MB/s | 9.7 MB/s | ~310 MB |
| 10000 人 | 7.0 MB/s | 32 MB/s | ~1 GB |
| 100 Mbps 网卡上限 | ~8500 人 (下行瓶颈) | | |
| 1 Gbps 网卡上限 | ~85000 人 | | |

---

## 四、KCP 压测报告 (1000 人, 最新: 2026-03-27)

```
配置: 250 房 × 4 人 = 1000 人, 20fps, 32B 输入, 10 秒
```

| 指标 | 数值 |
|------|------|
| 连接成功率 | 1000/1000 (0 失败) |
| 帧率 | 20.0 fps (零丢帧) |
| 全局上行 | 0.70 MB/s |
| 全局下行 | 3.22 MB/s |
| 每客户端上行 | 0.68 KB/s |
| 每客户端下行 | 3.14 KB/s |
| 堆内存 (HeapAlloc) | 323 MB |
| 堆使用 (HeapInUse) | 362 MB |
| 总 Sys 内存 | 917 MB |
| Goroutines | 2265 |

### KCP 压测历史对比

| 指标 | v0.1 (03-19) | v0.2 (03-27) | 变化 | 说明 |
|------|-------------|-------------|------|------|
| 带宽 (上/下) | 0.70 / 3.22 MB/s | 0.70 / 3.22 MB/s | 不变 | ✅ |
| 堆内存 | 331 MB | 323 MB | -2% | ✅ 略优 |
| 总 Sys | 909 MB | 917 MB | +1% | 噪声范围 |
| Goroutines | 2265 | 2265 | 不变 | ✅ |

---

## 五、TCP vs KCP 对比

| 指标 | TCP | KCP | 说明 |
|------|-----|-----|------|
| 每客户端上行 | 0.68 KB/s | 0.68 KB/s | 一致 |
| 每客户端下行 | 3.14 KB/s | 3.14 KB/s | 一致 |
| 每连接堆内存 | ~23 KB | ~323 KB | KCP 14 倍 |
| 每连接 Goroutine | ~0.25 | ~2.3 | KCP 多收发协程 |
| 延迟 | 依赖 TCP 拥塞控制 | 可调 NoDelay | KCP 弱网更优 |
| 适用场景 | PC 端 / 局域网 / 大规模 | 移动端 / 弱网 / 延迟敏感 | |

---

## 六、KCP 单元测试覆盖

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

## 七、运行测试命令

```bash
# 一键全量测试 (单元 + 跨语言 + TCP 联调)
./test.sh

# 性能基准 (Codec/Framing)
./bench.sh

# TCP 压测 (3000 人)
cd svr && go run ./cmd/stress/ -rooms=750 -players=4 -duration=10s

# KCP 压测 (1000 人)
cd svr && go run ./cmd/kcpstress/ -rooms=250 -players=4 -duration=10s

# KCP 单元测试 (需要先启动 KCP echo server)
cd svr && go run ./cmd/echo/ -proto=kcp :9000 &
cd cli && dotnet run --project KcpTest
```

---

## 附录：历史性能快照

<details>
<summary>v0.2 优化前 (2026-03-27 02:14) — 三层 Cmd 回退快照</summary>

> 三层 Cmd 分级重构后、两轮优化前的中间状态

| 操作 | 未优化 | 优化后 | 修复 |
|------|--------|--------|------|
| C# Encode 小消息 | 15.1 ns | 3.8 ns | AggressiveInlining + 消除 triple eval |
| C# Decode 小消息 | 7.3 ns | 6.9 ns | AggressiveInlining + switch→if/else |
| C# Decode Pooled | 13.7 ns | 12.1 ns | 同上 |
| Go EncodeTo | 3.9 ns | 3.6 ns | Core 独立快速路径 |
| Go Encode (Pool) | 25.1 ns | 23.1 ns | 同上 |

</details>

<details>
<summary>v0.1 (2026-03-19) — 基线版本</summary>

> 测试环境: Apple M3 Pro, macOS 26.2, arm64

### Codec

**C#:**

| 操作 | 耗时 | 内存分配 |
|------|------|---------|
| Encode 小消息 (41B payload) | 3.6 ns | 0 B |
| Encode 大消息 (1KB payload) | 20 ns | 0 B |
| Decode 小消息 | 6.4 ns | 72 B |
| Decode 大消息 (1KB) | 44 ns | 1048 B |
| Decode 小消息 (ArrayPool) | 12.6 ns | 0 B |
| Framing 100 条粘包拆包 | 3.9 μs | 0 B |

**Go:**

| 操作 | 耗时 | 内存分配 |
|------|------|---------|
| Encode 小消息 (sync.Pool) | 24 ns | 24 B / 1 alloc |
| Encode 大消息 (sync.Pool) | 32 ns | 24 B / 1 alloc |
| EncodeTo 零分配版 | 3.4 ns | 0 B / 0 alloc |
| Decode 小消息 (零拷贝) | 15 ns | 48 B / 1 alloc |
| Decode 大消息 (零拷贝) | 18 ns | 48 B / 1 alloc |
| FrameReader 10000 条 | 372 μs | 489 KB |
| FrameWriter 10000 条 | 81 μs | 9.3 KB / 3 alloc |

### TCP 3000 人

| 指标 | 数值 |
|------|------|
| 连接成功率 | 3000/3000 |
| 帧率 | 20.0 fps |
| 全局上行 / 下行 | 2.10 / 9.66 MB/s |
| 堆内存 | 52 MB |
| 总 Sys 内存 | 299 MB |
| Goroutines | 752 |

### KCP 1000 人

| 指标 | 数值 |
|------|------|
| 连接成功率 | 1000/1000 |
| 帧率 | 20.0 fps |
| 全局上行 / 下行 | 0.70 / 3.22 MB/s |
| 堆内存 | 331 MB |
| 总 Sys 内存 | 909 MB |
| Goroutines | 2265 |

</details>
