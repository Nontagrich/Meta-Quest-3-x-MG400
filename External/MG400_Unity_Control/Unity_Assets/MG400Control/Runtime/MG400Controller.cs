using System;
using System.Collections;
using UnityEngine;

namespace CPE494
{
    /// <summary>
    /// High-level MG400 operations used by the packing sequence, teleop
    /// station and UI.  Works in two modes:
    ///   * bridge online  -> commands relayed by bridge_server.py (real or sim)
    ///   * bridge offline -> identical motion simulated locally (LocalSim)
    /// so the scene still demos end-to-end without Python running.
    /// </summary>
    public class MG400Controller : MonoBehaviour
    {
        private static MG400Controller _instance;
        public static MG400Controller Instance
        {
            get
            {
                if (_instance == null) _instance = FindAnyObjectByType<MG400Controller>();
                return _instance;
            }
            private set => _instance = value;
        }

        [Header("Robot safety")]
        [Range(1, 100)] public int maxSpeedPct = 50;      // hard demo cap

        [Header("Local sim fallback (bridge offline)")]
        public float localJointSpeed = 150f;              // deg/s at 100%

        public bool BridgeOnline =>
            MG400BridgeClient.Instance != null && MG400BridgeClient.Instance.IsConnected;

        public bool UsingBridge { get; private set; }
        public bool Halted { get; private set; }

        public MG400DigitalTwin Twin { get; private set; }

        // pending ack waiters: id -> completion flag
        private readonly System.Collections.Generic.Dictionary<int, AckWaiter> _waiters
            = new System.Collections.Generic.Dictionary<int, AckWaiter>();

        private class AckWaiter
        {
            public bool Done;
            public bool Ok;
            public string Msg;
        }

        // local sim state (mirrors SimMG400)
        private Vector4 _localJoints = new Vector4(0f, MG400Geometry.E2, MG400Geometry.E3, 0f);
        private Vector4 _localTargets = new Vector4(0f, MG400Geometry.E2, MG400Geometry.E3, 0f);
        private bool _localVacuum;
        private int _editorTestStepMm = 5;
        private bool _editorTestMoveRunning;
#if UNITY_EDITOR
        private GUIStyle _editorTrackingNoteStyle;
        private string _editorTestMoveError;
        private string _editorWorkspaceNote;
#endif

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Twin = MG400DigitalTwin.Instance;
        }

        private void Start()
        {
            if (Twin == null) Twin = MG400DigitalTwin.Instance;
            EnsureSubscribed();
        }

        private void OnEnable()
        {
            EnsureSubscribed();
        }

        private void OnDisable()
        {
            if (MG400BridgeClient.Instance != null)
                MG400BridgeClient.Instance.OnAck -= HandleAck;
        }

