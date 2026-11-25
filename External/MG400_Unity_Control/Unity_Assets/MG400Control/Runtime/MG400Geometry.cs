using System;
using UnityEngine;

namespace CPE494
{
    /// <summary>
    /// MG400 kinematics constants + FK/IK, mirrored from python/mg400_driver.py.
    /// Base frame: +Y up, +Z "forward" at j1 = 0.  Lengths in METRES here
    /// (converted to mm at the bridge API boundary).
    /// NOTE: E2/E3 joint-zero offsets are our twin convention -- calibrate
    /// against the real arm before hardware day (README_CPE494.md).
    /// </summary>
    public static class MG400Geometry
    {
        // ------------------------------------------------------------------
        // geometry (metres / degrees) -- KEEP IN SYNC WITH mg400_driver.py
        // MG400 real kinematics: L2 = L3 = 175 mm, usable reach 220-440 mm
        // ------------------------------------------------------------------
        public const float L1 = 0.130f;   // base top -> J2 pivot height
        public const float O2 = 0.030f;   // J2 pivot forward offset (+Z)
        public const float L2 = 0.175f;   // rear arm length (J2 -> J3)
        public const float L3 = 0.175f;   // forearm length  (J3 -> J4)
        public const float L4 = 0.050f;   // J4 -> TCP (suction cup tip)
        public const float E2 = 90f;      // joint reading when rear arm horizontal
        public const float E3 = 90f;      // joint reading when forearm collinear

        // workspace clamp (mm, base frame) -- MG400 real usable reach
        public const float RMinMm = 220f, RMaxMm = 440f, ZMinMm = -20f, ZMaxMm = 450f;

        /// <summary>Joint angles (deg) -> TCP (x, y, z, r) in METRES (r in deg).</summary>
        public static Vector4 FK(Vector4 j)
        {
            float phi2 = j.y - E2;                    // rear arm elevation
            float phi3 = phi2 - (j.z - E3);           // forearm elevation
            float t2 = phi2 * Mathf.Deg2Rad;
            float t3 = phi3 * Mathf.Deg2Rad;
            float l3p = L3 + L4;                      // forearm + tool (collinear)

            float r = O2 + L2 * Mathf.Cos(t2) + l3p * Mathf.Cos(t3);
            float y = L1 + L2 * Mathf.Sin(t2) + l3p * Mathf.Sin(t3);

            float j1r = j.x * Mathf.Deg2Rad;
            return new Vector4(r * Mathf.Sin(j1r), y, r * Mathf.Cos(j1r), j.w);
        }

        /// <summary>
        /// TCP (x, y, z) in metres -> joint angles (j1, j2, j3).  Elbow-up.
        /// Points slightly outside the planar reach are pulled toward the
        /// shoulder instead of failing, so teleop always yields a valid pose.
        /// </summary>
        public static Vector3 IK(Vector3 tcp)
        {
            float r = Mathf.Sqrt(tcp.x * tcp.x + tcp.z * tcp.z);
            float j1 = Mathf.Atan2(tcp.x, tcp.z) * Mathf.Rad2Deg;

            float dx = r - O2;
            float dy = tcp.y - L1;
            float l3p = L3 + L4;
            float dMax = (L2 + l3p) * 0.999f;
            float dMin = Mathf.Abs(L2 - l3p) * 1.001f + 1e-5f;
            float d2 = dx * dx + dy * dy;
            float d = Mathf.Sqrt(d2);

            if (d < 1e-4f) d = 1e-4f;
            if (d > dMax || d < dMin)
            {
                // resolve unreachable targets by scaling toward/away from shoulder
                Vector2 dir = new Vector2(dx, dy) / d;
                float dc = Mathf.Clamp(d, dMin, dMax);
                dx = dir.x * dc;
                dy = dir.y * dc;
                d2 = dx * dx + dy * dy;
                d = dc;
            }

            float cosElbow = Mathf.Clamp((L2 * L2 + l3p * l3p - d2) / (2f * L2 * l3p), -1f, 1f);
            float cosShoulder = Mathf.Clamp((d2 + L2 * L2 - l3p * l3p) / (2f * d * L2), -1f, 1f);
            float thetaElbow = Mathf.Acos(cosElbow);
            float deltaShoulder = Mathf.Acos(cosShoulder);

            float gamma = Mathf.Atan2(dy, dx);
            float phi2 = gamma + deltaShoulder;                 // elbow-up configuration
            float phi3 = phi2 + Mathf.PI + thetaElbow;          // forearm bends down-forward
            phi2 = Mathf.Atan2(Mathf.Sin(phi2), Mathf.Cos(phi2));
            phi3 = Mathf.Atan2(Mathf.Sin(phi3), Mathf.Cos(phi3));

            return new Vector3(j1, phi2 * Mathf.Rad2Deg + E2, E3 - (phi3 - phi2) * Mathf.Rad2Deg);
        }

        /// <summary>Clamp a TCP point (metres) into the declared workspace box.</summary>
        public static Vector3 ClampWorkspace(Vector3 tcp)
        {
            float r = Mathf.Sqrt(tcp.x * tcp.x + tcp.z * tcp.z);
            r = Mathf.Clamp(r, RMinMm / 1000f, RMaxMm / 1000f);
            float yaw = Mathf.Atan2(tcp.x, tcp.z);
            float y = Mathf.Clamp(tcp.y, ZMinMm / 1000f, ZMaxMm / 1000f);
            return new Vector3(r * Mathf.Sin(yaw), y, r * Mathf.Cos(yaw));
        }

        public static bool InWorkspace(Vector3 tcp)
        {
            float r = Mathf.Sqrt(tcp.x * tcp.x + tcp.z * tcp.z);
            return r >= RMinMm / 1000f && r <= RMaxMm / 1000f
                && tcp.y >= ZMinMm / 1000f && tcp.y <= ZMaxMm / 1000f;
        }
    }

    /// <summary>Per-connection snapshot streamed by bridge_server.py (30 Hz).</summary>
    [Serializable]
    public class MG400State
    {
        public double t;
        public double[] joints;    // j1..j4 degrees
        public double[] pose;      // x, y, z, r in mm
        public int do1;            // vacuum on/off
        public int enabled;
        public bool sim;
        public bool moving;
        public bool outsideWorkspace;

        public float Joint(int i) => joints != null && joints.Length > i ? (float)joints[i] : 0f;
    }

    [Serializable]
    public class MG400Ack
    {
        public int id;
        public bool ok;
        public string msg;
    }
}
