# DuplicateFrame 根因分析 v3

**Date:** 2026-04-14  
**Status:** 已确认根因，待执行修复  
**Error:** `[4003 DuplicateFrame] frame=101 lastFrame=399`

> v2 文档（`fix-duplicate-frame-plan.md`）描述的是已修复的第一个 bug（`HandleReconnected` else 分支误推 `LastFrameNumber`）。  
> 本文档描述的是同一错误码的**第二个根因**，两者叠加才能复现 frame=101 lastFrame=399 的场景。

---

## 一、日志数据（两玩家同时断线场景）

```
[1776081520779][P1][FrameSync] FrameSync started (Init). seed=0x..., frameInterval=50ms (20fps)
[1776081521885][P2][FrameSync] P2 joined same room
[1776081525830][P1][Upload]   snapshot upload rejected: server has newer (frame=100)
  → 服务器已存有 frame=100 快照，snapshotStaleFrames 重置为 0

[1776081530806][P1][Conn]     TCP disconnect at frame=200
[1776081530807][P2][Conn]     TCP disconnect at frame=200
[1776081530807][P2][Recon]    QuickReconnect starts (frame=200)

[1776081540778][P2][FrameSync] FrameSyncPaused reason=SnapshotStale
  → P2 QuickReconnect attempt-3 已建连，收到服务端广播
  → 此时服务器 frameNumber ≈ 400，snapshotStaleFrames=300 触发暂停

[1776081544561][P1][Recon]    QuickReconnect starts (frame=200)
[1776081545275][P1][Session]  SendFailed ×70（旧 socket，新连接建立前）

[1776081545872][P2][Recon]    → SnapshotReconnect begins (chain=1/2 attempt=1/1)
[1776081545935][P2][FrameSync] [FATAL] DuplicateFrame: frame=101 lastFrame=399
  → SnapshotReconnect 开始后仅 63ms

[1776081555297][P1][Recon]    → SnapshotReconnect begins
[1776081555356][P1][FrameSync] [FATAL] DuplicateFrame: frame=101 lastFrame=399

[1776081565912][P2][Recon]    AllStrategiesExhausted
[1776081575344][P1][Recon]    AllStrategiesExhausted
```

**关键观测：**

| 观测点 | 数据 |
|--------|------|
| 两玩家断线帧 | 均为 frame=200 |
| DuplicateFrame 中的 lastFrame | 均为 **399** |
| DuplicateFrame 中的 frame | 均为 **101** |
| SnapshotReconnect 到 DuplicateFrame 间隔 | **63ms**（P2）/ 59ms（P1） |
| SnapshotStale 触发时刻 | t+10s，server frame≈400 |
| 服务器快照帧 | frame=100 |

---

## 二、因果链

### 前置知识

```
客户端 session.SendAsync(CmdReconnect, seq=N, onResponse, onTimeout)

// NetworkSession.DispatchMessage (line 430)
if (msg.HasSeq && _pendingRequests.TryComplete(msg.Seq, out pending))
{
    pending.OnResponse?.Invoke(msg);   // ← 只有 HasSeq=true 且 seq 匹配才触发
    return;
}
OnMessage?.Invoke(msg);               // ← HasSeq=false 走这里 → HandleMessage
// HandleMessage 没有 CmdReconnectRsp=11 的 case → 静默丢弃
```

### Step 1：TCP 断线后 HandleDisconnected 不立即触发

