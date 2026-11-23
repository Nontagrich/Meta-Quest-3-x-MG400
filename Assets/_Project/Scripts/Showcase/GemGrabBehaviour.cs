using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace GemStore.Showcase
{
    /// <summary>
    /// Behaviour for a gem that can be picked up: stops the idle spin, plays a sound and haptic pulse on grab,
    /// highlights the gem while it is hovered or held, and floats it back to its showcase slot when released.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(XRGrabInteractable), typeof(GemItem))]
    public class GemGrabBehaviour : MonoBehaviour
    {
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        [Header("References")]
        [SerializeField] private XRGrabInteractable grabInteractable;
        [SerializeField] private GemItem gemItem;
        [SerializeField] private IdleSpin idleSpin;
        [SerializeField] private MeshRenderer meshRenderer;

        [Header("Grab feedback")]
        [SerializeField] private AudioSource audioSource;
        [SerializeField, Tooltip("Short sound played when the gem is picked up. Placeholder - replace with a final sound.")]
        private AudioClip grabClip;
        [SerializeField, Range(0f, 1f)] private float hapticAmplitude = 0.3f;
        [SerializeField, Min(0f)] private float hapticDuration = 0.1f;

        [Header("Return to slot")]
        [SerializeField, Min(0.01f), Tooltip("Seconds the gem takes to float back to its slot after release.")]
        private float returnDuration = 0.4f;

        [Header("Hover highlight")]
        [SerializeField, Min(0f), Tooltip("Emission intensity (multiplied by the gem tint) while hovered or held. The material's resting emission is 0.12.")]
        private float highlightEmission = 0.4f;

        private MaterialPropertyBlock propertyBlock;
        private bool highlighted;
        private bool returning;
        private bool spinWasActive = true;
        private Vector3 homeLocalScale;
        private Vector3 fallbackLocalPosition;
        private Quaternion fallbackLocalRotation;

        /// <summary>True while the gem is floating back to its slot (it cannot be picked up during this time).</summary>
        public bool IsReturning => returning;

        /// <summary>Raised when the released gem starts floating back to its slot (for example to reset a jewelry preview).</summary>
        public event Action ReturnStarted;

        private void Reset()
        {
            CacheReferences();
        }

        private void Awake()
        {
            CacheReferences();
            homeLocalScale = transform.localScale;
            fallbackLocalPosition = transform.localPosition;
            fallbackLocalRotation = transform.localRotation;
        }

        private void OnEnable()
        {
            grabInteractable.selectEntered.AddListener(OnSelectEntered);
            grabInteractable.selectExited.AddListener(OnSelectExited);
        }

        private void OnDisable()
        {
            grabInteractable.selectEntered.RemoveListener(OnSelectEntered);
            grabInteractable.selectExited.RemoveListener(OnSelectExited);
            SetHighlight(false);

            if (returning)
            {
                // The coroutine is stopped with this component; do not leave the gem un-grabbable.
                returning = false;
                grabInteractable.enabled = true;
            }
        }

        private void Update()
        {
            bool wantHighlight = !returning && (grabInteractable.isHovered || grabInteractable.isSelected);
            if (wantHighlight != highlighted)
            {
                SetHighlight(wantHighlight);
            }
        }

        private void CacheReferences()
        {
            if (grabInteractable == null) grabInteractable = GetComponent<XRGrabInteractable>();
            if (gemItem == null) gemItem = GetComponent<GemItem>();
            if (idleSpin == null) idleSpin = GetComponent<IdleSpin>();
            if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();
            if (audioSource == null) audioSource = GetComponent<AudioSource>();
        }

        private void OnSelectEntered(SelectEnterEventArgs args)
        {
            if (idleSpin != null)
            {
                // Only remember the state on the first grab, so a second hand does not overwrite it.
                if (grabInteractable.interactorsSelecting.Count <= 1)
                {
                    spinWasActive = idleSpin.enabled;
                }

                idleSpin.SetActive(false);
            }

            if (audioSource != null && grabClip != null)
            {
                audioSource.PlayOneShot(grabClip);
            }

            if (args.interactorObject is XRBaseInputInteractor inputInteractor)
            {
                inputInteractor.SendHapticImpulse(hapticAmplitude, hapticDuration);
            }

            if (GemInfoPanel.Instance != null)
            {
                GemInfoPanel.Instance.Show(gemItem);
            }
        }

        private void OnSelectExited(SelectExitEventArgs args)
        {
            // With Select Mode = Multiple, only return home once the last hand has let go.
            if (grabInteractable.isSelected || returning)
            {
                return;
            }

            if (GemInfoPanel.Instance != null)
            {
                GemInfoPanel.Instance.Release(gemItem); // the panel lingers briefly so a button can still be pressed
            }

            StartCoroutine(ReturnToSlot());
        }

        private IEnumerator ReturnToSlot()
        {
            returning = true;

            // Wait one frame so the interaction manager finishes processing the exit before the interactable is disabled.
            yield return null;
            if (grabInteractable.isSelected)
            {
                // Grabbed again in that single frame: nothing to return.
                returning = false;
                yield break;
            }

            // A disabled interactable cannot be hovered or selected, so the gem cannot be grabbed until it is home.
            grabInteractable.enabled = false;

            ReturnStarted?.Invoke(); // listeners may change the scale, so this comes before the start pose is read

            Vector3 startPosition = transform.position;
            Quaternion startRotation = transform.rotation;
            Vector3 startScale = transform.localScale;

            float elapsed = 0f;
            while (elapsed < returnDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / returnDuration);
                float eased = 1f - Mathf.Pow(1f - t, 3f); // ease-out cubic

                GetHomePose(out Vector3 homePosition, out Quaternion homeRotation);
                transform.position = Vector3.Lerp(startPosition, homePosition, eased);
                transform.rotation = Quaternion.Slerp(startRotation, homeRotation, eased);
                transform.localScale = Vector3.Lerp(startScale, homeLocalScale, eased);
                yield return null;
            }

            GetHomePose(out Vector3 finalPosition, out Quaternion finalRotation);
            transform.SetPositionAndRotation(finalPosition, finalRotation);
            transform.localScale = homeLocalScale;

            grabInteractable.enabled = true;
            if (idleSpin != null)
            {
                idleSpin.SetActive(spinWasActive);
            }

            returning = false;

            if (GemInfoPanel.Instance != null)
            {
                GemInfoPanel.Instance.NotifyReturnedHome(gemItem); // the panel hides now unless a pointer is on it
            }
        }

        private void GetHomePose(out Vector3 position, out Quaternion rotation)
        {
            Transform slot = gemItem != null ? gemItem.HomeSlot : null;
            if (slot != null)
            {
                position = slot.position;
                rotation = slot.rotation;
                return;
            }

            // Not spawned by a showcase: go back to the pose the gem had when it was created.
            Transform parent = transform.parent;
            position = parent != null ? parent.TransformPoint(fallbackLocalPosition) : fallbackLocalPosition;
            rotation = parent != null ? parent.rotation * fallbackLocalRotation : fallbackLocalRotation;
        }

        private void SetHighlight(bool on)
        {
            highlighted = on;
            if (meshRenderer == null)
            {
                return;
            }

            if (!on)
            {
                // Clearing the block restores the shared material (and SRP Batcher compatibility).
                meshRenderer.SetPropertyBlock(null);
                return;
            }

            Color tint = gemItem != null && gemItem.Data != null ? gemItem.Data.Tint : Color.white;
            propertyBlock ??= new MaterialPropertyBlock();
            meshRenderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetColor(EmissionColorId, tint * highlightEmission);
            meshRenderer.SetPropertyBlock(propertyBlock);
        }
    }
}
