using GemStore.Payment;
using UnityEngine;

namespace GemStore.Network
{
    /// <summary>Publishes every paid order as JSON on "{prefix}/order/new" (QoS 1) for the robot arm.</summary>
    [DisallowMultipleComponent]
    public class OrderPublisher : MonoBehaviour
    {
        public const string TopicSuffix = "order/new";

        [SerializeField] private MqttService mqtt;

        private void OnEnable()
        {
            OrderEvents.OnOrderPaid += HandleOrderPaid;
        }

        private void OnDisable()
        {
            OrderEvents.OnOrderPaid -= HandleOrderPaid;
        }

        private void HandleOrderPaid(Order order)
        {
            MqttService service = mqtt != null ? mqtt : MqttService.Instance;
            if (service == null)
            {
                Debug.LogWarning($"[MQTT] Order {order.orderId} was paid but there is no MqttService in the scene, so it was not sent.");
                return;
            }

            service.Publish(service.Topic(TopicSuffix), order.ToJson(), 1, false);
        }
    }
}
