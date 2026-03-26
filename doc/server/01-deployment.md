# 服务器部署指南

## 方式一：Docker（推荐）

### 一键启动

```bash
cd svr
docker compose up -d
```

服务器在 `0.0.0.0:9000` 监听，Prometheus metrics 在 `0.0.0.0:9090`。

### 自定义配置

编辑 `cmd/framesync/config.yaml` 后重启：

```bash
docker compose restart
```

或挂载外部配置文件：

```bash
docker compose run -d \
  -v /path/to/your/config.yaml:/etc/boomnetwork/config.yaml:ro \
  framesync
```

### 查看日志

```bash
docker compose logs -f
```

### 停止

```bash
docker compose down
```

## 方式二：二进制编译

### 编译

```bash
cd svr
CGO_ENABLED=0 go build -ldflags="-s -w" -o framesync ./cmd/framesync/
```

产出 `framesync` 可执行文件，约 10MB，无外部依赖。

### 交叉编译

```bash
# Linux amd64
GOOS=linux GOARCH=amd64 go build -ldflags="-s -w" -o framesync-linux-amd64 ./cmd/framesync/

# Linux arm64
GOOS=linux GOARCH=arm64 go build -ldflags="-s -w" -o framesync-linux-arm64 ./cmd/framesync/

# Windows
GOOS=windows GOARCH=amd64 go build -ldflags="-s -w" -o framesync.exe ./cmd/framesync/
```

### 运行

```bash
# 使用配置文件
./framesync -config config.yaml

# 生成默认配置
./framesync -gen-config
```

## 方式三：go run（开发用）

```bash
cd svr
go run ./cmd/framesync/ -config cmd/framesync/config.yaml
```

## 配置说明

完整参数见 [config.yaml](../svr/cmd/framesync/config.yaml)，关键参数：

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `addr` | `:9000` | 监听地址 |
| `proto` | `tcp` | 传输协议: tcp / kcp |
| `frameRate` | `20` | 帧率（帧/秒） |
| `frameBufferSize` | `2400` | 帧缓冲区大小，决定重连补帧窗口 |
| `snapshotIntervalFrames` | `100` | 快照上传间隔（帧数），下发客户端 |
| `quickReconnectMaxMs` | `5000` | 快速重连超时（ms），下发客户端 |
| `disconnectKeepSec` | `120` | 断线玩家保留时长（秒） |
| `playersPerRoom` | `4` | 默认每房间最大人数 |
| `maxMessagesPerSec` | `100` | 单连接每秒消息上限（限流） |

### 参数关系

```
disconnectKeepSec × frameRate ≈ frameBufferSize
    120秒           × 20fps    = 2400 帧

snapshotIntervalFrames 决定重连时最多补多少帧:
    最坏情况补帧数 = snapshotIntervalFrames（如 100 帧 = 5 秒）
```

## 端口

| 端口 | 用途 |
|------|------|
| 9000 | 帧同步服务（TCP/KCP） |
| 9090 | Prometheus metrics（可选） |

云服务器 / 防火墙需放行 9000 端口（TCP 或 UDP，取决于 proto 配置）。

## 健康检查

```bash
# 检查服务是否在监听
nc -zv 你的IP 9000

# Prometheus metrics
curl http://你的IP:9090/metrics
```

## systemd 服务（Linux）

```ini
# /etc/systemd/system/boomnetwork.service
[Unit]
Description=BoomNetwork FrameSync Server
After=network.target

[Service]
Type=simple
ExecStart=/usr/local/bin/framesync -config /etc/boomnetwork/config.yaml
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable boomnetwork
sudo systemctl start boomnetwork
sudo journalctl -u boomnetwork -f   # 查看日志
```
