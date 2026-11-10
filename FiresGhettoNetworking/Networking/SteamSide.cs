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
        internal static readonly string Why;   // why Present is false, for the skipped-patch lines

        static SteamSide()
        {
            // AccessTools.TypeByName logs a HarmonyX warning when the type is missing; asking the
            // game's own assembly (and the loaded Steamworks one, if any) directly is silent
            bool socket = typeof(ZNet).Assembly.GetType("ZSteamSocket", false) != null, steamworks = FindLoaded("Steamworks.SteamAPI");
            Present = socket && steamworks;
            Why = !socket ? "no ZSteamSocket on this game build" : !steamworks ? "no Steamworks assembly loaded" : "";
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
