# 时序图

## 1. 正常游戏流程

```
  Client A          Server          Client B
     │                │                │
     ├─ Connect ─────→│                │
     │                │←─ Connect ─────┤
     │                │                │
     ├─ SessionBind ─→│                │
     │←─ BindRsp(id=1)│                │
     │                │←─ SessionBind ─┤
     │                │─ BindRsp(id=2)→│
     │                │                │
     │  (2人到齐，自动开始)              │
     │                │                │
     │←─ StartFrameSync(20fps) ───────→│
     │                │                │
     ├─ FrameInput ──→│                │  帧 1
     │                │←─ FrameInput ──┤
     │←─ PushFrames(帧1: A输入+B输入) ─→│
     │                │                │
     ├─ FrameInput ──→│                │  帧 2
     │←─ PushFrames(帧2: A输入) ──────→│
     │                │                │
     │←─ PushFrames(帧3: 空帧) ───────→│  帧 3
     │                │                │
     │     ...持续推帧...               │
     │                │                │
     │←─ StopFrameSync ──────────────→│
     │                │                │
```

## 2. 心跳保活

```
  Client             Server
     │                  │
     │  (每 3 秒)        │
     ├─ Heartbeat ─────→│
     │←─ HeartbeatRsp ──┤  ← 重置超时计时器
     │                  │
     │  (3 秒后)         │
     ├─ Heartbeat ─────→│
     │←─ HeartbeatRsp ──┤
     │                  │
     │  (3 秒后)         │
     ├─ Heartbeat ─────→│
     │       ✕           │  ← 回复丢了 / 网络断了
     │                  │
     │  (再 3 秒)        │
     ├─ Heartbeat ─────→│
     │       ✕           │
     │                  │
     │  (累计 10 秒无回复) │
     │  判定断线！        │
     │  触发重连 ↓        │
```

## 3. 快速重连（断线 < 3 秒）

```
  Client             Server
     │                  │
     │  ══ 网络断开 ══   │
     │                  │
     │  ConnectionManager 检测到断线
     │  委托 QuickReconnectStrategy
     │                  │
     │  Session.LightReset()  ← 保留发送缓冲区
     │  Transport.Reconnect()
     │                  │
     ├─ [TCP 重建连接] ─→│
     │←─ [连接成功] ─────┤
     │                  │
     ├─ Reconnect(playerId=1) →│
     │←─ ReconnectRsp(帧号=150)┤
     │                  │
     │  Session.ResendUnacked()  ← 重发未确认的消息
     ├─ [重发 Seq=45] ──→│
     ├─ [重发 Seq=46] ──→│
     │                  │
     │  状态恢复: Syncing
     │  继续收帧...
     │←─ PushFrames(帧151) ─┤
     │←─ PushFrames(帧152) ─┤
```

## 4. 超时重连（快速重连失败后降级）

```
  Client             Server
     │                  │
     │  ══ 长时间断线 ══  │
     │                  │
     │  QuickReconnect 尝试 3 次全部失败
     │  降级到 SnapshotReconnectStrategy
     │                  │
     │  Session.FullReset()  ← 清空所有缓冲区和状态
     │  Transport.Connect()
     │                  │
     ├─ [TCP 新建连接] ─→│
     │←─ [连接成功] ─────┤
     │                  │
     ├─ Reconnect(playerId=1) →│
     │←─ ReconnectRsp(帧号=500, 快照数据) ┤
     │                  │
     │  加载快照，丢弃本地状态
     │  从帧 500 继续
     │                  │
     │←─ PushFrames(帧501) ─┤
     │←─ PushFrames(帧502) ─┤
```

## 5. CompositeStrategy 完整重连编排

