using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// fgn_zdoflood [count] [prefab] — instantiation / zone-load stress (admin). A different axis than
    /// the socket tests: this stresses CPU/object churn, not bandwidth.
    ///
    /// The server spawns N networked prefabs scattered around the requesting client, which forces the
    /// CLIENT to instantiate the whole batch at once — the same ZNetScene path FGN time-slices
    /// (InstantiationBudgetMs / MaxInstancesPerFrame), i.e. the teleport-into-a-megabase scenario. The
    /// client samples its frame time across the instantiation and reports the worst hitch + fps, so you
    /// can see whether the time-slicing keeps it smooth or where it breaks. The server cleans the
    /// objects up afterward (best-effort, networked) so the test world isn't left flooded.
    /// </summary>
    [HarmonyPatch]
    public static class ZdoFloodTest
    {
        private const string RpcStart   = "FGN_ZdoFloodStart";    // client -> server (count, prefab, pos)
        private const string RpcMeasure = "FGN_ZdoFloodMeasure";  // server -> client (windowSecs, count)
        private const string RpcMsg     = "FGN_ZdoFloodMsg";      // server -> client (text)

        private const int DefaultCount = 500;
        private const int MaxCount = 20000;
        private const string DefaultPrefab = "Wood";
        private const float HitchMs = 50f;          // a frame slower than this counts as a hitch (~20 fps)
        private const int SpawnPerFrame = 100;      // server spawns in chunks so its own frame doesn't hard-stall

        private static bool s_registered;
        private static bool s_busy;
        private static readonly List<GameObject> s_spawned = new List<GameObject>();
        private static bool s_sampling;

        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        static void OnZNetStart()
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.Register<int, string, Vector3>(RpcStart, RPC_Start);
            ZRoutedRpc.instance.Register<float, int>(RpcMeasure, RPC_Measure);
            ZRoutedRpc.instance.Register<string>(RpcMsg, RPC_Msg);

            if (s_registered) return;
            s_registered = true;
            new Terminal.ConsoleCommand("fgn_zdoflood",
                "[count] [prefab] — FGN stress (admin): the server spawns N networked prefabs around you "
                + "(default 500 Wood) so your client must instantiate the whole batch at once — the "
                + "teleport-into-a-megabase path. Your client reports its worst frame hitch + fps; the "
                + "server cleans the objects up after. Pass a static piece prefab for a pure-instantiation test.",
                new Terminal.ConsoleEvent(OnCommand));
        }

        private static void OnCommand(Terminal.ConsoleEventArgs args)
        {
            int count = DefaultCount;
            string prefab = DefaultPrefab;
            if (args.Length >= 2) int.TryParse(args[1], out count);
            if (args.Length >= 3) prefab = args[2];
            count = Mathf.Clamp(count, 1, MaxCount);
            if (ZNet.instance == null || ZRoutedRpc.instance == null || Player.m_localPlayer == null)
            { args.Context?.AddString("FGN: not connected / no local player."); return; }

            Vector3 pos = Player.m_localPlayer.transform.position;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcStart, count, prefab, pos);
            args.Context?.AddString($"FGN zdoflood: asked the server to spawn {count} x {prefab} around you. Watch for the hitch report.");
        }

        // ---- server: spawn the batch around the requester ----
        private static void RPC_Start(long sender, int count, string prefabName, Vector3 pos)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!ServerClientUtils.IsAdmin(sender)) { Msg(sender, "FGN zdoflood denied — admin only."); return; }
            if (s_busy) { Msg(sender, "FGN zdoflood already running."); return; }
            count = Mathf.Clamp(count, 1, MaxCount);

            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            if (prefab == null) { Msg(sender, $"FGN zdoflood: prefab '{prefabName}' not found in ZNetScene — pick a valid networked prefab."); return; }
            if (prefab.GetComponent<ZNetView>() == null) { Msg(sender, $"FGN zdoflood: '{prefabName}' has no ZNetView (not networked) — can't flood with it."); return; }

            if (FiresGhettoNetworkMod.Instance != null)
                FiresGhettoNetworkMod.Instance.StartCoroutine(SpawnFlood(sender, count, prefab, prefabName, pos));
        }

        private static IEnumerator SpawnFlood(long target, int count, GameObject prefab, string prefabName, Vector3 center)
        {
            s_busy = true;
            CleanupNow();   // clear any leftovers from a prior run

            // Window scales with count so a big batch is measured long enough to finish syncing + instantiating.
            float window = Mathf.Clamp(10f + count / 500f, 10f, 60f);
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcMeasure, window, count);   // client starts sampling now
            Msg(target, $"[ZdoFlood] spawning {count} x {prefabName} around you...");

            // Scatter over a disc whose radius grows with count so they don't stack into a physics pile.
            float radius = Mathf.Clamp(Mathf.Sqrt(count) * 2f, 8f, 200f);
            int inFrame = 0;
            for (int i = 0; i < count; i++)
            {
                Vector2 r = Random.insideUnitCircle * radius;
                Vector3 p = center + new Vector3(r.x, 0f, r.y);
                GameObject go = Object.Instantiate(prefab, p, Quaternion.identity);
                if (go != null) s_spawned.Add(go);
                if (++inFrame >= SpawnPerFrame) { inFrame = 0; yield return null; }
            }
            Msg(target, $"[ZdoFlood] {s_spawned.Count} spawned — your client is instantiating them now.");

            // Clean up well after the client's measurement window so cleanup doesn't skew the reading.
            yield return new WaitForSeconds(window + 7f);
            int cleaned = CleanupNow();
            Msg(target, $"[ZdoFlood] cleaned up {cleaned} objects.");
            s_busy = false;
        }

        // Best-effort networked destroy — claim ownership first so the server can remove ZDOs a nearby
        // client may have taken over (same pattern vanilla/Guild uses before mutating a foreign ZDO).
        private static int CleanupNow()
        {
            int destroyed = 0;
            foreach (var go in s_spawned)
            {
                if (go == null) continue;
                var nv = go.GetComponent<ZNetView>();
                if (nv != null && nv.IsValid())
                {
                    if (!nv.IsOwner()) nv.ClaimOwnership();
                    ZNetScene.instance.Destroy(go);
                    destroyed++;
                }
                else Object.Destroy(go);
            }
            s_spawned.Clear();
            return destroyed;
        }

        // ---- client: sample frame time across the instantiation ----
        private static void RPC_Measure(long sender, float windowSecs, int count)
        {
            if (s_sampling) return;
            if (FiresGhettoNetworkMod.Instance != null)
                FiresGhettoNetworkMod.Instance.StartCoroutine(SampleFrames(windowSecs, count));
        }

        private static IEnumerator SampleFrames(float windowSecs, int count)
        {
            s_sampling = true;
            float worstMs = 0f, sumMs = 0f;
            int frames = 0, hitches = 0;
            float end = Time.unscaledTime + windowSecs;
            while (Time.unscaledTime < end)
            {
                yield return null;
                float ms = Time.unscaledDeltaTime * 1000f;
                sumMs += ms; frames++;
                if (ms > worstMs) worstMs = ms;
                if (ms > HitchMs) hitches++;
            }
            float avgMs = frames > 0 ? sumMs / frames : 0f;
            float avgFps = avgMs > 0.01f ? 1000f / avgMs : 0f;
            string verdict = worstMs < 100f
                ? "Time-slicing held it smooth."
                : (worstMs < 250f ? "Noticeable hitch — instantiation is pushing the per-frame budget."
                                  : "Big freeze — instantiation outran the budget at this count.");
            string msg = $"[ZdoFlood] client over {windowSecs:F0}s (~{count} objs): worst frame {worstMs:F0} ms, "
                + $"{hitches} hitches (>{HitchMs:F0} ms), avg {avgFps:F0} fps. {verdict}";
            if (Console.instance != null) Console.instance.AddString(msg);
            LoggerOptions.LogMessage(msg);
            s_sampling = false;
        }

        private static void RPC_Msg(long sender, string msg) => AdminConsoleEcho.Print(msg);

        // ---- helpers ----
        private static void Msg(long target, string msg) => AdminConsoleEcho.Send(RpcMsg, target, msg);

    }
}
