// BoomNetwork VampireSurvivors Demo — Layered Log Utility
//
// 使用方式：
//   VSLog.Enabled = VSLog.Channel.Default;    // 日常运行：只看关键事件 + 不同步
//   VSLog.Enabled = VSLog.Channel.DiagWave;   // 诊断波次/玩家初始化问题（+ Wave + Player）
//   VSLog.Enabled = VSLog.Channel.Verbose;    // 全开（含 Upgrade）
//   VSLog.Enabled = VSLog.Channel.Wave;       // 只看波次（Key + Desync 仍强制显示）
//
// Key   → 强制显示，前缀 [VS]，不加 channel tag（房间/同步启停/快照）
// Desync → 强制显示，LogError，前缀 [VS][Desync]（不同步 dump）
// Player  → 玩家加入/离开/slot 分配
// Upgrade → 升级选择发送
// Wave    → 波次启动 + HasAlivePlayers 转变（定点数帧诊断）

using UnityEngine;

namespace BoomNetwork.Samples.VampireSurvivors
{
    public static class VSLog
    {
        [System.Flags]
        public enum Channel : uint
        {
            None    = 0,
            Key     = 1u << 0,   // 总是显示 — 不可关闭
            Desync  = 1u << 1,   // 总是显示 — 不同步 dump（LogError）
            Player  = 1u << 2,   // 玩家加入 / 离开 / slot 分配
            Upgrade = 1u << 3,   // 升级选择
            Wave    = 1u << 4,   // 波次 + HasAlivePlayers 转变

            // --- 预设 ---
            /// <summary>日常运行：只看关键事件 + 不同步报告。</summary>
            Default  = Key | Desync,
            /// <summary>诊断波次/玩家初始化分叉时使用。</summary>
            DiagWave = Key | Desync | Wave | Player,
            /// <summary>全部 channel 打开。</summary>
            Verbose  = Key | Desync | Player | Upgrade | Wave,
        }

        /// <summary>
        /// 当前会话启用的 channel 组合。Key 和 Desync 无论此值为何都会强制输出。
        /// 在 Start() 或 Inspector 代码里设置即可，无需重启。
        /// </summary>
        public static Channel Enabled = Channel.Default;

        /// <summary>普通日志。Key channel 强制输出，其他 channel 受 Enabled 控制。</summary>
        public static void Log(Channel ch, string msg)
        {
            if (!ShouldLog(ch)) return;
            Debug.Log(ch == Channel.Key ? $"[VS] {msg}" : $"[VS][{ch}] {msg}");
        }

        /// <summary>错误日志。Desync channel 强制输出，其他 channel 受 Enabled 控制。</summary>
        public static void Error(Channel ch, string msg)
        {
            if (!ShouldLog(ch)) return;
            Debug.LogError(ch == Channel.Key ? $"[VS] {msg}" : $"[VS][{ch}] {msg}");
        }

        static bool ShouldLog(Channel ch) =>
            ch == Channel.Key || ch == Channel.Desync || (Enabled & ch) != 0;
    }
}
