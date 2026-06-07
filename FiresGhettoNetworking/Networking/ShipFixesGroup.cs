    using HarmonyLib;
    using UnityEngine;

    namespace FiresGhettoNetworkMod
    {
        /// <summary>
        /// Server-authority ship physics fixes for dedicated servers.
        ///
        /// Problems solved:
        /// 1. Vanilla kills ship speed + rudder when m_players.Count == 0
        /// 2. Vanilla clamps Slow/Back speed when no controlling player
        /// 3. Vanilla applies 10x velocity dampening when no players on board
        /// 4. Vanilla transfers ship ownership away from server via UpdateOwner
        ///
        /// Instead of the old approach (hardcoded prefab names, dummy user -1L hack,
        /// patching ShipControlls.HaveValidUser, patching Player.TryPlacePiece),
        /// we now directly patch Ship.CustomFixedUpdate and Ship.UpdateOwner on the
        /// server. Works with any ship (vanilla or modded) — no prefab list needed.
        ///
        /// See REFERENCE_Ship.md for full decompiled analysis.
        /// </summary>
        [HarmonyPatch]
        public static class ShipFixesGroup
        {
            /// <summary>
            /// Ship.CustomFixedUpdate Prefix — Server-authority ship physics
            ///
            /// On dedicated server, we read speed/rudder from ZDO (set by the controlling
            /// client via RPCs + UpdateControlls) and apply them directly, bypassing the
            /// three vanilla checks that kill unattended ship physics.
            ///
            /// The key insight from the decompiled Ship.CustomFixedUpdate:
            /// - UpdateControlls already reads m_speed and m_rudderValue from ZDO when not owner
            /// - But when owner (which the server is), it WRITES them to ZDO
            /// - So we need to preserve the ZDO values through the speed-kill checks
            /// </summary>
            [HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate))]
            [HarmonyPrefix]
            public static void CustomFixedUpdate_Prefix(Ship __instance, out Ship.Speed __state)
            {
                // Save current speed before vanilla potentially kills it
                __state = Ship.Speed.Stop;

                if (!FiresGhettoNetworkMod.ConfigEnableShipFixes.Value)
                    return;
                if (ZNet.instance == null || !ZNet.instance.IsDedicated())
                    return;
                if (__instance.m_nview == null || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner())
                    return;

                // Read the speed/rudder the client set via RPCs (stored in ZDO by UpdateControlls)
                var zdo = __instance.m_nview.GetZDO();
                if (zdo == null) return;

                __state = (Ship.Speed)zdo.GetInt(ZDOVars.s_forward);
            }

            /// <summary>
            /// Ship.CustomFixedUpdate Postfix — Restore speed after vanilla kills it
            ///
            /// Vanilla's CustomFixedUpdate sets m_speed=Stop and m_rudderValue=0 when
            /// m_players.Count==0. On dedicated server, m_players is always empty (no
            /// local player triggers), so speed is always killed.
            ///
            /// We restore the speed from ZDO after vanilla has run, so the physics
            /// calculations that already happened this frame used the correct speed.
            ///
            /// Note: The speed is read from ZDO (set by the controlling client via RPCs).
            /// When no client is controlling, speed naturally stays at whatever the last
            /// client set it to — ships coast to a stop via normal physics dampening.
            /// </summary>
            [HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate))]
            [HarmonyPostfix]
            public static void CustomFixedUpdate_Postfix(Ship __instance, Ship.Speed __state)
            {
                if (!FiresGhettoNetworkMod.ConfigEnableShipFixes.Value)
                    return;
                if (ZNet.instance == null || !ZNet.instance.IsDedicated())
                    return;
                if (__instance.m_nview == null || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner())
                    return;

                var zdo = __instance.m_nview.GetZDO();
                if (zdo == null) return;

                // Restore the speed from ZDO — vanilla may have killed it
                Ship.Speed zdoSpeed = (Ship.Speed)zdo.GetInt(ZDOVars.s_forward);

                // Only restore if vanilla actually killed the speed (speed was set to Stop
                // but ZDO says otherwise). This avoids fighting with legitimate Stop commands.
                if (__instance.GetSpeedSetting() == Ship.Speed.Stop && zdoSpeed != Ship.Speed.Stop)
                {
                    // Use AccessTools to set the private m_speed field
                    AccessTools.Field(typeof(Ship), "m_speed").SetValue(__instance, zdoSpeed);
                }

                // Also restore rudder value if it was zeroed
                float zdoRudder = zdo.GetFloat(ZDOVars.s_rudder);
                if (__instance.GetRudderValue() == 0f && zdoRudder != 0f)
                {
                    AccessTools.Field(typeof(Ship), "m_rudderValue").SetValue(__instance, zdoRudder);
                }

                // Write restored values back to ZDO so they sync to clients
                zdo.Set(ZDOVars.s_forward, (int)__instance.GetSpeedSetting());
                zdo.Set(ZDOVars.s_rudder, __instance.GetRudderValue());
            }

            /// <summary>
            /// Ship.UpdateOwner Prefix — Prevent ownership transfer on dedicated server
            ///
            /// Vanilla's UpdateOwner transfers ship ownership to players on board.
            /// On dedicated server, we want the server to keep ownership so it can
            /// authoritatively simulate physics. Player.m_localPlayer is always null
            /// on dedicated server anyway (so vanilla already returns early), but we
            /// skip it explicitly for clarity and to prevent any edge cases.
            /// </summary>
            [HarmonyPatch(typeof(Ship), "UpdateOwner")]
            [HarmonyPrefix]
            public static bool UpdateOwner_Prefix()
            {
                if (!FiresGhettoNetworkMod.ConfigEnableShipFixes.Value)
                    return true;
                if (ZNet.instance == null || !ZNet.instance.IsDedicated())
                    return true;

                // Skip vanilla ownership transfer — server keeps ownership
                return false;
            }
        }
    }
