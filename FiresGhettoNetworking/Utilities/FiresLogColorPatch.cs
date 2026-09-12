using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    // Colours this mod's console lines by their [ClassName] sub-tag. Fires* mods share one AppDomain-wide
    // ownership key: the first colour patch to fire claims it and the rest pass through, so no line is
    // printed twice.
    [HarmonyPatch]
    internal static class FiresLogColorPatch
    {
        // Default color for our mod tag when no class-keyword matches.
        // Magenta to match FAT — keeps a consistent visual identity
        // across the Fires* mod family.
        private const ConsoleColor DefaultColor = ConsoleColor.Magenta;

        // First-match-wins keyword → color rules. Substring match against
        // the [ClassName] inside the second pair of brackets, case-
        // insensitive. Tuned for FGN's class names — networking, ZDO,
        // RPC, AutoTune, ownership are the dominant log sources.
        private static readonly (string keyword, ConsoleColor color)[] s_classColorRules =
        {
            // RPC / routing / network transport
            ("RpcRouter",      ConsoleColor.Blue),
            ("RpcHandler",     ConsoleColor.Blue),
            ("Rpc",            ConsoleColor.Blue),
            ("Network",        ConsoleColor.Blue),
            ("Compression",    ConsoleColor.Blue),

            // ZDO / data layer
            ("ZDO",            ConsoleColor.DarkMagenta),
            ("BigZdo",         ConsoleColor.DarkMagenta),
            ("Delta",          ConsoleColor.DarkMagenta),
            ("Memory",         ConsoleColor.DarkMagenta),

            // Server-side authority / ownership
            ("ServerOwnership", ConsoleColor.DarkGreen),
            ("ServerAuthority", ConsoleColor.DarkGreen),
            ("ServerStability", ConsoleColor.DarkGreen),
            ("Server",         ConsoleColor.DarkGreen),

            // AutoTune / probe / tier
            ("AutoTune",       ConsoleColor.DarkYellow),
            ("Tune",           ConsoleColor.DarkYellow),
            ("Probe",          ConsoleColor.DarkYellow),
            ("Tier",           ConsoleColor.DarkYellow),

            // Zone / instantiation / throttling
            ("ZoneLoad",       ConsoleColor.DarkCyan),
            ("Throttle",       ConsoleColor.DarkCyan),
            ("Cleanup",        ConsoleColor.DarkCyan),
            ("AILOD",          ConsoleColor.DarkCyan),
            ("MonsterAi",      ConsoleColor.DarkCyan),
            ("Zone",           ConsoleColor.DarkCyan),

            // Bulk transfer / queue gate
            ("BulkTransfer",   ConsoleColor.Yellow),
            ("Queue",          ConsoleColor.Yellow),

            // Ship / vehicle
            ("Ship",           ConsoleColor.Cyan),
            ("Vehicle",        ConsoleColor.Cyan),

            // WearNTear classifier + support skip
            ("WearNTear",      ConsoleColor.DarkRed),
            ("Wnt",            ConsoleColor.DarkRed),

            // Diagnostics + heartbeat
            ("Diagnostic",     ConsoleColor.DarkGray),
            ("Heartbeat",      ConsoleColor.DarkGray),
            ("Status",         ConsoleColor.DarkGray),
            ("PatchVerify",    ConsoleColor.DarkGray),

            // Load summary (mini-banner emit path)
            ("LoadSummary",    ConsoleColor.Cyan),
            ("Banner",         ConsoleColor.Cyan),
        };

        // Mod tags we own. Substring match — covers "[FiresGhettoNetworkMod]"
        // / "[FiresGhetto:Sub]" etc. Same family list as FAT so we recognize
        // every Fires* mod that might be in the process.
        private static readonly string[] s_ourModTags =
        {
            "[FiresGhettoNetworkMod",
            "[FiresGhetto",
            "[FiresAdminTerrain",
            "[FiresAdminPrefabs",
            "[FiresNPCs",
            "[VerdantsAscent",
        };

        // Tolerant of any [Fires*] / [VerdantsAscent*] prefix. Captures
        // the second [bracketed] group as the class name.
        private static readonly Regex s_classNameRx = new Regex(
            @"^\s*\[(?:Fires[A-Za-z0-9_:]*|VerdantsAscent[A-Za-z0-9_:]*)\]\s*\[([^\]]+)\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Reflection cache — BepInEx.ConsoleManager is `internal` so
        // direct access fails CS0122. Resolved once on first call.

        // Cross-mod ownership coordination. Identical pattern to FAT's
        // copy of FiresLogColorPatch — first Fires* color patch whose
        // prefix fires claims ownership of the AppDomain-shared key,
        // subsequent prefixes from other mods see a different owner
        // and bail.
        //
        // Replaces the previous FAT-specific assembly-detection check
        // which only handled one specific peer. The owner-key approach
        // handles ANY combination of Fires* mods (FAT / FAP / FGN /
        // future ports) symmetrically.
        private const string OwnerKey = "FiresColorPatch.Owner";
        private const string MyOwnerName = "FiresGhettoNetworkMod";


        [HarmonyPatch(typeof(ConsoleLogListener), nameof(ConsoleLogListener.LogEvent))]
        [HarmonyPrefix]
        private static bool LogEvent_Prefix(LogEventArgs eventArgs)
        {
            try
            {
                if (eventArgs == null) return true;
                string message = eventArgs.Data?.ToString();
                if (string.IsNullOrEmpty(message)) return true;

                // Cross-mod ownership claim — first Fires* color patch to
                // fire claims ownership. If another mod (FAT / FAP / future)
                // already claimed, we bail (the owner is processing).
                var owner = AppDomain.CurrentDomain.GetData(OwnerKey) as string;
                if (owner == null)
                {
                    AppDomain.CurrentDomain.SetData(OwnerKey, MyOwnerName);
                    owner = MyOwnerName;
                }
                if (owner != MyOwnerName) return true;

                bool isOurs = false;
                for (int i = 0; i < s_ourModTags.Length; i++)
                {
                    if (message.IndexOf(s_ourModTags[i], StringComparison.Ordinal) >= 0)
                    {
                        isOurs = true;
                        break;
                    }
                }
                if (!isOurs) return true;

                ConsoleColor color = DefaultColor;
                var match = s_classNameRx.Match(message);
                if (match.Success)
                {
                    string className = match.Groups[1].Value;
                    for (int i = 0; i < s_classColorRules.Length; i++)
                    {
                        if (className.IndexOf(s_classColorRules[i].keyword,
                                              StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            color = s_classColorRules[i].color;
                            break;
                        }
                    }
                }

                // Errors and warnings keep their vanilla red/yellow.
                // Fully-qualified because FGN has its own `LogLevel` enum
                // (in the FiresGhettoNetworkMod namespace) that shadows
                // BepInEx.Logging.LogLevel via the `using` directive.
                if (eventArgs.Level == BepInEx.Logging.LogLevel.Error
                    || eventArgs.Level == BepInEx.Logging.LogLevel.Fatal
                    || eventArgs.Level == BepInEx.Logging.LogLevel.Warning)
                {
                    return true;
                }

                var stream = BepInExConsole.Stream;
                if (stream == null) return true;

                try
                {
                    BepInExConsole.SetColor(color);
                    stream.Write(eventArgs.ToStringLine());
                }
                finally
                {
                    BepInExConsole.SetColor(ConsoleColor.Gray);
                }
                return false;
            }
            catch
            {
                return true;
            }
        }

        // Manual install hook — call from Awake AFTER ResolveFatPresence
        // is safe to run (i.e. after all plugins have had a chance to
        // load). We auto-install via [HarmonyPatch] attribute at PatchAll
        // time, which is fine — the runtime FAT-detection in the prefix
        // gates execution per-call.
        public static void Install(Harmony harmony)
        {
            try { harmony.PatchAll(typeof(FiresLogColorPatch)); }
            catch (Exception ex)
            {
                // Color patch failure is non-fatal — logs still emit
                // through the vanilla path, just uncolored.
                UnityEngine.Debug.LogWarning($"[FiresGhettoNetworkMod] FiresLogColorPatch install failed: {ex.Message}");
            }
        }
    }
}
