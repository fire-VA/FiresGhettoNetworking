# ZPlayFabSocket — Decompiled Reference

Decompiled from `assembly_valheim.dll` using JetBrains decompiler.
This file is for reference only — not compiled into the mod.

---

## Key Fields

- `m_sendQueue` — `Queue<byte[]>` — outbound packet queue
- `m_recvQueue` — `Queue<ZPackage>` — inbound packet queue
- `m_inFlightQueue` — `InFlightQueue` — reliable delivery tracker
- `m_outOfOrderQueue` — `Dictionary<uint, byte[]>` — out-of-order buffer
- `m_retransmitCache` — `List<byte[]>` — retransmit buffer
- `m_zlibWorkQueue` — `PlayFabZLibWorkQueue` — async compression
- `m_peer` — `PlayFabPlayer[]` — remote player (single element array)
- `m_useCompression` — `bool` — enabled after VersionMatch()
- `m_isClient` — `bool` — true for client sockets
- `m_state` — `ZPlayFabSocketState` — LISTEN/CONNECTING/CONNECTED/CLOSED

## Key Insights

### Reliable delivery is custom-built

PlayFab uses `DeliveryOption.Guaranteed` but also implements its own
ACK/retransmit layer on top. Each message gets a sequential ID (uint).
Receiver sends ACK with the next expected ID. Sender retransmits
after timeout if no ACK received.

### GetSendQueueSize — Different from Steam!

Returns `m_inFlightQueue.Bytes * 0.25f` — only 25% of in-flight bytes.
This means the 102400 queue limit in ZDOMan.SendZDOs effectively allows
4x more data to be queued for PlayFab vs Steam. This may be intentional
(PlayFab has higher latency) or a bug.

### Compression

Uses ZLib (not ZSTD like our mod). Compression is async via
`PlayFabZLibWorkQueue` — compress/decompress happens on worker threads,
results polled in `LateUpdate`. Only enabled after `VersionMatch()`.

### Recovery / Reconnection

Has elaborate reconnection logic with party resets, kickstart after
recovery, and retransmit of entire in-flight queue. Much more complex
than ZSteamSocket.

### InFlightQueue (inner class)

Tracks: head/tail sequence numbers, total bytes, payload queue.
Retransmit timer: 3s normal, 1s "small" (after retransmit).
Kickstart cooldown: 6s between full retransmits.

---

## Full Decompiled Source

