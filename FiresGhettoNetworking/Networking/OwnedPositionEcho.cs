using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// The owner of an object is the authority over where it is. Vanilla ZDOMan.RPC_ZDOData still overwrites an OWNED object's
    /// position when the server's copy carries a higher data revision (a server-side write to that object, such as a world script
    /// setting data on a player), and ZSyncTransform.OwnerSync only rewrites the position when the body has moved since its last
    /// write. A body that stands still keeps the server's stale position in its ZDO: during a teleport the player is pinned at the
    /// target while it waits for the area, so its ZDO stays in the old sector, the new area's object list leaves the player out, and
    /// the instance cleanup destroys the local player (2026-10-02, the Enigma world: a join script's teleport plus a goto).
    /// When received data moves an object this client owns and runs a ZSyncTransform for, the owner's real position is written back
    /// as soon as the receive ends (see s_moved), before anything in that frame can act on the stale sector. Nothing is sent that
    /// vanilla would not send: it is the owner's ordinary position write.
    ///
    /// RPC_ZDOData applies the incoming owner (SetOwnerInternal) before InternalSetPosition, so IsOwner() here already reflects the
    /// received data: a package that hands the object to someone else is left alone, only data that moves an object we still own is
    /// answered. Runs on every peer, the dedicated server included: a server-owned body (server-side simulation, ships, creatures)
    /// whose ZDO a client's write moved gets the same correction, and with no ZSyncTransform instance nothing happens.
    /// </summary>
    [HarmonyPatch]
    internal static class OwnedPositionEcho
    {
        private static bool s_receiving;
        private static float s_nextLog;
        internal static long Restored;

        // Objects whose owned position a receive just overwrote, put back when the receive ends. Not inside InternalSetPosition:
        // vanilla records the received revision for the server (peer.m_zdos) right after it, so a write-back there would be
        // recorded as already sent and the server would keep the stale position. Not left to the next OwnerSync either: that runs
        // in LateUpdate, and ZNetScene.Update's RemoveObjects runs before it in the same frame, sees the owned object's ZDO in the
        // stale sector and destroys it (the local player, mid-teleport, 2026-10-02 Enigma: EWP rules writing the player on
        // every teleport landing).
        private static readonly List<KeyValuePair<ZDO, ZNetView>> s_moved = new List<KeyValuePair<ZDO, ZNetView>>();

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData"), HarmonyPrefix]
        private static void Receive_Prefix() => s_receiving = true;

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData"), HarmonyFinalizer]
        private static void Receive_Finalizer()
        {
            s_receiving = false;
            if (s_moved.Count == 0) return;
            for (int i = 0; i < s_moved.Count; i++)
            {
                ZDO zdo = s_moved[i].Key;
                ZNetView view = s_moved[i].Value;
                if (view == null || view.GetZDO() != zdo || !zdo.IsOwner()) continue;
                Vector3 body = view.transform.position, was = zdo.GetPosition();
                if (body == was) continue;
                // The owner's own write, as OwnerSync would make it: new sector, a revision above the received one, so it is
                // sent back up and the server's copy follows the body again.
                zdo.InternalSetPosition(body);
                ZSyncTransform sync = view.GetComponent<ZSyncTransform>();
                if (sync != null) sync.m_positionCached = body;
                Restored++;
                if (Time.unscaledTime < s_nextLog) continue;
                s_nextLog = Time.unscaledTime + 10f;
                LoggerOptions.LogDebug($"[OwnedPositionEcho] received data moved an object this client owns ({view.gameObject.name}) to ({was.x:0}, {was.z:0}), "
                    + $"{Vector3.Distance(was, body):0} m from where it is; put back to ({body.x:0}, {body.z:0}) ({Restored} so far)");
            }
            s_moved.Clear();
        }

        // InternalSetPosition is every owner's per-frame position write, so outside a receive this does nothing but the flag test.
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.InternalSetPosition)), HarmonyPrefix]
        private static void SetPosition_Prefix(ZDO __instance, out Vector3 __state)
            => __state = s_receiving ? __instance.GetPosition() : default;

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.InternalSetPosition)), HarmonyPostfix]
        private static void SetPosition_Postfix(ZDO __instance, Vector3 __state)
        {
            if (!s_receiving || !__instance.IsOwner() || __instance.GetPosition() == __state) return;
            ZNetScene scene = ZNetScene.instance;
            ZNetView view = scene != null ? scene.FindInstance(__instance) : null;
            if (view == null || view.GetComponent<ZSyncTransform>() == null) return;
            s_moved.Add(new KeyValuePair<ZDO, ZNetView>(__instance, view));
        }
    }
}
