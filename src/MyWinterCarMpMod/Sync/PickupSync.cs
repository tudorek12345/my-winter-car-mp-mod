using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Net;
using MyWinterCarMpMod.Util;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    public sealed class PickupSync
    {
        private const float HoldPollInterval = 0.12f;
        private const float RequestCooldownSeconds = 0.5f;
        private const byte FlagHeld = 1 << 0;
        private const byte FlagNoGravity = 1 << 1;
        private const byte FlagKinematic = 1 << 2;

        private static readonly string[] HeldBoolTokens = new[]
        {
            "held", "holding", "picked", "pickup", "grab", "gripped", "carry"
        };

        private static readonly string[] HoldStateTokens = new[]
        {
            "pick", "pickup", "grab", "hold", "carry"
        };

        private static readonly string[] DropStateTokens = new[]
        {
            "drop", "release", "put"
        };

        private readonly Settings _settings;
        private readonly List<PickupEntry> _pickups = new List<PickupEntry>();
        private readonly Dictionary<uint, PickupEntry> _pickupLookup = new Dictionary<uint, PickupEntry>();
        private readonly PlayerLocator _playerLocator = new PlayerLocator();
        private int _lastSceneIndex = int.MinValue;
        private string _lastSceneName = string.Empty;
        private float _nextSampleTime;
        private float _nextHoldPollTime;
        private bool _loggedNoPickups;

        public PickupSync(Settings settings)
        {
            _settings = settings;
        }

        public bool Enabled
        {
            get { return _settings != null && _settings.PickupSyncEnabled.Value; }
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
                if (_pickups.Count > 0)
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
            ScanPickups();
        }

        public void Update(float now)
        {
            if (!Enabled || _pickups.Count == 0)
            {
                return;
            }

            if (now < _nextHoldPollTime)
            {
                return;
            }
            _nextHoldPollTime = now + HoldPollInterval;

            Vector3 localPos;
            bool hasLocal = TryGetLocalPosition(out localPos);

            for (int i = 0; i < _pickups.Count; i++)
            {
                PickupEntry entry = _pickups[i];
                if (entry.Transform == null)
                {
                    continue;
                }

                bool held = DetermineHeld(entry, localPos, hasLocal);
                if (held != entry.IsHeld)
                {
                    entry.IsHeld = held;
                    entry.HeldStateDirty = true;
                }
            }
        }

        public int CollectChanges(long unixTimeMs, float now, List<PickupStateData> buffer, OwnerKind localOwner, bool includeUnowned)
        {
            buffer.Clear();
            if (!Enabled || _pickups.Count == 0)
            {
                return 0;
            }

            if (now < _nextSampleTime)
            {
                return 0;
            }

            float interval = 1f / _settings.GetPickupSendHz();
            _nextSampleTime = now + interval;
            float posThreshold = _settings.GetPickupPositionThreshold();
            float rotThreshold = _settings.GetPickupRotationThreshold();

            for (int i = 0; i < _pickups.Count; i++)
            {
                PickupEntry entry = _pickups[i];
                if (entry.Transform == null)
                {
                    continue;
                }

                if (!IsLocalAuthority(entry, localOwner, includeUnowned))
                {
                    continue;
                }

                Vector3 pos = entry.Transform.position;
                Quaternion rot = entry.Transform.rotation;
                bool moved = Vector3.Distance(entry.LastSentPosition, pos) >= posThreshold ||
                             Quaternion.Angle(entry.LastSentRotation, rot) >= rotThreshold;
                bool heldChanged = entry.HeldStateDirty || entry.LastSentHeld != entry.IsHeld;
                if (!moved && !heldChanged)
                {
                    continue;
                }

                entry.LastSentPosition = pos;
                entry.LastSentRotation = rot;
                entry.LastSentHeld = entry.IsHeld;
                entry.HeldStateDirty = false;

                PickupStateData state = new PickupStateData
                {
                    UnixTimeMs = unixTimeMs,
                    PickupId = entry.Id,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    RotX = rot.x,
                    RotY = rot.y,
                    RotZ = rot.z,
                    RotW = rot.w,
                    Flags = BuildFlags(entry)
                };
                buffer.Add(state);
            }

            return buffer.Count;
        }

        public void ApplyRemote(PickupStateData state, OwnerKind localOwner, bool includeUnowned)
        {
            if (!Enabled)
            {
                return;
            }

            PickupEntry entry;
            if (!_pickupLookup.TryGetValue(state.PickupId, out entry))
            {
                if (!_loggedNoPickups)
                {
                    DebugLog.Warn("PickupSync missing pickup id " + state.PickupId + ". Re-scan on next scene change.");
                    _loggedNoPickups = true;
                }
                return;
            }

            if (IsLocalAuthority(entry, localOwner, includeUnowned))
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

            if (entry.Body != null && !entry.Body.isKinematic)
            {
                entry.Body.MovePosition(pos);
                entry.Body.MoveRotation(rot);
            }
            else if (entry.Transform != null)
            {
                entry.Transform.position = pos;
                entry.Transform.rotation = rot;
            }

            ApplyFlags(entry, state.Flags);
        }

        public int CollectOwnershipRequests(OwnerKind localOwner, List<OwnershipRequestData> buffer)
        {
            buffer.Clear();
            if (!Enabled || _pickups.Count == 0 || localOwner != OwnerKind.Client)
            {
                return 0;
            }

            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < _pickups.Count; i++)
            {
                PickupEntry entry = _pickups[i];
                if (now - entry.LastOwnershipRequestTime < RequestCooldownSeconds)
                {
                    continue;
                }

                if (entry.IsHeld && entry.Owner != OwnerKind.Client)
                {
                    buffer.Add(new OwnershipRequestData
                    {
                        Kind = SyncObjectKind.Pickup,
                        ObjectId = entry.Id,
                        Action = OwnershipAction.Request
                    });
                    entry.LastOwnershipRequestTime = now;
                }
                else if (!entry.IsHeld && entry.Owner == OwnerKind.Client)
                {
                    buffer.Add(new OwnershipRequestData
                    {
                        Kind = SyncObjectKind.Pickup,
                        ObjectId = entry.Id,
                        Action = OwnershipAction.Release
                    });
                    entry.LastOwnershipRequestTime = now;
                }
            }

            return buffer.Count;
        }

        public int CollectOwnershipUpdates(OwnerKind localOwner, List<OwnershipUpdateData> buffer)
        {
            buffer.Clear();
            if (!Enabled || _pickups.Count == 0 || localOwner != OwnerKind.Host)
            {
                return 0;
            }

            for (int i = 0; i < _pickups.Count; i++)
            {
                PickupEntry entry = _pickups[i];
                if (entry.IsHeld && entry.Owner != OwnerKind.Host)
                {
                    entry.Owner = OwnerKind.Host;
                    buffer.Add(new OwnershipUpdateData
                    {
                        Kind = SyncObjectKind.Pickup,
                        ObjectId = entry.Id,
                        Owner = OwnerKind.Host
                    });
                }
                else if (!entry.IsHeld && entry.Owner == OwnerKind.Host)
                {
                    entry.Owner = OwnerKind.None;
                    buffer.Add(new OwnershipUpdateData
                    {
                        Kind = SyncObjectKind.Pickup,
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

            if (!Enabled || request.Kind != SyncObjectKind.Pickup)
            {
                return false;
            }

            PickupEntry entry;
            if (!_pickupLookup.TryGetValue(request.ObjectId, out entry))
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
                update = new OwnershipUpdateData
                {
                    Kind = SyncObjectKind.Pickup,
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
                update = new OwnershipUpdateData
                {
                    Kind = SyncObjectKind.Pickup,
                    ObjectId = entry.Id,
                    Owner = OwnerKind.None
                };
                return true;
            }

            return false;
        }

        public void ApplyOwnership(OwnershipUpdateData update)
        {
            if (!Enabled || update.Kind != SyncObjectKind.Pickup)
            {
                return;
            }

            PickupEntry entry;
            if (!_pickupLookup.TryGetValue(update.ObjectId, out entry))
            {
                return;
            }

            entry.Owner = update.Owner;
        }

        public OwnershipUpdateData[] BuildOwnershipSnapshot()
        {
            if (!Enabled || _pickups.Count == 0)
            {
                return new OwnershipUpdateData[0];
            }

            List<OwnershipUpdateData> list = new List<OwnershipUpdateData>(_pickups.Count);
            for (int i = 0; i < _pickups.Count; i++)
            {
                PickupEntry entry = _pickups[i];
                if (entry.Owner == OwnerKind.None)
                {
                    continue;
                }
                list.Add(new OwnershipUpdateData
                {
                    Kind = SyncObjectKind.Pickup,
                    ObjectId = entry.Id,
                    Owner = entry.Owner
                });
            }

            return list.ToArray();
        }

        public PickupStateData[] BuildSnapshot(long unixTimeMs, uint sessionId)
        {
            if (!Enabled || _pickups.Count == 0)
            {
                return new PickupStateData[0];
            }

            PickupStateData[] states = new PickupStateData[_pickups.Count];
            for (int i = 0; i < _pickups.Count; i++)
            {
                PickupEntry entry = _pickups[i];
                Vector3 pos = entry.Transform != null ? entry.Transform.position : Vector3.zero;
                Quaternion rot = entry.Transform != null ? entry.Transform.rotation : Quaternion.identity;
                states[i] = new PickupStateData
                {
                    SessionId = sessionId,
                    UnixTimeMs = unixTimeMs,
                    PickupId = entry.Id,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    RotX = rot.x,
                    RotY = rot.y,
                    RotZ = rot.z,
                    RotW = rot.w,
                    Flags = BuildFlags(entry)
                };
            }

            return states;
        }

        public void ResetOwnership()
        {
            for (int i = 0; i < _pickups.Count; i++)
            {
                _pickups[i].Owner = OwnerKind.None;
            }
        }

        public void Clear()
        {
            _pickups.Clear();
            _pickupLookup.Clear();
            _loggedNoPickups = false;
        }

        private void ScanPickups()
        {
            Clear();

            List<PickupCandidate> candidates = new List<PickupCandidate>();
            CollectByTag("ITEM", candidates);
            CollectByTag("PART", candidates);

            if (candidates.Count == 0)
            {
                DebugLog.Verbose("PickupSync: no pickupables found.");
                return;
            }

            string filter = _settings.GetPickupNameFilter();
            bool hasFilter = !string.IsNullOrEmpty(filter);
            if (hasFilter)
            {
                for (int i = candidates.Count - 1; i >= 0; i--)
                {
                    if (!NameContains(candidates[i].Root.name, filter))
                    {
                        candidates.RemoveAt(i);
                    }
                }
            }

            candidates.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            Dictionary<string, int> keyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < candidates.Count; i++)
            {
                PickupCandidate candidate = candidates[i];
                int count;
                if (!keyCounts.TryGetValue(candidate.Key, out count))
                {
                    count = 0;
                }
                count++;
                keyCounts[candidate.Key] = count;
                string uniqueKey = count == 1 ? candidate.Key : candidate.Key + "|dup" + count;
                AddPickup(candidate.Root, uniqueKey, candidate.DebugPath);
            }

            DebugLog.Info("PickupSync: tracking " + _pickups.Count + " pickup(s) in " + _lastSceneName + ".");
        }

        private void CollectByTag(string tag, List<PickupCandidate> candidates)
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
                Rigidbody body = obj.GetComponent<Rigidbody>();
                if (body == null)
                {
                    continue;
                }
                string key = ObjectKeyBuilder.BuildTypedKey(obj, "pickup");
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }
                string debugPath = ObjectKeyBuilder.BuildDebugPath(obj);
                candidates.Add(new PickupCandidate { Root = obj, Key = key, DebugPath = debugPath });
            }
        }

        private void AddPickup(GameObject root, string key, string debugPath)
        {
            if (root == null || string.IsNullOrEmpty(key))
            {
                return;
            }

            uint id = ObjectKeyBuilder.HashKey(key);
            if (_pickupLookup.ContainsKey(id))
            {
                return;
            }

            Rigidbody body = root.GetComponent<Rigidbody>();

            PickupEntry entry = new PickupEntry
            {
                Id = id,
                Key = key,
                DebugPath = debugPath,
                Transform = root.transform,
                Body = body,
                Owner = OwnerKind.None,
                LastSentPosition = root.transform.position,
                LastSentRotation = root.transform.rotation
            };

            AttachPlayMaker(root, entry);

            _pickups.Add(entry);
            _pickupLookup.Add(id, entry);
        }

        private static void AttachPlayMaker(GameObject root, PickupEntry entry)
        {
            if (root == null || entry == null)
            {
                return;
            }

            PlayMakerFSM[] fsms = root.GetComponentsInChildren<PlayMakerFSM>(true);
            if (fsms == null || fsms.Length == 0)
            {
                return;
            }

            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.Fsm == null)
                {
                    continue;
                }

                FsmBool heldBool = PlayMakerBridge.FindBoolByTokens(fsm, HeldBoolTokens);
                if (heldBool != null)
                {
                    entry.Fsm = fsm;
                    entry.HeldBool = heldBool;
                    entry.IsHeld = heldBool.Value;
                    return;
                }

                if (entry.Fsm == null)
                {
                    FsmState holdState = PlayMakerBridge.FindStateByNameContains(fsm, HoldStateTokens);
                    FsmState dropState = PlayMakerBridge.FindStateByNameContains(fsm, DropStateTokens);
                    if (holdState != null || dropState != null)
                    {
                        entry.Fsm = fsm;
                    }
                }
            }
        }

        private bool DetermineHeld(PickupEntry entry, Vector3 localPos, bool hasLocal)
        {
            if (entry == null)
            {
                return false;
            }

            if (entry.HeldBool != null)
            {
                return entry.HeldBool.Value;
            }

            Transform parent = entry.Transform != null ? entry.Transform.parent : null;
            if (IsLikelyHand(parent))
            {
                return true;
            }

            if (entry.Body != null)
            {
                if (!entry.Body.useGravity || entry.Body.isKinematic)
                {
                    if (hasLocal && entry.Transform != null)
                    {
                        float dist = Vector3.Distance(entry.Transform.position, localPos);
                        if (dist <= 1.5f)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool IsLikelyHand(Transform transform)
        {
            Transform current = transform;
            int depth = 0;
            while (current != null && depth < 4)
            {
                string name = current.name;
                if (!string.IsNullOrEmpty(name))
                {
                    if (name.IndexOf("hand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("hold", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("camera", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
                current = current.parent;
                depth++;
            }
            return false;
        }

        private static bool NameContains(string name, string filter)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(filter))
            {
                return true;
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

        private static bool IsLocalAuthority(PickupEntry entry, OwnerKind localOwner, bool includeUnowned)
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

        private static bool IsNewerSequence(uint seq, uint last)
        {
            if (seq == 0 || seq == last)
            {
                return false;
            }
            return seq > last;
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

        private static byte BuildFlags(PickupEntry entry)
        {
            byte flags = 0;
            if (entry.IsHeld)
            {
                flags |= FlagHeld;
            }

            if (entry.Body != null)
            {
                if (!entry.Body.useGravity)
                {
                    flags |= FlagNoGravity;
                }
                if (entry.Body.isKinematic)
                {
                    flags |= FlagKinematic;
                }
            }

            return flags;
        }

        private static void ApplyFlags(PickupEntry entry, byte flags)
        {
            if (entry == null || entry.Body == null)
            {
                return;
            }

            bool held = (flags & FlagHeld) != 0;
            bool noGravity = (flags & FlagNoGravity) != 0;

            if (held)
            {
                entry.Body.useGravity = false;
            }
            else if (!noGravity)
            {
                entry.Body.useGravity = true;
            }
        }

        private sealed class PickupEntry
        {
            public uint Id;
            public string Key;
            public string DebugPath;
            public Transform Transform;
            public Rigidbody Body;
            public PlayMakerFSM Fsm;
            public FsmBool HeldBool;
            public bool IsHeld;
            public bool LastSentHeld;
            public bool HeldStateDirty;
            public float LastOwnershipRequestTime;
            public uint LastRemoteSequence;
            public Vector3 LastSentPosition;
            public Quaternion LastSentRotation;
            public OwnerKind Owner;
        }

        private sealed class PickupCandidate
        {
            public GameObject Root;
            public string Key;
            public string DebugPath;
        }
    }
}
