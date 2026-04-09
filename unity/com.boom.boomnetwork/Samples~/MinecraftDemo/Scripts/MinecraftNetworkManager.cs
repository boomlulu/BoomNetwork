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
using Unity.Mathematics;
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

        public VoxelWorld VoxelWorld { set => voxelWorld = value; }
        public int WorldSeed { set => worldSeed = value; }

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
        float _entitySendTimer;
        Vector3 _lastSentPos;
        float _lastSentRotY;
        const float PositionThreshold = 0.01f;
        const float RotationThreshold = 0.5f;
        const float EntitySendIntervalMs = 50f; // 20fps throttle for entity state

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
            c.OnPlayerJoinedMsg += OnPlayerJoined;
            c.OnPlayerLeftMsg   += OnPlayerLeft;
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

            // --- Block action: only send when player actually does something ---
            SendBlockActionIfNeeded();

            // --- Entity position: only send when moved/rotated (throttled to 20fps) ---
            SendEntityStateIfMoved();
        }

        void SendBlockActionIfNeeded()
        {
            if (_localController == null) return;

            var action = _localController.PendingAction;
            if (action.ActionType == 0) return; // no action, send nothing

            _localController.PendingAction = default; // consume

            // Red Line #4: 本地立即执行，零延迟。OnFrame 回来时幂等重复执行无副作用。
            ApplyBlockAction(action.ActionType, action.Position, action.BlockType);

            MinecraftInput.Encode(_inputBuf, 0,
                action.ActionType, action.Position, action.BlockType);

            _network.SendInput(_inputBuf);
        }

        void SendEntityStateIfMoved()
        {
            if (_localController == null) return;

            _entitySendTimer += Time.deltaTime * 1000f;
            if (_entitySendTimer < EntitySendIntervalMs) return;
            _entitySendTimer -= EntitySendIntervalMs;

            var pos = _localController.transform.position;
            float rotY = _localController.transform.eulerAngles.y;

            bool posChanged = Vector3.SqrMagnitude(pos - _lastSentPos) > PositionThreshold * PositionThreshold;
            bool rotChanged = Mathf.Abs(Mathf.DeltaAngle(rotY, _lastSentRotY)) > RotationThreshold;

            if (!posChanged && !rotChanged) return; // idle, send nothing

            _lastSentPos = pos;
            _lastSentRotY = rotY;
            _network.Client.SendAuthorityEntityStates();
        }

        // --- Frame Sync Start: generate world ---

        void OnFrameSyncStart(FrameSyncInitData data)
        {
            if (!_worldGenerated && voxelWorld != null)
            {
                MinecraftSnapshot.ClearTracking();
                voxelWorld.GenerateFullWorld();
                _worldGenerated = true;

                System.GC.Collect();
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

                if (actionType == 0) continue;

                // SetBlock is idempotent: local player's action was already applied instantly,
                // remote players' actions are applied here for the first time.
                ApplyBlockAction(actionType, pos, blockType);
            }
        }

        void ApplyBlockAction(byte actionType, int3 pos, BlockType blockType)
        {
            BlockType current = voxelWorld.GetBlock(pos);
            BlockType target = actionType == 1 ? BlockType.Air : blockType;

            if (current == target) return; // idempotent: already applied

            voxelWorld.SetBlock(pos, target);

            // Track for snapshot: compare against generated original (pure math, no allocation)
            BlockType original = WorldGenerator.GetBlockAt(pos, voxelWorld.Seed);
            MinecraftSnapshot.TrackBlockChange(pos, target, original);
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
            {
                SpawnRemotePlayer(playerId);

                // New player just joined — send our current position so they don't see us at spawn.
                // This is the "Silent When Idle" exception: someone needs our state.
                _network.Client.SendAuthorityEntityStates();
            }
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
            // Root = physics position (transform.position.y = feet)
            var go = new GameObject("LocalPlayer");
            go.transform.position = GetSpawnPosition();

            // Visual capsule as child, offset up so it sits on top of feet
            // Capsule with scale (0.5, 0.9, 0.5) has visual height ~1.8, center offset = 0.9
            var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = "Body";
            visual.transform.SetParent(go.transform, false);
            visual.transform.localPosition = new Vector3(0, 0.9f, 0);
            visual.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
            visual.GetComponent<Renderer>().material.color = PlayerColors[(_network.PlayerId - 1) % PlayerColors.Length];
            var capsuleCol = visual.GetComponent<CapsuleCollider>();
            if (capsuleCol != null) Destroy(capsuleCol);

            _localController = go.AddComponent<MinecraftPlayerController>();
            _localController.World = voxelWorld;

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
                go = new GameObject();
                var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                visual.name = "Body";
                visual.transform.SetParent(go.transform, false);
                visual.transform.localPosition = new Vector3(0, 0.9f, 0);
                visual.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
                visual.GetComponent<Renderer>().material.color = PlayerColors[(playerId - 1) % PlayerColors.Length];
                var col = visual.GetComponent<Collider>();
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
            float x = VoxelConstants.WorldBlocksX * 0.5f;
            float z = VoxelConstants.WorldBlocksZ * 0.5f;
            float y = VoxelConstants.WorldBlocksY + 2f;
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

        // --- UI (cached styles — zero GC per frame) ---

        GUIStyle _titleStyle, _labelStyle, _btnStyle, _btnStyleBold, _helpHeaderStyle, _helpStyle;
        string[] _blockBarLabels;
        bool _stylesCached;

        void CacheStyles()
        {
            if (_stylesCached) return;
            _stylesCached = true;
            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            _btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 10 };
            _btnStyleBold = new GUIStyle(GUI.skin.button) { fontSize = 10, fontStyle = FontStyle.Bold };
            _helpHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
            };
            _helpStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true };

            _blockBarLabels = new string[BlockTypeExt.Count];
            for (int i = 1; i < BlockTypeExt.Count; i++)
                _blockBarLabels[i] = $"{i}\n{(BlockType)i}";
        }

        void OnGUI()
        {
            CacheStyles();

            GUILayout.BeginArea(new Rect(10, 10, 350, 200));
            GUILayout.Label("Minecraft Demo", _titleStyle);
            GUILayout.Label($"State: {_network.Client.CurrentState}", _labelStyle);
            GUILayout.Label($"Player: {_network.PlayerId}  Frame: {_lastFrame}", _labelStyle);
            GUILayout.Label($"RTT: {_network.Client.RttMs:F0}ms  Players: {1 + _remotePlayers.Count}", _labelStyle);

            if (_localController != null)
            {
                var block = _localController.SelectedBlockType;
                GUILayout.Label($"Selected: [{(int)block}] {block}", _labelStyle);
            }

            GUILayout.EndArea();

            DrawCrosshair();
            DrawBlockBar();
            DrawHelpPanel();
        }

        void DrawCrosshair()
        {
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            const float size = 12f;
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(cx - 1, cy - size, 2, size * 2), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - size, cy - 1, size * 2, 2), Texture2D.whiteTexture);
        }

        void DrawBlockBar()
        {
            if (_localController == null) return;

            float barWidth = BlockTypeExt.Count * 40f;
            float startX = (Screen.width - barWidth) * 0.5f;
            float y = Screen.height - 50f;

            for (int i = 1; i < BlockTypeExt.Count; i++)
            {
                bool selected = _localController.SelectedBlockType == (BlockType)i;
                var rect = new Rect(startX + (i - 1) * 40f, y, 38f, 38f);

                if (selected)
                {
                    GUI.color = Color.yellow;
                    GUI.DrawTexture(new Rect(rect.x - 2, rect.y - 2, rect.width + 4, rect.height + 4),
                        Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }

                if (GUI.Button(rect, _blockBarLabels[i], selected ? _btnStyleBold : _btnStyle))
                    _localController.SelectedBlockType = (BlockType)i;
            }
        }

        bool _showHelp = true;

        void DrawHelpPanel()
        {
            float panelW = 260f;
            float panelH = _showHelp ? 280f : 30f;
            float panelX = Screen.width - panelW - 10f;
            float panelY = 10f;

            GUI.color = new Color(0, 0, 0, 0.6f);
            GUI.DrawTexture(new Rect(panelX, panelY, panelW, panelH), Texture2D.whiteTexture);
            GUI.color = Color.white;

            if (GUI.Button(new Rect(panelX, panelY, panelW, 26f),
                _showHelp ? "[ Hide Help / \u9690\u85cf\u5e2e\u52a9 ]" : "[ ? Help / \u5e2e\u52a9 ]", _helpHeaderStyle))
            {
                _showHelp = !_showHelp;
            }

            if (!_showHelp) return;
            var area = new Rect(panelX + 10f, panelY + 30f, panelW - 20f, panelH - 40f);

            GUILayout.BeginArea(area);

            GUILayout.Label("<b>Controls / 操作说明</b>", _helpStyle);
            GUILayout.Space(4);
            GUILayout.Label("WASD        Move / 移动", _helpStyle);
            GUILayout.Label("Space         Jump / 跳跃", _helpStyle);
            GUILayout.Label("Mouse       Look / 视角", _helpStyle);
            GUILayout.Label("Left Click    Break block / 破坏方块", _helpStyle);
            GUILayout.Label("Right Click  Place block / 放置方块", _helpStyle);
            GUILayout.Label("1-7             Select block / 选择方块", _helpStyle);
            GUILayout.Label("Scroll          Switch block / 切换方块", _helpStyle);
            GUILayout.Label("Esc             Release mouse / 释放鼠标", _helpStyle);

            GUILayout.Space(8);
            GUILayout.Label("<b>Multiplayer / 多人联机</b>", _helpStyle);
            GUILayout.Space(4);
            GUILayout.Label("All players share the same world.", _helpStyle);
            GUILayout.Label("所有玩家共享同一个方块世界。", _helpStyle);
            GUILayout.Label("Block changes sync in real-time.", _helpStyle);
            GUILayout.Label("方块操作实时同步给其他人。", _helpStyle);

            GUILayout.EndArea();
        }
    }
}
