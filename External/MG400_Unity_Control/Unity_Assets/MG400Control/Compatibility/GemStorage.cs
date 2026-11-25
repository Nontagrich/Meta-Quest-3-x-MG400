using System;
using System.Collections.Generic;
using UnityEngine;

namespace CPE494
{
    /// <summary>
    /// Gem storage tray next to the MG400 (requirement 5.b): nine slots,
    /// one per catalog gem.  The packing sequence asks for a gem by
    /// shape/color and receives its GameObject.
    /// </summary>
    public class GemStorage : MonoBehaviour
    {
        private static GemStorage _instance;
        public static GemStorage Instance
        {
            get
            {
                if (_instance == null) _instance = FindAnyObjectByType<GemStorage>();
                return _instance;
            }
            private set => _instance = value;
        }

        [Serializable]
        public class Slot
        {
            public GemInfo info;
            public Transform anchor;          // rest position on the tray
            public GameObject gemObject;      // the visual gem instance
            public bool available = true;
        }

        public List<Slot> slots = new List<Slot>();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public Slot FindSlot(GemInfo info)
        {
            foreach (var s in slots)
                if (s.info == info) return s;
            return null;
        }

        public bool IsAvailable(GemInfo info)
        {
            var s = FindSlot(info);
            return s != null && s.available;
        }

        /// <summary>World position of the gem's rest pose.</summary>
        public Vector3 GemWorldPos(GemInfo info)
        {
            var s = FindSlot(info);
            return s != null && s.gemObject != null
                ? s.gemObject.transform.position
                : s != null && s.anchor != null ? s.anchor.position : transform.position;
        }

        /// <summary>Take the gem out of the tray (marks slot empty).</summary>
        public GameObject TakeGem(GemInfo info)
        {
            var s = FindSlot(info);
            if (s == null || !s.available || s.gemObject == null) return null;
            s.available = false;
            return s.gemObject;
        }

        /// <summary>Restock all gems (called between demo rounds).</summary>
        public void RestockAll()
        {
            foreach (var s in slots)
            {
                if (s.gemObject == null) continue;
                s.available = true;
                s.gemObject.SetActive(true);
                if (s.anchor != null)
                {
                    s.gemObject.transform.SetParent(s.anchor.parent, worldPositionStays: true);
                    s.gemObject.transform.position = s.anchor.position;
                    s.gemObject.transform.rotation = s.anchor.rotation;
                }
            }
        }
    }
}
