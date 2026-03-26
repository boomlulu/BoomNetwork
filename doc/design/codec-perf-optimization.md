# Codec 热路径性能优化实战

> 2026-03-27 | BoomNetwork v0.2
>
> 一次"加功能导致 4 倍回退 → 排查 → 修复 → 超越基线"的完整过程

---

## 背景

BoomNetwork 是一个自权威帧同步网络框架，C# 客户端 + Go 服务器。Codec 层（消息编解码）是整条链路中被调用最频繁的代码——每个玩家每帧至少 1 次 Encode + 1 次 Decode，3000 人 20fps 意味着 **每秒 12 万次编解码**。

v0.2 新增了三层 Cmd 分级（Core/Extended/Game），把原来的单一 `byte Cmd` 扩展为三种包头格式。功能上线后跑 benchmark 发现：

```
C# Encode 小消息:  3.6 ns → 15.1 ns  (+319%)
```

一个理应只改包头格式的重构，把最热的编码路径打慢了 4 倍。

---

## 第一轮：找到 4 倍回退的根因

### 症状

| 操作 | v0.1 基线 | 三层 Cmd 后 | 回退 |
|------|----------|-----------|------|
| C# Encode 小消息 | 3.6 ns | 15.1 ns | +319% |
| Go EncodeTo | 3.4 ns | 3.9 ns | +15% |
| C# Decode 小消息 | 6.4 ns | 7.3 ns | +14% |

### 根因：CmdExtraSize switch 属性被求值 3 次

三层 Cmd 重构引入了一个 `CmdExtraSize` 属性，内部是 C# switch 表达式：

```csharp
public int CmdExtraSize => MsgType switch
{
    CmdType.Extended => 2,
    CmdType.Game => 4,
    _ => 0,
};
```

问题在于 `Encode` 的一次调用中，这个 switch 被求值了 **3 次**：

```
Encode()
├── msg.TotalSize          ← 第 1 次: TotalSize → HeaderSize → CmdExtraSize
│   └── msg.HeaderSize
│       └── msg.CmdExtraSize  ← switch
├── int extra = msg.CmdExtraSize  ← 第 2 次
└── bool largeLen = ...CmdExtraSize  ← 第 3 次（通过 NeedLargeLen）
```

更致命的是：**C# switch 表达式会阻止 JIT 的内联决策**。v0.1 的 Encode 路径极简（一个 shift + 两个 BinaryPrimitives 写入），JIT 能把整个方法内联进 benchmark 循环。加入 switch 后内联被阻断，3.6ns 直接跳到 15.1ns。

### 修复

1. `CmdExtraSize` 属性 → `[AggressiveInlining] GetCmdExtraSize()` 方法
2. `Encode` 内部只算一次 `extra`，直接内联计算 `totalSize`，跳过 `TotalSize → HeaderSize` 属性链

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static int Encode(in Message msg, Span<byte> output)
{
    int extra = msg.GetCmdExtraSize();           // 只算一次
    int totalSize = 1 + (largeLen ? 4 : 2)       // 直接算，不走属性链
                  + seqSize + extra + dataLen;
    ...
}
```

**结果: 15.1 ns → 3.8 ns**，恢复到基线水平。

---

## 第二轮：消除所有剩余回退

修完 Encode 后，还有几个操作存在反增：

| 操作 | v0.1 | 现状 | 回退 |
|------|------|------|------|
| C# Decode 小消息 | 6.4 ns | 7.3 ns | +14% |
| C# Decode Pooled | 12.6 ns | 13.7 ns | +9% |
| Go EncodeTo | 3.4 ns | 3.9 ns | +15% |

### C# Decode：switch → if/else + AggressiveInlining

同样的三层 Cmd 重构在 Decode 里加了一个 switch：

```csharp
switch (cmdType)
{
    case CmdType.Core:     msg.Cmd = (byte)(flagsCmd >> 4); break;
    case CmdType.Extended: ... break;
    case CmdType.Game:     ... break;
}
```

帧同步流量 99% 是 Core 消息。C# 的 switch 在 ARM64 JIT 上会生成 bounds check + jump table，即使只有 3 个分支。改为 if/else 链，Core 在最前面：

```csharp
if (cmdType == CmdType.Core)          // ARM64: 单条 cbz 指令，几乎 100% 预测正确
    msg.Cmd = (byte)(flagsCmd >> 4);
else if (cmdType == CmdType.Extended)
    ...
