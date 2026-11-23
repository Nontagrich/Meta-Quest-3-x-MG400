using System;
using System.Collections.Generic;
using System.IO;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace GemStore.Teleop
{
    public enum CalibrationState
    {
        Idle,
        CapturingP0,
        CapturingP1,
        CapturingP2,
        Review // the three points are captured: check the errors, then Accept or Redo
    }

    /// <summary>
    /// 3-point calibration between the Unity world and the robot frame. The technician touches the three reference points (P0, P1, P2 of
    /// <see cref="TeleopConfig"/>) with the right controller tip or the right index fingertip and presses the trigger / pinches.
    /// The accepted calibration is saved to persistentDataPath and loaded again at start.
    /// <para>The points are stored in TRACKING space (relative to the camera's parent in the XR Origin), not in Unity world space, so the
    /// calibration stays on the real table when the XR Origin is moved or turned and after a restart. A system recenter (long-press Meta
    /// button) moves the tracking space itself: the panel then asks to check / redo the calibration.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public class RobotCalibration : MonoBehaviour
    {
        public const float WarnErrorMm = 10f;
        private const string FileName = "robot_calibration.json";

        [Serializable]
        private class SavedCalibration
        {
            public Vector3[] unityPoints;   // tracking space when space == "tracking"; Unity world space in older files
            public Vector3[] robotPoints;
            public string space;
            public string savedAt;
        }

        [SerializeField] private TeleopConfig config;
        [SerializeField, Tooltip("Right Poke Interactor: its attach point is the controller tip or the index fingertip. Found automatically if empty.")]
        private XRPokeInteractor tipInteractor;
        [SerializeField, Tooltip("Right Near-Far Interactor: its select input (trigger / pinch) confirms a point. Found automatically if empty.")]
        private NearFarInteractor selectInteractor;
        [SerializeField, Min(0f)] private float markerSize = 0.012f;

        private readonly Vector3[] captured = new Vector3[3]; // tracking space
        private readonly List<XRInputSubsystem> inputSubsystems = new List<XRInputSubsystem>();
        private Transform trackingSpace;
        private RobotFrame accepted;
        private RobotFrame active;
        private GameObject[] markers = new GameObject[3];
        private GameObject tipMarker;

        public static RobotCalibration Instance { get; private set; }

        public TeleopConfig Config => config;
        public CalibrationState State { get; private set; } = CalibrationState.Idle;

        /// <summary>The transform in use: the accepted one, or the one under review.</summary>
        public RobotFrame Frame => active;

        public bool IsCalibrated => active != null;

        /// <summary>True when a saved calibration was loaded but the reference points in TeleopConfig changed since: recalibrate.</summary>
        public bool NeedsRecalibration { get; private set; }

        /// <summary>The last problem (for example collinear points), or empty.</summary>
        public string LastError { get; private set; } = string.Empty;

        /// <summary>True after a system recenter changed the tracking space since the calibration was made / loaded: check it.</summary>
        public bool TrackingOriginChanged { get; private set; }

        /// <summary>The transform the calibration is stored in (the camera's parent in the XR Origin), or null (= world).</summary>
        public Transform TrackingSpace => trackingSpace;

        public float[] ErrorsMm => active != null ? active.ErrorsMm : null;

        public float MaxErrorMm => active != null ? active.MaxErrorMm : 0f;

        public bool HasWarning => active != null && State == CalibrationState.Review && active.MaxErrorMm > WarnErrorMm;

        /// <summary>For tests and fake calibration: replaces the tip position used when capturing.</summary>
        public Func<Vector3> TipOverride { get; set; }

        public event Action OnStateChanged;
        public event Action OnCalibrationChanged;

        public static string SavePath => Path.Combine(Application.persistentDataPath, FileName);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        private void Awake()
        {
            Instance = this;
            FindTrackingSpace();
        }

        private void OnEnable()
        {
            SubsystemManager.GetSubsystems(inputSubsystems);
            foreach (XRInputSubsystem subsystem in inputSubsystems)
            {
                subsystem.trackingOriginUpdated += OnTrackingOriginUpdated;
            }
        }

        private void OnDisable()
        {
            foreach (XRInputSubsystem subsystem in inputSubsystems)
            {
                if (subsystem != null)
                {
                    subsystem.trackingOriginUpdated -= OnTrackingOriginUpdated;
                }
            }

            inputSubsystems.Clear();
        }

        private void OnTrackingOriginUpdated(XRInputSubsystem subsystem)
        {
            if (IsCalibrated)
            {
                TrackingOriginChanged = true;
                Debug.LogWarning("[Teleop] The headset tracking origin was recentered: check the robot axes and recalibrate if they are off.");
                OnStateChanged?.Invoke();
            }
        }

        private void FindTrackingSpace()
        {
            var origin = FindAnyObjectByType<XROrigin>();
            if (origin != null && origin.Camera != null)
            {
                trackingSpace = origin.Camera.transform.parent;
            }
            else if (Camera.main != null)
            {
                trackingSpace = Camera.main.transform.parent;
            }

            if (trackingSpace != null)
            {
                trackingSpace.hasChanged = false;
            }
        }

        /// <summary>World point to tracking space (where the calibration is stored).</summary>
        public Vector3 WorldToTracking(Vector3 world) => trackingSpace != null ? trackingSpace.InverseTransformPoint(world) : world;

        public Vector3 TrackingToWorld(Vector3 tracking) => trackingSpace != null ? trackingSpace.TransformPoint(tracking) : tracking;

        private Vector3[] ToTracking(Vector3[] world)
        {
            var result = new Vector3[world.Length];
            for (int i = 0; i < world.Length; i++)
            {
                result[i] = WorldToTracking(world[i]);
            }

            return result;
        }

        private void Start()
        {
            FindInteractors();
            Load();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            DestroyMarkers();
        }

        private void FindInteractors()
        {
            if (tipInteractor == null || selectInteractor == null)
            {
                foreach (XRPokeInteractor poke in FindObjectsByType<XRPokeInteractor>(FindObjectsInactive.Exclude))
                {
                    if (poke.transform.parent != null && poke.transform.parent.name.StartsWith("Right"))
                    {
                        tipInteractor = poke;
                    }
                }

                foreach (NearFarInteractor nearFar in FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Exclude))
                {
                    if (nearFar.transform.parent != null && nearFar.transform.parent.name.StartsWith("Right"))
                    {
                        selectInteractor = nearFar;
                    }
                }
            }
        }

        private void Update()
        {
            if (trackingSpace != null && trackingSpace.hasChanged)
            {
                trackingSpace.hasChanged = false;
                if (IsCalibrated)
                {
                    OnCalibrationChanged?.Invoke(); // the origin moved: redraw the overlay / volume at the (unchanged) real place
                }
            }

            if (State == CalibrationState.Idle || State == CalibrationState.Review)
            {
                return;
            }

            UpdateTipMarker();
            if (selectInteractor != null && selectInteractor.selectInput.ReadWasPerformedThisFrame())
            {
                CaptureCurrent();
            }
        }

        /// <summary>Current tip position in world space: the right fingertip or controller tip (or the test override).</summary>
        public Vector3 TipPosition
        {
            get
            {
                if (TipOverride != null)
                {
                    return TipOverride();
                }

                if (tipInteractor == null)
                {
                    FindInteractors();
                }

                if (tipInteractor != null)
                {
                    Transform attach = tipInteractor.attachTransform != null ? tipInteractor.attachTransform : tipInteractor.transform;
                    return attach.position;
                }

                return Camera.main != null ? Camera.main.transform.position + Camera.main.transform.forward * 0.4f : Vector3.zero;
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Guided flow
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Starts (or restarts) the guided calibration at P0. The calibration in use stays active until a new one is accepted.</summary>
        public void StartCalibration()
        {
            if (config == null)
            {
                LastError = "No TeleopConfig assigned.";
                return;
            }

            LastError = string.Empty;
            DestroyMarkers();
            active = accepted;
            SetState(CalibrationState.CapturingP0);
            OnCalibrationChanged?.Invoke();
        }

        public void Redo()
        {
            StartCalibration();
        }

        public void Cancel()
        {
            DestroyMarkers();
            active = accepted;
            SetState(CalibrationState.Idle);
            OnCalibrationChanged?.Invoke();
        }

        /// <summary>Confirms the current tip position as the point being asked for.</summary>
        public void CaptureCurrent()
        {
            CapturePoint(TipPosition);
        }

        public void CapturePoint(Vector3 world)
        {
            int index = State == CalibrationState.CapturingP0 ? 0 : State == CalibrationState.CapturingP1 ? 1 : State == CalibrationState.CapturingP2 ? 2 : -1;
            if (index < 0)
            {
                return;
            }

            captured[index] = WorldToTracking(world);
            Color[] colors = { Color.red, Color.green, Color.blue };
            DestroyMarker(index);
            markers[index] = MakeMarker("Calibration_P" + index, colors[index], markerSize);
            markers[index].transform.position = world;

            if (index < 2)
            {
                SetState(index == 0 ? CalibrationState.CapturingP1 : CalibrationState.CapturingP2);
                return;
            }

            if (RobotFrame.TrySolve(captured, config.ReferencePoints, out RobotFrame frame, out string error))
            {
                LastError = string.Empty;
                active = frame;
                SetState(CalibrationState.Review);
                OnCalibrationChanged?.Invoke();
                Debug.Log($"[Teleop] Calibration fit errors (mm): P0 {frame.ErrorsMm[0]:F1}, P1 {frame.ErrorsMm[1]:F1}, P2 {frame.ErrorsMm[2]:F1}.");
            }
            else
            {
                LastError = error;
                Debug.LogWarning("[Teleop] Calibration failed: " + error);
                DestroyMarkers();
                SetState(CalibrationState.CapturingP0); // start over
            }
        }

        /// <summary>Keeps the calibration under review (even above the 10 mm warning) and saves it.</summary>
        public void Accept()
        {
            if (State != CalibrationState.Review || active == null)
            {
                return;
            }

            accepted = active;
            NeedsRecalibration = false;
            TrackingOriginChanged = false;
            Save(captured, config.ReferencePoints);
            DestroyMarkers();
            SetState(CalibrationState.Idle);
            OnCalibrationChanged?.Invoke();
        }

        /// <summary>Applies a calibration from three Unity world points right away (fake calibration, tests). Optionally saves it.</summary>
        public bool ApplyCalibration(Vector3[] unityWorldPoints, bool save, out string error)
        {
            error = null;
            if (config == null)
            {
                error = "No TeleopConfig assigned.";
                return false;
            }

            Vector3[] trackingPoints = ToTracking(unityWorldPoints);
            if (!RobotFrame.TrySolve(trackingPoints, config.ReferencePoints, out RobotFrame frame, out error))
            {
                return false;
            }

            accepted = frame;
            active = frame;
            NeedsRecalibration = false;
            TrackingOriginChanged = false;
            DestroyMarkers();
            Array.Copy(trackingPoints, captured, 3);
            if (save)
            {
                Save(trackingPoints, config.ReferencePoints);
            }

            SetState(CalibrationState.Idle);
            OnCalibrationChanged?.Invoke();
            return true;
        }

        /// <summary>Forgets the calibration in memory and on disk.</summary>
        public void Clear()
        {
            accepted = null;
            active = null;
            NeedsRecalibration = false;
            DestroyMarkers();
            if (File.Exists(SavePath))
            {
                File.Delete(SavePath);
            }

            SetState(CalibrationState.Idle);
            OnCalibrationChanged?.Invoke();
        }

        // ------------------------------------------------------------------------------------------------------
        // Conversion
        // ------------------------------------------------------------------------------------------------------

        public Vector3 UnityToRobot(Vector3 world) => active != null ? active.ToRobot(WorldToTracking(world)) : Vector3.zero;

        public Vector3 RobotToUnity(Vector3 robotMm) => active != null ? TrackingToWorld(active.ToUnity(robotMm)) : Vector3.zero;

        /// <summary>Instruction for the technician, for the panel.</summary>
        public string Instruction
        {
            get
            {
                switch (State)
                {
                    case CalibrationState.CapturingP0: return Ask(0, config.p0);
                    case CalibrationState.CapturingP1: return Ask(1, config.p1);
                    case CalibrationState.CapturingP2: return Ask(2, config.p2);
                    case CalibrationState.Review: return HasWarning ? "Fit error is above 10 mm. Redo is recommended." : "Calibration done. Check the axes, then Accept.";
                    default: return IsCalibrated && TrackingOriginChanged ? "Headset was recentered: check the axes, recalibrate if they are off." : IsCalibrated ? (NeedsRecalibration ? "Reference points changed since the last calibration: recalibrate." : "Calibrated.") : "Not calibrated.";
                }
            }
        }

        private static string Ask(int index, Vector3 robotPoint)
        {
            return $"Touch point P{index} ({robotPoint.x:F0}, {robotPoint.y:F0}, {robotPoint.z:F0} mm) and press the grip / pinch.";
        }

        // ------------------------------------------------------------------------------------------------------
        // Save / load
        // ------------------------------------------------------------------------------------------------------

        private void Save(Vector3[] trackingPoints, Vector3[] robotPoints)
        {
            try
            {
                var data = new SavedCalibration { unityPoints = (Vector3[])trackingPoints.Clone(), robotPoints = (Vector3[])robotPoints.Clone(), space = "tracking", savedAt = DateTime.Now.ToString("s") };
                File.WriteAllText(SavePath, JsonUtility.ToJson(data, true));
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Teleop] Could not save the calibration: " + exception.Message);
            }
        }

        /// <summary>Loads the saved calibration, if there is one. Uses the robot points that were saved with it.</summary>
        public bool Load()
        {
            try
            {
                if (!File.Exists(SavePath))
                {
                    return false;
                }

                SavedCalibration data = JsonUtility.FromJson<SavedCalibration>(File.ReadAllText(SavePath));
                if (data == null || data.unityPoints == null || data.robotPoints == null || data.unityPoints.Length != 3)
                {
                    return false;
                }

                bool legacy = data.space != "tracking";
                if (legacy)
                {
                    // older files stored Unity world points: convert them with the current origin and save them in the new form
                    data.unityPoints = ToTracking(data.unityPoints);
                }

                if (!RobotFrame.TrySolve(data.unityPoints, data.robotPoints, out RobotFrame frame, out _))
                {
                    return false;
                }

                if (legacy)
                {
                    Save(data.unityPoints, data.robotPoints);
                }

                accepted = frame;
                active = frame;
                TrackingOriginChanged = false;
                Array.Copy(data.unityPoints, captured, 3);
                NeedsRecalibration = config != null && !SamePoints(data.robotPoints, config.ReferencePoints);
                Debug.Log("[Teleop] Calibration loaded (saved " + data.savedAt + ")." + (NeedsRecalibration ? " The reference points in TeleopConfig changed: recalibrate." : string.Empty));
                OnCalibrationChanged?.Invoke();
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Teleop] Could not read the saved calibration: " + exception.Message);
                return false;
            }
        }

        private static bool SamePoints(Vector3[] a, Vector3[] b)
        {
            for (int i = 0; i < 3; i++)
            {
                if ((a[i] - b[i]).sqrMagnitude > 0.01f)
                {
                    return false;
                }
            }

            return true;
        }

        // ------------------------------------------------------------------------------------------------------

        private void SetState(CalibrationState state)
        {
            State = state;
            if (state == CalibrationState.Idle || state == CalibrationState.Review)
            {
                if (tipMarker != null)
                {
                    Destroy(tipMarker);
                    tipMarker = null;
                }
            }

            OnStateChanged?.Invoke();
        }

        private void UpdateTipMarker()
        {
            if (tipMarker == null)
            {
                tipMarker = MakeMarker("Calibration_Tip", Color.white, markerSize * 0.7f);
            }

            tipMarker.transform.position = TipPosition;
        }

        private static GameObject MakeMarker(string name, Color color, float size)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = name;
            marker.transform.localScale = Vector3.one * size;
            Destroy(marker.GetComponent<Collider>());
            var renderer = marker.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = color };
            renderer.sharedMaterial = material;
            return marker;
        }

        private void DestroyMarker(int index)
        {
            if (markers[index] != null)
            {
                Destroy(markers[index]);
                markers[index] = null;
            }
        }

        private void DestroyMarkers()
        {
            for (int i = 0; i < markers.Length; i++)
            {
                DestroyMarker(i);
            }

            if (tipMarker != null)
            {
                Destroy(tipMarker);
                tipMarker = null;
            }
        }
    }
}
