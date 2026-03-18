using System;
using System.Text;
using System.Threading;
using BoomNetwork.Core;
using BoomNetwork.Core.Codec;
using BoomNetwork.Client.Transport;
using BoomNetwork.Client.Session;

namespace BoomNetwork.Example
{
    class Program
    {
        const byte CmdEcho = 1;
        const byte CmdPing = 2;
        const byte CmdNoReply = 3;

        static int passed = 0;
        static int failed = 0;

        static void Main(string[] args)
        {
            string host = "127.0.0.1";
            int port = 9000;

            Console.WriteLine($"[Test] Connecting to {host}:{port}...");

            var transport = new TcpClientTransport();
            var session = new NetworkSession(transport);

            bool connected = false;
            session.OnConnected += () => { connected = true; Console.WriteLine("[Test] Connected!"); };
            session.OnDisconnected += () => Console.WriteLine("[Test] Disconnected.");
            session.OnError += (err) => Console.WriteLine($"[Test] Error: {err}");
            session.OnMessage += (msg) => Console.WriteLine($"[Test] Unsolicited message: {msg}");

            session.Connect(host, port);

            // 等连接
            for (int i = 0; i < 50 && !connected; i++)
            {
                session.Tick(100);
                Thread.Sleep(100);
            }
            if (!connected) { Console.WriteLine("[Test] FAIL: Connection timeout"); return; }

            // --- Test 1: Echo SendAsync ---
            Console.WriteLine("\n--- Test 1: Echo SendAsync ---");
            {
                bool gotResponse = false;
                string? responseData = null;

                session.SendAsync(CmdEcho, Encoding.UTF8.GetBytes("Hello Session!"), 3000,
                    onResponse: msg =>
                    {
                        gotResponse = true;
                        responseData = Encoding.UTF8.GetString(msg.Data);
                    },
                    onTimeout: err => Console.WriteLine($"  Timeout: {err}"));

                TickUntil(session, () => gotResponse, 2000);

                if (gotResponse && responseData == "Hello Session!")
                {
                    Console.WriteLine($"  PASS: Echo returned \"{responseData}\"");
                    passed++;
                }
                else
                {
                    Console.WriteLine($"  FAIL: gotResponse={gotResponse} data={responseData}");
                    failed++;
                }
            }

            // --- Test 2: Ping SendAsync ---
            Console.WriteLine("\n--- Test 2: Ping SendAsync ---");
            {
                bool gotPong = false;
                session.SendAsync(CmdPing, null, 3000,
                    onResponse: msg =>
                    {
                        gotPong = Encoding.UTF8.GetString(msg.Data) == "pong";
                    });

                TickUntil(session, () => gotPong, 2000);
                Report("Ping/Pong", gotPong);
            }

            // --- Test 3: Multiple concurrent SendAsync ---
            Console.WriteLine("\n--- Test 3: Multiple concurrent SendAsync ---");
            {
                int received = 0;
                for (int i = 0; i < 10; i++)
                {
                    session.SendAsync(CmdEcho, Encoding.UTF8.GetBytes($"msg-{i}"), 3000,
                        onResponse: msg => { Interlocked.Increment(ref received); });
                }

                TickUntil(session, () => received >= 10, 3000);
                Report($"10 concurrent (got {received}/10)", received == 10);
            }

            // --- Test 4: Timeout ---
            Console.WriteLine("\n--- Test 4: SendAsync timeout ---");
            {
                bool timedOut = false;
                session.SendAsync(CmdNoReply, null, 500,
                    onResponse: msg => { },
                    onTimeout: err => { timedOut = true; Console.WriteLine($"  Expected timeout: {err}"); });

                TickUntil(session, () => timedOut, 2000);
                Report("Timeout detected", timedOut);
            }

            // --- Summary ---
            Console.WriteLine($"\n==========================================");
            Console.WriteLine($"  Results: {passed} passed, {failed} failed");
            Console.WriteLine($"==========================================");

            session.Disconnect();
            session.Tick(0);

            if (failed > 0) Environment.Exit(1);
        }

        static void TickUntil(NetworkSession session, Func<bool> condition, int maxMs)
        {
            int elapsed = 0;
            while (!condition() && elapsed < maxMs)
            {
                session.Tick(16);
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
