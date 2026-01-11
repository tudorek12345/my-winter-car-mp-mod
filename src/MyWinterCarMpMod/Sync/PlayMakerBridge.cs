using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    internal static class PlayMakerBridge
    {
        private static readonly string[] DefaultDoorEventNames = new[] { "OPEN", "CLOSE" };

        public static PlayMakerFSM FindFsmByName(GameObject obj, string fsmName)
        {
            if (obj == null || string.IsNullOrEmpty(fsmName))
            {
                return null;
            }

            List<PlayMakerFSM> fsms = GatherFsms(obj);
            for (int i = 0; i < fsms.Count; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null)
                {
                    continue;
                }
                string name = GetFsmName(fsm);
                if (string.Equals(name, fsmName, StringComparison.OrdinalIgnoreCase))
                {
                    return fsm;
                }
            }
            return null;
        }

        public static PlayMakerFSM FindFsmWithStates(GameObject obj, string[] stateNames)
        {
            if (obj == null || stateNames == null || stateNames.Length == 0)
            {
                return null;
            }

            List<PlayMakerFSM> fsms = GatherFsms(obj);
            for (int i = 0; i < fsms.Count; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.Fsm == null)
                {
                    continue;
                }
                if (HasAllStates(fsm, stateNames))
                {
                    return fsm;
                }
            }
            return null;
        }

        public static FsmState FindStateByNameContains(PlayMakerFSM fsm, string[] tokens)
        {
            if (fsm == null || fsm.Fsm == null || tokens == null || tokens.Length == 0)
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
                if (NameContainsAny(state.Name, tokens))
                {
                    return state;
                }
            }
            return null;
        }

        public static bool HasAllStates(PlayMakerFSM fsm, string[] stateNames)
        {
            if (fsm == null || fsm.Fsm == null || stateNames == null || stateNames.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < stateNames.Length; i++)
            {
                if (fsm.Fsm.GetState(stateNames[i]) == null)
                {
                    return false;
                }
            }
            return true;
        }

        public static bool HasAnyEvent(PlayMakerFSM fsm, string[] eventNames)
        {
            if (fsm == null || fsm.Fsm == null || eventNames == null || eventNames.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < eventNames.Length; i++)
            {
                if (fsm.Fsm.HasEvent(eventNames[i]))
                {
                    return true;
                }
            }
            return false;
        }

        public static FsmEvent GetOrCreateEvent(PlayMakerFSM fsm, string eventName)
        {
            if (fsm == null || fsm.Fsm == null || string.IsNullOrEmpty(eventName))
            {
                return null;
            }

            FsmEvent existing = fsm.Fsm.GetEvent(eventName);
            if (existing != null)
            {
                return existing;
            }

            FsmEvent ev = FsmEvent.GetFsmEvent(eventName);
            List<FsmEvent> events = new List<FsmEvent>(fsm.Fsm.Events ?? new FsmEvent[0]);
            bool found = false;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] != null && events[i].Name == eventName)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                events.Add(ev);
                fsm.Fsm.Events = events.ToArray();
            }
            return ev;
        }

        public static void AddGlobalTransition(PlayMakerFSM fsm, FsmEvent ev, string stateName)
        {
            if (fsm == null || fsm.Fsm == null || ev == null || string.IsNullOrEmpty(stateName))
            {
                return;
            }

            FsmTransition[] oldTransitions = fsm.FsmGlobalTransitions;
            List<FsmTransition> transitions = new List<FsmTransition>();
            if (oldTransitions != null)
            {
                for (int i = 0; i < oldTransitions.Length; i++)
                {
                    transitions.Add(oldTransitions[i]);
                }
            }

            FsmTransition transition = new FsmTransition();
            transition.FsmEvent = ev;
            transition.ToState = stateName;
            transitions.Add(transition);
            fsm.Fsm.GlobalTransitions = transitions.ToArray();
        }

        public static void PrependAction(FsmState state, FsmStateAction action)
        {
            if (state == null || action == null)
            {
                return;
            }

            FsmStateAction[] oldActions = state.Actions ?? new FsmStateAction[0];
            FsmStateAction[] newActions = new FsmStateAction[oldActions.Length + 1];
            newActions[0] = action;
            for (int i = 0; i < oldActions.Length; i++)
            {
                newActions[i + 1] = oldActions[i];
            }
            state.Actions = newActions;
        }

        public static FsmBool FindBool(PlayMakerFSM fsm, string name)
        {
            if (fsm == null || fsm.Fsm == null || string.IsNullOrEmpty(name))
            {
                return null;
            }
            return fsm.FsmVariables.FindFsmBool(name);
        }

        public static FsmBool FindBoolByTokens(PlayMakerFSM fsm, string[] tokens)
        {
            if (fsm == null || fsm.Fsm == null || tokens == null || tokens.Length == 0)
            {
                return null;
            }

            FsmBool[] bools = fsm.FsmVariables.BoolVariables;
            if (bools == null)
            {
                return null;
            }

            for (int i = 0; i < bools.Length; i++)
            {
                FsmBool value = bools[i];
                if (value == null || string.IsNullOrEmpty(value.Name))
                {
                    continue;
                }
                if (NameContainsAny(value.Name, tokens))
                {
                    return value;
                }
            }
            return null;
        }

        public static string GetDefaultDoorOpenEventName()
        {
            return DefaultDoorEventNames[0];
        }

        public static string GetDefaultDoorCloseEventName()
        {
            return DefaultDoorEventNames[1];
        }

        private static List<PlayMakerFSM> GatherFsms(GameObject obj)
        {
            List<PlayMakerFSM> fsms = new List<PlayMakerFSM>();
            if (obj == null)
            {
                return fsms;
            }

            PlayMakerFSM[] local = obj.GetComponents<PlayMakerFSM>();
            if (local != null)
            {
                fsms.AddRange(local);
            }

            PlayMakerFSM[] parents = obj.GetComponentsInParent<PlayMakerFSM>(true);
            AddUnique(fsms, parents);

            PlayMakerFSM[] children = obj.GetComponentsInChildren<PlayMakerFSM>(true);
            AddUnique(fsms, children);

            return fsms;
        }

        private static void AddUnique(List<PlayMakerFSM> list, PlayMakerFSM[] items)
        {
            if (items == null)
            {
                return;
            }

            for (int i = 0; i < items.Length; i++)
            {
                PlayMakerFSM fsm = items[i];
                if (fsm == null)
                {
                    continue;
                }
                if (!list.Contains(fsm))
                {
                    list.Add(fsm);
                }
            }
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

        private static bool NameContainsAny(string value, string[] tokens)
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
    }
}
