using System;
using System.Text;
using System.Threading;
using BoomNetwork.Core.FrameSync;
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

            var client1 = new FrameSyncClient(500, 2000);
            var client2 = new FrameSyncClient(500, 2000);

            client1.OnLog += msg => Console.WriteLine($"  [C1] {msg}");
            client2.OnLog += msg => Console.WriteLine($"  [C2] {msg}");

            // === Test 1: 连接 + 绑定 ===
            Console.WriteLine("--- Test 1: Connect + Bind ---");
            client1.Connect(host, port);
            client2.Connect(host, port);

            TickUntil(new[] { client1, client2 },
                () => client1.CurrentState >= FrameSyncClient.State.Connected
                   && client2.CurrentState >= FrameSyncClient.State.Connected,
                5000);

            Report("Both connected",
                client1.CurrentState >= FrameSyncClient.State.Connected
                && client2.CurrentState >= FrameSyncClient.State.Connected);

            // === Test 2: 帧同步开始 ===
            Console.WriteLine("\n--- Test 2: FrameSync Start ---");

            // 快照回调
            client1.OnTakeSnapshot = () => Encoding.UTF8.GetBytes($"c1-snapshot-{client1.LastFrameNumber}");
            client2.OnTakeSnapshot = () => Encoding.UTF8.GetBytes($"c2-snapshot-{client2.LastFrameNumber}");

            // autoroom 模式：SessionBind 已自动分房，直接 RequestStart
            client1.RequestStart();
            Console.WriteLine("  Sent RequestStart");

            TickUntil(new[] { client1, client2 },
                () => client1.CurrentState == FrameSyncClient.State.Syncing
                   && client2.CurrentState == FrameSyncClient.State.Syncing,
                5000);

            Report("FrameSync started",
                client1.CurrentState == FrameSyncClient.State.Syncing
                && client2.CurrentState == FrameSyncClient.State.Syncing);

            // === Test 3: 服务器下发配置验证 ===
            Console.WriteLine("\n--- Test 3: Server-pushed config ---");
            var initData = client1.InitData ?? default;
            Console.WriteLine($"  SnapshotInterval={initData.SnapshotInterval}, QuickReconnectMaxMs={initData.QuickReconnectMaxMs}");
            Report("SnapshotInterval > 0", initData.SnapshotInterval > 0);
            Report("QuickReconnectMaxMs > 0", initData.QuickReconnectMaxMs > 0);
            Report("Client SnapshotInterval applied", client1.SnapshotInterval == (uint)initData.SnapshotInterval);

            // === Test 4: 收帧 ===
            Console.WriteLine("\n--- Test 4: Receive Frames ---");
            int c1Frames = 0;
            client1.OnFrame += _ => c1Frames++;

            for (int i = 0; i < 5; i++)
            {
                client1.SendInput(Encoding.UTF8.GetBytes($"input-{i}"));
                TickFor(new[] { client1, client2 }, 100);
            }
            TickFor(new[] { client1, client2 }, 1000);

            Report($"Client1 received {c1Frames} frames", c1Frames > 0);

            // === Test 5: 快照上传 ===
            Console.WriteLine("\n--- Test 5: Snapshot upload ---");
            int snapshotsTaken = 0;
            client1.OnTakeSnapshot = () =>
            {
                snapshotsTaken++;
                return Encoding.UTF8.GetBytes($"c1-snapshot-{client1.LastFrameNumber}");
            };
            client1.SnapshotInterval = 20;
            client2.SnapshotInterval = 20;

            TickFor(new[] { client1, client2 }, 3000);
            Console.WriteLine($"  Snapshots taken by Client1: {snapshotsTaken}");
            Report("Snapshots uploaded", snapshotsTaken >= 2);

            // === Test 6: 心跳保持连接 ===
            Console.WriteLine("\n--- Test 6: Heartbeat keeps alive ---");
            TickFor(new[] { client1, client2 }, 3000);
            Report("Still syncing after 3s",
                client1.CurrentState == FrameSyncClient.State.Syncing);

            // === Test 7: 快速重连 ===
            Console.WriteLine("\n--- Test 7: Quick Reconnect ---");
            bool reconnected7 = false;
            client1.OnReconnected += () => { reconnected7 = true; Console.WriteLine("  [C1] Reconnected!"); };

            uint frameBeforeDisconnect = client1.LastFrameNumber;
            Console.WriteLine($"  Frame before disconnect: {frameBeforeDisconnect}");

            client1.SimulateNetworkDrop();

            TickUntil(new[] { client1, client2 },
                () => reconnected7,
                10000);

            Report("Quick reconnected", reconnected7);
            Report("FrameSyncClient state = Syncing",
                client1.CurrentState == FrameSyncClient.State.Syncing);
            Report($"Frame advanced (was {frameBeforeDisconnect}, now {client1.LastFrameNumber})",
                client1.LastFrameNumber >= frameBeforeDisconnect);

            // === Test 8: 重连后继续收帧 ===
            Console.WriteLine("\n--- Test 8: Frames after reconnect ---");
            int framesAfterReconnect = 0;
            Action<FrameData> frameHandler8 = _ => framesAfterReconnect++;
            client1.OnFrame += frameHandler8;

            TickFor(new[] { client1, client2 }, 2000);

            Report($"Frames after reconnect: {framesAfterReconnect}",
                framesAfterReconnect > 0);
            client1.OnFrame -= frameHandler8;

            // === Test 9: 快照重连（缓冲区溢出） ===
            Console.WriteLine("\n--- Test 9: Snapshot Reconnect (buffer overflow) ---");

            TickFor(new[] { client1, client2 }, 2000);

            bool snapshotLoaded = false;
            client1.OnLoadSnapshot = data =>
            {
                snapshotLoaded = true;
                Console.WriteLine($"  [C1] LoadSnapshot ({data.Length} bytes): {Encoding.UTF8.GetString(data)}");
            };

            int reconnectCount9 = 0;
            Action reconnectHandler9 = () => { reconnectCount9++; Console.WriteLine($"  [C1] Reconnected (count={reconnectCount9})"); };
            client1.OnReconnected += reconnectHandler9;

            frameBeforeDisconnect = client1.LastFrameNumber;
            Console.WriteLine($"  Frame before disconnect: {frameBeforeDisconnect}");

            client1.SimulateNetworkDrop();

            // Phase 1: 只 tick Client2，让缓冲区溢出
            Console.WriteLine("  Phase 1: Only ticking Client2 (buffer overflow)...");
            TickFor(new[] { client2 }, 4000);

            // Phase 2: tick Client1，触发重连
            Console.WriteLine("  Phase 2: Now ticking Client1...");
            TickUntil(new[] { client1, client2 },
                () => reconnectCount9 > 0,
                30000);

            Report("Reconnected after buffer overflow", reconnectCount9 > 0);
            Report("Snapshot loaded via OnLoadSnapshot", snapshotLoaded);
            client1.OnReconnected -= reconnectHandler9;

            // === Test 10: 快照重连后继续收帧 ===
            Console.WriteLine("\n--- Test 10: Frames after snapshot reconnect ---");
            int framesAfterSnapshotReconnect = 0;
            Action<FrameData> frameHandler10 = _ => framesAfterSnapshotReconnect++;
            client1.OnFrame += frameHandler10;

            TickFor(new[] { client1, client2 }, 2000);

            Report($"Frames after snapshot reconnect: {framesAfterSnapshotReconnect}",
                framesAfterSnapshotReconnect > 0);
            client1.OnFrame -= frameHandler10;

            // === 清理 ===
            client1.Disconnect();
            client2.Disconnect();

            Console.WriteLine($"\n==========================================");
            Console.WriteLine($"  Results: {passed} passed, {failed} failed");
            Console.WriteLine($"==========================================");

            if (failed > 0) Environment.Exit(1);
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
