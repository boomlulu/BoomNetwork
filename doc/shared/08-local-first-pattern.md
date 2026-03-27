# 08 — Local-First Pattern (本地优先模式)

> 设计红线 #4: 自己的角色永远在本地立即执行，零等待。

## 定义

Local-First 是 BoomNetwork 帧同步框架的核心交互模式：

1. **玩家输入 → 本地立即执行**（不等网络往返）
2. **输入通过 `SendInput()` 上报服务器**
3. **服务器 `PushFrames` 回推包含该输入的权威帧**
4. **`OnFrame` 回调中幂等确认**（对自己的输入做 no-op，对他人的输入正常执行）

延迟体验 = 0 RTT（自己的操作），只有远程玩家的操作需要等网络。

## 为什么不需要回滚

BoomNetwork 遵循**自主权威**原则：每个玩家对自己的角色拥有绝对权威。

- 本地立即执行的结果 **就是** 权威结果
- 服务器的 `PushFrames` 是**幂等确认**，不是纠正
- 没有冲突 → 没有回滚 → 没有预测/回滚子系统

这与传统 CS 架构的 Client-Side Prediction + Server Reconciliation 有本质区别：
传统架构服务器是唯一权威，客户端预测可能被服务器否决。BoomNetwork 中客户端就是自己角色的权威。

## OnFrame 幂等合约

`OnFrame` 回调**必须**对已经本地执行过的输入做 no-op：

```csharp
// 推荐写法：帧号去重
private uint _lastAppliedFrame;

void OnFrame(FrameData frame)
{
    foreach (var input in frame.Inputs)
    {
        if (input.PlayerId == MyPlayerId)
        {
            // 自己的输入已在本地执行过 → 跳过
            // （如果需要校验可以在这里对比状态）
            continue;
        }
        // 远程玩家的输入 → 正常执行
        ApplyRemoteInput(input);
    }
}
```

关键：远程玩家的输入 **只在 OnFrame 中执行**，不做本地预测。

## 时序图

```
时间轴 →

本地玩家                    服务器                     远程玩家
    |                          |                          |
    | [按下跳跃]               |                          |
    | 本地立即执行 Jump()      |                          |
    | SendInput(jumpData) ──→  |                          |
    |                          | 收集输入, 组帧            |
    |                          | PushFrames ──────────→   |
    |                    ←──── | PushFrames                |
    | OnFrame:                 |                          | OnFrame:
    |   自己的 input → skip    |                          |   远程 input → Jump()
    |   远程 input → apply     |                          |   自己 input → skip
    |                          |                          |
```

## 适用范围

### 适用 Local-First 的操作（自己角色的权威范围）
- 移动、跳跃、旋转
- 开火（本地表现：枪口火焰、弹壳）
- 方块放置/破坏（MinecraftDemo: 本地立即修改 chunk）
- 技能释放（本地播放动画、特效）

### 不适用 Local-First 的操作（需要服务器权威）
- 伤害判定 — 伤害由被击中方或服务器裁决
- 道具掉落/拾取 — 涉及多人竞争
- 房间状态变更 — 服务器广播
- 分数/排行 — 服务器维护

错误示范：对伤害做 Local-First 会导致"自己看到打中了但服务器判定没中"的不一致体验。

## 快照与 Local-First 的关系

`OnTakeSnapshot` 捕获的是**已经 Local-First 执行过的状态**。
快照上传后用于断线重连和后加入玩家的状态恢复。

因此：
- 快照反映的是本地执行后的真实状态（不是"等待确认的状态"）
- 加载快照后不需要"追赶确认"步骤
- 这也是为什么 BoomNetwork 不需要 SnapshotBuffer / InputBuffer 等预测子系统

## 参考

- [04-core-philosophy.md](04-core-philosophy.md) — 自主权威、无回滚、冲突仲裁
- [02-concepts.md](02-concepts.md) — 帧驱动状态同步基础概念
