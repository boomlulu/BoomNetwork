// benchcmp — BoomNetwork Go vs C# benchmark 对比工具
//
// 用法:
//
//	go run . --go-only              # 只跑 Go benchmark
//	go run . --cs-only              # 只跑 C# benchmark
//	go run .                        # 双端对比
//	go run . --md                   # 输出 Markdown 表格（可追加到 benchmark-report.md）
//	go run . --compare-last         # 与上次运行对比（go-only）
//	go run . --go-only --bench-time 5s --count 5
//
// 从仓库根目录执行:
//
//	go run tools/benchcmp/main.go --go-only
package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strings"
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
		noSave      = flag.Bool("no-save", false, "Do not save results to history")
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

	// Load history for --compare-last
	history, err := LoadHistory(histPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, colorYellow+"Warning: could not load history: "+err.Error()+colorReset)
	}
	lastPairs := LastPairs(history)

	// --- Run benchmarks ---
	var goResults, csResults []BenchResult

	if !*csOnly {
		fmt.Fprintln(os.Stderr, colorBold+"\n[ Running Go benchmarks... ]"+colorReset)
		goRaw, err := RunGoBenchmarks(cfg)
		if err != nil {
			fmt.Fprintln(os.Stderr, colorRed+"Go benchmark failed: "+err.Error()+colorReset)
			if !*goOnly {
				fmt.Fprintln(os.Stderr, colorYellow+"Continuing without Go results."+colorReset)
			}
		} else {
			goResults, err = ParseGoOutput(strings.NewReader(goRaw))
			if err != nil {
				fmt.Fprintln(os.Stderr, colorRed+"Failed to parse Go output: "+err.Error()+colorReset)
			}
			// average multiple counts for the same benchmark name
			goResults = averageResults(goResults)
			fmt.Fprintf(os.Stderr, "  Parsed %d Go benchmarks\n", len(goResults))
		}
	}

	if !*goOnly {
		fmt.Fprintln(os.Stderr, colorBold+"\n[ Running C# benchmarks... ]"+colorReset)
		csRaw, err := RunCSBenchmarks(cfg)
		if err != nil {
			fmt.Fprintln(os.Stderr, colorYellow+"C# benchmark not available ("+err.Error()+")"+colorReset)
			fmt.Fprintln(os.Stderr, colorDim+"  Hint: create cli/Benchmark/ with BenchmarkDotNet for C# comparisons."+colorReset)
		} else {
			csResults, err = ParseCSOutput(strings.NewReader(csRaw))
			if err != nil {
				fmt.Fprintln(os.Stderr, colorRed+"Failed to parse C# output: "+err.Error()+colorReset)
			}
			fmt.Fprintf(os.Stderr, "  Parsed %d C# benchmarks\n", len(csResults))
		}
	}

	// --- Match and format ---
	pairs := MatchBenchmarks(goResults, csResults)

	fmt.Fprintln(os.Stderr, "")
	if *mdOut {
		PrintMarkdownTable(pairs)
	} else {
		PrintTerminalTable(pairs)
	}

	if *compareLast {
		PrintCompareLast(pairs, lastPairs)
	}

	// --- Save to history ---
	if !*noSave && len(pairs) > 0 {
		history, err = SaveHistory(histPath, history, pairs)
		if err != nil {
			fmt.Fprintln(os.Stderr, colorYellow+"Warning: could not save history: "+err.Error()+colorReset)
		} else {
			fmt.Fprintf(os.Stderr, colorDim+"\nSaved to %s (total %d runs)\n"+colorReset, histPath, len(history))
		}
	}
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
	// Fallback: use cwd, let runner fail with a clear error
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
	order := []string{}

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
