# GM 工具迭代 TODO

> 基于 gap 分析 + WebSocket 传输层升级后的状态

---

## 已完成 ✅

| # | 任务 | 完成日期 |
|---|------|---------|
| G0 | **WebSocket + MessagePack 传输层** | 2026-03-25 |
|    | Go: gorilla/websocket + vmihailenco/msgpack, Hub 订阅推送 | |
|    | Unity: AdminWsClient (后台线程) + MsgPackLite (零依赖) | |
|    | ServerWindow: WS queue drain + HTTP fallback | |
| G5 | **Cmd 27/28 名称映射** | 2026-03-25 |
| G6 | **ServerWindow Cmd 过滤器补 27/28** | 2026-03-25 |

---

## P0 — 补齐已有端点的 Unity 覆盖

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| G1 | **ServerWindow UI: /perf** | WS 已推送 perf topic → 加 Dashboard 展示 | 待做 |
| G2 | **ServerWindow UI: /rates** | WS 已推送 rates topic → 加 UI 展示（标红异常速率） | 待做 |
| G3 | **ServerWindow UI: /players/{pid}** | 需加 RPC 或 HTTP 端点调用 + 玩家详情面板 | 待做 |
| G4 | **MsgEntry.detail 字段补全** | HTTP AdminClient.MsgEntry 缺 Detail 字段（WS 侧已有 GmMsgEntry.Detail） | 待做 |

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
| G13 | **ParseMsgArray 健壮性** | HTTP fallback 路径的 JSON 解析仍脆弱（WS 路径用 MsgPackLite 已无此问题） | 低优先 |
| G14 | **ParseRoom 同上** | HTTP fallback 路径 | 低优先 |
| G15 | **请求失败 UI 提示** | HTTP fallback 模式下静默返回空值（WS 模式有 err 信封推送） | 待做 |
| G16 | ~~按需轮询~~ | **已解决**: WS 模式下按订阅推送，不再轮询 | ✅ |
| G17 | **StopServer 跨平台** | 当前 osascript macOS-only，加 Windows (taskkill) + Linux (kill) 路径 | 待做 |
| G18 | **disconnect_time 展示** | Rooms Tab 玩家 OFFLINE 时显示断线时间戳（从 /players/{pid} 或扩展 /rooms 响应） | 待做 |

---

## 建议迭代顺序

```
第一轮（WS 数据已有，只差 UI）:
  G1 + G2 → G7（Performance Tab）→ G4
  perf/rates 数据已通过 WS push，只需在 ServerWindow 加展示

第二轮（玩家详情）:
  G3 → G8
  点击玩家 → 弹出详情面板

第三轮（新服务器能力）:
  G10 → G9 → G11 → G12
  暂停房间 > 看日志 > 广播 > 热更新

第四轮（打磨）:
  G13-G18 按需穿插（G16 已解决）
```
