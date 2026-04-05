package main

import "strings"

// MatchedPair Go + C# 对等 benchmark 配对
type MatchedPair struct {
	Label  string       // 展示用名称
	Go     *BenchResult // 可能为 nil
	CS     *BenchResult // 可能为 nil
}

// matchingRules Go Name → (C# Name, 展示 Label)
// Go Name 是去掉 "Benchmark" 前缀后的原始名，e.g. "EncodeTo_Small"
var matchingRules = []struct {
	goName  string
	csName  string
	label   string
}{
	{"EncodeTo_Small", "Encode_Small", "Encode Small (pre-alloc buf)"},
	{"EncodeTo_Large", "Encode_Large", "Encode Large (pre-alloc buf)"},
	{"Encode_Small", "", "Encode Small (Pool)"},
	{"Encode_Large", "", "Encode Large (Pool)"},
	{"Decode_Small", "Decode_Small", "Decode Small"},
	{"Decode_Large", "Decode_Large", "Decode Large"},
	{"DecodePooled_Small", "Decode_Small_Pooled", "Decode Small (pooled)"},
	{"DecodePooled_Large", "Decode_Large_Pooled", "Decode Large (pooled)"},
	{"FrameReader_Small", "Framing_10000", "FrameReader 10K msgs"},
	{"FrameWriter_Small", "FrameWriter_10000", "FrameWriter 10K msgs"},
}

// MatchBenchmarks 根据匹配规则将 Go 和 C# 结果配对
// 未出现在规则里的 benchmark 以原名追加（Go-only）
func MatchBenchmarks(goResults, csResults []BenchResult) []MatchedPair {
	goMap := indexBy(goResults, func(r BenchResult) string { return r.Name })
	csMap := indexBy(csResults, func(r BenchResult) string { return r.Name })

	seen := make(map[string]bool)
	var pairs []MatchedPair

	for _, rule := range matchingRules {
		var goRes, csRes *BenchResult
		if r, ok := goMap[rule.goName]; ok {
			goRes = &r
		}
		if rule.csName != "" {
			if r, ok := csMap[rule.csName]; ok {
				csRes = &r
			}
		}
		if goRes == nil && csRes == nil {
			continue
		}
		pairs = append(pairs, MatchedPair{
			Label: rule.label,
			Go:    goRes,
			CS:    csRes,
		})
		seen[rule.goName] = true
	}

	// 追加规则未覆盖的 Go-only benchmark
	for _, r := range goResults {
		if seen[r.Name] {
			continue
		}
		rc := r
		pairs = append(pairs, MatchedPair{
			Label: friendlyLabel(r.Name),
			Go:    &rc,
		})
	}

	return pairs
}

func indexBy(results []BenchResult, key func(BenchResult) string) map[string]BenchResult {
	m := make(map[string]BenchResult, len(results))
	for _, r := range results {
		m[key(r)] = r
	}
	return m
}

// friendlyLabel 将 "HandleFrameInput_Parallel" 转换为可读格式
func friendlyLabel(name string) string {
	return strings.ReplaceAll(name, "_", " ")
}
