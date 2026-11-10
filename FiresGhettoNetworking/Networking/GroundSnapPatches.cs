using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>Layer mask for "something you can stand on", shared by the ground-snap patches and the dedicated-server fell-out rescue.</summary>
    public static class SolidSurface
    {
        public const string DefaultLayerCsv = "Default,static_solid,Default_small,piece,terrain,vehicle";

        private static int _cachedMask;
        private static string _cachedLayerCsv;

        public static int Mask()
        {
            string layerCsv = FiresGhettoNetworkMod.ConfigDediFellOutRescueLayers?.Value ?? DefaultLayerCsv;
            if (_cachedLayerCsv == layerCsv && _cachedMask != 0) return _cachedMask;

            int mask = ParseLayerMask(layerCsv);
            if (mask == 0)
            {
                LoggerOptions.LogWarning($"[SolidSurface] '{layerCsv}' matched no layers in this build; falling back to terrain only.");
                mask = LayerMask.GetMask("terrain");
            }

            _cachedMask = mask;
            _cachedLayerCsv = layerCsv;
            return mask;
        }

        private static int ParseLayerMask(string layerCsv)
        {
            var layerNames = new List<string>();
            foreach (var entry in layerCsv.Split(','))
            {
                string layerName = entry?.Trim();
                if (!string.IsNullOrEmpty(layerName)) layerNames.Add(layerName);
            }
            return layerNames.Count > 0 ? LayerMask.GetMask(layerNames.ToArray()) : 0;
        }
    }

    /// <summary>
    /// Vanilla's "fallen out of the world" rescues measure against the heightmap, which cannot see build
    /// pieces, so anything resting on a piece below heightmap level is teleported to the dirt. Each rescue
    /// asks for the ground height once; these transpilers redirect that one call to a physics probe.
    /// </summary>
    [HarmonyPatch]
    public static class GroundSnapPatches
    {
        private const float ProbeStartHeightAboveObject = 2f;
        private const float ProbeMaxDistance = 8f;
        private const float MaxCartTiltDegrees = 45f;   // 1.5.86

        [HarmonyPatch(typeof(Character), "UnderWorldCheck")]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Character_UnderWorldCheck_UseSupportProbe(
            IEnumerable<CodeInstruction> instructions)
        {
            return RedirectGroundHeightToSupportProbe(instructions, "Character.UnderWorldCheck");
        }

        [HarmonyPatch(typeof(TombStone), "PositionCheck")]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> TombStone_PositionCheck_UseSupportProbe(
            IEnumerable<CodeInstruction> instructions)
        {
            return RedirectGroundHeightToSupportProbe(instructions, "TombStone.PositionCheck");
        }

        [HarmonyPatch(typeof(ItemDrop), "TerrainCheck")]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> ItemDrop_TerrainCheck_UseSupportProbe(
            IEnumerable<CodeInstruction> instructions)
        {
            return RedirectGroundHeightToSupportProbe(instructions, "ItemDrop.TerrainCheck");
        }

        [HarmonyPatch(typeof(Floating), "TerrainCheck")]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Floating_TerrainCheck_UseSupportProbe(
            IEnumerable<CodeInstruction> instructions)
        {
            return RedirectGroundHeightToSupportProbe(instructions, "Floating.TerrainCheck");
        }

        private static IEnumerable<CodeInstruction> RedirectGroundHeightToSupportProbe(
            IEnumerable<CodeInstruction> instructions, string patchedMethod)
        {
            var code = new List<CodeInstruction>(instructions);
            // 1.5.81: the patched methods are all instance methods of a Component, so `this` goes along and the support ray skips
            // the object's own colliders.
            var supportProbe = AccessTools.Method(typeof(GroundSnapPatches), nameof(SupportOrGroundHeightFor));
            int redirected = 0;

            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Call && code[i].opcode != OpCodes.Callvirt) continue;

                var called = code[i].operand as MethodInfo;
                if (called == null || called.Name != "GetGroundHeight" || called.GetParameters().Length != 1) continue;

                // ldarg.0 goes right before the call (its arguments are already on the stack); a label on the original call
                // moves to it, so a jump there still runs both
                var self = new CodeInstruction(OpCodes.Ldarg_0);
                self.labels.AddRange(code[i].labels);
                self.blocks.AddRange(code[i].blocks);
                code[i] = new CodeInstruction(OpCodes.Call, supportProbe);
                code.Insert(i, self);
                i++;
                redirected++;
            }

            if (redirected == 0)
                LoggerOptions.LogWarning(
                    $"[GroundSnap] {patchedMethod} has no GetGroundHeight call to redirect; vanilla behaviour stands there.");

            return code;
        }

        /// <summary>Stands in for ZoneSystem.GetGroundHeight(Vector3) at the redirected call sites, so the signature must match it.
        /// Kept for callers outside FGN; FGN's own sites use SupportOrGroundHeightFor.</summary>
        public static float SupportOrGroundHeight(ZoneSystem zoneSystem, Vector3 point) => SupportOrGroundHeightFor(zoneSystem, point, null);

        private static readonly RaycastHit[] s_hits = new RaycastHit[16];

        /// <summary>
        /// 1.5.81 (Fire 10-26, carts hop every 30 s once loaded): the support under <paramref name="self"/>, skipping self's own
        /// colliders. A parked cart's root sits a hair under the heightmap, so the ray below ran and hit the cart itself; once its
        /// load models (vehicle-layer colliders) switch on with a full inventory that hit is over 1 m above the root, and
        /// Floating.TerrainCheck lifted the cart onto itself every 30 s.
        /// </summary>
        public static float SupportOrGroundHeightFor(ZoneSystem zoneSystem, Vector3 point, Component self)
        {
            float heightmapHeight = zoneSystem.GetGroundHeight(point);
            if (!(FiresGhettoNetworkMod.ConfigFixGroundSnapThroughFloors?.Value ?? true)) return heightmapHeight;

            // 1.5.87 (Fire 10-27): CARTS decide here, before vanilla's "pivot more than 1 m under the heightmap" test, which can't express
            // Fire's rule ("Push-up should only run if the wheel is below the terrain by at least half a meter"). Vanilla is then handed the
            // cart's own height (no move) and FGN lifts the cart itself, by exactly the deepest wheel's depth.
            Vagon cart = self != null ? self.GetComponentInParent<Vagon>() : null;
            if (cart != null) return CartDecision(zoneSystem, cart, self, point);

            if (point.y >= heightmapHeight) return heightmapHeight;

            // 1.6.31: a character is measured against the terrain, as vanilla; see CharacterDecision.
            if (self is Character character) return CharacterDecision(point, heightmapHeight, character);

            // On a dedicated server the heightmap and the physics ray below both read the uncarved ground (a VoxelWrap dedi
            // has no voxel colliders); FiresAdminTerrain's edited density knows the pond or pit, where it is loaded.
            if (ServerClientUtils.ZNetIsDedicated() && VoxelSurfaceProbe.TryGetSupport(point, out float voxelGround)) return voxelGround;

            Transform own = self != null ? self.transform : null;
            int n = Physics.RaycastNonAlloc(point + Vector3.up * ProbeStartHeightAboveObject, Vector3.down, s_hits,
                                            ProbeMaxDistance, SolidSurface.Mask(), QueryTriggerInteraction.Ignore);
            float best = float.MaxValue, bestY = heightmapHeight;
            for (int i = 0; i < n; i++)
            {
                var c = s_hits[i].collider;
                if (c == null || (own != null && c.transform.IsChildOf(own))) continue;
                if (s_hits[i].distance < best) { best = s_hits[i].distance; bestY = s_hits[i].point.y; }
            }
            if (best < float.MaxValue) { Why(self, point, $"skip: resting on {s_hits[0].collider?.name ?? "a surface"} at {bestY:0.0}, not the terrain"); return bestY; }

            // 1.5.86 (Fire 10-27: "players purposely wedge carts onto hangers on the wall ... bring them inside of buildings or barns or
            // up ramps ... the push-up logic ... should only ever run if the cart is sitting directly on terrain"): nothing under it, but
            // it touches a build piece (wedged between walls, hung on a rack): leave it where the player put it.
            string piece = TouchingPiece(self, point, own);
            if (piece != null) { Why(self, point, $"skip: wedged against piece {piece}, not on the terrain"); return point.y; }
            Why(self, point, $"snapped: sunk {heightmapHeight - point.y:0.0} m into the terrain, nothing under it");
            return heightmapHeight;
        }

        private const float VanillaUnderWorldMargin = 1f;   // Character.UnderWorldCheck: lifted when more than this under the ground height
        private const float FloorHoldSeconds = 5f;
        private const float FloorHoldGapSeconds = 0.5f;
        private static float s_floorSince, s_floorLastSeen;
        private static int s_terrainMask;

        // 1.6.31 ([fgn], player report 11-05): this probe could stop vanilla's rescue for good. A piece touching the bounds gave no lift and
        // an 8 m ray hit gave a height near the character, so a player or tame under a base stayed there. The ground height vanilla reads is
        // the terrain collider, built from the EDITED heights, so a dug basement is not "under" it and needs no help. In order:
        //  * within vanilla's margin: vanilla would not lift, and no ray can raise the answer to a floor overhead;
        //  * carved ground (voxel density on a dedicated server, a non-heightmap terrain collider on a client) IS the terrain there;
        //  * the local player standing on a piece: held up to FloorHoldSeconds, for edits still loading in (the collider shows the
        //    unedited ground over a basement until TerrainComp pokes the tile). Then lifted, so a fall onto a sunken foundation ends too;
        //  * everything else, tames included: vanilla's lift.
        private static float CharacterDecision(Vector3 point, float terrain, Character character)
        {
            float depth = terrain - point.y;
            if (depth <= VanillaUnderWorldMargin)
            {
                if (LoggerOptions.DebugEnabled) Why(character, point, $"skip: only {depth:0.0} m under the terrain, within vanilla's {VanillaUnderWorldMargin:0} m");
                return terrain;
            }

            if (ServerClientUtils.ZNetIsDedicated() && VoxelSurfaceProbe.TryGetSupport(point, out float voxelGround))
            {
                if (LoggerOptions.DebugEnabled) Why(character, point, $"skip: carved voxel ground at {voxelGround:0.0}, not the uncarved {terrain:0.0}");
                return voxelGround;
            }
            if (CarvedGroundUnder(point, out float carved))
            {
                if (LoggerOptions.DebugEnabled) Why(character, point, $"skip: carved terrain collider at {carved:0.0}, under the surface at {terrain:0.0}");
                return carved;
            }

            float stood = 0f;
            if (character.IsPlayer() && character.IsOnGround() && character.m_lastGroundCollider != null)
            {
                WearNTear floor = character.m_lastGroundCollider.GetComponentInParent<WearNTear>();
                if (floor != null)
                {
                    float now = Time.time;
                    if (now - s_floorLastSeen > FloorHoldGapSeconds) s_floorSince = now;
                    s_floorLastSeen = now;
                    stood = now - s_floorSince;
                    if (stood < FloorHoldSeconds)
                    {
                        if (LoggerOptions.DebugEnabled) Why(character, point, $"hold: standing on piece {floor.name.Replace("(Clone)", "")}, {depth:0.0} m under the terrain; lifted after {FloorHoldSeconds:0} s if the terrain does not drop");
                        return point.y;
                    }
                }
            }
            Why(character, point, $"snapped: {depth:0.0} m under the terrain at {terrain:0.0}" + (stood > 0f ? $", stood on a piece for {stood:0.0} s" : ""));
            return terrain;
        }

        // Ground on the terrain layer under the character that is not a heightmap tile. Under plain heightmap terrain the ray finds none.
        private static bool CarvedGroundUnder(Vector3 point, out float y)
        {
            y = 0f;
            if (s_terrainMask == 0) s_terrainMask = LayerMask.GetMask("terrain");
            int n = Physics.RaycastNonAlloc(point + Vector3.up * 0.5f, Vector3.down, s_hits, ProbeMaxDistance, s_terrainMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var c = s_hits[i].collider;
                if (c == null || c.GetComponent<Heightmap>() != null) continue;
                if (s_hits[i].distance < best) { best = s_hits[i].distance; y = s_hits[i].point.y; }
            }
            return best < float.MaxValue;
        }

        // A cart, in order (every probe WORLD down):
        //  1. tilted more than MaxCartTiltDegrees from upright (Fire: hung on a wall "rotated 90°") -> never moved;
        //  2. touching a build piece (wedged between walls, on a rack, inside a building) -> never moved;
        //  3. each WHEEL (Vagon.m_wheels; handles and body never count): standing on a piece under it -> not sunk; else its depth =
        //     the terrain under it minus its lowest point. The deepest wheel >= 'Cart push-up wheel depth (m)' (Fire: 0.5) -> FGN lifts the
        //     cart by that depth (+ 5 cm); less -> no action.
        // Always returns the cart's own height, so vanilla's own lift never runs on a cart.
        private static float CartDecision(ZoneSystem zoneSystem, Vagon cart, Component self, Vector3 point)
        {
            Transform own = cart.transform;
            float tilt = Vector3.Angle(own.up, Vector3.up);
            if (tilt > MaxCartTiltDegrees) { Why(self, point, $"skip: tilted {tilt:0} deg from upright (hung or wedged on purpose)"); return point.y; }
            string piece = TouchingPiece(cart, point, own);
            if (piece != null) { Why(self, point, $"skip: resting on or wedged against piece {piece}"); return point.y; }
            if (cart.m_wheels == null || cart.m_wheels.Length == 0) { Why(self, point, "skip: no wheels to measure"); return point.y; }

            float need = FiresGhettoNetworkMod.ConfigCartPushUpWheelDepth?.Value ?? 0.5f;
            float deepest = 0f;
            var line = new System.Text.StringBuilder();
            for (int w = 0; w < cart.m_wheels.Length; w++)
            {
                Rigidbody wheel = cart.m_wheels[w];
                if (wheel == null) continue;
                string side = w == 0 ? "L" : w == 1 ? "R" : w.ToString();
                if (WheelOnPiece(wheel, own, out string under)) { line.Append($" wheel {side} on piece {under} -> skip;"); continue; }
                float below = zoneSystem.GetGroundHeight(wheel.position) - WheelBottom(wheel);
                line.Append($" wheel {side} {below:0.00} m below terrain -> {(below >= need ? "snap" : "skip")};");
                deepest = Mathf.Max(deepest, below);
            }
            if (deepest < need) { Why(self, point, $"skip:{line} (needs {need:0.0} m)"); return point.y; }

            Vector3 up = Vector3.up * (deepest + 0.05f);
            own.position += up;
            var body = cart.GetComponent<Rigidbody>();
            if (body != null) { body.position += up; body.linearVelocity = Vector3.zero; }
            Why(self, point, $"snapped:{line} lifted {deepest + 0.05f:0.00} m");
            return own.position.y;   // vanilla: pos.y - this = 0, no second lift
        }

        // A build piece right under the wheel (ray from the wheel's centre, world down, to just past its bottom).
        private static bool WheelOnPiece(Rigidbody wheel, Transform own, out string name)
        {
            name = null;
            float reach = wheel.position.y - WheelBottom(wheel) + 0.3f;
            int n = Physics.RaycastNonAlloc(wheel.position, Vector3.down, s_hits, reach, SolidSurface.Mask(), QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = s_hits[i].collider;
                if (c == null || c.transform.IsChildOf(own)) continue;
                var wnt = c.GetComponentInParent<WearNTear>();
                if (wnt != null) { name = wnt.name.Replace("(Clone)", ""); return true; }
            }
            return false;
        }

        // The wheel's lowest world point: its solid colliders' bounds, else its rigidbody position.
        private static float WheelBottom(Rigidbody wheel)
        {
            float min = float.MaxValue;
            foreach (Collider c in wheel.GetComponentsInChildren<Collider>())
                if (c != null && !c.isTrigger) min = Mathf.Min(min, c.bounds.min.y);
            return min < float.MaxValue ? min : wheel.position.y;
        }

        private static readonly Collider[] s_touch = new Collider[16];
        private static int s_pieceMask;

        // A build piece (WearNTear) within its own bounds (+ 0.3 m) of the object, its own colliders left out; null when none.
        private static string TouchingPiece(Component self, Vector3 point, Transform own)
        {
            if (s_pieceMask == 0) s_pieceMask = LayerMask.GetMask("piece", "piece_nonsolid", "Default", "static_solid");
            Vector3 centre = point, half = Vector3.one * 0.6f;
            if (self != null)
            {
                bool any = false; Bounds b = default;
                foreach (Collider c in self.GetComponentsInChildren<Collider>())
                {
                    if (c == null || c.isTrigger) continue;
                    if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
                }
                if (any) { centre = b.center; half = b.extents + Vector3.one * 0.3f; }
            }
            int n = Physics.OverlapBoxNonAlloc(centre, half, s_touch, Quaternion.identity, s_pieceMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = s_touch[i];
                if (c == null || (own != null && c.transform.IsChildOf(own))) continue;
                var wnt = c.GetComponentInParent<WearNTear>();
                if (wnt != null) return wnt.name.Replace("(Clone)", "");
            }
            return null;
        }

        // One Debug line per object per decision change, tagged [CartSnap] for carts (Vagon) and [GroundSnap] for everything else.
        private static readonly Dictionary<int, string> s_lastWhy = new Dictionary<int, string>();
        private static void Why(Component self, Vector3 point, string what)
        {
            if (self == null) return;
            int id = self.GetInstanceID();
            string kind = what.StartsWith("snapped") ? "snapped" : what.Substring(0, what.IndexOf(':') + 1) + what.Split(' ')[1];
            if (s_lastWhy.TryGetValue(id, out string last) && last == kind) return;
            s_lastWhy[id] = kind;
            bool cart = self.GetComponentInParent<Vagon>() != null;
            LoggerOptions.LogDebug($"[{(cart ? "CartSnap" : "GroundSnap")}] {self.name.Replace("(Clone)", "")} at ({point.x:0.0},{point.y:0.0},{point.z:0.0}) {what}");
        }
    }
}
