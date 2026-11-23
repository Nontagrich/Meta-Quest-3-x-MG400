using GemStore.Payment;
using GemStore.Showcase;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace GemStore.Customize
{
    /// <summary>
    /// The customization part of the gem info panel: setting buttons (Loose / Ring / Pendant), metal swatches
    /// (Silver / Gold / Rose Gold), a "Popular" tag on Ring + Gold and the live price line. Every choice is applied to the held gem's
    /// <see cref="JewelryPreview"/> at once and passed to the Add to Cart button. The buttons are normal UI buttons, so poke and ray work.
    /// </summary>
    [DisallowMultipleComponent]
    public class GemCustomizePanel : MonoBehaviour
    {
        [Header("Setting buttons (Loose, Ring, Pendant)")]
        [SerializeField] private Button[] settingButtons = new Button[3];
        [SerializeField] private Image[] settingImages = new Image[3];
        [SerializeField] private TMP_Text[] settingLabels = new TMP_Text[3];

        [Header("Metal swatches (Silver, Gold, Rose Gold)")]
        [SerializeField] private GameObject metalRow;
        [SerializeField] private Button[] metalButtons = new Button[3];
        [SerializeField] private Image[] metalOutlines = new Image[3];
        [SerializeField, Tooltip("Small 'Popular' tag next to the Gold swatch; shown while Ring is selected.")] private GameObject popularTag;

        [Header("Output")]
        [SerializeField] private TMP_Text priceLine;
        [SerializeField] private AddToCartButton addButton;
        [SerializeField] private AudioSource audioSource;
        [SerializeField] private AudioClip clickClip;

        [Header("Colours")]
        [SerializeField] private Color selectedColor = new Color(1f, 0.80f, 0.30f);
        [SerializeField] private Color idleColor = new Color(0.20f, 0.22f, 0.30f);
        [SerializeField] private Color selectedText = new Color(0.12f, 0.09f, 0.02f);
        [SerializeField] private Color idleText = new Color(0.86f, 0.88f, 0.94f);

        private GemItem gem;
        private JewelryPreview preview;
        private XRGrabInteractable grab;
        private SettingType setting = SettingType.LooseGem;
        private MetalType metal = MetalType.Silver;

        /// <summary>The configuration currently chosen on the panel.</summary>
        public JewelryConfig Current { get; private set; }

        private void OnEnable()
        {
            for (int i = 0; i < settingButtons.Length; i++)
            {
                int index = i;
                if (settingButtons[i] != null) settingButtons[i].onClick.AddListener(() => SelectSetting((SettingType)index));
            }

            for (int i = 0; i < metalButtons.Length; i++)
            {
                int index = i;
                if (metalButtons[i] != null) metalButtons[i].onClick.AddListener(() => SelectMetal((MetalType)index));
            }
        }

        private void OnDisable()
        {
            foreach (Button button in settingButtons) button?.onClick.RemoveAllListeners();
            foreach (Button button in metalButtons) button?.onClick.RemoveAllListeners();
        }

        /// <summary>Called when a gem is picked up. Starts from the piece already in the cart, or from a loose gem.</summary>
        public void Bind(GemItem newGem)
        {
            gem = newGem;
            preview = newGem != null ? newGem.GetComponent<JewelryPreview>() : null;
            grab = newGem != null ? newGem.GetComponent<XRGrabInteractable>() : null;

            setting = SettingType.LooseGem;
            metal = MetalType.Silver;
            JewelryConfig inCart = gem != null && gem.Data != null ? CartManager.Instance.GetConfig(gem.Data) : null;
            if (inCart != null)
            {
                setting = inCart.setting;
                metal = inCart.metal;
            }

            Apply(true);
        }

        public void SelectSetting(SettingType newSetting)
        {
            if (gem == null)
            {
                return;
            }

            setting = newSetting;
            Click();
            Apply(true);
        }

        public void SelectMetal(MetalType newMetal)
        {
            if (gem == null || setting == SettingType.LooseGem)
            {
                return;
            }

            metal = newMetal;
            Click();
            Apply(true);
        }

        private void Apply(bool updatePreview)
        {
            if (gem == null || gem.Data == null)
            {
                return;
            }

            SettingCatalog catalog = preview != null ? preview.Catalog : null;
            Current = new JewelryConfig(gem.Data, setting, metal, catalog);

            // The preview only follows while the gem is in the hand. After release it floats home and resets itself.
            if (updatePreview && preview != null && grab != null && grab.isSelected)
            {
                preview.ApplyConfig(Current);
            }

            if (priceLine != null)
            {
                TMP_FontAsset font = priceLine.font;
                priceLine.text = setting == SettingType.LooseGem
                    ? PriceFormatter.Format(gem.Data.Price, font)
                    : $"{PriceFormatter.Format(gem.Data.Price, font)} + {JewelryNames.Setting(setting)} {PriceFormatter.Format(Current.SettingPrice, font)} = {PriceFormatter.Format(Current.TotalPrice, font)}";
            }

            addButton?.SetConfig(Current);

            for (int i = 0; i < settingImages.Length; i++)
            {
                bool selected = i == (int)setting;
                if (settingImages[i] != null) settingImages[i].color = selected ? selectedColor : idleColor;
                if (settingLabels[i] != null) settingLabels[i].color = selected ? selectedText : idleText;
            }

            if (metalRow != null) metalRow.SetActive(setting != SettingType.LooseGem);
            for (int i = 0; i < metalOutlines.Length; i++)
            {
                if (metalOutlines[i] != null) metalOutlines[i].enabled = i == (int)metal;
            }

            if (popularTag != null) popularTag.SetActive(setting == SettingType.Ring);
        }

        private void Click()
        {
            if (audioSource != null && clickClip != null)
            {
                audioSource.PlayOneShot(clickClip);
            }
        }
    }
}
