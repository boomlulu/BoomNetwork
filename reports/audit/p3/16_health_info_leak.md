# P3-16: /health 端点泄露 Go 版本和构建哈希

**严重程度**: P3 — 低风险
**状态**: ✅ 已完成（2026-04-10）
**影响**: 公开的 Go 运行时版本可帮助攻击者定位已知漏洞

---

## 根因

`admin.go:153`:
```go
fmt.Fprintf(w, `{..."goVersion":%q,"buildHash":%q}`, runtime.Version(), BuildHash)
```

/health 不鉴权（健康检查探针需要无障碍访问），任何人都能看到。

## 执行计划

### Step 1: 从 /health 移除敏感字段

```go
func handleHealth(w http.ResponseWriter, r *http.Request) {
    rooms := roomMgr.RoomCount()
    players := countOnlinePlayers()
    uptime := time.Since(serverStartTime).Truncate(time.Second).String()
    w.Header().Set("Content-Type", "application/json")
    fmt.Fprintf(w, `{"status":"ok","rooms":%d,"players":%d,"uptime":%q}`,
        rooms, players, uptime)
}
```

### Step 2: 将版本信息移到鉴权后的 /stats

在 `handleStats` 的响应中增加 `build_hash` 和 `go_version` 字段。

### 验证

- 手动测试 /health 不再包含版本信息
- 确认 /stats 包含版本信息（需鉴权）

---

## 验收标准

- [ ] `curl http://localhost:9091/health` 响应不包含 `goVersion` 和 `buildHash`
- [ ] `curl -H "Authorization: Bearer <token>" http://localhost:9091/stats` 响应包含版本信息
- [ ] 现有健康检查探针（K8s / systemd）不受影响（仍返回 `status: "ok"`）

**核心指标**: 未鉴权端点不暴露任何运行时和构建版本信息。
