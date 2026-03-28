# Changelog

All notable changes to BoomNetwork will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-03-28

First public release. Frame-sync networking framework for Unity — C# client + Go server.

### Core

- **Frame Sync** — Server-authoritative tick loop: collect inputs → build frames → broadcast
- **TCP + KCP dual transport** — Runtime-switchable, ITransport interface
- **Zero-allocation hot path** — Frame encode/broadcast reuses buffers, no GC pressure
- **Binary codec** — Dynamic header, length-prefix framing, ring buffer
- **Session routing** — Seq/AckSeq/SendAsync/message buffering

### Room Management

- Create / Join / Leave / List / Match rooms
- Mid-game join with existing player list sync
- MatchKey scoped matchmaking

### Reconnection

- **Two-stage reconnect** — Fast reconnect (frame replay) → Snapshot reconnect (state restore)
- Automatic downgrade on buffer overflow
- Configurable disconnect keep duration (slot reservation)

### Entity Authority Sync

- IEntitySync interface — owner-authority zero-latency, remote smoothing
- Dead Reckoning + Inertia Model + Correction Strategy (pluggable middleware)
- RegisterAuthorityEntity / UnregisterAuthorityEntity

### Lightweight State Sync

- SendStateMessage — fire-and-forget broadcast
- SetData / DeleteData — persistent KV store with server relay
- SendGameMessage — custom game messages with sender ID

### Frame Events

- In-frame player events: PlayerJoined / PlayerLeft / PlayerOffline / PlayerOnline / HostChanged
- Host election: first player = host → disconnect triggers election → first reconnect = new host
- Dual path: frame events during sync, ExtCmd when not syncing

### Desync Detection

- SendFrameHash — client uploads frame hash for server comparison
- OnDesyncDetected — callback with per-player hash mismatch details
- FrameSyncPaused / FrameSyncResumed notifications

### GM Tools

- Admin HTTP — 10 endpoints (health, stats, rooms, inspect, kick, stop, netsim, config reload, log level)
- WebSocket — long-lived GM channel with MessagePack
- Unity Editor panel — ServerWindow with 5 tabs (Monitor, Messages, Rooms, Control, Deploy)
- Network simulation — delay / jitter / packet loss (server → client)
- Multi-environment switcher (Local / Remote SSH)

### Deployment

- Docker multi-stage build with health check
- systemd unit file
- Grafana dashboard
- YAML config with server-to-client parameter push

### Unity Integration

- UPM package: `com.boom.boomnetwork`
- BoomNetworkManager MonoBehaviour — QuickStart one-liner
- 8 progressive samples (HelloWorld → MinecraftDemo)

### Documentation

- llms.txt / llms-full.txt — AI-native API reference
- Core philosophy doc (self-authority, no rollback, three axioms)
- API reference, quickstart, architecture, protocol command tiers
- Cheatsheet with lifecycle state machine

[0.1.0]: https://github.com/luwenyiCC/BoomNetwork/releases/tag/v0.1.0
