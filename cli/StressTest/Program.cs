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
        static long totalBound;
        static long totalFramesRecv;
        static long totalInputsSent;

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

            // 创建
            for (int i = 0; i < clientCount; i++)
            {
                var transport = new TcpClientTransport();
                var session = new NetworkSession(transport);
                var strategy = new CompositeReconnectStrategy(
                    (new QuickReconnectStrategy { TimeoutMs = 3000 }, 1),
                    (new SnapshotReconnectStrategy { TimeoutMs = 5000 }, 1)
                );
                var cm = new ConnectionManager(session, strategy);
                cm.HeartbeatIntervalMs = 5000;
                cm.HeartbeatTimeoutMs = 30000;
                clients[i] = new FrameSyncClient(session, cm);
                clients[i].OnBound += _ => Interlocked.Increment(ref totalBound);
                clients[i].OnError += _ => { };
            }

            // 批量连接
            Console.Write("[Connecting] ");
            for (int i = 0; i < clientCount; i++)
            {
                clients[i].Connect(host, port);
                if (i % 50 == 49)
                {
                    Thread.Sleep(50);
                    TickAll(clients, i + 1, 50);
                    Console.Write(".");
                }
            }
            Console.WriteLine();

            // 等绑定
            var deadline = Stopwatch.StartNew();
            while (Interlocked.Read(ref totalBound) < clientCount && deadline.ElapsedMilliseconds < 30000)
            {
                TickAll(clients, clientCount, 16);
                Thread.Sleep(16);
            }
            Console.WriteLine($"[Bound] {Interlocked.Read(ref totalBound)}/{clientCount}");

            // 等所有房间开始帧同步
            Console.Write("[Waiting FrameSync start] ");
            deadline.Restart();
            while (deadline.ElapsedMilliseconds < 10000)
            {
                TickAll(clients, clientCount, 16);
                Thread.Sleep(16);

                int syncing = 0;
                for (int i = 0; i < clientCount; i++)
                    if (clients[i].CurrentState >= FrameSyncClient.State.Syncing)
                        syncing++;
                if (syncing >= clientCount)
                    break;
            }
            {
                int syncing = 0;
                for (int i = 0; i < clientCount; i++)
                    if (clients[i].CurrentState >= FrameSyncClient.State.Syncing)
                        syncing++;
                Console.WriteLine($"{syncing}/{clientCount} syncing");
            }

            // 注册帧计数
            for (int i = 0; i < clientCount; i++)
                clients[i].OnFrame += _ => Interlocked.Increment(ref totalFramesRecv);

            // 重置计数器 — 从这里开始才是纯粹的稳定期统计
            Interlocked.Exchange(ref totalFramesRecv, 0);
            Interlocked.Exchange(ref totalInputsSent, 0);

            Console.WriteLine($"\n[Running] {durationSec}s stress test...\n");
            var inputData = new byte[inputSize];
            new Random(42).NextBytes(inputData);

            // 用 Stopwatch 精确控制 tick 间隔
            var sw = Stopwatch.StartNew();
            long nextTickMs = 0;
            const int tickIntervalMs = 16;
            int inputCounter = 0;

            while (sw.ElapsedMilliseconds < durationSec * 1000)
            {
                long now = sw.ElapsedMilliseconds;

                TickAll(clients, clientCount, tickIntervalMs);

                // 每 ~48ms 发一次输入 (约 20fps)
                inputCounter++;
                if (inputCounter % 3 == 0)
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

                // 精确 sleep：补偿 CPU 时间
                nextTickMs += tickIntervalMs;
                long sleepMs = nextTickMs - sw.ElapsedMilliseconds;
                if (sleepMs > 0)
                    Thread.Sleep((int)sleepMs);
            }

            // 最后再 tick 几次确保收完
            for (int i = 0; i < 10; i++)
            {
                TickAll(clients, clientCount, 16);
                Thread.Sleep(16);
            }

            double elapsed = sw.Elapsed.TotalSeconds;
            long frames = Interlocked.Read(ref totalFramesRecv);
            long inputs = Interlocked.Read(ref totalInputsSent);
            long bound = Interlocked.Read(ref totalBound);

            for (int i = 0; i < clientCount; i++)
                clients[i].Disconnect();

            Console.WriteLine("==========================================");
            Console.WriteLine("  C# ↔ Go Cross-Language Stress Test");
            Console.WriteLine("==========================================");
            Console.WriteLine($"  Duration:         {elapsed:F1}s");
            Console.WriteLine($"  Clients:          {bound}/{clientCount}");
            Console.WriteLine();
            Console.WriteLine("  [ Throughput ]");
            Console.WriteLine($"  Frames received:  {frames} total ({frames / elapsed:F0}/s)");
            if (bound > 0)
                Console.WriteLine($"  Per client:       {frames / (double)bound / elapsed:F1} frames/s");
            Console.WriteLine($"  Inputs sent:      {inputs} total ({inputs / elapsed:F0}/s)");
            Console.WriteLine();

            var gc0 = GC.CollectionCount(0);
            var gc1 = GC.CollectionCount(1);
            var gc2 = GC.CollectionCount(2);
            long mem = GC.GetTotalMemory(false);
            Console.WriteLine("  [ Client Memory ]");
            Console.WriteLine($"  Managed heap:     {mem / 1e6:F2} MB");
            Console.WriteLine($"  GC Gen0/1/2:      {gc0}/{gc1}/{gc2}");
            Console.WriteLine($"  Threads:          {Process.GetCurrentProcess().Threads.Count}");
            Console.WriteLine("==========================================");

            if (bound > 0 && frames > 0)
                Console.WriteLine("\n  RESULT: PASS");
            else
                Console.WriteLine("\n  RESULT: FAIL");
        }

        static void TickAll(FrameSyncClient[] clients, int count, float dt)
        {
            for (int i = 0; i < count; i++)
                clients[i].Tick(dt);
        }
    }
}
