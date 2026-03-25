# 实体权威同步（Entity Authority Sync）设计文档

> **状态**：设计确认中
>
> **核心思想**：帧驱动状态同步，用增量带宽换确定性。不依赖确定性数学库。
>
> **替换**：PredictionManager（全局回滚）→ EntitySyncManager（实体级权威 + Dead Reckoning）

---

## 1. 第一性原理

**问题**：多个客户端预测同一世界，必然分歧。怎么让玩家感觉不到？

**答案**：不靠确定性，靠物理直觉。

```
现实世界中，物体不能瞬间刹停、瞬间转身。
利用这个直觉：远端实体用惯性模型运动，
权威状态到达时不是"纠偏"，而是"更新运动目标"。
过渡是自然的，因为物理就是这样的。
```

| 方案 | 做法 | 问题 |
|------|------|------|
| GGPO 全局回滚 | 输入不匹配 → 全部回滚重算 | 需要确定性，成本爆炸 |
| 全局快照纠偏 | 定期发全世界快照 → 所有实体闪跳 | 高带宽，体验差 |
| **实体权威 + Dead Reckoning** | 管理者发自己的 pos+rot+vel → 远端惯性追踪 | ✅ 无需确定性，自然平滑 |

---

## 2. 核心模型

### 2.1 三个角色

| 角色 | 职责 |
|------|------|
| **Authority**（管理者） | 本地模拟实体，输入携带 pos+rot+vel 权威状态 |
| **Predictor**（远端） | Dead Reckoning 外推 + 惯性追踪权威状态 |
| **Server** | 帧驱动消息总线，透传状态字节，不解析内容 |

### 2.2 数据流

```
Authority A                Server              Predictor B
  本地移动角色               帧 N:               收到帧 N:
  SendInput(               打包广播               对 EntityA:
    input=[dx,dy]          ──────→                 LogicalState = 权威 pos+rot+vel
    + authority=[                                   VisualState 惯性追踪 LogicalState
      entityId: 1                                 对 EntityB:
      pos: (5.2, 3.1, 0)                           自己是管理者，跳过
      rot: (0, 0, 45°)
      vel: (1.0, 0, 0)
    ]
  )
```

### 2.3 远端实体的 60fps 渲染（Dead Reckoning + 惯性）

```
t=0ms:   服务器帧 N 到达，LogicalState = {pos=(5.0,0,0), vel=(1,0,0)}
t=16ms:  Unity 帧，外推 LogicalPos += vel*dt → (5.016, 0, 0)
t=32ms:  Unity 帧，外推 LogicalPos → (5.032, 0, 0)
t=50ms:  服务器帧 N+1 到达，LogicalState = {pos=(5.05,0,0), vel=(1,0,0)}
         微偏差 0.018 → 惯性模型自然吸收，无感知

方向突变：
t=50ms:  帧 N+1: vel 从 (1,0,0) 变为 (0,1,0)
         LogicalState 立刻更新目标速度
         VisualState 通过惯性模型减速→转向→加速
         玩家看到的是自然转弯，不是硬切
```

### 2.4 权威转移

```
场景：PlayerA 拾取地上的物品（Entity3）

帧 N:   Entity3.authority = Server（或 null）
帧 N:   PlayerA → Server: RequestAuthorityTransfer(entity=3)
帧 N+1: Server 裁决 → 广播: AuthorityGranted(entity=3, owner=A)
帧 N+2: PlayerA 开始发送 Entity3 的权威状态

冲突处理：同帧多人请求 → 服务器按到达顺序，先到先得，后到丢弃
```

---

## 3. 分层设计

```
┌─────────────────────────────────────────┐
│  游戏层                                  │
│  PlayerEntity : IEntitySync             │ ← 实现 2 个方法
│  EntityView<T> (可选 MonoBehaviour)      │ ← 拖组件，自动处理 logical/visual
│  SpringDamper / DeadZone (可选工具)      │ ← 按需使用
├─────────────────────────────────────────┤
│  框架层 — EntitySyncManager              │
│  权威表维护 + 状态字节投递 + 权威转移     │ ← 框架核心
├─────────────────────────────────────────┤
│  网络层 — FrameSyncClient                │
│  输入 + 权威状态编码 → 帧广播 → 解码分发  │ ← 已有，扩展
├─────────────────────────────────────────┤
│  传输层 — TCP / KCP                      │
│  字节收发                                │ ← 已有，不变
└─────────────────────────────────────────┘
```

