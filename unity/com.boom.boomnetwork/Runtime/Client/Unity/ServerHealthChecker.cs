using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace BoomNetwork.Unity
{
    /// <summary>
    /// 服务器健康检查组件
    ///
    /// 通过 HTTP GET /health 轮询服务器状态。
    /// 不走 TCP 游戏协议，不产生游戏日志，不影响玩家计数器。
    ///
    /// 用法：拖到 GameObject → 设置 adminUrl → 监听 OnServerOnline/OnServerOffline
    /// </summary>
    public class ServerHealthChecker : MonoBehaviour
    {
        [Header("Config")]
        [Tooltip("Admin HTTP 地址，对应服务器 adminAddr")]
        [SerializeField] private string adminUrl = "http://127.0.0.1:9091";

        [Tooltip("轮询间隔（秒）")]
        [SerializeField] private float pollIntervalSec = 5f;

        [Tooltip("请求超时（秒）")]
        [SerializeField] private float timeoutSec = 3f;

        [Header("Status (readonly)")]
        [SerializeField] private bool _isOnline;
        [SerializeField] private string _lastResponse;
        [SerializeField] private float _lastCheckAge;

        // --- 事件 ---
        public event Action OnServerOnline;
        public event Action OnServerOffline;
        public event Action<HealthStatus> OnStatusUpdated;

        // --- 属性 ---
        public bool IsOnline => _isOnline;
        public HealthStatus LastStatus { get; private set; }

        private Coroutine _pollCoroutine;
        private float _lastCheckTime = -999f;
        private bool _destroyed; // H5: scene-unload guard

        void OnEnable()  => _pollCoroutine = StartCoroutine(PollLoop());
        void OnDisable() { if (_pollCoroutine != null) StopCoroutine(_pollCoroutine); }

        // H5: 防止 OnDestroy 后 coroutine 回调继续触发 Unity 事件
        void OnDestroy()
        {
            _destroyed = true;
            StopAllCoroutines();
        }

        void Update() => _lastCheckAge = Time.time - _lastCheckTime;

        // ===================== Poll Loop =====================

        private IEnumerator PollLoop()
        {
            while (true)
            {
                if (_destroyed) yield break; // H5
                yield return StartCoroutine(CheckOnce());
                yield return new WaitForSeconds(pollIntervalSec);
            }
        }

        private IEnumerator CheckOnce()
        {
            string url = adminUrl.TrimEnd('/') + "/health";
            using var req = UnityWebRequest.Get(url);
            req.timeout = Mathf.Max(1, (int)timeoutSec);

            yield return req.SendWebRequest();

            if (_destroyed) yield break; // H5: guard after async yield

            _lastCheckTime = Time.time;
            bool wasOnline = _isOnline;

            if (req.result == UnityWebRequest.Result.Success)
            {
                _lastResponse = req.downloadHandler.text;
                var status = ParseStatus(_lastResponse);
                LastStatus = status;
                _isOnline = true;

                OnStatusUpdated?.Invoke(status);
                if (!wasOnline)
                {
                    Debug.Log($"[HealthChecker] Server online — rooms={status.Rooms} players={status.Players} uptime={status.Uptime}");
                    OnServerOnline?.Invoke();
                }
            }
            else
            {
                _lastResponse = req.error;
                _isOnline = false;

                if (wasOnline)
                {
                    Debug.LogWarning($"[HealthChecker] Server offline — {req.error}");
                    OnServerOffline?.Invoke();
                }
            }
        }

        // ===================== 手动触发 =====================

        /// <summary>立刻检查一次（不等待下次轮询）</summary>
        public void CheckNow() => StartCoroutine(CheckOnce());

        // ===================== JSON 解析 =====================

        private static HealthStatus ParseStatus(string json)
        {
            // 轻量手动解析，不依赖 Newtonsoft
            var s = new HealthStatus { Raw = json };
            s.IsOk   = json.Contains("\"ok\"");
            s.Rooms   = ParseInt(json, "rooms");
            s.Players = ParseInt(json, "players");
            s.Uptime  = ParseString(json, "uptime");
            return s;
        }

        private static int ParseInt(string json, string key)
        {
            int idx = json.IndexOf($"\"{key}\":", StringComparison.Ordinal);
            if (idx < 0) return 0;
            idx += key.Length + 3;
            int end = idx;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            return int.TryParse(json.Substring(idx, end - idx), out int v) ? v : 0;
        }

        private static string ParseString(string json, string key)
        {
            int idx = json.IndexOf($"\"{key}\":", StringComparison.Ordinal);
            if (idx < 0) return "";
            idx += key.Length + 3;
            if (idx >= json.Length || json[idx] != '"') return "";
            idx++;
            int end = json.IndexOf('"', idx);
            return end < 0 ? "" : json.Substring(idx, end - idx);
        }
    }

    [Serializable]
    public struct HealthStatus
    {
        public bool   IsOk;
        public int    Rooms;
        public int    Players;
        public string Uptime;
        public string Raw;
    }
}
