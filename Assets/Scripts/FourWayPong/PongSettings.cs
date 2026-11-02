using UnityEngine;

namespace FourWayPong
{
    public static class PongSettings
    {
        private const string PlayerNameKey = "FourWayPong.PlayerName";
        private const string P1UpKey = "FourWayPong.P1Up";
        private const string P1DownKey = "FourWayPong.P1Down";
        private const string P2UpKey = "FourWayPong.P2Up";
        private const string P2DownKey = "FourWayPong.P2Down";
        private const string P1MouseKey = "FourWayPong.P1Mouse";
        private const string P2MouseKey = "FourWayPong.P2Mouse";
        private const string VolumeKey = "FourWayPong.MasterVolume";
        private const string MutedKey = "FourWayPong.Muted";
        private const string ShowPingKey = "FourWayPong.ShowPing";

        public static string PlayerName { get; private set; } = string.Empty;
        public static KeyCode Player1Up { get; private set; } = KeyCode.W;
        public static KeyCode Player1Down { get; private set; } = KeyCode.S;
        public static KeyCode Player2Up { get; private set; } = KeyCode.UpArrow;
        public static KeyCode Player2Down { get; private set; } = KeyCode.DownArrow;
        public static bool Player1UsesMouse { get; private set; }
        public static bool Player2UsesMouse { get; private set; }
        public static float MasterVolume { get; private set; } = 0.75f;
        public static bool Muted { get; private set; }
        public static bool ShowPing { get; private set; } = true;

        public static float EffectiveVolume => Muted ? 0f : MasterVolume;

        public static void Load()
        {
            PlayerName = NormalizePlayerName(PlayerPrefs.GetString(PlayerNameKey, string.Empty));
            Player1Up = (KeyCode)PlayerPrefs.GetInt(P1UpKey, (int)KeyCode.W);
            Player1Down = (KeyCode)PlayerPrefs.GetInt(P1DownKey, (int)KeyCode.S);
            Player2Up = (KeyCode)PlayerPrefs.GetInt(P2UpKey, (int)KeyCode.UpArrow);
            Player2Down = (KeyCode)PlayerPrefs.GetInt(P2DownKey, (int)KeyCode.DownArrow);
            Player1UsesMouse = PlayerPrefs.GetInt(P1MouseKey, 0) != 0;
            Player2UsesMouse = PlayerPrefs.GetInt(P2MouseKey, 0) != 0;
            MasterVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(VolumeKey, 0.75f));
            Muted = PlayerPrefs.GetInt(MutedKey, 0) != 0;
            ShowPing = PlayerPrefs.GetInt(ShowPingKey, 1) != 0;
        }

        public static void SetPlayerName(string value)
        {
            PlayerName = NormalizePlayerName(value);
            Save();
        }

        private static string NormalizePlayerName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            value = value.Trim();
            if (value.Length > 20)
                value = value.Substring(0, 20);
            return value;
        }

        public static void SetBinding(int player, bool positive, KeyCode key)
        {
            if (key == KeyCode.None)
                return;

            if (player == 1)
            {
                if (positive) Player1Up = key;
                else Player1Down = key;
            }
            else
            {
                if (positive) Player2Up = key;
                else Player2Down = key;
            }

            Save();
        }


        public static void ToggleControlMode(int player)
        {
            if (player == 1)
                Player1UsesMouse = !Player1UsesMouse;
            else
                Player2UsesMouse = !Player2UsesMouse;

            Save();
        }

        public static void SetVolume(float value)
        {
            MasterVolume = Mathf.Clamp01(value);
            Save();
        }

        public static void SetMuted(bool muted)
        {
            Muted = muted;
            Save();
        }

        public static void SetShowPing(bool show)
        {
            ShowPing = show;
            Save();
        }

        public static void ResetBindings()
        {
            Player1Up = KeyCode.W;
            Player1Down = KeyCode.S;
            Player2Up = KeyCode.UpArrow;
            Player2Down = KeyCode.DownArrow;
            Player1UsesMouse = false;
            Player2UsesMouse = false;
            Save();
        }

        private static void Save()
        {
            PlayerPrefs.SetString(PlayerNameKey, PlayerName ?? string.Empty);
            PlayerPrefs.SetInt(P1UpKey, (int)Player1Up);
            PlayerPrefs.SetInt(P1DownKey, (int)Player1Down);
            PlayerPrefs.SetInt(P2UpKey, (int)Player2Up);
            PlayerPrefs.SetInt(P2DownKey, (int)Player2Down);
            PlayerPrefs.SetInt(P1MouseKey, Player1UsesMouse ? 1 : 0);
            PlayerPrefs.SetInt(P2MouseKey, Player2UsesMouse ? 1 : 0);
            PlayerPrefs.SetFloat(VolumeKey, MasterVolume);
            PlayerPrefs.SetInt(MutedKey, Muted ? 1 : 0);
            PlayerPrefs.SetInt(ShowPingKey, ShowPing ? 1 : 0);
            PlayerPrefs.Save();
        }
    }
}