**框架管什么，不管什么：**

| 框架管 | 框架不管 |
|--------|---------|
| 权威状态字节的可靠投递 | 状态字节内部格式 |
| 权威表（谁管理谁） | 惯性/插值/弹簧算法 |
| 权威转移协议 + 冲突裁决 | 多大偏差算"需要纠偏" |
| 可选工具（EntityView, SpringDamper） | 游戏逻辑 |

---

## 4. 接口设计

### 4.1 IEntitySync — 核心接口（游戏层实现）

```csharp
/// <summary>
/// 每个需要网络同步的实体实现此接口。
/// 框架只关心字节，不关心内容。
/// </summary>
public interface IEntitySync
{
    int EntityId { get; }
    int StateSize { get; }

    /// <summary>管理者调用：序列化当前 pos+rot+vel 到 buffer</summary>
    int WriteState(byte[] buffer, int offset);

    /// <summary>远端调用：收到管理者的权威状态</summary>
    /// <remarks>
    /// 游戏层决定怎么用这个状态：
    /// - 更新 LogicalState（推荐）
    /// - 直接 Snap（简单但生硬）
    /// - 喂给惯性模型（最佳体验）
    /// 框架不关心，只负责送达。
    /// </remarks>
    void OnRemoteState(ReadOnlySpan<byte> authorityState);
}
```

### 4.2 EntityView\<T\> — 可选 Unity 组件（框架提供）

```csharp
/// <summary>
/// 拖到 GameObject 上，自动处理：
/// - LogicalState（权威真值 + Dead Reckoning 外推）
/// - VisualState（渲染用，惯性追踪 LogicalState）
/// - 状态分离（逻辑帧率 = 服务器帧率，视觉帧率 = Unity 帧率）
/// </summary>
public class EntityView<T> : MonoBehaviour where T : struct, INetworkTransform
{
    public T LogicalState;    // 权威真值（每服务器帧更新）
    public T VisualState;     // 渲染值（每 Unity 帧平滑追踪）

    [Header("Smoothing")]
    public float positionSmoothTime = 0.1f;
    public float rotationSmoothTime = 0.05f;

    void Update()
    {
        // Dead Reckoning：用速度外推 LogicalState
        LogicalState.Position += LogicalState.Velocity * Time.deltaTime;

        // 惯性追踪：VisualState 弹簧阻尼追 LogicalState
        VisualState.Position = SmoothDamp(VisualState.Position, LogicalState.Position, ...);
        VisualState.Rotation = SmoothDamp(VisualState.Rotation, LogicalState.Rotation, ...);

        // 应用到 Transform
        transform.position = VisualState.Position;
        transform.rotation = VisualState.Rotation;
    }

    // 框架调用：权威状态到达
    public void SetRemoteState(T state)
    {
        LogicalState = state; // VisualState 不跳，由 Update 自然追踪
    }
}

/// <summary>状态必须包含这三个字段</summary>
public interface INetworkTransform
{
    Vector3 Position { get; set; }
    Quaternion Rotation { get; set; }
    Vector3 Velocity { get; set; }
}
```

### 4.3 可选工具

```csharp
// 弹簧阻尼器 — 比 Lerp 更物理，有减速过程
class SpringDamper { Vector3 SmoothDamp(current, target, ref velocity, smoothTime, dt); }

// 死区 — 微小偏差不纠偏，避免抖动
class DeadZone { bool ShouldCorrect(float divergence, float threshold); }

// 速度预测器 — 用历史速度预测方向变化趋势
class VelocityPredictor { Vector3 Predict(velocity, acceleration, dt); }
```

---

## 5. 协议变更

### 5.1 输入消息格式

