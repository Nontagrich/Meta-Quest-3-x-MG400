using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace GemStore.Showcase.Editor
{
    /// <summary>
    /// Dresses the grey-box store as a luxury jewelry boutique (cream walls, marble floor, dark walnut, brass, navy velvet).
    /// The layout is not touched: furniture stays where StoreLayoutBuilder put it. Textures are generated procedurally
    /// (no downloads). Everything this builder adds is named "Lux_*" and is rebuilt on every run, so it is safe to re-run.
    /// Run "GemStore/Arrange Store" first if the Store hierarchy does not exist yet.
    /// </summary>
    public static class StoreDecorBuilder
    {
        private const string MaterialsFolder = "Assets/_Project/Materials/Store";
        private const string TexturesFolder = "Assets/_Project/Materials/Store/Textures";
        private const string ModelsFolder = "Assets/_Project/Models/Decor";
        private const string LuxPrefix = "Lux_";

        private static readonly Color Cream = new Color(0.93f, 0.88f, 0.78f);
        private static readonly Color BrassColor = new Color(0.74f, 0.54f, 0.24f);
        private static readonly Color NavyColor = new Color(0.04f, 0.07f, 0.19f);
        private static readonly Color GoldText = new Color(1f, 0.82f, 0.38f);

        private static Material floorMat, wallMat, ceilingMat, trimMat, walnutMat, brassMat, velvetMat, rugMat, leafMat, leaf2Mat, potMat, soilMat, lightMat, screenMat, signMat;
        private static Material gemBlue, gemWhite, gemPink;
        private static Mesh torus;

        [MenuItem("GemStore/Decorate Store (Luxury)")]
        public static void Decorate()
        {
            Transform store = GameObject.Find("Store")?.transform;
            if (store == null)
            {
                Debug.LogError("GemStore: no 'Store' in the scene. Run GemStore/Arrange Store first.");
                return;
            }

            EnsureFolders();
            BuildAssets();

            Transform shell = store.Find("StoreShell");
            Transform showcase = store.Find("Showcase");
            Transform checkout = store.Find("Checkout");
            Transform staff = store.Find("StaffCorner");
            Transform decor = store.Find("Decor");

            RemoveOldLux(store);

            RestyleShell(shell);
            RestyleShowcase(showcase);
            RestyleStaff(staff);
            Transform lux = NewGroup(decor, LuxPrefix + "Decor");
            RestyleDecor(decor, lux);
            BuildTrim(lux);
            BuildCeilingLights(lux);
            BuildRug(lux);
            BuildPlants(lux);
            BuildShelfItems(decor, lux);
            BuildLogo(decor, lux);
            BuildSign(lux);
            BuildCounterBrass(checkout);
            BuildCabinetBrass(showcase);
            RestyleSharedMaterials();
            SetLighting();
            MarkStatic(store);

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();
            Debug.Log("GemStore: luxury decor built.");
        }

        // ------------------------------------------------------------------------------------------------------
        // Assets
        // ------------------------------------------------------------------------------------------------------

        private static void EnsureFolders()
        {
            Directory.CreateDirectory(Path.GetFullPath(TexturesFolder));
            Directory.CreateDirectory(Path.GetFullPath(ModelsFolder));
            AssetDatabase.Refresh();
        }

        private static void BuildAssets()
        {
            Texture2D marble = EnsureTexture("Store_Marble", 1024, MarblePixels);
            Texture2D walnut = EnsureTexture("Store_Walnut", 512, WalnutPixels);
            Texture2D rug = EnsureTexture("Store_Rug", 512, RugPixels);

            floorMat = Mat("Store_Floor", Color.white, 0.62f, 0f, marble, new Vector2(2.5f, 2.5f));
            wallMat = Mat("Store_Wall", Cream, 0.08f);
            ceilingMat = Mat("Store_Ceiling", new Color(0.96f, 0.94f, 0.89f), 0.05f);
            trimMat = Mat("Store_Trim", new Color(0.97f, 0.95f, 0.90f), 0.2f);
            walnutMat = Mat("Store_Walnut", Color.white, 0.38f, 0f, walnut, Vector2.one);
            brassMat = Mat("Store_Brass", BrassColor, 0.65f, 0.75f);
            velvetMat = Mat("Store_Velvet", NavyColor, 0.04f);
            rugMat = Mat("Store_Rug", Color.white, 0.05f, 0f, rug, Vector2.one);
            leafMat = Mat("Store_Leaf", new Color(0.14f, 0.36f, 0.17f), 0.25f);
            leaf2Mat = Mat("Store_Leaf2", new Color(0.22f, 0.46f, 0.20f), 0.25f);
            potMat = Mat("Store_Pot", new Color(0.92f, 0.89f, 0.82f), 0.5f);
            soilMat = Mat("Store_Soil", new Color(0.12f, 0.08f, 0.05f), 0.05f);
            lightMat = Mat("Store_Light", new Color(1f, 0.93f, 0.78f), 0.2f, 0f, null, Vector2.one, new Color(1.0f, 0.82f, 0.55f) * 1.6f);
            screenMat = Mat("Store_Screen", new Color(0.02f, 0.03f, 0.05f), 0.85f);
            signMat = Mat("Store_SignBack", new Color(0.95f, 0.92f, 0.84f), 0.1f);

            gemBlue = AssetDatabase.LoadAssetAtPath<UnityEngine.Material>("Assets/_Project/Materials/Gems/Gem_Blue.mat");
            gemWhite = AssetDatabase.LoadAssetAtPath<UnityEngine.Material>("Assets/_Project/Materials/Gems/Gem_White.mat");
            gemPink = AssetDatabase.LoadAssetAtPath<UnityEngine.Material>("Assets/_Project/Materials/Gems/Gem_Pink.mat");
            torus = EnsureTorus();
        }

        private static Material Mat(string name, Color color, float smoothness, float metallic = 0f, Texture2D texture = null, Vector2? tiling = null, Color? emission = null)
        {
            string path = $"{MaterialsFolder}/{name}.mat";
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            var material = AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(path);
            if (material == null)
            {
                material = new UnityEngine.Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            Style(material, color, smoothness, metallic, texture, tiling, emission);
            return material;
        }

        private static void Style(UnityEngine.Material material, Color color, float smoothness, float metallic = 0f, Texture2D texture = null, Vector2? tiling = null, Color? emission = null)
        {
            material.SetFloat("_Surface", 0f);
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Metallic", metallic);
            material.SetTexture("_BaseMap", texture);
            material.SetTextureScale("_BaseMap", tiling ?? Vector2.one);
            if (emission.HasValue)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emission.Value);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive; // emissive parts also light their surroundings in the lightmap bake
            }

            EditorUtility.SetDirty(material);
        }

        /// <summary>The cabinet and counter already have materials (from the earlier builders); give them the luxury look.</summary>
        private static void RestyleSharedMaterials()
        {
            Texture2D walnut = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexturesFolder}/Store_Walnut.png");
            Texture2D marble = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexturesFolder}/Store_Marble.png");
            StyleExisting("Assets/_Project/Materials/Cabinet/Cabinet_Wood.mat", Color.white, 0.38f, 0f, walnut, Vector2.one);
            StyleExisting("Assets/_Project/Materials/Cabinet/Cabinet_Velvet.mat", new Color(0.02f, 0.04f, 0.13f), 0.04f, 0f, null, Vector2.one);
            StyleExisting("Assets/_Project/Materials/Cabinet/Cabinet_Pedestal.mat", BrassColor, 0.65f, 0.75f, null, Vector2.one);
            StyleExisting("Assets/_Project/Materials/Checkout/Counter_Wood.mat", Color.white, 0.38f, 0f, walnut, Vector2.one);
            StyleExisting("Assets/_Project/Materials/Checkout/Counter_Top.mat", new Color(0.95f, 0.94f, 0.92f), 0.7f, 0f, marble, Vector2.one);
        }

        private static void StyleExisting(string path, Color color, float smoothness, float metallic, Texture2D texture, Vector2 tiling)
        {
            var material = AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(path);
            if (material == null)
            {
                Debug.LogWarning("GemStore: material not found: " + path);
                return;
            }

            Style(material, color, smoothness, metallic, texture, tiling);
        }

        // ------------------------------------------------------------------------------------------------------
        // Restyle existing grey-box parts (materials only: positions stay)
        // ------------------------------------------------------------------------------------------------------

        private static void RestyleShell(Transform shell)
        {
            SetMaterial(shell, "Floor", floorMat);
            SetMaterial(shell, "Wall_Front", wallMat);
            SetMaterial(shell, "Wall_Left", wallMat);
            SetMaterial(shell, "Wall_Right", wallMat);
            SetMaterial(shell, "Ceiling", ceilingMat);
            SetMaterial(shell, "DoorFrame_Left", walnutMat);
            SetMaterial(shell, "DoorFrame_Right", walnutMat);
            SetMaterial(shell, "DoorFrame_Top", walnutMat);
        }

        private static void RestyleShowcase(Transform showcase)
        {
            SetMaterial(showcase, "CabinetBase", walnutMat);
            SetMaterial(showcase, "CabinetBase_Top", brassMat);
            SetMaterial(showcase, "CabinetPost_Left", walnutMat);
            SetMaterial(showcase, "CabinetPost_Right", walnutMat);
            SetMaterial(showcase, "CabinetRoof", walnutMat);
        }

        private static void RestyleStaff(Transform staff)
        {
            SetMaterial(staff, "StaffScreen_Base", brassMat);
            SetMaterial(staff, "StaffScreen_Post", brassMat);
            SetMaterial(staff, "StaffScreen_Panel", screenMat);
            SetMaterial(staff, "CallButton_Pedestal", walnutMat);
            SetMaterial(staff, "CallButton_PedestalTop", brassMat);
            SetMaterial(staff, "CallButton_Placeholder", brassMat);
        }

        private static void RestyleDecor(Transform decor, Transform lux)
        {
            SetMaterial(decor, "Logo_Board", velvetMat);
            SetMaterial(decor, "OutsideGround", Existing("Store_DarkGrey"));
            foreach (Transform child in decor)
            {
                if (child.name.StartsWith("WallShelf_"))
                {
                    child.GetComponent<MeshRenderer>().sharedMaterial = walnutMat;
                }
            }
        }

        private static UnityEngine.Material Existing(string name)
        {
            return AssetDatabase.LoadAssetAtPath<UnityEngine.Material>($"{MaterialsFolder}/{name}.mat");
        }

        private static void SetMaterial(Transform root, string name, UnityEngine.Material material)
        {
            Transform t = FindDeep(root, name);
            if (t == null || material == null)
            {
                return;
            }

            var renderer = t.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
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

        // ------------------------------------------------------------------------------------------------------
        // New decoration
        // ------------------------------------------------------------------------------------------------------

        private static void RemoveOldLux(Transform store)
        {
            var doomed = new System.Collections.Generic.List<GameObject>();
            foreach (Transform t in store.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith(LuxPrefix))
                {
                    doomed.Add(t.gameObject);
                }
            }

            foreach (GameObject go in doomed)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }
        }

        private static void BuildTrim(Transform lux)
        {
            Transform trim = NewGroup(lux, LuxPrefix + "Trim");
            // Baseboard (skirting) and crown moulding on the three walls; a thin brass picture rail above head height.
            foreach (var spec in new[]
            {
                ("Front", new Vector3(0f, 0f, 2.9875f), new Vector3(5.0f, 1f, 0.025f)),
                ("Left", new Vector3(-2.4875f, 0f, 0.5f), new Vector3(0.025f, 1f, 5.0f)),
                ("Right", new Vector3(2.4875f, 0f, 0.5f), new Vector3(0.025f, 1f, 5.0f)),
            })
            {
                bool alongX = spec.Item3.x > spec.Item3.z;
                Vector3 baseSize = new Vector3(spec.Item3.x, 0.14f, spec.Item3.z);
                Cube(trim, "Skirting_" + spec.Item1, new Vector3(spec.Item2.x, 0.07f, spec.Item2.z), baseSize, trimMat);

                float crownDepth = 0.045f;
                Vector3 crownSize = alongX ? new Vector3(spec.Item3.x, 0.10f, crownDepth) : new Vector3(crownDepth, 0.10f, spec.Item3.z);
                Vector3 crownPos = spec.Item2;
                if (alongX)
                {
                    crownPos.z = 3.0f - crownDepth * 0.5f;
                }
                else
                {
                    crownPos.x = Mathf.Sign(spec.Item2.x) * (2.5f - crownDepth * 0.5f);
                }

                crownPos.y = 2.95f;
                Cube(trim, "Crown_" + spec.Item1, crownPos, crownSize, trimMat);

                Vector3 railSize = alongX ? new Vector3(spec.Item3.x, 0.014f, 0.012f) : new Vector3(0.012f, 0.014f, spec.Item3.z);
                Vector3 railPos = spec.Item2;
                if (alongX)
                {
                    railPos.z = 3.0f - 0.006f;
                }
                else
                {
                    railPos.x = Mathf.Sign(spec.Item2.x) * (2.5f - 0.006f);
                }

                railPos.y = 2.82f;
                Cube(trim, "BrassRail_" + spec.Item1, railPos, railSize, brassMat);
            }
        }

        private static void BuildCeilingLights(Transform lux)
        {
            // Recessed-looking downlights: a brass ring and a glowing disc (emissive, no real light, so it is free on Quest).
            Transform lights = NewGroup(lux, LuxPrefix + "Downlights");
            Vector2[] points = { new Vector2(-1.3f, 0.2f), new Vector2(1.3f, 0.2f), new Vector2(-1.3f, 1.9f), new Vector2(1.3f, 1.9f), new Vector2(0f, 1.0f) };
            for (int i = 0; i < points.Length; i++)
            {
                Cylinder(lights, "DownlightRing_" + i, new Vector3(points[i].x, 2.995f, points[i].y), 0.3f, 0.008f, brassMat);
                Cylinder(lights, "DownlightGlow_" + i, new Vector3(points[i].x, 2.99f, points[i].y), 0.24f, 0.008f, lightMat);
            }
        }

        private static void BuildRug(Transform lux)
        {
            // Round rug under the user's feet: it marks where to stand. The gold footprints point towards the shop (+Z).
            GameObject rug = Cylinder(lux, "Rug_StandHere", new Vector3(0f, 0.006f, 0f), 1.6f, 0.012f, rugMat);
            rug.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // the texture's +V points to -Z, so turn it to put the toes towards the shop
        }

        private static void BuildPlants(Transform lux)
        {
            Plant(lux, "Plant_Left", new Vector3(-2.25f, 0f, 2.7f));
            Plant(lux, "Plant_Right", new Vector3(2.25f, 0f, 2.7f));
        }

        private static void Plant(Transform parent, string name, Vector3 position)
        {
            Transform plant = NewGroup(parent, LuxPrefix + name);
            plant.localPosition = position;
            Cylinder(plant, "Pot", new Vector3(0f, 0.16f, 0f), 0.32f, 0.32f, potMat);
            Cylinder(plant, "PotRim", new Vector3(0f, 0.325f, 0f), 0.38f, 0.04f, potMat);
            Cylinder(plant, "PotBand", new Vector3(0f, 0.12f, 0f), 0.325f, 0.03f, brassMat);
            Cylinder(plant, "Soil", new Vector3(0f, 0.342f, 0f), 0.33f, 0.012f, soilMat);
            Sphere(plant, "Leaves_Main", new Vector3(0f, 0.68f, 0f), 0.44f, leafMat);
            Sphere(plant, "Leaves_A", new Vector3(0.13f, 0.52f, 0.07f), 0.30f, leaf2Mat);
            Sphere(plant, "Leaves_B", new Vector3(-0.12f, 0.55f, -0.05f), 0.30f, leaf2Mat);
            Sphere(plant, "Leaves_C", new Vector3(0.03f, 0.86f, -0.04f), 0.26f, leaf2Mat);
            Sphere(plant, "Leaves_D", new Vector3(-0.08f, 0.50f, 0.12f), 0.22f, leafMat);
        }

        private static void BuildShelfItems(Transform decor, Transform lux)
        {
            // Remove the grey stand-in boxes and put real display pieces on the shelves.
            var doomed = new System.Collections.Generic.List<GameObject>();
            foreach (Transform child in decor)
            {
                if (child.name.StartsWith("ShelfItem_"))
                {
                    doomed.Add(child.gameObject);
                }
            }

            foreach (GameObject go in doomed)
            {
                Object.DestroyImmediate(go);
            }

            Transform items = NewGroup(lux, LuxPrefix + "ShelfItems");
            float wallFace = 3.0f;
            float z = wallFace - 0.125f;
            foreach (float side in new[] { -1f, 1f })
            {
                float xc = side * 1.75f;
                string s = side < 0 ? "L" : "R";
                // lower shelf: ring stand + velvet ring box
                RingStand(items, "RingStand_" + s + "_Low", new Vector3(xc - side * 0.22f, 1.15f + 0.02f, z), gemWhite);
                JewelryBox(items, "Box_" + s + "_Low", new Vector3(xc + side * 0.25f, 1.15f + 0.02f, z));
                // middle shelf: necklace bust + brass vase
                Bust(items, "Bust_" + s, new Vector3(xc - side * 0.12f, 1.65f + 0.02f, z), gemBlue);
                Vase(items, "Vase_" + s + "_Mid", new Vector3(xc + side * 0.3f, 1.65f + 0.02f, z));
                // top shelf: ring stand + box
                RingStand(items, "RingStand_" + s + "_High", new Vector3(xc + side * 0.2f, 2.15f + 0.02f, z), gemPink);
                JewelryBox(items, "Box_" + s + "_High", new Vector3(xc - side * 0.22f, 2.15f + 0.02f, z));
            }
        }

        /// <summary>Neck form in navy velvet with a brass stand, a choker and a collar necklace with a pendant. Front faces -Z (the shop).</summary>
        private static void Bust(Transform parent, string name, Vector3 position, UnityEngine.Material pendant)
        {
            Transform bust = NewGroup(parent, name);
            bust.localPosition = position;
            bust.localRotation = Quaternion.Euler(0f, 180f, 0f);
            Cylinder(bust, "Plinth", new Vector3(0f, 0.0075f, 0f), 0.16f, 0.015f, brassMat);
            Cylinder(bust, "Stem", new Vector3(0f, 0.045f, 0f), 0.02f, 0.06f, brassMat);
            Sphere(bust, "Torso", new Vector3(0f, 0.135f, 0f), 1f, velvetMat, new Vector3(0.20f, 0.14f, 0.10f));
            Cylinder(bust, "Neck", new Vector3(0f, 0.225f, 0f), 0.07f, 0.09f, velvetMat);
            Sphere(bust, "NeckTop", new Vector3(0f, 0.27f, 0f), 1f, velvetMat, new Vector3(0.07f, 0.03f, 0.07f));
            TorusPiece(bust, "Choker", new Vector3(0f, 0.215f, 0f), 0.039f, 0.0035f, new Vector3(1f, 1f, 1f), brassMat);
            TorusPiece(bust, "Collar", new Vector3(0f, 0.168f, 0f), 0.070f, 0.0035f, new Vector3(1.32f, 1f, 0.69f), brassMat);
            Sphere(bust, "Pendant", new Vector3(0f, 0.152f, 0.0495f), 0.022f, pendant);
        }

        private static void RingStand(Transform parent, string name, Vector3 position, UnityEngine.Material gem)
        {
            Transform stand = NewGroup(parent, name);
            stand.localPosition = position;
            Cylinder(stand, "Column", new Vector3(0f, 0.06f, 0f), 0.10f, 0.12f, walnutMat);
            Cylinder(stand, "Cap", new Vector3(0f, 0.125f, 0f), 0.13f, 0.01f, brassMat);
            Cylinder(stand, "Cushion", new Vector3(0f, 0.14f, 0f), 0.09f, 0.02f, velvetMat);
            GameObject ring = TorusPiece(stand, "Ring", new Vector3(0f, 0.168f, 0f), 0.016f, 0.0035f, Vector3.one, brassMat);
            ring.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Sphere(stand, "Stone", new Vector3(0f, 0.187f, 0f), 0.02f, gem);
        }

        private static void JewelryBox(Transform parent, string name, Vector3 position)
        {
            Transform box = NewGroup(parent, name);
            box.localPosition = position;
            Cube(box, "Base", new Vector3(0f, 0.03f, 0f), new Vector3(0.12f, 0.06f, 0.12f), velvetMat);
            Cube(box, "Band", new Vector3(0f, 0.03f, 0f), new Vector3(0.124f, 0.008f, 0.124f), brassMat);
            Cube(box, "Clasp", new Vector3(0f, 0.045f, -0.061f), new Vector3(0.02f, 0.02f, 0.006f), brassMat);
        }

        private static void Vase(Transform parent, string name, Vector3 position)
        {
            Transform vase = NewGroup(parent, name);
            vase.localPosition = position;
            Cylinder(vase, "Foot", new Vector3(0f, 0.01f, 0f), 0.07f, 0.02f, brassMat);
            Cylinder(vase, "Body", new Vector3(0f, 0.09f, 0f), 0.09f, 0.14f, brassMat);
            Cylinder(vase, "Neck", new Vector3(0f, 0.19f, 0f), 0.045f, 0.06f, brassMat);
            Cylinder(vase, "Lip", new Vector3(0f, 0.222f, 0f), 0.065f, 0.012f, brassMat);
        }

        private static void BuildLogo(Transform decor, Transform lux)
        {
            // The old UI canvas logo is replaced by a 3D TextMeshPro with a brass-framed navy velvet board.
            Transform oldCanvas = decor.Find("Logo_Canvas");
            if (oldCanvas != null)
            {
                Object.DestroyImmediate(oldCanvas.gameObject);
            }

            Transform board = decor.Find("Logo_Board");
            const float wall = 3.0f;
            const float centerY = 2.55f;
            const float boardW = 2.4f, boardH = 0.5f;
            if (board != null)
            {
                board.localPosition = new Vector3(0f, centerY, wall - 0.03f);
                board.localScale = new Vector3(boardW, boardH, 0.06f);
            }

            Transform logo = NewGroup(lux, LuxPrefix + "Logo");
            logo.localPosition = new Vector3(0f, centerY, wall - 0.06f);
            float t = 0.02f;
            Cube(logo, "FrameTop", new Vector3(0f, boardH * 0.5f + t * 0.5f, 0.0f), new Vector3(boardW + 2f * t, t, 0.07f), brassMat);
            Cube(logo, "FrameBottom", new Vector3(0f, -boardH * 0.5f - t * 0.5f, 0.0f), new Vector3(boardW + 2f * t, t, 0.07f), brassMat);
            Cube(logo, "FrameLeft", new Vector3(-boardW * 0.5f - t * 0.5f, 0f, 0.0f), new Vector3(t, boardH, 0.07f), brassMat);
            Cube(logo, "FrameRight", new Vector3(boardW * 0.5f + t * 0.5f, 0f, 0.0f), new Vector3(t, boardH, 0.07f), brassMat);

            // Text sits in front of the board (towards -Z) with a darker copy slightly behind it for a embossed look.
            Text3D(logo, "Text_Back", "Bangmod Gems", new Vector3(0.007f, -0.007f, -0.012f), new Vector2(2.2f, 0.4f), new Color(0.42f, 0.27f, 0.07f), 6f, FontStyles.Bold);
            Text3D(logo, "Text_Front", "Bangmod Gems", new Vector3(0f, 0f, -0.022f), new Vector2(2.2f, 0.4f), GoldText, 6f, FontStyles.Bold);
        }

        private static void BuildSign(Transform lux)
        {
            // "Handcrafted by KMUTT CPE" plaque in a brass frame on the left wall.
            Transform sign = NewGroup(lux, LuxPrefix + "Sign_KMUTT");
            sign.localPosition = new Vector3(-2.5f, 1.65f, 1.9f);
            sign.localRotation = Quaternion.Euler(0f, -90f, 0f); // forward = -X, so the text reads from inside the room
            const float w = 0.9f, h = 0.45f, t = 0.025f;
            Cube(sign, "Board", new Vector3(0f, 0f, -0.015f), new Vector3(w, h, 0.03f), signMat);
            Cube(sign, "FrameTop", new Vector3(0f, h * 0.5f + t * 0.5f, -0.0225f), new Vector3(w + 2f * t, t, 0.045f), brassMat);
            Cube(sign, "FrameBottom", new Vector3(0f, -h * 0.5f - t * 0.5f, -0.0225f), new Vector3(w + 2f * t, t, 0.045f), brassMat);
            Cube(sign, "FrameLeft", new Vector3(-w * 0.5f - t * 0.5f, 0f, -0.0225f), new Vector3(t, h, 0.045f), brassMat);
            Cube(sign, "FrameRight", new Vector3(w * 0.5f + t * 0.5f, 0f, -0.0225f), new Vector3(t, h, 0.045f), brassMat);
            Text3D(sign, "Text_Line1", "Handcrafted by", new Vector3(0f, 0.07f, -0.035f), new Vector2(0.8f, 0.14f), new Color(0.35f, 0.24f, 0.10f), 2f, FontStyles.Italic);
            Text3D(sign, "Text_Line2", "KMUTT CPE", new Vector3(0f, -0.08f, -0.035f), new Vector2(0.8f, 0.2f), new Color(0.12f, 0.17f, 0.38f), 3f, FontStyles.Bold);
        }

        private static void BuildCounterBrass(Transform checkout)
        {
            Transform counter = FindDeep(checkout, "CheckoutCounter");
            Transform top = counter != null ? FindDeep(counter, "Top") : null;
            if (top == null)
            {
                Debug.LogWarning("GemStore: CheckoutCounter/Top not found, brass trim skipped.");
                return;
            }

            Transform trim = NewGroup(checkout, LuxPrefix + "CounterBrass");
            Vector3 s = top.localScale; // the counter top is a unit cube scaled to its size
            Quaternion rot = top.rotation;
            float topSurface = 0.5f; // unit-cube local y of the top face
            float lip = 0.007f;
            // four bars on the top face edge
            AddBar(trim, top, new Vector3(0f, topSurface, 0.5f), rot, new Vector3(s.x + 0.02f, 0.014f, 0.016f), new Vector3(0f, lip, -0.008f));
            AddBar(trim, top, new Vector3(0f, topSurface, -0.5f), rot, new Vector3(s.x + 0.02f, 0.014f, 0.016f), new Vector3(0f, lip, 0.008f));
            AddBar(trim, top, new Vector3(0.5f, topSurface, 0f), rot, new Vector3(0.016f, 0.014f, s.z - 0.016f), new Vector3(-0.008f, lip, 0f));
            AddBar(trim, top, new Vector3(-0.5f, topSurface, 0f), rot, new Vector3(0.016f, 0.014f, s.z - 0.016f), new Vector3(0.008f, lip, 0f));
            // brass kick plate along the foot of the counter front
            Transform body = FindDeep(counter, "Body");
            if (body != null)
            {
                Vector3 bs = body.localScale;
                var kick = Cube(trim, "KickPlate", Vector3.zero, new Vector3(bs.x + 0.01f, 0.04f, bs.z + 0.01f), brassMat);
                kick.transform.SetPositionAndRotation(body.position + body.rotation * new Vector3(0f, -bs.y * 0.5f + 0.02f, 0f), body.rotation);
                kick.transform.localScale = new Vector3(bs.x + 0.012f, 0.04f, bs.z + 0.012f);
            }
        }

        private static void AddBar(Transform parent, Transform reference, Vector3 unitLocal, Quaternion rot, Vector3 size, Vector3 worldOffsetInRefAxes)
        {
            GameObject bar = Cube(parent, "TopEdge", Vector3.zero, size, brassMat);
            bar.transform.SetPositionAndRotation(reference.TransformPoint(unitLocal) + rot * worldOffsetInRefAxes, rot);
            bar.transform.localScale = size;
        }

        private static void BuildCabinetBrass(Transform showcase)
        {
            Transform trim = NewGroup(showcase, LuxPrefix + "CabinetBrass");
            const float z = 0.70f;
            Cube(trim, "Plinth", new Vector3(0f, 0.02f, z), new Vector3(0.545f, 0.04f, 0.425f), brassMat);
            Cube(trim, "RoofTrim", new Vector3(0f, 1.665f, z + 0.01f), new Vector3(0.58f, 0.012f, 0.32f), brassMat);
        }

        // ------------------------------------------------------------------------------------------------------
        // Lighting, static flags
        // ------------------------------------------------------------------------------------------------------

        private static void SetLighting()
        {
            // Warm interior ambient; no extra realtime lights are added (Quest).
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.78f, 0.72f, 0.63f);
            RenderSettings.ambientEquatorColor = new Color(0.64f, 0.58f, 0.50f);
            RenderSettings.ambientGroundColor = new Color(0.34f, 0.30f, 0.25f);
            foreach (Light light in Object.FindObjectsByType<Light>())
            {
                if (light.type == LightType.Directional)
                {
                    light.intensity = 0.8f;
                    light.shadows = LightShadows.None; // realtime shadows cost ~20 draw calls and ~14k triangles on Quest; the room has no need for them
                }
            }
        }

        private static void MarkStatic(Transform store)
        {
            const StaticEditorFlags flags = StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic | StaticEditorFlags.ContributeGI;
            foreach (string group in new[] { "StoreShell", "Decor", "StaffCorner", "Showcase", "Checkout" })
            {
                Transform g = store.Find(group);
                if (g == null)
                {
                    continue;
                }

                foreach (Transform t in g.GetComponentsInChildren<Transform>(true))
                {
                    // The cabinet and the counter hold gems, the card, the terminal and effects that move: leave them alone.
                    if (IsDynamicFurniture(t))
                    {
                        continue;
                    }

                    // 3D text is unlit: no lightmap for it
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject, t.GetComponent<TMP_Text>() != null ? flags & ~StaticEditorFlags.ContributeGI : flags);
                }
            }

            MarkFurnitureParts(store, flags);
        }

        /// <summary>
        /// The cabinet frame and the counter body never move, so they can be static-batched too (fewer draw calls).
        /// Gems, card, terminal screen, gift box and particles stay dynamic.
        /// </summary>
        private static void MarkFurnitureParts(Transform store, StaticEditorFlags flags)
        {
            foreach (string path in new[]
            {
                "Showcase/ShowcaseCabinet/Frame", "Showcase/ShowcaseCabinet/BackPanel", "Showcase/ShowcaseCabinet/Pedestals", "Showcase/ShowcaseCabinet/Sign/SignBoard",
                "Checkout/CheckoutCounter/Structure", "Checkout/CheckoutCounter/PaymentTerminal/Base", "Checkout/CheckoutCounter/PaymentTerminal/Body/Shell",
            })
            {
                Transform part = store.Find(path);
                if (part == null)
                {
                    continue;
                }

                foreach (Transform t in part.GetComponentsInChildren<Transform>(true))
                {
                    if (t.GetComponent<MeshRenderer>() != null || t == part)
                    {
                        GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);
                    }
                }
            }
        }

        private static bool IsDynamicFurniture(Transform t)
        {
            for (Transform p = t; p != null; p = p.parent)
            {
                if (p.name == "ShowcaseCabinet" || p.name == "CheckoutCounter")
                {
                    return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------------------------------------------
        // Primitive helpers (no colliders, no shadows: decoration is free of physics and shadow cost)
        // ------------------------------------------------------------------------------------------------------

        private static Transform NewGroup(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        private static GameObject Prepare(GameObject go, Transform parent, string name, Vector3 localPosition, Vector3 scale, UnityEngine.Material material)
        {
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = scale;
            Collider collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                Object.DestroyImmediate(collider);
            }

            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.On; // casts into the lightmap bake; no realtime light has shadows, so it costs nothing at runtime
            renderer.receiveShadows = false;
            return go;
        }

        private static GameObject Cube(Transform parent, string name, Vector3 localPosition, Vector3 size, UnityEngine.Material material)
        {
            return Prepare(GameObject.CreatePrimitive(PrimitiveType.Cube), parent, name, localPosition, size, material);
        }

        /// <summary>Cylinder given by diameter and height (the Unity cylinder primitive is 1 wide and 2 tall).</summary>
        private static GameObject Cylinder(Transform parent, string name, Vector3 localCenter, float diameter, float height, UnityEngine.Material material)
        {
            return Prepare(GameObject.CreatePrimitive(PrimitiveType.Cylinder), parent, name, localCenter, new Vector3(diameter, height * 0.5f, diameter), material);
        }

        private static GameObject Sphere(Transform parent, string name, Vector3 localCenter, float diameter, UnityEngine.Material material, Vector3? scale = null)
        {
            return Prepare(GameObject.CreatePrimitive(PrimitiveType.Sphere), parent, name, localCenter, scale ?? Vector3.one * diameter, material);
        }

        private static GameObject TorusPiece(Transform parent, string name, Vector3 localCenter, float majorRadius, float minorRadius, Vector3 stretch, UnityEngine.Material material)
        {
            // The torus mesh has major radius 1 and minor radius 0.05: scale it so that the tube is minorRadius thick.
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.GetComponent<MeshFilter>().sharedMesh = torus;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localCenter;
            float scale = majorRadius;
            go.transform.localScale = new Vector3(scale * stretch.x, Mathf.Max(minorRadius / 0.05f, 0.0001f) * stretch.y, scale * stretch.z);
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.On; // casts into the lightmap bake; no realtime light has shadows, so it costs nothing at runtime
            renderer.receiveShadows = false;
            return go;
        }

        private static void Text3D(Transform parent, string name, string text, Vector3 localPosition, Vector2 size, Color color, float maxSize, FontStyles style)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.rectTransform.sizeDelta = size;
            tmp.text = text;
            tmp.color = color;
            tmp.fontStyle = style;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 0.5f;
            tmp.fontSizeMax = maxSize;
            tmp.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            tmp.GetComponent<MeshRenderer>().receiveShadows = false;
        }

        // ------------------------------------------------------------------------------------------------------
        // Procedural meshes and textures
        // ------------------------------------------------------------------------------------------------------

        private static Mesh EnsureTorus()
        {
            string path = $"{ModelsFolder}/Torus_LowPoly.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                if (existing.uv2 == null || existing.uv2.Length == 0)
                {
                    Unwrapping.GenerateSecondaryUVSet(existing); // lightmap UVs, needed because the necklace pieces are baked
                    EditorUtility.SetDirty(existing);
                }

                return existing;
            }

            const int major = 20, minor = 6;
            const float majorRadius = 1f, minorRadius = 0.05f;
            var vertices = new Vector3[major * minor];
            var normals = new Vector3[major * minor];
            var uvs = new Vector2[major * minor];
            var triangles = new int[major * minor * 6];
            for (int i = 0; i < major; i++)
            {
                float a = i / (float)major * Mathf.PI * 2f;
                var ring = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                for (int j = 0; j < minor; j++)
                {
                    float b = j / (float)minor * Mathf.PI * 2f;
                    Vector3 normal = ring * Mathf.Cos(b) + Vector3.up * Mathf.Sin(b);
                    int index = i * minor + j;
                    vertices[index] = ring * majorRadius + normal * minorRadius;
                    normals[index] = normal;
                    uvs[index] = new Vector2(i / (float)major, j / (float)minor);
                }
            }

            int t = 0;
            for (int i = 0; i < major; i++)
            {
                int i1 = (i + 1) % major;
                for (int j = 0; j < minor; j++)
                {
                    int j1 = (j + 1) % minor;
                    int a = i * minor + j, b = i1 * minor + j, c = i1 * minor + j1, d = i * minor + j1;
                    triangles[t++] = a; triangles[t++] = d; triangles[t++] = b;
                    triangles[t++] = b; triangles[t++] = d; triangles[t++] = c;
                }
            }

            var mesh = new Mesh { name = "Torus_LowPoly", vertices = vertices, normals = normals, uv = uvs, triangles = triangles };
            mesh.RecalculateBounds();
            Unwrapping.GenerateSecondaryUVSet(mesh);
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        private delegate void PixelFiller(Color32[] pixels, int size);

        private static Texture2D EnsureTexture(string name, int size, PixelFiller filler)
        {
            string path = $"{TexturesFolder}/{name}.png";
            if (!File.Exists(Path.GetFullPath(path)))
            {
                var pixels = new Color32[size * size];
                filler(pixels, size);
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                tex.SetPixels32(pixels);
                tex.Apply();
                File.WriteAllBytes(Path.GetFullPath(path), tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Bilinear;
            importer.anisoLevel = 4;
            importer.maxTextureSize = 1024;
            importer.textureCompression = TextureImporterCompression.Compressed;
            var android = new TextureImporterPlatformSettings
            {
                name = "Android",
                overridden = true,
                maxTextureSize = 1024,
                format = TextureImporterFormat.ASTC_6x6,
                compressionQuality = 50
            };
            importer.SetPlatformTextureSettings(android);
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + seed * 982451653;
                h = (h ^ (h >> 13)) * 1274126177;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }

        private static float ValueNoise(float x, float y, int seed)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float xf = x - xi, yf = y - yi;
            xf = xf * xf * (3f - 2f * xf);
            yf = yf * yf * (3f - 2f * yf);
            float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed), c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, xf), Mathf.Lerp(c, d, xf), yf);
        }

        private static float Fbm(float x, float y, int seed, int octaves)
        {
            float sum = 0f, amplitude = 0.5f, total = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += ValueNoise(x, y, seed + i * 17) * amplitude;
                total += amplitude;
                x *= 2f;
                y *= 2f;
                amplitude *= 0.5f;
            }

            return sum / total;
        }

        private static Color32 ToColor32(Color c)
        {
            return new Color32((byte)(Mathf.Clamp01(c.r) * 255f), (byte)(Mathf.Clamp01(c.g) * 255f), (byte)(Mathf.Clamp01(c.b) * 255f), 255);
        }

        /// <summary>White marble tiles with grey veins and thin grout. The texture is 2 x 2 tiles of 1 m each (tiling 2.5 on a 5 m floor).</summary>
        private static void MarblePixels(Color32[] pixels, int size)
        {
            int tiles = 2;
            int tileSize = size / tiles;
            var warm = new Color(0.95f, 0.94f, 0.91f);
            var grey = new Color(0.78f, 0.79f, 0.80f);
            var veinColor = new Color(0.50f, 0.52f, 0.55f);
            var grout = new Color(0.62f, 0.60f, 0.56f);
            for (int ty = 0; ty < tiles; ty++)
            {
                for (int tx = 0; tx < tiles; tx++)
                {
                    float ox = tx * 13.7f + ty * 7.3f, oy = tx * 3.1f + ty * 11.9f;
                    float variation = 0.97f + Hash(tx, ty, 5) * 0.05f;
                    for (int y = 0; y < tileSize; y++)
                    {
                        for (int x = 0; x < tileSize; x++)
                        {
                            float u = (x + 0.5f) / tileSize, v = (y + 0.5f) / tileSize;
                            float cloud = Fbm(u * 3f + ox, v * 3f + oy, 1, 4);
                            float warp = Fbm(u * 2.5f + ox + 5.2f, v * 2.5f + oy + 1.3f, 2, 5);
                            float d = (u * 0.8f + v * 0.6f) * 5f + warp * 3.2f;
                            float vein1 = Mathf.Pow(1f - Mathf.Abs(Mathf.Sin(d * Mathf.PI)), 18f);
                            float vein2 = Mathf.Pow(1f - Mathf.Abs(Mathf.Sin((d * 0.43f + warp) * Mathf.PI)), 6f) * 0.3f;
                            Color c = Color.Lerp(warm, grey, Mathf.SmoothStep(0.35f, 0.8f, cloud) * 0.6f);
                            c = Color.Lerp(c, veinColor, Mathf.Clamp01(vein1 * 0.8f + vein2 * 0.35f));
                            c *= variation;
                            float edge = Mathf.Min(Mathf.Min(x, tileSize - 1 - x), Mathf.Min(y, tileSize - 1 - y));
                            if (edge < 5f)
                            {
                                c = Color.Lerp(grout, c, Mathf.SmoothStep(2.5f, 5f, edge));
                            }

                            pixels[(ty * tileSize + y) * size + tx * tileSize + x] = ToColor32(c);
                        }
                    }
                }
            }
        }

        /// <summary>Dark walnut: long grain lines along U with fine fibres.</summary>
        private static void WalnutPixels(Color32[] pixels, int size)
        {
            var dark = new Color(0.14f, 0.08f, 0.045f);
            var light = new Color(0.34f, 0.20f, 0.105f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size, v = y / (float)size;
                    float warp = Fbm(u * 2f, v * 6f, 3, 4);
                    float rings = 0.5f + 0.5f * Mathf.Sin((v * 26f + warp * 5f) * Mathf.PI * 2f);
                    float fiber = Fbm(u * 3f, v * 90f, 4, 3);
                    float t = rings * 0.55f + fiber * 0.45f;
                    pixels[y * size + x] = ToColor32(Color.Lerp(dark, light, t));
                }
            }
        }

        /// <summary>Round rug: navy pile with gold rings and two gold footprints in the middle.</summary>
        private static void RugPixels(Color32[] pixels, int size)
        {
            var navy = new Color(0.05f, 0.08f, 0.21f);
            var gold = new Color(0.86f, 0.66f, 0.28f);
            var cream = new Color(0.90f, 0.84f, 0.70f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size, v = (y + 0.5f) / size;
                    float dx = u - 0.5f, dy = v - 0.5f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy) * 2f; // 1 at the edge of the disc
                    Color c = navy * (0.92f + 0.16f * Fbm(u * 60f, v * 60f, 7, 2));
                    if (r > 0.955f) c = cream;
                    else if (r > 0.915f && r < 0.945f) c = gold;
                    else if (r > 0.86f && r < 0.875f) c = gold;
                    else if (r > 0.66f && r < 0.675f) c = gold;
                    else if (r < 0.60f && r > 0.585f) c = gold;
                    // two footprints (toes up = +V = towards the shop)
                    foreach (float cx in new[] { 0.5f - 0.062f, 0.5f + 0.062f })
                    {
                        if (InEllipse(u, v, cx, 0.5f + 0.045f, 0.040f, 0.052f) || InEllipse(u, v, cx, 0.5f - 0.05f, 0.029f, 0.036f))
                        {
                            c = gold;
                        }
                    }

                    pixels[y * size + x] = ToColor32(c);
                }
            }
        }

        private static bool InEllipse(float u, float v, float cx, float cy, float rx, float ry)
        {
            float a = (u - cx) / rx, b = (v - cy) / ry;
            return a * a + b * b <= 1f;
        }
    }
}
