using GemStore.Payment;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GemStore.Network.Editor
{
    /// <summary>Editor-only tools for the MQTT link: one-click scene setup and robot status simulation.</summary>
    public static class NetworkEditorTools
    {
        private const string SettingsPath = "Assets/_Project/Data/MqttSettings.asset";

        /// <summary>Creates the settings asset and a "Network" object (MqttService + OrderPublisher + OrderStatusListener) in the open scene.</summary>
        [MenuItem("GemStore/Setup Network")]
        public static void Setup()
        {
            var settings = AssetDatabase.LoadAssetAtPath<MqttSettings>(SettingsPath);
            if (settings == null)
            {
                if (!AssetDatabase.IsValidFolder("Assets/_Project/Data"))
                {
                    AssetDatabase.CreateFolder("Assets/_Project", "Data");
                }

                settings = ScriptableObject.CreateInstance<MqttSettings>();
                AssetDatabase.CreateAsset(settings, SettingsPath);
            }

            var service = Object.FindAnyObjectByType<MqttService>();
            if (service == null)
            {
                var go = new GameObject("Network");
                service = go.AddComponent<MqttService>();
                var publisher = go.AddComponent<OrderPublisher>();
                var listener = go.AddComponent<OrderStatusListener>();

                var publisherSerialized = new SerializedObject(publisher);
                publisherSerialized.FindProperty("mqtt").objectReferenceValue = service;
                publisherSerialized.ApplyModifiedPropertiesWithoutUndo();
                var listenerSerialized = new SerializedObject(listener);
                listenerSerialized.FindProperty("mqtt").objectReferenceValue = service;
                listenerSerialized.ApplyModifiedPropertiesWithoutUndo();
            }

            var serviceSerialized = new SerializedObject(service);
            serviceSerialized.FindProperty("settings").objectReferenceValue = settings;
            serviceSerialized.ApplyModifiedPropertiesWithoutUndo();

            // The Quest needs the INTERNET permission to open a socket to the broker.
            PlayerSettings.Android.forceInternetPermission = true;

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Selection.activeObject = settings;
            Debug.Log("GemStore: network set up. Edit Assets/_Project/Data/MqttSettings.asset to choose the broker.");
        }

        [MenuItem("GemStore/Simulate Robot Status")]
        public static void SimulateRobotStatus()
        {
            var listener = Object.FindAnyObjectByType<OrderStatusListener>();
            if (listener == null)
            {
                Debug.LogWarning("GemStore: no OrderStatusListener in the scene. Run GemStore/Setup Network first.");
                return;
            }

            listener.SimulateSequence(2f);
        }

        [MenuItem("GemStore/Simulate Robot Status", true)]
        private static bool ValidateSimulateRobotStatus()
        {
            return Application.isPlaying;
        }
    }

    [CustomEditor(typeof(OrderStatusListener))]
    public class OrderStatusListenerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var listener = (OrderStatusListener)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Topic", listener.StatusTopic);

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                EditorGUILayout.LabelField("Simulate the robot (latest order)", EditorStyles.boldLabel);
                if (GUILayout.Button("Simulate picking → packing → done"))
                {
                    listener.SimulateSequence(2f);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    SendButton(listener, "Picking", OrderStatus.Picking);
                    SendButton(listener, "Packing", OrderStatus.Packing);
                    SendButton(listener, "Done", OrderStatus.Done);
                    SendButton(listener, "Error", OrderStatus.Error);
                }
            }

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode and pay for an order first, then use these buttons.", MessageType.Info);
            }
        }

        private static void SendButton(OrderStatusListener listener, string label, OrderStatus status)
        {
            if (!GUILayout.Button(label))
            {
                return;
            }

            var terminal = Object.FindAnyObjectByType<PaymentTerminal>();
            if (terminal == null || terminal.LastOrder == null)
            {
                Debug.LogWarning("GemStore: there is no paid order yet. Buy something first.");
                return;
            }

            listener.SimulateStatus(terminal.LastOrder.orderId, status, status == OrderStatus.Error ? "simulated error" : string.Empty);
        }
    }

    [CustomEditor(typeof(MqttService))]
    public class MqttServiceEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var service = (MqttService)target;
            if (Application.isPlaying)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("State", service.State.ToString());
                if (GUILayout.Button("Reconnect"))
                {
                    service.Connect();
                }

                Repaint();
            }
        }
    }
}
