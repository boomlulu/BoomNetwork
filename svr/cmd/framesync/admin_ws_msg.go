package main

import "github.com/vmihailenco/msgpack/v5"

// ===================== GM WebSocket 协议信封 =====================
//
// 每个 WebSocket 帧承载一个 GMEnvelope，使用 MessagePack 序列化。
//
// Type 字段：
//   C→S: auth, sub, unsub, rpc, ping
//   S→C: auth_ok, auth_err, push, rsp, err, pong

// GMEnvelope 是 WebSocket 通道的线格式容器
type GMEnvelope struct {
	Type    string             `msgpack:"t"`  // 消息类型
	ID      string             `msgpack:"id"` // RPC 请求 ID（rsp/err 回显）
	Topic   string             `msgpack:"tp"` // 主题名
	Payload msgpack.RawMessage `msgpack:"p"`  // 主题负载（已编码的 msgpack）
}

// ===================== Topic 常量 =====================

const (
	TopicHealth   = "health"
	TopicStats    = "stats"
	TopicMessages = "messages"
	TopicRooms    = "rooms"
	TopicPerf     = "perf"
	TopicRates    = "rates"
	TopicNetsim   = "netsim"
	TopicLogs     = "logs"
)

// topicBit 用于订阅位掩码
const (
	BitHealth   uint32 = 1 << iota
	BitStats
	BitMessages
	BitRooms
	BitPerf
	BitRates
	BitNetsim
	BitLogs
)

var topicToBit = map[string]uint32{
	TopicHealth:   BitHealth,
	TopicStats:    BitStats,
	TopicMessages: BitMessages,
	TopicRooms:    BitRooms,
	TopicPerf:     BitPerf,
	TopicRates:    BitRates,
	TopicNetsim:   BitNetsim,
	TopicLogs:     BitLogs,
}

// ===================== Auth 负载 =====================

type AuthPayload struct {
	Token string `msgpack:"token"`
}

// ===================== RPC 负载 =====================

type KickPayload struct {
	Pid int32 `msgpack:"pid"`
}

type StopRoomPayload struct {
	RoomID int32 `msgpack:"room_id"`
}

type KillRoomPayload struct {
	RoomID int32 `msgpack:"room_id"`
}

type CreateRoomPayload struct {
	MaxPlayers int    `msgpack:"max_players"`
	MatchKey   string `msgpack:"match_key,omitempty"`
}

type NetsimPayload struct {
	Enabled     *bool `msgpack:"enabled,omitempty"`
	LatencyMs   *int  `msgpack:"latency_ms,omitempty"`
	JitterMs    *int  `msgpack:"jitter_ms,omitempty"`
	LossPercent *int  `msgpack:"loss_percent,omitempty"`
}

// ===================== Push 负载结构体 =====================

// HealthPush — topic: health
type HealthPush struct {
	Status  string `msgpack:"status"`
	Rooms   int    `msgpack:"rooms"`
	Players int    `msgpack:"players"`
	Uptime  string `msgpack:"uptime"`
}

// StatsPush — topic: stats
type StatsPush struct {
	GameRxTotal int64 `msgpack:"game_rx_total"`
	GameTxTotal int64 `msgpack:"game_tx_total"`
	GameRx1Min  int64 `msgpack:"game_rx_1min"`
	GameTx1Min  int64 `msgpack:"game_tx_1min"`
	GameRx5Sec  int64 `msgpack:"game_rx_5sec"`
	GameTx5Sec  int64 `msgpack:"game_tx_5sec"`
	GmRxTotal   int64 `msgpack:"gm_rx_total"`
	GmTxTotal   int64 `msgpack:"gm_tx_total"`
	GmRx1Min    int64 `msgpack:"gm_rx_1min"`
	GmTx1Min    int64 `msgpack:"gm_tx_1min"`
	GmRx5Sec    int64 `msgpack:"gm_rx_5sec"`
	GmTx5Sec    int64 `msgpack:"gm_tx_5sec"`
}

// MsgEntryWire — topic: messages（单条推送）
type MsgEntryWire struct {
	Ts       int64  `msgpack:"ts"`
	Dir      string `msgpack:"dir"`
	Cmd      byte   `msgpack:"cmd"`
	Name     string `msgpack:"name"`
	Pid      int32  `msgpack:"pid"`
	Size     int    `msgpack:"size"`
	Detail   string `msgpack:"detail,omitempty"`
	RoomID   int32  `msgpack:"room_id,omitempty"`
	MatchKey string `msgpack:"match_key,omitempty"`
}

// MsgEntryToWire 将内部 MsgEntry 转为 wire 格式
func MsgEntryToWire(e MsgEntry) MsgEntryWire {
	return MsgEntryWire{
		Ts: e.Ts, Dir: e.Dir, Cmd: e.Cmd, Name: e.Name,
		Pid: e.Pid, Size: e.Size, Detail: e.Detail,
		RoomID: e.RoomID, MatchKey: e.MatchKey,
	}
}

// RoomDetailWire — topic: rooms
type RoomDetailWire struct {
	ID           int32            `msgpack:"id"`
	Running      bool             `msgpack:"running"`
	Paused       bool             `msgpack:"paused"`
	FrameNumber  uint32           `msgpack:"frame_number"`
	FrameRate    int32            `msgpack:"frame_rate"`
	MaxPlayers   int              `msgpack:"max_players"`
	OnlineCount  int              `msgpack:"online_count"`
	TotalPlayers int              `msgpack:"total_players"`
	MatchKey     string           `msgpack:"match_key,omitempty"`
	Players      []PlayerInfoWire `msgpack:"players"`
}

