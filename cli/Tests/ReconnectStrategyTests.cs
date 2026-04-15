using System;
using NUnit.Framework;
using BoomNetwork.Core;
using BoomNetwork.Client.Connection;
using BoomNetwork.Client.Session;

namespace BoomNetwork.Tests
{
    /// <summary>
    /// 重连策略单元测试
    ///
    /// 验证 ReconnectState / ReconnectOutcome 的所有权分离：
    ///   - State 由 CM 持有，跨 Attempt 同步帧号
    ///   - Outcome 为 readonly struct，策略通过 onSuccess(outcome) 回传
    ///
    /// 因 QuickReconnectStrategy / SnapshotReconnectStrategy 依赖真实 NetworkSession（需 TCP），
    /// 使用 FakeStrategy stub 来验证 Composite 编排逻辑和 Outcome 类型正确性。
    /// </summary>
    [TestFixture]
    public class ReconnectStrategyTests
    {
        // ─── 桩策略 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 直接成功的桩策略，立即调用 onSuccess 并回传预设 outcome。
        /// </summary>
        private class AlwaysSucceedStrategy : IReconnectStrategy
        {
            public string Name => "AlwaysSucceed";
            public ReconnectOutcome OutcomeToReturn { get; }

            public AlwaysSucceedStrategy(ReconnectOutcome outcome)
            {
                OutcomeToReturn = outcome;
            }

            public void Attempt(NetworkSession session, string host, int port,
                ReconnectState state, Action<ReconnectOutcome> onSuccess, Action<NetworkError> onFail)
            {
                onSuccess(OutcomeToReturn);
            }

            public void Cancel() { }
        }

        /// <summary>
        /// 直接失败的桩策略。
        /// </summary>
        private class AlwaysFailStrategy : IReconnectStrategy
        {
            public string Name => "AlwaysFail";
            private readonly string _reason;

            public AlwaysFailStrategy(string reason = "fail")
            {
                _reason = reason;
            }

            public void Attempt(NetworkSession session, string host, int port,
                ReconnectState state, Action<ReconnectOutcome> onSuccess, Action<NetworkError> onFail)
            {
                onFail(new NetworkError(ErrorCode.ReconnectFailed, _reason));
            }

            public void Cancel() { }
        }

        // ─── 测试用例 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// TC1: QuickReconnect 成功时 outcome.IsSnapshotRestore == false
        /// 通过 AlwaysSucceed stub 模拟 QuickReconnect 语义（快速重连不携带快照）。
        /// </summary>
        [Test]
        public void QuickReconnect_OnSuccess_OutcomeIsNotSnapshot()
        {
            var expectedOutcome = new ReconnectOutcome(serverFrameNumber: 100, isSnapshotRestore: false);
            var strategy = new AlwaysSucceedStrategy(expectedOutcome);
            var state = new ReconnectState { PlayerId = 1, LastFrameNumber = 50 };

            ReconnectOutcome? received = null;
            strategy.Attempt(null!, "host", 0, state,
                onSuccess: o => received = o,
                onFail: _ => Assert.Fail("Should not fail"));

            Assert.That(received, Is.Not.Null);
            Assert.That(received!.Value.IsSnapshotRestore, Is.False,
                "Quick reconnect must not set IsSnapshotRestore");
            Assert.That(received.Value.ServerFrameNumber, Is.EqualTo(100u));
        }

        /// <summary>
        /// TC2: SnapshotReconnect 成功时 outcome.IsSnapshotRestore == true，FrameNumber 正确传递
        /// 通过 AlwaysSucceed stub 模拟 SnapshotReconnect 语义（携带快照数据）。
        /// </summary>
        [Test]
        public void SnapshotReconnect_OnSuccess_OutcomeIsSnapshot()
        {
            var snapshotData = new byte[] { 1, 2, 3, 4 };
            var expectedOutcome = new ReconnectOutcome(
                serverFrameNumber: 500,
                isSnapshotRestore: true,
                snapshotFrame: 480,
                snapshotData: snapshotData);

            var strategy = new AlwaysSucceedStrategy(expectedOutcome);
            var state = new ReconnectState { PlayerId = 2, LastFrameNumber = 0 };

            ReconnectOutcome? received = null;
            strategy.Attempt(null!, "host", 0, state,
                onSuccess: o => received = o,
                onFail: _ => Assert.Fail("Should not fail"));

            Assert.That(received, Is.Not.Null);
            Assert.That(received!.Value.IsSnapshotRestore, Is.True,
                "Snapshot reconnect must set IsSnapshotRestore");
            Assert.That(received.Value.SnapshotFrame, Is.EqualTo(480u),
                "SnapshotFrame must round-trip through outcome");
            Assert.That(received.Value.SnapshotData, Is.SameAs(snapshotData),
                "SnapshotData reference must be preserved");
        }

        /// <summary>
        /// TC3: ReconnectState.LastFrameNumber 可被 CM 在重连期间更新（模拟 UpdateFrameNumber 行为）
        /// 验证 State 是可变的活状态，与 Outcome 的 readonly 设计形成对比。
        /// </summary>
        [Test]
        public void ReconnectState_LastFrameNumber_CanBeUpdatedByOwner()
        {
            var state = new ReconnectState { PlayerId = 5, LastFrameNumber = 100 };

            // CM 在两次 Attempt 之间收帧，同步最新帧号
            state.LastFrameNumber = 200;
            Assert.That(state.LastFrameNumber, Is.EqualTo(200u),
                "CM must be able to update LastFrameNumber between attempts");

            state.LastFrameNumber = 300;
            Assert.That(state.LastFrameNumber, Is.EqualTo(300u),
                "Frame number update must be visible immediately");

            // PlayerId 不变（整个重连过程 PlayerId 固定）
            Assert.That(state.PlayerId, Is.EqualTo(5));
        }

        /// <summary>
        /// TC4: CompositeReconnectStrategy — Quick 失败后降级到 Snapshot
        /// Quick(maxAttempts=1) 失败 → Composite 切换到 Snapshot，最终 outcome.IsSnapshotRestore == true
        /// </summary>
        [Test]
        public void CompositeReconnect_FallsThrough_ToSnapshot()
        {
            var snapshotOutcome = new ReconnectOutcome(
                serverFrameNumber: 300,
                isSnapshotRestore: true,
                snapshotFrame: 280);

            var composite = new CompositeReconnectStrategy(
                (new AlwaysFailStrategy("buffer stale"), 1),    // Quick: 最多1次，失败后降级
                (new AlwaysSucceedStrategy(snapshotOutcome), 1) // Snapshot: 成功
            );

            var state = new ReconnectState { PlayerId = 3, LastFrameNumber = 150 };

            ReconnectOutcome? received = null;
            NetworkError? receivedError = null;

            composite.Attempt(null!, "host", 0, state,
                onSuccess: o => received = o,
                onFail: e => receivedError = e);

            Assert.That(receivedError, Is.Null, "Should not reach terminal failure");
            Assert.That(received, Is.Not.Null, "Composite must succeed via Snapshot strategy");
            Assert.That(received!.Value.IsSnapshotRestore, Is.True,
                "Fall-through to snapshot must produce IsSnapshotRestore=true outcome");
            Assert.That(received.Value.ServerFrameNumber, Is.EqualTo(300u));
        }
    }
}
