using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.10 ([lead] + [visual] 10-29, a public EBM user: feasts not updating with Server-Side Simulation + Server ZDO Ownership
    /// Transfer on). Vanilla Feast.RPC_TryEat runs on the feast's OWNER: it writes the new stack into the ZDO and at once routes
    /// RPC_OnEat to everybody, whose UpdateVisual reads the stack from its LOCAL ZDO copy. When the server owns the feast (FGN's
    /// ownership transfer), that RPC can reach a client before the ZDO update does, so the client redraws the OLD stack, and a feast
    /// never redraws by itself. With 'Redraw Feasts When Their Stack Arrives' on, a client that got RPC_OnEat for a feast it does not
    /// own watches that feast's ZDO for up to WatchSeconds and calls UpdateVisual once when the data revision changes. Debug proof
    /// line when the stack it then draws differs from the one drawn at the RPC (the race happened). Nothing else changes.
    /// </summary>
    [HarmonyPatch]
    internal static class FeastRedraw
    {
        internal static ConfigEntry<bool> ConfigEnabled;
        private const float WatchSeconds = 10f;

        private struct Watch { public uint Rev; public int Drawn; public float Until; }
        private static readonly Dictionary<Feast, Watch> s_watch = new Dictionary<Feast, Watch>();
        private static readonly List<Feast> s_done = new List<Feast>();
        private static int s_fixed, s_same;
        private static bool s_proofLogged;

        internal static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("02 - Client Performance", "Redraw Feasts When Their Stack Arrives", true,
                Cfg.Adv("When someone eats from a feast that another peer (e.g. the server) owns, the feast is redrawn again once the new portion count arrives, so it never keeps showing the old number of portions."));
        }

        [HarmonyPatch(typeof(Feast), nameof(Feast.RPC_OnEat)), HarmonyPostfix]
        private static void OnEat(Feast __instance)
        {
            if (ConfigEnabled == null || !ConfigEnabled.Value || __instance == null) return;
            var nview = __instance.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || nview.IsOwner()) return;   // the owner already drew the value it wrote
            s_watch[__instance] = new Watch { Rev = nview.GetZDO().DataRevision, Drawn = __instance.GetStack(), Until = Time.time + WatchSeconds };
        }

        [HarmonyPatch(typeof(ZNet), "Update"), HarmonyPostfix]
        private static void Tick()
        {
            if (s_watch.Count == 0) return;   // inert unless a feast was eaten from nearby
            float now = Time.time;
            s_done.Clear();
            foreach (var kv in s_watch)
            {
                Feast f = kv.Key; Watch w = kv.Value;
                var nview = f != null ? f.GetComponent<ZNetView>() : null;
                if (nview == null || !nview.IsValid()) { s_done.Add(f); continue; }
                if (nview.GetZDO().DataRevision != w.Rev)
                {
                    int stack = f.GetStack();
                    f.UpdateVisual();
                    if (stack != w.Drawn) { s_fixed++; Proof(f, w.Drawn, stack); } else s_same++;
                    s_done.Add(f);
                }
                else if (now > w.Until) s_done.Add(f);
            }
            foreach (var f in s_done) s_watch.Remove(f);
        }

        // ---- F5 test 'fgn_feast_eat_test [n]' (round K with [visual]): the local player eats n times (default 3) from the nearest feast
        // within 6 m through vanilla Feast.Interact (the same call as pressing E). Food is cleared before each bite only so
        // CanConsumeItem lets the same player eat again. Each bite: the stack before, after 3 s, and whether the drawn parts match the stack.
        private static bool s_cmdAdded;

        [HarmonyPatch(typeof(Terminal), "InitTerminal"), HarmonyPostfix]
        private static void AddCommand()
        {
            if (s_cmdAdded) return;
            s_cmdAdded = true;
            new Terminal.ConsoleCommand("fgn_feast_eat_test", "[F5 test] [FGN] fgn_feast_eat_test [n] - eat n times (default 3) from the nearest feast within 6 m via vanilla Interact; checks the stack drops and the drawn feast parts match it after each bite (FeastRedraw).",
                args => { if (FiresGhettoNetworkMod.Instance != null) FiresGhettoNetworkMod.Instance.StartCoroutine(EatTest(args.Context, args.Length > 1 && int.TryParse(args[1], out int n) ? Mathf.Clamp(n, 1, 10) : 3)); }, isCheat: true);
        }

        private static System.Collections.IEnumerator EatTest(Terminal t, int bites)
        {
            void Say(string s) { LoggerOptions.LogDebug("[FeastEatTest] " + s); t?.AddString("[FeastEatTest] " + s); }
            float t0 = Time.realtimeSinceStartup; int pass = 0, fail = 0;
            Say("BEGIN");
            Player me = Player.m_localPlayer;
            Feast feast = null; float best = 6f;
            if (me != null) foreach (var f in Object.FindObjectsOfType<Feast>()) { float d = Vector3.Distance(f.transform.position, me.transform.position); if (d < best) { best = d; feast = f; } }
            var nview = feast != null ? feast.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid()) { Say("1/1 setup FAIL - no player or no feast within 6 m"); Say($"END FAIL: 0 pass, 1 fail, 0 skip in {Time.realtimeSinceStartup - t0:0} s"); yield break; }
            long owner = nview.GetZDO().GetOwner(), mine = ZDOMan.GetSessionID();
            Say($"feast '{feast.name}' at {best:0.0} m, stack {feast.GetStack()}, owner {owner} ({(owner == mine ? "THIS client: no race possible" : "another peer")}), redraw fix {(ConfigEnabled != null && ConfigEnabled.Value ? "ON" : "OFF")}");
            for (int i = 1; i <= bites; i++)
            {
                int before = feast.GetStack();
                if (before <= 0) { Say($"{i}/{bites} bite SKIP - the feast is empty"); break; }
                me.ClearFood();
                bool ok = feast.Interact(me, false, false);
                yield return new WaitForSeconds(3f);
                int after = feast.GetStack();
                bool drawnOk = PartsMatch(feast);
                bool good = ok && after == before - 1 && drawnOk;
                if (good) pass++; else fail++;
                Say($"{i}/{bites} bite {(good ? "PASS" : "FAIL")} - interact {ok}, stack {before} -> {after}, drawn parts {(drawnOk ? "match the stack" : "STALE")}; redraws corrected so far {s_fixed}");
            }
            Say($"END {(fail == 0 && pass > 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, 0 skip in {Time.realtimeSinceStartup - t0:0} s");
        }

        // the parts vanilla UpdateVisual would show for the current stack, compared with what is shown
        private static bool PartsMatch(Feast f)
        {
            float pct = f.GetStackPercentige();
            foreach (var lv in f.m_feastParts)
            {
                if (lv.m_onAboveEquals != null && lv.m_onAboveEquals.activeSelf != (pct >= lv.m_threshold)) return false;
                if (lv.m_onBelow != null && lv.m_onBelow.activeSelf != (pct < lv.m_threshold && pct >= lv.m_thresholdBelowMax)) return false;
            }
            return true;
        }

        private static void Proof(Feast f, int drawn, int stack)
        {
            string line = $"[FeastRedraw] '{f.name}' redrawn with {stack} portion(s): the eat RPC arrived before the stack (it drew {drawn}); {s_fixed} corrected, {s_same} already right";
            if (!s_proofLogged) { s_proofLogged = true; LoggerOptions.LogDebug(line); }
            else LoggerOptions.LogDebug(line);
        }
    }
}
