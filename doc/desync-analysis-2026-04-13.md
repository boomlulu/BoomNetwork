# VS Desync 分析报告（多方会谈用）

**Date:** 2026-04-13  
**Status:** 信息收集阶段（原始分析）；重连 bug 已于 2026-04-13 修复（见下方附录）  
**日志来源:** 用户提供的 Player 2（Pid=2）日志，约 200 帧

---

## 一、已知事实（来自日志 + 代码）

### 1.1 本次会话状态

| 项目 | 值 |
|---|---|
| 游戏 | VampireSurvivors Demo，多人联机 |
| 服务器 | 腾讯云 124.220.6.174:9000 |
| Player 2 dt.Raw | 51（= `FInt.FromInt(50)/FInt.FromInt(1000)` 整数截断） |
| Player 2 fps | 20（= `(FInt.One/dt).Raw >> 10 = 20560 >> 10 = 20`） |
| Player 2 targetFrames | 400（= 20 × 20s） |
| seed | 0x8557300C |
| startTime | 1776058576908 |
| Wave 1 开始帧 | 46，WaveRemaining=5620，batchSize=15 |
| Snapshot 上传 | frame=100 被拒（Server 已有 P1 的更新快照，正常）|
| 断线重连 | frame ≈ 200+ 时 Connected → Reconnecting |
| 不同步事件 | **0 条**（report server `/desync/groups` 空，无 pending_desync.jsonl） |

### 1.2 初始化时序（Player 2 视角）

```
Frame  6: HasAlivePlayers false→true（P1 通过 ApplyInputs 初始化 slot 0）
Frame 28: LocalSlot 解析，Pid=2 → slot 1（懒解析，OnPlayerJoined 已在更早帧触发）
Frame 46: Wave 1 启动，RngState=0x8557300C（与 seed 相同，说明 Init 清零了 RngState）
```

**关键点：** P1 的初始化是通过 `ApplyInputs` 触发（P1 在 frame 1-5 Silent When Idle，frame 6 才第一次移动），**没有** P1 的 `OnPlayerJoined` 日志。

---

## 二、检测层分析

### 2.1 HashThrottleMs = 0

```csharp
// FrameSyncClient.cs:206
public float HashThrottleMs = 0f;
```

**→ 每帧都会发送 hash，无节流问题。**

### 2.2 IsGamePaused 断线后重置

```csharp
// HandleDisconnected():596
IsGamePaused = false;
```

**→ 断线重连后 IsGamePaused = false，SendFrameHash 不会被错误跳过。**

### 2.3 broadcastToRoom 广播给全体

```go
// main.go:handleFrameHash
broadcastToRoom(room, -1, codec.NewExtMessage(framesync.ExtCmdFrameHashMismatch, mismatchData))
```

**→ 双端都会收到 mismatch 通知，都会触发 OnDesync，都会上传 report。**

### 2.4 结论：GameState hash 在本次测试中从未不同步

因为 report server 为空、pending_desync.jsonl 不存在，说明**服务器检测到的帧 hash 从未出现不一致**。有两种解释：

- **A** — GameState 实际上是同步的，但视觉表现不同（渲染层问题）
- **B** — GameState 已不同步，但 hash 对比没有发生（一端断线太快，server 只收到一端的 hash）

---

## 三、代码层潜在 Bug

### 3.1 🔴 `_snapshotLoaded` 从不重置（高风险）

```csharp
// VSNetworkManager.cs
bool _snapshotLoaded;  // 初始 false

void LoadSnapshot(byte[] data)
{
    _snapshotLoaded = true;   // ← 设置后从未重置
    VSSnapshot.Deserialize(data, _sim);
}

void OnFrameSyncStop()
{
    _syncing = false;
    // ← _snapshotLoaded 没有重置！
}

void OnFrameSyncStart(FrameSyncInitData init)
{
    if (!_snapshotLoaded)
        _sim.Init(dt, seed);   // 仅在 _snapshotLoaded=false 时初始化
    else
        _sim.State.Dt = dt;    // 快照恢复路径：只更新 Dt，其余状态来自快照
}
```

**触发条件：**
1. 第 N 局游戏中，玩家触发重连 + 快照恢复 → `_snapshotLoaded = true`
2. 第 N 局结束（`OnFrameSyncStop`）
3. 开第 N+1 局 → `HandleStartFrameSync` → `OnFrameSyncStart` → `_snapshotLoaded = true` → **`_sim.Init()` 被跳过**
4. 第 N+1 局用上一局快照状态跑，seed 不同、WaveNumber 不同 → **从第 1 帧开始脱节**

**验证方式：** 是否能复现"连续两局，第二局从开始就不同步"？

### 3.2 🟡 `_firstInputSent` 断线重连后不重置（低风险）

```csharp
bool _firstInputSent;  // 初始 false，从未在 Reconnect 后重置
```

**影响：** 重连后不会强制发送第一个空 input 来触发 `ApplyInputs` 初始化。  
**为何低风险：** 玩家已通过 `OnPlayerJoined` 帧事件初始化，slot 映射已建立。  
**边缘情况：** 如果重连后快照状态中没有该玩家的 slot 映射（快照来自玩家加入之前），可能出问题。

### 3.3 🟡 P1 缺少 `PlayerJoined` 帧事件

从日志可见，P2 客户端只收到了自己的 `OnPlayerJoined`，没有 `Player 1 joined via frame event`。

**P1 初始化路径：** `ApplyInputs`（P1 发输入 → `PidToSlot(1) = 0` → `InitPlayer(0)`）

