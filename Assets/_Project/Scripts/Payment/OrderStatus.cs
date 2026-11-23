using System;
using TMPro;
using UnityEngine;

namespace GemStore.Payment
{
    /// <summary>Progress of an order, reported by the robot arm system.</summary>
    public enum OrderStatus
    {
        Picking,
        Packing,
        Done,
        Error
    }

    /// <summary>
    /// Status message from the robot side, as JSON on the MQTT status topic:
    /// {"orderId":"1234","status":"picking"|"packing"|"done"|"error","message":""}
    /// </summary>
    [Serializable]
    public class OrderStatusMessage
    {
        public string orderId;
        public string status;
        public string message;

        public static string ToWire(OrderStatus status)
        {
            switch (status)
            {
                case OrderStatus.Picking: return "picking";
                case OrderStatus.Packing: return "packing";
                case OrderStatus.Done: return "done";
                default: return "error";
            }
        }

        /// <summary>Parses the wire value (case-insensitive). Returns false for unknown values.</summary>
        public bool TryGetStatus(out OrderStatus result)
        {
            switch ((status ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "picking": result = OrderStatus.Picking; return true;
                case "packing": result = OrderStatus.Packing; return true;
                case "done": result = OrderStatus.Done; return true;
                case "error": result = OrderStatus.Error; return true;
                default: result = OrderStatus.Error; return false;
            }
        }
    }

    /// <summary>Customer-facing texts for each status.</summary>
    public static class OrderStatusTexts
    {
        private const int GiftEmoji = 0x1F381;

        public static string For(OrderStatus status, TMP_FontAsset font = null)
        {
            switch (status)
            {
                case OrderStatus.Picking: return "Robot is picking your gem...";
                case OrderStatus.Packing: return "Packing your gift box...";
                case OrderStatus.Done:
                    // The default TMP font has no emoji; the gift emoji is added only if a font that has it is available.
                    font = font != null ? font : TMP_Settings.defaultFontAsset;
                    return HasGlyph(font, GiftEmoji)
                        ? "Your gift is ready! " + char.ConvertFromUtf32(GiftEmoji)
                        : "Your gift is ready!";
                default: return "Something went wrong – please call staff";
            }
        }

        // HasCharacter only takes a 16-bit char, so look the emoji up in the glyph tables (font and its fallbacks).
        private static bool HasGlyph(TMP_FontAsset font, int codePoint)
        {
            if (font == null)
            {
                return false;
            }

            if (font.characterLookupTable != null && font.characterLookupTable.ContainsKey((uint)codePoint))
            {
                return true;
            }

            if (font.fallbackFontAssetTable != null)
            {
                foreach (TMP_FontAsset fallback in font.fallbackFontAssetTable)
                {
                    if (fallback != null && fallback.characterLookupTable != null && fallback.characterLookupTable.ContainsKey((uint)codePoint))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>Static hub between whatever receives robot status (MQTT) and the terminal that displays it.</summary>
    public static class OrderStatusEvents
    {
        /// <summary>Raised for every valid status message. The order id is not filtered here.</summary>
        public static event Action<OrderStatusMessage, OrderStatus> OnStatusReceived;

        /// <summary>True while something (for example OrderStatusListener) is delivering status messages.</summary>
        public static bool IsListening { get; set; }

        public static void Notify(OrderStatusMessage message, OrderStatus status)
        {
            OnStatusReceived?.Invoke(message, status);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            OnStatusReceived = null;
            IsListening = false;
        }
    }
}
