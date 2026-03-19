# BoomNetwork 未来规划

## 当前状态 (v0.1)

```
✅ 已完成
├── Transport 层 (TCP + KCP, ITransport 接口)
├── Framing 层 (RingBuffer + ArrayPool)
├── Codec 层 (动态包头, 零分配)
├── Session 层 (Seq/AckSeq/SendAsync/消息缓冲)
├── ConnectionManager (心跳 + 重连编排 + 状态机)
├── IReconnectStrategy (Quick + Snapshot + Composite)
├── FrameSyncClient (帧收发 + 输入)
├── Go 帧同步服务器 (Room + Router)
├── 性能优化 (热路径零分配)
├── 压测 (TCP 3000人 / KCP 1000人)
└── 文档 + 测试体系
```

---

## Phase 1: 补全生产基础 — 让库可上线

### 1.1 服务端重连支持完善
**优先级: P0**

当前服务端重连只返回帧号。需要补全：
- 服务端消息缓冲区：保留最近 N 秒的已发送帧，支持客户端 ResendFrom(ackSeq)
- 快照生成与发送：服务端定期生成游戏状态快照，超时重连时返回
- 断线玩家保留：玩家断线后保留房间位置 N 秒，超时才踢出

### 1.2 FrameData 紧凑化
**优先级: P1**

```
当前:  PlayerId(4B) + DataLen(2B) + Data(NB)  = 6 + N per input
优化:  PlayerId(1B) + DataLen(1B) + Data(NB)  = 2 + N per input
       PlayerId 用房间内序号 (0-3)，DataLen 限 255B
```

预计下行再省 ~15%。

### 1.3 空帧紧凑化
**优先级: P1**

空帧（无输入）只发 FrameNumber(4B)，不发空的 InputCount。单独 Cmd 标识空帧。

```
当前空帧:  包头(3) + FrameNumber(4) + InputCount(2) = 9 bytes
优化后:    包头(3) + FrameNumber(4) = 7 bytes
```

### 1.4 连接鉴权
**优先级: P1**

SessionBind 时携带 token，服务端校验。防止非法连接。

---

## Phase 2: 移植到 Unity — 产生生产价值

### 2.1 Unity Package 封装
**优先级: P0**

- 将 C# 客户端代码打包为 Unity Package (UPM)
- 适配 Unity 的 MonoBehaviour 生命周期 (Update/FixedUpdate)
- 提供 UnityFrameSyncComponent 封装：
  ```csharp
  // Unity MonoBehaviour
  public class UnityFrameSyncComponent : MonoBehaviour
  {
      private FrameSyncClient _client;

      void Update() => _client.Tick(Time.deltaTime * 1000);
      void FixedUpdate() => /* 执行帧逻辑 */;
  }
  ```

### 2.2 替换 RunningCat 的 NetworkAdapter
**优先级: P0**

- 用 BoomNetwork 的 NetworkSession 替换现有 NetworkAdapter
- 用 ConnectionManager 替换 FrameSyncComponent 中的心跳/重连代码
- 用 ITransport (TCP/KCP) 替换现有 IMessageSender
- 逐步迁移，每步对真实服务器验证

### 2.3 协议兼容适配
**优先级: P0**

现有游戏服务器的线格式可能和 BoomNetwork 不同。两个选择：
- A: 改 BoomNetwork 的 Codec 适配现有服务器格式
- B: 改服务端协议和 BoomNetwork 对齐

建议选 A（客户端适配），不动服务端减少风险。

---

## Phase 3: 帧同步能力增强

### 3.1 帧预测 + 回滚 (Prediction & Rollback)
**优先级: P2**

客户端不等服务器确认，提前预测执行。服务器数据到达后校验，预测错误则回滚。

需要的基础设施：
- 游戏状态快照 (Snapshot): 每 N 帧保存，可恢复
- 确定性回放: 相同输入 → 相同结果
- 快速重执行: 回滚后一口气重跑 N 帧，不渲染

适用场景：格斗/竞技类对延迟极敏感的游戏。当前跑酷/派对类游戏优先级低。

### 3.2 帧压缩
**优先级: P2**

- 增量编码：只发和上一帧不同的输入
- LZ4 压缩：对大 payload (快照) 压缩，用 Flags bit 标记
- 预计快照带宽降 50-70%

### 3.3 多房间管理
**优先级: P1**

当前 Go 服务端只有一个全局 Room。需要：
- RoomManager: 创建/销毁/查找房间
- 匹配系统: 玩家排队 → 分配房间
- 房间生命周期: 创建 → 等待 → 游戏中 → 结算 → 销毁

### 3.4 观战
**优先级: P3**

观战者只收帧不发输入。服务端维护观战者列表，推帧时一起广播。

---

## Phase 4: 网络层增强

### 4.1 Payload 加密
**优先级: P2**

用 Flags 的保留位 (bit 2) 标记加密。对 Data 字段 AES 加密，包头明文。

### 4.2 Payload 压缩
**优先级: P2**

用 Flags 的保留位 (bit 3) 标记压缩。对 Data 字段 LZ4 压缩。

### 4.3 WebSocket Transport
**优先级: P3**

新增 `WsClientTransport : ITransport`，用于 Web 端接入。上层代码不变。

### 4.4 UDP Reliable Transport
**优先级: P3**

不依赖第三方 KCP 库，自研轻量可靠 UDP 层。减少 KCP 的内存开销 (当前 per-connection 331KB)。

---

## Phase 5: 工具链

### 5.1 帧录制与回放
**优先级: P1**

- 录制: 记录每帧的输入数据到文件
- 回放: 读取录制文件，脱离网络重放
- 用途: 不同步排查、自动化测试、比赛录像

### 5.2 网络模拟器
**优先级: P2**

给 ITransport 套一层代理，可配置：
- 延迟: 固定 + 随机抖动
- 丢包率
- 带宽限制
- 断线模拟

用于弱网测试，不需要真实网络环境。

### 5.3 监控面板
**优先级: P3**

实时显示：
- RTT / 丢包率 / 带宽
- 帧率 / 追帧进度
- 连接状态 / 重连次数

---

## 版本里程碑

| 版本 | 目标 | 关键交付 |
|------|------|---------|
| **v0.1** (当前) | 核心可用 | TCP/KCP 双端通信 + 帧同步 + 心跳重连 |
| **v0.2** | 生产就绪 | 服务端重连完善 + 连接鉴权 + FrameData 紧凑化 |
| **v0.3** | Unity 接入 | UPM Package + 替换 RunningCat NetworkAdapter |
| **v0.4** | 帧同步增强 | 多房间 + 帧录制回放 + 帧压缩 |
| **v1.0** | 正式版 | 加密 + 网络模拟器 + 监控面板 + 全文档 |
