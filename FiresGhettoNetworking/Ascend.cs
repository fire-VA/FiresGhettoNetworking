using BepInEx;
using BepInEx.Configuration;
using FiresGhettoNetworkMod.AutoTune;
using HarmonyLib;
using System;
using System.Collections;
using System.ComponentModel;
using System.Reflection;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(ValheimCommunityPatchCompat.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(ValheimPerformanceOptimizationsGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("Azumatt.MaxPlayerCount", BepInDependency.DependencyFlags.SoftDependency)]   // 1.6.31: load order only. The player-limit transpiler runs in Awake and reads PluginInfos, so a MaxPlayerCount loading after FGN was missed and both patched the cap.

    public class FiresGhettoNetworkMod : BaseUnityPlugin
    {
        public const string PluginGUID = "com.Fire.FiresGhettoNetworkMod";
        public const string PluginName = "FiresGhettoNetworkMod";
        public const string PluginVersion = "1.6.84";   // 1.6.84 ([fgn], [wishbone] round 2): congested re-sort keeps creatures and players owned by others first (key on); Creature Ownership In Fights (Vanilla / LowestPing / FirstFighter, default Vanilla).   // was 1.6.83:   // 1.6.83 ([fgn], [generator] review): far idle handoff guarded and finds FiresFixes without a Harmony type search.   // was 1.6.82: 1.6.82 ([fgn], lead audit 11-09): owner-safe write hooks skip with no peers (key), hosts build the leave cleanup list at first join (key), Self Cost Census timers start after arrival (key).   // was 1.6.81: 1.6.81 ([fgn], Fire SP split, second move, with FiresFixes 0.1.91): ground snap on clients and hosts, invulnerable support skip, cape and fall-through diagnostics moved to FiresFixes; a dedicated server keeps its ground snap. SelfCost names its allocation counter.   // was 1.6.80: 1.6.80 ([fgn], lead): Self Cost Census self-tests its allocation counter (falls back to the Mono heap size; names which).   // was 1.6.79: 1.6.79 ([fgn], lead/[visual] 11-08 SP split, never both): FGN's client create pacer stands aside only while EBM's says it is ready (EbmApi.StreamPacerReady, by name, re-read each pass); missing or false = FGN paces as 1.6.77. + SelfCost per-hook allocation bytes. (1.6.78's unconditional strip is superseded.)   // was 1.6.77: 1.6.77 ([fgn], lead SelfCost: CreateObjects prefix 0.683 ms/frame settled): the distant create pass walks a 4,000-entry round-robin window per pass instead of the whole distant list.   // was 1.6.76: 1.6.76 ([fgn], Fire 11-08 SP split): 9 single-player features moved to FiresFixes 0.1.84 (fireplace fuel, sleep skip, spawn / terrain-compiler guards, terrain rebuild spread, dungeon spread, ZDO read, fgn_tamecheck, rebuild census); the ZdoValueWriteNoBox duplicate of FF SetValueNoBox removed.   // was 1.6.75: 1.6.75 ([fgn], lead SelfCost 11-08: Sweeper.Update 0.734 ms/frame): the kinematic sync sweep walks only live ZSyncTransforms (OnEnable/OnDisable registry), a slice a frame, instead of every ZNetScene instance with a GetComponent each every 2 s.   // was 1.6.74: 1.6.74 ([fgn], lead 11-08 615 fill 183 s): with Adaptive Fill Budget the create queue also runs on every frame (not only vanilla's 30 Hz tick), a standing player fills up to ~45 ms frames even when the town runs below target, one Info [Fill] line per drained fill.   // was 1.6.73: 1.6.73 ([fgn], Fire 11-08 "adjust our throttle to the machine's abilities"): Adaptive Fill Budget (on): create budget = target frame (vsync / fps cap / refresh) minus measured other work, cap = budget / measured ms per create; still or loading up to 40 ms, moving headroom only; [Fill] census.   // was 1.6.72: 1.6.72 ([ghost], study N4): Send Portal Destination Early (OFF): a client at a connected portal names the PORTAL; the server checks it and appends the destination 3x3 terrain/solid ZDOs after vanilla's sync list for 8 s (spare budget, max 128 per list).   // was 1.6.71: 1.6.71 ([ghost], study F2): a feature class that fails to attach is rolled back (its already-attached parts removed) and named with the innermost cause; one startup [PatchSafety] summary line.   // was 1.6.70: 1.6.70 ([saddles] N1): the teleport answer for IsActiveAreaLoaded is given only inside ZNetScene.CreateObjects, and each object there also needs its own zone loaded (TeleportCreateGate); every other caller gets the real answer.   // was 1.6.69: 1.6.69 ([fgn] + [ghost], SpreadTheLoad study ideas, our own code): Server Simulation Distance Ceiling (-1 = vanilla), Resources Go To Whoever Hits Them (off), Judge Peers By Update Gaps (off, feeds the creature balancer), [ghost] Server Hands Ship To Helmsman For Unmodded Owners (on) + Creatures Go To The Player Touching Them (off) + in-use veto.   // was 1.6.68: 1.6.68 ([fgn], lead: Fire's one-session A/B): console "fgn_eventarea [on|off|cfg]" flips Event Driven Object Area live for this session only (cfg never written); the next tick runs a full gather.   // was 1.6.67: 1.6.67 ([fgn], VPO study row 1 step 2): clients in one zone run a LIGHT object-area pass (vanilla CreateObjects with the event queue + last full gather's uncreated distant objects, no FindSectorObjects) and the full gather at most once per "Event Driven Object Area Full Pass Seconds" (1 s) and on any change; key "Event Driven Object Area" (OFF until the A/B); proof in the 30 s [Stream] line.   // was 1.6.66: 1.6.66 ([fgn], VPO study row 1): the client removal scan marks in-area objects with the game's own TempRemoveEarmark byte instead of copying ~134k ZDOs into a HashSet up to 10x a second; key "Removal Scan Without Area Set" (on); Debug proof in the 30 s [CleanupThrottle] line.   // was 1.6.65: 1.6.65 ([fgn], VPO_LEVERS / lead audit, VPO's ZNetScene object-management idea, our code): clients skip vanilla's 30 Hz CreateDestroyObjects pass (FindSectorObjects over every nearby ZDO) on idle ticks: zone unchanged, event queue empty and seeded, nothing pending removal, the 10 Hz removal pass not due; key "Skip Idle Object Area Passes" on.   // was 1.6.64: 1.6.64 ([fgn], lead audit M6/M10/M18/FLIP, Fire 11-08 no mod relies on another): FGN runs the kinematic sync (prefix or 2 s sweep from its own key); FgnApi 9: RegisterDistantPrefab etc. (distant prefab streaming, server mark + force-send, client-only injection) for mods that patched the streaming path themselves; IsAutoTuneEnabled / IsAutoTuneSettled / AutoTuneSettled instead of reflecting a private field.   // was 1.6.63: 1.6.63 ([fgn], lead + Fire 11-08 "FGN relies on no other mod"): the last 3 lookups of another mod by type name are gone. Stats are pulled through FgnApi.GetStatsRows, far-idle creature skips are registered through FgnApi.RegisterFarIdleSkip, the kinematic sweep key is read through FgnApi.KinematicSweepEnabled (API Version 8). FGN names no other mod anywhere.   // was 1.6.62: 1.6.62 ([fgn], lead + Fire 11-08): RPC census names come from names seen in use (InvokeRoutedRPC / non-generic Register, observe-only postfixes), the IL literal scan (GetILAsByteArray + ResolveString over every assembly) is gone; server removal keep is FGN's own FgnApi.RegisterServerRemovalKeep (API 7), FGN no longer looks up Core's CompanionKeep.   // was 1.6.61: 1.6.61 ([fgn] + [generator], log-proven on the IsoDedi 10:52-11:00: 1.6.60's sliced removal fell back to the whole vanilla RemoveObjects every 2 s, a 17-20 ms pass, because Core's TamedCompanionZoneLoader patches it): with Core 0.2.1213's another mod's Npc.CompanionKeep (Refresh once a removal cycle, IsKept per ZDO) the walk keeps Core's companion set itself and stays sliced; Core's patch still counts as foreign without the API. Upkeep line adds the companion keep count / refresh ms.   // was 1.6.60: 1.6.60 ([fgn], [lead] / [classes-ui] 11-08 TOWN50 new baseline, IsoDedi log-proven: settled town, "whole pass 16.86 ms, create 0.00, keep-list 10.20, remove 2.71" at ~7 passes a second over 59,517 instances): the server queue's removal pass (keep-list + vanilla RemoveObjects) runs every 2 s and at once after a reseed, and the simulated area (creatures / ships among the live instances) is rebuilt every 0.5 s, instead of both every pass. "[SSS] server queue upkeep" Debug line.   // was 1.6.59: 1.6.59 ([fgn], lead 11-08; replaces the withdrawn, never-deployed 1.6.57 / 1.6.58 and carries both): cell 0 on 1.6.56 proved the pieces rule ran but its 3x3-zone areas covered the town (39 zones from 12 simulated objects; instances 11k -> 59k), so a building piece now gets a server instance only within 40 m of a simulated object or kept / test point (16 m cell set; one lookup per piece). Plus 1.6.57's key "Server Event-Driven Create Queue" (the SSS pass without walking every nearby object) and 1.6.58's removal of ServerStabilityPatches (its three fixes re-derived from vanilla: not needed).   // was 1.6.58: 1.6.58 ([fgn], lead 11-08 approved the rewrite of ServerStabilityPatches, "carried over from Serverside Simulations"): each of its three fixes re-derived from vanilla 1.0 and found not needed, so the file is removed (no third-party code left): (1) the UpdateAttack "stale Attack" guard: Attack.m_character is set in Attack.Start to the attacking Humanoid itself, so it cannot point at a destroyed character while that Humanoid runs; (2) the UpdateSupport collider re-init: vanilla SetupColliders writes m_colliders and m_bounds back to back and UpdateSupport calls it whenever m_colliders is null, so "colliders set, bounds null" is unreachable; (3) the server-side "RequestRespons" ownership hand-off: HelmOwnership already hands the ship to the helmsman on the owning game (RPC_RequestControl postfix), where its physics runs; the in-transit SetOwner on the server only raced it. No log ever showed an NRE on (1) / (2) (client, dedi and F:\ archives searched).   // was 1.6.57: 1.6.57 ([fgn], lead 11-08 SSS option (b)): key "Server Event-Driven Create Queue" (a fix, ON): the SSS server pass seeds one create queue and the area's sector sets when the players' zones change, adds new ZDOs from ZDOMan.AddToSector, sweeps 4,000 a pass, creates in waves under the server budget, and builds the RemoveObjects keep-list from live instances only (no walk of every nearby object). Keeps 1.6.56's pieces-only-where-simulated rule. "[SSS] server queue" Debug line + self-check.   // was 1.6.56: 1.6.56 ([fgn], lead 11-08 relaying Fire, IsoDedi BlumpkinPre town ~15 fps, 131k near objects, ~12.5k instances a minute): key "Server Builds Pieces Only Where It Simulates" (a fix, ON): with SSS the server instantiates building pieces only in the 3x3 zones around the creatures / ships it simulates (and kept areas); elsewhere pieces stay data only as on a vanilla server; pieces already built are released at most 100 a pass. "[SSS] pieces where simulated" Debug line. AutoTune "Link never settled" is Info (handled: the tier is held, the sample not cached).   // was 1.6.55: 1.6.55 ([fgn], Fire 11-08 "correct and performative and the best solution", log-proven on 0108k BlueHills): the create queue fills in waves (zone ring + type rank: the player's zone first, a ring's terrain/solid before the inner ring's loose items), a bucket waiting over 10 s gets a quarter of the frame's creates (no starving), and the budget comes from the measured frame (arrival: up to 120 ms frames, 8-40 ms; play with a backlog: 25 % of the other work, tier budget to 16 ms, 40 ms unfocused; a spike backs off for 30 frames). Proof: our pass avg/worst, frames over 150 ms and whose they were, zone settle time avg/max, "[Stream] arrival: the player's 3x3 zones settled N s".   // was 1.6.54: 1.6.54 ([fgn], Fire 11-08 "close it fix it and relaunch"): key "Event-Driven Create Queue" (a fix, ON) replaces 1.6.53's cached order: one event-driven queue (seeded per zone change, fed by ZDOMan.AddToSector, a round-robin safety sweep), vanilla's order by type and zone ring, a burst behind the loading screen, a frame budget in play with a backlog burst; publishes FgnStreaming.ZoneFilling / ZoneSettled + StreamingSignalActive (client / local host); "[Stream]" Debug proof line; Self-Check key (off).   // was 1.6.53: 1.6.53 ([fgn], Fire 11-08 shots 604-606 "we shouldnt be waiting minutes for it to load", log-proven: the time slice made 1 create a frame, ~22/s, on a 174k-object town because re-picking every nearby object took ~12 ms of its 5 ms budget each frame): key "Time-Slice Cached Create Order" (a fix, ON): the pick is kept across frames (rebuilt every 0.5 s / 4 m / when used up) and the budget counts creating only, so a dense town fills at vanilla's pace or faster; Debug proof line compares the walk with vanilla's per-frame full sort.   // was 1.6.52: 1.6.52 ([fgn], lead 11-08): key "Merge Edited Terrain First Build" (OFF until A/B + VegAB): an edited tile loading away from you is built once with its edits (vanilla built it plain, then again when its terrain compiler loaded).   // was 1.6.51: 1.6.51 ([fgn], lead 11-07): Fast Leave Cleanup finds ZDOPool.Release(Dictionary) in code (Hexium flagged the attribute on Steam dedi 26266951, which has it); if a build lacks it, Fast Leave Cleanup turns off for the session.   // was 1.6.50: 1.6.50 ([fgn], lead 11-07): Regenerate Census names who asked for each rebuild Spread Terrain Rebuilds queued ("Drain for [asker]"), so its same-tile repeats can be traced. Debug tool, key default off.   // was 1.6.49: 1.6.49 ([fgn], lead 11-07): the WackysDatabase null-ItemDrop snapshot skip logs one Info line then Debug (it was a Warning every time; handled, nothing lost). Named broken-prefab cases stay Warning.   // was 1.6.48: 1.6.48 ([fgn], lead 11-07): Regenerate Census counts a call Spread Terrain Rebuilds queued apart (1.6.47 counted it AND the later build, which showed as a repeat), checks every repeat for a real terrain edit in between, and reads the late paths through the Harmony wrapper name.   // was 1.6.47: 1.6.47 ([fgn], lead 11-07): Patch Timer also runs on a dedicated server (it waited for a local player, so the server never reported).   // was 1.6.46:   // 1.6.46 ([fgn], lead 11-06): Patch Timer skips a missing or ambiguous name with one warning (overloads take parameter types) and never writes its cfg; Regenerate Census (Debug, off) names who asks for terrain tile rebuilds.   // was 1.6.45:   // 1.6.45 ([fgn]+[visual] 11-06, one pacing owner): arrival (no player, teleporting) creates at vanilla speed; no x3 widening during play; the near pass picks only the first objects in vanilla's order and the pick counts in the budget.   // was 1.6.44:   // 1.6.44 ([fgn], Fire 11-05): Fast Leave Cleanup on by default, its self-check off by default.   // was 1.6.43:   // 1.6.43 ([fgn], trial 11-05): Call RPC Handlers Directly casts 4-6 argument RPCs to vanilla's own delegate types (ChatMessage threw); a value that doesn't fit hands that RPC to vanilla with one warning.   // was 1.6.42:   // 1.6.42 ([fgn], Fire 11-05): Remote Motion (experimental) defaults to off again, and its one-time switch-on of old cfgs is gone.   // was 1.6.41:   // 1.6.41 ([fgn], Fire 11-05 group D): Fast Leave Cleanup (off) with self-check.   // was 1.6.40:   // 1.6.40 ([fgn], Fire 11-05 group B): ZDO.Deserialize without its fourteen delegates per object (off).   // was 1.6.39:   // 1.6.39 ([fgn], Fire 11-05 group B): received RPC arguments read into one kept list; RPC handlers called directly (off by default).   // was 1.6.38:   // 1.6.38 ([fgn], Fire 11-05): a patch skipped on a game build without Steam logs one Info line with the reason; ZoneLoadDiag's Steam timing guarded.   // was 1.6.37:   // 1.6.37 ([fgn], Fire 11-05): Info shows at the default level, one-time events and state changes; test and repeating lines print only at Log Level Debug. // was 1.6.33:   // 1.6.33 ([fgn]): test commands need cheats; water depth check names each tile; fgn_tamecheck. // was 1.6.32:   // 1.6.32 ([fgn]): spread keeps a queued full rebuild, ForceGenerateAll takes the queue, water depth check. // was 1.6.31:   // 1.6.31 ([fgn], read from NPS gap review 11-05): station requests aimed at a headless server reach the router; Fermenter, cooking station and shield generator refunds name their item; RoutedSenderGuard checks in place without copying; Log Allocation Sites census (off); final read fixes: classic zone tests are the real square, far-idle covers the whole kept area, throttles leave a skipped step alone, pending server keys expire after 30 s, client creates are budgeted after the sort, hand-off answers only from the asked owner, instances kept while their zone loads, parked hulls get their collision mode back, TPP portals follow the peer rule, live compression toggle, Patch Timer no longer repatches. Fire's decisions: broad ownership asks for ships back only with Server-Side Ship Simulation on; the server no longer takes a ship from its helmsman; with Server Simulates Tamed Creatures Too off, a server-held tame goes to the player in range; key Raid Monsters Need A Player In The Raid Area (ON). Player report: tiles never built, the teleport target and ForceGenerateAll are no longer spread; key Map Icons Jump With Teleports (ON); Fix Ground Snap Through Floors no longer stops vanilla's lift of a character under the terrain.   // 1.6.30 ([fgn], lead 11-04): far-idle ticks test the owner first; non-owned copies skip the state lookup. Same behaviour.
        // was 1.6.28:   // 1.6.28 ([fgn], Fire 11-02): the 11 keep-alive + homestead keys leave FGN for FiresCompanions; FgnApi.SetKeepAliveSettings receives the values (API v6).
        // was 1.6.27:   // 1.6.27 ([fgn], Fire 10-31 "Fix it"): Creature/HelmOwnership pick up prefabs other mods register after the first scan (only the new tail of ZNetScene.m_prefabs is scanned when it grows). Includes 1.6.26 (never deployed).
        // was 1.6.26:   // 1.6.26 ([fgn], Fire 10-31 event-driven/cache rule, SAME BEHAVIOUR only): FarIdle reads m_nview/m_character fields instead of GetComponent per tick; ShipFixes cached FieldRefs; RandEvent delegates instead of MethodInfo.Invoke.
        // was 1.6.25:   // 1.6.25 ([fgn], lead 10-31 perf routing): FGN owns [Performance] Kinematic sync sweep (default on; value copied once from FiresCompanions/Core cfg; provided to Core at priority 10).
        // was 1.6.24:   // 1.6.24 ([lead] + [ghost] 10-30): a ZDO flagged bool "fgn_server_keeps" (set server side by its spawner) stays server-owned under broad server ownership and is never handed to a peer; Debug "[ServerOwnership] kept by the server". One generic flag; Core's CompanionOwnershipKeeper honours the same.1.6.23 ([lead] 10-30, Fire's one-implementation rule): REMOVED the client [FrameSplit] (FDT's stall ledger already splits each frame) and the F5 fgn_tame_ownership_test (unreliable; follow / stay is a hand check). No behaviour change. 1.6.22 ([lead] 10-30): with "Server Simulates Tamed Creatures Too" on, a tame that is FOLLOWING a player stays with / goes back to that player; only idle or staying tames and tamed NPCs go to the server (Debug why per decision, Info once). Tame test runs follow twice. 1.6.21 ([lead] 10-30): fgn_tame_ownership_test only: logs the wolf's ZDO follow, AI follow target and patrol point around follow / stay. 1.6.20 ([lead] 10-30): fgn_tame_ownership_test only: the follow step walks the player away (body moved each physics step) instead of one 12 m teleport. 1.6.19 ([lead] 10-30): fgn_tame_ownership_test only: the player moves with vanilla TeleportTo and follow / stay are judged on the wolf's own movement. 1.6.18 ([lead] 10-30): fgn_tame_ownership_test fixes only: claims the test wolf before taming (vanilla Tame acts on the owner), finds the Dverger hover / interact on children. 1.6.17 ([lead] 10-30, rig A/B): key "Server Simulates Tamed Creatures Too" (off): with broad server ownership, tamed non-companion creatures go to the server too. 1.6.16 ([lead] 10-30): Debug "[ServerOwnership] kept by players" names why a player keeps a creature (tamed / possessed / not in the simulated set / key off). 1.6.15 ([lead] 10-30): Debug [FrameSplit] fixed / update / render per frame + worst frame; broad server ownership also takes a creature back from the player whose game owns it (Server Takes Creatures Back From Players, ON; tames stay); Debug [OwnerClaim] names the code that made a client own a creature. 1.6.14 ([lead] 10-30): Patch Timer handles up to 48 patches per kind (ZNet.Update has 25 postfixes) and also times VisEquipment.UpdateVisuals. 1.6.13 ([lead] 10-29): Debug Patch Timer (14 - Diagnostics, off by default): marker patches on the vanilla method time every mod's prefix/postfix on a cfg list of methods (VisEquipment.UpdateEquipmentVisuals;ZNet.Update), one line per method every 10 s. 1.6.12 ([lead] 10-29): Debug-only [NetSplit] sub-timers (ZDOMan.Update, UpdateSave, UpdatePeers, sockets, rest). 1.6.11 (Fire 10-29, God Complex): FgnApi v5 network view, read-only: GetPeerNetStats / GetZoneOwners (zone owner = its _ZoneCtrl ZDO owner) / GetSimulationState, JSON for reflection; F5 fgn_netview_test. 1.6.10:1.6.10 ([lead] + [visual] 10-29): Redraw Feasts When Their Stack Arrives (ON): a feast another peer owns is redrawn once its new portion count arrives (the eat RPC can beat the ZDO update with server ownership); Info proof once, then Debug.1.6.9 (Fire 10-28): Spread Dungeon Room Spawns (ON): a loading dungeon places its saved rooms a few per frame (6 ms), same rooms and positions, zone held as loading until all are in (1.6.7/1.6.8 held: the loading-priority cap was based on a wrong reading). 1.6.6 ([lead] dnfly87): Time-Slice on the client learns each prefab's create cost and a known-heavy object (one Instantiate over the budget: locations, dungeon rooms) goes first and alone in a frame instead of after other creates; Debug "[TimeSlice] heavy creates". 1.6.5 (Fire 10-28): Time-Slice Instantiation also runs on a game hosted from the client (local host), not only on a client joined to a dedicated server; Debug "[TimeSlice] path: ..." names the path once. 1.5.96 ([fgn] + [ghost], Fire 10-28): Slow Ticks For Far Idle Creatures (owned, idle, >60 m, off-screen, never companions/tames/keep-alive areas); default off.
        // was 1.5.95:

        // ValheimPerformanceOptimizations (ontrigger) replaces ZNetScene.CreateDestroyObjects (always skipping vanilla, around
        // ZNet's reference position) and ZDOMan.ReleaseNearbyZDOS, the two methods Server-Side Simulation and ZDO ownership
        // transfer replace on the server. Soft dependency = load order only, so it is in PluginInfos when Awake runs.
        public const string ValheimPerformanceOptimizationsGuid = "dev.ontrigger.vpo";
        internal static Harmony Harmony { get; private set; }

        // Static reference so non-MonoBehaviour subsystems (AutoTuneProbe coroutine, etc.)
        // can call StartCoroutine via the plugin instance.
        public static FiresGhettoNetworkMod Instance { get; private set; }

        // BepInEx log source — banner emitters route through this (not Debug.Log)
        // so banner lines don't stdout-echo a raw white console duplicate.
        public static BepInEx.Logging.ManualLogSource Log;

        public static ConfigEntry<LogLevel> ConfigLogLevel;
        public static ConfigEntry<bool> ConfigEnableCompression;
        public static ConfigEntry<bool> ConfigServerTestCommands;
        public static ConfigEntry<UpdateRateOptions> ConfigUpdateRate;
        // KB/s as a plain NUMBER, not a dropdown. Auto-Tune writes the exact measured value; a fixed
        // list forced it to snap down to the nearest option and threw away up to half a thin line.
        public static ConfigEntry<int> ConfigSendRateMin;
        public static ConfigEntry<int> ConfigSendRateMax;
        public const int DefaultSendRateMinKb = 512;
        public const int DefaultSendRateMaxKb = 2048;
        public const int SendRateKbLow  = 4;        // below this a line is unplayable anyway
        public const int SendRateKbHigh = 131072;   // 128 MB/s - above every Auto-Tune ceiling
        public static ConfigEntry<bool> ConfigAdaptiveUpload;
        public static ConfigEntry<QueueSizeOptions> ConfigQueueSize;
        public static ConfigEntry<ForceCrossplayOptions> ConfigForceCrossplay;
        public static ConfigEntry<int> ConfigPlayerLimit;
        public static ConfigEntry<int> ConfigAdvertisedPlayerLimit;
        public static ConfigEntry<bool> ConfigEnableShipFixes;
        public static ConfigEntry<bool> ConfigEnableServerSideShipSimulation;
        public static ConfigEntry<int> ConfigExtendedZoneRadius;
        public static ConfigEntry<bool> ConfigEnableZDOThrottling;
        public static ConfigEntry<float> ConfigZDOThrottleDistance;
        public static ConfigEntry<bool> ConfigEnableAILOD;
        public static ConfigEntry<float> ConfigAILODNearDistance;
        public static ConfigEntry<float> ConfigAILODFarDistance;
        public static ConfigEntry<float> ConfigAILODThrottleFactor;
        public static ConfigEntry<bool> ConfigEnableAdaptiveThrottling;
        public static ConfigEntry<int> ConfigSendCongestionThresholdPct;
        public static ConfigEntry<bool> ConfigEnableSendHeartbeatLog;
        public static ConfigEntry<bool> ConfigEnableFallThroughDiagnostics;
        public static ConfigEntry<bool> ConfigEnableCapeCrashDiagnostics;
        public static ConfigEntry<int> ConfigZoneLoadBatchSize;
        public static ConfigEntry<int> ConfigZPackageReceiveBufferSize;
        public static ConfigEntry<bool>  ConfigEnableTimeSliceInstantiation;
        public static ConfigEntry<bool>  ConfigEventCreateQueue;       // 1.6.54
        public static ConfigEntry<bool>  ConfigAdaptiveFill;           // 1.6.73
        public static ConfigEntry<bool>  ConfigEventQueueSelfCheck;    // 1.6.54
        public static ConfigEntry<int>   ConfigInstantiationBudgetMs;
        public static ConfigEntry<int>   ConfigMaxInstancesPerFrame;
        public static ConfigEntry<int>   ConfigServerInstantiationBudgetMs;
        public static ConfigEntry<int>   ConfigServerPassHz;
        public static ConfigEntry<bool>  ConfigSafetyFallbackEnabled;
        public static ConfigEntry<int>   ConfigSafetyFallbackThreshold;
        public static ConfigEntry<bool>  ConfigEnablePredictiveZoneStreaming;
        public static ConfigEntry<float> ConfigPredictionLookaheadSec;
        public static ConfigEntry<float> ConfigPredictionMinVelocity;
        public static ConfigEntry<int>   ConfigPredictionMaxLookaheadZones;
        public static ConfigEntry<bool> ConfigEnableInvulnerableSupportSkip;
        public static ConfigEntry<bool> ConfigEnableInstanceOrphanPrune;
        public static ConfigEntry<bool> ConfigFixTeleportGhosts;
        public static ConfigEntry<bool> ConfigKeepaliveFirst;
        public static ConfigEntry<bool> ConfigKeepWorldClockAtRealTime;
        public static ConfigEntry<bool> ConfigSmoothServerClockCorrections;
        public static ConfigEntry<bool> ConfigFixGroundSnapThroughFloors;
        public static ConfigEntry<float> ConfigCartPushUpWheelDepth;   // 1.5.87
        public static ConfigEntry<bool> ConfigEnableRpcRouter;
        public static ConfigEntry<bool> ConfigEnableRpcAoI;
        public static ConfigEntry<float> ConfigRpcAoIRadius;
        public static ConfigEntry<bool> ConfigEnableZDODelta;
        public static ConfigEntry<bool> ConfigAllocationFreeZdoWrites;
        public static ConfigEntry<bool> ConfigEnableWNTServerOptimization;
        public static ConfigEntry<bool> ConfigEnableServerAuthority;
        public static ConfigEntry<bool> ConfigEnableServerOwnership;
        public static ConfigEntry<bool> ConfigEnableServerOwnershipSelective;
        public static ConfigEntry<bool> ConfigServerPiecesOnlyWhereSimulated;   // 1.6.56
        public static ConfigEntry<bool> ConfigServerEventQueue;   // 1.6.57
        public static ConfigEntry<bool> ConfigShowAILODInServerStatus;
        public static ConfigEntry<bool> ConfigEnableBootPatchVerification;
        public static ConfigEntry<bool> ConfigEnableLargeZdoDiagnostics;
        public static ConfigEntry<int> ConfigClientMaxDestroysPerFrame;
        public static ConfigEntry<string> ConfigDediFellOutRescueLayers;
        public static ConfigEntry<float> ConfigDiagnosticIntervalSec;
        public static ConfigEntry<bool> ConfigEnableBulkTransferBoost;
        public static ConfigEntry<int> ConfigBulkTransferBudgetPercent;
        public static ConfigEntry<bool> ConfigHyperBoost;

        // A type that can't load on this game build (Steam-only types on the Microsoft
        // Store build) fails the JIT of the method that names it BEFORE any line of it runs, so a check inside Awake can't help. The
        // body lives in AwakeCore (never inlined) and a load failure is caught and logged here instead of silently killing FGN.
        private void Awake()
        {
            // 1.6.84 ([fgn], [release]'s F44 finding): BepInEx rewrites the whole cfg on every new Bind (Entries starts empty each launch), so ~194 binds = ~194 full writes.
            // With the key on, binds during Awake don't save, and the file is written once at the end: the same file the last of those writes would have produced.
            var saveOnce = Config.Bind("12 - Advanced", "Save Config Once At Startup", false, "FGN writes its settings file once at startup instead of once per setting. Same file. Off until tested.");
            bool holdSaves = saveOnce.Value && Config.SaveOnConfigSet;
            int entriesBefore = Config.Count;
            var startupTimer = System.Diagnostics.Stopwatch.StartNew();
            if (holdSaves) Config.SaveOnConfigSet = false;
            try { AwakeCore(); }
            catch (Exception ex) when (ex is TypeLoadException || ex is MissingMemberException || ex is System.IO.FileNotFoundException)
            {
                Logger.LogError($"FiresGhettoNetworkMod could not finish loading on this game build ({ex.GetType().Name}: {ex.Message}). " +
                                "Please report it with this log.");
            }
            finally
            {
                if (holdSaves)
                {
                    Config.SaveOnConfigSet = true;
                    var writeTimer = System.Diagnostics.Stopwatch.StartNew();
                    try { Config.Save(); } catch (Exception ex) { Logger.LogWarning($"Saving the config once at startup failed: {ex.Message}"); }
                    LoggerOptions.LogDebug($"[Startup] config written once: {Math.Max(0, Config.Count - entriesBefore - 1)} full write(s) avoided, one write {writeTimer.Elapsed.TotalMilliseconds:0.0} ms, Awake {startupTimer.Elapsed.TotalMilliseconds:0} ms");
                }
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void AwakeCore()
        {
            Instance = this;
            Log = Logger;
            Harmony = new Harmony(PluginGUID);

            // BIG obnoxious "loading" banner — fires FIRST before any
            // other Debug.Log so it sits at the top of the FGN-related
            // console output as the load announcement. The smaller
            // antenna+signal-bar banner fires at the end of Awake to
            // bookend load with a "we're ready" confirmation. See
            // VAGhettoBanner for the two-banner rationale + content.
            try { VAGhettoBanner.PrintBig(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] VAGhettoBanner.PrintBig failed: {ex.Message}");
            }

            // Install the color patch BEFORE PatchAll runs on other
            // patch classes, so subsequent FGN logs (those that route
            // through Debug.Log with our [FiresGhetto] tag) get colored
            // from the very first line. The patch's own FAT-detection
            // guard bails if FiresAdminTerrain is loaded — see
            // FiresLogColorPatch class header for the dedup rationale.
            try { FiresLogColorPatch.Install(Harmony); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] FiresLogColorPatch.Install failed: {ex.Message}");
            }

            BindConfigs();

            try { Config.Save(); }
            catch (Exception ex) { Logger.LogWarning($"Failed to save config file immediately: {ex.Message}"); }

            LoggerOptions.Init(Logger);

            // Audit every AutoTune tier preset against the vanilla floor now that the logger exists
            // (it runs AFTER Init for exactly that reason); LogError on any sub-vanilla value so a
            // regression is caught at load, not as a field complaint.
            TierPresets.ValidateVanillaFloors();

            ServerClientUtils.Detect(Logger);

            bool isDedicated = ServerClientUtils.IsDedicatedServerDetected;

            // Visible at default log level so deployment side is obvious in logs.
            if (isDedicated)
            {
                LoggerOptions.LogMessage($"{PluginName} v{PluginVersion} — Running on DEDICATED SERVER, enabling server-side features.");
            }
            else
            {
                LoggerOptions.LogMessage($"{PluginName} v{PluginVersion} — Running on CLIENT or SINGLE-PLAYER/LISTEN SERVER, only client-safe features will be applied.");
            }

            ValheimCommunityPatchCompat.Detect();

            // Always registered: harmless on clients, or needed before anything else.
            InvokeStaticInitByTypeName("FiresGhettoNetworkMod.CompressionGroup", "InitConfig", new object[] { Config });
            InvokeStaticInitByTypeName("FiresGhettoNetworkMod.NetworkingRatesGroup", "Init", new object[] { Config });
            InvokeStaticInitByTypeName("FiresGhettoNetworkMod.DedicatedServerGroup", "Init", new object[] { Config });
            SendQueueHeadroomMonitor.InitConfig(Config); AllocationSiteCensus.InitConfig(Config);
            AdaptiveSendRate.InitConfig(Config);
            LinkController.InitConfig(Config);
            SendScheduler.InitConfig(Config);
            StationRouter.InitConfig(Config);
            CreatureOwnership.InitConfig(Config);
            HelmOwnership.InitConfig(Config);
            OwnershipSteady.InitConfig(Config);   // [wishbone] ownstab N22/N23: Ownership Move Hysteresis, Ownership Idle Gate (off)
            PortalAhead.InitConfig(Config);   // 1.6.72 ([ghost]): Send Portal Destination Early (off; needs it on server and client); [wishbone] arrival: + On Use (off)
            ArrivalGate.InitConfig(Config);   // [wishbone] arrival N42: Refuse Empty Teleport Arrival + census (off)
            TargetSync.InitConfig(Config);
            FightHandoff.InitConfig(Config);   // [wishbone] handoff (N6/N7/N8): all off
            SectorChangeTracker.InitConfig(Config);
            TransformWriteRate.InitConfig(Config);
            SyncListRefPosPatches.InitConfig(Config);
            JoinGrace.InitConfig(Config);
            PlayFabZlibWorker.InitConfig(Config);
            LagFairDodge.InitConfig(Config);
            LogoutHold.InitConfig(Config);
            RemoteMotion.InitConfig(Config);
            RemoteArrows.InitConfig(Config);
            MapPinJump.InitConfig(Config);   // 1.6.31
            FgnVersionGate.InitConfig(Config);
            OwnerSafeRevisions.InitConfig(Config);
            SendZdoPackagePool.InitConfig(Config);   // 1.6.38
            RpcArgumentList.InitConfig(Config);   // 1.6.39
            RpcTypedInvoke.InitConfig(Config);   // 1.6.39
            DisconnectSweep.InitConfig(Config);   // 1.6.41
            FeastRedraw.InitConfig(Config);   // 1.6.10
            PatchTimer.InitConfig(Config);   // 1.6.13
            ServerOwnershipPatches.InitConfig(Config);   // 1.6.15: Server Takes Creatures Back From Players
            ZDOThrottlingPatches.InitConfig(Config);   /* 1.6.84 */ MonsterAIPatches.InitConfig(Config);   // 1.6.31: Raid Monsters Need A Player In The Raid Area
            // 1.6.82: FarIdleCreatureTicks moved to FiresFixes (FarIdleHandoff tells it about keep-alive areas)
            KinematicSweepOwner.InitConfig(Config);   // 1.6.25
            KinematicSweepOwner.Apply(Harmony);       // 1.6.64: FGN runs the kinematic sync itself (audit M10)
            // 1.6.83: ClientCleanupThrottle moved to EasyBakeMeshes;   // 1.6.65: skip idle object-area passes on clients
            GhostOwner169.InitConfig(Config);   // 1.6.69 ([ghost]): server hands ships to the helmsman for non-FGN owners (on), touch rule (off), in-use veto
            ServerSimCeiling.InitConfig(Config);   // 1.6.69: dedicated-server simulation distance ceiling (-1 = vanilla)
            ResourceOwnership.InitConfig(Config);   // 1.6.69: resources go to whoever hits them (off until the A/B)
            PeerStalls.InitConfig(Config);   // 1.6.69: judge peers by update gaps (feeds the creature balancer; off until the A/B)
            SelfCost.InitConfig(Config);   // 1.6.73: Self Cost Census (off; measuring aid)
            LoggerOptions.LogDebug("[EventDriven] 1.6.26 caches on: FarIdle component fields, ShipFixes FieldRefs, RandEvent delegates (no behaviour change)");
            PickupClaimGuard.InitConfig(Config);
            Api.FgnApi.InitConfig(Config);
            FightNet.InitConfig(Config);   // [wishbone] fight census (Debug) + Fight Traffic First (in-scheduler order only; the Steam-queue reorder is not taken, see FightNet.cs header); all off
            SendLater.InitConfig(Config);   // [wishbone] sendeff N28: Send Far Idle Changes Later (off)
            ServerSweep.InitConfig(Config);   // [wishbone] server N39/N43: Spread Server Ownership Sweep, Hold Outer Ghost Zones On Full Queue, Server Sweep Census (all off)
            DropStaleConfigKeys();   // after every key is bound, so moved values carry over
            try { Config.Save(); } catch (Exception ex) { Logger.LogWarning($"Failed to save the tidied config: {ex.Message}"); }

            if (!SteamSide.Present)
                {
                LoggerOptions.LogMessage("Steam networking not present: FGN's Steam features off, the rest on");
                Log?.LogInfo($"[{PluginName}] skipped ZSteamSocketSend, ZSteamSocketRecv: {SteamSide.Why}, Steam socket send and receive hooks off.");
            }

            TryPatchAll(typeof(RoutedSenderGuard));   // 1.6.31: was never attached, so FGN without Core trusted the packet's sender. Core's copy claims the marker first when present.
            if (AllocationSiteCensus.ConfigEnabled != null && AllocationSiteCensus.ConfigEnabled.Value) TryPatchAll(typeof(AllocationSiteCensus));
            // Attached only when one of its keys is on at start.
            if (FightNet.AnyKeyOn) { TryPatchAll(typeof(FightNet)); LoggerOptions.LogDebug("[FightNet] attached: hit census " + FightNet.ConfigHitCensus.Value + ", stagger census " + FightNet.ConfigStaggerCensus.Value + ", fight traffic first " + FightNet.ConfigFightFirst.Value + "."); }
            // [wishbone] server: attached only on a headless process with one of its keys on at start; never on a client.
            if (ServerSweep.ShouldAttach()) { TryPatchAll(typeof(ServerSweep)); ServerSweep.NoteAttached(); }
            TryPatchAll(typeof(CompressionGroup));
            TryPatchAll(typeof(NetworkingRatesGroup));
            TryPatchSteam(typeof(NetworkingRatesGroup.SteamConnectPatch), "Steam send rates on connect");   // Steam-only (the Xbox build has no ZSteamSocket)
            TryPatchAll(typeof(DedicatedServerGroup));
            TryPatchSteam(typeof(DedicatedServerGroup.SteamPatches), "Steam lobby and server player limits");   // Steam-only (the Xbox build has no ZSteamSocket)
            TryPatchAll(typeof(PlayFabJoinCodeGuard));   // crossplay join-code check (null lobby entry), servers and hosts
            TryPatchAll(typeof(ServerSafePick));   // a server-owned Pickable no longer throws in RPC_Pick (no local player)
            TryPatchAll(typeof(SyncListRefPosPatches));
            TryPatchAll(typeof(UploadBreakdown));
            TryPatchAll(typeof(DownloadBreakdown));

            TryPatchAll(typeof(SendZDOsHeartbeatDiagnostic));

            if (ConfigEnableFallThroughDiagnostics.Value)
            {
                
            }

            TryPatchAll(typeof(ServerDisconnectDiagnostics));
            TryPatchSteam(typeof(CloseWithoutSleep), "closing Steam connections without the 100 ms wait");   // Steam-only (the Xbox build has no ZSteamSocket)
            PlayFabZlibWorker.Apply();

            TryPatchAll(typeof(BulkTransferGatePatches));

            TryPatchAll(typeof(BigZdoDiagnostic));

            // OwnerSync's prefix attaches only while one of its features is on (ZSyncHookGate)
            ZSyncHookGate.Init(TransformWriteRate.ConfigEnabled, TransformWriteRate.ConfigPrefabs, TransformWriteRate.ConfigWritesPerSecond, RemoteMotion.ConfigSendStamps);

            ZdoWireWriter.Initialize();
            TryPatchAll(typeof(ZdoWireWriter));

            if (ConfigEnableZDODelta.Value)
            {
                TryPatchAll(typeof(ZDODeltaPatches));
                LoggerOptions.LogInfo("ZDO delta compression enabled.");
            }

            TryPatchAll(typeof(DisarmOverloadTest));
            TryPatchSteam(typeof(DisarmOverloadTest.SteamPatch), "the overload test's Steam queue check");   // Steam-only (the Xbox build has no ZSteamSocket)

            TryPatchAll(typeof(CompressionRoundTripTest));

            TryPatchAll(typeof(SocketStressTests));

            TryPatchAll(typeof(SendQueueHeadroomMonitor));

            TryPatchAll(typeof(LinkController));

            TryPatchAll(typeof(NetSim));

            TryPatchAll(typeof(LogoutHold));
            TryPatchAll(typeof(RemoteArrows));
            TryPatchAll(typeof(MapPinJump));   // 1.6.31: Map Icons Jump With Teleports

            TryPatchAll(typeof(SendScheduler));

            TryPatchAll(typeof(ConnectionEcho));

            TryPatchAll(typeof(CreatureOwnership));

            TryPatchAll(typeof(HelmOwnership));
            TryPatchAll(typeof(PortalAhead));   // 1.6.72 ([ghost])
            if (ArrivalGate.KeyOnAtStart) TryPatchAll(typeof(ArrivalGate));   // [wishbone] arrival: attached only when a key is on at start
            if (OwnershipSteady.AnyKeyOn) TryPatchAll(typeof(OwnershipSteady));   // [wishbone] ownstab: census hooks attached only when a key is on at start
            if (SendLater.KeyOnAtStart) TryPatchAll(typeof(SendLater));   // [wishbone] sendeff: attached only when on at start
            TryPatchAll(typeof(SelfCost.InstallOnWorldLoad));   // 1.6.73

            TryPatchAll(typeof(TargetSync));
            if (FightHandoff.AnyKeyOn())   // [wishbone] handoff: attached only when one of its keys is on at start
            {
                TryPatchAll(typeof(FightHandoff));
                FightHandoff.MarkAttached();
            }

            TryPatchSteam(typeof(KeepaliveFirst), "keepalives jumping the Steam send queue");   // Steam-only (the Xbox build has no ZSteamSocket)

            TryPatchAll(typeof(JoinGrace));

            try
            {
                TryPatchAll(typeof(RoundTripTrace));
            }
            catch (System.Exception ex)
            {
                LoggerOptions.LogWarning($"[RoundTrip] could not be attached; round trips are still timed, without the per-stage breakdown. {ex.Message}");
            }

            TryPatchAll(typeof(ZdoFloodTest));

            WackyDatabaseCompatibilityPatch.Init(Harmony);

            TryPatchAll(typeof(PlayerPositionSyncPatches));

            
            

            if (isDedicated) TryPatchAll(typeof(GroundSnapPatches));   // 1.6.81: clients and hosts are FiresFixes
            TryPatchAll(typeof(CartSnapTest));   // 1.5.90: its InitTerminal postfix registers fgn_cartsnap_test (1.5.86-1.5.89 never patched it, so the command did not exist)
            TryPatchAll(typeof(ZoneLoadDiag)); TryPatchSteam(typeof(ZoneLoadDiag.SteamPatch), "Steam socket timing in [ZoneLoad]");
            TryPatchAll(typeof(SendZdoPackagePool));   // 1.6.38
            TryPatchAll(typeof(RpcArgumentList));   // 1.6.39
            TryPatchAll(typeof(RpcTypedInvoke));   // 1.6.39
            TryPatchAll(typeof(DisconnectSweep));   // 1.6.41
            TryPatchAll(typeof(DisconnectSweep.ReleaseAllHook));   // 1.6.51: the Release(Dictionary) prefix, target found in code (store-check safe)
            TryPatchAll(typeof(FeastRedraw));   // 1.6.10: Redraw Feasts When Their Stack Arrives
            TryPatchAll(typeof(PatchTimer));   // 1.6.13: Debug patch timer (markers on the vanilla method only)
            TryPatchAll(typeof(OwnershipClaimProbe));   // 1.6.15: Debug, which code makes this client own a creature
            TryPatchAll(typeof(Api.FgnNetView));   // 1.6.11: FgnApi v5 network view; only its F5 command is a patch
            TryPatchAll(typeof(RpcCensus));   // 1.5.92: [RpcCensus] routed RPCs handled, by handler, every 5 s (Debug only)
               // 1.5.93: per-frame breakdown for [ZoneLoad]'s worst frame (Debug only)

            TryPatchAll(typeof(ZSyncHookGate));   // the handoff prefix is attached by its session gate (off for a solo host), not here

            TryPatchAll(typeof(OwnedPositionEcho));
            TryPatchAll(typeof(OwnerSafeRevisions));
            TryPatchAll(typeof(PickupClaimGuard));   // a full player never claims a drop it cannot carry
            TryPatchAll(typeof(PendingServerKeys));
            OwnerSafeRevisions.MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

            try
            {
                TeleportGhostFix.Init();
                TryPatchAll(typeof(TeleportGhostFix));
            }
            catch (System.Exception ex)
            {
                LoggerOptions.LogWarning($"[TeleportGhostFix] could not be attached; vanilla behaviour is unchanged. {ex.Message}");
            }

            try
            {
            }
            catch (System.Exception ex)
            {
                LoggerOptions.LogWarning($"[TerrainComp] could not be attached; vanilla behaviour is unchanged. {ex.Message}");
            }

            // Always attached: it must run on clients too, not only with the server-only MonsterAIPatches.
            try
            {
            }
            catch (System.Exception ex)
            {
                LoggerOptions.LogWarning($"[Spawn] heightmap guard could not be attached; vanilla behaviour is unchanged. {ex.Message}");
            }


            if (!isDedicated)
                TryPatchAll(typeof(WorldClock));

            
            // Auto-tune: probe on clients, self-tune on servers.
            TryPatchAll(typeof(AutoTuneProbeHooks));
            TryPatchAll(typeof(ZoneLoadPatches));   TryPatchAll(typeof(EbmBridge));   // 1.6.54: the queue's AddToSector events
               // 1.6.70 N1: IsActiveAreaLoaded's teleport answer, create passes only, per-object zone gate
            ServerAutoTune.InitServerSide();

            if (isDedicated)
            {
                ApplyServerTrafficPatches();

                bool simulationActive = false;
                if (!ConfigEnableServerAuthority.Value)
                {
                    LoggerOptions.LogInfo("Server-side simulation disabled via ConfigEnableServerAuthority = false.");
                }
                else if (ValheimCommunityPatchCompat.SchedulesSceneObjects)
                {
                    LoggerOptions.LogWarning(
                        "Server-Side Simulation is OFF for this session: ValheimCommunityPatch replaces the server's object "
                        + "create/destroy pass with its own, built around world origin, and destroys every object FGN creates "
                        + "for players. Remove ValheimCommunityPatch from the server to use Server-Side Simulation, or turn "
                        + "'Enable Server-Side Simulation' off to silence this warning.");
                }
                else if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(ValheimPerformanceOptimizationsGuid))
                {
                    LoggerOptions.LogWarning(
                        "Server-Side Simulation (and ZDO ownership transfer) is OFF for this session: ValheimPerformanceOptimizations "
                        + "replaces the server's object create/destroy pass and its ownership pass with its own, built around the "
                        + "server's reference position, so it would undo what FGN does for players. Remove "
                        + "ValheimPerformanceOptimizations from the server to use Server-Side Simulation, or turn 'Enable Server-Side "
                        + "Simulation' off to silence this warning.");
                }
                else
                {
                    ApplyServerSideSimulationPatches();
                    ServerAuthorityPatches.SimulationActive = true;
                    simulationActive = true;
                    ZSyncHookGate.Refresh("Server-Side Simulation started");   // the kill-plane rescue needs OwnerSync
                }

                EmitServerFeatureSummary(simulationActive);
            }
            else
            {
                LoggerOptions.LogInfo("Server-side features skipped — not running on a dedicated server.");
            }

            foreach (Type hooks in FgnHooks.ReadAttachedFeatures())
                TryPatchAll(hooks);

            StartCoroutine(EmitCompactBannerWhenZNetReady());

            TmlGuard.Capture();   // our own settings, before any later plugin's Awake
            StartCoroutine(TmlGuard.Run(this));
        }

        /// <summary>Dedicated-server traffic shaping. None of it needs Server-Side Simulation; each feature follows its own toggle.</summary>
        private static void ApplyServerTrafficPatches()
        {
            TryPatchAll(typeof(ServerFrameProfile));
            TryPatchAll(typeof(SectorChangeTracker));
            TryPatchAll(typeof(ZDOThrottlingPatches));
            TryPatchAll(typeof(AILODPatches));

            // Attached whatever the toggles say, so 'Fix Lost Station Inserts' can be turned on live; the routed-RPC prefix
            // hands every message to vanilla while it and 'Enable RPC Router' are both off.
            TryPatchAll(typeof(RpcRouterPatches));
            TryPatchAll(typeof(StationRouter));
            DamageTextHandler.Register();

            if (ConfigEnableRpcRouter.Value || StationRouter.ConfigEnabled.Value)
            {
                VAGhettoLoadSummary.EmitRpcRouter(
                    handlersRegistered: RoutedRpcManager.HandlerCount,
                    aoiRadius: RoutedRpcManager.PositionRadius(),
                    aoiEnabled: RoutedRpcManager.FilteringEnabled());
                if (VAGhettoLoadSummary.VerboseEnabled)
                    LoggerOptions.LogInfo("RPC Router enabled — handlers: " + string.Join(", ", RoutedRpcManager.HandlerMethodNames) + ".");
            }

            if (ConfigEnableWNTServerOptimization.Value)
            {
                TryPatchAll(typeof(WearNTearServerPatches));
                LoggerOptions.LogInfo("WearNTear server optimization enabled.");
            }
        }

        /// <summary>The server instantiating, owning and driving the world around every peer.</summary>
        private static void ApplyServerSideSimulationPatches()
        {
            if (ConfigEnableShipFixes.Value)
                TryPatchAll(typeof(ShipFixesGroup));

            TryPatchAll(typeof(ServerShipSimulationPatches));

            TryPatchAll(typeof(ServerAuthorityPatches));
            TryPatchAll(typeof(SssQuietPass));   // quiet-pass counter for the [SSS] line
            TryPatchAll(typeof(ServerStream));   // 1.6.57: the server create queue's AddToSector events
            TryPatchAll(typeof(MonsterAIPatches));
            TryPatchAll(typeof(FarIdleHandoff));   // 1.6.82: far idle moved to FiresFixes
            
            

            if (ConfigEnableServerOwnershipSelective.Value)
            {
                if (ConfigEnableServerOwnership.Value)
                {
                    LoggerOptions.LogWarning(
                        "Both 'Server ZDO Ownership Transfer' flags are ENABLED — selective (V3) takes precedence; broad (V2) is being ignored. Disable one to silence this warning.");
                }
                TryPatchAll(typeof(ServerOwnershipPatchesV3));
                CreatureOwnership.ServerOwnsCreatures = true;
                HelmOwnership.ServerOwnsShips = ConfigEnableServerSideShipSimulation.Value;
                LoggerOptions.LogDebug(
                    "Server ZDO ownership (V3 SELECTIVE) ENABLED — Character/Ship only; drops/voxel/interactables/carts stay peer-owned.");
            }
            else if (ConfigEnableServerOwnership.Value)
            {
                TryPatchAll(typeof(ServerOwnershipPatches));
                CreatureOwnership.ServerOwnsCreatures = true;
                HelmOwnership.ServerOwnsShips = ConfigEnableServerSideShipSimulation.Value;   // 1.6.31: was always true. With ship sim off the simulated set leaves ships to the peers, so asking for them back flipped an empty moored boat's owner every 7-10 s.
                LoggerOptions.LogDebug(
                    "Server ZDO ownership (V2 BROAD SSS-exact) ENABLED — every persistent ZDO in any peer's active area will be claimed by the server.");
                LoggerOptions.LogDebug(ConfigEnableServerSideShipSimulation.Value
                    ? "[ServerOwnership] Ships: 'Server-Side Ship Simulation' is on, so the server sails empty ships and takes them back at rest."
                    : "[ServerOwnership] Ships: 'Server-Side Ship Simulation' is off, so ships stay with the players and the server never asks for them back.");
            }
            else
            {
                LoggerOptions.LogDebug(
                    "Server ZDO ownership transfer disabled (both V2 and V3 flags = false). Vanilla peer ownership in effect.");
            }

            PatchSafety.Summary();   // 1.6.71 ([ghost]): one line: classes attached / methods patched / failed

            if (ConfigEnableBootPatchVerification.Value)
            {
                DumpPatchInfo(typeof(BaseAI),       "UpdateAI");
                DumpPatchInfo(typeof(BaseAI),       "Awake");
                DumpPatchInfo(typeof(MonsterAI),    "UpdateAI");
                DumpPatchInfo(typeof(Character),    "CustomFixedUpdate");
                DumpPatchInfo(typeof(MonoUpdaters), "FixedUpdate");
                DumpPatchInfo(typeof(ZNetScene),    "CreateDestroyObjects");
                DumpPatchInfo(typeof(ZNetScene),    "CreateObject");
                DumpPatchInfo(typeof(ZoneSystem),   "IsActiveAreaLoaded");
                DumpPatchInfo(typeof(SpawnSystem),  "UpdateSpawning");
                DumpPatchInfo(typeof(ShieldDomeImageEffect), "Awake");
                DumpPatchInfo(typeof(Heightmap),    "RebuildRenderMesh");
            }

            if (VAGhettoLoadSummary.VerboseEnabled)
                LoggerOptions.LogInfo("All server-side simulation patches enabled.");
        }

        private static void EmitServerFeatureSummary(bool simulationActive)
        {
            string ownershipMode =
                ConfigEnableServerOwnershipSelective.Value ? "V3 selective"
                : ConfigEnableServerOwnership.Value ? "V2 broad"
                : "vanilla peer";
            VAGhettoLoadSummary.EmitServerAuthority(
                simulation: simulationActive,
                ownership: ownershipMode,
                zdoDelta: ConfigEnableZDODelta?.Value ?? false,
                zdoThrottle: ConfigEnableZDOThrottling?.Value ?? false,
                aiLod: ConfigEnableAILOD?.Value ?? false,
                wntOpt: ConfigEnableWNTServerOptimization?.Value ?? false);
        }

        // Waits for ZNetScene + ObjectDB to be live (same readiness
        // signal FAP uses in VaPieces.WaitForZNetReady), then emits
        // the compact loaded banner. Failure is non-fatal — falls
        // back to the plain "loaded" log so the load event is still
        // recorded in the file log.
        private IEnumerator EmitCompactBannerWhenZNetReady()
        {
            while (ZNetScene.instance == null
                   || ZNetScene.instance.m_prefabs == null
                   || ZNetScene.instance.m_prefabs.Count == 0)
            {
                yield return null;
            }
            yield return new WaitForEndOfFrame();

            // A dedicated server also prints the address players type into the join dialog, under the banner. Steam
            // reports the public IP shortly after the server logs on, so wait a little for it.
            string joinAddress = null;
            bool dedicated = ServerClientUtils.IsDedicatedServerDetected;
            if (dedicated)
            {
                float giveUpAt = Time.realtimeSinceStartup + JoinAddressWaitSeconds;
                while (!TryGetJoinAddress(out joinAddress) && Time.realtimeSinceStartup < giveUpAt)
                    yield return new WaitForSecondsRealtime(JoinAddressPollSeconds);
            }

            try { VAGhettoBanner.Print(); }
            catch (Exception ex)
            {
                Logger.LogInfo($"{PluginName} v{PluginVersion} loaded. (banner failed: {ex.Message})");
            }

            if (dedicated)
            {
                Logger.LogInfo(joinAddress != null
                    ? $"Join address: {joinAddress}"
                    : $"Join address: this server's public IP, port {ServerPort()} (Steam did not report the public IP within {JoinAddressWaitSeconds:0} s).");
            }
        }

        private const float JoinAddressWaitSeconds = 60f;
        private const float JoinAddressPollSeconds = 0.5f;
        private const int DefaultServerPort = 2456;

        private static bool TryGetJoinAddress(out string address)
        {
            address = null;
            int port = ServerPort();
            if (port <= 0) return false;
            // The Xbox build has no ZSteamSocket: the Steam game-server read is JIT'd only when Steam is there.
            return SteamSide.Present && TryGetSteamJoinAddress(port, out address);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]   // never inlined into a non-Steam caller
        private static bool TryGetSteamJoinAddress(int port, out string address)
        {
            address = null;
            try
            {
                if (!Steamworks.SteamGameServer.BLoggedOn()) return false;
                Steamworks.SteamIPAddress_t publicIp = Steamworks.SteamGameServer.GetPublicIP();
                if (!publicIp.IsSet()) return false;
                System.Net.IPAddress ip = publicIp.ToIPAddress();
                address = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}";
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // FejdStartup reads -port (default 2456) and hands it to the Steam and PlayFab sockets. ZNet.GetHostPort is no
        // help here: on a Steam socket it only answers 1 for a host.
        private static int ServerPort()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-port", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int port) && port > 0)
                    return port;
            }
            return DefaultServerPort;
        }

        // Compatibility patch for WackyDatabase — safely skips SnapshotItem for broken/null items
        [HarmonyPatch]
        public static class WackyDatabaseCompatibilityPatch
        {
            public static void Init(Harmony harmony)
            {
                // Safely detect if WackyDatabase is present
                Type functionsType = Type.GetType("wackydatabase.Util.Functions, WackysDatabase");
                if (functionsType == null)
                {
                    LoggerOptions.LogInfo("WackyDatabase not detected — skipping compatibility patch.");
                    return;
                }

                MethodInfo snapshotMethod = functionsType.GetMethod("SnapshotItem", BindingFlags.Static | BindingFlags.Public);
                if (snapshotMethod == null)
                {
                    LoggerOptions.LogWarning("WackyDatabase detected but SnapshotItem method not found — patch skipped.");
                    return;
                }

                // Apply the prefix patch
                harmony.Patch(
                    original: snapshotMethod,
                    prefix: new HarmonyMethod(typeof(WackyDatabaseCompatibilityPatch), nameof(SnapshotItem_Prefix))
                );

                LoggerOptions.LogInfo("WackyDatabase compatibility patch applied — will skip snapshots for invalid/broken clones.");
            }

            private static int s_nullSkips;   // 1.6.49

            // Prefix for SnapshotItem(ItemDrop item, ...)
            [HarmonyPrefix]
            public static bool SnapshotItem_Prefix(ref ItemDrop item) // Use ref to allow null check + early exit
            {
                // First and most important: null item
                if (item == null)
                {
                    // 1.6.49 ([lead] 11-07): handled, so not a player warning; there is no name to give (the ItemDrop itself is null).
                    // One Info line the first time, then a Debug line per skip with the running count.
                    s_nullSkips++;
                    if (s_nullSkips == 1) LoggerOptions.LogInfo("WDB: skipping snapshots WackysDatabase asks for with a null ItemDrop (likely a broken clone; handled, nothing lost). Each one is counted at Debug.");
                    LoggerOptions.LogDebug($"WDB: skipped snapshot for null ItemDrop #{s_nullSkips}");
                    return false; // Skip original
                }

                // Second: item has no valid gameObject (common when cloneFrom prefab is missing)
                if (item.gameObject == null)
                {
                    LoggerOptions.LogWarning($"WDB: Skipping snapshot for {item.name} — gameObject is null (missing prefab from removed mod).");
                    return false;
                }

                // Third: no renderable components (prevents NRE in bounds calculation and rendering)
                bool hasRenderer = item.GetComponentsInChildren<Renderer>(true).Length > 0;
                bool hasMesh = item.GetComponentsInChildren<MeshFilter>(true).Length > 0;

                if (!hasRenderer && !hasMesh)
                {
                    LoggerOptions.LogWarning($"WDB: Skipping snapshot for {item.name} — no renderers or meshes (broken model).");
                    return false;
                }

                // All good — allow original method to run
                return true;
            }
        }

        /// <summary>
        /// One patch class. Harmony compiles each patched method as it attaches, so a patch the game's IL can't take throws here;
        /// it is logged and that feature stays off, instead of the exception leaving Awake and skipping everything after it
        /// (which would leave every peer running a half-initialised FGN).
        /// </summary>
        // Steam-only patch classes: one Info line with the reason when skipped (Fire 11-05). Error stays for real failures, in TryPatchAll.
        private static void TryPatchSteam(Type type, string whatIsOff)
        {
            if (SteamSide.Present) TryPatchAll(type);
            else Log?.LogInfo($"[{PluginName}] skipped {(type.DeclaringType != null ? type.DeclaringType.Name + "." : "")}{type.Name}: {SteamSide.Why}, {whatIsOff} off.");
        }

        private static void TryPatchAll(Type type)
        {
            if (type == null)
            {
                Log?.LogError("Tried to patch a null type!");
                return;
            }
            try
            {
                PatchSafety.NoteAttached(Harmony.CreateClassProcessor(type, true).Patch());   // 1.6.71 ([ghost]): exactly what Harmony.PatchAll(type) runs (0Harmony 2.9 IL), now with its count
                FgnHooks.NoteAttached(type);
            }
            catch (Exception ex)
            {
                Exception cause = ex.InnerException ?? ex;
                int undone = PatchSafety.RollBack(Harmony, type);   // 1.6.71 ([ghost]): parts it had already attached are removed again
                while (cause.InnerException != null) cause = cause.InnerException;
                Log?.LogError($"[{PluginName}] couldn't attach {type.Name} ({cause.GetType().Name}: {cause.Message}); that feature is off, "
                    + "the rest of FGN loads normally." + (undone > 0 ? $" {undone} part(s) it had already attached were removed again, so it never runs half-way." : ""));
            }
        }

        // ============================================================
        // POST-PATCHALL VERIFICATION
        //
        // Logs every Harmony patch attached to (type, methodName) right
        // now — owner / patch-method full name / priority. Lets us prove
        // attachment from boot logs without waiting for the method to
        // actually fire at runtime.
        // ============================================================
        private static void DumpPatchInfo(Type type, string methodName)
        {
            var mi = AccessTools.Method(type, methodName);
            if (mi == null)
            {
                LoggerOptions.LogDebug($"[PatchVerify] {type.FullName}.{methodName} â†’ method NOT FOUND by AccessTools.");
                return;
            }
            var info = HarmonyLib.Harmony.GetPatchInfo(mi);
            if (info == null)
            {
                LoggerOptions.LogDebug($"[PatchVerify] {type.FullName}.{methodName} â†’ NO patches attached (info=null).");
                return;
            }
            int total = (info.Prefixes?.Count ?? 0)
                      + (info.Postfixes?.Count ?? 0)
                      + (info.Transpilers?.Count ?? 0)
                      + (info.Finalizers?.Count ?? 0);
            LoggerOptions.LogDebug($"[PatchVerify] {type.FullName}.{methodName} â†’ total={total} (prefixes={info.Prefixes?.Count ?? 0}, postfixes={info.Postfixes?.Count ?? 0}, transpilers={info.Transpilers?.Count ?? 0}, finalizers={info.Finalizers?.Count ?? 0}).");
            void DumpList(string kind, System.Collections.Generic.IEnumerable<HarmonyLib.Patch> ps)
            {
                if (ps == null) return;
                foreach (var p in ps)
                    LoggerOptions.LogDebug($"[PatchVerify]   [{kind}] owner='{p.owner}' method={p.PatchMethod.DeclaringType?.FullName}.{p.PatchMethod.Name} prio={p.priority}");
            }
            DumpList("Prefix",     info.Prefixes);
            DumpList("Postfix",    info.Postfixes);
            DumpList("Transpiler", info.Transpilers);
            DumpList("Finalizer",  info.Finalizers);
        }

        private void InvokeStaticInitByTypeName(string typeName, string methodName, object[] args)
        {
            try
            {
                var type = Type.GetType(typeName);
                if (type == null)
                {
                    Logger.LogWarning($"Type {typeName} not found; skipping {methodName}.");
                    return;
                }
                var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null)
                {
                    Logger.LogWarning($"Method {methodName} not found on {typeName}.");
                    return;
                }
                method.Invoke(null, args);
            }
            catch (TypeLoadException tle)
            {
                Logger.LogError($"TypeLoadException while invoking {typeName}.{methodName}: {tle}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Exception while invoking {typeName}.{methodName}: {ex}");
            }
        }

        /// <summary>Carries a pre-KB/s "_2048KB" dropdown value across the type change, so BepInEx does
        /// not reject it and reset the player to the default. A no-op once the file holds a number.</summary>
        private int MigrateLegacyKb(string section, string key, int defaultKb)
        {
            int legacyKb = -1;
            try
            {
                string path = Config.ConfigFilePath;
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    string current = null;
                    foreach (string raw in System.IO.File.ReadAllLines(path))
                    {
                        string line = raw.Trim();
                        if (line.StartsWith("[") && line.EndsWith("]")) { current = line.Substring(1, line.Length - 2).Trim(); continue; }
                        if (current != section || line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0 || line.Substring(0, eq).Trim() != key) continue;
                        var m = System.Text.RegularExpressions.Regex.Match(line.Substring(eq + 1).Trim(), @"^_(\d+)KB$");
                        if (m.Success) legacyKb = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    }
                }
            }
            catch (Exception ex) { Logger.LogWarning($"[Config] Could not read {key} for migration: {ex.Message}"); }

            if (legacyKb <= 0) return defaultKb;

            try
            {
                var prop = typeof(ConfigFile).GetProperty("OrphanedEntries",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (prop?.GetValue(Config) is System.Collections.Generic.Dictionary<ConfigDefinition, string> orphans)
                {
                    var def = new ConfigDefinition(section, key);
                    if (orphans.ContainsKey(def))
                        orphans[def] = legacyKb.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch { /* value still carried as the default below */ }

            Logger.LogInfo($"[Config] {key}: migrated from the old option _{legacyKb}KB to {legacyKb} KB/s.");
            return Mathf.Clamp(legacyKb, SendRateKbLow, SendRateKbHigh);
        }

        /// <summary>
        /// Removes keys FGN no longer reads from the cfg, so the file
        /// shows only live settings: the old Selective key whose dash an earlier save garbled, two retired [12 - Advanced] keys, and
        /// the diagnostics' old "10 - Diagnostics" section (it clashed with "10 - Server Authority"; now "14 - Diagnostics").
        /// Only these named leftovers go; anything else another tool wrote is kept.
        /// </summary>
        private void DropStaleConfigKeys()
        {
            try
            {
                var prop = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (!(prop?.GetValue(Config) is System.Collections.Generic.Dictionary<ConfigDefinition, string> orphans)) return;
                var drop = new System.Collections.Generic.List<ConfigDefinition>();
                foreach (ConfigDefinition d in orphans.Keys)
                {
                    bool garbled = d.Section == "10 - Server Authority" && d.Key.IndexOf('â') >= 0;   // "â€”" = an em dash saved twice
                    bool retired = d.Section == "12 - Advanced" && (d.Key == "Fix Boat Damage From Server Time Sync" || d.Key == "Max Active ZDOs");
                    bool moved = d.Section == "10 - Diagnostics";
                    if (garbled || retired || moved) drop.Add(d);
                }
                foreach (ConfigDefinition d in drop)
                {
                    // a moved key: the player's old value goes into the new key once, then the old line is removed
                    var target = new ConfigDefinition("14 - Diagnostics", d.Key);
                    if (d.Section == "10 - Diagnostics" && Config.ContainsKey(target) && Config[target] is ConfigEntryBase moved)
                    {
                        string old = orphans[d];
                        if (old != moved.GetSerializedValue())
                        {
                            moved.SetSerializedValue(old);
                            Logger.LogInfo($"[Config] '{d.Key}' moved from [10 - Diagnostics] to [14 - Diagnostics]; your value '{old}' was kept.");
                        }
                    }
                    orphans.Remove(d);
                }
                if (drop.Count > 0) Logger.LogInfo($"[Config] removed {drop.Count} setting(s) FGN no longer uses from the cfg file.");
            }
            catch (Exception ex) { Logger.LogWarning($"[Config] could not tidy old settings: {ex.Message}"); }
        }

        private void BindConfigs()
        {
            // Config UI rules: only the basics a player or server admin needs show without "Show Advanced" (Cfg.Basic);
            // everything else is Cfg.Adv. Descriptions are one short line. Section and key names are unchanged so saved values stay.
            ConfigLogLevel = Config.Bind(
                "01 - General",
                "Log Level",
                LogLevel.Message,
                Cfg.Basic("How much FGN writes to the log. Message is normal; Info and Debug are for troubleshooting."));
            ConfigServerTestCommands = Config.Bind(
                "01 - General",
                "Server test commands",
                false,
                Cfg.Adv("Lets admins run FGN's stress and test commands on this server (object floods, send-queue overloads, socket floods). Off on live servers."));

            // ConfigManager sections render in first-Bind() order. The priming
            // binds + Init calls below set sections 02..08 to match the "NN - "
            // prefix. Bind each key once: a second Bind of the same key only
            // returns the same entry, and two copies drift apart when edited.
            ConfigZoneLoadBatchSize = Config.Bind(
                "02 - Client Performance",
                "Zone Load Batch Size",
                1,
                Cfg.Adv("How many objects load per frame when an area loads in (1 = vanilla). Higher = faster, bigger hitches.",
                    new AcceptableValueRange<int>(1, 8)));

            PlayerPositionSyncPatches.Init(Config);

            ConfigEnableCompression = Config.Bind(
                "04 - Networking",
                "Enable Compression",
                true,
                Cfg.Basic("Compresses network traffic. Works when both server and player run FGN."));

            ConfigUpdateRate = Config.Bind(
                "04 - Networking",
                "ZDO Send Rate",
                UpdateRateOptions._100,
                Cfg.Adv("How often world updates are sent (100% = vanilla). Auto-Tune overwrites this while on."));

            ConfigSendRateMin = Config.Bind(
                "05 - Networking - Steamworks",
                "Send Rate Min",
                MigrateLegacyKb("05 - Networking - Steamworks", "Send Rate Min", DefaultSendRateMinKb),
                Cfg.Adv("Lowest Steam send rate in KB/s. Auto-Tune overwrites this while on.",
                new AcceptableValueRange<int>(SendRateKbLow, SendRateKbHigh)));

            ConfigSendRateMax = Config.Bind(
                "05 - Networking - Steamworks",
                "Send Rate Max",
                MigrateLegacyKb("05 - Networking - Steamworks", "Send Rate Max", DefaultSendRateMaxKb),
                Cfg.Adv("Highest Steam send rate in KB/s (your upload ceiling). Auto-Tune overwrites this while on.",
                new AcceptableValueRange<int>(SendRateKbLow, SendRateKbHigh)));

            ConfigAdaptiveUpload = Config.Bind(
                "04 - Networking",
                "Adaptive Upload",
                true,
                Cfg.Adv("Backs your send rate off when your connection struggles instead of flooding it. Steam only."));

            ConfigHyperBoost = Config.Bind(
                "05 - Networking - Steamworks",
                "HYPERBOOST",
                false,
                Cfg.Adv("Max-throughput mode for very fast links and benchmarks. Leave off for normal play."));

            AutoTuneConfig.Init(Config);

            ConfigQueueSize = Config.Bind(
                "04 - Networking",
                "Queue Size",
                QueueSizeOptions._32KB,
                Cfg.Adv("Biggest batch of world updates sent to one player at once. Auto-Tune overwrites this while on."));

            ConfigForceCrossplay = Config.Bind(
                "09 - Dedicated Server",
                "Force Crossplay",
                ForceCrossplayOptions.vanilla,
                Cfg.Basic("Server backend: vanilla follows -crossplay, steamworks = Steam only, playfab = crossplay. Restart."));

            ConfigPlayerLimit = Config.Bind(
                "09 - Dedicated Server",
                "Player Limit",
                10,
                Cfg.Basic("Max players on a dedicated server. Restart to apply.", new AcceptableValueRange<int>(1, 999)));

            ConfigAdvertisedPlayerLimit = Config.Bind(
                "09 - Dedicated Server",
                "Advertised Player Limit",
                0,
                Cfg.Basic("Player cap shown in server lists (0 = same as Player Limit). Restart to apply.",
                    new AcceptableValueRange<int>(0, 9999)));

            // ---- Client-side perf knobs ----
            // Zone Load Batch Size is bound once, at the top of this method, where it primes section 02's position.

            ConfigZPackageReceiveBufferSize = Config.Bind(
                "02 - Client Performance",
                "ZPackage Receive Buffer Bytes",
                256 * 1024,
                Cfg.Adv("Steam receive buffer in bytes when Auto-Tune is off (raised to at least 2 MB).",
                    new AcceptableValueRange<int>(64 * 1024, 4 * 1024 * 1024)));

            // ---- Time-sliced instantiation ----
            ConfigEnableTimeSliceInstantiation = Config.Bind(
                "02 - Client Performance",
                "Enable Time-Slice Instantiation",
                true,   // 1.6.4 (Fire 10-28): ON by default
                Cfg.Adv("Spreads loading a busy area over several frames instead of one big hitch."));
            // 1.6.4: one-time migration of cfgs left on the old off default (marker; the player's later choice is kept)
            var timeSliceMigrated = Config.Bind("12 - Advanced", "Time-Slice Default Migrated", false, Cfg.Hidden("Internal: the 1.6.4 default change was applied once."));
            if (!timeSliceMigrated.Value)
            {
                if (!ConfigEnableTimeSliceInstantiation.Value) { ConfigEnableTimeSliceInstantiation.Value = true; LoggerOptions.LogDebug("[FGN] 'Enable Time-Slice Instantiation' is now on by default; you can still turn it off."); }
                timeSliceMigrated.Value = true;
            }

            // 1.6.54 ([fgn], Fire 11-08 "close it fix it and relaunch"): a fix, ON. Replaces 1.6.53's "Time-Slice Cached Create Order".
            ConfigAdaptiveFill = Config.Bind("02 - Client Performance", "Adaptive Fill Budget", true,
                Cfg.Adv("Objects around you are created as fast as this PC can manage: the budget comes from your frame time target, the frame time actually left over and the measured cost of each create (more while you stand still or load, only the headroom while you move). Off = the fixed pacing as before."));
            ConfigEventCreateQueue = Config.Bind(
                "02 - Client Performance",
                "Event-Driven Create Queue",
                true,
                Cfg.Adv("Towns load fast: nearby objects are created from one queue kept up to date as objects arrive, instead of re-sorting every nearby object each frame. Behind the loading screen it creates in a big burst; in play it stays inside the frame budget and bursts while a lot is waiting. Also tells other mods when a zone has finished loading. Off = the older time slice."));
            ConfigEventQueueSelfCheck = Config.Bind(
                "02 - Client Performance",
                "Event-Driven Create Queue Self-Check",
                false,
                Cfg.Adv("Test tool: every 10 s, checks the create queue against the game's own list of nearby objects and logs any it missed (Log Level Debug)."));
            ConfigInstantiationBudgetMs = Config.Bind(
                "02 - Client Performance",
                "Instantiation Budget Ms",
                3,
                Cfg.Adv("Milliseconds per frame spent loading objects with time-slicing. Lower = smoother, higher = faster.",
                    new AcceptableValueRange<int>(1, 16)));

            ConfigMaxInstancesPerFrame = Config.Bind(
                "02 - Client Performance",
                "Max Instances Per Frame",
                100,
                Cfg.Adv("Most objects loaded in one frame with time-slicing.",
                    new AcceptableValueRange<int>(10, 500)));

            ConfigSafetyFallbackEnabled = Config.Bind(
                "02 - Client Performance",
                "Safety Fallback Enabled",
                true,
                Cfg.Adv("Kept for old configs. Since 1.6.45 arriving (loading screen or teleport) uses vanilla's fast creation, so this no longer widens the budget while you play."));

            ConfigSafetyFallbackThreshold = Config.Bind(
                "02 - Client Performance",
                "Safety Fallback Threshold",
                5000,
                Cfg.Adv("Backlog size at which the safety fallback kicks in.",
                    new AcceptableValueRange<int>(100, 50000)));

            ConfigEnableRpcRouter = Config.Bind(
                "10 - Server Authority",
                "Enable RPC Router",
                true,
                Cfg.Adv("Server relays hits and effects instead of everyone sending to everyone. Restart."));

            ConfigEnableShipFixes = Config.Bind(
                "11 - Ship Fixes",
                "Enable Universal Ship Fixes",
                true,
                Cfg.Adv("Keeps a server-run ship's speed and rudder as the helmsman set them. Restart."));

            ConfigEnableServerSideShipSimulation = Config.Bind(
                "11 - Ship Fixes",
                "Server-Side Ship Simulation",
                false,
                Cfg.Adv("Lets the server run ship physics too (needs server ownership on). Restart."));

            ConfigEnableRpcAoI = Config.Bind(
                "10 - Server Authority",
                "Enable RPC Area-of-Interest",
                true,
                Cfg.Adv("Only sends object messages and damage numbers to players close enough to see them."));

            ConfigRpcAoIRadius = Config.Bind(
                "10 - Server Authority",
                "RPC AoI Radius",
                256f,
                Cfg.Adv("Meters around a player for damage numbers and similar messages.",
                    new AcceptableValueRange<float>(64f, 1024f)));

            ConfigClientMaxDestroysPerFrame = Config.Bind(
                "02 - Client Performance",
                "Max Destroys Per Frame",
                200,
                Cfg.Adv("Most objects removed per frame when leaving an area, avoids freezes (0 = vanilla).",
                    new AcceptableValueRange<int>(0, 5000)));

            ConfigDediFellOutRescueLayers = Config.Bind(
                "10 - Server Authority",
                "Dedi Fell-Out Rescue Layers",
                "Default,static_solid,Default_small,piece,terrain,vehicle",
                Cfg.Adv("Layers that count as solid ground when rescuing things that fell out of the world."));

            ConfigFixGroundSnapThroughFloors = Config.Bind(
                "12 - Advanced",
                "Fix Ground Snap Through Floors",
                true,
                Cfg.Adv("Stops tombstones and dropped items popping up through floors built below ground. Players and creatures that really are under the terrain are still lifted."));
            ConfigCartPushUpWheelDepth = Config.Bind(
                "12 - Advanced",
                "Cart push-up wheel depth (m)",
                0.5f,
                Cfg.Adv("A cart is only pushed back up out of the ground when one of its WHEELS is at least this far below the terrain under it (handles and the body never count; carts on or against build pieces, or hung tilted on walls, are never moved).", new AcceptableValueRange<float>(0.1f, 5f)));

            ConfigShowAILODInServerStatus = Config.Bind(
                "01 - General",
                "Show AILOD in ServerStatus",
                true,
                Cfg.Adv("Adds AI LOD stats to the server status log line."));

            ConfigDiagnosticIntervalSec = Config.Bind(
                "01 - General",
                "Diagnostic Rollup Interval (sec)",
                60f,
                Cfg.Adv("Seconds between server status log lines.",
                    new AcceptableValueRange<float>(10f, 3600f)));

            ConfigEnableBulkTransferBoost = Config.Bind(
                "04 - Networking",
                "Enable Bulk Transfer Queue Boost",
                true,
                Cfg.Adv("Speeds up ServerSync config syncing on join and stops it kicking slow players. Restart."));

            ConfigBulkTransferBudgetPercent = Config.Bind(
                "04 - Networking",
                "Bulk Transfer Budget Percent",
                40,
                Cfg.Adv("Share of the send buffer ServerSync mods may use with the boost on.",
                    new AcceptableValueRange<int>(10, 80)));

            ConfigEnableServerAuthority = Config.Bind(
                "10 - Server Authority",
                "Enable Server-Side Simulation",
                false,
                Cfg.Basic("Dedicated server runs the world around every player so it can run creatures and ships. Restart."));

            ConfigServerInstantiationBudgetMs = Config.Bind(
                "10 - Server Authority",
                "Server Instantiation Budget Ms",
                8,
                Cfg.Adv("Milliseconds per frame the server spends loading objects near players (0 = vanilla).",
                    new AcceptableValueRange<int>(0, 50)));

            ConfigServerPassHz = Config.Bind(
                "10 - Server Authority",
                "Server Object Pass Hz",
                10,
                Cfg.Adv("How often per second the server refreshes objects around players (0 = every frame).",
                    new AcceptableValueRange<int>(0, 30)));

            ConfigEnableServerOwnership = Config.Bind(
                "10 - Server Authority",
                "Enable Server ZDO Ownership Transfer (EXPERIMENTAL)",
                false,
                Cfg.Adv("Server takes over almost everything near players. Can break pickups; prefer Selective. Restart."));

            ConfigEnableServerOwnershipSelective = Config.Bind(
                "10 - Server Authority",
                "Enable Server ZDO Ownership Transfer — Selective (EXPERIMENTAL)",
                false,
                Cfg.Adv("Server runs wild creatures near players; everything else stays with players. Restart."));

            // 1.6.56 ([fgn], lead 11-08 relaying Fire): a fix, ON
            ConfigServerPiecesOnlyWhereSimulated = Config.Bind(
                "10 - Server Authority",
                "Server Builds Pieces Only Where It Simulates",
                true,
                Cfg.Adv("With Server-Side Simulation on, the server builds walls, floors and other building pieces only around the creatures and ships it runs, instead of every town near a player. Big towns stop slowing the server down and it uses far less memory. Players see no difference: their own game builds everything as before. Off = the server builds every piece near a player."));

            // 1.6.57 ([fgn], lead 11-08): a fix, ON
            ConfigServerEventQueue = Config.Bind(
                "10 - Server Authority",
                "Server Event-Driven Create Queue",
                true,
                Cfg.Adv("With Server-Side Simulation on, the server keeps one queue of what it still has to build near players, updated as objects arrive, instead of checking every nearby object several times a second. Busy towns cost the server far less. Off = the full check every pass."));

            ConfigExtendedZoneRadius = Config.Bind(
                "10 - Server Authority",
                "Extended Zone Radius",
                0,
                Cfg.Adv("Extra zone rings the server loads around each player (0 = vanilla). Costs server CPU and RAM.",
                    new AcceptableValueRange<int>(0, 3)));

            // ---- Predictive zone pre-streaming ----
            ConfigEnablePredictiveZoneStreaming = Config.Bind(
                "10 - Server Authority",
                "Enable Predictive Zone Streaming",
                true,
                Cfg.Adv("Server loads the area ahead of a moving player so it is ready when they arrive."));

            ConfigPredictionLookaheadSec = Config.Bind(
                "10 - Server Authority",
                "Prediction Lookahead Sec",
                3.0f,
                Cfg.Adv("Seconds of travel ahead the prediction looks.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            ConfigPredictionMinVelocity = Config.Bind(
                "10 - Server Authority",
                "Prediction Min Velocity",
                2.0f,
                Cfg.Adv("Speed in m/s below which prediction is not used.",
                    new AcceptableValueRange<float>(0.5f, 20f)));

            ConfigPredictionMaxLookaheadZones = Config.Bind(
                "10 - Server Authority",
                "Prediction Max Lookahead Zones",
                9,
                Cfg.Adv("Furthest ahead the prediction may reach, in zones.",
                    new AcceptableValueRange<int>(1, 25)));

            // ZDO Throttling (server-only bandwidth optimization)
            ConfigEnableZDOThrottling = Config.Bind(
                "10 - Server Authority",
                "Enable ZDO Throttling",
                true,
                Cfg.Adv("On a backed-up connection, far loose objects are sent after nearer ones."));

            ConfigZDOThrottleDistance = Config.Bind(
                "10 - Server Authority",
                "ZDO Throttle Distance",
                500f,
                Cfg.Adv("Meters beyond which loose objects are sent last (0 = off).",
                    new AcceptableValueRange<float>(0f, 1000f)));

            // AI LOD Throttling (server-only CPU optimization)
            ConfigEnableAILOD = Config.Bind(
                "10 - Server Authority",
                "Enable AI LOD Throttling",
                true,
                Cfg.Adv("Updates creatures far from every player less often to save server CPU."));

            ConfigAILODNearDistance = Config.Bind(
                "10 - Server Authority",
                "AI LOD Near Distance",
                100f,
                Cfg.Adv("Stats only: distance counted as near in the status log.", new AcceptableValueRange<float>(50f, 200f)));

            ConfigAILODFarDistance = Config.Bind(
                "10 - Server Authority",
                "AI LOD Far Distance",
                300f,
                Cfg.Adv("Meters from the nearest player beyond which creatures update less often.", new AcceptableValueRange<float>(200f, 600f)));

            ConfigAILODThrottleFactor = Config.Bind(
                "10 - Server Authority",
                "AI LOD Throttle Factor",
                0.5f,
                Cfg.Adv("Share of updates far creatures still get (0.5 = half).", new AcceptableValueRange<float>(0.25f, 0.75f)));

            // Adaptive gate — the optimizations above only run when a peer's send queue
            // is actually backing up. Healthy server = vanilla behaviour (smoother).
            ConfigEnableAdaptiveThrottling = Config.Bind(
                "10 - Server Authority",
                "Adaptive Throttling",
                true,
                Cfg.Adv("Only throttles while a player's connection is backing up; otherwise plays like vanilla."));

            ConfigSendCongestionThresholdPct = Config.Bind(
                "10 - Server Authority",
                "Congestion Threshold",
                50,
                Cfg.Adv("How full (percent) a send window must be to count as backed up.",
                    new AcceptableValueRange<int>(10, 100)));

            ConfigEnableSendHeartbeatLog = Config.Bind(
                "12 - Advanced",
                "Log Send Queue Heartbeat",
                false,
                Cfg.Adv("Debug log: each player's send queue health every 10 s."));

            ConfigEnableBootPatchVerification = Config.Bind(
                "12 - Advanced",
                "Enable Boot Patch Verification",
                false,
                Cfg.Adv("Debug log: lists other mods' patches on methods FGN relies on at startup."));

            ConfigEnableLargeZdoDiagnostics = Config.Bind(
                "12 - Advanced",
                "Enable Large ZDO Diagnostics",
                false,
                Cfg.Adv("Debug log: names the object behind vanilla's 'Writing a lot of data' warning."));

            ConfigEnableZDODelta = Config.Bind(
                "12 - Advanced",
                "Enable ZDO Delta Compression",
                true,
                Cfg.Adv("Only resends values that changed, saving lots of bandwidth. Restart."));

            ConfigAllocationFreeZdoWrites = Config.Bind(
                "12 - Advanced",
                "Allocation-free ZDO Writes",
                true,
                Cfg.Adv("Writes network data without garbage, cutting stutter. Falls back to vanilla if anything differs."));

            ConfigEnableWNTServerOptimization = Config.Bind(
                "12 - Advanced",
                "Enable WearNTear Server Optimization",
                true,
                Cfg.Adv("Server skips wear updates on fully invulnerable build pieces. Restart."));

            ConfigEnableInvulnerableSupportSkip = Config.Bind(
                "12 - Advanced",
                "Enable Invulnerable Support Skip",
                true,
                Cfg.Adv("Skips support checks on fully invulnerable pieces; big CPU save in large bases."));

            ConfigEnableInstanceOrphanPrune = Config.Bind(
                "12 - Advanced",
                "Enable Instance Orphan Prune",
                true,
                Cfg.Adv("Cleans up broken object entries left by other mods instead of erroring."));

            ConfigFixTeleportGhosts = Config.Bind(
                "12 - Advanced",
                "Fix Teleport Ghost Players",
                true,
                Cfg.Adv("Fixes players staying visible, frozen, after they teleport away."));

            // 1.6.76 (Fire 11-08 SP split): "Fix Slow Sleep On Busy Servers" moved to FiresFixes 0.1.84 with its fix.

            ConfigKeepaliveFirst = Config.Bind(
                "12 - Advanced",
                "Keep Busy Connections Alive",
                true,
                Cfg.Adv("Stops big transfers, like a large base loading, from timing players out."));

            ConfigKeepWorldClockAtRealTime = Config.Bind(
                "12 - Advanced",
                "Keep World Clock At Real Time",
                true,
                Cfg.Adv("Stops the world clock falling behind and jumping back on a laggy server."));

            ConfigSmoothServerClockCorrections = Config.Bind(
                "12 - Advanced",
                "Smooth Server Clock Corrections",
                true,
                Cfg.Adv("Eases in clock corrections so waves, ships and timers do not jerk."));

            ConfigEnableCapeCrashDiagnostics = Config.Bind(
                "01 - General",
                "Enable Cape Crash Diagnostics",
                false,
                Cfg.Adv("Debug log for crashes during cape setup. Very noisy. Restart."));

            ConfigEnableFallThroughDiagnostics = Config.Bind(
                "01 - General",
                "Enable Fall-Through Diagnostics",
                false,
                Cfg.Adv("Debug log for items and tombstones sinking through floors. Costs performance. Restart."));

            // === CONFIG CHANGE LOGGING ===
            var allConfigs = new ConfigEntryBase[]
            {
        ConfigLogLevel,
        ConfigEnableCompression,
        ConfigUpdateRate,
        ConfigSendRateMin,
        ConfigSendRateMax,
        ConfigQueueSize,
        ConfigForceCrossplay,
        ConfigPlayerLimit,
        ConfigAdvertisedPlayerLimit,
        ConfigEnableShipFixes,
        ConfigEnableServerSideShipSimulation,
        ConfigEnableRpcRouter,
        ConfigEnableRpcAoI,
        ConfigRpcAoIRadius,
        ConfigEnableServerAuthority,
        ConfigExtendedZoneRadius,
        ConfigEnableZDOThrottling,
        ConfigZDOThrottleDistance,
        ConfigEnableAILOD,
        ConfigAILODNearDistance,
        ConfigAILODFarDistance,
        ConfigAILODThrottleFactor,
        ConfigEnableZDODelta,
        ConfigAllocationFreeZdoWrites,
        ConfigShowAILODInServerStatus,
        ConfigEnableBootPatchVerification,
        ConfigEnableLargeZdoDiagnostics,
        ConfigEnableWNTServerOptimization,
        ConfigZoneLoadBatchSize,
        ConfigZPackageReceiveBufferSize,
        ConfigEnableTimeSliceInstantiation,
        ConfigInstantiationBudgetMs,
        ConfigMaxInstancesPerFrame,
        ConfigServerInstantiationBudgetMs,
        ConfigServerPassHz,
        ConfigSafetyFallbackEnabled,
        ConfigSafetyFallbackThreshold,
        ConfigEnablePredictiveZoneStreaming,
        ConfigPredictionLookaheadSec,
        ConfigPredictionMinVelocity,
        ConfigPredictionMaxLookaheadZones,
        ConfigEnableInvulnerableSupportSkip,
        ConfigEnableInstanceOrphanPrune,
        ConfigFixTeleportGhosts,
        ConfigKeepaliveFirst,
        ConfigKeepWorldClockAtRealTime,
        ConfigSmoothServerClockCorrections,
        ConfigEnableFallThroughDiagnostics,
        ConfigEnableBulkTransferBoost,
        ConfigBulkTransferBudgetPercent,
        ConfigEnableServerOwnership,
        ConfigEnableServerOwnershipSelective
            };
            // Note: AutoTune.* configs are bound later (in Awake, after BindConfigs returns),
            // so they don't get change-logger hooks — their own log lines cover that.

            foreach (var baseCfg in allConfigs)
            {
                var cfgType = baseCfg.GetType();
                var settingChanged = cfgType.GetEvent("SettingChanged");
                if (settingChanged != null)
                {
                    var handler = new EventHandler((sender, __) =>
                    {
                        string side = (ZNet.instance != null && ZNet.instance.IsServer()) ? "SERVER" : "CLIENT";
                        var cfg = (ConfigEntryBase)sender;
                        LoggerOptions.LogDebug($"[{side}] Config changed: {cfg.Definition.Section} â†’ {cfg.Definition.Key} = {cfg.BoxedValue}");
                    });
                    settingChanged.AddEventHandler(baseCfg, handler);
                }
            }

            // Force default to false on clients
            if (ZNet.instance != null && !ZNet.instance.IsServer())
            {
                ConfigEnableServerAuthority.Value = false;
            }
        }
    }

    // ====================== ALL ENUMS DEFINED HERE ======================
    public enum LogLevel
    {
        [Description("Errors/Warnings only")]
        Warning,
        [Description("Errors/Warnings/Messages [default]")]
        Message,
        [Description("Everything including Info")]
        Info,
        // Added last (periodic diagnostics are debug logs) so the stored names and order of the others stay.
        [Description("Info plus the timed reports")]
        Debug
    }

    public enum UpdateRateOptions
    {
        [Description("150% - 30 network sends/sec [smoother, more bandwidth]")]
        _150,
        [Description("100% - 20 network sends/sec [default, vanilla]")]
        _100,
        [Description("75% - 15 network sends/sec")]
        _75,
        [Description("50% - 10 network sends/sec")]
        _50
    }

    public enum QueueSizeOptions
    {
        [Description("80 KB")]
        _80KB,
        [Description("64 KB")]
        _64KB,
        [Description("48 KB")]
        _48KB,
        [Description("32 KB [default]")]
        _32KB,
        [Description("Vanilla (~10 KB)")]
        _vanilla
    }

    public enum ForceCrossplayOptions
    {
        [Description("Vanilla behaviour - respect -crossplay flag [default]")]
        vanilla,
        [Description("Force crossplay ENABLED (use PlayFab backend)")]
        playfab,
        [Description("Force crossplay DISABLED (use Steamworks backend)")]
        steamworks
    }

    /// <summary>Tags that Configuration Manager and other hubs read by field name (reflection): Browsable, IsAdvanced, Order.</summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? Browsable;
        public bool? IsAdvanced;
        public int? Order;
    }

    /// <summary>Config descriptions: Basic = shown by default, Adv = only with "Show Advanced", Hidden = never shown (still saved).</summary>
    internal static class Cfg
    {
        internal static ConfigDescription Basic(string text, AcceptableValueBase range = null) => new ConfigDescription(text, range);
        internal static ConfigDescription Adv(string text, AcceptableValueBase range = null)
            => new ConfigDescription(text, range, new ConfigurationManagerAttributes { IsAdvanced = true });
        internal static ConfigDescription Hidden(string text, AcceptableValueBase range = null)
            => new ConfigDescription(text, range, new ConfigurationManagerAttributes { Browsable = false });
    }
}
