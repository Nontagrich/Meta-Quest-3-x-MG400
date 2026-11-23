using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using UnityEngine;

namespace GemStore.Network
{
    public enum MqttConnectionState
    {
        Disconnected,
        Connecting,
        Connected
    }

    /// <summary>
    /// The app's single MQTT connection (MQTTnet 4.3). Connects in the background, reconnects with a growing delay,
    /// re-subscribes after a reconnect, keeps messages that were published while offline, and delivers incoming
    /// messages on Unity's main thread.
    /// </summary>
    [DisallowMultipleComponent]
    public class MqttService : MonoBehaviour
    {
        private sealed class PendingMessage
        {
            public string Topic;
            public string Payload;
            public MqttQualityOfServiceLevel Qos;
            public bool Retain;
        }

        private static MqttService instance;

        [SerializeField] private MqttSettings settings;

        private readonly ConcurrentQueue<Action> mainThreadQueue = new ConcurrentQueue<Action>();
        private readonly ConcurrentQueue<PendingMessage> pending = new ConcurrentQueue<PendingMessage>();
        private readonly Dictionary<string, MqttQualityOfServiceLevel> subscriptions = new Dictionary<string, MqttQualityOfServiceLevel>();
        private readonly object subscriptionLock = new object();

        private IMqttClient client;
        private CancellationTokenSource cancellation;
        private string hostOverride;
        private int portOverride;
        private volatile int connectionState;

        /// <summary>The service in the scene, or null if there is none.</summary>
        public static MqttService Instance => instance;

        public MqttConnectionState State => (MqttConnectionState)connectionState;
        public bool IsConnected => State == MqttConnectionState.Connected;
        public string TopicPrefix => settings != null ? settings.topicPrefix : "gemstore";

        /// <summary>Raised on the main thread after a (re)connection.</summary>
        public event Action Connected;

        /// <summary>Raised on the main thread when the connection is lost or closed.</summary>
        public event Action Disconnected;

        /// <summary>Raised on the main thread for each received message: topic and UTF-8 payload.</summary>
        public event Action<string, string> MessageReceived;

        public string Topic(string suffix)
        {
            return $"{TopicPrefix}/{suffix}";
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            instance = null;
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(this);
                return;
            }

            instance = this;
        }

        private void Start()
        {
            if (instance == this && settings != null && settings.autoConnect)
            {
                Connect();
            }
        }

        private void Update()
        {
            while (mainThreadQueue.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
                Shutdown();
            }
        }

        /// <summary>Uses another broker address instead of the settings asset (nothing is written to the asset).</summary>
        public void OverrideEndpoint(string host, int port)
        {
            hostOverride = host;
            portOverride = port;
        }

        /// <summary>Starts connecting in the background. Safe to call again: it restarts the connection.</summary>
        public void Connect()
        {
            if (settings == null)
            {
                Debug.LogWarning("[MQTT] No MqttSettings assigned on the MqttService.", this);
                return;
            }

            Shutdown();
            cancellation = new CancellationTokenSource();
            client = new MqttFactory().CreateMqttClient();
            client.ApplicationMessageReceivedAsync += OnApplicationMessageReceived;
            _ = RunConnectionLoop(client, cancellation.Token);
        }

        public void Disconnect()
        {
            Shutdown();
        }

        /// <summary>Subscribes now if connected, and again after every reconnect.</summary>
        public void Subscribe(string topic, int qos = 1)
        {
            MqttQualityOfServiceLevel level = ToLevel(qos);
            lock (subscriptionLock)
            {
                subscriptions[topic] = level;
            }

            IMqttClient current = client;
            if (current != null && current.IsConnected)
            {
                _ = SubscribeOne(current, topic, level);
            }
        }

        /// <summary>Publishes a UTF-8 text payload. If offline the message is kept and sent after reconnecting.</summary>
        public void Publish(string topic, string payload, int qos = 1, bool retain = false)
        {
            var message = new PendingMessage { Topic = topic, Payload = payload, Qos = ToLevel(qos), Retain = retain };
            IMqttClient current = client;
            if (current != null && current.IsConnected)
            {
                _ = PublishNow(current, message);
            }
            else
            {
                Enqueue(message);
            }
        }

        private void Enqueue(PendingMessage message)
        {
            pending.Enqueue(message);
            int limit = settings != null ? settings.maxPendingMessages : 50;
            while (pending.Count > limit && pending.TryDequeue(out PendingMessage dropped))
            {
                RunOnMainThread(() => Debug.LogWarning($"[MQTT] Offline queue full, dropped message for '{dropped.Topic}'."));
            }

            RunOnMainThread(() => Debug.Log($"[MQTT] Not connected: message for '{message.Topic}' is queued and will be sent after reconnecting."));
        }

