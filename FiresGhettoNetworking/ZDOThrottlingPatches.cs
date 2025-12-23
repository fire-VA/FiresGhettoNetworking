using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [HarmonyPatch]
    public static class ZDOThrottlingPatches
    {
        // Throttle update rate for distant ZDOs
        [HarmonyPatch(typeof(ZDOMan), "SendZDOToPeers2")]
        [HarmonyPrefix]
        public static void ThrottleDistantZDOs(ref float dt, List<ZDO> objects, Vector3 refPos)
        {
            if (!ZNet.instance.IsServer() || !FiresGhettoNetworkMod.ConfigEnableZDOThrottling.Value)
                return;

            float throttleDistance = FiresGhettoNetworkMod.ConfigZDOThrottleDistance.Value;
            if (throttleDistance <= 0f) return;

            // Reduce update rate for distant objects
            for (int i = objects.Count - 1; i >= 0; i--)
            {
                ZDO zdo = objects[i];
                if (zdo == null) continue;

                float distance = Vector3.Distance(zdo.GetPosition(), refPos);
                if (distance > throttleDistance)
                {
                    // Throttle distant ZDOs (e.g., 50% slower updates)
                    dt *= 0.5f;
                    // Optional: Remove very distant ZDOs from send list (aggressive)
                    // if (distance > throttleDistance * 2f) objects.RemoveAt(i);
                }
            }
        }
    }
}