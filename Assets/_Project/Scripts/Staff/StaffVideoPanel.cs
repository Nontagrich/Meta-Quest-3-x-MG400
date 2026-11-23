using System;
using Agora.Rtc;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GemStore.Staff
{
    /// <summary>
    /// The staff screen: header, the staff video (Agora VideoSurface on a RawImage, aspect ratio kept), a placeholder with the
    /// shop logo while idle, a mic-mute toggle and the red End Call button. Pure view: <see cref="StaffCallController"/> drives it.
    /// </summary>
    [DisallowMultipleComponent]
    public class StaffVideoPanel : MonoBehaviour
    {
        [SerializeField] private GameObject placeholder;
        [SerializeField] private TMP_Text messageLabel;
        [SerializeField] private RawImage videoImage;
        [SerializeField] private VideoSurface videoSurface;
        [SerializeField] private AspectRatioFitter aspectFitter;
        [SerializeField] private GameObject controls;
        [SerializeField] private Button muteButton;
        [SerializeField] private Image muteBackground;
        [SerializeField] private TMP_Text muteLabel;
        [SerializeField] private Button endButton;
        [SerializeField] private Color micOnColor = new Color(0.20f, 0.45f, 0.30f);
        [SerializeField] private Color micMutedColor = new Color(0.55f, 0.30f, 0.12f);

        public VideoSurface Surface => videoSurface;

        public event Action MuteClicked;
        public event Action EndClicked;

        private void Awake()
        {
            if (videoSurface != null)
            {
                videoSurface.OnTextureSizeModify += HandleTextureSize;
            }
        }

        private void OnEnable()
        {
            if (muteButton != null) muteButton.onClick.AddListener(OnMute);
            if (endButton != null) endButton.onClick.AddListener(OnEnd);
        }

        private void OnDisable()
        {
            if (muteButton != null) muteButton.onClick.RemoveListener(OnMute);
            if (endButton != null) endButton.onClick.RemoveListener(OnEnd);
        }

        private void OnDestroy()
        {
            if (videoSurface != null)
            {
                videoSurface.OnTextureSizeModify -= HandleTextureSize;
            }
        }

        private void OnMute() => MuteClicked?.Invoke();

        private void OnEnd() => EndClicked?.Invoke();

        // The video arrives with its own size (for example 640x480 or 1280x720): keep that ratio instead of stretching it.
        private void HandleTextureSize(int width, int height)
        {
            if (aspectFitter != null && width > 0 && height > 0)
            {
                aspectFitter.aspectRatio = (float)width / height;
            }
        }

        /// <summary>Logo placeholder with a message ("Need help? Press Call Staff", "Calling staff...", ...).</summary>
        public void ShowPlaceholder(string message, bool showControls)
        {
            if (placeholder != null) placeholder.SetActive(true);
            if (videoImage != null) videoImage.gameObject.SetActive(false);
            if (messageLabel != null) messageLabel.text = message;
            if (controls != null) controls.SetActive(showControls);
        }

        /// <summary>Shows the staff video (the surface must already be bound to the staff uid).</summary>
        public void ShowVideo()
        {
            if (placeholder != null) placeholder.SetActive(false);
            if (videoImage != null) videoImage.gameObject.SetActive(true);
            if (controls != null) controls.SetActive(true);
        }

        public void SetMicMuted(bool muted)
        {
            if (muteLabel != null) muteLabel.text = muted ? "Mic muted" : "Mic on";
            if (muteBackground != null) muteBackground.color = muted ? micMutedColor : micOnColor;
        }
    }
}
