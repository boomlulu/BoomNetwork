---
name: bn-desync
description: "帧同步不同步（Desync）防御知识库。覆盖 14 类不同步根因、排查清单、Snapshot 完整性校验、ComputeHash 覆盖率、FInt 定点数安全规范。用于审计 Sample / Demo 或新增帧同步代码时防止引入不同步 bug。"
allowed-tools: ["Read", "Grep", "Glob", "Agent"]
---

# Desync Prevention — 帧同步不同步防御

## 触发场景

- 新增/修改 BoomNetwork Sample 或 Demo 的 Simulation 代码
- 排查线上不同步报告
- 审计 Snapshot 序列化/反序列化
- 审计 ComputeHash 覆盖率
- 新增 FInt 运算或调用

## 不同步根因分类（14 类）

### CRITICAL — 必然不同步

| # | 类别 | 典型表现 | 检查方法 |
|---|------|---------|---------|
| C1 | **Snapshot slot 重排** | Serialize 跳过 dead slot，Deserialize 从 0 填充 → late-joiner Alloc 返回不同 slot → RNG 级联偏移 | grep `for.*count.*i++` 在 Deserialize 中，检查是否用了序列化的 slot index |
| C2 | **OnFrame 外修改 GameState** | 网络回调直接改 IsActive/IsAlive → 不同客户端在不同帧生效 | grep `state\.\|_sim\.State\.` 在 Network/ 目录中，确认只在 OnFrame 回调链内修改 |
| C3 | **Snapshot 字段遗漏** | Dt/Facing/Weapon/Orb 等字段未序列化 → late-joiner 状态不完整 | 比较 struct 定义 vs Serialize 写入字段，逐一核对 |

### HIGH — 特定场景不同步

| # | 类别 | 典型表现 | 检查方法 |
|---|------|---------|---------|
| H1 | **浮点运算在 Simulation** | float/double/Mathf/Math.Sin 在帧循环内 → Mono vs IL2CPP 1 ULP 差异 | grep `float\|double\|Mathf\.\|Math\.` 在 Simulation/ 目录 |
| H2 | **Runtime FromFloat** | `FInt.FromFloat(non-constant)` 在非静态初始化上下文 | grep `FromFloat` 检查参数是否含变量 |
| H3 | **Dictionary/HashSet 迭代** | 遍历顺序不确定 → 影响 RNG 消费或处理顺序 | grep `Dictionary<\|HashSet<` 在 Simulation/ |
| H4 | **同帧重复击杀** | 空间哈希 bucket chain 未更新，已死敌人被再次命中 → 重复掉落 | 检查碰撞 resolver 内循环是否有 `IsAlive` 守卫 |
| H5 | **SinTable 平台差异** | Math.Sin 构建查找表 → Mono vs IL2CPP ±1 ULP | 检查 SinTable 是否硬编码 int 数组 |

### MEDIUM — 检测盲区或潜在风险

| # | 类别 | 典型表现 | 检查方法 |
|---|------|---------|---------|
| M1 | **ComputeHash 不完整** | 遗漏字段的不同步不会被检测到 | 对照 struct 字段 vs hash 内容 |
| M2 | **除零未防御** | FInt `/` 抛异常 → 不同客户端异常处理不同 | grep `operator /` 检查零值守卫 |
| M3 | **条件 RNG 消费** | if 分支内调用 RNG → 分支条件一致则安全，但 slot layout 偏移会级联 | 全局搜索 DeterministicRng 调用，标记条件分支 |
| M4 | **List.Sort 不稳定** | 同 key 元素排序不同 → 处理顺序不同 | grep `\.Sort` 检查 comparer 是否保证稳定 |

### LOW — 代码规范

| # | 类别 | 检查方法 |
|---|------|---------|
| L1 | **FInt * int 无 long 提升** | grep `operator \*.*int b.*a\.Raw \* b` 检查是否有溢出风险 |
| L2 | **static 可变状态** | grep `static.*\[\]` 在 Simulation/ 检查是否在游戏重置时清零 |
| L3 | **async/Coroutine 改 GameState** | grep `async\|StartCoroutine\|yield` 在 Simulation/ |

## 排查 Checklist

审计一个新 Sample 时，按此顺序执行：

```
□ 1. Simulation 目录内无 float/double/Mathf/Math.Sin/Random
□ 2. 所有 FInt.FromFloat 仅用于编译期常量（或换成 new FInt(raw)）
□ 3. SinTable 是硬编码 int[]，非 Math.Sin 运行时构建
□ 4. FInt operator / 有 b==0 防御
□ 5. 无 Dictionary/HashSet/LINQ 在 Simulation
□ 6. 所有 GameState 修改只在 OnFrame 回调链内
□ 7. Snapshot 序列化：slot index 保留（Serialize 写 index，Deserialize 读 index）
□ 8. Snapshot 序列化：所有 GameState 可变字段都被写入/读回
□ 9. Snapshot 序列化：Dt 字段包含在内
□ 10. ComputeHash 覆盖所有 Snapshot 中的字段
□ 11. 碰撞 resolver 内循环有 IsAlive 守卫（防同帧重复击杀）
□ 12. RNG 消费顺序：条件分支内的 RNG 调用，确认条件本身是确定性的
□ 13. AllocXxx 方法是 first-fit 扫描，slot layout 保持一致
□ 14. 无 async/Coroutine/Timer 修改 GameState
□ 15. PlayerId→Slot 不能用 pid-1（服务器 PID 全局递增），必须动态映射
```

