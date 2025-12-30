using System;
using System.Reflection;

namespace MyWinterCarMpMod.Patches
{
    internal static class SteamPatches
    {
        internal static bool RestartAppIfNecessaryPrefix(ref bool __result)
        {
            if (!Plugin.AllowMultipleInstances)
            {
                return true;
            }

            __result = false;
            return false;
        }

        internal static bool SteamApiInitPrefix(ref bool __result)
        {
            if (!Plugin.AllowMultipleInstances)
            {
                return true;
            }

            __result = false;
            return false;
        }

        internal static bool SteamManagerAwakePrefix(object __instance)
        {
            if (!Plugin.AllowMultipleInstances)
            {
                return true;
            }

            try
            {
                Type type = __instance.GetType();
                FieldInfo instanceField = type.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
                UnityEngine.Object unityObject = __instance as UnityEngine.Object;

                if (instanceField != null)
                {
                    object existing = instanceField.GetValue(null);
                    if (existing != null && !ReferenceEquals(existing, __instance))
                    {
                        if (unityObject != null)
                        {
                            UnityEngine.Object.Destroy(unityObject);
                        }
                        return false;
                    }

                    instanceField.SetValue(null, __instance);
                }

                if (unityObject != null)
                {
                    UnityEngine.Object.DontDestroyOnLoad(unityObject);
                }

                FieldInfo initField = type.GetField("m_bInitialized", BindingFlags.NonPublic | BindingFlags.Instance);
                if (initField != null)
                {
                    initField.SetValue(__instance, false);
                }
            }
            catch (Exception ex)
            {
                Util.DebugLog.Warn("SteamManager.Awake bypass failed: " + ex.Message);
            }

            return false;
        }
    }
}
