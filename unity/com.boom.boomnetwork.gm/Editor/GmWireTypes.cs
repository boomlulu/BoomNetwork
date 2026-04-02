// GM WebSocket 协议 — 信封 + Wire 类型
// 与 Go 服务端 admin_ws_msg.go 对齐

using System.Collections.Generic;

namespace BoomNetwork.GM.Editor
{
    // ===================== 信封 =====================

    /// <summary>GM WebSocket 消息信封（msgpack map 格式）</summary>
    public struct GmEnvelope
    {
        public string Type;    // t:  auth|auth_ok|auth_err|sub|unsub|push|rpc|rsp|err|ping|pong
        public string ID;      // id: RPC 请求 ID
        public string Topic;   // tp: health|stats|messages|rooms|perf|rates|netsim
        public byte[] Payload; // p:  msgpack-encoded 负载

        public static GmEnvelope Decode(byte[] data)
        {
            var map = MsgPackLite.DecodeMap(data);
            if (map == null) return default;

            // p 字段可能是 bin (byte[]) 或已解码的 msgpack 值 (map/array/etc)
            // Go RawMessage 不包 bin 壳，直接嵌入 raw msgpack
            // 如果已被 DecodeMap 解码为 object，需要重新编码回 bytes
            byte[] payload = null;
            if (map.TryGetValue("p", out var pVal) && pVal != null)
            {
                if (pVal is byte[] raw)
                    payload = raw;
                else
                    payload = MsgPackLite.Encode(pVal);
            }

            return new GmEnvelope
            {
                Type    = MsgPackLite.GetString(map, "t"),
                ID      = MsgPackLite.GetString(map, "id"),
                Topic   = MsgPackLite.GetString(map, "tp"),
                Payload = payload,
            };
        }

        public byte[] Encode()
        {
            var map = new Dictionary<string, object>
            {
                ["t"]  = Type ?? "",
                ["id"] = ID ?? "",
                ["tp"] = Topic ?? "",
            };
            // Payload 是已序列化的 msgpack，用 RawMsgPack 直接嵌入而非包 bin 壳
            if (Payload != null) map["p"] = new RawMsgPack(Payload);
            else map["p"] = null;
            return MsgPackLite.EncodeMap(map);
        }

        /// <summary>解码 Payload 为 msgpack map</summary>
        public Dictionary<string, object> DecodePayload()
        {
            if (Payload == null || Payload.Length == 0) return null;
            return MsgPackLite.DecodeMap(Payload);
        }
    }

    // ===================== Topic 常量 =====================

    public static class GmTopics
    {
        public const string Health   = "health";
        public const string Stats    = "stats";
        public const string Messages = "messages";
        public const string Rooms    = "rooms";
        public const string Perf     = "perf";
        public const string Rates    = "rates";
        public const string Netsim   = "netsim";
        public const string Logs     = "logs";

        public static readonly string[] All = { Health, Stats, Messages, Rooms, Perf, Rates, Netsim, Logs };
    }

    // ===================== Push 数据结构 =====================

    public struct GmHealthPush
    {
        public string Status;
        public int Rooms, Players;
        public string Uptime;

        public static GmHealthPush From(Dictionary<string, object> m)
        {
            return new GmHealthPush
            {
                Status  = MsgPackLite.GetString(m, "status"),
                Rooms   = MsgPackLite.GetInt(m, "rooms"),
                Players = MsgPackLite.GetInt(m, "players"),
                Uptime  = MsgPackLite.GetString(m, "uptime"),
            };
        }
    }

    public struct GmStatsPush
    {
        public long GameRxTotal, GameTxTotal, GameRx1Min, GameTx1Min, GameRx5Sec, GameTx5Sec;
        public long GmRxTotal, GmTxTotal, GmRx1Min, GmTx1Min, GmRx5Sec, GmTx5Sec;

        public static GmStatsPush From(Dictionary<string, object> m)
        {
            return new GmStatsPush
            {
                GameRxTotal = MsgPackLite.GetLong(m, "game_rx_total"),
                GameTxTotal = MsgPackLite.GetLong(m, "game_tx_total"),
                GameRx1Min  = MsgPackLite.GetLong(m, "game_rx_1min"),
                GameTx1Min  = MsgPackLite.GetLong(m, "game_tx_1min"),
                GameRx5Sec  = MsgPackLite.GetLong(m, "game_rx_5sec"),
                GameTx5Sec  = MsgPackLite.GetLong(m, "game_tx_5sec"),
                GmRxTotal   = MsgPackLite.GetLong(m, "gm_rx_total"),
                GmTxTotal   = MsgPackLite.GetLong(m, "gm_tx_total"),
                GmRx1Min    = MsgPackLite.GetLong(m, "gm_rx_1min"),
                GmTx1Min    = MsgPackLite.GetLong(m, "gm_tx_1min"),
                GmRx5Sec    = MsgPackLite.GetLong(m, "gm_rx_5sec"),
                GmTx5Sec    = MsgPackLite.GetLong(m, "gm_tx_5sec"),
            };
        }
    }

