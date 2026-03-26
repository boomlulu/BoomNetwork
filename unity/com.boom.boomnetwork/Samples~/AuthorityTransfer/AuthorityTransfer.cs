using System;
using System.Collections.Generic;
using UnityEngine;
using BoomNetwork.Client.FrameSync;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Unity;

/// <summary>
/// Level 6 -- Authority Transfer Demo
///
/// N 个玩家 + 1 个球。靠近按 Space 抢球，持球者是球的权威（零延迟操控），
/// 松手后球变为 unclaimed。体现公理 3："冲突由仲裁者一锤定音"。
///
/// 核心 API：
///   Client.RequestAuthorityTransfer(entityId) -- 请求获取权威
///   Client.ReleaseAuthority(entityId) -- 释放权威
///   Client.OnAuthorityChanged -- 服务器仲裁结果回调
/// </summary>
[RequireComponent(typeof(BoomNetworkManager))]
public class AuthorityTransfer : MonoBehaviour
{
    const int BallEntityId = 9999;
    const float GrabRadius = 1.5f;

    [Header("Game")]
    [SerializeField] float moveSpeed = 1f;
    [SerializeField] float smoothSpeed = 10f;

    BoomNetworkManager _network;
    readonly Dictionary<int, PlayerEntity> _players = new();
    BallEntity _ball;
    int _ballOwner;   // 本地镜像，由 OnAuthorityChanged 更新
    uint _lastFrame;
    float _lastH, _lastV;
    bool _authorityRegistered;
    readonly byte[] _inputBuf = new byte[8];

    static readonly Color[] Colors = { Color.green, new(0.3f, 0.5f, 1f), Color.red, Color.yellow };

    void Start()
    {
        _network = GetComponent<BoomNetworkManager>();
        var c = _network.Client;

        c.OnFrame += OnFrame;
        c.OnEntityState += OnEntityState;
        c.OnAuthorityChanged += OnAuthorityChanged;

        c.OnJoinedRoom += (_, existing) =>
        {
            foreach (var pid in existing)
                GetOrCreatePlayer(pid);
        };
        c.OnPlayerJoined += pid => GetOrCreatePlayer(pid);
        c.OnPlayerLeft += pid => DestroyPlayer(pid);
        c.OnTakeSnapshot = TakeSnapshot;
        c.OnLoadSnapshot = LoadSnapshot;

        // 创建球
        _ball = CreateBall();

        _network.QuickStart();
    }

    void Update()
    {
        if (!_network.IsSyncing) return;

        // 注册权威实体（玩家自己）
        if (!_authorityRegistered && _network.PlayerId > 0)
        {
            var me = GetOrCreatePlayer(_network.PlayerId);
            me.IsAuthority = true;
            _network.Client.RegisterAuthorityEntity(me);
            _authorityRegistered = true;
        }

        // 读输入
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");

        // 输入变化时发送
        if (Mathf.Abs(h - _lastH) > 0.01f || Mathf.Abs(v - _lastV) > 0.01f)
        {
            _lastH = h; _lastV = v;
            BitConverter.TryWriteBytes(_inputBuf.AsSpan(0, 4), h);
            BitConverter.TryWriteBytes(_inputBuf.AsSpan(4, 4), v);
            _network.SendInput(_inputBuf);
        }

        // Space: 抢球 / 放球
        if (Input.GetKeyDown(KeyCode.Space))
            TryGrabOrRelease();
    }

    void OnFrame(FrameData frame)
    {
        _lastFrame = frame.FrameNumber;

        // 帧驱动：更新权威玩家的逻辑位置
        if (_players.TryGetValue(_network.PlayerId, out var me) && me.IsAuthority)
        {
            var pos = me.LogicalPosition;
            pos.x += _lastH * moveSpeed;
            pos.y += _lastV * moveSpeed;
            if (pos.x > 8f) pos.x -= 16f; if (pos.x < -8f) pos.x += 16f;
            if (pos.y > 5f) pos.y -= 10f; if (pos.y < -5f) pos.y += 10f;
            me.LogicalPosition = pos;

            // 持球时：球跟随玩家（零延迟）
            if (_ballOwner == _network.PlayerId)
                _ball.LogicalPosition = pos;
        }
    }

    void LateUpdate()
    {
        // 所有实体：visual Lerp → logical
        foreach (var kv in _players)
            SmoothEntity(kv.Value.transform, kv.Value.LogicalPosition);
        SmoothEntity(_ball.transform, _ball.LogicalPosition);
    }

