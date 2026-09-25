using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Caps how often a listed object writes its transform into its ZDO. ZSyncTransform writes position, velocity and
    /// rotation from LateUpdate, so an owner writes once per rendered frame, while ZDOMan drains its outgoing set about
    /// thirty times a second and that set is a HashSet keyed by object. Every write past the send rate is therefore
    /// collapsed before it reaches the wire, but it has already bumped the object's revision and stamped its sector,
    /// which is what the server pays for when it works out what each player still needs.
    ///
    /// Fish are the case that matters: they are pushed by wave force every physics step and never come to rest, and one
    /// client uploaded 110,000 fish records in five minutes. Holding their writes to the send rate leaves the bytes on
    /// the wire alone - those are set by the send loop, not by this - and takes the wasted revisions with it.
    /// </summary>
    [HarmonyPatch]
    internal static class TransformWriteRate
    {
        public static ConfigEntry<bool> ConfigEnabled;
        public static ConfigEntry<string> ConfigPrefabs;
        public static ConfigEntry<float> ConfigWritesPerSecond;

        private const int MaxTracked = 4096;

        private sealed class Tracked
        {
            public bool InScope;
            public bool Decided;
            public ZNetView View;
            public float LastWrite;
        }

        private static readonly Dictionary<ZSyncTransform, Tracked> s_tracked = new Dictionary<ZSyncTransform, Tracked>();
        private static readonly HashSet<int> s_prefabs = new HashSet<int>();
        private static string s_parsedFrom;

        internal static long WritesAllowed;
        internal static long WritesHeldBack;

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("04 - Networking", "Limit Transform Write Rate", true,
                "Objects that never stop moving write their position into the world state once per drawn frame, which on a\n" +
                "fast machine is several times more often than the server sends anything. The extra writes are thrown away\n" +
                "before they reach the network, but each one still marks the area around it as changed and makes the server\n" +
                "recheck everything standing there. With this on, the objects listed below write no faster than the server\n" +
                "sends.");

            ConfigPrefabs = config.Bind("04 - Networking", "Transform Write Rate Objects",
                "Fish1,Fish2,Fish3,Fish4_cave,Fish5,Fish6,Fish7,Fish8,Fish9,Fish10,Fish11,Fish12",
                "Which objects the limit above applies to, by prefab name, separated by commas. Only objects that move\n" +
                "constantly and are not directly controlled by a player belong here.");

            ConfigWritesPerSecond = config.Bind("04 - Networking", "Transform Writes Per Second", 10f,
                new ConfigDescription(
                    "How many times a second a listed object may write its position. Anything above about thirty is thrown\n" +
                    "away by the send loop regardless. Below that, other players see the object move in fewer, larger steps:\n" +
                    "their game carries it along its last known heading and eases it onto each new position, and only jumps\n" +
                    "it outright once the guess is more than five metres out, which slow swimmers never reach. Lower saves\n" +
                    "more. Raise it if listed objects look like they are stuttering.",
                    new AcceptableValueRange<float>(2f, 120f)));
        }

        /// <summary>
        /// FGN's only hook on OwnerSync. It lives here rather than in ServerAuthorityPatches because that class is
        /// registered only on a dedicated server running server-side simulation, and the objects this limits are owned
        /// by clients.
        /// </summary>
        [HarmonyPatch(typeof(ZSyncTransform), "OwnerSync")]
        [HarmonyPrefix]
        public static bool OwnerSync_Prefix(ZSyncTransform __instance, Rigidbody ___m_body)
        {
            if (ServerAuthorityPatches.TryRescueBelowKillPlane(__instance, ___m_body)) return false;
            return !HoldBack(__instance);
        }

        /// <summary>True when this object has already written within its allowance and vanilla's write should be skipped.</summary>
        internal static bool HoldBack(ZSyncTransform sync)
        {
            if (ConfigEnabled == null || !ConfigEnabled.Value || sync == null) return false;

            if (!s_tracked.TryGetValue(sync, out var tracked))
            {
                if (s_tracked.Count >= MaxTracked) Sweep();
                tracked = new Tracked { View = sync.GetComponent<ZNetView>(), LastWrite = float.NegativeInfinity };
                s_tracked[sync] = tracked;
            }
            // An object whose view is not ready yet has no prefab to match, and answering "not ours" then would stick
            // for its whole life, so the question is left open until there is something to answer it with.
            if (!tracked.Decided)
            {
                if (tracked.View == null) tracked.View = sync.GetComponent<ZNetView>();
                if (tracked.View == null || !tracked.View.IsValid()) return false;
                tracked.InScope = InScope(tracked.View);
                tracked.Decided = true;
            }
            if (!tracked.InScope) return false;

            float rate = ConfigWritesPerSecond.Value;
            if (rate <= 0f) return false;

            // Hooking a fish claims ownership of it, so the player fishing already drives it from their own physics
            // rather than from the network. Letting a hooked one write freely keeps that true for everyone watching.
            if (IsHooked(tracked.View)) return false;

            float now = Time.unscaledTime;
            if (now - tracked.LastWrite < 1f / rate)
            {
                WritesHeldBack++;
                return true;
            }
            tracked.LastWrite = now;
            WritesAllowed++;
            return false;
        }

        private static bool InScope(ZNetView nview)
        {
            ParsePrefabs();
            if (s_prefabs.Count == 0) return false;
            var zdo = nview == null || !nview.IsValid() ? null : nview.GetZDO();
            return zdo != null && s_prefabs.Contains(zdo.GetPrefab());
        }

        private static bool IsHooked(ZNetView nview)
        {
            if (nview == null || !nview.IsValid()) return false;
            var zdo = nview.GetZDO();
            return zdo != null && zdo.GetInt(ZDOVars.s_hooked, 0) != 0;
        }

        private static void ParsePrefabs()
        {
            string raw = ConfigPrefabs?.Value ?? "";
            if (s_parsedFrom == raw) return;
            s_parsedFrom = raw;
            s_prefabs.Clear();
            foreach (string name in raw.Split(','))
            {
                string trimmed = name.Trim();
                if (trimmed.Length > 0) s_prefabs.Add(trimmed.GetStableHashCode());
            }
            // A changed list has to re-decide every object it may have already judged against the old one.
            s_tracked.Clear();
        }

        private static void Sweep()
        {
            var gone = new List<ZSyncTransform>();
            foreach (var pair in s_tracked)
                if (pair.Key == null) gone.Add(pair.Key);
            for (int i = 0; i < gone.Count; i++) s_tracked.Remove(gone[i]);
            if (s_tracked.Count >= MaxTracked) s_tracked.Clear();
        }

        internal static string TakeReportLine()
        {
            if (WritesAllowed == 0 && WritesHeldBack == 0) return null;
            long total = WritesAllowed + WritesHeldBack;
            string line = $"[Upload] Transform writes: {WritesAllowed:N0} written, {WritesHeldBack:N0} held back "
                + $"({(total > 0 ? WritesHeldBack * 100 / total : 0)}%).";
            WritesAllowed = 0;
            WritesHeldBack = 0;
            return line;
        }
    }
}
