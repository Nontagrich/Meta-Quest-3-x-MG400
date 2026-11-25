using System.Collections.Generic;
using UnityEngine;

namespace CPE494
{
    /// <summary>
    /// "Pick the fallen gem" scenario (requirement 4.b.i): gems dropped at
    /// unplanned random spots inside the cell, each marked with an AR label
    /// (tag/quad above it, as the spec allows).  When the MG400 TCP with the
    /// vacuum ON comes close, the gem attaches to the suction cup; releasing
    /// the vacuum over the tray returns it to storage.
    /// </summary>
    public class FallenGemSpawner : MonoBehaviour
    {
        public static FallenGemSpawner Instance { get; private set; }

        [Header("Spawn area (world space box)")]
        public Transform areaCenter;
        public Vector3 areaExtents = new Vector3(0.5f, 0.05f, 0.4f);
        public int gemCount = 3;

        [Header("Pick")]
        public float suctionRadius = 0.05f;      // metres from TCP
        public Transform gemPrefabSource;        // a gem-sized visual (scene-built)
        public Transform dropReturnTarget;       // gem storage tray root

        private readonly List<Transform> _fallen = new List<Transform>();
        private Transform _carried;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Start()
        {
            SpawnGems();
        }

        public void SpawnGems()
        {
            ClearGems();
            if (gemPrefabSource == null || areaCenter == null) return;
            for (int i = 0; i < gemCount; i++)
            {
                Vector3 local = new Vector3(
                    Random.Range(-areaExtents.x, areaExtents.x),
                    areaExtents.y,
                    Random.Range(-areaExtents.z, areaExtents.z));
                var gem = Instantiate(gemPrefabSource, areaCenter);
                gem.localPosition = local;
                gem.localRotation = Random.rotation;
                gem.gameObject.name = $"FallenGem_{i + 1}";
                _fallen.Add(gem);
            }
            Debug.Log($"[FallenGem] spawned {gemCount} gems at random positions");
        }

        public void ClearGems()
        {
            foreach (var g in _fallen)
                if (g != null) Destroy(g.gameObject);
            _fallen.Clear();
            _carried = null;
        }

        /// <summary>Called by TeleopStation each frame with the current TCP pose.</summary>
        public void UpdateVacuum(Vector3 tcpPos, bool vacuumOn)
        {
            if (vacuumOn && _carried == null)
            {
                // pick the closest gem within suction radius
                Transform best = null;
                float bestDist = suctionRadius;
                foreach (var g in _fallen)
                {
                    if (g == null || !g.gameObject.activeSelf) continue;
                    float d = Vector3.Distance(g.position, tcpPos);
                    if (d < bestDist) { bestDist = d; best = g; }
                }
                if (best != null) AttachToCup(best, tcpPos);
            }
            else if (!vacuumOn && _carried != null)
            {
                DropCarried();
            }
        }

        private void AttachToCup(Transform gem, Vector3 tcpPos)
        {
            _carried = gem;
            var twin = MG400DigitalTwin.Instance;
            if (twin != null && twin.tcpMarker != null)
            {
                gem.SetParent(twin.tcpMarker, worldPositionStays: true);
                gem.position = tcpPos;
            }
            Debug.Log($"[FallenGem] picked '{gem.name}' at {tcpPos:F2}");
        }

        private void DropCarried()
        {
            var gem = _carried;
            _carried = null;
            if (gem == null) return;
            gem.SetParent(areaCenter, worldPositionStays: true);

            // drop over the storage tray? -> restock it away; otherwise stay put
            if (dropReturnTarget != null &&
                Vector3.Distance(gem.position, dropReturnTarget.position) < 0.25f)
            {
                GemStorage.Instance?.RestockAll();
                gem.gameObject.SetActive(false);
                Debug.Log($"[FallenGem] '{gem.name}' returned to storage");
            }
        }

        public int RemainingCount()
        {
            int n = 0;
            foreach (var g in _fallen)
                if (g != null && g.gameObject.activeSelf) n++;
            return n;
        }
    }
}
