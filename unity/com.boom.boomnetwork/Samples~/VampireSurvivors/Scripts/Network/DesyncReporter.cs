// BoomNetwork VampireSurvivors Demo — Desync 事件上报器
//
// 用途：OnDesync 触发时将结构化数据 POST 到 report_server /desync 端点，
//       供 bn-desync-analyze skill 拉取并做跨客户端 diff 分析。
//
// 数据流：
//   VSNetworkManager.OnDesync
//     → DesyncReporter.Report(json)
//         ├─ 同步写盘  pending_desync.jsonl   ← crash 保护
//         └─ ThreadPool POST /desync

using System;
using System.IO;
using System.Threading;

namespace BoomNetwork.Samples.VampireSurvivors
{
    public static class DesyncReporter
    {
        // framesync_server.py 默认本机 9877；跨设备测试时改为服务器 IP:9877
        public const string  ServerBase  = "http://localhost:9877";
        private const string ServerUrl   = ServerBase + "/desync";
        private const string LogUrl      = ServerBase + "/log";
        private const string PendingFile = "pending_desync.jsonl";

        private static string _persistentPath;

        /// <summary>在 MonoBehaviour.Start()（主线程）调用，缓存 persistentDataPath。</summary>
        public static void Init(string persistentDataPath)
        {
            _persistentPath = persistentDataPath;
        }

        /// <summary>POST 结构化 desync JSON 到 report_server（异步，失败静默）。</summary>
        public static void Report(string json)
        {
            // 1. 同步落盘（游戏崩溃时保留数据）
            if (!string.IsNullOrEmpty(_persistentPath))
            {
                try
                {
                    string path = Path.Combine(_persistentPath, PendingFile);
                    // 单行存储（换行替换为空格防止 jsonl 行解析失败）
                    File.AppendAllText(path, json.Replace('\n', ' ') + "\n");
                }
                catch { }
            }

            // 2. 异步 HTTP POST（不阻塞主线程）
            string snapshot = json;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var client = new System.Net.WebClient();
                    client.Headers[System.Net.HttpRequestHeader.ContentType] = "application/json";
                    client.UploadString(ServerUrl, "POST", snapshot);
                }
                catch { /* 服务器未启动时静默忽略 */ }
            });
        }

        // ── JSON 构造工具 ───────────────────────────────────────────────────
        public static string EscJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
