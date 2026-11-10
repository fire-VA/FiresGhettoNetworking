using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// [ghost] FGN 1.6.70 ([lead] 11-08, [generator]'s 4-mod study item F2). FGN already patches one class at a time (Ascend.TryPatchAll), so one
    /// broken class never stops the rest. This adds the two missing parts:
    ///  - ROLL-BACK: Harmony applies a class's targets one by one, so a class that throws on its 3rd target has already patched the first two.
    ///    Those parts (our Harmony id, patch methods declared in exactly that class) are removed again, so a feature never runs half-way
    ///    (a prefix without the postfix that undoes it, a transpiler without its state hook).
    ///  - SUMMARY: one Info line after the boot patch pass: classes attached, game methods patched, classes that failed (named).
    /// FGN's own code; nothing shared with the other trinity mods.
    /// </summary>
    internal static class PatchSafety
    {
        private static int s_attached, s_methods;
        private static readonly List<string> s_failed = new List<string>();

        /// <summary>From TryPatchAll after a class attached: what Harmony's class processor returned (one entry per game method patched).</summary>
        internal static void NoteAttached(List<MethodInfo> patched)
        {
            s_attached++;
            if (patched != null) s_methods += patched.Count;
        }

        /// <summary>Called from TryPatchAll's catch: removes what this class had already applied; returns how many parts.</summary>
        internal static int RollBack(Harmony harmony, Type type)
        {
            s_failed.Add(type.DeclaringType != null ? type.DeclaringType.Name + "." + type.Name : type.Name);
            int undone = 0;
            try
            {
                foreach (var original in new List<MethodBase>(harmony.GetPatchedMethods()))
                {
                    var info = Harmony.GetPatchInfo(original);
                    if (info == null) continue;
                    undone += Undo(harmony, original, info.Prefixes, type);
                    undone += Undo(harmony, original, info.Postfixes, type);
                    undone += Undo(harmony, original, info.Transpilers, type);
                    undone += Undo(harmony, original, info.Finalizers, type);
                }
            }
            catch (Exception e) { LoggerOptions.LogDebug($"[PatchSafety] roll-back of {type.Name} incomplete: {e.Message}"); }
            return undone;
        }

        internal static void Summary()
        {
            if (s_failed.Count == 0)
                LoggerOptions.LogInfo($"[PatchSafety] {s_attached} FGN patch class(es) attached, {s_methods} game method(s) patched, none failed.");
            else
                LoggerOptions.LogWarning($"[PatchSafety] {s_attached} FGN patch class(es) attached, {s_methods} game method(s) patched; {s_failed.Count} could not attach and are off this session (each rolled back, the rest work): {string.Join(", ", s_failed)}");
        }

        private static int Undo(Harmony harmony, MethodBase original, IEnumerable<Patch> patches, Type type)
        {
            if (patches == null) return 0;
            var mine = new List<MethodInfo>();
            foreach (var p in patches)
                if (p != null && p.owner == harmony.Id && p.PatchMethod != null && p.PatchMethod.DeclaringType == type) mine.Add(p.PatchMethod);
            foreach (var m in mine) harmony.Unpatch(original, m);
            return mine.Count;
        }
    }
}