type PlayerInfoWire struct {
	ID    int32 `msgpack:"id"`
	State int   `msgpack:"state"`
}

// PerfPush — topic: perf
type PerfPush struct {
	Goroutines int     `msgpack:"goroutines"`
	HeapMB     float64 `msgpack:"heap_mb"`
	SysMB      float64 `msgpack:"sys_mb"`
	GCCount    uint32  `msgpack:"gc_count"`
	GCPauseUs  uint64  `msgpack:"gc_pause_us"`
	Rooms      int     `msgpack:"rooms"`
	Players    int     `msgpack:"players"`
}

// RatesPush — topic: rates
type RatesPush struct {
	Top []PlayerRateWire `msgpack:"top"`
}

type PlayerRateWire struct {
	Pid        int32 `msgpack:"pid"`
	MsgPer5Sec int32 `msgpack:"msg_5sec"`
}

// NetsimPush — topic: netsim
type NetsimPush struct {
	Enabled      bool  `msgpack:"enabled"`
	LatencyMs    int32 `msgpack:"latency_ms"`
	JitterMs     int32 `msgpack:"jitter_ms"`
	LossPercent  int32 `msgpack:"loss_percent"`
	StatsDropped int64 `msgpack:"stats_dropped"`
	StatsDelayed int64 `msgpack:"stats_delayed"`
}

// LogPush — topic: logs（单条实时推送）
type LogPush struct {
	Ts    int64  `msgpack:"ts"`
	Level string `msgpack:"level"`
	Msg   string `msgpack:"msg"`
	Attrs string `msgpack:"attrs,omitempty"`
}

// ===================== RPC 响应 =====================

type KickResult struct {
	Ok     bool  `msgpack:"ok"`
	Kicked int32 `msgpack:"kicked"`
	Room   int32 `msgpack:"room"`
}

type StopRoomResult struct {
	Ok      bool  `msgpack:"ok"`
	Stopped int32 `msgpack:"stopped"`
}

type KillRoomResult struct {
	Ok     bool  `msgpack:"ok"`
	Killed int32 `msgpack:"killed"`
}

type CreateRoomResult struct {
	Ok     bool  `msgpack:"ok"`
	RoomID int32 `msgpack:"room_id"`
}

type ErrorResult struct {
	Error string `msgpack:"error"`
}

// ===================== RPC: inspect_room =====================

type InspectRoomPayload struct {
	RoomID int32 `msgpack:"room_id"`
}

// RoomInspectWire — 房间深度检视响应
type RoomInspectWire struct {
	Ok                  bool               `msgpack:"ok" json:"ok"`
	ID                  int32              `msgpack:"id" json:"id"`
	Running             bool               `msgpack:"running" json:"running"`
	Paused              bool               `msgpack:"paused" json:"paused"`
	FrameNumber         uint32             `msgpack:"frame_number" json:"frame_number"`
	FrameRate           int32              `msgpack:"frame_rate" json:"frame_rate"`
	MaxPlayers          int                `msgpack:"max_players" json:"max_players"`
	OnlineCount         int                `msgpack:"online_count" json:"online_count"`
	TotalPlayers        int                `msgpack:"total_players" json:"total_players"`
	MatchKey            string             `msgpack:"match_key" json:"match_key"`
	Players             []PlayerInfoWire   `msgpack:"players" json:"players"`
	FrameBufferLen      int                `msgpack:"frame_buffer_len" json:"frame_buffer_len"`
	FrameBufferCap      int                `msgpack:"frame_buffer_cap" json:"frame_buffer_cap"`
	OldestBufferedFrame uint32             `msgpack:"oldest_buffered_frame" json:"oldest_buffered_frame"`
	SnapshotFrame       uint32             `msgpack:"snapshot_frame" json:"snapshot_frame"`
	SnapshotSizeBytes   int                `msgpack:"snapshot_size_bytes" json:"snapshot_size_bytes"`
	SnapshotStaleFrames uint32             `msgpack:"snapshot_stale_frames" json:"snapshot_stale_frames"`
	DataVersion         uint32             `msgpack:"data_version" json:"data_version"`
	EntityAuthority     []EntityAuthWire   `msgpack:"entity_authority" json:"entity_authority"`
	KVEntries           []KVEntryWire      `msgpack:"kv_entries" json:"kv_entries"`
}

type EntityAuthWire struct {
	EntityId int32 `msgpack:"entity_id" json:"entity_id"`
	OwnerId  int32 `msgpack:"owner_id" json:"owner_id"`
}

type KVEntryWire struct {
	PlayerId int32  `msgpack:"player_id" json:"player_id"`
	Key      int32  `msgpack:"key" json:"key"`
	Value    []byte `msgpack:"value" json:"value"`
}

// ===================== 编码辅助 =====================

func encodeEnvelope(env *GMEnvelope) ([]byte, error) {
	return msgpack.Marshal(env)
}

func decodeEnvelope(data []byte) (*GMEnvelope, error) {
	var env GMEnvelope
	err := msgpack.Unmarshal(data, &env)
	return &env, err
}

func mustEncodePayload(v interface{}) msgpack.RawMessage {
	b, err := msgpack.Marshal(v)
	if err != nil {
		return nil
	}
	return b
}

func makePushEnvelope(topic string, payload interface{}) []byte {
	env := GMEnvelope{
		Type:    "push",
		Topic:   topic,
		Payload: mustEncodePayload(payload),
	}
	data, _ := encodeEnvelope(&env)
	return data
}
