using System;
using System.Collections.Generic;
using UnityEngine;
using BoomNetwork.Client.FrameSync;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Unity;

/// <summary>
/// State Sync Move Demo — 状态同步移动（不启动帧同步）
///
/// 演示纯状态同步模式：用 SendStateMessage 广播位置，无需帧同步。
/// 适用于休闲社交、展厅漫游等不需要严格确定性的场景。
///
/// 核心 API：
///   Client.SendStateMessage(byte[]) — 广播自己的位置
///   Client.OnStateMessage(pid, data) — 收到他人位置
///   Client.SetData(key, value) — 存储玩家名字等持久数据
///   Client.OnDataSynced / OnDataChanged — 收到 KV 变更
///
/// 与帧同步（HelloWorld）的区别：
///   - 不调用 RequestStart / SendInput / OnFrame
///   - 位置由各客户端自己算，直接广播结果
///   - 更简单，但不保证确定性
/// </summary>
[RequireComponent(typeof(BoomNetworkManager))]
public class StateSyncMove : MonoBehaviour
{
    [Header("Game")]
    [SerializeField] float moveSpeed = 3f;
    [SerializeField] float smoothSpeed = 8f;
    [SerializeField] float broadcastRate = 15f;

    BoomNetworkManager _network;
    readonly Dictionary<int, RemotePlayer> _remotePlayers = new();
    Transform _localQuad;
    Vector2 _localPos;
    string _status = "Disconnected";
    readonly List<int> _roomPlayers = new();
    float _broadcastTimer;
    Vector2 _lastSentPos;

    static readonly Color[] Colors = { Color.green, new(0.3f, 0.5f, 1f), Color.red, Color.yellow, Color.cyan, Color.magenta };

    void Start()
    {
        _network = GetComponent<BoomNetworkManager>();

        var c = _network.Client;
        c.OnConnected += () =>
        {
            _status = "Connected, matching...";
            c.MatchRoom(8, "statesyncmove");
        };
        c.OnJoinedRoom += (roomId, existing) =>
        {
            _status = $"Room {roomId}";
            _roomPlayers.Clear();
            _roomPlayers.AddRange(existing);
            _roomPlayers.Add(_network.PlayerId);

            // 创建本地玩家方块
            if (_localQuad == null)
                _localQuad = CreateQuad(_network.PlayerId, true);
        };
        c.OnPlayerJoinedMsg += pid =>
        {
            if (!_roomPlayers.Contains(pid)) _roomPlayers.Add(pid);
        };
        c.OnPlayerLeftMsg += pid =>
        {
            _roomPlayers.Remove(pid);
            if (_remotePlayers.TryGetValue(pid, out var rp))
            {
                if (rp.Quad) Destroy(rp.Quad.gameObject);
                _remotePlayers.Remove(pid);
            }
        };
        c.OnStateMessage += OnStateMessage;
        c.OnDisconnected += () => { _status = "Disconnected"; Cleanup(); };
        c.OnLeftRoom += _ => { _status = "Connected"; Cleanup(); };
    }

    void Update()
    {
        if (_network.Client.CurrentState < FrameSyncClient.State.InRoom) return;
        if (_localQuad == null) return;

        // WASD 移动
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");

        if (Mathf.Abs(h) > 0.01f || Mathf.Abs(v) > 0.01f)
        {
            _localPos.x += h * moveSpeed * Time.deltaTime;
            _localPos.y += v * moveSpeed * Time.deltaTime;
            // 边界环绕
            if (_localPos.x > 8f) _localPos.x -= 16f;
            if (_localPos.x < -8f) _localPos.x += 16f;
            if (_localPos.y > 5f) _localPos.y -= 10f;
            if (_localPos.y < -5f) _localPos.y += 10f;
        }

        _localQuad.position = new Vector3(_localPos.x, _localPos.y, 0);

        // 定时广播位置
        _broadcastTimer += Time.deltaTime;
        float interval = 1f / broadcastRate;
        if (_broadcastTimer >= interval)
        {
            _broadcastTimer -= interval;
            if (Vector2.Distance(_localPos, _lastSentPos) > 0.01f)
            {
                BroadcastPosition();
                _lastSentPos = _localPos;
            }
        }

        // 平滑远端玩家
        foreach (var kv in _remotePlayers)
        {
            var rp = kv.Value;
            if (!rp.Quad) continue;
            var current = (Vector2)rp.Quad.position;
            if (Vector2.Distance(current, rp.TargetPos) > 6f)
                current = rp.TargetPos;
            else
                current = Vector2.Lerp(current, rp.TargetPos, smoothSpeed * Time.deltaTime);
            rp.Quad.position = new Vector3(current.x, current.y, 0);
        }
    }

