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
        private static readonly string[] VehicleDoorTokens = new[]
        {
            "door", "hatch", "boot", "lid"
        };

        private const int MaxNames = 12;
        private static int _lastSceneIndex = int.MinValue;
        private static string _lastSceneName = string.Empty;
        private static int _lastVehicleDoorSceneIndex = int.MinValue;
        private static string _lastVehicleDoorSceneName = string.Empty;
        private static int _lastScrapeSceneIndex = int.MinValue;
        private static string _lastScrapeSceneName = string.Empty;

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

        public static void ScanVehicleDoorFsms(int levelIndex, string levelName, bool allowScan, string vehicleToken)
        {
            if (!allowScan || string.IsNullOrEmpty(vehicleToken))
            {
                return;
            }

            if (levelIndex == _lastVehicleDoorSceneIndex && string.Equals(levelName, _lastVehicleDoorSceneName, StringComparison.Ordinal))
            {
                return;
            }

            _lastVehicleDoorSceneIndex = levelIndex;
            _lastVehicleDoorSceneName = levelName ?? string.Empty;

            PlayMakerFSM[] fsms = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>();
            int total = fsms != null ? fsms.Length : 0;
            if (total == 0)
            {
                DebugLog.Verbose("PlayMakerScanner: no FSMs found for vehicle scan in scene " + _lastVehicleDoorSceneName);
                return;
            }

            string vehicleTokenLower = vehicleToken.ToLowerInvariant();
            int matches = 0;
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null)
                {
                    continue;
                }

                string path = BuildDebugPath(fsm.transform);
                if (!path.ToLowerInvariant().Contains(vehicleTokenLower))
                {
                    continue;
                }

                string fsmName = GetFsmName(fsm);
                if (!NameContainsAny(fsmName, VehicleDoorTokens) &&
                    !NameContainsAny(fsm.gameObject != null ? fsm.gameObject.name : string.Empty, VehicleDoorTokens) &&
                    !NameContainsAny(path, VehicleDoorTokens))
                {
                    continue;
                }

                matches++;
                string stateNames = JoinStateNames(fsm != null ? fsm.Fsm : null);
                string eventNames = JoinEventNames(fsm != null ? fsm.Fsm : null);

                DebugLog.Verbose("PlayMakerScanner: vehicle door fsm=" + fsmName +
                    " go=" + (fsm != null ? fsm.gameObject.name : "<null>") +
                    " path=" + path +
                    " states=" + stateNames +
                    " events=" + eventNames);
            }

            DebugLog.Info("PlayMakerScanner: matched " + matches + " vehicle door FSM(s) in scene " + _lastVehicleDoorSceneName);
        }

        public static void ScanScrapeFsms(int levelIndex, string levelName, bool allowScan, string vehicleToken)
        {
            if (!allowScan)
            {
                return;
            }

            if (levelIndex == _lastScrapeSceneIndex && string.Equals(levelName, _lastScrapeSceneName, StringComparison.Ordinal))
            {
                return;
            }

            _lastScrapeSceneIndex = levelIndex;
            _lastScrapeSceneName = levelName ?? string.Empty;

            PlayMakerFSM[] fsms = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>();
            int total = fsms != null ? fsms.Length : 0;
            if (total == 0)
            {
                DebugLog.Verbose("PlayMakerScanner: no FSMs found for scrape scan in scene " + _lastScrapeSceneName);
                return;
            }

            string vehicleTokenLower = string.IsNullOrEmpty(vehicleToken) ? string.Empty : vehicleToken.ToLowerInvariant();
            int matches = 0;
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.Fsm == null || fsm.FsmVariables == null)
                {
                    continue;
                }

                string path = BuildDebugPath(fsm.transform);
                if (!string.IsNullOrEmpty(vehicleTokenLower) && !path.ToLowerInvariant().Contains(vehicleTokenLower))
                {
                    continue;
                }

                FsmInt layer;
                FsmFloat x;
                FsmFloat xold;
                FsmFloat dist;
                string scrapeLabel;
                if (!HasScrapeSignature(fsm, path, out layer, out x, out xold, out dist, out scrapeLabel))
                {
                    continue;
                }

                FsmBool inside = FindBoolByName(fsm, "Inside");
                FsmString eventNameVar = FindStringByName(fsm, "EventName");

                matches++;
                string fsmName = GetFsmName(fsm);
                string stateNames = JoinStateNames(fsm.Fsm);
                string eventNames = JoinEventNames(fsm.Fsm);
                string labelNote = string.IsNullOrEmpty(scrapeLabel) ? "<none>" : scrapeLabel;

                DebugLog.Verbose("PlayMakerScanner: scrape fsm=" + fsmName +
                    " go=" + (fsm.gameObject != null ? fsm.gameObject.name : "<null>") +
                    " path=" + path +
                    " label=" + labelNote +
                    " layer=" + (layer != null ? layer.Value.ToString() : "null") +
                    " x=" + (x != null ? x.Value.ToString("F2") : "null") +
                    " xold=" + (xold != null ? xold.Value.ToString("F2") : "null") +
                    " dist=" + (dist != null ? dist.Value.ToString("F2") : "null") +
                    " inside=" + (inside != null ? inside.Value.ToString() : "null") +
                    " eventName=" + (eventNameVar != null ? eventNameVar.Value : "<null>") +
                    " states=" + stateNames +
                    " events=" + eventNames);
            }

            DebugLog.Info("PlayMakerScanner: matched " + matches + " scrape FSM(s) in scene " + _lastScrapeSceneName);
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

        private static bool HasScrapeSignature(PlayMakerFSM fsm, string path, out FsmInt layer, out FsmFloat x, out FsmFloat xold, out FsmFloat dist, out string scrapeLabel)
        {
            layer = null;
            x = null;
            xold = null;
            dist = null;
            scrapeLabel = null;

            if (fsm == null || fsm.Fsm == null || fsm.FsmVariables == null)
            {
                return false;
            }

            if (!HasScrapeEvent(fsm))
            {
                return false;
            }

            layer = FindIntByName(fsm, "Layer");
            x = FindFloatByName(fsm, "X");
            xold = FindFloatByName(fsm, "Xold");
            dist = FindFloatByName(fsm, "Distance");

            if (layer == null || x == null || xold == null || dist == null)
            {
                return false;
            }

            scrapeLabel = FindStringValueContains(fsm, "SCRAPE WINDOW");
            if (!string.IsNullOrEmpty(scrapeLabel))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(path))
            {
                string lower = path.ToLowerInvariant();
                if (lower.Contains("window") || lower.Contains("windshield") || lower.Contains("glass"))
                {
                    return true;
                }
            }

            string fsmName = GetFsmName(fsm);
            return fsmName.IndexOf("scrape", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasScrapeEvent(PlayMakerFSM fsm)
        {
            if (fsm == null || fsm.Fsm == null || fsm.Fsm.Events == null)
            {
                return false;
            }

            FsmEvent[] events = fsm.Fsm.Events;
            for (int i = 0; i < events.Length; i++)
            {
                FsmEvent ev = events[i];
                if (ev == null || string.IsNullOrEmpty(ev.Name))
                {
                    continue;
                }
                if (string.Equals(ev.Name, "SCRAPE", StringComparison.OrdinalIgnoreCase) ||
                    ev.Name.IndexOf("scrape", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
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

        private static string FindStringValueContains(PlayMakerFSM fsm, string token)
        {
            if (fsm == null || fsm.FsmVariables == null || string.IsNullOrEmpty(token))
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
                if (value == null || string.IsNullOrEmpty(value.Value))
                {
                    continue;
                }
                if (value.Value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return value.Value;
                }
            }

            return null;
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
