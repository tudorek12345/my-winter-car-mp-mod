using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
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
        private const float VehicleHingeDeadzone = 3f;
        private const float EventOnlyRepeatSeconds = 0.9f;
        private const float ScrapeLocalHoldSeconds = 0.75f;
        private static readonly string[] DoorTokens = new[]
        {
            "door", "ovi", "hatch", "hatchback", "boot", "bootlid", "lid", "gate", "trunk", "hood", "bonnet", "fridge", "freezer", "refrigerator", "icebox"
        };
        private static readonly string[] DoorStateTokens = new[] { "Open", "Close" };
        private static readonly string[] TapTokens = new[]
        {
            "tap", "faucet", "sink", "ignition", "starter", "engine", "key",
            "wiper", "wipers", "light", "lights", "headlight", "headlights", "lamp", "beam",
            "indicator", "signal", "turn", "hazard",
            "scrape", "scraper", "defrost", "defog", "heater", "fan", "blower"
        };
        private static readonly string[] PhoneTokens = new[] { "phone", "telephone" };
        private static readonly string[] FridgeTokens = new[] { "fridge", "freezer", "refrigerator", "icebox" };
        private static readonly string[] TapOpenTokens = new[] { "on", "enable", "start", "down", "scrape" };
        private static readonly string[] TapCloseTokens = new[] { "off", "disable", "stop", "up" };
        private static readonly string[] PhoneOpenTokens = new[] { "pick", "answer", "open" };
        private static readonly string[] PhoneCloseTokens = new[] { "close", "hang", "put", "off" };
        private static readonly string[] TapEventTokensOn = new[] { "on", "enable", "start", "toggle", "down", "scrape", "use", "press", "click", "cycle", "next", "inc", "increase", "plus" };
        private static readonly string[] TapEventTokensOff = new[] { "off", "disable", "stop", "up", "dec", "decrease", "minus", "prev", "previous" };
        private static readonly string[] TapEventExactTokens = new[]
        {
            "ON", "OFF", "USE", "DOWN", "UP", "SCRAPE", "START", "STOP", "ENABLE", "DISABLE",
            "PRESS", "CLICK", "CYCLE", "NEXT", "INC", "DEC", "PLUS", "MINUS"
        };
        private static readonly string[] PhoneEventTokensOpen = new[] { "pick", "answer", "open" };
        private static readonly string[] PhoneEventTokensClose = new[] { "close", "hang", "off" };
        private static readonly string[] SorbetVarTokens = new[]
        {
            "heat", "heater", "temp", "blower", "fan", "defrost", "defog", "window", "ice", "scrape", "snow"
        };
        private static readonly string[] ScrapeStateTokensLayer1 = new[] { "scrape 1", "scrape1" };
        private static readonly string[] ScrapeStateTokensLayer2 = new[] { "scrape 2", "scrape2" };
        private static readonly string[] ScrapeStateTokensGlass = new[] { "get this glass", "get glass", "glass" };
        private static readonly string[] ScrapeStateTokensAny = new[] { "scrape" };
        private static readonly string[] BusPaymentEventTokens = new[] { "buy", "notpaid", "pay", "ticket", "money", "cash", "fare", "lippu", "maksu" };
        private static readonly string[] BusClientRequestEventTokens = new[] { "stop", "bell", "halt", "nextstop", "pysa", "kello" };
        private static readonly string[] VehicleContextPathTokens = new[]
        {
            "carparts", "traffic", "npc_cars", "taxijob", "gifu", "kekmet", "sorbet", "machtwagen",
            "heppa", "fittan", "galaxyliner", "lamore", "menace", "polsa", "svoboda", "victro", "truck", "bus"
        };
        private const float ScrapeFinishDistance = 0.02f;

        private readonly Settings _settings;
        private VehicleSync _vehicleSync;
        private readonly List<DoorEntry> _doors = new List<DoorEntry>();
        private readonly Dictionary<uint, DoorEntry> _doorLookup = new Dictionary<uint, DoorEntry>();
        private readonly List<DoorEventData> _doorEventQueue = new List<DoorEventData>(16);
        private readonly List<ScrapeStateData> _scrapeStateQueue = new List<ScrapeStateData>(16);
        private readonly HashSet<uint> _loggedSorbetEntries = new HashSet<uint>();
        private int _lastSceneIndex = int.MinValue;
        private string _lastSceneName = string.Empty;
        private float _nextRotationSampleTime;
        private float _nextHingeSampleTime;
        private float _nextSummaryTime;
        private float _nextApplyLogTime;
        private float _nextEventLogTime;
        private float _nextHingeSendLogTime;
        private float _nextHingeApplyLogTime;
        private float _nextScrapeLogTime;
        private float _nextScrapeForceLogTime;
        private float _nextScrapeAuthorityLogTime;
        private float _nextDashboardSampleTime;
        private float _nextDashboardSummaryTime;
        private float _lastDashboardSendTime;
        private float _nextDashboardApplyLogTime;
        private bool _dumpedDoors;
        private readonly HashSet<uint> _missingDoorIds = new HashSet<uint>();
        private bool _pendingRescan;
        private float _nextRescanTime;
        private string _rescanReason = string.Empty;
        private bool _sorbetBindingsReady;
        private readonly List<SorbetControlBinding> _sorbetControls = new List<SorbetControlBinding>(12);

        public DoorSync(Settings settings)
        {
            _settings = settings;
        }

        public void SetVehicleSync(VehicleSync vehicleSync)
        {
            _vehicleSync = vehicleSync;
        }

        public bool Enabled
        {
            get { return _settings != null && _settings.DoorSyncEnabled.Value; }
        }

        internal bool IsBusPath(string debugPath)
        {
            if (string.IsNullOrEmpty(debugPath))
            {
                return false;
            }

            // BUS is under NPC_CARS in current dumps/logs, but keep it flexible.
            if (debugPath.IndexOf("NPC_CARS", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            return debugPath.IndexOf("/BUS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   debugPath.IndexOf("BUS#", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildBusFsmKey(string debugPath, string fsmName)
        {
            // BUS is reparented at runtime (spawn points), so debug paths differ between instances.
            // Normalize to a stable identifier so DoorEvent ids match host <-> client.
            string stablePath = debugPath ?? string.Empty;
            if (!string.IsNullOrEmpty(stablePath))
            {
                string[] parts = stablePath.Split('/');
                int busIndex = -1;
                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i];
                    if (!string.IsNullOrEmpty(part) && part.IndexOf("BUS", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        busIndex = i;
                        break;
                    }
                }

                if (busIndex >= 0)
                {
                    List<string> kept = new List<string>(parts.Length - busIndex);
                    for (int i = busIndex; i < parts.Length; i++)
                    {
                        string part = parts[i];
                        if (string.IsNullOrEmpty(part))
                        {
                            continue;
                        }

                        int suffix = part.LastIndexOf('#');
                        if (suffix >= 0)
                        {
                            part = part.Substring(0, suffix);
                        }

                        kept.Add(part);
                    }
                    stablePath = string.Join("/", kept.ToArray());
                }
            }

            string name = string.IsNullOrEmpty(fsmName) ? "FSM" : fsmName;
            return "bus_fsm:" + name + ":" + stablePath + "|pm";
        }

        internal bool TryRegisterBusFsm(PlayMakerFSM fsm, string debugPath)
        {
            if (!Enabled || _settings == null || !_settings.DoorPlayMakerEnabled.Value)
            {
                return false;
            }

            if (fsm == null || fsm.Fsm == null || fsm.transform == null)
            {
                return false;
            }

            if (string.IsNullOrEmpty(debugPath))
            {
                debugPath = BuildDebugPath(fsm.transform);
            }

            if (!IsBusPath(debugPath))
            {
                return false;
            }

            string fsmName = GetFsmName(fsm);
            string[] openTokens = new[] { "OPEN", "ON" };
            string[] closeTokens = new[] { "CLOSE", "OFF" };

            if (string.Equals(fsmName, "Start", StringComparison.OrdinalIgnoreCase))
            {
                openTokens = new[] { "START", "ON" };
                closeTokens = new[] { "STOP", "OFF" };
            }
            else if (!string.Equals(fsmName, "Door", StringComparison.OrdinalIgnoreCase))
            {
                openTokens = new[] { "OPEN", "START", "ON" };
                closeTokens = new[] { "CLOSE", "STOP", "OFF" };
            }

            // BUS is reparented at runtime which changes debugPath; don't key bus FSMs by raw path.
            Transform doorTransform = fsm.transform;
            string key = BuildBusFsmKey(debugPath, fsmName);
            uint id = HashPath(key);

            DoorEntry entry;
            if (!_doorLookup.TryGetValue(id, out entry))
            {
                entry = new DoorEntry
                {
                    Id = id,
                    Key = key,
                    DebugPath = debugPath,
                    Transform = doorTransform,
                    Rigidbody = doorTransform != null ? doorTransform.GetComponent<Rigidbody>() : null,
                    Hinge = null,
                    BaseLocalRotation = doorTransform != null ? doorTransform.localRotation : Quaternion.identity,
                    LastSentRotation = doorTransform != null ? doorTransform.localRotation : Quaternion.identity,
                    LastAppliedRotation = doorTransform != null ? doorTransform.localRotation : Quaternion.identity,
                    InteractionKind = InteractionKind.Tap
                };
                _doors.Add(entry);
                _doorLookup.Add(id, entry);
            }
            else
            {
                // Keep debug path + transform updated as BUS moves around in the hierarchy.
                entry.DebugPath = debugPath;
                entry.Transform = doorTransform;
                entry.Rigidbody = doorTransform != null ? doorTransform.GetComponent<Rigidbody>() : null;
                entry.InteractionKind = InteractionKind.Tap;
            }

            entry.IsVehicleDoor = IsVehicleDoor(entry.Transform, entry.Hinge);
            if (entry.IsVehicleDoor)
            {
                entry.VehicleBody = entry.VehicleBody ?? FindVehicleBody(entry.Transform);
                entry.AllowVehiclePlayMaker = true;
                entry.SkipHingeSync = true;
            }

            bool playMaker = AttachPlayMakerEventOnly(fsm, entry, fsmName, openTokens, closeTokens, true);
            if (!playMaker)
            {
                return false;
            }

            entry.HasPlayMaker = true;
            UpdateRotationSyncPolicy(entry);
            EnsureScrapeState(entry);
            _sorbetBindingsReady = false;

            if (_settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("DoorSync: bus FSM registered id=" + id + " key=" + key + " fsm=" + fsmName + " path=" + debugPath);
            }

            return true;
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

                if (entry.AllowVehiclePlayMaker && !IsSorbetDoor(entry.DebugPath, entry.Transform))
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
                string doorPath = !string.IsNullOrEmpty(entry.DebugPath) ? entry.DebugPath : "<null>";
                string angleNote = entry.Hinge != null ? (" angle=" + entry.Hinge.angle.ToString("F1")) : string.Empty;
                DebugLog.Verbose("DoorSync: sending " + buffer.Count + " update(s). First=" + doorName + angleNote + " path=" + doorPath);
                _nextSummaryTime = now + 1f;
            }

            return buffer.Count;
        }

        public int CollectHingeChanges(long unixTimeMs, float now, List<DoorHingeStateData> buffer, OwnerKind localOwner, bool includeUnowned)
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

                if (entry.AllowVehiclePlayMaker && !IsSorbetDoor(entry.DebugPath, entry.Transform))
                {
                    continue;
                }

                if (entry.SkipHingeSync)
                {
                    continue;
                }

                if (!IsLocalVehicleAuthority(entry, localOwner, includeUnowned))
                {
                    continue;
                }

                if (now < entry.SuppressHingeUntilTime)
                {
                    continue;
                }

                float angle = NormalizeAngle(entry.Hinge.angle);
                if (Mathf.Abs(angle) <= VehicleHingeDeadzone)
                {
                    angle = 0f;
                }
                if (entry.VehicleBody != null)
                {
                    float speed = entry.VehicleBody.velocity.magnitude;
                    if (speed > 0.5f && Mathf.Abs(angle) < 8f)
                    {
                        angle = 0f;
                    }
                }
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
                string doorPath = entry != null && !string.IsNullOrEmpty(entry.DebugPath) ? entry.DebugPath : "<null>";
                DebugLog.Verbose("DoorSync: sending " + buffer.Count + " hinge update(s). First=" + doorName + " angle=" + buffer[0].Angle.ToString("F1") + " path=" + doorPath);
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

        public int CollectScrapeStates(List<ScrapeStateData> buffer)
        {
            buffer.Clear();
            if (!Enabled || _scrapeStateQueue.Count == 0)
            {
                return 0;
            }

            for (int i = 0; i < _scrapeStateQueue.Count; i++)
            {
                buffer.Add(_scrapeStateQueue[i]);
            }
            _scrapeStateQueue.Clear();
            return buffer.Count;
        }

        public int CollectScrapeSnapshot(List<ScrapeStateData> buffer)
        {
            buffer.Clear();
            if (!Enabled || _doors.Count == 0)
            {
                return 0;
            }

            for (int i = 0; i < _doors.Count; i++)
            {
                DoorEntry entry = _doors[i];
                if (entry == null || !IsScrapeEntry(entry))
                {
                    continue;
                }

                EnsureScrapeState(entry);
                if (!entry.HasScrapeState || entry.ScrapeLayer == null || entry.ScrapeX == null ||
                    entry.ScrapeXold == null || entry.ScrapeDistance == null)
                {
                    continue;
                }

                buffer.Add(new ScrapeStateData
                {
                    DoorId = entry.Id,
                    Layer = entry.ScrapeLayer.Value,
                    X = entry.ScrapeX.Value,
                    Xold = entry.ScrapeXold.Value,
                    Distance = entry.ScrapeDistance.Value
                });
            }

            return buffer.Count;
        }

        public bool TryBuildSorbetDashboardState(long unixTimeMs, uint vehicleId, bool allowSend, out SorbetDashboardStateData state)
        {
            state = new SorbetDashboardStateData();
            if (!Enabled || !allowSend || vehicleId == 0)
            {
                return false;
            }

            EnsureSorbetControlBindings();
            if (_sorbetControls.Count == 0)
            {
                return false;
            }

            float now = Time.realtimeSinceStartup;
            if (now < _nextDashboardSampleTime)
            {
                return false;
            }
            _nextDashboardSampleTime = now + 0.1f;

            bool changed = false;
            byte mask = 0;
            byte auxMask = 0;

            ControlValueKind heaterTempKind;
            float heaterTempValue;
            UpdateSorbetControl(_sorbetControls, SorbetControl.HeaterTemp, now, out heaterTempKind, out heaterTempValue, ref changed, ref mask, ref auxMask);

            ControlValueKind heaterBlowerKind;
            float heaterBlowerValue;
            UpdateSorbetControl(_sorbetControls, SorbetControl.HeaterBlower, now, out heaterBlowerKind, out heaterBlowerValue, ref changed, ref mask, ref auxMask);

            ControlValueKind heaterDirectionKind;
            float heaterDirectionValue;
            UpdateSorbetControl(_sorbetControls, SorbetControl.HeaterDirection, now, out heaterDirectionKind, out heaterDirectionValue, ref changed, ref mask, ref auxMask);

            ControlValueKind windowHeaterKind;
            float windowHeaterValue;
            UpdateSorbetControl(_sorbetControls, SorbetControl.WindowHeater, now, out windowHeaterKind, out windowHeaterValue, ref changed, ref mask, ref auxMask);

            ControlValueKind lightModesKind;
            float lightModesValue;
            UpdateSorbetControl(_sorbetControls, SorbetControl.LightModes, now, out lightModesKind, out lightModesValue, ref changed, ref mask, ref auxMask);

            ControlValueKind wipersKind;
            float wipersValue;
            UpdateSorbetControl(_sorbetControls, SorbetControl.Wipers, now, out wipersKind, out wipersValue, ref changed, ref mask, ref auxMask);

            ControlValueKind hazardKind;
            float hazardValue;
            UpdateSorbetControl(_sorbetControls, SorbetControl.Hazard, now, out hazardKind, out hazardValue, ref changed, ref mask, ref auxMask);

            ControlValueKind turnSignalsKind;
            float turnSignalsValue;
            UpdateSorbetControl(_sorbetControls, SorbetControl.TurnSignals, now, out turnSignalsKind, out turnSignalsValue, ref changed, ref mask, ref auxMask);

            ControlValueKind ignitionKind;
            float ignitionValue;
            UpdateSorbetControl(_sorbetControls, SorbetControl.Ignition, now, out ignitionKind, out ignitionValue, ref changed, ref mask, ref auxMask);

            ControlValueKind starterKind;
            float starterValue;
            UpdateSorbetControl(_sorbetControls, SorbetControl.Starter, now, out starterKind, out starterValue, ref changed, ref mask, ref auxMask);

            ControlValueKind interiorLightKind;
            float interiorLightValue;
            UpdateSorbetControl(_sorbetControls, SorbetControl.InteriorLight, now, out interiorLightKind, out interiorLightValue, ref changed, ref mask, ref auxMask);

            if (mask == 0 && auxMask == 0)
            {
                return false;
            }

            if (!changed && now - _lastDashboardSendTime < 1.5f)
            {
                return false;
            }

            _lastDashboardSendTime = now;
            state = new SorbetDashboardStateData
            {
                UnixTimeMs = unixTimeMs,
                VehicleId = vehicleId,
                Mask = mask,
                AuxMask = auxMask,
                HeaterTempKind = (byte)heaterTempKind,
                HeaterTempValue = heaterTempValue,
                HeaterBlowerKind = (byte)heaterBlowerKind,
                HeaterBlowerValue = heaterBlowerValue,
                HeaterDirectionKind = (byte)heaterDirectionKind,
                HeaterDirectionValue = heaterDirectionValue,
                WindowHeaterKind = (byte)windowHeaterKind,
                WindowHeaterValue = windowHeaterValue,
                LightModesKind = (byte)lightModesKind,
                LightModesValue = lightModesValue,
                WipersKind = (byte)wipersKind,
                WipersValue = wipersValue,
                HazardKind = (byte)hazardKind,
                HazardValue = hazardValue,
                TurnSignalsKind = (byte)turnSignalsKind,
                TurnSignalsValue = turnSignalsValue,
                IgnitionKind = (byte)ignitionKind,
                IgnitionValue = ignitionValue,
                StarterKind = (byte)starterKind,
                StarterValue = starterValue,
                InteriorLightKind = (byte)interiorLightKind,
                InteriorLightValue = interiorLightValue
            };

            if (_settings != null && _settings.VerboseLogging.Value && now >= _nextDashboardSummaryTime)
            {
                DebugLog.Verbose("DoorSync: sorbet dashboard summary temp=" + heaterTempValue.ToString("F2") +
                    " blower=" + heaterBlowerValue.ToString("F2") +
                    " dir=" + heaterDirectionValue.ToString("F2") +
                    " win=" + windowHeaterValue.ToString("F2") +
                    " lights=" + lightModesValue.ToString("F2") +
                    " wipers=" + wipersValue.ToString("F2") +
                    " hazard=" + hazardValue.ToString("F2") +
                    " signal=" + turnSignalsValue.ToString("F2") +
                    " ignition=" + ignitionValue.ToString("F2") +
                    " starter=" + starterValue.ToString("F2") +
                    " cabin=" + interiorLightValue.ToString("F2"));
                _nextDashboardSummaryTime = now + 1.5f;
            }

            return true;
        }

        public void ApplyRemoteScrapeState(ScrapeStateData state)
        {
            if (!Enabled)
            {
                return;
            }

            DoorEntry entry;
            if (!_doorLookup.TryGetValue(state.DoorId, out entry))
            {
                RegisterMissingDoorId(state.DoorId, "missing scrape door id");
                return;
            }

            if (state.Sequence != 0 && !IsNewerSequence(state.Sequence, entry.LastRemoteScrapeSequence))
            {
                return;
            }

            if (entry.Fsm == null || entry.Fsm.Fsm == null)
            {
                RequestRescan("missing scrape fsm for door id " + state.DoorId);
                return;
            }

            EnsureScrapeState(entry);
            if (!entry.HasScrapeState)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            bool localScrapeRecent = entry.LastLocalScrapeTime > 0f && now - entry.LastLocalScrapeTime <= 0.25f;
            if (now < entry.ScrapeLocalAuthorityUntil && localScrapeRecent)
            {
                if (_settings != null && _settings.VerboseLogging.Value && now >= _nextScrapeAuthorityLogTime)
                {
                    DebugLog.Verbose("DoorSync: skip remote scrape (local active) id=" + entry.Id +
                        " path=" + entry.DebugPath);
                    _nextScrapeAuthorityLogTime = now + 1f;
                }
                return;
            }

            entry.LastRemoteScrapeSequence = state.Sequence;
            entry.ScrapeSuppressUntilTime = now + 0.75f;

            bool sameAsRemoteBaseline = state.Layer == entry.LastRemoteScrapeLayer &&
                Mathf.Abs(state.X - entry.LastRemoteScrapeX) <= 0.01f &&
                Mathf.Abs(state.Xold - entry.LastRemoteScrapeXold) <= 0.01f &&
                Mathf.Abs(state.Distance - entry.LastRemoteScrapeDistance) <= 0.001f;
            if (sameAsRemoteBaseline && now < entry.ScrapeRemoteHoldUntil)
            {
                return;
            }

            entry.LastRemoteScrapeLayer = state.Layer;
            entry.LastRemoteScrapeX = state.X;
            entry.LastRemoteScrapeXold = state.Xold;
            entry.LastRemoteScrapeDistance = state.Distance;
            entry.LastRemoteScrapeApplyTime = now;
            bool isFinishState = state.Distance <= ScrapeFinishDistance || state.Layer <= 0;
            entry.ScrapeRemoteHoldUntil = now + (isFinishState ? 0.25f : 1.0f);

            bool changed = state.Layer != entry.LastScrapeLayer ||
                Mathf.Abs(state.X - entry.LastScrapeX) > 0.01f ||
                Mathf.Abs(state.Xold - entry.LastScrapeXold) > 0.01f ||
                Mathf.Abs(state.Distance - entry.LastScrapeDistance) > 0.001f;

            bool progressed = state.Layer > entry.LastScrapeLayer ||
                state.Distance < entry.LastScrapeDistance - 0.001f ||
                Mathf.Abs(state.X - entry.LastScrapeX) > 0.5f ||
                Mathf.Abs(state.Xold - entry.LastScrapeXold) > 0.5f;
            bool shouldKick = changed || progressed;

            if (entry.ScrapeLayer != null)
            {
                entry.ScrapeLayer.Value = state.Layer;
            }
            if (entry.ScrapeX != null)
            {
                entry.ScrapeX.Value = state.X;
            }
            if (entry.ScrapeXold != null)
            {
                entry.ScrapeXold.Value = state.Xold;
            }
            if (entry.ScrapeDistance != null)
            {
                entry.ScrapeDistance.Value = state.Distance;
            }

            entry.LastScrapeLayer = state.Layer;
            entry.LastScrapeX = state.X;
            entry.LastScrapeXold = state.Xold;
            entry.LastScrapeDistance = state.Distance;

            if (entry.ScrapeInside != null)
            {
                entry.ScrapeInside.Value = !isFinishState;
            }

            if (!isFinishState && shouldKick && entry.Fsm.Fsm.HasEvent("DOWN"))
            {
                EnsureGlobalTransitionForEvent(entry.Fsm, "DOWN");
                entry.Fsm.SendEvent("DOWN");
            }
            if (!isFinishState && shouldKick && entry.Fsm.Fsm.HasEvent("SCRAPE"))
            {
                EnsureGlobalTransitionForEvent(entry.Fsm, "SCRAPE");
                entry.Fsm.SendEvent("SCRAPE");
            }
            if (isFinishState && changed && entry.Fsm.Fsm.HasEvent("OFF"))
            {
                EnsureGlobalTransitionForEvent(entry.Fsm, "OFF");
                entry.Fsm.SendEvent("OFF");
            }
            if (shouldKick)
            {
                TryForceScrapeState(entry, state);
            }

            if (_settings != null && _settings.VerboseLogging.Value && now >= _nextScrapeLogTime)
            {
                DebugLog.Verbose("DoorSync: apply remote scrape id=" + entry.Id +
                    " layer=" + state.Layer +
                    " x=" + state.X.ToString("F2") +
                    " dist=" + state.Distance.ToString("F2") +
                    " path=" + entry.DebugPath);
                _nextScrapeLogTime = now + 1f;
            }
        }

        public void ApplySorbetDashboardState(SorbetDashboardStateData state)
        {
            if (!Enabled)
            {
                return;
            }

            EnsureSorbetControlBindings();
            if (_sorbetControls.Count == 0)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            ApplySorbetControl(_sorbetControls, SorbetControl.HeaterTemp, now, state.Mask, state.AuxMask, (ControlValueKind)state.HeaterTempKind, state.HeaterTempValue);
            ApplySorbetControl(_sorbetControls, SorbetControl.HeaterBlower, now, state.Mask, state.AuxMask, (ControlValueKind)state.HeaterBlowerKind, state.HeaterBlowerValue);
            ApplySorbetControl(_sorbetControls, SorbetControl.HeaterDirection, now, state.Mask, state.AuxMask, (ControlValueKind)state.HeaterDirectionKind, state.HeaterDirectionValue);
            ApplySorbetControl(_sorbetControls, SorbetControl.WindowHeater, now, state.Mask, state.AuxMask, (ControlValueKind)state.WindowHeaterKind, state.WindowHeaterValue);
            ApplySorbetControl(_sorbetControls, SorbetControl.LightModes, now, state.Mask, state.AuxMask, (ControlValueKind)state.LightModesKind, state.LightModesValue);
            ApplySorbetControl(_sorbetControls, SorbetControl.Wipers, now, state.Mask, state.AuxMask, (ControlValueKind)state.WipersKind, state.WipersValue);
            ApplySorbetControl(_sorbetControls, SorbetControl.Hazard, now, state.Mask, state.AuxMask, (ControlValueKind)state.HazardKind, state.HazardValue);
            ApplySorbetControl(_sorbetControls, SorbetControl.TurnSignals, now, state.Mask, state.AuxMask, (ControlValueKind)state.TurnSignalsKind, state.TurnSignalsValue);
            ApplySorbetControl(_sorbetControls, SorbetControl.Ignition, now, state.Mask, state.AuxMask, (ControlValueKind)state.IgnitionKind, state.IgnitionValue);
            ApplySorbetControl(_sorbetControls, SorbetControl.Starter, now, state.Mask, state.AuxMask, (ControlValueKind)state.StarterKind, state.StarterValue);
            ApplySorbetControl(_sorbetControls, SorbetControl.InteriorLight, now, state.Mask, state.AuxMask, (ControlValueKind)state.InteriorLightKind, state.InteriorLightValue);

            if (_settings != null && _settings.VerboseLogging.Value && now >= _nextDashboardApplyLogTime)
            {
                DebugLog.Verbose("DoorSync: applied sorbet dashboard state mask=" + state.Mask +
                    " auxMask=" + state.AuxMask +
                    " temp=" + state.HeaterTempValue.ToString("F2") +
                    " blower=" + state.HeaterBlowerValue.ToString("F2") +
                    " dir=" + state.HeaterDirectionValue.ToString("F2") +
                    " win=" + state.WindowHeaterValue.ToString("F2") +
                    " lights=" + state.LightModesValue.ToString("F2") +
                    " wipers=" + state.WipersValue.ToString("F2") +
                    " hazard=" + state.HazardValue.ToString("F2") +
                    " signal=" + state.TurnSignalsValue.ToString("F2") +
                    " ignition=" + state.IgnitionValue.ToString("F2") +
                    " starter=" + state.StarterValue.ToString("F2") +
                    " cabin=" + state.InteriorLightValue.ToString("F2"));
                _nextDashboardApplyLogTime = now + 1.5f;
            }
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

                if (entry.IsVehicleDoor && _vehicleSync != null && _settings != null)
                {
                    OwnerKind localOwner = OwnerKind.None;
                    if (_settings.Mode.Value == Mode.Host)
                    {
                        localOwner = OwnerKind.Host;
                    }
                    else if (_settings.Mode.Value == Mode.Client)
                    {
                        localOwner = OwnerKind.Client;
                    }

                    bool includeUnowned = localOwner == OwnerKind.Host;
                    if (!entry.AllowVehiclePlayMaker)
                    {
                        if (IsLocalVehicleAuthority(entry, localOwner, includeUnowned))
                        {
                            EnsureRemoteDoorDynamic(entry);
                        }
                        else
                        {
                            EnsureRemoteDoorKinematic(entry);
                        }
                    }
                }

                if (!entry.SkipRotationSync && now < entry.RemoteApplyUntilTime && now - entry.LastLocalChangeTime > LocalHoldSeconds)
                {
                    ApplyRotation(entry, entry.LastAppliedRotation);
                }

                if (entry.DoorOpenBool != null && now >= entry.SuppressPlayMakerUntilTime)
                {
                    if (entry.IsVehicleDoor && entry.AllowVehiclePlayMaker && !IsSorbetDoor(entry.DebugPath, entry.Transform))
                    {
                        continue;
                    }

                    bool open = entry.DoorOpenBool.Value;
                    if (open != entry.LastDoorOpen)
                    {
                        entry.LastDoorOpen = open;
                        EnqueueDoorEvent(entry, open, null);
                    }
                }

                if (entry.HasScrapeState)
                {
                    UpdateScrapeState(entry, now);
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
                RegisterMissingDoorId(state.DoorId, "missing door id");
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
            if (entry.IsVehicleDoor && entry.SkipHingeSync)
            {
                if (entry.SkipHingeSync)
                {
                    EnsureRemoteDoorKinematic(entry);
                }
            }
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
                RegisterMissingDoorId(state.DoorId, "missing hinge door id");
                return;
            }

            if (entry.Hinge == null)
            {
                RequestRescan("missing hinge for door id " + state.DoorId);
                return;
            }

            if (entry.SkipHingeSync)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            OwnerKind localOwner = OwnerKind.None;
            if (_settings != null)
            {
                if (_settings.Mode.Value == Mode.Host)
                {
                    localOwner = OwnerKind.Host;
                }
                else if (_settings.Mode.Value == Mode.Client)
                {
                    localOwner = OwnerKind.Client;
                }
            }
            bool includeUnowned = localOwner == OwnerKind.Host;
            if (entry.IsVehicleDoor && IsLocalVehicleAuthority(entry, localOwner, includeUnowned))
            {
                return;
            }
            if (now - entry.LastLocalHingeTime < LocalHoldSeconds)
            {
                return;
            }

            if (!IsNewerSequence(state.Sequence, entry.LastRemoteHingeSequence))
            {
                return;
            }

            float targetAngle = NormalizeAngle(state.Angle);
            bool useLimits = entry.HingeUseLimits;
            if (entry.IsVehicleDoor && useLimits && Mathf.Abs(entry.HingeMax - entry.HingeMin) < 0.05f)
            {
                useLimits = false;
                if (entry.Hinge.useLimits)
                {
                    entry.Hinge.useLimits = false;
                }
            }
            if (useLimits)
            {
                targetAngle = Mathf.Clamp(targetAngle, entry.HingeMin, entry.HingeMax);
            }
            if (Mathf.Abs(targetAngle) <= VehicleHingeDeadzone)
            {
                targetAngle = 0f;
            }
            if (entry.VehicleBody != null)
            {
                float speed = entry.VehicleBody.velocity.magnitude;
                if (speed > 0.5f && Mathf.Abs(targetAngle) < 8f)
                {
                    targetAngle = 0f;
                }
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
            EnsureRemoteDoorKinematic(entry);
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
                RegisterMissingDoorId(state.DoorId, "missing door event id");
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

            string incomingEvent = NormalizeDoorEventName(state.EventName);
            if (IsBusRouteEventLocalOnly(entry, incomingEvent))
            {
                return;
            }

            entry.LastRemoteEventSequence = state.Sequence;
            if (IsSorbetDashboardControlEntry(entry))
            {
                // Dashboard controls are synchronized by SorbetDashboardStateData.
                // Ignore EventOnly door events to prevent ON/OFF echo loops.
                return;
            }
            if (entry.Fsm == null || !entry.HasPlayMaker)
            {
                RequestRescan("missing PlayMaker for door id " + state.DoorId);
                return;
            }

            if (entry.IsVehicleDoor)
            {
                // Vehicle doors are hinge-synced by default; only allow PlayMaker for explicit vehicle mapping.
                if (!entry.AllowVehiclePlayMaker)
                {
                    return;
                }
            }

            float now = Time.realtimeSinceStartup;
            float suppressSeconds = RemoteSuppressSeconds;
            if (IsScrapeEntry(entry) || IsSorbetHeaterButton(entry))
            {
                suppressSeconds = 0.8f;
            }
            entry.SuppressPlayMakerUntilTime = now + suppressSeconds;
            bool isOpen = state.Open != 0;
            entry.LastDoorOpen = isOpen;
            if (entry.DoorOpenBool != null)
            {
                entry.DoorOpenBool.Value = isOpen;
            }

            string eventName = null;
            if (!string.IsNullOrEmpty(state.EventName) && entry.Fsm.Fsm != null && entry.Fsm.Fsm.HasEvent(state.EventName))
            {
                eventName = state.EventName;
                if (entry.EventOnly || IsSorbetDoor(entry.DebugPath, entry.Transform))
                {
                    EnsureGlobalTransitionForEvent(entry.Fsm, eventName);
                }
            }
            else if (!string.IsNullOrEmpty(state.EventName) && _settings != null && _settings.VerboseLogging.Value && now >= _nextEventLogTime)
            {
                DebugLog.Verbose("DoorSync: remote event missing fsm event id=" + entry.Id +
                    " name=" + state.EventName +
                    " fsm=" + (entry.Fsm != null ? entry.Fsm.FsmName : "<null>") +
                    " path=" + entry.DebugPath);
                _nextEventLogTime = now + 1f;
            }
            if (string.IsNullOrEmpty(eventName))
            {
                eventName = state.Open != 0 ? entry.MpOpenEventName : entry.MpCloseEventName;
                bool useNativeEvent = entry.EventOnly || (entry.InteractionKind != InteractionKind.Door && entry.InteractionKind != InteractionKind.Tap);
                if (useNativeEvent)
                {
                    string nativeEvent = state.Open != 0 ? entry.OpenEventName : entry.CloseEventName;
                    if (!string.IsNullOrEmpty(nativeEvent) && entry.Fsm.Fsm != null && entry.Fsm.Fsm.HasEvent(nativeEvent))
                    {
                        eventName = nativeEvent;
                    }
                }
            }

            if (!string.IsNullOrEmpty(eventName))
            {
                if (IsBusRouteEventLocalOnly(entry, eventName))
                {
                    return;
                }

                bool isScrape = IsScrapeEntry(entry);
                bool isHeater = IsSorbetHeaterButton(entry);
                bool isWindowHeater = isHeater && IsSorbetWindowHeater(entry);
                if (isScrape)
                {
                    if (!IsScrapeEvent(eventName))
                    {
                        if (_settings != null && _settings.VerboseLogging.Value && now >= _nextEventLogTime)
                        {
                            DebugLog.Verbose("DoorSync: skip remote scrape event id=" + entry.Id +
                                " event=" + eventName +
                                " path=" + entry.DebugPath);
                            _nextEventLogTime = now + 1f;
                        }
                        return;
                    }
                }
                if (isHeater && !isWindowHeater)
                {
                    if (!IsHeaterStepEvent(eventName))
                    {
                        if (_settings != null && _settings.VerboseLogging.Value && now >= _nextEventLogTime)
                        {
                            DebugLog.Verbose("DoorSync: skip remote heater event id=" + entry.Id +
                                " event=" + eventName +
                                " path=" + entry.DebugPath);
                            _nextEventLogTime = now + 1f;
                        }
                        return;
                    }
                }

                entry.Fsm.SendEvent(eventName);
                if (_settings != null && _settings.VerboseLogging.Value && now >= _nextEventLogTime)
                {
                    DebugLog.Verbose("DoorSync: remote door event id=" + entry.Id +
                        " open=" + (state.Open != 0) +
                        " fsm=" + entry.Fsm.FsmName +
                        " event=" + eventName +
                        " path=" + entry.DebugPath);
                    _nextEventLogTime = now + 1f;
                }
                LogSorbetDetails(entry, "remote", eventName);
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

                if (entry.SkipHingeSync)
                {
                    continue;
                }

                float angle = NormalizeAngle(entry.Hinge.angle);
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
            _scrapeStateQueue.Clear();
            _missingDoorIds.Clear();
            _dumpedDoors = false;
            _nextRotationSampleTime = 0f;
            _nextHingeSampleTime = 0f;
            _nextSummaryTime = 0f;
            _nextApplyLogTime = 0f;
            _nextEventLogTime = 0f;
            _nextHingeSendLogTime = 0f;
            _nextHingeApplyLogTime = 0f;
            _nextScrapeLogTime = 0f;
            _nextScrapeForceLogTime = 0f;
            _nextScrapeAuthorityLogTime = 0f;
            _nextDashboardSampleTime = 0f;
            _nextDashboardSummaryTime = 0f;
            _lastDashboardSendTime = 0f;
            _nextDashboardApplyLogTime = 0f;
            _pendingRescan = false;
            _nextRescanTime = 0f;
            _rescanReason = string.Empty;
            _sorbetBindingsReady = false;
            _sorbetControls.Clear();
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

            int busPlayMakerAttached = RegisterBusFsmsAfterScan();
            playMakerAttached += busPlayMakerAttached;
            DebugLog.Info("DoorSync: tracking " + _doors.Count + " door(s) in " + _lastSceneName + ". PlayMaker hooked=" + playMakerAttached + ".");
        }

        private int RegisterBusFsmsAfterScan()
        {
            PlayMakerFSM[] fsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
            if (fsms == null || fsms.Length == 0)
            {
                return 0;
            }

            int attached = 0;
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.gameObject == null || fsm.transform == null || fsm.Fsm == null)
                {
                    continue;
                }

                if (fsm.hideFlags != HideFlags.None)
                {
                    continue;
                }

                string fsmName = GetFsmName(fsm);
                if (!string.Equals(fsmName, "Door", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fsmName, "Start", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string path = BuildDebugPath(fsm.transform);
                if (!IsBusPath(path))
                {
                    continue;
                }

                if (TryRegisterBusFsm(fsm, path))
                {
                    attached++;
                }
            }

            if (attached > 0 && _settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("DoorSync: bus FSM fallback attached=" + attached);
            }

            return attached;
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
                LastAppliedRotation = doorTransform.localRotation,
                InteractionKind = DetectInteractionKind(doorTransform.name, debugPath)
            };

            entry.IsVehicleDoor = IsVehicleDoor(doorTransform, hinge);
            entry.VehicleBody = entry.IsVehicleDoor ? FindVehicleBody(doorTransform) : null;
            if (entry.IsVehicleDoor && hinge != null)
            {
                ConfigureVehicleHinge(entry);
            }

            bool playMaker = false;
            if (!entry.IsVehicleDoor)
            {
                playMaker = AttachPlayMaker(doorTransform, entry);
            }
            else if (IsSorbetDoor(debugPath, doorTransform))
            {
                PlayMakerFSM fsm = FindVehicleDoorFsm(doorTransform);
                if (fsm == null)
                {
                    DebugLog.Verbose("DoorSync: Sorbet door FSM not found for " + doorTransform.name);
                }
                else
                {
                    playMaker = AttachPlayMaker(fsm, entry, doorTransform.name);
                    if (playMaker)
                    {
                        entry.AllowVehiclePlayMaker = true;
                        entry.SkipHingeSync = true;
                        DebugLog.Verbose("DoorSync: Sorbet door PlayMaker enabled id=" + entry.Id + " path=" + entry.DebugPath);
                    }
                }
            }
            else if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("DoorSync: skipping PlayMaker for vehicle door " + doorTransform.name);
            }
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

        private bool IsLocalVehicleAuthority(DoorEntry entry, OwnerKind localOwner, bool includeUnowned)
        {
            if (entry == null || !entry.IsVehicleDoor)
            {
                return true;
            }

            if (_vehicleSync == null)
            {
                return localOwner == OwnerKind.Host && includeUnowned;
            }

            Transform transform = entry.Hinge != null ? entry.Hinge.transform : entry.Transform;
            return _vehicleSync.IsLocalAuthorityForTransform(transform, localOwner, includeUnowned);
        }

        private static bool IsSorbetDoor(string debugPath, Transform doorTransform)
        {
            if (!string.IsNullOrEmpty(debugPath) &&
                debugPath.IndexOf("sorbet", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (doorTransform != null && doorTransform.root != null)
            {
                string rootName = doorTransform.root.name;
                if (!string.IsNullOrEmpty(rootName) &&
                    rootName.IndexOf("sorbet", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSorbetTransform(Transform transform)
        {
            if (transform == null)
            {
                return false;
            }

            Transform root = transform.root;
            if (root != null && !string.IsNullOrEmpty(root.name) &&
                root.name.IndexOf("sorbet", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            Transform current = transform;
            int depth = 0;
            while (current != null && depth < 4)
            {
                if (!string.IsNullOrEmpty(current.name) &&
                    current.name.IndexOf("sorbet", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
                current = current.parent;
                depth++;
            }

            return false;
        }

        private static PlayMakerFSM FindVehicleDoorFsm(Transform doorTransform)
        {
            if (doorTransform == null)
            {
                return null;
            }

            PlayMakerFSM[] fsms = doorTransform.GetComponentsInChildren<PlayMakerFSM>(true);
            if (fsms == null || fsms.Length == 0)
            {
                return null;
            }

            PlayMakerFSM fallback = null;
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.Fsm == null)
                {
                    continue;
                }

                string fsmName = !string.IsNullOrEmpty(fsm.Fsm.Name) ? fsm.Fsm.Name : fsm.FsmName;
                if (!string.Equals(fsmName, "Use", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string path = BuildDebugPath(fsm.transform);
                if (path.IndexOf("handle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("hatch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("boot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("trunk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("lid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("gate", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return fsm;
                }

                if (fallback == null)
                {
                    fallback = fsm;
                }
            }

            return fallback;
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
            float angle = NormalizeAngle(hinge.angle);
            entry.LastSentHingeAngle = angle;
            entry.LastAppliedHingeAngle = angle;

            bool collapsedLimits = entry.HingeUseLimits && Mathf.Abs(entry.HingeMax - entry.HingeMin) < 0.05f;
            if (collapsedLimits && _settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("DoorSync: hinge limits collapsed for id=" + entry.Id +
                    " min=" + entry.HingeMin.ToString("F1") +
                    " max=" + entry.HingeMax.ToString("F1") +
                    " (keeping limits).");
            }

            if (_settings != null && _settings.VerboseLogging.Value)
            {
                JointMotor motor = hinge.motor;
                JointSpring spring = hinge.spring;
                string motorInfo = hinge.useMotor
                    ? (" motor=on vel=" + motor.targetVelocity.ToString("F1") + " force=" + motor.force.ToString("F1"))
                    : " motor=off";
                string springInfo = hinge.useSpring
                    ? (" spring=on k=" + spring.spring.ToString("F1") + " d=" + spring.damper.ToString("F1") + " target=" + spring.targetPosition.ToString("F1"))
                    : " spring=off";
                DebugLog.Verbose("DoorSync: vehicle hinge door id=" + entry.Id +
                    " axis=" + FormatVec3(entry.HingeAxis) +
                    " limits=" + entry.HingeMin.ToString("F1") + "," + entry.HingeMax.ToString("F1") +
                    " useLimits=" + entry.HingeUseLimits +
                    motorInfo +
                    springInfo +
                    " path=" + entry.DebugPath);
            }
        }

        private bool AddPlayMakerDoor(Transform doorTransform, PlayMakerFSM fsm, string key, string debugPath, ref int playMakerAttached)
        {
            if (doorTransform == null || string.IsNullOrEmpty(key))
            {
                return false;
            }

            string nameHint = BuildFsmNameHint(fsm);
            if (string.IsNullOrEmpty(nameHint) && doorTransform != null)
            {
                nameHint = doorTransform.name;
            }

            DoorEntry existing = FindDoorByPath(debugPath);
            if (existing != null)
            {
                if (existing.IsVehicleDoor)
                {
                    return false;
                }

                bool attached = fsm != null
                    ? AttachPlayMaker(fsm, existing, nameHint)
                    : AttachPlayMaker(doorTransform, existing);

                if (attached)
                {
                    existing.HasPlayMaker = true;
                    UpdateRotationSyncPolicy(existing);
                    EnsureScrapeState(existing);
                    _sorbetBindingsReady = false;
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
                LastAppliedRotation = doorTransform.localRotation,
                InteractionKind = DetectInteractionKind(nameHint, debugPath)
            };

            entry.IsVehicleDoor = IsVehicleDoor(doorTransform, null);
            entry.VehicleBody = entry.IsVehicleDoor ? FindVehicleBody(doorTransform) : null;
            if (entry.IsVehicleDoor && !IsSorbetDoor(debugPath, doorTransform) && entry.InteractionKind == InteractionKind.Door)
            {
                if (_settings != null && _settings.VerboseLogging.Value)
                {
                    DebugLog.Verbose("DoorSync: skipping PlayMaker for vehicle door " + doorTransform.name);
                }
                return false;
            }

            if (entry.IsVehicleDoor)
            {
                entry.AllowVehiclePlayMaker = true;
                entry.SkipHingeSync = true;
            }

            bool playMaker = AttachPlayMaker(fsm, entry, nameHint);
            if (!playMaker)
            {
                DebugLog.Verbose("DoorSync: playmaker door skipped (no FSM) " + doorTransform.name);
                return false;
            }

            entry.HasPlayMaker = playMaker;
            UpdateRotationSyncPolicy(entry);
            _doors.Add(entry);
            _doorLookup.Add(id, entry);
            playMakerAttached++;
            _sorbetBindingsReady = false;
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

            if (entry.Rigidbody != null && entry.Rigidbody.isKinematic && entry.Transform != null)
            {
                entry.Transform.localRotation = entry.BaseLocalRotation *
                    Quaternion.AngleAxis(targetAngle, entry.HingeAxis);
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

        private static float NormalizeAngle(float angle)
        {
            return Mathf.Repeat(angle + 180f, 360f) - 180f;
        }

        private static void EnsureRemoteDoorKinematic(DoorEntry entry)
        {
            if (entry == null || entry.Rigidbody == null)
            {
                return;
            }

            if (!entry.Rigidbody.isKinematic)
            {
                entry.Rigidbody.isKinematic = true;
                entry.Rigidbody.velocity = Vector3.zero;
                entry.Rigidbody.angularVelocity = Vector3.zero;
            }
            if (entry.Rigidbody.interpolation != RigidbodyInterpolation.Interpolate)
            {
                entry.Rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            }
        }

        private static void EnsureRemoteDoorDynamic(DoorEntry entry)
        {
            if (entry == null || entry.Rigidbody == null)
            {
                return;
            }

            if (entry.Rigidbody.isKinematic)
            {
                entry.Rigidbody.isKinematic = false;
                entry.Rigidbody.velocity = Vector3.zero;
                entry.Rigidbody.angularVelocity = Vector3.zero;
            }
            if (entry.Rigidbody.interpolation != RigidbodyInterpolation.None)
            {
                entry.Rigidbody.interpolation = RigidbodyInterpolation.None;
            }
        }

        private void UpdateRotationSyncPolicy(DoorEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            bool skip = entry.IsVehicleDoor &&
                ((entry.Hinge != null && !entry.SkipHingeSync) || entry.AllowVehiclePlayMaker);
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
                    if (!ContainsAnyToken(hinge.transform.name, FridgeTokens) &&
                        !ContainsAnyTokenInParents(hinge.transform, FridgeTokens, 3))
                    {
                        continue;
                    }
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
            HashSet<Transform> seenDoors = new HashSet<Transform>();
            HashSet<PlayMakerFSM> seenFsms = new HashSet<PlayMakerFSM>();
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

                string fsmPath = BuildDebugPath(fsm.transform);
                string nameHint = BuildFsmNameHint(fsm);
                InteractionKind kind = DetectInteractionKind(nameHint, fsmPath);
                Transform doorTransform = kind == InteractionKind.Door
                    ? ResolveDoorTransform(fsm.gameObject.transform)
                    : fsm.transform;
                if (doorTransform == null)
                {
                    continue;
                }

                if (kind == InteractionKind.Door)
                {
                    if (seenDoors.Contains(doorTransform))
                    {
                        continue;
                    }
                }
                else
                {
                    if (seenFsms.Contains(fsm))
                    {
                        continue;
                    }
                }

                string debugPath = BuildDebugPath(doorTransform);
                bool isSorbetHatch = debugPath.IndexOf("sorbet", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    debugPath.IndexOf("hatch", StringComparison.OrdinalIgnoreCase) >= 0;
                if (useFilter && !NameMatches(doorTransform, filter) && !isSorbetHatch)
                {
                    if (kind == InteractionKind.Door)
                    {
                        continue;
                    }
                }

                if (kind == InteractionKind.Door)
                {
                    seenDoors.Add(doorTransform);
                }
                else
                {
                    seenFsms.Add(fsm);
                }

                string key = debugPath + "|pm";
                candidates.Add(new DoorCandidate { Hinge = null, DoorTransform = doorTransform, Fsm = fsm, Key = key, DebugPath = debugPath });
            }

            return candidates.Count - before;
        }

        private static bool HasAnyEventContainingTokens(PlayMakerFSM fsm, string[] tokens)
        {
            if (fsm == null || fsm.Fsm == null || tokens == null || tokens.Length == 0)
            {
                return false;
            }

            FsmEvent[] events = fsm.Fsm.Events;
            if (events == null || events.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < events.Length; i++)
            {
                FsmEvent ev = events[i];
                if (ev == null || string.IsNullOrEmpty(ev.Name))
                {
                    continue;
                }
                if (ContainsAnyToken(ev.Name, tokens))
                {
                    return true;
                }
            }

            return false;
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

            bool hasDoorSignals = PlayMakerBridge.HasAnyEvent(fsm, new[] { "OPEN", "CLOSE", "OPENDOOR", "CLOSEDOOR" }) ||
                PlayMakerBridge.FindStateByNameContains(fsm, DoorStateTokens) != null;
            bool hasTapSignals = PlayMakerBridge.HasAnyEvent(fsm, TapEventExactTokens) ||
                HasAnyEventContainingTokens(fsm, TapEventTokensOn) ||
                HasAnyEventContainingTokens(fsm, TapEventTokensOff) ||
                (PlayMakerBridge.FindStateByNameContains(fsm, TapOpenTokens) != null &&
                 PlayMakerBridge.FindStateByNameContains(fsm, TapCloseTokens) != null);
            bool hasPhoneSignals = PlayMakerBridge.FindStateByNameContains(fsm, PhoneOpenTokens) != null &&
                PlayMakerBridge.FindStateByNameContains(fsm, PhoneCloseTokens) != null;

            bool isSorbet = IsSorbetTransform(fsm.transform);
            bool isDoorName = string.Equals(fsmName, "Use", StringComparison.OrdinalIgnoreCase) ||
                ContainsAnyToken(fsmName, DoorTokens) ||
                ContainsAnyToken(fsm.gameObject.name, DoorTokens) ||
                ContainsAnyTokenInParents(fsm.transform, DoorTokens, 4);

            bool isTapName = ContainsAnyToken(fsmName, TapTokens) ||
                ContainsAnyToken(fsm.gameObject.name, TapTokens) ||
                ContainsAnyTokenInParents(fsm.transform, TapTokens, 4);

            bool isPhoneName = ContainsAnyToken(fsmName, PhoneTokens) ||
                ContainsAnyToken(fsm.gameObject.name, PhoneTokens) ||
                ContainsAnyTokenInParents(fsm.transform, PhoneTokens, 4);

            if (hasDoorSignals && isDoorName)
            {
                return true;
            }

            if (hasTapSignals && (isTapName || isSorbet))
            {
                return true;
            }

            if (hasPhoneSignals && isPhoneName)
            {
                return true;
            }

            return false;
        }

        private static bool ContainsAnyTokenInParents(Transform transform, string[] tokens, int depthLimit)
        {
            Transform current = transform;
            int depth = 0;
            while (current != null && depth < depthLimit)
            {
                if (ContainsAnyToken(current.name, tokens))
                {
                    return true;
                }
                current = current.parent;
                depth++;
            }
            return false;
        }

        private static InteractionKind DetectInteractionKind(string name, string debugPath)
        {
            if (ContainsAnyToken(name, TapTokens) || ContainsAnyToken(debugPath, TapTokens))
            {
                return InteractionKind.Tap;
            }
            if (ContainsAnyToken(name, PhoneTokens) || ContainsAnyToken(debugPath, PhoneTokens))
            {
                return InteractionKind.Phone;
            }
            return InteractionKind.Door;
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

            InteractionKind kind = entry.InteractionKind;
            if (kind == InteractionKind.Door)
            {
                kind = DetectInteractionKind(doorName, entry.DebugPath);
                entry.InteractionKind = kind;
            }

            string[] openTokens = DoorStateTokens;
            string[] closeTokens = DoorStateTokens;
            string[] openEventTokens = new[] { "open" };
            string[] closeEventTokens = new[] { "close" };
            bool allowAnyEvent = false;

            if (kind == InteractionKind.Tap)
            {
                openTokens = TapOpenTokens;
                closeTokens = TapCloseTokens;
                openEventTokens = TapEventTokensOn;
                closeEventTokens = TapEventTokensOff;
                allowAnyEvent = true;
            }
            else if (kind == InteractionKind.Phone)
            {
                openTokens = PhoneOpenTokens;
                closeTokens = PhoneCloseTokens;
                openEventTokens = PhoneEventTokensOpen;
                closeEventTokens = PhoneEventTokensClose;
                allowAnyEvent = true;
            }
            else if (entry.IsVehicleDoor)
            {
                // Vehicle doors can use non-OPEN events; keep Sorbet strict to avoid ghost opens.
                allowAnyEvent = !IsSorbetDoor(entry.DebugPath, entry.Transform);
            }

            if (kind == InteractionKind.Tap)
            {
                if (AttachPlayMakerEventOnly(fsm, entry, doorName, openEventTokens, closeEventTokens))
                {
                    return true;
                }
            }

            FsmState openState = null;
            FsmState closeState = null;
            if (kind == InteractionKind.Door)
            {
                openState = fsm.Fsm.GetState("Open door") ?? PlayMakerBridge.FindStateByNameContains(fsm, DoorStateTokens);
                closeState = fsm.Fsm.GetState("Close door") ?? PlayMakerBridge.FindStateByNameContains(fsm, DoorStateTokens);
            }
            if (kind == InteractionKind.Tap)
            {
                openState = FindStateByNameExact(fsm, new[] { "ON" }) ?? openState;
                closeState = FindStateByNameExact(fsm, new[] { "OFF" }) ?? closeState;
            }
            if (openState == null || closeState == null)
            {
                openState = openState ?? PlayMakerBridge.FindStateByNameContains(fsm, openTokens);
                closeState = closeState ?? PlayMakerBridge.FindStateByNameContains(fsm, closeTokens);
            }
            if (openState == null || closeState == null)
            {
                if (kind == InteractionKind.Tap)
                {
                    if (AttachPlayMakerEventOnly(fsm, entry, doorName, openEventTokens, closeEventTokens))
                    {
                        return true;
                    }
                }

                DebugLog.Verbose("DoorSync: PlayMaker states missing for " + kind + " " + (doorName ?? "<null>"));
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

                string openEventName = FindEventName(fsm, PlayMakerBridge.GetDefaultDoorOpenEventName(), openEventTokens);
                string closeEventName = FindEventName(fsm, PlayMakerBridge.GetDefaultDoorCloseEventName(), closeEventTokens);
                string expectedOpenEvent = allowAnyEvent ? null : (fsm.Fsm.HasEvent(openEventName) ? openEventName : null);
                string expectedCloseEvent = allowAnyEvent ? null : (fsm.Fsm.HasEvent(closeEventName) ? closeEventName : null);
                PlayMakerBridge.PrependAction(openState, new DoorPlayMakerAction(this, entry.Id, true, expectedOpenEvent));
                PlayMakerBridge.PrependAction(closeState, new DoorPlayMakerAction(this, entry.Id, false, expectedCloseEvent));
            }

            entry.Fsm = fsm;
            entry.HasPlayMaker = true;
            entry.MpOpenEventName = mpOpenEvent;
            entry.MpCloseEventName = mpCloseEvent;
            entry.OpenEventName = FindEventName(fsm, PlayMakerBridge.GetDefaultDoorOpenEventName(), openEventTokens);
            entry.CloseEventName = FindEventName(fsm, PlayMakerBridge.GetDefaultDoorCloseEventName(), closeEventTokens);
            entry.DoorOpenBool = PlayMakerBridge.FindBool(fsm, "DoorOpen") ??
                PlayMakerBridge.FindBoolByTokens(fsm, new[] { "dooropen", "isopen", "open", "on" });
            EnsureScrapeState(entry);
            if (entry.DoorOpenBool != null)
            {
                entry.LastDoorOpen = entry.DoorOpenBool.Value;
            }
            if (entry.IsVehicleDoor && !entry.SkipHingeSync)
            {
                entry.SkipRotationSync = true;
            }
            DebugLog.Verbose("DoorSync: PlayMaker hook attached to " + (doorName ?? "<null>") +
                " kind=" + entry.InteractionKind +
                " open=" + entry.OpenEventName +
                " close=" + entry.CloseEventName);
            return true;
        }

        private bool AttachPlayMakerEventOnly(PlayMakerFSM fsm, DoorEntry entry, string doorName, string[] openEventTokens, string[] closeEventTokens)
        {
            return AttachPlayMakerEventOnly(fsm, entry, doorName, openEventTokens, closeEventTokens, false);
        }

        private bool AttachPlayMakerEventOnly(PlayMakerFSM fsm, DoorEntry entry, string doorName, string[] openEventTokens, string[] closeEventTokens, bool allowAnyEventOverride)
        {
            if (entry == null || fsm == null || fsm.Fsm == null)
            {
                return false;
            }

            if (entry.Fsm == fsm && entry.HasPlayMaker)
            {
                return true;
            }

            string openEventName = FindEventName(fsm, null, openEventTokens);
            string closeEventName = FindEventName(fsm, null, closeEventTokens);
            if (string.IsNullOrEmpty(openEventName) && string.IsNullOrEmpty(closeEventName) && fsm.Fsm.HasEvent("USE"))
            {
                openEventName = "USE";
            }

            if (string.IsNullOrEmpty(openEventName) && string.IsNullOrEmpty(closeEventName))
            {
                if (!allowAnyEventOverride)
                {
                    return false;
                }

                // For special cases (e.g. BUS route FSMs), we still want to hook all events even if we don't
                // recognize open/close by tokens. Use a stable fallback if possible.
                FsmEvent[] events = fsm.Fsm.Events;
                if (events != null)
                {
                    for (int i = 0; i < events.Length; i++)
                    {
                        FsmEvent ev = events[i];
                        if (ev != null && !string.IsNullOrEmpty(ev.Name))
                        {
                            openEventName = ev.Name;
                            break;
                        }
                    }
                }
                if (string.IsNullOrEmpty(openEventName))
                {
                    return false;
                }

                closeEventName = openEventName;
            }

            if (string.IsNullOrEmpty(openEventName))
            {
                openEventName = closeEventName;
            }

            if (string.IsNullOrEmpty(closeEventName))
            {
                closeEventName = openEventName;
            }

            bool allowAnyEvent = allowAnyEventOverride ||
                (entry.InteractionKind == InteractionKind.Tap && IsSorbetDoor(entry.DebugPath, entry.Transform));

            string mpOpenEventName = "MWC_MP_OPEN";
            string mpCloseEventName = "MWC_MP_CLOSE";

            EnsureEventOnlyGlobalTransitions(fsm, openEventTokens, closeEventTokens, mpOpenEventName, mpCloseEventName);

            FsmState[] states = fsm.Fsm.States;
            if (states != null)
            {
                for (int i = 0; i < states.Length; i++)
                {
                    FsmState state = states[i];
                    if (state == null)
                    {
                        continue;
                    }
                    PlayMakerBridge.PrependAction(state, new DoorPlayMakerEventAction(this, entry.Id, openEventTokens, closeEventTokens, allowAnyEvent));
                }
            }

            entry.Fsm = fsm;
            entry.HasPlayMaker = true;
            entry.EventOnly = true;
            entry.MpOpenEventName = mpOpenEventName;
            entry.MpCloseEventName = mpCloseEventName;
            entry.OpenEventName = openEventName;
            entry.CloseEventName = closeEventName;
            entry.DoorOpenBool = PlayMakerBridge.FindBool(fsm, "DoorOpen") ??
                PlayMakerBridge.FindBoolByTokens(fsm, new[] { "dooropen", "isopen", "open", "on" });
            EnsureScrapeState(entry);
            if (entry.DoorOpenBool != null)
            {
                entry.LastDoorOpen = entry.DoorOpenBool.Value;
            }
            if (entry.IsVehicleDoor && !entry.SkipHingeSync)
            {
                entry.SkipRotationSync = true;
            }

            DebugLog.Verbose("DoorSync: PlayMaker event hook attached to " + (doorName ?? "<null>") +
                " kind=" + entry.InteractionKind +
                " open=" + entry.OpenEventName +
                " close=" + entry.CloseEventName);
            return true;
        }

        private static void EnsureEventOnlyGlobalTransitions(PlayMakerFSM fsm, string[] openTokens, string[] closeTokens, string mpOpenEventName, string mpCloseEventName)
        {
            if (fsm == null || fsm.Fsm == null)
            {
                return;
            }

            FsmState openState = FindStateByNameExact(fsm, new[] { "ON" }) ?? PlayMakerBridge.FindStateByNameContains(fsm, openTokens);
            FsmState closeState = FindStateByNameExact(fsm, new[] { "OFF" }) ?? PlayMakerBridge.FindStateByNameContains(fsm, closeTokens);

            if (openState != null)
            {
                FsmEvent mpOpen = PlayMakerBridge.GetOrCreateEvent(fsm, mpOpenEventName);
                if (!HasGlobalTransition(fsm, mpOpen))
                {
                    PlayMakerBridge.AddGlobalTransition(fsm, mpOpen, openState.Name);
                }
            }

            if (closeState != null)
            {
                FsmEvent mpClose = PlayMakerBridge.GetOrCreateEvent(fsm, mpCloseEventName);
                if (!HasGlobalTransition(fsm, mpClose))
                {
                    PlayMakerBridge.AddGlobalTransition(fsm, mpClose, closeState.Name);
                }
            }
        }

        private static bool HasGlobalTransition(PlayMakerFSM fsm, FsmEvent ev)
        {
            if (fsm == null || fsm.Fsm == null || ev == null)
            {
                return false;
            }

            FsmTransition[] transitions = fsm.FsmGlobalTransitions;
            if (transitions == null)
            {
                return false;
            }

            for (int i = 0; i < transitions.Length; i++)
            {
                FsmTransition transition = transitions[i];
                if (transition != null && transition.FsmEvent == ev)
                {
                    return true;
                }
            }

            return false;
        }

        private static void EnsureGlobalTransitionForEvent(PlayMakerFSM fsm, string eventName)
        {
            if (fsm == null || fsm.Fsm == null || string.IsNullOrEmpty(eventName))
            {
                return;
            }

            string trimmed = eventName.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return;
            }

            FsmEvent ev = PlayMakerBridge.GetOrCreateEvent(fsm, trimmed);
            if (HasGlobalTransition(fsm, ev))
            {
                return;
            }

            string[] tokens = BuildEventTokens(trimmed);
            FsmState target = PlayMakerBridge.FindStateByNameContains(fsm, tokens);
            if (target == null)
            {
                return;
            }

            PlayMakerBridge.AddGlobalTransition(fsm, ev, target.Name);
        }

        private static void EnsureGlobalTransitionForState(PlayMakerFSM fsm, string eventName, string stateName)
        {
            if (fsm == null || fsm.Fsm == null || string.IsNullOrEmpty(eventName) || string.IsNullOrEmpty(stateName))
            {
                return;
            }

            FsmEvent ev = PlayMakerBridge.GetOrCreateEvent(fsm, eventName);
            if (HasGlobalTransition(fsm, ev))
            {
                return;
            }

            PlayMakerBridge.AddGlobalTransition(fsm, ev, stateName);
        }

        private static string BuildScrapeStateEventName(string stateName)
        {
            if (string.IsNullOrEmpty(stateName))
            {
                return null;
            }

            StringBuilder builder = new StringBuilder("MP_SCRAPE_STATE_");
            for (int i = 0; i < stateName.Length; i++)
            {
                char ch = stateName[i];
                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(char.ToUpperInvariant(ch));
                }
                else
                {
                    builder.Append('_');
                }
            }
            return builder.ToString();
        }

        private static FsmState FindScrapeTargetState(PlayMakerFSM fsm, int layer, float distance)
        {
            if (fsm == null || fsm.Fsm == null)
            {
                return null;
            }

            if (distance <= ScrapeFinishDistance || layer <= 0)
            {
                FsmState finishState = PlayMakerBridge.FindStateByNameContains(fsm, ScrapeStateTokensGlass);
                if (finishState != null)
                {
                    return finishState;
                }
            }

            if (layer <= 1)
            {
                FsmState layerOne = PlayMakerBridge.FindStateByNameContains(fsm, ScrapeStateTokensLayer1);
                if (layerOne != null)
                {
                    return layerOne;
                }
            }
            else
            {
                FsmState layerTwo = PlayMakerBridge.FindStateByNameContains(fsm, ScrapeStateTokensLayer2);
                if (layerTwo != null)
                {
                    return layerTwo;
                }
            }

            return PlayMakerBridge.FindStateByNameContains(fsm, ScrapeStateTokensAny);
        }

        private bool TryForceScrapeState(DoorEntry entry, ScrapeStateData state)
        {
            if (entry == null || entry.Fsm == null)
            {
                return false;
            }

            FsmState target = FindScrapeTargetState(entry.Fsm, state.Layer, state.Distance);
            if (target == null)
            {
                return false;
            }

            bool switched = PlayMakerBridge.TrySetState(entry.Fsm, target.Name);
            if (_settings != null && _settings.VerboseLogging.Value)
            {
                float now = Time.realtimeSinceStartup;
                if (now >= _nextScrapeForceLogTime)
                {
                    DebugLog.Verbose("DoorSync: scrape force state fsm=" + entry.Fsm.FsmName +
                        " target=" + target.Name +
                        " switched=" + switched +
                        " path=" + entry.DebugPath);
                    _nextScrapeForceLogTime = now + 0.5f;
                }
            }
            if (switched)
            {
                return true;
            }

            string eventName = BuildScrapeStateEventName(target.Name);
            if (string.IsNullOrEmpty(eventName))
            {
                return false;
            }

            EnsureGlobalTransitionForState(entry.Fsm, eventName, target.Name);
            entry.Fsm.SendEvent(eventName);
            return true;
        }

        private static string[] BuildEventTokens(string eventName)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                return new string[0];
            }

            string lower = eventName.ToLowerInvariant();
            List<string> tokens = new List<string>();
            tokens.Add(lower);

            if (lower.Contains("inc") || lower.Contains("increase") || lower.Contains("plus"))
            {
                tokens.Add("inc");
                tokens.Add("increase");
                tokens.Add("plus");
            }
            if (lower.Contains("dec") || lower.Contains("decrease") || lower.Contains("minus"))
            {
                tokens.Add("dec");
                tokens.Add("decrease");
                tokens.Add("minus");
            }
            if (lower == "on" || lower.Contains("enable") || lower.Contains("start"))
            {
                tokens.Add("on");
                tokens.Add("enable");
                tokens.Add("start");
            }
            if (lower == "off" || lower.Contains("disable") || lower.Contains("stop"))
            {
                tokens.Add("off");
                tokens.Add("disable");
                tokens.Add("stop");
            }
            if (lower == "down" || lower.Contains("scrape"))
            {
                tokens.Add("down");
                tokens.Add("scrape");
            }
            if (lower == "up")
            {
                tokens.Add("up");
            }
            if (lower == "next" || lower.Contains("cycle"))
            {
                tokens.Add("next");
                tokens.Add("cycle");
            }
            if (lower == "prev" || lower == "previous" || lower.Contains("back"))
            {
                tokens.Add("prev");
                tokens.Add("previous");
                tokens.Add("back");
            }

            return tokens.ToArray();
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

            for (int t = 0; t < tokens.Length; t++)
            {
                string token = tokens[t];
                if (string.IsNullOrEmpty(token))
                {
                    continue;
                }
                for (int i = 0; i < events.Length; i++)
                {
                    FsmEvent ev = events[i];
                    if (ev == null || string.IsNullOrEmpty(ev.Name))
                    {
                        continue;
                    }
                    if (ev.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return ev.Name;
                    }
                }
            }

            return fallback;
        }

        private static FsmState FindStateByNameExact(PlayMakerFSM fsm, string[] names)
        {
            if (fsm == null || fsm.Fsm == null || names == null || names.Length == 0)
            {
                return null;
            }

            FsmState[] states = fsm.Fsm.States;
            if (states == null)
            {
                return null;
            }

            for (int i = 0; i < states.Length; i++)
            {
                FsmState state = states[i];
                if (state == null || string.IsNullOrEmpty(state.Name))
                {
                    continue;
                }
                for (int n = 0; n < names.Length; n++)
                {
                    if (string.Equals(state.Name, names[n], StringComparison.OrdinalIgnoreCase))
                    {
                        return state;
                    }
                }
            }
            return null;
        }

        private void EnqueueDoorEvent(DoorEntry entry, bool open, string eventName)
        {
            if (entry == null)
            {
                return;
            }

            DoorEventData data = new DoorEventData
            {
                DoorId = entry.Id,
                Open = open ? (byte)1 : (byte)0,
                EventName = eventName
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

            // Bus FSMs are host authoritative; clients should not rebroadcast autonomous bus FSM transitions.
            // The host will broadcast Route/Door + Route/Start events and the client will apply them.
            if (_settings != null && _settings.Mode.Value == Mode.Client && entry.EventOnly && IsBusPath(entry.DebugPath))
            {
                string fsmName = GetFsmName(entry.Fsm);
                if (string.Equals(fsmName, "Door", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fsmName, "Start", StringComparison.OrdinalIgnoreCase))
                {
                    // Keep BUS authority on host, but allow explicit rider stop-request style events
                    // so a client passenger can request stops while sharing host-driven BUS movement.
                    string normalizedBusEvent = NormalizeDoorEventName(triggerEvent);
                    if (!IsBusClientRequestEvent(normalizedBusEvent))
                    {
                        return;
                    }

                    if (_settings.VerboseLogging.Value && now >= _nextEventLogTime)
                    {
                        DebugLog.Verbose("DoorSync: forwarding client bus request id=" + entry.Id +
                            " fsm=" + fsmName +
                            " event=" + (normalizedBusEvent ?? "<null>") +
                            " path=" + entry.DebugPath);
                        _nextEventLogTime = now + 1f;
                    }
                }
            }

            bool isSorbet = IsSorbetDoor(entry.DebugPath, entry.Transform);
            if (IsSorbetDashboardControlEntry(entry))
            {
                // These controls are synchronized through SorbetDashboardStateData.
                return;
            }
            if (entry.EventOnly && !isSorbet && !IsBusPath(entry.DebugPath))
            {
                // Ignore autonomous vehicle UI FSM chatter from non-authoritative vehicles
                // (for example CARPARTS/GIFU dashboard loops). Keep EventOnly sync focused
                // on Sorbet + BUS where we have explicit bindings.
                if (entry.IsVehicleDoor || IsVehicleContextPath(entry.DebugPath))
                {
                    return;
                }
            }
            if (!string.IsNullOrEmpty(triggerEvent))
            {
                if (string.Equals(triggerEvent, entry.MpOpenEventName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(triggerEvent, entry.MpCloseEventName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            if (entry.IsVehicleDoor && entry.InteractionKind == InteractionKind.Door && !isSorbet)
            {
                if (string.IsNullOrEmpty(triggerEvent))
                {
                    return;
                }
                if (string.Equals(triggerEvent, "FINISHED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(triggerEvent, "LOOP", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(triggerEvent, "GLOBALEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string triggerLower = triggerEvent.ToLowerInvariant();
                if (triggerLower.IndexOf("open") < 0 && triggerLower.IndexOf("close") < 0)
                {
                    return;
                }
            }

            string eventName = NormalizeDoorEventName(triggerEvent);
            if (!entry.EventOnly && !string.IsNullOrEmpty(triggerEvent) && IsNoiseEventName(triggerEvent))
            {
                return;
            }

            if (IsScrapeEntry(entry))
            {
                // Scrape replication is handled via ScrapeStateData; avoid echoing events.
                return;
            }
            if (entry.EventOnly)
            {
                bool isHeater = IsSorbetHeaterButton(entry);
                bool isWindowHeater = isHeater && IsSorbetWindowHeater(entry);

                if (isHeater && !isWindowHeater)
                {
                    if (!IsHeaterStepEvent(eventName))
                    {
                        return;
                    }
                }

                bool isUseLike = !string.IsNullOrEmpty(eventName) && IsUseLikeEvent(eventName);
                if (isUseLike)
                {
                    entry.LastUseTime = now;
                    bool doorOpen = entry.DoorOpenBool != null ? entry.DoorOpenBool.Value : !entry.LastDoorOpen;
                    if (entry.DoorOpenBool != null && doorOpen == entry.LastDoorOpen)
                    {
                        doorOpen = !entry.LastDoorOpen;
                    }
                    open = doorOpen;
                    string mapped = doorOpen ? entry.OpenEventName : entry.CloseEventName;
                    if (!string.IsNullOrEmpty(mapped) && !string.Equals(mapped, eventName, StringComparison.OrdinalIgnoreCase))
                    {
                        eventName = mapped;
                    }
                }
                else if (!string.IsNullOrEmpty(eventName))
                {
                    bool isOpenCloseEvent = string.Equals(eventName, entry.OpenEventName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(eventName, entry.CloseEventName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(eventName, "ON", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(eventName, "OFF", StringComparison.OrdinalIgnoreCase);
                    bool isOffEvent = string.Equals(eventName, entry.CloseEventName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(eventName, "OFF", StringComparison.OrdinalIgnoreCase);
                    if (isOffEvent && !open && entry.LastDoorOpen == open && now - entry.LastUseTime > 0.5f)
                    {
                        return;
                    }

                    // Track explicit user toggles only when logical state changed.
                    if (isOpenCloseEvent && open != entry.LastDoorOpen)
                    {
                        entry.LastUseTime = now;
                    }
                }

                if (!string.IsNullOrEmpty(eventName))
                {
                    if (string.Equals(eventName, entry.LastEventName, StringComparison.OrdinalIgnoreCase) &&
                        open == entry.LastEventOpen &&
                        now - entry.LastEventTime < EventOnlyRepeatSeconds)
                    {
                        return;
                    }
                    entry.LastEventName = eventName;
                    entry.LastEventOpen = open;
                    entry.LastEventTime = now;
                }
            }
            if (entry.LastDoorOpen == open)
            {
                float debounce = entry.EventOnly ? 0.15f : 0.25f;
                if (now - entry.LastLocalChangeTime < debounce)
                {
                    return;
                }
            }

            if (IsBusRouteEventLocalOnly(entry, eventName))
            {
                return;
            }

            entry.LastDoorOpen = open;
            entry.LastLocalChangeTime = now;
            if (entry.EventOnly && string.IsNullOrEmpty(eventName))
            {
                return;
            }
            EnqueueDoorEvent(entry, open, eventName);
            if (_settings != null && _settings.VerboseLogging.Value && now >= _nextEventLogTime)
            {
                DebugLog.Verbose("DoorSync: local door event id=" + entry.Id +
                    " open=" + open +
                    " trigger=" + (triggerEvent ?? "<null>") +
                    " path=" + entry.DebugPath);
                _nextEventLogTime = now + 1f;
            }

            LogSorbetDetails(entry, "local", triggerEvent);
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

        public void DevForceRescan(string reason)
        {
            if (!Enabled)
            {
                return;
            }

            _pendingRescan = true;
            _rescanReason = string.IsNullOrEmpty(reason) ? "dev" : reason;
            _nextRescanTime = Time.realtimeSinceStartup;
            DebugLog.Warn("DoorSync: dev rescan requested (" + _rescanReason + ").");
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

            return lower.Contains("pivot") || lower.Contains("door") || lower.Contains("hatch") || lower.Contains("boot") || lower.Contains("lid") || lower.Contains("trunk");
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

        private bool IsBusRouteEventLocalOnly(DoorEntry entry, string eventName)
        {
            if (entry == null || string.IsNullOrEmpty(eventName))
            {
                return false;
            }

            if (!IsBusPath(entry.DebugPath))
            {
                return false;
            }

            string fsmName = GetFsmName(entry.Fsm);
            if (!string.Equals(fsmName, "Start", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fsmName, "Door", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return ContainsAnyToken(eventName, BusPaymentEventTokens);
        }

        private static bool IsBusClientRequestEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                return false;
            }

            if (string.Equals(eventName, "STOP", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return ContainsAnyToken(eventName, BusClientRequestEventTokens);
        }

        private static bool IsVehicleContextPath(string debugPath)
        {
            return ContainsAnyToken(debugPath, VehicleContextPathTokens);
        }

        private static Rigidbody FindVehicleBody(Transform doorTransform)
        {
            if (doorTransform == null)
            {
                return null;
            }

            Rigidbody[] bodies = doorTransform.GetComponentsInParent<Rigidbody>(true);
            Rigidbody fallback = null;
            for (int i = 0; i < bodies.Length; i++)
            {
                Rigidbody body = bodies[i];
                if (body == null)
                {
                    continue;
                }
                if (body.transform == doorTransform)
                {
                    continue;
                }

                if (body.GetComponent<CarDynamics>() != null ||
                    body.GetComponent<Drivetrain>() != null ||
                    body.GetComponent<Axles>() != null)
                {
                    return body;
                }

                fallback = body;
            }

            return fallback;
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

        private static string GetFsmName(PlayMakerFSM fsm)
        {
            if (fsm == null)
            {
                return string.Empty;
            }

            if (fsm.Fsm != null && !string.IsNullOrEmpty(fsm.Fsm.Name))
            {
                return fsm.Fsm.Name;
            }

            return fsm.FsmName ?? string.Empty;
        }

        private static string BuildFsmNameHint(PlayMakerFSM fsm)
        {
            if (fsm == null)
            {
                return string.Empty;
            }

            string fsmName = GetFsmName(fsm);
            string goName = fsm.gameObject != null ? fsm.gameObject.name : string.Empty;

            if (string.IsNullOrEmpty(fsmName))
            {
                return goName ?? string.Empty;
            }

            if (string.IsNullOrEmpty(goName) || string.Equals(fsmName, goName, StringComparison.OrdinalIgnoreCase))
            {
                return fsmName;
            }

            return fsmName + " " + goName;
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

        private static string NormalizeDoorEventName(string eventName)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                return null;
            }

            string trimmed = eventName.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return null;
            }

            if (IsNoiseEventName(trimmed))
            {
                return null;
            }

            return trimmed;
        }

        private static bool IsNoiseEventName(string eventName)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                return true;
            }

            string upper = eventName.ToUpperInvariant();
            if (upper == "FINISHED" || upper == "LOOP" || upper == "GLOBALEVENT" ||
                upper == "SAVE" || upper == "SAVEGAME" || upper == "LOAD" || upper == "ASSEMBLE")
            {
                return true;
            }

            if (upper.StartsWith("MWC_MP", StringComparison.OrdinalIgnoreCase) ||
                upper.StartsWith("MP_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            bool allDigits = true;
            for (int i = 0; i < upper.Length; i++)
            {
                if (!char.IsDigit(upper[i]))
                {
                    allDigits = false;
                    break;
                }
            }

            return allDigits;
        }

        private static bool IsUseLikeEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                return false;
            }

            string upper = eventName.ToUpperInvariant();
            return upper == "USE" || upper == "PRESS" || upper == "CLICK" || upper == "TOGGLE";
        }

        private static bool IsScrapeEntry(DoorEntry entry)
        {
            if (entry == null)
            {
                return false;
            }

            if (entry.Fsm != null && entry.Fsm.Fsm != null)
            {
                if (entry.Fsm.Fsm.HasEvent("SCRAPE") || HasAnyEventContainingTokens(entry.Fsm, new[] { "scrape" }))
                {
                    return true;
                }
            }

            if (entry.Fsm != null && ContainsAnyToken(GetFsmName(entry.Fsm), new[] { "scrape" }))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(entry.DebugPath) && entry.DebugPath.IndexOf("scrape", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(entry.DebugPath))
            {
                string lower = entry.DebugPath.ToLowerInvariant();
                if (lower.Contains("windshield") || lower.Contains("glass") || lower.Contains("windowpivot"))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSorbetHeaterButton(DoorEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.DebugPath))
            {
                return false;
            }

            string lower = entry.DebugPath.ToLowerInvariant();
            if (!lower.Contains("sorbet"))
            {
                return false;
            }

            return lower.Contains("buttonheater") || lower.Contains("heater") || lower.Contains("blower") || lower.Contains("windowheater");
        }

        private static bool IsSorbetDashboardControlEntry(DoorEntry entry)
        {
            if (entry == null || !entry.EventOnly || string.IsNullOrEmpty(entry.DebugPath))
            {
                return false;
            }

            string lower = entry.DebugPath.ToLowerInvariant();
            if (!lower.Contains("sorbet"))
            {
                return false;
            }

            return lower.Contains("buttonheater") ||
                lower.Contains("buttonwindowheater") ||
                lower.Contains("buttonlightmodes") ||
                lower.Contains("buttonwipers") ||
                lower.Contains("buttonhazard") ||
                lower.Contains("turnsignals") ||
                lower.Contains("ignition") ||
                lower.Contains("starter") ||
                lower.Contains("interiorlight");
        }

        private void UpdateScrapeState(DoorEntry entry, float now)
        {
            if (entry == null || !entry.HasScrapeState)
            {
                return;
            }

            // Prevent echoing remote-driven scrape values back as local updates.
            bool localScrapeRecent = entry.LastLocalScrapeTime > 0f && now - entry.LastLocalScrapeTime <= 0.35f;
            if (!localScrapeRecent &&
                (now < entry.ScrapeRemoteHoldUntil || now - entry.LastRemoteScrapeApplyTime <= 0.5f))
            {
                return;
            }

            if (entry.ScrapeInside != null &&
                now > entry.ScrapeRemoteHoldUntil &&
                now > entry.ScrapeLocalAuthorityUntil &&
                entry.ScrapeInside.Value)
            {
                entry.ScrapeInside.Value = false;
            }

            if (now < entry.ScrapeSuppressUntilTime)
            {
                return;
            }

            if (entry.ScrapeLayer == null || entry.ScrapeX == null || entry.ScrapeXold == null || entry.ScrapeDistance == null)
            {
                return;
            }

            int layer = entry.ScrapeLayer.Value;
            float x = entry.ScrapeX.Value;
            float xold = entry.ScrapeXold.Value;
            float dist = entry.ScrapeDistance.Value;
            bool inside = entry.ScrapeInside != null && entry.ScrapeInside.Value;

            // Ignore only near-idle drift when nobody is actively scraping locally.
            // Keep updates flowing even if some scrape FSMs never toggle "Inside" reliably.
            bool likelyIdleDrift = !inside &&
                !localScrapeRecent &&
                dist >= 0.75f &&
                Mathf.Abs(x) <= 0.05f &&
                Mathf.Abs(xold) <= 0.05f;
            if (likelyIdleDrift)
            {
                entry.LastScrapeLayer = layer;
                entry.LastScrapeX = x;
                entry.LastScrapeXold = xold;
                entry.LastScrapeDistance = dist;
                entry.ScrapeDirtyWhileRemote = false;
                return;
            }

            bool changed = layer != entry.LastScrapeLayer ||
                Mathf.Abs(x - entry.LastScrapeX) > 0.01f ||
                Mathf.Abs(xold - entry.LastScrapeXold) > 0.01f ||
                Mathf.Abs(dist - entry.LastScrapeDistance) > 0.001f;

            bool hasAuthority = HasScrapeAuthority(entry, now);
            if (!hasAuthority)
            {
                if (changed)
                {
                    entry.LastScrapeLayer = layer;
                    entry.LastScrapeX = x;
                    entry.LastScrapeXold = xold;
                    entry.LastScrapeDistance = dist;
                    entry.ScrapeDirtyWhileRemote = true;

                    if (_settings != null && _settings.VerboseLogging.Value && now >= _nextScrapeAuthorityLogTime)
                    {
                        DebugLog.Verbose("DoorSync: scrape change suppressed (no authority) id=" + entry.Id +
                            " layer=" + layer +
                            " x=" + x.ToString("F2") +
                            " dist=" + dist.ToString("F2") +
                            " path=" + entry.DebugPath);
                        _nextScrapeAuthorityLogTime = now + 1f;
                    }
                }
                return;
            }

            if (!changed && entry.ScrapeDirtyWhileRemote)
            {
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            entry.LastScrapeLayer = layer;
            entry.LastScrapeX = x;
            entry.LastScrapeXold = xold;
            entry.LastScrapeDistance = dist;
            if (entry.ScrapeInside != null && entry.ScrapeInside.Value)
            {
                entry.LastLocalScrapeTime = now;
            }
            entry.ScrapeLocalAuthorityUntil = now + ScrapeLocalHoldSeconds;
            entry.ScrapeDirtyWhileRemote = false;

            _scrapeStateQueue.Add(new ScrapeStateData
            {
                DoorId = entry.Id,
                Layer = layer,
                X = x,
                Xold = xold,
                Distance = dist
            });

            if (_settings != null && _settings.VerboseLogging.Value && now >= _nextScrapeLogTime)
            {
                DebugLog.Verbose("DoorSync: scrape state change id=" + entry.Id +
                    " layer=" + layer +
                    " x=" + x.ToString("F2") +
                    " dist=" + dist.ToString("F2") +
                    " path=" + entry.DebugPath);
                _nextScrapeLogTime = now + 1f;
            }
        }

        private void EnsureScrapeState(DoorEntry entry)
        {
            if (entry == null || entry.Fsm == null || entry.Fsm.Fsm == null)
            {
                return;
            }

            if (entry.HasScrapeState || !IsScrapeEntry(entry))
            {
                return;
            }

            entry.ScrapeLayer = FindIntByName(entry.Fsm, "Layer");
            entry.ScrapeX = FindFloatByName(entry.Fsm, "X");
            entry.ScrapeXold = FindFloatByName(entry.Fsm, "Xold");
            entry.ScrapeDistance = FindFloatByName(entry.Fsm, "Distance");
            entry.ScrapeInside = FindBoolByName(entry.Fsm, "Inside");

            if (entry.ScrapeLayer == null || entry.ScrapeX == null || entry.ScrapeXold == null || entry.ScrapeDistance == null)
            {
                if (_settings != null && _settings.VerboseLogging.Value)
                {
                    DebugLog.Verbose("DoorSync: scrape vars missing id=" + entry.Id +
                        " layer=" + (entry.ScrapeLayer != null) +
                        " x=" + (entry.ScrapeX != null) +
                        " xold=" + (entry.ScrapeXold != null) +
                        " dist=" + (entry.ScrapeDistance != null) +
                        " path=" + entry.DebugPath);
                }
                return;
            }

            entry.HasScrapeState = true;
            entry.LastScrapeLayer = entry.ScrapeLayer.Value;
            entry.LastScrapeX = entry.ScrapeX.Value;
            entry.LastScrapeXold = entry.ScrapeXold.Value;
            entry.LastScrapeDistance = entry.ScrapeDistance.Value;

            if (_settings != null && _settings.VerboseLogging.Value)
            {
                FsmBool inside = FindBoolByName(entry.Fsm, "Inside");
                FsmString eventNameVar = FindStringByName(entry.Fsm, "EventName");
                DebugLog.Verbose("DoorSync: scrape bind id=" + entry.Id +
                    " fsm=" + GetFsmName(entry.Fsm) +
                    " layer=" + entry.ScrapeLayer.Name +
                    " x=" + entry.ScrapeX.Name +
                    " xold=" + entry.ScrapeXold.Name +
                    " dist=" + entry.ScrapeDistance.Name +
                    " inside=" + (inside != null ? inside.Name : "<null>") +
                    " eventName=" + (eventNameVar != null ? eventNameVar.Value : "<null>") +
                    " path=" + entry.DebugPath);
            }
        }

        private bool HasScrapeAuthority(DoorEntry entry, float now)
        {
            if (entry == null)
            {
                return false;
            }

            if (!IsSorbetDoor(entry.DebugPath, entry.Transform))
            {
                return true;
            }

            // Allow sorbet scraping from any local player to avoid authority deadlocks.
            return true;
        }

        private void EnsureSorbetControlBindings()
        {
            if (_sorbetBindingsReady)
            {
                return;
            }

            _sorbetControls.Clear();
            AddSorbetControl(SorbetControl.HeaterTemp, "ButtonHeaterTemp", new[] { "temp", "heat" });
            AddSorbetControl(SorbetControl.HeaterBlower, "ButtonHeaterBlower", new[] { "blower", "fan" });
            AddSorbetControl(SorbetControl.HeaterDirection, "ButtonHeaterDirection", new[] { "direction", "vent", "defrost" });
            AddSorbetControl(SorbetControl.WindowHeater, "ButtonWindowHeater", new[] { "window", "heater", "defrost" });
            AddSorbetControl(SorbetControl.LightModes, "ButtonLightModes", new[] { "light", "mode" });
            AddSorbetControl(SorbetControl.Wipers, "ButtonWipers", new[] { "wiper" });
            AddSorbetControl(SorbetControl.Hazard, "ButtonHazard", new[] { "hazard", "indicator" });
            AddSorbetControl(SorbetControl.TurnSignals, "TurnSignals", new[] { "turn", "signal", "indicator" });
            AddSorbetControl(SorbetControl.Ignition, "IGNITION", new[] { "ignition", "key", "engine" });
            AddSorbetControl(SorbetControl.Starter, "STARTER", new[] { "starter", "start", "engine" });
            AddSorbetControl(SorbetControl.InteriorLight, "InteriorLight", new[] { "interior", "light", "lamp" });

            _sorbetBindingsReady = true;
        }

        private void AddSorbetControl(SorbetControl control, string token, string[] tokens)
        {
            DoorEntry entry = FindSorbetEntry(token);
            SorbetControlBinding binding = new SorbetControlBinding
            {
                Control = control,
                Token = token,
                Tokens = tokens,
                Entry = entry
            };

            if (entry != null && entry.Fsm != null && entry.Fsm.Fsm != null)
            {
                ResolveControlVariables(binding);
            }
            else if (_settings != null && _settings.VerboseLogging.Value)
            {
                DebugLog.Verbose("DoorSync: sorbet control missing " + token);
            }

            _sorbetControls.Add(binding);
        }

        private DoorEntry FindSorbetEntry(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            for (int i = 0; i < _doors.Count; i++)
            {
                DoorEntry entry = _doors[i];
                if (entry == null || string.IsNullOrEmpty(entry.DebugPath))
                {
                    continue;
                }
                if (entry.DebugPath.IndexOf("sorbet", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                if (entry.DebugPath.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return entry;
                }
            }
            return null;
        }

        private void ResolveControlVariables(SorbetControlBinding binding)
        {
            if (binding == null || binding.Entry == null || binding.Entry.Fsm == null)
            {
                return;
            }

            PlayMakerFSM fsm = binding.Entry.Fsm;
            binding.IntVar = FindIntByTokens(fsm, binding.Tokens);
            binding.FloatVar = FindFloatByTokens(fsm, binding.Tokens);
            binding.BoolVar = PlayMakerBridge.FindBoolByTokens(fsm, binding.Tokens) ??
                PlayMakerBridge.FindBoolByTokens(fsm, new[] { binding.Token, "on", "off", "enabled" });
        }

        private void UpdateSorbetControl(List<SorbetControlBinding> bindings, SorbetControl control, float now, out ControlValueKind kind, out float value, ref bool changed, ref byte mask, ref byte auxMask)
        {
            kind = ControlValueKind.None;
            value = 0f;

            SorbetControlBinding binding = FindBinding(bindings, control);
            if (binding == null || binding.Entry == null || binding.Entry.Fsm == null || binding.Entry.Fsm.Fsm == null)
            {
                return;
            }

            if (binding.SuppressUntilTime > now)
            {
                return;
            }

            if (!IsStateIndexControl(control) && binding.IntVar == null && binding.FloatVar == null && binding.BoolVar == null)
            {
                ResolveControlVariables(binding);
            }

            if (IsStateIndexControl(control))
            {
                // Prefer explicit vars when present; state-index is fallback only.
                if (!TryReadControlValue(binding, out kind, out value))
                {
                    int stateIndex = GetActiveStateIndex(binding.Entry.Fsm);
                    if (stateIndex >= 0)
                    {
                        kind = ControlValueKind.StateIndex;
                        value = stateIndex;
                    }
                    else
                    {
                        return;
                    }
                }
            }
            else if (!TryReadControlValue(binding, out kind, out value))
            {
                return;
            }

            SetControlMaskBit(control, ref mask, ref auxMask);

            bool valueChanged = kind != binding.LastKind || Mathf.Abs(value - binding.LastValue) > 0.001f;
            if (valueChanged)
            {
                binding.LastKind = kind;
                binding.LastValue = value;
                if (_settings != null && _settings.VerboseLogging.Value && now >= binding.NextLogTime)
                {
                    DebugLog.Verbose("DoorSync: sorbet control change " + control +
                        " kind=" + kind +
                        " value=" + value.ToString("F2") +
                        " path=" + binding.Entry.DebugPath);
                    binding.NextLogTime = now + 0.75f;
                }
                changed = true;
            }
        }

        private void ApplySorbetControl(List<SorbetControlBinding> bindings, SorbetControl control, float now, byte mask, byte auxMask, ControlValueKind kind, float value)
        {
            if (!HasControlMaskBit(control, mask, auxMask))
            {
                return;
            }

            SorbetControlBinding binding = FindBinding(bindings, control);
            if (binding == null || binding.Entry == null || binding.Entry.Fsm == null || binding.Entry.Fsm.Fsm == null)
            {
                return;
            }

            binding.SuppressUntilTime = now + 0.6f;

            if (binding.IntVar == null && binding.FloatVar == null && binding.BoolVar == null)
            {
                ResolveControlVariables(binding);
            }

            switch (kind)
            {
                case ControlValueKind.Bool:
                    if (binding.BoolVar != null)
                    {
                        binding.BoolVar.Value = value > 0.5f;
                    }
                    else if (binding.IntVar != null)
                    {
                        binding.IntVar.Value = value > 0.5f ? 1 : 0;
                    }
                    else if (binding.FloatVar != null)
                    {
                        binding.FloatVar.Value = value > 0.5f ? 1f : 0f;
                    }
                    SendToggleEvent(binding.Entry.Fsm, value > 0.5f);
                    break;
                case ControlValueKind.Int:
                    if (binding.IntVar != null)
                    {
                        binding.IntVar.Value = Mathf.RoundToInt(value);
                    }
                    else if (binding.FloatVar != null)
                    {
                        binding.FloatVar.Value = value;
                    }
                    else if (binding.BoolVar != null)
                    {
                        binding.BoolVar.Value = value > 0.5f;
                    }
                    break;
                case ControlValueKind.Float:
                    if (binding.FloatVar != null)
                    {
                        binding.FloatVar.Value = value;
                    }
                    else if (binding.IntVar != null)
                    {
                        binding.IntVar.Value = Mathf.RoundToInt(value);
                    }
                    else if (binding.BoolVar != null)
                    {
                        binding.BoolVar.Value = value > 0.5f;
                    }
                    break;
                case ControlValueKind.StateIndex:
                    TrySetStateIndex(binding.Entry.Fsm, Mathf.RoundToInt(value));
                    break;
            }

            binding.LastKind = kind;
            binding.LastValue = value;
        }

        private static void SetControlMaskBit(SorbetControl control, ref byte mask, ref byte auxMask)
        {
            int index = (int)control;
            if (index < 0)
            {
                return;
            }

            if (index < 8)
            {
                mask |= (byte)(1 << index);
                return;
            }

            int auxIndex = index - 8;
            if (auxIndex < 8)
            {
                auxMask |= (byte)(1 << auxIndex);
            }
        }

        private static bool HasControlMaskBit(SorbetControl control, byte mask, byte auxMask)
        {
            int index = (int)control;
            if (index < 0)
            {
                return false;
            }

            if (index < 8)
            {
                return (mask & (1 << index)) != 0;
            }

            int auxIndex = index - 8;
            if (auxIndex < 8)
            {
                return (auxMask & (1 << auxIndex)) != 0;
            }

            return false;
        }

        private static void SendToggleEvent(PlayMakerFSM fsm, bool on)
        {
            if (fsm == null || fsm.Fsm == null)
            {
                return;
            }

            string eventName = on ? "ON" : "OFF";
            if (fsm.Fsm.HasEvent(eventName))
            {
                fsm.SendEvent(eventName);
            }
        }

        private static SorbetControlBinding FindBinding(List<SorbetControlBinding> bindings, SorbetControl control)
        {
            if (bindings == null)
            {
                return null;
            }

            for (int i = 0; i < bindings.Count; i++)
            {
                SorbetControlBinding binding = bindings[i];
                if (binding != null && binding.Control == control)
                {
                    return binding;
                }
            }
            return null;
        }

        private static bool TryReadControlValue(SorbetControlBinding binding, out ControlValueKind kind, out float value)
        {
            kind = ControlValueKind.None;
            value = 0f;

            if (binding == null || binding.Entry == null || binding.Entry.Fsm == null || binding.Entry.Fsm.Fsm == null)
            {
                return false;
            }

            if (binding.IntVar != null)
            {
                kind = ControlValueKind.Int;
                value = binding.IntVar.Value;
                return true;
            }

            if (binding.FloatVar != null)
            {
                kind = ControlValueKind.Float;
                value = binding.FloatVar.Value;
                return true;
            }

            if (binding.BoolVar != null)
            {
                kind = ControlValueKind.Bool;
                value = binding.BoolVar.Value ? 1f : 0f;
                return true;
            }

            int stateIndex = GetActiveStateIndex(binding.Entry.Fsm);
            if (stateIndex >= 0)
            {
                kind = ControlValueKind.StateIndex;
                value = stateIndex;
                return true;
            }

            return false;
        }

        private static int GetActiveStateIndex(PlayMakerFSM fsm)
        {
            if (fsm == null || fsm.Fsm == null || fsm.Fsm.ActiveState == null)
            {
                return -1;
            }

            FsmState[] states = fsm.FsmStates;
            if (states == null)
            {
                return -1;
            }

            for (int i = 0; i < states.Length; i++)
            {
                if (states[i] == fsm.Fsm.ActiveState)
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool TrySetStateIndex(PlayMakerFSM fsm, int stateIndex)
        {
            if (fsm == null || fsm.Fsm == null)
            {
                return false;
            }

            FsmState[] states = fsm.FsmStates;
            if (states == null || stateIndex < 0 || stateIndex >= states.Length)
            {
                return false;
            }

            int currentIndex = GetActiveStateIndex(fsm);
            if (currentIndex < 0)
            {
                return false;
            }

            if (currentIndex == stateIndex)
            {
                return true;
            }

            bool forward = stateIndex > currentIndex;
            string eventName = FindStepEventName(fsm, forward);
            if (string.IsNullOrEmpty(eventName))
            {
                return false;
            }

            int steps = Mathf.Min(Mathf.Abs(stateIndex - currentIndex), 12);
            for (int i = 0; i < steps; i++)
            {
                fsm.SendEvent(eventName);
                if (GetActiveStateIndex(fsm) == stateIndex)
                {
                    return true;
                }
            }

            return GetActiveStateIndex(fsm) == stateIndex;
        }

        private static string FindStepEventName(PlayMakerFSM fsm, bool forward)
        {
            if (fsm == null || fsm.Fsm == null)
            {
                return null;
            }

            string[] candidates = forward
                ? new[] { "NEXT", "INC", "INCREASE", "PLUS", "UP", "RIGHT" }
                : new[] { "PREV", "PREVIOUS", "DEC", "DECREASE", "MINUS", "DOWN", "LEFT" };

            for (int i = 0; i < candidates.Length; i++)
            {
                string name = candidates[i];
                if (fsm.Fsm.HasEvent(name))
                {
                    return name;
                }
            }

            return null;
        }

        private static FsmInt FindIntByName(PlayMakerFSM fsm, string name)
        {
            if (fsm == null || fsm.FsmVariables == null || string.IsNullOrEmpty(name))
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
                if (value != null && string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }

            return null;
        }

        private static FsmFloat FindFloatByName(PlayMakerFSM fsm, string name)
        {
            if (fsm == null || fsm.FsmVariables == null || string.IsNullOrEmpty(name))
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
                if (value != null && string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }

            return null;
        }

        private static FsmBool FindBoolByName(PlayMakerFSM fsm, string name)
        {
            if (fsm == null || fsm.FsmVariables == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            FsmBool[] values = fsm.FsmVariables.BoolVariables;
            if (values == null)
            {
                return null;
            }

            for (int i = 0; i < values.Length; i++)
            {
                FsmBool value = values[i];
                if (value != null && string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }

            return null;
        }

        private static FsmString FindStringByName(PlayMakerFSM fsm, string name)
        {
            if (fsm == null || fsm.FsmVariables == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            FsmString[] values = fsm.FsmVariables.StringVariables;
            if (values == null)
            {
                return null;
            }

            for (int i = 0; i < values.Length; i++)
            {
                FsmString value = values[i];
                if (value != null && string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }

            return null;
        }

        private static FsmInt FindIntByTokens(PlayMakerFSM fsm, string[] tokens)
        {
            if (fsm == null || fsm.FsmVariables == null || tokens == null || tokens.Length == 0)
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

        private static FsmFloat FindFloatByTokens(PlayMakerFSM fsm, string[] tokens)
        {
            if (fsm == null || fsm.FsmVariables == null || tokens == null || tokens.Length == 0)
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

        private static bool IsSorbetWindowHeater(DoorEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.DebugPath))
            {
                return false;
            }

            return entry.DebugPath.IndexOf("windowheater", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsStateIndexControl(SorbetControl control)
        {
            return control == SorbetControl.HeaterTemp ||
                control == SorbetControl.HeaterBlower ||
                control == SorbetControl.HeaterDirection ||
                control == SorbetControl.LightModes ||
                control == SorbetControl.Wipers ||
                control == SorbetControl.TurnSignals ||
                control == SorbetControl.Ignition;
        }

        private static bool IsHeaterStepEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                return false;
            }

            string upper = eventName.ToUpperInvariant();
            return upper == "INCREASE" || upper == "DECREASE" ||
                upper == "INC" || upper == "DEC" ||
                upper == "PLUS" || upper == "MINUS" ||
                upper == "NEXT" || upper == "PREV" || upper == "PREVIOUS";
        }

        private static bool IsScrapeEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                return false;
            }

            return string.Equals(eventName, "DOWN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(eventName, "SCRAPE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(eventName, "OFF", StringComparison.OrdinalIgnoreCase);
        }

        private void LogSorbetDetails(DoorEntry entry, string context, string triggerEvent)
        {
            if (_settings == null || !_settings.VerboseLogging.Value || entry == null)
            {
                return;
            }

            if (!IsSorbetDoor(entry.DebugPath, entry.Transform) && !IsScrapeEntry(entry))
            {
                return;
            }

            if (_loggedSorbetEntries.Contains(entry.Id))
            {
                return;
            }

            _loggedSorbetEntries.Add(entry.Id);

            string fsmName = entry.Fsm != null ? GetFsmName(entry.Fsm) : "<null>";
            string activeState = entry.Fsm != null && entry.Fsm.Fsm != null ? entry.Fsm.Fsm.ActiveStateName : "<null>";
            DebugLog.Verbose("DoorSync: sorbet debug (" + context + ") id=" + entry.Id +
                " fsm=" + fsmName +
                " state=" + activeState +
                " trigger=" + (triggerEvent ?? "<null>") +
                " path=" + entry.DebugPath);

            if (entry.Fsm == null || entry.Fsm.Fsm == null)
            {
                return;
            }

            if (IsScrapeEntry(entry))
            {
                LogAllFsmVariables(entry.Fsm);
                LogFsmStatesAndEvents(entry.Fsm);
            }
            else
            {
                LogSorbetVariables(entry.Fsm, entry.Fsm.FsmVariables.BoolVariables);
                LogSorbetVariables(entry.Fsm, entry.Fsm.FsmVariables.IntVariables);
                LogSorbetVariables(entry.Fsm, entry.Fsm.FsmVariables.FloatVariables);
            }
        }

        private void LogSorbetVariables(PlayMakerFSM fsm, FsmBool[] variables)
        {
            if (fsm == null || variables == null || variables.Length == 0)
            {
                return;
            }

            for (int i = 0; i < variables.Length; i++)
            {
                FsmBool variable = variables[i];
                if (variable == null || string.IsNullOrEmpty(variable.Name))
                {
                    continue;
                }

                if (!ContainsAnyToken(variable.Name, SorbetVarTokens))
                {
                    continue;
                }

                DebugLog.Verbose("DoorSync: sorbet var bool" +
                    " fsm=" + GetFsmName(fsm) +
                    " name=" + variable.Name +
                    " value=" + variable.Value);
            }
        }

        private void LogSorbetVariables(PlayMakerFSM fsm, FsmInt[] variables)
        {
            if (fsm == null || variables == null || variables.Length == 0)
            {
                return;
            }

            for (int i = 0; i < variables.Length; i++)
            {
                FsmInt variable = variables[i];
                if (variable == null || string.IsNullOrEmpty(variable.Name))
                {
                    continue;
                }

                if (!ContainsAnyToken(variable.Name, SorbetVarTokens))
                {
                    continue;
                }

                DebugLog.Verbose("DoorSync: sorbet var int" +
                    " fsm=" + GetFsmName(fsm) +
                    " name=" + variable.Name +
                    " value=" + variable.Value);
            }
        }

        private void LogSorbetVariables(PlayMakerFSM fsm, FsmFloat[] variables)
        {
            if (fsm == null || variables == null || variables.Length == 0)
            {
                return;
            }

            for (int i = 0; i < variables.Length; i++)
            {
                FsmFloat variable = variables[i];
                if (variable == null || string.IsNullOrEmpty(variable.Name))
                {
                    continue;
                }

                if (!ContainsAnyToken(variable.Name, SorbetVarTokens))
                {
                    continue;
                }

                DebugLog.Verbose("DoorSync: sorbet var float" +
                    " fsm=" + GetFsmName(fsm) +
                    " name=" + variable.Name +
                    " value=" + variable.Value.ToString("F3", CultureInfo.InvariantCulture));
            }
        }

        private void LogAllFsmVariables(PlayMakerFSM fsm)
        {
            if (fsm == null || fsm.Fsm == null)
            {
                return;
            }

            FsmVariables vars = fsm.FsmVariables;
            if (vars == null)
            {
                return;
            }

            FsmBool[] bools = vars.BoolVariables;
            if (bools != null)
            {
                for (int i = 0; i < bools.Length; i++)
                {
                    FsmBool variable = bools[i];
                    if (variable == null || string.IsNullOrEmpty(variable.Name))
                    {
                        continue;
                    }
                    DebugLog.Verbose("DoorSync: sorbet var bool fsm=" + GetFsmName(fsm) +
                        " name=" + variable.Name +
                        " value=" + variable.Value);
                }
            }

            FsmInt[] ints = vars.IntVariables;
            if (ints != null)
            {
                for (int i = 0; i < ints.Length; i++)
                {
                    FsmInt variable = ints[i];
                    if (variable == null || string.IsNullOrEmpty(variable.Name))
                    {
                        continue;
                    }
                    DebugLog.Verbose("DoorSync: sorbet var int fsm=" + GetFsmName(fsm) +
                        " name=" + variable.Name +
                        " value=" + variable.Value);
                }
            }

            FsmFloat[] floats = vars.FloatVariables;
            if (floats != null)
            {
                for (int i = 0; i < floats.Length; i++)
                {
                    FsmFloat variable = floats[i];
                    if (variable == null || string.IsNullOrEmpty(variable.Name))
                    {
                        continue;
                    }
                    DebugLog.Verbose("DoorSync: sorbet var float fsm=" + GetFsmName(fsm) +
                        " name=" + variable.Name +
                        " value=" + variable.Value.ToString("F3", CultureInfo.InvariantCulture));
                }
            }
        }

        private void LogFsmStatesAndEvents(PlayMakerFSM fsm)
        {
            if (fsm == null || fsm.Fsm == null)
            {
                return;
            }

            FsmState[] states = fsm.Fsm.States;
            if (states != null && states.Length > 0)
            {
                List<string> names = new List<string>();
                for (int i = 0; i < states.Length && i < 20; i++)
                {
                    if (states[i] != null && !string.IsNullOrEmpty(states[i].Name))
                    {
                        names.Add(states[i].Name);
                    }
                }
                DebugLog.Verbose("DoorSync: sorbet fsm states fsm=" + GetFsmName(fsm) +
                    " states=" + string.Join(", ", names.ToArray()));
            }

            FsmEvent[] events = fsm.Fsm.Events;
            if (events != null && events.Length > 0)
            {
                List<string> names = new List<string>();
                for (int i = 0; i < events.Length && i < 20; i++)
                {
                    if (events[i] != null && !string.IsNullOrEmpty(events[i].Name))
                    {
                        names.Add(events[i].Name);
                    }
                }
                DebugLog.Verbose("DoorSync: sorbet fsm events fsm=" + GetFsmName(fsm) +
                    " events=" + string.Join(", ", names.ToArray()));
            }
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

        private void RegisterMissingDoorId(uint doorId, string context)
        {
            if (_missingDoorIds.Contains(doorId))
            {
                return;
            }
            _missingDoorIds.Add(doorId);
            DebugLog.Warn("DoorSync " + context + " " + doorId + ". Ignoring until next scene.");
            DumpDoorsOnce(context + " " + doorId);
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

        private enum InteractionKind
        {
            Door = 0,
            Tap = 1,
            Phone = 2
        }

        private enum SorbetControl
        {
            HeaterTemp = 0,
            HeaterBlower = 1,
            HeaterDirection = 2,
            WindowHeater = 3,
            LightModes = 4,
            Hazard = 5,
            Wipers = 6,
            TurnSignals = 7,
            Ignition = 8,
            Starter = 9,
            InteriorLight = 10
        }

        private enum ControlValueKind : byte
        {
            None = 0,
            Bool = 1,
            Int = 2,
            Float = 3,
            StateIndex = 4
        }

        private sealed class DoorEntry
        {
            public uint Id;
            public string Key;
            public string DebugPath;
            public Transform Transform;
            public Rigidbody Rigidbody;
            public HingeJoint Hinge;
            public Rigidbody VehicleBody;
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
            public bool EventOnly;
            public bool IsVehicleDoor;
            public bool SkipRotationSync;
            public bool SkipHingeSync;
            public bool AllowVehiclePlayMaker;
            public InteractionKind InteractionKind;
            public string MpOpenEventName;
            public string MpCloseEventName;
            public string OpenEventName;
            public string CloseEventName;
            public bool LastDoorOpen;
            public string LastEventName;
            public bool LastEventOpen;
            public float LastEventTime;
            public float LastUseTime;
            public float SuppressPlayMakerUntilTime;
            public uint LastRemoteEventSequence;
            public bool HasScrapeState;
            public FsmInt ScrapeLayer;
            public FsmFloat ScrapeX;
            public FsmFloat ScrapeXold;
            public FsmFloat ScrapeDistance;
            public FsmBool ScrapeInside;
            public int LastScrapeLayer;
            public float LastScrapeX;
            public float LastScrapeXold;
            public float LastScrapeDistance;
            public int LastRemoteScrapeLayer;
            public float LastRemoteScrapeX;
            public float LastRemoteScrapeXold;
            public float LastRemoteScrapeDistance;
            public uint LastRemoteScrapeSequence;
            public float ScrapeSuppressUntilTime;
            public float ScrapeLocalAuthorityUntil;
            public float ScrapeRemoteHoldUntil;
            public float LastRemoteScrapeApplyTime;
            public float LastLocalScrapeTime;
            public bool ScrapeDirtyWhileRemote;
        }

        private sealed class SorbetControlBinding
        {
            public SorbetControl Control;
            public string Token;
            public string[] Tokens;
            public DoorEntry Entry;
            public FsmInt IntVar;
            public FsmFloat FloatVar;
            public FsmBool BoolVar;
            public ControlValueKind LastKind;
            public float LastValue;
            public float NextLogTime;
            public float SuppressUntilTime;
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

                if (State == null || State.Fsm == null)
                {
                    return;
                }

                string trigger = null;
                if (State.Fsm.LastTransition != null)
                {
                    trigger = State.Fsm.LastTransition.EventName;
                    if (!string.IsNullOrEmpty(_expectedEvent) && !string.Equals(trigger, _expectedEvent, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
                else if (!string.IsNullOrEmpty(_expectedEvent))
                {
                    return;
                }

                if (_owner != null)
                {
                    _owner.NotifyDoorPlayMaker(_doorId, _open, trigger);
                }
            }
        }

        private sealed class DoorPlayMakerEventAction : FsmStateAction
        {
            private readonly DoorSync _owner;
            private readonly uint _doorId;
            private readonly string[] _openTokens;
            private readonly string[] _closeTokens;
            private readonly bool _allowAnyEvent;

            public DoorPlayMakerEventAction(DoorSync owner, uint doorId, string[] openTokens, string[] closeTokens, bool allowAnyEvent)
            {
                _owner = owner;
                _doorId = doorId;
                _openTokens = openTokens;
                _closeTokens = closeTokens;
                _allowAnyEvent = allowAnyEvent;
            }

            public override void OnEnter()
            {
                Finish();

                if (State == null || State.Fsm == null || State.Fsm.LastTransition == null)
                {
                    return;
                }

                string trigger = State.Fsm.LastTransition.EventName;
                if (string.IsNullOrEmpty(trigger))
                {
                    return;
                }

                if (string.Equals(trigger, "FINISHED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(trigger, "LOOP", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(trigger, "GLOBALEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (trigger.StartsWith("MWC_MP", StringComparison.OrdinalIgnoreCase) ||
                    trigger.StartsWith("MP_", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                bool isOpen = ContainsAnyToken(trigger, _openTokens);
                bool isClose = ContainsAnyToken(trigger, _closeTokens);
                if (!isOpen && !isClose)
                {
                    if (!_allowAnyEvent)
                    {
                        return;
                    }

                    if (_owner != null)
                    {
                        _owner.NotifyDoorPlayMaker(_doorId, true, trigger);
                    }
                    return;
                }

                bool open = isOpen && !isClose ? true : (!isOpen && isClose ? false : isOpen);
                if (_owner != null)
                {
                    _owner.NotifyDoorPlayMaker(_doorId, open, trigger);
                }
            }
        }
    }
}
