package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"

	"github.com/boom/boomnetwork/framesync"
)

// ensureTestGlobals ensures package-level globals are usable in tests
func ensureTestGlobals() {
	if roomMgr == nil {
		roomMgr = framesync.NewRoomManager(framesync.RoomConfig{
			FrameRate:       20,
			FrameBufferSize: 100,
		})
	}
	if LogBuf == nil {
		LogBuf = NewLogRingBuffer(100)
	}
}

// ===================== /health =====================

func TestHealth_GET(t *testing.T) {
	ensureTestGlobals()
	req := httptest.NewRequest(http.MethodGet, "/health", nil)
	rec := httptest.NewRecorder()
	handleHealth(rec, req)

	if rec.Code != 200 {
		t.Fatalf("expected 200, got %d", rec.Code)
	}

	var body map[string]interface{}
	if err := json.Unmarshal(rec.Body.Bytes(), &body); err != nil {
		t.Fatalf("invalid JSON: %v", err)
	}
	if body["status"] != "ok" {
		t.Fatalf("expected status=ok, got %v", body["status"])
	}
	if _, ok := body["rooms"]; !ok {
		t.Fatal("missing 'rooms' field")
	}
	if _, ok := body["uptime"]; !ok {
		t.Fatal("missing 'uptime' field")
	}
}

func TestHealth_POST_NotAllowed(t *testing.T) {
	ensureTestGlobals()
	req := httptest.NewRequest(http.MethodPost, "/health", nil)
	rec := httptest.NewRecorder()
	handleHealth(rec, req)

	if rec.Code != http.StatusMethodNotAllowed {
		t.Fatalf("expected 405, got %d", rec.Code)
	}
}

// ===================== withAuth =====================

func TestWithAuth_NoToken(t *testing.T) {
	called := false
	handler := withAuth("", func(w http.ResponseWriter, r *http.Request) {
		called = true
		w.WriteHeader(200)
	})
	req := httptest.NewRequest(http.MethodGet, "/test", nil)
	rec := httptest.NewRecorder()
	handler(rec, req)

	if !called {
		t.Fatal("handler should be called when token is empty (no auth)")
	}
}

func TestWithAuth_ValidToken(t *testing.T) {
	called := false
	handler := withAuth("secret123", func(w http.ResponseWriter, r *http.Request) {
		called = true
		w.WriteHeader(200)
	})
	req := httptest.NewRequest(http.MethodGet, "/test", nil)
	req.Header.Set("Authorization", "Bearer secret123")
	rec := httptest.NewRecorder()
	handler(rec, req)

	if !called {
		t.Fatal("handler should be called with valid token")
	}
}

func TestWithAuth_InvalidToken(t *testing.T) {
	called := false
	handler := withAuth("secret123", func(w http.ResponseWriter, r *http.Request) {
		called = true
	})
	req := httptest.NewRequest(http.MethodGet, "/test", nil)
	req.Header.Set("Authorization", "Bearer wrong")
	rec := httptest.NewRecorder()
	handler(rec, req)

	if called {
		t.Fatal("handler should NOT be called with invalid token")
	}
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("expected 401, got %d", rec.Code)
	}
}

func TestWithAuth_MissingToken(t *testing.T) {
	handler := withAuth("secret123", func(w http.ResponseWriter, r *http.Request) {})
	req := httptest.NewRequest(http.MethodGet, "/test", nil)
	rec := httptest.NewRecorder()
	handler(rec, req)

	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("expected 401, got %d", rec.Code)
	}
}

// ===================== /stats =====================

func TestStats_GET(t *testing.T) {
	ensureTestGlobals()
	GameStats.RecordRx(100)
	GameStats.RecordTx(200)

	req := httptest.NewRequest(http.MethodGet, "/stats", nil)
	rec := httptest.NewRecorder()
	handleStats(rec, req)

	if rec.Code != 200 {
		t.Fatalf("expected 200, got %d", rec.Code)
	}

	var body map[string]interface{}
	if err := json.Unmarshal(rec.Body.Bytes(), &body); err != nil {
		t.Fatalf("invalid JSON: %v", err)
	}
	if body["game_rx_total"] == nil {
		t.Fatal("missing game_rx_total")
	}
}

