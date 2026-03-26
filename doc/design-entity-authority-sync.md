# 实体权威同步（Entity Authority Sync）设计文档

> **状态**：Phase 1 已实施（2026-03-25）
>
> - L1 框架核心：ExtCmd 40/41 + IEntitySync + EntityStateCodec（BoomNetwork `e45555e`）
> - L2 纠偏中间件：IDeadReckoning + IInertiaModel + ICorrectionStrategy（BoomNetworkUnity）
> - L3 NetworkTransformSync：2D 具体实现（BoomNetworkUnity）
> - L4 Demo02：EntitySyncDemoManager（BoomNetworkUnity）
> - 网络模拟：netsim 延迟/抖动/丢包 + GM 面板
> - 待做：Phase 2 权威转移 / Phase 3 生产加固
>
> **核心思想**：帧驱动状态同步，用增量带宽换确定性。不依赖确定性数学库。
>
> **替换**：PredictionManager（全局回滚）→ 实体权威同步（实体级权威 + Dead Reckoning）。Prediction 子系统已从核心层删除。

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
帧 N:   PlayerA → Server: AuthorityTransfer(ExtCmd 42, subCmd=request, entity=3)
帧 N+1: Server 裁决 → 广播: AuthorityTransfer(ExtCmd 42, subCmd=result, entity=3, owner=A)
帧 N+2: PlayerA 开始发送 Entity3 的权威状态

冲突处理：同帧多人请求 → 服务器按到达顺序，先到先得，后到丢弃
```

---

## 3. 分层设计

```
┌──────────────────────────────────────────────┐
│  Layer 4: 游戏层                              │
│  PlayerEntity : IEntitySync（实现 2 个方法）   │
│  定义状态结构体 T                              │
├──────────────────────────────────────────────┤
│  Layer 3: Unity 集成（可选）                   │
│  EntityView<T> — MonoBehaviour，Inspector 配置 │
│  拖上去就能用，自动 wire Layer 1 + Layer 2     │
├──────────────────────────────────────────────┤
│  Layer 2: 纠偏中间件（可插拔策略）             │
│  IDeadReckoning   — 看起来在动                 │
│  IInertiaModel    — 动得像人                   │
│  ICorrectionStrategy — 错了怎么办              │
│  默认实现开箱即用，可替换                       │
├──────────────────────────────────────────────┤
│  Layer 1: 框架核心（字节管道）                 │
│  EntitySyncManager — 权威表 + 状态投递         │
│  FrameSyncClient — 帧编码/广播/解码            │
│  IAuthorityModel — 谁说了算（服务器裁决）      │
├──────────────────────────────────────────────┤
│  Layer 0: 传输层（已有，不变）                 │
│  TCP / KCP                                    │
└──────────────────────────────────────────────┘
```

### 层间职责边界

| 层 | 管什么 | 不管什么 |
|----|--------|---------|
| L1 框架核心 | 权威状态字节投递、权威表、权威转移协议 | 状态内容、纠偏算法 |
| L2 中间件 | Dead Reckoning、惯性平滑、纠偏策略 | 网络协议、具体实体逻辑 |
| L3 Unity 集成 | Logical/Visual 状态分离、Inspector 配置 | 传输细节 |
| L4 游戏层 | 状态格式定义、WriteState/OnRemoteState | 一切框架和中间件已处理的 |

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

### 4.2 纠偏中间件接口（Layer 2，可插拔）

四个问题 → 四个策略接口 → 各有默认实现：

```csharp
// ① 看起来在动？— 两帧之间的外推
public interface IDeadReckoning
{
    /// <summary>用速度外推 logical state（每 Unity 帧调用）</summary>
    void Extrapolate(ref Vector3 position, ref Quaternion rotation, Vector3 velocity, float dt);
}

// 默认实现：线性外推
public class LinearDeadReckoning : IDeadReckoning
{
    public void Extrapolate(ref Vector3 pos, ref Quaternion rot, Vector3 vel, float dt)
    {
        pos += vel * dt;
    }
}
```

```csharp
// ② 动得像人？— visual 追 logical 的平滑方式
public interface IInertiaModel
{
    /// <summary>visual state 平滑追踪 logical state（每 Unity 帧调用）</summary>
    void Smooth(ref Vector3 visualPos, ref Quaternion visualRot,
                Vector3 logicalPos, Quaternion logicalRot, float dt);
}

