using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace FiresGhettoNetworkMod.AutoTune
{
    /// <summary>
    /// Per-server probe-result cache on disk (BepInEx/config/com.Fire.FiresGhettoNetworkMod_autotune.bin),
    /// keyed by server endpoint with a TTL. Hand-rolled binary, so the probe never depends on a JSON library
    /// the modset may not carry; any disk failure simply means a re-probe.
    /// </summary>
    public static class AutoTuneCache
    {
        // 'F','G','N','A' little-endian as int32 — checked on load to reject random
        // files / corrupted writes / pre-binary .bin files written by some prior build.
        private const int FileMagic = 0x414E4746;
        private const string FileName = "com.Fire.FiresGhettoNetworkMod_autotune.bin";
        public  const int    SchemaVersion = 2;  // 1 = old JSON, 2 = this binary format

        private static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

        private static Dictionary<string, CacheEntry> _entries;
        private static bool _loaded;

        public sealed class CacheEntry
        {
            public Tier Tier { get; set; }
            public int PingMedianMs { get; set; }
            public DateTime TimestampUtc { get; set; }
            public string HardwareHash { get; set; } = string.Empty;
        }

        private static string CachePath
        {
            get { return Path.Combine(Paths.ConfigPath, FileName); }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _entries = new Dictionary<string, CacheEntry>();

            if (!File.Exists(CachePath)) return;

            try
            {
                using (var fs = File.OpenRead(CachePath))
                using (var br = new BinaryReader(fs))
                {
                    int magic = br.ReadInt32();
                    if (magic != FileMagic)
                    {
                        LoggerOptions.LogInfo($"[AutoTune] Cache magic mismatch (0x{magic:X8} != 0x{FileMagic:X8}) — starting fresh.");
                        return;
                    }
                    int version = br.ReadInt32();
                    if (version != SchemaVersion)
                    {
                        LoggerOptions.LogInfo($"[AutoTune] Cache schema {version} (expected {SchemaVersion}) — starting fresh.");
                        return;
                    }
                    int count = br.ReadInt32();
                    if (count < 0 || count > 100_000) // sanity ceiling vs. truncated file
                    {
                        LoggerOptions.LogWarning($"[AutoTune] Cache entry count {count} out of range — starting fresh.");
                        return;
                    }
                    for (int i = 0; i < count; i++)
                    {
                        string serverKey   = br.ReadString();
                        int    tierInt     = br.ReadInt32();
                        int    pingMedian  = br.ReadInt32();
                        long   ticks       = br.ReadInt64();
                        string hwHash      = br.ReadString();

                        _entries[serverKey] = new CacheEntry
                        {
                            Tier         = (Tier)tierInt,
                            PingMedianMs = pingMedian,
                            TimestampUtc = new DateTime(ticks, DateTimeKind.Utc),
                            HardwareHash = hwHash ?? string.Empty,
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[AutoTune] Failed to load cache ({ex.GetType().Name}: {ex.Message}); starting fresh.");
                _entries = new Dictionary<string, CacheEntry>();
            }
        }

        private static void SaveToDisk()
        {
            try
            {
                using (var fs = File.Create(CachePath))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(FileMagic);
                    bw.Write(SchemaVersion);
                    bw.Write(_entries.Count);
                    foreach (var kvp in _entries)
                    {
                        bw.Write(kvp.Key ?? string.Empty);
                        bw.Write((int)kvp.Value.Tier);
                        bw.Write(kvp.Value.PingMedianMs);
                        bw.Write(kvp.Value.TimestampUtc.Ticks);
                        bw.Write(kvp.Value.HardwareHash ?? string.Empty);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[AutoTune] Failed to write cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Look up the cached entry for a server key. Returns null if missing OR expired
        /// OR if the hardware fingerprint changed (different machine / GPU / RAM since
        /// last probe — the previous tier was for a different system).
        /// </summary>
        public static CacheEntry TryGet(string serverKey, string currentHwHash, TimeSpan? ttl = null)
        {
            if (string.IsNullOrEmpty(serverKey)) return null;
            EnsureLoaded();

            CacheEntry entry;
            if (!_entries.TryGetValue(serverKey, out entry) || entry == null) return null;

            TimeSpan effectiveTtl = ttl ?? DefaultTtl;
            if (DateTime.UtcNow - entry.TimestampUtc > effectiveTtl)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(currentHwHash)
                && !string.IsNullOrEmpty(entry.HardwareHash)
                && entry.HardwareHash != currentHwHash)
            {
                return null;
            }

            return entry;
        }

        public static void Save(string serverKey, Tier tier, int pingMedianMs, string hwHash)
        {
            if (string.IsNullOrEmpty(serverKey)) return;
            EnsureLoaded();

            _entries[serverKey] = new CacheEntry
            {
                Tier         = tier,
                PingMedianMs = pingMedianMs,
                TimestampUtc = DateTime.UtcNow,
                HardwareHash = hwHash ?? string.Empty,
            };

            SaveToDisk();
        }

        public static void Forget(string serverKey)
        {
            if (string.IsNullOrEmpty(serverKey)) return;
            EnsureLoaded();
            if (_entries.Remove(serverKey)) SaveToDisk();
        }

        public static void ForgetAll()
        {
            EnsureLoaded();
            _entries.Clear();
            SaveToDisk();
        }
    }
}