// ===================== /rooms =====================

func TestRooms_GET_Empty(t *testing.T) {
	ensureTestGlobals()
	req := httptest.NewRequest(http.MethodGet, "/rooms", nil)
	rec := httptest.NewRecorder()
	handleRooms(rec, req)

	if rec.Code != 200 {
		t.Fatalf("expected 200, got %d", rec.Code)
	}
}

// ===================== /rooms/create =====================

func TestCreateRoom_POST(t *testing.T) {
	ensureTestGlobals()
	req := httptest.NewRequest(http.MethodPost, "/rooms/create?max_players=6&match_key=test", nil)
	rec := httptest.NewRecorder()
	handleAdminCreateRoom(rec, req)

	if rec.Code != 200 {
		t.Fatalf("expected 200, got %d", rec.Code)
	}
	var body map[string]interface{}
	json.Unmarshal(rec.Body.Bytes(), &body)
	if body["ok"] != true {
		t.Fatal("expected ok=true")
	}
	if body["room_id"] == nil {
		t.Fatal("missing room_id")
	}
}

// ===================== /rooms/stop/{id} =====================

func TestStopRoom_NotFound(t *testing.T) {
	ensureTestGlobals()
	req := httptest.NewRequest(http.MethodPost, "/rooms/stop/99999", nil)
	rec := httptest.NewRecorder()
	handleStopRoom(rec, req)

	if rec.Code != http.StatusNotFound {
		t.Fatalf("expected 404, got %d", rec.Code)
	}
}

func TestStopRoom_InvalidId(t *testing.T) {
	ensureTestGlobals()
	req := httptest.NewRequest(http.MethodPost, "/rooms/stop/abc", nil)
	rec := httptest.NewRecorder()
	handleStopRoom(rec, req)

	if rec.Code != http.StatusBadRequest {
		t.Fatalf("expected 400, got %d", rec.Code)
	}
}

// ===================== /kick/{pid} =====================

func TestKick_NotFound(t *testing.T) {
	ensureTestGlobals()
	req := httptest.NewRequest(http.MethodPost, "/kick/99999", nil)
	rec := httptest.NewRecorder()
	handleKick(rec, req)

	if rec.Code != http.StatusNotFound {
		t.Fatalf("expected 404, got %d", rec.Code)
	}
}

func TestKick_InvalidPid(t *testing.T) {
	ensureTestGlobals()
	req := httptest.NewRequest(http.MethodPost, "/kick/abc", nil)
	rec := httptest.NewRecorder()
	handleKick(rec, req)

	if rec.Code != http.StatusBadRequest {
		t.Fatalf("expected 400, got %d", rec.Code)
	}
}

// ===================== /netsim =====================

func TestNetSim_GET(t *testing.T) {
	ensureTestGlobals()
	req := httptest.NewRequest(http.MethodGet, "/netsim", nil)
	rec := httptest.NewRecorder()
	handleNetSim(rec, req)

	if rec.Code != 200 {
		t.Fatalf("expected 200, got %d", rec.Code)
	}
	var body map[string]interface{}
	json.Unmarshal(rec.Body.Bytes(), &body)
	if _, ok := body["enabled"]; !ok {
		t.Fatal("missing 'enabled' field")
	}
}

