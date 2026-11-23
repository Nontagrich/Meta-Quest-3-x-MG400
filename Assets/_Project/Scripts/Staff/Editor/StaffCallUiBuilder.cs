using Agora.Rtc;
using GemStore.Showcase.Editor;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace GemStore.Staff.Editor
{
    /// <summary>
    /// Builds the StaffCallButton and StaffVideoPanel prefabs and puts them at CallButton_Anchor and StaffScreen_Anchor.
    /// The store layout is not touched (the grey stand-ins are replaced in place).
    /// </summary>
    public static class StaffCallUiBuilder
    {
        private const string PrefabFolder = "Assets/_Project/Prefabs/Staff";
        private const string MaterialFolder = "Assets/_Project/Materials/Staff";
        private const string RingPath = "Assets/_Project/Audio/Staff_Ring_Placeholder.wav";

        private const float PanelWidth = 800f, PanelHeight = 480f, PanelScale = 0.0005f; // 40 x 24 cm

        private static readonly Color Gold = new Color(1f, 0.82f, 0.38f);
        private static readonly Color White = new Color(0.96f, 0.97f, 1f);

        [MenuItem("GemStore/Staff Call/Build UI")]
        public static void Build()
        {
            GemInfoPanelBuilder.EnsureFolder(PrefabFolder);
            GemInfoPanelBuilder.EnsureFolder(MaterialFolder);

            GameObject buttonPrefab = BuildButtonPrefab();
            GameObject panelPrefab = BuildPanelPrefab();
            PlaceInScene(buttonPrefab, panelPrefab);

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("GemStore: staff call UI built and placed at the anchors.");
        }

        // ------------------------------------------------------------------------------------------------------
        // Button
        // ------------------------------------------------------------------------------------------------------

        private static GameObject BuildButtonPrefab()
        {
            Material brass = LoadStore("Store_Brass");
            Material walnut = LoadStore("Store_Walnut");
            Material idle = LightMaterial("Staff_LightIdle", new Color(0.95f, 0.95f, 0.95f));
            Material callOn = LightMaterial("Staff_LightCalling", new Color(1f, 0.82f, 0.1f));
            Material callOff = LightMaterial("Staff_LightCallingOff", new Color(0.25f, 0.20f, 0.03f), false);
            Material connected = LightMaterial("Staff_LightConnected", new Color(0.15f, 0.95f, 0.3f));

            var root = new GameObject("StaffCallButton");
            Cylinder(root.transform, "Base", new Vector3(0f, 0.007f, 0f), 0.13f, 0.014f, walnut, false);
            Cylinder(root.transform, "Ring", new Vector3(0f, 0.016f, 0f), 0.105f, 0.004f, brass, false);

            // the bell dome (the part that moves and that is touched)
            GameObject dome = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dome.name = "Dome";
            dome.transform.SetParent(root.transform, false);
            dome.transform.localPosition = new Vector3(0f, 0.018f, 0f);
            dome.transform.localScale = new Vector3(0.085f, 0.06f, 0.085f);
            dome.GetComponent<MeshRenderer>().sharedMaterial = brass;
            dome.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            Collider domeCollider = dome.GetComponent<Collider>(); // kept: rays and pokes hit this

            var interactable = dome.AddComponent<XRSimpleInteractable>();
            var poke = dome.AddComponent<XRPokeFilter>();
            var pokeSerialized = new SerializedObject(poke);
            pokeSerialized.FindProperty("m_Interactable").objectReferenceValue = interactable;
            pokeSerialized.FindProperty("m_PokeCollider").objectReferenceValue = domeCollider;
            pokeSerialized.ApplyModifiedPropertiesWithoutUndo();

            // status light on the front rim of the base (front = -Z, towards the user)
            GameObject lamp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            lamp.name = "StatusLight";
            lamp.transform.SetParent(root.transform, false);
            lamp.transform.localPosition = new Vector3(0f, 0.012f, -0.062f);
            lamp.transform.localScale = Vector3.one * 0.016f;
            Object.DestroyImmediate(lamp.GetComponent<Collider>());
            var lampRenderer = lamp.GetComponent<MeshRenderer>();
            lampRenderer.sharedMaterial = idle;
            lampRenderer.shadowCastingMode = ShadowCastingMode.Off;

            // "Call Staff" label standing behind the bell, facing the user
            var canvasObject = new GameObject("Label", typeof(RectTransform));
            canvasObject.transform.SetParent(root.transform, false);
            canvasObject.transform.localPosition = new Vector3(0f, 0.085f, 0.03f);
            var canvasRect = canvasObject.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(400f, 100f);
            canvasRect.localScale = Vector3.one * 0.0004f; // 16 x 4 cm
            canvasObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            TMP_Text label = NewText("Text", canvasObject.transform, 64f, FontStyles.Bold, Gold, TextAlignmentOptions.Center, Vector2.zero, new Vector2(400f, 100f));
            label.text = "Call Staff";
            canvasObject.layer = LayerMask.NameToLayer("UI");
            label.gameObject.layer = canvasObject.layer;

            var button = root.AddComponent<StaffCallButton>();
            var so = new SerializedObject(button);
            so.FindProperty("interactable").objectReferenceValue = interactable;
            so.FindProperty("dome").objectReferenceValue = dome.transform;
            so.FindProperty("lightRenderer").objectReferenceValue = lampRenderer;
            so.FindProperty("idleMaterial").objectReferenceValue = idle;
            so.FindProperty("callingOnMaterial").objectReferenceValue = callOn;
            so.FindProperty("callingOffMaterial").objectReferenceValue = callOff;
            so.FindProperty("connectedMaterial").objectReferenceValue = connected;
            so.ApplyModifiedPropertiesWithoutUndo();

            string path = PrefabFolder + "/StaffCallButton.prefab";
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        // ------------------------------------------------------------------------------------------------------
        // Video panel
        // ------------------------------------------------------------------------------------------------------

        private static GameObject BuildPanelPrefab()
        {
            Sprite rounded = GemInfoPanelBuilder.GetOrCreateRoundedSprite();
            AudioClip ring = PlaceholderAudio.GetOrCreateTone(RingPath, 1.6f, 6f, 880f, 698f, 880f, 698f);
            int uiLayer = LayerMask.NameToLayer("UI");

            var root = new GameObject("StaffVideoPanel");
            // dark back plate behind the canvas (3D), so the screen looks like a monitor
            GameObject back = GameObject.CreatePrimitive(PrimitiveType.Cube);
            back.name = "Back";
            back.transform.SetParent(root.transform, false);
            back.transform.localPosition = new Vector3(0f, 0f, 0.016f);
            back.transform.localScale = new Vector3(0.42f, 0.26f, 0.03f);
            Object.DestroyImmediate(back.GetComponent<Collider>());
            back.GetComponent<MeshRenderer>().sharedMaterial = LoadStore("Store_DarkGrey");
            back.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;

            var canvasObject = new GameObject("Canvas", typeof(RectTransform));
            canvasObject.transform.SetParent(root.transform, false);
            var rootRect = canvasObject.GetComponent<RectTransform>();
            rootRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            rootRect.localScale = Vector3.one * PanelScale;
            canvasObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            canvasObject.AddComponent<TrackedDeviceGraphicRaycaster>();

            var audio = root.AddComponent<AudioSource>();
            audio.clip = ring;
            audio.playOnAwake = false;
            audio.loop = true;
            audio.spatialBlend = 1f;
            audio.volume = 0.5f;
            audio.minDistance = 0.3f;
            audio.maxDistance = 6f;

            Image bezel = NewImage("Background", canvasObject.transform, rounded, new Color(0.04f, 0.05f, 0.09f, 1f));
            Stretch(bezel.rectTransform);
            bezel.raycastTarget = false;

            TMP_Text header = NewText("Header", canvasObject.transform, 44f, FontStyles.Bold, Gold, TextAlignmentOptions.Center, new Vector2(0f, 212f), new Vector2(760f, 56f));
            header.text = "Staff – Bangmod Gems";
            Image accent = NewImage("AccentLine", canvasObject.transform, null, Gold);
            Place(accent.rectTransform, new Vector2(0f, 180f), new Vector2(740f, 3f));
            accent.raycastTarget = false;

            // video area
            var areaObject = new GameObject("VideoArea", typeof(RectTransform));
            areaObject.transform.SetParent(canvasObject.transform, false);
            Place(areaObject.GetComponent<RectTransform>(), new Vector2(0f, 8f), new Vector2(760f, 330f));
            Image areaBackground = areaObject.AddComponent<Image>();
            areaBackground.color = new Color(0.01f, 0.01f, 0.03f, 1f);
            areaBackground.raycastTarget = false;

            var videoObject = new GameObject("Video", typeof(RectTransform));
            videoObject.transform.SetParent(areaObject.transform, false);
            Stretch(videoObject.GetComponent<RectTransform>());
            var videoImage = videoObject.AddComponent<RawImage>();
            videoImage.raycastTarget = false;
            videoImage.uvRect = new Rect(0f, 1f, 1f, -1f); // Agora delivers the frame upside down for a RawImage: flip it vertically
            var fitter = videoObject.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = 16f / 9f;
            var surface = videoObject.AddComponent<VideoSurface>();
            var surfaceSerialized = new SerializedObject(surface);
            surfaceSerialized.FindProperty("VideoSurfaceType").enumValueIndex = (int)VideoSurfaceType.RawImage;
            surfaceSerialized.ApplyModifiedPropertiesWithoutUndo();
            videoObject.SetActive(false);

            // placeholder: logo + message
            var placeholder = new GameObject("Placeholder", typeof(RectTransform));
            placeholder.transform.SetParent(areaObject.transform, false);
            Stretch(placeholder.GetComponent<RectTransform>());
            TMP_Text logo = NewText("Logo", placeholder.transform, 76f, FontStyles.Bold, Gold, TextAlignmentOptions.Center, new Vector2(0f, 40f), new Vector2(740f, 110f));
            logo.text = "Bangmod Gems";
            TMP_Text message = NewText("Message", placeholder.transform, 40f, FontStyles.Normal, White, TextAlignmentOptions.Center, new Vector2(0f, -50f), new Vector2(740f, 70f));
            message.text = "Need help? Press Call Staff";

            // controls: mic toggle and End Call
            var controls = new GameObject("Controls", typeof(RectTransform));
            controls.transform.SetParent(canvasObject.transform, false);
            Stretch(controls.GetComponent<RectTransform>());
            Button mute = NewButton("MuteButton", controls.transform, rounded, new Vector2(-200f, -200f), new Vector2(330f, 70f), new Color(0.20f, 0.45f, 0.30f), "Mic on", White, out Image muteImage, out TMP_Text muteLabel);
            Button end = NewButton("EndCallButton", controls.transform, rounded, new Vector2(200f, -200f), new Vector2(330f, 70f), new Color(0.80f, 0.15f, 0.15f), "End Call", Color.white, out _, out _);
            controls.SetActive(false);

            var panel = root.AddComponent<StaffVideoPanel>();
            var so = new SerializedObject(panel);
            so.FindProperty("placeholder").objectReferenceValue = placeholder;
            so.FindProperty("messageLabel").objectReferenceValue = message;
            so.FindProperty("videoImage").objectReferenceValue = videoImage;
            so.FindProperty("videoSurface").objectReferenceValue = surface;
            so.FindProperty("aspectFitter").objectReferenceValue = fitter;
            so.FindProperty("controls").objectReferenceValue = controls;
            so.FindProperty("muteButton").objectReferenceValue = mute;
            so.FindProperty("muteBackground").objectReferenceValue = muteImage;
            so.FindProperty("muteLabel").objectReferenceValue = muteLabel;
            so.FindProperty("endButton").objectReferenceValue = end;
            so.ApplyModifiedPropertiesWithoutUndo();

            SetLayerRecursive(canvasObject, uiLayer);

            string path = PrefabFolder + "/StaffVideoPanel.prefab";
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        // ------------------------------------------------------------------------------------------------------
        // Scene
        // ------------------------------------------------------------------------------------------------------

        private static void PlaceInScene(GameObject buttonPrefab, GameObject panelPrefab)
        {
            Transform staff = GameObject.Find("Store")?.transform.Find("StaffCorner");
            Transform buttonAnchor = FindDeep(staff, "CallButton_Anchor");
            Transform screenAnchor = FindDeep(staff, "StaffScreen_Anchor");
            Transform cornerAnchor = FindDeep(staff, "StaffCorner_Anchor");
            if (buttonAnchor == null || screenAnchor == null || cornerAnchor == null)
            {
                Debug.LogError("GemStore: StaffCorner anchors not found. Run GemStore/Arrange Store first.");
                return;
            }

            // remove what was placed before, and the grey stand-ins
            foreach (string name in new[] { "StaffCallButton", "StaffVideoPanel", "StaffCallController", "StaffScreen_Panel", "CallButton_Placeholder", "StaffScreen_Bracket" })
            {
                Transform old = FindDeep(staff, name);
                if (old != null)
                {
                    Object.DestroyImmediate(old.gameObject);
                }
            }

            // the bell sits on top of the pedestal (the anchor is 3 cm above its surface)
            var button = (GameObject)PrefabUtility.InstantiatePrefab(buttonPrefab, buttonAnchor);
            button.transform.localPosition = new Vector3(0f, -0.03f, 0f);
            button.transform.localRotation = Quaternion.identity;

            var panel = (GameObject)PrefabUtility.InstantiatePrefab(panelPrefab, screenAnchor);
            panel.transform.localPosition = Vector3.zero;
            panel.transform.localRotation = Quaternion.identity;

            // short bracket joining the stand post to the screen
            GameObject bracket = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bracket.name = "StaffScreen_Bracket";
            bracket.transform.SetParent(cornerAnchor, false);
            bracket.transform.localPosition = new Vector3(0f, 1.31f, -0.025f);
            bracket.transform.localScale = new Vector3(0.05f, 0.1f, 0.05f);
            Object.DestroyImmediate(bracket.GetComponent<Collider>());
            bracket.GetComponent<MeshRenderer>().sharedMaterial = LoadStore("Store_Brass");

            var controllerObject = new GameObject("StaffCallController");
            controllerObject.transform.SetParent(cornerAnchor, false);
            var controller = controllerObject.AddComponent<StaffCallController>();
            var so = new SerializedObject(controller);
            so.FindProperty("button").objectReferenceValue = button.GetComponent<StaffCallButton>();
            so.FindProperty("panel").objectReferenceValue = panel.GetComponent<StaffVideoPanel>();
            so.FindProperty("ringSource").objectReferenceValue = panel.GetComponent<AudioSource>();
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ------------------------------------------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------------------------------------------

        private static Material LoadStore(string name)
        {
            return AssetDatabase.LoadAssetAtPath<Material>($"Assets/_Project/Materials/Store/{name}.mat");
        }

        private static Material LightMaterial(string name, Color color, bool glow = true)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, path);
            }

            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", 0.6f);
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", glow ? color * 2.5f : color * 0.2f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static GameObject Cylinder(Transform parent, string name, Vector3 center, float diameter, float height, Material material, bool keepCollider)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.transform.localScale = new Vector3(diameter, height * 0.5f, diameter);
            if (!keepCollider)
            {
                Object.DestroyImmediate(go.GetComponent<Collider>());
            }

            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            go.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            return go;
        }

        private static Image NewImage(string name, Transform parent, Sprite sprite, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
            image.color = color;
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

        private static Button NewButton(string name, Transform parent, Sprite sprite, Vector2 position, Vector2 size, Color color, string label, Color labelColor, out Image image, out TMP_Text text)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Place(go.GetComponent<RectTransform>(), position, size);
            image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.pixelsPerUnitMultiplier = 1.6f;
            image.color = color;
            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            ColorBlock colors = button.colors;
            colors.normalColor = new Color(0.9f, 0.9f, 0.9f);
            colors.highlightedColor = Color.white;
            colors.pressedColor = new Color(0.65f, 0.65f, 0.65f);
            colors.selectedColor = Color.white;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            text = NewText("Label", go.transform, 40f, FontStyles.Bold, labelColor, TextAlignmentOptions.Center, Vector2.zero, Vector2.zero);
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

    /// <summary>Editor-only: join / leave the staff call from the menu while in Play Mode, without the in-scene button.</summary>
    public static class StaffCallDebugMenu
    {
        [MenuItem("GemStore/Staff Call/Join")]
        private static void Join()
        {
            if (StaffCallController.Instance != null)
            {
                StaffCallController.Instance.RequestCall();
            }
            else if (StaffCallManager.Instance != null)
            {
                StaffCallManager.Instance.JoinCall();
            }
        }

        [MenuItem("GemStore/Staff Call/Leave")]
        private static void Leave()
        {
            if (StaffCallController.Instance != null)
            {
                StaffCallController.Instance.EndCall();
            }
            else if (StaffCallManager.Instance != null)
            {
                StaffCallManager.Instance.LeaveCall();
            }
        }

        [MenuItem("GemStore/Staff Call/Join", true)]
        [MenuItem("GemStore/Staff Call/Leave", true)]
        private static bool OnlyInPlayMode()
        {
            return Application.isPlaying;
        }
    }
}
