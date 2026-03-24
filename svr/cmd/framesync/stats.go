package main

import (
	"sync"
	"sync/atomic"
	"time"
)

// ringSize 决定滑动窗口精度：每个槽 = 1 秒，60 槽覆盖 60 秒
const ringSize = 60

type secondBucket struct {
	sec int64 // Unix 秒时间戳（标识此槽属于哪一秒）
	rx  int64
	tx  int64
}

// TrafficTracker 并发安全的流量统计器
//
// 原理：
//   - 全局原子计数器保存累计总量
//   - 环形缓冲区（60 个 1 秒槽）支持滑动窗口查询
//   - 写入时按 unixSecond % ringSize 定位槽，时间戳不匹配则覆盖旧数据
type TrafficTracker struct {
	rxTotal int64 // 累计接收字节（原子）
	txTotal int64 // 累计发送字节（原子）

	mu   sync.Mutex
	ring [ringSize]secondBucket
}

// 全局单例
var Stats = &TrafficTracker{}

func (t *TrafficTracker) RecordRx(n int64) {
	if n <= 0 {
		return
	}
	atomic.AddInt64(&t.rxTotal, n)
	t.record(n, 0)
}

func (t *TrafficTracker) RecordTx(n int64) {
	if n <= 0 {
		return
	}
	atomic.AddInt64(&t.txTotal, n)
	t.record(0, n)
}

func (t *TrafficTracker) record(rx, tx int64) {
	now := time.Now().Unix()
	idx := now % ringSize

	t.mu.Lock()
	b := &t.ring[idx]
	if b.sec == now {
		b.rx += rx
		b.tx += tx
	} else {
		// 新的一秒，覆盖旧槽
		*b = secondBucket{sec: now, rx: rx, tx: tx}
	}
	t.mu.Unlock()
}

// TrafficSnapshot 一次性快照，供 HTTP 响应使用
type TrafficSnapshot struct {
	RxTotal     int64 `json:"rx_total_bytes"`
	TxTotal     int64 `json:"tx_total_bytes"`
	Rx1MinBytes int64 `json:"rx_1min_bytes"`
	Tx1MinBytes int64 `json:"tx_1min_bytes"`
	Rx5SecBytes int64 `json:"rx_5sec_bytes"`
	Tx5SecBytes int64 `json:"tx_5sec_bytes"`
}

// Snapshot 读取当前统计快照（加锁，O(ringSize) = O(60)）
func (t *TrafficTracker) Snapshot() TrafficSnapshot {
	now := time.Now().Unix()
	cut1min := now - 60
	cut5sec := now - 5

	var rx1min, tx1min, rx5sec, tx5sec int64

	t.mu.Lock()
	for i := 0; i < ringSize; i++ {
		b := t.ring[i]
		if b.sec <= cut1min || b.sec > now {
			continue
		}
		rx1min += b.rx
		tx1min += b.tx
		if b.sec > cut5sec {
			rx5sec += b.rx
			tx5sec += b.tx
		}
	}
	t.mu.Unlock()

	return TrafficSnapshot{
		RxTotal:     atomic.LoadInt64(&t.rxTotal),
		TxTotal:     atomic.LoadInt64(&t.txTotal),
		Rx1MinBytes: rx1min,
		Tx1MinBytes: tx1min,
		Rx5SecBytes: rx5sec,
		Tx5SecBytes: tx5sec,
	}
}
