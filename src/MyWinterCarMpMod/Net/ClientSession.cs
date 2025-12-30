using BepInEx.Logging;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Sync;
using MyWinterCarMpMod.Util;
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
        private bool _awaitingHelloAck;
        private bool _hasHostState;
        private ulong _hostSteamId;
        private int _serverSendHz;
        private PlayerStateData _latestHostState;
        private string _progressMarker = string.Empty;
        private float _nextSendTime;
        private float _lastReceiveTime;
        private float _nextPingTime;
        private float _nextHelloTime;
        private float _connectDeadline;
        private float _nextReconnectTime;
        private bool _pendingReconnect;
        private int _reconnectAttempts;
        private uint _clientNonce;
        private uint _sessionId;
        private uint _outSequence;
        private uint _lastHostSequence;
        private string _status = "Client idle";
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

        public string Status
        {
            get { return _status; }
        }

        public bool Connect()
        {
            return StartConnect(true);
        }

        public void Disconnect()
        {
            if (_connected)
            {
                _transport.Send(Protocol.BuildDisconnect(_sessionId), true);
            }
            _transport.Disconnect();
            _running = false;
            _connected = false;
            _awaitingHelloAck = false;
            _hasHostState = false;
            _pendingReconnect = false;
            _reconnectAttempts = 0;
            _sessionId = 0;
            _playerLocator.Clear();
            _status = "Client idle";
            DebugLog.Info("Client disconnected.");
        }

        public void Update()
        {
            float now = Time.realtimeSinceStartup;

            if (!_running)
            {
                if (_pendingReconnect && now >= _nextReconnectTime)
                {
                    AttemptReconnect();
                }
                return;
            }

            TransportPacket packet;
            while (_transport.TryReceive(out packet))
            {
                HandlePacket(packet);
            }

            if (_connected)
            {
                float interval = 1f / _settings.GetSendHzClamped();
                if (now >= _nextSendTime)
                {
                    _nextSendTime = now + interval;
                    SendPlayerState();
                }

                if (now - _lastReceiveTime > _settings.GetConnectionTimeoutSeconds())
                {
                    if (_log != null)
                    {
                        _log.LogWarning("Connection timed out.");
                    }
                    HandleConnectionLost("Timed out", true);
                    return;
                }

                if (now >= _nextPingTime)
                {
                    _nextPingTime = now + _settings.GetKeepAliveSeconds();
                    _transport.Send(BuildPing(), false);
                }
            }
            else if (_awaitingHelloAck)
            {
                if (now >= _nextHelloTime)
                {
                    _nextHelloTime = now + _settings.GetHelloRetrySeconds();
                    SendHello();
                }

                if (now >= _connectDeadline)
                {
                    HandleConnectionLost("Connect timed out", true);
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
                    _transport.SendTo(packet.SenderId, Protocol.BuildDisconnect(0), true);
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

            if (message.Type != MessageType.HelloAck && message.Type != MessageType.HelloReject)
            {
                if (!_connected)
                {
                    return;
                }
                if (message.SessionId != _sessionId)
                {
                    if (_verbose && _log != null)
                    {
                        _log.LogWarning("Ignored packet from stale session " + message.SessionId);
                    }
                    return;
                }
            }

            _lastReceiveTime = Time.realtimeSinceStartup;

            switch (message.Type)
            {
                case MessageType.HelloAck:
                    if (!IsAuthoritativeHelloAck(packet.SenderId, message.HelloAck.HostSteamId))
                    {
                        return;
                    }
                    if (message.HelloAck.ClientNonce != _clientNonce)
                    {
                        if (!(_transport.Kind == TransportKind.TcpLan && message.HelloAck.ClientNonce == 0))
                        {
                            if (_log != null)
                            {
                                _log.LogWarning("HelloAck nonce mismatch.");
                            }
                            return;
                        }
                    }
                    _connected = true;
                    _hostSteamId = message.HelloAck.HostSteamId;
                    _serverSendHz = message.HelloAck.ServerSendHz;
                    _sessionId = message.HelloAck.SessionId;
                    _awaitingHelloAck = false;
                    _lastHostSequence = 0;
                    _outSequence = 0;
                    _nextPingTime = _lastReceiveTime + _settings.GetKeepAliveSeconds();
                    _status = "Connected";
                    if (_log != null)
                    {
                        _log.LogInfo("Connected to host " + _hostSteamId);
                    }
                    DebugLog.Info("HelloAck received. Session=" + _sessionId + " Host=" + _hostSteamId + " SendHz=" + _serverSendHz);
                    break;
                case MessageType.HelloReject:
                    if (message.HelloReject.ClientNonce != _clientNonce)
                    {
                        return;
                    }
                    if (_log != null)
                    {
                        _log.LogWarning("Connection rejected: " + message.HelloReject.Reason);
                    }
                    HandleConnectionLost("Rejected", false);
                    break;
                case MessageType.PlayerState:
                    if (IsNewerSequence(message.PlayerState.Sequence, _lastHostSequence))
                    {
                        _lastHostSequence = message.PlayerState.Sequence;
                        _latestHostState = message.PlayerState;
                        _hasHostState = true;
                    }
                    break;
                case MessageType.CameraState:
                    if (IsNewerSequence(message.CameraState.Sequence, _lastHostSequence))
                    {
                        _lastHostSequence = message.CameraState.Sequence;
                        _latestHostState = new PlayerStateData
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
                        _hasHostState = true;
                    }
                    break;
                case MessageType.LevelChange:
                    DebugLog.Info("LevelChange received. Session=" + message.LevelChange.SessionId + " Index=" + message.LevelChange.LevelIndex + " Name=" + message.LevelChange.LevelName);
                    _levelSync.ApplyLevelChange(message.LevelChange.LevelIndex, message.LevelChange.LevelName);
                    break;
                case MessageType.ProgressMarker:
                    _progressMarker = message.ProgressMarker.Marker;
                    break;
                case MessageType.Ping:
                    _transport.Send(Protocol.BuildPong(_sessionId, GetUnixTimeMs()), false);
                    break;
                case MessageType.Pong:
                    break;
                case MessageType.Disconnect:
                    if (_log != null)
                    {
                        _log.LogWarning("Host disconnected.");
                    }
                    HandleConnectionLost("Disconnected", true);
                    break;
            }
        }

        private void SendHello()
        {
            byte[] payload = Protocol.BuildHello(_transport.LocalSteamId, _clientNonce, _buildId, _modVersion);
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

            _outSequence++;
            state.SessionId = _sessionId;
            state.Sequence = _outSequence;
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

        private bool StartConnect(bool resetAttempts)
        {
            if (_transport.Kind == TransportKind.SteamP2P && _settings.SpectatorHostSteamId.Value == 0)
            {
                if (_log != null)
                {
                    _log.LogWarning("SpectatorHostSteamId is 0. Set host SteamID64 before connecting.");
                }
                _status = "Missing host SteamID";
                return false;
            }

            if (resetAttempts)
            {
                _reconnectAttempts = 0;
            }

            _running = _transport.Connect();
            _connected = false;
            _awaitingHelloAck = _running;
            _hasHostState = false;
            _pendingReconnect = false;
            _hostSteamId = _settings.SpectatorHostSteamId.Value;
            _nextSendTime = Time.realtimeSinceStartup;
            _lastReceiveTime = _nextSendTime;
            _nextPingTime = _nextSendTime + _settings.GetKeepAliveSeconds();
            _nextHelloTime = _nextSendTime;
            _connectDeadline = _nextSendTime + _settings.GetConnectionTimeoutSeconds();
            _clientNonce = GenerateNonce();
            _sessionId = 0;
            _outSequence = 0;
            _lastHostSequence = 0;
            _serverSendHz = 0;
            _progressMarker = string.Empty;

            if (_running)
            {
                SendHello();
                _status = "Connecting";
                if (_log != null)
                {
                    _log.LogInfo("Client connect requested.");
                }
                DebugLog.Info("Client connecting. Transport=" + _transport.Kind + " HostSteamId=" + _settings.SpectatorHostSteamId.Value + " HostIp=" + _settings.SpectatorHostIP.Value + " Port=" + _settings.HostPort.Value);
            }
            else
            {
                _status = "Connect failed";
            }
            return _running;
        }

        private void HandleConnectionLost(string reason, bool allowReconnect)
        {
            _connected = false;
            _running = false;
            _awaitingHelloAck = false;
            _hasHostState = false;
            _hostSteamId = 0ul;
            _sessionId = 0;
            _transport.Disconnect();
            _status = reason;
            _playerLocator.Clear();
            _progressMarker = string.Empty;
            _serverSendHz = 0;

            if (allowReconnect && _settings.AutoReconnect.Value)
            {
                ScheduleReconnect(reason);
            }
        }

        private void ScheduleReconnect(string reason)
        {
            _reconnectAttempts++;
            int maxAttempts = _settings.GetMaxReconnectAttempts();
            if (maxAttempts > 0 && _reconnectAttempts > maxAttempts)
            {
                if (_log != null)
                {
                    _log.LogWarning("Reconnect attempts exhausted.");
                }
                _pendingReconnect = false;
                return;
            }

            _nextReconnectTime = Time.realtimeSinceStartup + _settings.GetReconnectDelaySeconds();
            _pendingReconnect = true;
            _status = "Reconnecting";
            if (_log != null)
            {
                _log.LogInfo("Reconnect scheduled (" + reason + ").");
            }
        }

        private void AttemptReconnect()
        {
            _pendingReconnect = false;
            StartConnect(false);
        }

        private byte[] BuildPing()
        {
            return Protocol.BuildPing(_sessionId, GetUnixTimeMs());
        }

        private static bool IsNewerSequence(uint seq, uint last)
        {
            if (seq == 0 || seq == last)
            {
                return false;
            }
            return seq > last;
        }

        private static uint GenerateNonce()
        {
            return (uint)UnityEngine.Random.Range(1, int.MaxValue);
        }

        private static long GetUnixTimeMs()
        {
            System.DateTime epoch = new System.DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
            return (long)(System.DateTime.UtcNow - epoch).TotalMilliseconds;
        }
    }
}
