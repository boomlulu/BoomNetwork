using System;
using System.Buffers.Binary;
using System.IO;
using NUnit.Framework;
using BoomNetwork.Core.FrameSync;

namespace BoomNetwork.Tests
{
    /// <summary>
    /// C3 修复测试：FrameSyncProtocol 解码方法边界检查
    ///
    /// 测试策略（每个解码方法均覆盖）：
    ///   1. Valid roundtrip — 合法数据正常解码
    ///   2. Empty / too short — 空缓冲区或不足最小长度抛 InvalidDataException
    ///   3. Truncated mid-loop — 循环内读取时截断抛 InvalidDataException
    ///   4. Oversized count / length — 超上限拒绝，防 OOM
    /// </summary>
    [TestFixture]
    public class ProtocolBoundsTests
    {
        // ─── FrameDataCodec ───────────────────────────────────────────────────────

        [Test]
        public void FrameData_Valid_Roundtrip()
        {
            var frame = new FrameData
            {
                FrameNumber = 999,
                Inputs = new[]
                {
                    new FrameData.PlayerInput { PlayerId = 1, Data = new byte[] { 0xAA, 0xBB }, DataLength = 2 },
                    new FrameData.PlayerInput { PlayerId = 2, Data = Array.Empty<byte>(), DataLength = 0 },
                },
                Events = new[]
                {
                    new FrameEvent { EventType = 1, PlayerId = 42 },
                }
            };

            // Allocate generous buffer; Encode returns bytes written
            var scratch = new byte[512];
            int written = FrameDataCodec.Encode(frame, scratch);
            var decoded = FrameDataCodec.Decode(scratch.AsSpan(0, written));

            Assert.That(decoded.FrameNumber, Is.EqualTo(999u));
            Assert.That(decoded.Inputs.Length, Is.EqualTo(2));
            Assert.That(decoded.Inputs[0].PlayerId, Is.EqualTo(1));
            Assert.That(decoded.Inputs[0].DataLength, Is.EqualTo(2));
            Assert.That(decoded.Events.Length, Is.EqualTo(1));
            Assert.That(decoded.Events[0].EventType, Is.EqualTo(1));
            Assert.That(decoded.Events[0].PlayerId, Is.EqualTo(42));
        }

        [Test]
        public void FrameData_EmptyBuffer_Throws()
        {
            Assert.Throws<InvalidDataException>(() => FrameDataCodec.Decode(Array.Empty<byte>()));
        }

        [Test]
        public void FrameData_TooShort_Throws()
        {
            // Need at least 6 bytes
            Assert.Throws<InvalidDataException>(() => FrameDataCodec.Decode(new byte[5]));
        }

