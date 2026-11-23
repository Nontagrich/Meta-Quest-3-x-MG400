using UnityEngine;
using UnityEngine.SceneManagement;

namespace GemStore.Teleop
{
    /// <summary>Scene names of the app modes and the loader. All three scenes must be in the build settings (the builder does that).</summary>
    public static class ModeSwitcher
    {
        public const string ModeSelectScene = "ModeSelect";
        public const string StoreScene = "Storefront";
        public const string MaintenanceScene = "Maintenance";

        public static void LoadModeSelect() => Load(ModeSelectScene);

        public static void LoadStore() => Load(StoreScene);

        public static void LoadMaintenance() => Load(MaintenanceScene);

        public static void Load(string sceneName)
        {
            Debug.Log("[Mode] Loading " + sceneName);
            SceneManager.LoadScene(sceneName);
        }
    }
}
