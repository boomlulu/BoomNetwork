using System;
using System.Collections.Generic;
using BoomNetwork.Core.FrameSync;

namespace BoomNetwork.Core.Prediction
{
    /// <summary>
    /// 预测回滚管理器
    ///
    /// 核心流程:
    ///   1. 本地输入 → 立刻预测执行 → 玩家零延迟体验
    ///   2. 服务器确认帧到达 → 比对预测 → 一致则推进，不一致则回滚
    ///   3. 回滚: 恢复快照 → 用正确输入重新执行 → 修正状态
    /// </summary>
    public class PredictionManager
    {
        private readonly ISimulation _simulation;
        private readonly InputBuffer _inputBuffer;
        private readonly SnapshotBuffer _snapshotBuffer;

        /// <summary>
        /// 最大预测窗口（超过此帧数未确认则暂停预测）
        /// </summary>
        public int MaxPredictionFrames { get; set; } = 8;

        /// <summary>
        /// 已确认帧号（服务器已验证的最后一帧）
        /// </summary>
        public uint ConfirmedFrame { get; private set; }

        /// <summary>
        /// 已预测帧号（本地已执行的最后一帧）
        /// </summary>
        public uint PredictedFrame { get; private set; }

        /// <summary>
        /// 预测领先帧数
        /// </summary>
        public int AheadFrames => (int)(PredictedFrame - ConfirmedFrame);

        /// <summary>
        /// 总回滚次数（统计）
        /// </summary>
        public int TotalRollbacks { get; private set; }

        /// <summary>
        /// 总回滚帧数（统计）
        /// </summary>
        public int TotalRollbackFrames { get; private set; }

        /// <summary>
        /// 帧执行回调（每次 Simulate 后触发，包括回滚重执行）
        /// isRollback: true = 回滚重执行，false = 正常/预测执行
        /// </summary>
        public Action<uint, bool>? OnFrameSimulated;

        /// <summary>
        /// 回滚发生时回调
        /// (rollbackToFrame, replayCount)
        /// </summary>
        public Action<uint, int>? OnRollback;

        // 本地玩家 ID
        private int _localPlayerId;
        // 房间内所有玩家 ID
        private readonly List<int> _playerIds = new();
        // 服务器确认帧队列
        private readonly Queue<FrameData> _serverFrameQueue = new();

        private bool _started;

        /// <summary>
        /// 服务器帧间隔（毫秒）。预测帧率 = 1000 / FrameIntervalMs。
        /// 必须和服务器一致（默认 50ms = 20fps）。
        /// </summary>
        public float FrameIntervalMs { get; set; } = 50f;
        private float _predictionAccumulator;

        public PredictionManager(ISimulation simulation, int inputBufferCapacity = 128, int snapshotBufferCapacity = 32)
        {
            _simulation = simulation;
            _inputBuffer = new InputBuffer(inputBufferCapacity);
            _snapshotBuffer = new SnapshotBuffer(snapshotBufferCapacity);
        }

        /// <summary>
        /// 启动预测（帧同步开始时调用）
        /// </summary>
        public void Start(int localPlayerId, List<int> allPlayerIds)
        {
            _localPlayerId = localPlayerId;
            _playerIds.Clear();
            _playerIds.AddRange(allPlayerIds);
            ConfirmedFrame = 0;
            PredictedFrame = 0;
            TotalRollbacks = 0;
            TotalRollbackFrames = 0;
            _inputBuffer.ClearAll();
            _snapshotBuffer.Clear();
            _serverFrameQueue.Clear();
            _started = true;

            // 保存初始状态快照
            _snapshotBuffer.Save(0, _simulation.SaveState());
        }

        /// <summary>
        /// 停止预测
        /// </summary>
        public void Stop()
        {
            _started = false;
        }

        /// <summary>
        /// 每帧调用，传入 deltaTime 和当前本地输入。
        /// 内部按 FrameIntervalMs 节流，只在到达帧间隔时才预测执行。
        /// </summary>
        /// <param name="deltaTimeMs">Unity deltaTime * 1000</param>
        /// <param name="localInput">当前本地输入（每帧都传，内部决定何时消费）</param>
        /// <returns>本帧是否执行了预测</returns>
        public bool UpdatePrediction(float deltaTimeMs, byte[] localInput)
        {
            if (!_started) return false;

            _predictionAccumulator += deltaTimeMs;
            bool predicted = false;

            // 按服务器帧率节流（可能一次追多帧，但通常只执行 0-1 帧）
            while (_predictionAccumulator >= FrameIntervalMs)
            {
                _predictionAccumulator -= FrameIntervalMs;

                if (AheadFrames >= MaxPredictionFrames) break;

                PredictSingleFrame(localInput);
                predicted = true;
            }

            return predicted;
        }

        /// <summary>
        /// 直接预测一帧（不经过节流，供高级用途或测试）
        /// </summary>
        public void PredictFrame(byte[] localInput)
        {
            if (!_started) return;
            if (AheadFrames >= MaxPredictionFrames) return;
            PredictSingleFrame(localInput);
        }

