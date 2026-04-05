// benchcmp — BoomNetwork Go vs C# benchmark 对比工具
//
// 最简用法（从仓库根目录）：
//
//	./bench.sh          # 一键跑完，输出彩色表格
//	./bench.sh diff     # 与上次对比
//	./bench.sh md       # 输出 Markdown
//
// 直接调用（已编译）：
//
//	tools/benchcmp/benchcmp --go-only
//	tools/benchcmp/benchcmp --go-only --md
//	tools/benchcmp/benchcmp --go-only --compare-last
//	tools/benchcmp/benchcmp --bench-time 5s --count 5
package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

func main() {
	var (
		goOnly      = flag.Bool("go-only", false, "Only run Go benchmarks")
		csOnly      = flag.Bool("cs-only", false, "Only run C# benchmarks")
		mdOut       = flag.Bool("md", false, "Output Markdown table to stdout")
		compareLast = flag.Bool("compare-last", false, "Compare current run with last saved run")
		benchTime   = flag.String("bench-time", "2s", "Benchmark duration per test (e.g. 2s, 5s)")
		count       = flag.Int("count", 3, "Number of times to run each benchmark")
		goDir       = flag.String("go-dir", "", "Path to svr/ directory (auto-detected if empty)")
		csDir       = flag.String("cs-dir", "", "Path to cli/ directory (auto-detected if empty)")
		histFile    = flag.String("history", "", "Path to history.json (default: tools/benchcmp/history.json)")
		noSave      = flag.Bool("no-save", false, "Do not save results to history.json")
	)
	flag.Parse()

	// Auto-detect repo root (walk up from cwd until we find svr/go.mod)
	repoRoot, err := findRepoRoot()
	if err != nil {
		fmt.Fprintln(os.Stderr, "Could not find repo root:", err)
		os.Exit(1)
	}

	cfg := DefaultConfig(repoRoot)
	cfg.BenchTime = *benchTime
	cfg.Count = *count
	cfg.GoOnly = *goOnly
	cfg.CSOnly = *csOnly
	if *goDir != "" {
		cfg.GoDir = *goDir
	}
	if *csDir != "" {
		cfg.CSDir = *csDir
	}

	histPath := *histFile
	if histPath == "" {
		histPath = filepath.Join(repoRoot, "tools", "benchcmp", "history.json")
	}

	// Load history
	history, err := LoadHistory(histPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, colorYellow+"Warning: could not load history: "+err.Error()+colorReset)
	}
	lastPairs := LastPairs(history)

	// --- Run benchmarks ---
	var goResults, csResults []BenchResult

	if !*csOnly {
		estSecs := estimateDuration(*benchTime, *count, len(cfg.GoPackages))
		fmt.Fprintf(os.Stderr, "\n🔍 Running Go benchmarks... (this takes ~%ds)\n", estSecs)
		goRaw, err := RunGoBenchmarks(cfg)
		if err != nil {
			fmt.Fprintln(os.Stderr, colorRed+"   Go benchmark failed: "+err.Error()+colorReset)
			if *goOnly {
				os.Exit(1)
			}
			fmt.Fprintln(os.Stderr, colorYellow+"   Continuing without Go results."+colorReset)
		} else {
			goResults, err = ParseGoOutput(strings.NewReader(goRaw))
			if err != nil {
				fmt.Fprintln(os.Stderr, colorRed+"   Failed to parse Go output: "+err.Error()+colorReset)
			}
			goResults = averageResults(goResults)
			fmt.Fprintf(os.Stderr, "   Parsed %d Go benchmarks\n", len(goResults))
		}
	}

	if !*goOnly {
		fmt.Fprintln(os.Stderr, "\n🔍 Running C# benchmarks...")
		csRaw, err := RunCSBenchmarks(cfg)
		if err != nil {
			fmt.Fprintln(os.Stderr, colorYellow+"   C# benchmark not available: "+err.Error()+colorReset)
			fmt.Fprintln(os.Stderr, colorDim+"   Hint: create cli/Benchmark/ with BenchmarkDotNet for C# comparisons."+colorReset)
		} else {
			csResults, err = ParseCSOutput(strings.NewReader(csRaw))
			if err != nil {
				fmt.Fprintln(os.Stderr, colorRed+"   Failed to parse C# output: "+err.Error()+colorReset)
			}
			fmt.Fprintf(os.Stderr, "   Parsed %d C# benchmarks\n", len(csResults))
		}
	}

	// --- Match and format ---
	pairs := MatchBenchmarks(goResults, csResults)

	if *mdOut {
		// Markdown: print header as HTML comment, then table
		fmt.Printf("<!-- %s | %s/%s | Go %s | benchtime=%s count=%d -->\n",
			time.Now().Format("2006-01-02 15:04"),
			runtime.GOOS, runtime.GOARCH,
			goVersion(),
			*benchTime, *count,
		)
		PrintMarkdownTable(pairs)
	} else {
		PrintHeader(*benchTime, *count)
		PrintTerminalTable(pairs)
	}

	// Auto-show diff if history exists (terminal only, skipped for --md)
	if !*mdOut && (*compareLast || len(lastPairs) > 0) {
		PrintCompareLast(pairs, lastPairs)
	}

	// --- Save to history ---
	if !*noSave && len(pairs) > 0 {
		history, err = SaveHistory(histPath, history, pairs)
		if err != nil {
			fmt.Fprintln(os.Stderr, colorYellow+"\nWarning: could not save history: "+err.Error()+colorReset)
		} else {
			fmt.Fprintf(os.Stderr, "\n✅ Done! Results saved to %s (run #%d)\n", histPath, len(history))
		}
	} else if len(pairs) > 0 {
		fmt.Fprintln(os.Stderr, "\n✅ Done!")
	}
}

