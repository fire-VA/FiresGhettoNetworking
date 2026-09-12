using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Server-side WearNTear skip. UpdateWear's cost is the physics overlap in UpdateSupport, and it can
    /// change nothing for a piece that is undamaged, dry, outside the Ashlands and settled, or that
    /// WearNTearClassifier rates fully invulnerable. Clients still run it for health visuals.
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

            // WearNTear.m_nview is set in Awake - no GetComponent needed.
            ZNetView nview = __instance.m_nview;
            if (nview == null || !nview.IsValid() || !nview.IsOwner())
                return true;

            // Fully invulnerable pieces: nothing UpdateWear does has observable
            // effect (no damage can land, support graph contributions are static),
            // so skip regardless of placement age, wetness, biome, or HP.
            // Cheap classifier — ten enum compares, no allocation.
            if (WearNTearClassifier.IsFullyInvulnerable(__instance))
                return false;

            // Recently placed pieces need the full update (vanilla's ShouldUpdate threshold).
            if (__instance.m_createTime >= 0f && Time.time - __instance.m_createTime <= 30f)
                return true;

            // Ashlands (ash/lava damage) and wet pieces (rain damage) always need updating.
            if (__instance.m_inAshlands || __instance.m_rainWet)
                return true;

            // If damaged, support changes may still apply - let vanilla run.
            float maxHealth = __instance.m_health;
            float currentHealth = nview.GetZDO().GetFloat(ZDOVars.s_health, maxHealth);
            if (currentHealth < maxHealth)
                return true;

            // Full health, not wet, not in Ashlands, not recently placed - nothing can change.
            return false;
        }
    }
}
