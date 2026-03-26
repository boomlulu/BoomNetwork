package main

import (
	"testing"
)

func TestMsgRing_PushAndRecent(t *testing.T) {
	var ring msgRing

	// Push 5 entries
	for i := 0; i < 5; i++ {
		ring.Push(MsgEntry{Ts: int64(i), Dir: "rx", Pid: int32(i + 1)})
	}

	recent := ring.Recent(3)
	if len(recent) != 3 {
		t.Fatalf("expected 3 recent, got %d", len(recent))
	}

	// Recent returns newest first
	if recent[0].Ts != 4 {
		t.Errorf("most recent should be Ts=4, got %d", recent[0].Ts)
	}
	if recent[2].Ts != 2 {
		t.Errorf("third most recent should be Ts=2, got %d", recent[2].Ts)
	}
}

func TestMsgRing_RecentAll(t *testing.T) {
	var ring msgRing

	ring.Push(MsgEntry{Ts: 10})
	ring.Push(MsgEntry{Ts: 20})

	// Request more than available
	recent := ring.Recent(100)
	if len(recent) != 2 {
		t.Fatalf("expected 2, got %d", len(recent))
	}
}

func TestMsgRing_WrapAround(t *testing.T) {
	var ring msgRing

	// Fill beyond ring capacity
	for i := 0; i < msgRingSize+10; i++ {
		ring.Push(MsgEntry{Ts: int64(i)})
	}

	recent := ring.Recent(msgRingSize)
	if len(recent) != msgRingSize {
		t.Fatalf("expected %d, got %d", msgRingSize, len(recent))
	}

	// Newest should be the last pushed
	if recent[0].Ts != int64(msgRingSize+10-1) {
		t.Errorf("newest should be %d, got %d", msgRingSize+10-1, recent[0].Ts)
	}

	// Oldest should be the 10th pushed (0-9 were overwritten)
	if recent[msgRingSize-1].Ts != 10 {
		t.Errorf("oldest should be 10, got %d", recent[msgRingSize-1].Ts)
	}
}

func TestMsgRing_NotifyCh(t *testing.T) {
	var ring msgRing
	ch := make(chan MsgEntry, 10)
	ring.SetNotifyCh(ch)

	ring.Push(MsgEntry{Ts: 42, Dir: "tx"})

	select {
	case e := <-ch:
		if e.Ts != 42 || e.Dir != "tx" {
			t.Errorf("unexpected entry: %+v", e)
		}
	default:
		t.Error("expected notification on channel")
	}
}

func TestMsgRing_RoomIDInEntry(t *testing.T) {
	var ring msgRing

	ring.Push(MsgEntry{Ts: 1, Pid: 10, RoomID: 5, MatchKey: "demo"})

	recent := ring.Recent(1)
	if recent[0].RoomID != 5 {
		t.Errorf("expected RoomID=5, got %d", recent[0].RoomID)
	}
	if recent[0].MatchKey != "demo" {
		t.Errorf("expected MatchKey=demo, got %q", recent[0].MatchKey)
	}
}
