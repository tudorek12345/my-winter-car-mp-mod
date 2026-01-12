using System;
using System.Text;
using HutongGames.PlayMaker;
using MyWinterCarMpMod.Util;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    internal static class PlayMakerScanner
    {
        private static readonly string[] MatchTokens = new[]
        {
            "sink", "tap", "faucet", "phone", "telephone", "fridge", "freezer", "refrigerator", "icebox"
        };

        private const int MaxNames = 12;
        private static int _lastSceneIndex = int.MinValue;
        private static string _lastSceneName = string.Empty;

        public static void ScanInteractiveFsms(int levelIndex, string levelName, bool allowScan)
        {
            if (!allowScan)
            {
                return;
            }

            if (levelIndex == _lastSceneIndex && string.Equals(levelName, _lastSceneName, StringComparison.Ordinal))
            {
                return;
            }

            _lastSceneIndex = levelIndex;
            _lastSceneName = levelName ?? string.Empty;

            PlayMakerFSM[] fsms = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>();
            int total = fsms != null ? fsms.Length : 0;
            if (total == 0)
            {
                DebugLog.Verbose("PlayMakerScanner: no FSMs found in scene " + _lastSceneName);
                return;
            }

            int matches = 0;
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (!IsMatch(fsm))
                {
                    continue;
                }

                matches++;
                string fsmName = GetFsmName(fsm);
                string path = BuildDebugPath(fsm != null ? fsm.transform : null);
                string stateNames = JoinStateNames(fsm != null ? fsm.Fsm : null);
                string eventNames = JoinEventNames(fsm != null ? fsm.Fsm : null);

                DebugLog.Verbose("PlayMakerScanner: match fsm=" + fsmName +
                    " go=" + (fsm != null ? fsm.gameObject.name : "<null>") +
                    " path=" + path +
                    " states=" + stateNames +
                    " events=" + eventNames);
            }

            DebugLog.Info("PlayMakerScanner: matched " + matches + " FSM(s) of " + total + " in scene " + _lastSceneName);
        }

        private static bool IsMatch(PlayMakerFSM fsm)
        {
            if (fsm == null)
            {
                return false;
            }

            string fsmName = GetFsmName(fsm);
            if (NameContainsAny(fsmName, MatchTokens) || NameContainsAny(fsm.gameObject.name, MatchTokens))
            {
                return true;
            }

            Transform current = fsm.transform;
            int depth = 0;
            while (current != null && depth < 4)
            {
                if (NameContainsAny(current.name, MatchTokens))
                {
                    return true;
                }
                current = current.parent;
                depth++;
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
            return fsm.FsmName;
        }

        private static string JoinStateNames(Fsm fsm)
        {
            if (fsm == null || fsm.States == null || fsm.States.Length == 0)
            {
                return "<none>";
            }

            int count = Mathf.Min(MaxNames, fsm.States.Length);
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(fsm.States[i].Name);
            }
            if (fsm.States.Length > count)
            {
                builder.Append("...");
            }
            return builder.ToString();
        }

        private static string JoinEventNames(Fsm fsm)
        {
            if (fsm == null || fsm.Events == null || fsm.Events.Length == 0)
            {
                return "<none>";
            }

            int count = Mathf.Min(MaxNames, fsm.Events.Length);
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(fsm.Events[i].Name);
            }
            if (fsm.Events.Length > count)
            {
                builder.Append("...");
            }
            return builder.ToString();
        }

        private static bool NameContainsAny(string value, string[] tokens)
        {
            if (string.IsNullOrEmpty(value) || tokens == null || tokens.Length == 0)
            {
                return false;
            }

            string lower = value.ToLowerInvariant();
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (!string.IsNullOrEmpty(token) && lower.Contains(token))
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
                return "<null>";
            }

            string[] parts = new string[12];
            int count = 0;
            Transform current = transform;
            while (current != null && count < parts.Length)
            {
                parts[count++] = current.name;
                current = current.parent;
            }

            if (count == 0)
            {
                return "<null>";
            }

            StringBuilder builder = new StringBuilder();
            for (int i = count - 1; i >= 0; i--)
            {
                if (builder.Length > 0)
                {
                    builder.Append("/");
                }
                builder.Append(parts[i]);
            }
            if (current != null)
            {
                builder.Append("/...");
            }
            return builder.ToString();
        }
    }
}
