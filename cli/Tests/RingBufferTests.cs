using System;
using NUnit.Framework;
using BoomNetwork.Core.Framing;

namespace BoomNetwork.Tests
{
    [TestFixture]
    public class RingBufferTests
    {
        [Test]
        public void Empty_ReadableBytes_IsZero()
        {
            var rb = new RingBuffer(64);
            Assert.That(rb.ReadableBytes, Is.EqualTo(0));
        }

        [Test]
        public void Write_Then_Read()
        {
            var rb = new RingBuffer(64);
            var data = new byte[] { 1, 2, 3, 4, 5 };
            rb.Write(data, 0, data.Length);

            Assert.That(rb.ReadableBytes, Is.EqualTo(5));
            var span = rb.ReadableSpan;
            Assert.That(span[0], Is.EqualTo(1));
            Assert.That(span[4], Is.EqualTo(5));
        }

        [Test]
        public void Consume_Reduces_ReadableBytes()
        {
            var rb = new RingBuffer(64);
            rb.Write(new byte[] { 10, 20, 30 }, 0, 3);
            rb.Consume(2);

            Assert.That(rb.ReadableBytes, Is.EqualTo(1));
            Assert.That(rb.ReadableSpan[0], Is.EqualTo(30));
        }

        [Test]
        public void Consume_All_Resets_Positions()
        {
            var rb = new RingBuffer(64);
            rb.Write(new byte[] { 1, 2, 3 }, 0, 3);
            rb.Consume(3);

            Assert.That(rb.ReadableBytes, Is.EqualTo(0));
            // After consuming all, positions reset — full capacity available
            Assert.That(rb.WritableBytes, Is.EqualTo(64));
        }

        [Test]
        public void Reset_ClearsAll()
        {
            var rb = new RingBuffer(64);
            rb.Write(new byte[] { 1, 2, 3 }, 0, 3);
            rb.Reset();

            Assert.That(rb.ReadableBytes, Is.EqualTo(0));
            Assert.That(rb.WritableBytes, Is.EqualTo(64));
        }

        [Test]
        public void Compact_OnWrite_RecoversSpace()
        {
            var rb = new RingBuffer(16);
            // Fill 12 bytes
            rb.Write(new byte[12], 0, 12);
            // Consume 10 — leaves 2 readable, readPos=10
            rb.Consume(10);
            Assert.That(rb.ReadableBytes, Is.EqualTo(2));
            Assert.That(rb.WritableBytes, Is.EqualTo(4)); // 16 - 12 = 4

            // Write 8 bytes — needs compact to reclaim the 10 consumed bytes
            rb.Write(new byte[8], 0, 8);
            Assert.That(rb.ReadableBytes, Is.EqualTo(10)); // 2 + 8
        }

        [Test]
        public void Grow_WhenCompactNotEnough()
        {
            var rb = new RingBuffer(8);
            // Fill to capacity
            rb.Write(new byte[8], 0, 8);
            // Write more — must grow (no consumed space to reclaim)
            rb.Write(new byte[4], 0, 4);
            Assert.That(rb.ReadableBytes, Is.EqualTo(12));
        }

        [Test]
        public void MultipleWriteRead_Cycles()
        {
            var rb = new RingBuffer(32);
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var data = new byte[] { (byte)(cycle & 0xFF) };
                rb.Write(data, 0, 1);
                Assert.That(rb.ReadableBytes, Is.EqualTo(1));
                Assert.That(rb.ReadableSpan[0], Is.EqualTo((byte)(cycle & 0xFF)));
                rb.Consume(1);
                Assert.That(rb.ReadableBytes, Is.EqualTo(0));
            }
        }

        [Test]
        public void Write_WithOffset()
        {
            var rb = new RingBuffer(64);
            var data = new byte[] { 0, 0, 42, 43, 44 };
            rb.Write(data, 2, 3); // write bytes at offset 2, length 3

            Assert.That(rb.ReadableBytes, Is.EqualTo(3));
            Assert.That(rb.ReadableSpan[0], Is.EqualTo(42));
            Assert.That(rb.ReadableSpan[2], Is.EqualTo(44));
        }
    }
}
