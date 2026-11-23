using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace GemStore.Showcase.Editor
{
    /// <summary>
    /// Arranges the Storefront scene like a real shop (grey-box). Floor is y = 0, the user stands at (0,0,0) facing +Z,
    /// everything interactive is within ~1.2 m on an arc around the user. Existing objects (ShowcaseCabinet, CheckoutCounter)
    /// are moved, not recreated. Re-running the menu rebuilds only the grey-box parts.
    /// </summary>
    public static class StoreLayoutBuilder
    {
        private const string MaterialsFolder = "Assets/_Project/Materials/Store";

        // --- Layout (metres) -------------------------------------------------------------------------------
        private const float CabinetCenterHeight = 1.30f;   // middle of the 3x3 cabinet (same as ShowcaseCabinetBuilder)
        private const float CabinetCenterZ = 0.70f;        // cabinet front face is 0.62 m in front of the user
        private const float CabinetHalfSize = 0.225f;      // cabinet body is 0.45 x 0.45 m
        private const float CounterRadius = 1.0f;          // distance of the counter centre from the user
        private const float CounterAngle = 55f;            // degrees to the right of +Z
        private const float StaffRadius = 1.0f;
        private const float StaffAngle = -55f;             // degrees to the left of +Z

        private const float RoomMinX = -2.5f, RoomMaxX = 2.5f, RoomMinZ = -2.0f, RoomMaxZ = 3.0f, RoomHeight = 3.0f;
        private const float WallThickness = 0.1f;

        [MenuItem("GemStore/Arrange Store")]
        public static void Arrange()
        {
            if (!AssetDatabase.IsValidFolder(MaterialsFolder))
            {
                AssetDatabase.CreateFolder("Assets/_Project/Materials", "Store");
            }

            Material wall = GetMaterial("Store_Wall", new Color(0.66f, 0.66f, 0.68f), 0.15f);
            Material floorMaterial = GetMaterial("Store_Floor", new Color(0.30f, 0.30f, 0.32f), 0.3f);
            Material ceiling = GetMaterial("Store_Ceiling", new Color(0.82f, 0.82f, 0.84f), 0.1f);
            Material frame = GetMaterial("Store_Frame", new Color(0.45f, 0.45f, 0.48f), 0.2f);
            Material grey = GetMaterial("Store_Grey", new Color(0.55f, 0.55f, 0.58f), 0.2f);
            Material darkGrey = GetMaterial("Store_DarkGrey", new Color(0.22f, 0.22f, 0.25f), 0.35f);
            var wood = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Materials/Cabinet/Cabinet_Wood.mat");

            // Pick up the existing objects before the old Store is removed.
            GameObject cabinet = GameObject.Find("ShowcaseCabinet");
            GameObject counter = GameObject.Find("CheckoutCounter");
            GameObject ground = GameObject.Find("OutsideGround") ?? GameObject.Find("Floor");
            if (cabinet == null || counter == null)
            {
                Debug.LogError("GemStore: ShowcaseCabinet or CheckoutCounter is missing from the scene.");
                return;
            }

            foreach (GameObject keep in new[] { cabinet, counter, ground })
            {
                if (keep != null)
                {
                    keep.transform.SetParent(null, true);
                }
            }

            GameObject oldStore = GameObject.Find("Store");
            if (oldStore != null)
            {
                Object.DestroyImmediate(oldStore);
            }

            int cabinetLayer = ShowcaseCabinetBuilder.EnsureLayer("ShowcaseCabinet");

            var store = new GameObject("Store");
            Transform shellGroup = Group(store.transform, "StoreShell");
            Transform showcase = Group(store.transform, "Showcase");
            Transform checkout = Group(store.transform, "Checkout");
            Transform staff = Group(store.transform, "StaffCorner");
            Transform decor = Group(store.transform, "Decor");

            // ---- StoreShell: 5 x 5 m room, 3 m high, walls on 3 sides, open entrance behind the user --------------
            float width = RoomMaxX - RoomMinX;
            float depth = RoomMaxZ - RoomMinZ;
            float centerZ = (RoomMinZ + RoomMaxZ) * 0.5f;
            Part(shellGroup, "Floor", new Vector3(0f, -0.05f, centerZ), new Vector3(width, 0.1f, depth), floorMaterial, true);
            Part(shellGroup, "Wall_Front", new Vector3(0f, RoomHeight * 0.5f, RoomMaxZ + WallThickness * 0.5f), new Vector3(width + 2f * WallThickness, RoomHeight, WallThickness), wall, false);
            Part(shellGroup, "Wall_Left", new Vector3(RoomMinX - WallThickness * 0.5f, RoomHeight * 0.5f, centerZ + WallThickness * 0.5f), new Vector3(WallThickness, RoomHeight, depth + WallThickness), wall, false);
            Part(shellGroup, "Wall_Right", new Vector3(RoomMaxX + WallThickness * 0.5f, RoomHeight * 0.5f, centerZ + WallThickness * 0.5f), new Vector3(WallThickness, RoomHeight, depth + WallThickness), wall, false);
            Part(shellGroup, "Ceiling", new Vector3(0f, RoomHeight + WallThickness * 0.5f, centerZ + WallThickness * 0.5f), new Vector3(width + 2f * WallThickness, WallThickness, depth + WallThickness), ceiling, false);
            // Entrance frame (the back of the room is open): 2 m wide, 2.4 m high.
            Part(shellGroup, "DoorFrame_Left", new Vector3(-1.0f, 1.2f, RoomMinZ), new Vector3(0.1f, 2.4f, 0.1f), frame, false);
            Part(shellGroup, "DoorFrame_Right", new Vector3(1.0f, 1.2f, RoomMinZ), new Vector3(0.1f, 2.4f, 0.1f), frame, false);
            Part(shellGroup, "DoorFrame_Top", new Vector3(0f, 2.45f, RoomMinZ), new Vector3(2.1f, 0.1f, 0.1f), frame, false);

            // ---- Showcase: the existing cabinet on a plinth, with posts and a roof (front stays open) ---------------
            float bodyBottom = CabinetCenterHeight - CabinetHalfSize;     // 1.075
            float topOfSign = CabinetCenterHeight + 0.305f;               // 1.605
            float baseHeight = bodyBottom - 0.03f;                        // plinth body; a 3 cm top slab sits on it
            Material baseMaterial = wood != null ? wood : darkGrey;
            Part(showcase, "CabinetBase", new Vector3(0f, baseHeight * 0.5f, CabinetCenterZ), new Vector3(0.52f, baseHeight, 0.40f), baseMaterial, true, cabinetLayer);
            Part(showcase, "CabinetBase_Top", new Vector3(0f, bodyBottom - 0.015f, CabinetCenterZ), new Vector3(0.56f, 0.03f, 0.44f), darkGrey, true, cabinetLayer);
            float postHeight = topOfSign + 0.05f - bodyBottom;
            float postCenterY = bodyBottom + postHeight * 0.5f;
            Part(showcase, "CabinetPost_Left", new Vector3(-0.2475f, postCenterY, CabinetCenterZ - 0.02f), new Vector3(0.03f, postHeight, 0.12f), baseMaterial, true, cabinetLayer);
            Part(showcase, "CabinetPost_Right", new Vector3(0.2475f, postCenterY, CabinetCenterZ - 0.02f), new Vector3(0.03f, postHeight, 0.12f), baseMaterial, true, cabinetLayer);
            Part(showcase, "CabinetRoof", new Vector3(0f, topOfSign + 0.035f, CabinetCenterZ + 0.01f), new Vector3(0.56f, 0.03f, 0.30f), baseMaterial, true, cabinetLayer);

            cabinet.transform.SetPositionAndRotation(new Vector3(0f, CabinetCenterHeight, CabinetCenterZ), Quaternion.identity);
            cabinet.transform.SetParent(showcase, true);

            // ---- Checkout: the existing counter, front-right, facing the user ----------------------------------------
            counter.transform.SetPositionAndRotation(OnArc(CounterRadius, CounterAngle), Quaternion.Euler(0f, CounterAngle, 0f));
            counter.transform.SetParent(checkout, true);

            // ---- StaffCorner: front-left. Anchors + grey-box stand-ins for the staff video screen and call button ----
            var anchor = new GameObject("StaffCorner_Anchor").transform;
            anchor.SetParent(staff, false);
            anchor.SetPositionAndRotation(OnArc(StaffRadius, StaffAngle), Quaternion.Euler(0f, StaffAngle, 0f));

            Part(anchor, "StaffScreen_Base", new Vector3(0f, 0.02f, 0f), new Vector3(0.5f, 0.04f, 0.35f), darkGrey, false);
            Part(anchor, "StaffScreen_Post", new Vector3(0f, 0.665f, 0f), new Vector3(0.06f, 1.25f, 0.06f), darkGrey, false);
            Part(anchor, "StaffScreen_Panel", new Vector3(0f, 1.45f, -0.04f), new Vector3(0.62f, 0.36f, 0.04f), grey, false);
            Empty(anchor, "StaffScreen_Anchor", new Vector3(0f, 1.45f, -0.062f));

            Part(anchor, "CallButton_Pedestal", new Vector3(-0.45f, 0.48f, -0.2f), new Vector3(0.14f, 0.96f, 0.14f), darkGrey, false);
            Part(anchor, "CallButton_PedestalTop", new Vector3(-0.45f, 0.98f, -0.2f), new Vector3(0.28f, 0.04f, 0.28f), grey, false);
            GameObject button = Shape(anchor, PrimitiveType.Cylinder, "CallButton_Placeholder", new Vector3(-0.45f, 1.012f, -0.2f), new Vector3(0.09f, 0.012f, 0.09f), frame);
            Object.DestroyImmediate(button.GetComponent<Collider>());
            Empty(anchor, "CallButton_Anchor", new Vector3(-0.45f, 1.03f, -0.2f));

            // ---- Decor: logo on the back wall, wall shelves, the old test ground outside the room ----------------------
            float wallFace = RoomMaxZ;
            Part(decor, "Logo_Board", new Vector3(0f, 2.15f, wallFace - 0.03f), new Vector3(2.8f, 0.8f, 0.06f), darkGrey, false);
            BuildLogoText(decor, new Vector3(0f, 2.15f, wallFace - 0.062f));

            float[] shelfHeights = { 1.15f, 1.65f, 2.15f };
            foreach (float side in new[] { -1f, 1f })
            {
                foreach (float height in shelfHeights)
                {
                    float x = side * 1.75f;
                    Part(decor, $"WallShelf_{(side < 0 ? "L" : "R")}_{height:F2}", new Vector3(x, height, wallFace - 0.125f), new Vector3(1.0f, 0.04f, 0.25f), grey, false);
                    // a couple of grey boxes standing on each shelf (stand-ins for decoration)
                    Part(decor, $"ShelfItem_{(side < 0 ? "L" : "R")}_{height:F2}_A", new Vector3(x - 0.25f, height + 0.12f, wallFace - 0.125f), new Vector3(0.16f, 0.2f, 0.16f), frame, false);
                    Part(decor, $"ShelfItem_{(side < 0 ? "L" : "R")}_{height:F2}_B", new Vector3(x + 0.2f, height + 0.08f, wallFace - 0.125f), new Vector3(0.22f, 0.12f, 0.14f), darkGrey, false);
                }
            }

            if (ground != null)
            {
                ground.name = "OutsideGround";
                ground.transform.SetPositionAndRotation(new Vector3(0f, -0.01f, 0f), Quaternion.identity);
                ground.transform.SetParent(decor, true);
            }

            // ---- Mode toggle ----------------------------------------------------------------------------------------
            var toggle = store.AddComponent<StoreModeToggle>();
            var serialized = new SerializedObject(toggle);
            serialized.FindProperty("mode").enumValueIndex = (int)StoreMode.VirtualStore;
            serialized.FindProperty("storeShell").objectReferenceValue = shellGroup.gameObject;
            SerializedProperty hidden = serialized.FindProperty("hiddenInMixedReality");
            hidden.arraySize = 1;
            hidden.GetArrayElementAtIndex(0).objectReferenceValue = decor.gameObject;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Selection.activeGameObject = store;
            Debug.Log("GemStore: store arranged (Store/{StoreShell, Showcase, Checkout, StaffCorner, Decor}).");
        }

        private static Vector3 OnArc(float radius, float angleDegrees)
        {
            float a = angleDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(a) * radius, 0f, Mathf.Cos(a) * radius);
        }

        private static Transform Group(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        private static Transform Empty(Transform parent, string name, Vector3 localPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }

        /// <summary>Grey-box cube. Colliders are removed unless <paramref name="keepCollider"/> is set.</summary>
        private static GameObject Part(Transform parent, string name, Vector3 localPosition, Vector3 size, Material material, bool keepCollider, int layer = 0)
        {
            GameObject go = Shape(parent, PrimitiveType.Cube, name, localPosition, size, material);
            go.layer = layer;
            if (!keepCollider)
            {
                Object.DestroyImmediate(go.GetComponent<Collider>());
            }

            return go;
        }

        private static GameObject Shape(Transform parent, PrimitiveType type, string name, Vector3 localPosition, Vector3 size, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = size;
            var meshRenderer = go.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off; // the ceiling must not shadow the room
            meshRenderer.receiveShadows = false;
            return go;
        }

        private static void BuildLogoText(Transform parent, Vector3 position)
        {
            var canvasObject = new GameObject("Logo_Canvas", typeof(RectTransform));
            canvasObject.transform.SetParent(parent, false);
            canvasObject.transform.localPosition = position;
            canvasObject.transform.localScale = Vector3.one * 0.001f; // 1 unit = 1 mm
            canvasObject.GetComponent<RectTransform>().sizeDelta = new Vector2(2600f, 700f);
            canvasObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;

            var textObject = new GameObject("Logo_Text", typeof(RectTransform));
            textObject.transform.SetParent(canvasObject.transform, false);
            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var text = textObject.AddComponent<TextMeshProUGUI>();
            text.text = "Bangmod Gems";
            text.fontSize = 340f;
            text.fontStyle = FontStyles.Bold;
            text.alignment = TextAlignmentOptions.Center;
            text.color = new Color(1f, 0.82f, 0.38f);
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
        }

        private static Material GetMaterial(string name, Color color, float smoothness)
        {
            string path = $"{MaterialsFolder}/{name}.mat";
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null)
            {
                return material; // keep the look given by StoreDecorBuilder (GemStore/Decorate Store (Luxury))
            }

            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
            material.shader = shader;
            material.SetFloat("_Surface", 0f);
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(material);
            return material;
        }
    }

    [CustomEditor(typeof(StoreModeToggle))]
    public class StoreModeToggleEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var toggle = (StoreModeToggle)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Current mode", toggle.Mode.ToString());
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Virtual Store"))
                {
                    toggle.SetMode(StoreMode.VirtualStore);
                    EditorSceneManager.MarkSceneDirty(toggle.gameObject.scene);
                }

                if (GUILayout.Button("Mixed Reality"))
                {
                    toggle.SetMode(StoreMode.MixedReality);
                    EditorSceneManager.MarkSceneDirty(toggle.gameObject.scene);
                }
            }
        }
    }
}
