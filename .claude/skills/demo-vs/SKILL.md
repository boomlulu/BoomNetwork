---
name: demo-vs
description: "BoomNetwork VampireSurvivors Demo 完整架构。纯 FrameSync 弹幕生存，FInt 定点数确定性模拟，空间哈希碰撞，不同步防御。用于迭代游戏性、添加新怪/武器/机制、修改同步逻辑。"
allowed-tools: ["Read", "Write", "Edit", "Bash", "Glob", "Grep", "Agent"]
---

# VampireSurvivors Demo — BoomNetwork 弹幕生存 Sample

## 核心卖点

**"512 怪零额外带宽"** — 纯 FrameSync，所有怪物/武器/碰撞在客户端确定性模拟，网络只传输玩家输入。

## 文件位置

- **Samples~**: `unity/com.boom.boomnetwork/Samples~/VampireSurvivors/`
- **测试副本**: `/Users/boom/Demo/BoomNetworkUnity/Untiy/Assets/Samples/BoomNetwork/0.1.0/Vampire Survivors Demo/`
- 测试阶段改 BoomNetworkUnity 副本，通过后 rsync 回写 Samples~

## 架构总览

```
Scripts/
├── Network/          网络层（不改 GameState，只传递）
│   ├── VSNetworkManager.cs   编排：帧事件 → InitPlayer, OnFrame → Tick
│   ├── VSInput.cs             输入编解码 4B（dirX:1 dirZ:1 ability:1 pad:1）
│   └── VSSnapshot.cs          快照序列化（slot index 保留）
├── Simulation/       确定性模拟（零 float，纯 FInt）
│   ├── FInt.cs                22.10 定点数（硬编码 SinTable，二进制 Sqrt）
│   ├── DeterministicRng.cs    LCG 随机数（ref uint state）
│   ├── GameState.cs           全部游戏状态 + 常量 + ComputeHash
│   ├── VSSimulation.cs        帧循环：ApplyInputs → WaveSystem → EnemySystem → WeaponSystem → Collision
│   ├── WaveSystem.cs          波次生成（Timer → spawn batch）
│   ├── EnemySystem.cs         AI：Zombie 追踪 / Bat 随机漫游 / Mage 远程
│   ├── WeaponSystem.cs        武器：Knife/Orb/Lightning/HolyWater + 升级
│   └── CollisionSystem.cs     空间哈希 20×20 + 碰撞检测
└── Rendering/
    └── VSRenderer.cs          程序化几何体渲染（零外部资源）
```

## 同步架构原则

**GameState 变更只通过两条确定性路径：**

```
1. 帧事件 OnPlayerJoined/Left → InitPlayer / deactivate（同帧）
2. OnFrame → Tick → ApplyInputs auto-init（首次输入）
```

**禁止在 OnFrameSyncStart/LoadSnapshot/OnJoinedRoom 中改 GameState。**
详见 [bn-desync SKILL](../bn-desync/SKILL.md)

## 帧循环（VSSimulation.Tick）

```
State.FrameNumber = frame.FrameNumber
ApplyInputs(frame)          ← auto-init 玩家 + 移动 + 升级选择
if (IsAnyPlayerUpgrading()) return   ← 全局暂停
WaveSystem.Tick              ← 波次 Timer + spawn
EnemySystem.Tick             ← AI 移动（Zombie/Bat/Mage）
WeaponSystem.Tick            ← 武器 cooldown + 发射 + Orb 旋转
CollisionSystem.CachePositions → Rebuild → Resolve
  ├── KnivesVsEnemies
  ├── OrbsVsEnemies
  ├── HolyPuddleVsEnemies
  ├── EnemiesVsPlayers
  ├── BoneShardsVsPlayers
  └── PlayersVsGems          ← XP + Level Up
InvincibilityFrames--
```

## 数据结构

| Struct | 容量 | 关键字段 |
|--------|------|---------|
| PlayerState | 4 | Pos, Facing, HP, Weapons×4, Orbs×5, Level, XP, PendingLevelUp |
| EnemyState | 512 | Pos, Dir, HP, Type, TargetPlayerId, BehaviorTimer |
| ProjectileState | 256 | Pos, Dir, Radius, Lifetime, OwnerPlayerId, DamageTick |
| XpGemState | 512 | Pos, Value |
| LightningFlash | 16 | Pos, FramesLeft |

## 添加新怪物

1. `GameState.cs`: 加 EnemyType 枚举值 + 速度/血量/伤害常量（用 `new FInt(raw)`）
2. `EnemySystem.cs`: 在 `Tick` switch 里加 case，写 `TickNewEnemy` 方法
3. `WaveSystem.cs`: 在 `PickEnemyType` 里加概率分支（注意 RNG 消费顺序）
4. `CollisionSystem.cs`: `GetEnemyRadius` / `GetEnemyDamage` 加 case
5. `VSRenderer.cs`: `DrawEnemy` 加颜色/形状
6. `VSSnapshot.cs`: EnemyState 已全字段序列化，无需改
7. `GameState.ComputeHash`: EnemyState 已全字段 hash，无需改

## 添加新武器

1. `GameState.cs`: 加 WeaponType 枚举值 + 常量
2. `WeaponSystem.cs`: 在 `TickPlayerWeapons` switch 加 case + Fire 方法
3. `CollisionSystem.cs`: 加碰撞 resolver（注意 IsAlive 守卫）
4. `VSSimulation.cs`: `UpgradePool` 数组加新武器
5. `VSNetworkManager.cs`: `WeaponNames` / `WeaponIcons` 加条目
6. `ProjectileType` 加枚举值（如果是投射物武器）

## 设计红线

| 红线 | 措施 |
|------|------|
| #1 不回滚 | 无预测/回滚 |
| #2 不要求理解回滚 | 用户只需 SendInput + OnFrame |
| #4 零延迟 | 本地输入下帧生效（auto-init） |
| #5 无事不发包 | Silent When Idle + _firstInputSent 例外 |
| 确定性 | FInt only, 硬编码常量, 帧事件路径 |
