# framesync — 帧同步 Desync 排查工具

## 目录结构

```
tools/framesync/
├── server.py    # HTTP 上报服务（端口 9878）
├── start.sh     # 启动脚本（自动检测重复启动）
└── README.md    # 本文档
```

## 启动服务

```bash
# 推荐：自动检测是否已在运行，避免端口冲突
bash tools/framesync/start.sh

# 前台调试
python3 tools/framesync/server.py
```

服务启动后输出：

```
[framesync] 已启动 PID=12345 → http://localhost:9878
```

已在运行时输出：

```
[framesync] 服务已在运行 (PID 12345)，跳过启动
```

## 停止服务

```bash
kill $(lsof -ti :9878)
```

## 端点说明

| 方法 | 路径 | 说明 |
|------|------|------|
| `POST` | `/desync` | 客户端上报 Desync 事件（JSON） |
| `POST` | `/log` | 客户端上报控制台日志 |
| `GET` | `/desync/groups` | 按帧分组 + 跨客户端预计算 diff（**分析首选**） |
| `GET` | `/desync/latest` | 最近 10 条原始 Desync 事件 |
| `GET` | `/logs` | 全部控制台日志 |
| `POST` | `/clear` | 清空所有数据 |

## 数据流

```
Unity Client
  │  OnDesyncDetected
  └→ DesyncReporter.Report(json)
       ├─ 落盘 pending_desync.jsonl（崩溃保护）
       └─ POST http://localhost:9878/desync
              │
              ▼
         server.py
           ├─ 按 frame 分组到 desync_groups
           └─ 2+ 客户端同帧上报时自动计算 cross-client diff
              │
              ▼
         GET /desync/groups
              │
              ▼
         bn-desync-analyze skill（分析会话）
```

## 分析流程

1. 启动服务（见上）
2. 双端游戏复现 Desync
3. 切换 Claude 会话，执行 `/bn-desync-analyze`

## 客户端配置

客户端上报地址在 `DesyncReporter.cs`：

```csharp
public const string ServerBase = "http://localhost:9878";
```

跨设备测试（手机 + PC）时改为服务器 IP，例如：

```csharp
public const string ServerBase = "http://192.168.1.100:9878";
```
