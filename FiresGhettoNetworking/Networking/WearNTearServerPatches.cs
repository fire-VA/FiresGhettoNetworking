using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// WearNTear server-side CPU optimization.
    ///
    /// On a dedicated server with large player bases (500+ placed pieces in active
    /// zones), WearNTearUpdater calls WearNTear.UpdateWear every frame for every
    /// piece. The most expensive part is UpdateSupport(), which calls
    /// Physics.OverlapBoxNonAlloc for each piece to find structural connections.
    ///
    /// From decompiled WearNTear.UpdateWear:
    ///   - UpdateSupport() ? Physics.OverlapBoxNonAlloc per bound per piece
    ///   - Rain damage timer check
    ///   - Ashlands/lava damage
    ///
    /// For a piece at full health with no rain/ash damage pending, support
    /// calculation is the only work being done � and for static structures that
    /// haven't been modified, the support result never changes.
    ///
    /// Optimization: Skip UpdateWear entirely for pieces that are:
    ///   1. At full health (health == m_health, i.e. GetHealthPercentage() == 1.0)
    ///   2. Not in the Ashlands biome (no ash/lava damage possible)
    ///   3. Not currently wet (no rain damage timer running)
    ///   4. Not recently placed (createTime > 30s ago � vanilla's ShouldUpdate check)
    ///
    /// When these conditions hold, the support value cannot change and weather
    /// damage cannot tick, so UpdateWear would do nothing anyway.
    ///
    /// SERVER-ONLY � clients need UpdateWear for visual updates (SetHealthVisual).
    ///
    /// Workstream E.3 tightening: pieces classified as fully invulnerable by
    /// <see cref="WearNTearClassifier.IsFullyInvulnerable"/> hit the fast path
    /// REGARDLESS of damage state. A damaged-but-invulnerable piece (Infinity
    /// Hammer pieces that took accidental damage before being set immortal,
    /// admin-flagged stone, etc.) still has nothing UpdateWear can usefully
    /// do — support won't change because the piece can't be removed by damage,
    /// and weather won't tick because invulnerable pieces ignore all damage
    /// types including fire/poison-from-rain. Same outcome, broader fast path.
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

            // WearNTear.m_nview is set in Awake � no GetComponent needed.
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

            // If damaged, support changes may still apply � let vanilla run.
            float maxHealth = __instance.m_health;
            float currentHealth = nview.GetZDO().GetFloat(ZDOVars.s_health, maxHealth);
            if (currentHealth < maxHealth)
                return true;

            // Full health, not wet, not in Ashlands, not recently placed � nothing can change.
            return false;
        }
    }
}
