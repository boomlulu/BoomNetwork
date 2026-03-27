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
            return new GmEnvelope
            {
                Type    = MsgPackLite.GetString(map, "t"),
                ID      = MsgPackLite.GetString(map, "id"),
                Topic   = MsgPackLite.GetString(map, "tp"),
                Payload = MsgPackLite.GetBytes(map, "p"),
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
            if (Payload != null) map["p"] = Payload;
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

        public static readonly string[] All = { Health, Stats, Messages, Rooms, Perf, Rates, Netsim };
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

        public static GmRoomDetail From(Dictionary<string, object> m)
        {
            var r = new GmRoomDetail
            {
                Id           = MsgPackLite.GetInt(m, "id"),
                Running      = MsgPackLite.GetBool(m, "running"),
                Paused       = MsgPackLite.GetBool(m, "paused"),
                FrameNumber  = (uint)MsgPackLite.GetLong(m, "frame_number"),
                FrameRate    = MsgPackLite.GetInt(m, "frame_rate"),
                MaxPlayers   = MsgPackLite.GetInt(m, "max_players"),
                OnlineCount  = MsgPackLite.GetInt(m, "online_count"),
                TotalPlayers = MsgPackLite.GetInt(m, "total_players"),
                MatchKey     = MsgPackLite.GetString(m, "match_key"),
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

        public static GmPlayerInfo From(Dictionary<string, object> m)
        {
            return new GmPlayerInfo
            {
                Id    = MsgPackLite.GetInt(m, "id"),
                State = MsgPackLite.GetInt(m, "state"),
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
}
