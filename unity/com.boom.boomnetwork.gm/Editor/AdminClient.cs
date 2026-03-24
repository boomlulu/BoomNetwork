using System;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace BoomNetwork.GM.Editor
{
    /// <summary>
    /// 服务器 Admin HTTP 客户端（Editor-only）
    ///
    /// 统一封装 /health、/stats 等 Admin API 调用。
    /// 所有方法同步阻塞（Editor 场景，非游戏线程）。
    /// </summary>
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
            public int Rooms;
            public int Players;
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
            public long RxTotal, TxTotal;
            public long Rx1Min,  Tx1Min;
            public long Rx5Sec,  Tx5Sec;
        }

        public StatsResult FetchStats()
        {
            var r = new StatsResult();
            var json = Get("/stats");
            if (json == null) return r;

            r.HasData = true;
            r.RxTotal = ParseLong(json, "rx_total_bytes");
            r.TxTotal = ParseLong(json, "tx_total_bytes");
            r.Rx1Min  = ParseLong(json, "rx_1min_bytes");
            r.Tx1Min  = ParseLong(json, "tx_1min_bytes");
            r.Rx5Sec  = ParseLong(json, "rx_5sec_bytes");
            r.Tx5Sec  = ParseLong(json, "tx_5sec_bytes");
            return r;
        }

        // ===================== /rooms (预留) =====================

        // public RoomInfo[] FetchRooms() { ... }
        // public bool KickPlayer(int playerId) { ... }
        // public bool StopRoom(int roomId) { ... }

        // ===================== HTTP Core =====================

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
        // 轻量正则，不依赖 Newtonsoft

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
