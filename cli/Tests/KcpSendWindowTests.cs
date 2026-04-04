#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.Collections.Generic;
using BoomNetwork.Client.Transport;
using BoomNetwork.Core;
using BoomNetwork.Core.Transport;
using KcpProject;
using NUnit.Framework;

namespace BoomNetwork.Tests
{
    // C2 TDD 测试：KcpClientTransport.Send KCP 发送窗口已满时的静默丢包 bug
    //
    // ┌─────────────────────────────────────────────────────────────────────┐
    // │  BUG (C2): UDPSession.Send() 在 KCP 发送窗口满时返回 0，但         │
    // │  KcpClientTransport.Send() 忽略返回值，导致游戏输入静默丢失。        │
    // │                                                                     │
    // │  TDD 流程:                                                          │
    // │  Phase 1 — BUG 验证（[Explicit]，修复后不再 FAIL，但保留记录）       │
    // │    TestKcpSend_BugVerification_WindowFullSilentlyDrops              │
    // │      修复前: PASS（证明 bug：发送窗口满时 OnError 未触发）            │
    // │      修复后: FAIL（bug 已修复，不再静默丢失）                        │
    // │      → 因此标记为 [Explicit]，防止修复后 CI 失败                    │
    // │                                                                     │
    // │  Phase 2 — 修复验证                                                 │
    // │    TestKcpSend_FixVerification_WindowFullFiresOnError               │
    // │      修复前: FAIL（OnError 未触发）                                  │
    // │      修复后: PASS（OnError 正确触发 SendFailed）                     │
    // └─────────────────────────────────────────────────────────────────────┘

    /// <summary>
    /// MockUDPSession：IUDPSession 的测试替身。
    /// 通过 SendReturnValue 控制 Send 返回值，模拟 KCP 窗口满场景（返回 0）。
    /// </summary>
    internal class MockUDPSession : IUDPSession
    {
        public bool IsConnected { get; set; } = true;
        public bool AckNoDelay { get; set; }
        public bool WriteDelay { get; set; }

        /// <summary>控制 Send 返回值：0 模拟窗口满，>0 模拟正常发送</summary>
        public int SendReturnValue { get; set; } = 64;

        public List<(byte[] data, int index, int length)> SentPackets { get; } = new();

        public void Connect(string host, int port) { }
        public void Close() { IsConnected = false; }
        public void Update() { }

        public int Send(byte[] data, int index, int length)
        {
            if (SendReturnValue > 0)
                SentPackets.Add((data, index, length));
            return SendReturnValue;
        }

        public int Recv(byte[] data, int index, int length) => 0;
    }

    [TestFixture]
    public class KcpSendWindowTests
    {
        // ══════════════════════════════════════════════════════════════
        // Phase 1: BUG 验证（标记为 [Explicit] 防止修复后 CI 失败）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 证明 C2 bug 存在：KCP 发送窗口满时（Send 返回 0），
        /// 修复前 KcpClientTransport 静默忽略，OnError 从不触发。
        ///
        /// 标记为 [Explicit]：
        ///   修复前: 此测试 PASS（bug 已被证明）
        ///   修复后: 此测试 FAIL（OnError 被正确触发了，与此测试断言相反）
        ///   → Explicit 防止修复后 CI 拉红。人工运行可验证 bug 历史行为。
        /// </summary>
        [Test, Explicit, Category("BugVerification")]
        public void TestKcpSend_BugVerification_WindowFullSilentlyDrops()
        {
            var mock = new MockUDPSession { SendReturnValue = 0 }; // 模拟窗口满
            var transport = new KcpClientTransport(mock);

            NetworkError? capturedError = null;
            transport.OnError += err => capturedError = err;

            var data = new byte[64];
            transport.Send(data, 0, data.Length);

            // 修复前：OnError 从未触发 → capturedError == null → PASS（bug 已证明）
            // 修复后：OnError 触发 → capturedError != null → FAIL（此测试不再适用）
            Assert.IsNull(capturedError,
                "BUG VERIFIED: KcpClientTransport.Send 在窗口满时静默忽略（OnError 未触发）。" +
                "此断言失败表示 bug 已被修复。");
        }

        // ══════════════════════════════════════════════════════════════
        // Phase 2: 修复验证
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 证明修复有效：KCP 发送窗口满时（Send 返回 0），
        /// 修复后 KcpClientTransport 必须触发 OnError(SendFailed)。
        ///
        ///   修复前: FAIL（OnError 未触发）
        ///   修复后: PASS
        /// </summary>
        [Test]
        public void TestKcpSend_FixVerification_WindowFullFiresOnError()
        {
            var mock = new MockUDPSession { SendReturnValue = 0 }; // 模拟窗口满
            var transport = new KcpClientTransport(mock);

            NetworkError? capturedError = null;
            transport.OnError += err => capturedError = err;

            var data = new byte[64];
            transport.Send(data, 0, data.Length);

            Assert.IsNotNull(capturedError,
                "FIX VERIFICATION FAILED: Send 窗口满时应触发 OnError，但未触发。");
            Assert.AreEqual(ErrorCode.SendFailed, capturedError!.Value.Code,
                "FIX VERIFICATION: OnError 应携带 SendFailed 错误码。");

            TestContext.WriteLine($"FIX VERIFIED ✓ Send 窗口满时触发 OnError: {capturedError.Value.Message}");
        }

