using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GemStore.Customize;
using GemStore.Showcase;
using UnityEngine;

namespace GemStore.Payment
{
    /// <summary>One gem in a paid order. This is what the robot arm needs to pick the right gem.</summary>
    [Serializable]
    public class OrderItem
    {
        public string gemId;
        public GemShape shape;
        public GemColor color;
        public SettingType setting;   // LooseGem for a plain gem
        public MetalType metal;       // only meaningful when there is a setting
        public int slot;              // cabinet slot 1..9 (Slot_0 = 1) = robot pick pose number; 0 = gem not in a cabinet
        public int price;             // gem + setting
    }

    /// <summary>A paid order.</summary>
    [Serializable]
    public class Order
    {
        public string orderId;
        public List<OrderItem> items = new List<OrderItem>();
        public int total;
        public DateTime time;

        /// <summary>Builds an order from cart entries (gem + setting + metal). The robot still only needs shape + color to pick the gem.</summary>
        public static Order FromConfigs(string orderId, IReadOnlyList<JewelryConfig> configs, int total, DateTime time)
        {
            var order = new Order { orderId = orderId, total = total, time = time };
            foreach (JewelryConfig config in configs)
            {
                GemData gem = config.gem;
                order.items.Add(new OrderItem { gemId = gem.Id, shape = gem.Shape, color = gem.GemColor, setting = config.setting, metal = config.metal, slot = GemShowcase.SlotNumberOf(gem), price = config.TotalPrice });
            }

            return order;
        }

        public static Order FromGems(string orderId, IReadOnlyList<GemData> gems, int total, DateTime time)
        {
            var order = new Order { orderId = orderId, total = total, time = time };
            foreach (GemData gem in gems)
            {
                order.items.Add(new OrderItem { gemId = gem.Id, shape = gem.Shape, color = gem.GemColor, slot = GemShowcase.SlotNumberOf(gem), price = gem.Price });
            }

            return order;
        }

        /// <summary>
        /// JSON with enum names as text, for example
        /// {"orderId":"1234","items":[{"gemId":"gem_ruby_round","shape":"Round","color":"Red","price":45000}],"total":45000,"time":"2026-11-18T14:32:10"}.
        /// (JsonUtility writes enums as numbers, so the JSON is built by hand.)
        /// </summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"orderId\":\"").Append(Escape(orderId)).Append("\",\"items\":[");
            for (int i = 0; i < items.Count; i++)
            {
                OrderItem item = items[i];
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"gemId\":\"").Append(Escape(item.gemId))
                  .Append("\",\"shape\":\"").Append(item.shape)
                  .Append("\",\"color\":\"").Append(item.color)
                  .Append("\",\"setting\":\"").Append(item.setting == SettingType.LooseGem ? "Loose" : item.setting.ToString())
                  .Append("\",\"metal\":\"").Append(item.setting == SettingType.LooseGem ? "None" : item.metal.ToString())
                  .Append("\",\"slot\":").Append(item.slot.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"price\":").Append(item.price.ToString(CultureInfo.InvariantCulture))
                  .Append('}');
            }

            sb.Append("],\"total\":").Append(total.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"time\":\"").Append(time.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)).Append("\"}");
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    /// <summary>Global order events. Subscribe (for example from the robot arm controller) to react to a paid order.</summary>
    public static class OrderEvents
    {
        /// <summary>Raised when a payment succeeds, before the cart is cleared.</summary>
        public static event Action<Order> OnOrderPaid;

        public static void NotifyPaid(Order order)
        {
            OnOrderPaid?.Invoke(order);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            OnOrderPaid = null;
        }
    }
}
