using System;
using System.Collections.Generic;
using UnityEngine;
using BoomNetwork.Client.FrameSync;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Unity;

/// <summary>
/// Level 3 — 实体权威同步
///
/// 教会：IEntitySync 接口、权威实体注册、远端状态接收、视觉平滑。
///
/// 与 Level 0-2 的区别：
///   Level 0-2 用 OnFrame 收所有人的输入，然后本地算位置（锁步同步）。
///   Level 3 每个玩家只发自己的位置，远端直接收位置并平滑显示（状态同步）。
///
/// 核心哲学：
///   - 自己的操作零延迟（Owner 权威）
///   - 别人的状态信任并平滑（Lerp 插值）
/// </summary>
[RequireComponent(typeof(BoomNetworkManager))]
public class EntitySync : MonoBehaviour
{
    [Header("Game")]
    [SerializeField] private float moveSpeed = 1f;
    [SerializeField] private float smoothSpeed = 10f;

    private BoomNetworkManager _network;
    private readonly Dictionary<int, SyncedEntity> _entities = new();
    private readonly byte[] _inputBuf = new byte[8];
    private uint _lastFrame;
    private float _sendTimer;
    private float _lastH, _lastV;
    private bool _authorityRegistered;

    private static readonly Color[] Colors = { Color.green, new(0.3f, 0.5f, 1f), Color.red, Color.yellow };

    void Start()
    {
        _network = GetComponent<BoomNetworkManager>();

        var c = _network.Client;
        c.OnFrame += OnFrame;
        c.OnEntityState += OnEntityState;
        c.OnJoinedRoom += (_, existing) =>
        {
            // 为已有玩家预创建实体
            foreach (var pid in existing)
                GetOrCreateEntity(pid, false);
        };
        c.OnPlayerJoinedMsg += pid => GetOrCreateEntity(pid, false);
        c.OnPlayerLeftMsg += pid => DestroyEntity(pid);
        c.OnTakeSnapshot = TakeSnapshot;
        c.OnLoadSnapshot = LoadSnapshot;

        _network.QuickStart();
    }

    void Update()
    {
        if (!_network.IsSyncing) return;

        // 注册权威实体（仅一次，等 PlayerId 分配后）
        if (!_authorityRegistered && _network.PlayerId > 0)
        {
            var local = GetOrCreateEntity(_network.PlayerId, true);
            _network.Client.RegisterAuthorityEntity(local);
            _authorityRegistered = true;
        }

        // 输入节流
        _sendTimer += Time.deltaTime * 1000f;
        if (_sendTimer < 50f) return;
        _sendTimer -= 50f;

        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");

        // 本地权威：移动逻辑位置，零延迟（视觉平滑在 LateUpdate）
        if (_entities.TryGetValue(_network.PlayerId, out var me))
        {
            var pos = me.LogicalPosition;
            pos.x += h * moveSpeed;
            pos.y += v * moveSpeed;
            if (pos.x > 8f) pos.x -= 16f; if (pos.x < -8f) pos.x += 16f;
            if (pos.y > 5f) pos.y -= 10f; if (pos.y < -5f) pos.y += 10f;
            me.LogicalPosition = pos;
            me.Velocity = new Vector2(h, v) * moveSpeed;
        }

        // 发送输入（触发 SendAuthorityEntityStates）
        if (Mathf.Abs(h - _lastH) > 0.01f || Mathf.Abs(v - _lastV) > 0.01f)
        {
            _lastH = h; _lastV = v;
            BitConverter.TryWriteBytes(_inputBuf.AsSpan(0, 4), h);
            BitConverter.TryWriteBytes(_inputBuf.AsSpan(4, 4), v);
            _network.SendInput(_inputBuf);
        }
        else if (_authorityRegistered)
        {
            // 即使没有输入变化也要发送，驱动实体状态广播
            _network.SendInput(_inputBuf);
        }
    }

    void LateUpdate()
    {
        // 所有实体：visual 平滑追踪 logical（权威和远端统一处理）
        foreach (var kv in _entities)
        {
            var e = kv.Value;
            var current = (Vector2)e.transform.position;
            var target = e.LogicalPosition;

            // 瞬移检测：距离超过半屏则 snap（环绕传送）
            if (Vector2.Distance(current, target) > 6f)
                current = target;
            else
                current = Vector2.Lerp(current, target, smoothSpeed * Time.deltaTime);

            e.transform.position = new Vector3(current.x, current.y, 0);
        }
    }

    // --- 帧回调（Level 3 不在 OnFrame 里算位置，但仍需处理帧号）---

    void OnFrame(FrameData frame)
    {
        _lastFrame = frame.FrameNumber;
    }

    // --- 实体状态路由 ---

    void OnEntityState(int senderPid, int entityId, byte[] data, int offset, int length)
    {
        var entity = GetOrCreateEntity(entityId, false);
        entity.OnRemoteState(data, offset, length, senderPid);
    }

