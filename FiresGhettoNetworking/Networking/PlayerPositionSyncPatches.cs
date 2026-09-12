using BepInEx.Configuration;
using FiresGhettoNetworkMod.AutoTune;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [HarmonyPatch]
    public static class PlayerPositionSyncPatches
    {
        public static ConfigEntry<bool> ConfigEnablePlayerPositionBoost;
        public static ConfigEntry<float> ConfigPlayerPositionUpdateMultiplier;
        public static ConfigEntry<bool> ConfigEnableClientInterpolation;
        public static ConfigEntry<bool> ConfigEnablePlayerPrediction;
        public static ConfigEntry<float> ConfigSmoothingMinInterval;
        public static ConfigEntry<float> ConfigSmoothingMaxInterval;

        public static void Init(ConfigFile config)
        {
            ConfigEnablePlayerPositionBoost = config.Bind(
                "03 - Player Sync",
                "Enable High-Frequency Position Updates",
                true,
                "Boosts server send priority for player ZDOs so they sync before terrain/object ZDOs.\n" +
                "Reduces the floaty delayed movement you see on other players.\n" +
                "SERVER-ONLY — no effect on client.");

            ConfigPlayerPositionUpdateMultiplier = config.Bind(
                "03 - Player Sync",
                "Position Update Multiplier",
                2.5f,
                new ConfigDescription(
                    "How aggressively player ZDOs are prioritized over other ZDOs (1.0 = vanilla, 2.5 = recommended).\n" +
                    "Higher values push player positions to the front of the send queue more strongly.",
                    new AcceptableValueRange<float>(1.0f, 5.0f)));

            ConfigEnableClientInterpolation = config.Bind(
                "03 - Player Sync",
                "Enable Client-Side Interpolation",
                false,
                "Smooths other players' movement on your client by interpolating between received network positions.\n" +
                "Eliminates the snapping/teleporting caused by discrete 50ms network updates.\n" +
                "Disabled by default — the rest of the mod's networking improvements (higher update rate, server\n" +
                "ZDO priority boost, larger Steam buffers) usually deliver positions smoothly enough on their own,\n" +
                "and interpolation adds a small render-lag that some players prefer to avoid. Turn ON if you still\n" +
                "see other players snap/teleport even on a healthy connection.\n" +
                "CLIENT-ONLY — no server impact.");

            ConfigEnablePlayerPrediction = config.Bind(
                "03 - Player Sync",
                "Enable Client-Side Prediction",
                false,
                "Extrapolates other players' positions forward between network updates using their last known velocity.\n" +
                "Can help on high latency (>100ms) but may cause overshooting at low ping.\n" +
                "Disabled by default — only enable if interpolation alone feels laggy.\n" +
                "CLIENT-ONLY — no server impact.");

            ConfigSmoothingMinInterval = config.Bind(
                "03 - Player Sync",
                "Smoothing Min Interval (s)",
                0.0f,
                new ConfigDescription(
                    "When a remote player's packets arrive faster than this interval (seconds),\n" +
                    "smoothing is disabled entirely and vanilla movement renders directly.\n" +
                    "Default 0 = always smooth (recommended). The earlier default of 0.05\n" +
                    "(vanilla 20Hz) collapsed the smoothing-strength formula to ~0 under\n" +
                    "healthy traffic, which made BOTH this slider and Smoothing Max feel\n" +
                    "like no-ops and produced visible snaps every time inter-packet jitter\n" +
                    "crossed the threshold. Raise above 0 only if you specifically want\n" +
                    "fast packets to bypass smoothing (LAN / very low-latency servers).",
                    new AcceptableValueRange<float>(0.0f, 0.20f)));

            ConfigSmoothingMaxInterval = config.Bind(
                "03 - Player Sync",
                "Smoothing Max Interval (s)",
                0.20f,
                new ConfigDescription(
                    "When a remote player's packets arrive slower than this interval (seconds),\n" +
                    "smoothing runs at full strength. Between Min and Max the strength fades\n" +
                    "in linearly so the handoff is invisible. Raise it for a more aggressive\n" +
                    "fade-in (less smoothing on borderline-laggy connections); lower it for a\n" +
                    "snappier ramp to full smoothing.",
                    new AcceptableValueRange<float>(0.05f, 0.50f)));
        }

        // Player ZDO sort boost is handled in ZDOThrottlingPatches.

        private const float DefaultPacketIntervalSeconds = 0.05f;
        private const float MinPacketIntervalSeconds = 0.005f;
        private const float MaxPacketIntervalSeconds = 0.5f;
        private const float PacketIntervalAveragingLerp = 0.15f;

        private const float TeleportVelocityMetersPerSecond = 30f;
        private const float AccelerationSmoothingLerp = 0.25f;
        private const float VelocityDirectionLerp = 0.5f;
        private const float VelocityMagnitudeLerp = 0.7f;
        private const float RenderVelocityLerp = 0.6f;
        private const float StationaryDecayLerp = 0.9f;
        private const float StationaryVelocityFloor = 0.05f;

        private const float MinSmoothingIntervalSpan = 0.001f;
        private const float StationaryVelocityThreshold = 0.03f;
        private const float SettledDistanceMeters = 0.005f;
        private const float SettledAngleDegrees = 0.05f;

        private const float PacketSilenceStopSeconds = 0.3f;
        private const float SilenceDecayPerSecond = 8f;

        private const float PredictionVelocityThreshold = 0.2f;
        private const float PredictionLeadFraction = 0.8f;
        private const float PredictionLeadIntervalCap = 1.2f;
        private const float InterpolationLookAheadFraction = 0.4f;
        private const float MovingVelocityThreshold = 1.0f;
        private const float PredictedAccelerationFraction = 0.5f;

        private const float TeleportSnapMeters = 5f;
        private const float VelocityFactorFullSpeed = 8f;
        private const float DistanceFactorFullMeters = 1.5f;
        private const float MinFollowSpeed = 15f;
        private const float MaxFollowSpeed = 30f;
        private const float CloseRangeMeters = 0.2f;
        private const float CloseRangeSpeedScale = 0.6f;

        private const float RotationFollowSpeed = 25f;
        private const float RotationFastSnapSpeed = 40f;
        private const float RotationSettleSpeed = 15f;
        private const float RotationFastSnapAngle = 30f;
        private const float RotationSettleAngle = 5f;

        private static readonly int PlayerPrefabHash = "Player".GetStableHashCode();

        private static bool IsPlayerZDO(ZDO zdo)
        {
            return zdo != null && zdo.m_prefab == PlayerPrefabHash;
        }

        private static readonly Dictionary<long, PlayerSyncData> _syncData = new Dictionary<long, PlayerSyncData>();

        private sealed class PlayerSyncData
        {
            public Vector3    networkPos;
            public Vector3    prevNetworkPos;
            public Quaternion networkRot;
            public Quaternion prevNetworkRot;
            public Vector3    velocity;
            public Vector3    smoothedVelocity;
            public float      lastNetworkUpdateTime;
            public float      networkUpdateInterval;
            public bool       hasData;
            public Vector3    renderPos;
            public Quaternion renderRot;
            public float      lastFrameTime;
            public Vector3    acceleration;
        }

        /// <summary>Per received packet: rebuilds the velocity estimate and the rolling packet interval. Clients only.</summary>
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
        [HarmonyPostfix]
        public static void ZDO_Deserialize_Postfix(ZDO __instance)
        {
            if (ConfigEnableClientInterpolation == null || ConfigEnablePlayerPrediction == null) return;
            if (!ConfigEnableClientInterpolation.Value && !EffectiveConfig.EnablePlayerPrediction()) return;
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            if (__instance == null || !IsPlayerZDO(__instance)) return;

            long owner = __instance.GetOwner();
            if (owner == ZNet.GetUID()) return;

            Vector3 newPos = __instance.GetPosition();
            Quaternion newRot = Quaternion.Normalize(__instance.GetRotation());
            float now = Time.time;

            PlayerSyncData sync;
            if (!_syncData.TryGetValue(owner, out sync))
            {
                _syncData[owner] = new PlayerSyncData
                {
                    networkPos     = newPos,
                    prevNetworkPos = newPos,
                    networkRot     = newRot,
                    prevNetworkRot = newRot,
                    renderPos      = newPos,
                    renderRot      = newRot,
                    velocity       = Vector3.zero,
                    smoothedVelocity = Vector3.zero,
                    acceleration   = Vector3.zero,
                    networkUpdateInterval = DefaultPacketIntervalSeconds,
                    lastNetworkUpdateTime = now,
                    lastFrameTime  = now,
                    hasData        = true
                };
                return;
            }

            float packetInterval = now - sync.lastNetworkUpdateTime;

            if (newPos == sync.networkPos)
            {
                sync.velocity = Vector3.Lerp(sync.velocity, Vector3.zero, StationaryDecayLerp);
                sync.acceleration = Vector3.Lerp(sync.acceleration, Vector3.zero, StationaryDecayLerp);
                if (sync.velocity.magnitude < StationaryVelocityFloor)
                {
                    sync.velocity = Vector3.zero;
                    sync.acceleration = Vector3.zero;
                }
            }
            else if (packetInterval >= MinPacketIntervalSeconds && packetInterval <= MaxPacketIntervalSeconds)
            {
                sync.networkUpdateInterval =
                    Mathf.Lerp(sync.networkUpdateInterval, packetInterval, PacketIntervalAveragingLerp);

                Vector3 rawVelocity = (newPos - sync.networkPos) / packetInterval;

                if (rawVelocity.magnitude <= TeleportVelocityMetersPerSecond)
                {
                    Vector3 rawAcceleration = (rawVelocity - sync.velocity) / packetInterval;
                    sync.acceleration = Vector3.Lerp(sync.acceleration, rawAcceleration, AccelerationSmoothingLerp);

                    Vector3 directionCorrected = Vector3.Lerp(sync.velocity, rawVelocity, VelocityDirectionLerp);
                    sync.velocity = Vector3.Lerp(sync.velocity, directionCorrected, VelocityMagnitudeLerp);
                    sync.smoothedVelocity = Vector3.Lerp(sync.smoothedVelocity, sync.velocity, RenderVelocityLerp);
                }
                else
                {
                    sync.velocity = Vector3.zero;
                    sync.smoothedVelocity = Vector3.zero;
                    sync.acceleration = Vector3.zero;
                }
            }

            sync.prevNetworkPos = sync.networkPos;
            sync.prevNetworkRot = sync.networkRot;
            sync.networkPos     = newPos;
            sync.networkRot     = newRot;
            sync.lastNetworkUpdateTime = now;
            sync.hasData = true;
        }

        /// <summary>
        /// Renders remote players between packets. Smoothing strength fades in with the measured packet
        /// interval, so a healthy connection keeps showing vanilla movement and a degrading one fades into
        /// full smoothing without a visible handoff. Clients only.
        /// </summary>
        [HarmonyPatch(typeof(Player), "LateUpdate")]
        [HarmonyPostfix]
        public static void Player_LateUpdate_Postfix(Player __instance)
        {
            if (__instance == null || __instance == Player.m_localPlayer) return;
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            if (ConfigEnableClientInterpolation == null || !ConfigEnableClientInterpolation.Value) return;

            ZNetView nview = __instance.m_nview;
            if (nview == null || !nview.IsValid()) return;

            ZDO zdo = nview.GetZDO();
            if (zdo == null) return;

            PlayerSyncData sync;
            if (!_syncData.TryGetValue(zdo.GetOwner(), out sync) || !sync.hasData) return;

            float minInterval = EffectiveConfig.SmoothingMinInterval();
            float maxInterval = Mathf.Max(minInterval + MinSmoothingIntervalSpan, EffectiveConfig.SmoothingMaxInterval());
            if (sync.networkUpdateInterval < minInterval) return;

            float smoothingStrength =
                Mathf.Clamp01((sync.networkUpdateInterval - minInterval) / (maxInterval - minInterval));

            bool isStationary = sync.smoothedVelocity.magnitude < StationaryVelocityThreshold;
            bool isSettled    = Vector3.Distance(sync.renderPos, sync.networkPos) < SettledDistanceMeters;
            bool rotSettled   = Quaternion.Angle(sync.renderRot, sync.networkRot) < SettledAngleDegrees;
            if (isStationary && isSettled && rotSettled) return;

            float now          = Time.time;
            float frameDelta   = now - sync.lastFrameTime;
            sync.lastFrameTime = now;

            float timeSinceUpdate = now - sync.lastNetworkUpdateTime;

            // Vanilla stops sending a stopped player, so silence means stopped; without this decay the
            // render position keeps extrapolating past where they actually halted.
            if (timeSinceUpdate > PacketSilenceStopSeconds && sync.smoothedVelocity.magnitude > StationaryVelocityFloor)
            {
                float decay = Mathf.Clamp01(frameDelta * SilenceDecayPerSecond);
                sync.velocity         = Vector3.Lerp(sync.velocity,         Vector3.zero, decay);
                sync.smoothedVelocity = Vector3.Lerp(sync.smoothedVelocity, Vector3.zero, decay);
                sync.acceleration     = Vector3.Lerp(sync.acceleration,     Vector3.zero, decay);
            }

            float leadTime;
            if (EffectiveConfig.EnablePlayerPrediction() && sync.smoothedVelocity.magnitude > PredictionVelocityThreshold)
                leadTime = Mathf.Min(timeSinceUpdate * PredictionLeadFraction,
                                     sync.networkUpdateInterval * PredictionLeadIntervalCap);
            else
                leadTime = sync.networkUpdateInterval * InterpolationLookAheadFraction;

            Vector3 target;
            if (sync.smoothedVelocity.magnitude > MovingVelocityThreshold)
            {
                Vector3 predictedVelocity =
                    sync.smoothedVelocity + sync.acceleration * leadTime * PredictedAccelerationFraction;
                target = sync.networkPos + predictedVelocity * leadTime;
            }
            else
            {
                target = sync.networkPos;
            }

            float distanceToTarget = Vector3.Distance(sync.renderPos, target);

            if (distanceToTarget > TeleportSnapMeters)
            {
                sync.renderPos = target;
                sync.renderRot = Quaternion.Normalize(sync.networkRot);
            }
            else
            {
                float velocityFactor = Mathf.Clamp01(sync.smoothedVelocity.magnitude / VelocityFactorFullSpeed);
                float distanceFactor = Mathf.Clamp01(distanceToTarget / DistanceFactorFullMeters);

                float followSpeed = Mathf.Lerp(MinFollowSpeed, MaxFollowSpeed, Mathf.Max(velocityFactor, distanceFactor));
                if (distanceToTarget < CloseRangeMeters) followSpeed *= CloseRangeSpeedScale;

                float blend = Mathf.Clamp01(frameDelta * followSpeed);
                blend = blend * blend * (3f - 2f * blend);
                sync.renderPos = Vector3.Lerp(sync.renderPos, target, blend);

                float rotationAngle = Quaternion.Angle(sync.renderRot, sync.networkRot);
                float rotationSpeed = RotationFollowSpeed;
                if (rotationAngle > RotationFastSnapAngle) rotationSpeed = RotationFastSnapSpeed;
                else if (rotationAngle < RotationSettleAngle) rotationSpeed = RotationSettleSpeed;

                sync.renderRot = Quaternion.Normalize(
                    Quaternion.Slerp(sync.renderRot, sync.networkRot, Mathf.Clamp01(frameDelta * rotationSpeed)));
            }

            __instance.transform.position = Vector3.Lerp(sync.networkPos, sync.renderPos, smoothingStrength);
            __instance.transform.rotation = Quaternion.Slerp(
                Quaternion.Normalize(sync.networkRot), sync.renderRot, smoothingStrength);
        }
    }
}
