using UnityEngine;
using UnityEngine.UI;

namespace GemStore.Teleop
{
    /// <summary>Small button that returns to the Mode Select scene (used in the Storefront as its entry point, and as Back in Maintenance).</summary>
    [DisallowMultipleComponent]
    public class ModeMenuButton : MonoBehaviour
    {
        [SerializeField] private Button button;

        private void OnEnable()
        {
            if (button != null) button.onClick.AddListener(ModeSwitcher.LoadModeSelect);
        }

        private void OnDisable()
        {
            if (button != null) button.onClick.RemoveListener(ModeSwitcher.LoadModeSelect);
        }
    }
}
