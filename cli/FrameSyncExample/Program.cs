using System;
using System.Text;
using System.Threading;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Client.Transport;
using BoomNetwork.Client.Session;
using BoomNetwork.Client.Connection;
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
            int port = int.TryParse(Environment.GetEnvironmentVariable("BOOM_PORT"), out var p) ? p : 9000;

            Console.WriteLine($"[Test] Connecting 2 clients to {host}:{port}...\n");

            var (client1, cm1, transport1) = CreateClient("Client1");
            var (client2, cm2, transport2) = CreateClient("Client2");

            // 缩短心跳参数方便测试
            cm1.HeartbeatIntervalMs = 500;
            cm1.HeartbeatTimeoutMs = 2000;
            cm2.HeartbeatIntervalMs = 500;
            cm2.HeartbeatTimeoutMs = 2000;

            // === Test 1: 连接 + 绑定 ===
            Console.WriteLine("--- Test 1: Connect + Bind ---");
            client1.Connect(host, port);
            client2.Connect(host, port);

            TickUntil(new[] { client1, client2 },
                () => client1.CurrentState >= FrameSyncClient.State.WaitingStart
                   && client2.CurrentState >= FrameSyncClient.State.WaitingStart,
                5000);

            Report("Both bound",
                client1.CurrentState >= FrameSyncClient.State.WaitingStart
                && client2.CurrentState >= FrameSyncClient.State.WaitingStart);

            // === Test 2: 帧同步开始 ===
            Console.WriteLine("\n--- Test 2: FrameSync Start ---");
            // 发 RequestStart（服务端不再自动 Start）
            cm1.Session.Send(FrameSyncCmd.RequestStart);
            Console.WriteLine("  Sent RequestStart");

            TickUntil(new[] { client1, client2 },
                () => client1.CurrentState >= FrameSyncClient.State.Syncing
                   && client2.CurrentState >= FrameSyncClient.State.Syncing,
                5000);

            Report("FrameSync started",
                client1.CurrentState >= FrameSyncClient.State.Syncing
                && client2.CurrentState >= FrameSyncClient.State.Syncing);

            // === Test 3: 收帧 ===
            Console.WriteLine("\n--- Test 3: Receive Frames ---");
            int c1Frames = 0;
            client1.OnFrame += _ => c1Frames++;

            for (int i = 0; i < 5; i++)
            {
                client1.SendInput(Encoding.UTF8.GetBytes($"input-{i}"));
                TickFor(new[] { client1, client2 }, 100);
            }
            TickFor(new[] { client1, client2 }, 1000);

            Report($"Client1 received {c1Frames} frames", c1Frames > 0);

            // === Test 4: 心跳保持连接 ===
            Console.WriteLine("\n--- Test 4: Heartbeat keeps alive ---");
            TickFor(new[] { client1, client2 }, 3000);
            Report("Still syncing after 3s",
                client1.CurrentState == FrameSyncClient.State.Syncing
                && cm1.CurrentState == ConnectionManager.State.Connected);

            // === Test 5: 断线 + 自动重连 ===
            Console.WriteLine("\n--- Test 5: Disconnect + Auto Reconnect ---");
            bool reconnected = false;
            client1.OnReconnected += () =>
            {
                reconnected = true;
                Console.WriteLine("  [Client1] Reconnected!");
            };

            uint frameBeforeDisconnect = client1.LastFrameNumber;
            Console.WriteLine($"  Frame before disconnect: {frameBeforeDisconnect}");

            // 通过 transport 直接断开（模拟网络异常）
            transport1.Disconnect();

            // 等重连完成
            TickUntil(new[] { client1, client2 },
                () => reconnected,
                15000);

            Report("Auto reconnected", reconnected);
            Report("ConnectionManager state = Connected",
                cm1.CurrentState == ConnectionManager.State.Connected);
            Report("FrameSyncClient state = Syncing",
                client1.CurrentState == FrameSyncClient.State.Syncing);
            Report($"Frame advanced (was {frameBeforeDisconnect}, now {client1.LastFrameNumber})",
                client1.LastFrameNumber >= frameBeforeDisconnect);

            // === Test 6: 重连后继续收帧 ===
            Console.WriteLine("\n--- Test 6: Frames after reconnect ---");
            int framesAfterReconnect = 0;
            client1.OnFrame += _ => framesAfterReconnect++;

            TickFor(new[] { client1, client2 }, 2000);

            Report($"Frames after reconnect: {framesAfterReconnect}",
                framesAfterReconnect > 0);

            // === 清理 ===
            client1.Disconnect();
            client2.Disconnect();
            TickFor(new[] { client1, client2 }, 100);

            Console.WriteLine($"\n==========================================");
            Console.WriteLine($"  Results: {passed} passed, {failed} failed");
            Console.WriteLine($"==========================================");

            if (failed > 0) Environment.Exit(1);
        }

        static (FrameSyncClient client, ConnectionManager cm, TcpClientTransport transport) CreateClient(string name)
        {
            var transport = new TcpClientTransport();
            var session = new NetworkSession(transport);
            var reconnectStrategy = new CompositeReconnectStrategy(
                (new QuickReconnectStrategy { TimeoutMs = 3000 }, 2),
                (new SnapshotReconnectStrategy { TimeoutMs = 5000 }, 1)
            );
            var cm = new ConnectionManager(session, reconnectStrategy);
            var client = new FrameSyncClient(session, cm);

            client.OnBound += id => Console.WriteLine($"  [{name}] Bound as player {id}");
            client.OnFrameSyncStart += data =>
                Console.WriteLine($"  [{name}] FrameSync started (rate={data.FrameRate})");
            client.OnFrameSyncStop += () => Console.WriteLine($"  [{name}] FrameSync stopped");
            client.OnError += err => Console.WriteLine($"  [{name}] {err}");

            return (client, cm, transport);
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
