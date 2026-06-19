using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using FiresGhettoNetworkMod.AutoTune;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Hardens every loaded copy of any third-party "bulk transfer" framework
    /// (ServerSync and the ServerCharacters fork) against being starved by our
    /// raised ZDOMan queue cap. On each framework's waitForQueue-style gate loop
    /// we make TWO rewrites:
    ///
    ///   1. Disarm the 30-second self-disconnect — ALWAYS. ServerSync computes
    ///      `timeout = Time.time + 30f` and calls ZNet.Disconnect(peer) once it
    ///      elapses while the peer's send queue sits above the gate. A slow or
    ///      high-ping peer behind our raised ZDO queue trips this even though its
    ///      link is perfectly alive. We push that 30 out of reach so the send just
    ///      waits for the queue to drain instead of dropping the player. This is
    ///      the primary fix and runs regardless of the gate budget below.
    ///
    ///   2. Raise the internal 20 KB send-queue gate toward our cap — BUDGETED.
    ///      Secondary: helps the fragment loop keep pace, but is floored to a no-op
    ///      when many ServerSync copies share a small Steam send buffer (which is
    ///      exactly why the disconnect-disarm above is the real fix, not this).
    ///
    /// CURRENT TARGETS:
    ///   - ServerSync.ConfigSync          (Azumatt / Marketplace / EW / WackyDB / etc.)
    ///   - ServerCharacters.Shared        (Smoothbrain ServerCharacters fork)
    ///
    /// The scan keys on a method calling BOTH GetSendQueueSize and ZNet.Disconnect
    /// (the wait-or-drop loop), not on the gate constant — so the disarm lands even if
    /// a fork changed its gate value, while skipping plain GetSendQueueSize forwarders.
    /// Cecil-verified targets: ServerCharacters.Shared.&lt;sendCompressedDataToPeer&gt;waitForQueue
    /// (the player-profile path) and ServerSync.ConfigSync.&lt;distributeConfigToPeers&gt;waitForQueue.
    /// Every patched site is logged so the disarm can be confirmed in-game per copy.
    ///
    /// RUNS AT: ZNet.Start postfix. Plugin assemblies are loaded by then and the
    /// first bulk transfer (server→client config push on join, or SC profile push)
    /// happens after ZNet.Start, so the patches land in time.
    /// </summary>
    [HarmonyPatch]
    public static class BulkTransferGatePatches
    {
        private readonly struct Target
        {
            public readonly string TypeName;        // fully-qualified type to scan
            public readonly string GateMethodName;  // method whose return is compared
            public readonly int VanillaConstant;    // value the gate originally compared against

            public Target(string typeName, string gateMethodName, int vanillaConstant)
            {
                TypeName = typeName;
                GateMethodName = gateMethodName;
                VanillaConstant = vanillaConstant;
            }
        }

        private static readonly Target[] Targets =
        {
            new Target("ServerSync.ConfigSync",     "GetSendQueueSize", 20000),
            new Target("ServerCharacters.Shared",   "GetSendQueueSize", 20000),
        };

        // ServerSync's waitForQueue drops a peer if its send queue stays above the gate
        // for 30s. A live-but-slow/high-ping peer trips this even though its link is fine,
        // so we rewrite that 30f to an effectively-never value (1 day). Finite on purpose:
        // a peer that can receive small RPCs but somehow never the bulk sync still clears
        // eventually rather than lingering forever. ZNet/Steam handle genuinely dead peers.
        public static float DisconnectTimeoutSeconds = 86400f;
        private static readonly FieldInfo TimeoutField =
            AccessTools.Field(typeof(BulkTransferGatePatches), nameof(DisconnectTimeoutSeconds));

        // Jotunn's CustomRPC uses the same 30s waitForQueue but reads its timeout and gate from
        // static fields (CustomRPC.Timeout / MaximumSendQueueSize) instead of baked constants, so
        // we set the Timeout field directly rather than transpiling its IL. ApplyJotunnTimeout
        // mirrors DisconnectTimeoutSeconds onto it so the fgn_overload 'arm' control re-arms it too.
        private static FieldInfo s_jotunnTimeoutField;
        private static bool s_jotunnResolved;

        // Per-transpile context (Harmony's transpiler delegate takes no extra args).
        private static int s_currentVanillaConstant;

        // Set by the transpiler for the method just patched, so the scan log can confirm
        // the disarm actually landed — especially for the ServerCharacters copy.
        private static int s_lastTimeoutSitesDisarmed;

        // Idempotency — ZNet.Start can fire more than once per process.
        private static bool s_applied;

        private static int s_serverSyncCopyCount = 1;
        private static int s_budgetedGate;

        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        static void ApplyOnZNetStart()
        {
            if (s_applied) return;
            s_applied = true;
            if (!(FiresGhettoNetworkMod.ConfigEnableBulkTransferBoost?.Value ?? true))
            {
                LoggerOptions.LogMessage("Bulk-transfer gate scan skipped — ConfigEnableBulkTransferBoost = false.");
                return;
            }
            try
            {
                ApplyAll(FiresGhettoNetworkMod.Harmony);
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"Bulk-transfer gate scan threw: {ex.Message}");
            }
        }

        private static void ApplyAll(Harmony harmony)
        {
            int target = GetTargetQueueSize();

            s_serverSyncCopyCount = CountServerSyncCopies();
            s_budgetedGate = GetBudgetedGate();
            LoggerOptions.LogMessage(
                $"Bulk-transfer gate budget: {s_serverSyncCopyCount} ServerSync.ConfigSync copy/ies, "
                + $"Steam buffer ceiling {GetEffectiveSendBufferCeilingBytes() / 1024}KB, "
                + $"{FiresGhettoNetworkMod.ConfigBulkTransferBudgetPercent?.Value ?? 40}% budget → per-mod gate "
                + $"{s_budgetedGate} bytes (Queue Size target {target}).");
            if (s_budgetedGate <= 20000)
                LoggerOptions.LogMessage(
                    "Bulk-transfer budget floored the per-mod gate at the vanilla 20 KB (many ServerSync "
                    + "mods on the current Steam send buffer), so the gate is NOT raised — but the 30s "
                    + "self-disconnect is disarmed regardless, so slow/high-ping peers are never dropped. "
                    + "Install FiresSteamworksPatcher or lower 'Queue Size' only if you also want the gate raise.");

            int totalTypesFound = 0;
            int totalSitesPatched = 0;

            foreach (var t in Targets)
            {
                bool willRaiseGate = s_budgetedGate > t.VanillaConstant;

                int typesFound = 0;
                int sitesPatched = 0;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type rootType = null;
                    try { rootType = asm.GetType(t.TypeName, throwOnError: false); }
                    catch { continue; }
                    if (rootType == null) continue;

                    typesFound++;

                    foreach (var inspectedType in WalkAllNestedTypes(rootType))
                    {
                        foreach (var m in inspectedType.GetMethods(
                                     BindingFlags.Instance | BindingFlags.Static
                                     | BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.DeclaredOnly))
                        {
                            if (m.IsAbstract || m.ContainsGenericParameters) continue;
                            // The wait-or-drop loop calls BOTH GetSendQueueSize and ZNet.Disconnect.
                            // Requiring both pinpoints the gate loop (Cecil-verified: ServerCharacters.
                            // Shared.<sendCompressedDataToPeer>waitForQueue and ServerSync.ConfigSync.
                            // <distributeConfigToPeers>waitForQueue) and skips the BufferingSocket
                            // GetSendQueueSize forwarder, which sits on the hot per-iteration path.
                            if (!IsGateLoop(m, t.GateMethodName)) continue;

                            try
                            {
                                s_currentVanillaConstant = t.VanillaConstant;
                                s_lastTimeoutSitesDisarmed = 0;
                                harmony.Patch(
                                    original: m,
                                    transpiler: new HarmonyMethod(
                                        typeof(BulkTransferGatePatches),
                                        nameof(RaiseGateAndDisarmTimeout)));
                                sitesPatched++;
                                LoggerOptions.LogMessage(
                                    $"Bulk-transfer patched: {asm.GetName().Name} → {inspectedType.FullName}.{m.Name} "
                                    + $"(gate {t.VanillaConstant} → {(willRaiseGate ? s_budgetedGate : t.VanillaConstant)} bytes, "
                                    + $"30s self-disconnect disarmed at {s_lastTimeoutSitesDisarmed} site(s)).");
                            }
                            catch (Exception ex)
                            {
                                LoggerOptions.LogWarning(
                                    $"Failed to patch bulk-transfer gate at {asm.GetName().Name}.{inspectedType.FullName}.{m.Name}: {ex.Message}");
                            }
                        }
                    }
                }

                totalTypesFound += typesFound;
                totalSitesPatched += sitesPatched;
                LoggerOptions.LogMessage(
                    $"Bulk-transfer scan for {t.TypeName}: {typesFound} copies found, {sitesPatched} gate sites patched.");
            }

            LoggerOptions.LogMessage(
                $"Bulk-transfer scan complete: {totalTypesFound} third-party copies found across loaded assemblies, "
                + $"{totalSitesPatched} gate sites patched (per-mod gate {s_budgetedGate} bytes, 30s self-disconnect disarmed).");

            ApplyJotunnTimeout();
            LoggerOptions.LogMessage(s_jotunnTimeoutField != null
                ? $"Jotunn CustomRPC self-disconnect disarmed (Timeout -> {DisconnectTimeoutSeconds}s)."
                : "Jotunn CustomRPC not present — no Jotunn timeout to disarm.");
        }

        // Disarm Jotunn.Entities.CustomRPC's 30s self-disconnect by setting its static Timeout
        // field to DisconnectTimeoutSeconds. Called at scan time and re-called by the overload
        // test so the field tracks the current arm/disarm value (no IL transpile needed — Jotunn
        // reads the field live in its waitForQueue).
        public static void ApplyJotunnTimeout()
        {
            try
            {
                if (!s_jotunnResolved)
                {
                    s_jotunnResolved = true;
                    // Only probe for Jotunn's type when Jotunn is actually loaded — AccessTools.TypeByName
                    // logs a HarmonyX warning on every miss, so a server with no Jotunn mods would log
                    // "Could not find type named Jotunn.Entities.CustomRPC" at boot for nothing.
                    if (IsAssemblyLoaded("Jotunn"))
                    {
                        Type t = AccessTools.TypeByName("Jotunn.Entities.CustomRPC");
                        if (t != null) s_jotunnTimeoutField = AccessTools.Field(t, "Timeout");
                    }
                }
                s_jotunnTimeoutField?.SetValue(null, DisconnectTimeoutSeconds);
            }
            catch (Exception ex) { LoggerOptions.LogWarning($"Jotunn CustomRPC.Timeout disarm failed: {ex.Message}"); }
        }

        private static bool IsAssemblyLoaded(string assemblyName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (string.Equals(asm.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Depth-first walk through nested types. The frameworks we target use
        // `IEnumerable<bool> waitForQueue()` as a local function inside a coroutine,
        // which the compiler nests two levels deep in a state machine. Iterating
        // recursively makes discovery robust against any compiler version's nesting.
        private static IEnumerable<Type> WalkAllNestedTypes(Type root)
        {
            yield return root;
            Type[] nested;
            try { nested = root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic); }
            catch { yield break; }
            foreach (var nt in nested)
                foreach (var sub in WalkAllNestedTypes(nt))
                    yield return sub;
        }

        // IL pre-filter: true only if the method body calls BOTH the gate method
        // (GetSendQueueSize) and ZNet.Disconnect — the signature of a waitForQueue
        // wait-or-drop loop. This pinpoints the gate loop and excludes plain
        // GetSendQueueSize forwarders (e.g. BufferingSocket) that sit on the hot path.
        private static bool IsGateLoop(MethodBase m, string gateMethodName)
        {
            List<CodeInstruction> instructions;
            try { instructions = PatchProcessor.GetCurrentInstructions(m); }
            catch { return false; }

            bool hasGate = false, hasDisconnect = false;
            foreach (var ins in instructions)
            {
                if (!(ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt)) continue;
                if (!(ins.operand is MethodInfo mi)) continue;
                if (mi.Name == gateMethodName) hasGate = true;
                else if (mi.Name == "Disconnect") hasDisconnect = true;
                if (hasGate && hasDisconnect) return true;
            }
            return false;
        }

        // Transpiler — two rewrites on a confirmed waitForQueue method:
        //   1. Raise the <s_currentVanillaConstant> gate (only when the budget allows a
        //      higher value; a no-op when floored) within ±8 instructions of a
        //      GetSendQueueSize call, so an unrelated constant elsewhere is left alone.
        //   2. ALWAYS disarm the 30-second self-disconnect by rewriting the `30` in
        //      `timeout = Time.time + 30f` (float or double form) to DisconnectTimeoutSeconds.
        //      The method is pre-filtered to a queue-gate loop, so a 30 in it is the timeout.
        public static IEnumerable<CodeInstruction> RaiseGateAndDisarmTimeout(IEnumerable<CodeInstruction> instructions)
        {
            int vanilla = s_currentVanillaConstant;
            int target = s_budgetedGate;
            int disarmed = 0;
            var code = new List<CodeInstruction>(instructions);

            // Index every GetSendQueueSize call site for O(1) proximity lookup.
            var callIdx = new List<int>();
            for (int i = 0; i < code.Count; i++)
            {
                if ((code[i].opcode == OpCodes.Call || code[i].opcode == OpCodes.Callvirt)
                    && code[i].operand is MethodInfo mi && mi.Name == "GetSendQueueSize")
                    callIdx.Add(i);
            }
            if (callIdx.Count == 0)
            {
                s_lastTimeoutSitesDisarmed = 0;
                return code;
            }

            for (int i = 0; i < code.Count; i++)
            {
                // 1. Gate raise — only when the budget actually clears the vanilla floor.
                if (target > vanilla
                    && (code[i].opcode == OpCodes.Ldc_I4 || code[i].opcode == OpCodes.Ldc_I4_S)
                    && code[i].operand is int v && v == vanilla
                    && NearAnyCall(callIdx, i))
                {
                    code[i] = new CodeInstruction(OpCodes.Ldc_I4, target);
                    continue;
                }

                // 2. Disarm the 30s timeout (float or double form).
                if (code[i].opcode == OpCodes.Ldc_R4 && code[i].operand is float f && f == 30f)
                {
                    // Load the live timeout field instead of a baked constant, so the
                    // fgn_overload 'arm' control can restore 30s at runtime to prove the path.
                    code[i] = new CodeInstruction(OpCodes.Ldsfld, TimeoutField);
                    disarmed++;
                }
                else if (code[i].opcode == OpCodes.Ldc_R8 && code[i].operand is double d && d == 30.0)
                {
                    code[i] = new CodeInstruction(OpCodes.Ldc_R8, 86400.0);
                    disarmed++;
                }
            }

            s_lastTimeoutSitesDisarmed = disarmed;
            return code;
        }

        private static bool NearAnyCall(List<int> callIdx, int i)
        {
            foreach (var ci in callIdx)
                if (Math.Abs(ci - i) <= 8) return true;
            return false;
        }

        // Count ServerSync.ConfigSync copies across every loaded assembly. Each
        // ServerSync-bundling mod ILRepacks its own copy, so this is the number of
        // independent gates that could stack against the Steam send buffer.
        private static int CountServerSyncCopies()
        {
            int count = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (asm.GetType("ServerSync.ConfigSync", false) != null) count++; }
                catch { /* asm.GetType can throw on some dynamic assemblies — ignore */ }
            }
            return Math.Max(1, count);
        }

        // Steam's per-connection send buffer: 512 KB by default, or the larger value
        // FiresSteamworksPatcher unlocks (read through EffectiveConfig so the AutoTune
        // tier is respected). This is the pool the stacked ServerSync gates share.
        private static int GetEffectiveSendBufferCeilingBytes()
        {
            const int steamDefaultBuffer = 512 * 1024;
            try
            {
                if (NetworkingRatesGroup.IsSendBufferRaiseApplied())
                    return Math.Max(steamDefaultBuffer, EffectiveConfig.SteamSendBufferBytes());
            }
            catch { /* fall back to the Steam default if anything probes wrong */ }
            return steamDefaultBuffer;
        }

        // Per-mod gate = min(Queue Size target, (buffer * budget% / copy-count)), floored
        // at the vanilla 20 KB so we never throttle tighter than stock.
        private static int GetBudgetedGate()
        {
            int target = GetTargetQueueSize();
            int pct = FiresGhettoNetworkMod.ConfigBulkTransferBudgetPercent?.Value ?? 40;
            long budget = (long)GetEffectiveSendBufferCeilingBytes() * pct / 100L;
            int perMod = (int)(budget / Math.Max(1, s_serverSyncCopyCount));
            return Math.Max(20000, Math.Min(target, perMod));
        }

        private static int GetTargetQueueSize()
        {
            return EffectiveConfig.QueueSize() switch
            {
                QueueSizeOptions._80KB => 80 * 1024,
                QueueSizeOptions._64KB => 64 * 1024,
                QueueSizeOptions._48KB => 48 * 1024,
                QueueSizeOptions._32KB => 32 * 1024,
                _ => 20000 // _vanilla — match/under common third-party gates so the gate raise no-ops
            };
        }
    }
}
