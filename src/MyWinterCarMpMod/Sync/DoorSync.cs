using System;
using System.Collections.Generic;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Net;
using MyWinterCarMpMod.Util;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    public sealed class DoorSync
    {
        private const float LocalHoldSeconds = 0.15f;
        private const float RemoteSuppressSeconds = 0.2f;
        private const float RemoteApplySeconds = 0.35f;

        private readonly Settings _settings;
        private readonly List<DoorEntry> _doors = new List<DoorEntry>();
        private readonly Dictionary<uint, DoorEntry> _doorLookup = new Dictionary<uint, DoorEntry>();
        private int _lastSceneIndex = int.MinValue;
        private string _lastSceneName = string.Empty;
        private float _nextSampleTime;
        private float _nextSummaryTime;
        private float _nextApplyLogTime;
        private bool _loggedNoDoors;

        public DoorSync(Settings settings)
        {
            _settings = settings;
        }

        public bool Enabled
        {
            get { return _settings != null && _settings.DoorSyncEnabled.Value; }
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
                if (_doors.Count > 0)
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
            ScanDoors();
        }

        public int CollectChanges(long unixTimeMs, float now, List<DoorStateData> buffer)
        {
            buffer.Clear();
            if (!Enabled || _doors.Count == 0)
            {
                return 0;
            }

            if (now < _nextSampleTime)
            {
                return 0;
            }

            float interval = 1f / _settings.GetDoorSendHz();
            _nextSampleTime = now + interval;
            float angleThreshold = _settings.GetDoorAngleThreshold();

            for (int i = 0; i < _doors.Count; i++)
            {
                DoorEntry entry = _doors[i];
                if (entry.Transform == null)
                {
                    continue;
                }

                if (now < entry.SuppressUntilTime)
                {
                    continue;
                }

                Quaternion current = entry.Transform.localRotation;
                if (Quaternion.Angle(entry.LastSentRotation, current) < angleThreshold)
                {
                    continue;
                }

                entry.LastSentRotation = current;
                entry.LastLocalChangeTime = now;

                DoorStateData state = new DoorStateData
                {
                    UnixTimeMs = unixTimeMs,
                    DoorId = entry.Id,
                    RotX = current.x,
                    RotY = current.y,
                    RotZ = current.z,
                    RotW = current.w
                };
                buffer.Add(state);
            }

            if (buffer.Count > 0 && now >= _nextSummaryTime)
            {
                DoorEntry entry = _doors[0];
                string doorName = entry.Transform != null ? entry.Transform.name : "<null>";
                string angleNote = entry.Hinge != null ? (" angle=" + entry.Hinge.angle.ToString("F1")) : string.Empty;
                DebugLog.Verbose("DoorSync: sending " + buffer.Count + " update(s). First=" + doorName + angleNote);
                _nextSummaryTime = now + 1f;
            }

            return buffer.Count;
        }

        public void Update(float now)
        {
            if (!Enabled || _doors.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _doors.Count; i++)
            {
                DoorEntry entry = _doors[i];
                if (entry.Transform == null)
                {
                    continue;
                }

                if (now < entry.RemoteApplyUntilTime && now - entry.LastLocalChangeTime > LocalHoldSeconds)
                {
                    ApplyRotation(entry, entry.LastAppliedRotation);
                }
            }
        }

        public void ApplyRemote(DoorStateData state)
        {
            if (!Enabled)
            {
                return;
            }

            DoorEntry entry;
            if (!_doorLookup.TryGetValue(state.DoorId, out entry))
            {
                if (!_loggedNoDoors)
                {
                    DebugLog.Warn("DoorSync missing door id " + state.DoorId + ". Re-scan on next scene change.");
                    _loggedNoDoors = true;
                }
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now - entry.LastLocalChangeTime < LocalHoldSeconds)
            {
                return;
            }

            if (state.UnixTimeMs <= entry.LastRemoteTimeMs)
            {
                return;
            }

            Quaternion target = new Quaternion(state.RotX, state.RotY, state.RotZ, state.RotW);
            float angleThreshold = _settings.GetDoorAngleThreshold();
            if (Quaternion.Angle(entry.LastAppliedRotation, target) < angleThreshold)
            {
                entry.LastRemoteTimeMs = state.UnixTimeMs;
                return;
            }

            entry.LastRemoteTimeMs = state.UnixTimeMs;
            entry.LastAppliedRotation = target;
            entry.LastSentRotation = target;
            entry.SuppressUntilTime = now + RemoteSuppressSeconds;
            entry.RemoteApplyUntilTime = now + RemoteApplySeconds;
            ApplyRotation(entry, target);

            if (now >= _nextApplyLogTime)
            {
                string doorName = entry.Transform != null ? entry.Transform.name : "<null>";
                DebugLog.Verbose("DoorSync: applied remote door " + doorName + " id=" + entry.Id);
                _nextApplyLogTime = now + 1f;
            }
        }

        public void Clear()
        {
            _doors.Clear();
            _doorLookup.Clear();
            _loggedNoDoors = false;
            _nextSummaryTime = 0f;
            _nextApplyLogTime = 0f;
        }

        private void ScanDoors()
        {
            Clear();

            HingeJoint[] hinges = UnityEngine.Object.FindObjectsOfType<HingeJoint>();
            if (hinges == null || hinges.Length == 0)
            {
                DebugLog.Verbose("DoorSync: no hinge joints found.");
                return;
            }

            string filter = _settings.GetDoorNameFilter();
            bool hasFilter = !string.IsNullOrEmpty(filter);
            for (int i = 0; i < hinges.Length; i++)
            {
                HingeJoint hinge = hinges[i];
                if (hinge == null || hinge.transform == null)
                {
                    continue;
                }

                if (hasFilter && !NameMatches(hinge.transform, filter))
                {
                    continue;
                }

                AddDoor(hinge);
            }

            if (_doors.Count == 0 && hasFilter)
            {
                DebugLog.Warn("DoorSync: no doors matched filter '" + filter + "'. Falling back to all hinges.");
                for (int i = 0; i < hinges.Length; i++)
                {
                    HingeJoint hinge = hinges[i];
                    if (hinge == null || hinge.transform == null)
                    {
                        continue;
                    }
                    AddDoor(hinge);
                }
            }

            DebugLog.Info("DoorSync: tracking " + _doors.Count + " door(s) in " + _lastSceneName + ".");
        }

        private void AddDoor(HingeJoint hinge)
        {
            if (hinge == null || hinge.transform == null)
            {
                return;
            }

            string path = BuildPath(hinge.transform);
            uint id = HashPath(path);
            if (_doorLookup.ContainsKey(id))
            {
                return;
            }

            Transform doorTransform = hinge.transform;
            Rigidbody doorBody = hinge.GetComponent<Rigidbody>();
            if (doorBody == null && hinge.connectedBody != null)
            {
                doorBody = hinge.connectedBody;
                doorTransform = doorBody.transform;
            }

            if (doorTransform == null)
            {
                return;
            }

            DoorEntry entry = new DoorEntry
            {
                Id = id,
                Path = path,
                Transform = doorTransform,
                Rigidbody = doorBody,
                Hinge = hinge,
                BaseLocalRotation = doorTransform.localRotation,
                LastSentRotation = doorTransform.localRotation,
                LastAppliedRotation = doorTransform.localRotation
            };

            _doors.Add(entry);
            _doorLookup.Add(id, entry);

            string hingeName = hinge.transform != null ? hinge.transform.name : "<null>";
            string doorName = doorTransform != null ? doorTransform.name : "<null>";
            DebugLog.Verbose("DoorSync: add door id=" + id + " hinge=" + hingeName + " door=" + doorName + " path=" + path);
        }

        private static void ApplyRotation(DoorEntry entry, Quaternion localRotation)
        {
            if (entry.Transform == null)
            {
                return;
            }

            if (entry.Rigidbody != null && !entry.Rigidbody.isKinematic)
            {
                Quaternion worldRotation = entry.Transform.parent != null
                    ? entry.Transform.parent.rotation * localRotation
                    : localRotation;
                entry.Rigidbody.angularVelocity = Vector3.zero;
                entry.Rigidbody.MoveRotation(worldRotation);
            }
            else
            {
                entry.Transform.localRotation = localRotation;
            }
        }

        private static bool NameMatches(Transform transform, string filter)
        {
            if (transform == null)
            {
                return false;
            }

            if (NameContains(transform.name, filter))
            {
                return true;
            }

            Transform current = transform.parent;
            int depth = 0;
            while (current != null && depth < 3)
            {
                if (NameContains(current.name, filter))
                {
                    return true;
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
                return false;
            }
            string[] tokens = SplitFilter(filter);
            for (int i = 0; i < tokens.Length; i++)
            {
                if (name.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static string[] SplitFilter(string filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                return new string[0];
            }

            string[] tokens = filter.Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                tokens[i] = tokens[i].Trim();
            }
            return tokens;
        }

        private static string BuildPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            Stack<string> parts = new Stack<string>();
            Transform current = transform;
            while (current != null)
            {
                string name = current.name + "#" + current.GetSiblingIndex();
                parts.Push(name);
                current = current.parent;
            }

            return string.Join("/", parts.ToArray());
        }

        private static uint HashPath(string path)
        {
            const uint offset = 2166136261u;
            const uint prime = 16777619u;
            uint hash = offset;
            if (path == null)
            {
                return hash;
            }

            for (int i = 0; i < path.Length; i++)
            {
                hash ^= (byte)path[i];
                hash *= prime;
            }
            return hash;
        }

        private sealed class DoorEntry
        {
            public uint Id;
            public string Path;
            public Transform Transform;
            public Rigidbody Rigidbody;
            public HingeJoint Hinge;
            public Quaternion BaseLocalRotation;
            public Quaternion LastSentRotation;
            public Quaternion LastAppliedRotation;
            public float LastLocalChangeTime;
            public float SuppressUntilTime;
            public float RemoteApplyUntilTime;
            public long LastRemoteTimeMs;
        }
    }
}
