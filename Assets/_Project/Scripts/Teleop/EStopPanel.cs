using GemStore.Network;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GemStore.Teleop
{
    /// <summary>
    /// Big red E-STOP button (poke or ray, through the world canvas) plus a Reset button.
    /// With a <see cref="TeleopController"/> (Maintenance scene) it uses that controller; without one (storefront staff panel)
    /// it uses <see cref="RobotEStopRelay"/>. Both send {"type":"estop"} / {"type":"estop_reset"} to the robot over RobotLink.
    /// </summary>
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

        private void OnEStop()
        {
            if (Controller != null)
            {
                Controller.TriggerEStop("panel button");
            }
            else if (RobotEStopRelay.Instance != null)
            {
                RobotEStopRelay.Instance.Trigger("panel button");
            }
            else
            {
                Debug.LogError("[Robot] E-STOP pressed but there is no TeleopController or RobotEStopRelay in the scene: nothing was sent!");
            }
        }

        private void OnReset()
        {
            if (Controller != null)
            {
                Controller.ResetEStop();
            }
            else
            {
                RobotEStopRelay.Instance?.ResetLock();
            }
        }

        private void Update()
        {
            bool hasController = Controller != null;
            bool locked = hasController ? Controller.EStopLocked : RobotEStopRelay.Instance != null && RobotEStopRelay.Instance.Locked;
            resetButton.interactable = locked;
            if (stateLabel != null)
            {
                string hint = hasController ? "B button = E-STOP" : "Press to stop the robot";
                stateLabel.text = locked ? "<color=#FF5A4D>E-STOP ACTIVE</color>" : "<color=#B8C0D0>" + hint + "</color>";
            }
        }
    }
}
