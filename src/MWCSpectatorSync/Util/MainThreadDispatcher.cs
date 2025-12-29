using System;
using System.Collections.Generic;
using UnityEngine;

namespace MWCSpectatorSync.Util
{
    public sealed class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private static readonly Queue<Action> Actions = new Queue<Action>();
        private static readonly object LockObj = new object();

        public static bool IsReady
        {
            get { return _instance != null; }
        }

        public static void Initialize()
        {
            if (_instance != null)
            {
                return;
            }
            GameObject obj = new GameObject("MWC.MainThreadDispatcher");
            DontDestroyOnLoad(obj);
            _instance = obj.AddComponent<MainThreadDispatcher>();
        }

        public static void Enqueue(Action action)
        {
            if (action == null)
            {
                return;
            }
            lock (LockObj)
            {
                Actions.Enqueue(action);
            }
        }

        private void Update()
        {
            ExecutePending();
        }

        public static void ExecutePending()
        {
            while (true)
            {
                Action action = null;
                lock (LockObj)
                {
                    if (Actions.Count > 0)
                    {
                        action = Actions.Dequeue();
                    }
                }

                if (action == null)
                {
                    break;
                }
                action();
            }
        }
    }
}
