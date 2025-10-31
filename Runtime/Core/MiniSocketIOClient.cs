using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MiniSocketIO
{
    public enum ConnState { Closed, Connecting, Open }

    public sealed class MiniSocketIOClient : IDisposable
    {
        // Engine.IO (v4)
        const char EIO_OPEN    = '0';
        const char EIO_PING    = '2';
        const char EIO_PONG    = '3';
        const char EIO_MESSAGE = '4';

        // Socket.IO (v5)
        const char SIO_CONNECT       = '0';
        const char SIO_DISCONNECT    = '1';
        const char SIO_EVENT         = '2';
        const char SIO_ACK           = '3';
        const char SIO_CONNECT_ERROR = '4';
        
        public Action<string> OnRawIncoming;   // full Engine.IO text frames (e.g., "42[...]")
        public Action<string> OnRawOutgoing;   // full Engine.IO text frames we send
        public bool LogFrames = true;          // toggle verbose send/recv logs

        int _sendSeq = 0;                      // monotonic counter for emits
        
        void PrintHandshakeDetails(ClientWebSocket ws)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Handshake Details");
            sb.AppendLine($"Request URL: {Uri}");
            sb.AppendLine("Request Method: GET");
            sb.AppendLine("Status Code: 101 Switching Protocols");
            sb.AppendLine("Request Headers");
            sb.AppendLine("sec-websocket-version: 13");
            sb.AppendLine($"sec-websocket-key: (auto-generated)");
            sb.AppendLine("connection: Upgrade");
            sb.AppendLine("upgrade: websocket");
            sb.AppendLine("sec-websocket-extensions: permessage-deflate; client_max_window_bits");
            sb.AppendLine($"host: {Uri.Host}");
            sb.AppendLine("Response Headers");
            sb.AppendLine("sec-websocket-accept: (runtime-generated)");
            sb.AppendLine($"date: {System.DateTime.UtcNow:R}");
            sb.AppendLine("server: Unity WebSocket Client");
            sb.AppendLine("Upgrade: websocket");
            sb.AppendLine("Connection: Upgrade");
            sb.AppendLine();

            Debug.Log(sb.ToString());
        }


        static string Trunc(string s, int max = 240)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }

        public ConnState State { get; private set; } = ConnState.Closed;
        public bool EnableDebugLogs = false;
        public Uri Uri { get; }
        public string Namespace { get; }

        public event Action OnOpen;
        public event Action OnClose;
        public event Action<string> OnError;
        public event Action<string, string[]> OnEvent;
        public Action<string> OnRawSocketMessage; // debug hook

        readonly string _authJson;
        readonly TimeSpan _reconnectMin;
        readonly TimeSpan _reconnectMax;
        readonly float _backoffFactor;

        ClientWebSocket _ws = new ClientWebSocket();
        readonly CancellationTokenSource _life = new CancellationTokenSource();
        readonly ConcurrentQueue<Action> _main = new ConcurrentQueue<Action>();
        readonly ConcurrentDictionary<int, TaskCompletionSource<string[]>> _acks = new();

        int _nextAckId = 1;
        int _pingIntervalMs = 25000;   // from OPEN (not actively used for timers, we just reply to ping)
        bool _intentionalClose;

        // handshake hooks
        Action<int,int> _onEioOpen;
        Func<Task> _onEioOpenAfter;
        TaskCompletionSource<bool> _nspReadyTcs;

        public MiniSocketIOClient(
            string baseUrl,
            string nsp = "/",
            string authJson = null,
            TimeSpan? reconnectMin = null,
            TimeSpan? reconnectMax = null,
            float backoffFactor = 2f)
        {
            // EXACTLY like Postman: ws(s) + /socket.io/?EIO=4&transport=websocket
            var host = baseUrl.TrimEnd('/');
            var full = $"{host}/socket.io/?EIO=4&transport=websocket";
            Uri = new Uri(full);

            Namespace = string.IsNullOrEmpty(nsp) ? "/" : nsp;
            _authJson = authJson;
            _reconnectMin = reconnectMin ?? TimeSpan.FromSeconds(1);
            _reconnectMax = reconnectMax ?? TimeSpan.FromSeconds(10);
            _backoffFactor = Mathf.Max(1.1f, backoffFactor);
        }

        public void TickMainThread()
        {
            while (_main.TryDequeue(out var a))
                try { a(); } catch (Exception e) { Debug.LogException(e); }
        }

        void Enq(Action a) { _main.Enqueue(a); }
        void D(string msg) { if (EnableDebugLogs) Debug.Log($"[MiniSocketIO] {msg}"); }

        public async Task ConnectAsync(Dictionary<string,string> extraHeaders = null)
        {
            if (State != ConnState.Closed) return;
            State = ConnState.Connecting;
            _intentionalClose = false;
            var backoff = _reconnectMin.TotalMilliseconds;

            while (!_intentionalClose && !_life.IsCancellationRequested)
            {
                using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    D($"→ CONNECT {Uri}");
                    _ws?.Dispose();
                    _ws = new ClientWebSocket();
                    if (extraHeaders != null)
                        foreach (var kv in extraHeaders) _ws.Options.SetRequestHeader(kv.Key, kv.Value);

                    // Prepare latches/handlers BEFORE any receive can fire
                    var tcsEioOpen = new TaskCompletionSource<bool>();
                    _nspReadyTcs = new TaskCompletionSource<bool>();

                    _onEioOpen = (interval, timeout) =>
                    {
                        _pingIntervalMs = interval;
                        if (!tcsEioOpen.Task.IsCompleted) tcsEioOpen.TrySetResult(true);
                    };
                    _onEioOpenAfter = async () =>
                    {
                        // Send Socket.IO CONNECT frame (40…)
                        if (Namespace == "/")
                        {
                            // 40 or 40{json}
                            var frame = _authJson == null ? "40" : $"40{_authJson}";
                            await SendRawAsync(frame, _life.Token);
                        }
                        else
                        {
                            // 40<nsp>, or 40<nsp>,{json}
                            var frame = _authJson == null ? $"40{Namespace}," : $"40{Namespace},{_authJson}";
                            await SendRawAsync(frame, _life.Token);
                        }
                    };

                    await _ws.ConnectAsync(Uri, connectCts.Token);
                    D("✓ WebSocket connected (Engine.IO handshake next)");
                    PrintHandshakeDetails(_ws);
                    State = ConnState.Open;

                    // Start the receive loop AFTER handlers are set
                    var recv = ReceiveLoop(_ws, _life.Token);

                    // Wait for Engine.IO OPEN (0{...}) then SIO CONNECT will be sent by _onEioOpenAfter
                    using (var openTmo = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    using (openTmo.Token.Register(() => tcsEioOpen.TrySetCanceled()))
                        await tcsEioOpen.Task;

                    // Wait for namespace CONNECT ack (server sends 40…)
                    using (var nspTmo = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    using (nspTmo.Token.Register(() => _nspReadyTcs.TrySetCanceled()))
                        await _nspReadyTcs.Task;

                    Enq(() => OnOpen?.Invoke());

                    await recv; // block here until closed
                    break;
                }
                catch (Exception e)
                {
                    Enq(() => OnError?.Invoke($"Connect error: {e.Message}"));
                    State = ConnState.Connecting;
                    if (_intentionalClose) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(backoff), _life.Token);
                    backoff = Math.Min(backoff * _backoffFactor, _reconnectMax.TotalMilliseconds);
                }
            }
        }

        async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var seg = new ArraySegment<byte>(buffer);
            var sb = new StringBuilder(4096);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    sb.Length = 0;
                    WebSocketReceiveResult res;
                    do
                    {
                        res = await ws.ReceiveAsync(seg, ct);
                        if (res.MessageType == WebSocketMessageType.Close)
                        {
                            State = ConnState.Closed;
                            Enq(() => OnClose?.Invoke());
                            return;
                        }
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                    } while (!res.EndOfMessage);

                    var text = sb.ToString();
                    if (string.IsNullOrEmpty(text)) continue;

                    if (LogFrames) OnRawIncoming?.Invoke(text);   // <— NEW
                    D($"← {Trunc(text)}");

                    var eioType = text[0];
                    var payload = text.Length > 1 ? text.Substring(1) : string.Empty;

                    switch (eioType)
                    {
                        case EIO_OPEN:      // 0{json}
                            try
                            {
                                var open = JsonUtility.FromJson<Handshake>(payload);
                                D($"Handshake OK • pingInterval={open.pingInterval}ms pingTimeout={open.pingTimeout}ms sid={open.sid}");
                                _onEioOpen?.Invoke(open.pingInterval, open.pingTimeout);
                                if (_onEioOpenAfter != null) await _onEioOpenAfter();
                            }
                            catch (Exception ex) { Enq(() => OnError?.Invoke($"Bad OPEN: {ex.Message}")); }
                            break;

                        case EIO_PING:      // 2  → reply 3
                            await SendRawAsync(EIO_PONG.ToString(), ct);
                            break;

                        case EIO_MESSAGE:   // 4… → Socket.IO packet
                            HandleSocketIoMessage(payload);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Enq(() => OnError?.Invoke($"Receive error: {e.Message}")); }
            finally
            {
                State = ConnState.Closed;
                Enq(() => OnClose?.Invoke());
            }
        }

        void HandleSocketIoMessage(string s)
        {
            OnRawSocketMessage?.Invoke(s);
            if (string.IsNullOrEmpty(s)) return;

            int idx = 0;
            char t = s[idx++];

            // optional namespace
            if (idx < s.Length && s[idx] == '/')
            {
                int comma = s.IndexOf(',', idx);
                if (comma == -1) return; // malformed
                idx = comma + 1;
            }

            // optional ack id
            int? ackId = null;
            int jsonStart = idx;
            while (jsonStart < s.Length && char.IsDigit(s[jsonStart])) jsonStart++;
            if (jsonStart > idx && int.TryParse(s.Substring(idx, jsonStart - idx), out var id))
            {
                ackId = id;
                idx = jsonStart;
            }

            var json = idx < s.Length ? s.Substring(idx) : null;

            switch (t)
            {
                case SIO_CONNECT:
                    _nspReadyTcs?.TrySetResult(true);
                    break;

                case SIO_CONNECT_ERROR:
                    _nspReadyTcs?.TrySetException(new InvalidOperationException($"CONNECT_ERROR {json}"));
                    Enq(() => OnError?.Invoke($"CONNECT_ERROR {json}"));
                    break;

                case SIO_EVENT:
                    try
                    {
                        var arr = MiniJson.ParseArray(json);
                        if (arr.Length > 0)
                        {
                            var evt = arr[0];
                            var args = new string[arr.Length - 1];
                            Array.Copy(arr, 1, args, 0, args.Length);
                            Enq(() => OnEvent?.Invoke(evt, args));
                        }
                    }
                    catch (Exception ex) { Enq(() => OnError?.Invoke($"EVENT parse error: {ex.Message}")); }
                    break;

                case SIO_ACK:
                    try
                    {
                        var arr = MiniJson.ParseArray(json);
                        if (ackId.HasValue && _acks.TryRemove(ackId.Value, out var tcs))
                            tcs.TrySetResult(arr);
                    }
                    catch (Exception ex) { Enq(() => OnError?.Invoke($"ACK parse error: {ex.Message}")); }
                    break;

                case SIO_DISCONNECT:
                    Enq(() => OnClose?.Invoke());
                    break;
            }
        }

        public async Task EmitAsync(string eventName, string[] args = null, TimeSpan? ackTimeout = null)
        {
            // Build the Socket.IO array payload
            var jsonArray = MiniJson.MakeArray(eventName, args);

            // Engine.IO '4' + Socket.IO '2' (event) + optional namespace prefix
            var prefix = new StringBuilder();
            prefix.Append(EIO_MESSAGE).Append(SIO_EVENT);
            if (Namespace != "/") { prefix.Append(Namespace).Append(','); }
            var frame = prefix + jsonArray;

            // Emit diagnostics
            var seq = Interlocked.Increment(ref _sendSeq);
            var size = Encoding.UTF8.GetByteCount(jsonArray);
            D($"EMIT#{seq} event='{eventName}' nsp='{Namespace}' argsCount={args?.Length ?? 0} jsonBytes={size}");
            D($"EMIT#{seq} frame={Trunc(frame)}");

            try
            {
                await SendRawAsync(frame, _life.Token);
                D($"EMIT#{seq} SENT");
            }
            catch (Exception ex)
            {
                Enq(() => OnError?.Invoke($"EMIT#{seq} send failed: {ex.Message}"));
                throw;
            }
        }


        public async Task<string[]> EmitWithAckAsync(string eventName, string[] args = null, TimeSpan? ackTimeout = null)
        {
            var id = Interlocked.Increment(ref _nextAckId);
            D($"");
            var jsonArray = MiniJson.MakeArray(eventName, args);
            var prefix = new StringBuilder();
            prefix.Append(EIO_MESSAGE).Append(SIO_EVENT);
            if (Namespace != "/") { prefix.Append(Namespace).Append(','); }
            prefix.Append(id);
            var frame = prefix + jsonArray;

            var tcs = new TaskCompletionSource<string[]>();
            _acks[id] = tcs;

            await SendRawAsync(frame, _life.Token);

            using var cts = new CancellationTokenSource(ackTimeout ?? TimeSpan.FromSeconds(10));
            using (cts.Token.Register(() => tcs.TrySetException(new TimeoutException($"Ack timeout for {eventName}"))))
                return await tcs.Task;
        }

        public async Task CloseAsync()
        {
            _intentionalClose = true;
            D("→ CLOSE requested by client");
            try
            {
                // 41 (optionally with nsp), then close the websocket
                string frame = Namespace == "/" ? $"{EIO_MESSAGE}{SIO_DISCONNECT}" : $"{EIO_MESSAGE}{SIO_DISCONNECT}{Namespace},";
                await SendRawAsync(frame, _life.Token);
            }
            catch { /* ignore */ }
            try { if (_ws != null) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client close", CancellationToken.None); } catch { }
            State = ConnState.Closed;
            Enq(() => OnClose?.Invoke());
        }

        async Task SendRawAsync(string text, CancellationToken ct)
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
                throw new InvalidOperationException("socket not open");

            if (LogFrames) OnRawOutgoing?.Invoke(text);   // <— NEW
            D($"→ {Trunc(text)}");

            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            catch (Exception ex)
            {
                Enq(() => OnError?.Invoke($"Send error: {ex.Message}"));
                throw;
            }
        }

        public void Dispose()
        {
            _life.Cancel();
            _ws?.Dispose();
            _life.Dispose();
        }

        [Serializable]
        class Handshake { public string sid; public string[] upgrades; public int pingInterval; public int pingTimeout; public int maxPayload; }
    }

    // Tiny JSON helpers
    static class MiniJson
    {
        public static string MakeArray(string first, string[] rest)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            sb.Append('"').Append(E(first)).Append('"');
            if (rest != null)
            {
                foreach (var r in rest)
                {
                    sb.Append(',');
                    if (r != null && (r.StartsWith("{") || r.StartsWith("[") || r == "null" || r == "true" || r == "false" || double.TryParse(r, out _)))
                        sb.Append(r);
                    else
                        sb.Append('"').Append(E(r)).Append('"');
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static string[] ParseArray(string json)
        {
            var list = new System.Collections.Generic.List<string>();
            int i = 0; SkipWs(); if (json.Length == 0 || json[i] != '[') throw new Exception("not an array"); i++; SkipWs();
            while (i < json.Length && json[i] != ']')
            {
                var elem = ReadValue(); list.Add(elem); SkipWs(); if (i < json.Length && json[i] == ',') { i++; SkipWs(); }
            }
            return list.ToArray();

            void SkipWs(){ while (i < json.Length && char.IsWhiteSpace(json[i])) i++; }
            string ReadValue()
            {
                SkipWs();
                if (json[i] == '"') return ReadString();
                if (json[i] == '{') return ReadObject();
                if (json[i] == '[') return ReadArray();
                int start = i; while (i < json.Length && ",]}".IndexOf(json[i]) == -1) i++; return json.Substring(start, i - start);
            }
            string ReadString()
            { int start = ++i; var sb = new StringBuilder(); while (i < json.Length){ var c=json[i++]; if (c=='\\'){ if (i<json.Length) { var n=json[i++]; sb.Append('\\').Append(n);} } else if (c=='"'){ break; } else sb.Append(c);} return '"'+sb.ToString()+'"'; }
            string ReadObject()
            { int depth=1; int start=i; i++; while (i<json.Length && depth>0){ var c=json[i++]; if (c=='{') depth++; else if (c=='}') depth--; } return json.Substring(start-1, i - start + 1); }
            string ReadArray()
            { int depth=1; int start=i; i++; while (i<json.Length && depth>0){ var c=json[i++]; if (c=='[') depth++; else if (c==']') depth--; } return json.Substring(start-1, i - start + 1); }
        }
        static string E(string s) => s?.Replace("\\","\\\\").Replace("\"","\\\"") ?? string.Empty;
    }
}
