using System;
using BoomNetwork.Core;
using BoomNetwork.Client.Session;

namespace BoomNetwork.Client.Connection
{
    /// <summary>
    /// 组合重连策略 — 按顺序尝试，前一个失败后降级到下一个
    ///
    /// 默认: QuickReconnect → SnapshotReconnect
    /// 可自定义策略链和每个策略的重试次数
    /// </summary>
    public class CompositeReconnectStrategy : IReconnectStrategy
    {
        public string Name => "CompositeReconnect";

        private readonly (IReconnectStrategy strategy, int maxAttempts)[] _chain;
        private int _chainIndex;
        private int _currentAttempts;
        private bool _cancelled;

        /// <summary>
        /// 创建默认组合策略: 快速重连(3次) → 快照重连(2次)
        /// </summary>
        public static CompositeReconnectStrategy Default()
        {
            return new CompositeReconnectStrategy(
                (new QuickReconnectStrategy(), 3),
                (new SnapshotReconnectStrategy(), 2)
            );
        }

        public CompositeReconnectStrategy(params (IReconnectStrategy strategy, int maxAttempts)[] chain)
        {
            _chain = chain;
        }

        public void Attempt(NetworkSession session, string host, int port,
            ReconnectContext context, Action onSuccess, Action<NetworkError> onFail)
        {
            _cancelled = false;
            _chainIndex = 0;
            _currentAttempts = 0;

            TryNext(session, host, port, context, onSuccess, onFail);
        }

        public void Cancel()
        {
            _cancelled = true;
            if (_chainIndex < _chain.Length)
                _chain[_chainIndex].strategy.Cancel();
        }

        private void TryNext(NetworkSession session, string host, int port,
            ReconnectContext context, Action onSuccess, Action<NetworkError> onFail)
        {
            if (_cancelled)
                return;

            if (_chainIndex >= _chain.Length)
            {
                onFail(new NetworkError(ErrorCode.AllStrategiesExhausted, "All reconnect strategies exhausted"));
                return;
            }

            var (strategy, maxAttempts) = _chain[_chainIndex];
            _currentAttempts++;

            strategy.Attempt(session, host, port, context,
                onSuccess: () =>
                {
                    if (_cancelled) return;
                    onSuccess();
                },
                onFail: reason =>
                {
                    if (_cancelled) return;

                    if (_currentAttempts < maxAttempts)
                    {
                        // 同一策略再试
                        TryNext(session, host, port, context, onSuccess, onFail);
                    }
                    else
                    {
                        // 降级到下一个策略
                        _chainIndex++;
                        _currentAttempts = 0;
                        TryNext(session, host, port, context, onSuccess, onFail);
                    }
                });
        }
    }
}
