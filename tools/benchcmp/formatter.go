package main

import (
	"fmt"
	"math"
	"strings"
)

// ANSI color codes
const (
	colorReset  = "\033[0m"
	colorGreen  = "\033[32m"
	colorRed    = "\033[31m"
	colorYellow = "\033[33m"
	colorCyan   = "\033[36m"
	colorBold   = "\033[1m"
	colorDim    = "\033[2m"
)

// WinnerInfo 胜负判断结果
type WinnerInfo struct {
	Text  string // e.g. "Go +15%" / "C# 2.3×" / "≈ tie"
	IsGo  bool
	IsCS  bool
	IsTie bool
}

func computeWinner(p MatchedPair) WinnerInfo {
	if p.Go == nil || p.CS == nil {
		return WinnerInfo{Text: "—"}
	}
	goNs := p.Go.NsPerOp
	csNs := p.CS.NsPerOp
	if goNs <= 0 || csNs <= 0 {
		return WinnerInfo{Text: "—"}
	}

	ratio := csNs / goNs // > 1 means Go wins, < 1 means C# wins
	pct := (ratio - 1) * 100

	const tiePct = 5.0
	if math.Abs(pct) < tiePct {
		return WinnerInfo{Text: "≈ tie", IsTie: true}
	}
	if ratio >= 2.0 {
		return WinnerInfo{Text: fmt.Sprintf("Go %.1f×", ratio), IsGo: true}
	}
	if 1/ratio >= 2.0 {
		return WinnerInfo{Text: fmt.Sprintf("C# %.1f×", 1/ratio), IsCS: true}
	}
	if pct > 0 {
		return WinnerInfo{Text: fmt.Sprintf("Go +%.0f%%", pct), IsGo: true}
	}
	return WinnerInfo{Text: fmt.Sprintf("C# +%.0f%%", -pct), IsCS: true}
}

// PrintTerminalTable 输出彩色终端表格
func PrintTerminalTable(pairs []MatchedPair) {
	if len(pairs) == 0 {
		fmt.Println("(no benchmarks)")
		return
	}

	// 计算列宽
	colLabel := 30
	colNs := 12
	colBytes := 10
	colAllocs := 10
	colWinner := 14

	for _, p := range pairs {
		if l := len(p.Label); l+2 > colLabel {
			colLabel = l + 2
		}
	}

	sep := func(l, m, r, h string) string {
		return l +
			strings.Repeat(h, colLabel+2) + m +
			strings.Repeat(h, colNs+2) + m +
			strings.Repeat(h, colNs+2) + m +
			strings.Repeat(h, colBytes+2) + m +
			strings.Repeat(h, colAllocs+2) + m +
			strings.Repeat(h, colWinner+2) + r
	}

	cell := func(s string, width int) string {
		if len(s) >= width {
			return " " + s[:width-1] + " "
		}
		return " " + s + strings.Repeat(" ", width-len(s)) + " "
	}

	topSep := sep("╔", "╦", "╗", "═")
	midSep := sep("╠", "╬", "╣", "═")
	botSep := sep("╚", "╩", "╝", "═")
	rowFmt := "║%s║%s║%s║%s║%s║%s║"

	fmt.Println(colorBold + topSep + colorReset)

	// Header
	fmt.Printf(rowFmt+"\n",
		colorBold+cell("Operation", colLabel)+colorReset,
		colorCyan+cell("Go ns/op", colNs)+colorReset,
		colorCyan+cell("C# ns/op", colNs)+colorReset,
		colorCyan+cell("Go B/op", colBytes)+colorReset,
		colorCyan+cell("Go allocs", colAllocs)+colorReset,
		colorBold+cell("Winner", colWinner)+colorReset,
	)
	fmt.Println(colorBold + midSep + colorReset)

	for _, p := range pairs {
		winner := computeWinner(p)

		goNs, csNs, goBytes, goAllocs := "—", "—", "—", "—"
		if p.Go != nil {
			goNs = FormatNs(p.Go.NsPerOp)
			goBytes = FormatBytes(p.Go.BytesPerOp)
			goAllocs = fmt.Sprintf("%.0f", p.Go.AllocsPerOp)
			if p.Go.AllocsPerOp < 0 {
				goAllocs = "—"
			}
		}
		if p.CS != nil {
			csNs = FormatNs(p.CS.NsPerOp)
		}

		winnerColored := winner.Text
		switch {
		case winner.IsGo:
			winnerColored = colorGreen + winner.Text + colorReset
		case winner.IsCS:
			winnerColored = colorRed + winner.Text + colorReset
		case winner.IsTie:
			winnerColored = colorYellow + winner.Text + colorReset
		}

		// winner cell 去掉 ANSI 码后计算对齐
		winnerPadded := " " + winnerColored + strings.Repeat(" ", max(0, colWinner-visibleLen(winner.Text))) + " "

		fmt.Printf(rowFmt+"\n",
			cell(p.Label, colLabel),
			cell(goNs, colNs),
			cell(csNs, colNs),
			cell(goBytes, colBytes),
			cell(goAllocs, colAllocs),
			winnerPadded,
		)
	}

	fmt.Println(colorBold + botSep + colorReset)
}

