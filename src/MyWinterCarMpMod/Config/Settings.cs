using System;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace MyWinterCarMpMod.Config
{
    public enum Mode
    {
        Host = 0,
        Client = 1,
        Spectator = Client
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
        public ConfigEntry<bool> MainMenuPanelEnabled;
        public ConfigEntry<bool> VerboseLogging;
        public ConfigEntry<bool> AllowMultipleInstances;
        public ConfigEntry<string> RemoteAvatarBundlePath;
        public ConfigEntry<string> RemoteAvatarAssetName;
        public ConfigEntry<float> RemoteAvatarScale;
        public ConfigEntry<float> RemoteAvatarYOffset;

        public ConfigEntry<ulong> SpectatorHostSteamId;
        public ConfigEntry<ulong> AllowOnlySteamId;
        public ConfigEntry<int> P2PChannel;
        public ConfigEntry<bool> ReliableForControl;

        public ConfigEntry<string> HostBindIP;
        public ConfigEntry<int> HostPort;
        public ConfigEntry<string> SpectatorHostIP;

        public ConfigEntry<bool> SpectatorLockdown;

        public ConfigEntry<bool> DoorSyncEnabled;
        public ConfigEntry<bool> DoorPlayMakerEnabled;
        public ConfigEntry<int> DoorSendHz;
        public ConfigEntry<float> DoorAngleThreshold;
        public ConfigEntry<string> DoorNameFilter;
        public ConfigEntry<bool> VehicleSyncEnabled;
        public ConfigEntry<bool> VehicleSyncClientSend;
        public ConfigEntry<bool> VehicleOwnershipEnabled;
        public ConfigEntry<float> VehicleSeatDistance;
        public ConfigEntry<int> VehicleSendHz;
        public ConfigEntry<float> VehiclePositionThreshold;
        public ConfigEntry<float> VehicleRotationThreshold;
        public ConfigEntry<string> VehicleNameFilter;
        public ConfigEntry<bool> PickupSyncEnabled;
        public ConfigEntry<bool> PickupSyncClientSend;
        public ConfigEntry<int> PickupSendHz;
        public ConfigEntry<float> PickupPositionThreshold;
        public ConfigEntry<float> PickupRotationThreshold;
        public ConfigEntry<string> PickupNameFilter;
        public ConfigEntry<bool> TimeSyncEnabled;
        public ConfigEntry<int> TimeSyncSendHz;
        public ConfigEntry<string> TimeSyncLightFilter;
        public ConfigEntry<bool> TimeSyncAmbient;

        public ConfigEntry<float> ConnectionTimeoutSeconds;
        public ConfigEntry<float> HelloRetrySeconds;
        public ConfigEntry<float> KeepAliveSeconds;
        public ConfigEntry<bool> AutoReconnect;
        public ConfigEntry<float> ReconnectDelaySeconds;
        public ConfigEntry<int> MaxReconnectAttempts;
        public ConfigEntry<float> LevelSyncIntervalSeconds;

        public ConfigEntry<bool> LanDiscoveryEnabled;
        public ConfigEntry<int> LanDiscoveryPort;
        public ConfigEntry<float> LanBroadcastIntervalSeconds;
        public ConfigEntry<float> LanHostTimeoutSeconds;

        public void Bind(ConfigFile config, ManualLogSource log)
        {
            Mode = config.Bind("General", "Mode", Config.Mode.Host, "Host or Client (co-op) mode.");
            Transport = config.Bind("General", "Transport", TransportKind.SteamP2P, "SteamP2P (default) or TcpLan.");
            SendHz = config.Bind("General", "SendHz", 20, "Send rate in Hz (clamped 1-60).");
            SmoothingPosition = config.Bind("General", "SmoothingPosition", 0.15f, "Camera position smoothing (0-1).");
            SmoothingRotation = config.Bind("General", "SmoothingRotation", 0.15f, "Camera rotation/FOV smoothing (0-1).");
            OverlayEnabled = config.Bind("General", "OverlayEnabled", true, "Show on-screen overlay.");
            MainMenuPanelEnabled = config.Bind("UI", "MainMenuPanelEnabled", true, "Show co-op menu panel on the main menu.");
            VerboseLogging = config.Bind("General", "VerboseLogging", false, "Enable verbose logging.");
            AllowMultipleInstances = config.Bind("Compatibility", "AllowMultipleInstances", false, "Skip Steam restart checks and Steam API init (restart required).");
            RemoteAvatarBundlePath = config.Bind("Avatar", "BundlePath", "", "Optional AssetBundle path for a static remote avatar mesh/prefab.");
            RemoteAvatarAssetName = config.Bind("Avatar", "AssetName", "", "Prefab or Mesh name inside the avatar AssetBundle.");
            RemoteAvatarScale = config.Bind("Avatar", "Scale", 1.0f, "Scale for the remote avatar mesh.");
            RemoteAvatarYOffset = config.Bind("Avatar", "YOffset", 0.0f, "Vertical offset for the remote avatar mesh.");

            SpectatorHostSteamId = config.Bind("SteamP2P", "SpectatorHostSteamId", 0ul, "Client sets host SteamID64 (0 to show in overlay).");
            AllowOnlySteamId = config.Bind("SteamP2P", "AllowOnlySteamId", 0ul, "Host allowlist SteamID64 (0 allows first client).");
            P2PChannel = config.Bind("SteamP2P", "P2PChannel", 0, "Steam P2P channel (0-255).");
            ReliableForControl = config.Bind("SteamP2P", "ReliableForControl", true, "Send LevelChange/Marker reliable on Steam.");

            HostBindIP = config.Bind("TcpLan", "HostBindIP", "0.0.0.0", "Host bind IP for TCP fallback.");
            HostPort = config.Bind("TcpLan", "HostPort", 27055, "Host TCP port for fallback.");
            SpectatorHostIP = config.Bind("TcpLan", "SpectatorHostIP", "127.0.0.1", "Client target IP for fallback.");

            SpectatorLockdown = config.Bind("Spectator", "SpectatorLockdown", true, "Spectator-only input lockdown (unused in co-op).");

            DoorSyncEnabled = config.Bind("DoorSync", "Enabled", true, "Sync door hinge states between host/client.");
            DoorPlayMakerEnabled = config.Bind("DoorSync", "PlayMakerEvents", true, "Use PlayMaker door open/close events when available.");
            DoorSendHz = config.Bind("DoorSync", "SendHz", 10, "Door state send rate in Hz (1-30).");
            DoorAngleThreshold = config.Bind("DoorSync", "AngleThreshold", 1.0f, "Minimum angle change (degrees) before sending.");
            DoorNameFilter = config.Bind("DoorSync", "NameFilter", "door,ovi,tap,faucet,sink,phone,telephone,fridge,freezer,refrigerator,icebox", "Case-insensitive name filter for door objects (empty = all hinges). Comma-separated tokens.");

            VehicleSyncEnabled = config.Bind("VehicleSync", "Enabled", true, "Sync vehicle rigidbody states (experimental).");
            VehicleSyncClientSend = config.Bind("VehicleSync", "ClientSend", true, "Allow clients to send vehicle states (experimental).");
            VehicleOwnershipEnabled = config.Bind("VehicleSync", "OwnershipEnabled", true, "Allow client to request vehicle control.");
            VehicleSeatDistance = config.Bind("VehicleSync", "SeatDistance", 1.2f, "Distance (meters) to detect driver seat.");
            VehicleSendHz = config.Bind("VehicleSync", "SendHz", 10, "Vehicle state send rate in Hz (1-30).");
            VehiclePositionThreshold = config.Bind("VehicleSync", "PositionThreshold", 0.05f, "Minimum position delta before sending (meters).");
            VehicleRotationThreshold = config.Bind("VehicleSync", "RotationThreshold", 1.0f, "Minimum rotation delta before sending (degrees).");
            VehicleNameFilter = config.Bind("VehicleSync", "NameFilter", "sorbet", "Optional name filter for vehicles (comma-separated). Empty = all.");
            PickupSyncEnabled = config.Bind("PickupSync", "Enabled", false, "Sync pickupable rigidbody states (experimental).");
            PickupSyncClientSend = config.Bind("PickupSync", "ClientSend", false, "Allow clients to send pickup states (experimental).");
            PickupSendHz = config.Bind("PickupSync", "SendHz", 12, "Pickup state send rate in Hz (1-30).");
            PickupPositionThreshold = config.Bind("PickupSync", "PositionThreshold", 0.02f, "Minimum position delta before sending (meters).");
            PickupRotationThreshold = config.Bind("PickupSync", "RotationThreshold", 2.0f, "Minimum rotation delta before sending (degrees).");
            PickupNameFilter = config.Bind("PickupSync", "NameFilter", "", "Optional name filter for pickup objects (comma-separated).");

            TimeSyncEnabled = config.Bind("TimeSync", "Enabled", true, "Sync time-of-day lighting (directional light + ambient).");
            TimeSyncSendHz = config.Bind("TimeSync", "SendHz", 1, "Time sync send rate in Hz (1-10).");
            TimeSyncLightFilter = config.Bind("TimeSync", "LightNameFilter", "", "Optional name token to pick the sun light (empty = brightest directional).");
            TimeSyncAmbient = config.Bind("TimeSync", "SyncAmbient", true, "Sync ambient color/intensity along with the sun rotation.");

            ConnectionTimeoutSeconds = config.Bind("Networking", "ConnectionTimeoutSeconds", 10f, "Seconds without packets before timing out.");
            HelloRetrySeconds = config.Bind("Networking", "HelloRetrySeconds", 2f, "Seconds between hello retries while connecting.");
            KeepAliveSeconds = config.Bind("Networking", "KeepAliveSeconds", 2f, "Seconds between keepalive pings.");
            AutoReconnect = config.Bind("Networking", "AutoReconnect", true, "Auto-reconnect when disconnected.");
            ReconnectDelaySeconds = config.Bind("Networking", "ReconnectDelaySeconds", 3f, "Seconds to wait before reconnecting.");
            MaxReconnectAttempts = config.Bind("Networking", "MaxReconnectAttempts", 5, "Max reconnect attempts (0 = infinite).");
            LevelSyncIntervalSeconds = config.Bind("Networking", "LevelSyncIntervalSeconds", 5f, "Seconds between host level resyncs.");

            LanDiscoveryEnabled = config.Bind("LanDiscovery", "Enabled", true, "Enable LAN discovery for TCP.");
            LanDiscoveryPort = config.Bind("LanDiscovery", "Port", 27056, "UDP discovery port.");
            LanBroadcastIntervalSeconds = config.Bind("LanDiscovery", "BroadcastIntervalSeconds", 1.5f, "Seconds between host broadcasts.");
            LanHostTimeoutSeconds = config.Bind("LanDiscovery", "HostTimeoutSeconds", 5f, "Seconds before expiring LAN hosts.");

            if (log != null)
            {
                log.LogInfo("My Winter Car MP Mod settings loaded.");
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

        public float GetConnectionTimeoutSeconds()
        {
            return ClampMin(ConnectionTimeoutSeconds.Value, 2f);
        }

        public float GetHelloRetrySeconds()
        {
            return ClampMin(HelloRetrySeconds.Value, 0.5f);
        }

        public float GetKeepAliveSeconds()
        {
            return ClampMin(KeepAliveSeconds.Value, 0.5f);
        }

        public float GetReconnectDelaySeconds()
        {
            return ClampMin(ReconnectDelaySeconds.Value, 1f);
        }

        public int GetMaxReconnectAttempts()
        {
            int value = MaxReconnectAttempts.Value;
            if (value < 0)
            {
                return 0;
            }
            return value;
        }

        public float GetLevelSyncIntervalSeconds()
        {
            return ClampMin(LevelSyncIntervalSeconds.Value, 1f);
        }

        public int GetDoorSendHz()
        {
            int hz = DoorSendHz.Value;
            if (hz < 1)
            {
                hz = 1;
            }
            if (hz > 30)
            {
                hz = 30;
            }
            return hz;
        }

        public float GetDoorAngleThreshold()
        {
            return ClampMin(DoorAngleThreshold.Value, 0.1f);
        }

        public string GetDoorNameFilter()
        {
            return DoorNameFilter.Value ?? string.Empty;
        }

        public float GetVehicleSeatDistance()
        {
            return ClampMin(VehicleSeatDistance.Value, 0.2f);
        }

        public int GetVehicleSendHz()
        {
            int hz = VehicleSendHz.Value;
            if (hz < 1)
            {
                hz = 1;
            }
            if (hz > 30)
            {
                hz = 30;
            }
            return hz;
        }

        public float GetVehiclePositionThreshold()
        {
            return ClampMin(VehiclePositionThreshold.Value, 0.01f);
        }

        public float GetVehicleRotationThreshold()
        {
            return ClampMin(VehicleRotationThreshold.Value, 0.1f);
        }

        public string GetVehicleNameFilter()
        {
            return VehicleNameFilter.Value ?? string.Empty;
        }

        public int GetPickupSendHz()
        {
            int hz = PickupSendHz.Value;
            if (hz < 1)
            {
                hz = 1;
            }
            if (hz > 30)
            {
                hz = 30;
            }
            return hz;
        }

        public float GetPickupPositionThreshold()
        {
            return ClampMin(PickupPositionThreshold.Value, 0.01f);
        }

        public float GetPickupRotationThreshold()
        {
            return ClampMin(PickupRotationThreshold.Value, 0.1f);
        }

        public string GetPickupNameFilter()
        {
            return PickupNameFilter.Value ?? string.Empty;
        }

        public int GetTimeSyncSendHz()
        {
            int hz = TimeSyncSendHz.Value;
            if (hz < 1)
            {
                hz = 1;
            }
            if (hz > 10)
            {
                hz = 10;
            }
            return hz;
        }

        public float GetLanBroadcastIntervalSeconds()
        {
            return ClampMin(LanBroadcastIntervalSeconds.Value, 0.5f);
        }

        public float GetLanHostTimeoutSeconds()
        {
            return ClampMin(LanHostTimeoutSeconds.Value, 1f);
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

        private static float ClampMin(float value, float min)
        {
            if (value < min)
            {
                return min;
            }
            return value;
        }
    }
}
