using System;
using BepInEx.Logging;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Sync;
using MyWinterCarMpMod.Util;
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
        private readonly DoorSync _doorSync;
        private readonly System.Collections.Generic.List<DoorStateData> _doorSendBuffer = new System.Collections.Generic.List<DoorStateData>(32);

        private bool _running;
        private bool _connected;
        private ulong _clientSteamId;
        private string _clientBuildId;
        private string _clientModVersion;
        private string _progressMarker = string.Empty;
        private float _nextSendTime;
        private float _lastReceiveTime;
        private float _nextPingTime;
        private float _nextLevelSyncTime;
        private string _status = "Host idle.";
        private uint _sessionId;
        private uint _outSequence;
        private uint _lastClientSequence;
        private uint _doorSequence;
        private PlayerStateData _latestClientState;
        private bool _hasClientState;
        private float _nextStateLogTime;

        public HostSession(ITransport transport, Settings settings, LevelSync levelSync, DoorSync doorSync, ManualLogSource log, string buildId, string modVersion)
        {
            _transport = transport;
            _settings = settings;
            _levelSync = levelSync;
            _doorSync = doorSync;
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

        public string Status
        {
            get { return _status; }
        }

        public void OnLocalLevelChanged()
        {
            _playerLocator.Clear();
            DebugLog.Verbose("HostSession: local level changed, player locator reset.");
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
            _lastReceiveTime = Time.realtimeSinceStartup;
            _nextPingTime = _lastReceiveTime + _settings.GetKeepAliveSeconds();
            _nextLevelSyncTime = _lastReceiveTime + _settings.GetLevelSyncIntervalSeconds();
            _sessionId = 0;
            _outSequence = 0;
            _lastClientSequence = 0;
            _doorSequence = 0;
            _hasClientState = false;
            _status = _running ? "Hosting (waiting)" : "Host failed.";
            if (_running && _log != null)
            {
                _log.LogInfo("Host session started.");
            }
            DebugLog.Info("Host session start. Transport=" + _transport.Kind + " BindIP=" + _settings.HostBindIP.Value + " Port=" + _settings.HostPort.Value);
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
            _status = "Host stopped.";
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

            if (!_connected && _transport.Kind == TransportKind.TcpLan && _transport.IsConnected)
            {
                ForceTcpHandshake();
            }

            if (_connected)
            {
                float now = Time.realtimeSinceStartup;
                if (now - _lastReceiveTime > _settings.GetConnectionTimeoutSeconds())
                {
                    if (_log != null)
                    {
                        _log.LogWarning("Client timed out.");
                    }
                    ResetConnection("Hosting (waiting)");
                    return;
                }

                float interval = 1f / _settings.GetSendHzClamped();
                if (now >= _nextSendTime)
                {
                    _nextSendTime = now + interval;
                    SendPlayerState();
                    SendDoorStates(now);
                }

                if (now >= _nextPingTime)
                {
                    _nextPingTime = now + _settings.GetKeepAliveSeconds();
                    _transport.Send(Protocol.BuildPing(_sessionId, GetUnixTimeMs()), false);
                }

                if (now >= _nextLevelSyncTime)
                {
                    _nextLevelSyncTime = now + _settings.GetLevelSyncIntervalSeconds();
                    SendLevelChangeInternal(_levelSync.CurrentLevelIndex, _levelSync.CurrentLevelName, false);
                    DebugLog.Verbose("Level resync sent. Level=" + _levelSync.CurrentLevelName + " Index=" + _levelSync.CurrentLevelIndex);
                }
            }
        }

        public void SetProgressMarker(string marker)
        {
            _progressMarker = marker ?? string.Empty;
            if (_connected)
            {
                byte[] payload = Protocol.BuildProgressMarker(_sessionId, _progressMarker);
                _transport.Send(payload, _settings.ReliableForControl.Value);
            }
        }

        public void SendLevelChange(int levelIndex, string levelName)
        {
            SendLevelChangeInternal(levelIndex, levelName, true);
        }

        private void HandlePacket(TransportPacket packet)
        {
            if (_connected && packet.SenderId != 0ul && packet.SenderId != _clientSteamId)
            {
                if (_verbose && _log != null)
                {
                    _log.LogWarning("Ignored packet from non-authoritative sender " + packet.SenderId);
                }
                _transport.SendTo(packet.SenderId, Protocol.BuildDisconnect(0), true);
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
                DebugLog.Warn("Host parse failed: " + error + " (len=" + packet.Length + ")");
                return;
            }

            if (message.Type != MessageType.Hello)
            {
                if (!_connected)
                {
                    DebugLog.Verbose("Dropped " + message.Type + " (not connected).");
                    return;
                }
                if (message.SessionId != _sessionId)
                {
                    if (_verbose && _log != null)
                    {
                        _log.LogWarning("Ignored packet from stale session " + message.SessionId);
                    }
                    DebugLog.Verbose("Dropped " + message.Type + " (session mismatch " + message.SessionId + " != " + _sessionId + ").");
                    return;
                }
            }

            _lastReceiveTime = Time.realtimeSinceStartup;

            switch (message.Type)
            {
                case MessageType.Hello:
                    HandleHello(packet.SenderId, message.Hello);
                    break;
                case MessageType.Ping:
                    _transport.SendTo(packet.SenderId, Protocol.BuildPong(_sessionId, GetUnixTimeMs()), false);
                    break;
                case MessageType.Pong:
                    break;
                case MessageType.PlayerState:
                    if (IsNewerSequence(message.PlayerState.Sequence, _lastClientSequence))
                    {
                        _lastClientSequence = message.PlayerState.Sequence;
                        _latestClientState = message.PlayerState;
                        _hasClientState = true;
                    }
                    break;
                case MessageType.CameraState:
                    if (IsNewerSequence(message.CameraState.Sequence, _lastClientSequence))
                    {
                        _lastClientSequence = message.CameraState.Sequence;
                        _latestClientState = new PlayerStateData
                        {
                            SessionId = message.CameraState.SessionId,
                            Sequence = message.CameraState.Sequence,
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
                    }
                    break;
                case MessageType.Disconnect:
                    if (_verbose && _log != null)
                    {
                        _log.LogInfo("Client disconnected.");
                    }
                    ResetConnection("Hosting (waiting)");
                    break;
                case MessageType.DoorState:
                    if (_doorSync != null)
                    {
                        _doorSync.ApplyRemote(message.DoorState);
                    }
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
                _transport.SendTo(senderId, Protocol.BuildHelloReject(hello.ClientNonce, "Host already has a client."), true);
                return;
            }

            if (!IsAllowedClient(hello.SenderSteamId))
            {
                if (_log != null)
                {
                    _log.LogWarning("Rejected client " + hello.SenderSteamId + " (allowlist).");
                }
                _transport.SendTo(senderId, Protocol.BuildHelloReject(hello.ClientNonce, "SteamID not allowed."), true);
                return;
            }

            DebugLog.Info("Hello received. Sender=" + senderId + " SteamId=" + hello.SenderSteamId + " Build=" + hello.BuildId + " Mod=" + hello.ModVersion);

            if (!string.IsNullOrEmpty(_buildId) && hello.BuildId != _buildId && _log != null)
            {
                _log.LogWarning("Client buildId mismatch: " + hello.BuildId + " (host " + _buildId + ")");
            }
            if (!string.IsNullOrEmpty(_modVersion) && hello.ModVersion != _modVersion && _log != null)
            {
                _log.LogWarning("Client modVersion mismatch: " + hello.ModVersion + " (host " + _modVersion + ")");
            }

            bool reconnecting = _connected && senderId == _clientSteamId;
            _clientSteamId = senderId != 0 ? senderId : hello.SenderSteamId;
            _clientBuildId = hello.BuildId;
            _clientModVersion = hello.ModVersion;
            _connected = true;
            _sessionId = GenerateSessionId();
            _outSequence = 0;
            _lastClientSequence = 0;
            _hasClientState = false;
            _lastReceiveTime = Time.realtimeSinceStartup;
            _nextPingTime = _lastReceiveTime + _settings.GetKeepAliveSeconds();

            byte[] ack = Protocol.BuildHelloAck(_transport.LocalSteamId, hello.ClientNonce, _sessionId, _settings.GetSendHzClamped());
            _transport.SendTo(senderId, ack, true);

            DebugLog.Info("HelloAck sent. Session=" + _sessionId + " SendHz=" + _settings.GetSendHzClamped());

            SendLevelChangeInternal(_levelSync.CurrentLevelIndex, _levelSync.CurrentLevelName, true);
            if (!string.IsNullOrEmpty(_progressMarker))
            {
                _transport.Send(Protocol.BuildProgressMarker(_sessionId, _progressMarker), _settings.ReliableForControl.Value);
            }
            SendPlayerState();

            if (_log != null)
            {
                _log.LogInfo(reconnecting ? "Client reconnected: " + _clientSteamId : "Client connected: " + _clientSteamId);
            }
            _status = "Hosting (client connected)";
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
                float now = Time.realtimeSinceStartup;
                if (now >= _nextStateLogTime)
                {
                    DebugLog.Warn("Host: local player state unavailable (not sending).");
                    _nextStateLogTime = now + 2f;
                }
                return;
            }

            _outSequence++;
            state.SessionId = _sessionId;
            state.Sequence = _outSequence;
            byte[] payload = Protocol.BuildPlayerState(state);
            _transport.Send(payload, false);
        }

        private void SendDoorStates(float now)
        {
            if (_doorSync == null || !_doorSync.Enabled)
            {
                return;
            }

            long unixTimeMs = GetUnixTimeMs();
            int count = _doorSync.CollectChanges(unixTimeMs, now, _doorSendBuffer);
            for (int i = 0; i < count; i++)
            {
                DoorStateData state = _doorSendBuffer[i];
                _doorSequence++;
                state.SessionId = _sessionId;
                state.Sequence = _doorSequence;
                byte[] payload = Protocol.BuildDoorState(state);
                _transport.Send(payload, false);
            }
        }

        private void SendDisconnect()
        {
            _transport.Send(Protocol.BuildDisconnect(_sessionId), true);
        }

        private void ResetConnection(string status)
        {
            _connected = false;
            _clientSteamId = 0ul;
            _clientBuildId = null;
            _clientModVersion = null;
            _hasClientState = false;
            _sessionId = 0;
            _outSequence = 0;
            _lastClientSequence = 0;
            _status = status;
            DebugLog.Warn("Connection reset. Status=" + status);
        }

        private void ForceTcpHandshake()
        {
            _clientSteamId = 0ul;
            _clientBuildId = null;
            _clientModVersion = null;
            _connected = true;
            _sessionId = GenerateSessionId();
            _outSequence = 0;
            _lastClientSequence = 0;
            _hasClientState = false;
            _lastReceiveTime = Time.realtimeSinceStartup;
            _nextPingTime = _lastReceiveTime + _settings.GetKeepAliveSeconds();
            _nextLevelSyncTime = _lastReceiveTime + _settings.GetLevelSyncIntervalSeconds();

            byte[] ack = Protocol.BuildHelloAck(0ul, 0, _sessionId, _settings.GetSendHzClamped());
            _transport.Send(ack, true);

            SendLevelChangeInternal(_levelSync.CurrentLevelIndex, _levelSync.CurrentLevelName, true);
            if (!string.IsNullOrEmpty(_progressMarker))
            {
                _transport.Send(Protocol.BuildProgressMarker(_sessionId, _progressMarker), _settings.ReliableForControl.Value);
            }
            SendPlayerState();

            _status = "Hosting (client connected)";
            if (_log != null)
            {
                _log.LogInfo("TCP client connected (implicit handshake).");
            }
            DebugLog.Info("TCP client connected (implicit handshake). Session=" + _sessionId);
        }

        private void SendLevelChangeInternal(int levelIndex, string levelName, bool logInfo)
        {
            if (!_connected)
            {
                return;
            }
            byte[] payload = Protocol.BuildLevelChange(_sessionId, levelIndex, levelName);
            _transport.Send(payload, _settings.ReliableForControl.Value);

            if (logInfo)
            {
                DebugLog.Info("LevelChange sent. Session=" + _sessionId + " Index=" + levelIndex + " Name=" + levelName);
            }
            else
            {
                DebugLog.Verbose("LevelChange sent. Session=" + _sessionId + " Index=" + levelIndex + " Name=" + levelName);
            }
        }

        private static bool IsNewerSequence(uint seq, uint last)
        {
            if (seq == 0 || seq == last)
            {
                return false;
            }
            return seq > last;
        }

        private static uint GenerateSessionId()
        {
            return (uint)UnityEngine.Random.Range(1, int.MaxValue);
        }

        private static long GetUnixTimeMs()
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(DateTime.UtcNow - epoch).TotalMilliseconds;
        }

    }
}
