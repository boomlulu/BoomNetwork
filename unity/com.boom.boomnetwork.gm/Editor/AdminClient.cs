using System;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace BoomNetwork.GM.Editor
{
    public class AdminClient : IDisposable
    {
        public string BaseUrl { get; set; }
        public int TimeoutMs { get; set; }

        private HttpClient _http;

        public AdminClient(string baseUrl = "http://127.0.0.1:9091", int timeoutMs = 600)
        {
            BaseUrl = baseUrl;
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
            // 游戏流量
            public long GameRxTotal, GameTxTotal;
            public long GameRx1Min,  GameTx1Min;
            public long GameRx5Sec,  GameTx5Sec;
            // GM 流量
            public long GmRxTotal, GmTxTotal;
            public long GmRx1Min,  GmTx1Min;
            public long GmRx5Sec,  GmTx5Sec;
        }

        public StatsResult FetchStats()
        {
            var r = new StatsResult();
            var json = Get("/stats");
            if (json == null) return r;
            r.HasData     = true;
            r.GameRxTotal = ParseLong(json, "game_rx_total");
            r.GameTxTotal = ParseLong(json, "game_tx_total");
            r.GameRx1Min  = ParseLong(json, "game_rx_1min");
            r.GameTx1Min  = ParseLong(json, "game_tx_1min");
            r.GameRx5Sec  = ParseLong(json, "game_rx_5sec");
            r.GameTx5Sec  = ParseLong(json, "game_tx_5sec");
            r.GmRxTotal   = ParseLong(json, "gm_rx_total");
            r.GmTxTotal   = ParseLong(json, "gm_tx_total");
            r.GmRx1Min    = ParseLong(json, "gm_rx_1min");
            r.GmTx1Min    = ParseLong(json, "gm_tx_1min");
            r.GmRx5Sec    = ParseLong(json, "gm_rx_5sec");
            r.GmTx5Sec    = ParseLong(json, "gm_tx_5sec");
            return r;
        }

        // ===================== /messages =====================

        public struct MsgEntry
        {
            public long Ts;
            public string Dir, Name;
            public int Cmd, Pid, Size;
        }

        public MsgEntry[] FetchMessages(int limit = 100)
        {
            var json = Get($"/messages?limit={limit}");
            if (json == null || json.Length < 3) return Array.Empty<MsgEntry>();
            return ParseMsgArray(json);
        }

        static MsgEntry[] ParseMsgArray(string json)
        {
            // 轻量解析 JSON 数组 [{...}, {...}]
            var entries = new System.Collections.Generic.List<MsgEntry>();
            int i = 0;
            while (i < json.Length)
            {
                int start = json.IndexOf('{', i);
                if (start < 0) break;
                int end = json.IndexOf('}', start);
                if (end < 0) break;
                var obj = json.Substring(start, end - start + 1);
                entries.Add(new MsgEntry
                {
                    Ts   = ParseLong(obj, "ts"),
                    Dir  = ParseStr(obj, "dir"),
                    Name = ParseStr(obj, "name"),
                    Cmd  = ParseInt(obj, "cmd"),
                    Pid  = ParseInt(obj, "pid"),
                    Size = ParseInt(obj, "size"),
                });
                i = end + 1;
            }
            return entries.ToArray();
        }

        // ===================== HTTP =====================

        private string Get(string path)
        {
            try
            {
                var url = BaseUrl.TrimEnd('/') + path;
                var task = _http.GetStringAsync(url);
                task.Wait(TimeoutMs + 100);
                return task.IsCompletedSuccessfully ? task.Result : null;
            }
            catch { return null; }
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

        public static string FmtBytes(long b)
        {
            if (b < 0)       return "—";
            if (b < 1024)    return $"{b} B";
            if (b < 1 << 20) return $"{b / 1024.0:F1} KB";
            if (b < 1 << 30) return $"{b / (1024.0 * 1024):F2} MB";
            return $"{b / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
