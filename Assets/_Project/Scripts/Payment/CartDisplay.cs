using System;
using GemStore.Customize;
using TMPro;
using UnityEngine;

namespace GemStore.Payment
{
    /// <summary>
    /// World Space cart screen: lists the gems in the cart (name, price, remove button), the total, or
    /// "Your cart is empty". The panel grows upwards from its bottom edge as rows are added.
    /// </summary>
    [DisallowMultipleComponent]
    public class CartDisplay : MonoBehaviour
    {
        [Serializable]
        public class RowView
        {
            public GameObject root;
            public TMP_Text nameLabel;
            public TMP_Text priceLabel;
            public CartRemoveButton removeButton;
        }

        // Layout in canvas units (the prefab canvas is scaled 0.0004, so 1 unit = 0.4 mm).
        private const float Width = 750f;
        private const float TopMargin = 30f;
        private const float BottomMargin = 30f;
        private const float TitleHeight = 90f;
        private const float RowHeight = 80f;
        private const float TotalHeight = 100f;
        private const float Gap = 10f;
        private const float EmptyHeight = 90f;

        [SerializeField] private Canvas canvas;
        [SerializeField] private RectTransform background;
        [SerializeField] private TMP_Text titleLabel;
        [SerializeField] private TMP_Text emptyLabel;
        [SerializeField] private TMP_Text totalLabel;
        [SerializeField] private RowView[] rows = new RowView[0];

        private void OnEnable()
        {
            CartManager cart = CartManager.Instance;
            cart.OnCartChanged += Refresh;
            Refresh();
        }

        private void OnDisable()
        {
            if (CartManager.HasInstance)
            {
                CartManager.Instance.OnCartChanged -= Refresh;
            }
        }

        private void Start()
        {
            if (canvas != null && canvas.worldCamera == null)
            {
                canvas.worldCamera = Camera.main;
            }
        }

        /// <summary>Number of rows currently shown (for tests).</summary>
        public int VisibleRows { get; private set; }

        private void Refresh()
        {
            CartManager cart = CartManager.Instance;
            int count = Mathf.Min(cart.Count, rows.Length);
            VisibleRows = count;
            bool empty = cart.Count == 0;

            float height = empty
                ? TopMargin + TitleHeight + Gap + EmptyHeight + BottomMargin
                : TopMargin + TitleHeight + Gap + count * RowHeight + Gap + TotalHeight + BottomMargin;

            ((RectTransform)transform).sizeDelta = new Vector2(Width, height);
            background.sizeDelta = new Vector2(Width, height);

            SetY(titleLabel.rectTransform, height - TopMargin - TitleHeight * 0.5f);

            emptyLabel.gameObject.SetActive(empty);
            if (empty)
            {
                SetY(emptyLabel.rectTransform, BottomMargin + EmptyHeight * 0.5f);
            }

            totalLabel.gameObject.SetActive(!empty);
            if (!empty)
            {
                totalLabel.text = $"Total  {PriceFormatter.Format(cart.TotalPrice, totalLabel.font)}";
                SetY(totalLabel.rectTransform, BottomMargin + TotalHeight * 0.5f);
            }

            for (int i = 0; i < rows.Length; i++)
            {
                RowView row = rows[i];
                bool active = i < count;
                row.root.SetActive(active);
                if (!active)
                {
                    row.removeButton.SetGem(null);
                    continue;
                }

                JewelryConfig config = cart.Configs[i];
                var gem = config.gem;
                // "Ruby Round - Ring, Gold": long names shrink to fit the row
                row.nameLabel.enableAutoSizing = true;
                row.nameLabel.fontSizeMin = 24f;
                row.nameLabel.fontSizeMax = 40f;
                row.nameLabel.text = config.IsLoose ? gem.DisplayName : gem.DisplayName + " \u2013 " + config.Description;
                row.priceLabel.text = PriceFormatter.Format(config.TotalPrice, row.priceLabel.font);
                row.removeButton.SetGem(gem);
                SetY((RectTransform)row.root.transform, height - TopMargin - TitleHeight - Gap - RowHeight * (i + 0.5f));
            }
        }

        private static void SetY(RectTransform rect, float y)
        {
            Vector2 position = rect.anchoredPosition;
            position.y = y;
            rect.anchoredPosition = position;
        }
    }
}
