using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Opt-in client breadcrumbs for the crash in MagicaCloth2 cape collider registration. Every line is written before the step
    /// it names, straight to FGN's log source so Log Level cannot hide it, and reaches Player.log, which survives the crash.
    /// FGN's own network steps (Steam settings, auto-tune, compressed packets, clock corrections) log through Log as well.
    /// </summary>
    [HarmonyPatch]
    public static class CapeCrashDiagnostics
    {
        private const int MaxPathDepth = 12;
        private const int MaxStackFrames = 14;
        private const int CreationBurstLogCount = 25;
        private const float HeartbeatIntervalSec = 5f;

        private static readonly FieldInfo ClothCollidersField = AccessTools.Field(typeof(VisEquipment), "m_clothColliders");
        private static readonly Dictionary<int, int[]> s_colliderSnapshots = new Dictionary<int, int[]>();

        private static float s_nextHeartbeat;
        private static PropertyInfo s_magicaTeamProperty;
        private static PropertyInfo s_magicaTeamCountProperty;
        private static bool s_magicaTeamResolved;

        public static bool Enabled => FiresGhettoNetworkMod.ConfigEnableCapeCrashDiagnostics?.Value ?? false;

        public static void Log(string message)
        {
            if (!Enabled) return;
            FiresGhettoNetworkMod.Log?.LogMessage($"[CapeDiag {DateTime.Now:HH:mm:ss.fff}] {message}");
        }

        [HarmonyPatch(typeof(Player), nameof(Player.StartDoodadControl))]
        [HarmonyPostfix]
        public static void Player_StartDoodadControl_Postfix(Player __instance, IDoodadController shipControl)
        {
            if (!Enabled || __instance != Player.m_localPlayer) return;
            var controller = shipControl as Component;
            Log($"Local player took control of '{(controller != null ? controller.name : "unknown")}' (frame {Time.frameCount})");
        }

        [HarmonyPatch(typeof(Player), nameof(Player.StopDoodadControl))]
        [HarmonyPrefix]
        public static void Player_StopDoodadControl_Prefix(Player __instance)
        {
            if (!Enabled || __instance != Player.m_localPlayer) return;
            Log($"Local player released ship control (frame {Time.frameCount})");
        }

        [HarmonyPatch(typeof(VisEquipment), "SetShoulderEquipped")]
        [HarmonyPrefix]
        public static void VisEquipment_SetShoulderEquipped_Prefix(VisEquipment __instance, int hash, int variant, int quality,
            int ___m_currentShoulderItemHash, int ___m_currentShoulderItemVariant, int ___m_currentShoulderItemQuality, ZNetView ___m_nview)
        {
            if (!Enabled) return;
            if (___m_currentShoulderItemHash == hash && ___m_currentShoulderItemVariant == variant && ___m_currentShoulderItemQuality == quality) return;
            bool local = Player.m_localPlayer != null && __instance.gameObject == Player.m_localPlayer.gameObject;
            Log($"Shoulder item change on '{__instance.name}' ({DescribeView(___m_nview)}, player={__instance.m_isPlayer}, local={local}, frame {Time.frameCount}): "
                + $"{___m_currentShoulderItemHash}/{___m_currentShoulderItemVariant}/q{___m_currentShoulderItemQuality} -> {hash}/{variant}/q{quality}; called from {ShortStack()}");
        }

        [HarmonyPatch(typeof(VisEquipment), "SetupCloth")]
        [HarmonyPrefix]
        public static void VisEquipment_SetupCloth_Prefix(VisEquipment __instance, GameObject item)
        {
            if (!Enabled) return;

            var colliders = ClothCollidersField?.GetValue(__instance) as IList;
            Log($"SetupCloth '{(item != null ? item.name : "null")}' on '{__instance.name}' (frame {Time.frameCount}, cloth teams {MagicaTeamCount()}): "
                + $"{(colliders == null ? "no collider list" : colliders.Count + " cloth collider(s)")}");
            if (colliders == null) return;

            for (int i = 0; i < colliders.Count; i++)
            {
                object entry = colliders[i];
                if (ReferenceEquals(entry, null))
                {
                    Log($"  collider[{i}]: null reference");
                    continue;
                }

                Log($"  collider[{i}]: checking");
                if (!(entry is UnityEngine.Object unityObject))
                {
                    Log($"  collider[{i}]: not a Unity object ({entry.GetType().FullName})");
                    continue;
                }
                if (unityObject == null)
                {
                    Log($"  collider[{i}]: destroyed (instance {unityObject.GetInstanceID()})");
                    continue;
                }

                var component = unityObject as Component;
                var behaviour = unityObject as Behaviour;
                Log($"  collider[{i}]: {unityObject.GetType().Name} instance {unityObject.GetInstanceID()} "
                    + $"at '{(component != null ? PathOf(component.transform) : unityObject.name)}' active={(behaviour == null || behaviour.isActiveAndEnabled)}");
            }
        }

        [HarmonyPatch(typeof(VisEquipment), "SetupCloth")]
        [HarmonyPostfix]
        public static void VisEquipment_SetupCloth_Postfix(GameObject item)
        {
            if (Enabled) Log($"SetupCloth '{(item != null ? item.name : "null")}' finished");
        }

        /// <summary>Checks every frame that each character's cape collider list still holds the colliders it started with, so corruption is caught when it happens rather than when a cape is next built.</summary>
        [HarmonyPatch(typeof(VisEquipment), nameof(VisEquipment.CustomUpdate))]
        [HarmonyPostfix]
        public static void VisEquipment_CustomUpdate_Postfix(VisEquipment __instance)
        {
            if (!Enabled) return;
            if (Player.m_localPlayer != null && __instance.gameObject == Player.m_localPlayer.gameObject) Heartbeat();

            if (!(ClothCollidersField?.GetValue(__instance) is IList colliders) || colliders.Count == 0) return;
            int key = __instance.GetInstanceID();
            if (!s_colliderSnapshots.TryGetValue(key, out int[] expected))
            {
                s_colliderSnapshots[key] = ColliderIds(colliders);
                return;
            }

            string problem = FindColliderListChange(colliders, expected);
            if (problem == null) return;
            Log($"COLLIDER LIST CHANGED on '{__instance.name}' (frame {Time.frameCount}): {problem}");
            s_colliderSnapshots[key] = ColliderIds(colliders);
        }

        [HarmonyPatch(typeof(VisEquipment), "OnDisable")]
        [HarmonyPostfix]
        public static void VisEquipment_OnDisable_Postfix(VisEquipment __instance)
        {
            s_colliderSnapshots.Remove(__instance.GetInstanceID());
        }

        [HarmonyPatch(typeof(ZNetScene), "CreateDistantObjects")]
        [HarmonyPostfix]
        public static void ZNetScene_CreateDistantObjects_Postfix(int maxCreatedPerFrame, ref int created)
        {
            if (!Enabled || created < CreationBurstLogCount) return;
            Log($"Created {created} objects in frame {Time.frameCount} (per-frame cap {maxCreatedPerFrame})");
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
        [HarmonyPostfix]
        public static void ZDO_Deserialize_Postfix(ZDO __instance)
        {
            if (!Enabled) return;
            Player local = Player.m_localPlayer;
            if (local == null || local.m_nview == null || local.m_nview.GetZDO() != __instance) return;
            Log($"Received data overwrote the local player's ZDO {__instance.m_uid} (data rev {__instance.DataRevision}, owner {__instance.GetOwner()}, ours={__instance.IsOwner()}, frame {Time.frameCount})");
        }

        private static void Heartbeat()
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_nextHeartbeat) return;
            s_nextHeartbeat = now + HeartbeatIntervalSec;

            int objects = ZNetScene.instance != null ? ZNetScene.instance.m_instances.Count : -1;
            Log($"Heartbeat frame {Time.frameCount}: managed heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB, GC {GC.CollectionCount(0)}, "
                + $"objects {objects}, cloth teams {MagicaTeamCount()}");
        }

        private static int[] ColliderIds(IList colliders)
        {
            var ids = new int[colliders.Count];
            for (int i = 0; i < ids.Length; i++)
                ids[i] = colliders[i] is UnityEngine.Object unityObject ? unityObject.GetInstanceID() : 0;
            return ids;
        }

        private static string FindColliderListChange(IList colliders, int[] expected)
        {
            if (colliders.Count != expected.Length) return $"count {expected.Length} -> {colliders.Count}";
            for (int i = 0; i < expected.Length; i++)
            {
                object entry = colliders[i];
                if (ReferenceEquals(entry, null)) return expected[i] == 0 ? null : $"[{i}] instance {expected[i]} -> null reference";
                if (!(entry is UnityEngine.Object unityObject)) return $"[{i}] instance {expected[i]} -> non-Unity object {entry.GetType().FullName}";
                int id = unityObject.GetInstanceID();
                if (id != expected[i]) return $"[{i}] instance {expected[i]} -> {DescribeObject(entry)}";
            }
            return null;
        }

        private static int MagicaTeamCount()
        {
            try
            {
                if (!s_magicaTeamResolved)
                {
                    s_magicaTeamResolved = true;
                    s_magicaTeamProperty = AccessTools.Property(AccessTools.TypeByName("MagicaCloth2.MagicaManager"), "Team");
                    s_magicaTeamCountProperty = AccessTools.Property(AccessTools.TypeByName("MagicaCloth2.TeamManager"), "TeamCount");
                }
                object team = s_magicaTeamProperty?.GetValue(null);
                return team != null && s_magicaTeamCountProperty != null ? (int)s_magicaTeamCountProperty.GetValue(team) : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static string ShortStack()
        {
            StackFrame[] frames = new StackTrace(2, false).GetFrames();
            if (frames == null) return "unknown";
            var text = new StringBuilder();
            for (int i = 0; i < frames.Length && i < MaxStackFrames; i++)
            {
                MethodBase method = frames[i].GetMethod();
                if (method == null) continue;
                if (text.Length > 0) text.Append(" < ");
                text.Append(method.DeclaringType != null ? method.DeclaringType.Name + "." + method.Name : method.Name);
            }
            return text.ToString();
        }

        internal static string DescribeObject(object value)
        {
            if (ReferenceEquals(value, null)) return "null reference";
            if (!(value is UnityEngine.Object unityObject)) return value.GetType().FullName;
            if (unityObject == null) return $"destroyed {value.GetType().Name} (instance {unityObject.GetInstanceID()})";
            var component = unityObject as Component;
            return $"{value.GetType().Name} instance {unityObject.GetInstanceID()} at '{(component != null ? PathOf(component.transform) : unityObject.name)}'";
        }

        private static string DescribeView(ZNetView view)
        {
            if (view == null) return "no ZNetView";
            if (!view.IsValid()) return "invalid ZNetView";
            ZDO zdo = view.GetZDO();
            return $"zdo {zdo.m_uid} owner {zdo.GetOwner()} ours={zdo.IsOwner()} data rev {zdo.DataRevision}";
        }

        private static string PathOf(Transform transform)
        {
            var path = new StringBuilder(transform.name);
            Transform parent = transform.parent;
            for (int depth = 0; parent != null && depth < MaxPathDepth; depth++, parent = parent.parent)
                path.Insert(0, parent.name + "/");
            return path.ToString();
        }
    }

    /// <summary>Logs each collider MagicaCloth2 registers for a cloth, before and after, so a crash inside registration names the collider.</summary>
    [HarmonyPatch]
    public static class MagicaColliderRegistrationDiagnostics
    {
        private const string ColliderManagerTypeName = "MagicaCloth2.ColliderManager";

        public static bool Prepare() => AccessTools.TypeByName(ColliderManagerTypeName) != null;

        public static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName(ColliderManagerTypeName), "AddCollider");

        [HarmonyPrefix]
        public static void Prefix(object[] __args)
        {
            if (!CapeCrashDiagnostics.Enabled) return;
            CapeCrashDiagnostics.Log($"    MagicaCloth2 AddCollider {CapeCrashDiagnostics.DescribeObject(__args.Length > 1 ? __args[1] : null)}");
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            if (CapeCrashDiagnostics.Enabled) CapeCrashDiagnostics.Log("    MagicaCloth2 AddCollider returned");
        }
    }

    /// <summary>Logs every MagicaCloth2 cloth built or destroyed (capes, sails, anything else), so cloth churn before a crash is visible.</summary>
    [HarmonyPatch]
    public static class MagicaClothLifecycleDiagnostics
    {
        private const string MagicaClothTypeName = "MagicaCloth2.MagicaCloth";

        public static bool Prepare() => AccessTools.TypeByName(MagicaClothTypeName) != null;

        public static IEnumerable<MethodBase> TargetMethods()
        {
            Type cloth = AccessTools.TypeByName(MagicaClothTypeName);
            yield return AccessTools.Method(cloth, "BuildAndRun");
            yield return AccessTools.Method(cloth, "OnDestroy");
        }

        [HarmonyPrefix]
        public static void Prefix(object __instance, MethodBase __originalMethod)
        {
            if (!CapeCrashDiagnostics.Enabled) return;
            CapeCrashDiagnostics.Log($"MagicaCloth {__originalMethod.Name} on {CapeCrashDiagnostics.DescribeObject(__instance)} (frame {Time.frameCount})");
        }
    }
}
