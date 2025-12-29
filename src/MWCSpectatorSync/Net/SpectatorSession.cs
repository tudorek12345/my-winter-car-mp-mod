using BepInEx.Logging;
using MWCSpectatorSync.Config;
using MWCSpectatorSync.Sync;

namespace MWCSpectatorSync.Net
{
    public sealed class SpectatorSession
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
        private bool _hasCameraState;
        private ulong _hostSteamId;
        private int _serverSendHz;
        private CameraStateData _latestCameraState;
        private string _progressMarker = string.Empty;

        public SpectatorSession(ITransport transport, Settings settings, LevelSync levelSync, ManualLogSource log, string buildId, string modVersion)
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

        public bool HasCameraState
        {
            get { return _hasCameraState; }
        }

        public CameraStateData LatestCameraState
        {
            get { return _latestCameraState; }
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
            _hasCameraState = false;
            _hostSteamId = _settings.SpectatorHostSteamId.Value;

            if (_running)
            {
                SendHello();
                if (_log != null)
                {
                    _log.LogInfo("Spectator connect requested.");
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
            _hasCameraState = false;
        }

        public void Update()
        {
            if (!_running)
            {
                return;
            }

            while (_transport.TryReceive(out TransportPacket packet))
            {
                HandlePacket(packet);
            }
        }

        private void HandlePacket(TransportPacket packet)
        {
            NetMessage message;
            string error;
            if (!Protocol.TryParse(packet.Payload, packet.Length, out message, out error))
            {
                if (_verbose && _log != null)
                {
                    _log.LogWarning("Spectator parse failed: " + error);
                }
                return;
            }

            switch (message.Type)
            {
                case MessageType.HelloAck:
                    _connected = true;
                    _hostSteamId = message.HelloAck.HostSteamId;
                    _serverSendHz = message.HelloAck.ServerSendHz;
                    if (_log != null)
                    {
                        _log.LogInfo("Connected to host " + _hostSteamId);
                    }
                    break;
                case MessageType.CameraState:
                    _latestCameraState = message.CameraState;
                    _hasCameraState = true;
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
                    _hasCameraState = false;
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
    }
}
