using System;
using System.Collections.Generic;
using UnityEngine;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Unity;

/// <summary>
/// Level 2 — 断线重连
///
/// 教会：快照序列化、模拟断线、自动重连恢复。
/// 基于 HelloWorld 的方块游戏，新增断线测试按钮和重连状态 UI。
///
/// 新概念：
///   - OnTakeSnapshot / OnLoadSnapshot — 快照回调
///   - SimulateNetworkDrop() — 测试断线
///   - OnReconnected / OnDisconnected — 重连事件
///   - RttMs — 网络延迟
/// </summary>
[RequireComponent(typeof(BoomNetworkManager))]
public class Reconnect : MonoBehaviour
{
    [Header("Game")]
    [SerializeField] private float moveSpeed = 1f;

    private BoomNetworkManager _network;
    private readonly Dictionary<int, Transform> _players = new();
    private readonly byte[] _inputBuf = new byte[8];
    private uint _lastFrame;
    private float _sendTimer;
    private float _lastH, _lastV;

    // 重连状态
    private bool _isReconnecting;
    private int _reconnectCount;
    private string _lastEvent = "";

    private static readonly Color[] Colors = { Color.green, new(0.3f, 0.5f, 1f), Color.red, Color.yellow };

    void Start()
    {
        _network = GetComponent<BoomNetworkManager>();

        var c = _network.Client;
        c.OnFrame += OnFrame;
        c.OnTakeSnapshot = TakeSnapshot;
        c.OnLoadSnapshot = LoadSnapshot;

        c.OnDisconnected += () =>
        {
            _isReconnecting = true;
            _lastEvent = $"[{DateTime.Now:HH:mm:ss}] Disconnected — reconnecting...";
        };
        c.OnReconnected += () =>
        {
            _isReconnecting = false;
            _reconnectCount++;
            _lastEvent = $"[{DateTime.Now:HH:mm:ss}] Reconnected! (total: {_reconnectCount})";
        };

        _network.QuickStart();
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

    // --- Game Logic ---

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
        _lastEvent = $"[{DateTime.Now:HH:mm:ss}] Snapshot loaded ({count} players)";
    }

    // --- UI ---

    void OnGUI()
    {
        var title = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
        var label = new GUIStyle(GUI.skin.label) { fontSize = 13 };
        var btn = new GUIStyle(GUI.skin.button) { fontSize = 14, fixedHeight = 35 };

        // 左上角 — 状态信息
        GUILayout.BeginArea(new Rect(10, 10, 300, 200));
        GUILayout.Label("Reconnect Demo", title);
        GUILayout.Label($"State: {_network.Client.CurrentState}", label);
        GUILayout.Label($"Player: {_network.PlayerId}  Frame: {_lastFrame}", label);
        GUILayout.Label($"RTT: {_network.Client.RttMs:F0}ms  Reconnects: {_reconnectCount}", label);
        if (!string.IsNullOrEmpty(_lastEvent))
            GUILayout.Label(_lastEvent, label);
        GUILayout.EndArea();

        // 右上角 — 断线测试按钮
        GUILayout.BeginArea(new Rect(Screen.width - 180, 10, 170, 120));
        GUILayout.Label("Test Disconnect", title);
        if (GUILayout.Button("Drop Connection", btn))
            _network.Client.SimulateNetworkDrop();
        GUILayout.EndArea();

        // 重连遮罩
        if (_isReconnecting)
        {
            var overlay = new GUIStyle(GUI.skin.box);
            overlay.fontSize = 24;
            overlay.alignment = TextAnchor.MiddleCenter;
            overlay.normal.textColor = Color.white;
            GUI.Box(new Rect(0, 0, Screen.width, Screen.height), "");
            GUI.Label(new Rect(0, Screen.height / 2 - 30, Screen.width, 60),
                "Reconnecting...", overlay);
        }
    }
}
