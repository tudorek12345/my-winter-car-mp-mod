using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Net;
using MyWinterCarMpMod.Util;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    public sealed class VehicleSync
    {
        private readonly Settings _settings;
        private readonly List<VehicleEntry> _vehicles = new List<VehicleEntry>();
        private readonly Dictionary<uint, VehicleEntry> _vehicleLookup = new Dictionary<uint, VehicleEntry>();
        private readonly PlayerLocator _playerLocator = new PlayerLocator();
        private readonly List<VehicleSeatData> _seatEventQueue = new List<VehicleSeatData>(8);
        private CarCameras _cachedCarCameras;
        private int _lastSceneIndex = int.MinValue;
        private string _lastSceneName = string.Empty;
        private float _nextSampleTime;
        private float _nextControlSampleTime;
        private float _nextOwnershipTraceTime;
        private float _nextRescanTime;
        private bool _loggedNoVehicles;
        private bool _loggedSorbetSeatDump;
        private uint _remoteSeatVehicleId;
        private bool _remoteSeatIsDriver;
        private bool _remoteSeatInSeat;
        private float _remoteSeatLastTime;
        private uint _lastRemoteSeatSequence;
        private const float SeatOwnershipHoldSeconds = 6f;
        private const string SeatEnterFilter = "player in car,player in,playerin,incar,in car,seatbelt,drive,driver,ignition,enable ignition,enter,create player,check speed,reset view";
        private const string SeatExitFilter = "wait,press return,press,exit,leave";
        private const string SeatEnterEventFilter = "player in car,player in,incar,in car,drive,seatbelt,ignition,enter,enable ignition";
        private const string SeatExitEventFilter = "wait for player,wait,press return,press,exit,leave";
        private const string PassengerSeatStateFilter = "player 2,player2,passenger,create player 2,create player2";
        private const string PassengerPressReturnFilter = "press return,press";
        private const float RemoteControlTimeoutSeconds = 0.75f;
        private const byte VehicleControlFlagStartEngine = 1;
        private const float RemoteLerpSpeed = 14f;
        private const float RemoteSnapDistance = 3.5f;
        private const float RemoteSmoothTimeMin = 0.03f;
        private const float RemoteSmoothTimeMax = 0.12f;
        private const float RemoteExtrapolationMax = 0.12f;
        private const float RemoteSteerSmoothTime = 0.1f;

        public VehicleSync(Settings settings)
        {
            _settings = settings;
        }

        public bool Enabled
        {
            get { return _settings != null && _settings.VehicleSyncEnabled.Value; }
        }

        public void UpdateScene(int levelIndex, string levelName, bool allowScan)
        {
            if (!Enabled)
            {
                Clear();
                return;
            }

            if (!allowScan)
            {
                if (_vehicles.Count > 0)
                {
                    Clear();
                }
                return;
            }

            if (levelIndex == _lastSceneIndex && string.Equals(levelName, _lastSceneName, StringComparison.Ordinal))
            {
                return;
            }

            _lastSceneIndex = levelIndex;
            _lastSceneName = levelName ?? string.Empty;
            ScanVehicles(false);
        }

        public void FixedUpdate(float now, float deltaTime, OwnerKind localOwner, bool includeUnowned)
        {
            if (!Enabled || _vehicles.Count == 0)
            {
                return;
            }

            EnsureVehiclesValid(now, "fixed update");

            for (int i = 0; i < _vehicles.Count; i++)
            {
                VehicleEntry entry = _vehicles[i];
                if (entry == null || !entry.HasRemoteState)
                {
                    continue;
                }

                bool rescanned;
                if (!EnsureVehicleBinding(entry, now, "fixed update", out rescanned))
                {
                    if (rescanned)
                    {
                        return;
                    }
                    continue;
                }

                if (IsLocalAuthority(entry, localOwner, includeUnowned))
                {
                    continue;
                }

                ApplyRemoteSmoothing(entry, now, deltaTime);
            }
        }

        public int CollectChanges(long unixTimeMs, float now, List<VehicleStateData> buffer, OwnerKind localOwner, bool includeUnowned)
        {
            buffer.Clear();
            if (!Enabled || _vehicles.Count == 0)
            {
                return 0;
            }

            EnsureVehiclesValid(now, "collect changes");
            UpdateRemoteControlTimeouts(now, localOwner);

            if (now < _nextSampleTime)
            {
                return 0;
            }

            float interval = 1f / _settings.GetVehicleSendHz();
            _nextSampleTime = now + interval;
            float posThreshold = _settings.GetVehiclePositionThreshold();
            float rotThreshold = _settings.GetVehicleRotationThreshold();

            for (int i = 0; i < _vehicles.Count; i++)
            {
                VehicleEntry entry = _vehicles[i];
                bool rescanned;
                if (!EnsureVehicleBinding(entry, now, "collect changes", out rescanned))
                {
                    if (rescanned)
                    {
                        return 0;
                    }
                    continue;
                }

                if (!IsLocalAuthority(entry, localOwner, includeUnowned))
                {
                    continue;
                }

                Transform syncTransform = GetSyncTransform(entry);
                if (syncTransform == null)
                {
                    continue;
                }

                Vector3 pos = syncTransform.position;
                Quaternion rot = syncTransform.rotation;
                if (Vector3.Distance(entry.LastSentPosition, pos) < posThreshold &&
                    Quaternion.Angle(entry.LastSentRotation, rot) < rotThreshold)
                {
                    continue;
                }

                entry.LastSentPosition = pos;
                entry.LastSentRotation = rot;

                Vector3 vel = entry.Body != null ? entry.Body.velocity : Vector3.zero;
                Vector3 angVel = entry.Body != null ? entry.Body.angularVelocity : Vector3.zero;
                EnsureVehicleComponents(entry);
                float steerValue = GetCurrentSteer(entry);
                int gear = entry.Drivetrain != null ? entry.Drivetrain.gear : 0;
                float rpm = entry.Drivetrain != null ? entry.Drivetrain.rpm : 0f;

                VehicleStateData state = new VehicleStateData
                {
                    UnixTimeMs = unixTimeMs,
                    VehicleId = entry.Id,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    RotX = rot.x,
                    RotY = rot.y,
                    RotZ = rot.z,
                    RotW = rot.w,
                    VelX = vel.x,
                    VelY = vel.y,
                    VelZ = vel.z,
                    AngVelX = angVel.x,
                    AngVelY = angVel.y,
                    AngVelZ = angVel.z,
                    Steer = steerValue,
                    Gear = gear,
                    EngineRpm = rpm
                };
                buffer.Add(state);
            }

            return buffer.Count;
        }

        public int CollectControlInputs(long unixTimeMs, float now, List<VehicleControlData> buffer, OwnerKind localOwner)
        {
            buffer.Clear();
            if (!Enabled || _vehicles.Count == 0 || localOwner != OwnerKind.Client)
            {
                return 0;
            }

            if (now < _nextControlSampleTime)
            {
                return 0;
            }

            float interval = 1f / _settings.GetVehicleSendHz();
            _nextControlSampleTime = now + interval;

            EnsureVehiclesValid(now, "collect control inputs");

            Vector3 localPos;
            bool hasLocal = TryGetLocalPosition(out localPos);

            for (int i = 0; i < _vehicles.Count; i++)
            {
                VehicleEntry entry = _vehicles[i];
                bool rescanned;
                if (!EnsureVehicleBinding(entry, now, "collect control inputs", out rescanned))
                {
                    if (rescanned)
                    {
                        return buffer.Count;
                    }
                    continue;
                }

                if (!IsSorbetVehicle(entry))
                {
                    continue;
                }

                EnsureVehicleComponents(entry);
                RefreshSeatFromFsm(entry, now);

                bool wants = IsLocalDriver(entry, localPos, hasLocal);
                entry.LocalWantsControl = wants;
                if (!wants)
                {
                    continue;
                }

                float throttle;
                float brake;
                float steer;
                float handbrake;
                float clutch;
                bool startEngine;
                int targetGear;
                if (!TryGetAxisInputs(entry, out throttle, out brake, out steer, out handbrake, out clutch, out startEngine, out targetGear))
                {
                    continue;
                }

                VehicleControlData control = new VehicleControlData
                {
                    UnixTimeMs = unixTimeMs,
                    VehicleId = entry.Id,
                    Throttle = throttle,
                    Brake = brake,
                    Steer = steer,
                    Handbrake = handbrake,
                    Clutch = clutch,
                    TargetGear = targetGear,
                    Flags = startEngine ? VehicleControlFlagStartEngine : (byte)0
                };
                buffer.Add(control);

                if (_settings != null && _settings.VerboseLogging.Value && now - entry.LastControlLogTime >= 0.5f)
                {
                    DebugLog.Verbose("VehicleSync: send control vehicle=" + entry.DebugPath +
                        " throttle=" + throttle.ToString("F2") +
                        " brake=" + brake.ToString("F2") +
                        " steer=" + steer.ToString("F2") +
                        " clutch=" + clutch.ToString("F2") +
                        " gear=" + targetGear +
                        " start=" + startEngine);
                    entry.LastControlLogTime = now;
                }
            }

            return buffer.Count;
        }

        public int CollectSeatEvents(List<VehicleSeatData> buffer)
        {
            buffer.Clear();
            if (_seatEventQueue.Count == 0)
            {
                return 0;
            }

            for (int i = 0; i < _seatEventQueue.Count; i++)
            {
                buffer.Add(_seatEventQueue[i]);
            }
            _seatEventQueue.Clear();
            return buffer.Count;
        }

        public void ApplyRemote(VehicleStateData state, OwnerKind localOwner, bool includeUnowned)
        {
            if (!Enabled)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            EnsureVehiclesValid(now, "apply remote");

            VehicleEntry entry;
            if (!_vehicleLookup.TryGetValue(state.VehicleId, out entry))
            {
                EnsureVehiclesValid(now, "apply remote missing id");
                if (_vehicleLookup.TryGetValue(state.VehicleId, out entry))
                {
                    // rebind succeeded
                }
                else
                {
                    if (!_loggedNoVehicles)
                    {
                        DebugLog.Warn("VehicleSync missing vehicle id " + state.VehicleId + ". Re-scan on next scene change.");
                        _loggedNoVehicles = true;
                    }
                    return;
                }
            }

            bool rescanned;
            if (!EnsureVehicleBinding(entry, now, "apply remote", out rescanned))
            {
                if (rescanned)
                {
                    return;
                }
                if (!_loggedNoVehicles)
                {
                    DebugLog.Warn("VehicleSync missing vehicle transform for id " + state.VehicleId + ". Re-scan on next scene change.");
                    _loggedNoVehicles = true;
                }
                return;
            }

            bool isLocal = IsLocalAuthority(entry, localOwner, includeUnowned);
            ApplyLocalControl(entry, localOwner);
            if (isLocal)
            {
                return;
            }

            if (!IsNewerSequence(state.Sequence, entry.LastRemoteSequence))
            {
                return;
            }
            entry.LastRemoteSequence = state.Sequence;

            Vector3 pos = new Vector3(state.PosX, state.PosY, state.PosZ);
            Quaternion rot = new Quaternion(state.RotX, state.RotY, state.RotZ, state.RotW);
            Vector3 vel = new Vector3(state.VelX, state.VelY, state.VelZ);
            Vector3 angVel = new Vector3(state.AngVelX, state.AngVelY, state.AngVelZ);
            if (!entry.HasRemoteState)
            {
                if (entry.Body != null)
                {
                    entry.Body.position = pos;
                    entry.Body.rotation = rot;
                    if (!entry.Body.isKinematic)
                    {
                        entry.Body.velocity = vel;
                        entry.Body.angularVelocity = angVel;
                    }
                }
                else if (entry.Transform != null)
                {
                    entry.Transform.position = pos;
                    entry.Transform.rotation = rot;
                }
            }

            entry.RemoteTargetPosition = pos;
            entry.RemoteTargetRotation = rot;
            entry.RemoteTargetVelocity = vel;
            entry.RemoteTargetAngVelocity = angVel;
            entry.RemoteTargetSteer = state.Steer;
            entry.RemoteGear = state.Gear;
            entry.RemoteRpm = state.EngineRpm;
            if (!entry.HasRemoteState)
            {
                entry.RemoteSteer = state.Steer;
            }
            entry.RemoteTargetTime = now;
            entry.HasRemoteState = true;
        }

        public void ApplyRemoteSeat(VehicleSeatData state)
        {
            if (!Enabled)
            {
                return;
            }

            if (state.Sequence != 0 && !IsNewerSequence(state.Sequence, _lastRemoteSeatSequence))
            {
                return;
            }

            if (state.Sequence != 0)
            {
                _lastRemoteSeatSequence = state.Sequence;
            }

            _remoteSeatVehicleId = state.VehicleId;
            _remoteSeatIsDriver = state.SeatRole == 0;
            _remoteSeatInSeat = state.InSeat != 0;
            _remoteSeatLastTime = Time.realtimeSinceStartup;

            if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("VehicleSync: remote seat vehicle=" + state.VehicleId +
                    " inSeat=" + _remoteSeatInSeat +
                    " role=" + (_remoteSeatIsDriver ? "driver" : "passenger"));
            }
        }

        public bool TryGetRemoteSeatTransform(out Transform seatTransform)
        {
            seatTransform = null;
            if (!_remoteSeatInSeat)
            {
                return false;
            }

            VehicleEntry entry;
            if (!_vehicleLookup.TryGetValue(_remoteSeatVehicleId, out entry) || entry == null)
            {
                return false;
            }

            seatTransform = _remoteSeatIsDriver
                ? entry.SeatTransform
                : (entry.PassengerSeatTransform ?? entry.SeatTransform);
            return seatTransform != null;
        }

        public void ApplyRemoteControl(VehicleControlData control, OwnerKind localOwner)
        {
            if (!Enabled || localOwner != OwnerKind.Host)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            EnsureVehiclesValid(now, "apply remote control");

            VehicleEntry entry;
            if (!_vehicleLookup.TryGetValue(control.VehicleId, out entry))
            {
                EnsureVehiclesValid(now, "apply remote control missing id");
                return;
            }

            if (!IsSorbetVehicle(entry))
            {
                return;
            }

            bool rescanned;
            if (!EnsureVehicleBinding(entry, now, "apply remote control", out rescanned))
            {
                return;
            }

            if (!IsNewerSequence(control.Sequence, entry.LastControlSequence))
            {
                return;
            }

            entry.LastControlSequence = control.Sequence;
            entry.LastControlReceiveTime = now;
            entry.RemoteControlActive = true;

            EnsureVehicleComponents(entry);
            MPCarController controller = EnsureRemoteController(entry);
            ApplyLocalControl(entry, localOwner);

            if (controller != null)
            {
                bool startEngine = (control.Flags & VehicleControlFlagStartEngine) != 0;
                controller.SetInput(control.Throttle, control.Brake, control.Steer, control.Handbrake, control.Clutch, startEngine, control.TargetGear, control.Sequence, now);
            }

            if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("VehicleSync: recv control vehicle=" + entry.DebugPath +
                    " seq=" + control.Sequence +
                    " throttle=" + control.Throttle.ToString("F2") +
                    " brake=" + control.Brake.ToString("F2") +
                    " steer=" + control.Steer.ToString("F2") +
                    " gear=" + control.TargetGear);
            }
        }

        public int CollectOwnershipRequests(OwnerKind localOwner, List<OwnershipRequestData> buffer)
        {
            buffer.Clear();
            if (_settings == null)
            {
                return 0;
            }

            float now = Time.realtimeSinceStartup;
            bool verbose = _settings.VerboseLogging.Value;
            bool logProbe = verbose && now >= _nextOwnershipTraceTime;
            if (logProbe)
            {
                DebugLog.Verbose("VehicleSync: ownership poll enabled=" + Enabled +
                    " vehicles=" + _vehicles.Count +
                    " localOwner=" + localOwner +
                    " ownershipEnabled=" + _settings.VehicleOwnershipEnabled.Value);
                _nextOwnershipTraceTime = now + 1f;
            }

            EnsureVehiclesValid(now, "ownership poll");

            if (!Enabled || _vehicles.Count == 0 || localOwner != OwnerKind.Client || !_settings.VehicleOwnershipEnabled.Value)
            {
                return 0;
            }

            Vector3 localPos;
            bool hasLocal = TryGetLocalPosition(out localPos);

            for (int i = 0; i < _vehicles.Count; i++)
            {
                VehicleEntry entry = _vehicles[i];
                bool rescanned;
                if (!EnsureVehicleBinding(entry, now, "ownership poll", out rescanned))
                {
                    if (rescanned)
                    {
                        return buffer.Count;
                    }
                    if (logProbe)
                    {
                        DebugLog.Verbose("VehicleSync: ownership skip (null transform) vehicle=" + entry.DebugPath);
                    }
                    continue;
                }

                RefreshSeatFromFsm(entry, now);
                bool wants = IsLocalDriver(entry, localPos, hasLocal);
                if (logProbe)
                {
                    DebugLog.Verbose("VehicleSync: ownership probe vehicle=" + entry.DebugPath +
                        " owner=" + entry.Owner +
                        " wants=" + wants +
                        " fsm=" + entry.LocalDriverFromFsm +
                        " force=" + (entry.ForceOwnershipUntilTime > now) +
                        " hasLocal=" + hasLocal);
                }
                if (wants != entry.LocalWantsControl && _settings != null && _settings.VerboseLogging.Value)
                {
                    DebugLog.Verbose("VehicleSync: local driver=" + wants + " vehicle=" + entry.DebugPath);
                }
                entry.LocalWantsControl = wants;

                if (wants && entry.Owner != OwnerKind.Client)
                {
                    if (now - entry.LastOwnershipRequestTime >= 0.5f)
                    {
                        buffer.Add(new OwnershipRequestData
                        {
                            Kind = SyncObjectKind.Vehicle,
                            ObjectId = entry.Id,
                            Action = OwnershipAction.Request
                        });
                        if (_settings != null && _settings.VerboseLogging.Value && now - entry.LastOwnershipLogTime >= 1f)
                        {
                            DebugLog.Verbose("VehicleSync: ownership request=Request vehicle=" + entry.DebugPath);
                            entry.LastOwnershipLogTime = now;
                        }
                        entry.LastOwnershipRequestTime = now;
                    }
                }
                else if (!wants && entry.Owner == OwnerKind.Client)
                {
                    if (now - entry.LastOwnershipRequestTime >= 0.5f)
                    {
                        buffer.Add(new OwnershipRequestData
                        {
                            Kind = SyncObjectKind.Vehicle,
                            ObjectId = entry.Id,
                            Action = OwnershipAction.Release
                        });
                        if (_settings != null && _settings.VerboseLogging.Value && now - entry.LastOwnershipLogTime >= 1f)
                        {
                            DebugLog.Verbose("VehicleSync: ownership request=Release vehicle=" + entry.DebugPath);
                            entry.LastOwnershipLogTime = now;
                        }
                        entry.LastOwnershipRequestTime = now;
                    }
                }
            }

            return buffer.Count;
        }

        public int CollectOwnershipUpdates(OwnerKind localOwner, List<OwnershipUpdateData> buffer)
        {
            buffer.Clear();
            if (!Enabled || _vehicles.Count == 0 || localOwner != OwnerKind.Host || !_settings.VehicleOwnershipEnabled.Value)
            {
                return 0;
            }

            Vector3 localPos;
            bool hasLocal = TryGetLocalPosition(out localPos);
            float now = Time.realtimeSinceStartup;
            EnsureVehiclesValid(now, "ownership update");

            for (int i = 0; i < _vehicles.Count; i++)
            {
                VehicleEntry entry = _vehicles[i];
                bool rescanned;
                if (!EnsureVehicleBinding(entry, now, "ownership update", out rescanned))
                {
                    if (rescanned)
                    {
                        return buffer.Count;
                    }
                    continue;
                }

                RefreshSeatFromFsm(entry, now);
                bool wants = IsLocalDriver(entry, localPos, hasLocal);
                entry.LocalWantsControl = wants;

                if (wants && entry.Owner != OwnerKind.Host)
                {
                    entry.Owner = OwnerKind.Host;
                    ApplyLocalControl(entry, localOwner);
                    buffer.Add(new OwnershipUpdateData
                    {
                        Kind = SyncObjectKind.Vehicle,
                        ObjectId = entry.Id,
                        Owner = OwnerKind.Host
                    });
                }
                else if (!wants && entry.Owner == OwnerKind.Host)
                {
                    entry.Owner = OwnerKind.None;
                    ApplyLocalControl(entry, localOwner);
                    buffer.Add(new OwnershipUpdateData
                    {
                        Kind = SyncObjectKind.Vehicle,
                        ObjectId = entry.Id,
                        Owner = OwnerKind.None
                    });
                }
            }

            return buffer.Count;
        }

        public bool TryHandleOwnershipRequest(OwnershipRequestData request, out OwnershipUpdateData update)
        {
            update = default(OwnershipUpdateData);

            if (!Enabled || request.Kind != SyncObjectKind.Vehicle || !_settings.VehicleOwnershipEnabled.Value)
            {
                return false;
            }

            VehicleEntry entry;
            if (!_vehicleLookup.TryGetValue(request.ObjectId, out entry))
            {
                return false;
            }

            if (request.Action == OwnershipAction.Request)
            {
                if (entry.Owner == OwnerKind.Host)
                {
                    return false;
                }
                entry.Owner = OwnerKind.Client;
                if (_settings != null && _settings.VerboseLogging.Value)
                {
                    DebugLog.Verbose("VehicleSync: ownership granted to Client for vehicle=" + entry.DebugPath);
                }
                update = new OwnershipUpdateData
                {
                    Kind = SyncObjectKind.Vehicle,
                    ObjectId = entry.Id,
                    Owner = OwnerKind.Client
                };
                return true;
            }

            if (request.Action == OwnershipAction.Release)
            {
                if (entry.Owner != OwnerKind.Client)
                {
                    return false;
                }
                entry.Owner = OwnerKind.None;
                if (_settings != null && _settings.VerboseLogging.Value)
                {
                    DebugLog.Verbose("VehicleSync: ownership released by Client for vehicle=" + entry.DebugPath);
                }
                update = new OwnershipUpdateData
                {
                    Kind = SyncObjectKind.Vehicle,
                    ObjectId = entry.Id,
                    Owner = OwnerKind.None
                };
                return true;
            }

            return false;
        }

        public void ApplyOwnership(OwnershipUpdateData update, OwnerKind localOwner)
        {
            if (!Enabled || update.Kind != SyncObjectKind.Vehicle)
            {
                return;
            }

            VehicleEntry entry;
            if (!_vehicleLookup.TryGetValue(update.ObjectId, out entry))
            {
                return;
            }

            entry.Owner = update.Owner;
            if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("VehicleSync: ownership applied vehicle=" + entry.DebugPath + " owner=" + update.Owner);
            }
            ApplyLocalControl(entry, localOwner);
        }

        public OwnershipUpdateData[] BuildOwnershipSnapshot()
        {
            if (!Enabled || _vehicles.Count == 0)
            {
                return new OwnershipUpdateData[0];
            }

            List<OwnershipUpdateData> list = new List<OwnershipUpdateData>(_vehicles.Count);
            for (int i = 0; i < _vehicles.Count; i++)
            {
                VehicleEntry entry = _vehicles[i];
                if (entry.Owner == OwnerKind.None)
                {
                    continue;
                }
                list.Add(new OwnershipUpdateData
                {
                    Kind = SyncObjectKind.Vehicle,
                    ObjectId = entry.Id,
                    Owner = entry.Owner
                });
            }

            return list.ToArray();
        }

        public VehicleStateData[] BuildSnapshot(long unixTimeMs, uint sessionId)
        {
            if (!Enabled || _vehicles.Count == 0)
            {
                return new VehicleStateData[0];
            }

            VehicleStateData[] states = new VehicleStateData[_vehicles.Count];
            for (int i = 0; i < _vehicles.Count; i++)
            {
                VehicleEntry entry = _vehicles[i];
                Transform syncTransform = GetSyncTransform(entry);
                Vector3 pos = syncTransform != null ? syncTransform.position : Vector3.zero;
                Quaternion rot = syncTransform != null ? syncTransform.rotation : Quaternion.identity;
                Vector3 vel = entry.Body != null ? entry.Body.velocity : Vector3.zero;
                Vector3 angVel = entry.Body != null ? entry.Body.angularVelocity : Vector3.zero;
                EnsureVehicleComponents(entry);
                float steerValue = GetCurrentSteer(entry);
                int gear = entry.Drivetrain != null ? entry.Drivetrain.gear : 0;
                float rpm = entry.Drivetrain != null ? entry.Drivetrain.rpm : 0f;
                states[i] = new VehicleStateData
                {
                    SessionId = sessionId,
                    Sequence = 1,
                    UnixTimeMs = unixTimeMs,
                    VehicleId = entry.Id,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    RotX = rot.x,
                    RotY = rot.y,
                    RotZ = rot.z,
                    RotW = rot.w,
                    VelX = vel.x,
                    VelY = vel.y,
                    VelZ = vel.z,
                    AngVelX = angVel.x,
                    AngVelY = angVel.y,
                    AngVelZ = angVel.z,
                    Steer = steerValue,
                    Gear = gear,
                    EngineRpm = rpm
                };
            }

            return states;
        }

        public void ResetOwnership(OwnerKind localOwner)
        {
            for (int i = 0; i < _vehicles.Count; i++)
            {
                VehicleEntry entry = _vehicles[i];
                entry.Owner = OwnerKind.None;
                entry.RemoteControlActive = false;
                entry.LastControlSequence = 0;
                entry.LastControlReceiveTime = 0f;
                entry.ControlMode = VehicleControlMode.Unknown;
                ApplyLocalControl(entry, localOwner);
            }
        }

        public void Clear()
        {
            _vehicles.Clear();
            _vehicleLookup.Clear();
            _seatEventQueue.Clear();
            _loggedNoVehicles = false;
            _cachedCarCameras = null;
            _remoteSeatVehicleId = 0;
            _remoteSeatIsDriver = false;
            _remoteSeatInSeat = false;
            _remoteSeatLastTime = 0f;
            _lastRemoteSeatSequence = 0;
        }

        private void EnsureVehiclesValid(float now, string reason)
        {
            if (!Enabled)
            {
                return;
            }

            if (now < _nextRescanTime)
            {
                return;
            }

            if (_vehicles.Count == 0)
            {
                if (_settings != null && _settings.VerboseLogging.Value)
                {
                    DebugLog.Warn("VehicleSync: rescan vehicles (none) reason=" + reason);
                }
                _nextRescanTime = now + 2f;
                ScanVehicles(true);
                return;
            }

            bool needsRescan = false;
            for (int i = 0; i < _vehicles.Count; i++)
            {
                VehicleEntry entry = _vehicles[i];
                if (entry == null || entry.Transform == null)
                {
                    needsRescan = true;
                    break;
                }
            }

            if (!needsRescan)
            {
                return;
            }

            if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Warn("VehicleSync: rescan vehicles (missing transform) reason=" + reason);
            }
            _nextRescanTime = now + 2f;
            ScanVehicles(true);
        }

        private bool EnsureVehicleBinding(VehicleEntry entry, float now, string reason, out bool rescanned)
        {
            rescanned = false;
            if (entry == null)
            {
                return false;
            }

            if (entry.Transform == null && entry.Body != null)
            {
                entry.Transform = entry.Body.transform;
            }

            if (entry.Transform != null || entry.Body != null)
            {
                EnsureVehicleComponents(entry);
                return true;
            }

            if (TryRebindVehicle(entry, reason))
            {
                return true;
            }

            if (now < _nextRescanTime)
            {
                return false;
            }

            _nextRescanTime = now + 2f;
            if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Warn("VehicleSync: rescan vehicles (missing transform) reason=" + reason);
            }
            ScanVehicles(true);
            rescanned = true;
            return false;
        }

        private bool TryRebindVehicle(VehicleEntry entry, string reason)
        {
            if (entry == null || string.IsNullOrEmpty(entry.DebugPath))
            {
                return false;
            }

            string rootName = ExtractRootName(entry.DebugPath);
            if (string.IsNullOrEmpty(rootName))
            {
                return false;
            }

            GameObject root = FindVehicleRootByName(rootName);
            if (root == null)
            {
                return false;
            }

            RebindEntry(entry, root, reason);
            return true;
        }

        private void RebindEntry(VehicleEntry entry, GameObject root, string reason)
        {
            if (entry == null || root == null)
            {
                return;
            }

            Rigidbody body = root.GetComponent<Rigidbody>();
            if (body == null)
            {
                body = root.GetComponentInChildren<Rigidbody>();
            }

            CarDynamics dynamics = root.GetComponent<CarDynamics>();
            if (dynamics == null)
            {
                dynamics = root.GetComponentInChildren<CarDynamics>();
            }

            entry.Transform = root.transform;
            entry.Body = body;
            entry.CarDynamics = dynamics;
            if (dynamics != null)
            {
                entry.OriginalController = dynamics.controller;
            }
            EnsureVehicleComponents(entry);

            Transform driverSeat;
            Transform passengerSeat;
            FindSeatTransforms(root, out driverSeat, out passengerSeat);
            entry.SeatTransform = driverSeat;
            entry.PassengerSeatTransform = passengerSeat;
            entry.SeatFsms.Clear();
            entry.DriverSeatFsms.Clear();
            entry.PassengerSeatFsms.Clear();
            if (entry.SeatEventHooked != null)
            {
                entry.SeatEventHooked.Clear();
            }
            entry.SeatFsm = null;
            entry.SeatFsmName = null;
            entry.LocalDriverFromFsm = false;
            entry.LocalPassengerFromFsm = false;
            entry.LastSentDriverInSeat = false;
            entry.LastSentPassengerInSeat = false;

            Transform syncTransform = GetSyncTransform(entry);
            if (syncTransform != null)
            {
                entry.LastSentPosition = syncTransform.position;
                entry.LastSentRotation = syncTransform.rotation;
            }

            AttachSeatTriggers(entry, root);
            DebugLog.Warn("VehicleSync: rebound vehicle id=" + entry.Id + " root=" + root.name + " reason=" + reason);
        }

        private static GameObject FindVehicleRootByName(string rootName)
        {
            if (string.IsNullOrEmpty(rootName))
            {
                return null;
            }

            CarDynamics[] dynamics = UnityEngine.Object.FindObjectsOfType<CarDynamics>();
            for (int i = 0; i < dynamics.Length; i++)
            {
                CarDynamics dyn = dynamics[i];
                if (dyn == null || dyn.gameObject == null)
                {
                    continue;
                }
                if (string.Equals(dyn.gameObject.name, rootName, StringComparison.OrdinalIgnoreCase))
                {
                    return dyn.gameObject;
                }
            }

            GameObject byName = GameObject.Find(rootName);
            if (byName != null)
            {
                return byName;
            }

            string[] tags = new[] { "Car", "Truck", "Boat" };
            for (int t = 0; t < tags.Length; t++)
            {
                GameObject[] objs = null;
                try
                {
                    objs = GameObject.FindGameObjectsWithTag(tags[t]);
                }
                catch (UnityException)
                {
                    objs = null;
                }

                if (objs == null)
                {
                    continue;
                }

                for (int i = 0; i < objs.Length; i++)
                {
                    GameObject obj = objs[i];
                    if (obj != null && string.Equals(obj.name, rootName, StringComparison.OrdinalIgnoreCase))
                    {
                        return obj;
                    }
                }
            }

            return null;
        }

        private static string ExtractRootName(string debugPath)
        {
            if (string.IsNullOrEmpty(debugPath))
            {
                return string.Empty;
            }

            string segment = debugPath;
            int slashIndex = debugPath.IndexOf('/');
            if (slashIndex >= 0)
            {
                segment = debugPath.Substring(0, slashIndex);
            }

            int hashIndex = segment.LastIndexOf('#');
            if (hashIndex >= 0)
            {
                segment = segment.Substring(0, hashIndex);
            }

            return segment;
        }

        private void ScanVehicles(bool preserveOwnership)
        {
            Dictionary<uint, OwnerKind> owners = null;
            if (preserveOwnership && _vehicles.Count > 0)
            {
                owners = new Dictionary<uint, OwnerKind>(_vehicles.Count);
                for (int i = 0; i < _vehicles.Count; i++)
                {
                    VehicleEntry entry = _vehicles[i];
                    owners[entry.Id] = entry.Owner;
                }
            }

            Clear();

            List<VehicleCandidate> candidates = new List<VehicleCandidate>();
            CollectByTag("Car", candidates);
            CollectByTag("Truck", candidates);
            CollectByTag("Boat", candidates);
            CollectByCarDynamics(candidates);

            if (candidates.Count == 0)
            {
                DebugLog.Verbose("VehicleSync: no vehicles found.");
                return;
            }

            string filter = _settings != null ? _settings.GetVehicleNameFilter() : string.Empty;
            if (!string.IsNullOrEmpty(filter))
            {
                for (int i = candidates.Count - 1; i >= 0; i--)
                {
                    VehicleCandidate candidate = candidates[i];
                    string name = candidate.Root != null ? candidate.Root.name : string.Empty;
                    if (!NameContains(name, filter) && !NameContains(candidate.DebugPath, filter))
                    {
                        candidates.RemoveAt(i);
                    }
                }
                if (candidates.Count == 0)
                {
                    DebugLog.Warn("VehicleSync: no vehicles matched filter '" + filter + "'.");
                    return;
                }
            }

            candidates.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            Dictionary<string, int> keyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < candidates.Count; i++)
            {
                VehicleCandidate candidate = candidates[i];
                int count;
                if (!keyCounts.TryGetValue(candidate.Key, out count))
                {
                    count = 0;
                }
                count++;
                keyCounts[candidate.Key] = count;
                string uniqueKey = count == 1 ? candidate.Key : candidate.Key + "|dup" + count;
                uint id = ObjectKeyBuilder.HashKey(uniqueKey);
                AddVehicle(candidate.Root, uniqueKey, candidate.DebugPath);
                if (owners != null)
                {
                    OwnerKind owner;
                    if (owners.TryGetValue(id, out owner))
                    {
                        VehicleEntry entry;
                        if (_vehicleLookup.TryGetValue(id, out entry))
                        {
                            entry.Owner = owner;
                        }
                    }
                }
            }

            DebugLog.Info("VehicleSync: tracking " + _vehicles.Count + " vehicle(s) in " + _lastSceneName + ".");
        }

        private void CollectByTag(string tag, List<VehicleCandidate> candidates)
        {
            GameObject[] objects = null;
            try
            {
                objects = GameObject.FindGameObjectsWithTag(tag);
            }
            catch (UnityException)
            {
                return;
            }

            if (objects == null)
            {
                return;
            }

            for (int i = 0; i < objects.Length; i++)
            {
                GameObject obj = objects[i];
                if (obj == null)
                {
                    continue;
                }
                AddCandidate(obj, candidates);
            }
        }

        private void CollectByCarDynamics(List<VehicleCandidate> candidates)
        {
            Type carDynamicsType = FindType("CarDynamics");
            if (carDynamicsType == null)
            {
                return;
            }

            UnityEngine.Object[] comps = UnityEngine.Object.FindObjectsOfType(carDynamicsType);
            for (int i = 0; i < comps.Length; i++)
            {
                Component comp = comps[i] as Component;
                if (comp == null)
                {
                    continue;
                }
                AddCandidate(comp.gameObject, candidates);
            }
        }

        private void AddCandidate(GameObject obj, List<VehicleCandidate> candidates)
        {
            if (obj == null)
            {
                return;
            }

            string key = ObjectKeyBuilder.BuildTypedKey(obj, "vehicle");
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            string debugPath = ObjectKeyBuilder.BuildDebugPath(obj);
            candidates.Add(new VehicleCandidate { Root = obj, Key = key, DebugPath = debugPath });
        }

        private void AddVehicle(GameObject root, string key, string debugPath)
        {
            if (root == null || string.IsNullOrEmpty(key))
            {
                return;
            }

            uint id = ObjectKeyBuilder.HashKey(key);
            if (_vehicleLookup.ContainsKey(id))
            {
                return;
            }

            Rigidbody body = root.GetComponent<Rigidbody>();
            if (body == null)
            {
                body = root.GetComponentInChildren<Rigidbody>();
            }

            CarDynamics dynamics = root.GetComponent<CarDynamics>();
            if (dynamics == null)
            {
                dynamics = root.GetComponentInChildren<CarDynamics>();
            }

            Transform seatTransform;
            Transform passengerSeatTransform;
            FindSeatTransforms(root, out seatTransform, out passengerSeatTransform);

            Transform syncTransform = body != null ? body.transform : root.transform;
            VehicleEntry entry = new VehicleEntry
            {
                Id = id,
                Key = key,
                DebugPath = debugPath,
                Transform = root.transform,
                Body = body,
                CarDynamics = dynamics,
                SeatTransform = seatTransform,
                PassengerSeatTransform = passengerSeatTransform,
                Owner = OwnerKind.None,
                OriginalController = dynamics != null ? dynamics.controller : CarDynamics.Controller.axis,
                LastSentPosition = syncTransform != null ? syncTransform.position : Vector3.zero,
                LastSentRotation = syncTransform != null ? syncTransform.rotation : Quaternion.identity
            };

            EnsureVehicleComponents(entry);
            _vehicles.Add(entry);
            _vehicleLookup.Add(id, entry);
            if (syncTransform != null)
            {
                DebugLog.Info("VehicleSync: add vehicle id=" + entry.Id + " root=" + root.name + " sync=" + syncTransform.name + " path=" + debugPath);
            }

            AttachSeatTriggers(entry, root);
        }

        public bool IsLocalAuthorityForTransform(Transform transform, OwnerKind localOwner, bool includeUnowned)
        {
            if (transform == null)
            {
                return false;
            }

            VehicleEntry entry = FindEntryByTransform(transform);
            if (entry == null)
            {
                return localOwner == OwnerKind.Host && includeUnowned;
            }

            return IsLocalAuthority(entry, localOwner, includeUnowned);
        }

        public bool TryGetLocalSorbetVehicleId(float now, out uint vehicleId)
        {
            vehicleId = 0;
            if (!Enabled || _vehicles.Count == 0)
            {
                return false;
            }

            EnsureVehiclesValid(now, "sorbet driver check");

            Vector3 localPos;
            bool hasLocal = TryGetLocalPosition(out localPos);
            for (int i = 0; i < _vehicles.Count; i++)
            {
                VehicleEntry entry = _vehicles[i];
                if (entry == null || !IsSorbetVehicle(entry))
                {
                    continue;
                }

                EnsureVehicleComponents(entry);
                RefreshSeatFromFsm(entry, now);

                if (IsLocalDriver(entry, localPos, hasLocal))
                {
                    vehicleId = entry.Id;
                    return true;
                }
            }

            return false;
        }

        private VehicleEntry FindEntryByTransform(Transform transform)
        {
            if (transform == null)
            {
                return null;
            }

            for (int i = 0; i < _vehicles.Count; i++)
            {
                VehicleEntry entry = _vehicles[i];
                if (entry == null)
                {
                    continue;
                }

                Transform root = entry.Transform ?? (entry.Body != null ? entry.Body.transform : null);
                if (root == null)
                {
                    continue;
                }

                if (transform == root || transform.IsChildOf(root))
                {
                    return entry;
                }
            }

            return null;
        }

        private bool IsLocalAuthority(VehicleEntry entry, OwnerKind localOwner, bool includeUnowned)
        {
            if (entry == null)
            {
                return false;
            }

            if (IsSorbetVehicle(entry))
            {
                if (entry.Owner == localOwner)
                {
                    return true;
                }
                if (includeUnowned && localOwner == OwnerKind.Host && entry.Owner == OwnerKind.None)
                {
                    return true;
                }
                return false;
            }

            if (entry.Owner == localOwner)
            {
                return true;
            }
            if (includeUnowned && localOwner == OwnerKind.Host && entry.Owner == OwnerKind.None)
            {
                return true;
            }
            return false;
        }

        private void ApplyLocalControl(VehicleEntry entry, OwnerKind localOwner)
        {
            if (entry == null)
            {
                return;
            }

            EnsureVehicleComponents(entry);

            bool isSorbet = IsSorbetVehicle(entry);
            bool allowUnowned = localOwner == OwnerKind.Host;
            bool isLocal = entry.Owner == localOwner || (allowUnowned && entry.Owner == OwnerKind.None);

            if (isSorbet && localOwner == OwnerKind.Host && entry.RemoteControlActive)
            {
                SetControlMode(entry, VehicleControlMode.Remote);
            }
            else
            {
                SetControlMode(entry, isLocal ? VehicleControlMode.Local : VehicleControlMode.Passive);
            }

            bool shouldKinematic = localOwner == OwnerKind.Client && !isLocal;
            if (entry.Body != null)
            {
                if (entry.Body.isKinematic != shouldKinematic)
                {
                    entry.Body.isKinematic = shouldKinematic;
                }
                if (shouldKinematic)
                {
                    entry.Body.interpolation = RigidbodyInterpolation.Interpolate;
                }
                else if (entry.Body.interpolation != RigidbodyInterpolation.None)
                {
                    entry.Body.interpolation = RigidbodyInterpolation.None;
                }
            }

            EnsureWheelComponents(entry);
            if (entry.Wheels != null && entry.Wheels.Length > 0)
            {
                bool disableWheels = shouldKinematic;
                if (entry.WheelsDisabled != disableWheels)
                {
                    for (int i = 0; i < entry.Wheels.Length; i++)
                    {
                        Wheel wheel = entry.Wheels[i];
                        if (wheel != null)
                        {
                            wheel.enabled = !disableWheels;
                        }
                    }
                    entry.WheelsDisabled = disableWheels;
                }
            }
        }

        private void EnsureVehicleComponents(VehicleEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            Transform root = entry.Transform ?? (entry.Body != null ? entry.Body.transform : null);
            if (root == null)
            {
                return;
            }

            if (entry.AxisController == null)
            {
                entry.AxisController = root.GetComponent<AxisCarController>() ?? GetComponentInChildrenIncludingInactive<AxisCarController>(root);
            }
            if (entry.Drivetrain == null)
            {
                entry.Drivetrain = root.GetComponent<Drivetrain>() ?? GetComponentInChildrenIncludingInactive<Drivetrain>(root);
            }
            if (entry.BrakeLights == null)
            {
                entry.BrakeLights = root.GetComponent<BrakeLights>() ?? GetComponentInChildrenIncludingInactive<BrakeLights>(root);
            }
            if (entry.DashBoard == null)
            {
                entry.DashBoard = GetComponentInChildrenIncludingInactive<DashBoard>(root);
            }
            if (entry.SteeringWheel == null)
            {
                entry.SteeringWheel = GetComponentInChildrenIncludingInactive<SteeringWheel>(root);
            }
            if (entry.SoundController == null)
            {
                entry.SoundController = root.GetComponent<SoundController>() ?? GetComponentInChildrenIncludingInactive<SoundController>(root);
            }
            if (entry.RemoteController == null)
            {
                entry.RemoteController = root.GetComponent<MPCarController>() ?? GetComponentInChildrenIncludingInactive<MPCarController>(root);
            }
        }

        private void EnsureWheelComponents(VehicleEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            if (entry.Axles == null)
            {
                Transform root = entry.Transform ?? (entry.Body != null ? entry.Body.transform : null);
                if (root == null)
                {
                    return;
                }
                entry.Axles = root.GetComponent<Axles>() ?? GetComponentInChildrenIncludingInactive<Axles>(root);
            }

            if (entry.Wheels == null || entry.Wheels.Length == 0)
            {
                if (entry.Axles != null)
                {
                    entry.Wheels = entry.Axles.allWheels;
                }
                if ((entry.Wheels == null || entry.Wheels.Length == 0) && (entry.Transform != null || entry.Body != null))
                {
                    Transform root = entry.Transform ?? entry.Body.transform;
                    entry.Wheels = root.GetComponentsInChildren<Wheel>(true);
                }
            }
        }

        private float GetRemoteSmoothTime()
        {
            float hz = _settings != null ? _settings.GetVehicleSendHz() : 10f;
            float smoothTime = 0.75f / Mathf.Max(8f, hz);
            return Mathf.Clamp(smoothTime, RemoteSmoothTimeMin, RemoteSmoothTimeMax);
        }

        private void ApplyRemoteSmoothing(VehicleEntry entry, float now, float deltaTime)
        {
            if (entry == null || !entry.HasRemoteState)
            {
                return;
            }

            float lerp = Mathf.Clamp01(deltaTime * RemoteLerpSpeed);
            float age = Mathf.Clamp(now - entry.RemoteTargetTime, 0f, RemoteExtrapolationMax);
            Vector3 targetPos = entry.RemoteTargetPosition + entry.RemoteTargetVelocity * age;
            Quaternion targetRot = entry.RemoteTargetRotation;
            if (entry.RemoteTargetAngVelocity.sqrMagnitude > 0.0001f)
            {
                float angSpeed = entry.RemoteTargetAngVelocity.magnitude * Mathf.Rad2Deg;
                float angle = angSpeed * age;
                if (angle > 0.001f)
                {
                    targetRot = Quaternion.AngleAxis(angle, entry.RemoteTargetAngVelocity.normalized) * targetRot;
                }
            }

            entry.RemoteSteer = Mathf.SmoothDamp(entry.RemoteSteer, entry.RemoteTargetSteer, ref entry.RemoteSteerVelocity, RemoteSteerSmoothTime, Mathf.Infinity, deltaTime);

            if (entry.Body != null)
            {
                if ((entry.Body.position - targetPos).sqrMagnitude > RemoteSnapDistance * RemoteSnapDistance)
                {
                    entry.Body.position = targetPos;
                    entry.Body.rotation = targetRot;
                }
                else
                {
                    Quaternion newRot = Quaternion.Slerp(entry.Body.rotation, targetRot, lerp);
                    float smoothTime = GetRemoteSmoothTime();
                    Vector3 newPos = Vector3.SmoothDamp(entry.Body.position, targetPos, ref entry.RemoteSmoothVelocity, smoothTime, Mathf.Infinity, deltaTime);
                    entry.Body.MovePosition(newPos);
                    entry.Body.MoveRotation(newRot);
                }

                if (!entry.Body.isKinematic)
                {
                    entry.Body.velocity = Vector3.Lerp(entry.Body.velocity, entry.RemoteTargetVelocity, lerp);
                    entry.Body.angularVelocity = Vector3.Lerp(entry.Body.angularVelocity, entry.RemoteTargetAngVelocity, lerp);
                }
            }
            else if (entry.Transform != null)
            {
                entry.Transform.position = Vector3.Lerp(entry.Transform.position, targetPos, lerp);
                entry.Transform.rotation = Quaternion.Slerp(entry.Transform.rotation, targetRot, lerp);
            }

            if (entry.Drivetrain != null)
            {
                entry.Drivetrain.gear = entry.RemoteGear;
                entry.Drivetrain.rpm = entry.RemoteRpm;
            }

            UpdateWheelVisuals(entry, deltaTime);
        }

        private void UpdateWheelVisuals(VehicleEntry entry, float deltaTime)
        {
            if (entry == null || entry.Transform == null)
            {
                return;
            }

            EnsureWheelComponents(entry);
            if (entry.Wheels == null || entry.Wheels.Length == 0)
            {
                if (_settings != null && _settings.VerboseLogging.Value && !entry.LoggedNoWheels)
                {
                    DebugLog.Verbose("VehicleSync: no wheels found for " + entry.DebugPath);
                    entry.LoggedNoWheels = true;
                }
                return;
            }

            Vector3 baseVelocity = entry.RemoteTargetVelocity;
            if (entry.Body != null && !entry.Body.isKinematic && entry.Body.velocity.sqrMagnitude > 0.0001f)
            {
                baseVelocity = entry.Body.velocity;
            }
            float forwardSpeed = Vector3.Dot(baseVelocity, entry.Transform.forward);
            float steer = entry.RemoteSteer;
            bool overrideVisuals = entry.WheelsDisabled;
            if (overrideVisuals && (entry.WheelSpin == null || entry.WheelSpin.Length != entry.Wheels.Length))
            {
                entry.WheelSpin = new float[entry.Wheels.Length];
            }

            for (int i = 0; i < entry.Wheels.Length; i++)
            {
                Wheel wheel = entry.Wheels[i];
                if (wheel == null)
                {
                    continue;
                }

                float radius = Mathf.Max(0.01f, wheel.radius);
                float spinDelta = forwardSpeed / radius * deltaTime;

                bool isFront = wheel.wheelPos == WheelPos.FRONT_LEFT || wheel.wheelPos == WheelPos.FRONT_RIGHT;
                if (overrideVisuals)
                {
                    entry.WheelSpin[i] = Mathf.Repeat(entry.WheelSpin[i] + spinDelta, Mathf.PI * 2f);
                    float steerAngle = isFront ? wheel.maxSteeringAngle * steer : 0f;

                    Transform modelTransform = wheel.model != null ? wheel.model.transform : wheel.transform;
                    if (modelTransform != null)
                    {
                        modelTransform.localRotation = Quaternion.Euler(0f, steerAngle, 0f) *
                            Quaternion.AngleAxis(entry.WheelSpin[i] * Mathf.Rad2Deg, Vector3.right);
                    }

                    if (wheel.caliperModel != null)
                    {
                        Transform caliperTransform = wheel.caliperModel.transform;
                        caliperTransform.localRotation = Quaternion.Euler(0f, steerAngle, 0f);
                    }
                }
                else
                {
                    wheel.angularVelocity = forwardSpeed / radius;
                    if (isFront)
                    {
                        wheel.steering = steer;
                    }
                }
            }
        }

        private static T GetComponentInChildrenIncludingInactive<T>(Transform root) where T : Component
        {
            if (root == null)
            {
                return null;
            }

            T[] found = root.GetComponentsInChildren<T>(true);
            if (found == null || found.Length == 0)
            {
                return null;
            }

            return found[0];
        }

        private void SetControlMode(VehicleEntry entry, VehicleControlMode mode)
        {
            if (entry == null)
            {
                return;
            }

            if (entry.ControlMode == mode)
            {
                return;
            }

            EnsureVehicleComponents(entry);

            switch (mode)
            {
                case VehicleControlMode.Local:
                    if (entry.CarDynamics != null)
                    {
                        entry.CarDynamics.SetController(entry.OriginalController.ToString());
                    }
                    if (entry.AxisController != null)
                    {
                        entry.AxisController.enabled = true;
                    }
                    if (entry.RemoteController != null)
                    {
                        entry.RemoteController.enabled = false;
                    }
                    SetCarControllerRefs(entry, entry.AxisController);
                    break;
                case VehicleControlMode.Remote:
                    if (entry.CarDynamics != null)
                    {
                        entry.CarDynamics.SetController(CarDynamics.Controller.external.ToString());
                    }
                    if (entry.AxisController != null)
                    {
                        entry.AxisController.enabled = false;
                    }
                    MPCarController remoteController = EnsureRemoteController(entry);
                    if (remoteController != null)
                    {
                        remoteController.enabled = true;
                    }
                    SetCarControllerRefs(entry, remoteController);
                    break;
                case VehicleControlMode.Passive:
                default:
                    if (entry.CarDynamics != null)
                    {
                        entry.CarDynamics.SetController(CarDynamics.Controller.external.ToString());
                    }
                    if (entry.AxisController != null)
                    {
                        entry.AxisController.enabled = false;
                    }
                    if (entry.RemoteController != null)
                    {
                        entry.RemoteController.enabled = false;
                    }
                    SetCarControllerRefs(entry, entry.AxisController);
                    break;
            }

            entry.ControlMode = mode;
        }

        private static void SetCarControllerRefs(VehicleEntry entry, CarController controller)
        {
            if (entry == null)
            {
                return;
            }

            if (entry.CarDynamics != null)
            {
                entry.CarDynamics.carController = controller;
            }
            if (entry.Drivetrain != null)
            {
                entry.Drivetrain.carController = controller;
            }
            if (entry.BrakeLights != null)
            {
                entry.BrakeLights.carController = controller;
            }
            if (entry.DashBoard != null)
            {
                entry.DashBoard.carController = controller;
            }
            if (entry.SteeringWheel != null)
            {
                entry.SteeringWheel.carController = controller;
            }
            if (entry.SoundController != null)
            {
                entry.SoundController.carController = controller;
            }
        }

        private MPCarController EnsureRemoteController(VehicleEntry entry)
        {
            if (entry == null)
            {
                return null;
            }

            if (entry.RemoteController != null)
            {
                return entry.RemoteController;
            }

            Transform root = entry.Transform ?? (entry.Body != null ? entry.Body.transform : null);
            if (root == null)
            {
                return null;
            }

            MPCarController controller = root.GetComponent<MPCarController>();
            if (controller == null)
            {
                controller = root.gameObject.AddComponent<MPCarController>();
            }

            entry.RemoteController = controller;
            return controller;
        }

        private void UpdateRemoteControlTimeouts(float now, OwnerKind localOwner)
        {
            if (localOwner != OwnerKind.Host || _vehicles.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _vehicles.Count; i++)
            {
                VehicleEntry entry = _vehicles[i];
                if (entry == null || !entry.RemoteControlActive || !IsSorbetVehicle(entry))
                {
                    continue;
                }

                if (now - entry.LastControlReceiveTime <= RemoteControlTimeoutSeconds)
                {
                    continue;
                }

                entry.RemoteControlActive = false;
                if (_settings != null && _settings.VerboseLogging.Value)
                {
                    DebugLog.Verbose("VehicleSync: remote control timeout vehicle=" + entry.DebugPath);
                }

                if (entry.RemoteController != null)
                {
                    int gear = entry.Drivetrain != null ? entry.Drivetrain.gear : 0;
                    entry.RemoteController.SetInput(0f, 0f, 0f, 0f, 0f, false, gear, entry.LastControlSequence, now);
                }

                SetControlMode(entry, VehicleControlMode.Passive);
            }
        }

        private static bool IsSorbetVehicle(VehicleEntry entry)
        {
            if (entry == null)
            {
                return false;
            }

            string name = entry.DebugPath;
            if (string.IsNullOrEmpty(name))
            {
                name = entry.Key;
            }
            return !string.IsNullOrEmpty(name) && name.IndexOf("SORBET", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool TryGetAxisInputs(VehicleEntry entry, out float throttleInput, out float brakeInput, out float steerInput, out float handbrakeInput, out float clutchInput, out bool startEngineInput, out int targetGear)
        {
            throttleInput = 0f;
            brakeInput = 0f;
            steerInput = 0f;
            handbrakeInput = 0f;
            clutchInput = 0f;
            startEngineInput = false;
            targetGear = 0;

            if (entry == null)
            {
                return false;
            }

            EnsureVehicleComponents(entry);

            AxisCarController axis = entry.AxisController;
            Drivetrain drivetrain = entry.Drivetrain;
            if (axis == null || drivetrain == null)
            {
                if (_settings != null && _settings.VerboseLogging.Value)
                {
                    float now = Time.realtimeSinceStartup;
                    if (now - entry.LastAxisLogTime >= 1f)
                    {
                        DebugLog.Verbose("VehicleSync: input components missing vehicle=" + entry.DebugPath +
                            " axis=" + (axis != null) +
                            " drivetrain=" + (drivetrain != null));
                        entry.LastAxisLogTime = now;
                    }
                }
                return false;
            }

            throttleInput = cInput.GetAxisRaw(axis.throttleAxis);
            brakeInput = cInput.GetAxisRaw(axis.brakeAxis);
            steerInput = cInput.GetAxisRaw(axis.steerAxis);
            handbrakeInput = cInput.GetAxisRaw(axis.handbrakeAxis);
            clutchInput = cInput.GetAxisRaw(axis.clutchAxis);
            if (axis.normalizeThrottleInput)
            {
                throttleInput = (throttleInput + 1f) / 2f;
            }
            if (axis.exponentialThrottleInput)
            {
                throttleInput *= throttleInput;
            }
            if (axis.normalizeBrakesInput)
            {
                brakeInput = (brakeInput + 1f) / 2f;
            }
            if (axis.exponentialBrakesInput)
            {
                brakeInput *= brakeInput;
            }
            if (axis.normalizeClutchInput)
            {
                clutchInput = (clutchInput + 1f) / 2f;
            }
            if (axis.exponentialClutchInput)
            {
                clutchInput *= clutchInput;
            }
            startEngineInput = cInput.GetKeyDown(axis.startEngineButton);
            targetGear = drivetrain.gear;
            if (cInput.GetKeyDown(axis.shiftUpButton))
            {
                targetGear++;
            }
            if (cInput.GetKeyDown(axis.shiftDownButton))
            {
                targetGear--;
            }
            if (drivetrain.shifter)
            {
                if (cInput.GetButton("reverse"))
                {
                    targetGear = 0;
                }
                else if (cInput.GetButton("neutral"))
                {
                    targetGear = 1;
                }
                else if (cInput.GetButton("first"))
                {
                    targetGear = 2;
                }
                else if (cInput.GetButton("second"))
                {
                    targetGear = 3;
                }
                else if (cInput.GetButton("third"))
                {
                    targetGear = 4;
                }
                else if (cInput.GetButton("fourth"))
                {
                    targetGear = 5;
                }
                else if (cInput.GetButton("fifth"))
                {
                    targetGear = 6;
                }
                else if (cInput.GetButton("sixth"))
                {
                    targetGear = 7;
                }
                else
                {
                    targetGear = 1;
                }
            }

            return true;
        }

        private bool TryGetLocalPosition(out Vector3 position)
        {
            PlayerStateData state;
            if (_playerLocator.TryGetLocalState(out state))
            {
                position = new Vector3(state.PosX, state.PosY, state.PosZ);
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        private bool IsLocalDriver(VehicleEntry entry, Vector3 localPos, bool hasLocal)
        {
            Transform root = entry != null ? (entry.Transform ?? (entry.Body != null ? entry.Body.transform.root : null)) : null;
            if (entry == null || root == null)
            {
                return false;
            }

            float now = Time.realtimeSinceStartup;
            bool remoteDriverActive = _remoteSeatInSeat && _remoteSeatIsDriver && _remoteSeatVehicleId == entry.Id;
            bool force = entry.ForceOwnershipUntilTime > 0f && now <= entry.ForceOwnershipUntilTime;
            bool fromFsm = entry.LocalDriverFromFsm;
            bool isPassenger = entry.LocalPassengerFromFsm;

            if (remoteDriverActive && !fromFsm)
            {
                force = false;
            }

            bool childMatch = false;
            Transform body;
            Transform view;
            if (_playerLocator.TryGetLocalTransforms(out body, out view))
            {
                childMatch = IsChildOf(body, root) || IsChildOf(view, root);
            }

            bool cameraMatch = false;
            CarCameras cameras = FindCarCameras();
            if (cameras != null && cameras.driverView && cameras.mtarget != null)
            {
                Transform targetRoot = cameras.mtarget.root;
                Transform entryRoot = root.root;
                if (targetRoot == entryRoot)
                {
                    cameraMatch = true;
                }
            }

            bool seatMatch = false;
            float seatDistance = -1f;
            bool allowSeatMatch = !isPassenger;
            if (allowSeatMatch && hasLocal && entry.SeatTransform != null)
            {
                seatDistance = Vector3.Distance(localPos, entry.SeatTransform.position);
                seatMatch = seatDistance <= _settings.GetVehicleSeatDistance();
                if (seatMatch)
                {
                    entry.ForceOwnershipUntilTime = Mathf.Max(entry.ForceOwnershipUntilTime, now + SeatOwnershipHoldSeconds);
                }
            }

            if (isPassenger || (remoteDriverActive && !fromFsm))
            {
                childMatch = false;
                cameraMatch = false;
                seatMatch = false;
            }

            bool result = force || fromFsm || childMatch || cameraMatch || seatMatch;
            if (_settings != null && _settings.VerboseLogging.Value && now - entry.LastDriverLogTime >= 1f)
            {
                DebugLog.Verbose("VehicleSync: local driver eval vehicle=" + entry.DebugPath +
                    " result=" + result +
                    " force=" + force +
                    " fsm=" + fromFsm +
                    " remoteDriver=" + remoteDriverActive +
                    " passenger=" + isPassenger +
                    " child=" + childMatch +
                    " camera=" + cameraMatch +
                    " seatDist=" + seatDistance.ToString("F2") +
                    " seatOk=" + seatMatch +
                    " hasLocal=" + hasLocal);
                entry.LastDriverLogTime = now;
            }

            return result;
        }

        private static bool IsChildOf(Transform child, Transform parent)
        {
            if (child == null || parent == null)
            {
                return false;
            }
            if (child == parent)
            {
                return true;
            }
            return child.IsChildOf(parent);
        }

        private CarCameras FindCarCameras()
        {
            if (_cachedCarCameras != null)
            {
                return _cachedCarCameras;
            }

            _cachedCarCameras = UnityEngine.Object.FindObjectOfType<CarCameras>();
            return _cachedCarCameras;
        }

        private static Transform FindSeatTransform(GameObject root)
        {
            Transform driverSeat;
            Transform passengerSeat;
            FindSeatTransforms(root, out driverSeat, out passengerSeat);
            return driverSeat;
        }

        private static void FindSeatTransforms(GameObject root, out Transform driverSeat, out Transform passengerSeat)
        {
            driverSeat = null;
            passengerSeat = null;
            if (root == null)
            {
                return;
            }

            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform t = transforms[i];
                if (t == null)
                {
                    continue;
                }

                if (driverSeat == null && IsDriverSeatTransform(t))
                {
                    driverSeat = t;
                }

                if (passengerSeat == null && IsPassengerSeatTransform(t))
                {
                    passengerSeat = t;
                }
            }

            if (driverSeat == null)
            {
                driverSeat = root.transform;
            }

            if (passengerSeat == driverSeat)
            {
                passengerSeat = null;
            }
        }

        private static bool IsDriverSeatTransform(Transform transform)
        {
            if (transform == null)
            {
                return false;
            }

            if (string.Equals(transform.name, "Fixed_Camera_Driver_View", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            try
            {
                if (transform.CompareTag("Fixed_Camera_Driver_View"))
                {
                    return true;
                }
            }
            catch (UnityException)
            {
            }

            return false;
        }

        private static bool IsPassengerSeatTransform(Transform transform)
        {
            if (transform == null)
            {
                return false;
            }

            string name = transform.name ?? string.Empty;
            if (string.Equals(name, "Fixed_Camera_Passenger_View", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Fixed_Camera_Passenger", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Fixed_Camera_1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Fixed_Camera_2", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                if (transform.CompareTag("Fixed_Camera_1") || transform.CompareTag("Fixed_Camera_2"))
                {
                    return true;
                }
            }
            catch (UnityException)
            {
            }

            return false;
        }

        private static bool IsNewerSequence(uint seq, uint last)
        {
            if (seq == 0 || seq == last)
            {
                return false;
            }
            return seq > last;
        }

        private static long GetUnixTimeMs()
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(DateTime.UtcNow - epoch).TotalMilliseconds;
        }

        private static Type FindType(string typeName)
        {
            Type type = Type.GetType(typeName);
            if (type != null)
            {
                return type;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                type = assemblies[i].GetType(typeName, false);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }

        private static bool HasDriverSeatFsm(List<PlayMakerFSM> fsms)
        {
            if (fsms == null)
            {
                return false;
            }

            for (int i = 0; i < fsms.Count; i++)
            {
                if (IsDriverSeatFsm(fsms[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsDriverSeatFsm(PlayMakerFSM fsm)
        {
            if (fsm == null)
            {
                return false;
            }

            string objName = fsm.gameObject != null ? fsm.gameObject.name : string.Empty;
            if (!string.IsNullOrEmpty(objName) &&
                (objName.IndexOf("Drive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 objName.IndexOf("Driver", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            string fsmName = fsm.Fsm != null && !string.IsNullOrEmpty(fsm.Fsm.Name) ? fsm.Fsm.Name : fsm.FsmName;
            if (!string.IsNullOrEmpty(fsmName) &&
                (fsmName.IndexOf("Drive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 fsmName.IndexOf("Driver", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            return false;
        }

        private static bool IsPassengerSeatState(FsmState state)
        {
            if (state == null || string.IsNullOrEmpty(state.Name))
            {
                return false;
            }

            return NameContains(state.Name, PassengerSeatStateFilter);
        }

        private void AttachSeatTriggers(VehicleEntry entry, GameObject root)
        {
            if (entry == null || root == null)
            {
                return;
            }

            PlayMakerFSM[] fsms = root.GetComponentsInChildren<PlayMakerFSM>(true);
            if (fsms == null || fsms.Length == 0)
            {
                return;
            }

            PlayMakerFSM best = null;
            List<PlayMakerFSM> triggers = new List<PlayMakerFSM>();
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.Fsm == null)
                {
                    continue;
                }
                string fsmName = !string.IsNullOrEmpty(fsm.Fsm.Name) ? fsm.Fsm.Name : fsm.FsmName;
                if (!string.Equals(fsmName, "PlayerTrigger", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                triggers.Add(fsm);
                if (best == null)
                {
                    best = fsm;
                }

                string objName = fsm.gameObject != null ? fsm.gameObject.name : string.Empty;
                if (objName.IndexOf("Drive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    objName.IndexOf("Driver", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    best = fsm;
                    break;
                }
            }

            if (triggers.Count == 0)
            {
                if (_settings != null && _settings.VerboseLogging.Value)
                {
                    DebugLog.Verbose("VehicleSync: no PlayerTrigger FSM found for vehicle=" + entry.DebugPath);
                }
                return;
            }

            entry.SeatFsms.Clear();
            entry.SeatFsms.AddRange(triggers);
            entry.DriverSeatFsms.Clear();
            entry.PassengerSeatFsms.Clear();
            if (entry.PassengerExitAsEnterFsms == null)
            {
                entry.PassengerExitAsEnterFsms = new HashSet<int>();
            }
            entry.PassengerExitAsEnterFsms.Clear();
            bool treatAllDriver = IsSorbetVehicle(entry);
            bool hasDriverSeat = !treatAllDriver && HasDriverSeatFsm(triggers);
            for (int i = 0; i < triggers.Count; i++)
            {
                PlayMakerFSM fsm = triggers[i];
                bool isDriverSeat = treatAllDriver || !hasDriverSeat || IsDriverSeatFsm(fsm);
                if (isDriverSeat)
                {
                    entry.DriverSeatFsms.Add(fsm);
                }
                else
                {
                    entry.PassengerSeatFsms.Add(fsm);
                    if (entry.PassengerSeatTransform == null && fsm.gameObject != null)
                    {
                        entry.PassengerSeatTransform = fsm.gameObject.transform;
                    }

                    List<FsmState> enterStates = FindStatesByNameContains(fsm, SeatEnterFilter);
                    List<FsmState> exitStates = FindStatesByNameContains(fsm, SeatExitFilter);
                    if (enterStates.Count == 0 && exitStates.Count > 0)
                    {
                        entry.PassengerExitAsEnterFsms.Add(fsm.GetInstanceID());
                    }
                }
            }
            entry.SeatFsm = best;
            entry.SeatFsmName = !string.IsNullOrEmpty(best.Fsm.Name) ? best.Fsm.Name : best.FsmName;
            if (best.gameObject != null && (!hasDriverSeat || IsDriverSeatFsm(best) || entry.SeatTransform == null))
            {
                entry.SeatTransform = best.gameObject.transform;
            }
            SeedSeatStateFromFsm(entry);
            if (!_loggedSorbetSeatDump && entry.DebugPath != null &&
                entry.DebugPath.IndexOf("SORBET", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _loggedSorbetSeatDump = true;
                for (int i = 0; i < triggers.Count; i++)
                {
                    PlayMakerFSM fsm = triggers[i];
                    string objName = fsm != null && fsm.gameObject != null ? fsm.gameObject.name : "<null>";
                    DumpFsmStates(fsm, entry.DebugPath + " obj=" + objName);
                }
            }

            int hookedCount = 0;
            int eventHookedCount = 0;
            for (int i = 0; i < triggers.Count; i++)
            {
                PlayMakerFSM fsm = triggers[i];
                bool isDriverSeat = !hasDriverSeat || IsDriverSeatFsm(fsm);
                if (HookSeatFsm(entry, fsm, isDriverSeat))
                {
                    hookedCount++;
                }
                if (HookSeatEventTransitions(entry, fsm, isDriverSeat))
                {
                    eventHookedCount++;
                }
            }
            if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("VehicleSync: seat FSM hooked vehicle=" + entry.DebugPath +
                    " triggers=" + triggers.Count +
                    " stateHooks=" + hookedCount +
                    " eventHooks=" + eventHookedCount);
            }

            EnsurePassengerSeatTrigger(entry, root);
        }

        private void EnsurePassengerSeatTrigger(VehicleEntry entry, GameObject root)
        {
            if (entry == null || root == null || !IsSorbetVehicle(entry))
            {
                return;
            }

            Transform existing = root.transform.Find("MWC_MP_PassengerSeat");
            PassengerSeatTrigger seatTrigger = existing != null ? existing.GetComponent<PassengerSeatTrigger>() : null;
            if (seatTrigger == null)
            {
                Transform anchor = new GameObject("MWC_MP_PassengerSeat").transform;
                anchor.SetParent(root.transform, false);

                Vector3 localPos = Vector3.zero;
                Quaternion localRot = Quaternion.identity;

                if (entry.PassengerSeatTransform != null && entry.PassengerSeatTransform != root.transform)
                {
                    localPos = root.transform.InverseTransformPoint(entry.PassengerSeatTransform.position);
                    localRot = Quaternion.Inverse(root.transform.rotation) * entry.PassengerSeatTransform.rotation;
                }
                else if (entry.SeatTransform != null)
                {
                    Vector3 driverLocal = root.transform.InverseTransformPoint(entry.SeatTransform.position);
                    localPos = new Vector3(-driverLocal.x, driverLocal.y, driverLocal.z);
                    localPos.x += 0.1f;
                    localPos.z -= 0.05f;
                    localRot = Quaternion.Inverse(root.transform.rotation) * entry.SeatTransform.rotation;
                }

                anchor.localPosition = localPos;
                anchor.localRotation = localRot;

                BoxCollider collider = anchor.gameObject.AddComponent<BoxCollider>();
                collider.isTrigger = true;
                collider.size = new Vector3(0.35f, 0.45f, 0.35f);

                seatTrigger = anchor.gameObject.AddComponent<PassengerSeatTrigger>();
                seatTrigger.Initialize(this, entry.Id);

                if (entry.PassengerSeatTransform == null || entry.PassengerSeatTransform == entry.SeatTransform)
                {
                    entry.PassengerSeatTransform = anchor;
                }

                DebugLog.Warn("VehicleSync: passenger seat trigger created vehicle=" + entry.DebugPath);
            }
            else
            {
                seatTrigger.Initialize(this, entry.Id);
            }
        }

        internal void NotifySeatEvent(uint vehicleId, bool inSeat, bool isDriverSeat)
        {
            VehicleEntry entry;
            if (!_vehicleLookup.TryGetValue(vehicleId, out entry))
            {
                return;
            }

            if (isDriverSeat)
            {
                if (entry.LocalDriverFromFsm == inSeat)
                {
                    return;
                }

                if (inSeat)
                {
                    float now = Time.realtimeSinceStartup;
                    entry.ForceOwnershipUntilTime = Mathf.Max(entry.ForceOwnershipUntilTime, now + SeatOwnershipHoldSeconds);
                    entry.LastOwnershipRequestTime = 0f;
                    entry.LocalPassengerFromFsm = false;
                }
                else
                {
                    entry.ForceOwnershipUntilTime = 0f;
                }

                entry.LocalDriverFromFsm = inSeat;
                QueueSeatEvent(entry, inSeat, true);
                if (_settings != null && _settings.VerboseLogging.Value)
                {
                    DebugLog.Verbose("VehicleSync: seat event vehicle=" + entry.DebugPath + " inSeat=" + inSeat + " role=driver");
                }
            }
            else
            {
                if (entry.LocalPassengerFromFsm == inSeat)
                {
                    return;
                }

                entry.LocalPassengerFromFsm = inSeat;
                if (inSeat)
                {
                    entry.ForceOwnershipUntilTime = 0f;
                }

                QueueSeatEvent(entry, inSeat, false);
                if (_settings != null && _settings.VerboseLogging.Value)
                {
                    DebugLog.Verbose("VehicleSync: seat event vehicle=" + entry.DebugPath + " inSeat=" + inSeat + " role=passenger");
                }
            }
        }

        private void QueueSeatEvent(VehicleEntry entry, bool inSeat, bool isDriver)
        {
            if (entry == null)
            {
                return;
            }

            if (isDriver)
            {
                if (entry.LastSentDriverInSeat == inSeat)
                {
                    return;
                }
                entry.LastSentDriverInSeat = inSeat;
            }
            else
            {
                if (entry.LastSentPassengerInSeat == inSeat)
                {
                    return;
                }
                entry.LastSentPassengerInSeat = inSeat;
            }

            _seatEventQueue.Add(new VehicleSeatData
            {
                UnixTimeMs = GetUnixTimeMs(),
                VehicleId = entry.Id,
                SeatRole = (byte)(isDriver ? 0 : 1),
                InSeat = (byte)(inSeat ? 1 : 0)
            });
        }

        private bool HookSeatFsm(VehicleEntry entry, PlayMakerFSM fsm, bool isDriverSeat)
        {
            if (entry == null || fsm == null || fsm.Fsm == null)
            {
                return false;
            }

            List<FsmState> enterStates = FindStatesByNameContains(fsm, SeatEnterFilter);
            List<FsmState> exitStates = FindStatesByNameContains(fsm, SeatExitFilter);
            int fsmId = fsm.GetInstanceID();
            bool exitAsEnter = !isDriverSeat &&
                entry.PassengerExitAsEnterFsms != null &&
                entry.PassengerExitAsEnterFsms.Contains(fsmId);
            if (exitAsEnter && enterStates.Count == 0 && exitStates.Count > 0)
            {
                List<FsmState> pressReturnStates = new List<FsmState>();
                for (int i = 0; i < exitStates.Count; i++)
                {
                    FsmState state = exitStates[i];
                    if (state == null || string.IsNullOrEmpty(state.Name))
                    {
                        continue;
                    }
                    if (NameContains(state.Name, PassengerPressReturnFilter))
                    {
                        pressReturnStates.Add(state);
                    }
                }
                if (pressReturnStates.Count > 0)
                {
                    enterStates = pressReturnStates;
                    exitStates = new List<FsmState>();
                }
            }
            if (enterStates.Count == 0 && exitStates.Count == 0)
            {
                if (_settings != null && _settings.VerboseLogging.Value)
                {
                    string objName = fsm.gameObject != null ? fsm.gameObject.name : "<null>";
                    DebugLog.Verbose("VehicleSync: seat FSM state tokens missing vehicle=" + entry.DebugPath +
                        " fsm=" + (string.IsNullOrEmpty(entry.SeatFsmName) ? fsm.FsmName : entry.SeatFsmName) +
                        " obj=" + objName);
                }
                return false;
            }

            for (int i = 0; i < enterStates.Count; i++)
            {
                bool stateIsPassenger = IsPassengerSeatState(enterStates[i]);
                bool roleIsDriver = isDriverSeat && !stateIsPassenger;
                PlayMakerBridge.PrependAction(enterStates[i], new VehicleSeatAction(this, entry.Id, true, roleIsDriver));
            }
            for (int i = 0; i < exitStates.Count; i++)
            {
                bool stateIsPassenger = IsPassengerSeatState(exitStates[i]);
                bool roleIsDriver = isDriverSeat && !stateIsPassenger;
                PlayMakerBridge.PrependAction(exitStates[i], new VehicleSeatAction(this, entry.Id, false, roleIsDriver));
            }

            if (_settings != null && _settings.VerboseLogging.Value)
            {
                string objName = fsm.gameObject != null ? fsm.gameObject.name : "<null>";
                DebugLog.Verbose("VehicleSync: seat FSM hook vehicle=" + entry.DebugPath +
                    " fsm=" + fsm.FsmName +
                    " obj=" + objName +
                    " enterStates=" + enterStates.Count +
                    " exitStates=" + exitStates.Count);
            }

            return true;
        }

        private bool HookSeatEventTransitions(VehicleEntry entry, PlayMakerFSM fsm, bool isDriverSeat)
        {
            if (entry == null || fsm == null || fsm.Fsm == null)
            {
                return false;
            }

            int fsmId = fsm.GetInstanceID();
            if (entry.SeatEventHooked == null)
            {
                entry.SeatEventHooked = new HashSet<int>();
            }
            if (entry.SeatEventHooked.Contains(fsmId))
            {
                return false;
            }

            FsmState[] states = fsm.Fsm.States;
            if (states == null || states.Length == 0)
            {
                return false;
            }

            entry.SeatEventHooked.Add(fsmId);
            for (int i = 0; i < states.Length; i++)
            {
                FsmState state = states[i];
                if (state == null)
                {
                    continue;
                }
                PlayMakerBridge.PrependAction(state, new VehicleSeatEventAction(this, entry.Id, fsm, isDriverSeat));
            }

            if (_settings != null && _settings.VerboseLogging.Value)
            {
                string objName = fsm.gameObject != null ? fsm.gameObject.name : "<null>";
                DebugLog.Verbose("VehicleSync: seat event hook vehicle=" + entry.DebugPath +
                    " fsm=" + fsm.FsmName +
                    " obj=" + objName +
                    " states=" + states.Length);
            }

            return true;
        }

        private static void DumpFsmStates(PlayMakerFSM fsm, string vehiclePath)
        {
            if (fsm == null || fsm.Fsm == null)
            {
                return;
            }

            FsmState[] states = fsm.Fsm.States;
            if (states == null || states.Length == 0)
            {
                DebugLog.Verbose("VehicleSync: seat FSM dump (no states) vehicle=" + vehiclePath);
                return;
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i] == null || string.IsNullOrEmpty(states[i].Name))
                {
                    continue;
                }
                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(states[i].Name);
            }

            string fsmName = !string.IsNullOrEmpty(fsm.Fsm.Name) ? fsm.Fsm.Name : fsm.FsmName;
            DebugLog.Verbose("VehicleSync: seat FSM states vehicle=" + vehiclePath +
                " fsm=" + fsmName +
                " states=" + builder.ToString());
        }

        private static Transform GetSyncTransform(VehicleEntry entry)
        {
            if (entry == null)
            {
                return null;
            }
            if (entry.Body != null)
            {
                return entry.Body.transform;
            }
            return entry.Transform;
        }

        private static float GetCurrentSteer(VehicleEntry entry)
        {
            if (entry == null)
            {
                return 0f;
            }

            CarController controller = null;
            if (entry.CarDynamics != null && entry.CarDynamics.carController != null)
            {
                controller = entry.CarDynamics.carController;
            }
            else if (entry.AxisController != null)
            {
                controller = entry.AxisController;
            }
            else if (entry.RemoteController != null)
            {
                controller = entry.RemoteController;
            }

            return controller != null ? controller.steering : 0f;
        }

        private static bool NameContains(string name, string filter)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(filter))
            {
                return false;
            }
            string[] tokens = filter.Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i].Trim();
                if (token.Length == 0)
                {
                    continue;
                }
                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static List<FsmState> FindStatesByNameContains(PlayMakerFSM fsm, string filter)
        {
            List<FsmState> matches = new List<FsmState>();
            if (fsm == null || fsm.Fsm == null || string.IsNullOrEmpty(filter))
            {
                return matches;
            }

            FsmState[] states = fsm.Fsm.States;
            if (states == null || states.Length == 0)
            {
                return matches;
            }

            for (int i = 0; i < states.Length; i++)
            {
                FsmState state = states[i];
                if (state == null || string.IsNullOrEmpty(state.Name))
                {
                    continue;
                }
                if (NameContains(state.Name, filter))
                {
                    matches.Add(state);
                }
            }

            return matches;
        }

        private void SeedSeatStateFromFsm(VehicleEntry entry)
        {
            bool inSeat;
            bool isPassenger;
            string stateName;
            string fsmName;
            if (!TryEvaluateSeatState(entry, out inSeat, out isPassenger, out stateName, out fsmName))
            {
                return;
            }

            if (isPassenger)
            {
                entry.LocalPassengerFromFsm = inSeat;
                if (inSeat)
                {
                    entry.LocalDriverFromFsm = false;
                }
                QueueSeatEvent(entry, inSeat, false);
            }
            else
            {
                entry.LocalDriverFromFsm = inSeat;
                if (inSeat)
                {
                    entry.LocalPassengerFromFsm = false;
                }
                QueueSeatEvent(entry, inSeat, true);
            }
            if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("VehicleSync: seat FSM active vehicle=" + entry.DebugPath +
                    " fsm=" + (fsmName ?? "<null>") +
                    " state=" + (stateName ?? "<null>") +
                    " inSeat=" + inSeat +
                    " role=" + (isPassenger ? "passenger" : "driver"));
            }
        }

        private void RefreshSeatFromFsm(VehicleEntry entry, float now)
        {
            if (entry == null)
            {
                return;
            }

            TraceSeatFsmState(entry, now);
            if (now - entry.LastSeatStatePollTime < 0.25f)
            {
                return;
            }
            entry.LastSeatStatePollTime = now;

            bool inSeat;
            bool isPassenger;
            string stateName;
            string fsmName;
            if (!TryEvaluateSeatState(entry, out inSeat, out isPassenger, out stateName, out fsmName))
            {
                return;
            }

            if (isPassenger)
            {
                if (entry.LocalPassengerFromFsm == inSeat)
                {
                    return;
                }
                entry.LocalPassengerFromFsm = inSeat;
                if (inSeat)
                {
                    entry.ForceOwnershipUntilTime = 0f;
                    entry.LocalDriverFromFsm = false;
                }
                QueueSeatEvent(entry, inSeat, false);
            }
            else
            {
                if (entry.LocalDriverFromFsm == inSeat)
                {
                    return;
                }
                entry.LocalDriverFromFsm = inSeat;
                if (inSeat)
                {
                    entry.ForceOwnershipUntilTime = Mathf.Max(entry.ForceOwnershipUntilTime, now + SeatOwnershipHoldSeconds);
                    entry.LastOwnershipRequestTime = 0f;
                    entry.LocalPassengerFromFsm = false;
                }
                else
                {
                    entry.ForceOwnershipUntilTime = 0f;
                }
                QueueSeatEvent(entry, inSeat, true);
            }
            if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("VehicleSync: seat FSM poll vehicle=" + entry.DebugPath +
                    " fsm=" + (fsmName ?? "<null>") +
                    " state=" + (stateName ?? "<null>") +
                    " inSeat=" + inSeat +
                    " role=" + (isPassenger ? "passenger" : "driver"));
            }
        }

        private void TraceSeatFsmState(VehicleEntry entry, float now)
        {
            if (entry == null || _settings == null || !_settings.VerboseLogging.Value)
            {
                return;
            }

            if (string.IsNullOrEmpty(entry.DebugPath) ||
                entry.DebugPath.IndexOf("SORBET", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            List<PlayMakerFSM> fsms = entry.DriverSeatFsms != null && entry.DriverSeatFsms.Count > 0 ? entry.DriverSeatFsms : entry.SeatFsms;
            if (fsms == null || fsms.Count == 0)
            {
                return;
            }

            if (entry.SeatFsmStateNames == null)
            {
                entry.SeatFsmStateNames = new Dictionary<int, string>();
            }
            if (entry.SeatFsmEventNames == null)
            {
                entry.SeatFsmEventNames = new Dictionary<int, string>();
            }

            bool logged = false;
            for (int i = 0; i < fsms.Count; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.Fsm == null)
                {
                    continue;
                }

                int fsmId = fsm.GetInstanceID();
                string stateName = fsm.Fsm.ActiveStateName ?? "<null>";
                string eventName = fsm.Fsm.LastTransition != null ? fsm.Fsm.LastTransition.EventName : "<null>";

                string prevState;
                string prevEvent;
                entry.SeatFsmStateNames.TryGetValue(fsmId, out prevState);
                entry.SeatFsmEventNames.TryGetValue(fsmId, out prevEvent);

                if (!string.Equals(stateName, prevState, StringComparison.Ordinal) ||
                    !string.Equals(eventName, prevEvent, StringComparison.Ordinal))
                {
                    string fsmName = !string.IsNullOrEmpty(fsm.Fsm.Name) ? fsm.Fsm.Name : fsm.FsmName;
                    DebugLog.Verbose("VehicleSync: seat FSM trace vehicle=" + entry.DebugPath +
                        " fsm=" + fsmName +
                        " state=" + stateName +
                        " event=" + eventName);
                    entry.SeatFsmStateNames[fsmId] = stateName;
                    entry.SeatFsmEventNames[fsmId] = eventName;
                    logged = true;
                }
            }

            if (logged)
            {
                entry.LastSeatTraceTime = now;
            }
        }

        private bool TryEvaluateSeatState(VehicleEntry entry, out bool inSeat, out bool isPassenger, out string stateName, out string fsmName)
        {
            inSeat = false;
            isPassenger = false;
            stateName = null;
            fsmName = null;

            if (entry == null)
            {
                return false;
            }

            bool found = false;
            bool allowPassengerFsmTag = !IsSorbetVehicle(entry);
            List<PlayMakerFSM> fsms = entry.SeatFsms;
            if (fsms != null && fsms.Count > 0)
            {
                for (int i = 0; i < fsms.Count; i++)
                {
                    bool fsmInSeat;
                    bool fsmPassenger;
                    string fsmState;
                    string fsmLabel;
                    PlayMakerFSM fsm = fsms[i];
                    bool exitAsEnter = entry.PassengerExitAsEnterFsms != null &&
                        fsm != null &&
                        entry.PassengerExitAsEnterFsms.Contains(fsm.GetInstanceID());
                    if (!TryGetSeatStateFromFsm(fsm, exitAsEnter, out fsmInSeat, out fsmPassenger, out fsmState, out fsmLabel))
                    {
                        continue;
                    }

                    if (!allowPassengerFsmTag && fsmPassenger)
                    {
                        fsmPassenger = false;
                    }

                    if (allowPassengerFsmTag && !fsmPassenger && entry.PassengerSeatFsms != null && entry.PassengerSeatFsms.Contains(fsms[i]))
                    {
                        fsmPassenger = true;
                    }

                    found = true;
                    if (fsmInSeat)
                    {
                        inSeat = true;
                        isPassenger = fsmPassenger;
                        stateName = fsmState;
                        fsmName = fsmLabel;
                        return true;
                    }

                    if (stateName == null)
                    {
                        stateName = fsmState;
                        fsmName = fsmLabel;
                        isPassenger = fsmPassenger;
                    }
                }
            }
            else
            {
                bool exitAsEnter = entry.PassengerExitAsEnterFsms != null &&
                    entry.SeatFsm != null &&
                    entry.PassengerExitAsEnterFsms.Contains(entry.SeatFsm.GetInstanceID());
                found = TryGetSeatStateFromFsm(entry.SeatFsm, exitAsEnter, out inSeat, out isPassenger, out stateName, out fsmName);
            }

            return found;
        }

        private static bool TryGetSeatStateFromFsm(PlayMakerFSM fsm, bool exitAsEnter, out bool inSeat, out bool isPassenger, out string stateName, out string fsmName)
        {
            inSeat = false;
            isPassenger = false;
            stateName = null;
            fsmName = null;

            if (fsm == null || fsm.Fsm == null)
            {
                return false;
            }

            FsmState active = fsm.Fsm.ActiveState;
            if (active == null || string.IsNullOrEmpty(active.Name))
            {
                return false;
            }

            bool inSeatFlag = NameContains(active.Name, SeatEnterFilter);
            bool outSeatFlag = NameContains(active.Name, SeatExitFilter);
            if (!inSeatFlag && !outSeatFlag)
            {
                return false;
            }

            bool pressReturnFlag = NameContains(active.Name, PassengerPressReturnFilter);
            if (exitAsEnter && !inSeatFlag && pressReturnFlag)
            {
                inSeat = true;
                isPassenger = true;
            }
            else
            {
                inSeat = inSeatFlag && !outSeatFlag;
                isPassenger = IsPassengerSeatState(active);
            }
            stateName = active.Name;
            fsmName = !string.IsNullOrEmpty(fsm.Fsm.Name) ? fsm.Fsm.Name : fsm.FsmName;
            return true;
        }

        private enum VehicleControlMode
        {
            Unknown = 0,
            Local = 1,
            Remote = 2,
            Passive = 3
        }

        private sealed class VehicleEntry
        {
            public uint Id;
            public string Key;
            public string DebugPath;
            public Transform Transform;
            public Rigidbody Body;
            public CarDynamics CarDynamics;
            public AxisCarController AxisController;
            public MPCarController RemoteController;
            public Drivetrain Drivetrain;
            public BrakeLights BrakeLights;
            public DashBoard DashBoard;
            public SteeringWheel SteeringWheel;
            public SoundController SoundController;
            public Axles Axles;
            public Wheel[] Wheels;
            public Transform SeatTransform;
            public Transform PassengerSeatTransform;
            public OwnerKind Owner;
            public CarDynamics.Controller OriginalController;
            public bool LocalWantsControl;
            public float LastOwnershipRequestTime;
            public float LastOwnershipLogTime;
            public Vector3 LastSentPosition;
            public Quaternion LastSentRotation;
            public uint LastRemoteSequence;
            public bool HasRemoteState;
            public Vector3 RemoteTargetPosition;
            public Quaternion RemoteTargetRotation = Quaternion.identity;
            public Vector3 RemoteTargetVelocity;
            public Vector3 RemoteTargetAngVelocity;
            public Vector3 RemoteSmoothVelocity;
            public float RemoteTargetTime;
            public float RemoteSteer;
            public float RemoteTargetSteer;
            public float RemoteSteerVelocity;
            public int RemoteGear;
            public float RemoteRpm;
            public VehicleControlMode ControlMode;
            public bool RemoteControlActive;
            public uint LastControlSequence;
            public float LastControlReceiveTime;
            public bool LocalDriverFromFsm;
            public bool LocalPassengerFromFsm;
            public float LastControlLogTime;
            public string SeatFsmName;
            public PlayMakerFSM SeatFsm;
            public List<PlayMakerFSM> SeatFsms = new List<PlayMakerFSM>();
            public List<PlayMakerFSM> DriverSeatFsms = new List<PlayMakerFSM>();
            public List<PlayMakerFSM> PassengerSeatFsms = new List<PlayMakerFSM>();
            public float LastSeatStatePollTime;
            public HashSet<int> SeatEventHooked = new HashSet<int>();
            public HashSet<int> PassengerExitAsEnterFsms = new HashSet<int>();
            public float LastDriverLogTime;
            public float LastAxisLogTime;
            public float ForceOwnershipUntilTime;
            public float LastSeatTraceTime;
            public Dictionary<int, string> SeatFsmStateNames;
            public Dictionary<int, string> SeatFsmEventNames;
            public bool LoggedNoWheels;
            public float[] WheelSpin;
            public bool WheelsDisabled;
            public bool LastSentDriverInSeat;
            public bool LastSentPassengerInSeat;
        }

        private sealed class VehicleCandidate
        {
            public GameObject Root;
            public string Key;
            public string DebugPath;
        }

        private sealed class VehicleSeatAction : FsmStateAction
        {
            private readonly VehicleSync _owner;
            private readonly uint _vehicleId;
            private readonly bool _inSeat;
            private readonly bool _isDriverSeat;

            public VehicleSeatAction(VehicleSync owner, uint vehicleId, bool inSeat, bool isDriverSeat)
            {
                _owner = owner;
                _vehicleId = vehicleId;
                _inSeat = inSeat;
                _isDriverSeat = isDriverSeat;
            }

            public override void OnEnter()
            {
                Finish();
                if (_owner != null)
                {
                    _owner.NotifySeatEvent(_vehicleId, _inSeat, _isDriverSeat);
                }
            }
        }

        private sealed class VehicleSeatEventAction : FsmStateAction
        {
            private readonly VehicleSync _owner;
            private readonly uint _vehicleId;
            private readonly PlayMakerFSM _fsm;
            private readonly bool _isDriverSeat;

            public VehicleSeatEventAction(VehicleSync owner, uint vehicleId, PlayMakerFSM fsm, bool isDriverSeat)
            {
                _owner = owner;
                _vehicleId = vehicleId;
                _fsm = fsm;
                _isDriverSeat = isDriverSeat;
            }

            public override void OnEnter()
            {
                Finish();
                if (_owner == null || _fsm == null || _fsm.Fsm == null)
                {
                    return;
                }

                FsmTransition transition = _fsm.Fsm.LastTransition;
                if (transition == null || string.IsNullOrEmpty(transition.EventName))
                {
                    return;
                }

                string eventName = transition.EventName;
                if (eventName.StartsWith("MP_", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (NameContains(eventName, SeatEnterEventFilter))
                {
                    _owner.NotifySeatEvent(_vehicleId, true, _isDriverSeat);
                }
                else if (NameContains(eventName, SeatExitEventFilter))
                {
                    _owner.NotifySeatEvent(_vehicleId, false, _isDriverSeat);
                }
            }
        }
    }
}
