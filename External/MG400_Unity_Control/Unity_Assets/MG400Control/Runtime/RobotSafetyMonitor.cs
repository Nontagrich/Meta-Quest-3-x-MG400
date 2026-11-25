using System;
using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;

namespace CPE494
{
    /// <summary>
    /// Robot safety layer (project requirement #2): E-Stop, speed cap,
    /// safety-zone pause for teleop, connection watchdog logging.
    /// </summary>
    public class RobotSafetyMonitor : MonoBehaviour
    {
        public static RobotSafetyMonitor Instance { get; private set; }

        [Header("Safety zone (no teleop while an XR interactor is inside)")]
        public Collider safetyZone;

        [Header("UI (optional)")]
        public TMP_Text statusText;

        public bool EStopped { get; private set; }
        public event Action<bool> OnEStopChanged;

        private float _nextWatchdogLog;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            // keyboard E-Stop for desktop testing
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.f9Key.wasPressedThisFrame) TriggerEStop();
                if (kb.f10Key.wasPressedThisFrame) ResetStop();
            }

            if (statusText != null && Time.time >= _nextWatchdogLog)
            {
                _nextWatchdogLog = Time.time + 0.5f;
                var c = MG400Controller.Instance;
                var b = MG400BridgeClient.Instance;
                string mode = c == null ? "-" : (c.UsingBridge
                    ? (b != null && b.SimMode ? "BRIDGE-SIM" : "BRIDGE-REAL")
                    : "LOCAL-SIM");
                statusText.text = $"MG400 [{mode}]" + (EStopped ? "  E-STOP!" : "");
            }
        }

        /// <summary>Emergency stop: halt everything immediately.</summary>
        public void TriggerEStop()
        {
            if (EStopped) return;
            EStopped = true;
            MG400Controller.Instance?.EStop();
            Debug.LogWarning("[Safety] E-STOP triggered");
            OnEStopChanged?.Invoke(true);
        }

        public void ResetStop()
        {
            if (!EStopped) return;
            EStopped = false;
            MG400Controller.Instance?.ResetFromStop();
            Debug.Log("[Safety] E-Stop reset");
            OnEStopChanged?.Invoke(false);
        }

        /// <summary>True when the user is inside the robot safety volume.</summary>
        public bool UserInSafetyZone()
        {
            if (safetyZone == null) return false;
            var cam = Camera.main;
            return cam != null && safetyZone.bounds.Contains(cam.transform.position);
        }
    }
}
