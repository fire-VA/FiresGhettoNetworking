using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Lets the server skip rescanning parts of the world where nothing changed, and walk only what changed in the parts
    /// that did. A sector whose last scan for a player found nothing to send, and which nothing has entered or changed in
    /// since, cannot have anything to send now. A sector that did change keeps a short journal of the objects that changed,
    /// so one ticking torch costs one object instead of every object standing around it.
    /// </summary>
    [HarmonyPatch]
    internal static class SectorChangeTracker
    {
        public static ConfigEntry<bool> ConfigEnabled;
        public static ConfigEntry<bool> ConfigJournalEnabled;

        private const int SectorCount = 512 * 512;
        public static ConfigEntry<float> ConfigFullRescanSeconds;
        private const int MaxSectorStates = 4096;
        // What a player is still owed in one sector is a subset of that sector's contents, so walking it is never more
        // work than walking the sector - the only thing this bounds is memory, at about 24 bytes an entry. At 256 it
        // bound something else entirely: a lived-in base sector owes more than that continuously, so its state was
        // thrown away on every scan and it walked the long way forever, which is exactly the case this was built for.
        private const int MaxPending = 4096;
        private const int JournalCapacity = 512;
        private const int MaxJournals = 1024;
        private const int JournalIdleFrames = 1800;

        private sealed class PeerScans
        {
            public readonly Dictionary<int, SectorState> Sectors = new Dictionary<int, SectorState>();
            public double NextFullRescan;
        }

        /// <summary>
        /// What one player's last full walk of one sector found. WalkedAt is the frame it happened, Pending is what was
        /// still owed to them then. Anything that becomes owed afterwards has to be journalled, so the two together
        /// answer the sector without touching the objects that were already delivered and have not changed since.
        ///
        /// The earlier version only took this shortcut for a sector found completely delivered, which a lived-in base
        /// never is: something in it always just changed. Those sectors walked every object they held, every scan.
        /// </summary>
        private sealed class SectorState
        {
            public int WalkedAt;
            public readonly List<Owed> Pending = new List<Owed>();
        }

        private struct Owed
        {
            public ZDO Zdo;
            public ZDOID Uid;
        }

        /// <summary>
        /// The objects that changed in one sector, newest last. Entries are kept until the ring wraps; CoverageFrom is the
        /// oldest frame the journal can still answer for, so a player whose last walk of the sector is older than that has to
        /// walk it the long way again.
        /// </summary>
        private sealed class SectorJournal
        {
            public readonly ZDO[] Zdos = new ZDO[JournalCapacity];
            public readonly ZDOID[] Uids = new ZDOID[JournalCapacity];
            public readonly int[] Frames = new int[JournalCapacity];
            public int Head;
            public int Count;
            public int CoverageFrom;
            public int LastMarkFrame;

            public void Append(ZDO zdo, int frame)
            {
                if (Count == JournalCapacity) CoverageFrom = Frames[Head] + 1;
                else Count++;
                Zdos[Head] = zdo;
                Uids[Head] = zdo.m_uid;
                Frames[Head] = frame;
                Head = Head + 1 == JournalCapacity ? 0 : Head + 1;
                LastMarkFrame = frame;
            }
        }

        private static readonly int[] s_changedFrame = new int[SectorCount];
        private static readonly SectorJournal[] s_journals = new SectorJournal[SectorCount];
        private static readonly List<uint> s_journalSectors = new List<uint>();
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
        private static readonly HashSet<ZDOID> s_seen = new HashSet<ZDOID>();
        private static readonly List<Owed> s_rebuilt = new List<Owed>();
        private static ZDO s_lastMarked;
        private static int s_lastMarkedFrame;

        internal static long SectorsScanned;
        internal static long SectorsSkipped;
        internal static long FullRescans;
        internal static long ObjectsWalked;
        internal static long ObjectsWalkedInFullRescans;
        internal static long JournalWalks;
        internal static long ObjectsWalkedInJournals;
        internal static long JournalMisses;
        internal static long SkipsChecked;
        internal static long SkipsWrong;
        internal static long SkipsWrongObjects;
        internal static long LongWalks;
        internal static long LongWalksNoState;

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("04 - Networking", "Skip Unchanged Areas", true,
                "Every world update to a player rescans every object around them for anything they have not been sent, which in a\n" +
                "big base means tens of thousands of objects many times a second. With this on, the server skips the parts of that\n" +
                "area where nothing has changed since they were last walked and nothing is still owed, and rescans everything every two\n" +
                "seconds. DEDICATED SERVER ONLY.");

            ConfigJournalEnabled = config.Bind("04 - Networking", "Walk Only Changed Objects", true,
                "A sector stops being skippable the moment anything in it changes, and a base sector always has something changing,\n" +
                "so the skip above never reaches the sectors that cost the most. With this on, each changed sector also records\n" +
                "which objects changed, and a player's update walks those instead of everything standing around them. Needs 'Skip\n" +
                "Unchanged Areas'. DEDICATED SERVER ONLY.");

            ConfigFullRescanSeconds = config.Bind("04 - Networking", "Recheck Everything Every", 15f,
                new ConfigDescription(
                    "How often each player's whole surroundings are rechecked object by object, ignoring what changed.\n" +
                    "Every write is already tracked, so this only exists to catch anything that changed without being\n" +
                    "noticed. Rechecking every two seconds was most of what the skipping above was saving. The report\n" +
                    "line says whether a recheck ever finds something the tracking missed - if that stays at zero, this\n" +
                    "can go higher. DEDICATED SERVER ONLY.",
                    new AcceptableValueRange<float>(2f, 300f)));
        }

        [HarmonyPatch(typeof(ZNet), "Start"), HarmonyPostfix]
        static void OnStart(ZNet __instance)
        {
            s_tracking = __instance.IsServer();
            ResetTracking();
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown"), HarmonyPostfix]
        static void OnShutdown()
        {
            s_tracking = false;
            ResetTracking();
        }

        private static void ResetTracking()
        {
            s_scans.Clear();
            Array.Clear(s_changedFrame, 0, SectorCount);
            for (int i = 0; i < s_journalSectors.Count; i++) s_journals[s_journalSectors[i]] = null;
            s_journalSectors.Clear();
            s_lastMarked = null;
            s_frame = 1;
        }

        [HarmonyPatch(typeof(ZNet), "Update"), HarmonyPrefix]
        static void NextFrame() => s_frame++;

        // Loading files in puts objects straight into their sectors without going through AddToSector, so nothing stamps
        // them. Vanilla only does that before anyone has connected, but a mod that loads later would otherwise leave
        // players holding sectors marked clean that have objects they were never sent.
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.LoadChunks)), HarmonyPostfix]
        static void OnLoadChunks() => ResetTracking();

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
        static void OnAddToSector(ZDO zdo, ZoneSystem.SectorIndex sectorIndex)
        {
            if (s_tracking) Mark(sectorIndex.Sector, zdo);
        }

        // An object leaving takes nothing with it that a player still needs, so the sector is stamped but nothing is
        // journalled: a walk that finds no journalled change correctly calls the sector clean again.
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RemoveFromSector)), HarmonyPostfix]
        static void OnRemoveFromSector(ZoneSystem.SectorIndex sectorIndex)
        {
            if (s_tracking) Mark(sectorIndex.Sector, null);
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
                scans.NextFullRescan = now + (ConfigFullRescanSeconds?.Value ?? 15f);
                if (scans.Sectors.Count > MaxSectorStates) scans.Sectors.Clear();
                FullRescans++;
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
            bool haveState = s_scan.Sectors.TryGetValue(key, out var state);

            // Nothing owed and nothing changed since the walk means nothing to send. Anything still owed has to be
            // offered again, because an object nobody changed stays owed until it actually goes out.
            if (!s_fullScan && haveState && state.Pending.Count == 0 && s_changedFrame[index.Sector] < state.WalkedAt)
            {
                SectorsSkipped++;
                return false;
            }
            SectorsScanned++;

            var peer = s_scanPeer;
            if (!s_fullScan && haveState && ConfigJournalEnabled != null && ConfigJournalEnabled.Value
                && WalkShortList(index.Sector, peer, objects, distant, state, key))
                return false;

            // A full rescan reaching a sector the incremental path would have skipped is the one chance to check that
            // the skip was right. If it finds something owed here, a change went unstamped and the stamps cannot be
            // trusted - which is exactly the failure that stretching the rescan interval would hide.
            bool wouldHaveSkipped = s_fullScan && haveState && state.Pending.Count == 0
                && s_changedFrame[index.Sector] < state.WalkedAt;
            if (wouldHaveSkipped) SkipsChecked++;

            if (!s_fullScan)
            {
                LongWalks++;
                if (!haveState) LongWalksNoState++;
            }

            if (!haveState)
            {
                state = new SectorState();
                s_scan.Sectors[key] = state;
            }
            state.Pending.Clear();

            var sectorObjects = s_objectsBySector(zdoMan)[index.Sector];
            int walked = sectorObjects?.Count ?? 0;
            if (sectorObjects != null)
            {
                for (int i = 0; i < sectorObjects.Count; i++)
                {
                    var zdo = sectorObjects[i];
                    if ((distant && !zdo.Distant) || !Unsent(peer, zdo)) continue;
                    objects.Add(zdo);
                    state.Pending.Add(new Owed { Zdo = zdo, Uid = zdo.m_uid });
                }
            }
            if (!distant && s_portalObjects(zdoMan).TryGetValue(index, out var portals))
            {
                walked += portals.Count;
                for (int i = 0; i < portals.Count; i++)
                {
                    if (!Unsent(peer, portals[i])) continue;
                    objects.Add(portals[i]);
                    state.Pending.Add(new Owed { Zdo = portals[i], Uid = portals[i].m_uid });
                }
            }
            ObjectsWalked += walked;
            if (s_fullScan) ObjectsWalkedInFullRescans += walked;

            if (wouldHaveSkipped && state.Pending.Count > 0)
            {
                SkipsWrong++;
                SkipsWrongObjects += state.Pending.Count;
            }

            // A backlog too big to be worth carrying is dropped rather than re-walked every scan; the next scan rebuilds
            // it the long way. The full rescan rebuilds it anyway, which is also what clears out entries for objects
            // that have since moved out of the player's reach and would otherwise stay owed forever.
            if (state.Pending.Count > MaxPending) s_scan.Sectors.Remove(key);
            else state.WalkedAt = s_frame;
            return false;
        }

        /// <summary>
        /// Answers the sector from what the player is still owed plus what the journal says changed since, instead of
        /// walking everything in it. Returns false when the journal cannot reach back to the last walk.
        /// </summary>
        private static bool WalkShortList(uint sector, ZDOMan.ZDOPeer peer, List<ZDO> objects, bool distant, SectorState state, int key)
        {
            var journal = s_journals[sector];
            bool journalNeeded = s_changedFrame[sector] >= state.WalkedAt;
            if (journalNeeded && (journal == null || state.WalkedAt < journal.CoverageFrom))
            {
                JournalMisses++;
                return false;
            }

            s_seen.Clear();
            s_rebuilt.Clear();
            int walked = 0;

            for (int i = 0; i < state.Pending.Count; i++)
            {
                var owed = state.Pending[i];
                walked++;
                if (owed.Zdo == null || owed.Zdo.m_uid != owed.Uid) continue;
                if (!s_seen.Add(owed.Uid)) continue;
                if (!Unsent(peer, owed.Zdo)) continue;
                objects.Add(owed.Zdo);
                s_rebuilt.Add(owed);
            }

            if (journalNeeded)
            {
                int slot = journal.Head - journal.Count;
                if (slot < 0) slot += JournalCapacity;

                int lo = 0, hi = journal.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    int probe = slot + mid;
                    if (probe >= JournalCapacity) probe -= JournalCapacity;
                    if (journal.Frames[probe] < state.WalkedAt) lo = mid + 1;
                    else hi = mid;
                }

                for (int i = lo; i < journal.Count; i++)
                {
                    int at = slot + i;
                    if (at >= JournalCapacity) at -= JournalCapacity;
                    walked++;
                    var zdo = journal.Zdos[at];
                    if (zdo == null || zdo.m_uid != journal.Uids[at]) continue;
                    if (distant && !zdo.Distant) continue;
                    if (!s_seen.Add(journal.Uids[at])) continue;
                    if (!Unsent(peer, zdo)) continue;
                    objects.Add(zdo);
                    s_rebuilt.Add(new Owed { Zdo = zdo, Uid = zdo.m_uid });
                }
            }

            JournalWalks++;
            ObjectsWalked += walked;
            ObjectsWalkedInJournals += walked;

            // Everything owed has already been offered above, so this scan is complete either way. A backlog that has
            // grown past what is worth carrying just forgets the sector, and the next scan rebuilds it the long way.
            if (s_rebuilt.Count > MaxPending)
            {
                s_scan.Sectors.Remove(key);
                return true;
            }
            state.Pending.Clear();
            state.Pending.AddRange(s_rebuilt);
            state.WalkedAt = s_frame;
            return true;
        }


        private static bool Unsent(ZDOMan.ZDOPeer peer, ZDO zdo)
        {
            return !peer.m_zdos.TryGetValue(zdo.m_uid, out var sent)
                || zdo.OwnerRevision > sent.m_ownerRevision
                || zdo.DataRevision > sent.m_dataRevision;
        }

        // A single object commonly writes several values in one frame, and vanilla's SetOwner bumps the owner revision
        // right after SetOwnerInternal, so the same object arrives here several times in a row. Collapsing that here also
        // skips the position read below, which is why the check comes first.
        private static void Mark(ZDO zdo)
        {
            if (ReferenceEquals(zdo, s_lastMarked) && s_lastMarkedFrame == s_frame) return;
            s_lastMarked = zdo;
            s_lastMarkedFrame = s_frame;
            Mark(ZoneSystem.GetSectorIndex(zdo.GetPosition()).Sector, zdo);
        }

        private static void Mark(uint sector, ZDO zdo)
        {
            if (sector >= SectorCount) return;
            s_changedFrame[sector] = s_frame;
            if (zdo == null) return;

            var journal = s_journals[sector];
            if (journal == null)
            {
                journal = NewJournal(sector);
                if (journal == null) return;
            }
            journal.Append(zdo, s_frame);
        }

        private static SectorJournal NewJournal(uint sector)
        {
            if (s_journalSectors.Count >= MaxJournals)
            {
                SweepIdleJournals();
                if (s_journalSectors.Count >= MaxJournals) return null;
            }
            var journal = new SectorJournal { CoverageFrom = s_frame };
            s_journals[sector] = journal;
            s_journalSectors.Add(sector);
            return journal;
        }

        private static void SweepIdleJournals()
        {
            int cutoff = s_frame - JournalIdleFrames;
            int kept = 0;
            for (int i = 0; i < s_journalSectors.Count; i++)
            {
                uint sector = s_journalSectors[i];
                var journal = s_journals[sector];
                if (journal != null && journal.LastMarkFrame >= cutoff)
                {
                    s_journalSectors[kept++] = sector;
                    continue;
                }
                s_journals[sector] = null;
            }
            s_journalSectors.RemoveRange(kept, s_journalSectors.Count - kept);
        }
    }
}
