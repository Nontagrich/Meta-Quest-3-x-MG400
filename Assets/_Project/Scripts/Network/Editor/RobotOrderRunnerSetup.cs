using System.Collections.Generic;
using CPE494;
using GemStore.Customize;
using GemStore.Payment;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GemStore.Network.Editor
{
    /// <summary>Menu "GemStore/Add Robot Order Runner (MG400 bridge)": puts the runner + a bridge client into the open scene.</summary>
    public static class RobotOrderRunnerSetup
    {
        [MenuItem("GemStore/Add Robot Order Runner (MG400 bridge)")]
        public static void Add()
        {
            RobotOrderRunner runner = Object.FindAnyObjectByType<RobotOrderRunner>();
            if (runner == null)
            {
                var go = new GameObject("RobotOrderRunner");
                runner = go.AddComponent<RobotOrderRunner>();
                Undo.RegisterCreatedObjectUndo(go, "Add Robot Order Runner");
            }

            if (Object.FindAnyObjectByType<MG400BridgeClient>() == null)
            {
                var root = new GameObject("MG400_Root");
                root.AddComponent<MG400BridgeClient>(); // host: set it to the PC's LAN IP for the Quest
                Undo.RegisterCreatedObjectUndo(root, "Add MG400 bridge client");
            }

            // one pick entry per gem, poses left at 0 on purpose: the taught poses are typed in by hand
            var serialized = new SerializedObject(runner);
            SerializedProperty list = serialized.FindProperty("pickPoints");
            var existing = new HashSet<string>();
            for (int i = 0; i < list.arraySize; i++)
            {
                existing.Add(list.GetArrayElementAtIndex(i).FindPropertyRelative("gemId").stringValue);
            }

            foreach (string guid in AssetDatabase.FindAssets("t:GemData"))
            {
                var gem = AssetDatabase.LoadAssetAtPath<GemStore.Showcase.GemData>(AssetDatabase.GUIDToAssetPath(guid));
                string id = gem != null ? gem.Id : null;
                if (string.IsNullOrEmpty(id) || existing.Contains(id))
                {
                    continue;
                }

                list.InsertArrayElementAtIndex(list.arraySize);
                SerializedProperty element = list.GetArrayElementAtIndex(list.arraySize - 1);
                element.FindPropertyRelative("gemId").stringValue = id;
                element.FindPropertyRelative("x").floatValue = 0f;
                element.FindPropertyRelative("y").floatValue = 0f;
            }

            serialized.ApplyModifiedProperties();

            foreach (OrderSender sender in Object.FindObjectsByType<OrderSender>(FindObjectsInactive.Include))
            {
                Undo.RecordObject(sender, "Disable OrderSender");
                sender.enabled = false; // orders now go through the bridge, not RobotLink
            }

            EditorSceneManager.MarkSceneDirty(runner.gameObject.scene);
            Selection.activeObject = runner;
            Debug.Log("[RobotOrder] Runner added. Fill in the pick poses (x, y, pickZ, r) per gem and set MG400BridgeClient.host.");
        }
    }
}
