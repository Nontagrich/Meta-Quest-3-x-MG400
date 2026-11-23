using System.Collections;
using System.Collections.Generic;
using GemStore.Payment;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace GemStore.Showcase
{
    /// <summary>
    /// The single shared info panel (World Space canvas). It appears beside the held gem and its jewelry preview (on the user's right,
    /// or the left when the right would be behind the user), follows it smoothly and faces the user around the Y axis.
    /// After release it lingers so a button can still be pressed, never hides while a ray / poke is on it, and hides at once when the
    /// gem is back in its slot (nothing on the panel), another gem is grabbed, the payment starts or ✕ is pressed.
    /// </summary>
    /// <remarks>
    /// Hover is read every frame from the XR interactors' UI models instead of counting pointer enter/exit events: a missed exit
    /// (an interactor that is switched off while it points at the panel, controller ↔ hand switch) used to leave the old counter
    /// above zero for good, and the panel then never hid again.
    /// </remarks>
    [DisallowMultipleComponent]
    public class GemInfoPanel : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Canvas canvas;
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private TMP_Text nameLabel;
        [SerializeField] private TMP_Text priceLabel;
        [SerializeField] private AddToCartButton addButton;
        [SerializeField, Tooltip("Setting / metal selector and live price line. Optional: without it the panel shows only the gem price.")]
        private GemStore.Customize.GemCustomizePanel customize;
        [SerializeField, Tooltip("Small ✕ button that closes the panel.")] private Button closeButton;

        [Header("Placement")]
        [SerializeField, Min(0f), Tooltip("Gap between the edge of the gem + jewelry bounds and the panel edge, metres.")]
        private float margin = 0.08f;
        [SerializeField, Min(0f), Tooltip("The side is only used if the panel centre is at least this far in front of the eyes, metres (not beside / behind the user).")]
        private float minForward = 0.05f;
        [SerializeField, Range(10f, 90f), Tooltip("The side is only used if the panel is within this angle from the view direction.")]
        private float maxSideAngle = 70f;
        [SerializeField, Min(0.01f), Tooltip("Smoothing of the panel position while the gem moves, seconds.")]
        private float followSmoothTime = 0.08f;
        [SerializeField, Min(0.01f), Tooltip("How fast (m/s) the reserved width shrinks back when the piece gets narrower; it grows at once.")]
        private float extentRelaxSpeed = 0.15f;

        [Header("Timing")]
        [SerializeField, Min(0f), Tooltip("Seconds the panel stays after the gem is released (while it floats home), so a button can be pressed.")]
        private float lingerTime = 2f;
        [SerializeField, Min(0f), Tooltip("A pointer that just left the panel still counts as on it for this long (moving between buttons).")]
        private float hoverGrace = 0.25f;
        [SerializeField, Min(0.01f)] private float fadeInDuration = 0.12f;
        [SerializeField, Min(0.01f)] private float fadeOutDuration = 0.3f;

        [Header("Close animation (gem back in its slot)")]
        [SerializeField, Min(0.01f), Tooltip("Seconds the panel takes to shrink into the gem once the gem is back in its slot.")]
        private float closeDuration = 0.35f;
        [SerializeField, Range(0.05f, 1f), Tooltip("Panel size at the end of the close animation (1 = full size).")]
        private float closeEndScale = 0.15f;
        [SerializeField, Range(0f, 1f), Tooltip("How far the panel travels toward the gem while it closes (0 = stays, 1 = ends on the gem).")]
        private float closeTravel = 0.85f;
        [SerializeField, Range(0.5f, 1f), Tooltip("Size the panel pops in from when it appears.")]
        private float openStartScale = 0.85f;

        private readonly List<IXRInteractor> interactors = new List<IXRInteractor>();
        private readonly List<Renderer> targetRenderers = new List<Renderer>();
        private readonly List<XRGrabInteractable> cardGrabs = new List<XRGrabInteractable>();

        private Camera viewCamera;
        private XRInteractionManager interactionManager;
        private Coroutine fadeRoutine;
        private bool fadingOut;
        private bool closing;
        private GemItem target;
        private bool following;
        private bool released;
        private bool targetHome;
        private float lingerUntil = -1f;
        private float lastHoverTime = -10f;
        private float reservedExtent;
        private int side = 1; // +1 right of the user, -1 left
        private Vector3 velocity;
        private bool subscribedToCart;
        private Vector3 baseScale = Vector3.one;
        private float scaleFactor = 1f;
        private Vector3 restPosition;   // where the panel was before a close animation moved it
        private bool movedByClose;

        /// <summary>True while the "back in the slot" close animation is playing.</summary>
        public bool IsClosingToGem { get; private set; }

        /// <summary>Current size of the panel relative to its normal size (for tests).</summary>
        public float ScaleFactor => scaleFactor;

        public static GemInfoPanel Instance { get; private set; }

        /// <summary>True while the panel is on screen (including while it fades).</summary>
        public bool IsVisible => canvas != null && canvas.enabled;

        /// <summary>The gem the panel currently shows.</summary>
        public GemItem Target => target;

        public float Alpha => canvasGroup != null ? canvasGroup.alpha : 0f;

        /// <summary>True while a ray or poke points at (or presses) the panel.</summary>
        public bool IsPointerOnPanel { get; private set; }

        /// <summary>+1 when the panel is on the user's right of the piece, -1 on the left.</summary>
        public int Side => side;

        /// <summary>For tests: replaces the interactor hover check.</summary>
        public System.Func<bool> HoverOverride { get; set; }

        /// <summary>Combined world bounds of the shown gem and its jewelry preview (the last ones used for placement).</summary>
        public Bounds PieceBounds { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject); // only ever one panel
                return;
            }

            Instance = this;
            baseScale = transform.localScale;
            canvasGroup.alpha = 0f;
            SetVisible(false);
        }

        private void OnEnable()
        {
            if (closeButton != null)
            {
                closeButton.onClick.AddListener(Close);
            }
        }

        private void Start()
        {
            if (CartManager.HasInstance || FindAnyObjectByType<CartManager>() != null)
            {
                CartManager.Instance.OnLockChanged += OnCartLockChanged;
                subscribedToCart = true;
            }

            foreach (CreditCard card in FindObjectsByType<CreditCard>(FindObjectsInactive.Include))
            {
                var grab = card.GetComponent<XRGrabInteractable>();
                if (grab != null)
                {
                    grab.selectEntered.AddListener(OnCardGrabbed);
                    cardGrabs.Add(grab);
                }
            }
        }

        private void OnDisable()
        {
            if (closeButton != null)
            {
                closeButton.onClick.RemoveListener(Close);
            }
        }

        private void OnDestroy()
        {
            if (subscribedToCart && CartManager.HasInstance)
            {
                CartManager.Instance.OnLockChanged -= OnCartLockChanged;
            }

            foreach (XRGrabInteractable grab in cardGrabs)
            {
                if (grab != null)
                {
                    grab.selectEntered.RemoveListener(OnCardGrabbed);
                }
            }

            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Called by the gems
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Shows the panel for a gem that has just been picked up (switches to it if another gem was shown).</summary>
        public void Show(GemItem gem)
        {
            if (gem == null || gem.Data == null)
            {
                return;
            }

            bool wasShowingThis = IsVisible && target == gem && !fadingOut;
            target = gem;
            following = true;
            released = false;
            closing = false;
            targetHome = false;
            lingerUntil = -1f;
            CacheRenderers();

            GemData data = gem.Data;
            nameLabel.text = data.DisplayName;
            priceLabel.text = PriceFormatter.Format(data.Price, priceLabel.font);
            if (customize != null)
            {
                customize.Bind(gem); // sets the price line and the Add to Cart button for the chosen setting
            }
            else
            {
                addButton.SetGem(data);
            }

            if (canvas.worldCamera == null)
            {
                canvas.worldCamera = GetCamera();
            }

            SetVisible(true);
            if (!wasShowingThis)
            {
                StopFade();                  // e.g. a close animation of the previous gem
                movedByClose = false;
                SetScale(openStartScale);    // pops in from slightly smaller
            }

            UpdatePose(!wasShowingThis); // a new gem: snap, no glide from the old place
            StartFade(1f, fadeInDuration, false);
        }

        /// <summary>The gem was let go: the panel stays where it is for the linger time, then fades out.</summary>
        public void Release(GemItem gem)
        {
            if (gem != target || !IsVisible)
            {
                return;
            }

            following = false;
            released = true;
            lingerUntil = Time.time + lingerTime;
        }

        /// <summary>The released gem is back in its slot: hide now unless a pointer is on the panel.</summary>
        public void NotifyReturnedHome(GemItem gem)
        {
            if (gem != target || !IsVisible || following)
            {
                return;
            }

            targetHome = true;
        }

        /// <summary>✕ button: fades out now, until the next gem is picked up.</summary>
        public void Close()
        {
            if (IsVisible)
            {
                following = false;
                released = true;
                closing = true; // the pointer pressing ✕ is on the panel: do not let the hover keep it
                StartFade(0f, fadeOutDuration * 0.5f, true);
            }
        }

        public void HideImmediately()
        {
            StopFade();
            ClearTarget();
            canvasGroup.alpha = 0f;
            SetVisible(false);
            SetScale(1f);
            movedByClose = false;
        }

        // ------------------------------------------------------------------------------------------------------

        private void OnCartLockChanged()
        {
            if (CartManager.HasInstance && CartManager.Instance.IsLocked)
            {
                HideImmediately(); // payment is being processed
            }
        }

        private void OnCardGrabbed(SelectEnterEventArgs args)
        {
            HideImmediately(); // the customer goes to pay
        }

        private void LateUpdate()
        {
            if (!IsVisible)
            {
                return;
            }

            // never left behind: the gem was destroyed (showcase refilled, scene change) or switched off
            if (target == null || !target.isActiveAndEnabled)
            {
                HideImmediately();
                return;
            }

            IsPointerOnPanel = HoverOverride != null ? HoverOverride() : CheckPointerOnPanel();
            if (IsPointerOnPanel)
            {
                lastHoverTime = Time.time;
            }

            if (following)
            {
                UpdatePose(false);
                return;
            }

            if (!released || closing)
            {
                return;
            }

            bool hovered = Time.time - lastHoverTime <= hoverGrace;
            if (hovered)
            {
                if (fadingOut)
                {
                    StartFade(1f, fadeInDuration, false); // a pointer came back during the fade: keep the panel
                }

                return;
            }

            if (fadingOut)
            {
                return;
            }

            if (targetHome)
            {
                StartCloseToGem(); // shrink into the gem that is back in its slot, then hide
            }
            else if (lingerUntil >= 0f && Time.time >= lingerUntil)
            {
                StartFade(0f, fadeOutDuration, true);
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Hover
        // ------------------------------------------------------------------------------------------------------

        private bool CheckPointerOnPanel()
        {
            if (interactionManager == null)
            {
                interactionManager = FindAnyObjectByType<XRInteractionManager>();
                if (interactionManager == null)
                {
                    return false;
                }
            }

            interactors.Clear();
            interactionManager.GetRegisteredInteractors(interactors);
            foreach (IXRInteractor interactor in interactors)
            {
                if (interactor is IUIInteractor uiInteractor && uiInteractor.TryGetUIModel(out TrackedDeviceModel model))
                {
                    GameObject hit = model.currentRaycast.gameObject;
                    if (hit != null && hit.transform.IsChildOf(transform))
                    {
                        return true; // hovering or pressing (a press always comes with a hit on the panel)
                    }
                }
            }

            return false;
        }

        // ------------------------------------------------------------------------------------------------------
        // Placement
        // ------------------------------------------------------------------------------------------------------

        private void CacheRenderers()
        {
            targetRenderers.Clear();
            if (target != null)
            {
                target.GetComponentsInChildren(true, targetRenderers); // gem + JewelryPreview setting (ring / pendant with chain)
            }
        }

        private bool TryGetPieceBounds(out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Renderer renderer in targetRenderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer is ParticleSystemRenderer)
                {
                    continue;
                }

                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return any;
        }

        private void UpdatePose(bool snap)
        {
            Camera cam = GetCamera();
            if (cam == null || target == null)
            {
                return;
            }

            Vector3 eye = cam.transform.position;
            Vector3 forward = Flat(cam.transform.forward, Vector3.forward);

            if (!TryGetPieceBounds(out Bounds bounds))
            {
                bounds = new Bounds(target.transform.position, Vector3.one * 0.03f);
            }

            PieceBounds = bounds;

            // "Right" as seen from the eye toward the piece (tangent), not the camera's right: the panel is billboarded to the eye,
            // so off to the side it spans along this direction. Equal to the camera's right when the piece is straight ahead.
            Vector3 right = Flat(Vector3.Cross(Vector3.up, Flat(bounds.center - eye, forward)), Flat(cam.transform.right, Vector3.right));

            // half width of the piece along the user's right (axis-aligned bounds seen from the user)
            float extent = Mathf.Abs(bounds.extents.x * right.x) + Mathf.Abs(bounds.extents.z * right.z);
            reservedExtent = snap || extent > reservedExtent ? extent : Mathf.MoveTowards(reservedExtent, extent, extentRelaxSpeed * Time.deltaTime);

            float offset = reservedExtent + margin + HalfWidth();
            Vector3 center = bounds.center;
            Vector3 rightPos = center + right * offset;
            Vector3 leftPos = center - right * offset;

            bool rightOk = SideUsable(rightPos, eye, forward);
            bool leftOk = SideUsable(leftPos, eye, forward);
            if (snap)
            {
                side = rightOk || !leftOk ? 1 : -1;
            }
            else if (side > 0 && !rightOk && leftOk)
            {
                side = -1;
            }
            else if (side < 0 && rightOk)
            {
                side = 1; // back to the preferred side as soon as it fits
            }

            if (!rightOk && !leftOk)
            {
                side = ViewAngle(rightPos, eye, forward) <= ViewAngle(leftPos, eye, forward) ? 1 : -1; // the one nearer the view direction
            }

            Vector3 goal = side > 0 ? rightPos : leftPos;
            Vector3 position = snap ? goal : Vector3.SmoothDamp(transform.position, goal, ref velocity, followSmoothTime);
            if (snap)
            {
                velocity = Vector3.zero;
            }
            else
            {
                // Smooth inward, never late outward: if the piece grew or moved toward the panel faster than the smoothing,
                // push the panel out at once so it never covers the piece.
                Vector3 sideDir = right * side;
                float deficit = offset - Vector3.Dot(position - center, sideDir);
                if (deficit > 0f)
                {
                    position += sideDir * deficit;
                }
            }

            // Billboard around Y only: the canvas faces the user (its forward points away from the user).
            Vector3 away = Flat(position - eye, forward);
            transform.SetPositionAndRotation(position, Quaternion.LookRotation(away, Vector3.up));
        }

        private bool SideUsable(Vector3 position, Vector3 eye, Vector3 forward)
        {
            Vector3 toPanel = position - eye;
            toPanel.y = 0f;
            float ahead = Vector3.Dot(toPanel, forward);
            if (ahead < minForward)
            {
                return false; // beside or behind the user
            }

            return Vector3.Angle(forward, toPanel) <= maxSideAngle;
        }

        private static float ViewAngle(Vector3 position, Vector3 eye, Vector3 forward)
        {
            Vector3 toPanel = position - eye;
            toPanel.y = 0f;
            return Vector3.Angle(forward, toPanel);
        }

        private float HalfWidth()
        {
            var rect = transform as RectTransform;
            return rect != null ? rect.rect.width * Mathf.Abs(baseScale.x) * 0.5f /* full size, not the animated size */ : 0.09f;
        }

        private static Vector3 Flat(Vector3 v, Vector3 fallback)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : fallback;
        }

        private Camera GetCamera()
        {
            if (viewCamera == null)
            {
                viewCamera = Camera.main;
            }

            return viewCamera;
        }

        // ------------------------------------------------------------------------------------------------------
        // Visibility
        // ------------------------------------------------------------------------------------------------------

        private void ClearTarget()
        {
            target = null;
            following = false;
            released = false;
            closing = false;
            targetHome = false;
            lingerUntil = -1f;
            lastHoverTime = -10f;
            IsPointerOnPanel = false;
            targetRenderers.Clear();
        }

        private void SetVisible(bool visible)
        {
            canvas.enabled = visible;
            canvasGroup.blocksRaycasts = visible;
            canvasGroup.interactable = visible;
        }

        private void StopFade()
        {
            if (fadeRoutine != null)
            {
                StopCoroutine(fadeRoutine);
                fadeRoutine = null;
            }

            fadingOut = false;
            IsClosingToGem = false;
        }

        private void StartFade(float to, float duration, bool hideWhenDone)
        {
            // fade in: back to full size (and back to where it was if a close had moved it); fade out: shrink a little
            float toScale = to > 0f ? 1f : 0.9f;
            Vector3? toPosition = to > 0f && movedByClose ? restPosition : (Vector3?)null;
            if (to > 0f)
            {
                movedByClose = false;
            }

            StartAnimation(to, toScale, toPosition, duration, hideWhenDone, false);
        }

        /// <summary>The gem is back in its slot: the panel shrinks and slides into the gem while it fades, then hides.</summary>
        private void StartCloseToGem()
        {
            Vector3 from = transform.position;
            Vector3 gem = target != null ? target.transform.position : from;
            restPosition = from;
            movedByClose = true;
            StartAnimation(0f, closeEndScale, Vector3.Lerp(from, gem, closeTravel), closeDuration, true, true);
        }

        private void StartAnimation(float toAlpha, float toScale, Vector3? toPosition, float duration, bool hideWhenDone, bool closeToGem)
        {
            StopFade();
            fadingOut = hideWhenDone;
            IsClosingToGem = closeToGem;
            fadeRoutine = StartCoroutine(Animate(toAlpha, toScale, toPosition, duration, hideWhenDone, closeToGem));
        }

        private IEnumerator Animate(float toAlpha, float toScale, Vector3? toPosition, float duration, bool hideWhenDone, bool closeToGem)
        {
            float fromAlpha = canvasGroup.alpha;
            float fromScale = scaleFactor;
            Vector3 fromPosition = transform.position;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                // closing: ease-in (starts gently, then pulls into the gem); otherwise ease-out
                float eased = closeToGem ? t * t * t : 1f - (1f - t) * (1f - t);
                // closing keeps the panel readable for the first part, then fades
                float alphaT = closeToGem ? Mathf.Clamp01((t - 0.3f) / 0.7f) : t;
                canvasGroup.alpha = Mathf.Lerp(fromAlpha, toAlpha, alphaT);
                SetScale(Mathf.Lerp(fromScale, toScale, eased));
                if (toPosition.HasValue)
                {
                    transform.position = Vector3.Lerp(fromPosition, toPosition.Value, eased);
                }

                yield return null;
            }

            canvasGroup.alpha = toAlpha;
            SetScale(toScale);
            if (toPosition.HasValue)
            {
                transform.position = toPosition.Value;
            }

            fadeRoutine = null;
            fadingOut = false;
            IsClosingToGem = false;
            if (hideWhenDone)
            {
                ClearTarget();
                SetVisible(false);
                SetScale(1f);
                movedByClose = false;
            }
        }

        private void SetScale(float factor)
        {
            scaleFactor = factor;
            transform.localScale = baseScale * factor;
        }
    }
}
