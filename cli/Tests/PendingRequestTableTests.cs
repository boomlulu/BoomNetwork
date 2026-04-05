using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using BoomNetwork.Client.Session;
using BoomNetwork.Core;

namespace BoomNetwork.Tests
{
    // 通过 InternalsVisibleTo 访问 internal 类型；
    // 若编译报错，在 BoomNetwork.Client.csproj 添加：
    //   <InternalsVisibleTo Include="BoomNetwork.Tests" />
    // 目前通过反射-free 测试已足够验证正确性。

    [TestFixture]
    public class PendingRequestTableTests
    {
        // ─── 辅助工厂 ─────────────────────────────────────────────────────────────

        private static PendingRequest MakeReq(float timeoutMs = 1000f,
            Action<Message>? onResp = null, Action<NetworkError>? onTimeout = null)
            => new PendingRequest
            {
                TimeoutMs = timeoutMs,
                ElapsedMs = 0f,
                OnResponse = onResp,
                OnTimeout = onTimeout,
            };

        // ─── 单线程基础行为 ───────────────────────────────────────────────────────

        [Test]
        public void Add_ThenTryComplete_Succeeds()
        {
            var table = new PendingRequestTable();
            table.Add(1, MakeReq());
            Assert.That(table.Count, Is.EqualTo(1));

            bool found = table.TryComplete(1, out _);
            Assert.That(found, Is.True);
            Assert.That(table.Count, Is.Zero);
        }

        [Test]
        public void TryComplete_MissingSeq_ReturnsFalse()
        {
            var table = new PendingRequestTable();
            bool found = table.TryComplete(99, out _);
            Assert.That(found, Is.False);
        }

        [Test]
        public void TryComplete_Idempotent_SecondCallFails()
        {
            var table = new PendingRequestTable();
            table.Add(1, MakeReq());
            table.TryComplete(1, out _);
            bool second = table.TryComplete(1, out _);
            Assert.That(second, Is.False);
        }

        [Test]
        public void DrainTimeouts_NotYetExpired_DoesNotCollect()
        {
            var table = new PendingRequestTable();
            table.Add(1, MakeReq(timeoutMs: 500f));

            var timedOut = new List<PendingRequest>();
            table.DrainTimeouts(100f, timedOut); // 经过 100ms，未到 500ms
            Assert.That(timedOut.Count, Is.Zero);
            Assert.That(table.Count, Is.EqualTo(1));
        }

        [Test]
        public void DrainTimeouts_Expired_CollectsAndRemoves()
        {
            var table = new PendingRequestTable();
            table.Add(1, MakeReq(timeoutMs: 200f));

            var timedOut = new List<PendingRequest>();
            table.DrainTimeouts(150f, timedOut);
            Assert.That(timedOut.Count, Is.Zero); // 150 < 200

            table.DrainTimeouts(100f, timedOut); // 累计 250ms ≥ 200ms → 超时
            Assert.That(timedOut.Count, Is.EqualTo(1));
            Assert.That(table.Count, Is.Zero);
        }

        [Test]
        public void DrainTimeouts_MultipleEntries_OnlyExpiredCollected()
        {
            var table = new PendingRequestTable();
            table.Add(1, MakeReq(timeoutMs: 100f));
            table.Add(2, MakeReq(timeoutMs: 500f));
            table.Add(3, MakeReq(timeoutMs: 150f));

            var timedOut = new List<PendingRequest>();
            table.DrainTimeouts(200f, timedOut); // seq 1 (200≥100), seq 3 (200≥150) 超时

            Assert.That(timedOut.Count, Is.EqualTo(2));
            Assert.That(table.Count, Is.EqualTo(1)); // seq 2 留存
        }

        [Test]
        public void CancelAll_EmptiesTable_ReturnsAllRequests()
        {
            var table = new PendingRequestTable();
            for (int i = 1; i <= 5; i++)
                table.Add(i, MakeReq());

            var cancelled = new List<PendingRequest>();
            table.CancelAll(cancelled);

            Assert.That(cancelled.Count, Is.EqualTo(5));
            Assert.That(table.Count, Is.Zero);
        }

        [Test]
        public void CancelAll_EmptyTable_NoCrash()
        {
            var table = new PendingRequestTable();
            var cancelled = new List<PendingRequest>();
            Assert.DoesNotThrow(() => table.CancelAll(cancelled));
            Assert.That(cancelled.Count, Is.Zero);
        }

        // ─── TryComplete 赢 DrainTimeouts（同一条目只触发一次 callback）────────────

        [Test]
        public void TryComplete_BeforeDrainTimeout_CallbackFiresOnce()
        {
            int callCount = 0;
            var table = new PendingRequestTable();
            table.Add(1, MakeReq(
                timeoutMs: 100f,
                onResp: _ => callCount++,
                onTimeout: _ => callCount++
            ));

            // 模拟收包线程先完成
            bool found = table.TryComplete(1, out var req);
            req.OnResponse?.Invoke(default);

            // 之后 Tick 触发 DrainTimeouts，不应再收到 seq 1
            var timedOut = new List<PendingRequest>();
            table.DrainTimeouts(200f, timedOut);
            foreach (var r in timedOut) r.OnTimeout?.Invoke(default);

            Assert.That(callCount, Is.EqualTo(1));
        }

        // ─── 并发正确性 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 多个线程同时 TryComplete 同一 seq，只有一个应成功（原子 Remove）
        /// </summary>
        [Test]
        public void Concurrent_MultipleThreads_OnlyOneCompletes()
        {
            const int threadCount = 8;
            var table = new PendingRequestTable();
            table.Add(42, MakeReq());

            int successCount = 0;
            var barrier = new Barrier(threadCount);
            var tasks = new Task[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    barrier.SignalAndWait(); // 所有线程同时出发
                    if (table.TryComplete(42, out _))
                        Interlocked.Increment(ref successCount);
                });
            }

