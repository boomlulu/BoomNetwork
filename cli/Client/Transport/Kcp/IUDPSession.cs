#if !UNITY_WEBGL || UNITY_EDITOR
namespace KcpProject
{
    /// <summary>
    /// KCP UDP 会话抽象接口。
    ///
    /// 职责:
    ///   1. 允许单元测试通过 mock 注入，隔离网络层（C2 TDD 需求）
    ///   2. 允许高级用户提供自定义 UDP 会话实现
    ///
    /// 实现类: <see cref="UDPSession"/>（默认实现，基于 kcp-csharp）
    /// </summary>
    public interface IUDPSession
    {
        /// <summary>底层 Socket 是否已连接</summary>
        bool IsConnected { get; }

        /// <summary>ACK 无延迟模式（true = 立即 ACK）</summary>
        bool AckNoDelay { get; set; }

        /// <summary>写入延迟模式（false = 立即发送）</summary>
        bool WriteDelay { get; set; }

        /// <summary>连接到指定服务器（同步，含 DNS 解析）</summary>
        void Connect(string host, int port);

        /// <summary>关闭会话并释放 Socket</summary>
        void Close();

        /// <summary>
        /// 发送数据。
        /// 返回实际发送字节数；返回 0 表示 KCP 发送窗口已满（发包失败）。
        /// 调用方必须检查返回值，0 表示数据未发出。
        /// </summary>
        int Send(byte[] data, int index, int length);

        /// <summary>接收 KCP 解包后的数据。返回 ≤0 表示无数据可读</summary>
        int Recv(byte[] data, int index, int length);

        /// <summary>驱动 KCP 内部状态机（重传检测、ACK 等），每帧调用</summary>
        void Update();
    }
}
#endif
