using System;
using NUnit.Framework;
using BoomNetwork.Core.FrameSync;

namespace BoomNetwork.Tests
{
    [TestFixture]
    public class SnapshotProtocolTests
    {
        [Test]
        public void InitData_NewFields_RoundTrip()
        {
            var data = new FrameSyncInitData
            {
                FrameRate = 20,
                FrameInterval = 50,
                StartTime = 1234567890L,
                SnapshotInterval = 100,
                QuickReconnectMaxMs = 5000,
            };

            var buf = new byte[FrameSyncInitData.Size];
            data.WriteTo(buf);

            Assert.That(buf.Length, Is.EqualTo(24));

            var decoded = FrameSyncInitData.ReadFrom(buf);
            Assert.That(decoded.FrameRate, Is.EqualTo(20));
            Assert.That(decoded.FrameInterval, Is.EqualTo(50));
            Assert.That(decoded.StartTime, Is.EqualTo(1234567890L));
            Assert.That(decoded.SnapshotInterval, Is.EqualTo(100));
            Assert.That(decoded.QuickReconnectMaxMs, Is.EqualTo(5000));
        }

        [Test]
        public void InitData_LegacyCompat_16Bytes()
        {
            // 旧服务器只发 16 字节，新字段应为 0
            var data = new FrameSyncInitData
            {
                FrameRate = 20,
                FrameInterval = 50,
                StartTime = 999,
            };

            var buf = new byte[FrameSyncInitData.Size];
            data.WriteTo(buf);

            // 只取前 16 字节模拟旧服务器
            var legacy = buf.AsSpan(0, 16);
            var decoded = FrameSyncInitData.ReadFrom(legacy);

            Assert.That(decoded.FrameRate, Is.EqualTo(20));
            Assert.That(decoded.FrameInterval, Is.EqualTo(50));
            Assert.That(decoded.StartTime, Is.EqualTo(999));
            Assert.That(decoded.SnapshotInterval, Is.EqualTo(0));
            Assert.That(decoded.QuickReconnectMaxMs, Is.EqualTo(0));
        }

        [Test]
        public void ReconnectRsp_Success_WithSnapshot()
        {
            // 模拟服务端编码: [Result:1][RoomId:4][ServerFrame:4][SnapshotFrame:4][SnapshotData:N]
            var buf = new byte[13 + 3];
            buf[0] = ReconnectResult.Success;
            BitConverter.TryWriteBytes(buf.AsSpan(1), 42);        // roomId
            BitConverter.TryWriteBytes(buf.AsSpan(5), (uint)200);  // serverFrame
            BitConverter.TryWriteBytes(buf.AsSpan(9), (uint)150);  // snapshotFrame
            buf[13] = 0xAA;
            buf[14] = 0xBB;
            buf[15] = 0xCC;

            var (result, roomId, serverFrame, snapshotFrame, snapshotData) =
                SnapshotCodec.DecodeReconnectRsp(buf);

            Assert.That(result, Is.EqualTo(ReconnectResult.Success));
            Assert.That(roomId, Is.EqualTo(42));
            Assert.That(serverFrame, Is.EqualTo(200u));
            Assert.That(snapshotFrame, Is.EqualTo(150u));
            Assert.That(snapshotData, Is.Not.Null);
            Assert.That(snapshotData!.Length, Is.EqualTo(3));
            Assert.That(snapshotData[0], Is.EqualTo(0xAA));
        }

        [Test]
        public void ReconnectRsp_BufferStale()
        {
            var buf = new byte[13];
            buf[0] = ReconnectResult.BufferStale;
            BitConverter.TryWriteBytes(buf.AsSpan(1), 42);
            BitConverter.TryWriteBytes(buf.AsSpan(5), (uint)200);
            BitConverter.TryWriteBytes(buf.AsSpan(9), (uint)0);

            var (result, roomId, serverFrame, snapshotFrame, snapshotData) =
                SnapshotCodec.DecodeReconnectRsp(buf);

            Assert.That(result, Is.EqualTo(ReconnectResult.BufferStale));
            Assert.That(roomId, Is.EqualTo(42));
            Assert.That(serverFrame, Is.EqualTo(200u));
            Assert.That(snapshotData, Is.Null);
        }

