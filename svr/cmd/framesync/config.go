package main

import (
	"log"
	"os"

	"go.yaml.in/yaml/v2"
)

// ServerConfig 服务端 YAML 配置文件
type ServerConfig struct {
	// 网络
	Addr  string `yaml:"addr"`
	Proto string `yaml:"proto"`

	// 安全
	AuthToken string `yaml:"authToken"`

	// 监控
	MetricsAddr string `yaml:"metricsAddr"`

	// 房间
	PlayersPerRoom int `yaml:"playersPerRoom"`

	// 帧同步
	FrameRate int `yaml:"frameRate"`

	// 环形帧缓冲区大小（帧数）
	// 决定了服务器能缓存多少帧用于重连补帧
	// 建议 >= disconnectKeepSec × frameRate，确保覆盖整个重连窗口
	FrameBufferSize int `yaml:"frameBufferSize"`

	// 快照间隔（帧数），服务器下发给客户端
	// 客户端每隔这么多已确认帧上传一次快照
	// 连续 3 个间隔未收到任何客户端快照 → 服务器暂停帧同步
	SnapshotIntervalFrames int `yaml:"snapshotIntervalFrames"`

	// 快速重连最长重试时间（毫秒），服务器下发给客户端
	// 客户端在此时间内尝试快速重连（Stage 1），超时后降级到快照重连
	QuickReconnectMaxMs int `yaml:"quickReconnectMaxMs"`

	// 断线玩家保留时长（秒）
	// 超过此时间未重连的玩家将被移除房间
	DisconnectKeepSec int `yaml:"disconnectKeepSec"`

	// 空房间清理延迟（秒）
	RoomCleanupSec int `yaml:"roomCleanupSec"`

	// 安全限流
	MaxMessageSize    int `yaml:"maxMessageSize"`
	MaxMessagesPerSec int `yaml:"maxMessagesPerSec"`
}

func DefaultConfig() ServerConfig {
	return ServerConfig{
		Addr:           ":9000",
		Proto:          "tcp",
		AuthToken:      "",
		MetricsAddr:    ":9090",
		PlayersPerRoom: 4,

		FrameRate:              20,
		FrameBufferSize:        2400,
		SnapshotIntervalFrames: 100,
		QuickReconnectMaxMs:    5000,
		DisconnectKeepSec:      120,
		RoomCleanupSec:         30,

		MaxMessageSize:    65536,
		MaxMessagesPerSec: 100,
	}
}

// LoadConfig 从 YAML 文件加载配置
func LoadConfig(path string) ServerConfig {
	cfg := DefaultConfig()
	if path == "" {
		return cfg
	}

	data, err := os.ReadFile(path)
	if err != nil {
		log.Printf("[Config] File %s not found, using defaults\n", path)
		return cfg
	}

	if err := yaml.Unmarshal(data, &cfg); err != nil {
		log.Printf("[Config] Parse error: %v, using defaults\n", err)
		return cfg
	}

	log.Printf("[Config] Loaded from %s\n", path)
	return cfg
}

// SaveDefaultConfig 生成默认配置文件（带注释）
func SaveDefaultConfig(path string) {
	content := `# BoomNetwork 帧同步服务器配置

# ===== 网络 =====
addr: ":9000"         # 监听地址
proto: "tcp"          # 传输协议: tcp 或 kcp

# ===== 安全 =====
authToken: ""         # 鉴权 token，空字符串 = 不鉴权

# ===== 监控 =====
metricsAddr: ":9090"  # Prometheus metrics 地址，空 = 不启用

# ===== 房间 =====
playersPerRoom: 4     # 默认每房间最大玩家数

# ===== 帧同步核心 =====
frameRate: 20         # 帧率（帧/秒）

# 环形帧缓冲区大小（帧数）
# 决定了服务器能缓存多少帧用于重连补帧
# 建议 >= disconnectKeepSec × frameRate，确保覆盖整个重连窗口
# 例: 120秒 × 20fps = 2400 帧
frameBufferSize: 2400

# 快照间隔（帧数），服务器下发给客户端
# 客户端每隔这么多已确认帧上传一次快照
# 连续 3 个间隔未收到任何客户端快照 → 服务器暂停帧同步
# 例: 100 帧 = 5 秒 @20fps
snapshotIntervalFrames: 100

# 快速重连最长重试时间（毫秒），服务器下发给客户端
# 客户端在此时间内尝试快速重连（Stage 1），超时后降级到快照重连（Stage 2）
quickReconnectMaxMs: 5000

# ===== 玩家生命周期 =====

# 断线玩家保留时长（秒）
# 超过此时间未重连的玩家将被移除房间
# 这是允许重连的最大时间窗口
disconnectKeepSec: 120

# 空房间清理延迟（秒）
roomCleanupSec: 30

# ===== 安全限流 =====
maxMessageSize: 65536     # 单条消息最大字节
maxMessagesPerSec: 100    # 每秒最大消息数
`
	os.WriteFile(path, []byte(content), 0644)
	log.Printf("[Config] Default config saved to %s\n", path)
}
