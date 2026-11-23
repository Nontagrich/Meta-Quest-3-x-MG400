using GemStore.Showcase;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace GemStore.Payment
{
    /// <summary>The "remove" (X) button of one cart row. Works with ray and poke; disabled while the cart is locked.</summary>
    [RequireComponent(typeof(Button))]
    public class CartRemoveButton : MonoBehaviour, IPointerClickHandler
    {
        [SerializeField] private Button button;
        [SerializeField, Range(0f, 1f)] private float hapticAmplitude = 0.2f;
        [SerializeField, Min(0f)] private float hapticDuration = 0.06f;

        private GemData gem;

        private void Reset()
        {
            button = GetComponent<Button>();
        }

        private void OnEnable()
        {
            CartManager.Instance.OnLockChanged += RefreshInteractable;
            RefreshInteractable();
        }

        private void OnDisable()
        {
            if (CartManager.HasInstance)
            {
                CartManager.Instance.OnLockChanged -= RefreshInteractable;
            }
        }

        public void SetGem(GemData newGem)
        {
            gem = newGem;
            RefreshInteractable();
        }

        private void RefreshInteractable()
        {
            button.interactable = gem != null && !CartManager.Instance.IsLocked;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (gem == null || CartManager.Instance.IsLocked)
            {
                return;
            }

            if (!CartManager.Instance.Remove(gem))
            {
                return;
            }

            if (eventData is TrackedDeviceEventData deviceData && deviceData.interactor is XRBaseInputInteractor inputInteractor)
            {
                inputInteractor.SendHapticImpulse(hapticAmplitude, hapticDuration);
            }
        }
    }
}
