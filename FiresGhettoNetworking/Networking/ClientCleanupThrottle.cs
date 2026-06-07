using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [HarmonyPatch]
    public static class ClientCleanupThrottle
    {
        private static readonly HashSet<ZNetView> _pendingDestroySet = new HashSet<ZNetView>();
        private static readonly Queue<ZNetView> _pendingDestroyQueue = new Queue<ZNetView>();
        private static readonly List<ZDO> _zdosToUnregisterScratch = new List<ZDO>(256);

        [HarmonyPatch(typeof(ZNetScene), "RemoveObjects")]
        [HarmonyPrefix]
        public static bool RemoveObjects_ClientThrottle_Prefix(
            ZNetScene __instance,
            List<ZDO> currentNearObjects,
            List<ZDO> currentDistantObjects)
        {
            if (IsDedicatedServer()) return true;

            int maxDestroysPerFrame = FiresGhettoNetworkMod.ConfigClientMaxDestroysPerFrame?.Value ?? 0;
            if (maxDestroysPerFrame <= 0) return true;

            byte frameEarmark = ComputeCurrentFrameEarmark();
            MarkZdosAsStillInArea(currentNearObjects, frameEarmark);
            MarkZdosAsStillInArea(currentDistantObjects, frameEarmark);
            EnqueueOutOfAreaInstancesForDeferredDestroy(__instance, frameEarmark);
            DestroyUpToBudget(__instance, frameEarmark, maxDestroysPerFrame);
            return false;
        }

        private static bool IsDedicatedServer() => ZNet.instance != null && ZNet.instance.IsDedicated();

        private static byte ComputeCurrentFrameEarmark() => (byte)(Time.frameCount & byte.MaxValue);

        private static void MarkZdosAsStillInArea(List<ZDO> zdos, byte mark)
        {
            if (zdos == null) return;
            for (int i = 0; i < zdos.Count; i++)
            {
                var zdo = zdos[i];
                if (zdo != null) zdo.TempRemoveEarmark = mark;
            }
        }

        private static void EnqueueOutOfAreaInstancesForDeferredDestroy(ZNetScene scene, byte frameEarmark)
        {
            foreach (var pair in scene.m_instances)
            {
                var view = pair.Value;
                if (view == null) continue;
                var zdo = view.GetZDO();
                if (zdo == null) continue;
                if (IsStillInActiveArea(zdo, frameEarmark)) continue;
                if (_pendingDestroySet.Add(view))
                    _pendingDestroyQueue.Enqueue(view);
            }
        }

        private static void DestroyUpToBudget(ZNetScene scene, byte frameEarmark, int budget)
        {
            _zdosToUnregisterScratch.Clear();
            int destroyed = 0;

            while (_pendingDestroyQueue.Count > 0 && destroyed < budget)
            {
                var view = _pendingDestroyQueue.Dequeue();
                _pendingDestroySet.Remove(view);
                if (view == null) continue;
                var zdo = view.GetZDO();
                if (zdo == null) continue;
                if (IsStillInActiveArea(zdo, frameEarmark)) continue;

                view.ResetZDO();
                _zdosToUnregisterScratch.Add(zdo);
                if (!zdo.Persistent && zdo.IsOwner())
                    ZDOMan.instance.DestroyZDO(zdo);
                Object.Destroy(view.gameObject);
                destroyed++;
            }

            UnregisterDestroyedFromScene(scene);
        }

        private static bool IsStillInActiveArea(ZDO zdo, byte frameEarmark) =>
            zdo.TempRemoveEarmark == frameEarmark;

        private static void UnregisterDestroyedFromScene(ZNetScene scene)
        {
            for (int i = 0; i < _zdosToUnregisterScratch.Count; i++)
                scene.m_instances.Remove(_zdosToUnregisterScratch[i]);
            _zdosToUnregisterScratch.Clear();
        }
    }
}
