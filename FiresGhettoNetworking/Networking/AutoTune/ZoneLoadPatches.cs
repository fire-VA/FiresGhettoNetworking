using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod.AutoTune
{
    /// <summary>
    /// Client-side instantiation pacing for ZNetScene.CreateObjects: a time-budget prefix creates vanilla's near
    /// objects in vanilla's order, then its distant objects, one at a time until the frame's budget or cap is
    /// spent, with the older batch-cap transpiler as the fallback when time-slicing is switched off.
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

        // When SafetyFallbackEnabled and pending list exceeds the threshold,
        // multiply the per-frame budget by this factor — but never beyond 16 ms
        // (one full frame at 60 fps). Keeps a teleport-into-megabase from
        // bleeding visibly across many seconds, while still capping the worst
        // single-frame hitch.
        private const int SafetyBudgetMultiplier = 3;
        private const int SafetyBudgetMaxMs = 16;

        // Vanilla's private CreateObject, ZDOCompare and InLoadingScreen, called through delegates so other mods'
        // patches on them still apply. Resolved on first use; if this game version lacks one, or the pass ever
        // throws, time-slicing turns itself off and vanilla instantiates for the rest of the session.
        private static Func<ZNetScene, ZDO, GameObject> s_createObject;
        private static Comparison<ZDO> s_vanillaOrder;
        private static Func<ZNetScene, bool> s_inLoadingScreen;
        private static bool s_helpersResolved;
        private static bool s_timeSliceDisabled;

        private static readonly List<ZDO> s_pendingNear = new List<ZDO>();

        private static bool TryResolveVanillaHelpers()
        {
            if (s_helpersResolved) return !s_timeSliceDisabled;
            s_helpersResolved = true;

            try
            {
                s_createObject = AccessTools.MethodDelegate<Func<ZNetScene, ZDO, GameObject>>(
                    AccessTools.Method(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) }));
                s_vanillaOrder = AccessTools.MethodDelegate<Comparison<ZDO>>(
                    AccessTools.Method(typeof(ZNetScene), "ZDOCompare", new[] { typeof(ZDO), typeof(ZDO) }));
                s_inLoadingScreen = AccessTools.MethodDelegate<Func<ZNetScene, bool>>(
                    AccessTools.Method(typeof(ZNetScene), "InLoadingScreen", Type.EmptyTypes));
            }
            catch (Exception ex)
            {
                s_timeSliceDisabled = true;
                LoggerOptions.LogWarning($"[AutoTune] Time-slice instantiation is unavailable on this game version; vanilla instantiation stays. {ex.Message}");
            }

            return !s_timeSliceDisabled;
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
            // prefixes and hands CreateObjects empty lists; this pass would bypass it.
            if (ValheimCommunityPatchCompat.SchedulesObjectCreation)
                return true;

            if (!TryResolveVanillaHelpers())
                return true;

            // Behind the loading screen vanilla creates 100 objects a frame to get the player in sooner; frame pacing
            // only matters once they are playing.
            if (s_inLoadingScreen(__instance))
                return true;

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
                // Vanilla's CreateObjectsSorted order: uncreated near objects, terrain first, then buildings, then the
                // rest, nearest first, each created only once its zone is ready for that type.
                if (nearCount > 0 && ZoneSystem.instance.IsActiveAreaLoaded())
                {
                    Vector3 referencePosition = ZNet.instance.GetReferencePosition();
                    s_pendingNear.Clear();
                    foreach (ZDO zdo in currentNearObjects)
                    {
                        if (zdo.Created) continue;
                        zdo.m_tempSortValue = Utils.DistanceSqr(referencePosition, zdo.GetPosition());
                        s_pendingNear.Add(zdo);
                    }
                    s_pendingNear.Sort(s_vanillaOrder);

                    foreach (ZDO zdo in s_pendingNear)
                    {
                        if (created >= maxPerFrame || sw.ElapsedTicks >= budgetTicks) break;
                        if (!ZoneSystem.instance.IsZoneReadyForType(zdo.GetSector(), zdo.Type)) continue;
                        if (s_createObject(__instance, zdo) != null) created++;
                    }
                    s_pendingNear.Clear();
                }

                // Distant objects get what the near pass leaves, as in vanilla.
                if (distantCount > 0)
                {
                    foreach (ZDO zdo in currentDistantObjects)
                    {
                        if (created >= maxPerFrame || sw.ElapsedTicks >= budgetTicks) break;
                        if (zdo.Created) continue;
                        if (s_createObject(__instance, zdo) != null) created++;
                    }
                }
            }
            catch (Exception ex)
            {
                // Objects created before the throw are marked Created, so vanilla picks up exactly where this stopped.
                s_pendingNear.Clear();
                s_timeSliceDisabled = true;
                LoggerOptions.LogWarning($"[AutoTune] Time-slice instantiation threw and is off for the rest of this session; vanilla instantiation takes over. {ex}");
                return true;
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

        private static readonly Dictionary<int, int> s_loggedBatchCaps = new Dictionary<int, int>();

        public static int ScaleBatchCap(int vanillaCap)
        {
            int cap = ScaledBatchCap(vanillaCap);
            if (CapeCrashDiagnostics.Enabled && (!s_loggedBatchCaps.TryGetValue(vanillaCap, out int logged) || logged != cap))
            {
                s_loggedBatchCaps[vanillaCap] = cap;
                CapeCrashDiagnostics.Log($"Per-frame creation cap {vanillaCap} is now {cap} (Zone Load Batch Size {EffectiveConfig.ZoneLoadBatchSize()})");
            }
            return cap;
        }

        private static int ScaledBatchCap(int vanillaCap)
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
