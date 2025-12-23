using BepInEx.Configuration;
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
        public static ConfigEntry<bool> ConfigEnablePlayerPrediction; // NEW: Toggle for prediction

        public static void Init(ConfigFile config)
        {
            ConfigEnablePlayerPositionBoost = config.Bind(
                "Player Sync",
                "Enable High-Frequency Position Updates",
                true,
                "Boosts server send rate for player positions to reduce floaty movement/desync between players.");

            ConfigPlayerPositionUpdateMultiplier = config.Bind(
                "Player Sync",
                "Position Update Multiplier",
                2.5f,
                new ConfigDescription("Multiplier for player position sync priority (1.0 = vanilla, 2.5 = recommended). Higher = smoother on mixed PCs.", new AcceptableValueRange<float>(1.0f, 5.0f)));

            ConfigEnableClientInterpolation = config.Bind(
                "Player Sync",
                "Enable Client-Side Interpolation",
                true,
                "Smooths received player positions on clients to eliminate jitter on high-ping/low-end PCs.");

            // NEW: Client-side prediction
            ConfigEnablePlayerPrediction = config.Bind(
                "Player Sync",
                "Enable Client-Side Prediction",
                true,
                "Predicts other players' movement between network updates for ultra-smooth feel (especially in combat on high ping).\nCLIENT-ONLY — no server impact.");
        }

        // ====================================================================
        // SERVER: Boost player ZDO send priority
        // ====================================================================
        [HarmonyPatch(typeof(ZDOMan), "ServerSortSendZDOS")]
        [HarmonyPrefix]
        public static void ServerSortSendZDOS_Prefix(List<ZDO> objects, Vector3 refPos)
        {
            if (!ConfigEnablePlayerPositionBoost.Value || !ZNet.instance.IsServer()) return;
            float multiplier = ConfigPlayerPositionUpdateMultiplier.Value;
            foreach (ZDO zdo in objects)
            {
                zdo.m_tempSortValue = Vector3.Distance(zdo.GetPosition(), refPos);
                if (IsPlayerZDO(zdo))
                {
                    zdo.m_tempSortValue -= 150f * multiplier;
                }
            }
        }

        private static bool IsPlayerZDO(ZDO zdo)
        {
            if (zdo == null) return false;
            return zdo.GetString("playerName".GetStableHashCode(), "").Length > 0;
        }

        // ====================================================================
        // CLIENT: Prediction + Interpolation data
        // ====================================================================
        private static Dictionary<long, PlayerPredictionData> predictionData = new Dictionary<long, PlayerPredictionData>();

        private class PlayerPredictionData
        {
            public Vector3 lastPos;
            public Quaternion lastRot;
            public Vector3 velocity;
            public float lastUpdateTime;
            public bool hasData = false;
        }

        // Capture ZDO updates to calculate accurate velocity
        [HarmonyPatch(typeof(ZNetView), "Deserialize")]
        [HarmonyPostfix]
        public static void Deserialize_Postfix(ZNetView __instance, ZPackage pkg)
        {
            if (!ConfigEnableClientInterpolation.Value && !ConfigEnablePlayerPrediction.Value) return;

            if (ZNet.instance == null || ZNet.instance.IsServer() || ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected) return;

            ZDO zdo = __instance?.GetZDO();
            if (zdo == null || !IsPlayerZDO(zdo)) return;

            long owner = zdo.GetOwner();
            if (owner == ZNet.GetUID()) return; // Skip local player

            Vector3 newPos = zdo.GetPosition();
            Quaternion newRot = zdo.GetRotation();

            if (!predictionData.TryGetValue(owner, out PlayerPredictionData data))
            {
                data = new PlayerPredictionData
                {
                    lastPos = newPos,
                    lastRot = newRot,
                    lastUpdateTime = Time.time,
                    hasData = true
                };
                predictionData[owner] = data;
                return;
            }

            float dt = Time.time - data.lastUpdateTime;
            if (dt > 0f && data.hasData)
            {
                data.velocity = (newPos - data.lastPos) / dt;
            }

            data.lastPos = newPos;
            data.lastRot = newRot;
            data.lastUpdateTime = Time.time;
            data.hasData = true;
        }

        // Apply prediction + interpolation in LateUpdate
        [HarmonyPatch(typeof(Player), "LateUpdate")]
        [HarmonyPostfix]
        public static void Player_LateUpdate_Postfix(Player __instance)
        {
            if (__instance == Player.m_localPlayer) return;
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            if (ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected) return;

            ZNetView nview = __instance.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            ZDO zdo = nview.GetZDO();
            if (zdo == null || !IsPlayerZDO(zdo)) return;

            long owner = zdo.GetOwner();
            if (!predictionData.TryGetValue(owner, out PlayerPredictionData data) || !data.hasData) return;

            // Prediction: Extrapolate forward using velocity
            if (ConfigEnablePlayerPrediction.Value)
            {
                float predictTime = Time.deltaTime * 1.5f; // Predict slightly ahead for better responsiveness
                Vector3 predictedPos = data.lastPos + data.velocity * predictTime;
                __instance.transform.position = Vector3.Lerp(__instance.transform.position, predictedPos, 0.8f); // Strong toward prediction
            }

            // Interpolation fallback (keeps smoothness)
            if (ConfigEnableClientInterpolation.Value)
            {
                float t = Time.deltaTime * 12f; // Slightly faster correction
                t = Mathf.Clamp01(t);
                Vector3 targetPos = data.lastPos + data.velocity * Time.deltaTime * 0.5f; // Light prediction boost
                __instance.transform.position = Vector3.Lerp(__instance.transform.position, targetPos, t);
                __instance.transform.rotation = Quaternion.Slerp(__instance.transform.rotation, data.lastRot, t);
            }
        }

        // ====================================================================
        // SAFE: Skip Tameable Awake/SetText on server to prevent NRE
        // ====================================================================
        [HarmonyPatch(typeof(Tameable), "Awake")]
        [HarmonyPrefix]
        public static bool Tameable_Awake_Prefix()
        {
            return ZNet.instance == null || !ZNet.instance.IsServer();
        }

        [HarmonyPatch(typeof(Tameable), "SetText")]
        [HarmonyPrefix]
        public static bool Tameable_SetText_Prefix()
        {
            return ZNet.instance == null || !ZNet.instance.IsServer();
        }
    }
}