// PrintMarkdownTable 输出 Markdown 表格
func PrintMarkdownTable(pairs []MatchedPair) {
	if len(pairs) == 0 {
		fmt.Println("(no benchmarks)")
		return
	}

	fmt.Println("| Operation | Go ns/op | C# ns/op | Go B/op | Go allocs | Winner |")
	fmt.Println("|-----------|----------|----------|---------|-----------|--------|")

	for _, p := range pairs {
		winner := computeWinner(p)

		goNs, csNs, goBytes, goAllocs := "—", "—", "—", "—"
		if p.Go != nil {
			goNs = FormatNs(p.Go.NsPerOp)
			goBytes = FormatBytes(p.Go.BytesPerOp)
			goAllocs = fmt.Sprintf("%.0f", p.Go.AllocsPerOp)
			if p.Go.AllocsPerOp < 0 {
				goAllocs = "—"
			}
		}
		if p.CS != nil {
			csNs = FormatNs(p.CS.NsPerOp)
		}

		fmt.Printf("| %s | %s | %s | %s | %s | %s |\n",
			p.Label, goNs, csNs, goBytes, goAllocs, winner.Text)
	}
}

// PrintCompareLast 对比本次与上次运行的变化
func PrintCompareLast(current, last []MatchedPair) {
	if len(last) == 0 {
		fmt.Println(colorYellow + "No previous run to compare against." + colorReset)
		return
	}

	lastMap := make(map[string]MatchedPair)
	for _, p := range last {
		lastMap[p.Label] = p
	}

	fmt.Println(colorBold + "\n── vs last run ──" + colorReset)
	fmt.Printf("%-32s  %12s  %12s  %8s\n", "Operation", "Current", "Last", "Change")
	fmt.Println(strings.Repeat("─", 70))

	for _, cur := range current {
		prev, ok := lastMap[cur.Label]
		if !ok || cur.Go == nil || prev.Go == nil {
			continue
		}
		curNs := cur.Go.NsPerOp
		prevNs := prev.Go.NsPerOp
		if curNs <= 0 || prevNs <= 0 {
			continue
		}
		pct := (curNs/prevNs - 1) * 100
		changeStr := fmt.Sprintf("%+.1f%%", pct)
		color := colorReset
		switch {
		case pct < -5:
			color = colorGreen
			changeStr += " ✓"
		case pct > 5:
			color = colorRed
			changeStr += " ✗"
		}
		fmt.Printf("%-32s  %12s  %12s  %s%s%s\n",
			cur.Label,
			FormatNs(curNs),
			FormatNs(prevNs),
			color, changeStr, colorReset,
		)
	}
}

func visibleLen(s string) int {
	return len(s)
}

func max(a, b int) int {
	if a > b {
		return a
	}
	return b
}