// estimateDuration 估算 benchmark 运行时长（秒），用于打印友好提示
func estimateDuration(benchTime string, count, pkgCount int) int {
	// 粗估：每个包每次 count 约 benchTime × 测试数（保守按 10 个测试算）
	secs := 2 // default
	if len(benchTime) >= 2 {
		val := 0
		fmt.Sscanf(benchTime, "%d", &val)
		if val > 0 {
			secs = val
		}
	}
	return secs * count * pkgCount
}

// goVersion 返回 Go 版本字符串，例如 "go1.24"
func goVersion() string {
	v := runtime.Version()
	if len(v) > 7 {
		return v[:7]
	}
	return v
}

// findRepoRoot 向上查找包含 svr/go.mod 的目录
func findRepoRoot() (string, error) {
	cwd, err := os.Getwd()
	if err != nil {
		return "", err
	}
	dir := cwd
	for {
		if _, err := os.Stat(filepath.Join(dir, "svr", "go.mod")); err == nil {
			return dir, nil
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			break
		}
		dir = parent
	}
	return cwd, nil
}

// averageResults 对同名 benchmark 的多次 count 结果取算术平均
func averageResults(results []BenchResult) []BenchResult {
	type accum struct {
		nsSum     float64
		bytesSum  float64
		allocsSum float64
		count     int
		sample    BenchResult
	}
	seen := make(map[string]*accum)
	var order []string

	for _, r := range results {
		if a, ok := seen[r.Name]; ok {
			a.nsSum += r.NsPerOp
			a.bytesSum += r.BytesPerOp
			a.allocsSum += r.AllocsPerOp
			a.count++
		} else {
			seen[r.Name] = &accum{
				nsSum:     r.NsPerOp,
				bytesSum:  r.BytesPerOp,
				allocsSum: r.AllocsPerOp,
				count:     1,
				sample:    r,
			}
			order = append(order, r.Name)
		}
	}

	out := make([]BenchResult, 0, len(order))
	for _, name := range order {
		a := seen[name]
		r := a.sample
		r.NsPerOp = a.nsSum / float64(a.count)
		r.BytesPerOp = a.bytesSum / float64(a.count)
		r.AllocsPerOp = a.allocsSum / float64(a.count)
		out = append(out, r)
	}
	return out
}
