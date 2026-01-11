using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using BepInEx;
using HutongGames.PlayMaker;
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
        private static readonly string[] DoorTokens = new[] { "door", "ovi", "hatch", "boot", "lid", "gate" };
        private static readonly string[] DoorStateTokens = new[] { "Open", "Close" };

        private readonly Settings _settings;
        private readonly List<DoorEntry> _doors = new List<DoorEntry>();
        private readonly Dictionary<uint, DoorEntry> _doorLookup = new Dictionary<uint, DoorEntry>();
        private readonly List<DoorEventData> _doorEventQueue = new List<DoorEventData>(16);
        private int _lastSceneIndex = int.MinValue;
        private string _lastSceneName = string.Empty;
        private float _nextRotationSampleTime;
        private float _nextHingeSampleTime;
        private float _nextSummaryTime;
        private float _nextApplyLogTime;
        private float _nextEventLogTime;
        private float _nextHingeSendLogTime;
        private float _nextHingeApplyLogTime;
        private bool _loggedNoDoors;
        private bool _loggedMissingDoorEvent;
        private bool _dumpedDoors;
        private bool _pendingRescan;
        private float _nextRescanTime;
        private string _rescanReason = string.Empty;

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

            if (_pendingRescan && Time.realtimeSinceStartup >= _nextRescanTime)
            {
                _pendingRescan = false;
                DebugLog.Info("DoorSync: rescan triggered (" + _rescanReason + ").");
                _rescanReason = string.Empty;
                ScanDoors();
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

            if (now < _nextRotationSampleTime)
            {
                return 0;
            }

            float interval = 1f / _settings.GetDoorSendHz();
            _nextRotationSampleTime = now + interval;
            float angleThreshold = _settings.GetDoorAngleThreshold();

            for (int i = 0; i < _doors.Count; i++)
            {
                DoorEntry entry = _doors[i];
                if (entry.Transform == null)
                {
                    continue;
                }

                if (entry.SkipRotationSync)
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

        public int CollectHingeChanges(long unixTimeMs, float now, List<DoorHingeStateData> buffer)
        {
            buffer.Clear();
            if (!Enabled || _doors.Count == 0)
            {
                return 0;
            }

            if (now < _nextHingeSampleTime)
            {
                return 0;
            }

            float interval = 1f / _settings.GetDoorSendHz();
            _nextHingeSampleTime = now + interval;
            float angleThreshold = _settings.GetDoorAngleThreshold();

            for (int i = 0; i < _doors.Count; i++)
            {
                DoorEntry entry = _doors[i];
                if (!entry.IsVehicleDoor || entry.Hinge == null)
                {
                    continue;
                }

                if (now < entry.SuppressHingeUntilTime)
                {
                    continue;
                }

                float angle = entry.Hinge.angle;
                if (Mathf.Abs(Mathf.DeltaAngle(entry.LastSentHingeAngle, angle)) < angleThreshold)
                {
                    continue;
                }

                entry.LastSentHingeAngle = angle;
                entry.LastLocalHingeTime = now;

                DoorHingeStateData state = new DoorHingeStateData
                {
                    UnixTimeMs = unixTimeMs,
                    DoorId = entry.Id,
                    Angle = angle
                };
                buffer.Add(state);
            }

            if (buffer.Count > 0 && now >= _nextHingeSendLogTime)
            {
                DoorEntry entry;
                _doorLookup.TryGetValue(buffer[0].DoorId, out entry);
                string doorName = entry != null && entry.Transform != null ? entry.Transform.name : "<null>";
                DebugLog.Verbose("DoorSync: sending " + buffer.Count + " hinge update(s). First=" + doorName + " angle=" + buffer[0].Angle.ToString("F1"));
                _nextHingeSendLogTime = now + 1f;
            }

            return buffer.Count;
        }

        public int CollectEvents(List<DoorEventData> buffer)
        {
            buffer.Clear();
            if (!Enabled || _doorEventQueue.Count == 0)
            {
                return 0;
            }

            for (int i = 0; i < _doorEventQueue.Count; i++)
            {
                buffer.Add(_doorEventQueue[i]);
            }
            _doorEventQueue.Clear();
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

                if (!entry.SkipRotationSync && now < entry.RemoteApplyUntilTime && now - entry.LastLocalChangeTime > LocalHoldSeconds)
                {
                    ApplyRotation(entry, entry.LastAppliedRotation);
                }

                if (entry.DoorOpenBool != null && now >= entry.SuppressPlayMakerUntilTime)
                {
                    bool open = entry.DoorOpenBool.Value;
                    if (open != entry.LastDoorOpen)
                    {
                        entry.LastDoorOpen = open;
                        EnqueueDoorEvent(entry, open);
                    }
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
                    DumpDoorsOnce("missing door id " + state.DoorId);
                }
                RequestRescan("missing door id " + state.DoorId);
                return;
            }

            if (entry.Transform == null)
            {
                RequestRescan("missing transform for door id " + state.DoorId);
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now - entry.LastLocalChangeTime < LocalHoldSeconds)
            {
                return;
            }

            if (!IsNewerSequence(state.Sequence, entry.LastRemoteSequence))
            {
                return;
            }

            if (entry.SkipRotationSync)
            {
                entry.LastRemoteSequence = state.Sequence;
                entry.LastRemoteTimeMs = state.UnixTimeMs;
                return;
            }

            Quaternion target = new Quaternion(state.RotX, state.RotY, state.RotZ, state.RotW);
            float angleThreshold = _settings.GetDoorAngleThreshold();
            if (Quaternion.Angle(entry.LastAppliedRotation, target) < angleThreshold)
            {
                entry.LastRemoteSequence = state.Sequence;
                entry.LastRemoteTimeMs = state.UnixTimeMs;
                return;
            }

            entry.LastRemoteSequence = state.Sequence;
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

        public void ApplyRemoteHinge(DoorHingeStateData state)
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
                    DebugLog.Warn("DoorSync missing hinge door id " + state.DoorId + ". Re-scan on next scene change.");
                    _loggedNoDoors = true;
                    DumpDoorsOnce("missing hinge door id " + state.DoorId);
                }
                RequestRescan("missing hinge door id " + state.DoorId);
                return;
            }

            if (entry.Hinge == null)
            {
                RequestRescan("missing hinge for door id " + state.DoorId);
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now - entry.LastLocalHingeTime < LocalHoldSeconds)
            {
                return;
            }

            if (!IsNewerSequence(state.Sequence, entry.LastRemoteHingeSequence))
            {
                return;
            }

            float targetAngle = state.Angle;
            if (entry.HingeUseLimits)
            {
                targetAngle = Mathf.Clamp(targetAngle, entry.HingeMin, entry.HingeMax);
            }

            float angleThreshold = _settings.GetDoorAngleThreshold();
            if (Mathf.Abs(Mathf.DeltaAngle(entry.LastAppliedHingeAngle, targetAngle)) < angleThreshold)
            {
                entry.LastRemoteHingeSequence = state.Sequence;
                entry.LastRemoteHingeTimeMs = state.UnixTimeMs;
                return;
            }

            entry.LastRemoteHingeSequence = state.Sequence;
            entry.LastRemoteHingeTimeMs = state.UnixTimeMs;
            entry.LastAppliedHingeAngle = targetAngle;
            entry.LastSentHingeAngle = targetAngle;
            entry.SuppressHingeUntilTime = now + RemoteSuppressSeconds;
            ApplyHinge(entry, targetAngle);

            if (now >= _nextHingeApplyLogTime)
            {
                string doorName = entry.Transform != null ? entry.Transform.name : "<null>";
                DebugLog.Verbose("DoorSync: applied remote hinge " + doorName + " id=" + entry.Id + " angle=" + targetAngle.ToString("F1"));
                _nextHingeApplyLogTime = now + 1f;
            }
        }

        public void ApplyRemoteEvent(DoorEventData state)
        {
            if (!Enabled)
            {
                return;
            }

            DoorEntry entry;
            if (!_doorLookup.TryGetValue(state.DoorId, out entry))
            {
                if (!_loggedMissingDoorEvent)
                {
                    DebugLog.Warn("DoorSync missing door id for event " + state.DoorId + ". Re-scan on next scene change.");
                    _loggedMissingDoorEvent = true;
                    DumpDoorsOnce("missing door event id " + state.DoorId);
                }
                RequestRescan("missing door event id " + state.DoorId);
                return;
            }

            if (state.Sequence != 0 && !IsNewerSequence(state.Sequence, entry.LastRemoteEventSequence))
            {
                return;
            }
            if (state.Sequence == 0 && entry.LastRemoteEventSequence != 0)
            {
                return;
            }

            entry.LastRemoteEventSequence = state.Sequence;
            if (entry.Fsm == null || !entry.HasPlayMaker)
            {
                RequestRescan("missing PlayMaker for door id " + state.DoorId);
                return;
            }

            float now = Time.realtimeSinceStartup;
            entry.SuppressPlayMakerUntilTime = now + RemoteSuppressSeconds;
            entry.LastDoorOpen = state.Open != 0;

            string eventName = state.Open != 0 ? entry.MpOpenEventName : entry.MpCloseEventName;
            if (!string.IsNullOrEmpty(eventName))
            {
                entry.Fsm.SendEvent(eventName);
                if (_settings != null && _settings.VerboseLogging.Value && now >= _nextEventLogTime)
                {
                    DebugLog.Verbose("DoorSync: remote door event id=" + entry.Id +
                        " open=" + (state.Open != 0) +
                        " fsm=" + entry.Fsm.FsmName +
                        " path=" + entry.DebugPath);
                    _nextEventLogTime = now + 1f;
                }
            }
        }

        public DoorStateData[] BuildSnapshot(long unixTimeMs, uint sessionId)
        {
            if (!Enabled || _doors.Count == 0)
            {
                return new DoorStateData[0];
            }

            List<DoorStateData> states = new List<DoorStateData>(_doors.Count);
            for (int i = 0; i < _doors.Count; i++)
            {
                DoorEntry entry = _doors[i];
                if (entry.Transform == null || entry.SkipRotationSync)
                {
                    continue;
                }

                Quaternion rot = entry.Transform.localRotation;
                states.Add(new DoorStateData
                {
                    SessionId = sessionId,
                    Sequence = 1,
                    UnixTimeMs = unixTimeMs,
                    DoorId = entry.Id,
                    RotX = rot.x,
                    RotY = rot.y,
                    RotZ = rot.z,
                    RotW = rot.w
                });
            }

            return states.ToArray();
        }

        public DoorHingeStateData[] BuildHingeSnapshot(long unixTimeMs, uint sessionId)
        {
            if (!Enabled || _doors.Count == 0)
            {
                return new DoorHingeStateData[0];
            }

            List<DoorHingeStateData> states = new List<DoorHingeStateData>(_doors.Count);
            for (int i = 0; i < _doors.Count; i++)
            {
                DoorEntry entry = _doors[i];
                if (!entry.IsVehicleDoor || entry.Hinge == null)
                {
                    continue;
                }

                float angle = entry.Hinge.angle;
                states.Add(new DoorHingeStateData
                {
                    SessionId = sessionId,
                    Sequence = 1,
                    UnixTimeMs = unixTimeMs,
                    DoorId = entry.Id,
                    Angle = angle
                });
            }

            return states.ToArray();
        }

        public void Clear()
        {
            _doors.Clear();
            _doorLookup.Clear();
            _doorEventQueue.Clear();
            _loggedNoDoors = false;
            _loggedMissingDoorEvent = false;
            _dumpedDoors = false;
            _nextRotationSampleTime = 0f;
            _nextHingeSampleTime = 0f;
            _nextSummaryTime = 0f;
            _nextApplyLogTime = 0f;
            _nextEventLogTime = 0f;
            _nextHingeSendLogTime = 0f;
            _nextHingeApplyLogTime = 0f;
            _pendingRescan = false;
            _nextRescanTime = 0f;
            _rescanReason = string.Empty;
        }

        private void ScanDoors()
        {
            Clear();

            HingeJoint[] hinges = UnityEngine.Object.FindObjectsOfType<HingeJoint>();
            int totalHinges = hinges != null ? hinges.Length : 0;
            if (totalHinges == 0)
            {
                DebugLog.Verbose("DoorSync: no hinge joints found.");
                return;
            }

            string filter = _settings.GetDoorNameFilter();
            bool hasFilter = !string.IsNullOrEmpty(filter);
            List<DoorCandidate> candidates = new List<DoorCandidate>(totalHinges * 2);

            int filteredCount = 0;
            int unfilteredAdded = 0;
            if (hasFilter)
            {
                filteredCount = CollectCandidates(hinges, filter, true, candidates);
                if (filteredCount == 0)
                {
                    DebugLog.Warn("DoorSync: no doors matched filter '" + filter + "'. Relaxing filter to all hinges.");
                    unfilteredAdded = CollectCandidates(hinges, string.Empty, false, candidates);
                }
            }
            else
            {
                unfilteredAdded = CollectCandidates(hinges, string.Empty, false, candidates);
            }

            int playMakerAdded = CollectPlayMakerCandidates(filter, hasFilter, candidates);
            if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("DoorSync: hinges total=" + totalHinges +
                    " filteredMatch=" + filteredCount +
                    " addedUnfiltered=" + unfilteredAdded +
                    " playmakerAdded=" + playMakerAdded +
                    " candidates=" + candidates.Count);
            }

            candidates.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            Dictionary<string, int> keyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            int playMakerAttached = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                DoorCandidate candidate = candidates[i];
                int count;
                if (!keyCounts.TryGetValue(candidate.Key, out count))
                {
                    count = 0;
                }
                count++;
                keyCounts[candidate.Key] = count;
                candidate.UniqueKey = count == 1 ? candidate.Key : candidate.Key + "|dup" + count;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                DoorCandidate candidate = candidates[i];
                if (candidate.Hinge != null)
                {
                    AddDoor(candidate.Hinge, candidate.UniqueKey, candidate.DebugPath, ref playMakerAttached);
                }
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                DoorCandidate candidate = candidates[i];
                if (candidate.Hinge == null && candidate.DoorTransform != null)
                {
                    AddPlayMakerDoor(candidate.DoorTransform, candidate.Fsm, candidate.UniqueKey, candidate.DebugPath, ref playMakerAttached);
                }
            }

            DebugLog.Info("DoorSync: tracking " + _doors.Count + " door(s) in " + _lastSceneName + ". PlayMaker hooked=" + playMakerAttached + ".");
        }

        private bool AddDoor(HingeJoint hinge, string key, string debugPath, ref int playMakerAttached)
        {
            if (hinge == null || hinge.transform == null)
            {
                return false;
            }

            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            uint id = HashPath(key);
            if (_doorLookup.ContainsKey(id))
            {
                string originalKey = key;
                int suffix = 1;
                while (_doorLookup.ContainsKey(id) && suffix < 10)
                {
                    key = originalKey + "|h" + suffix;
                    id = HashPath(key);
                    suffix++;
                }
                if (_doorLookup.ContainsKey(id))
                {
                    DebugLog.Warn("DoorSync: duplicate door id for key " + originalKey + " (skipping).");
                    return false;
                }
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
                return false;
            }

            DoorEntry entry = new DoorEntry
            {
                Id = id,
                Key = key,
                DebugPath = debugPath,
                Transform = doorTransform,
                Rigidbody = doorBody,
                Hinge = hinge,
                BaseLocalRotation = doorTransform.localRotation,
                LastSentRotation = doorTransform.localRotation,
                LastAppliedRotation = doorTransform.localRotation
            };

            entry.IsVehicleDoor = IsVehicleDoor(doorTransform, hinge);
            if (entry.IsVehicleDoor && hinge != null)
            {
                ConfigureVehicleHinge(entry);
            }

            bool playMaker = AttachPlayMaker(doorTransform, entry);
            entry.HasPlayMaker = playMaker;
            UpdateRotationSyncPolicy(entry);

            _doors.Add(entry);
            _doorLookup.Add(id, entry);

            if (playMaker)
            {
                playMakerAttached++;
            }

            string hingeName = hinge.transform != null ? hinge.transform.name : "<null>";
            string doorName = doorTransform != null ? doorTransform.name : "<null>";
            DebugLog.Verbose("DoorSync: add door id=" + id + " hinge=" + hingeName + " door=" + doorName + " key=" + key + " path=" + debugPath + " playmaker=" + entry.HasPlayMaker);
            return true;
        }

        private void ConfigureVehicleHinge(DoorEntry entry)
        {
            if (entry == null || entry.Hinge == null)
            {
                return;
            }

            HingeJoint hinge = entry.Hinge;
            JointLimits limits = hinge.limits;
            entry.HingeUseLimits = hinge.useLimits;
            entry.HingeMin = limits.min;
            entry.HingeMax = limits.max;
            entry.HingeAxis = hinge.axis;
            entry.LastSentHingeAngle = hinge.angle;
            entry.LastAppliedHingeAngle = hinge.angle;

            if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("DoorSync: vehicle hinge door id=" + entry.Id +
                    " axis=" + FormatVec3(entry.HingeAxis) +
                    " limits=" + entry.HingeMin.ToString("F1") + "," + entry.HingeMax.ToString("F1") +
                    " useLimits=" + entry.HingeUseLimits +
                    " path=" + entry.DebugPath);
            }
        }

        private bool AddPlayMakerDoor(Transform doorTransform, PlayMakerFSM fsm, string key, string debugPath, ref int playMakerAttached)
        {
            if (doorTransform == null || string.IsNullOrEmpty(key))
            {
                return false;
            }

            DoorEntry existing = FindDoorByPath(debugPath);
            if (existing != null)
            {
                bool attached = fsm != null
                    ? AttachPlayMaker(fsm, existing, doorTransform.name)
                    : AttachPlayMaker(doorTransform, existing);

                if (attached)
                {
                    existing.HasPlayMaker = true;
                    UpdateRotationSyncPolicy(existing);
                    playMakerAttached++;
                    DebugLog.Verbose("DoorSync: playmaker attached to existing door id=" + existing.Id + " door=" + doorTransform.name + " path=" + debugPath);
                    return true;
                }
                return false;
            }

            uint id = HashPath(key);
            if (_doorLookup.ContainsKey(id))
            {
                string originalKey = key;
                int suffix = 1;
                while (_doorLookup.ContainsKey(id) && suffix < 10)
                {
                    key = originalKey + "|pm" + suffix;
                    id = HashPath(key);
                    suffix++;
                }
                if (_doorLookup.ContainsKey(id))
                {
                    DebugLog.Verbose("DoorSync: duplicate playmaker door id for key " + originalKey + " (skipping).");
                    return false;
                }
            }

            Rigidbody doorBody = doorTransform.GetComponent<Rigidbody>();
            DoorEntry entry = new DoorEntry
            {
                Id = id,
                Key = key,
                DebugPath = debugPath,
                Transform = doorTransform,
                Rigidbody = doorBody,
                Hinge = null,
                BaseLocalRotation = doorTransform.localRotation,
                LastSentRotation = doorTransform.localRotation,
                LastAppliedRotation = doorTransform.localRotation
            };

            bool playMaker = AttachPlayMaker(fsm, entry, doorTransform.name);
            if (!playMaker)
            {
                DebugLog.Verbose("DoorSync: playmaker door skipped (no FSM) " + doorTransform.name);
                return false;
            }

            entry.HasPlayMaker = playMaker;
            entry.IsVehicleDoor = IsVehicleDoor(doorTransform, null);
            UpdateRotationSyncPolicy(entry);
            _doors.Add(entry);
            _doorLookup.Add(id, entry);
            playMakerAttached++;
            DebugLog.Verbose("DoorSync: add playmaker door id=" + id + " door=" + doorTransform.name + " key=" + key + " path=" + debugPath);
            return true;
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

        private static void ApplyHinge(DoorEntry entry, float targetAngle)
        {
            if (entry == null || entry.Hinge == null)
            {
                return;
            }

            HingeJoint hinge = entry.Hinge;
            JointSpring spring = hinge.spring;
            if (spring.spring <= 0f)
            {
                spring.spring = 80f;
            }
            if (spring.damper <= 0f)
            {
                spring.damper = 8f;
            }
            spring.targetPosition = targetAngle;
            hinge.spring = spring;
            if (!hinge.useSpring)
            {
                hinge.useSpring = true;
            }
        }

        private void UpdateRotationSyncPolicy(DoorEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            bool skip = entry.IsVehicleDoor && (entry.Hinge != null || entry.HasPlayMaker || entry.Rigidbody != null);
            if (entry.SkipRotationSync == skip)
            {
                return;
            }

            entry.SkipRotationSync = skip;
            if (_settings != null && _settings.VerboseLogging.Value && entry.IsVehicleDoor)
            {
                if (skip)
                {
                    string reason = string.Empty;
                    if (entry.Hinge != null)
                    {
                        reason += "hinge ";
                    }
                    if (entry.HasPlayMaker)
                    {
                        reason += "playmaker ";
                    }
                    if (entry.Rigidbody != null)
                    {
                        reason += "rigidbody ";
                    }
                    reason = reason.Trim();
                    if (string.IsNullOrEmpty(reason))
                    {
                        reason = "unknown";
                    }
                    DebugLog.Verbose("DoorSync: vehicle door rotation disabled id=" + entry.Id +
                        " reason=" + reason +
                        " path=" + entry.DebugPath);
                }
                else
                {
                    DebugLog.Verbose("DoorSync: vehicle door rotation fallback enabled id=" + entry.Id +
                        " path=" + entry.DebugPath);
                }
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

        private static int CollectCandidates(HingeJoint[] hinges, string filter, bool useFilter, List<DoorCandidate> candidates)
        {
            if (hinges == null)
            {
                return 0;
            }

            int before = candidates.Count;
            for (int i = 0; i < hinges.Length; i++)
            {
                HingeJoint hinge = hinges[i];
                if (hinge == null || hinge.transform == null)
                {
                    continue;
                }

                if (useFilter && !NameMatches(hinge.transform, filter))
                {
                    continue;
                }

                string key = BuildDoorKey(hinge);
                string debugPath = BuildDebugPath(hinge.transform);
                candidates.Add(new DoorCandidate { Hinge = hinge, Key = key, DebugPath = debugPath });
            }
            return candidates.Count - before;
        }

        private static int CollectPlayMakerCandidates(string filter, bool useFilter, List<DoorCandidate> candidates)
        {
            PlayMakerFSM[] fsms = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>();
            if (fsms == null || fsms.Length == 0)
            {
                return 0;
            }

            int before = candidates.Count;
            HashSet<Transform> seen = new HashSet<Transform>();
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.gameObject == null)
                {
                    continue;
                }

                if (!IsDoorPlayMakerFsm(fsm))
                {
                    continue;
                }

                Transform doorTransform = ResolveDoorTransform(fsm.gameObject.transform);
                if (doorTransform == null || seen.Contains(doorTransform))
                {
                    continue;
                }

                if (useFilter && !NameMatches(doorTransform, filter))
                {
                    continue;
                }

                seen.Add(doorTransform);
                string key = BuildPlayMakerDoorKey(doorTransform);
                string debugPath = BuildDebugPath(doorTransform);
                candidates.Add(new DoorCandidate { Hinge = null, DoorTransform = doorTransform, Fsm = fsm, Key = key, DebugPath = debugPath });
            }

            return candidates.Count - before;
        }

        private static bool IsDoorPlayMakerFsm(PlayMakerFSM fsm)
        {
            if (fsm == null || fsm.Fsm == null)
            {
                return false;
            }

            string fsmName = fsm.FsmName;
            if (fsm.Fsm != null && !string.IsNullOrEmpty(fsm.Fsm.Name))
            {
                fsmName = fsm.Fsm.Name;
            }

            bool hasSignals = PlayMakerBridge.HasAnyEvent(fsm, new[] { "OPEN", "CLOSE", "OPENDOOR", "CLOSEDOOR" }) ||
                PlayMakerBridge.FindStateByNameContains(fsm, DoorStateTokens) != null;
            if (!hasSignals)
            {
                return false;
            }

            if (string.Equals(fsmName, "Use", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ContainsAnyToken(fsmName, DoorTokens) || ContainsAnyToken(fsm.gameObject.name, DoorTokens))
            {
                return true;
            }

            Transform current = fsm.transform;
            int depth = 0;
            while (current != null && depth < 4)
            {
                if (LooksLikeDoorRoot(current.name))
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

        private bool AttachPlayMaker(Transform doorTransform, DoorEntry entry)
        {
            if (entry == null || doorTransform == null || _settings == null || !_settings.DoorPlayMakerEnabled.Value)
            {
                return false;
            }

            PlayMakerFSM fsm = PlayMakerBridge.FindFsmByName(doorTransform.gameObject, "Use");
            if (fsm == null)
            {
                fsm = PlayMakerBridge.FindFsmWithStates(doorTransform.gameObject, new[] { "Open door", "Close door" });
            }
            if (fsm == null)
            {
                DebugLog.Verbose("DoorSync: PlayMaker FSM not found for door " + doorTransform.name);
                return false;
            }
            return AttachPlayMaker(fsm, entry, doorTransform.name);
        }

        private bool AttachPlayMaker(PlayMakerFSM fsm, DoorEntry entry, string doorName)
        {
            if (entry == null || fsm == null || fsm.Fsm == null || _settings == null || !_settings.DoorPlayMakerEnabled.Value)
            {
                return false;
            }

            if (entry.Fsm == fsm && entry.HasPlayMaker)
            {
                return true;
            }

            FsmState openState = fsm.Fsm.GetState("Open door") ?? PlayMakerBridge.FindStateByNameContains(fsm, DoorStateTokens);
            FsmState closeState = fsm.Fsm.GetState("Close door") ?? PlayMakerBridge.FindStateByNameContains(fsm, DoorStateTokens);
            if (openState == null || closeState == null)
            {
                DebugLog.Verbose("DoorSync: PlayMaker states missing for door " + (doorName ?? "<null>"));
                return false;
            }

            string mpOpenEvent = "MWC_MP_OPEN";
            string mpCloseEvent = "MWC_MP_CLOSE";

            FsmEvent mpOpen = PlayMakerBridge.GetOrCreateEvent(fsm, mpOpenEvent);
            FsmEvent mpClose = PlayMakerBridge.GetOrCreateEvent(fsm, mpCloseEvent);
            if (mpOpen != null && mpClose != null)
            {
                PlayMakerBridge.AddGlobalTransition(fsm, mpOpen, openState.Name);
                PlayMakerBridge.AddGlobalTransition(fsm, mpClose, closeState.Name);

                string openEventName = FindEventName(fsm, PlayMakerBridge.GetDefaultDoorOpenEventName(), new[] { "open" });
                string closeEventName = FindEventName(fsm, PlayMakerBridge.GetDefaultDoorCloseEventName(), new[] { "close" });
                string expectedOpenEvent = fsm.Fsm.HasEvent(openEventName) ? openEventName : null;
                string expectedCloseEvent = fsm.Fsm.HasEvent(closeEventName) ? closeEventName : null;
                PlayMakerBridge.PrependAction(openState, new DoorPlayMakerAction(this, entry.Id, true, expectedOpenEvent));
                PlayMakerBridge.PrependAction(closeState, new DoorPlayMakerAction(this, entry.Id, false, expectedCloseEvent));
            }

            entry.Fsm = fsm;
            entry.HasPlayMaker = true;
            entry.MpOpenEventName = mpOpenEvent;
            entry.MpCloseEventName = mpCloseEvent;
            entry.OpenEventName = FindEventName(fsm, PlayMakerBridge.GetDefaultDoorOpenEventName(), new[] { "open" });
            entry.CloseEventName = FindEventName(fsm, PlayMakerBridge.GetDefaultDoorCloseEventName(), new[] { "close" });
            entry.DoorOpenBool = PlayMakerBridge.FindBool(fsm, "DoorOpen") ?? PlayMakerBridge.FindBoolByTokens(fsm, new[] { "dooropen", "isopen" });
            if (entry.DoorOpenBool != null)
            {
                entry.LastDoorOpen = entry.DoorOpenBool.Value;
            }
            if (entry.IsVehicleDoor)
            {
                entry.SkipRotationSync = true;
            }
            DebugLog.Verbose("DoorSync: PlayMaker hook attached to " + (doorName ?? "<null>") + " open=" + entry.OpenEventName + " close=" + entry.CloseEventName);
            return true;
        }

        private static string FindEventName(PlayMakerFSM fsm, string fallback, string[] tokens)
        {
            if (fsm == null || fsm.Fsm == null || tokens == null || tokens.Length == 0)
            {
                return fallback;
            }

            FsmEvent[] events = fsm.Fsm.Events;
            if (events == null)
            {
                return fallback;
            }

            for (int i = 0; i < events.Length; i++)
            {
                FsmEvent ev = events[i];
                if (ev == null || string.IsNullOrEmpty(ev.Name))
                {
                    continue;
                }
                for (int t = 0; t < tokens.Length; t++)
                {
                    if (ev.Name.IndexOf(tokens[t], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return ev.Name;
                    }
                }
            }

            return fallback;
        }

        private void EnqueueDoorEvent(DoorEntry entry, bool open)
        {
            if (entry == null)
            {
                return;
            }

            DoorEventData data = new DoorEventData
            {
                DoorId = entry.Id,
                Open = open ? (byte)1 : (byte)0
            };
            _doorEventQueue.Add(data);
        }

        internal void NotifyDoorPlayMaker(uint doorId, bool open, string triggerEvent)
        {
            DoorEntry entry;
            if (!_doorLookup.TryGetValue(doorId, out entry))
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now < entry.SuppressPlayMakerUntilTime)
            {
                return;
            }

            if (!string.IsNullOrEmpty(triggerEvent))
            {
                if (string.Equals(triggerEvent, entry.MpOpenEventName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(triggerEvent, entry.MpCloseEventName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            entry.LastDoorOpen = open;
            entry.LastLocalChangeTime = now;
            EnqueueDoorEvent(entry, open);
            if (_settings != null && _settings.VerboseLogging.Value && now >= _nextEventLogTime)
            {
                DebugLog.Verbose("DoorSync: local door event id=" + entry.Id +
                    " open=" + open +
                    " trigger=" + (triggerEvent ?? "<null>") +
                    " path=" + entry.DebugPath);
                _nextEventLogTime = now + 1f;
            }
        }

        private static string BuildDoorKey(HingeJoint hinge)
        {
            if (hinge == null || hinge.transform == null)
            {
                return string.Empty;
            }

            string path = BuildDebugPath(hinge.transform);
            Vector3 axis = hinge.axis;
            Vector3 anchor = hinge.anchor;
            return string.Concat(
                path,
                "|a:", FormatVec3(axis),
                "|h:", FormatVec3(anchor));
        }

        private static string BuildPlayMakerDoorKey(Transform transform)
        {
            return BuildDebugPath(transform) + "|pm";
        }

        private DoorEntry FindDoorByPath(string debugPath)
        {
            if (string.IsNullOrEmpty(debugPath))
            {
                return null;
            }

            for (int i = 0; i < _doors.Count; i++)
            {
                DoorEntry entry = _doors[i];
                if (entry != null && string.Equals(entry.DebugPath, debugPath, StringComparison.Ordinal))
                {
                    return entry;
                }
            }
            return null;
        }

        private void RequestRescan(string reason)
        {
            if (_pendingRescan)
            {
                return;
            }

            _pendingRescan = true;
            _rescanReason = string.IsNullOrEmpty(reason) ? "unknown" : reason;
            _nextRescanTime = Time.realtimeSinceStartup + 1f;
            DebugLog.Warn("DoorSync: rescan scheduled (" + _rescanReason + ").");
        }

        private static Transform ResolveDoorTransform(Transform fsmTransform)
        {
            if (fsmTransform == null)
            {
                return null;
            }

            Transform current = fsmTransform;
            Transform fallback = null;
            int depth = 0;
            while (current != null && depth < 8)
            {
                if (current.GetComponent<HingeJoint>() != null)
                {
                    return current;
                }

                if (LooksLikeDoorRoot(current.name) && fallback == null)
                {
                    fallback = current;
                }

                current = current.parent;
                depth++;
            }

            return fallback ?? fsmTransform;
        }

        private static bool LooksLikeDoorRoot(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            string lower = name.ToLowerInvariant();
            if (lower.Contains("handle") || lower.Contains("coll") || lower.Contains("trigger") || lower.Contains("mesh") || lower.Contains("playercoll"))
            {
                return false;
            }

            return lower.Contains("pivot") || lower.Contains("door") || lower.Contains("hatch") || lower.Contains("boot") || lower.Contains("lid");
        }

        private static bool IsVehicleDoor(Transform doorTransform, HingeJoint hinge)
        {
            if (HasVehicleComponent(doorTransform))
            {
                return true;
            }

            if (hinge != null && hinge.connectedBody != null)
            {
                return HasVehicleComponent(hinge.connectedBody.transform);
            }

            return false;
        }

        private static bool HasVehicleComponent(Transform transform)
        {
            if (transform == null)
            {
                return false;
            }

            if (transform.GetComponentInParent<CarDynamics>() != null)
            {
                return true;
            }

            Transform root = transform.root;
            if (root != null && root.gameObject != null)
            {
                try
                {
                    if (root.CompareTag("Car") || root.CompareTag("Truck") || root.CompareTag("Boat"))
                    {
                        return true;
                    }
                }
                catch (UnityException)
                {
                }
            }

            return false;
        }

        private static bool ContainsAnyToken(string value, string[] tokens)
        {
            if (string.IsNullOrEmpty(value) || tokens == null || tokens.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (string.IsNullOrEmpty(token))
                {
                    continue;
                }
                if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static string BuildDebugPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            Stack<string> parts = new Stack<string>();
            Transform current = transform;
            while (current != null)
            {
                parts.Push(current.name + "#" + current.GetSiblingIndex());
                current = current.parent;
            }

            return string.Join("/", parts.ToArray());
        }

        private static string FormatVec3(Vector3 value)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F3},{1:F3},{2:F3}", value.x, value.y, value.z);
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

        private void DumpDoorsOnce(string reason)
        {
            if (_dumpedDoors)
            {
                return;
            }
            _dumpedDoors = true;

            try
            {
                string root = Paths.BepInExRootPath;
                string scene = string.IsNullOrEmpty(_lastSceneName) ? "UnknownScene" : _lastSceneName.Replace(" ", string.Empty);
                string fileName = "DoorDump_" + scene + "_" + Process.GetCurrentProcess().Id + ".log";
                string path = Path.Combine(root, fileName);
                using (StreamWriter writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false)))
                {
                    writer.WriteLine("DoorSync dump: " + reason);
                    writer.WriteLine("Scene: " + _lastSceneName + " Index=" + _lastSceneIndex);
                    writer.WriteLine("Count: " + _doors.Count);
                    for (int i = 0; i < _doors.Count; i++)
                    {
                        DoorEntry entry = _doors[i];
                        string name = entry.Transform != null ? entry.Transform.name : "<null>";
                        string pos = entry.Transform != null ? FormatVec3(entry.Transform.localPosition) : "<null>";
                        writer.WriteLine(entry.Id + " key=" + entry.Key + " path=" + entry.DebugPath + " name=" + name + " pos=" + pos);
                    }
                }
                DebugLog.Warn("DoorSync: dumped door registry to " + path);
            }
            catch (Exception ex)
            {
                DebugLog.Warn("DoorSync dump failed: " + ex.Message);
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

        private sealed class DoorEntry
        {
            public uint Id;
            public string Key;
            public string DebugPath;
            public Transform Transform;
            public Rigidbody Rigidbody;
            public HingeJoint Hinge;
            public Quaternion BaseLocalRotation;
            public Quaternion LastSentRotation;
            public Quaternion LastAppliedRotation;
            public float LastSentHingeAngle;
            public float LastAppliedHingeAngle;
            public float LastLocalChangeTime;
            public float LastLocalHingeTime;
            public float SuppressUntilTime;
            public float SuppressHingeUntilTime;
            public float RemoteApplyUntilTime;
            public uint LastRemoteSequence;
            public long LastRemoteTimeMs;
            public uint LastRemoteHingeSequence;
            public long LastRemoteHingeTimeMs;
            public Vector3 HingeAxis;
            public float HingeMin;
            public float HingeMax;
            public bool HingeUseLimits;
            public PlayMakerFSM Fsm;
            public FsmBool DoorOpenBool;
            public bool HasPlayMaker;
            public bool IsVehicleDoor;
            public bool SkipRotationSync;
            public string MpOpenEventName;
            public string MpCloseEventName;
            public string OpenEventName;
            public string CloseEventName;
            public bool LastDoorOpen;
            public float SuppressPlayMakerUntilTime;
            public uint LastRemoteEventSequence;
        }

        private sealed class DoorCandidate
        {
            public HingeJoint Hinge;
            public Transform DoorTransform;
            public PlayMakerFSM Fsm;
            public string Key;
            public string DebugPath;
            public string UniqueKey;
        }

        private sealed class DoorPlayMakerAction : FsmStateAction
        {
            private readonly DoorSync _owner;
            private readonly uint _doorId;
            private readonly bool _open;
            private readonly string _expectedEvent;

            public DoorPlayMakerAction(DoorSync owner, uint doorId, bool open, string expectedEvent)
            {
                _owner = owner;
                _doorId = doorId;
                _open = open;
                _expectedEvent = expectedEvent;
            }

            public override void OnEnter()
            {
                Finish();

                if (State == null || State.Fsm == null || State.Fsm.LastTransition == null)
                {
                    return;
                }

                string trigger = State.Fsm.LastTransition.EventName;
                if (!string.IsNullOrEmpty(_expectedEvent) && !string.Equals(trigger, _expectedEvent, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (_owner != null)
                {
                    _owner.NotifyDoorPlayMaker(_doorId, _open, trigger);
                }
            }
        }
    }
}
