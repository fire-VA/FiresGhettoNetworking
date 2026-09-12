using System;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Client-side skip of WearNTear.UpdateSupport for pieces WearNTearClassifier rates fully invulnerable.
    /// They keep advertising the maximum support vanilla's Awake already set, so neighbours' GetSupport
    /// queries still resolve, while the per-tick physics overlap is never paid. The server side is covered
    /// by WearNTearServerPatches.
    /// </summary>
    [HarmonyPatch]
    public static class WearNTearClientSupportPatches
    {
        // Cached field accessor for the private m_support field. Resolved once
        // at class init; calls are O(1) thereafter. AccessTools.FieldRefAccess
        // throws if the field is missing, which would be a Valheim restructure
        // — we catch and degrade in TryWriteSupport.
        private static readonly AccessTools.FieldRef<WearNTear, float> _supportField
            = AccessTools.FieldRefAccess<WearNTear, float>("m_support");

        // Reverse-patched proxy for the private GetMaxSupport getter. Returns
        // the material-tier max support (Wood=100, Stone=1000, Iron=1500, etc.)
        // — exactly what vanilla's Awake initializes m_support to. Using the
        // reverse patch instead of inlining the values means future Iron Gate
        // tweaks to material max-support keep working without our intervention.
        [HarmonyReversePatch]
        [HarmonyPatch(typeof(WearNTear), "GetMaxSupport")]
        public static float CallGetMaxSupport(WearNTear self)
        {
            throw new NotImplementedException("Harmony reverse patch failed for WearNTear.GetMaxSupport");
        }

        [HarmonyPatch(typeof(WearNTear), "UpdateSupport")]
        [HarmonyPrefix]
        public static bool UpdateSupport_Prefix(WearNTear __instance)
        {
            // `!cfg?.Value ?? false` would parse as `(!cfg?.Value) ?? false` and
            // give the wrong polarity for the "config not bound yet" case. Spell
            // the gate out longhand: missing config → bail to vanilla.
            var enabledCfg = FiresGhettoNetworkMod.ConfigEnableInvulnerableSupportSkip;
            if (enabledCfg == null || !enabledCfg.Value)
                return true;

            // Server-side has its own UpdateWear-level skip; don't double-up.
            // Server's UpdateWear short-circuit already prevents UpdateSupport
            // from being called for these pieces anyway, but the explicit
            // bail keeps the intent obvious.
            if (ZNet.instance != null && ZNet.instance.IsDedicated())
                return true;

            if (!WearNTearClassifier.IsFullyInvulnerable(__instance))
                return true;

            // Force-pin m_support at material max so any neighbour's GetSupport
            // call returns the right value without triggering our prefix again
            // (we don't patch GetSupport — see class doc). Costs nothing on
            // subsequent ticks if the value is already at max, but vanilla's
            // own GetSupport / RPC_HealthChanged paths can cause m_support to
            // drift; re-pinning each call is the simplest invariant.
            try
            {
                float maxSupport = CallGetMaxSupport(__instance);
                _supportField(__instance) = maxSupport;

                // If owner, also persist via ZDO so peers see the same value
                // when they query through the non-owner path of GetSupport.
                ZNetView nview = __instance.m_nview;
                if (nview != null && nview.IsValid() && nview.IsOwner())
                {
                    nview.GetZDO().Set(ZDOVars.s_support, maxSupport);
                }
            }
            catch (Exception ex)
            {
                // FieldRef or reverse-patch failed (Valheim API drift). Log once
                // and fall through to vanilla so structural sim isn't broken
                // — perf regression beats correctness regression here.
                LoggerOptions.LogWarning($"[WNT-Support] Skip threw, falling back to vanilla: {ex.Message}");
                return true;
            }

            return false;
        }
    }
}
