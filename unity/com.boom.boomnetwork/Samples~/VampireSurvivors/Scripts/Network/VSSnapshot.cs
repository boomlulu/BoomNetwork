// BoomNetwork VampireSurvivors Demo — Snapshot Serialization (Fixed-Point)
//
// FInt fields serialized as their Raw int (4 bytes, same size as float).

using System;
using System.IO;

namespace BoomNetwork.Samples.VampireSurvivors
{
    public static class VSSnapshot
    {
        static readonly byte[] Magic = { (byte)'V', (byte)'S', (byte)'F', (byte)'X' }; // FX = fixed-point

        public static byte[] Serialize(VSSimulation sim)
        {
            var state = sim.State;
            using var ms = new MemoryStream(262144);
            using var w = new BinaryWriter(ms);

            w.Write(Magic);
            w.Write(state.FrameNumber);
            w.Write(state.RngState);
            w.Write(state.Dt.Raw);
            w.Write(state.WaveNumber);
            w.Write(state.WaveSpawnTimer);
            w.Write(state.WaveSpawnRemaining);
            w.Write(state.FocusFireTarget);
            w.Write(state.FocusFireTimer);

            for (int t = 0; t < GameState.MaxRevivalTotems; t++)
            {
                ref var totem = ref state.RevivalTotems[t];
                w.Write(totem.Active);
                w.Write(totem.PosX.Raw); w.Write(totem.PosZ.Raw);
                w.Write(totem.OwnerSlot); w.Write(totem.ReviveProgress);
            }

            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                ref var p = ref state.Players[i];
                w.Write(p.IsActive); w.Write(p.IsAlive);
                w.Write(p.PosX.Raw); w.Write(p.PosZ.Raw);
                w.Write(p.FacingX.Raw); w.Write(p.FacingZ.Raw);
                w.Write(p.Hp); w.Write(p.MaxHp);
                w.Write(p.Xp); w.Write(p.Level); w.Write(p.XpToNextLevel);
                w.Write(p.InvincibilityFrames);
                w.Write(p.KillCount);
                w.Write(p.PendingLevelUp); w.Write(p.UpgradeChoice);
                w.Write(p.UpgradeOpt0); w.Write(p.UpgradeOpt1); w.Write(p.UpgradeOpt2); w.Write(p.UpgradeOpt3);

                for (int ws = 0; ws < PlayerState.MaxWeaponSlots; ws++)
                {
                    var wslot = p.GetWeapon(ws);
                    w.Write((byte)wslot.Type); w.Write(wslot.Level); w.Write(wslot.Cooldown);
                }
                for (int o = 0; o < PlayerState.MaxOrbs; o++)
                {
                    var orb = p.GetOrb(o);
                    w.Write(orb.Active); w.Write(orb.AngleDeg.Raw);
                }
            }

            // Enemies: write slot index to preserve layout across serialize/deserialize
            ushort enemyCount = 0;
            for (int i = 0; i < GameState.MaxEnemies; i++) if (state.Enemies[i].IsAlive) enemyCount++;
            w.Write(enemyCount);
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref state.Enemies[i];
                if (!e.IsAlive) continue;
                w.Write((ushort)i); // slot index — critical for AllocEnemy determinism
                w.Write((byte)e.Type);
                w.Write(e.PosX.Raw); w.Write(e.PosZ.Raw);
                w.Write(e.DirX.Raw); w.Write(e.DirZ.Raw);
                w.Write(e.Hp); w.Write(e.TargetPlayerId); w.Write(e.BehaviorTimer);
                w.Write(e.SlowFrames); w.Write(e.LinkedEnemyIdx); w.Write(e.HitWindowTimer);
            }

            // Projectiles: write slot index
            ushort projCount = 0;
            for (int i = 0; i < GameState.MaxProjectiles; i++) if (state.Projectiles[i].IsAlive) projCount++;
            w.Write(projCount);
            for (int i = 0; i < GameState.MaxProjectiles; i++)
            {
                ref var p = ref state.Projectiles[i];
                if (!p.IsAlive) continue;
                w.Write((ushort)i); // slot index
                w.Write((byte)p.Type);
                w.Write(p.PosX.Raw); w.Write(p.PosZ.Raw);
                w.Write(p.DirX.Raw); w.Write(p.DirZ.Raw);
                w.Write(p.Radius.Raw);
                w.Write(p.LifetimeFrames); w.Write(p.OwnerPlayerId); w.Write(p.DamageTick);
            }

            // Gems: write slot index
            ushort gemCount = 0;
            for (int i = 0; i < GameState.MaxGems; i++) if (state.Gems[i].IsAlive) gemCount++;
            w.Write(gemCount);
            for (int i = 0; i < GameState.MaxGems; i++)
            {
                ref var g = ref state.Gems[i];
                if (!g.IsAlive) continue;
                w.Write((ushort)i); // slot index
                w.Write(g.Attracting);
                w.Write(g.PosX.Raw); w.Write(g.PosZ.Raw); w.Write(g.Value);
            }

            w.Write((byte)GameState.MaxLightningFlashes);
            for (int i = 0; i < GameState.MaxLightningFlashes; i++)
            {
                ref var f = ref state.Flashes[i];
                w.Write(f.PosX.Raw); w.Write(f.PosZ.Raw); w.Write(f.FramesLeft);
            }

            // Pid→Slot mapping (deterministic, needed for late-joiners)
            sim.GetPidMap(out var pidMap, out int nextSlot);
            w.Write((byte)nextSlot);
            for (int i = 0; i < pidMap.Length; i++)
            {
                if (pidMap[i] >= 0)
                {
                    w.Write((byte)i);          // pid
                    w.Write((byte)pidMap[i]);   // slot
                }
            }
            w.Write((byte)255); // terminator

