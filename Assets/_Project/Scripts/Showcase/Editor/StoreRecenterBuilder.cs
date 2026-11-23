using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GemStore.Showcase.Editor
{
    /// <summary>
    /// Adds the StoreRecenter object to the Storefront scene (run again to rebuild). It also removes the old "StoreMenu" panel if present.
    /// </summary>
    public static class StoreRecenterBuilder
    {
        private const string ScenePath = "Assets/_Project/Scenes/Storefront.unity";
        private const string RecenterName = "StoreRecenter";
        private const string MenuName = "StoreMenu";

        [MenuItem("GemStore/Add Store Recenter")]
        public static void Build()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.path != ScenePath)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    return;
                }

                scene = EditorSceneManager.OpenScene(ScenePath);
            }

            foreach (string name in new[] { RecenterName, MenuName })
            {
                GameObject old = GameObject.Find(name);
                if (old != null)
                {
                    Object.DestroyImmediate(old);
                }
            }

            GameObject rug = GameObject.Find("Rug_StandHere");
            var recenterObject = new GameObject(RecenterName);
            var recenter = recenterObject.AddComponent<StoreRecenter>();
            var serialized = new SerializedObject(recenter);
            serialized.FindProperty("xrOrigin").objectReferenceValue = Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
            serialized.FindProperty("standingSpot").objectReferenceValue = rug != null ? rug.transform : null;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            if (rug == null)
            {
                Debug.LogWarning("GemStore: Rug_StandHere not found; StoreRecenter only turns the user (no standing spot).");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("GemStore: StoreRecenter added to Storefront.");
        }

        /// <summary>Adds GrabWithTriggerOrGrip to the Mode Select menu scene (grip or trigger presses the buttons). Never to Maintenance.</summary>
        [MenuItem("GemStore/Add Grip Or Trigger To Mode Select")]
        public static void AddGrabInputToModeSelect()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            string previous = EditorSceneManager.GetActiveScene().path;
            var scene = EditorSceneManager.OpenScene("Assets/_Project/Scenes/ModeSelect.unity");
            if (Object.FindAnyObjectByType<GrabWithTriggerOrGrip>() == null)
            {
                new GameObject("MenuGrabInput").AddComponent<GrabWithTriggerOrGrip>();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            if (!string.IsNullOrEmpty(previous) && previous != scene.path)
            {
                EditorSceneManager.OpenScene(previous);
            }

            Debug.Log("GemStore: Mode Select buttons press with grip or trigger.");
        }

        /// <summary>Adds GrabWithTriggerOrGrip to the Storefront scene (grab with grip OR trigger). Never to Maintenance.</summary>
        [MenuItem("GemStore/Add Grab With Trigger Or Grip")]
        public static void AddGrabInput()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.path != ScenePath)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    return;
                }

                scene = EditorSceneManager.OpenScene(ScenePath);
            }

            if (Object.FindAnyObjectByType<GrabWithTriggerOrGrip>() == null)
            {
                new GameObject("StoreGrabInput").AddComponent<GrabWithTriggerOrGrip>();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("GemStore: Storefront grabs with grip or trigger.");
        }
    }
}
