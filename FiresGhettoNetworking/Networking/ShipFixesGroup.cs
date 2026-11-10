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
        private static readonly AccessTools.FieldRef<Ship, Ship.Speed> s_speed = AccessTools.FieldRefAccess<Ship, Ship.Speed>("m_speed");
        private static readonly AccessTools.FieldRef<Ship, float> s_rudder = AccessTools.FieldRefAccess<Ship, float>("m_rudderValue");
        public static void CustomFixedUpdate_Postfix(Ship __instance)
        {
            if (!ShipFixesActive()) return;
            if (__instance.m_nview == null || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner()) return;

            var zdo = __instance.m_nview.GetZDO();
            if (zdo == null) return;

            Ship.Speed publishedSpeed = (Ship.Speed)zdo.GetInt(ZDOVars.s_forward);
            if (__instance.GetSpeedSetting() == Ship.Speed.Stop && publishedSpeed != Ship.Speed.Stop)
                s_speed(__instance) = publishedSpeed;   // 1.6.26: cached FieldRef (was AccessTools.Field per call)

            float publishedRudder = zdo.GetFloat(ZDOVars.s_rudder);
            if (__instance.GetRudderValue() == 0f && publishedRudder != 0f)
                s_rudder(__instance) = publishedRudder;
        }

        /// <summary>Ownership stays with the server; vanilla hands it to an onboard player every two seconds.</summary>
        [HarmonyPatch(typeof(Ship), "UpdateOwner")]
        [HarmonyPrefix]
        public static bool UpdateOwner_Prefix() => !ShipFixesActive();

        /// <summary>Sail visuals run ahead of the owner guard and reach cloth and transforms a server build does not
        /// have. Guarded on the build, not on ConfigEnableShipFixes: ships become server-owned on Server-Side Ship
        /// Simulation, so fixes-off + simulation-on would otherwise crash every fixed update.</summary>
        [HarmonyPatch(typeof(Ship), "UpdateSail")]
        [HarmonyPrefix]
        public static bool UpdateSail_Prefix() => !IsDedicatedServer();

        private static bool ShipFixesActive() =>
            FiresGhettoNetworkMod.ConfigEnableShipFixes.Value && IsDedicatedServer();

        private static bool IsDedicatedServer() => ZNet.instance != null && ZNet.instance.IsDedicated();
    }
}
