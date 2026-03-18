using System;
using System.Text;
using System.Threading;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Client.Transport;
using BoomNetwork.Client.Session;
using BoomNetwork.Client.FrameSync;

namespace BoomNetwork.FrameSyncExample
{
    class Program
    {
        static int passed = 0;
        static int failed = 0;

        static void Main(string[] args)
        {
            string host = "127.0.0.1";
            int port = 9000;

            Console.WriteLine($"[FrameSync Test] Connecting 2 clients to {host}:{port}...\n");

            // --- 创建两个客户端 ---
            var client1 = CreateClient("Client1");
            var client2 = CreateClient("Client2");

            // --- 连接 ---
            client1.Connect(host, port);
            client2.Connect(host, port);

            // 等待两个都绑定成功
            TickUntil(new[] { client1, client2 },
                () => client1.CurrentState >= FrameSyncClient.State.WaitingStart
                   && client2.CurrentState >= FrameSyncClient.State.WaitingStart,
                3000);

            Report("Both clients bound",
                client1.CurrentState >= FrameSyncClient.State.WaitingStart
                && client2.CurrentState >= FrameSyncClient.State.WaitingStart);

            Console.WriteLine($"  Client1: PlayerId={client1.PlayerId}");
            Console.WriteLine($"  Client2: PlayerId={client2.PlayerId}");

            // --- 等待帧同步开始（服务器 2 人自动开始）---
            TickUntil(new[] { client1, client2 },
                () => client1.CurrentState >= FrameSyncClient.State.Syncing
                   && client2.CurrentState >= FrameSyncClient.State.Syncing,
                5000);

            Report("FrameSync started",
                client1.CurrentState >= FrameSyncClient.State.Syncing
                && client2.CurrentState >= FrameSyncClient.State.Syncing);

            // --- 发送输入并接收帧 ---
            Console.WriteLine("\n--- Sending inputs and receiving frames ---");

            int client1Frames = 0;
            int client2Frames = 0;
            int client1RecvInputs = 0;
            int client2RecvInputs = 0;

            client1.OnFrame += frame =>
            {
                client1Frames++;
                client1RecvInputs += frame.Inputs?.Length ?? 0;
            };

            client2.OnFrame += frame =>
            {
                client2Frames++;
                client2RecvInputs += frame.Inputs?.Length ?? 0;
            };

            // 两个客户端各发 5 条输入
            for (int i = 0; i < 5; i++)
            {
                client1.SendInput(Encoding.UTF8.GetBytes($"c1-input-{i}"));
                client2.SendInput(Encoding.UTF8.GetBytes($"c2-input-{i}"));
                TickFor(new[] { client1, client2 }, 100); // 等一帧让输入到达
            }

            // 再 Tick 一段时间收完帧
            TickFor(new[] { client1, client2 }, 2000);

            Console.WriteLine($"  Client1: received {client1Frames} frames, {client1RecvInputs} inputs");
            Console.WriteLine($"  Client2: received {client2Frames} frames, {client2RecvInputs} inputs");

            // --- 验证 ---
            Report("Both clients received frames",
                client1Frames > 0 && client2Frames > 0);

            Report("Frame numbers match",
                client1.LastFrameNumber == client2.LastFrameNumber);

            Report("Both received inputs from both players",
                client1RecvInputs >= 5 && client2RecvInputs >= 5);

            // 帧号应该连续（服务器 20fps，2 秒约 40 帧）
            Report($"Frame number reasonable (got {client1.LastFrameNumber})",
                client1.LastFrameNumber >= 10 && client1.LastFrameNumber <= 200);

            // --- 清理 ---
            client1.Disconnect();
            client2.Disconnect();
            TickFor(new[] { client1, client2 }, 100);

            Console.WriteLine($"\n==========================================");
            Console.WriteLine($"  Results: {passed} passed, {failed} failed");
            Console.WriteLine($"==========================================");

            if (failed > 0) Environment.Exit(1);
        }

        static FrameSyncClient CreateClient(string name)
        {
            var transport = new TcpClientTransport();
            var session = new NetworkSession(transport);
            var client = new FrameSyncClient(session);

            client.OnBound += id => Console.WriteLine($"  [{name}] Bound as player {id}");
            client.OnFrameSyncStart += data =>
                Console.WriteLine($"  [{name}] FrameSync started (rate={data.FrameRate}, interval={data.FrameInterval}ms)");
            client.OnFrameSyncStop += () => Console.WriteLine($"  [{name}] FrameSync stopped");
            client.OnError += err => Console.WriteLine($"  [{name}] Error: {err}");

            return client;
        }

        static void TickUntil(FrameSyncClient[] clients, Func<bool> condition, int maxMs)
        {
            int elapsed = 0;
            while (!condition() && elapsed < maxMs)
            {
                foreach (var c in clients) c.Tick(16);
                Thread.Sleep(16);
                elapsed += 16;
            }
        }

        static void TickFor(FrameSyncClient[] clients, int ms)
        {
            int elapsed = 0;
            while (elapsed < ms)
            {
                foreach (var c in clients) c.Tick(16);
                Thread.Sleep(16);
                elapsed += 16;
            }
        }

        static void Report(string name, bool ok)
        {
            if (ok) { Console.WriteLine($"  PASS: {name}"); passed++; }
            else { Console.WriteLine($"  FAIL: {name}"); failed++; }
        }
    }
}
