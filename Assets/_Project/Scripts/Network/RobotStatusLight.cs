using UnityEngine;

namespace GemStore.Network
{
    /// <summary>Small status lamp on the counter: green = robot link up, red = down. Handy during a demo.</summary>
    [DisallowMultipleComponent]
    public class RobotStatusLight : MonoBehaviour
    {
        [SerializeField] private MeshRenderer lamp;
        [SerializeField] private Material connectedMaterial;
        [SerializeField] private Material disconnectedMaterial;

        private RobotLink link;

        private void OnEnable()
        {
            Apply(false);
            Attach();
        }

        private void Start()
        {
            Attach();
        }

        private void OnDisable()
        {
            if (link != null)
            {
                link.OnConnectionChanged -= Apply;
                link = null;
            }
        }

        private void Attach()
        {
            if (link != null || RobotLink.Instance == null)
            {
                return;
            }

            link = RobotLink.Instance;
            link.OnConnectionChanged += Apply;
            Apply(link.IsConnected);
        }

        private void Apply(bool connected)
        {
            if (lamp != null)
            {
                lamp.sharedMaterial = connected ? connectedMaterial : disconnectedMaterial;
            }
        }
    }
}
