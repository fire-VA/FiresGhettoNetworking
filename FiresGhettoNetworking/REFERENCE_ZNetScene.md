# ZNetScene — Decompiled Reference

Decompiled from `assembly_valheim.dll` using JetBrains decompiler.
This file is for reference only — not compiled into the mod.

---

## Key Fields

- `m_instances` — `Dictionary<ZDO, ZNetView>` — live instantiated objects
- `m_namedPrefabs` — `Dictionary<int, GameObject>` — prefab registry (hash ? prefab)
- `m_prefabs` — `List<GameObject>` — all registered prefabs
- `m_tempCurrentObjects` / `m_tempCurrentDistantObjects` — reusable lists for create/destroy

## Key Insights

### CreateDestroyObjects — The main object lifecycle loop

Called every frame (~30fps). Gets ZDOs in active sectors, creates
GameObjects for new ZDOs, destroys GameObjects for ZDOs no longer
in active area. Our `ServerAuthorityPatches.CreateDestroyObjects_Prefix`
replaces this on dedicated servers.

### CreateObject — Where object pooling would go

Currently: `Object.Instantiate<GameObject>(prefab, position, rotation)`
every time a ZDO enters view. Pooling would reuse deactivated objects
from a pool instead.

### RemoveObjects — Earmark-based removal

Uses `TempRemoveEarmark` (frame counter byte) to mark objects that
should still exist. Any instance without the current earmark gets
destroyed. Clever — avoids set operations.

### CreateObjectsSorted — Sorted by distance

Objects closer to player are created first. Max 10 per frame (100 during
loading screen). Uses `m_tempSortValue` for distance sorting.

---

## Full Decompiled Source

