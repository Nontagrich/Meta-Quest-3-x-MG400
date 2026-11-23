using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GemStore.Teleop
{
    /// <summary>Buttons of the Maintenance mode: control mode, move / lock the control volume, mark / clear the gem, Pick and Place.</summary>
    public class TeleopActionsPanel : MonoBehaviour
    {
        [SerializeField] private TeleopController controller;
        [SerializeField] private Button modeButton;
        [SerializeField] private TMP_Text modeLabel;
        [SerializeField] private Button volumeButton;
        [SerializeField] private TMP_Text volumeLabel;
        [SerializeField] private Button markButton;
        [SerializeField] private TMP_Text markLabel;
        [SerializeField] private Button clearButton;
        [SerializeField] private Button pickButton;
        [SerializeField] private Button placeButton;
        [SerializeField] private TMP_Text hintLabel;

        private TeleopController Controller => controller != null ? controller : controller = TeleopController.Instance;

        private void OnEnable()
        {
            modeButton.onClick.AddListener(OnMode);
            volumeButton.onClick.AddListener(OnVolume);
            markButton.onClick.AddListener(OnMark);
            clearButton.onClick.AddListener(OnClear);
            pickButton.onClick.AddListener(OnPick);
            placeButton.onClick.AddListener(OnPlace);
        }

        private void OnDisable()
        {
            modeButton.onClick.RemoveListener(OnMode);
            volumeButton.onClick.RemoveListener(OnVolume);
            markButton.onClick.RemoveListener(OnMark);
            clearButton.onClick.RemoveListener(OnClear);
            pickButton.onClick.RemoveListener(OnPick);
            placeButton.onClick.RemoveListener(OnPlace);
        }

        private void OnMode() => Controller?.ToggleMode();

        private void OnVolume()
        {
            ControlVolume volume = Controller != null ? Controller.Volume : null;
            if (volume != null && volume.Ready)
            {
                volume.SetUnlocked(!volume.Unlocked);
            }
        }

        private void OnMark() => Controller?.ToggleMarking();

        private void OnClear() => Controller?.ClearGem();

        private void OnPick() => Controller?.RequestPick();

        private void OnPlace() => Controller?.RequestPlace();

        private void Update()
        {
            TeleopController c = Controller;
            if (c == null)
            {
                return;
            }

            modeLabel.text = c.Mode == ControlMode.Offset ? "Mode: OFFSET (safe)\n<size=70%>tap for ABSOLUTE</size>" : "Mode: ABSOLUTE\n<size=70%>tap for OFFSET (safe)</size>";
            ControlVolume volume = c.Volume;
            bool volumeReady = volume != null && volume.Ready;
            volumeButton.interactable = volumeReady && c.Mode == ControlMode.Offset && !c.Following;
            volumeLabel.text = volumeReady && volume.Unlocked ? "Lock volume" : "Move volume";
            markLabel.text = c.Marking ? "Cancel marking" : "Mark gem";
            markButton.interactable = c.Calibrated && !c.Following;
            clearButton.interactable = c.HasGem;
            bool macroReady = c.Calibrated && c.LinkConnected && c.HasRobotPose && !c.EStopLocked && !c.MacroActive;
            pickButton.interactable = macroReady;
            placeButton.interactable = macroReady;
            if (hintLabel != null)
            {
                hintLabel.text = c.PendingMacro != MacroKind.None ? "Hold the right grip to run " + c.PendingMacro
                    : "Hold grip + press Pick / Place. Releasing the grip aborts.";
            }
        }
    }
}
