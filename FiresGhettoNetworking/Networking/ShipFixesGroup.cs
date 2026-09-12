using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Dedicated-server ship fixes, gated on ConfigEnableShipFixes. Vanilla reads every server-owned ship as
    /// empty, because no local player is aboard, and drops the throttle and heading its controlling client published.
    /// </summary>
    [HarmonyPatch]
    public static class ShipFixesGroup
    {
        [HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate))]
        [HarmonyPostfix]
        public static void CustomFixedUpdate_Postfix(Ship __instance)
        {
            if (!ShipFixesActive()) return;
            if (__instance.m_nview == null || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner()) return;

            var zdo = __instance.m_nview.GetZDO();
            if (zdo == null) return;

            Ship.Speed publishedSpeed = (Ship.Speed)zdo.GetInt(ZDOVars.s_forward);
            if (__instance.GetSpeedSetting() == Ship.Speed.Stop && publishedSpeed != Ship.Speed.Stop)
                AccessTools.Field(typeof(Ship), "m_speed").SetValue(__instance, publishedSpeed);

            float publishedRudder = zdo.GetFloat(ZDOVars.s_rudder);
            if (__instance.GetRudderValue() == 0f && publishedRudder != 0f)
                AccessTools.Field(typeof(Ship), "m_rudderValue").SetValue(__instance, publishedRudder);
        }

        /// <summary>Ownership stays with the server; vanilla hands it to an onboard player every two seconds.</summary>
        [HarmonyPatch(typeof(Ship), "UpdateOwner")]
        [HarmonyPrefix]
        public static bool UpdateOwner_Prefix() => !ShipFixesActive();

        /// <summary>Sail visuals run ahead of the owner guard and reach cloth and transforms a server build does not have.</summary>
        [HarmonyPatch(typeof(Ship), "UpdateSail")]
        [HarmonyPrefix]
        public static bool UpdateSail_Prefix() => !ShipFixesActive();

        private static bool ShipFixesActive()
        {
            return FiresGhettoNetworkMod.ConfigEnableShipFixes.Value
                && ZNet.instance != null
                && ZNet.instance.IsDedicated();
        }
    }
}