        private void PredictSingleFrame(byte[] localInput)
        {
            uint nextFrame = PredictedFrame + 1;

            _inputBuffer.Set(nextFrame, _localPlayerId, localInput);

            foreach (var pid in _playerIds)
            {
                if (pid == _localPlayerId) continue;
                _inputBuffer.PredictRemote(nextFrame, pid);
            }

            _snapshotBuffer.Save(nextFrame, _simulation.SaveState());

            var allInputs = _inputBuffer.GetAll(nextFrame);
            _simulation.Simulate(allInputs);
            PredictedFrame = nextFrame;

            OnFrameSimulated?.Invoke(nextFrame, false);
        }

        /// <summary>
        /// 服务器确认帧到达时调用
        /// </summary>
        public void OnServerFrame(FrameData serverFrame)
        {
            if (!_started) return;
            _serverFrameQueue.Enqueue(serverFrame);
        }

        /// <summary>
        /// 处理服务器确认帧（每帧 Update 调用一次）
        /// 可能触发回滚
        /// </summary>
        public void ProcessServerFrames()
        {
            if (!_started) return;

            while (_serverFrameQueue.Count > 0)
            {
                var serverFrame = _serverFrameQueue.Dequeue();
                ProcessSingleServerFrame(serverFrame);
            }
        }

        // 复用集合避免每帧分配
        private readonly HashSet<int> _serverInputPlayers = new();

        private void ProcessSingleServerFrame(FrameData serverFrame)
        {
            uint frame = serverFrame.FrameNumber;

            // 存储服务器确认的输入
            bool needRollback = false;
            _serverInputPlayers.Clear();

            if (serverFrame.Inputs != null)
            {
                for (int i = 0; i < serverFrame.Inputs.Length; i++)
                {
                    int pid = serverFrame.Inputs[i].PlayerId;
                    byte[] serverInput = serverFrame.Inputs[i].Data;
                    _serverInputPlayers.Add(pid);

                    // 检查预测是否正确
                    if (frame <= PredictedFrame && !_inputBuffer.Matches(frame, pid, serverInput))
                    {
                        needRollback = true;
                    }

                    // 用服务器的真实输入覆盖
                    _inputBuffer.Set(frame, pid, serverInput);
                }
            }

            // 检查: 客户端预测了某个远程玩家有输入，但服务器帧里没有该玩家
            // 说明该玩家实际没输入，但我们沿用了上一帧的输入 → 预测错误
            if (frame <= PredictedFrame)
            {
                foreach (var pid in _playerIds)
                {
                    if (pid == _localPlayerId) continue;
                    if (_serverInputPlayers.Contains(pid)) continue; // 服务器有这个玩家的输入，已在上面比对过

                    // 服务器没有这个玩家的输入 = 该玩家本帧无操作
                    var predicted = _inputBuffer.Get(frame, pid);
                    if (predicted != null && predicted.Length > 0)
                    {
                        // 我们预测了有输入，但实际没有 → 回滚
                        needRollback = true;
                        _inputBuffer.Set(frame, pid, Array.Empty<byte>()); // 清空为无输入
                    }
                }
            }

            // 如果这帧超过了预测帧，说明预测落后了，直接执行
            if (frame > PredictedFrame)
            {
                // 没有预测过这帧，直接执行
                _snapshotBuffer.Save(frame, _simulation.SaveState());
                var allInputs = _inputBuffer.GetAll(frame);
                _simulation.Simulate(allInputs);
                PredictedFrame = frame;
                ConfirmedFrame = frame;
                OnFrameSimulated?.Invoke(frame, false);
                return;
            }

            ConfirmedFrame = frame;

            if (needRollback)
            {
                DoRollback(frame);
            }
        }

        private void DoRollback(uint fromFrame)
        {
            // 1. 恢复到 fromFrame 执行前的快照
            var snapshot = _snapshotBuffer.Get(fromFrame);
            if (snapshot == null)
            {
                // 快照已被覆盖，无法回滚（预测太远）
                return;
            }

            _simulation.LoadState(snapshot);

            // 2. 从 fromFrame 重新执行到 PredictedFrame
            int replayCount = (int)(PredictedFrame - fromFrame + 1);
            TotalRollbacks++;
            TotalRollbackFrames += replayCount;

            OnRollback?.Invoke(fromFrame, replayCount);

            for (uint f = fromFrame; f <= PredictedFrame; f++)
            {
                // 重新保存快照（用修正后的状态）
                if (f > fromFrame)
                    _snapshotBuffer.Save(f, _simulation.SaveState());

                var allInputs = _inputBuffer.GetAll(f);
                _simulation.Simulate(allInputs);
                OnFrameSimulated?.Invoke(f, true);
            }
        }

        /// <summary>
        /// 从快照恢复（重连时使用）
        /// </summary>
        public void LoadFromSnapshot(byte[] snapshotData, uint snapshotFrame)
        {
            _simulation.LoadState(snapshotData);
            ConfirmedFrame = snapshotFrame;
            PredictedFrame = snapshotFrame;
            _snapshotBuffer.Clear();
            _snapshotBuffer.Save(snapshotFrame, snapshotData);
        }
    }
}
