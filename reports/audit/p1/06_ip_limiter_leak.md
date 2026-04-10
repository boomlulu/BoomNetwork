# P1-06: IPRateLimiter sync.Map 永不清理导致内存泄漏

**严重程度**: P1 — 高风险
**状态**: 待执行
**影响**: 公网扫描器不断从不同 IP 连接，sync.Map 条目无限增长

---

## 根因

`transport/security.go:128-155`:

```go
type IPRateLimiter struct {
    m sync.Map // IP string → *ipRate — 只增不删
}
```

每个新 IP 创建条目，永远不会被清理。

## 涉及文件

| 文件 | 行号（约） | 改动类型 |
|------|-----------|----------|
| `svr/transport/security.go` | 128-155 | 增加清理逻辑 |

## 执行计划

### Step 1: 给 ipRate 增加最后活跃时间

```go
type ipRate struct {
    mu        sync.Mutex
    count     int
    lastReset time.Time
    lastSeen  time.Time  // 新增：最后活跃时间
}

func (r *ipRate) allow(maxPerSec int) bool {
    r.mu.Lock()
    defer r.mu.Unlock()
    now := time.Now()
    r.lastSeen = now  // 更新活跃时间
    if now.Sub(r.lastReset) >= time.Second {
        r.count = 0
        r.lastReset = now
    }
    r.count++
    return r.count <= maxPerSec
}
```

### Step 2: 给 IPRateLimiter 增加 Cleanup 方法

```go
func (l *IPRateLimiter) Cleanup(maxAge time.Duration) int {
    now := time.Now()
    deleted := 0
    l.m.Range(func(key, value any) bool {
        rate := value.(*ipRate)
        rate.mu.Lock()
        age := now.Sub(rate.lastSeen)
        rate.mu.Unlock()
        if age > maxAge {
            l.m.Delete(key)
            deleted++
        }
        return true
    })
    return deleted
}
```

### Step 3: 在 main.go 中启动定期清理

在 `main()` 的后台 goroutine 区域（约 line 310 附近）增加：

```go
// IP 限流表清理
go func() {
    ticker := time.NewTicker(5 * time.Minute)
    defer ticker.Stop()
    for {
        select {
        case <-ctx.Done():
            return
        case <-ticker.C:
            // 注意：需要让 server 和 wsServer 的 ipLimiter 可访问
            // 或者改为共享同一个 ipLimiter 实例
        }
    }
}()
```

**注意**: 当前 TcpServer 和 WsServer 各自创建了独立的 `IPRateLimiter`，且是私有字段。需要：
- 方案 A: 在 Server 接口上暴露 `CleanupIPLimiter()` 方法
- 方案 B: 共享一个全局 `IPRateLimiter` 实例，在 main 中创建并注入

### Step 4: 验证

- `go test ./...`
- 添加单元测试：创建 1000 个不同 IP 的条目，Cleanup 后确认只保留活跃的

---

## 验收标准

### 编译与测试

- [ ] `go build ./...` 编译通过
- [ ] `go test ./...` 全部通过

### 功能验证

- [ ] **新增单元测试**: `TestIPRateLimiterCleanup`
  - 创建 100 个不同 IP 的条目
  - 等待 maxAge 过期
  - 调用 Cleanup，确认返回 deleted=100
  - 再次 Allow 同一 IP，确认创建新条目（不复用已删除的）
- [ ] **新增单元测试**: `TestIPRateLimiterCleanupKeepsActive`
  - 创建 100 个条目，其中 10 个在过期前再次 Allow
  - Cleanup 后确认只保留 10 个活跃条目

### 预期结果

| 场景 | 修复前 | 修复后 |
|------|--------|--------|
| 1000 个不同 IP 扫描后 | sync.Map 中 1000 条永驻 | 5 分钟后 Cleanup 清理不活跃条目 |
| 正常活跃客户端 IP | 正常 | 不受影响（lastSeen 持续更新，不会被清理） |
| 长期运行（30 天） | sync.Map 可能积累数十万条目 | 条目数稳定在活跃 IP 数 |

**核心指标**: `/perf` 端点的 `heap_mb` 在高并发扫描场景下不再持续增长。
