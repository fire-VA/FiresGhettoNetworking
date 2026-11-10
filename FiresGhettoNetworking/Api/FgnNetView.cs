using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace FiresGhettoNetworkMod.Api
{
    /// <summary>
    /// 1.6.11 FgnApi v5 "network view" (Fire 10-29: "see which player owns the zone and maybe some of their connection info or ping ...
    /// add apis to ghetto networking so that we can tap into this info with our god complex"). Read-only, server-side, JSON strings
    /// so God Complex / Studio Link reflect them without sharing types. No per-frame work: peer stats come from the sockets' own
    /// counters on each call; zone rows are cached 2 s per zone. Zone owner = the owner of the zone's _ZoneCtrl ZDO (the peer whose
    /// game runs that zone's spawners and simulation; the server under Server-Side Simulation).
    /// </summary>
    [HarmonyLib.HarmonyPatch]
    internal static class FgnNetView
    {
        private const int MaxRadius = 10;
        private const int ScanRadius = 5;   // server-held zones are looked for this many zones around each peer
        private const float ZoneCacheSeconds = 2f;
        private static readonly int s_zoneCtrlHash = "_ZoneCtrl".GetStableHashCode();
        private struct ZoneRow { public float At; public string Json; }
        private static readonly Dictionary<long, ZoneRow> s_zoneCache = new Dictionary<long, ZoneRow>();
        private static readonly Dictionary<long, int> s_counts = new Dictionary<long, int>();

        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        internal static string PeerNetStats()
        {
            var sb = new StringBuilder("[");
            ZNet n = ZNet.instance;
            if (n != null)
            {
                bool first = true;
                foreach (ZNetPeer p in n.GetConnectedPeers())
                {
                    if (p == null) continue;
                    int ping = -1, queue = 0; float o = 0f, i = 0f;
                    ISocket s = p.m_socket;
                    try { if (s != null) { s.GetConnectionQuality(out _, out _, out ping, out o, out i); queue = s.GetSendQueueSize(); } } catch { }
                    string type = s?.GetType().Name ?? "";
                    string platform = type.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) >= 0 ? "Steam" : type.IndexOf("PlayFab", StringComparison.OrdinalIgnoreCase) >= 0 ? "PlayFab" : "other";
                    string quality = ping < 0 ? "unknown" : (ping > 250 || queue > 512 * 1024) ? "poor" : (ping < 100 && queue < 64 * 1024) ? "good" : "fair";
                    if (!first) sb.Append(','); first = false;
                    sb.Append("{\"peerId\":").Append(p.m_uid).Append(",\"name\":\"").Append(Esc(p.m_playerName)).Append("\",\"platform\":\"").Append(platform)
                      .Append("\",\"pingMs\":").Append(ping).Append(",\"outKBps\":").Append(F(o / 1024f)).Append(",\"inKBps\":").Append(F(i / 1024f))
                      .Append(",\"sendQueueBytes\":").Append(queue).Append(",\"compressionRatio\":null,\"backoff\":null,\"tier\":null,\"quality\":\"").Append(quality).Append("\"}");
                }
            }
            return sb.Append(']').ToString();
        }

        internal static string ZoneOwners(int cx, int cz, int radius)
        {
            radius = Mathf.Clamp(radius, 0, MaxRadius);
            var sb = new StringBuilder("[");
            ZDOMan zm = ZDOMan.instance;
            if (zm != null && ZNet.instance != null)
            {
                float now = Time.realtimeSinceStartup; bool first = true;
                for (int z = cz - radius; z <= cz + radius; z++)
                    for (int x = cx - radius; x <= cx + radius; x++)
                    {
                        long key = ((long)x << 32) ^ (uint)z;
                        if (!s_zoneCache.TryGetValue(key, out ZoneRow row) || now - row.At > ZoneCacheSeconds)
                            s_zoneCache[key] = row = new ZoneRow { At = now, Json = BuildZone(zm, x, z) };
                        if (row.Json == null) continue;
                        if (!first) sb.Append(','); first = false;
                        sb.Append(row.Json);
                    }
                if (s_zoneCache.Count > 4096) s_zoneCache.Clear();
            }
            return sb.Append(']').ToString();
        }

        private static string BuildZone(ZDOMan zm, int x, int z)
        {
            List<ZDO> objects;
            try
            {
                uint idx = ZoneSystem.SectorToIndex(x, z).Sector;
                if (zm.m_objectsBySector == null || idx >= (uint)zm.m_objectsBySector.Length) return null;
                objects = zm.m_objectsBySector[idx];
            }
            catch { return null; }
            s_counts.Clear();
            long ctrlOwner = 0;
            if (objects != null)
                for (int i = 0; i < objects.Count; i++)
                {
                    ZDO zdo = objects[i]; if (zdo == null) continue;
                    long owner = zdo.GetOwner();
                    if (zdo.GetPrefab() == s_zoneCtrlHash) ctrlOwner = owner;
                    if (owner != 0) { s_counts.TryGetValue(owner, out int c); s_counts[owner] = c + 1; }
                }
            long server = ZDOMan.GetSessionID();
            bool dedi = ZNet.instance.IsDedicated();
            string by = ctrlOwner == 0 ? "null" : (dedi && ctrlOwner == server) ? "\"server\"" : ctrlOwner.ToString();
            var sb = new StringBuilder();
            sb.Append("{\"x\":").Append(x).Append(",\"z\":").Append(z).Append(",\"simulatedBy\":").Append(by).Append(",\"ownerName\":\"").Append(Esc(NameOf(ctrlOwner, server, dedi))).Append("\",\"zdoCountByPeer\":{");
            bool first = true;
            foreach (var kv in s_counts) { if (!first) sb.Append(','); first = false; sb.Append('"').Append(kv.Key).Append("\":").Append(kv.Value); }
            return sb.Append("}}").ToString();
        }

        private static string NameOf(long uid, long server, bool dedi)
        {
            if (uid == 0) return "";
            if (uid == server) return dedi ? "server" : (Game.instance != null ? Game.instance.GetPlayerProfile()?.GetName() ?? "host" : "host");
            ZNetPeer p = ZNet.instance.GetPeer(uid);
            return p != null ? p.m_playerName : "";
        }

        // ---- F5 test 'fgn_netview_test' (run where the API runs: the server / local host): every call returns well-formed JSON, the
        // peer count matches ZNet's, the player's own zone has a _ZoneCtrl owner that equals vanilla's, and each call's time is logged.
        private static bool s_cmdAdded;
        [HarmonyLib.HarmonyPatch(typeof(Terminal), "InitTerminal"), HarmonyLib.HarmonyPostfix]
        private static void AddCommand()
        {
            if (s_cmdAdded) return; s_cmdAdded = true;
            new Terminal.ConsoleCommand("fgn_netview_test", "[F5 test] [FGN] fgn_netview_test - FgnApi v5 network view: peer stats, zone owners around the player, simulation state; checks the JSON and the zone owner against vanilla.", args => RunTest(args.Context), isCheat: true);
        }

        private static void RunTest(Terminal t)
        {
            void Say(string s) { LoggerOptions.LogDebug("[NetViewTest] " + s); t?.AddString("[NetViewTest] " + s); }
            int pass = 0, fail = 0, n = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
            void Check(string name, bool ok, string detail) { n++; if (ok) pass++; else fail++; Say($"{n}/4 {name} {(ok ? "PASS" : "FAIL")} - {detail}"); }
            Say("BEGIN");
            bool Json(string s, char open, char close) => !string.IsNullOrEmpty(s) && s[0] == open && s[s.Length - 1] == close;
            var t0 = sw.Elapsed.TotalMilliseconds; string peers = PeerNetStats(); var t1 = sw.Elapsed.TotalMilliseconds;
            int rows = 0; foreach (char ch in peers) if (ch == '{') rows++;
            int want = ZNet.instance != null ? ZNet.instance.GetConnectedPeers().Count : 0;
            Check("peer stats", Json(peers, '[', ']') && rows == want, $"{rows} row(s), ZNet has {want}, {t1 - t0:0.00} ms: {(peers.Length > 300 ? peers.Substring(0, 300) + "..." : peers)}");
            Player me = Player.m_localPlayer; Vector2s zc = me != null ? ZoneSystem.GetZone(me.transform.position) : new Vector2s(0, 0);
            s_zoneCache.Clear(); t0 = sw.Elapsed.TotalMilliseconds; string zones = ZoneOwners(zc.x, zc.y, 2); t1 = sw.Elapsed.TotalMilliseconds;
            Check("zone owners (5x5)", Json(zones, '[', ']') && zones.Contains("\"simulatedBy\""), $"{t1 - t0:0.00} ms, {zones.Length} chars");
            string mine = ZoneOwners(zc.x, zc.y, 0); long vanilla = 0;
            var list = ZDOMan.instance?.m_objectsBySector[ZoneSystem.SectorToIndex(zc.x, zc.y).Sector];
            if (list != null) foreach (var z in list) if (z != null && z.GetPrefab() == s_zoneCtrlHash) { vanilla = z.GetOwner(); break; }
            string expect = vanilla == 0 ? "\"simulatedBy\":null" : (ZNet.instance.IsDedicated() && vanilla == ZDOMan.GetSessionID()) ? "\"simulatedBy\":\"server\"" : "\"simulatedBy\":" + vanilla;
            Check("own zone owner = vanilla _ZoneCtrl owner", mine.Contains(expect), $"zone {zc.x},{zc.y}: {mine}");
            t0 = sw.Elapsed.TotalMilliseconds; string sim = SimulationState(); t1 = sw.Elapsed.TotalMilliseconds;
            Check("simulation state", Json(sim, '{', '}') && sim.Contains("\"sss\""), $"{t1 - t0:0.00} ms: {sim}");
            Say($"END {(fail == 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, 0 skip in {sw.Elapsed.TotalSeconds:0} s");
        }

        internal static string SimulationState()
        {
            ZNet n = ZNet.instance;
            bool sss = ServerAuthorityPatches.SimulationActive, sel = FiresGhettoNetworkMod.ConfigEnableServerOwnershipSelective != null && FiresGhettoNetworkMod.ConfigEnableServerOwnershipSelective.Value;
            var sb = new StringBuilder("{\"sss\":").Append(sss ? "true" : "false").Append(",\"selectiveOwnership\":").Append(sel ? "true" : "false").Append(",\"serverZones\":[");
            ZDOMan zm = ZDOMan.instance;
            if (zm != null && n != null && n.IsDedicated() && zm.m_objectsBySector != null)
            {
                // only the zones that can be simulated at all: within ScanRadius zones of a connected peer (never the whole world)
                long server = ZDOMan.GetSessionID(); bool first = true;
                var seen = new HashSet<long>();
                foreach (ZNetPeer p in n.GetConnectedPeers())
                {
                    if (p == null) continue;
                    Vector2s c = ZoneSystem.GetZone(p.GetRefPos());
                    for (int z = c.y - ScanRadius; z <= c.y + ScanRadius; z++)
                        for (int x = c.x - ScanRadius; x <= c.x + ScanRadius; x++)
                        {
                            if (!seen.Add(((long)x << 32) ^ (uint)z)) continue;
                            List<ZDO> list;
                            try { uint idx = ZoneSystem.SectorToIndex(x, z).Sector; if (idx >= (uint)zm.m_objectsBySector.Length) continue; list = zm.m_objectsBySector[idx]; } catch { continue; }
                            if (list == null) continue;
                            for (int i = 0; i < list.Count; i++)
                            {
                                ZDO zdo = list[i];
                                if (zdo == null || zdo.GetPrefab() != s_zoneCtrlHash) continue;
                                if (zdo.GetOwner() == server) { if (!first) sb.Append(','); first = false; sb.Append('[').Append(x).Append(',').Append(z).Append(']'); }
                                break;
                            }
                        }
                }
            }
            FgnApi.GetHomesteadCounts(out int homes, out _, out _, out _);
            FgnApi.GetKeepAliveCounts(out int keep, out _, out _, out _);
            return sb.Append("],\"homesteads\":").Append(homes).Append(",\"keepAlive\":").Append(keep).Append('}').ToString();
        }
    }
}
