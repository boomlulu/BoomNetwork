# benchcmp — BoomNetwork Benchmark 对比工具

自动跑 Go（和可选 C#）codec benchmark，输出彩色对比表格，保存历史记录。

---

## Quick Start

```bash
# 从仓库根目录，直接跑：
./bench.sh
```

就这一行。不需要了解 Go 命令，不需要记任何参数。

---

## 四种用法

| 命令 | 做什么 |
|------|--------|
| `./bench.sh` | 跑 Go benchmark，输出彩色表格（默认） |
| `./bench.sh diff` | 跑完后自动对比上次结果，显示变化百分比 |
| `./bench.sh md` | 输出 Markdown 表格，直接粘贴到 `benchmark-report.md` |
| `./bench.sh all` | Go + C# 双端对比（需安装 dotnet） |

Windows 用 `bench.bat` 替代 `bench.sh`，参数完全一样。

---

## 输出示例

```
BoomNetwork Codec Benchmark  2026-04-05 14:23 | darwin/arm64 | Go go1.24 | benchtime=2s count=3

╔══════════════════════════════════╦══════════════╦══════════════╦════════════╦════════════╦════════════════╗
║ Operation                        ║ Go ns/op     ║ C# ns/op     ║ Go B/op    ║ Go allocs  ║ Winner         ║
╠══════════════════════════════════╬══════════════╬══════════════╬════════════╬════════════╬════════════════╣
║ Encode Small (pre-alloc buf)     ║ 3.4 ns       ║ 3.8 ns       ║ 0 B        ║ 0          ║ Go +11%        ║
║ Encode Large (pre-alloc buf)     ║ 13.8 ns      ║ 20.3 ns      ║ 0 B        ║ 0          ║ Go +32%        ║
║ Encode Small (Pool)              ║ 21.1 ns      ║ —            ║ 24 B       ║ 1          ║ —              ║
║ Decode Small                     ║ 15.0 ns      ║ 6.9 ns       ║ 48 B       ║ 1          ║ C# 2.2×        ║
║ Decode Small (pooled)            ║ 9.1 ns       ║ 12.1 ns      ║ 0 B        ║ 0          ║ Go +25%        ║
║ Decode Large (分配路径)           ║ 15.0 ns      ║ 47.2 ns      ║ 48 B       ║ 1          ║ Go 3.1×        ║
║ FrameReader 10K msgs             ║ 358.0 μs     ║ —            ║ 480000 B   ║ 10000      ║ —              ║
╚══════════════════════════════════╩══════════════╩══════════════╩════════════╩════════════╩════════════════╝

── vs last run ──
Operation                          Current         Last        Change
──────────────────────────────────────────────────────────────────────
Encode Small (pre-alloc buf)         3.4 ns        3.6 ns      -5.6% ✓
Decode Small (pooled)                9.1 ns        8.7 ns      +4.6%

✅ Done! Results saved to tools/benchcmp/history.json (run #3)
```

---

## 可选参数

直接调用编译好的二进制时可用（`bench.sh` 已封装常用组合）：

| Flag | 默认 | 说明 |
|------|------|------|
| `--go-only` | false | 只跑 Go benchmark |
| `--cs-only` | false | 只跑 C# benchmark |
| `--md` | false | 输出 Markdown 表格 |
| `--compare-last` | false | 与上次结果对比 |
| `--bench-time 2s` | 2s | 每个 benchmark 运行时长 |
| `--count 3` | 3 | 重复次数（取均值） |
| `--no-save` | false | 不保存到 history.json |
| `--history path` | auto | 指定 history.json 路径 |
| `--go-dir path` | auto | 指定 svr/ 目录 |
| `--cs-dir path` | auto | 指定 cli/ 目录 |

环境变量（仅 `bench.sh` / `bench.bat`）：

```bash
BENCH_TIME=5s ./bench.sh
BENCH_COUNT=5 ./bench.sh diff
```

---

## 历史记录

每次运行结果追加到 `history.json`（不纳入 git）。`./bench.sh diff` 自动读取上次数据。
手动查看：

```bash
cat tools/benchcmp/history.json | python3 -m json.tool | head -40
```
