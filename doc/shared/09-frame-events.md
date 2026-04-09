# 帧内嵌事件与房主管理

> **实现状态**：已完成（2026-03-28）

帧内嵌事件（Frame-Embedded Events）是 BoomNetwork 保证"玩家加入/离开/掉线"类事件具有**确定性**的核心机制。所有此类事件嵌入 `FrameData`，所有客户端在同一帧处理，不会出现因网络时序导致的状态分歧。

---

## 1. 为什么需要帧内嵌事件

不用帧内嵌事件的问题：

```
玩家 B 加入房间
  │
  ├── 客户端 A 的 ExtCmd 先到达 → A 在帧 100 处理 OnPlayerJoinedMsg
  └── 客户端 C 的 ExtCmd 后到达 → C 在帧 105 处理 OnPlayerJoinedMsg

结果：A 和 C 在不同帧 InitPlayer(B) → slot 分配偏移 → 帧 hash 不一致
```

帧内嵌事件的解决方案：

```
服务器在帧 100 的 FrameData 中嵌入 PlayerJoined(B)
  │
  ├── 客户端 A 在帧 100 处理 OnPlayerJoinedFrame(B) → 相同帧
  └── 客户端 C 在帧 100 处理 OnPlayerJoinedFrame(B) → 相同帧

结果：所有客户端同帧 InitPlayer(B) → slot 一致 → hash 不一致风险消除
```

---

## 2. 协议格式

`FrameData` 线格式（在 inputs 数组之后追加）：

```
[FrameNumber: 4B]
[InputCount: 1B]
  InputCount × [PlayerId: 4B][DataLen: 2B][Data: N bytes]
[EventCount: 1B]                             ← 新增
  EventCount × [EventType: 1B][PlayerId: 4B] ← 每条事件 5B
```

最大事件数：`MaxFrameEvents = 32`（防 OOM 上限）。

---

## 3. 事件类型

| 常量 | 值 | 触发时机 |
|------|----|---------|
| `PlayerJoined` | 1 | 玩家首次成功加入房间 |
| `PlayerLeft` | 2 | 玩家主动离开房间 |
| `PlayerOffline` | 3 | 玩家断线（心跳超时） |
| `PlayerOnline` | 4 | 玩家断线后重连成功 |
| `HostChanged` | 5 | 房主切换（掉线选举或主动转让） |

---

## 4. 双路径分发

服务器根据房间是否正在帧同步决定分发路径：

```
玩家事件（join/leave/offline/online/host-change）
        │
        ├── 房间正在运行（running=true）
        │     → 写入 room.pendingEvents[]
        │     → 下一个 stepFrame 时打包进 FrameData
        │     → 所有客户端同帧处理（确定性保证）
        │
        └── 房间未运行（running=false）
              → ExtCmd 立即广播（PlayerJoined/Left ExtCmd 20/21）
              → 客户端在 OnPlayerJoinedMsg/OnPlayerLeftMsg 回调中处理（非确定性，适合大厅阶段）
```

---

## 5. 客户端处理顺序

`HandlePushFrames` 中的分发顺序**严格固定**：

```csharp
// 1. 先处理帧内事件（可能修改游戏状态）
foreach (var evt in frame.Events)
    DispatchFrameEvent(evt);  // → OnPlayerJoinedFrame / OnPlayerLeftFrame / OnHostChanged

// 2. 再执行帧逻辑（依赖事件修改后的状态）
OnFrame?.Invoke(frame);
```

**为什么顺序重要**：游戏逻辑（如 `InitPlayer`）必须在帧事件中执行，OnFrame 内的 `Tick` 才能看到正确的玩家状态。如果顺序颠倒，新玩家的首帧输入会在 `InitPlayer` 之前处理，导致空 slot 访问。

---

## 6. 房主选举机制

### 规则

1. **首人即房主**：首个进入房间的玩家自动成为房主（`hostPlayerId = first player`）
2. **房主掉线**：从剩余在线玩家中选 playerId 最小者（稳定选举）
3. **全员掉线后首个重连**：首个成功重连的玩家成为新房主
4. **HostChanged 事件**：每次房主切换，服务器通过帧内嵌 `HostChanged` 事件广播新房主 id

### 客户端 API

```csharp
// 订阅房主变更
client.OnHostChanged += (newHostPlayerId) => {
    _isHost = (newHostPlayerId == client.PlayerId);
    UpdateHostUI();
};

// 判断自己是否为房主（用于 RequestStart / UI 按钮控制）
bool isHost = client.IsHost;  // 框架维护，基于 OnHostChanged
```

### 典型用法

```csharp
// 只有房主才能启动帧同步
if (client.IsHost)
    client.RequestStart();

// 房主变更时更新 UI
client.OnHostChanged += newHostId => {
    startButton.interactable = (newHostId == client.PlayerId);
};
```

---

## 7. 与 Desync 防御的关系

帧内嵌事件直接解决了 **C4**（分配型函数双重职责）和相关 slot 分配不同步问题：

| 风险 | 原因 | 帧内嵌事件的修复 |
|------|------|----------------|
| `InitPlayer` 在不同帧调用 | ExtCmd 到达时序不同 | 帧内嵌 PlayerJoined → 同帧触发 |
| slot 分配顺序不一致 | 各客户端 OnFrameSyncStart 顺序不同 | ApplyInputs auto-init + 首帧输入保证 |
| Host UI 跳变 | ExtCmd 在不同客户端帧时触发 | HostChanged 嵌入帧 → 同帧切换 |

---

## 8. 参考

- 协议格式：[06-protocol-cmd-tiers.md](06-protocol-cmd-tiers.md)
- 不同步防御：skill `bn-desync`（C4 根因 + 18 项 Checklist）
- 服务器实现：`svr/framesync/room.go`（`pendingEvents`、`stepFrame`、`hostPlayerId`）
- 客户端实现：`cli/Client/FrameSync/FrameSyncClient.cs`（`HandlePushFrames`、`OnHostChanged`）
