package framesync

// === 实体权威转移 ===

// TryGrantAuthority 尝试将 entityId 的权威授予 requesterId。
// 始终授予（支持抢夺），服务器按请求到达顺序仲裁。
func (r *Room) TryGrantAuthority(entityId int32, requesterId int32) (granted bool, currentOwner int32) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.entityAuthority[entityId] = requesterId
	return true, requesterId
}

// ReleaseAuthority 释放玩家对某实体的权威。只有持有者可释放。
func (r *Room) ReleaseAuthority(entityId int32, requesterId int32) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.entityAuthority[entityId] == requesterId {
		r.entityAuthority[entityId] = 0
		return true
	}
	return false
}

// ReleaseAllAuthority 释放某玩家持有的所有实体权威（断线清理用）。
func (r *Room) ReleaseAllAuthority(playerId int32) []int32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	var released []int32
	for eid, owner := range r.entityAuthority {
		if owner == playerId {
			r.entityAuthority[eid] = 0
			released = append(released, eid)
		}
	}
	return released
}

// EntityAuthorityEntry 实体权威条目（GM 检视用）
type EntityAuthorityEntry struct {
	EntityId int32
	OwnerId  int32
}

// GetEntityAuthority 获取当前实体权威表快照
func (r *Room) GetEntityAuthority() []EntityAuthorityEntry {
	r.mu.Lock()
	defer r.mu.Unlock()
	entries := make([]EntityAuthorityEntry, 0, len(r.entityAuthority))
	for eid, owner := range r.entityAuthority {
		entries = append(entries, EntityAuthorityEntry{EntityId: eid, OwnerId: owner})
	}
	return entries
}
