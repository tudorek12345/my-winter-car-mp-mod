using System;
using System.Collections.Generic;
using System.Text;
using HutongGames.PlayMaker;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Util;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    public sealed class RadioScanner
    {
        private readonly Settings _settings;
        private readonly HashSet<int> _loggedFsmIds = new HashSet<int>();
        private readonly HashSet<int> _loggedAudioIds = new HashSet<int>();
        private int _lastSceneIndex = int.MinValue;
        private string _lastSceneName = string.Empty;
        private float _nextScanTime;
        private const float ScanIntervalSeconds = 3f;

        private static readonly string[] RadioTokens = new[]
        {
            "radio", "stereo", "music", "cassette", "tape", "playlist", "song",
            "volume", "station", "freq", "speaker"
        };

        public RadioScanner(Settings settings)
        {
            _settings = settings;
        }

        public void UpdateScene(int levelIndex, string levelName, bool allowScan)
        {
            if (!allowScan)
            {
                _lastSceneIndex = levelIndex;
                _lastSceneName = levelName ?? string.Empty;
                _loggedFsmIds.Clear();
                _loggedAudioIds.Clear();
                return;
            }

            if (levelIndex != _lastSceneIndex || !string.Equals(levelName, _lastSceneName, StringComparison.Ordinal))
            {
                _lastSceneIndex = levelIndex;
                _lastSceneName = levelName ?? string.Empty;
                _loggedFsmIds.Clear();
                _loggedAudioIds.Clear();
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

            ScanAudioSources();
            ScanFsms();
        }

        private void ScanAudioSources()
        {
            AudioSource[] sources = UnityEngine.Object.FindObjectsOfType<AudioSource>();
            if (sources == null || sources.Length == 0)
            {
                return;
            }

            int logged = 0;
            for (int i = 0; i < sources.Length && logged < 20; i++)
            {
                AudioSource source = sources[i];
                if (source == null)
                {
                    continue;
                }

                int id = source.GetInstanceID();
                if (_loggedAudioIds.Contains(id))
                {
                    continue;
                }

                string path = BuildPath(source.transform);
                string clipName = source.clip != null ? source.clip.name : string.Empty;

                bool isSorbet = path.IndexOf("sorbet", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isSorbet && !MatchesRadio(source.name) && !MatchesRadio(clipName) && !MatchesRadio(path))
                {
                    continue;
                }

                DebugLog.Verbose("RadioScan: audio name=" + source.name +
                    " clip=" + (string.IsNullOrEmpty(clipName) ? "<none>" : clipName) +
                    " playing=" + source.isPlaying +
                    " volume=" + source.volume.ToString("F2") +
                    " pitch=" + source.pitch.ToString("F2") +
                    " spatialBlend=" + source.spatialBlend.ToString("F2") +
                    " path=" + path);

                _loggedAudioIds.Add(id);
                logged++;
            }
        }

        private void ScanFsms()
        {
            PlayMakerFSM[] fsms = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>();
            if (fsms == null || fsms.Length == 0)
            {
                return;
            }

            int logged = 0;
            for (int i = 0; i < fsms.Length && logged < 20; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.Fsm == null || fsm.FsmVariables == null)
                {
                    continue;
                }

                int id = fsm.GetInstanceID();
                if (_loggedFsmIds.Contains(id))
                {
                    continue;
                }

                string path = BuildPath(fsm.transform);
                string fsmName = !string.IsNullOrEmpty(fsm.Fsm.Name) ? fsm.Fsm.Name : (fsm.FsmName ?? string.Empty);

                bool isSorbet = path.IndexOf("sorbet", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isSorbet && !MatchesRadio(fsmName) && !MatchesRadio(fsm.gameObject != null ? fsm.gameObject.name : string.Empty) && !MatchesRadio(path))
                {
                    continue;
                }

                StringBuilder vars = new StringBuilder();
                AppendVariables(vars, fsm.FsmVariables.BoolVariables);
                AppendVariables(vars, fsm.FsmVariables.IntVariables);
                AppendVariables(vars, fsm.FsmVariables.FloatVariables);
                AppendVariables(vars, fsm.FsmVariables.StringVariables);

                if (vars.Length == 0 && !MatchesRadio(fsmName) && !MatchesRadio(path))
                {
                    // If nothing inside looks radio-ish, skip to reduce noise.
                    continue;
                }

                DebugLog.Verbose("RadioScan: fsm=" + fsmName +
                    " state=" + (fsm.ActiveStateName ?? "<null>") +
                    " go=" + (fsm.gameObject != null ? fsm.gameObject.name : "<null>") +
                    " path=" + path +
                    (vars.Length > 0 ? (" vars=" + vars.ToString()) : string.Empty));

                _loggedFsmIds.Add(id);
                logged++;
            }
        }

        private static void AppendVariables(StringBuilder builder, FsmBool[] vars)
        {
            AppendVariablesInternal(builder, vars, "b");
        }

        private static void AppendVariables(StringBuilder builder, FsmInt[] vars)
        {
            AppendVariablesInternal(builder, vars, "i");
        }

        private static void AppendVariables(StringBuilder builder, FsmFloat[] vars)
        {
            AppendVariablesInternal(builder, vars, "f");
        }

        private static void AppendVariables(StringBuilder builder, FsmString[] vars)
        {
            AppendVariablesInternal(builder, vars, "s");
        }

        private static void AppendVariablesInternal(StringBuilder builder, NamedVariable[] vars, string prefix)
        {
            if (builder == null || vars == null || vars.Length == 0)
            {
                return;
            }

            int added = 0;
            for (int i = 0; i < vars.Length && added < 12; i++)
            {
                NamedVariable v = vars[i];
                if (v == null || string.IsNullOrEmpty(v.Name))
                {
                    continue;
                }

                if (!MatchesRadio(v.Name))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(",");
                }
                builder.Append(prefix);
                builder.Append(":");
                builder.Append(v.Name);
                added++;
            }
        }

        private static bool MatchesRadio(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            string lower = value.ToLowerInvariant();
            for (int i = 0; i < RadioTokens.Length; i++)
            {
                if (lower.Contains(RadioTokens[i]))
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
            while (current != null && depth < 10)
            {
                path = current.name + "/" + path;
                current = current.parent;
                depth++;
            }
            return path;
        }
    }
}