            return ms.ToArray();
        }

        public static void Deserialize(byte[] data, VSSimulation sim)
        {
            var state = sim.State;
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);

            r.ReadBytes(4);
            state.FrameNumber = r.ReadUInt32();
            state.RngState = r.ReadUInt32();
            state.Dt = new FInt(r.ReadInt32());
            state.WaveNumber = r.ReadInt32();
            state.WaveSpawnTimer = r.ReadUInt32();
            state.WaveSpawnRemaining = r.ReadUInt32();
            state.FocusFireTarget = r.ReadInt32();
            state.FocusFireTimer = r.ReadUInt32();

            for (int t = 0; t < GameState.MaxRevivalTotems; t++)
            {
                ref var totem = ref state.RevivalTotems[t];
                totem.Active = r.ReadBoolean();
                totem.PosX = new FInt(r.ReadInt32()); totem.PosZ = new FInt(r.ReadInt32());
                totem.OwnerSlot = r.ReadInt32(); totem.ReviveProgress = r.ReadUInt32();
            }

            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                ref var p = ref state.Players[i];
                p.IsActive = r.ReadBoolean(); p.IsAlive = r.ReadBoolean();
                p.PosX = new FInt(r.ReadInt32()); p.PosZ = new FInt(r.ReadInt32());
                p.FacingX = new FInt(r.ReadInt32()); p.FacingZ = new FInt(r.ReadInt32());
                p.Hp = r.ReadInt32(); p.MaxHp = r.ReadInt32();
                p.Xp = r.ReadInt32(); p.Level = r.ReadInt32(); p.XpToNextLevel = r.ReadInt32();
                p.InvincibilityFrames = r.ReadUInt32();
                p.KillCount = r.ReadInt32();
                p.PendingLevelUp = r.ReadBoolean(); p.UpgradeChoice = r.ReadByte();
                p.UpgradeOpt0 = r.ReadByte(); p.UpgradeOpt1 = r.ReadByte();
                p.UpgradeOpt2 = r.ReadByte(); p.UpgradeOpt3 = r.ReadByte();

                for (int ws = 0; ws < PlayerState.MaxWeaponSlots; ws++)
                {
                    var wslot = new WeaponSlot();
                    wslot.Type = (WeaponType)r.ReadByte();
                    wslot.Level = r.ReadInt32(); wslot.Cooldown = r.ReadUInt32();
                    p.SetWeapon(ws, wslot);
                }
                for (int o = 0; o < PlayerState.MaxOrbs; o++)
                {
                    var orb = new OrbState();
                    orb.Active = r.ReadBoolean(); orb.AngleDeg = new FInt(r.ReadInt32());
                    p.SetOrb(o, orb);
                }
            }

            Array.Clear(state.Enemies, 0, GameState.MaxEnemies);
            Array.Clear(state.Projectiles, 0, GameState.MaxProjectiles);
            Array.Clear(state.Gems, 0, GameState.MaxGems);
            Array.Clear(state.Flashes, 0, GameState.MaxLightningFlashes);

            // Enemies: restore to original slot index
            ushort enemyCount = r.ReadUInt16();
            for (int i = 0; i < enemyCount; i++)
            {
                int slot = r.ReadUInt16();
                ref var e = ref state.Enemies[slot];
                e.IsAlive = true;
                e.Type = (EnemyType)r.ReadByte();
                e.PosX = new FInt(r.ReadInt32()); e.PosZ = new FInt(r.ReadInt32());
                e.DirX = new FInt(r.ReadInt32()); e.DirZ = new FInt(r.ReadInt32());
                e.Hp = r.ReadInt32(); e.TargetPlayerId = r.ReadInt32(); e.BehaviorTimer = r.ReadUInt32();
                e.SlowFrames = r.ReadUInt32(); e.LinkedEnemyIdx = r.ReadInt32(); e.HitWindowTimer = r.ReadUInt32();
            }

            // Projectiles: restore to original slot index
            ushort projCount = r.ReadUInt16();
            for (int i = 0; i < projCount; i++)
            {
                int slot = r.ReadUInt16();
                ref var p = ref state.Projectiles[slot];
                p.IsAlive = true;
                p.Type = (ProjectileType)r.ReadByte();
                p.PosX = new FInt(r.ReadInt32()); p.PosZ = new FInt(r.ReadInt32());
                p.DirX = new FInt(r.ReadInt32()); p.DirZ = new FInt(r.ReadInt32());
                p.Radius = new FInt(r.ReadInt32());
                p.LifetimeFrames = r.ReadUInt32(); p.OwnerPlayerId = r.ReadInt32(); p.DamageTick = r.ReadUInt32();
            }

            // Gems: restore to original slot index
            ushort gemCount = r.ReadUInt16();
            for (int i = 0; i < gemCount; i++)
            {
                int slot = r.ReadUInt16();
                ref var g = ref state.Gems[slot];
                g.IsAlive = true;
                g.Attracting = r.ReadBoolean();
                g.PosX = new FInt(r.ReadInt32()); g.PosZ = new FInt(r.ReadInt32()); g.Value = r.ReadInt32();
            }

            byte flashCount = r.ReadByte();
            for (int i = 0; i < flashCount; i++)
            {
                ref var f = ref state.Flashes[i];
                f.PosX = new FInt(r.ReadInt32()); f.PosZ = new FInt(r.ReadInt32()); f.FramesLeft = r.ReadUInt32();
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
        }
    }
}
