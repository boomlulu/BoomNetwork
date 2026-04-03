// BoomNetwork TowerDefense Demo — Snapshot Serialization
//
// FInt fields serialized as Raw int. Slot indices preserved for AllocEnemy determinism.
// FlowField is NOT serialized — rebuilt from Grid after deserialization.

using System;
using System.IO;

namespace BoomNetwork.Samples.TowerDefense
{
    public static class TDSnapshot
    {
        static readonly byte[] Magic = { (byte)'T', (byte)'D', (byte)'F', (byte)'X' };

        public static byte[] Serialize(TDSimulation sim)
        {
            var state = sim.State;
            using var ms = new MemoryStream(4096);
            using var w = new BinaryWriter(ms);

            w.Write(Magic);
            w.Write(state.FrameNumber);
            w.Write(state.RngState);
            w.Write(state.BaseHp);
            w.Write(state.SpeedMode);
            w.Write(state.SpeedCounter);
            // Layered economy
            for (int p = 0; p < GameState.MaxPlayers; p++)
                w.Write(state.PlayerGold[p]);
            w.Write(state.SharedGold);

            // Wave state
            w.Write(state.Wave.WaveNumber);
            w.Write(state.Wave.SpawnRemaining);
            w.Write(state.Wave.InterWaveTimer);
            w.Write(state.Wave.AllWavesDone);
            w.Write(WaveSystem.SpawnTickCounter);

            // Grid (all 400 cells)
            for (int i = 0; i < GameState.GridSize; i++)
            {
                ref var t = ref state.Grid[i];
                w.Write((byte)t.Type);
                w.Write(t.CooldownFrames);
                w.Write((byte)t.Level);
                w.Write(t.OwnerId);  // -1 = TeamOwner, 0-3 = player slot
            }

            // Enemies: write slot index for determinism
            ushort enemyCount = 0;
            for (int i = 0; i < GameState.MaxEnemies; i++)
                if (state.Enemies[i].IsAlive) enemyCount++;
            w.Write(enemyCount);
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref state.Enemies[i];
                if (!e.IsAlive) continue;
                w.Write((ushort)i);
                w.Write((byte)e.Type);
                w.Write(e.PosX.Raw);
                w.Write(e.PosZ.Raw);
                w.Write(e.Hp);
                w.Write(e.SlowFrames);
            }

            // Pid→Slot mapping
            sim.GetPidMap(out var pidMap, out int nextSlot);
            w.Write((byte)nextSlot);
            for (int i = 0; i < pidMap.Length; i++)
            {
                if (pidMap[i] >= 0)
                {
                    w.Write((byte)i);
                    w.Write((byte)pidMap[i]);
                }
            }
            w.Write((byte)255); // terminator

            return ms.ToArray();
        }

        public static void Deserialize(byte[] data, TDSimulation sim)
        {
            var state = sim.State;
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);

            r.ReadBytes(4); // magic
            state.FrameNumber = r.ReadUInt32();
            state.RngState    = r.ReadUInt32();
            state.BaseHp      = r.ReadInt32();
            state.SpeedMode    = r.ReadByte();
            state.SpeedCounter = r.ReadByte();
            // Layered economy
            for (int p = 0; p < GameState.MaxPlayers; p++)
                state.PlayerGold[p] = r.ReadInt32();
            state.SharedGold = r.ReadInt32();

            state.Wave = new WaveState
            {
                WaveNumber       = r.ReadInt32(),
                SpawnRemaining   = r.ReadInt32(),
                InterWaveTimer   = r.ReadInt32(),
                AllWavesDone     = r.ReadBoolean(),
            };
            WaveSystem.SpawnTickCounter = r.ReadInt32();

            // Grid
            for (int i = 0; i < GameState.GridSize; i++)
            {
                state.Grid[i] = new Tower
                {
                    Type           = (TowerType)r.ReadByte(),
                    CooldownFrames = r.ReadInt32(),
                    Level          = r.ReadByte(),
                    OwnerId        = r.ReadInt32(),
                };
            }

            // Enemies
            Array.Clear(state.Enemies, 0, GameState.MaxEnemies);
            ushort enemyCount = r.ReadUInt16();
            for (int i = 0; i < enemyCount; i++)
            {
                int slot = r.ReadUInt16();
                ref var e = ref state.Enemies[slot];
                e.IsAlive    = true;
                e.Type       = (EnemyType)r.ReadByte();
                e.PosX       = new FInt(r.ReadInt32());
                e.PosZ       = new FInt(r.ReadInt32());
                e.Hp         = r.ReadInt32();
                e.SlowFrames = r.ReadInt32();
            }

            // Pid→Slot mapping
            if (ms.Position < ms.Length)
            {
                int nextSlot = r.ReadByte();
                var pidMap = new int[256];
                for (int i = 0; i < pidMap.Length; i++) pidMap[i] = -1;
                while (true)
                {
                    byte pid = r.ReadByte();
                    if (pid == 255) break;
                    pidMap[pid] = r.ReadByte();
                }
                sim.SetPidMap(pidMap, nextSlot);
            }

            // Rebuild flow field from restored grid (not serialized)
            PathSystem.Rebuild(state);
        }
    }
}
