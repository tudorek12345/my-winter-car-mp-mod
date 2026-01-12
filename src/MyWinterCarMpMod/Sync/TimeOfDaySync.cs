using System;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Net;
using MyWinterCarMpMod.Util;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    public sealed class TimeOfDaySync
    {
        private readonly Settings _settings;
        private Light _sunLight;
        private int _lastSceneIndex = int.MinValue;
        private string _lastSceneName = string.Empty;
        private bool _loggedMissing;
        private TimeStateData _lastState;
        private bool _hasLastState;
        private bool _lastStateRemote;

        public TimeOfDaySync(Settings settings)
        {
            _settings = settings;
        }

        public bool Enabled
        {
            get { return _settings != null && _settings.TimeSyncEnabled.Value; }
        }

        public void UpdateScene(int levelIndex, string levelName, bool allowScan)
        {
            if (!Enabled)
            {
                _sunLight = null;
                _loggedMissing = false;
                _hasLastState = false;
                _lastStateRemote = false;
                return;
            }

            if (!allowScan)
            {
                _sunLight = null;
                _loggedMissing = false;
                _hasLastState = false;
                _lastStateRemote = false;
                return;
            }

            if (levelIndex == _lastSceneIndex && string.Equals(levelName, _lastSceneName, StringComparison.Ordinal) && _sunLight != null)
            {
                return;
            }

            _lastSceneIndex = levelIndex;
            _lastSceneName = levelName ?? string.Empty;
            FindSunLight();
        }

        public bool TryBuildState(long unixTimeMs, uint sessionId, uint sequence, out TimeStateData state)
        {
            state = new TimeStateData();
            if (!Enabled)
            {
                return false;
            }

            if (_sunLight == null)
            {
                FindSunLight();
            }

            if (_sunLight == null)
            {
                return false;
            }

            Quaternion rot = _sunLight.transform.rotation;
            Color ambient = RenderSettings.ambientLight;

            state.SessionId = sessionId;
            state.Sequence = sequence;
            state.UnixTimeMs = unixTimeMs;
            state.RotX = rot.x;
            state.RotY = rot.y;
            state.RotZ = rot.z;
            state.RotW = rot.w;
            state.SunIntensity = _sunLight.intensity;
            state.AmbientR = ambient.r;
            state.AmbientG = ambient.g;
            state.AmbientB = ambient.b;
            state.AmbientIntensity = RenderSettings.ambientIntensity;
            _lastState = state;
            _hasLastState = true;
            _lastStateRemote = false;
            return true;
        }

        public void ApplyRemote(TimeStateData state)
        {
            if (!Enabled)
            {
                return;
            }

            if (_sunLight == null)
            {
                FindSunLight();
            }

            if (_sunLight == null)
            {
                if (!_loggedMissing)
                {
                    DebugLog.Warn("TimeSync: no directional light found for time-of-day sync.");
                    _loggedMissing = true;
                }
                return;
            }

            _sunLight.transform.rotation = new Quaternion(state.RotX, state.RotY, state.RotZ, state.RotW);
            _sunLight.intensity = state.SunIntensity;
            _lastState = state;
            _hasLastState = true;
            _lastStateRemote = true;

            if (_settings != null && _settings.TimeSyncAmbient.Value)
            {
                RenderSettings.ambientLight = new Color(state.AmbientR, state.AmbientG, state.AmbientB);
                RenderSettings.ambientIntensity = state.AmbientIntensity;
            }
        }

        public bool TryGetLastState(out TimeStateData state, out bool fromRemote)
        {
            state = _lastState;
            fromRemote = _lastStateRemote;
            return _hasLastState;
        }

        private void FindSunLight()
        {
            _sunLight = null;
            string filter = _settings != null ? (_settings.TimeSyncLightFilter.Value ?? string.Empty) : string.Empty;

            Light[] lights = UnityEngine.Object.FindObjectsOfType<Light>();
            if (lights == null || lights.Length == 0)
            {
                LogMissingLight();
                return;
            }

            if (!string.IsNullOrEmpty(filter))
            {
                for (int i = 0; i < lights.Length; i++)
                {
                    Light light = lights[i];
                    if (light == null || light.type != LightType.Directional)
                    {
                        continue;
                    }
                    if (light.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _sunLight = light;
                        break;
                    }
                }
            }

            if (_sunLight == null)
            {
                float bestScore = -1f;
                for (int i = 0; i < lights.Length; i++)
                {
                    Light light = lights[i];
                    if (light == null || light.type != LightType.Directional)
                    {
                        continue;
                    }
                    float score = light.intensity;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        _sunLight = light;
                    }
                }
            }

            if (_sunLight != null)
            {
                _loggedMissing = false;
                DebugLog.Verbose("TimeSync: using directional light '" + _sunLight.name + "' for scene " + _lastSceneName + ".");
            }
            else
            {
                LogMissingLight();
            }
        }

        private void LogMissingLight()
        {
            if (_loggedMissing)
            {
                return;
            }
            _loggedMissing = true;
            DebugLog.Warn("TimeSync: no directional light found for scene " + _lastSceneName + ".");
        }
    }
}
