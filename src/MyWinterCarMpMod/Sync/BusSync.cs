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
                _nextScanTime = 0f;
                return;
            }

            if (levelIndex != _lastSceneIndex || !string.Equals(levelName, _lastSceneName, StringComparison.Ordinal))
            {
                _lastSceneIndex = levelIndex;
                _lastSceneName = levelName ?? string.Empty;
                _registeredFsmInstanceIds.Clear();
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

            PlayMakerFSM[] fsms = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>();
            if (fsms == null || fsms.Length == 0)
            {
                return;
            }

            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.transform == null)
                {
                    continue;
                }

                int instanceId = fsm.GetInstanceID();
                if (_registeredFsmInstanceIds.Contains(instanceId))
                {
                    continue;
                }

                string path = ObjectKeyBuilder.BuildDebugPath(fsm.transform);
                if (!_doorSync.IsBusPath(path))
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

                if (_doorSync.TryRegisterBusFsm(fsm, path))
                {
                    _registeredFsmInstanceIds.Add(instanceId);
                }
            }
        }
    }
}

