using System.Collections.Generic;
using UnityEngine;

namespace GemStore.Showcase.Editor
{
    /// <summary>
    /// Builds low-poly, flat-shaded gem meshes (one vertex set + one normal per face).
    /// Units are metres; pivot is the centre of the bounding box.
    /// </summary>
    public static class GemMeshGenerator
    {
        /// <summary>Collects polygon faces and emits an un-indexed (flat shaded) mesh.</summary>
        private sealed class FlatShadedBuilder
        {
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<Vector3> normals = new List<Vector3>();
            private readonly List<int> triangles = new List<int>();
            private readonly Vector3 interior;

            /// <param name="interiorPoint">A point strictly inside the (convex) gem, used to orient face winding outward.</param>
            public FlatShadedBuilder(Vector3 interiorPoint)
            {
                interior = interiorPoint;
            }

            /// <summary>Adds a planar convex polygon; winding is fixed automatically so the face points outward.</summary>
            public void AddFace(params Vector3[] polygon)
            {
                int n = polygon.Length;
                Vector3 centroid = Vector3.zero;
                for (int i = 0; i < n; i++)
                {
                    centroid += polygon[i];
                }
                centroid /= n;

                Vector3 area = Vector3.zero;
                for (int i = 1; i < n - 1; i++)
                {
                    area += Vector3.Cross(polygon[i] - polygon[0], polygon[i + 1] - polygon[0]);
                }

                if (area.sqrMagnitude < 1e-14f)
                {
                    return;
                }

                // Unity front faces are clockwise, and Cross(b - a, c - a) points toward the viewer of such a face.
                bool flip = Vector3.Dot(area, centroid - interior) < 0f;
                Vector3 normal = (flip ? -area : area).normalized;

                int start = vertices.Count;
                for (int i = 0; i < n; i++)
                {
                    vertices.Add(polygon[flip ? n - 1 - i : i]);
                    normals.Add(normal);
                }

                for (int i = 1; i < n - 1; i++)
                {
                    triangles.Add(start);
                    triangles.Add(start + i);
                    triangles.Add(start + i + 1);
                }
            }

