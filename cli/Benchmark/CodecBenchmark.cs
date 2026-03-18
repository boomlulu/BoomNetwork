using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using BoomNetwork.Core;
using BoomNetwork.Core.Codec;
using BoomNetwork.Core.Framing;

namespace BoomNetwork.Benchmark
{
    [MemoryDiagnoser]
    public class CodecBenchmark
    {
        private Message _smallMsg;
        private Message _largeMsg;
        private byte[] _smallEncoded = null!;
        private byte[] _largeEncoded = null!;
        private byte[] _encodeBuf = null!;

        [GlobalSetup]
        public void Setup()
        {
            var smallData = Encoding.UTF8.GetBytes("Hello BoomNetwork benchmark test payload!");
            _smallMsg = new Message
            {
                Cmd = 10, HasSeq = true, Seq = 12345,
                Data = smallData, DataLength = smallData.Length,
            };
            _largeMsg = new Message
            {
                Cmd = 20, HasSeq = false,
                Data = new byte[1024], DataLength = 1024,
            };

            _smallEncoded = new byte[MessageCodec.EncodedSize(_smallMsg)];
            MessageCodec.Encode(_smallMsg, _smallEncoded);

            _largeEncoded = new byte[MessageCodec.EncodedSize(_largeMsg)];
            MessageCodec.Encode(_largeMsg, _largeEncoded);

            _encodeBuf = new byte[4096];
        }

        [Benchmark]
        public int Encode_Small() => MessageCodec.Encode(_smallMsg, _encodeBuf);

        [Benchmark]
        public int Encode_Large() => MessageCodec.Encode(_largeMsg, _encodeBuf);

        [Benchmark]
        public Message Decode_Small() => MessageCodec.Decode(_smallEncoded);

        [Benchmark]
        public Message Decode_Large() => MessageCodec.Decode(_largeEncoded);

        [Benchmark]
        public Message Decode_Small_Pooled()
        {
            var msg = MessageCodec.Decode(_smallEncoded, usePool: true);
            MessageCodec.ReturnData(ref msg);
            return msg;
        }
    }

    [MemoryDiagnoser]
    public class FramingBenchmark
    {
        private byte[] _stickyData = null!;
        private LengthPrefixFraming _framing = null!;

        [GlobalSetup]
        public void Setup()
        {
            _framing = new LengthPrefixFraming();

            var data = Encoding.UTF8.GetBytes("benchmark payload");
            var msg = new Message
            {
                Cmd = 1, Data = data, DataLength = data.Length,
            };

            int frameSize = MessageCodec.EncodedSize(msg);
            _stickyData = new byte[frameSize * 100];
            var buf = new byte[frameSize];
            for (int i = 0; i < 100; i++)
            {
                MessageCodec.Encode(msg, buf);
                Buffer.BlockCopy(buf, 0, _stickyData, i * frameSize, frameSize);
            }
        }

        [Benchmark]
        public int Feed_100_StickyMessages()
        {
            _framing.Reset();
            int count = _framing.Feed(_stickyData, 0, _stickyData.Length);
            while (_framing.TryDequeueFrame(out var frame))
                frame.Dispose();
            return count;
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            BenchmarkRunner.Run<CodecBenchmark>();
            BenchmarkRunner.Run<FramingBenchmark>();
        }
    }
}
