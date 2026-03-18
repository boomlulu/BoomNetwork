using System;
using System.Text;
using System.Threading;
using BoomNetwork.Core;
using BoomNetwork.Core.Codec;
using BoomNetwork.Core.Framing;
using BoomNetwork.Client.Transport;

namespace BoomNetwork.KcpTest
{
    class Program
    {
        static void Main(string[] args)
        {
            string host = "127.0.0.1";
            int port = 9000;
            int passed = 0, failed = 0;

            Console.WriteLine($"[KCP Test] Connecting to {host}:{port} via KCP...\n");

            var transport = new KcpClientTransport();
            var framing = new LengthPrefixFraming();
            int recvCount = 0;

            transport.OnConnected += () => Console.WriteLine("  Connected!");
            transport.OnDisconnected += () => Console.WriteLine("  Disconnected.");
            transport.OnError += err => Console.WriteLine($"  Error: {err}");
            transport.OnData += (data, offset, length) =>
            {
                framing.Feed(data, offset, length);
                while (framing.TryDequeueFrame(out var frame))
                {
                    var msg = MessageCodec.Decode(frame.Span);
                    frame.Dispose();
                    recvCount++;
                    string text = msg.DataLength > 0 ? Encoding.UTF8.GetString(msg.DataSpan) : "";
                    Console.WriteLine($"  Recv #{recvCount}: {msg} data=\"{text}\"");
                }
            };

            transport.Connect(host, port);

            // Tick 等连接
            for (int i = 0; i < 50; i++)
            {
                transport.Tick();
                Thread.Sleep(50);
                if (transport.State == BoomNetwork.Core.Transport.TransportState.Connected)
                    break;
            }

            if (transport.State != BoomNetwork.Core.Transport.TransportState.Connected)
            {
                Console.WriteLine("  FAIL: Connection timeout");
                Environment.Exit(1);
            }

            // 发 3 条 echo 消息
            for (int i = 1; i <= 3; i++)
            {
                var msg = new Message
                {
                    Cmd = 1, // Echo
                    HasSeq = true,
                    Seq = i,
                    Data = Encoding.UTF8.GetBytes($"KCP hello #{i}"),
                    DataLength = Encoding.UTF8.GetByteCount($"KCP hello #{i}"),
                };
                var buf = new byte[MessageCodec.EncodedSize(msg)];
                MessageCodec.Encode(msg, buf);
                transport.Send(buf, 0, buf.Length);
                Console.WriteLine($"  Sent #{i}");

                // Tick 收回复
                for (int j = 0; j < 20; j++)
                {
                    transport.Tick();
                    Thread.Sleep(20);
                }
            }

            // 最后再 Tick 一段时间
            for (int i = 0; i < 30; i++)
            {
                transport.Tick();
                Thread.Sleep(20);
            }

            if (recvCount == 3)
            {
                Console.WriteLine($"\n  PASS: Sent 3, Received {recvCount}");
                passed++;
            }
            else
            {
                Console.WriteLine($"\n  FAIL: Sent 3, Received {recvCount}");
                failed++;
            }

            transport.Disconnect();

            Console.WriteLine($"\n==========================================");
            Console.WriteLine($"  KCP Test Results: {passed} passed, {failed} failed");
            Console.WriteLine($"==========================================");

            if (failed > 0) Environment.Exit(1);
        }
    }
}
