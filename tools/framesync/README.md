# framesync — 帧同步 Desync 排查工具

## 目录结构

```
tools/framesync/
├── server.py    # HTTP 上报服务（端口 9877）
└── README.md    # 本文档
```

## 启动服务

```bash
# 前台（调试用）
python3 tools/framesync/server.py

# 后台常驻（推荐）
nohup python3 tools/framesync/server.py > /tmp/framesync_server.log 2>&1 &
echo "PID=$!"
```

服务启动后输出：

```
[framesync] Listening on http://0.0.0.0:9877
```

## 停止服务

```bash
# 按 PID 停止
kill <PID>

# 或按端口查找再停止
lsof -i :9877
kill <PID>
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
       └─ POST http://localhost:9877/desync
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
public const string ServerBase = "http://localhost:9877";
```

跨设备测试（手机 + PC）时改为服务器 IP，例如：

```csharp
public const string ServerBase = "http://192.168.1.100:9877";
```
