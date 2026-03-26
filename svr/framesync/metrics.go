package framesync

import (
	"github.com/prometheus/client_golang/prometheus"
	"github.com/prometheus/client_golang/prometheus/promauto"
)

// Metrics Prometheus 指标
var Metrics = struct {
	// 连接
	ConnectionsTotal   prometheus.Counter
	ConnectionsCurrent prometheus.Gauge

	// 房间
	RoomsCurrent        prometheus.Gauge
	RoomLifetimeSeconds prometheus.Observer // Histogram

	// 帧同步
	FramesPushed          prometheus.Counter
	FrameBroadcastLatency prometheus.Observer // Histogram
	InputsReceived        prometheus.Counter

	// 流量
	BytesSent     prometheus.Counter
	BytesReceived prometheus.Counter

	// 重连
	ReconnectSuccess prometheus.Counter
	ReconnectFail    prometheus.Counter

	// 快照
	SnapshotSizeBytes prometheus.Gauge

	// 错误
	MessageErrors prometheus.Counter
	RoomPanics    prometheus.Counter
	AuthFailures  prometheus.Counter
	RateLimited   prometheus.Counter
}{
	ConnectionsTotal: promauto.NewCounter(prometheus.CounterOpts{
		Name: "boom_connections_total",
		Help: "Total connections accepted",
	}),
	ConnectionsCurrent: promauto.NewGauge(prometheus.GaugeOpts{
		Name: "boom_connections_current",
		Help: "Current active connections",
	}),
	RoomsCurrent: promauto.NewGauge(prometheus.GaugeOpts{
		Name: "boom_rooms_current",
		Help: "Current active rooms",
	}),
	RoomLifetimeSeconds: promauto.NewHistogram(prometheus.HistogramOpts{
		Name:    "boom_room_lifetime_seconds",
		Help:    "Room lifetime from Start() to Stop() in seconds",
		Buckets: []float64{1, 5, 30, 60, 300, 600, 1800, 3600},
	}),
	FramesPushed: promauto.NewCounter(prometheus.CounterOpts{
		Name: "boom_frames_pushed_total",
		Help: "Total frames pushed to clients",
	}),
	FrameBroadcastLatency: promauto.NewHistogram(prometheus.HistogramOpts{
		Name:    "boom_frame_broadcast_latency_seconds",
		Help:    "Time spent broadcasting a frame to all players",
		Buckets: []float64{0.001, 0.002, 0.005, 0.01, 0.025, 0.05, 0.1},
	}),
	InputsReceived: promauto.NewCounter(prometheus.CounterOpts{
		Name: "boom_inputs_received_total",
		Help: "Total player inputs received",
	}),
	BytesSent: promauto.NewCounter(prometheus.CounterOpts{
		Name: "boom_bytes_sent_total",
		Help: "Total bytes sent to clients",
	}),
	BytesReceived: promauto.NewCounter(prometheus.CounterOpts{
		Name: "boom_bytes_received_total",
		Help: "Total bytes received from clients",
	}),
	ReconnectSuccess: promauto.NewCounter(prometheus.CounterOpts{
		Name: "boom_reconnect_success_total",
		Help: "Total successful reconnections",
	}),
	ReconnectFail: promauto.NewCounter(prometheus.CounterOpts{
		Name: "boom_reconnect_fail_total",
		Help: "Total failed reconnection attempts",
	}),
	SnapshotSizeBytes: promauto.NewGauge(prometheus.GaugeOpts{
		Name: "boom_snapshot_size_bytes",
		Help: "Size of the most recent uploaded snapshot in bytes",
	}),
	MessageErrors: promauto.NewCounter(prometheus.CounterOpts{
		Name: "boom_message_errors_total",
		Help: "Total message processing errors",
	}),
	RoomPanics: promauto.NewCounter(prometheus.CounterOpts{
		Name: "boom_room_panics_total",
		Help: "Total room goroutine panics recovered",
	}),
	AuthFailures: promauto.NewCounter(prometheus.CounterOpts{
		Name: "boom_auth_failures_total",
		Help: "Total authentication failures",
	}),
	RateLimited: promauto.NewCounter(prometheus.CounterOpts{
		Name: "boom_rate_limited_total",
		Help: "Total connections rate limited",
	}),
}
