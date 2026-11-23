using GemStore.Payment;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace GemStore.Showcase.Editor
{
    /// <summary>
    /// Builds the ShowcaseCabinet prefab (3x3 display cabinet made of primitives) and places it in the open scene.
    /// Local origin of the prefab is the centre of the cabinet body; the open front faces local -Z.
    /// </summary>
    public static class ShowcaseCabinetBuilder
    {
        private const string PrefabPath = "Assets/_Project/Prefabs/ShowcaseCabinet.prefab";
        private const string MaterialsFolder = "Assets/_Project/Materials/Cabinet";
        private const string GemDataFolder = "Assets/_Project/Data/Gems";
        private const string GemPrefabFolder = "Assets/_Project/Prefabs";

        // Layout in metres.
        private const float Pitch = 0.12f;                              // slot spacing
        private const float Border = 0.045f;                            // outer frame thickness
        private const float Depth = 0.16f;
        private const float DividerThickness = 0.012f;
        private const float InnerSize = Pitch * 3f;                     // 0.36
        private const float OuterSize = InnerSize + Border * 2f;        // 0.45
        private const float PedestalHeight = 0.015f;
        private const float PedestalDiameter = 0.06f;
        private const float GemRestHeight = 0.03f;                      // pedestal top -> gem centre
        private const string ShopName = "Bangmod Gems";
        private const string CabinetLayerName = "ShowcaseCabinet";

        // Slot index = row * 3 + column, row 0 on top. Mixes shapes, valuable diamond in the centre.
        private static readonly string[] SlotGemNames =
        {
            "Ruby Round", "Sapphire Round", "Amethyst Round",
            "Tsavorite Emerald", "Diamond Emerald", "Morganite Emerald",
            "Peridot Round", "Sapphire Emerald", "Citrine Round"
        };

        [MenuItem("GemStore/Build Showcase Cabinet")]
        public static void BuildPrefab()
        {
            EnsureFolder(MaterialsFolder);
            Material wood = GetMaterial("Cabinet_Wood", new Color(0.19f, 0.10f, 0.05f), 0.4f, 0f);
            Material velvet = GetMaterial("Cabinet_Velvet", new Color(0.02f, 0.04f, 0.15f), 0.05f, 0f);
            Material pedestalMaterial = GetMaterial("Cabinet_Pedestal", new Color(0.03f, 0.03f, 0.035f), 0.7f, 0.2f);

            var root = new GameObject("ShowcaseCabinet");

            // Frame ---------------------------------------------------------------------------
            Transform frame = Empty(root.transform, "Frame");
            float edge = OuterSize * 0.5f - Border * 0.5f;
            Box(frame, "Top", new Vector3(0f, edge, 0f), new Vector3(OuterSize, Border, Depth), wood);
            Box(frame, "Bottom", new Vector3(0f, -edge, 0f), new Vector3(OuterSize, Border, Depth), wood);
            Box(frame, "Left", new Vector3(-edge, 0f, 0f), new Vector3(Border, InnerSize, Depth), wood);
            Box(frame, "Right", new Vector3(edge, 0f, 0f), new Vector3(Border, InnerSize, Depth), wood);
            Box(frame, "Divider_V0", new Vector3(-Pitch * 0.5f, 0f, 0f), new Vector3(DividerThickness, InnerSize, Depth), wood);
            Box(frame, "Divider_V1", new Vector3(Pitch * 0.5f, 0f, 0f), new Vector3(DividerThickness, InnerSize, Depth), wood);
            // Horizontal dividers are 4 mm shallower than the vertical ones (2 mm inset front and back) so the
            // faces where they cross do not share a plane (z-fighting).
            const float horizontalDividerDepth = Depth - 0.004f;
            Box(frame, "Divider_H0", new Vector3(0f, Pitch * 0.5f, 0f), new Vector3(InnerSize, DividerThickness, horizontalDividerDepth), wood);
            Box(frame, "Divider_H1", new Vector3(0f, -Pitch * 0.5f, 0f), new Vector3(InnerSize, DividerThickness, horizontalDividerDepth), wood);

            // Velvet back panel (matte navy). Its rear face sits 1 mm in front of the frame's rear plane.
            const float backPanelThickness = 0.01f;
            Box(root.transform, "BackPanel", new Vector3(0f, 0f, Depth * 0.5f - 0.001f - backPanelThickness * 0.5f), new Vector3(InnerSize, InnerSize, backPanelThickness), velvet);

            // Pedestals and slots --------------------------------------------------------------
            Transform pedestals = Empty(root.transform, "Pedestals");
            Transform slotsParent = Empty(root.transform, "Slots");
            var slots = new Transform[GemShowcase.SlotCount];
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    int index = row * 3 + col;
                    float x = (col - 1) * Pitch;
                    float y = (1 - row) * Pitch;

                    // Top surface of the shelf under this cell: bottom board for the lowest row, divider for the others.
                    float floorTop = row == 2 ? -InnerSize * 0.5f : y - Pitch * 0.5f + DividerThickness * 0.5f;

                    GameObject pedestal = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    pedestal.name = $"Pedestal_{index}";
                    pedestal.transform.SetParent(pedestals, false);
                    pedestal.transform.localPosition = new Vector3(x, floorTop + PedestalHeight * 0.5f, 0f);
                    pedestal.transform.localScale = new Vector3(PedestalDiameter, PedestalHeight * 0.5f, PedestalDiameter);
                    ConfigureRenderer(pedestal, pedestalMaterial);

                    Transform slot = Empty(slotsParent, $"Slot_{index}");
                    slot.localPosition = new Vector3(x, floorTop + PedestalHeight + GemRestHeight, 0f);
                    slots[index] = slot;
                }
            }

            // Sign ----------------------------------------------------------------------------
            Transform sign = Empty(root.transform, "Sign");
            const float signHeight = 0.08f;
            const float signDepth = 0.03f;
            sign.localPosition = new Vector3(0f, OuterSize * 0.5f + signHeight * 0.5f, -Depth * 0.5f + signDepth * 0.5f);
            Box(sign, "SignBoard", Vector3.zero, new Vector3(OuterSize, signHeight, signDepth), wood);

            var canvasObject = new GameObject("SignCanvas", typeof(RectTransform), typeof(Canvas));
            canvasObject.transform.SetParent(sign, false);
            canvasObject.transform.localPosition = new Vector3(0f, 0f, -signDepth * 0.5f - 0.0015f); // 1.5 mm in front of the board
            canvasObject.transform.localScale = Vector3.one * 0.001f;
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasObject.GetComponent<RectTransform>().sizeDelta = new Vector2(OuterSize * 1000f, signHeight * 1000f);

            var textObject = new GameObject("ShopName", typeof(RectTransform));
            textObject.transform.SetParent(canvasObject.transform, false);
            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            var label = textObject.AddComponent<TextMeshProUGUI>();
            label.text = ShopName;
            label.fontSize = 46f;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.color = new Color(1f, 0.82f, 0.38f);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.raycastTarget = false;

            // Cart summary on the front of the bottom board, right-aligned in the lower-right corner ----------
            var summaryCanvasObject = new GameObject("CartSummary", typeof(RectTransform), typeof(Canvas));
            summaryCanvasObject.transform.SetParent(root.transform, false);
            summaryCanvasObject.transform.localPosition = new Vector3(0.015f, -(OuterSize * 0.5f - Border * 0.5f), -Depth * 0.5f - 0.0015f);
            summaryCanvasObject.transform.localScale = Vector3.one * 0.001f;
            summaryCanvasObject.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            summaryCanvasObject.GetComponent<RectTransform>().sizeDelta = new Vector2(400f, 36f);

            var summaryTextObject = new GameObject("Label", typeof(RectTransform));
            summaryTextObject.transform.SetParent(summaryCanvasObject.transform, false);
            var summaryRect = summaryTextObject.GetComponent<RectTransform>();
            summaryRect.anchorMin = Vector2.zero;
            summaryRect.anchorMax = Vector2.one;
            summaryRect.offsetMin = Vector2.zero;
            summaryRect.offsetMax = Vector2.zero;
            var summaryLabel = summaryTextObject.AddComponent<TextMeshProUGUI>();
            summaryLabel.text = "Cart: 0 items";
            summaryLabel.fontSize = 26f;
            summaryLabel.fontStyle = FontStyles.Bold;
            summaryLabel.alignment = TextAlignmentOptions.Right;
            summaryLabel.color = new Color(1f, 0.82f, 0.38f);
            summaryLabel.textWrappingMode = TextWrappingModes.NoWrap;
            summaryLabel.raycastTarget = false;
            var summaryDisplay = summaryCanvasObject.AddComponent<CartSummaryDisplay>();
            var summarySerialized = new SerializedObject(summaryDisplay);
            summarySerialized.FindProperty("label").objectReferenceValue = summaryLabel;
            summarySerialized.ApplyModifiedPropertiesWithoutUndo();

            // Lights (no shadows) ---------------------------------------------------------------
            Transform lights = Empty(root.transform, "Lights");
            Vector3 aim = new Vector3(0f, -0.02f, 0.03f);
            // Mounted a little in front of the top board so the beam does not form hot spots on the frame.
            AddSpot(lights, "Spot_Left", new Vector3(-0.12f, 0.21f, -0.18f), aim);
            AddSpot(lights, "Spot_Right", new Vector3(0.12f, 0.21f, -0.18f), aim);

            // Showcase component ----------------------------------------------------------------
            var showcase = root.AddComponent<GemShowcase>();
            var serialized = new SerializedObject(showcase);

            SerializedProperty gemList = serialized.FindProperty("gems");
            gemList.arraySize = GemShowcase.SlotCount;
            SerializedProperty slotList = serialized.FindProperty("slots");
            slotList.arraySize = GemShowcase.SlotCount;
            for (int i = 0; i < GemShowcase.SlotCount; i++)
            {
                var data = AssetDatabase.LoadAssetAtPath<GemData>($"{GemDataFolder}/{SlotGemNames[i]}.asset");
                if (data == null)
                {
                    Object.DestroyImmediate(root);
                    throw new System.InvalidOperationException($"Missing GemData '{SlotGemNames[i]}'. Run GemStore/Build Gem Assets first.");
                }

                gemList.GetArrayElementAtIndex(i).objectReferenceValue = data;
                slotList.GetArrayElementAtIndex(i).objectReferenceValue = slots[i];
            }

            var roundPrefab = AssetDatabase.LoadAssetAtPath<GemItem>($"{GemPrefabFolder}/Gem_Round.prefab");
            var emeraldPrefab = AssetDatabase.LoadAssetAtPath<GemItem>($"{GemPrefabFolder}/Gem_Emerald.prefab");
            if (roundPrefab == null || emeraldPrefab == null)
            {
                Object.DestroyImmediate(root);
                throw new System.InvalidOperationException("Gem prefabs missing. Run GemStore/Build Gem Assets first.");
            }

            serialized.FindProperty("roundPrefab").objectReferenceValue = roundPrefab;
            serialized.FindProperty("emeraldPrefab").objectReferenceValue = emeraldPrefab;
            serialized.FindProperty("idleSpin").boolValue = true;

            var infoPanelPrefab = AssetDatabase.LoadAssetAtPath<GemInfoPanel>(GemInfoPanelBuilder.PrefabPath);
            if (infoPanelPrefab == null)
            {
                Object.DestroyImmediate(root);
                throw new System.InvalidOperationException("GemInfoPanel prefab missing. Run GemStore/Build Info Panel first.");
            }

            serialized.FindProperty("infoPanelPrefab").objectReferenceValue = infoPanelPrefab;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // The cabinet colliders live on their own layer. The Near-Far interactors only raycast Default/UI layers,
            // so rays and near grabs pass through the cabinet body and reach the gems inside it. Gems stay on Default.
            SetLayerRecursive(root, EnsureLayer(CabinetLayerName));

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("GemStore: ShowcaseCabinet prefab built.");
        }

        public static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
            {
                SetLayerRecursive(child.gameObject, layer);
            }
        }

        /// <summary>Returns the index of the named layer, adding it to the first free user layer (8-31) if needed.</summary>
        public static int EnsureLayer(string layerName)
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            SerializedProperty layers = tagManager.FindProperty("layers");
            for (int i = 0; i < layers.arraySize; i++)
            {
                if (layers.GetArrayElementAtIndex(i).stringValue == layerName)
                {
                    return i;
                }
            }

            for (int i = 8; i < layers.arraySize; i++)
            {
                SerializedProperty element = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(element.stringValue))
                {
                    element.stringValue = layerName;
                    tagManager.ApplyModifiedProperties();
                    return i;
                }
            }

            throw new System.InvalidOperationException("No free user layer available for the showcase cabinet.");
        }

        [MenuItem("GemStore/Place Showcase Cabinet In Scene")]
        public static void PlaceInScene()
        {
            const float centerHeight = 1.30f;   // middle of the cabinet, ~6 cm below the simulator eye height
            const float centerDistance = 0.70f; // body centre; front face ends up ~0.62 m from the rig

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError("GemStore: ShowcaseCabinet prefab missing. Run GemStore/Build Showcase Cabinet first.");
                return;
            }

            var scene = EditorSceneManager.GetActiveScene();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == "GemPreview_TEMP" || root.name == "ShowcaseCabinet")
                {
                    Object.DestroyImmediate(root);
                }
            }

            var xrOrigin = Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
            Vector3 originPosition = xrOrigin != null ? xrOrigin.transform.position : Vector3.zero;
            Vector3 forward = xrOrigin != null ? xrOrigin.transform.forward : Vector3.forward;
            forward.y = 0f;
            forward.Normalize();

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.transform.position = new Vector3(originPosition.x, centerHeight, originPosition.z) + forward * centerDistance;
            // Front of the cabinet is local -Z, so facing the rig means local +Z points away from it.
            instance.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);

            if (Object.FindAnyObjectByType<CartManager>() == null)
            {
                new GameObject("CartManager").AddComponent<CartManager>();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Selection.activeGameObject = instance;
            Debug.Log("GemStore: ShowcaseCabinet placed in the scene.");
        }

        private static Transform Empty(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        private static GameObject Box(Transform parent, string name, Vector3 localPosition, Vector3 size, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = size;
            ConfigureRenderer(go, material);
            return go;
        }

        private static void ConfigureRenderer(GameObject go, Material material)
        {
            var meshRenderer = go.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
        }

        private static void AddSpot(Transform parent, string name, Vector3 localPosition, Vector3 aimPoint)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.LookRotation(aimPoint - localPosition, Vector3.up);

            var spot = go.AddComponent<Light>();
            spot.type = LightType.Spot;
            spot.color = new Color(1f, 0.95f, 0.86f);
            spot.intensity = 1.6f;
            spot.range = 0.8f;
            spot.spotAngle = 80f;
            spot.innerSpotAngle = 40f;
            spot.shadows = LightShadows.None;
        }

        private static Material GetMaterial(string name, Color color, float smoothness, float metallic)
        {
            string path = $"{MaterialsFolder}/{name}.mat";
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            material.SetFloat("_Surface", 0f);
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Metallic", metallic);
            EditorUtility.SetDirty(material);
            return material;
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
