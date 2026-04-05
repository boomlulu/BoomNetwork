package main

import (
	"bufio"
	"fmt"
	"io"
	"strconv"
	"strings"
)

// BenchResult 单条 benchmark 测量结果（来自 Go 或 C#）
type BenchResult struct {
	Name      string  // 去掉 "Benchmark" 前缀后的名字，e.g. "EncodeTo_Small"
	NsPerOp   float64 // ns/op（-1 = 未知）
	BytesPerOp float64 // B/op（-1 = 未知）
	AllocsPerOp float64 // allocs/op（-1 = 未知）
	Iters     int64   // 迭代次数
	Source    string  // "go" 或 "cs"
	Raw       string  // 原始行
}

// --- Go benchmark output parser ---
// 格式: BenchmarkXxx-N   iter   ns/op [B/op  allocs/op]
// 示例: BenchmarkEncode_Small-8   305053878   3.656 ns/op   0 B/op   0 allocs/op

func ParseGoOutput(r io.Reader) ([]BenchResult, error) {
	var results []BenchResult
	scanner := bufio.NewScanner(r)
	for scanner.Scan() {
		line := scanner.Text()
		res, ok := parseGoLine(line)
		if ok {
			results = append(results, res)
		}
	}
	return results, scanner.Err()
}

func parseGoLine(line string) (BenchResult, bool) {
	// 必须以 Benchmark 开头
	if !strings.HasPrefix(line, "Benchmark") {
		return BenchResult{}, false
	}
	fields := strings.Fields(line)
	if len(fields) < 4 {
		return BenchResult{}, false
	}

	res := BenchResult{Source: "go", Raw: line, NsPerOp: -1, BytesPerOp: -1, AllocsPerOp: -1}

	// 名称：去掉 -N CPU 后缀
	nameWithCPU := fields[0]
	if idx := strings.LastIndex(nameWithCPU, "-"); idx > 0 {
		res.Name = nameWithCPU[len("Benchmark"):idx]
	} else {
		res.Name = nameWithCPU[len("Benchmark"):]
	}

	// 迭代次数
	if iters, err := strconv.ParseInt(fields[1], 10, 64); err == nil {
		res.Iters = iters
	}

	// 扫描剩余字段对：value unit
	for i := 2; i+1 < len(fields); i += 2 {
		val, err := strconv.ParseFloat(fields[i], 64)
		if err != nil {
			continue
		}
		unit := fields[i+1]
		switch unit {
		case "ns/op":
			res.NsPerOp = val
		case "B/op":
			res.BytesPerOp = val
		case "allocs/op":
			res.AllocsPerOp = val
		}
	}

	if res.NsPerOp < 0 {
		return BenchResult{}, false
	}
	return res, true
}

// --- C# BenchmarkDotNet output parser ---
// BenchmarkDotNet 默认 markdown 表格格式:
// | Method        | Mean   | Error | StdDev | Allocated |
// |---------------|--------|-------|--------|-----------|
// | Encode_Small  | 3.8 ns | ...   | ...    | 0 B       |
//
// 也支持 dotnet test --logger console 的简单输出（无表格）。

func ParseCSOutput(r io.Reader) ([]BenchResult, error) {
	var results []BenchResult
	scanner := bufio.NewScanner(r)

	// 先尝试解析 markdown 表格
	var headers []string
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if !strings.HasPrefix(line, "|") {
			continue
		}
		cells := splitMarkdownRow(line)
		if len(cells) < 2 {
			continue
		}
		// 分隔行（---|---）跳过
		if strings.Contains(cells[0], "-") && strings.TrimLeft(cells[0], "- ") == "" {
			continue
		}
		// 表头行
		if headers == nil {
			headers = cells
			continue
		}
		res, ok := parseCSRow(headers, cells)
		if ok {
			results = append(results, res)
		}
	}
	return results, scanner.Err()
}

