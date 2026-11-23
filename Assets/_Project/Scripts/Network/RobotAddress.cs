using System.Text.RegularExpressions;
using UnityEngine;

namespace GemStore.Network
{
    /// <summary>The robot's IP:port as edited in the headset. Saved in PlayerPrefs; it overrides the RobotLinkConfig asset when present.</summary>
    public static class RobotAddress
    {
        public const string HostKey = "robot_host";
        public const string PortKey = "robot_port";

        private static readonly Regex Ipv4 = new Regex(@"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$", RegexOptions.Compiled);

        public static bool HasSaved => PlayerPrefs.HasKey(HostKey) && PlayerPrefs.HasKey(PortKey);

        public static string SavedHost => PlayerPrefs.GetString(HostKey, string.Empty);

        public static int SavedPort => PlayerPrefs.GetInt(PortKey, 0);

        public static void Save(string host, int port)
        {
            PlayerPrefs.SetString(HostKey, host);
            PlayerPrefs.SetInt(PortKey, port);
            PlayerPrefs.Save();
        }

        public static void ClearSaved()
        {
            PlayerPrefs.DeleteKey(HostKey);
            PlayerPrefs.DeleteKey(PortKey);
            PlayerPrefs.Save();
        }

        /// <summary>Parses "a.b.c.d:port" (the port is optional and then <paramref name="defaultPort"/>). IPv4 addresses only (numeric keypad).</summary>
        public static bool TryParse(string text, int defaultPort, out string host, out int port, out string error)
        {
            host = string.Empty;
            port = defaultPort;
            error = string.Empty;
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                error = "Type an address like 192.168.1.20:5005";
                return false;
            }

            string hostPart = text;
            int colon = text.IndexOf(':');
            if (colon >= 0)
            {
                hostPart = text.Substring(0, colon);
                string portPart = text.Substring(colon + 1);
                if (!int.TryParse(portPart, out port) || port < 1 || port > 65535)
                {
                    error = "Port must be 1 - 65535";
                    return false;
                }
            }

            Match match = Ipv4.Match(hostPart);
            if (!match.Success)
            {
                error = "IP must look like 192.168.1.20";
                return false;
            }

            for (int i = 1; i <= 4; i++)
            {
                if (int.Parse(match.Groups[i].Value) > 255)
                {
                    error = "Each IP part must be 0 - 255";
                    return false;
                }
            }

            host = hostPart;
            return true;
        }
    }
}