    // --- Entity 管理 ---

    SyncedEntity GetOrCreateEntity(int entityId, bool isAuthority)
    {
        if (_entities.TryGetValue(entityId, out var existing))
        {
            if (isAuthority) existing.IsAuthority = true;
            return existing;
        }

        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = $"Entity_{entityId}";
        go.transform.localScale = Vector3.one * 0.8f;
        go.GetComponent<Renderer>().material.color = Colors[(entityId - 1) % Colors.Length];

        var entity = go.AddComponent<SyncedEntity>();
        entity.EntityId = entityId;
        entity.IsAuthority = isAuthority;
        _entities[entityId] = entity;
        return entity;
    }

    void DestroyEntity(int entityId)
    {
        if (_entities.TryGetValue(entityId, out var e))
        {
            if (e) Destroy(e.gameObject);
            _entities.Remove(entityId);
        }
    }

    // --- Snapshot ---

    byte[] TakeSnapshot()
    {
        var buf = new byte[2 + _entities.Count * 12];
        buf[0] = (byte)(_entities.Count & 0xFF); buf[1] = (byte)((_entities.Count >> 8) & 0xFF);
        int off = 2;
        foreach (var kv in _entities)
        {
            BitConverter.TryWriteBytes(buf.AsSpan(off, 4), kv.Key); off += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(off, 4), kv.Value.LogicalPosition.x); off += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(off, 4), kv.Value.LogicalPosition.y); off += 4;
        }
        return buf;
    }

    void LoadSnapshot(byte[] data)
    {
        if (data == null || data.Length < 2) return;
        int count = data[0] | (data[1] << 8); int off = 2;
        for (int i = 0; i < count && off + 12 <= data.Length; i++)
        {
            int eid = BitConverter.ToInt32(data, off); off += 4;
            float x = BitConverter.ToSingle(data, off); off += 4;
            float y = BitConverter.ToSingle(data, off); off += 4;
            var e = GetOrCreateEntity(eid, eid == _network.PlayerId);
            e.LogicalPosition = new Vector2(x, y);
            e.transform.position = new Vector3(x, y, 0);
        }
    }

    // --- UI ---

    void OnGUI()
    {
        var title = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
        var label = new GUIStyle(GUI.skin.label) { fontSize = 13 };

        GUILayout.BeginArea(new Rect(10, 10, 300, 250));
        GUILayout.Label("Entity Sync Demo", title);
        GUILayout.Label($"State: {_network.Client.CurrentState}", label);
        GUILayout.Label($"Player: {_network.PlayerId}  Frame: {_lastFrame}", label);
        GUILayout.Label($"RTT: {_network.Client.RttMs:F0}ms  Entities: {_entities.Count}", label);
        GUILayout.Space(5);
        foreach (var kv in _entities)
        {
            var e = kv.Value;
            var role = e.IsAuthority ? "Authority" : $"Remote (corrections: {e.CorrectionCount})";
            GUILayout.Label($"  Entity {e.EntityId}: {role}", label);
        }
        GUILayout.EndArea();
    }
}

/// <summary>
/// 最简 IEntitySync 实现 — 内联在 Sample 中，不依赖外部中间件。
///
/// 逻辑/视觉分离：
///   LogicalPosition — 权威真值（权威端直接写，远端收服务器状态写）
///   transform.position — 视觉位置（LateUpdate 中 Lerp 追踪 Logical）
///
/// 状态格式：[posX:4][posY:4][velX:4][velY:4] = 16 bytes
/// </summary>
public class SyncedEntity : MonoBehaviour, IEntitySync
{
    public int EntityId { get; set; }
    public int StateSize => 16;
    public bool IsAuthority;
    public Vector2 LogicalPosition;  // 逻辑位置（权威真值）
    public Vector2 Velocity;
    public int CorrectionCount { get; private set; }

    public int WriteState(byte[] buf, int offset)
    {
        // 权威端：序列化逻辑位置（不是视觉位置）
        BitConverter.TryWriteBytes(buf.AsSpan(offset, 4), LogicalPosition.x); offset += 4;
        BitConverter.TryWriteBytes(buf.AsSpan(offset, 4), LogicalPosition.y); offset += 4;
        BitConverter.TryWriteBytes(buf.AsSpan(offset, 4), Velocity.x); offset += 4;
        BitConverter.TryWriteBytes(buf.AsSpan(offset, 4), Velocity.y);
        return 16;
    }

    public void OnRemoteState(byte[] data, int offset, int length, int senderPlayerId)
    {
        if (length < 16 || IsAuthority) return;
        float x = BitConverter.ToSingle(data, offset);
        float y = BitConverter.ToSingle(data, offset + 4);
        // 远端：直接信任权威状态，更新逻辑位置
        LogicalPosition = new Vector2(x, y);
        CorrectionCount++;
    }
}
