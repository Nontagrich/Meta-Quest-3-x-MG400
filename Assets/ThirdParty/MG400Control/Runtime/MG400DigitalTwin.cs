using UnityEngine;

namespace CPE494
{
    /// <summary>
    /// Dobot MG400 digital twin: a 4-node kinematic chain driven either by
    /// the bridge's realtime joint feed or by MG400Controller's local sim.
    ///
    /// Expected hierarchy (built by JewelryProjectSetup):
    ///   MG400 (root, on table)
    ///     J1_Base   (rotates about +Y by j1)        -> turret mesh
    ///       J2_RearArm (pivot at L1 height, rotates about local X by phi2)
    ///         rear arm mesh
    ///         J3_Forearm (rotates about local X by interior bend)
    ///           forearm mesh
    ///           J4_Tool (roll about local Z by j4)
    ///             suction cup mesh, TCP marker at L4 along +Z
    /// </summary>
    public class MG400DigitalTwin : MonoBehaviour
    {
        private static MG400DigitalTwin _instance;
        public static MG400DigitalTwin Instance
        {
            get
            {
                if (_instance == null) _instance = FindAnyObjectByType<MG400DigitalTwin>();
                return _instance;
            }
            private set => _instance = value;
        }

        [Header("Kinematic chain (assign in inspector / auto by setup)")]
        public Transform j1Base;
        public Transform j2RearArm;
        public Transform j3Forearm;
        public Transform j4Tool;

        [Header("TCP")]
        public Transform tcpMarker;          // sits at suction cup tip

        [Header("Motion smoothing")]
        public float followSpeed = 360f;     // deg/s approach to target joints

        [Header("Status")]
        public Vector4 CurrentJoints = new Vector4(0f, MG400Geometry.E2, MG400Geometry.E3, 0f);
        public Vector4 TargetJoints = new Vector4(0f, MG400Geometry.E2, MG400Geometry.E3, 0f);

        public bool VacuumOn { get; private set; }

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
            // smooth approach toward the last commanded/observed joints
            Vector4 delta = TargetJoints - CurrentJoints;
            float maxStep = followSpeed * Time.deltaTime;
            float mag = new Vector3(delta.x, delta.y, delta.z).magnitude;
            if (mag > 0.0001f)
            {
                float step = Mathf.Min(maxStep, mag);
                CurrentJoints += delta * (step / mag);
            }
            else
            {
                CurrentJoints = TargetJoints;
            }
            ApplyJoints(CurrentJoints);
        }

        /// <summary>Jump immediately (used by realtime feed or tests).</summary>
        public void SetTargetJoints(Vector4 joints) => TargetJoints = joints;

        public void SetVacuum(bool on) => VacuumOn = on;

        public Vector3 TcpPosition =>
            tcpMarker != null ? tcpMarker.position : transform.position;

        private void ApplyJoints(Vector4 j)
        {
            if (j1Base != null)
                j1Base.localRotation = Quaternion.Euler(0f, j.x, 0f);

            if (j2RearArm != null)
            {
                float phi2 = j.y - MG400Geometry.E2;
                // Unity: +X rotation pitches +Z downward -> use -phi2 for "up"
                j2RearArm.localRotation = Quaternion.Euler(-phi2, 0f, 0f);
            }

            if (j3Forearm != null && j2RearArm != null)
            {
                float phi2 = j.y - MG400Geometry.E2;
                float phi3 = phi2 - (j.z - MG400Geometry.E3);
                // interior rotation relative to rear arm: -(phi3 - phi2)
                j3Forearm.localRotation = Quaternion.Euler(-(phi3 - phi2), 0f, 0f);
            }

            if (j4Tool != null)
                j4Tool.localRotation = Quaternion.Euler(0f, 0f, j.w);
        }

        /// <summary>
        /// World position the TCP should occupy for a base-frame point
        /// (used by the teleop station to sanity-map its hand targets).
        /// </summary>
        public Vector3 BasePointToWorld(Vector3 baseFrameMeters)
        {
            return transform.TransformPoint(baseFrameMeters);
        }
    }
}
