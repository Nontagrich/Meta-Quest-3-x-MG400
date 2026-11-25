using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace CPE494
{
    /// <summary>
    /// AR remote-maintenance station (requirement 4.b.i).  The technician
    /// points their hand/controller ray at the teleop pad; the MG400 TCP
    /// follows the pointed position on the robot's work plane (10 pts:
    /// "pick position follows the picker's hand").  Grip/activate turns the
    /// vacuum on to pick fallen gems (5 pts).
    /// </summary>
    public class TeleopStation : MonoBehaviour
    {
        public static TeleopStation Instance { get; private set; }

        [Header("Mapping")]
        public Transform padCenter;              // AR pad the user points at
        public float padHalfExtent = 0.30f;      // metres on the pad
        public float padToWorkspaceScale = 0.65f;
        public float workHeight = 0.05f;         // TCP height above MG400 base (m)
        public float sendInterval = 0.08f;       // ~12 Hz command stream

        [Header("Direct hand tracking (4.b.i - 10 pts)")]
        public bool directHandMode;              // true = TCP follows hand position
        public Transform handSource;             // right controller / hand joint
        public KeyCode toggleDirectKey = KeyCode.T;   // desktop toggle
        public float directRange = 0.45f;        // metres of hand travel mapped

        [Header("Vacuum")]
        public string vacuumActivateName = "";      // (grip/trigger while hovering pad)

        [Header("Visuals")]
        public Transform reticle;                // dot on the pad

        public bool TeleopActive { get; private set; }
        public bool ControllerTrackingLive => _liveTracking;
        public bool PreparingControllerPose => _liveTracking && !_trackingArmed;
        public bool ControllerPausedForFocus { get; private set; }
        public string TrackingStopReason { get; private set; }
        public Vector2 ControllerOffsetMm { get; private set; }
        public bool ControllerAtWorkspaceEdge { get; private set; }

        private XRRayInteractor _ray;
        private XRSimpleInteractable _padInteractable;
        private MG400Controller _controller;
        private MG400DigitalTwin _twin;
        private RobotSafetyMonitor _safety;
        private float _nextSend;
        private Vector3 _lastAim;
        private bool _activateHeld;
        private MG400BridgeClient _trackingBridge;
        private TrackedPoseDriver _handPoseDriver;
        private InputDevice _trackedControllerDevice;
        private Vector3 _handOrigin;
        private bool _liveTracking;
        private bool _trackingArmed;
        private int _trackingRequest;
        private float _trackingDeadline;
        private float _nextTrackingSend;
        private bool _reportedPreparation;
        private float _focusLostAt = -1f;
        private const float PreparationFocusGrace = 0.5f;
        private const string PreparingPoseMessage = "Preparing controller start pose";

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void OnDisable()
        {
            EndLiveTracking();
            directHandMode = false;
            _focusLostAt = -1f;
            ControllerPausedForFocus = false;
            TrackingStopReason = null;
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
            {
                if (_focusLostAt < 0f) _focusLostAt = Time.realtimeSinceStartup;
                // Live hand input must always be re-anchored after focus loss.
                if (_liveTracking && _trackingArmed) PauseTrackingForFocus();
                return;
            }

            if (_liveTracking && PreparationFocusGraceExpired()) PauseTrackingForFocus();
            _focusLostAt = -1f;
        }

        private bool PreparationFocusGraceExpired() => _focusLostAt >= 0f
            && Time.realtimeSinceStartup - _focusLostAt >= PreparationFocusGrace;

        private void PauseTrackingForFocus()
        {
            ControllerPausedForFocus = true;
            StopTracking("Unity lost focus. Click Game and press T to continue from the current pose.");
        }

        private void Start()
        {
            _controller = MG400Controller.Instance;
            _twin = MG400DigitalTwin.Instance;
            _safety = RobotSafetyMonitor.Instance;
            if (reticle != null) reticle.gameObject.SetActive(false);

            // auto-wire the first XR ray interactor (right hand/controller)
            if (_ray == null)
            {
                var found = FindAnyObjectByType<XRRayInteractor>();
                if (found != null) SetRayInteractor(found);
            }
            // auto-wire the pad interactable for grip -> vacuum
            if (_padInteractable == null)
            {
                var pad = GetComponent<XRSimpleInteractable>();
                if (pad != null) SetPadInteractable(pad);
            }
        }

        private void Update()
        {
            // Require a valid aim on every frame. Losing input must never
            // keep sending the last target.
            TeleopActive = false;
            if (_liveTracking && PreparationFocusGraceExpired()) PauseTrackingForFocus();
            var kb = Keyboard.current;
            if (_focusLostAt < 0f && kb != null && kb.tKey.wasPressedThisFrame)
            {
                if (directHandMode)
                {
                    EndLiveTracking();
                    directHandMode = false;
                }
                else
                {
                    StartDirectTracking();
                }
            }

            if (_liveTracking)
            {
                UpdateLiveTracking();
            }
            else if (directHandMode)
            {
                if (CanPreviewTracking()) AimFromHand();
                else StopTracking("Real bridge connected. Press T to start tracking.");
            }
            else
            {
                AimFromRay();
                if (_ray == null || !_ray.isActiveAndEnabled)
                    AimFromMouse();                      // desktop preview fallback
            }

            HandleVacuum();

            if (!_liveTracking && TeleopActive && Time.time >= _nextSend && CanPreviewTracking())
            {
                _nextSend = Time.time + sendInterval;
                SendTarget(_lastAim);
            }
        }

        private static bool CanPreviewTracking()
        {
            var bridge = MG400BridgeClient.Instance;
            return bridge == null || !bridge.IsConnected
                || (bridge.StateFresh && bridge.LastState != null && bridge.LastState.sim);
        }

        private bool TrackingInputReady() => handSource != null && handSource.gameObject.activeInHierarchy
            && padCenter != null && _controller != null && !_controller.Halted
            && (_safety == null || (!_safety.EStopped && !_safety.UserInSafetyZone()));

        private bool TryReadTrackedHand(out Vector3 position, out InputDevice device)
        {
            position = default;
            device = null;
            if (_handPoseDriver == null || !_handPoseDriver.isActiveAndEnabled) return false;
            var positionAction = _handPoseDriver.positionInput.action;
            var trackingAction = _handPoseDriver.trackingStateInput.action;
            if (positionAction == null || !positionAction.enabled || trackingAction == null || !trackingAction.enabled
                || (trackingAction.ReadValue<int>() & (int)UnityEngine.XR.InputTrackingState.Position) == 0)
                return false;

            device = trackingAction.activeControl?.device;
            var positionControl = positionAction.activeControl;
            if (device == null || (positionControl != null && positionControl.device != device)) return false;
            position = positionAction.ReadValue<Vector3>();
            float squaredLength = position.sqrMagnitude;
            return !float.IsNaN(squaredLength) && !float.IsInfinity(squaredLength);
        }

        private void StartDirectTracking()
        {
            ControllerPausedForFocus = false;
            TrackingStopReason = null;
            if (!TrackingInputReady())
            {
                StopTracking("Controller input or safety state is not ready.");
                return;
            }

            directHandMode = true;
            if (CanPreviewTracking()) return;

            _trackingBridge = MG400BridgeClient.Instance;
            if (!_trackingBridge.StateFresh || _trackingBridge.LastState == null
                || _trackingBridge.LastState.sim)
            {
                StopTracking("Waiting for real bridge status. Press T to try again.");
                return;
            }

            _handPoseDriver = handSource.GetComponent<TrackedPoseDriver>();
            if (!TryReadTrackedHand(out _, out _trackedControllerDevice))
            {
                StopTracking("Right Controller position is not tracked. Select or reconnect it, then press T.");
                return;
            }

            _liveTracking = true;
            _trackingBridge.OnAck += OnTrackingAck;
            _trackingBridge.OnConnectionChanged += OnTrackingConnectionChanged;
            SendTrackingRequest("teleop_xy_begin");
        }

        private void UpdateLiveTracking()
        {
            if (!directHandMode || !TrackingInputReady() || !_trackingBridge.IsConnected
                || !_trackingBridge.StateFresh || _trackingBridge.SimMode)
            {
                StopTracking("Controller tracking interrupted. Press T to start again.");
                return;
            }
            // A short Editor focus transition pauses preparation traffic.
            // A sustained transition ends the session and requires T again.
            if (_focusLostAt >= 0f) return;
            if (!TryReadTrackedHand(out Vector3 handPosition, out var device) || device != _trackedControllerDevice)
            {
                StopTracking("Right Controller tracking was lost or changed. Press T to re-anchor.");
                return;
            }
            if (_trackingRequest != 0)
            {
                if (Time.unscaledTime >= _trackingDeadline)
                    StopTracking("Bridge did not answer. Press T to start again.");
                return;
            }
            if (Time.unscaledTime < _nextTrackingSend) return;
            _nextTrackingSend = Time.unscaledTime + Mathf.Max(0.1f, sendInterval);
            if (!_trackingArmed)
            {
                SendTrackingRequest("teleop_xy_prepare");
                return;
            }

            // Tracking-space W/S (forward/back) maps to Dobot X. A/D
            // (left/right) maps to Dobot Y. Ignore height and rig motion.
            Vector3 offset = handPosition - _handOrigin;
            float scale = padToWorkspaceScale * 1000f;
            Vector2 commandOffset = new Vector2(Mathf.Clamp(offset.z, -directRange, directRange) * scale,
                                                Mathf.Clamp(offset.x, -directRange, directRange) * scale);
            ControllerOffsetMm = commandOffset;
            SendTrackingRequest("teleop_xy_step", c => { c.x = commandOffset.x; c.y = commandOffset.y; });
            TeleopActive = true;
        }

        private void SendTrackingRequest(string command, System.Action<MG400BridgeClient.OutCmd> setup = null)
        {
            _trackingRequest = _trackingBridge.NextId();
            _trackingDeadline = Time.unscaledTime + 4f;
            _trackingBridge.Send(_trackingRequest, command, setup);
        }

        private void OnTrackingAck(MG400Ack ack)
        {
            if (ack.id != _trackingRequest) return;
            _trackingRequest = 0;
            if (!ack.ok || !TrackingInputReady())
            {
                StopTracking(ack.ok ? "Controller input is no longer ready." : ack.msg);
                return;
            }
            if (_trackingArmed)
            {
                ControllerAtWorkspaceEdge = ack.msg == "Workspace boundary reached";
                return;
            }
            if (ack.msg == PreparingPoseMessage)
            {
                if (!_reportedPreparation)
                    Debug.Log("[Teleop] Preparing XY and R for controller tracking at the current Z. Press T again to cancel.");
                _reportedPreparation = true;
                return;
            }
            if (_focusLostAt >= 0f)
            {
                PauseTrackingForFocus();
                return;
            }
            if (!TryReadTrackedHand(out _handOrigin, out var device) || device != _trackedControllerDevice)
            {
                StopTracking("Right Controller unavailable at start pose. Press T to try again.");
                return;
            }
            _trackingArmed = true;
            _nextTrackingSend = Time.unscaledTime;
            Debug.Log($"[Teleop] {ack.msg}");
        }

        private void OnTrackingConnectionChanged(bool connected)
        {
            if (!connected) StopTracking("Bridge disconnected. Press T to start again.");
        }

        private void StopTracking(string reason)
        {
            EndLiveTracking();
            directHandMode = false;
            TrackingStopReason = reason;
            Debug.LogWarning($"[Teleop] {reason}");
        }

        private void EndLiveTracking()
        {
            if (_trackingBridge != null)
            {
                _trackingBridge.OnAck -= OnTrackingAck;
                _trackingBridge.OnConnectionChanged -= OnTrackingConnectionChanged;
                if (_trackingBridge.IsConnected)
                    _trackingBridge.Send("teleop_xy_end");
            }
            _trackingBridge = null;
            _handPoseDriver = null;
            _trackedControllerDevice = null;
            _trackingRequest = 0;
            _trackingArmed = false;
            _reportedPreparation = false;
            _liveTracking = false;
            ControllerOffsetMm = Vector2.zero;
            ControllerAtWorkspaceEdge = false;
            TeleopActive = false;
        }

        /// <summary>
        /// Direct hand-position tracking (10 pts): the TCP mirrors the hand's
        /// offset from the pad centre, so moving your hand in the air moves
        /// the suction cup to the matching spot above the table.
        /// </summary>
        private void AimFromHand()
        {
            if (padCenter == null || handSource == null) return;
            if (_safety != null && (_safety.EStopped || _safety.UserInSafetyZone()))
            {
                TeleopActive = false;
                return;
            }

            Vector3 delta = handSource.position - padCenter.position;
            delta = padCenter.InverseTransformDirection(delta);
            delta.x = Mathf.Clamp(delta.x, -directRange, directRange);
            delta.y = Mathf.Clamp(delta.y, -directRange, directRange);
            delta.z = 0f;                                // keep TCP on the work plane

            // virtual aim point on the pad plane (reuses the pad mapping)
            Vector3 aimPoint = padCenter.TransformPoint(
                new Vector3(delta.x, delta.y, 0.02f));
            AimAt(aimPoint);
        }

        // ------------------------------------------------------------------
        // aiming
        // ------------------------------------------------------------------
        private void AimFromRay()
        {
            if (_ray == null || !_ray.isActiveAndEnabled) return;
            // physical raycast against the pad collider (works with hands and controllers)
            if (!_ray.TryGetCurrent3DRaycastHit(out RaycastHit hit)) return;
            if (hit.transform != padCenter && !hit.transform.IsChildOf(padCenter)) return;
            AimAt(hit.point);
        }

        private void AimFromMouse()
        {
            // desktop fallback: mouse over the pad collider aims the arm
            var mouse = Mouse.current;
            var cam = Camera.main;
            if (mouse == null || cam == null) return;
            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            if (Physics.Raycast(ray, out var hit, 10f) && hit.transform == padCenter)
                AimAt(hit.point);
        }

        private void AimAt(Vector3 worldPoint)
        {
            if (_safety != null && _safety.EStopped) { TeleopActive = false; return; }
            if (_safety != null && _safety.UserInSafetyZone()) { TeleopActive = false; return; }
            if (padCenter == null) return;

            // The pad is a vertical plane: local.x = left/right, local.y = up/down.
            // Map onto the MG400 workspace: x -> lateral, y -> depth (+Z = forward).
            Vector3 local = padCenter.InverseTransformPoint(worldPoint);
            local.x = Mathf.Clamp(local.x, -padHalfExtent, padHalfExtent);
            local.y = Mathf.Clamp(local.y, -padHalfExtent, padHalfExtent);

            // pad point -> MG400 base-frame point (scaled) -> world
            Vector3 padOffset = new Vector3(local.x * padToWorkspaceScale, 0f,
                                            local.y * padToWorkspaceScale);
            _lastAim = _twin.transform.TransformPoint(padOffset);

            TeleopActive = true;
            if (reticle != null && !reticle.gameObject.activeSelf)
                reticle.gameObject.SetActive(true);
            if (reticle != null)
                reticle.position = padCenter.TransformPoint(new Vector3(local.x, local.y, 0.02f));
        }

        private void SendTarget(Vector3 worldTarget)
        {
            if (_twin == null || _controller == null || _controller.Halted) return;
            Vector3 baseFrame = _twin.transform.InverseTransformPoint(worldTarget);
            baseFrame = MG400Geometry.ClampWorkspace(baseFrame);
            baseFrame.y = workHeight;
            Vector3 joints = MG400Geometry.IK(baseFrame);
            _controller.PreviewTeleop(new Vector4(joints.x, joints.y, joints.z, 0f));
        }

        // ------------------------------------------------------------------
        // vacuum
        // ------------------------------------------------------------------
        private void HandleVacuum()
        {
            bool wantVacuum = (_activateHeld)
                || (Keyboard.current != null && Keyboard.current.spaceKey.isPressed);

            if (_twin != null && wantVacuum != _controller.Twin.VacuumOn)
            {
                StartCoroutine(_controller.SetVacuum(wantVacuum));
            }
            if (_twin != null)
                FallenGemSpawner.Instance?.UpdateVacuum(_twin.TcpPosition, wantVacuum);
        }

        /// <summary>
        /// Wire the pad's simple interactable: grip/trigger press while
        /// hovering the pad turns the vacuum on (XRI 3 exposes activate
        /// events on interactables, not interactors).
        /// </summary>
        public void SetRayInteractor(XRRayInteractor ray) => _ray = ray;

        public void SetPadInteractable(XRSimpleInteractable pad)
        {
            if (_padInteractable != null)
            {
                _padInteractable.activated.RemoveListener(OnActivate);
                _padInteractable.deactivated.RemoveListener(OnDeactivate);
            }
            _padInteractable = pad;
            if (_padInteractable != null)
            {
                _padInteractable.activated.AddListener(OnActivate);
                _padInteractable.deactivated.AddListener(OnDeactivate);
            }
        }

        private void OnActivate(ActivateEventArgs args) => _activateHeld = true;
        private void OnDeactivate(DeactivateEventArgs args) => _activateHeld = false;
    }
}
