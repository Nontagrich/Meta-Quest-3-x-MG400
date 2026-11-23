using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GemStore.Teleop
{
    /// <summary>Big red E-STOP button (poke or ray, through the world canvas) plus a Reset button that unlocks teleop.</summary>
    public class EStopPanel : MonoBehaviour
    {
        [SerializeField] private TeleopController controller;
        [SerializeField] private Button estopButton;
        [SerializeField] private Button resetButton;
        [SerializeField] private TMP_Text stateLabel;

        private void OnEnable()
        {
            estopButton.onClick.AddListener(OnEStop);
            resetButton.onClick.AddListener(OnReset);
        }

        private void OnDisable()
        {
            estopButton.onClick.RemoveListener(OnEStop);
            resetButton.onClick.RemoveListener(OnReset);
        }

        private TeleopController Controller => controller != null ? controller : controller = TeleopController.Instance;

        private void OnEStop() => Controller?.TriggerEStop("panel button");

        private void OnReset() => Controller?.ResetEStop();

        private void Update()
        {
            bool locked = Controller != null && Controller.EStopLocked;
            resetButton.interactable = locked;
            if (stateLabel != null)
            {
                stateLabel.text = locked ? "<color=#FF5A4D>E-STOP ACTIVE</color>" : "<color=#B8C0D0>B button = E-STOP</color>";
            }
        }
    }
}
