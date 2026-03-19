using System;

namespace BoomNetwork.Core.Transport
{
    public enum TransportState
    {
        Disconnected,
        Connecting,
        Connected,
    }

    /// <summary>
    /// 传输层接口 — 字节收发 + 连接管理
    /// 外部每帧调用 Tick 驱动，所有回调在 Tick 内同步触发。
    /// </summary>
    public interface ITransport
    {
        void Connect(string host, int port);
        void Disconnect();
        void Reconnect();
        void Send(byte[] data, int offset, int length);
        void Tick();

        TransportState State { get; }

        event Action OnConnected;
        event Action OnDisconnected;
        event Action<byte[], int, int> OnData;
        event Action<NetworkError> OnError;
    }
}
