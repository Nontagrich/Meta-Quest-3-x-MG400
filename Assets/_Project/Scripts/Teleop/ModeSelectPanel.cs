using UnityEngine;
using UnityEngine.UI;

namespace GemStore.Teleop
{
    /// <summary>The Mode Select panel: Customer Store / Maintenance. The buttons are UI buttons, so poke and ray both work.</summary>
    [DisallowMultipleComponent]
    public class ModeSelectPanel : MonoBehaviour
    {
        [SerializeField] private Button storeButton;
        [SerializeField] private Button maintenanceButton;

        private void OnEnable()
        {
            if (storeButton != null) storeButton.onClick.AddListener(ModeSwitcher.LoadStore);
            if (maintenanceButton != null) maintenanceButton.onClick.AddListener(ModeSwitcher.LoadMaintenance);
        }

        private void OnDisable()
        {
            if (storeButton != null) storeButton.onClick.RemoveListener(ModeSwitcher.LoadStore);
            if (maintenanceButton != null) maintenanceButton.onClick.RemoveListener(ModeSwitcher.LoadMaintenance);
        }
    }
}
