using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using CPE494;
using GemStore.Payment;
using GemStore.Teleop;
using UnityEngine;

namespace GemStore.Network
{
    /// <summary>
    /// Runs every paid order on the MG400 through the teammate's Python bridge (<see cref="MG400BridgeClient"/>, TCP 5555).
    /// Replaces <see cref="OrderSender"/> + <see cref="RobotLink"/> for orders. Per item (W, drop zone, safe Z and limits come
    /// from the bridge's get_workspace, never from constants here):
    /// get_workspace, speed, L pick(x,y,safeZ), sync, L pickZ, sync, DO on, wait, L safeZ, sync, L W(safeZ), sync,
    /// J drop(x,y,safeZ) [bridge caps speed], sync, L dropZ, sync, DO off, wait, L safeZ, sync, J W, sync.
    /// After the last item the arm stays at W. "sync" (not state.moving) detects arrival.
    /// Any ok:false, exception or timeout turns the suction off, stops, and reports status "error" with the bridge message.
    /// E-STOP (<see cref="EmergencyStop"/>), a bridge disconnect or an active Maintenance teleop abort at once.
    /// Orders are queued (and saved to disk) while the robot is offline and only run when the bridge reports the REAL robot
    /// (state.sim == false), unless the Editor-only "allowSim" flag is on.
    /// </summary>
    [DisallowMultipleComponent]
    public class RobotOrderRunner : MonoBehaviour
    {
        [Serializable]
        public class PickPoint
        {
            public string gemId;
            [Tooltip("Robot base frame, mm")] public float x;
            public float y;
            public float pickZ = -77.18f;
            public float r = -60f;
        }

        [Header("Robot poses (taught, base frame mm / degrees)")]
        [SerializeField] private List<PickPoint> pickPoints = new List<PickPoint>();
        [SerializeField] private float dropR = -53.26f;

        [Header("Motion")]
        [SerializeField, Range(1, 100)] private int speedPercent = 20;
        [SerializeField, Min(0f)] private float gripWait = 1.0f;
        [SerializeField, Min(1f)] private float ackTimeout = 10f;
        [SerializeField, Min(1f)] private float syncTimeout = 90f;

        [Header("Development")]
        [Tooltip("Editor only: run on the bridge's simulator. Ignored in builds.")]
        [SerializeField] private bool allowSim;

        [Serializable]
        private class DropZoneInfo
        {
            public float centerX, centerY, radius, dropZ, minZ, safeZ, waypointX, waypointY;
            public int maxSpeedPct;
        }

        [Serializable]
        private class WorkspaceInfo
        {
            public float minRadius, maxRadius;
            public bool sim, estopped;
            public DropZoneInfo dropZone;
        }

        private class Reply
        {
            public bool done, ok;
            public string msg;
        }

        private class QueuedOrder
        {
            public string orderId;
            public List<string> gemIds = new List<string>();
        }

        public static RobotOrderRunner Instance { get; private set; }

        private readonly List<QueuedOrder> queue = new List<QueuedOrder>();
        private readonly Dictionary<int, Reply> replies = new Dictionary<int, Reply>();
        private MG400BridgeClient client;
        private Coroutine running;
        private string failure;          // set by a failed step; ends the sequence
        private string abortReason;      // set from Update (E-STOP, disconnect, teleop)

        /// <summary>Orders waiting for the robot (not started yet).</summary>
        public int QueuedCount => queue.Count;
        public bool Busy => running != null;
        public bool EStopLatched { get; private set; }
        public string LastError { get; private set; } = string.Empty;
        public event Action OnStateChanged;

        public bool AllowSim
        {
            get
            {
#if UNITY_EDITOR
                return allowSim;
#else
                return false;
#endif
            }
        }

        private static string SavePath => Path.Combine(Application.persistentDataPath, "pending_runner_orders.txt");

        private void Awake()
        {
            Instance = this;
        }

        private void OnEnable()
        {
            OrderEvents.OnOrderPaid += HandleOrderPaid;
            OrderStatusEvents.IsListening = true;
        }

        private void Start()
        {
            LoadSaved();
        }

