package session

import (
	"testing"

	"github.com/boom/boomnetwork/codec"
	"github.com/boom/boomnetwork/transport"
)

func makeMsg(cmdType byte, cmd byte, extCmd uint16, gameCmd uint32) *codec.Message {
	return &codec.Message{CmdType: cmdType, Cmd: cmd, ExtCmd: extCmd, GameCmd: gameCmd}
}

func TestRouter_CoreDispatch(t *testing.T) {
	r := NewRouter()
	called := false
	r.OnCore(1, func(conn *transport.Conn, msg *codec.Message) *codec.Message {
		called = true
		return &codec.Message{Cmd: 2}
	})
	r.Freeze()

	rsp := r.Dispatch(nil, makeMsg(codec.CmdTypeCore, 1, 0, 0))
	if !called {
		t.Fatal("core handler not called")
	}
	if rsp == nil || rsp.Cmd != 2 {
		t.Fatal("unexpected response")
	}
}

func TestRouter_ExtDispatch(t *testing.T) {
	r := NewRouter()
	called := false
	r.OnExt(100, func(conn *transport.Conn, msg *codec.Message) *codec.Message {
		called = true
		return nil
	})
	r.Freeze()

	rsp := r.Dispatch(nil, makeMsg(codec.CmdTypeExtended, 0, 100, 0))
	if !called {
		t.Fatal("ext handler not called")
	}
	if rsp != nil {
		t.Fatal("expected nil response")
	}
}

func TestRouter_GameDispatch(t *testing.T) {
	r := NewRouter()
	var receivedCmd uint32
	r.OnGame(func(conn *transport.Conn, msg *codec.Message) *codec.Message {
		receivedCmd = msg.GameCmd
		return nil
	})
	r.Freeze()

	r.Dispatch(nil, makeMsg(codec.CmdTypeGame, 0, 0, 42))
	if receivedCmd != 42 {
		t.Fatalf("expected gameCmd 42, got %d", receivedCmd)
	}
}

func TestRouter_Fallback(t *testing.T) {
	r := NewRouter()
	fallbackCalled := false
	r.OnFallback(func(conn *transport.Conn, msg *codec.Message) *codec.Message {
		fallbackCalled = true
		return nil
	})
	r.Freeze()

	// No handler registered for Core cmd=99
	r.Dispatch(nil, makeMsg(codec.CmdTypeCore, 99, 0, 0))
	if !fallbackCalled {
		t.Fatal("fallback not called for unmatched cmd")
	}
}

func TestRouter_NoHandler_NoFallback_NilResponse(t *testing.T) {
	r := NewRouter()
	r.Freeze()

	rsp := r.Dispatch(nil, makeMsg(codec.CmdTypeCore, 1, 0, 0))
	if rsp != nil {
		t.Fatal("expected nil when no handler and no fallback")
	}
}

func TestRouter_UnfrozenDispatch(t *testing.T) {
	r := NewRouter()
	called := false
	r.OnCore(5, func(conn *transport.Conn, msg *codec.Message) *codec.Message {
		called = true
		return nil
	})
	// Don't freeze — test unfrozen path
	r.Dispatch(nil, makeMsg(codec.CmdTypeCore, 5, 0, 0))
	if !called {
		t.Fatal("unfrozen dispatch should still work")
	}
}

func TestRouter_On_IsAliasForOnCore(t *testing.T) {
	r := NewRouter()
	called := false
	r.On(7, func(conn *transport.Conn, msg *codec.Message) *codec.Message {
		called = true
		return nil
	})
	r.Freeze()
	r.Dispatch(nil, makeMsg(codec.CmdTypeCore, 7, 0, 0))
	if !called {
		t.Fatal("On() alias not working")
	}
}

func TestRouter_FreezeSnapshotsHandlers(t *testing.T) {
	r := NewRouter()
	r.OnCore(1, func(conn *transport.Conn, msg *codec.Message) *codec.Message {
		return &codec.Message{Cmd: 10}
	})
	r.Freeze()

	// Register a new handler AFTER freeze — should not affect frozen snapshot
	r.OnCore(1, func(conn *transport.Conn, msg *codec.Message) *codec.Message {
		return &codec.Message{Cmd: 99}
	})

	rsp := r.Dispatch(nil, makeMsg(codec.CmdTypeCore, 1, 0, 0))
	if rsp.Cmd != 10 {
		t.Fatalf("frozen snapshot should use old handler, got cmd=%d", rsp.Cmd)
	}
}

func TestRouter_MultipleHandlerTypes(t *testing.T) {
	r := NewRouter()
	var log []string

	r.OnCore(1, func(conn *transport.Conn, msg *codec.Message) *codec.Message {
		log = append(log, "core")
		return nil
	})
	r.OnExt(200, func(conn *transport.Conn, msg *codec.Message) *codec.Message {
		log = append(log, "ext")
		return nil
	})
	r.OnGame(func(conn *transport.Conn, msg *codec.Message) *codec.Message {
		log = append(log, "game")
		return nil
	})
	r.Freeze()

	r.Dispatch(nil, makeMsg(codec.CmdTypeCore, 1, 0, 0))
	r.Dispatch(nil, makeMsg(codec.CmdTypeExtended, 0, 200, 0))
	r.Dispatch(nil, makeMsg(codec.CmdTypeGame, 0, 0, 1))

	if len(log) != 3 || log[0] != "core" || log[1] != "ext" || log[2] != "game" {
		t.Fatalf("expected [core ext game], got %v", log)
	}
}
