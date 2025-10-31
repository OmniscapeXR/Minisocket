using System;
using UnityEngine;
using UnityEngine.Events;

namespace MiniSocketIO
{
    [Serializable] public class StringEvt : UnityEvent<string> {}
    [Serializable] public class EventEvt : UnityEvent<string, string[]> {}
    public class MiniSocketIOBehavior : MonoBehaviour
    {
        [Header("Socket.IO Endpoint")]
        [Tooltip("WS(S) base URL to your server (we append /socket.io/?EIO=4&transport=websocket) ")]
        public string baseUrl = "ws://localhost:3000/";
        [Tooltip("Namespace like '/' or '/chat'")]
        public string nsp = "/";
        [Tooltip("Auth JSON to send inside CONNECT packet, e.g. {\"token\":\"123\"}")]
        public string authJson;


        [Header("Events")]
        public UnityEvent onOpen;
        public UnityEvent onClose;
        public StringEvt onError;
        public EventEvt onEvent; // (eventName, args[])


        MiniSocketIOClient _c;


        async void OnEnable()
        {
            _c = new MiniSocketIOClient(baseUrl, nsp, string.IsNullOrWhiteSpace(authJson) ? null : authJson);
            _c.OnOpen += () => onOpen?.Invoke();
            _c.OnClose += () => onClose?.Invoke();
            _c.OnError += (e) => onError?.Invoke(e);
            _c.OnEvent += (ev, args) => onEvent?.Invoke(ev, args);


            try { await _c.ConnectAsync(); }
            catch (Exception e) { onError?.Invoke($"Connect failed: {e.Message}"); }
        }


        void Update() => _c?.TickMainThread();


        async void OnDisable()
        {
            if (_c != null)
            {
                try { await _c.CloseAsync(); } catch { }
                _c = null;
            }
        }


        public async void Emit(string eventName, params string[] args)
        {
            try { await _c.EmitAsync(eventName, args); }
            catch (Exception e) { onError?.Invoke($"Emit failed: {e.Message}"); }
        }


        public async void EmitWithAck(string eventName, params string[] args)
        {
            try { var back = await _c.EmitWithAckAsync(eventName, args); onEvent?.Invoke("__ack__", back); }
            catch (Exception e) { onError?.Invoke($"Emit/ack failed: {e.Message}"); }
        }
    }
}
