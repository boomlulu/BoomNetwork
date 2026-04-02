---
name: demo-minecraft
description: "BoomNetwork MinecraftDemo Sample 完整架构。覆盖体素引擎、混合同步（帧同步+实体同步+快照）、Voxel AABB 物理、零 GC 优化、设计哲学合规。用于修改/扩展 MinecraftDemo 或创建类似 Demo。"
allowed-tools: ["Read", "Write", "Edit", "Bash", "Glob", "Grep", "Agent"]
---

# MinecraftDemo — BoomNetwork 多人方块世界 Sample

## 概述

多人联机方块世界 Demo，展示 BoomNetwork 三种同步模式的混合使用。
创造模式：放置/破坏方块 + FPS 移动 + 多人实时同步。

## 文件位置

- **源码（Samples~）**: `unity/com.boom.boomnetwork/Samples~/MinecraftDemo/`
- **测试副本（BoomNetworkUnity）**: `/Users/boom/Demo/BoomNetworkUnity/Untiy/Assets/Samples/BoomNetwork/0.1.0/Minecraft Demo/`
- **测试阶段改 BoomNetworkUnity 副本，验收通过后回写 Samples~**

## 架构总览

```
┌──────────────────────────────────────────────────┐
│              MinecraftDemoBootstrap               │  场景入口，一键搭建
│  → matchKey + lighting + VoxelWorld + highlight   │
├──────────────────────────────────────────────────┤
│           MinecraftNetworkManager                │  网络编排层
│  SendBlockAction → SendInput (帧同步)             │
│  SendEntityState → IEntitySync (位置同步)         │
│  TakeSnapshot → dirty tracking (零 GC)           │
├────────────────┬─────────────────────────────────┤
│ PlayerController│     VoxelWorld                  │
│ Voxel AABB 物理│  Chunk 管理 + Mesh 队列         │
│ 方块射线交互  │  GetBlock / SetBlock             │
├────────────────┴─────────────────────────────────┤
│              ChunkMeshBuilder                     │  Greedy Meshing (静态 buffer, 零 GC)
├──────────────────────────────────────────────────┤
│  ChunkData │ ChunkRenderer │ WorldGenerator       │  数据 / 渲染 / 噪声生成
├──────────────────────────────────────────────────┤
│  BlockType │ VoxelConstants │ SimplexNoise         │  基础类型 / 常量 / 数学
└──────────────────────────────────────────────────┘
```

## 文件清单

### Scripts/ — 网络 + 游戏逻辑

| 文件 | 职责 |
|------|------|
| `MinecraftDemoBootstrap.cs` | 场景入口：targetFrameRate=60, matchKey, lighting, VoxelWorld 创建 |
| `MinecraftNetworkManager.cs` | 网络编排：帧同步方块 + 实体位置 + Snapshot + UI(OnGUI) |
| `MinecraftPlayerController.cs` | FPS 控制器：Voxel AABB 碰撞, 射线方块选取, 跳跃 |
| `MinecraftPlayerSync.cs` | IEntitySync 实现：16 字节 (xyz+rotY), 远端插值 |
| `MinecraftInput.cs` | 帧同步输入编解码：8 字节 (actionType+pos+blockType) |
| `MinecraftSnapshot.cs` | 零 GC 快照：dirty block tracking + 预分配 buffer |
| `MinecraftBlockHighlight.cs` | 线框高亮：LineRenderer 显示选中方块 |

### Voxel/ — 体素引擎

| 文件 | 职责 |
|------|------|
| `BlockType.cs` | 8 种方块枚举 + IsSolid/IsTransparent |
| `VoxelConstants.cs` | Chunk 16³, World 8×4×8, 坐标转换工具 |
| `SimplexNoise.cs` | 2D Simplex + FBM 噪声 |
| `ChunkData.cs` | 16³ BlockType 数组容器 |
| `WorldGenerator.cs` | 噪声高度图地形 + GetBlockAt 单点查询 |
| `ChunkMeshBuilder.cs` | Greedy Meshing (静态 mask/visited, 零 GC) |
| `ChunkRenderer.cs` | Mesh + MeshCollider MonoBehaviour 包装 |
| `VoxelWorld.cs` | 世界管理：chunk 字典 + 流式加载 + mesh 队列 |
| `ProceduralBlockAtlas.cs` | 运行时生成 URP 顶点色材质 |
| `BlockAtlas.shader` | URP HLSL shader (atlas + vertex color + 光照) |

