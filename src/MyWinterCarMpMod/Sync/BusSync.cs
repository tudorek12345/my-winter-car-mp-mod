using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Util;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    // Bus-specific PlayMaker discovery + sync hooks.
    // We piggyback on DoorSync's DoorEvent pipeline to sync bus Route/Door + Route/Start style FSM events.
    internal sealed class BusSync
    {
        private const float ScanIntervalSeconds = 2f;

        private readonly Settings _settings;
        private readonly DoorSync _doorSync;
        private readonly HashSet<int> _registeredFsmInstanceIds = new HashSet<int>();
        private readonly HashSet<int> _loggedBusRootInstanceIds = new HashSet<int>();

        private static readonly string[] BusInfoTokens = new[]
        {
            "bus", "route", "stop", "door", "start",
            "ticket", "pay", "fare", "money",
            "seat", "passenger"
        };

        private int _lastSceneIndex = int.MinValue;
        private string _lastSceneName = string.Empty;
        private float _nextScanTime;

        public BusSync(Settings settings, DoorSync doorSync)
        {
            _settings = settings;
            _doorSync = doorSync;
        }

        public void UpdateScene(int levelIndex, string levelName, bool allowScan)
        {
            if (!allowScan)
            {
                _lastSceneIndex = levelIndex;
                _lastSceneName = levelName ?? string.Empty;
                _registeredFsmInstanceIds.Clear();
                _loggedBusRootInstanceIds.Clear();
                _nextScanTime = 0f;
                return;
            }

            if (levelIndex != _lastSceneIndex || !string.Equals(levelName, _lastSceneName, StringComparison.Ordinal))
            {
                _lastSceneIndex = levelIndex;
                _lastSceneName = levelName ?? string.Empty;
                _registeredFsmInstanceIds.Clear();
                _loggedBusRootInstanceIds.Clear();
                _nextScanTime = 0f;
            }
        }

        public void Update(float now)
        {
            if (_settings == null || _doorSync == null)
            {
                return;
            }

            if (!_settings.DoorSyncEnabled.Value || !_settings.DoorPlayMakerEnabled.Value)
            {
                return;
            }

            if (now < _nextScanTime)
            {
                return;
            }

            _nextScanTime = now + ScanIntervalSeconds;

            bool includeInactive = _settings.Mode.Value == Mode.Client;
            CarDynamics[] cars = includeInactive ? Resources.FindObjectsOfTypeAll<CarDynamics>() : UnityEngine.Object.FindObjectsOfType<CarDynamics>();
            if (cars == null || cars.Length == 0)
            {
                return;
            }

            for (int c = 0; c < cars.Length; c++)
            {
                CarDynamics car = cars[c];
                if (car == null || car.gameObject == null)
                {
                    continue;
                }

                GameObject root = car.gameObject;
                if (includeInactive && root.hideFlags != HideFlags.None)
                {
                    continue;
                }

                if (root.name == null || root.name.IndexOf("bus", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                string rootPath = ObjectKeyBuilder.BuildDebugPath(root);
                if (rootPath.IndexOf("NPC_CARS", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                LogBusRootOnce(root.transform);

                PlayMakerFSM[] fsms = root.GetComponentsInChildren<PlayMakerFSM>(true);
                if (fsms == null || fsms.Length == 0)
                {
                    continue;
                }

                for (int i = 0; i < fsms.Length; i++)
                {
                    PlayMakerFSM fsm = fsms[i];
                    if (fsm == null || fsm.transform == null || fsm.Fsm == null)
                    {
                        continue;
                    }

                    int instanceId = fsm.GetInstanceID();
                    if (_registeredFsmInstanceIds.Contains(instanceId))
                    {
                        continue;
                    }

                    string fsmName = (fsm.Fsm != null && !string.IsNullOrEmpty(fsm.Fsm.Name))
                        ? fsm.Fsm.Name
                        : (fsm.FsmName ?? string.Empty);

                    if (!string.Equals(fsmName, "Door", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(fsmName, "Start", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string path = ObjectKeyBuilder.BuildDebugPath(fsm.transform);
                    if (!_doorSync.IsBusPath(path))
                    {
                        continue;
                    }

                    if (_doorSync.TryRegisterBusFsm(fsm, path))
                    {
                        _registeredFsmInstanceIds.Add(instanceId);
                    }
                }
            }
        }

        private void LogBusRootOnce(Transform anyChild)
        {
            if (_settings == null || !_settings.VerboseLogging.Value || anyChild == null)
            {
                return;
            }

            Transform root = FindBusRoot(anyChild);
            if (root == null)
            {
                return;
            }

            int id = root.GetInstanceID();
            if (_loggedBusRootInstanceIds.Contains(id))
            {
                return;
            }
            _loggedBusRootInstanceIds.Add(id);

            string rootPath = ObjectKeyBuilder.BuildDebugPath(root);
            DebugLog.Verbose("BusSync: bus root discovered path=" + rootPath + " name=" + root.name);

            // FSM inventory - helps us quickly locate ticket/payment/seat logic for future sync work.
            PlayMakerFSM[] fsms = root.GetComponentsInChildren<PlayMakerFSM>(true);
            if (fsms != null && fsms.Length > 0)
            {
                int logged = 0;
                for (int i = 0; i < fsms.Length && logged < 25; i++)
                {
                    PlayMakerFSM fsm = fsms[i];
                    if (fsm == null || fsm.Fsm == null)
                    {
                        continue;
                    }

                    string fsmName = !string.IsNullOrEmpty(fsm.Fsm.Name) ? fsm.Fsm.Name : (fsm.FsmName ?? string.Empty);
                    string path = ObjectKeyBuilder.BuildDebugPath(fsm.transform);
                    if (!ContainsAnyToken(path, BusInfoTokens) && !ContainsAnyToken(fsmName, BusInfoTokens))
                    {
                        continue;
                    }

                    DebugLog.Verbose("BusSync: fsm=" + fsmName +
                        " state=" + (fsm.ActiveStateName ?? "<null>") +
                        " go=" + (fsm.gameObject != null ? fsm.gameObject.name : "<null>") +
                        " path=" + path);
                    logged++;
                }
            }

            // Collider inventory - useful for finding seat triggers and payment interactables.
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            if (colliders != null && colliders.Length > 0)
            {
                int logged = 0;
                for (int i = 0; i < colliders.Length && logged < 25; i++)
                {
                    Collider c = colliders[i];
                    if (c == null)
                    {
                        continue;
                    }

                    string path = ObjectKeyBuilder.BuildDebugPath(c.transform);
                    if (!ContainsAnyToken(path, BusInfoTokens) && !ContainsAnyToken(c.name, BusInfoTokens))
                    {
                        continue;
                    }

                    DebugLog.Verbose("BusSync: collider name=" + c.name +
                        " trigger=" + c.isTrigger +
                        " path=" + path);
                    logged++;
                }
            }
        }

        private static Transform FindBusRoot(Transform start)
        {
            Transform current = start;
            int depth = 0;
            while (current != null && depth < 12)
            {
                if (current.name != null && current.name.IndexOf("BUS", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return current;
                }
                current = current.parent;
                depth++;
            }
            return null;
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
