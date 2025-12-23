using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [HarmonyPatch]
    public static class AILODPatches
    {
        // Target Character.FixedUpdate (base for all AI/monsters)
        [HarmonyPatch(typeof(Character), "FixedUpdate")]
        [HarmonyPrefix]
        public static bool FixedUpdate_Prefix(Character __instance)
        {
            if (!ZNet.instance.IsServer() || !FiresGhettoNetworkMod.ConfigEnableAILOD.Value)
                return true; // Run vanilla

            // Skip players and tamed creatures (optional — keep full speed)
            if (__instance.IsPlayer() || (__instance.GetComponent<Tameable>() is Tameable tame && tame.IsTamed()))
                return true;

            // Find nearest player
            float nearestDist = float.MaxValue;
            foreach (Player player in Player.GetAllPlayers())
            {
                if (player != null)
                {
                    float dist = Vector3.Distance(__instance.transform.position, player.transform.position);
                    if (dist < nearestDist) nearestDist = dist;
                }
            }

            if (nearestDist <= FiresGhettoNetworkMod.ConfigAILODNearDistance.Value)
                return true; // Full speed near players

            if (nearestDist > FiresGhettoNetworkMod.ConfigAILODFarDistance.Value)
            {
                // Throttle distant AI
                if (Time.time % (1f / FiresGhettoNetworkMod.ConfigAILODThrottleFactor.Value) > Time.fixedDeltaTime)
                    return false; // Skip this FixedUpdate
            }

            return true; // Normal update
        }
    }
}