using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [HarmonyPatch]
    public static class ServerAuthorityPatches
    {
        private const int DefaultActiveArea = 3;
        private const int DefaultDistantArea = 5;
        private const float ZoneSizeMeters = 64f;

        private const float KillPlaneY = -5000f;
        private const float RescueRaycastStartHeight = 6000f;
        private const float RescueRaycastMaxDistance = 12000f;
        private const float RescueGroundClearance = 1f;
        private const string DefaultRescueLayerMaskCsv = "Default,static_solid,Default_small,piece,terrain,vehicle";

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

        private static Vector3 RefreshVelocityIfMovedEnough(long uid, PeerMotionSample prev, Vector3 currentPos, float dt, float now)
        {
            Vector3 delta = currentPos - prev.Pos;
            if (delta.sqrMagnitude <= VelocityRefreshMinMoveSqr || dt <= VelocitySampleMinDeltaSec)
                return prev.Velocity;

            Vector3 instantVel = delta / dt;
            Vector3 smoothed = Vector3.Lerp(prev.Velocity, instantVel, VelocityEmaWeight);
            _peerMotion[uid] = new PeerMotionSample { Pos = currentPos, Time = now, Velocity = smoothed };
            return smoothed;
        }

        private static Vector3 ClampForwardOffsetToMaxZones(Vector3 offset, int maxZones)
        {
            float maxForward = maxZones * ZoneSizeMeters;
            return offset.sqrMagnitude > maxForward * maxForward ? offset.normalized * maxForward : offset;
        }

        [HarmonyPatch(typeof(ZDOMan), "RemovePeer")]
        [HarmonyPostfix]
        public static void RemovePeer_ClearMotion_Postfix(ZNetPeer netPeer)
        {
            if (netPeer == null) return;
            _peerMotion.Remove(netPeer.m_uid);
        }

        [HarmonyPatch(typeof(ZoneSystem), "IsActiveAreaLoaded")]
        [HarmonyPriority(Priority.First)]
        [HarmonyPrefix]
        public static bool IsActiveAreaLoaded_TeleportFix(ref bool __result)
        {
            if (Player.m_localPlayer == null || !Player.m_localPlayer.IsTeleporting()) return true;
            __result = true;
            return false;
        }

        private static readonly List<ZDO> _cdoNearScratch = new List<ZDO>(8192);
        private static readonly List<ZDO> _cdoDistantScratch = new List<ZDO>(8192);
        private static readonly HashSet<ZDO> _cdoSeenSet = new HashSet<ZDO>();
        private static readonly List<ZDO> _cdoNearFiltered = new List<ZDO>(8192);
        private static readonly List<ZDO> _cdoDistantFiltered = new List<ZDO>(8192);

        [HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
        [HarmonyPrefix]
        public static bool CreateDestroyObjects_Prefix(ZNetScene __instance)
        {
            if (!ZNet.instance || !ZNet.instance.IsDedicated() || ZNet.instance.GetConnectedPeers().Count == 0)
            {
                ServerStatusDiagnostics.s_cdo_bail_noPeers++;
                return true;
            }

            try
            {
                int extendedRadius = FiresGhettoNetworkMod.ConfigExtendedZoneRadius.Value;
                int activeArea = (ZoneSystem.instance?.m_activeArea ?? DefaultActiveArea) + extendedRadius;
                int distantArea = (ZoneSystem.instance?.m_activeDistantArea ?? DefaultDistantArea) + extendedRadius;

                CollectZdosFromAllPeerActiveAreas(activeArea, distantArea);
                FilterAndDedupeZdos(_cdoNearScratch, _cdoNearFiltered);
                FilterAndDedupeZdos(_cdoDistantScratch, _cdoDistantFiltered);
                RecordCreateDestroyObjectsDiagnostics();

                __instance.CreateObjects(_cdoNearFiltered, _cdoDistantFiltered);
                PruneOrphanInstances(__instance);
                __instance.RemoveObjects(_cdoNearFiltered, _cdoDistantFiltered);
                return false;
            }
            catch (System.NullReferenceException ex)
            {
                ServerStatusDiagnostics.s_cdo_bail_nre++;
                LoggerOptions.LogWarning($"CreateDestroyObjects encountered NRE (likely mod conflict), falling back to vanilla: {ex.Message}");
                return true;
            }
        }

        private static void CollectZdosFromAllPeerActiveAreas(int activeArea, int distantArea)
        {
            _cdoNearScratch.Clear();
            _cdoDistantScratch.Clear();
            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
            {
                if (!peer.IsReady()) continue;
                Vector3 pos = GetPredictedRefPos(peer);
#if PUBLIC_TEST
                Vector2s zone = ZoneSystem.GetZone(pos);
#else
                Vector2i zone = ZoneSystem.GetZone(pos);
#endif
                ZDOMan.instance.FindSectorObjects(zone, activeArea, distantArea, _cdoNearScratch, _cdoDistantScratch);
            }
        }

        private static void FilterAndDedupeZdos(List<ZDO> source, List<ZDO> dest)
        {
            dest.Clear();
            _cdoSeenSet.Clear();
            for (int i = 0; i < source.Count; i++)
            {
                var zdo = source[i];
                if (zdo == null || !zdo.IsValid() || zdo.m_uid.IsNone()) continue;
                if (_cdoSeenSet.Add(zdo)) dest.Add(zdo);
            }
        }

        private static void RecordCreateDestroyObjectsDiagnostics()
        {
            ServerStatusDiagnostics.s_cdo_passes++;
            ServerStatusDiagnostics.s_cdo_nearTotal += _cdoNearScratch.Count;
            ServerStatusDiagnostics.s_cdo_distantTotal += _cdoDistantScratch.Count;
            ServerStatusDiagnostics.s_cdo_distinctNearTotal += _cdoNearFiltered.Count;
            ServerStatusDiagnostics.s_cdo_distinctDistantTotal += _cdoDistantFiltered.Count;
            if (ZoneSystem.instance != null && ZoneSystem.instance.IsActiveAreaLoaded())
                ServerStatusDiagnostics.s_cdo_areaReadyTrue++;
            else
                ServerStatusDiagnostics.s_cdo_areaReadyFalse++;
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

        private static void PruneOrphanInstances(ZNetScene scene)
        {
            if (!(FiresGhettoNetworkMod.ConfigEnableInstanceOrphanPrune?.Value ?? true)) return;
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
            ServerStatusDiagnostics.s_cdo_orphansPruned += _orphanScratch.Count;
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
            int activeArea = zs.m_activeArea;
            var zones = zs.m_zones;
            int missing = 0;
            firstMissX = 0;
            firstMissY = 0;
            bool capturedFirstMiss = false;

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (!peer.IsReady()) continue;
                Vector3 refPos = GetPredictedRefPos(peer);
#if PUBLIC_TEST
                Vector2s centre = ZoneSystem.GetZone(refPos);
                for (int y = centre.y - activeArea; y <= centre.y + activeArea; y++)
                    for (int x = centre.x - activeArea; x <= centre.x + activeArea; x++)
                        if (!zones.ContainsKey(new Vector2s(x, y)))
                        {
                            missing++;
                            if (!capturedFirstMiss) { firstMissX = x; firstMissY = y; capturedFirstMiss = true; }
                        }
#else
                Vector2i centre = ZoneSystem.GetZone(refPos);
                for (int y = centre.y - activeArea; y <= centre.y + activeArea; y++)
                    for (int x = centre.x - activeArea; x <= centre.x + activeArea; x++)
                        if (!zones.ContainsKey(new Vector2i(x, y)))
                        {
                            missing++;
                            if (!capturedFirstMiss) { firstMissX = x; firstMissY = y; capturedFirstMiss = true; }
                        }
#endif
            }
            return missing;
        }

        private static void RecordIsActiveAreaLoadedDiagnostics(int missing, int firstMissX, int firstMissY)
        {
            ServerStatusDiagnostics.s_iaal_calls++;
            if (missing == 0)
            {
                ServerStatusDiagnostics.s_iaal_resultTrue++;
                return;
            }
            ServerStatusDiagnostics.s_iaal_resultFalse++;
            if (missing < ServerStatusDiagnostics.s_iaal_minMissingZones) ServerStatusDiagnostics.s_iaal_minMissingZones = missing;
            if (missing > ServerStatusDiagnostics.s_iaal_maxMissingZones) ServerStatusDiagnostics.s_iaal_maxMissingZones = missing;
            ServerStatusDiagnostics.s_iaal_lastMissingZoneX = firstMissX;
            ServerStatusDiagnostics.s_iaal_lastMissingZoneY = firstMissY;
        }

        [HarmonyPatch(typeof(ZoneSystem), "Update")]
        [HarmonyPostfix]
        public static void ZoneSystem_Update_Postfix(ZoneSystem __instance)
        {
            if (!ZNet.instance || !ZNet.instance.IsDedicated() || ZNet.instance.GetPeers().Count == 0) return;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                if (peer.IsReady())
                    __instance.CreateLocalZones(GetPredictedRefPos(peer));
        }

        [HarmonyPatch(typeof(ZNetScene), "OutsideActiveArea", new[] { typeof(Vector3) })]
        [HarmonyPrefix]
        public static bool OutsideActiveArea_Prefix(ref bool __result, Vector3 point)
        {
            if (!ZNet.instance || !ZNet.instance.IsDedicated() || ZNet.instance.GetPeers().Count == 0)
                return true;

            int extendedRadius = FiresGhettoNetworkMod.ConfigExtendedZoneRadius.Value;
            int activeArea = (ZoneSystem.instance?.m_activeArea ?? DefaultActiveArea) + extendedRadius;

            __result = !IsPointInsideAnyPeerActiveArea(point, activeArea);
            return false;
        }

        private static bool IsPointInsideAnyPeerActiveArea(Vector3 point, int activeArea)
        {
#if PUBLIC_TEST
            Vector2s pointZone = ZoneSystem.GetZone(point);
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                if (peer.IsReady() && ZNetScene.InActiveArea(pointZone, ZoneSystem.GetZone(GetPredictedRefPos(peer)), activeArea))
                    return true;
#else
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                if (peer.IsReady() && !ZNetScene.OutsideActiveArea(point, ZoneSystem.GetZone(GetPredictedRefPos(peer)), activeArea))
                    return true;
#endif
            return false;
        }

        [HarmonyPatch(typeof(Tameable), "Awake")]
        [HarmonyPrefix]
        public static bool Tameable_Awake_Prefix() => !IsDedicatedServer();

        [HarmonyPatch(typeof(Tameable), "Update")]
        [HarmonyPrefix]
        public static bool Tameable_Update_Prefix() => !IsDedicatedServer();

        [HarmonyPatch(typeof(Tameable), "SetText")]
        [HarmonyPrefix]
        public static bool Tameable_SetText_Prefix() => !IsDedicatedServer();

        [HarmonyPatch(typeof(AudioMan), "Update")]
        [HarmonyPrefix]
        public static bool AudioMan_Update_Prefix() => !IsDedicatedServer();

        [HarmonyPatch(typeof(TerrainComp), "Awake")]
        [HarmonyPrefix]
        public static bool TerrainComp_Awake_Prefix() => !IsDedicatedServer();

        [HarmonyPatch(typeof(TerrainComp), "Update")]
        [HarmonyPrefix]
        public static bool TerrainComp_Update_Prefix() => !IsDedicatedServer();

        [HarmonyPatch(typeof(TerrainComp), "OnDestroy")]
        [HarmonyPrefix]
        public static bool TerrainComp_OnDestroy_Prefix() => !IsDedicatedServer();

        [HarmonyPatch(typeof(ShieldDomeImageEffect), "Awake")]
        [HarmonyPrefix]
        public static bool ShieldDomeImageEffect_Awake_Prefix() => !IsDedicatedServer();

        private static bool IsDedicatedServer() => ZNet.instance != null && ZNet.instance.IsDedicated();

        private static int s_fellOutRescueMaskCached;
        private static string s_fellOutRescueMaskCachedSource;

        private static int GetFellOutRescueMask()
        {
            string source = FiresGhettoNetworkMod.ConfigDediFellOutRescueLayers?.Value
                            ?? DefaultRescueLayerMaskCsv;

            if (s_fellOutRescueMaskCachedSource == source) return s_fellOutRescueMaskCached;

            int mask = ParseLayerMaskCsv(source);
            if (mask == 0)
            {
                LoggerOptions.LogWarning(
                    $"[FellOutRescue] Layer mask from '{source}' resolved to 0 — falling back to vanilla terrain only. "
                    + "Check layer names exist in the build.");
                mask = LayerMask.GetMask("terrain");
            }

            s_fellOutRescueMaskCached = mask;
            s_fellOutRescueMaskCachedSource = source;
            return mask;
        }

        private static int ParseLayerMaskCsv(string csv)
        {
            var names = new List<string>();
            foreach (var raw in csv.Split(','))
            {
                var trimmed = raw?.Trim();
                if (!string.IsNullOrEmpty(trimmed)) names.Add(trimmed);
            }
            return names.Count > 0 ? LayerMask.GetMask(names.ToArray()) : 0;
        }

        [HarmonyPatch(typeof(ZSyncTransform), "OwnerSync")]
        [HarmonyPrefix]
        public static bool ZSyncTransform_OwnerSync_DediFellOutFix_Prefix(
            ZSyncTransform __instance,
            Rigidbody ___m_body)
        {
            if (!IsDedicatedServer()) return true;

            Vector3 pos = __instance.transform.position;
            if (pos.y >= KillPlaneY) return true;

            FreezeRigidbodyIfActive(___m_body);
            TryRescueOntoGroundCollider(__instance, ___m_body, pos);
            return false;
        }

        private static void FreezeRigidbodyIfActive(Rigidbody rb)
        {
            if (rb == null || rb.isKinematic) return;
            rb.isKinematic = true;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        private static void TryRescueOntoGroundCollider(ZSyncTransform sync, Rigidbody rb, Vector3 currentPos)
        {
            Vector3 rayStart = new Vector3(currentPos.x, RescueRaycastStartHeight, currentPos.z);
            if (!Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, RescueRaycastMaxDistance, GetFellOutRescueMask()))
                return;

            Vector3 rescued = currentPos;
            rescued.y = hit.point.y + RescueGroundClearance;
            sync.transform.position = rescued;
            if (rb != null)
            {
                rb.isKinematic = false;
                Physics.SyncTransforms();
            }
        }
    }
}
