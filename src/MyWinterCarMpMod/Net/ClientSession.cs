using BepInEx.Logging;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Sync;
using UnityEngine;

namespace MyWinterCarMpMod.Net
{
    public sealed class ClientSession
    {
        private readonly ITransport _transport;
        private readonly Settings _settings;
        private readonly ManualLogSource _log;
        private readonly bool _verbose;
        private readonly string _buildId;
        private readonly string _modVersion;
        private readonly LevelSync _levelSync;

        private bool _running;
        private bool _connected;
        private bool _hasHostState;
        private ulong _hostSteamId;
        private int _serverSendHz;
        private PlayerStateData _latestHostState;
        private string _progressMarker = string.Empty;
        private float _nextSendTime;
        private readonly PlayerLocator _playerLocator = new PlayerLocator();

        public ClientSession(ITransport transport, Settings settings, LevelSync levelSync, ManualLogSource log, string buildId, string modVersion)
        {
            _transport = transport;
            _settings = settings;
            _levelSync = levelSync;
            _log = log;
            _verbose = settings.VerboseLogging.Value;
            _buildId = buildId ?? string.Empty;
            _modVersion = modVersion ?? string.Empty;
        }

        public bool IsRunning
        {
            get { return _running; }
        }

        public bool IsConnected
        {
            get { return _connected; }
        }

        public bool HasHostState
        {
            get { return _hasHostState; }
        }

        public PlayerStateData LatestHostState
        {
            get { return _latestHostState; }
        }

        public string ProgressMarker
        {
            get { return _progressMarker; }
        }

        public ulong HostSteamId
        {
            get { return _hostSteamId; }
        }

        public int ServerSendHz
        {
            get { return _serverSendHz; }
        }

        public bool Connect()
        {
            if (_transport.Kind == TransportKind.SteamP2P && _settings.SpectatorHostSteamId.Value == 0)
            {
                if (_log != null)
                {
                    _log.LogWarning("SpectatorHostSteamId is 0. Set host SteamID64 before connecting.");
                }
                return false;
            }

            _running = _transport.Connect();
            _connected = false;
            _hasHostState = false;
            _hostSteamId = _settings.SpectatorHostSteamId.Value;
            _nextSendTime = Time.realtimeSinceStartup;

            if (_running)
            {
                SendHello();
                if (_log != null)
                {
                    _log.LogInfo("Client connect requested.");
                }
            }
            return _running;
        }

        public void Disconnect()
        {
            if (_connected)
            {
                _transport.Send(Protocol.BuildDisconnect(), true);
            }
            _transport.Disconnect();
            _running = false;
            _connected = false;
            _hasHostState = false;
            _playerLocator.Clear();
        }

        public void Update()
        {
            if (!_running)
            {
                return;
            }

            TransportPacket packet;
            while (_transport.TryReceive(out packet))
            {
                HandlePacket(packet);
            }

            if (_connected)
            {
                float now = Time.realtimeSinceStartup;
                float interval = 1f / _settings.GetSendHzClamped();
                if (now >= _nextSendTime)
                {
                    _nextSendTime = now + interval;
                    SendPlayerState();
                }
            }
        }

