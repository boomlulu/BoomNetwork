using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using BoomNetwork.Core;
using BoomNetwork.Core.Codec;
using BoomNetwork.Core.Framing;
using BoomNetwork.Client.Transport;
using BoomNetwork.Core.Transport;

namespace BoomNetwork.KcpTest
{
    class Program
    {
        static int passed = 0;
        static int failed = 0;

        static void Main(string[] args)
        {
            Console.WriteLine("==========================================");
            Console.WriteLine("  KCP Transport Test Suite");
            Console.WriteLine("==========================================\n");

            // 需要 Go KCP echo server 在 :9000 运行
            // 用 test_kcp.sh 启动

            Test1_BasicSendRecv();
            Test2_SmallPacket();
            Test3_LargePacket();
            Test4_HighFrequencySend();
            Test5_StickyPackets();
            Test6_MixedSizes();

            Console.WriteLine($"\n==========================================");
            Console.WriteLine($"  KCP Test Results: {passed} passed, {failed} failed");
            Console.WriteLine($"==========================================");

            if (failed > 0) Environment.Exit(1);
        }

        static void Test1_BasicSendRecv()
        {
            Console.WriteLine("--- Test 1: Basic Send/Recv ---");
            using var ctx = new TestContext();
            if (!ctx.Connect()) return;

            ctx.SendEcho("Hello KCP!");
            ctx.TickFor(500);

            Report("Basic echo", ctx.RecvCount == 1 && ctx.LastRecvText == "Hello KCP!");
        }

        static void Test2_SmallPacket()
        {
            Console.WriteLine("\n--- Test 2: Small Packet (1 byte) ---");
            using var ctx = new TestContext();
            if (!ctx.Connect()) return;

            ctx.SendEcho("X");
            ctx.TickFor(500);

            Report("1-byte payload", ctx.RecvCount == 1 && ctx.LastRecvText == "X");
        }

        static void Test3_LargePacket()
        {
            Console.WriteLine("\n--- Test 3: Large Packet (32KB) ---");
            using var ctx = new TestContext();
            if (!ctx.Connect()) return;

            var largeData = new string('A', 32 * 1024);
            ctx.SendEcho(largeData);
            ctx.TickFor(2000); // 大包需要更多时间

            Report($"32KB payload (recv {ctx.LastRecvLen} bytes)",
                ctx.RecvCount == 1 && ctx.LastRecvLen == 32 * 1024);
        }

        static void Test4_HighFrequencySend()
        {
            Console.WriteLine("\n--- Test 4: High Frequency (100 messages, no wait) ---");
            using var ctx = new TestContext();
            if (!ctx.Connect()) return;

            for (int i = 0; i < 100; i++)
            {
                ctx.SendEcho($"msg-{i}");
                if (i % 20 == 19) ctx.TickFor(100); // 每 20 条 tick 一次让 KCP flush
            }
            ctx.TickFor(5000);

            Report($"100 messages (recv {ctx.RecvCount}/100)", ctx.RecvCount == 100);
        }

        static void Test5_StickyPackets()
        {
            Console.WriteLine("\n--- Test 5: Sticky Packets (粘包) ---");
            // KCP 是流模式，多条消息可能粘在一起到达
            // 验证 Framing 层能正确拆分
            using var ctx = new TestContext();
            if (!ctx.Connect()) return;

            // 快速连发 50 条小消息
            for (int i = 0; i < 50; i++)
            {
                ctx.SendEcho($"s{i}");
                if (i % 10 == 9) ctx.TickFor(100);
            }
            ctx.TickFor(5000);

            // 验证每条都正确接收
            Report($"50 sticky messages (recv {ctx.RecvCount}/50)", ctx.RecvCount == 50);

            // 验证最后一条内容正确
            Report("Last message content correct", ctx.LastRecvText == "s49");
        }

        static void Test6_MixedSizes()
        {
            Console.WriteLine("\n--- Test 6: Mixed Sizes ---");
            using var ctx = new TestContext();
            if (!ctx.Connect()) return;

            // 交替发大小不同的消息
            ctx.SendEcho("tiny");                           // 4 bytes
            ctx.SendEcho(new string('B', 1000));            // 1KB
            ctx.SendEcho("small");                          // 5 bytes
            ctx.SendEcho(new string('C', 8000));            // 8KB
            ctx.SendEcho("end");                            // 3 bytes

            ctx.TickFor(2000);

            Report($"Mixed sizes (recv {ctx.RecvCount}/5)", ctx.RecvCount == 5);
        }

        static void Report(string name, bool ok)
        {
            if (ok) { Console.WriteLine($"  PASS: {name}"); passed++; }
            else { Console.WriteLine($"  FAIL: {name}"); failed++; }
        }

        /// <summary>
        /// 测试上下文 — 封装 Transport + Framing + 统计
        /// </summary>
        class TestContext : IDisposable
        {
            public KcpClientTransport Transport;
            public LengthPrefixFraming Framing;
            public int RecvCount;
            public string LastRecvText = "";
            public int LastRecvLen;

            private int _seq;

            public TestContext()
            {
                Transport = new KcpClientTransport();
                Framing = new LengthPrefixFraming();
                RecvCount = 0;

                Transport.OnData += (data, offset, length) =>
                {
                    Framing.Feed(data, offset, length);
                    while (Framing.TryDequeueFrame(out var frame))
                    {
                        var msg = MessageCodec.Decode(frame.Span);
                        frame.Dispose();
                        RecvCount++;
                        LastRecvLen = msg.DataLength;
                        if (msg.DataLength > 0 && msg.DataLength <= 1024)
                            LastRecvText = Encoding.UTF8.GetString(msg.DataSpan);
                        else if (msg.DataLength > 1024)
                            LastRecvText = $"[{msg.DataLength} bytes]";
                    }
                };

                Transport.OnError += err => Console.WriteLine($"  [Error] {err}");
            }

            public bool Connect()
            {
                int port = int.TryParse(Environment.GetEnvironmentVariable("BOOM_PORT"), out var p) ? p : 9000;
                Transport.Connect("127.0.0.1", port);
                TickFor(500);
                if (Transport.State != TransportState.Connected)
                {
                    Report("Connect", false);
                    return false;
                }
                return true;
            }

            public void SendEcho(string text)
            {
                var data = Encoding.UTF8.GetBytes(text);
                var msg = new Message
                {
                    Cmd = 1, // Echo
                    HasSeq = true,
                    Seq = ++_seq,
                    Data = data,
                    DataLength = data.Length,
                };
                var buf = new byte[MessageCodec.EncodedSize(msg)];
                MessageCodec.Encode(msg, buf);
                Transport.Send(buf, 0, buf.Length);
            }

            public void TickFor(int ms)
            {
                int elapsed = 0;
                while (elapsed < ms)
                {
                    Transport.Tick();
                    Thread.Sleep(10);
                    elapsed += 10;
                }
            }

            public void Dispose()
            {
                Transport.Disconnect();
                Framing.Reset();
            }
        }
    }
}
