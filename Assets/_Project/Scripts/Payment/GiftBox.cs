using UnityEngine;

namespace GemStore.Payment
{
    /// <summary>
    /// A virtual gift box built from primitives. It pops up with a small overshoot, bounces gently while it is
    /// shown, and shrinks away when hidden.
    /// </summary>
    [DisallowMultipleComponent]
    public class GiftBox : MonoBehaviour
    {
        private enum Phase
        {
            Hidden,
            Popping,
            Shown,
            Hiding
        }

        [SerializeField, Tooltip("The part that is scaled and moved by the animation.")]
        private Transform visual;
        [SerializeField] private AudioSource audioSource;
        [SerializeField, Tooltip("Placeholder pop sound.")] private AudioClip popClip;

        [Header("Animation")]
        [SerializeField, Min(0.05f)] private float popDuration = 0.6f;
        [SerializeField, Min(0.05f)] private float hideDuration = 0.25f;
        [SerializeField, Min(0f), Tooltip("Height of the idle bounce, in metres.")] private float bounceHeight = 0.008f;
        [SerializeField, Min(0f), Tooltip("Idle bounce speed, in bounces per second.")] private float bounceFrequency = 1.4f;
        [SerializeField, Min(0f), Tooltip("Squash and stretch of the idle bounce (0.03 = 3%).")] private float squash = 0.03f;

        private Phase phase = Phase.Hidden;
        private float timer;
        private Vector3 restPosition;

        public bool IsShown => phase != Phase.Hidden;
        public float HideDuration => hideDuration;

        /// <summary>Current uniform-ish scale of the visual (for tests).</summary>
        public float CurrentScale => visual != null ? visual.localScale.x : 0f;

        private void Awake()
        {
            if (visual == null && transform.childCount > 0)
            {
                visual = transform.GetChild(0);
            }

            restPosition = visual.localPosition;
            visual.localScale = Vector3.zero;
        }

        public void Show()
        {
            gameObject.SetActive(true);
            phase = Phase.Popping;
            timer = 0f;
            visual.localScale = Vector3.zero;

            if (audioSource != null && popClip != null)
            {
                audioSource.PlayOneShot(popClip);
            }
        }

        public void Hide()
        {
            if (phase == Phase.Hidden || phase == Phase.Hiding)
            {
                return;
            }

            phase = Phase.Hiding;
            timer = 0f;
        }

        public void HideImmediate()
        {
            phase = Phase.Hidden;
            if (visual != null)
            {
                visual.localScale = Vector3.zero;
                visual.localPosition = restPosition;
            }

            gameObject.SetActive(false);
        }

        private void Update()
        {
            switch (phase)
            {
                case Phase.Popping:
                    timer += Time.deltaTime;
                    float t = Mathf.Clamp01(timer / popDuration);
                    float s = EaseOutBack(t);
                    visual.localScale = Vector3.one * s;
                    visual.localPosition = restPosition + Vector3.down * (1f - t) * 0.03f;
                    if (t >= 1f)
                    {
                        phase = Phase.Shown;
                        timer = 0f;
                    }

                    break;

                case Phase.Shown:
                    timer += Time.deltaTime;
                    float wave = Mathf.Abs(Mathf.Sin(timer * Mathf.PI * bounceFrequency)); // 0..1 hop
                    visual.localPosition = restPosition + Vector3.up * wave * bounceHeight;
                    float stretch = 1f + (wave - 0.5f) * 2f * squash;
                    visual.localScale = new Vector3(1f / Mathf.Sqrt(stretch), stretch, 1f / Mathf.Sqrt(stretch));
                    break;

                case Phase.Hiding:
                    timer += Time.deltaTime;
                    float h = Mathf.Clamp01(timer / hideDuration);
                    visual.localScale = Vector3.one * (1f - h * h);
                    if (h >= 1f)
                    {
                        HideImmediate();
                    }

                    break;
            }
        }

        private static float EaseOutBack(float t)
        {
            const float c1 = 1.2f;
            const float c3 = c1 + 1f;
            float x = t - 1f;
            return 1f + c3 * x * x * x + c1 * x * x;
        }
    }
}
