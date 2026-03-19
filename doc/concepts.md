# 核心概念

## 帧同步 (Lockstep / Frame Sync)

一种多人游戏的网络同步方案。服务器不运行游戏逻辑，只做两件事：

1. 收集所有玩家的输入
2. 按固定帧率（如 20fps）把输入打包广播给所有客户端

每个客户端收到相同的输入，按相同的顺序执行相同的逻辑，得到相同的结果。

```
玩家A输入: 向右走 ─┐
                    ├→ 服务器打包成 "帧 100" → 广播给所有人
玩家B输入: 跳跃   ─┘

所有客户端执行帧 100: A 向右走 + B 跳跃 → 画面一致
```

**关键要求**：游戏逻辑必须是**确定性的**——给同样的输入，每台机器跑出完全一样的结果。不能用 `Random()`（要用种子随机），不能用 `float`（要用定点数）。

## 帧 (Frame)

帧同步中的最小时间单位。服务器每 50ms（20fps）产生一帧。每帧包含：
- 帧号（递增）
- 这一帧内所有玩家的输入

```
帧 100: [玩家A: 向右走] [玩家B: 跳跃]
帧 101: [玩家A: 向右走]              ← B 没操作就没输入
帧 102:                              ← 空帧，没人操作
帧 103: [玩家A: 攻击] [玩家B: 向左走]
```

## 追帧 (Frame Chasing)

客户端因为网络延迟，可能落后于服务器的当前帧。追帧就是加速执行历史帧来赶上进度。

```
服务器: 帧 100
客户端: 帧 95 → 快速执行 96,97,98,99,100 → 追上了
```

## 粘包 / 拆包 (Packet Sticking / Splitting)

TCP 是字节流协议，不保证消息边界。发两条消息，可能一次全到（粘包），也可能一条消息分两次到（拆包）。

```
发送: [消息A][消息B]

粘包:  一次收到 [消息A消息B]       ← 两条粘在一起了
拆包:  第一次收 [消息A的前半]
       第二次收 [消息A的后半消息B]  ← 一条被拆成两次
```

解决方式：每条消息前加长度前缀 `[BodyLen:4字节][消息内容]`，接收方先读长度再读内容。这就是 Framing 层做的事。

KCP 不需要 Framing（它保证消息边界），但 BoomNetwork 统一使用 Framing 层，确保 TCP 和 KCP 上层代码完全一致。

## Seq / AckSeq

**Seq (序列号)**：每条请求消息分配一个递增编号。服务器回复时带上相同的 Seq，客户端据此匹配"这是哪个请求的回复"。

```
客户端 → Seq=5 SessionBind
客户端 → Seq=6 Heartbeat
服务器 → Seq=6 HeartbeatRsp     ← 匹配到 Heartbeat
服务器 → Seq=5 SessionBindRsp   ← 匹配到 SessionBind（乱序也能匹配）
```

**AckSeq**：客户端已确认处理到的服务器消息序号。快速重连时告诉服务器"从这之后的消息请重发"。

## 心跳 (Heartbeat)

客户端定期（如每 3 秒）发一个空消息给服务器，服务器立刻回复。用途：

1. **检测断线**：如果 10 秒没收到回复，判定连接断了
2. **保持连接**：有些网络设备会清理长时间没数据的连接

心跳由 ConnectionManager 自动管理，FrameSyncClient 完全不知道心跳的存在。

## 快速重连 vs 超时重连

| | 快速重连 | 超时重连 |
|---|---------|---------|
| 触发 | 断线 < 3 秒 | 快速重连失败，或断线太久 |
| 做法 | 重建连接 + 重发未确认消息 | 重建连接 + 请求完整状态快照 |
| Session 状态 | 保留（不清缓冲区） | 全部清空 |
| 上层感知 | 几乎无感，补上缺失的帧 | 有感，画面跳到最新状态 |
| 服务端要求 | 需要保留最近几秒的消息缓冲 | 需要能生成状态快照 |

BoomNetwork 用 `CompositeReconnectStrategy` 自动编排：先尝试快速重连 3 次，失败后降级为超时重连。

## Tick 驱动

BoomNetwork 不使用 `async/await` 或后台定时器。所有逻辑都在 `Tick(deltaTimeMs)` 中同步执行：

```csharp
// 游戏主循环（Unity 的 Update）
void Update()
{
    client.Tick(Time.deltaTime * 1000);
    //       ↓ 内部按顺序做：
    //       1. Transport.Tick() — poll socket 收数据
    //       2. Framing — 拆包
    //       3. Codec — 解码
    //       4. Session — 分发消息、检查超时
    //       5. ConnectionManager — 心跳计时、重连检查
    //       6. FrameSyncClient — 帧分发回调
}
```

好处：所有回调在主线程触发，不需要锁，不会并发问题。这是游戏网络库的标准模式。

## 零分配 (Zero Allocation)

游戏每帧执行一次收发（20fps = 每秒 20 次）。如果每次都 `new byte[]`，大量短生命周期对象会触发 GC（垃圾回收），造成卡顿。

BoomNetwork 的做法：
- **ArrayPool**：从池里借 buffer，用完还回去，不 new
- **RingBuffer**：环形缓冲区，写入追加不移动
- **PooledFrame**：Framing 输出的帧用池化 buffer，调用方 `Dispose()` 归还
- **Span\<byte\>**：编码直接写入调用方提供的 buffer，不分配中间对象

## ITransport 接口

传输层的抽象。TCP 和 KCP 都实现这个接口，上层代码通过接口操作，不关心底层协议。

```csharp
// TCP
var transport = new TcpClientTransport();

// KCP — 一行切换，Session/ConnectionManager/FrameSyncClient 完全不变
var transport = new KcpClientTransport();
```

这就是**依赖倒置原则**：上层依赖抽象接口，不依赖具体实现。加新协议（如 WebSocket）只需新增一个类，不改已有代码。