        private void HandlePacket(TransportPacket packet)
        {
            if (!IsAuthoritativeSender(packet))
            {
                if (_verbose && _log != null)
                {
                    _log.LogWarning("Ignored packet from non-authoritative sender " + packet.SenderId);
                }
                if (_transport.Kind == TransportKind.SteamP2P && packet.SenderId != 0ul)
                {
                    _transport.SendTo(packet.SenderId, Protocol.BuildDisconnect(), true);
                }
                return;
            }

            NetMessage message;
            string error;
            if (!Protocol.TryParse(packet.Payload, packet.Length, out message, out error))
            {
                if (_verbose && _log != null)
                {
                    _log.LogWarning("Client parse failed: " + error);
                }
                return;
            }

            switch (message.Type)
            {
                case MessageType.HelloAck:
                    if (!IsAuthoritativeHelloAck(packet.SenderId, message.HelloAck.HostSteamId))
                    {
                        return;
                    }
                    _connected = true;
                    _hostSteamId = message.HelloAck.HostSteamId;
                    _serverSendHz = message.HelloAck.ServerSendHz;
                    if (_log != null)
                    {
                        _log.LogInfo("Connected to host " + _hostSteamId);
                    }
                    break;
                case MessageType.PlayerState:
                    _latestHostState = message.PlayerState;
                    _hasHostState = true;
                    break;
                case MessageType.CameraState:
                    _latestHostState = new PlayerStateData
                    {
                        UnixTimeMs = message.CameraState.UnixTimeMs,
                        PosX = message.CameraState.PosX,
                        PosY = message.CameraState.PosY,
                        PosZ = message.CameraState.PosZ,
                        ViewRotX = message.CameraState.RotX,
                        ViewRotY = message.CameraState.RotY,
                        ViewRotZ = message.CameraState.RotZ,
                        ViewRotW = message.CameraState.RotW
                    };
                    _hasHostState = true;
                    break;
                case MessageType.LevelChange:
                    _levelSync.ApplyLevelChange(message.LevelChange.LevelIndex, message.LevelChange.LevelName);
                    break;
                case MessageType.ProgressMarker:
                    _progressMarker = message.ProgressMarker.Marker;
                    break;
                case MessageType.Ping:
                    _transport.Send(Protocol.BuildPong(), false);
                    break;
                case MessageType.Disconnect:
                    if (_log != null)
                    {
                        _log.LogWarning("Host disconnected.");
                    }
                    _connected = false;
                    _running = false;
                    _hasHostState = false;
                    break;
            }
        }

        private void SendHello()
        {
            byte[] payload = Protocol.BuildHello(_transport.LocalSteamId, _buildId, _modVersion);
            if (_transport.Kind == TransportKind.SteamP2P)
            {
                _transport.SendTo(_settings.SpectatorHostSteamId.Value, payload, true);
            }
            else
            {
                _transport.Send(payload, true);
            }
        }

        private void SendPlayerState()
        {
            PlayerStateData state;
            if (!_playerLocator.TryGetLocalState(out state))
            {
                return;
            }

            byte[] payload = Protocol.BuildPlayerState(state);
            if (_transport.Kind == TransportKind.SteamP2P)
            {
                _transport.SendTo(_settings.SpectatorHostSteamId.Value, payload, false);
            }
            else
            {
                _transport.Send(payload, false);
            }
        }

        private bool IsAuthoritativeSender(TransportPacket packet)
        {
            if (_transport.Kind != TransportKind.SteamP2P)
            {
                return true;
            }

            if (packet.SenderId == 0ul)
            {
                return false;
            }

            ulong expectedHost = _settings.SpectatorHostSteamId.Value;
            if (expectedHost != 0ul && packet.SenderId != expectedHost)
            {
                return false;
            }

            if (_hostSteamId != 0ul && packet.SenderId != _hostSteamId)
            {
                return false;
            }

            return true;
        }

        private bool IsAuthoritativeHelloAck(ulong senderId, ulong hostSteamId)
        {
            if (_transport.Kind != TransportKind.SteamP2P)
            {
                return true;
            }

            if (senderId == 0ul || hostSteamId == 0ul)
            {
                return false;
            }

            if (senderId != hostSteamId)
            {
                if (_log != null)
                {
                    _log.LogWarning("HelloAck host mismatch: sender " + senderId + " host " + hostSteamId);
                }
                return false;
            }

            ulong expectedHost = _settings.SpectatorHostSteamId.Value;
            if (expectedHost != 0ul && hostSteamId != expectedHost)
            {
                if (_log != null)
                {
                    _log.LogWarning("HelloAck from unexpected host " + hostSteamId);
                }
                return false;
            }

            return true;
        }
    }
}
