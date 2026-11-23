using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GemStore.Teleop
{
    /// <summary>The calibration screen of the Maintenance mode: instruction, fit errors and the Start / Capture / Redo / Accept / Back buttons.</summary>
    [DisallowMultipleComponent]
    public class CalibrationPanel : MonoBehaviour
    {
        [SerializeField] private RobotCalibration calibration;
        [SerializeField] private TMP_Text titleLabel;
        [SerializeField] private TMP_Text instructionLabel;
        [SerializeField] private TMP_Text errorsLabel;
        [SerializeField] private Button startButton;
        [SerializeField] private TMP_Text startLabel;
        [SerializeField] private Button captureButton;
        [SerializeField] private Button redoButton;
        [SerializeField] private Button acceptButton;
        [SerializeField] private Button backButton;
        [SerializeField] private Color okColor = new Color(0.45f, 0.9f, 0.55f);
        [SerializeField] private Color warnColor = new Color(1f, 0.6f, 0.25f);

        private bool subscribed;

        private void OnEnable()
        {
            if (startButton != null) startButton.onClick.AddListener(OnStart);
            if (captureButton != null) captureButton.onClick.AddListener(OnCapture);
            if (redoButton != null) redoButton.onClick.AddListener(OnRedo);
            if (acceptButton != null) acceptButton.onClick.AddListener(OnAccept);
            if (backButton != null) backButton.onClick.AddListener(ModeSwitcher.LoadModeSelect);
            Attach();
            Refresh();
        }

        private void Start()
        {
            Attach();
            Refresh();
        }

        private void OnDisable()
        {
            if (startButton != null) startButton.onClick.RemoveListener(OnStart);
            if (captureButton != null) captureButton.onClick.RemoveListener(OnCapture);
            if (redoButton != null) redoButton.onClick.RemoveListener(OnRedo);
            if (acceptButton != null) acceptButton.onClick.RemoveListener(OnAccept);
            if (backButton != null) backButton.onClick.RemoveListener(ModeSwitcher.LoadModeSelect);
            if (subscribed && calibration != null)
            {
                calibration.OnStateChanged -= Refresh;
                calibration.OnCalibrationChanged -= Refresh;
            }

            subscribed = false;
        }

        private void Attach()
        {
            if (calibration == null)
            {
                calibration = RobotCalibration.Instance;
            }

            if (calibration != null && !subscribed)
            {
                calibration.OnStateChanged += Refresh;
                calibration.OnCalibrationChanged += Refresh;
                subscribed = true;
            }
        }

        private void OnStart() => calibration?.StartCalibration();

        private void OnCapture() => calibration?.CaptureCurrent();

        private void OnRedo() => calibration?.Redo();

        private void OnAccept() => calibration?.Accept();

        private void Refresh()
        {
            if (calibration == null || calibration.Config == null)
            {
                return;
            }

            CalibrationState state = calibration.State;
            bool capturing = state == CalibrationState.CapturingP0 || state == CalibrationState.CapturingP1 || state == CalibrationState.CapturingP2;
            bool review = state == CalibrationState.Review;

            if (instructionLabel != null)
            {
                string text = calibration.Instruction;
                if (!string.IsNullOrEmpty(calibration.LastError))
                {
                    text = calibration.LastError;
                }

                instructionLabel.text = text;
                instructionLabel.color = review && calibration.HasWarning ? warnColor : Color.white;
            }

            if (errorsLabel != null)
            {
                errorsLabel.text = BuildErrors();
            }

            if (startButton != null) startButton.gameObject.SetActive(state == CalibrationState.Idle);
            if (startLabel != null) startLabel.text = calibration.IsCalibrated ? "Recalibrate" : "Start calibration";
            if (captureButton != null) captureButton.gameObject.SetActive(capturing);
            if (redoButton != null) redoButton.gameObject.SetActive(capturing || review);
            if (acceptButton != null) acceptButton.gameObject.SetActive(review);
        }

        private string BuildErrors()
        {
            float[] errors = calibration.ErrorsMm;
            if (errors == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder("Fit error:  ");
            for (int i = 0; i < errors.Length; i++)
            {
                string color = errors[i] > RobotCalibration.WarnErrorMm ? ColorUtility.ToHtmlStringRGB(warnColor) : ColorUtility.ToHtmlStringRGB(okColor);
                sb.Append($"P{i} <color=#{color}>{errors[i]:F1} mm</color>   ");
            }

            return sb.ToString();
        }
    }
}