`FrameSyncClient.HandleDisconnected` 订阅的是 `CM.OnDisconnected`。  
TCP 断线时，CM 进入 `Reconnecting` 状态并开始策略链，**`CM.OnDisconnected` 只在所有策略耗尽后触发**（[ConnectionManager.cs:305-311](unity/com.boom.boomnetwork/Runtime/Client/Connection/ConnectionManager.cs#L305)）。

```
TCP 断线 at frame=200
  → CM.HandleSessionDisconnected → state=Reconnecting → Attempt()
  → FrameSyncClient.HandleDisconnected 未调用
  → _frameSyncStarted 仍为 TRUE
  → LastFrameNumber 仍为 200
```

### Step 2：服务端 handleReconnect 成功响应不回显 seq

**`handleJoinRoom`（正确）：**
```go
// main.go:742-746
joinRsp.HasSeq = msg.HasSeq   // ← 回显 seq
joinRsp.Seq = msg.Seq
_ = sendMsg(conn, joinRsp)
```

**`handleReconnect`（缺失）：**
```go
// main.go:630  ← BUG
_ = sendMsg(conn, codec.NewCoreMessage(framesync.CmdReconnectRsp, rsp))
// HasSeq=false, Seq=0，客户端无法匹配 pending request
return nil  // router 收到 nil，不再发任何带 seq 的响应
```

失败路径（BufferStale/S2CBufStale）走 `return codec.NewCoreMessage(...)` → router 在 [main.go:200-201](svr/cmd/framesync/main.go#L200) 自动回显 seq → 客户端 `onResponse` 触发 → `onFail`。

**结论：成功永远超时（5s/10s），失败立刻通知。**

### Step 3：QuickReconnect × 3 期间帧被正常处理，LastFrameNumber 野增

每次 QuickReconnect attempt 连上服务器后：
- 服务端正常启动 delivery loop，从 cursor=lastFrame 推帧
- 客户端 `_frameSyncStarted=true`，`HandlePushFrames` 正常处理，`LastFrameNumber` 随帧递增
- 5s 后 `onTimeout` → `onFail`，`LightReset()` 关旧连接，开始下一次 attempt
- 每次 attempt 的 `_activeState.LastFrameNumber` 被 `UpdateFrameNumber()` 实时同步，下次 attempt 带更新后的 lastFrame

```
attempt-1 [t=0~5s]:    lastFrame=200 → 推帧 201~N₁ → LastFrameNumber=N₁
attempt-2 [t=5~10s]:   lastFrame=N₁  → 推帧 N₁+1~N₂ → LastFrameNumber=N₂
attempt-3 [t=10~15s]:  lastFrame=N₂  → 推帧 N₂+1~399
  t=10s: stepFrame 触发 SnapshotStale（snapshotStaleFrames=300=100×3），
         frameNumber 冻结在 399（frame 400 的 tick 直接 return，未递增）
  delivery loop 阶段 1 推到 frame 399 后阶段 2 阻塞（无新帧）
  LastFrameNumber = 399
  onTimeout → onFail → chain=1 SnapshotReconnect
```

### Step 4：SnapshotReconnect 送来 frame=101，LastFrameNumber=399 → DuplicateFrame

```
SnapshotReconnect.Attempt():
  session.FullReset()      ← 清 session buffer，但不碰 _frameSyncStarted / LastFrameNumber
  session.Connect()

  onConnected → SendAsync(CmdReconnect, lastFrame=0)
    服务端 handleReconnect:
      snapshotFrame=100, snapshotData=<data>
      sendMsg(conn, CmdReconnectRsp) ← HasSeq=false，客户端丢弃
      AddPlayer(replaying=true, cursor=100) → delivery loop 从 101 推帧

  客户端：
    CmdReconnectRsp 到达 → HasSeq=false → OnMessage → HandleMessage → 无 case → 丢弃
    HandleReconnected 未调用
    _frameSyncStarted = TRUE（未被重置）
    LastFrameNumber = 399（未被重置）

    frame=101 到达 → HandlePushFrames:
      _frameSyncStarted=true ✓
      101 <= 399 → DuplicateFrame! ★
```

整个过程在 SnapshotReconnect 连上后 **<100ms** 内发生（TCP 握手 + 帧到达），与日志中的 63ms 完全一致。

### 为什么两玩家都是 lastFrame=399

两玩家同时断线，经历相同的 QuickReconnect × 3 时序（都在 t≈10s 遇到 SnapshotStale），因此 LastFrameNumber 同时停在同一帧（399 = SnapshotStale 前最后一帧），形成完全对称的结果。

---

## 三、修复方案

### 根因所在文件

```
svr/cmd/framesync/main.go  line 630
```

### 修改内容（1 处，对齐 handleJoinRoom 的 P2-3 模式）

```go
// 修改前（line 630）：
_ = sendMsg(conn, codec.NewCoreMessage(framesync.CmdReconnectRsp, rsp))

// 修改后：
reconnectRspMsg := codec.NewCoreMessage(framesync.CmdReconnectRsp, rsp)
reconnectRspMsg.HasSeq = msg.HasSeq   // ← 回显客户端请求的 seq
reconnectRspMsg.Seq = msg.Seq
_ = sendMsg(conn, reconnectRspMsg)
```

**注释说明（参考 handleJoinRoom:742 注释风格）：**
```go
// 先通过 sendMsg 直接发送 ReconnectRsp（附上原始 Seq，确保客户端 onResponse 触发），
// 再通过 AddPlayer 启动 delivery loop。TCP 保序，Rsp 必先于帧数据到达客户端。
```

### 为什么是这一处，而不是其他地方

| 方案 | 评价 |
|------|------|
| 服务端 sendMsg 回显 seq（本方案）| ✓ 根因修复，1 行改动，对齐 handleJoinRoom 已有模式 |
| 客户端 HandleMessage 增加 ReconnectRsp case | ✗ 绕过根因，需在 FrameSyncClient 写复杂状态恢复逻辑 |
| 客户端 HandleDisconnected 在 TCP 断线时立即调用 | ✗ 改变重连语义，HandleDisconnected 会重置 _frameSyncStarted=false，导致断线期间帧丢失 |
| handler return 替代手动 sendMsg | ✗ 破坏顺序保证：AddPlayer 在 handler return 前已 go deliveryLoop，router 响应反而晚于帧数据 |

### 修复后效果

```
服务端 handleReconnect 成功路径：
  reconnectRspMsg.HasSeq = true, Seq = N  ← 新增
  sendMsg(conn, reconnectRspMsg)           ← Rsp 先到，TCP 保序
  AddPlayer → go deliveryLoop(cursor=100)

客户端：
  CmdReconnectRsp 到达 → TryComplete(seq=N) 匹配 → onResponse 触发
  → onSuccess(ReconnectOutcome(serverFrame, isSnapshotRestore=true, snapshotFrame=100))
  → HandleReconnected:
      OnLoadSnapshot(snapshotData)
      LastFrameNumber = 100   ← 正确重置
      _lastSnapshotFrame = 100
      _frameSyncStarted = true

  frame=101 到达 → HandlePushFrames:
      101 > 100 → 正常推进 ✓
```

---

## 四、影响范围

- 仅改 `svr/cmd/framesync/main.go` 1 处
- 客户端（Unity / CLI）无需改动
- 所有已有重连测试（Go server tests）不受影响（测试走 handler return 路径，不走手动 sendMsg）
- `handleJoinRoom` 同类修复已经存在，本改动对齐既有模式

---

## 五、验收标准

1. 双人联机场景：断线重连后帧号从断线点连续递增，无 `DuplicateFrame` 日志
2. `[FrameSync] Reconnected (serverFrame=..., snapshot=True)` 日志出现（证明 HandleReconnected 被正确调用）
3. `./test.sh` 全部通过
