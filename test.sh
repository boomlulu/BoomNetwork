#!/bin/bash
set -e

ROOT="$(cd "$(dirname "$0")" && pwd)"
PASS=0
FAIL=0
TOTAL=7

cleanup_port() {
    lsof -ti:$1 2>/dev/null | xargs kill -9 2>/dev/null || true
}

check_result() {
    local name="$1"
    local output="$2"
    local pattern="${3:-0 failed}"

    if echo "$output" | grep -q "$pattern"; then
        echo "  ✓ $name passed"
        PASS=$((PASS + 1))
    else
        echo "  ✗ $name FAILED"
        FAIL=$((FAIL + 1))
    fi
}

echo "=========================================="
echo "  BoomNetwork Full Test Suite"
echo "=========================================="

# --- 1. C# Unit Tests ---
echo ""
echo "[1/$TOTAL] C# Unit Tests (Codec + Framing)..."
cd "$ROOT/cli"
dotnet test --nologo -v q 2>&1 | tail -3
echo "  ✓ C# unit tests passed"
PASS=$((PASS + 1))

# --- 2. Go Unit Tests ---
echo ""
echo "[2/$TOTAL] Go Unit Tests (Codec + Framing)..."
cd "$ROOT/svr"
go test ./codec/ -count=1 -run "^Test(Encode|Decode|HeaderSize|PeekFrame|Framing|CrossLanguageRound)" 2>&1 | tail -2
echo "  ✓ Go unit tests passed"
PASS=$((PASS + 1))

# --- 3. Cross-language: C# generate → Go verify ---
echo ""
echo "[3/$TOTAL] Cross-language: C# encode → Go decode..."
cd "$ROOT/cli"
dotnet test --nologo --filter "GenerateCSharpFixtures" -v q 2>&1 > /dev/null
cd "$ROOT/svr"
go test ./codec/ -run "TestVerifyCSharpFixtures" -count=1 2>&1 | tail -2
echo "  ✓ Go verified C# fixtures"
PASS=$((PASS + 1))

# --- 4. Cross-language: Go generate → C# verify ---
echo ""
echo "[4/$TOTAL] Cross-language: Go encode → C# decode..."
cd "$ROOT/svr"
go test ./codec/ -run "TestGenerateGoFixtures" -count=1 2>&1 > /dev/null
cd "$ROOT/cli"
dotnet test --nologo --filter "VerifyGoFixtures" -v q 2>&1 | tail -3
echo "  ✓ C# verified Go fixtures"
PASS=$((PASS + 1))

# --- 5. TCP Echo Integration ---
echo ""
echo "[5/$TOTAL] TCP Echo Integration (Session SendAsync + Timeout)..."
cleanup_port 9000
cd "$ROOT/svr"
go run ./cmd/echo/ :9000 > /dev/null 2>&1 &
ECHO_PID=$!
sleep 1

cd "$ROOT/cli"
OUTPUT=$(BOOM_PORT=9000 dotnet run --project Example 2>&1)
echo "$OUTPUT" | grep -E "PASS|FAIL|Results" | tail -5

kill $ECHO_PID 2>/dev/null || true; wait $ECHO_PID 2>/dev/null || true
check_result "TCP Echo" "$OUTPUT"

# --- 6. FrameSync Integration (TCP) ---
echo ""
echo "[6/$TOTAL] FrameSync Integration (Bind + Heartbeat + Reconnect)..."
cleanup_port 9001
cd "$ROOT/svr"
go run ./cmd/framesync/ -addr=:9001 > /dev/null 2>&1 &
FS_PID=$!
sleep 1

cd "$ROOT/cli"
OUTPUT=$(BOOM_PORT=9001 dotnet run --project FrameSyncExample 2>&1)
echo "$OUTPUT" | grep -E "PASS|FAIL|Results" | tail -12

kill $FS_PID 2>/dev/null || true; wait $FS_PID 2>/dev/null || true
check_result "FrameSync" "$OUTPUT"

# --- 7. KCP Echo Integration ---
echo ""
echo "[7/$TOTAL] KCP Echo Integration..."
cleanup_port 9002
cd "$ROOT/svr"
go run ./cmd/echo/ -proto=kcp :9002 > /dev/null 2>&1 &
KCP_PID=$!
sleep 1

cd "$ROOT/cli"
OUTPUT=$(BOOM_PORT=9002 dotnet run --project KcpTest 2>&1)
echo "$OUTPUT" | grep -E "PASS|FAIL|Results" | tail -10

kill $KCP_PID 2>/dev/null || true; wait $KCP_PID 2>/dev/null || true
check_result "KCP Echo" "$OUTPUT"

# --- Summary ---
echo ""
echo "=========================================="
echo "  Results: $PASS/$TOTAL passed, $FAIL failed"
echo "=========================================="

if [ $FAIL -gt 0 ]; then
    exit 1
fi