else
    ...
```

加上 `[AggressiveInlining]`，Decode 小消息从 7.3ns → 6.9ns，Pooled 版本从 13.7ns → **12.1ns（低于 v0.1 基线）**。

### Go EncodeTo：Core 独立快速路径

Go 版本的问题类似但不同。`EncodeTo` 在每次调用时都要：

1. 调用 `cmdExtraSize()` 函数（内部有 switch）
2. 判断 `if msg.CmdType == CmdTypeCore` 来构造 FlagsCmd
3. 在尾部走一遍 `switch msg.CmdType` 写 ExtCmd/GameCmd

对 Core 消息来说，这三步全是空操作——extra 永远是 0，尾部 switch 永远 fallthrough。但 Go 编译器不会为你消除这些分支。

修复方案：**把 Core 路径完整独立出来**，写成零分支的直线代码：

```go
func EncodeTo(msg *Message, buf []byte) int {
    if msg.CmdType == CmdTypeCore {
        // 直线执行：无 cmdExtraSize 调用，无尾部 switch
        flagsCmd := (msg.Cmd & 0x0F) << 4
        ...
        return offset
    }
    // Extended / Game 路径
    ...
}
```

**结果: 3.9ns → 3.6ns**，Go Encode(Pool) 更是从 25.1ns → **23.1ns（低于 v0.1 基线）**。

---

## 最终成绩单

| 操作 | v0.1 基线 | 三层 Cmd 回退 | 两轮优化后 | vs 基线 |
|------|----------|-------------|-----------|---------|
| **C# Encode 小** | 3.6 ns | 15.1 ns | **3.8 ns** | +6% |
| **C# Decode 小** | 6.4 ns | 7.3 ns | **6.9 ns** | +8% |
| **C# Decode Pooled** | 12.6 ns | 13.7 ns | **12.1 ns** | **-4%** |
| **Go EncodeTo** | 3.4 ns | 3.9 ns | **3.6 ns** | +5% |
| **Go Encode Pool** | 24 ns | 25.1 ns | **23.1 ns** | **-4%** |
| **Go Decode 大** | 18 ns | 15.1 ns | **15.3 ns** | **-15%** |

- 全部零分配（热路径无 GC 压力）
- 三层 Cmd 功能完整保留（Core 3B / Extended 5B / Game 7B 包头）
- TCP 3000 人 / KCP 1000 人压测带宽和帧率不变
- 全量测试 7/7 绿 + 跨语言 C#↔Go fixture 互验

---

## 经验总结

### 1. 属性/方法调用次数在热路径上是乘数

`CmdExtraSize` 只是一个 3 行 switch，但被调用 3 次就是 3 倍开销。热路径上每一次属性访问都要审视——**求值一次，用变量传递**。

### 2. switch 表达式是 JIT 内联的杀手

C# JIT 的内联预算（IL 字节数阈值）对 switch 表达式非常敏感。一个 switch 可能让方法刚好超过内联阈值。`[AggressiveInlining]` 不是万能药，但在你确认方法足够小时，它能把决定权从启发式还给你。

### 3. if/else 优于 switch 当分支概率极度不均

帧同步场景下 Core 消息占 99%+。switch 生成 jump table 对三个等概率分支是最优的，但对一个 99% 命中的场景，一条 `if (== Core)` 比 jump table 快——ARM64 上是 `cbz` vs `ldr + br`。

### 4. Go 不会帮你消除恒为零的变量

`extra := cmdExtraSize(Core)` 永远返回 0，`totalPayload := dataLen + 0` 等价于 `dataLen`，但 Go 编译器不做这种跨函数常量传播。手动把 Core 路径独立出来，让编译器看到的就是直线代码。

### 5. 基线数字要持怀疑态度

v0.1 的 3.6ns Encode 低于 L1 cache 访问延迟（~4ns on M3），说明 JIT 把整个方法体优化掉了。这不是"真实性能"而是"benchmark 被优化掉的产物"。优化后的 3.8ns 才是三层 Cmd 的真实开销——比回退前好，但别拿 3.6ns 当目标。

---

## 技术栈

- C#: .NET 8 + BenchmarkDotNet, ARM64 RyuJIT
- Go: go1.23, ARM64 gc compiler
- 硬件: Apple M3 Pro, macOS 26.3
- 压测: TCP 3000 人 / KCP 1000 人, 20fps, 10 秒
