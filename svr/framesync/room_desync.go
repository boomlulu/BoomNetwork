package framesync

// ===================== Desync Detection =====================

// ReportFrameHash 客户端上报帧 hash，检测不同步
// Returns true if desync detected
func (r *Room) ReportFrameHash(playerId int32, frameNumber uint32, hash uint32) bool {
	r.mu.Lock()
	defer r.mu.Unlock()

	if r.desyncDetected || !r.running {
		return false
	}

	if r.frameHashes[frameNumber] == nil {
		r.frameHashes[frameNumber] = make(map[int32]uint32)
	}
	r.frameHashes[frameNumber][playerId] = hash

	// Check for mismatch: compare against any existing hash for this frame
	hashes := r.frameHashes[frameNumber]
	if len(hashes) >= 2 {
		var firstHash uint32
		first := true
		for _, h := range hashes {
			if first {
				firstHash = h
				first = false
				continue
			}
			if h != firstHash {
				r.desyncDetected = true
				r.frameHashes = make(map[uint32]map[int32]uint32) // M9: 释放内存，检测完成后无需保留
				return true
			}
		}
	}

	// 清理超过 200 帧前的旧数据（避免内存无限增长）
	// 周期性清理：每 100 帧触发一次，避免每次 ReportFrameHash 都 O(n) 扫描全表。
	const maxHashHistory = 200
	if frameNumber > maxHashHistory && frameNumber%100 == 0 {
		cutoff := frameNumber - maxHashHistory
		for fn := range r.frameHashes {
			if fn < cutoff {
				delete(r.frameHashes, fn)
			}
		}
	}

	return false
}

// GetFrameHashes returns hashes for a specific frame (for logging)
func (r *Room) GetFrameHashes(frameNumber uint32) map[int32]uint32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	result := make(map[int32]uint32)
	if hashes, ok := r.frameHashes[frameNumber]; ok {
		for k, v := range hashes {
			result[k] = v
		}
	}
	return result
}

// IsDesyncDetected 是否已检测到 desync（GM 检视用）
func (r *Room) IsDesyncDetected() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.desyncDetected
}
