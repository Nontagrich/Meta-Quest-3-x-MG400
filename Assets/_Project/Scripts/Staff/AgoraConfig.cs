using UnityEngine;

namespace GemStore.Staff
{
    /// <summary>
    /// Agora settings for the staff call. The asset lives in Assets/_Project/Secrets/ (git-ignored): type the App ID and token
    /// in the Inspector yourself. Never put real values in code or in files that are committed.
    /// </summary>
    [CreateAssetMenu(fileName = "AgoraConfig", menuName = "GemStore/Agora Config", order = 3)]
    public class AgoraConfig : ScriptableObject
    {
        [Tooltip("App ID of your project from the Agora Console. Enter it here in the Inspector.")]
        public string appId;
        [Tooltip("Temporary token for the channel. May be empty if the project uses 'App ID only' (testing mode) authentication. Tokens expire (max 24 h): paste a fresh one when it does.")]
        public string token;
        public string channelName = "gemstore-staff";
    }
}
