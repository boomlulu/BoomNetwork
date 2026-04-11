# DuplicateFrame 修复方案（v2 — 基于源码排查）

**Date:** 2026-04-11  
**Status:** Proposed → 待执行

---

## 一、根因

**一行代码 bug：** `FrameSyncClient.HandleReconnected` 第 621 行。

```csharp
// FrameSyncClient.cs line 619-627（else 分支，快速重连路径）
else
{
    LastFrameNumber = context.ServerFrameNumber;  // ← BUG
    _lastSnapshotFrame = 0;
}
```

快速重连时，客户端将 `LastFrameNumber` 推进到服务器当前帧号（如 100），
但服务端 `deliveryLoop` 阶段 1 随即从 `lastFrame+1`（如 81）开始补帧。
帧 81 到达客户端时 `81 <= 100`，触发 `HandlePushFrames` 的 DuplicateFrame 判定。

**不是服务端 bug。** 服务端的 `deliveryLoop` 两阶段串行 + `PlayerReplaying` 状态过滤 + `cursor` 去重设计完全正确。

---

## 二、触发时序

假设客户端断线前 `LastFrameNumber = 80`，服务器当前帧 = 100。

```
服务端                                    客户端
──────                                    ──────
收到 Reconnect(pid, lastFrame=80)

GetReconnectSnapshot() → frame=100

sendMsg(conn, ReconnectRsp(               收到 ReconnectRsp:
  Success, serverFrame=100))                QuickReconnectStrategy.onResponse:
                                              context.ServerFrameNumber = 100
                                              context.IsSnapshotRestore = false
                                            HandleReconnected else 分支:
                                              LastFrameNumber = 100  ★

AddPlayer(replaying=true, cursor=80)
→ go deliveryLoop()

deliveryLoop 阶段1:
  GetFramesSince(80)
  → Send(PushFrames, frame=81)            HandlePushFrames:
                                            frame=81, LastFrameNumber=100
                                            81 <= 100 → DuplicateFrame! ★
  → Send(PushFrames, frame=82)            (已停止帧同步，不再处理)
  ...全部丢失
```

**每次快速重连必触发。** 只要 `lastFrame < serverFrame`（正常情况总是成立）。

---

## 三、为什么快照重连不受影响

快照重连走 `if (context.IsSnapshotRestore)` 分支：

```csharp
LastFrameNumber = context.SnapshotFrame;  // 如 50
```

服务端从 `snapshotFrame+1`（51）开始补帧，`51 > 50`，不触发 DuplicateFrame。

---

## 四、为什么服务端不需要改

逐层验证：

| 机制 | 源码位置 | 结论 |
|------|----------|------|
| `stepFrame` 只投递 `PlayerOnline` | `room_frame.go:210` `p.State == PlayerOnline` | ✓ `PlayerReplaying` 被跳过 |
| `deliveryLoop` 阶段 2 帧去重 | `room_frame.go:288` `cf.FrameNumber <= p.cursor` | ✓ 阶段 1 已发的帧被跳过 |
| `AddPlayer` 取消旧 loop | `room_player.go:48` `existing.cancelFn()` | ✓ 旧 deliveryLoop 被终止 |
| `SetPlayerLive` 在阶段 1 结束后 | `room_frame.go:277` | ✓ 追帧完才接收 live 帧 |
| `handleReconnect` 先发 Rsp 再启 loop | `main.go:630` vs `main.go:651` | ✓ TCP 保序，Rsp 先到客户端 |
| `Conn.Send` 有 mutex | `tcp_server.go:27` `c.mu.Lock()` | ✓ 无并发写 |

---

## 五、修改方案

### 5.1 修改内容（仅客户端，1 处）

**文件：**
```
com.boom.boomnetwork@.../Runtime/Client/FrameSync/FrameSyncClient.cs
```

**修改前（第 619-627 行）：**

```csharp
else
{
    LastFrameNumber = context.ServerFrameNumber;
    _lastSnapshotFrame = 0;
}
```

**修改后：**

```csharp
else
{
    // 快速重连: deliveryLoop 阶段 1 会从 LastFrameNumber+1 开始补帧,
    // 不能将 LastFrameNumber 推进到 ServerFrameNumber, 否则补帧全部被判为 DuplicateFrame.
    // LastFrameNumber 保持断线前的值 (= 发给服务器的 lastFrame), 让补帧自然推进.
    //
    // 快照重连无快照数据: deliveryLoop 从当前帧开始推送 live 帧,
    // LastFrameNumber 保持断线前的值同样安全 (live 帧号 > 断线前帧号).
    _lastSnapshotFrame = 0;
}
```

**改动量：删除 1 行，添加 5 行注释。**

### 5.2 不需要修改的文件

