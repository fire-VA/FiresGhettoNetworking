using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Applies vanilla's ownership-edge snap (ZSyncTransform.OwnerSync) at the top of FixedUpdate instead of in LateUpdate,
    /// so a creature's first owned physics step starts from the ZDO position rather than a dead-reckoned one; ships and carts
    /// also take their ZDO velocity and wait for the ground under them (VehicleOwnershipEdge).
    /// </summary>
    [HarmonyPatch]
    public static class OwnershipHandoffPatches
    {
        private static readonly AccessTools.FieldRef<ZSyncTransform, bool> _wasOwnerRef
            = AccessTools.FieldRefAccess<ZSyncTransform, bool>("m_wasOwner");
        private static readonly AccessTools.FieldRef<ZNetView, ZDO> _zdoRef
            = AccessTools.FieldRefAccess<ZNetView, ZDO>("m_zdo");

        // Below this the snap is not worth the transform write; vanilla's LateUpdate pass
        // applies the identical correction moments later regardless.
        private const float MinDriftToSnap = 0.05f;

        // Drift beyond this is worth surfacing — it means a creature was about to simulate a
        // physics step meters from where the world believes it is. Rate-limited so a busy
        // server cannot spam the log.
        private const float DriftReportMeters = 2f;
        private const float ReportIntervalSec = 30f;
        private static float _nextReportTime;

        /// <summary>FGN's one CustomFixedUpdate hook: vanilla's ClientSync stays off a player Remote Motion draws (it would move the
        /// body by its own lerp and velocity between frames), then the ownership-edge snap below.</summary>
        // Attached / detached by ZSyncHookGate - on while this process has any peer (or is a dedicated
        // server), off for a solo host, where nothing it does can happen (no remote bodies, no ownership changing hands).
        public static bool ZSyncTransform_CustomFixedUpdate_Prefix(ZSyncTransform __instance, ZNetView ___m_nview, Rigidbody ___m_body)
        {
            long perf = FgnPerf.Begin();
            bool runOriginal = !RemoteMotion.Handles(___m_nview) && !RemoteArrows.Handles(___m_nview);
            if (runOriginal) ZSyncTransform_CustomFixedUpdate_PreSnapOnOwnershipGain(__instance, ___m_nview, ___m_body);
            FgnPerf.End(FgnPerf.Point.SyncTransformFixed, perf);
            return runOriginal;
        }

        private static void ZSyncTransform_CustomFixedUpdate_PreSnapOnOwnershipGain(
            ZSyncTransform __instance,
            ZNetView ___m_nview,
            Rigidbody ___m_body)
        {
            // Hot path (thousands of calls a second, almost all of them returning here): the vanilla tests in the
            // same order, read straight from the fields (no Unity == null, which goes
            // native, and no IsValid/GetZDO/IsOwner wrapper calls on ZNetView). ZNetView.IsValid() = m_zdo != null && m_zdo.IsValid().
            ZDO zdo = (object)___m_nview != null ? _zdoRef(___m_nview) : null;
            if (zdo == null || !zdo.IsValid())
                return;
            bool owner = zdo.IsOwner();

            if (VehicleOwnershipEdge.HoldsAny && VehicleOwnershipEdge.KeepHolding(__instance, owner))
                return;

            // Not the owner, or already owner last LateUpdate → not a handoff frame, nothing to correct.
            if (!owner || _wasOwnerRef(__instance))
                return;

            var character = __instance.GetComponent<Character>();
            if (character == null)
            {
                VehicleOwnershipEdge.OnOwnershipGained(__instance, zdo, ___m_body);
                return;
            }
            if (character is Player)
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
                LoggerOptions.LogDebug(
                    $"[OwnershipHandoff] '{character.name}' was {drift:F1}m from its ZDO position when this peer took ownership; "
                    + $"pre-snapped before the physics step (vanilla would have simulated there first). "
                    + $"Further reports suppressed for {ReportIntervalSec:F0}s.");
            }
        }
    }
}
