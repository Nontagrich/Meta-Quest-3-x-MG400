using UnityEngine;

namespace GemStore.Teleop
{
    /// <summary>
    /// AR feedback for the teleoperation:
    ///  - a ghost sphere at the commanded target (red while clamped to the workspace) and a line from the hand to it,
    ///  - a blue cube where the robot reports it really is,
    ///  - a gold diamond on the marked gem,
    ///  - in OFFSET mode the same target / robot / gem markers again, mirrored inside the control volume next to the technician.
    /// </summary>
    [DisallowMultipleComponent]
    public class TeleopVisuals : MonoBehaviour
    {
        [SerializeField] private TeleopController controller;
        [SerializeField, Tooltip("Unlit material that follows the object's colour (Sprites/Default).")]
        private Material material;
        [SerializeField] private float targetSize = 0.035f;
        [SerializeField] private float robotMarkerSize = 0.03f;
        [SerializeField] private float gemMarkerSize = 0.03f;
        [SerializeField] private Color targetColor = new Color(0.25f, 1f, 0.45f, 0.75f);
        [SerializeField] private Color clampedColor = new Color(1f, 0.15f, 0.12f, 0.9f);
        [SerializeField] private Color robotColor = new Color(0.25f, 0.6f, 1f, 0.95f);
        [SerializeField] private Color gemColor = new Color(1f, 0.82f, 0.2f, 0.95f);
        [SerializeField] private Color previewColor = new Color(1f, 1f, 1f, 0.8f);

        private class Marker
        {
            public Transform transform;
            public Material material;
        }

        private Marker target;
        private Marker targetMirror;
        private Marker robot;
        private Marker robotMirror;
        private Marker gem;
        private Marker gemMirror;
        private Marker preview;
        private LineRenderer handLine;
        private Material lineMaterial;

        private void Awake()
        {
            if (material == null)
            {
                material = new Material(Shader.Find("Sprites/Default"));
            }

            target = CreateMarker("TeleopTarget", PrimitiveType.Sphere, targetSize, Vector3.zero);
            targetMirror = CreateMarker("TeleopTargetInVolume", PrimitiveType.Sphere, targetSize * 0.8f, Vector3.zero);
            robot = CreateMarker("RobotPose", PrimitiveType.Cube, robotMarkerSize, Vector3.zero);
            robotMirror = CreateMarker("RobotPoseInVolume", PrimitiveType.Cube, robotMarkerSize * 0.8f, Vector3.zero);
            gem = CreateMarker("GemMarker", PrimitiveType.Cube, gemMarkerSize, new Vector3(45f, 0f, 45f)); // a cube on its corner = a diamond
            gemMirror = CreateMarker("GemMarkerInVolume", PrimitiveType.Cube, gemMarkerSize * 0.8f, new Vector3(45f, 0f, 45f));
            preview = CreateMarker("GemPreview", PrimitiveType.Sphere, 0.02f, Vector3.zero);

            lineMaterial = new Material(material);
            var lineObject = new GameObject("TeleopHandLine");
            lineObject.transform.SetParent(transform, false);
            handLine = lineObject.AddComponent<LineRenderer>();
            handLine.useWorldSpace = true;
            handLine.positionCount = 2;
            handLine.widthMultiplier = 0.004f;
            handLine.sharedMaterial = lineMaterial;
            handLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            handLine.receiveShadows = false;
            handLine.enabled = false;
        }

        private Marker CreateMarker(string markerName, PrimitiveType type, float size, Vector3 euler)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = markerName;
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * size;
            go.transform.rotation = Quaternion.Euler(euler);
            Destroy(go.GetComponent<Collider>());
            var marker = new Marker { transform = go.transform, material = new Material(material) };
            Renderer r = go.GetComponent<Renderer>();
            r.sharedMaterial = marker.material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.SetActive(false);
            return marker;
        }

        private void OnDestroy()
        {
            foreach (Marker m in new[] { target, targetMirror, robot, robotMirror, gem, gemMirror, preview })
            {
                if (m != null && m.material != null) Destroy(m.material);
            }

            if (lineMaterial != null) Destroy(lineMaterial);
        }

        private static void Show(Marker marker, bool visible, Vector3 position, Color color)
        {
            marker.transform.gameObject.SetActive(visible);
            if (visible)
            {
                marker.transform.position = position;
                marker.material.color = color;
            }
        }

        private void LateUpdate()
        {
            if (controller == null)
            {
                controller = TeleopController.Instance;
                if (controller == null)
                {
                    return;
                }
            }

            bool calibrated = controller.Calibrated;
            ControlVolume volume = controller.Volume;
            bool inVolume = calibrated && volume != null && volume.Ready && controller.Mode == ControlMode.Offset;
            bool following = controller.Following;
            Color targetTint = controller.Clamped || controller.EStopLocked ? clampedColor : targetColor;

            // target (commanded) in the real workspace, and mirrored in the volume
            Show(target, following, controller.TargetUnity, targetTint);
            Vector3 mirrorTarget = inVolume ? volume.RobotToWorld(controller.TargetRobotMm) : Vector3.zero;
            Show(targetMirror, following && inVolume, mirrorTarget, targetTint);

            // line: in OFFSET mode from the hand to its cursor inside the volume, in ABSOLUTE mode from the hand to the real target
            handLine.enabled = following;
            if (following)
            {
                Vector3 end = inVolume ? mirrorTarget : controller.TargetUnity;
                handLine.SetPosition(0, controller.HandUnity);
                handLine.SetPosition(1, end);
                lineMaterial.color = targetTint;
            }

            // robot pose
            bool showRobot = calibrated && controller.HasRobotPose;
            Color robotTint = controller.EStopLocked ? clampedColor : robotColor;
            Show(robot, showRobot, controller.RobotPoseUnity, robotTint);
            Show(robotMirror, showRobot && inVolume, inVolume ? volume.RobotToWorld(controller.RobotPoseMm) : Vector3.zero, robotTint);

            // gem marker (real position and mirrored)
            bool showGem = calibrated && controller.HasGem;
            Color gemTint = controller.GemOutOfReach ? clampedColor : gemColor;
            Show(gem, showGem, controller.GemUnity, gemTint);
            Show(gemMirror, showGem && inVolume, inVolume ? volume.RobotToWorld(controller.GemRobotMm) : Vector3.zero, gemTint);

            // reticle while marking
            Show(preview, controller.Marking && controller.MarkPreviewValid, controller.MarkPreviewUnity, previewColor);
        }
    }
}