        [Test]
        public void FrameData_TruncatedInputHeader_Throws()
        {
            // inputCount=1 but no room for input header (needs 6 more bytes)
            var buf = new byte[7]; // 4(frame) + 2(count) + 1 byte only
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 1u);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 1); // inputCount=1
            Assert.Throws<InvalidDataException>(() => FrameDataCodec.Decode(buf));
        }

        [Test]
        public void FrameData_TruncatedInputData_Throws()
        {
            // inputCount=1, dataLen=100, but buffer too short
            var buf = new byte[12]; // 4+2 + 4+2 = 12 (no data bytes)
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 1u);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 1);   // inputCount=1
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(6), 1);    // playerId
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), 100); // dataLen=100
            Assert.Throws<InvalidDataException>(() => FrameDataCodec.Decode(buf));
        }

        [Test]
        public void FrameData_OversizedInputCount_Throws()
        {
            // inputCount = 257 (> MaxFrameInputs=256)
            var buf = new byte[6];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 1u);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 257);
            Assert.Throws<InvalidDataException>(() => FrameDataCodec.Decode(buf));
        }

        [Test]
        public void FrameData_OversizedDataLen_Throws()
        {
            // dataLen = 4097 (> MaxInputDataLen=4096)
            var buf = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 1u);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 1);    // inputCount=1
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(6), 1);     // playerId
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), 4097); // dataLen=4097
            Assert.Throws<InvalidDataException>(() => FrameDataCodec.Decode(buf));
        }

        [Test]
        public void FrameData_OversizedEventCount_Throws()
        {
            // No inputs, eventCount = 33 (> MaxFrameEvents=32)
            var buf = new byte[7];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 1u);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 0); // inputCount=0
            buf[6] = 33; // eventCount=33
            Assert.Throws<InvalidDataException>(() => FrameDataCodec.Decode(buf));
        }

        [Test]
        public void FrameData_TruncatedEvent_Throws()
        {
            // No inputs, eventCount=1, but only 3 bytes for event (need 5)
            var buf = new byte[9]; // 4+2+1+2 bytes
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 1u);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 0); // inputCount=0
            buf[6] = 1; // eventCount=1
            buf[7] = 1; // EventType
            // only 1 byte after eventType instead of 4 for PlayerId
            Assert.Throws<InvalidDataException>(() => FrameDataCodec.Decode(buf));
        }

        // ─── FrameHashMismatch ────────────────────────────────────────────────────

        [Test]
        public void FrameHashMismatch_Valid_Roundtrip()
        {
            // Wire: [FrameNumber:4][Count:1][PlayerId:4][Hash:4]...
            var buf = new byte[5 + 8]; // 1 player
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 77u);
            buf[4] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(5), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(9), 0xDEADBEEFu);
            var result = FrameHashMismatch.Decode(buf);
            Assert.That(result.FrameNumber, Is.EqualTo(77u));
            Assert.That(result.PlayerHashes.Length, Is.EqualTo(1));
            Assert.That(result.PlayerHashes[0].PlayerId, Is.EqualTo(3));
            Assert.That(result.PlayerHashes[0].Hash, Is.EqualTo(0xDEADBEEFu));
        }

        [Test]
        public void FrameHashMismatch_TooShort_Throws()
        {
            Assert.Throws<InvalidDataException>(() => FrameHashMismatch.Decode(new byte[4]));
        }

        [Test]
        public void FrameHashMismatch_TruncatedPlayerData_Throws()
        {
            // count=1 but buffer ends before 8 bytes of player data
            var buf = new byte[9]; // 5 header + 4 (need 8)
            buf[4] = 1;
            Assert.Throws<InvalidDataException>(() => FrameHashMismatch.Decode(buf));
        }

        // ─── FrameSyncInitData ────────────────────────────────────────────────────

        [Test]
        public void FrameSyncInitData_TooShort_Throws()
        {
            Assert.Throws<InvalidDataException>(() =>
            {
                var span = new ReadOnlySpan<byte>(new byte[10]);
                FrameSyncInitData.ReadFrom(span);
            });
        }

        // ─── RoomCodec ────────────────────────────────────────────────────────────

        [Test]
        public void RoomList_EmptyBuffer_ReturnsEmpty()
        {
            // An empty payload is treated as an empty room list (not an error)
            var rooms = RoomCodec.DecodeRoomList(Array.Empty<byte>());
            Assert.That(rooms.Length, Is.EqualTo(0));
        }

        [Test]
        public void RoomList_TruncatedRoom_Throws()
        {
            // count=1 but only 5 bytes of room data
            var buf = new byte[7]; // 2(count) + 5 bytes
            BinaryPrimitives.WriteUInt16LittleEndian(buf, 1);
            Assert.Throws<InvalidDataException>(() => RoomCodec.DecodeRoomList(buf));
        }

        [Test]
        public void CreateRoomRsp_TooShort_Throws()
        {
            Assert.Throws<InvalidDataException>(() => RoomCodec.DecodeCreateRoomRsp(new byte[3]));
        }

        [Test]
        public void JoinRoomRsp_TooShort_Throws()
        {
            Assert.Throws<InvalidDataException>(() => RoomCodec.DecodeJoinRoomRsp(new byte[5]));
        }

        [Test]
        public void DecodePlayerId_TooShort_Throws()
        {
            Assert.Throws<InvalidDataException>(() => RoomCodec.DecodePlayerId(new byte[3]));
        }

        // ─── StateSyncCodec ───────────────────────────────────────────────────────

        [Test]
        public void PushStateMsg_TooShort_Throws()
        {
            Assert.Throws<InvalidDataException>(() => StateSyncCodec.DecodePushStateMsg(new byte[3]));
        }

        [Test]
        public void PushData_TooShort_Throws()
        {
            Assert.Throws<InvalidDataException>(() => StateSyncCodec.DecodePushData(new byte[10]));
        }

        [Test]
        public void PushData_TruncatedValue_Throws()
        {
            // 14 header bytes + valueLen=100 but no value bytes
            var buf = new byte[14];
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12), 100); // valueLen=100
            Assert.Throws<InvalidDataException>(() => StateSyncCodec.DecodePushData(buf));
        }

        [Test]
        public void PushDataSync_TooShort_Throws()
        {
            Assert.Throws<InvalidDataException>(() => StateSyncCodec.DecodePushDataSync(new byte[4]));
        }

        [Test]
        public void PushDataSync_TruncatedEntry_Throws()
        {
            // count=1 but no entry data
            var buf = new byte[6];
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 1); // count=1
            Assert.Throws<InvalidDataException>(() => StateSyncCodec.DecodePushDataSync(buf));
        }

        // ─── AuthorityTransferCodec ───────────────────────────────────────────────

        [Test]
        public void AuthorityTransfer_TooShort_Throws()
        {
            Assert.Throws<InvalidDataException>(() => AuthorityTransferCodec.DecodeResult(new byte[7]));
        }
    }
}
