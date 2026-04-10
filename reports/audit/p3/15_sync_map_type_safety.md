# P3-15: sync.Map 缺乏编译期类型检查

**严重程度**: P3 — 低风险
**状态**: 待排期
**影响**: 直接 Store/Load 容易传错类型，运行时 panic

---

## 根因

`main.go:51-53`:
```go
var connPlayerMap sync.Map // connID → int32(playerId)
var playerRoomMap sync.Map // int32(playerId) → *Room
var playerConnMap sync.Map // int32(playerId) → *transport.Conn
```

虽然有辅助函数 `loadConnPlayerId` 等做了类型安全断言，但 Store 调用方仍可直接用错误类型写入。

## 执行计划

### 长期方案: 用泛型 map + sync.RWMutex 替代

Go 1.18+ 支持泛型，可以提供编译期类型安全：

```go
type SyncMap[K comparable, V any] struct {
    mu sync.RWMutex
    m  map[K]V
}
```

或使用 `sync.Map` 的泛型包装。

改动面较大，建议在其他重构中逐步替换。当前有辅助函数保护，风险可控。

### 验证

- 全量测试

---

## 验收标准

- [ ] 新的泛型 SyncMap 有完整的单元测试
- [ ] 编译器能在 Store 传入错误类型时报错
- [ ] 性能基准（benchmark）不低于原 sync.Map

**核心指标**: 消除运行时类型断言 panic 的可能性。长期任务，可分批替换。