        [Test]
        public void ReconnectRsp_Fail()
        {
            var buf = new byte[1];
            buf[0] = ReconnectResult.Fail;

            var (result, roomId, serverFrame, snapshotFrame, snapshotData) =
                SnapshotCodec.DecodeReconnectRsp(buf);

            Assert.That(result, Is.EqualTo(ReconnectResult.Fail));
            Assert.That(roomId, Is.EqualTo(0));
            Assert.That(snapshotData, Is.Null);
        }

        [Test]
        public void ReconnectRsp_Success_NoSnapshot()
        {
            // 快速重连路径: snapshotFrame=0, 无快照数据
            var buf = new byte[13];
            buf[0] = ReconnectResult.Success;
            BitConverter.TryWriteBytes(buf.AsSpan(1), 42);
            BitConverter.TryWriteBytes(buf.AsSpan(5), (uint)200);
            BitConverter.TryWriteBytes(buf.AsSpan(9), (uint)0); // snapshotFrame=0

            var (result, roomId, serverFrame, snapshotFrame, snapshotData) =
                SnapshotCodec.DecodeReconnectRsp(buf);

            Assert.That(result, Is.EqualTo(ReconnectResult.Success));
            Assert.That(snapshotFrame, Is.EqualTo(0u));
            Assert.That(snapshotData, Is.Null);
        }

        [Test]
        public void UploadSnapshot_Encode()
        {
            var snapshot = new byte[] { 1, 2, 3, 4, 5 };
            var encoded = SnapshotCodec.EncodeUploadSnapshot(100, snapshot);

            Assert.That(encoded.Length, Is.EqualTo(4 + 5));
            Assert.That(BitConverter.ToUInt32(encoded, 0), Is.EqualTo(100u));
            Assert.That(encoded[4], Is.EqualTo(1));
            Assert.That(encoded[8], Is.EqualTo(5));
        }

        [Test]
        public void JoinRoomRsp_WithExistingPlayers()
        {
            // 模拟: [PlayerId:4][RoomId:4][PlayerCount:2][P10:4][P20:4]
            var buf = new byte[18];
            BitConverter.TryWriteBytes(buf.AsSpan(0), 5);         // playerId
            BitConverter.TryWriteBytes(buf.AsSpan(4), 42);        // roomId
            BitConverter.TryWriteBytes(buf.AsSpan(8), (ushort)2); // count
            BitConverter.TryWriteBytes(buf.AsSpan(10), 10);       // P10
            BitConverter.TryWriteBytes(buf.AsSpan(14), 20);       // P20

            var (playerId, roomId, existing) = RoomCodec.DecodeJoinRoomRsp(buf);

            Assert.That(playerId, Is.EqualTo(5));
            Assert.That(roomId, Is.EqualTo(42));
            Assert.That(existing.Length, Is.EqualTo(2));
            Assert.That(existing[0], Is.EqualTo(10));
            Assert.That(existing[1], Is.EqualTo(20));
        }

        [Test]
        public void JoinRoomRsp_NoExistingPlayers()
        {
            var buf = new byte[10];
            BitConverter.TryWriteBytes(buf.AsSpan(0), 1);
            BitConverter.TryWriteBytes(buf.AsSpan(4), 2);
            BitConverter.TryWriteBytes(buf.AsSpan(8), (ushort)0);

            var (playerId, roomId, existing) = RoomCodec.DecodeJoinRoomRsp(buf);

            Assert.That(playerId, Is.EqualTo(1));
            Assert.That(roomId, Is.EqualTo(2));
            Assert.That(existing.Length, Is.EqualTo(0));
        }

        [Test]
        public void JoinRoomRsp_LegacyCompat_8Bytes()
        {
            // 旧服务器只发 8 字节，无 PlayerCount 字段
            var buf = new byte[8];
            BitConverter.TryWriteBytes(buf.AsSpan(0), 3);
            BitConverter.TryWriteBytes(buf.AsSpan(4), 7);

            var (playerId, roomId, existing) = RoomCodec.DecodeJoinRoomRsp(buf);

            Assert.That(playerId, Is.EqualTo(3));
            Assert.That(roomId, Is.EqualTo(7));
            Assert.That(existing.Length, Is.EqualTo(0));
        }
    }
}
