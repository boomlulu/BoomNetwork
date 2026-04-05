package main

import (
	"strings"
	"testing"
)

// --- Go output parsing ---

func TestParseGoLine_CoreFields(t *testing.T) {
	line := "BenchmarkEncode_Small-8   305053878   3.656 ns/op   0 B/op   0 allocs/op"
	r, ok := parseGoLine(line)
	if !ok {
		t.Fatal("expected ok=true")
	}
	if r.Name != "Encode_Small" {
		t.Errorf("Name = %q, want %q", r.Name, "Encode_Small")
	}
	if r.NsPerOp < 3.0 || r.NsPerOp > 4.0 {
		t.Errorf("NsPerOp = %f, want ~3.656", r.NsPerOp)
	}
	if r.BytesPerOp != 0 {
		t.Errorf("BytesPerOp = %f, want 0", r.BytesPerOp)
	}
	if r.AllocsPerOp != 0 {
		t.Errorf("AllocsPerOp = %f, want 0", r.AllocsPerOp)
	}
	if r.Source != "go" {
		t.Errorf("Source = %q, want %q", r.Source, "go")
	}
}

func TestParseGoLine_WithAllocs(t *testing.T) {
	line := "BenchmarkDecode_Small-8   100000000   16.012 ns/op   48 B/op   1 allocs/op"
	r, ok := parseGoLine(line)
	if !ok {
		t.Fatal("expected ok=true")
	}
	if r.Name != "Decode_Small" {
		t.Errorf("Name = %q", r.Name)
	}
	if r.BytesPerOp != 48 {
		t.Errorf("BytesPerOp = %f, want 48", r.BytesPerOp)
	}
	if r.AllocsPerOp != 1 {
		t.Errorf("AllocsPerOp = %f, want 1", r.AllocsPerOp)
	}
}

func TestParseGoLine_NoCPUSuffix(t *testing.T) {
	line := "BenchmarkFrameReader_Small   50000   30000 ns/op   1024 B/op   10 allocs/op"
	r, ok := parseGoLine(line)
	if !ok {
		t.Fatal("expected ok=true")
	}
	if r.Name != "FrameReader_Small" {
		t.Errorf("Name = %q", r.Name)
	}
}

func TestParseGoLine_NotBenchmark(t *testing.T) {
	_, ok := parseGoLine("ok  github.com/boomlulu/boomnetwork/codec  0.123s")
	if ok {
		t.Error("expected ok=false for non-benchmark line")
	}
}

func TestParseGoLine_TooFewFields(t *testing.T) {
	_, ok := parseGoLine("BenchmarkFoo-8  100")
	if ok {
		t.Error("expected ok=false for line with too few fields")
	}
}

func TestParseGoOutput_MultiLine(t *testing.T) {
	input := `goos: darwin
goarch: arm64
BenchmarkEncode_Small-8    300000000    3.6 ns/op    0 B/op    0 allocs/op
BenchmarkDecode_Small-8    100000000   16.0 ns/op   48 B/op    1 allocs/op
PASS
ok  github.com/boomlulu/boomnetwork/codec  4.321s`

	results, err := ParseGoOutput(strings.NewReader(input))
	if err != nil {
		t.Fatal(err)
	}
	if len(results) != 2 {
		t.Fatalf("got %d results, want 2", len(results))
	}
	if results[0].Name != "Encode_Small" {
		t.Errorf("results[0].Name = %q", results[0].Name)
	}
	if results[1].Name != "Decode_Small" {
		t.Errorf("results[1].Name = %q", results[1].Name)
	}
}

// --- C# BenchmarkDotNet output parsing ---

func TestParseCSOutput_MarkdownTable(t *testing.T) {
	input := `
| Method             | Mean    | Error   | StdDev  | Allocated |
|--------------------|---------|---------|---------| ----------|
| Encode_Small       | 3.8 ns  | 0.05 ns | 0.05 ns | 0 B       |
| Decode_Small       | 6.9 ns  | 0.10 ns | 0.09 ns | 72 B      |
| Decode_Small_Pooled| 12.1 ns | 0.15 ns | 0.14 ns | 0 B       |
`
	results, err := ParseCSOutput(strings.NewReader(input))
	if err != nil {
		t.Fatal(err)
	}
	if len(results) != 3 {
		t.Fatalf("got %d results, want 3", len(results))
	}

	enc := results[0]
	if enc.Name != "Encode_Small" {
		t.Errorf("Name = %q", enc.Name)
	}
	if enc.NsPerOp < 3.5 || enc.NsPerOp > 4.1 {
		t.Errorf("NsPerOp = %f, want ~3.8", enc.NsPerOp)
	}
	if enc.BytesPerOp != 0 {
		t.Errorf("BytesPerOp = %f, want 0", enc.BytesPerOp)
	}

	dec := results[1]
	if dec.BytesPerOp != 72 {
		t.Errorf("BytesPerOp = %f, want 72", dec.BytesPerOp)
	}
}

