using System;
using System.Buffers.Binary;
using System.Text;
using NUnit.Framework;
using BoomNetwork.Core.FrameSync;

namespace BoomNetwork.Tests
{
    [TestFixture]
    public class ProtocolDecodeTests
    {
        // ===================== EntityStateCodec =====================

        [Test]
        public void EntityState_Decode_SingleEntity()
        {
            // Build PushEntityState: [senderPid:4][count:1][entityId:4][stateLen:2][stateData]
            var buf = new byte[4 + 1 + 4 + 2 + 3]; // senderPid + count + entityId + stateLen + 3 bytes state
            BinaryPrimitives.WriteInt32LittleEndian(buf, 42);       // senderPid = 42
            buf[4] = 1;                                              // count = 1
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(5), 100); // entityId = 100
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(9), 3);  // stateLen = 3
            buf[11] = 0xAA; buf[12] = 0xBB; buf[13] = 0xCC;       // state data

            int callCount = 0;
            int gotSenderPid = 0, gotEntityId = 0, gotLen = 0;
            byte gotFirstByte = 0;

            EntityStateCodec.Decode(buf, (senderPid, entityId, data, offset, len) =>
            {
                callCount++;
                gotSenderPid = senderPid;
                gotEntityId = entityId;
                gotLen = len;
                gotFirstByte = data[offset];
            });

