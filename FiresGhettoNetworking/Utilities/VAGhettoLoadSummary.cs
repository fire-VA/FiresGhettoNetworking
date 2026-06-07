using System;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    // VAGhettoLoadSummary — emoji-headed mini-banners that REPLACE
    // per-item logging with a single visual summary per batch.
    //
    // Ported from FiresAdminPrefabs/Utilities/VAFapLoadSummary.cs.
    // Same shape, different emojis tuned for FGN's domain
    // (networking / RPC / ownership / autotune).
    //
    // Banner format:
    //   ╭── 📡 RPC ROUTER ──╮
    //   │ 9 handlers wired   │
    //   │ AoI radius 256m    │
    //   ╰────────────────────╯
    //
    // All lines tagged [FiresGhettoNetworkMod] [LoadSummary] so the
    // ported FiresLogColorPatch (or FAT's copy) routes them to a
    // distinct color (Cyan per the keyword rules above).
    public static class VAGhettoLoadSummary
    {
        private const string Tag = "[LoadSummary]";
        private const int InnerWidth = 30;

        // 📡 RPC ROUTER — RPC handler registration completion banner.
        // Emitted after all handlers are registered + AoI is configured.
        public static void EmitRpcRouter(int handlersRegistered, float aoiRadius, bool aoiEnabled)
        {
            EmitMiniBox("📡 RPC ROUTER", new[]
            {
                $"{handlersRegistered} handlers wired",
                aoiEnabled ? $"AoI {aoiRadius:F0}m" : "AoI disabled",
            });
        }

        // 🔧 AUTOTUNE — auto-tune init completion. Shows whether the
        // server probe is enabled and the active tier.
        public static void EmitAutoTune(string mode, string tier)
        {
            EmitMiniBox("🔧 AUTOTUNE", new[]
            {
                $"mode: {mode}",
                $"tier: {tier}",
            });
        }

        // 🏛 SERVER AUTHORITY — server-side patch group activation. Lists
        // which sub-systems are active (ownership variant, ZDO throttle,
        // AILOD, WNT optimization).
        public static void EmitServerAuthority(string ownership, bool zdoThrottle, bool aiLod, bool wntOpt)
        {
            EmitMiniBox("🏛 SERVER AUTH", new[]
            {
                $"own: {ownership}",
                $"zdo throttle: {(zdoThrottle ? "ON" : "off")}",
                $"AILOD: {(aiLod ? "ON" : "off")} · WNT: {(wntOpt ? "ON" : "off")}",
            });
        }

        // ⚙ PATCHES — Harmony PatchAll summary for client-mode load.
        // Shows the count of patch classes attached on this side.
        public static void EmitPatches(string side, int classesAttached)
        {
            EmitMiniBox("⚙ PATCHES", new[]
            {
                $"side: {side}",
                $"{classesAttached} patch class(es)",
            });
        }

        // Verbose-mode predicate. Delegates to FiresLogger.VerboseEnabled
        // (which reads ConfigLogLevel == Info). Per-item log sites use
        // this to decide whether to emit details or just the summary.
        public static bool VerboseEnabled => FiresLogger.VerboseEnabled;

        // Generic mini-box renderer. See VAFapLoadSummary.EmitMiniBox
        // for the width-math rationale — same code, same caveats.
        private static void EmitMiniBox(string title, string[] lines)
        {
            try
            {
                int titleChars = title.Length;
                int fillDashes = Math.Max(0, InnerWidth - titleChars - 5);
                string topFrame = "╭─── " + title + " " + new string('─', fillDashes) + "╮";
                string botFrame = "╰" + new string('─', InnerWidth) + "╯";

                Debug.Log($"[FiresGhettoNetworkMod] {Tag} {topFrame}");
                foreach (var line in lines)
                {
                    string body = line ?? string.Empty;
                    if (body.Length > InnerWidth - 2) body = body.Substring(0, InnerWidth - 2);
                    body = body.PadRight(InnerWidth - 2);
                    Debug.Log($"[FiresGhettoNetworkMod] {Tag} │ {body} │");
                }
                Debug.Log($"[FiresGhettoNetworkMod] {Tag} {botFrame}");
            }
            catch (Exception ex)
            {
                try { Debug.Log($"[FiresGhettoNetworkMod] {Tag} (summary render failed: {ex.Message}) {title}: {string.Join(", ", lines ?? new string[0])}"); }
                catch { }
            }
        }
    }
}
