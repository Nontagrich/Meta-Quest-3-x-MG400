using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GemStore.Staff.Editor
{
    /// <summary>Creates the (empty) AgoraConfig asset in the git-ignored Secrets folder and the StaffCall object in the scene.</summary>
    public static class StaffCallEditorSetup
    {
        private const string SecretsFolder = "Assets/_Project/Secrets";
        private const string ConfigPath = SecretsFolder + "/AgoraConfig.asset";

        [MenuItem("GemStore/Setup Staff Call")]
        public static void Setup()
        {
            if (!AssetDatabase.IsValidFolder(SecretsFolder))
            {
                AssetDatabase.CreateFolder("Assets/_Project", "Secrets");
            }

            var config = AssetDatabase.LoadAssetAtPath<AgoraConfig>(ConfigPath);
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<AgoraConfig>(); // appId and token stay empty: you type them in the Inspector
                AssetDatabase.CreateAsset(config, ConfigPath);
            }

            foreach (StaffCallManager old in Object.FindObjectsByType<StaffCallManager>(FindObjectsInactive.Include))
            {
                Object.DestroyImmediate(old.gameObject);
            }

            var go = new GameObject("StaffCall"); // root object (DontDestroyOnLoad)
            var manager = go.AddComponent<StaffCallManager>();
            var so = new SerializedObject(manager);
            so.FindProperty("config").objectReferenceValue = config;
            so.ApplyModifiedPropertiesWithoutUndo();

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Selection.activeObject = config;
            Debug.Log("GemStore: staff call set up. Enter your Agora App ID (and token) in " + ConfigPath);
        }
    }
}
