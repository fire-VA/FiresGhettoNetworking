using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.15 ([lead] 10-30: Fire's client owned 10 town Dvergers with server-side simulation on): Debug only, on a client, the first
    /// time per prefab that THIS game makes itself the owner of a creature (Character, not a player, not a tame), with the calling
    /// code, so the path that hands creatures to a player is named instead of guessed. At most 24 prefabs a session.
    /// "[OwnerClaim] this game took Dverger (owner was 0 / server / another player): Class.Method < Class.Method < ..."
    /// </summary>
    [HarmonyPatch]
    internal static class OwnershipClaimProbe
    {
        private static readonly HashSet<int> s_logged = new HashSet<int>();
        private static readonly Dictionary<int, bool> s_isCreature = new Dictionary<int, bool>();

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwner)), HarmonyPrefix]
        private static void Before(ZDO __instance, long uid, out long __state)
        {
            __state = __instance != null ? __instance.GetOwner() : 0L;
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwner)), HarmonyPostfix]
        private static void After(ZDO __instance, long uid, long __state)
        {
            if (!LoggerOptions.DebugEnabled || __instance == null || s_logged.Count >= 24) return;
            if (ZNet.instance == null || ZNet.instance.IsServer() || uid == 0L || uid != ZDOMan.GetSessionID() || __state == uid) return;
            int h = __instance.m_prefab;
            if (s_logged.Contains(h) || !IsCreature(h) || __instance.GetBool(ZDOVars.s_tamed)) return;
            s_logged.Add(h);
            var st = new StackTrace(2, false);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < st.FrameCount && i < 8; i++)
            {
                var m = st.GetFrame(i).GetMethod();
                if (m == null) continue;
                sb.Append(sb.Length == 0 ? "" : " < ").Append(m.DeclaringType != null ? m.DeclaringType.Name + "." : "").Append(m.Name);
            }
            string was = __state == 0L ? "nobody" : (ZNet.instance.GetServerPeer() != null && __state == ZNet.instance.GetServerPeer().m_uid ? "the server" : "peer " + __state);
            LoggerOptions.LogDebug($"[OwnerClaim] this game took {Name(h)} (owner was {was}): {sb}");
        }

        private static bool IsCreature(int h)
        {
            if (s_isCreature.TryGetValue(h, out bool c)) return c;
            GameObject p = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(h) : null;
            c = p != null && p.GetComponent<Character>() != null && p.GetComponent<Player>() == null;
            s_isCreature[h] = c;
            return c;
        }

        private static string Name(int h)
        {
            GameObject p = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(h) : null;
            return p != null ? p.name : h.ToString();
        }
    }
}
