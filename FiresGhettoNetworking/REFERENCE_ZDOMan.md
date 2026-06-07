# ZDOMan — Decompiled Reference

Decompiled from `assembly_valheim.dll` using JetBrains decompiler.
This file is for reference only — not compiled into the mod.

---

## Key Structures

### ZDOPeer (inner class)
- `m_peer` — ZNetPeer reference
- `m_zdos` — `Dictionary<ZDOID, PeerZDOInfo>` tracking what was last sent to each peer
- `m_forceSend` — `HashSet<ZDOID>` for priority sends
- `m_invalidSector` — `HashSet<ZDOID>` for sector invalidation notifications

### PeerZDOInfo (inner struct)
- `m_dataRevision` — `uint` — last data revision sent to this peer
- `m_ownerRevision` — `ushort` — last owner revision sent to this peer
- `m_syncTime` — `float` — Time.time when last synced

---

## Full Decompiled Source

```csharp
// Decompiled with JetBrains decompiler
// Type: ZDOMan
// Assembly: assembly_valheim, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;

#nullable disable
public class ZDOMan
{
  public Action<ZDO> m_onZDODestroyed;
  private readonly long m_sessionID = Utils.GenerateUID();
  private uint m_nextUid = 1;
  private readonly List<ZDO> m_portalObjects = new List<ZDO>();
  private readonly Dictionary<Vector2i, List<ZDO>> m_objectsByOutsideSector = new Dictionary<Vector2i, List<ZDO>>();
  private readonly List<ZDOMan.ZDOPeer> m_peers = new List<ZDOMan.ZDOPeer>();
  private readonly Dictionary<ZDOID, long> m_deadZDOs = new Dictionary<ZDOID, long>();
  private readonly List<ZDOID> m_destroySendList = new List<ZDOID>();
  private readonly HashSet<ZDOID> m_clientChangeQueue = new HashSet<ZDOID>();
  private readonly Dictionary<ZDOID, ZDO> m_objectsByID = new Dictionary<ZDOID, ZDO>();
  private List<ZDO>[] m_objectsBySector;
  private readonly int m_width;
  private readonly int m_halfWidth;
  private float m_sendTimer;
  private const float c_SendFPS = 20f;
  private float m_releaseZDOTimer;
  private int m_zdosSent;
  private int m_zdosRecv;
  private int m_zdosSentLastSec;
  private int m_zdosRecvLastSec;
  private float m_statTimer;
  private ZDOMan.SaveData m_saveData;
  private int m_nextSendPeer = -1;
  private readonly List<ZDO> m_tempToSync = new List<ZDO>();
  private readonly List<ZDO> m_tempToSyncDistant = new List<ZDO>();
  private readonly List<ZDO> m_tempNearObjects = new List<ZDO>();
  private readonly List<ZDOID> m_tempRemoveList = new List<ZDOID>();
  private readonly List<ZDO> m_tempSectorObjects = new List<ZDO>();
  private readonly List<List<ZDO>> m_tempNearObjectsForRemoval = new List<List<ZDO>>();
  private readonly List<bool> m_tempNearObjectsForRemovalAreasActive = new List<bool>();
  private static ZDOMan s_instance;
  private static long s_compareReceiver = 0;
  private static readonly List<int> s_brokenPrefabsToFilterOut = new List<int>()
  {
    1332933305,
    -1334479845
  };

  public static ZDOMan instance => ZDOMan.s_instance;

  public ZDOMan(int width)
  {
    ZDOMan.s_instance = this;
    ZRoutedRpc.instance.Register<ZPackage>("DestroyZDO", new Action<long, ZPackage>(this.RPC_DestroyZDO));
    ZRoutedRpc.instance.Register<ZDOID>("RequestZDO", new Action<long, ZDOID>(this.RPC_RequestZDO));
    this.m_width = width;
    this.m_halfWidth = this.m_width / 2;
    int num1 = ZoneSystem.instance.m_activeArea * 2 + 1;
    int num2 = num1 * num1;
    for (int index = 0; index < num2; ++index)
    {
      this.m_tempNearObjectsForRemoval.Add(new List<ZDO>());
      this.m_tempNearObjectsForRemovalAreasActive.Add(false);
    }
    ZDOID.Reset();
    this.ResetSectorArray();
    ZDOExtraData.Init();
  }

  private void ResetSectorArray()
  {
    this.m_objectsBySector = new List<ZDO>[this.m_width * this.m_width];
    this.m_objectsByOutsideSector.Clear();
  }

  public void ShutDown()
  {
    if (!ZNet.instance.IsServer())
      this.FlushClientObjects();
    ZDOPool.Release(this.m_objectsByID);
    this.m_objectsByID.Clear();
    this.m_tempToSync.Clear();
    this.m_tempToSyncDistant.Clear();
    this.m_tempNearObjects.Clear();
    this.m_tempRemoveList.Clear();
    this.m_peers.Clear();
    this.ResetSectorArray();
    ZDOExtraData.Reset();
    Game.instance.CollectResources();
  }

  public void PrepareSave()
  {
    this.m_saveData = new ZDOMan.SaveData();
    this.m_saveData.m_sessionID = this.m_sessionID;
    this.m_saveData.m_nextUid = this.m_nextUid;
    Stopwatch stopwatch1 = Stopwatch.StartNew();
    this.m_saveData.m_zdos = this.GetSaveClone();
    ZLog.Log((object) $"PrepareSave: clone done in {stopwatch1.ElapsedMilliseconds.ToString()}ms");
    Stopwatch stopwatch2 = Stopwatch.StartNew();
    ZDOExtraData.PrepareSave();
    ZLog.Log((object) $"PrepareSave: ZDOExtraData.PrepareSave done in {stopwatch2.ElapsedMilliseconds.ToString()} ms");
  }

  public void SaveAsync(BinaryWriter writer)
  {
    writer.Write(this.m_saveData.m_sessionID);
    writer.Write(this.m_saveData.m_nextUid);
    ZPackage pkg = new ZPackage();
    writer.Write(this.m_saveData.m_zdos.Count);
    pkg.SetWriter(writer);
    foreach (ZDO zdo in this.m_saveData.m_zdos)
      zdo.Save(pkg);
    ZLog.Log((object) $"Saved {this.m_saveData.m_zdos.Count.ToString()} ZDOs");
    foreach (ZDO zdo in this.m_saveData.m_zdos)
      zdo.Reset();
    this.m_saveData.m_zdos.Clear();
    this.m_saveData = (ZDOMan.SaveData) null;
    ZDOExtraData.ClearSave();
  }

  private void FilterZDO(
    ZDO zdo,
    ref List<ZDO> zdos,
    ref List<ZDO> warningZDOs,
    ref List<ZDO> brokenZDOs)
  {
    if (ZDOMan.s_brokenPrefabsToFilterOut.Contains(zdo.GetPrefab()))
    {
      brokenZDOs.Add(zdo);
    }
    else
    {
      if (!ZNetScene.instance.HasPrefab(zdo.GetPrefab()))
        warningZDOs.Add(zdo);
      zdos.Add(zdo);
    }
  }

  private void WarnAndRemoveBrokenZDOs(
    List<ZDO> warningZDOs,
    List<ZDO> brokenZDOs,
    int totalNumZDOs,
    int numZDOs)
  {
    int num1;
    if (warningZDOs.Count > 0)
    {
      int key = warningZDOs.Count;
      ZLog.LogWarning((object) $"Found {key.ToString()} ZDOs with unknown prefabs. Will load anyway.");
      Dictionary<int, int> dictionary1 = new Dictionary<int, int>();
      foreach (ZDO warningZdO in warningZDOs)
      {
        int prefab = warningZdO.GetPrefab();
        if (!dictionary1.TryAdd(prefab, 1))
        {
          Dictionary<int, int> dictionary2 = dictionary1;
          key = prefab;
          num1 = dictionary2[key]++;
        }
      }
      foreach (KeyValuePair<int, int> keyValuePair in dictionary1)
      {
        keyValuePair.Deconstruct(ref num1, ref key);
        int num2 = num1;
        int num3 = key;
        ZLog.LogWarning((object) $"    Hash {num2.ToString()} appeared {num3.ToString()} times.");
      }
    }
    if (brokenZDOs.Count <= 0)
      return;
    string[] strArray = new string[7];
    strArray[0] = "Found ";
    int key1 = brokenZDOs.Count;
    strArray[1] = key1.ToString();
    strArray[2] = " ZDOs with prefabs not supported. Removing. ";
    strArray[3] = totalNumZDOs.ToString();
    strArray[4] = " => ";
    strArray[5] = numZDOs.ToString();
    strArray[6] = " ZDOs loaded.";
    ZLog.LogError((object) string.Concat(strArray));
    Dictionary<int, int> dictionary3 = new Dictionary<int, int>();
    foreach (ZDO brokenZdO in brokenZDOs)
    {
      int prefab = brokenZdO.GetPrefab();
      if (!dictionary3.TryAdd(prefab, 1))
      {
        Dictionary<int, int> dictionary4 = dictionary3;
        key1 = prefab;
        num1 = dictionary4[key1]++;
      }
      ZDOPool.Release(brokenZdO);
    }
    foreach (KeyValuePair<int, int> keyValuePair in dictionary3)
    {
      keyValuePair.Deconstruct(ref num1, ref key1);
      int num4 = num1;
      int num5 = key1;
      ZLog.LogError((object) $"    Hash {num4.ToString()} filtered out {num5.ToString()} times.");
    }
  }

  public void Load(BinaryReader reader, int version)
  {
    reader.ReadInt64();
    uint num1 = reader.ReadUInt32();
    int totalNumZDOs = reader.ReadInt32();
    ZDOPool.Release(this.m_objectsByID);
    this.m_objectsByID.Clear();
    this.ResetSectorArray();
    ZDOExtraData.Init();
    ZLog.Log((object) $"Loading {totalNumZDOs.ToString()} zdos, my sessionID: {this.m_sessionID.ToString()}, data version: {version.ToString()}");
    List<ZDO> zdos = new List<ZDO>();
    zdos.Capacity = totalNumZDOs;
    ZNetScene instance = ZNetScene.instance;
    List<ZDO> brokenZDOs = new List<ZDO>();
    List<ZDO> warningZDOs = new List<ZDO>();
    ZLog.Log((object) "Loading in ZDOs");
    ZPackage pkg = new ZPackage();
    if (version < 31)
    {
      for (int index = 0; index < totalNumZDOs; ++index)
      {
        ZDO zdo = ZDOPool.Create();
        zdo.m_uid = new ZDOID(reader);
        int count = reader.ReadInt32();
        byte[] data = reader.ReadBytes(count);
        pkg.Load(data);
        zdo.LoadOldFormat(pkg, version);
        zdo.SetOwner(0L);
        this.FilterZDO(zdo, ref zdos, ref warningZDOs, ref brokenZDOs);
      }
    }
    else
    {
      pkg.SetReader(reader);
      for (int index = 0; index < totalNumZDOs; ++index)
      {
        ZDO zdo = ZDOPool.Create();
        zdo.Load(pkg, version);
        this.FilterZDO(zdo, ref zdos, ref warningZDOs, ref brokenZDOs);
      }
      num1 = (uint) (zdos.Count + 1);
    }
    this.WarnAndRemoveBrokenZDOs(warningZDOs, brokenZDOs, totalNumZDOs, zdos.Count);
    ZLog.Log((object) "Adding to Dictionary");
    foreach (ZDO zdo in zdos)
    {
      this.m_objectsByID.Add(zdo.m_uid, zdo);
      if (Game.instance.PortalPrefabHash.Contains(zdo.GetPrefab()))
        this.m_portalObjects.Add(zdo);
    }
    ZLog.Log((object) "Adding to Sectors");
    foreach (ZDO zdo in zdos)
      this.AddToSector(zdo, zdo.GetSector());
    if (version < 31)
    {
      ZLog.Log((object) "Converting Ships & Fishing-rods ownership");
      this.ConvertOwnerships(zdos);
      ZLog.Log((object) "Converting & mapping CreationTime");
      this.ConvertCreationTime(zdos);
      ZLog.Log((object) "Converting portals");
      this.ConvertPortals();
      ZLog.Log((object) "Converting spawners");
      this.ConvertSpawners();
      ZLog.Log((object) "Converting ZSyncTransforms");
      this.ConvertSyncTransforms();
      ZLog.Log((object) "Converting ItemSeeds");
      this.ConvertSeed();
      ZLog.Log((object) "Converting Dungeons");
      this.ConvertDungeonRooms(zdos);
    }
    else
    {
      ZLog.Log((object) "Connecting Portals, Spawners & ZSyncTransforms");
      this.ConnectPortals();
      this.ConnectSpawners();
      this.ConnectSyncTransforms();
    }
    Game.instance.ConnectPortals();
    this.m_deadZDOs.Clear();
    if (version < 31)
    {
      int num2 = reader.ReadInt32();
      for (int index = 0; index < num2; ++index)
      {
        reader.ReadInt64();
        int num3 = (int) reader.ReadUInt32();
        reader.ReadInt64();
      }
    }
    this.m_nextUid = num1;
  }

  public ZDO CreateNewZDO(Vector3 position, int prefabHash)
  {
    ZDOID zdoid = new ZDOID(this.m_sessionID, this.m_nextUid++);
    while (this.GetZDO(zdoid) != null)
      zdoid = new ZDOID(this.m_sessionID, this.m_nextUid++);
    return this.CreateNewZDO(zdoid, position, prefabHash);
  }

  private ZDO CreateNewZDO(ZDOID uid, Vector3 position, int prefabHashIn = 0)
  {
    ZDO newZdo = ZDOPool.Create(uid, position);
    newZdo.SetOwnerInternal(this.m_sessionID);
    this.m_objectsByID.Add(uid, newZdo);
    if (Game.instance.PortalPrefabHash.Contains(prefabHashIn != 0 ? prefabHashIn : newZdo.GetPrefab()))
      this.m_portalObjects.Add(newZdo);
    return newZdo;
  }

  public void AddToSector(ZDO zdo, Vector2i sector)
  {
    int index = this.SectorToIndex(sector);
    if (index >= 0)
    {
      if (this.m_objectsBySector[index] != null)
        this.m_objectsBySector[index].Add(zdo);
      else
        this.m_objectsBySector[index] = new List<ZDO>() { zdo };
    }
    else
    {
      List<ZDO> zdoList;
      if (this.m_objectsByOutsideSector.TryGetValue(sector, out zdoList))
      {
        zdoList.Add(zdo);
      }
      else
      {
        zdoList = new List<ZDO>();
        zdoList.Add(zdo);
        this.m_objectsByOutsideSector.Add(sector, zdoList);
      }
    }
  }

  public void ZDOSectorInvalidated(ZDO zdo)
  {
    foreach (ZDOMan.ZDOPeer peer in this.m_peers)
      peer.ZDOSectorInvalidated(zdo);
  }

  public void RemoveFromSector(ZDO zdo, Vector2i sector)
  {
    int index = this.SectorToIndex(sector);
    if (index >= 0)
    {
      if (this.m_objectsBySector[index] == null)
        return;
      this.m_objectsBySector[index].Remove(zdo);
    }
    else
    {
      List<ZDO> zdoList;
      if (!this.m_objectsByOutsideSector.TryGetValue(sector, out zdoList))
        return;
      zdoList.Remove(zdo);
    }
  }

  public ZDO GetZDO(ZDOID id)
  {
    if (id == ZDOID.None)
      return (ZDO) null;
    ZDO zdo;
    return this.m_objectsByID.TryGetValue(id, out zdo) ? zdo : (ZDO) null;
  }

  public void AddPeer(ZNetPeer netPeer)
  {
    ZDOMan.ZDOPeer zdoPeer = new ZDOMan.ZDOPeer();
    zdoPeer.m_peer = netPeer;
    this.m_peers.Add(zdoPeer);
    zdoPeer.m_peer.m_rpc.Register<ZPackage>("ZDOData", new Action<ZRpc, ZPackage>(this.RPC_ZDOData));
  }

  public void RemovePeer(ZNetPeer netPeer)
  {
    ZDOMan.ZDOPeer peer = this.FindPeer(netPeer);
    if (peer == null)
      return;
    this.m_peers.Remove(peer);
    if (!ZNet.instance.IsServer())
      return;
    this.RemoveOrphanNonPersistentZDOS();
  }

  private ZDOMan.ZDOPeer FindPeer(ZNetPeer netPeer)
  {
    foreach (ZDOMan.ZDOPeer peer in this.m_peers)
    {
      if (peer.m_peer == netPeer)
        return peer;
    }
    return (ZDOMan.ZDOPeer) null;
  }

  private ZDOMan.ZDOPeer FindPeer(ZRpc rpc)
  {
    foreach (ZDOMan.ZDOPeer peer in this.m_peers)
    {
      if (peer.m_peer.m_rpc == rpc)
        return peer;
    }
    return (ZDOMan.ZDOPeer) null;
  }

  public void Update(float dt)
  {
    if (ZNet.instance.IsServer())
      this.ReleaseZDOS(dt);
    this.SendZDOToPeers2(dt);
    this.SendDestroyed();
    this.UpdateStats(dt);
  }

  private void UpdateStats(float dt)
  {
    this.m_statTimer += dt;
    if ((double) this.m_statTimer < 1.0)
      return;
    this.m_statTimer = 0.0f;
    this.m_zdosSentLastSec = this.m_zdosSent;
    this.m_zdosRecvLastSec = this.m_zdosRecv;
    this.m_zdosRecv = 0;
    this.m_zdosSent = 0;
  }

  // =================================================================
  // CRITICAL PATH: SendZDOToPeers2 -> SendZDOs -> CreateSyncList
  // This is the main ZDO sync loop. Runs every 50ms (20 FPS).
  // Round-robins through peers, sending one peer per frame.
  // =================================================================
  private void SendZDOToPeers2(float dt)
  {
    if (this.m_peers.Count == 0)
      return;
    this.m_sendTimer += dt;
    if (this.m_nextSendPeer < 0)
    {
      if ((double) this.m_sendTimer <= 0.05000000074505806)
        return;
      this.m_nextSendPeer = 0;
      this.m_sendTimer = 0.0f;
    }
    else
    {
      if (this.m_nextSendPeer < this.m_peers.Count)
        this.SendZDOs(this.m_peers[this.m_nextSendPeer], false);
      ++this.m_nextSendPeer;
      if (this.m_nextSendPeer < this.m_peers.Count)
        return;
      this.m_nextSendPeer = -1;
    }
  }

  private void FlushClientObjects()
  {
    foreach (ZDOMan.ZDOPeer peer in this.m_peers)
      this.SendAllZDOs(peer);
  }

  private void ReleaseZDOS(float dt)
  {
    this.m_releaseZDOTimer += dt;
    if ((double) this.m_releaseZDOTimer <= 2.0)
      return;
    this.m_releaseZDOTimer = 0.0f;
    this.ReleaseNearbyZDOS(ZNet.instance.GetReferencePosition(), this.m_sessionID);
    foreach (ZDOMan.ZDOPeer peer in this.m_peers)
      this.ReleaseNearbyZDOS(peer.m_peer.m_refPos, peer.m_peer.m_uid);
  }

  private bool IsInPeerActiveArea(Vector2i sector, long uid)
  {
    if (uid == this.m_sessionID)
      return ZNetScene.InActiveArea(sector, ZNet.instance.GetReferencePosition());
    ZNetPeer peer = ZNet.instance.GetPeer(uid);
    return peer != null && ZNetScene.InActiveArea(sector, peer.GetRefPos());
  }

  private void ReleaseNearbyZDOS(Vector3 refPosition, long uid)
  {
    Vector2i zone = ZoneSystem.GetZone(refPosition);
    this.m_tempNearObjects.Clear();
    this.FindSectorObjects(zone, ZoneSystem.instance.m_activeArea, 0, this.m_tempNearObjects);
    int activatedArea = ZoneSystem.instance.m_activeArea - 1;
    foreach (ZDO tempNearObject in this.m_tempNearObjects)
    {
      if (tempNearObject.Persistent)
      {
        Vector2i sector = tempNearObject.GetSector();
        if (tempNearObject.GetOwner() == uid)
        {
          if (!ZNetScene.InActiveArea(sector, zone, activatedArea))
            tempNearObject.SetOwner(0L);
        }
        else if ((!tempNearObject.HasOwner() || !this.IsInPeerActiveArea(sector, tempNearObject.GetOwner())) && ZNetScene.InActiveArea(sector, zone, activatedArea))
          tempNearObject.SetOwner(uid);
      }
    }
  }

  public void DestroyZDO(ZDO zdo)
  {
    if (!zdo.IsOwner())
      return;
    this.m_destroySendList.Add(zdo.m_uid);
  }

  private void SendDestroyed()
  {
    if (this.m_destroySendList.Count == 0)
      return;
    ZPackage zpackage = new ZPackage();
    zpackage.Write(this.m_destroySendList.Count);
    foreach (ZDOID destroySend in this.m_destroySendList)
      zpackage.Write(destroySend);
    this.m_destroySendList.Clear();
    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "DestroyZDO", (object) zpackage);
  }

  private void RPC_DestroyZDO(long sender, ZPackage pkg)
  {
    int num = pkg.ReadInt();
    for (int index = 0; index < num; ++index)
      this.HandleDestroyedZDO(pkg.ReadZDOID());
  }

  private void HandleDestroyedZDO(ZDOID uid)
  {
    if (uid.UserID == this.m_sessionID && uid.ID >= this.m_nextUid)
      this.m_nextUid = uid.ID + 1U;
    ZDO zdo = this.GetZDO(uid);
    if (zdo == null)
      return;
    if (this.m_onZDODestroyed != null)
      this.m_onZDODestroyed(zdo);
    this.RemoveFromSector(zdo, zdo.GetSector());
    this.m_objectsByID.Remove(zdo.m_uid);
    if (Game.instance.PortalPrefabHash.Contains(zdo.GetPrefab()))
      this.m_portalObjects.Remove(zdo);
    ZDOPool.Release(zdo);
    foreach (ZDOMan.ZDOPeer peer in this.m_peers)
      peer.m_zdos.Remove(uid);
    if (!ZNet.instance.IsServer())
      return;
    long ticks = ZNet.instance.GetTime().Ticks;
    this.m_deadZDOs[uid] = ticks;
  }

  private void SendAllZDOs(ZDOMan.ZDOPeer peer)
  {
    do
      ;
    while (this.SendZDOs(peer, true));
  }

  // =================================================================
  // SendZDOs — THE MAIN SERIALIZATION + SEND METHOD
  //
  // For each ZDO in the sync list:
  // 1. Writes ZDOID, OwnerRevision, DataRevision, Owner, Position
  // 2. Calls zdo.Serialize(pkg) for the full ZDO data
  // 3. Updates PeerZDOInfo with new revision + sync time
  //
  // Key insight: It serializes the ENTIRE ZDO every time, even if
  // only one field changed. This is where delta compression would go.
  //
  // Queue limit: 102400 bytes (100KB) per peer per frame.
  // =================================================================
  private bool SendZDOs(ZDOMan.ZDOPeer peer, bool flush)
  {
    int sendQueueSize = peer.m_peer.m_socket.GetSendQueueSize();
    if (!flush && sendQueueSize > 102400)
      return false;
    int num = 102400 - sendQueueSize;
    if (num < 2048)
      return false;
    this.m_tempToSync.Clear();
    this.CreateSyncList(peer, this.m_tempToSync);
    if (this.m_tempToSync.Count == 0 && peer.m_invalidSector.Count == 0)
      return false;
    ZPackage zpackage = new ZPackage();
    bool flag1 = false;
    if (peer.m_invalidSector.Count > 0)
    {
      flag1 = true;
      zpackage.Write(peer.m_invalidSector.Count);
      foreach (ZDOID id in peer.m_invalidSector)
        zpackage.Write(id);
      peer.m_invalidSector.Clear();
    }
    else
      zpackage.Write(0);
    float time = Time.time;
    ZPackage pkg = new ZPackage();
    bool flag2 = false;
    foreach (ZDO zdo in this.m_tempToSync)
    {
      if (zpackage.Size() <= num)
      {
        peer.m_forceSend.Remove(zdo.m_uid);
        if (!ZNet.instance.IsServer())
          this.m_clientChangeQueue.Remove(zdo.m_uid);
        zpackage.Write(zdo.m_uid);
        zpackage.Write(zdo.OwnerRevision);
        zpackage.Write(zdo.DataRevision);
        zpackage.Write(zdo.GetOwner());
        zpackage.Write(zdo.GetPosition());
        pkg.Clear();
        zdo.Serialize(pkg);
        zpackage.Write(pkg);
        peer.m_zdos[zdo.m_uid] = new ZDOMan.ZDOPeer.PeerZDOInfo(zdo.DataRevision, zdo.OwnerRevision, time);
        flag2 = true;
        ++this.m_zdosSent;
      }
      else
        break;
    }
    zpackage.Write(ZDOID.None);
    if (flag2 | flag1)
      peer.m_peer.m_rpc.Invoke("ZDOData", (object) zpackage);
    return flag2 | flag1;
  }

  // =================================================================
  // RPC_ZDOData — INBOUND ZDO PROCESSING
  //
  // Receives ZDO data from a peer. For each ZDO in the packet:
  // 1. Reads ZDOID, OwnerRevision, DataRevision, Owner, Position
  // 2. Compares revisions — skips if we already have newer data
  // 3. Calls zdo.Deserialize(pkg) to apply the data
  //
  // Key insight for anti-cheat: No validation is done on position,
  // owner, or field values. A client can send anything.
  // =================================================================
  private void RPC_ZDOData(ZRpc rpc, ZPackage pkg)
  {
    ZDOMan.ZDOPeer peer = this.FindPeer(rpc);
    if (peer == null)
    {
      ZLog.Log((object) "ZDO data from unkown host, ignoring");
    }
    else
    {
      float time = Time.time;
      int num1 = 0;
      ZPackage pkg1 = new ZPackage();
      int num2 = pkg.ReadInt();
      for (int index = 0; index < num2; ++index)
        this.GetZDO(pkg.ReadZDOID())?.InvalidateSector();
      while (true)
      {
        ZDOID zdoid;
        ZDO zdo;
        bool flag;
        do
        {
          zdoid = pkg.ReadZDOID();
          if (!zdoid.IsNone())
          {
            ++num1;
            ushort ownerRevision = pkg.ReadUShort();
            uint dataRevision = pkg.ReadUInt();
            long uid = pkg.ReadLong();
            Vector3 vector3 = pkg.ReadVector3();
            pkg.ReadPackage(ref pkg1);
            zdo = this.GetZDO(zdoid);
            flag = false;
            if (zdo != null)
            {
              if (dataRevision <= zdo.DataRevision)
              {
                if ((int) ownerRevision > (int) zdo.OwnerRevision)
                {
                  zdo.SetOwnerInternal(uid);
                  zdo.OwnerRevision = ownerRevision;
                  peer.m_zdos[zdoid] = new ZDOMan.ZDOPeer.PeerZDOInfo(dataRevision, ownerRevision, time);
                  continue;
                }
                continue;
              }
            }
            else
            {
              zdo = this.CreateNewZDO(zdoid, vector3);
              flag = true;
            }
            zdo.OwnerRevision = ownerRevision;
            zdo.DataRevision = dataRevision;
            zdo.SetOwnerInternal(uid);
            zdo.InternalSetPosition(vector3);
            peer.m_zdos[zdoid] = new ZDOMan.ZDOPeer.PeerZDOInfo(zdo.DataRevision, zdo.OwnerRevision, time);
            zdo.Deserialize(pkg1);
            if (Game.instance.PortalPrefabHash.Contains(zdo.GetPrefab()))
              this.AddPortal(zdo);
          }
          else
            goto label_17;
        }
        while (!ZNet.instance.IsServer() || !flag || !this.m_deadZDOs.ContainsKey(zdoid));
        zdo.SetOwner(this.m_sessionID);
        this.DestroyZDO(zdo);
      }
label_17:
      this.m_zdosRecv += num1;
    }
  }

  public void FindSectorObjects(
    Vector2i sector,
    int area,
    int distantArea,
    List<ZDO> sectorObjects,
    List<ZDO> distantSectorObjects = null)
  {
    this.FindObjects(sector, sectorObjects);
    for (int index = 1; index <= area; ++index)
    {
      for (int _x = sector.x - index; _x <= sector.x + index; ++_x)
      {
        this.FindObjects(new Vector2i(_x, sector.y - index), sectorObjects);
        this.FindObjects(new Vector2i(_x, sector.y + index), sectorObjects);
      }
      for (int _y = sector.y - index + 1; _y <= sector.y + index - 1; ++_y)
      {
        this.FindObjects(new Vector2i(sector.x - index, _y), sectorObjects);
        this.FindObjects(new Vector2i(sector.x + index, _y), sectorObjects);
      }
    }
    List<ZDO> objects = distantSectorObjects ?? sectorObjects;
    for (int index = area + 1; index <= area + distantArea; ++index)
    {
      for (int _x = sector.x - index; _x <= sector.x + index; ++_x)
      {
        this.FindDistantObjects(new Vector2i(_x, sector.y - index), objects);
        this.FindDistantObjects(new Vector2i(_x, sector.y + index), objects);
      }
      for (int _y = sector.y - index + 1; _y <= sector.y + index - 1; ++_y)
      {
        this.FindDistantObjects(new Vector2i(sector.x - index, _y), objects);
        this.FindDistantObjects(new Vector2i(sector.x + index, _y), objects);
      }
    }
  }

  // =================================================================
  // CreateSyncList — DECIDES WHICH ZDOs TO SYNC TO A PEER
  //
  // Server path: Find all ZDOs in peer's active sectors, filter by
  // ShouldSend (revision check), sort by priority, add force-sends.
  //
  // Client path: Iterate m_clientChangeQueue (locally modified ZDOs).
  // =================================================================
  private void CreateSyncList(ZDOMan.ZDOPeer peer, List<ZDO> toSync)
  {
    if (ZNet.instance.IsServer())
    {
      Vector3 refPos = peer.m_peer.GetRefPos();
      Vector2i zone = ZoneSystem.GetZone(refPos);
      this.m_tempSectorObjects.Clear();
      this.m_tempToSyncDistant.Clear();
      this.FindSectorObjects(zone, ZoneSystem.instance.m_activeArea, ZoneSystem.instance.m_activeDistantArea, this.m_tempSectorObjects, this.m_tempToSyncDistant);
      foreach (ZDO tempSectorObject in this.m_tempSectorObjects)
      {
        if (peer.ShouldSend(tempSectorObject))
          toSync.Add(tempSectorObject);
      }
      this.ServerSortSendZDOS(toSync, refPos, peer);
      if (toSync.Count < 10)
      {
        foreach (ZDO zdo in this.m_tempToSyncDistant)
        {
          if (peer.ShouldSend(zdo))
            toSync.Add(zdo);
        }
      }
      this.AddForceSendZdos(peer, toSync);
    }
    else
    {
      this.m_tempRemoveList.Clear();
      foreach (ZDOID clientChange in this.m_clientChangeQueue)
      {
        ZDO zdo = this.GetZDO(clientChange);
        if (zdo != null && peer.ShouldSend(zdo))
          toSync.Add(zdo);
        else
          this.m_tempRemoveList.Add(clientChange);
      }
      foreach (ZDOID tempRemove in this.m_tempRemoveList)
        this.m_clientChangeQueue.Remove(tempRemove);
      this.ClientSortSendZDOS(toSync, peer);
      this.AddForceSendZdos(peer, toSync);
    }
  }

  private void AddForceSendZdos(ZDOMan.ZDOPeer peer, List<ZDO> syncList)
  {
    if (peer.m_forceSend.Count <= 0)
      return;
    this.m_tempRemoveList.Clear();
    foreach (ZDOID id in peer.m_forceSend)
    {
      ZDO zdo = this.GetZDO(id);
      if (zdo != null && peer.ShouldSend(zdo))
        syncList.Insert(0, zdo);
      else
        this.m_tempRemoveList.Add(id);
    }
    foreach (ZDOID tempRemove in this.m_tempRemoveList)
      peer.m_forceSend.Remove(tempRemove);
  }

  private static int ServerSendCompare(ZDO x, ZDO y)
  {
    bool flag1 = x.Type == ZDO.ObjectType.Prioritized && x.HasOwner() && x.GetOwner() != ZDOMan.s_compareReceiver;
    bool flag2 = y.Type == ZDO.ObjectType.Prioritized && y.HasOwner() && y.GetOwner() != ZDOMan.s_compareReceiver;
    if (flag1 & flag2)
      return Utils.CompareFloats(x.m_tempSortValue, y.m_tempSortValue);
    return flag1 != flag2 ? (!flag1 ? 1 : -1) : (x.Type == y.Type ? Utils.CompareFloats(x.m_tempSortValue, y.m_tempSortValue) : ((int) y.Type).CompareTo((int) x.Type));
  }

  // =================================================================
  // ServerSortSendZDOS — PRIORITY SORTING
  //
  // tempSortValue = distance - (timeSinceLastSync * 1.5)
  // Lower = higher priority. Objects not synced recently get boosted.
  // Prioritized + owned-by-other-peer sorted first.
  // =================================================================
  private void ServerSortSendZDOS(List<ZDO> objects, Vector3 refPos, ZDOMan.ZDOPeer peer)
  {
    float time = Time.time;
    foreach (ZDO zdo in objects)
    {
      Vector3 position = zdo.GetPosition();
      zdo.m_tempSortValue = Vector3.Distance(position, refPos);
      float num = 100f;
      ZDOMan.ZDOPeer.PeerZDOInfo peerZdoInfo;
      if (peer.m_zdos.TryGetValue(zdo.m_uid, out peerZdoInfo))
        num = Mathf.Clamp(time - peerZdoInfo.m_syncTime, 0.0f, 100f);
      zdo.m_tempSortValue -= num * 1.5f;
    }
    ZDOMan.s_compareReceiver = peer.m_peer.m_uid;
    objects.Sort(new Comparison<ZDO>(ZDOMan.ServerSendCompare));
  }

  private static int ClientSendCompare(ZDO x, ZDO y)
  {
    if (x.Type == y.Type)
      return Utils.CompareFloats(x.m_tempSortValue, y.m_tempSortValue);
    if (x.Type == ZDO.ObjectType.Prioritized)
      return -1;
    return y.Type == ZDO.ObjectType.Prioritized ? 1 : Utils.CompareFloats(x.m_tempSortValue, y.m_tempSortValue);
  }

  private void ClientSortSendZDOS(List<ZDO> objects, ZDOMan.ZDOPeer peer)
  {
    float time = Time.time;
    foreach (ZDO zdo in objects)
    {
      zdo.m_tempSortValue = 0.0f;
      float num = 100f;
      ZDOMan.ZDOPeer.PeerZDOInfo peerZdoInfo;
      if (peer.m_zdos.TryGetValue(zdo.m_uid, out peerZdoInfo))
        num = Mathf.Clamp(time - peerZdoInfo.m_syncTime, 0.0f, 100f);
      zdo.m_tempSortValue -= num * 1.5f;
    }
    objects.Sort(new Comparison<ZDO>(ZDOMan.ClientSendCompare));
  }

  public static long GetSessionID() => ZDOMan.s_instance.m_sessionID;

  private int SectorToIndex(Vector2i s)
  {
    int num1 = s.x + this.m_halfWidth;
    int num2 = s.y + this.m_halfWidth;
    return num1 < 0 || num2 < 0 || num1 >= this.m_width || num2 >= this.m_width ? -1 : num2 * this.m_width + num1;
  }

  private void FindObjects(Vector2i sector, List<ZDO> objects)
  {
    int index = this.SectorToIndex(sector);
    if (index >= 0)
    {
      if (this.m_objectsBySector[index] == null)
        return;
      objects.AddRange((IEnumerable<ZDO>) this.m_objectsBySector[index]);
    }
    else
    {
      List<ZDO> collection;
      if (!this.m_objectsByOutsideSector.TryGetValue(sector, out collection))
        return;
      objects.AddRange((IEnumerable<ZDO>) collection);
    }
  }

  private void FindDistantObjects(Vector2i sector, List<ZDO> objects)
  {
    int index = this.SectorToIndex(sector);
    if (index >= 0)
    {
      List<ZDO> zdoList = this.m_objectsBySector[index];
      if (zdoList == null)
        return;
      foreach (ZDO zdo in zdoList)
      {
        if (zdo.Distant)
          objects.Add(zdo);
      }
    }
    else
    {
      List<ZDO> zdoList;
      if (!this.m_objectsByOutsideSector.TryGetValue(sector, out zdoList))
        return;
      foreach (ZDO zdo in zdoList)
      {
        if (zdo.Distant)
          objects.Add(zdo);
      }
    }
  }

  private void RemoveOrphanNonPersistentZDOS()
  {
    foreach (KeyValuePair<ZDOID, ZDO> keyValuePair in this.m_objectsByID)
    {
      ZDO zdo = keyValuePair.Value;
      if (!zdo.Persistent && (!zdo.HasOwner() || !this.IsPeerConnected(zdo.GetOwner())))
      {
        ZLog.Log((object) $"Destroying abandoned non persistent zdo {zdo.m_uid.ToString()} owner {zdo.GetOwner().ToString()}");
        zdo.SetOwner(this.m_sessionID);
        this.DestroyZDO(zdo);
      }
    }
  }

  private bool IsPeerConnected(long uid)
  {
    if (this.m_sessionID == uid)
      return true;
    foreach (ZDOMan.ZDOPeer peer in this.m_peers)
    {
      if (peer.m_peer.m_uid == uid)
        return true;
    }
    return false;
  }

  private static bool InvalidZDO(ZDO zdo) => !zdo.IsValid();

  public bool GetAllZDOsWithPrefabIterative(string prefab, List<ZDO> zdos, ref int index)
  {
    int stableHashCode = prefab.GetStableHashCode();
    if (index >= this.m_objectsBySector.Length)
    {
      foreach (List<ZDO> zdoList in this.m_objectsByOutsideSector.Values)
      {
        foreach (ZDO zdo in zdoList)
        {
          if (zdo.GetPrefab() == stableHashCode)
            zdos.Add(zdo);
        }
      }
      zdos.RemoveAll(new Predicate<ZDO>(ZDOMan.InvalidZDO));
      return true;
    }
    int num = 0;
    while (index < this.m_objectsBySector.Length)
    {
      List<ZDO> zdoList = this.m_objectsBySector[index];
      if (zdoList != null)
      {
        foreach (ZDO zdo in zdoList)
        {
          if (zdo.GetPrefab() == stableHashCode)
            zdos.Add(zdo);
        }
        ++num;
        if (num > 400)
          break;
      }
      ++index;
    }
    return false;
  }

  private List<ZDO> GetSaveClone()
  {
    List<ZDO> saveClone = new List<ZDO>();
    for (int index = 0; index < this.m_objectsBySector.Length; ++index)
    {
      if (this.m_objectsBySector[index] != null)
      {
        foreach (ZDO zdo in this.m_objectsBySector[index])
        {
          if (zdo.Persistent)
            saveClone.Add(zdo.Clone());
        }
      }
    }
    foreach (List<ZDO> zdoList in this.m_objectsByOutsideSector.Values)
    {
      foreach (ZDO zdo in zdoList)
      {
        if (zdo.Persistent)
          saveClone.Add(zdo.Clone());
      }
    }
    return saveClone;
  }

  public List<ZDO> GetPortals() => this.m_portalObjects;

  public int NrOfObjects() => this.m_objectsByID.Count;

  public int GetSentZDOs() => this.m_zdosSentLastSec;

  public int GetRecvZDOs() => this.m_zdosRecvLastSec;

  public int GetClientChangeQueue() => this.m_clientChangeQueue.Count;

  public void GetAverageStats(out float sentZdos, out float recvZdos)
  {
    sentZdos = (float) this.m_zdosSentLastSec / 20f;
    recvZdos = (float) this.m_zdosRecvLastSec / 20f;
  }

  public void RequestZDO(ZDOID id)
  {
    ZRoutedRpc.instance.InvokeRoutedRPC(nameof (RequestZDO), (object) id);
  }

  private void RPC_RequestZDO(long sender, ZDOID id) => this.GetPeer(sender)?.ForceSendZDO(id);

  private ZDOMan.ZDOPeer GetPeer(long uid)
  {
    foreach (ZDOMan.ZDOPeer peer in this.m_peers)
    {
      if (peer.m_peer.m_uid == uid)
        return peer;
    }
    return (ZDOMan.ZDOPeer) null;
  }

  public void ForceSendZDO(ZDOID id)
  {
    foreach (ZDOMan.ZDOPeer peer in this.m_peers)
      peer.ForceSendZDO(id);
  }

  public void ForceSendZDO(long peerID, ZDOID id)
  {
    if (ZNet.instance.IsServer())
    {
      this.GetPeer(peerID)?.ForceSendZDO(id);
    }
    else
    {
      foreach (ZDOMan.ZDOPeer peer in this.m_peers)
        peer.ForceSendZDO(id);
    }
  }

  public void ClientChanged(ZDOID id) => this.m_clientChangeQueue.Add(id);

  // === Inner Classes ===

  private class ZDOPeer
  {
    public ZNetPeer m_peer;
    public readonly Dictionary<ZDOID, ZDOMan.ZDOPeer.PeerZDOInfo> m_zdos = new Dictionary<ZDOID, ZDOMan.ZDOPeer.PeerZDOInfo>();
    public readonly HashSet<ZDOID> m_forceSend = new HashSet<ZDOID>();
    public readonly HashSet<ZDOID> m_invalidSector = new HashSet<ZDOID>();
    public int m_sendIndex;

    public void ZDOSectorInvalidated(ZDO zdo)
    {
      if (zdo.GetOwner() == this.m_peer.m_uid || !this.m_zdos.ContainsKey(zdo.m_uid) || ZNetScene.InActiveArea(zdo.GetSector(), this.m_peer.GetRefPos()))
        return;
      this.m_invalidSector.Add(zdo.m_uid);
      this.m_zdos.Remove(zdo.m_uid);
    }

    public void ForceSendZDO(ZDOID id) => this.m_forceSend.Add(id);

    public bool ShouldSend(ZDO zdo)
    {
      ZDOMan.ZDOPeer.PeerZDOInfo peerZdoInfo;
      return !this.m_zdos.TryGetValue(zdo.m_uid, out peerZdoInfo) || (int) zdo.OwnerRevision > (int) peerZdoInfo.m_ownerRevision || zdo.DataRevision > peerZdoInfo.m_dataRevision;
    }

    public struct PeerZDOInfo(uint dataRevision, ushort ownerRevision, float syncTime)
    {
      public readonly uint m_dataRevision = dataRevision;
      public readonly ushort m_ownerRevision = ownerRevision;
      public readonly float m_syncTime = syncTime;
    }
  }

  private class SaveData
  {
    public long m_sessionID;
    public uint m_nextUid = 1;
    public List<ZDO> m_zdos;
  }
}
```
