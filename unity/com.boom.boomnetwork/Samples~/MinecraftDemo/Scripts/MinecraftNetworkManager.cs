// BoomNetwork MinecraftDemo — Main Network Manager
//
// Hybrid sync mode:
//   - Player movement: IEntitySync (state sync, auto-sent with each SendInput)
//   - Block changes: Frame Sync (SendInput → OnFrame, deterministic order)
//   - World state: Snapshot (delta-only, for reconnection)
//
// Usage: Attach to a GameObject alongside BoomNetworkManager.
//        Set BoomNetworkManager.matchKey = "minecraft"

using System;
using System.Collections.Generic;
using UnityEngine;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Unity;

namespace BoomNetwork.Samples.MinecraftDemo
{
    [RequireComponent(typeof(BoomNetworkManager))]
    public class MinecraftNetworkManager : MonoBehaviour
    {
        [Header("World")]
        [SerializeField] VoxelWorld voxelWorld;
        [SerializeField] int worldSeed = 42;

        [Header("Player Prefab (optional)")]
        [SerializeField] GameObject remotePlayerPrefab;

        BoomNetworkManager _network;
        MinecraftPlayerController _localController;
        MinecraftPlayerSync _localSync;

        readonly Dictionary<int, GameObject> _remotePlayers = new();
        readonly byte[] _inputBuf = new byte[MinecraftInput.InputSize];

        uint _lastFrame;
        bool _authorityRegistered;
        bool _worldGenerated;

        static readonly Color[] PlayerColors =
        {
            new Color(0.2f, 0.8f, 0.3f),
            new Color(0.3f, 0.5f, 1f),
            new Color(1f, 0.3f, 0.3f),
            new Color(1f, 0.9f, 0.2f),
        };

        void Start()
        {
            _network = GetComponent<BoomNetworkManager>();

            var c = _network.Client;
            c.OnFrameSyncStart += OnFrameSyncStart;
            c.OnFrame += OnFrame;
            c.OnEntityState += OnEntityState;
            c.OnJoinedRoom += OnJoinedRoom;
            c.OnPlayerJoined += OnPlayerJoined;
            c.OnPlayerLeft += OnPlayerLeft;
            c.OnTakeSnapshot = TakeSnapshot;
            c.OnLoadSnapshot = LoadSnapshot;

            // Configure world
            if (voxelWorld != null)
                voxelWorld.Seed = worldSeed;

            _network.QuickStart();
        }

        void Update()
        {
            if (!_network.IsSyncing) return;

            // Register authority entity once PlayerId is assigned
            if (!_authorityRegistered && _network.PlayerId > 0)
            {
                SpawnLocalPlayer();
                _authorityRegistered = true;
            }

            // Process chunk mesh queue
            if (voxelWorld != null)
            {
                if (_localController != null)
                    voxelWorld.UpdatePlayerPosition(_localController.transform.position);
                voxelWorld.ProcessMeshQueue();
            }

            // Collect block action from controller and send as frame sync input
            SendInput();
        }

        void SendInput()
        {
            if (_localController == null) return;

            var action = _localController.PendingAction;
            _localController.PendingAction = default; // consume

            MinecraftInput.Encode(_inputBuf, 0,
                action.ActionType, action.Position, action.BlockType);

            _network.SendInput(_inputBuf);
        }

        // --- Frame Sync Start: generate world ---

        void OnFrameSyncStart(FrameSyncInitData data)
        {
            if (!_worldGenerated && voxelWorld != null)
            {
                voxelWorld.GenerateFullWorld();
                _worldGenerated = true;
            }
        }

        // --- Frame Sync: apply block changes deterministically ---

        void OnFrame(FrameData frame)
        {
            _lastFrame = frame.FrameNumber;
            if (frame.Inputs == null || voxelWorld == null) return;

            for (int i = 0; i < frame.Inputs.Length; i++)
            {
                ref var input = ref frame.Inputs[i];
                if (input.DataLength < MinecraftInput.InputSize) continue;

                MinecraftInput.Decode(input.Data, 0,
                    out byte actionType, out var pos, out BlockType blockType);

                if (actionType == 0) continue; // no action

                if (actionType == 1)
                {
                    // Break block
                    voxelWorld.SetBlock(pos, BlockType.Air);
                }
                else if (actionType == 2)
                {
                    // Place block
                    voxelWorld.SetBlock(pos, blockType);
                }
            }
        }

        // --- Entity State: route to remote player syncs ---

        void OnEntityState(int senderPid, int entityId, byte[] data, int offset, int length)
        {
            if (_remotePlayers.TryGetValue(entityId, out var go))
            {
                var sync = go.GetComponent<MinecraftPlayerSync>();
                if (sync != null)
                    sync.OnRemoteState(data, offset, length, senderPid);
            }
        }

