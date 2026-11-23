using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GemStore.Network
{
    /// <summary>
    /// Shows the robot's IP:port and lets the technician edit it in the headset with a numeric keypad. Apply saves it (PlayerPrefs) and
    /// reconnects at once. Used on the Maintenance and Mode Select panels. Without a RobotLink in the scene (Mode Select) the address is
    /// only saved and used when the link starts in the next scene.
    /// </summary>
    public class RobotAddressPanel : MonoBehaviour
    {
        [SerializeField] private RobotLinkConfig config;
        [SerializeField] private TMP_Text addressLabel;
        [SerializeField] private TMP_Text statusLabel;
        [SerializeField] private Button editButton;
        [SerializeField] private GameObject keypad;
        [SerializeField] private Button[] keyButtons;
        [SerializeField, Tooltip("Characters typed by keyButtons, same order.")] private string[] keyChars;
        [SerializeField] private Button backspaceButton;
        [SerializeField] private Button cancelButton;
        [SerializeField] private Button applyButton;
        [SerializeField] private RectTransform background;
        [SerializeField] private float collapsedHeight = 230f;
        [SerializeField] private float expandedHeight = 800f;

        private string buffer = string.Empty;
        private bool editing;
        private string error = string.Empty;
        private RectTransform root;

        private void OnEnable()
        {
            root = transform as RectTransform;
            editButton.onClick.AddListener(BeginEdit);
            backspaceButton.onClick.AddListener(Backspace);
            cancelButton.onClick.AddListener(CancelEdit);
            applyButton.onClick.AddListener(OnApplyClicked);
            for (int i = 0; i < keyButtons.Length; i++)
            {
                string c = keyChars[i];
                keyButtons[i].onClick.AddListener(() => Type(c));
            }

            SetEditing(false);
        }

        private void OnDisable()
        {
            editButton.onClick.RemoveAllListeners();
            backspaceButton.onClick.RemoveAllListeners();
            cancelButton.onClick.RemoveAllListeners();
            applyButton.onClick.RemoveAllListeners();
            foreach (Button button in keyButtons)
            {
                button.onClick.RemoveAllListeners();
            }
        }

        private void OnApplyClicked()
        {
            Apply();
        }

        /// <summary>The address in use: the link's, the saved one, or the config asset's.</summary>
        public string CurrentAddress
        {
            get
            {
                RobotLink link = RobotLink.Instance;
                if (link != null && !string.IsNullOrEmpty(link.Host))
                {
                    return link.Host + ":" + link.Port;
                }

                if (RobotAddress.HasSaved)
                {
                    return RobotAddress.SavedHost + ":" + RobotAddress.SavedPort;
                }

                return config != null ? config.host + ":" + config.port : "127.0.0.1:5005";
            }
        }

        public bool Editing => editing;

        public string Buffer => buffer;

        public void BeginEdit()
        {
            buffer = CurrentAddress;
            error = string.Empty;
            SetEditing(true);
        }

        public void CancelEdit()
        {
            error = string.Empty;
            SetEditing(false);
        }

        public void Type(string character)
        {
            if (!editing || buffer.Length >= 21)
            {
                return;
            }

            buffer += character;
            error = string.Empty;
        }

        public void Backspace()
        {
            if (editing && buffer.Length > 0)
            {
                buffer = buffer.Substring(0, buffer.Length - 1);
                error = string.Empty;
            }
        }

        /// <summary>Validates, saves and reconnects. Returns false (and shows the problem) if the text is not a valid address.</summary>
        public bool Apply()
        {
            int defaultPort = config != null ? config.port : 5005;
            if (!RobotAddress.TryParse(buffer, defaultPort, out string host, out int port, out string problem))
            {
                error = problem;
                return false;
            }

            RobotLink link = RobotLink.Instance;
            if (link != null)
            {
                link.SetAddress(host, port);
            }
            else
            {
                RobotAddress.Save(host, port);
            }

            Debug.Log($"[Robot] Address set to {host}:{port}");
            error = string.Empty;
            SetEditing(false);
            return true;
        }

        private void SetEditing(bool value)
        {
            editing = value;
            if (keypad != null)
            {
                keypad.SetActive(value);
            }

            editButton.gameObject.SetActive(!value);
            if (root != null)
            {
                root.sizeDelta = new Vector2(root.sizeDelta.x, value ? expandedHeight : collapsedHeight);
            }
        }

        private void Update()
        {
            addressLabel.text = editing ? buffer + "<color=#FFD633>_</color>" : CurrentAddress;
            if (statusLabel == null)
            {
                return;
            }

            if (editing && !string.IsNullOrEmpty(error))
            {
                statusLabel.text = "<color=#FF5A4D>" + error + "</color>";
                return;
            }

            RobotLink link = RobotLink.Instance;
            if (editing)
            {
                statusLabel.text = "<color=#B8C0D0>Type IP:port, then Apply</color>";
            }
            else if (link == null)
            {
                statusLabel.text = "<color=#B8C0D0>link starts in the next mode</color>";
            }
            else
            {
                statusLabel.text = link.IsConnected ? "<color=#5BE37A>connected</color>" : "<color=#FF5A4D>offline</color>";
            }
        }
    }
}
