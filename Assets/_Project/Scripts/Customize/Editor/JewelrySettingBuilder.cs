using System.Collections.Generic;
using GemStore.Showcase;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GemStore.Customize.Editor
{
    /// <summary>
    /// Builds the low-poly ring and pendant settings (real size: a 17 mm ring around a 9 mm stone, a ~45 cm chain), the metal materials,
    /// the SettingCatalog asset, and adds JewelryPreview to the two gem prefabs. Re-running it rebuilds everything in place.
    /// </summary>
    public static class JewelrySettingBuilder
    {
        private const string ModelsFolder = "Assets/_Project/Models/Settings";
        private const string MaterialsFolder = "Assets/_Project/Materials/Jewelry";
        private const string CatalogPath = "Assets/_Project/Data/SettingCatalog.asset";
        private const float StoneScale = 0.2f; // 45 mm showcase gem -> 9 mm stone

        [MenuItem("GemStore/Build Jewelry Settings")]
        public static void Build()
        {
            EnsureFolder(ModelsFolder);
            EnsureFolder(MaterialsFolder);

            Material silver = MetalMaterial("Metal_Silver", new Color(0.84f, 0.85f, 0.88f));
            Material gold = MetalMaterial("Metal_Gold", new Color(1.00f, 0.77f, 0.34f));
            Material rose = MetalMaterial("Metal_RoseGold", new Color(0.93f, 0.58f, 0.48f));

            var catalog = AssetDatabase.LoadAssetAtPath<SettingCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<SettingCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            foreach (string shape in new[] { "Round", "Emerald" })
            {
                var gemMesh = AssetDatabase.LoadAssetAtPath<Mesh>($"Assets/_Project/Models/Gem_{shape}.asset");
                GemProfile gem = GemProfile.Read(gemMesh);
                Mesh ring = Save(BuildRing(gem), $"{ModelsFolder}/Ring_{shape}.asset");
                Mesh pendant = Save(BuildPendant(gem), $"{ModelsFolder}/Pendant_{shape}.asset");
                Debug.Log($"GemStore: {shape}: ring {ring.triangles.Length / 3} tris, pendant {pendant.triangles.Length / 3} tris.");
                AttachToPrefab($"Assets/_Project/Prefabs/Gem_{shape}.prefab", ring, pendant, new[] { silver, gold, rose }, catalog);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        // ------------------------------------------------------------------------------------------------------
        // Gem measurements (read from the gem mesh, scaled to the jewelry-size stone)
        // ------------------------------------------------------------------------------------------------------

        private struct GemProfile
        {
            public float culetY, girdleBottomY, girdleTopY, tableY;
            public float halfX, halfZ;                 // girdle half extents
            public Vector2[] corners;                  // girdle corner (x,z) for (+,+) (-,+) (-,-) (+,-)

            public static GemProfile Read(Mesh mesh)
            {
                Vector3[] verts = mesh.vertices;
                float maxX = 0f, maxZ = 0f, minY = float.MaxValue, maxY = float.MinValue;
                foreach (Vector3 v in verts)
                {
                    maxX = Mathf.Max(maxX, Mathf.Abs(v.x));
                    maxZ = Mathf.Max(maxZ, Mathf.Abs(v.z));
                    minY = Mathf.Min(minY, v.y);
                    maxY = Mathf.Max(maxY, v.y);
                }

                // girdle = the vertex rings with the widest X extent
                float girdleLow = float.MaxValue, girdleHigh = float.MinValue;
                foreach (Vector3 v in verts)
                {
                    if (Mathf.Abs(v.x) >= maxX * 0.995f)
                    {
                        girdleLow = Mathf.Min(girdleLow, v.y);
                        girdleHigh = Mathf.Max(girdleHigh, v.y);
                    }
                }

                var profile = new GemProfile
                {
                    culetY = minY * StoneScale,
                    tableY = maxY * StoneScale,
                    girdleBottomY = girdleLow * StoneScale,
                    girdleTopY = girdleHigh * StoneScale,
                    halfX = maxX * StoneScale,
                    halfZ = maxZ * StoneScale,
                    corners = new Vector2[4]
                };

                Vector2[] signs = { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(-1, -1), new Vector2(1, -1) };
                for (int i = 0; i < 4; i++)
                {
                    float best = float.MinValue;
                    foreach (Vector3 v in verts)
                    {
                        if (v.y < girdleLow - 1e-5f || v.y > girdleHigh + 1e-5f)
                        {
                            continue;
                        }

                        float score = signs[i].x * v.x / maxX + signs[i].y * v.z / maxZ;
                        if (score > best)
                        {
                            best = score;
                            profile.corners[i] = new Vector2(v.x, v.z) * StoneScale;
                        }
                    }
                }

                return profile;
            }

            public float GirdleMidY => (girdleBottomY + girdleTopY) * 0.5f;
        }

        // ------------------------------------------------------------------------------------------------------
        // Models
        // ------------------------------------------------------------------------------------------------------

        private static Mesh BuildRing(GemProfile g)
        {
            var b = new MeshBuilder();
            AddHead(b, g, true);

            // basket plate, shank and band. The band is a ring around the X axis (the finger runs along X).
            float basketY = g.culetY - 0.0017f;
            b.AddLathe(new[] { new Vector2(0f, basketY - 0.0003f), new Vector2(0.0022f, basketY - 0.0003f), new Vector2(0.0022f, basketY + 0.0003f), new Vector2(0f, basketY + 0.0003f) },
                10, g.halfX / 0.0045f, g.halfZ / 0.0045f);
            float bandTop = g.culetY - 0.0033f;
            b.AddLathe(new[] { new Vector2(0f, bandTop), new Vector2(0.0011f, bandTop), new Vector2(0.0011f, basketY - 0.0003f), new Vector2(0f, basketY - 0.0003f) }, 8, 1f, 1f);

            const float innerRadius = 0.0085f;   // 17 mm inner diameter
            const float radial = 0.0009f, axial = 0.00175f;
            float major = innerRadius + radial;
            float centerY = bandTop - major - radial;
            b.AddTorus(new Vector3(0f, centerY, 0f), Vector3.right, major, radial, axial, 20, 6);
            return b.ToMesh("Ring");
        }

        private static Mesh BuildPendant(GemProfile g)
        {
            var b = new MeshBuilder();
            AddHead(b, g, false);

            // cup under the stone: outer shell and inner shell (unit radius 1 = girdle half extent)
            var outer = new List<Vector2>();
            var inner = new List<Vector2>();
            outer.Add(new Vector2(0f, g.culetY - 0.0008f));
            inner.Add(new Vector2(0f, g.culetY - 0.0004f));
            foreach (float t in new[] { 0.15f, 0.45f, 0.8f, 1.0f })
            {
                float y = Mathf.Lerp(g.culetY, g.girdleBottomY, t);
                outer.Add(new Vector2((t + 0.16f) * 0.0045f, y - 0.0002f));
                inner.Add(new Vector2((t + 0.07f) * 0.0045f, y));
            }

            b.AddLatheShell(outer.ToArray(), inner.ToArray(), 10, g.halfX / 0.0045f, g.halfZ / 0.0045f);

            // bail on the +Z side and the chain loop through it (lying flat, ~45 cm long)
            float bailZ = g.halfZ + 0.0016f;
            float bailY = g.GirdleMidY;
            b.AddTorus(new Vector3(0f, bailY, bailZ), Vector3.right, 0.0014f, 0.00035f, 0.00035f, 12, 4);
            b.AddTube(new[] { new Vector3(0f, bailY, g.halfZ - 0.0003f), new Vector3(0f, bailY, bailZ - 0.0010f) }, 0.00045f, 4, false);

            const float a = 0.085f, c = 0.065f; // ellipse semi axes: circumference ~ 47 cm
            float zc = bailZ + 0.0014f + 0.0004f + c;
            var chain = new List<Vector3>();
            const int segments = 36;
            for (int i = 0; i < segments; i++)
            {
                float phi = i * Mathf.PI * 2f / segments;
                chain.Add(new Vector3(a * Mathf.Sin(phi), bailY, zc - c * Mathf.Cos(phi)));
            }

            b.AddTube(chain.ToArray(), 0.0006f, 4, true);
            return b.ToMesh("Pendant");
        }

        /// <summary>Four prongs at the girdle corners (a rectangle for the emerald cut) and a rail that ties them together.</summary>
        private static void AddHead(MeshBuilder b, GemProfile g, bool withRail)
        {
            float pavilionMidY = Mathf.Lerp(g.culetY, g.girdleBottomY, 0.62f);
            var rail = new List<Vector3>();
            foreach (Vector2 corner in g.corners)
            {
                var path = new[]
                {
                    new Vector3(corner.x * 0.35f, g.culetY - 0.0015f, corner.y * 0.35f),
                    new Vector3(corner.x * 0.80f, pavilionMidY, corner.y * 0.80f),
                    new Vector3(corner.x * 1.08f, g.GirdleMidY, corner.y * 1.08f),
                    new Vector3(corner.x * 0.97f, g.girdleTopY + 0.0006f, corner.y * 0.97f)
                };
                b.AddTube(path, 0.0004f, 4, false);
                rail.Add(path[1]);
            }

            if (withRail)
            {
                b.AddTube(rail.ToArray(), 0.00022f, 4, true);
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Mesh helper
        // ------------------------------------------------------------------------------------------------------

        private sealed class MeshBuilder
        {
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<int> triangles = new List<int>();

            // Unity draws a triangle when cross(b - a, c - a) points to the viewer: flip it when it points into the shape.
            private void Tri(int a, int b, int c, Vector3 outward)
            {
                Vector3 normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                if (Vector3.Dot(normal, outward) < 0f)
                {
                    (b, c) = (c, b);
                }

                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(c);
            }

            /// <summary>Round tube along a path (closed = loop). Open tubes get flat caps.</summary>
            public void AddTube(Vector3[] path, float radius, int sides, bool closed)
            {
                int n = path.Length;
                int start = vertices.Count;
                Vector3 normal = Vector3.zero;
                var outward = new Vector3[n * sides];
                for (int i = 0; i < n; i++)
                {
                    Vector3 prev = path[closed ? (i - 1 + n) % n : Mathf.Max(i - 1, 0)];
                    Vector3 next = path[closed ? (i + 1) % n : Mathf.Min(i + 1, n - 1)];
                    Vector3 tangent = (next - prev).normalized;
                    if (i == 0)
                    {
                        normal = Vector3.Cross(tangent, Mathf.Abs(tangent.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                    }

                    normal = (normal - tangent * Vector3.Dot(normal, tangent)).normalized;
                    Vector3 binormal = Vector3.Cross(tangent, normal);
                    for (int k = 0; k < sides; k++)
                    {
                        float angle = k * Mathf.PI * 2f / sides + Mathf.PI / sides;
                        Vector3 direction = Mathf.Cos(angle) * normal + Mathf.Sin(angle) * binormal;
                        outward[i * sides + k] = direction;
                        vertices.Add(path[i] + direction * radius);
                    }
                }

                int rings = closed ? n : n - 1;
                for (int i = 0; i < rings; i++)
                {
                    int i1 = (i + 1) % n;
                    for (int k = 0; k < sides; k++)
                    {
                        int k1 = (k + 1) % sides;
                        int a = start + i * sides + k, bb = start + i1 * sides + k, c = start + i1 * sides + k1, d = start + i * sides + k1;
                        Vector3 o = outward[i * sides + k] + outward[i * sides + k1];
                        Tri(a, bb, c, o);
                        Tri(a, c, d, o);
                    }
                }

                if (!closed)
                {
                    AddCap(start, sides, path[0], (path[0] - path[1]).normalized);
                    AddCap(start + (n - 1) * sides, sides, path[n - 1], (path[n - 1] - path[n - 2]).normalized);
                }
            }

            private void AddCap(int firstIndex, int sides, Vector3 center, Vector3 outward)
            {
                int centerIndex = vertices.Count;
                vertices.Add(center);
                for (int k = 0; k < sides; k++)
                {
                    Tri(centerIndex, firstIndex + k, firstIndex + (k + 1) % sides, outward);
                }
            }

            /// <summary>Torus around <paramref name="axis"/> with an elliptical section (radial / axial radius).</summary>
            public void AddTorus(Vector3 center, Vector3 axis, float major, float radial, float axial, int majorSegments, int minorSegments)
            {
                axis.Normalize();
                Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.forward).normalized;
                Vector3 v = Vector3.Cross(axis, u);
                int start = vertices.Count;
                var outward = new Vector3[majorSegments * minorSegments];
                for (int i = 0; i < majorSegments; i++)
                {
                    float phi = i * Mathf.PI * 2f / majorSegments;
                    Vector3 r = Mathf.Cos(phi) * u + Mathf.Sin(phi) * v;
                    for (int j = 0; j < minorSegments; j++)
                    {
                        float theta = j * Mathf.PI * 2f / minorSegments;
                        Vector3 dir = Mathf.Cos(theta) * r + Mathf.Sin(theta) * axis;
                        outward[i * minorSegments + j] = dir;
                        vertices.Add(center + major * r + Mathf.Cos(theta) * radial * r + Mathf.Sin(theta) * axial * axis);
                    }
                }

                for (int i = 0; i < majorSegments; i++)
                {
                    int i1 = (i + 1) % majorSegments;
                    for (int j = 0; j < minorSegments; j++)
                    {
                        int j1 = (j + 1) % minorSegments;
                        int a = start + i * minorSegments + j, b = start + i1 * minorSegments + j, c = start + i1 * minorSegments + j1, d = start + i * minorSegments + j1;
                        Vector3 o = outward[i * minorSegments + j] + outward[i * minorSegments + j1];
                        Tri(a, b, c, o);
                        Tri(a, c, d, o);
                    }
                }
            }

            /// <summary>Solid of revolution around Y from a (radius, y) profile (first and last point on the axis give closed ends).</summary>
            public void AddLathe(Vector2[] profile, int segments, float scaleX, float scaleZ)
            {
                int start = vertices.Count;
                for (int p = 0; p < profile.Length; p++)
                {
                    for (int s = 0; s < segments; s++)
                    {
                        float angle = s * Mathf.PI * 2f / segments;
                        vertices.Add(new Vector3(Mathf.Cos(angle) * profile[p].x * scaleX, profile[p].y, Mathf.Sin(angle) * profile[p].x * scaleZ));
                    }
                }

                Vector3 centroid = Centroid(start);
                for (int p = 0; p < profile.Length - 1; p++)
                {
                    for (int s = 0; s < segments; s++)
                    {
                        int s1 = (s + 1) % segments;
                        int a = start + p * segments + s, b = start + (p + 1) * segments + s, c = start + (p + 1) * segments + s1, d = start + p * segments + s1;
                        Vector3 o = (vertices[a] + vertices[b] + vertices[c] + vertices[d]) * 0.25f - centroid;
                        Tri(a, b, c, o);
                        Tri(a, c, d, o);
                    }
                }
            }

            /// <summary>Thin open cup: an outer shell, an inner shell facing inwards and a rim.</summary>
            public void AddLatheShell(Vector2[] outer, Vector2[] inner, int segments, float scaleX, float scaleZ)
            {
                int outerStart = vertices.Count;
                AddLathe(outer, segments, scaleX, scaleZ);
                int innerStart = vertices.Count;
                for (int p = 0; p < inner.Length; p++)
                {
                    for (int s = 0; s < segments; s++)
                    {
                        float angle = s * Mathf.PI * 2f / segments;
                        vertices.Add(new Vector3(Mathf.Cos(angle) * inner[p].x * scaleX, inner[p].y, Mathf.Sin(angle) * inner[p].x * scaleZ));
                    }
                }

                var axisPoint = new Vector3(0f, (outer[0].y + outer[outer.Length - 1].y) * 0.5f, 0f);
                for (int p = 0; p < inner.Length - 1; p++)
                {
                    for (int s = 0; s < segments; s++)
                    {
                        int s1 = (s + 1) % segments;
                        int a = innerStart + p * segments + s, b = innerStart + (p + 1) * segments + s, c = innerStart + (p + 1) * segments + s1, d = innerStart + p * segments + s1;
                        Vector3 o = axisPoint - (vertices[a] + vertices[b] + vertices[c] + vertices[d]) * 0.25f; // facing the axis
                        o.y = 0f;
                        Tri(a, b, c, o);
                        Tri(a, c, d, o);
                    }
                }

                // rim between the last outer and inner rows
                int outerTop = outerStart + (outer.Length - 1) * segments;
                int innerTop = innerStart + (inner.Length - 1) * segments;
                for (int s = 0; s < segments; s++)
                {
                    int s1 = (s + 1) % segments;
                    Tri(outerTop + s, innerTop + s, innerTop + s1, Vector3.up);
                    Tri(outerTop + s, innerTop + s1, outerTop + s1, Vector3.up);
                }
            }

            private Vector3 Centroid(int start)
            {
                Vector3 sum = Vector3.zero;
                for (int i = start; i < vertices.Count; i++)
                {
                    sum += vertices[i];
                }

                return sum / Mathf.Max(1, vertices.Count - start);
            }

            public Mesh ToMesh(string name)
            {
                var mesh = new Mesh { name = name };
                mesh.SetVertices(vertices);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                Unwrapping.GenerateSecondaryUVSet(mesh);
                return mesh;
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Assets
        // ------------------------------------------------------------------------------------------------------

        private static Mesh Save(Mesh mesh, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(mesh, existing);
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(mesh);
                return existing;
            }

            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        private static Material MetalMaterial(string name, Color color)
        {
            string path = $"{MaterialsFolder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, path);
            }

            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", 1f);
            material.SetFloat("_Smoothness", 0.85f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void AttachToPrefab(string prefabPath, Mesh ring, Mesh pendant, Material[] metals, SettingCatalog catalog)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Transform old = root.transform.Find("Setting");
                if (old != null)
                {
                    Object.DestroyImmediate(old.gameObject);
                }

                foreach (JewelryPreview oldPreview in root.GetComponents<JewelryPreview>())
                {
                    Object.DestroyImmediate(oldPreview);
                }

                var setting = new GameObject("Setting");
                setting.transform.SetParent(root.transform, false);
                var filter = setting.AddComponent<MeshFilter>();
                var renderer = setting.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = metals[1];
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.enabled = false; // no collider on the setting: the gem's own collider stays the only one

                var preview = root.AddComponent<JewelryPreview>();
                var so = new SerializedObject(preview);
                so.FindProperty("settingFilter").objectReferenceValue = filter;
                so.FindProperty("settingRenderer").objectReferenceValue = renderer;
                so.FindProperty("ringMesh").objectReferenceValue = ring;
                so.FindProperty("pendantMesh").objectReferenceValue = pendant;
                SerializedProperty array = so.FindProperty("metalMaterials");
                array.arraySize = metals.Length;
                for (int i = 0; i < metals.Length; i++)
                {
                    array.GetArrayElementAtIndex(i).objectReferenceValue = metals[i];
                }

                so.FindProperty("catalog").objectReferenceValue = catalog;
                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            int slash = path.LastIndexOf('/');
            string parent = path.Substring(0, slash);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
        }
    }
}
