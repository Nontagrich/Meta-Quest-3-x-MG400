using System.Collections.Generic;
using UnityEngine;

namespace GemStore.Showcase
{
    /// <summary>
    /// Fills the cabinet slots with gems on Start. Slot i receives gems[i], spawned from the prefab matching its shape.
    /// Each spawned <see cref="GemItem"/> remembers its slot so it can be returned there later.
    /// </summary>
    public class GemShowcase : MonoBehaviour
    {
        public const int SlotCount = 9;

        [SerializeField] private GemData[] gems = new GemData[SlotCount];
        [SerializeField] private Transform[] slots = new Transform[SlotCount];
        [SerializeField] private GemItem roundPrefab;
        [SerializeField] private GemItem emeraldPrefab;

        [SerializeField, Tooltip("Spin and bob the gems on their pedestals.")]
        private bool idleSpin = true;

        [SerializeField, Tooltip("Info panel prefab. One shared panel is created at Start if the scene has none.")]
        private GemInfoPanel infoPanelPrefab;

        private readonly GemItem[] spawned = new GemItem[SlotCount];

        private static readonly List<GemShowcase> Active = new List<GemShowcase>();

        private void OnEnable()
        {
            Active.Add(this);
        }

        private void OnDisable()
        {
            Active.Remove(this);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Active.Clear();
        }

        /// <summary>
        /// The cabinet slot number (1..9) that holds <paramref name="gem"/>: Slot_0 = 1 ... Slot_8 = 9, which is the physical
        /// pick pose number of the robot. 0 when the gem is not in any cabinet.
        /// </summary>
        public static int SlotNumberOf(GemData gem)
        {
            if (gem == null)
            {
                return 0;
            }

            foreach (GemShowcase showcase in Active)
            {
                for (int i = 0; i < showcase.gems.Length; i++)
                {
                    if (showcase.gems[i] == gem)
                    {
                        return i + 1;
                    }
                }
            }

            return 0;
        }

        public GemItem GetItem(int slotIndex)
        {
            return slotIndex >= 0 && slotIndex < spawned.Length ? spawned[slotIndex] : null;
        }

        private void OnValidate()
        {
            if (gems.Length != SlotCount)
            {
                System.Array.Resize(ref gems, SlotCount);
            }

            if (slots.Length != SlotCount)
            {
                System.Array.Resize(ref slots, SlotCount);
            }
        }

        private void Start()
        {
            if (infoPanelPrefab != null && GemInfoPanel.Instance == null)
            {
                Instantiate(infoPanelPrefab); // the single shared panel: shown/hidden, never re-created
            }

            Populate();
        }

        /// <summary>Destroys any gems spawned earlier and spawns a fresh set into the slots.</summary>
        public void Populate()
        {
            Clear();

            int count = Mathf.Min(gems.Length, slots.Length);
            for (int i = 0; i < count; i++)
            {
                GemData data = gems[i];
                Transform slot = slots[i];
                if (data == null || slot == null)
                {
                    Debug.LogWarning($"GemShowcase '{name}': slot {i} is missing its gem data or slot transform.", this);
                    continue;
                }

                GemItem prefab = data.Shape == GemShape.Round ? roundPrefab : emeraldPrefab;
                if (prefab == null)
                {
                    Debug.LogWarning($"GemShowcase '{name}': no prefab assigned for shape {data.Shape}.", this);
                    continue;
                }

                GemItem item = Instantiate(prefab, slot);
                item.transform.localPosition = Vector3.zero;
                item.transform.localRotation = Quaternion.identity;
                item.name = data.DisplayName;
                item.ApplyData(data);
                item.SetHomeSlot(slot);
                item.SetSlotNumber(i + 1);

                IdleSpin spin = item.GetComponent<IdleSpin>();
                if (spin != null)
                {
                    spin.SetActive(idleSpin);
                }

                spawned[i] = item;
            }
        }

        private void Clear()
        {
            for (int i = 0; i < spawned.Length; i++)
            {
                if (spawned[i] != null)
                {
                    Destroy(spawned[i].gameObject);
                    spawned[i] = null;
                }
            }
        }
    }
}