## FInt 安全规范

### 必须遵守

```
1. Simulation 内的常量用 new FInt(raw) 而非 FInt.FromFloat()
2. SinTable 硬编码（1024 个 int），消除 Math.Sin 依赖
3. operator / 对 b==0 返回 MaxValue/MinValue，不抛异常
4. operator * (FInt, FInt) 必须 long 提升：(long)a.Raw * b.Raw >> SHIFT
5. InvSqrt 优于 One / Sqrt（省一次除法，纯整数）
6. 距离比较用 DistanceSqr/LengthSqr 替代 Sqrt
```

### 推荐

```
7. 所有 operator 和小方法标记 [AggressiveInlining]
8. Sin/Cos 的 % TABLE_SIZE → & TABLE_MASK（无分支位与）
9. Sqrt 用二进制算法（纯位运算，零除法）而非 Newton-Raphson
```

## Snapshot 完整性校验模板

```
对于 GameState 中的每个 struct：
  1. 列出所有 public 可变字段
  2. 检查 Serialize 是否写了每个字段
  3. 检查 Deserialize 是否读回每个字段
  4. 检查 ComputeHash 是否包含每个字段
  5. 对于数组类型（Enemies/Projectiles/Gems）：
     - Serialize 必须写 slot index
     - Deserialize 必须用 slot index 恢复到原位
     - Deserialize 前必须 Array.Clear 整个数组
```

## 架构原则：GameState 变更的单一入口

**所有 GameState 变更只通过两条确定性路径，都由服务器 FrameData 驱动：**

```
GameState 变更
    │
    ├── 帧事件（OnPlayerJoined / OnPlayerLeft / OnHostChanged）
    │     嵌入 FrameData → 所有客户端同帧执行
    │     适用：玩家加入/离开/掉线 → InitPlayer / deactivate
    │
    └── OnFrame → Tick → ApplyInputs
          FrameData 中的输入 → 确定性模拟
          适用：移动/攻击/升级 + 玩家 auto-init（首次输入）
```

### 绝对禁止

```
✗ OnFrameSyncStart 里 InitPlayer（不同客户端 _knownPlayers 不同）
✗ LoadSnapshot 里 InitPlayer（catch-up 帧期间幽灵玩家）
✗ OnJoinedRoom 里改 GameState（网络回调时序不确定）
✗ _syncing=false 时 TakeSnapshot（Init 前快照 = 错误的 RNG seed）
✗ 用 _knownPlayers 等客户端本地状态决定 InitPlayer
```

### 正确做法

```
✓ OnFrameSyncStart: 只调 Init(dt, seed)，不 InitPlayer
✓ OnPlayerJoined 帧事件: if (_syncing && !IsActive) InitPlayer — 确定性
✓ ApplyInputs auto-init: 首次输入到达时 InitPlayer — 确定性
✓ TakeSnapshot: _syncing 时才序列化（Init 后的正确状态）
✓ LoadSnapshot: 纯反序列化，不改 GameState
✓ 首帧必发输入 (_firstInputSent): 解决 Silent When Idle 下 auto-init
```

### 为什么这样设计

初始快照陷阱：`RequestStart(snapshot)` 在 `OnFrameSyncStart` 之前执行。
此时 `Init` 还没调用，快照里 RNG seed=0、无玩家。late-joiner 加载
这个快照后 RNG 流完全不同。解法：`TakeSnapshot` 在 `_syncing=false` 时
返回 null，不发初始快照。第一个有效快照由正常的快照上传周期提供。

## VampireSurvivors Demo 已修复的不同步 Bug（2026-03-28）

| Bug | 根因 | 修复 |
|-----|------|------|
| **初始快照 RNG seed=0** | RequestStart 在 Init 前拍快照 | TakeSnapshot 检查 _syncing |
| **_knownPlayers 不一致** | 不同客户端 join 时序不同 | 删除 _knownPlayers，用帧事件 |
| **InitPlayer 时序不同** | OnFrameSyncStart/LoadSnapshot 各自 init | 统一到帧事件 + auto-init |
| Snapshot slot 重排 | Serialize 跳过 dead slot | 写入 ushort slot index |
| Dt 未序列化 | late-joiner 首帧 Dt=0 | Snapshot 增加 Dt.Raw |
| SinTable 平台差异 | Math.Sin 运行时构建 | 硬编码 1024 int 常量 |
| FInt 除零 | operator / 无 b==0 防御 | 返回 MaxValue/MinValue |
| FromFloat 运行时 | 17 处 FromFloat 用于常量 | 全部换 new FInt(raw) |
| Holy Water 重复击杀 | bucket chain 未刷新 IsAlive | 内循环加 IsAlive 守卫 |
| Knife/Orb 击杀已死敌人 | Lightning 同帧先杀 | 加 IsAlive 守卫 |
| ComputeHash 遗漏 | ~15 个可变字段未 hash | 补全所有字段 + Gems |
| **PlayerId→Slot 越界** | 服务器 PID 全局递增（8,9,10...），pid-1 超出 Players[4] | 动态 PidToSlot 映射，按首次出现顺序分配 slot 0-3 |
