using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using FiresGhettoNetworkMod.AutoTune;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.57 ([fgn], lead 11-08 option (b) after 1.6.56's (a); log-proven on the IsoDedi BlumpkinPre town): the SSS server pass
    /// without walking every nearby object. 1.6.56's pass, at ~6 passes a second, did all of this EVERY pass:
    ///  - collected every ZDO in every player's area (131,045 near in the town);
    ///  - filtered all of them (8.3 ms);
    ///  - picked the next creates by walking the filtered list again;
    ///  - handed the full lists to vanilla's RemoveObjects (6.3 ms).
    /// Now:
    ///  - The full collect + filter runs once when the set of player zones changes. It seeds one queue of uncreated objects and the
    ///    near / distant SECTOR sets of the area (the same rings and tests as vanilla FindSectorObjects, union over every source).
    ///  - New ZDOs enter at once through ZDOMan.AddToSector / InitialAddToSector. A 4,000-entry slice of the last full list is
    ///    re-checked each pass, so nothing is missed.
    ///  - Creates pop in waves (ring from the nearest source + type rank, as the client queue): solid / terrain first, the nearest
    ///    first, under 'Server Instantiation Budget Ms'.
    ///  - Removal walks only the live INSTANCES (far fewer than ZDOs since 1.6.56). An instance whose sector is in the area (or a
    ///    distant object in the distant band) stays; vanilla's own RemoveObjects removes the rest, from that keep-list.
    ///  - ServerStaticPieces' rule holds (1.6.59: 40 m around each simulated object, 16 m cells): a building piece outside it is not
    ///    created; it is parked per cell and comes back when a creature / ship comes near. One already built is released at most 100 a pass.
    /// The proof self-check (key "Event-Driven Create Queue Self-Check") compares, every 10 s, the full collect against the queue
    /// and the keep-list; mismatches must stay 0.
    /// Key "Server Event-Driven Create Queue" (a fix, ON). Any exception: off for the session, the 1.6.56 pass takes over.
    /// </summary>
    [HarmonyPatch]
    internal static class ServerStream
    {
        private const int Types = 4, Rings = 32, Waves = Rings + Types - 1;
        private const int SweepPerPass = 4000, ServerCap = 100;
        private const float ReportSeconds = 60f;

        private static readonly List<ZDO>[,] s_buckets = new List<ZDO>[Types, Rings];
        private static readonly List<ZDO> s_distant = new List<ZDO>();
        private static readonly Dictionary<ZDOID, ZDO> s_queued = new Dictionary<ZDOID, ZDO>();
        private static readonly Dictionary<ZDOID, Vector3> s_simQueued = new Dictionary<ZDOID, Vector3>();   // queued objects of the simulated set (few), by where they were queued
        private static readonly Dictionary<Vector2s, List<ZDO>> s_waitLoad = new Dictionary<Vector2s, List<ZDO>>();   // zone not loaded on the server yet
        private static readonly Dictionary<long, List<ZDO>> s_waitSim = new Dictionary<long, List<ZDO>>();    // building pieces outside every simulated area, by 16 m cell (ServerStaticPieces.CellOf)
        private static readonly List<long> s_tmpCells = new List<long>();
        private static readonly Dictionary<Vector2s, int> s_ringOf = new Dictionary<Vector2s, int>();                  // near sector -> ring from the nearest source
        private static readonly HashSet<Vector2s> s_distantSet = new HashSet<Vector2s>();
        private static readonly List<Vector2s> s_sources = new List<Vector2s>(8), s_lastSources = new List<Vector2s>(8), s_tmpZones = new List<Vector2s>();
        private static readonly List<ZDO> s_snapshot = new List<ZDO>(), s_keep = new List<ZDO>(), s_noDistant = new List<ZDO>();
        private static readonly bool[] s_ready = new bool[Types];
        private static int s_sweep;
        private const float RemovePeriodSeconds = 2f, SimAreaPeriodSeconds = 0.5f;   // 1.6.60
        private static float s_nextRemove, s_nextSimArea;
        private static bool s_removeDirty = true, s_simAreaDirty = true;
        private static int p_removePasses, p_simAreaBuilds;
        private static bool s_seeded, s_broken, s_active, s_saidOn;
        private static SimulationDistance s_sd;

        // proof (60 s)
        private static int p_passes, p_reseeds, p_created, p_events, p_sweepFound, p_released, p_parkedSim, p_parkedLoad, p_stale;
        private static double p_seedMs, p_createMs, p_createMax, p_keepMs, p_removeMs, p_totalMs, p_totalMax;
        private static float p_next, p_nextCheck;
        private static string p_check = "self-check off";

        static ServerStream() { for (int t = 0; t < Types; t++) for (int r = 0; r < Rings; r++) s_buckets[t, r] = new List<ZDO>(); }

        internal static bool Enabled => !s_broken && FiresGhettoNetworkMod.ConfigServerEventQueue != null && FiresGhettoNetworkMod.ConfigServerEventQueue.Value
                                        && (FiresGhettoNetworkMod.ConfigServerInstantiationBudgetMs?.Value ?? 0) > 0
                                        && !ValheimCommunityPatchCompat.SchedulesObjectCreation;

        /// <summary>One server pass in place of collect + filter + CreateObjects + RemoveObjects. False = it threw: the caller runs the 1.6.56 pass.</summary>
        internal static bool Pass(ZNetScene scene)
        {
            if (!ZoneLoadPatches.ServerHelpersReady()) return false;
            try { PassInner(scene); return true; }
            catch (Exception ex)
            {
                s_broken = true; s_active = false; ClearAll();
                LoggerOptions.LogWarning($"[SSS] the server create queue threw and is off for this session; the full server pass takes over. {ex}");
                return false;
            }
        }

        private static void PassInner(ZNetScene scene)
        {
            s_active = true;
            if (!s_saidOn) { s_saidOn = true; LoggerOptions.LogInfo("[SSS] server create queue on: the server pass no longer walks every nearby object; creates come from one queue, removal checks only live instances (Server Event-Driven Create Queue)"); }
            long t0 = Stopwatch.GetTimestamp();
            float now = Time.unscaledTime;
            s_sd = SimDistance.Widened(ServerAuthorityPatches.ExtendedZoneRadius());

            // ---- sources: every ready player's zone, kept companion areas and SSS test areas (the same list the full pass uses) ----
            s_sources.Clear();
            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers()) if (peer.IsReady()) s_sources.Add(FgnRing.Sector(ServerAuthorityPatches.GetPredictedRefPos(peer)));
            foreach (Vector3 p in SssTestAreas.Points) s_sources.Add(FgnRing.Sector(p));
            foreach (Vector3 p in CompanionKeepAlive.Points) s_sources.Add(FgnRing.Sector(p));
            if (!s_seeded || !SameSources()) Reseed(scene, now);
            else Sweep(scene);

            ReleaseLoadedZones(now);
            // 1.6.60: the simulated area (a walk of the live instances for creatures / ships) is rebuilt every SimAreaPeriodSeconds; 40 m of
            // slack covers a creature's movement in between, and a newly queued one adds its own area at once (ReleaseSimAt)
            if (s_simAreaDirty || now >= s_nextSimArea) { s_simAreaDirty = false; s_nextSimArea = now + SimAreaPeriodSeconds; p_simAreaBuilds++; UpdateSimZones(scene); }

            // ---- create ----
            long tc = Stopwatch.GetTimestamp();
            int budgetMs = FiresGhettoNetworkMod.ConfigServerInstantiationBudgetMs.Value;
            long budgetTicks = (long)budgetMs * Stopwatch.Frequency / 1000L;
            var sw = Stopwatch.StartNew(); int created = 0;
            for (int w = 0; w < Waves; w++)
                for (int t = Types - 1; t >= 0; t--)
                {
                    int r = w - (Types - 1 - t);
                    if (r < 0 || r >= Rings) continue;
                    if (DrainNear(scene, s_buckets[t, r], sw, budgetTicks, ref created)) goto distant;
                }
            distant:
            DrainDistant(scene, sw, budgetTicks, ref created);
            double cms = (Stopwatch.GetTimestamp() - tc) * 1000.0 / Stopwatch.Frequency;
            p_created += created; p_createMs += cms; if (cms > p_createMax) p_createMax = cms;

            // ---- remove ----
            // 1.6.60 (log-proven on the IsoDedi town, 1.6.59 settled: "whole pass 16.86 ms, create 0.00, keep-list 10.20, remove 2.71" at ~7
            // passes a second, 59,517 instances): the removal walk was the only per-pass cost left once the town is built. Now it is SLICED: a
            // cursor walks a snapshot of the live instances under a measured RemoveBudgetMs a pass (the budget is checked inside the loop), a
            // full cycle every ~2 s on the settled town. An instance the area no longer keeps is removed with vanilla RemoveObjects' own steps,
            // in its order (ResetZDO, Destroy the GameObject, DestroyZDO only if not persistent and ours, drop it from m_instances).
            // Vanilla's RemoveObjects cannot be sliced (one call removes every instance missing from its keep-list), so when ANOTHER mod
            // patches RemoveObjects on this server, the whole vanilla call runs every RemovePeriodSeconds instead, so their patch still runs.
            long tk = Stopwatch.GetTimestamp(), tr = tk;
            if (ForeignRemovePatch())
            {
                if (s_removeDirty || now >= s_nextRemove)
                {
                    s_removeDirty = false; s_nextRemove = now + RemovePeriodSeconds; p_removePasses++;
                    BuildKeepList(scene);
                    tr = Stopwatch.GetTimestamp();
                    try { scene.RemoveObjects(s_keep, s_noDistant); }
                    catch (NullReferenceException) when (ServerAuthorityPatches.OrphanPruneEnabled())
                    {
                        ServerAuthorityPatches.PruneOrphanInstances(scene);
                        BuildKeepList(scene);
                        scene.RemoveObjects(s_keep, s_noDistant);
                    }
                }
            }
            else
            {
                SlicedRemove(scene);
                tr = Stopwatch.GetTimestamp();
            }
            long te = Stopwatch.GetTimestamp();
            p_keepMs += (tr - tk) * 1000.0 / Stopwatch.Frequency;
            p_removeMs += (te - tr) * 1000.0 / Stopwatch.Frequency;
            double total = (te - t0) * 1000.0 / Stopwatch.Frequency;
            p_totalMs += total; if (total > p_totalMax) p_totalMax = total;
            p_passes++;

            if (FiresGhettoNetworkMod.ConfigEventQueueSelfCheck != null && FiresGhettoNetworkMod.ConfigEventQueueSelfCheck.Value && now >= p_nextCheck)
            { p_nextCheck = now + 10f; SelfCheck(scene); }
            Report(scene, now);
        }

        private static bool SameSources()
        {
            if (s_sources.Count != s_lastSources.Count) return false;
            for (int i = 0; i < s_sources.Count; i++) if (s_sources[i].x != s_lastSources[i].x || s_sources[i].y != s_lastSources[i].y) return false;
            return true;
        }

        // ---------------- area ----------------

        /// <summary>The full collect (vanilla's own FindObjects over every source, shared visited set), the area's sector sets, the queue.</summary>
        private static void Reseed(ZNetScene scene, float now)
        {
            long t0 = Stopwatch.GetTimestamp();
            ClearAll();
            s_lastSources.Clear(); s_lastSources.AddRange(s_sources);
            BuildSectorSets();
            ServerAuthorityPatches.CollectZdosFromAllPeerActiveAreas(s_sd);
            s_snapshot.Clear(); s_snapshot.AddRange(ServerAuthorityPatches._cdoNearScratch);
            UpdateSimZones(scene);
            foreach (ZDO zdo in s_snapshot) TryAddNear(zdo, scene);
            foreach (ZDO zdo in ServerAuthorityPatches._cdoDistantScratch) TryAddDistant(zdo, scene);
            s_seeded = true; p_reseeds++;
            s_removeDirty = true; s_simAreaDirty = true;   // 1.6.60: the area changed: remove and rebuild the simulated area this pass
            p_seedMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>The same sectors vanilla's FindSectorObjects reads, per source, into one ring map (nearest source wins) and the distant set.
        /// Vanilla's distant quirk is kept: a side column's second sector is tested by the first one's position.</summary>
        private static void BuildSectorSets()
        {
            ZoneSystem zones = ZoneSystem.instance;
            int near = s_sd.NearSimulationDistance, total = s_sd.TotalSimulationDistance;
            foreach (Vector2s c in s_sources)
            {
                AddNear(c, 0);
                for (int i = 1; i <= near; i++)
                {
                    for (int x = c.x - i; x <= c.x + i; x++) { TestNear(zones, c, new Vector2s(x, c.y - i), near, i); TestNear(zones, c, new Vector2s(x, c.y + i), near, i); }
                    for (int y = c.y - i + 1; y <= c.y + i - 1; y++) { TestNear(zones, c, new Vector2s(c.x - i, y), near, i); TestNear(zones, c, new Vector2s(c.x + i, y), near, i); }
                }
            }
            foreach (Vector2s c in s_sources)
            {
                int first = 1 + (s_sd.IsClassic ? near : 0);
                for (int i = first; i <= total; i++)
                {
                    for (int x = c.x - i; x <= c.x + i; x++)
                    {
                        var a = new Vector2s(x, c.y - i); var b = new Vector2s(x, c.y + i);
                        bool inA = s_sd.IsClassic || zones.ZonesWithinRadius(c, a, total, true);
                        if (inA && !s_ringOf.ContainsKey(a)) s_distantSet.Add(a);
                        if (inA && !s_ringOf.ContainsKey(b)) s_distantSet.Add(b);
                    }
                    for (int y = c.y - i + 1; y <= c.y + i - 1; y++)
                    {
                        var a = new Vector2s(c.x - i, y); var b = new Vector2s(c.x + i, y);
                        bool inA = s_sd.IsClassic || zones.ZonesWithinRadius(c, a, total, true);
                        if (inA && !s_ringOf.ContainsKey(a)) s_distantSet.Add(a);
                        if (inA && !s_ringOf.ContainsKey(b)) s_distantSet.Add(b);
                    }
                }
            }
        }

        private static void TestNear(ZoneSystem zones, Vector2s c, Vector2s s, int near, int ring)
        {
            if (s_sd.IsClassic || zones.ZonesWithinRadius(c, s, near, false)) AddNear(s, ring);
        }

        private static void AddNear(Vector2s s, int ring)
        {
            if (!s_ringOf.TryGetValue(s, out int old) || ring < old) s_ringOf[s] = ring;
        }

        // ---------------- queue ----------------

        private static bool NearFilter(ZDO zdo, ZNetScene scene)
        {
            if (zdo == null || zdo.Created || !zdo.IsValid() || zdo.m_uid.IsNone()) return false;
            int prefab = zdo.m_prefab;
            return prefab != 0 && scene.HasPrefab(prefab);   // a prefab the server lacks is left to its clients (as the full pass)
        }

        private static bool TryAddNear(ZDO zdo, ZNetScene scene)
        {
            if (!NearFilter(zdo, scene) || s_queued.ContainsKey(zdo.m_uid)) return false;
            Vector2s sec = zdo.GetSector();
            if (!s_ringOf.TryGetValue(sec, out int ring)) return false;
            s_queued[zdo.m_uid] = zdo;
            if (ServerOwnershipPatchesV3.IsSimulatedPrefab(zdo.m_prefab)) { Vector3 p = zdo.GetPosition(); s_simQueued[zdo.m_uid] = p; ReleaseSimAt(p, scene); }   // its floor is released in this same pass
            Place(zdo, sec, ring, scene);
            return true;
        }

        /// <summary>Into its wave bucket, or parked: zone not loaded on the server yet, or a building piece outside every simulated area.</summary>
        private static void Place(ZDO zdo, Vector2s sec, int ring, ZNetScene scene)
        {
            if (!ZoneSystem.instance.m_zones.ContainsKey(sec)) { Park(s_waitLoad, sec, zdo); p_parkedLoad++; return; }
            if (OutsideSim(zdo, scene)) { ParkSim(zdo); return; }
            int type = Mathf.Clamp((int)zdo.Type, 0, Types - 1);
            s_buckets[type, Math.Min(Rings - 1, ring)].Add(zdo);
        }

        private static bool TryAddDistant(ZDO zdo, ZNetScene scene)
        {
            if (!NearFilter(zdo, scene) || !zdo.Distant || s_queued.ContainsKey(zdo.m_uid)) return false;
            if (!s_distantSet.Contains(zdo.GetSector())) return false;
            s_queued[zdo.m_uid] = zdo;
            s_distant.Add(zdo);
            return true;
        }

        private static void Park(Dictionary<Vector2s, List<ZDO>> map, Vector2s sec, ZDO zdo)
        {
            if (!map.TryGetValue(sec, out List<ZDO> l)) map[sec] = l = new List<ZDO>();
            l.Add(zdo);
        }

        private static bool s_rule;   // ServerStaticPieces' rule this pass (BeginCells)
        private static bool OutsideSim(ZDO zdo, ZNetScene scene) => s_rule && ServerStaticPieces.IsStaticPiece(zdo.m_prefab, scene) && !ServerStaticPieces.InSimulatedArea(zdo.GetPosition());

        private static void ParkSim(ZDO zdo)
        {
            long cell = ServerStaticPieces.CellOf(zdo.GetPosition());
            if (!s_waitSim.TryGetValue(cell, out List<ZDO> l)) s_waitSim[cell] = l = new List<ZDO>();
            l.Add(zdo); p_parkedSim++; ServerStaticPieces.CountParked();
        }

        private static void Dequeue(ZDO zdo) { s_queued.Remove(zdo.m_uid); s_simQueued.Remove(zdo.m_uid); }

        private static void Sweep(ZNetScene scene)
        {
            int n = s_snapshot.Count; if (n == 0) return;
            if (s_sweep >= n) s_sweep = 0;
            int end = Math.Min(n, s_sweep + SweepPerPass);
            for (int i = s_sweep; i < end; i++) if (TryAddNear(s_snapshot[i], scene)) p_sweepFound++;
            s_sweep = end;
        }

        private static void ReleaseLoadedZones(float now)
        {
            if (s_waitLoad.Count == 0) return;
            s_tmpZones.Clear(); s_tmpZones.AddRange(s_waitLoad.Keys);
            ZNetScene scene = ZNetScene.instance;
            foreach (Vector2s z in s_tmpZones)
            {
                if (!ZoneSystem.instance.m_zones.ContainsKey(z)) continue;
                List<ZDO> l = s_waitLoad[z]; s_waitLoad.Remove(z);
                int ring = s_ringOf.TryGetValue(z, out int r) ? r : Rings - 1;
                foreach (ZDO zdo in l) if (Still(zdo)) Place(zdo, z, ring, scene);
            }
        }

        /// <summary>The simulated area this pass (ServerStaticPieces' 40 m cells around each simulated-set object, live or queued, plus kept /
        /// test points); cells that just became simulated get their parked pieces back. Rule off: every parked piece goes back.</summary>
        private static void UpdateSimZones(ZNetScene scene)
        {
            long t0 = Stopwatch.GetTimestamp();
            s_rule = ServerStaticPieces.BeginCells(CompanionKeepAlive.Points, SssTestAreas.Points);
            int sim = 0;
            if (s_rule)
            {
                foreach (KeyValuePair<ZDO, ZNetView> kv in scene.m_instances)
                {
                    ZDO zdo = kv.Key;
                    if (zdo != null && ServerOwnershipPatchesV3.IsSimulatedPrefab(zdo.m_prefab)) { ServerStaticPieces.AddSimulated(zdo.GetPosition()); sim++; }
                }
                foreach (KeyValuePair<ZDOID, Vector3> kv in s_simQueued) { ServerStaticPieces.AddSimulated(kv.Value); sim++; }
                ServerStaticPieces.EndCells(sim, t0);
            }
            ReleaseParkedInArea(scene);
        }

        /// <summary>Parked pieces whose cell is simulated now (or every one, with the rule off) go back into the waves.</summary>
        private static void ReleaseParkedInArea(ZNetScene scene)
        {
            if (s_waitSim.Count == 0) return;
            s_tmpCells.Clear();
            foreach (long c in s_waitSim.Keys) if (!s_rule || ServerStaticPieces.InSimulatedCell(c)) s_tmpCells.Add(c);
            foreach (long c in s_tmpCells)
            {
                List<ZDO> l = s_waitSim[c]; s_waitSim.Remove(c);
                foreach (ZDO zdo in l)
                {
                    if (!Still(zdo)) continue;
                    Vector2s sec = zdo.GetSector();
                    if (!s_ringOf.TryGetValue(sec, out int ring)) { Dequeue(zdo); continue; }
                    Place(zdo, sec, ring, scene);
                }
            }
        }

        /// <summary>A simulated object just queued at <paramref name="p"/>: its 40 m becomes simulated now and the pieces parked there go back
        /// into the waves, so the floor is in the queue before the creature is created (solid before creatures in the same ring).</summary>
        private static void ReleaseSimAt(Vector3 p, ZNetScene scene)
        {
            if (!s_rule) return;
            ServerStaticPieces.AddSimulated(p);
            ReleaseParkedInArea(scene);
        }

        /// <summary>Still the same queued, uncreated, valid ZDO (pooled ZDOs are reused: the entry must be the same object).</summary>
        private static bool Still(ZDO zdo)
        {
            if (zdo == null || !s_queued.TryGetValue(zdo.m_uid, out ZDO q) || !ReferenceEquals(q, zdo)) { p_stale++; return false; }
            if (zdo.Created || !zdo.IsValid()) { Dequeue(zdo); return false; }
            return true;
        }

        /// <summary>True when the pass must stop (cap, budget, a heavy object).</summary>
        private static bool DrainNear(ZNetScene scene, List<ZDO> b, Stopwatch sw, long budgetTicks, ref int created)
        {
            while (b.Count > 0)
            {
                if (created >= ServerCap || (created > 0 && sw.ElapsedTicks >= budgetTicks)) return true;
                ZDO zdo = b[b.Count - 1]; b.RemoveAt(b.Count - 1);
                if (!Still(zdo)) continue;
                Vector2s sec = zdo.GetSector();
                if (!s_ringOf.TryGetValue(sec, out int ring)) { Dequeue(zdo); continue; }   // moved out of the area
                if (!ZoneSystem.instance.m_zones.ContainsKey(sec)) { Park(s_waitLoad, sec, zdo); continue; }
                if (OutsideSim(zdo, scene)) { ParkSim(zdo); continue; }
                if (s_rule && ServerStaticPieces.IsStaticPiece(zdo.m_prefab, scene)) ServerStaticPieces.CountInside();
                if (!ZoneSystem.instance.IsZoneReadyForType(sec, zdo.Type)) { Park(s_waitLoad, sec, zdo); continue; }   // re-checked once the zone settles
                if (!ZoneLoadPatches.ServerFits(zdo, created, sw.ElapsedTicks, budgetTicks)) { b.Add(zdo); return true; }
                Dequeue(zdo);
                ZoneLoadPatches.ServerTimedCreate(scene, zdo, sw);
                if (zdo.Created) created++;
            }
            return false;
        }

        private static void DrainDistant(ZNetScene scene, Stopwatch sw, long budgetTicks, ref int created)
        {
            while (s_distant.Count > 0)
            {
                if (created >= ServerCap || (created > 0 && sw.ElapsedTicks >= budgetTicks)) return;
                ZDO zdo = s_distant[s_distant.Count - 1]; s_distant.RemoveAt(s_distant.Count - 1);
                if (!Still(zdo)) continue;
                if (!s_distantSet.Contains(zdo.GetSector()) && !s_ringOf.ContainsKey(zdo.GetSector())) { Dequeue(zdo); continue; }
                if (!ZoneLoadPatches.ServerFits(zdo, created, sw.ElapsedTicks, budgetTicks)) { s_distant.Add(zdo); return; }
                Dequeue(zdo);
                ZoneLoadPatches.ServerTimedCreate(scene, zdo, sw);
                if (zdo.Created) created++;
            }
        }

        // ---------------- remove ----------------

        /// <summary>What vanilla's RemoveObjects keeps, from the live instances: in the near area (an instance in a zone still loading
        /// stays, as 1.6.31), or a distant object in the distant band; minus building pieces outside every simulated area, at most 100 a pass.</summary>
        private static void BuildKeepList(ZNetScene scene)
        {
            s_keep.Clear();
            ServerStaticPieces.ResetReleases();   // 1.6.60: at most 100 released per removal pass
            foreach (KeyValuePair<ZDO, ZNetView> kv in scene.m_instances)
            {
                ZDO zdo = kv.Key;
                if (zdo == null) continue;
                Vector2s sec = zdo.GetSector();
                bool near = s_ringOf.ContainsKey(sec);
                if (near)
                {
                    if (OutsideSim(zdo, scene) && ServerStaticPieces.Release()) { p_released++; continue; }   // at most 100 a pass
                    s_keep.Add(zdo);
                }
                else if (zdo.Distant && s_distantSet.Contains(sec)) s_keep.Add(zdo);
            }
        }

        // ---------------- sliced removal (1.6.60) ----------------

        private const float RemoveBudgetMs = 1.5f;
        private static readonly List<ZDO> s_cycle = new List<ZDO>();
        private static int s_cycleAt;
        private static int p_cycles, p_removedSliced, p_orphans; private static double p_sliceMax;
        private static int s_foreign = -1;   // -1 = not looked yet, 0 = no other mod patches RemoveObjects, 1 = one does

        // 1.6.62 (Fire 11-08: "fgn better not rely on core for anything"): FGN's OWN keep hook, FgnApi.RegisterServerRemovalKeep. A mod that
        // keeps objects loaded on the server registers into FGN (FGN never looks any mod up). Replaces 1.6.61's
        // lookup of another mod's keep type. No mod registered = nothing extra is kept (no companions without their mod).
        private sealed class RemovalKeep { internal string Owner; internal Func<int> Refresh; internal Func<ZDO, bool> IsKept; internal int Count; }
        private static readonly List<RemovalKeep> s_keeps = new List<RemovalKeep>();
        private static int p_ckRefreshes; private static double p_ckMs, p_ckMax;

        internal static bool AddRemovalKeep(string owner, Func<int> refresh, Func<ZDO, bool> isKept)
        {
            if (string.IsNullOrEmpty(owner) || refresh == null || isKept == null) { LoggerOptions.LogWarning("[SSS] RegisterServerRemovalKeep refused: owner, refresh and isKept are all required"); return false; }
            s_keeps.RemoveAll(k => k.Owner == owner);
            s_keeps.Add(new RemovalKeep { Owner = owner, Refresh = refresh, IsKept = isKept });
            s_foreign = -1;   // look at RemoveObjects' patches again: this owner's own patch is now covered
            LoggerOptions.LogInfo($"[SSS] server removal keep registered by {owner}: its kept objects stay loaded through the sliced removal ({s_keeps.Count} keep(s))");
            return true;
        }

        internal static bool RemoveRemovalKeep(string owner)
        {
            int n = s_keeps.RemoveAll(k => k.Owner == owner);
            if (n == 0) return false;
            s_foreign = -1;
            LoggerOptions.LogInfo($"[SSS] server removal keep removed by {owner} ({s_keeps.Count} keep(s) left)");
            return true;
        }

        private static string KeepSummary()
        {
            var parts = new List<string>(s_keeps.Count);
            foreach (var k in s_keeps) parts.Add($"{k.Owner} {k.Count} kept");
            return string.Join(", ", parts);
        }

        private static bool KeepsOwner(string harmonyId) { foreach (var k in s_keeps) if (k.Owner == harmonyId) return true; return false; }

        /// <summary>True when another mod patches ZNetScene.RemoveObjects (looked up once): then the whole vanilla call must run.</summary>
        private static bool ForeignRemovePatch()
        {
            if (s_foreign >= 0) return s_foreign == 1;
            s_foreign = 0;
            try
            {
                var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(ZNetScene), "RemoveObjects"));
                var owners = new List<string>();
                // 1.6.62: a mod that registered a server removal keep (FgnApi, Version 7) has its own RemoveObjects patch covered: the
                // sliced walk keeps its objects itself
                if (info != null) foreach (string o in info.Owners)
                    if (!o.StartsWith("com.Fire.FiresGhettoNetworkMod", StringComparison.Ordinal) && !o.Contains("FiresDebugginTools") && !KeepsOwner(o)) owners.Add(o);
                if (owners.Count > 0) s_foreign = 1;
                string kept = s_keeps.Count == 0 ? "no keep registered" : "keeps honoured from " + string.Join(", ", s_keeps.ConvertAll(k => k.Owner));
                LoggerOptions.LogInfo(s_foreign == 1
                    ? $"[SSS] server removal: ZNetScene.RemoveObjects is patched by {string.Join(", ", owners)}, so the whole vanilla call runs every {RemovePeriodSeconds:0} s (their patch keeps running; a mod can register FgnApi.RegisterServerRemovalKeep instead)"
                    : $"[SSS] server removal: sliced, at most {RemoveBudgetMs:0.0} ms a pass ({kept}; no other mod patches ZNetScene.RemoveObjects)");
            }
            catch (Exception e) { s_foreign = 1; LoggerOptions.LogInfo($"[SSS] server removal: could not read RemoveObjects' patches ({e.Message}); the whole vanilla call runs every {RemovePeriodSeconds:0} s"); }
            return s_foreign == 1;
        }

        private static void SlicedRemove(ZNetScene scene)
        {
            if (s_removeDirty) { s_removeDirty = false; s_cycle.Clear(); s_cycleAt = 0; }   // the area changed: start a fresh cycle now
            if (s_cycleAt >= s_cycle.Count)
            {
                s_cycle.Clear(); s_cycle.AddRange(scene.m_instances.Keys); s_cycleAt = 0; p_cycles++;
                ServerStaticPieces.ResetReleases();   // at most 100 pieces released per cycle
                if (s_keeps.Count > 0)
                {
                    // 1.6.62: every registered keep, refreshed once a cycle (~2 s: the cadence a RemoveObjects prefix had under the whole vanilla call)
                    long t0 = Stopwatch.GetTimestamp();
                    for (int i = s_keeps.Count - 1; i >= 0; i--)
                    {
                        var k = s_keeps[i];
                        try { k.Count = k.Refresh(); }
                        catch (Exception e)
                        {
                            // a broken keep can't protect its objects: drop it and fall back to the whole vanilla call (its patch, if any, runs)
                            s_keeps.RemoveAt(i); s_foreign = 1;
                            LoggerOptions.LogWarning($"[SSS] server removal keep of {k.Owner} threw in refresh ({e.Message}); dropped, and the whole vanilla RemoveObjects runs again");
                        }
                    }
                    double ckMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    p_ckRefreshes++; p_ckMs += ckMs; if (ckMs > p_ckMax) p_ckMax = ckMs;
                }
            }
            long start = Stopwatch.GetTimestamp();
            long budget = (long)(RemoveBudgetMs * Stopwatch.Frequency / 1000.0);
            Dictionary<ZDO, ZNetView> inst = scene.m_instances;
            while (s_cycleAt < s_cycle.Count && Stopwatch.GetTimestamp() - start < budget)
            {
                ZDO zdo = s_cycle[s_cycleAt++];
                if (zdo == null || !inst.TryGetValue(zdo, out ZNetView view)) continue;   // already gone
                if (view == null)
                {
                    // the orphan vanilla's RemoveObjects would NRE on: drop the entry (the prune key decides, as in the full pass)
                    if (ServerAuthorityPatches.OrphanPruneEnabled()) { inst.Remove(zdo); p_orphans++; }
                    continue;
                }
                if (Keeps(zdo, scene)) continue;
                // vanilla ZNetScene.RemoveObjects' own steps, in its order
                view.ResetZDO();
                UnityEngine.Object.Destroy(view.gameObject);
                if (!zdo.Persistent && zdo.IsOwner()) ZDOMan.instance.DestroyZDO(zdo);
                inst.Remove(zdo);
                p_removedSliced++;
            }
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (ms > p_sliceMax) p_sliceMax = ms;
        }

        /// <summary>BuildKeepList's rule for one instance: in the near area (minus a building piece outside every simulated area, released at
        /// most 100 a cycle), or a distant object in the distant band.</summary>
        private static bool Keeps(ZDO zdo, ZNetScene scene)
        {
            for (int i = 0; i < s_keeps.Count; i++)   // 1.6.62: kept by a mod that registered through FgnApi.RegisterServerRemovalKeep
            {
                try { if (s_keeps[i].IsKept(zdo)) return true; }
                catch (Exception e) { LoggerOptions.LogWarning($"[SSS] server removal keep of {s_keeps[i].Owner} threw in isKept ({e.Message}); dropped, the whole vanilla RemoveObjects runs again"); s_keeps.RemoveAt(i); s_foreign = 1; return true; }
            }
            Vector2s sec = zdo.GetSector();
            if (s_ringOf.ContainsKey(sec))
            {
                if (OutsideSim(zdo, scene) && ServerStaticPieces.Release()) { p_released++; return false; }
                return true;
            }
            return zdo.Distant && s_distantSet.Contains(sec);
        }

        // ---------------- events ----------------

        private static void OnSectorAdd(ZDO zdo)
        {
            if (!s_active || !s_seeded || zdo == null || ZNetScene.instance == null) return;
            if (TryAddNear(zdo, ZNetScene.instance) || TryAddDistant(zdo, ZNetScene.instance)) p_events++;
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddToSector)), HarmonyPostfix]
        private static void AddToSectorPostfix(ZDO zdo) { if (s_active) OnSectorAdd(zdo); }

        [HarmonyPatch(typeof(ZDOMan), "InitialAddToSector"), HarmonyPostfix]
        private static void InitialAddToSectorPostfix(ZDO zdo) { if (s_active) OnSectorAdd(zdo); }

        // ---------------- proof ----------------

        /// <summary>Debug test (key "Event-Driven Create Queue Self-Check"): the full collect against the queue and the keep-list.</summary>
        private static void SelfCheck(ZNetScene scene)
        {
            ServerAuthorityPatches.CollectZdosFromAllPeerActiveAreas(s_sd);
            int missing = 0, uncreated = 0, keepMismatch = 0;
            var nearIds = new HashSet<ZDOID>(); var distantIds = new HashSet<ZDOID>();   // test tool only (Debug key, every 10 s)
            foreach (ZDO zdo in ServerAuthorityPatches._cdoDistantScratch) if (zdo != null) distantIds.Add(zdo.m_uid);
            foreach (ZDO zdo in ServerAuthorityPatches._cdoNearScratch)
            {
                if (zdo == null || !zdo.IsValid()) continue;
                nearIds.Add(zdo.m_uid);
                if (!NearFilter(zdo, scene)) continue;
                uncreated++;
                if (!s_queued.ContainsKey(zdo.m_uid)) missing++;
            }
            foreach (KeyValuePair<ZDO, ZNetView> kv in scene.m_instances)
            {
                ZDO zdo = kv.Key; if (zdo == null) continue;
                bool fullKeeps = nearIds.Contains(zdo.m_uid) || (zdo.Distant && distantIds.Contains(zdo.m_uid));
                bool oursKeeps = s_ringOf.ContainsKey(zdo.GetSector()) || (zdo.Distant && s_distantSet.Contains(zdo.GetSector()));
                if (fullKeeps != oursKeeps) keepMismatch++;
            }
            p_check = $"self-check: {missing} of {uncreated} uncreated area object(s) not queued, {keepMismatch} instance(s) where the area test and the full collect disagree (both must stay near 0)";
        }

        private static void ClearAll()
        {
            for (int t = 0; t < Types; t++) for (int r = 0; r < Rings; r++) s_buckets[t, r].Clear();
            s_distant.Clear(); s_queued.Clear(); s_simQueued.Clear(); s_waitLoad.Clear(); s_waitSim.Clear(); s_ringOf.Clear(); s_distantSet.Clear(); s_sweep = 0;
        }

        private static void Report(ZNetScene scene, float now)
        {
            if (!LoggerOptions.DebugEnabled || now < p_next) return;
            if (p_next > 0f && p_passes > 0)
            {
                int waitSim = 0; foreach (List<ZDO> l in s_waitSim.Values) waitSim += l.Count;
                int waitLoad = 0; foreach (List<ZDO> l in s_waitLoad.Values) waitLoad += l.Count;
                LoggerOptions.LogDebug($"[SSS] server queue, last {ReportSeconds:0} s over {p_passes} passes (average / worst ms per pass): whole pass {p_totalMs / p_passes:0.00} / {p_totalMax:0.0}, create {p_createMs / p_passes:0.00} / {p_createMax:0.0} ({p_created} objects), " +
                    $"keep-list {p_keepMs / p_passes:0.00}, remove {p_removeMs / p_passes:0.00}; reseeds {p_reseeds} ({(p_reseeds > 0 ? p_seedMs / p_reseeds : 0):0.0} ms each); queue {s_queued.Count} (building pieces waiting for a simulated area {waitSim}, waiting for their zone {waitLoad}); " +
                    $"added by events {p_events}, found by the sweep {p_sweepFound}, parked (no simulated area) {p_parkedSim}, parked (zone loading) {p_parkedLoad}, pieces released {p_released}, stale {p_stale}; area {s_ringOf.Count} near / {s_distantSet.Count} distant sector(s), {s_sources.Count} source(s); server instances {scene.m_instances.Count}; {p_check}");
            }
            if (LoggerOptions.DebugEnabled) LoggerOptions.LogDebug($"[SSS] server queue upkeep, last {ReportSeconds:0} s: removal {(s_foreign == 1 ? $"whole vanilla passes {p_removePasses} (every {RemovePeriodSeconds:0} s; another mod patches RemoveObjects)" : $"sliced: {p_cycles} full cycle(s) over the live instances, worst slice {p_sliceMax:0.00} ms (budget {RemoveBudgetMs:0.0} ms), removed {p_removedSliced}, orphan entries dropped {p_orphans}{(s_keeps.Count > 0 ? $", removal keeps {KeepSummary()}, refreshed {p_ckRefreshes}x, {(p_ckRefreshes > 0 ? p_ckMs / p_ckRefreshes : 0):0.00} ms avg / {p_ckMax:0.00} worst" : "")}")}; simulated-area builds {p_simAreaBuilds} (every {SimAreaPeriodSeconds:0.0} s)");
            p_removePasses = p_simAreaBuilds = p_cycles = p_removedSliced = p_orphans = p_ckRefreshes = 0; p_sliceMax = p_ckMs = p_ckMax = 0;
            ServerStaticPieces.Report(scene.m_instances.Count);
            p_next = now + ReportSeconds;
            p_passes = p_reseeds = p_created = p_events = p_sweepFound = p_released = p_parkedSim = p_parkedLoad = p_stale = 0;
            p_seedMs = p_createMs = p_createMax = p_keepMs = p_removeMs = p_totalMs = p_totalMax = 0;
        }
    }
}
