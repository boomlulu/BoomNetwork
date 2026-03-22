using System;
using System.Collections.Generic;

namespace BoomNetwork.Core.Prediction
{
    /// <summary>
    /// 输入缓冲区 — 环形缓冲，存储每帧每个玩家的输入
    ///
    /// 用途:
    ///   - 存储本地玩家的真实输入
    ///   - 存储远程玩家的预测输入（服务器确认前）
    ///   - 服务器确认后用真实输入覆盖预测输入
    ///   - 回滚时从 buffer 中读取正确输入重新执行
    /// </summary>
    public class InputBuffer
    {
        private readonly int _capacity;
        private readonly Dictionary<int, byte[]>[] _frames; // [ringIndex][playerId] = inputData

        public InputBuffer(int capacity = 128)
        {
            _capacity = capacity;
            _frames = new Dictionary<int, byte[]>[capacity];
            for (int i = 0; i < capacity; i++)
                _frames[i] = new Dictionary<int, byte[]>();
        }

        private int RingIndex(uint frame) => (int)(frame % _capacity);

        // 预分配的 byte[] 池，避免每次 Set 都 new
        private readonly Queue<byte[]> _bufferPool = new();
        private int _defaultInputSize = 8;

        private byte[] RentBuffer(int size)
        {
            if (_bufferPool.Count > 0)
            {
                var buf = _bufferPool.Dequeue();
                if (buf.Length >= size) return buf;
                // 大小不匹配，丢弃（罕见）
            }
            return new byte[Math.Max(size, _defaultInputSize)];
        }

        private void ReturnBuffer(byte[] buf)
        {
            if (buf != null && buf.Length > 0)
                _bufferPool.Enqueue(buf);
        }

        /// <summary>
        /// 设置某帧某玩家的输入（复制数据，使用缓冲池）
        /// </summary>
        public void Set(uint frame, int playerId, byte[] input)
        {
            var dict = _frames[RingIndex(frame)];
            if (input == null || input.Length == 0)
            {
                if (dict.TryGetValue(playerId, out var old) && old.Length > 0)
                    ReturnBuffer(old);
                dict[playerId] = Array.Empty<byte>();
                return;
            }
            // 尝试复用已有的 buffer
            if (dict.TryGetValue(playerId, out var existing) && existing != null && existing.Length >= input.Length)
            {
                Buffer.BlockCopy(input, 0, existing, 0, input.Length);
                return; // 原地更新，零分配
            }
            // 从池中租借
            var copy = RentBuffer(input.Length);
            Buffer.BlockCopy(input, 0, copy, 0, input.Length);
            dict[playerId] = copy;
        }

        /// <summary>
        /// 获取某帧某玩家的输入（没有则返回 null）
        /// </summary>
        public byte[]? Get(uint frame, int playerId)
        {
            _frames[RingIndex(frame)].TryGetValue(playerId, out var data);
            return data;
        }

        /// <summary>
        /// 获取某帧所有玩家的输入
        /// </summary>
        public FrameInput[] GetAll(uint frame)
        {
            var dict = _frames[RingIndex(frame)];
            var result = new FrameInput[dict.Count];
            int i = 0;
            foreach (var kvp in dict)
                result[i++] = new FrameInput(kvp.Key, kvp.Value);
            return result;
        }

        /// <summary>
        /// 预测远程玩家输入（用上一帧的输入的副本）
        /// </summary>
        public void PredictRemote(uint frame, int playerId)
        {
            var lastInput = Get(frame - 1, playerId);
            if (lastInput != null && lastInput.Length > 0)
                Set(frame, playerId, lastInput); // Set 内部会复制
        }

        /// <summary>
        /// 检查某帧某玩家的输入是否与给定数据一致
        /// </summary>
        public bool Matches(uint frame, int playerId, byte[] serverInput)
        {
            var local = Get(frame, playerId);
            if (local == null || serverInput == null) return local == serverInput;
            if (local.Length != serverInput.Length) return false;
            for (int i = 0; i < local.Length; i++)
                if (local[i] != serverInput[i]) return false;
            return true;
        }

        /// <summary>
        /// 清空某帧的数据（环形覆盖时自动清理）
        /// </summary>
        public void Clear(uint frame)
        {
            _frames[RingIndex(frame)].Clear();
        }

        /// <summary>
        /// 清空全部
        /// </summary>
        public void ClearAll()
        {
            for (int i = 0; i < _capacity; i++)
                _frames[i].Clear();
        }
    }
}
