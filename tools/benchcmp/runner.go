package main

import (
	"bytes"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

// RunConfig 控制运行参数
type RunConfig struct {
	GoDir       string // svr/ 目录（含 go.mod）
	CSDir       string // cli/ 目录（含 .csproj）
	BenchTime   string // e.g. "2s"
	Count       int    // benchmark 运行次数（统计用）
	GoOnly      bool
	CSOnly      bool
	GoPackages  []string // e.g. ["./codec/", "./framesync/", "./transport/"]
}

// DefaultConfig 基于仓库根目录生成默认配置
func DefaultConfig(repoRoot string) RunConfig {
	return RunConfig{
		GoDir:     filepath.Join(repoRoot, "svr"),
		CSDir:     filepath.Join(repoRoot, "cli"),
		BenchTime: "2s",
		Count:     3,
		GoPackages: []string{"./codec/", "./framesync/", "./transport/"},
	}
}

// RunGoBenchmarks 执行 Go benchmark 并返回原始输出
func RunGoBenchmarks(cfg RunConfig) (string, error) {
	args := []string{
		"test",
		"-bench=.",
		"-benchmem",
		fmt.Sprintf("-benchtime=%s", cfg.BenchTime),
		fmt.Sprintf("-count=%d", cfg.Count),
		"-run=^$", // 不跑单元测试
	}
	args = append(args, cfg.GoPackages...)

	fmt.Fprintln(os.Stderr, colorDim+"$ go "+strings.Join(args, " ")+colorReset)

	cmd := exec.Command("go", args...)
	cmd.Dir = cfg.GoDir

	var out bytes.Buffer
	var errOut bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = &errOut

	if err := cmd.Run(); err != nil {
		// 即使部分包失败也尽量返回已有输出
		fmt.Fprintln(os.Stderr, colorYellow+"go test stderr: "+errOut.String()+colorReset)
		if out.Len() == 0 {
			return "", fmt.Errorf("go test failed: %w\n%s", err, errOut.String())
		}
	}
	return out.String(), nil
}

// RunCSBenchmarks 执行 C# benchmark 并返回原始输出
// 尝试 dotnet run --project Benchmark（BenchmarkDotNet），
// 若项目不存在则回落到 dotnet test --filter Category=Benchmark。
func RunCSBenchmarks(cfg RunConfig) (string, error) {
	// 优先查找 Benchmark 项目（BenchmarkDotNet 模式）
	benchProj := filepath.Join(cfg.CSDir, "Benchmark")
	if _, err := os.Stat(benchProj); err == nil {
		return runCSBenchmarkDotNet(cfg.CSDir, benchProj)
	}

	// 回落：dotnet test --filter
	return runCSDotnetTest(cfg.CSDir)
}

func runCSBenchmarkDotNet(csDir, projDir string) (string, error) {
	args := []string{"run", "--project", projDir, "-c", "Release", "--", "--exporters", "markdown"}
	fmt.Fprintln(os.Stderr, colorDim+"$ dotnet "+strings.Join(args, " ")+colorReset)

	cmd := exec.Command("dotnet", args...)
	cmd.Dir = csDir
	out, err := cmd.CombinedOutput()
	return string(out), err
}

func runCSDotnetTest(csDir string) (string, error) {
	args := []string{"test", "Tests/", "--filter", "Category=Benchmark", "--logger", "console;verbosity=normal"}
	fmt.Fprintln(os.Stderr, colorDim+"$ dotnet "+strings.Join(args, " ")+colorReset)

	cmd := exec.Command("dotnet", args...)
	cmd.Dir = csDir
	out, err := cmd.CombinedOutput()
	return string(out), err
}
