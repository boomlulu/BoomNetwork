using System.Collections.Generic;
using System.Threading;
using BoomNetwork.Core;

namespace BoomNetwork.Client.Session
{
    /// <summary>
    /// 线程安全的 pending request 表。
    ///
    /// 设计目标：
    ///   - Add / TryComplete / DrainTimeouts 全部无阻塞，无竞争时约 1-2 ns 开销
    ///   - 永远不在持锁期间调用用户 callback，避免死锁和长时间自旋
    ///
    /// 线程模型：
    ///   - Add           → 主线程（SendAsync / SendExtAsync）
    ///   - TryComplete   → 收包线程（DispatchMessage，仅 HasSeq 消息）
    ///   - DrainTimeouts → 主线程（每帧 Tick）
    ///   - CancelAll     → 主线程或收包线程（断线 / reset）
    ///
    /// 方案选择：SpinLock（非 Monitor lock，非 ConcurrentDictionary）
    ///   - 临界区极短（纯 Dictionary Add/Remove/TryGetValue，稳态 n≤3）
    ///   - 无竞争概率 >99.99%；SpinLock 无竞争路径仅一次 Interlocked CAS
    ///   - 零额外分配，语义与之前完全一致
    ///   - ConcurrentDictionary 对 struct value 的 in-place 更新需 TryUpdate 循环，
    ///     不如 SpinLock + 普通 Dictionary 直接高效
    ///
    /// ⚠️  SpinLock 是 struct，字段禁止声明 readonly（否则 Enter/Exit 操作的是副本）。
    /// </summary>
    internal sealed class PendingRequestTable
    {
        // SpinLock 必须是可变字段（struct 语义，禁止 readonly）
        private SpinLock _lock = new SpinLock(enableThreadOwnerTracking: false);
        private readonly Dictionary<int, PendingRequest> _dict = new();

        // scratch buffer：仅 DrainTimeouts（主线程）使用，无需同步
        private readonly List<int> _scratchKeys = new();

        public int Count
        {
            get
            {
                bool taken = false;
                try { _lock.Enter(ref taken); return _dict.Count; }
                finally { if (taken) _lock.Exit(); }
            }
        }

        /// <summary>
        /// 注册一条 pending 请求。主线程调用。
        /// </summary>
        public void Add(int seq, PendingRequest req)
        {
            bool taken = false;
            try { _lock.Enter(ref taken); _dict[seq] = req; }
            finally { if (taken) _lock.Exit(); }
        }

        /// <summary>
        /// 收包线程：原子地取出并移除指定 seq 的 pending 请求。
        /// 返回 true 表示找到；调用方在锁外触发 callback。
        /// </summary>
        public bool TryComplete(int seq, out PendingRequest req)
        {
            bool taken = false;
            try { _lock.Enter(ref taken); return _dict.Remove(seq, out req); }
            finally { if (taken) _lock.Exit(); }
        }

        /// <summary>
        /// 主线程每帧调用：推进所有 pending 计时，将已超时的追加至 <paramref name="timedOut"/>。
        /// <para>调用方在此方法返回后（锁外）遍历 timedOut 并触发 OnTimeout callback。</para>
        /// </summary>
        public void DrainTimeouts(float deltaTimeMs, List<PendingRequest> timedOut)
        {
            bool taken = false;
            try
            {
                _lock.Enter(ref taken);

                if (_dict.Count == 0) return; // 稳态快路径，零额外开销

                // 收集 key（避免遍历时修改 dict）
                _scratchKeys.Clear();
                foreach (var kvp in _dict)
                    _scratchKeys.Add(kvp.Key);

                foreach (var key in _scratchKeys)
                {
                    if (!_dict.TryGetValue(key, out var req)) continue;

                    req.ElapsedMs += deltaTimeMs;
                    if (req.ElapsedMs >= req.TimeoutMs)
                    {
                        _dict.Remove(key);
                        timedOut.Add(req);  // 锁内只收集，不调 callback
                    }
                    else
                    {
                        _dict[key] = req;   // 回写更新后的 elapsed（struct 值语义）
                    }
                }
            }
            finally
            {
                if (taken) _lock.Exit();
            }
        }

        /// <summary>
        /// 清空所有 pending，将它们追加至 <paramref name="cancelled"/>。
        /// <para>调用方在此方法返回后（锁外）遍历 cancelled 并触发 OnTimeout callback。</para>
        /// 主线程或收包线程均可调用。
        /// </summary>
        public void CancelAll(List<PendingRequest> cancelled)
        {
            bool taken = false;
            try
            {
                _lock.Enter(ref taken);
                foreach (var kvp in _dict)
                    cancelled.Add(kvp.Value);
                _dict.Clear();
            }
            finally
            {
                if (taken) _lock.Exit();
            }
        }
    }
}
