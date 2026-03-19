using System;

namespace BoomNetwork.Core
{
    /// <summary>
    /// 错误分类
    /// </summary>
    public enum ErrorCategory
    {
        Transport = 1000,    // 传输层错误
        Session   = 2000,    // 会话层错误
        Connection = 3000,   // 连接管理层错误
        FrameSync = 4000,    // 帧同步层错误
    }

    /// <summary>
    /// 错误码
    /// </summary>
    public enum ErrorCode
    {
        // --- Transport 1xxx ---
        ConnectFailed       = 1001,
        SendFailed          = 1002,
        ConnectionDropped   = 1003,
        TransportError      = 1004,

        // --- Session 2xxx ---
        RequestTimeout      = 2001,
        SessionReset        = 2002,

        // --- Connection 3xxx ---
        HeartbeatTimeout    = 3001,
        ReconnectFailed     = 3002,
        AllStrategiesExhausted = 3003,

        // --- FrameSync 4xxx ---
        SessionBindTimeout  = 4001,
        SessionBindFailed   = 4002,
    }

    /// <summary>
    /// 结构化错误信息
    /// </summary>
    public struct NetworkError
    {
        public ErrorCode Code;
        public string Message;

        public NetworkError(ErrorCode code, string message)
        {
            Code = code;
            Message = message;
        }

        public override string ToString()
        {
            return $"[{(int)Code} {Code}] {Message}";
        }

        /// <summary>
        /// 错误所属分类
        /// </summary>
        public ErrorCategory Category => (ErrorCategory)((int)Code / 1000 * 1000);

        /// <summary>
        /// 是否是传输层错误
        /// </summary>
        public bool IsTransport => Category == ErrorCategory.Transport;

        /// <summary>
        /// 是否是可恢复的错误（重连可能解决）
        /// </summary>
        public bool IsRecoverable => Code == ErrorCode.ConnectionDropped
                                   || Code == ErrorCode.HeartbeatTimeout
                                   || Code == ErrorCode.RequestTimeout;
    }
}
