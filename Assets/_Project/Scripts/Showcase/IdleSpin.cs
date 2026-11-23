using UnityEngine;

namespace GemStore.Showcase
{
    /// <summary>
    /// Slowly spins an object around its local Y axis and bobs it up and down a few millimetres.
    /// Enable/disable the component (or call <see cref="SetActive"/>) to start or stop the motion.
    /// When re-enabled, the current local position becomes the new resting position.
    /// </summary>
    [DisallowMultipleComponent]
    public class IdleSpin : MonoBehaviour
    {
        [SerializeField, Tooltip("Spin speed around the local Y axis, in degrees per second.")]
        private float degreesPerSecond = 20f;

        [SerializeField, Tooltip("Vertical bob amplitude in metres.")]
        private float bobAmplitude = 0.004f;

        [SerializeField, Tooltip("Bob cycles per second.")]
        private float bobFrequency = 0.4f;

        [SerializeField, Tooltip("Start each instance at a random point of the bob cycle so a row of gems does not move in lockstep.")]
        private bool randomizePhase = true;

        private Vector3 restLocalPosition;
        private float phase;

        public bool IsActive => enabled;

        public void SetActive(bool active)
        {
            enabled = active;
        }

        private void Awake()
        {
            phase = randomizePhase ? Random.value * Mathf.PI * 2f : 0f;
        }

        private void OnEnable()
        {
            restLocalPosition = transform.localPosition;
        }

        private void Update()
        {
            transform.Rotate(0f, degreesPerSecond * Time.deltaTime, 0f, Space.Self);

            Vector3 position = restLocalPosition;
            position.y += Mathf.Sin(Time.time * bobFrequency * Mathf.PI * 2f + phase) * bobAmplitude;
            transform.localPosition = position;
        }
    }
}