    void SmoothEntity(Transform t, Vector2 logicalPos)
    {
        var current = (Vector2)t.position;
        if (Vector2.Distance(current, logicalPos) > 6f)
            current = logicalPos;
        else
            current = Vector2.Lerp(current, logicalPos, smoothSpeed * Time.deltaTime);
        t.position = new Vector3(current.x, current.y, 0);
    }

    // --- 权威变更回调（服务器仲裁结果）---

    void OnAuthorityChanged(int entityId, int newOwner)
    {
        if (entityId != BallEntityId) return;
        _ballOwner = newOwner;

        if (newOwner == _network.PlayerId)
        {
            // 我获得了球的权威
            _network.Client.RegisterAuthorityEntity(_ball);
            SetBallColor(Colors[(_network.PlayerId - 1) % Colors.Length]);
        }
        else
        {
            // 别人获得 或 unclaimed
            _network.Client.UnregisterAuthorityEntity(BallEntityId);
            SetBallColor(newOwner == 0
                ? Color.white
                : Colors[(newOwner - 1) % Colors.Length]);
        }
    }

    void TryGrabOrRelease()
    {
        if (_ballOwner == _network.PlayerId)
        {
            // 我持球 → 释放
            _network.Client.ReleaseAuthority(BallEntityId);
        }
        else
        {
            // 球 unclaimed 或别人持有 → 检查距离后请求（支持抢夺）
            if (_players.TryGetValue(_network.PlayerId, out var me))
            {
                if (Vector2.Distance(me.LogicalPosition, _ball.LogicalPosition) < GrabRadius)
                    _network.Client.RequestAuthorityTransfer(BallEntityId);
            }
        }
    }

    // --- 实体状态路由 ---

    void OnEntityState(int senderPid, int entityId, byte[] data, int offset, int length)
    {
        if (entityId == BallEntityId)
        {
            _ball.OnRemoteState(data, offset, length, senderPid);
            return;
        }
        if (_players.TryGetValue(entityId, out var p) && !p.IsAuthority)
            p.OnRemoteState(data, offset, length, senderPid);
    }

    // --- Entity 管理 ---

    PlayerEntity GetOrCreatePlayer(int playerId)
    {
        if (_players.TryGetValue(playerId, out var existing))
            return existing;

        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = $"Player_{playerId}";
        go.transform.localScale = Vector3.one * 0.8f;
        go.GetComponent<Renderer>().material.color = Colors[(playerId - 1) % Colors.Length];

        var entity = go.AddComponent<PlayerEntity>();
        entity.EntityId = playerId;
        _players[playerId] = entity;
        return entity;
    }

    void DestroyPlayer(int playerId)
    {
        if (_players.TryGetValue(playerId, out var p))
        {
            if (p) Destroy(p.gameObject);
            _players.Remove(playerId);
        }
    }

    BallEntity CreateBall()
    {
        // 用 Quad + 圆形纹理模拟 2D 圆球
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "Ball";
        go.transform.localScale = Vector3.one * 1.0f;
        go.transform.position = Vector3.zero;

        // 生成圆形纹理
        var renderer = go.GetComponent<Renderer>();
        renderer.material = new Material(Shader.Find("Sprites/Default"));
        renderer.material.mainTexture = CreateCircleTexture(64, Color.white);
        renderer.material.color = Color.white;

        var ball = go.AddComponent<BallEntity>();
        ball.EntityId = BallEntityId;
        return ball;
    }

