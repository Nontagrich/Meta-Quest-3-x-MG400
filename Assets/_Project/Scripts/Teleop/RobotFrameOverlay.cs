using UnityEngine;

namespace GemStore.Teleop
{
    /// <summary>
    /// AR overlay of the calibrated robot frame: the robot base axes (X red, Y green, Z blue) and the workspace box as a translucent wireframe.
    /// Redrawn whenever the calibration changes; hidden while there is no calibration.
    /// </summary>
    [DisallowMultipleComponent]
    public class RobotFrameOverlay : MonoBehaviour
    {
        [SerializeField] private RobotCalibration calibration;
        [SerializeField, Min(10f)] private float axisLengthMm = 100f;
        [SerializeField] private Material lineMaterial;
        [SerializeField, Range(0.001f, 0.01f)] private float axisWidth = 0.004f;
        [SerializeField, Range(0.0005f, 0.005f)] private float boxWidth = 0.002f;
        [SerializeField] private Color boxColor = new Color(0.35f, 0.85f, 1f, 0.55f);

        private LineRenderer[] axes = new LineRenderer[3];
        private LineRenderer[] edges = new LineRenderer[12];
        private bool subscribed;

        /// <summary>Number of overlay lines currently visible (3 axes + 12 box edges when calibrated).</summary>
        public int VisibleLines
        {
            get
            {
                int count = 0;
                foreach (LineRenderer line in axes) if (line != null && line.enabled) count++;
                foreach (LineRenderer line in edges) if (line != null && line.enabled) count++;
                return count;
            }
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

        private void Rebuild()
        {
            Attach();
            bool show = calibration != null && calibration.IsCalibrated;
            EnsureLines();
            foreach (LineRenderer line in axes) line.enabled = show;
            foreach (LineRenderer line in edges) line.enabled = show;
            if (!show)
            {
                return;
            }

            TeleopConfig config = calibration.Config;
            Vector3 origin = calibration.RobotToUnity(Vector3.zero);
            SetLine(axes[0], origin, calibration.RobotToUnity(new Vector3(axisLengthMm, 0f, 0f)), Color.red, axisWidth);
            SetLine(axes[1], origin, calibration.RobotToUnity(new Vector3(0f, axisLengthMm, 0f)), Color.green, axisWidth);
            SetLine(axes[2], origin, calibration.RobotToUnity(new Vector3(0f, 0f, axisLengthMm)), new Color(0.25f, 0.45f, 1f), axisWidth);

            Vector3 lo = config.WorkspaceMin;
            Vector3 hi = config.WorkspaceMax;
            var c = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                c[i] = calibration.RobotToUnity(new Vector3((i & 1) == 0 ? lo.x : hi.x, (i & 2) == 0 ? lo.y : hi.y, (i & 4) == 0 ? lo.z : hi.z));
            }

            int[,] pairs = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
            for (int i = 0; i < 12; i++)
            {
                SetLine(edges[i], c[pairs[i, 0]], c[pairs[i, 1]], boxColor, boxWidth);
            }
        }

        private void EnsureLines()
        {
            for (int i = 0; i < axes.Length; i++)
            {
                if (axes[i] == null) axes[i] = NewLine("Axis_" + "XYZ"[i]);
            }

            for (int i = 0; i < edges.Length; i++)
            {
                if (edges[i] == null) edges[i] = NewLine("BoxEdge_" + i);
            }
        }

        private LineRenderer NewLine(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.numCapVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.sharedMaterial = lineMaterial != null ? lineMaterial : new Material(Shader.Find("Sprites/Default"));
            return line;
        }

        private static void SetLine(LineRenderer line, Vector3 from, Vector3 to, Color color, float width)
        {
            line.SetPosition(0, from);
            line.SetPosition(1, to);
            line.startColor = color;
            line.endColor = color;
            line.startWidth = width;
            line.endWidth = width;
        }
    }
}
