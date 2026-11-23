using UnityEngine;

namespace GemStore.Network
{
    /// <summary>Broker address and topic names. Edit the asset in Assets/_Project/Data to point at the team's broker.</summary>
    [CreateAssetMenu(fileName = "MqttSettings", menuName = "GemStore/MQTT Settings", order = 1)]
    public class MqttSettings : ScriptableObject
    {
        [Header("Broker")]
        [Tooltip("Broker host name or IP. On the Quest this must be the LAN IP of the PC running the broker, not 'localhost'.")]
        public string brokerHost = "127.0.0.1";
        public int brokerPort = 1883;
        public bool useTls;
        public string username;
        public string password;

        [Header("Topics")]
        [Tooltip("Every topic starts with this prefix, for example 'gemstore/order/new'.")]
        public string topicPrefix = "gemstore";

        [Header("Connection")]
        public bool autoConnect = true;
        [Tooltip("Client id is this prefix plus a random suffix, so two headsets never kick each other off the broker.")]
        public string clientIdPrefix = "gemstore-app";
        [Min(5)] public int keepAliveSeconds = 30;
        [Min(1)] public int connectTimeoutSeconds = 5;
        [Min(1)] public int maxReconnectDelaySeconds = 30;
        [Tooltip("Messages published while disconnected are kept (up to this many) and sent after reconnecting.")]
        [Min(0)] public int maxPendingMessages = 50;
    }
}
