using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.5.49 (Xbox build has no ZSteamSocket): whether this game build has Steam networking at all. The Xbox / Microsoft Store
    /// build ships no ZSteamSocket and may ship no Steamworks assembly, so every class that names a Steam type is reached only
    /// behind this flag. Names no Steam type itself.
    /// </summary>
    internal static class SteamSide
    {
        internal static readonly bool Present;

        static SteamSide()
        {
            Present = AccessTools.TypeByName("ZSteamSocket") != null && AccessTools.TypeByName("Steamworks.SteamAPI") != null;
        }
    }
}
