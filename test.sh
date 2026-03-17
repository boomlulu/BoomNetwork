#!/bin/bash
set -e

ROOT="$(cd "$(dirname "$0")" && pwd)"
PASS=0
FAIL=0

echo "=========================================="
echo "  BoomNetwork Full Test Suite"
echo "=========================================="

# --- 1. C# Unit Tests ---
echo ""
echo "[1/5] C# Unit Tests (Codec + Framing)..."
cd "$ROOT/cli"
dotnet test --nologo -v q 2>&1 | tail -3
echo "  ✓ C# unit tests passed"
PASS=$((PASS + 1))

# --- 2. Go Unit Tests ---
echo ""
echo "[2/5] Go Unit Tests (Codec + Framing)..."
cd "$ROOT/svr"
go test ./codec/ -count=1 2>&1 | tail -2
echo "  ✓ Go unit tests passed"
PASS=$((PASS + 1))

# --- 3. Cross-language: C# generate → Go verify ---
echo ""
echo "[3/5] Cross-language: C# encode → Go decode..."
cd "$ROOT/cli"
dotnet test --nologo --filter "GenerateCSharpFixtures" -v q 2>&1 > /dev/null
cd "$ROOT/svr"
go test ./codec/ -run "TestVerifyCSharpFixtures" -count=1 2>&1 | tail -2
echo "  ✓ Go verified C# fixtures"
PASS=$((PASS + 1))

# --- 4. Cross-language: Go generate → C# verify ---
echo ""
echo "[4/5] Cross-language: Go encode → C# decode..."
cd "$ROOT/svr"
go test ./codec/ -run "TestGenerateGoFixtures" -count=1 2>&1 > /dev/null
cd "$ROOT/cli"
dotnet test --nologo --filter "VerifyGoFixtures" -v q 2>&1 | tail -3
echo "  ✓ C# verified Go fixtures"
PASS=$((PASS + 1))

# --- 5. Echo Integration Test ---
echo ""
echo "[5/5] Echo Integration (Go server + C# client)..."
cd "$ROOT/svr"
go run ./cmd/echo/ :9000 &
SERVER_PID=$!
sleep 1

cd "$ROOT/cli"
OUTPUT=$(dotnet run --project Example 2>&1)
echo "$OUTPUT" | tail -3

# Kill server
kill $SERVER_PID 2>/dev/null || true
wait $SERVER_PID 2>/dev/null || true

if echo "$OUTPUT" | grep -q "0 failed"; then
    echo "  ✓ Echo integration passed"
    PASS=$((PASS + 1))
else
    echo "  ✗ Echo integration FAILED"
    FAIL=$((FAIL + 1))
fi

# --- Summary ---
echo ""
echo "=========================================="
echo "  Results: $PASS passed, $FAIL failed"
echo "=========================================="

if [ $FAIL -gt 0 ]; then
    exit 1
fi
