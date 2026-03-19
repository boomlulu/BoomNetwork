using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using BoomNetwork.Core;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Client.Transport;
using BoomNetwork.Client.Session;
using BoomNetwork.Client.Connection;
using BoomNetwork.Client.FrameSync;

namespace BoomNetwork.StressTest
{
    class Program
    {
        // 统计
        static long totalConnected;
        static long totalConnectFailed;
        static long totalFramesRecv;
        static long totalInputsSent;
        static long totalBound;

        static void Main(string[] args)
        {
            string host = Environment.GetEnvironmentVariable("BOOM_HOST") ?? "127.0.0.1";
            int port = int.TryParse(Environment.GetEnvironmentVariable("BOOM_PORT"), out var p) ? p : 9000;
            int clientCount = int.TryParse(Environment.GetEnvironmentVariable("BOOM_CLIENTS"), out var c) ? c : 100;
            int durationSec = int.TryParse(Environment.GetEnvironmentVariable("BOOM_DURATION"), out var d) ? d : 10;
            int inputSize = 32;

            Console.WriteLine("==========================================");
            Console.WriteLine("  BoomNetwork C# Stress Test");
            Console.WriteLine($"  {clientCount} clients → {host}:{port}");
            Console.WriteLine($"  Duration: {durationSec}s, Input: {inputSize}B");
            Console.WriteLine("==========================================\n");

            var clients = new FrameSyncClient[clientCount];
            var stopEvent = new ManualResetEventSlim(false);

            // 创建所有客户端
            for (int i = 0; i < clientCount; i++)
            {
                int idx = i;
                var transport = new TcpClientTransport();
                var session = new NetworkSession(transport);
                var strategy = new CompositeReconnectStrategy(
                    (new QuickReconnectStrategy { TimeoutMs = 3000 }, 1),
                    (new SnapshotReconnectStrategy { TimeoutMs = 5000 }, 1)
                );
                var cm = new ConnectionManager(session, strategy);
                cm.HeartbeatIntervalMs = 5000;
                cm.HeartbeatTimeoutMs = 30000;
                var client = new FrameSyncClient(session, cm);

                client.OnBound += _ => Interlocked.Increment(ref totalBound);
                client.OnError += err => { };
                client.OnDisconnected += () => { };

                clients[idx] = client;
            }

            // 批量连接
            Console.Write("[Connecting] ");
            for (int i = 0; i < clientCount; i++)
            {
                clients[i].Connect(host, port);
                if (i % 50 == 49)
                {
                    // 每 50 个暂停，让 TCP 握手完成
                    Thread.Sleep(50);
                    // Tick 所有已创建的客户端
                    for (int j = 0; j <= i; j++)
                        clients[j].Tick(50);
                    Console.Write(".");
                }
            }
            Console.WriteLine();

            // 等所有人绑定完
            var deadline = Stopwatch.StartNew();
            while (Interlocked.Read(ref totalBound) < clientCount && deadline.ElapsedMilliseconds < 30000)
            {
                for (int i = 0; i < clientCount; i++)
                    clients[i].Tick(16);
                Thread.Sleep(16);
            }

            long connected = Interlocked.Read(ref totalBound);
            Console.WriteLine($"[Bound] {connected}/{clientCount}\n");

            if (connected == 0)
            {
                Console.WriteLine("FAIL: No clients connected");
                Environment.Exit(1);
            }

            // 注册帧计数
            for (int i = 0; i < clientCount; i++)
            {
                clients[i].OnFrame += _ => Interlocked.Increment(ref totalFramesRecv);
            }

            // 重置计数器
            Interlocked.Exchange(ref totalFramesRecv, 0);
            Interlocked.Exchange(ref totalInputsSent, 0);

            // 主循环
            Console.WriteLine($"[Running] {durationSec}s stress test...\n");
            var inputData = new byte[inputSize];
            new Random(42).NextBytes(inputData);

            var sw = Stopwatch.StartNew();
            int tickCount = 0;

            while (sw.ElapsedMilliseconds < durationSec * 1000)
            {
                for (int i = 0; i < clientCount; i++)
                {
                    clients[i].Tick(16);
                }

                // 每 50ms 发一次输入 (20fps)
                if (tickCount % 3 == 0) // ~48ms at 16ms tick
                {
                    for (int i = 0; i < clientCount; i++)
                    {
                        if (clients[i].CurrentState == FrameSyncClient.State.Syncing)
                        {
                            clients[i].SendInput(inputData);
                            Interlocked.Increment(ref totalInputsSent);
                        }
                    }
                }

                Thread.Sleep(16);
                tickCount++;
            }

            double elapsed = sw.Elapsed.TotalSeconds;
            long frames = Interlocked.Read(ref totalFramesRecv);
            long inputs = Interlocked.Read(ref totalInputsSent);

            // 断开
            for (int i = 0; i < clientCount; i++)
            {
                clients[i].Disconnect();
            }

            // 报告
            Console.WriteLine("==========================================");
            Console.WriteLine("  C# Stress Test Results");
            Console.WriteLine("==========================================");
            Console.WriteLine($"  Duration:         {elapsed:F1}s");
            Console.WriteLine($"  Clients:          {connected}/{clientCount}");
            Console.WriteLine();
            Console.WriteLine("  [ Throughput ]");
            Console.WriteLine($"  Frames received:  {frames} total ({frames / elapsed:F0}/s)");
            if (connected > 0)
                Console.WriteLine($"  Per client:       {frames / connected / elapsed:F1} frames/s");
            Console.WriteLine($"  Inputs sent:      {inputs} total ({inputs / elapsed:F0}/s)");
            Console.WriteLine();

            // 内存
            var gc0 = GC.CollectionCount(0);
            var gc1 = GC.CollectionCount(1);
            var gc2 = GC.CollectionCount(2);
            long mem = GC.GetTotalMemory(false);
            Console.WriteLine("  [ Client Memory ]");
            Console.WriteLine($"  Managed heap:     {mem / 1e6:F2} MB");
            Console.WriteLine($"  GC Gen0/1/2:      {gc0}/{gc1}/{gc2}");
            Console.WriteLine($"  Threads:          {Process.GetCurrentProcess().Threads.Count}");
            Console.WriteLine("==========================================");

            if (connected > 0 && frames > 0)
                Console.WriteLine("\n  RESULT: PASS");
            else
                Console.WriteLine("\n  RESULT: FAIL");
        }
    }
}
