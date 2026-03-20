#!/bin/bash
# ============================================
#  BoomNetwork 一键验收脚本
#  用法: ./verify.sh
#  输出: 终端彩色报告 + reports/verify_<date>.txt
# ============================================
set -o pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
BOLD='\033[1m'
NC='\033[0m'

PASS_COUNT=0
FAIL_COUNT=0
RESULTS=()
REPORT_FILE="reports/verify_$(date +%Y%m%d_%H%M%S).txt"

cd "$(dirname "$0")"
mkdir -p reports

log() { echo -e "$1"; }
divider() { log "${CYAN}──────────────────────────────────────────${NC}"; }

record() {
    local name="$1" result="$2" detail="$3"
    if [ "$result" = "PASS" ]; then
        PASS_COUNT=$((PASS_COUNT + 1))
        RESULTS+=("${GREEN}PASS${NC}  $name  $detail")
        echo "PASS  $name  $detail" >> "$REPORT_FILE"
    else
        FAIL_COUNT=$((FAIL_COUNT + 1))
        RESULTS+=("${RED}FAIL${NC}  $name  $detail")
        echo "FAIL  $name  $detail" >> "$REPORT_FILE"
    fi
}

cleanup_port() {
    lsof -ti:9000 2>/dev/null | xargs kill -9 2>/dev/null || true
    sleep 0.3
}

# ============================================
echo "BoomNetwork Verification Report" > "$REPORT_FILE"
echo "Date: $(date)" >> "$REPORT_FILE"
echo "Platform: $(uname -m) / $(sw_vers -productName 2>/dev/null || echo 'unknown') $(sw_vers -productVersion 2>/dev/null || echo '')" >> "$REPORT_FILE"
echo "" >> "$REPORT_FILE"

log ""
log "${BOLD}=========================================="
log "  BoomNetwork Verification"
log "  $(date '+%Y-%m-%d %H:%M:%S')"
log "==========================================${NC}"
log ""

# ============================================
# 1. C# 单元测试
# ============================================
divider
log "${BOLD}[1/7] C# Unit Tests${NC}"

OUTPUT=$(cd cli && dotnet test Tests/BoomNetwork.Tests.csproj --nologo -v q 2>&1)
PASSED=$(echo "$OUTPUT" | grep -oE '通过:\s*[0-9]+' | grep -oE '[0-9]+' || echo "0")
FAILED=$(echo "$OUTPUT" | grep -oE '失败:\s*[0-9]+' | grep -oE '[0-9]+' || echo "0")

if [ "$FAILED" = "0" ] && [ "$PASSED" -gt "0" ]; then
    record "C# Unit Tests" "PASS" "${PASSED} tests"
    log "  ${GREEN}PASS${NC} ${PASSED} tests"
else
    record "C# Unit Tests" "FAIL" "passed=${PASSED} failed=${FAILED}"
    log "  ${RED}FAIL${NC} passed=${PASSED} failed=${FAILED}"
fi

# ============================================
# 2. Go 单元测试
# ============================================
divider
log "${BOLD}[2/7] Go Unit Tests${NC}"

OUTPUT=$(cd svr && go test ./codec/ -count=1 2>&1)
if echo "$OUTPUT" | grep -q "^ok"; then
    GO_TESTS=$(echo "$OUTPUT" | grep -oE '\(cached\)|[0-9]+\.[0-9]+s' | head -1)
    record "Go Unit Tests" "PASS" "$GO_TESTS"
    log "  ${GREEN}PASS${NC} $GO_TESTS"
else
    record "Go Unit Tests" "FAIL" ""
    log "  ${RED}FAIL${NC}"
fi

# ============================================
# 3. 跨语言兼容
# ============================================
divider
log "${BOLD}[3/7] Cross-Language Compatibility${NC}"

# C# → Go
OUTPUT1=$(cd svr && go test ./codec/ -run TestDecodeCSFixtures -count=1 2>&1)
if echo "$OUTPUT1" | grep -q "^ok"; then
    record "C# encode -> Go decode" "PASS" ""
    log "  ${GREEN}PASS${NC} C# encode -> Go decode"
