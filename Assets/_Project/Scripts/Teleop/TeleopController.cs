using System;
using System.Collections.Generic;
using System.Globalization;
using GemStore.Network;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace GemStore.Teleop
{
    public enum ControlMode
    {
        /// <summary>Safe default: the hand moves inside the control volume, which maps 1:1 to the real workspace elsewhere.</summary>
        Offset,

        /// <summary>The robot goes to the hand's real position (calibrated frame). Capped speed; keep hands clear of the robot.</summary>
        Absolute
    }

    public enum MacroKind
    {
        None,
        Pick,
        Place
    }

    /// <summary>
    /// Hand-follow teleoperation. While the deadman (right grip / pinch) is held, the right hand (or controller tip) is mapped to robot
    /// millimetres, smoothed (One Euro), clamped to the workspace box, speed-limited and sent over <see cref="RobotLink"/>.
    /// Releasing the deadman, losing tracking / the link / focus, or pressing E-STOP stops it at once.
    ///
    /// Modes: OFFSET (default, via the <see cref="ControlVolume"/>) and ABSOLUTE (hand position = robot position, 30 mm/s cap).
    /// Precision: while the left grip / A is held, hand motion is scaled (default 0.3) around where precision started.
    /// Macros: Pick / Place are short sequences of ordinary teleop messages, aborted by E-STOP or by releasing the deadman.
    /// Gem marker: aim the right ray at the dropped gem and press the trigger; the point on the table plane is stored in robot mm.
    ///
    /// Messages (robot frame, mm):
    ///   {"type":"teleop","seq":123,"enable":true,"x":250.0,"y":-30.5,"z":40.0,"r":0.0,"grip":false}
    ///   {"type":"teleop","enable":false}
    ///   {"type":"estop"}  /  {"type":"estop_reset"}
    /// The robot streams {"type":"robot_pose","x":..,"y":..,"z":..,"r":..}, shown as a second marker.
    /// </summary>
    [DisallowMultipleComponent]
    public class TeleopController : MonoBehaviour
    {
        [Serializable]
        private class PoseMessage
        {
            public float x;
            public float y;
            public float z;
            public float r;
        }

        private class MacroStep
        {
            public string label;
            public Vector3 target;
            public bool grip;
            public float wait;
        }

        [SerializeField] private RobotCalibration calibration;
        [SerializeField] private ControlVolume controlVolume;
        [SerializeField, Tooltip("Right Near-Far interactor: its select input (grip / pinch) is the deadman, its activate input (trigger) toggles the gripper and places the gem marker. Found automatically if empty.")]
        private NearFarInteractor rightInteractor;
        [SerializeField, Tooltip("Left Near-Far interactor: left grip (select) = precision mode, left trigger (activate) toggles the gripper. Found automatically if empty.")]
        private NearFarInteractor leftInteractor;
        [SerializeField, Tooltip("If the robot pose has not been received for this long the robot marker is hidden.")]
        private float poseTimeoutSeconds = 2f;
        [SerializeField, Tooltip("Treat a lost controller / hand as an emergency stop of the follow mode.")]
        private bool requireTracking = true;
        [SerializeField, Tooltip("Stop when the editor loses focus too (in builds the app always stops when it loses focus).")]
        private bool stopOnFocusLossInEditor;
        [SerializeField, Tooltip("A Pick / Place request made without the deadman waits this long for the grip to be pressed.")]
        private float macroArmSeconds = 10f;
        [SerializeField, Tooltip("A macro step counts as reached when the robot reports to be this close (mm).")]
        private float macroArriveMm = 3f;
        [SerializeField, Tooltip("A macro step moves on after this long even if the robot never reported arriving.")]
        private float macroStepTimeoutSeconds = 6f;

        private OneEuroFilter filter;
        private InputAction estopAction;
        private InputAction pickAction;
        private InputAction placeAction;
        private InputAction precisionAction;
        private bool needRelease;
        private bool following;
        private bool sentAny;
        private Vector3 lastSent;
        private bool lastSentGrip;
        private float nextSendTime;
        private int seq;
        private float lastPoseTime = -100f;
        private RobotLink link;

        // commanded target: what is really sent. It moves toward the desired target at the speed limit.
        private Vector3 commanded;
        private bool commandedValid;

        // precision mode
        private bool precisionWasOn;
        private Vector3 precisionAnchorHand;
        private Vector3 precisionAnchorEffective;
        private Vector3 lastEffectiveHand;
        private float precisionReleaseTime = -10f;
        private Vector3 precisionReleaseFrom;

        // macros
        private MacroKind pendingMacro;
        private float pendingMacroUntil;
        private List<MacroStep> macroSteps;
        private int macroIndex;
        private float macroReachedAt = -1f;
        private float macroStepStarted;
        private bool macroForceSend;

        // ---- state for HUD / visuals ----------------------------------------------------------------------------
        public ControlMode Mode { get; private set; } = ControlMode.Offset;
        public Vector3 HandRobotMm { get; private set; }
        public Vector3 TargetRobotMm { get; private set; }
        public Vector3 TargetUnity { get; private set; }
        public Vector3 HandUnity { get; private set; }
        public bool Clamped { get; private set; }
        public bool Following => following;
        public bool GripOn { get; private set; }
        public bool EStopLocked { get; private set; }
        public bool DeadmanHeld { get; private set; }
        public bool PrecisionActive { get; private set; }
        public string BlockedReason { get; private set; } = string.Empty;
        public int SentCount { get; private set; }
        public bool HasRobotPose => Time.unscaledTime - lastPoseTime < poseTimeoutSeconds;
        public Vector3 RobotPoseMm { get; private set; }
        public float RobotRotation { get; private set; }
        public bool LinkConnected => link != null && link.IsConnected;
        public bool Calibrated => calibration != null && calibration.IsCalibrated && calibration.State == CalibrationState.Idle;
        public ControlVolume Volume => controlVolume;

        // gem marker
        public bool Marking { get; private set; }
        public bool HasGem { get; private set; }
        public Vector3 GemRobotMm { get; private set; }
        public bool GemOutOfReach { get; private set; }
        public bool MarkPreviewValid { get; private set; }
        public Vector3 MarkPreviewUnity { get; private set; }
        public string MarkMessage { get; private set; } = string.Empty;

        // macro
        public MacroKind PendingMacro => pendingMacro;
        public MacroKind ActiveMacro { get; private set; }
        public bool MacroActive => ActiveMacro != MacroKind.None;
        public string MacroStatus { get; private set; } = string.Empty;

        /// <summary>Hand-motion scale while precision mode is on.</summary>
        public float CurrentPrecisionScale => calibration != null && calibration.Config != null ? calibration.Config.precisionScale : 0.3f;

        /// <summary>The target speed limit in the current mode (mm/s).</summary>
        public float SpeedLimitMmPerSec
        {
            get
            {
                if (calibration == null || calibration.Config == null)
                {
                    return 30f;
                }

                TeleopConfig config = calibration.Config;
                return Mode == ControlMode.Absolute ? Mathf.Min(config.absoluteMaxSpeedMmPerSec, config.maxSpeedMmPerSec) : config.maxSpeedMmPerSec;
            }
        }

        /// <summary>Last text of the {"type":"teleop"...} message sent (for tests).</summary>
        public string LastTeleopJson { get; private set; }

        // ---- test hooks ----------------------------------------------------------------------------------------------
        /// <summary>When set, replaces the grip / pinch read from the interactor (tests, fake input).</summary>
        public Func<bool> DeadmanOverride { get; set; }
        public Func<bool> TrackingOverride { get; set; }
        public Func<bool> PrecisionOverride { get; set; }

        /// <summary>When set, replaces the right ray used to mark a gem (tests, fake input).</summary>
        public Func<Ray> MarkRayOverride { get; set; }

        public static TeleopController Instance { get; private set; }

        public event Action OnStateChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        private void Awake()
        {
            Instance = this;
        }

        private void OnEnable()
        {
            // B (right controller secondary) = E-STOP, X / Y (left) = Pick / Place, A (right primary, held) = precision.
            // Keyboard equivalents for the simulator: B, P, L, V (held).
            estopAction = MakeAction("TeleopEStop", "<XRController>{RightHand}/secondaryButton", "<Keyboard>/b");
            estopAction.performed += OnEStopShortcut;
            pickAction = MakeAction("TeleopPick", "<XRController>{LeftHand}/primaryButton", "<Keyboard>/p");
            pickAction.performed += OnPickShortcut;
            placeAction = MakeAction("TeleopPlace", "<XRController>{LeftHand}/secondaryButton", "<Keyboard>/l");
            placeAction.performed += OnPlaceShortcut;
            precisionAction = MakeAction("TeleopPrecision", "<XRController>{RightHand}/primaryButton", "<Keyboard>/v");
        }

        private static InputAction MakeAction(string name, string controllerPath, string keyboardPath)
        {
            var action = new InputAction(name, InputActionType.Button);
            action.AddBinding(controllerPath);
            action.AddBinding(keyboardPath);
            action.Enable();
            return action;
        }

        private void OnDisable()
        {
            Dispose(ref estopAction, OnEStopShortcut);
            Dispose(ref pickAction, OnPickShortcut);
            Dispose(ref placeAction, OnPlaceShortcut);
            Dispose(ref precisionAction, null);

            if (following)
            {
                StopFollowing("controller disabled", true);
            }

            DetachLink();
        }

        private static void Dispose(ref InputAction action, Action<InputAction.CallbackContext> handler)
        {
            if (action == null)
            {
                return;
            }

            if (handler != null)
            {
                action.performed -= handler;
            }

            action.Disable();
            action.Dispose();
            action = null;
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
            FindInteractors();
            AttachLink();
        }

        private void OnEStopShortcut(InputAction.CallbackContext context)
        {
            TriggerEStop("B button");
        }

        private void OnPickShortcut(InputAction.CallbackContext context)
        {
            RequestMacro(MacroKind.Pick);
        }

        private void OnPlaceShortcut(InputAction.CallbackContext context)
        {
            RequestMacro(MacroKind.Place);
        }

        private void FindInteractors()
        {
            foreach (NearFarInteractor nearFar in FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Exclude))
            {
                string parent = nearFar.transform.parent != null ? nearFar.transform.parent.name : string.Empty;
                if (parent.StartsWith("Right")) rightInteractor = nearFar;
                else if (parent.StartsWith("Left")) leftInteractor = nearFar;
            }

            if (calibration == null)
            {
                calibration = RobotCalibration.Instance;
            }

            if (controlVolume == null)
            {
                controlVolume = ControlVolume.Instance;
            }
        }

        private void AttachLink()
        {
            if (link != null || RobotLink.Instance == null)
            {
                return;
            }

            link = RobotLink.Instance;
            link.OnMessage += HandleMessage;
            link.OnConnectionChanged += HandleConnection;
        }

        private void DetachLink()
        {
            if (link != null)
            {
                link.OnMessage -= HandleMessage;
                link.OnConnectionChanged -= HandleConnection;
                link = null;
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Main loop
        // ------------------------------------------------------------------------------------------------------

        private void Update()
        {
            if (link == null)
            {
                AttachLink();
            }

            if (calibration == null || rightInteractor == null || controlVolume == null)
            {
                FindInteractors();
            }

            bool gripHeld = DeadmanOverride != null ? DeadmanOverride() : rightInteractor != null && rightInteractor.selectInput.ReadIsPerformed();
            if (!gripHeld)
            {
                needRelease = false; // a new press is needed after any automatic stop
            }

            DeadmanHeld = gripHeld;
            UpdateMarking();

            string blocked = CheckBlocked();
            if (blocked != BlockedReason)
            {
                BlockedReason = blocked;
                OnStateChanged?.Invoke();
            }

            if (pendingMacro != MacroKind.None && Time.unscaledTime > pendingMacroUntil)
            {
                pendingMacro = MacroKind.None;
                MacroStatus = "Pick / Place cancelled: grip was not pressed";
                OnStateChanged?.Invoke();
            }

            bool shouldFollow = gripHeld && !needRelease && string.IsNullOrEmpty(blocked);
            if (!shouldFollow)
            {
                if (following)
                {
                    // a lost tracking / link / calibration forces a new grip press; a plain release does not need one
                    if (gripHeld)
                    {
                        needRelease = true;
                    }

                    StopFollowing(string.IsNullOrEmpty(blocked) ? "deadman released" : blocked, true);
                }

                return;
            }

            if (!following)
            {
                StartFollowing();
            }

            if (pendingMacro != MacroKind.None && !MacroActive)
            {
                BeginMacro(pendingMacro);
                pendingMacro = MacroKind.None;
            }

            if (MacroActive)
            {
                RunMacro();
                return;
            }

            HandleGripButtons();
            UpdateHandTarget();
        }

        private void UpdateHandTarget()
        {
            Vector3 hand = calibration.TipPosition;
            HandUnity = hand;
            Vector3 effective = ApplyPrecision(hand);

            if (Mode == ControlMode.Offset && controlVolume != null && controlVolume.Ready)
            {
                HandRobotMm = controlVolume.WorldToRobot(effective);
            }
            else
            {
                HandRobotMm = calibration.UnityToRobot(effective);
            }

            Vector3 smoothed = filter.Filter(HandRobotMm, Time.unscaledTime);
            Vector3 desired = calibration.Config.ClampToWorkspace(smoothed);
            Clamped = (desired - smoothed).sqrMagnitude > 0.0001f;
            Slew(desired);
            PublishTarget();
            SendIfDue(false);
        }

        /// <summary>Moves the commanded target toward <paramref name="desired"/> at the speed limit of the current mode.</summary>
        private void Slew(Vector3 desired)
        {
            if (!commandedValid)
            {
                commanded = desired;
                commandedValid = true;
                return;
            }

            commanded = Vector3.MoveTowards(commanded, desired, SpeedLimitMmPerSec * Time.unscaledDeltaTime);
        }

        private void PublishTarget()
        {
            TargetRobotMm = commanded;
            TargetUnity = calibration.RobotToUnity(commanded);
        }

        /// <summary>Sends the commanded target when the rate limit allows and it moved (or the grip changed, or <paramref name="force"/>). True if sent.</summary>
        private bool SendIfDue(bool force)
        {
            TeleopConfig config = calibration.Config;
            float interval = 1f / Mathf.Max(1f, config.sendRateHz);
            bool moved = !sentAny || (TargetRobotMm - lastSent).magnitude >= config.minMoveMm;
            bool gripChanged = sentAny && GripOn != lastSentGrip;
            if (Time.unscaledTime >= nextSendTime && (moved || gripChanged || force))
            {
                SendTarget();
                nextSendTime = Time.unscaledTime + interval;
                return true;
            }

            return false;
        }

        private Vector3 ApplyPrecision(Vector3 hand)
        {
            bool precision = PrecisionOverride != null ? PrecisionOverride() : PrecisionHeld();
            float scale = calibration.Config.precisionScale;
            Vector3 effective;
            if (precision)
            {
                if (!precisionWasOn)
                {
                    precisionAnchorHand = hand;
                    precisionAnchorEffective = lastEffectiveHand == Vector3.zero ? hand : lastEffectiveHand;
                }

                effective = precisionAnchorEffective + (hand - precisionAnchorHand) * scale;
            }
            else
            {
                effective = hand;
                if (precisionWasOn)
                {
                    // leaving precision mode: blend back to the real hand instead of jumping
                    precisionReleaseTime = Time.unscaledTime;
                    precisionReleaseFrom = lastEffectiveHand;
                }

                const float blendSeconds = 0.5f;
                float t = (Time.unscaledTime - precisionReleaseTime) / blendSeconds;
                if (t < 1f)
                {
                    effective = Vector3.Lerp(precisionReleaseFrom, hand, Mathf.SmoothStep(0f, 1f, t));
                }
            }

            if (precision != precisionWasOn)
            {
                precisionWasOn = precision;
                PrecisionActive = precision;
                OnStateChanged?.Invoke();
            }

            lastEffectiveHand = effective;
            return effective;
        }

        private bool PrecisionHeld()
        {
            bool left = leftInteractor != null && leftInteractor.selectInput.ReadIsPerformed();
            bool button = precisionAction != null && precisionAction.IsPressed();
            return left || button;
        }

        private string CheckBlocked()
        {
            if (EStopLocked) return "E-STOP active";
            if (calibration == null || !calibration.IsCalibrated) return "not calibrated";
            if (calibration.State != CalibrationState.Idle) return "calibration in progress";
            if (!LinkConnected) return "robot not connected";
            if (requireTracking && !IsRightTracked()) return "hand tracking lost";
            if (Mode == ControlMode.Offset)
            {
                if (controlVolume == null || !controlVolume.Ready) return "no control volume";
                if (controlVolume.Unlocked) return "control volume unlocked (lock it)";
            }

            return string.Empty;
        }

        private bool IsRightTracked()
        {
            if (TrackingOverride != null)
            {
                return TrackingOverride();
            }

            XRController right = XRController.rightHand;
            return right != null && right.isTracked.isPressed;
        }

        private static bool PointingAtUi(NearFarInteractor interactor)
        {
            return interactor != null && interactor.TryGetCurrentUIRaycastResult(out UnityEngine.EventSystems.RaycastResult result) && result.isValid;
        }

        private void HandleGripButtons()
        {
            bool toggle = false;
            if (rightInteractor != null && rightInteractor.activateInput.ReadWasPerformedThisFrame() && !PointingAtUi(rightInteractor)) toggle = true; // right trigger
            if (leftInteractor != null && leftInteractor.activateInput.ReadWasPerformedThisFrame() && !PointingAtUi(leftInteractor)) toggle = true;   // left trigger
            if (toggle)
            {
                SetGrip(!GripOn);
            }
        }

        /// <summary>Turns the gripper / suction on or off (sent with the next teleop message).</summary>
        public void SetGrip(bool on)
        {
            if (GripOn == on)
            {
                return;
            }

            GripOn = on;
            OnStateChanged?.Invoke();
        }

        /// <summary>Switches OFFSET / ABSOLUTE. Following stops; the grip must be pressed again.</summary>
        public void SetMode(ControlMode mode)
        {
            if (Mode == mode)
            {
                return;
            }

            Mode = mode;
            if (following)
            {
                needRelease = DeadmanHeld;
                StopFollowing("mode changed", true);
            }

            Debug.Log("[Teleop] Mode: " + mode);
            OnStateChanged?.Invoke();
        }

        public void ToggleMode()
        {
            SetMode(Mode == ControlMode.Offset ? ControlMode.Absolute : ControlMode.Offset);
        }

        private void StartFollowing()
        {
            TeleopConfig config = calibration.Config;
            float minCutoff = config.smoothingTime > 0.0001f ? 1f / (2f * Mathf.PI * config.smoothingTime) : 1000f;
            filter = new OneEuroFilter(minCutoff, config.smoothingBeta, config.smoothingDerivativeCutoff);
            sentAny = false;
            nextSendTime = 0f;
            following = true;
            precisionWasOn = false;
            lastEffectiveHand = Vector3.zero;
            precisionReleaseTime = -10f;
            if (HasRobotPose)
            {
                commanded = RobotPoseMm; // start where the robot really is: no sudden leap, the target walks to the hand
                commandedValid = true;
            }
            else
            {
                commandedValid = false;
            }

            if (Marking)
            {
                CancelMarking();
            }

            Debug.Log("[Teleop] Following the hand (" + Mode + ").");
            OnStateChanged?.Invoke();
        }

        private void StopFollowing(string reason, bool sendDisable)
        {
            following = false;
            Clamped = false;
            PrecisionActive = false;
            precisionWasOn = false;
            filter?.Reset();
            if (MacroActive)
            {
                MacroStatus = ActiveMacro + " aborted: " + reason;
                ActiveMacro = MacroKind.None;
                macroSteps = null;
            }

            if (sendDisable && sentAny)
            {
                SendRaw("{\"type\":\"teleop\",\"enable\":false}");
            }

            sentAny = false;
            Debug.Log("[Teleop] Stopped following: " + reason);
            OnStateChanged?.Invoke();
        }

        private void SendTarget()
        {
            seq++;
            string json = string.Format(CultureInfo.InvariantCulture,
                "{{\"type\":\"teleop\",\"seq\":{0},\"enable\":true,\"x\":{1:F1},\"y\":{2:F1},\"z\":{3:F1},\"r\":0.0,\"grip\":{4}}}",
                seq, TargetRobotMm.x, TargetRobotMm.y, TargetRobotMm.z, GripOn ? "true" : "false");
            if (SendRaw(json))
            {
                lastSent = TargetRobotMm;
                lastSentGrip = GripOn;
                sentAny = true;
                SentCount++;
                LastTeleopJson = json;
            }
        }

        private bool SendRaw(string json)
        {
            return link != null && link.SendRaw(json);
        }

        // ------------------------------------------------------------------------------------------------------
        // Gem marker
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Starts (or cancels) marking: the next right trigger press places the marker where the right ray meets the table plane.</summary>
        public void ToggleMarking()
        {
            if (Marking)
            {
                CancelMarking();
            }
            else
            {
                BeginMarking();
            }
        }

        public void BeginMarking()
        {
            if (following)
            {
                MarkMessage = "Release the grip first";
                OnStateChanged?.Invoke();
                return;
            }

            if (calibration == null || !calibration.IsCalibrated)
            {
                MarkMessage = "Calibrate first";
                OnStateChanged?.Invoke();
                return;
            }

            Marking = true;
            MarkMessage = "Aim at the gem, press the trigger";
            OnStateChanged?.Invoke();
        }

        public void CancelMarking()
        {
            Marking = false;
            MarkPreviewValid = false;
            MarkMessage = string.Empty;
            OnStateChanged?.Invoke();
        }

        public void ClearGem()
        {
            HasGem = false;
            GemOutOfReach = false;
            MarkMessage = "Marker cleared";
            OnStateChanged?.Invoke();
        }

        private void UpdateMarking()
        {
            if (!Marking)
            {
                return;
            }

            Ray ray;
            if (MarkRayOverride != null)
            {
                ray = MarkRayOverride();
            }
            else if (rightInteractor != null && rightInteractor.curveOrigin != null)
            {
                ray = new Ray(rightInteractor.curveOrigin.position, rightInteractor.curveOrigin.forward);
            }
            else
            {
                MarkPreviewValid = false;
                return;
            }

            MarkPreviewValid = TryHitTable(ray, out Vector3 hit);
            MarkPreviewUnity = hit;

            bool press = rightInteractor != null && rightInteractor.activateInput.ReadWasPerformedThisFrame() && !PointingAtUi(rightInteractor);
            if (press && MarkPreviewValid)
            {
                PlaceGemAt(hit);
            }
        }

        /// <summary>Intersects a ray with the calibrated table plane (robot z = tableZ).</summary>
        public bool TryHitTable(Ray ray, out Vector3 hitUnity)
        {
            hitUnity = Vector3.zero;
            if (calibration == null || !calibration.IsCalibrated)
            {
                return false;
            }

            TeleopConfig config = calibration.Config;
            Vector3 centre = (config.WorkspaceMin + config.WorkspaceMax) * 0.5f;
            Vector3 onPlane = calibration.RobotToUnity(new Vector3(centre.x, centre.y, config.tableZ));
            Vector3 above = calibration.RobotToUnity(new Vector3(centre.x, centre.y, config.tableZ + 100f));
            var plane = new Plane((above - onPlane).normalized, onPlane);
            if (!plane.Raycast(ray, out float enter) || enter > 5f)
            {
                return false;
            }

            hitUnity = ray.GetPoint(enter);
            return true;
        }

        /// <summary>Stores the gem marker at a world point (projected on the table plane).</summary>
        public void PlaceGemAt(Vector3 world)
        {
            TeleopConfig config = calibration.Config;
            Vector3 mm = calibration.UnityToRobot(world);
            mm.z = config.tableZ;
            GemRobotMm = mm;
            HasGem = true;
            Vector3 lo = config.WorkspaceMin;
            Vector3 hi = config.WorkspaceMax;
            GemOutOfReach = mm.x < lo.x || mm.x > hi.x || mm.y < lo.y || mm.y > hi.y;
            Marking = false;
            MarkPreviewValid = false;
            MarkMessage = GemOutOfReach ? "Marked, but outside the robot's reach" : "Gem marked";
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "[Teleop] Gem marked at x {0:F0} y {1:F0} z {2:F0} mm.", mm.x, mm.y, mm.z));
            OnStateChanged?.Invoke();
        }

        /// <summary>Gem marker position in Unity world space.</summary>
        public Vector3 GemUnity => calibration != null && calibration.IsCalibrated ? calibration.RobotToUnity(GemRobotMm) : Vector3.zero;

        // ------------------------------------------------------------------------------------------------------
        // Pick / Place macros
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Asks for a Pick or Place. It runs while the deadman is held; releasing it or pressing E-STOP aborts it.</summary>
        public void RequestMacro(MacroKind kind)
        {
            string blocked = CheckBlocked();
            if (!string.IsNullOrEmpty(blocked))
            {
                MacroStatus = kind + " blocked: " + blocked;
                OnStateChanged?.Invoke();
                return;
            }

            if (!HasRobotPose)
            {
                MacroStatus = kind + " blocked: no robot pose";
                OnStateChanged?.Invoke();
                return;
            }

            if (MacroActive)
            {
                return;
            }

            if (following)
            {
                BeginMacro(kind);
            }
            else
            {
                pendingMacro = kind;
                pendingMacroUntil = Time.unscaledTime + macroArmSeconds;
                MacroStatus = "Hold the right grip to run " + kind;
                OnStateChanged?.Invoke();
            }
        }

        public void RequestPick() => RequestMacro(MacroKind.Pick);

        public void RequestPlace() => RequestMacro(MacroKind.Place);

        private void BeginMacro(MacroKind kind)
        {
            TeleopConfig config = calibration.Config;
            Vector3 here = RobotPoseMm;
            Vector3 start = calibration.Config.ClampToWorkspace(here);
            commanded = HasRobotPose ? RobotPoseMm : start;
            commandedValid = true;

            float pickZ = Mathf.Clamp(config.pickZ, config.zMin, config.zMax);
            float placeZ = Mathf.Clamp(config.placeZ, config.zMin, config.zMax);
            float safeZ = Mathf.Clamp(config.safeZ, config.zMin, config.zMax);
            Vector3 xy = new Vector3(start.x, start.y, 0f);

            macroSteps = new List<MacroStep>();
            if (kind == MacroKind.Pick)
            {
                macroSteps.Add(new MacroStep { label = "descending", target = new Vector3(xy.x, xy.y, pickZ), grip = false, wait = 0f });
                macroSteps.Add(new MacroStep { label = "suction on", target = new Vector3(xy.x, xy.y, pickZ), grip = true, wait = config.gripWaitSeconds });
                macroSteps.Add(new MacroStep { label = "lifting", target = new Vector3(xy.x, xy.y, safeZ), grip = true, wait = 0f });
            }
            else
            {
                bool carrying = GripOn; // keep whatever the gripper is doing while descending
                macroSteps.Add(new MacroStep { label = "descending", target = new Vector3(xy.x, xy.y, placeZ), grip = carrying, wait = 0f });
                macroSteps.Add(new MacroStep { label = "release", target = new Vector3(xy.x, xy.y, placeZ), grip = false, wait = config.gripWaitSeconds });
                macroSteps.Add(new MacroStep { label = "lifting", target = new Vector3(xy.x, xy.y, safeZ), grip = false, wait = 0f });
            }

            ActiveMacro = kind;
            macroIndex = 0;
            macroReachedAt = -1f;
            macroStepStarted = Time.unscaledTime;
            macroForceSend = true;
            Clamped = false;
            UpdateMacroStatus();
            Debug.Log("[Teleop] " + kind + " macro started at x " + xy.x.ToString("F0", CultureInfo.InvariantCulture) + " y " + xy.y.ToString("F0", CultureInfo.InvariantCulture));
            OnStateChanged?.Invoke();
        }

        private void UpdateMacroStatus()
        {
            MacroStatus = $"{ActiveMacro}: {macroSteps[macroIndex].label} ({macroIndex + 1}/{macroSteps.Count})";
        }

        private void RunMacro()
        {
            MacroStep step = macroSteps[macroIndex];
            HandUnity = calibration.TipPosition;
            GripOn = step.grip;
            Slew(step.target);
            PublishTarget();
            if (SendIfDue(macroForceSend))
            {
                macroForceSend = false; // a forced send that the rate limit held back is retried next frame
            }

            bool commandedThere = (commanded - step.target).sqrMagnitude < 0.01f;
            if (!commandedThere)
            {
                return;
            }

            if (macroReachedAt < 0f)
            {
                macroReachedAt = Time.unscaledTime;
                macroForceSend = true; // make sure the exact final target of the step was sent
            }

            bool arrived = !HasRobotPose || (RobotPoseMm - step.target).magnitude <= macroArriveMm
                           || Time.unscaledTime - macroStepStarted > macroStepTimeoutSeconds;
            if (!arrived || Time.unscaledTime - macroReachedAt < step.wait)
            {
                return;
            }

            macroIndex++;
            macroReachedAt = -1f;
            macroStepStarted = Time.unscaledTime;
            macroForceSend = true;
            if (macroIndex >= macroSteps.Count)
            {
                MacroKind finished = ActiveMacro;
                ActiveMacro = MacroKind.None;
                macroSteps = null;
                MacroStatus = finished + " done";
                needRelease = DeadmanHeld; // do not leap to the hand: grip must be pressed again
                StopFollowing(finished + " done", true);
                return;
            }

            UpdateMacroStatus();
        }

        // ------------------------------------------------------------------------------------------------------
        // Safety
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Emergency stop: tells the robot to stop and locks teleop until <see cref="ResetEStop"/>.</summary>
        public void TriggerEStop(string source = "button")
        {
            bool wasLocked = EStopLocked;
            EStopLocked = true;
            following = false;
            sentAny = false;
            pendingMacro = MacroKind.None;
            if (MacroActive)
            {
                MacroStatus = ActiveMacro + " aborted: E-STOP";
                ActiveMacro = MacroKind.None;
                macroSteps = null;
            }

            Marking = false;
            SendRaw("{\"type\":\"estop\"}"); // sent even if it was locked already: never assume the first one arrived
            if (!wasLocked)
            {
                Debug.Log("[Teleop] E-STOP (" + source + ")");
            }

            OnStateChanged?.Invoke();
        }

        /// <summary>Unlocks teleop after an E-STOP. A fresh grip press is needed to follow again.</summary>
        public void ResetEStop()
        {
            if (!EStopLocked)
            {
                return;
            }

            EStopLocked = false;
            needRelease = true;
            SendRaw("{\"type\":\"estop_reset\"}");
            Debug.Log("[Teleop] E-STOP reset.");
            OnStateChanged?.Invoke();
        }

        private void HandleConnection(bool connected)
        {
            if (!connected && following)
            {
                needRelease = true;
                StopFollowing("robot link lost", false);
            }
            else if (connected && EStopLocked)
            {
                SendRaw("{\"type\":\"estop\"}"); // the robot must know about the lock after a reconnect too
            }

            OnStateChanged?.Invoke();
        }

        /// <summary>The app lost focus (headset removed, system menu): stop following.</summary>
        public void HandleFocusLost()
        {
            if (following)
            {
                needRelease = true;
                StopFollowing("app lost focus", true);
            }
        }

        private void OnApplicationFocus(bool focus)
        {
#if UNITY_EDITOR
            if (!focus && stopOnFocusLossInEditor)
            {
                HandleFocusLost();
            }
#else
            if (!focus)
            {
                HandleFocusLost();
            }
#endif
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                HandleFocusLost();
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Robot feedback
        // ------------------------------------------------------------------------------------------------------

        private void HandleMessage(string type, string json)
        {
            if (type != "robot_pose")
            {
                return;
            }

            PoseMessage pose;
            try
            {
                pose = JsonUtility.FromJson<PoseMessage>(json);
            }
            catch (ArgumentException)
            {
                return;
            }

            if (pose == null)
            {
                return;
            }

            RobotPoseMm = new Vector3(pose.x, pose.y, pose.z);
            RobotRotation = pose.r;
            lastPoseTime = Time.unscaledTime;
        }

        /// <summary>Where the robot is, in Unity world space (valid only with a calibration and a recent pose).</summary>
        public Vector3 RobotPoseUnity => calibration != null && calibration.IsCalibrated ? calibration.RobotToUnity(RobotPoseMm) : Vector3.zero;
    }
}
