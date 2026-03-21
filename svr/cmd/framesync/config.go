package main

import (
	"encoding/json"
	"log"
	"os"
)

// ServerConfig 服务端 JSON 配置文件
type ServerConfig struct {
	Addr              string `json:"addr"`
	Proto             string `json:"proto"`
	PlayersPerRoom    int    `json:"playersPerRoom"`
	AuthToken         string `json:"authToken"`
	MetricsAddr       string `json:"metricsAddr"`
	MaxMessageSize    int    `json:"maxMessageSize"`
	MaxMessagesPerSec int    `json:"maxMessagesPerSec"`
	FrameRate         int    `json:"frameRate"`
	FrameBufferSize   int    `json:"frameBufferSize"`
	DisconnectKeepSec int    `json:"disconnectKeepSec"`
	RoomCleanupSec    int    `json:"roomCleanupSec"`
}

func DefaultConfig() ServerConfig {
	return ServerConfig{
		Addr:              ":9000",
		Proto:             "tcp",
		PlayersPerRoom:    4,
		AuthToken:         "",
		MetricsAddr:       ":9090",
		MaxMessageSize:    65536,
		MaxMessagesPerSec: 100,
		FrameRate:         20,
		FrameBufferSize:   200,
		DisconnectKeepSec: 30,
		RoomCleanupSec:    30,
	}
}

// LoadConfig 从 JSON 文件加载配置
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

	if err := json.Unmarshal(data, &cfg); err != nil {
		log.Printf("[Config] Parse error: %v, using defaults\n", err)
		return cfg
	}

	log.Printf("[Config] Loaded from %s\n", path)
	return cfg
}

// SaveDefaultConfig 生成默认配置文件
func SaveDefaultConfig(path string) {
	cfg := DefaultConfig()
	data, _ := json.MarshalIndent(cfg, "", "  ")
	os.WriteFile(path, data, 0644)
	log.Printf("[Config] Default config saved to %s\n", path)
}
