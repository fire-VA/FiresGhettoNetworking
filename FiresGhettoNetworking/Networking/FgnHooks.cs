using System;
using System.Collections.Generic;
using System.Reflection;
using FiresGhettoNetworkMod.AutoTune;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// FGN's one Harmony hook per game method and patch kind. Each feature keeps its handler; the hook calls the handlers of
    /// the features Ascend attached, in the order their separate hooks ran: priority first, then attach order.
    /// </summary>
    internal static class FgnHooks
    {
        private static readonly HashSet<Type> s_attached = new HashSet<Type>();

        private static bool s_compression, s_rates, s_dedicated, s_liveRefPos, s_heartbeat, s_fallThrough, s_pieceAudit,
            s_disconnects, s_bulk, s_wireWriter, s_zdoDelta, s_disarmTest, s_compressionTest, s_stressTests, s_headroom,
            s_links, s_scheduler, s_echo, s_creatures, s_helm, s_keepalive, s_roundTrip, s_floodTest, s_playerSync,
            s_clientSupport, s_cleanup, s_capeDiag, s_autoTune, s_profile, s_sectors, s_router, s_stations, s_shipFixes,
            s_shipSim, s_serverAuthority, s_stability, s_ownershipV3, s_ownershipV2;

        internal static void NoteAttached(Type feature) => s_attached.Add(feature);

        private static bool On(Type feature) => s_attached.Contains(feature);

        internal static Type[] ReadAttachedFeatures()
        {
            s_compression = On(typeof(CompressionGroup));
            s_rates = On(typeof(NetworkingRatesGroup));
            s_dedicated = On(typeof(DedicatedServerGroup));
            s_liveRefPos = On(typeof(SyncListRefPosPatches));
            s_heartbeat = On(typeof(SendZDOsHeartbeatDiagnostic));
            s_fallThrough = On(typeof(FallThroughProbe));
            s_pieceAudit = On(typeof(PieceTypeAudit));
            s_disconnects = On(typeof(ServerDisconnectDiagnostics));
            s_bulk = On(typeof(BulkTransferGatePatches));
            s_wireWriter = On(typeof(ZdoWireWriter));
            s_zdoDelta = On(typeof(ZDODeltaPatches));
            s_disarmTest = On(typeof(DisarmOverloadTest));
            s_compressionTest = On(typeof(CompressionRoundTripTest));
            s_stressTests = On(typeof(SocketStressTests));
            s_headroom = On(typeof(SendQueueHeadroomMonitor));
            s_links = On(typeof(LinkController));
            s_scheduler = On(typeof(SendScheduler));
            s_echo = On(typeof(ConnectionEcho));
            s_creatures = On(typeof(CreatureOwnership));
            s_helm = On(typeof(HelmOwnership));
            s_keepalive = On(typeof(KeepaliveFirst)) && KeepaliveFirst.Ready;
            s_roundTrip = On(typeof(RoundTripTrace));
            s_floodTest = On(typeof(ZdoFloodTest));
            s_playerSync = On(typeof(PlayerPositionSyncPatches));
            s_clientSupport = On(typeof(WearNTearClientSupportPatches));
            s_cleanup = On(typeof(ClientCleanupThrottle));
            s_capeDiag = On(typeof(CapeCrashDiagnostics));
            s_autoTune = On(typeof(AutoTuneProbeHooks));
            s_profile = On(typeof(ServerFrameProfile));
            s_sectors = On(typeof(SectorChangeTracker));
            s_router = On(typeof(RpcRouterPatches));
            s_stations = On(typeof(StationRouter));
            s_shipFixes = On(typeof(ShipFixesGroup));
            s_shipSim = On(typeof(ServerShipSimulationPatches));
            s_serverAuthority = On(typeof(ServerAuthorityPatches));
            s_stability = On(typeof(ServerStabilityPatches));
            s_ownershipV3 = On(typeof(ServerOwnershipPatchesV3));
            s_ownershipV2 = On(typeof(ServerOwnershipPatches));
            return typeof(FgnHooks).GetNestedTypes(BindingFlags.NonPublic);
        }

        [HarmonyPatch]
        private static class ZNetStart
        {
            static bool Prepare() => s_compression || s_rates || s_dedicated || s_bulk || s_wireWriter || s_disarmTest
                || s_compressionTest || s_stressTests || s_headroom || s_links || s_scheduler || s_floodTest || s_autoTune || s_sectors;

            [HarmonyPatch(typeof(ZNet), "Start"), HarmonyPostfix]
            static void Postfix(ZNet __instance)
            {
                if (s_compression) CompressionGroup.OnZNetStart();
                if (s_rates) NetworkingRatesGroup.EnsureRatesOnStart();
                if (s_dedicated) DedicatedServerGroup.LogBackendAtZNetStart();
                if (s_bulk) BulkTransferGatePatches.ApplyOnZNetStart();
                if (s_wireWriter) ZdoWireWriter.ZNet_Start_DetectForeignSerializer();
                if (s_disarmTest) DisarmOverloadTest.OnZNetStart();
                if (s_compressionTest) CompressionRoundTripTest.OnZNetStart();
                if (s_stressTests) SocketStressTests.OnZNetStart();
                if (s_headroom) SendQueueHeadroomMonitor.OnZNetStart();
                if (s_links) LinkController.OnZNetStart();
                if (s_scheduler) SendScheduler.OnZNetStart();
                if (s_floodTest) ZdoFloodTest.OnZNetStart();
                if (s_autoTune) AutoTuneProbeHooks.ZNet_Start_Postfix();
                if (s_sectors) SectorChangeTracker.OnStart(__instance);
            }
        }

        [HarmonyPatch]
        private static class ZNetOnNewConnection
        {
            static bool Prepare() => s_compression || s_disconnects || s_echo || s_creatures || s_autoTune;

            [HarmonyPatch(typeof(ZNet), "OnNewConnection"), HarmonyPostfix]
            static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                if (s_compression) CompressionGroup.OnNewConnection(peer);
                if (s_disconnects) ServerDisconnectDiagnostics.OnNewConnection_StampConnectTime(peer);
                if (s_echo) ConnectionEcho.OnNewConnection(__instance, peer);
                if (s_creatures) CreatureOwnership.OnNewConnection(__instance, peer);
                if (s_autoTune) AutoTuneProbeHooks.ZNet_OnNewConnection_Postfix(peer);
            }
        }

        [HarmonyPatch]
        private static class ZNetDisconnect
        {
            static bool Prepare() => s_disconnects || s_compression || s_echo || s_autoTune;

            [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect)), HarmonyPrefix]
            static void Prefix(ZNetPeer peer)
            {
                if (s_disconnects) ServerDisconnectDiagnostics.Disconnect_LogPeer(peer);
            }

            [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect)), HarmonyPostfix]
            static void Postfix(ZNetPeer peer)
            {
                if (s_compression) CompressionGroup.OnDisconnect(peer);
                if (s_echo) ConnectionEcho.OnDisconnect(peer);
                if (s_autoTune) AutoTuneProbeHooks.ZNet_Disconnect_Postfix(peer);
            }
        }

        [HarmonyPatch]
        private static class ZNetShutdown
        {
            static bool Prepare() => s_compression || s_links || s_scheduler || s_echo || s_creatures || s_helm || s_autoTune
                || s_sectors || s_stations;

            [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown)), HarmonyPostfix]
            static void Postfix()
            {
                if (s_compression) CompressionGroup.OnZNetShutdown();
                if (s_links) LinkController.OnZNetShutdown();
                if (s_scheduler) SendScheduler.OnZNetShutdown();
                if (s_echo) ConnectionEcho.OnShutdown();
                if (s_creatures) CreatureOwnership.OnShutdown();
                if (s_helm) HelmOwnership.OnShutdown();
                if (s_autoTune) AutoTuneProbeHooks.ZNet_Shutdown_Postfix();
                if (s_sectors) SectorChangeTracker.OnShutdown();
                if (s_stations) StationRouter.OnZNetShutdown();
            }
        }

        [HarmonyPatch]
        private static class ZNetOnDestroy
        {
            private static readonly System.Reflection.EventInfo s_graphicsChanged =
                typeof(GraphicsSettingsManager).GetEvent("GraphicsSettingsChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            /// <summary>
            /// A vanilla error at quit (Fire, 2026-09-30: "if we can fix it we should"). Vanilla's OnDestroy disposes m_blockCheckHandler,
            /// which only ZNet.Start creates. A game that quits while the main menu is still loading destroys that scene's ZNet after its
            /// Awake but before its Start, and the Dispose throws (every bot log that logged out, then quit: "NullReferenceException at
            /// ZNet.DMD&lt;ZNet::OnDestroy&gt;"). For such a ZNet this does vanilla's work without the Dispose: log, clear the instance,
            /// and undo Awake's graphics-settings subscription.
            /// </summary>
            [HarmonyPatch(typeof(ZNet), "OnDestroy"), HarmonyPrefix]
            static bool Prefix(ZNet __instance)
            {
                if (__instance == null || __instance.m_blockCheckHandler != null) return true;
                ZLog.Log("ZNet OnDestroy");
                if (ZNet.m_instance == __instance) ZNet.m_instance = null;
                try
                {
                    Delegate handshake = Delegate.CreateDelegate(typeof(Action), __instance, "SimulationDistanceServerHandshake");
                    s_graphicsChanged?.RemoveEventHandler(null, handshake);
                }
                catch (Exception) { }   // quitting anyway: a missing handler must not bring back the error this replaces
                return false;
            }

            [HarmonyPatch(typeof(ZNet), "OnDestroy"), HarmonyPostfix]
            static void Postfix()
            {
                if (s_links) LinkController.OnZNetDestroy();
                if (s_autoTune) AutoTuneProbeHooks.ZNet_OnDestroy_Postfix();
            }
        }

        [HarmonyPatch]
        private static class ZNetUpdate
        {
            static bool Prepare() => s_profile || s_sectors || s_links || s_echo || s_dedicated;

            [HarmonyPatch(typeof(ZNet), "Update"), HarmonyPrefix, HarmonyPriority(Priority.First)]
            static void Prefix()
            {
                if (s_profile) ServerFrameProfile.NetBegin();
                if (s_sectors) SectorChangeTracker.NextFrame();
            }

            [HarmonyPatch(typeof(ZNet), "Update"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
            static void Postfix(ZNet __instance)
            {
                if (s_dedicated) RefPosAudit.Tick(__instance);
                if (s_links) LinkController.ReportPeriodically(__instance);
                if (s_echo) ConnectionEcho.SendEchoes(__instance);
                if (s_profile) ServerFrameProfile.NetEnd();
            }
        }

        [HarmonyPatch]
        private static class ZDOManUpdate
        {
            static bool Prepare() => s_profile || s_zdoDelta || s_creatures || s_stations;

            [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Update)), HarmonyPrefix, HarmonyPriority(Priority.First)]
            static void Prefix()
            {
                if (s_profile) ServerFrameProfile.SendBegin();
            }

            [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Update)), HarmonyPostfix, HarmonyPriority(Priority.Last)]
            static void Postfix(ZDOMan __instance)
            {
                if (s_zdoDelta) ZDODeltaPatches.ZDOMan_Update_ForgetExpiredSnapshots(__instance);
                if (s_creatures) CreatureOwnership.Balance(__instance);
                if (s_stations) StationRouter.ReleaseHeld();
                if (s_profile) ServerFrameProfile.SendEnd();
            }
        }

        [HarmonyPatch]
        private static class ZDOManCreateSyncList
        {
            static bool Prepare() => s_profile || s_sectors || s_liveRefPos;

            [HarmonyPatch(typeof(ZDOMan), "CreateSyncList"), HarmonyPrefix, HarmonyPriority(Priority.First)]
            static void Prefix(ZDOMan.ZDOPeer peer)
            {
                if (s_profile) ServerFrameProfile.ScanBegin();
                if (s_sectors) SectorChangeTracker.ScanBegin(peer);
            }

            [HarmonyPatch(typeof(ZDOMan), "CreateSyncList"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
            static void Postfix()
            {
                if (s_profile) ServerFrameProfile.ScanEnd();
            }

            [HarmonyPatch(typeof(ZDOMan), "CreateSyncList"), HarmonyFinalizer]
            static void Finalizer()
            {
                if (s_sectors) SectorChangeTracker.ScanEnd();
            }

            [HarmonyPatch(typeof(ZDOMan), "CreateSyncList"), HarmonyTranspiler]
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
                => s_liveRefPos ? SyncListRefPosPatches.CreateSyncList_LiveRefPosTranspiler(instructions) : instructions;
        }

        [HarmonyPatch]
        private static class ZDOManSendZDOToPeers2
        {
            static bool Prepare() => s_rates || s_scheduler;

            [HarmonyPatch(typeof(ZDOMan), "SendZDOToPeers2"), HarmonyPrefix]
            static bool Prefix(ZDOMan __instance, ref float dt)
            {
                if (s_rates) NetworkingRatesGroup.AdjustUpdateInterval(ref dt);
                return !s_scheduler || SendScheduler.SendZDOToPeers2_Prefix(__instance);
            }
        }

        [HarmonyPatch]
        private static class ZDOManSendZDOs
        {
            static bool Prepare() => s_zdoDelta || s_heartbeat || s_rates;

            [HarmonyPatch(typeof(ZDOMan), "SendZDOs"), HarmonyPrefix, HarmonyPriority(Priority.Last)]
            static void Prefix(ZDOMan.ZDOPeer peer, bool flush)
            {
                if (s_zdoDelta) ZDODeltaPatches.SendZDOs_Prefix(peer, flush);
                if (s_heartbeat) SendZDOsHeartbeatDiagnostic.SendZDOs_Heartbeat_Prefix(peer, flush);
            }

            [HarmonyPatch(typeof(ZDOMan), "SendZDOs"), HarmonyPostfix]
            static void Postfix(ZDOMan.ZDOPeer peer, bool flush)
            {
                if (s_zdoDelta) ZDODeltaPatches.SendZDOs_Postfix(peer, flush);
            }

            [HarmonyPatch(typeof(ZDOMan), "SendZDOs"), HarmonyTranspiler]
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
                => s_rates ? NetworkingRatesGroup.SendZDOs_WindowTranspiler(instructions) : instructions;
        }

        [HarmonyPatch]
        private static class ZDOManReleaseNearbyZDOS
        {
            static bool Prepare() => s_ownershipV3 || s_ownershipV2;

            [HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS"), HarmonyPrefix]
            static bool Prefix(ZDOMan __instance, Vector3 refPosition, long uid)
            {
                bool runOriginal = true;
                if (s_ownershipV3) runOriginal &= ServerOwnershipPatchesV3.ReleaseNearbyZDOS_Prefix(__instance, refPosition, uid);
                if (s_ownershipV2) runOriginal &= ServerOwnershipPatches.ReleaseNearbyZDOS_Prefix(__instance, refPosition, uid);
                return runOriginal;
            }
        }

        [HarmonyPatch]
        private static class ZDOManRemovePeer
        {
            static bool Prepare() => s_heartbeat || s_zdoDelta || s_links || s_scheduler || s_sectors || s_serverAuthority;

            [HarmonyPatch(typeof(ZDOMan), "RemovePeer"), HarmonyPostfix]
            static void Postfix(ZNetPeer netPeer)
            {
                if (s_heartbeat) SendZDOsHeartbeatDiagnostic.RemovePeer_ClearStats_Postfix(netPeer);
                if (s_zdoDelta) ZDODeltaPatches.RemovePeer_Postfix(netPeer);
                if (s_links) LinkController.OnRemovePeer(netPeer);
                if (s_scheduler) SendScheduler.OnRemovePeer(netPeer);
                if (s_sectors) SectorChangeTracker.OnRemovePeer(netPeer);
                if (s_serverAuthority) ServerAuthorityPatches.RemovePeer_ClearMotion_Postfix(netPeer);
            }
        }

        [HarmonyPatch]
        private static class ZDODeserialize
        {
            static bool Prepare() => s_playerSync || s_capeDiag || s_sectors;

            [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize)), HarmonyPostfix]
            static void Postfix(ZDO __instance)
            {
                if (s_playerSync) PlayerPositionSyncPatches.ZDO_Deserialize_Postfix(__instance);
                if (s_capeDiag) CapeCrashDiagnostics.ZDO_Deserialize_Postfix(__instance);
                if (s_sectors) SectorChangeTracker.OnDeserialize(__instance);
            }
        }

        [HarmonyPatch]
        private static class ZNetSceneAwake
        {
            static bool Prepare() => s_pieceAudit || s_ownershipV3 || s_ownershipV2;

            [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
            static void Postfix(ZNetScene __instance)
            {
                if (s_pieceAudit) PieceTypeAudit.ZNetScene_Awake_Postfix();
                if (s_ownershipV3) ServerOwnershipPatchesV3.ZNetScene_Awake_BuildSet(__instance);
                if (s_ownershipV2) ServerOwnershipPatches.ZNetScene_Awake_BuildExclusionSets(__instance);
            }
        }

        [HarmonyPatch]
        private static class ZNetSceneShutdown
        {
            static bool Prepare() => s_cleanup || s_shipSim || s_ownershipV3;

            [HarmonyPatch(typeof(ZNetScene), "Shutdown"), HarmonyPostfix]
            static void Postfix()
            {
                if (s_cleanup) ClientCleanupThrottle.ZNetScene_Shutdown_ClearPendingState();
                if (s_shipSim) ServerShipSimulationPatches.ZNetScene_Shutdown_ClearParked();
                if (s_ownershipV3) ServerOwnershipPatchesV3.ZNetScene_Shutdown_DropSet();
            }
        }

        [HarmonyPatch]
        private static class TombStoneAwake
        {
            static bool Prepare() => s_fallThrough || s_serverAuthority;

            [HarmonyPatch(typeof(TombStone), "Awake"), HarmonyPostfix]
            static void Postfix(TombStone __instance)
            {
                if (s_fallThrough) FallThroughProbe.TombStone_Awake_Probe(__instance);
                if (s_serverAuthority) ServerAuthorityPatches.TombStone_Awake_DediKinematic_Postfix(__instance);
            }
        }

        [HarmonyPatch]
        private static class ZRoutedRpcRouteRPC
        {
            static bool Prepare() => s_router || s_stability;

            [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC"), HarmonyPrefix]
            static bool Prefix(ZRoutedRpc __instance, ZRoutedRpc.RoutedRPCData rpcData, bool __runOriginal)
            {
                bool runOriginal = !s_router || RpcRouterPatches.RouteRPC_Prefix(__instance, rpcData, __runOriginal);
                if (s_stability) ServerStabilityPatches.ZRoutedRpc_RouteRPC_ShipRequestRespons_Prefix(rpcData);
                return runOriginal;
            }
        }

        [HarmonyPatch]
        private static class ZoneSystemUpdate
        {
            static bool Prepare() => s_profile || s_serverAuthority;

            [HarmonyPatch(typeof(ZoneSystem), "Update"), HarmonyPrefix, HarmonyPriority(Priority.First)]
            static void Prefix()
            {
                if (s_profile) ServerFrameProfile.ZoneBegin();
            }

            [HarmonyPatch(typeof(ZoneSystem), "Update"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
            static void Postfix(ZoneSystem __instance)
            {
                if (s_serverAuthority) ServerAuthorityPatches.ZoneSystem_Update_Postfix(__instance);
                if (s_profile) ServerFrameProfile.ZoneEnd();
            }
        }

        [HarmonyPatch]
        private static class WearNTearUpdateSupport
        {
            static bool Prepare() => s_clientSupport || s_stability;

            [HarmonyPatch(typeof(WearNTear), "UpdateSupport"), HarmonyPrefix]
            static bool Prefix(WearNTear __instance)
            {
                bool runOriginal = !s_clientSupport || WearNTearClientSupportPatches.UpdateSupport_Prefix(__instance);
                if (s_stability) ServerStabilityPatches.WearNTear_UpdateSupport_ReinitColliders_Prefix(__instance);
                return runOriginal;
            }
        }

        [HarmonyPatch]
        private static class ZSteamSocketSend
        {
            static bool Prepare() => s_keepalive || s_compression;

            [HarmonyPatch(typeof(ZSteamSocket), nameof(ZSteamSocket.Send), new[] { typeof(ZPackage) }), HarmonyPrefix, HarmonyPriority(Priority.First)]
            static bool Prefix(ZSteamSocket __instance, ZPackage pkg) => !s_keepalive || KeepaliveFirst.JumpQueue(__instance, pkg);

            [HarmonyPatch(typeof(ZSteamSocket), nameof(ZSteamSocket.Send), new[] { typeof(ZPackage) }), HarmonyTranspiler]
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
                => s_compression ? CompressionGroup.Steam_CompressOnEnqueue(instructions) : instructions;
        }

        [HarmonyPatch]
        private static class ZSteamSocketRecv
        {
            static bool Prepare() => s_compression || s_roundTrip;

            [HarmonyPatch(typeof(ZSteamSocket), nameof(ZSteamSocket.Recv)), HarmonyPostfix]
            static void Postfix(ref ZPackage __result, ZSteamSocket __instance)
            {
                if (s_compression) CompressionGroup.Steam_RecvCompressed(ref __result, __instance);
            }

            [HarmonyPatch(typeof(ZSteamSocket), nameof(ZSteamSocket.Recv)), HarmonyTranspiler]
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
                => s_roundTrip ? RoundTripTrace.NoteEachSteamArrival(instructions) : instructions;
        }

        [HarmonyPatch]
        private static class ShipCustomFixedUpdate
        {
            static bool Prepare() => s_shipSim || s_shipFixes || s_helm;

            [HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate)), HarmonyPrefix]
            static bool Prefix(Ship __instance, ZNetView ___m_nview, Rigidbody ___m_body)
            {
                if (s_helm) HelmOwnership.HoldSlamOnTakeover(__instance, ___m_nview);
                return !s_shipSim || ServerShipSimulationPatches.Ship_CustomFixedUpdate_ParkUntilEnvironmentReady(__instance, ___m_nview, ___m_body);
            }

            [HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate)), HarmonyPostfix]
            static void Postfix(Ship __instance)
            {
                if (s_shipFixes) ShipFixesGroup.CustomFixedUpdate_Postfix(__instance);
            }
        }
    }
}
