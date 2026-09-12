using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Valheim 1.0 runs the server's "left your area" check before the new position is stored, so a portal
    /// jump never tells watchers to drop the traveller. This re-runs vanilla's own ZDOSectorInvalidated once
    /// the position has landed; the check is idempotent, so only the missed notices are added. Server or
    /// listen host only, portals skipped, and it stands down for the session when the game already stores
    /// the position first or another mod (ValheimCommunityPatch) handles it.
    /// </summary>
    [HarmonyPatch(typeof(ZDO), nameof(ZDO.InternalSetPosition))]
    public static class TeleportGhostFix
    {
        private const string CompetitorToggleKey = "Fix Teleport Ghost Players";
        private const uint NotTracked = uint.MaxValue;
        private const float ReportIntervalSec = 300f;

        private enum Mode { StoodDown, Active, DeferToCompetitor }

        private static int _mainThreadId = -1;

        // Decided lazily on the first zone change that has someone to notify, once per ZDOMan (world session).
        // By then every plugin has applied its patches, so a competing fix is visible.
        private static ZDOMan _decidedFor;
        private static Mode _mode = Mode.StoodDown;
        private static ConfigEntryBase _competitorToggle;

        private static bool _peerCountersBroken;
        private static ZDOMan _firstCatchReportedFor;
        private static float _windowStart = -1f;
        private static int _windowObjects;
        private static int _windowNotices;
        private static int _windowExamplePrefab;

        public static void Init() => _mainThreadId = Thread.CurrentThread.ManagedThreadId;

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(ZDO __instance, out uint __state)
        {
            __state = NotTracked;
            if (!(FiresGhettoNetworkMod.ConfigFixTeleportGhosts?.Value ?? false)) return;
            ZNet znet = ZNet.instance;
            if ((object)znet == null || !znet.IsServer()) return;
            if (_mode == Mode.StoodDown && ReferenceEquals(_decidedFor, ZDOMan.instance)) return;
            __state = __instance.GetSectorIndex().Sector;
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(ZDO __instance, uint __state)
        {
            if (__state == NotTracked) return;
            try
            {
                if (__instance.GetSectorIndex().Sector == __state) return;
                if (Thread.CurrentThread.ManagedThreadId != _mainThreadId) return;

                ZDOMan man = ZDOMan.instance;
                if (man == null) return;

                Game game = Game.instance;
                if ((object)game != null && game.PortalPrefabHash.Contains(__instance.GetPrefab())) return;

                if (PeerCount(man) == 0) return;
                if (!ShouldRun(man)) return;

                int queuedBefore = QueuedNotices(man);
                man.ZDOSectorInvalidated(__instance);
                if (queuedBefore >= 0)
                    Report(__instance, QueuedNotices(man) - queuedBefore);
            }
            catch (Exception ex)
            {
                DisableForSession(ex);
            }
        }

        private static bool ShouldRun(ZDOMan man)
        {
            if (!ReferenceEquals(_decidedFor, man))
            {
                _decidedFor = man;
                _mode = Decide();
            }

            switch (_mode)
            {
                case Mode.Active: return true;
                case Mode.DeferToCompetitor: return !CompetitorToggleOn();
                default: return false;
            }
        }

        private static bool CompetitorToggleOn() => _competitorToggle?.BoxedValue is bool on && on;

        private static Mode Decide()
        {
            _competitorToggle = null;
            try
            {
                MethodInfo setPosition = AccessTools.Method(typeof(ZDO), nameof(ZDO.InternalSetPosition), new[] { typeof(Vector3) });
                MethodInfo setSector = AccessTools.Method(typeof(ZDO), "SetSector");
                MethodInfo managerInvalidated = AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.ZDOSectorInvalidated), new[] { typeof(ZDO) });
                Type peerType = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
                MethodInfo peerInvalidated = peerType == null ? null : AccessTools.Method(peerType, "ZDOSectorInvalidated", new[] { typeof(ZDO) });

                if (setPosition == null || setSector == null || managerInvalidated == null || peerInvalidated == null)
                    return StandDown("the Valheim 1.0 methods it corrects are not all present, so the game has changed");

                List<CodeInstruction> setPositionIL = PatchProcessor.GetOriginalInstructions(setPosition);
                int sectorCall = setPositionIL.FindIndex(ci => ci.operand is MethodInfo m && m.Name == "SetSector" && m.DeclaringType == typeof(ZDO));
                int positionStore = setPositionIL.FindIndex(ci => ci.opcode == OpCodes.Stfld && ci.operand is FieldInfo f && f.Name == "m_position" && f.DeclaringType == typeof(ZDO));
                if (sectorCall < 0 || positionStore < 0)
                    return StandDown("ZDO.InternalSetPosition no longer has the Valheim 1.0 shape");
                if (positionStore < sectorCall)
                    return StandDown("the game already stores the new position before SetSector (fixed upstream)");
                if (!PatchProcessor.GetOriginalInstructions(peerInvalidated).Any(ci => ci.operand is MethodInfo m && m.Name == nameof(ZDO.GetPosition) && m.DeclaringType == typeof(ZDO)))
                    return StandDown("ZDOPeer.ZDOSectorInvalidated no longer tests the ZDO's position");

                foreach (MethodInfo invalidationStep in new[] { setSector, managerInvalidated, peerInvalidated })
                {
                    HarmonyLib.Patch foreign = ForeignPatches(invalidationStep).FirstOrDefault();
                    if (foreign != null)
                        return StandDown($"'{foreign.owner}' patches {invalidationStep.DeclaringType?.Name}.{invalidationStep.Name} and owns sector invalidation");
                }

                HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(setPosition);
                if (info != null)
                {
                    HarmonyLib.Patch rewrite = (info.Transpilers ?? Enumerable.Empty<HarmonyLib.Patch>()).FirstOrDefault(IsForeign);
                    if (rewrite != null)
                        return StandDown($"'{rewrite.owner}' rewrites ZDO.InternalSetPosition");

                    HarmonyLib.Patch rerun = WrapperPatches(info).Where(IsForeign).FirstOrDefault(p => CallsInvalidation(p.PatchMethod, 0));
                    if (rerun != null)
                    {
                        string who = $"'{rerun.owner}' ({rerun.PatchMethod.DeclaringType?.FullName}.{rerun.PatchMethod.Name})";
                        _competitorToggle = FindCompetitorToggle(rerun.owner);
                        if (_competitorToggle == null)
                            return StandDown($"{who} already re-runs the check");

                        LoggerOptions.LogMessage($"[TeleportGhostFix] {who} already re-runs the check; deferring to it while its '{CompetitorToggleKey}' is on (currently {(CompetitorToggleOn() ? "on" : "off")}).");
                        return Mode.DeferToCompetitor;
                    }
                }

                LoggerOptions.LogMessage("[TeleportGhostFix] active: re-running vanilla's sector invalidation with the new position after every zone change.");
                return Mode.Active;
            }
            catch (Exception ex)
            {
                return StandDown($"the game code could not be verified ({ex.GetType().Name}: {ex.Message})");
            }
        }

        private static Mode StandDown(string reason)
        {
            LoggerOptions.LogMessage($"[TeleportGhostFix] standing down: {reason}.");
            return Mode.StoodDown;
        }

        private static bool IsForeign(HarmonyLib.Patch patch) => patch != null && patch.owner != FiresGhettoNetworkMod.PluginGUID;

        private static IEnumerable<HarmonyLib.Patch> WrapperPatches(HarmonyLib.Patches info)
        {
            IEnumerable<HarmonyLib.Patch> none = Enumerable.Empty<HarmonyLib.Patch>();
            return (info.Prefixes ?? none).Concat(info.Postfixes ?? none).Concat(info.Finalizers ?? none);
        }

        private static IEnumerable<HarmonyLib.Patch> ForeignPatches(MethodBase method)
        {
            HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(method);
            if (info == null) return Enumerable.Empty<HarmonyLib.Patch>();
            return WrapperPatches(info).Concat(info.Transpilers ?? Enumerable.Empty<HarmonyLib.Patch>()).Where(IsForeign);
        }

        // A competing fix calls ZDOSectorInvalidated from its patch, or from a helper in its own assembly.
        private static bool CallsInvalidation(MethodBase method, int depth)
        {
            if (method == null || depth > 2) return false;

            List<CodeInstruction> il;
            try { il = PatchProcessor.GetOriginalInstructions(method); }
            catch { return false; }

            Assembly home = method.DeclaringType?.Assembly;
            foreach (CodeInstruction ci in il)
            {
                if (!(ci.operand is MethodInfo called)) continue;
                if (called.Name == nameof(ZDOMan.ZDOSectorInvalidated)) return true;
                if (home != null && called.DeclaringType?.Assembly == home && CallsInvalidation(called, depth + 1)) return true;
            }
            return false;
        }

        private static ConfigEntryBase FindCompetitorToggle(string owner)
        {
            if (string.IsNullOrEmpty(owner) || !Chainloader.PluginInfos.TryGetValue(owner, out BepInEx.PluginInfo plugin)) return null;
            if (plugin?.Instance == null) return null;

            foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> entry in (IEnumerable<KeyValuePair<ConfigDefinition, ConfigEntryBase>>)plugin.Instance.Config)
            {
                if (entry.Key.Key == CompetitorToggleKey && entry.Value?.BoxedValue is bool)
                    return entry.Value;
            }
            return null;
        }

        private static void DisableForSession(Exception ex)
        {
            ZDOMan man = ZDOMan.instance;
            if (_mode == Mode.StoodDown && ReferenceEquals(_decidedFor, man)) return;
            _decidedFor = man;
            _mode = Mode.StoodDown;
            LoggerOptions.LogWarning($"[TeleportGhostFix] turned off for this session after an error; vanilla behaviour is unchanged. {ex}");
        }

        private static void Report(ZDO zdo, int notices)
        {
            if (notices <= 0) return;

            int prefab = zdo.GetPrefab();
            if (!ReferenceEquals(_firstCatchReportedFor, _decidedFor))
            {
                _firstCatchReportedFor = _decidedFor;
                LoggerOptions.LogMessage($"[TeleportGhostFix] first catch this session: '{PrefabName(prefab)}' left the area of {notices} peer(s), who were told to drop it. Vanilla's check, still testing where it came from, sent nothing.");
            }

            _windowObjects++;
            _windowNotices += notices;
            _windowExamplePrefab = prefab;

            float now = Time.realtimeSinceStartup;
            if (_windowStart < 0f) _windowStart = now;
            if (now - _windowStart < ReportIntervalSec) return;

            LoggerOptions.LogInfo($"[TeleportGhostFix] last {now - _windowStart:F0}s: {_windowObjects} object(s) left a peer's area without vanilla noticing; {_windowNotices} drop notice(s) sent (e.g. '{PrefabName(_windowExamplePrefab)}').");
            _windowStart = now;
            _windowObjects = 0;
            _windowNotices = 0;
        }

        private static string PrefabName(int hash)
        {
            ZNetScene scene = ZNetScene.instance;
            GameObject prefab = (object)scene != null ? scene.GetPrefab(hash) : null;
            return prefab != null ? prefab.name : hash.ToString();
        }

        private static int PeerCount(ZDOMan man)
        {
            if (_peerCountersBroken) return -1;
            try { return PeerQueues.Count(man); }
            catch (Exception ex) { PeerCountersUnavailable(ex); return -1; }
        }

        private static int QueuedNotices(ZDOMan man)
        {
            if (_peerCountersBroken) return -1;
            try { return PeerQueues.Total(man); }
            catch (Exception ex) { PeerCountersUnavailable(ex); return -1; }
        }

        private static void PeerCountersUnavailable(Exception ex)
        {
            _peerCountersBroken = true;
            LoggerOptions.LogMessage($"[TeleportGhostFix] peer counters unavailable ({ex.GetType().Name}); the fix still runs, its reports are off.");
        }

        // The only code that names ZDOMan's private nested ZDOPeer type. A runtime access check here can switch
        // the counters off, never the fix.
        private static class PeerQueues
        {
            private static AccessTools.FieldRef<ZDOMan, List<ZDOMan.ZDOPeer>> _peers;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static int Count(ZDOMan man) => Peers(man)?.Count ?? -1;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static int Total(ZDOMan man)
            {
                List<ZDOMan.ZDOPeer> peers = Peers(man);
                if (peers == null) return -1;
                int total = 0;
                for (int i = 0; i < peers.Count; i++)
                    total += peers[i]?.m_invalidSector.Count ?? 0;
                return total;
            }

            private static List<ZDOMan.ZDOPeer> Peers(ZDOMan man)
            {
                if (_peers == null)
                    _peers = AccessTools.FieldRefAccess<ZDOMan, List<ZDOMan.ZDOPeer>>("m_peers");
                return _peers(man);
            }
        }
    }
}
