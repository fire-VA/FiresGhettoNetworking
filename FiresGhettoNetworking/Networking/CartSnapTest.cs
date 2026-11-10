using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.5.86 (Fire 10-27, carts wedged on wall hangers, inside buildings, up ramps): 'fgn_cartsnap_test' (F5, local player, admin world).
    /// Spawns six Carts near the player, each kinematic so physics can't move it and only the snap decides: on a wood floor 2 m up,
    /// rotated 90 deg and wedged between two walls (hung), on a ramp, upright inside a below-ground pocket touching a wall, and two on bare terrain with the deepest wheel 0.3 m and 0.7 m
    /// under it (Fire: 0.5 m is the line). Runs each cart's own Floating.TerrainCheck (the vanilla 30 s push-up that FGN's GroundSnapPatches redirect),
    /// then checks the height: only the 0.7 m one may be lifted. Removes everything it spawned.
    /// </summary>
    [HarmonyPatch]
    internal static class CartSnapTest
    {
        private static bool s_added;

        [HarmonyPatch(typeof(Terminal), "InitTerminal"), HarmonyPostfix]
        private static void AddCommand()
        {
            if (s_added) return;
            s_added = true;
            new Terminal.ConsoleCommand("fgn_cartsnap_test", "[F5 test] [FGN] fgn_cartsnap_test - carts on a floor 2 m up / hung 90 deg between walls / on a ramp / below ground against a wall / a wheel 0.3 m into terrain must NOT be pushed up; a wheel 0.7 m into terrain must be (Fire: half a meter). Spawns 6 kinematic carts, runs each one's push-up check, removes everything.",
                args => { if (FiresGhettoNetworkMod.Instance != null) FiresGhettoNetworkMod.Instance.StartCoroutine(Run(args.Context)); }, isCheat: true);
        }

        private sealed class Case { internal string Name; internal GameObject Cart; internal bool ShouldMove; internal float Y0; }

        private static IEnumerator Run(Terminal t)
        {
            Player me = Player.m_localPlayer;
            ZNetScene scene = ZNetScene.instance;
            float t0 = Time.realtimeSinceStartup;
            Say(t, "BEGIN");
            if (me == null || scene == null || scene.GetPrefab("Cart") == null) { Say(t, "1/1 setup FAIL - no player / scene / Cart prefab"); Say(t, "END FAIL: 0 pass, 1 fail, 0 skip in 0 s"); yield break; }
            var made = new List<GameObject>();
            var cases = new List<Case>();
            Vector3 fwd = me.transform.forward; fwd.y = 0f; fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 basePos = me.transform.position + fwd * 8f;
            float Ground(Vector3 p) => ZoneSystem.instance.GetGroundHeight(p);
            GameObject Spawn(string prefab, Vector3 at, Quaternion rot) { var go = Object.Instantiate(scene.GetPrefab(prefab), at, rot); made.Add(go); return go; }

            // 1: on a wood floor 2 m up
            Vector3 a = basePos - right * 8f; a.y = Ground(a) + 2f;
            Spawn("wood_floor", a, Quaternion.identity);
            cases.Add(new Case { Name = "on a wood floor 2 m up", Cart = Spawn("Cart", a + Vector3.up * 0.6f, Quaternion.LookRotation(fwd)), ShouldMove = false });
            // 2: rotated 90 deg, wedged between two walls (hung on purpose)
            Vector3 b = basePos - right * 3f; b.y = Ground(b) + 1.2f;
            Spawn("wood_wall", b + right * 0.9f + Vector3.up * 0.3f, Quaternion.LookRotation(right));
            Spawn("wood_wall", b - right * 0.9f + Vector3.up * 0.3f, Quaternion.LookRotation(right));
            cases.Add(new Case { Name = "rotated 90 deg, wedged between two walls", Cart = Spawn("Cart", b, Quaternion.LookRotation(fwd) * Quaternion.Euler(0f, 0f, 90f)), ShouldMove = false });
            // 3: on a ramp
            Vector3 c = basePos + right * 2f; c.y = Ground(c) + 0.1f;
            Spawn("wood_stair", c, Quaternion.LookRotation(fwd));
            cases.Add(new Case { Name = "on a ramp", Cart = Spawn("Cart", c + Vector3.up * 1.4f, Quaternion.LookRotation(fwd)), ShouldMove = false });
            // 4: upright 2 m below the terrain, touching a wall (a cellar), nothing under it
            Vector3 d = basePos + right * 7f; d.y = Ground(d) - 2f;
            Spawn("wood_wall", d + fwd * 1.2f, Quaternion.LookRotation(fwd));
            cases.Add(new Case { Name = "below ground, wedged against a wall", Cart = Spawn("Cart", d, Quaternion.LookRotation(fwd)), ShouldMove = false });
            // 5 + 6 (Fire: "only if the wheel is below the terrain by at least half a meter"): bare terrain, the deepest wheel set to
            // 0.3 m (must not move) and 0.7 m (must lift) below the terrain under it
            Vector3 e = basePos + right * 12f + fwd * 6f; e.y = Ground(e) + 0.5f;
            var shallow = new Case { Name = "a wheel 0.3 m into bare terrain", Cart = Spawn("Cart", e, Quaternion.LookRotation(fwd)), ShouldMove = false };
            Vector3 g = basePos + right * 12f - fwd * 6f; g.y = Ground(g) + 0.5f;
            var deep = new Case { Name = "a wheel 0.7 m into bare terrain", Cart = Spawn("Cart", g, Quaternion.LookRotation(fwd)), ShouldMove = true };
            cases.Add(shallow); cases.Add(deep);

            foreach (Case k in cases) { var rb = k.Cart.GetComponent<Rigidbody>(); if (rb != null) rb.isKinematic = true; }
            yield return new WaitForSeconds(2f);   // let the pieces' colliders and the zone settle
            Sink(shallow.Cart, 0.3f); Sink(deep.Cart, 0.7f);
            yield return new WaitForFixedUpdate();
            var check = AccessTools.Method(typeof(Floating), "TerrainCheck");
            int pass = 0, fail = 0, step = 0;
            foreach (Case k in cases)
            {
                Floating f = k.Cart != null ? k.Cart.GetComponent<Floating>() : null;
                if (f == null || check == null) { fail++; Say(t, $"{++step}/{cases.Count} {k.Name} FAIL - no Floating on the Cart"); continue; }
                k.Y0 = k.Cart.transform.position.y;
                check.Invoke(f, null);
                float dy = k.Cart.transform.position.y - k.Y0;
                bool moved = Mathf.Abs(dy) > 0.05f;
                bool ok = moved == k.ShouldMove;
                if (ok) pass++; else fail++;
                Say(t, $"{++step}/{cases.Count} {k.Name} {(ok ? "PASS" : "FAIL")} - {(moved ? $"pushed up {dy:0.0} m" : "not moved")} (expected {(k.ShouldMove ? "pushed up" : "not moved")})");
            }
            foreach (GameObject go in made)
            {
                if (go == null) continue;
                var nv = go.GetComponent<ZNetView>();
                if (nv != null && nv.IsValid()) { nv.ClaimOwnership(); scene.Destroy(go); } else Object.Destroy(go);
            }
            Say(t, $"END {(fail == 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, 0 skip in {Time.realtimeSinceStartup - t0:0} s (spawned pieces and carts removed)");
        }

        // Moves the cart so its deepest wheel's lowest point is exactly 'depth' below the terrain under that wheel.
        private static void Sink(GameObject cartGo, float depth)
        {
            Vagon cart = cartGo != null ? cartGo.GetComponent<Vagon>() : null;
            if (cart == null || cart.m_wheels == null) return;
            float shift = float.MinValue;   // the largest move that brings EVERY wheel to depth or shallower = the deepest wheel exactly at depth
            foreach (Rigidbody wheel in cart.m_wheels)
            {
                if (wheel == null) continue;
                float bottom = float.MaxValue;
                foreach (Collider c in wheel.GetComponentsInChildren<Collider>()) if (c != null && !c.isTrigger) bottom = Mathf.Min(bottom, c.bounds.min.y);
                if (bottom == float.MaxValue) bottom = wheel.position.y;
                shift = Mathf.Max(shift, (ZoneSystem.instance.GetGroundHeight(wheel.position) - depth) - bottom);
            }
            if (shift == float.MinValue) return;
            cartGo.transform.position += Vector3.up * shift;
            var rb = cartGo.GetComponent<Rigidbody>(); if (rb != null) rb.position = cartGo.transform.position;
            Physics.SyncTransforms();
        }

        private static void Say(Terminal t, string s)
        {
            LoggerOptions.LogDebug("[CartSnapTest] " + s);
            t?.AddString("[CartSnapTest] " + s);
        }
    }
}
