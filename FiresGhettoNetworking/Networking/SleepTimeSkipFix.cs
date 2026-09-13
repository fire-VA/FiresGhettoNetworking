using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Vanilla EnvMan.FixedUpdate advances the sleep time skip by one fixed step per rendered frame, so the skip
    /// designed to last 12 seconds takes longer the lower the server's frame rate. This feeds UpdateTimeSkip the
    /// real time since its previous call instead, so morning arrives on schedule however busy the server is.
    /// </summary>
    [HarmonyPatch(typeof(EnvMan), "UpdateTimeSkip")]
    public static class SleepTimeSkipFix
    {
        private const double NotSkipping = -1.0;

        private static double s_previousCallRealtime = NotSkipping;

        [HarmonyPrefix]
        public static void Prefix(EnvMan __instance, ref float dt)
        {
            if (!(FiresGhettoNetworkMod.ConfigFixSlowSleep?.Value ?? false)
                || ZNet.instance == null
                || !ZNet.instance.IsServer()
                || !__instance.IsTimeSkipping())
            {
                s_previousCallRealtime = NotSkipping;
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (s_previousCallRealtime != NotSkipping)
                dt = (float)(now - s_previousCallRealtime);
            s_previousCallRealtime = now;
        }
    }
}
