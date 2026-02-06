using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Net;
using MyWinterCarMpMod.Util;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    public sealed class NpcSync
    {
        private const byte FlagVehicle = 1 << 0;
        private const byte FlagBusNav = 1 << 1;
        private const float RemoteLerpSpeed = 8f;
        private const float RemoteSnapDistance = 3f;
        private const float PeriodicRescanSeconds = 8f;
        private const float MissingMatchVehicleMaxDistance = 20f;
        private const float MissingMatchNpcMaxDistance = 6f;
        private const string StableBusVehicleKey = "npc_vehicle:BUS";
        private const float BusRouteCacheRefreshSeconds = 10f;

        private readonly Settings _settings;
        private readonly List<NpcEntry> _npcs = new List<NpcEntry>();
        private readonly Dictionary<uint, NpcEntry> _npcLookup = new Dictionary<uint, NpcEntry>();
        private readonly HashSet<int> _trackedRootInstanceIds = new HashSet<int>();
        private readonly Dictionary<int, Transform> _busRouteRoots = new Dictionary<int, Transform>();
        private readonly PlayerLocator _playerLocator = new PlayerLocator();
        private readonly uint _stableBusNpcId;
        private int _lastSceneIndex = int.MinValue;
        private string _lastSceneName = string.Empty;
        private float _nextSampleTime;
        private bool _loggedNoNpcs;
        private float _nextRescanTime;
        private int _rescanAttempts;
        private bool _loggedCandidateDump;
        private float _nextBusApplyLogTime;
        private float _nextBusNavLogTime;
        private float _nextBusRouteCacheRefreshTime;

        public NpcSync(Settings settings)
        {
            _settings = settings;
            _stableBusNpcId = ObjectKeyBuilder.HashKey(StableBusVehicleKey);
        }

        public bool Enabled
        {
            get { return _settings != null && _settings.NpcSyncEnabled.Value; }
        }

        public int TrackedCount
        {
            get { return _npcs.Count; }
        }

        public int TrackedVehicleCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _npcs.Count; i++)
                {
                    NpcEntry entry = _npcs[i];
                    if (entry != null && entry.IsVehicle)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        public bool TryGetTrackedBus(out Transform busTransform, out string debugPath)
        {
            busTransform = null;
            debugPath = string.Empty;

            NpcEntry entry;
            if (!_npcLookup.TryGetValue(_stableBusNpcId, out entry) || entry == null || entry.Transform == null)
            {
                return false;
            }

            Transform t = entry.Transform;
            busTransform = t;

            string livePath = string.Empty;
            try
            {
                livePath = ObjectKeyBuilder.BuildDebugPath(t);
            }
            catch (Exception)
            {
                livePath = entry.DebugPath ?? string.Empty;
            }

            if (!string.IsNullOrEmpty(livePath))
            {
                entry.DebugPath = livePath;
            }

            debugPath = string.IsNullOrEmpty(livePath) ? (entry.DebugPath ?? string.Empty) : livePath;
            return true;
        }

        public void DevForceRescan(bool fullScan)
        {
            if (!Enabled)
            {
                return;
            }

            if (fullScan)
            {
                ScanNpcs();
            }
            else
            {
                ScanNpcsAdditive();
            }

            // Keep the normal periodic scan schedule after a manual scan.
            _nextRescanTime = Time.realtimeSinceStartup + PeriodicRescanSeconds;
        }

        public void DevDumpTracked(string reason)
        {
            if (!Enabled)
            {
                DebugLog.Warn("NpcSync dump skipped (disabled).");
                return;
            }

            int vehicles = TrackedVehicleCount;
            DebugLog.Warn("NpcSync dump: reason=" + (string.IsNullOrEmpty(reason) ? "<none>" : reason) +
                " total=" + _npcs.Count +
                " vehicles=" + vehicles +
                " mode=" + (_settings != null ? _settings.Mode.Value.ToString() : "<null>"));

            int logged = 0;
            for (int i = 0; i < _npcs.Count && logged < 50; i++)
            {
                NpcEntry entry = _npcs[i];
                if (entry == null || entry.Transform == null)
                {
                    continue;
                }

                Vector3 pos = entry.Transform.position;
                bool active = entry.Transform.gameObject != null && entry.Transform.gameObject.activeInHierarchy;
                Rigidbody rb = entry.Body;
                float speed = rb != null ? rb.velocity.magnitude : 0f;

                DebugLog.Verbose("NpcSync: tracked id=" + entry.Id +
                    " vehicle=" + entry.IsVehicle +
                    " active=" + active +
                    " remote=" + entry.HasRemoteState +
                    " speed=" + speed.ToString("F1") +
                    " pos=" + pos.x.ToString("F1") + "," + pos.y.ToString("F1") + "," + pos.z.ToString("F1") +
                    " path=" + (entry.DebugPath ?? string.Empty));
                logged++;
            }
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
                if (_npcs.Count > 0)
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
            _nextRescanTime = Time.realtimeSinceStartup + 5f;
            _rescanAttempts = 0;
            _loggedCandidateDump = false;
            ScanNpcs();
        }

        public void Update(float now)
        {
            if (!Enabled)
            {
                return;
            }

            if (now < _nextRescanTime)
            {
                return;
            }

            // Initial scene load: retry full scan a few times until we find something.
            if (_npcs.Count == 0)
            {
                if (_rescanAttempts >= 5)
                {
                    return;
                }

                _rescanAttempts++;
                _nextRescanTime = now + 5f + (_rescanAttempts * 2f);
                ScanNpcs();
                return;
            }

            // After we have *any* NPCs, keep doing additive scans to pick up traffic/road vehicles
            // that are enabled later (e.g. when driving to the highway).
            _nextRescanTime = now + PeriodicRescanSeconds;
            ScanNpcsAdditive();
        }

        public void FixedUpdate(float now, float deltaTime, OwnerKind localOwner)
        {
            if (!Enabled || _npcs.Count == 0)
            {
                return;
            }

            if (!UseHostAuthority())
            {
                return;
            }

            if (localOwner == OwnerKind.Host)
            {
                return;
            }

            for (int i = 0; i < _npcs.Count; i++)
            {
                NpcEntry entry = _npcs[i];
                if (entry == null || !entry.HasRemoteState)
                {
                    continue;
                }

                ApplyRemoteSmoothing(entry, deltaTime);
            }
        }

        public int CollectChanges(long unixTimeMs, float now, List<NpcStateData> buffer, OwnerKind localOwner)
        {
            buffer.Clear();
            if (!Enabled || _npcs.Count == 0)
            {
                return 0;
            }

            if (!UseHostAuthority())
            {
                return 0;
            }

            if (localOwner != OwnerKind.Host)
            {
                return 0;
            }

            if (now < _nextSampleTime)
            {
                return 0;
            }

            float interval = 1f / _settings.GetNpcSendHz();
            _nextSampleTime = now + interval;
            float posThreshold = _settings.GetNpcPositionThreshold();
            float rotThreshold = _settings.GetNpcRotationThreshold();

            for (int i = 0; i < _npcs.Count; i++)
            {
                NpcEntry entry = _npcs[i];
                if (entry == null || entry.Transform == null)
                {
                    continue;
                }

                Vector3 pos = entry.Transform.position;
                Quaternion rot = entry.Transform.rotation;
                if (Vector3.Distance(entry.LastSentPosition, pos) < posThreshold &&
                    Quaternion.Angle(entry.LastSentRotation, rot) < rotThreshold)
                {
                    continue;
                }

                entry.LastSentPosition = pos;
                entry.LastSentRotation = rot;

                Vector3 vel = entry.Body != null ? entry.Body.velocity : Vector3.zero;
                Vector3 angVel = entry.Body != null ? entry.Body.angularVelocity : Vector3.zero;

                NpcStateData state = new NpcStateData
                {
                    UnixTimeMs = unixTimeMs,
                    NpcId = entry.Id,
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
                    Flags = entry.IsVehicle ? FlagVehicle : (byte)0,
                    BusWaypoint = -1,
                    BusRoute = -1,
                    BusWaypointStart = -1,
                    BusWaypointEnd = -1
                };
                CaptureBusNavigationState(entry, ref state);
                buffer.Add(state);
            }

            return buffer.Count;
        }

        public void ApplyRemote(NpcStateData state, OwnerKind localOwner)
        {
            if (!Enabled)
            {
                return;
            }

            if (!UseHostAuthority())
            {
                return;
            }

            if (localOwner == OwnerKind.Host)
            {
                return;
            }

            NpcEntry entry;
            if (_npcLookup.TryGetValue(state.NpcId, out entry))
            {
                if (entry == null || entry.Transform == null || entry.Transform.gameObject == null)
                {
                    if (entry != null)
                    {
                        _npcs.Remove(entry);
                    }
                    _npcLookup.Remove(state.NpcId);
                    entry = null;

                    // Stale references are common for pooled/reparented traffic vehicles (especially BUS).
                    // Refresh candidates immediately so the missing-id matcher can rebind in the same tick.
                    ScanNpcsAdditive();
                }
            }

            if (entry == null && !_npcLookup.TryGetValue(state.NpcId, out entry))
            {
                Vector3 missingPos = new Vector3(state.PosX, state.PosY, state.PosZ);
                if (!TryMatchMissingNpc(state, missingPos, out entry))
                {
                    if (!_loggedNoNpcs)
                    {
                        DebugLog.Warn("NpcSync missing npc id " + state.NpcId + ". Re-scan on next scene change.");
                        _loggedNoNpcs = true;
                    }

                    // NPC vehicles can spawn later (traffic, bus). Schedule a near-term additive scan so
                    // we pick them up quickly instead of waiting for the periodic timer.
                    float now = Time.realtimeSinceStartup;
                    float desired = now + 0.5f;
                    if (_nextRescanTime <= 0f || _nextRescanTime > desired)
                    {
                        _nextRescanTime = desired;
                    }

                    // Do one immediate attempt to reduce the missing-id window after worldstate arrives.
                    ScanNpcsAdditive();
                    return;
                }

                _npcs.Add(entry);
                _npcLookup[state.NpcId] = entry;
            }

            if (!IsNewerSequence(state.Sequence, entry.LastRemoteSequence))
            {
                return;
            }
            entry.LastRemoteSequence = state.Sequence;

            if (entry.IsVehicle)
            {
                EnsureActiveOnClient(entry);
                DisableVehicleSimulationOnClient(entry);
            }

            Vector3 pos = new Vector3(state.PosX, state.PosY, state.PosZ);
            Quaternion rot = new Quaternion(state.RotX, state.RotY, state.RotZ, state.RotW);
            Vector3 vel = new Vector3(state.VelX, state.VelY, state.VelZ);
            Vector3 angVel = new Vector3(state.AngVelX, state.AngVelY, state.AngVelZ);

            if (!entry.HasRemoteState)
            {
                ApplyImmediate(entry, pos, rot, vel, angVel);
            }

            entry.RemoteTargetPosition = pos;
            entry.RemoteTargetRotation = rot;
            entry.RemoteTargetVelocity = vel;
            entry.RemoteTargetAngVelocity = angVel;
            entry.HasRemoteState = true;
            ApplyBusNavigationState(entry, state);

            if (_settings != null && _settings.VerboseLogging.Value && state.NpcId == _stableBusNpcId)
            {
                float now = Time.realtimeSinceStartup;
                if (now >= _nextBusApplyLogTime)
                {
                    _nextBusApplyLogTime = now + 1f;
                    DebugLog.Verbose("NpcSync: BUS remote apply seq=" + state.Sequence +
                        " pos=" + pos.x.ToString("F1") + "," + pos.y.ToString("F1") + "," + pos.z.ToString("F1") +
                        " path=" + (entry.DebugPath ?? string.Empty));
                }
            }
        }

        public NpcStateData[] BuildSnapshot(long unixTimeMs, uint sessionId, uint sequence)
        {
            if (!Enabled || _npcs.Count == 0 || !UseHostAuthority())
            {
                return new NpcStateData[0];
            }

            NpcStateData[] states = new NpcStateData[_npcs.Count];
            for (int i = 0; i < _npcs.Count; i++)
            {
                NpcEntry entry = _npcs[i];
                Vector3 pos = entry.Transform != null ? entry.Transform.position : Vector3.zero;
                Quaternion rot = entry.Transform != null ? entry.Transform.rotation : Quaternion.identity;
                Vector3 vel = entry.Body != null ? entry.Body.velocity : Vector3.zero;
                Vector3 angVel = entry.Body != null ? entry.Body.angularVelocity : Vector3.zero;
                states[i] = new NpcStateData
                {
                    SessionId = sessionId,
                    Sequence = sequence,
                    UnixTimeMs = unixTimeMs,
                    NpcId = entry.Id,
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
                    Flags = entry.IsVehicle ? FlagVehicle : (byte)0,
                    BusWaypoint = -1,
                    BusRoute = -1,
                    BusWaypointStart = -1,
                    BusWaypointEnd = -1
                };
                CaptureBusNavigationState(entry, ref states[i]);
            }

            return states;
        }

        public void Clear()
        {
            _npcs.Clear();
            _npcLookup.Clear();
            _trackedRootInstanceIds.Clear();
            _busRouteRoots.Clear();
            _loggedNoNpcs = false;
            _nextBusApplyLogTime = 0f;
            _nextBusNavLogTime = 0f;
            _nextBusRouteCacheRefreshTime = 0f;
        }

        private void ScanNpcs()
        {
            Clear();

            List<NpcCandidate> candidates = new List<NpcCandidate>();
            CollectNpcAnimators(candidates);
            CollectNpcVehicles(candidates);

            if (candidates.Count == 0)
            {
                DebugLog.Verbose("NpcSync: no NPCs found.");
                DumpNpcCandidates();
                return;
            }

            candidates.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            Dictionary<string, int> keyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < candidates.Count; i++)
            {
                NpcCandidate candidate = candidates[i];
                int count;
                if (!keyCounts.TryGetValue(candidate.Key, out count))
                {
                    count = 0;
                }
                count++;
                keyCounts[candidate.Key] = count;
                string uniqueKey = count == 1 ? candidate.Key : candidate.Key + "|dup" + count;
                AddNpc(candidate.Root, uniqueKey, candidate.DebugPath, candidate.IsVehicle);
            }

            DebugLog.Info("NpcSync: tracking " + _npcs.Count + " NPC(s) in " + _lastSceneName + ".");
        }

        private void ScanNpcsAdditive()
        {
            List<NpcCandidate> candidates = new List<NpcCandidate>();
            CollectNpcAnimators(candidates);
            CollectNpcVehicles(candidates);

            if (candidates.Count == 0)
            {
                return;
            }

            candidates.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            Dictionary<string, int> keyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            int added = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                NpcCandidate candidate = candidates[i];
                if (candidate == null || candidate.Root == null || string.IsNullOrEmpty(candidate.Key))
                {
                    continue;
                }

                int rootInstanceId = candidate.Root.GetInstanceID();
                if (_trackedRootInstanceIds.Contains(rootInstanceId))
                {
                    continue;
                }

                int count;
                if (!keyCounts.TryGetValue(candidate.Key, out count))
                {
                    count = 0;
                }
                count++;
                keyCounts[candidate.Key] = count;
                string uniqueKey = count == 1 ? candidate.Key : candidate.Key + "|dup" + count;

                uint id = ObjectKeyBuilder.HashKey(uniqueKey);
                NpcEntry existing;
                if (_npcLookup.TryGetValue(id, out existing))
                {
                    if (TryRebindCandidate(existing, candidate, uniqueKey, id))
                    {
                        added++;
                        if (_settings != null && _settings.VerboseLogging.Value)
                        {
                            DebugLog.Verbose("NpcSync: rebound " + (candidate.IsVehicle ? "vehicle" : "npc") + " id=" + id +
                                " key=" + uniqueKey + " path=" + candidate.DebugPath);
                        }
                    }
                    continue;
                }

                AddNpc(candidate.Root, uniqueKey, candidate.DebugPath, candidate.IsVehicle);
                added++;

                if (_settings != null && _settings.VerboseLogging.Value)
                {
                    DebugLog.Verbose("NpcSync: added " + (candidate.IsVehicle ? "vehicle" : "npc") + " id=" + id +
                        " key=" + uniqueKey + " path=" + candidate.DebugPath);
                }
            }

            if (added > 0)
            {
                DebugLog.Info("NpcSync: added " + added + " NPC(s). Total=" + _npcs.Count + ".");
            }
        }

        private void DumpNpcCandidates()
        {
            if (_settings == null || !_settings.VerboseLogging.Value || _loggedCandidateDump)
            {
                return;
            }

            _loggedCandidateDump = true;
            DebugLog.Verbose("NpcSync: candidate dump (nameFilter=" + _settings.GetNpcNameFilter() +
                ", vehicleFilter=" + _settings.GetNpcVehicleFilter() + ")");

            int logged = 0;
            Animator[] animators = UnityEngine.Object.FindObjectsOfType<Animator>();
            for (int i = 0; i < animators.Length && logged < 20; i++)
            {
                Animator animator = animators[i];
                if (animator == null || animator.gameObject == null)
                {
                    continue;
                }
                string name = animator.gameObject.name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                DebugLog.Verbose("NpcSync: animator candidate name=" + name);
                logged++;
            }

            logged = 0;
            CarDynamics[] vehicles = UnityEngine.Object.FindObjectsOfType<CarDynamics>();
            for (int i = 0; i < vehicles.Length && logged < 20; i++)
            {
                CarDynamics car = vehicles[i];
                if (car == null || car.gameObject == null)
                {
                    continue;
                }
                DebugLog.Verbose("NpcSync: vehicle candidate name=" + car.gameObject.name);
                logged++;
            }

            logged = 0;
            ArcadeCarNoPhysics[] arcade = UnityEngine.Object.FindObjectsOfType<ArcadeCarNoPhysics>();
            for (int i = 0; i < arcade.Length && logged < 20; i++)
            {
                ArcadeCarNoPhysics car = arcade[i];
                if (car == null || car.gameObject == null)
                {
                    continue;
                }
                DebugLog.Verbose("NpcSync: arcade vehicle candidate name=" + car.gameObject.name);
                logged++;
            }
        }

        private void CollectNpcAnimators(List<NpcCandidate> candidates)
        {
            if (candidates == null)
            {
                return;
            }

            Animator[] animators = UnityEngine.Object.FindObjectsOfType<Animator>();
            if (animators == null || animators.Length == 0)
            {
                return;
            }

            string filter = _settings.GetNpcNameFilter();
            for (int i = 0; i < animators.Length; i++)
            {
                Animator animator = animators[i];
                if (animator == null || animator.gameObject == null)
                {
                    continue;
                }

                GameObject root = animator.gameObject;
                if (IsLocalPlayerRoot(root.transform))
                {
                    continue;
                }

                if (root.name.IndexOf("MWC Remote Player", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                string debugPath = ObjectKeyBuilder.BuildDebugPath(root);
                if (!NameContains(root.name, filter) && !NameContains(debugPath, filter))
                {
                    continue;
                }

                string key = ObjectKeyBuilder.BuildTypedKey(root, "npc");
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }
                candidates.Add(new NpcCandidate { Root = root, Key = key, DebugPath = debugPath, IsVehicle = false });
            }
        }

        private void CollectNpcVehicles(List<NpcCandidate> candidates)
        {
            if (candidates == null)
            {
                return;
            }

            string filter = _settings.GetNpcVehicleFilter();
            if (string.IsNullOrEmpty(filter))
            {
                return;
            }

            CollectCarDynamicsVehicles(candidates, filter);
            CollectArcadeVehicles(candidates, filter);
        }

        private void CollectCarDynamicsVehicles(List<NpcCandidate> candidates, string filter)
        {
            // On clients we also scan inactive traffic/BUS vehicles so we can activate + drive them from remote state.
            // Use hideFlags filtering instead of GameObject.scene to stay compatible with older Unity versions.
            bool includeInactive = _settings != null && _settings.Mode.Value == Mode.Client;
            CarDynamics[] vehicles = includeInactive ? Resources.FindObjectsOfTypeAll<CarDynamics>() : UnityEngine.Object.FindObjectsOfType<CarDynamics>();
            if (vehicles == null || vehicles.Length == 0)
            {
                return;
            }
            GameObject bestBus = null;
            string bestBusPath = string.Empty;
            int bestBusScore = int.MinValue;

            for (int i = 0; i < vehicles.Length; i++)
            {
                CarDynamics car = vehicles[i];
                if (car == null || car.gameObject == null)
                {
                    continue;
                }

                GameObject root = car.gameObject;
                if (includeInactive && root.hideFlags != HideFlags.None)
                {
                    continue;
                }
                string name = root.name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (name.IndexOf("sorbet", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                string debugPath = ObjectKeyBuilder.BuildDebugPath(root);
                if (!NameContains(name, filter) && !NameContains(debugPath, filter))
                {
                    continue;
                }

                bool isBus = name.IndexOf("bus", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    debugPath.IndexOf("/BUS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    debugPath.IndexOf("BUS#", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isBus && debugPath.IndexOf("NPC_CARS", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // BUS is notorious for path changes (reparenting to spawn points) which breaks path-hash ids.
                    // Track it under a stable key and pick the "best" BUS root consistently.
                    int score = root.activeInHierarchy ? 1000 : 0;
                    if (debugPath.IndexOf("BusSpawn", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        score += 6000;
                    }
                    if (debugPath.IndexOf("BusSpawnPerajarvi", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        score += 2000;
                    }
                    if (score > bestBusScore)
                    {
                        bestBusScore = score;
                        bestBus = root;
                        bestBusPath = debugPath;
                    }
                    continue;
                }

                string key = ObjectKeyBuilder.BuildTypedKey(root, "npc_vehicle");
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }
                candidates.Add(new NpcCandidate { Root = root, Key = key, DebugPath = debugPath, IsVehicle = true });
            }

            if (bestBus != null)
            {
                candidates.Add(new NpcCandidate
                {
                    Root = bestBus,
                    Key = "npc_vehicle:BUS",
                    DebugPath = bestBusPath,
                    IsVehicle = true
                });
            }
        }

        private void CollectArcadeVehicles(List<NpcCandidate> candidates, string filter)
        {
            bool includeInactive = _settings != null && _settings.Mode.Value == Mode.Client;
            ArcadeCarNoPhysics[] vehicles = includeInactive ? Resources.FindObjectsOfTypeAll<ArcadeCarNoPhysics>() : UnityEngine.Object.FindObjectsOfType<ArcadeCarNoPhysics>();
            if (vehicles == null || vehicles.Length == 0)
            {
                return;
            }

            int matched = 0;
            for (int i = 0; i < vehicles.Length; i++)
            {
                ArcadeCarNoPhysics car = vehicles[i];
                if (car == null || car.gameObject == null)
                {
                    continue;
                }

                GameObject root = car.gameObject;
                if (includeInactive && root.hideFlags != HideFlags.None)
                {
                    continue;
                }
                string name = root.name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (name.IndexOf("sorbet", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                string debugPath = ObjectKeyBuilder.BuildDebugPath(root);
                if (!NameContains(name, filter) && !NameContains(debugPath, filter))
                {
                    continue;
                }

                string key = ObjectKeyBuilder.BuildTypedKey(root, "npc_vehicle");
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }
                candidates.Add(new NpcCandidate { Root = root, Key = key, DebugPath = debugPath, IsVehicle = true });
                matched++;
            }

            if (matched > 0 && _settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("NpcSync: matched " + matched + " ArcadeCarNoPhysics vehicles.");
            }
        }

        private bool UseHostAuthority()
        {
            return _settings == null || _settings.NpcAuthorityHostOnly.Value;
        }

        private void AddNpc(GameObject root, string key, string debugPath, bool isVehicle)
        {
            if (root == null || string.IsNullOrEmpty(key))
            {
                return;
            }

            uint id = ObjectKeyBuilder.HashKey(key);
            if (_npcLookup.ContainsKey(id))
            {
                return;
            }

            Rigidbody body = root.GetComponent<Rigidbody>();
            NpcEntry entry = new NpcEntry
            {
                Id = id,
                Key = key,
                DebugPath = debugPath,
                Transform = root.transform,
                Body = body,
                IsVehicle = isVehicle,
                LastSentPosition = root.transform.position,
                LastSentRotation = root.transform.rotation
            };

            _npcs.Add(entry);
            _npcLookup.Add(id, entry);
            _trackedRootInstanceIds.Add(root.GetInstanceID());

            // For host-authoritative traffic, disable local AI/physics immediately on clients so we don't
            // get divergent movement before the first remote NpcState arrives.
            if (entry.IsVehicle && _settings != null && _settings.Mode.Value == Mode.Client && UseHostAuthority())
            {
                DisableVehicleSimulationOnClient(entry);
            }
        }

        private bool TryMatchMissingNpc(NpcStateData state, Vector3 targetPos, out NpcEntry matched)
        {
            matched = null;

            if (_settings == null || _settings.Mode.Value != Mode.Client)
            {
                return false;
            }

            bool isVehicle = (state.Flags & FlagVehicle) != 0;
            float maxDist = isVehicle ? MissingMatchVehicleMaxDistance : MissingMatchNpcMaxDistance;

            List<NpcCandidate> candidates = new List<NpcCandidate>();
            if (isVehicle)
            {
                CollectNpcVehicles(candidates);
            }
            else
            {
                CollectNpcAnimators(candidates);
            }

            float bestDist = float.MaxValue;
            GameObject bestRoot = null;
            string bestPath = string.Empty;
            bool forceBusMatch = false;

            // BUS can jump large distances during route/startup and has multiple roots across scenes.
            // If host sends the stable BUS id, bind directly to the local BUS candidate by key.
            if (isVehicle && state.NpcId == _stableBusNpcId)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    NpcCandidate c = candidates[i];
                    if (c == null || c.Root == null || c.IsVehicle != isVehicle)
                    {
                        continue;
                    }

                    if (!string.Equals(c.Key, StableBusVehicleKey, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    bestRoot = c.Root;
                    bestPath = c.DebugPath;
                    bestDist = Vector3.Distance(c.Root.transform.position, targetPos);
                    forceBusMatch = true;
                    break;
                }
            }

            for (int i = 0; i < candidates.Count && !forceBusMatch; i++)
            {
                NpcCandidate c = candidates[i];
                if (c == null || c.Root == null || c.Root.transform == null || c.IsVehicle != isVehicle)
                {
                    continue;
                }

                float dist = Vector3.Distance(c.Root.transform.position, targetPos);
                if (dist > maxDist || dist >= bestDist)
                {
                    continue;
                }

                bestDist = dist;
                bestRoot = c.Root;
                bestPath = c.DebugPath;
            }

            if (bestRoot == null)
            {
                return false;
            }

            // If the best match is already tracked under a different local id, remap it to the host id.
            // This happens for dynamic NPC vehicle pools (notably the BUS) where hierarchy paths differ
            // between host/client or change at runtime due to reparenting.
            int bestInstanceId = bestRoot.GetInstanceID();
            if (_trackedRootInstanceIds.Contains(bestInstanceId))
            {
                NpcEntry existing = null;
                for (int i = 0; i < _npcs.Count; i++)
                {
                    NpcEntry e = _npcs[i];
                    if (e == null || e.Transform == null)
                    {
                        continue;
                    }
                    if (e.Transform.gameObject != null && e.Transform.gameObject.GetInstanceID() == bestInstanceId)
                    {
                        existing = e;
                        break;
                    }
                }

                if (existing != null && existing.IsVehicle == isVehicle)
                {
                    uint oldId = existing.Id;
                    uint newId = state.NpcId;
                    if (oldId != newId)
                    {
                        _npcLookup.Remove(oldId);
                        existing.Id = newId;
                        existing.Key = "npc_remap:" + (string.IsNullOrEmpty(bestPath) ? bestRoot.name : bestPath);
                        existing.DebugPath = string.IsNullOrEmpty(bestPath) ? ObjectKeyBuilder.BuildDebugPath(bestRoot) : bestPath;
                        existing.LastRemoteSequence = 0;
                        existing.HasRemoteState = false;
                        _npcLookup[newId] = existing;

                        if (_settings.VerboseLogging.Value)
                        {
                            DebugLog.Verbose("NpcSync: remapped npc id old=" + oldId + " new=" + newId +
                                " dist=" + bestDist.ToString("F1") +
                                " path=" + existing.DebugPath);
                        }
                    }

                    matched = existing;
                    return true;
                }
            }

            uint id = state.NpcId;
            if (_npcLookup.ContainsKey(id))
            {
                return false;
            }

            matched = new NpcEntry
            {
                Id = id,
                Key = "npc_remote_match:" + (string.IsNullOrEmpty(bestPath) ? bestRoot.name : bestPath),
                DebugPath = string.IsNullOrEmpty(bestPath) ? ObjectKeyBuilder.BuildDebugPath(bestRoot) : bestPath,
                Transform = bestRoot.transform,
                Body = bestRoot.GetComponent<Rigidbody>(),
                IsVehicle = isVehicle,
                LastSentPosition = bestRoot.transform.position,
                LastSentRotation = bestRoot.transform.rotation
            };

            _trackedRootInstanceIds.Add(bestRoot.GetInstanceID());
            if (_settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("NpcSync: matched missing remote npc id=" + id +
                    " dist=" + bestDist.ToString("F1") +
                    " path=" + matched.DebugPath);
            }

            return true;
        }

        private static void EnsureActiveOnClient(NpcEntry entry)
        {
            if (entry == null || entry.Transform == null)
            {
                return;
            }

            GameObject obj = entry.Transform.gameObject;
            if (obj != null && !obj.activeInHierarchy)
            {
                try
                {
                    obj.SetActive(true);
                }
                catch (Exception)
                {
                }
            }
        }

        private void CaptureBusNavigationState(NpcEntry entry, ref NpcStateData state)
        {
            if (entry == null || entry.Id != _stableBusNpcId)
            {
                return;
            }

            bool hasRefs = EnsureBusNavigationRefs(entry);

            state.Flags = (byte)(state.Flags | FlagBusNav);
            state.BusRoute = -1;
            state.BusWaypoint = -1;
            state.BusWaypointStart = -1;
            state.BusWaypointEnd = -1;

            if (hasRefs && entry.BusTargetSpeedVar != null)
            {
                state.BusTargetSpeed = entry.BusTargetSpeedVar.Value;
            }
            else if (entry.Body != null)
            {
                // Fallback: keep a meaningful target speed payload even when PlayMaker var names differ.
                state.BusTargetSpeed = entry.Body.velocity.magnitude;
            }

            if (hasRefs && entry.BusWaypointStartVar != null)
            {
                state.BusWaypointStart = entry.BusWaypointStartVar.Value;
            }

            if (hasRefs && entry.BusWaypointEndVar != null)
            {
                state.BusWaypointEnd = entry.BusWaypointEndVar.Value;
            }

            if (hasRefs && entry.BusRouteVar != null)
            {
                state.BusRoute = entry.BusRouteVar.Value;
            }

            if (hasRefs && entry.BusWaypointVar != null && entry.BusWaypointVar.Value != null)
            {
                GameObject waypointObj = entry.BusWaypointVar.Value;
                int waypoint;
                if (TryParseWaypointIndex(waypointObj.name, out waypoint))
                {
                    state.BusWaypoint = waypoint;
                }

                if (state.BusRoute < 0 && waypointObj.transform != null && waypointObj.transform.parent != null)
                {
                    state.BusRoute = RouteNameToId(waypointObj.transform.parent.name);
                }
            }

            if ((state.BusRoute < 0 || state.BusWaypoint < 0) && entry.Transform != null)
            {
                int guessedRoute;
                int guessedWaypoint;
                if (TryGuessNearestBusWaypoint(entry.Transform.position, out guessedRoute, out guessedWaypoint))
                {
                    if (state.BusRoute < 0)
                    {
                        state.BusRoute = guessedRoute;
                    }
                    if (state.BusWaypoint < 0)
                    {
                        state.BusWaypoint = guessedWaypoint;
                    }
                }
            }

            if (state.BusWaypointStart < 0 && state.BusWaypoint >= 0)
            {
                state.BusWaypointStart = state.BusWaypoint;
            }
            if (state.BusWaypointEnd < 0 && state.BusWaypoint >= 0)
            {
                state.BusWaypointEnd = state.BusWaypoint;
            }

            if (_settings != null && _settings.VerboseLogging.Value)
            {
                float now = Time.realtimeSinceStartup;
                if (now >= _nextBusNavLogTime)
                {
                    _nextBusNavLogTime = now + 1f;
                    DebugLog.Verbose("NpcSync: BUS nav sample speed=" + state.BusTargetSpeed.ToString("F2") +
                        " route=" + state.BusRoute +
                        " waypoint=" + state.BusWaypoint +
                        " start=" + state.BusWaypointStart +
                        " end=" + state.BusWaypointEnd +
                        " path=" + (entry.DebugPath ?? string.Empty));
                }
            }
        }

        private void ApplyBusNavigationState(NpcEntry entry, NpcStateData state)
        {
            if (entry == null || entry.Id != _stableBusNpcId)
            {
                return;
            }

            if ((state.Flags & FlagBusNav) == 0)
            {
                return;
            }

            if (!EnsureBusNavigationRefs(entry))
            {
                return;
            }

            if (entry.BusTargetSpeedVar != null)
            {
                entry.BusTargetSpeedVar.Value = state.BusTargetSpeed;
            }

            if (entry.BusWaypointStartVar != null && state.BusWaypointStart >= 0)
            {
                entry.BusWaypointStartVar.Value = state.BusWaypointStart;
            }

            if (entry.BusWaypointEndVar != null && state.BusWaypointEnd >= 0)
            {
                entry.BusWaypointEndVar.Value = state.BusWaypointEnd;
            }

            if (entry.BusRouteVar != null && state.BusRoute >= 0)
            {
                entry.BusRouteVar.Value = state.BusRoute;
            }

            if (entry.BusWaypointVar != null && state.BusWaypoint >= 0)
            {
                bool routeChanged = state.BusRoute != entry.LastAppliedBusRoute;
                bool waypointChanged = state.BusWaypoint != entry.LastAppliedBusWaypoint;
                if (routeChanged || waypointChanged || entry.BusWaypointVar.Value == null)
                {
                    GameObject waypointObj = ResolveBusWaypoint(state.BusRoute, state.BusWaypoint);
                    if (waypointObj != null)
                    {
                        entry.BusWaypointVar.Value = waypointObj;
                    }
                }
            }

            entry.LastAppliedBusRoute = state.BusRoute;
            entry.LastAppliedBusWaypoint = state.BusWaypoint;

            if (_settings != null && _settings.VerboseLogging.Value)
            {
                float now = Time.realtimeSinceStartup;
                if (now >= _nextBusNavLogTime)
                {
                    _nextBusNavLogTime = now + 1f;
                    DebugLog.Verbose("NpcSync: BUS nav apply speed=" + state.BusTargetSpeed.ToString("F2") +
                        " route=" + state.BusRoute +
                        " waypoint=" + state.BusWaypoint +
                        " start=" + state.BusWaypointStart +
                        " end=" + state.BusWaypointEnd +
                        " path=" + (entry.DebugPath ?? string.Empty));
                }
            }
        }

        private bool EnsureBusNavigationRefs(NpcEntry entry)
        {
            if (entry == null || entry.Transform == null || entry.Id != _stableBusNpcId)
            {
                return false;
            }

            if (entry.BusNavigationFsm != null && entry.BusNavigationFsm.Fsm != null)
            {
                return true;
            }

            entry.BusNavigationFsm = FindNavigationFsm(entry.Transform);
            entry.BusTargetSpeedVar = null;
            entry.BusWaypointVar = null;
            entry.BusRouteVar = null;
            entry.BusWaypointStartVar = null;
            entry.BusWaypointEndVar = null;

            if (entry.BusNavigationFsm == null)
            {
                return false;
            }

            entry.BusTargetSpeedVar = FindFsmFloatByTokens(entry.BusNavigationFsm, new[] { "targetspeed", "target speed", "speed" });
            entry.BusWaypointVar = FindFsmGameObjectByTokens(entry.BusNavigationFsm, new[] { "waypoint" });
            entry.BusRouteVar = FindFsmIntByTokens(entry.BusNavigationFsm, new[] { "route" });
            entry.BusWaypointStartVar = FindFsmIntByTokens(entry.BusNavigationFsm, new[] { "waypointstart", "waypoint start" });
            entry.BusWaypointEndVar = FindFsmIntByTokens(entry.BusNavigationFsm, new[] { "waypointend", "waypoint end" });

            return entry.BusTargetSpeedVar != null ||
                entry.BusWaypointVar != null ||
                entry.BusRouteVar != null ||
                entry.BusWaypointStartVar != null ||
                entry.BusWaypointEndVar != null;
        }

        private static PlayMakerFSM FindNavigationFsm(Transform root)
        {
            if (root == null)
            {
                return null;
            }

            PlayMakerFSM[] fsms = root.GetComponentsInChildren<PlayMakerFSM>(true);
            if (fsms == null || fsms.Length == 0)
            {
                return null;
            }

            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.Fsm == null)
                {
                    continue;
                }

                string fsmName = !string.IsNullOrEmpty(fsm.Fsm.Name) ? fsm.Fsm.Name : (fsm.FsmName ?? string.Empty);
                if (string.Equals(fsmName, "Navigation", StringComparison.OrdinalIgnoreCase))
                {
                    return fsm;
                }
            }

            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.Fsm == null)
                {
                    continue;
                }

                string fsmName = !string.IsNullOrEmpty(fsm.Fsm.Name) ? fsm.Fsm.Name : (fsm.FsmName ?? string.Empty);
                if (fsmName.IndexOf("navigation", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return fsm;
                }
            }

            return null;
        }

        private static FsmFloat FindFsmFloatByTokens(PlayMakerFSM fsm, string[] tokens)
        {
            if (fsm == null || fsm.Fsm == null || fsm.FsmVariables == null || tokens == null || tokens.Length == 0)
            {
                return null;
            }

            FsmFloat[] values = fsm.FsmVariables.FloatVariables;
            if (values == null)
            {
                return null;
            }

            for (int i = 0; i < values.Length; i++)
            {
                FsmFloat value = values[i];
                if (value == null || string.IsNullOrEmpty(value.Name))
                {
                    continue;
                }
                if (ContainsAnyToken(value.Name, tokens))
                {
                    return value;
                }
            }

            return null;
        }

        private static FsmInt FindFsmIntByTokens(PlayMakerFSM fsm, string[] tokens)
        {
            if (fsm == null || fsm.Fsm == null || fsm.FsmVariables == null || tokens == null || tokens.Length == 0)
            {
                return null;
            }

            FsmInt[] values = fsm.FsmVariables.IntVariables;
            if (values == null)
            {
                return null;
            }

            for (int i = 0; i < values.Length; i++)
            {
                FsmInt value = values[i];
                if (value == null || string.IsNullOrEmpty(value.Name))
                {
                    continue;
                }
                if (ContainsAnyToken(value.Name, tokens))
                {
                    return value;
                }
            }

            return null;
        }

        private static FsmGameObject FindFsmGameObjectByTokens(PlayMakerFSM fsm, string[] tokens)
        {
            if (fsm == null || fsm.Fsm == null || fsm.FsmVariables == null || tokens == null || tokens.Length == 0)
            {
                return null;
            }

            FsmGameObject[] values = fsm.FsmVariables.GameObjectVariables;
            if (values == null)
            {
                return null;
            }

            for (int i = 0; i < values.Length; i++)
            {
                FsmGameObject value = values[i];
                if (value == null || string.IsNullOrEmpty(value.Name))
                {
                    continue;
                }
                if (ContainsAnyToken(value.Name, tokens))
                {
                    return value;
                }
            }

            return null;
        }

        private static bool TryParseWaypointIndex(string name, out int waypoint)
        {
            waypoint = -1;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out waypoint))
            {
                return true;
            }

            string digits = string.Empty;
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if (char.IsDigit(ch))
                {
                    digits += ch;
                }
            }

            if (digits.Length == 0)
            {
                return false;
            }

            return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out waypoint);
        }

        private static int RouteNameToId(string routeName)
        {
            if (string.IsNullOrEmpty(routeName))
            {
                return -1;
            }

            string lower = routeName.ToLowerInvariant();
            if (lower.Contains("busroute") || (lower.Contains("bus") && lower.Contains("route")))
            {
                return 0;
            }
            if (lower.Contains("dirtroad"))
            {
                return 1;
            }
            if (lower.Contains("highway"))
            {
                return 2;
            }
            if (lower.Contains("homeroad"))
            {
                return 3;
            }
            if (lower.Contains("roadrace"))
            {
                return 4;
            }
            if (lower.Contains("trackfield"))
            {
                return 5;
            }
            if (lower.Contains("village"))
            {
                return 6;
            }
            return -1;
        }

        private static string RouteIdToName(int routeId)
        {
            switch (routeId)
            {
                case 0:
                    return "BusRoute";
                case 1:
                    return "DirtRoad";
                case 2:
                    return "Highway";
                case 3:
                    return "HomeRoad";
                case 4:
                    return "RoadRace";
                case 5:
                    return "Trackfield";
                case 6:
                    return "Village";
                default:
                    return "BusRoute";
            }
        }

        private GameObject ResolveBusWaypoint(int routeId, int waypoint)
        {
            if (waypoint < 0)
            {
                return null;
            }

            Transform routeRoot = ResolveBusRouteRoot(routeId);
            if (routeRoot == null)
            {
                return null;
            }

            string waypointName = waypoint.ToString(CultureInfo.InvariantCulture);
            Transform direct = routeRoot.Find(waypointName);
            if (direct != null)
            {
                return direct.gameObject;
            }

            for (int i = 0; i < routeRoot.childCount; i++)
            {
                Transform child = routeRoot.GetChild(i);
                int childIndex;
                if (child != null && TryParseWaypointIndex(child.name, out childIndex) && childIndex == waypoint)
                {
                    return child.gameObject;
                }
            }

            return null;
        }

        private bool TryGuessNearestBusWaypoint(Vector3 position, out int routeId, out int waypointId)
        {
            routeId = -1;
            waypointId = -1;

            float now = Time.realtimeSinceStartup;
            if (_busRouteRoots.Count == 0 || now >= _nextBusRouteCacheRefreshTime)
            {
                RefreshBusRouteCache(now);
            }

            if (_busRouteRoots.Count == 0)
            {
                return false;
            }

            float bestDistSq = float.MaxValue;
            foreach (KeyValuePair<int, Transform> kvp in _busRouteRoots)
            {
                Transform routeRoot = kvp.Value;
                if (routeRoot == null)
                {
                    continue;
                }

                for (int i = 0; i < routeRoot.childCount; i++)
                {
                    Transform child = routeRoot.GetChild(i);
                    if (child == null)
                    {
                        continue;
                    }

                    int childWaypoint;
                    if (!TryParseWaypointIndex(child.name, out childWaypoint))
                    {
                        continue;
                    }

                    float distSq = (child.position - position).sqrMagnitude;
                    if (distSq >= bestDistSq)
                    {
                        continue;
                    }

                    bestDistSq = distSq;
                    routeId = kvp.Key;
                    waypointId = childWaypoint;
                }
            }

            return routeId >= 0 && waypointId >= 0;
        }

        private Transform ResolveBusRouteRoot(int routeId)
        {
            Transform routeRoot;
            if (_busRouteRoots.TryGetValue(routeId, out routeRoot) && routeRoot != null)
            {
                return routeRoot;
            }

            float now = Time.realtimeSinceStartup;
            if (_busRouteRoots.Count == 0 || now >= _nextBusRouteCacheRefreshTime)
            {
                RefreshBusRouteCache(now);
                if (_busRouteRoots.TryGetValue(routeId, out routeRoot) && routeRoot != null)
                {
                    return routeRoot;
                }
            }

            if (_busRouteRoots.TryGetValue(0, out routeRoot) && routeRoot != null)
            {
                return routeRoot;
            }

            return null;
        }

        private void RefreshBusRouteCache(float now)
        {
            _busRouteRoots.Clear();
            _nextBusRouteCacheRefreshTime = now + BusRouteCacheRefreshSeconds;

            Transform[] transforms = Resources.FindObjectsOfTypeAll<Transform>();
            if (transforms == null || transforms.Length == 0)
            {
                return;
            }

            Transform bestRoutes = null;
            int bestScore = int.MinValue;
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform t = transforms[i];
                if (t == null || t.gameObject == null)
                {
                    continue;
                }

                if (!string.Equals(t.name, "Routes", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (t.hideFlags != HideFlags.None)
                {
                    continue;
                }

                int score = t.childCount;
                if (t.gameObject.activeInHierarchy)
                {
                    score += 10;
                }
                if (t.Find("BusRoute") != null)
                {
                    score += 100;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestRoutes = t;
                }
            }

            if (bestRoutes == null)
            {
                return;
            }

            for (int i = 0; i < bestRoutes.childCount; i++)
            {
                Transform child = bestRoutes.GetChild(i);
                if (child == null)
                {
                    continue;
                }

                int routeId = RouteNameToId(child.name);
                if (routeId < 0)
                {
                    continue;
                }

                if (!_busRouteRoots.ContainsKey(routeId))
                {
                    _busRouteRoots.Add(routeId, child);
                }
            }
        }

        private static bool ContainsAnyToken(string value, string[] tokens)
        {
            if (string.IsNullOrEmpty(value) || tokens == null || tokens.Length == 0)
            {
                return false;
            }

            string lower = value.ToLowerInvariant();
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (string.IsNullOrEmpty(token))
                {
                    continue;
                }
                if (lower.Contains(token))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsLocalPlayerRoot(Transform candidate)
        {
            if (candidate == null)
            {
                return false;
            }

            Transform body;
            Transform view;
            if (!_playerLocator.TryGetLocalTransforms(out body, out view))
            {
                return false;
            }

            if (body != null && (candidate == body || candidate.IsChildOf(body)))
            {
                return true;
            }
            if (view != null && (candidate == view || candidate.IsChildOf(view)))
            {
                return true;
            }

            return false;
        }

        private void ApplyImmediate(NpcEntry entry, Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel)
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

        private void ApplyRemoteSmoothing(NpcEntry entry, float deltaTime)
        {
            if (entry == null)
            {
                return;
            }

            if (entry.IsVehicle)
            {
                DisableVehicleSimulationOnClient(entry);
            }

            Vector3 targetPos = entry.RemoteTargetPosition;
            Quaternion targetRot = entry.RemoteTargetRotation;
            float lerp = Mathf.Clamp01(deltaTime * RemoteLerpSpeed);

            if (entry.Body != null)
            {
                if (_settings.NpcDisablePhysicsOnClient.Value && !entry.Body.isKinematic)
                {
                    entry.Body.isKinematic = true;
                    entry.Body.velocity = Vector3.zero;
                    entry.Body.angularVelocity = Vector3.zero;
                }

                if (Vector3.Distance(entry.Body.position, targetPos) > RemoteSnapDistance)
                {
                    entry.Body.position = targetPos;
                    entry.Body.rotation = targetRot;
                }
                else
                {
                    entry.Body.MovePosition(Vector3.Lerp(entry.Body.position, targetPos, lerp));
                    entry.Body.MoveRotation(Quaternion.Slerp(entry.Body.rotation, targetRot, lerp));
                }
            }
            else if (entry.Transform != null)
            {
                entry.Transform.position = Vector3.Lerp(entry.Transform.position, targetPos, lerp);
                entry.Transform.rotation = Quaternion.Slerp(entry.Transform.rotation, targetRot, lerp);
            }
        }

        private static bool NameContains(string name, string filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                return true;
            }
            if (string.IsNullOrEmpty(name))
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

        private static bool IsNewerSequence(uint seq, uint last)
        {
            if (seq == 0 || seq == last)
            {
                return false;
            }
            return seq > last;
        }

        private void DisableVehicleSimulationOnClient(NpcEntry entry)
        {
            if (entry == null || entry.LocalSimulationDisabled || !entry.IsVehicle || entry.Transform == null)
            {
                return;
            }

            // Arcade traffic uses ArcadeCarNoPhysics + AI scripts that directly move transforms.
            // Disable those on clients so host transforms win.
            ArcadeCarNoPhysics arcade = entry.Transform.GetComponent<ArcadeCarNoPhysics>();
            if (arcade == null)
            {
                arcade = entry.Transform.GetComponentInChildren<ArcadeCarNoPhysics>();
            }
            if (arcade != null && arcade.enabled)
            {
                arcade.enabled = false;
            }

            AICarController aiController = entry.Transform.GetComponent<AICarController>();
            if (aiController == null)
            {
                aiController = entry.Transform.GetComponentInChildren<AICarController>();
            }
            if (aiController != null && aiController.enabled)
            {
                aiController.enabled = false;
            }

            AI_OmaReitti aiRoute = entry.Transform.GetComponent<AI_OmaReitti>();
            if (aiRoute == null)
            {
                aiRoute = entry.Transform.GetComponentInChildren<AI_OmaReitti>();
            }
            if (aiRoute != null && aiRoute.enabled)
            {
                aiRoute.enabled = false;
            }

            CarDynamics dynamics = entry.Transform.GetComponent<CarDynamics>();
            if (dynamics == null)
            {
                dynamics = entry.Transform.GetComponentInChildren<CarDynamics>();
            }
            if (dynamics != null)
            {
                try
                {
                    // Stops local controller inputs (AI or player) from driving the CarDynamics vehicle.
                    dynamics.SetController("external");
                }
                catch (Exception)
                {
                }
            }

            int disabledFsms = 0;
            PlayMakerFSM[] fsms = entry.Transform.GetComponentsInChildren<PlayMakerFSM>(true);
            if (fsms != null)
            {
                for (int i = 0; i < fsms.Length; i++)
                {
                    PlayMakerFSM fsm = fsms[i];
                    if (fsm == null || !fsm.enabled)
                    {
                        continue;
                    }

                    string fsmName = (fsm.Fsm != null && !string.IsNullOrEmpty(fsm.Fsm.Name))
                        ? fsm.Fsm.Name
                        : (fsm.FsmName ?? string.Empty);
                    if (string.IsNullOrEmpty(fsmName))
                    {
                        continue;
                    }

                    // Keep bus Door/Start FSMs enabled so DoorSync can drive them from remote events.
                    if (string.Equals(fsmName, "Door", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fsmName, "Start", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (fsmName.IndexOf("Throttle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        fsmName.IndexOf("Navigation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        fsmName.IndexOf("Direction", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        fsm.enabled = false;
                        disabledFsms++;
                    }
                }
            }

            entry.LocalSimulationDisabled = true;

            if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("NpcSync: disabled local vehicle AI for " + entry.DebugPath +
                    " arcade=" + (arcade != null) +
                    " aiController=" + (aiController != null) +
                    " aiRoute=" + (aiRoute != null) +
                    " carDynamics=" + (dynamics != null) +
                    " disabledFsms=" + disabledFsms);
            }
        }

        private bool TryRebindCandidate(NpcEntry existing, NpcCandidate candidate, string uniqueKey, uint id)
        {
            if (existing == null || candidate == null || candidate.Root == null)
            {
                return false;
            }

            bool isBus = id == _stableBusNpcId;
            Transform candidateTransform = candidate.Root.transform;
            Transform existingTransform = existing.Transform;
            bool stale = existingTransform == null || existingTransform.gameObject == null;
            bool sameRoot = !stale && existingTransform.gameObject.GetInstanceID() == candidate.Root.GetInstanceID();

            if (!stale && (!isBus || sameRoot))
            {
                return false;
            }

            existing.Key = uniqueKey;
            existing.DebugPath = candidate.DebugPath;
            existing.Transform = candidateTransform;
            existing.Body = candidate.Root.GetComponent<Rigidbody>();
            existing.IsVehicle = candidate.IsVehicle;
            existing.LastSentPosition = candidateTransform.position;
            existing.LastSentRotation = candidateTransform.rotation;
            existing.LastRemoteSequence = 0;
            existing.HasRemoteState = false;
            existing.LocalSimulationDisabled = false;
            existing.BusNavigationFsm = null;
            existing.BusTargetSpeedVar = null;
            existing.BusWaypointVar = null;
            existing.BusRouteVar = null;
            existing.BusWaypointStartVar = null;
            existing.BusWaypointEndVar = null;
            existing.LastAppliedBusRoute = -1;
            existing.LastAppliedBusWaypoint = -1;
            _trackedRootInstanceIds.Add(candidate.Root.GetInstanceID());

            if (existing.IsVehicle && _settings != null && _settings.Mode.Value == Mode.Client && UseHostAuthority())
            {
                DisableVehicleSimulationOnClient(existing);
            }

            return true;
        }

        private sealed class NpcEntry
        {
            public uint Id;
            public string Key;
            public string DebugPath;
            public Transform Transform;
            public Rigidbody Body;
            public bool IsVehicle;
            public bool LocalSimulationDisabled;
            public Vector3 LastSentPosition;
            public Quaternion LastSentRotation;
            public uint LastRemoteSequence;
            public bool HasRemoteState;
            public Vector3 RemoteTargetPosition;
            public Quaternion RemoteTargetRotation = Quaternion.identity;
            public Vector3 RemoteTargetVelocity;
            public Vector3 RemoteTargetAngVelocity;
            public PlayMakerFSM BusNavigationFsm;
            public FsmFloat BusTargetSpeedVar;
            public FsmGameObject BusWaypointVar;
            public FsmInt BusRouteVar;
            public FsmInt BusWaypointStartVar;
            public FsmInt BusWaypointEndVar;
            public int LastAppliedBusRoute = -1;
            public int LastAppliedBusWaypoint = -1;
        }

        private sealed class NpcCandidate
        {
            public GameObject Root;
            public string Key;
            public string DebugPath;
            public bool IsVehicle;
        }
    }
}
