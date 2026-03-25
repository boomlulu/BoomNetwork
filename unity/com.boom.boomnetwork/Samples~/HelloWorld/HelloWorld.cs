using System;
using System.Collections.Generic;
using UnityEngine;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Unity;

/// <summary>
/// BoomNetwork Hello World — 最小接入示例
///
/// 使用方法:
///   1. 空场景，创建 GameObject
///   2. 挂上 BoomNetworkManager（Inspector 填服务器地址）+ 此脚本
///   3. Play → 自动连接、建房、开始帧同步
///   4. WASD 移动方块，开第二个客户端看到对方方块
/// </summary>
[RequireComponent(typeof(BoomNetworkManager))]
public class HelloWorld : MonoBehaviour
{
    [Header("Game")]
    [SerializeField] private float moveSpeed = 5f;

    private BoomNetworkManager _network;
    private readonly Dictionary<int, Transform> _players = new();
    private readonly byte[] _inputBuf = new byte[8];
    private uint _lastFrame;
    private float _sendTimer;

    private static readonly Color[] Colors = { Color.green, new(0.3f, 0.5f, 1f), Color.red, Color.yellow };

    void Start()
    {
        _network = GetComponent<BoomNetworkManager>();
        _network.Client.OnFrame += OnFrame;
        _network.Client.OnTakeSnapshot = TakeSnapshot;
        _network.Client.OnLoadSnapshot = LoadSnapshot;
        _network.QuickStart();
    }

    void Update()
    {
        if (!_network.IsSyncing) return;

        // 按服务器帧率节流（20fps = 50ms），不要每帧都发
        _sendTimer += Time.deltaTime * 1000f;
        if (_sendTimer < 50f) return;
        _sendTimer -= 50f;

        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");

        if (Mathf.Abs(h) > 0.01f || Mathf.Abs(v) > 0.01f)
        {
            BitConverter.TryWriteBytes(_inputBuf.AsSpan(0, 4), h);
            BitConverter.TryWriteBytes(_inputBuf.AsSpan(4, 4), v);
            _network.SendInput(_inputBuf);
        }
    }

    void OnFrame(FrameData frame)
    {
        _lastFrame = frame.FrameNumber;
        if (frame.Inputs == null) return;

        float delta = moveSpeed * (1f / 20f); // 20fps frame interval

        for (int i = 0; i < frame.Inputs.Length; i++)
        {
            ref var input = ref frame.Inputs[i];
            if (input.DataLength < 8) continue;

            float h = BitConverter.ToSingle(input.Data, 0);
            float v = BitConverter.ToSingle(input.Data, 4);
            int pid = input.PlayerId;

            if (!_players.TryGetValue(pid, out var t))
                t = SpawnPlayer(pid);

            var pos = t.position;
            pos.x += h * delta;
            pos.y += v * delta;

            // 屏幕环绕
            if (pos.x > 8f) pos.x -= 16f;
            if (pos.x < -8f) pos.x += 16f;
            if (pos.y > 5f) pos.y -= 10f;
            if (pos.y < -5f) pos.y += 10f;

            t.position = pos;
        }
    }

    Transform SpawnPlayer(int playerId)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = $"Player_{playerId}";
        go.transform.localScale = Vector3.one * 0.8f;

        var color = Colors[(playerId - 1) % Colors.Length];
        go.GetComponent<Renderer>().material.color = color;

        _players[playerId] = go.transform;
        return go.transform;
    }

    // --- 快照：服务器要求定期上传，用于重连恢复 ---

    byte[] TakeSnapshot()
    {
        var buf = new byte[2 + _players.Count * 12]; // count + N × (pid + x + y)
        buf[0] = (byte)(_players.Count & 0xFF);
        buf[1] = (byte)((_players.Count >> 8) & 0xFF);
        int offset = 2;
        foreach (var kv in _players)
        {
            BitConverter.TryWriteBytes(buf.AsSpan(offset, 4), kv.Key); offset += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(offset, 4), kv.Value.position.x); offset += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(offset, 4), kv.Value.position.y); offset += 4;
        }
        return buf;
    }

    void LoadSnapshot(byte[] data)
    {
        if (data == null || data.Length < 2) return;
        int count = data[0] | (data[1] << 8);
        int offset = 2;
        for (int i = 0; i < count && offset + 12 <= data.Length; i++)
        {
            int pid = BitConverter.ToInt32(data, offset); offset += 4;
            float x = BitConverter.ToSingle(data, offset); offset += 4;
            float y = BitConverter.ToSingle(data, offset); offset += 4;
            if (!_players.TryGetValue(pid, out var t))
                t = SpawnPlayer(pid);
            t.position = new Vector3(x, y, 0);
        }
    }

    void OnGUI()
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = 14 };
        GUILayout.Label($"State: {_network.Client.CurrentState}", style);
        GUILayout.Label($"Player: {_network.PlayerId}", style);
        GUILayout.Label($"Frame: {_lastFrame}", style);
        GUILayout.Label($"Players: {_players.Count}", style);
    }
}
