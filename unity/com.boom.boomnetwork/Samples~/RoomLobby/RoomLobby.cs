using System;
using System.Collections.Generic;
using UnityEngine;
using BoomNetwork.Client.FrameSync;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Unity;

/// <summary>
/// Level 1 — 房间大厅
///
/// 教会：手动连接、房间列表、创建/加入/离开房间、开始游戏。
/// 与 HelloWorld 的区别：不用 QuickStart，手动控制每一步。
///
/// 使用方法：挂上 BoomNetworkManager + 此脚本，Play 后点 Connect。
/// </summary>
[RequireComponent(typeof(BoomNetworkManager))]
public class RoomLobby : MonoBehaviour
{
    [Header("Game")]
    [SerializeField] private float moveSpeed = 1f;
    [SerializeField] private int maxPlayers = 4;

    private BoomNetworkManager _network;
    private readonly Dictionary<int, Transform> _players = new();
    private readonly byte[] _inputBuf = new byte[8];
    private readonly List<int> _roomPlayers = new();
    private RoomInfo[] _allRooms = Array.Empty<RoomInfo>();
    private RoomInfo[] _rooms = Array.Empty<RoomInfo>();
    private uint _lastFrame;
    private float _sendTimer;
    private float _lastH, _lastV;
    private string _status = "";
    private Vector2 _roomScroll;

    private static readonly Color[] Colors = { Color.green, new(0.3f, 0.5f, 1f), Color.red, Color.yellow };

    void Start()
    {
        _network = GetComponent<BoomNetworkManager>();

        var c = _network.Client;
        c.OnConnected += () => { _status = "Connected"; RefreshRooms(); };
        c.OnJoinedRoom += (roomId, existing) =>
        {
            _status = $"In Room {roomId}";
            _roomPlayers.Clear();
            _roomPlayers.AddRange(existing);
            _roomPlayers.Add(_network.PlayerId);
        };
        c.OnPlayerJoined += pid => { if (!_roomPlayers.Contains(pid)) _roomPlayers.Add(pid); };
        c.OnPlayerLeft += pid => _roomPlayers.Remove(pid);
        c.OnFrameSyncStart += _ => _status = "Syncing";
        c.OnFrameSyncStop += () => { _status = "In Room (stopped)"; ClearPlayers(); _lastFrame = 0; };
        c.OnFrame += OnFrame;
        c.OnLeftRoom += _ =>
        {
            _status = "Connected";
            _roomPlayers.Clear();
            ClearPlayers();
            RefreshRooms();
        };
        c.OnDisconnected += () => _status = "Disconnected";
        c.OnTakeSnapshot = TakeSnapshot;
        c.OnLoadSnapshot = LoadSnapshot;
    }

    void RefreshRooms()
    {
        var key = _network.MatchKey;
        _network.Client.GetRooms(rooms =>
        {
            _allRooms = rooms ?? Array.Empty<RoomInfo>();
            // 按 matchKey 过滤：只显示相同 key 的房间
            if (string.IsNullOrEmpty(key))
                _rooms = _allRooms;
            else
                _rooms = Array.FindAll(_allRooms, r => r.MatchKey == key);
        });
    }

    void Update()
    {
        if (!_network.IsSyncing) return;

        _sendTimer += Time.deltaTime * 1000f;
        if (_sendTimer < 50f) return;
        _sendTimer -= 50f;

        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");

        if (Mathf.Abs(h - _lastH) > 0.01f || Mathf.Abs(v - _lastV) > 0.01f)
        {
            _lastH = h; _lastV = v;
            BitConverter.TryWriteBytes(_inputBuf.AsSpan(0, 4), h);
            BitConverter.TryWriteBytes(_inputBuf.AsSpan(4, 4), v);
            _network.SendInput(_inputBuf);
        }
    }

    // --- Game Logic (same as HelloWorld) ---

    void OnFrame(FrameData frame)
    {
        _lastFrame = frame.FrameNumber;
        if (frame.Inputs == null) return;
        for (int i = 0; i < frame.Inputs.Length; i++)
        {
            ref var input = ref frame.Inputs[i];
            if (input.DataLength < 8) continue;
            float h = BitConverter.ToSingle(input.Data, 0);
            float v = BitConverter.ToSingle(input.Data, 4);
            int pid = input.PlayerId;
            if (!_players.TryGetValue(pid, out var t)) t = SpawnPlayer(pid);
            var pos = t.position;
            pos.x += h * moveSpeed; pos.y += v * moveSpeed;
            if (pos.x > 8f) pos.x -= 16f; if (pos.x < -8f) pos.x += 16f;
            if (pos.y > 5f) pos.y -= 10f; if (pos.y < -5f) pos.y += 10f;
            t.position = pos;
        }
    }

