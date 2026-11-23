using UnityEngine;

namespace GemStore.Customize
{
    /// <summary>Base price of each setting and the price multiplier of each metal (THB). The multiplier applies to the setting price only.</summary>
    [CreateAssetMenu(fileName = "SettingCatalog", menuName = "GemStore/Setting Catalog", order = 4)]
    public class SettingCatalog : ScriptableObject
    {
        [Header("Setting base price (THB)")]
        [SerializeField, Min(0)] private int looseGemPrice = 0;
        [SerializeField, Min(0)] private int ringPrice = 3900;
        [SerializeField, Min(0), Tooltip("Includes the chain.")] private int pendantPrice = 2900;

        [Header("Metal price multiplier (setting only)")]
        [SerializeField, Min(0f)] private float silverMultiplier = 1.0f;
        [SerializeField, Min(0f)] private float goldMultiplier = 2.2f;
        [SerializeField, Min(0f)] private float roseGoldMultiplier = 2.4f;

        public int GetBasePrice(SettingType setting)
        {
            switch (setting)
            {
                case SettingType.Ring: return ringPrice;
                case SettingType.Pendant: return pendantPrice;
                default: return looseGemPrice;
            }
        }

        public float GetMetalMultiplier(MetalType metal)
        {
            switch (metal)
            {
                case MetalType.Gold: return goldMultiplier;
                case MetalType.RoseGold: return roseGoldMultiplier;
                default: return silverMultiplier;
            }
        }

        /// <summary>Setting price after the metal multiplier. A loose gem has no setting, so no metal either.</summary>
        public int GetSettingPrice(SettingType setting, MetalType metal)
        {
            if (setting == SettingType.LooseGem)
            {
                return looseGemPrice;
            }

            return Mathf.RoundToInt(GetBasePrice(setting) * GetMetalMultiplier(metal));
        }
    }
}
