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

            // Case 1: Core — 空数据, 无 Seq
            WriteFixture("csharp_core.bin", new Message
            {
                MsgType = CmdType.Core, Cmd = 7,
                HasSeq = false, Data = Array.Empty<byte>(),
            });

            // Case 2: Extended — 有数据
            var extData = Encoding.UTF8.GetBytes("ext data");
            WriteFixture("csharp_ext.bin", new Message
            {
                MsgType = CmdType.Extended, ExtCmd = 42,
                Data = extData, DataLength = extData.Length,
            });

            // Case 3: Game — 有数据
            var gameData = Encoding.UTF8.GetBytes("game data");
            WriteFixture("csharp_game.bin", new Message
            {
                MsgType = CmdType.Game, GameCmd = 1001,
                Data = gameData, DataLength = gameData.Length,
            });

            Assert.Pass($"Generated 3 fixtures in {TestDataDir}");
        }

        [Test]
        public void VerifyGoFixtures()
        {
            // go_core.bin: Core Cmd=7, no data
            var msg1 = ReadFixture("go_core.bin");
            Assert.That(msg1.MsgType, Is.EqualTo(CmdType.Core));
            Assert.That(msg1.Cmd, Is.EqualTo((byte)7));
            Assert.That(msg1.DataLength, Is.EqualTo(0));

            // go_ext.bin: Extended ExtCmd=42, "ext data"
            var msg2 = ReadFixture("go_ext.bin");
            Assert.That(msg2.MsgType, Is.EqualTo(CmdType.Extended));
            Assert.That(msg2.ExtCmd, Is.EqualTo((ushort)42));
            Assert.That(Encoding.UTF8.GetString(msg2.DataSpan), Is.EqualTo("ext data"));

            // go_game.bin: Game GameCmd=1001, "game data"
            var msg3 = ReadFixture("go_game.bin");
            Assert.That(msg3.MsgType, Is.EqualTo(CmdType.Game));
            Assert.That(msg3.GameCmd, Is.EqualTo(1001u));
            Assert.That(Encoding.UTF8.GetString(msg3.DataSpan), Is.EqualTo("game data"));

            // go_large.bin: Core Cmd=5, HasSeq, 70000 bytes
            var msg4 = ReadFixture("go_large.bin");
            Assert.That(msg4.Cmd, Is.EqualTo((byte)5));
            Assert.That(msg4.Seq, Is.EqualTo(999));
            Assert.That(msg4.DataLength, Is.EqualTo(70000));
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
