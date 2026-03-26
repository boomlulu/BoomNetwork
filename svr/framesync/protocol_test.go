package framesync

import (
	"bytes"
	"encoding/binary"
	"testing"
)

func TestEncodeDecodeInitData_NewFields(t *testing.T) {
	d := &InitData{
		FrameRate:           20,
		FrameInterval:       50,
		StartTime:           1234567890,
		SnapshotInterval:    100,
		QuickReconnectMaxMs: 5000,
	}
	buf := EncodeInitData(d)
	if len(buf) != InitDataSize {
		t.Fatalf("expected %d bytes, got %d", InitDataSize, len(buf))
	}

	decoded := DecodeInitData(buf)
	if decoded.FrameRate != 20 {
		t.Errorf("FrameRate: got %d, want 20", decoded.FrameRate)
	}
	if decoded.FrameInterval != 50 {
		t.Errorf("FrameInterval: got %d, want 50", decoded.FrameInterval)
	}
	if decoded.StartTime != 1234567890 {
		t.Errorf("StartTime: got %d, want 1234567890", decoded.StartTime)
	}
	if decoded.SnapshotInterval != 100 {
		t.Errorf("SnapshotInterval: got %d, want 100", decoded.SnapshotInterval)
	}
	if decoded.QuickReconnectMaxMs != 5000 {
		t.Errorf("QuickReconnectMaxMs: got %d, want 5000", decoded.QuickReconnectMaxMs)
	}
}

func TestDecodeInitData_LegacyCompat(t *testing.T) {
	// 只有 16 字节（旧服务器），新字段应为 0
	d := &InitData{FrameRate: 20, FrameInterval: 50, StartTime: 999}
	buf := EncodeInitData(d)
	legacy := buf[:16]

	decoded := DecodeInitData(legacy)
	if decoded.FrameRate != 20 {
		t.Errorf("FrameRate: got %d", decoded.FrameRate)
	}
	if decoded.SnapshotInterval != 0 {
		t.Errorf("SnapshotInterval should be 0 for legacy, got %d", decoded.SnapshotInterval)
	}
	if decoded.QuickReconnectMaxMs != 0 {
		t.Errorf("QuickReconnectMaxMs should be 0 for legacy, got %d", decoded.QuickReconnectMaxMs)
	}
}

func TestEncodeReconnectRsp_Success(t *testing.T) {
	snapshot := []byte{0xAA, 0xBB, 0xCC}
	buf := EncodeReconnectRsp(ReconnectSuccess, 42, 200, 150, snapshot)

	if buf[0] != ReconnectSuccess {
		t.Errorf("Result: got %d, want %d", buf[0], ReconnectSuccess)
	}
	if len(buf) != 13+3 {
		t.Errorf("Length: got %d, want 16", len(buf))
	}
	if !bytes.Equal(buf[13:], snapshot) {
		t.Errorf("Snapshot mismatch")
	}
}

func TestEncodeReconnectRsp_BufferStale(t *testing.T) {
	buf := EncodeReconnectRsp(ReconnectFailBufferStale, 42, 200, 0, nil)
	if buf[0] != ReconnectFailBufferStale {
		t.Errorf("Result: got %d, want %d", buf[0], ReconnectFailBufferStale)
	}
	if len(buf) != 13 {
		t.Errorf("Length: got %d, want 13", len(buf))
	}
}

func TestEncodeReconnectRsp_Fail(t *testing.T) {
	buf := EncodeReconnectRsp(ReconnectFail, 0, 0, 0, nil)
	if buf[0] != ReconnectFail {
		t.Errorf("Result: got %d, want %d", buf[0], ReconnectFail)
	}
}

func TestEncodeJoinRoomRsp_WithExistingPlayers(t *testing.T) {
	existing := []int32{10, 20, 30}
	buf := EncodeJoinRoomRsp(5, 42, existing)

	// [PlayerId:4][RoomId:4][PlayerCount:2][PlayerIds:4×3] = 8+2+12 = 22
	if len(buf) != 22 {
		t.Fatalf("Length: got %d, want 22", len(buf))
	}

	pid := int32(binary.LittleEndian.Uint32(buf[0:]))
	rid := int32(binary.LittleEndian.Uint32(buf[4:]))
	count := int(binary.LittleEndian.Uint16(buf[8:]))

	if pid != 5 {
		t.Errorf("PlayerId: got %d, want 5", pid)
	}
	if rid != 42 {
		t.Errorf("RoomId: got %d, want 42", rid)
	}
	if count != 3 {
		t.Errorf("PlayerCount: got %d, want 3", count)
	}

	for i, expected := range existing {
		got := int32(binary.LittleEndian.Uint32(buf[10+i*4:]))
		if got != expected {
			t.Errorf("Player[%d]: got %d, want %d", i, got, expected)
		}
	}
}

func TestEncodeJoinRoomRsp_NoExistingPlayers(t *testing.T) {
	buf := EncodeJoinRoomRsp(1, 2, nil)
	// [PlayerId:4][RoomId:4][PlayerCount:2] = 10
	if len(buf) != 10 {
		t.Fatalf("Length: got %d, want 10", len(buf))
	}
	count := int(binary.LittleEndian.Uint16(buf[8:]))
	if count != 0 {
		t.Errorf("PlayerCount: got %d, want 0", count)
	}
}

// === 轻量状态同步 ===

func TestDataStoreKey(t *testing.T) {
	k1 := DataStoreKey(1, 100)
	k2 := DataStoreKey(1, 200)
	k3 := DataStoreKey(2, 100)
	if k1 == k2 {
		t.Error("different keys should produce different composite keys")
	}
	if k1 == k3 {
		t.Error("different players should produce different composite keys")
	}
}