| 文件 | 原因 |
|------|------|
| Go 服务端全部代码 | 服务端逻辑正确，无需改动 |
| `HandlePushFrames` DuplicateFrame 判定 | 保留 fatal 级别，作为帧序不变量的最后防线 |
| `QuickReconnectStrategy.cs` | 不涉及 LastFrameNumber |
| `SnapshotReconnectStrategy.cs` | 走 if 分支，不经过 else |
| `ConnectionManager.cs` | 不涉及帧处理 |

---

## 六、正确性论证

### 6.1 快速重连（`lastFrame > 0`，`IsSnapshotRestore = false`）

- 修改后 `LastFrameNumber` 保持断线前的值（= `lastFrame`，如 80）
- 服务端 `deliveryLoop` 阶段 1 从帧 81 开始补帧
- `81 > 80` → 通过 DuplicateFrame 检查 ✓
- 补帧依次推进 `LastFrameNumber` 到 81, 82, ..., 100
- 阶段 1 结束 → `SetPlayerLive` → 阶段 2 接收 live 帧 101, 102, ...
- 帧号连续，无间隙，无重复 ✓

### 6.2 快照重连有快照（`IsSnapshotRestore = true`）

- 走 `if` 分支，不受本次修改影响
- `LastFrameNumber = SnapshotFrame`（如 50）
- 补帧从 51 开始 → `51 > 50` ✓

### 6.3 快照重连无快照数据（`IsSnapshotRestore = false`，`lastFrame = 0`）

- 服务端 `handleReconnect`：`replayFrom = 0`，`AddPlayer(replaying=false, cursor=0)`
- `deliveryLoop` 跳过阶段 1（`cursor == currentFrame` 或 cursor=0 < currentFrame → 追帧从 frameRing 读取，但 cursor=0 不在 ring 中）
- 实际上 `replaying = (0 > 0) = false`，所以 `State = PlayerOnline`，直接进入阶段 2 接收 live 帧
- 修改后 `LastFrameNumber` 保持断线前的值
- live 帧号 > 断线前帧号（服务器帧号在断线期间持续推进） ✓

### 6.4 `HandleDisconnected` 不重置 `LastFrameNumber`

```csharp
private void HandleDisconnected()  // line 593
{
    _frameSyncStarted = false;   // ← 只重置这些
    IsGamePaused = false;
    // ...
    // 注意: LastFrameNumber 不被重置，保留断线前的值
}
```

这是修复成立的前提条件，已确认 ✓。

---

## 七、验收测试

### TC-1: 快速重连 — 核心场景

| 项 | 内容 |
|----|------|
| **前置** | 2 名玩家帧同步中，帧号 > 200 |
| **操作** | 玩家 A 调用 `SimulateNetworkDrop()`，等待自动重连 |
| **预期** | 重连成功，无 `[FATAL] DuplicateFrame`，帧号从断线前的值连续递增 |
| **验证** | 客户端日志：1) `QuickReconnect` 成功 2) 无 DuplicateFrame 3) OnFrame 回调的帧号连续 |

### TC-2: 快速重连 — 补帧完整性

| 项 | 内容 |
|----|------|
| **前置** | 同 TC-1 |
| **操作** | 断线前记录 `LastFrameNumber = X`，重连后检查 OnFrame 收到的第一帧 |
| **预期** | 第一帧 = X+1（无间隙），最后一帧追上当前服务器帧号 |
| **验证** | 在 `OnFrame` 回调中打印帧号，验证从 X+1 开始连续递增 |

### TC-3: 快照重连 — 无回归

| 项 | 内容 |
|----|------|
| **前置** | 2 名玩家帧同步中，帧号 > 200 |
| **操作** | `SimulateNetworkDropAndPause()` → 等 15 秒 → `ResumeReconnect()` |
| **预期** | 走 SnapshotReconnect 路径，`Snapshot restored` 日志出现，帧号从快照帧连续递增 |
| **验证** | 不受此修改影响（走 if 分支），验证无回归 |

### TC-4: 高频快速重连 — 无累积错误

| 项 | 内容 |
|----|------|
| **前置** | 2 名玩家帧同步中 |
| **操作** | 循环 10 次：`SimulateNetworkDrop()` → 等重连成功 → 等 3 秒 |
| **预期** | 每次重连均成功，帧号单调递增，无任何 DuplicateFrame |

### TC-5: 正常游戏 — 无回归

| 项 | 内容 |
|----|------|
| **前置** | 2 名玩家加入房间，帧同步运行 |
| **操作** | 持续移动 60 秒，不断线 |
| **预期** | 无 DuplicateFrame，帧号连续 |

### TC-6: 自动化测试

```bash
cd /Users/boom/Demo/BoomNetwork && ./test.sh
```

全量测试通过，无回归。

---

## 八、执行 Checklist

- [ ] `FrameSyncClient.cs` 第 621 行删除 `LastFrameNumber = context.ServerFrameNumber;`
- [ ] 添加注释说明为什么不设置
- [ ] 运行 TC-1 ~ TC-5 手工验证
- [ ] 运行 `./test.sh` 自动化验证
- [ ] 运行 `./bench.sh` 确认无性能回归