**潜在问题：** 如果 P1 在 frame 1-5 完全没有发输入，且没有 `PlayerJoined(1)` 帧事件，则 P2 的客户端在这些帧上看到 `HasAlivePlayers = false`。  
这与 P1 自己的客户端（P1 从 frame 1 就初始化了）视角不同 → WaveSystem 的 `WaveSpawnTimer` 递减时机不同！

**但：** 日志显示双端实际上都在 frame 6 才初始化 P1（因为 P1 在 1-5 帧没动，没有 input 进帧数据），所以 WaveSystem 在双端都从 frame 6 才开始计时。**这是确定性的。**

---

## 四、缺失信息（需要补充才能定位根因）

| 缺失项 | 重要性 | 获取方式 |
|---|---|---|
| Player 1 的完整日志 | ⭐⭐⭐ | 运行时开启，对比 seed/dt.Raw/wave start 帧号 |
| 重连之后的日志（两端） | ⭐⭐⭐ | 截取 Connected→Reconnecting 到 Desync/StopSync 的日志 |
| "不同步"的视觉表现 | ⭐⭐ | 具体描述：是敌人位置不同？玩家位置不同？还是游戏逻辑不同？ |
| 复现是否需要重连 | ⭐⭐ | 如果不重连也不同步 → 根因在初始化；重连才不同步 → 根因在恢复路径 |
| 两次测试之间是否重启 Unity | ⭐⭐ | 验证 `_snapshotLoaded` bug（不重启 → 继承上局状态）|
| 服务器端日志（frame 1-50） | ⭐ | 确认 P1/P2 的 FrameHash 是否一致 |

---

## 五、核心疑问（多方讨论切入点）

### Q1：不同步在重连前就存在，还是重连后才出现？

- **如果重连前就不同步：** hash 检测应该已经发现（发了 ~200 帧的 hash，服务器比对了）。但 report server 空 → 说明重连前 hash 是一致的。
- **如果重连后才出现：** 需要重连后的日志和 desync report。

### Q2：用户看到的"不同步"是什么？

- P1 和 P2 屏幕上的敌人位置不同 → GameState 不同步（hash 应该会检测到）
- 两端 FPS/jitter 不同 → 不是 desync，是渲染抖动
- 升级UI 弹出时机不同 → 可能是 WaveSystem 不同步或 XP 计算不同步

### Q3：每次测试前是否重启 Unity？

- 不重启 → `_snapshotLoaded` 可能从上局残留，复现 Bug 3.1
- 每次重启 → Bug 3.1 不会触发（`_snapshotLoaded` 从 false 开始）

---

## 六、代码确定性审计结果

| 子系统 | 审计状态 | 风险 |
|---|---|---|
| WaveSystem.Tick | ✅ 纯 FInt | 无 |
| EnemySystem.Tick | ✅ 纯 FInt，`FindNearestPlayer` 按 slot 顺序遍历 | 无 |
| WeaponSystem（未审计） | ❓ | 需检查 |
| CollisionSystem（未审计） | ❓ | 需检查 |
| VSSnapshot 序列化 | ✅ Raw 字段逐一保存 | 无 |
| Pid→Slot 映射 | ✅ 快照中包含，懒初始化 + 帧事件双保险 | 无 |
| `state.Dt.Raw` | ✅ 快照保存，初始化/恢复均正确 | 无 |

---

## 七、可立即排查的操作

1. **在 P1 端开启完整日志**：`VSLog.Enabled = VSLog.Channel.DiagWave` 已有，收集 P1 的 seed/dt.Raw/wave start 帧号
2. **对比两端 frame=6 的 HasAlivePlayers 日志**：确认 P1 初始化帧号相同
3. **对比两端 frame=46 的 WaveNum/WaveRemaining/batchSize**：确认 Wave 启动参数相同
4. **复现时保持 Unity 不重启，测试两局**：验证 Bug 3.1（`_snapshotLoaded` 残留）
5. **在 GM 工具 Rooms Tab 中检查 `hash_diverge_count`**（如有），确认服务器侧实际检测状态

---

## 附录：2026-04-13 重连 Bug 修复记录

本次分析触发了对重连系统的深度排查，发现并修复了两个独立 bug，另做一次架构重构。

### Fix 1 — `session_store.go` byPlayer 提前删除 (commit 85b59ba)

**根因：** `Disconnect()` 在 TCP 关闭时调用 `delete(s.byPlayer, playerId)`，导致 `handleReconnect` 调用 `ByPlayer()` 时找不到 session（`!ok`），所有重连 attempt 瞬间以 `AllStrategiesExhausted` 失败。

**修复：** 移除 `delete(s.byPlayer, playerId)`。`byPlayer` 清理改由 `sessions.Reconnect()`（成功重连后替换）或 Reconciler `DeleteByPlayer()`（超时清理）负责。

### Fix 2 — `ConnectionManager.cs` DuplicateFrame 跨 attempt 帧号 (commit 8acbc5c)

**根因：** `ReconnectContext.LastFrameNumber` 在断线时快照后不更新。attempt 1 补帧推进到帧 301，attempt 2 仍用快照值 201，服务器重放 201→301，客户端报 `DuplicateFrame` 致命错误。

**修复：** 添加 `_activeContext` 字段，在 `UpdateFrameNumber()` 中同步写入，保证 attempt 2 使用最新帧号。

### Refactor — ReconnectContext → ReconnectState + ReconnectOutcome (commit 5a26a9d)

Fix 2 的临时 patch 被架构化：`ReconnectContext` 拆分为两个职责清晰的类型：
- `ReconnectState` class：CM 独占持有，`LastFrameNumber` 每帧实时同步，策略只读
- `ReconnectOutcome` readonly struct：策略成功时通过 `onSuccess(outcome)` 回传，纯输出

**注：** 计划文档中命名为 `ReconnectResult`，实际因命名冲突改为 `ReconnectOutcome`。
