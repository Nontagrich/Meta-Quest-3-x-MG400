using System.Collections;
using GemStore.Payment;
using UnityEngine;

namespace GemStore.Network
{
    /// <summary>
    /// Subscribes to "{prefix}/order/status" (QoS 1) and forwards valid status messages to <see cref="OrderStatusEvents"/>,
    /// where the payment terminal shows them. Message: {"orderId":"1234","status":"picking|packing|done|error","message":""}.
    /// </summary>
    [DisallowMultipleComponent]
    public class OrderStatusListener : MonoBehaviour
    {
        public const string TopicSuffix = "order/status";

        [SerializeField] private MqttService mqtt;

        private MqttService subscribedService;

        /// <summary>Full topic this listener is subscribed to.</summary>
        public string StatusTopic
        {
            get
            {
                MqttService service = mqtt != null ? mqtt : MqttService.Instance;
                return service != null ? service.Topic(TopicSuffix) : "gemstore/" + TopicSuffix;
            }
        }

        private void OnEnable()
        {
            OrderStatusEvents.IsListening = true;
            Attach();
        }

        private void Start()
        {
            // The MqttService may have been created after this component enabled.
            if (subscribedService == null)
            {
                Attach();
            }
        }

        private void OnDisable()
        {
            OrderStatusEvents.IsListening = false;
            if (subscribedService != null)
            {
                subscribedService.MessageReceived -= HandleMessage;
                subscribedService = null;
            }
        }

        private void Attach()
        {
            MqttService service = mqtt != null ? mqtt : MqttService.Instance;
            if (service == null || subscribedService == service)
            {
                return;
            }

            subscribedService = service;
            service.MessageReceived += HandleMessage;
            service.Subscribe(service.Topic(TopicSuffix), 1);
        }

        private void HandleMessage(string topic, string payload)
        {
            if (topic == StatusTopic)
            {
                HandleStatusJson(payload);
            }
        }

        /// <summary>Parses a status JSON and forwards it. Also used for local injection when there is no broker.</summary>
        public void HandleStatusJson(string json)
        {
            OrderStatusMessage message;
            try
            {
                message = JsonUtility.FromJson<OrderStatusMessage>(json);
            }
            catch (System.ArgumentException exception)
            {
                Debug.LogWarning($"[MQTT] Ignored status message that is not valid JSON ({exception.Message}): {json}");
                return;
            }

            if (message == null || string.IsNullOrEmpty(message.orderId) || !message.TryGetStatus(out OrderStatus status))
            {
                Debug.LogWarning($"[MQTT] Ignored status message with a missing orderId or unknown status: {json}");
                return;
            }

            Debug.Log($"[MQTT] Order {message.orderId} status: {message.status} {(string.IsNullOrEmpty(message.message) ? string.Empty : "(" + message.message + ")")}");
            OrderStatusEvents.Notify(message, status);
        }

#if UNITY_EDITOR
        // ---- Editor-only debug helpers: test the whole flow without the real robot ----------------------------------

        /// <summary>
        /// Sends one status for an order. If the broker connection is up, the message goes out on the status topic and comes
        /// back through the subscription (the same path the robot uses). Otherwise it is injected locally.
        /// </summary>
        public void SimulateStatus(string orderId, OrderStatus status, string message = "")
        {
            string json = JsonUtility.ToJson(new OrderStatusMessage { orderId = orderId, status = OrderStatusMessage.ToWire(status), message = message });
            MqttService service = mqtt != null ? mqtt : MqttService.Instance;
            if (service != null && service.IsConnected)
            {
                service.Publish(service.Topic(TopicSuffix), json, 1, false);
            }
            else
            {
                Debug.Log("[MQTT] (simulated, no broker connection) " + json);
                HandleStatusJson(json);
            }
        }

        /// <summary>Sends picking, packing, done for the latest order, <paramref name="secondsBetween"/> apart.</summary>
        public void SimulateSequence(float secondsBetween = 2f)
        {
            PaymentTerminal terminal = FindAnyObjectByType<PaymentTerminal>();
            if (terminal == null || terminal.LastOrder == null)
            {
                Debug.LogWarning("[MQTT] Simulate Robot Status: there is no paid order yet. Buy something first.");
                return;
            }

            StartCoroutine(RunSequence(terminal.LastOrder.orderId, secondsBetween));
        }

        private IEnumerator RunSequence(string orderId, float secondsBetween)
        {
            SimulateStatus(orderId, OrderStatus.Picking);
            yield return new WaitForSeconds(secondsBetween);
            SimulateStatus(orderId, OrderStatus.Packing);
            yield return new WaitForSeconds(secondsBetween);
            SimulateStatus(orderId, OrderStatus.Done);
        }
#endif
    }
}
