@echo off
REM BoomNetwork Benchmark Runner (Windows)
REM Usage:
REM   bench.bat          -- run Go benchmarks and show colored table (default)
REM   bench.bat all      -- run Go + C# and compare
REM   bench.bat md       -- output Markdown table
REM   bench.bat diff     -- run and compare with last run
REM   bench.bat help     -- show this message

setlocal EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
set "BENCHCMP_SRC=%SCRIPT_DIR%tools\benchcmp"
set "BENCHCMP_BIN=%BENCHCMP_SRC%\benchcmp.exe"
set "MODE=%~1"
set "BENCH_TIME=%BENCH_TIME%"
set "BENCH_COUNT=%BENCH_COUNT%"
if "!BENCH_TIME!"=="" set "BENCH_TIME=2s"
if "!BENCH_COUNT!"=="" set "BENCH_COUNT=3"

REM ── help ─────────────────────────────────────────────────────────────────────
if /I "!MODE!"=="help" goto :help
if /I "!MODE!"=="-h"   goto :help
if /I "!MODE!"=="--help" goto :help

REM ── check Go ─────────────────────────────────────────────────────────────────
where go >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Go is not installed or not in PATH.
  echo         Install from: https://go.dev/dl/
  exit /b 1
)

REM ── build benchcmp if needed ─────────────────────────────────────────────────
if not exist "!BENCHCMP_BIN!" (
  echo [BUILD] Building benchcmp...
  pushd "!BENCHCMP_SRC!"
  go build -o benchcmp.exe .
  popd
  if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
  )
  echo        Built: !BENCHCMP_BIN!
)

REM ── run ──────────────────────────────────────────────────────────────────────
set "BASE_FLAGS=--bench-time !BENCH_TIME! --count !BENCH_COUNT!"

if "!MODE!"==""     goto :go_only
if /I "!MODE!"=="go"   goto :go_only
if /I "!MODE!"=="all"  goto :all
if /I "!MODE!"=="md"   goto :md
if /I "!MODE!"=="diff" goto :diff

echo [ERROR] Unknown command: '!MODE!'
echo         Run 'bench.bat help' for usage.
exit /b 1

:go_only
"!BENCHCMP_BIN!" --go-only !BASE_FLAGS!
goto :eof

:all
where dotnet >nul 2>&1
if errorlevel 1 (
  echo [WARN] dotnet not found - falling back to Go-only.
  echo        Install from: https://dotnet.microsoft.com/download
  "!BENCHCMP_BIN!" --go-only !BASE_FLAGS!
) else (
  "!BENCHCMP_BIN!" !BASE_FLAGS!
)
goto :eof

:md
"!BENCHCMP_BIN!" --go-only --md --no-save !BASE_FLAGS!
goto :eof

:diff
"!BENCHCMP_BIN!" --go-only --compare-last !BASE_FLAGS!
goto :eof

:help
echo BoomNetwork Benchmark Runner
echo.
echo Usage:
echo   bench.bat          Run Go benchmarks, show colored table (default)
echo   bench.bat all      Run Go + C# and compare
echo   bench.bat md       Output Markdown table (paste into benchmark-report.md)
echo   bench.bat diff     Run Go benchmarks and compare with last run
echo   bench.bat help     Show this message
echo.
echo Environment variables:
echo   set BENCH_TIME=5s       Override benchmark duration (default: 2s)
echo   set BENCH_COUNT=5       Override repeat count for averaging (default: 3)
echo.
echo Examples:
echo   bench.bat
echo   bench.bat diff
echo   set BENCH_TIME=5s ^& bench.bat md
goto :eof
