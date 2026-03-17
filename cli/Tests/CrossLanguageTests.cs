using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using BoomNetwork.Core;
using BoomNetwork.Core.Codec;

namespace BoomNetwork.Tests
{
    /// <summary>
    /// 跨语言兼容测试
    /// 1. C# 编码 → 写文件 → Go 读文件验证
    /// 2. Go 编码 → 写文件 → C# 读文件验证
    /// </summary>
    [TestFixture]
    public class CrossLanguageTests
    {
        private static readonly string TestDataDir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "testdata"));

        /// <summary>
        /// C# 生成测试 fixture 文件，供 Go 端读取验证
        /// </summary>
        [Test]
        public void GenerateCSharpFixtures()
        {
            Directory.CreateDirectory(TestDataDir);

            // Case 1: 空数据
            WriteFixture("csharp_empty.bin", new Message
            {
                Version = 1, Cmd = 42, ClientSeq = 10, ServerSeq = 20,
                Data = Array.Empty<byte>(),
            });

            // Case 2: 有数据
            WriteFixture("csharp_hello.bin", new Message
            {
                Version = 0, Cmd = 100, ClientSeq = 1, ServerSeq = 2,
                Data = Encoding.UTF8.GetBytes("Hello from C#"),
            });

            // Case 3: 极值
            WriteFixture("csharp_extreme.bin", new Message
            {
                Version = 255, Cmd = uint.MaxValue,
                ClientSeq = int.MaxValue, ServerSeq = int.MinValue,
                Data = new byte[] { 0xFF, 0x00, 0xAB, 0xCD },
            });

            Assert.Pass($"Generated 3 fixtures in {TestDataDir}");
        }

        /// <summary>
        /// 读取 Go 生成的 fixture 文件，验证 C# 解码正确
        /// </summary>
        [Test]
        public void VerifyGoFixtures()
        {
            // Case 1: 空数据
            var msg1 = ReadFixture("go_empty.bin");
            Assert.That(msg1.Version, Is.EqualTo((byte)1));
            Assert.That(msg1.Cmd, Is.EqualTo(42u));
            Assert.That(msg1.ClientSeq, Is.EqualTo(10));
            Assert.That(msg1.ServerSeq, Is.EqualTo(20));
            Assert.That(msg1.Data.Length, Is.EqualTo(0));

            // Case 2: 有数据
            var msg2 = ReadFixture("go_hello.bin");
            Assert.That(msg2.Cmd, Is.EqualTo(100u));
            Assert.That(Encoding.UTF8.GetString(msg2.Data), Is.EqualTo("Hello from Go"));

            // Case 3: 极值
            var msg3 = ReadFixture("go_extreme.bin");
            Assert.That(msg3.Version, Is.EqualTo((byte)255));
            Assert.That(msg3.Cmd, Is.EqualTo(uint.MaxValue));
            Assert.That(msg3.ClientSeq, Is.EqualTo(int.MaxValue));
            Assert.That(msg3.ServerSeq, Is.EqualTo(int.MinValue));
            Assert.That(msg3.Data, Is.EqualTo(new byte[] { 0xFF, 0x00, 0xAB, 0xCD }));
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
