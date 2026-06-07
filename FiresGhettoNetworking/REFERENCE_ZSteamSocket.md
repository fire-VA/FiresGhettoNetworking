# ZSteamSocket — Decompiled Reference

Decompiled from `assembly_valheim.dll` using JetBrains decompiler.
This file is for reference only — not compiled into the mod.

---

## Key Fields

- `m_sendQueue` — `Queue<byte[]>` — outbound packet queue
- `m_pkgQueue` — `Queue<ZPackage>` — inbound packet queue
- `m_con` — `HSteamNetConnection` — Steam connection handle
- `m_gotData` — `bool` — set when new data received
- `m_totalSent` / `m_totalRecv` — byte counters
- `m_listenSocket` — `HSteamListenSocket` — server listen socket
- `m_hostSocket` — `static ZSteamSocket` — the active listen socket

## Key Insights

### SendQueuedPackages — The send loop

Sends one packet at a time from `m_sendQueue`. Each packet is a `byte[]`
copied to unmanaged memory via `Marshal.AllocHGlobal`, sent via
`SteamGameServerNetworkingSockets.SendMessageToConnection`, then freed.

**Log spam source**: When `SendMessageToConnection` returns anything other
than `k_EResultOK`, it logs `"Failed to send data"` and breaks the loop.
This is the BetterZeeLog target — suppress/throttle this message.

**Batching opportunity**: Currently sends one `byte[]` per call. Multiple
small packets could be concatenated with length prefixes into a single
Steam message to reduce per-packet overhead (~40 bytes per message).

### Send — How packets enter the queue

Simply enqueues `pkg.GetArray()` and calls `SendQueuedPackages()`.
No batching, no coalescing.

### Recv — How packets are received

Calls `SteamGameServerNetworkingSockets.ReceiveMessagesOnConnection` for
exactly 1 message at a time. Could potentially receive multiple per call.

### GetSendQueueSize — Used by our queue limit patch

Sums all `byte[]` lengths in `m_sendQueue` + Steam's internal pending
reliable + unreliable + unacked reliable bytes.

### RegisterGlobalCallbacks — Default Steam networking config

Sets:
- `TimeoutConnected` = 30000 (30 sec)
- `IP_AllowWithoutAuth` = 1
- `SendRateMin` = 153600 (150 KB/s)
- `SendRateMax` = 153600 (150 KB/s)

Our `NetworkRatesGroup` overrides SendRateMin/Max after this.

---

## Full Decompiled Source

