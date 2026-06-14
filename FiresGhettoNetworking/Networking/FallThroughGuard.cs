using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Stops dropped items and tombstones from falling through structures during the
    /// zone-load physics race.
    ///
    /// On spawn an ItemDrop / TombStone is a non-kinematic Rigidbody under gravity. If it
    /// instantiates above a support whose collider isn't live in the physics scene yet — the
    /// floor streamed in a frame later, a custom non-Solid build piece, terrain not generated
    /// yet — gravity pulls it through before the support exists, and vanilla only self-corrects
    /// against TERRAIN (not structures) every ~10 s. The item ends up under the world.
    ///
    /// Fix: on spawn, if NOTHING is beneath the body, freeze it kinematic and poll each physics
    /// tick for support to appear; release once it does, or after a short timeout. So it rests
    /// on its floor the instant that floor loads, and a genuinely-unsupported drop still falls —
    /// just a beat later. No send-order or ObjectType changes (that was the 1.3.6 misfire); this
    /// is purely local physics on the owning peer.
    ///
    /// Owner-only: a non-owner's body is already kinematic (ZSyncTransform) and just follows the
    /// owner's synced position, so we only guard the peer that actually runs the physics.
    /// </summary>
    [HarmonyPatch]
    public static class FallThroughGuard
    {
        private const float MaxFreezeSec   = 3f;
        private const float SupportRayUp   = 0.3f;
        private const float SupportRayDown = 1.0f;

        private static int  s_groundMask;
        private static bool s_maskReady;

        private static int GroundMask()
        {
            if (!s_maskReady)
            {
                s_groundMask = LayerMask.GetMask(
                    "Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
                s_maskReady = true;
            }
            return s_groundMask;
        }

        [HarmonyPatch(typeof(ItemDrop), "Awake")]
        [HarmonyPostfix]
        public static void ItemDrop_Awake(ItemDrop __instance)
            => Begin(__instance != null ? __instance.gameObject : null);

        [HarmonyPatch(typeof(TombStone), "Awake")]
        [HarmonyPostfix]
        public static void TombStone_Awake(TombStone __instance)
            => Begin(__instance != null ? __instance.gameObject : null);

        private static void Begin(GameObject go)
        {
            if (go == null || FiresGhettoNetworkMod.Instance == null) return;
            if (!(FiresGhettoNetworkMod.ConfigEnableFallThroughGuard?.Value ?? true)) return;
            FiresGhettoNetworkMod.Instance.StartCoroutine(Guard(go));
        }

        private static IEnumerator Guard(GameObject go)
        {
            // One frame so ZNetView binds and the spawn position (set at Instantiate) applies.
            yield return null;
            if (go == null) yield break;

            ZNetView nview = go.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) yield break; // only the physics owner

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb == null || rb.isKinematic) yield break;

            // Something already under it → no race; let vanilla physics settle it normally.
            if (HasSupport(go.transform.position)) yield break;

            // Freeze until support streams in beneath it (or we give up).
            rb.isKinematic    = true;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;

            float waited = 0f;
            while (waited < MaxFreezeSec)
            {
                yield return new WaitForFixedUpdate();
                if (go == null) yield break;
                waited += Time.fixedDeltaTime;
                if (HasSupport(go.transform.position)) break;
            }

            // Resume physics — it now rests on the loaded support, or falls if there
            // genuinely is none (e.g. dropped over a ravine). Re-check ownership: it could
            // have transferred while we held it.
            if (go == null) yield break;
            Rigidbody rbNow = go.GetComponent<Rigidbody>();
            ZNetView nvNow = go.GetComponent<ZNetView>();
            if (rbNow != null && nvNow != null && nvNow.IsValid() && nvNow.IsOwner())
                rbNow.isKinematic = false;
        }

        private static bool HasSupport(Vector3 pos)
        {
            return Physics.Raycast(
                pos + Vector3.up * SupportRayUp, Vector3.down,
                SupportRayUp + SupportRayDown, GroundMask(), QueryTriggerInteraction.Ignore);
        }
    }
}
