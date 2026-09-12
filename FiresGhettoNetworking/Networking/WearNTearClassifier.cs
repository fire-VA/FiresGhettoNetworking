using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// The single definition of "fully invulnerable" for the WearNTear skips: every damage modifier is Immune
    /// or Ignore, the two that HitData.ApplyResistance reduces to zero. Anything else stays mortal so support
    /// and wear keep ticking. Cheap enough to call per piece per tick.
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
