using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Splits a round trip into where it spent its time: the message's queue on the sender, the answering side's wait for its
    /// next frame, the answer's queue there, and the sender's wait for its own frame. Steam stamps when each message reached
    /// the process, which separates a side waiting for its main thread from time spent on the way. Serves the auto-tune probe
    /// (client to server) and the connection echo (server to each player).
    /// </summary>
    [HarmonyPatch]
    internal static class RoundTripTrace
    {
        public const string RpcProbeStages = "FiresGhetto.AutoTune.PongStages";
        public const string RpcEchoStages = "FiresGhetto.Echo.Stages";

        private const int StagesFormat = 2;
        private const double MicrosecondsPerSecond = 1000000.0;
        private const float MicrosecondsPerMillisecond = 1000f;
        private const double MillisecondsPerSecond = 1000.0;
        private const double BytesPerKilobyte = 1024.0;
        private const double BytesPerMegabyte = 1024.0 * 1024.0;
        private const double SharedClockToleranceMs = 2.0;
        private const int MaxEchoSamples = 400;
        private static readonly double TicksPerMicrosecond = Stopwatch.Frequency / MicrosecondsPerSecond;

        /// <summary>Which side is which in the text, from the point of view of the side that started the round trip.</summary>
        private struct Roles
        {
            public string Answerer;
            public string Sender;
        }

        private static readonly Roles ProbeRoles = new Roles { Answerer = "server", Sender = "client" };
        private static readonly Roles EchoRoles = new Roles { Answerer = "client", Sender = "server" };

        internal struct Receipt
        {
            public bool ArrivalKnown;
            public long ArrivalTicks;
            public long DrainStartTicks;
            public long FrameGapTicks;
            public int PacketsAhead;
            public int BytesAhead;
            public long HandledTicks;
        }

        internal struct SendQueue
        {
            public bool ValheimKnown;
            public int ValheimPackets;
            public int ValheimBytes;
            public bool SteamKnown;
            public int SteamPendingBytes;
            public float SteamWaitMs;
            public int PacingBytesPerSecond;
        }

        /// <summary>What the answering side saw: when our message reached it, what its frame was doing, and what its answer queued behind.</summary>
        internal struct AnswerLeg
        {
            public Receipt Receipt;
            public bool FrameKnown;
            public ServerFrameProfile.SubsystemTicks Frame;
            public SendQueue AnswerQueue;
            public long AnswerTicks;
        }

        private sealed class Trace
        {
            public long SentTicks;
            public SendQueue OutboundQueue;
            public bool Answered;
            public Receipt AnswerReceipt;
            public bool HasAnswerLeg;
            public long AnswerFrequency;
            public AnswerLeg Answer;
            public bool AnswerHeldForNextFrame;
        }

        private struct Stages
        {
            public double RoundTripMs;
            public bool SenderKnown;
            public bool AnswererKnown;
            public bool HasAnswerLeg;
            public bool SharedClock;
            public double ToAnswererMs;
            public double BackMs;
            public double BothWaysMs;
            public double AnswererFrameWaitMs;
            public double AnswererBehindPacketsMs;
            public double AnswererReplyMs;
            public double SenderFrameWaitMs;
            public double SenderBehindPacketsMs;
            public double AnswererFramesApartMs;
            public double AnswerQueuedKilobytes;
            public double AnswerSteamWaitMs;
            public double OutboundQueuedKilobytes;
            public double OutboundSteamWaitMs;
        }

        /// <summary>The echoes to one player: the one in flight, the samples since the last report, and the worst of them.</summary>
        private sealed class EchoWindow
        {
            public long PendingStamp;
            public Trace Pending;
            public readonly List<Stages> Samples = new List<Stages>();
            public Trace WorstTrace;
            public Stages WorstStages;
        }

        private static readonly Dictionary<int, Trace> s_probes = new Dictionary<int, Trace>();
        private static readonly Dictionary<ZNetPeer, EchoWindow> s_echoes = new Dictionary<ZNetPeer, EchoWindow>();
        private static AccessTools.FieldRef<ZSteamSocket, Queue<byte[]>> s_valheimSendQueue;
        private static bool s_valheimSendQueueResolved;
        private static bool s_arrivalHooked;
        private static bool s_failureLogged;

        private static long s_lastArrivalMicroseconds;
        private static int s_lastArrivalBytes;
        private static int s_drainPackets;
        private static int s_drainBytes;
        private static long s_drainStartTicks;
        private static int s_drainFrame = -1;
        private static long s_frameDrainTicks;
        private static long s_previousFrameDrainTicks;
        private static ServerFrameProfile.SubsystemTicks s_frameDrainSubsystems;
        private static ServerFrameProfile.SubsystemTicks s_previousFrameDrainSubsystems;

        [HarmonyPatch(typeof(ZSteamSocket), nameof(ZSteamSocket.Recv)), HarmonyTranspiler]
        static IEnumerable<CodeInstruction> NoteEachSteamArrival(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo noteArrival = AccessTools.Method(typeof(RoundTripTrace), nameof(NoteArrival));
            int hooked = 0;
            for (int i = 0; i < code.Count; i++)
            {
                if (!ReadsSteamMessage(code[i])) continue;
                code.Insert(i + 1, new CodeInstruction(OpCodes.Dup));
                code.Insert(i + 2, new CodeInstruction(OpCodes.Call, noteArrival));
                i += 2;
                hooked++;
            }

            s_arrivalHooked = hooked == 1;
            if (s_arrivalHooked) return code;
            LoggerOptions.LogWarning($"[RoundTrip] ZSteamSocket.Recv reads {hooked} Steam messages where 1 was expected; round-trip "
                + "traces will not separate each side's wait for its frame from the time on the way.");
            return instructions;
        }

        private static bool ReadsSteamMessage(CodeInstruction instruction) =>
            instruction.operand is MethodInfo method && method.IsGenericMethod && method.Name == nameof(Marshal.PtrToStructure)
            && method.GetGenericArguments()[0] == typeof(SteamNetworkingMessage_t);

        public static void NoteArrival(SteamNetworkingMessage_t message)
        {
            s_lastArrivalMicroseconds = message.m_usecTimeReceived.m_SteamNetworkingMicroseconds;
            s_lastArrivalBytes = message.m_cbSize;
            s_drainPackets++;
            s_drainBytes += message.m_cbSize;
        }

        [HarmonyPatch(typeof(ZRpc), nameof(ZRpc.Update)), HarmonyPrefix]
        static void NoteDrainStart()
        {
            long now = Stopwatch.GetTimestamp();
            int frame = Time.frameCount;
            if (frame != s_drainFrame)
            {
                s_drainFrame = frame;
                s_previousFrameDrainTicks = s_frameDrainTicks;
                s_frameDrainTicks = now;
                s_previousFrameDrainSubsystems = s_frameDrainSubsystems;
                s_frameDrainSubsystems = ServerFrameProfile.SubsystemTicksSoFar();
            }
            s_drainStartTicks = now;
            s_drainPackets = 0;
            s_drainBytes = 0;
        }

        // ====================== ANSWERING SIDE ======================

        /// <summary>Call before answering: records when the message arrived and what the answer is about to queue behind.</summary>
        internal static AnswerLeg BeginAnswerLeg(ZRpc rpc)
        {
            var leg = new AnswerLeg();
            try
            {
                leg.Receipt = CaptureReceipt(rpc);
                leg.FrameKnown = ZNet.instance != null && ZNet.instance.IsDedicated() && s_previousFrameDrainTicks > 0;
                leg.Frame = s_frameDrainSubsystems.Since(s_previousFrameDrainSubsystems);
                leg.AnswerQueue = CaptureSendQueue(rpc.GetSocket());
            }
            catch (Exception ex)
            {
                LogFailureOnce(ex);
            }
            leg.AnswerTicks = Stopwatch.GetTimestamp();
            return leg;
        }

        /// <summary>Call right after answering: ships this side's half to whoever started the round trip.</summary>
        internal static void SendAnswerLeg(ZRpc rpc, string stagesRpc, long key, AnswerLeg leg)
        {
            try
            {
                ZSteamSocket steam = SteamSocketOf(rpc.GetSocket());
                Queue<byte[]> valheimQueue = steam != null ? ValheimSendQueueOf(steam) : null;
                var pkg = new ZPackage();
                pkg.Write(StagesFormat);
                pkg.Write(key);
                pkg.Write(Stopwatch.Frequency);
                WriteReceipt(pkg, leg.Receipt);
                pkg.Write(leg.FrameKnown);
                WriteSubsystems(pkg, leg.Frame);
                WriteSendQueue(pkg, leg.AnswerQueue);
                pkg.Write(leg.AnswerTicks);
                pkg.Write(valheimQueue != null && valheimQueue.Count > 0);
                rpc.Invoke(stagesRpc, pkg);
            }
            catch (Exception ex)
            {
                LogFailureOnce(ex);
            }
        }

        private static bool ReadStagesKey(ZPackage pkg, out long key)
        {
            key = 0L;
            if (pkg.ReadInt() != StagesFormat) return false;
            key = pkg.ReadLong();
            return true;
        }

        private static void ReadAnswerLegBody(ZPackage pkg, Trace trace)
        {
            trace.AnswerFrequency = pkg.ReadLong();
            trace.Answer.Receipt = ReadReceipt(pkg);
            trace.Answer.FrameKnown = pkg.ReadBool();
            trace.Answer.Frame = ReadSubsystems(pkg);
            trace.Answer.AnswerQueue = ReadSendQueue(pkg);
            trace.Answer.AnswerTicks = pkg.ReadLong();
            trace.AnswerHeldForNextFrame = pkg.ReadBool();
            trace.HasAnswerLeg = true;
        }

        // ====================== AUTO-TUNE PROBE (client to server) ======================

        internal static void NotePingSent(int seq, ZNetPeer serverPeer)
        {
            var trace = new Trace();
            try
            {
                trace.OutboundQueue = CaptureSendQueue(serverPeer.m_socket);
            }
            catch (Exception ex)
            {
                LogFailureOnce(ex);
            }
            trace.SentTicks = Stopwatch.GetTimestamp();
            s_probes[seq] = trace;
        }

        internal static void NotePongReceived(ZRpc rpc, int seq)
        {
            if (!s_probes.TryGetValue(seq, out Trace trace)) return;
            try
            {
                trace.AnswerReceipt = CaptureReceipt(rpc);
                trace.Answered = true;
            }
            catch (Exception ex)
            {
                LogFailureOnce(ex);
            }
        }

        internal static void OnProbeStages(ZRpc rpc, ZPackage pkg)
        {
            if (!ReadStagesKey(pkg, out long seq)) return;
            if (!s_probes.TryGetValue((int)seq, out Trace trace)) return;
            ReadAnswerLegBody(pkg, trace);
        }

        internal static void ClearProbes() => s_probes.Clear();

        /// <summary>Logs one line per ping and a summary of the medians, drops the traces, and returns the lines for a console.</summary>
        internal static List<string> Report(List<int> seqs, string label)
        {
            var lines = new List<string>(seqs.Count + 1);
            var answered = new List<Stages>(seqs.Count);
            for (int i = 0; i < seqs.Count; i++)
            {
                if (!s_probes.TryGetValue(seqs[i], out Trace trace)) continue;
                s_probes.Remove(seqs[i]);
                string prefix = $"[RoundTrip] {label} {i + 1}/{seqs.Count}: ";
                if (!trace.Answered)
                {
                    lines.Add(prefix + "no reply.");
                    continue;
                }
                Stages stages = MeasureStages(trace);
                answered.Add(stages);
                lines.Add(prefix + DescribeTrace(trace, stages, ProbeRoles));
            }

            foreach (string line in lines) LoggerOptions.LogInfo(line);
            string summary = Summarize(label, seqs.Count, answered, ProbeRoles);
            LoggerOptions.LogMessage(summary);
            lines.Add(summary);
            return lines;
        }

        // ====================== CONNECTION ECHO (server to each player) ======================

        /// <summary>Call before the echo is stamped: records what it is about to queue behind.</summary>
        internal static void NoteEchoSending(ZNetPeer peer)
        {
            if (peer == null) return;
            try
            {
                EchoWindow window = EchoWindowFor(peer);
                FinishPendingEcho(window);
                window.Pending = new Trace { OutboundQueue = CaptureSendQueue(peer.m_socket) };
            }
            catch (Exception ex)
            {
                LogFailureOnce(ex);
            }
        }

        /// <summary>Call with the stamp the echo carries: that stamp is both its identity and its send time.</summary>
        internal static void NoteEchoSent(ZNetPeer peer, long stamp)
        {
            if (peer == null || !s_echoes.TryGetValue(peer, out EchoWindow window) || window.Pending == null) return;
            window.PendingStamp = stamp;
            window.Pending.SentTicks = stamp;
        }

        internal static void NoteEchoAnswered(ZNetPeer peer, ZRpc rpc, long stamp)
        {
            if (peer == null || !s_echoes.TryGetValue(peer, out EchoWindow window)) return;
            if (window.Pending == null || window.PendingStamp != stamp) return;
            try
            {
                window.Pending.AnswerReceipt = CaptureReceipt(rpc);
                window.Pending.Answered = true;
            }
            catch (Exception ex)
            {
                LogFailureOnce(ex);
            }
        }

        internal static void OnEchoStages(ZRpc rpc, ZPackage pkg)
        {
            if (!ReadStagesKey(pkg, out long stamp)) return;
            ZNetPeer peer = ConnectionEcho.PeerOf(rpc);
            if (peer == null || !s_echoes.TryGetValue(peer, out EchoWindow window)) return;
            if (window.Pending == null || stamp != window.PendingStamp) return;
            ReadAnswerLegBody(pkg, window.Pending);
            FinishPendingEcho(window);
        }

        internal static void ForgetEchoes(ZNetPeer peer)
        {
            if (peer == null) s_echoes.Clear();
            else s_echoes.Remove(peer);
        }

        /// <summary>The medians and the worst echo since the last call, for the [Links] report; clears the window.</summary>
        internal static string DescribeEchoes(ZNetPeer peer)
        {
            if (peer == null || !s_echoes.TryGetValue(peer, out EchoWindow window) || window.Samples.Count == 0) return null;

            var text = new StringBuilder(384);
            text.Append("round trip by stage, ").Append(window.Samples.Count).Append(" echoes: median ")
                .Append(Whole(Median(window.Samples, stages => true, stages => stages.RoundTripMs))).Append(" ms = ")
                .Append(DescribeMedians(window.Samples, EchoRoles))
                .Append(". Echo queued behind a median ").Append(Whole(Median(window.Samples, stages => true, stages => stages.OutboundQueuedKilobytes)))
                .Append(" KB (Steam est. ").Append(Whole(Median(window.Samples, stages => true, stages => stages.OutboundSteamWaitMs))).Append(" ms).");
            if (window.WorstTrace != null)
                text.Append(" Worst: ").Append(DescribeTrace(window.WorstTrace, window.WorstStages, EchoRoles));

            window.Samples.Clear();
            window.WorstTrace = null;
            return text.ToString();
        }

        private static EchoWindow EchoWindowFor(ZNetPeer peer)
        {
            if (!s_echoes.TryGetValue(peer, out EchoWindow window))
            {
                window = new EchoWindow();
                s_echoes[peer] = window;
            }
            return window;
        }

        private static void FinishPendingEcho(EchoWindow window)
        {
            Trace pending = window.Pending;
            window.Pending = null;
            if (pending == null || !pending.Answered) return;

            Stages stages = MeasureStages(pending);
            if (window.Samples.Count >= MaxEchoSamples) window.Samples.RemoveAt(0);
            window.Samples.Add(stages);
            if (window.WorstTrace == null || stages.RoundTripMs > window.WorstStages.RoundTripMs)
            {
                window.WorstTrace = pending;
                window.WorstStages = stages;
            }
        }

        // ====================== MEASUREMENT ======================

        private static Receipt CaptureReceipt(ZRpc rpc)
        {
            long now = Stopwatch.GetTimestamp();
            var receipt = new Receipt
            {
                HandledTicks = now,
                DrainStartTicks = s_drainStartTicks,
                FrameGapTicks = s_previousFrameDrainTicks > 0 ? s_frameDrainTicks - s_previousFrameDrainTicks : 0L,
                PacketsAhead = Math.Max(0, s_drainPackets - 1),
                BytesAhead = Math.Max(0, s_drainBytes - s_lastArrivalBytes),
            };
            if (!s_arrivalHooked || SteamSocketOf(rpc.GetSocket()) == null) return receipt;

            long ageMicroseconds = SteamNowMicroseconds() - s_lastArrivalMicroseconds;
            receipt.ArrivalTicks = now - (long)(ageMicroseconds * TicksPerMicrosecond);
            receipt.ArrivalKnown = true;
            return receipt;
        }

        private static long SteamNowMicroseconds()
        {
            SteamNetworkingMicroseconds now = ZNet.instance != null && ZNet.instance.IsDedicated()
                ? SteamGameServerNetworkingUtils.GetLocalTimestamp()
                : SteamNetworkingUtils.GetLocalTimestamp();
            return now.m_SteamNetworkingMicroseconds;
        }

        private static SendQueue CaptureSendQueue(ISocket socket)
        {
            var queue = new SendQueue();
            ZSteamSocket steam = SteamSocketOf(socket);
            if (steam == null) return queue;

            Queue<byte[]> valheimQueue = ValheimSendQueueOf(steam);
            queue.ValheimKnown = valheimQueue != null;
            if (queue.ValheimKnown)
            {
                foreach (byte[] packet in valheimQueue)
                {
                    queue.ValheimPackets++;
                    queue.ValheimBytes += packet.Length;
                }
            }

            if (!LinkController.TryStatus(steam, out SteamNetConnectionRealTimeStatus_t status)) return queue;
            queue.SteamKnown = true;
            queue.SteamPendingBytes = status.m_cbPendingReliable + status.m_cbPendingUnreliable;
            queue.SteamWaitMs = (long)status.m_usecQueueTime / MicrosecondsPerMillisecond;
            queue.PacingBytesPerSecond = status.m_nSendRateBytesPerSecond;
            return queue;
        }

        private static ZSteamSocket SteamSocketOf(ISocket socket) =>
            socket == null ? null : NetworkingRatesGroup.UnwrapSocket(socket) as ZSteamSocket;

        private static Queue<byte[]> ValheimSendQueueOf(ZSteamSocket socket)
        {
            if (!s_valheimSendQueueResolved)
            {
                s_valheimSendQueueResolved = true;
                FieldInfo field = AccessTools.Field(typeof(ZSteamSocket), "m_sendQueue");
                if (field != null && field.FieldType == typeof(Queue<byte[]>))
                    s_valheimSendQueue = AccessTools.FieldRefAccess<ZSteamSocket, Queue<byte[]>>(field);
            }
            return s_valheimSendQueue?.Invoke(socket);
        }

        private static Stages MeasureStages(Trace trace)
        {
            Receipt answer = trace.AnswerReceipt;
            var stages = new Stages
            {
                RoundTripMs = Ms(answer.HandledTicks - trace.SentTicks, Stopwatch.Frequency),
                SenderKnown = answer.ArrivalKnown,
                HasAnswerLeg = trace.HasAnswerLeg,
                OutboundQueuedKilobytes = (trace.OutboundQueue.ValheimBytes + trace.OutboundQueue.SteamPendingBytes) / BytesPerKilobyte,
                OutboundSteamWaitMs = trace.OutboundQueue.SteamWaitMs,
            };
            if (stages.SenderKnown)
                SplitWait(answer, Stopwatch.Frequency, out stages.SenderFrameWaitMs, out stages.SenderBehindPacketsMs);
            if (!trace.HasAnswerLeg) return stages;

            Receipt far = trace.Answer.Receipt;
            stages.AnswererReplyMs = Ms(trace.Answer.AnswerTicks - far.HandledTicks, trace.AnswerFrequency);
            stages.AnswererFramesApartMs = Ms(far.FrameGapTicks, trace.AnswerFrequency);
            stages.AnswerQueuedKilobytes = (trace.Answer.AnswerQueue.ValheimBytes + trace.Answer.AnswerQueue.SteamPendingBytes) / BytesPerKilobyte;
            stages.AnswerSteamWaitMs = trace.Answer.AnswerQueue.SteamWaitMs;
            stages.AnswererKnown = far.ArrivalKnown;
            if (stages.AnswererKnown)
                SplitWait(far, trace.AnswerFrequency, out stages.AnswererFrameWaitMs, out stages.AnswererBehindPacketsMs);
            if (!stages.AnswererKnown || !stages.SenderKnown) return stages;

            stages.BothWaysMs = Math.Max(0.0, stages.RoundTripMs - stages.AnswererFrameWaitMs - stages.AnswererBehindPacketsMs
                - stages.AnswererReplyMs - stages.SenderFrameWaitMs - stages.SenderBehindPacketsMs);
            if (trace.AnswerFrequency != Stopwatch.Frequency) return stages;

            double toAnswererMs = Ms(far.ArrivalTicks - trace.SentTicks, Stopwatch.Frequency);
            double backMs = Ms(answer.ArrivalTicks - trace.Answer.AnswerTicks, Stopwatch.Frequency);
            stages.SharedClock = toAnswererMs >= -SharedClockToleranceMs && backMs >= -SharedClockToleranceMs
                && toAnswererMs + backMs <= stages.RoundTripMs + SharedClockToleranceMs;
            if (!stages.SharedClock) return stages;
            stages.ToAnswererMs = Math.Max(0.0, toAnswererMs);
            stages.BackMs = Math.Max(0.0, backMs);
            return stages;
        }

        private static void SplitWait(Receipt receipt, long frequency, out double frameWaitMs, out double behindPacketsMs)
        {
            long drainReachedIt = Math.Max(receipt.DrainStartTicks, receipt.ArrivalTicks);
            frameWaitMs = Ms(Math.Max(0L, receipt.DrainStartTicks - receipt.ArrivalTicks), frequency);
            behindPacketsMs = Ms(Math.Max(0L, receipt.HandledTicks - drainReachedIt), frequency);
        }

        // ====================== TEXT ======================

        private static string DescribeTrace(Trace trace, Stages stages, Roles roles)
        {
            var parts = new List<string>(6);
            if (stages.SharedClock) parts.Add($"to {roles.Answerer} " + Whole(stages.ToAnswererMs));
            if (trace.HasAnswerLeg)
            {
                parts.Add(stages.AnswererKnown
                    ? $"{roles.Answerer} waited " + DescribeWait(stages.AnswererFrameWaitMs, stages.AnswererBehindPacketsMs, trace.Answer.Receipt)
                    : $"{roles.Answerer} wait unknown (no Steam arrival time)");
                parts.Add("reply " + Whole(stages.AnswererReplyMs));
            }
            if (stages.SharedClock) parts.Add($"back to {roles.Sender} " + Whole(stages.BackMs));
            else if (stages.AnswererKnown && stages.SenderKnown) parts.Add("on the way both directions " + Whole(stages.BothWaysMs));
            parts.Add(stages.SenderKnown
                ? $"{roles.Sender} waited " + DescribeWait(stages.SenderFrameWaitMs, stages.SenderBehindPacketsMs, trace.AnswerReceipt)
                : $"{roles.Sender} wait unknown (no Steam arrival time)");

            var text = new StringBuilder(512);
            text.Append(Whole(stages.RoundTripMs)).Append(" ms = ").Append(string.Join(" + ", parts)).Append('.');
            if (trace.HasAnswerLeg)
            {
                text.Append(' ').Append(Capitalise(roles.Answerer)).Append(" frames ").Append(Whole(stages.AnswererFramesApartMs)).Append(" ms apart");
                if (trace.Answer.FrameKnown)
                    text.Append(" (").Append(DescribeServerFrame(trace.Answer.Frame, trace.Answer.Receipt.FrameGapTicks, trace.AnswerFrequency)).Append(')');
                text.Append(". Reply queued behind ").Append(DescribeQueue(trace.Answer.AnswerQueue));
                if (trace.AnswerHeldForNextFrame) text.Append(", held in Valheim's queue for the next frame");
                text.Append('.');
            }
            else
            {
                text.Append(" The ").Append(roles.Answerer).Append(" sent no stage breakdown (older FGN build).");
            }
            text.Append(" Sent behind ").Append(DescribeQueue(trace.OutboundQueue))
                .Append("; ").Append(roles.Sender).Append(" frames ").Append(Whole(Ms(trace.AnswerReceipt.FrameGapTicks, Stopwatch.Frequency))).Append(" ms apart.");
            return text.ToString();
        }

        private static string DescribeWait(double frameWaitMs, double behindPacketsMs, Receipt receipt) =>
            $"{Whole(frameWaitMs)} for its frame + {Whole(behindPacketsMs)} behind {Packets(receipt.PacketsAhead)} ({Kilobytes(receipt.BytesAhead)} KB)";

        private static string DescribeServerFrame(ServerFrameProfile.SubsystemTicks frame, long frameGapTicks, long frequency)
        {
            double networkingMs = Ms(frame.Networking, frequency);
            double zonesMs = Ms(frame.Zones, frequency);
            double objectsMs = Ms(frame.Objects, frequency);
            double everythingElseMs = Math.Max(0.0, Ms(frameGapTicks, frequency) - networkingMs - zonesMs - objectsMs);
            return $"networking {Whole(networkingMs)} incl. sending {Whole(Ms(frame.Sending, frequency))}, zones {Whole(zonesMs)}, "
                + $"objects {Whole(objectsMs)}, everything else {Whole(everythingElseMs)}, {frame.Collections} GC";
        }

        private static string DescribeQueue(SendQueue queue)
        {
            if (!queue.ValheimKnown && !queue.SteamKnown) return "an unknown queue (not a Steam connection)";
            string valheim = queue.ValheimKnown ? $"{Kilobytes(queue.ValheimBytes)} KB in Valheim ({Packets(queue.ValheimPackets)})" : "Valheim's queue unknown";
            if (!queue.SteamKnown) return valheim;
            return $"{valheim} + {Kilobytes(queue.SteamPendingBytes)} KB in Steam, est. {Whole(queue.SteamWaitMs)} ms at "
                + $"{(queue.PacingBytesPerSecond / BytesPerMegabyte):F1} MB/s";
        }

        private static string Summarize(string label, int sent, List<Stages> answered, Roles roles)
        {
            if (answered.Count == 0) return $"[RoundTrip] {label}: none of {sent} pings answered.";

            var text = new StringBuilder(384);
            text.Append("[RoundTrip] ").Append(label).Append(": ").Append(answered.Count).Append('/').Append(sent)
                .Append(" answered, median ").Append(Whole(Median(answered, stages => true, stages => stages.RoundTripMs))).Append(" ms.")
                .Append(" Median by stage: ").Append(DescribeMedians(answered, roles)).Append('.');

            List<Stages> withAnswerLeg = answered.FindAll(stages => stages.HasAnswerLeg);
            if (withAnswerLeg.Count > 0)
            {
                text.Append(' ').Append(Capitalise(roles.Answerer)).Append(" frames a median ")
                    .Append(Whole(Median(withAnswerLeg, stages => true, stages => stages.AnswererFramesApartMs)))
                    .Append(" ms apart; the reply queued behind a median ")
                    .Append(Whole(Median(withAnswerLeg, stages => true, stages => stages.AnswerQueuedKilobytes)))
                    .Append(" KB (Steam est. ").Append(Whole(Median(withAnswerLeg, stages => true, stages => stages.AnswerSteamWaitMs))).Append(" ms).");
            }
            else
            {
                text.Append(" The ").Append(roles.Answerer).Append(" sent no stage breakdowns.");
            }
            return text.ToString();
        }

        /// <summary>The per-stage medians, joined as a sum, plus which stage is the largest.</summary>
        private static string DescribeMedians(List<Stages> samples, Roles roles)
        {
            var medians = new List<KeyValuePair<string, double>>(7);
            AddMedian(medians, $"to {roles.Answerer}", samples, stages => stages.SharedClock, stages => stages.ToAnswererMs);
            AddMedian(medians, $"{roles.Answerer} frame wait", samples, stages => stages.AnswererKnown, stages => stages.AnswererFrameWaitMs);
            AddMedian(medians, $"{roles.Answerer} behind earlier packets", samples, stages => stages.AnswererKnown, stages => stages.AnswererBehindPacketsMs);
            AddMedian(medians, $"back to {roles.Sender}", samples, stages => stages.SharedClock, stages => stages.BackMs);
            AddMedian(medians, "on the way both directions", samples, stages => stages.AnswererKnown && stages.SenderKnown && !stages.SharedClock, stages => stages.BothWaysMs);
            AddMedian(medians, $"{roles.Sender} frame wait", samples, stages => stages.SenderKnown, stages => stages.SenderFrameWaitMs);
            AddMedian(medians, $"{roles.Sender} behind earlier packets", samples, stages => stages.SenderKnown, stages => stages.SenderBehindPacketsMs);
            if (medians.Count == 0) return "nothing measurable";

            var text = new StringBuilder(256);
            KeyValuePair<string, double> largest = medians[0];
            for (int i = 0; i < medians.Count; i++)
            {
                text.Append(i == 0 ? "" : " + ").Append(medians[i].Key).Append(' ').Append(Whole(medians[i].Value));
                if (medians[i].Value > largest.Value) largest = medians[i];
            }
            return text.Append(" (largest: ").Append(largest.Key).Append(')').ToString();
        }

        private static void AddMedian(List<KeyValuePair<string, double>> medians, string stage, List<Stages> samples,
            Predicate<Stages> known, Func<Stages, double> value)
        {
            if (!samples.Exists(known)) return;
            medians.Add(new KeyValuePair<string, double>(stage, Median(samples, known, value)));
        }

        private static double Median(List<Stages> samples, Predicate<Stages> known, Func<Stages, double> value)
        {
            var values = new List<double>(samples.Count);
            foreach (Stages stages in samples)
                if (known(stages)) values.Add(value(stages));
            if (values.Count == 0) return 0.0;
            values.Sort();
            int middle = values.Count / 2;
            return values.Count % 2 == 0 ? (values[middle - 1] + values[middle]) * 0.5 : values[middle];
        }

        // ====================== WIRE ======================

        private static void WriteReceipt(ZPackage pkg, Receipt receipt)
        {
            pkg.Write(receipt.ArrivalKnown);
            pkg.Write(receipt.ArrivalTicks);
            pkg.Write(receipt.DrainStartTicks);
            pkg.Write(receipt.FrameGapTicks);
            pkg.Write(receipt.PacketsAhead);
            pkg.Write(receipt.BytesAhead);
            pkg.Write(receipt.HandledTicks);
        }

        private static Receipt ReadReceipt(ZPackage pkg) => new Receipt
        {
            ArrivalKnown = pkg.ReadBool(),
            ArrivalTicks = pkg.ReadLong(),
            DrainStartTicks = pkg.ReadLong(),
            FrameGapTicks = pkg.ReadLong(),
            PacketsAhead = pkg.ReadInt(),
            BytesAhead = pkg.ReadInt(),
            HandledTicks = pkg.ReadLong(),
        };

        private static void WriteSubsystems(ZPackage pkg, ServerFrameProfile.SubsystemTicks frame)
        {
            pkg.Write(frame.Networking);
            pkg.Write(frame.Sending);
            pkg.Write(frame.Zones);
            pkg.Write(frame.Objects);
            pkg.Write(frame.Collections);
        }

        private static ServerFrameProfile.SubsystemTicks ReadSubsystems(ZPackage pkg) => new ServerFrameProfile.SubsystemTicks
        {
            Networking = pkg.ReadLong(),
            Sending = pkg.ReadLong(),
            Zones = pkg.ReadLong(),
            Objects = pkg.ReadLong(),
            Collections = pkg.ReadInt(),
        };

        private static void WriteSendQueue(ZPackage pkg, SendQueue queue)
        {
            pkg.Write(queue.ValheimKnown);
            pkg.Write(queue.ValheimPackets);
            pkg.Write(queue.ValheimBytes);
            pkg.Write(queue.SteamKnown);
            pkg.Write(queue.SteamPendingBytes);
            pkg.Write(queue.SteamWaitMs);
            pkg.Write(queue.PacingBytesPerSecond);
        }

        private static SendQueue ReadSendQueue(ZPackage pkg) => new SendQueue
        {
            ValheimKnown = pkg.ReadBool(),
            ValheimPackets = pkg.ReadInt(),
            ValheimBytes = pkg.ReadInt(),
            SteamKnown = pkg.ReadBool(),
            SteamPendingBytes = pkg.ReadInt(),
            SteamWaitMs = pkg.ReadSingle(),
            PacingBytesPerSecond = pkg.ReadInt(),
        };

        private static double Ms(long ticks, long frequency) => ticks * MillisecondsPerSecond / frequency;

        private static string Whole(double milliseconds) => milliseconds.ToString("F0");

        private static string Kilobytes(int bytes) => (bytes / BytesPerKilobyte).ToString("F0");

        private static string Packets(int count) => count == 1 ? "1 packet" : count + " packets";

        private static string Capitalise(string role) => char.ToUpperInvariant(role[0]) + role.Substring(1);

        private static void LogFailureOnce(Exception ex)
        {
            if (s_failureLogged) return;
            s_failureLogged = true;
            LoggerOptions.LogWarning($"[RoundTrip] Tracing a round trip failed ({ex.GetType().Name}: {ex.Message}); the round trip itself is "
                + "unaffected. Further failures are not logged.");
        }
    }
}
