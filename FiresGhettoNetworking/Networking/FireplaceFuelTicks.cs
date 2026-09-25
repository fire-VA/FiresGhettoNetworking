using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Stops long-burning fires from writing the network every two seconds. Vanilla's UpdateFireplace runs on a 2 s
    /// repeat and writes both "lastTime" and "fuel" every time, and each write bumps the object's revision, dirties its
    /// sector and puts the object back on the wire. A ground torch burns one fuel per 20,000 seconds, so those writes
    /// track a drain of five thousandths of a percent per tick: 56 of them accounted for 8,346 of one client's uploads
    /// in five minutes.
    ///
    /// Skipping ticks costs nothing, because the drain is computed from the stored timestamp rather than from the tick:
    /// fuel loses (now - lastTime) / secPerFuel whether that is collected once a minute or thirty times a minute. The
    /// fire's visible state is a separate call, so it keeps running on vanilla's 2 s beat, and a fire close enough to
    /// running out that the next tick could matter is left alone entirely.
    /// </summary>
    [HarmonyPatch]
    internal static class FireplaceFuelTicks
    {
        public static ConfigEntry<bool> ConfigEnabled;

        private const double DeferSeconds = 30.0;
        private const double MisdrainFraction = 0.01;

        private enum Decision { NotOurs, Write, Defer }

        private static readonly AccessTools.FieldRef<Fireplace, ZNetView> s_nview
            = AccessTools.FieldRefAccess<Fireplace, ZNetView>("m_nview");
        private static Action<Fireplace> s_updateState;
        private static bool s_bound;

        internal static long TicksDeferred;
        internal static long TicksRun;

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("04 - Networking", "Slow Fuel Ticks On Long Burning Fires", true,
                "Every fireplace and torch writes its fuel level to the network every two seconds, even one carrying\n" +
                "hours of fuel, and every write sends the object to all players who can see it. With this on, a fire\n" +
                "that cannot run out before the next check is only written every thirty seconds. Fuel drains from a\n" +
                "stored timestamp, so the amount burned is exactly the same either way, and a fire close to running\n" +
                "out is left on the normal two second check.");
        }

        [HarmonyPatch(typeof(Fireplace), "UpdateFireplace")]
        [HarmonyPrefix]
        public static bool UpdateFireplace_Prefix(Fireplace __instance)
        {
            switch (Decide(__instance))
            {
                case Decision.Defer:
                    TicksDeferred++;
                    return false;
                case Decision.Write:
                    TicksRun++;
                    return true;
                default:
                    return true;
            }
        }

        private static Decision Decide(Fireplace __instance)
        {
            try
            {
                // Vanilla only writes when it owns the fire and fuel actually burns, so anything else is not ours to
                // count: a fire this machine does not own ticks here but writes nothing either way.
                if (__instance.m_secPerFuel <= 0f) return Decision.NotOurs;

                var nview = s_nview(__instance);
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) return Decision.NotOurs;

                var zdo = nview.GetZDO();
                if (zdo == null || ZNet.instance == null) return Decision.NotOurs;

                if (ConfigEnabled == null || !ConfigEnabled.Value) return Decision.Write;

                long lastTime = zdo.GetLong(ZDOVars.s_lastTime, 0L);
                if (lastTime == 0L) return Decision.Write;

                double elapsed = (ZNet.instance.GetTime().Ticks - lastTime) / (double)TimeSpan.TicksPerSecond;
                if (elapsed < 0.0 || elapsed >= DeferSeconds) return Decision.Write;

                // What a held-back window can cost. Vanilla drains from the stored timestamp, so the only way deferring
                // changes anything is a fire that is lit partway through one: it then collects the whole window instead
                // of the part it was alight for. A torch burns one fuel per 20,000 seconds, so a 30 second window is
                // 0.0015 of a tank and the question does not arise. A bonfire at three seconds a fuel would lose a
                // whole tank, so it keeps vanilla's beat. Fuel level is deliberately not consulted: an unlit or empty
                // fire is the safest of all, because there is nothing left to drain.
                if (!__instance.m_infiniteFuel
                    && DeferSeconds / __instance.m_secPerFuel > __instance.m_maxFuel * MisdrainFraction)
                    return Decision.Write;

                if (!BindUpdateState()) return Decision.Write;
                s_updateState(__instance);
                return Decision.Defer;
            }
            catch
            {
                return Decision.NotOurs;
            }
        }

        private static bool BindUpdateState()
        {
            if (s_bound) return s_updateState != null;
            s_bound = true;
            var method = AccessTools.Method(typeof(Fireplace), "UpdateState");
            if (method != null)
                s_updateState = (Action<Fireplace>)Delegate.CreateDelegate(typeof(Action<Fireplace>), method);
            if (s_updateState == null)
                LoggerOptions.LogWarning("[Fireplace] UpdateState not found; fuel ticks are left on vanilla's two second beat.");
            return s_updateState != null;
        }

        internal static string TakeReportLine()
        {
            if (TicksDeferred == 0 && TicksRun == 0) return null;
            long total = TicksDeferred + TicksRun;
            string line = $"[Upload] Fire fuel ticks on fires this machine owns: {TicksRun:N0} written, "
                + $"{TicksDeferred:N0} held back ({(total > 0 ? TicksDeferred * 100 / total : 0)}%).";
            TicksDeferred = 0;
            TicksRun = 0;
            return line;
        }
    }
}
