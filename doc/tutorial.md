# 从零开始：5 分钟做一个多人游戏

> 本教程带你从 clone 到看到两个角色同步移动，每一步都有明确的操作。

## 前置条件

- Unity 2022.3+
- 一种方式启动服务器（任选一）：
  - Docker（推荐）
  - 或 Go 1.22+
  - 或直接下载 [二进制](https://github.com/boomlulu/BoomNetwork/releases)

---

## Step 1：启动服务器（1 分钟）

### 方式 A：Docker（最简单）

```bash
docker run -p 9000:9000 -p 9091:9091 ghcr.io/boomlulu/boomnetwork:latest
```

### 方式 B：下载二进制

从 [Releases](https://github.com/boomlulu/BoomNetwork/releases) 下载你平台的包，解压后：

```bash
./boomnetwork-server -config config.yaml
```

### 方式 C：源码

```bash
git clone https://github.com/boomlulu/BoomNetwork.git
cd BoomNetwork/svr
go run ./cmd/framesync/ -config cmd/framesync/config.yaml
```

看到这行输出说明成功：

```
[FrameSync Server] Running on :9000
```

**验证**：浏览器打开 `http://127.0.0.1:9091/health`，应返回 `{"status":"ok",...}`

<!-- 📸 截图占位：终端显示 "Running on :9000" -->

---

## Step 2：创建 Unity 项目（1 分钟）

1. Unity Hub → New Project → 3D (URP) → 命名 `MyMultiplayerGame`
2. 打开 `Window → Package Manager → + → Add package from git URL`
3. 输入：
   ```
   https://github.com/boomlulu/BoomNetwork.git?path=unity/com.boom.boomnetwork#dev1.0
   ```
4. 等待导入完成

<!-- 📸 截图占位：Package Manager 安装成功 -->

---

## Step 3：写游戏代码（2 分钟）

### 3.1 创建场景

1. 新建空场景 `MultiplayerTest`
2. 创建一个空 GameObject → 命名 `NetworkManager`
3. 给它添加 `BoomNetworkManager` 组件
4. Inspector 中设置：
   - Host: `127.0.0.1`（本地服务器）
   - Port: `9000`
   - Max Players: `4`
   - Auto Start: `true`（勾选）

<!-- 📸 截图占位：Inspector 中 BoomNetworkManager 组件设置 -->

### 3.2 创建玩家 Prefab

1. 创建 Cube → 命名 `Player`
2. 缩放为 (0.5, 1, 0.5)
3. 拖到 Project 创建 Prefab
4. 删除场景中的 Cube

### 3.3 创建游戏脚本

新建 C# 脚本 `MyGame.cs`，挂到 `NetworkManager` 上：

```csharp
using UnityEngine;
using BoomNetwork.Unity;
using BoomNetwork.Core.FrameSync;
using System.Collections.Generic;

public class MyGame : MonoBehaviour
{
    public GameObject playerPrefab;

    BoomNetworkManager _net;
    Dictionary<int, GameObject> _players = new();

    void Start()
    {
        _net = GetComponent<BoomNetworkManager>();

        // 有人加入 → 生成角色
        _net.Client.OnPlayerJoinedFrame += pid => SpawnPlayer(pid);  // 帧同步中触发，确定性
        // 自己加入也要生成
        _net.Client.OnReady += () => SpawnPlayer(_net.PlayerId);

        // 帧回调 — 核心同步逻辑
        _net.Client.OnFrame += OnFrame;

        // 一键启动：连接 → 匹配房间 → 开始帧同步
        _net.QuickStart();
    }

    void Update()
    {
        if (!_net.IsSyncing) return;

        // 采集输入（WASD）
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");

        // Silent When Idle：没输入就不发
        if (h == 0 && v == 0) return;

        // 编码输入为 2 字节
        byte[] input = new byte[] {
            (byte)(sbyte)(h * 127),
            (byte)(sbyte)(v * 127)
        };

        // 发送到服务器
        _net.SendInput(input);

        // Local-First：本地立即执行（零延迟）
        MovePlayer(_net.PlayerId, h, v);
    }

    // 帧回调：处理远程玩家的输入
    void OnFrame(FrameData frame)
    {
        foreach (var inp in frame.Inputs)
        {
            // 跳过自己（已在 Update 中执行）
            if (inp.PlayerId == _net.PlayerId) continue;

            // 解码远程输入
            float h = (sbyte)inp.Data[0] / 127f;
            float v = (sbyte)inp.Data[1] / 127f;
            MovePlayer(inp.PlayerId, h, v);
        }
    }

    void SpawnPlayer(int pid)
    {
        if (_players.ContainsKey(pid)) return;
        var go = Instantiate(playerPrefab, Vector3.zero, Quaternion.identity);
        go.name = $"Player_{pid}";
        // 不同玩家不同颜色
        go.GetComponent<Renderer>().material.color =
            pid == _net.PlayerId ? Color.green : Color.red;
        _players[pid] = go;
    }

    void MovePlayer(int pid, float h, float v)
    {
        if (!_players.TryGetValue(pid, out var go)) return;
        go.transform.position += new Vector3(h, 0, v) * 5f * Time.deltaTime;
    }
}
```

### 3.4 连接 Prefab

在 Inspector 中把 `Player` Prefab 拖到 `MyGame` 的 `playerPrefab` 字段。

<!-- 📸 截图占位：MyGame Inspector 设置 -->

---

## Step 4：运行（30 秒）

1. 确保服务器还在运行
2. 点 Play
3. 用 WASD 移动绿色方块
4. 你会看到一个绿色方块（本地玩家）可以移动

**要看到多人同步**，有两种方式：

### 方式 A：Build + Editor 双开

1. `File → Build Settings → Build` 出一个独立运行的 exe/app
2. 运行 Build 版本
3. 同时 Unity Editor 点 Play
4. 两个窗口各一个绿色方块（本地）和一个红色方块（远程）

### 方式 B：ParrelSync（推荐）

1. 安装 [ParrelSync](https://github.com/VeriorPies/ParrelSync)
2. 用它打开一个克隆 Editor
3. 两个 Editor 同时 Play

<!-- 📸 截图占位：两个角色同步移动 -->

---

## 发生了什么？

```
你按 W                           对方按 D
  │                                │
  ▼                                ▼
SendInput([0, 127])              SendInput([127, 0])
  │                                │
  ▼                                ▼
──────────── Go 服务器 ────────────
  │ 20fps tick: 收集输入 → 组帧 → 广播
  ▼
PushFrames(frame #42: [{P1: 0,127}, {P2: 127,0}])
  │                                │
  ▼                                ▼
OnFrame:                         OnFrame:
  P1 = 自己 → 跳过                 P1 = 远程 → 移动红色方块
  P2 = 远程 → 移动红色方块          P2 = 自己 → 跳过
```

**关键点：**
- 自己的输入**立即执行**（Update 里 MovePlayer），零延迟
- OnFrame 里**跳过自己**（Local-First），只执行远程玩家的输入
- 没输入时**不发包**（Silent When Idle）

---

## 下一步

| 想做什么 | 看哪个 Sample |
|---------|-------------|
| 房间列表/手动加入 | `RoomLobby` |
| 断线重连 | `Reconnect` |
| 实体权威同步（开放世界） | `EntitySync` |
| 聊天/社交 | `ChatRoom` |
| 多人方块世界 | `MinecraftDemo` |
| 弹幕生存 | `VampireSurvivors` |

安装 Samples：`Package Manager → BoomNetwork → Samples → Import`

---

## 遇到问题？

- 服务器连不上 → 确认 `http://127.0.0.1:9091/health` 能访问
- 只有一个玩家 → 需要两个客户端同时运行
- [GitHub Discussions](https://github.com/boomlulu/BoomNetwork/discussions) 提问
