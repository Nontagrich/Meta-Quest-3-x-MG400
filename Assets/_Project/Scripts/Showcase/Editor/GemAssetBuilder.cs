using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Interaction.Toolkit.Attachment;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

namespace GemStore.Showcase.Editor
{
    /// <summary>
    /// Editor-only generator for gem meshes, materials, GemData assets and prefabs.
    /// Safe to re-run: existing assets are updated in place so references and GUIDs are kept.
    /// </summary>
    public static class GemAssetBuilder
    {
        private const string ModelsFolder = "Assets/_Project/Models";
        private const string MaterialsFolder = "Assets/_Project/Materials/Gems";
        private const string DataFolder = "Assets/_Project/Data/Gems";
        private const string PrefabsFolder = "Assets/_Project/Prefabs";
        private const string PreviewRootName = "GemPreview_TEMP";

        private sealed class GemDefinition
        {
            public readonly string Id;
            public readonly string Name;
            public readonly GemShape Shape;
            public readonly GemColor Color;
            public readonly int Price;
            public readonly string Description;

            public GemDefinition(string id, string name, GemShape shape, GemColor color, int price, string description)
            {
                Id = id;
                Name = name;
                Shape = shape;
                Color = color;
                Price = price;
                Description = description;
            }
        }

        // 5 Round + 4 Emerald, covering all seven colours.
        private static readonly GemDefinition[] Definitions =
        {
            new GemDefinition("gem_ruby_round", "Ruby Round", GemShape.Round, GemColor.Red, 45000,
                "A vivid pigeon-blood red brilliant cut with deep fire and a warm glow."),
            new GemDefinition("gem_sapphire_round", "Sapphire Round", GemShape.Round, GemColor.Blue, 38000,
                "Royal blue brilliant cut sapphire, clean and sparkling from every angle."),
            new GemDefinition("gem_peridot_round", "Peridot Round", GemShape.Round, GemColor.Green, 9500,
                "Fresh spring-green peridot with a lively sparkle and a soft inner light."),
            new GemDefinition("gem_citrine_round", "Citrine Round", GemShape.Round, GemColor.Yellow, 6200,
                "Sunny golden citrine in a classic round brilliant, bright and cheerful."),
            new GemDefinition("gem_amethyst_round", "Amethyst Round", GemShape.Round, GemColor.Purple, 4800,
                "Rich violet amethyst with balanced colour and a mirror-like polish."),
            new GemDefinition("gem_sapphire_emerald", "Sapphire Emerald", GemShape.Emerald, GemColor.Blue, 52000,
                "Deep blue sapphire in an elegant step cut with long, glassy windows."),
            new GemDefinition("gem_tsavorite_emerald", "Tsavorite Emerald", GemShape.Emerald, GemColor.Green, 68000,
                "Intense forest-green tsavorite garnet, a rare gem cut to show its clarity."),
            new GemDefinition("gem_morganite_emerald", "Morganite Emerald", GemShape.Emerald, GemColor.Pink, 28000,
                "Soft blush-pink morganite in a graceful step cut, romantic and light."),
            new GemDefinition("gem_diamond_emerald", "Diamond Emerald", GemShape.Emerald, GemColor.White, 120000,
                "Colourless step-cut diamond with crisp reflections and a clean, icy look.")
        };

        private static Color BaseColor(GemColor color)
        {
            switch (color)
            {
                case GemColor.Red: return new Color(0.86f, 0.04f, 0.14f);
                case GemColor.Blue: return new Color(0.08f, 0.28f, 0.92f);
                case GemColor.Green: return new Color(0.05f, 0.72f, 0.32f);
                case GemColor.Yellow: return new Color(0.98f, 0.80f, 0.10f);
                case GemColor.Purple: return new Color(0.58f, 0.22f, 0.88f);
                case GemColor.Pink: return new Color(1.00f, 0.45f, 0.72f);
                default: return new Color(0.93f, 0.96f, 1.00f);
            }
        }

        [MenuItem("GemStore/Build Gem Assets")]
        public static void BuildAll()
        {
            EnsureFolder(ModelsFolder);
            EnsureFolder(MaterialsFolder);
            EnsureFolder(DataFolder);
            EnsureFolder(PrefabsFolder);

            Mesh round = SaveMesh(GemMeshGenerator.CreateRound(), ModelsFolder + "/Gem_Round.asset");
            Mesh emerald = SaveMesh(GemMeshGenerator.CreateEmerald(), ModelsFolder + "/Gem_Emerald.asset");
            Material[] materials = BuildMaterials();
            foreach (GemDefinition definition in Definitions)
            {
                SaveGemData(definition);
            }

            BuildPrefab("Gem_Round", round, materials);
            BuildPrefab("Gem_Emerald", emerald, materials);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("GemStore: gem meshes, materials, GemData and prefabs built.");
        }

