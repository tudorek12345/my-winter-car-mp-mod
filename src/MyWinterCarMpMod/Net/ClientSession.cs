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
        private readonly DoorSync _doorSync;
        private readonly VehicleSync _vehicleSync;
        private readonly PickupSync _pickupSync;
        private readonly TimeOfDaySync _timeSync;
        private readonly NpcSync _npcSync;

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
        private uint _doorSequence;
        private uint _doorHingeSequence;
        private uint _vehicleSequence;
        private uint _seatSequence;
        private uint _vehicleControlSequence;
        private uint _doorEventSequence;
        private uint _scrapeSequence;
        private uint _dashboardSequence;
        private uint _pickupSequence;
        private string _status = "Client idle";
        private readonly PlayerLocator _playerLocator = new PlayerLocator();
        private readonly System.Collections.Generic.List<DoorStateData> _doorSendBuffer = new System.Collections.Generic.List<DoorStateData>(32);
        private readonly System.Collections.Generic.List<DoorHingeStateData> _doorHingeSendBuffer = new System.Collections.Generic.List<DoorHingeStateData>(32);
        private readonly System.Collections.Generic.List<VehicleStateData> _vehicleSendBuffer = new System.Collections.Generic.List<VehicleStateData>(16);
        private readonly System.Collections.Generic.List<VehicleSeatData> _seatSendBuffer = new System.Collections.Generic.List<VehicleSeatData>(8);
        private readonly System.Collections.Generic.List<VehicleControlData> _vehicleControlSendBuffer = new System.Collections.Generic.List<VehicleControlData>(16);
        private readonly System.Collections.Generic.List<PickupStateData> _pickupSendBuffer = new System.Collections.Generic.List<PickupStateData>(32);
        private readonly System.Collections.Generic.List<DoorEventData> _doorEventSendBuffer = new System.Collections.Generic.List<DoorEventData>(16);
        private readonly System.Collections.Generic.List<ScrapeStateData> _scrapeSendBuffer = new System.Collections.Generic.List<ScrapeStateData>(16);
        private readonly System.Collections.Generic.List<OwnershipRequestData> _ownershipRequestBuffer = new System.Collections.Generic.List<OwnershipRequestData>(16);
        private float _nextStateLogTime;
        private int _pendingLevelIndex = int.MinValue;
        private string _pendingLevelName = string.Empty;
        private bool _sceneReadySent;
        private bool _worldStateReceived;
        private bool _pendingScrapeSnapshot;
        private float _nextScrapeSnapshotTime;
        private float _nextPlayerSendLogTime;
        private float _nextPlayerReceiveLogTime;
        private float _nextDoorReceiveLogTime;
        private float _nextTimeSyncLogTime;
        private float _nextOwnershipLogTime;

        public ClientSession(ITransport transport, Settings settings, LevelSync levelSync, DoorSync doorSync, VehicleSync vehicleSync, PickupSync pickupSync, TimeOfDaySync timeSync, NpcSync npcSync, ManualLogSource log, string buildId, string modVersion)
        {
            _transport = transport;
            _settings = settings;
            _levelSync = levelSync;
            _doorSync = doorSync;
            _vehicleSync = vehicleSync;
            _pickupSync = pickupSync;
            _timeSync = timeSync;
            _npcSync = npcSync;
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

        public void OnLocalLevelChanged()
        {
            _playerLocator.Clear();
            _worldStateReceived = false;
            _playerLocator.Warmup("ClientSession level change");
            DebugLog.Verbose("ClientSession: local level changed, player locator reset.");
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
            _doorSequence = 0;
            _doorHingeSequence = 0;
            _seatSequence = 0;
            _vehicleControlSequence = 0;
            _scrapeSequence = 0;
            _dashboardSequence = 0;
            _playerLocator.Clear();
            _pendingLevelIndex = int.MinValue;
            _pendingLevelName = string.Empty;
            _sceneReadySent = false;
            _worldStateReceived = false;
            _nextDoorReceiveLogTime = 0f;
            _nextPlayerSendLogTime = 0f;
            _nextPlayerReceiveLogTime = 0f;
            _nextDoorReceiveLogTime = 0f;
            _status = "Client idle";
            if (_vehicleSync != null)
            {
                _vehicleSync.ResetOwnership(OwnerKind.Client);
            }
            if (_pickupSync != null)
            {
                _pickupSync.ResetOwnership();
            }
            if (_npcSync != null)
            {
                _npcSync.Clear();
            }
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
                    if (_worldStateReceived)
                    {
                        SendDoorStates(now);
                        SendDoorHingeStates(now);
                        SendDoorEvents();
                        SendScrapeStates(now);
                        SendSorbetDashboardState(now);
                        SendVehicleSeatEvents();
                        SendVehicleControls(now);
                        SendVehicleStates(now);
                        SendPickupStates(now);
                        SendOwnershipRequests();
                    }
                }

                TrySendSceneReady();

                if (now - _lastReceiveTime > _settings.GetConnectionTimeoutSeconds())
                {
                    if (_levelSync != null && !_levelSync.IsReady)
                    {
                        // Loading scenes can stall updates; keep connection alive while loading.
                        _lastReceiveTime = now;
                    }
                    else
                    {
                    if (_log != null)
                    {
                        _log.LogWarning("Connection timed out.");
                    }
                    HandleConnectionLost("Timed out", true);
                    return;
                    }
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
                DebugLog.Warn("Client parse failed: " + error + " (len=" + packet.Length + ")");
                return;
            }

            if (message.Type != MessageType.HelloAck && message.Type != MessageType.HelloReject)
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
                    _worldStateReceived = false;
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
                        float receiveNow = Time.realtimeSinceStartup;
                        if (_verbose && receiveNow >= _nextPlayerReceiveLogTime)
                        {
                            DebugLog.Verbose("Client: recv PlayerState seq=" + message.PlayerState.Sequence +
                                " pos=" + message.PlayerState.PosX.ToString("F2") + "," +
                                message.PlayerState.PosY.ToString("F2") + "," +
                                message.PlayerState.PosZ.ToString("F2"));
                            _nextPlayerReceiveLogTime = receiveNow + 1f;
                        }
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
                        float receiveNowCamera = Time.realtimeSinceStartup;
                        if (_verbose && receiveNowCamera >= _nextPlayerReceiveLogTime)
                        {
                            DebugLog.Verbose("Client: recv CameraState seq=" + message.CameraState.Sequence +
                                " pos=" + message.CameraState.PosX.ToString("F2") + "," +
                                message.CameraState.PosY.ToString("F2") + "," +
                                message.CameraState.PosZ.ToString("F2"));
                            _nextPlayerReceiveLogTime = receiveNowCamera + 1f;
                        }
                    }
                    break;
                case MessageType.LevelChange:
                    DebugLog.Info("LevelChange received. Session=" + message.LevelChange.SessionId + " Index=" + message.LevelChange.LevelIndex + " Name=" + message.LevelChange.LevelName);
                    _levelSync.ApplyLevelChange(message.LevelChange.LevelIndex, message.LevelChange.LevelName);
                    _pendingLevelIndex = message.LevelChange.LevelIndex;
                    _pendingLevelName = message.LevelChange.LevelName ?? string.Empty;
                    _sceneReadySent = false;
                    _worldStateReceived = false;
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
                case MessageType.DoorState:
                    if (_doorSync != null)
                    {
                        if (_verbose && Time.realtimeSinceStartup >= _nextDoorReceiveLogTime)
                        {
                            DebugLog.Verbose("Client: recv DoorState id=" + message.DoorState.DoorId +
                                " seq=" + message.DoorState.Sequence);
                            _nextDoorReceiveLogTime = Time.realtimeSinceStartup + 1f;
                        }
                        _doorSync.ApplyRemote(message.DoorState);
                    }
                    break;
                case MessageType.DoorHingeState:
                    if (_doorSync != null)
                    {
                        if (_verbose && Time.realtimeSinceStartup >= _nextDoorReceiveLogTime)
                        {
                            DebugLog.Verbose("Client: recv DoorHinge id=" + message.DoorHingeState.DoorId +
                                " angle=" + message.DoorHingeState.Angle.ToString("F1") +
                                " seq=" + message.DoorHingeState.Sequence);
                            _nextDoorReceiveLogTime = Time.realtimeSinceStartup + 1f;
                        }
                        _doorSync.ApplyRemoteHinge(message.DoorHingeState);
                    }
                    break;
                case MessageType.DoorEvent:
                    if (_doorSync != null)
                    {
                        if (_verbose && Time.realtimeSinceStartup >= _nextDoorReceiveLogTime)
                        {
                            DebugLog.Verbose("Client: recv DoorEvent id=" + message.DoorEvent.DoorId +
                                " open=" + (message.DoorEvent.Open != 0) +
                                " seq=" + message.DoorEvent.Sequence);
                            _nextDoorReceiveLogTime = Time.realtimeSinceStartup + 1f;
                        }
                        _doorSync.ApplyRemoteEvent(message.DoorEvent);
                    }
                    break;
                case MessageType.ScrapeState:
                    if (_doorSync != null)
                    {
                        _doorSync.ApplyRemoteScrapeState(message.ScrapeState);
                    }
                    break;
                case MessageType.SorbetDashboardState:
                    if (_doorSync != null)
                    {
                        _doorSync.ApplySorbetDashboardState(message.SorbetDashboardState);
                    }
                    break;
                case MessageType.TimeState:
                    if (_timeSync != null)
                    {
                        if (_verbose && Time.realtimeSinceStartup >= _nextTimeSyncLogTime)
                        {
                            DebugLog.Verbose("TimeSync: recv sunIntensity=" + message.TimeState.SunIntensity.ToString("F2") +
                                " ambientIntensity=" + message.TimeState.AmbientIntensity.ToString("F2"));
                            _nextTimeSyncLogTime = Time.realtimeSinceStartup + 2f;
                        }
                        _timeSync.ApplyRemote(message.TimeState);
                    }
                    break;
                case MessageType.VehicleState:
                    if (_vehicleSync != null)
                    {
                        _vehicleSync.ApplyRemote(message.VehicleState, OwnerKind.Client, false);
                    }
                    break;
                case MessageType.VehicleSeat:
                    if (_vehicleSync != null)
                    {
                        _vehicleSync.ApplyRemoteSeat(message.VehicleSeat);
                    }
                    break;
                case MessageType.NpcState:
                    if (_npcSync != null)
                    {
                        _npcSync.ApplyRemote(message.NpcState, OwnerKind.Client);
                    }
                    break;
                case MessageType.PickupState:
                    if (_pickupSync != null)
                    {
                        _pickupSync.ApplyRemote(message.PickupState, OwnerKind.Client, false);
                    }
                    break;
                case MessageType.OwnershipUpdate:
                    if (_vehicleSync != null && message.OwnershipUpdate.Kind == SyncObjectKind.Vehicle)
                    {
                        _vehicleSync.ApplyOwnership(message.OwnershipUpdate, OwnerKind.Client);
                    }
                    if (_pickupSync != null && message.OwnershipUpdate.Kind == SyncObjectKind.Pickup)
                    {
                        _pickupSync.ApplyOwnership(message.OwnershipUpdate);
                    }
                    break;
                case MessageType.WorldState:
                    HandleWorldState(message.WorldState);
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
                float failNow = Time.realtimeSinceStartup;
                if (failNow >= _nextStateLogTime)
                {
                    DebugLog.Warn("Client: local player state unavailable (not sending).");
                    _nextStateLogTime = failNow + 2f;
                }
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

            float sendNow = Time.realtimeSinceStartup;
            if (_verbose && sendNow >= _nextPlayerSendLogTime)
            {
                DebugLog.Verbose("Client: sent PlayerState seq=" + _outSequence +
                    " pos=" + state.PosX.ToString("F2") + "," +
                    state.PosY.ToString("F2") + "," +
                    state.PosZ.ToString("F2"));
                _nextPlayerSendLogTime = sendNow + 1f;
            }
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
                if (_transport.Kind == TransportKind.SteamP2P)
                {
                    _transport.SendTo(_settings.SpectatorHostSteamId.Value, payload, false);
                }
                else
                {
                    _transport.Send(payload, false);
                }
            }
        }

        private void SendDoorHingeStates(float now)
        {
            if (_doorSync == null || !_doorSync.Enabled)
            {
                return;
            }

            long unixTimeMs = GetUnixTimeMs();
            int count = _doorSync.CollectHingeChanges(unixTimeMs, now, _doorHingeSendBuffer, OwnerKind.Client, false);
            for (int i = 0; i < count; i++)
            {
                DoorHingeStateData state = _doorHingeSendBuffer[i];
                _doorHingeSequence++;
                state.SessionId = _sessionId;
                state.Sequence = _doorHingeSequence;
                byte[] payload = Protocol.BuildDoorHingeState(state);
                if (_transport.Kind == TransportKind.SteamP2P)
                {
                    _transport.SendTo(_settings.SpectatorHostSteamId.Value, payload, false);
                }
                else
                {
                    _transport.Send(payload, false);
                }
            }
        }

        private void SendDoorEvents()
        {
            if (_doorSync == null || !_doorSync.Enabled)
            {
                return;
            }

            int count = _doorSync.CollectEvents(_doorEventSendBuffer);
            if (count > 0 && _verbose)
            {
                DebugLog.Verbose("Client: sending " + count + " door event(s).");
            }
            for (int i = 0; i < count; i++)
            {
                DoorEventData state = _doorEventSendBuffer[i];
                _doorEventSequence++;
                state.SessionId = _sessionId;
                state.Sequence = _doorEventSequence;
                byte[] payload = Protocol.BuildDoorEvent(state);
                if (_transport.Kind == TransportKind.SteamP2P)
                {
                    _transport.SendTo(_settings.SpectatorHostSteamId.Value, payload, _settings.ReliableForControl.Value);
                }
                else
                {
                    _transport.Send(payload, _settings.ReliableForControl.Value);
                }
            }
        }

        private void SendScrapeStates(float now)
        {
            if (_doorSync == null || !_doorSync.Enabled)
            {
                return;
            }

            if (_pendingScrapeSnapshot)
            {
                if (now < _nextScrapeSnapshotTime)
                {
                    return;
                }

                int snapshotCount = _doorSync.CollectScrapeSnapshot(_scrapeSendBuffer);
                _pendingScrapeSnapshot = false;
                if (snapshotCount > 0)
                {
                    SendScrapeBuffer(snapshotCount);
                    if (_verbose && now >= _nextPlayerSendLogTime)
                    {
                        DebugLog.Verbose("Client: sent scrape snapshot (" + snapshotCount + ").");
                        _nextPlayerSendLogTime = now + 1f;
                    }
                }
                return;
            }

            int count = _doorSync.CollectScrapeStates(_scrapeSendBuffer);
            if (count == 0)
            {
                return;
            }

            SendScrapeBuffer(count);

            if (_verbose && now >= _nextPlayerSendLogTime)
            {
                DebugLog.Verbose("Client: sent " + count + " scrape state update(s).");
            }
        }

        private void SendScrapeBuffer(int count)
        {
            if (count <= 0)
            {
                return;
            }

            long unixTimeMs = GetUnixTimeMs();
            for (int i = 0; i < count; i++)
            {
                ScrapeStateData state = _scrapeSendBuffer[i];
                _scrapeSequence++;
                state.SessionId = _sessionId;
                state.Sequence = _scrapeSequence;
                state.UnixTimeMs = unixTimeMs;
                byte[] payload = Protocol.BuildScrapeState(state);
                if (_transport.Kind == TransportKind.SteamP2P)
                {
                    _transport.SendTo(_settings.SpectatorHostSteamId.Value, payload, _settings.ReliableForControl.Value);
                }
                else
                {
                    _transport.Send(payload, _settings.ReliableForControl.Value);
                }
            }
        }

        private void ScheduleScrapeSnapshot(string reason)
        {
            _pendingScrapeSnapshot = true;
            _nextScrapeSnapshotTime = Time.realtimeSinceStartup + 1f;
            if (_verbose)
            {
                DebugLog.Verbose("Client: scheduled scrape snapshot (" + reason + ").");
            }
        }

        private void SendSorbetDashboardState(float now)
        {
            if (_doorSync == null || !_doorSync.Enabled || _vehicleSync == null || !_vehicleSync.Enabled)
            {
                return;
            }

            uint vehicleId;
            if (!_vehicleSync.TryGetLocalSorbetVehicleId(now, out vehicleId))
            {
                return;
            }

            SorbetDashboardStateData state;
            if (!_doorSync.TryBuildSorbetDashboardState(GetUnixTimeMs(), vehicleId, true, out state))
            {
                return;
            }

            _dashboardSequence++;
            state.SessionId = _sessionId;
            state.Sequence = _dashboardSequence;
            byte[] payload = Protocol.BuildSorbetDashboardState(state);
            if (_transport.Kind == TransportKind.SteamP2P)
            {
                _transport.SendTo(_settings.SpectatorHostSteamId.Value, payload, _settings.ReliableForControl.Value);
            }
            else
            {
                _transport.Send(payload, _settings.ReliableForControl.Value);
            }

            if (_verbose && now >= _nextPlayerSendLogTime)
            {
                DebugLog.Verbose("Client: sent sorbet dashboard state mask=" + state.Mask);
            }
        }

        private void SendVehicleControls(float now)
        {
            if (_vehicleSync == null || !_vehicleSync.Enabled)
            {
                return;
            }
            if (!_settings.VehicleSyncClientSend.Value)
            {
                return;
            }

            long unixTimeMs = GetUnixTimeMs();
            int count = _vehicleSync.CollectControlInputs(unixTimeMs, now, _vehicleControlSendBuffer, OwnerKind.Client);
            for (int i = 0; i < count; i++)
            {
                VehicleControlData control = _vehicleControlSendBuffer[i];
                _vehicleControlSequence++;
                control.SessionId = _sessionId;
                control.Sequence = _vehicleControlSequence;
                byte[] payload = Protocol.BuildVehicleControl(control);
                bool reliable = control.Flags != 0 && _settings.ReliableForControl.Value;
                if (_transport.Kind == TransportKind.SteamP2P)
                {
                    _transport.SendTo(_settings.SpectatorHostSteamId.Value, payload, reliable);
                }
                else
                {
                    _transport.Send(payload, reliable);
                }
            }
        }

        private void SendVehicleSeatEvents()
        {
            if (_vehicleSync == null || !_vehicleSync.Enabled)
            {
                return;
            }
            if (!_settings.VehicleSyncClientSend.Value)
            {
                return;
            }

            int count = _vehicleSync.CollectSeatEvents(_seatSendBuffer);
            if (count <= 0)
            {
                return;
            }

            long unixTimeMs = GetUnixTimeMs();
            for (int i = 0; i < count; i++)
            {
                VehicleSeatData state = _seatSendBuffer[i];
                _seatSequence++;
                state.SessionId = _sessionId;
                state.Sequence = _seatSequence;
                if (state.UnixTimeMs == 0)
                {
                    state.UnixTimeMs = unixTimeMs;
                }
                byte[] payload = Protocol.BuildVehicleSeat(state);
                if (_transport.Kind == TransportKind.SteamP2P)
                {
                    _transport.SendTo(_settings.SpectatorHostSteamId.Value, payload, _settings.ReliableForControl.Value);
                }
                else
                {
                    _transport.Send(payload, _settings.ReliableForControl.Value);
                }
            }
        }

        private void SendVehicleStates(float now)
        {
            if (_vehicleSync == null || !_vehicleSync.Enabled)
            {
                return;
            }
            if (!_settings.VehicleSyncClientSend.Value)
            {
                return;
            }

            long unixTimeMs = GetUnixTimeMs();
            int count = _vehicleSync.CollectChanges(unixTimeMs, now, _vehicleSendBuffer, OwnerKind.Client, false);
            for (int i = 0; i < count; i++)
            {
                VehicleStateData state = _vehicleSendBuffer[i];
                _vehicleSequence++;
                state.SessionId = _sessionId;
                state.Sequence = _vehicleSequence;
                byte[] payload = Protocol.BuildVehicleState(state);
                if (_transport.Kind == TransportKind.SteamP2P)
                {
                    _transport.SendTo(_settings.SpectatorHostSteamId.Value, payload, false);
                }
                else
                {
                    _transport.Send(payload, false);
                }
            }
        }

        private void SendPickupStates(float now)
        {
            if (_pickupSync == null || !_pickupSync.Enabled)
            {
                return;
            }
            if (!_settings.PickupSyncClientSend.Value)
            {
                return;
            }

            long unixTimeMs = GetUnixTimeMs();
            int count = _pickupSync.CollectChanges(unixTimeMs, now, _pickupSendBuffer, OwnerKind.Client, false);
            for (int i = 0; i < count; i++)
            {
                PickupStateData state = _pickupSendBuffer[i];
                _pickupSequence++;
                state.SessionId = _sessionId;
                state.Sequence = _pickupSequence;
                byte[] payload = Protocol.BuildPickupState(state);
                if (_transport.Kind == TransportKind.SteamP2P)
                {
                    _transport.SendTo(_settings.SpectatorHostSteamId.Value, payload, false);
                }
                else
                {
                    _transport.Send(payload, false);
                }
            }
        }

        private void SendOwnershipRequests()
        {
            float now = Time.realtimeSinceStartup;
            bool anyRequest = false;
            if (_vehicleSync != null && _settings.VehicleOwnershipEnabled.Value)
            {
                int count = _vehicleSync.CollectOwnershipRequests(OwnerKind.Client, _ownershipRequestBuffer);
                for (int i = 0; i < count; i++)
                {
                    OwnershipRequestData request = _ownershipRequestBuffer[i];
                    request.SessionId = _sessionId;
                    byte[] payload = Protocol.BuildOwnershipRequest(request);
                    if (_transport.Kind == TransportKind.SteamP2P)
                    {
                        _transport.SendTo(_settings.SpectatorHostSteamId.Value, payload, _settings.ReliableForControl.Value);
                    }
                    else
                    {
                        _transport.Send(payload, _settings.ReliableForControl.Value);
                    }

                    anyRequest = true;
                    if (_verbose && now >= _nextOwnershipLogTime)
                    {
                        DebugLog.Verbose("Ownership request sent. Kind=" + request.Kind +
                            " Id=" + request.ObjectId +
                            " Action=" + request.Action);
                        _nextOwnershipLogTime = now + 0.5f;
                    }
                }
            }

            if (_pickupSync != null && _settings.PickupSyncEnabled.Value)
            {
                int count = _pickupSync.CollectOwnershipRequests(OwnerKind.Client, _ownershipRequestBuffer);
                for (int i = 0; i < count; i++)
                {
                    OwnershipRequestData request = _ownershipRequestBuffer[i];
                    request.SessionId = _sessionId;
                    byte[] payload = Protocol.BuildOwnershipRequest(request);
                    if (_transport.Kind == TransportKind.SteamP2P)
                    {
                        _transport.SendTo(_settings.SpectatorHostSteamId.Value, payload, _settings.ReliableForControl.Value);
                    }
                    else
                    {
                        _transport.Send(payload, _settings.ReliableForControl.Value);
                    }

                    anyRequest = true;
                    if (_verbose && now >= _nextOwnershipLogTime)
                    {
                        DebugLog.Verbose("Ownership request sent. Kind=" + request.Kind +
                            " Id=" + request.ObjectId +
                            " Action=" + request.Action);
                        _nextOwnershipLogTime = now + 0.5f;
                    }
                }
            }

            if (!anyRequest && _verbose && now >= _nextOwnershipLogTime)
            {
                DebugLog.Verbose("Ownership request tick: none");
                _nextOwnershipLogTime = now + 1f;
            }
        }

        private void TrySendSceneReady()
        {
            if (_sceneReadySent || _pendingLevelIndex == int.MinValue || _levelSync == null)
            {
                return;
            }

            if (!_levelSync.IsReady)
            {
                return;
            }

            int currentIndex = _levelSync.CurrentLevelIndex;
            string currentName = _levelSync.CurrentLevelName ?? string.Empty;
            bool matchesIndex = _pendingLevelIndex >= 0 && currentIndex == _pendingLevelIndex;
            bool matchesName = !string.IsNullOrEmpty(_pendingLevelName) && currentName == _pendingLevelName;
            if (!matchesIndex && !matchesName)
            {
                return;
            }

            _playerLocator.Warmup("SceneReady");
            byte[] payload = Protocol.BuildSceneReady(_sessionId, currentIndex, currentName);
            if (_transport.Kind == TransportKind.SteamP2P)
            {
                _transport.SendTo(_settings.SpectatorHostSteamId.Value, payload, _settings.ReliableForControl.Value);
            }
            else
            {
                _transport.Send(payload, true);
            }
            _sceneReadySent = true;
            DebugLog.Info("SceneReady sent. Index=" + currentIndex + " Name=" + currentName);
        }

        private void HandleWorldState(WorldStateData state)
        {
            if (!_connected)
            {
                return;
            }

            if (_doorSync != null && state.Doors != null)
            {
                for (int i = 0; i < state.Doors.Length; i++)
                {
                    _doorSync.ApplyRemote(state.Doors[i]);
                }
            }

            if (_doorSync != null && state.DoorHinges != null)
            {
                for (int i = 0; i < state.DoorHinges.Length; i++)
                {
                    _doorSync.ApplyRemoteHinge(state.DoorHinges[i]);
                }
            }

            if (_vehicleSync != null && state.Vehicles != null)
            {
                for (int i = 0; i < state.Vehicles.Length; i++)
                {
                    _vehicleSync.ApplyRemote(state.Vehicles[i], OwnerKind.Client, false);
                }
            }

            if (_pickupSync != null && state.Pickups != null)
            {
                for (int i = 0; i < state.Pickups.Length; i++)
                {
                    _pickupSync.ApplyRemote(state.Pickups[i], OwnerKind.Client, false);
                }
            }

            if (_npcSync != null && state.Npcs != null)
            {
                for (int i = 0; i < state.Npcs.Length; i++)
                {
                    _npcSync.ApplyRemote(state.Npcs[i], OwnerKind.Client);
                }
            }

            if (state.Ownership != null)
            {
                for (int i = 0; i < state.Ownership.Length; i++)
                {
                    OwnershipUpdateData update = state.Ownership[i];
                    if (_vehicleSync != null && update.Kind == SyncObjectKind.Vehicle)
                    {
                        _vehicleSync.ApplyOwnership(update, OwnerKind.Client);
                    }
                    if (_pickupSync != null && update.Kind == SyncObjectKind.Pickup)
                    {
                        _pickupSync.ApplyOwnership(update);
                    }
                }
            }

            _worldStateReceived = true;
            ScheduleScrapeSnapshot("world state");

            byte[] ack = Protocol.BuildWorldStateAck(_sessionId);
            if (_transport.Kind == TransportKind.SteamP2P)
            {
                _transport.SendTo(_settings.SpectatorHostSteamId.Value, ack, true);
            }
            else
            {
                _transport.Send(ack, true);
            }

            DebugLog.Info("World state applied. Doors=" + (state.Doors != null ? state.Doors.Length : 0) +
                          " DoorHinges=" + (state.DoorHinges != null ? state.DoorHinges.Length : 0) +
                          " Vehicles=" + (state.Vehicles != null ? state.Vehicles.Length : 0) +
                          " Pickups=" + (state.Pickups != null ? state.Pickups.Length : 0) +
                          " Npcs=" + (state.Npcs != null ? state.Npcs.Length : 0) +
                          " | Ack sent Session=" + _sessionId + " Host=" + _hostSteamId);
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
            _doorSequence = 0;
            _doorHingeSequence = 0;
            _vehicleSequence = 0;
            _seatSequence = 0;
            _vehicleControlSequence = 0;
            _doorEventSequence = 0;
            _scrapeSequence = 0;
            _dashboardSequence = 0;
            _pickupSequence = 0;
            _serverSendHz = 0;
            _progressMarker = string.Empty;
            _pendingLevelIndex = int.MinValue;
            _pendingLevelName = string.Empty;
            _sceneReadySent = false;
            _worldStateReceived = false;

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
            _vehicleSequence = 0;
            _doorHingeSequence = 0;
            _pendingLevelIndex = int.MinValue;
            _pendingLevelName = string.Empty;
            _sceneReadySent = false;
            _serverSendHz = 0;
            _worldStateReceived = false;
            if (_vehicleSync != null)
            {
                _vehicleSync.ResetOwnership(OwnerKind.Client);
            }
            if (_pickupSync != null)
            {
                _pickupSync.ResetOwnership();
            }

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