// 默认实现：弹簧阻尼
public class SpringInertia : IInertiaModel
{
    public float positionSmoothTime = 0.1f;
    public float rotationSmoothTime = 0.05f;
    private Vector3 _velRef;

    public void Smooth(ref Vector3 vPos, ref Quaternion vRot,
                       Vector3 lPos, Quaternion lRot, float dt)
    {
        vPos = Vector3.SmoothDamp(vPos, lPos, ref _velRef, positionSmoothTime, Mathf.Infinity, dt);
        vRot = Quaternion.Slerp(vRot, lRot, 1f - Mathf.Exp(-dt / rotationSmoothTime));
    }
}
```

```csharp
// ③ 错了怎么办？— 收到权威状态时的处理策略
public interface ICorrectionStrategy
{
    /// <summary>权威状态到达，决定如何修正 logical state</summary>
    /// <param name="logical">当前本地预测的逻辑状态（可修改）</param>
    /// <param name="authority">管理者的权威状态</param>
    void OnAuthorityReceived(ref Vector3 logicalPos, ref Quaternion logicalRot,
                             ref Vector3 logicalVel,
                             Vector3 authPos, Quaternion authRot, Vector3 authVel);
}

// 默认实现：死区 + 平滑 + 瞬移阈值
public class SmoothCorrection : ICorrectionStrategy
{
    public float deadZone = 0.01f;      // 偏差 < 此值，不纠偏（避免抖动）
    public float snapThreshold = 5.0f;  // 偏差 > 此值，直接瞬移（太远了平滑没意义）

    public void OnAuthorityReceived(ref Vector3 lPos, ref Quaternion lRot, ref Vector3 lVel,
                                    Vector3 aPos, Quaternion aRot, Vector3 aVel)
    {
        float dist = Vector3.Distance(lPos, aPos);
        if (dist < deadZone) return;            // 微偏差，忽略
        if (dist > snapThreshold)               // 巨偏差，瞬移
        {
            lPos = aPos; lRot = aRot; lVel = aVel;
            return;
        }
        lPos = aPos; lRot = aRot; lVel = aVel;  // 正常：更新 logical，visual 靠惯性追
    }
}
```

```csharp
// ④ 谁说了算？— 权威模型（Phase 2 可替换）
public interface IAuthorityModel
{
    /// <summary>某实体的权威从哪来</summary>
    int GetAuthority(int entityId);
    bool IsLocalAuthority(int entityId, int localPlayerId);
}

// 默认实现：服务器分配 + 先到先得转移
public class ServerAuthority : IAuthorityModel { ... }
```

**可插拔示例：**

```csharp
// 格斗游戏：用更激进的纠偏（容忍度低）
entityView.correctionStrategy = new SmoothCorrection { deadZone = 0.001f, snapThreshold = 2f };

// 休闲游戏：用更宽松的纠偏（容忍度高）
entityView.correctionStrategy = new SmoothCorrection { deadZone = 0.1f, snapThreshold = 10f };

// 自定义：贝塞尔曲线纠偏
entityView.correctionStrategy = new BezierCorrection { curveDuration = 0.3f };
```

### 4.3 EntityView\<T\> — Unity 组件（组装 Layer 1 + Layer 2）

```csharp
/// <summary>
/// 拖到 GameObject → Inspector 选策略 → 自动 work。
/// 组装框架核心（状态投递）+ 中间件（4 个策略）于一体。
/// </summary>
public class EntityView<T> : MonoBehaviour, IEntitySync where T : struct, INetworkTransform
{
    // --- 状态 ---
    public T LogicalState;    // 权威真值 + Dead Reckoning 外推
    public T VisualState;     // 渲染值，惯性追踪 Logical

    // --- 可插拔策略（Inspector 或代码设置）---
    public IDeadReckoning deadReckoning = new LinearDeadReckoning();
    public IInertiaModel inertia = new SpringInertia();
    public ICorrectionStrategy correction = new SmoothCorrection();

    // --- IEntitySync 实现（框架调用）---
    public int EntityId { get; set; }
    public int StateSize => ...; // sizeof(T)

    public int WriteState(byte[] buf, int offset)
    {
        // 管理者：序列化本地 logical state
        return Serialize(LogicalState, buf, offset);
    }

