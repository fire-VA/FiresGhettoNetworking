using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// fgn_netsim: adds latency, jitter and retransmit-sized spikes to everything this game sends, for test rounds
    /// (Tools\REMOTE_MOTION.md; Fire's rule: nothing outside the game). Every message on a link waits in that link's queue and
    /// leaves in order at max(the previous one's time, now + delay ± jitter), so jitter bunches messages the way a reliable stream
    /// does. Steam and PlayFab links are reliable, so loss is modelled as what it costs there: a chance of an extra spike.
    /// Session only: never written to the config, off at start, off again when the time given runs out (10 min by default) or
    /// the session ends, and admin/host only. It works on ZRpc.SendPackage, so Steam and PlayFab links are both covered.
    /// </summary>
    [HarmonyPatch]
    public static class NetSim
    {
        private const string Command = "fgn_netsim";
        private const double MaxDelayMs = 1000.0;
        private const double MaxSpikeMs = 2000.0;
        private const double DefaultSeconds = 600.0;
        private const double MaxSeconds = 3600.0;
        private const double ReportSeconds = 10.0;
        private const double MsPerSecond = 1000.0;

        private sealed class Held
        {
            public ISocket Socket;
            public byte[] Data;
            public double Due;
            public double Queued;
        }

        private sealed class Link
        {
            public readonly Queue<Held> Waiting = new Queue<Held>();
            public double LastDue;
        }

        private static readonly Dictionary<ZRpc, Link> s_links = new Dictionary<ZRpc, Link>();
        private static readonly System.Random s_dice = new System.Random();
        private static readonly List<double> s_added = new List<double>();
        private static bool s_on;
        private static bool s_commandRegistered;
        private static double s_delay, s_jitter, s_spikeChance, s_spike, s_until, s_nextReport;
        private static int s_spikes, s_dropped;
        private static Pump s_pump;

        private static readonly AccessTools.FieldRef<ZRpc, ISocket> s_socket = AccessTools.FieldRefAccess<ZRpc, ISocket>("m_socket");
        private static readonly AccessTools.FieldRef<ZRpc, int> s_sentPackages = AccessTools.FieldRefAccess<ZRpc, int>("m_sentPackages");
        private static readonly AccessTools.FieldRef<ZRpc, int> s_sentData = AccessTools.FieldRefAccess<ZRpc, int>("m_sentData");

        public static bool Active => s_on;

        /// <summary>Called from LinkController's ZNet.Start postfix (no hook of its own).</summary>
        public static void RegisterCommand()
        {
            if (s_commandRegistered) return;
            s_commandRegistered = true;
            new Terminal.ConsoleCommand(Command,
                "<delay_ms> [jitter_ms] [spike% spike_ms] [for <seconds>] | off — FGN test tool (admin/host): holds everything this game sends "
                + "for delay ± jitter ms (in order), plus spike_ms on spike% of messages (what a retransmit costs). Session only; off after "
                + $"{DefaultSeconds / 60.0:F0} min unless 'for' says otherwise. No arguments: status.",
                new Terminal.ConsoleEvent(OnCommand));
        }

        private static void OnCommand(Terminal.ConsoleEventArgs args)
        {
            if (ZNet.instance == null) { args.Context?.AddString($"{Command}: join a world first."); return; }
            if (!ZNet.instance.IsServer() && !ZNet.instance.LocalPlayerIsAdminOrHost())
            { args.Context?.AddString($"{Command}: admins and hosts only."); return; }
            var words = args.Args.Skip(1).Select(a => a.ToLowerInvariant()).ToList();
            if (words.Count == 0) { args.Context?.AddString(Status()); return; }
            if (words[0] == "off") { TurnOff("turned off"); args.Context?.AddString($"{Command}: off."); return; }

            double seconds = DefaultSeconds;
            int forAt = words.IndexOf("for");
            if (forAt >= 0)
            {
                if (forAt + 1 >= words.Count || !TryNumber(words[forAt + 1], out seconds) || seconds <= 0.0)
                { args.Context?.AddString($"{Command}: 'for' needs a number of seconds."); return; }
                words.RemoveRange(forAt, 2);
            }
            var numbers = new List<double>();
            foreach (string word in words)
            {
                if (!TryNumber(word.TrimEnd('%'), out double value) || value < 0.0)
                { args.Context?.AddString($"{Command}: '{word}' isn't a number; usage: {Command} <delay_ms> [jitter_ms] [spike% spike_ms] [for <seconds>] | off"); return; }
                numbers.Add(value);
            }
            if (numbers.Count == 3 || numbers.Count > 4)
            { args.Context?.AddString($"{Command}: spikes need both spike% and spike_ms."); return; }
            TurnOn(Math.Min(numbers[0], MaxDelayMs), numbers.Count > 1 ? Math.Min(numbers[1], MaxDelayMs) : 0.0,
                numbers.Count > 3 ? Math.Min(numbers[2], 100.0) : 0.0, numbers.Count > 3 ? Math.Min(numbers[3], MaxSpikeMs) : 0.0,
                Math.Min(seconds, MaxSeconds));
            args.Context?.AddString(Status());
        }

        public static void TurnOn(double delayMs, double jitterMs, double spikePercent, double spikeMs, double seconds)
        {
            s_delay = delayMs / MsPerSecond;
            s_jitter = Math.Min(jitterMs, delayMs) / MsPerSecond;
            s_spikeChance = spikePercent / 100.0;
            s_spike = spikeMs / MsPerSecond;
            s_until = Now() + seconds;
            s_nextReport = Now() + ReportSeconds;
            s_added.Clear();
            s_spikes = s_dropped = 0;
            s_on = true;
            if (s_pump == null)
            {
                var host = new GameObject("FGN_NetSim");
                UnityEngine.Object.DontDestroyOnLoad(host);
                s_pump = host.AddComponent<Pump>();
            }
            LoggerOptions.LogWarning($"[NetSim] ON: everything this game sends waits {delayMs:F0} ± {Math.Min(jitterMs, delayMs):F0} ms"
                + (s_spikeChance > 0.0 ? $", plus {spikeMs:F0} ms on {spikePercent:F0}% of messages" : string.Empty)
                + $", for {seconds:F0} s (test tool; '{Command} off' ends it).");
        }

        /// <summary>Off, with everything still waiting sent at once, in order. Also called when the session ends.</summary>
        public static void TurnOff(string why)
        {
            bool wasOn = s_on;
            s_on = false;
            Flush(double.MaxValue);
            s_links.Clear();
            if (wasOn)
            {
                Report("before it went off");
                LoggerOptions.LogWarning($"[NetSim] OFF ({why}).");
            }
        }

        private static string Status() =>
            !s_on ? $"{Command}: off."
            : $"{Command}: on, {s_delay * MsPerSecond:F0} ± {s_jitter * MsPerSecond:F0} ms"
              + (s_spikeChance > 0.0 ? $", {s_spike * MsPerSecond:F0} ms spikes on {s_spikeChance * 100.0:F0}%" : string.Empty)
              + $", {Math.Max(0.0, s_until - Now()):F0} s left; {s_links.Values.Sum(l => l.Waiting.Count)} message(s) waiting on {s_links.Count} link(s).";

        [HarmonyPatch(typeof(ZRpc), "SendPackage"), HarmonyPrefix]
        static bool Hold(ZRpc __instance, ZPackage pkg)
        {
            if (!s_on || __instance == null || pkg == null) return true;
            double now = Now();
            if (now >= s_until) { TurnOff("its time ran out"); return true; }
            ISocket socket = s_socket(__instance);
            if (socket == null) return true;
            Flush(now);
            if (!s_links.TryGetValue(__instance, out Link link)) s_links[__instance] = link = new Link();
            double delay = Math.Max(0.0, s_delay + (s_dice.NextDouble() * 2.0 - 1.0) * s_jitter);
            if (s_spikeChance > 0.0 && s_dice.NextDouble() < s_spikeChance)
            {
                delay += s_spike;
                s_spikes++;
            }
            double due = Math.Max(link.LastDue, now + delay);
            link.LastDue = due;
            link.Waiting.Enqueue(new Held { Socket = socket, Data = pkg.GetArray(), Due = due, Queued = now });
            // Vanilla's own counters, as if it had sent it now.
            s_sentPackages(__instance)++;
            s_sentData(__instance) += pkg.Size();
            return false;
        }

        /// <summary>Everything due by now leaves, in order per link; a link whose socket has closed drops what it still held.</summary>
        private static void Flush(double now)
        {
            if (s_links.Count == 0) return;
            List<ZRpc> gone = null;
            foreach (KeyValuePair<ZRpc, Link> entry in s_links)
            {
                Queue<Held> waiting = entry.Value.Waiting;
                while (waiting.Count > 0 && waiting.Peek().Due <= now)
                {
                    Held held = waiting.Dequeue();
                    if (held.Socket != null && held.Socket.IsConnected())
                    {
                        held.Socket.Send(new ZPackage(held.Data));
                        if (now != double.MaxValue) s_added.Add(now - held.Queued);
                    }
                    else s_dropped++;
                }
                bool connected = s_socket(entry.Key)?.IsConnected() ?? false;
                if (waiting.Count == 0 && !connected) (gone ??= new List<ZRpc>()).Add(entry.Key);
            }
            if (gone != null) foreach (ZRpc rpc in gone) s_links.Remove(rpc);
        }

        private static void Report(string window)
        {
            if (s_added.Count == 0 && s_dropped == 0) return;
            List<double> sorted = s_added.OrderBy(v => v).ToList();
            string added = sorted.Count == 0 ? "none sent"
                : $"added {Ms(sorted[sorted.Count / 2])} median, {Ms(sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))])} p95, "
                  + $"{Ms(sorted[sorted.Count - 1])} max";
            LoggerOptions.LogMessage($"[NetSim] {window}: {sorted.Count} message(s) held on {s_links.Count} link(s), {added}; {s_spikes} spike(s), "
                + $"{s_dropped} dropped with their link.");
            s_added.Clear();
            s_spikes = s_dropped = 0;
        }

        private static string Ms(double seconds) => $"{seconds * MsPerSecond:F0} ms";

        private static double Now() => Time.realtimeSinceStartupAsDouble;

        private static bool TryNumber(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        /// <summary>Releases what is due every frame, even when this game sends nothing new; reports every 10 s.</summary>
        private sealed class Pump : MonoBehaviour
        {
            private void Update() => Tick();
            private void LateUpdate() => Tick();

            private static void Tick()
            {
                if (!s_on) return;
                double now = Now();
                if (now >= s_until) { TurnOff("its time ran out"); return; }
                Flush(now);
                if (now >= s_nextReport)
                {
                    s_nextReport = now + ReportSeconds;
                    Report($"last {ReportSeconds:F0} s");
                }
            }
        }
    }
}
