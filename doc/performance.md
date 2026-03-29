# Performance Report

> Environment: Apple M3 Pro, macOS, arm64. Go 1.24, .NET 8.
> Last updated: 2026-03-28

## TL;DR

| Metric | Value |
|--------|-------|
| Max concurrent players | **4,000** (1,000 rooms × 4) |
| Frame rate | **20 fps** (stable, zero drift) |
| Per-player bandwidth | **0.68 KB/s up, 3.14 KB/s down** |
| Server memory (4K players) | **200 MB heap** |
| Codec encode (small) | **3.8 ns** (C#), **3.6 ns** (Go zero-alloc) |
| Hot path GC allocation | **0 bytes** |
| Soak test (15 min) | 0 disconnections, 0.0009% frame loss |

---

## 1. Codec Benchmarks

The binary codec is the hottest path — called on every frame for every player.

### C# (.NET 8, BenchmarkDotNet)

| Operation | Latency | Alloc |
|-----------|---------|-------|
| Encode small (41B payload) | 3.8 ns | 0 B |
| Encode large (1KB payload) | 20.3 ns | 0 B |
| Decode small | 6.9 ns | 72 B |
| Decode large (1KB) | 47.2 ns | 1,048 B |
| Decode small (ArrayPool) | 12.1 ns | 0 B |
| Framing 100 sticky packets | 4.0 us | 0 B |

### Go (go test -bench)

| Operation | Latency | Alloc |
|-----------|---------|-------|
| EncodeTo (zero-alloc) | 3.6 ns | 0 B |
| Encode (sync.Pool) | 23.1 ns | 24 B |
| Decode (zero-copy) | 15.3 ns | 48 B |
| FrameReader 10K messages | 381 us | 489 KB |

---

## 2. Stress Test — 4,000 Players

```
Config:     1,000 rooms × 4 players = 4,000 total
Duration:   15 minutes
Frame rate: 20 fps (server tick)
```

| Metric | Value |
|--------|-------|
| Connected | 4,000 / 4,000 (0 failed) |
| Frames received | 71,999,366 / 72,000,000 (99.999%) |
| Upload bandwidth | 2.80 MB/s total (0.68 KB/s per client) |
| Download bandwidth | 12.88 MB/s total (3.14 KB/s per client) |
| Total transferred | 14.1 GB |
| Server heap | 200 MB |
| GC cycles | 126 (1 per 7.1s) |
| Goroutines | 1,002 |
| Disconnections | **0** |
| Frame rate stability | **20.0 fps throughout** |

---

## 3. Soak Test — Memory Leak Detection

```
Config:   100 rooms × 4 players = 400 total
Duration: 5 minutes, sampled every 30s
```

| Time | Heap | GC Count | Goroutines |
|------|------|----------|------------|
| 0:30 | 36 MB | 10 | 1,302 |
| 1:30 | 40 MB | 18 | 1,302 |
| 3:00 | 42 MB (peak) | 30 | 1,302 |
| 5:00 | 36 MB | 46 | 1,302 |

**Result: No memory leak.** Heap rises then returns to baseline. GC rate steady at ~1 per 6.5s.

---

## 4. Silent When Idle — Bandwidth Savings

| Scenario | Before | After | Change |
|----------|--------|-------|--------|
| Idle player send rate | 20 msg/s | 0 msg/s | **-100%** |
| 3,000 idle players bandwidth | 480 KB/s | 0 KB/s | **-100%** |
| Active input frequency | 60 fps | 20 fps | **-67%** |

The "Silent When Idle" design principle eliminates all bandwidth for inactive players.

---

## 5. Packet Header Optimization

| Metric | Old (17B header) | New (3-7B header) | Change |
|--------|-------------------|-------------------|--------|
| Per-client upload | 0.96 KB/s | 0.68 KB/s | **-29%** |
| Per-client download | 3.42 KB/s | 3.14 KB/s | **-8%** |
| Server memory | 299 MB | 290 MB | -3% |
| Frame rate | 20.0 fps | 20.0 fps | unchanged |

---

## How to Reproduce

```bash
# Codec benchmarks
cd cli/Benchmark && dotnet run -c Release
cd svr && go test ./codec/ -bench=. -benchmem

# Stress test (requires running server)
cd svr && go run ./cmd/stress/ -rooms=1000 -players=4 -duration=15m -addr=127.0.0.1:9000
```
