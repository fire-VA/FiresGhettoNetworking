using System;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    // Boxed mini-banners that replace per-item logging with one summary per batch. Tagged
    // [FiresGhettoNetworkMod] [LoadSummary] so FiresLogColorPatch gives them their own colour.
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

        public static bool VerboseEnabled => LoggerOptions.VerboseEnabled;

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

                EmitLine(topFrame);
                foreach (var line in lines)
                {
                    string body = line ?? string.Empty;
                    if (body.Length > InnerWidth - 2) body = body.Substring(0, InnerWidth - 2);
                    body = body.PadRight(InnerWidth - 2);
                    EmitLine($"│ {body} │");
                }
                EmitLine(botFrame);
            }
            catch (Exception ex)
            {
                try { EmitLine($"(summary render failed: {ex.Message}) {title}: {string.Join(", ", lines ?? new string[0])}"); }
                catch { }
            }
        }

        // Routes a summary line through the plugin's BepInEx log source (not
        // Debug.Log) so banner lines print once — Debug.Log would also stdout-echo
        // a raw white console duplicate. The {Tag} stays for FUC colouring.
        private static void EmitLine(string frame)
        {
            if (FiresGhettoNetworkMod.Log != null) FiresGhettoNetworkMod.Log.LogInfo($"{Tag} {frame}");
            else Debug.Log($"[FiresGhettoNetworkMod] {Tag} {frame}");
        }
    }
}
