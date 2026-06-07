# ZRoutedRpc — Decompiled Reference

Decompiled from `assembly_valheim.dll` using JetBrains decompiler.
This file is for reference only — not compiled into the mod.

---

## Key Fields

- `m_server` — `bool` — true on dedicated server
- `m_id` — `long` — our peer ID (server's session ID)
- `m_peers` — `List<ZNetPeer>` — connected RPC peers
- `m_functions` — `Dictionary<int, RoutedMethodBase>` — registered RPC handlers

## Key Inner Class: RoutedRPCData

- `m_msgID` — unique message ID (sender ID + incrementing counter)
- `m_senderPeerID` — who sent the RPC
- `m_targetPeerID` — who should receive (0 = broadcast / Everybody)
- `m_targetZDO` — which ZDO the RPC targets (ZDOID.None = global)
- `m_methodHash` — stable hash of the RPC method name
- `m_parameters` — ZPackage with serialized parameters

## Key Insights for Our RPC Router

### RouteRPC — How vanilla forwards RPCs

On server with `m_targetPeerID != 0`: sends only to that specific peer.
On server with `m_targetPeerID == 0`: broadcasts to ALL peers except sender.

**This is where Area-of-Interest filtering would go** — instead of sending
to all peers, check `m_targetZDO` position and only send to nearby peers.

### RPC_RoutedRPC — What we intercept

This is the method our `RpcRouterPatches` prefixes. Vanilla logic:
1. Deserialize RoutedRPCData from package
2. If targeted at us or broadcast ? HandleRoutedRPC locally
3. If server and not targeted at us ? RouteRPC to forward

Our prefix replaces this entirely on server, running handlers first.

---

## Full Decompiled Source

```csharp
// Decompiled with JetBrains decompiler
// Type: ZRoutedRpc
// Assembly: assembly_valheim, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null

using System;
using System.Collections.Generic;

#nullable disable
public class ZRoutedRpc
{
  public static long Everybody;
  public Action<long> m_onNewPeer;
  private int m_rpcMsgID = 1;
  private bool m_server;
  private long m_id;
  private readonly List<ZNetPeer> m_peers = new List<ZNetPeer>();
  private readonly Dictionary<int, RoutedMethodBase> m_functions = new Dictionary<int, RoutedMethodBase>();
  private static ZRoutedRpc s_instance;

  public static ZRoutedRpc instance => ZRoutedRpc.s_instance;

  public ZRoutedRpc(bool server)
  {
    ZRoutedRpc.s_instance = this;
    this.m_server = server;
  }

  public void SetUID(long uid) => this.m_id = uid;

  public void AddPeer(ZNetPeer peer)
  {
    this.m_peers.Add(peer);
    peer.m_rpc.Register<ZPackage>("RoutedRPC", new Action<ZRpc, ZPackage>(this.RPC_RoutedRPC));
    if (this.m_onNewPeer == null)
      return;
    this.m_onNewPeer(peer.m_uid);
  }

  public void RemovePeer(ZNetPeer peer) => this.m_peers.Remove(peer);

  private ZNetPeer GetPeer(long uid)
  {
    foreach (ZNetPeer peer in this.m_peers)
    {
      if (peer.m_uid == uid)
        return peer;
    }
    return (ZNetPeer) null;
  }

  public void InvokeRoutedRPC(long targetPeerID, string methodName, params object[] parameters)
  {
    this.InvokeRoutedRPC(targetPeerID, ZDOID.None, methodName, parameters);
  }

  public void InvokeRoutedRPC(string methodName, params object[] parameters)
  {
    this.InvokeRoutedRPC(this.GetServerPeerID(), methodName, parameters);
  }

  private long GetServerPeerID()
  {
    if (this.m_server)
      return this.m_id;
    return this.m_peers.Count > 0 ? this.m_peers[0].m_uid : 0L;
  }

  public void InvokeRoutedRPC(
    long targetPeerID,
    ZDOID targetZDO,
    string methodName,
    params object[] parameters)
  {
    ZRoutedRpc.RoutedRPCData routedRpcData = new ZRoutedRpc.RoutedRPCData();
    routedRpcData.m_msgID = this.m_id + (long) this.m_rpcMsgID++;
    routedRpcData.m_senderPeerID = this.m_id;
    routedRpcData.m_targetPeerID = targetPeerID;
    routedRpcData.m_targetZDO = targetZDO;
    routedRpcData.m_methodHash = methodName.GetStableHashCode();
    ZRpc.Serialize(parameters, ref routedRpcData.m_parameters);
    routedRpcData.m_parameters.SetPos(0);
    if (targetPeerID == this.m_id || targetPeerID == 0L)
      this.HandleRoutedRPC(routedRpcData);
    if (targetPeerID == this.m_id)
      return;
    this.RouteRPC(routedRpcData);
  }

  // =================================================================
  // RouteRPC — FORWARDS RPCs TO PEERS
  //
  // Server + targeted: send to specific peer only
  // Server + broadcast (targetPeerID==0): send to ALL except sender
  // Client: send to all peers (usually just the server)
  //
  // KEY OPPORTUNITY: For broadcast RPCs with a m_targetZDO, we could
  // look up the ZDO position and only send to peers within range.
  // =================================================================
  private void RouteRPC(ZRoutedRpc.RoutedRPCData rpcData)
  {
    ZPackage pkg = new ZPackage();
    rpcData.Serialize(pkg);
    if (this.m_server)
    {
      if (rpcData.m_targetPeerID != 0L)
      {
        ZNetPeer peer = this.GetPeer(rpcData.m_targetPeerID);
        if (peer == null || !peer.IsReady())
          return;
        peer.m_rpc.Invoke("RoutedRPC", (object) pkg);
      }
      else
      {
        foreach (ZNetPeer peer in this.m_peers)
        {
          if (rpcData.m_senderPeerID != peer.m_uid && peer.IsReady())
            peer.m_rpc.Invoke("RoutedRPC", (object) pkg);
        }
      }
    }
    else
    {
      foreach (ZNetPeer peer in this.m_peers)
      {
        if (peer.IsReady())
          peer.m_rpc.Invoke("RoutedRPC", (object) pkg);
      }
    }
  }

  // =================================================================
  // RPC_RoutedRPC — INBOUND RPC HANDLING (we prefix this)
  //
  // 1. Deserialize RoutedRPCData from package
  // 2. If targeted at us or broadcast ? HandleRoutedRPC locally
  // 3. If server and not targeted at us ? RouteRPC to forward
  // =================================================================
  private void RPC_RoutedRPC(ZRpc rpc, ZPackage pkg)
  {
    ZRoutedRpc.RoutedRPCData routedRpcData = new ZRoutedRpc.RoutedRPCData();
    routedRpcData.Deserialize(pkg);
    if (routedRpcData.m_targetPeerID == this.m_id || routedRpcData.m_targetPeerID == 0L)
      this.HandleRoutedRPC(routedRpcData);
    if (!this.m_server || routedRpcData.m_targetPeerID == this.m_id)
      return;
    this.RouteRPC(routedRpcData);
  }

  private void HandleRoutedRPC(ZRoutedRpc.RoutedRPCData data)
  {
    if (data.m_targetZDO.IsNone())
    {
      RoutedMethodBase routedMethodBase;
      if (!this.m_functions.TryGetValue(data.m_methodHash, out routedMethodBase))
        return;
      routedMethodBase.Invoke(data.m_senderPeerID, data.m_parameters);
    }
    else
    {
      ZDO zdo = ZDOMan.instance.GetZDO(data.m_targetZDO);
      if (zdo == null)
        return;
      ZNetView instance = ZNetScene.instance.FindInstance(zdo);
      if (!((UnityEngine.Object) instance != (UnityEngine.Object) null))
        return;
      instance.HandleRoutedRPC(data);
    }
  }

  public void Register(string name, Action<long> f)
  {
    this.m_functions.Add(name.GetStableHashCode(), (RoutedMethodBase) new RoutedMethod(f));
  }

  public void Register<T>(string name, Action<long, T> f)
  {
    this.m_functions.Add(name.GetStableHashCode(), (RoutedMethodBase) new RoutedMethod<T>(f));
  }

  public void Register<T, U>(string name, Action<long, T, U> f)
  {
    this.m_functions.Add(name.GetStableHashCode(), (RoutedMethodBase) new RoutedMethod<T, U>(f));
  }

  public void Register<T, U, V>(string name, Action<long, T, U, V> f)
  {
    this.m_functions.Add(name.GetStableHashCode(), (RoutedMethodBase) new RoutedMethod<T, U, V>(f));
  }

  public void Register<T, U, V, B>(string name, RoutedMethod<T, U, V, B>.Method f)
  {
    this.m_functions.Add(name.GetStableHashCode(), (RoutedMethodBase) new RoutedMethod<T, U, V, B>(f));
  }

  public void Register<T, U, V, B, K>(string name, RoutedMethod<T, U, V, B, K>.Method f)
  {
    this.m_functions.Add(name.GetStableHashCode(), (RoutedMethodBase) new RoutedMethod<T, U, V, B, K>(f));
  }

  public void Register<T, U, V, B, K, M>(string name, RoutedMethod<T, U, V, B, K, M>.Method f)
  {
    this.m_functions.Add(name.GetStableHashCode(), (RoutedMethodBase) new RoutedMethod<T, U, V, B, K, M>(f));
  }

  public class RoutedRPCData
  {
    public long m_msgID;
    public long m_senderPeerID;
    public long m_targetPeerID;
    public ZDOID m_targetZDO;
    public int m_methodHash;
    public ZPackage m_parameters = new ZPackage();

    public void Serialize(ZPackage pkg)
    {
      pkg.Write(this.m_msgID);
      pkg.Write(this.m_senderPeerID);
      pkg.Write(this.m_targetPeerID);
      pkg.Write(this.m_targetZDO);
      pkg.Write(this.m_methodHash);
      pkg.Write(this.m_parameters);
    }

    public void Deserialize(ZPackage pkg)
    {
      this.m_msgID = pkg.ReadLong();
      this.m_senderPeerID = pkg.ReadLong();
      this.m_targetPeerID = pkg.ReadLong();
      this.m_targetZDO = pkg.ReadZDOID();
      this.m_methodHash = pkg.ReadInt();
      this.m_parameters = pkg.ReadPackage();
    }
  }
}
```
