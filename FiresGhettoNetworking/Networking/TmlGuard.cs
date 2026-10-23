using System;
using System.Collections;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// "TooManyLags - FiresGhettoNetworking Addon" (botanic.toomanylags.fgnaddon) is not supported. It replaces FGN's own
    /// ConfigEntry fields with copies of its own (Selective ownership off, Server-Side Simulation on, ...), rewrites FGN's
    /// internal statics, transpiles FGN's methods and drives FGN's private hand-off protocol by name. FGN records its own
    /// settings at the end of its Awake; the add-on is not neutralised, FGN logs ONE WARNING naming what it overrides and
    /// leaves it running.
    /// </summary>
    internal static class TmlGuard
    {
        internal const string TmlGuid = "botanic.toomanylags.fgnaddon";
        private static ConfigEntry<bool> s_auth, s_own, s_ownSel, s_creatures, s_station;
        internal static bool Present => Chainloader.PluginInfos.ContainsKey(TmlGuid);

        /// <summary>The end of FGN's Awake: our own entries and switches, as configured.</summary>
        internal static void Capture()
        {
            s_auth = FiresGhettoNetworkMod.ConfigEnableServerAuthority;
            s_own = FiresGhettoNetworkMod.ConfigEnableServerOwnership;
            s_ownSel = FiresGhettoNetworkMod.ConfigEnableServerOwnershipSelective;
            s_creatures = CreatureOwnership.ConfigEnabled;
            s_station = StationRouter.ConfigEnabled;
        }

        internal static IEnumerator Run(MonoBehaviour host)
        {
            yield return null; yield return null;   // after every plugin's Awake and Start
            if (!Present) { LoggerOptions.LogInfo("[TmlGuard] TML not present (TooManyLags FGN Addon): nothing to do."); yield break; }
            // People may try mods: one clear WARNING with what it overrides (read from FGN's own entries vs what is active
            // now), nothing restored or unpatched.
            Warn();
        }

        internal static bool Warned;

        private static void Warn()
        {
            if (Warned) return;
            Warned = true;
            var changed = new System.Collections.Generic.List<string>();
            void Note(string name, ConfigEntry<bool> ours, ConfigEntry<bool> live)
            {
                if (ours != null && !ReferenceEquals(ours, live)) changed.Add($"{name} (your setting {ours.Value}, active {(live != null ? live.Value.ToString() : "?")})");
            }
            Note("Server-Side Simulation", s_auth, FiresGhettoNetworkMod.ConfigEnableServerAuthority);
            Note("Server ownership", s_own, FiresGhettoNetworkMod.ConfigEnableServerOwnership);
            Note("Selective ownership", s_ownSel, FiresGhettoNetworkMod.ConfigEnableServerOwnershipSelective);
            Note("Creature ownership", s_creatures, CreatureOwnership.ConfigEnabled);
            Note("Station routing", s_station, StationRouter.ConfigEnabled);
            FiresGhettoNetworkMod.Log?.LogWarning("TooManyLags FGN Addon (" + TmlGuid + ") is installed. It is NOT endorsed or tested by the FGN team; we don't know " +
                "whether it works. It overrides FGN settings and patches FGN's own code at runtime. " +
                (changed.Count > 0 ? "Overridden now: " + string.Join("; ", changed) + ". " : "") +
                "If something breaks, test with FGN alone before reporting it to us.");
        }
    }
}