    public struct GmMsgEntry
    {
        public long Ts;
        public string Dir, Name, Detail;
        public int Cmd, Pid, Size;
        public int RoomID;
        public string MatchKey;

        public static GmMsgEntry From(Dictionary<string, object> m)
        {
            return new GmMsgEntry
            {
                Ts       = MsgPackLite.GetLong(m, "ts"),
                Dir      = MsgPackLite.GetString(m, "dir"),
                Cmd      = MsgPackLite.GetInt(m, "cmd"),
                Name     = MsgPackLite.GetString(m, "name"),
                Pid      = MsgPackLite.GetInt(m, "pid"),
                Size     = MsgPackLite.GetInt(m, "size"),
                Detail   = MsgPackLite.GetString(m, "detail"),
                RoomID   = MsgPackLite.GetInt(m, "room_id"),
                MatchKey = MsgPackLite.GetString(m, "match_key"),
            };
        }
    }

    public struct GmRoomDetail
    {
        public int Id;
        public bool Running, Paused;
        public uint FrameNumber;
        public int FrameRate, MaxPlayers, OnlineCount, TotalPlayers;
        public string MatchKey;
        public GmPlayerInfo[] Players;
        // 生命周期倒计时
        public long EmptyAt;           // 房间变空的时刻（unix ms）；有玩家时为 0
        public int EmptyGraceSec;      // 销毁宽限期（秒）
        public int DisconnectKeepSec;  // 玩家踢出宽限期（秒）

        public static GmRoomDetail From(Dictionary<string, object> m)
        {
            var r = new GmRoomDetail
            {
                Id                = MsgPackLite.GetInt(m, "id"),
                Running           = MsgPackLite.GetBool(m, "running"),
                Paused            = MsgPackLite.GetBool(m, "paused"),
                FrameNumber       = (uint)MsgPackLite.GetLong(m, "frame_number"),
                FrameRate         = MsgPackLite.GetInt(m, "frame_rate"),
                MaxPlayers        = MsgPackLite.GetInt(m, "max_players"),
                OnlineCount       = MsgPackLite.GetInt(m, "online_count"),
                TotalPlayers      = MsgPackLite.GetInt(m, "total_players"),
                MatchKey          = MsgPackLite.GetString(m, "match_key"),
                EmptyAt           = MsgPackLite.GetLong(m, "empty_at"),
                EmptyGraceSec     = MsgPackLite.GetInt(m, "empty_grace_sec"),
                DisconnectKeepSec = MsgPackLite.GetInt(m, "disconnect_keep_sec"),
            };
            var players = MsgPackLite.GetArray(m, "players");
            if (players != null)
            {
                r.Players = new GmPlayerInfo[players.Count];
                for (int i = 0; i < players.Count; i++)
                {
                    if (players[i] is Dictionary<string, object> pm)
                        r.Players[i] = GmPlayerInfo.From(pm);
                }
            }
            else r.Players = System.Array.Empty<GmPlayerInfo>();
            return r;
        }
    }

    public struct GmPlayerInfo
    {
        public int Id, State;
        public long DisconnectTime;  // 断线时刻（unix ms）；在线时为 0

        public static GmPlayerInfo From(Dictionary<string, object> m)
        {
            return new GmPlayerInfo
            {
                Id             = MsgPackLite.GetInt(m, "id"),
                State          = MsgPackLite.GetInt(m, "state"),
                DisconnectTime = MsgPackLite.GetLong(m, "disconnect_time"),
            };
        }
    }

    public struct GmPerfPush
    {
        public int Goroutines;
        public double HeapMB, SysMB;
        public uint GCCount;
        public long GCPauseUs;
        public int Rooms, Players;

        public static GmPerfPush From(Dictionary<string, object> m)
        {
            return new GmPerfPush
            {
                Goroutines = MsgPackLite.GetInt(m, "goroutines"),
                HeapMB     = MsgPackLite.GetDouble(m, "heap_mb"),
                SysMB      = MsgPackLite.GetDouble(m, "sys_mb"),
                GCCount    = (uint)MsgPackLite.GetLong(m, "gc_count"),
                GCPauseUs  = MsgPackLite.GetLong(m, "gc_pause_us"),
                Rooms      = MsgPackLite.GetInt(m, "rooms"),
                Players    = MsgPackLite.GetInt(m, "players"),
            };
        }
    }

    public struct GmPlayerRate
    {
        public int Pid;
        public int MsgPer5Sec;

        public static GmPlayerRate From(Dictionary<string, object> m)
        {
            return new GmPlayerRate
            {
                Pid        = MsgPackLite.GetInt(m, "pid"),
                MsgPer5Sec = MsgPackLite.GetInt(m, "msg_5sec"),
            };
        }
    }

    public struct GmRatesPush
    {
        public List<GmPlayerRate> Top;

