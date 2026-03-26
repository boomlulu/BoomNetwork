using System;
using System.Text;
using NUnit.Framework;
using BoomNetwork.Core;
using BoomNetwork.Core.Codec;

namespace BoomNetwork.Tests
{
    [TestFixture]
    public class CodecTests
    {
        [Test]
        public void Encode_Decode_EmptyData_NoSeq()
        {
            var msg = new Message
            {
                Cmd = 10,
                HasSeq = false,
                Data = Array.Empty<byte>(),
                DataLength = 0,
            };

            var buf = new byte[MessageCodec.EncodedSize(msg)];
            int written = MessageCodec.Encode(msg, buf);

            Assert.That(written, Is.EqualTo(3)); // FlagsCmd(1) + BodyLen(2) = 3

            var decoded = MessageCodec.Decode(buf);
            Assert.That(decoded.Cmd, Is.EqualTo((byte)10));
            Assert.That(decoded.HasSeq, Is.False);
            Assert.That(decoded.DataLength, Is.EqualTo(0));
        }

        [Test]
        public void Encode_Decode_WithData_WithSeq()
        {
            var payload = Encoding.UTF8.GetBytes("Hello BoomNetwork!");
            var msg = new Message
            {
                Cmd = 10,
                HasSeq = true,
                Seq = 12345,
                Data = payload,
                DataLength = payload.Length,
            };

            var buf = new byte[MessageCodec.EncodedSize(msg)];
            MessageCodec.Encode(msg, buf);

            var decoded = MessageCodec.Decode(buf);
            Assert.That(decoded.Cmd, Is.EqualTo((byte)10));
            Assert.That(decoded.HasSeq, Is.True);
            Assert.That(decoded.Seq, Is.EqualTo(12345));
            Assert.That(decoded.DataSpan.ToArray(), Is.EqualTo(payload));
        }

        [Test]
        public void Encode_Decode_LargeData_LargeLen()
        {
            // Data > 65530 triggers 4-byte BodyLen
            var largeData = new byte[70000];
            new Random(42).NextBytes(largeData);
            var msg = new Message
            {
                Cmd = 5,
                HasSeq = true,
                Seq = 999,
                Data = largeData,
                DataLength = largeData.Length,
            };

            var buf = new byte[MessageCodec.EncodedSize(msg)];
            int written = MessageCodec.Encode(msg, buf);

            // FlagsCmd(1) + BodyLen(4) + Seq(4) + Data(70000) = 70009
            Assert.That(written, Is.EqualTo(70009));

            var decoded = MessageCodec.Decode(buf);
            Assert.That(decoded.Cmd, Is.EqualTo((byte)5));
            Assert.That(decoded.Seq, Is.EqualTo(999));
            Assert.That(decoded.DataLength, Is.EqualTo(70000));
        }

        [Test]
        public void Encode_Decode_MaxCmd()
        {
            var msg = new Message { Cmd = 15, Data = Array.Empty<byte>() }; // Core Cmd 4-bit max = 15
            var buf = new byte[MessageCodec.EncodedSize(msg)];
            MessageCodec.Encode(msg, buf);
            var decoded = MessageCodec.Decode(buf);
            Assert.That(decoded.Cmd, Is.EqualTo((byte)15));
        }

        [Test]
        public void EncodedSize_MatchesActualOutput()
        {
            var msg = new Message
            {
                Cmd = 1,
                HasSeq = true,
                Seq = 1,
                Data = new byte[128],
                DataLength = 128,
            };

            int predicted = MessageCodec.EncodedSize(msg);
            var buf = new byte[predicted];
            int actual = MessageCodec.Encode(msg, buf);
            Assert.That(actual, Is.EqualTo(predicted));
        }

        [Test]
        public void Encode_BufferTooSmall_Throws()
        {
            var msg = new Message { Cmd = 1, Data = new byte[100], DataLength = 100 };
            var buf = new byte[10];
            Assert.Throws<ArgumentException>(() => MessageCodec.Encode(msg, buf));
        }

        [Test]
        public void Decode_BufferTooShort_Throws()
        {
            Assert.Throws<ArgumentException>(() => MessageCodec.Decode(new byte[1]));
        }

        [Test]
        public void PeekFrameSize_Works()
        {
            var msg = new Message { Cmd = 1, HasSeq = true, Seq = 5, Data = new byte[20], DataLength = 20 };
            var buf = new byte[MessageCodec.EncodedSize(msg)];
            MessageCodec.Encode(msg, buf);

            int size = MessageCodec.PeekFrameSize(buf);
            Assert.That(size, Is.EqualTo(buf.Length));
        }

        [Test]
        public void PeekFrameSize_InsufficientData_ReturnsNeg()
        {
            Assert.That(MessageCodec.PeekFrameSize(Array.Empty<byte>()), Is.EqualTo(-1));
            Assert.That(MessageCodec.PeekFrameSize(new byte[1]), Is.EqualTo(-1));
        }

        [Test]
        public void HeaderSize_Variants()
        {
            // No Seq, small len: 1 + 2 = 3
            var m1 = new Message { Cmd = 1, Data = Array.Empty<byte>() };
            Assert.That(m1.HeaderSize, Is.EqualTo(3));

            // With Seq, small len: 1 + 2 + 4 = 7
            var m2 = new Message { Cmd = 1, HasSeq = true, Seq = 1, Data = Array.Empty<byte>() };
            Assert.That(m2.HeaderSize, Is.EqualTo(7));

            // No Seq, large len: 1 + 4 = 5
            var m3 = new Message { Cmd = 1, Data = new byte[70000], DataLength = 70000 };
            Assert.That(m3.HeaderSize, Is.EqualTo(5));

            // With Seq, large len: 1 + 4 + 4 = 9
            var m4 = new Message { Cmd = 1, HasSeq = true, Seq = 1, Data = new byte[70000], DataLength = 70000 };
            Assert.That(m4.HeaderSize, Is.EqualTo(9));
        }
    }
}
