using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using FiresGhettoNetworkMod.AutoTune;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Patches every loaded ServerSync / ServerCharacters bulk-transfer gate loop at ZNet.Start: the
    /// 30-second self-disconnect is disarmed unconditionally, and the 20 KB send-queue gate is raised toward
    /// our cap within the configured budget. Sites are matched by shape (GetSendQueueSize plus
    /// ZNet.Disconnect in one method), not by constant, and every patched site is logged.
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
            new Target("ServerSync.ConfigSync",     "GetSendQueueSize", ThirdPartyGateFloorBytes),
            new Target("ServerCharacters.Shared",   "GetSendQueueSize", ThirdPartyGateFloorBytes),
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

        // Fallback when the config entry is not bound yet; matches the config default.
        private const int DefaultBudgetPercent = 40;
        // ServerSync's baked-in self-disconnect timeout, in the float and double forms the compiler may emit.
        private const float VanillaQueueTimeoutSeconds = 30f;
        private const double VanillaQueueTimeoutSecondsDouble = 30.0;
        // DisconnectTimeoutSeconds' default, for the double form that is rewritten to a constant.
        private const double DisarmedTimeoutSecondsDouble = 86400.0;
        // How many IL instructions from a GetSendQueueSize call the gate constant may sit.
        private const int GateCallProximity = 8;

        internal static void ApplyOnZNetStart()
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
            LoggerOptions.LogInfo(
                $"Bulk-transfer gate budget: {s_serverSyncCopyCount} ServerSync.ConfigSync copy/ies, "
                + $"Steam buffer ceiling {GetEffectiveSendBufferCeilingBytes() / 1024}KB, "
                + $"{FiresGhettoNetworkMod.ConfigBulkTransferBudgetPercent?.Value ?? DefaultBudgetPercent}% budget → per-mod gate "
                + $"{s_budgetedGate} bytes (Queue Size target {target}).");
            if (s_budgetedGate <= ThirdPartyGateFloorBytes)
                LoggerOptions.LogInfo(
                    "Bulk-transfer budget floored the per-mod gate at the vanilla 20 KB (many ServerSync "
                    + "mods on the current Steam send buffer), so the gate is NOT raised — but the 30s "
                    + "self-disconnect is disarmed regardless, so slow/high-ping peers are never dropped. "
                    + "Install FiresSteamworksPatcher or lower 'Queue Size' only if you also want the gate raise.");

            int totalTypesFound = 0;
            int totalSitesPatched = 0;
            int typesSkipped = 0;   // obfuscated copies left as shipped

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
                            if (!IsGateLoop(m, t.GateMethodName, out string unreadable)) continue;
                            if (unreadable != null)
                            {
                                // An obfuscated mod's ServerSync copy can read back with a call Harmony cannot resolve, and HarmonyX
                                // fails it with "Invalid argument for call NULL". No IL patch can land there; leave that copy as it ships.
                                typesSkipped++;
                                LoggerOptions.LogInfo(
                                    $"Bulk-transfer gate left as shipped in {asm.GetName().Name} → {inspectedType.FullName}.{m.Name}: "
                                    + $"its code is obfuscated ({unreadable}), so it cannot be patched. That mod keeps ServerSync's own "
                                    + "20 KB gate and 30s self-disconnect; nothing else changes.");
                                continue;
                            }

                            // Run the rewrite on a copy of the body first and check what HarmonyX would write.
                            // A body it would refuse is left as shipped with one Info line, so HarmonyX never gets to log an Error.
                            s_currentVanillaConstant = t.VanillaConstant;
                            string bad = ValidateRewrite(m);
                            s_lastTimeoutSitesDisarmed = 0;
                            if (bad != null)
                            {
                                typesSkipped++;
                                LoggerOptions.LogInfo(
                                    $"Bulk-transfer gate left as shipped in {asm.GetName().Name} → {inspectedType.FullName}.{m.Name}: "
                                    + $"its rewritten code would not be valid ({bad}). That mod keeps ServerSync's own 20 KB gate and 30s self-disconnect.");
                                continue;
                            }

                            try
                            {
                                harmony.Patch(
                                    original: m,
                                    transpiler: new HarmonyMethod(
                                        typeof(BulkTransferGatePatches),
                                        nameof(RaiseGateAndDisarmTimeout)));
                                sitesPatched++;
                                LoggerOptions.LogInfo(
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
                LoggerOptions.LogInfo(
                    $"Bulk-transfer scan for {t.TypeName}: {typesFound} copies found, {sitesPatched} gate sites patched.");
            }

            LoggerOptions.LogMessage(
                $"Bulk-transfer scan complete: {totalTypesFound} third-party copies found across loaded assemblies, "
                + $"{totalSitesPatched} gate sites patched (per-mod gate {s_budgetedGate} bytes, 30s self-disconnect disarmed)"
                + (typesSkipped > 0 ? $", {typesSkipped} obfuscated copy/ies left as shipped." : "."));

            ApplyJotunnTimeout();
            LoggerOptions.LogInfo(s_jotunnTimeoutField != null
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
        // `unreadable` is set (and the method is still reported as a gate loop) when an instruction that needs an operand
        // reads back with none: obfuscated code (a call whose target reflection cannot resolve). Harmony refuses to write
        // such a body back, whatever the transpiler does, so the caller skips it with an Info line instead.
        private static bool IsGateLoop(MethodBase m, string gateMethodName, out string unreadable)
        {
            unreadable = null;
            List<CodeInstruction> instructions;
            try { instructions = PatchProcessor.GetCurrentInstructions(m); }
            catch { return false; }

            bool hasGate = false, hasDisconnect = false;
            foreach (var ins in instructions)
            {
                if (unreadable == null && ins.operand == null && ins.opcode.OperandType != OperandType.InlineNone)
                    unreadable = $"a {ins.opcode.Name} with no resolvable operand";
                if (!(ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt)) continue;
                if (!(ins.operand is MethodInfo mi)) continue;
                if (mi.Name == gateMethodName) hasGate = true;
                else if (mi.Name == "Disconnect") hasDisconnect = true;
            }
            return hasGate && hasDisconnect;
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
                    // Rewritten in place: a new CodeInstruction would drop the labels and exception blocks on this one,
                    // and a branch to it would then point nowhere.
                    code[i].opcode = OpCodes.Ldc_I4;
                    code[i].operand = target;
                    continue;
                }

                // 2. Disarm the 30s timeout (float or double form), in place for the same reason.
                if (code[i].opcode == OpCodes.Ldc_R4 && code[i].operand is float f && f == VanillaQueueTimeoutSeconds)
                {
                    // Load the live timeout field instead of a baked constant, so the
                    // fgn_overload 'arm' control can restore 30s at runtime to prove the path.
                    code[i].opcode = OpCodes.Ldsfld;
                    code[i].operand = TimeoutField;
                    disarmed++;
                }
                else if (code[i].opcode == OpCodes.Ldc_R8 && code[i].operand is double d && d == VanillaQueueTimeoutSecondsDouble)
                {
                    code[i].operand = DisarmedTimeoutSecondsDouble;
                    disarmed++;
                }
            }

            s_lastTimeoutSitesDisarmed = disarmed;
            return code;
        }

        /// <summary>
        /// The transpiler's output for this method, checked the way HarmonyX's writer needs it: every instruction that takes an
        /// operand has one, and every branch / switch target is a label some instruction carries. Null = fine to patch; otherwise
        /// the first problem, by instruction index and opcode.
        /// </summary>
        private static string ValidateRewrite(MethodBase m)
        {
            List<CodeInstruction> code;
            try { code = RaiseGateAndDisarmTimeout(PatchProcessor.GetCurrentInstructions(m)).ToList(); }
            catch (Exception ex) { return $"reading it failed: {ex.GetType().Name}"; }
            var defined = new HashSet<Label>();
            foreach (var ins in code) foreach (var label in ins.labels) defined.Add(label);
            for (int i = 0; i < code.Count; i++)
            {
                var ins = code[i];
                if (ins.opcode.OperandType == OperandType.InlineNone) continue;
                if (ins.operand == null) return $"instruction {i} {ins.opcode.Name} has no operand";
                if (ins.operand is Label target && !defined.Contains(target)) return $"instruction {i} {ins.opcode.Name} branches to a missing label";
                if (ins.operand is Label[] targets)
                    foreach (var each in targets)
                        if (!defined.Contains(each)) return $"instruction {i} switch branches to a missing label";
            }
            return null;
        }

        private static bool NearAnyCall(List<int> callIdx, int i)
        {
            foreach (var ci in callIdx)
                if (Math.Abs(ci - i) <= GateCallProximity) return true;
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
            int pct = FiresGhettoNetworkMod.ConfigBulkTransferBudgetPercent?.Value ?? DefaultBudgetPercent;
            long budget = (long)GetEffectiveSendBufferCeilingBytes() * pct / 100L;
            int perMod = (int)(budget / Math.Max(1, s_serverSyncCopyCount));
            return Math.Max(ThirdPartyGateFloorBytes, Math.Min(target, perMod));
        }

        private const int ThirdPartyGateFloorBytes = 20000;

        /// <summary>The vanilla option targets the common third-party gate size, so raising the gate no-ops.</summary>
        private static int GetTargetQueueSize() => EffectiveConfig.QueueSizeBytes(ThirdPartyGateFloorBytes);
    }
}
