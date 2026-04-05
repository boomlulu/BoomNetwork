#!/usr/bin/env bash
# BoomNetwork Benchmark Runner
# Usage:
#   ./bench.sh          — run Go benchmarks and show colored table (default)
#   ./bench.sh all      — run Go + C# and compare
#   ./bench.sh md       — output Markdown table (paste into benchmark-report.md)
#   ./bench.sh diff     — run and compare with last run
#   ./bench.sh help     — show this message
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BENCHCMP_SRC="$SCRIPT_DIR/tools/benchcmp"
BENCHCMP_BIN="$BENCHCMP_SRC/benchcmp"

MODE="${1:-}"

# ── help ──────────────────────────────────────────────────────────────────────
if [[ "$MODE" == "help" || "$MODE" == "--help" || "$MODE" == "-h" ]]; then
  cat <<'EOF'
BoomNetwork Benchmark Runner

Usage:
  ./bench.sh          Run Go benchmarks, show colored table (default)
  ./bench.sh all      Run Go + C# and compare
  ./bench.sh md       Output Markdown table (paste into benchmark-report.md)
  ./bench.sh diff     Run Go benchmarks and compare with last run
  ./bench.sh help     Show this message

Environment:
  BENCH_TIME=5s       Override benchmark duration (default: 2s)
  BENCH_COUNT=5       Override repeat count for averaging (default: 3)

Examples:
  ./bench.sh
  ./bench.sh diff
  BENCH_TIME=5s ./bench.sh md
EOF
  exit 0
fi

# ── check dependencies ─────────────────────────────────────────────────────────
check_go() {
  if ! command -v go &>/dev/null; then
    echo "❌  Go is not installed or not in PATH."
    echo "    Install from: https://go.dev/dl/"
    exit 1
  fi
}

check_dotnet() {
  if ! command -v dotnet &>/dev/null; then
    echo "⚠️   dotnet is not installed — C# benchmarks will be skipped."
    echo "    Install from: https://dotnet.microsoft.com/download"
    return 1
  fi
  return 0
}

check_go

# ── build benchcmp (incremental: skip if binary is newer than all source files) ─
build_benchcmp() {
  local needs_build=0

  if [[ ! -f "$BENCHCMP_BIN" ]]; then
    needs_build=1
  else
    # Rebuild if any .go source is newer than the binary
    while IFS= read -r -d '' f; do
      if [[ "$f" -nt "$BENCHCMP_BIN" ]]; then
        needs_build=1
        break
      fi
    done < <(find "$BENCHCMP_SRC" -name '*.go' -not -name '*_test.go' -print0)
  fi

  if [[ $needs_build -eq 1 ]]; then
    echo "🔨 Building benchcmp..."
    # tools/benchcmp is its own Go module, must build from within its directory
    (cd "$BENCHCMP_SRC" && go build -o benchcmp .)
    echo "   Built: $BENCHCMP_BIN"
  fi
}

build_benchcmp

# ── resolve flags ──────────────────────────────────────────────────────────────
BENCH_TIME="${BENCH_TIME:-2s}"
BENCH_COUNT="${BENCH_COUNT:-3}"

BASE_FLAGS="--bench-time $BENCH_TIME --count $BENCH_COUNT"

case "$MODE" in
  ""|"go")
    "$BENCHCMP_BIN" --go-only $BASE_FLAGS
    ;;
  "all")
    if check_dotnet; then
      "$BENCHCMP_BIN" $BASE_FLAGS
    else
      echo "   Falling back to Go-only."
      "$BENCHCMP_BIN" --go-only $BASE_FLAGS
    fi
    ;;
  "md")
    "$BENCHCMP_BIN" --go-only --md --no-save $BASE_FLAGS
    ;;
  "diff")
    "$BENCHCMP_BIN" --go-only --compare-last $BASE_FLAGS
    ;;
  *)
    echo "❌  Unknown command: '$MODE'"
    echo "    Run './bench.sh help' for usage."
    exit 1
    ;;
esac
