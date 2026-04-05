package main

import (
	"encoding/json"
	"os"
	"time"
)

const historyFile = "history.json"

// HistoryRecord 一次运行的快照
type HistoryRecord struct {
	Timestamp time.Time     `json:"timestamp"`
	Pairs     []PairSnapshot `json:"pairs"`
}

// PairSnapshot 序列化用的简化结构
type PairSnapshot struct {
	Label       string  `json:"label"`
	GoNsPerOp   float64 `json:"go_ns_op,omitempty"`
	CSNsPerOp   float64 `json:"cs_ns_op,omitempty"`
	GoBytesPerOp float64 `json:"go_b_op,omitempty"`
	GoAllocs    float64 `json:"go_allocs_op,omitempty"`
}

// LoadHistory 从 history.json 加载历史记录
func LoadHistory(path string) ([]HistoryRecord, error) {
	data, err := os.ReadFile(path)
	if os.IsNotExist(err) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	var records []HistoryRecord
	if err := json.Unmarshal(data, &records); err != nil {
		return nil, err
	}
	return records, nil
}

// SaveHistory 将新记录追加到 history.json
func SaveHistory(path string, records []HistoryRecord, current []MatchedPair) ([]HistoryRecord, error) {
	rec := HistoryRecord{Timestamp: time.Now().UTC()}
	for _, p := range current {
		snap := PairSnapshot{Label: p.Label}
		if p.Go != nil {
			snap.GoNsPerOp = p.Go.NsPerOp
			snap.GoBytesPerOp = p.Go.BytesPerOp
			snap.GoAllocs = p.Go.AllocsPerOp
		}
		if p.CS != nil {
			snap.CSNsPerOp = p.CS.NsPerOp
		}
		rec.Pairs = append(rec.Pairs, snap)
	}
	records = append(records, rec)

	data, err := json.MarshalIndent(records, "", "  ")
	if err != nil {
		return records, err
	}
	return records, os.WriteFile(path, data, 0644)
}

// LastPairs 从最后一条记录还原 []MatchedPair（仅含 Go 数据，用于 --compare-last）
func LastPairs(records []HistoryRecord) []MatchedPair {
	if len(records) == 0 {
		return nil
	}
	last := records[len(records)-1]
	pairs := make([]MatchedPair, 0, len(last.Pairs))
	for _, s := range last.Pairs {
		p := MatchedPair{Label: s.Label}
		if s.GoNsPerOp > 0 {
			p.Go = &BenchResult{
				NsPerOp:     s.GoNsPerOp,
				BytesPerOp:  s.GoBytesPerOp,
				AllocsPerOp: s.GoAllocs,
				Source:      "go",
			}
		}
		if s.CSNsPerOp > 0 {
			p.CS = &BenchResult{
				NsPerOp: s.CSNsPerOp,
				Source:  "cs",
			}
		}
		pairs = append(pairs, p)
	}
	return pairs
}
