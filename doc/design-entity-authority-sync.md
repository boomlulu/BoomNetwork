# 实体权威同步（Entity Authority Sync）设计文档

> **状态**：设计中，待确认
>
> **目标**：替换现有 PredictionManager（全局回滚），实现实体级权威同步 + 增量纠偏。
> 成为生产级通用帧同步框架的核心预测系统。

---

## 1. 第一性原理

**问题**：多个客户端对同一个游戏世界做本地预测，必然产生分歧。怎么纠正？

| 方案 | 代价 | 适合 |
|------|------|------|
| 确定性模拟 + 输入回滚（GGPO） | 需要确定性数学/物理，全局 SaveState/LoadState | 格斗游戏（2 人，状态小） |
| 全局快照纠偏 | 高带宽（全世界快照），全部实体闪跳 | 简单原型 |
| **实体级权威 + 增量纠偏** | 每帧携带管理实体的状态（低带宽），只纠偏不一致的实体 | **通用，生产级** |

**选择第三种。核心洞察：不需要确定性，用增量带宽换纠偏精度。**

---

## 2. 核心模型

### 2.1 三个角色

```
Authority（权威方）= 实体的管理者
  ┌─ 本地模拟该实体，状态是"真相"
  ├─ 输入消息携带该实体的权威状态
  └─ 可转移给其他玩家

Predictor（预测方）= 非管理者客户端
  ┌─ 本地预测该远端实体（外推最后已知输入）
  ├─ 收到权威状态 → 对比 → 不匹配则纠偏
  └─ 纠偏方式：平滑插值（不是全局回滚）

Server（服务器）= 帧驱动的消息总线
  ┌─ 收集所有玩家输入（含权威状态）
  ├─ 按帧率打包广播
  └─ 不理解实体状态内容（纯透传）
```

### 2.2 数据流

```
                    ┌──────── Server ────────┐
                    │   帧 N:                │
Authority A ──────→ │     PlayerA: input+state│ ──────→ Predictor B
  SendInput(        │     PlayerB: input+state│           ReceiveFrame:
    input=[dx,dy]   │   广播给所有人          │             对 EntityA: 对比 + 纠偏
    + entities=[    └────────────────────────┘             对 EntityB: 本地权威，跳过
      {id:1, state:[pos,rot,...]}
    ]
  )
```

### 2.3 权威转移

```
帧 N: Entity3.authority = PlayerA
帧 N+1: PlayerA 发送 AuthorityTransfer(entityId=3, newOwner=PlayerB)
帧 N+2: Entity3.authority = PlayerB（所有客户端同步更新）
```

转移通过帧系统广播，保证所有客户端在同一帧看到权威变更。

---

## 3. 接口设计（游戏层需要实现的）

### 3.1 IEntitySync — 单个可同步实体

```csharp
/// <summary>
/// 游戏层为每个需要网络同步的实体实现此接口。
/// 框架不关心实体是什么（角色/子弹/道具），只关心状态字节。
/// </summary>
public interface IEntitySync
{
    /// <summary>实体唯一标识</summary>
    int EntityId { get; }

    /// <summary>序列化当前状态（框架调用，放入输入消息）</summary>
    /// <remarks>
    /// 返回的 byte[] 由框架复制，调用者可复用 buffer。
    /// 只需包含需要同步的状态（位置、旋转、速度等），不需要包含 EntityId。
    /// </remarks>
    int WriteState(byte[] buffer, int offset);

    /// <summary>从权威状态纠偏（框架调用，当预测与权威不一致时）</summary>
    /// <param name="authorityState">管理者的权威状态字节</param>
    /// <param name="divergence">偏差程度（0=完全匹配）</param>
    /// <remarks>
    /// 游戏层决定纠偏方式：硬切、插值、弹簧等。
    /// 框架只负责检测不一致并调用此方法。
    /// </remarks>
    void ApplyCorrection(ReadOnlySpan<byte> authorityState, float divergence);

    /// <summary>计算本地状态与权威状态的偏差（0=匹配，>0=偏离）</summary>
    /// <remarks>
    /// 框架用返回值决定是否触发 ApplyCorrection。
    /// 典型实现：Vector3.Distance(localPos, authorityPos)
    /// </remarks>
    float CompareTo(ReadOnlySpan<byte> authorityState);

    /// <summary>从输入预测下一帧状态（仅对远端实体调用）</summary>
    void PredictFromInput(ReadOnlySpan<byte> input, float deltaTime);

    /// <summary>状态大小（字节数，固定大小或最大大小）</summary>
    int StateSize { get; }
}
```

### 3.2 IEntitySyncManager — 框架对游戏层暴露的管理器

