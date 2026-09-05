using System.Runtime.CompilerServices;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    // FGN's sections for the shared FiresCore help panel. Core is a SOFT
    // dependency — TryRegister checks the Chainloader before touching any
    // FiresCore type, and DoRegister is NoInlining so the JIT never resolves
    // FiresCore when Core is absent.
    internal static class FgnHelpContent
    {
        private const string ModId = "FiresGhettoNetworking";
        private static bool _registered;

        public static void TryRegister()
        {
            if (_registered || Application.isBatchMode) return;
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.Fire.FiresUnifiedCore")) return;
            try { DoRegister(); _registered = true; }
            catch (System.Exception ex) { Debug.LogWarning($"[FiresGhettoNetworkMod] Help registration failed: {ex.Message}"); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DoRegister()
        {
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "What FGN Does", 0, BuildOverview);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Auto-Tune & HYPERBOOST", 1, BuildAutoTune);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Server Tuning", 2, BuildServerTuning, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Stress & Diagnostic Commands", 3, BuildCommands, adminOnly: true);
        }

        private static void BuildOverview(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("What FGN Does");
            w.Paragraph("Fires Ghetto Networking is the family's network engine. You don't operate " +
                "it — you feel it: smoother players, faster world streaming, and no more dropping " +
                "out when a lot happens at once.");
            w.Divider();

            w.SubHeader("Under the Hood");
            w.Bullet("Network compression — packets shrink dramatically before they hit the wire.");
            w.Bullet("Player positions get priority treatment, with optional interpolation and " +
                "prediction smoothing other players' movement.");
            w.Bullet("Bigger send/receive buffers and a per-player adaptive send rate that ramps " +
                "each connection toward its real capacity.");
            w.Bullet("Login-flood protection — the classic 'everyone teleporting/flying after a " +
                "big sync' disconnects are gone: bulk-transfer gates are raised and the 30-second " +
                "self-disconnect is disarmed during bursts.");
            w.Bullet("Megabase mercy — arriving at (and leaving) huge builds streams objects on a " +
                "frame budget instead of freezing.");
            w.Bullet("Universal ship fixes — ships keep simulating and stay put when unmanned.");
        }

        private static void BuildAutoTune(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("Auto-Tune & HYPERBOOST");
            w.SubHeader("Auto-Tune");
            w.Paragraph("On your first login FGN quietly probes your connection — a few pings and " +
                "a bandwidth test — and picks a LOW / MED / HIGH performance tier for you. Nothing " +
                "in your config is overwritten; the tier shadows it. A rolling monitor keeps " +
                "re-checking and adjusts if your connection changes.");
            w.Bullet("06 - Auto-Tune / Enable Client Auto-Tune — the toggle (on by default)");
            w.Bullet("Retune On Every Login — ignore the cached tier and re-probe each join");
            w.Bullet("Dedicated servers self-tune too, scoring their own CPU/RAM.");
            w.Divider();

            w.SubHeader("HYPERBOOST");
            w.Paragraph("05 - Networking - Steamworks / HYPERBOOST — the manual override: maximum " +
                "throughput, buffers and rates unlocked, Auto-Tune bypassed. Applies live. Use it " +
                "on strong connections; leave it off if your link is modest.");
        }

        private static void BuildServerTuning(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Server Tuning");
            w.Paragraph("The sections that matter most on a dedicated server (all in F8):");
            w.AdminDivider();

            w.SubHeader("09 - Dedicated Server");
            w.Bullet("Player Limit — the real cap; Advertised Player Limit — what matchmaking shows");
            w.Bullet("Force Crossplay — vanilla / playfab / steamworks backend (restart)");
            w.AdminDivider();

            w.SubHeader("10 - Server Authority");
            w.Bullet("Enable Server-Side Simulation — the MASTER switch for the authority stack");
            w.Bullet("RPC Router + Area-of-Interest — filters exploit RPCs and stops broadcasting " +
                "target-ZDO RPCs to players hundreds of meters away");
            w.Bullet("ZDO Throttling + AI LOD — distant objects and AI update less often");
            w.Bullet("Predictive Zone Streaming — pre-loads zones ahead of moving players");
            w.Bullet("Ownership Transfer (both variants) — EXPERIMENTAL; leave off unless testing");
            w.AdminDivider();

            w.SubHeader("Traffic");
            w.Bullet("04 - Networking: ZDO Send Rate, Queue Size, Bulk Transfer Queue Boost + " +
                "Budget Percent — the login-flood protections");
            w.Bullet("02 - Client Performance: Max Destroys Per Frame (megabase exit), " +
                "time-sliced instantiation (megabase entry)");
            w.Bullet("11 - Ship Fixes: universal fixes + optional server-side ship simulation");
            w.AdminDivider();

            w.Paragraph("Note: '12 - Advanced / Max Active ZDOs' is bound twice in code with " +
                "different defaults — the effective default is 500000 (the early bind wins). " +
                "Diagnostics live under '10 - Diagnostics' and '01 - General' (the [ServerStatus] " +
                "rollup line cadence).");
        }

        private static void BuildCommands(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Stress & Diagnostic Commands");
            w.Paragraph("All admin-gated server-side; run them from a client console to test YOUR " +
                "connection against the server.");
            w.Bullet("fgn_headroom — snapshot every peer's send-queue vs its ceiling");
            w.Bullet("fgn_comptest [count] [sizeKB] — round-trip compression verification burst");
            w.Bullet("fgn_flood [count] [sizeKB] [raw|comp] — packet flood; reports throughput + loss");
            w.Bullet("fgn_socketramp [startGB] [stepGB] [maxGB] — escalate until the link breaks; " +
                "results land in FiresGhetto_StressResults.txt");
            w.Bullet("fgn_zdoflood [count] [prefab] — mass-spawn test for the megabase-teleport " +
                "path; reports your worst frame hitch, then cleans up");
            w.Bullet("fgn_overload [seconds] [arm] — prove the 20KB-gate disconnect disarm holds " +
                "('arm' restores the vanilla 30s timeout as a control)");
        }
    }
}
