using System;
using GemStore.Showcase;
using UnityEngine;

namespace GemStore.Customize
{
    public enum SettingType
    {
        LooseGem,
        Ring,
        Pendant
    }

    public enum MetalType
    {
        Silver,
        Gold,
        RoseGold
    }

    /// <summary>A gem together with the setting and metal the customer chose, and what the whole piece costs (THB).</summary>
    [Serializable]
    public class JewelryConfig
    {
        public GemData gem;
        public SettingType setting;
        public MetalType metal;

        [SerializeField] private int settingPrice;
        [SerializeField] private int totalPrice;

        /// <summary>Gem price + setting price (the metal multiplier only applies to the setting).</summary>
        public int TotalPrice => totalPrice;

        /// <summary>Price of the setting alone, after the metal multiplier.</summary>
        public int SettingPrice => settingPrice;

        public bool IsLoose => setting == SettingType.LooseGem;

        /// <summary>"Loose", or for example "Ring, Gold" / "Pendant, Rose Gold".</summary>
        public string Description => IsLoose ? "Loose" : $"{JewelryNames.Setting(setting)}, {JewelryNames.Metal(metal)}";

        /// <summary>True when both configs are the same gem with the same setting (and metal, unless loose).</summary>
        public bool SameChoice(JewelryConfig other)
        {
            if (other == null || gem != other.gem || setting != other.setting)
            {
                return false;
            }

            return IsLoose || metal == other.metal;
        }

        public JewelryConfig()
        {
        }

        public JewelryConfig(GemData gem, SettingType setting, MetalType metal, SettingCatalog catalog)
        {
            this.gem = gem;
            this.setting = setting;
            this.metal = metal;
            Recalculate(catalog);
        }

        public static JewelryConfig LooseGem(GemData gem)
        {
            return new JewelryConfig { gem = gem, setting = SettingType.LooseGem, metal = MetalType.Silver, totalPrice = gem != null ? gem.Price : 0 };
        }

        public void Recalculate(SettingCatalog catalog)
        {
            settingPrice = catalog != null ? catalog.GetSettingPrice(setting, metal) : 0;
            totalPrice = (gem != null ? gem.Price : 0) + settingPrice;
        }
    }

    /// <summary>Display names and the lowercase names used in the robot order.</summary>
    public static class JewelryNames
    {
        public static string Setting(SettingType setting)
        {
            switch (setting)
            {
                case SettingType.Ring: return "Ring";
                case SettingType.Pendant: return "Pendant";
                default: return "Loose";
            }
        }

        public static string Metal(MetalType metal)
        {
            switch (metal)
            {
                case MetalType.Gold: return "Gold";
                case MetalType.RoseGold: return "Rose Gold";
                default: return "Silver";
            }
        }

        public static string Wire(SettingType setting)
        {
            switch (setting)
            {
                case SettingType.Ring: return "ring";
                case SettingType.Pendant: return "pendant";
                default: return "loose";
            }
        }

        /// <summary>"silver", "gold", "rosegold"; a loose gem has no metal: "none".</summary>
        public static string Wire(MetalType metal, SettingType setting)
        {
            if (setting == SettingType.LooseGem)
            {
                return "none";
            }

            switch (metal)
            {
                case MetalType.Gold: return "gold";
                case MetalType.RoseGold: return "rosegold";
                default: return "silver";
            }
        }
    }
}
