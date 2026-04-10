# P1-04: playerCounter int32 溢出导致玩家 ID 冲突

**严重程度**: P1 — 高风险
**状态**: ✅ 已完成（2026-04-10）
**影响**: 长期运行 ~248 天后，玩家 ID 溢出为负数，映射表被覆盖，玩家 A 操作路由到玩家 B

---

## 根因

`svr/cmd/framesync/main.go:77`:

```go
var playerCounter int32
```

int32 最大值 2,147,483,647。假设每秒 100 个新连接，248 天溢出。溢出后 `atomic.AddInt32` 返回负数，与现有正数 playerId 不冲突，但继续递增后会再次到达 1, 2, 3... 与活跃玩家 ID 冲突。

## 涉及文件

| 文件 | 行号（约） | 改动类型 |
|------|-----------|----------|
| `svr/cmd/framesync/main.go` | 77, 454-456 | 类型升级 |
| `svr/cmd/framesync/main.go` | 所有 `playerId` 声明和使用 | 类型级联 |
| `svr/framesync/room.go` | Player.ID, AddPlayer 等 | 类型级联 |
| `svr/framesync/protocol.go` | 编解码函数 | 确认线上协议是 uint32 还是 int32 |

## 执行计划

### Step 1: 评估协议层的 playerId 宽度

搜索 `EncodePlayerId` 和 `DecodePlayerId`（或类似编码函数），确认网络协议中 playerId 用的是 4 字节还是 8 字节。如果协议是 4 字节（uint32），则服务端改为 int64 后需要确保在 4 字节范围内不会溢出，或者同时升级协议。

### Step 2: 如果协议为 4 字节（最可能）

保持协议不变，但将服务端 counter 改为 `int64`，并在 `nextPlayerId` 中加上范围保护：

```go
var playerCounter int64

func nextPlayerId() int32 {
    id := atomic.AddInt64(&playerCounter, 1)
    if id > math.MaxInt32 {
        // 安全策略：回绕或 panic，取决于业务需求
        // 回绕方案：
        atomic.StoreInt64(&playerCounter, 1)
        return 1
        // 或直接 panic，要求运维重启
    }
    return int32(id)
}
```

**注意**: 回绕方案存在 ID 冲突风险（如果 playerId=1 的玩家还在线）。更安全的做法是加一个已分配 ID 的检查。

### Step 3: 备选方案——定期维护重启

如果改动面太大或风险高，可以先在运维侧加一个告警：当 `playerCounter` 超过 20 亿时告警，并安排维护窗口重启。通过 `/stats` 端点的 `total_connections` 监控。

### Step 4: 验证

- `go vet ./...`
- `go test ./...`
- 搜索所有使用 `playerId` 的地方，确认类型一致
- 特别关注 `binary.LittleEndian.PutUint32` / `Uint32` 的编解码是否会截断

---

## 验收标准

### 编译与测试

- [ ] `go build ./...` 编译通过
- [ ] `go test ./...` 全部通过
- [ ] `grep -rn 'playerCounter' svr/` 确认类型已升级为 int64

### 功能验证

- [ ] **新增单元测试**: 模拟 playerCounter 到达 int32 边界（2,147,483,646），再调 nextPlayerId() 两次，确认不会返回负数或 0
- [ ] 协议层 playerId 编解码仍使用 uint32（4 字节），与客户端兼容
- [ ] 服务端 counter 的范围保护逻辑在达到 MaxInt32 时行为正确（重置或拒绝）

### 预期结果

| 场景 | 修复前 | 修复后 |
|------|--------|--------|
| 累计连接 < 21 亿 | 正常 | 正常（无行为变化） |
| 累计连接 = 2,147,483,647 | 下一个 ID 溢出为负数 | 安全回绕或告警拒绝 |
| 累计连接 > 21 亿 | ID 冲突，映射表被覆盖 | 不存在 ID 冲突 |

**核心指标**: 服务器可安全运行 365 天以上无需因 counter 溢出重启。
