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
            byte[] lastSnapshot = Array.Empty<byte>();

            // 注册快照回调（模拟游戏状态序列化）
            client1.OnTakeSnapshot = () =>
            {
                snapshotsTaken++;
                lastSnapshot = Encoding.UTF8.GetBytes($"snapshot-frame-{client1.LastFrameNumber}");
                return lastSnapshot;
            };
            client2.OnTakeSnapshot = () =>
            {
                return Encoding.UTF8.GetBytes($"c2-snapshot-{client2.LastFrameNumber}");
            };

            // 设置短快照间隔方便测试
            client1.SnapshotInterval = 20;
            client2.SnapshotInterval = 20;

            // 跑一段时间让快照上传
            TickFor(new[] { client1, client2 }, 3000);
            Console.WriteLine($"  Snapshots taken by Client1: {snapshotsTaken}");
            Report("Snapshots uploaded", snapshotsTaken >= 2);

            // === Test 6: 心跳保持连接 ===
            Console.WriteLine("\n--- Test 6: Heartbeat keeps alive ---");
            TickFor(new[] { client1, client2 }, 3000);
            Report("Still syncing after 3s",
                client1.CurrentState == FrameSyncClient.State.Syncing
                && cm1.CurrentState == ConnectionManager.State.Connected);

            // === Test 7: 快速重连（断线 < 缓冲区内） ===
            Console.WriteLine("\n--- Test 7: Quick Reconnect ---");
            bool reconnected7 = false;
            Action reconnectHandler7 = () =>
            {
                reconnected7 = true;
                Console.WriteLine("  [Client1] Quick reconnected!");
            };
            client1.OnReconnected += reconnectHandler7;

            uint frameBeforeDisconnect = client1.LastFrameNumber;
            Console.WriteLine($"  Frame before disconnect: {frameBeforeDisconnect}");

            // 通过 transport 直接断开（模拟网络异常）
            transport1.Disconnect();

            // 等重连完成（应该很快，快速重连）
            TickUntil(new[] { client1, client2 },
                () => reconnected7,
                10000);

            Report("Quick reconnected", reconnected7);
            Report("ConnectionManager state = Connected",
                cm1.CurrentState == ConnectionManager.State.Connected);
            Report("FrameSyncClient state = Syncing",
                client1.CurrentState == FrameSyncClient.State.Syncing);
            Report($"Frame advanced (was {frameBeforeDisconnect}, now {client1.LastFrameNumber})",
                client1.LastFrameNumber >= frameBeforeDisconnect);
            client1.OnReconnected -= reconnectHandler7;

            // === Test 8: 重连后继续收帧 ===
            Console.WriteLine("\n--- Test 8: Frames after reconnect ---");
            int framesAfterReconnect = 0;
            Action<FrameData> frameHandler8 = _ => framesAfterReconnect++;
            client1.OnFrame += frameHandler8;

            TickFor(new[] { client1, client2 }, 2000);

            Report($"Frames after reconnect: {framesAfterReconnect}",
                framesAfterReconnect > 0);
            client1.OnFrame -= frameHandler8;

            // === Test 9: 快照重连（模拟长时间断线，缓冲区溢出） ===
            Console.WriteLine("\n--- Test 9: Snapshot Reconnect (buffer overflow) ---");
            Console.WriteLine("  NOTE: Requires server started with config_test.yaml (frameBufferSize=40)");

            // 确保有快照
            TickFor(new[] { client1, client2 }, 2000);

            bool snapshotLoaded = false;
            client1.OnLoadSnapshot = data =>
            {
                snapshotLoaded = true;
                Console.WriteLine($"  [Client1] LoadSnapshot called ({data.Length} bytes): {Encoding.UTF8.GetString(data)}");
            };

            int reconnectCount9 = 0;
            Action reconnectHandler9 = () =>
            {
                reconnectCount9++;
                Console.WriteLine($"  [Client1] Reconnected via snapshot path (count={reconnectCount9})");
            };
            client1.OnReconnected += reconnectHandler9;

            frameBeforeDisconnect = client1.LastFrameNumber;
            Console.WriteLine($"  Frame before disconnect: {frameBeforeDisconnect}");

            // 断开 Client1
            transport1.Disconnect();

            // Phase 1: 只 tick Client2，让服务器继续推帧覆盖缓冲区
            // Client1 不 tick → 重连逻辑不推进 → TCP 不发起
            // 缓冲区 40 帧 = 2 秒 @20fps，等 4 秒确保溢出
            Console.WriteLine("  Phase 1: Only ticking Client2 (buffer overflow in ~2s)...");
            TickFor(new[] { client2 }, 4000);

            // Phase 2: 开始 tick Client1，触发重连
            // QuickReconnect 发 lastFrame=271 → 服务器 oldestFrame > 271 → BufferStale → 降级快照重连
            Console.WriteLine("  Phase 2: Now ticking Client1 → reconnect starts...");
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
                Console.WriteLine($"  [{name}] FrameSync started (rate={data.FrameRate}, snapshot={data.SnapshotInterval}, quickReconnectMs={data.QuickReconnectMaxMs})");
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
