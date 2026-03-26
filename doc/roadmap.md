# BoomNetwork 路线图

## 当前状态 (v0.2)

```
✅ v0.1 已完成
├── Transport 层 (TCP + KCP, ITransport 接口)
├── Framing 层 (RingBuffer + ArrayPool)
├── Codec 层 (动态包头, 零分配)
├── Session 层 (Seq/AckSeq/SendAsync/消息缓冲)
├── ConnectionManager (心跳 + 重连编排 + 状态机)
├── IReconnectStrategy (Quick + Snapshot + Composite)
├── FrameSyncClient (帧收发 + 输入，长生命周期)
├── Go 帧同步服务器 (Room + Router + RoomManager)
├── 性能优化 (热路径零分配)
├── 压测 (TCP 3000人 / KCP 1000人)
├── 文档 + 测试体系
│
✅ v0.2 已完成
├── Unity UPM 包 (com.boom.boomnetwork + com.boom.boomnetwork.gm)
├── 实体权威同步 (IEntitySync + Dead Reckoning + 惯性模型)
├── 多房间管理 (RoomManager + CreateRoom/JoinRoom/LeaveRoom)
├── 网络模拟器 netsim (延迟/抖动/丢包，Dashboard 滑块控制)
├── GM 工具 (Admin HTTP 10 端点 + WebSocket + ServerWindow 4 Tab)
├── 连接鉴权 (SessionBind token + env override + WS origin whitelist)
├── 运维 (systemd + Docker + health check + sd_notify)
├── 安全加固 (auth disconnect + per-IP rate limit)
└── Person 瘦身 + FrameSyncClient 长生命周期重构
```

---

## Phase 1: 开源就绪 — 降低接入门槛

### 1.1 文档重组
**优先级: P0**

- ✅ 按 shared/client/server/design 分目录 + 数字前缀排序
- 更新过时内容、修复交叉引用

### 1.2 服务端重连支持完善
**优先级: P0**

当前服务端重连只返回帧号。需要补全：
- 服务端消息缓冲区：保留最近 N 秒的已发送帧，支持客户端 ResendFrom(ackSeq)
- 快照生成与发送：服务端定期生成游戏状态快照，超时重连时返回
- 断线玩家保留：玩家断线后保留房间位置 N 秒，超时才踢出

### 1.3 FrameData 紧凑化
**优先级: P1**

```
当前:  PlayerId(4B) + DataLen(2B) + Data(NB)  = 6 + N per input
优化:  PlayerId(1B) + DataLen(1B) + Data(NB)  = 2 + N per input
       PlayerId 用房间内序号 (0-3)，DataLen 限 255B
```

预计下行再省 ~15%。

### 1.4 空帧紧凑化
**优先级: P1**

空帧（无输入）只发 FrameNumber(4B)，不发空的 InputCount。单独 Cmd 标识空帧。

```
当前空帧:  包头(3) + FrameNumber(4) + InputCount(2) = 9 bytes
优化后:    包头(3) + FrameNumber(4) = 7 bytes
```

---

## Phase 2: 帧同步能力增强

### ~~2.1 帧预测 + 回滚~~ (Prediction & Rollback)
**状态: 核心层代码已删除（2026-03-25）**

> Per [core-philosophy.md](shared/04-core-philosophy.md)：回滚不在核心层，只能作为可选中间件。
> PredictionManager / ISimulation / InputBuffer / SnapshotBuffer 已从 `Core/Prediction/` 删除。
>
> 如未来需要（格斗/竞技场景），以外部中间件形式实现，不进入框架核心包。

### 2.2 帧压缩
**优先级: P2**

- 增量编码：只发和上一帧不同的输入
- LZ4 压缩：对大 payload (快照) 压缩，用 Flags bit 标记
- 预计快照带宽降 50-70%

### 2.3 实体权威同步 Phase 2 — 权威转移
**优先级: P1**

Phase 1（自权威同步）已完成。Phase 2：运行时权威转移、仲裁者角色。

### 2.4 观战
**优先级: P3**

观战者只收帧不发输入。服务端维护观战者列表，推帧时一起广播。

---

## Phase 3: 网络层增强

### 3.1 Payload 加密
**优先级: P2**

用 Flags 的保留位 (bit 2) 标记加密。对 Data 字段 AES 加密，包头明文。

### 3.2 Payload 压缩
**优先级: P2**

用 Flags 的保留位 (bit 3) 标记压缩。对 Data 字段 LZ4 压缩。

### 3.3 WebSocket Transport
**优先级: P3**

新增 `WsClientTransport : ITransport`，用于 Web 端接入。上层代码不变。

### 3.4 UDP Reliable Transport
**优先级: P3**

不依赖第三方 KCP 库，自研轻量可靠 UDP 层。减少 KCP 的内存开销 (当前 per-connection 331KB)。

---

## Phase 4: 工具链

### 4.1 帧录制与回放
**优先级: P1**

- 录制: 记录每帧的输入数据到文件
- 回放: 读取录制文件，脱离网络重放
- 用途: 不同步排查、自动化测试、比赛录像

---

## 版本里程碑

| 版本 | 目标 | 关键交付 |
|------|------|---------|
| **v0.1** ✅ | 核心可用 | TCP/KCP 双端通信 + 帧同步 + 心跳重连 |
| **v0.2** ✅ | Unity + 生产工具 | UPM 包 + 实体同步 + 多房间 + GM 工具 + netsim + 鉴权 |
| **v0.3** | 开源就绪 | 文档重组 + 服务端重连完善 + FrameData 紧凑化 |
| **v0.4** | 帧同步增强 | 权威转移 + 帧压缩 + 帧录制回放 |
| **v1.0** | 正式版 | 加密 + WebSocket Transport + 全文档 |
