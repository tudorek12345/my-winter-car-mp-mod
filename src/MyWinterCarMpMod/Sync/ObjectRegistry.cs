using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using BepInEx;
using MyWinterCarMpMod.Util;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    internal enum SyncObjectType
    {
        Unknown = 0,
        Door = 1,
        Vehicle = 2,
        Pickupable = 3,
        Player = 4,
        World = 5
    }

    internal sealed class ObjectRegistry
    {
        private readonly Dictionary<uint, ObjectEntry> _entries = new Dictionary<uint, ObjectEntry>();
        private bool _dumped;

        public ICollection<ObjectEntry> Entries
        {
            get { return _entries.Values; }
        }

        public void Clear()
        {
            _entries.Clear();
            _dumped = false;
        }

        public bool Register(GameObject obj, SyncObjectType type, string key, string debugPath, out uint id)
        {
            id = 0;
            if (obj == null || string.IsNullOrEmpty(key))
            {
                return false;
            }

            id = ObjectKeyBuilder.HashKey(key);
            if (_entries.ContainsKey(id))
            {
                string originalKey = key;
                int suffix = 1;
                while (_entries.ContainsKey(id) && suffix < 10)
                {
                    key = originalKey + "|h" + suffix;
                    id = ObjectKeyBuilder.HashKey(key);
                    suffix++;
                }
                if (_entries.ContainsKey(id))
                {
                    DebugLog.Warn("ObjectRegistry: duplicate id for key " + originalKey + " (skipping).");
                    return false;
                }
            }

            ObjectEntry entry = new ObjectEntry
            {
                Id = id,
                Key = key,
                DebugPath = debugPath ?? string.Empty,
                Type = type,
                GameObject = obj
            };
            _entries[id] = entry;
            return true;
        }

        public bool TryGet(uint id, out ObjectEntry entry)
        {
            return _entries.TryGetValue(id, out entry);
        }

        public void DumpOnce(string reason, string sceneName, int sceneIndex)
        {
            if (_dumped)
            {
                return;
            }
            _dumped = true;

            try
            {
                string root = Paths.BepInExRootPath;
                string scene = string.IsNullOrEmpty(sceneName) ? "UnknownScene" : sceneName.Replace(" ", string.Empty);
                string fileName = "ObjectRegistry_" + scene + "_" + Process.GetCurrentProcess().Id + ".log";
                string path = Path.Combine(root, fileName);
                using (StreamWriter writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false)))
                {
                    writer.WriteLine("ObjectRegistry dump: " + reason);
                    writer.WriteLine("Scene: " + sceneName + " Index=" + sceneIndex);
                    writer.WriteLine("Count: " + _entries.Count);
                    foreach (ObjectEntry entry in _entries.Values)
                    {
                        string name = entry.GameObject != null ? entry.GameObject.name : "<null>";
                        writer.WriteLine(entry.Id + " type=" + entry.Type + " key=" + entry.Key + " path=" + entry.DebugPath + " name=" + name);
                    }
                }
                DebugLog.Warn("ObjectRegistry: dumped entries to " + path);
            }
            catch (Exception ex)
            {
                DebugLog.Warn("ObjectRegistry dump failed: " + ex.Message);
            }
        }
    }

    internal sealed class ObjectEntry
    {
        public uint Id;
        public string Key;
        public string DebugPath;
        public SyncObjectType Type;
        public GameObject GameObject;
    }

    internal static class ObjectKeyBuilder
    {
        public static string BuildKey(GameObject obj)
        {
            if (obj == null)
            {
                return string.Empty;
            }

            Transform transform = obj.transform;
            return BuildDebugPath(transform);
        }

        public static string BuildTypedKey(GameObject obj, string typeTag)
        {
            if (obj == null)
            {
                return string.Empty;
            }

            string tag = string.IsNullOrEmpty(typeTag) ? "obj" : typeTag;
            string path = BuildDebugPath(obj.transform);
            return string.Concat(tag, ":", path);
        }

        public static string BuildDebugPath(GameObject obj)
        {
            if (obj == null)
            {
                return string.Empty;
            }
            return BuildDebugPath(obj.transform);
        }

        public static string BuildDebugPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            Stack<string> parts = new Stack<string>();
            Transform current = transform;
            while (current != null)
            {
                parts.Push(current.name + "#" + current.GetSiblingIndex());
                current = current.parent;
            }

            return string.Join("/", parts.ToArray());
        }

        public static uint HashKey(string key)
        {
            const uint offset = 2166136261u;
            const uint prime = 16777619u;
            uint hash = offset;
            if (key == null)
            {
                return hash;
            }

            for (int i = 0; i < key.Length; i++)
            {
                hash ^= (byte)key[i];
                hash *= prime;
            }
            return hash;
        }

        private static string FormatVec3(Vector3 value)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F3},{1:F3},{2:F3}", value.x, value.y, value.z);
        }
    }
}