        // --- Player Management ---

        void OnJoinedRoom(int roomId, int[] existingPlayers)
        {
            foreach (int pid in existingPlayers)
            {
                if (pid != _network.PlayerId)
                    SpawnRemotePlayer(pid);
            }
        }

        void OnPlayerJoined(int playerId)
        {
            if (playerId != _network.PlayerId)
                SpawnRemotePlayer(playerId);
        }

        void OnPlayerLeft(int playerId)
        {
            if (_remotePlayers.TryGetValue(playerId, out var go))
            {
                Destroy(go);
                _remotePlayers.Remove(playerId);
            }
        }

        void SpawnLocalPlayer()
        {
            var go = new GameObject("LocalPlayer");
            go.transform.position = GetSpawnPosition();

            _localController = go.AddComponent<MinecraftPlayerController>();

            _localSync = go.AddComponent<MinecraftPlayerSync>();
            _localSync.Init(_network.PlayerId, true);

            _network.Client.RegisterAuthorityEntity(_localSync);
        }

        void SpawnRemotePlayer(int playerId)
        {
            if (_remotePlayers.ContainsKey(playerId)) return;

            GameObject go;
            if (remotePlayerPrefab != null)
            {
                go = Instantiate(remotePlayerPrefab);
            }
            else
            {
                // Default: capsule with color
                go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
                var renderer = go.GetComponent<Renderer>();
                renderer.material.color = PlayerColors[(playerId - 1) % PlayerColors.Length];
                // Remove default capsule collider to avoid physics interference
                var col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);
            }

            go.name = $"RemotePlayer_{playerId}";
            go.transform.position = GetSpawnPosition();

            var sync = go.AddComponent<MinecraftPlayerSync>();
            sync.Init(playerId, false);

            _remotePlayers[playerId] = go;
        }

        Vector3 GetSpawnPosition()
        {
            // Spawn above world center
            float x = VoxelConstants.WorldBlocksX * 0.5f;
            float z = VoxelConstants.WorldBlocksZ * 0.5f;
            float y = VoxelConstants.WorldBlocksY + 2f; // above max height, will fall
            return new Vector3(x, y, z);
        }

        // --- Snapshot ---

        byte[] TakeSnapshot()
        {
            if (voxelWorld == null) return null;
            return MinecraftSnapshot.TakeSnapshot(voxelWorld);
        }

        void LoadSnapshot(byte[] data)
        {
            if (voxelWorld == null || data == null) return;
            MinecraftSnapshot.LoadSnapshot(voxelWorld, data);
            _worldGenerated = true;
        }

        // --- UI ---

        void OnGUI()
        {
            var title = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            var label = new GUIStyle(GUI.skin.label) { fontSize = 13 };

            GUILayout.BeginArea(new Rect(10, 10, 350, 200));
            GUILayout.Label("Minecraft Demo", title);
            GUILayout.Label($"State: {_network.Client.CurrentState}", label);
            GUILayout.Label($"Player: {_network.PlayerId}  Frame: {_lastFrame}", label);
            GUILayout.Label($"RTT: {_network.Client.RttMs:F0}ms  Players: {1 + _remotePlayers.Count}", label);

            if (_localController != null)
            {
                var block = _localController.SelectedBlockType;
                GUILayout.Label($"Selected: [{(int)block}] {block}", label);

                if (_localController.HasTarget)
                    GUILayout.Label($"Target: {_localController.TargetBlockPos}", label);
            }

            GUILayout.EndArea();

            // Crosshair
            DrawCrosshair();

            // Block selection bar
            DrawBlockBar();
        }

        void DrawCrosshair()
        {
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            float size = 12f;
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(cx - 1, cy - size, 2, size * 2), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - size, cy - 1, size * 2, 2), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        void DrawBlockBar()
        {
            if (_localController == null) return;

            float barWidth = BlockTypeExt.Count * 40f;
            float startX = (Screen.width - barWidth) * 0.5f;
            float y = Screen.height - 50f;

            for (int i = 1; i < BlockTypeExt.Count; i++)
            {
                var block = (BlockType)i;
                bool selected = _localController.SelectedBlockType == block;

                var style = new GUIStyle(GUI.skin.button)
                {
                    fontSize = 10,
                    fontStyle = selected ? FontStyle.Bold : FontStyle.Normal,
                };

                var rect = new Rect(startX + (i - 1) * 40f, y, 38f, 38f);
                if (selected)
                {
                    GUI.color = Color.yellow;
                    GUI.DrawTexture(new Rect(rect.x - 2, rect.y - 2, rect.width + 4, rect.height + 4),
                        Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }

                if (GUI.Button(rect, $"{i}\n{block}", style))
                    _localController.SelectedBlockType = block;
            }
        }
    }
}