```
[inputLen: 2B][inputData: N bytes]
[entityCount: 1B]
  N × [entityId: 4B][stateLen: 2B][stateData: M bytes]
```

Phase 1（1 人 1 实体）典型大小：`2 + 8 + 1 + 4 + 2 + 24 = 41 bytes/frame`

### 5.2 新增协议命令

| Cmd | 名称 | 方向 | 说明 |
|-----|------|------|------|
| 27 | RequestAuthorityTransfer | C→S | 请求获取实体权威 |
| 28 | AuthorityGranted | S→C | 服务器裁决：权威已授予 |
| 29 | AuthorityRevoked | S→C | 服务器裁决：权威已收回 |

---

## 6. 实现计划

### Phase 1：MVP（1 人 = 1 实体，无权威转移）

```
Core/EntitySync/（新目录，替换 Core/Prediction/）
  IEntitySync.cs           核心接口
  EntitySyncManager.cs     权威表 + 状态投递调度
  EntitySyncCodec.cs       输入 + 权威状态编解码

Client/FrameSync/FrameSyncClient.cs
  EntitySync 属性替换 Prediction
  SendInput 追加权威状态
  HandlePushFrames 触发 OnRemoteState

Unity 包:
  Runtime/EntitySync/EntityView.cs      EntityView<T> 组件
  Runtime/EntitySync/INetworkTransform.cs
  Runtime/EntitySync/SpringDamper.cs    弹簧工具

Demo02/ 用新系统重写

删除:
  Core/Prediction/* (Phase 1 通过后)
```

### Phase 2：权威转移 + 多实体

```
Cmd 27/28/29 协议
服务器先到先得裁决
EntitySyncManager.TransferAuthority()
Demo: 可拾取物品
```

### Phase 3：生产加固

```
StateHash 反同步检测（上报，不强制纠偏）
纠偏统计 GM 面板
自动化测试（模拟延迟 + 验证平滑度）
服务器验证（反作弊）
```

---

## 7. 验收标准

### Phase 1

| 测试 | 预期 |
|------|------|
| 正常移动 | 远端角色平滑跟随，无抖动 |
| 100ms 延迟 | 远端有轻微滞后但平滑，方向变化自然过渡 |
| 急转弯 | 惯性模型：减速→转向→加速，不硬切 |
| 断线重连 | 权威状态恢复后，远端平滑归位 |
| 带宽 | 20fps × 41B/frame ≈ 820 B/s/player |

---

## 8. 与现有系统的关系

| 组件 | 变化 |
|------|------|
| PredictionManager | Phase 1 通过后删除 |
| ISimulation | 删除（被 IEntitySync 替代） |
| InputBuffer / SnapshotBuffer | 删除（不需要全局快照缓冲） |
| FrameSyncClient | `Prediction` → `EntitySync` |
| Person | `SetPrediction` → `RegisterEntity` |
| TakeSnapshot / LoadSnapshot | 保留（重连用，独立于实体同步） |
| 服务器 | 仅新增权威转移裁决，状态字节纯透传 |

---

## 9. 设计决策记录

| 问题 | 决策 | 理由 |
|------|------|------|
| 确定性要求 | 不要求 | 确定性数学库是成本陷阱，用带宽换 |
| 纠偏方式 | 惯性模型（物理直觉） | 现实物体不能瞬间刹停，自然过渡 |
| 远端渲染 | Dead Reckoning + 惯性追踪 | 30 年验证的方案 |
| 接口复杂度 | 2 个核心方法 | WriteState + OnRemoteState |
| 框架 vs 游戏层 | 框架管投递，游戏层管纠偏算法 | EntityView\<T\> 可选工具 |
| 权威状态内容 | pos + rot + vel（~24B） | 支持 Dead Reckoning |
| 逻辑/视觉分离 | EntityView\<T\> 组件 | 框架提供，游戏层可选 |
| 权威转移冲突 | 服务器裁决（先到先得） | 客户端做不可靠 |
| 全局回滚 | 删除（替换） | 实体级纠偏更轻量、更通用 |
| 带宽估算 | ~41B/frame/entity @ 20fps | Phase 1 限 1 人 1 实体 |
