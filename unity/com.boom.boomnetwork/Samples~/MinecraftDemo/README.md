# Minecraft Demo — BoomNetwork Sample

多人方块世界 Demo，展示 BoomNetwork 的核心能力：

- **Entity Authority Sync** — 玩家移动零延迟，远端平滑插值
- **Frame Sync** — 方块放置/破坏确定性同步，所有客户端状态一致
- **Snapshot** — 断线重连恢复世界状态

## 快速开始

1. 空场景，创建空 GameObject
2. 添加组件：`BoomNetworkManager` + `MinecraftNetworkManager` + `MinecraftDemoBootstrap`
3. 设置 `BoomNetworkManager.MatchKey = "minecraft"`
4. Play → 自动连接、建房、生成世界
5. 开第二个客户端，两人共享方块世界

## 操作

| 按键 | 功能 |
|------|------|
| WASD | 移动 |
| Space | 跳跃 |
| 鼠标 | 视角 |
| 左键 | 破坏方块 |
| 右键 | 放置方块 |
| 1-7 | 选择方块类型 |
| 滚轮 | 切换方块 |
| Esc | 释放鼠标 |

## 技术架构

```
混合同步模式:
  玩家移动 → IEntitySync (自权威，20fps 状态广播)
  方块操作 → SendInput → OnFrame (帧同步，确定性执行)
  世界恢复 → Snapshot (delta-only，RLE 压缩)
```

## 世界参数

- 大小: 128×64×128 blocks (8×4×8 chunks)
- Chunk: 16³ blocks
- 地形: 2D Simplex Noise 高度图
- 方块: Stone/Dirt/Grass/Wood/Leaf/Sand/Water (7种)

## 体素引擎

- Greedy Meshing — 合并相邻同类面为大四边形，减少 draw call
- Procedural Atlas — 运行时生成方块贴图集，无需外部资源
- Chunk Streaming — 按玩家位置流式加载/卸载 chunks

## Credits

Voxel engine inspired by [Minecraft4Unity](https://github.com/paternostrox/Minecraft4Unity) (MIT License).
