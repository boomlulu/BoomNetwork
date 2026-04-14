# Reconnect Storm 根因分析（2026-04-15）

**Status:** Bug 1+2 已修复（`5a8cf34`）；诊断日志已补全（`03af608` + rate limit Room LogEvent + BurstDiag in-progress）；待第三次测试验证

---

## 症状

VS Demo 多人联机，游戏启动约 8-10 秒后进入 reconnect storm：
- 重连成功后 ~37ms 内立即再次断线，循环往复
- `[AllStrategiesExhausted]` 每 ~1s 触发一次
- 服务器在 frame ~400 停止推帧（`paused=true`）
- 用户预期游戏应运行到 ~3392 帧

---

## 完整因果链

```
[frame ~160, 23:31:06]
  初次断线 ← 原因待 Possible A/B 日志确认（见下）

[23:31:07~18] Reconnect Storm
  ┌─ QuickReconnect 成功（serverFrame=303）
  ├─ deliveryLoop Phase1 补发 ~160 帧（burst）
  ├─ 客户端同一 Unity Tick 内收到 160 帧
  ├─ HandlePushFrames × 160 → SendFrameHash × 160（HashThrottleMs=0，无节流）
  ├─ 160 条 Ext(60)/FrameHash 在 37ms 内涌入服务器
  ├─ IPRateLimiter(100/sec) → RateLevelWarn → RateLevelDeny → 关闭连接
  ├─ 客户端 DisconnectEvent → 再次重连 → 再次 burst → 循环
  └─ AllStrategiesExhausted 后 CM state=Disconnected
     → TCP close 事件绕过 "Already reconnecting" 保护
     → 立即重启新一轮 reconnect（bug）

[frame 400, 23:31:28]
  snapshotStaleFrames 累积到 300（= snapshotIntervalFrames×3 = 100×3）
  ← storm 期间无玩家处于稳定在线状态，快照无法上传
  → stepFrame 触发 snapshotPaused=true
  → 帧号停在 400，不再推帧
```

---

## 两个根本 Bug

### Bug 1：HashThrottleMs=0，补帧时 FrameHash 无节流（主因）

- **文件:** `unity/com.boom.boomnetwork/Runtime/Client/FrameSync/FrameSyncClient.cs:206`
- **现象:** `HandlePushFrames` 对每帧调用 `SendFrameHash`；补帧时同一 Tick 收到 N 帧 → N 条 FrameHash → rate limit
- **修复:** `HashThrottleMs=100`（稳态 10 msg/sec）+ 补帧期间只发最后一帧 hash

### Bug 2：AllStrategiesExhausted 后 CM 无限重启（放大器）

- **文件:** `unity/com.boom.boomnetwork/Runtime/Client/Connection/ConnectionManager.cs:304-312`
- **现象:** `onFail` 将状态置为 `Disconnected`；后续 TCP close 事件（来自 rate limit 杀连接）绕过 `Reconnecting` 保护 → 立即重启 reconnect
- **修复:** `onFail` 中设置 `_intentionalDisconnect=true`，防止后续 TCP close 事件重启 reconnect

---

## 待确认：初次断线原因

frame ~160（23:31:06）的初次断线原因有两种假设，需下次测试通过新增日志确认：

| 假设 | 确认方式 | 日志来源 |
|------|---------|---------|
| **Possible A** 服务端 tickLoop 被 deliveryLoop Phase1 flood 拖慢 → write timeout | Room log: `tickLoop jitter elapsed_ms>>100` 紧随 `deliveryLoop phase1 done` | `/rooms/logs/{id}` |
| **Possible B** 升级 Pause/Resume 调用过于密集触发 rate limit | Server slog: `rate limit extCmd=58/59`；VS channel: `[Pause]` 调用频率 | slog + `9878/VS` |

---

## 诊断日志（commit 03af608）

### 服务端新增

| 日志 | 触发条件 | 日志位置 |
|------|---------|---------|
| `tickLoop jitter elapsed_ms=X expected_ms=50` | tick 间隔 > 2× 预期（100ms） | Room LogEvent + slog |
| `deliveryLoop phase1 start count=N from_frame=A to_frame=B` | 任意重连追帧开始 | Room LogEvent + slog |
| `deliveryLoop phase1 done count=N elapsed_ms=X` | Phase1 追帧完成 | Room LogEvent + slog |
| `client rate limit warn/deny extCmd=60` | FrameHash 触发速率限制 | slog (ws/kcp) |
| `client rate limit warn/deny extCmd=58/59` | Pause/Resume 触发速率限制 | slog (ws/kcp) |

### 客户端新增

| 日志 | 触发条件 | Channel |
|------|---------|---------|
| `[BurstDiag] catchup burst: frames=N hashes=M hashThrottleMs=X` | 单 Tick 处理 >1 帧时下一 Tick 初始报告 | FrameSync |
| `[Pause] RequestGamePause frame=N` | OnFrame 内检测到升级暂停 | VS |
| `[Pause] RequestGameResume frame=N` | OnFrame 内检测到升级恢复 | VS |
| `[Pause] RequestGameResume (upgrade deadlock-breaker)` | Update() 内升级选择发送后的解锁 | VS |

---

## 拉取命令（测试时用）

```bash
# 1. 确认 burst
curl -s "http://localhost:9878/client-logs?channel=FrameSync&limit=500" \
  | python3 -c "import json,sys; [print(e['msg']) for e in json.load(sys.stdin) if 'BurstDiag' in e['msg']]"

# 2. 确认 rate limit 触发消息类型（SSH 到服务器看 slog）
ssh -i ~/.ssh/tencent_wepie.pem ubuntu@124.220.6.174 \
  "journalctl -u boomnetwork --no-pager -n 200 | grep 'rate limit'"

# 3. 确认 tickLoop jitter + Phase1 时序
curl -s "http://124.220.6.174:9091/rooms/logs/1?limit=500" \
  | python3 -c "
import json,sys,datetime
d=json.load(sys.stdin)
for e in reversed(d['logs']):
    if any(k in e['msg'] for k in ['jitter','phase1','rate']):
        t=datetime.datetime.fromtimestamp(e['ts']/1000).strftime('%H:%M:%S.%f')[:-3]
        attrs=' '.join(f'{k}={v}' for k,v in (e.get('attrs') or {}).items())
        print(f'[{t}] {e[\"level\"]:5} {e[\"msg\"]}  {attrs}')
"

# 4. 确认 Pause/Resume 频率
curl -s "http://localhost:9878/client-logs?channel=VS&limit=500" \
  | python3 -c "import json,sys; [print(e['msg']) for e in json.load(sys.stdin) if '[Pause]' in e['msg']]"
```

---

## 修复方案（已实施）

### Fix 1（根本）`FrameSyncClient.cs`
- `HashThrottleMs = 0f` → `100f`（节流 100ms = 稳态 10 msg/sec）
- 效果：补帧 burst（同一 Tick 收到 N 帧）时，节流器保证整个 Tick 只发 1 条 FrameHash；N 条 → 1 条，彻底消除 rate limit 触发

### Fix 2（截断）`ConnectionManager.cs`
- `onFail` 回调中添加 `_intentionalDisconnect = true`，位于 `TransitionTo(State.Disconnected)` 之前
- 效果：AllStrategiesExhausted 后，后续服务器 rate-limit 杀连接产生的 TCP close 事件进入 `HandleSessionDisconnected` → 检测到 `_intentionalDisconnect=true` → 直接 return，不再重启 reconnect