    void BroadcastPosition()
    {
        var buf = new byte[8];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), _localPos.x);
        BitConverter.TryWriteBytes(buf.AsSpan(4, 4), _localPos.y);
        _network.Client.SendStateMessage(buf);
    }

    void OnStateMessage(int playerId, byte[] data)
    {
        if (playerId == _network.PlayerId) return;
        if (data == null || data.Length < 8) return;

        float x = BitConverter.ToSingle(data, 0);
        float y = BitConverter.ToSingle(data, 4);

        if (!_remotePlayers.TryGetValue(playerId, out var rp))
        {
            rp = new RemotePlayer { Quad = CreateQuad(playerId, false), TargetPos = new Vector2(x, y) };
            rp.Quad.position = new Vector3(x, y, 0);
            _remotePlayers[playerId] = rp;
        }
        rp.TargetPos = new Vector2(x, y);
    }

    Transform CreateQuad(int playerId, bool isLocal)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = isLocal ? $"Local_P{playerId}" : $"Remote_P{playerId}";
        go.transform.localScale = Vector3.one * 0.8f;
        var color = Colors[(playerId - 1) % Colors.Length];
        if (!isLocal) color = new Color(color.r, color.g, color.b, 0.8f);
        go.GetComponent<Renderer>().material.color = color;

        // 标签
        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform);
        labelGo.transform.localPosition = new Vector3(0, 0.7f, 0);
        var tm = labelGo.AddComponent<TextMesh>();
        tm.text = isLocal ? $"You (P{playerId})" : $"P{playerId}";
        tm.characterSize = 0.12f;
        tm.fontSize = 48;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = color;

        return go.transform;
    }

    void Cleanup()
    {
        foreach (var kv in _remotePlayers)
            if (kv.Value.Quad) Destroy(kv.Value.Quad.gameObject);
        _remotePlayers.Clear();
        _roomPlayers.Clear();
        if (_localQuad) { Destroy(_localQuad.gameObject); _localQuad = null; }
    }

    void OnGUI()
    {
        var title = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
        var label = new GUIStyle(GUI.skin.label) { fontSize = 13 };
        var btn = new GUIStyle(GUI.skin.button) { fontSize = 14 };

        GUILayout.BeginArea(new Rect(10, 10, 320, 200));
        GUILayout.Label("State Sync Move", title);
        GUILayout.Label($"Status: {_status}  |  Player: {_network.PlayerId}", label);
        GUILayout.Label($"Online: {_roomPlayers.Count}  |  RTT: {_network.Client.RttMs:F0}ms", label);
        GUILayout.Label("WASD to move. No frame sync needed!", label);
        GUILayout.Space(5);

        var state = _network.Client.CurrentState;
        if (state == FrameSyncClient.State.Disconnected)
        {
            if (GUILayout.Button("Connect", btn, GUILayout.Height(30)))
                _network.Connect();
        }
        else if (state >= FrameSyncClient.State.InRoom)
        {
            if (GUILayout.Button("Leave Room", btn, GUILayout.Height(25)))
                _network.Client.LeaveRoom();
        }

        GUILayout.EndArea();
    }

    class RemotePlayer
    {
        public Transform Quad;
        public Vector2 TargetPos;
    }
}
