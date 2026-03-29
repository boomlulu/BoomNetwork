package main

import (
	"testing"
)

// ===================== TrafficTracker =====================

func TestTrafficTracker_RecordAndSnapshot(t *testing.T) {
	tr := &TrafficTracker{}
	tr.RecordRx(100)
	tr.RecordRx(200)
	tr.RecordTx(50)

	snap := tr.Snapshot()
	if snap.RxTotal != 300 {
		t.Fatalf("expected RxTotal=300, got %d", snap.RxTotal)
	}
	if snap.TxTotal != 50 {
		t.Fatalf("expected TxTotal=50, got %d", snap.TxTotal)
	}
	// Recent 5sec should include current second's data
	if snap.Rx5Sec == 0 {
		t.Fatal("Rx5Sec should be > 0")
	}
}

func TestTrafficTracker_ZeroIgnored(t *testing.T) {
	tr := &TrafficTracker{}
	tr.RecordRx(0)
	tr.RecordRx(-10)
	tr.RecordTx(0)
	tr.RecordTx(-5)

	snap := tr.Snapshot()
	if snap.RxTotal != 0 || snap.TxTotal != 0 {
		t.Fatalf("zero/negative should be ignored: rx=%d tx=%d", snap.RxTotal, snap.TxTotal)
	}
}

// ===================== PlayerRates =====================

func TestPlayerRates_RecordAndRate(t *testing.T) {
	pr := &playerRate{counts: make(map[int32]*[ringSize]rateBucket)}
	// Record 10 messages for player 1
	for i := 0; i < 10; i++ {
		pr.Record(1)
	}

	rate := pr.Rate5Sec(1)
	if rate < 1.0 {
		t.Fatalf("expected rate >= 1.0, got %f", rate)
	}
}

func TestPlayerRates_ZeroPidIgnored(t *testing.T) {
	pr := &playerRate{counts: make(map[int32]*[ringSize]rateBucket)}
	pr.Record(0)
	pr.Record(-1)
	if len(pr.counts) != 0 {
		t.Fatal("zero/negative pid should be ignored")
	}
}

func TestPlayerRates_UnknownPlayer(t *testing.T) {
	pr := &playerRate{counts: make(map[int32]*[ringSize]rateBucket)}
	rate := pr.Rate5Sec(999)
	if rate != 0 {
		t.Fatalf("unknown player should return 0, got %f", rate)
	}
}

func TestPlayerRates_Remove(t *testing.T) {
	pr := &playerRate{counts: make(map[int32]*[ringSize]rateBucket)}
	pr.Record(5)
	pr.Remove(5)
	rate := pr.Rate5Sec(5)
	if rate != 0 {
		t.Fatalf("removed player should return 0, got %f", rate)
	}
}

func TestPlayerRates_TopPlayers(t *testing.T) {
	pr := &playerRate{counts: make(map[int32]*[ringSize]rateBucket)}
	// Player 1: 5 msgs, Player 2: 10 msgs, Player 3: 1 msg
	for i := 0; i < 5; i++ {
		pr.Record(1)
	}
	for i := 0; i < 10; i++ {
		pr.Record(2)
	}
	pr.Record(3)

	top := pr.TopPlayers(2)
	if len(top) != 2 {
		t.Fatalf("expected 2 results, got %d", len(top))
	}
	// Player 2 should be first (highest rate)
	if top[0].Pid != 2 {
		t.Fatalf("expected top player pid=2, got %d", top[0].Pid)
	}
	if top[1].Pid != 1 {
		t.Fatalf("expected second player pid=1, got %d", top[1].Pid)
	}
}

func TestPlayerRates_TopPlayers_LimitExceedsCount(t *testing.T) {
	pr := &playerRate{counts: make(map[int32]*[ringSize]rateBucket)}
	pr.Record(1)
	top := pr.TopPlayers(100)
	if len(top) != 1 {
		t.Fatalf("expected 1, got %d", len(top))
	}
}

// ===================== MsgName =====================

func TestMsgName_Core(t *testing.T) {
	name := MsgName(0, 1, 0, 0)
	if name != "SessionBind" {
		t.Fatalf("expected SessionBind, got %s", name)
	}
}

func TestMsgName_CoreUnknown(t *testing.T) {
	name := MsgName(0, 99, 0, 0)
	if name != "Core(99)" {
		t.Fatalf("expected Core(99), got %s", name)
	}
}

func TestMsgName_Extended(t *testing.T) {
	name := MsgName(1, 0, 5, 0)
	if name != "JoinRoom" {
		t.Fatalf("expected JoinRoom, got %s", name)
	}
}

func TestMsgName_Game(t *testing.T) {
	name := MsgName(2, 0, 0, 42)
	if name != "Game(42)" {
		t.Fatalf("expected Game(42), got %s", name)
	}
}

func TestMsgName_Unknown(t *testing.T) {
	name := MsgName(255, 0, 0, 0)
	if name != "Unknown" {
		t.Fatalf("expected Unknown, got %s", name)
	}
}

func TestCmdName_Compat(t *testing.T) {
	name := CmdName(6)
	if name != "FrameInput" {
		t.Fatalf("expected FrameInput, got %s", name)
	}
}
