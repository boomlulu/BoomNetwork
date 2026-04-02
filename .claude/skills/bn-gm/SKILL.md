---
name: bn-gm
description: "BoomNetwork Unity GM 工具。ServerWindow 5 Tab（Monitor/Messages/Rooms/Control/Deploy）、多环境切换、一键发布（本地+SSH）、Admin HTTP+WS。"
allowed-tools: ["Read", "Write", "Edit", "Bash", "Glob", "Grep", "Agent"]
---

# BoomNetwork GM 工具开发（Unity 端）

> **服务器端 Admin/GM 架构** → 见 [../bn-svr/reference/admin.md](../bn-svr/reference/admin.md)
> **网络模拟 (netsim)** → 见 [../bn-svr/reference/netsim.md](../bn-svr/reference/netsim.md)

## 适用场景
开发、扩展、调试 BoomNetwork 的 Unity Editor GM 工具。
包含 ServerWindow（5 Tab）、多环境切换、一键发布（本地 + 远程 SSH）。

---

## Unity 端文件索引

| File | 职责 |
|---|---|
| `unity/com.boom.boomnetwork.gm/Editor/AdminClient.cs` | HTTP fallback |
| `unity/com.boom.boomnetwork.gm/Editor/AdminWsClient.cs` | WebSocket 主通道（连接失败静默处理） |
| `unity/com.boom.boomnetwork.gm/Editor/MsgPackLite.cs` | MessagePack 编解码 |
| `unity/com.boom.boomnetwork.gm/Editor/GmWireTypes.cs` | 信封 + Push 结构体 |
| `unity/com.boom.boomnetwork.gm/Editor/ServerWindow.cs` | 5 Tab 面板 + 环境切换 |
| `unity/com.boom.boomnetwork.gm/Editor/DeployTool.cs` | 构建/部署/验证引擎 |
| `unity/com.boom.boomnetwork.gm/Editor/DeployProfile.cs` | 部署配置 |

---

## ServerWindow 5 Tab

```
状态栏（每个 Tab 顶部）:
  [◉ Dev Local ▼ | ☁ Tencent Prod ▼]  ● RUNNING  Rooms:0  Players:0  WS

Tab 0: Monitor
  - Traffic 统计（TX/RX/Players/Heap Sparkline 图表，Handles.DrawAAPolyLine）
  - Runtime Perf（Foldout，趋势箭头：GC/CPU/Goroutine）
  - Hot Players（Foldout，阈值高亮 + Kick 按钮）

Tab 1: Messages
  - 实时消息（WS 推送），最多保留 200 条
  - 筛选 Cmd + Hide Heartbeat + Pause + Copy

Tab 2: Rooms
  - 房间列表 + fps/MatchKey badge / SNAPSHOT PAUSED 警告
  - Foldout 深度检视：帧缓冲进度条 / 快照 / 权威表 / KV
  - Kick / Stop 按钮

Tab 3: Control
  - Network Simulation（Enable + Latency/Jitter/Loss 滑块 + Apply/Reset，S→C 注入）
  - Log Level 热调（不重启）
  - Config Reload
  - Start/Stop SSH（感知当前 Profile：Local=本地进程，Remote=SSH systemctl）
    + 远程时显示 user@host + systemd 服务名

Tab 4: Deploy
  - Profile 选择器（+ Local / + SSH / Del）
  - 字段：Name / Type / Config / Health URL / Admin Token / Health Timeout
  - Build Target：Local 自动检测，Remote 选 OS/Arch
  - Remote SSH：Host / Port / User / SSH Key / Remote Bin / Remote Cfg / Systemd
  - Deploy 按钮 + Stage 指示 + 日志面板（带颜色分级）
```

---

## 服务器环境切换器

状态栏左侧下拉，读取所有 DeployProfile，切换时自动更新 `_adminUrl` + `_adminToken` 并重连 WS。

- `◉` = Local，`☁` = RemoteSSH
- `ApplyServerProfile(idx)` — 切换逻辑
- `BuildServerSwitcher()` — 在 OnEnable / RefreshProfileNames / SaveDeployProfile 后刷新

---

## DeployProfile 数据模型

```
Name            string    "Dev Local" / "Tencent Prod"
Type            enum      Local | RemoteSSH
TargetOS        string    linux / darwin / windows
TargetArch      string    amd64 / arm64
ConfigFile      string    cmd/framesync/config.yaml
HealthUrl       string    http://127.0.0.1:9091
AdminToken      string    （每环境独立 Token）
HealthTimeoutSec int      15

— Remote SSH —
SshHost / SshPort / SshUser / SshKeyPath
RemoteBinaryPath / RemoteConfigPath / SystemdService
```

持久化：EditorPrefs，`BoomNetwork.GM.Deploy.profile.N.xxx`

---

## Deploy 流水线

```
Idle → Building → [Uploading] → Stopping → Starting → Verifying → Done/Failed
                  ↑ 仅 RemoteSSH
```

- Build：`GOOS/GOARCH/CGO_ENABLED=0 go build -ldflags "-X main.BuildHash -X main.BuildTime"`
- Upload：SSH 备份旧二进制 → SCP 新二进制 → chmod +x → SCP config
- Stop/Start：systemctl 或 nohup/pkill
- Verify：每 2s 轮询 `/health`，超时则 Failed

---

## Start/Stop SSH（Control Tab）

- 感知当前 Profile 类型，Remote 时走 SSH
- `RunSshAsync(profile, cmd)` — ThreadPool 后台执行，结果打到 Unity Console
- Stop 后设 `_stopCooldownUntil = now + 10s`，防止 health 轮询立即重连
- 冷却期内 `_health = default`，防止状态栏闪现 RUNNING
- Start 时清零 `_stopCooldownUntil`，允许立即重连

---

## 添加新端点（跨 Go + Unity）

1. Go: `admin.go` 写 `handleXxx`
2. Go: `startAdminServer` 注册 `mux.HandleFunc("/xxx", withAuth(token, handleXxx))`
3. Unity: `AdminClient.cs` 加 `FetchXxx()`
4. Unity: `ServerWindow.cs` 加 UI

---

## UPM 包

- 路径: `unity/com.boom.boomnetwork.gm/`
- **必须包含 .meta 文件**
- asmdef: `BoomNetwork.GM.Editor`，`includePlatforms: ["Editor"]`
- UPM URL: `https://github.com/luwenyiCC/BoomNetwork.git?path=unity/com.boom.boomnetwork.gm#dev1.0`