        [MenuItem("GemStore/Place Gem Preview Row")]
        public static void PlacePreviewRow()
        {
            var scene = EditorSceneManager.GetActiveScene();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == PreviewRootName)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }

            var parent = new GameObject(PreviewRootName);
            const float spacing = 0.07f;
            float startX = -spacing * (Definitions.Length - 1) * 0.5f;
            parent.transform.position = new Vector3(0f, 1.3f, 1.0f);

            for (int i = 0; i < Definitions.Length; i++)
            {
                GemDefinition definition = Definitions[i];
                var data = AssetDatabase.LoadAssetAtPath<GemData>($"{DataFolder}/{definition.Name}.asset");
                string prefabName = definition.Shape == GemShape.Round ? "Gem_Round" : "Gem_Emerald";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabsFolder}/{prefabName}.prefab");
                if (data == null || prefab == null)
                {
                    Debug.LogError($"GemStore: missing data or prefab for {definition.Name}. Run GemStore/Build Gem Assets first.");
                    continue;
                }

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.transform);
                instance.name = definition.Name;
                instance.transform.localPosition = new Vector3(startX + i * spacing, 0f, 0f);
                instance.GetComponent<GemItem>().ApplyData(data);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Selection.activeGameObject = parent;
            Debug.Log($"GemStore: placed temporary preview row '{PreviewRootName}'. Delete it when done.");
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

        private static Mesh SaveMesh(Mesh mesh, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                existing.Clear();
                EditorUtility.CopySerialized(mesh, existing);
                UnityEngine.Object.DestroyImmediate(mesh);
                EditorUtility.SetDirty(existing);
                return existing;
            }

            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        private static Material[] BuildMaterials()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                throw new InvalidOperationException("URP Lit shader not found.");
            }

            var colors = (GemColor[])Enum.GetValues(typeof(GemColor));
            var result = new Material[colors.Length];
            foreach (GemColor color in colors)
            {
                string path = $"{MaterialsFolder}/Gem_{color}.mat";
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                {
                    material = new Material(shader);
                    AssetDatabase.CreateAsset(material, path);
                }
                else
                {
                    material.shader = shader;
                }

                Color baseColor = BaseColor(color);
                material.SetFloat("_Surface", 0f);                 // opaque
                material.SetColor("_BaseColor", baseColor);
                material.SetFloat("_Metallic", 0.1f);
                material.SetFloat("_Smoothness", 0.8f);             // 0.92 made small facet highlights shimmer while spinning
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", baseColor * 0.12f); // softer glow
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                EditorUtility.SetDirty(material);
                result[(int)color] = material;
            }

            return result;
        }

        private static void SaveGemData(GemDefinition definition)
        {
            string path = $"{DataFolder}/{definition.Name}.asset";
            var asset = AssetDatabase.LoadAssetAtPath<GemData>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<GemData>();
                AssetDatabase.CreateAsset(asset, path);
            }

            var serialized = new SerializedObject(asset);
            serialized.FindProperty("id").stringValue = definition.Id;
            serialized.FindProperty("displayName").stringValue = definition.Name;
            serialized.FindProperty("shape").enumValueIndex = (int)definition.Shape;
            serialized.FindProperty("gemColor").enumValueIndex = (int)definition.Color;
            serialized.FindProperty("tint").colorValue = BaseColor(definition.Color);
            serialized.FindProperty("price").intValue = definition.Price;
            serialized.FindProperty("description").stringValue = definition.Description;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        private static void BuildPrefab(string prefabName, Mesh mesh, Material[] materials)
        {
            var go = new GameObject(prefabName);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = materials[0];
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;

            var meshCollider = go.AddComponent<MeshCollider>();
            meshCollider.sharedMesh = mesh;
            meshCollider.convex = true;

            go.AddComponent<IdleSpin>();

            var item = go.AddComponent<GemItem>();
            var serialized = new SerializedObject(item);
            serialized.FindProperty("meshRenderer").objectReferenceValue = meshRenderer;
            SerializedProperty list = serialized.FindProperty("colorMaterials");
            list.arraySize = materials.Length;
            for (int i = 0; i < materials.Length; i++)
            {
                list.GetArrayElementAtIndex(i).objectReferenceValue = materials[i];
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();

            ConfigureGrabbing(go, meshRenderer);

            PrefabUtility.SaveAsPrefabAsset(go, $"{PrefabsFolder}/{prefabName}.prefab");
            UnityEngine.Object.DestroyImmediate(go);
        }

        /// <summary>Adds the Rigidbody, XR Grab Interactable, grab transformer, audio source and GemGrabBehaviour.</summary>
        private static void ConfigureGrabbing(GameObject go, MeshRenderer meshRenderer)
        {
            var body = go.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            var grab = go.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grab.trackPosition = true;
            grab.trackRotation = true;
            grab.trackScale = true;
            grab.useDynamicAttach = true;
            grab.farAttachMode = InteractableFarAttachMode.Near;   // far (ray) grabs pull the gem to the hand
            grab.attachEaseInTime = 0.25f;                        // the gem floats to the hand instead of snapping
            grab.selectMode = InteractableSelectMode.Multiple;     // two hands can hold it
            grab.throwOnDetach = false;
            grab.retainTransformParent = true;

            // XRGrabInteractable finds this component on the same GameObject and registers it for 1- and 2-hand grabs.
            var transformer = go.AddComponent<XRGeneralGrabTransformer>();
            transformer.allowTwoHandedRotation = XRGeneralGrabTransformer.TwoHandedRotationMode.TwoHandedAverage;
            transformer.allowTwoHandedScaling = true;
            transformer.clampScaling = true;
            transformer.minimumScaleRatio = 1f;   // 1x ...
            transformer.maximumScaleRatio = 3f;   // ... to 3x of the original size

            var audio = go.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.loop = false;
            audio.spatialBlend = 1f;
            audio.volume = 0.5f;
            audio.minDistance = 0.2f;
            audio.maxDistance = 5f;

            var behaviour = go.AddComponent<GemGrabBehaviour>();
            var serialized = new SerializedObject(behaviour);
            serialized.FindProperty("grabInteractable").objectReferenceValue = grab;
            serialized.FindProperty("gemItem").objectReferenceValue = go.GetComponent<GemItem>();
            serialized.FindProperty("idleSpin").objectReferenceValue = go.GetComponent<IdleSpin>();
            serialized.FindProperty("meshRenderer").objectReferenceValue = meshRenderer;
            serialized.FindProperty("audioSource").objectReferenceValue = audio;
            serialized.FindProperty("grabClip").objectReferenceValue = GetOrCreatePlaceholderGrabClip();
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Creates a tiny two-note chime WAV as a placeholder grab sound (only if it does not exist yet).</summary>
        private static AudioClip GetOrCreatePlaceholderGrabClip()
        {
            const string folder = "Assets/_Project/Audio";
            const string path = folder + "/GemGrab_Placeholder.wav";
            EnsureFolder(folder);

            if (!File.Exists(path))
            {
                const int sampleRate = 44100;
                int sampleCount = (int)(sampleRate * 0.18f);
                var bytes = new byte[44 + sampleCount * 2];

                void WriteText(int offset, string text)
                {
                    for (int i = 0; i < text.Length; i++) bytes[offset + i] = (byte)text[i];
                }

                void WriteInt(int offset, int value)
                {
                    bytes[offset] = (byte)value;
                    bytes[offset + 1] = (byte)(value >> 8);
                    bytes[offset + 2] = (byte)(value >> 16);
                    bytes[offset + 3] = (byte)(value >> 24);
                }

                void WriteShort(int offset, int value)
                {
                    bytes[offset] = (byte)value;
                    bytes[offset + 1] = (byte)(value >> 8);
                }

                WriteText(0, "RIFF");
                WriteInt(4, 36 + sampleCount * 2);
                WriteText(8, "WAVE");
                WriteText(12, "fmt ");
                WriteInt(16, 16);
                WriteShort(20, 1);               // PCM
                WriteShort(22, 1);               // mono
                WriteInt(24, sampleRate);
                WriteInt(28, sampleRate * 2);
                WriteShort(32, 2);
                WriteShort(34, 16);
                WriteText(36, "data");
                WriteInt(40, sampleCount * 2);

                float phase = 0f;
                for (int i = 0; i < sampleCount; i++)
                {
                    float t = i / (float)sampleRate;
                    float frequency = t < 0.06f ? 1318.5f : 1760f;
                    phase += 2f * Mathf.PI * frequency / sampleRate;
                    float envelope = Mathf.Exp(-t * 22f) * Mathf.Min(1f, t * 400f);
                    short sample = (short)(Mathf.Sin(phase) * envelope * 0.5f * short.MaxValue);
                    WriteShort(44 + i * 2, sample);
                }

                File.WriteAllBytes(path, bytes);
                AssetDatabase.ImportAsset(path);
            }

            return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        }
    }
}
