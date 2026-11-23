using System;
using System.Collections.Concurrent;
using Agora.Rtc;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace GemStore.Staff
{
    /// <summary>State of the staff call.</summary>
    public enum StaffCallState
    {
        Idle,
        RequestingPermission,
        Joining,
        WaitingForStaff, // joined the channel, nobody from staff is in it yet
        InCall,          // at least one staff member is in the channel
        Error
    }

    /// <summary>
    /// Voice call from the customer (Quest) to the shop staff through Agora. The Quest publishes only its microphone and receives
    /// the staff's video and audio; it never publishes a camera. Callbacks from the Agora SDK are queued and raised on the main thread.
    /// </summary>
    [DisallowMultipleComponent]
    public class StaffCallManager : MonoBehaviour
    {
        [SerializeField] private AgoraConfig config;

        private readonly ConcurrentQueue<Action> mainThreadQueue = new ConcurrentQueue<Action>();
        private readonly System.Collections.Generic.HashSet<uint> remoteUsers = new System.Collections.Generic.HashSet<uint>();

        private IRtcEngine engine;
        private bool joinRequested;
        private string lastEngineError;

        public static StaffCallManager Instance { get; private set; }

        public StaffCallState State { get; private set; } = StaffCallState.Idle;

        /// <summary>The first staff member in the channel (0 if none). Use it to show their video (see <see cref="ShowRemoteVideo"/>).</summary>
        public uint RemoteUid { get; private set; }

        public string ChannelName => config != null ? config.channelName : string.Empty;

        /// <summary>Last error as readable text (empty when there was none).</summary>
        public string LastError => lastEngineError;

        /// <summary>True while the customer's microphone is muted (resets to false on every new call).</summary>
        public bool IsMicMuted { get; private set; }

        public event Action<StaffCallState> OnCallStateChanged;
        public event Action<uint> OnRemoteUserJoined;
        public event Action<uint> OnRemoteUserLeft;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            while (mainThreadQueue.TryDequeue(out Action action))
            {
                action();
            }
        }

        private void OnApplicationQuit()
        {
            Shutdown();
        }

        private void OnDestroy()
        {
            Shutdown();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Asks for the microphone (Android), creates the engine if needed and joins the staff channel.</summary>
        public void JoinCall()
        {
            if (State != StaffCallState.Idle && State != StaffCallState.Error)
            {
                Debug.LogWarning("[Staff] JoinCall ignored: already " + State + ".");
                return;
            }

            if (config == null || string.IsNullOrWhiteSpace(config.appId))
            {
                Fail("No Agora App ID. Open Assets/_Project/Secrets/AgoraConfig.asset and enter your App ID in the Inspector.");
                return;
            }

            joinRequested = true;
            lastEngineError = string.Empty;
            IsMicMuted = false;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                SetState(StaffCallState.RequestingPermission);
                var callbacks = new PermissionCallbacks();
                callbacks.PermissionGranted += _ => mainThreadQueue.Enqueue(ContinueJoin);
                callbacks.PermissionDenied += _ => mainThreadQueue.Enqueue(() => Fail("Microphone permission was denied. Allow the microphone for this app in the headset settings, then call again."));
                callbacks.PermissionDeniedAndDontAskAgain += _ => mainThreadQueue.Enqueue(() => Fail("Microphone permission was denied permanently. Enable it in the headset: Settings > Apps > this app > Permissions."));
                Permission.RequestUserPermission(Permission.Microphone, callbacks);
                return;
            }
#endif
            ContinueJoin();
        }

        /// <summary>Leaves the channel (the engine stays ready for the next call).</summary>
        public void LeaveCall()
        {
            joinRequested = false;
            if (engine != null && State != StaffCallState.Idle)
            {
                engine.LeaveChannel();
            }

            remoteUsers.Clear();
            RemoteUid = 0;
            SetState(StaffCallState.Idle);
        }

        /// <summary>Mutes or unmutes the customer's microphone (the channel stays joined).</summary>
        public void SetMicMuted(bool muted)
        {
            IsMicMuted = muted;
            if (engine != null)
            {
                engine.MuteLocalAudioStream(muted);
            }
        }

        /// <summary>Stops rendering the staff video on a surface.</summary>
        public void HideRemoteVideo(VideoSurface surface)
        {
            if (surface != null)
            {
                surface.SetEnable(false);
                surface.SetForUser(0);
            }
        }

        /// <summary>Shows the staff member's video on a VideoSurface (a quad/RawImage with the Agora VideoSurface component).</summary>
        public void ShowRemoteVideo(VideoSurface surface)
        {
            if (surface == null)
            {
                return;
            }

            if (RemoteUid == 0)
            {
                surface.SetEnable(false);
                return;
            }

            surface.SetForUser(RemoteUid, ChannelName, VIDEO_SOURCE_TYPE.VIDEO_SOURCE_REMOTE);
            surface.SetEnable(true);
        }

        // ------------------------------------------------------------------------------------------------------
        // Engine
        // ------------------------------------------------------------------------------------------------------

        private void ContinueJoin()
        {
            if (!joinRequested)
            {
                return;
            }

            if (!EnsureEngine())
            {
                return;
            }

            SetState(StaffCallState.Joining);

            var options = new ChannelMediaOptions();
            options.channelProfile.SetValue(CHANNEL_PROFILE_TYPE.CHANNEL_PROFILE_COMMUNICATION);
            options.clientRoleType.SetValue(CLIENT_ROLE_TYPE.CLIENT_ROLE_BROADCASTER);
            options.publishMicrophoneTrack.SetValue(!IsMicMuted);   // the customer talks
            options.publishCameraTrack.SetValue(false);      // the Quest never sends video
            options.autoSubscribeAudio.SetValue(true);
            options.autoSubscribeVideo.SetValue(true);       // but it shows the staff's video

            string token = config.token != null ? config.token.Trim() : string.Empty;
            if (token.Length == 0)
            {
                Debug.LogWarning("[Staff] No token set: this only works if the Agora project allows 'App ID only' authentication.");
            }

            int result = engine.JoinChannel(token, config.channelName, 0, options);
            if (result != 0)
            {
                Fail("JoinChannel failed: " + Describe(result));
            }
        }

        private bool EnsureEngine()
        {
            if (engine != null)
            {
                return true;
            }

            try
            {
                engine = RtcEngine.CreateAgoraRtcEngine();
                var context = new RtcEngineContext
                {
                    appId = config.appId.Trim(),
                    channelProfile = CHANNEL_PROFILE_TYPE.CHANNEL_PROFILE_COMMUNICATION,
                    audioScenario = AUDIO_SCENARIO_TYPE.AUDIO_SCENARIO_DEFAULT,
                };
                int result = engine.Initialize(context);
                if (result != 0)
                {
                    DisposeEngine();
                    Fail("Could not start the Agora engine: " + Describe(result));
                    return false;
                }

                engine.InitEventHandler(new Handler(this));
                engine.EnableAudio();
                engine.EnableVideo();          // needed to RECEIVE the staff's video
                engine.EnableLocalVideo(false); // no camera capture on the Quest
                engine.MuteLocalVideoStream(true);
                engine.SetChannelProfile(CHANNEL_PROFILE_TYPE.CHANNEL_PROFILE_COMMUNICATION);
                return true;
            }
            catch (Exception exception)
            {
                DisposeEngine();
                Fail("Agora could not be loaded (" + exception.GetType().Name + "): " + exception.Message);
                return false;
            }
        }

        private void Shutdown()
        {
            joinRequested = false;
            if (engine == null)
            {
                return;
            }

            try
            {
                engine.LeaveChannel();
            }
            catch (Exception)
            {
            }

            DisposeEngine();
            remoteUsers.Clear();
            RemoteUid = 0;
            State = StaffCallState.Idle;
        }

        private void DisposeEngine()
        {
            if (engine == null)
            {
                return;
            }

            try
            {
                engine.Dispose();
            }
            catch (Exception)
            {
            }

            engine = null;
        }

        // ------------------------------------------------------------------------------------------------------
        // State and events (main thread)
        // ------------------------------------------------------------------------------------------------------

        private void SetState(StaffCallState state)
        {
            if (State == state)
            {
                return;
            }

            State = state;
            Debug.Log("[Staff] Call state: " + state);
            OnCallStateChanged?.Invoke(state);
        }

        private void Fail(string message)
        {
            lastEngineError = message;
            Debug.LogError("[Staff] " + message);
            joinRequested = false;
            SetState(StaffCallState.Error);
        }

        private void HandleJoined()
        {
            if (!joinRequested)
            {
                return;
            }

            SetState(remoteUsers.Count > 0 ? StaffCallState.InCall : StaffCallState.WaitingForStaff);
        }

        private void HandleUserJoined(uint uid)
        {
            if (!joinRequested)
            {
                return; // late callback from a call that was already left
            }

            remoteUsers.Add(uid);
            if (RemoteUid == 0)
            {
                RemoteUid = uid;
            }

            if (State == StaffCallState.WaitingForStaff || State == StaffCallState.Joining)
            {
                SetState(StaffCallState.InCall);
            }

            OnRemoteUserJoined?.Invoke(uid);
        }

        private void HandleUserLeft(uint uid)
        {
            remoteUsers.Remove(uid);
            if (RemoteUid == uid)
            {
                RemoteUid = 0;
                foreach (uint other in remoteUsers)
                {
                    RemoteUid = other;
                    break;
                }
            }

            OnRemoteUserLeft?.Invoke(uid);
            if (remoteUsers.Count == 0 && State == StaffCallState.InCall)
            {
                SetState(StaffCallState.WaitingForStaff);
            }
        }

        private void HandleConnectionState(CONNECTION_STATE_TYPE state, CONNECTION_CHANGED_REASON_TYPE reason)
        {
            if (state == CONNECTION_STATE_TYPE.CONNECTION_STATE_FAILED)
            {
                Fail("Connection failed: " + DescribeReason(reason));
                return;
            }

            if (reason == CONNECTION_CHANGED_REASON_TYPE.CONNECTION_CHANGED_INVALID_APP_ID
                || reason == CONNECTION_CHANGED_REASON_TYPE.CONNECTION_CHANGED_INVALID_TOKEN
                || reason == CONNECTION_CHANGED_REASON_TYPE.CONNECTION_CHANGED_TOKEN_EXPIRED
                || reason == CONNECTION_CHANGED_REASON_TYPE.CONNECTION_CHANGED_INVALID_CHANNEL_NAME
                || reason == CONNECTION_CHANGED_REASON_TYPE.CONNECTION_CHANGED_REJECTED_BY_SERVER
                || reason == CONNECTION_CHANGED_REASON_TYPE.CONNECTION_CHANGED_BANNED_BY_SERVER)
            {
                Fail("Cannot join the call: " + DescribeReason(reason));
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Readable errors
        // ------------------------------------------------------------------------------------------------------

        private static string Describe(int code)
        {
            code = Math.Abs(code);
            switch (code)
            {
                case 2: return "invalid argument (2)";
                case 5: return "request refused (5)";
                case 7: return "engine not initialized (7)";
                case 9: return "no permission, check the microphone permission (9)";
                case 14: return "network is down (14)";
                case 17: return "join rejected: you are already in this channel or joining it (17)";
                case 101: return "invalid App ID: check the App ID in AgoraConfig (101)";
                case 102: return "invalid channel name (102)";
                case 109: return "token expired: paste a new token in AgoraConfig (109)";
                case 110: return "invalid token: it does not match the App ID / channel name, or it is mistyped (110)";
                case 112: return "connection lost (112)";
                default: return "Agora error " + code + " (see the Agora error code list)";
            }
        }

        private static string DescribeReason(CONNECTION_CHANGED_REASON_TYPE reason)
        {
            switch (reason)
            {
                case CONNECTION_CHANGED_REASON_TYPE.CONNECTION_CHANGED_INVALID_APP_ID: return "the App ID is invalid. Check the App ID in AgoraConfig.";
                case CONNECTION_CHANGED_REASON_TYPE.CONNECTION_CHANGED_INVALID_CHANNEL_NAME: return "the channel name is invalid.";
                case CONNECTION_CHANGED_REASON_TYPE.CONNECTION_CHANGED_INVALID_TOKEN: return "the token is invalid. It must be generated for this App ID and channel name.";
                case CONNECTION_CHANGED_REASON_TYPE.CONNECTION_CHANGED_TOKEN_EXPIRED: return "the token has expired. Paste a new token in AgoraConfig.";
                case CONNECTION_CHANGED_REASON_TYPE.CONNECTION_CHANGED_REJECTED_BY_SERVER: return "the Agora server rejected the connection (maybe the project requires a token).";
                case CONNECTION_CHANGED_REASON_TYPE.CONNECTION_CHANGED_BANNED_BY_SERVER: return "this user was banned by the server.";
                case CONNECTION_CHANGED_REASON_TYPE.CONNECTION_CHANGED_JOIN_FAILED: return "joining the channel failed (network?).";
                default: return reason.ToString();
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Agora callbacks (any thread) -> main thread
        // ------------------------------------------------------------------------------------------------------

        private class Handler : IRtcEngineEventHandler
        {
            private readonly StaffCallManager owner;

            public Handler(StaffCallManager owner)
            {
                this.owner = owner;
            }

            public override void OnJoinChannelSuccess(RtcConnection connection, int elapsed)
            {
                owner.mainThreadQueue.Enqueue(owner.HandleJoined);
            }

            public override void OnUserJoined(RtcConnection connection, uint remoteUid, int elapsed)
            {
                owner.mainThreadQueue.Enqueue(() => owner.HandleUserJoined(remoteUid));
            }

            public override void OnUserOffline(RtcConnection connection, uint remoteUid, USER_OFFLINE_REASON_TYPE reason)
            {
                owner.mainThreadQueue.Enqueue(() => owner.HandleUserLeft(remoteUid));
            }

            public override void OnConnectionStateChanged(RtcConnection connection, CONNECTION_STATE_TYPE state, CONNECTION_CHANGED_REASON_TYPE reason)
            {
                owner.mainThreadQueue.Enqueue(() => owner.HandleConnectionState(state, reason));
            }

            public override void OnError(int err, string msg)
            {
                owner.mainThreadQueue.Enqueue(() =>
                {
                    owner.lastEngineError = Describe(err) + (string.IsNullOrEmpty(msg) ? string.Empty : " - " + msg);
                    Debug.LogError("[Staff] Agora error: " + owner.lastEngineError);
                });
            }

            public override void OnTokenPrivilegeWillExpire(RtcConnection connection, string token)
            {
                owner.mainThreadQueue.Enqueue(() => Debug.LogWarning("[Staff] The Agora token will expire soon. Paste a fresh token in AgoraConfig before the next call."));
            }

            public override void OnRequestToken(RtcConnection connection)
            {
                owner.mainThreadQueue.Enqueue(() => owner.Fail("The token has expired. Paste a new token in AgoraConfig."));
            }

            public override void OnPermissionError(PERMISSION_TYPE permissionType)
            {
                owner.mainThreadQueue.Enqueue(() => owner.Fail("Agora has no permission for " + permissionType + ". Allow the microphone for this app."));
            }
        }
    }
}
