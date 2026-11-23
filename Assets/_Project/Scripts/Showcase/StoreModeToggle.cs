using System;
using UnityEngine;

namespace GemStore.Showcase
{
    public enum StoreMode
    {
        /// <summary>The full virtual shop room (walls, ceiling, decoration) is shown.</summary>
        VirtualStore,

        /// <summary>Only the interactive furniture is shown, to be placed in the real room (passthrough).</summary>
        MixedReality
    }

    /// <summary>
    /// Switches between the Virtual Store (StoreShell and the decoration visible) and Mixed Reality (they are hidden, so the
    /// real room can show through). The furniture (showcase, checkout, staff corner) stays in both modes.
    /// Passthrough camera setup (transparent background) is not handled here.
    /// </summary>
    [DisallowMultipleComponent]
    public class StoreModeToggle : MonoBehaviour
    {
        [SerializeField] private StoreMode mode = StoreMode.VirtualStore;
        [SerializeField, Tooltip("Room shell: floor, walls and ceiling. Visible only in Virtual Store.")]
        private GameObject storeShell;
        [SerializeField, Tooltip("Extra objects that only make sense inside the virtual room (for example the wall decoration).")]
        private GameObject[] hiddenInMixedReality = new GameObject[0];

        public StoreMode Mode => mode;

        /// <summary>Raised after the mode changes.</summary>
        public event Action<StoreMode> ModeChanged;

        private void Start()
        {
            Apply();
        }

        public void SetMode(StoreMode newMode)
        {
            bool changed = mode != newMode;
            mode = newMode;
            Apply();
            if (changed)
            {
                ModeChanged?.Invoke(mode);
            }
        }

        public void Toggle()
        {
            SetMode(mode == StoreMode.VirtualStore ? StoreMode.MixedReality : StoreMode.VirtualStore);
        }

        [ContextMenu("Show Virtual Store")]
        private void ShowVirtualStore()
        {
            SetMode(StoreMode.VirtualStore);
        }

        [ContextMenu("Show Mixed Reality")]
        private void ShowMixedReality()
        {
            SetMode(StoreMode.MixedReality);
        }

        private void Apply()
        {
            bool virtualStore = mode == StoreMode.VirtualStore;
            if (storeShell != null)
            {
                storeShell.SetActive(virtualStore);
            }

            foreach (GameObject go in hiddenInMixedReality)
            {
                if (go != null)
                {
                    go.SetActive(virtualStore);
                }
            }
        }
    }
}
