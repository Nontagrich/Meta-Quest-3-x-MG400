using System;
using GemStore.Showcase.Editor;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Attachment;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace GemStore.Payment.Editor
{
    /// <summary>
    /// Builds the checkout area: CartDisplay, CreditCard and PaymentTerminal prefabs and the CheckoutCounter that holds them,
    /// then places the counter next to the showcase cabinet.
    /// </summary>
    public static class PaymentAssetBuilder
    {
        private const string UiFolder = "Assets/_Project/Prefabs/UI";
        private const string PaymentFolder = "Assets/_Project/Prefabs/Payment";
        private const string SpritesFolder = UiFolder + "/Sprites";
        private const string MaterialsFolder = "Assets/_Project/Materials/Checkout";

        private const string CartDisplayPath = UiFolder + "/CartDisplay.prefab";
        private const string CreditCardPath = PaymentFolder + "/CreditCard.prefab";
        private const string TerminalPath = PaymentFolder + "/PaymentTerminal.prefab";
        private const string CounterPath = PaymentFolder + "/CheckoutCounter.prefab";
        private const string GiftBoxPath = PaymentFolder + "/GiftBox.prefab";
        private const string AudioFolder = "Assets/_Project/Audio";
        private const string CrossSpritePath = SpritesFolder + "/CrossMark.png";
        private const string ContactlessSpritePath = SpritesFolder + "/Contactless.png";
        private const string CheckoutLayerName = "Checkout";

        private static readonly Color Gold = new Color(1f, 0.82f, 0.38f);
        private static readonly Color White = new Color(0.96f, 0.97f, 1f);
        private static readonly Color SoftWhite = new Color(0.78f, 0.82f, 0.90f);

        [MenuItem("GemStore/Build Checkout")]
        public static void BuildAll()
        {
            GemInfoPanelBuilder.EnsureFolder(SpritesFolder);
            GemInfoPanelBuilder.EnsureFolder(PaymentFolder);
            GemInfoPanelBuilder.EnsureFolder(MaterialsFolder);

            BuildCartDisplay();
            BuildCreditCard();
            BuildGiftBox();
            BuildTerminal();
            BuildCounter();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("GemStore: checkout prefabs built (CartDisplay, CreditCard, PaymentTerminal, CheckoutCounter).");
        }

        [MenuItem("GemStore/Place Checkout Counter")]
        public static void PlaceCounter()
        {
            // Counter top at ~1.0 m. Its left edge is ~0.5 m to the right of the cabinet; it faces the XR Origin.
            const float x = 1.15f;
            const float z = 0.70f;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CounterPath);
            if (prefab == null)
            {
                Debug.LogError("GemStore: CheckoutCounter prefab missing. Run GemStore/Build Checkout first.");
                return;
            }

            var scene = EditorSceneManager.GetActiveScene();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == "CheckoutCounter")
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }

            var xrOrigin = UnityEngine.Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
            Vector3 origin = xrOrigin != null ? xrOrigin.transform.position : Vector3.zero;
            Vector3 position = new Vector3(x, 0f, z);
            Vector3 away = position - origin;
            away.y = 0f;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.transform.position = position;
            // The front of the counter is local -Z, so local +Z must point away from the user.
            instance.transform.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);

            if (UnityEngine.Object.FindAnyObjectByType<CartManager>() == null)
            {
                new GameObject("CartManager").AddComponent<CartManager>();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Selection.activeGameObject = instance;
            Debug.Log("GemStore: CheckoutCounter placed in the scene.");
        }

        // ------------------------------------------------------------------------------------------------
        // CartDisplay
        // ------------------------------------------------------------------------------------------------
        private static void BuildCartDisplay()
        {
            Sprite rounded = GemInfoPanelBuilder.GetOrCreateRoundedSprite();
            Sprite cross = GetOrCreateSprite(CrossSpritePath, 128, CrossAlpha);

            var root = new GameObject("CartDisplay", typeof(RectTransform));
            var rootRect = root.GetComponent<RectTransform>();
            rootRect.pivot = new Vector2(0.5f, 0f); // grows upwards from the bottom edge
            rootRect.sizeDelta = new Vector2(750f, 250f);
            rootRect.localScale = Vector3.one * 0.0004f; // 1 unit = 0.4 mm -> 30 cm wide

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            root.AddComponent<TrackedDeviceGraphicRaycaster>();
            var display = root.AddComponent<CartDisplay>();

            Image background = NewImage("Background", root.transform, rounded, new Color(0.04f, 0.05f, 0.09f, 0.9f));
            PlaceBottom(background.rectTransform, new Vector2(0f, 0f), new Vector2(750f, 250f), 0f);
            background.raycastTarget = true;

            TMP_Text title = NewText("Title", root.transform, 52f, FontStyles.Bold, White, TextAlignmentOptions.Center, Vector2.zero, new Vector2(690f, 90f));
            title.text = "Your Cart";
            PlaceBottom(title.rectTransform, new Vector2(0f, 175f), new Vector2(690f, 90f), 0.5f);

            TMP_Text empty = NewText("EmptyLabel", root.transform, 44f, FontStyles.Normal, SoftWhite, TextAlignmentOptions.Center, Vector2.zero, new Vector2(690f, 90f));
            empty.text = "Your cart is empty";
            PlaceBottom(empty.rectTransform, new Vector2(0f, 75f), new Vector2(690f, 90f), 0.5f);

            TMP_Text total = NewText("TotalLabel", root.transform, 56f, FontStyles.Bold, Gold, TextAlignmentOptions.Right, Vector2.zero, new Vector2(690f, 100f));
            PlaceBottom(total.rectTransform, new Vector2(0f, 80f), new Vector2(690f, 100f), 0.5f);

            var display0 = new SerializedObject(display);
            SerializedProperty rowsProperty = display0.FindProperty("rows");
            rowsProperty.arraySize = 9;
            for (int i = 0; i < 9; i++)
            {
                var row = new GameObject($"Row_{i}", typeof(RectTransform));
                row.transform.SetParent(root.transform, false);
                PlaceBottom(row.GetComponent<RectTransform>(), new Vector2(0f, 100f), new Vector2(690f, 70f), 0.5f);

                TMP_Text nameLabel = NewText("Name", row.transform, 40f, FontStyles.Normal, White, TextAlignmentOptions.Left, new Vector2(-155f, 0f), new Vector2(380f, 70f));
                TMP_Text priceLabel = NewText("Price", row.transform, 40f, FontStyles.Bold, Gold, TextAlignmentOptions.Right, new Vector2(145f, 0f), new Vector2(210f, 70f));

                var buttonObject = new GameObject("RemoveButton", typeof(RectTransform));
                buttonObject.transform.SetParent(row.transform, false);
                Place(buttonObject.GetComponent<RectTransform>(), new Vector2(305f, 0f), new Vector2(64f, 64f));
                Image buttonImage = buttonObject.AddComponent<Image>();
                buttonImage.sprite = rounded;
                buttonImage.type = Image.Type.Sliced;
                buttonImage.pixelsPerUnitMultiplier = 3f;
                buttonImage.color = new Color(0.85f, 0.27f, 0.30f);
                Button button = buttonObject.AddComponent<Button>();
                button.targetGraphic = buttonImage;
                button.navigation = new Navigation { mode = Navigation.Mode.None };
                ColorBlock colors = button.colors;
                colors.normalColor = new Color(0.9f, 0.9f, 0.9f);
                colors.highlightedColor = Color.white;
                colors.pressedColor = new Color(0.7f, 0.7f, 0.7f);
                colors.selectedColor = Color.white;
                colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 1f);
                colors.fadeDuration = 0.08f;
                button.colors = colors;
                var removeButton = buttonObject.AddComponent<CartRemoveButton>();
                var removeSerialized = new SerializedObject(removeButton);
                removeSerialized.FindProperty("button").objectReferenceValue = button;
                removeSerialized.ApplyModifiedPropertiesWithoutUndo();

                Image icon = NewImage("Icon", buttonObject.transform, cross, Color.white);
                Place(icon.rectTransform, Vector2.zero, new Vector2(36f, 36f));
                icon.raycastTarget = false;

                SerializedProperty element = rowsProperty.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("root").objectReferenceValue = row;
                element.FindPropertyRelative("nameLabel").objectReferenceValue = nameLabel;
                element.FindPropertyRelative("priceLabel").objectReferenceValue = priceLabel;
                element.FindPropertyRelative("removeButton").objectReferenceValue = removeButton;

                row.SetActive(false); // rows are switched on by CartDisplay when the cart has items
            }

            display0.FindProperty("canvas").objectReferenceValue = canvas;
            display0.FindProperty("background").objectReferenceValue = background.rectTransform;
            display0.FindProperty("titleLabel").objectReferenceValue = title;
            display0.FindProperty("emptyLabel").objectReferenceValue = empty;
            display0.FindProperty("totalLabel").objectReferenceValue = total;
            display0.ApplyModifiedPropertiesWithoutUndo();

            ShowcaseCabinetBuilder.SetLayerRecursive(root, LayerMask.NameToLayer("UI"));
            PrefabUtility.SaveAsPrefabAsset(root, CartDisplayPath);
            UnityEngine.Object.DestroyImmediate(root);
        }

        // ------------------------------------------------------------------------------------------------
        // CreditCard
        // ------------------------------------------------------------------------------------------------
        private static void BuildCreditCard()
        {
            Material black = GetMaterial("Card_Black", new Color(0.03f, 0.03f, 0.035f), 0.85f, 0.5f);
            Material gold = GetMaterial("Card_Gold", new Color(0.92f, 0.70f, 0.22f), 0.75f, 0.9f);

            const float width = 0.0856f;  // ISO/IEC 7810 ID-1: 85.6 x 54 mm
            const float depth = 0.054f;

            var root = new GameObject("CreditCard");
            var collider = root.AddComponent<BoxCollider>();
            collider.size = new Vector3(width, 0.012f, depth); // thicker than the visible card so it is easy to grab

            var body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            var grab = root.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grab.trackPosition = true;
            grab.trackRotation = true;
            grab.trackScale = false;
            grab.useDynamicAttach = true;
            grab.farAttachMode = InteractableFarAttachMode.Near;
            grab.attachEaseInTime = 0.25f;
            grab.selectMode = InteractableSelectMode.Single;
            grab.throwOnDetach = false;
            grab.retainTransformParent = true;

            var card = root.AddComponent<CreditCard>();
            var cardSerialized = new SerializedObject(card);
            cardSerialized.FindProperty("grabInteractable").objectReferenceValue = grab;
            cardSerialized.ApplyModifiedPropertiesWithoutUndo();

            Box(root.transform, "Body", Vector3.zero, new Vector3(width, 0.0012f, depth), black, false);
            Box(root.transform, "GoldStripe", new Vector3(0f, 0.0001f, -0.0215f), new Vector3(width, 0.0013f, 0.0055f), gold, false);
            Box(root.transform, "Chip", new Vector3(-0.027f, 0.0001f, 0.004f), new Vector3(0.0115f, 0.0013f, 0.009f), gold, false);

            // Shop name printed on the top face (canvas turned to face up).
            var canvasObject = new GameObject("BrandCanvas", typeof(RectTransform));
            canvasObject.transform.SetParent(root.transform, false);
            canvasObject.transform.localPosition = new Vector3(0f, 0.0008f, 0f);
            canvasObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            canvasObject.transform.localScale = Vector3.one * 0.0001f;
            canvasObject.GetComponent<RectTransform>().sizeDelta = new Vector2(856f, 540f);
            canvasObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            TMP_Text brand = NewText("Brand", canvasObject.transform, 78f, FontStyles.Bold, new Color(0.95f, 0.76f, 0.30f), TextAlignmentOptions.Center, new Vector2(110f, 120f), new Vector2(560f, 110f));
            brand.text = "Bangmod Gems";

            PrefabUtility.SaveAsPrefabAsset(root, CreditCardPath);
            UnityEngine.Object.DestroyImmediate(root);
        }

        // ------------------------------------------------------------------------------------------------
        // GiftBox
        // ------------------------------------------------------------------------------------------------
        private static void BuildGiftBox()
        {
            Material rose = GetMaterial("Gift_Rose", new Color(0.78f, 0.13f, 0.25f), 0.55f, 0f);
            Material roseDark = GetMaterial("Gift_RoseDark", new Color(0.62f, 0.08f, 0.20f), 0.55f, 0f);
            Material gold = GetMaterial("Card_Gold", new Color(0.92f, 0.70f, 0.22f), 0.75f, 0.9f);
            AudioClip pop = PlaceholderAudio.GetOrCreateTone(AudioFolder + "/GiftBox_Pop.wav", 0.18f, 14f, 784f, 1175f);

            var root = new GameObject("GiftBox");
            var visual = new GameObject("Visual");
            visual.transform.SetParent(root.transform, false);
            Transform v = visual.transform;

            // Box (0.12 m) with a slightly wider lid, crossed ribbons and a bow.
            Box(v, "Base", new Vector3(0f, 0.035f, 0f), new Vector3(0.12f, 0.07f, 0.12f), rose, false);
            Box(v, "Lid", new Vector3(0f, 0.083f, 0f), new Vector3(0.13f, 0.026f, 0.13f), roseDark, false);
            Box(v, "RibbonBaseA", new Vector3(0f, 0.035f, 0f), new Vector3(0.022f, 0.07f, 0.123f), gold, false);
            Box(v, "RibbonBaseB", new Vector3(0f, 0.035f, 0f), new Vector3(0.123f, 0.07f, 0.022f), gold, false);
            Box(v, "RibbonLidA", new Vector3(0f, 0.083f, 0f), new Vector3(0.022f, 0.028f, 0.133f), gold, false);
            Box(v, "RibbonLidB", new Vector3(0f, 0.083f, 0f), new Vector3(0.133f, 0.028f, 0.022f), gold, false);
            Box(v, "BowKnot", new Vector3(0f, 0.104f, 0f), new Vector3(0.024f, 0.016f, 0.024f), gold, false);
            GameObject loopLeft = Shape(v, PrimitiveType.Sphere, "BowLoopLeft", new Vector3(-0.026f, 0.112f, 0f), new Vector3(0.05f, 0.03f, 0.022f), gold);
            loopLeft.transform.localRotation = Quaternion.Euler(0f, 0f, 28f);
            GameObject loopRight = Shape(v, PrimitiveType.Sphere, "BowLoopRight", new Vector3(0.026f, 0.112f, 0f), new Vector3(0.05f, 0.03f, 0.022f), gold);
            loopRight.transform.localRotation = Quaternion.Euler(0f, 0f, -28f);

            var audio = root.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;
            audio.volume = 0.6f;
            audio.minDistance = 0.3f;
            audio.maxDistance = 6f;

            var giftBox = root.AddComponent<GiftBox>();
            var serialized = new SerializedObject(giftBox);
            serialized.FindProperty("visual").objectReferenceValue = visual.transform;
            serialized.FindProperty("audioSource").objectReferenceValue = audio;
            serialized.FindProperty("popClip").objectReferenceValue = pop;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            ShowcaseCabinetBuilder.SetLayerRecursive(root, ShowcaseCabinetBuilder.EnsureLayer(CheckoutLayerName));
            PrefabUtility.SaveAsPrefabAsset(root, GiftBoxPath);
            UnityEngine.Object.DestroyImmediate(root);
        }

        // ------------------------------------------------------------------------------------------------
        // PaymentTerminal
        // ------------------------------------------------------------------------------------------------
        private static void BuildTerminal()
        {
            Sprite rounded = GemInfoPanelBuilder.GetOrCreateRoundedSprite();
            Sprite check = GemInfoPanelBuilder.GetOrCreateCheckSprite();
            Sprite contactless = GetOrCreateSprite(ContactlessSpritePath, 128, ContactlessAlpha);
            Material shell = GetMaterial("Terminal_Shell", new Color(0.05f, 0.05f, 0.06f), 0.5f, 0.4f);
            Material dark = GetMaterial("Counter_Dock", new Color(0.09f, 0.09f, 0.10f), 0.6f, 0f);
            AudioClip beep = PlaceholderAudio.GetOrCreateTone(AudioFolder + "/Terminal_Beep.wav", 0.12f, 18f, 1318f);
            AudioClip success = PlaceholderAudio.GetOrCreateTone(AudioFolder + "/Payment_Success.wav", 0.8f, 5f, 1047f, 1319f, 1568f, 2093f);
            var giftBoxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GiftBoxPath);

            var root = new GameObject("PaymentTerminal");
            Box(root.transform, "Base", new Vector3(0f, 0.005f, 0f), new Vector3(0.17f, 0.01f, 0.1f), dark, false);

            // The body leans back 25 degrees so the screen faces up towards the user.
            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.01f, 0f);
            body.transform.localRotation = Quaternion.Euler(25f, 0f, 0f);
            Box(body.transform, "Shell", new Vector3(0f, 0.13f, 0f), new Vector3(0.16f, 0.26f, 0.035f), shell, false);

            // Screen + contactless symbol on the front face. Canvas: 500 x 820 units, 1 unit = 0.3 mm.
            var canvasObject = new GameObject("ScreenCanvas", typeof(RectTransform));
            canvasObject.transform.SetParent(body.transform, false);
            canvasObject.transform.localPosition = new Vector3(0f, 0.13f, -0.0178f);
            canvasObject.transform.localScale = Vector3.one * 0.0003f;
            canvasObject.GetComponent<RectTransform>().sizeDelta = new Vector2(500f, 820f);
            canvasObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;

            Image screen = NewImage("Screen", canvasObject.transform, rounded, new Color(0.02f, 0.03f, 0.05f));
            Place(screen.rectTransform, new Vector2(0f, 150f), new Vector2(460f, 500f));
            screen.pixelsPerUnitMultiplier = 3f; // smaller corner radius
            screen.raycastTarget = false;

            TMP_Text headline = NewText("Headline", canvasObject.transform, 40f, FontStyles.Bold, White, TextAlignmentOptions.Center, new Vector2(-10f, 365f), new Vector2(340f, 56f));
            headline.enableAutoSizing = true;
            headline.fontSizeMin = 20f;
            headline.fontSizeMax = 40f;
            headline.textWrappingMode = TextWrappingModes.Normal; // the robot status line can take two lines

            TMP_Text amount = NewText("Amount", canvasObject.transform, 80f, FontStyles.Bold, Gold, TextAlignmentOptions.Center, new Vector2(0f, 215f), new Vector2(420f, 150f));
            amount.textWrappingMode = TextWrappingModes.Normal;
            amount.enableAutoSizing = true;   // long totals shrink to fit on one line instead of wrapping
            amount.fontSizeMin = 28f;
            amount.fontSizeMax = 80f;

            // Receipt (shown on Success instead of the amount). Left-aligned, up to ~12 lines.
            TMP_Text receipt = NewText("Receipt", canvasObject.transform, 24f, FontStyles.Normal, White, TextAlignmentOptions.TopLeft, new Vector2(0f, 135f), new Vector2(430f, 400f));
            receipt.textWrappingMode = TextWrappingModes.NoWrap;
            receipt.richText = true;
            receipt.enableAutoSizing = true;
            receipt.fontSizeMin = 14f;
            receipt.fontSizeMax = 24f;
            receipt.gameObject.SetActive(false);

            var progressRoot = new GameObject("Progress", typeof(RectTransform));
            progressRoot.transform.SetParent(canvasObject.transform, false);
            Place(progressRoot.GetComponent<RectTransform>(), new Vector2(0f, 95f), new Vector2(340f, 22f));
            Image progressBackground = NewImage("Track", progressRoot.transform, rounded, new Color(0.2f, 0.22f, 0.28f));
            Place(progressBackground.rectTransform, Vector2.zero, new Vector2(340f, 22f));
            progressBackground.pixelsPerUnitMultiplier = 4f;
            progressBackground.raycastTarget = false;
            Image progressFill = NewImage("Fill", progressRoot.transform, rounded, Gold);
            Place(progressFill.rectTransform, Vector2.zero, new Vector2(340f, 22f));
            progressFill.type = Image.Type.Filled;
            progressFill.fillMethod = Image.FillMethod.Horizontal;
            progressFill.fillOrigin = 0;
            progressFill.fillAmount = 0f;
            progressFill.raycastTarget = false;
            progressRoot.SetActive(false);

            Image successIcon = NewImage("SuccessIcon", canvasObject.transform, check, new Color(0.35f, 0.85f, 0.5f));
            Place(successIcon.rectTransform, new Vector2(195f, 365f), new Vector2(40f, 40f));
            successIcon.raycastTarget = false;
            successIcon.enabled = false;

            Image contactlessIcon = NewImage("ContactlessIcon", canvasObject.transform, contactless, new Color(0.45f, 0.5f, 0.6f));
            Place(contactlessIcon.rectTransform, new Vector2(0f, -250f), new Vector2(170f, 170f));
            contactlessIcon.raycastTarget = false;

            TMP_Text tapLabel = NewText("TapLabel", canvasObject.transform, 32f, FontStyles.Normal, SoftWhite, TextAlignmentOptions.Center, new Vector2(0f, -360f), new Vector2(420f, 50f));
            tapLabel.text = "Tap card here";

            // Trigger volume in front of the contactless symbol.
            var zoneObject = new GameObject("TapZone");
            zoneObject.transform.SetParent(body.transform, false);
            zoneObject.transform.localPosition = new Vector3(0f, 0.055f, -0.085f);
            var zoneCollider = zoneObject.AddComponent<BoxCollider>();
            zoneCollider.isTrigger = true;
            zoneCollider.size = new Vector3(0.17f, 0.13f, 0.12f);
            var tapZone = zoneObject.AddComponent<CardTapZone>();

            // Effects: audio, gold sparkles shooting up from the top of the terminal, and the gift box on the counter.
            var audio = root.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;
            audio.volume = 0.6f;
            audio.minDistance = 0.3f;
            audio.maxDistance = 6f;

            var sparkleObject = new GameObject("SuccessSparkles");
            sparkleObject.transform.SetParent(root.transform, false);
            sparkleObject.transform.localPosition = new Vector3(0f, 0.25f, 0f);
            sparkleObject.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f); // the cone emits along +Z, so this points it up
            ParticleSystem sparkles = ConfigureSparkles(sparkleObject);

            var giftBoxObject = (GameObject)PrefabUtility.InstantiatePrefab(giftBoxPrefab, root.transform);
            giftBoxObject.transform.localPosition = new Vector3(-0.27f, 0f, -0.06f);
            giftBoxObject.SetActive(false);

            var terminal = root.AddComponent<PaymentTerminal>();
            var serialized = new SerializedObject(terminal);
            serialized.FindProperty("headlineLabel").objectReferenceValue = headline;
            serialized.FindProperty("amountLabel").objectReferenceValue = amount;
            serialized.FindProperty("receiptLabel").objectReferenceValue = receipt;
            serialized.FindProperty("progressRoot").objectReferenceValue = progressRoot;
            serialized.FindProperty("progressFill").objectReferenceValue = progressFill;
            serialized.FindProperty("successIcon").objectReferenceValue = successIcon;
            serialized.FindProperty("contactlessIcon").objectReferenceValue = contactlessIcon;
            serialized.FindProperty("tapZone").objectReferenceValue = tapZone;
            serialized.FindProperty("audioSource").objectReferenceValue = audio;
            serialized.FindProperty("beepClip").objectReferenceValue = beep;
            serialized.FindProperty("successClip").objectReferenceValue = success;
            serialized.FindProperty("sparkles").objectReferenceValue = sparkles;
            serialized.FindProperty("giftBox").objectReferenceValue = giftBoxObject.GetComponent<GiftBox>();
            serialized.ApplyModifiedPropertiesWithoutUndo();

            int checkoutLayer = ShowcaseCabinetBuilder.EnsureLayer(CheckoutLayerName);
            ShowcaseCabinetBuilder.SetLayerRecursive(root, checkoutLayer);
            ShowcaseCabinetBuilder.SetLayerRecursive(canvasObject, LayerMask.NameToLayer("UI"));

            PrefabUtility.SaveAsPrefabAsset(root, TerminalPath);
            UnityEngine.Object.DestroyImmediate(root);
        }

        /// <summary>One-shot burst of small gold glitter cubes that shoot up and fall back slowly. No transparency (cheap on Quest).</summary>
        private static ParticleSystem ConfigureSparkles(GameObject go)
        {
            Material glitter = GetMaterial("Sparkle_Gold", new Color(1f, 0.80f, 0.25f), 0.8f, 0.9f);
            glitter.EnableKeyword("_EMISSION");
            glitter.SetColor("_EmissionColor", new Color(1f, 0.65f, 0.15f) * 0.9f);
            glitter.enableInstancing = true;

            var system = go.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = system.main;
            main.duration = 1.2f;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.9f, 1.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.1f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.006f, 0.013f);
            main.gravityModifier = 0.35f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 80;
            main.startRotation3D = true;
            main.startRotationX = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startRotationY = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startRotationZ = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.82f, 0.30f), new Color(1f, 0.93f, 0.62f));

            var emission = system.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 60) });

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 22f;
            shape.radius = 0.04f;

            var sizeOverLifetime = system.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));

            var rotation = system.rotationOverLifetime;
            rotation.enabled = true;
            rotation.separateAxes = true;
            rotation.x = new ParticleSystem.MinMaxCurve(-8f, 8f);
            rotation.y = new ParticleSystem.MinMaxCurve(-8f, 8f);
            rotation.z = new ParticleSystem.MinMaxCurve(-8f, 8f);

            var particleRenderer = go.GetComponent<ParticleSystemRenderer>();
            particleRenderer.renderMode = ParticleSystemRenderMode.Mesh;
            particleRenderer.mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            particleRenderer.sharedMaterial = glitter;
            particleRenderer.shadowCastingMode = ShadowCastingMode.Off;
            particleRenderer.receiveShadows = false;
            particleRenderer.enableGPUInstancing = true;

            return system;
        }

        // ------------------------------------------------------------------------------------------------
        // CheckoutCounter
        // ------------------------------------------------------------------------------------------------
        private static void BuildCounter()
        {
            Material wood = GetMaterial("Counter_Wood", new Color(0.19f, 0.10f, 0.05f), 0.4f, 0f);
            Material top = GetMaterial("Counter_Top", new Color(0.07f, 0.06f, 0.06f), 0.65f, 0.1f);
            Material dock = GetMaterial("Counter_Dock", new Color(0.09f, 0.09f, 0.10f), 0.6f, 0f);

            var cardPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CreditCardPath);
            var terminalPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(TerminalPath);
            var displayPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CartDisplayPath);

            var root = new GameObject("CheckoutCounter");
            var structure = new GameObject("Structure");
            structure.transform.SetParent(root.transform, false);

            // Waist-high counter: the top surface is at 1.0 m.
            Box(structure.transform, "Body", new Vector3(0f, 0.48f, 0f), new Vector3(0.86f, 0.96f, 0.46f), wood, true);
            Box(structure.transform, "Top", new Vector3(0f, 0.98f, 0f), new Vector3(0.90f, 0.04f, 0.50f), top, true);

            // Card dock: a small stand that holds the card tilted towards the user.
            var dockObject = new GameObject("CardDock");
            dockObject.transform.SetParent(structure.transform, false);
            dockObject.transform.localPosition = new Vector3(-0.29f, 1.0f, -0.04f);
            Box(dockObject.transform, "Foot", new Vector3(0f, 0.004f, 0f), new Vector3(0.12f, 0.008f, 0.085f), dock, true);
            Quaternion tilt = Quaternion.Euler(-25f, 0f, 0f);
            Vector3 cradleCenter = new Vector3(0f, 0.026f, 0f);
            GameObject cradle = Box(dockObject.transform, "Cradle", cradleCenter, new Vector3(0.11f, 0.005f, 0.07f), dock, true);
            cradle.transform.localRotation = tilt;
            var slot = new GameObject("CardDockSlot").transform;
            slot.SetParent(dockObject.transform, false);
            slot.localPosition = cradleCenter + tilt * Vector3.up * 0.0045f;
            slot.localRotation = tilt;

            int checkoutLayer = ShowcaseCabinetBuilder.EnsureLayer(CheckoutLayerName);
            ShowcaseCabinetBuilder.SetLayerRecursive(structure, checkoutLayer);

            // Nested prefab instances.
            var card = (GameObject)PrefabUtility.InstantiatePrefab(cardPrefab, root.transform);
            card.transform.SetPositionAndRotation(slot.position, slot.rotation);
            var cardSerialized = new SerializedObject(card.GetComponent<CreditCard>());
            cardSerialized.FindProperty("homeSlot").objectReferenceValue = slot;
            cardSerialized.ApplyModifiedPropertiesWithoutUndo();

            var terminal = (GameObject)PrefabUtility.InstantiatePrefab(terminalPrefab, root.transform);
            terminal.transform.localPosition = new Vector3(0.29f, 1.0f, -0.03f);

            var display = (GameObject)PrefabUtility.InstantiatePrefab(displayPrefab, root.transform);
            display.transform.localPosition = new Vector3(0f, 1.20f, -0.02f);

            PrefabUtility.SaveAsPrefabAsset(root, CounterPath);
            UnityEngine.Object.DestroyImmediate(root);
        }

        // ------------------------------------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------------------------------------
        private static GameObject Box(Transform parent, string name, Vector3 localPosition, Vector3 size, Material material, bool keepCollider)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = size;
            var meshRenderer = go.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            if (!keepCollider)
            {
                UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
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
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
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

        private static Image NewImage(string name, Transform parent, Sprite sprite, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.type = sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
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

        /// <summary>Anchors the rect to the bottom centre of its parent (y measured upwards from the bottom edge).</summary>
        private static void PlaceBottom(RectTransform rect, Vector2 anchoredPosition, Vector2 sizeDelta, float pivotY)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, pivotY);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;
        }

        /// <summary>Creates a white sprite whose alpha comes from <paramref name="alpha"/> (u, v in 0..1, v up).</summary>
        private static Sprite GetOrCreateSprite(string path, int size, Func<float, float, float> alpha)
        {
            if (AssetDatabase.LoadAssetAtPath<Sprite>(path) == null)
            {
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                var pixels = new Color[size * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha((x + 0.5f) / size, (y + 0.5f) / size)));
                    }
                }

                texture.SetPixels(pixels);
                System.IO.File.WriteAllBytes(path, texture.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(texture);
                AssetDatabase.ImportAsset(path);

                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Sprite;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
            }

            var existing = (TextureImporter)AssetImporter.GetAtPath(path);
            if (existing.spriteImportMode != SpriteImportMode.Single)
            {
                existing.spriteImportMode = SpriteImportMode.Single;
                existing.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        private static float CrossAlpha(float u, float v)
        {
            Vector2 p = new Vector2(u, v);
            float distance = Mathf.Min(
                DistanceToSegment(p, new Vector2(0.28f, 0.28f), new Vector2(0.72f, 0.72f)),
                DistanceToSegment(p, new Vector2(0.28f, 0.72f), new Vector2(0.72f, 0.28f))) - 0.075f;
            return 0.5f - distance * 128f;
        }

        /// <summary>Contactless ("wave") symbol: a dot and four arcs opening to the right.</summary>
        private static float ContactlessAlpha(float u, float v)
        {
            Vector2 center = new Vector2(0.14f, 0.5f);
            Vector2 offset = new Vector2(u, v) - center;
            float radius = offset.magnitude;
            float angle = Mathf.Abs(Mathf.Atan2(offset.y, offset.x)) * Mathf.Rad2Deg;

            float best = Mathf.Abs(radius - 0.05f) - 0.055f; // dot
            if (angle <= 40f)
            {
                foreach (float arcRadius in new[] { 0.22f, 0.38f, 0.54f, 0.70f })
                {
                    best = Mathf.Min(best, Mathf.Abs(radius - arcRadius) - 0.03f);
                }
            }

            return 0.5f - best * 128f;
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }
    }
}
