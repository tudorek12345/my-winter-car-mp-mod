using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Util;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    // Bus-specific seat + interaction discovery.
    // - Attaches PassengerSeatTrigger to BUS seat trigger colliders (local-only seating; remote players already sync by PlayerState).
    // - Logs potential payment-related PlayMaker FSMs (so we can wire per-player pay later without making the bus non-host-authoritative).
    internal sealed class BusSeatSync
    {
        private const float ScanIntervalSeconds = 3f;

        private static readonly string[] SeatTokens = new[]
        {
            "seat", "sit", "passenger", "chair", "penk", "penkki"
        };

        private static readonly string[] PayTokens = new[]
        {
            "pay", "ticket", "money", "cash", "coin", "lippu", "maksu", "payment"
        };

        private readonly Settings _settings;
        private readonly HashSet<int> _seatTriggerIds = new HashSet<int>();
        private readonly HashSet<int> _loggedBusNoSeats = new HashSet<int>();
        private readonly HashSet<int> _loggedFsmIds = new HashSet<int>();

        private int _lastSceneIndex = int.MinValue;
        private string _lastSceneName = string.Empty;
        private float _nextScanTime;

        public BusSeatSync(Settings settings)
        {
            _settings = settings;
        }

        public void UpdateScene(int levelIndex, string levelName, bool allowScan)
        {
            if (!allowScan)
            {
                _lastSceneIndex = levelIndex;
                _lastSceneName = levelName ?? string.Empty;
                _seatTriggerIds.Clear();
                _loggedBusNoSeats.Clear();
                _loggedFsmIds.Clear();
                _nextScanTime = 0f;
                return;
            }

            if (levelIndex != _lastSceneIndex || !string.Equals(levelName, _lastSceneName, StringComparison.Ordinal))
            {
                _lastSceneIndex = levelIndex;
                _lastSceneName = levelName ?? string.Empty;
                _seatTriggerIds.Clear();
                _loggedBusNoSeats.Clear();
                _loggedFsmIds.Clear();
                _nextScanTime = 0f;
            }
        }

        public void Update(float now)
        {
            if (_settings == null)
            {
                return;
            }

            if (now < _nextScanTime)
            {
                return;
            }
            _nextScanTime = now + ScanIntervalSeconds;

            ScanBuses();
        }

        private void ScanBuses()
        {
            bool includeInactive = _settings != null && _settings.Mode.Value == Mode.Client;
            CarDynamics[] cars = includeInactive ? Resources.FindObjectsOfTypeAll<CarDynamics>() : UnityEngine.Object.FindObjectsOfType<CarDynamics>();
            if (cars == null || cars.Length == 0)
            {
                return;
            }

            for (int i = 0; i < cars.Length; i++)
            {
                CarDynamics car = cars[i];
                if (car == null || car.gameObject == null)
                {
                    continue;
                }

                GameObject root = car.gameObject;
                if (includeInactive && root.hideFlags != HideFlags.None)
                {
                    continue;
                }

                // BUS instances are named "BUS" in current dumps/logs and live under NPC_CARS.
                if (root.name.IndexOf("bus", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                string rootPath = ObjectKeyBuilder.BuildDebugPath(root);
                if (rootPath.IndexOf("NPC_CARS", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                TryAttachSeatTriggers(root);

                if (_settings.VerboseLogging.Value)
                {
                    ScanPayFsms(root);
                }
            }
        }

        private void TryAttachSeatTriggers(GameObject busRoot)
        {
            if (busRoot == null)
            {
                return;
            }

            Collider[] colliders = busRoot.GetComponentsInChildren<Collider>(true);
            if (colliders == null || colliders.Length == 0)
            {
                return;
            }

            int attached = 0;
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider col = colliders[i];
                if (col == null || col.gameObject == null || !col.isTrigger)
                {
                    continue;
                }

                int id = col.gameObject.GetInstanceID();
                if (_seatTriggerIds.Contains(id))
                {
                    continue;
                }

                string name = col.gameObject.name ?? string.Empty;
                string path = ObjectKeyBuilder.BuildDebugPath(col.transform);
                if (!ContainsAnyToken(name, SeatTokens) && !ContainsAnyToken(path, SeatTokens))
                {
                    continue;
                }

                PassengerSeatTrigger trigger = col.gameObject.GetComponent<PassengerSeatTrigger>();
                if (trigger == null)
                {
                    trigger = col.gameObject.AddComponent<PassengerSeatTrigger>();
                    trigger.Initialize(null, 0);
                }

                _seatTriggerIds.Add(id);
                attached++;

                if (_settings != null && _settings.VerboseLogging.Value)
                {
                    DebugLog.Verbose("BusSeatSync: attached seat trigger go=" + name + " path=" + path);
                }
            }

            if (attached == 0 && _settings != null && _settings.VerboseLogging.Value)
            {
                int busId = busRoot.GetInstanceID();
                if (_loggedBusNoSeats.Add(busId))
                {
                    // Help identify real seat trigger names by logging a few trigger colliders.
                    int logged = 0;
                    for (int i = 0; i < colliders.Length && logged < 12; i++)
                    {
                        Collider col = colliders[i];
                        if (col == null || col.gameObject == null || !col.isTrigger)
                        {
                            continue;
                        }
                        string path = ObjectKeyBuilder.BuildDebugPath(col.transform);
                        DebugLog.Verbose("BusSeatSync: trigger collider name=" + col.gameObject.name + " path=" + path);
                        logged++;
                    }
                }
            }
        }

        private void ScanPayFsms(GameObject busRoot)
        {
            PlayMakerFSM[] fsms = busRoot.GetComponentsInChildren<PlayMakerFSM>(true);
            if (fsms == null || fsms.Length == 0)
            {
                return;
            }

            int logged = 0;
            for (int i = 0; i < fsms.Length && logged < 10; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.Fsm == null)
                {
                    continue;
                }

                int id = fsm.GetInstanceID();
                if (_loggedFsmIds.Contains(id))
                {
                    continue;
                }

                string fsmName = !string.IsNullOrEmpty(fsm.Fsm.Name) ? fsm.Fsm.Name : (fsm.FsmName ?? string.Empty);
                string path = ObjectKeyBuilder.BuildDebugPath(fsm.transform);
                if (!ContainsAnyToken(fsmName, PayTokens) && !ContainsAnyToken(path, PayTokens) && !ContainsAnyToken(fsm.gameObject.name, PayTokens))
                {
                    continue;
                }

                DebugLog.Verbose("BusScan: fsm=" + fsmName +
                    " state=" + (fsm.ActiveStateName ?? "<null>") +
                    " go=" + (fsm.gameObject != null ? fsm.gameObject.name : "<null>") +
                    " path=" + path);

                _loggedFsmIds.Add(id);
                logged++;
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
    }
}
