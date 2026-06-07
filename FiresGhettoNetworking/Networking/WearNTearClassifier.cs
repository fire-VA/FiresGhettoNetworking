using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Shared invulnerability classifier for WearNTear pieces.
    ///
    /// "Fully invulnerable" = every damage-type modifier on the piece's
    /// <c>HitData.DamageModifiers</c> is either <c>Immune</c> or <c>Ignore</c>
    /// — both produce zero damage in <see cref="HitData.ApplyResistance"/>:
    ///   - <c>Ignore</c>: short-circuits to <c>return 0</c> at the top of
    ///     <c>ApplyModifier</c>.
    ///   - <c>Immune</c>: enters the switch with <c>mod - 1 == Weak</c> case
    ///     which sets the per-type damage to 0.
    /// Any other modifier (Normal, Resistant, Weak, VeryResistant, VeryWeak,
    /// SlightlyResistant, SlightlyWeak) leaves at least one damage type
    /// reachable and the piece is treated as mortal — even if functionally
    /// near-invulnerable, the support graph and wear ticks should keep running.
    ///
    /// Used by both the server-side wear-skip (E.3) and the client-side
    /// support-skip (E.1) so a single primitive defines what "invulnerable"
    /// means for the whole perf system. Cheap (10 enum compares, no allocation,
    /// no reflection) so safe to call per-piece per-tick.
    ///
    /// We accept a small false-negative on Ashlands pieces with `m_ashDamageImmune`
    /// — those are immune to ash but still hit-mortal, so they correctly fail
    /// this classifier. Tightening the predicate further (e.g. include ash-immune
    /// stone) would re-enter Ashlands tick logic which the existing
    /// WearNTearServerPatches predicate handles separately.
    /// </summary>
    public static class WearNTearClassifier
    {
        public static bool IsFullyInvulnerable(WearNTear wnt)
        {
            if (wnt == null) return false;
            return IsZeroOrIgnored(wnt.m_damages.m_blunt)
                && IsZeroOrIgnored(wnt.m_damages.m_slash)
                && IsZeroOrIgnored(wnt.m_damages.m_pierce)
                && IsZeroOrIgnored(wnt.m_damages.m_chop)
                && IsZeroOrIgnored(wnt.m_damages.m_pickaxe)
                && IsZeroOrIgnored(wnt.m_damages.m_fire)
                && IsZeroOrIgnored(wnt.m_damages.m_frost)
                && IsZeroOrIgnored(wnt.m_damages.m_lightning)
                && IsZeroOrIgnored(wnt.m_damages.m_poison)
                && IsZeroOrIgnored(wnt.m_damages.m_spirit);
        }

        private static bool IsZeroOrIgnored(HitData.DamageModifier mod)
        {
            return mod == HitData.DamageModifier.Immune
                || mod == HitData.DamageModifier.Ignore;
        }
    }
}