        private void OnDisable()
        {
            OrderEvents.OnOrderPaid -= HandleOrderPaid;
            if (client != null)
            {
                client.OnAck -= HandleAck;
                client = null;
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ---- public API --------------------------------------------------------------------------------------------

        /// <summary>Emergency stop: sends "estop" to the bridge (it stops the arm and turns the suction off) and aborts the order.</summary>
        public void EmergencyStop(string source = "button")
        {
            EStopLatched = true;
            abortReason = "E-STOP (" + source + ")";
            if (Attach() && client.IsConnected)
            {
                client.Send("estop");
            }

            Debug.LogWarning("[RobotOrder] " + abortReason);
            OnStateChanged?.Invoke();
        }

        /// <summary>Clears the E-STOP latch (after checking the robot). Queued orders start again.</summary>
        public void ResetEStop()
        {
            if (!EStopLatched)
            {
                return;
            }

            EStopLatched = false;
            if (Attach() && client.IsConnected)
            {
                client.Send("estop_reset");
            }

            OnStateChanged?.Invoke();
        }

        public bool CanRun(out string reason)
        {
            if (!Attach() || !client.IsConnected)
            {
                reason = "bridge not connected";
                return false;
            }

            if (!client.StateFresh)
            {
                reason = "no fresh robot state";
                return false;
            }

            if (client.SimMode && !AllowSim)
            {
                reason = "bridge is in simulator mode (real robot required)";
                return false;
            }

            if (EStopLatched)
            {
                reason = "E-STOP latched";
                return false;
            }

            if (TeleopBusy())
            {
                reason = "Maintenance teleop active";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        // ---- queue -------------------------------------------------------------------------------------------------

        private void HandleOrderPaid(Order order)
        {
            var queued = new QueuedOrder { orderId = order.orderId };
            foreach (OrderItem item in order.items)
            {
                queued.gemIds.Add(item.gemId);
            }

            queue.Add(queued);
            Save();
            if (!CanRun(out string reason))
            {
                Debug.LogWarning($"[RobotOrder] Order {order.orderId} queued: {reason}.");
            }

            OnStateChanged?.Invoke();
        }

        private void Update()
        {
            Attach();
            if (running != null)
            {
                if (abortReason == null)
                {
                    if (client == null || !client.IsConnected)
                    {
                        abortReason = "bridge disconnected";
                    }
                    else if (TeleopBusy())
                    {
                        abortReason = "Maintenance teleop became active";
                    }
                }

                return;
            }

            if (queue.Count > 0 && CanRun(out _))
            {
                abortReason = null;
                failure = null;
                running = StartCoroutine(RunOrder(queue[0]));
            }
        }

        private bool Attach()
        {
            if (client == null)
            {
                client = MG400BridgeClient.Instance;
                if (client != null)
                {
                    client.OnAck += HandleAck;
                }
            }

            return client != null;
        }

        private static bool TeleopBusy()
        {
            TeleopController teleop = TeleopController.Instance;
            return teleop != null && (teleop.Following || teleop.MacroActive || teleop.EStopLocked);
        }

        private void HandleAck(MG400Ack ack)
        {
            if (replies.TryGetValue(ack.id, out Reply reply))
            {
                reply.done = true;
                reply.ok = ack.ok;
                reply.msg = ack.msg;
            }
        }

        // ---- the sequence ------------------------------------------------------------------------------------------

        private IEnumerator RunOrder(QueuedOrder order)
        {
            Notify(order.orderId, OrderStatus.Picking, string.Empty);
            for (int i = 0; i < order.gemIds.Count && failure == null; i++)
            {
                yield return RunItem(order, i);
            }

            if (failure != null || abortReason != null)
            {
                string why = failure ?? abortReason;
                yield return Cleanup();
                LastError = why;
                Debug.LogError($"[RobotOrder] Order {order.orderId} failed: {why}");
                Notify(order.orderId, OrderStatus.Error, why);
            }
            else
            {
                Debug.Log($"[RobotOrder] Order {order.orderId} done.");
                Notify(order.orderId, OrderStatus.Done, string.Empty);
            }

            queue.Remove(order);
            Save();
            running = null;
            OnStateChanged?.Invoke();
        }

        private IEnumerator RunItem(QueuedOrder order, int index)
        {
            string gemId = order.gemIds[index];
            PickPoint pick = pickPoints.Find(p => p != null && p.gemId == gemId);
            if (pick == null)
            {
                failure = $"no taught pick pose for '{gemId}'";
                yield break;
            }

            // 1. limits and poses from the bridge
            var info = new WorkspaceInfo();
            yield return Step("get_workspace", "get_workspace", null, ackTimeout, reply =>
            {
                try
                {
                    JsonUtility.FromJsonOverwrite(reply.msg, info);
                }
                catch (ArgumentException)
                {
                    failure = "get_workspace: unreadable reply";
                }
            });
            if (failure != null)
            {
                yield break;
            }

            DropZoneInfo drop = info.dropZone;
            if (drop == null)
            {
                failure = "bridge has no dropZone configured";
                yield break;
            }

            if (info.estopped)
            {
                failure = "bridge reports E-STOP active";
                yield break;
            }

            float pickRadius = Mathf.Sqrt(pick.x * pick.x + pick.y * pick.y);
            if (pickRadius < info.minRadius || pickRadius > info.maxRadius)
            {
                failure = $"pick pose for '{gemId}' is at radius {pickRadius:F0} mm, outside the bridge limits {info.minRadius:F0}..{info.maxRadius:F0}";
                yield break;
            }

            float safeZ = drop.safeZ;
            Debug.Log($"[RobotOrder] Order {order.orderId} item {index + 1}/{order.gemIds.Count} ({gemId}): safeZ {safeZ:F1}, W ({drop.waypointX:F1}, {drop.waypointY:F1}).");

            // 2. speed
            yield return Step("speed", "speed", c => c.pct = speedPercent, ackTimeout);

            // 3. to the gem and pick it up
            yield return Move("L", pick.x, pick.y, safeZ, pick.r);
            yield return Move("L", pick.x, pick.y, pick.pickZ, pick.r);
            yield return Suction(true);
            yield return Wait(gripWait);
            yield return Move("L", pick.x, pick.y, safeZ, pick.r);
            if (index == order.gemIds.Count - 1 && failure == null && abortReason == null)
            {
                Notify(order.orderId, OrderStatus.Packing, string.Empty);
            }

            // 4. carry to W, into the drop zone, release
            yield return Move("L", drop.waypointX, drop.waypointY, safeZ, dropR);
            yield return Move("J", drop.centerX, drop.centerY, safeZ, dropR);
            yield return Move("L", drop.centerX, drop.centerY, drop.dropZ, dropR);
            yield return Suction(false);
            yield return Wait(gripWait);
            yield return Move("L", drop.centerX, drop.centerY, safeZ, dropR);
            yield return Move("J", drop.waypointX, drop.waypointY, safeZ, dropR);
        }

        private IEnumerator Move(string type, float x, float y, float z, float r)
        {
            if (failure != null || abortReason != null)
            {
                yield break;
            }

            yield return Step($"move_to {type} ({x:F1}, {y:F1}, {z:F1})", "move_to", c =>
            {
                c.x = x;
                c.y = y;
                c.z = z;
                c.r = r;
                c.type = type;
            }, ackTimeout);
            yield return Step("sync", "sync", null, syncTimeout);
        }

        private IEnumerator Suction(bool on)
        {
            yield return Step(on ? "suction on" : "suction off", "do", c =>
            {
                c.index = 1;
                c.value = on ? 1 : 0;
            }, ackTimeout);
        }

        private IEnumerator Wait(float seconds)
        {
            float end = Time.unscaledTime + seconds;
            while (Time.unscaledTime < end && failure == null && abortReason == null)
            {
                yield return null;
            }
        }

        /// <summary>One bridge command: waits for its ack; any failure, timeout or abort ends the whole sequence.</summary>
        private IEnumerator Step(string label, string cmd, Action<MG400BridgeClient.OutCmd> setup, float timeout, Action<Reply> onOk = null)
        {
            if (failure != null || abortReason != null)
            {
                yield break;
            }

            var reply = new Reply();
            int id = client.NextId();
            replies[id] = reply;
            client.Send(id, cmd, setup);
            float end = Time.unscaledTime + timeout;
            while (!reply.done && Time.unscaledTime < end && abortReason == null)
            {
                if (!client.IsConnected)
                {
                    abortReason = "bridge disconnected";
                }

                yield return null;
            }

            replies.Remove(id);
            if (abortReason != null)
            {
                yield break;
            }

            if (!reply.done)
            {
                failure = $"{label}: timeout after {timeout:F0}s";
            }
            else if (!reply.ok)
            {
                failure = $"{label}: bridge said \"{reply.msg}\"";
            }
            else
            {
                onOk?.Invoke(reply);
            }
        }

        /// <summary>Suction off, best effort, after any failure or abort.</summary>
        private IEnumerator Cleanup()
        {
            if (client != null && client.IsConnected)
            {
                client.Send(client.NextId(), "do", c =>
                {
                    c.index = 1;
                    c.value = 0;
                });
            }

            yield return null;
        }

        private void Notify(string orderId, OrderStatus status, string message)
        {
            var wire = new OrderStatusMessage { orderId = orderId, status = OrderStatusMessage.ToWire(status), message = message };
            OrderStatusEvents.Notify(wire, status);
        }

        // ---- persistence: queued orders survive an app restart ----------------------------------------------------------

        private void Save()
        {
            try
            {
                var lines = new List<string>();
                foreach (QueuedOrder order in queue)
                {
                    lines.Add(order.orderId + "\t" + string.Join(",", order.gemIds));
                }

                File.WriteAllLines(SavePath, lines);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[RobotOrder] Could not save the queue: " + exception.Message);
            }
        }

        private void LoadSaved()
        {
            try
            {
                if (!File.Exists(SavePath))
                {
                    return;
                }

                foreach (string line in File.ReadAllLines(SavePath))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length != 2 || queue.Exists(o => o.orderId == parts[0]))
                    {
                        continue;
                    }

                    var queued = new QueuedOrder { orderId = parts[0] };
                    queued.gemIds.AddRange(parts[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                    queue.Add(queued);
                }

                if (queue.Count > 0)
                {
                    Debug.LogWarning($"[RobotOrder] {queue.Count} order(s) from an earlier run are waiting for the robot.");
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[RobotOrder] Could not read the queue: " + exception.Message);
            }
        }
    }
}
