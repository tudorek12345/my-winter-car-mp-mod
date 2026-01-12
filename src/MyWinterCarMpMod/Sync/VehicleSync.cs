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
        private CarCameras _cachedCarCameras;
        private int _lastSceneIndex = int.MinValue;
        private string _lastSceneName = string.Empty;
        private float _nextSampleTime;
        private bool _loggedNoVehicles;
        private bool _loggedSorbetSeatDump;

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
            ScanVehicles();
        }

        public int CollectChanges(long unixTimeMs, float now, List<VehicleStateData> buffer, OwnerKind localOwner, bool includeUnowned)
        {
            buffer.Clear();
            if (!Enabled || _vehicles.Count == 0)
            {
                return 0;
            }

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
                if (entry.Transform == null)
                {
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
                    AngVelZ = angVel.z
                };
                buffer.Add(state);
            }

            return buffer.Count;
        }

        public void ApplyRemote(VehicleStateData state, OwnerKind localOwner, bool includeUnowned)
        {
            if (!Enabled)
            {
                return;
            }

            VehicleEntry entry;
            if (!_vehicleLookup.TryGetValue(state.VehicleId, out entry))
            {
                if (!_loggedNoVehicles)
                {
                    DebugLog.Warn("VehicleSync missing vehicle id " + state.VehicleId + ". Re-scan on next scene change.");
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

            if (entry.Body != null)
            {
                entry.Body.MovePosition(pos);
                entry.Body.MoveRotation(rot);
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

        public int CollectOwnershipRequests(OwnerKind localOwner, List<OwnershipRequestData> buffer)
        {
            buffer.Clear();
            if (!Enabled || _vehicles.Count == 0 || localOwner != OwnerKind.Client || !_settings.VehicleOwnershipEnabled.Value)
            {
                return 0;
            }

            float now = Time.realtimeSinceStartup;
            Vector3 localPos;
            bool hasLocal = TryGetLocalPosition(out localPos);

            for (int i = 0; i < _vehicles.Count; i++)
            {
                VehicleEntry entry = _vehicles[i];
                if (entry.Transform == null)
                {
                    continue;
                }

                bool wants = IsLocalDriver(entry, localPos, hasLocal);
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

            for (int i = 0; i < _vehicles.Count; i++)
            {
                VehicleEntry entry = _vehicles[i];
                if (entry.Transform == null)
                {
                    continue;
                }

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
                    AngVelZ = angVel.z
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
                ApplyLocalControl(entry, localOwner);
            }
        }

        public void Clear()
        {
            _vehicles.Clear();
            _vehicleLookup.Clear();
            _loggedNoVehicles = false;
            _cachedCarCameras = null;
        }

        private void ScanVehicles()
        {
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
                AddVehicle(candidate.Root, uniqueKey, candidate.DebugPath);
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

            Transform seatTransform = FindSeatTransform(root);

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
                Owner = OwnerKind.None,
                OriginalController = dynamics != null ? dynamics.controller : CarDynamics.Controller.axis,
                LastSentPosition = syncTransform != null ? syncTransform.position : Vector3.zero,
                LastSentRotation = syncTransform != null ? syncTransform.rotation : Quaternion.identity
            };

            _vehicles.Add(entry);
            _vehicleLookup.Add(id, entry);
            if (syncTransform != null)
            {
                DebugLog.Info("VehicleSync: add vehicle id=" + entry.Id + " root=" + root.name + " sync=" + syncTransform.name + " path=" + debugPath);
            }

            AttachSeatTriggers(entry, root);
        }

        private bool IsLocalAuthority(VehicleEntry entry, OwnerKind localOwner, bool includeUnowned)
        {
            if (entry == null)
            {
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
            if (entry == null || entry.CarDynamics == null)
            {
                if (entry == null || entry.Body == null)
                {
                    return;
                }
            }

            bool allowUnowned = localOwner == OwnerKind.Host;
            bool isLocal = entry.Owner == localOwner || (allowUnowned && entry.Owner == OwnerKind.None);
            if (!entry.ControlInitialized || entry.LastControlIsLocal != isLocal)
            {
                if (entry.CarDynamics != null)
                {
                    if (isLocal)
                    {
                        entry.CarDynamics.SetController(entry.OriginalController.ToString());
                    }
                    else
                    {
                        entry.CarDynamics.SetController(CarDynamics.Controller.external.ToString());
                    }
                }
                entry.LastControlIsLocal = isLocal;
                entry.ControlInitialized = true;
            }

            if (entry.Body != null)
            {
                if (entry.Body.isKinematic)
                {
                    entry.Body.isKinematic = false;
                }
            }
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
            if (entry == null || entry.Transform == null)
            {
                return false;
            }

            if (entry.LocalDriverFromFsm)
            {
                return true;
            }

            Transform body;
            Transform view;
            if (_playerLocator.TryGetLocalTransforms(out body, out view))
            {
                Transform root = entry.Transform;
                if (IsChildOf(body, root) || IsChildOf(view, root))
                {
                    return true;
                }
            }

            CarCameras cameras = FindCarCameras();
            if (cameras != null && cameras.driverView && cameras.mtarget != null)
            {
                Transform targetRoot = cameras.mtarget.root;
                Transform entryRoot = entry.Transform.root;
                if (targetRoot == entryRoot)
                {
                    return true;
                }
            }

            if (!hasLocal || entry.SeatTransform == null)
            {
                return false;
            }

            float dist = Vector3.Distance(localPos, entry.SeatTransform.position);
            return dist <= _settings.GetVehicleSeatDistance();
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
            if (root == null)
            {
                return null;
            }

            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform t = transforms[i];
                if (t == null)
                {
                    continue;
                }
                if (string.Equals(t.name, "Fixed_Camera_Driver_View", StringComparison.OrdinalIgnoreCase))
                {
                    return t;
                }
                try
                {
                    if (t.CompareTag("Fixed_Camera_Driver_View"))
                    {
                        return t;
                    }
                }
                catch (UnityException)
                {
                }
            }
            return root.transform;
        }

        private static bool IsNewerSequence(uint seq, uint last)
        {
            if (seq == 0 || seq == last)
            {
                return false;
            }
            return seq > last;
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

            if (best == null)
            {
                return;
            }

            FsmState enterState = best.Fsm.GetState("Player in car");
            FsmState exitState = best.Fsm.GetState("Wait for player");
            if (enterState == null)
            {
                enterState = PlayMakerBridge.FindStateByNameContains(best, new[] { "player in", "enter", "drive" });
            }
            if (exitState == null)
            {
                exitState = PlayMakerBridge.FindStateByNameContains(best, new[] { "wait", "exit", "leave" });
            }

            if (enterState == null || exitState == null)
            {
                return;
            }

            entry.SeatFsmName = !string.IsNullOrEmpty(best.Fsm.Name) ? best.Fsm.Name : best.FsmName;
            PlayMakerBridge.PrependAction(enterState, new VehicleSeatAction(this, entry.Id, true));
            PlayMakerBridge.PrependAction(exitState, new VehicleSeatAction(this, entry.Id, false));
            if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("VehicleSync: seat FSM hooked vehicle=" + entry.DebugPath + " fsm=" + entry.SeatFsmName);
            }
        }

        private void NotifySeatEvent(uint vehicleId, bool inSeat)
        {
            VehicleEntry entry;
            if (!_vehicleLookup.TryGetValue(vehicleId, out entry))
            {
                return;
            }

            if (entry.LocalDriverFromFsm == inSeat)
            {
                return;
            }

            entry.LocalDriverFromFsm = inSeat;
            if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("VehicleSync: seat event vehicle=" + entry.DebugPath + " inSeat=" + inSeat);
            }
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

        private sealed class VehicleEntry
        {
            public uint Id;
            public string Key;
            public string DebugPath;
            public Transform Transform;
            public Rigidbody Body;
            public CarDynamics CarDynamics;
            public Transform SeatTransform;
            public OwnerKind Owner;
            public CarDynamics.Controller OriginalController;
            public bool LocalWantsControl;
            public float LastOwnershipRequestTime;
            public Vector3 LastSentPosition;
            public Quaternion LastSentRotation;
            public uint LastRemoteSequence;
            public bool LastControlIsLocal;
            public bool ControlInitialized;
            public bool LocalDriverFromFsm;
            public string SeatFsmName;
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

            public VehicleSeatAction(VehicleSync owner, uint vehicleId, bool inSeat)
            {
                _owner = owner;
                _vehicleId = vehicleId;
                _inSeat = inSeat;
            }

            public override void OnEnter()
            {
                Finish();
                if (_owner != null)
                {
                    _owner.NotifySeatEvent(_vehicleId, _inSeat);
                }
            }
        }
    }
}
