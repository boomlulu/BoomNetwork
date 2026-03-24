package main

import (
	"encoding/json"
	"fmt"
	"log"
	"net/http"
	"strconv"
	"sync/atomic"
	"time"
)

var serverStartTime = time.Now()

// startAdminServer 启动 Admin HTTP 服务
func startAdminServer(addr string) {
	mux := http.NewServeMux()
	mux.HandleFunc("/health", handleHealth)
	mux.HandleFunc("/stats", handleStats)
	mux.HandleFunc("/messages", handleMessages)

	// GM 流量统计中间件
	handler := gmTrafficMiddleware(mux)

	log.Printf("[Admin] Listening on %s\n", addr)
	if err := http.ListenAndServe(addr, handler); err != nil {
		log.Printf("[Admin] Failed: %v\n", err)
	}
}

// ===================== GM 流量中间件 =====================

type countingWriter struct {
	http.ResponseWriter
	bytes int64
}

func (cw *countingWriter) Write(b []byte) (int, error) {
	n, err := cw.ResponseWriter.Write(b)
	cw.bytes += int64(n)
	return n, err
}

func gmTrafficMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// RX: 请求 URL + 固定头部估算
		GmStats.RecordRx(int64(len(r.URL.String()) + 200))
		// TX: 包装 ResponseWriter 精确计数
		cw := &countingWriter{ResponseWriter: w}
		next.ServeHTTP(cw, r)
		GmStats.RecordTx(cw.bytes)
	})
}

// ===================== /health =====================

func handleHealth(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	rooms := roomMgr.RoomCount()
	players := countOnlinePlayers()
	uptime := time.Since(serverStartTime).Truncate(time.Second).String()

	w.Header().Set("Content-Type", "application/json")
	fmt.Fprintf(w, `{"status":"ok","rooms":%d,"players":%d,"uptime":%q}`,
		rooms, players, uptime)
}

// ===================== /stats =====================

func handleStats(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	g := GameStats.Snapshot()
	m := GmStats.Snapshot()

	w.Header().Set("Content-Type", "application/json")
	fmt.Fprintf(w,
		`{"game_rx_total":%d,"game_tx_total":%d,"game_rx_1min":%d,"game_tx_1min":%d,"game_rx_5sec":%d,"game_tx_5sec":%d,`+
			`"gm_rx_total":%d,"gm_tx_total":%d,"gm_rx_1min":%d,"gm_tx_1min":%d,"gm_rx_5sec":%d,"gm_tx_5sec":%d}`,
		g.RxTotal, g.TxTotal, g.Rx1Min, g.Tx1Min, g.Rx5Sec, g.Tx5Sec,
		m.RxTotal, m.TxTotal, m.Rx1Min, m.Tx1Min, m.Rx5Sec, m.Tx5Sec,
	)
}

// ===================== /messages =====================

func handleMessages(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	limit := 100
	if v := r.URL.Query().Get("limit"); v != "" {
		if n, err := strconv.Atoi(v); err == nil && n > 0 && n <= 500 {
			limit = n
		}
	}

	msgs := MsgLog.Recent(limit)
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(msgs)
}

// ===================== Helpers =====================

func countOnlinePlayers() int {
	var count int64
	connPlayerMap.Range(func(_, _ any) bool {
		atomic.AddInt64(&count, 1)
		return true
	})
	return int(count)
}

func jsonError(w http.ResponseWriter, code int, msg string) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	b, _ := json.Marshal(map[string]string{"error": msg})
	w.Write(b)
}
