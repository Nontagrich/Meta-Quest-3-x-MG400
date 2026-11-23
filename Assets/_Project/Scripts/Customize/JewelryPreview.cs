using GemStore.Showcase;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace GemStore.Customize
{
    /// <summary>
    /// On a gem prefab: shows a ring or pendant setting around the gem and swaps the metal. The setting is a child of the gem, so it
    /// moves and turns with the gem while it is held. With a setting the stone is shown at a realistic jewelry size (the showcase gems are
    /// 4.5 cm, a ring stone is ~9 mm) and the setting models are real size, so a ring is a real 17 mm ring. The preview goes back to a
    /// loose gem when the released gem starts floating back to its cabinet slot.
    /// </summary>
    [DisallowMultipleComponent]
    public class JewelryPreview : MonoBehaviour
    {
        [SerializeField] private MeshFilter settingFilter;
        [SerializeField] private MeshRenderer settingRenderer;
        [SerializeField] private Mesh ringMesh;
        [SerializeField] private Mesh pendantMesh;
        [SerializeField, Tooltip("One material per MetalType: Silver, Gold, RoseGold.")] private Material[] metalMaterials = new Material[3];
        [SerializeField] private SettingCatalog catalog;
        [SerializeField, Range(0.05f, 1f), Tooltip("Scale of the gem when it sits in a setting (9 mm stone / 45 mm showcase gem = 0.2).")]
        private float stoneScale = 0.2f;

        private GemGrabBehaviour grab;
        private GemItem item;
        private Vector3 homeScale = Vector3.one;

        public JewelryConfig Current { get; private set; }

        public SettingCatalog Catalog => catalog;

        private void Awake()
        {
            homeScale = transform.localScale;
            grab = GetComponent<GemGrabBehaviour>();
            item = GetComponent<GemItem>();
            Current = JewelryConfig.LooseGem(item != null ? item.Data : null);

            // The grab transformer would otherwise write the scale it captured at grab time back every frame and undo the jewelry scale.
            // (Nothing scales gems by hand; GemGrabBehaviour restores the scale itself when the gem returns.)
            var grabInteractable = GetComponent<XRGrabInteractable>();
            if (grabInteractable != null)
            {
                grabInteractable.trackScale = false;
            }

            Hide();
        }

        private void OnEnable()
        {
            if (grab != null)
            {
                grab.ReturnStarted += ResetToLooseGem;
            }
        }

        private void OnDisable()
        {
            if (grab != null)
            {
                grab.ReturnStarted -= ResetToLooseGem;
            }
        }

        /// <summary>Applies a setting and metal. LooseGem shows the gem alone (the original behaviour).</summary>
        public void ApplyConfig(JewelryConfig config)
        {
            if (config == null || config.setting == SettingType.LooseGem)
            {
                ResetToLooseGem();
                return;
            }

            Mesh mesh = config.setting == SettingType.Ring ? ringMesh : pendantMesh;
            if (mesh == null || settingFilter == null || settingRenderer == null)
            {
                Debug.LogWarning($"[Customize] {name}: no model for {config.setting}.", this);
                return;
            }

            config.Recalculate(catalog);
            Current = config;

            settingFilter.sharedMesh = mesh;
            int index = Mathf.Clamp((int)config.metal, 0, metalMaterials.Length - 1);
            settingRenderer.sharedMaterial = metalMaterials[index];

            // stone at jewelry size, setting models are real size: cancel the parent scale on the setting
            transform.localScale = homeScale * stoneScale;
            settingRenderer.transform.localScale = Vector3.one / stoneScale;
            settingRenderer.enabled = true;
        }

        /// <summary>Back to the gem alone at its normal size.</summary>
        public void ResetToLooseGem()
        {
            Current = JewelryConfig.LooseGem(item != null ? item.Data : null);
            transform.localScale = homeScale;
            Hide();
        }

        private void Hide()
        {
            if (settingRenderer != null)
            {
                settingRenderer.enabled = false;
            }
        }
    }
}
