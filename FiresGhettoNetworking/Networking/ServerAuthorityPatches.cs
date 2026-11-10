using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [HarmonyPatch]
    public static class ServerAuthorityPatches
    {
        private const float ZoneSizeMeters = 64f;

        private const float KillPlaneY = -5000f;
        private const float RescueRaycastStartHeight = 6000f;
        private const float RescueRaycastMaxDistance = 12000f;
        private const float RescueGroundClearance = 1f;

        private const float VelocityEmaWeight = 0.5f;
        private const float VelocitySampleMinDeltaSec = 0.05f;
        private const float VelocitySampleStaleSec = 5f;
        private const float VelocityRefreshMinMoveSqr = 0.01f;
        private const float DefaultPredictionMinSpeedMps = 2f;
        private const int DefaultPredictionMaxLookaheadZones = 9;

        private struct PeerMotionSample
        {
            public Vector3 Pos;
            public float Time;
            public Vector3 Velocity;
        }

        private static readonly Dictionary<long, PeerMotionSample> _peerMotion
            = new Dictionary<long, PeerMotionSample>();

        internal static Vector3 GetPredictedRefPos(ZNetPeer peer)
        {
            Vector3 currentPos = peer.GetRefPos();

            if (!(FiresGhettoNetworkMod.ConfigEnablePredictiveZoneStreaming?.Value ?? false)) return currentPos;
            float lookahead = FiresGhettoNetworkMod.ConfigPredictionLookaheadSec?.Value ?? 0f;
            if (lookahead <= 0f) return currentPos;

            long uid = peer.m_uid;
            float now = Time.time;

            if (!_peerMotion.TryGetValue(uid, out var prev))
            {
                _peerMotion[uid] = new PeerMotionSample { Pos = currentPos, Time = now, Velocity = Vector3.zero };
                return currentPos;
            }

            float dt = now - prev.Time;
            if (dt > VelocitySampleStaleSec)
            {
                _peerMotion[uid] = new PeerMotionSample { Pos = currentPos, Time = now, Velocity = Vector3.zero };
                return currentPos;
            }

            Vector3 velocity = RefreshVelocityIfMovedEnough(uid, prev, currentPos, dt, now);

            float minSpeed = FiresGhettoNetworkMod.ConfigPredictionMinVelocity?.Value ?? DefaultPredictionMinSpeedMps;
            if (velocity.sqrMagnitude < minSpeed * minSpeed) return currentPos;

            int maxZones = FiresGhettoNetworkMod.ConfigPredictionMaxLookaheadZones?.Value ?? DefaultPredictionMaxLookaheadZones;
            return currentPos + ClampForwardOffsetToMaxZones(velocity * lookahead, maxZones);
        }

        private const float TeleportSpeedSqr = 60f * 60f;   // faster than any mount or ship

        private static Vector3 RefreshVelocityIfMovedEnough(long uid, PeerMotionSample prev, Vector3 currentPos, float dt, float now)
        {
            Vector3 delta = currentPos - prev.Pos;
            if (delta.sqrMagnitude <= VelocityRefreshMinMoveSqr || dt <= VelocitySampleMinDeltaSec)
                return prev.Velocity;

            Vector3 instantVel = delta / dt;
            // 1.6.31: a teleport is not motion: it would aim the prediction up to its cap away from the player who just arrived.
            if (instantVel.sqrMagnitude > TeleportSpeedSqr)
            {
                _peerMotion[uid] = new PeerMotionSample { Pos = currentPos, Time = now, Velocity = Vector3.zero };
                return Vector3.zero;
            }
            Vector3 smoothed = Vector3.Lerp(prev.Velocity, instantVel, VelocityEmaWeight);
            _peerMotion[uid] = new PeerMotionSample { Pos = currentPos, Time = now, Velocity = smoothed };
            return smoothed;
        }

        private static Vector3 ClampForwardOffsetToMaxZones(Vector3 offset, int maxZones)
        {
            float maxForward = maxZones * ZoneSizeMeters;
            return offset.sqrMagnitude > maxForward * maxForward ? offset.normalized * maxForward : offset;
        }

        public static void RemovePeer_ClearMotion_Postfix(ZNetPeer netPeer)
        {
            if (netPeer == null) return;
            _peerMotion.Remove(netPeer.m_uid);
        }

        // 1.6.70 N1: the teleport answer for IsActiveAreaLoaded moved to TeleportCreateGate (create passes only, each object gated on its own zone).

        private const int ZdoScratchCapacity = 8192;
        internal static readonly List<ZDO> _cdoNearScratch = new List<ZDO>(ZdoScratchCapacity);
        internal static readonly List<ZDO> _cdoDistantScratch = new List<ZDO>(ZdoScratchCapacity);
        private static int _cdoSources;   // areas collected this frame (ready peers + SSS test areas)
        private static float _cdoNextPass;   // realtime of the next server object pass ('Server Object Pass Hz')
        internal static readonly List<ZDO> _cdoNearFiltered = new List<ZDO>(ZdoScratchCapacity);
        internal static readonly List<ZDO> _cdoDistantFiltered = new List<ZDO>(ZdoScratchCapacity);

        [HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
        [HarmonyPrefix]
        public static bool CreateDestroyObjects_Prefix(ZNetScene __instance)
        {
            // 1.5.75: kept companion areas count as sources too, so a world with no player online still builds them
            if (!ZNet.instance || !ZNet.instance.IsDedicated() || (ZNet.instance.GetConnectedPeers().Count == 0 && CompanionKeepAlive.Points.Count == 0))
            {
                ServerStatusDiagnostics.s_createDestroy_bail_noPeers++;
                return true;
            }

            // Filtering and removal cost several ms a frame in a dense city, so the pass
            // runs at 'Server Object Pass Hz' (default 10) instead of every frame. Each pass that does run collects FRESH lists, so
            // RemoveObjects never sees a stale one (a stale list would destroy objects the server just spawned); in between, nothing
            // is created or removed and vanilla's pass is skipped too.
            int passHz = FiresGhettoNetworkMod.ConfigServerPassHz?.Value ?? 0;
            if (passHz > 0)
            {
                float now = Time.realtimeSinceStartup;
                if (now < _cdoNextPass) return false;
                _cdoNextPass = now + 1f / passHz;
            }

            SssTestAreas.RemindIfActive();
            // 1.6.57: the event-driven server pass (ServerStream); false = it threw this session, the full pass below runs
            if (ServerStream.Enabled && ServerStream.Pass(__instance)) return false;
            try
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                CollectZdosFromAllPeerActiveAreas(SimDistance.Widened(ExtendedZoneRadius()));
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool quiet = SssQuietPass.WasQuiet(_cdoZones);   // read before this pass changes anything
                ServerStaticPieces.BeginPass(_cdoNearScratch, CompanionKeepAlive.Points, SssTestAreas.Points);   // 1.6.56: the simulated areas for this pass
                long tQuiet = System.Diagnostics.Stopwatch.GetTimestamp();
                FilterAndDedupeZdos(_cdoNearScratch, _cdoNearFiltered, requireLoadedZone: true);
                long tNear = System.Diagnostics.Stopwatch.GetTimestamp();
                FilterAndDedupeZdos(_cdoDistantScratch, _cdoDistantFiltered, requireLoadedZone: false);
                long tDistant = System.Diagnostics.Stopwatch.GetTimestamp();
                RecordCreateDestroyObjectsDiagnostics();
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                // The "filter" figure split into its parts (Debug line only; timing, no behaviour change)
                ServerFrameSplit.FilterParts(tQuiet - t1, tNear - tQuiet, tDistant - tNear, t2 - tDistant);

                int instancesBefore = __instance.m_instances.Count;
                __instance.CreateObjects(_cdoNearFiltered, _cdoDistantFiltered);
                int instancesCreated = __instance.m_instances.Count;
                long t3 = System.Diagnostics.Stopwatch.GetTimestamp();

                // REACTIVE orphan prune. A null-view/ZDO NRE in RemoveObjects only
                // appears when another mod mismanages ZNetScene state (e.g. a permission
                // mod blocking WearNTear.RPC_Remove on the dedi), so rather than scan
                // m_instances every frame, let RemoveObjects run and only scan-and-prune
                // when it actually hits one, then retry. On a healthy server (the normal
                // case) this is zero per-frame prune cost; the full scan happens only the
                // moment an orphan really exists.
                try
                {
                    __instance.RemoveObjects(_cdoNearFiltered, _cdoDistantFiltered);
                }
                catch (System.NullReferenceException) when (OrphanPruneEnabled())
                {
                    PruneOrphanInstances(__instance);
                    __instance.RemoveObjects(_cdoNearFiltered, _cdoDistantFiltered);
                }
                SssQuietPass.PassDone(quiet, instancesCreated - instancesBefore, instancesCreated - __instance.m_instances.Count);
                ServerFrameSplit.Frame(t1 - t0, t2 - t1, t3 - t2, System.Diagnostics.Stopwatch.GetTimestamp() - t3,
                    _cdoNearFiltered.Count, _cdoDistantFiltered.Count);
                ServerStaticPieces.Report(__instance.m_instances.Count);
                return false;
            }
            catch (System.NullReferenceException ex)
            {
                ServerStatusDiagnostics.s_createDestroy_bail_nre++;
                LoggerOptions.LogWarning($"CreateDestroyObjects encountered NRE (likely mod conflict), falling back to vanilla: {ex.Message}");
                return true;
            }
        }

        // Vanilla FindSectorObjects clears its visited-sector set on every call, so two players' overlapping areas would return
        // every shared sector twice and need deduping object by object. Instead every source's NEAR rings are walked first and then every
        // source's DISTANT rings, all through one shared visited set (vanilla's own FindObjects / FindDistantObjects, which add
        // each sector they read). Each sector is read once: near wins over distant, exactly the union of the per-player calls.
        // One source still uses vanilla's FindSectorObjects unchanged.
        internal static readonly List<Vector2s> _cdoZones = new List<Vector2s>(8);
        private static readonly HashSet<ZoneSystem.SectorIndex> _cdoVisited = new HashSet<ZoneSystem.SectorIndex>();

        internal static void CollectZdosFromAllPeerActiveAreas(SimulationDistance simulationDistance)
        {
            _cdoNearScratch.Clear();
            _cdoDistantScratch.Clear();
            _cdoZones.Clear();
            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
            {
                if (!peer.IsReady()) continue;
                _cdoZones.Add(FgnRing.Sector(GetPredictedRefPos(peer)));
            }
            foreach (Vector3 point in SssTestAreas.Points) _cdoZones.Add(FgnRing.Sector(point));
            foreach (Vector3 point in CompanionKeepAlive.Points) _cdoZones.Add(FgnRing.Sector(point));
            _cdoSources = _cdoZones.Count;
            ZDOMan zdoMan = ZDOMan.instance;
            if (_cdoSources == 1) { zdoMan.FindSectorObjects(_cdoZones[0], simulationDistance, _cdoNearScratch, _cdoDistantScratch); return; }
            _cdoVisited.Clear();
            ZoneSystem zones = ZoneSystem.instance;
            for (int i = 0; i < _cdoZones.Count; i++) NearRings(zdoMan, zones, _cdoZones[i], simulationDistance);
            for (int i = 0; i < _cdoZones.Count; i++) DistantRings(zdoMan, zones, _cdoZones[i], simulationDistance);
        }

        // vanilla ZDOMan.FindSectorObjects' near half (ZDOMan.cs:952-973), into _cdoNearScratch, through the shared visited set
        private static void NearRings(ZDOMan z, ZoneSystem zones, Vector2s sector, SimulationDistance d)
        {
            z.FindObjects(sector, _cdoNearScratch, _cdoVisited);
            int near = d.NearSimulationDistance;
            for (int index = 1; index <= near; ++index)
            {
                for (int x = sector.x - index; x <= sector.x + index; ++x)
                {
                    var a = new Vector2s(x, sector.y - index);
                    var b = new Vector2s(x, sector.y + index);
                    if (d.IsClassic || zones.ZonesWithinRadius(sector, a, near)) z.FindObjects(a, _cdoNearScratch, _cdoVisited);
                    if (d.IsClassic || zones.ZonesWithinRadius(sector, b, near)) z.FindObjects(b, _cdoNearScratch, _cdoVisited);
                }
                for (int y = sector.y - index + 1; y <= sector.y + index - 1; ++y)
                {
                    var a = new Vector2s(sector.x - index, y);
                    var b = new Vector2s(sector.x + index, y);
                    if (d.IsClassic || zones.ZonesWithinRadius(sector, a, near)) z.FindObjects(a, _cdoNearScratch, _cdoVisited);
                    if (d.IsClassic || zones.ZonesWithinRadius(sector, b, near)) z.FindObjects(b, _cdoNearScratch, _cdoVisited);
                }
            }
        }

        // vanilla ZDOMan.FindSectorObjects' distant half (ZDOMan.cs:974-997), into _cdoDistantScratch, through the shared visited
        // set; vanilla's own quirk kept: the second test of the side columns checks the first column's sector
        private static void DistantRings(ZDOMan z, ZoneSystem zones, Vector2s sector, SimulationDistance d)
        {
            int total = d.TotalSimulationDistance;
            int first = 1 + (d.IsClassic ? d.NearSimulationDistance : 0);
            for (int index = first; index <= total; ++index)
            {
                for (int x = sector.x - index; x <= sector.x + index; ++x)
                {
                    var a = new Vector2s(x, sector.y - index);
                    var b = new Vector2s(x, sector.y + index);
                    if (d.IsClassic || zones.ZonesWithinRadius(sector, a, total, true)) z.FindDistantObjects(a, _cdoDistantScratch, _cdoVisited);
                    if (d.IsClassic || zones.ZonesWithinRadius(sector, a, total, true)) z.FindDistantObjects(b, _cdoDistantScratch, _cdoVisited);
                }
                for (int y = sector.y - index + 1; y <= sector.y + index - 1; ++y)
                {
                    var a = new Vector2s(sector.x - index, y);
                    var b = new Vector2s(sector.x + index, y);
                    if (d.IsClassic || zones.ZonesWithinRadius(sector, a, total, true)) z.FindDistantObjects(a, _cdoDistantScratch, _cdoVisited);
                    if (d.IsClassic || zones.ZonesWithinRadius(sector, a, total, true)) z.FindDistantObjects(b, _cdoDistantScratch, _cdoVisited);
                }
            }
        }

        /// <summary>
        /// A near object whose zone is not loaded on this server would spawn with no heightmap, water or terrain collider
        /// under it, so it waits until LoadNextZoneAround has loaded that zone. Vanilla's own active-area gate only covers
        /// the near band, not FGN's extended ring.
        /// </summary>
        private static int s_keptInUnloadedZone;
        private static float s_nextKeptLog;
        private static void NoteKeptInUnloadedZone(ZDO zdo)
        {
            s_keptInUnloadedZone++;
            float now = Time.unscaledTime;
            if (now < s_nextKeptLog) return;
            s_nextKeptLog = now + 30f;
            LoggerOptions.LogDebug($"[SSS] {s_keptInUnloadedZone} live instance(s) kept while their zone loads (latest at {zdo.GetPosition():F0}): not destroyed for crossing into it");
            s_keptInUnloadedZone = 0;
        }

        internal static void FilterAndDedupeZdos(List<ZDO> source, List<ZDO> dest, bool requireLoadedZone)
        {
            dest.Clear();
            Vector2s lastSector = default;
            bool haveLast = false, lastLoaded = false;
            ZNetScene scene = ZNetScene.instance;
            var loadedZones = requireLoadedZone ? ZoneSystem.instance.m_zones : null;
            for (int i = 0; i < source.Count; i++)
            {
                var zdo = source[i];
                if (zdo == null || !zdo.IsValid() || zdo.m_uid.IsNone()) continue;
                // Skip prefabs this server doesn't have registered (e.g. a Marketplace mod's hammer that's
                // registered client-side only). If it reaches CreateObjects, vanilla can't build it and the
                // SERVER branch destroys the ZDO ("Destroyed invalid prefab ZDO"), wiping the client's object.
                // Leaving it out of the server's create sweep keeps it client-owned and intact.
                // Prefab 0 is skipped the same way: nothing can be built from it, so letting it through only reached
                // vanilla's destroy, and prefab-less ZDOs are how mods keep pure data objects.
                int prefab = zdo.m_prefab;
                if (prefab == 0 || (scene != null && !scene.HasPrefab(prefab))) continue;
                if (loadedZones != null)
                {
                    // Objects arrive grouped by sector, so the zone lookup is made once per sector run, not per object
                    Vector2s sector = zdo.GetSector();
                    if (!haveLast || sector.x != lastSector.x || sector.y != lastSector.y) { lastSector = sector; lastLoaded = loadedZones.ContainsKey(sector); haveLast = true; }
                    // 1.6.31: an instance that already exists stays in the list: left out, RemoveObjects destroyed it (and its ZDO if not persistent)
                    // when it crossed into a zone still loading. Vanilla never removes one for that. Only creating a new one waits for the zone.
                    if (!lastLoaded)
                    {
                        if (scene == null || !scene.HaveInstance(zdo)) continue;
                        NoteKeptInUnloadedZone(zdo);
                    }
                }
                // 1.6.56: a building piece outside every simulated area stays data only (ServerStaticPieces)
                if (requireLoadedZone && ServerStaticPieces.LeaveOut(zdo, scene)) continue;
                // No duplicates to drop: CollectZdosFromAllPeerActiveAreas reads each sector once across all sources.
                dest.Add(zdo);
            }
        }

        // Always the config value, Auto-Tune on or off: every tier says 0, and a server that raised it keeps it.
        internal static int ExtendedZoneRadius() => RenderLimitsCompat.DeferRadius(FiresGhettoNetworkMod.ConfigExtendedZoneRadius.Value);

        private static void RecordCreateDestroyObjectsDiagnostics()
        {
            ServerStatusDiagnostics.s_createDestroy_passes++;
            ServerStatusDiagnostics.s_createDestroy_nearTotal += _cdoNearScratch.Count;
            ServerStatusDiagnostics.s_createDestroy_distantTotal += _cdoDistantScratch.Count;
            ServerStatusDiagnostics.s_createDestroy_distinctNearTotal += _cdoNearFiltered.Count;
            ServerStatusDiagnostics.s_createDestroy_distinctDistantTotal += _cdoDistantFiltered.Count;
            if (ZoneSystem.instance != null && ZoneSystem.instance.IsActiveAreaLoaded())
                ServerStatusDiagnostics.s_createDestroy_areaReadyTrue++;
            else
                ServerStatusDiagnostics.s_createDestroy_areaReadyFalse++;
        }

        // Remove dictionary entries whose ZNetView is Unity-destroyed or whose
        // view.GetZDO() returns null — exactly the entries that would NRE vanilla
        // RemoveObjects. Only the dict entry is removed; the GameObject is left
        // for Unity GC / mod pool ownership.
        //
        // Triggered, for example, when TargetPortalProtection blocks
        // WearNTear.RPC_Remove on a dedi (its permission check reads
        // Player.m_localPlayer which is null server-side) while ZDOMan.DestroyZDO
        // still reaps the ZDO via the client-initiated path. The result is a
        // view with a null ZDO sitting in m_instances; the next RemoveObjects
        // walk would NRE on view.GetZDO().TempRemoveEarmark.
        private static readonly List<ZDO> _orphanScratch = new List<ZDO>(64);
        private const float OrphanLogIntervalSec = 5f;
        private static float _orphanNextLogTime;

        internal static bool OrphanPruneEnabled()
            => FiresGhettoNetworkMod.ConfigEnableInstanceOrphanPrune?.Value ?? true;

        internal static void PruneOrphanInstances(ZNetScene scene)
        {
            if (!OrphanPruneEnabled()) return;
            if (scene == null || scene.m_instances == null) return;

            _orphanScratch.Clear();
            int nullViews = 0;
            int nullZdos = 0;
            foreach (var kvp in scene.m_instances)
            {
                var view = kvp.Value;
                if (view == null)
                {
                    _orphanScratch.Add(kvp.Key);
                    nullViews++;
                    continue;
                }
                if (view.GetZDO() == null)
                {
                    _orphanScratch.Add(kvp.Key);
                    nullZdos++;
                }
            }
            if (_orphanScratch.Count == 0) return;

            for (int i = 0; i < _orphanScratch.Count; i++)
                scene.m_instances.Remove(_orphanScratch[i]);
            ServerStatusDiagnostics.s_createDestroy_orphansPruned += _orphanScratch.Count;
            _orphanScratch.Clear();

            float now = Time.realtimeSinceStartup;
            if (now >= _orphanNextLogTime)
            {
                LoggerOptions.LogWarning(
                    $"[m_instances orphan prune] removed {nullViews + nullZdos} broken entry/ies "
                    + $"(nullView={nullViews}, nullZdo={nullZdos}). If this fires repeatedly, "
                    + "another mod is mismanaging ZNetScene state — most often a permission mod "
                    + "blocking WearNTear.RPC_Remove on the dedi while ZDOMan still reaps the ZDO.");
                _orphanNextLogTime = now + OrphanLogIntervalSec;
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), "IsActiveAreaLoaded")]
        [HarmonyPrefix]
        public static bool IsActiveAreaLoaded_Prefix(ref bool __result, ZoneSystem __instance)
        {
            if (!ZNet.instance || !ZNet.instance.IsDedicated() || ZNet.instance.GetPeers().Count == 0)
                return true;
            if (__instance.m_zones == null) return true;

            int missing = CountMissingZonesAcrossAllPeers(__instance, out int firstMissX, out int firstMissY);
            RecordIsActiveAreaLoadedDiagnostics(missing, firstMissX, firstMissY);
            __result = (missing == 0);
            return false;
        }

        private static int CountMissingZonesAcrossAllPeers(ZoneSystem zs, out int firstMissX, out int firstMissY)
        {
            int activeArea = SimDistance.Near();
            var zones = zs.m_zones;
            int missing = 0;
            firstMissX = 0;
            firstMissY = 0;
            bool capturedFirstMiss = false;

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (!peer.IsReady()) continue;
                Vector3 refPos = GetPredictedRefPos(peer);
                Vector2s centre = ZoneSystem.GetZone(refPos);
                for (int y = centre.y - activeArea; y <= centre.y + activeArea; y++)
                    for (int x = centre.x - activeArea; x <= centre.x + activeArea; x++)
                    {
                        var zone = new Vector2s(x, y);
                        // Vanilla's own IsActiveAreaLoaded sweeps the square then discards the
                        // corners via ZonesWithinRadius, so counting them here would report
                        // zones as "missing" that the game never asked to be loaded.
                        if (!SimDistance.ZoneInRadius(centre, zone, activeArea)) continue;
                        if (!zones.ContainsKey(zone))
                        {
                            missing++;
                            if (!capturedFirstMiss) { firstMissX = x; firstMissY = y; capturedFirstMiss = true; }
                        }
                    }
            }
            return missing;
        }

        private static void RecordIsActiveAreaLoadedDiagnostics(int missing, int firstMissX, int firstMissY)
        {
            ServerStatusDiagnostics.s_activeAreaLoaded_calls++;
            if (missing == 0)
            {
                ServerStatusDiagnostics.s_activeAreaLoaded_resultTrue++;
                return;
            }
            ServerStatusDiagnostics.s_activeAreaLoaded_resultFalse++;
            if (missing < ServerStatusDiagnostics.s_activeAreaLoaded_minMissingZones) ServerStatusDiagnostics.s_activeAreaLoaded_minMissingZones = missing;
            if (missing > ServerStatusDiagnostics.s_activeAreaLoaded_maxMissingZones) ServerStatusDiagnostics.s_activeAreaLoaded_maxMissingZones = missing;
            ServerStatusDiagnostics.s_activeAreaLoaded_lastMissingZoneX = firstMissX;
            ServerStatusDiagnostics.s_activeAreaLoaded_lastMissingZoneY = firstMissY;
        }

        public static void ZoneSystem_Update_Postfix(ZoneSystem __instance)
        {
            if (!ZNet.instance || !ZNet.instance.IsDedicated()) return;
            CompanionKeepAlive.Update();   // 1.5.75: guards, kept areas, companion ownership (1 Hz inside)
            if (ZNet.instance.GetPeers().Count == 0 && CompanionKeepAlive.Points.Count == 0) return;
            int radius = SimDistance.Near() + ExtendedZoneRadius();
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                if (peer.IsReady())
                    LoadNextZoneAround(__instance, GetPredictedRefPos(peer), radius);
            foreach (Vector3 point in SssTestAreas.Points)
                LoadNextZoneAround(__instance, point, radius);
            foreach (Vector3 point in CompanionKeepAlive.Points)
                LoadNextZoneAround(__instance, point, radius);
        }

        /// <summary>
        /// Vanilla CreateLocalZones out to FGN's widened radius: keeps every zone in range alive and spawns at most one
        /// missing zone per call, so the extended ring the server creates objects in actually has terrain.
        /// </summary>
        private static void LoadNextZoneAround(ZoneSystem zoneSystem, Vector3 refPoint, int radius)
        {
            Vector2s centre = ZoneSystem.GetZone(refPoint);
            if (zoneSystem.PokeLocalZone(centre)) return;
            for (int y = centre.y - radius; y <= centre.y + radius; y++)
                for (int x = centre.x - radius; x <= centre.x + radius; x++)
                {
                    var zone = new Vector2s(x, y);
                    if (zone == centre || !SimDistance.ZoneInRadius(centre, zone, radius)) continue;
                    if (zoneSystem.PokeLocalZone(zone)) return;
                }
        }

        [HarmonyPatch(typeof(ZNetScene), "OutsideActiveArea", new[] { typeof(Vector3) })]
        [HarmonyPrefix]
        public static bool OutsideActiveArea_Prefix(ref bool __result, Vector3 point)
        {
            if (!ZNet.instance || !ZNet.instance.IsDedicated() || (ZNet.instance.GetPeers().Count == 0 && CompanionKeepAlive.Points.Count == 0))
                return true;

            int activeArea = SimDistance.Near() + ExtendedZoneRadius();

            __result = !IsPointInsideAnyPeerActiveArea(point, activeArea);
            return false;
        }

        // ZNetScene.InActiveArea / OutsideActiveArea no longer accept a radius — they read the
        // synced SimulationDistance internally — so an extended radius has to be evaluated
        // against the same radial rule directly.
        private static bool IsPointInsideAnyPeerActiveArea(Vector3 point, int activeArea)
        {
            Vector2s pointZone = FgnRing.Sector(point);
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                if (peer.IsReady() && SimDistance.ZoneInRadius(FgnRing.Sector(GetPredictedRefPos(peer)), pointZone, activeArea))
                    return true;
            foreach (Vector3 testPoint in SssTestAreas.Points)
                if (SimDistance.ZoneInRadius(FgnRing.Sector(testPoint), pointZone, activeArea))
                    return true;
            if (CompanionKeepAlive.Covers(pointZone, activeArea)) return true;
            return false;
        }

        /// <summary>Weapon swing trails are visuals; on server-built copies of players they index trail points a headless server never records.</summary>
        [HarmonyPatch(typeof(MeleeWeaponTrail), nameof(MeleeWeaponTrail.CustomFixedUpdate))]
        [HarmonyPrefix]
        public static bool MeleeWeaponTrail_CustomFixedUpdate_Prefix() => !ServerClientUtils.ZNetIsDedicated();

        // Tameable.Awake and Update must run on a dedicated server, or a tame the server owns has none of its RPCs ("Command",
        // "SetName", "RPC_UnSummon", "AddSaddle", "SetSaddle"), no feeding hook and no TamingUpdate. Only SetText stays off (it reads the platform's local user, which a headless server lacks).
        [HarmonyPatch(typeof(Tameable), "SetText")]
        [HarmonyPrefix]
        public static bool Tameable_SetText_Prefix() => !ServerClientUtils.ZNetIsDedicated();

        /// <summary>Tameable.Tame (and other server-run code) counts a player stat; a dedicated server has no player profile.</summary>
        [HarmonyPatch(typeof(Game), nameof(Game.IncrementPlayerStat))]
        [HarmonyPrefix]
        public static bool Game_IncrementPlayerStat_NoProfilePrefix(PlayerProfile ___m_playerProfile) => ___m_playerProfile != null;

        [HarmonyPatch(typeof(AudioMan), "Update")]
        [HarmonyPrefix]
        public static bool AudioMan_Update_Prefix() => !ServerClientUtils.ZNetIsDedicated();

        /// <summary>
        /// The terrain render mesh is only ever drawn. Heights, collision and the paint mask are rebuilt without it,
        /// so terrain edits synced from other peers still reach the server's physics, navmesh and crop checks.
        /// </summary>
        [HarmonyPatch(typeof(Heightmap), "RebuildRenderMesh")]
        [HarmonyPrefix]
        public static bool Heightmap_RebuildRenderMesh_Prefix() => !ServerClientUtils.ZNetIsDedicated();

        // Skipping this Awake entirely on a dedi would also skip the one line that sets the static
        // gradient GetDomeColor reads, and a server-simulated shield generator calls it on every update. The dedi still skips the
        // material and texture, but takes the prefab's own gradient and smoothing as vanilla Awake does.
        [HarmonyPatch(typeof(ShieldDomeImageEffect), "Awake")]
        [HarmonyPrefix]
        public static bool ShieldDomeImageEffect_Awake_Prefix(ShieldDomeImageEffect __instance)
        {
            if (!ServerClientUtils.ZNetIsDedicated()) return true;
            if (__instance.m_shieldColorGradient != null) ShieldDomeImageEffect.s_staticGradient = __instance.m_shieldColorGradient;
            ShieldDomeImageEffect.Smoothing = __instance.m_smoothing;
            return false;
        }

        /// <summary>When no ShieldDomeImageEffect has woken on this dedi (the camera's is inactive or absent): the gradient from any
        /// loaded one, else a plain white one, so GetDomeColor never reads null.</summary>
        [HarmonyPatch(typeof(ShieldGenerator), "UpdateShield")]
        [HarmonyPrefix]
        public static void ShieldGenerator_UpdateShield_GradientPrefix()
        {
            if (ShieldDomeImageEffect.s_staticGradient != null || !ServerClientUtils.ZNetIsDedicated()) return;
            var effect = UnityEngine.Object.FindFirstObjectByType<ShieldDomeImageEffect>(FindObjectsInactive.Include);
            ShieldDomeImageEffect.s_staticGradient = effect != null && effect.m_shieldColorGradient != null ? effect.m_shieldColorGradient : new Gradient();
        }

        /// <summary>A plant the dedi does not own only refreshes visuals and hover status a headless server never shows; plants it owns run vanilla so they still grow.</summary>
        [HarmonyPatch(typeof(Plant), "SUpdate")]
        [HarmonyPrefix]
        public static bool Plant_SUpdate_Prefix(ZNetView ___m_nview) =>
            !ServerClientUtils.ZNetIsDedicated() || (___m_nview != null && ___m_nview.IsValid() && ___m_nview.IsOwner());

        /// <summary>Carts are instantiated for collision but never simulated: a live body runs before the zone's colliders exist.</summary>
        [HarmonyPatch(typeof(Vagon), "Awake")]
        [HarmonyPostfix]
        public static void Vagon_Awake_DediKinematic_Postfix(Vagon __instance)
        {
            if (!ServerClientUtils.ZNetIsDedicated()) return;
            HoldStillOnDedi(__instance);
        }

        /// <summary>A grave is a live Rigidbody launched upward on spawn; an unfrozen dedi sinks it through the floor and persists that.</summary>
        public static void TombStone_Awake_DediKinematic_Postfix(TombStone __instance)
        {
            if (!ServerClientUtils.ZNetIsDedicated()) return;
            HoldStillOnDedi(__instance);
        }

        /// <summary>
        /// Owner-side grave upkeep the dedi must not run: it snaps graves to heightmap height, and it deletes any
        /// grave whose Container reads empty, which a freshly inherited ZDO does until its items arrive.
        /// </summary>
        [HarmonyPatch(typeof(TombStone), "UpdateDespawn")]
        [HarmonyPrefix]
        public static bool TombStone_UpdateDespawn_Prefix() => !ServerClientUtils.ZNetIsDedicated();

        /// <summary>Freezes a body the dedi instantiates but must not simulate, and stops vanilla writing velocities into it.</summary>
        private static void HoldStillOnDedi(Component root)
        {
            if (root == null) return;

            foreach (var body in root.GetComponentsInChildren<Rigidbody>())
            {
                if (body == null) continue;
                if (body.collisionDetectionMode == CollisionDetectionMode.Continuous
                    || body.collisionDetectionMode == CollisionDetectionMode.ContinuousDynamic)
                    body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                body.isKinematic = true;
            }

            foreach (var syncTransform in root.GetComponentsInChildren<ZSyncTransform>())
            {
                if (syncTransform == null) continue;
                syncTransform.m_syncBodyVelocity = false;
                MarkBodyKinematicForSync(syncTransform);
            }
        }

        /// <summary>Buoyancy writes velocity, which a frozen body cannot take; live server-owned bodies still float.</summary>
        [HarmonyPatch(typeof(Floating), nameof(Floating.CustomFixedUpdate))]
        [HarmonyPrefix]
        public static bool Floating_CustomFixedUpdate_DediKinematic_Prefix(Rigidbody ___m_body)
        {
            if (!ServerClientUtils.ZNetIsDedicated()) return true;
            return ___m_body == null || !___m_body.isKinematic;
        }

        /// <summary>
        /// Game.SleepStop scans every WearNTear in the scene on wake, outside its local-player guard. The dedi owns
        /// none of them, so only the scan costs anything, and it stalls the frame the wake RPC flushes in.
        /// </summary>
        [HarmonyPatch(typeof(Game), "SleepStop")]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Game_SleepStop_SkipWearNTearScanOnDedi(
            IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var wearNTearSource = AccessTools.Method(typeof(ServerAuthorityPatches), nameof(WearNTearsToWake));

            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Call && code[i].opcode != OpCodes.Callvirt) continue;

                var called = code[i].operand as MethodInfo;
                if (called == null || called.Name != "FindObjectsByType") continue;

                code[i] = new CodeInstruction(OpCodes.Call, wearNTearSource);
                return code;
            }

            LoggerOptions.LogWarning(
                "[SleepStop] Game.SleepStop has no FindObjectsByType call to redirect; the dedicated server keeps vanilla's scene-wide scan on wake.");
            return code;
        }

        private static WearNTear[] WearNTearsToWake(FindObjectsSortMode sortMode)
        {
            if (ServerClientUtils.ZNetIsDedicated()) return System.Array.Empty<WearNTear>();
            return UnityEngine.Object.FindObjectsByType<WearNTear>(sortMode);
        }

        private static AccessTools.FieldRef<ZSyncTransform, bool> _bodyIsKinematicSnapshot;

        /// <summary>ZSyncTransform caches isKinematic in Awake and branches on that copy, so freezing a body must update it.</summary>
        private static void MarkBodyKinematicForSync(ZSyncTransform syncTransform)
        {
            try
            {
                if (_bodyIsKinematicSnapshot == null)
                    _bodyIsKinematicSnapshot = AccessTools.FieldRefAccess<ZSyncTransform, bool>("m_isKinematicBody");
                _bodyIsKinematicSnapshot(syncTransform) = true;
            }
            catch (System.Exception ex)
            {
                LoggerOptions.LogWarning($"[HoldStillOnDedi] ZSyncTransform kinematic snapshot not updated ({ex.Message}); the body is still frozen.");
            }
        }

        // The solid-surface mask lives in SolidSurface now: same config entry, one parser, shared with
        // the ground-snap fix so both answer "what can you stand on?" identically.
        private static int GetFellOutRescueMask() => SolidSurface.Mask();

        /// <summary>Set once server-side simulation is actually running, since this class is only patched in then.</summary>
        internal static bool SimulationActive;

        /// <summary>
        /// True when the object was below the kill plane and has been dealt with, so vanilla's sync must not run. The
        /// Harmony hook on OwnerSync lives in TransformWriteRate because that one has to load on clients too; this class
        /// is only registered on a dedicated server running server-side simulation.
        /// </summary>
        internal static bool TryRescueBelowKillPlane(ZSyncTransform sync, Rigidbody body)
        {
            if (!SimulationActive || !ServerClientUtils.ZNetIsDedicated()) return false;

            Vector3 pos = sync.transform.position;
            if (pos.y >= KillPlaneY) return false;

            StopRigidbody(body);
            TryRescueOntoGroundCollider(sync, pos);
            return true;
        }

        // Kill the fall momentum so the rescue reposition lands clean. We do NOT set isKinematic: setting
        // a velocity on a kinematic body logs "Setting velocity of a kinematic body is not supported" every
        // tick a body sits below the kill plane. Zeroing velocity on the live body stops it just as well.
        private static void StopRigidbody(Rigidbody rb)
        {
            if (rb == null || rb.isKinematic) return;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        private static void TryRescueOntoGroundCollider(ZSyncTransform sync, Vector3 currentPos)
        {
            Vector3 rayStart = new Vector3(currentPos.x, RescueRaycastStartHeight, currentPos.z);
            if (!Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, RescueRaycastMaxDistance, GetFellOutRescueMask()))
                return;

            Vector3 rescued = currentPos;
            rescued.y = hit.point.y + RescueGroundClearance;
            sync.transform.position = rescued;
            Physics.SyncTransforms();
        }
    }
}
