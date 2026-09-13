using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// ZRpc drops a peer whose keepalive has gone unanswered for 30 seconds, and ZSteamSocket queues the 5-byte ping and
    /// its reply behind every packet already waiting to go out. A peer taking a large transfer was timed out while the
    /// link was still delivering (rig, 2026-09-13: kicked 79 s into an 8 GB stream). Keepalives go to the front instead;
    /// their only wait is then Steam's own send buffer.
    /// </summary>
    [HarmonyPatch]
    public static class KeepaliveFirst
    {
        private const int KeepaliveBytes = 5;
        private const double ReportIntervalSeconds = 300.0;

        private static AccessTools.FieldRef<ZSteamSocket, Queue<byte[]>> s_sendQueue;
        private static Action<ZSteamSocket> s_sendQueuedPackages;
        private static int s_jumped;
        private static int s_deepestQueue;
        private static long s_nextReport;

        static bool Prepare()
        {
            if (s_sendQueuedPackages != null) return true;
            var queueField = AccessTools.Field(typeof(ZSteamSocket), "m_sendQueue");
            var sendQueued = AccessTools.Method(typeof(ZSteamSocket), "SendQueuedPackages", Type.EmptyTypes);
            if (queueField == null || queueField.FieldType != typeof(Queue<byte[]>) || sendQueued == null)
            {
                LoggerOptions.LogWarning("[Keepalive] ZSteamSocket no longer has the send queue this fix reorders; keepalives keep vanilla's order.");
                return false;
            }
            s_sendQueue = AccessTools.FieldRefAccess<ZSteamSocket, Queue<byte[]>>(queueField);
            s_sendQueuedPackages = AccessTools.MethodDelegate<Action<ZSteamSocket>>(sendQueued);
            return true;
        }

        [HarmonyPatch(typeof(ZSteamSocket), nameof(ZSteamSocket.Send), new[] { typeof(ZPackage) })]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        static bool Prefix(ZSteamSocket __instance, ZPackage pkg)
        {
            if (!(FiresGhettoNetworkMod.ConfigKeepaliveFirst?.Value ?? false)) return true;
            if (pkg == null || pkg.Size() != KeepaliveBytes || !__instance.IsConnected()) return true;

            Queue<byte[]> queue = s_sendQueue(__instance);
            if (queue == null || queue.Count == 0) return true;

            // Method hash 0 is ZRpc's ping and pong and nothing else.
            byte[] packet = pkg.GetArray();
            if (packet[0] != 0 || packet[1] != 0 || packet[2] != 0 || packet[3] != 0) return true;

            byte[][] waiting = queue.ToArray();
            queue.Clear();
            queue.Enqueue(packet);
            for (int i = 0; i < waiting.Length; i++) queue.Enqueue(waiting[i]);
            s_sendQueuedPackages(__instance);

            s_jumped++;
            if (waiting.Length > s_deepestQueue) s_deepestQueue = waiting.Length;
            MaybeReport();
            return false;
        }

        private static void MaybeReport()
        {
            long now = Stopwatch.GetTimestamp();
            if (s_nextReport != 0 && now < s_nextReport) return;
            if (s_nextReport == 0)
                LoggerOptions.LogInfo($"[Keepalive] A keepalive went ahead of {s_deepestQueue} queued packet(s) so a busy connection is not timed out. Next report in 5 min at the earliest.");
            else
                LoggerOptions.LogInfo($"[Keepalive] {s_jumped} keepalive(s) went ahead of queued packets since the last report, the deepest queue {s_deepestQueue} packet(s).");
            s_jumped = 0;
            s_deepestQueue = 0;
            s_nextReport = now + (long)(ReportIntervalSeconds * Stopwatch.Frequency);
        }
    }
}