## 同步模式

```
方块操作 → SendInput (帧同步)
  - 保证确定性执行顺序
  - Red Line #4: 本地立即执行 + OnFrame 幂等确认
  - Silent When Idle: 无操作不发包

玩家位置 → IEntitySync (实体权威同步)
  - 自权威零延迟
  - 变化阈值: pos>0.01, rot>0.5°
  - OnPlayerJoined 时主动广播一次 (late-joiner 例外)

世界恢复 → Snapshot (dirty tracking)
  - 只追踪修改过的方块 (Dictionary<int3, BlockType>)
  - TakeSnapshot: 预分配 buffer, 仅 result byte[] 有 GC
  - LoadSnapshot: seed → 重生世界 → 应用 deltas
```

## 不同步防御

修改方块同步（帧同步路径）或 Snapshot 时，须遵守 [bn-desync SKILL](../bn-desync/SKILL.md) 检查清单。
MinecraftDemo 特有注意点：dirty tracking 的 Dictionary 仅用于 Snapshot 序列化，不在 OnFrame 内遍历（安全）。

## 设计红线遵守情况

| 红线 | 措施 |
|------|------|
| #1 不回滚 | 无预测/回滚代码 |
| #2 不要求理解回滚 | 用户只需 SendInput + OnFrame |
| #3 接入简洁 | public setter 注入, 无反射 |
| #4 零延迟 | 本地立即 ApplyBlockAction, OnFrame 幂等 |
| #5 无事不发包 | SendInput 仅有操作时, EntityState 仅位置变化时 |

## 物理系统

**不使用 CharacterController / Rigidbody**，用 Voxel AABB 碰撞：

```
每帧:
  1. Ground check (脚下 0.05 格有 solid block?)
  2. 输入 → 水平速度, 重力/跳跃 → 垂直速度
  3. 分轴移动: Y → X → Z
  4. MoveAxis: 试移动 → CollidesWithWorld? → snap 到方块边
```

- `CollidesWithWorld`: 扫描玩家 AABB 覆盖的所有 block cell
- 直接查 `VoxelWorld.GetBlock()`，不依赖 Unity 物理
- 方块放置拒绝: `OverlapsPlayerBody` 检查 AABB 重叠

## 性能优化

| 优化点 | 措施 |
|--------|------|
| Mesh 构建 | 静态 mask[]/visited[] + Array.Clear 复用 |
| Mesh 队列 | HashSet 辅助 O(1) Contains |
| 世界加载 | GenerateFullWorld 同步构建 + GC.Collect |
| Snapshot | dirty tracking 替代全量对比 (1.6MB→几十字节) |
| 委托 | _getBlockCached 缓存避免 per-call 分配 |
| UI | GUIStyle/string[] 缓存, 非每帧 new |
| 集合 | _chunksToKeep/_toRemove 字段级复用 |

## 玩家视觉

- transform.position.y = 脚底 (物理位置)
- Capsule mesh 作为子对象, localPosition.y = 0.9 (半身高偏移)
- 本地和远程玩家都用此结构

## 扩展指南

### 加新方块类型
1. `BlockType.cs`: 枚举加值, `BlockTypeExt.Count` 更新
2. `ChunkMeshBuilder.GetBlockFaceColor`: 加颜色
3. `WorldGenerator.GenerateChunk` / `GetBlockAt`: 加生成规则

### 加多人聊天
用 `SendStateMessage` — 不走帧同步，纯广播

### 加持久化
存 `MinecraftSnapshot.s_dirtyBlocks` 到文件，LoadSnapshot 时恢复
