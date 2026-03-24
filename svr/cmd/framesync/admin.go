package main

import (
	"encoding/json"
	"fmt"
	"log"
	"net/http"
	"sync/atomic"
	"time"
)

var serverStartTime = time.Now()

// startAdminServer 启动 Admin HTTP 服务
//
// 路由：
//   GET  /health     服务器健康状态
//   GET  /stats      流量统计（总量 + 近 1 分钟 + 近 5 秒）
//
// 后续扩展示例：
//   GET  /rooms      房间列表
//   POST /rooms/{id}/stop   强制停止房间
//   POST /kick/{pid}        踢出玩家
func startAdminServer(addr string) {
	mux := http.NewServeMux()
	mux.HandleFunc("/health", handleHealth)
	mux.HandleFunc("/stats", handleStats)

	log.Printf("[Admin] Listening on %s\n", addr)
	if err := http.ListenAndServe(addr, mux); err != nil {
		log.Printf("[Admin] Failed: %v\n", err)
	}
}

// handleHealth GET /health
//
// Response 200:
//
//	{
//	  "status":  "ok",
//	  "rooms":   3,
//	  "players": 12,
//	  "uptime":  "1h23m45s"
//	}
func handleHealth(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	rooms := roomMgr.RoomCount()
	players := countOnlinePlayers()
	uptime := time.Since(serverStartTime).Truncate(time.Second).String()

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	fmt.Fprintf(w, `{"status":"ok","rooms":%d,"players":%d,"uptime":%q}`,
		rooms, players, uptime)
}

// countOnlinePlayers 统计当前在线玩家数（已 SessionBind 的连接）
func countOnlinePlayers() int {
	var count int64
	connPlayerMap.Range(func(_, _ any) bool {
		atomic.AddInt64(&count, 1)
		return true
	})
	return int(count)
}

// handleStats GET /stats
//
// Response 200:
//
//	{
//	  "rx_total_bytes": 1234567,
//	  "tx_total_bytes": 2345678,
//	  "rx_1min_bytes":  12345,
//	  "tx_1min_bytes":  23456,
//	  "rx_5sec_bytes":  1234,
//	  "tx_5sec_bytes":  2345
//	}
func handleStats(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	s := Stats.Snapshot()
	w.Header().Set("Content-Type", "application/json")
	fmt.Fprintf(w,
		`{"rx_total_bytes":%d,"tx_total_bytes":%d,"rx_1min_bytes":%d,"tx_1min_bytes":%d,"rx_5sec_bytes":%d,"tx_5sec_bytes":%d}`,
		s.RxTotal, s.TxTotal, s.Rx1MinBytes, s.Tx1MinBytes, s.Rx5SecBytes, s.Tx5SecBytes,
	)
}

// jsonError 统一错误响应格式（供后续 GM 接口使用）
func jsonError(w http.ResponseWriter, code int, msg string) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	b, _ := json.Marshal(map[string]string{"error": msg})
	w.Write(b)
}
