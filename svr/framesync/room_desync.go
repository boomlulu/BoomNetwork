package framesync

// ===================== Desync Detection =====================

// ReportFrameHash 客户端上报帧 hash，检测不同步
// Returns (true, mismatchHashes) if desync detected; (false, nil) otherwise.
// mismatchHashes 包含触发检测的那帧所有玩家的 hash，用于广播诊断信息。
//
// 锁顺序规则：先短暂持 r.mu 读 running，再持 r.desyncMu 操作 desync 状态。
// 永远不在持有 r.desyncMu 时再获取 r.mu（防死锁）。
func (r *Room) ReportFrameHash(playerId int32, frameNumber uint32, hash uint32) (bool, map[int32]uint32) {
	// 快速读取 running（由 r.mu 保护），不进入 desync 锁
	r.mu.Lock()
	running := r.running
	r.mu.Unlock()
	if !running {
		return false, nil
	}

	r.desyncMu.Lock()
	defer r.desyncMu.Unlock()

	if r.desyncDetected {
		return false, nil
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
				// 在清除前保存各玩家 hash，供调用方广播诊断信息使用
				saved := make(map[int32]uint32, len(hashes))
				for k, v := range hashes {
					saved[k] = v
				}
				r.desyncDetected = true
				r.frameHashes = make(map[uint32]map[int32]uint32) // M9: 释放内存，检测完成后无需保留
				return true, saved
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

	return false, nil
}

// GetFrameHashes returns hashes for a specific frame (for logging)
func (r *Room) GetFrameHashes(frameNumber uint32) map[int32]uint32 {
	r.desyncMu.Lock()
	defer r.desyncMu.Unlock()
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
	r.desyncMu.Lock()
	defer r.desyncMu.Unlock()
	return r.desyncDetected
}

// ===================== Test-only helpers（同 package _test.go 调用，不 export）=====================

// testOnlyFrameHashesLen 供测试读取 frameHashes 长度，使用正确的锁
func (r *Room) testOnlyFrameHashesLen() int {
	r.desyncMu.Lock()
	defer r.desyncMu.Unlock()
	return len(r.frameHashes)
}

// testOnlyDesyncDetected 供测试读取 desyncDetected 标志，使用正确的锁
func (r *Room) testOnlyDesyncDetected() bool {
	r.desyncMu.Lock()
	defer r.desyncMu.Unlock()
	return r.desyncDetected
}

// testOnlySetFrameHashes 供测试预置 frameHashes 数据，使用正确的锁
func (r *Room) testOnlySetFrameHashes(m map[uint32]map[int32]uint32) {
	r.desyncMu.Lock()
	r.frameHashes = m
	r.desyncMu.Unlock()
}

// testOnlyGetFrameHash 供测试读取特定帧的特定玩家 hash，使用正确的锁
func (r *Room) testOnlyGetFrameHashEntry(frameNumber uint32, key uint32) (bool, bool) {
	r.desyncMu.Lock()
	defer r.desyncMu.Unlock()
	_, has := r.frameHashes[key]
	_, hasFrame := r.frameHashes[frameNumber]
	return has, hasFrame
}
