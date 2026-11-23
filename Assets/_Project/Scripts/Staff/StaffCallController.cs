using UnityEngine;

namespace GemStore.Staff
{
    /// <summary>
    /// Call flow between the bell, the staff screen and <see cref="StaffCallManager"/>.
    /// Idle -> (press) Calling -> (staff joins, or is already there) Connected -> (End Call / staff leaves) Idle.
    /// No staff within <see cref="callTimeoutSeconds"/> -> "Staff is busy" message -> leave -> Idle.
    /// </summary>
    [DisallowMultipleComponent]
    public class StaffCallController : MonoBehaviour
    {
        public enum UiState
        {
            Idle,
            Calling,
            Connected,
            Message // a short notice ("Staff is busy...") is on screen; the call is already left
        }

        [SerializeField] private StaffCallButton button;
        [SerializeField] private StaffVideoPanel panel;
        [SerializeField] private AudioSource ringSource;
        [SerializeField, Min(5f)] private float callTimeoutSeconds = 30f;
        [SerializeField, Min(1f)] private float messageSeconds = 3f;

        private StaffCallManager manager;
        private float stateStartTime;
        private string idleText = "Need help? Press Call Staff";

        public static StaffCallController Instance { get; private set; }

        public UiState State { get; private set; } = UiState.Idle;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        private void Awake()
        {
            Instance = this;
        }

        private void OnEnable()
        {
            if (button != null) button.Pressed += RequestCall;
            if (panel != null)
            {
                panel.EndClicked += EndCall;
                panel.MuteClicked += ToggleMute;
            }

            Attach();
            ApplyIdle();
        }

        private void Start()
        {
            Attach();
        }

        private void OnDisable()
        {
            if (button != null) button.Pressed -= RequestCall;
            if (panel != null)
            {
                panel.EndClicked -= EndCall;
                panel.MuteClicked -= ToggleMute;
            }

            if (manager != null)
            {
                manager.OnCallStateChanged -= HandleManagerState;
                manager.OnRemoteUserJoined -= HandleRemoteJoined;
                manager.OnRemoteUserLeft -= HandleRemoteLeft;
                manager = null;
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // The headset going to sleep or the app being sent to the background ends the call (the engine is also shut down on quit).
        private void OnApplicationPause(bool paused)
        {
            if (paused && State != UiState.Idle)
            {
                EndCall();
            }
        }

        private void Attach()
        {
            if (manager != null || StaffCallManager.Instance == null)
            {
                return;
            }

            manager = StaffCallManager.Instance;
            manager.OnCallStateChanged += HandleManagerState;
            manager.OnRemoteUserJoined += HandleRemoteJoined;
            manager.OnRemoteUserLeft += HandleRemoteLeft;
        }

        private void Update()
        {
            if (State == UiState.Calling && Time.unscaledTime - stateStartTime >= callTimeoutSeconds)
            {
                Notice("Staff is busy, please try again");
            }
            else if (State == UiState.Message && Time.unscaledTime - stateStartTime >= messageSeconds)
            {
                ApplyIdle();
            }
        }

        // ------------------------------------------------------------------------------------------------------

        /// <summary>Starts a call. Does nothing unless the state is Idle (so pressing the bell during a call is ignored).</summary>
        public void RequestCall()
        {
            if (State != UiState.Idle)
            {
                return;
            }

            Attach();
            if (manager == null)
            {
                Debug.LogError("[StaffUI] There is no StaffCallManager in the scene.");
                return;
            }

            SetState(UiState.Calling);
            if (panel != null)
            {
                panel.SetMicMuted(false);
                panel.ShowPlaceholder("Calling staff...", true);
            }

            button?.SetLight(StaffCallButton.LightMode.Calling);
            StartRing();
            manager.JoinCall();
            if (manager.State == StaffCallState.Error)
            {
                Notice("Cannot reach staff, please ask a team member");
            }
        }

        /// <summary>Ends the call (End Call button, staff left, debug menu). Safe to call in any state.</summary>
        public void EndCall()
        {
            if (State == UiState.Idle)
            {
                return;
            }

            Leave();
            ApplyIdle();
        }

        private void ToggleMute()
        {
            if (manager == null || State == UiState.Idle || State == UiState.Message)
            {
                return;
            }

            manager.SetMicMuted(!manager.IsMicMuted);
            panel?.SetMicMuted(manager.IsMicMuted);
        }

        private void Connect()
        {
            if (State != UiState.Calling)
            {
                return;
            }

            StopRing();
            SetState(UiState.Connected);
            panel?.ShowVideo();
            manager.ShowRemoteVideo(panel != null ? panel.Surface : null);
            button?.SetLight(StaffCallButton.LightMode.Connected);
        }

        private void Leave()
        {
            StopRing();
            if (manager != null)
            {
                manager.HideRemoteVideo(panel != null ? panel.Surface : null);
                manager.LeaveCall();
            }
        }

        private void Notice(string text)
        {
            Leave();
            SetState(UiState.Message);
            panel?.ShowPlaceholder(text, false);
            button?.SetLight(StaffCallButton.LightMode.Idle);
        }

        private void ApplyIdle()
        {
            if (State != UiState.Idle)
            {
                SetState(UiState.Idle);
            }

            panel?.ShowPlaceholder(idleText, false);
            button?.SetLight(StaffCallButton.LightMode.Idle);
        }

        private void SetState(UiState state)
        {
            if (State == state)
            {
                return;
            }

            Debug.Log($"[StaffUI] {State} -> {state}");
            State = state;
            stateStartTime = Time.unscaledTime;
        }

        // ---- manager events ---------------------------------------------------------------------------------------

        private void HandleManagerState(StaffCallState state)
        {
            if (state == StaffCallState.InCall && State == UiState.Calling)
            {
                Connect();
            }
            else if (state == StaffCallState.Error && (State == UiState.Calling || State == UiState.Connected))
            {
                Notice("Cannot reach staff, please ask a team member");
            }
        }

        private void HandleRemoteJoined(uint uid)
        {
            if (State == UiState.Calling)
            {
                Connect();
            }
            else if (State == UiState.Connected)
            {
                manager.ShowRemoteVideo(panel != null ? panel.Surface : null); // another staff member: follow the current one
            }
        }

        private void HandleRemoteLeft(uint uid)
        {
            if (State == UiState.Connected && manager.RemoteUid == 0)
            {
                EndCall();
            }
            else if (State == UiState.Connected)
            {
                manager.ShowRemoteVideo(panel != null ? panel.Surface : null);
            }
        }

        // ---- ringing -----------------------------------------------------------------------------------------------

        private void StartRing()
        {
            if (ringSource != null && ringSource.clip != null)
            {
                ringSource.loop = true;
                ringSource.Play();
            }
        }

        private void StopRing()
        {
            if (ringSource != null)
            {
                ringSource.Stop();
            }
        }
    }
}
