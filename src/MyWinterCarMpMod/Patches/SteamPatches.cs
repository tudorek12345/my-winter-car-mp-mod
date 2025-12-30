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
    }
}
