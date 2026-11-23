using UnityEngine;

namespace GemStore.Teleop
{
    /// <summary>
    /// Robot-agnostic teleoperation settings. Everything is in the ROBOT frame in millimetres: the robot team gives the real calibration
    /// reference points and the safe workspace; Unity converts between its own world (metres) and this frame.
    /// </summary>
    [CreateAssetMenu(fileName = "TeleopConfig", menuName = "GemStore/Teleop Config", order = 5)]
    public class TeleopConfig : ScriptableObject
    {
        [Header("Calibration reference points (robot frame, mm). The technician touches these three physical points.")]
        public Vector3 p0 = new Vector3(200f, 0f, 0f);
        public Vector3 p1 = new Vector3(300f, 0f, 0f);
        public Vector3 p2 = new Vector3(200f, 100f, 0f);

        [Header("Workspace limits (robot frame, mm). Targets are clamped to this box.")]
        public float xMin = 100f;
        public float xMax = 400f;
        public float yMin = -150f;
        public float yMax = 150f;
        public float zMin = 0f;
        public float zMax = 150f;

        [Header("Sending targets over RobotLink")]
        [Min(1f)] public float sendRateHz = 10f;
        [Min(0f), Tooltip("A new target is only sent when it moved at least this far from the last one sent.")]
        public float minMoveMm = 5f;
        [Min(0f), Tooltip("Smoothing of the hand position: seconds of lag at rest (0 = no smoothing). It becomes the One Euro filter's minimum cutoff 1 / (2 pi t).")]
        public float smoothingTime = 0.15f;
        [Min(0f), Tooltip("One Euro filter beta: how fast the smoothing relaxes when the hand moves fast (0 = constant smoothing).")]
        public float smoothingBeta = 0.02f;
        [Min(0.1f), Tooltip("One Euro filter cutoff (Hz) of the speed estimate.")]
        public float smoothingDerivativeCutoff = 1f;
        [Min(1f)] public float maxSpeedMmPerSec = 100f;

        [Header("Safety / control modes")]
        [Min(1f), Tooltip("ABSOLUTE mode only: the target moves at most this fast (mm/s). Keep hands clear of the robot.")]
        public float absoluteMaxSpeedMmPerSec = 30f;
        [Range(0.05f, 1f), Tooltip("Precision mode (left grip / A held): hand motion is scaled by this around where precision started.")]
        public float precisionScale = 0.3f;
        [Min(0.1f), Tooltip("Offset mode: distance in front of the technician where the control volume is placed (metres).")]
        public float controlVolumeDistance = 0.5f;

        [Header("Gem marker and pick / place macros (robot frame, mm)")]
        [Tooltip("Height of the table plane in the robot frame (the calibration points lie on it).")]
        public float tableZ = 0f;
        [Tooltip("Height at which the gripper closes on a gem (default zMin + 5 mm).")]
        public float pickZ = 5f;
        [Tooltip("Height at which the gripper opens to release a gem.")]
        public float placeZ = 5f;
        [Tooltip("Travel height between pick and place.")]
        public float safeZ = 80f;
        [Min(0f), Tooltip("Seconds to wait after the suction / gripper switches on or off.")]
        public float gripWaitSeconds = 0.5f;

        public Vector3[] ReferencePoints => new[] { p0, p1, p2 };

        public Vector3 WorkspaceMin => new Vector3(xMin, yMin, zMin);

        public Vector3 WorkspaceMax => new Vector3(xMax, yMax, zMax);

        public Vector3 ClampToWorkspace(Vector3 robotMm)
        {
            return new Vector3(Mathf.Clamp(robotMm.x, xMin, xMax), Mathf.Clamp(robotMm.y, yMin, yMax), Mathf.Clamp(robotMm.z, zMin, zMax));
        }

        private void OnValidate()
        {
            if (xMax < xMin) xMax = xMin;
            if (yMax < yMin) yMax = yMin;
            if (zMax < zMin) zMax = zMin;
        }
    }
}