func TestNetSim_POST(t *testing.T) {
	ensureTestGlobals()
	// Reset
	GlobalNetSim.SetEnabled(false)
	atomic.StoreInt32(&GlobalNetSim.LatencyMs, 0)

	body := `{"enabled":true,"latency_ms":100,"jitter_ms":20,"loss_percent":5}`
	req := httptest.NewRequest(http.MethodPost, "/netsim", strings.NewReader(body))
	rec := httptest.NewRecorder()
	handleNetSim(rec, req)

	if rec.Code != 200 {
		t.Fatalf("expected 200, got %d", rec.Code)
	}
	if !GlobalNetSim.IsEnabled() {
		t.Fatal("netsim should be enabled after POST")
	}
	if atomic.LoadInt32(&GlobalNetSim.LatencyMs) != 100 {
		t.Fatal("latency should be 100")
	}
	if atomic.LoadInt32(&GlobalNetSim.JitterMs) != 20 {
		t.Fatal("jitter should be 20")
	}
	if atomic.LoadInt32(&GlobalNetSim.LossPercent) != 5 {
		t.Fatal("loss should be 5")
	}
}

// ===================== /logs =====================

func TestLogs_GET(t *testing.T) {
	ensureTestGlobals()
	LogBuf.Add(LogEntry{Level: "INFO", Msg: "test log"})

	req := httptest.NewRequest(http.MethodGet, "/logs?lines=10&level=INFO", nil)
	rec := httptest.NewRecorder()
	handleLogs(rec, req)

	if rec.Code != 200 {
		t.Fatalf("expected 200, got %d", rec.Code)
	}
	var entries []LogEntry
	json.Unmarshal(rec.Body.Bytes(), &entries)
	if len(entries) == 0 {
		t.Fatal("expected at least 1 log entry")
	}
}

// ===================== /rooms/inspect/{id} =====================

func TestRoomInspect_NotFound(t *testing.T) {
	ensureTestGlobals()
	req := httptest.NewRequest(http.MethodGet, "/rooms/inspect/99999", nil)
	rec := httptest.NewRecorder()
	handleRoomInspect(rec, req)

	if rec.Code != http.StatusNotFound {
		t.Fatalf("expected 404, got %d", rec.Code)
	}
}

func TestRoomInspect_InvalidId(t *testing.T) {
	ensureTestGlobals()
	req := httptest.NewRequest(http.MethodGet, "/rooms/inspect/abc", nil)
	rec := httptest.NewRecorder()
	handleRoomInspect(rec, req)

	if rec.Code != http.StatusBadRequest {
		t.Fatalf("expected 400, got %d", rec.Code)
	}
}

// ===================== /messages =====================

func TestMessages_GET(t *testing.T) {
	ensureTestGlobals()
	MsgLog.Push(MsgEntry{Dir: "rx", Cmd: 1, Name: "SessionBind", Size: 10})

	req := httptest.NewRequest(http.MethodGet, "/messages?limit=5", nil)
	rec := httptest.NewRecorder()
	handleMessages(rec, req)

	if rec.Code != 200 {
		t.Fatalf("expected 200, got %d", rec.Code)
	}
}

// ===================== /perf =====================

func TestPerf_GET(t *testing.T) {
	ensureTestGlobals()
	// Clear perf cache
	atomic.StoreInt64(&perfCacheTime, 0)
	perfCachedJSON = nil

	req := httptest.NewRequest(http.MethodGet, "/perf", nil)
	rec := httptest.NewRecorder()
	handlePerf(rec, req)

	if rec.Code != 200 {
		t.Fatalf("expected 200, got %d", rec.Code)
	}
	var body map[string]interface{}
	json.Unmarshal(rec.Body.Bytes(), &body)
	if body["goroutines"] == nil {
		t.Fatal("missing goroutines field")
	}
}

// ===================== jsonError =====================

func TestJsonError(t *testing.T) {
	rec := httptest.NewRecorder()
	jsonError(rec, 418, "teapot")

	if rec.Code != 418 {
		t.Fatalf("expected 418, got %d", rec.Code)
	}
	var body map[string]string
	json.Unmarshal(rec.Body.Bytes(), &body)
	if body["error"] != "teapot" {
		t.Fatalf("expected error=teapot, got %s", body["error"])
	}
}