```csharp
// Decompiled with JetBrains decompiler
// Type: ZPlayFabSocket
// Assembly: assembly_valheim, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null

using PlayFab.Party;
using Splatform;
using System;
using System.Collections.Generic;
using UnityEngine;

#nullable disable
public class ZPlayFabSocket : ZNetStats, IDisposable, ISocket
{
  private const byte PAYLOAD_DAT = 17;
  private const byte PAYLOAD_ACK = 42;
  private const byte PAYLOAD_INT = 64;
  private const int PAYLOAD_HEADER_LEN = 5;
  private const float PARTY_RESET_GRACE_SEC = 3f;
  private const float PARTY_RESET_TIMEOUT_SEC = 20f;
  private const float KICKSTART_COOLDOWN = 6f;
  private const float NETWORK_ERROR_WATCHDOG = 26f;
  private const float INFLIGHT_SCALING_FACTOR = 0.25f;
  private const byte INT_PLATFORM_ID = 1;
  private static ZPlayFabSocket s_listenSocket;
  private static readonly Dictionary<string, ZPlayFabSocket> s_connectSockets = new Dictionary<string, ZPlayFabSocket>();
  private static float s_durationToPartyReset;
  private static DateTime s_lastReception;
  private ZPlayFabSocketState m_state;
  private PlayFabPlayer[] m_peer;
  private string m_lobbyId;
  private readonly byte[] m_sndMsg = new byte[5];
  private readonly bool m_isClient;
  public readonly string m_remotePlayerId;
  private PlatformUserID m_platformPlayerId;
  private readonly Queue<ZPackage> m_recvQueue = new Queue<ZPackage>();
  private readonly Dictionary<uint, byte[]> m_outOfOrderQueue = new Dictionary<uint, byte[]>();
  private readonly Queue<byte[]> m_sendQueue = new Queue<byte[]>();
  private readonly ZPlayFabSocket.InFlightQueue m_inFlightQueue = new ZPlayFabSocket.InFlightQueue();
  private readonly List<byte[]> m_retransmitCache = new List<byte[]>();
  private readonly List<Action> m_delayedInitActions = new List<Action>();
  private readonly PlayFabZLibWorkQueue m_zlibWorkQueue = new PlayFabZLibWorkQueue();
  private readonly Queue<ZPlayFabSocket> m_backlog = new Queue<ZPlayFabSocket>();
  private uint m_next;
  private float m_partyResetTimeout;
  private float m_partyResetConnectTimeout;
  private bool m_partyNetworkLeft;
  private bool m_didRecover;
  private float m_canKickstartIn;
  private bool m_useCompression;
  private Action<PlayFabMatchmakingServerData> m_serverDataFoundCallback;

  public ZPlayFabSocket()
  {
    this.m_state = ZPlayFabSocketState.LISTEN;
    PlayFabMultiplayerManager.Get().LogLevel = PlayFabMultiplayerManager.LogLevelType.None;
  }

  public ZPlayFabSocket(
    string remotePlayerId,
    Action<PlayFabMatchmakingServerData> serverDataFoundCallback)
  {
    PlayFabMultiplayerManager.Get().LogLevel = PlayFabMultiplayerManager.LogLevelType.None;
    this.m_state = ZPlayFabSocketState.CONNECTING;
    this.m_remotePlayerId = remotePlayerId;
    this.ClientConnect();
    PlayFabMultiplayerManager.Get().OnDataMessageReceived += new PlayFabMultiplayerManager.OnDataMessageReceivedHandler(this.OnDataMessageReceived);
    PlayFabMultiplayerManager.Get().OnRemotePlayerJoined += new PlayFabMultiplayerManager.OnRemotePlayerJoinedHandler(this.OnRemotePlayerJoined);
    this.m_isClient = true;
    this.m_platformPlayerId = PlatformManager.DistributionPlatform.LocalUser.PlatformUserID;
    this.m_serverDataFoundCallback = serverDataFoundCallback;
    ZPackage pkg = new ZPackage();
    pkg.Write((byte) 1);
    pkg.Write(this.m_platformPlayerId.ToString());
    this.Send(pkg, (byte) 64);
    ZLog.Log((object) $"PlayFab socket with remote ID {remotePlayerId} sent local Platform ID {this.GetHostName()}");
  }

  private void ClientConnect()
  {
    ZPlayFabMatchmaking.CheckHostOnlineStatus(this.m_remotePlayerId, new ZPlayFabMatchmakingSuccessCallback(this.OnRemotePlayerSessionFound), new ZPlayFabMatchmakingFailedCallback(this.OnRemotePlayerNotFound), true);
  }

  private ZPlayFabSocket(PlayFabPlayer remotePlayer)
  {
    this.InitRemotePlayer(remotePlayer);
    this.Connect(remotePlayer);
    this.m_isClient = false;
    this.m_remotePlayerId = remotePlayer.EntityKey.Id;
    PlayFabMultiplayerManager.Get().OnDataMessageReceived += new PlayFabMultiplayerManager.OnDataMessageReceivedHandler(this.OnDataMessageReceived);
    ZLog.Log((object) ("PlayFab listen socket child connected to remote player " + this.m_remotePlayerId));
  }

  private void InitRemotePlayer(PlayFabPlayer remotePlayer)
  {
    this.m_delayedInitActions.Add((Action) (() =>
    {
      remotePlayer.IsMuted = true;
      ZLog.Log((object) ("Muted PlayFab remote player " + remotePlayer.EntityKey.Id));
    }));
  }

  private void OnRemotePlayerSessionFound(PlayFabMatchmakingServerData serverData)
  {
    Action<PlayFabMatchmakingServerData> dataFoundCallback = this.m_serverDataFoundCallback;
    if (dataFoundCallback != null)
      dataFoundCallback(serverData);
    if (this.m_state == ZPlayFabSocketState.CLOSED)
      return;
    string networkId = PlayFabMultiplayerManager.Get().NetworkId;
    this.m_lobbyId = serverData.lobbyId;
    if (this.m_state == ZPlayFabSocketState.CONNECTING)
    {
      ZLog.Log((object) $"Joining server '{serverData.serverName}' at PlayFab network {serverData.networkId} from lobby {serverData.lobbyId}");
      PlayFabMultiplayerManager.Get().JoinNetwork(serverData.networkId);
      PlayFabMultiplayerManager.Get().OnNetworkJoined += new PlayFabMultiplayerManager.OnNetworkJoinedHandler(this.OnNetworkJoined);
    }
    else if (networkId == null || networkId != serverData.networkId || this.m_partyNetworkLeft)
    {
      ZLog.Log((object) $"Re-joining server '{serverData.serverName}' at new PlayFab network {serverData.networkId}");
      PlayFabMultiplayerManager.Get().JoinNetwork(serverData.networkId);
      this.m_partyNetworkLeft = false;
    }
    else
    {
      if (!this.PartyResetInProgress())
        return;
      ZLog.Log((object) $"Leave server '{serverData.serverName}' at new PlayFab network {serverData.networkId}, try to re-join later");
      this.ResetPartyTimeout();
      PlayFabMultiplayerManager.Get().LeaveNetwork();
      this.m_partyNetworkLeft = true;
    }
  }

  private void OnRemotePlayerNotFound(ZPLayFabMatchmakingFailReason failReason)
  {
    ZLog.LogWarning((object) ("Failed to locate network session for PlayFab player " + this.m_remotePlayerId));
    switch (failReason)
    {
      case ZPLayFabMatchmakingFailReason.InvalidServerData:
        ZNet.SetExternalError(ZNet.ConnectionStatus.ErrorVersion);
        break;
      case ZPLayFabMatchmakingFailReason.ServerFull:
        ZNet.SetExternalError(ZNet.ConnectionStatus.ErrorFull);
        break;
      case ZPLayFabMatchmakingFailReason.APIRequestLimitExceeded:
        this.ResetPartyTimeout();
        return;
    }
    this.Close();
  }

  private void CheckReestablishConnection(byte[] maybeCompressedBuffer)
  {
    try
    {
      this.OnDataMessageReceivedCont(this.m_zlibWorkQueue.UncompressOnThisThread(maybeCompressedBuffer));
      return;
    }
    catch { }
    byte[] buffer = maybeCompressedBuffer;
    byte msgType = this.GetMsgType(buffer);
    if (this.GetMsgId(buffer) != 0U || msgType != (byte) 64)
      return;
    ZLog.Log((object) $"Assume restarted game session for remote ID {this.GetEndPointString()} and Platform ID {this.GetHostName()}");
    this.ResetAll();
    this.OnDataMessageReceivedCont(buffer);
  }

  private void ResetAll()
  {
    this.m_recvQueue.Clear();
    this.m_outOfOrderQueue.Clear();
    this.m_sendQueue.Clear();
    this.m_inFlightQueue.ResetAll();
    this.m_retransmitCache.Clear();
    this.m_zlibWorkQueue.Poll(out List<byte[]> _, out List<byte[]> _);
    this.m_next = 0U;
    this.m_canKickstartIn = 0.0f;
    this.m_useCompression = false;
    this.m_didRecover = false;
    this.CancelResetParty();
  }

  private void OnDataMessageReceived(object sender, PlayFabPlayer from, byte[] compressedBuffer)
  {
    if (!(from.EntityKey.Id == this.m_remotePlayerId))
      return;
    this.DelayedInit();
    if (this.m_useCompression)
    {
      if (!this.m_isClient && this.m_didRecover)
        this.CheckReestablishConnection(compressedBuffer);
      else
        this.m_zlibWorkQueue.Decompress(compressedBuffer);
    }
    else
      this.OnDataMessageReceivedCont(compressedBuffer);
  }

  private void OnDataMessageReceivedCont(byte[] buffer)
  {
    byte msgType = this.GetMsgType(buffer);
    uint msgId = this.GetMsgId(buffer);
    ZPlayFabSocket.s_lastReception = DateTime.UtcNow;
    this.IncRecvBytes(buffer.Length);
    if (msgType == (byte) 42)
      this.ProcessAck(msgId);
    else if ((int) this.m_next != (int) msgId)
    {
      this.SendAck(this.m_next);
      if (msgId - this.m_next >= (uint) int.MaxValue || this.m_outOfOrderQueue.ContainsKey(msgId))
        return;
      this.m_outOfOrderQueue.Add(msgId, buffer);
    }
    else
    {
      switch (msgType)
      {
        case 17:
          this.m_recvQueue.Enqueue(new ZPackage(buffer, buffer.Length - 5));
          break;
        case 64:
          this.InternalReceive(new ZPackage(buffer, buffer.Length - 5));
          break;
        default:
          ZLog.LogError((object) $"Unknown message type {msgType.ToString()} received by socket!\nByte array:\n{BitConverter.ToString(buffer)}");
          return;
      }
      this.SendAck(++this.m_next);
      if (this.m_outOfOrderQueue.Count == 0)
        return;
      this.TryDeliverOutOfOrder();
    }
  }

  private void ProcessAck(uint msgId)
  {
    while ((int) this.m_inFlightQueue.Tail != (int) msgId)
    {
      if (this.m_inFlightQueue.IsEmpty)
      {
        this.Close();
        break;
      }
      this.m_inFlightQueue.Drop();
    }
  }

  private void TryDeliverOutOfOrder()
  {
    byte[] buffer;
    while (this.m_outOfOrderQueue.TryGetValue(this.m_next, out buffer))
    {
      this.m_outOfOrderQueue.Remove(this.m_next);
      this.OnDataMessageReceivedCont(buffer);
    }
  }

  private void InternalReceive(ZPackage pkg)
  {
    if (pkg.ReadByte() == (byte) 1)
    {
      this.m_platformPlayerId = new PlatformUserID(pkg.ReadString());
      ZLog.Log((object) $"PlayFab socket with remote ID {this.GetEndPointString()} received local Platform ID {this.GetHostName()}");
    }
    else
      ZLog.LogError((object) "Unknown data in internal receive! Ignoring");
  }

  private void SendAck(uint nextMsgId)
  {
    ZPlayFabSocket.SetMsgType(this.m_sndMsg, (byte) 42);
    ZPlayFabSocket.SetMsgId(this.m_sndMsg, nextMsgId);
    this.InternalSend(this.m_sndMsg);
  }

  private static void SetMsgType(byte[] payload, byte t) => payload[4] = t;

  private static void SetMsgId(byte[] payload, uint id)
  {
    payload[0] = (byte) id;
    payload[1] = (byte) (id >> 8);
    payload[2] = (byte) (id >> 16);
    payload[3] = (byte) (id >> 24);
  }

  private uint GetMsgId(byte[] buffer)
  {
    int index = buffer.Length - 5;
    return (uint) (0 + (int) buffer[index] + ((int) buffer[index + 1] << 8) + ((int) buffer[index + 2] << 16) + ((int) buffer[index + 3] << 24));
  }

  private byte GetMsgType(byte[] buffer) => buffer[buffer.Length - 1];

  private void DelayedInit()
  {
    if (this.m_delayedInitActions.Count == 0)
      return;
    foreach (Action delayedInitAction in this.m_delayedInitActions)
      delayedInitAction();
    this.m_delayedInitActions.Clear();
  }

  private void OnNetworkJoined(object sender, string networkId)
  {
    ZLog.Log((object) $"PlayFab client socket to remote player {this.m_remotePlayerId} joined network {networkId}");
    if (this.m_isClient && this.m_state == ZPlayFabSocketState.CONNECTED)
      this.ClientConnect();
    ZRpc.SetLongTimeout(true);
  }

  private void OnRemotePlayerJoined(object sender, PlayFabPlayer player)
  {
    this.InitRemotePlayer(player);
    if (!(player.EntityKey.Id == this.m_remotePlayerId))
      return;
    ZLog.Log((object) ("PlayFab socket connected to remote player " + this.m_remotePlayerId));
    this.Connect(player);
  }

  private void Connect(PlayFabPlayer remotePlayer)
  {
    string id = remotePlayer.EntityKey.Id;
    if (!ZPlayFabSocket.s_connectSockets.ContainsKey(id))
    {
      ZPlayFabSocket.s_connectSockets.Add(id, this);
      ZPlayFabSocket.s_lastReception = DateTime.UtcNow;
    }
    if (this.m_state == ZPlayFabSocketState.CONNECTED)
      ZLog.Log((object) ("Resume TX on " + this.GetEndPointString()));
    this.m_peer = new PlayFabPlayer[1]{ remotePlayer };
    this.m_state = ZPlayFabSocketState.CONNECTED;
    this.CancelResetParty();
    if (this.m_sendQueue.Count > 0)
    {
      this.m_inFlightQueue.ResetRetransTimer();
      while (this.m_sendQueue.Count > 0)
        this.InternalSend(this.m_sendQueue.Dequeue());
    }
    else
      this.KickstartAfterRecovery();
  }

  private bool PartyResetInProgress() => (double) this.m_partyResetTimeout > 0.0;

  private void CancelResetParty()
  {
    this.m_didRecover = this.PartyResetInProgress();
    this.m_partyNetworkLeft = false;
    this.m_partyResetTimeout = 0.0f;
    this.m_partyResetConnectTimeout = 0.0f;
    ZPlayFabSocket.s_durationToPartyReset = 0.0f;
  }

  private void InternalSend(byte[] payload)
  {
    if (this.PartyResetInProgress())
      return;
    this.IncSentBytes(payload.Length);
    if (this.m_useCompression)
    {
      if ((UnityEngine.Object) ZNet.instance != (UnityEngine.Object) null && ZNet.instance.HaveStopped)
        this.InternalSendCont(this.m_zlibWorkQueue.CompressOnThisThread(payload));
      else
        this.m_zlibWorkQueue.Compress(payload);
    }
    else
      this.InternalSendCont(payload);
  }

  private void InternalSendCont(byte[] compressedPayload)
  {
    if (this.PartyResetInProgress())
      return;
    if (PlayFabMultiplayerManager.Get().SendDataMessage(compressedPayload, (IEnumerable<PlayFabPlayer>) this.m_peer, DeliveryOption.Guaranteed))
    {
      if (this.m_isClient)
        return;
      ZPlayFabMatchmaking.ForwardProgress();
    }
    else
    {
      if (this.m_isClient)
        ZPlayFabSocket.ScheduleResetParty();
      this.ResetPartyTimeout();
      ZLog.Log((object) $"Failed to send, suspend TX on {this.GetEndPointString()} while trying to reconnect");
    }
  }

  private void ResetPartyTimeout()
  {
    this.m_partyResetConnectTimeout = UnityEngine.Random.Range(9f, 11f) + ZPlayFabSocket.s_durationToPartyReset;
    this.m_partyResetTimeout = UnityEngine.Random.Range(18f, 22f) + ZPlayFabSocket.s_durationToPartyReset;
  }

  internal static void ScheduleResetParty()
  {
    if ((double) ZPlayFabSocket.s_durationToPartyReset > 0.0)
      return;
    ZPlayFabSocket.s_durationToPartyReset = UnityEngine.Random.Range(2.69999981f, 3.30000019f);
  }

  public void Dispose()
  {
    Debug.Log((object) ("ZPlayFabSocket::Dispose. State: " + this.m_state.ToString()));
    this.m_zlibWorkQueue.Dispose();
    this.ResetAll();
    if (this.m_state == ZPlayFabSocketState.CLOSED)
      return;
    if (this.m_state == ZPlayFabSocketState.LISTEN)
    {
      ZPlayFabSocket.s_listenSocket = (ZPlayFabSocket) null;
      foreach (ZPlayFabSocket zplayFabSocket in this.m_backlog)
        zplayFabSocket.Close();
    }
    else
      PlayFabMultiplayerManager.Get().OnDataMessageReceived -= new PlayFabMultiplayerManager.OnDataMessageReceivedHandler(this.OnDataMessageReceived);
    if (!ZNet.instance.IsServer())
    {
      PlayFabMultiplayerManager.Get().OnRemotePlayerJoined -= new PlayFabMultiplayerManager.OnRemotePlayerJoinedHandler(this.OnRemotePlayerJoined);
      PlayFabMultiplayerManager.Get().OnNetworkJoined -= new PlayFabMultiplayerManager.OnNetworkJoinedHandler(this.OnNetworkJoined);
      PlayFabMultiplayerManager.Get().LeaveNetwork();
    }
    if (this.m_state == ZPlayFabSocketState.CONNECTED)
      ZPlayFabSocket.s_connectSockets.Remove(this.m_peer[0].EntityKey.Id);
    Debug.Log((object) ("ZPlayFabSocket::Dispose. leave lobby. LobbyId: " + this.m_lobbyId));
    if (this.m_lobbyId != null)
      ZPlayFabMatchmaking.LeaveLobby(this.m_lobbyId);
    this.m_state = ZPlayFabSocketState.CLOSED;
  }

  private void Update(float dt)
  {
    if ((double) this.m_canKickstartIn >= 0.0)
      this.m_canKickstartIn -= dt;
    if (!this.m_isClient)
      return;
    if (this.PartyResetInProgress())
    {
      this.m_partyResetTimeout -= dt;
      if ((double) this.m_partyResetConnectTimeout <= 0.0)
        return;
      this.m_partyResetConnectTimeout -= dt;
      if ((double) this.m_partyResetConnectTimeout > 0.0)
        return;
      this.ClientConnect();
    }
    else
    {
      if ((DateTime.UtcNow - ZPlayFabSocket.s_lastReception).TotalSeconds < 26.0 || this.m_state != ZPlayFabSocketState.CONNECTED)
        return;
      ZLog.Log((object) "Do a reset party as nothing seems to be received");
      this.ResetPartyTimeout();
      PlayFabMultiplayerManager.Get().ResetParty();
    }
  }

  private void LateUpdate()
  {
    List<byte[]> compressedBuffers;
    List<byte[]> decompressedBuffers;
    this.m_zlibWorkQueue.Poll(out compressedBuffers, out decompressedBuffers);
    if (compressedBuffers != null)
    {
      foreach (byte[] compressedPayload in compressedBuffers)
        this.InternalSendCont(compressedPayload);
    }
    if (decompressedBuffers == null)
      return;
    foreach (byte[] buffer in decompressedBuffers)
      this.OnDataMessageReceivedCont(buffer);
  }

  public bool IsConnected()
  {
    return this.m_state == ZPlayFabSocketState.CONNECTED || this.m_state == ZPlayFabSocketState.CONNECTING;
  }

  public void VersionMatch() => this.m_useCompression = true;

  public void Send(ZPackage pkg, byte messageType)
  {
    if (pkg.Size() == 0 || !this.IsConnected())
      return;
    pkg.Write(this.m_inFlightQueue.Head);
    pkg.Write(messageType);
    byte[] array = pkg.GetArray();
    this.m_inFlightQueue.Enqueue(array);
    if (this.m_state == ZPlayFabSocketState.CONNECTED)
      this.InternalSend(array);
    else
      this.m_sendQueue.Enqueue(array);
  }

  public void Send(ZPackage pkg) => this.Send(pkg, (byte) 17);

  public ZPackage Recv()
  {
    this.CheckRetransmit();
    return !this.GotNewData() ? (ZPackage) null : this.m_recvQueue.Dequeue();
  }

  private void CheckRetransmit()
  {
    if (this.m_inFlightQueue.IsEmpty || this.PartyResetInProgress() || this.m_state != ZPlayFabSocketState.CONNECTED || (double) Time.time < (double) this.m_inFlightQueue.NextResend)
      return;
    this.DoRetransmit();
  }

  private void DoRetransmit(bool canKickstart = true)
  {
    if (canKickstart && this.CanKickstartRatelimit())
    {
      this.KickstartAfterRecovery();
    }
    else
    {
      if (this.m_inFlightQueue.IsEmpty)
        return;
      this.InternalSend(this.m_inFlightQueue.Peek());
      this.m_inFlightQueue.ResetRetransTimer(true);
    }
  }

  private bool CanKickstartRatelimit() => (double) this.m_canKickstartIn <= 0.0;

  private void KickstartAfterRecovery()
  {
    try
    {
      this.TryKickstartAfterRecovery();
    }
    catch (Exception ex)
    {
      ZLog.LogWarning((object) $"Failed to resend data on ${this.GetEndPointString()}, closing socket: {ex.Message}");
      this.Close();
    }
  }

  private void TryKickstartAfterRecovery()
  {
    if (!this.m_inFlightQueue.IsEmpty)
    {
      this.m_inFlightQueue.CopyPayloads(this.m_retransmitCache);
      foreach (byte[] payload in this.m_retransmitCache)
        this.InternalSend(payload);
      this.m_retransmitCache.Clear();
      this.m_inFlightQueue.ResetRetransTimer();
    }
    this.m_canKickstartIn = 6f;
  }

  // =================================================================
  // GetSendQueueSize — NOTE: Returns 25% of in-flight bytes!
  // This means ZDOMan's 102400 limit is effectively 4x larger for PlayFab.
  // =================================================================
  public int GetSendQueueSize() => (int) ((double) this.m_inFlightQueue.Bytes * 0.25);

  public int GetCurrentSendRate() => throw new NotImplementedException();

  internal static uint NumSockets() => (uint) ZPlayFabSocket.s_connectSockets.Count;

  internal void StartHost()
  {
    if (ZPlayFabSocket.s_listenSocket != null)
      ZLog.LogError((object) "Multiple PlayFab listen sockets");
    else
      ZPlayFabSocket.s_listenSocket = this;
  }

  public bool IsHost() => this.m_state == ZPlayFabSocketState.LISTEN;

  public bool GotNewData() => this.m_recvQueue.Count > 0;

  public string GetEndPointString()
  {
    string str = "";
    if (this.m_peer != null)
      str = this.m_peer[0].EntityKey.Id;
    return "playfab/" + str;
  }

  public ISocket Accept()
  {
    if (this.m_backlog.Count == 0)
      return (ISocket) null;
    ZRpc.SetLongTimeout(true);
    return (ISocket) this.m_backlog.Dequeue();
  }

  public int GetHostPort() => !this.IsHost() ? -1 : 0;

  public bool Flush() => throw new NotImplementedException();

  public string GetHostName() => this.m_platformPlayerId.ToString();

  public void Close() => this.Dispose();

  internal static void LostConnection(PlayFabPlayer player)
  {
    string id = player.EntityKey.Id;
    ZPlayFabSocket zplayFabSocket;
    if (!ZPlayFabSocket.s_connectSockets.TryGetValue(id, out zplayFabSocket))
      return;
    ZLog.Log((object) $"Keep socket for {zplayFabSocket.GetEndPointString()}, try to reconnect before timeout");
  }

  internal static void QueueConnection(PlayFabPlayer player)
  {
    string id = player.EntityKey.Id;
    ZPlayFabSocket zplayFabSocket;
    if (ZPlayFabSocket.s_connectSockets.TryGetValue(id, out zplayFabSocket))
    {
      ZLog.Log((object) ("Resume TX on " + zplayFabSocket.GetEndPointString()));
      zplayFabSocket.Connect(player);
    }
    else if (ZPlayFabSocket.s_listenSocket != null)
      ZPlayFabSocket.s_listenSocket.m_backlog.Enqueue(new ZPlayFabSocket(player));
    else
      ZLog.LogError((object) "Incoming PlayFab connection without any open listen socket");
  }

  internal static void DestroyListenSocket()
  {
    while (ZPlayFabSocket.s_connectSockets.Count > 0)
    {
      Dictionary<string, ZPlayFabSocket>.Enumerator enumerator = ZPlayFabSocket.s_connectSockets.GetEnumerator();
      enumerator.MoveNext();
      enumerator.Current.Value.Close();
    }
    ZPlayFabSocket.s_listenSocket.Close();
    ZPlayFabSocket.s_listenSocket = (ZPlayFabSocket) null;
  }

  internal static void UpdateAllSockets(float dt)
  {
    if ((double) ZPlayFabSocket.s_durationToPartyReset > 0.0)
    {
      ZPlayFabSocket.s_durationToPartyReset -= dt;
      if ((double) ZPlayFabSocket.s_durationToPartyReset < 0.0)
      {
        ZLog.Log((object) "Reset party to clear network error");
        PlayFabMultiplayerManager.Get().ResetParty();
      }
    }
    foreach (ZPlayFabSocket zplayFabSocket in ZPlayFabSocket.s_connectSockets.Values)
      zplayFabSocket.Update(dt);
  }

  internal static void LateUpdateAllSocket()
  {
    foreach (ZPlayFabSocket zplayFabSocket in ZPlayFabSocket.s_connectSockets.Values)
      zplayFabSocket.LateUpdate();
  }

  // === Inner Class ===

  public class InFlightQueue
  {
    private readonly Queue<byte[]> m_payloads = new Queue<byte[]>();
    private float m_nextResend;
    private uint m_size;
    private uint m_head;
    private uint m_tail;

    public uint Bytes => this.m_size;
    public uint Head => this.m_head;
    public uint Tail => this.m_tail;
    public bool IsEmpty => this.m_payloads.Count == 0;
    public float NextResend => this.m_nextResend;

    public void Enqueue(byte[] payload)
    {
      this.m_payloads.Enqueue(payload);
      this.m_size += (uint) payload.Length;
      ++this.m_head;
    }

    public void Drop()
    {
      this.m_size -= (uint) this.m_payloads.Dequeue().Length;
      ++this.m_tail;
      this.ResetRetransTimer();
    }

    public byte[] Peek() => this.m_payloads.Peek();

    public void CopyPayloads(List<byte[]> payloads)
    {
      while (this.m_payloads.Count > 0)
        payloads.Add(this.m_payloads.Dequeue());
      foreach (byte[] payload in payloads)
        this.m_payloads.Enqueue(payload);
    }

    public void ResetRetransTimer(bool small = false)
    {
      this.m_nextResend = Time.time + (small ? 1f : 3f);
    }

    public void ResetAll()
    {
      this.m_payloads.Clear();
      this.m_nextResend = 0.0f;
      this.m_size = 0U;
      this.m_head = 0U;
      this.m_tail = 0U;
    }
  }
}
```
