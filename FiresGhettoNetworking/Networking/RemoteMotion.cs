using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Remote Motion (Tools\REMOTE_MOTION.md): how this client draws other players. Each player's own game stamps every position it
    /// sends with the physics time of that position; here those updates are kept on the sender's clock and drawn a short delay
    /// behind the newest (cubic Hermite with the sent velocity), carried on by that velocity when the next update is late (capped),
    /// and any jump a new update brings is blended out instead of snapped. Tuned offline on motion_test recordings (R39): 40 ms,
    /// 200 ms, 50 ms beat vanilla on error, lag, overshoot, judder and lurches.
    ///
    /// Only players whose game stamps its updates (FGN 1.5.0+) and who aren't aboard a ship or cart; everyone else is vanilla.
    /// Drawing is off by default (opt-in until a live A/B round confirms it); stamping is on, as other players' games need it.
    /// No hooks of its own: TransformWriteRate's OwnerSync prefix stamps, PlayerPositionSyncPatches' ZDO.Deserialize and
    /// Player.LateUpdate postfixes feed and draw, and OwnershipHandoffPatches' CustomFixedUpdate prefix keeps vanilla's
    /// ClientSync off a player drawn here.
    /// </summary>
    public static class RemoteMotion
    {
        public static ConfigEntry<bool> ConfigEnabled;
        public static ConfigEntry<bool> ConfigSendStamps;
        public static ConfigEntry<int> ConfigDelayMs;
        public static ConfigEntry<int> ConfigExtrapolationCapMs;
        public static ConfigEntry<int> ConfigBlendMs;

        private static readonly int StampHash = "fgn_rm_t".GetStableHashCode();
        private static readonly int PlayerPrefab = "Player".GetStableHashCode();
        private const float SnapMeters = 5f;
        private const int MaxSnapshots = 64;
        private const double StaleSeconds = 1.0;
        private const double MsPerSecond = 1000.0;
        private const float RotationSlerpPerSecond = 20f;

        private sealed class Track
        {
            public readonly List<double> Sent = new List<double>();
            public readonly List<Vector3> Position = new List<Vector3>();
            public readonly List<Vector3> Velocity = new List<Vector3>();
            public Quaternion Rotation = Quaternion.identity;
            public double Offset = double.MaxValue;
            public double LastReceived;
            public double LastNow = double.NaN;
            public int CountAtLastDraw;
            public Vector3 Error;
            public Vector3 Drawn;
            public bool HasDrawn;
        }

        private static readonly Dictionary<ZDOID, Track> s_tracks = new Dictionary<ZDOID, Track>();
        private static readonly List<ZDOID> s_stale = new List<ZDOID>();

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("03 - Player Sync", "Remote Motion (experimental)", true,
                "Draws other players from their own time-stamped updates: a short buffer, carried on by their velocity when an update is\n" +
                "late, corrections blended instead of snapped. Measured offline against vanilla: less error and lag, less overshoot at\n" +
                "turns, half the judder. Needs the other player to run FGN 1.5.0+; others are drawn as vanilla. Replaces the\n" +
                "interpolation/prediction options while on. CLIENT-ONLY.");
            ConfigSendStamps = config.Bind("03 - Player Sync", "Send Remote Motion Timestamps", true,
                "Stamps your own position updates with their physics time (8 bytes an update) so other players' Remote Motion can\n" +
                "draw you. Harmless to players without FGN.");
            ConfigDelayMs = config.Bind("03 - Player Sync", "Remote Motion Delay (ms)", DefaultDelayMs,
                new ConfigDescription("How far behind the newest update other players are drawn. Lower is snappier but overshoots more.",
                    new AcceptableValueRange<int>(0, 200)));
            ConfigExtrapolationCapMs = config.Bind("03 - Player Sync", "Remote Motion Extrapolation Cap (ms)", DefaultCapMs,
                new ConfigDescription("How long a player is carried on by their velocity when their next update is late.",
                    new AcceptableValueRange<int>(0, 500)));
            ConfigBlendMs = config.Bind("03 - Player Sync", "Remote Motion Blend (ms)", DefaultBlendMs,
                new ConfigDescription("How long a correction from a new update takes to fade (0 = snap).", new AcceptableValueRange<int>(0, 500)));
            // Fire, 2026-09-28 (after the R44/R44b A/B): ON at 20/100/100. A cfg still holding the untouched old defaults (off, 40/200/50)
            // gets the new ones once; any value a player changed stays.
            if (!ConfigEnabled.Value && ConfigDelayMs.Value == 40 && ConfigExtrapolationCapMs.Value == 200 && ConfigBlendMs.Value == 50)
            {
                ConfigEnabled.Value = true;
                ConfigDelayMs.Value = DefaultDelayMs;
                ConfigExtrapolationCapMs.Value = DefaultCapMs;
                ConfigBlendMs.Value = DefaultBlendMs;
                LoggerOptions.LogInfo("[RemoteMotion] the untouched old defaults moved to the new ones: "
                    + $"'{ConfigEnabled.Definition.Key}' false -> true, '{ConfigDelayMs.Definition.Key}' 40 -> {DefaultDelayMs}, "
                    + $"'{ConfigExtrapolationCapMs.Definition.Key}' 200 -> {DefaultCapMs}, '{ConfigBlendMs.Definition.Key}' 50 -> {DefaultBlendMs}.");
            }
        }

        private const int DefaultDelayMs = 20;
        private const int DefaultCapMs = 100;
        private const int DefaultBlendMs = 100;

        /// <summary>The sender's physics clock (the one its stamps use) as of now, minus the fastest one-way trip seen from it, for the
        /// player whose peer is <paramref name="uid"/>; false when that player isn't tracked here (Remote Motion off, no stamps).</summary>
        internal static bool TrySenderNow(long uid, out double senderNow)
        {
            senderNow = 0.0;
            double now = Now();
            // R54: a player standing still sends no stamped updates (only moves are stamped), so its track goes stale in a second and
            // Remote Arrows lost the clock mid-flight. R58: taking a re-created track's first offset (before its minimum settled)
            // put the clock BEHIND the launch, and arrows waited at the bow (19 m off at 20 m). Each player's clock is therefore the
            // lowest offset seen across its tracks, allowed to rise slowly for drift, and it comes first.
            if (s_clocks.TryGetValue(uid, out (double Offset, double Seen) clock) && now - clock.Seen <= ClockKeepSeconds)
            {
                senderNow = now - clock.Offset;
                return true;
            }
            foreach (KeyValuePair<ZDOID, Track> entry in s_tracks)
            {
                if (entry.Key.UserID != uid || entry.Value.Offset == double.MaxValue || now - entry.Value.LastReceived > StaleSeconds) continue;
                senderNow = now - entry.Value.Offset;
                return true;
            }
            return false;
        }

        /// <summary>From Receive: fold one stamped update's (arrival - stamp) into that player's clock.</summary>
        private static void NoteClock(long uid, double sample, double now)
        {
            if (s_clocks.TryGetValue(uid, out (double Offset, double Seen) clock) && now - clock.Seen <= ClockKeepSeconds)
                s_clocks[uid] = (Math.Min(clock.Offset + ClockDriftPerSecond * (now - clock.Seen), sample), now);
            else
                s_clocks[uid] = (sample, now);
        }

        private const double ClockKeepSeconds = 120.0;
        private const double ClockDriftPerSecond = 0.002;
        private static readonly Dictionary<long, (double Offset, double Seen)> s_clocks = new Dictionary<long, (double, double)>();

        public static bool Drawing => ConfigEnabled != null && ConfigEnabled.Value;

        // R86 (Selective ON): the DEDI's copy of Fire's player sat ~290 m off and fell to y -3973 while he stood at y 60.
        // PlayerPositionSyncPatches is patched on the dedi too: Receive built a track per player there, Handles then kept vanilla's
        // ZSyncTransform.ClientSync off that copy, but Draw never runs on a server (the Player.LateUpdate postfix returns for it),
        // so nothing moved it and nothing turned its gravity off (ClientSync sets useGravity = false every step): frozen, then
        // falling. With server-side simulation the dedi instantiates players, so it showed. No drawing on a server: vanilla syncs.
        private static bool OnServer() => ZNet.instance != null && ZNet.instance.IsServer();

        private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        // ------------------------------------------------------------ sender

        /// <summary>From TransformWriteRate's OwnerSync prefix when vanilla's write goes ahead: a moved player position gets the
        /// physics time it was taken at (vanilla writes m_body.position, the last physics step's pose). Runs for every owned
        /// object every frame, so the view comes in from the hook, and non-players leave at the prefab check.</summary>
        internal static void Stamp(ZSyncTransform sync, ZNetView view, Rigidbody body)
        {
            if (ConfigSendStamps == null || !ConfigSendStamps.Value || sync == null || view == null) return;
            ZDO zdo = view.GetZDO();
            if (zdo == null || zdo.GetPrefab() != PlayerPrefab || !zdo.IsOwner()) return;
            Vector3 at = body != null ? body.position : sync.transform.position;
            if (zdo.GetPosition() == at) return;
            zdo.Set(StampHash, (long)(Time.fixedTimeAsDouble * MsPerSecond));
        }

        // ------------------------------------------------------------ receiver

        /// <summary>From PlayerPositionSyncPatches' ZDO.Deserialize postfix: another player's stamped update.</summary>
        internal static void Receive(ZDO zdo)
        {
            if (!Drawing || zdo == null || zdo.GetPrefab() != PlayerPrefab || zdo.IsOwner() || OnServer()) return;
            long stamp = zdo.GetLong(StampHash, -1L);
            if (stamp < 0) return;
            if (!zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.SyncTransform).IsNone()) { s_tracks.Remove(zdo.m_uid); return; }
            double sent = stamp / MsPerSecond, now = Now();
            if (!s_tracks.TryGetValue(zdo.m_uid, out Track track)) s_tracks[zdo.m_uid] = track = new Track();
            int n = track.Sent.Count;
            if (n > 0 && sent < track.Sent[n - 1]) { s_tracks[zdo.m_uid] = track = new Track(); n = 0; }   // their game restarted
            if (n > 0 && sent == track.Sent[n - 1]) { track.Rotation = zdo.GetRotation(); return; }
            track.Sent.Add(sent);
            track.Position.Add(zdo.GetPosition());
            track.Velocity.Add(zdo.GetVec3(ZDOVars.s_velHash, Vector3.zero));
            track.Rotation = zdo.GetRotation();
            track.Offset = Math.Min(track.Offset, now - sent);
            NoteClock(zdo.m_uid.UserID, now - sent, now);
            track.LastReceived = now;
            if (track.Sent.Count > MaxSnapshots)
            {
                track.Sent.RemoveAt(0);
                track.Position.RemoveAt(0);
                track.Velocity.RemoveAt(0);
                track.CountAtLastDraw = Math.Max(0, track.CountAtLastDraw - 1);
            }
        }

        /// <summary>Whether this object is a player drawn here, so vanilla's ClientSync stays off it (per fixed step, per object:
        /// cheap when nothing is drawn).</summary>
        internal static bool Handles(ZNetView view)
        {
            if (!Drawing || s_tracks.Count == 0 || view == null || OnServer()) return false;
            ZDO zdo = view.GetZDO();
            return zdo != null && zdo.GetPrefab() == PlayerPrefab && !zdo.IsOwner()
                   && s_tracks.TryGetValue(zdo.m_uid, out Track track) && Now() - track.LastReceived < StaleSeconds;
        }

        /// <summary>From PlayerPositionSyncPatches' Player.LateUpdate postfix, every rendered frame: draw this player. False when it
        /// isn't drawn here (vanilla or FGN's older options draw it).</summary>
        internal static bool Draw(Player player)
        {
            if (!Drawing || player == null) return false;
            ZNetView view = player.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || zdo.IsOwner() || !s_tracks.TryGetValue(zdo.m_uid, out Track track) || track.Sent.Count == 0) return false;
            double now = Now();
            if (now - track.LastReceived >= StaleSeconds) { Prune(now); return false; }

            int n = track.Sent.Count;
            double tau = ConfigBlendMs.Value / MsPerSecond;
            if (tau > 0.0 && !double.IsNaN(track.LastNow) && n > track.CountAtLastDraw && track.CountAtLastDraw > 0)
                track.Error -= Raw(track, track.LastNow, n) - Raw(track, track.LastNow, track.CountAtLastDraw);
            track.Error = tau > 0.0 && !double.IsNaN(track.LastNow) ? track.Error * (float)Math.Exp(-(now - track.LastNow) / tau) : Vector3.zero;
            Vector3 at = Raw(track, now, n) + track.Error;
            if (track.HasDrawn && Vector3.Distance(at, track.Drawn) > SnapMeters) track.Error = Vector3.zero;
            track.LastNow = now;
            track.CountAtLastDraw = n;
            track.Drawn = at;
            track.HasDrawn = true;

            player.transform.position = at;
            player.transform.rotation = Quaternion.Slerp(player.transform.rotation, track.Rotation, Mathf.Clamp01(Time.deltaTime * RotationSlerpPerSecond));
            Rigidbody body = player.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.position = at;
                body.linearVelocity = Vector3.zero;
            }
            return true;
        }

        /// <summary>Where the buffer puts the player at this time: Hermite between the updates around it, or carried on past the
        /// newest by its velocity (capped).</summary>
        private static Vector3 Raw(Track track, double now, int count)
        {
            double at = now - track.Offset - ConfigDelayMs.Value / MsPerSecond;
            int last = count - 1;
            if (at >= track.Sent[last])
                return track.Position[last] + track.Velocity[last] * (float)Math.Min(at - track.Sent[last], ConfigExtrapolationCapMs.Value / MsPerSecond);
            if (at <= track.Sent[0]) return track.Position[0];
            int lo = 0, hi = last;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (track.Sent[mid] <= at) lo = mid; else hi = mid;
            }
            float h = (float)(track.Sent[hi] - track.Sent[lo]);
            float u = h > 0f ? (float)((at - track.Sent[lo]) / h) : 0f;
            float u2 = u * u, u3 = u2 * u;
            return (2f * u3 - 3f * u2 + 1f) * track.Position[lo] + (u3 - 2f * u2 + u) * h * track.Velocity[lo]
                   + (-2f * u3 + 3f * u2) * track.Position[hi] + (u3 - u2) * h * track.Velocity[hi];
        }

        private static void Prune(double now)
        {
            s_stale.Clear();
            foreach (KeyValuePair<ZDOID, Track> entry in s_tracks)
                if (now - entry.Value.LastReceived >= StaleSeconds) s_stale.Add(entry.Key);
            foreach (ZDOID id in s_stale) s_tracks.Remove(id);
        }

        /// <summary>A session ended: nothing carries over.</summary>
        internal static void Reset()
        {
            s_tracks.Clear();
            s_clocks.Clear();
        }
    }
}
