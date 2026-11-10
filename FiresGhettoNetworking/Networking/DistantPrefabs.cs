using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.64 ([lead] audit M6 + M18). A mod registers prefabs through FgnApi.RegisterDistantPrefab. Vanilla only syncs a ZDO beyond the active area when it was created Distant, so
    /// a server (dedicated or host) sets every registered prefab's ZDOs Distant and force-sends them, every 10 s and at once after a registration change.
    /// 1.6.83 ([fgn], Fire 11-09 single-player split): the client half (in-range cache + FindSectorObjects injection) moved to EasyBakeMeshes; registrations are forwarded to EbmApi
    /// on a client or host. The server half is networking and stays. Nothing runs until something registers.
    /// </summary>
    internal static class DistantPrefabs
    {
        private const float ServerScanSeconds = 10f;
        private static readonly Type[] s_registerTypes = { typeof(string), typeof(float) }, s_nameTypes = { typeof(string) };

        private static readonly Dictionary<string, float> s_distance = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly List<ZDO> s_serverScan = new List<ZDO>();
        private static GameObject s_driver;

        internal static int Count => s_distance.Count;

        private static bool OnDedicated => ZNet.instance != null && ZNet.instance.IsDedicated();

        internal static bool Register(string prefab, float maxMeters)
        {
            if (string.IsNullOrEmpty(prefab)) { LoggerOptions.LogWarning("[DistantPrefabs] RegisterDistantPrefab refused: empty prefab name"); return false; }
            if (maxMeters <= 0f) return Unregister(prefab);
            bool isNew = !s_distance.ContainsKey(prefab);
            s_distance[prefab] = maxMeters;
            EnsureDriver();
            if (!OnDedicated) EbmBridge.Call("RegisterDistantPrefab", s_registerTypes, new object[] { prefab, maxMeters });
            if (isNew) LoggerOptions.LogDebug($"[DistantPrefabs] '{prefab}' registered to {maxMeters:0} m ({s_distance.Count} prefab(s))");
            return true;
        }

        internal static bool Unregister(string prefab)
        {
            if (string.IsNullOrEmpty(prefab) || !s_distance.Remove(prefab)) return false;
            if (!OnDedicated) EbmBridge.Call("UnregisterDistantPrefab", s_nameTypes, new object[] { prefab });
            return true;
        }

        /// <summary>After a batch of registrations: mark + send on the server now, and let EBM rebuild its client cache.</summary>
        internal static void Refresh()
        {
            TryServerMarkAndSend();
            if (!OnDedicated) EbmBridge.Call("RefreshDistantPrefabs", Type.EmptyTypes, new object[0]);
        }

        /// <summary>From EbmBridge once all plugins are loaded: registrations made before EBM was found are handed over.</summary>
        internal static void ForwardAll()
        {
            if (OnDedicated) return;
            foreach (var kv in s_distance) EbmBridge.Call("RegisterDistantPrefab", s_registerTypes, new object[] { kv.Key, kv.Value });
        }

        private static void EnsureDriver()
        {
            if (s_driver != null) return;
            s_driver = new GameObject("FGN DistantPrefabs");
            UnityEngine.Object.DontDestroyOnLoad(s_driver);
            s_driver.hideFlags = HideFlags.HideAndDontSave;
            s_driver.AddComponent<Driver>();
            LoggerOptions.LogInfo("[DistantPrefabs] first registration: distant prefab streaming on");
        }

        private static void TryServerMarkAndSend()
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null || s_distance.Count == 0) return;
                foreach (var kv in s_distance)
                {
                    s_serverScan.Clear();
                    int idx = 0;
                    while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(kv.Key, s_serverScan, ref idx)) { }
                    foreach (var zdo in s_serverScan)
                    {
                        if (zdo == null) continue;
                        if (!zdo.Distant) zdo.SetDistant(true);
                        ZDOMan.instance.ForceSendZDO(zdo.m_uid);
                    }
                }
                s_serverScan.Clear();
            }
            catch (Exception e) { LoggerOptions.LogDebug($"[DistantPrefabs] server scan threw: {e.Message}"); }
        }

        private sealed class Driver : MonoBehaviour
        {
            private float _timer;
            private bool _first = true;

            private void Update()
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null || s_distance.Count == 0) return;
                if (_first) { _first = false; _timer = 0f; TryServerMarkAndSend(); return; }
                _timer += Time.deltaTime;
                if (_timer < ServerScanSeconds) return;
                _timer = 0f;
                TryServerMarkAndSend();
            }
        }
    }
}
