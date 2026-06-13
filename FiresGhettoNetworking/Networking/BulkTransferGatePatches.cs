using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using FiresGhettoNetworkMod.AutoTune;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Reflectively raises the internal 20 KB send-queue gate inside every
    /// loaded copy of any third-party "bulk transfer" framework so its gate
    /// matches our raised ZDOMan queue cap. Without this raise, those
    /// frameworks' fragment loops stall forever (queue stays above their gate
    /// because of our cap) and hit their 30-second self-disconnect.
    ///
    /// REPLACES the old BulkTransferGuard. BTG suppressed ZDOMan.SendZDOs to
    /// the destination peer during bulk fragment bursts, which had the side
    /// effect of starving *player position* ZDOs at the same time — the cause
    /// of the "players flying / teleporting / hits from across the map"
    /// complaint set. Raising the gate at the source avoids that entire
    /// failure mode: bulk fragments flow at our cap speed, ZDO sync is never
    /// suppressed, player position stays smooth.
    ///
    /// CURRENT TARGETS:
    ///   - ServerSync.ConfigSync          (Azumatt / Marketplace / EW / etc.)
    ///   - ServerCharacters.Shared        (Smoothbrain ServerCharacters)
    ///
    /// EXTENSIBILITY: add a new (typeName, callName, constant) tuple to
    /// <see cref="Targets"/> if another framework adopts the same pattern.
    /// Pre-filter is robust to false positives — only methods whose IL
    /// contains BOTH the constant load AND a call to the named method
    /// (within ±8 instructions of each other) get transpiled.
    ///
    /// RUNS AT: ZNet.Start postfix. Plugin assemblies are loaded by then and
    /// the first bulk transfer (server→client config push on join, or SC
    /// profile push) happens after ZNet.Start, so the patches land in time.
    /// </summary>
    [HarmonyPatch]
    public static class BulkTransferGatePatches
    {
        // Each entry describes a third-party type whose nested types contain a
        // local function shaped like `while (socket.GetSendQueueSize() > N)`.
        // The scan walks the type + every nested type and looks for methods
        // mentioning both the gate constant AND a call to the gate method.
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

        // Per-transpile target context. Set on the patch site (one harmony.Patch
        // call per match) so the shared transpiler knows which constant to look
        // for. Used to avoid passing arguments through Harmony's transpiler
        // delegate (which takes no extra context).
        private static int s_currentVanillaConstant;

        // Idempotency — ZNet.Start can fire more than once per process
        // (host quit → join again from main menu).
        private static bool s_applied;

        // Number of ServerSync.ConfigSync copies across loaded assemblies, and the
        // per-mod gate after dividing the Steam send-buffer budget across them.
        // Both are computed once in ApplyAll BEFORE any harmony.Patch call, so the
        // transpiler (BumpQueueGate) can read the final budgeted value.
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

            // Budget the per-mod gate against the Steam send-buffer ceiling so that
            // N stacked ServerSync gates can't collectively overflow it (the cause
            // of the heavy-area peer disconnects). Counted before any Patch() so the
            // transpiler reads the final budgeted value.
            s_serverSyncCopyCount = CountServerSyncCopies();
            s_budgetedGate = GetBudgetedGate();
            LoggerOptions.LogMessage(
                $"Bulk-transfer gate budget: {s_serverSyncCopyCount} ServerSync.ConfigSync copy/ies, "
                + $"Steam buffer ceiling {GetEffectiveSendBufferCeilingBytes() / 1024}KB, "
                + $"{FiresGhettoNetworkMod.ConfigBulkTransferBudgetPercent?.Value ?? 40}% budget → per-mod gate "
                + $"{s_budgetedGate} bytes (Queue Size target {target}).");
            if (s_budgetedGate <= 20000)
                LoggerOptions.LogWarning(
                    "Bulk-transfer budget floored the per-mod gate at the vanilla 20 KB — too many "
                    + "ServerSync mods for the current Steam send buffer. Install FiresSteamworksPatcher "
                    + "to raise the buffer, or lower 'Queue Size', to give each mod more headroom.");

            int totalTypesFound = 0;
            int totalSitesPatched = 0;

            foreach (var t in Targets)
            {
                if (s_budgetedGate <= t.VanillaConstant)
                {
                    LoggerOptions.LogInfo(
                        $"Bulk-transfer gate scan for {t.TypeName} skipped — "
                        + $"budgeted gate ({s_budgetedGate} bytes) is at/below the vanilla {t.VanillaConstant} default.");
                    continue;
                }

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
                            if (!MentionsBoth(m, t.GateMethodName, t.VanillaConstant)) continue;

                            try
                            {
                                s_currentVanillaConstant = t.VanillaConstant;
                                harmony.Patch(
                                    original: m,
                                    transpiler: new HarmonyMethod(
                                        typeof(BulkTransferGatePatches),
                                        nameof(BumpQueueGate)));
                                sitesPatched++;
                                // Message-level so operators can verify exactly which methods we
                                // touched without flipping log level. If a non-waitForQueue method
                                // appears here it's a false positive and the kill switch
                                // (ConfigEnableBulkTransferBoost = false) lets us bisect cleanly.
                                LoggerOptions.LogMessage(
                                    $"Bulk-transfer gate patched: {asm.GetName().Name} → {inspectedType.FullName}.{m.Name} "
                                    + $"({t.VanillaConstant} → {s_budgetedGate} bytes).");
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
                    $"Bulk-transfer gate scan for {t.TypeName}: {typesFound} copies found, {sitesPatched} gate sites patched.");
            }

            LoggerOptions.LogMessage(
                $"Bulk-transfer gate scan complete: {totalTypesFound} third-party copies found across loaded assemblies, "
                + $"{totalSitesPatched} queue-gate sites patched (per-mod gate {s_budgetedGate} bytes).");
        }

        // Depth-first walk through nested types. The frameworks we target use
        // `IEnumerable<bool> waitForQueue()` as a local function inside a
        // coroutine method, which the C# compiler turns into:
        //   <RootType>                            — the outer type
        //     <>c__DisplayClassN_0                — closure capturing locals
        //     <<Outer>g__waitForQueue|N_M>d__K    — iterator state machine
        // The gate constant lives in the state machine's MoveNext, two levels
        // deep. Iterating recursively makes the discovery robust against any
        // compiler version's nesting choices.
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

        // IL pre-filter: returns true only if the method body contains BOTH
        // (a) a load of the given int constant and (b) a call to a method
        // named `callName`. Pairing both signals is highly specific to the
        // queue-gate site and avoids transpiling unrelated nested methods.
        private static bool MentionsBoth(MethodBase m, string callName, int needsConstant)
        {
            List<CodeInstruction> instructions;
            try { instructions = PatchProcessor.GetCurrentInstructions(m); }
            catch { return false; }

            bool hasConst = false, hasCall = false;
            foreach (var ins in instructions)
            {
                if (!hasConst
                    && (ins.opcode == OpCodes.Ldc_I4 || ins.opcode == OpCodes.Ldc_I4_S)
                    && ins.operand is int v && v == needsConstant)
                    hasConst = true;

                if (!hasCall
                    && (ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt)
                    && ins.operand is MethodInfo mi && mi.Name == callName)
                    hasCall = true;

                if (hasConst && hasCall) return true;
            }
            return false;
        }

        // Transpiler — rewrite each occurrence of <s_currentVanillaConstant>
        // that sits within ±8 instructions of a GetSendQueueSize call. The
        // proximity check ensures we don't accidentally touch an unrelated
        // constant elsewhere in the same method.
        public static IEnumerable<CodeInstruction> BumpQueueGate(IEnumerable<CodeInstruction> instructions)
        {
            int vanilla = s_currentVanillaConstant;
            int target = s_budgetedGate;
            var code = new List<CodeInstruction>(instructions);

            // Index every GetSendQueueSize call site up front for O(1) lookup
            // per candidate constant.
            var callIdx = new List<int>();
            for (int i = 0; i < code.Count; i++)
            {
                if ((code[i].opcode == OpCodes.Call || code[i].opcode == OpCodes.Callvirt)
                    && code[i].operand is MethodInfo mi && mi.Name == "GetSendQueueSize")
                    callIdx.Add(i);
            }
            if (callIdx.Count == 0) return code;

            for (int i = 0; i < code.Count; i++)
            {
                if (!(code[i].opcode == OpCodes.Ldc_I4 || code[i].opcode == OpCodes.Ldc_I4_S))
                    continue;
                if (!(code[i].operand is int v) || v != vanilla)
                    continue;

                bool near = false;
                foreach (var ci in callIdx)
                {
                    if (Math.Abs(ci - i) <= 8) { near = true; break; }
                }
                if (!near) continue;

                code[i] = new CodeInstruction(OpCodes.Ldc_I4, target);
            }

            return code;
        }

        // Match the value NetworkingRatesGroup's ZDOMan.SendZDOs transpiler
        // uses. Keeping the two in lockstep means our cap and third-party
        // gates stay aligned regardless of which queue-size tier is active.
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

        // Steam's per-connection send buffer: 512 KB by default, or the larger
        // value FiresSteamworksPatcher unlocks (read through EffectiveConfig so the
        // AutoTune tier is respected). This is the pool the stacked ServerSync gates
        // must share.
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

        // Per-mod gate = min(Queue Size target, (buffer * budget% / copy-count)),
        // floored at the vanilla 20 KB so we never throttle tighter than stock
        // (which would re-introduce the stall the gate-raise exists to prevent).
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
                _ => 20000 // _vanilla — return a value matching/under common third-party gates so the scan skips
            };
        }
    }
}
