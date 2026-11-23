using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace GemStore.Showcase.Editor
{
    /// <summary>
    /// Baked lighting for the boutique, tuned for Meta Quest 3: all architectural lights are Baked (free at runtime), exactly one
    /// realtime light (the main directional, no shadows) lights the things that move (gems, card, hands), light probes cover the
    /// play area and two box-projected reflection probes give the gems their glints.
    /// Run order: Arrange Store, Decorate Store (Luxury), Setup Store Lighting, Bake Store Lighting.
    /// </summary>
    public static class StoreLightingBuilder
    {
        private const string MaterialsFolder = "Assets/_Project/Materials/Store";
        private const string SettingsPath = "Assets/_Project/Scenes/Storefront_LightingSettings.lighting";
        private const string GroupName = "Lighting";

        // Warm white, 3000-3500 K: ceiling and cabinet 3500, wall wash 3300, counter LED strip 3000.
        private const float CeilingKelvin = 3500f, WashKelvin = 3300f, CabinetKelvin = 3500f, StripKelvin = 3000f, MainKelvin = 3500f;

        // Intensities (linear). Tuned by looking at the bake; the cabinet light is the strongest on purpose.
        public static float CeilingIntensity = 1.3f;
        public static float WashIntensity = 1.7f;
        public static float CabinetIntensity = 4.5f;
        public static float StripIntensity = 1.2f;
        public static float MainIntensity = 0.35f;

        [MenuItem("GemStore/Setup Store Lighting")]
        public static void Setup()
        {
            Transform store = GameObject.Find("Store")?.transform;
            if (store == null)
            {
                Debug.LogError("GemStore: no 'Store' in the scene. Run Arrange Store and Decorate Store first.");
                return;
            }

            Transform old = store.Find(GroupName);
            if (old != null)
            {
                Object.DestroyImmediate(old.gameObject);
            }

            var group = new GameObject(GroupName).transform;
            group.SetParent(store, false);

            GraphicsSettings.lightsUseColorTemperature = true;
            Material led = EmissiveMaterial("Store_LED", new Color(1f, 0.70f, 0.38f), new Color(1.0f, 0.55f, 0.22f) * 2.2f);
            Material ledCabinet = EmissiveMaterial("Store_LED_Cabinet", new Color(1f, 0.86f, 0.62f), new Color(1.0f, 0.80f, 0.50f) * 4.0f);
            Material downlight = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialsFolder}/Store_Light.mat");

            ConfigureMainLight(group);
            ConfigureExistingSpots(group);
            BuildCeilingLights(group);
            BuildWallWash(group);
            BuildCabinetLight(store, group, ledCabinet);
            BuildCounterStrips(store, group, led);
            BuildLightProbes(group);
            BuildReflectionProbes(group);
            MakeStaticRenderersBake(store);
            ConfigureEnvironment();
            ConfigureLightingSettings();
            ConfigureQuestPipeline();

            if (downlight != null)
            {
                downlight.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
                EditorUtility.SetDirty(downlight);
            }

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("GemStore: store lighting set up (not baked yet: run GemStore/Bake Store Lighting).");
        }

        [MenuItem("GemStore/Bake Store Lighting")]
        public static void Bake()
        {
            SessionState.SetFloat("GemStore.BakeStart", (float)EditorApplication.timeSinceStartup);
            Lightmapping.BakeAsync();
        }

        // ------------------------------------------------------------------------------------------------------

        private static void ConfigureMainLight(Transform group)
        {
            // The only realtime light: dim warm main light for dynamic objects, no shadows.
            Light main = null;
            foreach (Light light in Object.FindObjectsByType<Light>())
            {
                if (light.type == LightType.Directional)
                {
                    main = light;
                    break;
                }
            }

            if (main == null)
            {
                var go = new GameObject("Main Light (realtime)");
                main = go.AddComponent<Light>();
                main.type = LightType.Directional;
                go.transform.rotation = Quaternion.Euler(50f, 330f, 0f);
            }

            main.transform.SetParent(group, true);
            main.name = "Main Light (realtime)";
            main.lightmapBakeType = LightmapBakeType.Realtime;
            main.shadows = LightShadows.None;
            main.useColorTemperature = true;
            main.colorTemperature = MainKelvin;
            main.color = Color.white;
            main.intensity = MainIntensity;
        }

        private static void ConfigureExistingSpots(Transform group)
        {
            // The two old cabinet spot lights become baked accent lights (no runtime cost).
            foreach (Light light in Object.FindObjectsByType<Light>())
            {
                if (light.type != LightType.Spot || !light.name.StartsWith("Spot_"))
                {
                    continue;
                }

                // they live inside the ShowcaseCabinet prefab instance, so they are configured in place (not reparented)
                light.lightmapBakeType = LightmapBakeType.Baked;
                light.shadows = LightShadows.Soft;
                light.useColorTemperature = true;
                light.colorTemperature = CabinetKelvin;
                light.color = Color.white;
                light.intensity = 1.2f;
            }
        }

        private static void BuildCeilingLights(Transform group)
        {
            Transform ceiling = New(group, "CeilingLights");
            Vector2[] points = { new Vector2(-1.3f, 0.2f), new Vector2(1.3f, 0.2f), new Vector2(-1.3f, 1.9f), new Vector2(1.3f, 1.9f), new Vector2(0f, 1.0f) };
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 position = new Vector3(points[i].x, 2.96f, points[i].y);
                Spot(ceiling, "Downlight_" + i, position, position + Vector3.down, CeilingIntensity, 4.2f, 115f, 55f, CeilingKelvin);
            }
        }

        private static void BuildWallWash(Transform group)
        {
            // Spots close to the wall, aimed along it, so the walls get scallops of light (depth) instead of flat fill.
            Transform wash = New(group, "WallWash");
            foreach (float x in new[] { -1.8f, -0.6f, 0.6f, 1.8f })
            {
                Vector3 position = new Vector3(x, 2.9f, 2.55f);
                Spot(wash, "Wash_Front_" + x.ToString("F1"), position, new Vector3(x, 1.2f, 3.0f), WashIntensity, 4.2f, 75f, 25f, WashKelvin);
            }

            foreach (float side in new[] { -1f, 1f })
            {
                foreach (float z in new[] { -0.7f, 0.8f, 2.2f })
                {
                    Vector3 position = new Vector3(side * 2.1f, 2.9f, z);
                    Spot(wash, $"Wash_{(side < 0 ? "Left" : "Right")}_{z:F1}", position, new Vector3(side * 2.5f, 1.2f, z), WashIntensity * 0.82f, 3.8f, 75f, 25f, WashKelvin);
                }
            }
        }

        private static void BuildCabinetLight(Transform store, Transform group, Material led)
        {
            // Light strip under the cabinet roof and the light that goes with it: the brightest thing in the shop.
            Transform showcase = store.Find("Showcase");
            Transform cabinetLight = New(group, "CabinetLight");
            Vector3 position = new Vector3(0f, 1.60f, 0.52f);
            Spot(cabinetLight, "Cabinet_Downlight", position, new Vector3(0f, 1.2f, 0.72f), CabinetIntensity, 1.6f, 100f, 40f, CabinetKelvin);
            Spot(cabinetLight, "Cabinet_Fill", new Vector3(0f, 1.0f, 0.12f), new Vector3(0f, 1.3f, 0.72f), 1.0f, 1.4f, 70f, 30f, CabinetKelvin);

            if (showcase != null)
            {
                Transform old = showcase.Find("Lux_CabinetStrip");
                if (old != null)
                {
                    Object.DestroyImmediate(old.gameObject);
                }

                GameObject strip = GameObject.CreatePrimitive(PrimitiveType.Cube);
                strip.name = "Lux_CabinetStrip";
                strip.transform.SetParent(showcase, false);
                strip.transform.localPosition = new Vector3(0f, 1.62f, 0.56f);
                strip.transform.localScale = new Vector3(0.44f, 0.008f, 0.016f);
                Object.DestroyImmediate(strip.GetComponent<Collider>());
                strip.GetComponent<MeshRenderer>().sharedMaterial = led;
                strip.GetComponent<MeshRenderer>().receiveShadows = false;
                GameObjectUtility.SetStaticEditorFlags(strip, StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI | StaticEditorFlags.ReflectionProbeStatic | StaticEditorFlags.OccludeeStatic);
            }
        }

        private static void BuildCounterStrips(Transform store, Transform group, Material led)
        {
            Transform checkout = store.Find("Checkout");
            Transform counter = FindDeep(checkout, "CheckoutCounter");
            Transform body = counter != null ? FindDeep(counter, "Body") : null;
            if (body == null)
            {
                Debug.LogWarning("GemStore: counter body not found, LED strips skipped.");
                return;
            }

            foreach (Transform t in checkout.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Lux_CounterStrip")
                {
                    Object.DestroyImmediate(t.gameObject);
                    break;
                }
            }

            Transform strips = New(checkout, "Lux_CounterStrip");
            Transform lights = New(group, "CounterLights");
            Vector3 s = body.localScale;
            Quaternion rot = body.rotation;
            float drop = 0.07f; // below the top edge of the body, hidden by the overhang of the counter top

            // front (towards the user = local -Z) and the two sides
            AddStrip(strips, led, "Front", body.TransformPoint(new Vector3(0f, 0.5f, -0.5f)) + rot * new Vector3(0f, -drop, -0.004f), rot, new Vector3(s.x - 0.12f, 0.014f, 0.008f));
            AddStrip(strips, led, "Left", body.TransformPoint(new Vector3(-0.5f, 0.5f, 0f)) + rot * new Vector3(-0.004f, -drop, 0f), rot, new Vector3(0.008f, 0.014f, s.z - 0.12f));
            AddStrip(strips, led, "Right", body.TransformPoint(new Vector3(0.5f, 0.5f, 0f)) + rot * new Vector3(0.004f, -drop, 0f), rot, new Vector3(0.008f, 0.014f, s.z - 0.12f));

            Vector3 down = Vector3.down;
            Vector3 front = body.TransformPoint(new Vector3(0f, 0.5f, -0.5f)) + rot * new Vector3(0f, -drop - 0.03f, -0.05f);
            Spot(lights, "Counter_Front", front, front + down, StripIntensity, 1.4f, 150f, 80f, StripKelvin);
            Vector3 left = body.TransformPoint(new Vector3(-0.5f, 0.5f, 0f)) + rot * new Vector3(-0.05f, -drop - 0.03f, 0f);
            Spot(lights, "Counter_Left", left, left + down, StripIntensity, 1.2f, 150f, 80f, StripKelvin);
            Vector3 right = body.TransformPoint(new Vector3(0.5f, 0.5f, 0f)) + rot * new Vector3(0.05f, -drop - 0.03f, 0f);
            Spot(lights, "Counter_Right", right, right + down, StripIntensity, 1.2f, 150f, 80f, StripKelvin);
        }

        private static void AddStrip(Transform parent, Material led, string name, Vector3 position, Quaternion rotation, Vector3 size)
        {
            GameObject strip = GameObject.CreatePrimitive(PrimitiveType.Cube);
            strip.name = "Strip_" + name;
            strip.transform.SetParent(parent, false);
            strip.transform.SetPositionAndRotation(position, rotation);
            strip.transform.localScale = size;
            Object.DestroyImmediate(strip.GetComponent<Collider>());
            var renderer = strip.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = led;
            renderer.receiveShadows = false;
            GameObjectUtility.SetStaticEditorFlags(strip, StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI | StaticEditorFlags.ReflectionProbeStatic | StaticEditorFlags.OccludeeStatic);
        }

        private static void BuildLightProbes(Transform group)
        {
            var go = new GameObject("LightProbes");
            go.transform.SetParent(group, false);
            var probes = go.AddComponent<LightProbeGroup>();
            var positions = new List<Vector3>();

            // coarse grid over the whole room
            foreach (float x in new[] { -2.0f, -1.0f, 0f, 1.0f, 2.0f })
            foreach (float z in new[] { -1.5f, -0.5f, 0.5f, 1.5f, 2.5f })
            foreach (float y in new[] { 0.5f, 1.5f, 2.5f })
            {
                positions.Add(new Vector3(x, y, z));
            }

            // fine grid around the user: where hands, gems and the card move
            foreach (float x in new[] { -0.6f, -0.3f, 0f, 0.3f, 0.6f })
            foreach (float z in new[] { -0.2f, 0.1f, 0.4f })
            foreach (float y in new[] { 0.9f, 1.2f, 1.5f })
            {
                positions.Add(new Vector3(x, y, z));
            }

            // inside and in front of the cabinet (gems being pulled out)
            foreach (float x in new[] { -0.15f, 0f, 0.15f })
            foreach (float y in new[] { 1.15f, 1.3f, 1.45f })
            foreach (float z in new[] { 0.55f, 0.7f })
            {
                positions.Add(new Vector3(x, y, z));
            }

            foreach (float x in new[] { -0.3f, 0f, 0.3f })
            foreach (float y in new[] { 1.1f, 1.3f, 1.5f })
            foreach (float z in new[] { 0.25f, 0.4f })
            {
                positions.Add(new Vector3(x, y, z));
            }

            // above the counter top (card, terminal, gift box)
            Vector3 counter = new Vector3(0.82f, 0f, 0.57f);
            Quaternion rot = Quaternion.Euler(0f, 55f, 0f);
            foreach (float lx in new[] { -0.4f, 0f, 0.4f })
            foreach (float lz in new[] { -0.3f, 0.2f })
            foreach (float y in new[] { 1.12f, 1.35f })
            {
                positions.Add(counter + rot * new Vector3(lx, 0f, lz) + new Vector3(0f, y, 0f));
            }

            probes.probePositions = positions.ToArray();
        }

        private static void BuildReflectionProbes(Transform group)
        {
            // Cabinet probe (small, high priority): what the gems reflect. Box projection with a box around the cabinet front.
            ReflectionProbe cabinet = NewProbe(group, "ReflectionProbe_Cabinet", new Vector3(0f, 1.30f, 0.66f), new Vector3(0.9f, 0.9f, 0.9f), new Vector3(0f, 0f, -0.15f), 128, 2, 0.2f);
            // Room probe: the whole room, box projection matched to the walls.
            ReflectionProbe room = NewProbe(group, "ReflectionProbe_Room", new Vector3(0f, 1.4f, 0.3f), new Vector3(5f, 3f, 5f), new Vector3(0f, 0.1f, 0.2f), 128, 1, 0.3f);
            cabinet.boxProjection = true;
            room.boxProjection = true;
        }

        private static ReflectionProbe NewProbe(Transform parent, string name, Vector3 position, Vector3 size, Vector3 center, int resolution, int importance, float blend)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            var probe = go.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Baked;
            probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
            probe.resolution = resolution;
            probe.hdr = true;
            probe.size = size;
            probe.center = center;
            probe.importance = importance;
            probe.blendDistance = blend;
            probe.nearClipPlane = 0.02f;
            probe.farClipPlane = 12f;
            probe.clearFlags = ReflectionProbeClearFlags.Skybox;
            return probe;
        }

        /// <summary>Every static renderer contributes to the bake, casts baked shadows (nothing casts realtime shadows) and gets a lightmap.</summary>
        private static void MakeStaticRenderersBake(Transform store)
        {
            foreach (MeshRenderer renderer in store.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer.GetComponent<TMPro.TMP_Text>() != null)
                {
                    continue;
                }

                StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(renderer.gameObject);
                if ((flags & StaticEditorFlags.BatchingStatic) == 0)
                {
                    continue; // moving or interactive: lit by the realtime light and the probes
                }

                GameObjectUtility.SetStaticEditorFlags(renderer.gameObject, flags | StaticEditorFlags.ContributeGI | StaticEditorFlags.ReflectionProbeStatic);
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveGI = ReceiveGI.Lightmaps;

                // the cabinet interior is small and important: give it a finer lightmap
                bool inCabinet = false;
                for (Transform p = renderer.transform; p != null; p = p.parent)
                {
                    if (p.name == "ShowcaseCabinet")
                    {
                        inCabinet = true;
                        break;
                    }
                }

                if (inCabinet)
                {
                    var so = new SerializedObject(renderer);
                    SerializedProperty scale = so.FindProperty("m_ScaleInLightmap");
                    if (scale != null)
                    {
                        scale.floatValue = 2f;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
            }
        }

        private static void ConfigureEnvironment()
        {
            // Little ambient: the shape of the room comes from the baked lights. Gradient so up-facing faces are slightly brighter.
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.22f, 0.20f, 0.18f);
            RenderSettings.ambientEquatorColor = new Color(0.16f, 0.145f, 0.125f);
            RenderSettings.ambientGroundColor = new Color(0.07f, 0.06f, 0.05f);
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 1f;
        }

        private static void ConfigureLightingSettings()
        {
            LightingSettings settings;
            if (!Lightmapping.TryGetLightingSettings(out settings) || settings == null)
            {
                settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(SettingsPath);
                if (settings == null)
                {
                    settings = new LightingSettings { name = "Storefront_LightingSettings" };
                    AssetDatabase.CreateAsset(settings, SettingsPath);
                }

                Lightmapping.lightingSettings = settings;
            }

            settings.lightmapper = LightingSettings.Lightmapper.ProgressiveGPU;
            settings.bakedGI = true;
            settings.realtimeGI = false;
            settings.mixedBakeMode = MixedLightingMode.IndirectOnly;
            settings.lightmapResolution = 20f;            // texels per unit (indirect); 10-20 asked
            settings.lightmapPadding = 2;
            settings.lightmapMaxSize = 1024;
            settings.lightmapCompression = LightmapCompression.NormalQuality;
            settings.directionalityMode = LightmapsMode.NonDirectional; // we have no normal maps: half the lightmap memory
            settings.ao = true;
            settings.aoMaxDistance = 0.6f;
            settings.aoExponentIndirect = 1f;
            settings.aoExponentDirect = 0.5f;
            settings.maxBounces = 3;
            settings.directSampleCount = 64;
            settings.indirectSampleCount = 256;
            settings.environmentSampleCount = 128;
            settings.filteringMode = LightingSettings.FilterMode.Auto;
            EditorUtility.SetDirty(settings);
        }

        private static void ConfigureQuestPipeline()
        {
            // Every light is baked except the main one, so the Quest and Mobile pipeline assets do not need additional lights at all.
            foreach (string name in new[] { "Quest_RPAsset", "Mobile_RPAsset" })
            {
                var asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>($"Assets/Settings/{name}.asset");
                if (asset == null)
                {
                    continue;
                }

                var so = new SerializedObject(asset);
                SerializedProperty mode = so.FindProperty("m_AdditionalLightsRenderingMode");
                if (mode != null)
                {
                    mode.intValue = 0; // Disabled
                }

                SerializedProperty blending = so.FindProperty("m_ReflectionProbeBlending");
                if (blending != null) blending.boolValue = true;
                SerializedProperty box = so.FindProperty("m_ReflectionProbeBoxProjection");
                if (box != null) box.boolValue = true;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(asset);
            }
        }

        // ------------------------------------------------------------------------------------------------------

        private static Transform New(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        private static Light Spot(Transform parent, string name, Vector3 position, Vector3 aimAt, float intensity, float range, float angle, float innerAngle, float kelvin)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            go.transform.rotation = Quaternion.LookRotation((aimAt - position).normalized, Mathf.Abs(Vector3.Dot((aimAt - position).normalized, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up);
            var light = go.AddComponent<Light>();
            light.type = LightType.Spot;
            light.lightmapBakeType = LightmapBakeType.Baked;
            light.shadows = LightShadows.Soft; // baked shadows only
            light.range = range;
            light.spotAngle = angle;
            light.innerSpotAngle = innerAngle;
            light.useColorTemperature = true;
            light.colorTemperature = kelvin;
            light.color = Color.white;
            light.intensity = intensity;
            return light;
        }

        private static Material EmissiveMaterial(string name, Color color, Color emission)
        {
            string path = $"{MaterialsFolder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, path);
            }

            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", 0.2f);
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", emission);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null)
            {
                return null;
            }

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name)
                {
                    return t;
                }
            }

            return null;
        }
    }
}