func TestEncodeDecodePushData(t *testing.T) {
	value := []byte{1, 2, 3, 4, 5}
	buf := EncodePushData(42, 7, 99, value)

	version := binary.LittleEndian.Uint32(buf[0:4])
	playerId := int32(binary.LittleEndian.Uint32(buf[4:8]))
	key := int32(binary.LittleEndian.Uint32(buf[8:12]))
	valueLen := int(binary.LittleEndian.Uint16(buf[12:14]))

	if version != 42 {
		t.Errorf("version: got %d, want 42", version)
	}
	if playerId != 7 {
		t.Errorf("playerId: got %d, want 7", playerId)
	}
	if key != 99 {
		t.Errorf("key: got %d, want 99", key)
	}
	if valueLen != 5 {
		t.Errorf("valueLen: got %d, want 5", valueLen)
	}
	if !bytes.Equal(buf[14:14+valueLen], value) {
		t.Errorf("value mismatch")
	}
}

func TestEncodeDecodePushData_Delete(t *testing.T) {
	buf := EncodePushData(10, 3, 50, nil)
	valueLen := int(binary.LittleEndian.Uint16(buf[12:14]))
	if valueLen != 0 {
		t.Errorf("delete should have valueLen=0, got %d", valueLen)
	}
	if len(buf) != 14 {
		t.Errorf("delete message should be 14 bytes, got %d", len(buf))
	}
}

func TestEncodeDecodePushDataSync(t *testing.T) {
	entries := []DataEntry{
		{PlayerId: 1, Key: 10, Value: []byte{0xAA, 0xBB}},
		{PlayerId: 2, Key: 20, Value: []byte{0xCC}},
		{PlayerId: 1, Key: 30, Value: []byte{}},
	}
	buf := EncodePushDataSync(99, entries)

	version := binary.LittleEndian.Uint32(buf[0:4])
	entryCount := int(binary.LittleEndian.Uint16(buf[4:6]))
	if version != 99 {
		t.Errorf("version: got %d, want 99", version)
	}
	if entryCount != 3 {
		t.Errorf("count: got %d, want 3", entryCount)
	}

	offset := 6
	for i, expected := range entries {
		pid := int32(binary.LittleEndian.Uint32(buf[offset:]))
		offset += 4
		k := int32(binary.LittleEndian.Uint32(buf[offset:]))
		offset += 4
		vlen := int(binary.LittleEndian.Uint16(buf[offset:]))
		offset += 2
		if pid != expected.PlayerId {
			t.Errorf("entry[%d] playerId: got %d, want %d", i, pid, expected.PlayerId)
		}
		if k != expected.Key {
			t.Errorf("entry[%d] key: got %d, want %d", i, k, expected.Key)
		}
		if vlen != len(expected.Value) {
			t.Errorf("entry[%d] valueLen: got %d, want %d", i, vlen, len(expected.Value))
		}
		if vlen > 0 && !bytes.Equal(buf[offset:offset+vlen], expected.Value) {
			t.Errorf("entry[%d] value mismatch", i)
		}
		offset += vlen
	}
}

func TestDecodeSetData(t *testing.T) {
	buf := make([]byte, 9)
	binary.LittleEndian.PutUint32(buf[0:], uint32(42))
	binary.LittleEndian.PutUint16(buf[4:], 3)
	copy(buf[6:], []byte{1, 2, 3})

	key, value, ok := DecodeSetData(buf)
	if !ok || key != 42 || !bytes.Equal(value, []byte{1, 2, 3}) {
		t.Errorf("SetData decode failed: key=%d, value=%v, ok=%v", key, value, ok)
	}

	delBuf := make([]byte, 6)
	binary.LittleEndian.PutUint32(delBuf[0:], uint32(99))
	binary.LittleEndian.PutUint16(delBuf[4:], 0)

	key, value, ok = DecodeSetData(delBuf)
	if !ok || key != 99 || value != nil {
		t.Errorf("DeleteData decode failed: key=%d, value=%v, ok=%v", key, value, ok)
	}
}

func TestRoomSetDataAndSnapshot(t *testing.T) {
	room := NewRoomWithConfig(DefaultRoomConfig())

	v1 := room.SetData(1, 10, []byte{0xAA})
	v2 := room.SetData(1, 20, []byte{0xBB, 0xCC})
	if v1 != 1 || v2 != 2 {
		t.Errorf("versions: got %d,%d, want 1,2", v1, v2)
	}

	v3 := room.SetData(2, 10, []byte{0xDD})
	if v3 != 3 {
		t.Errorf("version: got %d, want 3", v3)
	}

	entries, ver := room.GetDataSnapshot()
	if ver != 3 || len(entries) != 3 {
		t.Errorf("snapshot: ver=%d, entries=%d, want ver=3, entries=3", ver, len(entries))
	}

	v4 := room.SetData(1, 10, nil)
	if v4 != 4 {
		t.Errorf("delete version: got %d, want 4", v4)
	}
	entries2, ver2 := room.GetDataSnapshot()
	if ver2 != 4 || len(entries2) != 2 {
		t.Errorf("after delete: ver=%d, entries=%d, want ver=4, entries=2", ver2, len(entries2))
	}

	deleted, versions := room.ClearPlayerData(1)
	if len(deleted) != 1 || len(versions) != 1 {
		t.Errorf("clear: deleted=%d, versions=%d, want 1,1", len(deleted), len(versions))
	}
	if room.DataStoreEmpty() {
		t.Error("store should still have player 2's data")
	}

	deleted2, _ := room.ClearPlayerData(2)
	if len(deleted2) != 1 {
		t.Errorf("clear player 2: deleted=%d, want 1", len(deleted2))
	}
	if !room.DataStoreEmpty() {
		t.Error("store should be empty")
	}
}
