package transport

import (
	"log/slog"
	"net"
	"sync"
	"sync/atomic"
	"time"
)

// SecurityConfig 安全配置
type SecurityConfig struct {
	MaxMessageSize    int           // 单条消息最大字节数（超出断开）
	MaxMessagesPerSec int           // 每秒最大消息数（超出断开）
	RequireAuth       bool          // 是否要求鉴权
	AuthTimeout       time.Duration // 鉴权超时
	AuthToken         string        // 预共享 token（简单鉴权）
	AllowedOrigins    []string      // WebSocket Origin 白名单（空=允许所有，向后兼容）
}

// DefaultSecurityConfig 默认安全配置
func DefaultSecurityConfig() SecurityConfig {
	return SecurityConfig{
		MaxMessageSize:    65536, // 64KB
		MaxMessagesPerSec: 100,   // 每秒最多 100 条消息（与 DefaultConfig() 对齐）
		RequireAuth:       false,
		AuthTimeout:       5 * time.Second,
		AuthToken:         "",
	}
}

// RateLevel 速率检查结果
type RateLevel int

const (
	RateLevelOK   RateLevel = 0 // 正常
	RateLevelWarn RateLevel = 1 // 接近上限（80%），应发送警告
	RateLevelDeny RateLevel = 2 // 超限，应断开
)

// RateLimiter 每连接速率限制器
// H1: 热路径使用原子 CAS 替代 sync.Mutex，避免每条消息的锁竞争。
// packed 字段布局：高 32 位 = unix 秒，低 32 位 = 当前秒内计数（uint32 解释）。
type RateLimiter struct {
	packed   int64      // [unixSecond:32][count:32] — 原子 CAS
	limit    int32
	warnAt   int32
	warnMu   sync.Mutex // 仅用于防止并发警告日志
	warnTime int64      // 上次发出 Warn 的 unix 秒（原子读写）
}

// NewRateLimiter 创建速率限制器
func NewRateLimiter(messagesPerSec int) *RateLimiter {
	nowSec := time.Now().Unix()
	// 初始 packed：当前秒 + 计数 0（0 表示"尚未计数"）
	initPacked := nowSec << 32
	return &RateLimiter{
		packed: initPacked,
		limit:  int32(messagesPerSec),
		warnAt: int32(messagesPerSec * 8 / 10), // 80%
	}
}

// Allow 检查是否允许（返回 false 表示超限）
func (rl *RateLimiter) Allow() bool {
	return rl.AllowLevel() != RateLevelDeny
}

// AllowLevel 检查速率等级（OK / Warn / Deny）
// 使用原子 CAS 实现无锁热路径。
func (rl *RateLimiter) AllowLevel() RateLevel {
	nowSec := time.Now().Unix()
	for {
		old := atomic.LoadInt64(&rl.packed)
		oldSec := old >> 32
		oldCount := int32(old) // 低 32 位转 int32

		var newCount int32
		if oldSec != nowSec {
			newCount = 1
		} else {
			if oldCount > rl.limit {
				return RateLevelDeny // 快速路径：无需 CAS
			}
			newCount = oldCount + 1
		}

		newPacked := (nowSec << 32) | int64(uint32(newCount))
		if atomic.CompareAndSwapInt64(&rl.packed, old, newPacked) {
			if newCount > rl.limit {
				return RateLevelDeny
			}
			if newCount > rl.warnAt {
				// 每秒最多发一次 Warn
				lastWarn := atomic.LoadInt64(&rl.warnTime)
				if lastWarn < nowSec && atomic.CompareAndSwapInt64(&rl.warnTime, lastWarn, nowSec) {
					return RateLevelWarn
				}
			}
			return RateLevelOK
		}
		// CAS 失败（并发写入），重试
	}
}

// ===================== S18: Per-IP 连接速率限制 =====================

// ipRate 记录单个 IP 在当前 1 秒窗口内的新连接数
type ipRate struct {
	mu        sync.Mutex
	count     int
	lastReset time.Time
}

// allow 返回 false 表示该 IP 在本秒内已超出限制
func (r *ipRate) allow(maxPerSec int) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	now := time.Now()
	if now.Sub(r.lastReset) >= time.Second {
		r.count = 0
		r.lastReset = now
	}
	r.count++
	return r.count <= maxPerSec
}

// IPRateLimiter 管理全部 IP 的连接速率
type IPRateLimiter struct {
	m          sync.Map // IP string → *ipRate
	maxPerSec  int
}

// NewIPRateLimiter 创建 IP 速率限制器，maxPerSec=0 表示不限制
func NewIPRateLimiter(maxPerSec int) *IPRateLimiter {
	return &IPRateLimiter{maxPerSec: maxPerSec}
}

// Allow 检查该远端地址（host:port 格式）是否允许建立新连接
func (l *IPRateLimiter) Allow(remoteAddr net.Addr) bool {
	if l.maxPerSec <= 0 {
		return true
	}
	ip, _, err := net.SplitHostPort(remoteAddr.String())
	if err != nil {
		ip = remoteAddr.String()
	}
	v, _ := l.m.LoadOrStore(ip, &ipRate{lastReset: time.Now()})
	rate := v.(*ipRate)
	if !rate.allow(l.maxPerSec) {
		slog.Error("per-IP connection rate limit exceeded", "ip", ip, "maxPerSec", l.maxPerSec)
		return false
	}
	return true
}
