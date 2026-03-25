# GM 工具迭代 TODO

> 基于 9 端点现状的 gap 分析，按优先级排列

---

## P0 — 补齐已有端点的 Unity 覆盖

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| G1 | **AdminClient + UI: /perf** | 加 `FetchPerf()` + `PerfResult` 结构体 + ServerWindow 展示（Dashboard 或新 Tab） | 待做 |
| G2 | **AdminClient + UI: /rates** | 加 `FetchRates()` + `PlayerRateInfo[]` + ServerWindow 展示（标红异常速率） | 待做 |
| G3 | **AdminClient + UI: /players/{pid}** | 加 `FetchPlayerDetail(pid)` + `PlayerDetailResult` + 点击玩家弹出详情面板 | 待做 |
| G4 | **MsgEntry.detail 字段补全** | AdminClient.MsgEntry 缺 Detail 字段，ParseMsgArray 未读取。Messages Tab 静默丢弃 payload 摘要 | 待做 |
| G5 | **Cmd 27/28 名称映射** | stats.go `cmdNames` 缺 CmdSendEntityState(27) / CmdPushEntityState(28)，消息日志显示 "Unknown" | 待做 |
| G6 | **ServerWindow Cmd 过滤器补 27/28** | BuildCmdFilter() 下拉菜单未包含实体同步命令 | 待做 |

## P1 — 新增能力

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| G7 | **ServerWindow 第四 Tab: Performance** | 合并 /perf + /rates 到专属 Tab，goroutine/heap/GC 指标 + 玩家速率 Top 20 表格 | 待做 |
| G8 | **玩家详情面板** | Rooms Tab 点击玩家 → 弹出浮窗或侧边栏，显示 /players/{pid} 的完整状态 + 最近 20 条消息 | 待做 |
| G9 | **服务器日志端点** | 新端点 GET /logs — 服务器 stderr/stdout 最近 N 行。ServerWindow 内直接看日志而不切终端 | 待做 |
| G10 | **房间暂停/恢复** | 新端点 POST /rooms/pause/{id} + /rooms/resume/{id}，调试时冻结帧推送 | 待做 |
| G11 | **广播公告** | 新端点 POST /broadcast — 向所有玩家推送系统消息（Cmd 待定） | 待做 |
| G12 | **配置热更新** | 新端点 POST /config — 运行时修改 frameRate/maxPlayers 等参数，不重启服务器 | 待做 |

## P2 — 质量 & 体验优化

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| G13 | **ParseMsgArray 健壮性** | 当前用 IndexOf('{'/'}') 切分 JSON 对象，detail 含嵌套大括号会炸。改用状态机或正则 | 待做 |
| G14 | **ParseRoom 同上** | players 子数组解析同样脆弱 | 待做 |
| G15 | **请求失败 UI 提示** | FetchHealth/Stats/Messages/Rooms 失败时静默返回空值，无错误提示。应在 ServerWindow 显示错误横幅 | 待做 |
| G16 | **按需轮询** | 当前 /stats + /health 每 2s 无条件轮询。切到 Messages Tab 时不需要刷 stats，切到 Dashboard 时不需要刷 messages | 待做 |
| G17 | **StopServer 跨平台** | 当前 osascript macOS-only，加 Windows (taskkill) + Linux (kill) 路径 | 待做 |
| G18 | **disconnect_time 展示** | Rooms Tab 玩家 OFFLINE 时显示断线时间戳（从 /players/{pid} 或扩展 /rooms 响应） | 待做 |

---

## 建议迭代顺序

```
第一轮（补齐缺口，1-2 天）:
  G5 + G6 → G4 → G1 → G2 → G3
  先修数据层（Cmd 名称 + detail 字段），再加三个缺失端点的 Unity 封装

第二轮（新 Tab + 面板）:
  G7 → G8
  有了 P0 的 AdminClient 方法后，加 UI 展示

第三轮（新服务器能力）:
  G10 → G9 → G11 → G12
  按调试价值排序：暂停房间 > 看日志 > 广播 > 热更新

第四轮（打磨）:
  G13-G18 按需穿插
```
