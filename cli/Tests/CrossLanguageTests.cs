using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using BoomNetwork.Core;
using BoomNetwork.Core.Codec;

namespace BoomNetwork.Tests
{
    [TestFixture]
    public class CrossLanguageTests
    {
        private static readonly string TestDataDir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "testdata"));

        [Test]
        public void GenerateCSharpFixtures()
        {
            Directory.CreateDirectory(TestDataDir);

            // Case 1: 空数据, 无 Seq
            WriteFixture("csharp_empty.bin", new Message
            {
                Cmd = 42, HasSeq = false, Data = Array.Empty<byte>(),
            });

            // Case 2: 有数据, 有 Seq
            var hello = Encoding.UTF8.GetBytes("Hello from C#");
            WriteFixture("csharp_hello.bin", new Message
            {
                Cmd = 10, HasSeq = true, Seq = 12345,
                Data = hello, DataLength = hello.Length,
            });

            // Case 3: 大包 (4B BodyLen)
            var largeData = new byte[70000];
            new Random(42).NextBytes(largeData);
            WriteFixture("csharp_large.bin", new Message
            {
                Cmd = 5, HasSeq = true, Seq = 999,
                Data = largeData, DataLength = largeData.Length,
            });

            Assert.Pass($"Generated 3 fixtures in {TestDataDir}");
        }

        [Test]
        public void VerifyGoFixtures()
        {
            var msg1 = ReadFixture("go_empty.bin");
            Assert.That(msg1.Cmd, Is.EqualTo((byte)42));
            Assert.That(msg1.HasSeq, Is.False);
            Assert.That(msg1.DataLength, Is.EqualTo(0));

            var msg2 = ReadFixture("go_hello.bin");
            Assert.That(msg2.Cmd, Is.EqualTo((byte)10));
            Assert.That(msg2.HasSeq, Is.True);
            Assert.That(msg2.Seq, Is.EqualTo(12345));
            Assert.That(Encoding.UTF8.GetString(msg2.DataSpan), Is.EqualTo("Hello from Go"));

            var msg3 = ReadFixture("go_large.bin");
            Assert.That(msg3.Cmd, Is.EqualTo((byte)5));
            Assert.That(msg3.Seq, Is.EqualTo(999));
            Assert.That(msg3.DataLength, Is.EqualTo(70000));
        }

        private void WriteFixture(string filename, Message msg)
        {
            var buf = new byte[MessageCodec.EncodedSize(msg)];
            MessageCodec.Encode(msg, buf);
            File.WriteAllBytes(Path.Combine(TestDataDir, filename), buf);
        }

        private Message ReadFixture(string filename)
        {
            var path = Path.Combine(TestDataDir, filename);
            if (!File.Exists(path))
                Assert.Ignore($"Fixture not found: {path} (run Go generate first)");
            var bytes = File.ReadAllBytes(path);
            return MessageCodec.Decode(bytes);
        }
    }
}
