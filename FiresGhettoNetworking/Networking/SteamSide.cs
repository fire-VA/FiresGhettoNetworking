using System;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Whether this game build has Steam networking at all. The Xbox / Microsoft Store
    /// build ships no ZSteamSocket and may ship no Steamworks assembly, so every class that names a Steam type is reached only
    /// behind this flag. Names no Steam type itself.
    /// </summary>
    internal static class SteamSide
    {
        internal static readonly bool Present;

        static SteamSide()
        {
            // AccessTools.TypeByName logs a HarmonyX warning when the type is missing; asking the
            // game's own assembly (and the loaded Steamworks one, if any) directly is silent
            Present = typeof(ZNet).Assembly.GetType("ZSteamSocket", false) != null && FindLoaded("Steamworks.SteamAPI");
        }

        private static bool FindLoaded(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (asm.GetType(fullName, false) != null) return true; }
                catch (Exception) { }
            }
            return false;
        }
    }
}
