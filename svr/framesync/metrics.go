package framesync

import (
	"github.com/prometheus/client_golang/prometheus"
	"github.com/prometheus/client_golang/prometheus/promauto"
)

// Metrics Prometheus 指标
var Metrics = struct {
	ConnectionsTotal  prometheus.Counter
	ConnectionsCurrent prometheus.Gauge
	RoomsCurrent      prometheus.Gauge
	FramesPushed      prometheus.Counter
	InputsReceived    prometheus.Counter
	BytesSent         prometheus.Counter
	BytesReceived     prometheus.Counter
	MessageErrors     prometheus.Counter
	RoomPanics        prometheus.Counter
	AuthFailures      prometheus.Counter
	RateLimited       prometheus.Counter
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
	FramesPushed: promauto.NewCounter(prometheus.CounterOpts{
		Name: "boom_frames_pushed_total",
		Help: "Total frames pushed to clients",
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
