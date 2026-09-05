using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Ownership-handoff pre-snap for creatures.
    ///
    /// Vanilla ZSyncTransform.OwnerSync snaps an object onto zdo.GetPosition() on the frame
    /// it becomes owner (the m_wasOwner rising edge, ZSyncTransform.cs:85-108) — but that
    /// runs from CustomLateUpdate (MonoUpdaters.cs:80), AFTER the FixedUpdate in which the
    /// new owner already ran BaseAI.UpdateAI (MonoUpdaters.cs:39) and
    /// Character.CustomFixedUpdate (:42), and after Unity stepped physics.
    ///
    /// Until the previous owner's final update lands, a non-owner dead-reckons the transform
    /// forward on last-known velocity (ClientSync extrapolates, clamped to 2s), so that first
    /// owned step can run from a position that drifted into — or through — nearby geometry.
    /// Unity then depenetrates along the shortest axis at maxDepenetrationVelocity = 2, which
    /// for a thin wall is frequently the OUTSIDE face. Vanilla's late snap restores position
    /// and rotation but NOT velocity: m_syncBodyVelocity defaults false (ZSyncTransform.cs:16),
    /// so the kick that bogus step produced outlives the correction that follows it.
    ///
    /// This performs vanilla's own snap at the earliest point of FixedUpdate — ZSyncTransform
    /// is updated at MonoUpdaters.cs:30, ahead of both BaseAI and Character — so the step
    /// starts where the ZDO says the creature is and never generates the kick to begin with.
    ///
    /// Deliberately conservative:
    ///   * velocity is left alone. Zeroing it would stall a legitimately-moving creature on
    ///     every handoff, and ZDOMan.ReleaseNearbyZDOS migrates ownership every 2s.
    ///   * m_wasOwner is NOT written. Vanilla's LateUpdate pass still runs and lands on the
    ///     same values, so this is purely the same correction applied earlier.
    ///   * creatures only. Ships and carts have their own separately tuned handling.
    ///
    /// Relevant to this mod specifically: anything that widens the gap between ZDO updates
    /// (distant-ZDO throttling and AI LOD, both ServerAuthority-gated) lengthens the
    /// dead-reckoning window and makes the drift worse, so FGN's own optimizations raise the
    /// odds of the very step this removes.
    /// </summary>
    [HarmonyPatch]
    public static class OwnershipHandoffPatches
    {
        private static readonly AccessTools.FieldRef<ZSyncTransform, bool> _wasOwnerRef
            = AccessTools.FieldRefAccess<ZSyncTransform, bool>("m_wasOwner");

        // Below this the snap is not worth the transform write; vanilla's LateUpdate pass
        // applies the identical correction moments later regardless.
        private const float MinDriftToSnap = 0.05f;

        // Drift beyond this is worth surfacing — it means a creature was about to simulate a
        // physics step meters from where the world believes it is. Rate-limited so a busy
        // server cannot spam the log.
        private const float DriftReportMeters = 2f;
        private const float ReportIntervalSec = 30f;
        private static float _nextReportTime;

        [HarmonyPatch(typeof(ZSyncTransform), nameof(ZSyncTransform.CustomFixedUpdate))]
        [HarmonyPrefix]
        public static void ZSyncTransform_CustomFixedUpdate_PreSnapOnOwnershipGain(
            ZSyncTransform __instance,
            ZNetView ___m_nview,
            Rigidbody ___m_body)
        {
            if (__instance == null || ___m_nview == null || !___m_nview.IsValid())
                return;

            ZDO zdo = ___m_nview.GetZDO();
            if (zdo == null || !zdo.IsOwner())
                return;

            // Already owner last LateUpdate → not a handoff frame, nothing to correct.
            if (_wasOwnerRef(__instance))
                return;

            var character = __instance.GetComponent<Character>();
            if (character == null || character is Player)
                return;

            Vector3 target = zdo.GetPosition();
            float drift = Vector3.Distance(__instance.transform.position, target);
            if (drift < MinDriftToSnap)
                return;

            bool transformWritten = false;
            if (__instance.m_syncPosition)
            {
                __instance.transform.position = target;
                transformWritten = true;
            }
            if (__instance.m_syncRotation)
            {
                __instance.transform.rotation = zdo.GetRotation();
                transformWritten = true;
            }
            if (transformWritten && ___m_body != null)
                Physics.SyncTransforms();

            if (drift >= DriftReportMeters && Time.time >= _nextReportTime)
            {
                _nextReportTime = Time.time + ReportIntervalSec;
                LoggerOptions.LogInfo(
                    $"[OwnershipHandoff] '{character.name}' was {drift:F1}m from its ZDO position when this peer took ownership; "
                    + $"pre-snapped before the physics step (vanilla would have simulated there first). "
                    + $"Further reports suppressed for {ReportIntervalSec:F0}s.");
            }
        }
    }
}
