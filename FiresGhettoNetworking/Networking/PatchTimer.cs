using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.13 ([lead] 10-29, Fire's town session: VisEquipment.UpdateEquipmentVisuals costs ~35 µs a call with 97% of calls skipped,
    /// ZNet.Update has 100-276 ms per 5 s outside every vanilla call): Debug-only "who costs what" on a hot vanilla method.
    ///
    /// Never detours another mod's patch method (10-28: Core's patch-of-patch timers were the suspect for an ntdll crash). Instead it
    /// adds timestamp MARKER prefixes / postfixes to the VANILLA method itself, ordered between the other mods' patches with Harmony's
    /// own before / after / priority, then reads Harmony's final sorted order back. Every interval between two neighbouring markers
    /// is charged to exactly the patches Harmony placed between them (named by owner id + method), so a wrong placement can never
    /// mis-name a cost: it can only merge two owners into one row, which the line shows. The interval between the last prefix
    /// marker and the first postfix marker is "original (vanilla body + transpilers)". Finalizers are listed, not timed.
    ///
    /// Config (14 - Diagnostics): "Patch Timer" (default off; on on the rig) + "Patch Timer Methods" (Type.Method;Type.Method).
    /// Installed once the local player exists (on a dedicated server, once the server runs; 1.6.47), re-scanned every 30 s (a mod that patches late is picked up), removed when turned off.
    /// Every 10 s, per method: "[PatchTimer] Type.Method 10 s: N calls, whole X ms/s (µs/call) | owner: method µs/call ms/s | ...".
    /// </summary>
    internal static class PatchTimer
    {
        internal static ConfigEntry<bool> ConfigEnabled;
        internal static ConfigEntry<string> ConfigMethods;
        private const string MarkerId = "com.Fire.FiresGhettoNetworkMod.patchtimer";
        // 1.6.31: a method skipped for too many patches never reached s_targets, so every scan saw a change and repatched the whole list
        private static readonly Dictionary<MethodBase, int> s_tooMany = new Dictionary<MethodBase, int>();
        private const int MaxMarkers = 48;   // per kind (prefix / postfix) per method (1.6.14: 24 -> 48, ZNet.Update has 25 postfixes)
        private const float ReportSeconds = 10f, RescanSeconds = 30f;

        private static Harmony s_harmony;
        private static float s_nextScan = -1f, s_nextReport = -1f;
        private static bool s_installed;

        private sealed class Target
        {
            internal MethodBase Method;
            internal string Name;
            internal int PatchCount;               // foreign prefixes + postfixes + finalizers + transpilers at the last scan
            internal string[] PreLabel, PostLabel; // interval label ending at marker k
            internal string BodyLabel;
            internal double[] PreTicks, PostTicks; internal double BodyTicks, WholeTicks;
            internal long Last, Start; internal int Depth, LastPre, Calls;
            internal string Finalizers = "", Transpilers = "";
        }

        private static readonly Dictionary<MethodBase, Target> s_targets = new Dictionary<MethodBase, Target>();

        internal static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("14 - Diagnostics", "Patch Timer", false,
                Cfg.Adv("Debug only: times every mod's patches on the methods below, one log line per method every 10 seconds (for testing)."));
            ConfigMethods = config.Bind("14 - Diagnostics", "Patch Timer Methods", "VisEquipment.UpdateVisuals;VisEquipment.UpdateEquipmentVisuals;ZNet.Update",
                Cfg.Adv("Methods for the patch timer, as Type.Method separated by ';'. A method with overloads needs its parameter types, e.g. ZoneSystem.GetGroundHeight(Vector3). A name that is missing or ambiguous is skipped with a warning."));
            ConfigEnabled.SettingChanged += (_, __) => { if (!ConfigEnabled.Value) Remove(); s_nextScan = 0f; };
            ConfigMethods.SettingChanged += (_, __) => { Remove(); s_brokenThisSession = false; s_nextScan = 0f; };
        }

        private static bool On => ConfigEnabled != null && ConfigEnabled.Value && LoggerOptions.DebugEnabled;

        [HarmonyPatch(typeof(ZNetScene), "Update"), HarmonyPostfix]
        private static void Tick()
        {
            if (!On) { if (s_installed) Remove(); return; }
            // 1.6.47 ([lead] 11-07): a dedicated server has no local player; it times once the server runs (ZNet.cs:1877 IsServer)
            if (Player.m_localPlayer == null && (ZNet.instance == null || !ZNet.instance.IsServer())) return;
            float now = Time.realtimeSinceStartup;
            if (now >= s_nextScan) { s_nextScan = now + RescanSeconds; Scan(); }
            if (s_installed && now >= s_nextReport) { if (s_nextReport > 0f) Report(now); s_nextReport = now + ReportSeconds; ResetCounts(); }
        }

        // ---- install ----

        // 1.6.46 ([lead] 11-06, 4c's empty timer): each name resolves on its own; a missing or ambiguous one is skipped with ONE warning
        // (per name per methods value) and the rest is still timed. The cfg is never written (the old catch set Patch Timer = false).
        private static readonly HashSet<string> s_warned = new HashSet<string>();
        private static readonly List<string> s_skipped = new List<string>();
        private static string s_warnedFor;
        private static bool s_brokenThisSession;

        private static void Skip(string name, string why)
        {
            s_skipped.Add($"{name} ({why})");
            if (s_warned.Add(name)) LoggerOptions.LogWarning($"[PatchTimer] '{name}' skipped: {why}");
        }

        private static readonly Dictionary<string, string> s_alias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "float", "Single" }, { "int", "Int32" }, { "bool", "Boolean" }, { "string", "String" }, { "long", "Int64" },
            { "double", "Double" }, { "byte", "Byte" }, { "uint", "UInt32" }, { "short", "Int16" }, { "ulong", "UInt64" }, { "object", "Object" },
        };

        private static string ParamName(Type p)
        {
            string n = p.Name;
            return n.EndsWith("&") ? n.Substring(0, n.Length - 1) : n;
        }

        /// <summary>1.6.46: "Type.Method" or "Type.Method(T1, out T2)"; null with <paramref name="why"/> when missing or ambiguous.</summary>
        private static MethodBase Resolve(string s, out string why)
        {
            why = null;
            int paren = s.IndexOf('(');
            string head = paren >= 0 ? s.Substring(0, paren).Trim() : s;
            string[] hint = null;
            if (paren >= 0)
            {
                int close = s.LastIndexOf(')');
                if (close < paren) { why = "no closing ')'"; return null; }
                string inner = s.Substring(paren + 1, close - paren - 1).Trim();
                hint = inner.Length == 0 ? new string[0] : inner.Split(',').Select(x =>
                {
                    string a = x.Trim();
                    if (a.StartsWith("out ")) a = a.Substring(4).Trim(); else if (a.StartsWith("ref ")) a = a.Substring(4).Trim();
                    int dot2 = a.LastIndexOf('.'); if (dot2 >= 0) a = a.Substring(dot2 + 1);
                    return s_alias.TryGetValue(a, out string full) ? full : a;
                }).ToArray();
            }
            int dot = head.LastIndexOf('.');
            if (dot <= 0) { why = "not Type.Method"; return null; }
            Type t = AccessTools.TypeByName(head.Substring(0, dot));
            if (t == null) { why = "type not found"; return null; }
            string method = head.Substring(dot + 1);
            List<MethodInfo> all = AccessTools.GetDeclaredMethods(t).Where(m => m.Name == method).ToList();
            if (all.Count == 0) { why = "method not found"; return null; }
            string Sig(MethodInfo m) => "(" + string.Join(", ", m.GetParameters().Select(p => (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + ParamName(p.ParameterType))) + ")";
            if (hint == null)
            {
                if (all.Count == 1) return all[0];
                why = $"ambiguous, {all.Count} overloads: {string.Join(" | ", all.Select(Sig))}; add the parameter types, e.g. {head}{Sig(all[0]).Replace("out ", "")}";
                return null;
            }
            List<MethodInfo> hit = all.Where(m =>
            {
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != hint.Length) return false;
                for (int i = 0; i < ps.Length; i++) if (!string.Equals(ParamName(ps[i].ParameterType), hint[i], StringComparison.Ordinal)) return false;
                return true;
            }).ToList();
            if (hit.Count == 1) return hit[0];
            why = hit.Count == 0 ? $"no overload matches; there are: {string.Join(" | ", all.Select(Sig))}" : $"still ambiguous ({hit.Count} matches)";
            return null;
        }

        private static void Scan()
        {
            if (s_brokenThisSession) return;
            try
            {
                string value = ConfigMethods.Value ?? "";
                if (s_warnedFor != value) { s_warnedFor = value; s_warned.Clear(); }
                s_skipped.Clear();
                var wanted = new List<MethodBase>();
                foreach (string raw in value.Split(';'))
                {
                    string s = raw.Trim();
                    if (s.Length == 0) continue;
                    MethodBase m;
                    try { m = Resolve(s, out string why); if (m == null) { Skip(s, why); continue; } }
                    catch (Exception e) { Skip(s, e.GetType().Name + ": " + e.Message); continue; }
                    if (!wanted.Contains(m)) wanted.Add(m);
                }
                wanted.RemoveAll(m => s_tooMany.TryGetValue(m, out int seen) && seen == ForeignCount(m));
                bool changed = wanted.Count != s_targets.Count || wanted.Any(m => !s_targets.ContainsKey(m) || ForeignCount(m) != s_targets[m].PatchCount);
                if (!changed) return;
                Remove();
                s_harmony = s_harmony ?? new Harmony(MarkerId);
                foreach (MethodBase m in wanted)
                {
                    try { Install(m); }
                    catch (Exception e)
                    {
                        // only THIS method's markers come off; the rest stay timed. Not retried until its patch count changes.
                        try { s_harmony.Unpatch(m, HarmonyPatchType.All, MarkerId); } catch { }
                        s_targets.Remove(m); s_tooMany[m] = ForeignCount(m);
                        Skip(Name(m), "install failed: " + e.Message);
                    }
                }
                s_installed = true;
                s_nextReport = -1f;
                LoggerOptions.LogInfo($"[PatchTimer] timing {s_targets.Count} method(s): {string.Join(", ", s_targets.Values.Select(x => x.Name))}"
                    + (s_skipped.Count > 0 ? "; skipped: " + string.Join(", ", s_skipped) : ""));
            }
            catch (Exception e)
            {
                LoggerOptions.LogWarning($"[PatchTimer] install failed, timer stopped for this session (the cfg is left as it is): {e.Message}");
                Remove(); s_brokenThisSession = true;
            }
        }

        private static int ForeignCount(MethodBase m)
        {
            Patches p = Harmony.GetPatchInfo(m);
            if (p == null) return 0;
            return p.Prefixes.Count(x => x.owner != MarkerId) + p.Postfixes.Count(x => x.owner != MarkerId)
                 + p.Finalizers.Count + p.Transpilers.Count;
        }

        private static void Install(MethodBase m)
        {
            Patches info = Harmony.GetPatchInfo(m);
            var pre = info != null ? Sorted(m, info.Prefixes.Where(x => x.owner != MarkerId).ToArray()) : new List<Patch>();
            var post = info != null ? Sorted(m, info.Postfixes.Where(x => x.owner != MarkerId).ToArray()) : new List<Patch>();
            if (pre.Count + 1 > MaxMarkers || post.Count + 1 > MaxMarkers) { LoggerOptions.LogDebug($"[PatchTimer] {Name(m)}: too many patches ({pre.Count}/{post.Count}), skipped"); s_tooMany[m] = ForeignCount(m); return; }

            var t = new Target { Method = m, Name = Name(m), PatchCount = ForeignCount(m) };
            if (info != null)
            {
                t.Finalizers = string.Join(", ", info.Finalizers.Select(Label));
                t.Transpilers = string.Join(", ", info.Transpilers.Select(Label));
            }
            s_targets[m] = t;

            // prefix markers: P0 before everything, Pk right after foreign prefix k-1 (and before prefix k)
            for (int k = 0; k <= pre.Count; k++)
                s_harmony.Patch(m, prefix: Marker("Pre", k, k == 0 ? null : pre[k - 1], k < pre.Count ? pre[k] : null, true));
            // postfix markers: Q0 before every postfix, Qk right after foreign postfix k-1
            for (int k = 0; k <= post.Count; k++)
                s_harmony.Patch(m, postfix: Marker("Post", k, k == 0 ? null : post[k - 1], k < post.Count ? post[k] : null, false));

            // read Harmony's REAL order back and label every interval by what actually sits in it
            Patches fin = Harmony.GetPatchInfo(m);
            t.PreLabel = Labels(Sorted(m, fin.Prefixes.ToArray()), "Pre", out int preMarkers);
            t.PostLabel = Labels(Sorted(m, fin.Postfixes.ToArray()), "Post", out int postMarkers);
            t.PreTicks = new double[preMarkers]; t.PostTicks = new double[postMarkers];
            t.BodyLabel = "original (vanilla body" + (t.Transpilers.Length > 0 ? " + transpilers: " + t.Transpilers : "") + ")";
            LoggerOptions.LogDebug($"[PatchTimer] {t.Name}: timing {pre.Count} prefix(es) and {post.Count} postfix(es) of other mods; order "
                + $"pre [{string.Join(" | ", t.PreLabel.Skip(1))}] post [{string.Join(" | ", t.PostLabel.Skip(1))}]"
                + (t.Finalizers.Length > 0 ? "; finalizers not timed: " + t.Finalizers : ""));
        }

        // Harmony's own sort (before / after / priority / index); falls back to priority then index if the internal API moved
        private static readonly MethodInfo s_sort = AccessTools.Method("HarmonyLib.PatchFunctions:GetSortedPatchMethods");
        private static List<Patch> Sorted(MethodBase m, Patch[] patches)
        {
            if (s_sort != null && patches.Length > 1)
            {
                try
                {
                    var order = (List<MethodInfo>)s_sort.Invoke(null, new object[] { m, patches, false });
                    var left = patches.ToList(); var res = new List<Patch>();
                    foreach (MethodInfo mi in order) { int i = left.FindIndex(p => p.PatchMethod == mi); if (i >= 0) { res.Add(left[i]); left.RemoveAt(i); } }
                    res.AddRange(left);
                    return res;
                }
                catch { }
            }
            return patches.OrderByDescending(p => p.priority).ThenBy(p => p.index).ToList();
        }

        private static HarmonyMethod Marker(string kind, int k, Patch prev, Patch next, bool prefix)
        {
            var hm = new HarmonyMethod(AccessTools.Method(typeof(PatchTimer), kind + k));
            if (k == 0) { hm.priority = Priority.First + 1; if (next != null) hm.before = new[] { next.owner }; return hm; }
            hm.priority = prev.priority;   // same priority as the patch it follows, then pinned behind its owner
            hm.after = new[] { prev.owner };
            if (next != null && next.owner != prev.owner) hm.before = new[] { next.owner };
            return hm;
        }

        // label of marker k's interval = the foreign patches between marker k-1 and marker k in the final order
        private static string[] Labels(List<Patch> order, string kind, out int markers)
        {
            var labels = new List<string>(); var cur = new List<string>();
            foreach (Patch p in order)
            {
                if (p.owner == MarkerId && p.PatchMethod.Name.StartsWith(kind))
                {
                    labels.Add(cur.Count == 0 ? "(nothing)" : string.Join(" + ", cur));
                    cur.Clear();
                }
                else if (p.owner != MarkerId) cur.Add(Label(p));
            }
            markers = labels.Count;
            if (cur.Count > 0) labels[labels.Count - 1] += " (+ untimed after the last marker: " + string.Join(" + ", cur) + ")";
            return labels.ToArray();
        }

        private static string Label(Patch p) => p.owner + ":" + (p.PatchMethod.DeclaringType?.Name ?? "?") + "." + p.PatchMethod.Name;
        private static string Name(MethodBase m) => m.DeclaringType.Name + "." + m.Name;

        private static void Remove()
        {
            if (s_harmony != null) { try { s_harmony.UnpatchSelf(); } catch (Exception e) { LoggerOptions.LogWarning($"[PatchTimer] unpatch: {e.Message}"); } }
            s_targets.Clear(); s_installed = false;
        }

        // ---- the markers (one static method per slot; __originalMethod tells which target is running) ----

        private static void MarkPre(MethodBase o, int k)
        {
            if (o == null || !s_targets.TryGetValue(o, out Target t) || t.PreTicks == null) return;
            long now = Stopwatch.GetTimestamp();
            if (k == 0) { t.Depth = 1; t.Start = now; t.Last = now; t.LastPre = 0; return; }   // the targets never re-enter themselves; a throw in the original (no postfixes) is reset here
            if (t.Depth != 1 || k >= t.PreTicks.Length) return;
            t.PreTicks[k] += now - t.Last; t.Last = now; t.LastPre = k;
        }

        private static void MarkPost(MethodBase o, int k)
        {
            if (o == null || !s_targets.TryGetValue(o, out Target t) || t.PostTicks == null || t.Depth == 0) return;
            long now = Stopwatch.GetTimestamp();
            if (t.Depth != 1) { if (k == t.PostTicks.Length - 1) t.Depth--; return; }
            if (k == 0) t.BodyTicks += now - t.Last;
            else if (k < t.PostTicks.Length) t.PostTicks[k] += now - t.Last;
            t.Last = now;
            if (k == t.PostTicks.Length - 1) { t.WholeTicks += now - t.Start; t.Calls++; t.Depth = 0; }
        }

        private static void Pre0(MethodBase __originalMethod) => MarkPre(__originalMethod, 0);
        private static void Pre1(MethodBase __originalMethod) => MarkPre(__originalMethod, 1);
        private static void Pre2(MethodBase __originalMethod) => MarkPre(__originalMethod, 2);
        private static void Pre3(MethodBase __originalMethod) => MarkPre(__originalMethod, 3);
        private static void Pre4(MethodBase __originalMethod) => MarkPre(__originalMethod, 4);
        private static void Pre5(MethodBase __originalMethod) => MarkPre(__originalMethod, 5);
        private static void Pre6(MethodBase __originalMethod) => MarkPre(__originalMethod, 6);
        private static void Pre7(MethodBase __originalMethod) => MarkPre(__originalMethod, 7);
        private static void Pre8(MethodBase __originalMethod) => MarkPre(__originalMethod, 8);
        private static void Pre9(MethodBase __originalMethod) => MarkPre(__originalMethod, 9);
        private static void Pre10(MethodBase __originalMethod) => MarkPre(__originalMethod, 10);
        private static void Pre11(MethodBase __originalMethod) => MarkPre(__originalMethod, 11);
        private static void Pre12(MethodBase __originalMethod) => MarkPre(__originalMethod, 12);
        private static void Pre13(MethodBase __originalMethod) => MarkPre(__originalMethod, 13);
        private static void Pre14(MethodBase __originalMethod) => MarkPre(__originalMethod, 14);
        private static void Pre15(MethodBase __originalMethod) => MarkPre(__originalMethod, 15);
        private static void Pre16(MethodBase __originalMethod) => MarkPre(__originalMethod, 16);
        private static void Pre17(MethodBase __originalMethod) => MarkPre(__originalMethod, 17);
        private static void Pre18(MethodBase __originalMethod) => MarkPre(__originalMethod, 18);
        private static void Pre19(MethodBase __originalMethod) => MarkPre(__originalMethod, 19);
        private static void Pre20(MethodBase __originalMethod) => MarkPre(__originalMethod, 20);
        private static void Pre21(MethodBase __originalMethod) => MarkPre(__originalMethod, 21);
        private static void Pre22(MethodBase __originalMethod) => MarkPre(__originalMethod, 22);
        private static void Pre23(MethodBase __originalMethod) => MarkPre(__originalMethod, 23);
        private static void Pre24(MethodBase __originalMethod) => MarkPre(__originalMethod, 24);
        private static void Pre25(MethodBase __originalMethod) => MarkPre(__originalMethod, 25);
        private static void Pre26(MethodBase __originalMethod) => MarkPre(__originalMethod, 26);
        private static void Pre27(MethodBase __originalMethod) => MarkPre(__originalMethod, 27);
        private static void Pre28(MethodBase __originalMethod) => MarkPre(__originalMethod, 28);
        private static void Pre29(MethodBase __originalMethod) => MarkPre(__originalMethod, 29);
        private static void Pre30(MethodBase __originalMethod) => MarkPre(__originalMethod, 30);
        private static void Pre31(MethodBase __originalMethod) => MarkPre(__originalMethod, 31);
        private static void Pre32(MethodBase __originalMethod) => MarkPre(__originalMethod, 32);
        private static void Pre33(MethodBase __originalMethod) => MarkPre(__originalMethod, 33);
        private static void Pre34(MethodBase __originalMethod) => MarkPre(__originalMethod, 34);
        private static void Pre35(MethodBase __originalMethod) => MarkPre(__originalMethod, 35);
        private static void Pre36(MethodBase __originalMethod) => MarkPre(__originalMethod, 36);
        private static void Pre37(MethodBase __originalMethod) => MarkPre(__originalMethod, 37);
        private static void Pre38(MethodBase __originalMethod) => MarkPre(__originalMethod, 38);
        private static void Pre39(MethodBase __originalMethod) => MarkPre(__originalMethod, 39);
        private static void Pre40(MethodBase __originalMethod) => MarkPre(__originalMethod, 40);
        private static void Pre41(MethodBase __originalMethod) => MarkPre(__originalMethod, 41);
        private static void Pre42(MethodBase __originalMethod) => MarkPre(__originalMethod, 42);
        private static void Pre43(MethodBase __originalMethod) => MarkPre(__originalMethod, 43);
        private static void Pre44(MethodBase __originalMethod) => MarkPre(__originalMethod, 44);
        private static void Pre45(MethodBase __originalMethod) => MarkPre(__originalMethod, 45);
        private static void Pre46(MethodBase __originalMethod) => MarkPre(__originalMethod, 46);
        private static void Pre47(MethodBase __originalMethod) => MarkPre(__originalMethod, 47);
        private static void Post0(MethodBase __originalMethod) => MarkPost(__originalMethod, 0);
        private static void Post1(MethodBase __originalMethod) => MarkPost(__originalMethod, 1);
        private static void Post2(MethodBase __originalMethod) => MarkPost(__originalMethod, 2);
        private static void Post3(MethodBase __originalMethod) => MarkPost(__originalMethod, 3);
        private static void Post4(MethodBase __originalMethod) => MarkPost(__originalMethod, 4);
        private static void Post5(MethodBase __originalMethod) => MarkPost(__originalMethod, 5);
        private static void Post6(MethodBase __originalMethod) => MarkPost(__originalMethod, 6);
        private static void Post7(MethodBase __originalMethod) => MarkPost(__originalMethod, 7);
        private static void Post8(MethodBase __originalMethod) => MarkPost(__originalMethod, 8);
        private static void Post9(MethodBase __originalMethod) => MarkPost(__originalMethod, 9);
        private static void Post10(MethodBase __originalMethod) => MarkPost(__originalMethod, 10);
        private static void Post11(MethodBase __originalMethod) => MarkPost(__originalMethod, 11);
        private static void Post12(MethodBase __originalMethod) => MarkPost(__originalMethod, 12);
        private static void Post13(MethodBase __originalMethod) => MarkPost(__originalMethod, 13);
        private static void Post14(MethodBase __originalMethod) => MarkPost(__originalMethod, 14);
        private static void Post15(MethodBase __originalMethod) => MarkPost(__originalMethod, 15);
        private static void Post16(MethodBase __originalMethod) => MarkPost(__originalMethod, 16);
        private static void Post17(MethodBase __originalMethod) => MarkPost(__originalMethod, 17);
        private static void Post18(MethodBase __originalMethod) => MarkPost(__originalMethod, 18);
        private static void Post19(MethodBase __originalMethod) => MarkPost(__originalMethod, 19);
        private static void Post20(MethodBase __originalMethod) => MarkPost(__originalMethod, 20);
        private static void Post21(MethodBase __originalMethod) => MarkPost(__originalMethod, 21);
        private static void Post22(MethodBase __originalMethod) => MarkPost(__originalMethod, 22);
        private static void Post23(MethodBase __originalMethod) => MarkPost(__originalMethod, 23);
        private static void Post24(MethodBase __originalMethod) => MarkPost(__originalMethod, 24);
        private static void Post25(MethodBase __originalMethod) => MarkPost(__originalMethod, 25);
        private static void Post26(MethodBase __originalMethod) => MarkPost(__originalMethod, 26);
        private static void Post27(MethodBase __originalMethod) => MarkPost(__originalMethod, 27);
        private static void Post28(MethodBase __originalMethod) => MarkPost(__originalMethod, 28);
        private static void Post29(MethodBase __originalMethod) => MarkPost(__originalMethod, 29);
        private static void Post30(MethodBase __originalMethod) => MarkPost(__originalMethod, 30);
        private static void Post31(MethodBase __originalMethod) => MarkPost(__originalMethod, 31);
        private static void Post32(MethodBase __originalMethod) => MarkPost(__originalMethod, 32);
        private static void Post33(MethodBase __originalMethod) => MarkPost(__originalMethod, 33);
        private static void Post34(MethodBase __originalMethod) => MarkPost(__originalMethod, 34);
        private static void Post35(MethodBase __originalMethod) => MarkPost(__originalMethod, 35);
        private static void Post36(MethodBase __originalMethod) => MarkPost(__originalMethod, 36);
        private static void Post37(MethodBase __originalMethod) => MarkPost(__originalMethod, 37);
        private static void Post38(MethodBase __originalMethod) => MarkPost(__originalMethod, 38);
        private static void Post39(MethodBase __originalMethod) => MarkPost(__originalMethod, 39);
        private static void Post40(MethodBase __originalMethod) => MarkPost(__originalMethod, 40);
        private static void Post41(MethodBase __originalMethod) => MarkPost(__originalMethod, 41);
        private static void Post42(MethodBase __originalMethod) => MarkPost(__originalMethod, 42);
        private static void Post43(MethodBase __originalMethod) => MarkPost(__originalMethod, 43);
        private static void Post44(MethodBase __originalMethod) => MarkPost(__originalMethod, 44);
        private static void Post45(MethodBase __originalMethod) => MarkPost(__originalMethod, 45);
        private static void Post46(MethodBase __originalMethod) => MarkPost(__originalMethod, 46);
        private static void Post47(MethodBase __originalMethod) => MarkPost(__originalMethod, 47);

        // ---- report ----

        private static void ResetCounts()
        {
            foreach (Target t in s_targets.Values)
            {
                if (t.PreTicks != null) Array.Clear(t.PreTicks, 0, t.PreTicks.Length);
                if (t.PostTicks != null) Array.Clear(t.PostTicks, 0, t.PostTicks.Length);
                t.BodyTicks = t.WholeTicks = 0; t.Calls = 0;
            }
        }

        private static void Report(float now)
        {
            double secs = ReportSeconds + (now - s_nextReport), f = Stopwatch.Frequency;
            var sb = new StringBuilder(1024);
            foreach (Target t in s_targets.Values)
            {
                if (t.PreTicks == null || t.Calls == 0) continue;
                var rows = new List<KeyValuePair<string, double>>();
                for (int k = 1; k < t.PreTicks.Length; k++) rows.Add(new KeyValuePair<string, double>("prefix " + t.PreLabel[k], t.PreTicks[k]));
                rows.Add(new KeyValuePair<string, double>(t.BodyLabel, t.BodyTicks));
                for (int k = 1; k < t.PostTicks.Length; k++) rows.Add(new KeyValuePair<string, double>("postfix " + t.PostLabel[k], t.PostTicks[k]));
                rows.Sort((a, b) => b.Value.CompareTo(a.Value));
                sb.Length = 0;
                sb.Append("[PatchTimer] ").Append(t.Name).Append(' ').Append(secs.ToString("0")).Append(" s: ").Append(t.Calls).Append(" calls, whole ")
                  .Append((t.WholeTicks * 1000.0 / f / secs).ToString("0.00")).Append(" ms/s (").Append((t.WholeTicks * 1e6 / f / t.Calls).ToString("0.0")).Append(" µs/call)");
                foreach (var r in rows)
                    sb.Append(" | ").Append(r.Key).Append(' ').Append((r.Value * 1e6 / f / t.Calls).ToString("0.0")).Append(" µs/call ")
                      .Append((r.Value * 1000.0 / f / secs).ToString("0.00")).Append(" ms/s");
                LoggerOptions.LogDebug(sb.ToString());
            }
        }
    }
}
