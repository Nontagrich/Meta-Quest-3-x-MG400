using System;
using System.Collections.Generic;
using GemStore.Customize;
using GemStore.Showcase;
using UnityEngine;

namespace GemStore.Payment
{
    /// <summary>
    /// Shopping cart singleton. Each gem is unique, so a gem can only be in the cart once. Each entry is a
    /// <see cref="JewelryConfig"/> (gem + setting + metal); adding the same gem with a different configuration replaces the old one.
    /// </summary>
    [DisallowMultipleComponent]
    public class CartManager : MonoBehaviour
    {
        private static CartManager instance;
        private static bool quitting;

        [SerializeField] private List<JewelryConfig> items = new List<JewelryConfig>();
        private readonly List<GemData> gemList = new List<GemData>();

        /// <summary>Raised after the cart contents change (add, remove, clear).</summary>
        public event Action OnCartChanged;

        /// <summary>Raised when the cart is locked or unlocked.</summary>
        public event Action OnLockChanged;

        /// <summary>While locked (a payment is being processed) the cart cannot be changed.</summary>
        public bool IsLocked { get; private set; }

        public void SetLocked(bool locked)
        {
            if (IsLocked == locked)
            {
                return;
            }

            IsLocked = locked;
            OnLockChanged?.Invoke();
        }

        /// <summary>The cart. Finds one in the scene, or creates a "CartManager" object if there is none.</summary>
        public static CartManager Instance
        {
            get
            {
                if (instance == null && !quitting)
                {
                    instance = FindAnyObjectByType<CartManager>();
                    if (instance == null)
                    {
                        instance = new GameObject("CartManager").AddComponent<CartManager>();
                    }
                }

                return instance;
            }
        }

        /// <summary>True when a cart exists. Use in OnDisable/OnDestroy so shutting down does not create one.</summary>
        public static bool HasInstance => instance != null;

        /// <summary>The gems in the cart (without their settings).</summary>
        public IReadOnlyList<GemData> Items
        {
            get
            {
                gemList.Clear();
                foreach (JewelryConfig config in items)
                {
                    gemList.Add(config.gem);
                }

                return gemList;
            }
        }

        /// <summary>The cart entries: gem, setting, metal and price of each piece.</summary>
        public IReadOnlyList<JewelryConfig> Configs => items;

        public int Count => items.Count;

        /// <summary>Sum of the TotalPrice of every piece (gem + setting).</summary>
        public int TotalPrice
        {
            get
            {
                int total = 0;
                foreach (JewelryConfig config in items)
                {
                    total += config.TotalPrice;
                }

                return total;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            instance = null;
            quitting = false;
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(this);
                return;
            }

            instance = this;
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }
        }

        private void OnApplicationQuit()
        {
            quitting = true;
        }

        private int IndexOf(GemData gem)
        {
            if (gem == null)
            {
                return -1;
            }

            return items.FindIndex(item => item.gem == gem || (!string.IsNullOrEmpty(gem.Id) && item.gem != null && item.gem.Id == gem.Id));
        }

        public bool Contains(GemData gem)
        {
            return IndexOf(gem) >= 0;
        }

        /// <summary>The configuration of a gem that is in the cart, or null.</summary>
        public JewelryConfig GetConfig(GemData gem)
        {
            int index = IndexOf(gem);
            return index >= 0 ? items[index] : null;
        }

        /// <summary>Adds a loose gem. Returns false if it is null or already in the cart.</summary>
        public bool Add(GemData gem)
        {
            return gem != null && Add(JewelryConfig.LooseGem(gem));
        }

        /// <summary>
        /// Adds a piece. If the same gem is already in the cart with a different setting or metal, the old entry is replaced.
        /// Returns false when nothing changed (locked, no gem, or exactly the same piece is already in the cart).
        /// </summary>
        public bool Add(JewelryConfig config)
        {
            if (IsLocked || config == null || config.gem == null)
            {
                return false;
            }

            int index = IndexOf(config.gem);
            if (index >= 0)
            {
                if (items[index].SameChoice(config))
                {
                    return false;
                }

                items[index] = config;
            }
            else
            {
                items.Add(config);
            }

            OnCartChanged?.Invoke();
            return true;
        }

        /// <summary>Removes a gem (with its setting). Returns false if it was not in the cart.</summary>
        public bool Remove(GemData gem)
        {
            if (IsLocked || gem == null)
            {
                return false;
            }

            int index = IndexOf(gem);
            if (index < 0)
            {
                return false;
            }

            items.RemoveAt(index);
            OnCartChanged?.Invoke();
            return true;
        }

        public void Clear()
        {
            if (IsLocked || items.Count == 0)
            {
                return;
            }

            items.Clear();
            OnCartChanged?.Invoke();
        }
    }
}
