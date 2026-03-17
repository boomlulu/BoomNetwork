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
        public void Encode_Decode_EmptyData()
        {
            var msg = new Message
            {
                Version = 1,
                Cmd = 42,
                ClientSeq = 10,
                ServerSeq = 20,
                Data = Array.Empty<byte>(),
            };

            var buf = new byte[MessageCodec.EncodedSize(msg)];
            int written = MessageCodec.Encode(msg, buf);

            Assert.That(written, Is.EqualTo(Message.HeaderSize + Message.BodyHeaderSize));

            var decoded = MessageCodec.Decode(buf);
            Assert.That(decoded.Version, Is.EqualTo(msg.Version));
            Assert.That(decoded.Cmd, Is.EqualTo(msg.Cmd));
            Assert.That(decoded.ClientSeq, Is.EqualTo(msg.ClientSeq));
            Assert.That(decoded.ServerSeq, Is.EqualTo(msg.ServerSeq));
            Assert.That(decoded.Data.Length, Is.EqualTo(0));
        }

        [Test]
        public void Encode_Decode_WithData()
        {
            var payload = Encoding.UTF8.GetBytes("Hello BoomNetwork!");
            var msg = new Message
            {
                Version = 0,
                Cmd = 100,
                ClientSeq = 1,
                ServerSeq = 2,
                Data = payload,
            };

            var buf = new byte[MessageCodec.EncodedSize(msg)];
            MessageCodec.Encode(msg, buf);

            var decoded = MessageCodec.Decode(buf);
            Assert.That(decoded.Cmd, Is.EqualTo(100u));
            Assert.That(decoded.Data, Is.EqualTo(payload));
        }

        [Test]
        public void Encode_Decode_LargeSeqValues()
        {
            var msg = new Message
            {
                Version = 255,
                Cmd = uint.MaxValue,
                ClientSeq = int.MaxValue,
                ServerSeq = int.MinValue,
                Data = new byte[] { 0xFF, 0x00, 0xAB },
            };

            var buf = new byte[MessageCodec.EncodedSize(msg)];
            MessageCodec.Encode(msg, buf);

            var decoded = MessageCodec.Decode(buf);
            Assert.That(decoded.Version, Is.EqualTo(255));
            Assert.That(decoded.Cmd, Is.EqualTo(uint.MaxValue));
            Assert.That(decoded.ClientSeq, Is.EqualTo(int.MaxValue));
            Assert.That(decoded.ServerSeq, Is.EqualTo(int.MinValue));
            Assert.That(decoded.Data, Is.EqualTo(new byte[] { 0xFF, 0x00, 0xAB }));
        }

        [Test]
        public void EncodedSize_MatchesActualOutput()
        {
            var msg = new Message
            {
                Version = 0,
                Cmd = 1,
                ClientSeq = 0,
                ServerSeq = 0,
                Data = new byte[128],
            };

            int predicted = MessageCodec.EncodedSize(msg);
            var buf = new byte[predicted];
            int actual = MessageCodec.Encode(msg, buf);

            Assert.That(actual, Is.EqualTo(predicted));
        }

        [Test]
        public void Encode_BufferTooSmall_Throws()
        {
            var msg = new Message { Data = new byte[100] };
            var buf = new byte[10]; // too small

            Assert.Throws<ArgumentException>(() => MessageCodec.Encode(msg, buf));
        }

        [Test]
        public void Decode_BufferTooShort_Throws()
        {
            Assert.Throws<ArgumentException>(() => MessageCodec.Decode(new byte[5]));
        }

        [Test]
        public void NullData_TreatedAsEmpty()
        {
            var msg = new Message
            {
                Version = 0,
                Cmd = 1,
                ClientSeq = 0,
                ServerSeq = 0,
                Data = null!,
            };

            var buf = new byte[MessageCodec.EncodedSize(msg)];
            MessageCodec.Encode(msg, buf);

            var decoded = MessageCodec.Decode(buf);
            Assert.That(decoded.Data.Length, Is.EqualTo(0));
        }
    }
}
