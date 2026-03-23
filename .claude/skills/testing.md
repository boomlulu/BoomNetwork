# 测试体系技能

## 测试脚本

```bash
# 全量自动化测试（7 项）
cd /Users/boom/Demo/BoomNetwork && ./test.sh

# 一键验收（含服务器自动启停 + 帧同步联调）
./verify.sh

# 性能基准（Go benchmark + C# BenchmarkDotNet）
./bench.sh
```

## test.sh 包含的 7 项测试

| # | 测试项 | 验证内容 |
|---|--------|---------|
| 1 | C# Unit Tests (19个) | Codec 编解码 + Framing 粘包拆包 |
| 2 | Go Unit Tests | Codec + Framing |
| 3 | Cross-language: C#→Go | C# 编码的 fixture 文件让 Go 解码 |
| 4 | Cross-language: Go→C# | Go 编码的 fixture 文件让 C# 解码 |
| 5 | TCP Echo Integration | Go Echo Server + C# Client: 发收 + Ping + 超时 |
| 6 | FrameSync Integration | 2 客户端帧同步 + 重连 + 断线恢复 |
| 7 | KCP Echo Integration | KCP 协议基本收发 |

## verify.sh 包含的 9 项验收

```
[1/9] C# Unit Tests
[2/9] Go Unit Tests
[3/9] Cross-language C#→Go
[4/9] Cross-language Go→C#
[5/9] TCP Echo (4 checks: echo + ping + concurrent + timeout)
[6/9] FrameSync (2 clients: bind + start + frames + reconnect)
[7/9] KCP Echo
[8/9] Stress Test (3000 players, 10s)
[9/9] Performance Baseline
```

## 压测工具

### Go 同进程压测
```bash
cd /Users/boom/Demo/BoomNetwork/svr
go run ./cmd/stress/ -rooms=750 -players=4 -duration=10s
# 3000 人，20fps，验证吞吐/带宽/内存
```

### C#↔Go 跨语言压测
```bash
# 终端 1: 启动 Go 服务器
cd svr && go run ./cmd/framesync/ -addr=:9000 -ppr=4 -autoroom

# 终端 2: 启动 C# 压测客户端
cd cli && dotnet run --project StressTest -- --host=127.0.0.1 --port=9000 --clients=1000 --ppr=4 --duration=10
```

### KCP 压测
```bash
cd svr && go run ./cmd/kcpstress/ -rooms=250 -players=4 -duration=10s
```

## 性能基准（bench.sh）

### C# BenchmarkDotNet
```
cli/Benchmark/CodecBenchmark.cs
  - Encode_Small / Encode_Large
  - Decode_Small / Decode_Large / Decode_Small_Pooled

cli/Benchmark/FramingBenchmark.cs（如果存在）
  - Feed_100_StickyMessages
```

### Go testing.B
```
svr/codec/bench_test.go
  - BenchmarkEncode_Small / BenchmarkEncode_Large / BenchmarkEncodeTo_Small
  - BenchmarkDecode_Small / BenchmarkDecode_Large
  - BenchmarkFrameReader_Small / BenchmarkFrameWriter_Small
```

## 性能基线数据

```
C# Encode_Small:        3.6 ns, 0 Allocated
C# Encode_Large:       20.4 ns, 0 Allocated
C# Decode_Small:        6.3 ns, 72 B (非 pooled)
C# Decode_Small_Pooled: 12.6 ns, 0 Allocated
C# Framing 100 sticky:  3.9 μs, 0 Allocated

Go EncodeTo_Small:      3.3 ns, 0 allocs
Go Decode_Small:       14.5 ns, 48 B

Stress 3000p 10s:  20.0 fps, 9.66 MB/s down, 125 MB heap
Stress 4000p 15m:  20.0 fps, 12.88 MB/s down, 200 MB heap, 126 GC
```

## Unity CLI 测试

```bash
# EditMode（不需要 Play）
/Users/boom/Demo/BoomNetworkUnity/test_unity.sh edit

# PlayMode（需要 Go 服务器）
/Users/boom/Demo/BoomNetworkUnity/test_unity.sh play

# 全部
/Users/boom/Demo/BoomNetworkUnity/test_unity.sh all
```

Unity 测试位于: `BoomNetworkUnity/Untiy/Assets/Tests/`

## 报告存储

```
/Users/boom/Demo/BoomNetwork/reports/
├── bench_YYYYMMDD_HHMMSS.txt     ← 性能基准
├── stress_15min_4000players.txt   ← 长时间压测
├── p1_security_summary.txt        ← 安全加固前后对比
└── perf_comparison_final.txt      ← 最终性能对比
```

## 修改代码后的测试流程

```
1. 改代码
2. cd /Users/boom/Demo/BoomNetwork && ./test.sh    ← 单元测试 + 跨语言
3. ./verify.sh                                     ← 联调 + 压测
4. ./bench.sh                                      ← 性能无回退？
5. 同步 UPM 包 → Unity Update → test_unity.sh all  ← Unity 兼容？
6. git commit + push
```
