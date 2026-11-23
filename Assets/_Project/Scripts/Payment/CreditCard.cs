using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace GemStore.Payment
{
    /// <summary>
    /// The shop's credit card (8.56 x 5.4 cm). It can be picked up and, once every hand lets go,
    /// floats back to the card dock. It cannot be grabbed again until it is back.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(XRGrabInteractable))]
    public class CreditCard : MonoBehaviour
    {
        [SerializeField] private XRGrabInteractable grabInteractable;
        [SerializeField, Tooltip("Where the card rests on the dock. If empty, the pose the card has at start is used.")]
        private Transform homeSlot;
        [SerializeField, Min(0.01f)] private float returnDuration = 0.4f;

        private Vector3 startPosition;
        private Quaternion startRotation;
        private bool returning;

        public bool IsHeld => grabInteractable != null && grabInteractable.isSelected;
        public bool IsReturning => returning;

        /// <summary>How many haptic pulses were requested on the hand holding the card (for tests and debugging).</summary>
        public int HapticPulsesSent { get; private set; }

        /// <summary>Sends a haptic pulse to the hand (controller) that is holding the card. Does nothing if it is not held.</summary>
        public void SendHaptic(float amplitude, float duration)
        {
            if (grabInteractable == null)
            {
                return;
            }

            foreach (var interactor in grabInteractable.interactorsSelecting)
            {
                if (interactor is XRBaseInputInteractor inputInteractor)
                {
                    inputInteractor.SendHapticImpulse(amplitude, duration);
                    HapticPulsesSent++;
                    return;
                }
            }
        }

        private void Reset()
        {
            grabInteractable = GetComponent<XRGrabInteractable>();
        }

        private void Awake()
        {
            if (grabInteractable == null)
            {
                grabInteractable = GetComponent<XRGrabInteractable>();
            }

            startPosition = transform.position;
            startRotation = transform.rotation;
        }

        private void OnEnable()
        {
            grabInteractable.selectExited.AddListener(OnSelectExited);
        }

        private void OnDisable()
        {
            grabInteractable.selectExited.RemoveListener(OnSelectExited);
            if (returning)
            {
                returning = false;
                grabInteractable.enabled = true;
            }
        }

        private void OnSelectExited(SelectExitEventArgs args)
        {
            if (grabInteractable.isSelected || returning)
            {
                return;
            }

            StartCoroutine(ReturnToDock());
        }

        private IEnumerator ReturnToDock()
        {
            returning = true;
            yield return null; // let the interaction manager finish processing the exit

            if (grabInteractable.isSelected)
            {
                returning = false;
                yield break;
            }

            grabInteractable.enabled = false; // cannot be grabbed while floating back

            Vector3 fromPosition = transform.position;
            Quaternion fromRotation = transform.rotation;
            float elapsed = 0f;
            while (elapsed < returnDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / returnDuration);
                float eased = 1f - Mathf.Pow(1f - t, 3f);
                GetHomePose(out Vector3 position, out Quaternion rotation);
                transform.SetPositionAndRotation(Vector3.Lerp(fromPosition, position, eased), Quaternion.Slerp(fromRotation, rotation, eased));
                yield return null;
            }

            GetHomePose(out Vector3 finalPosition, out Quaternion finalRotation);
            transform.SetPositionAndRotation(finalPosition, finalRotation);
            grabInteractable.enabled = true;
            returning = false;
        }

        private void GetHomePose(out Vector3 position, out Quaternion rotation)
        {
            if (homeSlot != null)
            {
                position = homeSlot.position;
                rotation = homeSlot.rotation;
            }
            else
            {
                position = startPosition;
                rotation = startRotation;
            }
        }
    }
}
