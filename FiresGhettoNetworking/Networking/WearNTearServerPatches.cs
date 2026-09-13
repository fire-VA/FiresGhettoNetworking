using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Dedicated-server skip of WearNTear.UpdateWear for pieces WearNTearClassifier rates fully invulnerable, on the
    /// server that owns them. Every other piece runs vanilla: support, rain, snow, Ashlands and event damage can all
    /// change a piece at full health, and the wet and Ashlands flags are only ever written by the visit itself.
    /// </summary>
    [HarmonyPatch]
    public static class WearNTearServerPatches
    {
        [HarmonyPatch(typeof(WearNTear), "UpdateWear")]
        [HarmonyPrefix]
        public static bool UpdateWear_Prefix(WearNTear __instance)
        {
            if (!FiresGhettoNetworkMod.ConfigEnableWNTServerOptimization.Value)
                return true;
            if (ZNet.instance == null || !ZNet.instance.IsDedicated())
                return true;

            ZNetView nview = __instance.m_nview;
            if (nview == null || !nview.IsValid() || !nview.IsOwner())
                return true;

            return !WearNTearClassifier.IsFullyInvulnerable(__instance);
        }
    }
}
