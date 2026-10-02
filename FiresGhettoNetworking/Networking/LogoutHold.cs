using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using PlayFab.Party;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Finish sending before logout (R38: a character save sent at logout over crossplay never reached the server). Vanilla logs
    /// out in one frame: ContinueLogout → Game.Shutdown → SavePlayerProfile → ZNet.Shutdown → Disconnect → each socket disposed,
    /// which throws away whatever a crossplay socket still held. Steam delivers what it was given; PlayFab doesn't.
    ///
    /// The hold keeps the game running until every crossplay link has had its messages acknowledged by the server (at most 5 s),
    /// then lets vanilla's logout run. "Its messages" are the ones queued when the hold began: the game keeps sending while it
    /// waits, so an empty queue may never come (R43: the bot waited the full 5 s on 912 B of fresh position updates). It follows the logout-hold protocol the Fires mods share on Game.ContinueLogout, agreed with
    /// VAngarde's author for its character save (no API between mods):
    ///   1. hold at most once per logout, keyed on the Game instance;
    ///   2. when holding, start the wait on Game (frames keep running) and return false;
    ///   3. while the own wait is pending, return false for every call (other holders' re-invocations included);
    ///   4. when the wait ends, call ContinueLogout again with the same arguments;
    ///   5. once done, return true.
    /// So vanilla's ContinueLogout runs exactly once whatever the order or number of holders.
    ///
    /// Quitting from a world on a crossplay link logs out first (so these holds apply), then quits. For a quit that still gets
    /// through, anything still being compressed is finished and sent before the Disconnect goes out.
    /// </summary>
    [HarmonyPatch]
    public static class LogoutHold
    {
        public static ConfigEntry<bool> ConfigEnabled;

        private const float CapSeconds = 5f;
        private const double MarkAfterCompressingSeconds = 1.0;
        private const float MessageAfterSeconds = 0.5f;
        private const float MessageEverySeconds = 1f;
        private const double QuitFlushMaxSeconds = 1.0;
        private const float QuitAfterLogoutCapSeconds = 20f;

        private static Game s_holdingFor;
        private static Game s_doneFor;

        private static readonly MethodInfo s_continueLogout = AccessTools.Method(typeof(Game), "ContinueLogout");
        private static readonly MethodInfo s_socketLateUpdate = AccessTools.Method(typeof(ZPlayFabSocket), "LateUpdate");
        private static readonly AccessTools.FieldRef<ZPlayFabSocket, ZPlayFabSocket.InFlightQueue> s_inFlight =
            AccessTools.FieldRefAccess<ZPlayFabSocket, ZPlayFabSocket.InFlightQueue>("m_inFlightQueue");
        private static readonly AccessTools.FieldRef<ZPlayFabSocket, PlayFabZLibWorkQueue> s_zlib =
            AccessTools.FieldRefAccess<ZPlayFabSocket, PlayFabZLibWorkQueue>("m_zlibWorkQueue");

        // Fire, 2026-09-29 (R77: Ctrl+C in the dedi's window did nothing, twice; closing the window saved and quit): none of this is
        // for a dedicated server. There it registers nothing and every hook returns at once, so vanilla's Ctrl+C -> save -> exit runs
        // untouched. A dedicated server runs -nographics, so it is known from the first line, before ZNet exists.
        private static bool s_dedicated;

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("04 - Networking", "Finish Sending Before Logout", true,
                "Crossplay (PlayFab) only. Logout waits up to 5 s for the server to confirm what was queued (such as your character\n" +
                "save) instead of dropping it, and quitting from a world logs out first. Client only.");
            s_dedicated = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
            if (!s_dedicated)
            {
                Application.wantsToQuit += HoldQuit;
                return;
            }
            // Only watching, never holding: where a quit request goes on the server, for the Ctrl+C investigation. If Ctrl+C does
            // nothing and neither line appears, Unity never turned it into a quit at all.
            Application.wantsToQuit += () =>
            {
                LoggerOptions.LogMessage("[LogoutHold] dedicated server: Unity asked to quit (wantsToQuit); FGN lets it go.");
                return true;
            };
            Application.quitting += () => LoggerOptions.LogMessage("[LogoutHold] dedicated server: Unity is quitting.");
            LoggerOptions.LogInfo("[LogoutHold] dedicated server: the logout and quit holds are off here; vanilla's Ctrl+C / window close run untouched.");
        }

        private static bool s_quitHolding;
        private static bool s_quitDone;
        private static bool s_lastResortSaved;
        private const string VAngardeGuid = "com.Fire.FiresVAngarde";

        /// <summary>
        /// Quitting from a world on a crossplay link becomes a logout, and the game quits once vanilla's shutdown has run. R40: a quit
        /// in the same frame as a logout reached OnApplicationQuit in that frame although wantsToQuit returned false; the link went
        /// down and the character save was lost. The logout passes the ContinueLogout holds (this one and VAngarde's) like any other.
        /// Agreed with VAngarde's author: VAngarde 0.2.32 converts the same way for server-held characters. Both converting is
        /// harmless: a second Logout meets the running holds, and whichever quits first after the shutdown ends it.
        /// Window close and Alt+F4 arrive through wantsToQuit, vanilla's Quit button through Menu.QuitGame.
        /// </summary>
        private static bool HoldQuit() => !QuitAfterLogout("a quit");

        [HarmonyPatch(typeof(Menu), "QuitGame"), HarmonyPrefix]
        static bool HoldQuitButton() => s_dedicated || !QuitAfterLogout("the Quit button");

        /// <summary>True when the quit is taken over (a logout first, the quit after it); false lets it go ahead.</summary>
        private static bool QuitAfterLogout(string what)
        {
            if (s_quitDone) return false;
            Game game = Game.instance;
            if (s_quitHolding) return game != null && !game.IsShuttingDown();   // another mod's quit after the shutdown goes through
            if (ConfigEnabled == null || !ConfigEnabled.Value || game == null || game.IsShuttingDown() || ZNet.instance == null
                || ZNet.instance.IsServer() || CrossplaySockets().Count == 0)
                return false;
            s_quitHolding = true;
            bool logoutRunning = ReferenceEquals(s_holdingFor, game);
            LoggerOptions.LogInfo($"[LogoutHold] {what} on a crossplay link: {(logoutRunning ? "the logout already under way finishes" : "logging out")} first, then the game quits.");
            if (!logoutRunning)
            {
                try
                {
                    game.Logout(true, false);
                }
                catch (Exception ex)
                {
                    LoggerOptions.LogWarning($"[LogoutHold] logging out before the quit failed ({ex.GetType().Name}: {ex.Message}); quitting at once.");
                    s_quitHolding = false;
                    s_quitDone = true;
                    return false;
                }
            }
            QuitOnceShutDown(game);
            return true;
        }

        /// <summary>
        /// R40 also showed Unity sending OnApplicationQuit although the quit had been put off: frames ran on for seconds after it.
        /// Vanilla's OnApplicationQuit shuts the game down (the save, then ZNet) and sleeps 2 s. Under a held logout that takes the
        /// link away from what the hold waits for. So while this hold runs, vanilla's body is skipped. The held logout does the
        /// shutdown, and the quit comes after it. VAngarde 0.2.33 has the same guard for its own hold.
        /// </summary>
        [HarmonyPatch(typeof(Game), "OnApplicationQuit"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        static bool SkipEarlyQuitShutdown(Game __instance)
        {
            if (s_dedicated || __instance == null || !ReferenceEquals(s_holdingFor, __instance) || __instance.IsShuttingDown()) return true;
            LoggerOptions.LogInfo("[LogoutHold] OnApplicationQuit arrived during the held logout; vanilla's early shutdown skipped, the held logout shuts down, then the game quits.");
            // Last resort, once per quit: should the process end after all (an OS session end), the character is on disk. Vanilla's
            // body would have saved here. A local save only: VAngarde 0.2.33 uploads no profile save while its own hold's upload is
            // under way, and none at all when it isn't holding.
            // VAngarde makes this save itself and knows whether the server has the profile; two saves cost R44 a 1.2 s frame.
            if (!s_lastResortSaved && !BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(VAngardeGuid))
            {
                s_lastResortSaved = true;
                try
                {
                    __instance.SavePlayerProfile(setLogoutPoint: true);
                }
                catch (Exception ex)
                {
                    LoggerOptions.LogWarning($"[LogoutHold] the last-resort character save failed ({ex.GetType().Name}: {ex.Message})");
                }
            }
            if (!s_quitHolding && !s_quitDone)
            {
                s_quitHolding = true;
                QuitOnceShutDown(__instance);
            }
            return false;
        }

        /// <summary>The quit, on an object that outlives the world, so it comes even if the Game goes away.</summary>
        private static void QuitOnceShutDown(Game game)
        {
            if (s_quitHost == null)
            {
                var host = new GameObject("FGN_QuitHold");
                UnityEngine.Object.DontDestroyOnLoad(host);
                s_quitHost = host.AddComponent<QuitHost>();
            }
            s_quitHost.StartCoroutine(QuitWhenShutDown(game));
        }

        private sealed class QuitHost : MonoBehaviour { }

        private static QuitHost s_quitHost;

        /// <summary>
        /// Quits once vanilla's shutdown (the character save, then ZNet) has run. A logout with Logout(save, false) shuts down without
        /// quitting. The cap covers a logout that never finishes, such as a declined low-disk-space prompt.
        /// </summary>
        private static IEnumerator QuitWhenShutDown(Game game)
        {
            var clock = Stopwatch.StartNew();
            while (game != null && !game.IsShuttingDown() && clock.Elapsed.TotalSeconds < QuitAfterLogoutCapSeconds)
                yield return null;
            if (game != null && !game.IsShuttingDown())
                LoggerOptions.LogWarning($"[LogoutHold] the logout hadn't finished after {QuitAfterLogoutCapSeconds:F0} s; quitting anyway.");
            else
                LoggerOptions.LogInfo($"[LogoutHold] logged out {clock.Elapsed.TotalMilliseconds:F0} ms after the quit was asked for; quitting now.");
            s_quitHolding = false;
            s_quitDone = true;
            RunDeferredPartyQuit();
            Application.Quit();
        }

        private static PlayFabMultiplayerManager s_partyQuitDeferred;
        private static bool s_partyQuitRunning;
        private static bool s_partyQuitRan;
        private static readonly Stopwatch s_partyQuitClock = new Stopwatch();
        private static readonly MethodInfo s_partyOnQuit = AccessTools.Method(typeof(PlayFabMultiplayerManager), "OnApplicationQuit");

        private static bool LogoutOrQuitHeld()
        {
            Game game = Game.instance;
            return (s_holdingFor != null || s_quitHolding) && game != null && !game.IsShuttingDown();
        }

        /// <summary>
        /// R44: the OnApplicationQuit broadcast also reaches PlayFab Party's manager, which tears the Party endpoint down: the next send
        /// failed ("suspend TX"), the server logged 4098 invalid handle, and the held character save never arrived. While a logout or quit
        /// is held, Party's teardown waits. It runs once, after the logout has shut down (or the quit cap), just before the quit.
        /// </summary>
        [HarmonyPatch(typeof(PlayFabMultiplayerManager), "OnApplicationQuit"), HarmonyPrefix]
        static bool DeferPartyQuit(PlayFabMultiplayerManager __instance)
        {
            if (s_dedicated || s_partyQuitRunning) return true;
            if (s_partyQuitRan) return false;
            if (LogoutOrQuitHeld())
            {
                if (s_partyQuitDeferred == null)
                {
                    s_partyQuitClock.Restart();
                    LoggerOptions.LogInfo("[LogoutHold] PlayFab Party teardown deferred during the held logout; it runs after the shutdown, before the quit.");
                }
                s_partyQuitDeferred = __instance;
                return false;
            }
            if (s_partyQuitDeferred != null) LogPartyQuitRan("a later OnApplicationQuit");
            s_partyQuitRan = true;
            s_partyQuitDeferred = null;
            return true;
        }

        private static void RunDeferredPartyQuit()
        {
            PlayFabMultiplayerManager manager = s_partyQuitDeferred;
            if (manager == null || s_partyQuitRan) return;
            s_partyQuitRunning = true;
            try
            {
                s_partyOnQuit?.Invoke(manager, null);
                LogPartyQuitRan("the quit");
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[LogoutHold] PlayFab Party's deferred teardown failed ({ex.GetType().Name}: {ex.Message})");
            }
            finally
            {
                s_partyQuitRunning = false;
                s_partyQuitRan = true;
                s_partyQuitDeferred = null;
            }
        }

        private static void LogPartyQuitRan(string by) =>
            LoggerOptions.LogInfo($"[LogoutHold] PlayFab Party teardown ran {s_partyQuitClock.ElapsedMilliseconds} ms after it was deferred ({by}).");

        [HarmonyPatch(typeof(Game), "ContinueLogout"), HarmonyPrefix]
        static bool HoldLogout(Game __instance, bool save, bool shouldExit, bool changeToStartScene)
        {
            if (s_dedicated || ReferenceEquals(s_doneFor, __instance)) return true;   // 5: done for this logout (never held on a dedi)
            if (ReferenceEquals(s_holdingFor, __instance)) return false;   // 3: our wait is pending
            if (ConfigEnabled == null || !ConfigEnabled.Value || (!save && !shouldExit)
                || ZNet.instance == null || ZNet.instance.IsServer() || CrossplaySockets().Count == 0)
            {
                s_doneFor = __instance;
                return true;
            }
            s_holdingFor = __instance;                                       // 1-2: hold once, frames keep running
            __instance.StartCoroutine(HoldThenLogOut(__instance, save, shouldExit, changeToStartScene));
            return false;
        }

        private static IEnumerator HoldThenLogOut(Game game, bool save, bool shouldExit, bool changeToStartScene)
        {
            var clock = Stopwatch.StartNew();
            float nextMessage = MessageAfterSeconds;
            var marks = new Dictionary<ZPlayFabSocket, uint>();
            string pending = Pending(marks, clock);
            string lastSeen = pending;
            while (pending != null && clock.Elapsed.TotalSeconds < CapSeconds)
            {
                if (clock.Elapsed.TotalSeconds >= nextMessage)
                {
                    nextMessage += MessageEverySeconds;
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "Saving to the server…");
                }
                yield return null;
                lastSeen = pending;
                pending = Pending(marks, clock);
            }
            double ms = clock.Elapsed.TotalMilliseconds;
            // Pending counts only connected links, so a link that dropped reads as "nothing pending" (R40): tell it apart.
            if (pending != null) LoggerOptions.LogWarning($"[LogoutHold] logout went ahead after {ms / 1000.0:F1} s with {pending} unacknowledged.");
            else if (CrossplaySockets().Count == 0) LoggerOptions.LogWarning($"[LogoutHold] the crossplay link was lost after {ms:F0} ms, with {lastSeen ?? "nothing"} unacknowledged when last seen.");
            else LoggerOptions.LogInfo($"[LogoutHold] logout held {ms:F0} ms until the server had acknowledged everything sent before it.");
            s_holdingFor = null;
            s_doneFor = game;                                                // 4-5
            if (game != null) s_continueLogout.Invoke(game, new object[] { save, shouldExit, changeToStartScene });
        }

        /// <summary>
        /// What the crossplay links still owe from before the hold, or null when all of it is acknowledged (or no link is left).
        /// Each link's mark is the in-flight queue's head at the first frame its compression is idle (what was still being
        /// compressed when the hold began lands in the queue first); acknowledged means the tail has reached the mark.
        /// </summary>
        private static string Pending(Dictionary<ZPlayFabSocket, uint> marks, Stopwatch clock)
        {
            var parts = new List<string>();
            foreach (ZPlayFabSocket socket in CrossplaySockets())
            {
                ZPlayFabSocket.InFlightQueue inFlight = s_inFlight(socket);
                if (!marks.TryGetValue(socket, out uint mark))
                {
                    if (PlayFabZlibWorker.HasWork(s_zlib(socket)) && clock.Elapsed.TotalSeconds < MarkAfterCompressingSeconds)
                    {
                        parts.Add($"{(inFlight != null ? inFlight.Bytes : 0u)} B to {socket.GetEndPointString()} (still compressing)");
                        continue;
                    }
                    if (inFlight == null) continue;
                    mark = inFlight.Head;
                    marks[socket] = mark;
                }
                if (inFlight != null && (int)(inFlight.Tail - mark) < 0)
                    parts.Add($"{mark - inFlight.Tail} message(s) to {socket.GetEndPointString()} ({inFlight.Bytes} B in flight)");
            }
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }

        private static List<ZPlayFabSocket> CrossplaySockets()
        {
            var sockets = new List<ZPlayFabSocket>();
            if (ZNet.instance == null) return sockets;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                // BufferingSocket derives from ZPlayFabSocket: unwrap, then compare the exact type.
                ISocket socket = NetworkingRatesGroup.UnwrapSocket(peer?.m_socket);
                if (socket != null && socket.GetType() == typeof(ZPlayFabSocket) && socket.IsConnected()) sockets.Add((ZPlayFabSocket)socket);
            }
            return sockets;
        }

        /// <summary>
        /// Every shutdown, logout or quit: before the Disconnect goes out, let what is still queued for compression finish and go
        /// out first (vanilla compresses on another thread until ZNet stops, and disposing a socket throws that queue away).
        /// Waits at most 1 s; quitting can't be held any other way.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "StopAll"), HarmonyPrefix]
        static void FlushBeforeDisconnect(ZNet __instance)
        {
            try
            {
                if (s_dedicated || __instance == null || __instance.HaveStopped || ConfigEnabled == null || !ConfigEnabled.Value) return;
                List<ZPlayFabSocket> sockets = CrossplaySockets();
                if (sockets.Count == 0) return;
                var clock = Stopwatch.StartNew();
                while (sockets.Any(s => PlayFabZlibWorker.HasWork(s_zlib(s))) && clock.Elapsed.TotalSeconds < QuitFlushMaxSeconds)
                    Thread.Sleep(1);
                foreach (ZPlayFabSocket socket in sockets) s_socketLateUpdate?.Invoke(socket, null);
                if (clock.Elapsed.TotalMilliseconds >= 1.0)
                    LoggerOptions.LogInfo($"[LogoutHold] finished compressing before the disconnect ({clock.Elapsed.TotalMilliseconds:F0} ms).");
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[LogoutHold] flush before disconnect failed ({ex.GetType().Name}: {ex.Message})");
            }
        }
    }
}