        /// <summary>
        /// 修复后正常发送（窗口有空间，Send 返回 length）不触发 OnError。
        /// 验证修复不引入回归。
        /// </summary>
        [Test]
        public void TestKcpSend_FixVerification_NormalSendNoError()
        {
            var mock = new MockUDPSession { SendReturnValue = 64 }; // 模拟正常发送
            var transport = new KcpClientTransport(mock);

            NetworkError? capturedError = null;
            transport.OnError += err => capturedError = err;

            var data = new byte[64];
            transport.Send(data, 0, data.Length);

            Assert.IsNull(capturedError,
                "FIX VERIFICATION: 正常发送不应触发 OnError。");
            Assert.AreEqual(1, mock.SentPackets.Count,
                "FIX VERIFICATION: 正常发送应将数据传递到 IUDPSession.Send。");

            TestContext.WriteLine("FIX VERIFIED ✓ 正常发送不触发 OnError，数据成功传递。");
        }

        /// <summary>
        /// 验证 OnError 不会在连接状态为 Disconnected 时触发（Send 提前返回）。
        /// </summary>
        [Test]
        public void TestKcpSend_FixVerification_DisconnectedStateSkipsSend()
        {
            // 注意：不使用注入构造函数（那会强制 Connected 状态）
            // 使用默认构造函数，State = Disconnected
            var transport = new KcpClientTransport();

            NetworkError? capturedError = null;
            transport.OnError += err => capturedError = err;

            var data = new byte[64];
            transport.Send(data, 0, data.Length); // 应提前返回，不调用 session

            Assert.IsNull(capturedError,
                "Disconnected 状态下 Send 应直接返回，不触发任何事件。");

            TestContext.WriteLine("FIX VERIFIED ✓ Disconnected 状态 Send 提前返回，无副作用。");
        }

        /// <summary>
        /// 验证 MockUDPSession 是 IUDPSession 的正确实现（编译层验证）。
        /// 同时验证注入构造函数工作正常。
        /// </summary>
        [Test]
        public void TestKcpTransport_MockInjection_StateIsConnected()
        {
            var mock = new MockUDPSession();
            var transport = new KcpClientTransport(mock);

            Assert.AreEqual(TransportState.Connected, transport.State,
                "注入构造函数应将 State 设为 Connected。");

            TestContext.WriteLine("FIX VERIFIED ✓ MockUDPSession 注入后 State=Connected。");
        }

        // ══════════════════════════════════════════════════════════════
        // Benchmarks（人工运行）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 基准测试：正常发送路径（有额外的返回值检查开销）。
        /// 人工运行确认 Send 检查返回值对正常路径的影响可忽略不计。
        /// N=100_000 次，期望 < 5ms。
        /// </summary>
        [Test, Explicit, Category("Benchmark")]
        public void BenchmarkKcpSend_NormalPath_ReturnValueCheck()
        {
            var mock = new MockUDPSession { SendReturnValue = 64 };
            var transport = new KcpClientTransport(mock);
            var data = new byte[64];

            const int N = 100_000;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
            {
                // 清理已发数据避免内存压力干扰
                if (i % 1000 == 0) mock.SentPackets.Clear();
                transport.Send(data, 0, data.Length);
            }
            sw.Stop();

            TestContext.WriteLine($"BENCHMARK: {N} × Send (window-ok) = {sw.ElapsedMilliseconds}ms " +
                                  $"({sw.Elapsed.TotalMicroseconds / N:F2}μs/op)");
        }

        /// <summary>
        /// 基准测试：窗口满路径（OnError 触发开销）。
        /// </summary>
        [Test, Explicit, Category("Benchmark")]
        public void BenchmarkKcpSend_WindowFull_OnErrorOverhead()
        {
            var mock = new MockUDPSession { SendReturnValue = 0 };
            var transport = new KcpClientTransport(mock);

            int errorCount = 0;
            transport.OnError += _ => errorCount++;

            var data = new byte[64];
            const int N = 10_000;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
                transport.Send(data, 0, data.Length);
            sw.Stop();

            Assert.AreEqual(N, errorCount, "每次窗口满都应触发 OnError。");
            TestContext.WriteLine($"BENCHMARK: {N} × Send (window-full) = {sw.ElapsedMilliseconds}ms " +
                                  $"({sw.Elapsed.TotalMicroseconds / N:F2}μs/op, errorCount={errorCount})");
        }
    }
}
#endif