        public static GmRatesPush From(Dictionary<string, object> m)
        {
            var push = new GmRatesPush { Top = new List<GmPlayerRate>() };
            var arr = MsgPackLite.GetArray(m, "top");
            if (arr != null)
            {
                foreach (var item in arr)
                {
                    if (item is Dictionary<string, object> pm)
                        push.Top.Add(GmPlayerRate.From(pm));
                }
            }
            return push;
        }
    }

    // ===================== Log Entry =====================

    public struct GmLogEntry
    {
        public long Ts;
        public string Level, Msg, Attrs;

        public static GmLogEntry From(Dictionary<string, object> m)
        {
            return new GmLogEntry
            {
                Ts    = MsgPackLite.GetLong(m, "ts"),
                Level = MsgPackLite.GetString(m, "level"),
                Msg   = MsgPackLite.GetString(m, "msg"),
                Attrs = MsgPackLite.GetString(m, "attrs"),
            };
        }
    }

    // ===================== Room Inspect (Phase 3) =====================

    public struct GmEntityAuth
    {
        public int EntityId, OwnerId;

        public static GmEntityAuth From(Dictionary<string, object> m)
        {
            return new GmEntityAuth
            {
                EntityId = MsgPackLite.GetInt(m, "entity_id"),
                OwnerId  = MsgPackLite.GetInt(m, "owner_id"),
            };
        }
    }

    public struct GmKVEntry
    {
        public int PlayerId, Key;
        public byte[] Value;

        public static GmKVEntry From(Dictionary<string, object> m)
        {
            return new GmKVEntry
            {
                PlayerId = MsgPackLite.GetInt(m, "player_id"),
                Key      = MsgPackLite.GetInt(m, "key"),
                Value    = MsgPackLite.GetBytes(m, "value"),
            };
        }
    }

    public struct GmRoomInspect
    {
        public bool Ok;
        public int Id;
        public bool Running, Paused;
        public uint FrameNumber;
        public int FrameRate, MaxPlayers, OnlineCount, TotalPlayers;
        public string MatchKey;
        public int FrameBufferLen, FrameBufferCap;
        public uint OldestBufferedFrame;
        public uint SnapshotFrame;
        public int SnapshotSizeBytes;
        public uint SnapshotStaleFrames;
        public uint DataVersion;
        public GmEntityAuth[] EntityAuthority;
        public GmKVEntry[] KVEntries;

        public bool HasData;

        public static GmRoomInspect From(Dictionary<string, object> m)
        {
            var r = new GmRoomInspect
            {
                HasData             = true,
                Ok                  = MsgPackLite.GetBool(m, "ok"),
                Id                  = MsgPackLite.GetInt(m, "id"),
                Running             = MsgPackLite.GetBool(m, "running"),
                Paused              = MsgPackLite.GetBool(m, "paused"),
                FrameNumber         = (uint)MsgPackLite.GetLong(m, "frame_number"),
                FrameRate           = MsgPackLite.GetInt(m, "frame_rate"),
                MaxPlayers          = MsgPackLite.GetInt(m, "max_players"),
                OnlineCount         = MsgPackLite.GetInt(m, "online_count"),
                TotalPlayers        = MsgPackLite.GetInt(m, "total_players"),
                MatchKey            = MsgPackLite.GetString(m, "match_key"),
                FrameBufferLen      = MsgPackLite.GetInt(m, "frame_buffer_len"),
                FrameBufferCap      = MsgPackLite.GetInt(m, "frame_buffer_cap"),
                OldestBufferedFrame = (uint)MsgPackLite.GetLong(m, "oldest_buffered_frame"),
                SnapshotFrame       = (uint)MsgPackLite.GetLong(m, "snapshot_frame"),
                SnapshotSizeBytes   = MsgPackLite.GetInt(m, "snapshot_size_bytes"),
                SnapshotStaleFrames = (uint)MsgPackLite.GetLong(m, "snapshot_stale_frames"),
                DataVersion         = (uint)MsgPackLite.GetLong(m, "data_version"),
            };

            var eaArr = MsgPackLite.GetArray(m, "entity_authority");
            if (eaArr != null)
            {
                r.EntityAuthority = new GmEntityAuth[eaArr.Count];
                for (int i = 0; i < eaArr.Count; i++)
                    if (eaArr[i] is Dictionary<string, object> em)
                        r.EntityAuthority[i] = GmEntityAuth.From(em);
            }
            else r.EntityAuthority = System.Array.Empty<GmEntityAuth>();

            var kvArr = MsgPackLite.GetArray(m, "kv_entries");
            if (kvArr != null)
            {
                r.KVEntries = new GmKVEntry[kvArr.Count];
                for (int i = 0; i < kvArr.Count; i++)
                    if (kvArr[i] is Dictionary<string, object> km)
                        r.KVEntries[i] = GmKVEntry.From(km);
            }
            else r.KVEntries = System.Array.Empty<GmKVEntry>();

            return r;
        }
    }
}