        private async Task RunConnectionLoop(IMqttClient loopClient, CancellationToken token)
        {
            int attempt = 0;
            string host = hostOverride ?? settings.brokerHost;
            int port = hostOverride != null ? portOverride : settings.brokerPort;

            MqttClientOptions options = BuildOptions(host, port);
            Interlocked.Exchange(ref connectionState, (int)MqttConnectionState.Connecting);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(settings.connectTimeoutSeconds));
                        await loopClient.ConnectAsync(options, timeout.Token).ConfigureAwait(false);
                    }

                    attempt = 0;
                    Interlocked.Exchange(ref connectionState, (int)MqttConnectionState.Connected);
                    RunOnMainThread(() =>
                    {
                        Debug.Log($"[MQTT] Connected to {host}:{port} (topic prefix '{TopicPrefix}').");
                        Connected?.Invoke();
                    });

                    await SubscribeAll(loopClient).ConfigureAwait(false);
                    await FlushPending(loopClient).ConfigureAwait(false);

                    while (!token.IsCancellationRequested && loopClient.IsConnected)
                    {
                        await Task.Delay(500, token).ConfigureAwait(false);
                    }

                    if (!token.IsCancellationRequested)
                    {
                        Interlocked.Exchange(ref connectionState, (int)MqttConnectionState.Connecting);
                        RunOnMainThread(() =>
                        {
                            Debug.LogWarning("[MQTT] Connection lost. Reconnecting...");
                            Disconnected?.Invoke();
                        });
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    int shownAttempt = attempt + 1;
                    string reason = exception.GetBaseException().Message;
                    RunOnMainThread(() => Debug.LogWarning($"[MQTT] Cannot connect to {host}:{port} (attempt {shownAttempt}): {reason}"));
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                int delaySeconds = Math.Min(settings.maxReconnectDelaySeconds, 1 << Math.Min(attempt + 1, 6));
                attempt++;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            Interlocked.Exchange(ref connectionState, (int)MqttConnectionState.Disconnected);
        }

        private MqttClientOptions BuildOptions(string host, int port)
        {
            string clientId = $"{settings.clientIdPrefix}-{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            MqttClientOptionsBuilder builder = new MqttClientOptionsBuilder()
                .WithTcpServer(host, port)
                .WithClientId(clientId)
                .WithCleanSession()
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(settings.keepAliveSeconds));

            if (!string.IsNullOrEmpty(settings.username))
            {
                builder = builder.WithCredentials(settings.username, settings.password);
            }

            if (settings.useTls)
            {
                builder = builder.WithTlsOptions(tls => tls.UseTls());
            }

            return builder.Build();
        }

        private async Task SubscribeAll(IMqttClient target)
        {
            KeyValuePair<string, MqttQualityOfServiceLevel>[] all;
            lock (subscriptionLock)
            {
                all = new KeyValuePair<string, MqttQualityOfServiceLevel>[subscriptions.Count];
                int i = 0;
                foreach (var pair in subscriptions)
                {
                    all[i++] = pair;
                }
            }

            foreach (var pair in all)
            {
                await SubscribeOne(target, pair.Key, pair.Value).ConfigureAwait(false);
            }
        }

        private async Task SubscribeOne(IMqttClient target, string topic, MqttQualityOfServiceLevel level)
        {
            try
            {
                var filter = new MqttTopicFilterBuilder().WithTopic(topic).WithQualityOfServiceLevel(level).Build();
                await target.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(filter).Build()).ConfigureAwait(false);
                RunOnMainThread(() => Debug.Log($"[MQTT] Subscribed to '{topic}' (QoS {(int)level})."));
            }
            catch (Exception exception)
            {
                string reason = exception.GetBaseException().Message;
                RunOnMainThread(() => Debug.LogWarning($"[MQTT] Subscribe to '{topic}' failed: {reason}"));
            }
        }

        private async Task FlushPending(IMqttClient target)
        {
            while (target.IsConnected && pending.TryDequeue(out PendingMessage message))
            {
                await PublishNow(target, message).ConfigureAwait(false);
            }
        }

        private async Task PublishNow(IMqttClient target, PendingMessage message)
        {
            try
            {
                MqttApplicationMessage applicationMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(message.Topic)
                    .WithPayload(Encoding.UTF8.GetBytes(message.Payload))
                    .WithQualityOfServiceLevel(message.Qos)
                    .WithRetainFlag(message.Retain)
                    .Build();
                await target.PublishAsync(applicationMessage).ConfigureAwait(false);
                RunOnMainThread(() => Debug.Log($"[MQTT] Published to '{message.Topic}' (QoS {(int)message.Qos}): {message.Payload}"));
            }
            catch (Exception exception)
            {
                string reason = exception.GetBaseException().Message;
                RunOnMainThread(() => Debug.LogWarning($"[MQTT] Publish to '{message.Topic}' failed: {reason}"));
                pending.Enqueue(message); // try again after the next reconnect
            }
        }

        private Task OnApplicationMessageReceived(MqttApplicationMessageReceivedEventArgs args)
        {
            string topic = args.ApplicationMessage.Topic;
            ArraySegment<byte> segment = args.ApplicationMessage.PayloadSegment;
            string payload = segment.Array != null ? Encoding.UTF8.GetString(segment.Array, segment.Offset, segment.Count) : string.Empty;
            RunOnMainThread(() => MessageReceived?.Invoke(topic, payload));
            return Task.CompletedTask;
        }

        private void RunOnMainThread(Action action)
        {
            mainThreadQueue.Enqueue(action);
        }

        private void Shutdown()
        {
            CancellationTokenSource oldCancellation = cancellation;
            IMqttClient oldClient = client;
            cancellation = null;
            client = null;
            Interlocked.Exchange(ref connectionState, (int)MqttConnectionState.Disconnected);

            oldCancellation?.Cancel();
            if (oldClient != null)
            {
                oldClient.ApplicationMessageReceivedAsync -= OnApplicationMessageReceived;
                _ = CloseClient(oldClient);
            }
        }

        private static async Task CloseClient(IMqttClient target)
        {
            try
            {
                if (target.IsConnected)
                {
                    await target.DisconnectAsync().ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // closing: nothing useful to do
            }
            finally
            {
                target.Dispose();
            }
        }

        private static MqttQualityOfServiceLevel ToLevel(int qos)
        {
            switch (qos)
            {
                case 0: return MqttQualityOfServiceLevel.AtMostOnce;
                case 2: return MqttQualityOfServiceLevel.ExactlyOnce;
                default: return MqttQualityOfServiceLevel.AtLeastOnce;
            }
        }
    }
}