            public Mesh Build(string meshName)
            {
                Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                foreach (Vector3 v in vertices)
                {
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }

                Vector3 center = (min + max) * 0.5f;
                float footprint = Mathf.Max(max.x - min.x, max.z - min.z);

                var uvs = new List<Vector2>(vertices.Count);
                for (int i = 0; i < vertices.Count; i++)
                {
                    Vector3 p = vertices[i] - center;
                    vertices[i] = p;
                    uvs.Add(new Vector2(p.x / footprint + 0.5f, p.z / footprint + 0.5f));
                }

                var mesh = new Mesh { name = meshName };
                mesh.SetVertices(vertices);
                mesh.SetNormals(normals);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        private static Vector3 Polar(float radius, float angle, float y)
        {
            return new Vector3(radius * Mathf.Cos(angle), y, radius * Mathf.Sin(angle));
        }

        /// <summary>
        /// Round brilliant cut: flat table, 16-fold crown (star, bezel and upper-girdle facets),
        /// thin girdle band and a pointed pavilion. Width 4.5 cm.
        /// </summary>
        public static Mesh CreateRound()
        {
            const int sides = 16;
            const float diameter = 0.045f;

            float radius = diameter * 0.5f;
            float tableRadius = radius * 0.57f;
            float girdleHeight = diameter * 0.03f;
            float crownHeight = diameter * 0.16f;
            float pavilionDepth = diameter * 0.43f;

            float yGirdleBottom = 0f;
            float yGirdleTop = girdleHeight;
            float yTable = yGirdleTop + crownHeight;
            float yCulet = -pavilionDepth;

            float step = Mathf.PI * 2f / sides;
            float half = step * 0.5f;

            // Mid-ring heights are chosen so that the midpoint of each neighbouring pair lies on the line
            // between the table (or culet) vertex and the girdle vertex, which keeps the kite facets planar.
            float crownMidRadius = radius * 0.86f;
            float crownMidRho = crownMidRadius * Mathf.Cos(half);
            float yCrownMid = yTable + (yGirdleTop - yTable) * (crownMidRho - tableRadius) / (radius - tableRadius);

            float pavilionMidRadius = radius * 0.62f;
            float pavilionMidRho = pavilionMidRadius * Mathf.Cos(half);
            float yPavilionMid = yCulet + (yGirdleBottom - yCulet) * pavilionMidRho / radius;

            var table = new Vector3[sides];
            var girdleTop = new Vector3[sides];
            var girdleBottom = new Vector3[sides];
            var crownMid = new Vector3[sides];
            var pavilionMid = new Vector3[sides];
            for (int i = 0; i < sides; i++)
            {
                float a = i * step;
                float aMid = a + half;
                table[i] = Polar(tableRadius, a, yTable);
                girdleTop[i] = Polar(radius, a, yGirdleTop);
                girdleBottom[i] = Polar(radius, a, yGirdleBottom);
                crownMid[i] = Polar(crownMidRadius, aMid, yCrownMid);
                pavilionMid[i] = Polar(pavilionMidRadius, aMid, yPavilionMid);
            }

            Vector3 culet = new Vector3(0f, yCulet, 0f);
            var builder = new FlatShadedBuilder(new Vector3(0f, (yTable + yCulet) * 0.5f, 0f));

            builder.AddFace(table);

            for (int i = 0; i < sides; i++)
            {
                int next = (i + 1) % sides;
                int prev = (i + sides - 1) % sides;

                // Crown
                builder.AddFace(table[i], table[next], crownMid[i]);                         // star facet
                builder.AddFace(table[i], crownMid[prev], girdleTop[i], crownMid[i]);        // bezel (kite) facet
                builder.AddFace(crownMid[i], girdleTop[i], girdleTop[next]);                 // upper girdle facet

                // Girdle band
                builder.AddFace(girdleTop[i], girdleTop[next], girdleBottom[next], girdleBottom[i]);

                // Pavilion
                builder.AddFace(culet, pavilionMid[prev], girdleBottom[i], pavilionMid[i]);  // main (kite) facet
                builder.AddFace(pavilionMid[i], girdleBottom[next], girdleBottom[i]);        // lower girdle facet
            }

            return builder.Build("Gem_Round");
        }

        private static Vector3[] Octagon(float halfLength, float halfWidth, float cut, float scale, float y)
        {
            float hx = halfLength * scale;
            float hz = halfWidth * scale;
            float c = cut * scale;
            return new[]
            {
                new Vector3(hx, y, -hz + c),
                new Vector3(hx, y, hz - c),
                new Vector3(hx - c, y, hz),
                new Vector3(-hx + c, y, hz),
                new Vector3(-hx, y, hz - c),
                new Vector3(-hx, y, -hz + c),
                new Vector3(-hx + c, y, -hz),
                new Vector3(hx - c, y, -hz)
            };
        }

        private static void AddBand(FlatShadedBuilder builder, Vector3[] upper, Vector3[] lower)
        {
            for (int i = 0; i < upper.Length; i++)
            {
                int next = (i + 1) % upper.Length;
                builder.AddFace(upper[i], upper[next], lower[next], lower[i]);
            }
        }

        /// <summary>
        /// Emerald (step) cut: rectangle with chamfered corners, three stepped bands on the crown,
        /// a short girdle band and two stepped bands on the pavilion ending in a keel line. Length 4.5 cm.
        /// </summary>
        public static Mesh CreateEmerald()
        {
            const float length = 0.045f;
            const float width = 0.034f;

            float halfLength = length * 0.5f;
            float halfWidth = width * 0.5f;
            float cut = width * 0.22f;

            float yGirdleBottom = 0f;
            float yGirdleTop = 0.0016f;

            // Crown: girdle -> step 1 -> step 2 -> table.
            Vector3[] crown0 = Octagon(halfLength, halfWidth, cut, 1.00f, yGirdleTop);
            Vector3[] crown1 = Octagon(halfLength, halfWidth, cut, 0.86f, yGirdleTop + 0.0030f);
            Vector3[] crown2 = Octagon(halfLength, halfWidth, cut, 0.70f, yGirdleTop + 0.0058f);
            Vector3[] table = Octagon(halfLength, halfWidth, cut, 0.52f, yGirdleTop + 0.0074f);

            // Pavilion: girdle -> step 1 -> step 2 -> keel.
            Vector3[] pav0 = Octagon(halfLength, halfWidth, cut, 1.00f, yGirdleBottom);
            Vector3[] pav1 = Octagon(halfLength, halfWidth, cut, 0.80f, -0.0070f);
            Vector3[] pav2 = Octagon(halfLength, halfWidth, cut, 0.56f, -0.0138f);

            float yKeel = -0.0205f;
            float keelHalf = (halfLength - cut) * 0.56f;
            Vector3 keelRight = new Vector3(keelHalf, yKeel, 0f);
            Vector3 keelLeft = new Vector3(-keelHalf, yKeel, 0f);

            float yTable = yGirdleTop + 0.0074f;
            var builder = new FlatShadedBuilder(new Vector3(0f, (yTable + yKeel) * 0.5f, 0f));

            // Top
            builder.AddFace(table);
            AddBand(builder, table, crown2);
            AddBand(builder, crown2, crown1);
            AddBand(builder, crown1, crown0);

            // Girdle
            AddBand(builder, crown0, pav0);

            // Pavilion
            AddBand(builder, pav0, pav1);
            AddBand(builder, pav1, pav2);

            // Keel facets (octagon index layout: 0,1 = +X end, 2,3 = +Z side, 4,5 = -X end, 6,7 = -Z side)
            builder.AddFace(pav2[2], pav2[3], keelLeft, keelRight);
            builder.AddFace(pav2[6], pav2[7], keelRight, keelLeft);
            builder.AddFace(pav2[0], pav2[1], keelRight);
            builder.AddFace(pav2[1], pav2[2], keelRight);
            builder.AddFace(pav2[7], pav2[0], keelRight);
            builder.AddFace(pav2[4], pav2[5], keelLeft);
            builder.AddFace(pav2[3], pav2[4], keelLeft);
            builder.AddFace(pav2[5], pav2[6], keelLeft);

            return builder.Build("Gem_Emerald");
        }
    }
}
