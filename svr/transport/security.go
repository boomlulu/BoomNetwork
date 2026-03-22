package transport

import (
	"sync"
	"time"
)

// SecurityConfig 安全配置
type SecurityConfig struct {
	MaxMessageSize    int           // 单条消息最大字节数（超出断开）
	MaxMessagesPerSec int           // 每秒最大消息数（超出断开）
	RequireAuth       bool          // 是否要求鉴权
	AuthTimeout       time.Duration // 鉴权超时
	AuthToken         string        // 预共享 token（简单鉴权）
}

// DefaultSecurityConfig 默认安全配置
func DefaultSecurityConfig() SecurityConfig {
	return SecurityConfig{
		MaxMessageSize:    65536, // 64KB
		MaxMessagesPerSec: 200,   // 每秒最多 200 条消息（含突发容忍）
		RequireAuth:       false,
		AuthTimeout:       5 * time.Second,
		AuthToken:         "",
	}
}

// RateLimiter 每连接速率限制器
type RateLimiter struct {
	mu        sync.Mutex
	count     int
	lastReset time.Time
	limit     int
}

// NewRateLimiter 创建速率限制器
func NewRateLimiter(messagesPerSec int) *RateLimiter {
	return &RateLimiter{
		limit:     messagesPerSec,
		lastReset: time.Now(),
	}
}

// Allow 检查是否允许（返回 false 表示超限）
func (rl *RateLimiter) Allow() bool {
	rl.mu.Lock()
	defer rl.mu.Unlock()

	now := time.Now()
	if now.Sub(rl.lastReset) >= time.Second {
		rl.count = 0
		rl.lastReset = now
	}

	rl.count++
	return rl.count <= rl.limit
}