```
  Client
     │
     │  断线检测
     │
     ├─ CompositeStrategy 开始
     │
     │  ┌─ QuickReconnect 第 1 次 ── 失败
     │  ├─ QuickReconnect 第 2 次 ── 失败
     │  ├─ QuickReconnect 第 3 次 ── 失败
     │  │
     │  │  快速重连耗尽，降级
     │  │
     │  ├─ SnapshotReconnect 第 1 次 ── 失败
     │  ├─ SnapshotReconnect 第 2 次 ── 成功！✓
     │  └─ 恢复连接
     │
     │  如果全部失败:
     │  └─ OnDisconnected 通知上层（弹窗/退出）
```

## 6. Tick 内部执行顺序

```
client.Tick(16ms)
  │
  ├─ ConnectionManager.Tick(16ms)
  │     │
  │     ├─ NetworkSession.Tick(16ms)
  │     │     │
  │     │     ├─ ITransport.Tick()
  │     │     │     └─ poll socket → OnData(bytes)
  │     │     │           └─ Framing.Feed(bytes)
  │     │     │                 └─ MessageCodec.Decode(frame)
  │     │     │                       └─ DispatchMessage(msg)
  │     │     │                             ├─ 匹配 PendingRequest → 回调
  │     │     │                             └─ OnMessage 事件
  │     │     │
  │     │     └─ CheckTimeouts(16ms)
  │     │           └─ 超时的 SendAsync → onTimeout 回调
  │     │
  │     └─ TickHeartbeat(16ms)
  │           ├─ 计时器到 → Send(Heartbeat)
  │           └─ 超时检测 → 触发断线
  │
  └─ (OnMessage 事件分发到 FrameSyncClient)
        ├─ PushFrames → OnFrame 回调 → 游戏逻辑执行
        ├─ StartFrameSync → 状态切换
        └─ HeartbeatRsp → 重置心跳计时器
```

## 7. 帧内嵌事件处理顺序

```
  Client A            Server            Client B
     │                  │                  │
     │   帧 100 tick     │                  │
     │                  │← FrameInput(B) ──┤  玩家 B 首次发包
     │                  │                  │
     │                  │  stepFrame():    │
     │                  │  pendingEvents = [PlayerJoined(B)]
     │                  │  FrameData = {   │
     │                  │    inputs: [...] │
     │                  │    events: [PlayerJoined(B)]
     │                  │  }               │
     │←── PushFrames(帧100) ─────────────→│
     │                  │                  │
     │  HandlePushFrames():               │  HandlePushFrames():
     │  1. DispatchFrameEvent(            │  1. DispatchFrameEvent(
     │       PlayerJoined(B))             │       PlayerJoined(B))
     │     → OnPlayerJoined(B)            │     → OnPlayerJoined(B)
     │     → InitPlayer(B, slot=1)        │     → InitPlayer(B, slot=1) ← 同帧同 slot ✓
     │  2. OnFrame(帧100)                 │  2. OnFrame(帧100)
     │     → Tick → ApplyInputs(B)        │     → Tick → ApplyInputs(B)
     │                  │                  │

注意：帧事件（步骤 1）必须在 OnFrame（步骤 2）之前处理，
      确保 InitPlayer 在 Tick 前执行，slot 对所有客户端一致。
```

## 8. 房主选举（HostChanged）

```
  Client A            Server            Client B
  (Host)               │             (Normal)
     │                  │                  │
     │  ══ 网络断开 ══   │                  │
     │                  │                  │
     │  (心跳超时 10s)   │                  │
     │                  │  PlayerOffline(A) 进入 pendingEvents
     │                  │                  │
     │                  │  选举逻辑:       │
     │                  │  在线玩家中 playerId 最小者 = B
     │                  │  hostPlayerId = B
     │                  │  HostChanged(B) 进入 pendingEvents
     │                  │                  │
     │                  │  stepFrame():    │
     │                  │  events = [PlayerOffline(A), HostChanged(B)]
     │                  ├── PushFrames ───→│
     │                  │                  │
     │                  │               HandlePushFrames():
     │                  │               1. OnPlayerOffline(A)
     │                  │               2. OnHostChanged(B)  → isHost = true
     │                  │               3. OnFrame
```
