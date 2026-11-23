using GemStore.Customize;
using GemStore.Payment;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace GemStore.Showcase.Editor
{
    /// <summary>
    /// Builds the GemInfoPanel prefab: a 18 x 16 cm World Space canvas (dark translucent rounded background,
    /// white/gold text) with an Add to Cart button that works with ray and poke input.
    /// </summary>
    public static class GemInfoPanelBuilder
    {
        public const string PrefabPath = "Assets/_Project/Prefabs/UI/GemInfoPanel.prefab";
        private const string UiFolder = "Assets/_Project/Prefabs/UI";
        private const string SpritesFolder = UiFolder + "/Sprites";
        private const string RoundedSpritePath = SpritesFolder + "/RoundedRect.png";
        private const string CheckSpritePath = SpritesFolder + "/CheckMark.png";
        private const string ClickSoundPath = "Assets/_Project/Audio/UI_Click_Placeholder.wav";

        // Canvas units: 1 unit = 0.2 mm (scale 0.0002), so 900 x 800 units = 18 x 16 cm.
        private const float CanvasWidth = 900f;
        private const float CanvasHeight = 800f;
        private const float CanvasScale = 0.0002f;

        private static readonly Color Gold = new Color(1f, 0.82f, 0.38f);
        private static readonly Color White = new Color(0.96f, 0.97f, 1f);
        private static readonly Color SoftWhite = new Color(0.78f, 0.82f, 0.90f);

        [MenuItem("GemStore/Build Info Panel")]
        public static void Build()
        {
            EnsureFolder(SpritesFolder);
            Sprite rounded = GetOrCreateRoundedSprite();
            Sprite check = GetOrCreateCheckSprite();
            Sprite circle = GetOrCreateCircleSprite();
            AudioClip click = PlaceholderAudio.GetOrCreateBeep(ClickSoundPath, 0.09f, 1046f, 1568f);

            int uiLayer = LayerMask.NameToLayer("UI");

            var root = new GameObject("GemInfoPanel", typeof(RectTransform));
            var rootRect = root.GetComponent<RectTransform>();
            rootRect.sizeDelta = new Vector2(CanvasWidth, CanvasHeight);
            rootRect.localScale = Vector3.one * CanvasScale;

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            root.AddComponent<TrackedDeviceGraphicRaycaster>();   // lets XR rays and pokes hit the canvas
            var canvasGroup = root.AddComponent<CanvasGroup>();
            var audio = root.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;
            audio.volume = 0.5f;
            audio.minDistance = 0.2f;
            audio.maxDistance = 5f;

            // Background: dark, translucent, rounded.
            Image background = NewImage("Background", root.transform, rounded, new Color(0.04f, 0.05f, 0.09f, 0.88f), 1f);
            Stretch(background.rectTransform);
            background.raycastTarget = true; // receives pointer enter/exit so the panel can stay while a pointer is on it

            // ---- name ---------------------------------------------------------------------------------------------
            TMP_Text title = NewText("Title", root.transform, 70f, FontStyles.Bold, White, TextAlignmentOptions.Center, new Vector2(0f, 335f), new Vector2(820f, 100f));
            Image accent = NewImage("AccentLine", root.transform, null, Gold, 1f);
            Place(accent.rectTransform, new Vector2(0f, 272f), new Vector2(780f, 4f));
            accent.raycastTarget = false;

            // ---- setting selector: Loose / Ring / Pendant -----------------------------------------------------------
            var settingButtons = new Button[3];
            var settingImages = new Image[3];
            var settingLabels = new TMP_Text[3];
            string[] settingNames = { "Loose", "Ring", "Pendant" };
            for (int i = 0; i < 3; i++)
            {
                settingButtons[i] = NewButton("Setting_" + settingNames[i], root.transform, rounded, new Vector2(-290f + i * 290f, 190f), new Vector2(270f, 96f),
                    new Color(0.20f, 0.22f, 0.30f), settingNames[i], 50f, new Color(0.86f, 0.88f, 0.94f), out settingImages[i], out settingLabels[i]);
            }

            // ---- metal swatches: Silver / Gold / Rose Gold (hidden for a loose gem) ---------------------------------
            var metalRow = new GameObject("MetalRow", typeof(RectTransform));
            metalRow.transform.SetParent(root.transform, false);
            Place(metalRow.GetComponent<RectTransform>(), new Vector2(0f, 45f), new Vector2(880f, 190f));
            var metalButtons = new Button[3];
            var metalOutlines = new Image[3];
            Color[] metalColors = { new Color(0.84f, 0.85f, 0.88f), new Color(1.00f, 0.77f, 0.34f), new Color(0.93f, 0.58f, 0.48f) };
            string[] metalNames = { "Silver", "Gold", "Rose Gold" };
            for (int i = 0; i < 3; i++)
            {
                float x = -290f + i * 290f;
                Image outline = NewImage("Outline_" + metalNames[i], metalRow.transform, circle, Color.white, 1f);
                Place(outline.rectTransform, new Vector2(x, 28f), new Vector2(134f, 134f));
                outline.raycastTarget = false;
                metalOutlines[i] = outline;

                var swatch = new GameObject("Metal_" + metalNames[i], typeof(RectTransform));
                swatch.transform.SetParent(metalRow.transform, false);
                Place(swatch.GetComponent<RectTransform>(), new Vector2(x, 28f), new Vector2(112f, 112f));
                Image swatchImage = swatch.AddComponent<Image>();
                swatchImage.sprite = circle;
                swatchImage.color = metalColors[i];
                Button swatchButton = swatch.AddComponent<Button>();
                swatchButton.targetGraphic = swatchImage;
                swatchButton.navigation = new Navigation { mode = Navigation.Mode.None };
                ColorBlock swatchColors = swatchButton.colors;
                swatchColors.normalColor = Color.white;
                swatchColors.highlightedColor = new Color(0.92f, 0.92f, 0.92f);
                swatchColors.pressedColor = new Color(0.7f, 0.7f, 0.7f);
                swatchColors.selectedColor = Color.white;
                swatchColors.fadeDuration = 0.08f;
                swatchButton.colors = swatchColors;
                metalButtons[i] = swatchButton;

                TMP_Text metalLabel = NewText("Label_" + metalNames[i], metalRow.transform, 40f, FontStyles.Normal, White, TextAlignmentOptions.Center, new Vector2(x, -62f), new Vector2(270f, 56f));
                metalLabel.text = metalNames[i];
            }

            // "Popular" tag next to the Gold swatch (shown while Ring is selected)
            Image tag = NewImage("PopularTag", metalRow.transform, rounded, new Color(0.88f, 0.22f, 0.30f), 3f);
            Place(tag.rectTransform, new Vector2(60f, 88f), new Vector2(150f, 44f));
            tag.raycastTarget = false;
            TMP_Text tagLabel = NewText("Text", tag.transform, 30f, FontStyles.Bold, Color.white, TextAlignmentOptions.Center, Vector2.zero, Vector2.zero);
            Stretch(tagLabel.rectTransform);
            tagLabel.text = "Popular";

            // ---- price line: "฿12,900 + Ring ฿8,580 = ฿21,480" -----------------------------------------------------
            TMP_Text price = NewText("Price", root.transform, 46f, FontStyles.Bold, Gold, TextAlignmentOptions.Center, new Vector2(0f, -112f), new Vector2(860f, 80f));
            price.enableAutoSizing = true;
            price.fontSizeMin = 30f;
            price.fontSizeMax = 46f;

            // ---- Add to Cart button -----------------------------------------------------------------------------------
            var buttonObject = new GameObject("AddToCartButton", typeof(RectTransform));
            buttonObject.transform.SetParent(root.transform, false);
            Place(buttonObject.GetComponent<RectTransform>(), new Vector2(0f, -272f), new Vector2(580f, 118f));
            Image buttonImage = buttonObject.AddComponent<Image>();
            buttonImage.sprite = rounded;
            buttonImage.type = Image.Type.Sliced;
            buttonImage.pixelsPerUnitMultiplier = 1.6f;
            buttonImage.color = Gold;
            Button button = buttonObject.AddComponent<Button>();
            button.targetGraphic = buttonImage;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            ColorBlock colors = button.colors;
            colors.normalColor = new Color(0.88f, 0.88f, 0.88f);
            colors.highlightedColor = Color.white;
            colors.pressedColor = new Color(0.68f, 0.68f, 0.68f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.82f, 0.82f, 0.82f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            TMP_Text buttonLabel = NewText("Label", buttonObject.transform, 56f, FontStyles.Bold, new Color(0.12f, 0.09f, 0.02f), TextAlignmentOptions.Center, Vector2.zero, Vector2.zero);
            Stretch(buttonLabel.rectTransform);
            buttonLabel.text = "Add to Cart";

            Image checkIcon = NewImage("CheckIcon", buttonObject.transform, check, new Color(0.12f, 0.09f, 0.02f), 1f);
            Place(checkIcon.rectTransform, new Vector2(-105f, 0f), new Vector2(60f, 60f));
            checkIcon.raycastTarget = false;
            checkIcon.enabled = false;

            var addButton = buttonObject.AddComponent<AddToCartButton>();
            var addSerialized = new SerializedObject(addButton);
            addSerialized.FindProperty("button").objectReferenceValue = button;
            addSerialized.FindProperty("background").objectReferenceValue = buttonImage;
            addSerialized.FindProperty("label").objectReferenceValue = buttonLabel;
            addSerialized.FindProperty("checkIcon").objectReferenceValue = checkIcon;
            addSerialized.FindProperty("audioSource").objectReferenceValue = audio;
            addSerialized.FindProperty("clickClip").objectReferenceValue = click;
            addSerialized.ApplyModifiedPropertiesWithoutUndo();

            // ---- customization component ---------------------------------------------------------------------------------
            var customize = root.AddComponent<GemCustomizePanel>();
            var customizeSerialized = new SerializedObject(customize);
            SetArray(customizeSerialized.FindProperty("settingButtons"), settingButtons);
            SetArray(customizeSerialized.FindProperty("settingImages"), settingImages);
            SetArray(customizeSerialized.FindProperty("settingLabels"), settingLabels);
            customizeSerialized.FindProperty("metalRow").objectReferenceValue = metalRow;
            SetArray(customizeSerialized.FindProperty("metalButtons"), metalButtons);
            SetArray(customizeSerialized.FindProperty("metalOutlines"), metalOutlines);
            customizeSerialized.FindProperty("popularTag").objectReferenceValue = tag.gameObject;
            customizeSerialized.FindProperty("priceLine").objectReferenceValue = price;
            customizeSerialized.FindProperty("addButton").objectReferenceValue = addButton;
            customizeSerialized.FindProperty("audioSource").objectReferenceValue = audio;
            customizeSerialized.FindProperty("clickClip").objectReferenceValue = click;
            customizeSerialized.ApplyModifiedPropertiesWithoutUndo();

            // ---- panel component -------------------------------------------------------------------------------------------
            var panel = root.AddComponent<GemInfoPanel>();
            var panelSerialized = new SerializedObject(panel);
            panelSerialized.FindProperty("canvas").objectReferenceValue = canvas;
            panelSerialized.FindProperty("canvasGroup").objectReferenceValue = canvasGroup;
            panelSerialized.FindProperty("nameLabel").objectReferenceValue = title;
            panelSerialized.FindProperty("priceLabel").objectReferenceValue = price;
            panelSerialized.FindProperty("addButton").objectReferenceValue = addButton;
            panelSerialized.FindProperty("customize").objectReferenceValue = customize;
            panelSerialized.FindProperty("closeButton").objectReferenceValue = CreateCloseButton(root.transform, rounded);
            panelSerialized.ApplyModifiedPropertiesWithoutUndo();

            SetLayerRecursive(root, uiLayer);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("GemStore: GemInfoPanel prefab built.");
        }

        /// <summary>Adds the ✕ close button to the existing prefab (keeps everything else as it is).</summary>
        [MenuItem("GemStore/Add Info Panel Close Button")]
        public static void AddCloseButtonToPrefab()
        {
            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var panel = root.GetComponent<GemInfoPanel>();
                Transform old = root.transform.Find("CloseButton");
                if (old != null)
                {
                    Object.DestroyImmediate(old.gameObject);
                }

                Button close = CreateCloseButton(root.transform, GetOrCreateRoundedSprite());
                SetLayerRecursive(close.gameObject, root.layer);
                var serialized = new SerializedObject(panel);
                serialized.FindProperty("closeButton").objectReferenceValue = close;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                Debug.Log("GemStore: close button added to the GemInfoPanel prefab.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>Small round ✕ in the top-right corner (84 units = 1.7 cm). The cross is two bars, so no font glyph is needed.</summary>
        private static Button CreateCloseButton(Transform parent, Sprite rounded)
        {
            var go = new GameObject("CloseButton", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Place(go.GetComponent<RectTransform>(), new Vector2(CanvasWidth * 0.5f - 62f, CanvasHeight * 0.5f - 62f), new Vector2(84f, 84f));
            Image image = go.AddComponent<Image>();
            image.sprite = rounded;
            image.type = Image.Type.Sliced;
            image.pixelsPerUnitMultiplier = 3f;
            image.color = new Color(0.20f, 0.22f, 0.30f);
            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            ColorBlock colors = button.colors;
            colors.normalColor = new Color(0.92f, 0.92f, 0.92f);
            colors.highlightedColor = new Color(1f, 0.6f, 0.6f);
            colors.pressedColor = new Color(0.7f, 0.4f, 0.4f);
            colors.selectedColor = Color.white;
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            for (int i = 0; i < 2; i++)
            {
                Image bar = NewImage(i == 0 ? "Bar1" : "Bar2", go.transform, null, White, 1f);
                Place(bar.rectTransform, Vector2.zero, new Vector2(50f, 9f));
                bar.rectTransform.localRotation = Quaternion.Euler(0f, 0f, i == 0 ? 45f : -45f);
                bar.raycastTarget = false;
            }

            return button;
        }

        private static void SetArray<T>(SerializedProperty property, T[] values) where T : Object
        {
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }
        }

        private static Button NewButton(string name, Transform parent, Sprite sprite, Vector2 position, Vector2 size, Color color, string label, float fontSize, Color labelColor, out Image image, out TMP_Text text)
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
            colors.normalColor = new Color(0.92f, 0.92f, 0.92f);
            colors.highlightedColor = Color.white;
            colors.pressedColor = new Color(0.7f, 0.7f, 0.7f);
            colors.selectedColor = Color.white;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            text = NewText("Label", go.transform, fontSize, FontStyles.Bold, labelColor, TextAlignmentOptions.Center, Vector2.zero, Vector2.zero);
            Stretch(text.rectTransform);
            text.text = label;
            return button;
        }

        /// <summary>White filled circle, tinted per Image (swatches and their selection ring).</summary>
        public static Sprite GetOrCreateCircleSprite()
        {
            const string path = SpritesFolder + "/Circle.png";
            if (AssetDatabase.LoadAssetAtPath<Sprite>(path) == null)
            {
                const int size = 128;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                var pixels = new Color[size * size];
                float half = size * 0.5f;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(half, half)) - (half - 1f);
                        pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - distance));
                    }
                }

                texture.SetPixels(pixels);
                System.IO.File.WriteAllBytes(path, texture.EncodeToPNG());
                Object.DestroyImmediate(texture);
                AssetDatabase.ImportAsset(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        private static Image NewImage(string name, Transform parent, Sprite sprite, Color color, float pixelsPerUnitMultiplier)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.type = sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            image.pixelsPerUnitMultiplier = pixelsPerUnitMultiplier;
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

        private static void Place(RectTransform rect, Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;
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

        /// <summary>White rounded rectangle with a 9-slice border, tinted per Image.</summary>
        public static Sprite GetOrCreateRoundedSprite()
        {
            if (AssetDatabase.LoadAssetAtPath<Sprite>(RoundedSpritePath) == null)
            {
                const int size = 128;
                const float radius = 48f;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                var pixels = new Color[size * size];
                float half = size * 0.5f;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        // Signed distance to a rounded box (negative inside).
                        Vector2 p = new Vector2(Mathf.Abs(x + 0.5f - half), Mathf.Abs(y + 0.5f - half));
                        Vector2 q = p - new Vector2(half - radius, half - radius);
                        float distance = new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - radius;
                        pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - distance));
                    }
                }

                texture.SetPixels(pixels);
                System.IO.File.WriteAllBytes(RoundedSpritePath, texture.EncodeToPNG());
                Object.DestroyImmediate(texture);
                AssetDatabase.ImportAsset(RoundedSpritePath);

                var importer = (TextureImporter)AssetImporter.GetAtPath(RoundedSpritePath);
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteBorder = new Vector4(48f, 48f, 48f, 48f);
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
            }

            // New sprites default to "Multiple" import mode, which ignores the 9-slice border. It must be "Single".
            var existing = (TextureImporter)AssetImporter.GetAtPath(RoundedSpritePath);
            if (existing.spriteImportMode != SpriteImportMode.Single || existing.spriteBorder.x < 1f)
            {
                existing.spriteImportMode = SpriteImportMode.Single;
                existing.spriteBorder = new Vector4(48f, 48f, 48f, 48f);
                existing.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Sprite>(RoundedSpritePath);
        }

        /// <summary>White check mark (the TMP font has no check glyph), tinted per Image.</summary>
        public static Sprite GetOrCreateCheckSprite()
        {
            if (AssetDatabase.LoadAssetAtPath<Sprite>(CheckSpritePath) == null)
            {
                const int size = 128;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                var pixels = new Color[size * size];
                Vector2 a = new Vector2(0.18f, 0.52f);
                Vector2 b = new Vector2(0.42f, 0.28f);
                Vector2 c = new Vector2(0.82f, 0.76f);
                const float thickness = 0.075f;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        Vector2 p = new Vector2((x + 0.5f) / size, (y + 0.5f) / size);
                        float distance = Mathf.Min(DistanceToSegment(p, a, b), DistanceToSegment(p, b, c)) - thickness;
                        pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - distance * size));
                    }
                }

                texture.SetPixels(pixels);
                System.IO.File.WriteAllBytes(CheckSpritePath, texture.EncodeToPNG());
                Object.DestroyImmediate(texture);
                AssetDatabase.ImportAsset(CheckSpritePath);

                var importer = (TextureImporter)AssetImporter.GetAtPath(CheckSpritePath);
                importer.textureType = TextureImporterType.Sprite;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
            }

            var checkImporter = (TextureImporter)AssetImporter.GetAtPath(CheckSpritePath);
            if (checkImporter.spriteImportMode != SpriteImportMode.Single)
            {
                checkImporter.spriteImportMode = SpriteImportMode.Single;
                checkImporter.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Sprite>(CheckSpritePath);
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }

        public static void EnsureFolder(string path)
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
