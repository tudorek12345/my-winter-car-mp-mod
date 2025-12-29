using System;
using BepInEx.Logging;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Sync;
using UnityEngine;

namespace MyWinterCarMpMod.Net
{
    public sealed class HostSession
    {
        private readonly ITransport _transport;
        private readonly Settings _settings;
        private readonly ManualLogSource _log;
        private readonly bool _verbose;
        private readonly string _buildId;
        private readonly string _modVersion;
        private readonly LevelSync _levelSync;
        private readonly PlayerLocator _playerLocator = new PlayerLocator();

        private bool _running;
        private bool _connected;
        private ulong _clientSteamId;
        private string _clientBuildId;
        private string _clientModVersion;
        private string _progressMarker = string.Empty;
        private float _nextSendTime;
        private PlayerStateData _latestClientState;
        private bool _hasClientState;

        public HostSession(ITransport transport, Settings settings, LevelSync levelSync, ManualLogSource log, string buildId, string modVersion)
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

        public ulong ClientSteamId
        {
            get { return _clientSteamId; }
        }

        public string ClientBuildId
        {
            get { return _clientBuildId; }
        }

        public string ClientModVersion
        {
            get { return _clientModVersion; }
        }

        public string ProgressMarker
        {
            get { return _progressMarker; }
        }

        public bool HasClientState
        {
            get { return _hasClientState; }
        }

        public PlayerStateData LatestClientState
        {
            get { return _latestClientState; }
        }

        public bool Start()
        {
            _running = _transport.StartHost();
            _connected = false;
            _clientSteamId = 0ul;
            _clientBuildId = null;
            _clientModVersion = null;
            _nextSendTime = Time.realtimeSinceStartup;
            _hasClientState = false;
            if (_running && _log != null)
            {
                _log.LogInfo("Host session started.");
            }
            return _running;
        }

        public void Stop()
        {
            if (_connected)
            {
                SendDisconnect();
            }
            _transport.Stop();
            _running = false;
            _connected = false;
            _clientSteamId = 0ul;
            _hasClientState = false;
            _playerLocator.Clear();
            if (_log != null)
            {
                _log.LogInfo("Host session stopped.");
            }
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

        public void SetProgressMarker(string marker)
        {
            _progressMarker = marker ?? string.Empty;
            if (_connected)
            {
                byte[] payload = Protocol.BuildProgressMarker(_progressMarker);
                _transport.Send(payload, _settings.ReliableForControl.Value);
            }
        }

        public void SendLevelChange(int levelIndex, string levelName)
        {
            if (!_connected)
            {
                return;
            }
            byte[] payload = Protocol.BuildLevelChange(levelIndex, levelName);
            _transport.Send(payload, _settings.ReliableForControl.Value);
        }

        private void HandlePacket(TransportPacket packet)
        {
            if (_connected && packet.SenderId != 0ul && packet.SenderId != _clientSteamId)
            {
                if (_verbose && _log != null)
                {
                    _log.LogWarning("Ignored packet from non-authoritative sender " + packet.SenderId);
                }
                _transport.SendTo(packet.SenderId, Protocol.BuildDisconnect(), true);
                return;
            }

            NetMessage message;
            string error;
            if (!Protocol.TryParse(packet.Payload, packet.Length, out message, out error))
            {
                if (_verbose && _log != null)
                {
                    _log.LogWarning("Host parse failed: " + error);
                }
                return;
            }

            switch (message.Type)
            {
                case MessageType.Hello:
                    HandleHello(packet.SenderId, message.Hello);
                    break;
                case MessageType.Ping:
                    _transport.SendTo(packet.SenderId, Protocol.BuildPong(), false);
                    break;
                case MessageType.PlayerState:
                    _latestClientState = message.PlayerState;
                    _hasClientState = true;
                    break;
                case MessageType.CameraState:
                    _latestClientState = new PlayerStateData
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
                    _hasClientState = true;
                    break;
                case MessageType.Disconnect:
                    if (_verbose && _log != null)
                    {
                        _log.LogInfo("Client disconnected.");
                    }
                    _connected = false;
                    _clientSteamId = 0ul;
                    _hasClientState = false;
                    break;
            }
        }

        private void HandleHello(ulong senderId, HelloData hello)
        {
            if (_connected && senderId != _clientSteamId)
            {
                if (_verbose && _log != null)
                {
                    _log.LogInfo("Ignored Hello from additional client " + senderId);
                }
                _transport.SendTo(senderId, Protocol.BuildDisconnect(), true);
                return;
            }

            if (!IsAllowedClient(hello.SenderSteamId))
            {
                if (_log != null)
                {
                    _log.LogWarning("Rejected client " + hello.SenderSteamId + " (allowlist).");
                }
                _transport.SendTo(senderId, Protocol.BuildDisconnect(), true);
                return;
            }

            if (!string.IsNullOrEmpty(_buildId) && hello.BuildId != _buildId && _log != null)
            {
                _log.LogWarning("Client buildId mismatch: " + hello.BuildId + " (host " + _buildId + ")");
            }
            if (!string.IsNullOrEmpty(_modVersion) && hello.ModVersion != _modVersion && _log != null)
            {
                _log.LogWarning("Client modVersion mismatch: " + hello.ModVersion + " (host " + _modVersion + ")");
            }

            _clientSteamId = senderId != 0 ? senderId : hello.SenderSteamId;
            _clientBuildId = hello.BuildId;
            _clientModVersion = hello.ModVersion;
            _connected = true;

            byte[] ack = Protocol.BuildHelloAck(_transport.LocalSteamId, _settings.GetSendHzClamped());
            _transport.SendTo(senderId, ack, true);

            SendLevelChange(_levelSync.CurrentLevelIndex, _levelSync.CurrentLevelName);
            if (!string.IsNullOrEmpty(_progressMarker))
            {
                _transport.Send(Protocol.BuildProgressMarker(_progressMarker), _settings.ReliableForControl.Value);
            }
            SendPlayerState();

            if (_log != null)
            {
                _log.LogInfo("Client connected: " + _clientSteamId);
            }
        }

        private bool IsAllowedClient(ulong senderSteamId)
        {
            ulong allowOnly = _settings.AllowOnlySteamId.Value;
            if (allowOnly == 0)
            {
                return true;
            }

            if (senderSteamId == allowOnly)
            {
                return true;
            }

            if (_transport.Kind == TransportKind.TcpLan && senderSteamId == 0)
            {
                if (_log != null)
                {
                    _log.LogWarning("AllowOnlySteamId set, but TCP client has no SteamID. Allowing.");
                }
                return true;
            }

            return false;
        }

        private void SendPlayerState()
        {
            PlayerStateData state;
            if (!_playerLocator.TryGetLocalState(out state))
            {
                return;
            }

            byte[] payload = Protocol.BuildPlayerState(state);
            _transport.Send(payload, false);
        }

        private void SendDisconnect()
        {
            _transport.Send(Protocol.BuildDisconnect(), true);
        }

    }
}
