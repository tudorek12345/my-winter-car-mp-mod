using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Util;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    public sealed class WeatherScanner
    {
        private readonly Settings _settings;
        private readonly HashSet<int> _loggedParticleIds = new HashSet<int>();
        private readonly HashSet<int> _loggedFsmIds = new HashSet<int>();
        private int _lastSceneIndex = int.MinValue;
        private string _lastSceneName = string.Empty;
        private float _nextScanTime;
        private const float ScanIntervalSeconds = 3f;

        private static readonly string[] WeatherTokens = new[]
        {
            "snow", "flake", "blizzard", "storm", "rain", "weather", "hail", "sleet"
        };

        public WeatherScanner(Settings settings)
        {
            _settings = settings;
        }

        public void UpdateScene(int levelIndex, string levelName, bool allowScan)
        {
            if (!allowScan)
            {
                _lastSceneIndex = levelIndex;
                _lastSceneName = levelName ?? string.Empty;
                _loggedParticleIds.Clear();
                _loggedFsmIds.Clear();
                return;
            }

            if (levelIndex != _lastSceneIndex || !string.Equals(levelName, _lastSceneName, StringComparison.Ordinal))
            {
                _lastSceneIndex = levelIndex;
                _lastSceneName = levelName ?? string.Empty;
                _loggedParticleIds.Clear();
                _loggedFsmIds.Clear();
                _nextScanTime = 0f;
            }
        }

        public void Update(float now)
        {
            if (_settings == null || !_settings.VerboseLogging.Value)
            {
                return;
            }

            if (now < _nextScanTime)
            {
                return;
            }

            _nextScanTime = now + ScanIntervalSeconds;
            ScanParticles();
            ScanFsms();
        }

        private void ScanParticles()
        {
            ParticleSystem[] systems = UnityEngine.Object.FindObjectsOfType<ParticleSystem>();
            if (systems == null || systems.Length == 0)
            {
                return;
            }

            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem system = systems[i];
                if (system == null)
                {
                    continue;
                }

                if (_loggedParticleIds.Contains(system.GetInstanceID()))
                {
                    continue;
                }

                string path = BuildPath(system.transform);
                if (!MatchesWeather(path) && !MatchesWeather(system.name))
                {
                    continue;
                }

                bool emissionEnabled = system.enableEmission;
                float rate = system.emissionRate;

                DebugLog.Verbose("WeatherScan: particle name=" + system.name +
                    " playing=" + system.isPlaying +
                    " active=" + system.gameObject.activeInHierarchy +
                    " emission=" + emissionEnabled +
                    " rate=" + rate.ToString("F2") +
                    " path=" + path);

                _loggedParticleIds.Add(system.GetInstanceID());
            }
        }

        private void ScanFsms()
        {
            PlayMakerFSM[] fsms = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>();
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

                if (_loggedFsmIds.Contains(fsm.GetInstanceID()))
                {
                    continue;
                }

                string path = BuildPath(fsm.transform);
                string name = fsm.FsmName ?? string.Empty;
                if (!MatchesWeather(path) && !MatchesWeather(name) && !MatchesWeather(fsm.name))
                {
                    continue;
                }

                DebugLog.Verbose("WeatherScan: fsm=" + name +
                    " state=" + (fsm.ActiveStateName ?? "<null>") +
                    " go=" + fsm.gameObject.name +
                    " path=" + path);

                _loggedFsmIds.Add(fsm.GetInstanceID());
            }
        }

        private static bool MatchesWeather(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            string lower = value.ToLowerInvariant();
            for (int i = 0; i < WeatherTokens.Length; i++)
            {
                if (lower.Contains(WeatherTokens[i]))
                {
                    return true;
                }
            }
            return false;
        }

        private static string BuildPath(Transform transform)
        {
            if (transform == null)
            {
                return "<null>";
            }

            string path = transform.name;
            Transform current = transform.parent;
            int depth = 0;
            while (current != null && depth < 8)
            {
                path = current.name + "/" + path;
                current = current.parent;
                depth++;
            }
            return path;
        }
    }
}
