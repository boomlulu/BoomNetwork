package framesync

// SetData 设置/删除 KV 数据，返回新版本号
// value == nil 表示删除
func (r *Room) SetData(playerId int32, key int32, value []byte) uint32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	compositeKey := DataStoreKey(playerId, key)
	if value == nil {
		delete(r.dataStore, compositeKey)
	} else {
		r.dataStore[compositeKey] = DataEntry{
			PlayerId: playerId,
			Key:      key,
			Value:    value,
		}
	}
	r.dataVersion++
	return r.dataVersion
}

// GetDataSnapshot 获取全量 KV 快照 + 当前版本号
func (r *Room) GetDataSnapshot() ([]DataEntry, uint32) {
	r.mu.Lock()
	defer r.mu.Unlock()
	entries := make([]DataEntry, 0, len(r.dataStore))
	for _, e := range r.dataStore {
		entries = append(entries, e)
	}
	return entries, r.dataVersion
}

// ClearPlayerData 清除指定玩家的所有 KV 数据
// 返回被删除的条目（用于广播删除）和每次删除后的版本号
func (r *Room) ClearPlayerData(playerId int32) ([]DataEntry, []uint32) {
	r.mu.Lock()
	defer r.mu.Unlock()
	var deleted []DataEntry
	var versions []uint32
	for k, e := range r.dataStore {
		if e.PlayerId == playerId {
			deleted = append(deleted, e)
			delete(r.dataStore, k)
			r.dataVersion++
			versions = append(versions, r.dataVersion)
		}
	}
	return deleted, versions
}

// DataVersion 获取当前数据版本号
func (r *Room) DataVersion() uint32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.dataVersion
}

// DataStoreEmpty 检查数据存储是否为空
func (r *Room) DataStoreEmpty() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.dataStore) == 0
}

// DataStoreEntry KV 数据条目（GM 检视用）
type DataStoreEntry struct {
	PlayerId int32
	Key      int32
	Value    []byte
}

// GetDataStoreEntries 获取 KV 数据仓全量快照
func (r *Room) GetDataStoreEntries() []DataStoreEntry {
	r.mu.Lock()
	defer r.mu.Unlock()
	entries := make([]DataStoreEntry, 0, len(r.dataStore))
	for _, de := range r.dataStore {
		entries = append(entries, DataStoreEntry{
			PlayerId: de.PlayerId,
			Key:      de.Key,
			Value:    de.Value,
		})
	}
	return entries
}
