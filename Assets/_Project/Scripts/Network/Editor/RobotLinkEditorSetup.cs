using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GemStore.Network.Editor
{
    /// <summary>Creates RobotLinkConfig, the RobotLink object, the counter status lamp and sets Android Internet Access = Require.</summary>
    public static class RobotLinkEditorSetup
    {
        private const string ConfigPath = "Assets/_Project/Data/RobotLinkConfig.asset";

        [MenuItem("GemStore/Setup Robot Link")]
        public static void Setup()
        {
            var config = AssetDatabase.LoadAssetAtPath<RobotLinkConfig>(ConfigPath);
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<RobotLinkConfig>(); // defaults: 127.0.0.1:5005, 3 s / 2 s / 6 s
                AssetDatabase.CreateAsset(config, ConfigPath);
            }

            foreach (RobotLink old in Object.FindObjectsByType<RobotLink>(FindObjectsInactive.Include))
            {
                Object.DestroyImmediate(old.gameObject);
            }

            var linkObject = new GameObject("RobotLink"); // root object: DontDestroyOnLoad needs one
            var link = linkObject.AddComponent<RobotLink>();
            var so = new SerializedObject(link);
            so.FindProperty("config").objectReferenceValue = config;
            so.ApplyModifiedPropertiesWithoutUndo();
            linkObject.AddComponent<OrderSender>();
            linkObject.AddComponent<RobotOrderBridge>();

            BuildStatusLamp();

            PlayerSettings.Android.forceInternetPermission = true; // Internet Access = Require

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("GemStore: robot link set up (config, RobotLink, status lamp, Android Internet Access = Require).");
        }

        private static void BuildStatusLamp()
        {
            Transform checkout = GameObject.Find("Store")?.transform.Find("Checkout");
            if (checkout == null)
            {
                Debug.LogWarning("GemStore: Store/Checkout not found, status lamp skipped.");
                return;
            }

            Transform old = checkout.Find("RobotStatusLight");
            if (old != null)
            {
                Object.DestroyImmediate(old.gameObject);
            }

            Transform counter = null, top = null;
            foreach (Transform t in checkout.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "CheckoutCounter") counter = t;
                else if (t.name == "Top" && t.parent != null && t.parent.name == "Structure") top = t;
            }

            if (counter == null || top == null)
            {
                Debug.LogWarning("GemStore: counter top not found, status lamp skipped.");
                return;
            }

            Material on = LampMaterial("Robot_LampOn", new Color(0.15f, 0.9f, 0.3f));
            Material off = LampMaterial("Robot_LampOff", new Color(0.95f, 0.1f, 0.1f));

            var lampRoot = new GameObject("RobotStatusLight");
            lampRoot.transform.SetParent(checkout, false);
            Vector3 position = top.TransformPoint(new Vector3(-0.40f, 0.5f, -0.38f)) + Vector3.up * 0.006f;
            lampRoot.transform.SetPositionAndRotation(position, counter.rotation);

            GameObject lamp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            lamp.name = "Lamp";
            lamp.transform.SetParent(lampRoot.transform, false);
            lamp.transform.localScale = new Vector3(0.04f, 0.006f, 0.04f);
            Object.DestroyImmediate(lamp.GetComponent<Collider>());
            var renderer = lamp.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = off;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var status = lampRoot.AddComponent<RobotStatusLight>();
            var so = new SerializedObject(status);
            so.FindProperty("lamp").objectReferenceValue = renderer;
            so.FindProperty("connectedMaterial").objectReferenceValue = on;
            so.FindProperty("disconnectedMaterial").objectReferenceValue = off;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Material LampMaterial(string name, Color color)
        {
            string path = $"Assets/_Project/Materials/Checkout/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, path);
            }

            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", 0.5f);
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", color * 2.5f);
            EditorUtility.SetDirty(material);
            return material;
        }
    }
}
