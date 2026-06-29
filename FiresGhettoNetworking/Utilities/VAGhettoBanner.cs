using System;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    // VAGhettoBanner — ported from FAP's VAFapBanner. Emits two banners
    // at different load phases:
    //
    //   PrintBig()  → BIG ASCII radio tower with sparks. Fires at the
    //                 very start of Awake as the load announcement.
    //                 Multi-color per-line for electric / metal /
    //                 signal effect.
    //
    //   Print()     → Compact antenna + signal-strength bar. Fires at
    //                 the end of load (after RPC + autotune init done)
    //                 as the "ready" confirmation.
    //
    // Both use direct console writes (via BepInEx.ConsoleManager
    // reflection) instead of Debug.Log so each line gets its own
    // color. Plain fallback path emits via Debug.Log if reflection
    // fails — the shape renders, just uncolored.
    //
    // Color philosophy for the radio tower:
    //   The art depicts a radio tower with sparks shooting off the
    //   top. Color zones map to physical regions:
    //     - Sky / upper sparks: Yellow / White / Cyan (electric arcs)
    //     - Antenna tip:        Yellow (hot spark point)
    //     - Tower steel:        DarkGray / Gray alternating (shimmer)
    //     - Signal / wiring:    DarkCyan / Cyan (networking theme)
    //     - Equipment panels:   Cyan / DarkCyan
    //     - Base / grounding:   DarkGray
    //   Lines vary per-row so no two consecutive lines share a color
    //   in the spark zone — gives the banner a flickery, alive feel.
    public static class VAGhettoBanner
    {
        private readonly struct Segment
        {
            public readonly string Text;
            public readonly ConsoleColor Color;
            public Segment(string text, ConsoleColor color) { Text = text; Color = color; }
        }

        // Reflection cache for direct console writes.
        private static bool s_reflectionResolved;
        private static Func<object> s_consoleStreamGetter;
        private static Action<ConsoleColor> s_setConsoleColor;

        // Color aliases used by the compact banner.
        private const ConsoleColor Spark   = ConsoleColor.Yellow;
        private const ConsoleColor SparkHi = ConsoleColor.White;
        private const ConsoleColor Tower   = ConsoleColor.DarkGray;
        private const ConsoleColor TowerHi = ConsoleColor.Gray;
        private const ConsoleColor Signal  = ConsoleColor.Cyan;
        private const ConsoleColor SignalDim = ConsoleColor.DarkCyan;
        private const ConsoleColor Brand   = ConsoleColor.Cyan;
        private const ConsoleColor Tagline = ConsoleColor.Magenta;
        private const ConsoleColor Bar     = ConsoleColor.Green;       // signal-strength bar (filled)
        private const ConsoleColor BarMid  = ConsoleColor.DarkYellow;  // signal-strength bar (cooler tail)
        private const ConsoleColor BarEmpty = ConsoleColor.DarkGray;   // signal-strength bar (empty)

        // ─────────────────────────────────────────────────────────────
        //  COMPACT banner — antenna with signal-strength bar.
        // ─────────────────────────────────────────────────────────────
        // 5-line antenna (single tip → 3 sparks → narrowing → tower
        // body) above a 24-wide framed signal-strength bar with the
        // last 4 segments fading (Green → DarkYellow → DarkGray) to
        // imply "signal locked but still scanning."
        private static readonly Segment[][] s_compactLines =
        {
            // Spark crown
            new[] { new Segment("              ⚡",            SparkHi) },
            new[] { new Segment("           ⚡ ⚡ ⚡",          Spark) },
            new[] { new Segment("            ╲│╱",             TowerHi) },
            new[] { new Segment("             │",              Tower) },
            new[] { new Segment("             │",              Tower) },

            // Frame top — 31-char total line width (3 lead + 1 corner +
            // 26 horizontal dashes + 1 corner). Body lines below also
            // size to 31 chars so the right `║` aligns with `╗` / `╝`.
            new[] { new Segment("   ╔══════════════════════════╗", Tower) },

            // Signal-strength bar (frame DarkGray, bar segments
            // green → yellow → dim for fade-out effect). Width
            // accounting per segment:
            //   `   ║   ` (7) + `[` (1) + 18 bar cells + `]` (1)
            //   + `   ║` (4) = 31 ✓
            // Bar split: 13 bright green / 3 yellow tail / 2 empty.
            new[]
            {
                new Segment("   ║   ",         Tower),
                new Segment("[",               TowerHi),
                new Segment("█████████████",   Bar),
                new Segment("███",             BarMid),
                new Segment("░░",              BarEmpty),
                new Segment("]",               TowerHi),
                new Segment("   ║",            Tower),
            },

            // Status line. Width accounting:
            //   `   ║    ` (8) + "📡 SIGNAL LOCKED" (16 chars .Length —
            //   📡 surrogate pair contributes 2, " SIGNAL LOCKED" = 14)
            //   + `      ║` (7) = 31 ✓
            //
            // Width math caveat (same as VAFapLoadSummary): this
            // assumes 📡 renders at 2 visual cells (it's Unicode East
            // Asian Width "Wide" so this holds in any modern emoji-
            // aware terminal). If a terminal renders it at 1 cell,
            // this line ends up 1 cell short — acceptable for the
            // segmented compact banner.
            new[]
            {
                new Segment("   ║    ",        Tower),
                new Segment("📡 SIGNAL LOCKED", Signal),
                new Segment("      ║",         Tower),
            },

            // Frame bottom
            new[] { new Segment("   ╚══════════════════════════╝", Tower) },

            // Brand + tagline
            new[] { new Segment("    GHETTO NETWORKING",       Brand) },
            new Segment[0],
            new[] { new Segment("   Fires Ghetto Networking Loaded", Tagline) },
        };

        // ─────────────────────────────────────────────────────────────
        //  Public entry points
        // ─────────────────────────────────────────────────────────────

        // Routes a one-line banner header through the plugin's BepInEx log source
        // (not Debug.Log) so it prints once instead of colour+white. FUC colours it
        // by the Fires source name.
        private static void EmitHeader(string text)
        {
            if (FiresGhettoNetworkMod.Log != null) FiresGhettoNetworkMod.Log.LogInfo(text);
            else Debug.Log($"[FiresGhettoNetworkMod] {text}");
        }

        public static void Print()
        {
            try { EmitHeader("Fires Ghetto Networking Loaded."); }
            catch { }

            try
            {
                if (!TryWriteSegmented(s_compactLines))
                    WritePlainFallbackSegmented(s_compactLines);
            }
            catch
            {
                try { WritePlainFallbackSegmented(s_compactLines); } catch { }
            }
        }

        public static void PrintBig()
        {
            try { EmitHeader("Fires Ghetto Networking Loading..."); }
            catch { }

            try
            {
                if (!TryWriteColoredBig())
                    WritePlainFallbackBig();
            }
            catch
            {
                try { WritePlainFallbackBig(); } catch { }
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  BIG banner — radio tower with sparks
        // ─────────────────────────────────────────────────────────────
        // Per-line single color since the ASCII shading carries the
        // visual depth. Color zones per the class header:
        //   - Sky / upper sparks (lines 1-10): Yellow / Cyan / White
        //   - Upper tower with sparks (11-26): DarkGray + spark accents
        //   - Mid tower body (27-42):          DarkGray / Gray shimmer
        //   - Lower equipment (43-60):         DarkCyan / Cyan
        //   - Base (61-65):                    DarkGray
        //   - Brand/text section (66+):        Cyan / Magenta
        private static readonly (string text, ConsoleColor color)[] s_bigLines = new (string, ConsoleColor)[]
        {
            ("", ConsoleColor.Gray),

            // ── Sky / upper sparks (cloud-like with electric arcs)
            ("%%%%%%%%%@@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@%%%%%%%%%%%%%%%%%%@", ConsoleColor.Cyan),
            ("%%%%%%@%%%%%%%%%%%%%%%@@@%%%%%@@@%@%%%%%%%%%%%%%%%%@%%%%%%%%%%%@@@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@", ConsoleColor.DarkCyan),
            ("%%%%%%%%%%%%%%%%%%%%%%@@@%%%%%@%%@@%@%%%%%%%%%%@%%%@%%%%%%@%%@@@%%%%%@@@%%%%%%%%%%%%%%%%%%%@@%%%@@",   ConsoleColor.Cyan),
            ("%%%%%%%%%@%%%%%%@%%%%@@@%%@%@%%%%%%%%%%%@%%%%%%%%@%%%%%%%%%%%@@@%%%%%%%%%%%%%%%%%%%%%%%@%%%%%@%%%%%%", ConsoleColor.DarkCyan),
            ("%%%%%%%@%%%%%%%%%%%%%%@@%%%%@%%%%%@@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@%%%%@%%%%%%%%%%@@%%", ConsoleColor.Blue),
            ("%%%%%%%%%%%%%%%%%%%%%%@@%%@@%%%@%@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@%%%%%%%%%%%%%%@@%%%%%%%%%@@@@%%%@%%", ConsoleColor.DarkBlue),
            ("@%%%%%%%%%%%%%%%%%%%%%%%%%%@%%%%%%%%%%%%%%@%%%%%%%%%%%%%%%@%@%%%%%%%%%%%%%%%%%%%%%%%%%@%%%%@@@%%%%%%", ConsoleColor.DarkCyan),
            ("@@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@@@%%%%%%%@%%%%%%%%%%%%%%%%%%%%%%%%%%%@@@%%%%%%%%%%%%%%%%%%%%%%%%%@@@", ConsoleColor.Cyan),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@%%%%%%%%%%%%%%%%%%%%%@%%%%%@@@", ConsoleColor.Yellow),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@@@%%%@", ConsoleColor.White),

            // ── Upper tower with sparks (the @@@ clusters and == patterns
            //     are the antenna-tip sparks and electric arcs)
            ("%%%%@@%%%%%%%%%%%@@%%%%%%%%%%%%%%%%%@%%%%%%%@%%%%%%%%%%%%%@%%%%%%%%%%%%%%%%%%%%%%%%@@@%@@@@%@@%%%@%@", ConsoleColor.DarkGray),
            ("%@%@@@@@%@@%%%%%%%%%*@@%%%%%%%%%%%@@%%%%%%@@@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%*%%%@@@%%@@%@@%%%%%%%%", ConsoleColor.Gray),
            ("%%%%%@@@@@@@@%%%%@%%%*#%%%%%%%%%%%%=*@%%%@@@@%%%%%%%%%%%%%%%%%*=%@%%%%%%%%%%**%@%@@@@%%@%%%%%%%%%%%%", ConsoleColor.DarkGray),
            ("%%%%%%@@@%%%%%%%%%%%@@#-%@%%%@%%%@@%+#%%%%@@%%%%%%%%%@@@%%%%%#+%%%%%%%%%%@#-%@%%%%%%%%%%%%%%%%%%%@%%", ConsoleColor.Yellow),
            ("@@@%@@%%%%%%%%%%%%%%@@@@-=%@%%%%%%%%%*%%@@%%%%%@%-%%%@@@%@%%%#@%%%%%%%%@%--%%@%%%%@%%%%%%%%%@@%%%@%%", ConsoleColor.DarkYellow),
            ("%@@%@@@%%%%%%%%%%%%%%@@@@+.+%%%%%%%%%%%%%%%%%%%%%=%@%%%%@@%%%%%%%%%%%%%+.+%%%%%%%@%%%%%%%%%%%%%%%%%%", ConsoleColor.Yellow),
            ("%%%@@@%%%%%%%%%%%%%%@%%%%%*.:#@%%%%%%%%%%%%%%%%%%=%@%%%%%%%%%%%%%%%@@*.:#%@%%%%%%%@@@%@%@%%%%%%%%%%%", ConsoleColor.White),
            ("%@@@%%%%%%%%%%%@%@@%%%%%%%%%:.-%@%==%%%%%%%%%%%%%=%%%%%@%%%%%@%-=%%#:.-%%%@%%%%%%%%%%%%%@@%%%%%%%@%@", ConsoleColor.Cyan),
            ("%@@@@@%@@%%%%%%%@@@%%%%@@%-:=-..=*..-%%@%##@%%%%%=%%%%%%#%%%%%-..#=..=-.-%@@%%%%@%%@@%%%%%%%%%@@%%%%", ConsoleColor.DarkCyan),
            ("%@@@%%%@%%%@@@%@@@@%%%%%@@@*.........-%%%%#%@%%%%=@%%%@%%@%%%:.........#@@@@%%%%%%%%%%@@@%%%%@@@%%%%", ConsoleColor.DarkGray),
            ("%%%%%%%%%%%@%%@@@%%%%%@%%@%%%=........:%%%%%%@%%@*@%%%%%@@@#:........=%%@%%%%%%%%%%%%@@@@%%%%%%%%%%%", ConsoleColor.Gray),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%@@%:...*=..:#@%%%%%%%#@%%%%%%%#:..+*...:%%@@%%%%%%%%%%%%%%@@@%%%%@%%%%%@", ConsoleColor.DarkGray),
            ("%%%%%%@%%%%%%@%%%%%%%%%%@@@@@@@@+.-%%%+..#%%%%%%%%@%%%@@%*.:+%%%-.*%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", ConsoleColor.Gray),
            ("%%%%%%%%%%%%%%%%%%%%%@@%%%@%%@@@@%#@%%%%*:*%%%%%%%%%%@@@*:*%%%%@#%%%%%%%%%%@%%%%%%%%@@%%%%%%%%%%%%%%", ConsoleColor.DarkCyan),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%@@@@@%%%%%%%@@**%%%%%%%%%%%*#%@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", ConsoleColor.Cyan),

            // ── Mid tower body (the gear/equipment section with =-:=
            //     symbols representing the radio equipment + wiring)
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@%%%@%=-::::=%%%@%%%%@%%%%%%%%%%%%%%%%%%%%%@%%@@@%%%%@@%%%%%", ConsoleColor.DarkGray),
            ("@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@%%%%%%%%%%%%-:-%%%%%-:=%%%%%%@@%%%%@%%%%%%%%%%@%%%%@@@%@%%%%%@%%%%%%", ConsoleColor.Gray),
            ("%%%%%%%%%%%%%%%@%%%%%%%%@%%%%%%#*-=%%%%%%%%%:-%%+++%%-:%%%%%%%%%=-*#%%@%%%%%%%%%%%%%%%@%%%%%%@%%%%%%", ConsoleColor.DarkGray),
            ("%%%%%%%@@@@%%%%%%%%%%%%%@%#=:....-%%%%%@@%%%:=%%...%%=:%@%%%%%%%%-....:=#%%%%%%%%%%%%%%%%%%%%%%%%%%%", ConsoleColor.Cyan),
            ("%%%%%%%%%%%%%%%%%%@%#+:..........:::..:+#%%%:=%#:::%%=:%%%#+:..:::.........:-*#%%%%%%%%%%%%@%%%%%%%%", ConsoleColor.DarkCyan),
            ("%%%%%%%%%%%%%%%%%#####%%%%%%*.....-+%%%%%%%%:=%#:::%%=:%%%%@@%%+-.....*%%%%%%#####%%%%%%%%%@@@@@%%%%", ConsoleColor.Gray),
            ("@@@%%%%%%@@@%%%%%%%%%%%%%%@#..-*%@%@%%%%%%%%:=%#.:.%%=:%%%%%%%%%%%%*-..#%%%%%@@%%%@%%%%%%%%%%%%@%@%%", ConsoleColor.DarkGray),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%@#%@@%@%%@@@@%=---:=%#:..%%=:---=%@%%%%%%%%@%#%%%%%%%%%%%%%%%%%%%%%%%%%%%%", ConsoleColor.DarkCyan),
            ("%%%%%%%%%%%@@%%%%@@@@@%%%%%%%%%%%%%%%%@=:#%%%%%%%%%%%%%%%#:+%%%%%%%%%%%%%@@@%%%%@%%%%%%%%%%%%%%%%%%%", ConsoleColor.Cyan),
            ("%%%%%%%%%%%%%%%@%%%%%@%%%@@%@%%@%%%%%%%=:#%%***********%%#:+%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", ConsoleColor.DarkCyan),
            ("@@@@@@%%%%%@@%%@%%@@%%%%%@@%=:%@%%%#-#%=:#%%####%%%%###%%#:+%#-#@@%%%:=%%%%%@@%%%%%%%%%%%%%%%%%%%%%@", ConsoleColor.Gray),
            ("@@%%@@%%%%@@%%@@%%%%%%%%%@*..:%@#-.=%%%=:*##%%%#####%%%##*:+%%%-.-#@%..:#%%%%%%%%%%%%%%%%%%%%%%%%%%%", ConsoleColor.DarkGray),
            ("%%%%%%%%%%%%%%%%%@@@@@%@%-....=...*%%%+-::::%%+:::::*%#::::-+@%%*...=....-%@%%%%%%%%%%%%%%%%%%@@%%%%", ConsoleColor.DarkCyan),
            ("@%%%%%%%%%%%%%%%%%@@@@%+........-%%%%*:*%%%%%%%%%%%%%%%%%%%+:*%@%%-........+@@%%%%%%%%%%@%%%%%@@%%%%", ConsoleColor.Cyan),
            ("%%%%%%%%%%%%%%%%%%%%@#:...:....*@@%%%*:*%%***************%%+:*%%%%%*....:...:#@%%@%%%%%%@%%%%%%%%%%%", ConsoleColor.Gray),
            ("%%%%%%%@%%%%%%%%%@%%-..:#%=..-%%%%%%%*:*%%***************%%+.*%%%%%@%-..+%*:..-%%%%%%%%%%%%%@%%%%%%%", ConsoleColor.DarkGray),

            // ── Lower equipment / control panel section
            ("%@%%%%%@@@%%%@%%%@+..=%@@%=.*%%%%%%%%*:*%%%%%%%%%%%%%%%%%%%+:*%%%@@%@%*.+@%@%=..*@%%%%%%@%@@@@%%%%%%", ConsoleColor.DarkCyan),
            ("@@%%%%%@@@%%%%@@%::#%@%%%@*%%%%%%%%%%%-:::%%=.........=%%:::=%%%%@%@@@@%*@%%%%%#:-%@%%%%%%%@%%%@%%%@", ConsoleColor.Cyan),
            ("@@@%%%%%%%%%%@%==%@@@%%%%%@@%%%%%%%%%%%+:=%%%*..=%-.:*%%%-:*@@@@@%%%%@@@@@@@%%%@@%++%%%%%%%%%%@@%%%%", ConsoleColor.DarkCyan),
            ("@@@%%%%@@%%%@##%@@@%%%%%%%%%%%%%%%@%%%%::%%=*%%%=.=%%%*=%%::@@@%%%@@%%%%@@@%%%%%%@@@##%%%%%%%%%%%%%%", ConsoleColor.Gray),
            ("%@%%%%%%@%@%%%%%@@%%%%%%%%%%@@@%%@@%%%+:=%%...:%%%%%:...%%=:+%%%%@@@%%%@@%%%%%%%%@%%%%%%%%%%%%%%%%@%", ConsoleColor.DarkGray),
            ("%%%%%%%%@@@@@%%%%@%%%%%%%@@%%%%@%%%@%%::%%=..=%%%+%%%=..=%%::%%%%%%@%@@@@%@@%%@@%%%%%%%%%%%%%%%%%%%@", ConsoleColor.DarkCyan),
            ("%@%%%%%%%%@@%%@@%%@%%%%%%%%%%%@@%%%%%=:+%#.%%%#..:..%%%%.%%=:=%%%%%@@@@%%%@@@@%%%%%%@%%%%%%%%%%%%%%@", ConsoleColor.Cyan),
            ("@@%%%%%%%%%%%@%@%@@@%%%%%%%%%@@@@%%%%:.%%%%%=.:*%%%+:.+%%%%%::%@%%%%%%%%@@%@%%@@%%%%%%%%%%%%%%%%%%%@", ConsoleColor.DarkCyan),
            ("@%%%%%%%%%%%%%@%%%@%%%%%%%%@@@%%%%%%-:+%%%#..-%%%%%%%-..%%%%+:-%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", ConsoleColor.Cyan),
            ("%%%%%%%%%%%%%%%@@@%%%%%%%%%%%%%%%%@%:.%%=+%%%=.:#%*..=%%%+=%%.:%%%%%@%%%%%%%%%%%%@@@@%%%%%%%%%%%%%%@", ConsoleColor.DarkCyan),
            ("%%@@%%%@@%@%@%%%@%%@@%%%%%%%%%%%%%%-:*@#.:.:%%%#:.:#%%#:.:.#%*:-%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@@", ConsoleColor.Gray),
            ("@%%%%@@@%%%%%%%%%%%%%%%%%%%%%%%%%%#:.%%-.%%+..+%%%%%=..*%%.-%%.:#%%%@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@", ConsoleColor.DarkGray),
            ("@%%%%%@@%%%%%%@@%%%%%%%%%%%%%%%%%%:.#%#.+%#-.:#%%%%%#:.-%%=.#%*.:%%%%%%%%%%%%%@@%@@@%%%%%%%%@@%%%%%%", ConsoleColor.DarkCyan),
            ("%%%%%%%%%%%%%%%%%%%%@%%%%%@@@@%%%*:.%%::+..+%%%=.:.=%%%+..+:-%%.:*%%%%%%%@%%%%%%%@@@@%%%@%%%%@%%%%%%", ConsoleColor.Cyan),
            ("%%%%%%@%%%%%%%%%%%%%%%%%%%%%@%%%%:.*%*..:#%%#:.:*@*:.:#%%#:..*%#.:%@%%%%%%%%@%%%%%@@%%%%%%%%%%@%%@@@", ConsoleColor.DarkCyan),
            ("%%%%%%%%%%%%%%%%@%%%%%%%%%%%%@%%+::%%.+%%%=.:=%%@@%%%=:.=%%%=:%%::+%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@@", ConsoleColor.Gray),
            ("%%%%%%%%%%%%%%@@@%%%%%%%%%%%%%%%:.#%%%%#..:*%%%%%%%%%%%*:.:#%%%%#.:%%%%%%@@%%%%%%%@@@%%%%%@@%%%%%%%%", ConsoleColor.DarkGray),
            ("%%@@%@%%%%%%%%%@%%%%%%%%%%%%%%%=:-%%%=.:-%%%%%%%%%%%%%%%%%-..=%%%-:=%%%%%%%%%%%%%%%%%@%%%%%@%%%%%@%@", ConsoleColor.DarkCyan),
            ("%%%%%@@@%%%%%%%%@%%%%%%%%%%%%%%:.*#.::*%@@%%%%%%%%%%%%%%%%%%*:..%*.:%%%%%@%%%%%%%%%%@@%%%%%%%@%%%%%%", ConsoleColor.Cyan),
            ("%@%%%%@@%%%%%%%%@@%%%%%%%%%%%%-::::=%@%%%%%%%%%%%%%%%%%%%%%%%%%-::::-%%%%%%%%%%%%%%%@%%%%%@@%%%%%%%%", ConsoleColor.DarkCyan),
            ("%@%%%%%%%%%%%%%@@@%%%%%%%%%%%%@@%@@@@%%%%%%%%%%%%@@%%%%%%%%%%%%%%%%@@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", ConsoleColor.Gray),

            // ── Base / grounding (the ####### bands)
            ("%%%%%%%%%%%%%%%@%@%%%%%%@@%%##########%%%%%%%###########%%%####%%%%%%%%%##%%%%%%%%%%%%%@@@%%%%%%%%%%", ConsoleColor.DarkGray),
            ("%%%%%%%%%%%%%%@%%%%%%%%%@=::::::::::::=%%+::::::::::::::%%=:::::+@%%%%%=::+%%%%%%%%%%%%@@@%%%%%%%%%%", ConsoleColor.Gray),
            ("%%%%%%%%%%%%%%@@%%%%%%%%*::-%%%%@%%%%%%%#:::%%%%@%%%@%%%%%=::+-:::%%%@%=::*%%%%%%%%%@@%%@%%%%%%%%%%%", ConsoleColor.DarkGray),
            ("%%%%%%%%@@@%%@@@%%%%%%@%*::-**********%%*::+%%%#********%%=::+@*:::#%%%=::+@%%%%%%%%@@@%%%%%%%%%%%%%", ConsoleColor.DarkCyan),
            ("%%%%%%%%%%%%%%@%%%%%%%%%+:::::::::::::+%*::+%%%+::::::::@%=::+@%%-::=@@=::+@@@%%%@%%%%%%%%%%%%%%%%@@", ConsoleColor.Cyan),

            // ── Brand / signal-section (the readout panels at the bottom)
            ("%%%%%%%%%%%@%%%%%%%%%%%%+::=@@@%%@%%%%%%*::-%%%%%%%%%:::%@=::+@%%%+:::%=::+@%%%%%%%%%%%%%%%%%%%%%%%%", Brand),
            ("%%%%%%%%%%%%%%@@%%%%%%@@*::=@%%%%%@%%%%%%:::::------::::%%=::+%%%%%#::::::+%%%%%%%%%%%%%%%@@@%%%%%%@", Brand),
            ("%%%%%%%%%%%%@@@@@%%%%%%%*::=@%%%%%%%%%%%%%#-:::::::::::*%%+::*%@%%%@%-::::#%%@@%%%%%%%%%%%%%%@%%%%%%", Brand),
            ("@@%%%%%%%%%%%@@%%%%%%%%%%@@@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@%%%%%%%%%%%%%@@@@@@%%%%%%%%%%@%%%@%%%%", SignalDim),
            ("%%%%%%%%%%%%%%@%%%%%%%%%%%%%%%%%%%%%%%@@@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@@%%%%%%%%%%@@%%%%%%%", SignalDim),
            ("%%%%%%%%%%%%%%%@%%%%%%%%%%%%@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@@%%%%%%%%%%%%%%%@@%@@%%%%%%%%@@%%%%@%", Tower),
            ("%%%%%%%%%%%%%%%%%%%%@%%%%%%@@@@@%%%%%%@%@@%%%%@@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@%%%%%%%%%%%%%%%%%@@@%%", Tower),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%@@@%%%%@@@@@%%%%%@@%%%%%%%%%%%%%%@@@%%%%%%%%%%@@@%%%%%%%@@@%%%%%%%%%%@%%", Tower),
            ("%%%%%%%%%%%%%%%%%@@%%%%%%%%%@%%%%%@@@@@%%%%%%@%%%%%%%%%%%%%%%%%%@@@%%%%%%%%%%%%%%%%%%@@@@@%%%%%%@%%", Tower),
            ("%%%@@%%@%%%%@@%%%%@%%%%%@@%%%%%@@@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@@@@@%%%%%%%%%%%%%%%@@@@%%@@%%%%", Tower),
            ("@%%%%%%%%@@@@@@%%%%%@@%%%%%%%%%@%%%@%%%%%%%%@%@%%%%%%%%%%%%%%%%%%%%%%%@%%%%%%%%%%%%@@@%%%%%%%%%%%%%%", Tower),
            ("%%%%%%%%%@@@@@%%%%%%%%%%%%%%%%%@@%%%%%@%%%%%@@@%%%%%@@%%%%%%%%%%%%@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Tower),
            ("%%%%%%%%%%%%@@@@%%%@%%%@@@@%%@@@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@%%%@@%%%%%%%%%%%@@%%%%%%%%%%%%%%@", Tower),
            ("%%%%%%%%%%%%@@@@%%%%%%%%%@@@%@@%%%%%%%%%%%%%%%%%%@%%%%%%%%%%%%%%%%%%%%@@%%%%%%%%%%%%@%%%@@%%%%%%%%%@", Tower),

            ("", ConsoleColor.Gray),
        };

        // ─────────────────────────────────────────────────────────────
        //  Write paths
        // ─────────────────────────────────────────────────────────────

        // Multi-segment per-line write path (used by the compact banner).
        private static bool TryWriteSegmented(Segment[][] lines)
        {
            EnsureReflection();
            if (s_consoleStreamGetter == null || s_setConsoleColor == null)
                return false;

            var stream = s_consoleStreamGetter() as TextWriter;
            if (stream == null) return false;

            try
            {
                s_setConsoleColor(ConsoleColor.Gray);
                stream.WriteLine();
                foreach (var line in lines)
                {
                    foreach (var seg in line)
                    {
                        s_setConsoleColor(seg.Color);
                        stream.Write(seg.Text);
                    }
                    stream.WriteLine();
                }
            }
            finally
            {
                try { s_setConsoleColor(ConsoleColor.Gray); } catch { }
            }
            return true;
        }

        private static void WritePlainFallbackSegmented(Segment[][] lines)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var line in lines)
                {
                    sb.Clear();
                    foreach (var seg in line) sb.Append(seg.Text);
                    Debug.Log($"[FiresGhettoNetworkMod] {sb}");
                }
            }
            catch { }
        }

        // Single-color-per-line write path (used by the big banner).
        private static bool TryWriteColoredBig()
        {
            EnsureReflection();
            if (s_consoleStreamGetter == null || s_setConsoleColor == null)
                return false;

            var stream = s_consoleStreamGetter() as TextWriter;
            if (stream == null) return false;

            try
            {
                s_setConsoleColor(ConsoleColor.Gray);
                stream.WriteLine();
                foreach (var line in s_bigLines)
                {
                    s_setConsoleColor(line.color);
                    stream.WriteLine(line.text);
                }
            }
            finally
            {
                try { s_setConsoleColor(ConsoleColor.Gray); } catch { }
            }
            return true;
        }

        private static void WritePlainFallbackBig()
        {
            try
            {
                foreach (var line in s_bigLines)
                    Debug.Log($"[FiresGhettoNetworkMod] {line.text}");
            }
            catch { }
        }

        // Resolves BepInEx.ConsoleManager via reflection. Identical to
        // VAFapBanner's resolver — see that class for the rationale on
        // why we go through reflection instead of typed access.
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
            catch { /* leave delegates null — caller falls back */ }
        }
    }
}
