using System;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Workstream E.1 — client-side structural support skip for invulnerable pieces.
    ///
    /// Vanilla <see cref="WearNTear.UpdateSupport"/> is the most expensive
    /// per-piece per-tick path on the client during heavy bases: each call does
    /// up to N <c>Physics.OverlapBoxNonAlloc</c> queries (one per BoundData),
    /// then iterates each hit collider, walks the support graph, computes lossy
    /// support transfer, and updates the cached state. For a 140k-piece base
    /// where invulnerable pieces dominate the active area, the SUPPORT
    /// PROPAGATION cost across that set is the steady-state ceiling.
    ///
    /// Invulnerable pieces by definition cannot be removed by damage, cannot
    /// collapse, and never need their own support recomputed. They DO still
    /// participate in the support graph from neighbours' perspectives — a
    /// mortal piece resting on an invulnerable substrate calls
    /// <see cref="WearNTear.GetSupport"/> on the substrate and gets back
    /// <c>m_support</c>. By forcing <c>m_support</c> to the material's max value
    /// once (vanilla's Awake already does this) and never touching it again,
    /// the substrate continues to advertise full support to mortal queries
    /// while paying zero per-tick CPU.
    ///
    /// "Asymmetric query path" (per the perf plan): we DON'T patch GetSupport,
    /// only UpdateSupport. Mortal-piece support queries land on our forced
    /// m_support value and return correctly without ever entering vanilla's
    /// physics overlap path on the invulnerable substrate. Result: invulnerable
    /// pieces are pure support sources, never consumers.
    ///
    /// Server-side intentionally NOT patched here — <see cref="WearNTearServerPatches"/>
    /// already short-circuits the entire <c>UpdateWear</c> (which is what calls
    /// UpdateSupport on the server) for invulnerable pieces via the same
    /// <see cref="WearNTearClassifier"/>. Patching UpdateSupport directly on
    /// the server side would be redundant work.
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