```csharp
// Decompiled with JetBrains decompiler
// Type: ZSteamSocket
// Assembly: assembly_valheim, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null

using Steamworks;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

#nullable disable
public class ZSteamSocket : IDisposable, ISocket
{
  private static List<ZSteamSocket> m_sockets = new List<ZSteamSocket>();
  private static Callback<SteamNetConnectionStatusChangedCallback_t> m_statusChanged;
  private static int m_steamDataPort = 2459;
  private Queue<ZSteamSocket> m_pendingConnections = new Queue<ZSteamSocket>();
  private HSteamNetConnection m_con = HSteamNetConnection.Invalid;
  private SteamNetworkingIdentity m_peerID;
  private Queue<ZPackage> m_pkgQueue = new Queue<ZPackage>();
  private Queue<byte[]> m_sendQueue = new Queue<byte[]>();
  private int m_totalSent;
  private int m_totalRecv;
  private bool m_gotData;
  private HSteamListenSocket m_listenSocket = HSteamListenSocket.Invalid;
  private static ZSteamSocket m_hostSocket;
  private static ESteamNetworkingConfigValue[] m_configValues = new ESteamNetworkingConfigValue[1];

  public ZSteamSocket()
  {
    ZSteamSocket.RegisterGlobalCallbacks();
    ZSteamSocket.m_sockets.Add(this);
  }

  public ZSteamSocket(SteamNetworkingIPAddr host)
  {
    ZSteamSocket.RegisterGlobalCallbacks();
    string buf;
    host.ToString(out buf, true);
    ZLog.Log((object) ("Starting to connect to " + buf));
    this.m_con = SteamNetworkingSockets.ConnectByIPAddress(ref host, 0, (SteamNetworkingConfigValue_t[]) null);
    ZSteamSocket.m_sockets.Add(this);
  }

  public ZSteamSocket(CSteamID peerID)
  {
    ZSteamSocket.RegisterGlobalCallbacks();
    this.m_peerID.SetSteamID(peerID);
    this.m_con = SteamGameServerNetworkingSockets.ConnectP2P(ref this.m_peerID, 0, 0, (SteamNetworkingConfigValue_t[]) null);
    ZLog.Log((object) ("Connecting to " + this.m_peerID.GetSteamID().ToString()));
    ZSteamSocket.m_sockets.Add(this);
  }

  public ZSteamSocket(HSteamNetConnection con)
  {
    ZSteamSocket.RegisterGlobalCallbacks();
    this.m_con = con;
    SteamNetConnectionInfo_t pInfo;
    SteamGameServerNetworkingSockets.GetConnectionInfo(this.m_con, out pInfo);
    this.m_peerID = pInfo.m_identityRemote;
    ZLog.Log((object) ("Connecting to " + this.m_peerID.ToString()));
    ZSteamSocket.m_sockets.Add(this);
  }

  private static void RegisterGlobalCallbacks()
  {
    if (ZSteamSocket.m_statusChanged != null)
      return;
    ZSteamSocket.m_statusChanged = Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(new Callback<SteamNetConnectionStatusChangedCallback_t>.DispatchDelegate(ZSteamSocket.OnStatusChanged));
    GCHandle gcHandle1 = GCHandle.Alloc((object) 30000f, GCHandleType.Pinned);
    GCHandle gcHandle2 = GCHandle.Alloc((object) 1, GCHandleType.Pinned);
    GCHandle gcHandle3 = GCHandle.Alloc((object) 153600, GCHandleType.Pinned);
    SteamGameServerNetworkingUtils.SetConfigValue(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Float, gcHandle1.AddrOfPinnedObject());
    SteamGameServerNetworkingUtils.SetConfigValue(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_IP_AllowWithoutAuth, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, gcHandle2.AddrOfPinnedObject());
    SteamGameServerNetworkingUtils.SetConfigValue(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, gcHandle3.AddrOfPinnedObject());
    SteamGameServerNetworkingUtils.SetConfigValue(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, gcHandle3.AddrOfPinnedObject());
    gcHandle1.Free();
    gcHandle2.Free();
    gcHandle3.Free();
  }

  private static void UnregisterGlobalCallbacks()
  {
    ZLog.Log((object) ("ZSteamSocket  UnregisterGlobalCallbacks, existing sockets:" + ZSteamSocket.m_sockets.Count.ToString()));
    if (ZSteamSocket.m_statusChanged == null)
      return;
    ZSteamSocket.m_statusChanged.Dispose();
    ZSteamSocket.m_statusChanged = (Callback<SteamNetConnectionStatusChangedCallback_t>) null;
  }

  private static void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t data)
  {
    ZLog.Log((object) ("Got status changed msg " + data.m_info.m_eState.ToString()));
    if (data.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected && data.m_eOldState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting)
    {
      ZLog.Log((object) "Connected");
      ZSteamSocket socket = ZSteamSocket.FindSocket(data.m_hConn);
      if (socket != null)
      {
        SteamNetConnectionInfo_t pInfo;
        if (SteamGameServerNetworkingSockets.GetConnectionInfo(data.m_hConn, out pInfo))
          socket.m_peerID = pInfo.m_identityRemote;
        ZLog.Log((object) ("Got connection SteamID " + socket.m_peerID.GetSteamID().ToString()));
      }
    }
    if (data.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting && data.m_eOldState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None)
    {
      ZLog.Log((object) "New connection");
      ZSteamSocket.GetListner()?.OnNewConnection(data.m_hConn);
    }
    if (data.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
    {
      ZLog.Log((object) $"Got problem {data.m_info.m_eEndReason.ToString()}:{data.m_info.m_szEndDebug}");
      ZSteamSocket socket = ZSteamSocket.FindSocket(data.m_hConn);
      if (socket != null)
      {
        ZLog.Log((object) ("  Closing socket " + socket.GetHostName()));
        socket.Close();
      }
    }
    if (data.m_info.m_eState != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer)
      return;
    ZLog.Log((object) ("Socket closed by peer " + data.ToString()));
    ZSteamSocket socket1 = ZSteamSocket.FindSocket(data.m_hConn);
    if (socket1 == null)
      return;
    ZLog.Log((object) ("  Closing socket " + socket1.GetHostName()));
    socket1.Close();
  }

  private static ZSteamSocket FindSocket(HSteamNetConnection con)
  {
    foreach (ZSteamSocket socket in ZSteamSocket.m_sockets)
    {
      if (socket.m_con == con)
        return socket;
    }
    return (ZSteamSocket) null;
  }

  public void Dispose()
  {
    ZLog.Log((object) "Disposing socket");
    this.Close();
    this.m_pkgQueue.Clear();
    ZSteamSocket.m_sockets.Remove(this);
    if (ZSteamSocket.m_sockets.Count != 0)
      return;
    ZLog.Log((object) "Last socket, unregistering callback");
    ZSteamSocket.UnregisterGlobalCallbacks();
  }

  public void Close()
  {
    if (this.m_con != HSteamNetConnection.Invalid)
    {
      ZLog.Log((object) ("Closing socket " + this.GetEndPointString()));
      this.Flush();
      ZLog.Log((object) ("  send queue size:" + this.m_sendQueue.Count.ToString()));
      Thread.Sleep(100);
      CSteamID steamId = this.m_peerID.GetSteamID();
      SteamGameServerNetworkingSockets.CloseConnection(this.m_con, 0, "", false);
      SteamGameServer.EndAuthSession(steamId);
      this.m_con = HSteamNetConnection.Invalid;
    }
    if (this.m_listenSocket != HSteamListenSocket.Invalid)
    {
      ZLog.Log((object) "Stopping listening socket");
      SteamGameServerNetworkingSockets.CloseListenSocket(this.m_listenSocket);
      this.m_listenSocket = HSteamListenSocket.Invalid;
    }
    if (ZSteamSocket.m_hostSocket == this)
      ZSteamSocket.m_hostSocket = (ZSteamSocket) null;
    this.m_peerID.Clear();
  }

  public bool StartHost()
  {
    if (ZSteamSocket.m_hostSocket != null)
    {
      ZLog.Log((object) "Listen socket already started");
      return false;
    }
    this.m_listenSocket = SteamGameServerNetworkingSockets.CreateListenSocketIP(ref new SteamNetworkingIPAddr()
    {
      m_port = (ushort) ZSteamSocket.m_steamDataPort
    }, 0, (SteamNetworkingConfigValue_t[]) null);
    ZSteamSocket.m_hostSocket = this;
    this.m_pendingConnections.Clear();
    return true;
  }

  private void OnNewConnection(HSteamNetConnection con)
  {
    EResult eresult = SteamGameServerNetworkingSockets.AcceptConnection(con);
    ZLog.Log((object) ("Accepting connection " + eresult.ToString()));
    if (eresult != EResult.k_EResultOK)
      return;
    this.QueuePendingConnection(con);
  }

  private void QueuePendingConnection(HSteamNetConnection con)
  {
    this.m_pendingConnections.Enqueue(new ZSteamSocket(con));
  }

  public ISocket Accept()
  {
    if (this.m_listenSocket == HSteamListenSocket.Invalid)
      return (ISocket) null;
    return this.m_pendingConnections.Count > 0 ? (ISocket) this.m_pendingConnections.Dequeue() : (ISocket) null;
  }

  public bool IsConnected() => this.m_con != HSteamNetConnection.Invalid;

  public void Send(ZPackage pkg)
  {
    if (pkg.Size() == 0 || !this.IsConnected())
      return;
    this.m_sendQueue.Enqueue(pkg.GetArray());
    this.SendQueuedPackages();
  }

  public bool Flush()
  {
    this.SendQueuedPackages();
    HSteamNetConnection con = this.m_con;
    int num = (int) SteamGameServerNetworkingSockets.FlushMessagesOnConnection(this.m_con);
    return this.m_sendQueue.Count == 0;
  }

  // =================================================================
  // SendQueuedPackages — THE ACTUAL NETWORK SEND LOOP
  //
  // Sends one byte[] at a time from m_sendQueue via Steam.
  // Each packet: AllocHGlobal ? Copy ? SendMessageToConnection ? FreeHGlobal
  //
  // Send flags = 8 (k_nSteamNetworkingSend_Reliable)
  //
  // "Failed to send data" log happens here when EResult != OK.
  // This is the BetterZeeLog spam target.
  //
  // BATCHING: Could concatenate multiple small byte[] into one large
  // send with length-prefix framing to reduce per-packet overhead.
  // =================================================================
  private void SendQueuedPackages()
  {
    if (!this.IsConnected())
      return;
    while (this.m_sendQueue.Count > 0)
    {
      byte[] source = this.m_sendQueue.Peek();
      IntPtr num = Marshal.AllocHGlobal(source.Length);
      Marshal.Copy(source, 0, num, source.Length);
      EResult connection = SteamGameServerNetworkingSockets.SendMessageToConnection(this.m_con, num, (uint) source.Length, 8, out long _);
      Marshal.FreeHGlobal(num);
      if (connection == EResult.k_EResultOK)
      {
        this.m_totalSent += source.Length;
        this.m_sendQueue.Dequeue();
      }
      else
      {
        ZLog.Log((object) ("Failed to send data " + connection.ToString()));
        break;
      }
    }
  }

  public static void UpdateAllSockets(float dt)
  {
    foreach (ZSteamSocket socket in ZSteamSocket.m_sockets)
      socket.Update(dt);
  }

  private void Update(float dt) => this.SendQueuedPackages();

  private static ZSteamSocket GetListner() => ZSteamSocket.m_hostSocket;

  public ZPackage Recv()
  {
    if (!this.IsConnected())
      return (ZPackage) null;
    IntPtr[] ppOutMessages = new IntPtr[1];
    if (SteamGameServerNetworkingSockets.ReceiveMessagesOnConnection(this.m_con, ppOutMessages, 1) != 1)
      return (ZPackage) null;
    SteamNetworkingMessage_t structure = Marshal.PtrToStructure<SteamNetworkingMessage_t>(ppOutMessages[0]);
    byte[] numArray = new byte[structure.m_cbSize];
    Marshal.Copy(structure.m_pData, numArray, 0, structure.m_cbSize);
    ZPackage zpackage = new ZPackage(numArray);
    SteamNetworkingMessage_t.Release(ppOutMessages[0]);
    this.m_totalRecv += zpackage.Size();
    this.m_gotData = true;
    return zpackage;
  }

  public string GetEndPointString() => this.m_peerID.GetSteamID().ToString();

  public string GetHostName() => this.m_peerID.GetSteamID().ToString();

  public CSteamID GetPeerID() => this.m_peerID.GetSteamID();

  public bool IsHost() => ZSteamSocket.m_hostSocket != null;

  // =================================================================
  // GetSendQueueSize — Used by our queue limit transpiler
  //
  // Returns: our queued bytes + Steam's pending reliable/unreliable
  // + unacked reliable bytes. This is what SendZDOs checks against
  // the 102400 limit.
  // =================================================================
  public int GetSendQueueSize()
  {
    if (!this.IsConnected())
      return 0;
    int sendQueueSize = 0;
    foreach (byte[] send in this.m_sendQueue)
      sendQueueSize += send.Length;
    SteamNetConnectionRealTimeStatus_t pStatus = new SteamNetConnectionRealTimeStatus_t();
    SteamNetConnectionRealTimeLaneStatus_t pLanes = new SteamNetConnectionRealTimeLaneStatus_t();
    if (SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(this.m_con, ref pStatus, 0, ref pLanes) == EResult.k_EResultOK)
      sendQueueSize += pStatus.m_cbPendingReliable + pStatus.m_cbPendingUnreliable + pStatus.m_cbSentUnackedReliable;
    return sendQueueSize;
  }

  public int GetCurrentSendRate()
  {
    SteamNetConnectionRealTimeStatus_t pStatus = new SteamNetConnectionRealTimeStatus_t();
    SteamNetConnectionRealTimeLaneStatus_t pLanes = new SteamNetConnectionRealTimeLaneStatus_t();
    if (SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(this.m_con, ref pStatus, 0, ref pLanes) != EResult.k_EResultOK)
      return 0;
    int num = pStatus.m_cbPendingReliable + pStatus.m_cbPendingUnreliable + pStatus.m_cbSentUnackedReliable;
    foreach (byte[] send in this.m_sendQueue)
      num += send.Length;
    return num / Mathf.Clamp(pStatus.m_nPing, 5, 250) * 1000;
  }

  public void GetConnectionQuality(
    out float localQuality,
    out float remoteQuality,
    out int ping,
    out float outByteSec,
    out float inByteSec)
  {
    SteamNetConnectionRealTimeStatus_t pStatus = new SteamNetConnectionRealTimeStatus_t();
    SteamNetConnectionRealTimeLaneStatus_t pLanes = new SteamNetConnectionRealTimeLaneStatus_t();
    if (SteamNetworkingSockets.GetConnectionRealTimeStatus(this.m_con, ref pStatus, 0, ref pLanes) == EResult.k_EResultOK)
    {
      localQuality = pStatus.m_flConnectionQualityLocal;
      remoteQuality = pStatus.m_flConnectionQualityRemote;
      ping = pStatus.m_nPing;
      outByteSec = pStatus.m_flOutBytesPerSec;
      inByteSec = pStatus.m_flInBytesPerSec;
    }
    else
    {
      localQuality = 0.0f;
      remoteQuality = 0.0f;
      ping = 0;
      outByteSec = 0.0f;
      inByteSec = 0.0f;
    }
  }

  public void GetAndResetStats(out int totalSent, out int totalRecv)
  {
    totalSent = this.m_totalSent;
    totalRecv = this.m_totalRecv;
    this.m_totalSent = 0;
    this.m_totalRecv = 0;
  }

  public bool GotNewData()
  {
    int num = this.m_gotData ? 1 : 0;
    this.m_gotData = false;
    return num != 0;
  }

  public int GetHostPort() => this.IsHost() ? 1 : -1;

  public static void SetDataPort(int port) => ZSteamSocket.m_steamDataPort = port;

  public void VersionMatch()
  {
  }
}
```
