using System.Collections.Generic;
using FiresGhettoNetworkMod.Api;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.63 ([lead] / Fire 11-08: FGN relies on no other mod): FGN's stats rows as plain data. A hub mod that shows stats reads them through
    /// FgnApi.GetStatsRows (Version 8) and builds its own card; FGN never looks that mod up. Called only while the hub shows them, on the
    /// main thread; the rows read FgnApi's live state (one pass over the peer list) and a few flags.
    /// </summary>
    internal static class FgnStatsSection
    {
        internal const string Id = "fgn", Title = "Networking (FGN)";
        internal const int Order = 200;
        internal const float RefreshSeconds = 1f;

        private const int QueueBadBytes = 256 * 1024;
        private const int QueueWarnBytes = 64 * 1024;
        private const int PingBadMs = 300;
        private const int PingWarnMs = 150;

        // state: "normal", "good", "warn", "bad", "muted"
        private static void Add(List<string[]> list, string label, string value, string state = "normal", string tip = null)
            => list.Add(new[] { label, value, state, tip });

        internal static List<string[]> Rows()
        {
            var r = new List<string[]>(12);
            string role = FgnApi.GetRole();
            Add(r, "Role", role, role == "offline" ? "muted" : "normal");
            Add(r, "Peers", FgnApi.GetPeerCount().ToString());
            if (FgnApi.GetTraffic(out float outK, out float inK, out int queue, out int ping))
            {
                Add(r, "Upload", $"{outK:F1} KB/s");
                Add(r, "Download", $"{inK:F1} KB/s");
                Add(r, "Send queue", queue >= 1024 ? $"{queue / 1024f:F0} KB" : $"{queue} B", queue > QueueBadBytes ? "bad" : queue > QueueWarnBytes ? "warn" : "normal",
                    "Bytes waiting in the send queues of all peers. Growing = the link can't keep up.");
                Add(r, "Worst ping", $"{ping} ms", ping > PingBadMs ? "bad" : ping > PingWarnMs ? "warn" : "good");
            }
            else Add(r, "Traffic", "no peers", "muted");
            bool sim = FgnApi.IsSimulationActive();
            Add(r, "Server-side simulation", sim ? "running" : "off", sim ? "good" : "muted");
            Add(r, "Server owns ships", FgnApi.GetSetting("ships.serverOwns") ? "yes" : "no");
            Add(r, "Server owns creatures", FgnApi.GetSetting("creatures.serverOwns") ? "yes" : "no");
            Add(r, "Sync hooks", $"OwnerSync {(ZSyncHookGate.OwnerSyncAttached ? "on" : "off")}, handoff {(ZSyncHookGate.HandoffAttached ? "on" : "off")}", "normal",
                "FGN's per-object ZSyncTransform hooks; off when nothing needs them (saves Harmony dispatch cost).");
            return r;
        }
    }
}
