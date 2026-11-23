using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GemStore.Showcase;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GemStore.Payment
{
    public enum PaymentState
    {
        Idle,
        WaitingCard,
        Processing,
        Success
    }

    /// <summary>
    /// Card terminal. Shows its state on the screen:
    /// Idle (empty cart) -> WaitingCard (total + "Tap your card") -> Processing (card held in the zone for the dwell time,
    /// cart locked) -> Success (receipt, gift box, sparkles, sound; the order is announced through <see cref="OrderEvents"/>) -> Idle.
    /// </summary>
    [DisallowMultipleComponent]
    public class PaymentTerminal : MonoBehaviour
    {
        private const int MaxReceiptItems = 3;

        [Header("Screen")]
        [SerializeField] private TMP_Text headlineLabel;
        [SerializeField] private TMP_Text amountLabel;
        [SerializeField] private TMP_Text receiptLabel;
        [SerializeField] private GameObject progressRoot;
        [SerializeField] private Image progressFill;
        [SerializeField] private Image successIcon;
        [SerializeField] private Image contactlessIcon;

        [Header("Card zone")]
        [SerializeField] private CardTapZone tapZone;

        [Header("Timing (seconds)")]
        [SerializeField, Min(0f), Tooltip("How long the card must stay in the zone to start the payment.")]
        private float dwellSeconds = 0.5f;
        [SerializeField, Min(0.1f)] private float processingSeconds = 1.5f;
        [SerializeField, Min(1f), Tooltip("How long the receipt and gift box stay before the terminal returns to Idle.")]
        private float successSeconds = 6f;
        [SerializeField, Min(0.1f), Tooltip("How long 'Cart is empty' is shown when a card is tapped with an empty cart.")]
        private float emptyNoticeSeconds = 2f;

        [Header("Effects")]
        [SerializeField] private AudioSource audioSource;
        [SerializeField, Tooltip("Placeholder: short beep when the card touches the tap zone.")] private AudioClip beepClip;
        [SerializeField, Tooltip("Placeholder: chime when the payment succeeds.")] private AudioClip successClip;
        [SerializeField] private ParticleSystem sparkles;
        [SerializeField] private GiftBox giftBox;

        [Header("Robot order status")]
        [SerializeField, Min(1f), Tooltip("If the robot does not report any status within this time, the terminal just shows 'Order sent'.")]
        private float statusTimeoutSeconds = 10f;
        [SerializeField, Min(1f), Tooltip("How long the final status (done / error / order sent) stays on the idle screen.")]
        private float finalStatusHoldSeconds = 8f;
        [SerializeField, Min(10f), Tooltip("Safety net: a status line with no news for this long is removed.")]
        private float staleStatusSeconds = 180f;

        [Header("Haptics (on the hand holding the card)")]
        [SerializeField, Range(0f, 1f)] private float tapHapticAmplitude = 0.25f;
        [SerializeField, Min(0f)] private float tapHapticDuration = 0.08f;
        [SerializeField, Range(0f, 1f)] private float successHapticAmplitude = 0.6f;
        [SerializeField, Min(0f)] private float successHapticDuration = 0.35f;

        [Header("Colours")]
        [SerializeField] private Color textColor = new Color(0.96f, 0.97f, 1f);
        [SerializeField] private Color goldColor = new Color(1f, 0.82f, 0.38f);
        [SerializeField] private Color successColor = new Color(0.35f, 0.85f, 0.5f);
        [SerializeField] private Color noticeColor = new Color(1f, 0.65f, 0.3f);

        private readonly HashSet<CreditCard> cardsInZone = new HashSet<CreditCard>();
        private List<GemData> lastBought = new List<GemData>();
        private List<GemStore.Customize.JewelryConfig> lastConfigs = new List<GemStore.Customize.JewelryConfig>();
        private CreditCard payingCard;
        private float dwellTimer;
        private float stateTimer;
        private float noticeTimer;
        private bool requireCardExit;
        private bool boxHideStarted;
        private int lastTotal;
        private int lastOrderNumber;

        // robot order tracking
        private bool trackingActive;
        private bool awaitingFirstStatus;
        private string trackedOrderId;
        private string trackedText;
        private Color trackedColor;
        private float trackingStartTime;
        private float lastStatusTime;
        private float trackedHideAt = -1f;

        public PaymentState State { get; private set; }

        /// <summary>The most recent paid order (null until the first payment).</summary>
        public Order LastOrder { get; private set; }

        /// <summary>The order whose robot status is being shown (null when nothing is tracked).</summary>
        public string TrackedOrderId => trackingActive ? trackedOrderId : null;

        /// <summary>The robot status text currently shown for the tracked order (for tests).</summary>
        public string TrackedStatusText => trackingActive ? trackedText : null;

        /// <summary>True while a "Cart is empty" notice is showing.</summary>
        public bool IsShowingEmptyNotice => noticeTimer > 0f;

        /// <summary>Seconds the card has been in the zone towards the dwell time (for tests).</summary>
        public float DwellTimer => dwellTimer;

        public event Action<PaymentState> OnStateChanged;

        /// <summary>Raised when a payment succeeds: the items that were bought and the amount paid.</summary>
        public event Action<IReadOnlyList<GemData>, int> OnPaymentCompleted;

        private void OnEnable()
        {
            if (tapZone != null)
            {
                tapZone.CardEntered += HandleCardEntered;
                tapZone.CardExited += HandleCardExited;
            }

            CartManager.Instance.OnCartChanged += HandleCartChanged;
            OrderStatusEvents.OnStatusReceived += HandleOrderStatus;
            State = CartManager.Instance.Count > 0 ? PaymentState.WaitingCard : PaymentState.Idle;
            Render();
        }

        private void OnDisable()
        {
            if (tapZone != null)
            {
                tapZone.CardEntered -= HandleCardEntered;
                tapZone.CardExited -= HandleCardExited;
            }

            OrderStatusEvents.OnStatusReceived -= HandleOrderStatus;
            if (CartManager.HasInstance)
            {
                CartManager.Instance.OnCartChanged -= HandleCartChanged;
                if (State == PaymentState.Processing)
                {
                    CartManager.Instance.SetLocked(false);
                }
            }
        }

        private void Update()
        {
            cardsInZone.RemoveWhere(card => card == null || !card.isActiveAndEnabled);
            UpdateTracking();

            switch (State)
            {
                case PaymentState.Idle:
                    if (noticeTimer > 0f)
                    {
                        noticeTimer -= Time.deltaTime;
                        if (noticeTimer <= 0f)
                        {
                            noticeTimer = 0f;
                            Render();
                        }
                    }

                    break;

                case PaymentState.WaitingCard:
                    UpdateWaitingCard();
                    break;

                case PaymentState.Processing:
                    stateTimer += Time.deltaTime;
                    progressFill.fillAmount = Mathf.Clamp01(stateTimer / processingSeconds);
                    if (stateTimer >= processingSeconds)
                    {
                        CompletePayment();
                    }

                    break;

                case PaymentState.Success:
                    stateTimer += Time.deltaTime;
                    if (giftBox != null && !boxHideStarted && stateTimer >= successSeconds - giftBox.HideDuration)
                    {
                        boxHideStarted = true;
                        giftBox.Hide(); // the box shrinks away just before the terminal goes back to Idle
                    }

                    if (stateTimer >= successSeconds)
                    {
                        SetState(CartManager.Instance.Count > 0 ? PaymentState.WaitingCard : PaymentState.Idle);
                    }

                    break;
            }
        }

        private void UpdateWaitingCard()
        {
            if (requireCardExit)
            {
                // After a payment the card has to leave the zone before it can pay again.
                if (cardsInZone.Count == 0)
                {
                    requireCardExit = false;
                }

                dwellTimer = 0f;
                return;
            }

            if (cardsInZone.Count > 0)
            {
                dwellTimer += Time.deltaTime;
                if (dwellTimer >= dwellSeconds)
                {
                    StartProcessing();
                }
            }
            else
            {
                dwellTimer = 0f; // card left early: keep waiting
            }
        }

        private void StartProcessing()
        {
            dwellTimer = 0f;
            requireCardExit = true;
            payingCard = null;
            foreach (CreditCard card in cardsInZone)
            {
                payingCard = card;
                break;
            }

            CartManager.Instance.SetLocked(true);
            SetState(PaymentState.Processing);
        }

        private void CompletePayment()
        {
            CartManager cart = CartManager.Instance;
            cart.SetLocked(false);

            lastTotal = cart.TotalPrice;
            lastBought = new List<GemData>(cart.Items);
            lastConfigs = new List<GemStore.Customize.JewelryConfig>(cart.Configs);
            LastOrder = Order.FromConfigs(NextOrderNumber(), lastConfigs, lastTotal, DateTime.Now);

            // Announce the order (for example to the robot arm) while the cart still holds the items.
            Debug.Log("[Order] paid: " + LastOrder.ToJson());
            OrderEvents.NotifyPaid(LastOrder);

            cart.Clear();
            StartTracking(LastOrder.orderId);
            SetState(PaymentState.Success);
            PlaySuccessEffects();
            OnPaymentCompleted?.Invoke(lastBought, lastTotal);
        }

        private string NextOrderNumber()
        {
            int number;
            do
            {
                number = UnityEngine.Random.Range(1000, 10000);
            }
            while (number == lastOrderNumber);

            lastOrderNumber = number;
            return number.ToString(CultureInfo.InvariantCulture);
        }

        private void PlaySuccessEffects()
        {
            if (audioSource != null && successClip != null)
            {
                audioSource.PlayOneShot(successClip);
            }

            if (payingCard != null)
            {
                payingCard.SendHaptic(successHapticAmplitude, successHapticDuration);
            }

            if (sparkles != null)
            {
                sparkles.Play();
            }

            if (giftBox != null)
            {
                giftBox.Show();
            }
        }

        private void StopSuccessEffects()
        {
            if (sparkles != null)
            {
                sparkles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }

            if (giftBox != null && giftBox.IsShown)
            {
                giftBox.HideImmediate();
            }
        }

        // ---- Robot order status ------------------------------------------------------------------------------

        private void StartTracking(string orderId)
        {
            if (!OrderStatusEvents.IsListening)
            {
                trackingActive = false; // nothing delivers robot status: keep the plain receipt message
                return;
            }

            trackingActive = true;
            awaitingFirstStatus = true;
            trackedOrderId = orderId;
            trackedText = "Sending order to robot...";
            trackedColor = textColor;
            trackingStartTime = Time.time;
            lastStatusTime = Time.time;
            trackedHideAt = -1f;
        }

        private void HandleOrderStatus(OrderStatusMessage message, OrderStatus status)
        {
            if (!trackingActive || message.orderId != trackedOrderId)
            {
                return; // not the order shown on this terminal
            }

            awaitingFirstStatus = false;
            lastStatusTime = Time.time;
            trackedText = OrderStatusTexts.For(status, receiptLabel.font);
            trackedColor = status == OrderStatus.Done ? successColor : status == OrderStatus.Error ? noticeColor : goldColor;
            trackedHideAt = status == OrderStatus.Done || status == OrderStatus.Error ? Time.time + finalStatusHoldSeconds : -1f;
            RefreshTrackingDisplay();
        }

        private void UpdateTracking()
        {
            if (!trackingActive)
            {
                return;
            }

            if (awaitingFirstStatus && Time.time - trackingStartTime >= statusTimeoutSeconds)
            {
                // The robot never answered: do not leave the customer waiting, just confirm the order was sent.
                awaitingFirstStatus = false;
                trackedText = "Order sent";
                trackedColor = textColor;
                trackedHideAt = Time.time + finalStatusHoldSeconds;
                RefreshTrackingDisplay();
            }
            else if (!awaitingFirstStatus && trackedHideAt < 0f && Time.time - lastStatusTime >= staleStatusSeconds)
            {
                trackedHideAt = Time.time;
            }

            if (trackedHideAt >= 0f && Time.time >= trackedHideAt)
            {
                trackingActive = false;
                RefreshTrackingDisplay();
            }
        }

        private void RefreshTrackingDisplay()
        {
            if (State == PaymentState.Success)
            {
                receiptLabel.text = BuildReceipt();
            }
            else if (State == PaymentState.Idle)
            {
                Render();
            }
        }

        private void HandleCardEntered(CreditCard card)
        {
            cardsInZone.Add(card);

            if (State == PaymentState.Idle && CartManager.Instance.Count == 0)
            {
                noticeTimer = emptyNoticeSeconds;
                Render();
            }
            else if (State == PaymentState.WaitingCard && !requireCardExit)
            {
                // The card touched the tap zone: beep + short haptic on the hand holding it.
                if (audioSource != null && beepClip != null)
                {
                    audioSource.PlayOneShot(beepClip);
                }

                card.SendHaptic(tapHapticAmplitude, tapHapticDuration);
            }
        }

        private void HandleCardExited(CreditCard card)
        {
            cardsInZone.Remove(card);
        }

        private void HandleCartChanged()
        {
            if (State == PaymentState.Processing || State == PaymentState.Success)
            {
                return;
            }

            if (State == PaymentState.Idle && CartManager.Instance.Count > 0)
            {
                noticeTimer = 0f;
                SetState(PaymentState.WaitingCard);
            }
            else if (State == PaymentState.WaitingCard && CartManager.Instance.Count == 0)
            {
                SetState(PaymentState.Idle);
            }
            else
            {
                Render();
            }
        }

        private void SetState(PaymentState newState)
        {
            if (State == PaymentState.Success && newState != PaymentState.Success)
            {
                StopSuccessEffects();
            }

            State = newState;
            stateTimer = 0f;
            dwellTimer = 0f;
            boxHideStarted = false;
            Render();
            OnStateChanged?.Invoke(newState);
        }

        private void Render()
        {
            CartManager cart = CartManager.Instance;
            string amount = PriceFormatter.Format(cart.TotalPrice, amountLabel.font);

            progressRoot.SetActive(State == PaymentState.Processing);
            successIcon.enabled = State == PaymentState.Success;
            amountLabel.gameObject.SetActive(State != PaymentState.Success);
            receiptLabel.gameObject.SetActive(State == PaymentState.Success);
            if (State == PaymentState.Processing)
            {
                progressFill.fillAmount = 0f;
            }

            switch (State)
            {
                case PaymentState.Idle when noticeTimer > 0f:
                    Show("Cart is empty", noticeColor, string.Empty, 80f, goldColor);
                    break;
                case PaymentState.Idle:
                    if (trackingActive)
                    {
                        Show($"Order #{trackedOrderId}  \u00B7  {trackedText}", trackedColor, "Add a gem to cart", 48f, textColor);
                    }
                    else
                    {
                        Show(string.Empty, textColor, "Add a gem to cart", 48f, textColor);
                    }

                    break;
                case PaymentState.WaitingCard:
                    Show("Tap your card", textColor, amount, 80f, goldColor);
                    break;
                case PaymentState.Processing:
                    Show("Processing...", textColor, amount, 80f, goldColor);
                    break;
                default:
                    Show("Payment Successful", successColor, string.Empty, 80f, goldColor);
                    receiptLabel.text = BuildReceipt();
                    break;
            }

            if (contactlessIcon != null)
            {
                contactlessIcon.color = State == PaymentState.WaitingCard ? goldColor : new Color(0.45f, 0.5f, 0.6f);
            }
        }

        private void Show(string headline, Color headlineColor, string main, float mainSize, Color mainColor)
        {
            headlineLabel.text = headline;
            headlineLabel.color = headlineColor;
            amountLabel.text = main;
            amountLabel.fontSizeMax = mainSize; // the label auto-sizes down from this
            amountLabel.fontSize = mainSize;
            amountLabel.color = mainColor;
        }

        /// <summary>Receipt shown on the screen: order number, time, the first items, the total and a message.</summary>
        private string BuildReceipt()
        {
            const string grey = "<color=#9AA3B5>";
            TMP_FontAsset font = receiptLabel.font;
            var sb = new StringBuilder();

            sb.Append("<b>Order #").Append(LastOrder.orderId).Append("</b>\n");
            sb.Append(grey).Append(LastOrder.time.ToString("dd MMM yyyy  HH:mm:ss", CultureInfo.InvariantCulture)).Append("</color>\n");
            sb.Append("<size=40%>\n</size>");

            int shown = Mathf.Min(lastBought.Count, MaxReceiptItems);
            for (int i = 0; i < shown; i++)
            {
                GemStore.Customize.JewelryConfig config = lastConfigs[i];
                GemData gem = config.gem;
                sb.Append(gem.DisplayName).Append("<pos=68%>").Append(PriceFormatter.Format(config.TotalPrice, font)).Append('\n');
                sb.Append("<size=80%>").Append(grey).Append(gem.Shape).Append(" · ").Append(gem.GemColor);
                if (!config.IsLoose)
                {
                    sb.Append(" · ").Append(config.Description); // setting and metal, for example "Ring, Gold"
                }

                sb.Append("</color></size>\n");
            }

            if (lastBought.Count > shown)
            {
                int more = lastBought.Count - shown;
                sb.Append(grey).Append("+ ").Append(more).Append(more == 1 ? " more item" : " more items").Append("</color>\n");
            }

            sb.Append("<size=40%>\n</size>");
            sb.Append("<b>Total<pos=68%>").Append(PriceFormatter.Format(lastTotal, font)).Append("</b>\n");
            sb.Append("<size=40%>\n</size>");
            bool showTracked = trackingActive && trackedOrderId == LastOrder.orderId;
            string messageColor = showTracked ? ColorUtility.ToHtmlStringRGB(trackedColor) : "6FD98A";
            sb.Append("<color=#").Append(messageColor).Append('>').Append(showTracked ? trackedText : "Your gems are being packed!").Append("</color>");
            return sb.ToString();
        }
    }
}