    public void OnRemoteState(ReadOnlySpan<byte> authorityState)
    {
        // 远端：收到权威状态
        T auth = Deserialize<T>(authorityState);

        // ③ Correction：决定怎么修正 logical
        var lPos = LogicalState.Position;
        var lRot = LogicalState.Rotation;
        var lVel = LogicalState.Velocity;
        correction.OnAuthorityReceived(
            ref lPos, ref lRot, ref lVel,
            auth.Position, auth.Rotation, auth.Velocity);
        LogicalState.Position = lPos;
        LogicalState.Rotation = lRot;
        LogicalState.Velocity = lVel;
        // VisualState 不动，由 Update 惯性追踪
    }

    void Update()
    {
        if (!isLocalAuthority)
        {
            // ① Dead Reckoning：帧间外推
            var pos = LogicalState.Position;
            var rot = LogicalState.Rotation;
            deadReckoning.Extrapolate(ref pos, ref rot, LogicalState.Velocity, Time.deltaTime);
            LogicalState.Position = pos;
            LogicalState.Rotation = rot;
        }

        // ② Inertia：visual 平滑追 logical
        var vp = VisualState.Position;
        var vr = VisualState.Rotation;
        inertia.Smooth(ref vp, ref vr, LogicalState.Position, LogicalState.Rotation, Time.deltaTime);
        VisualState.Position = vp;
        VisualState.Rotation = vr;

        // 应用到 Transform
        transform.position = VisualState.Position;
        transform.rotation = VisualState.Rotation;
    }
}

/// <summary>权威状态必须包含的基础字段</summary>
public interface INetworkTransform
{
    Vector3 Position { get; set; }
    Quaternion Rotation { get; set; }
    Vector3 Velocity { get; set; }
}
```

**使用方式：**

```csharp
// 方式 1：默认策略（拖组件即可）
var view = go.AddComponent<EntityView<PlayerState>>();
// 开箱即用：LinearDeadReckoning + SpringInertia + SmoothCorrection

// 方式 2：替换策略
view.inertia = new SpringInertia { positionSmoothTime = 0.2f };
view.correction = new SmoothCorrection { deadZone = 0.05f };

// 方式 3：完全自定义
view.deadReckoning = new MyQuadraticDeadReckoning();
view.correction = new MyBezierCorrection();
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

| ExtCmd | 名称 | 方向 | 说明 |
|--------|------|------|------|
| 40 | SendEntityState | C→S | 管理者发送实体权威状态 |
| 41 | PushEntityState | S→C | 广播实体权威状态（带 senderPid） |
| 42 | AuthorityTransfer (subCmd=request) | C→S | 请求获取实体权威 |
| 42 | AuthorityTransfer (subCmd=result) | S→C | 服务器裁决：权威授予/收回结果 |

---

## 6. 实现计划

### Phase 1：MVP（1 人 = 1 实体，无权威转移）

```
✅ 已完成:
  Core/Prediction/* — 已从 cli/ 和 unity UPM 包中删除
  FrameSyncClient — Prediction 属性、PredictWithInput() 已删除
  IEntitySync + EntityStateCodec — 在 FrameSyncProtocol.cs 中实现
  FrameSyncClient.RegisterAuthorityEntity / SendAuthorityEntityStates — 已实现
  Demo02 用新系统重写

待做:
  Unity 包: Runtime/EntitySync/EntityView<T> 组件（L2+L3 反哺 UPM 包）
```

### Phase 2：权威转移 + 多实体

```
ExtCmd 40/41/42 协议（SendEntityState / PushEntityState / AuthorityTransfer）
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

| 组件 | 变化 | 状态 |
|------|------|------|
| PredictionManager | 已从核心层删除 | ✅ 已完成 |
| ISimulation | 已删除（被 IEntitySync 替代） | ✅ 已完成 |
| InputBuffer / SnapshotBuffer | 已删除（不需要全局快照缓冲） | ✅ 已完成 |
| ISnapshotable | 已删除（被 OnTakeSnapshot/OnLoadSnapshot 委托替代） | ✅ 已完成 |
| FrameSyncClient | `Prediction` 属性、`PredictWithInput()` 已删除；实体同步通过 `RegisterAuthorityEntity` | ✅ 已完成 |
| TakeSnapshot / LoadSnapshot | 保留（重连用，独立于实体同步） | — |
| 服务器 | 仅新增权威转移裁决，状态字节纯透传 | — |

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
| 全局回滚 | 已删除 | 实体级纠偏更轻量、更通用。Prediction 子系统已从核心层移除 |
| 带宽估算 | ~41B/frame/entity @ 20fps | Phase 1 限 1 人 1 实体 |