        private void EnsureSubscribed()
        {
            if (MG400BridgeClient.Instance != null)
            {
                MG400BridgeClient.Instance.OnAck -= HandleAck;
                MG400BridgeClient.Instance.OnAck += HandleAck;
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            // keep twin fed: realtime state wins; local sim otherwise
            if (BridgeOnline && MG400BridgeClient.Instance.StateFresh
                && MG400BridgeClient.Instance.LastState is { } st)
            {
                UsingBridge = true;
                Twin.SetTargetJoints(new Vector4(
                    st.Joint(0), st.Joint(1), st.Joint(2), st.Joint(3)));
                Twin.SetVacuum(st.do1 == 1);
            }
            else
            {
                UsingBridge = false;
                StepLocalSim();
                Twin.SetTargetJoints(_localJoints);
                Twin.SetVacuum(_localVacuum);
            }
        }

        // ------------------------------------------------------------------
        // local sim (mirror of python SimMG400)
        // ------------------------------------------------------------------
        private static readonly float[] LocalJointSpeeds = { 150f, 150f, 150f, 300f };

        private void StepLocalSim()
        {
            float scale = maxSpeedPct / 100f;
            Vector4 delta = _localTargets - _localJoints;
            Vector4 abs = new Vector4(Mathf.Abs(delta.x), Mathf.Abs(delta.y),
                                      Mathf.Abs(delta.z), Mathf.Abs(delta.w));
            float worstTime = 0f;
            for (int i = 0; i < 4; i++)
                worstTime = Mathf.Max(worstTime, abs[i] / (LocalJointSpeeds[i] * Mathf.Max(scale, 0.01f)));
            if (worstTime < 0.0001f) return;
            float stepFrac = Mathf.Clamp01(Time.deltaTime / worstTime);
            _localJoints += delta * stepFrac;
        }

        private void HaltLocal()
        {
            _localTargets = _localJoints;
        }

        // ------------------------------------------------------------------
        // ack plumbing
        // ------------------------------------------------------------------
        private void HandleAck(MG400Ack ack)
        {
            if (ack.id > 0 && _waiters.TryGetValue(ack.id, out var w))
            {
                w.Done = true;
                w.Ok = ack.ok;
                w.Msg = ack.msg;
            }
        }

        private IEnumerator SendAndWait(int id, string cmd,
                                        Action<MG400BridgeClient.OutCmd> setup,
                                        float timeout = 30f)
        {
            EnsureSubscribed();
            var waiter = new AckWaiter();
            _waiters[id] = waiter;
            MG400BridgeClient.Instance.Send(id, cmd, setup);
            float deadline = Time.time + timeout;
            while (!waiter.Done && Time.time < deadline && !Halted)
                yield return null;
            _waiters.Remove(id);
            if (Halted)
                throw new OperationCanceledException("robot halted (E-Stop)");
            if (!waiter.Done)
                throw new TimeoutException($"no ack for '{cmd}' within {timeout:F0}s");
            if (!waiter.Ok)
                throw new InvalidOperationException($"'{cmd}' rejected: {waiter.Msg}");
        }

        // ------------------------------------------------------------------
        // public operations (coroutines so sequences can yield on them)
        // ------------------------------------------------------------------
        public IEnumerator Enable()
        {
            if (Halted) yield break;
            if (BridgeOnline)
                yield return SendAndWait(0, "enable", null);
        }

        public IEnumerator Disable()
        {
            if (BridgeOnline)
                yield return SendAndWait(0, "disable", null);
        }

        /// <summary>Send SpeedFactor with an explicit value (safe coroutine).</summary>
        public IEnumerator SpeedFactor(int pct)
        {
            pct = Mathf.Clamp(pct, 1, maxSpeedPct);
            if (!BridgeOnline)
            {
                // offline/local-sim mode: nothing to send, keep the cap locally
                yield break;
            }
            yield return SendAndWait(MG400BridgeClient.Instance.NextId(), "speed",
                c => { c.pct = pct; }, 10f);
        }

        public IEnumerator Home()
        {
            Vector4 home = new Vector4(0f, MG400Geometry.E2 + 35f, MG400Geometry.E3 - 45f, 0f);
            if (BridgeOnline)
            {
                var bridge = MG400BridgeClient.Instance;
                yield return SendAndWait(bridge.NextId(), "home", null);
                if (bridge.SimMode)
                    yield return SendAndWait(bridge.NextId(), "move_joints",
                        c => { c.joints = new double[] { home.x, home.y, home.z, home.w }; });
                yield return SyncWait();
            }
            else
            {
                _localTargets = home;
                yield return WaitLocalArrive();
            }
        }

        /// <summary>Move joints (degrees) and optionally wait for arrival.</summary>
        public IEnumerator MoveJoints(Vector4 joints, int speedPct = 50, bool wait = true)
        {
            if (Halted) throw new OperationCanceledException("robot halted");
            joints = ClampJoints(joints);
            if (BridgeOnline)
            {
                var send = SendAndWait(MG400BridgeClient.Instance.NextId(), "move_joints",
                    c =>
                    {
                        c.joints = new double[] { joints.x, joints.y, joints.z, joints.w };
                        c.speed = speedPct;
                    });
                yield return send;
                if (wait) yield return SyncWait();
            }
            else
            {
                _localTargets = joints;
                if (wait) yield return WaitLocalArrive();
            }
        }

        /// <summary>Cartesian move from the twin's base frame in metres.</summary>
        public IEnumerator MoveTo(Vector3 tcp, int speedPct = 50, bool wait = true, char type = 'L')
        {
            if (Halted) throw new OperationCanceledException("robot halted");
            var bridge = MG400BridgeClient.Instance;
            if (BridgeOnline && !bridge.SimMode)
            {
                // Twin axes are lateral X, vertical Y, forward Z. The
                // MG400 Pose uses forward X, lateral Y, vertical Z in mm.
                yield return MoveCartesianMm(tcp.z * 1000f, tcp.x * 1000f, tcp.y * 1000f, 0f,
                    speedPct, wait, preserveRotation: true);
                yield break;
            }
            tcp = MG400Geometry.ClampWorkspace(tcp);
            Vector3 joints = MG400Geometry.IK(tcp);
            yield return MoveJoints(new Vector4(joints.x, joints.y, joints.z, 0f), speedPct, wait);
        }

        /// <summary>Move to an MG400 base-frame Cartesian pose in millimetres through the live bridge.</summary>
        public IEnumerator MoveCartesianMm(float x, float y, float z, float r,
                                           int speedPct = 20, bool wait = true, bool preserveRotation = false)
        {
            if (Halted) throw new OperationCanceledException("robot halted");
            if (!BridgeOnline)
                throw new InvalidOperationException("MG400 bridge is offline; refusing Cartesian hardware command");

            yield return SpeedFactor(speedPct);
            yield return SendAndWait(MG400BridgeClient.Instance.NextId(), "move_to",
                c =>
                {
                    c.x = x;
                    c.y = y;
                    c.z = z;
                    c.r = r;
                    c.preserveR = preserveRotation;
                    c.type = "L";
                });
            if (wait) yield return SyncWait();
        }

        public IEnumerator CheckPackingWorkspace(params Vector3?[] targets)
        {
            if (!BridgeOnline) yield break;
            var bridge = MG400BridgeClient.Instance;
            if (!bridge.StateFresh || bridge.LastState == null)
                throw new InvalidOperationException("Bridge robot status is unavailable");
            if (bridge.SimMode) yield break;

            var path = new MG400BridgeClient.WorkspacePoint[targets.Length];
            for (int index = 0; index < targets.Length; index++)
            {
                var point = targets[index];
                path[index] = point.HasValue
                    ? new MG400BridgeClient.WorkspacePoint { x = point.Value.z * 1000f, y = point.Value.x * 1000f }
                    : new MG400BridgeClient.WorkspacePoint { park = true };
            }
            yield return SendAndWait(bridge.NextId(), "check_packing_workspace", c => c.path = path, 10f);
        }

#if UNITY_EDITOR
        private void OnGUI()
        {
            if (!Application.isPlaying) return;

            MG400State state;
            string status;
            bool ready = EditorPanelReady(out state, out status);
            var teleop = TeleopStation.Instance;
            bool showControllerOffset = teleop != null && teleop.ControllerTrackingLive && !teleop.PreparingControllerPose;
            string panelNote = _editorTestMoveError ?? _editorWorkspaceNote ?? teleop?.TrackingStopReason;
            bool showPanelNote = !string.IsNullOrEmpty(panelNote);

            const float panelWidth = 320f;
            float panelHeight = 196f + (showPanelNote ? 59f : 0f) + (showControllerOffset ? 23f : 0f);
            GUILayout.BeginArea(new Rect(Mathf.Max(8f, Screen.width - panelWidth - 12f), 12f,
                                         panelWidth, panelHeight), GUI.skin.box);
            GUILayout.Label($"MG400 TEST: {status}");
            if (state != null && state.pose != null && state.pose.Length >= 4)
                GUILayout.Label($"X {state.pose[0]:F1}   Y {state.pose[1]:F1}   Z {state.pose[2]:F1} mm   R {state.pose[3]:F1}°");
            else
                GUILayout.Label("Pose: unavailable");

            if (showControllerOffset)
            {
                Vector2 offset = teleop.ControllerOffsetMm;
                string edge = teleop.ControllerAtWorkspaceEdge ? "  |  XY EDGE" : "";
                GUILayout.Label($"Controller ΔX {offset.x:F1}   ΔY {offset.y:F1} mm{edge}");
            }

            if (showPanelNote)
            {
                _editorTrackingNoteStyle ??= new GUIStyle(GUI.skin.label) { wordWrap = true };
                GUILayout.Label(panelNote, _editorTrackingNoteStyle);
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Step", GUILayout.Width(38f));
            if (GUILayout.Toggle(_editorTestStepMm == 1, "1 mm", GUI.skin.button)) _editorTestStepMm = 1;
            if (GUILayout.Toggle(_editorTestStepMm == 5, "5 mm", GUI.skin.button)) _editorTestStepMm = 5;
            if (GUILayout.Toggle(_editorTestStepMm == 10, "10 mm", GUI.skin.button)) _editorTestStepMm = 10;
            GUILayout.EndHorizontal();

            bool previousGuiEnabled = GUI.enabled;
            GUI.enabled = previousGuiEnabled && ready;
            DrawEditorAxisRow("X", 0, _editorTestStepMm);
            DrawEditorAxisRow("Y", 1, _editorTestStepMm);
            DrawEditorAxisRow("Z", 2, _editorTestStepMm);
            GUI.enabled = previousGuiEnabled;
            GUILayout.EndArea();
        }

        private bool EditorPanelReady(out MG400State state, out string status)
        {
            var bridge = MG400BridgeClient.Instance;
            state = bridge != null ? bridge.LastState : null;

            if (bridge == null || !BridgeOnline)
            {
                status = "BRIDGE OFFLINE";
                return false;
            }

            if (bridge.SimMode)
            {
                status = "SIM MODE - MOVES DISABLED";
                return false;
            }

            if (!bridge.StateFresh)
            {
                status = "WAITING FOR FRESH STATE";
                return false;
            }

            if (state == null || state.pose == null || state.pose.Length < 4)
            {
                status = "POSE UNAVAILABLE";
                return false;
            }

            if (state.enabled != 1)
            {
                status = "ROBOT DISABLED";
                return false;
            }

            if (Halted)
            {
                status = "E-STOPPED";
                return false;
            }

            var teleop = TeleopStation.Instance;
            if (teleop != null && teleop.ControllerTrackingLive)
            {
                status = teleop.PreparingControllerPose ? "PREPARING CONTROLLER" : "CONTROLLER ACTIVE";
                return false;
            }

            if (state.moving || _editorTestMoveRunning)
            {
                status = "MOVING...";
                return false;
            }

            if (_editorTestMoveError != null)
            {
                status = "MOVE RESPONSE FAILED";
                return false;
            }

            if (state.outsideWorkspace)
            {
                status = "OUTSIDE XY WORKSPACE - USE T";
                return false;
            }

            status = teleop != null && teleop.ControllerPausedForFocus
                ? "CONTROLLER PAUSED" : "REAL ROBOT READY";
            return true;
        }

        private void DrawEditorAxisRow(string axis, int axisIndex, int stepMm)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(axis, GUILayout.Width(38f));
            if (GUILayout.Button("-", GUILayout.Height(27f)))
                StartCoroutine(EditorStepMove(axisIndex, -stepMm));
            if (GUILayout.Button("+", GUILayout.Height(27f)))
                StartCoroutine(EditorStepMove(axisIndex, stepMm));
            GUILayout.EndHorizontal();
        }

        /// <summary>Issue one bounded, low-speed Cartesian step from fresh real telemetry.</summary>
        private IEnumerator EditorStepMove(int axisIndex, int deltaMm)
        {
            MG400State state;
            string status;
            if (_editorTestMoveRunning)
            {
                Debug.LogWarning("[Unity MG400 Panel] move rejected: another move is already running");
                yield break;
            }

            if (!EditorPanelReady(out state, out status))
            {
                Debug.LogWarning($"[Unity MG400 Panel] move rejected: {status}");
                yield break;
            }

            double x = state.pose[0];
            double y = state.pose[1];
            double z = state.pose[2];
            double r = state.pose[3];
            if (axisIndex == 0) x += deltaMm;
            else if (axisIndex == 1) y += deltaMm;
            else if (axisIndex == 2) z += deltaMm;
            else
            {
                Debug.LogError($"[Unity MG400 Panel] invalid axis index {axisIndex}");
                yield break;
            }

            _editorWorkspaceNote = null;
            _editorTestMoveRunning = true;
            string axisName = axisIndex == 0 ? "X" : axisIndex == 1 ? "Y" : "Z";
            Debug.Log($"[Unity MG400 Panel] sending {axisName}{deltaMm:+#;-#;0} mm step to ({x:F1}, {y:F1}, {z:F1}, {r:F1}) mm");
            var routines = new System.Collections.Generic.Stack<IEnumerator>();
            routines.Push(MoveCartesianMm((float)x, (float)y, (float)z, (float)r));
            try
            {
                // Drive nested command waits here so an exception in a child
                // coroutine is shown on the panel and always releases MOVING.
                while (routines.Count > 0)
                {
                    var current = routines.Peek();
                    object instruction = null;
                    bool hasNext = false;
                    bool failed = false;
                    try
                    {
                        hasNext = current.MoveNext();
                        if (hasNext) instruction = current.Current;
                    }
                    catch (Exception error)
                    {
                        failed = true;
                        if (error.Message.Contains("Workspace limit:", StringComparison.Ordinal))
                            _editorWorkspaceNote = error.Message;
                        else
                            _editorTestMoveError = $"{error.Message}. Exit Play and reopen Bridge before retrying.";
                        Debug.LogWarning($"[Unity MG400 Panel] {_editorTestMoveError ?? _editorWorkspaceNote}");
                    }

                    if (failed) yield break;
                    if (!hasNext) (routines.Pop() as IDisposable)?.Dispose();
                    else if (instruction is IEnumerator nested) routines.Push(nested);
                    else yield return instruction;
                }
                Debug.Log($"[Unity MG400 Panel] move completed: ({x:F1}, {y:F1}, {z:F1}, {r:F1}) mm");
            }
            finally
            {
                while (routines.Count > 0) (routines.Pop() as IDisposable)?.Dispose();
                _editorTestMoveRunning = false;
            }
        }
#endif

        public IEnumerator SyncWait(float timeout = 30f)
        {
            if (BridgeOnline)
                yield return SendAndWait(MG400BridgeClient.Instance.NextId(), "sync", null, timeout);
            else
                yield return WaitLocalArrive(timeout);
        }

        public IEnumerator SetVacuum(bool on)
        {
            if (Halted) throw new OperationCanceledException("robot halted");
            if (BridgeOnline)
                yield return SendAndWait(MG400BridgeClient.Instance.NextId(), "do",
                    c => { c.index = 1; c.value = on ? 1 : 0; }, 10f);
            _localVacuum = on;
        }

        public IEnumerator Raw(string text)
        {
            if (!BridgeOnline) yield break;
            yield return SendAndWait(MG400BridgeClient.Instance.NextId(), "raw",
                c => { c.text = text; });
        }

        /// <summary>Update the simulator using a verb that cannot control hardware.</summary>
        public void PreviewTeleop(Vector4 joints)
        {
            if (Halted) return;
            joints = ClampJoints(joints);
            if (BridgeOnline)
            {
                MG400BridgeClient.Instance.Send("teleop_sim",
                    c => c.joints = new double[] { joints.x, joints.y, joints.z, joints.w });
            }
            _localTargets = joints;
        }

        public bool IsMoving =>
            !Halted &&
            (BridgeOnline
                ? (MG400BridgeClient.Instance.LastState is { } st && st.moving)
                : Vector4Distance(_localJoints, _localTargets) > 0.5f);

        private static float Vector4Distance(Vector4 a, Vector4 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z, dw = a.w - b.w;
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw);
        }

