using System.Text;
using TMPro;
using UnityEngine;

namespace GemStore.Teleop
{
    /// <summary>World-space status panel of the teleoperation: link, calibration, mode, deadman, target, gripper, gem marker, macros and E-STOP.</summary>
    public class TeleopHud : MonoBehaviour
    {
        [SerializeField] private TeleopController controller;
        [SerializeField] private TMP_Text statusLabel;

        private readonly StringBuilder builder = new StringBuilder(512);
        private float nextRefresh;

        private const string Ok = "#5BE37A";
        private const string Bad = "#FF5A4D";
        private const string Dim = "#B8C0D0";
        private const string Warn = "#FFB347";

        private void Update()
        {
            if (Time.unscaledTime < nextRefresh)
            {
                return;
            }

            nextRefresh = Time.unscaledTime + 0.1f;
            if (controller == null)
            {
                controller = TeleopController.Instance;
            }

            if (controller == null || statusLabel == null)
            {
                return;
            }

            builder.Clear();
            Line("Robot link", controller.LinkConnected ? "connected" : "OFFLINE", controller.LinkConnected ? Ok : Bad);
            Line("Calibrated", controller.Calibrated ? "yes" : "NO", controller.Calibrated ? Ok : Bad);
            Line("Mode", controller.Mode == ControlMode.Offset ? "OFFSET (safe)" : "ABSOLUTE", controller.Mode == ControlMode.Offset ? Ok : Warn);
            Line("Deadman", controller.Following ? "HELD - following" : (controller.DeadmanHeld ? "held (blocked)" : "released"), controller.Following ? Ok : Dim);

            if (controller.PrecisionActive)
            {
                Line("Precision", "PRECISION x" + controller.CurrentPrecisionScale.ToString("0.0"), Warn);
            }

            if (controller.Following)
            {
                Vector3 t = controller.TargetRobotMm;
                Line("Target mm", $"x {t.x:F0}  y {t.y:F0}  z {t.z:F0}", controller.Clamped ? Bad : Ok);
            }
            else
            {
                Line("Target mm", "-", Dim);
            }

            if (controller.HasRobotPose)
            {
                Vector3 p = controller.RobotPoseMm;
                Line("Robot mm", $"x {p.x:F0}  y {p.y:F0}  z {p.z:F0}", "#6FB2FF");
            }
            else
            {
                Line("Robot mm", "no pose", Dim);
            }

            Line("Gripper", controller.GripOn ? "CLOSED / suction ON" : "open / off", controller.GripOn ? Ok : Dim);

            if (controller.HasGem)
            {
                Vector3 g = controller.GemRobotMm;
                Line("Gem mm", $"x {g.x:F0}  y {g.y:F0}  z {g.z:F0}" + (controller.GemOutOfReach ? " (out of reach)" : string.Empty), controller.GemOutOfReach ? Bad : "#FFD633");
            }
            else
            {
                Line("Gem mm", controller.Marking ? "aim + trigger..." : "not marked", controller.Marking ? Warn : Dim);
            }

            Line("E-STOP", controller.EStopLocked ? "ACTIVE - press Reset" : "ok", controller.EStopLocked ? Bad : Ok);

            if (!string.IsNullOrEmpty(controller.MacroStatus))
            {
                Note(controller.MacroStatus, controller.MacroActive ? Warn : Dim);
            }

            if (!string.IsNullOrEmpty(controller.MarkMessage))
            {
                Note(controller.MarkMessage, Dim);
            }

            if (controller.Mode == ControlMode.Absolute)
            {
                Note("Keep hands clear of the robot (speed capped " + controller.SpeedLimitMmPerSec.ToString("0") + " mm/s)", Bad);
            }
            else if (controller.Volume != null && controller.Volume.TooCloseToRobot)
            {
                Note("Control volume is close to the robot: move it away", Bad);
            }

            if (controller.Clamped)
            {
                Note("Target limited to the workspace", Bad);
            }

            if (!string.IsNullOrEmpty(controller.BlockedReason))
            {
                Note("Blocked: " + controller.BlockedReason, Bad);
            }

            statusLabel.text = builder.ToString();
        }

        private void Line(string name, string value, string color)
        {
            builder.Append("<color=").Append(Dim).Append(">").Append(name).Append(":</color> <color=").Append(color).Append("><b>")
                .Append(value).Append("</b></color>\n");
        }

        private void Note(string text, string color)
        {
            builder.Append("<color=").Append(color).Append("><b>").Append(text).Append("</b></color>\n");
        }
    }
}
