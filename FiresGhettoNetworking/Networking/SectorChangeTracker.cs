using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Lets the server skip rescanning parts of the world where nothing has changed: a sector whose last scan for a player
    /// found nothing to send, and which nothing has entered, left or changed in since, cannot have anything to send now.
    /// Every change stamps its sector, and each player still gets a full rescan every two seconds.
    /// </summary>
    [HarmonyPatch]
    internal static class SectorChangeTracker
    {
        public static ConfigEntry<bool> ConfigEnabled;

        private const int SectorCount = 512 * 512;
        private const double FullRescanSeconds = 2.0;
        private const int MaxCleanEntries = 4096;

        private sealed class PeerScans
        {
            public readonly Dictionary<int, int> CleanSince = new Dictionary<int, int>();
            public double NextFullRescan;
        }

        private static readonly int[] s_changedFrame = new int[SectorCount];
        private static readonly Dictionary<ZDOMan.ZDOPeer, PeerScans> s_scans = new Dictionary<ZDOMan.ZDOPeer, PeerScans>();
        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> s_objectsBySector
            = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZoneSystem.SectorIndex, List<ZDO>>> s_portalObjects
            = AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZoneSystem.SectorIndex, List<ZDO>>>("m_portalObjects");

        private static bool s_tracking;
        private static int s_frame = 1;
        private static ZDOMan.ZDOPeer s_scanPeer;
        private static PeerScans s_scan;
        private static bool s_fullScan;

        internal static long SectorsScanned;
        internal static long SectorsSkipped;

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("04 - Networking", "Skip Unchanged Areas", true,
                "Every world update to a player rescans every object around them for anything they have not been sent, which in a\n" +
                "big base means tens of thousands of objects many times a second. With this on, the server skips the parts of that\n" +
                "area where nothing has changed since they were last found fully sent, and still rescans everything every two\n" +
                "seconds. DEDICATED SERVER ONLY.");
        }

        [HarmonyPatch(typeof(ZNet), "Start"), HarmonyPostfix]
        static void OnStart(ZNet __instance)
        {
            s_tracking = __instance.IsServer();
            s_scans.Clear();
            Array.Clear(s_changedFrame, 0, SectorCount);
            s_frame = 1;
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown"), HarmonyPostfix]
        static void OnShutdown()
        {
            s_tracking = false;
            s_scans.Clear();
        }

        [HarmonyPatch(typeof(ZNet), "Update"), HarmonyPrefix]
        static void NextFrame() => s_frame++;

        [HarmonyPatch(typeof(ZDOMan), "RemovePeer"), HarmonyPostfix]
        static void OnRemovePeer(ZNetPeer netPeer)
        {
            ZDOMan.ZDOPeer gone = null;
            foreach (var peer in s_scans.Keys)
            {
                if (peer.m_peer != netPeer) continue;
                gone = peer;
                break;
            }
            if (gone != null) s_scans.Remove(gone);
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddToSector)), HarmonyPostfix]
        static void OnAddToSector(ZoneSystem.SectorIndex sectorIndex)
        {
            if (s_tracking) Mark(sectorIndex.Sector);
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RemoveFromSector)), HarmonyPostfix]
        static void OnRemoveFromSector(ZoneSystem.SectorIndex sectorIndex)
        {
            if (s_tracking) Mark(sectorIndex.Sector);
        }

        [HarmonyPatch(typeof(ZDO), "IncreaseDataRevision"), HarmonyPostfix]
        static void OnDataRevision(ZDO __instance)
        {
            if (s_tracking) Mark(__instance);
        }

        [HarmonyPatch(typeof(ZDO), "IncreaseOwnerRevision"), HarmonyPostfix]
        static void OnOwnerRevision(ZDO __instance)
        {
            if (s_tracking) Mark(__instance);
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize)), HarmonyPostfix]
        static void OnDeserialize(ZDO __instance)
        {
            if (s_tracking) Mark(__instance);
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwnerInternal)), HarmonyPostfix]
        static void OnSetOwnerInternal(ZDO __instance)
        {
            if (s_tracking) Mark(__instance);
        }

        [HarmonyPatch(typeof(ZDOMan), "CreateSyncList"), HarmonyPrefix]
        static void ScanBegin(ZDOMan.ZDOPeer peer)
        {
            s_scanPeer = null;
            s_scan = null;
            if (!s_tracking || ConfigEnabled == null || !ConfigEnabled.Value || peer?.m_peer == null) return;
            if (!s_scans.TryGetValue(peer, out var scans))
            {
                scans = new PeerScans();
                s_scans[peer] = scans;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            s_fullScan = now >= scans.NextFullRescan;
            if (s_fullScan)
            {
                scans.NextFullRescan = now + FullRescanSeconds;
                if (scans.CleanSince.Count > MaxCleanEntries) scans.CleanSince.Clear();
            }
            s_scanPeer = peer;
            s_scan = scans;
        }

        [HarmonyPatch(typeof(ZDOMan), "CreateSyncList"), HarmonyFinalizer]
        static void ScanEnd()
        {
            s_scanPeer = null;
            s_scan = null;
        }

        [HarmonyPatch(typeof(ZDOMan), "FindObjects"), HarmonyPrefix]
        static bool FindObjects_Prefix(ZDOMan __instance, Vector2s sector, List<ZDO> objects, HashSet<ZoneSystem.SectorIndex> visitedSectorIndices)
            => s_scanPeer == null || ScanSector(__instance, sector, objects, visitedSectorIndices, false);

        [HarmonyPatch(typeof(ZDOMan), "FindDistantObjects"), HarmonyPrefix]
        static bool FindDistantObjects_Prefix(ZDOMan __instance, Vector2s sector, List<ZDO> objects, HashSet<ZoneSystem.SectorIndex> visitedSectorIndices)
            => s_scanPeer == null || ScanSector(__instance, sector, objects, visitedSectorIndices, true);

        private static bool ScanSector(ZDOMan zdoMan, Vector2s sector, List<ZDO> objects, HashSet<ZoneSystem.SectorIndex> visited, bool distant)
        {
            var index = ZoneSystem.SectorToIndex(sector);
            if (!visited.Add(index)) return false;

            int key = (int)index.Sector * 2 + (distant ? 1 : 0);
            if (!s_fullScan && s_scan.CleanSince.TryGetValue(key, out int cleanSince) && s_changedFrame[index.Sector] < cleanSince)
            {
                SectorsSkipped++;
                return false;
            }
            SectorsScanned++;

            var peer = s_scanPeer;
            bool clean = true;
            var sectorObjects = s_objectsBySector(zdoMan)[index.Sector];
            if (sectorObjects != null)
            {
                for (int i = 0; i < sectorObjects.Count; i++)
                {
                    var zdo = sectorObjects[i];
                    if ((distant && !zdo.Distant) || !Unsent(peer, zdo)) continue;
                    objects.Add(zdo);
                    clean = false;
                }
            }
            if (!distant && s_portalObjects(zdoMan).TryGetValue(index, out var portals))
            {
                for (int i = 0; i < portals.Count; i++)
                {
                    if (!Unsent(peer, portals[i])) continue;
                    objects.Add(portals[i]);
                    clean = false;
                }
            }

            if (clean) s_scan.CleanSince[key] = s_frame;
            else s_scan.CleanSince.Remove(key);
            return false;
        }

        private static bool Unsent(ZDOMan.ZDOPeer peer, ZDO zdo)
        {
            return !peer.m_zdos.TryGetValue(zdo.m_uid, out var sent)
                || zdo.OwnerRevision > sent.m_ownerRevision
                || zdo.DataRevision > sent.m_dataRevision;
        }

        private static void Mark(ZDO zdo) => Mark(ZoneSystem.GetSectorIndex(zdo.GetPosition()).Sector);

        private static void Mark(uint sector)
        {
            if (sector < SectorCount) s_changedFrame[sector] = s_frame;
        }
    }
}
