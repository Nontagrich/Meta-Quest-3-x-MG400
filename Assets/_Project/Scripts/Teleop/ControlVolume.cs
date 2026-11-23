using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace GemStore.Teleop
{
    /// <summary>
    /// The "control volume" of SAFE OFFSET mode: a 1:1 copy of the robot workspace box that floats in front of the technician (default 0.5 m
    /// ahead, at waist height, axes parallel to the robot axes). The hand position inside it maps to the same position in the real workspace,
    /// so the arm follows the hand without ever coming toward the technician's own hand.
    /// It can be unlocked (HUD button) and grabbed by its handle to reposition it; it only moves, it never rotates.
    /// Local axes: x = robot X, y = robot Z (up), z = robot Y; local origin = the workspace minimum corner. 1 m of volume = 1000 mm.
    /// </summary>
    [DisallowMultipleComponent]
    public class ControlVolume : MonoBehaviour
    {
        [SerializeField] private RobotCalibration calibration;
        [SerializeField] private Material lineMaterial;
        [SerializeField] private Color edgeColor = new Color(1f, 0.62f, 0.15f, 0.9f);
        [SerializeField] private Color handleColor = new Color(1f, 0.85f, 0.2f, 0.9f);
        [SerializeField] private Color lockedHandleColor = new Color(0.4f, 0.4f, 0.4f, 0.0f);
        [SerializeField, Tooltip("Horizontal distance (m) between the control volume and the real workspace below which a warning is shown.")]
        private float minSafeDistance = 0.4f;
        [SerializeField, Tooltip("Height of the volume's centre below the head (m): about waist height.")]
        private float centerBelowHead = 0.45f;

        private readonly LineRenderer[] edges = new LineRenderer[12];
        private Transform handle;
        private Renderer handleRenderer;
        private Collider handleCollider;
        private XRGrabInteractable grab;
        private Material handleMaterial;
        private bool placed;
        private bool userMoved;
        private float startTime;
        private bool subscribed;
        private Vector3 size;

        public static ControlVolume Instance { get; private set; }

        /// <summary>True once the volume has a calibration and a position.</summary>
        public bool Ready => placed && calibration != null && calibration.IsCalibrated;

        /// <summary>True while the handle can be grabbed (teleop is blocked then).</summary>
        public bool Unlocked => grab != null && grab.enabled;

        /// <summary>Horizontal distance (m) between the volume's centre and the real workspace's centre.</summary>
        public float DistanceToRealWorkspace
        {
            get
            {
                if (!Ready)
                {
                    return float.MaxValue;
                }

                Vector3 real = calibration.RobotToUnity(Center(calibration.Config));
                Vector3 mine = RobotToWorld(Center(calibration.Config));
                real.y = 0f;
                mine.y = 0f;
                return Vector3.Distance(real, mine);
            }
        }

        /// <summary>True when the control volume is so close to the real workspace that the hand would be near the arm.</summary>
        public bool TooCloseToRobot => Ready && DistanceToRealWorkspace < minSafeDistance;

        private static Vector3 Center(TeleopConfig config) => (config.WorkspaceMin + config.WorkspaceMax) * 0.5f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        private void Awake()
        {
            Instance = this;
            startTime = Time.unscaledTime;
            BuildParts();
            SetUnlocked(false);
            SetVisible(false);
        }

        private void OnEnable()
        {
            Attach();
        }

        private void Start()
        {
            Attach();
            Rebuild();
        }

        private void OnDisable()
        {
            if (subscribed && calibration != null)
            {
                calibration.OnCalibrationChanged -= Rebuild;
            }

            subscribed = false;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            if (handleMaterial != null)
            {
                Destroy(handleMaterial);
            }
        }

        private void Attach()
        {
            if (calibration == null)
            {
                calibration = RobotCalibration.Instance;
            }

            if (calibration != null && !subscribed)
            {
                calibration.OnCalibrationChanged += Rebuild;
                subscribed = true;
            }
        }

        private void Update()
        {
            if (!placed && calibration != null && calibration.IsCalibrated)
            {
                Rebuild();
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Mapping
        // ------------------------------------------------------------------------------------------------------

        public Vector3 RobotToWorld(Vector3 robotMm)
        {
            Vector3 lo = calibration.Config.WorkspaceMin;
            return transform.TransformPoint(new Vector3(robotMm.x - lo.x, robotMm.z - lo.z, robotMm.y - lo.y) * 0.001f);
        }

        public Vector3 WorldToRobot(Vector3 world)
        {
            Vector3 lo = calibration.Config.WorkspaceMin;
            Vector3 local = transform.InverseTransformPoint(world) * 1000f;
            return new Vector3(lo.x + local.x, lo.y + local.z, lo.z + local.y);
        }

        // ------------------------------------------------------------------------------------------------------
        // Lock / unlock
        // ------------------------------------------------------------------------------------------------------

        public void SetUnlocked(bool unlocked)
        {
            if (grab != null)
            {
                grab.enabled = unlocked;
            }

            if (handleCollider != null)
            {
                handleCollider.enabled = unlocked;
            }

            if (handleRenderer != null)
            {
                handleRenderer.enabled = unlocked;
            }
        }

        /// <summary>Moves the volume so that its centre is at <paramref name="center"/> (world), keeping its orientation.</summary>
        public void MoveCenterTo(Vector3 center)
        {
            transform.position = center - transform.rotation * (size * 0.5f);
            userMoved = true;
        }

        // ------------------------------------------------------------------------------------------------------
        // Build
        // ------------------------------------------------------------------------------------------------------

        private void BuildParts()
        {
            if (lineMaterial == null)
            {
                lineMaterial = new Material(Shader.Find("Sprites/Default"));
            }

            for (int i = 0; i < edges.Length; i++)
            {
                var go = new GameObject("VolumeEdge_" + i);
                go.transform.SetParent(transform, false);
                var line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = false;
                line.positionCount = 2;
                line.numCapVertices = 2;
                line.widthMultiplier = 0.003f;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.sharedMaterial = lineMaterial;
                line.startColor = edgeColor;
                line.endColor = edgeColor;
                edges[i] = line;
            }

            // handle: a thin slab under the box, the only thing that can be grabbed
            GameObject pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pad.name = "ControlVolumeHandle";
            pad.transform.SetParent(transform, false);
            handle = pad.transform;
            handleCollider = pad.GetComponent<Collider>();
            handleRenderer = pad.GetComponent<Renderer>();
            handleMaterial = new Material(lineMaterial) { color = handleColor };
            handleRenderer.sharedMaterial = handleMaterial;
            handleRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            handleRenderer.receiveShadows = false;

            var body = gameObject.GetComponent<Rigidbody>();
            if (body == null)
            {
                body = gameObject.AddComponent<Rigidbody>();
            }

            body.isKinematic = true;
            body.useGravity = false;

            grab = gameObject.GetComponent<XRGrabInteractable>();
            if (grab == null)
            {
                grab = gameObject.AddComponent<XRGrabInteractable>();
            }

            grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grab.trackRotation = false; // the axes stay parallel to the robot axes
            grab.throwOnDetach = false;
            grab.useDynamicAttach = true; // keep the offset between hand and volume: no jump when grabbed
            grab.selectExited.AddListener(_ => userMoved = true);
        }

        private void SetVisible(bool visible)
        {
            foreach (LineRenderer line in edges)
            {
                if (line != null)
                {
                    line.enabled = visible;
                }
            }
        }

        private void Rebuild()
        {
            Attach();
            bool show = calibration != null && calibration.IsCalibrated;
            SetVisible(show);
            if (!show)
            {
                placed = false;
                SetUnlocked(false);
                return;
            }

            TeleopConfig config = calibration.Config;
            Vector3 lo = config.WorkspaceMin;
            Vector3 hi = config.WorkspaceMax;
            size = new Vector3(hi.x - lo.x, hi.z - lo.z, hi.y - lo.y) * 0.001f; // local x, y (up), z

            // orientation: local x = robot X, local y = robot Z, local z = robot Y, as seen in Unity
            Vector3 origin = calibration.RobotToUnity(Vector3.zero);
            Vector3 dirY = (calibration.RobotToUnity(new Vector3(0f, 1000f, 0f)) - origin).normalized;
            Vector3 dirZ = (calibration.RobotToUnity(new Vector3(0f, 0f, 1000f)) - origin).normalized;
            Vector3 dirX = (calibration.RobotToUnity(new Vector3(1000f, 0f, 0f)) - origin).normalized;
            Quaternion rotation = Quaternion.LookRotation(dirY, dirZ);
            if (Vector3.Dot(rotation * Vector3.right, dirX) < 0.9f)
            {
                Debug.LogWarning("[Teleop] Control volume axes do not match the robot X axis; check the calibration handedness.");
            }

            Vector3 centerBefore = placed ? transform.position + transform.rotation * (size * 0.5f) : Vector3.zero;
            transform.rotation = rotation;

            var c = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                c[i] = new Vector3((i & 1) == 0 ? 0f : size.x, (i & 2) == 0 ? 0f : size.y, (i & 4) == 0 ? 0f : size.z);
            }

            int[,] pairs = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
            for (int i = 0; i < 12; i++)
            {
                edges[i].SetPosition(0, c[pairs[i, 0]]);
                edges[i].SetPosition(1, c[pairs[i, 1]]);
            }

            handle.localPosition = new Vector3(size.x * 0.5f, -0.03f, size.z * 0.5f);
            handle.localScale = new Vector3(Mathf.Max(0.12f, size.x * 0.6f), 0.025f, 0.07f);

            if (!placed)
            {
                Camera cam = Camera.main;
                if (cam != null && cam.transform.position.y < 0.3f && Time.unscaledTime - startTime < 1.5f)
                {
                    SetVisible(false);
                    return; // head tracking has not delivered a pose yet: try again next frame
                }

                PlaceInFront(cam);
                placed = true;
            }
            else if (userMoved)
            {
                MoveCenterTo(centerBefore); // a re-calibration keeps the place the technician chose
            }
        }

        private void PlaceInFront(Camera cam)
        {
            TeleopConfig config = calibration.Config;
            Vector3 realCenter = calibration.RobotToUnity(Center(config));
            realCenter.y = 0f;
            Vector3 head = cam != null ? cam.transform.position : new Vector3(0f, 1.4f, 0f);
            Vector3 forward = cam != null ? Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up) : Vector3.forward;
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = Vector3.forward;
            }

            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            float distance = config.controlVolumeDistance;

            // straight ahead first; if that is too close to the real robot area, try the sides and keep the farthest
            Vector3 best = Vector3.zero;
            float bestDistance = -1f;
            foreach (float side in new[] { 0f, -0.45f, 0.45f })
            {
                Vector3 candidate = head + forward * distance + right * side;
                candidate.y = head.y - centerBelowHead;
                Vector3 flat = candidate;
                flat.y = 0f;
                float d = Vector3.Distance(flat, realCenter);
                if (d > bestDistance)
                {
                    bestDistance = d;
                    best = candidate;
                }

                if (d >= minSafeDistance)
                {
                    best = candidate;
                    break;
                }
            }

            transform.position = best - transform.rotation * (size * 0.5f);
        }
    }
}
