# 性能优化技能

## 已完成的优化

### 热路径零分配（C# 客户端）

| 组件 | 优化前 | 优化后 |
|------|--------|--------|
| Transport recv | new byte[bytesRead] 每次 | ArrayPool.Rent + ConcurrentQueue |
| Framing | new byte[frameLen] 每帧 | ArrayPool.Rent + PooledFrame |
| Codec Encode | new byte[] 每次 | 外部传入 Span<byte> |
| Codec Decode | new byte[] for Data | Decode_Pooled: ArrayPool |
| Framing 内部 | BlockCopy 前移剩余数据 | RingBuffer O(1) |
| Session pending | List 线性查找 | Dictionary O(1) |

### 热路径零分配（Go 服务端）

| 组件 | 优化前 | 优化后 |
|------|--------|--------|
| Encode | make([]byte) 每次 | sync.Pool 复用 |
| Room broadcast | 每次编码帧数据 | CachedFrame.EncodedData 预编码 |

### 包头优化

```
旧包头: [BodyLen:4][Version:1][Cmd:4][ClientSeq:4][ServerSeq:4] = 17 bytes
新包头: [FlagsCmd:1][BodyLen:2][Seq:0-4] = 3-9 bytes
节省: 53-82%
```

### 输入节流

```
旧: Unity Update 60fps 发 FrameInput → 60 msg/sec/client
新: 按服务器帧率 20fps 发 → 20 msg/sec/client
节省: 67%

空输入不发送:
旧: 每帧都发（包括 dir=0,0）→ 20 msg/sec
新: 有输入才发 → 空闲 0 msg/sec
节省: 空闲时 100%
```

## 仍需优化的点

### P0（影响正确性）
- 无

### P1（影响性能）
- VampireSurvivors Demo VSSnapshot.Serialize 仍有 byte[] 分配 — 应改静态 buffer
- EntityStateCodec.Encode 传入 IList<IEntitySync> 已避免 ToArray()，但 Encode 内部可进一步复用

### P2（可以后做）
- DecodeInput 的 static byte[8] 临时变量
- Camera.main 缓存（Unity Demo）
- OnGUI 字符串拼接

## 性能分析方法

### C# 用 BenchmarkDotNet
```bash
cd cli && dotnet run -c Release --project Benchmark
```

### Go 用内置 benchmark
```bash
cd svr && go test ./codec/ -bench=. -benchmem
```

### Unity Profiler
- Window → Analysis → Profiler
- 重点看: GC Alloc、CPU Time、Deep Profile
- 帧同步热路径: PersonManager.Update → Tick → OnFrame → ApplyMove

### 压测关注指标
```
帧率: 必须稳定 20.0 fps
内存: 不能持续增长（泄漏检测）
GC: 频率和暂停时间
带宽: 每客户端上下行
Goroutines: 不能持续增长
```

## 内存分配红线

```
热路径（每帧执行）:
  ✗ 不允许 new byte[]
  ✗ 不允许 new List/Dictionary
  ✗ 不允许 string 拼接
  ✗ 不允许 boxing (int → object)
  ✓ 允许 ArrayPool.Rent/Return
  ✓ 允许 stackalloc
  ✓ 允许 Span<byte>
  ✓ 允许 static 复用缓冲区

冷路径（连接/断开/房间操作）:
  ✓ 允许分配，不影响运行时性能
```

## 带宽预算（3000 人基准）

```
当前:
  上行: 2.10 MB/s (所有客户端合计)
  下行: 9.66 MB/s (所有客户端合计)
  每客户端: 上行 0.68 KB/s, 下行 3.14 KB/s

百兆网卡上限: 12.5 MB/s
当前下行已用: 77%

优化空间:
  - FrameData 紧凑化 (PlayerId 4B→1B) → 下行降 ~15%
  - Silent When Idle 已实现：无输入时不发 FrameInput
```
