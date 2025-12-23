    using HarmonyLib;
    using System.Collections.Generic;
    using System.Linq;
    using UnityEngine;
    using System; // <-- Add this using directive

    namespace FiresGhettoNetworkMod
    {
        [HarmonyPatch]
        public static class ShipFixesGroup
        {
            // ONLY SHIPS — these get permanent autopilot
            public static readonly List<string> ShipPrefabNames = new List<string>
            {
                "Vandrarskapp01",
                "VAPrototype",
                "VAPrototype1",
                "VAPinnacle",
                "VAPinnacle1",
                "VAPinnacle2",
                "RowBoat",
                "VikingShip_Ashlands",
                "VikingShip",
                "Raft",
                "Karve",
                "Trailership",
            };
            // Enable controls + force permanent autopilot on placement
            [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
            public static class Player_TryPlacePiece_Patch
            {
                public static void Postfix(Player __instance, Piece piece, bool __result)
                {
                    if (!__result || piece == null) return;

                    var controls = piece.GetComponent<ShipControlls>();
                    if (controls != null)
                    {
                        controls.enabled = true;
                        Debug.Log($"{FiresGhettoNetworkMod.PluginName}: Enabled ShipControlls on placed ship: {piece.name}");
                    }

                    // Clean name without (Clone) suffix
                    string cleanName = piece.name.Replace("(Clone)", "").Trim();

                    // Only apply permanent autopilot to ships in our list
                    if (ShipPrefabNames.Any(name => cleanName.StartsWith(name)))
                    {
                        var nview = piece.GetComponent<ZNetView>();
                        if (nview != null && nview.GetZDO() != null)
                        {
                            nview.GetZDO().Set(ZDOVars.s_user, -1L);
                            Debug.Log($"{FiresGhettoNetworkMod.PluginName}: {cleanName} placed — PERMANENT AUTOPILOT ENABLED");
                        }
                    }
                }
            }

            // BLOCK VANILLA FROM EVER CHANGING THE USER
            // Changed: do NOT skip original RPC handling (no Prefix that returns false).
            // Instead apply corrective logic in a Postfix only for our ships so other mods' RPCs are not suppressed.
            [HarmonyPatch(typeof(ShipControlls), "RPC_RequestControl")]
            [HarmonyPatch(typeof(ShipControlls), "RPC_ReleaseControl")]
            public static class ShipControlls_BlockUserWrite_Patch
            {
                // Allow original to run so other mods' patches and vanilla behavior execute.
                static bool Prefix(ShipControlls __instance) => true;

                // After RPC runs, ensure our ships remain in the desired state (only if shipfixes enabled).
                static void Postfix(ShipControlls __instance)
                {
                    if (!FiresGhettoNetworkMod.ConfigEnableShipFixes.Value) return;
                    if (__instance?.m_ship == null) return;

                    string cleanName = __instance.m_ship.name.Replace("(Clone)", "").Trim();
                    if (!ShipPrefabNames.Any(n => cleanName.StartsWith(n))) return;

                    var nview = __instance.GetComponent<ZNetView>();
                    if (nview == null || !nview.IsValid()) return;

                    // Ensure no user if we require dummy-user physics (customize policy as needed)
                    long user = nview.GetZDO().GetLong(ZDOVars.s_user, 0L);
                    if (user != -1L)
                    {
                        nview.GetZDO().Set(ZDOVars.s_user, -1L);
                        nview.InvokeRPC("ForceUpdateZDO", Array.Empty<object>());
                        LoggerOptions.LogInfo($"Reverted user on ship {cleanName} to -1 to preserve dummy physics.");
                    }
                }
            }

            // Keep physics running even with dummy user
            [HarmonyPatch(typeof(ShipControlls), nameof(ShipControlls.HaveValidUser))]
            public static class ShipControlls_DummyPhysics_Patch
            {
                static bool Prefix(ShipControlls __instance, ref bool __result)
                {
                    if (__instance.m_ship == null) return true;

                    // Clean name without (Clone)
                    string cleanName = __instance.m_ship.name.Replace("(Clone)", "").Trim();

                    // Only apply dummy physics to our ships
                    if (!ShipPrefabNames.Any(n => cleanName.StartsWith(n)))
                        return true;

                    var nview = __instance.GetComponent<ZNetView>();
                    if (nview == null || !nview.IsValid()) return true;

                    long user = nview.GetZDO().GetLong(ZDOVars.s_user, 0L);
                    if (user == -1L)
                    {
                        __result = true;
                        return false;
                    }

                    return true;
                }
            }
        }
    }