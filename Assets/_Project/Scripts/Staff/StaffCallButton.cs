using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace GemStore.Staff
{
    /// <summary>
    /// The physical "Call Staff" bell. Pressed by poke (finger) or by ray (controller / simulator) through an
    /// XRSimpleInteractable. The small status light shows: white = idle, blinking yellow = calling, green = connected.
    /// </summary>
    [DisallowMultipleComponent]
    public class StaffCallButton : MonoBehaviour
    {
        public enum LightMode
        {
            Idle,
            Calling,
            Connected
        }

        [SerializeField] private XRSimpleInteractable interactable;
        [SerializeField] private Transform dome;
        [SerializeField] private MeshRenderer lightRenderer;
        [SerializeField] private Material idleMaterial;
        [SerializeField] private Material callingOnMaterial;
        [SerializeField] private Material callingOffMaterial;
        [SerializeField] private Material connectedMaterial;
        [SerializeField, Min(0.05f)] private float blinkSeconds = 0.4f;
        [SerializeField] private float pressDepth = 0.008f;

        private LightMode mode = LightMode.Idle;
        private Vector3 domeRest;
        private Coroutine pressRoutine;

        /// <summary>Raised whenever the bell is pressed (the controller decides whether that does anything).</summary>
        public event Action Pressed;

        public LightMode Light => mode;

        private void Awake()
        {
            if (dome != null)
            {
                domeRest = dome.localPosition;
            }
        }

        private void OnEnable()
        {
            if (interactable != null)
            {
                interactable.selectEntered.AddListener(OnSelectEntered);
            }

            Apply();
        }

        private void OnDisable()
        {
            if (interactable != null)
            {
                interactable.selectEntered.RemoveListener(OnSelectEntered);
            }
        }

        private void Update()
        {
            if (mode == LightMode.Calling)
            {
                Apply();
            }
        }

        public void SetLight(LightMode newMode)
        {
            mode = newMode;
            Apply();
        }

        /// <summary>Presses the bell from code (animation + event). Used by tests and debug tools.</summary>
        public void Press()
        {
            Animate();
            Pressed?.Invoke();
        }

        private void OnSelectEntered(SelectEnterEventArgs args)
        {
            if (args.interactorObject is XRBaseInputInteractor input)
            {
                input.SendHapticImpulse(0.4f, 0.08f);
            }

            Press();
        }

        private void Apply()
        {
            if (lightRenderer == null)
            {
                return;
            }

            switch (mode)
            {
                case LightMode.Calling:
                    bool on = Mathf.FloorToInt(Time.unscaledTime / blinkSeconds) % 2 == 0;
                    lightRenderer.sharedMaterial = on ? callingOnMaterial : callingOffMaterial;
                    break;
                case LightMode.Connected:
                    lightRenderer.sharedMaterial = connectedMaterial;
                    break;
                default:
                    lightRenderer.sharedMaterial = idleMaterial;
                    break;
            }
        }

        private void Animate()
        {
            if (dome == null)
            {
                return;
            }

            if (pressRoutine != null)
            {
                StopCoroutine(pressRoutine);
            }

            pressRoutine = StartCoroutine(PressAnimation());
        }

        private IEnumerator PressAnimation()
        {
            dome.localPosition = domeRest + Vector3.down * pressDepth;
            yield return new WaitForSecondsRealtime(0.12f);
            dome.localPosition = domeRest;
            pressRoutine = null;
        }
    }
}