func splitMarkdownRow(line string) []string {
	// "|  A  |  B  |  C  |" → ["A", "B", "C"]
	line = strings.Trim(line, "|")
	parts := strings.Split(line, "|")
	out := make([]string, len(parts))
	for i, p := range parts {
		out[i] = strings.TrimSpace(p)
	}
	return out
}

func parseCSRow(headers, cells []string) (BenchResult, bool) {
	if len(cells) < len(headers) {
		return BenchResult{}, false
	}
	res := BenchResult{Source: "cs", NsPerOp: -1, BytesPerOp: -1, AllocsPerOp: -1}

	colIdx := func(name string) int {
		name = strings.ToLower(name)
		for i, h := range headers {
			if strings.ToLower(h) == name {
				return i
			}
		}
		return -1
	}

	// 名称列（Method / Name / Benchmark）
	for _, col := range []string{"method", "name", "benchmark"} {
		if i := colIdx(col); i >= 0 && i < len(cells) {
			res.Name = cells[i]
			break
		}
	}
	if res.Name == "" {
		return BenchResult{}, false
	}

	// Mean 列 → ns/op
	if i := colIdx("mean"); i >= 0 && i < len(cells) {
		if v, unit, ok := parseTimeCell(cells[i]); ok {
			res.NsPerOp = toNs(v, unit)
		}
	}

	// Allocated / B/op 列
	for _, col := range []string{"allocated", "b/op", "bytes"} {
		if i := colIdx(col); i >= 0 && i < len(cells) {
			if v, unit, ok := parseBytesCell(cells[i]); ok {
				res.BytesPerOp = toBytesPerOp(v, unit)
				break
			}
		}
	}

	if res.NsPerOp < 0 {
		return BenchResult{}, false
	}
	return res, true
}

func parseTimeCell(s string) (float64, string, bool) {
	// "3.8 ns" / "1.23 μs" / "12.1 ms"
	fields := strings.Fields(s)
	if len(fields) < 2 {
		return 0, "", false
	}
	val, err := strconv.ParseFloat(strings.ReplaceAll(fields[0], ",", ""), 64)
	if err != nil {
		return 0, "", false
	}
	return val, fields[1], true
}

func toNs(val float64, unit string) float64 {
	switch unit {
	case "ns", "ns/op":
		return val
	case "μs", "us":
		return val * 1000
	case "ms":
		return val * 1_000_000
	case "s":
		return val * 1_000_000_000
	}
	return val
}

func parseBytesCell(s string) (float64, string, bool) {
	// "0 B" / "48 B" / "1.00 KB"
	if s == "-" || s == "0 B" || s == "0" {
		return 0, "B", true
	}
	fields := strings.Fields(s)
	if len(fields) < 1 {
		return 0, "", false
	}
	val, err := strconv.ParseFloat(strings.ReplaceAll(fields[0], ",", ""), 64)
	if err != nil {
		return 0, "", false
	}
	unit := "B"
	if len(fields) >= 2 {
		unit = fields[1]
	}
	return val, unit, true
}

func toBytesPerOp(val float64, unit string) float64 {
	switch strings.ToUpper(unit) {
	case "B":
		return val
	case "KB":
		return val * 1024
	case "MB":
		return val * 1024 * 1024
	}
	return val
}

// FormatNs 将 ns 值格式化为人类可读字符串
func FormatNs(ns float64) string {
	switch {
	case ns < 0:
		return "N/A"
	case ns < 1000:
		return fmt.Sprintf("%.1f ns", ns)
	case ns < 1_000_000:
		return fmt.Sprintf("%.1f μs", ns/1000)
	default:
		return fmt.Sprintf("%.1f ms", ns/1_000_000)
	}
}

// FormatBytes 将字节数格式化
func FormatBytes(b float64) string {
	if b < 0 {
		return "N/A"
	}
	return fmt.Sprintf("%.0f B", b)
}
