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
            public int DataLength;
            public bool Valid;
        }

        public SnapshotBuffer(int capacity = 32)
        {
            _capacity = capacity;
            _entries = new Entry[capacity];
        }

        private int RingIndex(uint frame) => (int)(frame % _capacity);

        /// <summary>
        /// 保存快照（复用已有 entry 的 buffer 避免分配）
        /// </summary>
        public void Save(uint frame, byte[] snapshot)
        {
            var idx = RingIndex(frame);
            ref var entry = ref _entries[idx];

            // 复用已有的 byte[]（如果大小够的话）
            if (entry.Data != null && entry.Data.Length >= snapshot.Length)
            {
                Buffer.BlockCopy(snapshot, 0, entry.Data, 0, snapshot.Length);
                entry.DataLength = snapshot.Length;
            }
            else
            {
                entry.Data = new byte[snapshot.Length];
                Buffer.BlockCopy(snapshot, 0, entry.Data, 0, snapshot.Length);
                entry.DataLength = snapshot.Length;
            }
            entry.Frame = frame;
            entry.Valid = true;
        }

        /// <summary>
        /// 获取某帧的快照（没有或已被覆盖则返回 null）
        /// 注意: 返回的 byte[] 可能比实际数据大（复用 buffer），
        /// 调用者应依赖数据内部的长度字段而非 array.Length。
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
                // 保留 Data 引用以便复用，不设 null
                _entries[i].DataLength = 0;
                _entries[i].Frame = 0;
            }
        }
    }
}
