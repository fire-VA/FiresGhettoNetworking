using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Server-authority ship physics for dedicated servers.
    ///
    /// Vanilla Ship.CustomFixedUpdate zeroes m_speed and m_rudderValue whenever there
    /// is no Player trigger on board. A dedicated server has no local player, so every
    /// server-owned ship is treated as empty and loses the throttle/heading the
    /// controlling client last published to the ZDO. We re-apply those ZDO values in a
    /// CustomFixedUpdate postfix so the following physics tick steers correctly.
    ///
    /// No write-back to the ZDO is needed: vanilla UpdateControlls already re-publishes
    /// m_speed/m_rudderValue to the ZDO every tick for an owned ship (Ship.cs 441-442),
    /// so a restore here is picked up and synced on the next tick at no extra cost.
    ///
    /// Registration is gated on ConfigEnableShipFixes (Ascend.cs); when the feature is
    /// off these patches are never applied, so there is zero per-tick cost when disabled.
    /// </summary>
    [HarmonyPatch]
    public static class ShipFixesGroup
    {
        [HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate))]
        [HarmonyPostfix]
        public static void CustomFixedUpdate_Postfix(Ship __instance)
        {
            if (!FiresGhettoNetworkMod.ConfigEnableShipFixes.Value)
                return;
            if (ZNet.instance == null || !ZNet.instance.IsDedicated())
                return;
            if (__instance.m_nview == null || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner())
                return;

            var zdo = __instance.m_nview.GetZDO();
            if (zdo == null)
                return;

            Ship.Speed zdoSpeed = (Ship.Speed)zdo.GetInt(ZDOVars.s_forward);
            if (__instance.GetSpeedSetting() == Ship.Speed.Stop && zdoSpeed != Ship.Speed.Stop)
                AccessTools.Field(typeof(Ship), "m_speed").SetValue(__instance, zdoSpeed);

            float zdoRudder = zdo.GetFloat(ZDOVars.s_rudder);
            if (__instance.GetRudderValue() == 0f && zdoRudder != 0f)
                AccessTools.Field(typeof(Ship), "m_rudderValue").SetValue(__instance, zdoRudder);
        }

        /// <summary>
        /// Ship.UpdateOwner runs every 2s and would hand ship ownership to an onboard
        /// player. On a dedicated server it already no-ops (Player.m_localPlayer is null
        /// and m_players is empty), but we skip it explicitly so the server keeps
        /// authoritative ownership even if a future change relaxes those vanilla guards.
        /// </summary>
        [HarmonyPatch(typeof(Ship), "UpdateOwner")]
        [HarmonyPrefix]
        public static bool UpdateOwner_Prefix()
        {
            if (!FiresGhettoNetworkMod.ConfigEnableShipFixes.Value)
                return true;
            if (ZNet.instance == null || !ZNet.instance.IsDedicated())
                return true;
            return false;
        }
    }
}
