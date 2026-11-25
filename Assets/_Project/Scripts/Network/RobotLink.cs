using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GemStore.Network
{
    /// <summary>
    /// TCP client to the robot arm. Protocol: one JSON object per line, UTF-8, each line ends with '\n'.
    /// All socket work (connect, read, heartbeat, reconnect) runs on a background thread; messages are queued and
    /// delivered on the main thread in Update, so handlers may touch Unity objects freely.
    /// Heartbeat: {"type":"ping"} every heartbeatInterval; if nothing at all arrives for timeout seconds the link is dropped
    /// and reconnected every reconnectInterval.
    /// </summary>
    [DisallowMultipleComponent]
    public class RobotLink : MonoBehaviour
    {
        [Serializable]
        private class TypeProbe
        {
            public string type;
        }

        [SerializeField] private RobotLinkConfig config;

        private const string PingJson = "{\"type\":\"ping\"}";
        private const string PongJson = "{\"type\":\"pong\"}";

        private readonly ConcurrentQueue<Action> mainThreadQueue = new ConcurrentQueue<Action>();
        private readonly object writeLock = new object();

        private Thread worker;
        private volatile bool running;
        private TcpClient activeClient;     // guarded by writeLock
        private NetworkStream activeStream; // guarded by writeLock
        private bool connected;             // main-thread view

        public static RobotLink Instance { get; private set; }

        /// <summary>True while the link is up (updated on the main thread).</summary>
        public bool IsConnected => connected;

        /// <summary>The address the link is using (or about to use).</summary>
        public string Host { get; private set; } = string.Empty;

        public int Port { get; private set; }

        /// <summary>Saves a new address (PlayerPrefs) and reconnects at once. Any teleop is stopped by the connection drop.</summary>
        public void SetAddress(string host, int port)
        {
            RobotAddress.Save(host, port);
            if (!isActiveAndEnabled || Instance != this)
            {
                return;
            }

            StopWorker();
            StartWorker();
        }

        /// <summary>Raised on the main thread when the link comes up (true) or goes down (false).</summary>
        public event Action<bool> OnConnectionChanged;

        /// <summary>Raised on the main thread for every received message except ping/pong: message type and the full JSON line.</summary>
        public event Action<string, string> OnMessage;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnEnable()
        {
            if (Instance == this)
            {
                StartWorker();
            }
        }

        private void OnDisable()
        {
            StopWorker();
        }

        private void OnApplicationQuit()
        {
            StopWorker();
        }

        private void OnDestroy()
        {
            StopWorker();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Update()
        {
            while (mainThreadQueue.TryDequeue(out Action action))
            {
                action();
            }
        }

        /// <summary>Sends a message (serialized with JsonUtility) as one line. Thread-safe. Returns false if the link is down.</summary>
        public bool Send(object message)
        {
            if (message == null)
            {
                return false;
            }

            string json = message as string ?? JsonUtility.ToJson(message);
            return SendRaw(json);
        }

        /// <summary>Sends an already serialized JSON object as one line. Thread-safe. Returns false if the link is down.</summary>
        public bool SendRaw(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return false;
            }

            // The protocol is line based: a newline inside the message would split it.
            string line = json.Replace("\r", string.Empty).Replace("\n", string.Empty) + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            lock (writeLock)
            {
                if (activeStream == null)
                {
                    return false;
                }

                try
                {
                    activeStream.Write(bytes, 0, bytes.Length);
                    return true;
                }
                catch (Exception)
                {
                    // the worker notices the dead socket on its next read and reconnects
                    CloseActiveLocked();
                    return false;
                }
            }
        }

        // ------------------------------------------------------------------------------------------------------

        private void StartWorker()
        {
            if (running)
            {
                return;
            }

            RobotLinkConfig settings = config;
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<RobotLinkConfig>();
                Debug.LogWarning("[Robot] No RobotLinkConfig assigned, using defaults (127.0.0.1:5005).");
            }

            // copy the values: ScriptableObjects must not be touched from another thread
            string host = settings.host;
            int port = settings.port;
            if (RobotAddress.HasSaved)
            {
                host = RobotAddress.SavedHost; // edited in the headset: overrides the asset
                port = RobotAddress.SavedPort;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (host.StartsWith("127.") || host == "localhost")
            {
                Debug.LogWarning("[Robot] The robot address is " + host + ", which is the headset itself. Type the PC's LAN IP in Settings (or set it in RobotLinkConfig before building).");
            }
#endif
            Host = host;
            Port = port;
            Debug.Log($"[Robot] Connecting to {host}:{port}");
            float reconnect = settings.reconnectInterval;
            float heartbeat = settings.heartbeatInterval;
            float timeout = settings.timeout;

            running = true;
            worker = new Thread(() => Run(host, port, reconnect, heartbeat, timeout)) { IsBackground = true, Name = "RobotLink" };
            worker.Start();
        }

        private void StopWorker()
        {
            if (!running && worker == null)
            {
                return;
            }

            running = false;
            lock (writeLock)
            {
                CloseActiveLocked(); // unblocks a pending read or connect
            }

            Thread thread = worker;
            worker = null;
            if (thread != null && thread.IsAlive)
            {
                thread.Join(1500);
            }

            if (connected)
            {
                connected = false;
                OnConnectionChanged?.Invoke(false);
            }
        }

        private void CloseActiveLocked()
        {
            try { activeStream?.Close(); } catch (Exception) { }
            try { activeClient?.Close(); } catch (Exception) { }
            activeStream = null;
            activeClient = null;
        }

        private void RaiseConnection(bool up)
        {
            mainThreadQueue.Enqueue(() =>
            {
                if (connected == up)
                {
                    return;
                }

                connected = up;
                Debug.Log(up ? "[Robot] Connected." : "[Robot] Disconnected.");
                OnConnectionChanged?.Invoke(up);
            });
        }

        private void Dispatch(string line)
        {
            mainThreadQueue.Enqueue(() =>
            {
                string type;
                try
                {
                    type = JsonUtility.FromJson<TypeProbe>(line)?.type;
                }
                catch (ArgumentException)
                {
                    Debug.LogWarning("[Robot] Ignored line that is not valid JSON: " + line);
                    return;
                }

                if (string.IsNullOrEmpty(type))
                {
                    Debug.LogWarning("[Robot] Ignored message without a \"type\": " + line);
                    return;
                }

                if (type == "pong")
                {
                    return; // only proves the link is alive (the worker already noted the traffic)
                }

                if (type == "ping")
                {
                    SendRaw(PongJson);
                    return;
                }

                OnMessage?.Invoke(type, line);
            });
        }

        // ---- background thread -------------------------------------------------------------------------------

        private void Run(string host, int port, float reconnectSeconds, float heartbeatSeconds, float timeoutSeconds)
        {
            while (running)
            {
                bool wasConnected = false;
                TcpClient client = null;
                try
                {
                    client = new TcpClient { NoDelay = true };
                    lock (writeLock)
                    {
                        activeClient = client;
                    }

                    var connect = client.ConnectAsync(host, port);
                    if (!connect.Wait(TimeSpan.FromSeconds(Mathf.Max(1f, timeoutSeconds))) || !client.Connected)
                    {
                        throw new TimeoutException("connect timed out");
                    }

                    NetworkStream stream = client.GetStream();
                    stream.WriteTimeout = 1500;
                    lock (writeLock)
                    {
                        if (!running)
                        {
                            break;
                        }

                        activeStream = stream;
                    }

                    wasConnected = true;
                    RaiseConnection(true);
                    ReadLoop(client, stream, heartbeatSeconds, timeoutSeconds);
                }
                catch (Exception)
                {
                    // refused / unreachable / timed out / reset: fall through to the reconnect wait
                }
                finally
                {
                    lock (writeLock)
                    {
                        if (ReferenceEquals(activeClient, client))
                        {
                            CloseActiveLocked();
                        }
                    }

                    try { client?.Close(); } catch (Exception) { }
                    if (wasConnected)
                    {
                        RaiseConnection(false);
                    }
                }

                var wait = Stopwatch.StartNew();
                while (running && wait.Elapsed.TotalSeconds < reconnectSeconds)
                {
                    Thread.Sleep(50);
                }
            }
        }

        private void ReadLoop(TcpClient client, NetworkStream stream, float heartbeatSeconds, float timeoutSeconds)
        {
            var clock = Stopwatch.StartNew();
            double lastReceive = 0;
            double lastPing = -heartbeatSeconds; // ping right away
            var buffer = new byte[4096];
            var pending = new StringBuilder();

            while (running)
            {
                double now = clock.Elapsed.TotalSeconds;
                if (now - lastPing >= heartbeatSeconds)
                {
                    if (!SendRaw(PingJson))
                    {
                        return;
                    }

                    lastPing = now;
                }

                if (now - lastReceive > timeoutSeconds)
                {
                    mainThreadQueue.Enqueue(() => Debug.LogWarning("[Robot] No data for " + timeoutSeconds + " s: link considered lost."));
                    return;
                }

                if (!client.Client.Poll(100000, SelectMode.SelectRead))
                {
                    continue; // nothing to read for 100 ms
                }

                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return; // the robot closed the connection
                }

                lastReceive = clock.Elapsed.TotalSeconds;
                pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
                ExtractLines(pending);
            }
        }

        private void ExtractLines(StringBuilder pending)
        {
            while (true)
            {
                int newline = -1;
                for (int i = 0; i < pending.Length; i++)
                {
                    if (pending[i] == '\n')
                    {
                        newline = i;
                        break;
                    }
                }

                if (newline < 0)
                {
                    if (pending.Length > 1 << 20)
                    {
                        pending.Clear(); // a line this long is garbage: do not grow without bound
                    }

                    return;
                }

                string line = pending.ToString(0, newline).Trim();
                pending.Remove(0, newline + 1);
                if (line.Length > 0)
                {
                    Dispatch(line);
                }
            }
        }
    }
}
