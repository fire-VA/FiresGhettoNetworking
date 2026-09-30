using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Admin test tool for pricing Server-Side Simulation: `fgn_sss_areas N` makes the server also build the world around N
    /// virtual players placed in the busiest far-apart areas, so one client can measure what N spread-out players would cost.
    /// </summary>
    internal static class SssTestAreas
    {
        private const string Command = "fgn_sss_areas";
        private const string RequestRpc = "FGN_SssAreasReq";
        private const string ReplyRpc = "FGN_SssAreasReply";
        private const int MaxAreas = 32;
        private const int MinSpacingZones = 5;
        private const int SectorGridWidth = 512;
        private const int SectorGridOffset = 256;
        private const float ReminderIntervalSec = 60f;

        private static readonly List<Vector3> s_points = new List<Vector3>();
        private static bool s_commandRegistered;
        private static float s_nextReminder;

        internal static List<Vector3> Points => s_points;

        internal static void Clear()
        {
            if (s_points.Count > 0)
                LoggerOptions.LogWarning($"[SSSAreas] TEST MODE OFF: the {s_points.Count} virtual player area(s) were cleared with the world.");
            s_points.Clear();
        }

        internal static void RegisterCommandAndRpcs()
        {
            if (ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.Register<int>(RequestRpc, OnRequest);
                ZRoutedRpc.instance.Register<string>(ReplyRpc, OnReply);
            }
            if (s_commandRegistered) return;
            s_commandRegistered = true;
            new Terminal.ConsoleCommand(Command,
                "FGN test (admin): the server also builds the world around N virtual players in the busiest far-apart areas, to price "
                + "Server-Side Simulation with one client. 0 clears. Logged loudly while on; cleared when the world unloads.",
                new Terminal.ConsoleEvent(OnCommand));
        }

        internal static void RemindIfActive()
        {
            if (s_points.Count == 0 || Time.realtimeSinceStartup < s_nextReminder) return;
            s_nextReminder = Time.realtimeSinceStartup + ReminderIntervalSec;
            LoggerOptions.LogWarning($"[SSSAreas] TEST MODE: the server is also building {s_points.Count} virtual player area(s); frame "
                + "rates and [ServerStatus] counts in this log are NOT a normal run.");
        }

        private static void OnCommand(Terminal.ConsoleEventArgs args)
        {
            if (ZRoutedRpc.instance == null || args.Length < 2 || !int.TryParse(args[1], out int count))
            {
                args.Context?.AddString($"Usage: {Command} <0-{MaxAreas}>");
                return;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(RequestRpc, count);
        }

        private static void OnRequest(long sender, int count)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            string reply;
            if (!ServerClientUtils.IsAdmin(sender)) reply = "FGN: fgn_sss_areas is admin only.";
            else if (!ServerAuthorityPatches.SimulationActive) reply = "FGN: Server-Side Simulation is off on this server; nothing to price.";
            else reply = PlaceAreas(Mathf.Clamp(count, 0, MaxAreas));
            LoggerOptions.LogWarning(reply);
            AdminConsoleEcho.Send(ReplyRpc, sender, reply);
        }

        private static void OnReply(long sender, string reply) => AdminConsoleEcho.Print(reply);

        private static string PlaceAreas(int count)
        {
            s_points.Clear();
            if (count == 0) return "[SSSAreas] TEST MODE OFF: no virtual player areas.";

            var taken = new List<Vector2s>();
            foreach (var peer in ZNet.instance.GetPeers())
                if (peer != null && peer.IsReady()) taken.Add(ZoneSystem.GetZone(peer.GetRefPos()));

            foreach (var zone in BusiestZones())
            {
                if (s_points.Count >= count) break;
                if (TooClose(zone, taken)) continue;
                taken.Add(zone);
                s_points.Add(ZoneSystem.GetZonePos(zone));
            }
            s_nextReminder = Time.realtimeSinceStartup + ReminderIntervalSec;
            return $"[SSSAreas] TEST MODE ON: the server is also building {s_points.Count} virtual player area(s) at "
                + string.Join(", ", s_points.ConvertAll(p => $"({p.x:F0}, {p.z:F0})")) + ". Clear with '" + Command + " 0'.";
        }

        private static IEnumerable<Vector2s> BusiestZones()
        {
            var sectors = ZDOMan.instance.m_objectsBySector;
            var counts = new List<KeyValuePair<int, int>>();
            for (int index = 0; index < sectors.Length; index++)
                if (sectors[index] != null && sectors[index].Count > 0) counts.Add(new KeyValuePair<int, int>(index, sectors[index].Count));
            counts.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (var entry in counts)
                yield return new Vector2s(entry.Key % SectorGridWidth - SectorGridOffset, entry.Key / SectorGridWidth - SectorGridOffset);
        }

        private static bool TooClose(Vector2s zone, List<Vector2s> taken)
        {
            foreach (var other in taken)
                if (Math.Abs(zone.x - other.x) < MinSpacingZones && Math.Abs(zone.y - other.y) < MinSpacingZones) return true;
            return false;
        }
    }
}
