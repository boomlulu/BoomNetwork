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

        [TearDown]
        public void TearDown()
        {
            _framing.Reset(); // 归还所有未消费的 pooled frames
        }

        [Test]
        public void Feed_SingleCompleteFrame()
        {
            var msg = MakeMessage(1, "test");
            var bytes = EncodeMessage(msg);

            int count = _framing.Feed(bytes, 0, bytes.Length);

            Assert.That(count, Is.EqualTo(1));
            Assert.That(_framing.TryDequeueFrame(out var frame), Is.True);
            using (frame)
            {
                var decoded = MessageCodec.Decode(frame.Span);
                Assert.That(decoded.Cmd, Is.EqualTo(1u));
                Assert.That(Encoding.UTF8.GetString(decoded.DataSpan), Is.EqualTo("test"));
            }
        }

        [Test]
        public void Feed_MultipleFramesAtOnce_Sticky()
        {
            var b1 = EncodeMessage(MakeMessage(1, "aaa"));
            var b2 = EncodeMessage(MakeMessage(2, "bbb"));
            var b3 = EncodeMessage(MakeMessage(3, "ccc"));

            var combined = new byte[b1.Length + b2.Length + b3.Length];
            Buffer.BlockCopy(b1, 0, combined, 0, b1.Length);
            Buffer.BlockCopy(b2, 0, combined, b1.Length, b2.Length);
            Buffer.BlockCopy(b3, 0, combined, b1.Length + b2.Length, b3.Length);

            int count = _framing.Feed(combined, 0, combined.Length);
            Assert.That(count, Is.EqualTo(3));

            AssertFrameCmd(1u);
            AssertFrameCmd(2u);
            AssertFrameCmd(3u);
        }

        [Test]
        public void Feed_SplitFrame_Unpacking()
        {
            var bytes = EncodeMessage(MakeMessage(13, "split-test-data"));
            int split = bytes.Length / 2;

            Assert.That(_framing.Feed(bytes, 0, split), Is.EqualTo(0));
            Assert.That(_framing.Feed(bytes, split, bytes.Length - split), Is.EqualTo(1));

            Assert.That(_framing.TryDequeueFrame(out var frame), Is.True);
            using (frame)
            {
                var decoded = MessageCodec.Decode(frame.Span);
                Assert.That(decoded.Cmd, Is.EqualTo((byte)13));
                Assert.That(Encoding.UTF8.GetString(decoded.DataSpan), Is.EqualTo("split-test-data"));
            }
        }

        [Test]
        public void Feed_ByteByByte()
        {
            var bytes = EncodeMessage(MakeMessage(7, "x"));

            for (int i = 0; i < bytes.Length - 1; i++)
            {
                Assert.That(_framing.Feed(bytes, i, 1), Is.EqualTo(0));
            }

            Assert.That(_framing.Feed(bytes, bytes.Length - 1, 1), Is.EqualTo(1));
            AssertFrameCmd(7u);
        }

        [Test]
        public void Feed_StickyAndSplit_Mixed()
        {
            var b1 = EncodeMessage(MakeMessage(1, "aa"));
            var b2 = EncodeMessage(MakeMessage(2, "bbbb"));

            var combined = new byte[b1.Length + b2.Length];
            Buffer.BlockCopy(b1, 0, combined, 0, b1.Length);
            Buffer.BlockCopy(b2, 0, combined, b1.Length, b2.Length);

            int cut = b1.Length + b2.Length / 2;
            Assert.That(_framing.Feed(combined, 0, cut), Is.EqualTo(1));
            Assert.That(_framing.Feed(combined, cut, combined.Length - cut), Is.EqualTo(1));

            AssertFrameCmd(1u);
            AssertFrameCmd(2u);
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
            var bytes = EncodeMessage(MakeMessage(1, "data"));
            _framing.Feed(bytes, 0, bytes.Length / 2);
            _framing.Reset();

            var bytes2 = EncodeMessage(MakeMessage(2, "fresh"));
            Assert.That(_framing.Feed(bytes2, 0, bytes2.Length), Is.EqualTo(1));
            AssertFrameCmd(2u);
        }

        private void AssertFrameCmd(uint expectedCmd)
        {
            Assert.That(_framing.TryDequeueFrame(out var frame), Is.True);
            using (frame)
            {
                var decoded = MessageCodec.Decode(frame.Span);
                Assert.That(decoded.Cmd, Is.EqualTo(expectedCmd));
            }
        }

        private static Message MakeMessage(byte cmd, string data)
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            return new Message
            {
                Cmd = cmd, Data = bytes, DataLength = bytes.Length,
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
