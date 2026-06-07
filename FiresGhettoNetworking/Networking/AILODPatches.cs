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

            ServerStatusDiagnostics.s_ailod_examined++;

            if (__instance.IsPlayer() || __instance.IsTamed())
            {
                ServerStatusDiagnostics.s_ailod_playerOrTamed++;
                return true;
            }

            float nearestDist = ComputeDistanceToNearestPeer(__instance.transform.position,
                                                             out int peerCount);
            ServerStatusDiagnostics.s_ailod_peersLastSeen = peerCount;
            UpdateNearestDistanceObservedRange(nearestDist);

            float nearMeters = FiresGhettoNetworkMod.ConfigAILODNearDistance.Value;
            float farMeters  = FiresGhettoNetworkMod.ConfigAILODFarDistance.Value;

            if (nearestDist <= nearMeters)
            {
                ServerStatusDiagnostics.s_ailod_decidedNear++;
                return true;
            }

            if (nearestDist > farMeters)
                return DecideFarBandTickOrSkip(__instance, dt);

            ServerStatusDiagnostics.s_ailod_decidedMidBand++;
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
            if (nearestDist < ServerStatusDiagnostics.s_ailod_minNearestDist)
                ServerStatusDiagnostics.s_ailod_minNearestDist = nearestDist;
            if (nearestDist > ServerStatusDiagnostics.s_ailod_maxNearestDist && nearestDist < float.MaxValue)
                ServerStatusDiagnostics.s_ailod_maxNearestDist = nearestDist;
        }

        private static bool DecideFarBandTickOrSkip(Character mob, float dt)
        {
            float throttleInterval = 1f / FiresGhettoNetworkMod.ConfigAILODThrottleFactor.Value;
            float jitteredPhase = Time.time + mob.GetHashCode() * PerInstanceHashJitterMultiplier;
            bool shouldSkipThisTick = jitteredPhase % throttleInterval > dt;

            if (shouldSkipThisTick)
            {
                ServerStatusDiagnostics.s_ailod_decidedFarSkipped++;
                return false;
            }
            ServerStatusDiagnostics.s_ailod_decidedFarRan++;
            return true;
        }
    }
}