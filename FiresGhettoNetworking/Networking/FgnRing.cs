using System;
using System.Reflection;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.5.78 ([fgn] 10-24, halo ring zone-key root fix): FGN's one view of FAT's ring OBJECT sectors (Tools\PLAN_halo_zone_keys_20261024.md).
    /// On the HaloFlat ring FAT files every ZDO by ring sector (unrolled around/along the ring) instead of the x,z column; terrain
    /// zones stay x,z. FGN's own object queries, ownership and active-area tests therefore ask Sector(pos), not ZoneSystem.GetZone(pos).
    /// Soft: "VerdantsAscent.FatApi" HaloZonesOn / HaloZone / HaloZoneDelta / HaloSurfaceDistance (add-only), bound once by name as
    /// delegates. FAT missing, older, or any world but HaloFlat = vanilla answers, so normal worlds behave exactly as before.
    /// </summary>
    internal static class FgnRing
    {
        private static bool s_looked;
        private static Func<bool> s_on;
        private static Func<Vector3, Vector2s> s_zone;
        private static Func<Vector2s, Vector2s, Vector2s> s_delta;
        private static Func<Vector3, Vector3, float> s_dist;

        private static void Look()
        {
            if (s_looked) return;
            s_looked = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("VerdantsAscent.FatApi", false);
                    if (t == null) continue;
                    const BindingFlags S = BindingFlags.Public | BindingFlags.Static;
                    var on = t.GetProperty("HaloZonesOn", S)?.GetGetMethod();
                    var zone = t.GetMethod("HaloZone", S, null, new[] { typeof(Vector3) }, null);
                    var delta = t.GetMethod("HaloZoneDelta", S, null, new[] { typeof(Vector2s), typeof(Vector2s) }, null);
                    var dist = t.GetMethod("HaloSurfaceDistance", S, null, new[] { typeof(Vector3), typeof(Vector3) }, null);
                    if (on == null || zone == null || delta == null || dist == null) break;   // older FAT: stay vanilla
                    s_on = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), on);
                    s_zone = (Func<Vector3, Vector2s>)Delegate.CreateDelegate(typeof(Func<Vector3, Vector2s>), zone);
                    s_delta = (Func<Vector2s, Vector2s, Vector2s>)Delegate.CreateDelegate(typeof(Func<Vector2s, Vector2s, Vector2s>), delta);
                    s_dist = (Func<Vector3, Vector3, float>)Delegate.CreateDelegate(typeof(Func<Vector3, Vector3, float>), dist);
                    LoggerOptions.LogInfo("[FGN halo] bound FAT's ring object sectors (FatApi.HaloZone / HaloZoneDelta / HaloSurfaceDistance)");
                    break;
                }
            }
            catch (Exception) { s_on = null; s_zone = null; s_delta = null; s_dist = null; }
        }

        /// <summary>FAT's ring object sectors are active in this world.</summary>
        // 1.5.79 ([wishbone]'s audit): On is asked inside per-ZDO loops; the answer only changes when a world loads, so it is
        // cached for 250 ms (Environment.TickCount: safe off the main thread, unlike Time.frameCount).
        private static int s_onAt = int.MinValue;
        private static bool s_onCached;

        internal static bool On
        {
            get
            {
                int now = Environment.TickCount;
                if (unchecked(now - s_onAt) < 250 && s_onAt != int.MinValue) return s_onCached;
                Look();
                bool on = false;
                if (s_on != null) { try { on = s_on(); } catch (Exception) { on = false; } }
                s_onCached = on; s_onAt = now;
                return on;
            }
        }

        /// <summary>Up at p: the halo ring's up inside its field (FatApi.HaloTryGetUp), world up elsewhere. One bound delegate, no allocation.</summary>
        internal static Vector3 Up(Vector3 p) => -Down(p);

        /// <summary>The object sector around p (ring sector on the ring, ZoneSystem.GetZone elsewhere): for ZDOMan queries,
        /// ownership and active-area tests. Terrain-zone code (IsZoneLoaded, m_zones) keeps ZoneSystem.GetZone.</summary>
        internal static Vector2s Sector(Vector3 p)
        {
            if (!On) return ZoneSystem.GetZone(p);
            try { return s_zone(p); } catch (Exception) { return ZoneSystem.GetZone(p); }
        }

        /// <summary>b - a in sector ids, wrapped at the ring's seam.</summary>
        internal static Vector2s Delta(Vector2s a, Vector2s b)
        {
            if (!On) return new Vector2s(b.x - a.x, b.y - a.y);
            try { return s_delta(a, b); } catch (Exception) { return new Vector2s(b.x - a.x, b.y - a.y); }
        }

        private delegate bool TryUp(Vector3 pos, out Vector3 up);
        private static bool s_upLooked;
        private static TryUp s_tryUp;

        /// <summary>1.5.78 ([seasons]' audit): "down" at p for projectile gravity: away from the halo ring's axis inside its field
        /// (FatApi.HaloTryGetUp, the same field Core's GravityUp and projectiles use), world down everywhere else.</summary>
        internal static Vector3 Down(Vector3 p)
        {
            if (!s_upLooked)
            {
                s_upLooked = true;
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var m = asm.GetType("VerdantsAscent.FatApi", false)?.GetMethod("HaloTryGetUp", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vector3), typeof(Vector3).MakeByRefType() }, null);
                        if (m == null) continue;
                        s_tryUp = (TryUp)Delegate.CreateDelegate(typeof(TryUp), m);
                        break;
                    }
                }
                catch (Exception) { s_tryUp = null; }
            }
            if (s_tryUp == null) return Vector3.down;
            try { return s_tryUp(p, out Vector3 up) ? -up : Vector3.down; } catch (Exception) { return Vector3.down; }
        }

        /// <summary>Squared distance for send priority: along the ring surface on the ring, x,z (vanilla FGN) elsewhere.</summary>
        internal static float PriorityDistanceSqr(Vector3 a, Vector3 b) => PriorityDistanceSqr(a, b, On);

        /// <summary>1.5.79: the same with On read once by the caller outside its per-ZDO loop.</summary>
        internal static float PriorityDistanceSqr(Vector3 a, Vector3 b, bool ringOn)
        {
            if (ringOn)
            {
                try { float d = s_dist(a, b); return d * d; } catch (Exception) { }
            }
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }
    }
}