    Transform SpawnPlayer(int pid)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = $"Player_{pid}";
        go.transform.localScale = Vector3.one * 0.8f;
        go.GetComponent<Renderer>().material.color = Colors[(pid - 1) % Colors.Length];
        _players[pid] = go.transform;
        return go.transform;
    }

    void ClearPlayers()
    {
        foreach (var kv in _players) if (kv.Value) Destroy(kv.Value.gameObject);
        _players.Clear();
    }

    // --- Snapshot ---

    byte[] TakeSnapshot()
    {
        var buf = new byte[2 + _players.Count * 12];
        buf[0] = (byte)(_players.Count & 0xFF); buf[1] = (byte)((_players.Count >> 8) & 0xFF);
        int off = 2;
        foreach (var kv in _players)
        {
            BitConverter.TryWriteBytes(buf.AsSpan(off, 4), kv.Key); off += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(off, 4), kv.Value.position.x); off += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(off, 4), kv.Value.position.y); off += 4;
        }
        return buf;
    }

    void LoadSnapshot(byte[] data)
    {
        if (data == null || data.Length < 2) return;
        int count = data[0] | (data[1] << 8); int off = 2;
        for (int i = 0; i < count && off + 12 <= data.Length; i++)
        {
            int pid = BitConverter.ToInt32(data, off); off += 4;
            float x = BitConverter.ToSingle(data, off); off += 4;
            float y = BitConverter.ToSingle(data, off); off += 4;
            if (!_players.TryGetValue(pid, out var t)) t = SpawnPlayer(pid);
            t.position = new Vector3(x, y, 0);
        }
    }

    // --- UI ---

    void OnGUI()
    {
        var state = _network.Client.CurrentState;
        var title = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
        var label = new GUIStyle(GUI.skin.label) { fontSize = 13 };
        var btn = new GUIStyle(GUI.skin.button) { fontSize = 14 };

        GUILayout.BeginArea(new Rect(10, 10, 280, 500));
        GUILayout.Label("Room Lobby", title);
        GUILayout.Label($"State: {state}  Player: {_network.PlayerId}  Frame: {_lastFrame}", label);
        GUILayout.Space(5);

        if (state == FrameSyncClient.State.Disconnected)
        {
            if (GUILayout.Button("Connect", btn)) _network.Connect();
        }
        else if (state == FrameSyncClient.State.Connected)
        {
            // --- 房间列表（滚动）---
            GUILayout.Label($"Rooms ({_rooms.Length}):", label);
            _roomScroll = GUILayout.BeginScrollView(_roomScroll, GUILayout.Height(200));
            if (_rooms.Length == 0)
                GUILayout.Label("  (none)", label);
            foreach (var r in _rooms)
            {
                GUILayout.BeginHorizontal();
                var keyTag = string.IsNullOrEmpty(r.MatchKey) ? "" : $" [{r.MatchKey}]";
                GUILayout.Label($"  #{r.RoomId} ({r.PlayerCount}/{r.MaxPlayers}) {(r.Running ? "Playing" : "Waiting")}{keyTag}", label);
                if (GUILayout.Button("Join", GUILayout.Width(50)))
                    _network.Client.JoinRoom(r.RoomId);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh", btn)) RefreshRooms();
            if (GUILayout.Button("Create Room", btn)) _network.Client.CreateRoom(maxPlayers, _ => RefreshRooms());
            if (GUILayout.Button("Quick Match", btn))
            {
                var key = string.IsNullOrEmpty(_network.MatchKey) ? null : _network.MatchKey;
                _network.Client.MatchRoom(maxPlayers, key);
            }
            GUILayout.EndHorizontal();
        }
        else if (state == FrameSyncClient.State.InRoom)
        {
            GUILayout.Label($"Room {_network.Client.RoomId} — Players:", label);
            foreach (var pid in _roomPlayers)
                GUILayout.Label($"  Player {pid}{(pid == _network.PlayerId ? " (you)" : "")}", label);
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Start Game", btn)) _network.Client.RequestStart();
            if (GUILayout.Button("Leave Room", btn)) _network.Client.LeaveRoom();
            GUILayout.EndHorizontal();
        }
        else if (state == FrameSyncClient.State.Syncing)
        {
            GUILayout.Label($"Game running! Frame: {_lastFrame}", label);
            GUILayout.Label("WASD to move.", label);
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Stop Game", btn)) _network.Client.RequestStop();
            if (GUILayout.Button("Leave Room", btn)) _network.Client.LeaveRoom();
            GUILayout.EndHorizontal();
        }

        GUILayout.Label($"\n{_status}", label);
        GUILayout.EndArea();
    }
}
