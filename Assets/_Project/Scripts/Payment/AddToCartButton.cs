using System.Collections;
using GemStore.Customize;
using GemStore.Showcase;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace GemStore.Payment
{
    /// <summary>
    /// "Add to Cart" button of the gem info panel. Works with ray and poke input (both arrive as UI pointer clicks).
    /// A gem already in the cart shows "In Cart" and cannot be added again.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class AddToCartButton : MonoBehaviour, IPointerClickHandler
    {
        public enum ButtonState
        {
            NoGem,
            Ready,
            Added,
            InCart,
            Update // the gem is in the cart with another setting or metal: pressing replaces it
        }

        [Header("References")]
        [SerializeField] private Button button;
        [SerializeField] private Image background;
        [SerializeField] private TMP_Text label;
        [SerializeField, Tooltip("Check mark shown next to 'Added' (the TMP font has no check mark glyph).")]
        private Image checkIcon;
        [SerializeField] private AudioSource audioSource;
        [SerializeField, Tooltip("Placeholder click sound.")] private AudioClip clickClip;

        [Header("Feedback")]
        [SerializeField] private float addedDuration = 1.5f;
        [SerializeField, Range(0f, 1f)] private float hapticAmplitude = 0.25f;
        [SerializeField, Min(0f)] private float hapticDuration = 0.08f;

        [Header("Colours")]
        [SerializeField] private Color readyColor = new Color(1f, 0.80f, 0.30f);
        [SerializeField] private Color addedColor = new Color(0.30f, 0.78f, 0.45f);
        [SerializeField] private Color inCartColor = new Color(0.30f, 0.32f, 0.38f);
        [SerializeField] private Color darkText = new Color(0.12f, 0.09f, 0.02f);
        [SerializeField] private Color lightText = new Color(0.78f, 0.80f, 0.86f);

        private JewelryConfig config;
        private Coroutine flashRoutine;

        public ButtonState State { get; private set; } = ButtonState.NoGem;

        private void Reset()
        {
            button = GetComponent<Button>();
            background = GetComponent<Image>();
            label = GetComponentInChildren<TMP_Text>();
        }

        private void OnEnable()
        {
            CartManager.Instance.OnCartChanged += Refresh;
            CartManager.Instance.OnLockChanged += Refresh;
            Refresh();
        }

        private void OnDisable()
        {
            if (CartManager.HasInstance)
            {
                CartManager.Instance.OnCartChanged -= Refresh;
                CartManager.Instance.OnLockChanged -= Refresh;
            }

            flashRoutine = null;
        }

        /// <summary>Sets the gem this button adds. Cancels any "Added" flash from the previous gem.</summary>
        public void SetGem(GemData newGem)
        {
            SetConfig(newGem != null ? JewelryConfig.LooseGem(newGem) : null);
        }

        /// <summary>Sets the piece (gem + setting + metal) this button adds to the cart or updates in it.</summary>
        public void SetConfig(JewelryConfig newConfig)
        {
            bool sameGem = config != null && newConfig != null && config.gem == newConfig.gem;
            if (flashRoutine != null && !sameGem)
            {
                StopCoroutine(flashRoutine);
                flashRoutine = null;
            }

            config = newConfig;
            Refresh();
        }

        private void Refresh()
        {
            if (flashRoutine != null)
            {
                return; // keep "Added" visible until the flash is over
            }

            if (config == null || config.gem == null)
            {
                Apply(ButtonState.NoGem);
                return;
            }

            JewelryConfig inCart = CartManager.Instance.GetConfig(config.gem);
            if (inCart == null)
            {
                Apply(ButtonState.Ready);
            }
            else
            {
                Apply(inCart.SameChoice(config) ? ButtonState.InCart : ButtonState.Update);
            }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if ((State != ButtonState.Ready && State != ButtonState.Update) || config == null || CartManager.Instance.IsLocked)
            {
                return;
            }

            if (!CartManager.Instance.Add(config))
            {
                return;
            }

            if (audioSource != null && clickClip != null)
            {
                audioSource.PlayOneShot(clickClip);
            }

            if (eventData is TrackedDeviceEventData deviceData && deviceData.interactor is XRBaseInputInteractor inputInteractor)
            {
                inputInteractor.SendHapticImpulse(hapticAmplitude, hapticDuration);
            }

            Apply(ButtonState.Added);
            flashRoutine = StartCoroutine(FlashAdded());
        }

        private IEnumerator FlashAdded()
        {
            yield return new WaitForSeconds(addedDuration);
            flashRoutine = null;
            Refresh();
        }

        private void Apply(ButtonState state)
        {
            State = state;
            button.interactable = (state == ButtonState.Ready || state == ButtonState.Update) && !CartManager.Instance.IsLocked;

            switch (state)
            {
                case ButtonState.Added:
                    label.text = "Added";
                    label.margin = new Vector4(90f, 0f, 0f, 0f); // make room for the check icon on the left
                    SetColors(addedColor, darkText);
                    break;
                case ButtonState.InCart:
                    label.text = "In Cart";
                    label.margin = Vector4.zero;
                    SetColors(inCartColor, lightText);
                    break;
                case ButtonState.Update:
                    label.text = "Update Cart";
                    label.margin = Vector4.zero;
                    SetColors(readyColor, darkText);
                    break;
                default:
                    label.text = "Add to Cart";
                    label.margin = Vector4.zero;
                    SetColors(readyColor, darkText);
                    break;
            }

            if (checkIcon != null)
            {
                checkIcon.enabled = state == ButtonState.Added;
                checkIcon.color = darkText;
            }
        }

        private void SetColors(Color backgroundColor, Color textColor)
        {
            background.color = backgroundColor;
            label.color = textColor;
        }
    }
}
