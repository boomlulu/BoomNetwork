using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using BoomNetwork.Client.FrameSync;
using BoomNetwork.Unity;

/// <summary>
/// Chat Room Demo — 聊天房（不启动帧同步）
///
/// 演示 SendStateMessage 做实时消息广播：
///   - 连接 → 匹配房间 → 不启动帧同步
///   - 输入文字 → SendStateMessage → 服务器转发 → OnStateMessage 接收
///   - 玩家加入/离开通知
///
/// 核心 API：
///   Client.SendStateMessage(byte[]) — 发送（服务器转发，不存储）
///   Client.OnStateMessage(playerId, data) — 接收
/// </summary>
[RequireComponent(typeof(BoomNetworkManager))]
public class ChatRoom : MonoBehaviour
{
    BoomNetworkManager _network;
    readonly List<string> _messages = new();
    string _input = "";
    Vector2 _scroll;
    string _status = "Disconnected";
    readonly List<int> _roomPlayers = new();

    void Start()
    {
        _network = GetComponent<BoomNetworkManager>();

        var c = _network.Client;
        c.OnConnected += () =>
        {
            _status = "Connected, matching...";
            c.MatchRoom(4, "chatroom");
        };
        c.OnJoinedRoom += (roomId, existing) =>
        {
            _status = $"In Room {roomId}";
            _roomPlayers.Clear();
            _roomPlayers.AddRange(existing);
            _roomPlayers.Add(_network.PlayerId);
            AddSystemMsg($"Joined room {roomId} (players: {string.Join(", ", _roomPlayers)})");
        };
        c.OnPlayerJoinedMsg += pid =>
        {
            if (!_roomPlayers.Contains(pid)) _roomPlayers.Add(pid);
            AddSystemMsg($"Player {pid} joined");
        };
        c.OnPlayerLeftMsg += pid =>
        {
            _roomPlayers.Remove(pid);
            AddSystemMsg($"Player {pid} left");
        };
        c.OnStateMessage += OnStateMessage;
        c.OnDisconnected += () => { _status = "Disconnected"; AddSystemMsg("Disconnected"); };
        c.OnLeftRoom += _ => { _status = "Connected"; _roomPlayers.Clear(); };
    }

    void OnStateMessage(int playerId, byte[] data)
    {
        if (data == null || data.Length == 0) return;
        string text = Encoding.UTF8.GetString(data);
        AddMsg(playerId, text);
    }

    void SendChat(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_network.Client.CurrentState < FrameSyncClient.State.InRoom) return;

        byte[] data = Encoding.UTF8.GetBytes(text);
        _network.Client.SendStateMessage(data);
        AddMsg(_network.PlayerId, text);
    }

    void AddMsg(int pid, string text)
    {
        string prefix = pid == _network.PlayerId ? "You" : $"P{pid}";
        _messages.Add($"[{DateTime.Now:HH:mm:ss}] {prefix}: {text}");
        if (_messages.Count > 100) _messages.RemoveAt(0);
        _scroll.y = float.MaxValue;
    }

    void AddSystemMsg(string text)
    {
        _messages.Add($"[{DateTime.Now:HH:mm:ss}] --- {text} ---");
        if (_messages.Count > 100) _messages.RemoveAt(0);
        _scroll.y = float.MaxValue;
    }

    void OnGUI()
    {
        var title = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
        var label = new GUIStyle(GUI.skin.label) { fontSize = 13 };
        var msgStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, richText = true };
        var btn = new GUIStyle(GUI.skin.button) { fontSize = 14 };
        var inputStyle = new GUIStyle(GUI.skin.textField) { fontSize = 14 };

        float w = Mathf.Min(Screen.width - 20, 500);
        float h = Screen.height - 20;
        GUILayout.BeginArea(new Rect(10, 10, w, h));

        GUILayout.Label("Chat Room", title);
        GUILayout.Label($"Status: {_status}  |  Player: {_network.PlayerId}  |  Online: {_roomPlayers.Count}", label);
        GUILayout.Space(5);

        var state = _network.Client.CurrentState;
        if (state == FrameSyncClient.State.Disconnected)
        {
            if (GUILayout.Button("Connect", btn, GUILayout.Height(35)))
                _network.Connect();
        }
        else if (state >= FrameSyncClient.State.InRoom)
        {
            // 消息列表
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            foreach (var msg in _messages)
                GUILayout.Label(msg, msgStyle);
            GUILayout.EndScrollView();

            // 输入栏
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("ChatInput");
            _input = GUILayout.TextField(_input, inputStyle, GUILayout.Height(30));

            if (GUILayout.Button("Send", btn, GUILayout.Width(60), GUILayout.Height(30))
                || (Event.current.isKey && Event.current.keyCode == KeyCode.Return
                    && GUI.GetNameOfFocusedControl() == "ChatInput"))
            {
                SendChat(_input);
                _input = "";
                GUI.FocusControl("ChatInput");
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(3);
            if (GUILayout.Button("Leave Room", btn, GUILayout.Height(25)))
                _network.Client.LeaveRoom();
        }
        else
        {
            GUILayout.Label("Connecting...", label);
        }

        GUILayout.EndArea();
    }
}
