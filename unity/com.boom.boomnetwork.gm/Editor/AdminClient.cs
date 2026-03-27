using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace BoomNetwork.GM.Editor
{
    public class AdminClient : IDisposable
    {
        public string BaseUrl { get; set; }
        public string Token { get; set; }
        public int TimeoutMs { get; set; }

        private HttpClient _http;

        public AdminClient(string baseUrl = "http://127.0.0.1:9091", int timeoutMs = 600)
        {
            BaseUrl = baseUrl;
            Token = "";
            TimeoutMs = timeoutMs;
            _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        }

        public void Dispose() => _http?.Dispose();

        // ===================== /health =====================

        public struct HealthResult
        {
            public bool IsOnline;
            public int Rooms, Players;
            public string Uptime;
        }

        public HealthResult FetchHealth()
        {
            var r = new HealthResult();
            var json = Get("/health");
            if (json == null) return r;
            r.IsOnline = json.Contains("\"ok\"");
            r.Rooms    = ParseInt(json, "rooms");
            r.Players  = ParseInt(json, "players");
            r.Uptime   = ParseStr(json, "uptime");
            return r;
        }

        // ===================== /stats =====================

        public struct StatsResult
        {
            public bool HasData;
            public long GameRxTotal, GameTxTotal, GameRx1Min, GameTx1Min, GameRx5Sec, GameTx5Sec;
            public long GmRxTotal, GmTxTotal, GmRx1Min, GmTx1Min, GmRx5Sec, GmTx5Sec;
        }

        public StatsResult FetchStats()
        {
            var r = new StatsResult();
            var j = Get("/stats");
            if (j == null) return r;
            r.HasData     = true;
            r.GameRxTotal = ParseLong(j, "game_rx_total"); r.GameTxTotal = ParseLong(j, "game_tx_total");
            r.GameRx1Min  = ParseLong(j, "game_rx_1min");  r.GameTx1Min  = ParseLong(j, "game_tx_1min");
            r.GameRx5Sec  = ParseLong(j, "game_rx_5sec");  r.GameTx5Sec  = ParseLong(j, "game_tx_5sec");
            r.GmRxTotal   = ParseLong(j, "gm_rx_total");   r.GmTxTotal   = ParseLong(j, "gm_tx_total");
            r.GmRx1Min    = ParseLong(j, "gm_rx_1min");    r.GmTx1Min    = ParseLong(j, "gm_tx_1min");
            r.GmRx5Sec    = ParseLong(j, "gm_rx_5sec");    r.GmTx5Sec    = ParseLong(j, "gm_tx_5sec");
            return r;
        }

        // ===================== /messages =====================

        public struct MsgEntry
        {
            public long Ts;
            public string Dir, Name;
            public int Cmd, Pid, Size;
            public int RoomID;
            public string MatchKey;
        }

        public MsgEntry[] FetchMessages(int limit = 100)
        {
            var json = Get($"/messages?limit={limit}");
            if (json == null || json.Length < 3) return Array.Empty<MsgEntry>();
            return ParseMsgArray(json);
        }

        // ===================== /rooms (G1) =====================

        public struct RoomDetail
        {
            public int Id;
            public bool Running, Paused;
            public uint FrameNumber;
            public int FrameRate, MaxPlayers, OnlineCount, TotalPlayers;
            public string MatchKey;
            public PlayerInfo[] Players;
        }

        public struct PlayerInfo
        {
            public int Id;
            public int State; // 0=online, 1=disconnected
        }

        public RoomDetail[] FetchRooms()
        {
            var json = Get("/rooms");
            if (json == null || json.Length < 3) return Array.Empty<RoomDetail>();
            return ParseRoomArray(json);
        }

        // ===================== POST /kick/{pid} (G2) =====================

        public struct ActionResult
        {
            public bool Ok;
            public string Error;
        }

        public ActionResult KickPlayer(int playerId) => Post($"/kick/{playerId}");

        // ===================== POST /rooms/stop/{id} (G3) =====================

        public ActionResult StopRoom(int roomId) => Post($"/rooms/stop/{roomId}");

        public ActionResult KillRoom(int roomId) => Post($"/rooms/kill/{roomId}");

        public ActionResult CreateRoom(int maxPlayers = 2, string matchKey = "")
        {
            var query = $"/rooms/create?max_players={maxPlayers}";
            if (!string.IsNullOrEmpty(matchKey)) query += $"&match_key={Uri.EscapeDataString(matchKey)}";
            return Post(query);
        }

        // ===================== GET/POST /netsim =====================

        public struct NetSimResult
        {
            public bool HasData;
            public bool Enabled;
            public int LatencyMs, JitterMs, LossPercent;
            public long Dropped, Delayed;
        }

        public NetSimResult FetchNetSim()
        {
            var r = new NetSimResult();
            var j = Get("/netsim");
            if (j == null) return r;
            r.HasData     = true;
            r.Enabled     = ParseBool(j, "enabled");
            r.LatencyMs   = ParseInt(j, "latency_ms");
            r.JitterMs    = ParseInt(j, "jitter_ms");
            r.LossPercent = ParseInt(j, "loss_percent");
            r.Dropped     = ParseLong(j, "stats_dropped");
            r.Delayed     = ParseLong(j, "stats_delayed");
            return r;
        }

        public ActionResult SetNetSim(bool enabled, int latencyMs, int jitterMs, int lossPercent)
        {
            return PostJson("/netsim",
                $"{{\"enabled\":{(enabled ? "true" : "false")},\"latency_ms\":{latencyMs},\"jitter_ms\":{jitterMs},\"loss_percent\":{lossPercent}}}");
        }

        // ===================== GET/POST /log-level =====================

        public struct LogLevelResult
        {
            public bool HasData;
            public string Level;
        }

        public LogLevelResult GetLogLevel()
        {
            var r = new LogLevelResult();
            var j = Get("/log-level");
            if (j == null) return r;
            r.HasData = true;
            r.Level = ParseStr(j, "level");
            return r;
        }

        public ActionResult SetLogLevel(string level)
        {
            return PostJson("/log-level", $"{{\"level\":\"{level}\"}}");
        }

        // ===================== POST /config/reload =====================

        public ActionResult ReloadConfig()
        {
            return Post("/config/reload");
        }

        // ===================== GET /perf (HTTP fallback) =====================

        public struct PerfResult
        {
            public bool HasData;
            public int Goroutines;
            public double HeapMB, SysMB;
            public uint GCCount;
            public long GCPauseUs;
            public int Rooms, Players;
        }

        public PerfResult FetchPerf()
        {
            var r = new PerfResult();
            var j = Get("/perf");
            if (j == null) return r;
            r.HasData    = true;
            r.Goroutines = ParseInt(j, "goroutines");
            r.HeapMB     = ParseDouble(j, "heap_mb");
            r.SysMB      = ParseDouble(j, "sys_mb");
            r.GCCount    = (uint)ParseLong(j, "gc_count");
            r.GCPauseUs  = ParseLong(j, "gc_pause_us");
            r.Rooms      = ParseInt(j, "rooms");
            r.Players    = ParseInt(j, "players");
            return r;
        }

        // ===================== GET /rates (HTTP fallback) =====================

        public struct RatesResult
        {
            public bool HasData;
            public List<GmPlayerRate> Top;
        }

        public RatesResult FetchRates()
        {
            var r = new RatesResult { Top = new List<GmPlayerRate>() };
            var j = Get("/rates");
            if (j == null) return r;
            r.HasData = true;
            int depth = 0, start = -1;
            for (int i = 0; i < j.Length; i++)
            {
                if (j[i] == '{') { if (depth == 0) start = i; depth++; }
                else if (j[i] == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        var obj = j.Substring(start, i - start + 1);
                        r.Top.Add(new GmPlayerRate
                        {
                            Pid        = ParseInt(obj, "pid"),
                            MsgPer5Sec = ParseInt(obj, "msg_5sec"),
                        });
                        start = -1;
                    }
                }
            }
            return r;
        }

        // ===================== HTTP Core =====================

        private string Get(string path)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl.TrimEnd('/') + path);
                AddAuth(req);
                var task = _http.SendAsync(req);
                task.Wait(TimeoutMs + 100);
                if (!task.IsCompletedSuccessfully) return null;
                var readTask = task.Result.Content.ReadAsStringAsync();
                readTask.Wait(TimeoutMs);
                return readTask.IsCompletedSuccessfully ? readTask.Result : null;
            }
            catch { return null; }
        }

        private ActionResult PostJson(string path, string jsonBody)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl.TrimEnd('/') + path);
                AddAuth(req);
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                var task = _http.SendAsync(req);
                task.Wait(TimeoutMs + 100);
                if (!task.IsCompletedSuccessfully) return new ActionResult { Error = "timeout" };
                var readTask = task.Result.Content.ReadAsStringAsync();
                readTask.Wait(TimeoutMs);
                var json = readTask.IsCompletedSuccessfully ? readTask.Result : "";
                return new ActionResult { Ok = json.Contains("\"ok\":true"), Error = ParseStr(json, "error") };
            }
            catch (Exception e) { return new ActionResult { Error = e.Message }; }
        }

        private ActionResult Post(string path)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl.TrimEnd('/') + path);
                AddAuth(req);
                req.Content = new StringContent("", Encoding.UTF8);
                var task = _http.SendAsync(req);
                task.Wait(TimeoutMs + 100);
                if (!task.IsCompletedSuccessfully)
                    return new ActionResult { Error = "timeout" };
                var readTask = task.Result.Content.ReadAsStringAsync();
                readTask.Wait(TimeoutMs);
                var json = readTask.IsCompletedSuccessfully ? readTask.Result : "";
                bool ok = json.Contains("\"ok\":true");
                string err = ok ? "" : ParseStr(json, "error");
                return new ActionResult { Ok = ok, Error = err };
            }
            catch (Exception e)
            {
                return new ActionResult { Error = e.Message };
            }
        }

        private void AddAuth(HttpRequestMessage req)
        {
            if (!string.IsNullOrEmpty(Token))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
        }

        // ===================== JSON Parsing =====================

        public static int ParseInt(string j, string k) => (int)ParseLong(j, k);

        public static long ParseLong(string j, string k)
        {
            var m = Regex.Match(j, $@"""{k}""\s*:\s*(-?\d+)");
            return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : 0;
        }

        public static string ParseStr(string j, string k)
        {
            var m = Regex.Match(j, $@"""{k}""\s*:\s*""([^""]*)""");
            return m.Success ? m.Groups[1].Value : "";
        }

        public static bool ParseBool(string j, string k)
        {
            return j.Contains($"\"{k}\":true");
        }

        public static double ParseDouble(string j, string k)
        {
            var m = Regex.Match(j, $@"""{k}""\s*:\s*([0-9.eE+-]+)");
            return m.Success && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        public static string FmtBytes(long b)
        {
            if (b < 0)       return "—";
            if (b < 1024)    return $"{b} B";
            if (b < 1 << 20) return $"{b / 1024.0:F1} KB";
            if (b < 1 << 30) return $"{b / (1024.0 * 1024):F2} MB";
            return $"{b / (1024.0 * 1024 * 1024):F2} GB";
        }

        // ===================== Array Parsers =====================

        static MsgEntry[] ParseMsgArray(string json)
        {
            var list = new List<MsgEntry>();
            int i = 0;
            while ((i = json.IndexOf('{', i)) >= 0)
            {
                int end = json.IndexOf('}', i);
                if (end < 0) break;
                var obj = json.Substring(i, end - i + 1);
                list.Add(new MsgEntry
                {
                    Ts = ParseLong(obj, "ts"), Dir = ParseStr(obj, "dir"),
                    Name = ParseStr(obj, "name"), Cmd = ParseInt(obj, "cmd"),
                    Pid = ParseInt(obj, "pid"), Size = ParseInt(obj, "size"),
                    RoomID = ParseInt(obj, "room_id"), MatchKey = ParseStr(obj, "match_key"),
                });
                i = end + 1;
            }
            return list.ToArray();
        }

        static RoomDetail[] ParseRoomArray(string json)
        {
            var list = new List<RoomDetail>();
            // Split rooms by top-level { } (rooms are flat objects with nested players array)
            int depth = 0, start = -1;
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] == '{') { if (depth == 0) start = i; depth++; }
                else if (json[i] == '}') { depth--; if (depth == 0 && start >= 0) {
                    list.Add(ParseRoom(json.Substring(start, i - start + 1)));
                    start = -1;
                }}
            }
            return list.ToArray();
        }

        static RoomDetail ParseRoom(string obj)
        {
            var r = new RoomDetail
            {
                Id           = ParseInt(obj, "id"),
                Running      = ParseBool(obj, "running"),
                Paused       = ParseBool(obj, "paused"),
                FrameNumber  = (uint)ParseLong(obj, "frame_number"),
                FrameRate    = ParseInt(obj, "frame_rate"),
                MaxPlayers   = ParseInt(obj, "max_players"),
                OnlineCount  = ParseInt(obj, "online_count"),
                TotalPlayers = ParseInt(obj, "total_players"),
                MatchKey     = ParseStr(obj, "match_key"),
            };

            // Parse players array
            var players = new List<PlayerInfo>();
            int pStart = obj.IndexOf("\"players\":", StringComparison.Ordinal);
            if (pStart >= 0)
            {
                int pi = pStart;
                while ((pi = obj.IndexOf('{', pi)) >= 0)
                {
                    int pe = obj.IndexOf('}', pi);
                    if (pe < 0) break;
                    var po = obj.Substring(pi, pe - pi + 1);
                    players.Add(new PlayerInfo { Id = ParseInt(po, "id"), State = ParseInt(po, "state") });
                    pi = pe + 1;
                }
            }
            r.Players = players.ToArray();
            return r;
        }
    }
}
