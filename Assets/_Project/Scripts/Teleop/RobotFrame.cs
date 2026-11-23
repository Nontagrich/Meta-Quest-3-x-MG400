using UnityEngine;

namespace GemStore.Teleop
{
    /// <summary>
    /// Rigid transform between the Unity world (metres, left-handed, Y up) and the robot frame (millimetres, right-handed).
    /// Built from three point pairs.
    /// </summary>
    public sealed class RobotFrame
    {
        /// <summary>Millimetres per Unity metre.</summary>
        public const float MmPerMetre = 1000f;

        public Matrix4x4 UnityToRobot { get; private set; }
        public Matrix4x4 RobotToUnity { get; private set; }

        /// <summary>Distance in mm between each transformed Unity point and its robot reference point.</summary>
        public float[] ErrorsMm { get; private set; }

        public float MaxErrorMm
        {
            get
            {
                float max = 0f;
                foreach (float e in ErrorsMm) max = Mathf.Max(max, e);
                return max;
            }
        }

        public Vector3 ToRobot(Vector3 unityWorld) => UnityToRobot.MultiplyPoint3x4(unityWorld);

        public Vector3 ToUnity(Vector3 robotMm) => RobotToUnity.MultiplyPoint3x4(robotMm);

        /// <summary>Converts a direction (no translation).</summary>
        public Vector3 DirectionToUnity(Vector3 robotDirection) => RobotToUnity.MultiplyVector(robotDirection);

        /// <summary>
        /// Solves the transform. The orthonormal frame of each point set is X = P0->P1, Z = X x (P0->P2), Y = Z x X. Unity is left-handed,
        /// so its points are mirrored on Z first (the physical frames then have the same handedness). The rotation is the one that turns the Unity
        /// frame into the robot frame; the translation is fitted on the centroids, which spreads the error over the three points.
        /// </summary>
        public static bool TrySolve(Vector3[] unityMetres, Vector3[] robotMm, out RobotFrame frame, out string error)
        {
            frame = null;
            error = null;
            if (unityMetres == null || robotMm == null || unityMetres.Length != 3 || robotMm.Length != 3)
            {
                error = "Three point pairs are needed.";
                return false;
            }

            var mirrored = new Vector3[3];
            for (int i = 0; i < 3; i++)
            {
                mirrored[i] = new Vector3(unityMetres[i].x, unityMetres[i].y, -unityMetres[i].z);
            }

            if (!TryBasis(mirrored, out Vector3 ux, out Vector3 uy, out Vector3 uz) ||
                !TryBasis(robotMm, out Vector3 rx, out Vector3 ry, out Vector3 rz))
            {
                error = "The three points are on one line (or two are the same). Touch three different points that form a triangle.";
                return false;
            }

            // rotation = R_robot * R_unity^T (the bases are the matrix columns)
            Matrix4x4 robotBasis = Basis(rx, ry, rz);
            Matrix4x4 unityBasis = Basis(ux, uy, uz);
            Matrix4x4 rotation = robotBasis * unityBasis.transpose;

            // linear part: scale (m -> mm), rotation, and the Z mirror
            Matrix4x4 mirror = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
            Matrix4x4 linear = rotation * Matrix4x4.Scale(Vector3.one * MmPerMetre) * mirror; // the scale is uniform, so its position does not matter

            Vector3 unityCentroid = (unityMetres[0] + unityMetres[1] + unityMetres[2]) / 3f;
            Vector3 robotCentroid = (robotMm[0] + robotMm[1] + robotMm[2]) / 3f;
            Vector3 translation = robotCentroid - linear.MultiplyPoint3x4(unityCentroid);

            Matrix4x4 full = linear;
            full.m03 = translation.x;
            full.m13 = translation.y;
            full.m23 = translation.z;
            full.m33 = 1f;

            var result = new RobotFrame { UnityToRobot = full, RobotToUnity = full.inverse, ErrorsMm = new float[3] };
            for (int i = 0; i < 3; i++)
            {
                result.ErrorsMm[i] = (result.ToRobot(unityMetres[i]) - robotMm[i]).magnitude;
            }

            frame = result;
            return true;
        }

        private static bool TryBasis(Vector3[] p, out Vector3 x, out Vector3 y, out Vector3 z)
        {
            x = y = z = Vector3.zero;
            Vector3 a = p[1] - p[0];
            Vector3 b = p[2] - p[0];
            if (a.sqrMagnitude < 1e-12f || b.sqrMagnitude < 1e-12f)
            {
                return false;
            }

            x = a.normalized;
            Vector3 cross = Vector3.Cross(x, b);
            if (cross.magnitude / b.magnitude < 0.05f) // closer than ~3 degrees to a line
            {
                return false;
            }

            z = cross.normalized;
            y = Vector3.Cross(z, x);
            return true;
        }

        private static Matrix4x4 Basis(Vector3 x, Vector3 y, Vector3 z)
        {
            var m = Matrix4x4.identity;
            m.SetColumn(0, new Vector4(x.x, x.y, x.z, 0f));
            m.SetColumn(1, new Vector4(y.x, y.y, y.z, 0f));
            m.SetColumn(2, new Vector4(z.x, z.y, z.z, 0f));
            return m;
        }
    }
}
