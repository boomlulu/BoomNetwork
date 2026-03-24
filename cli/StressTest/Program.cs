using System;
using System.Diagnostics;
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
        static long totalSyncing;
        static long totalFramesRecv;
        static long totalInputsSent;

        static void Main(string[] args)
        {
            string host = Environment.GetEnvironmentVariable("BOOM_HOST") ?? "127.0.0.1";
            int port = int.TryParse(Environment.GetEnvironmentVariable("BOOM_PORT"), out var p) ? p : 9000;
            int clientCount = int.TryParse(Environment.GetEnvironmentVariable("BOOM_CLIENTS"), out var c) ? c : 100;
            int durationSec = int.TryParse(Environment.GetEnvironmentVariable("BOOM_DURATION"), out var d) ? d : 10;

            Console.WriteLine("==========================================");
            Console.WriteLine("  BoomNetwork C# ↔ Go Stress Test");
            Console.WriteLine($"  {clientCount} clients → {host}:{port}");
            Console.WriteLine($"  Duration: {durationSec}s");
            Console.WriteLine("==========================================\n");

            var clients = new FrameSyncClient[clientCount];

            for (int i = 0; i < clientCount; i++)
            {
                clients[i] = new FrameSyncClient(5000, 30000);
                clients[i].OnConnected += () => Interlocked.Increment(ref totalBound);
                clients[i].OnFrameSyncStart += _ => Interlocked.Increment(ref totalSyncing);
                clients[i].OnError += _ => { };
            }

            // --- 阶段 1：连接 ---
            Console.Write("[1/4 Connecting] ");
            for (int i = 0; i < clientCount; i++)
            {
                clients[i].Connect(host, port);
                if (i % 50 == 49)
                {
                    Thread.Sleep(30);
                    TickAll(clients, i + 1, 30);
                    Console.Write(".");
                }
            }
            Console.WriteLine();

            // --- 阶段 2：等所有人绑定 ---
            WaitFor("2/4 Bound", () => Interlocked.Read(ref totalBound) >= clientCount,
                clients, clientCount, 30000);
            Console.WriteLine($"  {Interlocked.Read(ref totalBound)}/{clientCount}");

            // --- 阶段 3：等所有人进入帧同步 ---
            WaitFor("3/4 Syncing", () => Interlocked.Read(ref totalSyncing) >= clientCount,
                clients, clientCount, 30000);
            Console.WriteLine($"  {Interlocked.Read(ref totalSyncing)}/{clientCount}");

            // --- 阶段 4：稳定期测量 ---
            // 注册帧计数
            for (int i = 0; i < clientCount; i++)
                clients[i].OnFrame += _ => Interlocked.Increment(ref totalFramesRecv);

            // 清零（排除启动阶段）
            Interlocked.Exchange(ref totalFramesRecv, 0);
            Interlocked.Exchange(ref totalInputsSent, 0);

            Console.WriteLine($"\n[4/4 Running] {durationSec}s measurement...\n");

            var inputData = new byte[32];
            new Random(42).NextBytes(inputData);

            var sw = Stopwatch.StartNew();
            long nextTickMs = 0;
            const int tickMs = 16;
            int tickCount = 0;

            while (sw.ElapsedMilliseconds < durationSec * 1000)
            {
                TickAll(clients, clientCount, tickMs);

                // ~20fps 发输入
                tickCount++;
                if (tickCount % 3 == 0)
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

                nextTickMs += tickMs;
                long sleepMs = nextTickMs - sw.ElapsedMilliseconds;
                if (sleepMs > 0) Thread.Sleep((int)sleepMs);
            }

            // 最终排空：TCP buffer 里可能还有数据没取出
            for (int i = 0; i < 30; i++)
            {
                TickAll(clients, clientCount, tickMs);
                Thread.Sleep(10);
            }

            double elapsed = sw.Elapsed.TotalSeconds; // 用实际 elapsed（含排空时间）
            long frames = Interlocked.Read(ref totalFramesRecv);
            long inputs = Interlocked.Read(ref totalInputsSent);
            long bound = Interlocked.Read(ref totalBound);
            long syncing = Interlocked.Read(ref totalSyncing);

            for (int i = 0; i < clientCount; i++)
                clients[i].Disconnect();

            double fpsPerClient = frames / (double)syncing / elapsed;

            Console.WriteLine("==========================================");
            Console.WriteLine("  C# ↔ Go Cross-Language Stress Test");
            Console.WriteLine("==========================================");
            Console.WriteLine($"  Duration:         {durationSec}s");
            Console.WriteLine($"  Bound:            {bound}/{clientCount}");
            Console.WriteLine($"  Syncing:          {syncing}/{clientCount}");
            Console.WriteLine();
            Console.WriteLine("  [ Throughput ]");
            Console.WriteLine($"  Frames received:  {frames} total ({frames / elapsed:F0}/s)");
            Console.WriteLine($"  Per client:       {fpsPerClient:F1} frames/s");
            Console.WriteLine($"  Inputs sent:      {inputs} total ({inputs / elapsed:F0}/s)");
            Console.WriteLine();

            long mem = GC.GetTotalMemory(false);
            Console.WriteLine("  [ Client Memory ]");
            Console.WriteLine($"  Managed heap:     {mem / 1e6:F2} MB");
            Console.WriteLine($"  GC Gen0/1/2:      {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
            Console.WriteLine($"  Threads:          {Process.GetCurrentProcess().Threads.Count}");
            Console.WriteLine("==========================================");

            bool pass = syncing >= clientCount * 95 / 100 && fpsPerClient >= 19.5;
            Console.WriteLine(pass ? "\n  RESULT: PASS" : "\n  RESULT: FAIL");
            if (!pass) Environment.Exit(1);
        }

        static void TickAll(FrameSyncClient[] clients, int count, float dt)
        {
            for (int i = 0; i < count; i++)
                clients[i].Tick(dt);
        }

        static void WaitFor(string label, Func<bool> condition,
            FrameSyncClient[] clients, int count, int timeoutMs)
        {
            Console.Write($"[{label}] ");
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            {
                TickAll(clients, count, 16);
                Thread.Sleep(8); // 快速 tick，不拖延
            }
        }
    }
}
