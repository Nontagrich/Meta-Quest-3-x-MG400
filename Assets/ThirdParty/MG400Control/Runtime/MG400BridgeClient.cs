using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace CPE494
{
    /// <summary>
    /// TCP client for bridge_server.py (control :5555).  Newline-delimited JSON.
    /// Reader thread -> concurrent queue -> drained on the Unity main thread,
    /// so OnState / OnAck / OnEvent always fire on the main thread.
    /// Auto-reconnects every 3 s and pings every 5 s.
    /// </summary>
    public class MG400BridgeClient : MonoBehaviour
    {
        private static MG400BridgeClient _instance;
        public static MG400BridgeClient Instance
        {
            get
            {
                if (_instance == null) _instance = FindAnyObjectByType<MG400BridgeClient>();
                return _instance;
            }
            private set => _instance = value;
        }

        [Header("Bridge location")]
        public string host = "127.0.0.1";
        public int controlPort = 5555;

        [Header("Heartbeat")]
        public float reconnectInterval = 3f;
        public float pingInterval = 5f;

        public bool IsConnected { get; private set; }
        public bool SimMode { get; private set; } = true;

        /// <summary>Last state snapshot (main thread only).</summary>
        public MG400State LastState { get; private set; }
        public float LastStateAge => Time.time - _lastStateTime;

        public event Action<MG400State> OnState;
        public event Action<MG400Ack> OnAck;
        public event Action<string, string> OnEvent;      // (event, source)
        public event Action<bool> OnConnectionChanged;

        // threads / sockets
        private TcpClient _client;
        private NetworkStream _stream;
        private readonly object _sendLock = new object();
        private Thread _readerThread;
        private volatile bool _running;

        // main-thread queues
        private readonly ConcurrentQueue<MG400State> _stateQueue = new ConcurrentQueue<MG400State>();
        private readonly ConcurrentQueue<MG400Ack> _ackQueue = new ConcurrentQueue<MG400Ack>();
        private readonly ConcurrentQueue<(string, string)> _eventQueue = new ConcurrentQueue<(string, string)>();

        private float _nextReconnect;
        private float _nextPing;
        private int _cmdId = 1;
        private float _lastStateTime = -999f;

        [Serializable]
        public class WorkspacePoint
        {
            public double x, y;
            public bool park;
        }

        [Serializable]
        public class OutCmd
        {
            public int id;
            public string cmd;
            public string text;
            public double[] joints;
            public double pct;
            public double x, y, z, r;
            public string type;
            public int index;
            public int value;
            public double speed;
            public bool preserveR;
            public WorkspacePoint[] path;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            Connect();
        }

        private void Update()
        {
            MainThread.Pump();
            DrainQueues();
            Heartbeat();
        }

        private void OnDisable()
        {
            _running = false;
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            IsConnected = false;
        }

        private void OnDestroy()
        {
            _running = false;
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            if (_instance == this) _instance = null;
        }

        // ------------------------------------------------------------------
        // public API
        // ------------------------------------------------------------------
        public int NextId() => _cmdId++;

        public void Send(string cmd, Action<OutCmd> setup = null)
        {
            var msg = new OutCmd { id = NextId(), cmd = cmd };
            setup?.Invoke(msg);
            SendJson(JsonUtility.ToJson(msg));
        }

        /// <summary>Send with a specific id (used when awaiting an ack).</summary>
        public void Send(int id, string cmd, Action<OutCmd> setup = null)
        {
            var msg = new OutCmd { id = id, cmd = cmd };
            setup?.Invoke(msg);
            SendJson(JsonUtility.ToJson(msg));
        }

        // ------------------------------------------------------------------
        // internals
        // ------------------------------------------------------------------
        private void SendJson(string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json + "\n");
            bool ok = false;
            lock (_sendLock)
            {
                if (_stream != null && IsConnected)
                {
                    try
                    {
                        _stream.Write(payload, 0, payload.Length);
                        _stream.Flush();
                        ok = true;
                    }
                    catch (Exception) { ok = false; }
                }
            }
            if (!ok)
            {
                Debug.LogWarning($"[Bridge] not connected, dropped '{cmdName(json)}'");
            }
        }

        private static string cmdName(string json)
        {
            int i = json.IndexOf("\"cmd\":\"", StringComparison.Ordinal);
            return i >= 0 ? json.Substring(i + 7, Math.Min(12, json.Length - i - 7)) : json;
        }

        private void Connect()
        {
            if (_running) return;
            _running = true;
            _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "MG400BridgeReader" };
            _readerThread.Start();
        }

        private void ReaderLoop()
        {
            var buf = new StringBuilder();
            while (_running)
            {
                if (!EnsureConnected())
                {
                    Thread.Sleep((int)(reconnectInterval * 1000));
                    continue;
                }
                try
                {
                    var raw = _stream;
                    if (raw == null) continue;
                    byte[] chunk = new byte[8192];
                    int n = raw.Read(chunk, 0, chunk.Length);
                    if (n == 0)
                    {
                        HandleDisconnect("server closed");
                        continue;
                    }
                    buf.Append(Encoding.UTF8.GetString(chunk, 0, n));
                    while (true)
                    {
                        string all = buf.ToString();
                        int nl = all.IndexOf('\n');
                        if (nl < 0) break;
                        string line = all.Substring(0, nl).Trim();
                        buf.Remove(0, nl + 1);
                        if (line.Length > 0)
                        {
                            try { ParseLine(line); }
                            catch (Exception pe) { Debug.LogWarning($"[Bridge] parse line error: {pe.Message}"); }
                        }
                    }
                    if (buf.Length > 65536) buf.Clear();     // runaway guard
                }
                catch (Exception ex)
                {
                    if (_running)
                        HandleDisconnect($"read error: {ex.GetType().Name} ({ex.Message})");
                }
            }
        }

        private bool _loggedConnectFail;

        private bool EnsureConnected()
        {
            if (IsConnected) return true;
            if (!_running) return false;
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                client.ReceiveTimeout = 0;
                client.SendTimeout = 5000;
                var result = client.BeginConnect(host, controlPort, null, null);
                if (!result.AsyncWaitHandle.WaitOne(1000))
                {
                    try { client.Close(); } catch { }
                    return false;
                }
                if (!_running)
                {
                    try { client.Close(); } catch { }
                    return false;
                }
                client.EndConnect(result);
                client.NoDelay = true;
                var stream = client.GetStream();
                _client = client;
                _stream = stream;
                IsConnected = true;
                _loggedConnectFail = false;
                // NOTE: do NOT touch Time.time here -- this runs on the reader
                // thread and Unity API throws off the main thread.  DrainQueues
                // stamps _lastStateTime on the main thread when state arrives.
                Debug.Log($"[Bridge] connected to {host}:{controlPort}");
                MainThread.Enqueue(() => OnConnectionChanged?.Invoke(true));
                // identify ourselves + ask for state
                SendJson("{\"id\":0,\"cmd\":\"ping\"}");
                return true;
            }
            catch (Exception e)
            {
                if (_running && !_loggedConnectFail)
                {
                    _loggedConnectFail = true;
                    Debug.Log($"[Bridge] offline (using LOCAL-SIM): {e.Message}");
                }
                try { client?.Close(); } catch { }
                IsConnected = false;
                return false;
            }
        }

        private void HandleDisconnect(string reason)
        {
            if (!IsConnected) return;
            IsConnected = false;
            Debug.LogWarning($"[Bridge] disconnected ({reason}); retrying in {reconnectInterval}s");
            try { _client?.Close(); } catch { }
            _client = null;
            _stream = null;
            MainThread.Enqueue(() => OnConnectionChanged?.Invoke(false));
        }

        private void ParseLine(string line)
        {
            try
            {
                // cheap type peek avoids double-parse of unknown payloads
                if (line.Contains("\"type\": \"state\"") || line.Contains("\"type\":\"state\""))
                {
                    var st = JsonUtility.FromJson<MG400State>(line);
                    if (st != null) _stateQueue.Enqueue(st);
                }
                else if (line.Contains("\"ack\""))
                {
                    var ack = JsonUtility.FromJson<MG400Ack>(line);
                    if (ack != null) _ackQueue.Enqueue(ack);
                }
                else if (line.Contains("\"event\""))
                {
                    var ev = JsonUtility.FromJson<EventWire>(line);
                    if (ev != null && !string.IsNullOrEmpty(ev.@event))
                        _eventQueue.Enqueue((ev.@event, ev.source ?? ""));
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bridge] bad line: {e.Message}");
            }
        }

        [Serializable]
        private class EventWire
        {
            public string type;
            public string @event;
            public string source;
        }

        private void DrainQueues()
        {
            while (_stateQueue.TryDequeue(out var st))
            {
                LastState = st;
                SimMode = st.sim;
                _lastStateTime = Time.time;
                OnState?.Invoke(st);
            }
            while (_ackQueue.TryDequeue(out var ack)) OnAck?.Invoke(ack);
            while (_eventQueue.TryDequeue(out var ev)) OnEvent?.Invoke(ev.Item1, ev.Item2);
        }

        private void Heartbeat()
        {
            if (Time.time >= _nextPing)
            {
                _nextPing = Time.time + pingInterval;
                if (IsConnected) SendJson("{\"id\":-1,\"cmd\":\"ping\"}");
            }
        }

        /// <summary>True when a state snapshot arrived within 1.5 s.</summary>
        public bool StateFresh => Time.time - _lastStateTime < 1.5f;
    }

    /// <summary>Tiny main-thread dispatcher for callbacks from reader thread.</summary>
    public static class MainThread
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        public static void Enqueue(Action action) => Queue.Enqueue(action);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() => Queue.Clear();

        /// <summary>Call from one Update()-driven MonoBehaviour (bridge client does it via helper).</summary>
        public static void Pump()
        {
            while (Queue.TryDequeue(out var a))
            {
                try { a(); } catch (Exception e) { Debug.LogException(e); }
            }
        }
    }
}
