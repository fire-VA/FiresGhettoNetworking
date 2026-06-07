using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    // FiresLogColorPatch — ports FiresAdminTerrain's per-class console-
    // color override into FiresGhettoNetworkMod. Intercepts
    // BepInEx.Logging.ConsoleLogListener.LogEvent and writes our mod's
    // log lines in distinct console colors based on the [ClassName]
    // sub-tag in the message.
    //
    // Why ported (not source-linked):
    //   - FAT (FiresAdminTerrain) installs the same patch. If both mods
    //     are loaded, both prefixes would fire on every matching log
    //     line and double-print.
    //   - To avoid double-printing without modifying FAT, this copy
    //     uses a RUNTIME guard: if FAT's color-patch assembly is
    //     loaded, our prefix bails (return true → pass through to
    //     vanilla → FAT's prefix already handled it).
    //   - The check is memoized on first call so the per-line overhead
    //     is one volatile-bool read.
    //
    // Coexistence matrix:
    //   FAT loaded + FGN loaded → FAT handles all logs, FGN bails
    //   FAT NOT loaded + FGN loaded → FGN handles all logs (Fires* tags)
    //   FAT loaded + FGN NOT loaded → FAT handles all logs (current behavior)
    //
    // Adding a new category:
    //   Same as FAT — add a (keyword, color) entry to s_classColorRules
    //   in first-match-wins order.
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
        private static bool s_reflectionResolved;
        private static Func<object> s_consoleStreamGetter;
        private static Action<ConsoleColor> s_setConsoleColor;

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

        private static void EnsureReflection()
        {
            if (s_reflectionResolved) return;
            s_reflectionResolved = true;
            try
            {
                var asm = typeof(BepInEx.Logging.ConsoleLogListener).Assembly;
                var consoleManagerType = asm.GetType("BepInEx.ConsoleManager", throwOnError: false);
                if (consoleManagerType == null) return;

                var streamProp = consoleManagerType.GetProperty("ConsoleStream",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (streamProp != null)
                {
                    var getMethod = streamProp.GetGetMethod(nonPublic: true);
                    if (getMethod != null)
                    {
                        s_consoleStreamGetter = (Func<object>)Delegate.CreateDelegate(
                            typeof(Func<object>), getMethod);
                    }
                }

                var setColorMethod = consoleManagerType.GetMethod("SetConsoleColor",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(ConsoleColor) },
                    modifiers: null);
                if (setColorMethod != null)
                {
                    s_setConsoleColor = (Action<ConsoleColor>)Delegate.CreateDelegate(
                        typeof(Action<ConsoleColor>), setColorMethod);
                }
            }
            catch { /* leave delegates null — caller falls back to vanilla */ }
        }

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

                EnsureReflection();
                if (s_consoleStreamGetter == null || s_setConsoleColor == null)
                    return true;

                var stream = s_consoleStreamGetter() as TextWriter;
                if (stream == null) return true;

                try
                {
                    s_setConsoleColor(color);
                    stream.Write(eventArgs.ToStringLine());
                }
                finally
                {
                    s_setConsoleColor(ConsoleColor.Gray);
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
