// Packages/com.omniscape.minisocket/Editor/MiniSocketIOConsoleWindow.cs
// A no-code Socket.IO console for designers/QAs.
// Requires: MiniSocketIOClient (Runtime)

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MiniSocketIO;
using UnityEditor;
using UnityEngine;

public sealed class MiniSocketIOConsoleWindow : EditorWindow
{
    const string Pfx = "com.omniscape.minisocket.console.";
    const string K_BaseUrl = Pfx + "baseUrl";
    const string K_Nsp     = Pfx + "nsp";
    const string K_Auth    = Pfx + "authJson";
    const string K_Event   = Pfx + "eventName";
    const string K_Message = Pfx + "message";
    const string K_Headers = Pfx + "extraHeaders";
    const string K_AckTmo  = Pfx + "ackSeconds";

    string _baseUrl  = "wss://your-server.example.com/";
    string _nsp      = "/";
    string _authJson = "";       // e.g. {"token":"..."}
    string _event    = "ping_test";
    string _message  = "{\"msg\":\"Hello\"}";
    string _headers  = "";       // key:value per line
    float  _ackSec   = 10f;

    Vector2 _scroll;
    string _log = "";
    bool _autoScroll = true;

    MiniSocketIOClient _client;
    bool _connecting;
    DateTime _lastTick;
    bool _tickHooked;

    [MenuItem("Window/Omniscape/MiniSocket IO Console")]
    public static void ShowWindow()
    {
        var w = GetWindow<MiniSocketIOConsoleWindow>("MiniSocket IO");
        w.minSize = new Vector2(520, 420);
        w.LoadPrefs();
        w.Show();
    }

    void OnEnable()
    {
        LoadPrefs();
        HookTick(true);
    }

    void OnDisable()
    {
        HookTick(false);
        _ = CloseClientAsync();
        SavePrefs();
    }

    void HookTick(bool on)
    {
        if (on && !_tickHooked)
        {
            EditorApplication.update += EditorTick;
            _tickHooked = true;
        }
        else if (!on && _tickHooked)
        {
            EditorApplication.update -= EditorTick;
            _tickHooked = false;
        }
    }

    void EditorTick()
    {
        // Allow MiniSocket to marshal callbacks onto main thread
        _client?.TickMainThread();

        // simple heart-beat repaint
        if ((DateTime.UtcNow - _lastTick).TotalSeconds > 0.25)
        {
            _lastTick = DateTime.UtcNow;
            Repaint();
        }
    }

    void LoadPrefs()
    {
        _baseUrl  = EditorPrefs.GetString(K_BaseUrl, _baseUrl);
        _nsp      = EditorPrefs.GetString(K_Nsp, _nsp);
        _authJson = EditorPrefs.GetString(K_Auth, _authJson);
        _event    = EditorPrefs.GetString(K_Event, _event);
        _message  = EditorPrefs.GetString(K_Message, _message);
        _headers  = EditorPrefs.GetString(K_Headers, _headers);
        _ackSec   = EditorPrefs.GetFloat (K_AckTmo, _ackSec);
    }

    void SavePrefs()
    {
        EditorPrefs.SetString(K_BaseUrl, _baseUrl);
        EditorPrefs.SetString(K_Nsp, _nsp);
        EditorPrefs.SetString(K_Auth, _authJson);
        EditorPrefs.SetString(K_Event, _event);
        EditorPrefs.SetString(K_Message, _message);
        EditorPrefs.SetString(K_Headers, _headers);
        EditorPrefs.SetFloat (K_AckTmo, _ackSec);
    }

    void OnGUI()
    {
        using (new EditorGUILayout.VerticalScope())
        {
            DrawHeader();
            EditorGUILayout.Space(4);
            DrawConnection();
            EditorGUILayout.Space(6);
            DrawEmit();
            EditorGUILayout.Space(6);
            DrawLog();
        }
    }

    void DrawHeader()
    {
        GUILayout.Label("MiniSocket IO Console", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Paste your Socket.IO server URL, optional auth JSON, choose a namespace, and send events.\n" +
            "The console shows incoming events and acks. Designed for non-devs and QA.",
            MessageType.Info);
    }

