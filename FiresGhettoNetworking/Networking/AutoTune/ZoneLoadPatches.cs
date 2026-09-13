using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod.AutoTune
{
    /// <summary>
    /// Client-side instantiation pacing for ZNetScene.CreateObjects: a time-budget prefix replays vanilla's
    /// own sorted and distant passes in chunks, with the older batch-cap transpiler as the fallback when
    /// time-slicing is switched off.
    /// </summary>
    [HarmonyPatch]
    public static class ZoneLoadPatches
    {
        // ----- Cap-bump transpiler tuning -----

        // Sanity range for "per-frame creation cap" constants. Vanilla 0.221.12 uses
        // 10 and 100; this range is wide enough to absorb minor Valheim tweaks but
        // tight enough that we won't accidentally rewrite e.g. a 4 (loop limit) or
        // 1000 (a count buffer) elsewhere in the method.
        private const int CapMin = 5;
        private const int CapMax = 200;

        // ----- Time-budget tuning -----

        // Vanilla's CreateObjectsSorted is chunked so the time budget can interrupt between chunks. One
        // prefab per chunk caps the overshoot at a single Instantiate; the previous ten assumed ~0.1 ms each,
        // which a city's first-instantiate spikes (shader variants, bundle resolution) turned into 50 ms of
        // overshoot and visible hitching. The per-chunk sort costs about 1.5 us on a few hundred elements.
        private const int ChunkSize = 1;

        // When SafetyFallbackEnabled and pending list exceeds the threshold,
        // multiply the per-frame budget by this factor — but never beyond 16 ms
        // (one full frame at 60 fps). Keeps a teleport-into-megabase from
        // bleeding visibly across many seconds, while still capping the worst
        // single-frame hitch.
        private const int SafetyBudgetMultiplier = 3;
        private const int SafetyBudgetMaxMs = 16;

        // Reverse-patched targets — Harmony copies the original method bodies into
        // these stubs at patch-time so we can call them directly without reflection.
        // First parameter is the instance (target methods are private instance
        // methods on ZNetScene). Refs preserved exactly.
        [HarmonyReversePatch]
        [HarmonyPatch(typeof(ZNetScene), "CreateObjectsSorted")]
        public static void CallCreateObjectsSorted(
            ZNetScene self,
            List<ZDO> currentNearObjects,
            int maxCreatedPerFrame,
            ref int created)
        {
            throw new NotImplementedException("Harmony reverse patch failed for ZNetScene.CreateObjectsSorted");
        }

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(ZNetScene), "CreateDistantObjects")]
        public static void CallCreateDistantObjects(
            ZNetScene self,
            List<ZDO> objects,
            int maxCreatedPerFrame,
            ref int created)
        {
            throw new NotImplementedException("Harmony reverse patch failed for ZNetScene.CreateDistantObjects");
        }

        // ====================================================================
        //                    PRIMARY: TIME-BUDGET PREFIX
        // ====================================================================
        [HarmonyPatch(typeof(ZNetScene), "CreateObjects")]
        [HarmonyPrefix]
        public static bool CreateObjects_TimeBudgetPrefix(
            ZNetScene __instance,
            List<ZDO> currentNearObjects,
            List<ZDO> currentDistantObjects)
        {
            // Server: dedicated server's CreateDestroyObjects path lives in
            // ServerAuthorityPatches and explicitly calls __instance.CreateObjects.
            // We don't want to time-slice the server — its instantiation cadence
            // is feeding clients, not driving a player camera. Bail and let
            // vanilla run (with the transpiler-bumped cap if active).
            try
            {
                if (ZNet.instance != null && ZNet.instance.IsServer())
                    return true;
            }
            catch
            {
                // ZNet.instance access failed — very early in startup. Defer to vanilla.
                return true;
            }

            if (!EffectiveConfig.TimeSliceInstantiationEnabled())
                return true;

            // ValheimCommunityPatch creates from its own queue inside the CreateObjectsSorted / CreateDistantObjects
            // prefixes and hands CreateObjects empty lists; the reverse-patched originals below would bypass it.
            if (ValheimCommunityPatchCompat.SchedulesObjectCreation)
                return true;

            // Cheap exits: nothing to do.
            int nearCount    = currentNearObjects    != null ? currentNearObjects.Count    : 0;
            int distantCount = currentDistantObjects != null ? currentDistantObjects.Count : 0;
            if (nearCount == 0 && distantCount == 0)
                return false;

            int budgetMs    = EffectiveConfig.InstantiationBudgetMs();
            int maxPerFrame = EffectiveConfig.MaxInstancesPerFrame();

            // Safety fallback: if the pending list is huge (teleport into megabase,
            // freshly-loaded zones), widen the budget so we don't bleed across
            // many seconds. Capped at SafetyBudgetMaxMs so we never burn an entire
            // frame on instantiation alone.
            if (EffectiveConfig.SafetyFallbackEnabled())
            {
                int totalPending = nearCount + distantCount;
                if (totalPending > EffectiveConfig.SafetyFallbackThreshold())
                {
                    int widened = budgetMs * SafetyBudgetMultiplier;
                    if (widened > SafetyBudgetMaxMs) widened = SafetyBudgetMaxMs;
                    budgetMs = widened;
                }
            }

            long budgetTicks = (long)budgetMs * Stopwatch.Frequency / 1000L;
            var sw = Stopwatch.StartNew();
            int created = 0;

            try
            {
                // Drain near list first (vanilla sorts these by Type then distance).
                while (currentNearObjects != null
                    && currentNearObjects.Count > 0
                    && sw.ElapsedTicks < budgetTicks
                    && created < maxPerFrame)
                {
                    int beforeCreated = created;
                    int chunkCap = Math.Min(ChunkSize, maxPerFrame - created);
                    if (chunkCap <= 0) break;

                    CallCreateObjectsSorted(__instance, currentNearObjects, chunkCap, ref created);

                    // No progress this chunk → all candidates filtered (zones not ready,
                    // already created, etc). No point looping — exit and let next tick
                    // re-evaluate with fresh sector data.
                    if (created == beforeCreated) break;
                }

                // Then distant list. Same loop shape; vanilla doesn't sort distant.
                while (currentDistantObjects != null
                    && currentDistantObjects.Count > 0
                    && sw.ElapsedTicks < budgetTicks
                    && created < maxPerFrame)
                {
                    int beforeCreated = created;
                    int chunkCap = Math.Min(ChunkSize, maxPerFrame - created);
                    if (chunkCap <= 0) break;

                    CallCreateDistantObjects(__instance, currentDistantObjects, chunkCap, ref created);

                    if (created == beforeCreated) break;
                }
            }
            catch (Exception ex)
            {
                // Reverse-patch invocation or downstream Instantiate threw. Log
                // once (not per-frame — Harmony will keep firing this prefix) and
                // skip vanilla so we don't double-instantiate whatever we already
                // committed this frame. Next frame either recovers or hits the
                // same exception; that's the user's signal something's wrong.
                LoggerOptions.LogWarning($"[AutoTune] Time-slice CreateObjects threw: {ex.Message}");
            }

            return false;
        }

        // ====================================================================
        //                  FALLBACK: CAP-BUMP TRANSPILER
        // ====================================================================
        // Only matters when the prefix above returns true (time-slicing disabled
        // or server). At that point vanilla's body runs and the transpiled cap
        // gives users the previous-generation behaviour rather than raw vanilla
        // 10/100. Wires <see cref="EffectiveConfig.ZoneLoadBatchSize"/> in via
        // a runtime helper so config / tier changes take effect immediately.

        public static int ScaleBatchCap(int vanillaCap)
        {
            try
            {
                if (ZNet.instance != null && ZNet.instance.IsServer()) return vanillaCap;
                int batch = EffectiveConfig.ZoneLoadBatchSize();
                if (batch <= 1) return vanillaCap;
                int scaled = vanillaCap * batch;
                if (scaled > vanillaCap * 8) scaled = vanillaCap * 8;
                return scaled;
            }
            catch
            {
                return vanillaCap;
            }
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateObjects))]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> CreateObjects_BatchCapTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var helper = AccessTools.Method(typeof(ZoneLoadPatches), nameof(ScaleBatchCap));
            if (helper == null) return code;

            int patched = 0;

            for (int i = 0; i < code.Count - 1; i++)
            {
                if (!IsSmallIntLoad(code[i], out int constant)) continue;
                if (constant < CapMin || constant > CapMax) continue;
                if (!IsStLocZero(code[i + 1])) continue;

                code[i].opcode  = OpCodes.Ldc_I4;
                code[i].operand = constant;
                code.Insert(i + 1, new CodeInstruction(OpCodes.Call, helper));
                i++; // skip the inserted call
                patched++;
            }

            if (patched == 0)
            {
                LoggerOptions.LogWarning("[AutoTune] ZoneLoadBatch: no per-frame cap constant found in ZNetScene.CreateObjects — game IL may have changed; ZoneLoadBatchSize will be a no-op until a follow-up patch addresses the new IL shape.");
            }
            else
            {
                LoggerOptions.LogInfo($"[AutoTune] ZoneLoadBatch: patched {patched} per-frame cap constant(s) (in-game cap + loading-screen cap) via runtime helper.");
            }

            return code;
        }

        private static bool IsStLocZero(CodeInstruction inst)
        {
            if (inst.opcode == OpCodes.Stloc_0) return true;
            if (inst.opcode == OpCodes.Stloc_S || inst.opcode == OpCodes.Stloc)
            {
                if (inst.operand is byte b) return b == 0;
                if (inst.operand is int  i) return i == 0;
                var lb = inst.operand as System.Reflection.Emit.LocalBuilder;
                if (lb != null) return lb.LocalIndex == 0;
                var lvi = inst.operand as System.Reflection.LocalVariableInfo;
                if (lvi != null) return lvi.LocalIndex == 0;
            }
            return false;
        }

        private static bool IsSmallIntLoad(CodeInstruction inst, out int value)
        {
            if (inst.opcode == OpCodes.Ldc_I4_0) { value = 0; return true; }
            if (inst.opcode == OpCodes.Ldc_I4_1) { value = 1; return true; }
            if (inst.opcode == OpCodes.Ldc_I4_2) { value = 2; return true; }
            if (inst.opcode == OpCodes.Ldc_I4_3) { value = 3; return true; }
            if (inst.opcode == OpCodes.Ldc_I4_4) { value = 4; return true; }
            if (inst.opcode == OpCodes.Ldc_I4_5) { value = 5; return true; }
            if (inst.opcode == OpCodes.Ldc_I4_6) { value = 6; return true; }
            if (inst.opcode == OpCodes.Ldc_I4_7) { value = 7; return true; }
            if (inst.opcode == OpCodes.Ldc_I4_8) { value = 8; return true; }

            if (inst.opcode == OpCodes.Ldc_I4 || inst.opcode == OpCodes.Ldc_I4_S)
            {
                if (inst.operand is int   i)  { value = i;  return true; }
                if (inst.operand is sbyte sb) { value = sb; return true; }
                if (inst.operand is byte  b)  { value = b;  return true; }
                if (inst.operand is short sh) { value = sh; return true; }
            }

            value = 0;
            return false;
        }
    }
}
