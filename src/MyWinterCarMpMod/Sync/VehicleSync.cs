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
        private const string SeatEnterFilter = "player in,playerin,incar,in car,seatbelt,drive,driver,ignition,enter";
        private const string SeatExitFilter = "wait,press return,press,exit,leave";
        private const string SeatEnterEventFilter = "player in car,player in,incar,in car";
        private const string SeatExitEventFilter = "wait for player,wait,press return,press,exit,leave";

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

                RefreshSeatFromFsm(entry, now);
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
            float now = Time.realtimeSinceStartup;

            for (int i = 0; i < _vehicles.Count; i++)
            {
                VehicleEntry entry = _vehicles[i];
                if (entry.Transform == null)
                {
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

        private VehicleEntry FindEntryByTransform(Transform transform)
        {
            if (transform == null)
            {
                return null;
            }

            for (int i = 0; i < _vehicles.Count; i++)
            {
                VehicleEntry entry = _vehicles[i];
                if (entry == null || entry.Transform == null)
                {
                    continue;
                }

                if (transform == entry.Transform || transform.IsChildOf(entry.Transform))
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

            float now = Time.realtimeSinceStartup;
            bool force = entry.ForceOwnershipUntilTime > 0f && now <= entry.ForceOwnershipUntilTime;
            bool fromFsm = entry.LocalDriverFromFsm;

            bool childMatch = false;
            Transform body;
            Transform view;
            if (_playerLocator.TryGetLocalTransforms(out body, out view))
            {
                Transform root = entry.Transform;
                childMatch = IsChildOf(body, root) || IsChildOf(view, root);
            }

            bool cameraMatch = false;
            CarCameras cameras = FindCarCameras();
            if (cameras != null && cameras.driverView && cameras.mtarget != null)
            {
                Transform targetRoot = cameras.mtarget.root;
                Transform entryRoot = entry.Transform.root;
                if (targetRoot == entryRoot)
                {
                    cameraMatch = true;
                }
            }

            bool seatMatch = false;
            float seatDistance = -1f;
            if (hasLocal && entry.SeatTransform != null)
            {
                seatDistance = Vector3.Distance(localPos, entry.SeatTransform.position);
                seatMatch = seatDistance <= _settings.GetVehicleSeatDistance();
            }

            bool result = force || fromFsm || childMatch || cameraMatch || seatMatch;
            if (_settings != null && _settings.VerboseLogging.Value && now - entry.LastDriverLogTime >= 1f)
            {
                DebugLog.Verbose("VehicleSync: local driver eval vehicle=" + entry.DebugPath +
                    " result=" + result +
                    " force=" + force +
                    " fsm=" + fromFsm +
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
            entry.SeatFsm = best;
            entry.SeatFsmName = !string.IsNullOrEmpty(best.Fsm.Name) ? best.Fsm.Name : best.FsmName;
            entry.SeatTransform = best.gameObject != null ? best.gameObject.transform : entry.SeatTransform;
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
                if (HookSeatFsm(entry, triggers[i]))
                {
                    hookedCount++;
                }
                if (HookSeatEventTransitions(entry, triggers[i]))
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

            if (inSeat)
            {
                entry.ForceOwnershipUntilTime = Time.realtimeSinceStartup + 2f;
            }
            else
            {
                entry.ForceOwnershipUntilTime = 0f;
            }

            entry.LocalDriverFromFsm = inSeat;
            if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("VehicleSync: seat event vehicle=" + entry.DebugPath + " inSeat=" + inSeat);
            }
        }

        private bool HookSeatFsm(VehicleEntry entry, PlayMakerFSM fsm)
        {
            if (entry == null || fsm == null || fsm.Fsm == null)
            {
                return false;
            }

            List<FsmState> enterStates = FindStatesByNameContains(fsm, SeatEnterFilter);
            List<FsmState> exitStates = FindStatesByNameContains(fsm, SeatExitFilter);
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
                PlayMakerBridge.PrependAction(enterStates[i], new VehicleSeatAction(this, entry.Id, true));
            }
            for (int i = 0; i < exitStates.Count; i++)
            {
                PlayMakerBridge.PrependAction(exitStates[i], new VehicleSeatAction(this, entry.Id, false));
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

        private bool HookSeatEventTransitions(VehicleEntry entry, PlayMakerFSM fsm)
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
                PlayMakerBridge.PrependAction(state, new VehicleSeatEventAction(this, entry.Id, fsm));
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
            string stateName;
            string fsmName;
            if (!TryEvaluateSeatState(entry, out inSeat, out stateName, out fsmName))
            {
                return;
            }

            entry.LocalDriverFromFsm = inSeat;
            if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("VehicleSync: seat FSM active vehicle=" + entry.DebugPath +
                    " fsm=" + (fsmName ?? "<null>") +
                    " state=" + (stateName ?? "<null>") +
                    " inSeat=" + inSeat);
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
            string stateName;
            string fsmName;
            if (!TryEvaluateSeatState(entry, out inSeat, out stateName, out fsmName))
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
                DebugLog.Verbose("VehicleSync: seat FSM poll vehicle=" + entry.DebugPath +
                    " fsm=" + (fsmName ?? "<null>") +
                    " state=" + (stateName ?? "<null>") +
                    " inSeat=" + inSeat);
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

            List<PlayMakerFSM> fsms = entry.SeatFsms;
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

        private bool TryEvaluateSeatState(VehicleEntry entry, out bool inSeat, out string stateName, out string fsmName)
        {
            inSeat = false;
            stateName = null;
            fsmName = null;

            if (entry == null)
            {
                return false;
            }

            bool found = false;
            List<PlayMakerFSM> fsms = entry.SeatFsms;
            if (fsms != null && fsms.Count > 0)
            {
                for (int i = 0; i < fsms.Count; i++)
                {
                    bool fsmInSeat;
                    string fsmState;
                    string fsmLabel;
                    if (!TryGetSeatStateFromFsm(fsms[i], out fsmInSeat, out fsmState, out fsmLabel))
                    {
                        continue;
                    }

                    found = true;
                    if (fsmInSeat)
                    {
                        inSeat = true;
                        stateName = fsmState;
                        fsmName = fsmLabel;
                        return true;
                    }

                    if (stateName == null)
                    {
                        stateName = fsmState;
                        fsmName = fsmLabel;
                    }
                }
            }
            else
            {
                found = TryGetSeatStateFromFsm(entry.SeatFsm, out inSeat, out stateName, out fsmName);
            }

            return found;
        }

        private static bool TryGetSeatStateFromFsm(PlayMakerFSM fsm, out bool inSeat, out string stateName, out string fsmName)
        {
            inSeat = false;
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

            inSeat = inSeatFlag && !outSeatFlag;
            stateName = active.Name;
            fsmName = !string.IsNullOrEmpty(fsm.Fsm.Name) ? fsm.Fsm.Name : fsm.FsmName;
            return true;
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
            public float LastOwnershipLogTime;
            public Vector3 LastSentPosition;
            public Quaternion LastSentRotation;
            public uint LastRemoteSequence;
            public bool LastControlIsLocal;
            public bool ControlInitialized;
            public bool LocalDriverFromFsm;
            public string SeatFsmName;
            public PlayMakerFSM SeatFsm;
            public List<PlayMakerFSM> SeatFsms = new List<PlayMakerFSM>();
            public float LastSeatStatePollTime;
            public HashSet<int> SeatEventHooked = new HashSet<int>();
            public float LastDriverLogTime;
            public float ForceOwnershipUntilTime;
            public float LastSeatTraceTime;
            public Dictionary<int, string> SeatFsmStateNames;
            public Dictionary<int, string> SeatFsmEventNames;
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

        private sealed class VehicleSeatEventAction : FsmStateAction
        {
            private readonly VehicleSync _owner;
            private readonly uint _vehicleId;
            private readonly PlayMakerFSM _fsm;

            public VehicleSeatEventAction(VehicleSync owner, uint vehicleId, PlayMakerFSM fsm)
            {
                _owner = owner;
                _vehicleId = vehicleId;
                _fsm = fsm;
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
                    _owner.NotifySeatEvent(_vehicleId, true);
                }
                else if (NameContains(eventName, SeatExitEventFilter))
                {
                    _owner.NotifySeatEvent(_vehicleId, false);
                }
            }
        }
    }
}
