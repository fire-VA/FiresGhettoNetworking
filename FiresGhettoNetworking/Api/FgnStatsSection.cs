using System;
using System.Collections.Generic;
using System.Reflection;
using FiresGhettoNetworkMod.Api;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// FGN's card on Core's hub Stats tab, order 200.
    /// Found by reflection (Core 0.2.510+); on an older Core or without Core nothing happens. Core calls the rows only while the tab is
    /// visible, every 1 s, on the main thread; they read FgnApi's live state (one pass over the peer list) and a few flags.
    /// </summary>
    internal static class FgnStatsSection
    {
        private const int QueueBadBytes = 256 * 1024;
        private const int QueueWarnBytes = 64 * 1024;
        private const int PingBadMs = 300;
        private const int PingWarnMs = 150;

        private static bool s_registered;
        private static ConstructorInfo s_rowCtor;
        private static Array s_states;   // FiresCore.UI.Shell.StatState values: Normal, Good, Warn, Bad, Muted

        internal static void TryRegister()
        {
            if (s_registered) return;
            try
            {
                Type shell = Type.GetType("FiresCore.UI.Shell.FiresShell, FiresUnifiedCore");
                Type row = Type.GetType("FiresCore.UI.Shell.StatRow, FiresUnifiedCore");
                Type state = Type.GetType("FiresCore.UI.Shell.StatState, FiresUnifiedCore");
                if (shell == null || row == null || state == null) return;   // Core older than 0.2.510, or no Core
                MethodInfo register = shell.GetMethod("RegisterStatsSection", BindingFlags.Public | BindingFlags.Static);
                if (register == null) return;
                s_rowCtor = row.GetConstructor(new[] { typeof(string), typeof(string), state, typeof(string) });
                s_states = Enum.GetValues(state);
                if (s_rowCtor == null) return;
                Type func = typeof(Func<>).MakeGenericType(typeof(IList<>).MakeGenericType(row));
                Delegate rows = Delegate.CreateDelegate(func, typeof(FgnStatsSection).GetMethod(nameof(Rows), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(row));
                register.Invoke(null, new object[] { "fgn", "Networking (FGN)", 200, rows, 1f });
                s_registered = true;
                LoggerOptions.LogInfo("[FGN] stats: registered the hub Stats section 'fgn' (order 200)");
            }
            catch (Exception ex) { LoggerOptions.LogWarning($"[FGN] stats: section not registered: {ex.GetType().Name}: {ex.Message}"); }
        }

        // state index: 0 Normal, 1 Good, 2 Warn, 3 Bad, 4 Muted
        private static void Add<T>(List<T> list, string label, string value, int state = 0, string tip = null)
            => list.Add((T)s_rowCtor.Invoke(new object[] { label, value, s_states.GetValue(state), tip }));

        private static IList<T> Rows<T>()
        {
            var r = new List<T>(12);
            string role = FgnApi.GetRole();
            Add(r, "Role", role, role == "offline" ? 4 : 0);
            Add(r, "Peers", FgnApi.GetPeerCount().ToString());
            if (FgnApi.GetTraffic(out float outK, out float inK, out int queue, out int ping))
            {
                Add(r, "Upload", $"{outK:F1} KB/s");
                Add(r, "Download", $"{inK:F1} KB/s");
                Add(r, "Send queue", queue >= 1024 ? $"{queue / 1024f:F0} KB" : $"{queue} B", queue > QueueBadBytes ? 3 : queue > QueueWarnBytes ? 2 : 0,
                    "Bytes waiting in the send queues of all peers. Growing = the link can't keep up.");
                Add(r, "Worst ping", $"{ping} ms", ping > PingBadMs ? 3 : ping > PingWarnMs ? 2 : 1);
            }
            else Add(r, "Traffic", "no peers", 4);
            Add(r, "Server-side simulation", FgnApi.IsSimulationActive() ? "running" : "off", FgnApi.IsSimulationActive() ? 1 : 4);
            Add(r, "Server owns ships", FgnApi.GetSetting("ships.serverOwns") ? "yes" : "no");
            Add(r, "Server owns creatures", FgnApi.GetSetting("creatures.serverOwns") ? "yes" : "no");
            Add(r, "Sync hooks", $"OwnerSync {(ZSyncHookGate.OwnerSyncAttached ? "on" : "off")}, handoff {(ZSyncHookGate.HandoffAttached ? "on" : "off")}", 0,
                "FGN's per-object ZSyncTransform hooks; off when nothing needs them (saves Harmony dispatch cost).");
            return r;
        }
    }
}