            Assert.That(callCount, Is.EqualTo(1));
            Assert.That(gotSenderPid, Is.EqualTo(42));
            Assert.That(gotEntityId, Is.EqualTo(100));
            Assert.That(gotLen, Is.EqualTo(3));
            Assert.That(gotFirstByte, Is.EqualTo(0xAA));
        }

        [Test]
        public void EntityState_Decode_MultipleEntities()
        {
            // 2 entities
            var buf = new byte[4 + 1 + (4 + 2 + 2) * 2];
            BinaryPrimitives.WriteInt32LittleEndian(buf, 1);    // senderPid
            buf[4] = 2;                                          // count

            int off = 5;
            // Entity 1: id=10, state=[0x01, 0x02]
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(off), 10); off += 4;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off), 2); off += 2;
            buf[off++] = 0x01; buf[off++] = 0x02;

            // Entity 2: id=20, state=[0x03, 0x04]
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(off), 20); off += 4;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off), 2); off += 2;
            buf[off++] = 0x03; buf[off++] = 0x04;

            int callCount = 0;
            EntityStateCodec.Decode(buf, (_, entityId, data, o, len) =>
            {
                callCount++;
                if (callCount == 1) Assert.That(entityId, Is.EqualTo(10));
                if (callCount == 2) Assert.That(entityId, Is.EqualTo(20));
            });

            Assert.That(callCount, Is.EqualTo(2));
        }

        [Test]
        public void EntityState_Decode_EmptyBuffer()
        {
            int callCount = 0;
            EntityStateCodec.Decode(ReadOnlySpan<byte>.Empty, (_, _, _, _, _) => callCount++);
            Assert.That(callCount, Is.EqualTo(0));
        }

        [Test]
        public void EntityState_Decode_TooShortBuffer()
        {
            var buf = new byte[3]; // less than 5 bytes
            int callCount = 0;
            EntityStateCodec.Decode(buf, (_, _, _, _, _) => callCount++);
            Assert.That(callCount, Is.EqualTo(0));
        }

        // ===================== AuthorityTransferCodec =====================

        [Test]
        public void AuthorityTransfer_RequestRoundTrip()
        {
            var encoded = AuthorityTransferCodec.EncodeRequest(42, false);
            var (entityId, release) = AuthorityTransferCodec.DecodeRequest(encoded);
            Assert.That(entityId, Is.EqualTo(42));
            Assert.That(release, Is.False);
        }

        [Test]
        public void AuthorityTransfer_ReleaseRoundTrip()
        {
            var encoded = AuthorityTransferCodec.EncodeRequest(99, true);
            var (entityId, release) = AuthorityTransferCodec.DecodeRequest(encoded);
            Assert.That(entityId, Is.EqualTo(99));
            Assert.That(release, Is.True);
        }

        [Test]
        public void AuthorityTransfer_ResultRoundTrip()
        {
            var encoded = AuthorityTransferCodec.EncodeResult(50, 3);
            var (entityId, newOwner) = AuthorityTransferCodec.DecodeResult(encoded);
            Assert.That(entityId, Is.EqualTo(50));
            Assert.That(newOwner, Is.EqualTo(3));
        }

        [Test]
        public void AuthorityTransfer_ResultUnclaimed()
        {
            var encoded = AuthorityTransferCodec.EncodeResult(50, 0);
            var (_, newOwner) = AuthorityTransferCodec.DecodeResult(encoded);
            Assert.That(newOwner, Is.EqualTo(0)); // unclaimed
        }

        // ===================== StateSyncCodec =====================

        [Test]
        public void StateMsg_EncodeIsIdentity()
        {
            var data = new byte[] { 1, 2, 3, 4, 5 };
            var encoded = StateSyncCodec.EncodeStateMsg(data);
            Assert.That(encoded, Is.EqualTo(data));
        }

        [Test]
        public void PushStateMsg_Decode()
        {
            var buf = new byte[4 + 3];
            BinaryPrimitives.WriteInt32LittleEndian(buf, 7); // playerId
            buf[4] = 0xAA; buf[5] = 0xBB; buf[6] = 0xCC;

            var (playerId, data) = StateSyncCodec.DecodePushStateMsg(buf);
            Assert.That(playerId, Is.EqualTo(7));
            Assert.That(data.Length, Is.EqualTo(3));
            Assert.That(data[0], Is.EqualTo(0xAA));
        }

        [Test]
        public void SetData_EncodeRoundTrip()
        {
            var value = new byte[] { 0x10, 0x20 };
            var encoded = StateSyncCodec.EncodeSetData(5, value);
            // [Key:4][ValueLen:2][Value:2]
            Assert.That(encoded.Length, Is.EqualTo(8));
            int key = BinaryPrimitives.ReadInt32LittleEndian(encoded);
            Assert.That(key, Is.EqualTo(5));
            ushort len = BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(4));
            Assert.That(len, Is.EqualTo(2));
        }

        [Test]
        public void DeleteData_Encode()
        {
            var encoded = StateSyncCodec.EncodeDeleteData(42);
            Assert.That(encoded.Length, Is.EqualTo(6));
            int key = BinaryPrimitives.ReadInt32LittleEndian(encoded);
            Assert.That(key, Is.EqualTo(42));
            ushort len = BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(4));
            Assert.That(len, Is.EqualTo(0)); // delete = valueLen 0
        }

        [Test]
        public void PushData_Decode()
        {
            // [Version:4][PlayerId:4][Key:4][ValueLen:2][Value:N]
            var buf = new byte[14 + 2];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 10);        // version
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), 3); // playerId
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), 7); // key
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12), 2); // valueLen
            buf[14] = 0xDE; buf[15] = 0xAD;

            var (version, playerId, key, value) = StateSyncCodec.DecodePushData(buf);
            Assert.That(version, Is.EqualTo(10u));
            Assert.That(playerId, Is.EqualTo(3));
            Assert.That(key, Is.EqualTo(7));
            Assert.That(value, Is.EqualTo(new byte[] { 0xDE, 0xAD }));
        }

        [Test]
        public void PushData_Decode_EmptyValue()
        {
            var buf = new byte[14];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 1);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), 2);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), 3);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12), 0); // empty = delete

            var (_, _, _, value) = StateSyncCodec.DecodePushData(buf);
            Assert.That(value.Length, Is.EqualTo(0));
        }

        [Test]
        public void PushDataSync_Decode()
        {
            // [Version:4][Count:2] + 2 entries
            int entrySize = 4 + 4 + 2 + 1; // playerId + key + valueLen + value(1B)
            var buf = new byte[6 + entrySize * 2];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 99);         // version
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 2); // count

            int off = 6;
            // Entry 1
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(off), 1); off += 4; // pid
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(off), 10); off += 4; // key
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off), 1); off += 2; // valueLen
            buf[off++] = 0xAA;

            // Entry 2
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(off), 2); off += 4;
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(off), 20); off += 4;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off), 1); off += 2;
            buf[off++] = 0xBB;

            var (version, entries) = StateSyncCodec.DecodePushDataSync(buf);
            Assert.That(version, Is.EqualTo(99u));
            Assert.That(entries.Length, Is.EqualTo(2));
            Assert.That(entries[0].PlayerId, Is.EqualTo(1));
            Assert.That(entries[0].Key, Is.EqualTo(10));
            Assert.That(entries[0].Value[0], Is.EqualTo(0xAA));
            Assert.That(entries[1].PlayerId, Is.EqualTo(2));
            Assert.That(entries[1].Key, Is.EqualTo(20));
        }

        [Test]
        public void PushDataSync_Decode_Empty()
        {
            var buf = new byte[6];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 1);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 0); // zero entries

            var (version, entries) = StateSyncCodec.DecodePushDataSync(buf);
            Assert.That(version, Is.EqualTo(1u));
            Assert.That(entries.Length, Is.EqualTo(0));
        }

        // ===================== FrameHashMismatch =====================

        [Test]
        public void FrameHashMismatch_Decode()
        {
            // [FrameNumber:4][PlayerCount:1][PlayerId:4+Hash:4]...
            var buf = new byte[4 + 1 + 8 * 2]; // 2 players
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 500);   // frame
            buf[4] = 2;
            // Player 1
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(5), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(9), 0xAAAAAAAA);
            // Player 2
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(13), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(17), 0xBBBBBBBB);

            var mismatch = FrameHashMismatch.Decode(buf);
            Assert.That(mismatch.FrameNumber, Is.EqualTo(500u));
            Assert.That(mismatch.PlayerHashes.Length, Is.EqualTo(2));
            Assert.That(mismatch.PlayerHashes[0].PlayerId, Is.EqualTo(1));
            Assert.That(mismatch.PlayerHashes[0].Hash, Is.EqualTo(0xAAAAAAAAu));
            Assert.That(mismatch.PlayerHashes[1].PlayerId, Is.EqualTo(2));
            Assert.That(mismatch.PlayerHashes[1].Hash, Is.EqualTo(0xBBBBBBBBu));
        }

        // ===================== FrameDataCodec (client-side decode) =====================

        [Test]
        public void FrameData_Decode_NoInputsNoEvents()
        {
            // [FrameNumber:4][InputCount:2]
            var buf = new byte[6];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 42);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 0); // no inputs

            var frame = FrameDataCodec.Decode(buf);
            Assert.That(frame.FrameNumber, Is.EqualTo(42u));
            Assert.That(frame.Inputs.Length, Is.EqualTo(0));
            Assert.That(frame.Events?.Length ?? 0, Is.EqualTo(0));
        }

        [Test]
        public void FrameData_Decode_WithInputs()
        {
            // [FrameNumber:4][InputCount:2] + [PlayerId:4][DataLen:2][Data:2]
            var buf = new byte[6 + 4 + 2 + 2];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 100);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 1); // 1 input
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(6), 7);  // playerId
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), 2); // dataLen
            buf[12] = 0x01; buf[13] = 0x02;

            var frame = FrameDataCodec.Decode(buf);
            Assert.That(frame.FrameNumber, Is.EqualTo(100u));
            Assert.That(frame.Inputs.Length, Is.EqualTo(1));
            Assert.That(frame.Inputs[0].PlayerId, Is.EqualTo(7));
            Assert.That(frame.Inputs[0].DataLength, Is.EqualTo(2));
        }

        [Test]
        public void FrameData_Decode_WithEvents()
        {
            // [FrameNumber:4][InputCount:2] + [EventCount:1][EventType:1][PlayerId:4]
            var buf = new byte[6 + 1 + 5];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 200);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 0); // no inputs
            buf[6] = 1; // event count
            buf[7] = FrameEventType.PlayerJoined; // event type
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), 5); // playerId

            var frame = FrameDataCodec.Decode(buf);
            Assert.That(frame.FrameNumber, Is.EqualTo(200u));
            Assert.That(frame.Inputs.Length, Is.EqualTo(0));
            Assert.That(frame.Events.Length, Is.EqualTo(1));
            Assert.That(frame.Events[0].EventType, Is.EqualTo(FrameEventType.PlayerJoined));
            Assert.That(frame.Events[0].PlayerId, Is.EqualTo(5));
        }

        // ===================== RoomCodec =====================

        [Test]
        public void RoomCodec_CreateRoom_Encode()
        {
            // Wire format: [MaxPlayers:2][MatchKeyLen:2] = 4 bytes when no key
            var buf = RoomCodec.EncodeCreateRoom(8);
            Assert.That(buf.Length, Is.EqualTo(4));
            int max = BinaryPrimitives.ReadUInt16LittleEndian(buf);
            Assert.That(max, Is.EqualTo(8));
            // keyLen should be 0
            int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2));
            Assert.That(keyLen, Is.EqualTo(0));
        }

        [Test]
        public void RoomCodec_MatchRoom_Encode_NoKey()
        {
            var buf = RoomCodec.EncodeMatchRoom(4);
            Assert.That(buf.Length, Is.EqualTo(4)); // maxPlayers(2) + keyLen(2)
        }

        [Test]
        public void RoomCodec_MatchRoom_Encode_WithKey()
        {
            var buf = RoomCodec.EncodeMatchRoom(4, "test");
            // [maxPlayers:2][keyLen:2][keyBytes:4]
            Assert.That(buf.Length, Is.EqualTo(8));
            ushort keyLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2));
            Assert.That(keyLen, Is.EqualTo(4));
            string key = Encoding.UTF8.GetString(buf, 4, keyLen);
            Assert.That(key, Is.EqualTo("test"));
        }

        [Test]
        public void RoomCodec_JoinRoom_Encode()
        {
            var buf = RoomCodec.EncodeJoinRoom(42);
            Assert.That(buf.Length, Is.EqualTo(4));
            int roomId = BinaryPrimitives.ReadInt32LittleEndian(buf);
            Assert.That(roomId, Is.EqualTo(42));
        }

        [Test]
        public void RoomCodec_DecodePlayerId()
        {
            var buf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buf, 99);
            int pid = RoomCodec.DecodePlayerId(buf);
            Assert.That(pid, Is.EqualTo(99));
        }
    }
}
