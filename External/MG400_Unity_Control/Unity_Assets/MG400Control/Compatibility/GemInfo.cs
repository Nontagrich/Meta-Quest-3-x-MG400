using System;
using System.Collections.Generic;
using UnityEngine;

namespace CPE494
{
    public enum GemShape { Round, Emerald }

    /// <summary>One gem type in the shop catalog (9 total: shapes x colors mixed).</summary>
    [Serializable]
    public class GemInfo
    {
        public GemShape shape;
        public string colorName;
        public Color color;
        public int priceBaht;

        public string DisplayName => $"{colorName} {shape}";
        public override string ToString() => DisplayName;
    }

    /// <summary>Catalog shared by cabinet, sales panel and packing cell.</summary>
    public static class GemCatalog
    {
        public static readonly GemInfo[] All =
        {
            new GemInfo { shape = GemShape.Round,   colorName = "Ruby",     color = new Color(0.80f, 0.06f, 0.12f), priceBaht = 2500 },
            new GemInfo { shape = GemShape.Round,   colorName = "Sapphire", color = new Color(0.05f, 0.18f, 0.75f), priceBaht = 3100 },
            new GemInfo { shape = GemShape.Emerald, colorName = "Emerald",  color = new Color(0.02f, 0.62f, 0.28f), priceBaht = 4200 },
            new GemInfo { shape = GemShape.Round,   colorName = "Amethyst", color = new Color(0.52f, 0.22f, 0.78f), priceBaht = 1800 },
            new GemInfo { shape = GemShape.Emerald, colorName = "Topaz",    color = new Color(0.95f, 0.62f, 0.10f), priceBaht = 1500 },
            new GemInfo { shape = GemShape.Round,   colorName = "Citrine",  color = new Color(0.98f, 0.85f, 0.20f), priceBaht = 1200 },
            new GemInfo { shape = GemShape.Emerald, colorName = "Rose",     color = new Color(0.92f, 0.35f, 0.55f), priceBaht = 2200 },
            new GemInfo { shape = GemShape.Round,   colorName = "Diamond",  color = new Color(0.92f, 0.96f, 1.00f), priceBaht = 6900 },
            new GemInfo { shape = GemShape.Emerald, colorName = "Aquamarine", color = new Color(0.30f, 0.85f, 0.90f), priceBaht = 3400 },
        };

        public static GemInfo Find(string colorName, GemShape shape)
        {
            foreach (var g in All)
                if (g.colorName == colorName && g.shape == shape) return g;
            return null;
        }

        public static Dictionary<string, GemInfo> ByKey()
        {
            var d = new Dictionary<string, GemInfo>();
            foreach (var g in All) d[Key(g)] = g;
            return d;
        }

        public static string Key(GemInfo g) => $"{g.shape}_{g.colorName}";
    }
}