```csharp
// Decompiled with JetBrains decompiler
// Type: ZNetScene
// Assembly: assembly_valheim, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null

using System;
using System.Collections.Generic;
using UnityEngine;

#nullable disable
public class ZNetScene : MonoBehaviour
{
  private static ZNetScene s_instance;
  private const int m_maxCreatedPerFrame = 10;
  private const float m_createDestroyFps = 30f;
  public List<GameObject> m_prefabs = new List<GameObject>();
  public List<GameObject> m_nonNetViewPrefabs = new List<GameObject>();
  private readonly Dictionary<int, GameObject> m_namedPrefabs = new Dictionary<int, GameObject>();
  private readonly Dictionary<ZDO, ZNetView> m_instances = new Dictionary<ZDO, ZNetView>();
  private readonly List<ZDO> m_tempCurrentObjects = new List<ZDO>();
  private readonly List<ZDO> m_tempCurrentObjects2 = new List<ZDO>();
  private readonly List<ZDO> m_tempCurrentDistantObjects = new List<ZDO>();
  private readonly List<ZNetView> m_tempRemoved = new List<ZNetView>();
  private float m_createDestroyTimer;

  public static ZNetScene instance => ZNetScene.s_instance;

  private void Awake()
  {
    ZNetScene.s_instance = this;
    foreach (GameObject prefab in this.m_prefabs)
      this.m_namedPrefabs.Add(prefab.name.GetStableHashCode(), prefab);
    foreach (GameObject nonNetViewPrefab in this.m_nonNetViewPrefabs)
      this.m_namedPrefabs.Add(nonNetViewPrefab.name.GetStableHashCode(), nonNetViewPrefab);
    ZDOMan.instance.m_onZDODestroyed += new Action<ZDO>(this.OnZDODestroyed);
    ZRoutedRpc.instance.Register<Vector3, Quaternion, int>("SpawnObject", new Action<long, Vector3, Quaternion, int>(this.RPC_SpawnObject));
  }

  private void OnDestroy()
  {
    ZLog.Log((object) "Net scene destroyed");
    if (!((UnityEngine.Object) ZNetScene.s_instance == (UnityEngine.Object) this))
      return;
    ZNetScene.s_instance = (ZNetScene) null;
  }

  public void Shutdown()
  {
    foreach (KeyValuePair<ZDO, ZNetView> instance in this.m_instances)
    {
      if ((bool) (UnityEngine.Object) instance.Value)
      {
        instance.Value.ResetZDO();
        UnityEngine.Object.Destroy((UnityEngine.Object) instance.Value.gameObject);
      }
    }
    this.m_instances.Clear();
    this.enabled = false;
  }

  public void AddInstance(ZDO zdo, ZNetView nview)
  {
    zdo.Created = true;
    this.m_instances[zdo] = nview;
  }

  private bool IsPrefabZDOValid(ZDO zdo)
  {
    int prefab = zdo.GetPrefab();
    return prefab != 0 && (UnityEngine.Object) this.GetPrefab(prefab) != (UnityEngine.Object) null;
  }

  // =================================================================
  // CreateObject — INSTANTIATES A GAMEOBJECT FROM A ZDO
  //
  // This is where object pooling would intercept:
  // Instead of Object.Instantiate, check pool for deactivated instance.
  // =================================================================
  private GameObject CreateObject(ZDO zdo)
  {
    int prefab1 = zdo.GetPrefab();
    if (prefab1 == 0)
      return (GameObject) null;
    GameObject prefab2 = this.GetPrefab(prefab1);
    if ((UnityEngine.Object) prefab2 == (UnityEngine.Object) null)
      return (GameObject) null;
    Vector3 position = zdo.GetPosition();
    Quaternion rotation = zdo.GetRotation();
    ZNetView.m_useInitZDO = true;
    ZNetView.m_initZDO = zdo;
    GameObject gameObject = UnityEngine.Object.Instantiate<GameObject>(prefab2, position, rotation);
    if (ZNetView.m_initZDO != null)
    {
      ZLog.LogWarning((object) $"ZDO {zdo.m_uid.ToString()} not used when creating object {prefab2.name}");
      ZNetView.m_initZDO = (ZDO) null;
    }
    ZNetView.m_useInitZDO = false;
    return gameObject;
  }

  public void Destroy(GameObject go)
  {
    ZNetView component = go.GetComponent<ZNetView>();
    if ((bool) (UnityEngine.Object) component && component.GetZDO() != null)
    {
      ZDO zdo = component.GetZDO();
      component.ResetZDO();
      this.m_instances.Remove(zdo);
      if (zdo.IsOwner())
        ZDOMan.instance.DestroyZDO(zdo);
    }
    UnityEngine.Object.Destroy((UnityEngine.Object) go);
  }

  public bool HasPrefab(int hash) => this.m_namedPrefabs.ContainsKey(hash);

  public GameObject GetPrefab(int hash)
  {
    GameObject gameObject;
    return this.m_namedPrefabs.TryGetValue(hash, out gameObject) ? gameObject : (GameObject) null;
  }

  public GameObject GetPrefab(string name) => this.GetPrefab(name.GetStableHashCode());

  public int GetPrefabHash(GameObject go) => go.name.GetStableHashCode();

  public bool IsAreaReady(Vector3 point)
  {
    Vector2i zone = ZoneSystem.GetZone(point);
    if (!ZoneSystem.instance.IsZoneLoaded(zone))
      return false;
    this.m_tempCurrentObjects.Clear();
    ZDOMan.instance.FindSectorObjects(zone, 1, 0, this.m_tempCurrentObjects);
    foreach (ZDO tempCurrentObject in this.m_tempCurrentObjects)
    {
      if (this.IsPrefabZDOValid(tempCurrentObject) && !(bool) (UnityEngine.Object) this.FindInstance(tempCurrentObject))
        return false;
    }
    return true;
  }

  private bool InLoadingScreen()
  {
    return (UnityEngine.Object) Player.m_localPlayer == (UnityEngine.Object) null || Player.m_localPlayer.IsTeleporting();
  }

  // =================================================================
  // CreateObjects — BATCH CREATION (we call this from our prefix)
  // =================================================================
  private void CreateObjects(List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
  {
    int maxCreatedPerFrame = 10;
    if (this.InLoadingScreen())
      maxCreatedPerFrame = 100;
    int created = 0;
    this.CreateObjectsSorted(currentNearObjects, maxCreatedPerFrame, ref created);
    this.CreateDistantObjects(currentDistantObjects, maxCreatedPerFrame, ref created);
  }

  private void CreateObjectsSorted(
    List<ZDO> currentNearObjects,
    int maxCreatedPerFrame,
    ref int created)
  {
    if (!ZoneSystem.instance.IsActiveAreaLoaded())
      return;
    this.m_tempCurrentObjects2.Clear();
    Vector3 referencePosition = ZNet.instance.GetReferencePosition();
    foreach (ZDO currentNearObject in currentNearObjects)
    {
      if (!currentNearObject.Created)
      {
        currentNearObject.m_tempSortValue = Utils.DistanceSqr(referencePosition, currentNearObject.GetPosition());
        this.m_tempCurrentObjects2.Add(currentNearObject);
      }
    }
    int num = Mathf.Max(this.m_tempCurrentObjects2.Count / 100, maxCreatedPerFrame);
    this.m_tempCurrentObjects2.Sort(new Comparison<ZDO>(ZNetScene.ZDOCompare));
    foreach (ZDO zdo in this.m_tempCurrentObjects2)
    {
      if (ZoneSystem.instance.IsZoneReadyForType(zdo.GetSector(), zdo.Type))
      {
        if ((UnityEngine.Object) this.CreateObject(zdo) != (UnityEngine.Object) null)
        {
          ++created;
          if (created > num)
            break;
        }
        else if (ZNet.instance.IsServer())
        {
          zdo.SetOwner(ZDOMan.GetSessionID());
          ZLog.Log((object) ("Destroyed invalid predab ZDO:" + zdo.m_uid.ToString()));
          ZDOMan.instance.DestroyZDO(zdo);
        }
      }
    }
  }

  private static int ZDOCompare(ZDO x, ZDO y)
  {
    return x.Type == y.Type ? Utils.CompareFloats(x.m_tempSortValue, y.m_tempSortValue) : ((int) y.Type).CompareTo((int) x.Type);
  }

  private void CreateDistantObjects(List<ZDO> objects, int maxCreatedPerFrame, ref int created)
  {
    if (created > maxCreatedPerFrame)
      return;
    foreach (ZDO zdo in objects)
    {
      if (!zdo.Created)
      {
        if ((UnityEngine.Object) this.CreateObject(zdo) != (UnityEngine.Object) null)
        {
          ++created;
          if (created > maxCreatedPerFrame)
            break;
        }
        else if (ZNet.instance.IsServer())
        {
          zdo.SetOwner(ZDOMan.GetSessionID());
          ZLog.Log((object) $"Destroyed invalid predab ZDO:{zdo.m_uid.ToString()}  prefab hash:{zdo.GetPrefab().ToString()}");
          ZDOMan.instance.DestroyZDO(zdo);
        }
      }
    }
  }

  private void OnZDODestroyed(ZDO zdo)
  {
    ZNetView znetView;
    if (!this.m_instances.TryGetValue(zdo, out znetView))
      return;
    znetView.ResetZDO();
    UnityEngine.Object.Destroy((UnityEngine.Object) znetView.gameObject);
    this.m_instances.Remove(zdo);
  }

  // =================================================================
  // RemoveObjects — EARMARK-BASED REMOVAL (we call this from our prefix)
  //
  // Uses TempRemoveEarmark (frame counter byte) to mark objects that
  // should still exist. Anything without the current earmark gets destroyed.
  // =================================================================
  private void RemoveObjects(List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
  {
    byte num = (byte) (Time.frameCount & (int) byte.MaxValue);
    foreach (ZDO currentNearObject in currentNearObjects)
      currentNearObject.TempRemoveEarmark = num;
    foreach (ZDO currentDistantObject in currentDistantObjects)
      currentDistantObject.TempRemoveEarmark = num;
    this.m_tempRemoved.Clear();
    foreach (ZNetView znetView in this.m_instances.Values)
    {
      if ((int) znetView.GetZDO().TempRemoveEarmark != (int) num)
        this.m_tempRemoved.Add(znetView);
    }
    for (int index = 0; index < this.m_tempRemoved.Count; ++index)
    {
      ZNetView znetView = this.m_tempRemoved[index];
      ZDO zdo = znetView.GetZDO();
      znetView.ResetZDO();
      UnityEngine.Object.Destroy((UnityEngine.Object) znetView.gameObject);
      if (!zdo.Persistent && zdo.IsOwner())
        ZDOMan.instance.DestroyZDO(zdo);
      this.m_instances.Remove(zdo);
    }
  }

  public ZNetView FindInstance(ZDO zdo)
  {
    ZNetView znetView;
    return this.m_instances.TryGetValue(zdo, out znetView) ? znetView : (ZNetView) null;
  }

  public bool HaveInstance(ZDO zdo) => this.m_instances.ContainsKey(zdo);

  public GameObject FindInstance(ZDOID id)
  {
    ZDO zdo = ZDOMan.instance.GetZDO(id);
    if (zdo != null)
    {
      ZNetView instance = this.FindInstance(zdo);
      if ((bool) (UnityEngine.Object) instance)
        return instance.gameObject;
    }
    return (GameObject) null;
  }

  private void Update()
  {
    this.m_createDestroyTimer += Time.deltaTime;
    if ((double) this.m_createDestroyTimer < 0.033333335071802139)
      return;
    this.m_createDestroyTimer = 0.0f;
    this.CreateDestroyObjects();
  }

  // =================================================================
  // CreateDestroyObjects — MAIN LOOP (we prefix this on server)
  // =================================================================
  private void CreateDestroyObjects()
  {
    Vector2i zone = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
    this.m_tempCurrentObjects.Clear();
    this.m_tempCurrentDistantObjects.Clear();
    ZDOMan.instance.FindSectorObjects(zone, ZoneSystem.instance.m_activeArea, ZoneSystem.instance.m_activeDistantArea, this.m_tempCurrentObjects, this.m_tempCurrentDistantObjects);
    this.CreateObjects(this.m_tempCurrentObjects, this.m_tempCurrentDistantObjects);
    this.RemoveObjects(this.m_tempCurrentObjects, this.m_tempCurrentDistantObjects);
  }

  public static bool InActiveArea(Vector2i zone, Vector3 refPoint)
  {
    Vector2i zone1 = ZoneSystem.GetZone(refPoint);
    return ZNetScene.InActiveArea(zone, zone1);
  }

  public static bool InActiveArea(Vector2i zone, Vector2i refCenterZone)
  {
    int num = ZoneSystem.instance.m_activeArea - 1;
    return zone.x >= refCenterZone.x - num && zone.x <= refCenterZone.x + num && zone.y <= refCenterZone.y + num && zone.y >= refCenterZone.y - num;
  }

  public static bool InActiveArea(Vector2i zone, Vector2i refCenterZone, int activatedArea)
  {
    return zone.x >= refCenterZone.x - activatedArea && zone.x <= refCenterZone.x + activatedArea && zone.y <= refCenterZone.y + activatedArea && zone.y >= refCenterZone.y - activatedArea;
  }

  public bool OutsideActiveArea(Vector3 point)
  {
    return ZNetScene.OutsideActiveArea(point, ZNet.instance.GetReferencePosition());
  }

  private static bool OutsideActiveArea(Vector3 point, Vector3 refPoint)
  {
    Vector2i zone1 = ZoneSystem.GetZone(refPoint);
    Vector2i zone2 = ZoneSystem.GetZone(point);
    return zone2.x <= zone1.x - ZoneSystem.instance.m_activeArea || zone2.x >= zone1.x + ZoneSystem.instance.m_activeArea || zone2.y >= zone1.y + ZoneSystem.instance.m_activeArea || zone2.y <= zone1.y - ZoneSystem.instance.m_activeArea;
  }

  public static bool OutsideActiveArea(Vector3 point, Vector2i centerZone, int activeArea)
  {
    Vector2i zone = ZoneSystem.GetZone(point);
    return zone.x <= centerZone.x - activeArea || zone.x >= centerZone.x + activeArea || zone.y >= centerZone.y + activeArea || zone.y <= centerZone.y - activeArea;
  }

  public bool HaveInstanceInSector(Vector2i sector)
  {
    foreach (KeyValuePair<ZDO, ZNetView> instance in this.m_instances)
    {
      if ((bool) (UnityEngine.Object) instance.Value && !instance.Value.m_distant && ZoneSystem.GetZone(instance.Value.transform.position) == sector)
        return true;
    }
    return false;
  }

  public int NrOfInstances() => this.m_instances.Count;

  public void SpawnObject(Vector3 pos, Quaternion rot, GameObject prefab)
  {
    int prefabHash = this.GetPrefabHash(prefab);
    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, nameof (SpawnObject), (object) pos, (object) rot, (object) prefabHash);
  }

  public List<string> GetPrefabNames()
  {
    List<string> prefabNames = new List<string>();
    foreach (KeyValuePair<int, GameObject> namedPrefab in this.m_namedPrefabs)
      prefabNames.Add(namedPrefab.Value.name);
    return prefabNames;
  }

  private void RPC_SpawnObject(long spawner, Vector3 pos, Quaternion rot, int prefabHash)
  {
    GameObject prefab = this.GetPrefab(prefabHash);
    if ((UnityEngine.Object) prefab == (UnityEngine.Object) null)
      ZLog.Log((object) ("Missing prefab " + prefabHash.ToString()));
    else
      UnityEngine.Object.Instantiate<GameObject>(prefab, pos, rot);
  }
}
```
