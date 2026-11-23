using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;

namespace GemStore.Showcase
{
    /// <summary>
    /// Storefront only. The real direction the user faces when the app starts is not the scene's +Z, so once the head pose is valid this
    /// turns and moves the XR Origin around the camera until the user stands on the standing spot (the rug) and faces the showcase (+Z).
    /// Only yaw and the horizontal position change: the floor height (Floor tracking) is kept. It runs again after a system recenter
    /// (long-press Meta button: XRInputSubsystem.trackingOriginUpdated). Recenter() can be called from code / a UI button.
    /// Do not add this to the Maintenance scene: moving the origin there would move the robot calibration with it.
    /// </summary>
    [DisallowMultipleComponent]
    public class StoreRecenter : MonoBehaviour
    {
        [SerializeField, Tooltip("Found automatically if empty.")] private XROrigin xrOrigin;
        [SerializeField, Tooltip("Where the user should stand (the rug). Its position is used, not its rotation.")]
        private Transform standingSpot;
        [SerializeField, Tooltip("The user faces this direction after recentering (the showcase cabinet is on +Z).")]
        private Vector3 faceDirection = Vector3.forward;
        [SerializeField, Min(0), Tooltip("Frames to wait after the head pose becomes valid (the first poses can still jump).")]
        private int settleFrames = 5;
        [SerializeField, Min(0f), Tooltip("Recenter anyway after this many seconds if the head never reports a valid pose.")]
        private float maxWaitSeconds = 3f;
        [SerializeField] private bool recenterOnStart = true;
        [SerializeField] private bool recenterOnSystemRecenter = true;

        private readonly List<XRInputSubsystem> subsystems = new List<XRInputSubsystem>();
        private Coroutine pending;

        public static StoreRecenter Instance { get; private set; }

        /// <summary>How many times the origin was recentered (for tests and the debug log).</summary>
        public int RecenterCount { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        private void Awake()
        {
            Instance = this;
            if (xrOrigin == null)
            {
                xrOrigin = FindAnyObjectByType<XROrigin>();
            }
        }

        private void OnEnable()
        {
            if (recenterOnSystemRecenter)
            {
                SubsystemManager.GetSubsystems(subsystems);
                foreach (XRInputSubsystem subsystem in subsystems)
                {
                    subsystem.trackingOriginUpdated += OnTrackingOriginUpdated;
                }
            }
        }

        private void OnDisable()
        {
            foreach (XRInputSubsystem subsystem in subsystems)
            {
                if (subsystem != null)
                {
                    subsystem.trackingOriginUpdated -= OnTrackingOriginUpdated;
                }
            }

            subsystems.Clear();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Start()
        {
            if (recenterOnStart)
            {
                RecenterWhenReady();
            }
        }

        private void OnTrackingOriginUpdated(XRInputSubsystem subsystem)
        {
            RecenterWhenReady(); // the system moved the tracking origin: the poses of the next frames are the new ones
        }

        /// <summary>Recenters at once (for a UI button or code).</summary>
        public void Recenter()
        {
            if (pending != null)
            {
                StopCoroutine(pending);
                pending = null;
            }

            Apply();
        }

        /// <summary>Waits for a valid, settled head pose, then recenters.</summary>
        public void RecenterWhenReady()
        {
            if (pending != null)
            {
                StopCoroutine(pending);
            }

            pending = StartCoroutine(WaitAndRecenter());
        }

        private IEnumerator WaitAndRecenter()
        {
            float start = Time.realtimeSinceStartup;
            while (!HeadPoseValid() && Time.realtimeSinceStartup - start < maxWaitSeconds)
            {
                yield return null;
            }

            for (int i = 0; i < settleFrames; i++)
            {
                yield return null;
            }

            pending = null;
            Apply();
        }

        private static bool HeadPoseValid()
        {
            // Input System HMD (OpenXR on Quest, and the XR Interaction Simulator's simulated HMD)
            var hmd = UnityEngine.InputSystem.InputSystem.GetDevice<UnityEngine.InputSystem.XR.XRHMD>();
            if (hmd != null && hmd.added && hmd.isTracked.isPressed)
            {
                return true;
            }

            // legacy XR input device
            InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!head.isValid)
            {
                return false;
            }

            if (head.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && !tracked)
            {
                return false;
            }

            return !head.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState state) || (state & InputTrackingState.Rotation) != 0;
        }

        /// <summary>Turns (yaw) and moves the origin around the camera so the camera stands over the spot looking along faceDirection.</summary>
        public bool Apply()
        {
            if (xrOrigin == null || xrOrigin.Camera == null)
            {
                Debug.LogWarning("[Store] Recenter skipped: no XR Origin / camera.");
                return false;
            }

            Transform origin = xrOrigin.Origin != null ? xrOrigin.Origin.transform : xrOrigin.transform;
            Transform cam = xrOrigin.Camera.transform;
            Vector3 up = origin.up;

            Vector3 currentForward = Vector3.ProjectOnPlane(cam.forward, up);
            if (currentForward.sqrMagnitude < 1e-4f)
            {
                currentForward = Vector3.ProjectOnPlane(cam.up, up); // looking straight down / up: use the top of the head
            }

            Vector3 wantedForward = Vector3.ProjectOnPlane(faceDirection, up);
            if (currentForward.sqrMagnitude < 1e-6f || wantedForward.sqrMagnitude < 1e-6f)
            {
                return false;
            }

            float angle = Vector3.SignedAngle(currentForward, wantedForward, up);
            origin.RotateAround(cam.position, up, angle);

            if (standingSpot != null)
            {
                Vector3 offset = standingSpot.position - cam.position;
                offset = Vector3.ProjectOnPlane(offset, up); // horizontal only: the floor height stays as tracked
                origin.position += offset;
            }

            RecenterCount++;
            Debug.Log($"[Store] Recentered: turned {angle:F0}°, user on the standing spot facing the showcase.");
            return true;
        }
    }
}
