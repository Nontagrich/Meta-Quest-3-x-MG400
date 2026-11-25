using UnityEngine;
using UnityEngine.UI;

namespace GemStore.Teleop
{
    /// <summary>Small button that shows / hides another object (for example the robot address panel in the Storefront staff corner).</summary>
    [DisallowMultipleComponent]
    public class PanelToggleButton : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private GameObject target;

        private void OnEnable()
        {
            if (button != null) button.onClick.AddListener(Toggle);
        }

        private void OnDisable()
        {
            if (button != null) button.onClick.RemoveListener(Toggle);
        }

        public void Toggle()
        {
            if (target != null) target.SetActive(!target.activeSelf);
        }
    }
}
