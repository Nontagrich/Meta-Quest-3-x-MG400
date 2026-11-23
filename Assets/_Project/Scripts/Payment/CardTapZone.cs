using System;
using UnityEngine;

namespace GemStore.Payment
{
    /// <summary>Trigger volume in front of the terminal's contactless symbol. Reports credit cards entering and leaving.</summary>
    [RequireComponent(typeof(Collider))]
    public class CardTapZone : MonoBehaviour
    {
        public event Action<CreditCard> CardEntered;
        public event Action<CreditCard> CardExited;

        private void Reset()
        {
            GetComponent<Collider>().isTrigger = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            CreditCard card = other.GetComponentInParent<CreditCard>();
            if (card != null)
            {
                CardEntered?.Invoke(card);
            }
        }

        private void OnTriggerExit(Collider other)
        {
            CreditCard card = other.GetComponentInParent<CreditCard>();
            if (card != null)
            {
                CardExited?.Invoke(card);
            }
        }
    }
}