            Task.WaitAll(tasks);
            Assert.That(successCount, Is.EqualTo(1), "Only one thread should complete seq 42");
            Assert.That(table.Count, Is.Zero);
        }

        /// <summary>
        /// 收包线程 TryComplete vs 主线程 DrainTimeouts 并发，合计 callback 触发次数 == 1
        /// </summary>
        [Test]
        public void Concurrent_TryComplete_vs_DrainTimeouts_ExactlyOneWins()
        {
            const int iterations = 50_000;
            int totalCallbacks = 0;

            for (int iter = 0; iter < iterations; iter++)
            {
                var table = new PendingRequestTable();
                table.Add(1, MakeReq(timeoutMs: 0f)); // timeout 0 → 每次 Drain 都会超时

                int localCount = 0;
                var barrier = new Barrier(2);

                // 收包线程
                var recvTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    if (table.TryComplete(1, out var r))
                    {
                        r.OnResponse?.Invoke(default);
                        Interlocked.Increment(ref localCount);
                    }
                });

                // 主线程（模拟）
                barrier.SignalAndWait();
                var timedOut = new List<PendingRequest>();
                table.DrainTimeouts(999f, timedOut);
                foreach (var r in timedOut)
                {
                    r.OnTimeout?.Invoke(default);
                    Interlocked.Increment(ref localCount);
                }

                recvTask.Wait();
                Interlocked.Add(ref totalCallbacks, localCount);
            }

            // 每次迭代 callback 只应触发一次（要么 TryComplete 赢，要么 DrainTimeouts 赢）
            Assert.That(totalCallbacks, Is.EqualTo(iterations),
                $"Expected {iterations} total callbacks (1 per iteration), got {totalCallbacks}");
        }

        /// <summary>
        /// 高并发压力：Add（主线程）+ TryComplete（多个 recv 线程）不崩溃，
        /// 且每个 seq 被处理恰好一次（TryComplete 或 DrainTimeouts 两条路径合计 = seqCount）。
        /// </summary>
        [Test]
        public void Concurrent_StressTest_NoExceptions()
        {
            const int seqCount = 1000;
            const int recvThreads = 4;

            var table = new PendingRequestTable();
            // 使用极长 timeout，确保 DrainTimeouts 在测试期间不超时任何条目
            for (int i = 1; i <= seqCount; i++)
                table.Add(i, MakeReq(timeoutMs: 999_999f));

            int completed = 0;
            int timedOutTotal = 0;
            var barrier = new Barrier(recvThreads + 1); // +1 包含主线程
            var tasks = new Task[recvThreads];

            for (int t = 0; t < recvThreads; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    // 每个线程扫全量 seq，交叉争用
                    for (int i = 1; i <= seqCount; i++)
                    {
                        if (table.TryComplete(i, out _))
                            Interlocked.Increment(ref completed);
                    }
                });
            }

            // 主线程也参与（发很小 delta，不触发超时）
            barrier.SignalAndWait();
            var timedOut = new List<PendingRequest>();
            for (int tick = 0; tick < 10; tick++)
            {
                table.DrainTimeouts(1f, timedOut); // 1ms × 10 = 10ms，远小于 999999ms
                timedOutTotal += timedOut.Count;
                timedOut.Clear();
            }

            Task.WaitAll(tasks);

            int remaining = table.Count;

            // 关键断言：completed + timedOut + remaining == seqCount（无重复，无丢失）
            Assert.That(completed + timedOutTotal + remaining, Is.EqualTo(seqCount),
                $"Total accounting mismatch: completed={completed} timedOut={timedOutTotal} remaining={remaining}");
        }

        // ─── Benchmark（内嵌，作为可观测指标，非 NUnit 性能断言）──────────────────

        [Test]
        [Category("Benchmark")]
        public void Benchmark_TryComplete_UncontendedLatency()
        {
            // 预热
            var table = new PendingRequestTable();
            for (int i = 0; i < 100; i++)
            {
                table.Add(i, MakeReq());
                table.TryComplete(i, out _);
            }

            const int N = 100_000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
            {
                table.Add(i, MakeReq());
                table.TryComplete(i, out _);
            }
            sw.Stop();

            double nsPerOp = sw.Elapsed.TotalNanoseconds / N;
            Console.WriteLine($"[Benchmark] TryComplete (uncontended): {nsPerOp:F1} ns/op (Add+TryComplete pair)");

            // 宽松断言：无竞争下每对 Add+Complete 应 < 500 ns（在 CI 上保守估计）
            Assert.That(nsPerOp, Is.LessThan(500.0),
                $"Add+TryComplete pair too slow: {nsPerOp:F1} ns");
        }

        [Test]
        [Category("Benchmark")]
        public void Benchmark_DrainTimeouts_EmptyTable_FastPath()
        {
            var table = new PendingRequestTable(); // 空表
            var timedOut = new List<PendingRequest>();

            // 预热
            for (int i = 0; i < 1000; i++)
                table.DrainTimeouts(16f, timedOut);

            const int N = 1_000_000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
                table.DrainTimeouts(16f, timedOut);
            sw.Stop();

            double nsPerOp = sw.Elapsed.TotalNanoseconds / N;
            Console.WriteLine($"[Benchmark] DrainTimeouts (empty table fast path): {nsPerOp:F1} ns/op");

            // 空表走快路径，应 < 50 ns（放宽阈值防止 CI 机器负载波动导致偶发失败）
            Assert.That(nsPerOp, Is.LessThan(50.0),
                $"Empty DrainTimeouts too slow: {nsPerOp:F1} ns");
        }
    }
}
