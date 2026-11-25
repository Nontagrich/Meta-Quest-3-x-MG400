using UnityEngine;

namespace GemStore.Showcase
{
    /// <summary>
    /// Runtime component on a gem prefab. Holds its <see cref="GemData"/> and swaps the
    /// shared material to match the gem colour (shared materials keep the SRP Batcher happy on Quest).
    /// </summary>
    [DisallowMultipleComponent]
    public class GemItem : MonoBehaviour
    {
        [SerializeField] private GemData data;
        [SerializeField] private MeshRenderer meshRenderer;

        [Tooltip("One material per GemColor, indexed by the enum value: Red, Blue, Green, Yellow, Purple, Pink, White.")]
        [SerializeField] private Material[] colorMaterials = new Material[0];

        [Tooltip("Showcase slot this gem was spawned on. Used to return the gem there after it is released.")]
        [SerializeField] private Transform homeSlot;

        public GemData Data => data;

        /// <summary>Cabinet slot number 1..9 (Slot_0 = 1) = the robot's physical pick pose number; 0 if not set by a showcase.</summary>
        public int SlotNumber { get; private set; }

        public void SetSlotNumber(int slotNumber)
        {
            SlotNumber = slotNumber;
        }

        /// <summary>The slot this gem came from, or null if it was not spawned by a showcase.</summary>
        public Transform HomeSlot => homeSlot;

        public void SetHomeSlot(Transform slot)
        {
            homeSlot = slot;
        }

        private void Reset()
        {
            meshRenderer = GetComponent<MeshRenderer>();
        }

        private void Awake()
        {
            if (data != null)
            {
                ApplyData(data);
            }
        }

        /// <summary>Assigns the data and updates the renderer material to the matching colour.</summary>
        public void ApplyData(GemData newData)
        {
            data = newData;
            if (newData == null)
            {
                return;
            }

            if (meshRenderer == null)
            {
                meshRenderer = GetComponent<MeshRenderer>();
            }

            int index = (int)newData.GemColor;
            bool hasMaterial = meshRenderer != null
                               && colorMaterials != null
                               && index >= 0
                               && index < colorMaterials.Length
                               && colorMaterials[index] != null;

            if (!hasMaterial)
            {
                Debug.LogWarning($"GemItem '{name}': no material assigned for colour {newData.GemColor}.", this);
                return;
            }

            meshRenderer.sharedMaterial = colorMaterials[index];
        }
    }
}
