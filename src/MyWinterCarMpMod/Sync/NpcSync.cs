using System;
using System.Collections.Generic;
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
        private const float RemoteLerpSpeed = 8f;
        private const float RemoteSnapDistance = 3f;
        private const float PeriodicRescanSeconds = 8f;

        private readonly Settings _settings;
        private readonly List<NpcEntry> _npcs = new List<NpcEntry>();
        private readonly Dictionary<uint, NpcEntry> _npcLookup = new Dictionary<uint, NpcEntry>();
        private readonly PlayerLocator _playerLocator = new PlayerLocator();
        private int _lastSceneIndex = int.MinValue;
        private string _lastSceneName = string.Empty;
        private float _nextSampleTime;
        private bool _loggedNoNpcs;
        private float _nextRescanTime;
        private int _rescanAttempts;
        private bool _loggedCandidateDump;

        public NpcSync(Settings settings)
        {
            _settings = settings;
        }

        public bool Enabled
        {
            get { return _settings != null && _settings.NpcSyncEnabled.Value; }
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
                    Flags = entry.IsVehicle ? FlagVehicle : (byte)0
                };
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
            if (!_npcLookup.TryGetValue(state.NpcId, out entry))
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
                return;
            }

            if (!IsNewerSequence(state.Sequence, entry.LastRemoteSequence))
            {
                return;
            }
            entry.LastRemoteSequence = state.Sequence;

            if (entry.IsVehicle)
            {
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
        }

        public NpcStateData[] BuildSnapshot(long unixTimeMs, uint sessionId)
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
                    Flags = entry.IsVehicle ? FlagVehicle : (byte)0
                };
            }

            return states;
        }

        public void Clear()
        {
            _npcs.Clear();
            _npcLookup.Clear();
            _loggedNoNpcs = false;
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

                int count;
                if (!keyCounts.TryGetValue(candidate.Key, out count))
                {
                    count = 0;
                }
                count++;
                keyCounts[candidate.Key] = count;
                string uniqueKey = count == 1 ? candidate.Key : candidate.Key + "|dup" + count;

                uint id = ObjectKeyBuilder.HashKey(uniqueKey);
                if (_npcLookup.ContainsKey(id))
                {
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

                if (!NameContains(root.name, filter))
                {
                    continue;
                }

                string key = ObjectKeyBuilder.BuildTypedKey(root, "npc");
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }
                string debugPath = ObjectKeyBuilder.BuildDebugPath(root);
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
            CarDynamics[] vehicles = UnityEngine.Object.FindObjectsOfType<CarDynamics>();
            if (vehicles == null || vehicles.Length == 0)
            {
                return;
            }

            for (int i = 0; i < vehicles.Length; i++)
            {
                CarDynamics car = vehicles[i];
                if (car == null || car.gameObject == null)
                {
                    continue;
                }

                GameObject root = car.gameObject;
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
            }
        }

        private void CollectArcadeVehicles(List<NpcCandidate> candidates, string filter)
        {
            ArcadeCarNoPhysics[] vehicles = UnityEngine.Object.FindObjectsOfType<ArcadeCarNoPhysics>();
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