func TestParseCSOutput_MicrosecondUnit(t *testing.T) {
	input := `
| Method      | Mean    | Allocated |
|-------------|---------|-----------|
| Framing_10000 | 4.0 μs | 0 B       |
`
	results, err := ParseCSOutput(strings.NewReader(input))
	if err != nil {
		t.Fatal(err)
	}
	if len(results) != 1 {
		t.Fatalf("got %d results, want 1", len(results))
	}
	if results[0].NsPerOp < 3900 || results[0].NsPerOp > 4100 {
		t.Errorf("NsPerOp = %f, want ~4000 (4 μs)", results[0].NsPerOp)
	}
}

// --- Matcher ---

func TestMatchBenchmarks_PairsCorrectly(t *testing.T) {
	goResults := []BenchResult{
		{Name: "EncodeTo_Small", NsPerOp: 3.6, Source: "go"},
		{Name: "Decode_Small", NsPerOp: 16.0, Source: "go"},
		{Name: "DecodePooled_Small", NsPerOp: 8.7, Source: "go"},
	}
	csResults := []BenchResult{
		{Name: "Encode_Small", NsPerOp: 3.8, Source: "cs"},
		{Name: "Decode_Small", NsPerOp: 6.9, Source: "cs"},
		{Name: "Decode_Small_Pooled", NsPerOp: 12.1, Source: "cs"},
	}

	pairs := MatchBenchmarks(goResults, csResults)

	find := func(label string) *MatchedPair {
		for i := range pairs {
			if pairs[i].Label == label {
				return &pairs[i]
			}
		}
		return nil
	}

	enc := find("Encode Small (pre-alloc buf)")
	if enc == nil {
		t.Fatal("missing 'Encode Small (pre-alloc buf)' pair")
	}
	if enc.Go == nil || enc.CS == nil {
		t.Errorf("EncodeTo_Small pair missing Go or C# side: go=%v cs=%v", enc.Go, enc.CS)
	}

	dec := find("Decode Small")
	if dec == nil {
		t.Fatal("missing 'Decode Small' pair")
	}

	pool := find("Decode Small (pooled)")
	if pool == nil {
		t.Fatal("missing 'Decode Small (pooled)' pair")
	}
	if pool.CS == nil || pool.CS.NsPerOp != 12.1 {
		t.Errorf("DecodePooled C# not matched correctly: %+v", pool.CS)
	}
}

func TestMatchBenchmarks_GoOnlyBenchmark(t *testing.T) {
	goResults := []BenchResult{
		{Name: "HandleFrameInput", NsPerOp: 54.6, Source: "go"},
	}
	pairs := MatchBenchmarks(goResults, nil)
	if len(pairs) == 0 {
		t.Fatal("expected at least 1 pair for unmatched Go benchmark")
	}
	if pairs[0].CS != nil {
		t.Error("expected CS=nil for Go-only benchmark")
	}
}

// --- FormatNs ---

func TestFormatNs(t *testing.T) {
	cases := []struct{ ns float64; want string }{
		{3.6, "3.6 ns"},
		{1500, "1.5 μs"},
		{2_000_000, "2.0 ms"},
		{-1, "N/A"},
	}
	for _, tc := range cases {
		got := FormatNs(tc.ns)
		if got != tc.want {
			t.Errorf("FormatNs(%g) = %q, want %q", tc.ns, got, tc.want)
		}
	}
}

// --- averageResults ---

func TestAverageResults_MultipleRuns(t *testing.T) {
	results := []BenchResult{
		{Name: "Foo", NsPerOp: 10, BytesPerOp: 0, AllocsPerOp: 0},
		{Name: "Foo", NsPerOp: 12, BytesPerOp: 0, AllocsPerOp: 0},
		{Name: "Bar", NsPerOp: 5, BytesPerOp: 48, AllocsPerOp: 1},
	}
	avg := averageResults(results)
	if len(avg) != 2 {
		t.Fatalf("got %d, want 2", len(avg))
	}
	if avg[0].NsPerOp != 11 {
		t.Errorf("avg Foo NsPerOp = %f, want 11", avg[0].NsPerOp)
	}
}
