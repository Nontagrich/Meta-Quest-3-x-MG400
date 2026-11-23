using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace GemStore.Showcase
{
    /// <summary>
    /// Storefront and Mode Select: the grip and the trigger do the same thing, on both hands (hand tracking: pinch, as before).
    /// <list type="bullet">
    /// <item>Grab: grip OR trigger (the Starter Assets rig grabs with the grip only; most first-time Quest users pull the trigger).</item>
    /// <item>Press UI buttons: trigger OR grip (the rig presses UI with the trigger only).</item>
    /// </list>
    /// It wraps each Near-Far interactor's Select and UI Press inputs so each reads "grip OR trigger" from the rig's own bindings;
    /// the input actions and the Samples are not changed. In the XR Interaction Simulator: G, T (right hand) and Shift+G, Shift+T
    /// (left hand) all grab and all press buttons.
    /// Do NOT add this to the Maintenance scene: there the grip is the teleop deadman and the trigger toggles the gripper.
    /// </summary>
    [DisallowMultipleComponent]
    public class GrabWithTriggerOrGrip : MonoBehaviour
    {
        private readonly List<(NearFarInteractor interactor, IXRInputButtonReader select, IXRInputButtonReader uiPress)> wrapped =
            new List<(NearFarInteractor, IXRInputButtonReader, IXRInputButtonReader)>();

        /// <summary>How many interactors were changed (for tests).</summary>
        public int WrappedCount => wrapped.Count;

        private void Start()
        {
            // includes inactive ones: the rig switches between controller and hand interactors at runtime
            foreach (NearFarInteractor interactor in FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include))
            {
                XRInputButtonReader select = interactor.selectInput;
                XRInputButtonReader activate = interactor.activateInput;
                XRInputButtonReader uiPress = interactor.uiPressInput;
                if (select == null || activate == null || uiPress == null || select.bypass is EitherButtonReader)
                {
                    continue;
                }

                // copies of the original sources, taken before any bypass is set: grip (select), trigger (activate), UI press
                XRInputButtonReader grip = Copy(select);
                XRInputButtonReader trigger = Copy(activate);
                XRInputButtonReader press = Copy(uiPress);

                wrapped.Add((interactor, select.bypass, uiPress.bypass));
                select.bypass = new EitherButtonReader(grip, trigger);
                uiPress.bypass = new EitherButtonReader(press, Copy(select));
            }

            Debug.Log($"[Store] Grip and trigger both grab and press UI on {wrapped.Count} interactor(s).");
        }

        private void OnDestroy()
        {
            foreach ((NearFarInteractor interactor, IXRInputButtonReader select, IXRInputButtonReader uiPress) in wrapped)
            {
                if (interactor == null)
                {
                    continue;
                }

                if (interactor.selectInput != null)
                {
                    interactor.selectInput.bypass = select;
                }

                if (interactor.uiPressInput != null)
                {
                    interactor.uiPressInput.bypass = uiPress;
                }
            }

            wrapped.Clear();
        }

        /// <summary>A reader with the same source as the original (so reading it does not go through the bypass again).</summary>
        private static XRInputButtonReader Copy(XRInputButtonReader source)
        {
            var copy = new XRInputButtonReader { inputSourceMode = source.inputSourceMode };
            switch (source.inputSourceMode)
            {
                case XRInputButtonReader.InputSourceMode.InputActionReference:
                    copy.inputActionReferencePerformed = source.inputActionReferencePerformed;
                    copy.inputActionReferenceValue = source.inputActionReferenceValue;
                    break;
                case XRInputButtonReader.InputSourceMode.InputAction:
                    copy.inputActionPerformed = source.inputActionPerformed;
                    copy.inputActionValue = source.inputActionValue;
                    break;
                case XRInputButtonReader.InputSourceMode.ObjectReference:
                    copy.SetObjectReference(source.GetObjectReference());
                    break;
                case XRInputButtonReader.InputSourceMode.ManualValue:
                    copy.manualPerformed = source.manualPerformed;
                    copy.manualValue = source.manualValue;
                    break;
            }

            return copy;
        }

        /// <summary>Pressed while either button is pressed; "pressed this frame" only on the first one, "released" only when both are up.</summary>
        private sealed class EitherButtonReader : IXRInputButtonReader
        {
            private readonly IXRInputButtonReader a;
            private readonly IXRInputButtonReader b;
            private int frame = -1;
            private bool now;
            private bool before;

            public EitherButtonReader(IXRInputButtonReader a, IXRInputButtonReader b)
            {
                this.a = a;
                this.b = b;
            }

            private void Refresh()
            {
                if (frame == Time.frameCount)
                {
                    return;
                }

                // the previous frame's state: if nobody read it last frame, use what the buttons say now minus this frame's presses
                before = frame == Time.frameCount - 1 ? now : (a.ReadIsPerformed() && !a.ReadWasPerformedThisFrame()) || (b.ReadIsPerformed() && !b.ReadWasPerformedThisFrame()) || a.ReadWasCompletedThisFrame() || b.ReadWasCompletedThisFrame();
                frame = Time.frameCount;
                now = a.ReadIsPerformed() || b.ReadIsPerformed();
            }

            public bool ReadIsPerformed()
            {
                Refresh();
                return now;
            }

            public bool ReadWasPerformedThisFrame()
            {
                Refresh();
                return now && !before;
            }

            public bool ReadWasCompletedThisFrame()
            {
                Refresh();
                return !now && before;
            }

            public float ReadValue() => Mathf.Max(a.ReadValue(), b.ReadValue());

            public bool TryReadValue(out float value)
            {
                bool hasA = a.TryReadValue(out float va);
                bool hasB = b.TryReadValue(out float vb);
                value = Mathf.Max(hasA ? va : 0f, hasB ? vb : 0f);
                return hasA || hasB;
            }
        }
    }
}
