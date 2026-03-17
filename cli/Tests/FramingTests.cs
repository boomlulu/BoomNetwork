using System;
using System.Text;
using NUnit.Framework;
using BoomNetwork.Core;
using BoomNetwork.Core.Codec;
using BoomNetwork.Core.Framing;

namespace BoomNetwork.Tests
{
    [TestFixture]
    public class FramingTests
    {
        private LengthPrefixFraming _framing = null!;

        [SetUp]
        public void SetUp()
        {
            _framing = new LengthPrefixFraming();
        }

        [Test]
        public void Feed_SingleCompleteFrame()
        {
            var msg = MakeMessage(1, "test");
            var bytes = EncodeMessage(msg);

            int count = _framing.Feed(bytes, 0, bytes.Length);

            Assert.That(count, Is.EqualTo(1));
            Assert.That(_framing.TryDequeueFrame(out var frame), Is.True);

            var decoded = MessageCodec.Decode(frame);
            Assert.That(decoded.Cmd, Is.EqualTo(1u));
            Assert.That(Encoding.UTF8.GetString(decoded.Data), Is.EqualTo("test"));
        }

        [Test]
        public void Feed_MultipleFramesAtOnce_Sticky()
        {
            var msg1 = MakeMessage(1, "aaa");
            var msg2 = MakeMessage(2, "bbb");
            var msg3 = MakeMessage(3, "ccc");

            var b1 = EncodeMessage(msg1);
            var b2 = EncodeMessage(msg2);
            var b3 = EncodeMessage(msg3);

            // 粘包：三条消息合并成一个 byte[]
            var combined = new byte[b1.Length + b2.Length + b3.Length];
            Buffer.BlockCopy(b1, 0, combined, 0, b1.Length);
            Buffer.BlockCopy(b2, 0, combined, b1.Length, b2.Length);
            Buffer.BlockCopy(b3, 0, combined, b1.Length + b2.Length, b3.Length);

            int count = _framing.Feed(combined, 0, combined.Length);
            Assert.That(count, Is.EqualTo(3));

            _framing.TryDequeueFrame(out var f1);
            _framing.TryDequeueFrame(out var f2);
            _framing.TryDequeueFrame(out var f3);

            Assert.That(MessageCodec.Decode(f1).Cmd, Is.EqualTo(1u));
            Assert.That(MessageCodec.Decode(f2).Cmd, Is.EqualTo(2u));
            Assert.That(MessageCodec.Decode(f3).Cmd, Is.EqualTo(3u));
        }

        [Test]
        public void Feed_SplitFrame_Unpacking()
        {
            var msg = MakeMessage(99, "split-test-data");
            var bytes = EncodeMessage(msg);

            // 拆包：把一条消息分两次 Feed
            int split = bytes.Length / 2;

            int count1 = _framing.Feed(bytes, 0, split);
            Assert.That(count1, Is.EqualTo(0)); // 数据不完整

            int count2 = _framing.Feed(bytes, split, bytes.Length - split);
            Assert.That(count2, Is.EqualTo(1)); // 现在完整了

            _framing.TryDequeueFrame(out var frame);
            var decoded = MessageCodec.Decode(frame);
            Assert.That(decoded.Cmd, Is.EqualTo(99u));
            Assert.That(Encoding.UTF8.GetString(decoded.Data), Is.EqualTo("split-test-data"));
        }

        [Test]
        public void Feed_ByteByByte()
        {
            var msg = MakeMessage(7, "x");
            var bytes = EncodeMessage(msg);

            // 极端拆包：逐字节喂入
            for (int i = 0; i < bytes.Length - 1; i++)
            {
                int count = _framing.Feed(bytes, i, 1);
                Assert.That(count, Is.EqualTo(0));
            }

            int last = _framing.Feed(bytes, bytes.Length - 1, 1);
            Assert.That(last, Is.EqualTo(1));

            _framing.TryDequeueFrame(out var frame);
            Assert.That(MessageCodec.Decode(frame).Cmd, Is.EqualTo(7u));
        }

        [Test]
        public void Feed_StickyAndSplit_Mixed()
        {
            var msg1 = MakeMessage(1, "aa");
            var msg2 = MakeMessage(2, "bbbb");
            var b1 = EncodeMessage(msg1);
            var b2 = EncodeMessage(msg2);

            var combined = new byte[b1.Length + b2.Length];
            Buffer.BlockCopy(b1, 0, combined, 0, b1.Length);
            Buffer.BlockCopy(b2, 0, combined, b1.Length, b2.Length);

            // 第一次喂入：完整的 msg1 + msg2 的前半部分
            int cut = b1.Length + b2.Length / 2;
            int count1 = _framing.Feed(combined, 0, cut);
            Assert.That(count1, Is.EqualTo(1)); // 只有 msg1 完整

            // 第二次喂入：msg2 的后半部分
            int count2 = _framing.Feed(combined, cut, combined.Length - cut);
            Assert.That(count2, Is.EqualTo(1)); // msg2 完整了

            _framing.TryDequeueFrame(out var f1);
            _framing.TryDequeueFrame(out var f2);
            Assert.That(MessageCodec.Decode(f1).Cmd, Is.EqualTo(1u));
            Assert.That(MessageCodec.Decode(f2).Cmd, Is.EqualTo(2u));
        }

        [Test]
        public void Feed_EmptyData_NoFrames()
        {
            int count = _framing.Feed(Array.Empty<byte>(), 0, 0);
            Assert.That(count, Is.EqualTo(0));
            Assert.That(_framing.TryDequeueFrame(out _), Is.False);
        }

        [Test]
        public void Reset_ClearsState()
        {
            var msg = MakeMessage(1, "data");
            var bytes = EncodeMessage(msg);

            // 喂入不完整数据
            _framing.Feed(bytes, 0, bytes.Length / 2);
            _framing.Reset();

            // 喂入完整数据（不应受之前残留影响）
            var msg2 = MakeMessage(2, "fresh");
            var bytes2 = EncodeMessage(msg2);
            int count = _framing.Feed(bytes2, 0, bytes2.Length);

            Assert.That(count, Is.EqualTo(1));
            _framing.TryDequeueFrame(out var frame);
            Assert.That(MessageCodec.Decode(frame).Cmd, Is.EqualTo(2u));
        }

        private static Message MakeMessage(uint cmd, string data)
        {
            return new Message
            {
                Version = 0,
                Cmd = cmd,
                ClientSeq = 0,
                ServerSeq = 0,
                Data = Encoding.UTF8.GetBytes(data),
            };
        }

        private static byte[] EncodeMessage(Message msg)
        {
            var buf = new byte[MessageCodec.EncodedSize(msg)];
            MessageCodec.Encode(msg, buf);
            return buf;
        }
    }
}
