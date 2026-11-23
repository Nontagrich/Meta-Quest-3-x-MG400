using System.Collections.Generic;
using GemStore.Network;
using GemStore.Showcase.Editor;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace GemStore.Teleop.Editor
{
    /// <summary>
    /// Builds the TeleopConfig asset, the panels, and the two new scenes: "ModeSelect" and "Maintenance". Both are copies of the Storefront
    /// scene without the shop (same XR Origin, interaction manager and event system), so the XR setup is identical. The Storefront only gets a
    /// small "Mode" button. Re-running the menu rebuilds ModeSelect and Maintenance from the current Storefront.
    /// </summary>
    public static class TeleopSceneBuilder
    {
        private const string ScenesFolder = "Assets/_Project/Scenes";
        private const string StorePath = ScenesFolder + "/Storefront.unity";
        private const string ModeSelectPath = ScenesFolder + "/ModeSelect.unity";
        private const string MaintenancePath = ScenesFolder + "/Maintenance.unity";
        private const string PrefabFolder = "Assets/_Project/Prefabs/Teleop";
        private const string ConfigPath = "Assets/_Project/Data/TeleopConfig.asset";
        private const string LineMaterialPath = "Assets/_Project/Materials/Teleop/Teleop_Line.mat";

        private static readonly Color Gold = new Color(1f, 0.82f, 0.38f);
        private static readonly Color White = new Color(0.96f, 0.97f, 1f);

        [MenuItem("GemStore/Teleop/Build Scenes")]
        public static void Build()
        {
            GemInfoPanelBuilder.EnsureFolder(PrefabFolder);
            GemInfoPanelBuilder.EnsureFolder("Assets/_Project/Materials/Teleop");

            var config = AssetDatabase.LoadAssetAtPath<TeleopConfig>(ConfigPath);
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<TeleopConfig>();
                AssetDatabase.CreateAsset(config, ConfigPath);
            }

            Material lineMaterial = AssetDatabase.LoadAssetAtPath<Material>(LineMaterialPath);
            if (lineMaterial == null)
            {
                lineMaterial = new Material(Shader.Find("Sprites/Default"));
                AssetDatabase.CreateAsset(lineMaterial, LineMaterialPath);
            }

            Sprite rounded = GemInfoPanelBuilder.GetOrCreateRoundedSprite();
            GameObject modeSelectPrefab = BuildModeSelectPanel(rounded);
            GameObject menuButtonPrefab = BuildMenuButton(rounded);
            GameObject calibrationPrefab = BuildCalibrationPanel(rounded);
            BuildHudPanel(rounded);
            BuildEStopPanel(rounded);
            BuildActionsPanel(rounded);
            BuildAddressPanel(rounded);
            AssetDatabase.SaveAssets();

            // 1) Storefront: only the entry point back to Mode Select
            EditorSceneManager.OpenScene(StorePath, OpenSceneMode.Single);
            RemoveByName("ModeMenuEntry");
            var entry = (GameObject)PrefabUtility.InstantiatePrefab(menuButtonPrefab);
            entry.name = "ModeMenuEntry";
            PlaceRoot(entry, new Vector3(2.45f, 1.55f, 0.3f), Quaternion.Euler(0f, 90f, 0f)); // on the right wall; the readable side faces the room (-X)
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());

            // 2) ModeSelect and Maintenance: copies of the Storefront without the shop
            MakeScene(ModeSelectPath, false, scene =>
            {
                var panel = (GameObject)PrefabUtility.InstantiatePrefab(modeSelectPrefab);
                panel.name = "ModeSelectPanel";
                PlaceRoot(panel, new Vector3(0f, 1.35f, 0.8f), Quaternion.identity);
                AddPlacement(panel, 0.8f, 0f, -0.05f, 0f);

                var address = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabFolder + "/RobotAddressPanel.prefab"));
                address.name = "RobotAddressPanel";
                PlaceRoot(address, new Vector3(0f, 1.0f, 0.8f), Quaternion.identity);
                AddPlacement(address, 0.8f, 0f, -0.2f, 0f); // below the mode menu (its top edge is the pivot)
                WireAddress(address);
            });

            MakeScene(MaintenancePath, true, scene =>
            {
                // load the assets again here: opening a scene unloads assets nothing references (a captured ScriptableObject would be null)
                var sceneConfig = AssetDatabase.LoadAssetAtPath<TeleopConfig>(ConfigPath);
                var sceneLineMaterial = AssetDatabase.LoadAssetAtPath<Material>(LineMaterialPath);
                var teleop = new GameObject("Teleop");
                var calibration = teleop.AddComponent<RobotCalibration>();
                var calibrationSerialized = new SerializedObject(calibration);
                calibrationSerialized.FindProperty("config").objectReferenceValue = sceneConfig;
                calibrationSerialized.ApplyModifiedPropertiesWithoutUndo();

                var overlay = teleop.AddComponent<RobotFrameOverlay>();
                var overlaySerialized = new SerializedObject(overlay);
                overlaySerialized.FindProperty("calibration").objectReferenceValue = calibration;
                overlaySerialized.FindProperty("lineMaterial").objectReferenceValue = sceneLineMaterial;
                overlaySerialized.ApplyModifiedPropertiesWithoutUndo();

                var panel = (GameObject)PrefabUtility.InstantiatePrefab(calibrationPrefab);
                panel.name = "CalibrationPanel";
                PlaceRoot(panel, new Vector3(0f, 1.3f, 0.9f), Quaternion.identity);
                AddPlacement(panel, 0.9f, 0f, -0.05f, 0f);
                var panelSerialized = new SerializedObject(panel.GetComponent<CalibrationPanel>());
                panelSerialized.FindProperty("calibration").objectReferenceValue = calibration;
                panelSerialized.ApplyModifiedPropertiesWithoutUndo();

                // hand-follow teleoperation: controller, AR visuals, HUD and the E-STOP panel
                var volumeObject = new GameObject("ControlVolume");
                var volume = volumeObject.AddComponent<ControlVolume>();
                var volumeSerialized = new SerializedObject(volume);
                volumeSerialized.FindProperty("calibration").objectReferenceValue = calibration;
                volumeSerialized.FindProperty("lineMaterial").objectReferenceValue = sceneLineMaterial;
                volumeSerialized.ApplyModifiedPropertiesWithoutUndo();

                var controller = teleop.AddComponent<TeleopController>();
                var controllerSerialized = new SerializedObject(controller);
                controllerSerialized.FindProperty("calibration").objectReferenceValue = calibration;
                controllerSerialized.FindProperty("controlVolume").objectReferenceValue = volume;
                controllerSerialized.ApplyModifiedPropertiesWithoutUndo();

                var visuals = teleop.AddComponent<TeleopVisuals>();
                var visualsSerialized = new SerializedObject(visuals);
                visualsSerialized.FindProperty("controller").objectReferenceValue = controller;
                visualsSerialized.FindProperty("material").objectReferenceValue = sceneLineMaterial;
                visualsSerialized.ApplyModifiedPropertiesWithoutUndo();

                var sceneHud = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabFolder + "/TeleopHud.prefab"));
                sceneHud.name = "TeleopHud";
                PlaceRoot(sceneHud, new Vector3(-0.6f, 1.3f, 0.8f), Quaternion.Euler(0f, -30f, 0f));
                AddPlacement(sceneHud, 0.85f, -0.6f, -0.05f, -30f);
                var hudSerialized = new SerializedObject(sceneHud.GetComponent<TeleopHud>());
                hudSerialized.FindProperty("controller").objectReferenceValue = controller;
                hudSerialized.ApplyModifiedPropertiesWithoutUndo();

                var sceneEStop = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabFolder + "/EStopPanel.prefab"));
                sceneEStop.name = "EStopPanel";
                PlaceRoot(sceneEStop, new Vector3(0.6f, 1.2f, 0.8f), Quaternion.Euler(0f, 30f, 0f));
                AddPlacement(sceneEStop, 0.85f, 0.6f, -0.15f, 30f);
                var estopSerialized = new SerializedObject(sceneEStop.GetComponent<EStopPanel>());
                estopSerialized.FindProperty("controller").objectReferenceValue = controller;
                estopSerialized.ApplyModifiedPropertiesWithoutUndo();

                var actions = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabFolder + "/TeleopActionsPanel.prefab"));
                actions.name = "TeleopActionsPanel";
                PlaceRoot(actions, new Vector3(0f, 0.9f, 0.8f), Quaternion.identity);
                AddPlacement(actions, 0.8f, 0f, -0.42f, 0f);
                var actionsSerialized = new SerializedObject(actions.GetComponent<TeleopActionsPanel>());
                actionsSerialized.FindProperty("controller").objectReferenceValue = controller;
                actionsSerialized.ApplyModifiedPropertiesWithoutUndo();

                var address = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabFolder + "/RobotAddressPanel.prefab"));
                address.name = "RobotAddressPanel";
                PlaceRoot(address, new Vector3(1.0f, 1.4f, 0.5f), Quaternion.Euler(0f, 55f, 0f));
                AddPlacement(address, 0.8f, 1.15f, 0.15f, 55f);
                WireAddress(address);
            });

            // 3) build settings: Mode Select first, then the store, then Maintenance
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(ModeSelectPath, true),
                new EditorBuildSettingsScene(StorePath, true),
                new EditorBuildSettingsScene(MaintenancePath, true),
            };

            EditorSceneManager.OpenScene(StorePath, OpenSceneMode.Single);
            AssetDatabase.SaveAssets();
            Debug.Log("GemStore: ModeSelect and Maintenance scenes built; build settings = ModeSelect, Storefront, Maintenance.");
        }

        private static void MakeScene(string path, bool keepRobotLink, System.Action<Scene> addContent)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null)
            {
                AssetDatabase.DeleteAsset(path);
            }

            AssetDatabase.CopyAsset(StorePath, path);
            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

            // without the shop: the store, its managers and the staff call go; the XR rig, interaction manager and event system stay
            var remove = new List<string> { "Store", "CartManager", "Network", "StaffCall", "ModeMenuEntry" };
            if (!keepRobotLink)
            {
                remove.Add("RobotLink");
            }

            foreach (string name in remove)
            {
                RemoveByName(name);
            }

            Lightmapping.lightingDataAsset = null; // the baked lighting belongs to the shop

            // The XR rig has gravity and the shop's floor is gone: without a floor the user falls forever (black screen).
            // An invisible collider keeps them standing; the real room shows through the passthrough on the headset.
            var floor = new GameObject("FloorCollider");
            floor.transform.position = new Vector3(0f, -0.5f, 0f);
            floor.AddComponent<BoxCollider>().size = new Vector3(30f, 1f, 30f);

            // plain MR-style lighting and background (passthrough is enabled later on the headset)
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.62f, 0.62f, 0.64f);
            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.None;
            light.intensity = 0.9f;
            lightObject.transform.rotation = Quaternion.Euler(50f, 330f, 0f);

            Camera cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            }

            addContent(scene);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        /// <summary>Positions a world-space canvas root. A RectTransform root keeps its X/Y only through anchoredPosition3D.</summary>
        private static void PlaceRoot(GameObject go, Vector3 position, Quaternion rotation)
        {
            var rect = (RectTransform)go.transform;
            rect.rotation = rotation;
            rect.anchoredPosition3D = position;
        }

        /// <summary>The panel moves in front of the user when the scene starts (never behind them or out of view).</summary>
        private static void AddPlacement(GameObject panel, float distance, float side, float height, float yaw)
        {
            var placement = panel.AddComponent<PlaceInFrontOfUser>();
            var so = new SerializedObject(placement);
            so.FindProperty("distance").floatValue = distance;
            so.FindProperty("sideOffset").floatValue = side;
            so.FindProperty("heightOffset").floatValue = height;
            so.FindProperty("yawOffset").floatValue = yaw;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void RemoveByName(string name)
        {
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == name)
                {
                    Object.DestroyImmediate(root);
                }
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Panels
        // ------------------------------------------------------------------------------------------------------

        private static GameObject NewCanvasRoot(string name, float width, float height, float scale)
        {
            var root = new GameObject(name, typeof(RectTransform));
            var rect = root.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(width, height);
            rect.localScale = Vector3.one * scale;
            root.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            root.AddComponent<TrackedDeviceGraphicRaycaster>();
            return root;
        }

        private static GameObject BuildModeSelectPanel(Sprite rounded)
        {
            GameObject root = NewCanvasRoot("ModeSelectPanel", 720f, 460f, 0.0005f); // 36 x 23 cm
            Image background = NewImage("Background", root.transform, rounded, new Color(0.04f, 0.05f, 0.09f, 0.92f));
            Stretch(background.rectTransform);
            TMP_Text title = NewText("Title", root.transform, 62f, FontStyles.Bold, Gold, TextAlignmentOptions.Center, new Vector2(0f, 170f), new Vector2(660f, 90f));
            title.text = "Gem Store AR";
            TMP_Text subtitle = NewText("Subtitle", root.transform, 38f, FontStyles.Normal, White, TextAlignmentOptions.Center, new Vector2(0f, 105f), new Vector2(660f, 56f));
            subtitle.text = "Select a mode";
            Button store = NewButton("StoreButton", root.transform, rounded, new Vector2(0f, 15f), new Vector2(560f, 110f), new Color(1f, 0.80f, 0.30f), "Customer Store", 54f, new Color(0.12f, 0.09f, 0.02f));
            Button maintenance = NewButton("MaintenanceButton", root.transform, rounded, new Vector2(0f, -125f), new Vector2(560f, 110f), new Color(0.30f, 0.55f, 0.85f), "Maintenance", 54f, Color.white);

            var panel = root.AddComponent<ModeSelectPanel>();
            var so = new SerializedObject(panel);
            so.FindProperty("storeButton").objectReferenceValue = store;
            so.FindProperty("maintenanceButton").objectReferenceValue = maintenance;
            so.ApplyModifiedPropertiesWithoutUndo();
            return Save(root, "ModeSelectPanel");
        }

        private static GameObject BuildMenuButton(Sprite rounded)
        {
            GameObject root = NewCanvasRoot("ModeMenuButton", 300f, 120f, 0.0005f); // 15 x 6 cm
            Button button = NewButton("Button", root.transform, rounded, Vector2.zero, new Vector2(300f, 120f), new Color(0.16f, 0.20f, 0.30f), "Mode Select", 44f, White);
            var menu = root.AddComponent<ModeMenuButton>();
            var so = new SerializedObject(menu);
            so.FindProperty("button").objectReferenceValue = button;
            so.ApplyModifiedPropertiesWithoutUndo();
            return Save(root, "ModeMenuButton");
        }

        private static GameObject BuildCalibrationPanel(Sprite rounded)
        {
            GameObject root = NewCanvasRoot("CalibrationPanel", 900f, 640f, 0.0004f); // 36 x 25.6 cm
            Image background = NewImage("Background", root.transform, rounded, new Color(0.04f, 0.05f, 0.09f, 0.92f));
            Stretch(background.rectTransform);

            TMP_Text title = NewText("Title", root.transform, 54f, FontStyles.Bold, Gold, TextAlignmentOptions.Center, new Vector2(0f, 270f), new Vector2(700f, 80f));
            title.text = "Robot Calibration";
            Button back = NewButton("BackButton", root.transform, rounded, new Vector2(-340f, 270f), new Vector2(170f, 70f), new Color(0.16f, 0.20f, 0.30f), "Back", 38f, White);

            TMP_Text instruction = NewText("Instruction", root.transform, 42f, FontStyles.Normal, White, TextAlignmentOptions.Center, new Vector2(0f, 120f), new Vector2(820f, 220f));
            instruction.textWrappingMode = TextWrappingModes.Normal;
            instruction.text = "Not calibrated.";
            TMP_Text errors = NewText("Errors", root.transform, 40f, FontStyles.Bold, White, TextAlignmentOptions.Center, new Vector2(0f, -45f), new Vector2(820f, 70f));
            errors.richText = true;

            Button start = NewButton("StartButton", root.transform, rounded, new Vector2(0f, -190f), new Vector2(520f, 110f), new Color(1f, 0.80f, 0.30f), "Start calibration", 46f, new Color(0.12f, 0.09f, 0.02f));
            TMP_Text startLabel = start.GetComponentInChildren<TMP_Text>();
            Button capture = NewButton("CaptureButton", root.transform, rounded, new Vector2(-230f, -190f), new Vector2(380f, 110f), new Color(1f, 0.80f, 0.30f), "Capture point", 44f, new Color(0.12f, 0.09f, 0.02f));
            Button accept = NewButton("AcceptButton", root.transform, rounded, new Vector2(-230f, -190f), new Vector2(380f, 110f), new Color(0.30f, 0.78f, 0.45f), "Accept", 46f, new Color(0.04f, 0.12f, 0.06f));
            Button redo = NewButton("RedoButton", root.transform, rounded, new Vector2(230f, -190f), new Vector2(380f, 110f), new Color(0.85f, 0.35f, 0.30f), "Redo", 46f, Color.white);
            capture.gameObject.SetActive(false);
            accept.gameObject.SetActive(false);
            redo.gameObject.SetActive(false);

            var panel = root.AddComponent<CalibrationPanel>();
            var so = new SerializedObject(panel);
            so.FindProperty("titleLabel").objectReferenceValue = title;
            so.FindProperty("instructionLabel").objectReferenceValue = instruction;
            so.FindProperty("errorsLabel").objectReferenceValue = errors;
            so.FindProperty("startButton").objectReferenceValue = start;
            so.FindProperty("startLabel").objectReferenceValue = startLabel;
            so.FindProperty("captureButton").objectReferenceValue = capture;
            so.FindProperty("redoButton").objectReferenceValue = redo;
            so.FindProperty("acceptButton").objectReferenceValue = accept;
            so.FindProperty("backButton").objectReferenceValue = back;
            so.ApplyModifiedPropertiesWithoutUndo();
            return Save(root, "CalibrationPanel");
        }

        private static GameObject BuildHudPanel(Sprite rounded)
        {
            GameObject root = NewCanvasRoot("TeleopHud", 720f, 900f, 0.0005f); // 36 x 45 cm
            Image background = NewImage("Background", root.transform, rounded, new Color(0.04f, 0.05f, 0.09f, 0.92f));
            background.raycastTarget = false;
            Stretch(background.rectTransform);
            TMP_Text title = NewText("Title", root.transform, 50f, FontStyles.Bold, Gold, TextAlignmentOptions.Center, new Vector2(0f, 400f), new Vector2(660f, 70f));
            title.text = "Teleoperation";
            TMP_Text status = NewText("Status", root.transform, 33f, FontStyles.Normal, White, TextAlignmentOptions.TopLeft, new Vector2(0f, -35f), new Vector2(660f, 780f));
            status.richText = true;
            status.textWrappingMode = TextWrappingModes.Normal;
            status.lineSpacing = 10f;

            var hud = root.AddComponent<TeleopHud>();
            var so = new SerializedObject(hud);
            so.FindProperty("statusLabel").objectReferenceValue = status;
            so.ApplyModifiedPropertiesWithoutUndo();
            return Save(root, "TeleopHud");
        }

        private static GameObject BuildEStopPanel(Sprite rounded)
        {
            GameObject root = NewCanvasRoot("EStopPanel", 560f, 520f, 0.0005f); // 28 x 26 cm
            Image background = NewImage("Background", root.transform, rounded, new Color(0.04f, 0.05f, 0.09f, 0.92f));
            background.raycastTarget = false;
            Stretch(background.rectTransform);
            Button estop = NewButton("EStopButton", root.transform, rounded, new Vector2(0f, 70f), new Vector2(480f, 300f), new Color(0.92f, 0.08f, 0.06f), "E-STOP", 96f, Color.white);
            Button reset = NewButton("ResetButton", root.transform, rounded, new Vector2(0f, -135f), new Vector2(360f, 90f), new Color(1f, 0.80f, 0.30f), "Reset", 48f, new Color(0.12f, 0.09f, 0.02f));
            TMP_Text state = NewText("State", root.transform, 36f, FontStyles.Bold, White, TextAlignmentOptions.Center, new Vector2(0f, -215f), new Vector2(520f, 60f));
            state.richText = true;

            var panel = root.AddComponent<EStopPanel>();
            var so = new SerializedObject(panel);
            so.FindProperty("estopButton").objectReferenceValue = estop;
            so.FindProperty("resetButton").objectReferenceValue = reset;
            so.FindProperty("stateLabel").objectReferenceValue = state;
            so.ApplyModifiedPropertiesWithoutUndo();
            return Save(root, "EStopPanel");
        }

        private static GameObject BuildActionsPanel(Sprite rounded)
        {
            GameObject root = NewCanvasRoot("TeleopActionsPanel", 1000f, 540f, 0.0005f); // 50 x 27 cm
            Image background = NewImage("Background", root.transform, rounded, new Color(0.04f, 0.05f, 0.09f, 0.92f));
            background.raycastTarget = false;
            Stretch(background.rectTransform);

            var dark = new Color(0.16f, 0.20f, 0.30f);
            Button mode = NewButton("ModeButton", root.transform, rounded, new Vector2(0f, 195f), new Vector2(940f, 110f), new Color(0.85f, 0.62f, 0.18f), "Mode", 40f, new Color(0.12f, 0.09f, 0.02f));
            Button volume = NewButton("VolumeButton", root.transform, rounded, new Vector2(-330f, 60f), new Vector2(300f, 100f), dark, "Move volume", 36f, White);
            Button mark = NewButton("MarkButton", root.transform, rounded, new Vector2(0f, 60f), new Vector2(300f, 100f), dark, "Mark gem", 36f, White);
            Button clear = NewButton("ClearButton", root.transform, rounded, new Vector2(330f, 60f), new Vector2(300f, 100f), dark, "Clear marker", 36f, White);
            Button pick = NewButton("PickButton", root.transform, rounded, new Vector2(-235f, -80f), new Vector2(450f, 110f), new Color(0.30f, 0.78f, 0.45f), "Pick", 48f, new Color(0.04f, 0.12f, 0.06f));
            Button place = NewButton("PlaceButton", root.transform, rounded, new Vector2(235f, -80f), new Vector2(450f, 110f), new Color(0.30f, 0.55f, 0.85f), "Place", 48f, Color.white);
            TMP_Text hint = NewText("Hint", root.transform, 30f, FontStyles.Normal, new Color(0.72f, 0.75f, 0.82f), TextAlignmentOptions.Center, new Vector2(0f, -200f), new Vector2(940f, 70f));
            hint.textWrappingMode = TextWrappingModes.Normal;

            var panel = root.AddComponent<TeleopActionsPanel>();
            var so = new SerializedObject(panel);
            so.FindProperty("modeButton").objectReferenceValue = mode;
            so.FindProperty("modeLabel").objectReferenceValue = mode.GetComponentInChildren<TMP_Text>();
            so.FindProperty("volumeButton").objectReferenceValue = volume;
            so.FindProperty("volumeLabel").objectReferenceValue = volume.GetComponentInChildren<TMP_Text>();
            so.FindProperty("markButton").objectReferenceValue = mark;
            so.FindProperty("markLabel").objectReferenceValue = mark.GetComponentInChildren<TMP_Text>();
            so.FindProperty("clearButton").objectReferenceValue = clear;
            so.FindProperty("pickButton").objectReferenceValue = pick;
            so.FindProperty("placeButton").objectReferenceValue = place;
            so.FindProperty("hintLabel").objectReferenceValue = hint;
            so.ApplyModifiedPropertiesWithoutUndo();
            return Save(root, "TeleopActionsPanel");
        }

        /// <summary>Anchors a child to the top edge of its parent so it stays put when the panel grows downward.</summary>
        private static void ToTop(RectTransform rect, Vector2 positionFromTop)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(positionFromTop.x, -positionFromTop.y);
        }

        private static GameObject BuildAddressPanel(Sprite rounded)
        {
            GameObject root = NewCanvasRoot("RobotAddressPanel", 520f, 260f, 0.0005f); // 26 cm wide; grows to 43 cm with the keypad
            ((RectTransform)root.transform).pivot = new Vector2(0.5f, 1f);
            Image background = NewImage("Background", root.transform, rounded, new Color(0.04f, 0.05f, 0.09f, 0.92f));
            background.raycastTarget = false;
            Stretch(background.rectTransform);

            TMP_Text title = NewText("Title", root.transform, 36f, FontStyles.Bold, Gold, TextAlignmentOptions.Center, Vector2.zero, new Vector2(480f, 50f));
            title.text = "Robot address";
            ToTop(title.rectTransform, new Vector2(0f, 35f));
            TMP_Text address = NewText("Address", root.transform, 50f, FontStyles.Bold, White, TextAlignmentOptions.Center, Vector2.zero, new Vector2(500f, 70f));
            address.richText = true;
            ToTop(address.rectTransform, new Vector2(0f, 95f));
            TMP_Text status = NewText("Status", root.transform, 30f, FontStyles.Normal, White, TextAlignmentOptions.Center, Vector2.zero, new Vector2(500f, 44f));
            status.richText = true;
            ToTop(status.rectTransform, new Vector2(0f, 148f));
            Button edit = NewButton("EditButton", root.transform, rounded, Vector2.zero, new Vector2(240f, 64f), new Color(0.30f, 0.55f, 0.85f), "Edit", 36f, Color.white);
            ToTop((RectTransform)edit.transform, new Vector2(0f, 215f));

            var keypad = new GameObject("Keypad", typeof(RectTransform));
            keypad.transform.SetParent(root.transform, false);
            Stretch((RectTransform)keypad.transform);
            var dark = new Color(0.16f, 0.20f, 0.30f);
            string[] chars = { "1", "2", "3", "4", "5", "6", "7", "8", "9", ".", "0", ":" };
            var keys = new Button[chars.Length];
            for (int i = 0; i < chars.Length; i++)
            {
                int row = i / 3;
                int column = i % 3;
                keys[i] = NewButton("Key_" + chars[i], keypad.transform, rounded, Vector2.zero, new Vector2(135f, 100f), dark, chars[i], 52f, White);
                ToTop((RectTransform)keys[i].transform, new Vector2((column - 1) * 150f, 250f + row * 112f));
            }

            Button back = NewButton("BackspaceButton", keypad.transform, rounded, Vector2.zero, new Vector2(135f, 100f), new Color(0.85f, 0.35f, 0.30f), "<", 52f, Color.white);
            ToTop((RectTransform)back.transform, new Vector2(-150f, 250f + 4 * 112f));
            Button cancel = NewButton("CancelButton", keypad.transform, rounded, Vector2.zero, new Vector2(135f, 100f), dark, "X", 48f, White);
            ToTop((RectTransform)cancel.transform, new Vector2(0f, 250f + 4 * 112f));
            Button apply = NewButton("ApplyButton", keypad.transform, rounded, Vector2.zero, new Vector2(135f, 100f), new Color(0.30f, 0.78f, 0.45f), "OK", 48f, new Color(0.04f, 0.12f, 0.06f));
            ToTop((RectTransform)apply.transform, new Vector2(150f, 250f + 4 * 112f));

            var panel = root.AddComponent<RobotAddressPanel>();
            var so = new SerializedObject(panel);
            so.FindProperty("addressLabel").objectReferenceValue = address;
            so.FindProperty("statusLabel").objectReferenceValue = status;
            so.FindProperty("editButton").objectReferenceValue = edit;
            so.FindProperty("keypad").objectReferenceValue = keypad;
            so.FindProperty("keyButtons").arraySize = keys.Length;
            so.FindProperty("keyChars").arraySize = keys.Length;
            for (int i = 0; i < keys.Length; i++)
            {
                so.FindProperty("keyButtons").GetArrayElementAtIndex(i).objectReferenceValue = keys[i];
                so.FindProperty("keyChars").GetArrayElementAtIndex(i).stringValue = chars[i];
            }

            so.FindProperty("backspaceButton").objectReferenceValue = back;
            so.FindProperty("cancelButton").objectReferenceValue = cancel;
            so.FindProperty("applyButton").objectReferenceValue = apply;
            so.FindProperty("collapsedHeight").floatValue = 260f;
            so.FindProperty("expandedHeight").floatValue = 860f;
            so.ApplyModifiedPropertiesWithoutUndo();
            return Save(root, "RobotAddressPanel");
        }

        /// <summary>Gives a scene's address panel the RobotLinkConfig asset (the fallback when no address was saved).</summary>
        private static void WireAddress(GameObject address)
        {
            string[] guids = AssetDatabase.FindAssets("t:RobotLinkConfig");
            if (guids.Length == 0)
            {
                return;
            }

            var so = new SerializedObject(address.GetComponent<RobotAddressPanel>());
            so.FindProperty("config").objectReferenceValue = AssetDatabase.LoadAssetAtPath<RobotLinkConfig>(AssetDatabase.GUIDToAssetPath(guids[0]));
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static GameObject Save(GameObject root, string name)
        {
            SetLayerRecursive(root, LayerMask.NameToLayer("UI"));
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, $"{PrefabFolder}/{name}.prefab");
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static Image NewImage(string name, Transform parent, Sprite sprite, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
            image.color = color;
            image.raycastTarget = true;
            return image;
        }

        private static TMP_Text NewText(string name, Transform parent, float size, FontStyles style, Color color, TextAlignmentOptions alignment, Vector2 position, Vector2 sizeDelta)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Place(go.GetComponent<RectTransform>(), position, sizeDelta);
            var text = go.AddComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            return text;
        }

        private static Button NewButton(string name, Transform parent, Sprite sprite, Vector2 position, Vector2 size, Color color, string label, float fontSize, Color labelColor)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Place(go.GetComponent<RectTransform>(), position, size);
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.pixelsPerUnitMultiplier = 1.6f;
            image.color = color;
            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            ColorBlock colors = button.colors;
            colors.normalColor = new Color(0.92f, 0.92f, 0.92f);
            colors.highlightedColor = Color.white;
            colors.pressedColor = new Color(0.7f, 0.7f, 0.7f);
            colors.selectedColor = Color.white;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            TMP_Text text = NewText("Label", go.transform, fontSize, FontStyles.Bold, labelColor, TextAlignmentOptions.Center, Vector2.zero, Vector2.zero);
            Stretch(text.rectTransform);
            text.text = label;
            return button;
        }

        private static void Place(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
            {
                SetLayerRecursive(child.gameObject, layer);
            }
        }
    }

    /// <summary>Editor-only menu entries to test the calibration without touching real points.</summary>
    public static class TeleopDebugMenu
    {
        // Three predefined scene points 10 cm apart on a table in front of the user (P0 -> P1 along +X, P0 -> P2 along +Z).
        // With the default robot points (200,0,0) (300,0,0) (200,100,0) this puts the robot origin at (-0.2, 0.9, 0.6) and the workspace
        // box 30 cm wide, in front of the user.
        public static readonly Vector3[] FakePoints =
        {
            new Vector3(0.0f, 0.9f, 0.6f),
            new Vector3(0.1f, 0.9f, 0.6f),
            new Vector3(0.0f, 0.9f, 0.7f),
        };

        [MenuItem("GemStore/Teleop/Fake Calibration")]
        private static void FakeCalibration()
        {
            if (RobotCalibration.Instance == null)
            {
                Debug.LogWarning("[Teleop] No RobotCalibration in the scene (open the Maintenance scene).");
                return;
            }

            if (!RobotCalibration.Instance.ApplyCalibration(FakePoints, false, out string error))
            {
                Debug.LogWarning("[Teleop] Fake calibration failed: " + error);
            }
            else
            {
                Debug.Log("[Teleop] Fake calibration applied (not saved).");
            }
        }

        [MenuItem("GemStore/Teleop/Fake Calibration", true)]
        private static bool FakeValid() => Application.isPlaying;

        [MenuItem("GemStore/Teleop/Clear Saved Calibration")]
        private static void ClearSaved()
        {
            if (Application.isPlaying && RobotCalibration.Instance != null)
            {
                RobotCalibration.Instance.Clear();
            }
            else if (System.IO.File.Exists(RobotCalibration.SavePath))
            {
                System.IO.File.Delete(RobotCalibration.SavePath);
            }

            Debug.Log("[Teleop] Saved calibration cleared.");
        }
    }
}
