# P1-08: frameHashes map 无限增长

**严重程度**: P1 — 高风险
**状态**: ✅ 已完成（2026-04-10）
**影响**: 长时间运行的房间，frameHashes 内存持续膨胀

---

## 根因

`room.go:174`:

```go
frameHashes map[uint32]map[int32]uint32 // frameNumber → playerId → hash
```

每帧的 hash 被收集后从未清理。30 分钟房间 @ 20fps = 36000 个 map 条目，每条 ≈ 100+ bytes（map 头 + 2-4 个 playerId→hash 条目），总计 ~3.6MB。多房间叠加后可观。

## 涉及文件

| 文件 | 行号（约） | 改动类型 |
|------|-----------|----------|
| `svr/framesync/room.go` | stepFrame 或 hash 收集逻辑 | 增加清理 |

## 执行计划

### Step 1: 找到 hash 收集的位置

搜索 `frameHashes` 的写入点，确认 hash 是在哪个函数中被存入的（可能在 `handleFrameHash` 调用的 room 方法中）。

### Step 2: 在写入时清理过期条目

在 hash 存入后，删除超过 N 帧之前的旧条目。N 取 200（10 秒 @ 20fps，足够检测 desync）:

```go
func (r *Room) RecordFrameHash(frameNumber uint32, playerId int32, hash uint32) {
    r.mu.Lock()
    defer r.mu.Unlock()

    if r.frameHashes[frameNumber] == nil {
        r.frameHashes[frameNumber] = make(map[int32]uint32)
    }
    r.frameHashes[frameNumber][playerId] = hash

    // 清理超过 200 帧前的旧数据
    const maxHashHistory = 200
    if frameNumber > maxHashHistory {
        cutoff := frameNumber - maxHashHistory
        for fn := range r.frameHashes {
            if fn < cutoff {
                delete(r.frameHashes, fn)
            }
        }
    }
}
```

### Step 3: 优化——避免每次写入都遍历

如果 hash 写入频率很高，可以改为每 100 帧清理一次：

```go
if frameNumber%100 == 0 && frameNumber > maxHashHistory {
    // 清理
}
```

### Step 4: 验证

- `go test ./...`
- 添加测试：模拟 1000 帧 hash 写入，确认 map 大小始终 ≤ 200 + buffer

---

## 验收标准

### 编译与测试

- [ ] `go build ./...` 编译通过
- [ ] `go test ./...` 全部通过

### 功能验证

- [ ] **新增单元测试**: `TestFrameHashesCleanup`
  - 写入 500 帧 hash
  - 确认 `len(frameHashes)` ≤ maxHashHistory（200）+ 容差
  - 确认最近 200 帧的 hash 仍可查询
  - 确认 201 帧前的 hash 已被清理
- [ ] desync 检测功能仍然正常工作（已有的 desync 相关测试通过）

### 预期结果

| 场景 | 修复前 | 修复后 |
|------|--------|--------|
| 房间运行 30 分钟（36000 帧） | frameHashes 积累 36000 条 (~3.6MB) | 始终 ≤ 200 条 (~20KB) |
| desync 检测 | 可检测任意历史帧 | 只能检测最近 200 帧（10 秒窗口，足够） |

**核心指标**: 每个房间的 frameHashes 内存消耗恒定，不随运行时间增长。
