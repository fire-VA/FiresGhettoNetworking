using HarmonyLib;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Spreads ZNetScene.RemoveObjects across frames so leaving a megabase does not freeze the client.
    /// Anything that simulates its own position is still destroyed in-frame, and whatever defers drains
    /// loose before solid, so containment always outlives its contents.
    /// </summary>
    [HarmonyPatch]
    public static class ClientCleanupThrottle
    {
        // Contents drain before containment.
        private static readonly HashSet<ZNetView> _pendingDestroySet = new HashSet<ZNetView>();
        private static readonly Queue<ZNetView> _pendingLooseQueue = new Queue<ZNetView>();
        private static readonly Queue<ZNetView> _pendingSolidQueue = new Queue<ZNetView>();

        private static readonly List<ZDO> _zdosToUnregisterScratch = new List<ZDO>(256);
        private static readonly List<ZNetView> _destroyNowScratch = new List<ZNetView>(64);

        // Exact per-frame in-area membership. Vanilla marks ZDO.TempRemoveEarmark with
        // (frameCount & 255) and compares it within the same frame, which is sound for
        // an atomic pass. Ours spans frames, and an entry sitting in the queue for 256+
        // frames (a 150k drain at 200/frame is ~750) can collide with the current
        // frame's earmark and be mistaken for "came back into the active area", so it
        // is skipped instead of destroyed. Tracking membership ourselves removes the
        // wrap entirely.
        private static readonly HashSet<ZDO> _inAreaThisFrame = new HashSet<ZDO>();

        // Stands down when an earlier prefix already owns the unload pass. Those lists need not be the full
        // in-area set (ValheimCommunityPatch passes empty ones), and trusting them destroys every live instance.
        [HarmonyPatch(typeof(ZNetScene), "RemoveObjects")]
        [HarmonyPrefix]
        public static bool RemoveObjects_ClientThrottle_Prefix(
            ZNetScene __instance,
            List<ZDO> currentNearObjects,
            List<ZDO> currentDistantObjects,
            bool __runOriginal)
        {
            if (!__runOriginal) return false;

            if (ValheimCommunityPatchCompat.SchedulesObjectRemoval) return true;
            if (ServerClientUtils.ZNetIsDedicated()) return true;

            int maxDestroysPerFrame = FiresGhettoNetworkMod.ConfigClientMaxDestroysPerFrame?.Value ?? 0;
            if (maxDestroysPerFrame <= 0) return true;

            RebuildInAreaSet(currentNearObjects, currentDistantObjects);
            ScanInstances(__instance);
            DestroyUpToBudget(__instance, maxDestroysPerFrame);
            return false;
        }

        // Static state outlives a world session, so a disconnect/reconnect would
        // otherwise carry dead ZNetViews from the previous ZNetScene into the next.
        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        [HarmonyPostfix]
        public static void ZNetScene_Shutdown_ClearPendingState()
        {
            _pendingDestroySet.Clear();
            _pendingLooseQueue.Clear();
            _pendingSolidQueue.Clear();
            _zdosToUnregisterScratch.Clear();
            _destroyNowScratch.Clear();
            _inAreaThisFrame.Clear();
            _sessionMoversDestroyed = 0;
            _sessionStaticDeferred = 0;
        }


        // ---- teardown instrumentation -------------------------------------------------------
        // Without this the fix is invisible: a creature that stays penned proves nothing, because
        // the pre-fix enqueue order was ZNetScene.m_instances dictionary order and would SOMETIMES
        // have drained the walls last anyway. A non-zero mover count is the positive evidence —
        // it names the exact objects that, before this patch, would have been left alive and
        // simulating while their surroundings were destroyed around them.
        private static int _sessionMoversDestroyed;
        private static int _sessionStaticDeferred;
        private static float _nextTeardownLogTime;
        private const float TeardownLogIntervalSec = 5f;

        private static void ReportTeardown(int moverCharacters, int moverBodies, int queuedLoose, int queuedSolid)
        {
            int movers = moverCharacters + moverBodies;
            int deferred = queuedLoose + queuedSolid;
            if (movers == 0 && deferred == 0) return;

            _sessionMoversDestroyed += movers;
            _sessionStaticDeferred += deferred;

            // Only movers are worth surfacing — a pure-static teardown is the boring path and would
            // spam every time a player walks out of a base.
            if (movers == 0 || Time.time < _nextTeardownLogTime) return;
            _nextTeardownLogTime = Time.time + TeardownLogIntervalSec;

            LoggerOptions.LogMessage(
                $"[CleanupThrottle] teardown: {movers} mover(s) destroyed IN-FRAME "
                + $"({moverCharacters} Character, {moverBodies} rigidbody: {NameMovers()}) — these would previously have been queued "
                + $"and left simulating while their surroundings were destroyed. "
                + $"{deferred} static deferred ({queuedSolid} solid last). "
                + $"Session totals: {_sessionMoversDestroyed} movers / {_sessionStaticDeferred} static.");
        }

        private const int MaxNamedMovers = 5;
        private static readonly Dictionary<string, int> _moverCounts = new Dictionary<string, int>();
        private static readonly List<KeyValuePair<string, int>> _moverRanking = new List<KeyValuePair<string, int>>();

        private static string NameMovers()
        {
            _moverCounts.Clear();
            for (int i = 0; i < _destroyNowScratch.Count; i++)
            {
                var view = _destroyNowScratch[i];
                if (view == null) continue;
                string name = PrefabName(view);
                _moverCounts.TryGetValue(name, out int count);
                _moverCounts[name] = count + 1;
            }
            _moverRanking.Clear();
            _moverRanking.AddRange(_moverCounts);
            _moverRanking.Sort((a, b) => b.Value.CompareTo(a.Value));

            var names = new StringBuilder();
            for (int i = 0; i < _moverRanking.Count && i < MaxNamedMovers; i++)
            {
                if (i > 0) names.Append(", ");
                names.Append(_moverRanking[i].Key);
                if (_moverRanking[i].Value > 1) names.Append(" x").Append(_moverRanking[i].Value);
            }
            if (_moverRanking.Count > MaxNamedMovers) names.Append(", +").Append(_moverRanking.Count - MaxNamedMovers).Append(" more");
            return names.ToString();
        }

        private static string PrefabName(ZNetView view)
        {
            var zdo = view.GetZDO();
            var prefab = zdo != null && ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            return prefab != null ? prefab.name : view.gameObject.name;
        }

        private static void RebuildInAreaSet(List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
        {
            _inAreaThisFrame.Clear();
            AddAllToInAreaSet(currentNearObjects);
            AddAllToInAreaSet(currentDistantObjects);
        }

        private static void AddAllToInAreaSet(List<ZDO> zdos)
        {
            if (zdos == null) return;
            for (int i = 0; i < zdos.Count; i++)
            {
                var zdo = zdos[i];
                if (zdo != null) _inAreaThisFrame.Add(zdo);
            }
        }

        private const int MoverNone = 0;
        private const int MoverCharacter = 1;
        private const int MoverRigidbody = 2;

        /// <summary>
        /// Classifies anything that moves under its own simulation and therefore must never be
        /// left alive after the geometry around it has been destroyed. Runs once per instance per
        /// teardown — the caller only reaches it for views newly added to the pending set.
        /// </summary>
        private static int ClassifyMover(ZNetView view)
        {
            if (view.GetComponent<Character>() != null) return MoverCharacter;
            var body = view.GetComponent<Rigidbody>();
            return (body != null && !body.isKinematic) ? MoverRigidbody : MoverNone;
        }

        private static void ScanInstances(ZNetScene scene)
        {
            _destroyNowScratch.Clear();
            int moverCharacters = 0, moverBodies = 0, queuedLoose = 0, queuedSolid = 0;

            foreach (var pair in scene.m_instances)
            {
                var view = pair.Value;
                if (view == null) continue;
                var zdo = view.GetZDO();
                if (zdo == null) continue;
                if (_inAreaThisFrame.Contains(zdo)) continue;
                if (!_pendingDestroySet.Add(view)) continue;

                int mover = ClassifyMover(view);
                if (mover != MoverNone)
                {
                    _destroyNowScratch.Add(view);
                    if (mover == MoverCharacter) moverCharacters++; else moverBodies++;
                }
                else if (zdo.Type == ZDO.ObjectType.Solid || zdo.Type == ZDO.ObjectType.Terrain)
                {
                    _pendingSolidQueue.Enqueue(view);
                    queuedSolid++;
                }
                else
                {
                    _pendingLooseQueue.Enqueue(view);
                    queuedLoose++;
                }
            }

            ReportTeardown(moverCharacters, moverBodies, queuedLoose, queuedSolid);

            // Deferred to here because DestroyInstance removes from scene.m_instances,
            // which cannot be mutated while the loop above enumerates it.
            _zdosToUnregisterScratch.Clear();
            for (int i = 0; i < _destroyNowScratch.Count; i++)
            {
                var view = _destroyNowScratch[i];
                _pendingDestroySet.Remove(view);
                DestroyInstance(view);
            }
            _destroyNowScratch.Clear();
            UnregisterDestroyedFromScene(scene);
        }

        private static void DestroyUpToBudget(ZNetScene scene, int budget)
        {
            _zdosToUnregisterScratch.Clear();
            int destroyed = 0;

            while (destroyed < budget && TryDequeueNextPending(out ZNetView view))
            {
                if (view == null) continue;
                var zdo = view.GetZDO();
                if (zdo == null) continue;
                if (_inAreaThisFrame.Contains(zdo)) continue;

                DestroyInstance(view);
                destroyed++;
            }

            UnregisterDestroyedFromScene(scene);
        }

        // Loose contents before solid containment — see rule 2 in the class header.
        private static bool TryDequeueNextPending(out ZNetView view)
        {
            if (_pendingLooseQueue.Count > 0)
            {
                view = _pendingLooseQueue.Dequeue();
                _pendingDestroySet.Remove(view);
                return true;
            }
            if (_pendingSolidQueue.Count > 0)
            {
                view = _pendingSolidQueue.Dequeue();
                _pendingDestroySet.Remove(view);
                return true;
            }
            view = null;
            return false;
        }

        private static void DestroyInstance(ZNetView view)
        {
            if (view == null) return;
            var zdo = view.GetZDO();
            if (zdo == null) return;

            view.ResetZDO();
            _zdosToUnregisterScratch.Add(zdo);
            if (!zdo.Persistent && zdo.IsOwner())
                ZDOMan.instance.DestroyZDO(zdo);
            Object.Destroy(view.gameObject);
        }

        private static void UnregisterDestroyedFromScene(ZNetScene scene)
        {
            for (int i = 0; i < _zdosToUnregisterScratch.Count; i++)
                scene.m_instances.Remove(_zdosToUnregisterScratch[i]);
            _zdosToUnregisterScratch.Clear();
        }
    }
}
