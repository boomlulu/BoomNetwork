// AdminWsClient — 后台线程 WebSocket GM 客户端
// 线程安全：后台线程负责连接/收发，主线程通过 ConcurrentQueue 读取

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace BoomNetwork.GM.Editor
{
    public class AdminWsClient : IDisposable
    {
        public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open && _authenticated;
        public bool IsConnecting => _bgThread != null && _bgThread.IsAlive && !_authenticated;

        /// <summary>主线程从这里读取入站信封</summary>
        public readonly ConcurrentQueue<GmEnvelope> Inbound = new ConcurrentQueue<GmEnvelope>();

        private readonly ConcurrentQueue<byte[]> _outbound = new ConcurrentQueue<byte[]>();

        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private Thread _bgThread;
        private string _url;
        private string _token;
        private volatile bool _authenticated;
        private volatile bool _disposed;
        private int _authFailCount;

        /// <summary>连接到 GM WebSocket 服务器</summary>
        public void Connect(string httpAdminUrl, string token)
        {
            Disconnect();

            // http://host:port → ws://host:port/ws
            _url = httpAdminUrl.TrimEnd('/').Replace("https://", "wss://").Replace("http://", "ws://") + "/ws";
            _token = token;
            _authenticated = false;
            _authFailCount = 0;

            _cts = new CancellationTokenSource();
            _bgThread = new Thread(RunLoop) { IsBackground = true, Name = "BoomGMWs" };
            _bgThread.Start();
        }

        public void Disconnect()
        {
            _authenticated = false;
            _cts?.Cancel();
            try { _ws?.Dispose(); } catch { }
            _ws = null;
            // 清空队列
            while (_outbound.TryDequeue(out _)) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        // ===================== 发送 API =====================

        /// <summary>发送 RPC 请求（Kick/StopRoom/Netsim）</summary>
        public void SendRpc(string topic, Dictionary<string, object> payload, string id = null)
        {
            var env = new GmEnvelope
            {
                Type = "rpc",
                ID = id ?? Guid.NewGuid().ToString("N").Substring(0, 8),
                Topic = topic,
                Payload = MsgPackLite.EncodeMap(payload),
            };
            _outbound.Enqueue(env.Encode());
        }

        /// <summary>发送 Ping 心跳</summary>
        public void SendPing()
        {
            var env = new GmEnvelope { Type = "ping" };
            _outbound.Enqueue(env.Encode());
        }

        // ===================== 后台线程 =====================

        private void RunLoop()
        {
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    ConnectAndLoop(ct).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[GM-WS] Connection error: {ex.GetType().Name}: {ex.Message}");
                }

                _authenticated = false;

                // Auth 连续失败 3 次后停止重连，避免无限刷日志
                if (_authFailCount >= 3)
                {
                    UnityEngine.Debug.LogWarning("[GM-WS] Auth failed 3 times, stopping reconnect. Switch profile or fix token to retry.");
                    break;
                }

                // 重连退避 3 秒
                if (!ct.IsCancellationRequested)
                {
                    try { Task.Delay(3000, ct).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private async Task ConnectAndLoop(CancellationToken ct)
        {
            _ws = new ClientWebSocket();
            _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            await _ws.ConnectAsync(new Uri(_url), ct);

            // ===== 认证 =====
            var authPayload = MsgPackLite.EncodeMap(new Dictionary<string, object> { ["token"] = _token ?? "" });
            var authEnv = new GmEnvelope { Type = "auth", Payload = authPayload };
            await SendFrame(authEnv.Encode(), ct);

            // 等待 auth_ok / auth_err
            var authRsp = await ReceiveFrame(ct);
            if (authRsp == null) return;
            var authEnvRsp = GmEnvelope.Decode(authRsp);
            if (authEnvRsp.Type == "auth_err")
            {
                _authFailCount++;
                var errDetail = authEnvRsp.DecodePayload();
                var errMsg = errDetail != null ? MsgPackLite.GetString(errDetail, "error") : "unknown";
                UnityEngine.Debug.LogError($"[GM-WS] Auth failed: {errMsg} (check Admin Token in Deploy Profile)");
                return;
            }
            if (authEnvRsp.Type != "auth_ok")
            {
                UnityEngine.Debug.LogError($"[GM-WS] Unexpected auth response: {authEnvRsp.Type}");
                return;
            }

            _authenticated = true;
            _authFailCount = 0;

            // ===== 订阅所有 topic =====
            foreach (var topic in GmTopics.All)
            {
                var subEnv = new GmEnvelope { Type = "sub", Topic = topic };
                await SendFrame(subEnv.Encode(), ct);
            }

            // ===== 收发主循环 =====
            // 注意：ClientWebSocket.ReceiveAsync 被 CancellationToken 取消会导致连接 Abort，
            // 所以不能用短超时轮询。改为并发：recv 阻塞等数据，send 独立 Task 刷出站队列。
            var sendTask = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    while (_outbound.TryDequeue(out var frame))
                        await SendFrame(frame, ct);
                    await Task.Delay(50, ct);
                }
            }, ct);

            var recvBuf = new byte[65536];
            var msgBuf = new List<byte>();
            try
            {
                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    var seg = new ArraySegment<byte>(recvBuf);
                    var result = await _ws.ReceiveAsync(seg, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    if (result.Count > 0)
                    {
                        if (result.EndOfMessage && msgBuf.Count == 0)
                        {
                            var data = new byte[result.Count];
                            Buffer.BlockCopy(recvBuf, 0, data, 0, result.Count);
                            ProcessInboundFrame(data);
                        }
                        else
                        {
                            for (int i = 0; i < result.Count; i++)
                                msgBuf.Add(recvBuf[i]);
                            if (result.EndOfMessage)
                            {
                                ProcessInboundFrame(msgBuf.ToArray());
                                msgBuf.Clear();
                            }
                        }
                    }
                }
            }
            finally
            {
                try { await sendTask; } catch { /* send task cleanup */ }
            }
        }

        private void ProcessInboundFrame(byte[] data)
        {
            try
            {
                var env = GmEnvelope.Decode(data);
                if (!string.IsNullOrEmpty(env.Type))
                    Inbound.Enqueue(env);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[GM-WS] Decode error: {ex.Message} (len={data.Length})");
            }
        }

        private async Task SendFrame(byte[] data, CancellationToken ct)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            await _ws.SendAsync(
                new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, ct);
        }

        private async Task<byte[]> ReceiveFrame(CancellationToken ct)
        {
            var buf = new byte[65536];
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(5000); // 5 秒认证超时
                try
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), timeout.Token);
                    if (result.Count == 0 || result.MessageType == WebSocketMessageType.Close)
                        return null;
                    var data = new byte[result.Count];
                    Buffer.BlockCopy(buf, 0, data, 0, result.Count);
                    return data;
                }
                catch { return null; }
            }
        }
    }
}
