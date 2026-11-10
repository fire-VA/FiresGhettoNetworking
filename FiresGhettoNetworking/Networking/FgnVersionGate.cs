using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Client and server tell each other which FGN they run, and FGN's per-peer wire paths (ZDO deltas, compression, the
    /// ownership hand-offs) run only with a peer whose FGN matches ours. Until then, and for good with a vanilla or a
    /// different FGN, that peer gets plain vanilla traffic: a client that cannot read FGN's deltas would otherwise build a
    /// partial ZDO from the next delta.
    ///
    /// The hello is a routed RPC carrying (plugin version, wire protocol) and follows CompressionGroup's handshake: one
    /// Register&lt;ZPackage&gt; at ZNet.Start, the client greeting the server via 0L, the handler resolving the peer by sender
    /// and replying once. FGN before 1.5.31 has no hello but does answer ConnectionEcho, which is how an older FGN is told
    /// apart from a vanilla client.
    /// </summary>
    public static class FgnVersionGate
    {
        // Bump whenever anything FGN puts on the wire changes meaning (delta format, compression framing, RPC payloads), so two
        // builds that would misread each other never match, even when the plugin version string was not changed.
        internal const int WireProtocol = 2;

        public static ConfigEntry<bool> ConfigRefuseMismatched;

        private const string HelloRpc = "FGN_VersionHello";
        private const string RefusedRpc = "FGN_VersionRefused";
        private const double SweepSeconds = 0.5;

        // Server: an echo answered but no hello this long after it is an FGN from before the hello existed. No echo at all
        // this long after the peer is ready is a vanilla client (echoes go out every 2 s).
        // The client only greets once fully connected and waits 30 s itself, so a shorter wait here would misjudge a client
        // still loading on a rejoin. Same 30 s as the client; a hello that arrives later still flips the peer to matched.
        private const double OlderFgnSeconds = 30.0;
        private const double NoFgnSeconds = 15.0;

        // Client: the server's routed RPCs to a joining client can be held back by ServerSync's config sync (see
        // CompressionGroup), so the client waits longer than the server before calling the server's FGN an older one.
        private const double ServerHelloSeconds = 30.0;
        private const double GreetIntervalSeconds = 2.0;
        private const double GreetWindowSeconds = 60.0;

        // Long enough for the refusal text to reach the client before its connection goes.
        private const double RefuseDelaySeconds = 2.0;

        internal enum State { Unknown, Matched, Mismatched, NoFgn }

        private sealed class PeerVersion
        {
            public State State;
            public string Version;
            public int Protocol;
            public double ReadySince;
            public double EchoSince;
            public bool HelloSent;
            public bool Reported;
            public double RefuseAt;
        }

        private static readonly Dictionary<long, PeerVersion> s_peers = new Dictionary<long, PeerVersion>();
        private static readonly List<ZNetPeer> s_refuseNow = new List<ZNetPeer>();
        private static double s_nextSweep;
        private static bool s_isServer;

        // Client side, about the one server.
        private static double s_serverEchoSince;
        private static double s_nextGreet;
        private static double s_greetUntil;
        private static string s_pendingHud;
        private static bool s_pendingHudNow;

        // 1.5.82: what the server's hello says about it (the homestead upgrade gate). False until the server's hello arrives.
        private static bool s_serverSimulation, s_serverHomesteads, s_serverFlagsKnown;

        /// <summary>Client: the server said Server-Side Simulation runs on it. On the server itself, its own state.</summary>
        internal static bool ServerSimulationActive => s_isServer ? ServerAuthorityPatches.SimulationActive : s_serverSimulation;

        /// <summary>Client: the server said it keeps homestead areas. On the server itself, its own state.</summary>
        internal static bool ServerHomesteadsAvailable => s_isServer ? CompanionKeepAlive.HomesteadsAvailable : s_serverHomesteads;

        private static string Ours => FiresGhettoNetworkMod.PluginVersion;

        public static void InitConfig(ConfigFile config)
        {
            ConfigRefuseMismatched = config.Bind("13 - Compatibility", "Refuse Mismatched FGN Clients", false,
                Cfg.Basic("Kicks players whose FGN version differs from the server's. Players without FGN can still join."));
        }

        /// <summary>True once this peer has told us it runs our exact FGN build and wire protocol.</summary>
        internal static bool IsMatched(ZNetPeer peer)
            => peer != null && s_peers.TryGetValue(peer.m_uid, out var entry) && entry.State == State.Matched;

        internal static void OnZNetStart()
        {
            s_isServer = ZNet.instance != null && ZNet.instance.IsServer();
            // A new ZNet means new connections: nothing decided about a peer in an earlier session may carry over (a server keeps
            // its session id across a client's rejoin, so a stale entry would stop the client greeting it).
            s_peers.Clear();
            s_refuseNow.Clear();
            ResetClientSide();
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.Register<ZPackage>(HelloRpc, RPC_Hello);
            ZRoutedRpc.instance.Register<string>(RefusedRpc, RPC_Refused);
        }

        internal static void OnDisconnect(ZNetPeer peer)
        {
            if (peer == null) return;
            s_peers.Remove(peer.m_uid);
            if (peer.m_server) ResetClientSide();
        }

        internal static void OnShutdown()
        {
            s_peers.Clear();
            s_refuseNow.Clear();
            ResetClientSide();
        }

        private static void ResetClientSide()
        {
            s_serverEchoSince = 0;
            s_nextGreet = 0;
            s_greetUntil = 0;
            s_pendingHud = null;
            s_pendingHudNow = false;
            s_serverSimulation = s_serverHomesteads = s_serverFlagsKnown = false;
        }

        /// <summary>Client: the server sent a ConnectionEcho, so it runs FGN of some version.</summary>
        internal static void NoteServerEcho()
        {
            if (s_serverEchoSince == 0) s_serverEchoSince = Time.realtimeSinceStartupAsDouble;
        }

        internal static void Tick(ZNet net)
        {
            if (net == null) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= s_nextSweep)
            {
                s_nextSweep = now + SweepSeconds;
                if (net.IsServer()) SweepServer(net, now);
                else SweepClient(net, now);
            }
            ShowPendingHud();
        }

        private static void SweepServer(ZNet net, double now)
        {
            foreach (ZNetPeer peer in net.GetPeers())
            {
                if (peer == null || peer.m_uid == 0L || !peer.IsReady()) continue;
                PeerVersion entry = Entry(peer.m_uid, now);
                if (entry.State == State.Unknown || entry.State == State.NoFgn)
                {
                    if (ConnectionEcho.HasFgn(peer))
                    {
                        if (entry.EchoSince == 0)
                        {
                            entry.EchoSince = now;
                            if (entry.State == State.NoFgn)
                            {
                                entry.State = State.Unknown;
                                entry.Reported = false;
                            }
                        }
                        // Greet as soon as the peer shows it runs FGN, so a new client hears from us even if its own hello was lost.
                        if (!entry.HelloSent)
                        {
                            entry.HelloSent = true;
                            SendHello(peer);
                        }
                        if (entry.State == State.Unknown && now - entry.EchoSince > OlderFgnSeconds)
                            Decide(peer, entry, State.Mismatched, $"older than {Ours}", 0);
                    }
                    else if (entry.State == State.Unknown && now - entry.ReadySince > NoFgnSeconds)
                    {
                        Decide(peer, entry, State.NoFgn, null, 0);
                    }
                }
                if (entry.RefuseAt > 0 && now >= entry.RefuseAt) s_refuseNow.Add(peer);
            }

            // Disconnecting edits ZNet's peer list, so never inside the loop above.
            foreach (ZNetPeer peer in s_refuseNow)
            {
                LoggerOptions.LogWarning($"[Version] disconnecting {NameOf(peer)}: 'Refuse Mismatched FGN Clients' is on.");
                net.Disconnect(peer);
            }
            s_refuseNow.Clear();
        }

        private static void SweepClient(ZNet net, double now)
        {
            if (ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected) return;
            ZNetPeer server = null;
            foreach (ZNetPeer peer in net.GetPeers())
                if (peer != null && peer.m_server) { server = peer; break; }
            if (server == null || server.m_uid == 0L) return;

            PeerVersion entry = Entry(server.m_uid, now);
            if (entry.State != State.Unknown) return;

            // The server drops our routed RPCs until it has accepted us, so greet only once connected, and keep greeting
            // until it answers, as CompressionGroup does.
            if (s_greetUntil == 0) s_greetUntil = now + GreetWindowSeconds;
            if (now >= s_nextGreet && now < s_greetUntil)
            {
                s_nextGreet = now + GreetIntervalSeconds;
                entry.HelloSent = true;
                if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(0L, HelloRpc, BuildHello());
            }

            if (s_serverEchoSince > 0 && now - s_serverEchoSince > ServerHelloSeconds)
                Decide(server, entry, State.Mismatched, $"older than {Ours}", 0);
        }

        private static PeerVersion Entry(long uid, double now)
        {
            if (!s_peers.TryGetValue(uid, out var entry))
            {
                entry = new PeerVersion { ReadySince = now };
                s_peers[uid] = entry;
            }
            return entry;
        }

        private static ZPackage BuildHello()
        {
            var pkg = new ZPackage();
            pkg.Write(Ours);
            pkg.Write(WireProtocol);
            // 1.5.82: trailing server flags. Older FGN stops reading after the protocol, so this needs no wire bump.
            bool server = ZNet.instance != null && ZNet.instance.IsServer();
            pkg.Write(server && ServerAuthorityPatches.SimulationActive);
            pkg.Write(server && CompanionKeepAlive.HomesteadsAvailable);
            return pkg;
        }

        // peer.m_server ? 0L : peer.m_uid, as CompressionGroup.SendHelloToPeer.
        private static void SendHello(ZNetPeer peer)
        {
            if (ZRoutedRpc.instance == null || peer == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_server ? 0L : peer.m_uid, HelloRpc, BuildHello());
        }

        // Both sides run this: record the sender's FGN, reply once, decide. The reply goes out before the decision so a
        // compression hello re-sent on a match reaches the peer after our version hello, never before it.
        private static void RPC_Hello(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || pkg == null) return;
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer == null) return;   // our own 0L send dispatched locally, or another client's hello passing through

            string version;
            int protocol;
            try
            {
                version = pkg.ReadString();
                protocol = pkg.ReadInt();
                if (!s_isServer && peer.m_server && pkg.GetPos() < pkg.Size())
                {
                    s_serverSimulation = pkg.ReadBool();
                    s_serverHomesteads = pkg.ReadBool();
                    if (!s_serverFlagsKnown)
                        LoggerOptions.LogDebug($"[Version] server flags: Server-Side Simulation {(s_serverSimulation ? "on" : "off")}, "
                            + $"homestead areas {(s_serverHomesteads ? "available" : "not available")}");
                    s_serverFlagsKnown = true;
                }
            }
            catch
            {
                version = "unreadable";
                protocol = 0;
            }

            PeerVersion entry = Entry(peer.m_uid, Time.realtimeSinceStartupAsDouble);
            if (!entry.HelloSent)
            {
                entry.HelloSent = true;
                SendHello(peer);
            }
            // The wire protocol is what has to agree; a release that changed nothing on the wire (same protocol) still pairs, and only
            // the version difference is reported. Every wire change bumps WireProtocol, so this never pairs two incompatible builds.
            bool same = protocol == WireProtocol;
            Decide(peer, entry, same ? State.Matched : State.Mismatched, version, protocol);
        }

        private static void RPC_Refused(long sender, string message)
        {
            ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(sender) : null;
            if (peer == null || !peer.m_server || string.IsNullOrEmpty(message)) return;
            LoggerOptions.LogWarning($"[Version] {message}");
            s_pendingHud = message;
            s_pendingHudNow = true;   // the connection closes in seconds; do not wait for the player to spawn
        }

        private static void Decide(ZNetPeer peer, PeerVersion entry, State state, string version, int protocol)
        {
            State before = entry.State;
            bool changed = before != state || entry.Version != version || entry.Protocol != protocol;
            entry.State = state;
            entry.Version = version;
            entry.Protocol = protocol;
            if (!changed && entry.Reported) return;
            entry.Reported = true;

            // Delta snapshots describe what a matched peer was sent; once it gets full vanilla sends they go stale, and a later
            // delta built on them could hold back a field the peer no longer has.
            if (before == State.Matched && state != State.Matched) ZDODeltaPatches.RemovePeer_Postfix(peer);

            string shown = protocol != 0 && protocol != WireProtocol ? $"{version} (wire {protocol})" : version;
            if (s_isServer) ReportOnServer(peer, entry, state, shown);
            else ReportOnClient(state, shown, protocol);

            if (state == State.Matched)
            {
                entry.RefuseAt = 0;
                CompressionGroup.OnPeerVersionMatched(peer);
            }
        }

        private static void ReportOnServer(ZNetPeer peer, PeerVersion entry, State state, string shown)
        {
            string name = NameOf(peer);
            switch (state)
            {
                case State.Matched:
                    if (shown != Ours) LoggerOptions.LogWarning($"[Version] peer {name} runs FGN {shown}, this server {Ours}: same wire protocol, so FGN wire paths stay on; update both to match.");
                    else LoggerOptions.LogDebug($"[Version] peer {name} runs FGN {shown}, same as this server: FGN wire paths on for this peer.");
                    break;
                case State.NoFgn:
                    LoggerOptions.LogDebug($"[Version] peer {name} has no FGN: plain vanilla, as always.");
                    break;
                case State.Mismatched:
                    // Plain vanilla for a peer is fine behaviour; only a REAL mismatch (its hello named another wire
                    // protocol) is a Warning. "No hello yet" is an inference (an FGN before 1.5.31, or a slow join) and is Info.
                    if (entry.Protocol != 0)
                        LoggerOptions.LogWarning($"[Version] peer {name} runs FGN {shown} vs server {Ours}: FGN wire paths OFF for this peer (plain vanilla)");
                    else
                        LoggerOptions.LogDebug($"[Version] peer {name} runs FGN but sent no version hello in {OlderFgnSeconds:0} s (an FGN before 1.5.31, or still loading): "
                            + "plain vanilla for this peer until its hello arrives.");
                    // A first join to a fresh world can spend 30+ s building world data and send nothing, so silence is never
                    // grounds for a refusal. Only a REAL mismatch (a hello naming another wire protocol) is refused; a silent peer stays plain vanilla until its hello.
                    if (entry.Protocol == 0)
                    {
                        if (ConfigRefuseMismatched != null && ConfigRefuseMismatched.Value)
                            LoggerOptions.LogDebug($"[Version] {name}: no hello yet, not refused (only a hello naming another wire protocol is).");
                    }
                    else if (ConfigRefuseMismatched != null && ConfigRefuseMismatched.Value && entry.RefuseAt == 0)
                    {
                        // Clients from before 1.5.31 have no handler for this and simply drop it; they still get disconnected.
                        string message = $"FiresGhettoNetworking version mismatch: server {Ours}, you {shown} — this server refuses "
                            + "mismatched FGN clients; update to match.";
                        if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, RefusedRpc, message);
                        entry.RefuseAt = Time.realtimeSinceStartupAsDouble + RefuseDelaySeconds;
                    }
                    break;
            }
        }

        private static void ReportOnClient(State state, string shown, int protocol)
        {
            switch (state)
            {
                case State.Matched:
                    LoggerOptions.LogDebug($"[Version] the server runs FGN {shown}, same as this client: FGN wire paths on.");
                    break;
                case State.Mismatched:
                    if (protocol != 0)
                    {
                        LoggerOptions.LogWarning($"[Version] the server runs FGN {shown} vs this client {Ours}: FGN wire paths OFF with this server (plain vanilla).");
                        s_pendingHud = $"FiresGhettoNetworking version mismatch: server {shown}, you {Ours} — update to match";
                    }
                    else
                        LoggerOptions.LogDebug($"[Version] the server runs FGN but sent no version hello in {ServerHelloSeconds:0} s (an FGN before 1.5.31): "
                            + "plain vanilla with this server until its hello arrives.");
                    break;
            }
        }

        // The mismatch notice waits for the player to be in the world so it is seen, not shown behind the loading screen.
        private static void ShowPendingHud()
        {
            if (s_pendingHud == null || MessageHud.instance == null) return;
            if (!s_pendingHudNow && Player.m_localPlayer == null) return;
            // It follows Ctrl+F3 like any vanilla message. While the HUD is hidden
            // it stays pending and shows once the player brings the HUD back; the Warning line above is in the log either way.
            if (Hud.IsUserHidden()) return;
            MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, s_pendingHud, 0, null, false);
            s_pendingHud = null;
            s_pendingHudNow = false;
        }

        private static string NameOf(ZNetPeer peer)
            => peer == null ? "unknown" : string.IsNullOrEmpty(peer.m_playerName) ? peer.m_uid.ToString() : peer.m_playerName;
    }
}
