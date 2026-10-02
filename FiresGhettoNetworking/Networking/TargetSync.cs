using BepInEx.Configuration;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Shares who a monster is after. Vanilla keeps a monster's target only on the game that owns it and syncs just a
    /// "has a target" flag, so every other game (a player's companions, a test bot, the dedi's own copy when a player owns it,
    /// and every client when the server simulates monsters) reads no target and can't tell a monster is coming for it.
    /// The owner's game writes the target's ZDOID beside that flag, only when it changes, with its own session id so a reader
    /// can tell a key left behind by an earlier owner (one without FGN) from a live one.
    ///
    /// Read order for other mods (no FGN reference needed; keys by name):
    ///   1. the owner's game: BaseAI.GetTargetCreature();
    ///   2. anyone else: BaseAI.HaveTarget() true, ZDO long "fgn_target_by" == ZDO.GetOwner(), then
    ///      ZDO.GetZDOID(ZDO.GetHashZDOID("fgn_target")) -> ZNetScene.FindInstance;
    ///   3. key absent or stale (the owner has no FGN): the mod's own guess (e.g. an alerted monster nearest to us).
    /// </summary>
    [HarmonyPatch]
    public static class TargetSync
    {
        public static ConfigEntry<bool> ConfigEnabled;

        public const string TargetKeyName = "fgn_target";
        public const string WriterKeyName = "fgn_target_by";
        private static readonly System.Collections.Generic.KeyValuePair<int, int> s_targetKey = ZDO.GetHashZDOID(TargetKeyName);
        private static readonly int s_writerKey = WriterKeyName.GetStableHashCode();

        private static bool s_announced;

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("04 - Networking", "Sync Monster Targets", true,
                "Shares who each monster is after, so other games (companions, clients when the server runs monsters) see its\n" +
                "target and a new owner keeps chasing it. Applies wherever monsters are run. Mods can read 'fgn_target'/'fgn_target_by'.");
        }

        // One hook on BaseAI.SetTargetInfo: vanilla's owner-side call every target update, which gets the target's ZDOID and
        // keeps only "have a target". Nothing else in FGN hooks it.
        [HarmonyPatch(typeof(BaseAI), "SetTargetInfo"), HarmonyPrefix]
        static void SetTargetInfo_Prefix(BaseAI __instance, ref ZDOID targetID)
        {
            if (!(ConfigEnabled?.Value ?? false)) return;
            ZNetView view = __instance.m_nview;
            if (view == null || !view.IsValid() || !view.IsOwner()) return;
            ZDO zdo = view.GetZDO();
            long me = ZDOMan.GetSessionID();
            if (targetID.IsNone() && AdoptOnHandover(__instance, zdo, me, out ZDOID adopted)) targetID = adopted;
            // A monster that never had a target synced has nothing to clear: most idle monsters never gain the keys.
            if (targetID.IsNone() && zdo.GetLong(s_writerKey) == 0L) return;
            // ZDO.Set bumps the data revision (and so a send) only on a change; skip the reads-equal case outright.
            if (zdo.GetZDOID(s_targetKey) == targetID && zdo.GetLong(s_writerKey) == me) return;
            zdo.Set(s_targetKey, targetID);
            zdo.Set(s_writerKey, me);
            if (s_announced) return;
            s_announced = true;
            LoggerOptions.LogInfo($"[FGN] target sync: on ({TargetKeyName})");
        }

        // R85 (Selective ON): a pack spawned alerted and targeting went PASSIVE when the dedi took it over. The alert
        // survives a handover (vanilla: a non-owner copy re-reads s_alert every UpdateAI), the target does not: MonsterAI keeps
        // m_targetCreature only on the owner's game. A new owner's first "no target" report finds the previous owner's
        // fgn_target (writer != this session) and takes it over when that target is still alive, still an enemy and within
        // AdoptMaxMeters; then the monster is after the same player on the new owner too. Tamed ones never (their owner decides).
        private const float AdoptMaxMeters = 60f;
        private static int s_adopted;
        private static float s_nextAdoptLog;

        private static bool AdoptOnHandover(BaseAI ai, ZDO zdo, long me, out ZDOID adopted)
        {
            adopted = ZDOID.None;
            if (!(ai is MonsterAI monster) || monster.m_character == null || monster.m_character.IsTamed()) return false;
            long writer = zdo.GetLong(s_writerKey);
            if (writer == 0L || writer == me) return false;
            ZDOID id = zdo.GetZDOID(s_targetKey);
            if (id.IsNone() || ZNetScene.instance == null) return false;
            UnityEngine.GameObject go = ZNetScene.instance.FindInstance(id);
            Character target = go != null ? go.GetComponent<Character>() : null;
            if (target == null || target.IsDead() || !BaseAI.IsEnemy(monster.m_character, target)) return false;
            if (UnityEngine.Vector3.Distance(target.transform.position, monster.transform.position) > AdoptMaxMeters) return false;
            monster.m_targetCreature = target;
            monster.m_timeSinceSensedTargetCreature = 0f;
            monster.SetAlerted(true);
            adopted = id;
            s_adopted++;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now >= s_nextAdoptLog)
            {
                s_nextAdoptLog = now + 10f;
                LoggerOptions.LogDebug($"[FGN] target sync: {s_adopted} monster(s) kept their target across an owner change "
                                         + $"(last: {monster.name} after {target.name})");
            }
            return true;
        }
    }
}