```csharp
/// <summary>
/// 框架提供，游戏层注册/注销实体。
/// </summary>
public interface IEntitySyncManager
{
    /// <summary>注册实体到同步系统</summary>
    void RegisterEntity(IEntitySync entity, int authorityPlayerId);

    /// <summary>注销实体</summary>
    void UnregisterEntity(int entityId);

    /// <summary>转移权威</summary>
    void TransferAuthority(int entityId, int newAuthorityPlayerId);

    /// <summary>查询权威</summary>
    int GetAuthority(int entityId);

    /// <summary>纠偏阈值（偏差小于此值不触发纠偏）</summary>
    float CorrectionThreshold { get; set; }
}
```

---

## 4. 协议变更

### 4.1 输入消息格式（C→S）

现有：`[inputData: N bytes]`（纯原始字节，框架不解析）

**新增**：输入数据后追加权威实体状态

```
[inputLen: 2B]
[inputData: inputLen bytes]        ← 游戏输入（dx, dy 等）
[entityCount: 1B]                  ← 该玩家管理的实体数
  N × [
    entityId: 4B                   ← 实体 ID
    stateLen: 2B                   ← 状态字节数
    stateData: stateLen bytes      ← 权威状态
  ]
```

### 4.2 权威转移消息

新增协议命令：

```
CmdAuthorityTransfer = 27  // C→S 或 S→C
  [entityId: 4B]
  [newAuthorityPlayerId: 4B]
```

服务器收到后广播给所有客户端（含发起者），保证所有人在同一帧生效。

### 4.3 向后兼容

- 旧客户端（不发权威状态）：`entityCount = 0`，正常工作但无纠偏
- 服务器透传，不解析实体状态内容

---

## 5. 实现计划

### Phase 1：MVP（一个玩家 = 一个实体，无权威转移）

```
改动：
  Core/Prediction/ → Core/EntitySync/（新目录）
    IEntitySync.cs          — 接口定义
    EntitySyncManager.cs    — 注册/纠偏/预测调度
    EntitySyncCodec.cs      — 输入+权威状态的编解码

  Client/FrameSync/FrameSyncClient.cs
    EntitySync 属性（替换 Prediction 属性）
    SendInput 自动附加权威实体状态
    HandlePushFrames 自动触发远端实体纠偏

  Demo02/ → 用新系统重写
    每个 PlayerEntity 实现 IEntitySync
    验证：两个客户端，位置+旋转纠偏正确

删除：
  Core/Prediction/PredictionManager.cs（旧全局回滚）
  Core/Prediction/InputBuffer.cs
  Core/Prediction/SnapshotBuffer.cs
  Core/Prediction/ISimulation.cs
```

### Phase 2：权威转移

```
新增：
  CmdAuthorityTransfer 协议
  EntitySyncManager.TransferAuthority()
  服务器透传逻辑

Demo：
  可拾取物品 — 拾取后权威从世界→玩家
```

### Phase 3：生产加固

```
新增：
  纠偏统计（每个实体的纠偏次数/平均偏差）
  GM 工具展示纠偏状态
  自动化测试（模拟延迟 + 验证纠偏正确性）
  文档 + 接入指南
```

---

## 6. 验收标准

### Phase 1 验收

```
场景：两个客户端，每人控制一个角色，20fps 帧同步

测试 1：正常移动
  Player A 移动 → Player B 屏幕上 A 的角色平滑跟随
  无明显抖动或跳跃

测试 2：人为增加延迟
  模拟 100ms 延迟 → 远端角色有轻微滞后但平滑
  纠偏发生时不闪烁

测试 3：方向突变
  Player A 急转弯 → Player B 预测错误 → 平滑纠偏到正确位置
  纠偏过程 < 200ms

测试 4：断线重连
  Player A 断线 → 重连 → 权威状态恢复 → 双方一致
```

### Phase 2 验收

```
测试 5：权威转移
  Player A 拾取物品 → 物品权威从世界→A → A 移动时物品跟随
  Player B 屏幕上物品平滑跟随 A
```

---

## 7. 与现有系统的关系

| 现有组件 | 变化 |
|---------|------|
| FrameSyncClient | 替换 `Prediction` 属性为 `EntitySync` |
| SendInput | 自动追加权威实体状态字节 |
| HandlePushFrames | 触发 EntitySyncManager 处理远端实体 |
| Person | 替换 SetPrediction/ClearPrediction 为 RegisterEntity/UnregisterEntity |
| TakeSnapshot/LoadSnapshot | 保留（用于重连，与实体同步独立） |
| 帧数据格式 | FrameInput.Data 长度增加（携带权威状态） |
| 服务器 | **不变**（纯透传，不解析实体状态） |

---

## 8. 风险

| 风险 | 缓解 |
|------|------|
| 带宽增加 | 每帧只发管理实体的状态，通常 1 实体 × 12-20B。20fps = 240-400B/s，可接受 |
| 权威转移竞争 | 同一帧两个玩家都声称拥有同一实体 → 服务器按先到先得 |
| 恶意客户端发假状态 | 生产环境需要服务器验证（Phase 3） |
| 旧 PredictionManager 删除 | Phase 1 通过后再删，保留 git 历史可回退 |
