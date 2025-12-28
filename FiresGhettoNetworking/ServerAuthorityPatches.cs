using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [HarmonyPatch]
    public static class ServerAuthorityPatches
    {
        private static readonly int DefaultActiveArea = 3;   // vanilla values as fallback
        private static readonly int DefaultDistantArea = 5;

        // ====================================================================
        // TELEPORT FIX — MUST RUN BEFORE OTHER IsActiveAreaLoaded PATCHES
        // ====================================================================
        [HarmonyPatch(typeof(ZoneSystem), "IsActiveAreaLoaded")]
        [HarmonyPriority(Priority.First)] // Ensures this runs before the server-authority version below
        [HarmonyPrefix]
        public static bool IsActiveAreaLoaded_TeleportFix(ref bool __result)
        {
            // Client-side: if the local player is teleporting, force "loaded" to prevent black screen / hmap NRE
            if (Player.m_localPlayer != null && Player.m_localPlayer.IsTeleporting())
            {
                __result = true;
                return false;
            }
            return true; // Continue to other patches
        }

        // ====================================================================
        // 1. Server creates/destroys objects for ALL connected peers — SAFE
        // ====================================================================
        [HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
        [HarmonyPrefix]
        public static bool CreateDestroyObjects_Prefix(ZNetScene __instance)
        {
            // Only run on dedicated/listen server AND when there are actual connected peers
            if (!ZNet.instance || !ZNet.instance.IsServer() || ZNet.instance.GetConnectedPeers().Count == 0)
                return true;

            int extendedRadius = FiresGhettoNetworkMod.ConfigExtendedZoneRadius.Value;
            int activeArea = (ZoneSystem.instance?.m_activeArea ?? DefaultActiveArea) + extendedRadius;
            int distantArea = (ZoneSystem.instance?.m_activeDistantArea ?? DefaultDistantArea) + extendedRadius;

            var near = new List<ZDO>();
            var distant = new List<ZDO>();

            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
            {
                if (!peer.IsReady()) continue; // Skip not-fully-connected peers
                Vector3 pos = peer.GetRefPos();
                Vector2i zone = ZoneSystem.GetZone(pos);
                ZDOMan.instance.FindSectorObjects(zone, activeArea, distantArea, near, distant);
            }

            var distinctNear = near.Distinct().ToList();
            var distinctDistant = distant.Distinct().ToList();

            var traverse = Traverse.Create(__instance);
            traverse.Method("CreateObjects", distinctNear, distinctDistant).GetValue();
            traverse.Method("RemoveObjects", distinctNear, distinctDistant).GetValue();

            return false; // Skip original
        }

        // ====================================================================
        // 2. IsActiveAreaLoaded checks ALL peers — SAFE
        // ====================================================================
        [HarmonyPatch(typeof(ZoneSystem), "IsActiveAreaLoaded")]
        [HarmonyPrefix]
        public static bool IsActiveAreaLoaded_Prefix(ref bool __result, ZoneSystem __instance, Dictionary<Vector2i, object> ___m_zones)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || ZNet.instance.GetPeers().Count == 0)
                return true;

            int extendedRadius = FiresGhettoNetworkMod.ConfigExtendedZoneRadius.Value;
            int activeArea = __instance.m_activeArea + extendedRadius;

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (!peer.IsReady()) continue;
                Vector2i zone = ZoneSystem.GetZone(peer.GetRefPos());

                for (int y = zone.y - activeArea; y <= zone.y + activeArea; y++)
                {
                    for (int x = zone.x - activeArea; x <= zone.x + activeArea; x++)
                    {
                        if (!___m_zones.ContainsKey(new Vector2i(x, y)))
                        {
                            __result = false;
                            return false;
                        }
                    }
                }
            }

            __result = true;
            return false;
        }

        // ====================================================================
        // 3. ZoneSystem.Update runs fully on server — SAFE
        // ====================================================================
        [HarmonyPatch(typeof(ZoneSystem), "Update")]
        [HarmonyPrefix]
        public static bool ZoneSystem_Update_Prefix(ZoneSystem __instance, ref float ___m_updateTimer)
        {
            if (ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected)
                return false;

            ___m_updateTimer += Time.deltaTime;
            if (___m_updateTimer > 0.1f)
            {
                ___m_updateTimer = 0f;
                Traverse.Create(__instance).Method("UpdateTTL", 0.1f).GetValue();

                if (ZNet.instance && ZNet.instance.IsServer() && ZNet.instance.GetPeers().Count > 0)
                {
                    var traverse = Traverse.Create(__instance);
                    foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                    {
                        if (peer.IsReady())
                            traverse.Method("CreateLocalZones", peer.GetRefPos()).GetValue();
                    }
                }
            }

            return false;
        }

        // ====================================================================
        // 4. Server owns all persistent ZDOs in active areas — SAFE
        // ====================================================================
        [HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS")]
        [HarmonyPrefix]
        public static bool ReleaseNearbyZDOS_Prefix(ZDOMan __instance, ref Vector3 refPosition, ref long uid)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || ZNet.instance.GetPeers().Count == 0)
                return true;

            int extendedRadius = FiresGhettoNetworkMod.ConfigExtendedZoneRadius.Value;
            int activeArea = (ZoneSystem.instance?.m_activeArea ?? DefaultActiveArea) + extendedRadius;

            Vector2i zone = ZoneSystem.GetZone(refPosition);
            var sectorObjects = Traverse.Create(__instance).Field<List<ZDO>>("m_tempNearObjects").Value;
            sectorObjects.Clear();
            __instance.FindSectorObjects(zone, activeArea, 0, sectorObjects);

            foreach (ZDO zdo in sectorObjects)
            {
                if (!zdo.Persistent) continue;

                bool inAnyActiveArea = false;
                foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                {
                    if (peer.IsReady() && ZNetScene.InActiveArea(zdo.GetSector(), ZoneSystem.GetZone(peer.GetRefPos())))
                    {
                        inAnyActiveArea = true;
                        break;
                    }
                }

                long owner = zdo.GetOwner();
                if (owner == uid || owner == ZNet.GetUID())
                {
                    if (!inAnyActiveArea)
                        zdo.SetOwner(0L);
                }
                else
                {
                    bool shouldOwn = owner == 0L ||
                                     !Traverse.Create(__instance).Method("IsInPeerActiveArea", zdo.GetSector(), owner).GetValue<bool>();

                    if (shouldOwn && inAnyActiveArea)
                        zdo.SetOwner(ZNet.GetUID());
                }
            }

            return false;
        }

        // ====================================================================
        // 5. OutsideActiveArea checks ALL peers — SAFE
        // ====================================================================
        [HarmonyPatch(typeof(ZNetScene), "OutsideActiveArea", new[] { typeof(Vector3) })]
        [HarmonyPrefix]
        public static bool OutsideActiveArea_Prefix(ref bool __result, Vector3 point)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || ZNet.instance.GetPeers().Count == 0)
                return true;

            int extendedRadius = FiresGhettoNetworkMod.ConfigExtendedZoneRadius.Value;
            int activeArea = (ZoneSystem.instance?.m_activeArea ?? DefaultActiveArea) + extendedRadius;

            __result = true;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer.IsReady() && !ZNetScene.OutsideActiveArea(point, ZoneSystem.GetZone(peer.GetRefPos()), activeArea))
                {
                    __result = false;
                    break;
                }
            }

            return false;
        }

        // ====================================================================
        // FIX: Skip client-only logic in Tameable on server
        // ====================================================================
        [HarmonyPatch(typeof(Tameable), "Awake")]
        [HarmonyPrefix]
        public static bool Tameable_Awake_Prefix(Tameable __instance)
        {
            return ZNet.instance == null || !ZNet.instance.IsServer();
        }

        [HarmonyPatch(typeof(Tameable), "Update")]
        [HarmonyPrefix]
        public static bool Tameable_Update_Prefix(Tameable __instance)
        {
            return ZNet.instance == null || !ZNet.instance.IsServer();
        }

        // ====================================================================
        // FIX: Suppress "Can not play a disabled audio source" spam on server ONLY
        // ====================================================================
        [HarmonyPatch(typeof(AudioMan), "Update")]
        [HarmonyPrefix]
        public static bool AudioMan_Update_Prefix()
        {
            return !ZNet.instance?.IsServer() ?? true;
        }
    }
}