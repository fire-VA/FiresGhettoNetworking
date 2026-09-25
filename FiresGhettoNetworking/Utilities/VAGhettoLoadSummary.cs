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
        private const char EmojiPresentation = '️';

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

        // 🏛 SERVER AUTHORITY — dedicated-server feature activation. Shows whether
        // the server simulates the world (and its ownership variant), then the
        // traffic features, which run with simulation on or off.
        public static void EmitServerAuthority(bool simulation, string ownership, bool zdoDelta, bool zdoThrottle, bool aiLod, bool wntOpt)
        {
            EmitMiniBox("🏛 SERVER AUTH", new[]
            {
                simulation ? $"sim: ON · own: {ownership}" : "sim: off",
                $"delta: {OnOff(zdoDelta)} · throttle: {OnOff(zdoThrottle)}",
                $"AILOD: {OnOff(aiLod)} · WNT: {OnOff(wntOpt)}",
            });
        }

        private static string OnOff(bool enabled) => enabled ? "ON" : "off";

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
                string boxTitle = WithEmojiPresentation(title);
                int fillDashes = Math.Max(0, InnerWidth - DisplayWidth(boxTitle) - 5);
                string topFrame = "╭─── " + boxTitle + " " + new string('─', fillDashes) + "╮";
                string botFrame = "╰" + new string('─', InnerWidth) + "╯";

                EmitLine(topFrame);
                foreach (var line in lines)
                {
                    string body = line ?? string.Empty;
                    if (body.Length > InnerWidth - 2) body = body.Substring(0, InnerWidth - 2);
                    body += new string(' ', Math.Max(0, InnerWidth - 2 - DisplayWidth(body)));
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

        // Windows Terminal draws a text-style emoji (the classical building) one cell wide and a colour emoji two, so a
        // title's leading emoji gets VS16 and is counted as two. Core's LoadSummary does the same; FGN carries its own
        // copy because it runs without Core.
        private static string WithEmojiPresentation(string title)
        {
            if (title.Length < 2 || !char.IsSurrogatePair(title, 0)) return title;
            if (title.Length > 2 && title[2] == EmojiPresentation) return title;
            return title.Insert(2, EmojiPresentation.ToString());
        }

        // Cells as Windows Terminal draws them: an emoji two, VS16 none, anything else one.
        private static int DisplayWidth(string text)
        {
            int width = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == EmojiPresentation) continue;
                if (char.IsSurrogatePair(text, i)) { width += 2; i++; continue; }
                width++;
            }
            return width;
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