    static Texture2D CreateCircleTexture(int size, Color color)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float radius = size * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), new Vector2(radius, radius));
                tex.SetPixel(x, y, dist < radius - 1f ? color : Color.clear);
            }
        tex.Apply();
        return tex;
    }

    void SetBallColor(Color c)
    {
        if (!_ball) return;
        var mat = _ball.GetComponent<Renderer>().material;
        mat.color = c;
        mat.mainTexture = CreateCircleTexture(64, c);
    }

    // --- Snapshot ---

    byte[] TakeSnapshot()
    {
        // [playerCount:2] + N*[pid:4][x:4][y:4] + [ballOwner:4][ballX:4][ballY:4]
        var buf = new byte[2 + _players.Count * 12 + 12];
        buf[0] = (byte)(_players.Count & 0xFF);
        buf[1] = (byte)((_players.Count >> 8) & 0xFF);
        int off = 2;
        foreach (var kv in _players)
        {
            BitConverter.TryWriteBytes(buf.AsSpan(off, 4), kv.Key); off += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(off, 4), kv.Value.LogicalPosition.x); off += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(off, 4), kv.Value.LogicalPosition.y); off += 4;
        }
        BitConverter.TryWriteBytes(buf.AsSpan(off, 4), _ballOwner); off += 4;
        BitConverter.TryWriteBytes(buf.AsSpan(off, 4), _ball.LogicalPosition.x); off += 4;
        BitConverter.TryWriteBytes(buf.AsSpan(off, 4), _ball.LogicalPosition.y);
        return buf;
    }

    void LoadSnapshot(byte[] data)
    {
        if (data == null || data.Length < 2) return;
        int count = data[0] | (data[1] << 8);
        int off = 2;
        for (int i = 0; i < count && off + 12 <= data.Length; i++)
        {
            int pid = BitConverter.ToInt32(data, off); off += 4;
            float x = BitConverter.ToSingle(data, off); off += 4;
            float y = BitConverter.ToSingle(data, off); off += 4;
            var p = GetOrCreatePlayer(pid);
            p.LogicalPosition = new Vector2(x, y);
            p.transform.position = new Vector3(x, y, 0);
        }
        if (off + 12 <= data.Length)
        {
            _ballOwner = BitConverter.ToInt32(data, off); off += 4;
            float bx = BitConverter.ToSingle(data, off); off += 4;
            float by = BitConverter.ToSingle(data, off);
            _ball.LogicalPosition = new Vector2(bx, by);
            _ball.transform.position = new Vector3(bx, by, 0);
        }
    }

    // --- UI ---

    void OnGUI()
    {
        var title = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
        var label = new GUIStyle(GUI.skin.label) { fontSize = 13 };

        GUILayout.BeginArea(new Rect(10, 10, 350, 200));
        GUILayout.Label("Authority Transfer Demo", title);
        GUILayout.Label($"State: {_network.Client.CurrentState}", label);
        GUILayout.Label($"Player: {_network.PlayerId}  Frame: {_lastFrame}", label);
        GUILayout.Label($"RTT: {_network.Client.RttMs:F0}ms  Players: {_players.Count}", label);

        string ownerText = _ballOwner == 0 ? "Unclaimed (white)"
            : _ballOwner == _network.PlayerId ? $"You (Player {_ballOwner})"
            : $"Player {_ballOwner}";
        GUILayout.Label($"Ball Owner: {ownerText}", label);

        // 靠近提示
        if (_ballOwner == 0 && _players.TryGetValue(_network.PlayerId, out var me))
        {
            if (Vector2.Distance(me.LogicalPosition, _ball.LogicalPosition) < GrabRadius)
                GUILayout.Label(">> Press Space to grab! <<", label);
        }
        else if (_ballOwner == _network.PlayerId)
        {
            GUILayout.Label(">> Press Space to release <<", label);
        }

        GUILayout.EndArea();
    }
}

// --- PlayerEntity: IEntitySync for players ---
// State: [posX:4][posY:4] = 8 bytes

public class PlayerEntity : MonoBehaviour, IEntitySync
{
    public int EntityId { get; set; }
    public int StateSize => 8;
    public bool IsAuthority;
    public Vector2 LogicalPosition;

    public int WriteState(byte[] buf, int offset)
    {
        BitConverter.TryWriteBytes(buf.AsSpan(offset, 4), LogicalPosition.x); offset += 4;
        BitConverter.TryWriteBytes(buf.AsSpan(offset, 4), LogicalPosition.y);
        return 8;
    }

    public void OnRemoteState(byte[] data, int offset, int length, int senderPlayerId)
    {
        if (length < 8 || IsAuthority) return;
        LogicalPosition = new Vector2(
            BitConverter.ToSingle(data, offset),
            BitConverter.ToSingle(data, offset + 4));
    }
}

// --- BallEntity: IEntitySync for the ball ---
// State: [posX:4][posY:4] = 8 bytes

public class BallEntity : MonoBehaviour, IEntitySync
{
    public int EntityId { get; set; }
    public int StateSize => 8;
    public Vector2 LogicalPosition;

    public int WriteState(byte[] buf, int offset)
    {
        BitConverter.TryWriteBytes(buf.AsSpan(offset, 4), LogicalPosition.x); offset += 4;
        BitConverter.TryWriteBytes(buf.AsSpan(offset, 4), LogicalPosition.y);
        return 8;
    }

    public void OnRemoteState(byte[] data, int offset, int length, int senderPlayerId)
    {
        if (length < 8) return;
        LogicalPosition = new Vector2(
            BitConverter.ToSingle(data, offset),
            BitConverter.ToSingle(data, offset + 4));
    }
}
