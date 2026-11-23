using UnityEngine;

namespace GemStore.Showcase
{
    public enum GemShape
    {
        Round,
        Emerald
    }

    public enum GemColor
    {
        Red,
        Blue,
        Green,
        Yellow,
        Purple,
        Pink,
        White
    }

    /// <summary>
    /// Static catalogue data for one gemstone sold in the store.
    /// </summary>
    [CreateAssetMenu(fileName = "GemData", menuName = "GemStore/Gem Data", order = 0)]
    public class GemData : ScriptableObject
    {
        [SerializeField] private string id;
        [SerializeField] private string displayName;
        [SerializeField] private GemShape shape;
        [SerializeField] private GemColor gemColor;
        [SerializeField] private Color tint = Color.white;
        [SerializeField, Min(0), Tooltip("Price in Thai baht (THB).")] private int price;
        [SerializeField, TextArea(2, 5)] private string description;

        public string Id => id;
        public string DisplayName => displayName;
        public GemShape Shape => shape;
        public GemColor GemColor => gemColor;
        public Color Tint => tint;
        public int Price => price;
        public string Description => description;
    }
}
