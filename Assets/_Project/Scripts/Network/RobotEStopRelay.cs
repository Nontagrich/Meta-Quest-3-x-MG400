using System;
using UnityEngine;

namespace GemStore.Network
{
    /// <summary>
    /// E-STOP for scenes without the teleop controller (the storefront staff panel): sends {"type":"estop"} / {"type":"estop_reset"}
    /// to the robot over <see cref="RobotLink"/>. While locked the estop is sent again after every reconnect, so the robot
    /// always learns about it (the robot server rejects orders until the reset arrives).
    /// In the Maintenance scene the TeleopController does the same for its own E-STOP button.
    /// </summary>
    [DisallowMultipleComponent]
    public class RobotEStopRelay : MonoBehaviour
    {
        private const string EStopJson = "{\"type\":\"estop\"}";
        private const string ResetJson = "{\"type\":\"estop_reset\"}";

        private RobotLink link;

        public static RobotEStopRelay Instance { get; private set; }

        /// <summary>True from <see cref="Trigger"/> until <see cref="ResetLock"/>.</summary>
        public bool Locked { get; private set; }

        public event Action OnStateChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
        }

        private void OnEnable()
        {
            Attach();
        }

        private void OnDisable()
        {
            if (link != null)
            {
                link.OnConnectionChanged -= HandleConnection;
                link = null;
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Attach()
        {
            if (link != null || RobotLink.Instance == null)
            {
                return;
            }

            link = RobotLink.Instance;
            link.OnConnectionChanged += HandleConnection;
        }

        /// <summary>Emergency stop: the robot stops (normal stop first) and refuses new orders until <see cref="ResetLock"/>.</summary>
        public void Trigger(string source = "button")
        {
            Attach();
            bool wasLocked = Locked;
            Locked = true;
            bool sent = link != null && link.SendRaw(EStopJson); // sent again even if locked: never assume the first one arrived
            if (!wasLocked || !sent)
            {
                Debug.Log("[Robot] E-STOP (" + source + ")" + (sent ? string.Empty : " - NOT sent: the robot is not connected, it is sent again on reconnect"));
            }

            OnStateChanged?.Invoke();
        }

        /// <summary>Releases the E-STOP lock and tells the robot (it accepts orders again).</summary>
        public void ResetLock()
        {
            if (!Locked)
            {
                return;
            }

            Locked = false;
            Attach();
            link?.SendRaw(ResetJson);
            Debug.Log("[Robot] E-STOP reset.");
            OnStateChanged?.Invoke();
        }

        private void HandleConnection(bool connected)
        {
            if (connected && Locked)
            {
                link?.SendRaw(EStopJson); // the robot must know about the lock after a reconnect too
            }
        }
    }
}
