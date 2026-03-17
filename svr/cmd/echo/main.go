package main

import (
	"fmt"
	"os"
	"os/signal"
	"syscall"

	"github.com/boom/boomnetwork/codec"
	"github.com/boom/boomnetwork/session"
	"github.com/boom/boomnetwork/transport"
)

const (
	CmdEcho    = 1
	CmdPing    = 2
	CmdNoReply = 3 // 不回复，用于测试客户端超时
)

func main() {
	addr := ":9000"
	if len(os.Args) > 1 {
		addr = os.Args[1]
	}

	router := session.NewRouter()

	router.On(CmdEcho, func(conn *transport.Conn, msg *codec.Message) *codec.Message {
		fmt.Printf("[Echo] Client %d: %s data=%s\n", conn.ID, msg, string(msg.Data))
		return &codec.Message{Cmd: CmdEcho, Data: msg.Data}
	})

	router.On(CmdPing, func(conn *transport.Conn, msg *codec.Message) *codec.Message {
		fmt.Printf("[Ping] Client %d\n", conn.ID)
		return &codec.Message{Cmd: CmdPing, Data: []byte("pong")}
	})

	router.On(CmdNoReply, func(conn *transport.Conn, msg *codec.Message) *codec.Message {
		fmt.Printf("[NoReply] Client %d (intentionally no response)\n", conn.ID)
		return nil // 不回复
	})

	server := transport.NewTcpServer(router.AsTransportHandler())

	if err := server.Listen(addr); err != nil {
		fmt.Fprintf(os.Stderr, "Failed to start: %v\n", err)
		os.Exit(1)
	}

	fmt.Printf("[Server] Running on %s (Cmd: Echo=%d Ping=%d NoReply=%d)\n", addr, CmdEcho, CmdPing, CmdNoReply)

	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	<-sig

	fmt.Println("\n[Server] Shutting down...")
	server.Close()
}
