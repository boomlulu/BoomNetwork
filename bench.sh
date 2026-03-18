#!/bin/bash
set -e

ROOT="$(cd "$(dirname "$0")" && pwd)"

echo "=========================================="
echo "  BoomNetwork Performance Report"
echo "  $(date '+%Y-%m-%d %H:%M:%S')"
echo "  $(uname -m) / $(sw_vers -productName 2>/dev/null || echo Linux) $(sw_vers -productVersion 2>/dev/null || uname -r)"
echo "=========================================="

# --- Go Benchmark ---
echo ""
echo "[ Go Benchmark ]"
echo "------------------------------------------"
cd "$ROOT/svr"
go test ./codec/ -bench=. -benchmem -count=1 2>&1 | grep -E "^Benchmark|^goos|^goarch|^cpu"

# --- C# Benchmark ---
echo ""
echo "[ C# Benchmark ]"
echo "------------------------------------------"
cd "$ROOT/cli"
# BenchmarkDotNet 输出很多，只提取表格
OUTPUT=$(dotnet run --project Benchmark -c Release 2>&1)
echo "$OUTPUT" | grep -E "^\| |^\|[-]"

# --- Save report ---
REPORT_DIR="$ROOT/reports"
mkdir -p "$REPORT_DIR"
REPORT_FILE="$REPORT_DIR/bench_$(date '+%Y%m%d_%H%M%S').txt"

{
    echo "=========================================="
    echo "  BoomNetwork Performance Report"
    echo "  $(date '+%Y-%m-%d %H:%M:%S')"
    echo "=========================================="
    echo ""
    echo "[ Go Benchmark ]"
    cd "$ROOT/svr"
    go test ./codec/ -bench=. -benchmem -count=1 2>&1 | grep -E "^Benchmark|^goos|^goarch|^cpu"
    echo ""
    echo "[ C# Benchmark ]"
    echo "$OUTPUT" | grep -E "^\| |^\|[-]"
} > "$REPORT_FILE"

echo ""
echo "------------------------------------------"
echo "Report saved to: $REPORT_FILE"
echo "=========================================="