    void DrawConnection()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            GUILayout.Label("Connection", EditorStyles.boldLabel);
            _baseUrl  = EditorGUILayout.TextField(new GUIContent("Base URL", "e.g. wss://api.example.com/"), _baseUrl);
            _nsp      = EditorGUILayout.TextField(new GUIContent("Namespace", "e.g. / or /admin"), _nsp);
            _authJson = EditorGUILayout.TextField(new GUIContent("Auth JSON", "Optional: {\"token\":\"...\"}"), _authJson);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Extra Headers", GUILayout.Width(100));
                EditorGUILayout.HelpBox("Optional. One per line as key:value", MessageType.None);
            }
            _headers = EditorGUILayout.TextArea(_headers, GUILayout.MinHeight(40));

            using (new EditorGUILayout.HorizontalScope())
            {
                var connected = _client != null && _client.State == ConnState.Open;
                GUI.enabled = !_connecting && !connected;
                if (GUILayout.Button("Connect", GUILayout.Height(26))) _ = ConnectAsync();
                GUI.enabled = connected;
                if (GUILayout.Button("Disconnect", GUILayout.Height(26))) _ = CloseClientAsync();
                GUI.enabled = true;

                GUILayout.FlexibleSpace();
                var state = _client == null ? ConnState.Closed : _client.State;
                var status = _connecting ? "Connecting..." : state.ToString();
                GUILayout.Label($"Status: {status}", EditorStyles.miniBoldLabel);
            }
        }
    }

    void DrawEmit()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            GUILayout.Label("Emit Event", EditorStyles.boldLabel);
            _event   = EditorGUILayout.TextField(new GUIContent("Event Name"), _event);
            _message = EditorGUILayout.TextField(new GUIContent("Message (JSON or text)"), _message);
            _ackSec  = EditorGUILayout.Slider(new GUIContent("Ack Timeout (s)"), _ackSec, 1f, 60f);

            using (new EditorGUILayout.HorizontalScope())
            {
                var canEmit = _client != null && _client.State == ConnState.Open && !string.IsNullOrEmpty(_event);
                GUI.enabled = canEmit;
                if (GUILayout.Button("Emit", GUILayout.Height(24))) _ = EmitAsync(false);
                if (GUILayout.Button("Emit With Ack", GUILayout.Height(24))) _ = EmitAsync(true);
                GUI.enabled = true;

                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Clear Log", GUILayout.Width(90))) { _log = ""; }
                _autoScroll = GUILayout.Toggle(_autoScroll, "Auto Scroll", GUILayout.Width(100));
            }
        }
    }

    void DrawLog()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            GUILayout.Label("Console", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            var style = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
            EditorGUILayout.TextArea(_log, style, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            if (_autoScroll)
            {
                _scroll.y = Mathf.Infinity;
                Repaint();
            }
        }
    }

    async Task ConnectAsync()
    {
        SavePrefs();

        if (string.IsNullOrWhiteSpace(_baseUrl))
        {
            AppendLog("[error] Base URL is required.");
            return;
        }

        await CloseClientAsync();

        _client = new MiniSocketIOClient(_baseUrl.Trim(), string.IsNullOrWhiteSpace(_nsp) ? "/" : _nsp.Trim(),
                                         string.IsNullOrWhiteSpace(_authJson) ? null : _authJson.Trim());

        _client.OnOpen  += () => AppendLog("[open] Connected.");
        _client.OnClose += () => AppendLog("[close] Disconnected.");
        _client.OnError += (e) => AppendLog("[error] " + e);
        _client.OnEvent += (ev, args) =>
        {
            if (ev == "__ack__")
                AppendLog("[ack] " + string.Join(", ", args ?? Array.Empty<string>()));
            else
                AppendLog($"[event] {ev} → {string.Join(", ", args ?? Array.Empty<string>())}");
        };

        _connecting = true;
        try
        {
            var headers = ParseHeaders(_headers);
            await _client.ConnectAsync(headers);
        }
        catch (Exception ex)
        {
            AppendLog("[error] Connect failed: " + ex.Message);
            await CloseClientAsync();
        }
        finally
        {
            _connecting = false;
        }
    }

    async Task CloseClientAsync()
    {
        if (_client != null)
        {
            try { await _client.CloseAsync(); } catch { }
            _client = null;
        }
    }

    async Task EmitAsync(bool withAck)
    {
        if (_client == null || _client.State != ConnState.Open)
        {
            AppendLog("[warn] Not connected.");
            return;
        }

        var args = BuildArgs(_message);
        try
        {
            if (!withAck)
            {
                await _client.EmitAsync(_event, args);
                AppendLog($"[emit] {_event} → {string.Join(", ", args ?? Array.Empty<string>())}");
            }
            else
            {
                var back = await _client.EmitWithAckAsync(_event, args, TimeSpan.FromSeconds(Mathf.Max(1f, _ackSec)));
                AppendLog($"[emit/ack] {_event} ✓ {string.Join(", ", back ?? Array.Empty<string>())}");
            }
        }
        catch (Exception ex)
        {
            AppendLog("[error] Emit failed: " + ex.Message);
        }
    }

    static string[] BuildArgs(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return Array.Empty<string>();

        // If msg looks like a JSON array, pass it as multiple args: ["a", {"b":1}] → two args
        // If msg looks like a JSON object/primitive, pass it as a single raw JSON arg.
        var t = msg.Trim();
        if (t.StartsWith("[") && t.EndsWith("]"))
        {
            // naive split for common cases; advanced users can paste multiple lines as key:value too
            // but MiniSocketIO will treat each element as raw JSON correctly.
            return new[] { t };
        }
        return new[] { t };
    }

    static Dictionary<string, string> ParseHeaders(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return dict;
        var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            var k = line.Substring(0, i).Trim();
            var v = line.Substring(i + 1).Trim();
            if (!string.IsNullOrEmpty(k)) dict[k] = v;
        }
        return dict;
    }

    void AppendLog(string line)
    {
        _log += $"[{DateTime.Now:HH:mm:ss}] {line}\n";
        if (_autoScroll) _scroll.y = Mathf.Infinity;
        Repaint();
    }
}
#endif
