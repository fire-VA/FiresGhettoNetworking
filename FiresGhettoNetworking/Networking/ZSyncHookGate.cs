using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// FGN's per-tick ZSyncTransform hooks cost several ms a second on a busy scene, almost all of it Harmony dispatch rather
    /// than the work inside, so the OwnerSync prefix is attached only while one of its three
    /// features can act, and detached otherwise, so a server that runs none of them pays nothing per object:
    ///   - Limit Transform Write Rate, with at least one listed prefab and a rate above 0;
    ///   - Send Remote Motion Timestamps;
    ///   - Server-Side Simulation active on this dedicated server (the kill-plane rescue).
    /// With any of them on, the same prefix runs exactly as before. Re-evaluated when any of those settings change (they are
    /// server-synced) and when Server-Side Simulation starts. Logs "[ZSyncHooks] OwnerSync attached|detached (why)".
    /// </summary>
    [HarmonyPatch]   // its ZNet session hooks (the handoff gate) are attached by PatchAll
    internal static class ZSyncHookGate
    {
        private const string HarmonyId = "com.Fire.FiresGhettoNetworkMod.ownersync";
        private static Harmony s_harmony;
        private static bool s_attached, s_hooked;
        internal static bool OwnerSyncAttached => s_attached;
        internal static bool HandoffAttached => s_handoffAttached;

        internal static void Init(ConfigEntry<bool> limiter, ConfigEntry<string> prefabs, ConfigEntry<float> rate, ConfigEntry<bool> stamps)
        {
            if (!s_hooked)
            {
                s_hooked = true;
                if (limiter != null) limiter.SettingChanged += (_, __) => Refresh("Limit Transform Write Rate changed");
                if (prefabs != null) prefabs.SettingChanged += (_, __) => Refresh("Transform Write Rate Objects changed");
                if (rate != null) rate.SettingChanged += (_, __) => Refresh("Transform Writes Per Second changed");
                if (stamps != null) stamps.SettingChanged += (_, __) => Refresh("Send Remote Motion Timestamps changed");
            }
            Refresh("startup");
        }

        // ---------------- the CustomFixedUpdate prefix (OwnershipHandoff), off for a solo host ----------------
        // Its three jobs need another peer - skip ClientSync for a REMOTE player's body
        // (RemoteMotion / RemoteArrows), and snap a body when ownership arrives from someone else. A solo host (a local world, no peer
        // connected) owns everything from the start, so the prefix would only cost Harmony dispatch. Attached when
        // this process is a dedicated server or has any peer (a client always has its server; a host the moment someone connects, before
        // that peer's handshake), detached when the last peer leaves; same body when attached. Logged "[ZSyncHooks] handoff …".
        private const string HandoffId = "com.Fire.FiresGhettoNetworkMod.handoff";
        private static Harmony s_handoffHarmony;
        private static bool s_handoffAttached;

        internal static void RefreshHandoff(string why, int peersAfter)
        {
            try
            {
                bool dedicated = ZNet.instance != null && ZNet.instance.IsDedicated();
                bool want = ZNet.instance != null && (dedicated || peersAfter > 0);
                if (want == s_handoffAttached) return;
                if (s_handoffHarmony == null) s_handoffHarmony = new Harmony(HandoffId);
                var target = AccessTools.Method(typeof(ZSyncTransform), nameof(ZSyncTransform.CustomFixedUpdate));
                if (want) s_handoffHarmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(OwnershipHandoffPatches), nameof(OwnershipHandoffPatches.ZSyncTransform_CustomFixedUpdate_Prefix))));
                else s_handoffHarmony.Unpatch(target, HarmonyPatchType.Prefix, HandoffId);
                s_handoffAttached = want;
                FiresGhettoNetworkMod.Log?.LogDebug($"[ZSyncHooks] handoff prefix {(want ? "attached" : "detached")} ({why}; {(dedicated ? "dedicated" : peersAfter + " peer(s)")})");
            }
            catch (Exception ex) { FiresGhettoNetworkMod.Log?.LogWarning($"[ZSyncHooks] handoff gate failed ({why}): {ex.GetType().Name}: {ex.Message}"); }
        }

        [HarmonyPatch(typeof(ZNet), "Awake"), HarmonyPostfix]
        private static void SessionStart() { RefreshHandoff("session start", ZNet.instance != null ? ZNet.instance.GetPeers().Count : 0); }   // 1.6.63: stats are pulled through FgnApi.GetStatsRows

        [HarmonyPatch(typeof(ZNet), "OnNewConnection"), HarmonyPostfix]
        private static void PeerJoined() => RefreshHandoff("a peer connected", ZNet.instance != null ? ZNet.instance.GetPeers().Count : 1);

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect)), HarmonyPostfix]
        private static void PeerLeft() => RefreshHandoff("a peer left", ZNet.instance != null ? ZNet.instance.GetPeers().Count : 0);

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown)), HarmonyPostfix]
        private static void SessionEnd() => RefreshHandoff("session end", 0);

        internal static void Refresh(string why)
        {
            try
            {
                bool limiter = TransformWriteRate.ConfigEnabled != null && TransformWriteRate.ConfigEnabled.Value
                    && !string.IsNullOrWhiteSpace(TransformWriteRate.ConfigPrefabs?.Value)
                    && TransformWriteRate.ConfigWritesPerSecond != null && TransformWriteRate.ConfigWritesPerSecond.Value > 0f;
                bool stamps = RemoteMotion.ConfigSendStamps != null && RemoteMotion.ConfigSendStamps.Value;
                bool rescue = ServerAuthorityPatches.SimulationActive;
                bool want = limiter || stamps || rescue;
                string features = $"write limit {(limiter ? "on" : "off")}, motion stamps {(stamps ? "on" : "off")}, SSS rescue {(rescue ? "on" : "off")}";
                if (want == s_attached) { FiresGhettoNetworkMod.Log?.LogDebug($"[ZSyncHooks] OwnerSync stays {(want ? "attached" : "detached")} ({why}; {features})"); return; }
                if (s_harmony == null) s_harmony = new Harmony(HarmonyId);
                var target = AccessTools.Method(typeof(ZSyncTransform), "OwnerSync");
                if (want) s_harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(TransformWriteRate), nameof(TransformWriteRate.OwnerSync_Prefix))));
                else s_harmony.Unpatch(target, HarmonyPatchType.Prefix, HarmonyId);
                s_attached = want;
                FiresGhettoNetworkMod.Log?.LogDebug($"[ZSyncHooks] OwnerSync {(want ? "attached" : "detached")} ({why}; {features})");
            }
            catch (Exception ex)
            {
                FiresGhettoNetworkMod.Log?.LogWarning($"[ZSyncHooks] OwnerSync gate failed ({why}): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
