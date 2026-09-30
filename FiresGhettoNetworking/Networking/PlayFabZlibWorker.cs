using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Vanilla's PlayFab compression thread holds one lock, shared by every crossplay socket, while it compresses and decompresses
    /// everything queued; the main thread takes the same lock for every crossplay send and for each socket's per-frame poll, so a
    /// send waits out the whole batch. R34: the dedi's 872 ms frame was 841 ms blocked, 525 ms of it in one send, with 1 MB
    /// messages and a 270 KB log upload queued, and PvP hits relayed through the dedi arrived 0.4-0.9 s late. This loop takes the
    /// lock only to move one buffer in or out and runs zlib outside it: the same buffers, in the same order, on one thread.
    ///
    /// No Harmony patch: vanilla's WorkerMain never returns (an endless loop around its locks), and Mono refused to compile both a
    /// skipping prefix (1.4.81) and a body-replacing transpiler (1.4.82) onto it. Vanilla starts its thread only from the first
    /// queue's constructor, and only while PlayFabZLibWorkQueue.s_thread is null (under its lock), so FGN starts this loop's thread
    /// at load, before any crossplay socket exists, and records it there; vanilla then never starts its own.
    /// </summary>
    public static class PlayFabZlibWorker
    {
        public static ConfigEntry<bool> ConfigEnabled;

        private const string ThreadName = "PlayfabZlibThread (FGN)";

        private static readonly AccessTools.FieldRef<object> s_lock =
            AccessTools.StaticFieldRefAccess<object>(AccessTools.Field(typeof(PlayFabZLibWorkQueue), "m_lock"));
        private static readonly AccessTools.FieldRef<Thread> s_thread =
            AccessTools.StaticFieldRefAccess<Thread>(AccessTools.Field(typeof(PlayFabZLibWorkQueue), "s_thread"));
        private static readonly AccessTools.FieldRef<List<PlayFabZLibWorkQueue>> s_workers =
            AccessTools.StaticFieldRefAccess<List<PlayFabZLibWorkQueue>>(AccessTools.Field(typeof(PlayFabZLibWorkQueue), "s_workers"));
        private static readonly AccessTools.FieldRef<ManualResetEventSlim> s_workEvent =
            AccessTools.StaticFieldRefAccess<ManualResetEventSlim>(AccessTools.Field(typeof(PlayFabZLibWorkQueue), "s_workEvent"));
        private static readonly AccessTools.FieldRef<PlayFabZLibWorkQueue, Queue<byte[]>> s_inCompress =
            AccessTools.FieldRefAccess<PlayFabZLibWorkQueue, Queue<byte[]>>("m_inCompress");
        private static readonly AccessTools.FieldRef<PlayFabZLibWorkQueue, Queue<byte[]>> s_outCompress =
            AccessTools.FieldRefAccess<PlayFabZLibWorkQueue, Queue<byte[]>>("m_outCompress");
        private static readonly AccessTools.FieldRef<PlayFabZLibWorkQueue, Queue<byte[]>> s_inDecompress =
            AccessTools.FieldRefAccess<PlayFabZLibWorkQueue, Queue<byte[]>>("m_inDecompress");
        private static readonly AccessTools.FieldRef<PlayFabZLibWorkQueue, Queue<byte[]>> s_outDecompress =
            AccessTools.FieldRefAccess<PlayFabZLibWorkQueue, Queue<byte[]>>("m_outDecompress");

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("12 - Advanced", "Fix PlayFab Compression Stalls", true,
                "Crossplay (PlayFab) only. Vanilla compresses and decompresses every crossplay message on one thread that holds a lock\n" +
                "shared with the main thread for the whole batch, so a send, including every PvP hit a server relays, can wait\n" +
                "hundreds of milliseconds behind a large message. With this on, the lock is held only to hand one message in or out.\n" +
                "Same messages, same order. Requires restart.");
        }

        /// <summary>At load, when the fix is on: FGN's loop takes vanilla's place. One result line either way; any failure leaves
        /// vanilla's loop to start as usual.</summary>
        public static void Apply()
        {
            if (ConfigEnabled == null || !ConfigEnabled.Value) return;
            try
            {
                string started = StartWorkerThread();
                if (started == null)
                    LoggerOptions.LogInfo("[PlayFabZlib] crossplay (de)compression runs outside the shared lock; the lock only hands messages in and "
                        + "out (FGN's compression thread runs in place of vanilla's).");
                else
                    LoggerOptions.LogWarning($"[PlayFabZlib] {started}; vanilla's compression loop is used this session.");
            }
            catch (Exception ex)
            {
                LoggerOptions.LogError($"[PlayFabZlib] couldn't start FGN's compression thread ({ex.GetType().Name}: {ex.Message}); vanilla's is used, "
                    + "the rest of FGN loads normally.");
            }
        }

        /// <summary>Starts the loop's thread and records it as vanilla's, under vanilla's lock so no queue can be created in between.
        /// Null when it runs; otherwise why it didn't start. Public so it can be checked outside the game.</summary>
        public static string StartWorkerThread()
        {
            lock (s_lock())
            {
                Thread existing = s_thread();
                if (existing != null) return $"vanilla's compression thread is already running ({existing.Name})";
                var thread = new Thread(RunLoop) { Name = ThreadName, IsBackground = true };
                s_thread() = thread;
                try
                {
                    thread.Start();
                }
                catch
                {
                    // Not running: let the first queue start vanilla's thread as usual, so crossplay compression never stalls.
                    s_thread() = null;
                    throw;
                }
            }
            return null;
        }

        private static void RunLoop()
        {
            object gate = s_lock();
            ManualResetEventSlim work = s_workEvent();
            var workers = new List<PlayFabZLibWorkQueue>();
            while (true)
            {
                try
                {
                    work.Wait();
                    work.Reset();
                    workers.Clear();
                    lock (gate) workers.AddRange(s_workers());
                    foreach (PlayFabZLibWorkQueue queue in workers)
                    {
                        Drain(gate, s_inDecompress(queue), s_outDecompress(queue), queue.UncompressOnThisThread);
                        Drain(gate, s_inCompress(queue), s_outCompress(queue), queue.CompressOnThisThread);
                    }
                }
                catch (Exception ex)
                {
                    // Unity's own logger is thread-safe; BepInEx's console writer is not.
                    UnityEngine.Debug.LogWarning($"[FiresGhetto] [PlayFabZlib] {ex.GetType().Name}: {ex.Message}; carrying on.");
                }
            }
        }

        /// <summary>One message at a time: taken under the lock, (de)compressed outside it, handed back under the lock.</summary>
        private static void Drain(object gate, Queue<byte[]> input, Queue<byte[]> output, Func<byte[], byte[]> transform)
        {
            while (true)
            {
                byte[] payload;
                lock (gate)
                {
                    if (input.Count == 0) return;
                    payload = input.Dequeue();
                    s_inHand++;
                }
                byte[] result;
                try
                {
                    result = transform(payload);
                }
                catch
                {
                    lock (gate) s_inHand--;
                    continue;
                }
                lock (gate)
                {
                    output.Enqueue(result);
                    s_inHand--;
                }
            }
        }

        // Messages taken off a queue and not yet handed back (FGN's loop only; vanilla's holds the lock for its whole batch).
        private static int s_inHand;

        /// <summary>
        /// Whether a crossplay socket still has messages waiting to be compressed, or being compressed (LogoutHold). Under the
        /// shared lock, so with vanilla's loop running this waits out its batch and then sees it done.
        /// </summary>
        public static bool HasWork(PlayFabZLibWorkQueue queue)
        {
            if (queue == null) return false;
            try
            {
                lock (s_lock()) return s_inCompress(queue).Count > 0 || s_inHand > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
