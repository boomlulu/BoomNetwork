using System;

namespace BoomNetwork.Core.Prediction
{
    /// <summary>
    /// 快照环形缓冲区 — 存储最近 N 帧的游戏状态快照
    ///
    /// 用途: 预测回滚时恢复到某帧的状态
    /// 容量: 通常 = 最大预测窗口 × 2（默认 32）
    /// </summary>
    public class SnapshotBuffer
    {
        private readonly int _capacity;
        private readonly Entry[] _entries;

        private struct Entry
        {
            public uint Frame;
            public byte[] Data;
            public bool Valid;
        }

        public SnapshotBuffer(int capacity = 32)
        {
            _capacity = capacity;
            _entries = new Entry[capacity];
        }

        private int RingIndex(uint frame) => (int)(frame % _capacity);

        /// <summary>
        /// 保存快照
        /// </summary>
        public void Save(uint frame, byte[] snapshot)
        {
            var idx = RingIndex(frame);
            _entries[idx].Frame = frame;
            _entries[idx].Data = snapshot;
            _entries[idx].Valid = true;
        }

        /// <summary>
        /// 获取某帧的快照（没有或已被覆盖则返回 null）
        /// </summary>
        public byte[]? Get(uint frame)
        {
            var idx = RingIndex(frame);
            ref var entry = ref _entries[idx];
            if (entry.Valid && entry.Frame == frame)
                return entry.Data;
            return null;
        }

        /// <summary>
        /// 清空全部
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < _capacity; i++)
            {
                _entries[i].Valid = false;
                _entries[i].Data = null;
                _entries[i].Frame = 0;
            }
        }
    }
}
