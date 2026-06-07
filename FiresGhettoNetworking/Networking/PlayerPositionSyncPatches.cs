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

        // Pre-calculated once at startup — avoids recomputing the hash on every call.
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

        // Fires once per received network packet. Builds a smoothed velocity estimate and
        // tracks the rolling packet interval. Decays velocity immediately when position is
        // unchanged (stationary resync) to prevent drift.
        // CLIENT-ONLY: Only runs on clients to smooth remote player movement.
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
        [HarmonyPostfix]
        public static void ZDO_Deserialize_Postfix(ZDO __instance)
        {
            // Early exit if configs are null (mod not fully initialized)
            if (ConfigEnableClientInterpolation == null || ConfigEnablePlayerPrediction == null) return;
            // Interpolation stays a direct read — it's a fundamental on/off, not a tiering decision.
            // Prediction goes through EffectiveConfig so Auto-Tune can flip it based on measured ping.
            if (!ConfigEnableClientInterpolation.Value && !EffectiveConfig.EnablePlayerPrediction()) return;

            // Only run on clients (never on dedicated servers)
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;

            // Safety: ensure ZDO is valid and is a player
            if (__instance == null || !IsPlayerZDO(__instance)) return;

            long owner = __instance.GetOwner();
            // Don't smooth local player (they control themselves)
            if (owner == ZNet.GetUID()) return;

            Vector3 newPos = __instance.GetPosition();
            Quaternion newRot = __instance.GetRotation();

            // CRITICAL: Normalize incoming rotation to prevent drift
            newRot = Quaternion.Normalize(newRot);

            float now = Time.time;

            PlayerSyncData data;
            if (!_syncData.TryGetValue(owner, out data))
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
                    networkUpdateInterval  = 0.05f,
                    lastNetworkUpdateTime  = now,
                    lastFrameTime  = now,
                    hasData        = true
                };
                return;
            }

            float dt = now - data.lastNetworkUpdateTime;

            bool positionUnchanged = newPos == data.networkPos;

            if (positionUnchanged)
            {
                // Smoothly decay velocity and acceleration when stationary
                data.velocity = Vector3.Lerp(data.velocity, Vector3.zero, 0.9f);
                data.acceleration = Vector3.Lerp(data.acceleration, Vector3.zero, 0.9f);
                if (data.velocity.magnitude < 0.05f)
                {
                    data.velocity = Vector3.zero;
                    data.acceleration = Vector3.zero;
                }
            }
            else if (dt >= 0.005f && dt <= 0.5f)
            {
                // Update network interval with smoother averaging
                data.networkUpdateInterval = Mathf.Lerp(data.networkUpdateInterval, dt, 0.15f);

                // Calculate raw velocity
                Vector3 rawVelocity = (newPos - data.networkPos) / dt;

                // Only accept reasonable velocities (< 30 m/s, not teleports)
                if (rawVelocity.magnitude <= 30f)
                {
                    // Calculate acceleration (change in velocity)
                    Vector3 rawAcceleration = (rawVelocity - data.velocity) / dt;

                    // Smooth acceleration more aggressively for natural feel
                    data.acceleration = Vector3.Lerp(data.acceleration, rawAcceleration, 0.25f);

                    // Apply multi-stage velocity smoothing for better quality
                    // Stage 1: Fast response to direction changes
                    Vector3 intermediate = Vector3.Lerp(data.velocity, rawVelocity, 0.5f);

                    // Stage 2: Smooth out magnitude changes
                    data.velocity = Vector3.Lerp(data.velocity, intermediate, 0.7f);

                    // Stage 3: Create smoothed velocity for rendering
                    data.smoothedVelocity = Vector3.Lerp(data.smoothedVelocity, data.velocity, 0.6f);
                }
                else
                {
                    // Teleport detected - reset smoothing
                    data.velocity = Vector3.zero;
                    data.smoothedVelocity = Vector3.zero;
                    data.acceleration = Vector3.zero;
                }
            }

            data.prevNetworkPos = data.networkPos;
            data.prevNetworkRot = data.networkRot;
            data.networkPos     = newPos;
            data.networkRot     = newRot;
            data.lastNetworkUpdateTime = now;
            data.hasData = true;
        }

        // Runs every frame. Skips entirely when the player is stationary and settled.
        // Uses hermite interpolation with acceleration for ultra-smooth movement.
        // Snaps immediately on teleport. Rotation uses quaternion smoothing.
        // CLIENT-ONLY: Only runs on clients to render smoothed remote player positions.
        [HarmonyPatch(typeof(Player), "LateUpdate")]
        [HarmonyPostfix]
        public static void Player_LateUpdate_Postfix(Player __instance)
        {
            // Early exit if this is the local player (don't smooth yourself)
            if (__instance == null || __instance == Player.m_localPlayer) return;

            // Only run on clients (never on dedicated servers)
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;

            // Safety: check config is initialized and enabled
            if (ConfigEnableClientInterpolation == null || !ConfigEnableClientInterpolation.Value) return;

            // Player.m_nview is set in Character.Awake — no GetComponent needed.
            ZNetView nview = __instance.m_nview;
            if (nview == null || !nview.IsValid()) return;

            // Safety: ensure ZDO exists before accessing it
            ZDO zdo = nview.GetZDO();
            if (zdo == null) return;

            // We're patching Player.LateUpdate so the ZDO is always a player ZDO — no need to re-check.
            long owner = zdo.GetOwner();
            PlayerSyncData data;
            if (!_syncData.TryGetValue(owner, out data) || !data.hasData) return;

            // Adaptive smoothing strength based on inter-packet arrival rate.
            // Below Min: vanilla renders directly (no smoothing). Above Max: full
            // smoothing. In between: linear fade so the handoff is imperceptible.
            // The +epsilon guard keeps the divide sane if an admin sets Max <= Min.
            float interval = data.networkUpdateInterval;
            float minInterval = EffectiveConfig.SmoothingMinInterval();
            float maxInterval = Mathf.Max(minInterval + 0.001f,
                EffectiveConfig.SmoothingMaxInterval());
            if (interval < minInterval) return;
            float smoothingStrength =
                Mathf.Clamp01((interval - minInterval) / (maxInterval - minInterval));

            bool isStationary = data.smoothedVelocity.magnitude < 0.03f;
            bool isSettled    = Vector3.Distance(data.renderPos, data.networkPos) < 0.005f;
            bool rotSettled   = Quaternion.Angle(data.renderRot, data.networkRot) < 0.05f;
            if (isStationary && isSettled && rotSettled) return;

            float now             = Time.time;
            float frameDelta      = now - data.lastFrameTime;
            data.lastFrameTime    = now;

            float timeSinceUpdate = now - data.lastNetworkUpdateTime;

            // Packet-silence stop detection. Vanilla ZDOMan.SendZDOs skips ZDOs
            // whose state hasn't changed, so when a remote player stops running
            // the server stops sending their position entirely — no "stationary"
            // packet ever arrives. The decay branch in ZDO_Deserialize_Postfix
            // only fires on packets that DO arrive, so without this fallback
            // smoothedVelocity stayed frozen at whatever speed they were moving
            // when packets stopped, and the prediction branch below kept
            // extrapolating them past their actual stopping point (the
            // "ghost-running" symptom). After ~6 missed vanilla updates (0.3s
            // at 20Hz) we treat the silence as a stop and decay velocity each
            // frame; once smoothedVelocity drops under 0.03 the isStationary
            // early-exit at the top of this method catches the next frame.
            // frameDelta * 8 = ~125ms half-life at 60fps, frame-rate independent.
            if (timeSinceUpdate > 0.3f && data.smoothedVelocity.magnitude > 0.05f)
            {
                float decay = Mathf.Clamp01(frameDelta * 8f);
                data.velocity         = Vector3.Lerp(data.velocity,         Vector3.zero, decay);
                data.smoothedVelocity = Vector3.Lerp(data.smoothedVelocity, Vector3.zero, decay);
                data.acceleration     = Vector3.Lerp(data.acceleration,     Vector3.zero, decay);
            }

            // Calculate prediction/lead time based on velocity confidence
            float leadTime = 0f;
            if (EffectiveConfig.EnablePlayerPrediction() && data.smoothedVelocity.magnitude > 0.2f)
            {
                // Use prediction when moving, scaled by time since last update
                leadTime = Mathf.Min(timeSinceUpdate * 0.8f, data.networkUpdateInterval * 1.2f);
            }
            else
            {
                // Use slight look-ahead for smoother interpolation
                leadTime = data.networkUpdateInterval * 0.4f;
            }

            // Calculate target position with optional prediction
            Vector3 target;
            if (data.smoothedVelocity.magnitude > 1.0f)
            {
                // Moving: use smoothed velocity + acceleration for natural curves
                Vector3 predictedVel = data.smoothedVelocity + data.acceleration * leadTime * 0.5f;
                target = data.networkPos + predictedVel * leadTime;
            }
            else
            {
                // Slow or stationary: interpolate directly to network position
                target = data.networkPos;
            }

            float dist = Vector3.Distance(data.renderPos, target);

            // Teleport detection - snap immediately if too far
            if (dist > 5f)
            {
                data.renderPos = target;
                data.renderRot = Quaternion.Normalize(data.networkRot);  // Normalize on snap
            }
            else
            {
                // Adaptive interpolation speed based on distance and velocity
                // Faster when far away or moving quickly, slower when close for stability
                float velocityFactor = Mathf.Clamp01(data.smoothedVelocity.magnitude / 8f);
                float distanceFactor = Mathf.Clamp01(dist / 1.5f);

                // Base speed increases with distance and velocity
                float baseSpeed = Mathf.Lerp(15f, 30f, Mathf.Max(velocityFactor, distanceFactor));

                // Apply extra smoothing when very close to reduce jitter
                if (dist < 0.2f)
                {
                    baseSpeed *= 0.6f;
                }

                // Hermite interpolation for smoother movement than linear lerp
                float t = Mathf.Clamp01(frameDelta * baseSpeed);
                t = t * t * (3f - 2f * t); // Smoothstep function

                data.renderPos = Vector3.Lerp(data.renderPos, target, t);

                // Rotation: faster interpolation for more responsive look direction
                // Use angular velocity-based speed for natural head tracking
                float rotSpeed = 25f;
                float rotAngle = Quaternion.Angle(data.renderRot, data.networkRot);
                if (rotAngle > 30f)
                {
                    rotSpeed = 40f; // Fast snap for big direction changes
                }
                else if (rotAngle < 5f)
                {
                    rotSpeed = 15f; // Slow and smooth when nearly aligned
                }

                // Slerp and normalize to ensure unit length (prevents Unity errors)
                data.renderRot = Quaternion.Slerp(data.renderRot, data.networkRot, 
                    Mathf.Clamp01(frameDelta * rotSpeed));

                // CRITICAL: Normalize to ensure unit length and prevent drift
                data.renderRot = Quaternion.Normalize(data.renderRot);
            }

            // Blend between raw network value and the smoothed render value by
            // strength. State (renderPos/renderRot) keeps evolving regardless so the
            // transition from "fast arrival, vanilla shows through" to "slow arrival,
            // full smoothing" is seamless when the connection degrades mid-session.
            __instance.transform.position = Vector3.Lerp(
                data.networkPos, data.renderPos, smoothingStrength);
            __instance.transform.rotation = Quaternion.Slerp(
                Quaternion.Normalize(data.networkRot), data.renderRot, smoothingStrength);
        }
    }
}