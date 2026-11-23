using TMPro;
using UnityEngine;

namespace GemStore.Payment
{
    /// <summary>Keeps a small label up to date with the cart, for example "Cart: 2 items · ฿25,800".</summary>
    public class CartSummaryDisplay : MonoBehaviour
    {
        [SerializeField] private TMP_Text label;

        private void Reset()
        {
            label = GetComponentInChildren<TMP_Text>();
        }

        private void OnEnable()
        {
            CartManager.Instance.OnCartChanged += Refresh;
            Refresh();
        }

        private void OnDisable()
        {
            if (CartManager.HasInstance)
            {
                CartManager.Instance.OnCartChanged -= Refresh;
            }
        }

        private void Refresh()
        {
            if (label == null)
            {
                return;
            }

            CartManager cart = CartManager.Instance;
            string noun = cart.Count == 1 ? "item" : "items";
            label.text = $"Cart: {cart.Count} {noun}  ·  {PriceFormatter.Format(cart.TotalPrice, label.font)}";
        }
    }
}
