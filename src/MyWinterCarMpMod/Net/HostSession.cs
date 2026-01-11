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
        private readonly System.Collections.Generic.List<DoorHingeStateData> _doorHingeSendBuffer = new System.Collections.Generic.List<DoorHingeStateData>(32);
        private readonly VehicleSync _vehicleSync;
        private readonly System.Collections.Generic.List<VehicleStateData> _vehicleSendBuffer = new System.Collections.Generic.List<VehicleStateData>(16);
        private readonly PickupSync _pickupSync;
        private readonly System.Collections.Generic.List<PickupStateData> _pickupSendBuffer = new System.Collections.Generic.List<PickupStateData>(32);
        private readonly System.Collections.Generic.List<DoorEventData> _doorEventSendBuffer = new System.Collections.Generic.List<DoorEventData>(16);
        private readonly System.Collections.Generic.List<OwnershipUpdateData> _ownershipUpdateBuffer = new System.Collections.Generic.List<OwnershipUpdateData>(16);

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
        private uint _doorHingeSequence;
        private uint _vehicleSequence;
        private uint _doorEventSequence;
        private uint _pickupSequence;
        private PlayerStateData _latestClientState;
        private bool _hasClientState;
        private float _nextStateLogTime;
        private bool _clientSceneReady;
        private int _clientSceneLevelIndex = int.MinValue;
        private string _clientSceneLevelName = string.Empty;
        private bool _awaitWorldStateAck;
        private bool _worldStateAcked;
        private float _nextWorldStateTime;
        private int _worldStateAttempts;
        private float _nextPlayerSendLogTime;
        private float _nextPlayerReceiveLogTime;

        public HostSession(ITransport transport, Settings settings, LevelSync levelSync, DoorSync doorSync, VehicleSync vehicleSync, PickupSync pickupSync, ManualLogSource log, string buildId, string modVersion)
        {
            _transport = transport;
            _settings = settings;
            _levelSync = levelSync;
            _doorSync = doorSync;
            _vehicleSync = vehicleSync;
            _pickupSync = pickupSync;
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
            _clientSceneReady = false;
            _awaitWorldStateAck = false;
            _worldStateAcked = false;
            _playerLocator.Warmup("HostSession level change");
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
            _doorHingeSequence = 0;
            _vehicleSequence = 0;
            _doorEventSequence = 0;
            _pickupSequence = 0;
            _hasClientState = false;
            _clientSceneReady = false;
            _clientSceneLevelIndex = int.MinValue;
            _clientSceneLevelName = string.Empty;
            _awaitWorldStateAck = false;
            _worldStateAcked = false;
            _nextWorldStateTime = 0f;
            _worldStateAttempts = 0;
            _nextPlayerSendLogTime = 0f;
            _nextPlayerReceiveLogTime = 0f;
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
            _clientSceneReady = false;
            _clientSceneLevelIndex = int.MinValue;
            _clientSceneLevelName = string.Empty;
            _awaitWorldStateAck = false;
            _worldStateAcked = false;
            _worldStateAttempts = 0;
            if (_vehicleSync != null)
            {
                _vehicleSync.ResetOwnership(OwnerKind.Host);
            }
            if (_pickupSync != null)
            {
                _pickupSync.ResetOwnership();
            }
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

            if (_connected)
            {
                float now = Time.realtimeSinceStartup;
                if (now - _lastReceiveTime > _settings.GetConnectionTimeoutSeconds())
                {
                    if (!_clientSceneReady)
                    {
                        // Give clients extra time while they load into the scene.
                        _lastReceiveTime = now;
                    }
                    else
                    {
                    if (_log != null)
                    {
                        _log.LogWarning("Client timed out.");
                    }
                    ResetConnection("Hosting (waiting)");
                    return;
                    }
                }

                if (_clientSceneReady && _awaitWorldStateAck && now >= _nextWorldStateTime)
                {
                    SendWorldState();
                }

                float interval = 1f / _settings.GetSendHzClamped();
                if (now >= _nextSendTime)
                {
                    _nextSendTime = now + interval;
                    SendPlayerState();
                    if (_clientSceneReady)
                    {
                        SendDoorStates(now);
                        SendDoorHingeStates(now);
                        SendDoorEvents();
                    }
                    if (_clientSceneReady && _worldStateAcked)
                    {
                        SendVehicleStates(now);
                        SendPickupStates(now);
                        SendOwnershipUpdates();
                    }
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
                        EnsureWorldStateAcked("client player state");
                        float receiveNow = Time.realtimeSinceStartup;
                        if (_verbose && receiveNow >= _nextPlayerReceiveLogTime)
                        {
                            DebugLog.Verbose("Host: recv PlayerState seq=" + message.PlayerState.Sequence +
                                " pos=" + message.PlayerState.PosX.ToString("F2") + "," +
                                message.PlayerState.PosY.ToString("F2") + "," +
                                message.PlayerState.PosZ.ToString("F2"));
                            _nextPlayerReceiveLogTime = receiveNow + 1f;
                        }
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
                        EnsureWorldStateAcked("client camera state");
                        float receiveNowCamera = Time.realtimeSinceStartup;
                        if (_verbose && receiveNowCamera >= _nextPlayerReceiveLogTime)
                        {
                            DebugLog.Verbose("Host: recv CameraState seq=" + message.CameraState.Sequence +
                                " pos=" + message.CameraState.PosX.ToString("F2") + "," +
                                message.CameraState.PosY.ToString("F2") + "," +
                                message.CameraState.PosZ.ToString("F2"));
                            _nextPlayerReceiveLogTime = receiveNowCamera + 1f;
                        }
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
                case MessageType.DoorHingeState:
                    if (_doorSync != null)
                    {
                        _doorSync.ApplyRemoteHinge(message.DoorHingeState);
                    }
                    break;
                case MessageType.DoorEvent:
                    if (_doorSync != null)
                    {
                        _doorSync.ApplyRemoteEvent(message.DoorEvent);
                    }
                    break;
                case MessageType.SceneReady:
                    HandleSceneReady(message.SceneReady);
                    break;
                case MessageType.VehicleState:
                    if (_vehicleSync != null && _settings.VehicleSyncClientSend.Value)
                    {
                        _vehicleSync.ApplyRemote(message.VehicleState, OwnerKind.Host, true);
                    }
                    break;
                case MessageType.PickupState:
                    if (_pickupSync != null && _settings.PickupSyncClientSend.Value)
                    {
                        _pickupSync.ApplyRemote(message.PickupState, OwnerKind.Host, true);
                    }
                    break;
                case MessageType.OwnershipRequest:
                    HandleOwnershipRequest(message.OwnershipRequest);
                    break;
                case MessageType.OwnershipUpdate:
                    break;
                case MessageType.WorldStateAck:
                    HandleWorldStateAck();
                    break;
            }
        }

        private void HandleSceneReady(SceneReadyData ready)
        {
            if (!_connected)
            {
                return;
            }

            _clientSceneLevelIndex = ready.LevelIndex;
            _clientSceneLevelName = ready.LevelName ?? string.Empty;

            bool matchesIndex = ready.LevelIndex >= 0 && ready.LevelIndex == _levelSync.CurrentLevelIndex;
            bool matchesName = !string.IsNullOrEmpty(ready.LevelName) && ready.LevelName == _levelSync.CurrentLevelName;
            if (matchesIndex || matchesName)
            {
                _clientSceneReady = true;
                _worldStateAcked = false;
                _awaitWorldStateAck = true;
                _worldStateAttempts = 0;
                _nextWorldStateTime = Time.realtimeSinceStartup;
                if (_log != null)
                {
                    _log.LogInfo("Client scene ready: " + _clientSceneLevelName + " (" + _clientSceneLevelIndex + ")");
                }
                _status = "Hosting (client connected)";
            }
            else
            {
                if (_log != null)
                {
                    _log.LogWarning("Client scene ready mismatch: " + _clientSceneLevelName + " (" + _clientSceneLevelIndex + ") vs host " + _levelSync.CurrentLevelName + " (" + _levelSync.CurrentLevelIndex + ")");
                }
                _clientSceneReady = false;
                _status = "Hosting (client loading)";
            }
        }

        private void HandleWorldStateAck()
        {
            if (!_connected)
            {
                return;
            }

            _awaitWorldStateAck = false;
            _worldStateAcked = true;
            if (_log != null)
            {
                _log.LogInfo("World state acknowledged by client. Attempts=" + _worldStateAttempts + " Session=" + _sessionId);
            }
        }

        private void EnsureWorldStateAcked(string reason)
        {
            if (!_awaitWorldStateAck || _worldStateAcked || !_connected)
            {
                return;
            }

            _awaitWorldStateAck = false;
            _worldStateAcked = true;
            if (_log != null)
            {
                _log.LogInfo("World state acknowledged (implicit: " + reason + "). Attempts=" + _worldStateAttempts + " Session=" + _sessionId);
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
            _clientSceneReady = false;
            _clientSceneLevelIndex = int.MinValue;
            _clientSceneLevelName = string.Empty;
            _doorSequence = 0;
            _doorHingeSequence = 0;
            _vehicleSequence = 0;
            _doorEventSequence = 0;
            _pickupSequence = 0;
            _awaitWorldStateAck = false;
            _worldStateAcked = false;
            _worldStateAttempts = 0;
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
            _status = "Hosting (client loading)";
        }

        private void HandleOwnershipRequest(OwnershipRequestData request)
        {
            OwnershipUpdateData update = default(OwnershipUpdateData);
            bool handled = false;

            if (request.Kind == SyncObjectKind.Vehicle && _vehicleSync != null)
            {
                handled = _vehicleSync.TryHandleOwnershipRequest(request, out update);
                if (handled)
                {
                    _vehicleSync.ApplyOwnership(update, OwnerKind.Host);
                }
            }
            else if (request.Kind == SyncObjectKind.Pickup && _pickupSync != null)
            {
                handled = _pickupSync.TryHandleOwnershipRequest(request, out update);
                if (handled)
                {
                    _pickupSync.ApplyOwnership(update);
                }
            }

            if (!handled)
            {
                return;
            }

            update.SessionId = _sessionId;
            byte[] payload = Protocol.BuildOwnershipUpdate(update);
            _transport.Send(payload, _settings.ReliableForControl.Value);
            DebugLog.Verbose("Ownership update sent. Kind=" + update.Kind + " Id=" + update.ObjectId + " Owner=" + update.Owner);
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
                float failNow = Time.realtimeSinceStartup;
                if (failNow >= _nextStateLogTime)
                {
                    DebugLog.Warn("Host: local player state unavailable (not sending).");
                    _nextStateLogTime = failNow + 2f;
                }
                return;
            }

            _outSequence++;
            state.SessionId = _sessionId;
            state.Sequence = _outSequence;
            byte[] payload = Protocol.BuildPlayerState(state);
            _transport.Send(payload, false);

            float sendNow = Time.realtimeSinceStartup;
            if (_verbose && sendNow >= _nextPlayerSendLogTime)
            {
                DebugLog.Verbose("Host: sent PlayerState seq=" + _outSequence +
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
                _transport.Send(payload, false);
            }
        }

        private void SendDoorHingeStates(float now)
        {
            if (_doorSync == null || !_doorSync.Enabled)
            {
                return;
            }

            long unixTimeMs = GetUnixTimeMs();
            int count = _doorSync.CollectHingeChanges(unixTimeMs, now, _doorHingeSendBuffer);
            for (int i = 0; i < count; i++)
            {
                DoorHingeStateData state = _doorHingeSendBuffer[i];
                _doorHingeSequence++;
                state.SessionId = _sessionId;
                state.Sequence = _doorHingeSequence;
                byte[] payload = Protocol.BuildDoorHingeState(state);
                _transport.Send(payload, false);
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
                DebugLog.Verbose("Host: sending " + count + " door event(s).");
            }
            for (int i = 0; i < count; i++)
            {
                DoorEventData state = _doorEventSendBuffer[i];
                _doorEventSequence++;
                state.SessionId = _sessionId;
                state.Sequence = _doorEventSequence;
                byte[] payload = Protocol.BuildDoorEvent(state);
                _transport.Send(payload, _settings.ReliableForControl.Value);
            }
        }

        private void SendVehicleStates(float now)
        {
            if (_vehicleSync == null || !_vehicleSync.Enabled)
            {
                return;
            }

            long unixTimeMs = GetUnixTimeMs();
            int count = _vehicleSync.CollectChanges(unixTimeMs, now, _vehicleSendBuffer, OwnerKind.Host, true);
            for (int i = 0; i < count; i++)
            {
                VehicleStateData state = _vehicleSendBuffer[i];
                _vehicleSequence++;
                state.SessionId = _sessionId;
                state.Sequence = _vehicleSequence;
                byte[] payload = Protocol.BuildVehicleState(state);
                _transport.Send(payload, false);
            }
        }

        private void SendPickupStates(float now)
        {
            if (_pickupSync == null || !_pickupSync.Enabled)
            {
                return;
            }

            long unixTimeMs = GetUnixTimeMs();
            int count = _pickupSync.CollectChanges(unixTimeMs, now, _pickupSendBuffer, OwnerKind.Host, true);
            for (int i = 0; i < count; i++)
            {
                PickupStateData state = _pickupSendBuffer[i];
                _pickupSequence++;
                state.SessionId = _sessionId;
                state.Sequence = _pickupSequence;
                byte[] payload = Protocol.BuildPickupState(state);
                _transport.Send(payload, false);
            }
        }

        private void SendOwnershipUpdates()
        {
            if (_vehicleSync != null && _settings.VehicleOwnershipEnabled.Value)
            {
                int count = _vehicleSync.CollectOwnershipUpdates(OwnerKind.Host, _ownershipUpdateBuffer);
                for (int i = 0; i < count; i++)
                {
                    OwnershipUpdateData update = _ownershipUpdateBuffer[i];
                    update.SessionId = _sessionId;
                    byte[] payload = Protocol.BuildOwnershipUpdate(update);
                    _transport.Send(payload, _settings.ReliableForControl.Value);
                }
            }

            if (_pickupSync != null && _settings.PickupSyncEnabled.Value)
            {
                int count = _pickupSync.CollectOwnershipUpdates(OwnerKind.Host, _ownershipUpdateBuffer);
                for (int i = 0; i < count; i++)
                {
                    OwnershipUpdateData update = _ownershipUpdateBuffer[i];
                    update.SessionId = _sessionId;
                    byte[] payload = Protocol.BuildOwnershipUpdate(update);
                    _transport.Send(payload, _settings.ReliableForControl.Value);
                }
            }
        }

        private void SendDisconnect()
        {
            _transport.Send(Protocol.BuildDisconnect(_sessionId), true);
        }

        private void SendWorldState()
        {
            if (!_clientSceneReady || !_connected)
            {
                if (_verbose && _log != null)
                {
                    _log.LogDebug("WorldState send skipped. Connected=" + _connected + " ClientReady=" + _clientSceneReady);
                }
                return;
            }

            _worldStateAttempts++;
            _nextWorldStateTime = Time.realtimeSinceStartup + 1.5f;

            long unixTimeMs = GetUnixTimeMs();

            DoorStateData[] doors = _doorSync != null ? _doorSync.BuildSnapshot(unixTimeMs, _sessionId) : new DoorStateData[0];
            DoorHingeStateData[] doorHinges = _doorSync != null ? _doorSync.BuildHingeSnapshot(unixTimeMs, _sessionId) : new DoorHingeStateData[0];
            VehicleStateData[] vehicles = _vehicleSync != null ? _vehicleSync.BuildSnapshot(unixTimeMs, _sessionId) : new VehicleStateData[0];
            PickupStateData[] pickups = _pickupSync != null ? _pickupSync.BuildSnapshot(unixTimeMs, _sessionId) : new PickupStateData[0];

            System.Collections.Generic.List<OwnershipUpdateData> ownershipList = new System.Collections.Generic.List<OwnershipUpdateData>();
            if (_vehicleSync != null)
            {
                ownershipList.AddRange(_vehicleSync.BuildOwnershipSnapshot());
            }
            if (_pickupSync != null)
            {
                ownershipList.AddRange(_pickupSync.BuildOwnershipSnapshot());
            }

            for (int i = 0; i < ownershipList.Count; i++)
            {
                OwnershipUpdateData update = ownershipList[i];
                update.SessionId = _sessionId;
                ownershipList[i] = update;
            }

            WorldStateData state = new WorldStateData
            {
                SessionId = _sessionId,
                Doors = doors,
                DoorHinges = doorHinges,
                Vehicles = vehicles,
                Pickups = pickups,
                Ownership = ownershipList.ToArray()
            };

            byte[] payload = Protocol.BuildWorldState(state);
            _transport.Send(payload, true);

            if (_doorSequence < 1)
            {
                _doorSequence = 1;
            }
            if (_doorHingeSequence < 1)
            {
                _doorHingeSequence = 1;
            }
            if (_vehicleSequence < 1)
            {
                _vehicleSequence = 1;
            }
            if (_pickupSequence < 1)
            {
                _pickupSequence = 1;
            }

            if (_log != null)
            {
                _log.LogDebug("WorldState sending attempt " + _worldStateAttempts + " Session=" + _sessionId + " Doors=" + doors.Length + " DoorHinges=" + doorHinges.Length + " Vehicles=" + vehicles.Length + " Pickups=" + pickups.Length + " Ownership=" + ownershipList.Count);
            }
            DebugLog.Verbose("WorldState sent. Doors=" + doors.Length + " DoorHinges=" + doorHinges.Length + " Vehicles=" + vehicles.Length + " Pickups=" + pickups.Length + " Ownership=" + ownershipList.Count);
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
            _vehicleSequence = 0;
            _doorHingeSequence = 0;
            _clientSceneReady = false;
            _clientSceneLevelIndex = int.MinValue;
            _clientSceneLevelName = string.Empty;
            _awaitWorldStateAck = false;
            _worldStateAcked = false;
            _worldStateAttempts = 0;
            _status = status;
            if (_vehicleSync != null)
            {
                _vehicleSync.ResetOwnership(OwnerKind.Host);
            }
            if (_pickupSync != null)
            {
                _pickupSync.ResetOwnership();
            }
            DebugLog.Warn("Connection reset. Status=" + status);
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
