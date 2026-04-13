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
        public event Action<string>? OnLog;

        private readonly (IReconnectStrategy strategy, int maxAttempts)[] _chain;
        private int _chainIndex;
        private int _currentAttempts;
        private bool _cancelled;

        public CompositeReconnectStrategy(params (IReconnectStrategy strategy, int maxAttempts)[] chain)
        {
            _chain = chain;
        }

        /// <summary>
        /// 设置快速重连策略的超时时间（由服务器下发）
        /// </summary>
        public void SetQuickReconnectTimeout(int timeoutMs)
        {
            foreach (var (strategy, _) in _chain)
            {
                if (strategy is QuickReconnectStrategy quick)
                    quick.TimeoutMs = timeoutMs;
            }
        }

        public void Attempt(NetworkSession session, string host, int port,
            ReconnectState state, Action<ReconnectOutcome> onSuccess, Action<NetworkError> onFail)
        {
            _cancelled = false;
            _chainIndex = 0;
            _currentAttempts = 0;

            TryNext(session, host, port, state, onSuccess, onFail);
        }

        public void Cancel()
        {
            _cancelled = true;
            if (_chainIndex < _chain.Length)
                _chain[_chainIndex].strategy.Cancel();
        }

        private void TryNext(NetworkSession session, string host, int port,
            ReconnectState state, Action<ReconnectOutcome> onSuccess, Action<NetworkError> onFail)
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

            OnLog?.Invoke($"[Composite] chain={_chainIndex}/{_chain.Length} attempt={_currentAttempts}/{maxAttempts} strategy={strategy.GetType().Name}");

            strategy.Attempt(session, host, port, state,
                onSuccess: outcome =>
                {
                    if (_cancelled) return;
                    onSuccess(outcome);
                },
                onFail: reason =>
                {
                    if (_cancelled) return;

                    if (_currentAttempts < maxAttempts)
                    {
                        // 同一策略再试
                        TryNext(session, host, port, state, onSuccess, onFail);
                    }
                    else
                    {
                        // 降级到下一个策略
                        _chainIndex++;
                        _currentAttempts = 0;
                        TryNext(session, host, port, state, onSuccess, onFail);
                    }
                });
        }
    }
}
