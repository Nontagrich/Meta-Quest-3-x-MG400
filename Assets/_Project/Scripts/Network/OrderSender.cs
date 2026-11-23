using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GemStore.Customize;
using GemStore.Payment;
using UnityEngine;

namespace GemStore.Network
{
    /// <summary>
    /// Sends every paid order to the robot over <see cref="RobotLink"/> and makes sure none is lost:
    /// orders wait in a queue (also saved to disk) until the robot is connected, and each one is resent every
    /// <see cref="ackTimeoutSeconds"/> until {"type":"ack","orderId":"..."} arrives (max <see cref="maxAttempts"/> tries per connection).
    /// Message: {"type":"order","orderId":"1234","items":[{"gemId":"gem_ruby_round","shape":"round","color":"red","setting":"ring","metal":"gold"}]}
    /// </summary>
    [DisallowMultipleComponent]
    public class OrderSender : MonoBehaviour
    {
        [Serializable]
        private class AckProbe
        {
            public string orderId;
        }

        private class PendingOrder
        {
            public string orderId;
            public string json;
            public int attempts;
            public float lastSentTime;
            public bool gaveUp; // 3 unanswered tries on this connection; tried again after the next reconnect
        }

        [SerializeField, Min(1f)] private float ackTimeoutSeconds = 5f;
        [SerializeField, Min(1)] private int maxAttempts = 3;

        private readonly List<PendingOrder> pending = new List<PendingOrder>();
        private RobotLink link;

        /// <summary>Orders that were paid but not acknowledged by the robot yet.</summary>
        public int PendingCount => pending.Count;

        private static string SavePath => Path.Combine(Application.persistentDataPath, "pending_robot_orders.txt");

        private void OnEnable()
        {
            OrderEvents.OnOrderPaid += HandleOrderPaid;
            Attach();
        }

        private void Start()
        {
            Attach();
            LoadSaved();
        }

        private void OnDisable()
        {
            OrderEvents.OnOrderPaid -= HandleOrderPaid;
            if (link != null)
            {
                link.OnMessage -= HandleMessage;
                link.OnConnectionChanged -= HandleConnectionChanged;
                link = null;
            }
        }

        private void Attach()
        {
            if (link != null || RobotLink.Instance == null)
            {
                return;
            }

            link = RobotLink.Instance;
            link.OnMessage += HandleMessage;
            link.OnConnectionChanged += HandleConnectionChanged;
        }

        private void Update()
        {
            if (link == null)
            {
                Attach();
            }

            if (link == null || !link.IsConnected)
            {
                return;
            }

            foreach (PendingOrder order in pending)
            {
                if (order.gaveUp)
                {
                    continue;
                }

                if (order.attempts == 0 || Time.unscaledTime - order.lastSentTime >= ackTimeoutSeconds)
                {
                    if (order.attempts >= maxAttempts)
                    {
                        order.gaveUp = true;
                        Debug.LogError($"[Robot] Order {order.orderId}: no ack after {maxAttempts} tries. It stays queued and is sent again after the next reconnect.");
                        continue;
                    }

                    Send(order);
                }
            }
        }

        private void Send(PendingOrder order)
        {
            order.attempts++;
            order.lastSentTime = Time.unscaledTime;
            if (link.SendRaw(order.json))
            {
                Debug.Log($"[Robot] Order {order.orderId} sent (try {order.attempts}/{maxAttempts}).");
            }
        }

        private void HandleOrderPaid(Order order)
        {
            pending.Add(new PendingOrder { orderId = order.orderId, json = BuildJson(order) });
            Save();
            if (link == null || !link.IsConnected)
            {
                Debug.LogWarning($"[Robot] Order {order.orderId} is queued: the robot is not connected yet.");
            }
        }

        private void HandleConnectionChanged(bool connected)
        {
            if (!connected)
            {
                return;
            }

            // fresh connection: everything that was not acknowledged gets a fresh set of tries
            foreach (PendingOrder order in pending)
            {
                order.attempts = 0;
                order.gaveUp = false;
            }
        }

        private void HandleMessage(string type, string json)
        {
            if (type != "ack")
            {
                return;
            }

            AckProbe ack;
            try
            {
                ack = JsonUtility.FromJson<AckProbe>(json);
            }
            catch (ArgumentException)
            {
                return;
            }

            if (ack == null || string.IsNullOrEmpty(ack.orderId))
            {
                return;
            }

            int removed = pending.RemoveAll(o => o.orderId == ack.orderId);
            if (removed > 0)
            {
                Debug.Log($"[Robot] Order {ack.orderId} acknowledged.");
                Save();
            }
        }

        /// <summary>The wire format: only what the robot needs to pick the gem; shape and color are lowercase.</summary>
        public static string BuildJson(Order order)
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"order\",\"orderId\":\"").Append(Escape(order.orderId)).Append("\",\"items\":[");
            for (int i = 0; i < order.items.Count; i++)
            {
                OrderItem item = order.items[i];
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"gemId\":\"").Append(Escape(item.gemId))
                  .Append("\",\"shape\":\"").Append(item.shape.ToString().ToLowerInvariant())
                  .Append("\",\"color\":\"").Append(item.color.ToString().ToLowerInvariant())
                  .Append("\",\"setting\":\"").Append(JewelryNames.Wire(item.setting))
                  .Append("\",\"metal\":\"").Append(JewelryNames.Wire(item.metal, item.setting))
                  .Append("\"}");
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // ---- persistence: unsent orders survive an app restart --------------------------------------------------

        private void Save()
        {
            try
            {
                var lines = new List<string>();
                foreach (PendingOrder order in pending)
                {
                    lines.Add(order.orderId + "\t" + order.json);
                }

                File.WriteAllLines(SavePath, lines);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Robot] Could not save the pending orders: " + exception.Message);
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
                    int tab = line.IndexOf('\t');
                    if (tab <= 0)
                    {
                        continue;
                    }

                    string id = line.Substring(0, tab);
                    if (!pending.Exists(o => o.orderId == id))
                    {
                        pending.Add(new PendingOrder { orderId = id, json = line.Substring(tab + 1) });
                    }
                }

                if (pending.Count > 0)
                {
                    Debug.LogWarning($"[Robot] {pending.Count} order(s) from an earlier run were not acknowledged and will be sent again.");
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Robot] Could not read the pending orders: " + exception.Message);
            }
        }
    }
}
