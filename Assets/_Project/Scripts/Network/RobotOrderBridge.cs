using GemStore.Payment;
using UnityEngine;

namespace GemStore.Network
{
    /// <summary>
    /// Receives {"type":"status","orderId":"1234","status":"picking|packing|done|error","message":""} from the robot over
    /// <see cref="RobotLink"/> and passes it on to the payment terminal (via <see cref="OrderStatusEvents"/>).
    /// Sending orders is done by <see cref="OrderSender"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public class RobotOrderBridge : MonoBehaviour
    {
        private RobotLink link;

        private void OnEnable()
        {
            OrderStatusEvents.IsListening = true;
            Attach();
        }

        private void Start()
        {
            Attach();
        }

        private void OnDisable()
        {
            if (link != null)
            {
                link.OnMessage -= HandleMessage;
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
        }

        private void HandleMessage(string type, string json)
        {
            if (type != "status")
            {
                return;
            }

            OrderStatusMessage message;
            try
            {
                message = JsonUtility.FromJson<OrderStatusMessage>(json);
            }
            catch (System.ArgumentException)
            {
                return;
            }

            if (message == null || string.IsNullOrEmpty(message.orderId) || !message.TryGetStatus(out OrderStatus status))
            {
                Debug.LogWarning("[Robot] Ignored status message with a missing orderId or unknown status: " + json);
                return;
            }

            Debug.Log($"[Robot] Order {message.orderId} status: {message.status}");
            OrderStatusEvents.Notify(message, status);
        }
    }
}
