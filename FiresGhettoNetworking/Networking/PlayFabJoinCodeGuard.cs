using HarmonyLib;
using PlayFab.MultiplayerModels;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Crossplay join-code check (Fire, 2026-10-05). A report on FGN 1.5.36: a dedicated server registered its PlayFab lobby and
    /// session, then threw "NullReferenceException at ZPlayFabMatchmaking.OnCheckJoinCodeSuccess [IL 0x00070]". In the 1.0.16 IL,
    /// 0x70 is "ldfld LobbySummary::Owner" on Lobbies[0]: the search came back with one entry that was itself null, so the
    /// server never activated its session and crossplay clients found no lobby.
    ///
    /// Every check's result goes to the log at FGN's Debug level (the evidence for the cause, whatever it is). A result holding a
    /// null entry takes vanilla's own "no lobby yet" path instead of throwing: retry while retries remain, else unregister, and
    /// says so at Warning level, so the guard never hides that it fired.
    /// </summary>
    [HarmonyPatch]
    public static class PlayFabJoinCodeGuard
    {
        [HarmonyPatch(typeof(ZPlayFabMatchmaking), "OnCheckJoinCodeSuccess"), HarmonyPrefix]
        static bool CheckJoinCodeResult(ZPlayFabMatchmaking __instance, FindLobbiesResult result)
        {
            var lobbies = result?.Lobbies;
            int count = lobbies != null ? lobbies.Count : -1, nulls = 0, noOwner = 0;
            if (lobbies != null)
                foreach (LobbySummary lobby in lobbies)
                {
                    if (lobby == null) nulls++;
                    else if (lobby.Owner == null) noOwner++;
                }
            LoggerOptions.LogDebug($"[PlayFabJoinCode] join-code check result: {(count < 0 ? "no lobby list" : $"{count} lobby(ies)")}, "
                + $"{nulls} null entry(ies), {noOwner} without an owner; retries left {__instance.m_retries}");
            if (count >= 0 && nulls == 0 && noOwner == 0) return true;   // vanilla handles it as always

            // A list vanilla would throw on: treat it as "no usable lobby yet", vanilla's own retry path.
            if (__instance.m_retries > 0)
            {
                __instance.m_retries--;
                LoggerOptions.LogWarning($"[PlayFabJoinCode] GUARD: the join-code check got {(count < 0 ? "no lobby list" : $"{nulls} null / {noOwner} ownerless of {count} lobby(ies)")}; "
                    + $"vanilla would throw (NullReferenceException in OnCheckJoinCodeSuccess). Checking again ({__instance.m_retries} retries left).");
                __instance.ScheduleJoinCodeCheck();
            }
            else
            {
                LoggerOptions.LogWarning("[PlayFabJoinCode] GUARD: the join-code check still has no usable lobby after every retry; unregistering, as vanilla does when none comes back.");
                __instance.UnregisterServer();
            }
            return false;
        }
    }
}
