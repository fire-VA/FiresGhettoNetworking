using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [HarmonyPatch]
    public static class AILODPatches
    {
        private const float PerInstanceHashJitterMultiplier = 0.001f;

        [HarmonyPatch(typeof(Character), "CustomFixedUpdate")]
        [HarmonyPrefix]
        public static bool CustomFixedUpdate_Prefix(Character __instance, float dt)
        {
            if (!IsAILODActiveOnDedicatedServer()) return true;

            // ADAPTIVE: throttling distant AI only helps a server that is actually under
            // load. While no peer's send queue is backing up, run every mob at full rate
            // (vanilla) and skip the per-mob distance scan entirely — far mobs stay
            // responsive and we burn zero extra CPU on a healthy server.
            if ((FiresGhettoNetworkMod.ConfigEnableAdaptiveThrottling == null
                 || FiresGhettoNetworkMod.ConfigEnableAdaptiveThrottling.Value)
                && !SendCongestion.AnyPeerCongested())
                return true;

            ServerStatusDiagnostics.s_aiLod_examined++;

            if (__instance.IsPlayer() || __instance.IsTamed())
            {
                ServerStatusDiagnostics.s_aiLod_playerOrTamed++;
                return true;
            }

            float nearestDist = ComputeDistanceToNearestPeer(__instance.transform.position,
                                                             out int peerCount);
            ServerStatusDiagnostics.s_aiLod_peersLastSeen = peerCount;
            UpdateNearestDistanceObservedRange(nearestDist);

            float nearMeters = FiresGhettoNetworkMod.ConfigAILODNearDistance.Value;
            float farMeters  = FiresGhettoNetworkMod.ConfigAILODFarDistance.Value;

            if (nearestDist <= nearMeters)
            {
                ServerStatusDiagnostics.s_aiLod_decidedNear++;
                return true;
            }

            if (nearestDist > farMeters)
                return DecideFarBandTickOrSkip(__instance, dt);

            ServerStatusDiagnostics.s_aiLod_decidedMidBand++;
            return true;
        }

        private static bool IsAILODActiveOnDedicatedServer() =>
            ZNet.instance != null
            && ZNet.instance.IsDedicated()
            && FiresGhettoNetworkMod.ConfigEnableAILOD.Value;

        private static float ComputeDistanceToNearestPeer(Vector3 origin, out int peerCount)
        {
            float nearestDistSqr = float.MaxValue;
            peerCount = 0;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null) continue;
                peerCount++;
                Vector3 peerPos = peer.GetRefPos();
                float dx = origin.x - peerPos.x;
                float dy = origin.y - peerPos.y;
                float dz = origin.z - peerPos.z;
                float distSqr = dx * dx + dy * dy + dz * dz;
                if (distSqr < nearestDistSqr) nearestDistSqr = distSqr;
            }
            return nearestDistSqr < float.MaxValue ? Mathf.Sqrt(nearestDistSqr) : float.MaxValue;
        }

        private static void UpdateNearestDistanceObservedRange(float nearestDist)
        {
            if (nearestDist < ServerStatusDiagnostics.s_aiLod_minNearestDist)
                ServerStatusDiagnostics.s_aiLod_minNearestDist = nearestDist;
            if (nearestDist > ServerStatusDiagnostics.s_aiLod_maxNearestDist && nearestDist < float.MaxValue)
                ServerStatusDiagnostics.s_aiLod_maxNearestDist = nearestDist;
        }

        private static bool DecideFarBandTickOrSkip(Character mob, float dt)
        {
            float throttleInterval = 1f / FiresGhettoNetworkMod.ConfigAILODThrottleFactor.Value;
            float jitteredPhase = Time.time + mob.GetHashCode() * PerInstanceHashJitterMultiplier;
            bool shouldSkipThisTick = jitteredPhase % throttleInterval > dt;

            if (shouldSkipThisTick)
            {
                ServerStatusDiagnostics.s_aiLod_decidedFarSkipped++;
                return false;
            }
            ServerStatusDiagnostics.s_aiLod_decidedFarRan++;
            return true;
        }
    }
}