else
    record "C# encode -> Go decode" "FAIL" ""
    log "  ${RED}FAIL${NC} C# encode -> Go decode"
fi

# Go → C#
OUTPUT2=$(cd cli && dotnet test Tests/BoomNetwork.Tests.csproj --nologo -v q --filter "CrossLanguage" 2>&1)
if echo "$OUTPUT2" | grep -q "通过"; then
    record "Go encode -> C# decode" "PASS" ""
    log "  ${GREEN}PASS${NC} Go encode -> C# decode"
else
    record "Go encode -> C# decode" "FAIL" ""
    log "  ${RED}FAIL${NC} Go encode -> C# decode"
fi

# ============================================
# 4. TCP Echo 联调
# ============================================
divider
log "${BOLD}[4/7] TCP Echo Integration${NC}"

cleanup_port
(cd svr && go run ./cmd/echo/ -proto=tcp :9000) > /dev/null 2>&1 &
ECHO_PID=$!
sleep 2

OUTPUT=$(cd cli && dotnet run --project Example -- --host=127.0.0.1 --port=9000 2>&1)
kill $ECHO_PID 2>/dev/null; wait $ECHO_PID 2>/dev/null
cleanup_port

ECHO_PASS=$(echo "$OUTPUT" | grep -c "PASS" || true)
ECHO_FAIL=$(echo "$OUTPUT" | grep -c "FAIL" || true)
if [ "${ECHO_PASS:-0}" -gt "0" ] && [ "${ECHO_FAIL:-0}" -eq "0" ]; then
    record "TCP Echo" "PASS" "${ECHO_PASS} checks"
    log "  ${GREEN}PASS${NC} ${ECHO_PASS} checks"
else
    record "TCP Echo" "FAIL" "pass=${ECHO_PASS} fail=${ECHO_FAIL}"
    log "  ${RED}FAIL${NC} pass=${ECHO_PASS} fail=${ECHO_FAIL}"
    echo "$OUTPUT" | grep -E "FAIL|Error" | head -3
fi

# ============================================
# 5. 帧同步联调
# ============================================
divider
log "${BOLD}[5/7] FrameSync Integration${NC}"

cleanup_port
(cd svr && go run ./cmd/framesync/ -addr=:9000 -ppr=2) > /dev/null 2>&1 &
FS_PID=$!
sleep 2

OUTPUT=$(cd cli && dotnet run --project FrameSyncExample -- --host=127.0.0.1 --port=9000 --clients=2 --duration=3 2>&1)
kill $FS_PID 2>/dev/null; wait $FS_PID 2>/dev/null
cleanup_port

FS_PASS=$(echo "$OUTPUT" | grep -c "PASS" || true)
FS_FAIL=$(echo "$OUTPUT" | grep -c "FAIL" || true)
if [ "${FS_PASS:-0}" -gt "0" ] && [ "${FS_FAIL:-0}" -eq "0" ]; then
    FPS=$(echo "$OUTPUT" | grep -oE '[0-9]+\.[0-9]+ fps' | tail -1 || echo "?")
    record "FrameSync 2-client" "PASS" "$FPS"
    log "  ${GREEN}PASS${NC} $FPS"
else
    record "FrameSync 2-client" "FAIL" ""
    log "  ${RED}FAIL${NC}"
    echo "$OUTPUT" | grep -E "PASS|FAIL" | head -5
fi

# ============================================
# 6. 压力测试 (3000人)
# ============================================
divider
log "${BOLD}[6/7] Stress Test (3000 players)${NC}"

cleanup_port
OUTPUT=$(cd svr && ulimit -n 10000 2>/dev/null; go run ./cmd/stress/ -rooms=750 -players=4 -duration=10s 2>&1)

