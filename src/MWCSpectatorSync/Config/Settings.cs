using System;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace MWCSpectatorSync.Config
{
    public enum Mode
    {
        Host = 0,
        Spectator = 1
    }

    public enum TransportKind
    {
        SteamP2P = 0,
        TcpLan = 1
    }

    public sealed class Settings
    {
        public ConfigEntry<Mode> Mode;
        public ConfigEntry<TransportKind> Transport;
        public ConfigEntry<int> SendHz;
        public ConfigEntry<float> SmoothingPosition;
        public ConfigEntry<float> SmoothingRotation;
        public ConfigEntry<bool> OverlayEnabled;
        public ConfigEntry<bool> VerboseLogging;

        public ConfigEntry<ulong> SpectatorHostSteamId;
        public ConfigEntry<ulong> AllowOnlySteamId;
        public ConfigEntry<int> P2PChannel;
        public ConfigEntry<bool> ReliableForControl;

        public ConfigEntry<string> HostBindIP;
        public ConfigEntry<int> HostPort;
        public ConfigEntry<string> SpectatorHostIP;

        public ConfigEntry<bool> SpectatorLockdown;

        public void Bind(ConfigFile config, ManualLogSource log)
        {
            Mode = config.Bind("General", "Mode", Config.Mode.Host, "Host or Spectator mode.");
            Transport = config.Bind("General", "Transport", TransportKind.SteamP2P, "SteamP2P (default) or TcpLan.");
            SendHz = config.Bind("General", "SendHz", 20, "Send rate in Hz (clamped 1-60).");
            SmoothingPosition = config.Bind("General", "SmoothingPosition", 0.15f, "Camera position smoothing (0-1).");
            SmoothingRotation = config.Bind("General", "SmoothingRotation", 0.15f, "Camera rotation/FOV smoothing (0-1).");
            OverlayEnabled = config.Bind("General", "OverlayEnabled", true, "Show on-screen overlay.");
            VerboseLogging = config.Bind("General", "VerboseLogging", false, "Enable verbose logging.");

            SpectatorHostSteamId = config.Bind("SteamP2P", "SpectatorHostSteamId", 0ul, "Spectator sets host SteamID64 (0 to show in overlay).");
            AllowOnlySteamId = config.Bind("SteamP2P", "AllowOnlySteamId", 0ul, "Host allowlist SteamID64 (0 allows first spectator).");
            P2PChannel = config.Bind("SteamP2P", "P2PChannel", 0, "Steam P2P channel (0-255).");
            ReliableForControl = config.Bind("SteamP2P", "ReliableForControl", true, "Send LevelChange/Marker reliable on Steam.");

            HostBindIP = config.Bind("TcpLan", "HostBindIP", "0.0.0.0", "Host bind IP for TCP fallback.");
            HostPort = config.Bind("TcpLan", "HostPort", 27055, "Host TCP port for fallback.");
            SpectatorHostIP = config.Bind("TcpLan", "SpectatorHostIP", "127.0.0.1", "Spectator target IP for fallback.");

            SpectatorLockdown = config.Bind("Spectator", "SpectatorLockdown", true, "Best-effort disable player input scripts on spectator.");

            if (log != null)
            {
                log.LogInfo("MWC Spectator Sync settings loaded.");
            }
        }

        public int GetSendHzClamped()
        {
            int hz = SendHz.Value;
            if (hz < 1)
            {
                hz = 1;
            }
            if (hz > 60)
            {
                hz = 60;
            }
            return hz;
        }

        public float GetSmoothingPosition()
        {
            return Clamp01(SmoothingPosition.Value);
        }

        public float GetSmoothingRotation()
        {
            return Clamp01(SmoothingRotation.Value);
        }

        public byte GetP2PChannel()
        {
            int channel = P2PChannel.Value;
            if (channel < 0)
            {
                channel = 0;
            }
            if (channel > 255)
            {
                channel = 255;
            }
            return (byte)channel;
        }

        private static float Clamp01(float value)
        {
            if (value < 0f)
            {
                return 0f;
            }
            if (value > 1f)
            {
                return 1f;
            }
            return value;
        }
    }
}
