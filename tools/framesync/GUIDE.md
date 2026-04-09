# framesync 使用指南 — Desync 排查完整流程

## 适用场景

多客户端帧同步对局中出现"不同步"（Desync）时，用本工具采集双端数据，
自动计算 diff，定位分叉帧和根因子系统。

---

## 完整工作流

### 第 1 步：启动上报服务

```bash
bash tools/framesync/start.sh
```

输出 `http://localhost:9878` 即就绪。服务常驻，多次执行会自动跳过。

---

### 第 2 步：确认客户端配置

打开 `DesyncReporter.cs`，确认 `ServerBase` 指向正确地址：

| 场景 | 地址 |
|------|------|
| 双端同机（Editor + Editor） | `http://localhost:9878` |
| 双端跨设备（手机 + PC） | `http://<本机局域网IP>:9878` |

> 本机局域网 IP 查询：`ipconfig getifaddr en0`

---

### 第 3 步：复现 Desync

正常进行多人对局。出现不同步时：

- 游戏界面弹出 **DESYNC** 提示
- 双端自动将结构化数据 POST 到 `/desync`
- 本地同时落盘备份到 `pending_desync.jsonl`（防崩溃丢数据）

验证上报成功：

```bash
curl http://localhost:9878/desync/latest
```

返回非空数组说明数据已接收。

---

### 第 4 步：触发分析

**新开一个 Claude 会话**，执行：

```
/bn-desync-analyze
```

Claude 自动拉取 `/desync/groups`（预计算跨客户端 diff），输出分析报告。

---

### 第 5 步：根据报告排查

报告结构：

```
## Desync 分析报告

不同步帧: 399
触发时间: 2026-04-08 18:30:xx

### 根因定位
- 首次分叉帧: 312（来自 hash_history 对比）
- 分叉子系统: wave
- 具体差异: wr_a=2283 wr_b=1268 diff=1015

### 根因假设
WaveSpawnRemaining 分叉，差值 ≈ batchSize 整数倍，
疑似双端 HasAlivePlayers() false→true 转变帧号不同（置信度：高）

### 建议排查
1. 对比双端 [VS][Wave] HasAlivePlayers: false→true 的帧号
2. 检查 OnPlayerJoinedFrame 触发时序是否双端一致
```

---

## 分析后清空数据

复现下一次 Desync 前，清空旧数据避免干扰：

```bash
curl -X POST http://localhost:9878/clear
```

---

## 常见问题

**Q：启动脚本提示"服务已在运行"但 curl 超时？**

说明 9878 被其他进程占用（非 framesync）。强制替换：

```bash
kill $(lsof -ti :9878)
bash tools/framesync/start.sh
```

---

**Q：只有一个客户端上报，没有 diff？**

`/desync/groups` 的 `diff` 字段在 2+ 客户端上报同一帧时才自动计算。
确认双端都弹出了 DESYNC 提示，或手动查看 `/desync/latest` 确认条数。

---

**Q：日志里有数据但 hash_history 为空？**

客户端版本过旧，不含 hash_history 字段。确保 `VSNetworkManager.cs` 是最新版本（含 `_hashHistory` 环形缓冲区）。

---

## 快速命令速查

```bash
# 启动
bash tools/framesync/start.sh

# 查看原始事件（最近 10 条）
curl http://localhost:9878/desync/latest

# 查看分组+diff（分析用）
curl http://localhost:9878/desync/groups

# 查看控制台日志
curl http://localhost:9878/logs

# 清空所有数据
curl -X POST http://localhost:9878/clear

# 停止服务
kill $(lsof -ti :9878)
```