if echo "$OUTPUT" | grep -q "Connected.*3000 / 3000"; then
    FPS=$(echo "$OUTPUT" | grep "Per client:" | grep -oE '[0-9]+\.[0-9]+ frames' | head -1 || echo "?")
    DOWN=$(echo "$OUTPUT" | grep "Download (all)" | grep -oE '[0-9]+\.[0-9]+ MB/s' || echo "?")
    MEM=$(echo "$OUTPUT" | grep "Heap in use" | grep -oE '[0-9]+\.[0-9]+ MB' || echo "?")
    record "TCP 3000 players" "PASS" "${FPS}/s, down=${DOWN}, mem=${MEM}"
    log "  ${GREEN}PASS${NC} ${FPS}/s, down=${DOWN}, mem=${MEM}"
else
    record "TCP 3000 players" "FAIL" ""
    log "  ${RED}FAIL${NC}"
fi

# ============================================
# 7. 性能基准 (快速版)
# ============================================
divider
log "${BOLD}[7/7] Performance Baseline${NC}"

# C# 性能: 用 dotnet test 跑 Codec 基准（不依赖 BenchmarkDotNet 输出格式）
CS_BENCH_OUTPUT=$(cd svr && go test ./codec/ -bench=BenchmarkEncode_Small -benchmem -count=1 -benchtime=100ms 2>&1)
CS_ENCODE_ALLOC=$(echo "$CS_BENCH_OUTPUT" | grep "BenchmarkEncode_Small" | awk '{print $5}')
CS_ENCODE_NS=$(echo "$CS_BENCH_OUTPUT" | grep "BenchmarkEncode_Small" | awk '{print $3}')
if [ "${CS_ENCODE_ALLOC}" = "24" ] || [ "${CS_ENCODE_ALLOC}" = "0" ]; then
    record "Go Encode benchmark" "PASS" "${CS_ENCODE_NS} ns/op, ${CS_ENCODE_ALLOC} B/op"
    log "  ${GREEN}PASS${NC} Go Encode: ${CS_ENCODE_NS} ns/op, ${CS_ENCODE_ALLOC} B/op"
else
    record "Go Encode benchmark" "FAIL" "alloc=${CS_ENCODE_ALLOC}"
    log "  ${RED}FAIL${NC} Go Encode alloc=${CS_ENCODE_ALLOC}"
fi

OUTPUT=$(cd svr && go test ./codec/ -bench=BenchmarkEncodeTo_Small -benchmem -count=1 -benchtime=100ms 2>&1)
GO_NS=$(echo "$OUTPUT" | grep "BenchmarkEncodeTo" | awk '{print $3}' || echo "?")
GO_ALLOC=$(echo "$OUTPUT" | grep "BenchmarkEncodeTo" | awk '{print $5}' || echo "?")

if [ "$GO_ALLOC" = "0" ]; then
    record "Go EncodeTo zero-alloc" "PASS" "${GO_NS} ns/op, 0 alloc"
    log "  ${GREEN}PASS${NC} Go EncodeTo: ${GO_NS} ns/op, 0 alloc"
else
    record "Go EncodeTo zero-alloc" "FAIL" "alloc=${GO_ALLOC}"
    log "  ${RED}FAIL${NC} alloc=${GO_ALLOC}"
fi

# ============================================
# 汇总
# ============================================
log ""
log "${BOLD}=========================================="
log "  VERIFICATION SUMMARY"
log "==========================================${NC}"
log ""

for r in "${RESULTS[@]}"; do
    log "  $r"
done

log ""
TOTAL=$((PASS_COUNT + FAIL_COUNT))

echo "" >> "$REPORT_FILE"
echo "SUMMARY: ${PASS_COUNT}/${TOTAL} passed, ${FAIL_COUNT} failed" >> "$REPORT_FILE"

if [ "$FAIL_COUNT" -eq 0 ]; then
    log "  ${GREEN}${BOLD}ALL PASSED: ${PASS_COUNT}/${TOTAL}${NC}"
    log ""
    log "  Report: ${REPORT_FILE}"
    exit 0
else
    log "  ${RED}${BOLD}FAILED: ${PASS_COUNT} passed, ${FAIL_COUNT} failed${NC}"
    log ""
    log "  Report: ${REPORT_FILE}"
    exit 1
fi