        private IEnumerator WaitLocalArrive(float timeout = 30f)
        {
            float deadline = Time.time + timeout;
            while (Vector4Distance(_localJoints, _localTargets) > 0.5f)
            {
                if (Halted) throw new OperationCanceledException("robot halted (E-Stop)");
                if (Time.time > deadline)
                    throw new TimeoutException("local sim move timeout");
                yield return null;
            }
        }

        private static Vector4 ClampJoints(Vector4 j)
        {
            return new Vector4(
                Mathf.Clamp(j.x, -140f, 140f),
                Mathf.Clamp(j.y, -30f, 210f),
                Mathf.Clamp(j.z, -100f, 240f),
                Mathf.Clamp(j.w, -185f, 185f));
        }

        // ------------------------------------------------------------------
        // safety
        // ------------------------------------------------------------------
        public void EStop()
        {
            Halted = true;
            HaltLocal();
            StartCoroutine(EStopRoutine());
        }

        private IEnumerator EStopRoutine()
        {
            if (BridgeOnline)
            {
                MG400BridgeClient.Instance.Send("speed", c => { c.pct = 10; });
                MG400BridgeClient.Instance.Send("disable", null);
            }
            yield return null;
        }

        public void ResetFromStop()
        {
            Halted = false;
        }
    }
}
