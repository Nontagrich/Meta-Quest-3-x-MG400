using UnityEngine;

namespace GemStore.Network
{
    /// <summary>Address and timing of the TCP link to the robot arm. Unity is the TCP client, the robot controller is the server.</summary>
    [CreateAssetMenu(fileName = "RobotLinkConfig", menuName = "GemStore/Robot Link Config", order = 2)]
    public class RobotLinkConfig : ScriptableObject
    {
        [Tooltip("IP of the PC / controller running the robot server. On the Quest use its LAN IP, not 127.0.0.1.")]
        public string host = "127.0.0.1";
        public int port = 5005;
        [Min(0.5f), Tooltip("Seconds between reconnect attempts after the link drops.")]
        public float reconnectInterval = 3f;
        [Min(0.5f), Tooltip("Seconds between {\"type\":\"ping\"} messages.")]
        public float heartbeatInterval = 2f;
        [Min(1f), Tooltip("The link is considered lost when nothing at all was received for this many seconds.")]
        public float timeout = 6f;
    }
}
